import SwiftUI
import RemoteAppProtocol

struct RootView: View {
    @EnvironmentObject var model: ClientModel

    var body: some View {
        ZStack {
            switch model.phase {
            case .browsing, .connecting:
                NavigationStack { DiscoveryView() }
            case .pairing(let name, let error):
                NavigationStack { PairingView(serverName: name, error: error) }
            case .apps:
                NavigationStack { AppListView() }
            case .streaming:
                StreamingScreen()
            }
        }
        .animation(.default, value: model.phase)
        .alert("Error", isPresented: Binding(get: { model.errorMessage != nil }, set: { if !$0 { model.errorMessage = nil } })) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(model.errorMessage ?? "")
        }
    }
}

struct DiscoveryView: View {
    @EnvironmentObject var model: ClientModel
    @AppStorage("lastHost") private var host = ""
    @FocusState private var hostFocused: Bool

    var body: some View {
        List {
            Section {
                Picker("Transport", selection: $model.transport) {
                    ForEach(Transport.Kind.allCases) { Text($0.displayName).tag($0) }
                }
                .pickerStyle(.segmented)
            } footer: {
                Text("Must match the transport selected in the Mac server.")
            }
            Section {
                HStack {
                    TextField("my-mac.tailnet.ts.net or 100.x.y.z", text: $host)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .keyboardType(.asciiCapable)
                        .focused($hostFocused)
                        .onSubmit { connectManual() }
                    Button("Connect") { connectManual() }
                        .disabled(host.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            } header: {
                Text("Connect over Tailscale")
            } footer: {
                Text("Enter the MagicDNS name or tailnet IP shown in RemoteApp Server. The server is reachable only over your tailnet.")
            }
            Section("Macs on this network (Bonjour, optional)") {
                if model.servers.isEmpty {
                    HStack {
                        ProgressView()
                        Text("Looking for RemoteApp servers…").foregroundStyle(.secondary)
                    }
                }
                ForEach(model.servers) { server in
                    Button {
                        model.choose(server)
                    } label: {
                        Label(server.name, systemImage: "desktopcomputer")
                    }
                }
            }
            if case .connecting(let name) = model.phase {
                Section { HStack { ProgressView(); Text("Connecting to \(name)…") } }
            }
        }
        .navigationTitle("RemoteApp")
    }

    private func connectManual() {
        hostFocused = false
        model.chooseManual(host: host)
    }
}

struct PairingView: View {
    @EnvironmentObject var model: ClientModel
    let serverName: String
    let error: String?
    @State private var code = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(spacing: 24) {
            Text("Enter the pairing code shown in RemoteApp Server on “\(serverName)”.")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
            TextField("000000", text: $code)
                .keyboardType(.numberPad)
                .font(.system(size: 40, weight: .semibold, design: .monospaced))
                .multilineTextAlignment(.center)
                .focused($focused)
                .onChange(of: code) { _, new in
                    code = String(new.filter(\.isNumber).prefix(6))
                    if code.count == 6 { model.pair(code: code) }
                }
            if let error {
                Text(error).foregroundStyle(.red)
            }
            Button("Connect") { model.pair(code: code) }
                .buttonStyle(.borderedProminent)
                .disabled(code.count != 6)
        }
        .padding()
        .navigationTitle("Pair")
        .toolbar { ToolbarItem(placement: .cancellationAction) { Button("Cancel") { model.disconnect() } } }
        .onAppear { focused = true }
    }
}

struct AppListView: View {
    @EnvironmentObject var model: ClientModel

    var body: some View {
        List(model.apps) { app in
            Button { model.select(app) } label: {
                HStack(spacing: 12) {
                    if let data = app.iconPNG, let image = UIImage(data: data) {
                        Image(uiImage: image).resizable().frame(width: 36, height: 36)
                    } else {
                        Image(systemName: "macwindow").frame(width: 36, height: 36)
                    }
                    VStack(alignment: .leading) {
                        Text(app.name)
                        Text("\(app.windowCount) window\(app.windowCount == 1 ? "" : "s") · \(app.bundleIdentifier)")
                            .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                }
            }
            .tint(.primary)
        }
        .navigationTitle(model.serverName)
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("Disconnect") { model.disconnect() } }
            ToolbarItem(placement: .primaryAction) { Button { model.refreshApps() } label: { Image(systemName: "arrow.clockwise") } }
        }
        .refreshable { model.refreshApps() }
    }
}

struct StreamingScreen: View {
    @EnvironmentObject var model: ClientModel

    var body: some View {
        ZStack(alignment: .top) {
            // Ignore the notch/home-indicator but NOT the keyboard, so the video
            // area shrinks to sit above the keyboard when it is up.
            StreamView().ignoresSafeArea(.container, edges: .all)
            if model.hudVisible {
                HStack(spacing: 12) {
                    Button { model.backToApps() } label: { Image(systemName: "chevron.left") }
                    Button { model.keyboardVisible.toggle() } label: {
                        Image(systemName: model.keyboardVisible ? "keyboard.chevron.compact.down" : "keyboard")
                    }
                    Spacer()
                    VStack(alignment: .trailing, spacing: 0) {
                        Text(String(format: "%.0f ms · %.0f fps", model.latencyMs, model.fps)).monospacedDigit()
                        if let g = model.geometry {
                            Text("\(Int(g.width))×\(Int(g.height)) pt").font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    Button { model.hudVisible = false } label: { Image(systemName: "eye.slash") }
                }
                .font(.footnote)
                .padding(.horizontal, 12)
                .padding(.vertical, 6)
                .background(.black.opacity(0.55), in: Capsule())
                .padding(.top, 8)
                .padding(.horizontal, 12)
                .transition(.move(edge: .top).combined(with: .opacity))
            }
            if !model.hudVisible {
                // Hint persists so the controls are never unreachable.
                Text("Two-finger tap for controls")
                    .font(.caption2).foregroundStyle(.white.opacity(0.5))
                    .padding(.top, 10)
                    .allowsHitTesting(false)
            }
        }
        .animation(.easeInOut(duration: 0.2), value: model.hudVisible)
        .statusBarHidden(true)
        .persistentSystemOverlays(.hidden)
    }
}
