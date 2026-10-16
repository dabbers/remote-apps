import SwiftUI
import RemoteAppProtocol

struct ContentView: View {
    @EnvironmentObject var model: ServerModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Pairing code").font(.caption).foregroundStyle(.secondary)
                HStack(alignment: .firstTextBaseline) {
                    Text(model.pairingCode).font(.system(size: 44, weight: .semibold, design: .monospaced))
                    Button("New code") { model.regenerateCode() }
                }
                HStack {
                    Text(model.pairedCount == 0 ? "No devices remembered"
                         : "\(model.pairedCount) device\(model.pairedCount == 1 ? "" : "s") remembered — they skip the code")
                        .font(.caption).foregroundStyle(.secondary)
                    if model.pairedCount > 0 {
                        Button("Forget") { model.forgetPairedDevices() }
                            .font(.caption)
                    }
                }
            }

            GroupBox("Permissions") {
                VStack(alignment: .leading, spacing: 6) {
                    permissionRow("Screen Recording", granted: model.screenRecordingGranted)
                    permissionRow("Accessibility", granted: model.accessibilityGranted)
                    HStack {
                        Button("Request…") { model.requestPermissions() }
                        Button("Re-check") { model.refreshPermissions() }
                        Spacer()
                        Text("Grant both in System Settings › Privacy & Security, then relaunch.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            GroupBox("Network") {
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text("On the client, connect to:")
                        Spacer()
                        Picker("Transport", selection: $model.transport) {
                            Text("QUIC").tag(Transport.Kind.quic)
                            Text("TLS/TCP").tag(Transport.Kind.tcpTLS)
                        }
                        .frame(width: 160)
                        .onChange(of: model.transport) { _, _ in model.start() }
                    }
                    Text("\(model.connectTarget) : \(String(model.port))")
                        .font(.system(.title3, design: .monospaced)).textSelection(.enabled)
                    if let ts = model.tailscale {
                        Label("Tailnet: \(ts.dnsName) (\(ts.ipv4))\(ts.online ? "" : " — offline")",
                              systemImage: ts.online ? "lock.shield.fill" : "exclamationmark.shield")
                            .font(.caption).foregroundStyle(ts.online ? .green : .orange)
                    }
                    if let warning = model.tailnetWarning {
                        Label(warning, systemImage: "exclamationmark.triangle.fill")
                            .font(.caption).foregroundStyle(.orange)
                    }
                    Text("Listener: \(model.listenerState)")
                        .font(.callout)
                    if !model.boundEndpoints.isEmpty {
                        Text("Bound to: " + model.boundEndpoints.joined(separator: ", "))
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    if let client = model.clientState {
                        Text("Client: \(client)").font(.callout)
                    } else {
                        Text("No client connected").font(.callout).foregroundStyle(.secondary)
                    }
                    if let stats = model.stats {
                        Text(String(format: "%.0f fps · %.0f kbit/s · %d dropped", stats.fps, stats.kbps, stats.droppedFrames))
                            .font(.callout.monospacedDigit())
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            GroupBox("Log") {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(alignment: .leading, spacing: 2) {
                            ForEach(Array(model.log.enumerated()), id: \.offset) { i, line in
                                Text(line).font(.system(.caption, design: .monospaced)).id(i)
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .onChange(of: model.log.count) { _, count in
                        proxy.scrollTo(count - 1, anchor: .bottom)
                    }
                }
                .frame(minHeight: 160)
            }
        }
        .padding()
        .textSelection(.enabled)
    }

    private func permissionRow(_ name: String, granted: Bool) -> some View {
        HStack {
            Image(systemName: granted ? "checkmark.circle.fill" : "xmark.circle.fill")
                .foregroundStyle(granted ? .green : .red)
            Text(name)
        }
    }
}
