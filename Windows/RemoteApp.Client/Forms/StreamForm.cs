// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Client.Video;
using RemoteApp.Protocol;
using RemoteApp.WindowsCommon;
using Windows.Win32;

namespace RemoteApp.Client.Forms;

/// <summary>
/// The "virtual application" window: a normal, resizable Windows window whose
/// client area is the remote app's window. Its title and icon are the remote
/// app's, its menu bar reproduces the remote app's menus, and its size is kept
/// in step with the host window: resizing here asks the host to resize (in
/// points, debounced like the iOS client), and the host's actual geometry
/// snaps this window back when the app refused (minimum sizes), so the
/// picture is normally pixel-exact rather than letterboxed.
/// </summary>
internal sealed class StreamForm : Form
{
    private readonly ClientModel _model;
    private readonly VideoPipeline _video;
    private readonly StreamSurface _surface;
    private readonly MenuStrip _menu;
    private readonly ToolStripMenuItem _remoteMenu;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _stats;
    private readonly ToolStripStatusLabel _transient;
    private readonly System.Windows.Forms.Timer _reportTimer;
    private readonly System.Windows.Forms.Timer _transientTimer;
    private Icon? _icon;
    private bool _userSizing;
    private bool _applyingGeometry;
    private bool _menuRebuildPending;
    private (double Width, double Height) _lastReported;
    private WindowGeometry? _pendingGeometry;
    private LaunchForm? _launchForm;

    public StreamForm(ClientModel model, VideoPipeline video)
    {
        _model = model;
        _video = video;
        Text = model.StreamingAppName;
        _icon = IconHelper.IconFromPng(model.StreamingAppIcon);
        if (_icon != null) Icon = _icon;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(320, 240);
        BackColor = Color.Black;
        KeyPreview = false;
        Font = new Font("Segoe UI", 9.5f);

        _menu = new MenuStrip { Dock = DockStyle.Top, RenderMode = ToolStripRenderMode.System };
        _remoteMenu = BuildRemoteMenu();
        _menu.MenuActivate += (_, _) => _model.RequestMenu();
        _menu.MenuDeactivate += (_, _) => { if (_menuRebuildPending) RebuildMenu(); };

        _surface = new StreamSurface(video)
        {
            Dock = DockStyle.Fill,
            CtrlSendsCommand = model.Settings.CtrlSendsCommand,
            HoverEnabled = model.ServerFeatures.Contains(RemoteApp.Protocol.ProtocolFeatures.Move),
        };
        _surface.Input += _model.SendInput;
        _surface.SurfaceResized += OnSurfaceResized;

        _statusStrip = new StatusStrip { Dock = DockStyle.Bottom, SizingGrip = true, Visible = model.Settings.ShowStatistics };
        _stats = new ToolStripStatusLabel { Spring = false };
        _transient = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleRight };
        _statusStrip.Items.AddRange([_stats, _transient]);

        Controls.Add(_surface);
        Controls.Add(_statusStrip);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        _reportTimer = new System.Windows.Forms.Timer { Interval = 180 };
        _reportTimer.Tick += (_, _) => { _reportTimer.Stop(); ReportSizeNow(); };
        _transientTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _transientTimer.Tick += (_, _) => { _transientTimer.Stop(); _transient.Text = ""; };

        _model.GeometryChanged += OnGeometryChanged;
        _model.MenuChanged += RebuildMenu;
        _model.StatsChanged += UpdateStats;
        _model.StatusMessage += ShowTransientStatus;

