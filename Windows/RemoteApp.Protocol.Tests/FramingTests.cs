// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Protocol;
using Xunit;

namespace RemoteApp.Protocol.Tests;

public class FramingTests
{
    [Fact]
    public void ControlRoundTripInAwkwardChunks()
    {
        var hello = new HelloMessage(new Hello(1, "iPhone", "dev", "123456", null, new ScreenSize(402, 874, 3)));
        var input = new InputMessage(new ScrollEvent(new NormalizedPoint(0.5, 0.25), 0, -12.5));
        var bytes = MessageCodec.Encode(hello).Concat(MessageCodec.Encode(input)).ToArray();

        var parser = new FrameParser();
        for (var i = 0; i < bytes.Length; i += 7)
            parser.Append(bytes.AsSpan(i, Math.Min(7, bytes.Length - i)));

        var messages = parser.Drain();
        Assert.Equal(2, messages.Count);
        Assert.Equal(hello, Assert.IsType<ControlFrame>(messages[0]).Control);
        Assert.Equal(input, Assert.IsType<ControlFrame>(messages[1]).Control);
        Assert.Equal(0, parser.BufferedBytes);
    }

    [Fact]
    public void VideoFrameRoundTrip()
    {
        var frame = new VideoFrame(123_456_789, 1_700_000_000_000_000, true, new byte[] { 0, 0, 0, 2, 0x65, 0x88 });
        var encoded = MessageCodec.Encode(frame);
        Assert.Equal(4 + 1 + VideoFrame.HeaderSize + 6, encoded.Length);
        Assert.Equal((byte)MessageKind.VideoFrame, encoded[4]);

        var parser = new FrameParser();
        parser.Append(encoded);
        var decoded = Assert.IsType<VideoFrameMessage>(Assert.Single(parser.Drain())).Frame;
        Assert.Equal(frame.PtsMicros, decoded.PtsMicros);
        Assert.Equal(frame.CaptureWallMicros, decoded.CaptureWallMicros);
        Assert.True(decoded.IsKeyframe);
        Assert.Equal(frame.Data.ToArray(), decoded.Data.ToArray());
    }

    [Fact]
    public void HeaderIsBigEndianAndCountsKindByte()
    {
        var encoded = MessageCodec.Encode(new ListAppsMessage());
        var json = "{\"listApps\":{}}"u8;
        Assert.Equal(new byte[] { 0, 0, 0, (byte)(1 + json.Length), 1 }, encoded[..5]);
        Assert.Equal(json.ToArray(), encoded[5..]);
    }

    [Fact]
    public void RejectsOversizedFrame()
    {
        var parser = new FrameParser();
        parser.Append(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1 });
        Assert.Throws<FramingException>(() => parser.Drain());
    }

    [Fact]
    public void RejectsUnknownKind()
    {
        var parser = new FrameParser();
        parser.Append(new byte[] { 0, 0, 0, 2, 9, 0 });
        Assert.Throws<FramingException>(() => parser.Drain());
    }

    [Theory]
    [InlineData("my-mac.tailnet.ts.net", "my-mac.tailnet.ts.net", 53123)]
    [InlineData("127.0.0.1:53124", "127.0.0.1", 53124)]
    [InlineData("[::1]:53124", "::1", 53124)]
    [InlineData("fd7a:115c:a1e0::1", "fd7a:115c:a1e0::1", 53123)]
    [InlineData("  host : 1 ", "host ", 1)]
    public void EndpointParsing(string text, string host, int port)
    {
        var endpoint = Endpoint.Parse(text);
        Assert.Equal(host, endpoint.Host);
        Assert.Equal(port, endpoint.Port);
    }
}
