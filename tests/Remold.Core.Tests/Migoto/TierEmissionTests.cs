using System;
using System.IO;
using System.Linq;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Skeleton;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Replaced LOD tiers: each suppressed part's tier gets its own capture + recovery operator against the
/// SAME union, and the anchor's tiers run the full chain, falling back per part to the lod0 recover when
/// there is no same-suffix tier. A weighted tier bone the union never saw must carry Gate 1's verdict.
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
    public void Lod0_chain_uses_current_frame_witness_conversion_without_tiers()
    {
        var req = Request(out string outDir);
        GenericPositions(Path.Combine(_root, "alpha"), 32);
        GenericPositions(Path.Combine(_root, "beta"), 16);
        new MigotoEmitter().Build(req);

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        int at = ini.IndexOf("[TextureOverride_Cap_alpha]", StringComparison.Ordinal);
        string section = ini[at..ini.IndexOf("\n\n", at, StringComparison.Ordinal)];
        Assert.Contains("run = CustomShaderConvertW_swap", section);
        Assert.DoesNotContain("run = CustomShaderConvert_swap", section);
        Assert.Contains("[CustomShaderConvertW_swap]\ncs = convert_witness_swap.hlsl\n", ini);
        Assert.True(File.Exists(Path.Combine(outDir, "convert_witness_swap.hlsl")));
    }

    [Fact]
    public void Lod0_without_a_sound_witness_keeps_the_constant_fallback_explicit()
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A });
        string bd = Path.Combine(_root, "beta"); SyntheticPool.WritePartDump(bd, 2, 16, new[] { C });
        GenericPositions(ad, 32);
        GenericPositions(bd, 16);
        string outDir = Path.Combine(_root, "out");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
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
                },
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        int at = ini.IndexOf("[TextureOverride_Cap_alpha]", StringComparison.Ordinal);
        string section = ini[at..ini.IndexOf("\n\n", at, StringComparison.Ordinal)];
        Assert.Contains("run = CustomShaderConvert_swap", section);
        Assert.DoesNotContain("run = CustomShaderConvertW_swap", section);
        Assert.False(File.Exists(Path.Combine(outDir, "convert_witness_swap.hlsl")));
        Assert.Contains(result.Diagnostics, d => d.Contains("freshness depends on draw order"));
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

        // scatter: beta owns C (union slot 2), and B (slot 1) too — alpha outweighs it on B but recovers it
        // only ill-conditioned, and beta holds it with a slim selection → [1, 2]
        var map = File.ReadAllBytes(Path.Combine(outDir, "beta_lod1_map_swap.buf"));
        Assert.Equal(1u, BitConverter.ToUInt32(map, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(map, 4));
    }

    [Fact]
    public void A_classified_tier_bone_outside_the_union_uses_the_write_nothing_sentinel()
    {
        string td = Path.Combine(_root, "alpha_bad"); SyntheticPool.WritePartDump(td, 3, 16, new[] { A, 999u });
        var verdict = new PoolDerive.TierBoneVerdict("alpha", "alpha", "alpha_lod1", 999,
            PoolDerive.TierBoneClass.Lod1Only, Array.Empty<string>());
        var req = Request(out string outDir, null, new PoolTier("alpha", "alpha_lod1", "lod1", td,
            "aaaa0002", BoneVerdicts: new[] { verdict }));

        var result = new MigotoEmitter().Build(req);

        var map = File.ReadAllBytes(Path.Combine(outDir, "alpha_lod1_map_swap.buf"));
        Assert.Equal(PoolMath.Sentinel, BitConverter.ToUInt32(map, 4));
        // the sentinel itself warns of nothing; the tier lacking B is the tier tie's warning, not this one's
        Assert.All(result.Warnings, w => Assert.Contains("moves less naturally", w));
    }

    [Fact]
    public void An_unclassified_weighted_tier_bone_outside_the_union_is_a_contract_error()
    {
        string td = Path.Combine(_root, "alpha_unclassified");
        SyntheticPool.WritePartDump(td, 3, 16, new[] { A, 999u });
        var req = Request(out _, null, new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002"));

        var e = Assert.Throws<AuthoredRefusalException>(() => new MigotoEmitter().Build(req));

        Assert.Equal(
            "LOD 'alpha_lod1' of 'alpha' cannot be built because its geometry uses a bone missing from the "
            + "original part. Internal detail: expected exactly one upstream tier-row verdict but found 0. "
            + "Remove this mesh edit",
            e.Message);
        Assert.DoesNotContain("0x", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_ownerless_merged_tier_row_is_a_user_shaped_contract_refusal()
    {
        string td = Path.Combine(_root, "alpha_ownerless");
        SyntheticPool.WritePartDump(td, 3, 16, new[] { A, 999u });
        var verdict = new PoolDerive.TierBoneVerdict("alpha", "alpha", "alpha_lod1", 999,
            PoolDerive.TierBoneClass.Merged, Array.Empty<string>());
        var req = Request(out _, null, new PoolTier("alpha", "alpha_lod1", "lod1", td,
            "aaaa0002", BoneVerdicts: new[] { verdict }));

        var e = Assert.Throws<AuthoredRefusalException>(() => new MigotoEmitter().Build(req));

        Assert.Equal(
            "LOD 'alpha_lod1' of 'alpha' cannot be built because it is missing geometry from another part at "
            + "this detail level. Internal detail: a MERGED tier-row verdict has no owning part. "
            + "Remove this mesh edit",
            e.Message);
        Assert.DoesNotContain("0x", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_stale_tier_row_verdict_is_a_user_shaped_contract_refusal()
    {
        string td = Path.Combine(_root, "alpha_stale");
        SyntheticPool.WritePartDump(td, 3, 16, new[] { A });
        var verdict = new PoolDerive.TierBoneVerdict("alpha", "alpha", "alpha_lod1", 999,
            PoolDerive.TierBoneClass.Lod1Only, Array.Empty<string>());
        var req = Request(out _, null, new PoolTier("alpha", "alpha_lod1", "lod1", td,
            "aaaa0002", BoneVerdicts: new[] { verdict }));

        var e = Assert.Throws<AuthoredRefusalException>(() => new MigotoEmitter().Build(req));

        Assert.Equal(
            "LOD 'alpha_lod1' of 'alpha' cannot be built because its recorded bones do not match its geometry. "
            + "Internal detail: an upstream tier-row verdict does not match a weighted bone outside the union. "
            + "Remove this mesh edit",
            e.Message);
        Assert.DoesNotContain("0x", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_merged_tier_row_emits_the_approved_warning_and_the_write_nothing_sentinel()
    {
        uint boot = BoneTable.Hash("Shoes01_L");
        string td = Path.Combine(_root, "alpha_merged");
        SyntheticPool.WritePartDump(td, 3, 16, new[] { A, boot });
        var verdict = new PoolDerive.TierBoneVerdict("cloth1", "cloth1", "cloth1_lod1", boot,
            PoolDerive.TierBoneClass.Merged, new[] { "shoes", "hair", "dress", "hat" });
        var req = Request(out string outDir, null, new PoolTier("alpha", "alpha_lod1", "lod1", td,
            "aaaa0002", SourcePart: "cloth1", SourceMesh: "cloth1_lod1", BoneVerdicts: new[] { verdict }));
        req = req with
        {
            Pipelines = new[]
            {
                req.Pipelines.Single() with
                {
                    BonePaths = new System.Collections.Generic.Dictionary<uint, string>
                    {
                        [boot] = "Prefab/root/Root_M/Toes_L/Shoes01_L",
                    },
                },
            },
        };

        var result = new MigotoEmitter().Build(req);

        string warning = Assert.Single(result.Warnings, w => w.Contains("does not show some geometry"));
        Assert.Equal(
            "'cloth1' does not show some geometry from 'shoes', 'hair', and 2 more parts at longer view distances. "
            + "The build log names the bones.",
            warning);
        Assert.DoesNotContain('/', warning);
        Assert.DoesNotContain("cloth1_lod1", warning, StringComparison.Ordinal);
        Assert.Equal(
            $"MERGED tier geometry: affected part 'cloth1'; tier mesh 'cloth1_lod1'; owning parts "
            + $"'shoes', 'hair', 'dress', and 'hat'; "
            + $"bones 'Shoes01_L' (0x{boot:x8}).",
            Assert.Single(result.Diagnostics, d => d.StartsWith("MERGED tier geometry:", StringComparison.Ordinal)));
        var map = File.ReadAllBytes(Path.Combine(outDir, "alpha_lod1_map_swap.buf"));
        Assert.Equal(PoolMath.Sentinel, BitConverter.ToUInt32(map, 4));
    }

    [Fact]
    public void Merged_rows_share_one_warning_and_one_verbose_diagnostic()
    {
        uint named = BoneTable.Hash("Shoes01_L");
        uint[] missing = { named, 998u, 999u };
        string td = Path.Combine(_root, "alpha_merged_group");
        SyntheticPool.WritePartDump(td, 3, 24, new[] { A }.Concat(missing).ToArray());
        var verdicts = missing.Select(b => new PoolDerive.TierBoneVerdict(
            "cloth1", "cloth1", "cloth1_lod1", b, PoolDerive.TierBoneClass.Merged,
            new[] { "shoes" })).ToArray();
        var req = Request(out string outDir, null, new PoolTier("alpha", "alpha_lod1", "lod1", td,
            "aaaa0002", SourcePart: "cloth1", SourceMesh: "cloth1_lod1", BoneVerdicts: verdicts));
        req = req with
        {
            Pipelines = new[]
            {
                req.Pipelines.Single() with
                {
                    BonePaths = new System.Collections.Generic.Dictionary<uint, string>
                    {
                        [named] = "Prefab/root/Root_M/Toes_L/Shoes01_L",
                    },
                },
            },
        };

        var result = new MigotoEmitter().Build(req);

        Assert.Equal(
            "'cloth1' does not show some geometry from 'shoes' at longer view distances. "
            + "The build log names the bones.",
            Assert.Single(result.Warnings, w => w.Contains("does not show some geometry")));
        string diagnostic = Assert.Single(result.Diagnostics,
            d => d.StartsWith("MERGED tier geometry:", StringComparison.Ordinal));
        Assert.Contains("affected part 'cloth1'; tier mesh 'cloth1_lod1'; owning parts 'shoes'", diagnostic);
        Assert.Contains($"'Shoes01_L' (0x{named:x8})", diagnostic);
        Assert.Contains("no matching chain suffix (0x000003e6)", diagnostic);
        Assert.Contains("no matching chain suffix (0x000003e7)", diagnostic);
        var map = File.ReadAllBytes(Path.Combine(outDir, "alpha_lod1_map_swap.buf"));
        Assert.All(Enumerable.Range(1, 3), i =>
            Assert.Equal(PoolMath.Sentinel, BitConverter.ToUInt32(map, i * 4)));
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

    // ---- stream-1 layout per tier ------------------------------------------------------------------
    // A donor draw at a tier is read through the tier mesh's input layout, so a tier storing stream 1
    // differently from the lod0 needs the stream re-encoded for it.

    private const int Float32 = 0, Float16 = 1;

    /// <summary>The fixtures' table with stream 1's UVs at <paramref name="uvFormat"/>, an optional second
    /// UV pair after them, stream 1 dropped altogether, or stream 0's normals stored as halves.</summary>
    private static UnityMesh.ChannelDef[] Layout(int uvFormat, bool secondUv = false, bool noStream1 = false,
        bool halfNormals = false)
    {
        var t = SyntheticPool.SkinnedLayout();
        t[4] = new(1, 16, uvFormat, 2);
        if (secondUv) t[5] = new(1, uvFormat == Float32 ? 24 : 20, uvFormat, 2);
        if (noStream1) t[3] = t[4] = t[5] = default;
        if (halfNormals) t[1] = new(0, 12, Float16, 3);
        return t;
    }

    /// <summary>Replace the channel table a dump's meta records.</summary>
    private static void RecordLayout(string dir, UnityMesh.ChannelDef[]? table)
    {
        string path = Path.Combine(dir, "meta.json");
        string meta = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(path),
            ",\\s*\"channels\": \\[.*?\\]", "", System.Text.RegularExpressions.RegexOptions.Singleline).TrimEnd();
        File.WriteAllText(path, table is null ? meta : meta[..^1] + ", " + SyntheticPool.ChannelsJson(table) + " }");
    }

    /// <summary>Overwrite a fixture dump's stride-20 stream 1 with readable values: color (v,0,0,1) and
    /// half UVs (v/64, 1 - v/64).</summary>
    private static void ReadableStream1(string dir, int verts)
    {
        var s1 = new byte[verts * 20];
        for (int v = 0; v < verts; v++)
        {
            BitConverter.GetBytes((float)v).CopyTo(s1, v * 20);
            BitConverter.GetBytes(1f).CopyTo(s1, v * 20 + 12);
            BitConverter.GetBytes((Half)(v / 64f)).CopyTo(s1, v * 20 + 16);
            BitConverter.GetBytes((Half)(1f - v / 64f)).CopyTo(s1, v * 20 + 18);
        }
        File.WriteAllBytes(Path.Combine(dir, "stream1.buf"), s1);
    }

    private PoolBuildRequest LayoutRequest(out string outDir, params (string Suffix, int UvFormat, bool SecondUv)[] tiers) =>
        LayoutRequest(out outDir, tiers.Select(t => (t.Suffix, (UnityMesh.ChannelDef[]?)Layout(t.UvFormat, t.SecondUv))).ToArray());

    private PoolBuildRequest LayoutRequest(out string outDir, params (string Suffix, UnityMesh.ChannelDef[]? Table)[] tiers)
    {
        var poolTiers = tiers.Select((t, i) =>
        {
            string td = Path.Combine(_root, "alpha_" + t.Suffix);
            SyntheticPool.WritePartDump(td, 3 + i, 24, new[] { A, B });
            RecordLayout(td, t.Table);
            return new PoolTier("alpha", "alpha_" + t.Suffix, t.Suffix, td, $"aaaa00{i + 2:d2}");
        }).ToArray();
        var req = Request(out outDir, null, poolTiers);
        foreach (var (part, verts) in new[] { ("alpha", 32), ("beta", 16) })
            ReadableStream1(Path.Combine(_root, part), verts);
        return req;
    }

    private static string Section(string ini, string header)
    {
        int at = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(at >= 0, header);
        int end = ini.IndexOf("\n\n", at, StringComparison.Ordinal);
        return end < 0 ? ini[at..] : ini[at..end];
    }

    [Fact]
    public void Anchor_tier_storing_stream1_differently_draws_a_reencoded_stream()
    {
        var req = LayoutRequest(out string outDir, ("lod1", Float32, false));
        new MigotoEmitter().Build(req);

        // the variant: same 48 vertices, color verbatim, the half UVs widened to float32
        var primary = File.ReadAllBytes(Path.Combine(outDir, "combined_vb1_swap.buf"));
        var variant = File.ReadAllBytes(Path.Combine(outDir, "combined_vb1_swap_v1.buf"));
        Assert.Equal(48 * 20, primary.Length);
        Assert.Equal(48 * 24, variant.Length);
        for (int v = 0; v < 48; v++)
        {
            Assert.Equal(primary.AsSpan(v * 20, 16).ToArray(), variant.AsSpan(v * 24, 16).ToArray());
            Assert.Equal((float)BitConverter.ToHalf(primary, v * 20 + 16), BitConverter.ToSingle(variant, v * 24 + 16));
            Assert.Equal((float)BitConverter.ToHalf(primary, v * 20 + 18), BitConverter.ToSingle(variant, v * 24 + 20));
        }

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.Contains("global $zz_vb1_swap = 0\n", ini);
        Assert.Contains("[Resource_NewVB1_swap_v1]\ntype = RWBuffer\nstride = 24\nfilename = combined_vb1_swap_v1.buf\n", ini);
        // every anchor capture names its stream, so the selector never carries a stale value over
        Assert.Contains("$zz_vb1_swap = 0\n", Section(ini, "[TextureOverride_Cap_alpha]"));
        Assert.Contains("$zz_vb1_swap = 1\n", Section(ini, "[TextureOverride_Cap_alpha_lod1]"));
        Assert.Contains("vb1 = Resource_NewVB1_swap\nif $zz_vb1_swap == 1\nvb1 = Resource_NewVB1_swap_v1\nendif\n",
            Section(ini, "[CommandListDraw_swap]"));
    }

    [Fact]
    public void Tiers_sharing_a_differing_layout_share_one_stream_and_a_matching_tier_binds_the_primary()
    {
        var req = LayoutRequest(out string outDir, ("lod1", Float32, false), ("lod2", Float16, false),
            ("lod3", Float32, false));
        new MigotoEmitter().Build(req);

        Assert.True(File.Exists(Path.Combine(outDir, "combined_vb1_swap_v1.buf")));
        Assert.False(File.Exists(Path.Combine(outDir, "combined_vb1_swap_v2.buf")));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.Contains("$zz_vb1_swap = 1\n", Section(ini, "[TextureOverride_Cap_alpha_lod1]"));
        Assert.Contains("$zz_vb1_swap = 0\n", Section(ini, "[TextureOverride_Cap_alpha_lod2]"));
        Assert.Contains("$zz_vb1_swap = 1\n", Section(ini, "[TextureOverride_Cap_alpha_lod3]"));
    }

    [Fact]
    public void Tiers_matching_the_lod0_layout_emit_no_selector()
    {
        var req = LayoutRequest(out string outDir, ("lod1", Float16, false));
        new MigotoEmitter().Build(req);

        Assert.False(File.Exists(Path.Combine(outDir, "combined_vb1_swap_v1.buf")));
        Assert.DoesNotContain("zz_vb1_", File.ReadAllText(Path.Combine(outDir, "mod.ini")));
    }

    [Fact]
    public void Tier_storing_a_uv_set_the_lod0_lacks_gets_it_filled_from_the_first()
    {
        var req = LayoutRequest(out string outDir, ("lod1", Float16, true));
        var result = new MigotoEmitter().Build(req);

        var primary = File.ReadAllBytes(Path.Combine(outDir, "combined_vb1_swap.buf"));
        var variant = File.ReadAllBytes(Path.Combine(outDir, "combined_vb1_swap_v1.buf"));
        Assert.Equal(48 * 24, variant.Length);
        for (int v = 0; v < 48; v++)
        {
            Assert.Equal(primary.AsSpan(v * 20, 20).ToArray(), variant.AsSpan(v * 24, 20).ToArray());
            Assert.Equal(primary.AsSpan(v * 20 + 16, 4).ToArray(), variant.AsSpan(v * 24 + 20, 4).ToArray());
        }
        Assert.Contains(result.Diagnostics, d => d.StartsWith("alpha_lod1: stores TexCoord1,", StringComparison.Ordinal));
    }

    [Fact]
    public void Tier_storing_a_non_uv_channel_the_lod0_lacks_refuses_the_build()
    {
        // the fixtures' lod0 stores no color once it is dropped from the anchor's table
        var req = LayoutRequest(out _, ("lod1", Layout(Float16)));
        var noColor = Layout(Float16);
        noColor[3] = default;
        noColor[4] = new(1, 0, Float16, 2);
        foreach (var (part, verts) in new[] { ("alpha", 32), ("beta", 16) })
        {
            File.WriteAllBytes(Path.Combine(_root, part, "stream1.buf"), new byte[verts * 4]);
            RecordLayout(Path.Combine(_root, part), noColor);
        }
        var e = Assert.Throws<AuthoredRefusalException>(() => new MigotoEmitter().Build(req));
        // the tier AND the part it belongs to, as the other tier refusals name them
        Assert.Contains("LOD 'alpha_lod1' of 'alpha'", e.Message);
        Assert.Contains("Color", e.Message);
    }

    [Fact]
    public void A_tier_storing_nothing_in_stream1_needs_no_copy()
    {
        // its draw reads nothing from that slot, so whatever is bound there is never sampled
        var req = LayoutRequest(out string outDir, ("lod1", Layout(Float16, noStream1: true)));
        new MigotoEmitter().Build(req);

        Assert.False(File.Exists(Path.Combine(outDir, "combined_vb1_swap_v1.buf")));
        Assert.DoesNotContain("zz_vb1_", File.ReadAllText(Path.Combine(outDir, "mod.ini")));
    }

    [Fact]
    public void A_tier_storing_stream0_differently_refuses_the_build()
    {
        // stream 0 is posed into one fixed shape every frame, so there is no copy to give such a tier
        var req = LayoutRequest(out _, ("lod1", Layout(Float16, halfNormals: true)));
        var e = Assert.Throws<AuthoredRefusalException>(() => new MigotoEmitter().Build(req));
        Assert.Contains("LOD 'alpha_lod1' of 'alpha'", e.Message);
        Assert.Contains("stream 0", e.Message);
    }

    [Fact]
    public void A_tier_with_no_recorded_layout_is_an_error_not_a_guess()
    {
        var req = LayoutRequest(out _, ("lod1", (UnityMesh.ChannelDef[]?)null));
        var e = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(req));
        Assert.Contains("alpha_lod1", e.Message);
    }

    [Fact]
    public void The_reencoded_stream_is_named_by_mesh_in_the_build_log()
    {
        var req = LayoutRequest(out _, ("lod1", Float32, false));
        var result = new MigotoEmitter().Build(req);
        Assert.Contains(result.Diagnostics, d => d.StartsWith("alpha_lod1: stores stream 1 differently", StringComparison.Ordinal));
    }

    [Fact]
    public void A_compiled_donors_own_table_is_what_a_tier_is_checked_against()
    {
        // The app always builds with a compiled donor, and the donor's folder, not the anchor's dump,
        // states the layout the shipped stream is sliced in.
        string td = Path.Combine(_root, "alpha_lod1");
        SyntheticPool.WritePartDump(td, 3, 24, new[] { A, B });
        RecordLayout(td, Layout(Float32));
        var req = Request(out string outDir, null, new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002"));
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 12, unionBones: 3, submeshes: 1);
        ReadableStream1(donor, 12);
        // the anchor's dump claims float UVs too: only the donor's table says the stream holds halves
        RecordLayout(Path.Combine(_root, "alpha"), Layout(Float32));
        req = new PoolBuildRequest
        {
            OutDir = req.OutDir,
            Pipelines = new[] { req.Pipelines[0] with { DonorDir = donor } },
        };
        new MigotoEmitter().Build(req);

        var primary = File.ReadAllBytes(Path.Combine(outDir, "combined_vb1_swap.buf"));
        var variant = File.ReadAllBytes(Path.Combine(outDir, "combined_vb1_swap_v1.buf"));
        Assert.Equal(12 * 20, primary.Length);
        Assert.Equal(12 * 24, variant.Length);
        for (int v = 0; v < 12; v++)
            Assert.Equal((float)BitConverter.ToHalf(primary, v * 20 + 16), BitConverter.ToSingle(variant, v * 24 + 16));
    }
}
