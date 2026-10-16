// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteApp.WindowsCommon;

/// <summary>PNG ↔ GDI icon conversions for the app lists and window icons.</summary>
public static partial class IconHelper
{
    /// <summary>Decodes a PNG (an app icon from the wire), or null.</summary>
    public static Bitmap? BitmapFromPng(byte[]? png)
    {
        if (png == null || png.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(png);
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A window icon from a PNG, or null.</summary>
    public static Icon? IconFromPng(byte[]? png)
    {
        using var bitmap = BitmapFromPng(png);
        if (bitmap == null) return null;
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    /// <summary>Encodes an icon as a square PNG of the given size (for the app lists on the wire).</summary>
    public static byte[]? PngFromIcon(Icon? icon, int size = 48)
    {
        if (icon == null) return null;
        try
        {
            using var source = icon.ToBitmap();
            using var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.Clear(Color.Transparent);
                graphics.DrawImage(source, 0, 0, size, size);
            }
            using var stream = new MemoryStream();
            scaled.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
