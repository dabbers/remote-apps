// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import AppKit
import Foundation
import Network
import ScreenCaptureKit
import CoreMedia
import RemoteAppProtocol

/// One connected client: auth, app selection, capture → encode → send, input.
final class ClientSession {
    struct Stats {
        var fps: Double = 0
        var kbps: Double = 0
        var droppedFrames: Int = 0
    }

    let id = UUID()
    var onLog: ((String) -> Void)?
    var onStateChange: ((String) -> Void)?
    var onStats: ((Stats) -> Void)?
    var onClose: (() -> Void)?
    var onAuthenticated: (() -> Void)?

    private let connection: MessageConnection
    private let authenticator: Authenticator
    private let pairingStore: PairingStore
    private let serverName: String
    private let frameRate = 60
    private let bitrate = 12_000_000

    private(set) var isAuthenticated = false
    private var clientName = "client"
    private var clientScreen = ScreenSize(width: 390, height: 844, scale: 3)

    private var capture: WindowCapture?
    private var encoder: H264Encoder?
    private var injector: InputInjector?
    private var window: AXUIElement?
    private var display: SCDisplay?
    private var scale: CGFloat = 2
    private var windowFrame: CGRect = .zero
    private var geometryTimer: DispatchSourceTimer?
    private let pipelineQueue = DispatchQueue(label: "remoteapp.pipeline", qos: .userInteractive)

    /// The app currently being streamed, and capture-restart bookkeeping.
    private var currentPID: pid_t?
    private var isRestarting = false
    private var restartAttempts = 0
    private let maxRestartAttempts = 12

    private var inFlight = 0
    private let maxInFlight = 4
    private var statFrames = 0
    private var statBytes = 0
    private var statDropped = 0
    private var statStart = Date()

    init(connection: NWConnection, authenticator: Authenticator, pairingStore: PairingStore, serverName: String) {
        self.connection = MessageConnection(connection: connection)
        self.authenticator = authenticator
        self.pairingStore = pairingStore
        self.serverName = serverName
    }

    func start() {
        connection.onMessage = { [weak self] message in self?.handle(message) }
        connection.onStateChange = { [weak self] state in
            self?.onStateChange?("\(state)")
        }
        connection.onClose = { [weak self] error in
            self?.log("connection closed\(error.map { ": \($0)" } ?? "")")
            self?.teardown()
            self?.onClose?()
        }
        connection.start()
    }

    func close() {
        connection.cancel()
    }

    private func log(_ s: String) { onLog?("[\(clientName)] \(s)") }

    private func send(_ control: ControlMessage) {
        connection.send(control)
    }

    // MARK: Message handling

    private func handle(_ message: Message) {
        switch message {
        case .control(let control):
            handleControl(control)
        case .video:
            break // clients don't send video
        }
    }

    private func handleControl(_ control: ControlMessage) {
        if !isAuthenticated {
            if case .ping(let n) = control { send(.pong(n)); return }
            guard case .hello(let hello) = control else {
                send(.error(message: "hello required"))
                connection.cancel()
                return
            }
            clientName = hello.clientName
            guard hello.protocolVersion == protocolVersion else {
                send(.error(message: "protocol version mismatch"))
                connection.cancel()
                return
            }
            // A remembered device skips the code.
            if let token = hello.token, pairingStore.isValid(deviceID: hello.deviceID, token: token) {
                isAuthenticated = true
                onAuthenticated?()
                clientScreen = hello.screen
                log("authenticated by remembered pairing (\(hello.clientName))")
                Task { await self.sendAppList(asHelloAck: true, issuedToken: nil) }
                return
            }
            guard authenticator.verify(credential: hello.credential, clientName: hello.clientName) else {
                log("auth failed")
                send(.authFailed(reason: "wrong pairing code"))
                return
            }
            isAuthenticated = true
            onAuthenticated?()
            clientScreen = hello.screen
            // First pairing with the code: mint a token so this device is remembered.
            let issued = pairingStore.issue(deviceID: hello.deviceID, name: hello.clientName)
            log("paired \(hello.clientName); device will be remembered")
            Task { await self.sendAppList(asHelloAck: true, issuedToken: issued) }
            return
        }

        switch control {
        case .hello:
            break
        case .listApps:
            Task { await self.sendAppList(asHelloAck: false) }
        case .selectApp(let select):
            Task { await self.selectApp(pid: select.pid) }
        case .requestLaunchable:
            pipelineQueue.async { [weak self] in
                let apps = LaunchableApps.fetch()
                self?.send(.launchableApps(apps))
            }
        case .launchApp(let bundleID):
            Task { await self.launchApp(bundleIdentifier: bundleID) }
        case .screenSize(let size):
            clientScreen = size
            Task { await self.applyClientSize() }
        case .input(let event):
            injector?.handle(event)
        case .requestMenu:
            sendMenu()
        case .menuAction(let path):
            performMenuAction(path: path)
        case .requestClipboard:
            Task { @MainActor in self.send(.clipboard(ClipboardBridge.read())) }
        case .clipboard(let content):
            Task { @MainActor in
                ClipboardBridge.write(content)
                self.log("clipboard set from \(self.clientName) (\(content.items.count) item\(content.items.count == 1 ? "" : "s"))")
            }
        case .ping(let n):
            send(.pong(n))
        default:
            break
        }
    }

