// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteApp.Protocol;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;

namespace RemoteApp.Server.Hosting;

/// <summary>
/// Installed apps that can be launched: everything the Start menu lists, read
/// from the shell's <c>shell:AppsFolder</c> (classic shortcuts and Store/MSIX
/// apps alike). The item's shell path (an AppUserModelID or a parsing name)
/// doubles as the launch identifier; launching goes through the shell exactly
/// like a Start menu click. Falls back to scanning Start Menu shortcuts if the
/// shell folder cannot be read.
/// </summary>
internal static class LaunchableApps
{
    private const string AppsFolder = "shell:AppsFolder";

    private static readonly string[] ShortcutRoots =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
    ];

    private static readonly Dictionary<string, byte[]?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<LaunchableApp> Fetch()
    {
        var apps = new Dictionary<string, LaunchableApp>(StringComparer.CurrentCultureIgnoreCase);
        RunOnStaThread(() =>
        {
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                dynamic folder = shell.NameSpace(AppsFolder);
                if (folder == null) return;
                foreach (var item in folder.Items())
                {
                    string name = item.Name;
                    string path = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                    if (apps.ContainsKey(name)) continue;
                    apps[name] = new LaunchableApp(path, name, Icon(path));
                }
            }
            catch (Exception)
            {
                // Shell automation unavailable: fall back to the shortcut folders below.
            }
        });
        if (apps.Count == 0) FetchShortcuts(apps);
        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void FetchShortcuts(Dictionary<string, LaunchableApp> apps)
    {
        foreach (var root in ShortcutRoots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch (Exception) { continue; }
            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) || apps.ContainsKey(name)) continue;
                apps[name] = new LaunchableApp(file, name, ShareableApps.Icon(file));
            }
        }
    }

    /// <summary>The Shell automation objects are apartment-threaded; give them an STA.</summary>
    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }

    /// <summary>
    /// Launches an app by its AppsFolder path (or a shortcut file) and waits
    /// (up to ~8 s) for a new top-level window. Returns its process ID, or null.
    /// </summary>
    public static uint? Launch(string identifier, Action<string> log)
    {
        var before = new HashSet<uint>();
        foreach (var hwnd in WindowControl.TopLevelWindows()) before.Add(WindowControl.ProcessId(hwnd));

        int? startedPid = null;
        try
        {
            var start = File.Exists(identifier)
                ? new ProcessStartInfo(identifier) { UseShellExecute = true }
                : new ProcessStartInfo("explorer.exe", $"\"{AppsFolder}\\{identifier}\"") { UseShellExecute = true };
            using var process = Process.Start(start);
            startedPid = process?.Id;
        }
        catch (Exception e)
        {
            log($"launch failed: {e.Message}");
            return null;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            Thread.Sleep(200);
            if (startedPid is { } pid && !before.Contains((uint)pid) && WindowControl.MainWindow((uint)pid) != null) return (uint)pid;
            foreach (var hwnd in WindowControl.TopLevelWindows())
            {
                var owner = WindowControl.ProcessId(hwnd);
                if (!before.Contains(owner) && owner != (uint)Environment.ProcessId) return owner;
            }
        }
        return null;
    }

    /// <summary>The shell's icon for an AppsFolder item or file, as a 48 px PNG (works for Store apps too).</summary>
    public static unsafe byte[]? Icon(string shellPath)
    {
        lock (IconCache)
        {
            if (IconCache.TryGetValue(shellPath, out var cached)) return cached;
        }
        byte[]? png = null;
        try
        {
            var parsingName = File.Exists(shellPath) ? shellPath : $"{AppsFolder}\\{shellPath}";
            PInvoke.SHCreateItemFromParsingName(parsingName, null, out IShellItemImageFactory factory).ThrowOnFailure();
            try
            {
                factory.GetImage(new SIZE(48, 48), SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK, out var hbitmap);
                using (hbitmap)   // the safe handle deletes the GDI object
                {
                    png = PngFromHBitmap(hbitmap.DangerousGetHandle());
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(factory);
            }
        }
        catch (Exception) { png = null; }
        lock (IconCache)
        {
            if (IconCache.Count > 1000) IconCache.Clear();
            IconCache[shellPath] = png;
        }
        return png;
    }

    /// <summary>Copies a 32-bit DIB section (with alpha) into a PNG; Image.FromHbitmap would drop the alpha.</summary>
    private static unsafe byte[]? PngFromHBitmap(IntPtr hbitmap)
    {
        BITMAP info;
        if (PInvoke.GetObject(new HGDIOBJ(hbitmap), sizeof(BITMAP), &info) == 0 || info.bmBits == null || info.bmBitsPixel != 32) return null;
        var height = Math.Abs(info.bmHeight);
        using var bitmap = new Bitmap(info.bmWidth, height, info.bmWidthBytes, PixelFormat.Format32bppPArgb, (IntPtr)info.bmBits);
        using var copy = new Bitmap(bitmap);   // detach from the DIB memory
        if (info.bmHeight > 0) copy.RotateFlip(RotateFlipType.RotateNoneFlipY);   // bottom-up DIB
        using var stream = new MemoryStream();
        copy.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
