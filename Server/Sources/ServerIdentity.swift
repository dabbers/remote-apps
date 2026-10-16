// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import Foundation
import Security
import RemoteAppProtocol

/// Provides the server's TLS identity. On first launch it generates a
/// self-signed certificate into Application Support — nothing is bundled in the
/// app or committed to the repo — so a fresh checkout (or a published build)
/// carries no private key. The certificate is only ever used over the tailnet,
/// where WireGuard is the real trust boundary; the client does not verify it.
enum ServerIdentity {
    private static var directory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return base.appendingPathComponent("RemoteAppServer", isDirectory: true)
    }
    private static var p12URL: URL { directory.appendingPathComponent("server.p12") }
    /// A per-install random passphrase for the local-only .p12, stored beside it
    /// (both files mode 0600). Keeping no passphrase in source means there is no
    /// secret to scrub for a public release.
    private static var passURL: URL { directory.appendingPathComponent("server.p12.pass") }

    /// Loads the stored identity, generating one on first run.
    static func loadOrCreate() throws -> SecIdentity {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        if let identity = try? loadExisting() { return identity }
        try generate()
        return try loadExisting()
    }

    private static func loadExisting() throws -> SecIdentity {
        let data = try Data(contentsOf: p12URL)
        let passphrase = try String(contentsOf: passURL, encoding: .utf8)
        return try Transport.loadIdentity(p12: data, passphrase: passphrase)
    }

    /// Runs `openssl` to make a self-signed cert + key, exported as PKCS#12 with
    /// a fresh random passphrase. Same recipe the old Scripts/gen-cert.sh used.
    private static func generate() throws {
        let passphrase = UUID().uuidString
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: tmp, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tmp) }
        let key = tmp.appendingPathComponent("key.pem")
        let cert = tmp.appendingPathComponent("cert.pem")

        try run(["req", "-x509", "-newkey", "rsa:2048", "-sha256", "-days", "3650", "-nodes",
                 "-keyout", key.path, "-out", cert.path,
                 "-subj", "/CN=RemoteApp Server/O=RemoteApp",
                 "-addext", "subjectAltName=DNS:remoteapp.local"])
        try run(["pkcs12", "-export", "-inkey", key.path, "-in", cert.path,
                 "-name", "RemoteApp Server", "-passout", "pass:\(passphrase)",
                 "-keypbe", "PBE-SHA1-3DES", "-certpbe", "PBE-SHA1-3DES", "-macalg", "sha1",
                 "-out", p12URL.path])
        try passphrase.write(to: passURL, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: p12URL.path)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: passURL.path)
    }

    private static func run(_ args: [String]) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/openssl")
        p.arguments = args
        let errPipe = Pipe()
        p.standardError = errPipe
        p.standardOutput = Pipe()
        try p.run()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else {
            let err = String(data: errPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
            throw NSError(domain: "RemoteApp", code: Int(p.terminationStatus),
                          userInfo: [NSLocalizedDescriptionKey: "openssl \(args.first ?? "") failed: \(err)"])
        }
    }
}
