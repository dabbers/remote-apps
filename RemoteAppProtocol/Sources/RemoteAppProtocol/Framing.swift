import Foundation

/// Wire framing: every message is `u32 length (big-endian) | u8 kind | payload`,
/// where `length` counts the kind byte plus the payload.
public enum MessageKind: UInt8, Sendable {
    case control = 1
    case videoFrame = 2
}

public enum Message: Sendable, Equatable {
    case control(ControlMessage)
    case video(VideoFrame)
}

public enum FramingError: Error, Equatable {
    case unknownKind(UInt8)
    case malformedPayload
    case frameTooLarge(Int)
}

public struct MessageCodec {
    public static let maxFrameLength = 64 * 1024 * 1024

    private static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .secondsSince1970
        return e
    }()
    private static let decoder = JSONDecoder()

    public static func encode(_ message: Message) throws -> Data {
        let kind: MessageKind
        let payload: Data
        switch message {
        case .control(let c):
            kind = .control
            payload = try encoder.encode(c)
        case .video(let f):
            kind = .videoFrame
            payload = f.encodedPayload()
        }
        var out = Data(capacity: 5 + payload.count)
        out.appendUInt32BE(UInt32(1 + payload.count))
        out.append(kind.rawValue)
        out.append(payload)
        return out
    }

    public static func decodePayload(kind rawKind: UInt8, payload: Data) throws -> Message {
        guard let kind = MessageKind(rawValue: rawKind) else { throw FramingError.unknownKind(rawKind) }
        switch kind {
        case .control:
            return .control(try decoder.decode(ControlMessage.self, from: payload))
        case .videoFrame:
            guard let f = VideoFrame(payload: payload) else { throw FramingError.malformedPayload }
            return .video(f)
        }
    }
}

/// Incremental parser: feed it bytes as they arrive, pull out complete messages.
public struct FrameParser {
    private var buffer = Data()

    public init() {}

    public mutating func append(_ data: Data) {
        buffer.append(data)
    }

    /// Returns all complete messages currently buffered. Throws on a malformed
    /// stream; the caller should drop the connection in that case.
    public mutating func drain() throws -> [Message] {
        var out: [Message] = []
        while true {
            guard buffer.count >= 5 else { break }
            var offset = buffer.startIndex
            let length = Int(buffer.readUInt32BE(at: &offset))
            guard length >= 1, length <= MessageCodec.maxFrameLength else { throw FramingError.frameTooLarge(length) }
            guard buffer.count >= 4 + length else { break }
            let kind = buffer[offset]
            offset += 1
            let payload = buffer.subdata(in: offset..<(buffer.startIndex + 4 + length))
            buffer.removeSubrange(buffer.startIndex..<(buffer.startIndex + 4 + length))
            out.append(try MessageCodec.decodePayload(kind: kind, payload: payload))
        }
        return out
    }
}

extension Data {
    mutating func appendUInt32BE(_ v: UInt32) {
        var be = v.bigEndian
        Swift.withUnsafeBytes(of: &be) { append(contentsOf: $0) }
    }
    mutating func appendUInt64BE(_ v: UInt64) {
        var be = v.bigEndian
        Swift.withUnsafeBytes(of: &be) { append(contentsOf: $0) }
    }
    func readUInt32BE(at offset: inout Index) -> UInt32 {
        var v: UInt32 = 0
        for i in 0..<4 { v = (v << 8) | UInt32(self[offset + i]) }
        offset += 4
        return v
    }
    func readUInt64BE(at offset: inout Index) -> UInt64 {
        var v: UInt64 = 0
        for i in 0..<8 { v = (v << 8) | UInt64(self[offset + i]) }
        offset += 8
        return v
    }
}
