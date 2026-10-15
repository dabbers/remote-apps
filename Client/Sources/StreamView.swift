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
        return view
    }

    func updateUIView(_ view: StreamUIView, context: Context) {
        if model.keyboardVisible, !view.isFirstResponder {
            view.becomeFirstResponder()
        } else if !model.keyboardVisible, view.isFirstResponder {
            view.resignFirstResponder()
        }
    }
}

final class StreamUIView: UIView, UIScrollViewDelegate, UIGestureRecognizerDelegate, UIKeyInput {
    var onInput: ((InputEvent) -> Void)?
    /// Called on a two-finger tap: shows or hides the on-screen controls.
    var onToggleHUD: (() -> Void)?

    private let renderer: VideoRenderer
    private let scrollView = UIScrollView()
    private let contentView = UIView()
    private var lastPanPoint: CGPoint = .zero

    init(renderer: VideoRenderer) {
        self.renderer = renderer
        super.init(frame: .zero)
        backgroundColor = .black

        scrollView.delegate = self
        scrollView.minimumZoomScale = 1
        scrollView.maximumZoomScale = 4
        scrollView.bouncesZoom = true
        scrollView.showsVerticalScrollIndicator = false
        scrollView.showsHorizontalScrollIndicator = false
        scrollView.contentInsetAdjustmentBehavior = .never
        // Two fingers pan the zoomed view; one finger is forwarded as a remote scroll.
        scrollView.panGestureRecognizer.minimumNumberOfTouches = 2
        addSubview(scrollView)

        contentView.backgroundColor = .black
        contentView.layer.addSublayer(renderer.layer)
        scrollView.addSubview(contentView)

        let tap = UITapGestureRecognizer(target: self, action: #selector(handleTap))
        tap.numberOfTapsRequired = 1
        let longPress = UILongPressGestureRecognizer(target: self, action: #selector(handleLongPress))
        longPress.minimumPressDuration = 0.45
        let pan = UIPanGestureRecognizer(target: self, action: #selector(handlePan))
        pan.maximumNumberOfTouches = 1
        for g in [tap, longPress, pan] {
            g.delegate = self
            contentView.addGestureRecognizer(g)
        }

        // Two-finger tap reveals/hides the controls, without stealing single-finger
        // taps (forwarded as clicks). A two-finger drag still pans the zoomed view.
        let twoFingerTap = UITapGestureRecognizer(target: self, action: #selector(handleTwoFingerTap))
        twoFingerTap.numberOfTouchesRequired = 2
        twoFingerTap.delegate = self
        addGestureRecognizer(twoFingerTap)

        renderer.onVideoSizeChange = { [weak self] _ in self?.setNeedsLayout() }
    }

    required init?(coder: NSCoder) { fatalError() }

    override func layoutSubviews() {
        super.layoutSubviews()
        scrollView.frame = bounds
        if scrollView.zoomScale == 1 {
            scrollView.contentSize = bounds.size
            contentView.frame = CGRect(origin: .zero, size: bounds.size)
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        renderer.layer.frame = contentView.bounds
        CATransaction.commit()
    }

    // MARK: Coordinate mapping

    /// The rectangle the video actually occupies inside the content view (letterboxed).
    private var videoRect: CGRect {
        let size = renderer.videoSize
        guard size.width > 0, size.height > 0 else { return contentView.bounds }
        return AVMakeRect(aspectRatio: size, insideRect: contentView.bounds)
    }

    private func normalized(_ point: CGPoint) -> NormalizedPoint? {
        let rect = videoRect
        guard rect.contains(point) else { return nil }
        return NormalizedPoint(x: (point.x - rect.minX) / rect.width, y: (point.y - rect.minY) / rect.height)
    }

    // MARK: Gestures

    @objc private func handleTwoFingerTap() {
        onToggleHUD?()
    }

    @objc private func handleTap(_ g: UITapGestureRecognizer) {
        guard let p = normalized(g.location(in: contentView)) else { return }
        onInput?(.tap(at: p, button: .left))
    }

    @objc private func handleLongPress(_ g: UILongPressGestureRecognizer) {
        guard g.state == .began, let p = normalized(g.location(in: contentView)) else { return }
        onInput?(.longPress(at: p))
    }

    @objc private func handlePan(_ g: UIPanGestureRecognizer) {
        let location = g.location(in: contentView)
        switch g.state {
        case .began:
            lastPanPoint = location
        case .changed:
            let dx = (location.x - lastPanPoint.x) / scrollView.zoomScale
            let dy = (location.y - lastPanPoint.y) / scrollView.zoomScale
            lastPanPoint = location
            guard let p = normalized(location), dx != 0 || dy != 0 else { return }
            // Scale client points to host points so one finger-length scrolls about the same distance.
            let hostPerClient = renderer.videoSize.width > 0 ? (videoRect.width > 0 ? 1 / (videoRect.width / contentView.bounds.width) : 1) : 1
            onInput?(.scroll(at: p, dx: dx * hostPerClient, dy: dy * hostPerClient))
        default:
            break
        }
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        // Let the pinch zoom proceed alongside our recognizers.
        other is UIPinchGestureRecognizer
    }

    // MARK: UIScrollViewDelegate

    func viewForZooming(in scrollView: UIScrollView) -> UIView? { contentView }

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

    func insertText(_ text: String) {
        if text == "\n" {
            onInput?(.key(KeyEvent(keyCode: KeyEvent.returnKey)))
        } else if text == "\t" {
            onInput?(.key(KeyEvent(keyCode: KeyEvent.tabKey)))
        } else {
            onInput?(.key(KeyEvent(text: text)))
        }
    }

    func deleteBackward() {
        onInput?(.key(KeyEvent(keyCode: KeyEvent.deleteKey)))
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
