// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteApp.Protocol;

/// <summary>
/// JSON options reproducing what Swift's JSONEncoder/JSONDecoder produce and
/// accept for the protocol types (PROTOCOL.md §4): camelCase names (set
/// explicitly on every property), nulls omitted, byte[] as base64, enums as
/// their lowercase case names, and a lenient reader.
/// </summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Swift escapes "/" as "\/"; System.Text.Json accepts that on read. On
        // write, emit plain UTF-8 without escaping non-ASCII so text/clipboard
        // payloads stay compact.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Encode(ControlMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static string EncodeToString(ControlMessage message) =>
        JsonSerializer.Serialize(message, Options);

    public static ControlMessage Decode(ReadOnlySpan<byte> utf8) =>
        JsonSerializer.Deserialize<ControlMessage>(utf8, Options)
        ?? throw new JsonException("null control message");

    public static ControlMessage Decode(string json) =>
        JsonSerializer.Deserialize<ControlMessage>(json, Options)
        ?? throw new JsonException("null control message");
}

/// <summary>Writes <see cref="KeyModifiers"/> as the integer bitmask (not a string).</summary>
public sealed class KeyModifiersConverter : JsonConverter<KeyModifiers>
{
    public override KeyModifiers Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        (KeyModifiers)reader.GetByte();

    public override void Write(Utf8JsonWriter writer, KeyModifiers value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((byte)value);
}

/// <summary>
/// Swift Codable enum envelope for <see cref="InputEvent"/>:
/// <c>{"tap":{"at":{…},"button":"left"}}</c>, <c>{"key":{"_0":{…}}}</c>, etc.
/// Cases with labeled associated values are flat objects; <c>key</c> holds one
/// unlabeled value and is wrapped under <c>_0</c>.
/// </summary>
public sealed class InputEventConverter : JsonConverter<InputEvent>
{
    public override InputEvent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var (name, body) = Envelope.Open(doc.RootElement);
        switch (name)
        {
            case "tap":
                return new TapEvent(
                    body.GetProperty("at").Deserialize<NormalizedPoint>(options),
                    body.TryGetProperty("button", out var b) ? b.Deserialize<MouseButton>(options) : MouseButton.Left);
            case "longPress":
                return new LongPressEvent(body.GetProperty("at").Deserialize<NormalizedPoint>(options));
            case "scroll":
                return new ScrollEvent(
                    body.GetProperty("at").Deserialize<NormalizedPoint>(options),
                    body.GetProperty("dx").GetDouble(),
                    body.GetProperty("dy").GetDouble());
            case "drag":
                return new DragEvent(
                    body.GetProperty("at").Deserialize<NormalizedPoint>(options),
                    body.GetProperty("phase").Deserialize<DragPhase>(options));
            case "key":
                return new KeyInputEvent(Envelope.Unwrap<KeyEvent>(body, options));
            default:
                throw new JsonException($"unknown input event '{name}'");
        }
    }

