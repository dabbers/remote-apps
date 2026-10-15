import AppKit
import Foundation
import Network
import RemoteAppProtocol

@MainActor
final class ServerModel: ObservableObject {
    @Published var pairingCode = PairingCodeAuthenticator.randomCode()
    @Published var listenerState = "stopped"
    @Published var boundEndpoints: [String] = []
    @Published var clientState: String?
    @Published var stats: ClientSession.Stats?
    @Published var log: [String] = []
    @Published var screenRecordingGranted = CGPreflightScreenCaptureAccess()
    @Published var accessibilityGranted = WindowControl.isTrusted
    @Published var transport: Transport.Kind = .tcpTLS
    @Published var tailscale: TailscaleInfo?
    @Published var tailnetWarning: String?

    let serverName = Host.current().localizedName ?? "Mac"
    let port = defaultPort
    /// The address a client should connect to: MagicDNS name when on the tailnet.
    var connectTarget: String { tailscale?.dnsName ?? "127.0.0.1" }

    private static let logFile: FileHandle? = {
        let url = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/RemoteAppServer.log")
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let handle = try? FileHandle(forWritingTo: url)
        handle?.seekToEndOfFile()
        return handle
    }()
    private var listeners: [NWListener] = []
    private var sessions: [UUID: ClientSession] = [:]
    private var pathMonitor: NWPathMonitor?
    private var listenersBuilt = false

    func refreshPermissions() {
        screenRecordingGranted = CGPreflightScreenCaptureAccess()
        accessibilityGranted = WindowControl.isTrusted
    }

    func requestPermissions() {
        if !CGPreflightScreenCaptureAccess() { CGRequestScreenCaptureAccess() }
        if !WindowControl.isTrusted { WindowControl.promptForTrust() }
        refreshPermissions()
    }

    func start() {
        stop()
        tailscale = TailscaleInfo.current()
        if let ts = tailscale, !ts.online {
            tailnetWarning = "Tailscale is installed but this Mac is not online on the tailnet."
        } else if tailscale == nil {
            tailnetWarning = "Tailscale not detected. Binding to loopback only; no remote device can reach this server."
        } else {
            tailnetWarning = nil
        }

        guard let url = Bundle.main.url(forResource: "server", withExtension: "p12") else {
            append("server.p12 missing; run Scripts/gen-cert.sh")
            listenerState = "failed"
            return
        }
        let identity: SecIdentity
        do {
            identity = try Transport.loadIdentity(p12: try Data(contentsOf: url), passphrase: "remoteapp")
        } catch {
            append("identity load failed: \(error.localizedDescription)")
            listenerState = "failed"
            return
        }

        listenerState = "starting"
        append("transport \(transport.rawValue); connect target “\(connectTarget):\(port)”; pairing code \(pairingCode)")
        if let w = tailnetWarning { append("⚠︎ \(w)") }

        // Resolve live NWInterfaces, then bind each listener to one of them with
        // `requiredInterface` (IP_BOUND_IF). This scopes the socket to a single
        // interface — the Tailscale tunnel and loopback — so traffic on any other
        // interface (Wi-Fi/LAN/the public internet) is never delivered.
        // `requiredLocalEndpoint` would be cleaner in lsof but is unusable here:
        // every accepted connection inherits it and re-binds the listener's
        // address, failing with EADDRINUSE. See the README's Network exposure note.
        listenersBuilt = false
        let monitor = NWPathMonitor()
        pathMonitor = monitor
        monitor.pathUpdateHandler = { [weak self] path in
            Task { @MainActor in
                guard let self, !self.listenersBuilt else { return }
                self.listenersBuilt = true
                self.buildListeners(identity: identity, interfaces: path.availableInterfaces)
            }
        }
        monitor.start(queue: .main)
    }

