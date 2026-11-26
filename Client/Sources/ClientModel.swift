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

    // MARK: Foldable (iPhone Duo)
    /// Hinge state reported by the stream view; `.unknown` on flat devices and
    /// before the first update. The HUD shows the flex toggle only when half-open.
    @Published var posture: DevicePosture = .unknown
    /// True while the laptop deck (menu bar, keyboard, trackpad) is on screen.
    @Published var flexActive = false
    /// Half-open in landscape, like a laptop: stream on the upright half, a
    /// trackpad on the flat half. Off, the stream fills the screen regardless.
    @Published var flexModeEnabled: Bool = UserDefaults.standard.object(forKey: "flexMode") as? Bool ?? true {
        didSet { UserDefaults.standard.set(flexModeEnabled, forKey: "flexMode") }
    }
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
        var size = uiScreen.bounds.size
        // A half-open foldable streams on its upper half only (the hinge
        // crosses the long axis), so ask for that from the start.
        if posture == .partiallyOpen, flexModeEnabled, size.height > size.width {
            size.height = floor(size.height / 2)
        }
        return ScreenSize(width: size.width, height: size.height, scale: uiScreen.scale)
    }

    private let discovery = Discovery()
    private var connection: MessageConnection?
    private var pendingServer: Discovery.Server?
    private var latencyEMA: Double?
    private var frameCount = 0
    private var fpsWindowStart = Date()
    private var lastPublish = Date()
    private var keepalive: Timer?

    // MARK: Reconnect state
    /// True while a dropped session is being re-established; the UI shows a
    /// banner over whatever was on screen instead of an error.
    @Published var reconnecting = false
    /// Where to land after an automatic reconnect.
    private enum ResumeTarget {
        case apps
        /// `pid` is 0 for an app opened by bundle id, whose pid we never learn.
        case app(pid: Int32, bundleIdentifier: String)
    }
    /// Set once a session is up; nil means there is nothing to resume.
    private var resumeTarget: ResumeTarget?
    private var reconnectAttempt = 0
    private var reconnectWork: DispatchWorkItem?
    private var connectTimeout: DispatchWorkItem?
    /// The connection was dropped while in the background; redial on return.
    private var resumeOnForeground = false
    /// The Mac said another device took this session over: do not redial.
    private var takenOver = false
    /// When the current session was established, to tell a session that
    /// worked from one that is dropped again the moment it connects.
    private var sessionStart = Date.distantPast
    /// Tracked here rather than read from `UIApplication.applicationState`,
    /// which still says "background" while the will-enter-foreground
    /// notification is being delivered.
    private var inBackground = false
    private var backgroundTask: UIBackgroundTaskIdentifier = .invalid
    private var lifecycleObservers: [NSObjectProtocol] = []
    /// When the Mac was last heard from (any message, video included).
    private let inbound = ActivityClock()
    /// Delays before each redial; about 16 s in all before giving up.
    private static let reconnectDelays: [TimeInterval] = [0.2, 1, 2, 3, 5, 5]
    /// The Mac answers a ping within a round trip; this much silence means the
    /// connection is dead even though the socket has not said so.
    private static let silenceLimit: TimeInterval = 12

    init() {
        discovery.onUpdate = { [weak self] servers in self?.servers = servers }
        discovery.start(kind: transport)
        observeLifecycle()
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
        // A redial keeps the current screen under the "Reconnecting" banner.
        if !reconnecting { phase = .connecting(server.name) }
        // Never leave an earlier connection behind: its late callbacks are
        // ignored below, and the Mac sees it close rather than linger.
        connection?.cancel()
        connectTimeout?.cancel()
        let nw = NWConnection(to: server.endpoint, using: Transport.clientParameters(kind: transport))
        let conn = MessageConnection(connection: nw)
        conn.onStateChange = { [weak self] state in
            NSLog("RemoteApp connection state: %@", "\(state)")
            Task { @MainActor in
                // Only the current connection may change the model.
                guard let self, self.connection === conn else { return }
                switch state {
                case .ready:
                    self.connectTimeout?.cancel()
                    self.inbound.touch()
                    self.sendHello(code: credential, token: token)
                case .waiting(let error):
                    // `.failed` needs no case: it arrives through `onClose`.
                    if !self.reconnecting {
                        self.phase = .connecting("\(server.name) (waiting: \(error.localizedDescription))")
                    }
                default: break
                }
            }
        }
        // A connection stuck in `.waiting` (tunnel still coming up) never fails
        // by itself; give up on it so the retry logic can dial again.
        let timeout = DispatchWorkItem { [weak self] in
            guard let self, self.connection === conn else { return }
            self.connection = nil
            conn.cancel()
            self.connectionLost("timed out connecting to \(server.name)")
        }
        connectTimeout = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + 10, execute: timeout)
        conn.onMessage = { [weak self] message in
            self?.inbound.touch()
            // Video, and the configuration it decodes under, go straight to the
            // renderer off the main thread, in arrival order. A config that took
            // the main-actor hop with the other messages could land after the
            // keyframe it describes, and that keyframe was then dropped
            // (PROTOCOL.md §11); with the next keyframe far off, the picture
            // stayed black on a reconnect until a resize forced a new encoder.
            if case .video(let frame) = message {
                self?.renderer.enqueue(frame) { latency in
                    Task { @MainActor in self?.recordFrame(latencyMs: latency) }
                }
                return
            }
            if case .control(.videoConfig(let config)) = message {
                self?.renderer.apply(config)
                return
            }
            Task { @MainActor in self?.handle(message) }
        }
        conn.onClose = { [weak self] error in
            NSLog("RemoteApp connection closed: %@", "\(String(describing: error))")
            Task { @MainActor in
                guard let self, self.connection === conn else { return }
                self.connection = nil
                self.connectionLost(error?.localizedDescription)
            }
        }
        connection = conn
        conn.start()
    }

    /// QUIC drops idle connections (30 s); keep it warm while no video flows.
    /// Started only after the server accepted our hello so no ping precedes it.
    /// The same timer notices a dead connection: the Mac answers every ping,
    /// so a long silence means it is gone and the session is redialled.
    private func startKeepalive() {
        keepalive?.invalidate()
        inbound.touch()
        keepalive = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, let conn = self.connection else { return }
                if self.inbound.age > Self.silenceLimit {
                    self.connection = nil
                    conn.cancel()
                    self.connectionLost("the Mac stopped answering")
                    return
                }
                conn.send(.ping(UInt64(Date().timeIntervalSince1970 * 1000)))
            }
        }
    }

    // MARK: Reconnecting

    /// The connection ended without the user asking. An established session is
    /// redialled; a first connection that never got going reports the error.
    private func connectionLost(_ reason: String?) {
        keepalive?.invalidate()
        connectTimeout?.cancel()
        if takenOver {
            takenOver = false
            fail("Another device connected to \(serverName.isEmpty ? "the Mac" : serverName) and took over.")
            return
        }
        guard resumeTarget != nil, pendingServer != nil else {
            if case .browsing = phase { return }
            fail(reason.map { "Disconnected: \($0)" } ?? "Disconnected")
            return
        }
        // A session that held for a while earns a fresh set of attempts. One
        // that keeps dying right after it connects does not, so whatever is
        // closing it cannot keep this client redialling forever.
        if Date().timeIntervalSince(sessionStart) > 10 { reconnectAttempt = 0 }
        NSLog("RemoteApp connection lost (%@); reconnecting", reason ?? "closed")
        scheduleReconnect(lastError: reason)
    }

    private func scheduleReconnect(lastError: String?) {
        guard let server = pendingServer else { return }
        reconnecting = true
        // iOS gives a backgrounded app no network time worth dialling in:
        // wait for the user to come back.
        if inBackground {
            resumeOnForeground = true
            return
        }
        guard reconnectAttempt < Self.reconnectDelays.count else {
            fail("Lost the connection to \(server.name)" + (lastError.map { ": \($0)" } ?? ""))
            return
        }
        let delay = Self.reconnectDelays[reconnectAttempt]
        reconnectAttempt += 1
        reconnectWork?.cancel()
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.reconnecting, self.connection == nil else { return }
            self.connect(to: server, credential: "", token: self.savedToken(for: server.name))
        }
        reconnectWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }

    /// Back to a clean slate once a session is up, or abandoned. A session
    /// that has only just come up keeps its attempt count (`keepAttempts`)
    /// until it has proved it stays up; see `connectionLost`.
    private func clearReconnectState(keepAttempts: Bool = false) {
        reconnecting = false
        if !keepAttempts { reconnectAttempt = 0 }
        resumeOnForeground = false
        reconnectWork?.cancel()
        reconnectWork = nil
        connectTimeout?.cancel()
        connectTimeout = nil
    }

    // MARK: App lifecycle

    private func observeLifecycle() {
        let center = NotificationCenter.default
        lifecycleObservers = [
            center.addObserver(forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.didEnterBackground() }
            },
            center.addObserver(forName: UIApplication.willEnterForegroundNotification, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.willEnterForeground() }
            },
        ]
    }

    /// Switching apps suspends this one within seconds, which silently kills
    /// the socket. Ask iOS for its background grace period (about 30 s) so a
    /// quick switch keeps the session; when that runs out, hang up properly so
    /// the Mac frees the app at once instead of holding a dead session.
    private func didEnterBackground() {
        inBackground = true
        guard connection != nil, isConnected, backgroundTask == .invalid else { return }
        backgroundTask = UIApplication.shared.beginBackgroundTask(withName: "dabtop session") { [weak self] in
            MainActor.assumeIsolated { self?.hangUpForBackground() }
        }
    }

    private func hangUpForBackground() {
        keepalive?.invalidate()
        if let conn = connection {
            connection = nil
            conn.cancel()
        }
        if resumeTarget != nil {
            reconnecting = true
            resumeOnForeground = true
        }
        endBackgroundTask()
    }

    private func endBackgroundTask() {
        guard backgroundTask != .invalid else { return }
        UIApplication.shared.endBackgroundTask(backgroundTask)
        backgroundTask = .invalid
    }

    private func willEnterForeground() {
        inBackground = false
        endBackgroundTask()
        if resumeOnForeground || (reconnecting && connection == nil) {
            resumeOnForeground = false
            reconnectAttempt = 0   // the user came back: a fresh set of attempts
            scheduleReconnect(lastError: nil)
            return
        }
        guard let conn = connection, isConnected else { return }
        // The session survived the switch, or looks as if it did: make sure the
        // Mac still answers before trusting it.
        let asked = Date()
        conn.send(.ping(UInt64(asked.timeIntervalSince1970 * 1000)))
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in
            guard let self, self.connection === conn else { return }
            if self.inbound.last < asked {
                self.connection = nil
                conn.cancel()
                self.connectionLost("no answer after returning to the app")
            } else if case .streaming = self.phase {
                // The decoder was flushed in the background and a still window
                // sends no frames: restart the stream for a fresh keyframe.
                self.restartStream()
            }
        }
    }

    /// Asks the Mac to start the current app's stream again.
    private func restartStream() {
        guard case .app(let pid, let bundleIdentifier)? = resumeTarget else { return }
        renderer.reset()
        lastReportedSize = .zero
        if pid != 0 {
            connection?.send(.selectApp(SelectApp(pid: pid)))
        } else {
            connection?.send(.launchApp(bundleIdentifier: bundleIdentifier))
        }
    }

    private func sendHello(code: String, token: String?) {
        connection?.send(.hello(Hello(clientName: UIDevice.current.name, deviceID: deviceID,
                                      credential: code, token: token, screen: screen)))
    }

    func disconnect() {
        keepalive?.invalidate()
        clearReconnectState()
        resumeTarget = nil
        endBackgroundTask()
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
        clearReconnectState()
        resumeTarget = nil
        connection?.cancel()
        connection = nil
        renderer.reset()
        geometry = nil
        keyboardVisible = false   // or the keyboard comes back up on the next stream
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
        resumeTarget = .app(pid: app.pid, bundleIdentifier: app.bundleIdentifier)
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
        resumeTarget = .app(pid: 0, bundleIdentifier: app.bundleIdentifier)
        connection?.send(.launchApp(bundleIdentifier: app.bundleIdentifier))
        phase = .streaming
    }

    func backToApps() {
        keyboardVisible = false
        resumeTarget = .apps
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
            // Remember this device for next time so the code is not needed again.
            if let issued = ack.issuedToken, let key = pendingServer?.name { saveToken(issued, for: key) }
            // After a reconnect, go back to the app that was on screen. Its pid
            // is unchanged unless it was relaunched; then match the bundle id.
            let resuming = reconnecting ? resumeTarget : nil
            clearReconnectState(keepAttempts: true)
            sessionStart = Date()
            takenOver = false
            startKeepalive()
            if case .app(let pid, let bundleIdentifier)? = resuming {
                if let app = ack.apps.first(where: { $0.pid == pid && pid != 0 })
                    ?? ack.apps.first(where: { $0.bundleIdentifier == bundleIdentifier && !bundleIdentifier.isEmpty }) {
                    select(app)
                } else {
                    resumeTarget = .apps
                    phase = .apps
                }
            } else {
                resumeTarget = .apps
                phase = .apps
            }
        case .authFailed(let reason):
            clearReconnectState()
            resumeTarget = nil
            // A rejected token is stale (the Mac forgot this device): drop it and
            // ask for the code again.
            if let key = pendingServer?.name { clearToken(for: key) }
            if let server = pendingServer { phase = .pairing(server.name, error: reason) }
        case .appList(let list):
            apps = list
        case .windowGeometry(let g):
            geometry = g
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
            if message == sessionTakenOverMessage {
                // The close that follows is deliberate; `connectionLost` reports it.
                takenOver = true
            } else {
                errorMessage = message
            }
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

/// How far a foldable's hinge is open. Flat devices and Mac report `.unknown`.
enum DevicePosture: Equatable {
    case unknown, closed, partiallyOpen, fullyOpen
}
