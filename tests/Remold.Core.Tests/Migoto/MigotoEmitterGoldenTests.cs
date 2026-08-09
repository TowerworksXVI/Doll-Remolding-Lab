using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The emitter's TEXT CONTRACT: a full pooled build over synthetic inputs must emit <c>mod.ini</c> and
/// <c>union.json</c> byte-for-byte as committed in <c>Migoto/golden/</c>, so emitted-text changes are
/// always deliberate. To alter emitted text on purpose, regenerate with <c>REMOLD_REGOLD=1</c> for one
/// run and account for every diff hunk in the commit message.
/// </summary>
public class MigotoEmitterGoldenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-golden-" + Guid.NewGuid().ToString("N"));

    public MigotoEmitterGoldenTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static string GoldenDir([System.Runtime.CompilerServices.CallerFilePath] string self = "") =>
        Path.Combine(Path.GetDirectoryName(self)!, "golden");

    /// <summary>One pipeline of two 4-vert parts (disjoint bones, union of 4), a donor riding the whole
    /// union, two hide hashes — every emission feature of the pooled path at once. The donor's four
    /// submeshes are the MIXED map case the per-draw binds exist for: submesh 0 untouched, 1 authored on
    /// all three slots, 2 asking for the neutral normal and RMO, 3 untouched again behind the two that
    /// bound.</summary>
    internal static IReadOnlyDictionary<int, SubmeshMaps> MixedMaps(string root)
    {
        string albedo = Path.Combine(root, "sub1.dds");
        string normal = Path.Combine(root, "sub1_n.dds");
        string rmo = Path.Combine(root, "sub1_r.dds");
        FlatDds.Write(albedo, (10, 220, 90, 255));
        FlatDds.Write(normal, (120, 130, 250, 255));
        FlatDds.Write(rmo, (40, 60, 200, 255));
        return new Dictionary<int, SubmeshMaps>
        {
            [1] = new(MapSlot.From(albedo), MapSlot.From(normal), MapSlot.From(rmo)),
            [2] = new(MapSlot.Inherit, MapSlot.Neutral, MapSlot.Neutral),
        };
    }

    private string RunFullBuild()
    {
        string dumps = Path.Combine(_root, "dumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64, boneHashes: new uint[] { 101, 102 });
        SyntheticPool.WritePartDump(Path.Combine(dumps, "beta"), seed: 60, verts: 64, boneHashes: new uint[] { 201, 202 });
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 4, submeshes: 4);

        string outDir = Path.Combine(_root, "out");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            HideHashes = new[] { "cccc3333", "dddd4444" },
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")), new PoolPart("beta", Path.Combine(dumps, "beta")) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111", ["beta"] = "bbbb2222" },
                    SubTextures = MixedMaps(_root),
                    StockMaps = new[]
                    {
                        new StockMapTag("f1f1a1a1", StockMapKind.Albedo),
                        new StockMapTag("f2f2b2b2", StockMapKind.Normal),
                        new StockMapTag("f3f3c3c3", StockMapKind.Rmo),
                    },
                },
            },
        });
        AssertEmittedIniIsClean(outDir);
        return outDir;
    }

    /// <summary>3DMigoto drops a duplicate-named section silently, so every ini these tests emit is checked
    /// for one before anything is asserted about its text.</summary>
    private static void AssertEmittedIniIsClean(string outDir) =>
        ModBuilderTests.AssertNoDuplicateSections(File.ReadAllText(Path.Combine(outDir, "mod.ini")));

    [Fact]
    public void An_untouched_submesh_of_a_mixed_pipeline_binds_no_texture_slot()
    {
        // The whole point of per-draw binds: the vanilla submesh keeps the anchor's real maps, so its draw
        // carries no ps-t assignment of its own — and the one AFTER the authored draws puts back what they
        // stomped rather than inheriting a neighbour's map.
        string ini = File.ReadAllText(Path.Combine(RunFullBuild(), "mod.ini"));
        string list = ini[ini.IndexOf("[CommandListDraw_swap]", StringComparison.Ordinal)..];

        // everything past the probe's last line is binds and draws, in submesh order
        const string probeEnd = "$zz_slot_r = 6\nendif\n";
        var chunks = list[(list.IndexOf(probeEnd, StringComparison.Ordinal) + probeEnd.Length)..]
            .Split("drawindexed = ");
        // chunks[0] is submesh 0's binds; every later chunk opens with the previous draw's arguments
        string Binds(int submesh) => submesh == 0 ? chunks[0] : chunks[submesh][(chunks[submesh].IndexOf('\n') + 1)..];

        Assert.DoesNotContain("ps-t", Binds(0));                       // untouched: the anchor's real maps
        Assert.Contains("ps-t0 = Resource_Tex0\n", Binds(1));          // authored albedo
        Assert.Contains("ps-t0 = Resource_Tex1\n", Binds(1));          // authored normal
        Assert.Contains("ps-t0 = Resource_Tex2\n", Binds(1));          // authored RMO
        Assert.Contains("ps-t0 = Resource_NeutralN\n", Binds(2));      // explicit neutral
        Assert.Contains("ps-t0 = Resource_NeutralRMO\n", Binds(2));
        Assert.Contains("ps-t0 = Resource_SaveT0\n", Binds(2));        // albedo back to the anchor's own
        // the last submesh is untouched again: only what the neutral draw stomped is put back
        Assert.Equal(2, CountOf(Binds(3), "ps-t0 = Resource_SaveT0\n"));

        // the binds are guarded by the probe answer, so a pass holding no tagged map draws geometry-only
        for (int s = 0; s <= 6; s++)
            Assert.Contains($"if $zz_slot_a == {s}\n", list);
    }

    private static int CountOf(string haystack, string needle) => haystack.Split(needle).Length - 1;

    /// <summary>The same build with keys on both tiers: the mod carries F6, the one Replace carries F8, and
    /// one of the two hides carries F9. Pins where the gates land, what the <c>[Key…]</c> sections look like,
    /// and that a keyed variable starts at 1.</summary>
    private string RunKeyedBuild()
    {
        string dumps = Path.Combine(_root, "kdumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64, boneHashes: new uint[] { 101, 102 });
        SyntheticPool.WritePartDump(Path.Combine(dumps, "beta"), seed: 60, verts: 64, boneHashes: new uint[] { 201, 202 });
        string donor = Path.Combine(_root, "kdonor");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 4);

        string outDir = Path.Combine(_root, "kout");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            ToggleKey = "F6",
            HideHashes = new[] { "cccc3333", "dddd4444" },
            HideKeys = new Dictionary<string, string> { ["dddd4444"] = "F9" },
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")), new PoolPart("beta", Path.Combine(dumps, "beta")) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111", ["beta"] = "bbbb2222" },
                    ToggleKey = "F8",
                },
            },
        });
        AssertEmittedIniIsClean(outDir);
        return outDir;
    }

    [Fact]
    public void Keyed_build_emits_the_pinned_text_contract()
    {
        string outDir = RunKeyedBuild();
        bool regold = Environment.GetEnvironmentVariable("REMOLD_REGOLD") == "1";
        string emitted = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        string goldenPath = Path.Combine(GoldenDir(), "mod_keyed.ini");
        if (regold)
        {
            Directory.CreateDirectory(GoldenDir());
            File.WriteAllText(goldenPath, emitted);
        }
        else
        {
            Assert.True(File.Exists(goldenPath), $"golden asset missing: {goldenPath} (run once with REMOLD_REGOLD=1)");
            Assert.Equal(File.ReadAllText(goldenPath), emitted);
        }
        Assert.False(regold, "REMOLD_REGOLD run regenerated the goldens — rerun without it to compare");
    }

    [Fact]
    public void A_keyed_build_starts_on_and_gates_both_tiers()
    {
        var ini = File.ReadAllText(Path.Combine(RunKeyedBuild(), "mod.ini"));

        // every keyed variable starts at 1: a keyed mod behaves as an unkeyed one until a key is pressed
        foreach (var (v, k) in new[] { ("zz_key_f6", "F6"), ("zz_key_f8", "F8"), ("zz_key_f9", "F9") })
        {
            Assert.Contains($"global ${v} = 1", ini);
            // the Key section binds and runs; the flip lives in the command list it runs, because an
            // assignment inside a [Key…] section is dropped when 3DMigoto parses it as a KeyOverride
            Assert.Contains($"[Key_{v}]\nkey = no_modifiers {k}\nrun = CommandListKey_{v}\n\n", ini);
            Assert.Contains($"[CommandListKey_{v}]\n${v} = 1 - ${v}\n", ini);
        }
        // a toggle is per-session: nothing carries a press into the next launch
        Assert.DoesNotContain("persist", ini);
        // tier 1 outside, tier 2 inside — either one switches the Replace off
        Assert.Contains("if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nhandling = skip\n", ini);
        // an unkeyed hide still gates on the mod's key alone
        Assert.Contains("hash = cccc3333\nmatch_priority = 0\nif $zz_key_f6 == 1\nhandling = skip\nendif\n", ini);
        Assert.Contains("hash = dddd4444\nmatch_priority = 0\nif $zz_key_f6 == 1\nif $zz_key_f9 == 1\nhandling = skip\nendif\nendif\n", ini);
        // captures are NOT gated: a pipeline switched back on mid-frame must have posed data to recover from
        Assert.Contains("hash = aaaa1111\nmatch_priority = 0\nResource_alpha_Posed = ref vb0\n", ini);
    }

    /// <summary>An overlay build with every retexture shape at once: a plain rebind, a keyed draw-scoped
    /// image over two anchor meshes, and a hide beside them — the emission the scoped probe, the tag
    /// section, the saves and the unconditional restores all belong to.</summary>
    private string RunScopedOverlayBuild()
    {
        string scopedDds = Path.Combine(_root, "scoped_skin.dds");
        FlatDds.Write(scopedDds, (90, 40, 30, 255));
        string plainDds = Path.Combine(_root, "plain_face.dds");
        FlatDds.Write(plainDds, (10, 20, 30, 255));

        string outDir = Path.Combine(_root, "sout");
        new MigotoEmitter().BuildOverlaysOnly(outDir,
            entries: new[] { new RetexEntry("ilse_face_a", "3ff9db6d", plainDds) },
            hideHashes: new[] { "eeee5555" },
            modKey: "F6",
            scopedEntries: new[]
            {
                new ScopedRetexEntry("skin_d", "a1b2c3d4", new[]
                {
                    new ScopedRetexImage(scopedDds, new[]
                    {
                        new ScopedAnchor("aaaa1111", "vesna_body_lod0"),
                        new ScopedAnchor("bbbb2222", "vesna_cloth_lod0"),
                    }, "F7"),
                }),
            });
        AssertEmittedIniIsClean(outDir);
        return outDir;
    }

    [Fact]
    public void Scoped_overlay_build_emits_the_pinned_text_contract()
    {
        string outDir = RunScopedOverlayBuild();
        bool regold = Environment.GetEnvironmentVariable("REMOLD_REGOLD") == "1";
        string emitted = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        string goldenPath = Path.Combine(GoldenDir(), "mod_scoped.ini");
        if (regold)
        {
            Directory.CreateDirectory(GoldenDir());
            File.WriteAllText(goldenPath, emitted);
        }
        else
        {
            Assert.True(File.Exists(goldenPath), $"golden asset missing: {goldenPath} (run once with REMOLD_REGOLD=1)");
            Assert.Equal(File.ReadAllText(goldenPath), emitted);
        }
        Assert.False(regold, "REMOLD_REGOLD run regenerated the goldens — rerun without it to compare");
    }

    [Fact]
    public void Full_build_emits_the_pinned_text_contract()
    {
        string outDir = RunFullBuild();
        bool regold = Environment.GetEnvironmentVariable("REMOLD_REGOLD") == "1";

        foreach (var name in new[] { "mod.ini", "union_swap.json" })
        {
            string emitted = File.ReadAllText(Path.Combine(outDir, name));
            string goldenPath = Path.Combine(GoldenDir(), name);
            if (regold)
            {
                Directory.CreateDirectory(GoldenDir());
                File.WriteAllText(goldenPath, emitted);
                continue;
            }
            Assert.True(File.Exists(goldenPath), $"golden asset missing: {goldenPath} (run once with REMOLD_REGOLD=1)");
            Assert.Equal(File.ReadAllText(goldenPath), emitted);
        }
        Assert.False(regold, "REMOLD_REGOLD run regenerated the goldens — rerun without it to compare");
    }

    [Fact]
    public void Full_build_emits_the_expected_file_inventory()
    {
        string outDir = RunFullBuild();
        var files = Directory.GetFiles(outDir).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(new[]
        {
            "alpha_cpinv.buf", "alpha_map_swap.buf", "alpha_off.buf", "alpha_sel.buf",
            "beta_cpinv.buf", "beta_map_swap.buf", "beta_off.buf", "beta_sel.buf",
            "combined_bind_swap.buf", "combined_ib_swap.buf", "combined_skin_swap.buf", "combined_vb1_swap.buf",
            "convert_cs_swap.hlsl", "mod.ini", "neutral_n.dds", "neutral_rmo.dds",
            "owner_part_swap.buf", "palette_seed_swap.buf", "recover_alpha_cs.hlsl", "recover_beta_cs.hlsl",
            "skin_cs_swap.hlsl", "sub1_n.dds", "sub1_r.dds", "sub1.dds", "tiefill_alpha_swap.hlsl",
            "union_swap.json",
        }, files);
    }
}
