// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Protocol;
using RemoteApp.Server.Capture;
using RemoteApp.Server.Hosting;
using RemoteApp.WindowsCommon;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace RemoteApp.Server;

/// <summary>
/// One connected client: auth, app selection, capture → encode → send, input,
/// menus and clipboard (the Windows counterpart of the Swift <c>ClientSession</c>).
/// Protocol callbacks arrive on the network thread; streaming state is guarded
/// by <see cref="_sync"/>; clipboard work is marshalled to the UI (STA) thread.
/// </summary>
internal sealed class ClientSession
{
    public sealed record Stats(double Fps, double Kbps, int DroppedFrames);

    private const int FrameRate = 60;
    private const int Bitrate = 12_000_000;
    private const int MaxInFlight = 4;

    public Guid Id { get; } = Guid.NewGuid();
    public bool IsAuthenticated { get; private set; }
    public string ClientName { get; private set; } = "client";
    public string RemoteEndpoint { get; }

    public event Action<string>? Log;
    public event Action<Stats>? StatsChanged;
    public event Action? Authenticated;
    public event Action? Closed;

    private readonly MessageConnection _connection;
    private readonly Func<string> _pairingCode;
    private readonly PairingStore _pairings;
    private readonly string _serverName;
    private readonly SynchronizationContext _ui;
    private ScreenSize _clientScreen = new(1280, 800, 1);

    // Streaming state.
    private readonly object _sync = new();
    private HWND _hwnd;
    private uint _pid;
    private Rectangle _bounds;
    private HMONITOR _monitor;
    private double _scale = 1;
    private FramePipeline? _pipeline;
    private InputInjector? _injector;
    private System.Threading.Timer? _geometryTimer;
    private Dictionary<int, ShareableApps.Entry> _lastApps = new();

    private int _statFrames;
    private long _statBytes;
    private long _statStart = Environment.TickCount64;

    public ClientSession(MessageConnection connection, string remoteEndpoint, Func<string> pairingCode,
        PairingStore pairings, string serverName, SynchronizationContext ui)
    {
        _connection = connection;
        RemoteEndpoint = remoteEndpoint;
        _pairingCode = pairingCode;
        _pairings = pairings;
        _serverName = serverName;
        _ui = ui;
    }

    public void Start()
    {
        _connection.ControlReceived += Handle;
        _connection.Closed += error =>
        {
            Emit($"connection closed{(error == null ? "" : ": " + error.Message)}");
            Teardown();
            Closed?.Invoke();
        };
        _connection.Start();
    }

    public void Close() => _connection.Close();

    private void Emit(string line) => Log?.Invoke($"[{ClientName}] {line}");

    private void Send(ControlMessage message) => _connection.Send(message);

    // MARK: Message handling

    private void Handle(ControlMessage message)
    {
        try
        {
            if (!IsAuthenticated)
            {
                HandleUnauthenticated(message);
                return;
            }
            switch (message)
            {
                case ListAppsMessage:
                    Send(new AppListMessage(FetchApps()));
                    break;
                case SelectAppMessage select:
                    SelectApp((uint)select.Pid);
                    break;
                case RequestLaunchableMessage:
                    Send(new LaunchableAppsMessage(LaunchableApps.Fetch()));
                    break;
                case LaunchAppMessage launch:
                    LaunchApp(launch.BundleIdentifier);
                    break;
                case ScreenSizeMessage screen:
                    _clientScreen = screen.Screen;
                    ApplyClientSize();
                    break;
                case InputMessage input:
                    _injector?.Handle(input.Event);
                    break;
                case RequestMenuMessage:
                    SendMenu();
                    break;
                case MenuActionMessage action:
                    PerformMenu(action.Path);
                    break;
                case RequestClipboardMessage:
                    Send(new ClipboardMessage(OnUi(ClipboardBridge.Read)));
                    break;
                case ClipboardMessage clipboard:
                    var saved = OnUi(() => ClipboardBridge.Apply(clipboard.Content));
                    Emit($"clipboard set ({clipboard.Content.Items.Count} item{(clipboard.Content.Items.Count == 1 ? "" : "s")}" +
                         (saved.Count > 0 ? $", {saved.Count} saved to {ClipboardBridge.ReceivedDirectory})" : ")"));
                    break;
                case PingMessage ping:
                    Send(new PongMessage(ping.Value));
                    break;
            }
        }
        catch (Exception e)
        {
            Emit($"error handling {message.GetType().Name}: {e.Message}");
            Send(new ErrorMessage(e.Message));
        }
    }