    // MARK: Menu bar

    private func sendMenu() {
        guard let pid = currentPID else { return }
        // AX reads can block briefly; keep them off the connection queue.
        pipelineQueue.async { [weak self] in
            let tree = MenuReader.read(pid: pid)
            self?.send(.menuTree(tree))
        }
    }

    private func performMenuAction(path: [Int]) {
        guard let pid = currentPID else { return }
        pipelineQueue.async { [weak self] in
            let ok = MenuReader.perform(pid: pid, path: path)
            self?.onLog?("menu action \(path) \(ok ? "performed" : "failed")")
            // Resend the tree so checkmarks / enabled state update after the action.
            self?.pipelineQueue.asyncAfter(deadline: .now() + 0.15) {
                self?.send(.menuTree(MenuReader.read(pid: pid)))
            }
        }
    }

    private func sendAppList(asHelloAck: Bool, issuedToken: String? = nil) async {
        do {
            let snapshot = try await ShareableApps.fetch()
            if asHelloAck {
                let hostScale = Double(NSScreen.main?.backingScaleFactor ?? 2)
                send(.helloAck(HelloAck(serverName: serverName, hostScale: hostScale, apps: snapshot.apps, issuedToken: issuedToken)))
            } else {
                send(.appList(snapshot.apps))
            }
        } catch {
            log("shareable content failed: \(error)")
            send(.error(message: "Screen Recording permission missing? \(error.localizedDescription)"))
        }
    }

    // MARK: Streaming

    /// Launches an installed app (or activates it if already running), waits for
    /// it to have a window, then streams it.
    private func launchApp(bundleIdentifier: String) async {
        guard let url = LaunchableApps.url(forBundleIdentifier: bundleIdentifier) else {
            send(.error(message: "app \(bundleIdentifier) not found"))
            return
        }
        let config = NSWorkspace.OpenConfiguration()
        config.activates = true
        do {
            let app = try await NSWorkspace.shared.openApplication(at: url, configuration: config)
            let pid = app.processIdentifier
            log("launching \(app.localizedName ?? bundleIdentifier)")
            // Wait for a main window to appear (new apps open one asynchronously).
            for _ in 0..<40 {   // up to ~8s
                if WindowControl.mainWindow(pid: pid) != nil { break }
                try await Task.sleep(nanoseconds: 200_000_000)
            }
            guard WindowControl.mainWindow(pid: pid) != nil else {
                send(.error(message: "\(app.localizedName ?? bundleIdentifier) launched but opened no window"))
                return
            }
            await selectApp(pid: pid)
        } catch {
            send(.error(message: "could not launch: \(error.localizedDescription)"))
        }
    }

