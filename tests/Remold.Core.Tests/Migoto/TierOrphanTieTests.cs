using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The tier tie: a donor-weighted union row whose part's LOD tier writes nothing for it (the tier's rig lacks
/// the bone) is filled, in that tier's chain, with a copy of a row the tier does write — the co-riding bone
/// by the part's lod0 skin, else the nearest by support centroid. Without it the row stands at the last
/// lod0-frame recovery or the identity seed, and the donor vertices on it stretch at that tier's draws.
/// </summary>
public class TierOrphanTieTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-tiertie-" + Guid.NewGuid().ToString("N"));

    public TierOrphanTieTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const uint A = 101, B = 102, C = 103;

    /// <summary>Generic (rank-4-support) positions, so no bone of the dump trips the weak-support gate.</summary>
    private static void GenericPositions(string dir, int verts)
    {
        var s0 = File.ReadAllBytes(Path.Combine(dir, "stream0.buf"));
        for (int v = 0; v < verts; v++)
        {
            BitConverter.GetBytes((v * 13 % 17) / 4f).CopyTo(s0, v * 40);
            BitConverter.GetBytes((v * 7 % 23) / 5f).CopyTo(s0, v * 40 + 4);
            BitConverter.GetBytes((v * 11 % 29) / 6f).CopyTo(s0, v * 40 + 8);
        }
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);
    }

    /// <summary>One part (alpha, the anchor) with the given lod0 bones, one lod1 tier with the given
    /// bones, and a donor weighted across every union row.</summary>
    private PoolBuildRequest Request(out string outDir, uint[] lod0Bones, uint[] tierBones)
    {
        string ad = Path.Combine(_root, "alpha");
        SyntheticPool.WritePartDump(ad, 1, 32, lod0Bones);
        GenericPositions(ad, 32);
        string td = Path.Combine(_root, "alpha_l1");
        SyntheticPool.WritePartDump(td, 3, 24, tierBones);
        GenericPositions(td, 24);
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 3 * lod0Bones.Length, unionBones: lod0Bones.Length, submeshes: 1);
        outDir = Path.Combine(_root, "out");
        return new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad) },
                    Anchor = "alpha",
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                    Tiers = new[] { new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002") },
                },
            },
        };
    }

    private static string Section(string ini, string header)
    {
        int at = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(at >= 0, $"missing {header}");
        return ini[at..ini.IndexOf("\n\n", at, StringComparison.Ordinal)];
    }

    [Fact]
    public void A_bone_the_tier_rig_lacks_copies_its_nearest_written_row_in_that_tier_chain()
    {
        // lod0 rigs A and B; the tier rigs A only; the donor weights both. Union order [A, B], so B is
        // row 1 and the only row the tier writes is A, row 0. The lod0 skin is rigid (one bone per
        // vertex), so no co-weight exists and the proximity fallback picks A.
        var result = new MigotoEmitter().Build(Request(out string outDir, new[] { A, B }, new[] { A }));

        string shader = File.ReadAllText(Path.Combine(outDir, "tiertie_lod1_swap.hlsl"));
        Assert.Contains("static const uint2 PAIR[1] = { uint2(1,0) };", shader);
        Assert.Contains("StructuredBuffer<float4> palIn  : register(t0);", shader);

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        Assert.Contains("[CustomShaderTierTie_lod1_swap]\ncs = tiertie_lod1_swap.hlsl\n"
                      + "cs-t0 = copy Resource_PaletteConv_swap\n"
                      + "cs-u1 = copy Resource_PaletteConv_swap\nDispatch = 1, 1, 1\n"
                      + "Resource_PaletteConv_swap = copy cs-u1\npost cs-u1 = null\n", ini);
        // the fill runs in the tier's chain after the witness convert and before the skin
        string tier = Section(ini, "[TextureOverride_Cap_alpha_lod1]");
        Assert.Contains("run = CustomShaderConvertW_swap\nrun = CustomShaderTierTie_lod1_swap\n"
                      + "run = CustomShaderSkin_swap\n", tier);
        // and never in the lod0 chain, where every row has its own recover
        string lod0 = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.DoesNotContain("CustomShaderTierTie", lod0);

        Assert.Contains(result.Diagnostics, d => d.Contains("alpha_lod1: bone 0x00000066 has no row at this tier")
            && d.Contains("nearest bone 0x00000065"));
        var warning = Assert.Single(result.Warnings, w => w.Contains("longer view distances"));
        Assert.Equal("'alpha' moves less naturally at longer view distances: its lower-detail mesh does not "
            + "use 1 bone the replacement mesh uses. The build log names the bones.", warning);
        Assert.DoesNotContain("0x", warning);
    }

    [Fact]
    public void A_co_weighted_orphan_copies_the_bone_it_shares_the_most_weight_with()
    {
        // lod0 rigs A, B, C; the tier rigs A and B. On the lod0 skin every C vertex is 0.6 C + 0.4 B,
        // so B (row 1) is C's co-rider, and A (row 0), the nearer centroid, must lose to it.
        var req = Request(out string outDir, new[] { A, B, C }, new[] { A, B });
        string ad = Path.Combine(_root, "alpha");
        var s2 = File.ReadAllBytes(Path.Combine(ad, "stream2.buf"));
        var s0 = File.ReadAllBytes(Path.Combine(ad, "stream0.buf"));
        for (int v = 0; v < 32; v++)
        {
            uint bone = BitConverter.ToUInt32(s2, v * 32 + 16);
            if (bone != 2) continue;
            BitConverter.GetBytes(0.6f).CopyTo(s2, v * 32);
            BitConverter.GetBytes(0.4f).CopyTo(s2, v * 32 + 4);
            BitConverter.GetBytes(1u).CopyTo(s2, v * 32 + 20);
            // park C's support next to A's so the centroid route would pick A, not B
            BitConverter.GetBytes(0f).CopyTo(s0, v * 40);
            BitConverter.GetBytes(0f).CopyTo(s0, v * 40 + 4);
            BitConverter.GetBytes((v % 5) / 4f).CopyTo(s0, v * 40 + 8);
        }
        File.WriteAllBytes(Path.Combine(ad, "stream2.buf"), s2);
        File.WriteAllBytes(Path.Combine(ad, "stream0.buf"), s0);

        var result = new MigotoEmitter().Build(req);

        string shader = File.ReadAllText(Path.Combine(outDir, "tiertie_lod1_swap.hlsl"));
        Assert.Contains("static const uint2 PAIR[1] = { uint2(2,1) };", shader);
        Assert.Contains(result.Diagnostics, d => d.Contains("bone 0x00000067 has no row at this tier")
            && d.Contains("co-riding bone 0x00000066"));
    }

    [Fact]
    public void A_tier_that_rigs_every_donor_bone_gets_no_tie_and_no_warning()
    {
        var result = new MigotoEmitter().Build(Request(out string outDir, new[] { A, B }, new[] { A, B }));

        Assert.False(File.Exists(Path.Combine(outDir, "tiertie_lod1_swap.hlsl")));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.DoesNotContain("CustomShaderTierTie", ini);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("longer view distances"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("has no row at this tier"));
    }
}
