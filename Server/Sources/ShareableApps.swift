import AppKit
import ScreenCaptureKit
import RemoteAppProtocol

/// Enumerates apps that have at least one normal on-screen window.
enum ShareableApps {
    struct Snapshot {
        let content: SCShareableContent
        let apps: [AppInfo]
    }

    static func fetch() async throws -> Snapshot {
        let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
        let myPid = ProcessInfo.processInfo.processIdentifier
        var windowCount: [pid_t: Int] = [:]
        for window in content.windows where window.windowLayer == 0 && window.frame.width >= 100 && window.frame.height >= 60 {
            guard let pid = window.owningApplication?.processID else { continue }
            windowCount[pid, default: 0] += 1
        }
        let apps = content.applications
            .filter { $0.processID != myPid && (windowCount[$0.processID] ?? 0) > 0 }
            // System dialogs (e.g. the Accessibility permission alert) are not streamable apps.
            .filter { !$0.bundleIdentifier.hasPrefix("com.apple.accessibility.") }
            .map { app in
                AppInfo(pid: app.processID,
                        bundleIdentifier: app.bundleIdentifier,
                        name: app.applicationName.isEmpty ? app.bundleIdentifier : app.applicationName,
                        windowCount: windowCount[app.processID] ?? 0,
                        iconPNG: iconPNG(pid: app.processID))
            }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        return Snapshot(content: content, apps: apps)
    }

    private static func iconPNG(pid: pid_t, size: CGFloat = 48) -> Data? {
        guard let icon = NSRunningApplication(processIdentifier: pid)?.icon else { return nil }
        let target = NSSize(width: size, height: size)
        let image = NSImage(size: target)
        image.lockFocus()
        icon.draw(in: NSRect(origin: .zero, size: target), from: .zero, operation: .copy, fraction: 1)
        image.unlockFocus()
        guard let tiff = image.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }
}
