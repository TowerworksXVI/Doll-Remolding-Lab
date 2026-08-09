using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// What a Replace's tier-2 key leaves on screen when it is off. The suppression of the character's own part
/// and the donor draw are gated separately, and the difference between the two gates IS the choice: sharing
/// one gate brings the original part back, dropping the tier-2 key from the suppression gate leaves the part
/// absent. The mod's tier-1 key stays on both gates either way.
/// </summary>
public class KeyOffModeEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-keyoff-" + Guid.NewGuid().ToString("N"));

    public KeyOffModeEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const uint A = 101, B = 102;

    /// <summary>One pipeline over alpha + beta, anchored at beta.</summary>
    private string Build(bool hideWhenOff, string? modKey = "F6", string? changeKey = "F8",
        string? latch = null, IReadOnlyList<string>? noSkip = null, PoolTier[]? tiers = null,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        string bd = Path.Combine(_root, "beta"); SyntheticPool.WritePartDump(bd, 2, 32, new[] { A, B });
        string outDir = Path.Combine(_root, name + (hideWhenOff ? "-hide" : "-vanilla"));
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            ToggleKey = modKey,
            Latches = latch is null ? null
                : new[] { new WitnessLatch(latch, new[] { "ffff9999" }) },
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    ToggleKey = changeKey,
                    HideWhenOff = hideWhenOff,
                    NoSkipParts = noSkip,
                    Tiers = tiers,
                    Latch = latch,
                },
            },
        });
        return File.ReadAllText(Path.Combine(outDir, "mod.ini"));
    }

    /// <summary>The section one ib hash owns, from its header through the newline ending its last line —
    /// so an assertion can pin a closing <c>endif</c> as a whole line.</summary>
    private static string Section(string ini, string header)
    {
        int at = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{header} is not in the emitted ini");
        return ini[at..(ini.IndexOf("\n\n", at, StringComparison.Ordinal) + 1)];
    }

    [Fact]
    public void Reverting_to_vanilla_gates_the_suppression_and_the_draw_on_the_same_two_keys()
    {
        string ini = Build(hideWhenOff: false);

        // the non-anchor part carries the suppression alone: both keys, and only one skip
        string alpha = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.Contains("if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nhandling = skip\nendif\nendif\n", alpha);
        Assert.Single(Regex.Matches(alpha, Regex.Escape("handling = skip")));

        // the anchor's draw sits under the identical gate, so the key takes both together
        string beta = Section(ini, "[TextureOverride_Cap_beta]");
        Assert.Contains("if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nhandling = skip\nendif\nendif\n", beta);
        Assert.Contains("if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nif $zz_done_swap == 0\n", beta);
    }

    [Fact]
    public void Hiding_when_off_drops_the_changes_key_from_the_suppression_and_keeps_it_on_the_draw()
    {
        string ini = Build(hideWhenOff: true);

        // the suppression answers to the MOD's key only, so an off change leaves nothing drawing there
        string alpha = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.Contains("if $zz_key_f6 == 1\nhandling = skip\nendif\n", alpha);
        Assert.DoesNotContain("if $zz_key_f8 == 1\nhandling = skip", alpha);

        string beta = Section(ini, "[TextureOverride_Cap_beta]");
        Assert.Contains("if $zz_key_f6 == 1\nhandling = skip\nendif\n", beta);
        // the donor draw and the compute that feeds it keep the change's own key
        Assert.Contains("if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nif $zz_done_swap == 0\n", beta);
        Assert.Contains("run = CommandListDraw_swap\nendif\nendif\n", beta);
    }

    /// <summary>The compute chain already sits inside the draw gate, so an off part dispatches nothing: the
    /// recover/convert/skin runs are never reached with the change's key down.</summary>
    [Fact]
    public void An_off_hiding_change_costs_no_dispatches()
    {
        string beta = Section(Build(hideWhenOff: true), "[TextureOverride_Cap_beta]");
        foreach (var run in new[] { "CustomShaderRecover_alpha_swap", "CustomShaderRecover_beta_swap",
                                    "CustomShaderConvertW_swap", "CustomShaderSkin_swap", "CommandListDraw_swap" })
            Assert.Contains($"if $zz_key_f8 == 1\n", beta[..beta.IndexOf(run, StringComparison.Ordinal)]);
    }

    /// <summary>Captures are ungated whichever the off state means, so the palette is valid the instant the
    /// key comes back on.</summary>
    [Fact]
    public void Captures_stay_ungated_in_both_meanings()
    {
        foreach (bool hide in new[] { false, true })
        {
            string alpha = Section(Build(hideWhenOff: hide), "[TextureOverride_Cap_alpha]");
            Assert.Contains("hash = aaaa0001\nmatch_priority = 0\nResource_alpha_Posed = ref vb0\nResource_alpha_CB = copy vs-cb1\n", alpha);
        }
    }

    /// <summary>The presence latch gates BOTH: an edit that only applies while its outfit is on screen must
    /// not hold another wearer's part suppressed.</summary>
    [Fact]
    public void The_presence_latch_stays_on_both_gates_when_the_change_hides()
    {
        string ini = Build(hideWhenOff: true, latch: "vesnassr01");
        string alpha = Section(ini, "[TextureOverride_Cap_alpha]");

        Assert.Contains("if $zz_key_f6 == 1\nif $zz_gate_vesnassr01 == 1\nhandling = skip\nendif\nendif\n", alpha);
        Assert.Contains("if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nif $zz_gate_vesnassr01 == 1\n",
            Section(ini, "[TextureOverride_Cap_beta]"));
    }

    /// <summary>A part the pipeline deliberately leaves running is not suppressed at all, so there is no
    /// suppression for the off state to hold open.</summary>
    [Fact]
    public void A_no_skip_part_is_never_suppressed_however_the_key_is_read()
    {
        string alpha = Section(Build(hideWhenOff: true, noSkip: new[] { "alpha" }), "[TextureOverride_Cap_alpha]");
        Assert.DoesNotContain("handling = skip", alpha);
    }

    /// <summary>A replaced tier follows its part: LOD choice is not distance-only, so a tier left drawing
    /// vanilla while lod0 is hidden would blink the original part back in.</summary>
    [Fact]
    public void A_replaced_tier_reads_the_same_off_state_as_its_part()
    {
        string td = Path.Combine(_root, "beta_l1"); SyntheticPool.WritePartDump(td, 3, 24, new[] { A, B });
        string ini = Build(hideWhenOff: true,
            tiers: new[] { new PoolTier("beta", "beta_lod1", "lod1", td, "bbbb0002") });
        string tier = Section(ini, "[TextureOverride_Cap_beta_lod1]");

        Assert.Contains("if $zz_key_f6 == 1\nhandling = skip\nendif\n", tier);
        Assert.Contains("if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nif $zz_done_swap_lod1 == 0\n", tier);
    }

    /// <summary>Without a key of its own the pipeline has no off state, so both meanings emit the mod key
    /// alone and the two gates are one gate.</summary>
    [Fact]
    public void An_unkeyed_pipeline_emits_one_gate_whichever_meaning_is_asked_for()
    {
        string vanilla = Build(hideWhenOff: false, changeKey: null);
        string hiding = Build(hideWhenOff: true, changeKey: null);

        Assert.Equal(vanilla, hiding);
        Assert.Contains("if $zz_key_f6 == 1\nhandling = skip\nendif\n",
            Section(vanilla, "[TextureOverride_Cap_alpha]"));
    }

    /// <summary>A part pooled by one pipeline that reverts to vanilla and one that hides carries BOTH gates
    /// in its single merged section: the skip is the OR, so the part stays suppressed while either applies.</summary>
    [Fact]
    public void A_part_pooled_by_both_meanings_carries_both_skips()
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        string sd = Path.Combine(_root, "shared"); SyntheticPool.WritePartDump(sd, 2, 32, new[] { A, B });
        string bd = Path.Combine(_root, "beta"); SyntheticPool.WritePartDump(bd, 3, 32, new[] { A, B });
        string outDir = Path.Combine(_root, "mixed");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            ToggleKey = "F6",
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "a",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("shared", sd) },
                    Anchor = "alpha",
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001", ["shared"] = "cccc0001" },
                    ToggleKey = "F8",
                },
                new ReplacePipeline
                {
                    Suffix = "b",
                    Parts = new[] { new PoolPart("shared", sd), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    CaptureHashes = new Dictionary<string, string> { ["shared"] = "cccc0001", ["beta"] = "bbbb0001" },
                    ToggleKey = "F9",
                    HideWhenOff = true,
                },
            },
        });
        string shared = Section(File.ReadAllText(Path.Combine(outDir, "mod.ini")), "[TextureOverride_Cap_shared]");

        Assert.Equal(2, Regex.Matches(shared, Regex.Escape("handling = skip")).Count);
        Assert.Contains("if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nhandling = skip\nendif\nendif\n", shared);
        Assert.Contains("if $zz_key_f6 == 1\nhandling = skip\nendif\n", shared);
        Assert.DoesNotContain("$zz_key_f9", shared);
    }

    // ---- how a key starts ----

    /// <summary>The declaration is the only place a start lives, and both build routes go through it: a key
    /// named as starting off is declared 0, everything else 1.</summary>
    [Fact]
    public void A_key_that_starts_off_is_declared_zero_on_both_build_routes()
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        string pooled = Path.Combine(_root, "pooled-start");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = pooled,
            ToggleKey = "F6",
            HideHashes = new[] { "cccc3333" },
            HideKeys = new Dictionary<string, string> { ["cccc3333"] = "F9" },
            KeysStartingOff = new[] { "f8", "F9" },     // normalized on the way in, as a key always is
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad) },
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                    ToggleKey = "F8",
                },
            },
        });
        string ini = File.ReadAllText(Path.Combine(pooled, "mod.ini"));
        Assert.Contains("global $zz_key_f6 = 1\n", ini);
        Assert.Contains("global $zz_key_f8 = 0\n", ini);
        Assert.Contains("global $zz_key_f9 = 0\n", ini);
        // the toggle itself is start-agnostic: the same flip serves either declared state
        Assert.Contains("[CommandListKey_zz_key_f8]\n$zz_key_f8 = 1 - $zz_key_f8\n", ini);

        string overlay = Path.Combine(_root, "overlay-start");
        new MigotoEmitter().BuildOverlaysOnly(overlay, entries: null,
            hideHashes: new[] { "dddd4444" }, modKey: "F6",
            hideKeys: new Dictionary<string, string> { ["dddd4444"] = "F9" },
            keysStartingOff: new[] { "F9" });
        string overlayIni = File.ReadAllText(Path.Combine(overlay, "mod.ini"));
        Assert.Contains("global $zz_key_f6 = 1\n", overlayIni);
        Assert.Contains("global $zz_key_f9 = 0\n", overlayIni);
    }

    /// <summary>Starts off + hides when off is how an optional part ships: nothing draws there until the
    /// first press, and the vanilla part never comes back on its own.</summary>
    [Fact]
    public void An_optional_part_starts_off_and_hidden()
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        string outDir = Path.Combine(_root, "optional");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            KeysStartingOff = new[] { "F8" },
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad) },
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                    ToggleKey = "F8",
                    HideWhenOff = true,
                },
            },
        });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains("global $zz_key_f8 = 0\n", ini);
        string alpha = Section(ini, "[TextureOverride_Cap_alpha]");
        // no mod key here, so the suppression is unconditional and the draw waits on the press
        Assert.Contains("hash = aaaa0001\nmatch_priority = 0\nResource_alpha_Posed = ref vb0\nResource_alpha_CB = copy vs-cb1\n"
            + "handling = skip\nif $zz_key_f8 == 1\n", alpha);
    }

    // ---- the rigid route ----
    // A rigid replacement's section holds both roles: the suppression of the vanilla draw and the donor draw
    // that stands in for it. They take the same two gates the pooled route gives its skip and its chain.

    /// <summary>A compiled donor dir the rigid emission consumes: one submesh, positions only.</summary>
    private string RigidDonor()
    {
        string dir = Path.Combine(_root, "rigid-donor");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), new byte[3 * 12]);
        File.WriteAllBytes(Path.Combine(dir, "ib.buf"), new byte[] { 0, 0, 1, 0, 2, 0 });
        File.WriteAllText(Path.Combine(dir, "meta.json"),
            "{\n  \"mesh\": \"donor\", \"verts\": 3, \"boneCount\": 0,\n"
            + "  \"indexFormat\": \"R16_UINT\", \"indexBufferBytes\": 6,\n"
            + "  \"streams\": [{ \"stream\": 0, \"stride\": 12 }],\n"
            + "  \"submeshes\": [{ \"firstByte\": 0, \"indexCount\": 3, \"baseVertex\": 0 }]\n}\n");
        return dir;
    }

    /// <summary>One rigid replacement with a lod1 tier, gated as the caller asks.</summary>
    private string BuildRigid(bool hideWhenOff, string? modKey = "F6", string? changeKey = "F8",
        string? latch = null, IReadOnlyCollection<string>? startsOff = null,
        [System.Runtime.CompilerServices.CallerMemberName] string name = "")
    {
        string outDir = Path.Combine(_root, name + (hideWhenOff ? "-hide" : "-vanilla"));
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            Pipelines = Array.Empty<ReplacePipeline>(),
            OutDir = outDir,
            ToggleKey = modKey,
            KeysStartingOff = startsOff,
            Latches = latch is null ? null
                : new[] { new WitnessLatch(latch, new[] { "ffff9999" }) },
            Rigids = new[]
            {
                new RigidReplace
                {
                    Suffix = "frame",
                    DonorDir = RigidDonor(),
                    Hash = "aaaa0001",
                    TierHashes = new[] { "aaaa0002" },
                    ToggleKey = changeKey,
                    HideWhenOff = hideWhenOff,
                    Latch = latch,
                },
            },
        });
        return File.ReadAllText(Path.Combine(outDir, "mod.ini"));
    }

    /// <summary>Reverting to vanilla keeps ONE gate around both roles, which is the emission a rigid
    /// replacement has always had.</summary>
    [Fact]
    public void A_rigid_replacement_reverting_to_vanilla_wraps_both_roles_in_one_gate()
    {
        string ini = BuildRigid(hideWhenOff: false);

        Assert.Equal("[TextureOverride_Rigid_frame]\nhash = aaaa0001\nmatch_priority = 0\n"
            + "if $zz_key_f6 == 1\nif $zz_key_f8 == 1\n"
            + "handling = skip\nrun = CommandListRigid_frame\n"
            + "endif\nendif\n",
            Section(ini, "[TextureOverride_Rigid_frame]"));
    }

    /// <summary>Hiding when off drops the change's key from the suppression and keeps it on the donor draw,
    /// so an off change leaves nothing drawing there.</summary>
    [Fact]
    public void A_rigid_replacement_hiding_when_off_gates_its_skip_and_its_draw_apart()
    {
        string ini = BuildRigid(hideWhenOff: true);

        Assert.Equal("[TextureOverride_Rigid_frame]\nhash = aaaa0001\nmatch_priority = 0\n"
            + "if $zz_key_f6 == 1\nhandling = skip\nendif\n"
            + "if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nrun = CommandListRigid_frame\nendif\nendif\n",
            Section(ini, "[TextureOverride_Rigid_frame]"));
    }

    /// <summary>A replaced tier follows its part, as a pooled one does: a tier left drawing vanilla while
    /// lod0 is hidden would blink the original mesh back in wherever the game picks it.</summary>
    [Fact]
    public void A_rigid_tier_section_reads_the_same_off_state_as_its_part()
    {
        string ini = BuildRigid(hideWhenOff: true);

        Assert.Equal("[TextureOverride_Rigid_frame_1]\nhash = aaaa0002\nmatch_priority = 0\n"
            + "if $zz_key_f6 == 1\nhandling = skip\nendif\n"
            + "if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nrun = CommandListRigid_frame\nendif\nendif\n",
            Section(ini, "[TextureOverride_Rigid_frame_1]"));
    }

    /// <summary>Without a key of its own a rigid replacement has no off state, so both meanings emit the mod
    /// key alone and the two gates are one gate.</summary>
    [Fact]
    public void An_unkeyed_rigid_replacement_emits_one_gate_whichever_meaning_is_asked_for()
    {
        string vanilla = BuildRigid(hideWhenOff: false, changeKey: null);
        string hiding = BuildRigid(hideWhenOff: true, changeKey: null);

        Assert.Equal(vanilla, hiding);
        Assert.Equal("[TextureOverride_Rigid_frame]\nhash = aaaa0001\nmatch_priority = 0\n"
            + "if $zz_key_f6 == 1\nhandling = skip\nrun = CommandListRigid_frame\nendif\n",
            Section(vanilla, "[TextureOverride_Rigid_frame]"));
    }

    /// <summary>The presence latch gates BOTH: a replacement that only applies while its outfit is on screen
    /// must not hold another wearer's draw suppressed.</summary>
    [Fact]
    public void The_presence_latch_stays_on_both_rigid_gates_when_the_change_hides()
    {
        string ini = BuildRigid(hideWhenOff: true, latch: "vesnassr01");

        Assert.Equal("[TextureOverride_Rigid_frame]\nhash = aaaa0001\nmatch_priority = 0\n"
            + "if $zz_key_f6 == 1\nif $zz_gate_vesnassr01 == 1\nhandling = skip\nendif\nendif\n"
            + "if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nif $zz_gate_vesnassr01 == 1\n"
            + "run = CommandListRigid_frame\nendif\nendif\nendif\n",
            Section(ini, "[TextureOverride_Rigid_frame]"));
    }

    /// <summary>A sighting recorded in the replacement's OWN section stays outside both gates: a latch whose
    /// witness stopped recording while a key was off would read the outfit as absent the frame it comes back
    /// on.</summary>
    [Fact]
    public void A_rigid_sections_own_sighting_stays_above_both_gates_when_the_change_hides()
    {
        string outDir = Path.Combine(_root, "rigid-sighting");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            Pipelines = Array.Empty<ReplacePipeline>(),
            OutDir = outDir,
            ToggleKey = "F6",
            Latches = new[] { new WitnessLatch("vesnassr01", new[] { "aaaa0001" }) },
            Rigids = new[]
            {
                new RigidReplace
                {
                    Suffix = "frame", DonorDir = RigidDonor(), Hash = "aaaa0001",
                    ToggleKey = "F8", HideWhenOff = true, Latch = "vesnassr01",
                },
            },
        });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Equal("[TextureOverride_Rigid_frame]\nhash = aaaa0001\nmatch_priority = 0\n"
            + "$zz_seen_vesnassr01 = 1\n"
            + "if $zz_key_f6 == 1\nif $zz_gate_vesnassr01 == 1\nhandling = skip\nendif\nendif\n"
            + "if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nif $zz_gate_vesnassr01 == 1\n"
            + "run = CommandListRigid_frame\nendif\nendif\nendif\n",
            Section(ini, "[TextureOverride_Rigid_frame]"));
    }

    /// <summary>Starts off + hides when off on a rigid change: the suppression is live at launch and the
    /// donor draw waits on the first press, so the part is simply absent until it is asked for.</summary>
    [Fact]
    public void An_optional_rigid_part_starts_off_and_hidden()
    {
        string ini = BuildRigid(hideWhenOff: true, modKey: null, startsOff: new[] { "F8" });

        Assert.Contains("global $zz_key_f8 = 0\n", ini);
        Assert.Equal("[TextureOverride_Rigid_frame]\nhash = aaaa0001\nmatch_priority = 0\n"
            + "handling = skip\n"
            + "if $zz_key_f8 == 1\nrun = CommandListRigid_frame\nendif\n",
            Section(ini, "[TextureOverride_Rigid_frame]"));
    }

    /// <summary>A hide has no replacement of its own, so vanilla is its only off state: its section keeps
    /// its own key on the one gate it has.</summary>
    [Fact]
    public void A_hide_keeps_its_own_key_on_its_suppression()
    {
        string outDir = Path.Combine(_root, "hides");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { "dddd4444" }, modKey: "F6",
            hideKeys: new Dictionary<string, string> { ["dddd4444"] = "F9" });

        Assert.Contains("hash = dddd4444\nmatch_priority = 0\nif $zz_key_f6 == 1\nif $zz_key_f9 == 1\nhandling = skip\nendif\nendif\n",
            File.ReadAllText(Path.Combine(outDir, "mod.ini")));
    }
}