    private void HandleUnauthenticated(ControlMessage message)
    {
        if (message is PingMessage ping) { Send(new PongMessage(ping.Value)); return; }
        if (message is not HelloMessage { Hello: var hello })
        {
            Send(new ErrorMessage("hello required"));
            _connection.Close();
            return;
        }
        ClientName = hello.ClientName;
        if (hello.ProtocolVersion != ProtocolConstants.ProtocolVersion)
        {
            Send(new ErrorMessage("protocol version mismatch"));
            _connection.Close();
            return;
        }
        string? issued = null;
        if (hello.Token != null && _pairings.IsValid(hello.DeviceID, hello.Token))
        {
            Emit($"authenticated by remembered pairing ({hello.ClientName})");
        }
        else if (PairingCode.Verify(_pairingCode(), hello.Credential))
        {
            issued = _pairings.Issue(hello.DeviceID, hello.ClientName);
            Emit($"paired {hello.ClientName}; device will be remembered");
        }
        else
        {
            Emit("auth failed");
            Send(new AuthFailedMessage("wrong pairing code"));
            return;
        }
        IsAuthenticated = true;
        _clientScreen = hello.Screen;
        Authenticated?.Invoke();
        var hostScale = WindowControl.Scale(WindowControl.PrimaryMonitor());
        Send(new HelloAckMessage(new HelloAck(_serverName, hostScale, FetchApps(), issued)));
    }

