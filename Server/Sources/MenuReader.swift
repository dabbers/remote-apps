// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import AppKit
import ApplicationServices
import CoreGraphics
import RemoteAppProtocol

/// Reads a target app's menu bar through the Accessibility API and performs
/// menu items, so the client can show a native menu instead of streamed pixels.
enum MenuReader {
    private static let maxDepth = 8
    private static let maxItemsPerMenu = 200

    // MARK: Reading

    /// Top-level menus of the app, with the Apple menu omitted.
    static func read(pid: pid_t) -> [MenuNode] {
        let app = AXUIElementCreateApplication(pid)
        guard let menuBar = copy(app, kAXMenuBarAttribute) as! AXUIElement?,
              let items = copy(menuBar, kAXChildrenAttribute) as? [AXUIElement] else { return [] }
        // The first menu-bar item is the Apple menu; skip it.
        return items.dropFirst().map { node(for: $0, depth: 0) }
    }

    private static func node(for item: AXUIElement, depth: Int) -> MenuNode {
        let title = (copy(item, kAXTitleAttribute) as? String) ?? ""
        let enabled = (copy(item, kAXEnabledAttribute) as? Bool) ?? true
        let mark = (copy(item, "AXMenuItemMarkChar") as? String) ?? ""
        let children = depth < maxDepth ? submenuChildren(of: item, depth: depth + 1) : []
        let isSeparator = title.isEmpty && children.isEmpty
        return MenuNode(title: title,
                        enabled: enabled,
                        isSeparator: isSeparator,
                        checked: !mark.isEmpty,
                        shortcut: children.isEmpty ? shortcut(of: item) : nil,
                        children: children)
    }

    /// A menu item's children live under item → AXMenu (its single child) → items.
    private static func submenuChildren(of item: AXUIElement, depth: Int) -> [MenuNode] {
        guard let menus = copy(item, kAXChildrenAttribute) as? [AXUIElement],
              let menu = menus.first,
              let items = copy(menu, kAXChildrenAttribute) as? [AXUIElement] else { return [] }
        return items.prefix(maxItemsPerMenu).map { node(for: $0, depth: depth) }
    }

    private static func shortcut(of item: AXUIElement) -> String? {
        guard let cmdChar = copy(item, "AXMenuItemCmdChar") as? String, !cmdChar.isEmpty else { return nil }
        let mods = (copy(item, "AXMenuItemCmdModifiers") as? Int) ?? 0
        var s = ""
        if mods & 4 != 0 { s += "⌃" }        // control
        if mods & 2 != 0 { s += "⌥" }        // option
        if mods & 1 != 0 { s += "⇧" }        // shift
        if mods & 8 == 0 { s += "⌘" }        // command, unless the "no command" bit is set
        s += cmdChar.uppercased()
        return s
    }

    // MARK: Performing

    /// Resolves a path of child indexes (from `read`, Apple menu omitted) to its
    /// AXUIElement and presses it. Falls back to posting the shortcut.
    @discardableResult
    static func perform(pid: pid_t, path: [Int]) -> Bool {
        let app = AXUIElementCreateApplication(pid)
        guard let item = resolve(app: app, path: path) else { return false }
        let result = AXUIElementPerformAction(item, kAXPressAction as CFString)
        if result == .success { return true }
        return postShortcut(for: item, pid: pid)
    }

    private static func resolve(app: AXUIElement, path: [Int]) -> AXUIElement? {
        guard let menuBar = copy(app, kAXMenuBarAttribute) as! AXUIElement?,
              let top = copy(menuBar, kAXChildrenAttribute) as? [AXUIElement] else { return nil }
        let menus = Array(top.dropFirst())           // skip Apple menu
        guard let first = path.first, menus.indices.contains(first) else { return nil }
        var current = menus[first]
        for index in path.dropFirst() {
            guard let subMenus = copy(current, kAXChildrenAttribute) as? [AXUIElement],
                  let menu = subMenus.first,
                  let items = copy(menu, kAXChildrenAttribute) as? [AXUIElement],
                  items.indices.contains(index) else { return nil }
            current = items[index]
        }
        return current
    }

    /// Best-effort fallback: post the item's keyboard shortcut to the app.
    private static func postShortcut(for item: AXUIElement, pid: pid_t) -> Bool {
        guard let vkey = copy(item, "AXMenuItemCmdVirtualKey") as? Int else { return false }
        let mods = (copy(item, "AXMenuItemCmdModifiers") as? Int) ?? 0
        var flags = CGEventFlags()
        if mods & 1 != 0 { flags.insert(.maskShift) }
        if mods & 2 != 0 { flags.insert(.maskAlternate) }
        if mods & 4 != 0 { flags.insert(.maskControl) }
        if mods & 8 == 0 { flags.insert(.maskCommand) }
        let source = CGEventSource(stateID: .hidSystemState)
        let down = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(vkey), keyDown: true)
        let up = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(vkey), keyDown: false)
        down?.flags = flags
        up?.flags = flags
        down?.postToPid(pid)
        up?.postToPid(pid)
        return down != nil
    }

    private static func copy(_ element: AXUIElement, _ attribute: String) -> AnyObject? {
        var value: AnyObject?
        return AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success ? value : nil
    }
}
