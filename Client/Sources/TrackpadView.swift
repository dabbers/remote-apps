// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import UIKit

/// A laptop-style trackpad for the lower half of a half-open foldable (iPhone
/// Duo in "flex" posture): the stream sits on the upright half, this on the
/// flat one. One finger moves a virtual cursor over the video, a tap clicks
/// where it points, two fingers scroll, a two-finger tap is a secondary click,
/// and press-and-hold grabs (a haptic tick) so moving then drags until the
/// finger lifts. Deltas are in this view's points; the stream view converts
/// them to video coordinates.
final class TrackpadView: UIView {
    var onMove: ((_ dx: CGFloat, _ dy: CGFloat) -> Void)?
    var onTap: (() -> Void)?
    var onSecondaryTap: (() -> Void)?
    var onScroll: ((_ dx: CGFloat, _ dy: CGFloat) -> Void)?
    var onDragBegan: (() -> Void)?
    var onDragMoved: ((_ dx: CGFloat, _ dy: CGFloat) -> Void)?
    var onDragEnded: (() -> Void)?

    private let surface = UIView()
    private let hint = UILabel()
    private var lastMovePoint: CGPoint = .zero
    private var lastScrollPoint: CGPoint = .zero
    private var lastDragPoint: CGPoint = .zero
    private var dragging = false
    private let haptic = UIImpactFeedbackGenerator(style: .medium)

    override init(frame: CGRect) {
        super.init(frame: frame)
        backgroundColor = UIColor(white: 0.07, alpha: 1)

        surface.backgroundColor = UIColor(white: 0.13, alpha: 1)
        surface.layer.cornerRadius = 18
        surface.layer.cornerCurve = .continuous
        surface.layer.borderWidth = 1
        surface.layer.borderColor = UIColor(white: 1, alpha: 0.12).cgColor
        addSubview(surface)

        hint.text = "tap: click · hold, then move: drag · two fingers: scroll · two-finger tap: right-click"
        hint.font = .preferredFont(forTextStyle: .caption2)
        hint.textColor = UIColor(white: 1, alpha: 0.35)
        hint.textAlignment = .center
        surface.addSubview(hint)

        let move = UIPanGestureRecognizer(target: self, action: #selector(handleMove))
        move.minimumNumberOfTouches = 1
        move.maximumNumberOfTouches = 1
        let scroll = UIPanGestureRecognizer(target: self, action: #selector(handleScroll))
        scroll.minimumNumberOfTouches = 2
        scroll.maximumNumberOfTouches = 2
        let tap = UITapGestureRecognizer(target: self, action: #selector(handleTap))
        tap.numberOfTouchesRequired = 1
        let twoFingerTap = UITapGestureRecognizer(target: self, action: #selector(handleSecondary))
        twoFingerTap.numberOfTouchesRequired = 2
        // Press-and-hold grabs; the same recognizer then tracks the drag, so
        // the one-finger pan stands down while a drag is in progress.
        let hold = UILongPressGestureRecognizer(target: self, action: #selector(handleHold))
        hold.minimumPressDuration = 0.4
        hold.allowableMovement = 12
        for g in [move, scroll, tap, twoFingerTap, hold] { addGestureRecognizer(g) }
    }

    required init?(coder: NSCoder) { fatalError() }

    override func layoutSubviews() {
        super.layoutSubviews()
        let inset: CGFloat = 14
        surface.frame = bounds.inset(by: UIEdgeInsets(top: inset, left: inset + safeAreaInsets.left,
                                                      bottom: inset + safeAreaInsets.bottom, right: inset + safeAreaInsets.right))
        hint.frame = CGRect(x: 0, y: surface.bounds.height - 26, width: surface.bounds.width, height: 18)
    }

    @objc private func handleMove(_ g: UIPanGestureRecognizer) {
        let p = g.location(in: self)
        switch g.state {
        case .began:
            lastMovePoint = p
        case .changed:
            guard !dragging else { return }
            onMove?(p.x - lastMovePoint.x, p.y - lastMovePoint.y)
            lastMovePoint = p
        default:
            break
        }
    }

    @objc private func handleScroll(_ g: UIPanGestureRecognizer) {
        let p = g.location(in: self)
        switch g.state {
        case .began:
            lastScrollPoint = p
        case .changed:
            onScroll?(p.x - lastScrollPoint.x, p.y - lastScrollPoint.y)
            lastScrollPoint = p
        default:
            break
        }
    }

    @objc private func handleTap() { onTap?() }
    @objc private func handleSecondary() { onSecondaryTap?() }
    @objc private func handleHold(_ g: UILongPressGestureRecognizer) {
        let p = g.location(in: self)
        switch g.state {
        case .began:
            dragging = true
            lastDragPoint = p
            haptic.impactOccurred()
            surface.layer.borderColor = UIColor.tintColor.cgColor
            onDragBegan?()
        case .changed:
            guard dragging else { return }
            onDragMoved?(p.x - lastDragPoint.x, p.y - lastDragPoint.y)
            lastDragPoint = p
        case .ended, .cancelled, .failed:
            guard dragging else { return }
            dragging = false
            surface.layer.borderColor = UIColor(white: 1, alpha: 0.12).cgColor
            onDragEnded?()
        default:
            break
        }
    }
}
