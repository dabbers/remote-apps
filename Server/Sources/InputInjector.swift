// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import AppKit
import Foundation
import CoreGraphics
import RemoteAppProtocol

/// Turns protocol input events into CGEvents posted directly to the target
/// app's process. Posting to the PID (rather than the HID system) means the
/// host's real cursor does not move and other apps are unaffected.
final class InputInjector {
    let pid: pid_t
    /// Streamed window frame, global top-left-origin coordinates.
    var windowFrame: CGRect
    /// Client points per host point for scroll conversion.
    var scrollScale: Double = 1

    private let source = CGEventSource(stateID: .hidSystemState)
    private let queue = DispatchQueue(label: "remoteapp.input", qos: .userInteractive)
    private var lastClick: (time: Date, point: CGPoint, count: Int64)?
    private var dragging = false
    private var lastActivate = Date.distantPast

    init(pid: pid_t, windowFrame: CGRect) {
        self.pid = pid
        self.windowFrame = windowFrame
    }

    var onLog: ((String) -> Void)?

    /// Events are posted to the app's PID, but an inactive window absorbs the
    /// first click (and ignores keystrokes) just to become key. So bring the app
    /// to the front first whenever it is not already active. Throttled so a burst
    /// of events does not hammer activation.
    private func ensureActive() {
        guard Date().timeIntervalSince(lastActivate) > 0.3 else { return }
        guard !WindowControl.isFrontmost(pid: pid) else { return }
        lastActivate = Date()
        WindowControl.activate(pid: pid)                       // AX frontmost (works from background)
        DispatchQueue.main.async { [pid] in
            NSRunningApplication(processIdentifier: pid)?.activate()   // best-effort backup
        }
        usleep(120_000)   // let the window server make it key
        onLog?("activated app \(pid) for input (was not frontmost)")
    }

    func handle(_ event: InputEvent) {
        queue.async { [self] in
            switch event {
            case .unknown:
                return   // an optional event from a newer client (PROTOCOL.md §8): ignore
            case .move(let at):
                // Hover never activates the app (a click does): the host cursor
                // follows the remote pointer only while the streamed app is
                // already frontmost, so it cannot disturb whatever else is in use.
                guard WindowControl.isFrontmost(pid: pid) else { return }
                post(mouseEvent(.mouseMoved, at: screenPoint(at), button: .left, clickState: 0))
                return
            default:
                break
            }
            ensureActive()
            switch event {
            case .tap(let at, let button):
                click(at: screenPoint(at), button: button)
            case .longPress(let at):
                click(at: screenPoint(at), button: .right)
            case .scroll(let at, let dx, let dy):
                scroll(at: screenPoint(at), dx: dx * scrollScale, dy: dy * scrollScale)
            case .drag(let at, let phase):
                drag(at: screenPoint(at), phase: phase)
            case .key(let key):
                self.key(key)
            case .move, .unknown:
                break   // handled above
            }
        }
    }

    private func screenPoint(_ p: NormalizedPoint) -> CGPoint {
        CGPoint(x: windowFrame.minX + min(max(p.x, 0), 1) * windowFrame.width,
                y: windowFrame.minY + min(max(p.y, 0), 1) * windowFrame.height)
    }

    /// Post through the HID event stream so the window server hit-tests the
    /// event like a real click/keystroke. Posting straight to the PID
    /// (`postToPid`) leaves keystrokes working but mouse clicks are not
    /// reliably hit-tested, so they would not place the caret or hit controls.
    /// The trade-off is that the host's real cursor moves (fine when the Mac is
    /// being driven remotely); the target app is brought frontmost first.
    private func post(_ event: CGEvent?) {
        event?.post(tap: .cghidEventTap)
    }

