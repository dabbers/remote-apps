// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import Foundation

/// Protocol version. Bump when the wire format changes incompatibly.
public let protocolVersion: UInt32 = 1

/// Bonjour service type the server advertises for QUIC (see `Transport.Kind.serviceType`).
public let bonjourServiceType = "_remoteapp._udp"

/// ALPN token negotiated during the TLS/QUIC handshake.
public let alpnProtocol = "remoteapp/1"

/// Fixed port the server binds on its tailnet and loopback interfaces, so a
/// client can connect by MagicDNS name without discovering the port first.
public let defaultPort: UInt16 = 53123

// MARK: - Control messages (JSON, kind = .control)

public struct Hello: Codable, Sendable, Equatable {
    public var protocolVersion: UInt32
    public var clientName: String
    /// Stable per-install identifier so the server can remember this device.
    public var deviceID: String
    /// Pairing code (prototype) or password; empty when authenticating by token.
    public var credential: String
    /// A remembered pairing token from a previous successful pairing, if any.
    /// When it is valid the server skips the pairing code.
    public var token: String?
    public var screen: ScreenSize

    public init(protocolVersion: UInt32 = RemoteAppProtocol.protocolVersion, clientName: String,
                deviceID: String, credential: String, token: String? = nil, screen: ScreenSize) {
        self.protocolVersion = protocolVersion
        self.clientName = clientName
        self.deviceID = deviceID
        self.credential = credential
        self.token = token
        self.screen = screen
    }
}

public struct HelloAck: Codable, Sendable, Equatable {
    public var serverName: String
    /// Backing scale factor of the host's main display (e.g. 2.0 on Retina).
    public var hostScale: Double
    public var apps: [AppInfo]
    /// A pairing token the client should save and present next time to skip the
    /// code. Sent only after a successful code-based pairing.
    public var issuedToken: String?
    /// Optional capabilities this server accepts beyond the base protocol
    /// (names in `ProtocolFeature`). Absent from older servers, which a client
    /// must treat as "none" (see `supportedFeatures`).
    public var features: [String]?

    public init(serverName: String, hostScale: Double, apps: [AppInfo], issuedToken: String? = nil, features: [String]? = nil) {
        self.serverName = serverName
        self.hostScale = hostScale
        self.apps = apps
        self.issuedToken = issuedToken
        self.features = features
    }

    /// `features` as a set, empty when the server sent none.
    public var supportedFeatures: Set<String> { Set(features ?? []) }
}

/// Names of the optional capabilities a server may list in `HelloAck.features`.
/// A client sends an optional event only if the server listed its feature.
public enum ProtocolFeature {
    /// The server accepts the `move` (pointer hover) input event.
    public static let move = "move"
    /// Every optional capability the servers in this repository implement.
    public static let all: [String] = [move]
}

/// Client's drawable area, in points, plus its backing scale.
public struct ScreenSize: Codable, Sendable, Equatable {
    public var width: Double
    public var height: Double
    public var scale: Double

    public init(width: Double, height: Double, scale: Double) {
        self.width = width
        self.height = height
        self.scale = scale
    }
}

public struct AppInfo: Codable, Sendable, Equatable, Identifiable, Hashable {
    public var pid: Int32
    public var bundleIdentifier: String
    public var name: String
    public var windowCount: Int
    /// PNG-encoded icon, small (e.g. 64x64). Optional to keep the list light.
    public var iconPNG: Data?

    public var id: Int32 { pid }

    public init(pid: Int32, bundleIdentifier: String, name: String, windowCount: Int, iconPNG: Data? = nil) {
        self.pid = pid
        self.bundleIdentifier = bundleIdentifier
        self.name = name
        self.windowCount = windowCount
        self.iconPNG = iconPNG
    }
}

/// An installed app that can be launched (and then streamed), whether or not
/// it is currently running.
public struct LaunchableApp: Codable, Sendable, Equatable, Identifiable, Hashable {
    public var bundleIdentifier: String
    public var name: String
    public var iconPNG: Data?

    public var id: String { bundleIdentifier }

    public init(bundleIdentifier: String, name: String, iconPNG: Data? = nil) {
        self.bundleIdentifier = bundleIdentifier
        self.name = name
        self.iconPNG = iconPNG
    }
}

public struct SelectApp: Codable, Sendable, Equatable {
    public var pid: Int32
    public init(pid: Int32) { self.pid = pid }
}

/// Geometry of the window actually being streamed, after the host tried to
/// resize it to the client's screen. If `width`/`height` differ from what the
/// client asked for, the app clamped the resize and the client letterboxes.
public struct WindowGeometry: Codable, Sendable, Equatable {
    /// Window size in host points.
    public var width: Double
    public var height: Double
    /// Encoded video size in pixels.
    public var pixelWidth: Int
    public var pixelHeight: Int
    public var windowTitle: String?

