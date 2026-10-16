// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Protocol;

namespace RemoteApp.Client.Forms;

/// <summary>First window: type the server's MagicDNS name or tailnet IP and connect.</summary>
internal sealed class ConnectForm : Form
{
    private readonly ClientModel _model;
    private readonly ComboBox _host;
    private readonly Button _connect;
    private readonly Button _cancel;
    private readonly Label _status;

    public ConnectForm(ClientModel model)
    {
        _model = model;
        Text = "RemoteApp";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 250);
        Font = new Font("Segoe UI", 9.5f);

        var title = new Label
        {
            Text = "Connect over Tailscale",
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = true,
            Location = new Point(20, 18),
        };
        var hint = new Label
        {
            Text = "Enter the MagicDNS name or tailnet IP shown in RemoteApp Server (optionally :port).\n" +
                   $"For a server on this PC, use 127.0.0.1:{ProtocolConstants.LoopbackPort}.",
            Location = new Point(20, 50),
            Size = new Size(440, 44),
            ForeColor = SystemColors.GrayText,
        };
        _host = new ComboBox
        {
            Location = new Point(20, 100),
            Size = new Size(330, 28),
            DropDownStyle = ComboBoxStyle.DropDown,
            Font = new Font("Segoe UI", 11f),
        };
        _host.Items.AddRange(model.Settings.RecentHosts.Cast<object>().ToArray());
        _host.Text = model.Settings.LastHost;
        _host.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Return) return;
            e.SuppressKeyPress = true;
            Connect();
        };
        _connect = new Button { Text = "Connect", Location = new Point(360, 99), Size = new Size(100, 30) };
        _connect.Click += (_, _) => Connect();
        _cancel = new Button { Text = "Cancel", Location = new Point(360, 99), Size = new Size(100, 30), Visible = false };
        _cancel.Click += (_, _) => _model.Disconnect();
        _status = new Label
        {
            Location = new Point(20, 145),
            Size = new Size(440, 60),
            ForeColor = SystemColors.GrayText,
        };
        var footer = new Label
        {
            Text = "The server is reachable only over your tailnet; the pairing code is a second factor.",
            Location = new Point(20, 215),
            Size = new Size(440, 24),
            ForeColor = SystemColors.GrayText,
            Font = new Font("Segoe UI", 8.5f),
        };
        Controls.AddRange([title, hint, _host, _connect, _cancel, _status, footer]);
        AcceptButton = _connect;

        _model.PhaseChanged += OnPhaseChanged;
        OnPhaseChanged();
    }

    private void Connect()
    {
        var host = _host.Text.Trim();
        if (host.Length == 0) return;
        ShowStatus("", isError: false);
        _model.Connect(host);
    }

    private void OnPhaseChanged()
    {
        var connecting = _model.Phase is ClientPhase.Connecting or ClientPhase.Pairing;
        _connect.Visible = !connecting;
        _cancel.Visible = connecting;
        _host.Enabled = !connecting;
        switch (_model.Phase)
        {
            case ClientPhase.Connecting:
                ShowStatus($"Connecting to {_model.ConnectingTo}…", isError: false);
                break;
            case ClientPhase.Pairing:
                ShowStatus("Enter the pairing code shown in the server window.", isError: false);
                break;
            case ClientPhase.Disconnected:
                // Keep the last error visible.
                _host.Items.Clear();
                _host.Items.AddRange(_model.Settings.RecentHosts.Cast<object>().ToArray());
                break;
        }
    }

    public void ShowStatus(string message, bool isError)
    {
        _status.Text = message;
        _status.ForeColor = isError ? Color.Firebrick : SystemColors.GrayText;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _model.PhaseChanged -= OnPhaseChanged;
        base.OnFormClosed(e);
    }
}
