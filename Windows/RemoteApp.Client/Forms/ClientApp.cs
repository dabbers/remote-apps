// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Client.Video;
using RemoteApp.Protocol;
using RemoteApp.WindowsCommon;

namespace RemoteApp.Client.Forms;

/// <summary>
/// Application context: owns the model and the video pipeline, and shows the
/// right window for the current phase — connect, pairing code, app list, or
/// the virtual app window while streaming.
/// </summary>
internal sealed class ClientApp : ApplicationContext
{
    private readonly ClientModel _model;
    private readonly VideoPipeline _video;
    private readonly ConnectForm _connectForm;
    private readonly SynchronizationContext _ui;
    private PairForm? _pairForm;
    private AppsForm? _appsForm;
    private StreamForm? _streamForm;

    private readonly SmokeTest? _smoke;

    public ClientApp(SmokeOptions? smoke = null)
    {
        // No control exists yet, so install the WinForms context before anything captures it.
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;
        var settings = Settings.Load();
        _model = new ClientModel(settings);
        _video = new VideoPipeline();
        if (smoke != null)
            _smoke = new SmokeTest(smoke, _model) { StreamFormProvider = () => _streamForm, Exit = ExitThread, DumpFrame = _video.DumpNextFrame };

        _model.VideoConfigReceived += config => _video.Configure(config);
        _model.VideoFrameReceived += frame => _video.Enqueue(frame);
        _video.FramePresented += latency =>
        {
            _model.RecordFrame(latency);
            _smoke?.FramePresented();
        };
        _video.Error += message => _ui.Post(_ =>
        {
            _smoke?.Log("video: " + message);
            ShowError(message);
        }, null);
        _video.Info += message => _smoke?.Log("video: " + message);

        _model.PhaseChanged += OnPhaseChanged;
        _model.ErrorOccurred += ShowError;
        _model.ClipboardReceived += OnClipboardReceived;

        _connectForm = new ConnectForm(_model);
        _connectForm.FormClosed += (_, _) => ExitThread();
        if (_smoke != null) _connectForm.Shown += (_, _) => _smoke.Begin();
        MainForm = _connectForm;
        _connectForm.Show();
    }

    private Form ActiveOwner => _streamForm is { Visible: true } ? _streamForm
        : _appsForm is { Visible: true } ? _appsForm
        : _connectForm;

    private bool _inPhaseChange;
    private bool _phaseChangePending;

    /// <summary>
    /// Phase handlers can change the phase again (selecting an app from the
    /// list goes Apps → Streaming inside the Apps handler), so transitions are
    /// applied one at a time: a nested change is queued and applied after the
    /// current one finishes, never re-entered with a stale phase.
    /// </summary>
    private void OnPhaseChanged()
    {
        if (_inPhaseChange)
        {
            _phaseChangePending = true;
            return;
        }
        _inPhaseChange = true;
        try
        {
            do
            {
                _phaseChangePending = false;
                ApplyPhase(_model.Phase);
            } while (_phaseChangePending);
        }
        finally
        {
            _inPhaseChange = false;
        }
    }

    private void ApplyPhase(ClientPhase phase)
    {
        if (_smoke != null && _smoke.OnPhase()) return;
        switch (phase)
        {
            case ClientPhase.Disconnected:
                ClosePairForm();
                CloseStreamForm();
                CloseAppsForm();
                _connectForm.Show();
                _connectForm.Activate();
                break;

            case ClientPhase.Connecting:
                ClosePairForm();
                _connectForm.Show();
                break;

            case ClientPhase.Pairing:
                _connectForm.Show();
                if (_pairForm == null || _pairForm.IsDisposed)
                {
                    _pairForm = new PairForm(_model);
                    _pairForm.Show(_connectForm);
                }
                else
                {
                    _pairForm.ShowError(_model.PairingError);
                    _pairForm.Activate();
                }
                break;

            case ClientPhase.Apps:
                ClosePairForm();
                CloseStreamForm();
                _connectForm.Hide();
                if (_appsForm == null || _appsForm.IsDisposed)
                {
                    _appsForm = new AppsForm(_model);
                    _appsForm.FormClosed += (_, _) =>
                    {
                        _appsForm = null;
                        if (_model.Phase == ClientPhase.Apps) _model.Disconnect();
                    };
                }
                _appsForm.Show();
                _appsForm.Activate();
                break;

            case ClientPhase.Streaming:
                _appsForm?.Hide();
                _video.Reset();
                CloseStreamForm();
                var streamForm = new StreamForm(_model, _video);
                streamForm.FormClosed += (sender, _) =>
                {
                    // Only a user-initiated close returns to the list; a programmatic
                    // close (new app selected, disconnect) has already moved on.
                    if (!ReferenceEquals(_streamForm, sender)) return;
                    _streamForm = null;
                    if (_model.Phase == ClientPhase.Streaming) _model.BackToApps();
                };
                _streamForm = streamForm;
                streamForm.Show();
                break;
        }
    }

    private void ClosePairForm()
    {
        if (_pairForm is { IsDisposed: false }) _pairForm.Close();
        _pairForm = null;
    }

    private void CloseAppsForm()
    {
        var form = _appsForm;
        _appsForm = null;
        if (form is { IsDisposed: false }) form.Close();
    }

    private void CloseStreamForm()
    {
        var form = _streamForm;
        _streamForm = null;
        if (form is { IsDisposed: false }) form.Close();
    }

    private void ShowError(string message)
    {
        if (_streamForm is { Visible: true } stream)
        {
            stream.ShowTransientStatus(message);
            return;
        }
        if (_connectForm.Visible) _connectForm.ShowStatus(message, isError: true);
        else MessageBox.Show(ActiveOwner, message, "RemoteApp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void OnClipboardReceived(ClipboardContent content)
    {
        var saved = ClipboardBridge.Apply(content);
        string status;
        if (saved.Count > 0)
            status = $"Saved {saved.Count} item{(saved.Count == 1 ? "" : "s")} to {ClipboardBridge.ReceivedDirectory}";
        else if (content.Text != null)
            status = $"Copied text from {_model.ServerName}";
        else
            status = $"{_model.ServerName}'s clipboard is empty";
        if (_streamForm is { Visible: true } stream) stream.ShowTransientStatus(status);
        else if (_appsForm is { Visible: true } apps) apps.ShowStatus(status);
    }

    protected override void ExitThreadCore()
    {
        _model.Disconnect();
        _video.Dispose();
        base.ExitThreadCore();
    }
}
