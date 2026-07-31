using System;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Replaced LOD tiers: each suppressed part's tier gets its own capture + recovery operator against the
/// SAME union, and the anchor's tiers run the full chain, falling back per part to the lod0 recover when
/// there is no same-suffix tier. A tier bone the union never saw fails loudly.
/// </summary>
public class TierEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-tier-" + Guid.NewGuid().ToString("N"));

    public TierEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const uint A = 101, B = 102, C = 103;

    /// <summary>Rewrite a dump's positions as a generic (rank-4-support) cloud — the shared fixture's
    /// ramp positions are near-collinear, which the weak-support sentinel correctly rejects.</summary>
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

    /// <summary>alpha (bones A,B, owning both) + beta (bones B,C, owning C), anchor alpha. The vert counts
    /// keep every bone's support above the weak-support sentinel threshold.</summary>
    private PoolBuildRequest Request(out string outDir, string[]? noSkip = null, params PoolTier[] tiers)
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        string bd = Path.Combine(_root, "beta"); SyntheticPool.WritePartDump(bd, 2, 16, new[] { B, C });
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
                    Anchor = "alpha",
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    NoSkipParts = noSkip,
                    Tiers = tiers.Length > 0 ? tiers : null,
                },
            },
        };
    }

    [Fact]
    public void Anchor_tier_runs_the_chain_with_lod0_fallback_for_tierless_parts()
    {
        string td = Path.Combine(_root, "alpha_l1"); SyntheticPool.WritePartDump(td, 3, 24, new[] { A, B });
        var req = Request(out string outDir, null, new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002"));
        new MigotoEmitter().Build(req);

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        int cap = ini.IndexOf("[TextureOverride_Cap_alpha_lod1]", StringComparison.Ordinal);
        Assert.True(cap >= 0);
        string section = ini[cap..ini.IndexOf("\n\n", cap, StringComparison.Ordinal)];
        Assert.Contains("hash = aaaa0002", section);
        Assert.Contains("handling = skip", section);
        Assert.Contains("Resource_alpha_lod1_Posed = ref vb0", section);
        Assert.DoesNotContain("vs-cb1", section);                             // tiers capture NO constants
        Assert.Contains("if $zz_done_swap_lod1 == 0", section);               // compute gated per frame
        Assert.Contains("run = CustomShaderRecover_alpha_lod1_swap", section);     // its own tier recover
        Assert.Contains("run = CustomShaderRecover_beta_swap", section);           // lod0 fallback
        Assert.Contains("run = CustomShaderConvertW_swap", section);          // constants-free witness convert
        Assert.Contains("run = CommandListDraw_swap", section);

        Assert.Contains("[CustomShaderRecover_alpha_lod1_swap]", ini);
        Assert.Contains("[CustomShaderConvertW_swap]\ncs = convert_witness_swap.hlsl\n"
                      + "cs-u1 = copy Resource_PaletteConv_swap\ncs-t0 = copy Resource_Palette_swap\n"
                      + "cs-t1 = Resource_OwnerPart_swap\n", ini);
        Assert.True(File.Exists(Path.Combine(outDir, "convert_witness_swap.hlsl")));
        // per-frame flags declared and reset
        Assert.Contains("global $zz_done_swap_lod1 = 0", ini);
        Assert.Contains("[Present]\n$zz_done_swap = 0\n$zz_done_swap_lod1 = 0\n", ini);
        Assert.True(File.Exists(Path.Combine(outDir, "alpha_lod1_cpinv.buf")));
        Assert.True(File.Exists(Path.Combine(outDir, "recover_alpha_lod1_cs.hlsl")));
    }

    [Fact]
    public void NonAnchor_tier_captures_and_skips_without_a_chain()
    {
        string td = Path.Combine(_root, "beta_l1"); SyntheticPool.WritePartDump(td, 3, 16, new[] { B, C });
        GenericPositions(td, 16);
        var req = Request(out string outDir, null, new PoolTier("beta", "beta_lod1", "lod1", td, "bbbb0002"));
        new MigotoEmitter().Build(req);

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        int cap = ini.IndexOf("[TextureOverride_Cap_beta_lod1]", StringComparison.Ordinal);
        Assert.True(cap >= 0);
        string section = ini[cap..ini.IndexOf("\n\n", cap, StringComparison.Ordinal)];
        Assert.Contains("handling = skip", section);
        Assert.DoesNotContain("run = CommandListDraw", section);

        // scatter: beta owns C (union slot 2) but not B (alpha's weight wins) → [Sentinel, 2]
        var map = File.ReadAllBytes(Path.Combine(outDir, "beta_lod1_map_swap.buf"));
        Assert.Equal(PoolMath.Sentinel, BitConverter.ToUInt32(map, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(map, 4));
    }

    [Fact]
    public void A_tier_bone_outside_the_union_fails_loudly()
    {
        string td = Path.Combine(_root, "alpha_bad"); SyntheticPool.WritePartDump(td, 3, 16, new[] { A, 999u });
        var req = Request(out _, null, new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002"));
        var e = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(req));
        Assert.Contains("union palette can't pose it", e.Message);
    }

    [Fact]
    public void A_leave_parts_tier_captures_without_skipping()
    {
        string td = Path.Combine(_root, "beta_leave"); SyntheticPool.WritePartDump(td, 3, 16, new[] { B, C });
        var req = Request(out string outDir, new[] { "beta" }, new PoolTier("beta", "beta_lod1", "lod1", td, "bbbb0002"));
        new MigotoEmitter().Build(req);

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        int cap = ini.IndexOf("[TextureOverride_Cap_beta_lod1]", StringComparison.Ordinal);
        Assert.True(cap >= 0);
        string section = ini[cap..ini.IndexOf("\n\n", cap, StringComparison.Ordinal)];
        Assert.Contains("Resource_beta_lod1_Posed = ref vb0", section);
        Assert.DoesNotContain("handling = skip", section);
    }

    [Fact]
    public void A_witness_bone_gives_tier_chains_a_constants_free_space_fix()
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        GenericPositions(ad, 32);
        string bd = Path.Combine(_root, "beta"); SyntheticPool.WritePartDump(bd, 2, 16, new[] { B, C });
        GenericPositions(bd, 16);
        string td = Path.Combine(_root, "beta_l1"); SyntheticPool.WritePartDump(td, 3, 16, new[] { B, C });
        GenericPositions(td, 16);
        string outDir = Path.Combine(_root, "out");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "alpha",
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    Tiers = new[] { new PoolTier("beta", "beta_lod1", "lod1", td, "bbbb0002") },
                },
            },
        });

        // beta's witness is the shared sound bone B, redirected to reserved slot 3 in BOTH its lod0 and
        // tier maps; the anchor owns B, so the anchor side reads the real slot 1
        var map0 = File.ReadAllBytes(Path.Combine(outDir, "beta_map_swap.buf"));
        Assert.Equal(3u, BitConverter.ToUInt32(map0, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(map0, 4));
        var map1 = File.ReadAllBytes(Path.Combine(outDir, "beta_lod1_map_swap.buf"));
        Assert.Equal(3u, BitConverter.ToUInt32(map1, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(map1, 4));
        Assert.Equal(4 * 4 * 16, new FileInfo(Path.Combine(outDir, "palette_seed_swap.buf")).Length);
        string hlsl = File.ReadAllText(Path.Combine(outDir, "convert_witness_swap.hlsl"));
        Assert.Contains("static const uint ANCHOR=0;", hlsl);
        Assert.Contains("uint2(0xffffffff,0xffffffff), uint2(0x0000000c,0x00000004)", hlsl);
    }

    [Fact]
    public void A_pipeline_without_donor_textures_keeps_every_original_map()
    {
        // No donor textures ⇒ no neutral substitution and no neutral maps on disk. Stomping an unmeasured
        // pass's slot with a neutral paints its raw colour — a magenta NeutralRMO reads as pink geometry.
        var req = Request(out string outDir);
        new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.DoesNotContain("Resource_NeutralN", ini);
        Assert.DoesNotContain("Resource_NeutralRMO", ini);
        Assert.False(File.Exists(Path.Combine(outDir, "neutral_n.dds")));
        Assert.False(File.Exists(Path.Combine(outDir, "neutral_rmo.dds")));
    }

    [Fact]
    public void A_weakly_supported_owned_tier_bone_is_sentineled_with_a_diagnostic()
    {
        // 4 verts over 2 bones = 2 supporting verts each — below the rank-4 floor for both
        string td = Path.Combine(_root, "beta_weak"); SyntheticPool.WritePartDump(td, 3, 4, new[] { B, C });
        var req = Request(out string outDir, null, new PoolTier("beta", "beta_lod1", "lod1", td, "bbbb0002"));
        var result = new MigotoEmitter().Build(req);

        // beta owns only C (union slot 2); its weak tier support sentinels it → whole map is sentinel
        var map = File.ReadAllBytes(Path.Combine(outDir, "beta_lod1_map_swap.buf"));
        Assert.Equal(PoolMath.Sentinel, BitConverter.ToUInt32(map, 0));
        Assert.Equal(PoolMath.Sentinel, BitConverter.ToUInt32(map, 4));
        Assert.Contains(result.Diagnostics, w => w.Contains("beta_lod1") && w.Contains("weakly supported"));
        Assert.Empty(result.Warnings);   // a fidelity observation is never a user-facing warning
    }
}
