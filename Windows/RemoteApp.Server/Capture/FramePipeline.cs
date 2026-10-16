// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteApp.Protocol;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;

namespace RemoteApp.Server.Capture;

/// <summary>
/// Capture → color conversion → encode for one window size. Frames arrive as
/// BGRA GPU textures from <see cref="DesktopCapture"/>; the preferred path
/// converts them to NV12 with a D3D11 video processor and hands the NV12
/// texture to a D3D-aware encoder without ever touching system memory. If the
/// encoder is not D3D-aware the NV12 texture is read back (1.5 bytes/px); if
/// the adapter has no video processing at all, the BGRA frame is read back and
/// converted on the CPU.
/// </summary>
internal sealed unsafe class FramePipeline : IDisposable
{
    private const int RingSize = 6;

    public event Action<VideoConfig>? ConfigChanged;
    public event Action<VideoFrame>? FrameEncoded;
    public event Action<string>? Error;

    // Timing: capture wall-clock → encoded frame handed to the connection.
    private long _encodedFrames;
    private double _encodeLatencySumMs;
    private double _encodeLatencyMaxMs;

    /// <summary>Capture → encoded output, averaged over the pipeline's lifetime.</summary>
    private int _capturedFrames;

    public string TimingSummary => _encodedFrames == 0 ? $"{_capturedFrames} captured, no frames encoded"
        : $"{_capturedFrames} captured, {_encodedFrames} encoded, capture→encoded avg {_encodeLatencySumMs / _encodedFrames:0.0} ms, " +
          $"max {_encodeLatencyMaxMs:0.0} ms, {_encoder.BusyDrops} dropped by a busy encoder";

    /// <summary>Encoded size (even).</summary>
    public int Width { get; }
    public int Height { get; }

    /// <summary>Asked before encoding a frame; true drops it (back-pressure from the connection).</summary>
    public Func<bool>? ShouldDrop { get; set; }

    public int DroppedFrames => _dropped;
    public string Description { get; }

