// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import Foundation
import Security

/// Remembers which devices have paired so they skip the code next time. One
/// long-lived random token per device, persisted to Application Support.
final class PairingStore {
    struct Device: Codable {
        var token: String
        var name: String
        var paired: Date
        var lastSeen: Date
    }

    private let url: URL
    private let queue = DispatchQueue(label: "remoteapp.pairings")
    private var devices: [String: Device] = [:]   // deviceID → Device

    init() {
        let dir = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("RemoteAppServer", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        url = dir.appendingPathComponent("pairings.json")
        if let data = try? Data(contentsOf: url),
           let decoded = try? JSONDecoder().decode([String: Device].self, from: data) {
            devices = decoded
        }
    }

    private func save() {
        if let data = try? JSONEncoder().encode(devices) { try? data.write(to: url) }
    }

    /// True if this device presented a token we issued it.
    func isValid(deviceID: String, token: String) -> Bool {
        queue.sync {
            guard !token.isEmpty, let d = devices[deviceID], d.token == token else { return false }
            devices[deviceID]?.lastSeen = Date()
            save()
            return true
        }
    }

    /// Mint and store a token after a successful code pairing (replaces any
    /// previous token for the same device).
    func issue(deviceID: String, name: String) -> String {
        queue.sync {
            let token = Self.randomToken()
            devices[deviceID] = Device(token: token, name: name, paired: Date(), lastSeen: Date())
            save()
            return token
        }
    }

    func forgetAll() { queue.sync { devices.removeAll(); save() } }

    var pairedCount: Int { queue.sync { devices.count } }
    var pairedNames: [String] { queue.sync { devices.values.map(\.name).sorted() } }

    private static func randomToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64EncodedString()
    }
}
