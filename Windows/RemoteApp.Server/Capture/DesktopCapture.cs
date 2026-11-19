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
using Windows.Win32.System.Power;

namespace RemoteApp.Server.Capture;

/// <summary>
/// Captures one monitor with DXGI Desktop Duplication and delivers the region
/// under the shared window as a BGRA <b>GPU texture</b>, with the mouse
/// pointer composited in. Like the Mac server's display-level capture,
/// anything drawn inside the window's rectangle — menus, dialogs, tooltips —
/// is included, which a per-window capture would miss. The region can be
/// moved while running. Frames stay on the GPU; the pipeline decides whether
/// to convert there or read back.
///
/// The target monitor may be on an adapter other than the encoder's (a virtual
/// display or a Remote Desktop display is its own graphics adapter). Capture
/// then runs on the owning adapter and each frame is bridged to the encoder
/// device through system memory; when the two adapters are the same there is no
/// bridge and the encoder device is used directly.
/// </summary>
internal sealed unsafe class DesktopCapture : IDisposable
{
    /// <summary>The frame texture is valid until the handler returns (it is reused for the next frame).</summary>
    public delegate void FrameHandler(ID3D11Texture2D frame, int width, int height, ulong captureWallMicros);

    private readonly GraphicsDevice _gpu;
    private readonly HMONITOR _monitor;
    private readonly object _lock = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private Rectangle _crop;              // screen coordinates (physical pixels)
    private Rectangle _desktopBounds;     // the output's desktop coordinates
    private IDXGIOutputDuplication? _duplication;
    // Capture runs on the adapter that owns the target monitor, which may not be
    // the encoder's adapter. When it differs, _captureDevice is a device we own
    // on that adapter and frames are bridged to the encoder device (_gpu)
    // through system memory; when it is the same, _captureDevice aliases
    // _gpu.Device, _captureContext aliases _gpu.Context, and nothing is bridged.
    private ID3D11Device? _captureDevice;
    private ID3D11DeviceContext? _captureContext;
    private bool _bridged;
    private bool _ownCaptureDevice;
    private ID3D11Texture2D? _bridgeStaging;   // capture adapter: CPU-readable copy of _frame
    private ID3D11Texture2D? _bridgeTarget;    // encoder adapter: the texture the pipeline receives
    // REMOTEAPP_FORCE_CAPTURE_BRIDGE=1 forces the cross-adapter capture + bridge
    // path even when the monitor is on the encoder's adapter (diagnostics/testing).
    private static readonly bool ForceBridge = Environment.GetEnvironmentVariable("REMOTEAPP_FORCE_CAPTURE_BRIDGE") == "1";
    private ID3D11Texture2D? _frame;
    private int _frameWidth;
    private int _frameHeight;
    private ID3D11Texture2D? _pointerStaging;
    private int _pointerStagingWidth;
    private int _pointerStagingHeight;
    private byte[] _pointerPixels = [];

    // Mouse pointer state from the duplication API.
    private byte[]? _pointerShape;
    private DXGI_OUTDUPL_POINTER_SHAPE_INFO _pointerInfo;
    private Point _pointerPosition;
    private bool _pointerVisible;

    public event FrameHandler? FrameCaptured;
    public event Action<string>? Error;
    /// <summary>Informational notes for the server log (not sent to the client as errors).</summary>
    public event Action<string>? Log;

