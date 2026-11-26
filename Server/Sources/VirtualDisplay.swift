// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import AppKit
import CoreGraphics

/// Gives the host a display to stream from when no monitor is attached.
///
/// Capture is a region of a display (see WindowCapture), so a headless Mac has
/// nothing to capture. This creates a software display with the private
/// CGVirtualDisplay API (CGVirtualDisplayPrivate.h); streamed windows are placed
/// on it. The display lives as long as this process holds the object: it goes
/// away when the mode is turned off or the server quits.
@MainActor
final class VirtualDisplayManager {
    enum Mode: String, CaseIterable {
        /// Never create one.
        case off
        /// Create one only while no real monitor is online.
        case auto
        /// Always create one and stream from it, even with a monitor attached.
        case always
    }

    static let shared = VirtualDisplayManager()

    var onLog: ((String) -> Void)?
    /// Called after the display is created or removed.
    var onChange: (() -> Void)?

    private(set) var mode: Mode
    private var display: CGVirtualDisplay?
    private var observer: NSObjectProtocol?

    private static let modeKey = "virtualDisplayMode"

    /// The virtual display, when one exists.
    var displayID: CGDirectDisplayID? { display?.displayID }

    /// Size in points; override with
    /// `defaults write <bundle id> virtualDisplayWidth -int 3008` (and Height).
    private var pointSize: (width: Int, height: Int) {
        let d = UserDefaults.standard
        let w = d.integer(forKey: "virtualDisplayWidth")
        let h = d.integer(forKey: "virtualDisplayHeight")
        return (w >= 640 ? w : 2560, h >= 480 ? h : 1600)
    }

    /// Retina (2x) unless `virtualDisplayHiDPI` is set to false.
    private var hiDPI: Bool {
        UserDefaults.standard.object(forKey: "virtualDisplayHiDPI") as? Bool ?? true
    }

    private init() {
        mode = UserDefaults.standard.string(forKey: Self.modeKey).flatMap(Mode.init(rawValue:)) ?? .auto
    }

    func setMode(_ newMode: Mode) {
        mode = newMode
        UserDefaults.standard.set(newMode.rawValue, forKey: Self.modeKey)
        reconcile()
    }

    /// Start following display changes (a monitor unplugged while running).
    func startObserving() {
        guard observer == nil else { return }
        observer = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.reconcile() }
        }
    }

    /// Creates or removes the display to match the mode. Idempotent.
    func reconcile() {
        switch mode {
        case .off:
            destroy()
        case .always:
            if display == nil { create() }
        case .auto:
            // Created when nothing else is online. It is deliberately kept if a
            // monitor appears later, so a running stream is not pulled off it.
            if display == nil, otherOnlineDisplayCount() == 0 { create() }
        }
    }

    /// Reconciles, then waits briefly for AppKit to see the new display.
    /// Returns the display streamed windows should be placed on, if any.
    func ready() async -> CGDirectDisplayID? {
        reconcile()
        guard let id = displayID else { return nil }
        for _ in 0..<40 where WindowControl.screen(displayID: id) == nil {   // up to ~2s
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        return WindowControl.screen(displayID: id) != nil ? id : nil
    }

    /// Online displays that are neither ours nor macOS's headless placeholder.
    ///
    /// With no monitor attached, an Apple silicon Mac still reports one
    /// 1920×1080 1x display whose vendor and model are the four-character codes
    /// 'unkn' and 'virt'. It is not a real monitor, so it does not count.
    private func otherOnlineDisplayCount() -> Int {
        var count: UInt32 = 0
        guard CGGetOnlineDisplayList(0, nil, &count) == .success, count > 0 else { return 0 }
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetOnlineDisplayList(count, &ids, &count) == .success else { return 0 }
        return ids.prefix(Int(count)).filter { id in
            let placeholder = CGDisplayVendorNumber(id) == 0x756E_6B6E && CGDisplayModelNumber(id) == 0x7669_7274
            return id != displayID && !placeholder
        }.count
    }

    private func create() {
        let size = pointSize
        let factor = hiDPI ? 2 : 1

        let descriptor = CGVirtualDisplayDescriptor()
        descriptor.setDispatchQueue(DispatchQueue.main)
        descriptor.name = "dabtop Virtual Display"
        descriptor.maxPixelsWide = UInt32(size.width * factor)
        descriptor.maxPixelsHigh = UInt32(size.height * factor)
        // ~110 points per inch, so macOS treats it like an ordinary desktop panel.
        descriptor.sizeInMillimeters = CGSize(width: Double(size.width) * 25.4 / 110, height: Double(size.height) * 25.4 / 110)
        descriptor.vendorID = 0xDAB0
        descriptor.productID = 0x0001
        descriptor.serialNum = 1
        descriptor.terminationHandler = { [weak self] _, _ in
            Task { @MainActor in
                guard let self, self.display != nil else { return }
                self.display = nil
                self.onLog?("virtual display was removed by the system")
                self.onChange?()
            }
        }

        guard let created = CGVirtualDisplay(descriptor: descriptor) else {
            onLog?("⚠︎ could not create a virtual display (CGVirtualDisplay unavailable on this macOS?)")
            return
        }
        let settings = CGVirtualDisplaySettings()
        settings.hiDPI = hiDPI ? 1 : 0
        settings.modes = [CGVirtualDisplayMode(width: UInt(size.width), height: UInt(size.height), refreshRate: 60)]
        guard created.apply(settings) else {
            onLog?("⚠︎ virtual display rejected \(size.width)×\(size.height)\(hiDPI ? " @2x" : "")")
            return
        }
        display = created
        onLog?("virtual display created: \(size.width)×\(size.height)\(hiDPI ? " @2x" : "") (id \(created.displayID))")
        onChange?()
    }

    private func destroy() {
        guard display != nil else { return }
        display = nil   // releasing the object removes the display
        onLog?("virtual display removed")
        onChange?()
    }
}
