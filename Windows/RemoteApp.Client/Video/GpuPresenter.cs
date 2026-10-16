// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;

namespace RemoteApp.Client.Video;

/// <summary>
/// Presents decoded NV12 pictures with the GPU: a D3D11 video processor
/// converts, scales and letterboxes straight into a DXGI flip-model swap
/// chain on the window. Decoder textures are used in place (the sample is
/// held until the next frame so the decoder does not recycle it); pictures
/// decoded to system memory are uploaded to a texture first. Used from the
/// decode thread only.
/// </summary>
internal sealed unsafe class GpuPresenter : IDisposable
{
    private readonly GraphicsDevice _gpu;
    private readonly HWND _hwnd;
    private IDXGISwapChain1 _swapChain;
    private ID3D11Texture2D? _backBuffer;
    private ID3D11VideoProcessorOutputView? _outputView;
    private VideoProcessorConverter? _converter;
    private int _targetWidth;
    private int _targetHeight;

    // The picture being shown: a raw AddRef on the decoder's sample (keeps it
    // out of the pool) and on its texture, or our own upload texture.
    private IntPtr _heldSample;
    private IntPtr _heldTexture;
    private uint _heldSlice;
    private int _codedWidth;
    private int _codedHeight;
    private Rectangle _visible;
    private ID3D11Texture2D? _upload;        // DEFAULT NV12 for CPU-decoded frames
    private ID3D11Texture2D? _uploadStaging; // DYNAMIC NV12 written by the CPU
    private int _uploadWidth;
    private int _uploadHeight;
    private bool _disposed;

    /// <summary>Where the last frame was drawn, in client pixels (for input mapping).</summary>
    public Rectangle VideoRect { get; private set; }

