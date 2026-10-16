// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import SwiftUI

@main
struct RemoteAppServerApp: App {
    @StateObject private var model = ServerModel()

    var body: some Scene {
        WindowGroup("RemoteApp Server") {
            ContentView()
                .environmentObject(model)
                .frame(minWidth: 460, minHeight: 520)
                .onAppear {
                    model.start()
                    // Trigger the system prompts on first launch; both grants are
                    // toggled by the user in System Settings › Privacy & Security.
                    model.requestPermissions()
                }
        }
        .windowResizability(.contentSize)
    }
}
