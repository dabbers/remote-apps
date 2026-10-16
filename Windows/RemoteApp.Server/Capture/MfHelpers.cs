// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace RemoteApp.Server.Capture;

/// <summary>Small conveniences over the CsWin32 Media Foundation projections.</summary>
internal static class Mf
{
    private static int _started;

    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_NOSOCKET).ThrowOnFailure();
    }

    public static unsafe void SetGuid(this IMFAttributes attributes, Guid key, Guid value) => attributes.SetGUID(&key, &value);
    public static unsafe void SetU32(this IMFAttributes attributes, Guid key, uint value) => attributes.SetUINT32(&key, value);
    public static unsafe void SetU64(this IMFAttributes attributes, Guid key, ulong value) => attributes.SetUINT64(&key, value);

    public static unsafe Guid GetGuid(this IMFAttributes attributes, Guid key)
    {
        Guid value;
        attributes.GetGUID(&key, &value);
        return value;
    }

    public static unsafe bool TryGetU32(this IMFAttributes attributes, Guid key, out uint value)
    {
        try { attributes.GetUINT32(&key, out value); return true; }
        catch (COMException) { value = 0; return false; }
    }

    public static unsafe bool TryGetBlob(this IMFAttributes attributes, Guid key, out byte[] blob)
    {
        try
        {
            attributes.GetBlobSize(&key, out var size);
            blob = new byte[size];
            attributes.GetBlob(&key, blob, size, null);
            return true;
        }
        catch (COMException)
        {
            blob = [];
            return false;
        }
    }

    public static unsafe string? TryGetString(this IMFAttributes attributes, Guid key)
    {
        try
        {
            attributes.GetStringLength(&key, out var length);
            var buffer = new char[length + 1];
            fixed (char* p = buffer)
            {
                attributes.GetString(&key, new Windows.Win32.Foundation.PWSTR(p), length + 1, null);
            }
            return new string(buffer, 0, (int)length);
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
