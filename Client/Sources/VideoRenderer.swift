// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import AVFoundation
import CoreMedia
import Foundation
import RemoteAppProtocol

/// Builds compressed CMSampleBuffers from the wire format and hands them to an
/// AVSampleBufferDisplayLayer, which decodes with VideoToolbox and displays.
final class VideoRenderer {
    let layer = AVSampleBufferDisplayLayer()
    /// Decoded video size in pixels; used for touch → window coordinate mapping.
    private(set) var videoSize = CGSize.zero
    var onVideoSizeChange: ((CGSize) -> Void)?

    private var formatDescription: CMVideoFormatDescription?
    private var waitingForKeyframe = true
    private let queue = DispatchQueue(label: "remoteapp.renderer", qos: .userInteractive)

    init() {
        layer.videoGravity = .resizeAspect
        layer.backgroundColor = CGColor(gray: 0, alpha: 1)
    }

    func apply(_ config: VideoConfig) {
        queue.async { [self] in
            var pointers: [UnsafePointer<UInt8>] = []
            var sizes: [Int] = []
            // Keep the Data buffers alive while we take pointers into them.
            let sets = config.parameterSets.map { $0 as NSData }
            for set in sets {
                pointers.append(set.bytes.assumingMemoryBound(to: UInt8.self))
                sizes.append(set.length)
            }
            var format: CMVideoFormatDescription?
            let status: OSStatus
            switch config.codec {
            case .h264:
                status = CMVideoFormatDescriptionCreateFromH264ParameterSets(allocator: nil, parameterSetCount: pointers.count,
                                                                             parameterSetPointers: pointers, parameterSetSizes: sizes,
                                                                             nalUnitHeaderLength: Int32(config.nalLengthSize),
                                                                             formatDescriptionOut: &format)
            case .hevc:
                status = CMVideoFormatDescriptionCreateFromHEVCParameterSets(allocator: nil, parameterSetCount: pointers.count,
                                                                             parameterSetPointers: pointers, parameterSetSizes: sizes,
                                                                             nalUnitHeaderLength: Int32(config.nalLengthSize),
                                                                             extensions: nil, formatDescriptionOut: &format)
            }
            guard status == noErr, let format else {
                print("format description failed: \(status)")
                return
            }
            formatDescription = format
            waitingForKeyframe = true
            layer.sampleBufferRenderer.flush()
            let size = CGSize(width: config.pixelWidth, height: config.pixelHeight)
            if size != videoSize {
                videoSize = size
                DispatchQueue.main.async { self.onVideoSizeChange?(size) }
            }
        }
    }

    /// Enqueues a frame. Returns nothing; latency is reported via `onLatency`.
    func enqueue(_ frame: VideoFrame, completion: ((_ latencyMs: Double) -> Void)? = nil) {
        queue.async { [self] in
            guard let formatDescription else { return }
            if waitingForKeyframe {
                guard frame.isKeyframe else { return }
                waitingForKeyframe = false
            }
            let renderer = layer.sampleBufferRenderer
            if renderer.status == .failed || renderer.requiresFlushToResumeDecoding {
                renderer.flush()
                waitingForKeyframe = !frame.isKeyframe
                if waitingForKeyframe { return }
            }

            var blockBuffer: CMBlockBuffer?
            let length = frame.data.count
            guard CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: length, blockAllocator: nil,
                                                     customBlockSource: nil, offsetToData: 0, dataLength: length,
                                                     flags: 0, blockBufferOut: &blockBuffer) == noErr, let blockBuffer else { return }
            let copied = frame.data.withUnsafeBytes { ptr -> OSStatus in
                CMBlockBufferReplaceDataBytes(with: ptr.baseAddress!, blockBuffer: blockBuffer, offsetIntoDestination: 0, dataLength: length)
            }
            guard copied == noErr else { return }

            var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 60),
                                            presentationTimeStamp: CMTime(value: CMTimeValue(frame.ptsMicros), timescale: 1_000_000),
                                            decodeTimeStamp: .invalid)
            var sizeEntry = length
            var sampleBuffer: CMSampleBuffer?
            guard CMSampleBufferCreateReady(allocator: nil, dataBuffer: blockBuffer, formatDescription: formatDescription,
                                            sampleCount: 1, sampleTimingEntryCount: 1, sampleTimingArray: &timing,
                                            sampleSizeEntryCount: 1, sampleSizeArray: &sizeEntry,
                                            sampleBufferOut: &sampleBuffer) == noErr, let sampleBuffer else { return }
            if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true) as? [CFMutableDictionary],
               let first = attachments.first {
                CFDictionarySetValue(first, Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
                                     Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
            }
            renderer.enqueue(sampleBuffer)
            let nowMicros = Date().timeIntervalSince1970 * 1_000_000
            completion?((nowMicros - Double(frame.captureWallMicros)) / 1000)
        }
    }

    func reset() {
        queue.async { [self] in
            layer.sampleBufferRenderer.flush()
            formatDescription = nil
            waitingForKeyframe = true
        }
    }
}
