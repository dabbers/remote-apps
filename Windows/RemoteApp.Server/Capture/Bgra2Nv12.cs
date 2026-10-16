// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

namespace RemoteApp.Server.Capture;

/// <summary>
/// BGRA → NV12 (BT.709, limited range) in fixed point, parallel over row
/// pairs. Width and height must be even. CPU for now; a D3D11 video processor
/// would move this to the GPU.
/// </summary>
internal static unsafe class Bgra2Nv12
{
    // BT.709 limited range, ×256: Y = 16 + (47R + 157G + 16B)/256; U = 128 + (−26R − 87G + 112B)/256; V = 128 + (112R − 102G − 10B)/256.
    public static void Convert(byte* bgra, int bgraStride, int width, int height, byte* y, int yStride, byte* uv, int uvStride)
    {
        var src = (nint)bgra;
        var yAddr = (nint)y;
        var uvAddr = (nint)uv;
        var rowPairs = height / 2;
        const int pairsPerChunk = 8;
        var chunks = (rowPairs + pairsPerChunk - 1) / pairsPerChunk;
        Parallel.For(0, chunks, chunk =>
        {
            var firstPair = chunk * pairsPerChunk;
            var lastPair = Math.Min(firstPair + pairsPerChunk, rowPairs);
            for (var pair = firstPair; pair < lastPair; pair++)
            {
                var row0 = (byte*)src + (pair * 2) * bgraStride;
                var row1 = row0 + bgraStride;
                var y0 = (byte*)yAddr + (pair * 2) * yStride;
                var y1 = y0 + yStride;
                var uvRow = (byte*)uvAddr + pair * uvStride;
                for (var x = 0; x < width; x += 2)
                {
                    var p00 = row0 + x * 4;
                    var p01 = p00 + 4;
                    var p10 = row1 + x * 4;
                    var p11 = p10 + 4;

                    y0[x] = Luma(p00);
                    y0[x + 1] = Luma(p01);
                    y1[x] = Luma(p10);
                    y1[x + 1] = Luma(p11);

                    // Average the 2×2 block for chroma.
                    var b = p00[0] + p01[0] + p10[0] + p11[0];
                    var g = p00[1] + p01[1] + p10[1] + p11[1];
                    var r = p00[2] + p01[2] + p10[2] + p11[2];
                    // Sums are ×4, so divide the ×256 coefficients' result by 1024.
                    uvRow[x] = Clamp(((-26 * r - 87 * g + 112 * b + 512) >> 10) + 128);
                    uvRow[x + 1] = Clamp(((112 * r - 102 * g - 10 * b + 512) >> 10) + 128);
                }
            }
        });
    }

    private static byte Luma(byte* p) => Clamp(((47 * p[2] + 157 * p[1] + 16 * p[0] + 128) >> 8) + 16);

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
