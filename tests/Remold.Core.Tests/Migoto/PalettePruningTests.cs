using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

public sealed class PalettePruningTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "remold-palette-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    static void UseOnly(string donorDir, uint row)
    {
        var skin = File.ReadAllBytes(Path.Combine(donorDir, "stream2.buf"));
        for (int o = 0; o + 32 <= skin.Length; o += 32)
            BitConverter.GetBytes(row).CopyTo(skin, o + 16);
        File.WriteAllBytes(Path.Combine(donorDir, "stream2.buf"), skin);
    }

    static void SetLane(string donorDir, int lane, float weight, uint row)
    {
        var skin = File.ReadAllBytes(Path.Combine(donorDir, "stream2.buf"));
        for (int o = 0; o + 32 <= skin.Length; o += 32)
        {
            BitConverter.GetBytes(weight).CopyTo(skin, o + lane * sizeof(float));
            BitConverter.GetBytes(row).CopyTo(skin, o + 16 + lane * sizeof(uint));
        }
        File.WriteAllBytes(Path.Combine(donorDir, "stream2.buf"), skin);
    }

    static uint[] Map(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Enumerable.Range(0, bytes.Length / sizeof(uint))
            .Select(i => BitConverter.ToUInt32(bytes, i * sizeof(uint))).ToArray();
    }

    [Fact]
    public void Shared_operator_unions_rows_used_by_every_shipped_pipeline()
    {
        string alpha = Path.Combine(_root, "alpha");
        string donorA = Path.Combine(_root, "donor-a");
        string donorB = Path.Combine(_root, "donor-b");
        string output = Path.Combine(_root, "out");
        SyntheticPool.WritePartDump(alpha, seed: 7, verts: 24, new uint[] { 0x101, 0x102 });
        SyntheticPool.WriteDonor(donorA, verts: 6, unionBones: 2, submeshes: 1);
        SyntheticPool.WriteDonor(donorB, verts: 6, unionBones: 2, submeshes: 1);
        UseOnly(donorA, 0);
        UseOnly(donorB, 1);

        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = output,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "a", Parts = new[] { new PoolPart("alpha", alpha) }, DonorDir = donorA,
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["alpha"] = "aaaa" },
                },
                new ReplacePipeline
                {
                    Suffix = "b", Parts = new[] { new PoolPart("alpha", alpha) }, DonorDir = donorB,
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["alpha"] = "aaaa" },
                },
            },
        });

        // The shared operator retains the union of both contexts. Each pipeline map has both compact
        // operator rows but scatters only the row its own compact palette contains.
        Assert.Equal(new uint[] { 0, PoolMath.Sentinel }, Map(Path.Combine(output, "alpha_map_a.buf")));
        Assert.Equal(new uint[] { PoolMath.Sentinel, 0 }, Map(Path.Combine(output, "alpha_map_b.buf")));

        using var unionA = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "union_a.json")));
        using var unionB = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "union_b.json")));
        Assert.Equal("257", Assert.Single(unionA.RootElement.GetProperty("order").EnumerateArray()).GetString());
        Assert.Equal("258", Assert.Single(unionB.RootElement.GetProperty("order").EnumerateArray()).GetString());
        var (_, skinA) = PoolMath.ParseSkin(File.ReadAllBytes(Path.Combine(output, "combined_skin_a.buf")));
        var (_, skinB) = PoolMath.ParseSkin(File.ReadAllBytes(Path.Combine(output, "combined_skin_b.buf")));
        Assert.All(skinA.Cast<int>(), index => Assert.Equal(0, index));
        Assert.All(skinB.Cast<int>(), index => Assert.Equal(0, index));
        Assert.Contains("palette: a/alpha 1/2 rows used", result.Diagnostics);
        Assert.Contains("palette: b/alpha 1/2 rows used", result.Diagnostics);
    }

    [Fact]
    public void A_zero_weight_uint_sentinel_lane_builds_and_rewrites_to_compact_slot_zero()
    {
        string alpha = Path.Combine(_root, "alpha-sentinel");
        string donor = Path.Combine(_root, "donor-sentinel");
        string output = Path.Combine(_root, "out-sentinel");
        SyntheticPool.WritePartDump(alpha, seed: 7, verts: 24, new uint[] { 0x101, 0x102 });
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2, submeshes: 1);
        UseOnly(donor, 0);
        SetLane(donor, lane: 1, weight: 0, row: uint.MaxValue);

        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = output,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap", Parts = new[] { new PoolPart("alpha", alpha) }, DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa" },
                },
            },
        });

        var (_, indices) = PoolMath.ParseSkin(File.ReadAllBytes(Path.Combine(output, "combined_skin_swap.buf")));
        for (int v = 0; v < indices.GetLength(0); v++) Assert.Equal(0, indices[v, 1]);
    }

    [Fact]
    public void A_positive_weight_high_bit_index_gets_the_named_contract_error_and_remedy()
    {
        string alpha = Path.Combine(_root, "alpha-high-bit");
        string donor = Path.Combine(_root, "donor-high-bit");
        SyntheticPool.WritePartDump(alpha, seed: 7, verts: 24, new uint[] { 0x101, 0x102 });
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2, submeshes: 1);
        SetLane(donor, lane: 0, weight: 1, row: 0x80000000);

        var ex = Assert.Throws<InvalidDataException>(() => new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = Path.Combine(_root, "out-high-bit"),
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap", Parts = new[] { new PoolPart("alpha", alpha) }, DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa" },
                },
            },
        }));

        Assert.Contains("swap: positive donor weight references palette row 2147483648", ex.Message);
        Assert.EndsWith("Recompile the donor against THIS union.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_only_donor_whose_lod0_supplies_no_live_row_refuses_an_empty_compact_union()
    {
        const uint groupBone = 0x301;
        string alpha = Path.Combine(_root, "alpha-empty");
        string member = Path.Combine(_root, "member-empty");
        string donor = Path.Combine(_root, "donor-empty");
        SyntheticPool.WritePartDump(alpha, seed: 7, verts: 24, new uint[] { 0x101 });
        SyntheticPool.WritePartDump(member, seed: 9, verts: 24, new uint[] { 0x201, groupBone },
            weightedBones: 1);
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2, submeshes: 1);
        UseOnly(donor, 1); // full union row 0 is unused; row 1 is the coverage-group continuation

        var ex = Assert.Throws<InvalidDataException>(() => new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = Path.Combine(_root, "out-empty"),
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap", Parts = new[] { new PoolPart("alpha", alpha) }, DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa" },
                    Groups = new[]
                    {
                        new PoolGroup(7, new[] { groupBone }, new[]
                        {
                            new PoolGroupMember(1, PresenceContext.Always, "member", "member")
                            {
                                Meshes = new[] { new PoolGroupMesh("member", "", member, "bbbb") },
                            },
                        }),
                    },
                },
            },
        }));

        Assert.Contains("swap: the shipped conversion pipeline has an empty compact union", ex.Message);
        Assert.Contains("constant-buffer resource", ex.Message);
    }

    [Fact]
    public void Classification_only_does_not_publish_a_second_full_operator_cache_entry()
    {
        string alpha = Path.Combine(_root, "alpha-cache");
        string donor = Path.Combine(_root, "donor-cache");
        string cache = Path.Combine(_root, "operator-cache");
        SyntheticPool.WritePartDump(alpha, seed: 7, verts: 24, new uint[] { 0x101, 0x102 });
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2, submeshes: 1);
        UseOnly(donor, 0);

        new MigotoEmitter { OperatorCacheDir = cache }.Build(new PoolBuildRequest
        {
            OutDir = Path.Combine(_root, "out-cache"),
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", alpha, "catalog|bundle|mesh") },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa" },
                },
            },
        });

        Assert.Single(Directory.GetFiles(cache, "*.op"));
    }
}
