// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import UIKit
import RemoteAppProtocol

/// The flat half of a half-open foldable (iPhone Duo held like a laptop).
/// Top to bottom, mirroring a laptop: the host app's menu bar right below the
/// hinge, a compact keyboard, and a trackpad. The stream itself lives on the
/// upright half (see `StreamUIView`).
final class LaptopDeckView: UIView {
    /// Host menu tree, as last reported; drives the menu bar's buttons.
    var menu: [MenuNode] = [] {
        didSet { if menu != oldValue { rebuildMenuBar() } }
    }
    var onMenuAction: (([Int]) -> Void)?
    /// Asks the host for a fresh menu tree (on appear and whenever a menu opens,
    /// so enabled/checked states are current).
    var onRequestMenu: (() -> Void)?
    var onKey: ((KeyEvent) -> Void)?
    let trackpad = TrackpadView()

    private let menuBar = UIScrollView()
    private let menuStack = UIStackView()
    private let menuPlaceholder = UILabel()
    private let keyboard = SoftKeyboardView()

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = UIColor(white: 0.07, alpha: 1)

        menuBar.showsHorizontalScrollIndicator = false
        menuBar.alwaysBounceHorizontal = true
        menuStack.axis = .horizontal
        menuStack.spacing = 2
        menuStack.alignment = .center
        menuBar.addSubview(menuStack)
        addSubview(menuBar)

        menuPlaceholder.text = "Menu bar"
        menuPlaceholder.font = .systemFont(ofSize: 13)
        menuPlaceholder.textColor = UIColor(white: 1, alpha: 0.35)
        menuBar.addSubview(menuPlaceholder)

        keyboard.onKey = { [weak self] key in self?.onKey?(key) }
        addSubview(keyboard)
        addSubview(trackpad)
        rebuildMenuBar()
    }

    required init?(coder: NSCoder) { fatalError() }

    override func layoutSubviews() {
        super.layoutSubviews()
        let insets = safeAreaInsets
        let x = insets.left
        let width = bounds.width - insets.left - insets.right
        let menuHeight: CGFloat = 36
        let bottom = bounds.height - insets.bottom
        // The keyboard takes what the trackpad does not need: a laptop pad is
        // short and wide, so give it ~a third of what is left. On a short half
        // (the cover display) the keyboard keeps its typable height and the pad
        // shrinks first, down to a strip.
        let remaining = bottom - menuHeight
        let keyboardHeight = max(remaining - max(84, floor(remaining * 0.34)),
                                 min(remaining - 56, keyboard.preferredHeight))
        let padHeight = remaining - keyboardHeight

        menuBar.frame = CGRect(x: x, y: 0, width: width, height: menuHeight)
        menuStack.frame = CGRect(x: 8, y: 0, width: max(menuStack.systemLayoutSizeFitting(UIView.layoutFittingCompressedSize).width, 0),
                                 height: menuHeight)
        menuBar.contentSize = CGSize(width: menuStack.frame.maxX + 8, height: menuHeight)
        menuPlaceholder.frame = CGRect(x: 14, y: 0, width: 200, height: menuHeight)
        keyboard.frame = CGRect(x: x, y: menuHeight, width: width, height: keyboardHeight)
        trackpad.frame = CGRect(x: 0, y: menuHeight + keyboardHeight, width: bounds.width, height: bounds.height - menuHeight - keyboardHeight)
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        if window != nil, menu.isEmpty { onRequestMenu?() }
    }

    // MARK: Menu bar

    private func rebuildMenuBar() {
        for v in menuStack.arrangedSubviews { v.removeFromSuperview() }
        let titles = menu.enumerated().filter { !$0.element.isSeparator && !$0.element.title.isEmpty }
        menuPlaceholder.isHidden = !titles.isEmpty
        for (index, node) in titles {
            var config = UIButton.Configuration.plain()
            config.title = node.title
            config.baseForegroundColor = node.enabled ? .white : UIColor(white: 1, alpha: 0.4)
            config.contentInsets = NSDirectionalEdgeInsets(top: 6, leading: 10, bottom: 6, trailing: 10)
            config.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer { attrs in
                var a = attrs
                a.font = .systemFont(ofSize: 14, weight: .medium)
                return a
            }
            let button = UIButton(configuration: config)
            button.menu = Self.uiMenu(for: node, path: [index]) { [weak self] path in self?.onMenuAction?(path) }
            button.showsMenuAsPrimaryAction = true
            button.isEnabled = node.enabled
            // Refresh the tree each time a menu opens so the next open is current.
            button.addAction(UIAction { [weak self] _ in self?.onRequestMenu?() }, for: .menuActionTriggered)
            menuStack.addArrangedSubview(button)
        }
        setNeedsLayout()
    }

    /// A UIMenu tree from a MenuNode; separators become inline sections. The
    /// same shape the Mac menu bar uses, with the action routed through `act`.
    static func uiMenu(for node: MenuNode, path: [Int], act: @escaping ([Int]) -> Void) -> UIMenu {
        var sections: [UIMenuElement] = []
        var group: [UIMenuElement] = []
        func flush() {
            if !group.isEmpty {
                sections.append(UIMenu(title: "", options: .displayInline, children: group))
                group = []
            }
        }
        for (i, child) in node.children.enumerated() {
            let childPath = path + [i]
            if child.isSeparator {
                flush()
            } else if child.hasSubmenu {
                group.append(uiMenu(for: child, path: childPath, act: act))
            } else {
                let action = UIAction(title: child.title.isEmpty ? " " : child.title,
                                      subtitle: child.shortcut,
                                      state: child.checked ? .on : .off) { _ in act(childPath) }
                if !child.enabled { action.attributes = [.disabled] }
                group.append(action)
            }
        }
        flush()
        return UIMenu(title: node.title, children: sections)
    }
}