    private readonly GraphicsDevice _gpu;
    private readonly DesktopCapture _capture;
    private readonly H264Encoder _encoder;
    private readonly VideoProcessorConverter? _converter;
    private readonly ID3D11Texture2D[] _nv12 = new ID3D11Texture2D[RingSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews = new ID3D11VideoProcessorOutputView[RingSize];
    private readonly ID3D11Texture2D? _nv12Staging;
    private readonly ID3D11Texture2D? _bgraStaging;
    private ID3D11VideoProcessorInputView? _inputView;
    private ID3D11Texture2D? _inputViewSource;
    private int _ring;
    private int _dropped;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Capture and encode overlap: the capture thread converts into a ring slot
    // and hands it over; the encode thread always takes the newest slot, so a
    // slow encoder costs frames, not latency, and never stalls the capture.
    private readonly record struct Pending(int Slot, long Pts, ulong Wall);
    private readonly BlockingCollection<Pending> _pending = new(new ConcurrentQueue<Pending>(), 2);
    private readonly Thread? _encodeThread;

    public FramePipeline(GraphicsDevice gpu, HMONITOR monitor, Rectangle bounds, int frameRate, int bitrate)
    {
        _gpu = gpu;
        Width = Math.Max(2, bounds.Width & ~1);
        Height = Math.Max(2, bounds.Height & ~1);

        _encoder = new H264Encoder(Width, Height, frameRate, bitrate, gpu.HasVideo ? gpu : null);
        _encoder.ConfigChanged += config => ConfigChanged?.Invoke(config);
        _encoder.FrameEncoded += frame =>
        {
            if (frame.CaptureWallMicros != 0)
            {
                var ms = ((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000 - frame.CaptureWallMicros) / 1000.0;
                _encodedFrames++;
                _encodeLatencySumMs += ms;
                if (ms > _encodeLatencyMaxMs) _encodeLatencyMaxMs = ms;
            }
            FrameEncoded?.Invoke(frame);
        };
        _encoder.Error += line => Error?.Invoke(line);

        string path;
        if (gpu.HasVideo)
        {
            try
            {
                _converter = new VideoProcessorConverter(gpu, Width, Height, Width, Height, rgbInput: true);
                for (var i = 0; i < RingSize; i++)
                {
                    _nv12[i] = gpu.CreateTexture(Width, Height, DXGI_FORMAT.DXGI_FORMAT_NV12, D3D11_USAGE.D3D11_USAGE_DEFAULT,
                        D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET, 0);
                    _outputViews[i] = _converter.CreateOutputView(_nv12[i]);
                }
                if (!_encoder.UsesGpuInput)
                    _nv12Staging = gpu.CreateTexture(Width, Height, DXGI_FORMAT.DXGI_FORMAT_NV12, D3D11_USAGE.D3D11_USAGE_STAGING,
                        0, D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ);
                path = _encoder.UsesGpuInput ? "GPU convert, texture to encoder" : "GPU convert, NV12 readback to encoder";
            }
            catch (COMException e)
            {
                Error?.Invoke($"GPU color conversion unavailable (0x{e.HResult:X8}); converting on the CPU");
                ReleaseGpuResources();
                _converter = null;
                path = "CPU convert";
            }
        }
        else
        {
            path = "CPU convert";
        }
        if (_converter == null)
            _bgraStaging = gpu.CreateTexture(Width, Height, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_USAGE.D3D11_USAGE_STAGING,
                0, D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ);
        Description = $"{_encoder.EncoderName} ({(_encoder.IsHardware ? "hardware" : "software")}), {path}, {Width}×{Height}";

        if (_converter != null && _encoder.UsesGpuInput)
            _encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = "remoteapp-encode", Priority = ThreadPriority.AboveNormal };

        _capture = new DesktopCapture(gpu, monitor, bounds);
        _capture.FrameCaptured += OnFrame;
        _capture.Error += line => Error?.Invoke(line);
    }

    public void Start()
    {
        _encodeThread?.Start();
        _capture.Start();
    }

    public void SetCrop(Rectangle bounds) => _capture.SetCrop(bounds);

    private void OnFrame(ID3D11Texture2D frame, int width, int height, ulong captureWallMicros)
    {
        if (width != Width || height != Height) return;   // a stale size during a restart
        _capturedFrames++;
        if (ShouldDrop?.Invoke() == true)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        var pts = _clock.ElapsedTicks * 1_000_000L / Stopwatch.Frequency;
        try
        {
            if (_converter != null) EncodeViaGpu(frame, pts, captureWallMicros);
            else EncodeViaCpu(frame, pts, captureWallMicros);
        }
        catch (COMException e)
        {
            Error?.Invoke($"conversion failed: 0x{e.HResult:X8}");
        }
    }

    /// <summary>Capture thread: convert into the next ring slot and hand it to the encode thread.</summary>
    private void EncodeViaGpu(ID3D11Texture2D frame, long pts, ulong wall)
    {
        // The capture reuses its texture, so the input view is reused too.
        if (_inputView == null || !ReferenceEquals(_inputViewSource, frame))
        {
            if (_inputView != null) Mf.Release(_inputView);
            _inputView = _converter!.CreateInputView(frame, 0);
            _inputViewSource = frame;
        }
        var slot = _ring;
        _ring = (_ring + 1) % RingSize;
        _converter!.Blt(_inputView, _outputViews[slot]);
        var pending = new Pending(slot, pts, wall);
        if (_encoder.UsesGpuInput)
        {
            // Texture input: let the encoder run on its own thread so a slow
            // submission costs frames rather than stalling the capture.
            if (!_pending.TryAdd(pending)) Interlocked.Increment(ref _dropped);
            return;
        }
        // Readback input: encode inline. One stage less means one frame less
        // of latency, and the capture loop paces itself on the encoder anyway.
        EncodeReadback(pending);
    }

    /// <summary>Encode thread: feeds the newest converted frame to the encoder.</summary>
    private void EncodeLoop()
    {
        try
        {
            foreach (var item in _pending.GetConsumingEnumerable())
            {
                var pending = item;
                // Skip to the newest frame if more than one is waiting.
                while (_pending.TryTake(out var newer))
                {
                    Interlocked.Increment(ref _dropped);
                    pending = newer;
                }
                try
                {
                    if (_encoder.UsesGpuInput) _encoder.EncodeTexture(_nv12[pending.Slot], pending.Pts, pending.Wall);
                    else EncodeReadback(pending);
                }
                catch (COMException e)
                {
                    Error?.Invoke($"encode failed: 0x{e.HResult:X8}");
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Software encoder: read the NV12 back (a third of the BGRA bytes) and feed system memory.</summary>
    private void EncodeReadback(Pending pending)
    {
        _gpu.Context.CopyResource(_nv12Staging!, _nv12[pending.Slot]);
        D3D11_MAPPED_SUBRESOURCE mapped;
        _gpu.Context.Map(_nv12Staging!, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
        try
        {
            var y = (byte*)mapped.pData;
            var uv = y + mapped.RowPitch * Height;
            _encoder.EncodeNv12(y, (int)mapped.RowPitch, uv, (int)mapped.RowPitch, pending.Pts, pending.Wall);
        }
        finally
        {
            _gpu.Context.Unmap(_nv12Staging!, 0);
        }
    }

    private void EncodeViaCpu(ID3D11Texture2D frame, long pts, ulong wall)
    {
        _gpu.Context.CopyResource(_bgraStaging!, frame);
        D3D11_MAPPED_SUBRESOURCE mapped;
        _gpu.Context.Map(_bgraStaging!, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
        try
        {
            _encoder.EncodeBgra((byte*)mapped.pData, (int)mapped.RowPitch, pts, wall);
        }
        finally
        {
            _gpu.Context.Unmap(_bgraStaging!, 0);
        }
    }

    private void ReleaseGpuResources()
    {
        if (_inputView != null) Mf.Release(_inputView);
        _inputView = null;
        _inputViewSource = null;
        for (var i = 0; i < RingSize; i++)
        {
            if (_outputViews[i] != null) Mf.Release(_outputViews[i]);
            if (_nv12[i] != null) Mf.Release(_nv12[i]);
            _outputViews[i] = null!;
            _nv12[i] = null!;
        }
        _converter?.Dispose();
    }

    public void Dispose()
    {
        _capture.Dispose();
        _pending.CompleteAdding();
        if (_encodeThread is { IsAlive: true }) _encodeThread.Join(TimeSpan.FromSeconds(2));
        _encoder.Dispose();
        ReleaseGpuResources();
        if (_nv12Staging != null) Mf.Release(_nv12Staging);
        if (_bgraStaging != null) Mf.Release(_bgraStaging);
    }
}
