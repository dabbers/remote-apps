// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import UIKit
import RemoteAppProtocol

/// App delegate. On Mac (Catalyst) it builds the menu bar so it mirrors the
/// streamed app's menus, plus a "Remote" menu to manage the connection.
final class AppDelegate: UIResponder, UIApplicationDelegate {
    func application(_ application: UIApplication,
                     didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]? = nil) -> Bool {
        true
    }

    override func buildMenu(with builder: UIMenuBuilder) {
        super.buildMenu(with: builder)
        #if targetEnvironment(macCatalyst)
        guard builder.system == .main else { return }
        let model = ClientModel.shared

        // Controls to manage the remote session, independent of the streamed app.
        // Derive the menu identifier from the bundle ID so it carries no literal
        // bundle prefix (which is configured, not hardcoded).
        let remoteID = UIMenu.Identifier((Bundle.main.bundleIdentifier ?? "remoteapp") + ".remote")
        let remote = UIMenu(title: "Remote", identifier: remoteID, children: [
            UIKeyCommand(title: "Open an App…", action: #selector(remoteOpenApps),
                         input: "o", modifierFlags: [.command, .shift]),
            UIKeyCommand(title: "Back to App List", action: #selector(remoteBackToApps),
                         input: "l", modifierFlags: [.command, .shift]),
            UIKeyCommand(title: "Show/Hide Controls", action: #selector(remoteToggleHUD),
                         input: ".", modifierFlags: [.command]),
            UIMenu(title: "", options: .displayInline, children: [
                UIKeyCommand(title: "Disconnect", action: #selector(remoteDisconnect),
                             input: "d", modifierFlags: [.command, .shift]),
            ]),
        ])
        builder.insertSibling(remote, afterMenu: .application)

        // Reproduce the streamed app's menu bar, replacing the standard menus.
        guard !model.menu.isEmpty else { return }
        // Replace the standard editing menus with the streamed app's. Window and
        // Help are system-managed on macOS and re-added automatically, so we keep
        // those native and skip the streamed app's equivalents to avoid duplicates.
        for standard: UIMenu.Identifier in [.file, .edit, .format, .view] {
            builder.remove(menu: standard)
        }
        let menus = model.menu.enumerated().compactMap { index, node -> UIMenu? in
            guard !node.isSeparator else { return nil }
            let title = node.title.lowercased()
            guard title != "window", title != "help" else { return nil }
            return Self.menu(for: node, path: [index])
        }
        // Each insert goes immediately after Remote, so reverse to keep order.
        for menu in menus.reversed() {
            builder.insertSibling(menu, afterMenu: remoteID)
        }
        #endif
    }

    #if targetEnvironment(macCatalyst)
    /// Build a UIMenu tree from a MenuNode; separators become inline sections.
    private static func menu(for node: MenuNode, path: [Int]) -> UIMenu {
        var sections: [UIMenuElement] = []
        var group: [UIMenuElement] = []
        func flush() {
            if !group.isEmpty {
                sections.append(UIMenu(title: "", options: .displayInline, children: group))
                group = []
            }
        }
        for (i, child) in node.children.enumerated() {
            let childPath = path + [i]
            if child.isSeparator {
                flush()
            } else if child.hasSubmenu {
                group.append(menu(for: child, path: childPath))
            } else {
                let action = UIAction(title: child.title.isEmpty ? " " : child.title,
                                      state: child.checked ? .on : .off) { _ in
                    ClientModel.shared.performMenu(path: childPath)
                }
                if !child.enabled { action.attributes = [.disabled] }
                group.append(action)
            }
        }
        flush()
        return UIMenu(title: node.title, children: sections)
    }

    @objc private func remoteOpenApps() {
        ClientModel.shared.requestLaunchable()
        ClientModel.shared.showLaunchSheet = true
    }
    @objc private func remoteBackToApps() { ClientModel.shared.backToApps() }
    @objc private func remoteToggleHUD() { ClientModel.shared.hudVisible.toggle() }
    @objc private func remoteDisconnect() { ClientModel.shared.disconnect() }

    // Enable the Remote commands only while connected.
    override func canPerformAction(_ action: Selector, withSender sender: Any?) -> Bool {
        switch action {
        case #selector(remoteOpenApps), #selector(remoteBackToApps),
             #selector(remoteToggleHUD), #selector(remoteDisconnect):
            return ClientModel.shared.isConnected
        default:
            return super.canPerformAction(action, withSender: sender)
        }
    }
    #endif
}
