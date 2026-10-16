// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteApp.Protocol;

namespace RemoteApp.Client.Video;

/// <summary>
/// Owns the decode thread: wire frames in, decoded pictures presented. With a
/// capable adapter the whole path is on the GPU: the decoder outputs D3D11
/// NV12 textures and a video processor converts, scales and letterboxes them
/// into a swap chain (<see cref="GpuPresenter"/>). Otherwise pictures are
/// decoded to system memory, converted on the CPU and drawn with Direct2D
/// (<see cref="D2DPresenter"/>). Config changes, resets and the window's
/// surface commands travel through the same queue, so ordering is preserved
/// and every Direct3D/Direct2D object is used on this one thread only. If the
/// queue backs up, non-keyframes are dropped until the next keyframe so the
/// picture catches up instead of lagging.
/// </summary>
internal sealed unsafe class VideoPipeline : IDisposable
{
    private sealed record ResetCommand;
    private sealed record AttachCommand(IntPtr Hwnd, int Width, int Height);
    private sealed record DetachCommand(ManualResetEventSlim Done);
    private sealed record ResizeCommand(int Width, int Height);
    private sealed record RedrawCommand;
    private sealed record QueuedFrame(VideoFrame Frame, long ReceivedTicks);

    private const int BacklogThreshold = 12;

    private readonly BlockingCollection<object> _queue = new(new ConcurrentQueue<object>());
    private readonly Thread _thread;
    private GraphicsDevice? _gpu;
    private bool _gpuTried;
    private H264Decoder? _decoder;
    private GpuPresenter? _gpuPresenter;
    private D2DPresenter? _d2dPresenter;
    private byte[]? _bgra;
    private int _bgraWidth;
    private int _bgraHeight;
    private bool _skipping;
    private ulong _currentCaptureWallMicros;
    private long _currentReceivedTicks;
    private int _framesSeen;
    private int _presentedSinceConfig;
    private string? _dumpPath;
    private Rectangle _videoRect;

    // Timing: wire frame received → picture presented (client-side only).
    private long _timedFrames;
    private double _clientLatencySumMs;
    private double _clientLatencyMaxMs;
    private double _decodeSumMs;

    /// <summary>Where the last frame was drawn in the attached surface, client pixels (for input mapping).</summary>
    public Rectangle VideoRect => _videoRect;

    /// <summary>Receive → present timing, averaged over the pipeline's lifetime.</summary>
    public string TimingSummary => _timedFrames == 0 ? "no frames presented"
        : $"{_timedFrames} frames, receive→present avg {_clientLatencySumMs / _timedFrames:0.0} ms (max {_clientLatencyMaxMs:0.0} ms), " +
          $"decode+present call avg {_decodeSumMs / _timedFrames:0.0} ms";

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

    public void Enqueue(VideoFrame frame) => Enqueue(new QueuedFrame(frame, Stopwatch.GetTimestamp()));

    /// <summary>Discard queued frames and wait for a keyframe (new stream selected).</summary>
    public void Reset() => Enqueue(new ResetCommand());

    /// <summary>Draw into this window from now on.</summary>
    public void AttachSurface(IntPtr hwnd, int width, int height) => Enqueue(new AttachCommand(hwnd, width, height));

    /// <summary>Stop drawing into the window; blocks until the GPU targets are released.</summary>
    public void DetachSurface()
    {
        using var done = new ManualResetEventSlim(false);
        Enqueue(new DetachCommand(done));
        if (!_queue.IsAddingCompleted) done.Wait(TimeSpan.FromSeconds(3));
    }

    public void ResizeSurface(int width, int height) => Enqueue(new ResizeCommand(width, height));

    public void RedrawSurface() => Enqueue(new RedrawCommand());

    /// <summary>Diagnostics: save the next decoded picture as a PNG (CPU-decoded pictures only).</summary>
    public void DumpNextFrame(string path) => _dumpPath = path;

    private void Enqueue(object item)
    {
        try { _queue.Add(item); }
        catch (InvalidOperationException) { /* disposed */ }
    }

