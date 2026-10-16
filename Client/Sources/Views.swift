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
    @State private var showingLaunch = false

    var body: some View {
        List {
            Section("Running") {
                ForEach(model.apps) { app in
                    Button { model.select(app) } label: {
                        HStack(spacing: 12) {
                            icon(app.iconPNG)
                            VStack(alignment: .leading) {
                                Text(app.name)
                                Text("\(app.windowCount) window\(app.windowCount == 1 ? "" : "s") · \(app.bundleIdentifier)")
                                    .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                            }
                        }
                    }
                    .tint(.primary)
                }
            }
        }
        .navigationTitle(model.serverName)
        .toolbar {
            ToolbarItem(placement: .cancellationAction) { Button("Disconnect") { model.disconnect() } }
            ToolbarItem(placement: .primaryAction) {
                Button {
                    model.requestLaunchable()
                    showingLaunch = true
                } label: { Image(systemName: "plus.app") }
            }
            ToolbarItem(placement: .primaryAction) { Button { model.refreshApps() } label: { Image(systemName: "arrow.clockwise") } }
        }
        .refreshable { model.refreshApps() }
        .sheet(isPresented: $showingLaunch) {
            LaunchAppsView { app in
                model.launchApp(app)
                showingLaunch = false
            }
            .environmentObject(model)
        }
    }

    @ViewBuilder
    private func icon(_ data: Data?) -> some View {
        if let data, let image = UIImage(data: data) {
            Image(uiImage: image).resizable().frame(width: 36, height: 36)
        } else {
            Image(systemName: "macwindow").frame(width: 36, height: 36)
        }
    }
}

/// Picker of installed apps to launch (read from the Mac on demand).
struct LaunchAppsView: View {
    @EnvironmentObject var model: ClientModel
    @Environment(\.dismiss) private var dismiss
    @State private var search = ""
    let onLaunch: (LaunchableApp) -> Void

    private var filtered: [LaunchableApp] {
        guard !search.isEmpty else { return model.launchable }
        return model.launchable.filter { $0.name.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        NavigationStack {
            Group {
                if model.launchable.isEmpty {
                    HStack { ProgressView(); Text("Loading apps…").foregroundStyle(.secondary) }
                } else {
                    List(filtered) { app in
                        Button { onLaunch(app) } label: {
                            HStack(spacing: 12) {
                                if let data = app.iconPNG, let image = UIImage(data: data) {
                                    Image(uiImage: image).resizable().frame(width: 32, height: 32)
                                } else {
                                    Image(systemName: "app").frame(width: 32, height: 32)
                                }
                                Text(app.name)
                            }
                        }
                        .tint(.primary)
                    }
                    .searchable(text: $search, placement: .navigationBarDrawer(displayMode: .always))
                }
            }
            .navigationTitle("Open an app")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .primaryAction) { Button { model.requestLaunchable() } label: { Image(systemName: "arrow.clockwise") } }
            }
        }
    }
}

struct StreamingScreen: View {
    @EnvironmentObject var model: ClientModel
    @State private var showingMenu = false

    var body: some View {
        ZStack(alignment: .top) {
            // Ignore the notch/home-indicator but NOT the keyboard, so the video
            // area shrinks to sit above the keyboard when it is up.
            StreamView().ignoresSafeArea(.container, edges: .all)
            if model.geometry == nil {
                VStack(spacing: 10) {
                    ProgressView().tint(.white)
                    Text("Starting…").foregroundStyle(.white.opacity(0.7)).font(.callout)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .allowsHitTesting(false)
            }
            if model.hudVisible {
                HStack(spacing: 12) {
                    Button { model.backToApps() } label: { Image(systemName: "chevron.left") }
                    Button {
                        model.requestMenu()
                        showingMenu = true
                    } label: { Image(systemName: "menubar.rectangle") }
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
        .sheet(isPresented: $showingMenu) {
            MenuSheet(onAct: { path in
                model.performMenu(path: path)
                showingMenu = false
            }, onRefresh: { model.requestMenu() })
            .environmentObject(model)
        }
    }
}

/// Native rendering of the host app's menu bar (read via Accessibility).
struct MenuSheet: View {
    @EnvironmentObject var model: ClientModel
    @Environment(\.dismiss) private var dismiss
    let onAct: ([Int]) -> Void
    let onRefresh: () -> Void

    var body: some View {
        NavigationStack {
            Group {
                if model.menu.isEmpty {
                    ContentUnavailableView("No menu", systemImage: "menubar.rectangle",
                                           description: Text("The app reported no menu bar, or Accessibility is not granted."))
                } else {
                    MenuLevel(nodes: model.menu, prefix: [], onAct: onAct)
                }
            }
            .navigationTitle("Menu")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Done") { dismiss() } }
                ToolbarItem(placement: .primaryAction) { Button { onRefresh() } label: { Image(systemName: "arrow.clockwise") } }
            }
        }
        .presentationDetents([.medium, .large])
    }
}

/// One level of the menu hierarchy; submenus push a deeper level.
struct MenuLevel: View {
    let nodes: [MenuNode]
    let prefix: [Int]
    let onAct: ([Int]) -> Void

    var body: some View {
        List {
            ForEach(Array(nodes.enumerated()), id: \.offset) { index, node in
                let path = prefix + [index]
                if node.isSeparator {
                    Divider()
                } else if node.hasSubmenu {
                    NavigationLink {
                        MenuLevel(nodes: node.children, prefix: path, onAct: onAct)
                            .navigationTitle(node.title)
                            .navigationBarTitleDisplayMode(.inline)
                    } label: {
                        row(node)
                    }
                    .disabled(!node.enabled)
                } else {
                    Button { onAct(path) } label: { row(node) }
                        .disabled(!node.enabled)
                }
            }
        }
        .listStyle(.plain)
    }

    private func row(_ node: MenuNode) -> some View {
        HStack {
            Image(systemName: "checkmark")
                .opacity(node.checked ? 1 : 0)
                .font(.caption)
                .frame(width: 14)
            Text(node.title)
                .foregroundStyle(node.enabled ? .primary : .secondary)
            Spacer()
            if let shortcut = node.shortcut {
                Text(shortcut)
                    .font(.callout.monospaced())
                    .foregroundStyle(.secondary)
            }
        }
    }
}
