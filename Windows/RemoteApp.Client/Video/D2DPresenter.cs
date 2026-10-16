// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Dxgi.Common;

namespace RemoteApp.Client.Video;

/// <summary>
/// Presents BGRA frames into a window with Direct2D: GPU-scaled (bilinear)
/// and letterboxed when the frame's aspect differs from the window's. Safe
/// to call from the decode thread and the UI thread (internally locked).
/// </summary>
internal sealed unsafe class D2DPresenter : IDisposable
{
    private readonly object _lock = new();
    private readonly HWND _hwnd;
    private readonly ID2D1Factory _factory;
    private ID2D1HwndRenderTarget? _target;
    private ID2D1Bitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private int _targetWidth;
    private int _targetHeight;
    private bool _disposed;

    /// <summary>Where the last frame was drawn, in client pixels (for input mapping).</summary>
    public Rectangle VideoRect { get; private set; }

    public D2DPresenter(IntPtr hwnd, int width, int height)
    {
        _hwnd = new HWND(hwnd);
        _targetWidth = Math.Max(1, width);
        _targetHeight = Math.Max(1, height);
        PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED, typeof(ID2D1Factory).GUID, null, out var factory)
            .ThrowOnFailure();
        _factory = (ID2D1Factory)factory;
        CreateTarget();
    }

    private void CreateTarget()
    {
        var targetProperties = new D2D1_RENDER_TARGET_PROPERTIES
        {
            type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_DEFAULT,
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE,
            },
            // 96 DPI so one Direct2D unit is one pixel regardless of monitor scaling.
            dpiX = 96,
            dpiY = 96,
        };
        var hwndProperties = new D2D1_HWND_RENDER_TARGET_PROPERTIES
        {
            hwnd = _hwnd,
            pixelSize = new D2D_SIZE_U { width = (uint)_targetWidth, height = (uint)_targetHeight },
            // Present without waiting for vsync: lower latency for an interactive stream.
            presentOptions = D2D1_PRESENT_OPTIONS.D2D1_PRESENT_OPTIONS_IMMEDIATELY,
        };
        _factory.CreateHwndRenderTarget(&targetProperties, &hwndProperties, out _target);
        ReleaseBitmap();
    }

    private void ReleaseBitmap()
    {
        if (_bitmap != null) Mf.Release(_bitmap);
        _bitmap = null;
        _bitmapWidth = _bitmapHeight = 0;
    }

    private void ReleaseTarget()
    {
        ReleaseBitmap();
        if (_target != null) Mf.Release(_target);
        _target = null;
    }

    /// <summary>The window's client area changed size.</summary>
    public void Resize(int width, int height)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _targetWidth = Math.Max(1, width);
            _targetHeight = Math.Max(1, height);
            if (_target != null)
            {
                var size = new D2D_SIZE_U { width = (uint)_targetWidth, height = (uint)_targetHeight };
                if (_target.Resize(&size).Failed)
                {
                    ReleaseTarget();
                    CreateTarget();
                }
            }
            Draw();
        }
    }

    /// <summary>Uploads a BGRA frame and draws it.</summary>
    public void Present(byte* bgra, int width, int height, int stride)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (_target == null) CreateTarget();
            if (_bitmap == null || _bitmapWidth != width || _bitmapHeight != height)
            {
                ReleaseBitmap();
                var properties = new D2D1_BITMAP_PROPERTIES
                {
                    pixelFormat = new D2D1_PIXEL_FORMAT
                    {
                        format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                        alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE,
                    },
                    dpiX = 96,
                    dpiY = 96,
                };
                _target!.CreateBitmap(new D2D_SIZE_U { width = (uint)width, height = (uint)height }, null, 0, &properties, out _bitmap);
                _bitmapWidth = width;
                _bitmapHeight = height;
            }
            _bitmap!.CopyFromMemory(null, bgra, (uint)stride);
            Draw();
        }
    }

    /// <summary>Repaints the last frame (after a resize or WM_PAINT).</summary>
    public void Redraw()
    {
        lock (_lock)
        {
            if (_disposed || _target == null) return;
            Draw();
        }
    }

    private void Draw()
    {
        if (_target == null) return;
        var destination = Letterbox(_targetWidth, _targetHeight, _bitmapWidth, _bitmapHeight);
        VideoRect = destination;

        _target.BeginDraw();
        var black = new D2D1_COLOR_F { r = 0, g = 0, b = 0, a = 1 };
        _target.Clear(&black);
        if (_bitmap != null)
        {
            var rect = new D2D_RECT_F
            {
                left = destination.Left,
                top = destination.Top,
                right = destination.Right,
                bottom = destination.Bottom,
            };
            _target.DrawBitmap(_bitmap, &rect, 1f, D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR, null);
        }
        var hr = ((ID2D1RenderTarget)_target).EndDraw(null, null);
        if (hr == HRESULT.D2DERR_RECREATE_TARGET)
        {
            // Device lost (driver reset, display change): rebuild; the next frame repaints.
            ReleaseTarget();
            CreateTarget();
        }
    }

    /// <summary>Largest aspect-correct rectangle for a bitmap inside the target, centred.</summary>
    private static Rectangle Letterbox(int targetWidth, int targetHeight, int bitmapWidth, int bitmapHeight)
    {
        if (bitmapWidth <= 0 || bitmapHeight <= 0) return new Rectangle(0, 0, targetWidth, targetHeight);
        var scale = Math.Min(targetWidth / (double)bitmapWidth, targetHeight / (double)bitmapHeight);
        var width = Math.Max(1, (int)Math.Round(bitmapWidth * scale));
        var height = Math.Max(1, (int)Math.Round(bitmapHeight * scale));
        return new Rectangle((targetWidth - width) / 2, (targetHeight - height) / 2, width, height);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseTarget();
            if (Marshal.IsComObject(_factory)) Marshal.FinalReleaseComObject(_factory);
        }
    }
}
