// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Drawing.Imaging;
using System.Text;

namespace RemoteApp.Client.Forms;

/// <summary>Arguments for the unattended smoke run (see <c>Program</c>).</summary>
internal sealed record SmokeOptions(string Host, string? Code, string? AppName, string ReportPath, string ScreenshotPath, int Seconds);

/// <summary>
/// Drives the real UI without a user: connect, pair, select an app, stream
/// for a while, then screenshot the virtual window and write a report with
/// frame/latency counts. Exit code 0 means frames were decoded and shown.
/// </summary>
internal sealed class SmokeTest
{
    private readonly SmokeOptions _options;
    private readonly ClientModel _model;
    private readonly StringBuilder _log = new();
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly System.Windows.Forms.Timer _steps = new() { Interval = 1500 };
    private int _step;
    private int _presentedFrames;
    private bool _paired;
    private bool _selected;
    private bool _finished;

    public Func<StreamForm?>? StreamFormProvider { get; init; }
    public Action? Exit { get; init; }
    public Action<string>? DumpFrame { get; init; }
    public Func<string>? ExtraStats { get; init; }

    public SmokeTest(SmokeOptions options, ClientModel model)
    {
        _options = options;
        _model = model;
        _model.ErrorOccurred += message => Log("error: " + message);
        _model.StatusMessage += message => Log("status: " + message);
        _model.GeometryChanged += () => Log($"geometry: {_model.Geometry}");
        _model.MenuChanged += () => Log($"menu: {_model.Menu.Count} top-level menus" +
                                        (_model.Menu.Count > 0 ? ": " + string.Join(", ", _model.Menu.Select(m => $"{m.Title} ({m.ChildrenOrEmpty.Count})")) : ""));
        _model.ClipboardReceived += content => Log($"clipboard from host: {content.Items.Count} items" +
                                                    (content.Text != null ? $", text \"{Truncate(content.Text)}\"" : ""));
        _timer.Tick += (_, _) => Finish("timer");
        _steps.Tick += (_, _) => RunStep();
        Application.ThreadException += (_, e) => Log("unhandled UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("unhandled exception: " + e.ExceptionObject);
    }

    public void Log(string line) => _log.AppendLine($"{DateTime.Now:HH:mm:ss.fff} {line}");

    public void FramePresented() => Interlocked.Increment(ref _presentedFrames);

    public void Begin()
    {
        // REMOTEAPP_SMOKE_SIZE=2560x1440 opens the virtual window at that size (in points).
        var size = Environment.GetEnvironmentVariable("REMOTEAPP_SMOKE_SIZE")?.Split('x');
        if (size is { Length: 2 } && int.TryParse(size[0], out var w) && int.TryParse(size[1], out var h) && w > 100 && h > 100)
        {
            _model.Settings.StreamWidth = w;
            _model.Settings.StreamHeight = h;
            Log($"initial window size {w}×{h} pt");
        }
        Log($"connecting to {_options.Host}");
        _model.Connect(_options.Host);
    }

    /// <summary>Called on every phase change; returns true if the smoke test handled the phase.</summary>
    public bool OnPhase()
    {
        if (_finished) return true;
        switch (_model.Phase)
        {
            case ClientPhase.Pairing:
                if (_options.Code == null) { Log("pairing required but no code given"); Finish("pairing"); return true; }
                if (_model.PairingError != null) { Log("pairing failed: " + _model.PairingError); Finish("pairing"); return true; }
                if (_paired) return true;
                _paired = true;
                Log("pairing with code");
                _model.Pair(_options.Code);
                return true;
            case ClientPhase.Apps:
                if (_selected) return false;
                Log($"apps: {string.Join(", ", _model.Apps.Select(a => $"{a.Name} (pid {a.Pid}, {a.WindowCount} windows, icon {(a.IconPNG?.Length ?? 0)} B)"))}");
                var app = _options.AppName == null
                    ? _model.Apps.FirstOrDefault()
                    : _model.Apps.FirstOrDefault(a => a.Name.Contains(_options.AppName, StringComparison.OrdinalIgnoreCase));
                if (app == null)
                {
                    if (_options.AppName == null) { Log("no running apps"); Finish("no app"); return true; }
                    // Not running: launch it through the host's installed-apps list.
                    Log($"{_options.AppName} is not running; asking for launchable apps");
                    _selected = true;
                    _model.LaunchableChanged += LaunchWhenListed;
                    _model.RequestLaunchable();
                    return true;
                }
                _selected = true;
                Log($"selecting {app.Name}");
                _model.Select(app);
                StartStreamingTimers();
                return false;
            case ClientPhase.Disconnected:
                Log("disconnected: " + (_model.LastError ?? "(no error)"));
                if (_model.LastException != null) Log(_model.LastException.ToString());
                Finish("disconnected");
                return true;
            default:
                return false;
        }
    }

    private void LaunchWhenListed()
    {
        _model.LaunchableChanged -= LaunchWhenListed;
        var name = _options.AppName!;
        var app = _model.Launchable.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                  ?? _model.Launchable.FirstOrDefault(a => a.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        Log($"launchable: {_model.Launchable.Count} apps; " + (app == null ? "no match" : $"launching {app.Name} ({app.BundleIdentifier})"));
        if (app == null) { Finish("no app"); return; }
        _model.Launch(app);
        StartStreamingTimers();
    }

    private void StartStreamingTimers()
    {
        _timer.Interval = Math.Max(1, _options.Seconds) * 1000;
        _timer.Start();
        _steps.Start();
    }

    private static string Truncate(string s) => s.Length <= 60 ? s.Replace("\r", "\\r").Replace("\n", "\\n") : s[..60].Replace("\r", "\\r").Replace("\n", "\\n") + "…";

    /// <summary>Exercises input, resize matching and the clipboard while streaming, one step per tick.</summary>
    private void RunStep()
    {
        if (_finished || _model.Phase != ClientPhase.Streaming) return;
        var form = StreamFormProvider?.Invoke();
        switch (_step++)
        {
            case 0:
                // Click in the middle of the window, go to the end of the text and type a line.
                Log("input: click + type");
                _model.SendInput(new Protocol.TapEvent(new Protocol.NormalizedPoint(0.5, 0.6)));
                _model.SendInput(new Protocol.KeyInputEvent(Protocol.KeyEvent.ForKeyCode(Protocol.KeyCodes.End, Protocol.KeyModifiers.Command)));
                _model.SendInput(new Protocol.KeyInputEvent(Protocol.KeyEvent.ForKeyCode(Protocol.KeyCodes.Return)));
                _model.SendInput(new Protocol.KeyInputEvent(Protocol.KeyEvent.ForText("Typed by the Windows client via SendInput: héllo wörld 123")));
                break;
            case 1:
                if (form == null) { Log("resize: no stream window"); break; }
                Log($"resize: window client {form.ClientSize.Width}×{form.ClientSize.Height} → shrinking by 280×180 px");
                form.ClientSize = new Size(form.ClientSize.Width - 280, form.ClientSize.Height - 180);
                break;
            case 2:
                if (Environment.GetEnvironmentVariable("REMOTEAPP_SMOKE_NO_CLIPBOARD") == "1") { Log("clipboard: skipped"); break; }
                Log("clipboard: select all + copy on the host, then pull the host clipboard");
                _model.SendInput(new Protocol.KeyInputEvent(Protocol.KeyEvent.ForText("a", Protocol.KeyModifiers.Command)));
                _model.SendInput(new Protocol.KeyInputEvent(Protocol.KeyEvent.ForText("c", Protocol.KeyModifiers.Command)));
                _model.SendInput(new Protocol.KeyInputEvent(Protocol.KeyEvent.ForKeyCode(Protocol.KeyCodes.End, Protocol.KeyModifiers.Command)));
                break;
            case 3:
                if (Environment.GetEnvironmentVariable("REMOTEAPP_SMOKE_NO_CLIPBOARD") != "1") _model.RequestClipboard();
                _model.RequestMenu();
                DumpFrame?.Invoke(Path.ChangeExtension(_options.ScreenshotPath, ".frame.png"));
                break;
            default:
                _steps.Stop();
                break;
        }
    }

    private void Finish(string reason)
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        _steps.Stop();
        Log($"finishing ({reason}): presented {_presentedFrames} frames, {_model.Fps:0.0} fps, {_model.LatencyMs:0.0} ms capture→present latency");
        if (ExtraStats != null) Log("video stats: " + ExtraStats());
        try
        {
            var form = StreamFormProvider?.Invoke();
            if (form is { IsHandleCreated: true, Visible: true })
            {
                form.Activate();
                Application.DoEvents();
                var rect = form.RectangleToScreen(form.ClientRectangle);
                using var bitmap = new Bitmap(Math.Max(1, rect.Width), Math.Max(1, rect.Height));
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(rect.Location, Point.Empty, rect.Size);
                bitmap.Save(_options.ScreenshotPath, ImageFormat.Png);
                Log($"screenshot {rect.Width}×{rect.Height} saved");
            }
            else
            {
                Log("no stream window to screenshot");
            }
        }
        catch (Exception e)
        {
            Log("screenshot failed: " + e.Message);
        }
        Log(_presentedFrames > 0 ? "RESULT: PASS" : "RESULT: FAIL");
        try { File.WriteAllText(_options.ReportPath, _log.ToString()); } catch (Exception) { }
        Environment.ExitCode = _presentedFrames > 0 ? 0 : 1;
        Exit?.Invoke();
    }
}
