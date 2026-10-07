using System.Collections.Generic;
using System.Numerics;
using Remold.Core.Mesh;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The in-memory channel accessors on <c>UnityMesh</c> (Has / AsVector2 / AsVector3 / AsVector4).
/// The byte-level <c>Decode</c> path needs a real AssetsTools type-tree field, so it lives in the
/// opt-in <c>LiveRoundtripTests</c>; here we build channel arrays by hand.
/// </summary>
public class UnityMeshTests
{
    private static UnityMesh TwoVertMesh() => new()
    {
        Name = "synthetic",
        VertexCount = 2,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = new float[] { 1, 2, 3, 4, 5, 6 },
            ["TexCoord0"] = new float[] { 0.1f, 0.2f, 0.3f, 0.4f },
            ["Tangent"] = new float[] { 1, 0, 0, 1, 0, 1, 0, 1 },
        },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["TexCoord0"] = 2, ["Tangent"] = 4 },
    };

    [Fact]
    public void Has_ReflectsPresentChannels()
    {
        var mesh = TwoVertMesh();
        Assert.True(mesh.Has("Vertex"));
        Assert.False(mesh.Has("Color"));
    }

    [Fact]
    public void AsVector3_ReinterpretsThreeFloatsPerVertex()
    {
        var v = TwoVertMesh().AsVector3("Vertex");
        Assert.Equal(2, v.Count);
        Assert.Equal(new Vector3(1, 2, 3), v[0]);
        Assert.Equal(new Vector3(4, 5, 6), v[1]);
    }

    [Fact]
    public void AsVector2_ReinterpretsTwoFloatsPerVertex()
    {
        var uv = TwoVertMesh().AsVector2("TexCoord0");
        Assert.Equal(new Vector2(0.1f, 0.2f), uv[0]);
        Assert.Equal(new Vector2(0.3f, 0.4f), uv[1]);
    }

    [Fact]
    public void AsVector4_ReinterpretsFourFloatsPerVertex()
    {
        var t = TwoVertMesh().AsVector4("Tangent");
        Assert.Equal(new Vector4(1, 0, 0, 1), t[0]);
        Assert.Equal(new Vector4(0, 1, 0, 1), t[1]);
    }

    // ---- one stream between two layouts ------------------------------------------------------------

    /// <summary>Position in stream 0; color float4 + one UV pair in stream 1 at the given format.</summary>
    private static UnityMesh.ChannelDef[] Layout(int uvFormat, bool secondUv = false)
    {
        var t = new UnityMesh.ChannelDef[14];
        t[0] = new(0, 0, 0, 3);
        t[3] = new(1, 0, 0, 4);
        t[4] = new(1, 16, uvFormat, 2);
        if (secondUv) t[5] = new(1, uvFormat == 0 ? 24 : 20, uvFormat, 2);
        return t;
    }

    private static byte[] HalfUvStream()
    {
        var s = new byte[2 * 20];
        for (int v = 0; v < 2; v++)
        {
            for (int c = 0; c < 4; c++) System.BitConverter.GetBytes(v + c * 0.25f).CopyTo(s, v * 20 + c * 4);
            System.BitConverter.GetBytes((System.Half)(0.25f + v)).CopyTo(s, v * 20 + 16);
            System.BitConverter.GetBytes((System.Half)(0.75f - v)).CopyTo(s, v * 20 + 18);
        }
        return s;
    }

    [Fact]
    public void SameStreamLayout_compares_only_the_named_stream()
    {
        var half = Layout(1);
        var moved = Layout(1);
        moved[0] = new(0, 4, 0, 3);   // stream 0 differs, stream 1 does not

        Assert.True(UnityMesh.SameStreamLayout(half, moved, 1));
        Assert.False(UnityMesh.SameStreamLayout(half, moved, 0));
        Assert.False(UnityMesh.SameStreamLayout(half, Layout(0), 1));
        Assert.False(UnityMesh.SameStreamLayout(half, Layout(1, secondUv: true), 1));
    }

    [Fact]
    public void TranscodeStream_widens_half_uvs_and_keeps_the_other_channels()
    {
        var source = HalfUvStream();

        var wide = UnityMesh.TranscodeStream(source, 2, Layout(1), Layout(0), 1);

        Assert.Equal(2 * 24, wide.Length);
        for (int v = 0; v < 2; v++)
        {
            Assert.Equal(source[(v * 20)..(v * 20 + 16)], wide[(v * 24)..(v * 24 + 16)]);
            Assert.Equal(0.25f + v, System.BitConverter.ToSingle(wide, v * 24 + 16));
            Assert.Equal(0.75f - v, System.BitConverter.ToSingle(wide, v * 24 + 20));
        }
        // and back: these values are exact in half precision
        Assert.Equal(source, UnityMesh.TranscodeStream(wide, 2, Layout(0), Layout(1), 1));
    }

    [Fact]
    public void TranscodeStream_fills_a_uv_set_the_source_lacks_from_its_first_one()
    {
        // a draw whose mesh lacks a UV set is fed its first one, so that is what the copy states
        var source = HalfUvStream();

        var wide = UnityMesh.TranscodeStream(source, 2, Layout(1), Layout(1, secondUv: true), 1, out var filled);

        Assert.Equal(new[] { "TexCoord1" }, filled);
        Assert.Equal(2 * 24, wide.Length);
        for (int v = 0; v < 2; v++)
        {
            Assert.Equal(source[(v * 20)..(v * 20 + 20)], wide[(v * 24)..(v * 24 + 20)]);
            Assert.Equal(source[(v * 20 + 16)..(v * 20 + 20)], wide[(v * 24 + 20)..(v * 24 + 24)]);
        }
    }

    [Fact]
    public void TranscodeStream_refuses_a_non_uv_channel_the_source_does_not_carry()
    {
        var noColor = Layout(1);
        noColor[3] = default;
        noColor[4] = new(1, 0, 1, 2);
        var e = Assert.Throws<System.FormatException>(() =>
            UnityMesh.TranscodeStream(new byte[2 * 4], 2, noColor, Layout(1), 1));
        Assert.Contains("Color", e.Message);
    }

    [Fact]
    public void TranscodeStream_refuses_a_target_that_stores_nothing_in_the_stream()
    {
        var empty = new UnityMesh.ChannelDef[14];
        empty[0] = new(0, 0, 0, 3);
        Assert.Throws<System.FormatException>(() => UnityMesh.TranscodeStream(HalfUvStream(), 2, Layout(1), empty, 1));
    }

    [Fact]
    public void TranscodeStream_copies_a_channel_stored_the_same_way_byte_for_byte()
    {
        // SNorm8 -128 decodes to -1 and would re-encode as -127: the stored bytes are the answer
        var from = new UnityMesh.ChannelDef[14];
        from[3] = new(1, 0, 3, 4);
        from[4] = new(1, 4, 1, 2);
        var to = new UnityMesh.ChannelDef[14];
        to[3] = new(1, 0, 3, 4);
        to[4] = new(1, 4, 0, 2);
        var source = new byte[] { 0x80, 0x81, 0x7f, 0x00, 0, 0, 0, 0 };

        var wide = UnityMesh.TranscodeStream(source, 1, from, to, 1);

        Assert.Equal(12, wide.Length);
        Assert.Equal(new byte[] { 0x80, 0x81, 0x7f, 0x00 }, wide[..4]);
    }

    [Fact]
    public void TranscodeStream_refuses_bytes_that_are_not_the_stated_layouts_size()
    {
        Assert.Throws<System.FormatException>(() =>
            UnityMesh.TranscodeStream(new byte[2 * 24], 2, Layout(1), Layout(0), 1));
    }
}
