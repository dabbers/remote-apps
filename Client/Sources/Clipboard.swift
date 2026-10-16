// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import UIKit
import UniformTypeIdentifiers
import RemoteAppProtocol

/// iOS side of the shared clipboard: translate between `UIPasteboard` / picked
/// files and the platform-neutral `ClipboardContent`, and help save items that
/// arrive from the Mac. The wire format is OS-agnostic (see `ClipboardItem`),
/// so the same messages serve a future Windows peer.
enum Clipboard {
    /// The current iOS clipboard as neutral items (text and image).
    static func current() -> ClipboardContent {
        let pb = UIPasteboard.general
        var items: [ClipboardItem] = []
        if pb.hasImages, let image = pb.image, let png = image.pngData() {
            items.append(ClipboardItem(kind: .image, mimeType: "image/png", name: "clipboard.png", data: png))
        }
        if pb.hasStrings, let s = pb.string, !s.isEmpty {
            items.append(.text(s))
        }
        return ClipboardContent(items: items)
    }

    /// A picked photo becomes a file item, so it lands on the Mac as a real file
    /// (and on its pasteboard as a file URL) — "copy the photo over".
    static func fileItem(data: Data, name: String) -> ClipboardItem {
        ClipboardItem(kind: .file, mimeType: mimeType(forName: name), name: name, data: data)
    }

    /// Put text/images from the Mac directly onto the iOS clipboard so Paste
    /// works immediately. Returns the items worth offering to save (files and
    /// images), which the UI presents via a share/save sheet.
    @discardableResult
    static func apply(_ content: ClipboardContent) -> [ClipboardItem] {
        if let text = content.text { UIPasteboard.general.string = text }
        if let imageItem = content.items.first(where: { $0.kind == .image }),
           let image = UIImage(data: imageItem.data) {
            UIPasteboard.general.image = image
        }
        return content.items.filter { $0.kind == .file || $0.kind == .image }
    }

    /// Write an item to a temp file so it can be shared (Save to Files) or saved.
    static func writeTemp(_ item: ClipboardItem) -> URL? {
        let name = (item.name?.isEmpty == false ? item.name! : defaultName(for: item))
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(name)
        do { try item.data.write(to: url); return url } catch { return nil }
    }

    private static func defaultName(for item: ClipboardItem) -> String {
        switch item.kind {
        case .image: return "image.png"
        case .text: return "text.txt"
        case .file: return "file"
        }
    }

    static func mimeType(forName name: String) -> String {
        let ext = (name as NSString).pathExtension
        guard !ext.isEmpty, let type = UTType(filenameExtension: ext),
              let mime = type.preferredMIMEType else { return "application/octet-stream" }
        return mime
    }
}
