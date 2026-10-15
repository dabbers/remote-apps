import AppKit
import Foundation
import Network
import RemoteAppProtocol

@MainActor
final class ServerModel: ObservableObject {
    @Published var pairingCode = PairingCodeAuthenticator.randomCode()
    @Published var listenerState = "stopped"
    @Published var port: UInt16?
    @Published var clientState: String?
    @Published var stats: ClientSession.Stats?
    @Published var log: [String] = []
    @Published var screenRecordingGranted = CGPreflightScreenCaptureAccess()
    @Published var accessibilityGranted = WindowControl.isTrusted
    @Published var transport: Transport.Kind = .quic

    let serverName = Host.current().localizedName ?? "Mac"
    /// Mirror of the in-app log at ~/Library/Logs/RemoteAppServer.log.
    private static let logFile: FileHandle? = {
        let url = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/RemoteAppServer.log")
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let handle = try? FileHandle(forWritingTo: url)
        handle?.seekToEndOfFile()
        return handle
    }()
    private var listener: NWListener?
    private var sessions: [UUID: ClientSession] = [:]

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
        do {
            guard let url = Bundle.main.url(forResource: "server", withExtension: "p12") else {
                throw NSError(domain: "Server", code: 1, userInfo: [NSLocalizedDescriptionKey: "server.p12 missing; run Scripts/gen-cert.sh"])
            }
            let identity = try Transport.loadIdentity(p12: try Data(contentsOf: url), passphrase: "remoteapp")
            let params = Transport.serverParameters(kind: transport, identity: identity)
            let listener = try NWListener(using: params)
            listener.service = NWListener.Service(name: serverName, type: transport.serviceType)
            listener.stateUpdateHandler = { [weak self] state in
                Task { @MainActor in
                    guard let self else { return }
                    self.listenerState = "\(state)"
                    if case .ready = state { self.port = listener.port?.rawValue }
                    self.append("listener: \(state)")
                }
            }
            listener.newConnectionHandler = { [weak self] connection in
                Task { @MainActor in self?.accept(connection) }
            }
            listener.start(queue: .main)
            self.listener = listener
            append("advertising \(transport.serviceType) as “\(serverName)” over \(transport.rawValue); pairing code \(pairingCode)")
        } catch {
            append("listener failed: \(error.localizedDescription)")
            listenerState = "failed"
        }
    }

    func stop() {
        listener?.cancel()
        listener = nil
        sessions.values.forEach { $0.close() }
        sessions.removeAll()
        port = nil
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
            // One client at a time: the host can't be shared concurrently, so an
            // authenticated session evicts any other (including stale attempts
            // from the same client's other network paths).
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
