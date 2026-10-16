// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RemoteApp.Protocol;

namespace RemoteApp.Client.Video;

/// <summary>
/// Owns the decode thread: wire frames in, decoded pictures converted to BGRA
/// and presented. Config changes, resets and the window's surface commands
/// (attach, resize, redraw, detach) travel through the same queue, so ordering
/// is preserved and — importantly — the Direct2D objects are created and used
/// on this one thread only (COM interop refuses to share them across
/// apartments). If the queue backs up, non-keyframes are dropped until the
/// next keyframe so the picture catches up instead of lagging.
/// </summary>
internal sealed unsafe class VideoPipeline : IDisposable
{
    private sealed record ResetCommand;
    private sealed record AttachCommand(IntPtr Hwnd, int Width, int Height);
    private sealed record DetachCommand(ManualResetEventSlim Done);
    private sealed record ResizeCommand(int Width, int Height);
    private sealed record RedrawCommand;

    private const int BacklogThreshold = 12;

    private readonly BlockingCollection<object> _queue = new(new ConcurrentQueue<object>());
    private readonly Thread _thread;
    private H264Decoder? _decoder;
    private D2DPresenter? _presenter;
    private byte[]? _bgra;
    private int _bgraWidth;
    private int _bgraHeight;
    private bool _skipping;
    private ulong _currentCaptureWallMicros;
    private int _framesSeen;
    private int _presentedSinceConfig;
    private string? _dumpPath;
    private Rectangle _videoRect;

    /// <summary>Diagnostics: save the next decoded picture as a PNG.</summary>
    public void DumpNextFrame(string path) => _dumpPath = path;

    /// <summary>Where the last frame was drawn in the attached surface, client pixels (for input mapping).</summary>
    public Rectangle VideoRect => _videoRect;

    /// <summary>Raised on the decode thread with the end-to-end latency (capture → present) in ms.</summary>
    public event Action<double>? FramePresented;

    /// <summary>Raised on the decode thread when decoding fails.</summary>
    public event Action<string>? Error;

    /// <summary>Diagnostics (configuration, first frames' NAL layout); raised on the decode thread.</summary>
    public event Action<string>? Info;

    public VideoPipeline()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "remoteapp-video", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Configure(VideoConfig config) => Enqueue(config);

    public void Enqueue(VideoFrame frame) => Enqueue((object)frame);

    /// <summary>Discard queued frames and wait for a keyframe (new stream selected).</summary>
    public void Reset() => Enqueue(new ResetCommand());

    /// <summary>Draw into this window from now on.</summary>
    public void AttachSurface(IntPtr hwnd, int width, int height) => Enqueue(new AttachCommand(hwnd, width, height));

    /// <summary>Stop drawing into the window; blocks until the Direct2D target is released.</summary>
    public void DetachSurface()
    {
        using var done = new ManualResetEventSlim(false);
        Enqueue(new DetachCommand(done));
        if (!_queue.IsAddingCompleted) done.Wait(TimeSpan.FromSeconds(3));
    }

    public void ResizeSurface(int width, int height) => Enqueue(new ResizeCommand(width, height));

    public void RedrawSurface() => Enqueue(new RedrawCommand());

    private void Enqueue(object item)
    {
        try { _queue.Add(item); }
        catch (InvalidOperationException) { /* disposed */ }
    }

