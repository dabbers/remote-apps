// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RemoteApp.Server.Hosting;

/// <summary>
/// Top-level window queries and control. All rectangles are in physical
/// screen pixels (the process is per-monitor DPI aware); "points" are DIPs at
/// the window's monitor DPI, which is what the protocol carries.
/// </summary>
internal static class WindowControl
{
    public static string Title(HWND hwnd)
    {
        var length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0) return "";
        Span<char> buffer = stackalloc char[length + 1];
        var copied = PInvoke.GetWindowText(hwnd, buffer);
        return copied > 0 ? new string(buffer[..copied]) : "";
    }

    public static string ClassName(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        var copied = PInvoke.GetClassName(hwnd, buffer);
        return copied > 0 ? new string(buffer[..copied]) : "";
    }

    public static uint ProcessId(HWND hwnd)
    {
        PInvoke.GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static unsafe bool IsCloaked(HWND hwnd)
    {
        uint cloaked = 0;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        return hr.Succeeded && cloaked != 0;
    }

    /// <summary>A normal, visible, titled, top-level app window (not a tool window, not cloaked).</summary>
    public static bool IsCandidate(HWND hwnd)
    {
        if (!PInvoke.IsWindowVisible(hwnd)) return false;
        if (PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) != hwnd) return false;
        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW)) return false;
        if (IsCloaked(hwnd)) return false;
        if (PInvoke.GetWindowTextLength(hwnd) == 0) return false;
        var cls = ClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return false;
        var bounds = VisibleBounds(hwnd);
        return bounds.Width >= 100 && bounds.Height >= 60;
    }

    /// <summary>All candidate top-level windows, in Z order (front first).</summary>
    public static List<HWND> TopLevelWindows()
    {
        var windows = new List<HWND>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            if (IsCandidate(hwnd)) windows.Add(hwnd);
            return true;
        }, default);
        return windows;
    }

    /// <summary>The process's main window: its foreground window if any, else its largest.</summary>
    public static HWND? MainWindow(uint pid)
    {
        var foreground = PInvoke.GetForegroundWindow();
        HWND? best = null;
        long bestArea = -1;
        foreach (var hwnd in TopLevelWindows())
        {
            if (ProcessId(hwnd) != pid) continue;
            if (hwnd == foreground) return hwnd;
            var bounds = VisibleBounds(hwnd);
            long area = (long)bounds.Width * bounds.Height;
            if (area > bestArea) { bestArea = area; best = hwnd; }
        }
        return best;
    }

    /// <summary>Window rectangle including the invisible resize borders.</summary>
    public static Rectangle WindowRect(HWND hwnd)
    {
        PInvoke.GetWindowRect(hwnd, out var rect);
        return new Rectangle(rect.left, rect.top, rect.Width, rect.Height);
    }

    /// <summary>What the user sees: the DWM frame bounds (no invisible borders).</summary>
    public static unsafe Rectangle VisibleBounds(HWND hwnd)
    {
        RECT rect;
        var hr = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(RECT));
        if (hr.Failed) return WindowRect(hwnd);
        return new Rectangle(rect.left, rect.top, rect.Width, rect.Height);
    }

    /// <summary>
    /// Moves and resizes so the visible bounds become the requested rectangle,
    /// then returns the bounds the app actually settled on (apps clamp to their
    /// minimum sizes). A maximized window is restored first so it can be sized.
    /// </summary>
    public static Rectangle SetVisibleBounds(HWND hwnd, Rectangle target)
    {
        if (PInvoke.IsIconic(hwnd) || PInvoke.IsZoomed(hwnd)) PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        var window = WindowRect(hwnd);
        var visible = VisibleBounds(hwnd);
        var leftBorder = visible.Left - window.Left;
        var topBorder = visible.Top - window.Top;
        var extraWidth = window.Width - visible.Width;
        var extraHeight = window.Height - visible.Height;
        PInvoke.SetWindowPos(hwnd, HWND.Null,
            target.X - leftBorder, target.Y - topBorder, target.Width + extraWidth, target.Height + extraHeight,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        Thread.Sleep(50);   // some apps apply the size asynchronously
        return VisibleBounds(hwnd);
    }

    public static bool IsForeground(HWND hwnd) => PInvoke.GetForegroundWindow() == hwnd;

    public static bool IsForegroundProcess(uint pid)
    {
        var foreground = PInvoke.GetForegroundWindow();
        return foreground != HWND.Null && ProcessId(foreground) == pid;
    }

    /// <summary>
    /// Brings the window to the front. SetForegroundWindow is restricted for
    /// background processes, so attach to the foreground thread's input queue
    /// first (the classic workaround), then raise.
    /// </summary>
    public static void Activate(HWND hwnd)
    {
        if (PInvoke.IsIconic(hwnd)) PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        var foreground = PInvoke.GetForegroundWindow();
        var ourThread = PInvoke.GetCurrentThreadId();
        var foregroundThread = foreground != HWND.Null ? PInvoke.GetWindowThreadProcessId(foreground) : 0;
        var attached = foregroundThread != 0 && foregroundThread != ourThread && PInvoke.AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            PInvoke.BringWindowToTop(hwnd);
            PInvoke.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) PInvoke.AttachThreadInput(ourThread, foregroundThread, false);
        }
    }

    public static HMONITOR MonitorOf(HWND hwnd) => PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

    public static HMONITOR PrimaryMonitor() => PInvoke.MonitorFromPoint(new Point(0, 0), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);

    /// <summary>DPI scale (1.0 = 96 dpi) of a monitor.</summary>
    public static double Scale(HMONITOR monitor)
    {
        var hr = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _);
        return hr.Succeeded && dpiX > 0 ? dpiX / 96.0 : 1.0;
    }

    public static double Scale(HWND hwnd)
    {
        var dpi = PInvoke.GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : Scale(MonitorOf(hwnd));
    }

    /// <summary>The monitor's work area (excluding the taskbar), in physical pixels.</summary>
    public static Rectangle WorkArea(HMONITOR monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info)) return Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        return new Rectangle(info.rcWork.left, info.rcWork.top, info.rcWork.Width, info.rcWork.Height);
    }

    public static bool IsAlive(HWND hwnd) => PInvoke.IsWindow(hwnd);
}
