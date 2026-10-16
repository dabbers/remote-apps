// Copyright (c) 2026 dabbers (https://github.com/dabbers)
// SPDX-License-Identifier: MIT

using RemoteApp.Protocol;
using Xunit;

namespace RemoteApp.Protocol.Tests;

public class H264NalTests
{
    private static readonly byte[] Sps = [0x67, 0x42, 0x00, 0x1E];
    private static readonly byte[] Pps = [0x68, 0xCE, 0x3C, 0x80];
    private static readonly byte[] Idr = [0x65, 0x88, 0x84, 0x00];
    private static readonly byte[] Slice = [0x41, 0x9A, 0x02];

    [Fact]
    public void AvccToAnnexBPrependsParameterSets()
    {
        byte[] avcc = [0, 0, 0, 4, .. Idr];
        var annexB = H264Nal.AvccToAnnexB(avcc, 4, [Sps, Pps]);
        byte[] expected = [0, 0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps, 0, 0, 0, 1, .. Idr];
        Assert.Equal(expected, annexB);
    }

    [Fact]
    public void AnnexBToAvccExtractsParameterSetsAndKeyframe()
    {
        // Encoders emit AUD + SPS + PPS + IDR on a keyframe, with mixed 3/4-byte start codes.
        byte[] annexB = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, .. Sps, 0, 0, 1, .. Pps, 0, 0, 0, 1, .. Idr];
        var unit = H264Nal.AnnexBToAvcc(annexB);
        Assert.True(unit.IsKeyframe);
        Assert.Equal(2, unit.ParameterSets.Count);
        Assert.Equal(Sps, unit.ParameterSets[0]);
        Assert.Equal(Pps, unit.ParameterSets[1]);
        byte[] expectedIdr = [0, 0, 0, 4, .. Idr];
        Assert.Equal(expectedIdr, unit.Data);
    }

    [Fact]
    public void NonKeyframeKeepsSlices()
    {
        byte[] annexB = [0, 0, 0, 1, .. Slice];
        var unit = H264Nal.AnnexBToAvcc(annexB);
        Assert.False(unit.IsKeyframe);
        Assert.Empty(unit.ParameterSets);
        byte[] expectedSlice = [0, 0, 0, 3, .. Slice];
        Assert.Equal(expectedSlice, unit.Data);
    }

    [Fact]
    public void RoundTripThroughBothDirections()
    {
        byte[] annexB = [0, 0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps, 0, 0, 0, 1, .. Idr, 0, 0, 0, 1, .. Slice];
        var unit = H264Nal.AnnexBToAvcc(annexB);
        var back = H264Nal.AvccToAnnexB(unit.Data, 4, unit.ParameterSets);
        Assert.Equal(annexB, back);
    }
}
