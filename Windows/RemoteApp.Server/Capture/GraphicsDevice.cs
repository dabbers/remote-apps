// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D10;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.Media.MediaFoundation;

namespace RemoteApp.Server.Capture;

/// <summary>
/// One Direct3D 11 device shared by capture, the video processor (GPU color
/// conversion) and the encoder (through a Media Foundation DXGI device
/// manager). Multithread protection is on because Media Foundation uses the
/// device from its own threads. <see cref="HasVideo"/> is false on adapters
/// without video processing support; the pipeline then converts on the CPU.
/// </summary>
internal sealed class GraphicsDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11VideoDevice? VideoDevice { get; }
    public ID3D11VideoContext? VideoContext { get; }
    public IMFDXGIDeviceManager? Manager { get; }
    public bool HasVideo => VideoDevice != null && VideoContext != null && Manager != null;

    private static readonly object SharedLock = new();
    private static GraphicsDevice? _shared;
    private static bool _sharedTried;

    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, ID3D11VideoDevice? videoDevice,
        ID3D11VideoContext? videoContext, IMFDXGIDeviceManager? manager)
    {
        Device = device;
        Context = context;
        VideoDevice = videoDevice;
        VideoContext = videoContext;
        Manager = manager;
    }

    /// <summary>The process-wide device, created on first use (null if D3D11 is unavailable).</summary>
    public static GraphicsDevice? Shared(Action<string>? log)
    {
        lock (SharedLock)
        {
            if (!_sharedTried)
            {
                _sharedTried = true;
                _shared = TryCreate(log);
            }
            return _shared;
        }
    }

    public static GraphicsDevice? TryCreate(Action<string>? log)
    {
        ID3D11Device device;
        ID3D11DeviceContext context;
        var hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            default, PInvoke.D3D11_SDK_VERSION, out device, out _, out context);
        if (hr.Failed)
        {
            // Some drivers refuse VIDEO_SUPPORT; a plain device still serves capture.
            hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, default, PInvoke.D3D11_SDK_VERSION,
                out device, out _, out context);
            if (hr.Failed)
            {
                log?.Invoke($"D3D11 device unavailable: 0x{hr.Value:X8}");
                return null;
            }
        }

        try
        {
            if (device is ID3D10Multithread multithread) multithread.SetMultithreadProtected(true);
        }
        catch (COMException) { /* not fatal */ }

        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext? videoContext = null;
        IMFDXGIDeviceManager? manager = null;
        try
        {
            // REMOTEAPP_FORCE_CPU_VIDEO=1 keeps the CPU conversion path (diagnostics, comparisons).
            var forceCpu = Environment.GetEnvironmentVariable("REMOTEAPP_FORCE_CPU_VIDEO") == "1";
            if (!forceCpu && device is ID3D11VideoDevice vd && context is ID3D11VideoContext vc)
            {
                Mf.EnsureStarted();
                PInvoke.MFCreateDXGIDeviceManager(out var token, out var created).ThrowOnFailure();
                created.ResetDevice(device, token);
                videoDevice = vd;
                videoContext = vc;
                manager = created;
            }
            else
            {
                log?.Invoke("D3D11 video processing not supported on this adapter; converting on the CPU");
            }
        }
        catch (COMException e)
        {
            log?.Invoke($"DXGI device manager unavailable (0x{e.HResult:X8}); converting on the CPU");
            videoDevice = null;
            videoContext = null;
            manager = null;
        }
        return new GraphicsDevice(device, context, videoDevice, videoContext, manager);
    }

    /// <summary>Creates a 2D texture; the caller releases it.</summary>
    public unsafe ID3D11Texture2D CreateTexture(int width, int height, DXGI_FORMAT format, D3D11_USAGE usage,
        D3D11_BIND_FLAG bind, D3D11_CPU_ACCESS_FLAG cpuAccess)
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
        Device.CreateTexture2D(&desc, null, &raw);
        var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((IntPtr)raw);
        Marshal.Release((IntPtr)raw);
        return texture;
    }

    public void Dispose()
    {
        if (Manager != null) Mf.Release(Manager);
        Mf.Release(Context);
        Mf.Release(Device);
    }
}
