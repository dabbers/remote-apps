// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using RemoteApp.Protocol;

namespace RemoteApp.Server;

/// <summary>
/// Listener and session owner (the Windows counterpart of the Swift
/// <c>ServerModel</c>). Binds TLS listeners to the Tailscale address (port
/// 53123) and loopback (53124) only — never a LAN or public interface — and
/// hands accepted connections to <see cref="ClientSession"/>s. All public state
/// is read on the UI thread; <see cref="Changed"/> fires there.
/// </summary>
internal sealed class ServerModel
{
    public string PairingCode { get; private set; } = Server.PairingCode.Random();
    public string ListenerState { get; private set; } = "stopped";
    public List<string> BoundEndpoints { get; private set; } = [];
    public string? ClientState { get; private set; }
    public ClientSession.Stats? Stats { get; private set; }
    public TailscaleInfo? Tailscale { get; private set; }
    public string? TailnetWarning { get; private set; }
    public string? Fingerprint { get; private set; }
    public string ServerName { get; } = Environment.MachineName;
    public PairingStore Pairings { get; } = new();
    public int PairedCount => Pairings.Count;
    public int Port => ProtocolConstants.DefaultPort;

    /// <summary>The address a client should connect to: MagicDNS name when on the tailnet.</summary>
    public string ConnectTarget => Tailscale?.DnsName ?? "127.0.0.1";

    public event Action? Changed;
    public event Action<string>? LogLine;

    private readonly SynchronizationContext _ui;
    private readonly List<TcpListener> _listeners = [];
    private readonly Dictionary<Guid, ClientSession> _sessions = new();
    private X509Certificate2? _certificate;
    private CancellationTokenSource? _cts;
    private readonly StreamWriter? _logFile;

    public ServerModel()
    {
        _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("create the model on the UI thread");
        try
        {
            Directory.CreateDirectory(TlsIdentity.Directory);
            _logFile = new StreamWriter(Path.Combine(TlsIdentity.Directory, "RemoteAppServer.log"), append: true) { AutoFlush = true };
        }
        catch (Exception) { _logFile = null; }
    }

    private void Post(Action action) => _ui.Post(_ => action(), null);

    public void Start()
    {
        Stop();
        Tailscale = TailscaleInfo.Current();
        TailnetWarning = Tailscale switch
        {
            null => "Tailscale not detected. Binding to loopback only; no remote device can reach this server.",
            { Online: false } => "Tailscale is installed but this PC is not online on the tailnet.",
            _ => null,
        };

        try
        {
            _certificate = TlsIdentity.LoadOrCreate();
            Fingerprint = TlsIdentity.Fingerprint(_certificate);
        }
        catch (Exception e)
        {
            Append($"TLS identity unavailable: {e.Message}");
            ListenerState = "failed";
            Changed?.Invoke();
            return;
        }

        _cts = new CancellationTokenSource();
        ListenerState = "starting";
        Append($"connect target “{ConnectTarget}:{Port}”; pairing code {PairingCode}");
        if (TailnetWarning != null) Append($"⚠ {TailnetWarning}");

        var bound = new List<string>();
        if (Tailscale != null)
        {
            Bind(IPAddress.Parse(Tailscale.IPv4), Port, "tailnet", bound);
            if (Tailscale.IPv6 != null && IPAddress.TryParse(Tailscale.IPv6, out var v6)) Bind(v6, Port, "tailnet v6", bound);
        }
        Bind(IPAddress.Loopback, ProtocolConstants.LoopbackPort, "loopback", bound);
        BoundEndpoints = bound;
        ListenerState = _listeners.Count > 0 ? "ready" : "failed";
        Changed?.Invoke();
    }

    private void Bind(IPAddress address, int port, string label, List<string> bound)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            _listeners.Add(listener);
            var endpoint = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
            bound.Add($"{label} {endpoint}");
            Append($"listening on {label} {endpoint}");
            _ = AcceptLoop(listener, _cts!.Token);
        }
        catch (SocketException e)
        {
            Append($"bind {label} {address}:{port} failed: {e.Message}");
        }
    }

    private async Task AcceptLoop(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener.AcceptSocketAsync(cancellationToken); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) { Append($"accept failed: {e.Message}"); continue; }
            _ = HandleAccept(socket, cancellationToken);
        }
    }

    private async Task HandleAccept(Socket socket, CancellationToken cancellationToken)
    {
        var remote = socket.RemoteEndPoint?.ToString() ?? "?";
        Stream stream;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            stream = await Transport.AcceptAsync(socket, _certificate!, timeout.Token);
        }
        catch (Exception e)
        {
            Append($"TLS handshake with {remote} failed: {e.Message}");
            socket.Dispose();
            return;
        }
        Post(() => Accept(new MessageConnection(stream), remote));
    }

    private void Accept(MessageConnection connection, string remote)
    {
        var session = new ClientSession(connection, remote, () => PairingCode, Pairings, ServerName, _ui);
        var id = session.Id;
        session.Log += line => Post(() => Append(line));
        session.Authenticated += () => Post(() =>
        {
            // One client at a time: a newly authenticated session replaces any other.
            foreach (var (otherId, other) in _sessions.ToList())
                if (otherId != id && other.IsAuthenticated) other.Close();
            ClientState = $"{session.ClientName} ({remote})";
            Changed?.Invoke();
        });
        session.StatsChanged += stats => Post(() => { Stats = stats; Changed?.Invoke(); });
        session.Closed += () => Post(() =>
        {
            _sessions.Remove(id);
            if (_sessions.Values.All(s => !s.IsAuthenticated)) { ClientState = null; Stats = null; }
            Changed?.Invoke();
        });
        _sessions[id] = session;
        Append($"client connected from {remote}");
        session.Start();
        Changed?.Invoke();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        foreach (var listener in _listeners) { try { listener.Stop(); } catch (Exception) { } }
        _listeners.Clear();
        foreach (var session in _sessions.Values.ToList()) session.Close();
        _sessions.Clear();
        BoundEndpoints = [];
        ClientState = null;
        Stats = null;
        ListenerState = "stopped";
        Changed?.Invoke();
    }

    public void RegenerateCode()
    {
        PairingCode = Server.PairingCode.Random();
        Append($"new pairing code {PairingCode}");
        Changed?.Invoke();
    }

    public void ForgetPairedDevices()
    {
        Pairings.ForgetAll();
        Append("forgot all paired devices; they must enter the code again");
        Changed?.Invoke();
    }

    private void Append(string line)
    {
        var stamped = $"{DateTime.Now:HH:mm:ss}  {line}";
        LogLine?.Invoke(stamped);
        try { _logFile?.WriteLine(stamped); } catch (Exception) { }
    }
}
