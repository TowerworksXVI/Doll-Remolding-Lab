using System;
using System.Collections.Generic;
using Remold.Core.Mesh;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The pure vertex/index byte codec on hand-built arrays — no AssetsTools, no bundle. Per-channel
/// <c>VertexFormat</c> decoding, multi-stream strides plus the 16-byte inter-stream padding, within-stream
/// offsets, and baseVertex folding.
/// </summary>
public class MeshCodecTests
{
    private static readonly UnityMesh.SubMeshDef[] NoSubs = Array.Empty<UnityMesh.SubMeshDef>();

    private static void PutFloats(byte[] buf, int at, params float[] vals)
    {
        foreach (var f in vals) { BitConverter.GetBytes(f).CopyTo(buf, at); at += 4; }
    }

    private static byte[] U16(params int[] vals)
    {
        var b = new byte[vals.Length * 2];
        for (int i = 0; i < vals.Length; i++) BitConverter.GetBytes((ushort)vals[i]).CopyTo(b, i * 2);
        return b;
    }

    private static byte[] U32(params int[] vals)
    {
        var b = new byte[vals.Length * 4];
        for (int i = 0; i < vals.Length; i++) BitConverter.GetBytes((uint)vals[i]).CopyTo(b, i * 4);
        return b;
    }

    // A positional channel list (index → semantic). dim0 entries are unused slots.
    private static List<UnityMesh.ChannelDef> Slots(params UnityMesh.ChannelDef[] defs) => new(defs);
    private static UnityMesh.ChannelDef Empty => new(0, 0, 0, 0);