    private func mouseEvent(_ type: CGEventType, at point: CGPoint, button: CGMouseButton, clickState: Int64 = 1) -> CGEvent? {
        let e = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: point, mouseButton: button)
        e?.setIntegerValueField(.mouseEventClickState, value: clickState)
        return e
    }

    private func click(at point: CGPoint, button: MouseButton) {
        let now = Date()
        var count: Int64 = 1
        if let last = lastClick, now.timeIntervalSince(last.time) < 0.4, hypot(last.point.x - point.x, last.point.y - point.y) < 10 {
            count = last.count + 1
        }
        lastClick = (now, point, count)
        let (down, up, cgButton): (CGEventType, CGEventType, CGMouseButton) = switch button {
        case .left: (.leftMouseDown, .leftMouseUp, .left)
        case .right: (.rightMouseDown, .rightMouseUp, .right)
        case .middle: (.otherMouseDown, .otherMouseUp, .center)
        }
        post(mouseEvent(.mouseMoved, at: point, button: cgButton, clickState: 0))
        post(mouseEvent(down, at: point, button: cgButton, clickState: count))
        usleep(30_000)
        post(mouseEvent(up, at: point, button: cgButton, clickState: count))
    }

    private func scroll(at point: CGPoint, dx: Double, dy: Double) {
        guard let e = CGEvent(scrollWheelEvent2Source: source, units: .pixel, wheelCount: 2,
                              wheel1: Int32(dy.rounded()), wheel2: Int32(dx.rounded()), wheel3: 0) else { return }
        e.location = point
        post(e)
    }

    private func drag(at point: CGPoint, phase: DragPhase) {
        switch phase {
        case .began:
            dragging = true
            post(mouseEvent(.mouseMoved, at: point, button: .left, clickState: 0))
            post(mouseEvent(.leftMouseDown, at: point, button: .left))
        case .moved:
            guard dragging else { return }
            post(mouseEvent(.leftMouseDragged, at: point, button: .left))
        case .ended, .cancelled:
            guard dragging else { return }
            dragging = false
            post(mouseEvent(.leftMouseUp, at: point, button: .left))
        }
    }

    private func key(_ key: KeyEvent) {
        var flags = CGEventFlags()
        if key.modifiers.contains(.shift) { flags.insert(.maskShift) }
        if key.modifiers.contains(.control) { flags.insert(.maskControl) }
        if key.modifiers.contains(.option) { flags.insert(.maskAlternate) }
        if key.modifiers.contains(.command) { flags.insert(.maskCommand) }

        // A shortcut (⌘/⌃ held) must be posted as a virtual key code: the window
        // server matches menu shortcuts and key equivalents on the key code, not
        // on the unicode payload, so ⌘C via a unicode "c" would not trigger Copy.
        // Map the typed character to its US-layout key code for that case.
        if key.keyCode == nil, let text = key.text, text.count == 1,
           key.modifiers.contains(.command) || key.modifiers.contains(.control),
           let code = Self.keyCode(for: text) {
            postKeyCode(code, flags: flags)
            return
        }

        if let code = key.keyCode {
            postKeyCode(CGKeyCode(code), flags: flags)
        } else if let text = key.text {
            // One key event per character; the virtual key code is a placeholder,
            // the unicode payload is what text views insert.
            for scalar in text.unicodeScalars {
                var utf16 = Array(String(scalar).utf16)
                let down = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: true)
                let up = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: false)
                down?.keyboardSetUnicodeString(stringLength: utf16.count, unicodeString: &utf16)
                up?.keyboardSetUnicodeString(stringLength: utf16.count, unicodeString: &utf16)
                down?.flags = flags
                up?.flags = flags
                post(down)
                post(up)
            }
        }
    }

    private func postKeyCode(_ code: CGKeyCode, flags: CGEventFlags) {
        let down = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: true)
        let up = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: false)
        down?.flags = flags
        up?.flags = flags
        post(down)
        post(up)
    }

    /// US-QWERTY character → virtual key code, for posting modifier shortcuts.
    private static func keyCode(for text: String) -> CGKeyCode? {
        guard let ch = text.lowercased().first else { return nil }
        return usKeyCodes[ch]
    }

    private static let usKeyCodes: [Character: CGKeyCode] = [
        "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8,
        "v": 9, "b": 11, "q": 12, "w": 13, "e": 14, "r": 15, "y": 16, "t": 17,
        "1": 18, "2": 19, "3": 20, "4": 21, "6": 22, "5": 23, "=": 24, "9": 25,
        "7": 26, "-": 27, "8": 28, "0": 29, "]": 30, "o": 31, "u": 32, "[": 33,
        "i": 34, "p": 35, "l": 37, "j": 38, "'": 39, "k": 40, ";": 41, "\\": 42,
        ",": 43, "/": 44, "n": 45, "m": 46, ".": 47, "`": 50, " ": 49,
    ]
}