    public DesktopCapture(GraphicsDevice gpu, HMONITOR monitor, Rectangle crop)
    {
        _gpu = gpu;
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
        // Nothing may escape this thread: an exception that does ends the whole
        // process (CsWin32 maps E_ACCESSDENIED to UnauthorizedAccessException,
        // which is not a COMException, and that is exactly how the server once
        // died silently while a client was connecting).
        try
        {
            KeepDisplayOn();
            if (!TryCreateDuplication(attempts: 3)) return;
            var failures = 0;
            while (!_stop.IsSet)
            {
                if (_duplication == null)
                {
                    // Lost below: rebuild, with a pause between attempts.
                    try { CreateDuplication(); failures = 0; }
                    catch (Exception e)
                    {
                        if (failures++ == 0) Error?.Invoke($"desktop duplication lost: {Describe(e)}; retrying");
                        if (failures > 20) { Error?.Invoke("desktop duplication could not be restored; capture stopped"); return; }
                        _stop.Wait(1000);
                        continue;
                    }
                }
                try
                {
                    CaptureOne();
                }
                catch (Exception e) when (IsAccessLost(e))
                {
                    // Mode change, UAC/secure desktop, or the output went away.
                    ReleaseDuplication();
                    _stop.Wait(250);
                }
                catch (Exception e)
                {
                    Error?.Invoke($"capture error: {Describe(e)}");
                    _stop.Wait(250);
                }
            }
        }
        catch (Exception e)
        {
            Error?.Invoke($"capture stopped: {Describe(e)}");
        }
        finally
        {
            ReleaseDuplication();
            ReleaseTextures();
            PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
        }
    }