/// A compact laptop-style keyboard that sends host key events. Characters go
/// as text (shifted form when ⇧ is armed); ⌘/⌃/⌥ and the non-printing keys go
/// as key codes with modifier flags, which the host turns into shortcuts.
/// Modifiers are one-shot: tap ⌘, then "s", for ⌘S, as on the key bar.
///
/// Touches are handled directly rather than through buttons: a key fires on
/// touch-down (nothing to wait for), several fingers can be down at once, the
/// gaps between keys belong to the nearest key, and a pressed key lights up
/// and shows a balloon just above itself (clear of the thumb) until the
/// finger lifts, with a light haptic tick. The balloon goes away the instant
/// the finger lifts so neighbouring keys can be hit in quick succession.
final class SoftKeyboardView: UIView {
    var onKey: ((KeyEvent) -> Void)?

    private enum Action {
        case text(String, shifted: String)
        case code(UInt16)
        case modifier(KeyModifiers)
    }
    private struct Key {
        let label: String
        let action: Action
        var weight: CGFloat = 1
    }
    private struct Row {
        let keys: [Key]
        /// Height relative to a character row; the special-key strip is shorter.
        var height: CGFloat = 1
        /// Drawn like the accessory bar above the system keyboard.
        var isStrip = false
    }

    private static func chars(_ plain: String, _ shifted: String) -> [Key] {
        zip(plain, shifted).map { Key(label: String($0), action: .text(String($0), shifted: String($1))) }
    }

    /// Top to bottom: the same strip as the key bar above the system keyboard
    /// (esc, tab, one-shot modifiers, arrows), then plain character rows.
    private static let rows: [Row] = [
        Row(keys: [Key(label: "esc", action: .code(KeyEvent.escapeKey)),
                   Key(label: "tab", action: .code(KeyEvent.tabKey)),
                   Key(label: "⌃", action: .modifier(.control)),
                   Key(label: "⌥", action: .modifier(.option)),
                   Key(label: "⇧", action: .modifier(.shift)),
                   Key(label: "⌘", action: .modifier(.command)),
                   Key(label: "←", action: .code(KeyEvent.leftArrow)),
                   Key(label: "↑", action: .code(KeyEvent.upArrow)),
                   Key(label: "↓", action: .code(KeyEvent.downArrow)),
                   Key(label: "→", action: .code(KeyEvent.rightArrow))],
            height: 0.85, isStrip: true),
        Row(keys: chars("`1234567890-=", "~!@#$%^&*()_+")
            + [Key(label: "⌫", action: .code(KeyEvent.deleteKey), weight: 1.6)]),
        Row(keys: chars("qwertyuiop[]\\", "QWERTYUIOP{}|")),
        Row(keys: chars("asdfghjkl;'", "ASDFGHJKL:\"")
            + [Key(label: "return", action: .code(KeyEvent.returnKey), weight: 1.8)]),
        Row(keys: [Key(label: "⇧", action: .modifier(.shift), weight: 1.5)]
            + chars("zxcvbnm,./", "ZXCVBNM<>?")
            + [Key(label: "⇧", action: .modifier(.shift), weight: 1.5)]),
        Row(keys: [Key(label: "", action: .text(" ", shifted: " "))], height: 0.9),
    ]

