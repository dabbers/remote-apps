// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using RemoteApp.Protocol;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace RemoteApp.Client.Video;

/// <summary>
/// A decoded NV12 picture, valid only for the duration of the callback. Either
/// a GPU texture (<see cref="IsGpu"/>: <see cref="Texture"/> slice
/// <see cref="Slice"/>, with <see cref="Sample"/> the decoder sample that owns
/// it) or system-memory planes.
/// </summary>
internal unsafe struct DecodedFrame
{
    public bool IsGpu;
    public ID3D11Texture2D? Texture;
    public uint Slice;
    public IMFSample? Sample;
    public byte* Y;
    public int YStride;
    public byte* UV;
    public int UVStride;
    /// <summary>The visible picture inside the coded frame.</summary>
    public Rectangle Visible;
    public int CodedWidth;
    public int CodedHeight;
    public int Width => Visible.Width;
    public int Height => Visible.Height;
}

internal unsafe delegate void DecodedFrameHandler(DecodedFrame frame);

/// <summary>
/// Media Foundation H.264 decoder (the in-box <c>CMSH264DecoderMFT</c>) in
/// low-latency mode. Wire frames are AVCC; they are converted to Annex-B with
/// the SPS/PPS from <see cref="VideoConfig"/> prepended to keyframes (PROTOCOL.md §7).
/// With a <see cref="GraphicsDevice"/> the decoder runs on the GPU (DXVA) and
/// outputs D3D11 NV12 textures; otherwise NV12 in system memory.
/// </summary>
internal sealed unsafe class H264Decoder : IDisposable
{
    /// <summary>The in-box Microsoft H.264 decoder MFT (not in the Win32 metadata, so spelled out here).</summary>
    private static readonly Guid ClsidH264DecoderMft = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");

    private readonly IMFTransform _transform;
    private bool _streaming;
    private bool _waitingForKeyframe = true;
    private IReadOnlyList<byte[]> _parameterSets = [];
    private int _configuredWidth;
    private int _configuredHeight;

    // Output format as negotiated with the MFT.
    private uint _codedWidth;
    private uint _codedHeight;
    private Rectangle _visible;
    private MFT_OUTPUT_STREAM_INFO _outputInfo;
    private bool _mftProvidesSamples;
    private bool _disposed;

    /// <summary>True when the decoder outputs D3D11 textures.</summary>
    public bool UsesGpuOutput { get; }

