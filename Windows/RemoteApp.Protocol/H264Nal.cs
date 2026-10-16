// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace RemoteApp.Protocol;

/// <summary>
/// H.264 NAL unit helpers for moving between the wire's AVCC framing
/// (length-prefixed NAL units, PROTOCOL.md §7) and the Annex-B byte stream
/// (start-code delimited) that Media Foundation reads and writes.
/// </summary>
public static class H264Nal
{
    public const byte NalTypeNonIdr = 1;
    public const byte NalTypeIdr = 5;
    public const byte NalTypeSei = 6;
    public const byte NalTypeSps = 7;
    public const byte NalTypePps = 8;
    public const byte NalTypeAud = 9;

    private static readonly byte[] StartCode = [0, 0, 0, 1];

    public static byte NalType(ReadOnlySpan<byte> nal) => (byte)(nal.Length > 0 ? nal[0] & 0x1F : 0);

    /// <summary>Splits an AVCC access unit into its NAL units.</summary>
    public static List<ReadOnlyMemory<byte>> SplitAvcc(ReadOnlyMemory<byte> avcc, int lengthSize = 4)
    {
        var nals = new List<ReadOnlyMemory<byte>>();
        var offset = 0;
        var span = avcc.Span;
        while (offset + lengthSize <= span.Length)
        {
            var length = lengthSize switch
            {
                4 => (int)BinaryPrimitives.ReadUInt32BigEndian(span[offset..]),
                2 => BinaryPrimitives.ReadUInt16BigEndian(span[offset..]),
                1 => span[offset],
                _ => throw new ArgumentOutOfRangeException(nameof(lengthSize)),
            };
            offset += lengthSize;
            if (length < 0 || offset + length > span.Length) break;
            nals.Add(avcc.Slice(offset, length));
            offset += length;
        }
        return nals;
    }

    /// <summary>
    /// Converts an AVCC access unit to Annex-B, optionally prepending parameter
    /// sets (so a keyframe is self-contained for the decoder).
    /// </summary>
    public static byte[] AvccToAnnexB(ReadOnlyMemory<byte> avcc, int lengthSize = 4, IReadOnlyList<byte[]>? parameterSets = null)
    {
        var nals = SplitAvcc(avcc, lengthSize);
        var size = 0;
        if (parameterSets != null) foreach (var ps in parameterSets) size += 4 + ps.Length;
        foreach (var nal in nals) size += 4 + nal.Length;
        var output = new byte[size];
        var offset = 0;
        if (parameterSets != null)
        {
            foreach (var ps in parameterSets)
            {
                StartCode.CopyTo(output, offset); offset += 4;
                ps.CopyTo(output, offset); offset += ps.Length;
            }
        }
        foreach (var nal in nals)
        {
            StartCode.CopyTo(output, offset); offset += 4;
            nal.Span.CopyTo(output.AsSpan(offset)); offset += nal.Length;
        }
        return output;
    }

    /// <summary>Annex-B framing of a list of parameter sets (SPS, PPS…).</summary>
    public static byte[] AnnexBParameterSets(IReadOnlyList<byte[]> parameterSets)
    {
        var size = 0;
        foreach (var ps in parameterSets) size += 4 + ps.Length;
        var output = new byte[size];
        var offset = 0;
        foreach (var ps in parameterSets)
        {
            StartCode.CopyTo(output, offset); offset += 4;
            ps.CopyTo(output, offset); offset += ps.Length;
        }
        return output;
    }

    /// <summary>Splits an Annex-B byte stream into NAL units (start codes of 3 or 4 bytes).</summary>
    public static List<ReadOnlyMemory<byte>> SplitAnnexB(ReadOnlyMemory<byte> stream)
    {
        var nals = new List<ReadOnlyMemory<byte>>();
        var span = stream.Span;
        var i = 0;
        var nalStart = -1;
        while (i + 2 < span.Length)
        {
            if (span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 1)
            {
                if (nalStart >= 0)
                {
                    // Trailing zero of a 4-byte start code belongs to the code, not the NAL.
                    var end = i;
                    if (end > nalStart && span[end - 1] == 0) end--;
                    if (end > nalStart) nals.Add(stream[nalStart..end]);
                }
                i += 3;
                nalStart = i;
            }
            else
            {
                i++;
            }
        }
        if (nalStart >= 0 && nalStart < span.Length) nals.Add(stream[nalStart..]);
        return nals;
    }

    /// <summary>Result of converting an encoder's Annex-B output to the wire format.</summary>
    public sealed record AvccAccessUnit(byte[] Data, bool IsKeyframe, List<byte[]> ParameterSets);

    /// <summary>
    /// Converts an Annex-B access unit (as Media Foundation encoders emit) to
    /// AVCC with 4-byte lengths. SPS/PPS NAL units are extracted into
    /// <see cref="AvccAccessUnit.ParameterSets"/> and removed from the data
    /// (they travel in <see cref="VideoConfig"/>); AUD NALs are dropped.
    /// </summary>
    public static AvccAccessUnit AnnexBToAvcc(ReadOnlyMemory<byte> annexB)
    {
        var nals = SplitAnnexB(annexB);
        var parameterSets = new List<byte[]>();
        var isKeyframe = false;
        var size = 0;
        var kept = new List<ReadOnlyMemory<byte>>();
        foreach (var nal in nals)
        {
            var type = NalType(nal.Span);
            switch (type)
            {
                case NalTypeSps:
                case NalTypePps:
                    parameterSets.Add(nal.ToArray());
                    continue;
                case NalTypeAud:
                    continue;
                case NalTypeIdr:
                    isKeyframe = true;
                    break;
            }
            kept.Add(nal);
            size += 4 + nal.Length;
        }
        var output = new byte[size];
        var offset = 0;
        foreach (var nal in kept)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(offset), (uint)nal.Length);
            offset += 4;
            nal.Span.CopyTo(output.AsSpan(offset));
            offset += nal.Length;
        }
        // SPS first, then PPS, which is the order VideoConfig.parameterSets expects.
        parameterSets.Sort((a, b) => NalType(a).CompareTo(NalType(b)));
        return new AvccAccessUnit(output, isKeyframe, parameterSets);
    }

    public static bool SameParameterSets(IReadOnlyList<byte[]> a, IReadOnlyList<byte[]> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!a[i].AsSpan().SequenceEqual(b[i])) return false;
        return true;
    }
}
