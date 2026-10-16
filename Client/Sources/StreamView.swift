// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import AVFoundation
import SwiftUI
import UIKit
import RemoteAppProtocol

/// Full-screen video with gesture → input mapping and keyboard capture.
struct StreamView: UIViewRepresentable {
    @EnvironmentObject var model: ClientModel

    func makeUIView(context: Context) -> StreamUIView {
        let view = StreamUIView(renderer: model.renderer)
        view.onInput = { [weak model] event in model?.send(event) }
        view.onToggleHUD = { [weak model] in model?.hudVisible.toggle() }
        view.onSizeChange = { [weak model] size in model?.reportStreamSize(size) }
        return view
    }

    func updateUIView(_ view: StreamUIView, context: Context) {
        if model.keyboardVisible, !view.isFirstResponder {
            view.becomeFirstResponder()
        } else if !model.keyboardVisible, view.isFirstResponder {
            view.resignFirstResponder()
        }
        // On Mac (Catalyst) the window is a real resizable Mac window; title it
        // with the streamed app so it reads like that app's own window.
        #if targetEnvironment(macCatalyst)
        let title = model.geometry?.windowTitle.flatMap { $0.isEmpty ? nil : $0 } ?? model.streamingAppName
        if let scene = view.window?.windowScene, scene.title != title {
            scene.title = title
        }
        #endif
    }
}

/// Renders the stream and maps touches to remote input.
///
/// Zoom/pan is done with a plain affine transform on the content view rather
/// than a UIScrollView: an AVSampleBufferDisplayLayer renders black inside a
/// zooming scroll view, but composes correctly under a view transform. This
/// matters when the host window cannot shrink to phone size (or to fit above
/// the keyboard): the picture is letterboxed small and the user pinches in and
/// two-finger-pans to reach the part they need.
final class StreamUIView: UIView, UIGestureRecognizerDelegate, UIKeyInput {
    var onInput: ((InputEvent) -> Void)?
    /// Called on a two-finger tap: shows or hides the on-screen controls.
    var onToggleHUD: (() -> Void)?
    /// Called when the drawable area changes (rotation, window resize, keyboard).
    var onSizeChange: ((CGSize) -> Void)?
    private var lastBoundsSize: CGSize = .zero

    /// One-shot modifiers armed from the accessory bar, applied to the next key.
    private var activeModifiers: KeyModifiers = []
    private var modifierButtons: [KeyModifiers: UIButton] = [:]

    private let renderer: VideoRenderer
    private let contentView = UIView()

    private var zoomScale: CGFloat = 1
    private var panOffset: CGPoint = .zero
    private let maxZoom: CGFloat = 6
    private var pinchStartScale: CGFloat = 1
    private var pinchStartOffset: CGPoint = .zero
    private var pinchAnchor: CGPoint = .zero
    private var panStartOffset: CGPoint = .zero
    private var lastScrollPoint: CGPoint = .zero

