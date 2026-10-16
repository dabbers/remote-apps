// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using RemoteApp.Protocol;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;

namespace RemoteApp.Server.Capture;

/// <summary>
/// Media Foundation H.264 encoder tuned for low latency: CBR, no B-frames,
/// a keyframe every two seconds, low-latency mode. Prefers a hardware MFT
/// (Intel/NVIDIA/AMD, which are asynchronous and driven by their event queue)
/// and falls back to the in-box software encoder (synchronous). Input is NV12,
/// either as a D3D11 texture (D3D-aware encoders, no system-memory copy) or
/// in system memory; output Annex-B is converted to the wire's AVCC and the
/// SPS/PPS are reported through <see cref="ConfigChanged"/>.
/// </summary>
internal sealed unsafe class H264Encoder : IDisposable
{
    public event Action<VideoConfig>? ConfigChanged;
    public event Action<VideoFrame>? FrameEncoded;
    public event Action<string>? Error;

    public int Width { get; }
    public int Height { get; }
    public string EncoderName { get; private set; } = "?";
    public bool IsHardware { get; private set; }

    /// <summary>True when the encoder takes D3D11 NV12 textures directly.</summary>
    public bool UsesGpuInput { get; private set; }

    private int _busyDrops;

    /// <summary>Frames dropped because the (asynchronous) encoder did not ask for input in time.</summary>
    public int BusyDrops => _busyDrops;

    // MediaEventType values an asynchronous MFT raises (mfobjects.h).
    private const uint TransformNeedInput = 601;
    private const uint TransformHaveOutput = 602;

    private readonly int _frameRate;
    private readonly int _bitrate;
    private readonly GraphicsDevice? _gpu;
    private IMFTransform? _transform;
    private IMFActivate? _activate;
    private uint _inputId;
    private uint _outputId;
    private bool _async;
    private bool _providesOutputSamples;
    private MFT_OUTPUT_STREAM_INFO _outputInfo;
    private IMFSample? _outputSample;
    private Thread? _eventThread;
    private readonly SemaphoreSlim _needInput = new(0, int.MaxValue);
    private readonly ConcurrentDictionary<long, ulong> _wallByPts = new();
    private List<byte[]> _lastParameterSets = [];
    private byte[]? _nv12;
    private readonly object _lock = new();
    private bool _disposed;

    /// <param name="gpu">A device with video support to try D3D11 input with; null for system memory only.</param>
    public H264Encoder(int width, int height, int frameRate, int bitrate, GraphicsDevice? gpu = null)
    {
        if ((width & 1) != 0 || (height & 1) != 0) throw new ArgumentException("NV12 needs even dimensions");
        Width = width;
        Height = height;
        _frameRate = frameRate;
        _bitrate = bitrate;
        _gpu = gpu is { HasVideo: true } ? gpu : null;
        Mf.EnsureStarted();

        var errors = new List<string>();
        foreach (var hardware in new[] { true, false })
        {
            foreach (var activate in Enumerate(hardware))
            {
                var name = activate.TryGetString(PInvoke.MFT_FRIENDLY_NAME_Attribute) ?? (hardware ? "hardware encoder" : "software encoder");
                try
                {
                    var transform = Activate(activate);
                    Configure(transform);
                    _transform = transform;
                    _activate = activate;
                    EncoderName = name;
                    IsHardware = hardware;
                    return;
                }
                catch (Exception e) when (e is COMException or InvalidOperationException)
                {
                    errors.Add($"{name}: {e.Message}");
                    try { activate.ShutdownObject(); } catch (COMException) { }
                    Mf.Release(activate);
                }
            }
        }
        throw new InvalidOperationException("no usable H.264 encoder: " + string.Join("; ", errors));
    }

    // MARK: Enumeration & configuration

