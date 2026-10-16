// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Collections.Specialized;
using System.Drawing.Imaging;
using RemoteApp.Protocol;

namespace RemoteApp.WindowsCommon;

/// <summary>
/// Windows side of the shared clipboard (PROTOCOL.md §10): translates between
/// the Win32 clipboard (via WinForms, so call on an STA/UI thread) and the
/// platform-neutral <see cref="ClipboardContent"/>. Images are normalised to
/// PNG and text to UTF-8; received files are written to a folder and put on
/// the clipboard as a file drop so they can be pasted into Explorer.
/// </summary>
public static class ClipboardBridge
{
    /// <summary>Largest total payload read from the local clipboard to send.</summary>
    private const long MaxSendBytes = 200L * 1024 * 1024;

    /// <summary>Where received files land: Downloads\RemoteApp.</summary>
    public static readonly string ReceivedDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RemoteApp");

    /// <summary>The current clipboard as neutral items: files, else an image, plus text.</summary>
    public static ClipboardContent Read()
    {
        var items = new List<ClipboardItem>();
        long total = 0;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                foreach (var path in Clipboard.GetFileDropList().Cast<string>())
                {
                    if (!File.Exists(path)) continue;
                    var info = new FileInfo(path);
                    if (total + info.Length > MaxSendBytes) break;
                    total += info.Length;
                    items.Add(new ClipboardItem(ClipboardKind.File, MimeType(info.Extension), info.Name, File.ReadAllBytes(path)));
                }
            }
            if (items.Count == 0 && Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                using (image)
                using (var stream = new MemoryStream())
                {
                    image.Save(stream, ImageFormat.Png);
                    items.Add(new ClipboardItem(ClipboardKind.Image, "image/png", "clipboard.png", stream.ToArray()));
                }
            }
            if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } text)
                items.Add(ClipboardItem.FromText(text));
        }
        catch (Exception)
        {
            // The clipboard can be locked by another app; treat as empty.
        }
        return new ClipboardContent(items);
    }

    /// <summary>Files picked by the user, as file items.</summary>
    public static ClipboardContent FromFiles(IEnumerable<string> paths)
    {
        var items = new List<ClipboardItem>();
        long total = 0;
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists || total + info.Length > MaxSendBytes) continue;
            total += info.Length;
            items.Add(new ClipboardItem(ClipboardKind.File, MimeType(info.Extension), info.Name, File.ReadAllBytes(path)));
        }
        return new ClipboardContent(items);
    }

    /// <summary>
    /// Puts received content on this PC's clipboard. Text and images paste
    /// directly; files (and images) are also saved to <see cref="ReceivedDirectory"/>.
    /// Returns the paths of saved files.
    /// </summary>
    public static List<string> Apply(ClipboardContent content)
    {
        var saved = new List<string>();
        var data = new DataObject();
        var any = false;

        if (content.Text is { Length: > 0 } text)
        {
            data.SetText(text);
            any = true;
        }
        var imageItem = content.Items.FirstOrDefault(i => i.Kind == ClipboardKind.Image);
        if (imageItem != null)
        {
            try
            {
                using var stream = new MemoryStream(imageItem.Data);
                using var image = Image.FromStream(stream);
                data.SetImage(new Bitmap(image));
                any = true;
            }
            catch (Exception) { /* not a decodable image; still saved as a file below */ }
        }

        var files = new StringCollection();
        foreach (var item in content.Items.Where(i => i.Kind is ClipboardKind.File or ClipboardKind.Image))
        {
            var path = WriteReceivedFile(item);
            if (path == null) continue;
            saved.Add(path);
            files.Add(path);
        }
        if (files.Count > 0)
        {
            data.SetFileDropList(files);
            any = true;
        }

        if (any)
        {
            try { Clipboard.SetDataObject(data, true); }
            catch (Exception) { /* clipboard busy */ }
        }
        return saved;
    }

    private static string? WriteReceivedFile(ClipboardItem item)
    {
        try
        {
            Directory.CreateDirectory(ReceivedDirectory);
            var name = string.IsNullOrWhiteSpace(item.Name) ? (item.Kind == ClipboardKind.Image ? "image.png" : "file") : item.Name!;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var path = Path.Combine(ReceivedDirectory, name);
            if (File.Exists(path))
            {
                var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                path = Path.Combine(ReceivedDirectory,
                    $"{Path.GetFileNameWithoutExtension(name)}-{stamp}{Path.GetExtension(name)}");
            }
            File.WriteAllBytes(path, item.Data);
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string MimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        ".md" => "text/markdown",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".mp4" => "video/mp4",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        _ => "application/octet-stream",
    };
}
