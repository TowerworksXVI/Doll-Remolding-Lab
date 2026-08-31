using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// What a key group longer than two states emits, and what a part another group takes off screen emits.
/// Both are text contracts: the two builds below are pinned byte-for-byte in <c>Migoto/golden/</c> beside
/// the pooled and overlay ones, and regenerated with <c>REMOLD_REGOLD=1</c> for one run.
/// </summary>
public class KeyCycleEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "gf2-cycle-" + Guid.NewGuid().ToString("N"));

    public KeyCycleEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static string GoldenDir([System.Runtime.CompilerServices.CallerFilePath] string self = "") =>
        Path.Combine(Path.GetDirectoryName(self)!, "golden");

    private static void Golden(string outDir, string name)
    {
        bool regold = Environment.GetEnvironmentVariable("REMOLD_REGOLD") == "1";
        string emitted = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        string path = Path.Combine(GoldenDir(), name);
        if (regold)
        {
            Directory.CreateDirectory(GoldenDir());
            File.WriteAllText(path, emitted);
        }
        else
        {
            Assert.True(File.Exists(path),
                $"golden asset missing: {path} (run once with REMOLD_REGOLD=1)");
            Assert.Equal(File.ReadAllText(path), emitted);
        }
        Assert.False(regold, "REMOLD_REGOLD run regenerated the goldens — rerun without it to compare");
    }

    /// <summary>One part, three answers: the same two pool parts replaced by a donor of its own in each of
    /// the three positions key F7 cycles. The three pipelines share the part's capture hashes, so the one
    /// section that hash owns carries all three answers — three guarded skips and three gated draws.</summary>
    private string RunCycleBuild()
    {
        string dumps = Path.Combine(_root, "cdumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        SyntheticPool.WritePartDump(Path.Combine(dumps, "beta"), seed: 60, verts: 64,
            boneHashes: new uint[] { 201, 202 });
        var pipelines = new List<ReplacePipeline>();
        for (int state = 0; state < 3; state++)
        {
            string donor = Path.Combine(_root, $"cdonor{state}");
            SyntheticPool.WriteDonor(donor, verts: 6 + state, unionBones: 4);
            pipelines.Add(new ReplacePipeline
            {
                Suffix = $"swap_s{state}",
                Parts = new[]
                {
                    new PoolPart("alpha", Path.Combine(dumps, "alpha")),
                    new PoolPart("beta", Path.Combine(dumps, "beta")),
                },
                DonorDir = donor,
                CaptureHashes = new Dictionary<string, string>
                    { ["alpha"] = "aaaa1111", ["beta"] = "bbbb2222" },
                ToggleKey = new KeyRef("F7", state),
            });
        }

        string outDir = Path.Combine(_root, "cout");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = pipelines,
            KeyCycles = new[] { new KeyCycle("F7", 3, 0) },
        });
        ModBuilderTests.AssertNoDuplicateSections(File.ReadAllText(Path.Combine(outDir, "mod.ini")));
        return outDir;
    }

    /// <summary>A two-state group owning one part's content, and a SECOND group whose second position takes
    /// that same part off screen. The hider flag is what carries the or-of-hiders into the content gate;
    /// the vanilla draw it closes over is suppressed by a guarded skip of its own.
    /// <para>Both pool parts carry a lod1 tier, because LOD choice is not distance-only: what the hiding
    /// position owes the part's OTHER draws is the same thing it owes the lod0 one, and a build without
    /// tiers cannot state that.</para></summary>
    private string RunStackedHideBuild()
    {
        string dumps = Path.Combine(_root, "hdumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        SyntheticPool.WritePartDump(Path.Combine(dumps, "beta"), seed: 60, verts: 64,
            boneHashes: new uint[] { 201, 202 });
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha_lod1"), seed: 20, verts: 32,
            boneHashes: new uint[] { 101, 102 });
        SyntheticPool.WritePartDump(Path.Combine(dumps, "beta_lod1"), seed: 70, verts: 32,
            boneHashes: new uint[] { 201, 202 });
        string donor = Path.Combine(_root, "hdonor");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 4);

        string outDir = Path.Combine(_root, "hout");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            ToggleKey = "F6",
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[]
                    {
                        new PoolPart("alpha", Path.Combine(dumps, "alpha")),
                        new PoolPart("beta", Path.Combine(dumps, "beta")),
                    },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa1111", ["beta"] = "bbbb2222" },
                    Tiers = new[]
                    {
                        new PoolTier("alpha", "alpha_lod1", "lod1",
                            Path.Combine(dumps, "alpha_lod1"), "aaaa3333"),
                        new PoolTier("beta", "beta_lod1", "lod1",
                            Path.Combine(dumps, "beta_lod1"), "bbbb4444"),
                    },
                    ToggleKey = new KeyRef("F7", 0),
                    HiddenBy = "vesna_ssr01_body",
                    // the hidden part is the one this pipeline replaces; its pool mate keeps its own
                    // vanilla draw while the other group stands in its hiding position
                    SuppressWhen = new Dictionary<string, IReadOnlyList<KeyRef>>
                        { ["beta"] = new[] { new KeyRef("F8", 1) } },
                },
            },
            KeyCycles = new[] { new KeyCycle("F7", 2, 0), new KeyCycle("F8", 2, 0) },
            HiddenFlags = new[]
            {
                new HiddenFlag("vesna_ssr01_body", new[] { new KeyRef("F8", 1) }),
            },
        });
        ModBuilderTests.AssertNoDuplicateSections(File.ReadAllText(Path.Combine(outDir, "mod.ini")));
        return outDir;
    }

    [Fact]
    public void A_three_state_cycle_emits_the_pinned_text_contract() =>
        Golden(RunCycleBuild(), "mod_cycle.ini");

    [Fact]
    public void A_stacked_hide_emits_the_pinned_text_contract() =>
        Golden(RunStackedHideBuild(), "mod_stacked_hide.ini");

    /// <summary>One key, one variable, three positions: declared where the cycle launches, stepped and
    /// wrapped on each press. A longer cycle is not three keys and not three variables.</summary>
    [Fact]
    public void A_three_state_group_declares_one_variable_and_steps_it_through_its_cycle()
    {
        string ini = File.ReadAllText(Path.Combine(RunCycleBuild(), "mod.ini"));

        Assert.Single(Regex.Matches(ini, Regex.Escape("global $zz_key_f7 = ")));
        Assert.Contains("global $zz_key_f7 = 0\n", ini);
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Key_zz_key_f7]")));
        Assert.Contains("[CommandListKey_zz_key_f7]\n$zz_key_f7 = $zz_key_f7 + 1\n"
            + "if $zz_key_f7 == 3\n$zz_key_f7 = 0\nendif\n", ini);
        // nothing carries a press into the next launch: the declaration above is where every session starts
        Assert.DoesNotContain("persist", ini);
    }

    /// <summary>Each position's content draws only while the key stands in it. The vanilla draw is
    /// suppressed in every one of them, and a set of skips covering the WHOLE cycle says exactly what one
    /// bare skip says — so it is written once. Which positions suppress is still checkable: it is every
    /// one of them, which is what the absent gate states.</summary>
    [Fact]
    public void Each_position_of_the_cycle_gates_its_own_content()
    {
        string ini = File.ReadAllText(Path.Combine(RunCycleBuild(), "mod.ini"));
        string section = Section(ini, "[TextureOverride_Cap_beta]");

        for (int state = 0; state < 3; state++)
        {
            Assert.Contains($"if $zz_key_f7 == {state}\nif $zz_done_swap_s{state} == 0\n", section);
            Assert.Contains($"run = CommandListDraw_swap_s{state}\nendif\n", section);
        }
        // every position suppresses, so the suppression is stated once and carries no key term
        Assert.Single(Regex.Matches(section, Regex.Escape("handling = skip")));
        Assert.DoesNotContain("handling = skip\nendif", section);
        // the only test against 3 is the step's own wrap; nothing gates on a position off the end
        Assert.DoesNotContain("if $zz_key_f7 == 3", section);
    }

    /// <summary>The hider flag: declared with the keys, recomputed from <c>[Constants]</c> so a session
    /// opens with the answer its launch positions imply, and recomputed again at the end of EVERY key's
    /// command list, since any press can change any flag.</summary>
    [Fact]
    public void A_stacked_hide_recomputes_its_flag_at_load_and_after_every_press()
    {
        string ini = File.ReadAllText(Path.Combine(RunStackedHideBuild(), "mod.ini"));

        Assert.Contains("global $zz_hid_vesna_ssr01_body = 0\n", ini);
        // the run sits after the key declarations, so the flag is computed from the positions just written
        Assert.Contains("global $zz_key_f8 = 0\nrun = CommandListRecomputeHidden\n", ini);
        Assert.Contains("[CommandListRecomputeHidden]\n$zz_hid_vesna_ssr01_body = 0\n"
            + "if $zz_key_f8 == 1\n$zz_hid_vesna_ssr01_body = 1\nendif\n", ini);
        foreach (var v in new[] { "zz_key_f6", "zz_key_f7", "zz_key_f8" })
            Assert.Contains($"[CommandListKey_{v}]\n", ini);
        // one per key section plus the one in [Constants]
        Assert.Equal(4, Regex.Matches(ini, Regex.Escape("run = CommandListRecomputeHidden")).Count);
    }

    /// <summary>Hidden outranks content: the flag joins the DRAW gate, and the vanilla draw the content
    /// gate just closed over is suppressed by its own guarded skip, so the hiding position leaves nothing
    /// on screen rather than handing the game's part back.</summary>
    [Fact]
    public void A_stacked_hide_gates_the_draw_on_its_flag_and_suppresses_the_vanilla_beside_it()
    {
        string ini = File.ReadAllText(Path.Combine(RunStackedHideBuild(), "mod.ini"));
        string section = Section(ini, "[TextureOverride_Cap_beta]");

        // the content's own position, then the flag: an ordinal test either way, no negation form needed
        Assert.Contains("if $zz_key_f6 == 0\nif $zz_key_f7 == 0\nif $zz_hid_vesna_ssr01_body == 0\n"
            + "if $zz_done_swap == 0\n", section);
        // its own position suppresses the vanilla draw, and so does the other group's hiding position.
        // The flag is NOT on the suppression: what the hiding position does to the vanilla draw is that
        // position's own guarded skip to say.
        Assert.Contains("if $zz_key_f6 == 0\nif $zz_key_f7 == 0\n"
            + "handling = skip\nendif\nendif\n", section);
        Assert.Contains("if $zz_key_f6 == 0\nif $zz_key_f8 == 1\nhandling = skip\nendif\nendif\n", section);
        Assert.Equal(2, Regex.Matches(section, Regex.Escape("handling = skip")).Count);
        // the pool MATE this pipeline only leans on for bones is not hidden by that position: it keeps its
        // own vanilla draw, suppressed by the pipeline's content gate and nothing else
        string mate = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.Contains("if $zz_key_f6 == 0\nif $zz_key_f7 == 0\nhandling = skip\nendif\nendif\n", mate);
        Assert.Single(Regex.Matches(mate, Regex.Escape("handling = skip")));
        Assert.DoesNotContain("$zz_key_f8", mate);
        // the flag gates the draw, never the capture: a part hidden this frame must still have posed data
        // to recover from the frame it comes back
        Assert.Contains("Resource_beta_Posed = ref vb0\n", section);
        Assert.DoesNotContain("if $zz_hid_vesna_ssr01_body == 0\nResource_beta_Posed", section);
    }

    /// <summary>The hiding position reaches the part's OTHER LOD draws too. LOD choice is not
    /// distance-only, so a tier left running would hand the part back the moment the renderer picked it —
    /// the part would vanish up close and stand at distance, in a position that asked for it gone. The
    /// tier section carries the same two skips its lod0 does, from the same per-part terms.</summary>
    [Fact]
    public void A_stacked_hide_suppresses_the_hidden_parts_tiers_as_well_as_its_lod0()
    {
        string ini = File.ReadAllText(Path.Combine(RunStackedHideBuild(), "mod.ini"));
        string tier = Section(ini, "[TextureOverride_Cap_beta_lod1]");

        // this pipeline's own gate, exactly as at lod0
        Assert.Contains("if $zz_key_f6 == 0\nif $zz_key_f7 == 0\n"
            + "handling = skip\nendif\nendif\n", tier);
        // and the other group's hiding position, which is what the lod0 section alone used to state
        Assert.Contains("if $zz_key_f6 == 0\nif $zz_key_f8 == 1\nhandling = skip\nendif\nendif\n", tier);
        Assert.Equal(2, Regex.Matches(tier, Regex.Escape("handling = skip")).Count);
        // the capture is never gated: a tier hidden this frame still owes recovery input the frame it
        // comes back, the same rule the lod0 capture holds to
        Assert.Contains("Resource_beta_lod1_Posed = ref vb0\n", tier);
        Assert.DoesNotContain("if $zz_key_f8 == 1\nResource_beta_lod1_Posed", tier);
    }

    /// <summary>The negative that keeps the skip PER PART: a tier of a part no position hides carries this
    /// pipeline's gate and nothing else. A hider broadcast across the pool would take the wrong part off
    /// screen at distance.</summary>
    [Fact]
    public void A_tier_of_an_unhidden_pool_part_carries_only_its_pipelines_own_gate()
    {
        string ini = File.ReadAllText(Path.Combine(RunStackedHideBuild(), "mod.ini"));
        string tier = Section(ini, "[TextureOverride_Cap_alpha_lod1]");

        Assert.Contains("if $zz_key_f6 == 0\nif $zz_key_f7 == 0\n"
            + "handling = skip\nendif\nendif\n", tier);
        Assert.Single(Regex.Matches(tier, Regex.Escape("handling = skip")));
        Assert.DoesNotContain("$zz_key_f8", tier);
    }

    /// <summary>A hider holding in EVERY position of its key says nothing about that key, at a tier as at
    /// lod0: the set of guarded skips collapses to the one bare skip it is equal to. The part here is a
    /// leave part, so this pipeline's own gate adds no skip of its own and the hider's set stands alone —
    /// which is the shape the collapse can be read off.</summary>
    [Fact]
    public void A_tier_hidden_in_every_position_of_a_key_states_its_skip_once()
    {
        string dumps = Path.Combine(_root, "edumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        SyntheticPool.WritePartDump(Path.Combine(dumps, "beta"), seed: 60, verts: 64,
            boneHashes: new uint[] { 201, 202 });
        SyntheticPool.WritePartDump(Path.Combine(dumps, "beta_lod1"), seed: 70, verts: 32,
            boneHashes: new uint[] { 201, 202 });
        string donor = Path.Combine(_root, "edonor");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 4);

        string outDir = Path.Combine(_root, "eout");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[]
                    {
                        new PoolPart("alpha", Path.Combine(dumps, "alpha")),
                        new PoolPart("beta", Path.Combine(dumps, "beta")),
                    },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa1111", ["beta"] = "bbbb2222" },
                    Tiers = new[]
                    {
                        new PoolTier("beta", "beta_lod1", "lod1",
                            Path.Combine(dumps, "beta_lod1"), "bbbb4444"),
                    },
                    // beta is leaned on for bones only: this pipeline suppresses none of its draws
                    NoSkipParts = new[] { "beta" },
                    // …but the other group takes it off screen in BOTH of its positions
                    SuppressWhen = new Dictionary<string, IReadOnlyList<KeyRef>>
                        { ["beta"] = new[] { new KeyRef("F8", 0), new KeyRef("F8", 1) } },
                },
            },
            KeyCycles = new[] { new KeyCycle("F8", 2, 0) },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        foreach (var name in new[] { "[TextureOverride_Cap_beta]", "[TextureOverride_Cap_beta_lod1]" })
        {
            string section = Section(ini, name);
            Assert.Contains("handling = skip\n", section);
            Assert.Single(Regex.Matches(section, Regex.Escape("handling = skip")));
            // stated once means stated unconditionally — no position of F8 is named
            Assert.DoesNotContain("$zz_key_f8", section);
        }
    }

    /// <summary>A three-position key whose FIRST and LAST positions answer with the same change. One
    /// pipeline, one payload, and a content flag standing in for the position term: the recompute raises
    /// it in each answering position, and the draw gate tests the flag rather than naming one of them.
    /// The middle position leaves the flag down, so the vanilla draws there.</summary>
    [Fact]
    public void One_change_answering_two_positions_gates_on_its_content_flag()
    {
        string dumps = Path.Combine(_root, "sdumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "sdonor");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2);

        string outDir = Path.Combine(_root, "sout");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                    // no position term of its own: the flag names every position that answers with it
                    ShownBy = "vesna_ssr01_body",
                },
            },
            KeyCycles = new[] { new KeyCycle("F7", 3, 0) },
            ShownFlags = new[]
            {
                new ShownFlag("vesna_ssr01_body",
                    new[] { new KeyRef("F7", 0), new KeyRef("F7", 2) }),
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.Contains("global $zz_shw_vesna_ssr01_body = 0\n", ini);
        Assert.Contains("global $zz_key_f7 = 0\nrun = CommandListRecomputeHidden\n", ini);
        Assert.Contains("[CommandListRecomputeHidden]\n$zz_shw_vesna_ssr01_body = 0\n"
            + "if $zz_key_f7 == 0\n$zz_shw_vesna_ssr01_body = 1\nendif\n"
            + "if $zz_key_f7 == 2\n$zz_shw_vesna_ssr01_body = 1\nendif\n", ini);
        // the payload is emitted once, and its section gates on the flag rather than on a position
        string section = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.Contains("if $zz_shw_vesna_ssr01_body == 1\nhandling = skip\nendif\n", section);
        Assert.Contains("if $zz_shw_vesna_ssr01_body == 1\nif $zz_done_swap == 0\n", section);
        Assert.DoesNotContain("if $zz_key_f7 ==", section);
        Assert.Single(Regex.Matches(ini, Regex.Escape("run = CommandListDraw_swap")));
    }

    /// <summary>A section body, up to the blank line that ends it.</summary>
    private static string Section(string ini, string header)
    {
        int at = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{header} is not in the emitted ini");
        return ini[at..(ini.IndexOf("\n\n", at, StringComparison.Ordinal) + 1)];
    }
}
