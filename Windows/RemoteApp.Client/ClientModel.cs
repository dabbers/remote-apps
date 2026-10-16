// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Protocol;

namespace RemoteApp.Client;

public enum ClientPhase
{
    Disconnected,
    Connecting,
    Pairing,
    Apps,
    Streaming,
}

/// <summary>
/// Client state machine and connection owner (the Windows counterpart of the
/// Swift <c>ClientModel</c>). All state and events live on the UI thread;
/// network callbacks are posted there, except video frames, which go straight
/// to <see cref="VideoFrameReceived"/> on the receive thread for the decoder.
/// </summary>
public sealed class ClientModel
{
    private readonly SynchronizationContext _ui;
    private MessageConnection? _connection;
    private CancellationTokenSource? _connectCts;
    private System.Threading.Timer? _keepalive;
    private string _hostKey = "";

    public Settings Settings { get; }

    public ClientPhase Phase { get; private set; } = ClientPhase.Disconnected;
    public string? PairingError { get; private set; }
    public string ConnectingTo { get; private set; } = "";
    public string ServerName { get; private set; } = "";
    public double HostScale { get; private set; } = 2;
    /// <summary>Optional protocol capabilities the connected server listed (<see cref="ProtocolFeatures"/>); empty for older servers.</summary>
    public IReadOnlySet<string> ServerFeatures { get; private set; } = new HashSet<string>();
    public List<AppInfo> Apps { get; private set; } = [];
    public List<LaunchableApp> Launchable { get; private set; } = [];
    public WindowGeometry? Geometry { get; private set; }
    public List<MenuNode> Menu { get; private set; } = [];
    /// <summary>Name and icon of the app being streamed (for the virtual window's chrome).</summary>
    public string StreamingAppName { get; private set; } = "";
    public byte[]? StreamingAppIcon { get; private set; }

    /// <summary>The virtual window's drawable area, in points; sent in <c>hello</c> and on resize.</summary>
    public ScreenSize CurrentScreen { get; set; }

    /// <summary>Smoothed capture → present latency; NaN until a comparable host timestamp arrives.</summary>
    public double LatencyMs { get; private set; } = double.NaN;
    public double Fps { get; private set; }

    public event Action? PhaseChanged;
    public event Action? AppsChanged;
    public event Action? LaunchableChanged;
    public event Action? GeometryChanged;
    public event Action? MenuChanged;
    public event Action? StatsChanged;
    public event Action<string>? ErrorOccurred;
    /// <summary>Transient, non-fatal status (e.g. "Copied text from host").</summary>
    public event Action<string>? StatusMessage;
    public event Action<ClipboardContent>? ClipboardReceived;
    public event Action<VideoConfig>? VideoConfigReceived;
    /// <summary>Raised on the network thread.</summary>
    public event Action<VideoFrame>? VideoFrameReceived;

    public bool IsConnected => Phase is ClientPhase.Apps or ClientPhase.Streaming;

    public ClientModel(Settings settings)
    {
        Settings = settings;
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("create the model on the UI thread");
        CurrentScreen = new ScreenSize(settings.StreamWidth, settings.StreamHeight, 1);
    }

    private void Post(Action action) => _ui.Post(_ => action(), null);

    private void SetPhase(ClientPhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        PhaseChanged?.Invoke();
    }

    // MARK: Connection lifecycle

