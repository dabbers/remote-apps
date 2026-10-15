import Foundation
import Network

/// Wraps an `NWConnection` with message framing. Thread-safe to call `send`
/// from anywhere; delegate callbacks arrive on `queue`.
public final class MessageConnection: @unchecked Sendable {
    public let connection: NWConnection
    public let queue: DispatchQueue

    public var onMessage: ((Message) -> Void)?
    public var onStateChange: ((NWConnection.State) -> Void)?
    public var onClose: ((Error?) -> Void)?

    private var parser = FrameParser()
    private var closed = false

    public init(connection: NWConnection, queue: DispatchQueue = DispatchQueue(label: "remoteapp.connection")) {
        self.connection = connection
        self.queue = queue
    }

    public func start() {
        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            self.onStateChange?(state)
            switch state {
            case .ready:
                self.receiveLoop()
            case .failed(let error):
                self.finish(error: error)
            case .cancelled:
                self.finish(error: nil)
            default:
                break
            }
        }
        connection.start(queue: queue)
    }

    public func cancel() {
        connection.cancel()
    }

    public func send(_ message: Message, completion: ((NWError?) -> Void)? = nil) {
        do {
            let data = try MessageCodec.encode(message)
            connection.send(content: data, completion: .contentProcessed { error in
                completion?(error)
            })
        } catch {
            completion?(.posix(.EINVAL))
        }
    }

    public func send(_ control: ControlMessage, completion: ((NWError?) -> Void)? = nil) {
        send(.control(control), completion: completion)
    }

    private func receiveLoop() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 1 << 20) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let data, !data.isEmpty {
                self.parser.append(data)
                do {
                    for message in try self.parser.drain() {
                        self.onMessage?(message)
                    }
                } catch {
                    self.connection.cancel()
                    self.finish(error: error)
                    return
                }
            }
            if let error {
                self.finish(error: error)
                return
            }
            if isComplete {
                self.connection.cancel()
                self.finish(error: nil)
                return
            }
            self.receiveLoop()
        }
    }

    private func finish(error: Error?) {
        guard !closed else { return }
        closed = true
        onClose?(error)
    }
}
