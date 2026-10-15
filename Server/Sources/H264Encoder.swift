import Foundation
import VideoToolbox
import CoreMedia
import RemoteAppProtocol

/// Hardware H.264 encoder tuned for low latency: real-time, no B-frames,
/// frequent keyframes, CBR-ish data-rate limits.
final class H264Encoder {
    var onConfig: ((VideoConfig) -> Void)?
    var onFrame: ((VideoFrame) -> Void)?

    let width: Int
    let height: Int
    private var session: VTCompressionSession?
    private var lastParameterSets: [Data] = []
    private var frameIndex: Int64 = 0

    init(width: Int, height: Int, frameRate: Int, bitrate: Int) throws {
        self.width = width
        self.height = height
        var session: VTCompressionSession?
        let spec: [CFString: Any] = [kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder: true]
        let status = VTCompressionSessionCreate(allocator: nil, width: Int32(width), height: Int32(height),
                                                codecType: kCMVideoCodecType_H264, encoderSpecification: spec as CFDictionary,
                                                imageBufferAttributes: nil, compressedDataAllocator: nil,
                                                outputCallback: nil, refcon: nil, compressionSessionOut: &session)
        guard status == noErr, let session else {
            throw NSError(domain: "H264Encoder", code: Int(status), userInfo: [NSLocalizedDescriptionKey: "VTCompressionSessionCreate failed (\(status))"])
        }
        self.session = session
        func set(_ key: CFString, _ value: Any) {
            VTSessionSetProperty(session, key: key, value: value as CFTypeRef)
        }
        set(kVTCompressionPropertyKey_RealTime, true)
        set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_High_AutoLevel)
        set(kVTCompressionPropertyKey_AllowFrameReordering, false)
        set(kVTCompressionPropertyKey_MaxKeyFrameInterval, frameRate * 2)
        set(kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, 2)
        set(kVTCompressionPropertyKey_ExpectedFrameRate, frameRate)
        set(kVTCompressionPropertyKey_AverageBitRate, bitrate)
        set(kVTCompressionPropertyKey_DataRateLimits, [bitrate / 8, 1] as [Int])
        set(kVTCompressionPropertyKey_AllowTemporalCompression, true)
        if #available(macOS 11.0, *) {
            set(kVTCompressionPropertyKey_PrioritizeEncodingSpeedOverQuality, true)
        }
        VTCompressionSessionPrepareToEncodeFrames(session)
    }

    deinit { invalidate() }

    func invalidate() {
        if let session {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(session)
        }
        session = nil
    }

    func encode(_ pixelBuffer: CVPixelBuffer, pts: CMTime, captureWallMicros: UInt64, forceKeyframe: Bool = false) {
        guard let session else { return }
        frameIndex += 1
        var properties: [CFString: Any] = [:]
        if forceKeyframe { properties[kVTEncodeFrameOptionKey_ForceKeyFrame] = true }
        VTCompressionSessionEncodeFrame(session, imageBuffer: pixelBuffer, presentationTimeStamp: pts, duration: .invalid,
                                        frameProperties: properties as CFDictionary, infoFlagsOut: nil) { [weak self] status, _, sampleBuffer in
            guard let self, status == noErr, let sampleBuffer, sampleBuffer.isValid else { return }
            self.handleEncoded(sampleBuffer, captureWallMicros: captureWallMicros)
        }
    }

    private func handleEncoded(_ sampleBuffer: CMSampleBuffer, captureWallMicros: UInt64) {
        let isKeyframe: Bool = {
            guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[CFString: Any]],
                  let first = attachments.first else { return true }
            return !(first[kCMSampleAttachmentKey_NotSync] as? Bool ?? false)
        }()

        if isKeyframe, let format = CMSampleBufferGetFormatDescription(sampleBuffer) {
            let sets = Self.parameterSets(from: format)
            if sets != lastParameterSets, !sets.isEmpty {
                lastParameterSets = sets
                let dims = CMVideoFormatDescriptionGetDimensions(format)
                onConfig?(VideoConfig(codec: .h264, parameterSets: sets, pixelWidth: Int(dims.width), pixelHeight: Int(dims.height)))
            }
        }

        guard let blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
        let length = CMBlockBufferGetDataLength(blockBuffer)
        var data = Data(count: length)
        let copyStatus = data.withUnsafeMutableBytes { ptr -> OSStatus in
            CMBlockBufferCopyDataBytes(blockBuffer, atOffset: 0, dataLength: length, destination: ptr.baseAddress!)
        }
        guard copyStatus == noErr else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let ptsMicros = UInt64(max(0, CMTimeGetSeconds(pts)) * 1_000_000)
        onFrame?(VideoFrame(ptsMicros: ptsMicros, captureWallMicros: captureWallMicros, isKeyframe: isKeyframe, data: data))
    }

    private static func parameterSets(from format: CMFormatDescription) -> [Data] {
        var count = 0
        guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: 0, parameterSetPointerOut: nil,
                                                                  parameterSetSizeOut: nil, parameterSetCountOut: &count,
                                                                  nalUnitHeaderLengthOut: nil) == noErr else { return [] }
        var sets: [Data] = []
        for i in 0..<count {
            var pointer: UnsafePointer<UInt8>?
            var size = 0
            guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: i, parameterSetPointerOut: &pointer,
                                                                      parameterSetSizeOut: &size, parameterSetCountOut: nil,
                                                                      nalUnitHeaderLengthOut: nil) == noErr, let pointer else { return [] }
            sets.append(Data(bytes: pointer, count: size))
        }
        return sets
    }
}
