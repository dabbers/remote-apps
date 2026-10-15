import SwiftUI

@main
struct RemoteAppClientApp: App {
    @StateObject private var model = ClientModel()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(model)
                .preferredColorScheme(.dark)
        }
    }
}
