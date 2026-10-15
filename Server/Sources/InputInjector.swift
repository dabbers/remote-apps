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

    init(pid: pid_t, windowFrame: CGRect) {
        self.pid = pid
        self.windowFrame = windowFrame
    }

    func handle(_ event: InputEvent) {
        queue.async { [self] in
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
            }
        }
    }

    private func screenPoint(_ p: NormalizedPoint) -> CGPoint {
        CGPoint(x: windowFrame.minX + min(max(p.x, 0), 1) * windowFrame.width,
                y: windowFrame.minY + min(max(p.y, 0), 1) * windowFrame.height)
    }

    private func post(_ event: CGEvent?) {
        event?.postToPid(pid)
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

        if let code = key.keyCode {
            let down = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(code), keyDown: true)
            let up = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(code), keyDown: false)
            down?.flags = flags
            up?.flags = flags
            post(down)
            post(up)
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
}
