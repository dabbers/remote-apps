// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using RemoteApp.Protocol;
using RemoteApp.WindowsCommon;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Shell;

namespace RemoteApp.Server.Hosting;

/// <summary>Enumerates running apps that have at least one normal on-screen window.</summary>
internal static class ShareableApps
{
    public sealed record Entry(AppInfo Info, HWND MainWindow, string? ExePath);

    private static readonly Dictionary<string, byte[]?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static List<Entry> Fetch()
    {
        var ourPid = (uint)Environment.ProcessId;
        var byPid = new Dictionary<uint, List<HWND>>();
        foreach (var hwnd in WindowControl.TopLevelWindows())
        {
            var pid = WindowControl.ProcessId(hwnd);
            if (pid == ourPid || pid == 0) continue;
            if (!byPid.TryGetValue(pid, out var list)) byPid[pid] = list = [];
            list.Add(hwnd);
        }

        var entries = new List<Entry>();
        foreach (var (pid, windows) in byPid)
        {
            string? exePath = null;
            var name = "";
            try
            {
                using var process = Process.GetProcessById((int)pid);
                name = process.ProcessName;
                try { exePath = process.MainModule?.FileName; }
                catch (Exception) { /* elevated process: no module access */ }
                if (exePath != null)
                {
                    var description = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
                    if (!string.IsNullOrWhiteSpace(description)) name = description.Trim();
                }
            }
            catch (Exception)
            {
                continue;
            }
            // UWP apps are hosted by ApplicationFrameHost; the window title is the app.
            if (string.Equals(name, "Application Frame Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                name = WindowControl.Title(windows[0]);

            // Z order from EnumWindows: the first is frontmost.
            var main = WindowControl.MainWindow(pid) ?? windows[0];
            var bundle = exePath != null ? Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant() + ".exe" : name;
            entries.Add(new Entry(
                new AppInfo((int)pid, bundle, name, windows.Count, exePath != null ? Icon(exePath) : null),
                main, exePath));
        }
        entries.Sort((a, b) => string.Compare(a.Info.Name, b.Info.Name, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }

    /// <summary>The shell's large icon for a file (exe or shortcut), as a 48 px PNG, cached per path.</summary>
    public static byte[]? Icon(string path)
    {
        lock (IconCache)
        {
            if (IconCache.TryGetValue(path, out var cached)) return cached;
        }
        byte[]? png = null;
        try
        {
            var info = new SHFILEINFOW();
            var result = PInvoke.SHGetFileInfo(path, (FILE_FLAGS_AND_ATTRIBUTES)0, ref info,
                SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_LARGEICON);
            if (result != 0 && !info.hIcon.IsNull)
            {
                try
                {
                    using var icon = System.Drawing.Icon.FromHandle(info.hIcon);
                    png = IconHelper.PngFromIcon(icon);
                }
                finally
                {
                    PInvoke.DestroyIcon(info.hIcon);
                }
            }
        }
        catch (Exception) { png = null; }
        lock (IconCache)
        {
            if (IconCache.Count > 500) IconCache.Clear();
            IconCache[path] = png;
        }
        return png;
    }
}