    [Fact]
    public void DecodeRaw_SingleStream_DecodesVertexPositions()
    {
        var buf = new byte[24];
        PutFloats(buf, 0, 1, 2, 3, 4, 5, 6);   // 2 verts, dim3
        var channels = Slots(new UnityMesh.ChannelDef(Stream: 0, Offset: 0, Format: 0, Dimension: 3));

        var mesh = UnityMesh.DecodeRaw("m", 2, channels, buf, 0, Array.Empty<byte>(), NoSubs);

        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6 }, mesh.Channels["Vertex"]);
        Assert.Equal(3, mesh.Dims["Vertex"]);
    }

    [Fact]
    public void DecodeRaw_Throws_WhenStreamOverrunsTheVertexBlob()
    {
        // The header claims 36 bytes of verts but the blob is 24: the stride guard must throw a clear
        // InvalidDataException, not an opaque IndexOutOfRange in the read loop.
        var buf = new byte[24];
        var channels = Slots(new UnityMesh.ChannelDef(Stream: 0, Offset: 0, Format: 0, Dimension: 3));

        Assert.Throws<System.IO.InvalidDataException>(() =>
            UnityMesh.DecodeRaw("m", 3, channels, buf, 0, Array.Empty<byte>(), NoSubs));
    }

    public static IEnumerable<object[]> FormatCases()
    {
        yield return new object[] { 0, BitConverter.GetBytes(1.5f), 1.5f };                                   // Float32
        yield return new object[] { 1, BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)0.5f)), 0.5f }; // Float16
        yield return new object[] { 2, new byte[] { 255 }, 1.0f };                                            // UNorm8
        yield return new object[] { 3, new byte[] { unchecked((byte)(sbyte)127) }, 1.0f };                    // SNorm8
        yield return new object[] { 4, U16(65535), 1.0f };                                                    // UNorm16
        yield return new object[] { 5, U16(32767), 1.0f };                                                    // SNorm16
        yield return new object[] { 6, new byte[] { 200 }, 200f };                                            // UInt8
        yield return new object[] { 7, new byte[] { unchecked((byte)(sbyte)-5) }, -5f };                      // SInt8
        yield return new object[] { 8, U16(4000), 4000f };                                                    // UInt16
        yield return new object[] { 9, U16(unchecked((ushort)(short)-1234)), -1234f };                        // SInt16
        yield return new object[] { 10, U32(100000), 100000f };                                               // UInt32
        yield return new object[] { 11, U32(-100000), -100000f };                                             // SInt32
    }

    [Theory]
    [MemberData(nameof(FormatCases))]
    public void DecodeRaw_DecodesEveryVertexFormat(int format, byte[] raw, float expected)
    {
        var channels = Slots(new UnityMesh.ChannelDef(0, 0, format, 1));
        var mesh = UnityMesh.DecodeRaw("m", 1, channels, raw, 0, Array.Empty<byte>(), NoSubs);
        Assert.Equal(expected, mesh.Channels["Vertex"][0], precision: 3);
    }

    [Fact]
    public void DecodeRaw_NormalizedFormats_ScaleAndClamp()
    {
        // UNorm8 51/255 ≈ 0.2; SNorm8 -128 clamps to -1 (not -128/127).
        Assert.Equal(0.2f, UnityMesh.DecodeRaw("m", 1, Slots(new UnityMesh.ChannelDef(0, 0, 2, 1)),
            new byte[] { 51 }, 0, Array.Empty<byte>(), NoSubs).Channels["Vertex"][0], precision: 3);
        Assert.Equal(-1.0f, UnityMesh.DecodeRaw("m", 1, Slots(new UnityMesh.ChannelDef(0, 0, 3, 1)),
            new byte[] { unchecked((byte)(sbyte)-128) }, 0, Array.Empty<byte>(), NoSubs).Channels["Vertex"][0], precision: 3);
    }

    [Fact]
    public void DecodeRaw_PadsIntermediateStreamsTo16Bytes()
    {
        // Stream 0 is 24 bytes PADDED to 32, so stream 1 starts at 32 and not 24 — the UVs prove the
        // 16-byte alignment is applied.
        var buf = new byte[48];
        PutFloats(buf, 0, 1, 2, 3, 4, 5, 6);            // Vertex (stream 0)
        PutFloats(buf, 32, 0.5f, 0.6f, 0.7f, 0.8f);     // TexCoord0 (stream 1, after the pad)

        var channels = Slots(
            new UnityMesh.ChannelDef(0, 0, 0, 3),   // 0 Vertex   → stream 0
            Empty, Empty, Empty,                    // 1..3 Normal/Tangent/Color unused
            new UnityMesh.ChannelDef(1, 0, 0, 2));  // 4 TexCoord0 → stream 1

        var mesh = UnityMesh.DecodeRaw("m", 2, channels, buf, 0, Array.Empty<byte>(), NoSubs);

        Assert.Equal(new float[] { 1, 2, 3, 4, 5, 6 }, mesh.Channels["Vertex"]);
        Assert.Equal(new float[] { 0.5f, 0.6f, 0.7f, 0.8f }, mesh.Channels["TexCoord0"]);
    }

    [Fact]
    public void DecodeRaw_HonoursWithinStreamOffsets()
    {
        // One stream holding Vertex (offset 0, dim3) then Color (offset 12, dim4).
        var buf = new byte[28];
        PutFloats(buf, 0, 1, 2, 3);                 // Vertex
        PutFloats(buf, 12, 0.1f, 0.2f, 0.3f, 0.4f); // Color at offset 12

        var channels = Slots(
            new UnityMesh.ChannelDef(0, 0, 0, 3),     // 0 Vertex
            Empty, Empty,                             // 1..2 unused
            new UnityMesh.ChannelDef(0, 12, 0, 4));   // 3 Color, offset 12 in same stream

        var mesh = UnityMesh.DecodeRaw("m", 1, channels, buf, 0, Array.Empty<byte>(), NoSubs);

        Assert.Equal(new float[] { 1, 2, 3 }, mesh.Channels["Vertex"]);
        Assert.Equal(new float[] { 0.1f, 0.2f, 0.3f, 0.4f }, mesh.Channels["Color"]);
    }

    [Fact]
    public void DecodeRaw_Submeshes_Uint16_FoldBaseVertexAndFirstByte()
    {
        var indices = U16(0, 1, 2, 0, 1, 2);
        var subs = new[]
        {
            new UnityMesh.SubMeshDef(FirstByte: 0, IndexCount: 3, BaseVertex: 0),
            new UnityMesh.SubMeshDef(FirstByte: 6, IndexCount: 3, BaseVertex: 100),
        };
        var mesh = UnityMesh.DecodeRaw("m", 0, Slots(), Array.Empty<byte>(), 0, indices, subs);

        Assert.Equal(2, mesh.Submeshes.Count);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.Submeshes[0]);
        Assert.Equal(new[] { 100, 101, 102 }, mesh.Submeshes[1]);   // baseVertex folded, firstByte applied
    }

    [Fact]
    public void DecodeRaw_Submeshes_Uint32()
    {
        var mesh = UnityMesh.DecodeRaw("m", 0, Slots(), Array.Empty<byte>(),
            indexFormat: 1, U32(5, 6, 7), new[] { new UnityMesh.SubMeshDef(0, 3, 0) });
        Assert.Equal(new[] { 5, 6, 7 }, mesh.Submeshes[0]);
    }

    [Fact]
    public void DecodeRaw_Throws_OnUnknownVertexFormat()
    {
        var channels = Slots(new UnityMesh.ChannelDef(0, 0, 12, 1));   // 12 is not a real format
        Assert.Throws<FormatException>(() =>
            UnityMesh.DecodeRaw("m", 1, channels, new byte[4], 0, Array.Empty<byte>(), NoSubs));
    }
}
