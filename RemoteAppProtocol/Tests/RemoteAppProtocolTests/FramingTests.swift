import XCTest
@testable import RemoteAppProtocol

final class FramingTests: XCTestCase {
    func testControlRoundTrip() throws {
        let hello = ControlMessage.hello(Hello(clientName: "iPhone", credential: "123456", screen: ScreenSize(width: 402, height: 874, scale: 3)))
        let input = ControlMessage.input(.scroll(at: NormalizedPoint(x: 0.5, y: 0.25), dx: 0, dy: -12.5))
        var parser = FrameParser()
        let bytes = try MessageCodec.encode(.control(hello)) + MessageCodec.encode(.control(input))
        // Feed in awkward chunks to exercise partial-frame buffering.
        for chunk in stride(from: 0, to: bytes.count, by: 7) {
            parser.append(bytes.subdata(in: chunk..<min(chunk + 7, bytes.count)))
        }
        let messages = try parser.drain()
        XCTAssertEqual(messages, [.control(hello), .control(input)])
    }

    func testVideoFrameRoundTrip() throws {
        let frame = VideoFrame(ptsMicros: 123_456_789, captureWallMicros: 1_700_000_000_000_000, isKeyframe: true, data: Data([0, 0, 0, 2, 0x65, 0x88]))
        var parser = FrameParser()
        parser.append(try MessageCodec.encode(.video(frame)))
        XCTAssertEqual(try parser.drain(), [.video(frame)])
    }

    func testRejectsOversizedFrame() throws {
        var parser = FrameParser()
        parser.append(Data([0xFF, 0xFF, 0xFF, 0xFF, 1]))
        XCTAssertThrowsError(try parser.drain())
    }
}
