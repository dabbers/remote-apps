import Foundation

/// Protocol version. Bump when the wire format changes incompatibly.
public let protocolVersion: UInt32 = 1

/// Bonjour service type the server advertises for QUIC (see `Transport.Kind.serviceType`).
public let bonjourServiceType = "_remoteapp._udp"

/// ALPN token negotiated during the TLS/QUIC handshake.
public let alpnProtocol = "remoteapp/1"

// MARK: - Control messages (JSON, kind = .control)

public struct Hello: Codable, Sendable, Equatable {
    public var protocolVersion: UInt32
    public var clientName: String
    /// Pairing code (prototype) or password, depending on the authenticator in use.
    public var credential: String
    public var screen: ScreenSize

    public init(protocolVersion: UInt32 = RemoteAppProtocol.protocolVersion, clientName: String, credential: String, screen: ScreenSize) {
        self.protocolVersion = protocolVersion
        self.clientName = clientName
        self.credential = credential
        self.screen = screen
    }
}

public struct HelloAck: Codable, Sendable, Equatable {
    public var serverName: String
    /// Backing scale factor of the host's main display (e.g. 2.0 on Retina).
    public var hostScale: Double
    public var apps: [AppInfo]

    public init(serverName: String, hostScale: Double, apps: [AppInfo]) {
        self.serverName = serverName
        self.hostScale = hostScale
        self.apps = apps
    }
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

public enum InputEvent: Codable, Sendable, Equatable {
    /// A click at `at`. The server derives double-clicks from timing.
    case tap(at: NormalizedPoint, button: MouseButton)
    /// A long press; the server maps this to a secondary (right) click.
    case longPress(at: NormalizedPoint)
    /// Scroll by (dx, dy) client points at `at`. Positive dy = finger moved down.
    case scroll(at: NormalizedPoint, dx: Double, dy: Double)
    /// Left-button drag, one message per phase.
    case drag(at: NormalizedPoint, phase: DragPhase)
    case key(KeyEvent)
}

// MARK: Envelope

public enum ControlMessage: Codable, Sendable, Equatable {
    case hello(Hello)
    case helloAck(HelloAck)
    case authFailed(reason: String)
    case listApps
    case appList([AppInfo])
    case selectApp(SelectApp)
    case screenSize(ScreenSize)
    case windowGeometry(WindowGeometry)
    case videoConfig(VideoConfig)
    case input(InputEvent)
    case error(message: String)
    case ping(UInt64)
    case pong(UInt64)
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