    /// One drawn key: its model, its view, and whether it sits in the strip.
    private struct KeyCap {
        let key: Key
        let view: UIView
        let label: UILabel
        let inStrip: Bool
    }

    private var modifiers: KeyModifiers = []
    private var caps: [KeyCap] = []
    /// Keys currently held, by touch.
    private var held: [UITouch: Int] = [:]
    /// The balloon shown above each held key, by touch.
    private var balloons: [UITouch: UIView] = [:]
    private let haptic = UIImpactFeedbackGenerator(style: .light)

    override init(frame: CGRect) {
        super.init(frame: frame)
        isMultipleTouchEnabled = true
        for row in Self.rows {
            for key in row.keys {
                let view = UIView()
                view.layer.cornerRadius = 6
                view.layer.cornerCurve = .continuous
                view.isUserInteractionEnabled = false
                let label = UILabel()
                label.textAlignment = .center
                label.textColor = .white
                label.adjustsFontSizeToFitWidth = true
                label.minimumScaleFactor = 0.6
                view.addSubview(label)
                addSubview(view)
                caps.append(KeyCap(key: key, view: view, label: label, inStrip: row.isStrip))
            }
        }
        refreshLabels()
    }

    required init?(coder: NSCoder) { fatalError() }

    private static let gap: CGFloat = 4
    private static let heightUnits = rows.reduce(0) { $0 + $1.height }
    /// Height at which a character row is a comfortable 30 pt.
    var preferredHeight: CGFloat { 30 * Self.heightUnits + Self.gap * CGFloat(Self.rows.count + 1) }

    override func layoutSubviews() {
        super.layoutSubviews()
        let gap = Self.gap
        let unitHeight = (bounds.height - gap * CGFloat(Self.rows.count + 1)) / Self.heightUnits
        let font = UIFont.systemFont(ofSize: min(16, max(10, floor(unitHeight * 0.55))))
        var index = 0
        var y = gap
        for row in Self.rows {
            let rowHeight = unitHeight * row.height
            let weights = row.keys.reduce(0) { $0 + $1.weight }
            let unit = (bounds.width - gap * CGFloat(row.keys.count + 1)) / weights
            var x = gap
            for key in row.keys {
                let w = unit * key.weight
                let cap = caps[index]
                cap.view.frame = CGRect(x: x, y: y, width: w, height: rowHeight)
                cap.label.frame = cap.view.bounds
                cap.label.font = font
                x += w + gap
                index += 1
            }
            y += rowHeight + gap
        }
    }

    // MARK: Touches

