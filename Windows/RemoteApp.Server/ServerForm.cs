// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

namespace RemoteApp.Server;

/// <summary>Status window: pairing code, network state, connected client and log.</summary>
internal sealed class ServerForm : Form
{
    private readonly ServerModel _model;
    private readonly Label _code;
    private readonly Label _paired;
    private readonly Button _forget;
    private readonly TextBox _target;
    private readonly Label _tailnet;
    private readonly Label _warning;
    private readonly Label _listener;
    private readonly Label _client;
    private readonly Label _stats;
    private readonly TextBox _log;

    public ServerForm()
    {
        Text = "RemoteApp Server";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 620);
        MinimumSize = new Size(520, 480);
        Font = new Font("Segoe UI", 9.5f);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            Padding = new Padding(14, 10, 14, 4),
        };

        // Pairing code.
        var codeTitle = Caption("Pairing code");
        _code = new Label { Font = new Font("Consolas", 34f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
        var newCode = new Button { Text = "New code", AutoSize = true, Margin = new Padding(16, 10, 0, 0) };
        newCode.Click += (_, _) => _model.RegenerateCode();
        var codeRow = Row(_code, newCode);
        _paired = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
        _forget = new Button { Text = "Forget", AutoSize = true, Margin = new Padding(10, 0, 0, 0) };
        _forget.Click += (_, _) => _model.ForgetPairedDevices();
        var pairedRow = Row(_paired, _forget);

        // Network.
        var networkTitle = Caption("Network");
        var connectHint = new Label { Text = "On the client, connect to:", AutoSize = true };
        _target = new TextBox { ReadOnly = true, Font = new Font("Consolas", 13f), Width = 520, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control };
        _tailnet = new Label { AutoSize = true, MaximumSize = new Size(600, 0) };
        _warning = new Label { AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(600, 0) };
        _listener = new Label { AutoSize = true, MaximumSize = new Size(600, 0) };
        _client = new Label { AutoSize = true };
        _stats = new Label { AutoSize = true, Font = new Font("Consolas", 9.5f) };

        var logTitle = Caption("Log");
        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9f),
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(250, 250, 250),
            WordWrap = false,
        };

        foreach (Control c in new Control[] { codeTitle, codeRow, pairedRow, Spacer(), networkTitle, connectHint, _target, _tailnet, _warning, _listener, _client, _stats, Spacer(), logTitle })
            panel.Controls.Add(c);

        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 14, 12) };
        logHost.Controls.Add(_log);
        Controls.Add(logHost);
        Controls.Add(panel);

        _model = new ServerModel();
        _model.Changed += Refresh;
        _model.LogLine += line =>
        {
            if (_log.TextLength > 200_000) _log.Clear();
            _log.AppendText(line + Environment.NewLine);
        };
        Refresh();
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font("Segoe UI Semibold", 10f),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 6, 0, 2),
    };

    private static Control Spacer() => new Panel { Height = 6, AutoSize = false, Margin = Padding.Empty };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private new void Refresh()
    {
        _code.Text = _model.PairingCode;
        var count = _model.PairedCount;
        _paired.Text = count == 0 ? "No devices remembered" : $"{count} device{(count == 1 ? "" : "s")} remembered — they skip the code";
        _forget.Visible = count > 0;
        _target.Text = $"{_model.ConnectTarget} : {_model.Port}";
        _tailnet.Text = _model.Tailscale is { } ts
            ? $"Tailnet: {ts.DnsName} ({ts.IPv4}){(ts.Online ? "" : " — offline")}"
            : "Tailnet: not available";
        _tailnet.ForeColor = _model.Tailscale is { Online: true } ? Color.ForestGreen : Color.DarkOrange;
        _warning.Text = _model.TailnetWarning ?? "";
        _warning.Visible = _model.TailnetWarning != null;
        var bound = _model.BoundEndpoints.Count > 0 ? " — bound to " + string.Join(", ", _model.BoundEndpoints) : "";
        _listener.Text = $"Listener: {_model.ListenerState}{bound}";
        _client.Text = _model.ClientState != null ? $"Client: {_model.ClientState}" : "No client connected";
        _client.ForeColor = _model.ClientState != null ? SystemColors.ControlText : SystemColors.GrayText;
        _stats.Text = _model.Stats is { } s ? $"{s.Fps:0} fps · {s.Kbps:0} kbit/s · {s.DroppedFrames} dropped" : "";
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _model.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _model.Stop();
        base.OnFormClosed(e);
    }
}
