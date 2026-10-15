import Foundation
import ScreenCaptureKit
import CoreMedia

/// Captures one app with ScreenCaptureKit, cropped to a rectangle (the app's
/// main window). Because the filter is app-level rather than window-level,
/// menus, sheets and popovers belonging to the app are included while other
/// apps' windows are not.
final class WindowCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    struct Config {
        var rect: CGRect          // display-local points
        var scale: CGFloat
        var frameRate: Int
        var pixelSize: CGSize {
            CGSize(width: Self.even(rect.width * scale), height: Self.even(rect.height * scale))
        }
        static func even(_ v: CGFloat) -> CGFloat { max(2, floor(v / 2) * 2) }
    }

    var onFrame: ((CVPixelBuffer, CMTime) -> Void)?
    var onError: ((Error) -> Void)?

    private let app: SCRunningApplication
    private let display: SCDisplay
    private var stream: SCStream?
    private let queue = DispatchQueue(label: "remoteapp.capture", qos: .userInteractive)
    private(set) var config: Config

    init(app: SCRunningApplication, display: SCDisplay, config: Config) {
        self.app = app
        self.display = display
        self.config = config
    }

    private func makeConfiguration() -> SCStreamConfiguration {
        let c = SCStreamConfiguration()
        let px = config.pixelSize
        c.width = Int(px.width)
        c.height = Int(px.height)
        c.sourceRect = config.rect
        c.scalesToFit = false
        c.pixelFormat = kCVPixelFormatType_32BGRA
        c.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(config.frameRate))
        c.queueDepth = 6
        c.showsCursor = true
        c.backgroundColor = .black
        c.capturesAudio = false
        if #available(macOS 14.0, *) {
            c.ignoreShadowsDisplay = true
        }
        return c
    }

    func start() async throws {
        let filter = SCContentFilter(display: display, including: [app], exceptingWindows: [])
        let stream = SCStream(filter: filter, configuration: makeConfiguration(), delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        self.stream = stream
        try await stream.startCapture()
    }

    func update(rect: CGRect) async throws {
        config.rect = rect
        try await stream?.updateConfiguration(makeConfiguration())
    }

    func stop() async {
        try? await stream?.stopCapture()
        stream = nil
    }

    // MARK: SCStreamOutput

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid else { return }
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let statusRaw = attachments.first?[.status] as? Int,
              let status = SCFrameStatus(rawValue: statusRaw),
              status == .complete,
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        onFrame?(pixelBuffer, CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        onError?(error)
    }
}
