// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace RemoteApp.Server.Capture;

/// <summary>
/// A Direct3D 11 video processor doing color conversion (and, if asked,
/// scaling) on the GPU: here BGRA → NV12 (BT.709, limited range) for the
/// encoder. Input/output views are created per texture by the caller.
/// </summary>
internal sealed unsafe class VideoProcessorConverter : IDisposable
{
    private readonly GraphicsDevice _gpu;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessor _processor;

    public int InputWidth { get; }
    public int InputHeight { get; }
    public int OutputWidth { get; }
    public int OutputHeight { get; }

    /// <param name="rgbInput">true: RGB (full range) in, YCbCr (limited) out; false: the reverse.</param>
    public VideoProcessorConverter(GraphicsDevice gpu, int inputWidth, int inputHeight, int outputWidth, int outputHeight, bool rgbInput)
    {
        if (!gpu.HasVideo) throw new InvalidOperationException("no D3D11 video device");
        _gpu = gpu;
        InputWidth = inputWidth;
        InputHeight = inputHeight;
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;

        var desc = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            InputWidth = (uint)inputWidth,
            InputHeight = (uint)inputHeight,
            OutputFrameRate = new DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            OutputWidth = (uint)outputWidth,
            OutputHeight = (uint)outputHeight,
            Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_PLAYBACK_NORMAL,
        };
        gpu.VideoDevice!.CreateVideoProcessorEnumerator(&desc, out _enumerator);
        gpu.VideoDevice.CreateVideoProcessor(_enumerator, 0, out _processor);

        var context = gpu.VideoContext!;
        context.VideoProcessorSetStreamFrameFormat(_processor, 0, D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);

        // Color spaces: RGB is full range; YCbCr is BT.709 limited (16–235).
        var input = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE();
        input.Usage = false;
        input.RGB_Range = false;
        input.YCbCr_Matrix = true;
        input.YCbCr_xvYCC = false;
        input.Nominal_Range = (byte)(rgbInput ? D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE.D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255
                                              : D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE.D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235);
        var output = new D3D11_VIDEO_PROCESSOR_COLOR_SPACE();
        output.Usage = false;
        output.RGB_Range = false;
        output.YCbCr_Matrix = true;
        output.YCbCr_xvYCC = false;
        output.Nominal_Range = (byte)(rgbInput ? D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE.D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235
                                               : D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE.D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255);
        context.VideoProcessorSetStreamColorSpace(_processor, 0, &input);
        context.VideoProcessorSetOutputColorSpace(_processor, &output);

        var black = new D3D11_VIDEO_COLOR();
        black.RGBA = new D3D11_VIDEO_COLOR_RGBA { R = 0, G = 0, B = 0, A = 1 };
        context.VideoProcessorSetOutputBackgroundColor(_processor, false, &black);
        SetRects(new Rectangle(0, 0, inputWidth, inputHeight), new Rectangle(0, 0, outputWidth, outputHeight),
            new Rectangle(0, 0, outputWidth, outputHeight));
    }

    /// <summary>Which part of the input to read, where to draw it, and the output area to touch (the rest is the background color).</summary>
    public void SetRects(Rectangle source, Rectangle destination, Rectangle target)
    {
        var context = _gpu.VideoContext!;
        var s = new RECT(source);
        var d = new RECT(destination);
        var t = new RECT(target);
        context.VideoProcessorSetStreamSourceRect(_processor, 0, true, &s);
        context.VideoProcessorSetStreamDestRect(_processor, 0, true, &d);
        context.VideoProcessorSetOutputTargetRect(_processor, true, &t);
    }

    public ID3D11VideoProcessorInputView CreateInputView(ID3D11Texture2D texture, uint arraySlice)
    {
        var desc = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
        {
            FourCC = 0,
            ViewDimension = D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D,
        };
        desc.Texture2D = new D3D11_TEX2D_VPIV { MipSlice = 0, ArraySlice = arraySlice };
        ID3D11VideoProcessorInputView_unmanaged* raw;
        _gpu.VideoDevice!.CreateVideoProcessorInputView(texture, _enumerator, &desc, &raw);
        var view = (ID3D11VideoProcessorInputView)Marshal.GetObjectForIUnknown((IntPtr)raw);
        Marshal.Release((IntPtr)raw);
        return view;
    }

    public ID3D11VideoProcessorOutputView CreateOutputView(ID3D11Texture2D texture)
    {
        var desc = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
        {
            ViewDimension = D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D,
        };
        desc.Texture2D = new D3D11_TEX2D_VPOV { MipSlice = 0 };
        ID3D11VideoProcessorOutputView_unmanaged* raw;
        _gpu.VideoDevice!.CreateVideoProcessorOutputView(texture, _enumerator, &desc, &raw);
        var view = (ID3D11VideoProcessorOutputView)Marshal.GetObjectForIUnknown((IntPtr)raw);
        Marshal.Release((IntPtr)raw);
        return view;
    }

    public void Blt(ID3D11VideoProcessorInputView input, ID3D11VideoProcessorOutputView output)
    {
        var streams = new[]
        {
            new D3D11_VIDEO_PROCESSOR_STREAM
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                pInputSurface = input,
            },
        };
        _gpu.VideoContext!.VideoProcessorBlt(_processor, output, 0, 1, streams);
    }

    public void Dispose()
    {
        Mf.Release(_processor);
        Mf.Release(_enumerator);
    }
}