    /// <summary>Connect by MagicDNS name or tailnet IP, with an optional :port (127.0.0.1:53124 for a same-PC server).</summary>
    public void Connect(string hostText)
    {
        Endpoint endpoint;
        try { endpoint = Endpoint.Parse(hostText); }
        catch (FormatException e) { ErrorOccurred?.Invoke($"Bad address: {e.Message}"); return; }

        Teardown();
        _hostKey = hostText.Trim();
        Settings.RememberHost(_hostKey);
        Settings.Save();
        Settings.PairTokens.TryGetValue(_hostKey, out var token);
        ConnectingTo = endpoint.ToString();
        PairingError = null;
        SetPhase(ClientPhase.Connecting);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _connectCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var stream = await Transport.ConnectAsync(endpoint, cts.Token);
                Post(() => OnConnected(stream, token, cts));
            }
            catch (Exception e)
            {
                Post(() =>
                {
                    if (_connectCts != cts) return;
                    Fail(cts.IsCancellationRequested ? "Connection timed out" : $"Connection failed: {e.Message}");
                });
            }
        });
    }

    private void OnConnected(Stream stream, string? token, CancellationTokenSource cts)
    {
        if (_connectCts != cts || Phase != ClientPhase.Connecting) { stream.Dispose(); return; }
        var connection = new MessageConnection(stream);
        connection.ControlReceived += message =>
        {
            if (_connection != connection) return;
            // Decoder configuration must stay ordered with the frames that follow
            // it, so it goes straight to the video pipeline from the receive
            // thread instead of taking the detour through the UI thread.
            if (message is VideoConfigMessage config)
            {
                VideoConfigReceived?.Invoke(config.Config);
                return;
            }
            Post(() => { if (_connection == connection) Handle(message); });
        };
        connection.VideoReceived += frame => { if (_connection == connection) VideoFrameReceived?.Invoke(frame); };
        connection.Closed += error => Post(() =>
        {
            if (_connection != connection) return;
            _connection = null;
            if (Phase == ClientPhase.Disconnected) return;
            LastException = error;
            Fail(error == null ? "Disconnected" : $"Disconnected: {error.Message}");
        });
        _connection = connection;
        connection.Start();

        // A remembered server authenticates by token; otherwise ask for the code.
        if (!string.IsNullOrEmpty(token)) SendHello("", token);
        else SetPhase(ClientPhase.Pairing);
    }

    /// <summary>Submit the 6-digit pairing code shown in the server window.</summary>
    public void Pair(string code)
    {
        if (_connection == null)
        {
            // The connection dropped while the code dialog was up: reconnect, then pair.
            Connect(_hostKey);
            return;
        }
        PairingError = null;
        SendHello(code, null);
    }

    private void SendHello(string credential, string? token)
    {
        _connection?.Send(new HelloMessage(new Hello(
            ProtocolConstants.ProtocolVersion, Environment.MachineName, Settings.DeviceID,
            credential, token, CurrentScreen)));
    }

    public void Disconnect()
    {
        Teardown();
        SetPhase(ClientPhase.Disconnected);
    }

    /// <summary>The message of the last failure that ended a connection (set before the phase changes).</summary>
    public string? LastError { get; private set; }

    /// <summary>The exception that ended the last connection, if any (for diagnostics).</summary>
    public Exception? LastException { get; private set; }

    private void Fail(string message)
    {
        LastError = message;
        Teardown();
        SetPhase(ClientPhase.Disconnected);
        ErrorOccurred?.Invoke(message);
    }

    private void Teardown()
    {
        _connectCts?.Cancel();
        _connectCts = null;
        _keepalive?.Dispose();
        _keepalive = null;
        var connection = _connection;
        _connection = null;
        connection?.Close();
        Geometry = null;
        Menu = [];
        Apps = [];
    }

    /// <summary>QUIC drops idle connections; TLS/TCP does not, but a ping every 5 s also detects a dead peer.</summary>
    private void StartKeepalive()
    {
        _keepalive?.Dispose();
        _keepalive = new System.Threading.Timer(_ =>
        {
            _connection?.Send(new PingMessage((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    // MARK: Actions

    public void RefreshApps() => _connection?.Send(new ListAppsMessage());

    public void Select(AppInfo app)
    {
        BeginStream(app.Name, app.IconPNG);
        _connection?.Send(new SelectAppMessage(app.Pid));
    }

    public void RequestLaunchable() => _connection?.Send(new RequestLaunchableMessage());

    /// <summary>Launch an installed app (the server opens it if needed) and stream it.</summary>
    public void Launch(LaunchableApp app)
    {
        BeginStream(app.Name, app.IconPNG);
        _connection?.Send(new LaunchAppMessage(app.BundleIdentifier));
    }

    private void BeginStream(string name, byte[]? icon)
    {
        StreamingAppName = name;
        StreamingAppIcon = icon;
        Geometry = null;
        Menu = [];
        SetPhase(ClientPhase.Streaming);
    }

    public void BackToApps()
    {
        if (!IsConnected) return;
        SetPhase(ClientPhase.Apps);
        RefreshApps();
    }

    public void SendInput(InputEvent input) => _connection?.Send(new InputMessage(input));

    /// <summary>The virtual window was resized: ask the host to match (in points).</summary>
    public void ReportScreenSize(ScreenSize size)
    {
        CurrentScreen = size;
        if (Phase == ClientPhase.Streaming) _connection?.Send(new ScreenSizeMessage(size));
    }

    public void RequestMenu() => _connection?.Send(new RequestMenuMessage());

    public void PerformMenu(List<int> path) => _connection?.Send(new MenuActionMessage(path));

    /// <summary>Pull the host's clipboard onto this PC.</summary>
    public void RequestClipboard() => _connection?.Send(new RequestClipboardMessage());

    /// <summary>Push clipboard content (text, image, files) to the host.</summary>
    public void SendClipboard(ClipboardContent content)
    {
        if (content.IsEmpty) { StatusMessage?.Invoke("This PC's clipboard is empty"); return; }
        _connection?.Send(new ClipboardMessage(content));
        StatusMessage?.Invoke($"Sent {content.Items.Count} item{(content.Items.Count == 1 ? "" : "s")} to {ServerName}");
    }

    // MARK: Incoming

    private void Handle(ControlMessage message)
    {
        switch (message)
        {
            case HelloAckMessage ack:
                ServerName = ack.Ack.ServerName;
                HostScale = ack.Ack.HostScale;
                ServerFeatures = new HashSet<string>(ack.Ack.Features ?? [], StringComparer.Ordinal);
                Apps = ack.Ack.Apps;
                if (!string.IsNullOrEmpty(ack.Ack.IssuedToken))
                {
                    Settings.PairTokens[_hostKey] = ack.Ack.IssuedToken;
                    Settings.Save();
                }
                StartKeepalive();
                SetPhase(ClientPhase.Apps);
                AppsChanged?.Invoke();
                break;
            case AuthFailedMessage failed:
                // A rejected token is stale (the host forgot this device): drop it and ask for the code.
                if (Settings.PairTokens.Remove(_hostKey)) Settings.Save();
                PairingError = failed.Reason;
                if (Phase == ClientPhase.Pairing) PhaseChanged?.Invoke(); else SetPhase(ClientPhase.Pairing);
                break;
            case AppListMessage list:
                Apps = list.Apps;
                AppsChanged?.Invoke();
                break;
            case LaunchableAppsMessage list:
                Launchable = list.Apps;
                LaunchableChanged?.Invoke();
                break;
            case WindowGeometryMessage geometry:
                Geometry = geometry.Geometry;
                GeometryChanged?.Invoke();
                break;
            case MenuTreeMessage menu:
                Menu = menu.Menus;
                MenuChanged?.Invoke();
                break;
            case ClipboardMessage clipboard:
                ClipboardReceived?.Invoke(clipboard.Content);
                break;
            case ErrorMessage error:
                ErrorOccurred?.Invoke(error.Message);
                break;
            case PingMessage ping:
                _connection?.Send(new PongMessage(ping.Value));
                break;
        }
    }

    // MARK: Stats (called from the video thread)

    private double? _latencyEma;
    private int _frameCount;
    private long _windowStart = Environment.TickCount64;

    /// <param name="latencyMs">Capture → present, or NaN when the host's clock cannot be compared with ours.</param>
    public void RecordFrame(double latencyMs)
    {
        if (double.IsFinite(latencyMs))
            _latencyEma = _latencyEma is { } ema ? ema * 0.9 + latencyMs * 0.1 : latencyMs;
        _frameCount++;
        var now = Environment.TickCount64;
        var elapsed = now - _windowStart;
        if (elapsed < 500) return;
        var fps = _frameCount * 1000.0 / elapsed;
        var latency = _latencyEma ?? double.NaN;
        _frameCount = 0;
        _windowStart = now;
        Post(() =>
        {
            Fps = fps;
            LatencyMs = latency;
            StatsChanged?.Invoke();
        });
    }
}
