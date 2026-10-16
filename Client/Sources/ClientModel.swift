// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import Foundation
import Network
import UIKit
import RemoteAppProtocol

@MainActor
final class ClientModel: ObservableObject {
    /// Shared instance so the Mac menu bar (built outside the SwiftUI view tree)
    /// can reach the live model.
    static let shared = ClientModel()

    enum Phase: Equatable {
        case browsing
        case connecting(String)
        case pairing(String, error: String?)
        case apps
        case streaming
    }

    @Published var phase: Phase = .browsing

    /// True once past pairing (app list or streaming).
    var isConnected: Bool {
        switch phase { case .apps, .streaming: return true; default: return false }
    }
    @Published var servers: [Discovery.Server] = []
    @Published var transport: Transport.Kind = .tcpTLS { didSet { discovery.start(kind: transport) } }
    @Published var apps: [AppInfo] = []
    @Published var serverName = ""
    /// Optional protocol capabilities the connected server advertised
    /// (`ProtocolFeature` names); empty for older servers.
    @Published var serverFeatures: Set<String> = []
    @Published var geometry: WindowGeometry?
    @Published var latencyMs: Double = 0
    @Published var fps: Double = 0
    @Published var errorMessage: String?
    @Published var keyboardVisible = false
    @Published var hudVisible = true
    @Published var menu: [MenuNode] = [] {
        didSet {
            #if targetEnvironment(macCatalyst)
            // Rebuild the Mac menu bar to mirror the streamed app.
            UIMenuSystem.main.setNeedsRebuild()
            #endif
        }
    }
    @Published var launchable: [LaunchableApp] = []
    /// Name of the app currently being streamed (used for the Mac window title).
    @Published var streamingAppName = ""
    /// Drives the "Open an app" sheet (also from the Mac menu bar).
    @Published var showLaunchSheet = false

    // MARK: Shared clipboard
    /// Drives the clipboard actions sheet.
    @Published var showClipboardSheet = false
    /// Files/images received from the Mac, awaiting the user's save choice.
    @Published var receivedClipboardItems: [ClipboardItem] = []
    /// Transient confirmation banner for clipboard actions.
    @Published var clipboardStatus: String?

    let renderer = VideoRenderer()
    /// Full-screen drawable size in points. Read lazily: at app init the
    /// window does not exist yet and UIScreen may report a placeholder size.
    var screen: ScreenSize {
        let uiScreen = (UIApplication.shared.connectedScenes.first as? UIWindowScene)?.screen ?? UIScreen.main
        return ScreenSize(width: uiScreen.bounds.width, height: uiScreen.bounds.height, scale: uiScreen.scale)
    }

    private let discovery = Discovery()
    private var connection: MessageConnection?
    private var pendingServer: Discovery.Server?
    private var latencyEMA: Double?
    private var frameCount = 0
    private var fpsWindowStart = Date()
    private var lastPublish = Date()
    private var keepalive: Timer?

    init() {
        discovery.onUpdate = { [weak self] servers in self?.servers = servers }
        discovery.start(kind: transport)
    }

    // MARK: Responsive sizing

