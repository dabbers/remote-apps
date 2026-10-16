// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

import AppKit
import Foundation
import UniformTypeIdentifiers
import RemoteAppProtocol

/// Translates between the Mac's `NSPasteboard` and the platform-neutral
/// `ClipboardContent` on the wire. Images are normalized to PNG and text to
/// UTF-8 so a client on any OS can consume them; received files are written to
/// a folder and also placed on the pasteboard as file URLs.
enum ClipboardBridge {
    /// Where received files land, so they are real files the user can open and
    /// the pasteboard's file URLs point somewhere stable.
    static let receivedDirectory: URL = {
        let base = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        let dir = base.appendingPathComponent("RemoteApp", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()

    // MARK: Read (Mac clipboard → wire)

    @MainActor
    static func read() -> ClipboardContent {
        let pb = NSPasteboard.general
        var items: [ClipboardItem] = []

        // Files first: copying in Finder puts file URLs on the pasteboard.
        if let urls = pb.readObjects(forClasses: [NSURL.self]) as? [URL] {
            for url in urls where url.isFileURL {
                guard let data = try? Data(contentsOf: url) else { continue }
                items.append(ClipboardItem(kind: .file, mimeType: mimeType(forExtension: url.pathExtension),
                                           name: url.lastPathComponent, data: data))
            }
        }

        // An image on the pasteboard (e.g. copied from Preview/Photos), as PNG.
        if items.isEmpty, let png = imagePNG(from: pb) {
            items.append(ClipboardItem(kind: .image, mimeType: "image/png", name: "clipboard.png", data: png))
        }

        // Plain text — included alongside files when both are present.
        if let string = pb.string(forType: .string), !string.isEmpty {
            items.append(.text(string))
        }

        return ClipboardContent(items: items)
    }

    /// PNG bytes for an image on the pasteboard, or nil if there is no image.
    @MainActor
    private static func imagePNG(from pb: NSPasteboard) -> Data? {
        if let png = pb.data(forType: .png) { return png }
        guard let objs = pb.readObjects(forClasses: [NSImage.self]) as? [NSImage],
              let image = objs.first,
              let tiff = image.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }

    // MARK: Write (wire → Mac clipboard)

    @MainActor
    static func write(_ content: ClipboardContent) {
        var objects: [NSPasteboardWriting] = []
        for item in content.items {
            switch item.kind {
            case .text:
                if let s = item.text { objects.append(s as NSString) }
            case .image:
                if let image = NSImage(data: item.data) { objects.append(image) }
            case .file:
                if let url = writeReceivedFile(item) { objects.append(url as NSURL) }
            }
        }
        guard !objects.isEmpty else { return }
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.writeObjects(objects)
    }

    /// Persist a received file so the pasteboard can reference it by URL.
    private static func writeReceivedFile(_ item: ClipboardItem) -> URL? {
        let name = (item.name?.isEmpty == false ? item.name! : "file")
        var url = receivedDirectory.appendingPathComponent(name)
        // Avoid clobbering an existing file of the same name.
        if FileManager.default.fileExists(atPath: url.path) {
            let base = url.deletingPathExtension().lastPathComponent
            let ext = url.pathExtension
            let stamped = ext.isEmpty ? "\(base)-\(Int(Date().timeIntervalSince1970))"
                                      : "\(base)-\(Int(Date().timeIntervalSince1970)).\(ext)"
            url = receivedDirectory.appendingPathComponent(stamped)
        }
        do { try item.data.write(to: url); return url } catch { return nil }
    }

    private static func mimeType(forExtension ext: String) -> String {
        guard !ext.isEmpty, let type = UTType(filenameExtension: ext),
              let mime = type.preferredMIMEType else { return "application/octet-stream" }
        return mime
    }
}