    private func selectApp(pid: pid_t) async {
        teardown()
        do {
            let snapshot = try await ShareableApps.fetch()
            guard let scApp = snapshot.content.applications.first(where: { $0.processID == pid }) else {
                send(.error(message: "app \(pid) not found"))
                return
            }
            guard let axWindow = WindowControl.mainWindow(pid: pid) else {
                send(.error(message: "no main window for \(scApp.applicationName) (Accessibility permission missing?)"))
                return
            }
            NSRunningApplication(processIdentifier: pid)?.activate(options: [.activateIgnoringOtherApps])
            WindowControl.raise(axWindow)
            window = axWindow

            // Resize to the client's screen; the app may clamp.
            let origin = WindowControl.mainScreenContentOrigin()
            let requested = CGSize(width: clientScreen.width, height: clientScreen.height)
            guard let frame = WindowControl.setFrame(axWindow, origin: origin, size: requested) else {
                send(.error(message: "could not read window frame"))
                return
            }
            windowFrame = frame
            log("\(scApp.applicationName): requested \(Int(requested.width))×\(Int(requested.height)), got \(Int(frame.width))×\(Int(frame.height))")

            guard let display = snapshot.content.displays.first(where: { $0.frame.contains(CGPoint(x: frame.midX, y: frame.midY)) })
                    ?? snapshot.content.displays.first else {
                send(.error(message: "no display"))
                return
            }
            self.display = display
            scale = NSScreen.screens.first { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID) == display.displayID }?.backingScaleFactor ?? 2

            currentPID = pid
            restartAttempts = 0
            injector = InputInjector(pid: pid, windowFrame: frame)
            injector?.onLog = { [weak self] line in self?.onLog?(line) }
            try startPipeline(app: scApp, display: display, frame: frame)
            sendGeometry()
            sendMenu()
            startGeometryTimer()
        } catch {
            log("select failed: \(error)")
            send(.error(message: "capture failed: \(error.localizedDescription)"))
        }
    }

    private func localRect(_ frame: CGRect, in display: SCDisplay) -> CGRect {
        frame.offsetBy(dx: -display.frame.minX, dy: -display.frame.minY)
    }

    private func startPipeline(app: SCRunningApplication, display: SCDisplay, frame: CGRect) throws {
        let config = WindowCapture.Config(rect: localRect(frame, in: display), scale: scale, frameRate: frameRate)
        let px = config.pixelSize
        let encoder = try H264Encoder(width: Int(px.width), height: Int(px.height), frameRate: frameRate, bitrate: bitrate)
        encoder.onConfig = { [weak self] cfg in self?.send(.videoConfig(cfg)) }
        encoder.onFrame = { [weak self] frame in self?.sendVideo(frame) }
        self.encoder = encoder

        let capture = WindowCapture(app: app, display: display, config: config)
        capture.onFrame = { [weak self] pixelBuffer, pts in
            guard let self else { return }
            let wall = UInt64(Date().timeIntervalSince1970 * 1_000_000)
            self.pipelineQueue.async {
                if self.inFlight >= self.maxInFlight {
                    self.statDropped += 1
                    return
                }
                self.encoder?.encode(pixelBuffer, pts: pts, captureWallMicros: wall)
            }
        }
        capture.onError = { [weak self] error in
            // ScreenCaptureKit stops the stream on transient conditions — the
            // host display sleeping, the window briefly vanishing during a
            // resize, a display reconfiguration. Rebuild the pipeline instead of
            // giving up, so the stream self-heals without the client re-selecting.
            self?.log("capture stopped: \(error.localizedDescription); restarting")
            self?.scheduleRestart()
        }
        self.capture = capture
        statStart = Date()
        Task {
            do { try await capture.start() } catch {
                log("startCapture failed: \(error)")
                send(.error(message: "startCapture failed: \(error.localizedDescription)"))
            }
        }
    }

    private func sendVideo(_ frame: VideoFrame) {
        pipelineQueue.async {
            self.inFlight += 1
            self.statFrames += 1
            self.statBytes += frame.data.count
            self.connection.send(.video(frame)) { _ in
                self.pipelineQueue.async { self.inFlight -= 1 }
            }
            let elapsed = Date().timeIntervalSince(self.statStart)
            if elapsed >= 1 {
                let stats = Stats(fps: Double(self.statFrames) / elapsed,
                                  kbps: Double(self.statBytes * 8) / elapsed / 1000,
                                  droppedFrames: self.statDropped)
                self.statFrames = 0
                self.statBytes = 0
                self.statStart = Date()
                self.onStats?(stats)
            }
        }
    }

    private func sendGeometry() {
        let px = capture?.config.pixelSize ?? .zero
        send(.windowGeometry(WindowGeometry(width: windowFrame.width, height: windowFrame.height,
                                            pixelWidth: Int(px.width), pixelHeight: Int(px.height),
                                            windowTitle: window.flatMap(WindowControl.title(of:)))))
    }

    /// The client's screen changed (rotation, etc.): try to resize the host window again.
    private func applyClientSize() async {
        guard let window else { return }
        let origin = WindowControl.mainScreenContentOrigin()
        WindowControl.setFrame(window, origin: origin, size: CGSize(width: clientScreen.width, height: clientScreen.height))
        await refreshGeometry()
    }

    /// Follows the window if the user (or the app) moves or resizes it.
    private func startGeometryTimer() {
        let timer = DispatchSource.makeTimerSource(queue: pipelineQueue)
        timer.schedule(deadline: .now() + 0.5, repeating: 0.5)
        timer.setEventHandler { [weak self] in
            Task { await self?.refreshGeometry() }
        }
        timer.resume()
        geometryTimer = timer
    }

    // MARK: Capture auto-restart

    private func scheduleRestart() {
        pipelineQueue.async { [weak self] in
            guard let self, self.currentPID != nil, !self.isRestarting else { return }
            self.isRestarting = true
            self.restartAttempts += 1
            let attempt = self.restartAttempts
            // Back off a little between tries; a sleeping display can take a
            // few seconds to come back.
            let delay = min(0.5 * Double(attempt), 3.0)
            self.pipelineQueue.asyncAfter(deadline: .now() + delay) {
                Task { await self.restartCapture() }
            }
        }
    }

    private func restartCapture() async {
        guard let pid = currentPID else { isRestarting = false; return }
        let old = capture
        capture = nil
        await old?.stop()
        encoder?.invalidate()
        encoder = nil
        do {
            let snapshot = try await ShareableApps.fetch()
            guard let scApp = snapshot.content.applications.first(where: { $0.processID == pid }) else {
                throw NSError(domain: "RemoteApp", code: 1, userInfo: [NSLocalizedDescriptionKey: "app no longer running"])
            }
            guard let axWindow = WindowControl.mainWindow(pid: pid),
                  let frame = WindowControl.frame(of: axWindow) else {
                throw NSError(domain: "RemoteApp", code: 2, userInfo: [NSLocalizedDescriptionKey: "window not found"])
            }
            window = axWindow
            windowFrame = frame
            injector?.windowFrame = frame
            let display = self.display
                ?? snapshot.content.displays.first(where: { $0.frame.contains(CGPoint(x: frame.midX, y: frame.midY)) })
                ?? snapshot.content.displays.first
            guard let display else { throw NSError(domain: "RemoteApp", code: 3, userInfo: [NSLocalizedDescriptionKey: "no display"]) }
            self.display = display
            try startPipeline(app: scApp, display: display, frame: frame)
            sendGeometry()
            isRestarting = false
            restartAttempts = 0
            log("capture restarted")
        } catch {
            isRestarting = false
            if restartAttempts < maxRestartAttempts {
                scheduleRestart()
            } else {
                restartAttempts = 0
                log("giving up restart: \(error.localizedDescription)")
                send(.error(message: "capture stopped and could not be restarted: \(error.localizedDescription)"))
            }
        }
    }

    private func refreshGeometry() async {
        // Don't fight an in-progress restart.
        guard !isRestarting else { return }
        guard let window, let display, let capture, let frame = WindowControl.frame(of: window) else { return }
        guard frame != windowFrame else { return }
        let sizeChanged = frame.size != windowFrame.size
        windowFrame = frame
        injector?.windowFrame = frame
        if sizeChanged {
            // Pixel size changed: the encoder session is fixed-size, so rebuild it.
            await capture.stop()
            encoder?.invalidate()
            guard let app = try? await ShareableApps.fetch().content.applications.first(where: { $0.processID == injector?.pid }) else { return }
            try? startPipeline(app: app, display: display, frame: frame)
            log("window resized to \(Int(frame.width))×\(Int(frame.height))")
        } else {
            try? await capture.update(rect: localRect(frame, in: display))
        }
        sendGeometry()
    }

    private func teardown() {
        geometryTimer?.cancel()
        geometryTimer = nil
        currentPID = nil
        isRestarting = false
        restartAttempts = 0
        let capture = self.capture
        self.capture = nil
        Task { await capture?.stop() }
        encoder?.invalidate()
        encoder = nil
        injector = nil
        window = nil
    }
}
