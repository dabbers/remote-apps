// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;
using RemoteApp.Client.Input;
using RemoteApp.Protocol;
using Windows.Win32;

namespace RemoteApp.Client.Video;

/// <summary>
/// The control that shows the stream and turns mouse/keyboard into protocol
/// input events (PROTOCOL.md §8–§9): clicks become taps (the host derives
/// double-clicks from timing), a press-and-move becomes a drag, the wheel
/// scrolls in client points, and keys are mapped by <see cref="KeyMapper"/>.
/// Painting is done by the <see cref="VideoPipeline"/>, which owns the
/// Direct2D target for this window on its own thread.
/// </summary>
internal sealed class StreamSurface : Control
{
    private const int DragThresholdPixels = 4;
    /// <summary>Client points scrolled per wheel notch (the iOS client sends finger distance; ~40 pt reads naturally).</summary>
    private const double WheelStepPoints = 40;

    /// <summary>Minimum interval between hover moves (~60/s, PROTOCOL.md §8).</summary>
    private const int HoverIntervalMs = 16;

    private readonly VideoPipeline _video;
    private Point _pressPoint;
    private bool _dragging;
    private char? _pendingHighSurrogate;

    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = HoverIntervalMs };
    private readonly Stopwatch _hoverClock = Stopwatch.StartNew();
    private long _lastHoverMs = -HoverIntervalMs;
    private NormalizedPoint? _pendingHover;
    private NormalizedPoint? _lastHover;

    /// <summary>Raised for every input event to forward to the host.</summary>
    public event Action<InputEvent>? Input;

    /// <summary>Raised when the client area changes size.</summary>
    public event Action? SurfaceResized;

    /// <summary>Ctrl sends ⌘ (command) when true, ⌃ (control) when false.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CtrlSendsCommand { get; set; } = true;

    /// <summary>
    /// True when the host listed <see cref="ProtocolFeatures.Move"/>: hovering
    /// over the video then moves the host cursor. Off for older servers, which
    /// would drop the connection on an input event they do not know.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HoverEnabled { get; set; }

    public StreamSurface(VideoPipeline video)
    {
        _video = video;
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); FlushHover(); };
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque
                 | ControlStyles.Selectable | ControlStyles.UserMouse, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, false);
        TabStop = true;
        BackColor = Color.Black;
        Cursor = Cursors.Default;
    }

    // MARK: Surface lifecycle (the pipeline thread owns the Direct2D target)

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _video.AttachSurface(Handle, ClientSize.Width, ClientSize.Height);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _video.DetachSurface();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hoverTimer.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Direct2D owns the whole surface.
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _video.RedrawSurface();
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (IsHandleCreated) _video.ResizeSurface(ClientSize.Width, ClientSize.Height);
        SurfaceResized?.Invoke();
    }

    // MARK: Coordinate mapping

    private Rectangle VideoRect
    {
        get
        {
            var rect = _video.VideoRect;
            return rect.Width > 0 && rect.Height > 0 ? rect : ClientRectangle;
        }
    }

    private NormalizedPoint? Normalize(Point p)
    {
        var rect = VideoRect;
        if (rect.Width <= 0 || rect.Height <= 0 || !rect.Contains(p)) return null;
        return new NormalizedPoint((p.X - rect.X) / (double)rect.Width, (p.Y - rect.Y) / (double)rect.Height);
    }

    private NormalizedPoint ClampNormalize(Point p)
    {
        var rect = VideoRect;
        if (rect.Width <= 0 || rect.Height <= 0) return new NormalizedPoint(0, 0);
        return new NormalizedPoint(
            Math.Clamp((p.X - rect.X) / (double)rect.Width, 0, 1),
            Math.Clamp((p.Y - rect.Y) / (double)rect.Height, 0, 1));
    }

    private void Send(InputEvent input) => Input?.Invoke(input);

    // MARK: Mouse

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!Focused) Focus();
        if (e.Button == MouseButtons.Left)
        {
            _pressPoint = e.Location;
            _dragging = false;
            Capture = true;
        }
        // A press-and-move is a drag; drop any hover still waiting for its slot.
        _hoverTimer.Stop();
        _pendingHover = null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.Button == MouseButtons.None)
        {
            // No button held: a hover, sent only while over the video itself.
            if (HoverEnabled && Normalize(e.Location) is { } at) QueueHover(at);
            return;
        }
        if (!e.Button.HasFlag(MouseButtons.Left)) return;
        if (!_dragging)
        {
            var dx = e.X - _pressPoint.X;
            var dy = e.Y - _pressPoint.Y;
            if (dx * dx + dy * dy < DragThresholdPixels * DragThresholdPixels) return;
            if (Normalize(_pressPoint) is not { } origin) return;
            _dragging = true;
            Send(new DragEvent(origin, DragPhase.Began));
        }
        Send(new DragEvent(ClampNormalize(e.Location), DragPhase.Moved));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        // Never report a position the pointer has already left.
        _hoverTimer.Stop();
        _pendingHover = null;
    }

    /// <summary>
    /// Rate-limits hover to ~60/s while always delivering the newest position:
    /// a move inside the interval waits for the timer instead of being dropped,
    /// so the host cursor ends exactly where the pointer stopped.
    /// </summary>
    private void QueueHover(NormalizedPoint at)
    {
        if (_pendingHover == null && at == _lastHover) return;   // WM_MOUSEMOVE without motion
        _pendingHover = at;
        if (_hoverClock.ElapsedMilliseconds - _lastHoverMs >= HoverIntervalMs) FlushHover();
        else if (!_hoverTimer.Enabled) _hoverTimer.Start();
    }

    private void FlushHover()
    {
        if (_pendingHover is not { } at) return;
        _pendingHover = null;
        _lastHover = at;
        _lastHoverMs = _hoverClock.ElapsedMilliseconds;
        Send(new MoveEvent(at));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
        {
            Capture = false;
            if (_dragging)
            {
                _dragging = false;
                Send(new DragEvent(ClampNormalize(e.Location), DragPhase.Ended));
                return;
            }
        }
        if (Normalize(e.Location) is not { } at) return;
        var button = e.Button switch
        {
            MouseButtons.Left => MouseButton.Left,
            MouseButtons.Right => MouseButton.Right,
            MouseButtons.Middle => MouseButton.Middle,
            _ => (MouseButton?)null,
        };
        if (button is { } b) Send(new TapEvent(at, b));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        if (Normalize(e.Location) is not { } at) return;
        // Wheel away from the user = content moves down = positive dy (PROTOCOL.md §8).
        var step = e.Delta / 120.0 * WheelStepPoints;
        if (ModifierKeys.HasFlag(Keys.Shift)) Send(new ScrollEvent(at, -step, 0));
        else Send(new ScrollEvent(at, 0, step));
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == (int)PInvoke.WM_MOUSEHWHEEL)
        {
            var delta = (short)((long)m.WParam >> 16 & 0xFFFF);
            var screen = new Point((short)((long)m.LParam & 0xFFFF), (short)((long)m.LParam >> 16 & 0xFFFF));
            if (Normalize(PointToClient(screen)) is { } at)
                Send(new ScrollEvent(at, -delta / 120.0 * WheelStepPoints, 0));   // tilt right = content moves left
            m.Result = 1;
            return;
        }
        base.WndProc(ref m);
    }

    // MARK: Keyboard

    protected override bool IsInputKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        return key is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Tab or Keys.Return or Keys.Escape
            || base.IsInputKey(keyData);
    }

    /// <summary>
    /// Called from the form's <c>ProcessCmdKey</c> for every key-down, before
    /// menus see it. Handles non-printing keys and modifier combinations;
    /// plain characters fall through to <see cref="OnKeyPress"/> so the
    /// keyboard layout, Shift and dead keys are honoured.
    /// </summary>
    public bool HandleCommandKey(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        var modifiers = keyData & Keys.Modifiers;
        if (KeyMapper.IsModifierKey(key)) return false;
        if (key == Keys.F4 && modifiers == Keys.Alt) return false;                      // close window
        if (modifiers == (Keys.Control | Keys.Alt)) return false;                       // AltGr characters

        var protocolModifiers = KeyMapper.Modifiers(modifiers, CtrlSendsCommand);
        if (KeyMapper.SpecialKeyCode(key) is { } code)
        {
            Send(new KeyInputEvent(KeyEvent.ForKeyCode(code, protocolModifiers)));
            return true;
        }
        if (modifiers.HasFlag(Keys.Control) || modifiers.HasFlag(Keys.Alt))
        {
            if (KeyMapper.CharForKey(key) is { } ch)
            {
                Send(new KeyInputEvent(KeyEvent.ForText(ch, protocolModifiers)));
                return true;
            }
        }
        return false;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        var c = e.KeyChar;
        string text;
        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            e.Handled = true;
            return;
        }
        if (char.IsLowSurrogate(c) && _pendingHighSurrogate is { } high)
        {
            text = new string([high, c]);
            _pendingHighSurrogate = null;
        }
        else if (char.IsControl(c))
        {
            base.OnKeyPress(e);
            return;
        }
        else
        {
            text = c.ToString();
        }
        Send(new KeyInputEvent(KeyEvent.ForText(text)));
        e.Handled = true;
    }
}
