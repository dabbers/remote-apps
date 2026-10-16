// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

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

    func testInputEventsKeepTheSynthesizedWireShape() throws {
        // PROTOCOL.md §5: labeled fields as keys, `key` wrapped under "_0".
        let events: [(InputEvent, String)] = [
            (.tap(at: NormalizedPoint(x: 0.5, y: 0.25), button: .left), #""tap":{"#),
            (.longPress(at: NormalizedPoint(x: 0.5, y: 0.25)), #""longPress":{"at":{"#),
            (.scroll(at: NormalizedPoint(x: 0.5, y: 0.5), dx: 0, dy: -40), #""scroll":{"#),
            (.drag(at: NormalizedPoint(x: 0.5, y: 0.5), phase: .began), #""phase":"began""#),
            (.key(KeyEvent(keyCode: KeyEvent.returnKey)), #""key":{"_0":{"#),
            (.move(at: NormalizedPoint(x: 0.5, y: 0.5)), #""move":{"at":{"#),
        ]
        for (event, fragment) in events {
            let bytes = try MessageCodec.encode(.control(.input(event)))
            let json = String(decoding: bytes.dropFirst(5), as: UTF8.self)   // u32 length + kind byte
            XCTAssertTrue(json.contains(fragment), "\(json) should contain \(fragment)")
            var parser = FrameParser()
            parser.append(bytes)
            XCTAssertEqual(try parser.drain(), [.control(.input(event))])
        }
    }

    func testUnknownInputEventDecodesToNoOp() throws {
        let payload = Data(#"{"input":{"_0":{"wiggle":{"amount":3}}}}"#.utf8)
        let message = try MessageCodec.decodePayload(kind: MessageKind.control.rawValue, payload: payload)
        XCTAssertEqual(message, .control(.input(.unknown("wiggle"))))
        XCTAssertThrowsError(try MessageCodec.encode(.control(.input(.unknown("wiggle")))))
    }

    func testHelloAckFeaturesAreOptional() throws {
        // An older server sends no `features`; a client must read that as none.
        let legacy = Data(#"{"helloAck":{"_0":{"apps":[],"hostScale":2,"serverName":"host"}}}"#.utf8)
        guard case .control(.helloAck(let ack)) = try MessageCodec.decodePayload(kind: MessageKind.control.rawValue, payload: legacy) else {
            return XCTFail("expected helloAck")
        }
        XCTAssertNil(ack.features)
        XCTAssertFalse(ack.supportedFeatures.contains(ProtocolFeature.move))

        let current = HelloAck(serverName: "host", hostScale: 2, apps: [], features: ProtocolFeature.all)
        var parser = FrameParser()
        parser.append(try MessageCodec.encode(.control(.helloAck(current))))
        XCTAssertEqual(try parser.drain(), [.control(.helloAck(current))])
        XCTAssertTrue(current.supportedFeatures.contains(ProtocolFeature.move))
    }
}
