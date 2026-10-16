import AppKit
import RemoteAppProtocol

/// Lists installed apps that can be launched, and resolves bundle identifiers
/// to app URLs for launching.
enum LaunchableApps {
    private static let directories = [
        "/Applications",
        "/Applications/Utilities",
        "/System/Applications",
        "/System/Applications/Utilities",
        NSHomeDirectory() + "/Applications",
    ]

    static func fetch() -> [LaunchableApp] {
        let fm = FileManager.default
        var seen = Set<String>()
        var apps: [LaunchableApp] = []
        for dir in directories {
            guard let entries = try? fm.contentsOfDirectory(atPath: dir) else { continue }
            for entry in entries where entry.hasSuffix(".app") {
                let path = dir + "/" + entry
                guard let bundle = Bundle(path: path),
                      let bid = bundle.bundleIdentifier,
                      !seen.contains(bid) else { continue }
                // Skip agents / UI-less helpers.
                if (bundle.infoDictionary?["LSUIElement"] as? Bool) == true { continue }
                if (bundle.infoDictionary?["LSBackgroundOnly"] as? Bool) == true { continue }
                seen.insert(bid)
                let name = fm.displayName(atPath: path).replacingOccurrences(of: ".app", with: "")
                apps.append(LaunchableApp(bundleIdentifier: bid, name: name, iconPNG: iconPNG(path: path)))
            }
        }
        return apps.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    static func url(forBundleIdentifier bid: String) -> URL? {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: bid)
    }

    private static func iconPNG(path: String, size: CGFloat = 48) -> Data? {
        let icon = NSWorkspace.shared.icon(forFile: path)
        let target = NSSize(width: size, height: size)
        let image = NSImage(size: target)
        image.lockFocus()
        icon.draw(in: NSRect(origin: .zero, size: target), from: .zero, operation: .copy, fraction: 1)
        image.unlockFocus()
        guard let tiff = image.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }
}
