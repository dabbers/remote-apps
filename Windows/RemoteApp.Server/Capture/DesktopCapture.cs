// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Graphics.Gdi;

namespace RemoteApp.Server.Capture;

/// <summary>
/// Captures one monitor with DXGI Desktop Duplication and delivers the region
/// under the shared window as BGRA frames, with the mouse pointer composited
/// in. Like the Mac server's display-level capture, anything drawn inside the
/// window's rectangle — menus, dialogs, tooltips — is included, which a
/// per-window capture would miss. The region can be moved while running.
/// </summary>
internal sealed unsafe class DesktopCapture : IDisposable
{
    public delegate void FrameHandler(byte* bgra, int stride, int width, int height, ulong captureWallMicros);

    private readonly HMONITOR _monitor;
    private readonly object _lock = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private Rectangle _crop;              // screen coordinates (physical pixels)
    private Rectangle _desktopBounds;     // the output's desktop coordinates
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;

    // Mouse pointer state from the duplication API.
    private byte[]? _pointerShape;
    private DXGI_OUTDUPL_POINTER_SHAPE_INFO _pointerInfo;
    private Point _pointerPosition;
    private bool _pointerVisible;

    public event FrameHandler? FrameCaptured;
    public event Action<string>? Error;

    public DesktopCapture(HMONITOR monitor, Rectangle crop)
    {
        _monitor = monitor;
        _crop = crop;
        _thread = new Thread(Run) { IsBackground = true, Name = "remoteapp-capture", Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    /// <summary>Change the captured screen region (window moved/resized).</summary>
    public void SetCrop(Rectangle crop)
    {
        lock (_lock) _crop = crop;
    }

    private void Run()
    {
        try
        {
            CreateDuplication();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            Error?.Invoke($"desktop duplication unavailable: {e.Message}");
            return;
        }

        while (!_stop.IsSet)
        {
            try
            {
                if (!CaptureOne()) continue;
            }
            catch (COMException e) when ((uint)e.HResult == (uint)HRESULT.DXGI_ERROR_ACCESS_LOST)
            {
                // Mode change, UAC desktop, or the output went away: rebuild.
                ReleaseDuplication();
                Thread.Sleep(250);
                try { CreateDuplication(); }
                catch (Exception inner) when (inner is COMException or InvalidOperationException)
                {
                    Error?.Invoke($"desktop duplication lost: {inner.Message}");
                    Thread.Sleep(1000);
                }
            }
            catch (COMException e)
            {
                Error?.Invoke($"capture error 0x{e.HResult:X8}");
                Thread.Sleep(250);
            }
        }
        ReleaseDuplication();
        ReleaseDevice();
    }

    private void CreateDuplication()
    {
        if (_device == null)
        {
            PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, default, PInvoke.D3D11_SDK_VERSION,
                out var device, out _, out var context).ThrowOnFailure();
            _device = device;
            _context = context;
        }

        // Find the DXGI output for the monitor.
        var dxgiDevice = (IDXGIDevice)_device;
        dxgiDevice.GetAdapter(out var adapter);
        IDXGIOutput? found = null;
        for (uint i = 0; ; i++)
        {
            if (adapter.EnumOutputs(i, out var output).Failed) break;
            var desc = output.GetDesc();
            if (desc.Monitor == _monitor)
            {
                found = output;
                _desktopBounds = new Rectangle(desc.DesktopCoordinates.left, desc.DesktopCoordinates.top,
                    desc.DesktopCoordinates.Width, desc.DesktopCoordinates.Height);
                break;
            }
            Mf.Release(output);
        }
        Mf.Release(adapter);
        if (found == null) throw new InvalidOperationException("the window's monitor is not on the primary graphics adapter");

        var output1 = (IDXGIOutput1)found;
        output1.DuplicateOutput(_device, out var duplication);
        Mf.Release(found);
        _duplication = duplication;
    }

    private void ReleaseDuplication()
    {
        if (_duplication != null) { try { _duplication.ReleaseFrame(); } catch (COMException) { } Mf.Release(_duplication); }
        _duplication = null;
        if (_staging != null) Mf.Release(_staging);
        _staging = null;
        _stagingWidth = _stagingHeight = 0;
    }

    private void ReleaseDevice()
    {
        if (_context != null) Mf.Release(_context);
        if (_device != null) Mf.Release(_device);
        _context = null;
        _device = null;
    }

    /// <summary>Acquires one desktop frame; returns false when nothing changed.</summary>
    private bool CaptureOne()
    {
        if (_duplication == null || _device == null || _context == null) { Thread.Sleep(100); return false; }

        DXGI_OUTDUPL_FRAME_INFO info;
        var hr = _duplication.AcquireNextFrame(16, &info, out var resource);
        if (hr == HRESULT.DXGI_ERROR_WAIT_TIMEOUT) return false;
        if (hr.Failed) throw new COMException("AcquireNextFrame failed", hr.Value);

        try
        {
            if (info.PointerShapeBufferSize > 0) ReadPointerShape(info.PointerShapeBufferSize);
            if (info.LastMouseUpdateTime != 0)
            {
                _pointerVisible = info.PointerPosition.Visible;
                _pointerPosition = new Point(info.PointerPosition.Position.X, info.PointerPosition.Position.Y);
            }
            // Pointer-only updates carry no new desktop image (and right after
            // DuplicateOutput the surface is undefined until the first real frame).
            if (info.LastPresentTime == 0) return false;
            Rectangle crop;
            lock (_lock) crop = _crop;
            var local = Rectangle.Intersect(new Rectangle(crop.X - _desktopBounds.X, crop.Y - _desktopBounds.Y, crop.Width, crop.Height),
                new Rectangle(0, 0, _desktopBounds.Width, _desktopBounds.Height));
            if (local.Width < 2 || local.Height < 2) return false;
            local.Width &= ~1;
            local.Height &= ~1;

            EnsureStaging(local.Width, local.Height);
            var texture = (ID3D11Texture2D)resource;
            var box = new D3D11_BOX
            {
                left = (uint)local.X, top = (uint)local.Y, front = 0,
                right = (uint)local.Right, bottom = (uint)local.Bottom, back = 1,
            };
            _context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, texture, 0, &box);
            Mf.Release(texture);

            D3D11_MAPPED_SUBRESOURCE mapped;
            _context.Map(_staging!, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            try
            {
                var pixels = (byte*)mapped.pData;
                if (_pointerVisible && _pointerShape != null)
                    DrawPointer(pixels, (int)mapped.RowPitch, local);
                var wall = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
                FrameCaptured?.Invoke(pixels, (int)mapped.RowPitch, local.Width, local.Height, wall);
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
            return true;
        }
        finally
        {
            Mf.Release(resource);
            _duplication.ReleaseFrame();
        }
    }

    private void EnsureStaging(int width, int height)
    {
        if (_staging != null && _stagingWidth == width && _stagingHeight == height) return;
        if (_staging != null) Mf.Release(_staging);
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };
        ID3D11Texture2D_unmanaged* texture;
        _device!.CreateTexture2D(&desc, null, &texture);
        _staging = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((IntPtr)texture);
        Marshal.Release((IntPtr)texture);
        _stagingWidth = width;
        _stagingHeight = height;
    }

    private void ReadPointerShape(uint size)
    {
        var buffer = new byte[size];
        DXGI_OUTDUPL_POINTER_SHAPE_INFO shape;
        fixed (byte* p = buffer)
        {
            _duplication!.GetFramePointerShape(size, p, out _, &shape);
        }
        _pointerShape = buffer;
        _pointerInfo = shape;
    }

    /// <summary>Composites the pointer (color, monochrome or masked color) into the captured region.</summary>
    private void DrawPointer(byte* pixels, int pitch, Rectangle local)
    {
        var shape = _pointerShape!;
        var info = _pointerInfo;
        var type = (DXGI_OUTDUPL_POINTER_SHAPE_TYPE)info.Type;
        var height = type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE.DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME ? (int)info.Height / 2 : (int)info.Height;
        var width = (int)info.Width;
        var originX = _pointerPosition.X - local.X;
        var originY = _pointerPosition.Y - local.Y;

        for (var row = 0; row < height; row++)
        {
            var y = originY + row;
            if (y < 0 || y >= local.Height) continue;
            var dst = pixels + y * pitch;
            for (var col = 0; col < width; col++)
            {
                var x = originX + col;
                if (x < 0 || x >= local.Width) continue;
                var d = dst + x * 4;
                switch (type)
                {
                    case DXGI_OUTDUPL_POINTER_SHAPE_TYPE.DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR:
                    {
                        var s = row * (int)info.Pitch + col * 4;
                        var a = shape[s + 3];
                        if (a == 0) break;
                        d[0] = (byte)((shape[s] * a + d[0] * (255 - a)) / 255);
                        d[1] = (byte)((shape[s + 1] * a + d[1] * (255 - a)) / 255);
                        d[2] = (byte)((shape[s + 2] * a + d[2] * (255 - a)) / 255);
                        break;
                    }
                    case DXGI_OUTDUPL_POINTER_SHAPE_TYPE.DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME:
                    {
                        var bit = 7 - (col & 7);
                        var andMask = (shape[row * (int)info.Pitch + (col >> 3)] >> bit) & 1;
                        var xorMask = (shape[(row + height) * (int)info.Pitch + (col >> 3)] >> bit) & 1;
                        // AND then XOR: 0/0 black, 0/1 white, 1/0 transparent, 1/1 inverted.
                        if (andMask == 0)
                        {
                            var v = (byte)(xorMask == 1 ? 255 : 0);
                            d[0] = d[1] = d[2] = v;
                        }
                        else if (xorMask == 1)
                        {
                            d[0] = (byte)~d[0]; d[1] = (byte)~d[1]; d[2] = (byte)~d[2];
                        }
                        break;
                    }
                    case DXGI_OUTDUPL_POINTER_SHAPE_TYPE.DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR:
                    {
                        var s = row * (int)info.Pitch + col * 4;
                        var mask = shape[s + 3];
                        if (mask == 0)
                        {
                            d[0] = shape[s]; d[1] = shape[s + 1]; d[2] = shape[s + 2];
                        }
                        else if (mask == 0xFF)
                        {
                            d[0] ^= shape[s]; d[1] ^= shape[s + 1]; d[2] ^= shape[s + 2];
                        }
                        break;
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(2))) { /* background thread */ }
    }
}