    private void Run()
    {
        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                switch (item)
                {
                    case VideoConfig config:
                        HandleConfig(config);
                        break;
                    case VideoFrame frame:
                        HandleFrame(frame);
                        break;
                    case ResetCommand:
                        _decoder?.Reset();
                        _skipping = false;
                        break;
                    case AttachCommand attach:
                        _presenter?.Dispose();
                        _presenter = null;
                        try
                        {
                            _presenter = new D2DPresenter(attach.Hwnd, attach.Width, attach.Height);
                        }
                        catch (COMException e)
                        {
                            Error?.Invoke($"Direct2D unavailable: 0x{e.HResult:X8}");
                        }
                        break;
                    case DetachCommand detach:
                        _presenter?.Dispose();
                        _presenter = null;
                        _videoRect = Rectangle.Empty;
                        detach.Done.Set();
                        break;
                    case ResizeCommand resize:
                        _presenter?.Resize(resize.Width, resize.Height);
                        if (_presenter != null) _videoRect = _presenter.VideoRect;
                        break;
                    case RedrawCommand:
                        _presenter?.Redraw();
                        break;
                }
            }
        }
        catch (ObjectDisposedException) { }
        finally
        {
            _presenter?.Dispose();
            _presenter = null;
            _decoder?.Dispose();
            _decoder = null;
        }
    }

    private void HandleConfig(VideoConfig config)
    {
        try
        {
            _decoder ??= new H264Decoder();
            _decoder.Configure(config);
            _skipping = false;
            _framesSeen = 0;
            _presentedSinceConfig = 0;
            Info?.Invoke($"decoder configured: {config.PixelWidth}×{config.PixelHeight}, parameter sets " +
                         string.Join("/", config.ParameterSets.Select(p => $"type {H264Nal.NalType(p)} ({p.Length} B)")) +
                         $"; {_decoder.Description}");
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or NotSupportedException)
        {
            Error?.Invoke($"Video decoder setup failed: {e.Message}");
        }
    }

    private void HandleFrame(VideoFrame frame)
    {
        if (_decoder == null || !_decoder.IsConfigured) return;
        if (_framesSeen++ < 3)
        {
            var nals = H264Nal.SplitAvcc(frame.Data);
            Info?.Invoke($"frame {_framesSeen}: keyframe {frame.IsKeyframe}, pts {frame.PtsMicros} µs, {frame.Data.Length} B, NALs " +
                         string.Join(" ", nals.Select(n => $"{H264Nal.NalType(n.Span)}:{n.Length}")));
        }
        if (!_skipping && _queue.Count > BacklogThreshold && !frame.IsKeyframe) _skipping = true;
        if (_skipping)
        {
            if (!frame.IsKeyframe) return;
            _skipping = false;
        }
        _currentCaptureWallMicros = frame.CaptureWallMicros;
        try
        {
            _decoder.Decode(frame, Present);
        }
        catch (COMException e)
        {
            Error?.Invoke($"Video decode error: {e.Message}");
            _decoder.Reset();
        }
    }

    private void Present(DecodedFrame frame)
    {
        var presenter = _presenter;
        if (presenter == null) return;
        var stride = frame.Width * 4;
        if (_bgra == null || _bgraWidth != frame.Width || _bgraHeight != frame.Height)
        {
            _bgra = new byte[stride * frame.Height];
            _bgraWidth = frame.Width;
            _bgraHeight = frame.Height;
        }
        fixed (byte* dst = _bgra)
        {
            Nv12Converter.ToBgra(frame.Y, frame.YStride, frame.UV, frame.UVStride, frame.Width, frame.Height, dst, stride);
            presenter.Present(dst, frame.Width, frame.Height, stride);
            if (_dumpPath is { } dumpPath)
            {
                _dumpPath = null;
                try
                {
                    using var bitmap = new Bitmap(frame.Width, frame.Height, stride, System.Drawing.Imaging.PixelFormat.Format32bppRgb, (IntPtr)dst);
                    bitmap.Save(dumpPath, System.Drawing.Imaging.ImageFormat.Png);
                    Info?.Invoke($"dumped decoded frame {frame.Width}×{frame.Height} to {dumpPath}");
                }
                catch (Exception e) { Info?.Invoke("frame dump failed: " + e.Message); }
            }
        }
        _videoRect = presenter.VideoRect;
        if (_presentedSinceConfig++ < 2)
            Info?.Invoke($"presented {frame.Width}×{frame.Height} (Y stride {frame.YStride}) into {presenter.VideoRect}");
        var nowMicros = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
        var latency = _currentCaptureWallMicros == 0 ? 0 : ((double)nowMicros - _currentCaptureWallMicros) / 1000.0;
        FramePresented?.Invoke(latency);
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(2))) { /* background thread; let it die with the process */ }
        _queue.Dispose();
    }
}