    /// The key under a point, treating the gaps as part of the nearest key so a
    /// finger landing between two keys still registers.
    private func keyIndex(at point: CGPoint) -> Int? {
        var best: (index: Int, distance: CGFloat)?
        for (i, cap) in caps.enumerated() {
            let f = cap.view.frame
            if f.contains(point) { return i }
            // Distance from the point to the rectangle (0 inside).
            let dx = max(f.minX - point.x, 0, point.x - f.maxX)
            let dy = max(f.minY - point.y, 0, point.y - f.maxY)
            let d = dx * dx + dy * dy
            if best == nil || d < best!.distance { best = (i, d) }
        }
        // Only claim points that are within a gap's reach of some key.
        guard let best, best.distance <= (Self.gap + 2) * (Self.gap + 2) else { return nil }
        return best.index
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            guard let index = keyIndex(at: touch.location(in: self)) else { continue }
            held[touch] = index
            setHighlighted(index, true)
            showBalloon(for: touch, over: index)
            haptic.impactOccurred(intensity: 0.7)
            haptic.prepare()
            press(caps[index].key)
        }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) { release(touches) }
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) { release(touches) }

    private func release(_ touches: Set<UITouch>) {
        for touch in touches {
            guard let index = held.removeValue(forKey: touch) else { continue }
            balloons.removeValue(forKey: touch)?.removeFromSuperview()
            if !held.values.contains(index) { setHighlighted(index, false) }
        }
    }

    /// An enlarged copy of the key, sitting just above it like the system
    /// keyboard's key pop so the pressed key is readable under a thumb. Small
    /// (about a third larger) and shown without animation.
    private func showBalloon(for touch: UITouch, over index: Int) {
        let cap = caps[index]
        let keyFrame = cap.view.frame
        let size = CGSize(width: max(keyFrame.width * 1.35, 40), height: keyFrame.height * 1.5)
        var origin = CGPoint(x: keyFrame.midX - size.width / 2, y: keyFrame.minY - size.height - 2)
        origin.x = min(max(origin.x, 0), bounds.width - size.width)
        let balloon = UIView(frame: CGRect(origin: origin, size: size))
        balloon.backgroundColor = UIColor(white: 0.45, alpha: 1)
        balloon.layer.cornerRadius = 8
        balloon.layer.cornerCurve = .continuous
        balloon.layer.shadowColor = UIColor.black.cgColor
        balloon.layer.shadowOpacity = 0.6
        balloon.layer.shadowRadius = 4
        balloon.layer.shadowOffset = CGSize(width: 0, height: 2)
        balloon.isUserInteractionEnabled = false
        let label = UILabel(frame: balloon.bounds)
        label.text = cap.label.text?.isEmpty == false ? cap.label.text : "space"
        label.textAlignment = .center
        label.textColor = .white
        label.font = .systemFont(ofSize: (cap.label.font.pointSize) * 1.3, weight: .medium)
        label.adjustsFontSizeToFitWidth = true
        label.minimumScaleFactor = 0.6
        balloon.addSubview(label)
        addSubview(balloon)
        balloons[touch] = balloon
    }

    private func setHighlighted(_ index: Int, _ on: Bool) {
        let cap = caps[index]
        cap.view.backgroundColor = on ? UIColor(white: 0.55, alpha: 1) : baseColor(for: cap)
        cap.view.transform = on ? CGAffineTransform(scaleX: 1.06, y: 1.06) : .identity
    }

    private func baseColor(for cap: KeyCap) -> UIColor {
        switch cap.key.action {
        case .text: return UIColor(white: 0.24, alpha: 1)
        case .code: return UIColor(white: cap.inStrip ? 0.2 : 0.16, alpha: 1)
        case .modifier(let mod):
            return modifiers.contains(mod) ? .tintColor : UIColor(white: cap.inStrip ? 0.2 : 0.16, alpha: 1)
        }
    }

    // MARK: Keys → events

    private func press(_ key: Key) {
        switch key.action {
        case .modifier(let mod):
            if modifiers.contains(mod) { modifiers.remove(mod) } else { modifiers.insert(mod) }
            refreshLabels()
        case .code(let code):
            onKey?(KeyEvent(keyCode: code, modifiers: modifiers))
            clearModifiers()
        case .text(let plain, let shifted):
            // Shift becomes the shifted character itself (what a Mac keyboard
            // produces); the host derives Shift from it for key-code readers.
            // Other modifiers ride along as flags for shortcut matching.
            let text = modifiers.contains(.shift) ? shifted : plain
            onKey?(KeyEvent(text: text, modifiers: modifiers.subtracting(.shift)))
            clearModifiers()
        }
    }

    private func clearModifiers() {
        guard !modifiers.isEmpty else { return }
        modifiers = []
        refreshLabels()
    }

    private func refreshLabels() {
        let shift = modifiers.contains(.shift)
        let heldIndices = Set(held.values)
        for (i, cap) in caps.enumerated() {
            if case .text(let plain, let shifted) = cap.key.action {
                cap.label.text = shift ? shifted : plain
            } else {
                cap.label.text = cap.key.label
            }
            if !heldIndices.contains(i) { cap.view.backgroundColor = baseColor(for: cap) }
        }
    }
}
