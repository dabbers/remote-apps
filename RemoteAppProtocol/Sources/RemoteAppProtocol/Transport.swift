import Foundation
import Network
import Security

/// NWParameters factories shared by server and client.
///
/// QUIC is the primary transport (one bidirectional stream per session, TLS 1.3
/// built in). The TCP+TLS variant exists as a fallback and uses the same
/// identity/verification hooks.
public enum Transport {
    public enum Kind: String, Sendable, CaseIterable, Identifiable {
        case quic, tcpTLS
        public var id: String { rawValue }
        /// Bonjour service type: QUIC is UDP-based, the TLS fallback is TCP-based.
        public var serviceType: String { self == .quic ? "_remoteapp._udp" : "_remoteapp._tcp" }
        public var displayName: String { self == .quic ? "QUIC" : "TLS/TCP" }
    }

    /// Server-side parameters. `identity` is the TLS server identity (a
    /// self-signed certificate is fine for the prototype; the client pins or
    /// skips verification).
    public static func serverParameters(kind: Kind, identity: SecIdentity) -> NWParameters {
        guard let secIdentity = sec_identity_create(identity) else {
            preconditionFailure("sec_identity_create failed")
        }
        switch kind {
        case .quic:
            let quic = NWProtocolQUIC.Options(alpn: [alpnProtocol])
            quic.direction = .bidirectional
            quic.idleTimeout = 30_000
            sec_protocol_options_set_local_identity(quic.securityProtocolOptions, secIdentity)
            sec_protocol_options_set_min_tls_protocol_version(quic.securityProtocolOptions, .TLSv13)
            let params = NWParameters(quic: quic)
            params.includePeerToPeer = true
            return params
        case .tcpTLS:
            let tls = NWProtocolTLS.Options()
            sec_protocol_options_set_local_identity(tls.securityProtocolOptions, secIdentity)
            sec_protocol_options_add_tls_application_protocol(tls.securityProtocolOptions, alpnProtocol)
            let tcp = NWProtocolTCP.Options()
            tcp.noDelay = true
            let params = NWParameters(tls: tls, tcp: tcp)
            params.includePeerToPeer = true
            return params
        }
    }

    /// Client-side parameters. The prototype trusts any server certificate;
    /// `verify` lets a real client pin the host's certificate fingerprint.
    public static func clientParameters(kind: Kind, verify: @escaping (sec_protocol_metadata_t, sec_trust_t) -> Bool = { _, _ in true }) -> NWParameters {
        let queue = DispatchQueue(label: "remoteapp.tls.verify")
        switch kind {
        case .quic:
            let quic = NWProtocolQUIC.Options(alpn: [alpnProtocol])
            quic.direction = .bidirectional
            quic.idleTimeout = 30_000
            sec_protocol_options_set_verify_block(quic.securityProtocolOptions, { metadata, trust, complete in
                complete(verify(metadata, trust))
            }, queue)
            let params = NWParameters(quic: quic)
            params.includePeerToPeer = true
            return params
        case .tcpTLS:
            let tls = NWProtocolTLS.Options()
            sec_protocol_options_add_tls_application_protocol(tls.securityProtocolOptions, alpnProtocol)
            sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { metadata, trust, complete in
                complete(verify(metadata, trust))
            }, queue)
            let tcp = NWProtocolTCP.Options()
            tcp.noDelay = true
            let params = NWParameters(tls: tls, tcp: tcp)
            params.includePeerToPeer = true
            return params
        }
    }

    /// Loads a PKCS#12 blob (e.g. a bundled self-signed dev certificate).
    public static func loadIdentity(p12 data: Data, passphrase: String) throws -> SecIdentity {
        var items: CFArray?
        var options: [CFString: Any] = [kSecImportExportPassphrase: passphrase]
        #if os(macOS)
        // Keep the identity in memory: importing into the login keychain makes
        // macOS prompt for the keychain password on every launch of an ad-hoc
        // signed build, because each build has a different code signature.
        if #available(macOS 15.0, *) {
            options[kSecImportToMemoryOnly] = true
        }
        #endif
        let status = SecPKCS12Import(data as CFData, options as CFDictionary, &items)
        guard status == errSecSuccess,
              let array = items as? [[CFString: Any]],
              let first = array.first,
              let identityRef = first[kSecImportItemIdentity] else {
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(status),
                          userInfo: [NSLocalizedDescriptionKey: "SecPKCS12Import failed (\(status))"])
        }
        return identityRef as! SecIdentity
    }
}
