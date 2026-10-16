// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace RemoteApp.Client.Video;

/// <summary>Small conveniences over the CsWin32 Media Foundation projections.</summary>
internal static class Mf
{
    private static int _started;

    /// <summary>Starts Media Foundation once per process.</summary>
    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_NOSOCKET).ThrowOnFailure();
    }

    public static unsafe void SetGuid(this IMFAttributes attributes, Guid key, Guid value) =>
        attributes.SetGUID(&key, &value);

    public static unsafe void SetU32(this IMFAttributes attributes, Guid key, uint value) =>
        attributes.SetUINT32(&key, value);

    public static unsafe void SetU64(this IMFAttributes attributes, Guid key, ulong value) =>
        attributes.SetUINT64(&key, value);

    public static unsafe void SetBlob(this IMFAttributes attributes, Guid key, byte[] blob) =>
        attributes.SetBlob(&key, blob, (uint)blob.Length);

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

    public static unsafe bool TryGetU64(this IMFAttributes attributes, Guid key, out ulong value)
    {
        try { attributes.GetUINT64(&key, out value); return true; }
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

    /// <summary>Packs two 32-bit values the way MF_MT_FRAME_SIZE / MF_MT_FRAME_RATE expect (high = first).</summary>
    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    public static (uint High, uint Low) Unpack(ulong value) => ((uint)(value >> 32), (uint)(value & 0xFFFFFFFF));

    /// <summary>Releases a COM RCW eagerly (sample pools starve if we wait for the GC).</summary>
    public static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }
}
