// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace RemoteApp.Protocol;

/// <summary>Protocol-wide constants. See PROTOCOL.md §1–§2.</summary>
public static class ProtocolConstants
{
    /// <summary>Protocol version carried in <see cref="Hello"/>; bump on an incompatible change.</summary>
    public const uint ProtocolVersion = 1;

    /// <summary>ALPN token negotiated during the TLS handshake.</summary>
    public const string Alpn = "remoteapp/1";

    /// <summary>Port the server listens on for its tailnet interface.</summary>
    public const int DefaultPort = 53123;

    /// <summary>Port the server listens on for same-host (loopback) clients.</summary>
    public const int LoopbackPort = 53124;

    /// <summary>Largest frame (kind byte + payload) either side will accept.</summary>
    public const int MaxFrameLength = 64 * 1024 * 1024;
}

// MARK: Handshake

/// <summary>Client's drawable area in points, plus its backing scale.</summary>
public sealed record ScreenSize(
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("scale")] double Scale);

/// <summary>First message after TLS is up (client → server).</summary>
public sealed record Hello(
    [property: JsonPropertyName("protocolVersion")] uint ProtocolVersion,
    [property: JsonPropertyName("clientName")] string ClientName,
    [property: JsonPropertyName("deviceID")] string DeviceID,
    [property: JsonPropertyName("credential")] string Credential,
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("screen")] ScreenSize Screen);

/// <summary>Auth succeeded (server → client). <see cref="IssuedToken"/> is present only after a code pairing.</summary>
public sealed record HelloAck(
    [property: JsonPropertyName("serverName")] string ServerName,
    [property: JsonPropertyName("hostScale")] double HostScale,
    [property: JsonPropertyName("apps")] List<AppInfo> Apps,
    [property: JsonPropertyName("issuedToken")] string? IssuedToken);

// MARK: Apps

/// <summary>A running app with at least one streamable window.</summary>
public sealed record AppInfo(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("bundleIdentifier")] string BundleIdentifier,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("windowCount")] int WindowCount,
    [property: JsonPropertyName("iconPNG")] byte[]? IconPNG);

/// <summary>An installed app that can be launched and then streamed.</summary>
public sealed record LaunchableApp(
    [property: JsonPropertyName("bundleIdentifier")] string BundleIdentifier,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("iconPNG")] byte[]? IconPNG);

public sealed record SelectApp([property: JsonPropertyName("pid")] int Pid);

// MARK: Geometry & video

/// <summary>
/// Geometry of the window actually being streamed, after the host tried to
/// resize it to the client's screen. If <see cref="Width"/>/<see cref="Height"/>
/// differ from what the client asked for, the app clamped the resize.
/// </summary>
public sealed record WindowGeometry(
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("pixelWidth")] int PixelWidth,
    [property: JsonPropertyName("pixelHeight")] int PixelHeight,
    [property: JsonPropertyName("windowTitle")] string? WindowTitle);

[JsonConverter(typeof(JsonStringEnumConverter<VideoCodec>))]
public enum VideoCodec
{
    [JsonStringEnumMemberName("h264")] H264,
    [JsonStringEnumMemberName("hevc")] Hevc,
}

/// <summary>Decoder configuration: parameter sets (SPS, PPS[, VPS]) and the coded size.</summary>
public sealed record VideoConfig(
    [property: JsonPropertyName("codec")] VideoCodec Codec,
    [property: JsonPropertyName("parameterSets")] List<byte[]> ParameterSets,
    [property: JsonPropertyName("pixelWidth")] int PixelWidth,
    [property: JsonPropertyName("pixelHeight")] int PixelHeight,
    [property: JsonPropertyName("nalLengthSize")] int NalLengthSize = 4);

// MARK: Input

/// <summary>A point normalized to the streamed window: (0,0) top-left, (1,1) bottom-right.</summary>
public readonly record struct NormalizedPoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

