// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using RemoteApp.Protocol;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RemoteApp.Server.Hosting;

/// <summary>
/// Turns protocol input events into <c>SendInput</c> calls aimed at the
/// shared window. Normalised points map onto the window's visible bounds;
/// the window is brought to the foreground first (keyboard input only reaches
/// the foreground window). Events run on their own thread so sleeps never
/// block the network.
/// </summary>
internal sealed class InputInjector : IDisposable
{
    private readonly HWND _hwnd;
    private readonly uint _pid;
    private readonly BlockingCollection<InputEvent> _queue = new(new ConcurrentQueue<InputEvent>());
    private readonly Thread _thread;
    private Rectangle _bounds;
    private double _scrollScale = 1;
    private long _lastActivate;
    private bool _dragging;
    private double _wheelRemainder;
    private double _hwheelRemainder;

    /// <summary>Host pixels of content per wheel notch (Windows' usual 3 lines ≈ 48 px at 100%).</summary>
    private const double PixelsPerNotch = 48;

    /// <summary>Delay between injected keystrokes when typing a string (see <see cref="Key"/>).</summary>
    private const int TypingPaceMs = 6;

    /// <summary>Extra settle time after a Unicode (VK_PACKET) key, which XAML apps process slowly.</summary>
    private const int UnicodeSettleMs = 40;

    public event Action<string>? Log;

    public InputInjector(HWND hwnd, uint pid, Rectangle bounds)
    {
        _hwnd = hwnd;
        _pid = pid;
        _bounds = bounds;
        _thread = new Thread(Run) { IsBackground = true, Name = "remoteapp-input" };
        _thread.Start();
    }

    /// <summary>Visible bounds of the shared window, physical pixels.</summary>
    public Rectangle Bounds
    {
        set { lock (_queue) _bounds = value; }
    }

    private Rectangle CurrentBounds
    {
        get { lock (_queue) return _bounds; }
    }

    /// <summary>Host pixels per client point, for scroll distances.</summary>
    public double ScrollScale { set => Volatile.Write(ref _scrollScale, value); }

    public void Handle(InputEvent input)
    {
        try { _queue.Add(input); }
        catch (InvalidOperationException) { }
    }

    private void Run()
    {
        try
        {
            foreach (var input in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (!EnsureActive())
                    {
                        // Never type into whatever else happens to be in front.
                        if (Environment.TickCount64 - _lastDropLog > 2000)
                        {
                            _lastDropLog = Environment.TickCount64;
                            Log?.Invoke("dropping input: the shared window could not be brought to the foreground");
                        }
                        continue;
                    }
                    switch (input)
                    {
                        case TapEvent tap: Click(ScreenPoint(tap.At), tap.Button); break;
                        case LongPressEvent press: Click(ScreenPoint(press.At), MouseButton.Right); break;
                        case ScrollEvent scroll: Scroll(ScreenPoint(scroll.At), scroll.Dx * _scrollScale, scroll.Dy * _scrollScale); break;
                        case DragEvent drag: Drag(ScreenPoint(drag.At), drag.Phase); break;
                        case KeyInputEvent key: Key(key.Key); break;
                    }
                }
                catch (Exception e)
                {
                    Log?.Invoke($"input failed: {e.Message}");
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    private long _lastDropLog;

    /// <summary>
    /// An inactive window absorbs the first click just to become active and
    /// ignores keystrokes, so bring the app forward whenever it is not already.
    /// Returns false if the shared app still is not in the foreground, in which
    /// case the caller must drop the event rather than deliver it elsewhere.
    /// Throttled so a burst of events does not hammer activation.
    /// </summary>
    private bool EnsureActive()
    {
        if (WindowControl.IsForegroundProcess(_pid)) return true;
        var now = Environment.TickCount64;
        if (now - _lastActivate < 300) return false;
        _lastActivate = now;
        WindowControl.Activate(_hwnd);
        Thread.Sleep(120);
        var active = WindowControl.IsForegroundProcess(_pid);
        Log?.Invoke(active ? $"activated window of pid {_pid} for input" : $"could not activate window of pid {_pid}");
        return active;
    }

    private Point ScreenPoint(NormalizedPoint p)
    {
        var bounds = CurrentBounds;
        return new Point(
            bounds.X + (int)Math.Round(Math.Clamp(p.X, 0, 1) * (bounds.Width - 1)),
            bounds.Y + (int)Math.Round(Math.Clamp(p.Y, 0, 1) * (bounds.Height - 1)));
    }

    // MARK: Mouse

    private static INPUT MouseInput(MOUSE_EVENT_FLAGS flags, int dx = 0, int dy = 0, int data = 0)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_MOUSE };
        input.Anonymous.mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags, time = 0, dwExtraInfo = 0 };
        return input;
    }