    public override void Write(Utf8JsonWriter writer, InputEvent value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case TapEvent tap:
                writer.WriteStartObject("tap");
                writer.WritePropertyName("at");
                JsonSerializer.Serialize(writer, tap.At, options);
                writer.WritePropertyName("button");
                JsonSerializer.Serialize(writer, tap.Button, options);
                writer.WriteEndObject();
                break;
            case LongPressEvent lp:
                writer.WriteStartObject("longPress");
                writer.WritePropertyName("at");
                JsonSerializer.Serialize(writer, lp.At, options);
                writer.WriteEndObject();
                break;
            case ScrollEvent s:
                writer.WriteStartObject("scroll");
                writer.WritePropertyName("at");
                JsonSerializer.Serialize(writer, s.At, options);
                writer.WriteNumber("dx", s.Dx);
                writer.WriteNumber("dy", s.Dy);
                writer.WriteEndObject();
                break;
            case DragEvent d:
                writer.WriteStartObject("drag");
                writer.WritePropertyName("at");
                JsonSerializer.Serialize(writer, d.At, options);
                writer.WritePropertyName("phase");
                JsonSerializer.Serialize(writer, d.Phase, options);
                writer.WriteEndObject();
                break;
            case KeyInputEvent k:
                writer.WritePropertyName("key");
                Envelope.WriteWrapped(writer, k.Key, options);
                break;
            default:
                throw new JsonException($"unsupported input event {value.GetType().Name}");
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// Swift Codable enum envelope for <see cref="ControlMessage"/> (PROTOCOL.md §4):
/// a single-key object named after the case. One unlabeled associated value is
/// wrapped under <c>"_0"</c>; labeled values are keyed by label; a case with no
/// value is an empty object.
/// </summary>
public sealed class ControlMessageConverter : JsonConverter<ControlMessage>
{
    public override ControlMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var (name, body) = Envelope.Open(doc.RootElement);
        return name switch
        {
            "hello" => new HelloMessage(Envelope.Unwrap<Hello>(body, options)),
            "helloAck" => new HelloAckMessage(Envelope.Unwrap<HelloAck>(body, options)),
            "authFailed" => new AuthFailedMessage(body.GetProperty("reason").GetString() ?? ""),
            "listApps" => new ListAppsMessage(),
            "appList" => new AppListMessage(Envelope.Unwrap<List<AppInfo>>(body, options)),
            "selectApp" => new SelectAppMessage(Envelope.Unwrap<SelectApp>(body, options).Pid),
            "requestLaunchable" => new RequestLaunchableMessage(),
            "launchableApps" => new LaunchableAppsMessage(Envelope.Unwrap<List<LaunchableApp>>(body, options)),
            "launchApp" => new LaunchAppMessage(body.GetProperty("bundleIdentifier").GetString() ?? ""),
            "screenSize" => new ScreenSizeMessage(Envelope.Unwrap<ScreenSize>(body, options)),
            "windowGeometry" => new WindowGeometryMessage(Envelope.Unwrap<WindowGeometry>(body, options)),
            "videoConfig" => new VideoConfigMessage(Envelope.Unwrap<VideoConfig>(body, options)),
            "input" => new InputMessage(Envelope.Unwrap<InputEvent>(body, options)),
            "error" => new ErrorMessage(body.GetProperty("message").GetString() ?? ""),
            "ping" => new PingMessage(body.GetProperty("_0").GetUInt64()),
            "pong" => new PongMessage(body.GetProperty("_0").GetUInt64()),
            "requestMenu" => new RequestMenuMessage(),
            "menuTree" => new MenuTreeMessage(Envelope.Unwrap<List<MenuNode>>(body, options)),
            "menuAction" => new MenuActionMessage(body.GetProperty("path").Deserialize<List<int>>(options) ?? []),
            "requestClipboard" => new RequestClipboardMessage(),
            "clipboard" => new ClipboardMessage(Envelope.Unwrap<ClipboardContent>(body, options)),
            _ => new UnknownMessage(name),
        };
    }

    public override void Write(Utf8JsonWriter writer, ControlMessage value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case HelloMessage m: Wrapped(writer, "hello", m.Hello, options); break;
            case HelloAckMessage m: Wrapped(writer, "helloAck", m.Ack, options); break;
            case AuthFailedMessage m: Labeled(writer, "authFailed", "reason", m.Reason); break;
            case ListAppsMessage: Empty(writer, "listApps"); break;
            case AppListMessage m: Wrapped(writer, "appList", m.Apps, options); break;
            case SelectAppMessage m: Wrapped(writer, "selectApp", new SelectApp(m.Pid), options); break;
            case RequestLaunchableMessage: Empty(writer, "requestLaunchable"); break;
            case LaunchableAppsMessage m: Wrapped(writer, "launchableApps", m.Apps, options); break;
            case LaunchAppMessage m: Labeled(writer, "launchApp", "bundleIdentifier", m.BundleIdentifier); break;
            case ScreenSizeMessage m: Wrapped(writer, "screenSize", m.Screen, options); break;
            case WindowGeometryMessage m: Wrapped(writer, "windowGeometry", m.Geometry, options); break;
            case VideoConfigMessage m: Wrapped(writer, "videoConfig", m.Config, options); break;
            case InputMessage m: Wrapped(writer, "input", m.Event, options); break;
            case ErrorMessage m: Labeled(writer, "error", "message", m.Message); break;
            case PingMessage m:
                writer.WriteStartObject("ping");
                writer.WriteNumber("_0", m.Value);
                writer.WriteEndObject();
                break;
            case PongMessage m:
                writer.WriteStartObject("pong");
                writer.WriteNumber("_0", m.Value);
                writer.WriteEndObject();
                break;
            case RequestMenuMessage: Empty(writer, "requestMenu"); break;
            case MenuTreeMessage m: Wrapped(writer, "menuTree", m.Menus, options); break;
            case MenuActionMessage m:
                writer.WriteStartObject("menuAction");
                writer.WritePropertyName("path");
                JsonSerializer.Serialize(writer, m.Path, options);
                writer.WriteEndObject();
                break;
            case RequestClipboardMessage: Empty(writer, "requestClipboard"); break;
            case ClipboardMessage m: Wrapped(writer, "clipboard", m.Content, options); break;
            case UnknownMessage m: throw new JsonException($"cannot encode unknown message '{m.Name}'");
            default: throw new JsonException($"unsupported control message {value.GetType().Name}");
        }
        writer.WriteEndObject();
    }

    private static void Wrapped<T>(Utf8JsonWriter writer, string name, T value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(name);
        Envelope.WriteWrapped(writer, value, options);
    }

    private static void Labeled(Utf8JsonWriter writer, string name, string label, string value)
    {
        writer.WriteStartObject(name);
        writer.WriteString(label, value);
        writer.WriteEndObject();
    }

    private static void Empty(Utf8JsonWriter writer, string name)
    {
        writer.WriteStartObject(name);
        writer.WriteEndObject();
    }
}

internal static class Envelope
{
    /// <summary>Opens a single-key envelope object, returning the case name and body.</summary>
    public static (string Name, JsonElement Body) Open(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("envelope must be an object");
        foreach (var prop in root.EnumerateObject())
            return (prop.Name, prop.Value);
        throw new JsonException("empty envelope");
    }

    /// <summary>Reads the one unlabeled associated value: <c>{"_0": value}</c>.</summary>
    public static T Unwrap<T>(JsonElement body, JsonSerializerOptions options)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("_0", out var inner))
            throw new JsonException("expected {\"_0\": …}");
        return inner.Deserialize<T>(options) ?? throw new JsonException("null associated value");
    }

    public static void WriteWrapped<T>(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("_0");
        JsonSerializer.Serialize(writer, value, options);
        writer.WriteEndObject();
    }
}
