import Foundation
import Network
import RemoteAppProtocol

/// Browses Bonjour for RemoteApp servers of the selected transport.
final class Discovery {
    struct Server: Identifiable, Hashable {
        let name: String
        let endpoint: NWEndpoint
        var id: String { name }
    }

    var onUpdate: (([Server]) -> Void)?
    private var browser: NWBrowser?

    func start(kind: Transport.Kind) {
        stop()
        let params = NWParameters()
        params.includePeerToPeer = true
        let browser = NWBrowser(for: .bonjour(type: kind.serviceType, domain: nil), using: params)
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            let servers = results.compactMap { result -> Server? in
                guard case .service(let name, _, _, _) = result.endpoint else { return nil }
                return Server(name: name, endpoint: result.endpoint)
            }.sorted { $0.name < $1.name }
            DispatchQueue.main.async { self?.onUpdate?(servers) }
        }
        browser.stateUpdateHandler = { state in
            if case .failed(let error) = state { print("browser failed: \(error)") }
        }
        browser.start(queue: DispatchQueue(label: "remoteapp.browser"))
        self.browser = browser
    }

    func stop() {
        browser?.cancel()
        browser = nil
    }
}
