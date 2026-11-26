// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import CoreGraphics
import Foundation
import IOKit.pwr_mgt

/// Keeps the host's display on, and reports when its screen is locked.
///
/// A sleeping display has nothing to capture: ScreenCaptureKit reports no
/// displays or windows and window frames cannot be read. Worse, macOS locks
/// the screen a few minutes after the display sleeps, and a locked screen
/// hides every app's windows from Accessibility and capture until someone
/// types the password. A headless host left alone past its display-sleep
/// timeout (10 minutes by default) was therefore unusable from then on.
///
/// The server wakes the display when a client arrives, and holds it awake
/// while a client is connected or while it is streaming from its own virtual
/// display (a headless host, where there is no panel to save).
@MainActor
enum DisplayPower {
    private static var assertion: IOPMAssertionID = 0
    private static var held = false

    /// True while the login session's screen is locked. Nothing can be streamed
    /// then, and only the user can unlock it.
    nonisolated static var isScreenLocked: Bool {
        guard let session = CGSessionCopyCurrentDictionary() as? [String: Any] else { return false }
        return (session["CGSSessionScreenIsLocked"] as? Bool) ?? ((session["CGSSessionScreenIsLocked"] as? Int) == 1)
    }

    /// What to tell a client that tries to stream from a locked host.
    nonisolated static let lockedMessage = "The Mac's screen is locked. Unlock it, and set System Settings › Lock Screen › Require password to Never so it stays reachable."

    /// Wakes a sleeping display, as a key press would.
    static func wake() {
        var id: IOPMAssertionID = 0
        IOPMAssertionDeclareUserActivity("dabtop client connected" as CFString, kIOPMUserActiveLocal, &id)
    }

    /// Wakes the display and waits (up to ~4 s) until it is actually on, so the
    /// caller does not try to read or capture windows while it is still dark.
    static func wakeAndWait() async {
        wake()
        for _ in 0..<40 where CGDisplayIsAsleep(CGMainDisplayID()) != 0 {
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
    }

    /// Holds, or releases, the assertion that stops the display idling to sleep.
    static func keepAwake(_ on: Bool) {
        if on, !held {
            held = IOPMAssertionCreateWithName(kIOPMAssertionTypePreventUserIdleDisplaySleep as CFString,
                                               IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                               "dabtop is streaming to a client" as CFString,
                                               &assertion) == kIOReturnSuccess
        } else if !on, held {
            IOPMAssertionRelease(assertion)
            held = false
        }
    }
}
