using System;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="SkinLayout"/>: the canonical float4/uint4 skin stream, and the widening that brings any
/// stored influence width (1–4) into it. Both one-influence spellings the corpus ships are here — weights
/// and indices stored x1, and indices alone with each weight implicitly 1 — because only their bytes tell
/// them apart and a reader keyed on the wrong one reads garbage silently; so are the two- and
/// three-influence pairs, whose stored split must land verbatim in the widened records.
/// </summary>
public class SkinLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-skinlayout-" + Guid.NewGuid().ToString("N"));

    public SkinLayoutTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly uint[] Bones = { 0x11u, 0x22u };
    private static float[] Cloud(int verts)
    {
        var pos = new float[verts * 3];
        for (int i = 0; i < pos.Length; i++) pos[i] = i * 0.25f - 1f;
        return pos;
    }
    private static int[] Tris(int verts) =>
        Enumerable.Range(0, verts).SelectMany(v => new[] { v, (v + 1) % verts, (v + 2) % verts }).ToArray();

    private AssetsTools.NET.AssetTypeValueField Mesh(string name, int skinWidth, bool implicitWeights = false,
        int verts = 6, bool extraSkinChannel = false)
    {
        string bundle = Path.Combine(_root, name + ".bundle");
        SyntheticBundle.BuildOneSkinnedMesh(bundle, name, Cloud(verts), Tris(verts), Bones,
            skinWidth: skinWidth, implicitWeights: implicitWeights, extraSkinChannel: extraSkinChannel);
        return new BundleReader().GetMeshField(File.ReadAllBytes(bundle), name)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_one_influence_skin_reads_as_the_canonical_four_wide_shape(bool implicitWeights)
    {
        var field = Mesh("narrow", skinWidth: 1, implicitWeights);

        Assert.False(SkinLayout.IsCanonical(field));
        Assert.True(SkinLayout.IsNarrow(field));
        Assert.True(SkinLayout.Recoverable(field));

        var s2 = SkinLayout.Canonical(field);
        Assert.Equal(6 * SkinLayout.CanonicalStride, s2.Length);
        for (int v = 0; v < 6; v++)
        {
            int o = v * SkinLayout.CanonicalStride;
            // the whole vertex on its one bone, the other three slots empty
            Assert.Equal(1f, BitConverter.ToSingle(s2, o));
            Assert.Equal((uint)(v % Bones.Length), BitConverter.ToUInt32(s2, o + 16));
            for (int k = 1; k < 4; k++)
            {
                Assert.Equal(0f, BitConverter.ToSingle(s2, o + k * 4));
                Assert.Equal(0u, BitConverter.ToUInt32(s2, o + 16 + k * 4));
            }
        }
    }

    [Fact]
    public void A_four_wide_skin_is_already_canonical_and_comes_back_verbatim()
    {
        var field = Mesh("wide", skinWidth: 4);
        Assert.True(SkinLayout.IsCanonical(field));
        Assert.False(SkinLayout.IsNarrow(field));

        var raw = MeshRaw.From(field);
        int ordinal = raw.StreamIds.IndexOf(SkinLayout.SkinStream);
        Assert.Equal(raw.StreamBytes(ordinal), SkinLayout.Canonical(field));
        Assert.False(SkinLayout.Widen(field));   // nothing to do
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_below_four_skin_widens_with_its_stored_split_intact(int skinWidth)
    {
        // The stored influences ARE the mesh's whole skin, so widening pads zero-weight slots and the
        // split lands verbatim in the canonical records.
        var field = Mesh($"pair{skinWidth}", skinWidth);

        Assert.False(SkinLayout.IsCanonical(field));
        Assert.False(SkinLayout.IsNarrow(field));   // narrow marks ONE influence, not "below four"
        Assert.True(SkinLayout.Recoverable(field));

        var split = SyntheticBundle.SkinSplit(skinWidth);
        var s2 = SkinLayout.Canonical(field);
        Assert.Equal(6 * SkinLayout.CanonicalStride, s2.Length);
        for (int v = 0; v < 6; v++)
        {
            int o = v * SkinLayout.CanonicalStride;
            for (int k = 0; k < 4; k++)
            {
                Assert.Equal(k < split.Length ? split[k] : 0f, BitConverter.ToSingle(s2, o + k * 4));
                Assert.Equal(k < split.Length ? (uint)((v + k) % Bones.Length) : 0u,
                    BitConverter.ToUInt32(s2, o + 16 + k * 4));
            }
        }

        Assert.True(SkinLayout.Widen(field));
        Assert.True(SkinLayout.IsCanonical(field));
        var after = MeshRaw.From(field);
        Assert.Equal(s2, after.StreamBytes(after.StreamIds.IndexOf(SkinLayout.SkinStream)));
        Assert.False(SkinLayout.Widen(field));   // idempotent
    }

    [Fact]
    public void The_bones_a_two_influence_mesh_poses_include_its_second_influence()
    {
        // One vertex, two bones: the second bone is reachable only through the second stored influence,
        // so a reader that widened by the first slot alone would report it unposed.
        var field = Mesh("second", skinWidth: 2, verts: 1);

        Assert.Equal(Bones, StreamDump.WeightedBoneHashes(field).OrderBy(h => h).ToArray());
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1)]
    public void A_third_channel_on_the_skin_stream_takes_the_layout_out_of_reach(int skinWidth)
    {
        // The stream is read at one stride and written whole, so those bytes are both records a reader
        // would take for weights or indices and bytes a widening would overwrite. Refusing beats returning
        // a buffer whose stride is not the one every consumer reads it at.
        var field = Mesh($"shared{skinWidth}", skinWidth, implicitWeights: skinWidth == 1,
            extraSkinChannel: true);

        Assert.False(SkinLayout.IsCanonical(field));
        Assert.False(SkinLayout.IsNarrow(field));
        Assert.False(SkinLayout.Recoverable(field));
        Assert.False(SkinLayout.Widen(field));
        Assert.Throws<InvalidDataException>(() => SkinLayout.Canonical(field));
        Assert.Equal("its skin weights are stored in a shape this app can't read",
            StreamDump.UnrecoverableSkinReason(field));
    }

    [Fact]
    public void The_pre_parsed_overload_refuses_a_raw_whose_skin_stride_is_not_the_canonical_one()
    {
        // The overload takes the caller's parse on trust. What it may never do is hand back bytes at some
        // other stride while declaring them canonical records.
        var canonical = Mesh("pair_wide", skinWidth: 4);
        var narrow = Mesh("pair_narrow", skinWidth: 1);

        Assert.Equal(SkinLayout.Canonical(canonical), SkinLayout.Canonical(canonical, MeshRaw.From(canonical)));
        var e = Assert.Throws<InvalidDataException>(
            () => SkinLayout.Canonical(canonical, MeshRaw.From(narrow)));
        Assert.Contains("8 bytes per vertex", e.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Widening_rewrites_the_skin_stream_and_leaves_every_other_stream_alone(bool implicitWeights)
    {
        var field = Mesh("widen", skinWidth: 1, implicitWeights);
        var before = MeshRaw.From(field);
        var s0 = before.StreamBytes(before.StreamIds.IndexOf(0));
        var s1 = before.StreamBytes(before.StreamIds.IndexOf(1));
        var expected = SkinLayout.Canonical(field);

        Assert.True(SkinLayout.Widen(field));

        Assert.True(SkinLayout.IsCanonical(field));
        var after = MeshRaw.From(field);
        Assert.Equal(before.VertexCount, after.VertexCount);
        Assert.Equal(s0, after.StreamBytes(after.StreamIds.IndexOf(0)));
        Assert.Equal(s1, after.StreamBytes(after.StreamIds.IndexOf(1)));
        Assert.Equal(SkinLayout.CanonicalStride, after.Stride(after.StreamIds.IndexOf(SkinLayout.SkinStream)));
        Assert.Equal(expected, after.StreamBytes(after.StreamIds.IndexOf(SkinLayout.SkinStream)));
        Assert.False(SkinLayout.Widen(field));   // idempotent
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_dump_writes_a_one_influence_mesh_at_the_canonical_stride(bool implicitWeights)
    {
        // The pooled machinery reads every dumped skin stream as 32-byte records, so a narrow mesh has to
        // reach it widened or its rows would be misread against the positions.
        string bundle = Path.Combine(_root, "dump.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(bundle, "dump", Cloud(6), Tris(6), Bones,
            skinWidth: 1, implicitWeights: implicitWeights);
        string outDir = Path.Combine(_root, "out" + implicitWeights);

        var result = StreamDump.Dump(File.ReadAllBytes(bundle), "dump", outDir);

        Assert.Equal(6 * SkinLayout.CanonicalStride,
            new FileInfo(Path.Combine(outDir, "stream2.buf")).Length);
        Assert.Contains("{ \"stream\": 2, \"stride\": 32 }", File.ReadAllText(Path.Combine(outDir, "meta.json")));
        Assert.Equal(SkinLayout.CanonicalStride, result.Streams.Single(s => s.Stream == 2).Stride);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_bones_a_one_influence_mesh_poses_are_read_from_the_widened_stream(bool implicitWeights)
    {
        var field = Mesh("weighted", skinWidth: 1, implicitWeights);

        Assert.Equal(Bones, StreamDump.WeightedBoneHashes(field).OrderBy(h => h).ToArray());
    }
}