    public GpuPresenter(GraphicsDevice gpu, IntPtr hwnd, int width, int height)
    {
        _gpu = gpu;
        _hwnd = new HWND(hwnd);
        _targetWidth = Math.Max(1, width);
        _targetHeight = Math.Max(1, height);

        var dxgiDevice = (IDXGIDevice)gpu.Device;
        dxgiDevice.GetAdapter(out var adapter);
        var factoryIid = typeof(IDXGIFactory2).GUID;
        adapter.GetParent(&factoryIid, out var factoryRaw);
        var factory = (IDXGIFactory2)Marshal.GetObjectForIUnknown((IntPtr)factoryRaw);
        Marshal.Release((IntPtr)factoryRaw);
        Mf.Release(adapter);
        try
        {
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)_targetWidth,
                Height = (uint)_targetHeight,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                Stereo = false,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_DISCARD,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE,
                Flags = 0,
            };
            factory.CreateSwapChainForHwnd(gpu.Device, _hwnd, &desc, null, null, out _swapChain);
        }
        finally
        {
            Mf.Release(factory);
        }
        AcquireBackBuffer();
    }

    private void AcquireBackBuffer()
    {
        var iid = typeof(ID3D11Texture2D).GUID;
        _swapChain.GetBuffer(0, &iid, out var raw);
        _backBuffer = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((IntPtr)raw);
        Marshal.Release((IntPtr)raw);
    }

    private void ReleaseBackBuffer()
    {
        if (_outputView != null) Mf.Release(_outputView);
        _outputView = null;
        if (_backBuffer != null) Mf.Release(_backBuffer);
        _backBuffer = null;
    }

    /// <summary>The window's client area changed size.</summary>
    public void Resize(int width, int height)
    {
        if (_disposed) return;
        _targetWidth = Math.Max(1, width);
        _targetHeight = Math.Max(1, height);
        ReleaseBackBuffer();
        _swapChain.ResizeBuffers(0, (uint)_targetWidth, (uint)_targetHeight, DXGI_FORMAT.DXGI_FORMAT_UNKNOWN, 0);
        AcquireBackBuffer();
        _converter?.Dispose();   // output size changed
        _converter = null;
        Draw();
    }

    /// <summary>Shows a decoder-owned NV12 texture (one array slice of it).</summary>
    public void Present(ID3D11Texture2D texture, uint slice, IMFSample sample, Rectangle visible, int codedWidth, int codedHeight)
    {
        if (_disposed) return;
        var samplePtr = Marshal.GetIUnknownForObject(sample);
        var texturePtr = Marshal.GetIUnknownForObject(texture);
        ReleaseHeld();
        _heldSample = samplePtr;
        _heldTexture = texturePtr;
        _heldSlice = slice;
        _visible = visible;
        _codedWidth = codedWidth;
        _codedHeight = codedHeight;
        Draw();
    }

    /// <summary>Shows a picture decoded to system memory (NV12 planes), uploaded to the GPU here.</summary>
    public void PresentCpu(byte* y, int yStride, byte* uv, int uvStride, int width, int height)
    {
        if (_disposed) return;
        if (_upload == null || _uploadWidth != width || _uploadHeight != height)
        {
            if (_upload != null) Mf.Release(_upload);
            if (_uploadStaging != null) Mf.Release(_uploadStaging);
            _upload = _gpu.CreateTexture(width, height, DXGI_FORMAT.DXGI_FORMAT_NV12, D3D11_USAGE.D3D11_USAGE_DEFAULT,
                D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE, 0);
            _uploadStaging = _gpu.CreateTexture(width, height, DXGI_FORMAT.DXGI_FORMAT_NV12, D3D11_USAGE.D3D11_USAGE_STAGING,
                0, D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_WRITE);
            _uploadWidth = width;
            _uploadHeight = height;
        }
        D3D11_MAPPED_SUBRESOURCE mapped;
        _gpu.Context.Map(_uploadStaging!, 0, D3D11_MAP.D3D11_MAP_WRITE, 0, &mapped);
        try
        {
            var dst = (byte*)mapped.pData;
            for (var row = 0; row < height; row++)
                new ReadOnlySpan<byte>(y + row * yStride, width).CopyTo(new Span<byte>(dst + row * mapped.RowPitch, width));
            var dstUv = dst + mapped.RowPitch * height;
            for (var row = 0; row < height / 2; row++)
                new ReadOnlySpan<byte>(uv + row * uvStride, width).CopyTo(new Span<byte>(dstUv + row * mapped.RowPitch, width));
        }
        finally
        {
            _gpu.Context.Unmap(_uploadStaging!, 0);
        }
        _gpu.Context.CopyResource(_upload!, _uploadStaging!);

        var texturePtr = Marshal.GetIUnknownForObject(_upload!);
        ReleaseHeld();
        _heldTexture = texturePtr;
        _heldSlice = 0;
        _visible = new Rectangle(0, 0, width, height);
        _codedWidth = width;
        _codedHeight = height;
        Draw();
    }

    /// <summary>Repaints the last picture (after a resize or WM_PAINT).</summary>
    public void Redraw()
    {
        if (!_disposed) Draw();
    }

    private void Draw()
    {
        if (_heldTexture == IntPtr.Zero || _backBuffer == null) return;
        if (_converter == null || _converter.InputWidth != _codedWidth || _converter.InputHeight != _codedHeight
            || _converter.OutputWidth != _targetWidth || _converter.OutputHeight != _targetHeight)
        {
            _converter?.Dispose();
            if (_outputView != null) Mf.Release(_outputView);
            _outputView = null;
            _converter = new VideoProcessorConverter(_gpu, _codedWidth, _codedHeight, _targetWidth, _targetHeight);
        }
        _outputView ??= _converter.CreateOutputView(_backBuffer);

        var destination = Letterbox(_targetWidth, _targetHeight, _visible.Width, _visible.Height);
        VideoRect = destination;
        _converter.SetRects(_visible, destination, new Rectangle(0, 0, _targetWidth, _targetHeight));

        var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(_heldTexture);
        try
        {
            var inputView = _converter.CreateInputView(texture, _heldSlice);
            try
            {
                _converter.Blt(inputView, _outputView);
            }
            finally
            {
                Mf.Release(inputView);
            }
        }
        finally
        {
            Mf.Release(texture);
        }
        var hr = _swapChain.Present(0, 0);
        if (hr == HRESULT.DXGI_ERROR_DEVICE_REMOVED || hr == HRESULT.DXGI_ERROR_DEVICE_RESET)
            throw new COMException("device lost", hr.Value);
    }

    private static Rectangle Letterbox(int targetWidth, int targetHeight, int videoWidth, int videoHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0) return new Rectangle(0, 0, targetWidth, targetHeight);
        var scale = Math.Min(targetWidth / (double)videoWidth, targetHeight / (double)videoHeight);
        var width = Math.Max(1, (int)Math.Round(videoWidth * scale));
        var height = Math.Max(1, (int)Math.Round(videoHeight * scale));
        return new Rectangle((targetWidth - width) / 2, (targetHeight - height) / 2, width, height);
    }

    private void ReleaseHeld()
    {
        if (_heldSample != IntPtr.Zero) Marshal.Release(_heldSample);
        if (_heldTexture != IntPtr.Zero) Marshal.Release(_heldTexture);
        _heldSample = IntPtr.Zero;
        _heldTexture = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseHeld();
        _converter?.Dispose();
        _converter = null;
        ReleaseBackBuffer();
        if (_upload != null) Mf.Release(_upload);
        if (_uploadStaging != null) Mf.Release(_uploadStaging);
        _upload = null;
        _uploadStaging = null;
        Mf.Release(_swapChain);
        _swapChain = null!;
    }
}
