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

namespace RemoteApp.Client.Video;

/// <summary>
/// The Direct3D 11 device shared by the decoder (through a Media Foundation
/// DXGI device manager, so it outputs GPU textures) and the presenter (video
/// processor + swap chain). Multithread protection is on because Media
/// Foundation uses the device from its own threads. Null when the adapter has
/// no video support; the pipeline then decodes to system memory and presents
/// with Direct2D.
/// </summary>
internal sealed class GraphicsDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public ID3D11VideoDevice VideoDevice { get; }
    public ID3D11VideoContext VideoContext { get; }
    public IMFDXGIDeviceManager Manager { get; }

    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context, ID3D11VideoDevice videoDevice,
        ID3D11VideoContext videoContext, IMFDXGIDeviceManager manager)
    {
        Device = device;
        Context = context;
        VideoDevice = videoDevice;
        VideoContext = videoContext;
        Manager = manager;
    }

    public static GraphicsDevice? TryCreate(Action<string>? log)
    {
        var hr = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            default, PInvoke.D3D11_SDK_VERSION, out var device, out _, out var context);
        if (hr.Failed)
        {
            log?.Invoke($"D3D11 video device unavailable (0x{hr.Value:X8}); using the CPU path");
            return null;
        }
        try
        {
            if (device is ID3D10Multithread multithread) multithread.SetMultithreadProtected(true);
            if (device is not ID3D11VideoDevice videoDevice || context is not ID3D11VideoContext videoContext)
            {
                log?.Invoke("D3D11 video processing not supported on this adapter; using the CPU path");
                Mf.Release(context);
                Mf.Release(device);
                return null;
            }
            Mf.EnsureStarted();
            PInvoke.MFCreateDXGIDeviceManager(out var token, out var manager).ThrowOnFailure();
            manager.ResetDevice(device, token);
            return new GraphicsDevice(device, context, videoDevice, videoContext, manager);
        }
        catch (COMException e)
        {
            log?.Invoke($"DXGI device manager unavailable (0x{e.HResult:X8}); using the CPU path");
            Mf.Release(context);
            Mf.Release(device);
            return null;
        }
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
        Mf.Release(Manager);
        Mf.Release(Context);
        Mf.Release(Device);
    }
}
