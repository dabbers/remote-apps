// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Protocol;
using RemoteApp.WindowsCommon;

namespace RemoteApp.Client.Forms;

/// <summary>Searchable list of the host's installed apps; picking one launches and streams it.</summary>
internal sealed class LaunchForm : Form
{
    private readonly ClientModel _model;
    private readonly TextBox _search;
    private readonly ListView _list;
    private readonly ImageList _icons;
    private readonly Label _loading;

    public LaunchForm(ClientModel model)
    {
        _model = model;
        Text = "Open an app on " + model.ServerName;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, 520);
        MinimumSize = new Size(320, 300);
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9.5f);

        _search = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Search", Margin = new Padding(8) };
        _search.TextChanged += (_, _) => Populate();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && _list.Items.Count > 0) { _list.Focus(); _list.Items[0].Selected = true; e.Handled = true; }
            if (e.KeyCode == Keys.Return) { OpenSelected(first: true); e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Escape) { Close(); e.SuppressKeyPress = true; }
        };

        _icons = new ImageList { ImageSize = LogicalToDeviceUnits(new Size(28, 28)), ColorDepth = ColorDepth.Depth32Bit };
        _ = _icons.Handle;   // so Images.Add copies bitmaps we dispose afterwards
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.None,
            FullRowSelect = true,
            MultiSelect = false,
            SmallImageList = _icons,
            BorderStyle = BorderStyle.None,
        };
        _list.Columns.Add("Name", -2);
        _list.ItemActivate += (_, _) => OpenSelected(first: false);
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        _list.Resize += (_, _) => { if (_list.Columns.Count > 0) _list.Columns[0].Width = _list.ClientSize.Width - 4; };

        _loading = new Label
        {
            Dock = DockStyle.Bottom,
            Text = "Loading apps…",
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(8, 4, 8, 4),
            AutoSize = false,
            Height = 28,
        };

        Controls.Add(_list);
        Controls.Add(_loading);
        Controls.Add(_search);

        _model.LaunchableChanged += Populate;
        if (_model.Launchable.Count == 0) _model.RequestLaunchable();
        Populate();
    }

    private void Populate()
    {
        var filter = _search.Text.Trim();
        var apps = _model.Launchable
            .Where(a => filter.Length == 0 || a.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        _icons.Images.Clear();
        foreach (var app in apps)
        {
            using var bitmap = IconHelper.BitmapFromPng(app.IconPNG);
            if (bitmap != null) _icons.Images.Add(app.BundleIdentifier, bitmap);
            _list.Items.Add(new ListViewItem(app.Name) { Tag = app, ImageKey = bitmap != null ? app.BundleIdentifier : null });
        }
        _list.EndUpdate();
        _loading.Text = _model.Launchable.Count == 0 ? "Loading apps…" : $"{apps.Count} of {_model.Launchable.Count} apps";
    }

    private void OpenSelected(bool first)
    {
        LaunchableApp? app = null;
        if (_list.SelectedItems.Count > 0) app = _list.SelectedItems[0].Tag as LaunchableApp;
        else if (first && _list.Items.Count > 0) app = _list.Items[0].Tag as LaunchableApp;
        if (app == null) return;
        _model.Launch(app);
        Close();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _search.Focus();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _model.LaunchableChanged -= Populate;
        base.OnFormClosed(e);
    }
}