    public init(width: Double, height: Double, pixelWidth: Int, pixelHeight: Int, windowTitle: String? = nil) {
        self.width = width
        self.height = height
        self.pixelWidth = pixelWidth
        self.pixelHeight = pixelHeight
        self.windowTitle = windowTitle
    }
}

/// H.264/HEVC decoder configuration: SPS/PPS(/VPS) parameter sets, sent before
/// the first keyframe and whenever they change.
public struct VideoConfig: Codable, Sendable, Equatable {
    public enum Codec: String, Codable, Sendable { case h264, hevc }
    public var codec: Codec
    public var parameterSets: [Data]
    public var pixelWidth: Int
    public var pixelHeight: Int
    /// NAL unit length prefix size in the frame payload (AVCC/HVCC), normally 4.
    public var nalLengthSize: Int

    public init(codec: Codec, parameterSets: [Data], pixelWidth: Int, pixelHeight: Int, nalLengthSize: Int = 4) {
        self.codec = codec
        self.parameterSets = parameterSets
        self.pixelWidth = pixelWidth
        self.pixelHeight = pixelHeight
        self.nalLengthSize = nalLengthSize
    }
}

// MARK: Input

/// A point normalized to the streamed window: (0,0) top-left, (1,1) bottom-right.
public struct NormalizedPoint: Codable, Sendable, Equatable {
    public var x: Double
    public var y: Double
    public init(x: Double, y: Double) { self.x = x; self.y = y }
}

public enum MouseButton: String, Codable, Sendable { case left, right, middle }

public enum DragPhase: String, Codable, Sendable { case began, moved, ended, cancelled }

public struct KeyModifiers: OptionSet, Codable, Sendable, Hashable {
    public let rawValue: UInt8
    public init(rawValue: UInt8) { self.rawValue = rawValue }
    public static let shift   = KeyModifiers(rawValue: 1 << 0)
    public static let control = KeyModifiers(rawValue: 1 << 1)
    public static let option  = KeyModifiers(rawValue: 1 << 2)
    public static let command = KeyModifiers(rawValue: 1 << 3)
}

/// Key input. Either `text` (characters typed, inserted via a unicode key
/// event) or `keyCode` (a macOS virtual key code for non-printing keys such
/// as Return = 36, Delete = 51, Tab = 48, Escape = 53, arrows 123–126).
public struct KeyEvent: Codable, Sendable, Equatable {
    public var text: String?
    public var keyCode: UInt16?
    public var modifiers: KeyModifiers
    public var isDown: Bool

    public init(text: String? = nil, keyCode: UInt16? = nil, modifiers: KeyModifiers = [], isDown: Bool = true) {
        self.text = text
        self.keyCode = keyCode
        self.modifiers = modifiers
        self.isDown = isDown
    }

    public static let returnKey: UInt16 = 36
    public static let deleteKey: UInt16 = 51
    public static let tabKey: UInt16 = 48
    public static let escapeKey: UInt16 = 53
    public static let leftArrow: UInt16 = 123
    public static let rightArrow: UInt16 = 124
    public static let downArrow: UInt16 = 125
    public static let upArrow: UInt16 = 126
}

public enum InputEvent: Sendable, Equatable {
    /// A click at `at`. The server derives double-clicks from timing.
    case tap(at: NormalizedPoint, button: MouseButton)
    /// A long press; the server maps this to a secondary (right) click.
    case longPress(at: NormalizedPoint)
    /// Scroll by (dx, dy) client points at `at`. Positive dy = finger moved down.
    case scroll(at: NormalizedPoint, dx: Double, dy: Double)
    /// Left-button drag, one message per phase.
    case drag(at: NormalizedPoint, phase: DragPhase)
    case key(KeyEvent)
    /// Pointer hover at `at` with no button held: the host cursor follows it.
    /// Optional — send only if the server lists `ProtocolFeature.move`, and at
    /// most ~60 per second (PROTOCOL.md §8).
    case move(at: NormalizedPoint)
    /// An input case this build does not know, sent by a newer peer. Decodes to
    /// this no-op instead of failing the message; never encoded.
    case unknown(String)
}