[JsonConverter(typeof(JsonStringEnumConverter<MouseButton>))]
public enum MouseButton
{
    [JsonStringEnumMemberName("left")] Left,
    [JsonStringEnumMemberName("right")] Right,
    [JsonStringEnumMemberName("middle")] Middle,
}

[JsonConverter(typeof(JsonStringEnumConverter<DragPhase>))]
public enum DragPhase
{
    [JsonStringEnumMemberName("began")] Began,
    [JsonStringEnumMemberName("moved")] Moved,
    [JsonStringEnumMemberName("ended")] Ended,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
}

/// <summary>Modifier bitmask: shift=1, control=2, option(alt)=4, command=8. Encoded as an integer.</summary>
[Flags]
[JsonConverter(typeof(KeyModifiersConverter))]
public enum KeyModifiers : byte
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Option = 1 << 2,
    Command = 1 << 3,
}

/// <summary>
/// Key input: either <see cref="Text"/> (characters to type, OS-neutral) or
/// <see cref="KeyCode"/> (a protocol constant for a non-printing key, see
/// <see cref="KeyCodes"/>). <see cref="IsDown"/> is always true today; the
/// server synthesises a down+up pair.
/// </summary>
public sealed record KeyEvent(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("keyCode")] ushort? KeyCode,
    [property: JsonPropertyName("modifiers")] KeyModifiers Modifiers = KeyModifiers.None,
    [property: JsonPropertyName("isDown")] bool IsDown = true)
{
    public static KeyEvent ForText(string text, KeyModifiers modifiers = KeyModifiers.None) =>
        new(text, null, modifiers);

    public static KeyEvent ForKeyCode(ushort keyCode, KeyModifiers modifiers = KeyModifiers.None) =>
        new(null, keyCode, modifiers);
}

/// <summary>
/// Protocol key-code constants for non-printing keys (PROTOCOL.md §9). They
/// happen to equal macOS virtual key codes; treat them as protocol values.
/// The first eight are the documented core set; the rest are extended values
/// a Mac server passes straight through.
/// </summary>
public static class KeyCodes
{
    public const ushort Return = 36;
    public const ushort Delete = 51;      // Backspace
    public const ushort Tab = 48;
    public const ushort Escape = 53;
    public const ushort LeftArrow = 123;
    public const ushort RightArrow = 124;
    public const ushort DownArrow = 125;
    public const ushort UpArrow = 126;

    // Extended (macOS virtual key codes; forwarded verbatim by the Mac server).
    public const ushort ForwardDelete = 117;
    public const ushort Home = 115;
    public const ushort End = 119;
    public const ushort PageUp = 116;
    public const ushort PageDown = 121;
    public const ushort Space = 49;
    public const ushort F1 = 122, F2 = 120, F3 = 99, F4 = 118, F5 = 96, F6 = 97,
                        F7 = 98, F8 = 100, F9 = 101, F10 = 109, F11 = 103, F12 = 111;
}

/// <summary>One user input event, client → server. See <see cref="InputEventConverter"/> for the JSON shape.</summary>
[JsonConverter(typeof(InputEventConverter))]
public abstract record InputEvent;

/// <summary>A click at <paramref name="At"/>. The server derives double-clicks from timing.</summary>
public sealed record TapEvent(NormalizedPoint At, MouseButton Button = MouseButton.Left) : InputEvent;

/// <summary>A long press; the server maps it to a secondary (right) click.</summary>
public sealed record LongPressEvent(NormalizedPoint At) : InputEvent;

/// <summary>Scroll by (dx, dy) client points at <paramref name="At"/>. Positive dy = content moved down.</summary>
public sealed record ScrollEvent(NormalizedPoint At, double Dx, double Dy) : InputEvent;

/// <summary>Left-button drag, one message per phase.</summary>
public sealed record DragEvent(NormalizedPoint At, DragPhase Phase) : InputEvent;

public sealed record KeyInputEvent(KeyEvent Key) : InputEvent;

// MARK: Menu bar