    /// The drawable area changed — device rotation, a resized Mac/iPad window, or
    /// the keyboard shrinking the view. Report it (in points) so the host window
    /// resizes to match and the picture fills it. Debounced, since each resize
    /// rebuilds the host encoder.
    private var lastReportedSize: CGSize = .zero
    private var sizeReportWork: DispatchWorkItem?
    func reportStreamSize(_ size: CGSize) {
        guard case .streaming = phase, size.width > 50, size.height > 50 else { return }
        guard abs(size.width - lastReportedSize.width) > 8 || abs(size.height - lastReportedSize.height) > 8 else { return }
        // Debounce: during a live window drag the size changes continuously, and
        // each host resize rebuilds the encoder — only act once it settles.
        sizeReportWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.lastReportedSize = size
            self.connection?.send(.screenSize(ScreenSize(width: size.width, height: size.height, scale: self.screen.scale)))
        }
        sizeReportWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.18, execute: work)
    }

    // MARK: Remembered pairing

    private let defaults = UserDefaults.standard

    /// Stable per-install identifier so a server can remember this device.
    private var deviceID: String {
        if let id = defaults.string(forKey: "deviceID") { return id }
        let id = UUID().uuidString
        defaults.set(id, forKey: "deviceID")
        return id
    }

    private func savedToken(for key: String) -> String? {
        (defaults.dictionary(forKey: "pairTokens") as? [String: String])?[key]
    }
    private func saveToken(_ token: String, for key: String) {
        var d = (defaults.dictionary(forKey: "pairTokens") as? [String: String]) ?? [:]
        d[key] = token
        defaults.set(d, forKey: "pairTokens")
    }
    private func clearToken(for key: String) {
        var d = (defaults.dictionary(forKey: "pairTokens") as? [String: String]) ?? [:]
        d[key] = nil
        defaults.set(d, forKey: "pairTokens")
    }

    // MARK: Connection lifecycle

    func choose(_ server: Discovery.Server) {
        pendingServer = server
        // A remembered Mac connects straight through, skipping the code screen.
        if let token = savedToken(for: server.name) {
            connect(to: server, credential: "", token: token)
        } else {
            phase = .pairing(server.name, error: nil)
        }
    }

    /// Connect by MagicDNS name (e.g. my-mac.tailnet.ts.net) or tailnet IP. This
    /// is the Tailscale path: the address resolves and routes only over the
    /// tailnet, so nothing is exposed to the open internet.
    func chooseManual(host: String) {
        let trimmed = host.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        // Accept an optional ":port" suffix (e.g. 127.0.0.1:53124 for the
        // server's loopback listener); default to the published port otherwise.
        var hostPart = trimmed
        var portValue = defaultPort
        if let colon = trimmed.lastIndex(of: ":"),
           let p = UInt16(trimmed[trimmed.index(after: colon)...]),
           !trimmed[trimmed.startIndex..<colon].contains(":") {
            hostPart = String(trimmed[trimmed.startIndex..<colon])
            portValue = p
        }
        let endpoint = NWEndpoint.hostPort(host: NWEndpoint.Host(hostPart), port: NWEndpoint.Port(rawValue: portValue)!)
        choose(Discovery.Server(name: trimmed, endpoint: endpoint))
    }

    func pair(code: String) {
        guard let server = pendingServer else { return }
        if connection == nil {
            connect(to: server, credential: code, token: nil)
        } else {
            sendHello(code: code, token: nil)
        }
    }

    private func connect(to server: Discovery.Server, credential: String, token: String?) {
        phase = .connecting(server.name)
        let nw = NWConnection(to: server.endpoint, using: Transport.clientParameters(kind: transport))
        let conn = MessageConnection(connection: nw)
        conn.onStateChange = { [weak self] state in
            NSLog("RemoteApp connection state: %@", "\(state)")
            Task { @MainActor in
                guard let self else { return }
                switch state {
                case .ready:
                    self.sendHello(code: credential, token: token)
                case .failed(let error):
                    self.fail("Connection failed: \(error.localizedDescription)")
                case .waiting(let error):
                    self.phase = .connecting("\(server.name) (waiting: \(error.localizedDescription))")
                default: break
                }
            }
        }
        conn.onMessage = { [weak self] message in
            // Video goes straight to the renderer off the main thread.
            if case .video(let frame) = message {
                self?.renderer.enqueue(frame) { latency in
                    Task { @MainActor in self?.recordFrame(latencyMs: latency) }
                }
                return
            }
            Task { @MainActor in self?.handle(message) }
        }
        conn.onClose = { [weak self] error in
            NSLog("RemoteApp connection closed: %@", "\(String(describing: error))")
            Task { @MainActor in
                guard let self, self.connection === conn else { return }
                self.connection = nil
                if case .browsing = self.phase { return }
                self.fail(error.map { "Disconnected: \($0.localizedDescription)" } ?? "Disconnected")
            }
        }
        connection = conn
        conn.start()
    }

    /// QUIC drops idle connections (30 s); keep it warm while no video flows.
    /// Started only after the server accepted our hello so no ping precedes it.
    private func startKeepalive() {
        keepalive?.invalidate()
        keepalive = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            self?.connection?.send(.ping(UInt64(Date().timeIntervalSince1970 * 1000)))
        }
    }

    private func sendHello(code: String, token: String?) {
        connection?.send(.hello(Hello(clientName: UIDevice.current.name, deviceID: deviceID,
                                      credential: code, token: token, screen: screen)))
    }

    func disconnect() {
        keepalive?.invalidate()
        connection?.cancel()
        connection = nil
        renderer.reset()
        geometry = nil
        apps = []
        keyboardVisible = false
        phase = .browsing
    }

    private func fail(_ message: String) {
        errorMessage = message
        keepalive?.invalidate()
        connection?.cancel()
        connection = nil
        renderer.reset()
        geometry = nil
        phase = .browsing
    }

    // MARK: Actions

    func refreshApps() { connection?.send(.listApps) }

    func select(_ app: AppInfo) {
        renderer.reset()
        streamingAppName = app.name
        geometry = nil
        menu = []
        latencyEMA = nil
        lastReportedSize = .zero
        connection?.send(.selectApp(SelectApp(pid: app.pid)))
        phase = .streaming
    }

    func requestLaunchable() { connection?.send(.requestLaunchable) }

    /// Launch an installed app (server opens it if needed) and stream it.
    func launchApp(_ app: LaunchableApp) {
        renderer.reset()
        streamingAppName = app.name
        geometry = nil
        menu = []
        latencyEMA = nil
        lastReportedSize = .zero
        connection?.send(.launchApp(bundleIdentifier: app.bundleIdentifier))
        phase = .streaming
    }

    func backToApps() {
        keyboardVisible = false
        phase = .apps
    }

    func send(_ event: InputEvent) {
        connection?.send(.input(event))
    }

    // MARK: Menu bar

    func requestMenu() { connection?.send(.requestMenu) }

    func performMenu(path: [Int]) { connection?.send(.menuAction(path: path)) }

    // MARK: Shared clipboard

    /// Pull the Mac's clipboard onto this device.
    func getClipboardFromMac() { connection?.send(.requestClipboard) }

    /// Push this device's clipboard (text/image) to the Mac.
    func sendClipboardToMac() {
        let content = Clipboard.current()
        guard !content.isEmpty else { setClipboardStatus("This device's clipboard is empty"); return }
        connection?.send(.clipboard(content))
        setClipboardStatus("Sent \(content.items.count) item\(content.items.count == 1 ? "" : "s") to Mac")
    }

    /// Push picked files/photos to the Mac (they land there as files).
    func sendItemsToMac(_ items: [ClipboardItem]) {
        guard !items.isEmpty else { return }
        connection?.send(.clipboard(ClipboardContent(items: items)))
        setClipboardStatus("Sent \(items.count) item\(items.count == 1 ? "" : "s") to Mac")
    }

    private func setClipboardStatus(_ text: String) {
        clipboardStatus = text
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { [weak self] in
            if self?.clipboardStatus == text { self?.clipboardStatus = nil }
        }
    }

    // MARK: Incoming

    private func handle(_ message: Message) {
        guard case .control(let control) = message else { return }
        switch control {
        case .helloAck(let ack):
            serverName = ack.serverName
            serverFeatures = ack.supportedFeatures
            apps = ack.apps
            phase = .apps
            // Remember this device for next time so the code is not needed again.
            if let issued = ack.issuedToken, let key = pendingServer?.name { saveToken(issued, for: key) }
            startKeepalive()
        case .authFailed(let reason):
            // A rejected token is stale (the Mac forgot this device): drop it and
            // ask for the code again.
            if let key = pendingServer?.name { clearToken(for: key) }
            if let server = pendingServer { phase = .pairing(server.name, error: reason) }
        case .appList(let list):
            apps = list
        case .windowGeometry(let g):
            geometry = g
        case .videoConfig(let config):
            renderer.apply(config)
        case .menuTree(let tree):
            menu = tree
        case .launchableApps(let list):
            launchable = list
        case .clipboard(let content):
            // Text/images go straight onto this device's clipboard so Paste
            // works at once; files/images are also offered for saving.
            let saveable = Clipboard.apply(content)
            if saveable.isEmpty {
                setClipboardStatus(content.text != nil ? "Copied text from Mac" : "Mac clipboard is empty")
            } else {
                receivedClipboardItems = saveable
            }
        case .error(let message):
            errorMessage = message
        case .ping(let n):
            connection?.send(.pong(n))
        default:
            break
        }
    }

    private func recordFrame(latencyMs: Double) {
        frameCount += 1
        latencyEMA = latencyEMA.map { $0 * 0.9 + latencyMs * 0.1 } ?? latencyMs
        let now = Date()
        if now.timeIntervalSince(lastPublish) >= 0.5 {
            let elapsed = now.timeIntervalSince(fpsWindowStart)
            fps = Double(frameCount) / max(elapsed, 0.001)
            frameCount = 0
            fpsWindowStart = now
            lastPublish = now
            self.latencyMs = latencyEMA ?? 0
        }
    }
}
