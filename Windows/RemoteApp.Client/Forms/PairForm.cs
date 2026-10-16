// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

namespace RemoteApp.Client.Forms;

/// <summary>Asks for the 6-digit pairing code shown in the server window (once per server).</summary>
internal sealed class PairForm : Form
{
    private readonly ClientModel _model;
    private readonly TextBox _code;
    private readonly Label _error;
    private readonly Button _ok;

    public PairForm(ClientModel model)
    {
        _model = model;
        Text = "Pair with " + model.ConnectingTo;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(360, 200);
        Font = new Font("Segoe UI", 9.5f);

        var prompt = new Label
        {
            Text = $"Enter the pairing code shown in RemoteApp Server on\n{model.ConnectingTo}.",
            Location = new Point(20, 16),
            Size = new Size(320, 40),
        };
        _code = new TextBox
        {
            Location = new Point(80, 62),
            Size = new Size(200, 48),
            Font = new Font("Consolas", 26f),
            TextAlign = HorizontalAlignment.Center,
            MaxLength = 6,
        };
        _code.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
        };
        _code.TextChanged += (_, _) =>
        {
            _ok.Enabled = _code.Text.Length == 6;
            if (_code.Text.Length == 6) Submit();
        };
        _error = new Label
        {
            Location = new Point(20, 118),
            Size = new Size(320, 24),
            ForeColor = Color.Firebrick,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _ok = new Button { Text = "Connect", Location = new Point(160, 152), Size = new Size(90, 30), Enabled = false };
        _ok.Click += (_, _) => Submit();
        var cancel = new Button { Text = "Cancel", Location = new Point(258, 152), Size = new Size(82, 30) };
        cancel.Click += (_, _) => { _model.Disconnect(); Close(); };
        Controls.AddRange([prompt, _code, _error, _ok, cancel]);
        AcceptButton = _ok;
        CancelButton = cancel;
        ShowError(model.PairingError);
    }

    private void Submit()
    {
        if (_code.Text.Length != 6) return;
        _error.Text = "";
        _model.Pair(_code.Text);
    }

    public void ShowError(string? message)
    {
        _error.Text = message ?? "";
        if (message != null)
        {
            _code.SelectAll();
            _code.Focus();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _code.Focus();
    }
}