    private GraphicsDevice? Gpu()
    {
        if (!_gpuTried)
        {
            _gpuTried = true;
            // REMOTEAPP_FORCE_CPU_VIDEO=1 keeps the CPU decode/convert path (diagnostics, comparisons).
            _gpu = Environment.GetEnvironmentVariable("REMOTEAPP_FORCE_CPU_VIDEO") == "1" ? null : GraphicsDevice.TryCreate(line => Info?.Invoke(line));
            if (_gpu != null) Info?.Invoke("GPU path: D3D11 video device ready");
        }
        return _gpu;
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
                    case QueuedFrame queued:
                        HandleFrame(queued);
                        break;
                    case ResetCommand:
                        _decoder?.Reset();
                        _skipping = false;
                        break;
                    case AttachCommand attach:
                        Attach(attach);
                        break;
                    case DetachCommand detach:
                        DisposePresenters();
                        _videoRect = Rectangle.Empty;
                        detach.Done.Set();
                        break;
                    case ResizeCommand resize:
                        try
                        {
                            _gpuPresenter?.Resize(resize.Width, resize.Height);
                            _d2dPresenter?.Resize(resize.Width, resize.Height);
                        }
                        catch (COMException e) { Error?.Invoke($"resize failed: 0x{e.HResult:X8}"); }
                        UpdateVideoRect();
                        break;
                    case RedrawCommand:
                        try
                        {
                            _gpuPresenter?.Redraw();
                            _d2dPresenter?.Redraw();
                        }
                        catch (COMException e) { Error?.Invoke($"redraw failed: 0x{e.HResult:X8}"); }
                        break;
                }
            }
        }
        catch (ObjectDisposedException) { }
        finally
        {
            DisposePresenters();
            _decoder?.Dispose();
            _decoder = null;
            _gpu?.Dispose();
            _gpu = null;
        }
    }

    private void Attach(AttachCommand attach)
    {
        DisposePresenters();
        var gpu = Gpu();
        if (gpu != null)
        {
            try
            {
                _gpuPresenter = new GpuPresenter(gpu, attach.Hwnd, attach.Width, attach.Height);
                return;
            }
            catch (COMException e)
            {
                Info?.Invoke($"GPU presenter unavailable (0x{e.HResult:X8}); using Direct2D and CPU decoding");
                // The decoder must not output GPU textures without a GPU presenter.
                _decoder?.Dispose();
                _decoder = null;
                _gpu?.Dispose();
                _gpu = null;
            }
        }
        try
        {
            _d2dPresenter = new D2DPresenter(attach.Hwnd, attach.Width, attach.Height);
        }
        catch (COMException e)
        {
            Error?.Invoke($"Direct2D unavailable: 0x{e.HResult:X8}");
        }
    }

    private void DisposePresenters()
    {
        _gpuPresenter?.Dispose();
        _gpuPresenter = null;
        _d2dPresenter?.Dispose();
        _d2dPresenter = null;
    }

    private void UpdateVideoRect()
    {
        if (_gpuPresenter != null) _videoRect = _gpuPresenter.VideoRect;
        else if (_d2dPresenter != null) _videoRect = _d2dPresenter.VideoRect;
    }

    private void HandleConfig(VideoConfig config)
    {
        try
        {
            _decoder ??= new H264Decoder(Gpu());
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

    private void HandleFrame(QueuedFrame queued)
    {
        var frame = queued.Frame;
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
        _currentReceivedTicks = queued.ReceivedTicks;
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (_decoder.Decode(frame, Present))
                _decodeSumMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        catch (COMException e)
        {
            Error?.Invoke($"Video decode error: {e.Message}");
            _decoder.Reset();
        }
    }

    private void Present(DecodedFrame frame)
    {
        try
        {
            if (_gpuPresenter != null)
            {
                if (frame.IsGpu)
                    _gpuPresenter.Present(frame.Texture!, frame.Slice, frame.Sample!, frame.Visible, frame.CodedWidth, frame.CodedHeight);
                else
                    _gpuPresenter.PresentCpu(frame.Y, frame.YStride, frame.UV, frame.UVStride, frame.Width, frame.Height);
                _videoRect = _gpuPresenter.VideoRect;
            }
            else if (_d2dPresenter != null)
            {
                if (frame.IsGpu) return;   // no CPU pixels to draw with Direct2D
                PresentWithD2D(frame);
                _videoRect = _d2dPresenter.VideoRect;
            }
            else
            {
                return;
            }
        }
        catch (COMException e)
        {
            Error?.Invoke($"present failed: 0x{e.HResult:X8}");
            return;
        }

        var nowMicros = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
        var latency = _currentCaptureWallMicros == 0 ? 0 : ((double)nowMicros - _currentCaptureWallMicros) / 1000.0;
        var clientMs = Stopwatch.GetElapsedTime(_currentReceivedTicks).TotalMilliseconds;
        _timedFrames++;
        _clientLatencySumMs += clientMs;
        if (clientMs > _clientLatencyMaxMs) _clientLatencyMaxMs = clientMs;
        FramePresented?.Invoke(latency);
        if (_presentedSinceConfig++ < 2)
            Info?.Invoke($"presented {frame.Width}×{frame.Height} ({(frame.IsGpu ? "GPU texture" : "CPU planes")}) into {_videoRect}, " +
                         $"receive→present {clientMs:0.0} ms, capture→present {latency:0.0} ms");
    }

    private void PresentWithD2D(DecodedFrame frame)
    {
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
            _d2dPresenter!.Present(dst, frame.Width, frame.Height, stride);
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
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (!_thread.Join(TimeSpan.FromSeconds(2))) { /* background thread; let it die with the process */ }
        _queue.Dispose();
    }
}
