using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The tie underlay and the gated source recovers: a pool part the scene state never renders leaves its
/// recover unrun (its latch stays down), and every donor-WEIGHTED union bone it owns is filled from its
/// nearest ANCHOR-owned skeleton ancestor's converted row — a rigid ride instead of a never-substantiated
/// copy's garbage. Bones with no path or no anchor-owned ancestor keep the identity seed, named in the
/// build log.
/// </summary>
public class TieUnderlayEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-tie-" + Guid.NewGuid().ToString("N"));

    public TieUnderlayEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const uint A = 101, B = 102, C = 103;

    /// <summary>alpha (A,B) + beta (B,C) with beta the anchor: after anchor-preferred ownership beta owns
    /// B and C, alpha owns only A. The donor rides all three union slots, so A is the tied bone.</summary>
    private PoolBuildRequest Request(out string outDir, IReadOnlyDictionary<uint, string>? bonePaths,
        PoolTier[]? tiers = null)
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        string bd = Path.Combine(_root, "beta"); SyntheticPool.WritePartDump(bd, 2, 32, new[] { B, C });
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 9, unionBones: 3, submeshes: 1);
        outDir = Path.Combine(_root, "out");
        return new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    Tiers = tiers,
                    BonePaths = bonePaths,
                },
            },
        };
    }

    [Fact]
    public void A_donor_ridden_bone_of_another_part_ties_to_its_anchor_owned_ancestor()
    {
        // A descends from B, which the anchor owns; C is the root. Union order [A, B, C].
        var result = new MigotoEmitter().Build(Request(out string outDir, new Dictionary<uint, string>
        {
            [A] = "root/spine/arm",
            [B] = "root/spine",
            [C] = "root",
        }));

        string shader = File.ReadAllText(Path.Combine(outDir, "tiefill_alpha_swap.hlsl"));
        Assert.Contains("static const uint2 PAIR[1] = { uint2(0,1) };", shader);

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        Assert.Contains("[CustomShaderTie_alpha_swap]\ncs = tiefill_alpha_swap.hlsl\n"
                      + "cs-t0 = copy Resource_PaletteConv_swap\n"
                      + "cs-u1 = copy Resource_PaletteConv_swap\nDispatch = 1, 1, 1\n"
                      + "Resource_PaletteConv_swap = copy cs-u1\npost cs-u1 = null\n", ini);
        // the ancestor rows are READ through the copy, never the UAV: a 4-component typed UAV load
        // does not compile on cs_5_0, and a failed compile is a silent no-fill in game
        string tieShader = File.ReadAllText(Path.Combine(outDir, "tiefill_alpha_swap.hlsl"));
        Assert.Contains("StructuredBuffer<float4> palIn  : register(t0);", tieShader);
        Assert.DoesNotContain("palOut[(PAIR[p].y", tieShader);
        // the fill fires only while alpha's latch is down, after the convert and before the skin —
        // and alpha's own recover is gated the opposite way
        Assert.Contains("if $zz_gate_src_alpha == 1\nrun = CustomShaderRecover_alpha_swap\nendif\n"
                      + "run = CustomShaderRecover_beta_swap\n"
                      + "run = CustomShaderConvert_swap\n"
                      + "if $zz_gate_src_alpha == 0\nrun = CustomShaderTie_alpha_swap\nendif\n"
                      + "run = CustomShaderSkin_swap\n", ini);
        Assert.Contains(result.Diagnostics, d => d.Contains("0x00000065") && d.Contains("rides its ancestor")
            && d.Contains("0x00000066") && d.Contains("'alpha'"));
    }

    [Fact]
    public void A_bone_with_no_anchor_owned_ancestor_is_reseeded_to_identity_while_its_owner_is_absent()
    {
        // A's chain never meets an anchor-owned bone: B sits elsewhere in the tree. No tie exists — but
        // the row must still be WRITTEN while alpha is absent, because the converts rewrite every union
        // row (an absent part's constants-K is zero), so the fill reseeds it to identity.
        var result = new MigotoEmitter().Build(Request(out string outDir, new Dictionary<uint, string>
        {
            [A] = "root/arm/hand",
            [B] = "root/spine",
            [C] = "root/spine/chest",
        }));

        string shader = File.ReadAllText(Path.Combine(outDir, "tiefill_alpha_swap.hlsl"));
        Assert.Contains("static const uint PAIRS=0, SEEDS=1;", shader);
        Assert.Contains("static const uint  SEED[1] = { 0 };", shader);
        Assert.Contains("if $zz_gate_src_alpha == 0\nrun = CustomShaderTie_alpha_swap\nendif\n",
            File.ReadAllText(Path.Combine(outDir, "mod.ini")));
        Assert.Contains(result.Diagnostics,
            d => d.Contains("0x00000065") && d.Contains("no anchor-owned skeleton ancestor"));
    }

    [Fact]
    public void Absent_bone_paths_degrade_to_the_identity_reseed_with_the_reason()
    {
        var result = new MigotoEmitter().Build(Request(out string outDir, bonePaths: null));

        // no path, no tie — but the absent-owner fill still ships, all seeds
        string shader = File.ReadAllText(Path.Combine(outDir, "tiefill_alpha_swap.hlsl"));
        Assert.Contains("static const uint PAIRS=0, SEEDS=1;", shader);
        Assert.Contains(result.Diagnostics, d => d.Contains("0x00000065") && d.Contains("no skeleton path"));
        // the recover gate still stands: absence of ties never re-opens the poisoned-copy route
        Assert.Contains("if $zz_gate_src_alpha == 1\nrun = CustomShaderRecover_alpha_swap\nendif\n",
            File.ReadAllText(Path.Combine(outDir, "mod.ini")));
    }

    [Fact]
    public void A_tied_part_with_a_tier_has_one_latch_the_tier_also_raises()
    {
        string at = Path.Combine(_root, "alpha_l1"); SyntheticPool.WritePartDump(at, 3, 24, new[] { A, B });
        new MigotoEmitter().Build(Request(out string outDir, new Dictionary<uint, string>
        {
            [A] = "root/spine/arm",
            [B] = "root/spine",
            [C] = "root",
        }, new[] { new PoolTier("alpha", "alpha_lod1", "lod1", at, "aaaa0002") }));

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        // ONE latch per part: the tie fires on its complement, so no mesh-up/mesh-down state can run
        // neither the recover nor the fill
        Assert.Contains("if $zz_gate_src_alpha == 0\nrun = CustomShaderTie_alpha_swap\nendif\n", ini);
        Assert.DoesNotContain("zz_gate_src_alpha_lod1", ini);
        // the tier's capture raises the PART's latch — a part on screen at any detail is present
        Assert.Contains("Resource_alpha_lod1_Posed = ref vb0\n$zz_seen_src_alpha = 1\n", ini);
    }
}