    init(renderer: VideoRenderer) {
        self.renderer = renderer
        super.init(frame: .zero)
        backgroundColor = .black
        clipsToBounds = true

        contentView.backgroundColor = .black
        contentView.layer.addSublayer(renderer.layer)
        addSubview(contentView)

        // One finger → remote input.
        let tap = UITapGestureRecognizer(target: self, action: #selector(handleTap))
        tap.numberOfTouchesRequired = 1
        let longPress = UILongPressGestureRecognizer(target: self, action: #selector(handleLongPress))
        longPress.minimumPressDuration = 0.45
        longPress.numberOfTouchesRequired = 1
        let scroll = UIPanGestureRecognizer(target: self, action: #selector(handleScroll))
        scroll.minimumNumberOfTouches = 1
        scroll.maximumNumberOfTouches = 1

        // Two fingers → zoom/pan the local picture, or tap to toggle the HUD.
        let pinch = UIPinchGestureRecognizer(target: self, action: #selector(handlePinch))
        let twoFingerPan = UIPanGestureRecognizer(target: self, action: #selector(handleZoomPan))
        twoFingerPan.minimumNumberOfTouches = 2
        twoFingerPan.maximumNumberOfTouches = 2
        let twoFingerTap = UITapGestureRecognizer(target: self, action: #selector(handleTwoFingerTap))
        twoFingerTap.numberOfTouchesRequired = 2

        for g in [tap, longPress, scroll, pinch, twoFingerPan, twoFingerTap] {
            g.delegate = self
            addGestureRecognizer(g)
        }

        renderer.onVideoSizeChange = { [weak self] _ in self?.setNeedsLayout() }
    }

    required init?(coder: NSCoder) { fatalError() }

    // MARK: Layout

    override func layoutSubviews() {
        super.layoutSubviews()
        if bounds.size != lastBoundsSize {
            lastBoundsSize = bounds.size
            onSizeChange?(bounds.size)
        }
        // Content view fills the view at 1x, then the zoom/pan transform is applied.
        contentView.transform = .identity
        contentView.bounds = CGRect(origin: .zero, size: bounds.size)
        contentView.center = CGPoint(x: bounds.midX, y: bounds.midY)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        renderer.layer.frame = contentView.bounds
        CATransaction.commit()
        applyTransform()
    }

    private func applyTransform() {
        clampOffset()
        contentView.transform = CGAffineTransform(scaleX: zoomScale, y: zoomScale)
        contentView.center = CGPoint(x: bounds.midX + panOffset.x, y: bounds.midY + panOffset.y)
    }

    /// Keeps the scaled content covering the viewport so panning never reveals black.
    private func clampOffset() {
        let maxX = max(0, (zoomScale - 1) * bounds.width / 2)
        let maxY = max(0, (zoomScale - 1) * bounds.height / 2)
        panOffset.x = min(max(panOffset.x, -maxX), maxX)
        panOffset.y = min(max(panOffset.y, -maxY), maxY)
        if zoomScale <= 1 { panOffset = .zero }
    }

    // MARK: Coordinate mapping

    /// The rectangle the video occupies inside the (unscaled) content view.
    private var videoRect: CGRect {
        let size = renderer.videoSize
        guard size.width > 0, size.height > 0 else { return contentView.bounds }
        return AVMakeRect(aspectRatio: size, insideRect: contentView.bounds)
    }

    /// Maps a point in the content view (UIKit already accounts for the zoom
    /// transform in `location(in:)`) to a point normalized to the video.
    private func normalized(_ point: CGPoint) -> NormalizedPoint? {
        let rect = videoRect
        guard rect.contains(point) else { return nil }
        return NormalizedPoint(x: (point.x - rect.minX) / rect.width, y: (point.y - rect.minY) / rect.height)
    }

    // MARK: One-finger gestures → remote input

    @objc private func handleTap(_ g: UITapGestureRecognizer) {
        guard let p = normalized(g.location(in: contentView)) else { return }
        onInput?(.tap(at: p, button: .left))
    }

    @objc private func handleLongPress(_ g: UILongPressGestureRecognizer) {
        guard g.state == .began, let p = normalized(g.location(in: contentView)) else { return }
        onInput?(.longPress(at: p))
    }

    @objc private func handleScroll(_ g: UIPanGestureRecognizer) {
        let location = g.location(in: contentView)
        switch g.state {
        case .began:
            lastScrollPoint = location
        case .changed:
            let dx = location.x - lastScrollPoint.x
            let dy = location.y - lastScrollPoint.y
            lastScrollPoint = location
            guard let p = normalized(location), dx != 0 || dy != 0 else { return }
            // Convert content-view points to host points so a finger-length
            // scroll moves about the same distance on the Mac.
            let scale = videoRect.width > 0 ? contentView.bounds.width / videoRect.width : 1
            onInput?(.scroll(at: p, dx: dx * scale, dy: dy * scale))
        default:
            break
        }
    }

    // MARK: Two-finger gestures → local zoom/pan, HUD

    @objc private func handleTwoFingerTap() {
        onToggleHUD?()
    }

    @objc private func handlePinch(_ g: UIPinchGestureRecognizer) {
        switch g.state {
        case .began:
            pinchStartScale = zoomScale
            pinchStartOffset = panOffset
            pinchAnchor = g.location(in: self)
        case .changed:
            let newScale = min(max(pinchStartScale * g.scale, 1), maxZoom)
            // Keep the point under the pinch fixed on screen as it scales.
            let anchorRel = CGPoint(x: pinchAnchor.x - bounds.midX, y: pinchAnchor.y - bounds.midY)
            let ratio = newScale / pinchStartScale
            panOffset = CGPoint(x: anchorRel.x - (anchorRel.x - pinchStartOffset.x) * ratio,
                                y: anchorRel.y - (anchorRel.y - pinchStartOffset.y) * ratio)
            zoomScale = newScale
            applyTransform()
        case .ended, .cancelled:
            if zoomScale <= 1.02 { zoomScale = 1; panOffset = .zero; applyTransform() }
        default:
            break
        }
    }

    @objc private func handleZoomPan(_ g: UIPanGestureRecognizer) {
        guard zoomScale > 1 else { return }
        switch g.state {
        case .began:
            panStartOffset = panOffset
        case .changed:
            let t = g.translation(in: self)
            panOffset = CGPoint(x: panStartOffset.x + t.x, y: panStartOffset.y + t.y)
            applyTransform()
        default:
            break
        }
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        // Pinch and two-finger pan work together; everything else stays exclusive.
        (gestureRecognizer is UIPinchGestureRecognizer && other is UIPanGestureRecognizer)
            || (gestureRecognizer is UIPanGestureRecognizer && other is UIPinchGestureRecognizer)
    }

    // MARK: UIKeyInput

    override var canBecomeFirstResponder: Bool { true }
    var hasText: Bool { true }
    var keyboardType: UIKeyboardType { .default }
    var autocorrectionType: UITextAutocorrectionType { .no }
    var autocapitalizationType: UITextAutocapitalizationType { .none }
    var spellCheckingType: UITextSpellCheckingType { .no }
    var smartQuotesType: UITextSmartQuotesType { .no }
    var smartDashesType: UITextSmartDashesType { .no }
    var smartInsertDeleteType: UITextSmartInsertDeleteType { .no }
    var inlinePredictionType: UITextInlinePredictionType { .no }
    // Keys are forwarded verbatim to the host, so the local field must never
    // autocorrect, autocapitalize, or predict; it is a raw key conduit.

    func insertText(_ text: String) {
        let mods = activeModifiers
        if text == "\n" {
            onInput?(.key(KeyEvent(keyCode: KeyEvent.returnKey, modifiers: mods)))
        } else if text == "\t" {
            onInput?(.key(KeyEvent(keyCode: KeyEvent.tabKey, modifiers: mods)))
        } else {
            onInput?(.key(KeyEvent(text: text, modifiers: mods)))
        }
        clearModifiers()
    }

    func deleteBackward() {
        onInput?(.key(KeyEvent(keyCode: KeyEvent.deleteKey, modifiers: activeModifiers)))
        clearModifiers()
    }

    // MARK: Key accessory bar
    //
    // A toolbar above the software keyboard with keys the iOS keyboard lacks:
    // Esc, Tab, arrows, and sticky modifiers (⌃⌥⇧⌘). A modifier is armed by a
    // tap and applied to the very next key (character or special), then cleared,
    // so ⌘ then "c" sends ⌘C. Not shown on Mac (Catalyst), which has a real
    // keyboard and the reproduced menu bar for shortcuts.

    override var inputAccessoryView: UIView? { keyBar }

    private lazy var keyBar: UIView = makeKeyBar()

    private func makeKeyBar() -> UIView {
        let bar = UIInputView(frame: CGRect(x: 0, y: 0, width: 0, height: 48), inputViewStyle: .keyboard)
        let stack = UIStackView()
        stack.axis = .horizontal
        stack.distribution = .fillEqually
        stack.spacing = 5
        stack.translatesAutoresizingMaskIntoConstraints = false

        stack.addArrangedSubview(specialKey("esc", code: KeyEvent.escapeKey))
        stack.addArrangedSubview(specialKey("tab", code: KeyEvent.tabKey))
        stack.addArrangedSubview(modifierKey("⌃", mod: .control))
        stack.addArrangedSubview(modifierKey("⌥", mod: .option))
        stack.addArrangedSubview(modifierKey("⇧", mod: .shift))
        stack.addArrangedSubview(modifierKey("⌘", mod: .command))
        stack.addArrangedSubview(specialKey("←", code: KeyEvent.leftArrow))
        stack.addArrangedSubview(specialKey("↑", code: KeyEvent.upArrow))
        stack.addArrangedSubview(specialKey("↓", code: KeyEvent.downArrow))
        stack.addArrangedSubview(specialKey("→", code: KeyEvent.rightArrow))

        bar.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: bar.safeAreaLayoutGuide.leadingAnchor, constant: 5),
            stack.trailingAnchor.constraint(equalTo: bar.safeAreaLayoutGuide.trailingAnchor, constant: -5),
            stack.topAnchor.constraint(equalTo: bar.topAnchor, constant: 5),
            stack.bottomAnchor.constraint(equalTo: bar.bottomAnchor, constant: -5),
        ])
        return bar
    }

    private func styleKey(_ button: UIButton) {
        button.titleLabel?.font = .systemFont(ofSize: 17, weight: .regular)
        button.setTitleColor(.label, for: .normal)
        button.backgroundColor = UIColor.secondarySystemFill
        button.layer.cornerRadius = 6
    }

    private func specialKey(_ title: String, code: UInt16) -> UIButton {
        let button = UIButton(type: .system, primaryAction: UIAction(title: title) { [weak self] _ in
            guard let self else { return }
            self.onInput?(.key(KeyEvent(keyCode: code, modifiers: self.activeModifiers)))
            self.clearModifiers()
        })
        styleKey(button)
        return button
    }

    private func modifierKey(_ title: String, mod: KeyModifiers) -> UIButton {
        let button = UIButton(type: .system, primaryAction: UIAction(title: title) { [weak self] _ in
            self?.toggleModifier(mod)
        })
        styleKey(button)
        modifierButtons[mod] = button
        return button
    }

    private func toggleModifier(_ mod: KeyModifiers) {
        if activeModifiers.contains(mod) { activeModifiers.remove(mod) } else { activeModifiers.insert(mod) }
        updateModifierButtons()
    }

    private func clearModifiers() {
        guard !activeModifiers.isEmpty else { return }
        activeModifiers = []
        updateModifierButtons()
    }

    private func updateModifierButtons() {
        for (mod, button) in modifierButtons {
            let on = activeModifiers.contains(mod)
            button.backgroundColor = on ? UIColor.tintColor : UIColor.secondarySystemFill
            button.setTitleColor(on ? .white : .label, for: .normal)
        }
    }

    // Hardware keyboard arrows / escape.
    override var keyCommands: [UIKeyCommand]? {
        [
            UIKeyCommand(input: UIKeyCommand.inputUpArrow, modifierFlags: [], action: #selector(arrow(_:))),
            UIKeyCommand(input: UIKeyCommand.inputDownArrow, modifierFlags: [], action: #selector(arrow(_:))),
            UIKeyCommand(input: UIKeyCommand.inputLeftArrow, modifierFlags: [], action: #selector(arrow(_:))),
            UIKeyCommand(input: UIKeyCommand.inputRightArrow, modifierFlags: [], action: #selector(arrow(_:))),
            UIKeyCommand(input: UIKeyCommand.inputEscape, modifierFlags: [], action: #selector(arrow(_:))),
        ]
    }

    @objc private func arrow(_ command: UIKeyCommand) {
        let code: UInt16? = switch command.input {
        case UIKeyCommand.inputUpArrow: KeyEvent.upArrow
        case UIKeyCommand.inputDownArrow: KeyEvent.downArrow
        case UIKeyCommand.inputLeftArrow: KeyEvent.leftArrow
        case UIKeyCommand.inputRightArrow: KeyEvent.rightArrow
        case UIKeyCommand.inputEscape: KeyEvent.escapeKey
        default: nil
        }
        if let code { onInput?(.key(KeyEvent(keyCode: code))) }
    }
}
