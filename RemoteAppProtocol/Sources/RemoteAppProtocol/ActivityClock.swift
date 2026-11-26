// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import Foundation

/// When the peer was last heard from. Both sides use it to notice a dead
/// connection: a suspended phone or a dropped tunnel sends no FIN, so the
/// socket alone can look healthy for minutes. Safe to touch from any queue.
public final class ActivityClock: @unchecked Sendable {
    private let lock = NSLock()
    private var stamp = Date()

    public init() {}

    /// Record that something arrived just now.
    public func touch() {
        lock.lock(); stamp = Date(); lock.unlock()
    }

    /// When something last arrived.
    public var last: Date {
        lock.lock(); defer { lock.unlock() }
        return stamp
    }

    /// Seconds since something last arrived.
    public var age: TimeInterval { Date().timeIntervalSince(last) }
}