        RebuildMenu();
        SetInitialSize();
        UseDarkTitleBar();
    }

    private double Scale => DeviceDpi / 96.0;

    private int ChromeHeight => _menu.Height + (_statusStrip.Visible ? _statusStrip.Height : 0);

    // MARK: Sizing

    private void SetInitialSize()
    {
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        var width = (int)Math.Round(_model.Settings.StreamWidth * Scale);
        var height = (int)Math.Round(_model.Settings.StreamHeight * Scale);
        var frame = Size - ClientSize;
        width = Math.Min(width, (int)(workingArea.Width * 0.92) - frame.Width);
        height = Math.Min(height, (int)(workingArea.Height * 0.92) - frame.Height - ChromeHeight);
        ClientSize = new Size(Math.Max(320, width), Math.Max(240, height) + ChromeHeight);
        Location = new Point(
            workingArea.Left + (workingArea.Width - Width) / 2,
            workingArea.Top + (workingArea.Height - Height) / 2);
    }

    private void UseDarkTitleBar()
    {
        try
        {
            var dark = 1;
            unsafe
            {
                PInvoke.DwmSetWindowAttribute(new Windows.Win32.Foundation.HWND(Handle),
                    Windows.Win32.Graphics.Dwm.DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(int));
            }
        }
        catch (Exception) { /* cosmetic */ }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _surface.Select();
        ReportSizeNow();
    }

    private void OnSurfaceResized()
    {
        if (_applyingGeometry || !IsHandleCreated) return;
        // Debounce: during a live drag the size changes continuously, and each
        // host resize rebuilds the encoder — act once it settles briefly.
        _reportTimer.Stop();
        _reportTimer.Start();
    }

    /// <summary>Sends the surface size (in points) to the host if it changed. Returns true if sent.</summary>
    private bool ReportSizeNow()
    {
        if (!IsHandleCreated || _model.Phase != ClientPhase.Streaming) return false;
        var size = _surface.ClientSize;
        if (size.Width < 50 || size.Height < 50) return false;
        var width = Math.Round(size.Width / Scale);
        var height = Math.Round(size.Height / Scale);
        if (Math.Abs(width - _lastReported.Width) < 1 && Math.Abs(height - _lastReported.Height) < 1) return false;
        _lastReported = (width, height);
        _model.ReportScreenSize(new ScreenSize(width, height, Scale));
        _model.Settings.StreamWidth = (int)width;
        _model.Settings.StreamHeight = (int)height;
        return true;
    }

    private void OnGeometryChanged()
    {
        var geometry = _model.Geometry;
        if (geometry == null) return;
        UpdateTitle(geometry);
        UpdateStats();
        if (_userSizing)
        {
            _pendingGeometry = geometry;
            return;
        }
        ApplyGeometry(geometry);
    }

    /// <summary>Resize this window so the surface matches the host window exactly (points × DPI scale).</summary>
    private void ApplyGeometry(WindowGeometry geometry)
    {
        if (WindowState != FormWindowState.Normal || !IsHandleCreated) return;
        var targetWidth = (int)Math.Round(geometry.Width * Scale);
        var targetHeight = (int)Math.Round(geometry.Height * Scale);
        var current = _surface.ClientSize;
        if (Math.Abs(targetWidth - current.Width) <= 1 && Math.Abs(targetHeight - current.Height) <= 1)
        {
            _lastReported = (geometry.Width, geometry.Height);
            return;
        }

        // Never grow past the working area (the picture letterboxes instead), and
        // keep a usable window even for a tiny host window.
        var workingArea = Screen.FromControl(this).WorkingArea;
        var frame = Size - ClientSize;
        targetWidth = Math.Max(120, Math.Min(targetWidth, workingArea.Width - frame.Width));
        targetHeight = Math.Max(80, Math.Min(targetHeight, workingArea.Height - frame.Height - ChromeHeight));

        _applyingGeometry = true;
        try
        {
            ClientSize = new Size(
                ClientSize.Width + (targetWidth - current.Width),
                ClientSize.Height + (targetHeight - current.Height));
            // Keep the window on screen after growing.
            var x = Math.Max(workingArea.Left, Math.Min(Left, workingArea.Right - Width));
            var y = Math.Max(workingArea.Top, Math.Min(Top, workingArea.Bottom - Height));
            if (x != Left || y != Top) Location = new Point(x, y);
        }
        finally
        {
            _applyingGeometry = false;
        }
        _reportTimer.Stop();
        _lastReported = (geometry.Width, geometry.Height);
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        switch ((uint)m.Msg)
        {
            case PInvoke.WM_ENTERSIZEMOVE:
                _userSizing = true;
                break;
            case PInvoke.WM_EXITSIZEMOVE:
                _userSizing = false;
                _reportTimer.Stop();
                var pending = _pendingGeometry;
                _pendingGeometry = null;
                // If the final size was already reported, the host will not answer
                // again, so snap to the geometry it last told us about.
                if (!ReportSizeNow() && pending != null) ApplyGeometry(pending);
                break;
        }
        base.WndProc(ref m);
    }

    private void UpdateTitle(WindowGeometry geometry)
    {
        var title = string.IsNullOrWhiteSpace(geometry.WindowTitle) ? _model.StreamingAppName : geometry.WindowTitle!;
        if (Text != title) Text = title;
    }

    private void UpdateStats()
    {
        var geometry = _model.Geometry;
        var size = geometry == null ? "" : $" · {geometry.Width:0}×{geometry.Height:0} pt ({geometry.PixelWidth}×{geometry.PixelHeight} px)";
        var latency = double.IsNaN(_model.LatencyMs) ? "— ms" : $"{_model.LatencyMs:0} ms";
        _stats.Text = $"{latency} · {_model.Fps:0} fps{size}";
    }

    public void ShowTransientStatus(string message)
    {
        if (!_statusStrip.Visible)
        {
            // No status bar: use the title briefly instead.
            var original = Text;
            Text = message;
            _transientTimer.Stop();
            _transientTimer.Tick += RestoreTitleOnce;
            _transientTimer.Start();
            void RestoreTitleOnce(object? sender, EventArgs e)
            {
                _transientTimer.Tick -= RestoreTitleOnce;
                if (Text == message) Text = _model.Geometry is { WindowTitle: { Length: > 0 } t } ? t : original;
            }
            return;
        }
        _transient.Text = message;
        _transientTimer.Stop();
        _transientTimer.Start();
    }

    // MARK: Menus

    private ToolStripMenuItem BuildRemoteMenu()
    {
        var remote = new ToolStripMenuItem("&Remote");

        var openApp = new ToolStripMenuItem("Open an App…");
        openApp.Click += (_, _) =>
        {
            if (_launchForm is { IsDisposed: false }) { _launchForm.Activate(); return; }
            _launchForm = new LaunchForm(_model);
            _launchForm.Show(this);
        };
        var backToList = new ToolStripMenuItem("Back to App List");
        backToList.Click += (_, _) => Close();

        var getClipboard = new ToolStripMenuItem("Get Clipboard from Host");
        getClipboard.Click += (_, _) => _model.RequestClipboard();
        var sendClipboard = new ToolStripMenuItem("Send Clipboard to Host");
        sendClipboard.Click += (_, _) => _model.SendClipboard(ClipboardBridge.Read());
        var sendFiles = new ToolStripMenuItem("Send Files to Host…");
        sendFiles.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Multiselect = true, Title = "Send files to " + _model.ServerName };
            if (dialog.ShowDialog(this) == DialogResult.OK) _model.SendClipboard(ClipboardBridge.FromFiles(dialog.FileNames));
        };

        var ctrlIsCommand = new ToolStripMenuItem("Ctrl Key Sends ⌘ Command") { CheckOnClick = true, Checked = _model.Settings.CtrlSendsCommand };
        ctrlIsCommand.CheckedChanged += (_, _) =>
        {
            _model.Settings.CtrlSendsCommand = ctrlIsCommand.Checked;
            _model.Settings.Save();
            _surface.CtrlSendsCommand = ctrlIsCommand.Checked;
        };
        var fit = new ToolStripMenuItem("Fit Window to App");
        fit.Click += (_, _) => { if (_model.Geometry is { } g) { WindowState = FormWindowState.Normal; ApplyGeometry(g); } };
        var showStats = new ToolStripMenuItem("Show Statistics") { CheckOnClick = true, Checked = _model.Settings.ShowStatistics };
        showStats.CheckedChanged += (_, _) =>
        {
            _model.Settings.ShowStatistics = showStats.Checked;
            _model.Settings.Save();
            _statusStrip.Visible = showStats.Checked;
        };
        var disconnect = new ToolStripMenuItem("Disconnect");
        disconnect.Click += (_, _) => _model.Disconnect();

        remote.DropDownItems.AddRange([
            openApp, backToList, new ToolStripSeparator(),
            getClipboard, sendClipboard, sendFiles, new ToolStripSeparator(),
            ctrlIsCommand, fit, showStats, new ToolStripSeparator(),
            disconnect,
        ]);
        return remote;
    }

    /// <summary>Reproduce the host app's menu bar (read through Accessibility on a Mac) as native menus.</summary>
    private void RebuildMenu()
    {
        // Replacing items while a dropdown is open would close it under the user.
        if (_menu.Items.OfType<ToolStripMenuItem>().Any(item => item.DropDown.Visible))
        {
            _menuRebuildPending = true;
            return;
        }
        _menuRebuildPending = false;
        _menu.SuspendLayout();
        _menu.Items.Clear();
        _menu.Items.Add(_remoteMenu);
        var menus = _model.Menu;
        for (var i = 0; i < menus.Count; i++)
        {
            var node = menus[i];
            if (node.IsSeparator || string.IsNullOrEmpty(node.Title)) continue;
            _menu.Items.Add(BuildMenuItem(node, [i]));
        }
        _menu.ResumeLayout();
    }

    private ToolStripMenuItem BuildMenuItem(MenuNode node, List<int> path)
    {
        var item = new ToolStripMenuItem(node.Title.Replace("&", "&&"))
        {
            Enabled = node.Enabled,
            Checked = node.Checked,
            ShortcutKeyDisplayString = node.Shortcut,
        };
        if (node.HasSubmenu)
        {
            var children = node.ChildrenOrEmpty;
            for (var i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child.IsSeparator)
                {
                    item.DropDownItems.Add(new ToolStripSeparator());
                    continue;
                }
                item.DropDownItems.Add(BuildMenuItem(child, [.. path, i]));
            }
        }
        else if (path.Count > 1)
        {
            item.Click += (_, _) => _model.PerformMenu(path);
        }
        return item;
    }

    // MARK: Keyboard routing

    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message msg, Keys keyData)
    {
        // Keys go to the host first; the menu bar only sees what the surface declines.
        if (_surface.Focused && _surface.HandleCommandKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (!_surface.Focused) _surface.Select();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _model.GeometryChanged -= OnGeometryChanged;
        _model.MenuChanged -= RebuildMenu;
        _model.StatsChanged -= UpdateStats;
        _model.StatusMessage -= ShowTransientStatus;
        _surface.Input -= _model.SendInput;
        _reportTimer.Dispose();
        _transientTimer.Dispose();
        _model.Settings.Save();
        _icon?.Dispose();
        base.OnFormClosed(e);
    }
}
