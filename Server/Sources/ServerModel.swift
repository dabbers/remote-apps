// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

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
    /// Software display for a host with no monitor (VirtualDisplay.swift).
    @Published var virtualDisplayMode = VirtualDisplayManager.shared.mode {
        didSet {
            guard virtualDisplayMode != oldValue else { return }
            VirtualDisplayManager.shared.setMode(virtualDisplayMode)
        }
    }
    @Published var virtualDisplayActive = false

    let serverName = Host.current().localizedName ?? "Mac"
    let pairingStore = PairingStore()
    @Published var pairedCount = 0

    func forgetPairedDevices() {
        pairingStore.forgetAll()
        pairedCount = 0
        append("forgot all paired devices; they must enter the code again")
    }

    func refreshPairedCount() { pairedCount = pairingStore.pairedCount }
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

    // Multi-session (PROTOCOL.md §13): the device each authenticated session
    // belongs to, and the app pid each streaming session currently holds.
    private var deviceOf: [UUID: String] = [:]
    private var streamingPID: [UUID: pid_t] = [:]
    private let maxSessionsPerDevice = 8

    /// The display stays awake while a client is connected, and for as long as
    /// the server streams from its own virtual display (a headless host): once
    /// that display sleeps macOS locks the screen, and then nothing can be
    /// streamed until someone unlocks it by hand.
    private func updateDisplayPower() {
        DisplayPower.keepAwake(!deviceOf.isEmpty || VirtualDisplayManager.shared.displayID != nil)
    }

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
        refreshPairedCount()
        // Before anything is captured: a headless host needs a display to exist.
        let displays = VirtualDisplayManager.shared
        displays.onLog = { [weak self] line in self?.append(line) }
        displays.onChange = { [weak self] in
            self?.virtualDisplayActive = VirtualDisplayManager.shared.displayID != nil
            self?.updateDisplayPower()
        }
        displays.startObserving()
        displays.reconcile()
        updateDisplayPower()
        if DisplayPower.isScreenLocked { append("⚠︎ the screen is locked; nothing can be streamed until it is unlocked") }
        tailscale = TailscaleInfo.current()
        if let ts = tailscale, !ts.online {
            tailnetWarning = "Tailscale is installed but this Mac is not online on the tailnet."
        } else if tailscale == nil {
            tailnetWarning = "Tailscale not detected. Binding to loopback only; no remote device can reach this server."
        } else {
            tailnetWarning = nil
        }

        let identity: SecIdentity
        do {
            // Generated into Application Support on first launch; nothing bundled.
            identity = try ServerIdentity.loadOrCreate()
        } catch {
            append("TLS identity unavailable: \(error.localizedDescription)")
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
        deviceOf.removeAll()
        streamingPID.removeAll()
        DisplayPower.keepAwake(false)
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
                                    pairingStore: pairingStore,
                                    serverName: serverName)
        let id = session.id
        session.onLog = { [weak self] line in Task { @MainActor in self?.append(line) } }
        session.onStateChange = { [weak self] state in
            Task { @MainActor in
                self?.clientState = state
                self?.append("client \(id.uuidString.prefix(4)): \(state)")
            }
        }
        session.coordinator = self
        session.onAuthenticated = { [weak self] in
            Task { @MainActor in
                guard let self else { return }
                self.refreshPairedCount()
                self.sessionDidAuthenticate(id, deviceID: session.deviceID)
                // Per-device cap: refuse beyond the limit (PROTOCOL.md §13).
                if self.authenticatedSessions(deviceID: session.deviceID, excluding: id) >= self.maxSessionsPerDevice {
                    self.append("session cap (\(self.maxSessionsPerDevice)) reached for a device; refusing")
                    session.reject("too many sessions from this device")
                }
            }
        }
        session.onStats = { [weak self] stats in Task { @MainActor in self?.stats = stats } }
        session.onClose = { [weak self] in
            Task { @MainActor in
                guard let self else { return }
                let device = self.deviceOf[id]
                let wasStreaming = self.streamingPID[id] != nil
                self.sessions[id] = nil
                self.deviceOf[id] = nil
                self.streamingPID[id] = nil
                // A stream ended: refresh the device's other sessions' app lists.
                if wasStreaming, let device {
                    for (otherId, other) in self.sessions where self.deviceOf[otherId] == device && other.isAuthenticated {
                        other.resendAppList()
                    }
                }
                if self.sessions.isEmpty { self.clientState = nil; self.stats = nil }
                // With nobody left, the display may sleep again (unless headless).
                self.updateDisplayPower()
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

// MARK: - Multi-session coordination (PROTOCOL.md §13)

extension ServerModel: SessionCoordinator {
    func streamedPIDs(excluding sessionID: UUID) -> Set<pid_t> {
        Set(streamingPID.filter { $0.key != sessionID }.values)
    }

    func authenticatedSessions(deviceID: String, excluding sessionID: UUID) -> Int {
        deviceOf.filter { $0.key != sessionID && $0.value == deviceID }.count
    }

    func sessionDidAuthenticate(_ sessionID: UUID, deviceID: String) {
        deviceOf[sessionID] = deviceID
        // A sleeping display cannot be captured: wake it, and keep it on while
        // anyone is connected.
        DisplayPower.wake()
        updateDisplayPower()
        // A new device takes over the host; sessions from the same device stack.
        for (otherId, other) in sessions where otherId != sessionID && other.isAuthenticated {
            // Say why before closing, so that device does not reconnect and
            // start a tug of war with this one.
            if let otherDevice = deviceOf[otherId], otherDevice != deviceID { other.reject(sessionTakenOverMessage) }
        }
        // The same device connecting again is often a phone coming back from
        // being suspended: check its earlier sessions and drop any that no
        // longer answer, so they stop holding an app and a session slot.
        for (otherId, other) in sessions where otherId != sessionID && other.isAuthenticated && deviceOf[otherId] == deviceID {
            Task { @MainActor in
                guard await !other.isResponsive(within: 2) else { return }
                self.append("session \(otherId.uuidString.prefix(4)) stopped answering; closing it")
                other.close()
            }
        }
    }

    func reclaim(pid: pid_t, for sessionID: UUID, deviceID: String) async -> Bool {
        guard let holderID = streamingPID.first(where: { $0.key != sessionID && $0.value == pid })?.key else { return true }
        guard deviceOf[holderID] == deviceID, let holder = sessions[holderID] else { return false }
        if await holder.isResponsive(within: 2) { return false }
        append("session \(holderID.uuidString.prefix(4)) stopped answering; its app goes to the device's new session")
        streamingPID[holderID] = nil
        holder.close()
        return true
    }

    func sessionStreaming(_ sessionID: UUID, deviceID: String, pid: pid_t?) {
        streamingPID[sessionID] = pid
        // Refresh the device's other sessions so their `streamed` flags update.
        for (otherId, other) in sessions where otherId != sessionID && deviceOf[otherId] == deviceID && other.isAuthenticated {
            other.resendAppList()
        }
    }
}
