// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import AVFoundation
import SwiftUI
import UIKit
import UIKit.UIGestureRecognizerSubclass
import RemoteAppProtocol

/// Full-screen video with gesture → input mapping and keyboard capture.
struct StreamView: UIViewRepresentable {
    @EnvironmentObject var model: ClientModel

    func makeUIView(context: Context) -> StreamUIView {
        let view = StreamUIView(renderer: model.renderer)
        view.onInput = { [weak model] event in model?.send(event) }
        view.onToggleHUD = { [weak model] in model?.hudVisible.toggle() }
        view.onSizeChange = { [weak model] size in model?.reportStreamSize(size) }
        view.onPostureChange = { [weak model] posture in model?.posture = posture }
        view.onFlexChange = { [weak model] active in
            model?.flexActive = active
            // The deck has its own keyboard; the system one would otherwise take
            // the flat half and push the deck up into the upright half.
            if active { model?.keyboardVisible = false }
        }
        view.onMenuAction = { [weak model] path in model?.performMenu(path: path) }
        view.onRequestMenu = { [weak model] in model?.requestMenu() }
        view.flexModeEnabled = model.flexModeEnabled
        view.menu = model.menu
        return view
    }

    func updateUIView(_ view: StreamUIView, context: Context) {
        view.hoverEnabled = model.serverFeatures.contains(ProtocolFeature.move)
        view.flexModeEnabled = model.flexModeEnabled
        view.menu = model.menu
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
///
/// On a foldable (iPhone Duo) the view also watches the hinge. Held half-open
/// like a laptop, it goes into a "flex" layout: the video takes
/// the upright half and a `LaptopDeckView` (menu bar, keyboard, trackpad) the
/// flat half, the trackpad driving a virtual cursor drawn over the video. Folding shut or opening flat returns to the
/// full-bleed layout; the drawable area reported to the host follows, so the
/// host window matches whichever half, or whole, screen is in use.
final class StreamUIView: UIView, UIGestureRecognizerDelegate, UIKeyInput {
    var onInput: ((InputEvent) -> Void)?
    /// Called on a two-finger tap: shows or hides the on-screen controls.
    var onToggleHUD: (() -> Void)?
    /// Called when the drawable area changes (rotation, window resize, keyboard,
    /// fold/unfold, or entering/leaving the flex layout).
    var onSizeChange: ((CGSize) -> Void)?
    /// Called when a foldable's hinge changes state (never on flat devices).
    var onPostureChange: ((DevicePosture) -> Void)?
    /// Called when the laptop deck appears or disappears.
    var onFlexChange: ((Bool) -> Void)?
    /// When false, a half-open hinge keeps the plain full-screen layout.
    var flexModeEnabled = true {
        didSet { if flexModeEnabled != oldValue { setNeedsLayout() } }
    }
    /// Host menu tree for the deck's menu bar (flex layout only).
    var menu: [MenuNode] {
        get { deck.menu }
        set { deck.menu = newValue }
    }
    var onMenuAction: (([Int]) -> Void)? {
        get { deck.onMenuAction }
        set { deck.onMenuAction = newValue }
    }
    var onRequestMenu: (() -> Void)? {
        get { deck.onRequestMenu }
        set { deck.onRequestMenu = newValue }
    }
    /// True when the server accepts `move` events (PROTOCOL.md §8); pointer
    /// hover (Mac, iPad trackpad/mouse, Pencil hover) then moves the host cursor.
    var hoverEnabled = false
    private var lastReportedArea: CGSize = .zero
    private var lastLoggedBounds: CGSize = .zero

    // MARK: Foldable posture
    private var posture: DevicePosture = .unknown
    /// The part of the view the video occupies: all of it, or the upper half in
    /// the flex layout. Everything that maps touches or clamps zoom uses this.
    private var videoFrame: CGRect = .zero
    private let deck = LaptopDeckView()
    private var trackpad: TrackpadView { deck.trackpad }
    /// Where the trackpad's cursor points, normalized to the video.
    private var virtualCursor = NormalizedPoint(x: 0.5, y: 0.5)
    private let cursorView = UIView()
    /// The hinge crosses the inner display's long axis, so a half-open device
    /// is a portrait view (669×951 pt on the Duo) split at mid-height: the
    /// upright half above, the flat half below. A wider-than-tall view in this
    /// posture is the system keyboard's doing (it shrinks the view to the
    /// upright half), not a laptop arrangement, so it never splits.
    private var flexLayoutActive: Bool {
        flexModeEnabled && posture == .partiallyOpen && bounds.height > bounds.width
    }

    /// Hover is rate-limited to ~60/s; the newest position waits here for the
    /// next slot so the host cursor always ends where the pointer stopped.
    private let hoverInterval: CFTimeInterval = 1.0 / 60
    private var lastHoverTime: CFTimeInterval = 0
    private var pendingHover: NormalizedPoint?
    private var hoverFlush: DispatchWorkItem?

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
    /// The tap-then-hold recognizer, and when the last drag position was sent.
    private let tapHoldDrag = TapHoldDragRecognizer()
    private var lastDragSend: CFTimeInterval = 0

    init(renderer: VideoRenderer) {
        self.renderer = renderer
        super.init(frame: .zero)
        backgroundColor = .black
        clipsToBounds = true

        contentView.backgroundColor = .black
        contentView.layer.addSublayer(renderer.layer)
        addSubview(contentView)

        // Virtual cursor for the trackpad: a ring over the video, hidden unless
        // the flex layout is active. Added above the video layer.
        cursorView.bounds = CGRect(x: 0, y: 0, width: 22, height: 22)
        cursorView.layer.cornerRadius = 11
        cursorView.layer.borderWidth = 2
        cursorView.layer.borderColor = UIColor.white.cgColor
        cursorView.backgroundColor = UIColor(white: 0, alpha: 0.25)
        cursorView.layer.shadowColor = UIColor.black.cgColor
        cursorView.layer.shadowOpacity = 0.8
        cursorView.layer.shadowRadius = 2
        cursorView.layer.shadowOffset = .zero
        cursorView.isUserInteractionEnabled = false
        cursorView.isHidden = true
        contentView.addSubview(cursorView)

        deck.isHidden = true
        deck.onKey = { [weak self] key in self?.onInput?(.key(key)) }
        trackpad.onMove = { [weak self] dx, dy in self?.moveCursor(dx: dx, dy: dy) }
        trackpad.onTap = { [weak self] in
            guard let self else { return }
            self.onInput?(.tap(at: self.virtualCursor, button: .left))
        }
        trackpad.onSecondaryTap = { [weak self] in
            guard let self else { return }
            self.onInput?(.longPress(at: self.virtualCursor))
        }
        trackpad.onScroll = { [weak self] dx, dy in self?.scrollAtCursor(dx: dx, dy: dy) }
        trackpad.onDragBegan = { [weak self] in
            guard let self else { return }
            self.onInput?(.drag(at: self.virtualCursor, phase: .began))
        }
        trackpad.onDragMoved = { [weak self] dx, dy in
            guard let self else { return }
            self.moveCursor(dx: dx, dy: dy, hover: false)
            self.onInput?(.drag(at: self.virtualCursor, phase: .moved))
        }
        trackpad.onDragEnded = { [weak self] in
            guard let self else { return }
            self.onInput?(.drag(at: self.virtualCursor, phase: .ended))
        }
        addSubview(deck)

        var postureForced = false
        #if DEBUG
        // Developer aid: exercise the foldable layouts on a flat device or
        // simulator by launching with REMOTEAPP_POSTURE=partiallyOpen (or
        // closed / fullyOpen). The forced posture replaces hinge updates, which
        // would otherwise report the simulator's real (flat) state over it.
        if let fake = ProcessInfo.processInfo.environment["REMOTEAPP_POSTURE"] {
            let forced: DevicePosture = switch fake {
            case "closed": .closed
            case "partiallyOpen": .partiallyOpen
            case "fullyOpen": .fullyOpen
            default: .unknown
            }
            DispatchQueue.main.async { [weak self] in self?.setPosture(forced) }
            postureForced = true
        }
        #endif

        if !postureForced, #available(iOS 27.1, *) {
            let hinge = UIHingeInteraction { [weak self] _, update in
                NSLog("RemoteApp hinge update: %@",
                      update.hinge.map { "status=\($0.status.rawValue) angle=\($0.angle)" } ?? "nil (no hinge for this hierarchy)")
                // A nil hinge means this hierarchy gets no hinge updates right now
                // (iOS hands them out only in some arrangements, e.g. while the
                // system keyboard holds the flat half); the device did not change,
                // so keep the last posture rather than falling back to flat.
                guard let hinge = update.hinge else { return }
                let posture: DevicePosture = switch hinge.status {
                case .closed: .closed
                case .partiallyOpen: .partiallyOpen
                case .fullyOpen: .fullyOpen
                @unknown default: .unknown
                }
                self?.setPosture(posture)
            }
            addInteraction(hinge)
        }

        // One finger → remote input.
        let tap = UITapGestureRecognizer(target: self, action: #selector(handleTap))
        tap.numberOfTouchesRequired = 1
        let longPress = UILongPressGestureRecognizer(target: self, action: #selector(handleLongPress))
        longPress.minimumPressDuration = 0.45
        longPress.numberOfTouchesRequired = 1
        let scroll = UIPanGestureRecognizer(target: self, action: #selector(handleScroll))
        scroll.minimumNumberOfTouches = 1
        scroll.maximumNumberOfTouches = 1

        // Tap, then touch again and hold or move → left-button drag (select
        // text, move a slider, drag a window), like tap-and-a-half on a
        // trackpad. It fails the instant a touch is not preceded by a tap, so
        // making scroll and long press wait for it costs them nothing.
        tapHoldDrag.addTarget(self, action: #selector(handleTapHoldDrag))
        longPress.require(toFail: tapHoldDrag)
        scroll.require(toFail: tapHoldDrag)

        // Two fingers → zoom/pan the local picture, or tap to toggle the HUD.
        let pinch = UIPinchGestureRecognizer(target: self, action: #selector(handlePinch))
        let twoFingerPan = UIPanGestureRecognizer(target: self, action: #selector(handleZoomPan))
        twoFingerPan.minimumNumberOfTouches = 2
        twoFingerPan.maximumNumberOfTouches = 2
        let twoFingerTap = UITapGestureRecognizer(target: self, action: #selector(handleTwoFingerTap))
        twoFingerTap.numberOfTouchesRequired = 2

        // A pointer (Mac, iPad trackpad/mouse, Pencil hover) → host cursor follows.
        let hover = UIHoverGestureRecognizer(target: self, action: #selector(handleHover))

        for g in [tap, longPress, scroll, tapHoldDrag, pinch, twoFingerPan, twoFingerTap, hover] {
            g.delegate = self
            addGestureRecognizer(g)
        }

        renderer.onVideoSizeChange = { [weak self] _ in self?.setNeedsLayout() }
    }

    required init?(coder: NSCoder) { fatalError() }

    // MARK: Layout

    override func layoutSubviews() {
        super.layoutSubviews()
        let flex = flexLayoutActive
        // The hinge of a half-open foldable runs across the middle of the long
        // axis; the video stays on the upright (upper) half, the deck lies flat.
        videoFrame = flex
            ? CGRect(x: 0, y: 0, width: bounds.width, height: floor(bounds.height / 2))
            : bounds
        if deck.isHidden == flex {
            deck.isHidden = !flex
            if flex { onRequestMenu?() }
            onFlexChange?(flex)
        }
        if bounds.size != lastLoggedBounds {
            lastLoggedBounds = bounds.size
            NSLog("RemoteApp stream view %.0f×%.0f posture=%@ flex=%d", bounds.width, bounds.height, "\(posture)", flex ? 1 : 0)
        }
        deck.frame = CGRect(x: 0, y: videoFrame.maxY, width: bounds.width, height: bounds.height - videoFrame.maxY)
        cursorView.isHidden = !flex
        if videoFrame.size != lastReportedArea {
            lastReportedArea = videoFrame.size
            onSizeChange?(videoFrame.size)
        }
        // Content view fills the video area at 1x, then the zoom/pan transform is applied.
        contentView.transform = .identity
        contentView.bounds = CGRect(origin: .zero, size: videoFrame.size)
        contentView.center = CGPoint(x: videoFrame.midX, y: videoFrame.midY)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        renderer.layer.frame = contentView.bounds
        CATransaction.commit()
        positionCursor()
        applyTransform()
    }

    private func setPosture(_ new: DevicePosture) {
        guard new != posture else { return }
        posture = new
        onPostureChange?(new)
        setNeedsLayout()
    }

    private func applyTransform() {
        clampOffset()
        contentView.transform = CGAffineTransform(scaleX: zoomScale, y: zoomScale)
        contentView.center = CGPoint(x: videoFrame.midX + panOffset.x, y: videoFrame.midY + panOffset.y)
    }

    /// Keeps the scaled content covering the viewport so panning never reveals black.
    private func clampOffset() {
        let maxX = max(0, (zoomScale - 1) * videoFrame.width / 2)
        let maxY = max(0, (zoomScale - 1) * videoFrame.height / 2)
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
        // A direct tap on the video also parks the trackpad cursor there.
        virtualCursor = p
        positionCursor()
        onInput?(.tap(at: p, button: .left))
        // Remember it: touching down again right here starts a drag.
        tapHoldDrag.noteTap(at: g.location(in: self))
    }

    /// Like `normalized`, but a point outside the video is pulled to its edge,
    /// so a drag that runs off the picture keeps tracking along the border.
    private func normalizedClamped(_ point: CGPoint) -> NormalizedPoint {
        let rect = videoRect
        guard rect.width > 0, rect.height > 0 else { return NormalizedPoint(x: 0, y: 0) }
        return NormalizedPoint(x: min(max((point.x - rect.minX) / rect.width, 0), 1),
                               y: min(max((point.y - rect.minY) / rect.height, 0), 1))
    }

    // MARK: Tap, then hold → drag

    @objc private func handleTapHoldDrag(_ g: TapHoldDragRecognizer) {
        let location = g.location(in: contentView)
        switch g.state {
        case .began:
            // Press where the finger came down, not where it had got to by the
            // time the drag was recognised, so a selection starts on target.
            let start = normalizedClamped(contentView.convert(g.startLocation, from: self))
            virtualCursor = start
            UIImpactFeedbackGenerator(style: .light).impactOccurred()
            onInput?(.drag(at: start, phase: .began))
            lastDragSend = 0
            fallthrough
        case .changed:
            // The host needs no more than a display's worth of positions.
            let now = CACurrentMediaTime()
            guard now - lastDragSend >= 1.0 / 60 else { return }
            lastDragSend = now
            let p = normalizedClamped(location)
            virtualCursor = p
            positionCursor()
            onInput?(.drag(at: p, phase: .moved))
        case .ended:
            onInput?(.drag(at: normalizedClamped(location), phase: .ended))
        case .cancelled:
            onInput?(.drag(at: normalizedClamped(location), phase: .cancelled))
        default:
            break
        }
    }

    // MARK: Trackpad (flex layout) → virtual cursor

    /// Trackpad points → video points (the pad is as wide as the video, so a
    /// finger across the pad crosses the picture; a little gain makes it feel
    /// like a laptop pad rather than a 1:1 slate). Hover-moves the host cursor
    /// when the server supports it; clicks always land at the cursor.
    private func moveCursor(dx: CGFloat, dy: CGFloat, hover: Bool = true) {
        let rect = videoRect
        guard rect.width > 0, rect.height > 0 else { return }
        let gain: CGFloat = 1.4 / zoomScale
        virtualCursor = NormalizedPoint(x: min(max(virtualCursor.x + Double(dx * gain / rect.width), 0), 1),
                                        y: min(max(virtualCursor.y + Double(dy * gain / rect.height), 0), 1))
        positionCursor()
        if hover, hoverEnabled { queueHover(virtualCursor) }
    }

    private func scrollAtCursor(dx: CGFloat, dy: CGFloat) {
        guard dx != 0 || dy != 0 else { return }
        let scale = videoRect.width > 0 ? contentView.bounds.width / videoRect.width : 1
        onInput?(.scroll(at: virtualCursor, dx: dx * scale, dy: dy * scale))
    }

    private func positionCursor() {
        guard !cursorView.isHidden else { return }
        let rect = videoRect
        cursorView.center = CGPoint(x: rect.minX + rect.width * virtualCursor.x,
                                    y: rect.minY + rect.height * virtualCursor.y)
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

    // MARK: Pointer hover → host cursor

    @objc private func handleHover(_ g: UIHoverGestureRecognizer) {
        guard hoverEnabled else { return }
        switch g.state {
        case .began, .changed:
            // Only over the video itself (not the letterbox), and never while a
            // touch gesture is in flight — a press-and-move is a drag.
            guard let p = normalized(g.location(in: contentView)) else { return }
            queueHover(p)
        default:
            break
        }
    }

    private func queueHover(_ p: NormalizedPoint) {
        pendingHover = p
        let elapsed = CACurrentMediaTime() - lastHoverTime
        if elapsed >= hoverInterval {
            flushHover()
        } else if hoverFlush == nil {
            let item = DispatchWorkItem { [weak self] in self?.flushHover() }
            hoverFlush = item
            DispatchQueue.main.asyncAfter(deadline: .now() + (hoverInterval - elapsed), execute: item)
        }
    }

    private func flushHover() {
        hoverFlush = nil
        guard let p = pendingHover else { return }
        pendingHover = nil
        lastHoverTime = CACurrentMediaTime()
        onInput?(.move(at: p))
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
            let anchorRel = CGPoint(x: pinchAnchor.x - videoFrame.midX, y: pinchAnchor.y - videoFrame.midY)
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

    /// Touches on the deck belong to it alone; a superview's recognizers would
    /// otherwise see them too and treat a two-finger tap as "toggle HUD".
    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
        guard let view = touch.view else { return true }
        return !view.isDescendant(of: deck)
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        // Hover tracks the pointer alongside everything; pinch and two-finger
        // pan work together; everything else stays exclusive.
        if gestureRecognizer is UIHoverGestureRecognizer || other is UIHoverGestureRecognizer { return true }
        return (gestureRecognizer is UIPinchGestureRecognizer && other is UIPanGestureRecognizer)
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

/// One finger: tap, then touch down again nearby and either hold briefly or
/// start moving. Recognised as a continuous gesture for the second touch.
///
/// It fails immediately when a touch is not preceded by a recent tap, so other
/// recognizers that wait for it (scroll, long press) are not delayed. A quick
/// second tap that lifts without moving also fails it, leaving the ordinary
/// double tap (double-click) alone.
final class TapHoldDragRecognizer: UIGestureRecognizer {
    /// How soon after the tap the second touch must land, and how close.
    /// `defaults write <bundle id> tapHoldDragInterval -float 0.6` lengthens
    /// the window for anyone who finds the default too quick.
    var maximumInterval: TimeInterval = {
        let custom = UserDefaults.standard.double(forKey: "tapHoldDragInterval")
        return custom > 0 ? custom : 0.4
    }()
    var maximumDistance: CGFloat = 48
    /// Holding still this long starts the drag; so does moving `slop` points.
    var holdDelay: TimeInterval = 0.16
    var slop: CGFloat = 8

    /// Where the second touch came down, in the recognizer's view.
    private(set) var startLocation: CGPoint = .zero

    private var lastTap: (time: TimeInterval, point: CGPoint)?
    private var holdTimer: Timer?

    /// Call when a single tap is delivered, with its location in the view.
    func noteTap(at point: CGPoint) {
        lastTap = (ProcessInfo.processInfo.systemUptime, point)
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent) {
        super.touchesBegan(touches, with: event)
        guard state == .possible, numberOfTouches == 1, let touch = touches.first, let tap = lastTap else {
            // A second finger, or no tap to follow: not this gesture.
            state = state == .possible ? .failed : .cancelled
            return
        }
        let point = touch.location(in: view)
        guard touch.timestamp - tap.time <= maximumInterval,
              hypot(point.x - tap.point.x, point.y - tap.point.y) <= maximumDistance else {
            state = .failed
            return
        }
        lastTap = nil   // one tap starts at most one drag
        startLocation = point
        holdTimer = Timer.scheduledTimer(withTimeInterval: holdDelay, repeats: false) { [weak self] _ in
            guard let self, self.state == .possible else { return }
            self.state = .began
        }
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent) {
        super.touchesMoved(touches, with: event)
        guard let touch = touches.first else { return }
        switch state {
        case .possible:
            let point = touch.location(in: view)
            if hypot(point.x - startLocation.x, point.y - startLocation.y) > slop { state = .began }
        case .began, .changed:
            state = .changed
        default:
            break
        }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent) {
        super.touchesEnded(touches, with: event)
        // Lifted before holding or moving: that was a double tap, not a drag.
        state = state == .possible ? .failed : .ended
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent) {
        super.touchesCancelled(touches, with: event)
        state = state == .possible ? .failed : .cancelled
    }

    override func reset() {
        super.reset()
        holdTimer?.invalidate()
        holdTimer = nil
    }
}
