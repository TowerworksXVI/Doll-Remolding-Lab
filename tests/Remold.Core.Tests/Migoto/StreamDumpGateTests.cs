using System;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Migoto;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// <see cref="StreamDump"/>'s recoverable-skin gate: palette recovery consumes the full
/// float4-weight/uint4-index skin stream, so a mesh without it must be refused LOUDLY at dump time — the
/// raw stream slices would otherwise be misread downstream as 32-byte skin records.
/// </summary>
public class StreamDumpGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-dumpgate-" + Guid.NewGuid().ToString("N"));

    public StreamDumpGateTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly float[] TriPositions = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] TriIndices = { 0, 1, 2 };

    [Fact]
    public void A_mesh_without_the_full_skin_stream_is_named_unrecoverable()
    {
        string bundle = Path.Combine(_root, "rigid.bundle");
        SyntheticBundle.BuildOneMesh(bundle, "rigid", TriPositions, TriIndices);
        var field = new BundleReader().GetMeshField(File.ReadAllBytes(bundle), "rigid");

        Assert.NotNull(field);
        var reason = StreamDump.UnrecoverableSkinReason(field!);
        Assert.NotNull(reason);
        Assert.Contains("skin stream", reason);
    }

    [Fact]
    public void Dump_refuses_a_mesh_without_the_full_skin_stream()
    {
        string bundle = Path.Combine(_root, "rigid2.bundle");
        SyntheticBundle.BuildOneMesh(bundle, "rigid2", TriPositions, TriIndices);

        var e = Assert.Throws<InvalidDataException>(() =>
            StreamDump.Dump(File.ReadAllBytes(bundle), "rigid2", Path.Combine(_root, "out")));
        Assert.Contains("palette recovery", e.Message);
    }

    [Fact]
    public void Weighted_bones_are_the_ones_the_skin_stream_rides()
    {
        // Three vertices over two weighted bones plus two the table lists and no vertex rides. Callers
        // asking which bones a mesh POSES must get the two, or a bone that moves nothing looks like one
        // the palette owes a row.
        string bundle = Path.Combine(_root, "skin.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(bundle, "skin", TriPositions, TriIndices,
            new uint[] { 0x11, 0x22 }, tabledOnlyBones: new uint[] { 0x33, 0x44 });
        var field = new BundleReader().GetMeshField(File.ReadAllBytes(bundle), "skin");

        var weighted = StreamDump.WeightedBoneHashes(field!);

        Assert.Equal(new uint[] { 0x11, 0x22 }, weighted.OrderBy(h => h).ToArray());
    }

    [Fact]
    public void Weighted_bones_refuse_a_mesh_the_skin_rule_already_refuses()
    {
        string bundle = Path.Combine(_root, "rigid3.bundle");
        SyntheticBundle.BuildOneMesh(bundle, "rigid3", TriPositions, TriIndices);
        var field = new BundleReader().GetMeshField(File.ReadAllBytes(bundle), "rigid3");

        var e = Assert.Throws<InvalidDataException>(() => StreamDump.WeightedBoneHashes(field!));
        Assert.Contains("skin stream", e.Message);
    }
}