    private func buildListeners(identity: SecIdentity, interfaces: [NWInterface]) {
        var bound: [String] = []

        func makeListener(bindPort: UInt16, label: String, configure: (NWParameters) -> Void) {
            do {
                let params = Transport.serverParameters(kind: transport, identity: identity)
                params.includePeerToPeer = false
                // The tailnet and loopback listeners share the port on different
                // interfaces (IP_BOUND_IF); SO_REUSEADDR lets both bind it. Accepted
                // connections inherit requiredInterface, not a local endpoint, so
                // they do not re-bind and the earlier EADDRINUSE does not recur.
                params.allowLocalEndpointReuse = true
                configure(params)
                let listener = try NWListener(using: params, on: NWEndpoint.Port(rawValue: bindPort)!)
                listener.stateUpdateHandler = { [weak self] state in
                    Task { @MainActor in
                        guard let self else { return }
                        if case .failed(let error) = state { self.append("listener \(label) failed: \(error)") }
                        if case .ready = state { self.append("listening on \(label):\(bindPort)") }
                        self.updateListenerState()
                    }
                }
                listener.newConnectionHandler = { [weak self] connection in
                    Task { @MainActor in self?.accept(connection) }
                }
                listener.start(queue: .main)
                listeners.append(listener)
                bound.append("\(label):\(bindPort)")
            } catch {
                append("bind \(label) failed: \(error.localizedDescription)")
            }
        }

        // Tailnet: bound to the specific Tailscale tunnel interface.
        if let ts = tailscale {
            if let name = TailscaleInfo.interfaceName(forIPv4: ts.ipv4),
               let iface = interfaces.first(where: { $0.name == name }) {
                makeListener(bindPort: port, label: "tailnet \(name)") { $0.requiredInterface = iface }
            } else {
                append("⚠︎ could not resolve the tailnet interface; tailnet listener not started")
            }
        }
        // Loopback for same-Mac clients (e.g. the Simulator). NWPathMonitor does
        // not list the loopback NWInterface, so restrict by interface *type*.
        // Uses port+1: two interface-bound sockets cannot share a port even with
        // SO_REUSEADDR, so same-Mac clients connect to 127.0.0.1:<port+1>.
        makeListener(bindPort: port + 1, label: "loopback lo0") { $0.requiredInterfaceType = .loopback }

        boundEndpoints = bound
        if listeners.isEmpty { listenerState = "failed" }
    }

    private func updateListenerState() {
        let states = listeners.map { $0.state }
        if states.contains(where: { if case .ready = $0 { return true } else { return false } }) {
            listenerState = "ready"
        } else if states.allSatisfy({ if case .failed = $0 { return true } else { return false } }), !states.isEmpty {
            listenerState = "failed"
        } else {
            listenerState = "starting"
        }
    }

    func stop() {
        pathMonitor?.cancel()
        pathMonitor = nil
        listenersBuilt = false
        listeners.forEach { $0.cancel() }
        listeners.removeAll()
        sessions.values.forEach { $0.close() }
        sessions.removeAll()
        boundEndpoints = []
        listenerState = "stopped"
    }

    func regenerateCode() {
        pairingCode = PairingCodeAuthenticator.randomCode()
        start()
    }

    private func accept(_ connection: NWConnection) {
        let session = ClientSession(connection: connection,
                                    authenticator: PairingCodeAuthenticator(code: pairingCode),
                                    serverName: serverName)
        let id = session.id
        session.onLog = { [weak self] line in Task { @MainActor in self?.append(line) } }
        session.onStateChange = { [weak self] state in
            Task { @MainActor in
                self?.clientState = state
                self?.append("client \(id.uuidString.prefix(4)): \(state)")
            }
        }
        session.onAuthenticated = { [weak self] in
            Task { @MainActor in
                guard let self else { return }
                for (otherId, other) in self.sessions where otherId != id && other.isAuthenticated { other.close() }
            }
        }
        session.onStats = { [weak self] stats in Task { @MainActor in self?.stats = stats } }
        session.onClose = { [weak self] in
            Task { @MainActor in
                self?.sessions[id] = nil
                self?.clientState = nil
                self?.stats = nil
            }
        }
        sessions[id] = session
        append("client connected from \(connection.endpoint)")
        session.start()
    }

    private func append(_ line: String) {
        let stamp = DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium)
        log.append("\(stamp)  \(line)")
        print("\(stamp)  \(line)"); fflush(stdout)
        Self.logFile?.write(Data("\(stamp)  \(line)\n".utf8))
        if log.count > 200 { log.removeFirst(log.count - 200) }
    }
}