    /// <summary>
    /// A host that is used remotely turns its display off after the power
    /// plan's idle timeout, and the desktop is then no longer composed: the
    /// duplication only times out, even as windows appear (measured: a
    /// connecting client got no frame at all until an input event). Wake the
    /// display and keep it on while this thread captures. The one-shot call is
    /// what wakes a display that is already off; the continuous state, which
    /// is per thread and cleared above when the thread ends, keeps it on.
    /// </summary>
    private static void KeepDisplayOn()
    {
        PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_DISPLAY_REQUIRED);
        PInvoke.SetThreadExecutionState(EXECUTION_STATE.ES_DISPLAY_REQUIRED);
    }

    /// <summary>
    /// Creates the duplication, retrying briefly when the desktop refuses access
    /// (a secure-desktop switch is momentary); reports and returns false when it
    /// stays unavailable, with the reason a user can act on.
    /// </summary>
    private bool TryCreateDuplication(int attempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                CreateDuplication();
                return true;
            }
            catch (Exception e)
            {
                if (IsAccessDenied(e) && attempt < attempts && !_stop.IsSet) { _stop.Wait(300); continue; }
                Error?.Invoke("desktop duplication unavailable: " + (IsAccessDenied(e)
                    ? "access denied. A Remote Desktop session or the secure desktop can refuse it; the session must be on the console with a display (see the README, \"Using the Windows host remotely\")"
                    : Describe(e)));
                return false;
            }
        }
    }

    private static bool IsAccessDenied(Exception e) => e is UnauthorizedAccessException || (uint)e.HResult == 0x80070005;

    private static bool IsAccessLost(Exception e) => (uint)e.HResult == (uint)HRESULT.DXGI_ERROR_ACCESS_LOST || IsAccessDenied(e);

    private static string Describe(Exception e) => e is COMException ? $"0x{e.HResult:X8} {e.Message}" : e.Message;

    private void CreateDuplication()
    {
        var output1 = ResolveOutput(out var owningAdapter);
        try
        {
            PrepareCaptureDevice(owningAdapter);
            output1.DuplicateOutput(_captureDevice!, out var duplication);
            _duplication = duplication;
        }
        finally
        {
            Mf.Release(output1);
            if (owningAdapter != null) Mf.Release(owningAdapter);
        }
    }

    /// <summary>
    /// Finds the DXGI output for the target monitor. The encoder's own adapter is
    /// tried first (the common case, no bridge). If the monitor is not there — a
    /// virtual or Remote Desktop display lives on its own adapter — every adapter
    /// is searched and the owning one is returned (in <paramref name="owningAdapter"/>)
    /// so capture can run on it.
    /// </summary>
    private IDXGIOutput1 ResolveOutput(out IDXGIAdapter? owningAdapter)
    {
        owningAdapter = null;
        var outputs = 0;

        // Fast path: the encoder's own adapter owns the monitor, so no bridge is
        // needed. REMOTEAPP_FORCE_CAPTURE_BRIDGE skips it to exercise the
        // cross-adapter path on a single-GPU machine.
        if (!ForceBridge)
        {
            var dxgiDevice = (IDXGIDevice)_gpu.Device;
            dxgiDevice.GetAdapter(out var gpuAdapter);
            var onGpu = FindOutput(gpuAdapter, ref outputs);
            Mf.Release(gpuAdapter);
            if (onGpu != null) return onGpu;
        }

        var factory = GetDxgiFactory();
        try
        {
            for (uint i = 0; ; i++)
            {
                // A void EnumAdapters throws DXGI_ERROR_NOT_FOUND past the last adapter.
                try
                {
                    factory.EnumAdapters(i, out var adapter);
                    var found = FindOutput(adapter, ref outputs);
                    if (found != null) { owningAdapter = adapter; return found; }
                    Mf.Release(adapter);
                }
                catch (COMException) { break; }
            }
        }
        finally { Mf.Release(factory); }

        // A disconnected Remote Desktop session has no display on any adapter and
        // nothing to duplicate (nor a foreground window for input); say so rather
        // than blaming the GPU. Otherwise the window's monitor genuinely vanished.
        throw new InvalidOperationException(outputs == 0
            ? "no display is attached to the host's session (a disconnected Remote Desktop session has none): " +
              "reattach the session to the console or keep a physical or virtual display attached"
            : "the window's monitor could not be found on any graphics adapter (it may have been disconnected)");
    }

    /// <summary>Returns the output for the target monitor on one adapter, or null; counts the outputs it saw.</summary>
    private IDXGIOutput1? FindOutput(IDXGIAdapter adapter, ref int outputs)
    {
        for (uint i = 0; adapter.EnumOutputs(i, out var output).Succeeded; i++)
        {
            outputs++;
            var desc = output.GetDesc();
            if (desc.Monitor == _monitor)
            {
                _desktopBounds = new Rectangle(desc.DesktopCoordinates.left, desc.DesktopCoordinates.top,
                    desc.DesktopCoordinates.Width, desc.DesktopCoordinates.Height);
                return (IDXGIOutput1)output;   // same RCW as output; released by the caller
            }
            Mf.Release(output);
        }
        return null;
    }

    private IDXGIFactory1 GetDxgiFactory()
    {
        var iid = typeof(IDXGIFactory1).GUID;
        PInvoke.CreateDXGIFactory1(&iid, out var factory).ThrowOnFailure();
        return (IDXGIFactory1)factory;
    }

    /// <summary>
    /// Sets up the device capture runs on, once, and reuses it across rebuilds.
    /// When the monitor is on the encoder's adapter, that device is reused and no
    /// frame is bridged. When it is on another adapter (a virtual or Remote
    /// Desktop display), a plain D3D11 device is created on that adapter; its
    /// frames are later bridged to the encoder device, which does the encoding.
    /// </summary>
    private void PrepareCaptureDevice(IDXGIAdapter? owningAdapter)
    {
        if (_captureDevice != null) return;
        if (owningAdapter == null)
        {
            _captureDevice = _gpu.Device;
            _captureContext = _gpu.Context;
            _bridged = false;
            return;
        }
        ID3D11Device device;
        ID3D11DeviceContext context;
        PInvoke.D3D11CreateDevice(owningAdapter, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN, HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, default, PInvoke.D3D11_SDK_VERSION,
            out device, out _, out context).ThrowOnFailure();
        _captureDevice = device;
        _captureContext = context;
        _ownCaptureDevice = true;
        _bridged = true;
        Log?.Invoke("the window's display is on another graphics adapter; capturing it and bridging frames to the encoder GPU through system memory");
    }

    private void ReleaseDuplication()
    {
        if (_duplication != null) { try { _duplication.ReleaseFrame(); } catch (COMException) { } Mf.Release(_duplication); }
        _duplication = null;
    }

    private void ReleaseTextures()
    {
        if (_frame != null) Mf.Release(_frame);
        _frame = null;
        _frameWidth = _frameHeight = 0;
        if (_pointerStaging != null) Mf.Release(_pointerStaging);
        _pointerStaging = null;
        _pointerStagingWidth = _pointerStagingHeight = 0;
        if (_bridgeStaging != null) Mf.Release(_bridgeStaging);
        _bridgeStaging = null;
        if (_bridgeTarget != null) Mf.Release(_bridgeTarget);
        _bridgeTarget = null;
        // Textures on the capture device are released above, before the device.
        if (_ownCaptureDevice)
        {
            if (_captureContext != null) Mf.Release(_captureContext);
            if (_captureDevice != null) Mf.Release(_captureDevice);
        }
        _captureDevice = null;
        _captureContext = null;
        _ownCaptureDevice = false;
        _bridged = false;
    }

    /// <summary>Acquires one desktop frame; returns false when nothing changed.</summary>
    private bool CaptureOne()
    {
        if (_duplication == null) { Thread.Sleep(100); return false; }

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

            EnsureFrameTexture(local.Width, local.Height);
            var desktop = (ID3D11Texture2D)resource;
            var box = new D3D11_BOX
            {
                left = (uint)local.X, top = (uint)local.Y, front = 0,
                right = (uint)local.Right, bottom = (uint)local.Bottom, back = 1,
            };
            _captureContext!.CopySubresourceRegion(_frame!, 0, 0, 0, 0, desktop, 0, &box);
            Mf.Release(desktop);

            if (_pointerVisible && _pointerShape != null) CompositePointer(local);

            var delivered = _bridged ? BridgeToEncoderDevice() : _frame!;

            var wall = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
            FrameCaptured?.Invoke(delivered, local.Width, local.Height, wall);
            return true;
        }
        finally
        {
            Mf.Release(resource);
            _duplication.ReleaseFrame();
        }
    }

    private void EnsureFrameTexture(int width, int height)
    {
        if (_frame != null && _frameWidth == width && _frameHeight == height) return;
        if (_frame != null) Mf.Release(_frame);
        _frame = CreateTexture(_captureDevice!, width, height, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_USAGE.D3D11_USAGE_DEFAULT,
            D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE, 0);
        _frameWidth = width;
        _frameHeight = height;
        if (_bridged)
        {
            if (_bridgeStaging != null) Mf.Release(_bridgeStaging);
            if (_bridgeTarget != null) Mf.Release(_bridgeTarget);
            // Staging on the capture adapter (CPU-readable); target on the encoder
            // adapter with the bind flags _frame would have had there.
            _bridgeStaging = CreateTexture(_captureDevice!, width, height, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                D3D11_USAGE.D3D11_USAGE_STAGING, 0, D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ);
            _bridgeTarget = _gpu.CreateTexture(width, height, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_USAGE.D3D11_USAGE_DEFAULT,
                D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE, 0);
        }
    }

    /// <summary>
    /// Copies the finished BGRA frame from the capture adapter to the encoder
    /// adapter through a CPU-mapped staging texture, and returns the encoder-side
    /// texture the pipeline consumes. Only used when the two adapters differ.
    /// </summary>
    private ID3D11Texture2D BridgeToEncoderDevice()
    {
        _captureContext!.CopyResource(_bridgeStaging!, _frame!);
        D3D11_MAPPED_SUBRESOURCE mapped;
        _captureContext.Map(_bridgeStaging!, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
        try
        {
            _gpu.Context.UpdateSubresource(_bridgeTarget!, 0, null, mapped.pData, mapped.RowPitch, 0);
        }
        finally
        {
            _captureContext.Unmap(_bridgeStaging!, 0);
        }
        return _bridgeTarget!;
    }

    /// <summary>Creates a 2D texture on a specific device; the caller releases it.</summary>
    private static ID3D11Texture2D CreateTexture(ID3D11Device device, int width, int height, DXGI_FORMAT format,
        D3D11_USAGE usage, D3D11_BIND_FLAG bind, D3D11_CPU_ACCESS_FLAG cpuAccess)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = usage,
            BindFlags = bind,
            CPUAccessFlags = cpuAccess,
            MiscFlags = 0,
        };
        ID3D11Texture2D_unmanaged* raw;
        device.CreateTexture2D(&desc, null, &raw);
        var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((IntPtr)raw);
        Marshal.Release((IntPtr)raw);
        return texture;
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

    /// <summary>
    /// Composites the pointer into the frame texture: the (small) rectangle
    /// under the pointer is read back, blended on the CPU (color, monochrome
    /// or masked color shapes) and written back with UpdateSubresource.
    /// </summary>
    private void CompositePointer(Rectangle local)
    {
        var info = _pointerInfo;
        var type = (DXGI_OUTDUPL_POINTER_SHAPE_TYPE)info.Type;
        var shapeHeight = type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE.DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME ? (int)info.Height / 2 : (int)info.Height;
        var pointerRect = new Rectangle(_pointerPosition.X - local.X, _pointerPosition.Y - local.Y, (int)info.Width, shapeHeight);
        var area = Rectangle.Intersect(pointerRect, new Rectangle(0, 0, local.Width, local.Height));
        if (area.Width <= 0 || area.Height <= 0) return;

        EnsurePointerStaging(area.Width, area.Height);
        var box = new D3D11_BOX
        {
            left = (uint)area.X, top = (uint)area.Y, front = 0,
            right = (uint)area.Right, bottom = (uint)area.Bottom, back = 1,
        };
        _captureContext!.CopySubresourceRegion(_pointerStaging!, 0, 0, 0, 0, _frame!, 0, &box);

        var stride = area.Width * 4;
        if (_pointerPixels.Length < stride * area.Height) _pointerPixels = new byte[stride * area.Height];
        D3D11_MAPPED_SUBRESOURCE mapped;
        _captureContext!.Map(_pointerStaging!, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
        try
        {
            for (var row = 0; row < area.Height; row++)
                new ReadOnlySpan<byte>((byte*)mapped.pData + row * mapped.RowPitch, stride).CopyTo(_pointerPixels.AsSpan(row * stride));
        }
        finally
        {
            _captureContext!.Unmap(_pointerStaging!, 0);
        }

        fixed (byte* pixels = _pointerPixels)
        {
            DrawPointer(pixels, stride, area, pointerRect.Location);
            _captureContext!.UpdateSubresource(_frame!, 0, &box, pixels, (uint)stride, 0);
        }
    }

    private void EnsurePointerStaging(int width, int height)
    {
        if (_pointerStaging != null && _pointerStagingWidth >= width && _pointerStagingHeight >= height) return;
        if (_pointerStaging != null) Mf.Release(_pointerStaging);
        var w = Math.Max(width, 64);
        var h = Math.Max(height, 64);
        _pointerStaging = CreateTexture(_captureDevice!, w, h, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, D3D11_USAGE.D3D11_USAGE_STAGING,
            0, D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ);
        _pointerStagingWidth = w;
        _pointerStagingHeight = h;
    }

    /// <summary>Blends the pointer shape into <paramref name="pixels"/>, which holds <paramref name="area"/> of the frame.</summary>
    private void DrawPointer(byte* pixels, int pitch, Rectangle area, Point pointerOrigin)
    {
        var shape = _pointerShape!;
        var info = _pointerInfo;
        var type = (DXGI_OUTDUPL_POINTER_SHAPE_TYPE)info.Type;
        var height = type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE.DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME ? (int)info.Height / 2 : (int)info.Height;
        var width = (int)info.Width;

        for (var row = 0; row < height; row++)
        {
            var y = pointerOrigin.Y + row - area.Y;
            if (y < 0 || y >= area.Height) continue;
            var dst = pixels + y * pitch;
            for (var col = 0; col < width; col++)
            {
                var x = pointerOrigin.X + col - area.X;
                if (x < 0 || x >= area.Width) continue;
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
