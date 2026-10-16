// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

namespace RemoteApp.Client.Video;

/// <summary>
/// NV12 → BGRA (BT.709, limited range) in fixed point, parallel over row pairs.
/// A CPU path keeps the pipeline simple; a GPU conversion (D3D11 video
/// processor) is the obvious upgrade if large windows at 60 fps get CPU-bound.
/// </summary>
internal static unsafe class Nv12Converter
{
    // 10-bit fixed point: 1.164 → 1192, 1.793 → 1836, 0.213 → 218, 0.533 → 546, 2.112 → 2163.
    private const int YScale = 1192;
    private const int RV = 1836;
    private const int GU = -218;
    private const int GV = -546;
    private const int BU = 2163;

    public static void ToBgra(byte* y, int yStride, byte* uv, int uvStride, int width, int height, byte* dst, int dstStride)
    {
        var yAddr = (nint)y;
        var uvAddr = (nint)uv;
        var dstAddr = (nint)dst;
        var rowPairs = (height + 1) / 2;
        // Chunk a few row pairs per task so scheduling overhead stays small.
        const int pairsPerChunk = 8;
        var chunks = (rowPairs + pairsPerChunk - 1) / pairsPerChunk;
        Parallel.For(0, chunks, chunk =>
        {
            var firstPair = chunk * pairsPerChunk;
            var lastPair = Math.Min(firstPair + pairsPerChunk, rowPairs);
            for (var pair = firstPair; pair < lastPair; pair++)
            {
                var uvRow = (byte*)uvAddr + pair * uvStride;
                for (var sub = 0; sub < 2; sub++)
                {
                    var row = pair * 2 + sub;
                    if (row >= height) break;
                    var yRow = (byte*)yAddr + row * yStride;
                    var dstRow = (byte*)dstAddr + row * dstStride;
                    ConvertRow(yRow, uvRow, dstRow, width);
                }
            }
        });
    }

    private static void ConvertRow(byte* yRow, byte* uvRow, byte* dstRow, int width)
    {
        var x = 0;
        for (; x + 1 < width; x += 2)
        {
            var u = uvRow[x] - 128;
            var v = uvRow[x + 1] - 128;
            var rv = RV * v + 512;
            var guv = GU * u + GV * v + 512;
            var bu = BU * u + 512;

            var c0 = (yRow[x] - 16) * YScale;
            var c1 = (yRow[x + 1] - 16) * YScale;
            var p = dstRow + x * 4;
            p[0] = Clamp((c0 + bu) >> 10);
            p[1] = Clamp((c0 + guv) >> 10);
            p[2] = Clamp((c0 + rv) >> 10);
            p[3] = 255;
            p[4] = Clamp((c1 + bu) >> 10);
            p[5] = Clamp((c1 + guv) >> 10);
            p[6] = Clamp((c1 + rv) >> 10);
            p[7] = 255;
        }
        if (x < width)
        {
            var u = uvRow[x] - 128;
            var v = uvRow[x + 1] - 128;
            var c = (yRow[x] - 16) * YScale;
            var p = dstRow + x * 4;
            p[0] = Clamp((c + BU * u + 512) >> 10);
            p[1] = Clamp((c + GU * u + GV * v + 512) >> 10);
            p[2] = Clamp((c + RV * v + 512) >> 10);
            p[3] = 255;
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
