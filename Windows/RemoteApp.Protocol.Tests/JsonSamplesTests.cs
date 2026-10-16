// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using RemoteApp.Protocol;
using Xunit;

namespace RemoteApp.Protocol.Tests;

/// <summary>
/// Every sample in PROTOCOL.md §5 must decode, and re-encode to the same JSON
/// (structurally — key order is irrelevant). This pins the Swift Codable
/// envelope rules (§4) so the Windows build stays wire-compatible.
/// </summary>
public class JsonSamplesTests
{
    public static IEnumerable<object[]> Samples() =>
    [
        ["""{"hello":{"_0":{"clientName":"Dave's iPhone","credential":"123456","deviceID":"A1B2-C3D4","protocolVersion":1,"screen":{"height":844,"scale":3,"width":390},"token":"tok_abc"}}}"""],
        ["""{"helloAck":{"_0":{"apps":[{"bundleIdentifier":"com.apple.TextEdit","name":"TextEdit","pid":501,"windowCount":1}],"hostScale":2,"issuedToken":"tok_new","serverName":"dab-mba"}}}"""],
        ["""{"authFailed":{"reason":"wrong pairing code"}}"""],
        ["""{"listApps":{}}"""],
        ["""{"appList":{"_0":[{"bundleIdentifier":"com.apple.TextEdit","iconPNG":"iVBORw==","name":"TextEdit","pid":501,"windowCount":1}]}}"""],
        ["""{"selectApp":{"_0":{"pid":501}}}"""],
        ["""{"requestLaunchable":{}}"""],
        ["""{"launchableApps":{"_0":[{"bundleIdentifier":"com.apple.TextEdit","name":"TextEdit"}]}}"""],
        ["""{"launchApp":{"bundleIdentifier":"com.apple.TextEdit"}}"""],
        ["""{"screenSize":{"_0":{"height":844,"scale":3,"width":390}}}"""],
        ["""{"windowGeometry":{"_0":{"height":790,"pixelHeight":2370,"pixelWidth":1170,"width":390,"windowTitle":"Untitled"}}}"""],
        ["""{"videoConfig":{"_0":{"codec":"h264","nalLengthSize":4,"parameterSets":["Z0IA","aM4="],"pixelHeight":2370,"pixelWidth":1170}}}"""],
        ["""{"input":{"_0":{"tap":{"at":{"x":0.5,"y":0.25},"button":"left"}}}}"""],
        ["""{"input":{"_0":{"longPress":{"at":{"x":0.5,"y":0.25}}}}}"""],
        ["""{"input":{"_0":{"scroll":{"at":{"x":0.5,"y":0.5},"dx":0,"dy":-40}}}}"""],
        ["""{"input":{"_0":{"drag":{"at":{"x":0.5,"y":0.5},"phase":"began"}}}}"""],
        ["""{"input":{"_0":{"key":{"_0":{"isDown":true,"modifiers":0,"text":"a"}}}}}"""],
        ["""{"input":{"_0":{"key":{"_0":{"isDown":true,"modifiers":8,"text":"c"}}}}}"""],
        ["""{"input":{"_0":{"key":{"_0":{"isDown":true,"keyCode":36,"modifiers":0}}}}}"""],
        ["""{"input":{"_0":{"key":{"_0":{"isDown":true,"keyCode":123,"modifiers":5}}}}}"""],
        ["""{"requestMenu":{}}"""],
        ["""{"menuTree":{"_0":[{"checked":false,"children":[{"checked":false,"children":[],"enabled":true,"isSeparator":false,"shortcut":"⌘N","title":"New"},{"checked":false,"children":[],"enabled":true,"isSeparator":true,"title":""},{"checked":false,"children":[],"enabled":true,"isSeparator":false,"shortcut":"⌘W","title":"Close"}],"enabled":true,"isSeparator":false,"title":"File"}]}}"""],
        ["""{"menuAction":{"path":[0,2]}}"""],
        ["""{"requestClipboard":{}}"""],
        ["""{"clipboard":{"_0":{"items":[{"data":"aGVsbG8=","kind":"text","mimeType":"text/plain"},{"data":"iVA=","kind":"file","mimeType":"image/png","name":"photo.png"}]}}}"""],
        ["""{"ping":{"_0":1700000000000}}"""],
        ["""{"pong":{"_0":1700000000000}}"""],
        ["""{"error":{"message":"something failed"}}"""],
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public void SampleRoundTrips(string json)
    {
        var message = ProtocolJson.Decode(json);
        Assert.IsNotType<UnknownMessage>(message);
        var reencoded = ProtocolJson.EncodeToString(message);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(reencoded)),
            $"expected {json}\n     got {reencoded}");
    }

    [Fact]
    public void SwiftEscapedSlashDecodes()
    {
        var message = ProtocolJson.Decode("""{"clipboard":{"_0":{"items":[{"data":"aGVsbG8=","kind":"text","mimeType":"text\/plain"}]}}}""");
        var clipboard = Assert.IsType<ClipboardMessage>(message);
        Assert.Equal("text/plain", clipboard.Content.Items[0].MimeType);
        Assert.Equal("hello", clipboard.Content.Text);
    }

    [Fact]
    public void OptionalsAreOmittedWhenNull()
    {
        var json = ProtocolJson.EncodeToString(new HelloMessage(new Hello(1, "pc", "id", "123456", null, new ScreenSize(1280, 800, 1))));
        Assert.DoesNotContain("token", json);
        Assert.DoesNotContain("null", json);
        var key = ProtocolJson.EncodeToString(new InputMessage(new KeyInputEvent(KeyEvent.ForKeyCode(KeyCodes.Return))));
        Assert.DoesNotContain("text", key);
        Assert.Contains("\"keyCode\":36", key);
    }

    [Fact]
    public void ModifiersEncodeAsInteger()
    {
        var json = ProtocolJson.EncodeToString(new InputMessage(new KeyInputEvent(
            KeyEvent.ForKeyCode(KeyCodes.LeftArrow, KeyModifiers.Shift | KeyModifiers.Option))));
        Assert.Contains("\"modifiers\":5", json);
    }

    [Fact]
    public void UnknownMessageIsLenient()
    {
        var message = ProtocolJson.Decode("""{"somethingNew":{"_0":42}}""");
        var unknown = Assert.IsType<UnknownMessage>(message);
        Assert.Equal("somethingNew", unknown.Name);
    }

    [Fact]
    public void TapDefaultsToLeftButton()
    {
        var message = ProtocolJson.Decode("""{"input":{"_0":{"tap":{"at":{"x":0.1,"y":0.2}}}}}""");
        var tap = Assert.IsType<TapEvent>(Assert.IsType<InputMessage>(message).Event);
        Assert.Equal(MouseButton.Left, tap.Button);
    }
}
