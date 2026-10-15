import Foundation
import Network
import UIKit
import RemoteAppProtocol

@MainActor
final class ClientModel: ObservableObject {
    enum Phase: Equatable {
        case browsing
        case connecting(String)
        case pairing(String, error: String?)
        case apps
        case streaming
    }

    @Published var phase: Phase = .browsing
    @Published var servers: [Discovery.Server] = []
    @Published var transport: Transport.Kind = .quic { didSet { discovery.start(kind: transport) } }
    @Published var apps: [AppInfo] = []
    @Published var serverName = ""
    @Published var geometry: WindowGeometry?
    @Published var latencyMs: Double = 0
    @Published var fps: Double = 0
    @Published var errorMessage: String?
    @Published var keyboardVisible = false

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

    // MARK: Connection lifecycle

    func choose(_ server: Discovery.Server) {
        pendingServer = server
        phase = .pairing(server.name, error: nil)
    }

    func pair(code: String) {
        guard let server = pendingServer else { return }
        if connection == nil {
            connect(to: server, then: code)
        } else {
            sendHello(code: code)
        }
    }

    private func connect(to server: Discovery.Server, then code: String) {
        phase = .connecting(server.name)
        let nw = NWConnection(to: server.endpoint, using: Transport.clientParameters(kind: transport))
        let conn = MessageConnection(connection: nw)
        conn.onStateChange = { [weak self] state in
            NSLog("RemoteApp connection state: %@", "\(state)")
            Task { @MainActor in
                guard let self else { return }
                switch state {
                case .ready:
                    self.sendHello(code: code)
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

    private func sendHello(code: String) {
        connection?.send(.hello(Hello(clientName: UIDevice.current.name, credential: code, screen: screen)))
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
        geometry = nil
        latencyEMA = nil
        connection?.send(.selectApp(SelectApp(pid: app.pid)))
        phase = .streaming
    }

    func backToApps() {
        keyboardVisible = false
        phase = .apps
    }

    func send(_ event: InputEvent) {
        connection?.send(.input(event))
    }

    // MARK: Incoming

    private func handle(_ message: Message) {
        guard case .control(let control) = message else { return }
        switch control {
        case .helloAck(let ack):
            serverName = ack.serverName
            apps = ack.apps
            phase = .apps
            startKeepalive()
        case .authFailed(let reason):
            if let server = pendingServer { phase = .pairing(server.name, error: reason) }
        case .appList(let list):
            apps = list
        case .windowGeometry(let g):
            geometry = g
        case .videoConfig(let config):
            renderer.apply(config)
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