extension InputEvent: Codable {
    // Hand-written to produce exactly what Codable synthesis produces for the
    // known cases (PROTOCOL.md §4: a single-key envelope named after the case,
    // labeled fields as keys, one unlabeled value under "_0"), while mapping an
    // unknown case name to `.unknown` rather than throwing. Optional events can
    // thus be added without a protocol version bump.
    private struct CaseKey: CodingKey {
        var stringValue: String
        var intValue: Int? { nil }
        init(_ name: String) { stringValue = name }
        init?(stringValue: String) { self.stringValue = stringValue }
        init?(intValue: Int) { nil }
    }
    private enum TapKeys: String, CodingKey { case at, button }
    private enum PointKeys: String, CodingKey { case at }
    private enum ScrollKeys: String, CodingKey { case at, dx, dy }
    private enum DragKeys: String, CodingKey { case at, phase }
    private enum WrappedKeys: String, CodingKey { case _0 }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CaseKey.self)
        guard let key = container.allKeys.first else {
            throw DecodingError.dataCorrupted(.init(codingPath: decoder.codingPath, debugDescription: "empty input event"))
        }
        switch key.stringValue {
        case "tap":
            let c = try container.nestedContainer(keyedBy: TapKeys.self, forKey: key)
            self = .tap(at: try c.decode(NormalizedPoint.self, forKey: .at),
                        button: try c.decodeIfPresent(MouseButton.self, forKey: .button) ?? .left)
        case "longPress":
            let c = try container.nestedContainer(keyedBy: PointKeys.self, forKey: key)
            self = .longPress(at: try c.decode(NormalizedPoint.self, forKey: .at))
        case "scroll":
            let c = try container.nestedContainer(keyedBy: ScrollKeys.self, forKey: key)
            self = .scroll(at: try c.decode(NormalizedPoint.self, forKey: .at),
                           dx: try c.decode(Double.self, forKey: .dx),
                           dy: try c.decode(Double.self, forKey: .dy))
        case "drag":
            let c = try container.nestedContainer(keyedBy: DragKeys.self, forKey: key)
            self = .drag(at: try c.decode(NormalizedPoint.self, forKey: .at),
                         phase: try c.decode(DragPhase.self, forKey: .phase))
        case "key":
            let c = try container.nestedContainer(keyedBy: WrappedKeys.self, forKey: key)
            self = .key(try c.decode(KeyEvent.self, forKey: ._0))
        case "move":
            let c = try container.nestedContainer(keyedBy: PointKeys.self, forKey: key)
            self = .move(at: try c.decode(NormalizedPoint.self, forKey: .at))
        default:
            self = .unknown(key.stringValue)
        }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CaseKey.self)
        switch self {
        case .tap(let at, let button):
            var c = container.nestedContainer(keyedBy: TapKeys.self, forKey: CaseKey("tap"))
            try c.encode(at, forKey: .at)
            try c.encode(button, forKey: .button)
        case .longPress(let at):
            var c = container.nestedContainer(keyedBy: PointKeys.self, forKey: CaseKey("longPress"))
            try c.encode(at, forKey: .at)
        case .scroll(let at, let dx, let dy):
            var c = container.nestedContainer(keyedBy: ScrollKeys.self, forKey: CaseKey("scroll"))
            try c.encode(at, forKey: .at)
            try c.encode(dx, forKey: .dx)
            try c.encode(dy, forKey: .dy)
        case .drag(let at, let phase):
            var c = container.nestedContainer(keyedBy: DragKeys.self, forKey: CaseKey("drag"))
            try c.encode(at, forKey: .at)
            try c.encode(phase, forKey: .phase)
        case .key(let key):
            var c = container.nestedContainer(keyedBy: WrappedKeys.self, forKey: CaseKey("key"))
            try c.encode(key, forKey: ._0)
        case .move(let at):
            var c = container.nestedContainer(keyedBy: PointKeys.self, forKey: CaseKey("move"))
            try c.encode(at, forKey: .at)
        case .unknown(let name):
            throw EncodingError.invalidValue(self, .init(codingPath: encoder.codingPath, debugDescription: "cannot encode unknown input event '\(name)'"))
        }
    }
}

// MARK: Menu bar

/// One node of the streamed app's menu bar, read through the Accessibility API
/// (not captured as pixels), so the client can render it as a native iOS menu.
public struct MenuNode: Codable, Sendable, Equatable {
    public var title: String
    public var enabled: Bool
    public var isSeparator: Bool
    /// True when the item shows a checkmark (or mixed-state mark).
    public var checked: Bool
    /// Display hint for the keyboard shortcut, e.g. "⌘B" or "⇧⌘S"; nil if none.
    public var shortcut: String?
    public var children: [MenuNode]

    public init(title: String, enabled: Bool = true, isSeparator: Bool = false,
                checked: Bool = false, shortcut: String? = nil, children: [MenuNode] = []) {
        self.title = title
        self.enabled = enabled
        self.isSeparator = isSeparator
        self.checked = checked
        self.shortcut = shortcut
        self.children = children
    }

    public var hasSubmenu: Bool { !children.isEmpty }
}

// MARK: Clipboard (platform-agnostic)