/// <summary>One node of the streamed app's menu bar.</summary>
public sealed record MenuNode(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("isSeparator")] bool IsSeparator = false,
    [property: JsonPropertyName("checked")] bool Checked = false,
    [property: JsonPropertyName("shortcut")] string? Shortcut = null,
    [property: JsonPropertyName("children")] List<MenuNode>? Children = null)
{
    [JsonIgnore]
    public List<MenuNode> ChildrenOrEmpty => Children ?? [];

    [JsonIgnore]
    public bool HasSubmenu => Children is { Count: > 0 };
}

// MARK: Clipboard

[JsonConverter(typeof(JsonStringEnumConverter<ClipboardKind>))]
public enum ClipboardKind
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("image")] Image,
    [JsonStringEnumMemberName("file")] File,
}

/// <summary>
/// One clipboard item in a platform-neutral form: a kind, a MIME type and raw
/// bytes (plus a suggested filename for files/images). Text is UTF-8; images
/// are PNG or JPEG.
/// </summary>
public sealed record ClipboardItem(
    [property: JsonPropertyName("kind")] ClipboardKind Kind,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("data")] byte[] Data)
{
    public static ClipboardItem FromText(string text) =>
        new(ClipboardKind.Text, "text/plain", null, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Decoded text, if this is a text item.</summary>
    [JsonIgnore]
    public string? Text => Kind == ClipboardKind.Text ? System.Text.Encoding.UTF8.GetString(Data) : null;
}

/// <summary>A clipboard snapshot: zero or more items. Receivers take what they can use.</summary>
public sealed record ClipboardContent([property: JsonPropertyName("items")] List<ClipboardItem> Items)
{
    public static ClipboardContent Empty => new([]);

    [JsonIgnore]
    public bool IsEmpty => Items.Count == 0;

    /// <summary>First plain-text representation, if any.</summary>
    [JsonIgnore]
    public string? Text => Items.FirstOrDefault(i => i.Kind == ClipboardKind.Text)?.Text;
}

// MARK: Control message envelope

/// <summary>
/// A control message (framing kind 1). The JSON envelope is a single-key
/// object named after the case; see <see cref="ControlMessageConverter"/>
/// and PROTOCOL.md §4–§5. Use pattern matching on the concrete record types.
/// </summary>
[JsonConverter(typeof(ControlMessageConverter))]
public abstract record ControlMessage;

public sealed record HelloMessage(Hello Hello) : ControlMessage;
public sealed record HelloAckMessage(HelloAck Ack) : ControlMessage;
public sealed record AuthFailedMessage(string Reason) : ControlMessage;
public sealed record ListAppsMessage : ControlMessage;
public sealed record AppListMessage(List<AppInfo> Apps) : ControlMessage;
public sealed record SelectAppMessage(int Pid) : ControlMessage;
public sealed record RequestLaunchableMessage : ControlMessage;
public sealed record LaunchableAppsMessage(List<LaunchableApp> Apps) : ControlMessage;
public sealed record LaunchAppMessage(string BundleIdentifier) : ControlMessage;
public sealed record ScreenSizeMessage(ScreenSize Screen) : ControlMessage;
public sealed record WindowGeometryMessage(WindowGeometry Geometry) : ControlMessage;
public sealed record VideoConfigMessage(VideoConfig Config) : ControlMessage;
public sealed record InputMessage(InputEvent Event) : ControlMessage;
public sealed record ErrorMessage(string Message) : ControlMessage;
public sealed record PingMessage(ulong Value) : ControlMessage;
public sealed record PongMessage(ulong Value) : ControlMessage;
public sealed record RequestMenuMessage : ControlMessage;
public sealed record MenuTreeMessage(List<MenuNode> Menus) : ControlMessage;
public sealed record MenuActionMessage(List<int> Path) : ControlMessage;
public sealed record RequestClipboardMessage : ControlMessage;
public sealed record ClipboardMessage(ClipboardContent Content) : ControlMessage;

/// <summary>
/// A control message whose name this build does not know. Decoded leniently
/// (instead of dropping the connection) so a newer peer can add messages.
/// Never sent.
/// </summary>
public sealed record UnknownMessage(string Name) : ControlMessage;
