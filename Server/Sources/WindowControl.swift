// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import AppKit
import ApplicationServices

/// Accessibility-API window control. Coordinates are global, top-left origin
/// (the same space CGWindow bounds and CGEvent locations use).
enum WindowControl {
    static var isTrusted: Bool { AXIsProcessTrusted() }

    static func promptForTrust() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
    }

    /// The app's main window, falling back to the focused window, then the first window.
    static func mainWindow(pid: pid_t) -> AXUIElement? {
        let app = AXUIElementCreateApplication(pid)
        for attribute in [kAXMainWindowAttribute, kAXFocusedWindowAttribute] {
            if let element = copyAttribute(app, attribute) {
                return (element as! AXUIElement)
            }
        }
        if let windows = copyAttribute(app, kAXWindowsAttribute) as? [AXUIElement], let first = windows.first {
            return first
        }
        return nil
    }

    /// Brings the app to the front through the Accessibility API. Unlike
    /// `NSRunningApplication.activate()`, which macOS ignores when the caller is
    /// not itself the active app, setting `AXFrontmost` works from our
    /// (Accessibility-trusted) background server, which is what lets injected
    /// clicks reach controls instead of just waking an inactive window.
    static func activate(pid: pid_t) {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetAttributeValue(app, kAXFrontmostAttribute as CFString, kCFBooleanTrue)
        if let window = mainWindow(pid: pid) {
            AXUIElementPerformAction(window, kAXRaiseAction as CFString)
            AXUIElementSetAttributeValue(window, kAXMainAttribute as CFString, kCFBooleanTrue)
        }
    }

    static func isFrontmost(pid: pid_t) -> Bool {
        let app = AXUIElementCreateApplication(pid)
        return (copyAttribute(app, kAXFrontmostAttribute) as? Bool) ?? false
    }

    static func frame(of window: AXUIElement) -> CGRect? {
        guard let posValue = copyAttribute(window, kAXPositionAttribute),
              let sizeValue = copyAttribute(window, kAXSizeAttribute) else { return nil }
        var origin = CGPoint.zero
        var size = CGSize.zero
        guard AXValueGetValue(posValue as! AXValue, .cgPoint, &origin),
              AXValueGetValue(sizeValue as! AXValue, .cgSize, &size) else { return nil }
        return CGRect(origin: origin, size: size)
    }

    static func title(of window: AXUIElement) -> String? {
        copyAttribute(window, kAXTitleAttribute) as? String
    }

    /// Moves and resizes the window, then returns the frame the app actually
    /// settled on (apps clamp to their minimum/maximum sizes).
    @discardableResult
    static func setFrame(_ window: AXUIElement, origin: CGPoint, size: CGSize) -> CGRect? {
        var origin = origin
        var size = size
        if let posValue = AXValueCreate(.cgPoint, &origin) {
            AXUIElementSetAttributeValue(window, kAXPositionAttribute as CFString, posValue)
        }
        if let sizeValue = AXValueCreate(.cgSize, &size) {
            AXUIElementSetAttributeValue(window, kAXSizeAttribute as CFString, sizeValue)
        }
        // Some apps apply the size asynchronously; give them a moment.
        usleep(50_000)
        return frame(of: window)
    }

    static func raise(_ window: AXUIElement) {
        AXUIElementPerformAction(window, kAXRaiseAction as CFString)
    }

    /// The NSScreen showing a given display, if AppKit knows about it yet.
    static func screen(displayID: CGDirectDisplayID) -> NSScreen? {
        NSScreen.screens.first {
            ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID) == displayID
        }
    }

    /// Top-left corner of a screen's usable area (below its menu bar), in
    /// global top-left-origin coordinates. `nil`, or a display AppKit does not
    /// know, means the main screen.
    static func contentOrigin(displayID: CGDirectDisplayID? = nil) -> CGPoint {
        guard let screen = displayID.flatMap(screen(displayID:)) ?? NSScreen.main else { return .zero }
        // AppKit's y axis points up from the bottom of the primary screen; flip
        // against the primary's height to get the top-left-origin y.
        let primaryHeight = NSScreen.screens.first?.frame.maxY ?? screen.frame.maxY
        return CGPoint(x: screen.visibleFrame.minX, y: primaryHeight - screen.visibleFrame.maxY)
    }

    private static func copyAttribute(_ element: AXUIElement, _ attribute: String) -> AnyObject? {
        var value: AnyObject?
        let result = AXUIElementCopyAttributeValue(element, attribute as CFString, &value)
        return result == .success ? value : nil
    }
}
