// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace RemoteApp.Protocol;

/// <summary>Wire framing kinds: <c>u32BE length | u8 kind | payload</c> (PROTOCOL.md §3).</summary>
public enum MessageKind : byte
{
    Control = 1,
    VideoFrame = 2,
}

/// <summary>A decoded frame: either a control message or a video frame.</summary>
public abstract record Message;
public sealed record ControlFrame(ControlMessage Control) : Message;
public sealed record VideoFrameMessage(VideoFrame Frame) : Message;

public sealed class FramingException(string message) : Exception(message);

/// <summary>
/// Binary video frame (kind 2, PROTOCOL.md §7):
/// <c>ptsMicros u64BE | captureWallMicros u64BE | keyframe u8 | AVCC access unit</c>.
/// </summary>
public sealed class VideoFrame
{
    public const int HeaderSize = 8 + 8 + 1;

    /// <summary>Presentation timestamp, microseconds, monotonic on the host.</summary>
    public ulong PtsMicros { get; }

    /// <summary>Wall-clock capture time, microseconds since the Unix epoch.</summary>
    public ulong CaptureWallMicros { get; }

    public bool IsKeyframe { get; }

    /// <summary>AVCC-formatted access unit: length-prefixed NAL units.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    public VideoFrame(ulong ptsMicros, ulong captureWallMicros, bool isKeyframe, ReadOnlyMemory<byte> data)
    {
        PtsMicros = ptsMicros;
        CaptureWallMicros = captureWallMicros;
        IsKeyframe = isKeyframe;
        Data = data;
    }

    /// <summary>Parses a kind-2 payload. Returns null if too short.</summary>
    public static VideoFrame? Parse(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < HeaderSize) return null;
        var span = payload.Span;
        var pts = BinaryPrimitives.ReadUInt64BigEndian(span);
        var wall = BinaryPrimitives.ReadUInt64BigEndian(span[8..]);
        var key = span[16] != 0;
        return new VideoFrame(pts, wall, key, payload[HeaderSize..]);
    }

    /// <summary>Writes the payload (without the 5-byte frame header) into <paramref name="dest"/>.</summary>
    public int WritePayload(Span<byte> dest)
    {
        BinaryPrimitives.WriteUInt64BigEndian(dest, PtsMicros);
        BinaryPrimitives.WriteUInt64BigEndian(dest[8..], CaptureWallMicros);
        dest[16] = (byte)(IsKeyframe ? 1 : 0);
        Data.Span.CopyTo(dest[HeaderSize..]);
        return HeaderSize + Data.Length;
    }

    public int PayloadLength => HeaderSize + Data.Length;
}

/// <summary>Encodes and decodes framed messages.</summary>
public static class MessageCodec
{
    public const int MaxFrameLength = ProtocolConstants.MaxFrameLength;

    /// <summary>A complete wire frame (header + kind + payload) for a control message.</summary>
    public static byte[] Encode(ControlMessage message)
    {
        var json = ProtocolJson.Encode(message);
        var frame = new byte[4 + 1 + json.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(1 + json.Length));
        frame[4] = (byte)MessageKind.Control;
        json.CopyTo(frame.AsSpan(5));
        return frame;
    }

    /// <summary>A complete wire frame (header + kind + payload) for a video frame.</summary>
    public static byte[] Encode(VideoFrame video)
    {
        var payloadLength = video.PayloadLength;
        var frame = new byte[4 + 1 + payloadLength];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)(1 + payloadLength));
        frame[4] = (byte)MessageKind.VideoFrame;
        video.WritePayload(frame.AsSpan(5));
        return frame;
    }

    /// <summary>Decodes one frame's kind byte and payload.</summary>
    public static Message DecodePayload(byte kind, ReadOnlyMemory<byte> payload)
    {
        switch ((MessageKind)kind)
        {
            case MessageKind.Control:
                return new ControlFrame(ProtocolJson.Decode(payload.Span));
            case MessageKind.VideoFrame:
                return new VideoFrameMessage(VideoFrame.Parse(payload) ?? throw new FramingException("malformed video frame"));
            default:
                throw new FramingException($"unknown message kind {kind}");
        }
    }
}

/// <summary>
/// Incremental parser: feed it bytes as they arrive, pull out complete
/// messages. Throws <see cref="FramingException"/> on a malformed stream; the
/// caller should drop the connection in that case.
/// </summary>
public sealed class FrameParser
{
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;

    public int BufferedBytes => _end - _start;

    public void Append(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(data.Length);
        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    /// <summary>Returns all complete messages currently buffered.</summary>
    public List<Message> Drain()
    {
        var output = new List<Message>();
        while (true)
        {
            var available = _end - _start;
            if (available < 5) break;
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(_buffer.AsSpan(_start));
            if (length < 1 || length > MessageCodec.MaxFrameLength)
                throw new FramingException($"frame length {length} out of range");
            if (available < 4 + length) break;
            var kind = _buffer[_start + 4];
            // Copy the payload out so the parser's buffer can be reused.
            var payload = _buffer.AsSpan(_start + 5, length - 1).ToArray();
            _start += 4 + length;
            output.Add(MessageCodec.DecodePayload(kind, payload));
        }
        if (_start == _end) { _start = 0; _end = 0; }
        return output;
    }

    private void EnsureCapacity(int extra)
    {
        if (_end + extra <= _buffer.Length) return;
        // Compact first; grow only if still too small.
        var used = _end - _start;
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, used);
            _start = 0;
            _end = used;
        }
        if (_end + extra > _buffer.Length)
        {
            var size = Math.Max(_buffer.Length * 2, _end + extra);
            Array.Resize(ref _buffer, size);
        }
    }
}