    /// <summary>Absolute move across the virtual desktop (0–65535 per axis).</summary>
    private static INPUT Move(Point p)
    {
        var left = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var top = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        var width = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN));
        var height = Math.Max(1, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));
        var x = (int)Math.Round((p.X - left) * 65535.0 / (width - 1));
        var y = (int)Math.Round((p.Y - top) * 65535.0 / (height - 1));
        return MouseInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_VIRTUALDESK, x, y);
    }

    private static void Send(params INPUT[] inputs)
    {
        unsafe
        {
            PInvoke.SendInput(inputs, sizeof(INPUT));
        }
    }

    private void Click(Point p, MouseButton button)
    {
        var (down, up) = button switch
        {
            MouseButton.Right => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN, MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP),
        };
        Send(Move(p));
        Thread.Sleep(10);
        Send(MouseInput(down), MouseInput(up));
    }

    private void Scroll(Point p, double dxPixels, double dyPixels)
    {
        Send(Move(p));
        // Positive dy = content moves down = wheel forward (positive WHEEL_DELTA).
        _wheelRemainder += dyPixels / PixelsPerNotch * 120;
        var notches = (int)_wheelRemainder;
        if (notches != 0)
        {
            _wheelRemainder -= notches;
            Send(MouseInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_WHEEL, data: notches));
        }
        // Positive dx = content moves right = scroll left = negative horizontal wheel.
        _hwheelRemainder += -dxPixels / PixelsPerNotch * 120;
        var hNotches = (int)_hwheelRemainder;
        if (hNotches != 0)
        {
            _hwheelRemainder -= hNotches;
            Send(MouseInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_HWHEEL, data: hNotches));
        }
    }

    private void Drag(Point p, DragPhase phase)
    {
        switch (phase)
        {
            case DragPhase.Began:
                _dragging = true;
                Send(Move(p));
                Thread.Sleep(10);
                Send(MouseInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN));
                break;
            case DragPhase.Moved:
                if (_dragging) Send(Move(p));
                break;
            case DragPhase.Ended:
            case DragPhase.Cancelled:
                if (!_dragging) break;
                _dragging = false;
                Send(Move(p));
                Send(MouseInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP));
                break;
        }
    }

    // MARK: Keyboard

    private static INPUT KeyInput(VIRTUAL_KEY vk, bool up, bool extended = false)
    {
        var flags = (up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0) | (extended ? KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY : 0);
        // Include the scan code: some apps (games, XAML) read it rather than the virtual key.
        var scan = (ushort)(PInvoke.MapVirtualKey((uint)vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC) & 0xFF);
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, time = 0, dwExtraInfo = 0 };
        return input;
    }

    private static INPUT UnicodeInput(char unit, bool up)
    {
        var flags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0);
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki = new KEYBDINPUT { wVk = 0, wScan = unit, dwFlags = flags, time = 0, dwExtraInfo = 0 };
        return input;
    }

    /// <summary>Protocol key codes (macOS virtual key values, PROTOCOL.md §9) → Windows virtual keys.</summary>
    private static (VIRTUAL_KEY Key, bool Extended)? VirtualKey(ushort code) => code switch
    {
        KeyCodes.Return => (VIRTUAL_KEY.VK_RETURN, false),
        KeyCodes.Delete => (VIRTUAL_KEY.VK_BACK, false),
        KeyCodes.Tab => (VIRTUAL_KEY.VK_TAB, false),
        KeyCodes.Escape => (VIRTUAL_KEY.VK_ESCAPE, false),
        KeyCodes.Space => (VIRTUAL_KEY.VK_SPACE, false),
        KeyCodes.LeftArrow => (VIRTUAL_KEY.VK_LEFT, true),
        KeyCodes.RightArrow => (VIRTUAL_KEY.VK_RIGHT, true),
        KeyCodes.DownArrow => (VIRTUAL_KEY.VK_DOWN, true),
        KeyCodes.UpArrow => (VIRTUAL_KEY.VK_UP, true),
        KeyCodes.ForwardDelete => (VIRTUAL_KEY.VK_DELETE, true),
        KeyCodes.Home => (VIRTUAL_KEY.VK_HOME, true),
        KeyCodes.End => (VIRTUAL_KEY.VK_END, true),
        KeyCodes.PageUp => (VIRTUAL_KEY.VK_PRIOR, true),
        KeyCodes.PageDown => (VIRTUAL_KEY.VK_NEXT, true),
        KeyCodes.F1 => (VIRTUAL_KEY.VK_F1, false),
        KeyCodes.F2 => (VIRTUAL_KEY.VK_F2, false),
        KeyCodes.F3 => (VIRTUAL_KEY.VK_F3, false),
        KeyCodes.F4 => (VIRTUAL_KEY.VK_F4, false),
        KeyCodes.F5 => (VIRTUAL_KEY.VK_F5, false),
        KeyCodes.F6 => (VIRTUAL_KEY.VK_F6, false),
        KeyCodes.F7 => (VIRTUAL_KEY.VK_F7, false),
        KeyCodes.F8 => (VIRTUAL_KEY.VK_F8, false),
        KeyCodes.F9 => (VIRTUAL_KEY.VK_F9, false),
        KeyCodes.F10 => (VIRTUAL_KEY.VK_F10, false),
        KeyCodes.F11 => (VIRTUAL_KEY.VK_F11, false),
        KeyCodes.F12 => (VIRTUAL_KEY.VK_F12, false),
        _ => null,
    };

    /// <summary>
    /// Modifier bits → Windows modifier keys. ⌘ (command) and ⌃ (control) both
    /// become Ctrl, since that is where Windows keeps the shortcuts a Mac puts
    /// on ⌘; ⌥ (option) is Alt.
    /// </summary>
    private static List<VIRTUAL_KEY> ModifierKeys(KeyModifiers modifiers)
    {
        var keys = new List<VIRTUAL_KEY>(3);
        if (modifiers.HasFlag(KeyModifiers.Shift)) keys.Add(VIRTUAL_KEY.VK_SHIFT);
        if (modifiers.HasFlag(KeyModifiers.Command) || modifiers.HasFlag(KeyModifiers.Control)) keys.Add(VIRTUAL_KEY.VK_CONTROL);
        if (modifiers.HasFlag(KeyModifiers.Option)) keys.Add(VIRTUAL_KEY.VK_MENU);
        return keys;
    }

    private void Key(KeyEvent key)
    {
        var modifiers = ModifierKeys(key.Modifiers);
        var inputs = new List<INPUT>();
        foreach (var m in modifiers) inputs.Add(KeyInput(m, up: false));

        if (key.KeyCode is { } code && VirtualKey(code) is { } vk)
        {
            inputs.Add(KeyInput(vk.Key, up: false, vk.Extended));
            inputs.Add(KeyInput(vk.Key, up: true, vk.Extended));
        }
        else if (key.Text is { Length: > 0 } text)
        {
            if (modifiers.Count > 0 && modifiers.Any(m => m != VIRTUAL_KEY.VK_SHIFT))
            {
                // A shortcut: press the key that produces the character in the current layout.
                foreach (var ch in text)
                {
                    var scan = PInvoke.VkKeyScan(ch);
                    if (scan == -1) continue;
                    var keyVk = (VIRTUAL_KEY)(scan & 0xFF);
                    var needsShift = (scan & 0x100) != 0 && !modifiers.Contains(VIRTUAL_KEY.VK_SHIFT);
                    if (needsShift) inputs.Add(KeyInput(VIRTUAL_KEY.VK_SHIFT, up: false));
                    inputs.Add(KeyInput(keyVk, up: false));
                    inputs.Add(KeyInput(keyVk, up: true));
                    if (needsShift) inputs.Add(KeyInput(VIRTUAL_KEY.VK_SHIFT, up: true));
                }
            }
            else
            {
                // Plain typing. Characters the active layout can produce are sent
                // as real key presses (virtual key + scan code + Shift), which every
                // app handles like a keyboard; anything else goes as a Unicode
                // (VK_PACKET) event. Each character is its own SendInput call and
                // is paced: XAML/WinUI apps read the keyboard state when they get
                // round to the message, not when it was injected, so a burst
                // loses Shift and collapses VK_PACKETs to the last character.
                if (inputs.Count > 0) Send(inputs.ToArray());
                var shiftHeld = modifiers.Contains(VIRTUAL_KEY.VK_SHIFT);
                foreach (var unit in text)
                {
                    var scan = PInvoke.VkKeyScan(unit);
                    var shiftState = scan >> 8 & 0xFF;
                    if (scan != -1 && !char.IsSurrogate(unit) && (shiftState & ~1) == 0)
                    {
                        var keyVk = (VIRTUAL_KEY)(scan & 0xFF);
                        var needsShift = (shiftState & 1) != 0 && !shiftHeld;
                        if (needsShift) { Send(KeyInput(VIRTUAL_KEY.VK_SHIFT, up: false)); Thread.Sleep(TypingPaceMs); }
                        Send(KeyInput(keyVk, up: false), KeyInput(keyVk, up: true));
                        Thread.Sleep(TypingPaceMs);
                        if (needsShift) { Send(KeyInput(VIRTUAL_KEY.VK_SHIFT, up: true)); Thread.Sleep(TypingPaceMs); }
                    }
                    else
                    {
                        Send(UnicodeInput(unit, up: false));
                        Thread.Sleep(TypingPaceMs);
                        Send(UnicodeInput(unit, up: true));
                        Thread.Sleep(UnicodeSettleMs);
                        Thread.Sleep(TypingPaceMs);
                    }
                }
                inputs.Clear();
                for (var i = modifiers.Count - 1; i >= 0; i--) inputs.Add(KeyInput(modifiers[i], up: true));
                if (inputs.Count > 0) Send(inputs.ToArray());
                return;
            }
        }

        for (var i = modifiers.Count - 1; i >= 0; i--) inputs.Add(KeyInput(modifiers[i], up: true));
        if (inputs.Count > 0) Send(inputs.ToArray());
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (_dragging)
        {
            _dragging = false;
            try { Send(MouseInput(MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP)); } catch (Exception) { }
        }
        _queue.Dispose();
    }
}
