import Foundation
import Darwin

/// Reads this Mac's tailnet identity from the Tailscale local API so the server
/// can bind to the tailnet address and advertise its MagicDNS name. This is the
/// GUI Tailscale app's bundled CLI; `tailscale status --json` talks to the local
/// daemon over its unix socket. No tailnet traffic leaves the machine to get this.
struct TailscaleInfo {
    var ipv4: String
    var ipv6: String?
    /// MagicDNS name, e.g. "my-mac.tailnet-name.ts.net" (trailing dot stripped).
    var dnsName: String
    var hostName: String
    var online: Bool

    /// Candidate paths for the CLI: GUI app bundle first, then Homebrew / standalone.
    static let cliPaths = [
        "/Applications/Tailscale.app/Contents/MacOS/Tailscale",
        "/opt/homebrew/bin/tailscale",
        "/usr/local/bin/tailscale",
        "/usr/bin/tailscale",
    ]

    static func cliPath() -> String? {
        cliPaths.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    /// Returns nil if Tailscale is not installed or the daemon is not reachable.
    static func current() -> TailscaleInfo? {
        guard let cli = cliPath() else { return nil }
        guard let data = run(cli, ["status", "--json"]) else { return nil }
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let selfNode = root["Self"] as? [String: Any],
              let ips = selfNode["TailscaleIPs"] as? [String] else { return nil }
        let v4 = ips.first { !$0.contains(":") }
        let v6 = ips.first { $0.contains(":") }
        guard let v4 else { return nil }
        let dns = (selfNode["DNSName"] as? String ?? "").trimmingCharacters(in: CharacterSet(charactersIn: "."))
        return TailscaleInfo(ipv4: v4,
                             ipv6: v6,
                             dnsName: dns.isEmpty ? v4 : dns,
                             hostName: selfNode["HostName"] as? String ?? "Mac",
                             online: selfNode["Online"] as? Bool ?? false)
    }

    /// Name of the network interface that carries a given IPv4 address
    /// (e.g. "utun4" for the Tailscale address), via getifaddrs.
    static func interfaceName(forIPv4 ip: String) -> String? {
        var ifaddrPtr: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifaddrPtr) == 0 else { return nil }
        defer { freeifaddrs(ifaddrPtr) }
        var ptr = ifaddrPtr
        while let cur = ptr {
            let ifa = cur.pointee
            if let addr = ifa.ifa_addr, addr.pointee.sa_family == UInt8(AF_INET) {
                var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                if getnameinfo(addr, socklen_t(addr.pointee.sa_len), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0,
                   String(cString: host) == ip {
                    return String(cString: ifa.ifa_name)
                }
            }
            ptr = ifa.ifa_next
        }
        return nil
    }

    private static func run(_ path: String, _ args: [String]) -> Data? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = args
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
        } catch {
            return nil
        }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return process.terminationStatus == 0 ? data : nil
    }
}
