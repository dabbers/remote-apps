// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Protocol;
using RemoteApp.WindowsCommon;

namespace RemoteApp.Client.Forms;

/// <summary>Running apps on the host; pick one to open it as a virtual window here.</summary>
internal sealed class AppsForm : Form
{
    private readonly ClientModel _model;
    private readonly ListView _list;
    private readonly ImageList _icons;
    private readonly ToolStripStatusLabel _status;
    private LaunchForm? _launchForm;

    public AppsForm(ClientModel model)
    {
        _model = model;
        Text = model.ServerName;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 480);
        MinimumSize = new Size(380, 300);
        Font = new Font("Segoe UI", 9.5f);

        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Padding = new Padding(6, 2, 6, 2) };
        var open = new ToolStripButton("Open an app…") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        open.Click += (_, _) => ShowLaunch();
        var refresh = new ToolStripButton("Refresh") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        refresh.Click += (_, _) => _model.RefreshApps();
        var disconnect = new ToolStripButton("Disconnect") { DisplayStyle = ToolStripItemDisplayStyle.Text, Alignment = ToolStripItemAlignment.Right };
        disconnect.Click += (_, _) => _model.Disconnect();
        tools.Items.AddRange([open, refresh, disconnect]);

        var iconSize = LogicalToDeviceUnits(new Size(40, 40));
        _icons = new ImageList { ImageSize = iconSize, ColorDepth = ColorDepth.Depth32Bit };
        // Create the native list now so Images.Add copies the bitmap immediately
        // (otherwise it keeps our bitmap, which we dispose after adding).
        _ = _icons.Handle;
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Tile,
            LargeImageList = _icons,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            TileSize = LogicalToDeviceUnits(new Size(250, 52)),
        };
        _list.Columns.Add("Name");
        _list.Columns.Add("Details");
        _list.ItemActivate += (_, _) => OpenSelected();

        var statusStrip = new StatusStrip { SizingGrip = false };
        _status = new ToolStripStatusLabel();
        statusStrip.Items.Add(_status);

        Controls.Add(_list);
        Controls.Add(statusStrip);
        Controls.Add(tools);

        _model.AppsChanged += Populate;
        _model.PhaseChanged += OnPhaseChanged;
        Populate();
    }

    private void OnPhaseChanged()
    {
        if (_model.Phase == ClientPhase.Apps) Text = _model.ServerName;
    }

    private void Populate()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        _icons.Images.Clear();
        foreach (var app in _model.Apps)
        {
            var key = app.Pid.ToString();
            using var bitmap = IconHelper.BitmapFromPng(app.IconPNG);
            if (bitmap != null) _icons.Images.Add(key, bitmap);
            var item = new ListViewItem(app.Name) { Tag = app, ImageKey = bitmap != null ? key : null };
            item.SubItems.Add($"{app.WindowCount} window{(app.WindowCount == 1 ? "" : "s")} · {app.BundleIdentifier}");
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _status.Text = _model.Apps.Count == 0
            ? "No running apps with windows. Use “Open an app…” to launch one."
            : $"{_model.Apps.Count} running app{(_model.Apps.Count == 1 ? "" : "s")} on {_model.ServerName}";
    }

    private void OpenSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not AppInfo app) return;
        _model.Select(app);
    }

    private void ShowLaunch()
    {
        if (_launchForm is { IsDisposed: false })
        {
            _launchForm.Activate();
            return;
        }
        _launchForm = new LaunchForm(_model);
        _launchForm.Show(this);
    }

    public void ShowStatus(string message) => _status.Text = message;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _model.AppsChanged -= Populate;
        _model.PhaseChanged -= OnPhaseChanged;
        base.OnFormClosed(e);
    }
}