    private static List<IMFActivate> Enumerate(bool hardware)
    {
        var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = PInvoke.MFVideoFormat_H264 };
        var flags = (hardware ? MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE : MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT)
                    | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;
        var result = new List<IMFActivate>();
        IMFActivate_unmanaged** activates;
        uint count;
        PInvoke.MFTEnumEx(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, flags, null, output, out activates, out count).ThrowOnFailure();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var pointer = (IntPtr)activates[i];
                result.Add((IMFActivate)Marshal.GetObjectForIUnknown(pointer));
                Marshal.Release(pointer);
            }
        }
        finally
        {
            PInvoke.CoTaskMemFree(activates);
        }
        return result;
    }

    private static IMFTransform Activate(IMFActivate activate)
    {
        var iid = typeof(IMFTransform).GUID;
        activate.ActivateObject(&iid, out var ppv);
        var transform = (IMFTransform)Marshal.GetObjectForIUnknown((IntPtr)ppv);
        Marshal.Release((IntPtr)ppv);
        return transform;
    }

    private void Configure(IMFTransform transform)
    {
        // Stream IDs: most MFTs use 0; hardware ones may not.
        try
        {
            var inputs = new uint[1];
            var outputs = new uint[1];
            transform.GetStreamIDs(1, inputs, 1, outputs);
            _inputId = inputs[0];
            _outputId = outputs[0];
        }
        catch (COMException)
        {
            _inputId = _outputId = 0;
        }

        _async = false;
        UsesGpuInput = false;
        try
        {
            transform.GetAttributes(out var attributes);
            if (attributes.TryGetU32(PInvoke.MF_TRANSFORM_ASYNC, out var isAsync) && isAsync == 1)
            {
                _async = true;
                attributes.SetU32(PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1);
            }
            attributes.SetU32(PInvoke.MF_LOW_LATENCY, 1);

            // D3D-aware encoders can take NV12 textures directly. Opt-in
            // (REMOTEAPP_ENCODER_GPU_INPUT=1): on the NVIDIA MFT this measured
            // 45–90 ms per frame, while system-memory NV12 read back from the
            // GPU conversion encodes in ~12 ms, so the readback is the default.
            var gpuInputWanted = Environment.GetEnvironmentVariable("REMOTEAPP_ENCODER_GPU_INPUT") == "1";
            if (_gpu != null && gpuInputWanted && attributes.TryGetU32(PInvoke.MF_SA_D3D11_AWARE, out var aware) && aware == 1)
            {
                var manager = Marshal.GetIUnknownForObject(_gpu.Manager!);
                try
                {
                    transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)manager);
                    UsesGpuInput = true;
                }
                catch (COMException) { UsesGpuInput = false; }
                finally { Marshal.Release(manager); }
            }
        }
        catch (COMException) { /* attributes are optional */ }

        if (transform is ICodecAPI codec)
        {
            SetCodecValue(codec, typeof(CODECAPI_AVEncCommonRateControlMode).GUID, (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR);
            SetCodecValue(codec, typeof(CODECAPI_AVEncCommonMeanBitRate).GUID, (uint)_bitrate);
            SetCodecValue(codec, typeof(CODECAPI_AVLowLatencyMode).GUID, true);
            SetCodecValue(codec, typeof(CODECAPI_AVEncMPVGOPSize).GUID, (uint)(_frameRate * 2));
            SetCodecValue(codec, typeof(CODECAPI_AVEncMPVDefaultBPictureCount).GUID, 0u);
            SetCodecValue(codec, typeof(CODECAPI_AVEncCommonQualityVsSpeed).GUID, 33u);
        }

        // Encoders want the output type first.
        PInvoke.MFCreateMediaType(out var output).ThrowOnFailure();
        output.SetGuid(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        output.SetGuid(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
        output.SetU32(PInvoke.MF_MT_AVG_BITRATE, (uint)_bitrate);
        output.SetU64(PInvoke.MF_MT_FRAME_SIZE, Mf.Pack((uint)Width, (uint)Height));
        output.SetU64(PInvoke.MF_MT_FRAME_RATE, Mf.Pack((uint)_frameRate, 1));
        output.SetU64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
        output.SetU32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        output.SetU32(PInvoke.MF_MT_MPEG2_PROFILE, (uint)eAVEncH264VProfile.eAVEncH264VProfile_High);
        transform.SetOutputType(_outputId, output, 0);

        PInvoke.MFCreateMediaType(out var input).ThrowOnFailure();
        input.SetGuid(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        input.SetGuid(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
        input.SetU64(PInvoke.MF_MT_FRAME_SIZE, Mf.Pack((uint)Width, (uint)Height));
        input.SetU64(PInvoke.MF_MT_FRAME_RATE, Mf.Pack((uint)_frameRate, 1));
        input.SetU64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
        input.SetU32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        input.SetU32(PInvoke.MF_MT_DEFAULT_STRIDE, (uint)Width);
        transform.SetInputType(_inputId, input, 0);

        MFT_OUTPUT_STREAM_INFO info;
        transform.GetOutputStreamInfo(_outputId, &info);
        _outputInfo = info;
        _providesOutputSamples = (info.dwFlags & (uint)(_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
                                                        | _MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;

        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

        if (_async)
        {
            if (transform is not IMFMediaEventGenerator generator)
                throw new InvalidOperationException("asynchronous MFT without an event generator");
            _eventThread = new Thread(() => EventLoop(transform, generator)) { IsBackground = true, Name = "remoteapp-encoder-events" };
            _eventThread.Start();
        }
    }

    private static void SetCodecValue(ICodecAPI codec, Guid api, object value)
    {
        try { codec.SetValue(&api, value); }
        catch (COMException) { /* not supported by this encoder */ }
    }

    // MARK: Encoding

    /// <summary>Encodes a BGRA frame in system memory (converted to NV12 on the CPU here).</summary>
    public void EncodeBgra(byte* bgra, int stride, long ptsMicros, ulong captureWallMicros)
    {
        if (_disposed || _transform == null) return;
        var ySize = Width * Height;
        _nv12 ??= new byte[ySize * 3 / 2];
        fixed (byte* nv12 = _nv12)
        {
            Bgra2Nv12.Convert(bgra, stride, Width, Height, nv12, Width, nv12 + ySize, Width);
            EncodeNv12(nv12, Width, nv12 + ySize, Width, ptsMicros, captureWallMicros);
        }
    }

    /// <summary>Encodes an NV12 frame in system memory (planes may be padded).</summary>
    public void EncodeNv12(byte* y, int yStride, byte* uv, int uvStride, long ptsMicros, ulong captureWallMicros)
    {
        if (_disposed || _transform == null) return;
        var length = (uint)(Width * Height * 3 / 2);
        PInvoke.MFCreateAlignedMemoryBuffer(length, 15, out var buffer).ThrowOnFailure();
        byte* data;
        buffer.Lock(&data, null, null);
        try
        {
            for (var row = 0; row < Height; row++)
                new ReadOnlySpan<byte>(y + row * yStride, Width).CopyTo(new Span<byte>(data + row * Width, Width));
            var dstUv = data + Width * Height;
            for (var row = 0; row < Height / 2; row++)
                new ReadOnlySpan<byte>(uv + row * uvStride, Width).CopyTo(new Span<byte>(dstUv + row * Width, Width));
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.SetCurrentLength(length);
        EncodeBuffer(buffer, ptsMicros, captureWallMicros);
    }

    /// <summary>Encodes an NV12 D3D11 texture (requires <see cref="UsesGpuInput"/>).</summary>
    public void EncodeTexture(ID3D11Texture2D texture, long ptsMicros, ulong captureWallMicros)
    {
        if (_disposed || _transform == null) return;
        PInvoke.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, texture, 0, false, out var buffer).ThrowOnFailure();
        try { buffer.SetCurrentLength((uint)(Width * Height * 3 / 2)); }
        catch (COMException) { /* some buffers compute their own length */ }
        EncodeBuffer(buffer, ptsMicros, captureWallMicros);
    }

    private void EncodeBuffer(IMFMediaBuffer buffer, long ptsMicros, ulong captureWallMicros)
    {
        PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
        sample.AddBuffer(buffer);
        sample.SetSampleTime(ptsMicros * 10);
        sample.SetSampleDuration(10_000_000L / _frameRate);
        Mf.Release(buffer);
        _wallByPts[ptsMicros * 10] = captureWallMicros;
        try
        {
            if (_async)
            {
                // Hardware MFTs accept input only after asking for it. Wait outside
                // the lock: the event thread needs it to drain output, and the
                // encoder only asks for more input once output has been taken.
                if (!_needInput.Wait(100))
                {
                    _wallByPts.TryRemove(ptsMicros * 10, out _);
                    Interlocked.Increment(ref _busyDrops);
                    return;
                }
                lock (_lock)
                {
                    if (_disposed || _transform == null) return;
                    _transform.ProcessInput(_inputId, sample, 0).ThrowOnFailure();
                }
            }
            else
            {
                lock (_lock)
                {
                    if (_disposed || _transform == null) return;
                    var hr = _transform.ProcessInput(_inputId, sample, 0);
                    if (hr == HRESULT.MF_E_NOTACCEPTING)
                    {
                        while (ProcessOutputOnce()) { }
                        hr = _transform.ProcessInput(_inputId, sample, 0);
                    }
                    hr.ThrowOnFailure();
                    while (ProcessOutputOnce()) { }
                }
            }
        }
        catch (COMException e)
        {
            Error?.Invoke($"encode failed: 0x{e.HResult:X8}");
        }
        finally
        {
            Mf.Release(sample);
        }
    }

    private void EventLoop(IMFTransform transform, IMFMediaEventGenerator generator)
    {
        while (!_disposed)
        {
            var hr = generator.GetEvent(0, out var mediaEvent);
            if (hr.Failed)
            {
                if (hr == HRESULT.MF_E_SHUTDOWN || _disposed) return;
                Thread.Sleep(5);
                continue;
            }
            try
            {
                mediaEvent.GetType(out var type);
                switch (type)
                {
                    case TransformNeedInput:
                        _needInput.Release();
                        break;
                    case TransformHaveOutput:
                        lock (_lock)
                        {
                            if (!_disposed) ProcessOutputOnce();
                        }
                        break;
                }
            }
            catch (COMException e)
            {
                Error?.Invoke($"encoder event error: 0x{e.HResult:X8}");
            }
            finally
            {
                Mf.Release(mediaEvent);
            }
        }
    }

    private IMFSample OutputSample()
    {
        if (_outputSample != null) return _outputSample;
        var size = _outputInfo.cbSize > 0 ? _outputInfo.cbSize : (uint)(Width * Height * 4);
        var alignment = _outputInfo.cbAlignment == 0 ? 16u : _outputInfo.cbAlignment;
        if ((alignment & (alignment - 1)) != 0) alignment++;
        PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
        PInvoke.MFCreateAlignedMemoryBuffer(size, alignment - 1, out var buffer).ThrowOnFailure();
        sample.AddBuffer(buffer);
        Mf.Release(buffer);
        _outputSample = sample;
        return sample;
    }

    /// <summary>Pulls one encoded sample if available. Caller holds the lock.</summary>
    private bool ProcessOutputOnce()
    {
        var transform = _transform!;
        var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
        buffers[0].dwStreamID = _outputId;
        if (!_providesOutputSamples) buffers[0].pSample = OutputSample();

        var hr = transform.ProcessOutput(0, 1, buffers, out _);
        if (hr == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT) return false;
        if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
        {
            // The encoder refreshed its output type (e.g. with a sequence header); keep going.
            try
            {
                transform.GetOutputAvailableType(_outputId, 0, out var type);
                transform.SetOutputType(_outputId, type, 0);
            }
            catch (COMException) { }
            return true;
        }
        hr.ThrowOnFailure();

        if (buffers[0].pEvents != null) Mf.Release(buffers[0].pEvents);
        var sample = buffers[0].pSample;
        if (sample == null) return true;
        try
        {
            Emit(sample);
        }
        finally
        {
            if (_providesOutputSamples) Mf.Release(sample);
        }
        return true;
    }

    private void Emit(IMFSample sample)
    {
        sample.GetSampleTime(out var sampleTime);
        var isCleanPoint = sample.TryGetU32(PInvoke.MFSampleExtension_CleanPoint, out var clean) && clean != 0;
        sample.ConvertToContiguousBuffer(out var buffer);
        byte[] annexB;
        try
        {
            byte* data;
            uint length;
            buffer.Lock(&data, null, &length);
            try
            {
                annexB = new byte[length];
                new ReadOnlySpan<byte>(data, (int)length).CopyTo(annexB);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            Mf.Release(buffer);
        }

        var unit = H264Nal.AnnexBToAvcc(annexB);
        var keyframe = unit.IsKeyframe || isCleanPoint;
        var parameterSets = unit.ParameterSets;
        if (parameterSets.Count == 0 && keyframe && _lastParameterSets.Count == 0)
        {
            // Some hardware encoders only put SPS/PPS in the media type.
            try
            {
                _transform!.GetOutputCurrentType(_outputId, out var type);
                if (type.TryGetBlob(PInvoke.MF_MT_MPEG_SEQUENCE_HEADER, out var header) && header.Length > 0)
                    parameterSets = H264Nal.AnnexBToAvcc(header).ParameterSets;
            }
            catch (COMException) { }
        }
        if (parameterSets.Count >= 2 && !H264Nal.SameParameterSets(parameterSets, _lastParameterSets))
        {
            _lastParameterSets = parameterSets;
            ConfigChanged?.Invoke(new VideoConfig(VideoCodec.H264, parameterSets, Width, Height, 4));
        }
        if (_lastParameterSets.Count == 0) return;   // no decoder config yet; a keyframe will bring one
        if (unit.Data.Length == 0) return;

        _wallByPts.TryRemove(sampleTime, out var wall);
        if (_wallByPts.Count > 120) _wallByPts.Clear();
        FrameEncoded?.Invoke(new VideoFrame((ulong)Math.Max(0, sampleTime / 10), wall, keyframe, unit.Data));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _transform?.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
                _transform?.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
            }
            catch (COMException) { }
            if (_transform is IMFShutdown shutdown)
            {
                try { shutdown.Shutdown(); } catch (COMException) { }
            }
            try { _activate?.ShutdownObject(); } catch (COMException) { }
            if (_outputSample != null) Mf.Release(_outputSample);
            _outputSample = null;
        }
        _needInput.Release();
        if (_eventThread != null && _eventThread.IsAlive) _eventThread.Join(TimeSpan.FromSeconds(1));
        Mf.Release(_transform);
        Mf.Release(_activate);
        _transform = null;
        _activate = null;
    }
}
