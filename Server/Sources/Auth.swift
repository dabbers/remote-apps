import Foundation
import OpenDirectory

protocol Authenticator: Sendable {
    func verify(credential: String, clientName: String) -> Bool
}

/// Prototype auth: the client must send the 6-digit code shown in the server window.
struct PairingCodeAuthenticator: Authenticator {
    let code: String
    func verify(credential: String, clientName: String) -> Bool {
        credential.trimmingCharacters(in: .whitespaces) == code
    }
    static func randomCode() -> String {
        String(format: "%06d", Int.random(in: 0...999_999))
    }
}

/// Hook for real auth: verifies the credential as the password of a local
/// macOS user account via OpenDirectory. Not wired up by default; swap it in
/// for `PairingCodeAuthenticator` in `ServerModel`.
struct OpenDirectoryAuthenticator: Authenticator {
    let username: String
    func verify(credential: String, clientName: String) -> Bool {
        do {
            let session = ODSession.default()
            let node = try ODNode(session: session, type: ODNodeType(kODNodeTypeAuthentication))
            let record = try node.record(withRecordType: kODRecordTypeUsers, name: username, attributes: nil)
            try record.verifyPassword(credential)
            return true
        } catch {
            return false
        }
    }
}