    public H264Decoder(GraphicsDevice? gpu = null)
    {
        Mf.EnsureStarted();
        PInvoke.CoCreateInstance(ClsidH264DecoderMft, null, CLSCTX.CLSCTX_INPROC_SERVER, out IMFTransform transform)
            .ThrowOnFailure();
        _transform = transform;

        // Low-latency mode: output each frame as soon as it is decodable
        // instead of buffering for reordering (the stream has no B-frames).
        try
        {
            _transform.GetAttributes(out var attributes);
            attributes.SetU32(PInvoke.MF_LOW_LATENCY, 1);
            if (gpu != null && attributes.TryGetU32(PInvoke.MF_SA_D3D11_AWARE, out var aware) && aware == 1)
            {
                var manager = Marshal.GetIUnknownForObject(gpu.Manager);
                try
                {
                    _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_SET_D3D_MANAGER, (nuint)manager);
                    UsesGpuOutput = true;
                }
                catch (COMException) { UsesGpuOutput = false; }
                finally { Marshal.Release(manager); }
            }
        }
        catch (COMException) { /* optional */ }
        try
        {
            if (_transform is ICodecAPI codec)
            {
                var key = typeof(CODECAPI_AVLowLatencyMode).GUID;
                codec.SetValue(&key, true);
            }
        }
        catch (COMException) { /* optional */ }
    }

    public bool IsConfigured => _streaming;

    /// <summary>Negotiated output details, for diagnostics.</summary>
    public string Description =>
        $"{(UsesGpuOutput ? "GPU (DXVA)" : "CPU")} output, coded {_codedWidth}×{_codedHeight}, visible {_visible}, " +
        $"stream flags 0x{_outputInfo.dwFlags:X}, cbSize {_outputInfo.cbSize}, mft provides samples {_mftProvidesSamples}";

    /// <summary>(Re)configures for new parameter sets / size. Decoding resumes at the next keyframe.</summary>
    public void Configure(VideoConfig config)
    {
        if (config.Codec != VideoCodec.H264)
            throw new NotSupportedException($"codec {config.Codec} is not supported by this client");
        if (config.NalLengthSize != 4)
            throw new NotSupportedException($"NAL length size {config.NalLengthSize} is not supported");

        if (_streaming)
        {
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, 0);
            _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
            _streaming = false;
        }

        _parameterSets = config.ParameterSets;
        _configuredWidth = config.PixelWidth;
        _configuredHeight = config.PixelHeight;

        PInvoke.MFCreateMediaType(out var input).ThrowOnFailure();
        input.SetGuid(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        input.SetGuid(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
        input.SetU64(PInvoke.MF_MT_FRAME_SIZE, Mf.Pack((uint)config.PixelWidth, (uint)config.PixelHeight));
        input.SetU32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        input.SetU64(PInvoke.MF_MT_FRAME_RATE, Mf.Pack(60, 1));
        input.SetU64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
        if (_parameterSets.Count > 0)
            input.SetBlob(PInvoke.MF_MT_MPEG_SEQUENCE_HEADER, H264Nal.AnnexBParameterSets(_parameterSets));
        _transform.SetInputType(0, input, 0);

        NegotiateOutput();

        _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        _streaming = true;
        _waitingForKeyframe = true;
    }

    private void NegotiateOutput()
    {
        for (uint i = 0; ; i++)
        {
            var hr = _transform.GetOutputAvailableType(0, i, out var type);
            if (hr.Failed) throw new InvalidOperationException("the H.264 decoder offers no NV12 output type");
            if (type.GetGuid(PInvoke.MF_MT_SUBTYPE) != PInvoke.MFVideoFormat_NV12) continue;
            _transform.SetOutputType(0, type, 0);
            ReadOutputFormat(type);
            break;
        }
        MFT_OUTPUT_STREAM_INFO info;
        _transform.GetOutputStreamInfo(0, &info);
        _outputInfo = info;
        const uint providesSamples = (uint)(_MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES
                                            | _MFT_OUTPUT_STREAM_INFO_FLAGS.MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES);
        _mftProvidesSamples = (info.dwFlags & providesSamples) != 0;
    }

    private void ReadOutputFormat(IMFMediaType type)
    {
        if (!type.TryGetU64(PInvoke.MF_MT_FRAME_SIZE, out var size))
            throw new InvalidOperationException("decoder output type has no frame size");
        (_codedWidth, _codedHeight) = Mf.Unpack(size);

        // The coded size may be padded (e.g. 1088 for 1080); the aperture gives the visible picture.
        var visible = new Rectangle(0, 0,
            (int)Math.Min(_codedWidth, (uint)Math.Max(_configuredWidth, 2)),
            (int)Math.Min(_codedHeight, (uint)Math.Max(_configuredHeight, 2)));
        if (type.TryGetBlob(PInvoke.MF_MT_MINIMUM_DISPLAY_APERTURE, out var blob) && blob.Length >= 16)
        {
            // MFVideoArea: MFOffset OffsetX {ushort fract; short value}, MFOffset OffsetY, SIZE Area.
            var x = BitConverter.ToInt16(blob, 2);
            var y = BitConverter.ToInt16(blob, 6);
            var w = BitConverter.ToInt32(blob, 8);
            var h = BitConverter.ToInt32(blob, 12);
            if (w > 0 && h > 0 && x >= 0 && y >= 0 && x + w <= _codedWidth && y + h <= _codedHeight)
                visible = new Rectangle(x, y, w, h);
        }
        _visible = visible;
    }

    /// <summary>
    /// Decodes one wire frame; <paramref name="consume"/> is called for each
    /// picture produced (normally one). Returns false if the frame was skipped
    /// while waiting for a keyframe.
    /// </summary>
    public bool Decode(VideoFrame frame, DecodedFrameHandler consume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_streaming) return false;
        if (_waitingForKeyframe)
        {
            if (!frame.IsKeyframe) return false;
            _waitingForKeyframe = false;
        }

        var annexB = H264Nal.AvccToAnnexB(frame.Data, 4, frame.IsKeyframe ? _parameterSets : null);
        var sample = CreateInputSample(annexB, frame.PtsMicros);
        try
        {
            var hr = _transform.ProcessInput(0, sample, 0);
            if (hr == HRESULT.MF_E_NOTACCEPTING)
            {
                DrainOutputs(consume);
                hr = _transform.ProcessInput(0, sample, 0);
            }
            if (hr.Failed)
                throw new COMException($"ProcessInput failed (0x{hr.Value:X8}, {annexB.Length} bytes, keyframe {frame.IsKeyframe})", hr.Value);
        }
        finally
        {
            Mf.Release(sample);
        }
        return DrainOutputs(consume) > 0;
    }

    /// <summary>Drops any queued pictures and waits for the next keyframe (after a stall or reconnect).</summary>
    public void Reset()
    {
        if (_streaming)
        {
            try { _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, 0); }
            catch (COMException) { /* best effort */ }
        }
        _waitingForKeyframe = true;
    }

    private static IMFSample CreateInputSample(byte[] annexB, ulong ptsMicros)
    {
        PInvoke.MFCreateMemoryBuffer((uint)annexB.Length, out var buffer).ThrowOnFailure();
        byte* data;
        buffer.Lock(&data, null, null);
        try
        {
            annexB.AsSpan().CopyTo(new Span<byte>(data, annexB.Length));
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.SetCurrentLength((uint)annexB.Length);

        PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
        sample.AddBuffer(buffer);
        sample.SetSampleTime((long)(ptsMicros * 10));          // 100 ns units
        sample.SetSampleDuration(10_000_000 / 60);
        Mf.Release(buffer);
        return sample;
    }

    /// <summary>A fresh output sample (the decoder rejects a reused one on the next call).</summary>
    private IMFSample NewOutputSample()
    {
        var size = _outputInfo.cbSize > 0 ? _outputInfo.cbSize : _codedWidth * _codedHeight * 3 / 2;
        // cbAlignment is a byte count; MFCreateAlignedMemoryBuffer wants (alignment - 1).
        var alignment = _outputInfo.cbAlignment == 0 ? 16u : _outputInfo.cbAlignment;
        if ((alignment & (alignment - 1)) != 0) alignment++;
        PInvoke.MFCreateSample(out var sample).ThrowOnFailure();
        PInvoke.MFCreateAlignedMemoryBuffer(size, alignment - 1, out var buffer).ThrowOnFailure();
        sample.AddBuffer(buffer);
        Mf.Release(buffer);
        return sample;
    }

    private int DrainOutputs(DecodedFrameHandler consume)
    {
        var produced = 0;
        while (true)
        {
            var buffers = new MFT_OUTPUT_DATA_BUFFER[1];
            buffers[0].dwStreamID = 0;
            IMFSample? ours = null;
            if (!_mftProvidesSamples) buffers[0].pSample = ours = NewOutputSample();

            var hr = _transform.ProcessOutput(0, 1, buffers, out var status);
            if (hr == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT) { Mf.Release(ours); break; }
            if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // The real SPS differed from the configured size: renegotiate and go on.
                Mf.Release(ours);
                NegotiateOutput();
                continue;
            }
            if (hr.Failed)
            {
                Mf.Release(ours);
                throw new COMException($"ProcessOutput failed (0x{hr.Value:X8}, status 0x{status:X}, buffer status 0x{buffers[0].dwStatus:X}, mft provides samples {_mftProvidesSamples}, cbSize {_outputInfo.cbSize})", hr.Value);
            }

            if (buffers[0].pEvents != null) Mf.Release(buffers[0].pEvents);
            var output = buffers[0].pSample;
            if (output == null) { Mf.Release(ours); continue; }
            try
            {
                Deliver(output, consume);
                produced++;
            }
            finally
            {
                Mf.Release(output);
            }
        }
        return produced;
    }

    private void Deliver(IMFSample sample, DecodedFrameHandler consume)
    {
        sample.GetBufferByIndex(0, out var buffer);
        try
        {
            if (UsesGpuOutput && buffer is IMFDXGIBuffer dxgi)
            {
                DeliverGpu(sample, dxgi, consume);
                return;
            }
            DeliverCpu(buffer, consume);
        }
        finally
        {
            Mf.Release(buffer);
        }
    }

    private void DeliverGpu(IMFSample sample, IMFDXGIBuffer dxgi, DecodedFrameHandler consume)
    {
        var iid = typeof(ID3D11Texture2D).GUID;
        void* raw;
        dxgi.GetResource(&iid, &raw);
        var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((IntPtr)raw);
        Marshal.Release((IntPtr)raw);
        try
        {
            dxgi.GetSubresourceIndex(out var slice);
            consume(new DecodedFrame
            {
                IsGpu = true,
                Texture = texture,
                Slice = slice,
                Sample = sample,
                Visible = _visible,
                CodedWidth = (int)_codedWidth,
                CodedHeight = (int)_codedHeight,
            });
        }
        finally
        {
            Mf.Release(texture);
        }
    }

    private void DeliverCpu(IMFMediaBuffer buffer, DecodedFrameHandler consume)
    {
        byte* scanline0;
        int pitch;
        var buffer2D = buffer as IMF2DBuffer;
        if (buffer2D != null)
        {
            buffer2D.Lock2D(&scanline0, out pitch);
        }
        else
        {
            buffer.Lock(&scanline0, null, null);
            pitch = (int)_codedWidth;
        }
        try
        {
            consume(new DecodedFrame
            {
                IsGpu = false,
                Y = scanline0 + _visible.Y * pitch + _visible.X,
                YStride = pitch,
                UV = scanline0 + pitch * (int)_codedHeight + (_visible.Y / 2) * pitch + (_visible.X & ~1),
                UVStride = pitch,
                Visible = new Rectangle(0, 0, _visible.Width, _visible.Height),
                CodedWidth = _visible.Width,
                CodedHeight = _visible.Height,
            });
        }
        finally
        {
            if (buffer2D != null) buffer2D.Unlock2D(); else buffer.Unlock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_streaming)
            {
                _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, 0);
                _transform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
            }
        }
        catch (COMException) { /* shutting down */ }
        _streaming = false;
        Mf.Release(_transform);
    }
}