/// One item on the shared clipboard, in a platform-neutral form: a kind, a MIME
/// type, and raw bytes (plus a suggested filename for files/images). Every
/// platform maps these to and from its own clipboard — macOS NSPasteboard, iOS
/// UIPasteboard, and (later) the Windows clipboard — so nothing on the wire is
/// tied to one OS's pasteboard flavors. Text is UTF-8 and images use a standard
/// web image type (PNG or JPEG), so any client can consume them.
public struct ClipboardItem: Codable, Sendable, Equatable {
    public enum Kind: String, Codable, Sendable { case text, image, file }
    public var kind: Kind
    /// MIME type, e.g. "text/plain", "image/png", "application/pdf",
    /// "application/octet-stream". Advisory; `kind` drives handling.
    public var mimeType: String
    /// Suggested filename for file/image items (e.g. "photo.png"); nil for text.
    public var name: String?
    public var data: Data

    public init(kind: Kind, mimeType: String, name: String? = nil, data: Data) {
        self.kind = kind
        self.mimeType = mimeType
        self.name = name
        self.data = data
    }

    /// A UTF-8 text item.
    public static func text(_ string: String) -> ClipboardItem {
        ClipboardItem(kind: .text, mimeType: "text/plain", data: Data(string.utf8))
    }

    /// Decoded text, if this is a text item.
    public var text: String? {
        guard kind == .text else { return nil }
        return String(data: data, encoding: .utf8)
    }
}

/// A clipboard snapshot: zero or more items (a single copy can carry several
/// representations, e.g. text plus an image). Receivers take what they can use.
public struct ClipboardContent: Codable, Sendable, Equatable {
    public var items: [ClipboardItem]
    public init(items: [ClipboardItem] = []) { self.items = items }

    public var isEmpty: Bool { items.isEmpty }
    /// First plain-text representation, if any.
    public var text: String? { items.first(where: { $0.kind == .text })?.text }
}

// MARK: Envelope

public enum ControlMessage: Codable, Sendable, Equatable {
    case hello(Hello)
    case helloAck(HelloAck)
    case authFailed(reason: String)
    case listApps
    case appList([AppInfo])
    case selectApp(SelectApp)
    /// Client asks for the list of installed (launchable) apps.
    case requestLaunchable
    /// Server's list of installed apps that can be launched.
    case launchableApps([LaunchableApp])
    /// Client asks the server to launch an app (if not already running) and
    /// stream it once it has a window.
    case launchApp(bundleIdentifier: String)
    case screenSize(ScreenSize)
    case windowGeometry(WindowGeometry)
    case videoConfig(VideoConfig)
    case input(InputEvent)
    case error(message: String)
    case ping(UInt64)
    case pong(UInt64)
    /// Client asks the server to (re-)read and send the menu bar.
    case requestMenu
    /// Server's current menu bar: top-level menus (the Apple menu is omitted).
    case menuTree([MenuNode])
    /// Client triggers a menu item, addressed by child indexes from the root
    /// of `menuTree` (e.g. [2, 5, 1] = third menu → its 6th item → its 2nd item).
    case menuAction(path: [Int])
    /// Either side asks the other for its current clipboard; the peer replies
    /// with `clipboard`. Used to pull the Mac's clipboard onto the device.
    case requestClipboard
    /// A clipboard snapshot pushed to the peer (a reply to `requestClipboard`,
    /// or an unsolicited push to set the peer's clipboard). Symmetric in both
    /// directions so any platform's client and server share the same code path.
    case clipboard(ClipboardContent)
}

// MARK: - Video frames (binary, kind = .videoFrame)

public struct VideoFrame: Sendable, Equatable {
    /// Presentation timestamp, microseconds, monotonic on the host.
    public var ptsMicros: UInt64
    /// Wall-clock capture time, microseconds since the Unix epoch. Used for
    /// end-to-end latency measurement when the clocks are in sync.
    public var captureWallMicros: UInt64
    public var isKeyframe: Bool
    /// AVCC/HVCC-formatted access unit: length-prefixed NAL units.
    public var data: Data

    public init(ptsMicros: UInt64, captureWallMicros: UInt64, isKeyframe: Bool, data: Data) {
        self.ptsMicros = ptsMicros
        self.captureWallMicros = captureWallMicros
        self.isKeyframe = isKeyframe
        self.data = data
    }

    static let headerSize = 8 + 8 + 1

    public func encodedPayload() -> Data {
        var d = Data(capacity: Self.headerSize + data.count)
        d.appendUInt64BE(ptsMicros)
        d.appendUInt64BE(captureWallMicros)
        d.append(isKeyframe ? 1 : 0)
        d.append(data)
        return d
    }

    public init?(payload: Data) {
        guard payload.count >= Self.headerSize else { return nil }
        var offset = payload.startIndex
        ptsMicros = payload.readUInt64BE(at: &offset)
        captureWallMicros = payload.readUInt64BE(at: &offset)
        isKeyframe = payload[offset] != 0
        offset += 1
        data = payload.subdata(in: offset..<payload.endIndex)
    }
}