    private T OnUi<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;
        _ui.Send(_ =>
        {
            try { result = action(); }
            catch (Exception e) { failure = e; }
        }, null);
        if (failure != null) throw failure;
        return result;
    }

    private List<AppInfo> FetchApps()
    {
        var entries = ShareableApps.Fetch();
        _lastApps = entries.ToDictionary(e => e.Info.Pid, e => e);
        return entries.Select(e => e.Info).ToList();
    }

    // MARK: Streaming

    private void LaunchApp(string identifier)
    {
        Emit($"launching {identifier}");
        var pid = LaunchableApps.Launch(identifier, Emit);
        if (pid == null)
        {
            Send(new ErrorMessage($"{identifier} launched but opened no window"));
            return;
        }
        SelectApp(pid.Value);
    }

    private void SelectApp(uint pid)
    {
        lock (_sync)
        {
            Teardown();
            var hwnd = (_lastApps.TryGetValue((int)pid, out var entry) && WindowControl.IsAlive(entry.MainWindow)
                           ? entry.MainWindow
                           : WindowControl.MainWindow(pid))
                       ?? HWND.Null;
            if (hwnd.IsNull)
            {
                Send(new ErrorMessage($"no window found for process {pid}"));
                return;
            }
            var name = entry?.Info.Name ?? WindowControl.Title(hwnd);

            // Place the window at the top-left of the primary work area, sized to
            // the client's screen (in points × this monitor's scale); the app may clamp.
            _monitor = WindowControl.PrimaryMonitor();
            _scale = WindowControl.Scale(_monitor);
            var work = WindowControl.WorkArea(_monitor);
            var requested = new Rectangle(work.X, work.Y,
                Math.Max(2, (int)Math.Round(_clientScreen.Width * _scale)) & ~1,
                Math.Max(2, (int)Math.Round(_clientScreen.Height * _scale)) & ~1);
            WindowControl.Activate(hwnd);
            var bounds = WindowControl.SetVisibleBounds(hwnd, requested);
            Emit($"{name}: requested {requested.Width}×{requested.Height}, got {bounds.Width}×{bounds.Height} px (scale {_scale:0.##})");

            _hwnd = hwnd;
            _pid = pid;
            _bounds = bounds;
            _injector = new InputInjector(hwnd, pid, bounds) { ScrollScale = _scale };
            _injector.Log += Emit;
            try
            {
                StartPipeline();
            }
            catch (Exception e)
            {
                Emit($"capture failed: {e.Message}");
                Send(new ErrorMessage($"capture failed: {e.Message}"));
                Teardown();
                return;
            }
            SendGeometry();
            _geometryTimer = new System.Threading.Timer(_ => RefreshGeometry(), null, 500, 500);
        }
        SendMenu();
    }

    private static Rectangle EvenSize(Rectangle r) =>
        new(r.X, r.Y, Math.Max(2, r.Width & ~1), Math.Max(2, r.Height & ~1));

    /// <summary>Caller holds <see cref="_sync"/>.</summary>
    private void StartPipeline()
    {
        var gpu = GraphicsDevice.Shared(Emit) ?? throw new InvalidOperationException("Direct3D 11 is not available on this PC");
        var pipeline = new FramePipeline(gpu, _monitor, _bounds, FrameRate, Bitrate)
        {
            ShouldDrop = () => _connection.QueuedVideoFrames >= MaxInFlight,
        };
        pipeline.ConfigChanged += config => Send(new VideoConfigMessage(config));
        pipeline.FrameEncoded += SendVideo;
        pipeline.Error += line =>
        {
            Emit(line);
            Send(new ErrorMessage(line));
        };
        _pipeline = pipeline;
        _statStart = Environment.TickCount64;
        pipeline.Start();
        Emit($"pipeline: {pipeline.Description} @ {FrameRate} fps");
    }

    private void SendVideo(VideoFrame frame)
    {
        _connection.Send(frame);
        Interlocked.Increment(ref _statFrames);
        Interlocked.Add(ref _statBytes, frame.Data.Length);
        var elapsed = Environment.TickCount64 - _statStart;
        if (elapsed < 1000) return;
        var frames = Interlocked.Exchange(ref _statFrames, 0);
        var bytes = Interlocked.Exchange(ref _statBytes, 0);
        _statStart = Environment.TickCount64;
        StatsChanged?.Invoke(new Stats(frames * 1000.0 / elapsed, bytes * 8.0 / elapsed, _pipeline?.DroppedFrames ?? 0));
    }

    /// <summary>Caller holds <see cref="_sync"/>.</summary>
    private void SendGeometry()
    {
        var size = EvenSize(_bounds);
        Send(new WindowGeometryMessage(new WindowGeometry(
            Math.Round(_bounds.Width / _scale), Math.Round(_bounds.Height / _scale),
            _pipeline?.Width ?? size.Width, _pipeline?.Height ?? size.Height, WindowControl.Title(_hwnd))));
    }

    /// <summary>The client's window changed size: resize the host window to match.</summary>
    private void ApplyClientSize()
    {
        lock (_sync)
        {
            if (_hwnd.IsNull || !WindowControl.IsAlive(_hwnd)) return;
            var work = WindowControl.WorkArea(_monitor);
            var requested = new Rectangle(work.X, work.Y,
                Math.Max(2, (int)Math.Round(_clientScreen.Width * _scale)) & ~1,
                Math.Max(2, (int)Math.Round(_clientScreen.Height * _scale)) & ~1);
            WindowControl.SetVisibleBounds(_hwnd, requested);
        }
        RefreshGeometry();
    }

    /// <summary>Follows the window if the user (or the app) moves or resizes it; rebuilds the pipeline on size changes.</summary>
    private void RefreshGeometry()
    {
        lock (_sync)
        {
            if (_hwnd.IsNull) return;
            if (!WindowControl.IsAlive(_hwnd))
            {
                Emit("shared window closed");
                Send(new ErrorMessage("The shared window was closed."));
                Teardown();
                return;
            }
            var bounds = WindowControl.VisibleBounds(_hwnd);
            if (bounds == _bounds) return;
            var sizeChanged = EvenSize(bounds).Size != EvenSize(_bounds).Size;
            _bounds = bounds;
            if (_injector != null) _injector.Bounds = bounds;
            if (sizeChanged)
            {
                // The encoder is fixed-size: rebuild capture, conversion and encoder.
                if (_pipeline != null) Emit($"pipeline stopped: {_pipeline.TimingSummary}, {_pipeline.DroppedFrames} dropped");
                _pipeline?.Dispose();
                _pipeline = null;
                try { StartPipeline(); }
                catch (Exception e) { Emit($"restart failed: {e.Message}"); Send(new ErrorMessage($"capture restart failed: {e.Message}")); }
                Emit($"window resized to {bounds.Width}×{bounds.Height} px");
            }
            else
            {
                _pipeline?.SetCrop(bounds);
            }
            SendGeometry();
        }
    }

    private void Teardown()
    {
        lock (_sync)
        {
            _geometryTimer?.Dispose();
            _geometryTimer = null;
            if (_pipeline != null)
            {
                Emit($"pipeline stopped: {_pipeline.TimingSummary}, {_pipeline.DroppedFrames} dropped");
                _pipeline.Dispose();
                _pipeline = null;
            }
            _injector?.Dispose();
            _injector = null;
            _hwnd = HWND.Null;
            _pid = 0;
        }
    }

    // MARK: Menus

    private void SendMenu()
    {
        var hwnd = _hwnd;
        if (hwnd.IsNull) return;
        Task.Run(() =>
        {
            try { Send(new MenuTreeMessage(MenuReader.Read(hwnd))); }
            catch (Exception e) { Emit($"menu read failed: {e.Message}"); }
        });
    }

    private void PerformMenu(List<int> path)
    {
        var hwnd = _hwnd;
        if (hwnd.IsNull) return;
        Task.Run(async () =>
        {
            var ok = MenuReader.Perform(hwnd, path);
            Emit($"menu action [{string.Join(',', path)}] {(ok ? "performed" : "failed")}");
            await Task.Delay(150);
            try { Send(new MenuTreeMessage(MenuReader.Read(hwnd))); }
            catch (Exception) { }
        });
    }
}
