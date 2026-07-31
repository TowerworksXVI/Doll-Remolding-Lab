using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.App.ViewModels;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Migoto;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Toggle keys end to end on the data side: what a key string is allowed to be, that the mod's key and each
/// change's survive a save/open, that a removed subject takes its keys with it, and that everything sharing
/// one key can say what else moves with it.
/// </summary>
public class ToggleKeyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-keys-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ---- ModKeys ----

    [Theory]
    [InlineData("f6", "F6")]
    [InlineData("  ctrl   shift h ", "CTRL SHIFT H")]
    [InlineData("VK_OEM_3", "VK_OEM_3")]
    public void A_key_normalizes_to_the_tokens_an_ini_line_takes(string typed, string expected) =>
        Assert.Equal(expected, ModKeys.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("F6;")]          // an ini comment/separator character would break the emitted line
    [InlineData("ctrl+f6")]
    [InlineData("VK OEM 3")]     // the tokens of one key name, space-separated: not a modifier + key
    [InlineData("CTRL_F6")]      // a modifier joined into the key token, which no key name does
    [InlineData("SHIFT_H")]
    public void A_blank_or_unusable_key_normalizes_to_no_key(string? typed) =>
        Assert.Null(ModKeys.Normalize(typed));

    [Theory]
    [InlineData("CTRL SHIFT H")]
    [InlineData("alt f4")]
    [InlineData("VK_OEM_3")]
    public void A_modifier_run_and_a_multi_token_key_name_both_stay_usable(string typed) =>
        Assert.NotNull(ModKeys.Normalize(typed));

    [Fact]
    public void One_key_is_one_variable_however_it_was_typed()
    {
        Assert.Equal(ModKeys.VariableFor("F6"), ModKeys.VariableFor("f6"));
        Assert.NotEqual(ModKeys.VariableFor("F6"), ModKeys.VariableFor("CTRL F6"));
        Assert.True(ModKeys.SameKey("f6", "F6"));
        Assert.False(ModKeys.SameKey("f6", null));
        Assert.False(ModKeys.SameKey(null, null));   // no key is not "the same key" as no key
    }

    /// <summary>The variable is derived by folding the token separator to <c>_</c>, so two DIFFERENT keys
    /// that fold the same way would collapse into one <c>[Constants]</c>/<c>[Key]</c> declaration and
    /// switch together with nothing said. Every shape that could do that is refused by
    /// <see cref="ModKeys.Normalize"/>, which is what makes this hold.</summary>
    [Theory]
    [InlineData("VK_OEM_3", "VK OEM 3")]
    [InlineData("CTRL F6", "CTRL_F6")]
    [InlineData("CTRL SHIFT H", "CTRL_SHIFT_H")]
    public void Two_keys_that_would_share_one_variable_are_never_both_usable(string a, string b)
    {
        var na = ModKeys.Normalize(a);
        var nb = ModKeys.Normalize(b);
        if (na is null || nb is null) return;                     // one of them is refused: no collision
        Assert.Equal(na, nb);                                     // else they are the SAME key
        Assert.Equal(ModKeys.VariableFor(a), ModKeys.VariableFor(b));
    }

    // ---- persistence ----

    [Fact]
    public void Version_description_and_every_key_survive_a_save_and_open()
    {
        var proj = new ModProject { RootDir = Path.Combine(_root, "mod") };
        proj.Info.Name = "test mod";
        proj.Info.Version = "2.1";
        proj.Info.Description = "line one\nline two";
        proj.Info.ToggleKey = "CTRL F7";
        proj.SetChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace, "f6");
        proj.SetChangeKey("Vesna", "VesnaSSR01", "c_hair_lod0", EditVerbs.Hide, "F8");
        proj.Save();

        var loaded = ModProject.Load(proj.RootDir!);

        Assert.Equal("2.1", loaded.Info.Version);
        Assert.Equal("line one\nline two", loaded.Info.Description);
        Assert.Equal("CTRL F7", loaded.Info.ToggleKey);
        Assert.Equal("F6", loaded.GetChangeKey("vesna", "vesnassr01", "C_BODY_LOD0", EditVerbs.Replace));
        Assert.Equal("F8", loaded.GetChangeKey("Vesna", "VesnaSSR01", "c_hair_lod0", EditVerbs.Hide));
        // the verb is part of the identity, exactly as it is for a build exclusion
        Assert.Null(loaded.GetChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Retexture));
    }

    [Fact]
    public void Rebinding_a_change_replaces_its_key_and_clearing_removes_the_entry()
    {
        var proj = new ModProject();
        proj.SetChangeKey("Vesna", "VesnaSSR01", "body", EditVerbs.Replace, "F6");
        proj.SetChangeKey("Vesna", "VesnaSSR01", "body", EditVerbs.Replace, "F7");
        Assert.Equal("F7", Assert.Single(proj.ChangeKeys).Key);

        proj.SetChangeKey("Vesna", "VesnaSSR01", "body", EditVerbs.Replace, null);
        Assert.Empty(proj.ChangeKeys);

        // an unusable string clears rather than persisting something the emitter would have to refuse
        proj.SetChangeKey("Vesna", "VesnaSSR01", "body", EditVerbs.Replace, "F6");
        proj.SetChangeKey("Vesna", "VesnaSSR01", "body", EditVerbs.Replace, "ctrl+f6");
        Assert.Empty(proj.ChangeKeys);
    }

    /// <summary>What a key means when it is off travels with the key: it round-trips, a rebind that names
    /// no meaning sets none, and clearing the key takes it away with the entry.</summary>
    [Fact]
    public void The_off_meaning_rides_the_key_through_a_save_a_rebind_and_a_clear()
    {
        var proj = new ModProject { RootDir = Path.Combine(_root, "offmode") };
        proj.SetChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace, "F6", hideWhenOff: true);
        proj.SetChangeKey("Vesna", "VesnaSSR01", "c_hair_lod0", EditVerbs.Replace, "F7");
        proj.Save();

        var loaded = ModProject.Load(proj.RootDir!);
        Assert.True(loaded.FindChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace)!.HideWhenOff);
        Assert.False(loaded.FindChangeKey("Vesna", "VesnaSSR01", "c_hair_lod0", EditVerbs.Replace)!.HideWhenOff);

        // the whole binding is stated on every write, so a rebind that names no meaning carries none
        loaded.SetChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace, "F8");
        Assert.False(loaded.FindChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace)!.HideWhenOff);

        loaded.SetChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace, "F8", hideWhenOff: true);
        loaded.SetChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace, null);
        Assert.Null(loaded.FindChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace));
    }

    /// <summary>An unmodified binding writes no off-meaning field, so a project that never touched the
    /// control keeps the manifest it had.</summary>
    [Fact]
    public void A_default_off_meaning_and_start_write_nothing_into_the_manifest()
    {
        var proj = new ModProject { RootDir = Path.Combine(_root, "offmode-default") };
        proj.SetChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace, "F6");
        proj.Save();

        string manifest = File.ReadAllText(ModProject.ManifestPathFor(proj.RootDir!));
        Assert.DoesNotContain("hide_when_off", manifest);
        Assert.DoesNotContain("starts_off", manifest);
    }

    /// <summary>How a change starts rides its key exactly as the off meaning does: it round-trips, a rebind
    /// that names it not sets it not, and clearing the key takes it with the entry.</summary>
    [Fact]
    public void The_start_state_rides_the_key_through_a_save_and_a_rebind()
    {
        var proj = new ModProject { RootDir = Path.Combine(_root, "startstate") };
        proj.SetChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace, "F6",
            hideWhenOff: true, startsOff: true);
        proj.Save();

        var loaded = ModProject.Load(proj.RootDir!);
        var binding = loaded.FindChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace)!;
        Assert.True(binding.StartsOff);
        Assert.True(binding.HideWhenOff);

        loaded.SetChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace, "F6");
        Assert.False(loaded.FindChangeKey("Vesna", "VesnaSSR01", "c_body_lod0", EditVerbs.Replace)!.StartsOff);
    }

    [Fact]
    public void Removing_a_subject_takes_its_keys_with_it()
    {
        var proj = new ModProject { RootDir = Path.Combine(_root, "sub") };
        proj.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        proj.SetChangeKey("Vesna", "VesnaSSR01", "body", EditVerbs.Replace, "F6");
        proj.SetChangeKey("Other", "OtherSSR01", "body", EditVerbs.Replace, "F7");

        SubjectRemoval.Remove(proj, "Vesna", "VesnaSSR01", "c_vesna01");

        Assert.Null(proj.GetChangeKey("Vesna", "VesnaSSR01", "body", EditVerbs.Replace));
        Assert.Equal("F7", proj.GetChangeKey("Other", "OtherSSR01", "body", EditVerbs.Replace));
    }

    // ---- the shared-key state each control carries ----

    [Fact]
    public void Two_changes_on_one_key_each_name_the_other()
    {
        var tips = KeyCollisions.Tips(new (string, string?, bool)[]
        {
            ("body (vesna)", "F6", false),
            ("hair (vesna)", "f6", false),
            ("coat (vesna)", "F7", false),
        });

        Assert.Equal("Same key as hair (vesna). They switch together.", tips[0]);
        Assert.Equal("Same key as body (vesna). They switch together.", tips[1]);
        Assert.Equal("", tips[2]);   // the only thing on F7
    }

    [Fact]
    public void Three_on_one_key_read_as_a_list()
    {
        var tips = KeyCollisions.Tips(new (string, string?, bool)[]
        {
            ("body (vesna)", "F6", false),
            ("hair (vesna)", "F6", false),
            ("coat (vesna)", "F6", false),
        });

        Assert.Equal("Same key as hair (vesna) and coat (vesna). They switch together.", tips[0]);
        Assert.Equal("Same key as body (vesna) and coat (vesna). They switch together.", tips[1]);
        Assert.Equal("Same key as body (vesna) and hair (vesna). They switch together.", tips[2]);
    }

    [Fact]
    public void A_change_sharing_the_whole_mods_key_says_so_from_both_sides()
    {
        var tips = KeyCollisions.Tips(new (string, string?, bool)[]
        {
            (KeyCollisions.WholeModLabel, "F6", false),
            ("body (vesna)", "F6", false),
        });

        Assert.Equal("Same key as body (vesna). They switch together.", tips[0]);
        Assert.Equal($"Same key as {KeyCollisions.WholeModLabel}. They switch together.", tips[1]);
    }

    [Fact]
    public void Distinct_keys_and_unkeyed_changes_carry_nothing()
    {
        Assert.All(KeyCollisions.Tips(new (string, string?, bool)[]
        {
            ("body (vesna)", "F6", false),
            ("hair (vesna)", "F7", false),
            ("coat (vesna)", null, false),
            ("boot (vesna)", null, false),   // two unkeyed changes are not "both on the same key"
        }), t => Assert.Equal("", t));
    }

    [Fact]
    public void Two_verbs_on_one_part_are_told_apart_by_the_label()
    {
        // one part can carry a retexture and a hide at once; without the verb both read as the same owner
        // and each tip names the row it sits on
        var tips = KeyCollisions.Tips(new[]
        {
            (KeyCollisions.OwnerLabel("body", "vesna", "Base", EditVerbs.Retexture), (string?)"F6", false),
            (KeyCollisions.OwnerLabel("body", "vesna", "Base", EditVerbs.Hide), "F6", false),
        });

        // the verb reads in the change list's own words, not as the raw verb token
        Assert.Equal("Same key as hidden on body (vesna · Base). They switch together.", tips[0]);
        Assert.Equal("Same key as new textures on body (vesna · Base). They switch together.", tips[1]);
    }

    [Fact]
    public void Two_outfits_of_one_character_are_told_apart_by_the_label()
    {
        // both outfits carry a part called "body", so without the outfit each tip names its own row
        var tips = KeyCollisions.Tips(new[]
        {
            (KeyCollisions.OwnerLabel("body", "vesna", "Base", EditVerbs.Replace), (string?)"F6", false),
            (KeyCollisions.OwnerLabel("body", "vesna", "Snowline", EditVerbs.Replace), "F6", false),
        });

        Assert.Equal("Same key as new mesh on body (vesna · Snowline). They switch together.", tips[0]);
        Assert.Equal("Same key as new mesh on body (vesna · Base). They switch together.", tips[1]);
    }

    /// <summary>One key is one switch, so sharers that disagree on how they start say so on the same ⚠ the
    /// sharing itself carries. The line states what SHIPS: there is no ordering rule to decode, and nothing
    /// off the row to go and look at. Sharers that agree carry nothing extra.</summary>
    [Fact]
    public void Sharers_that_disagree_on_their_start_say_what_ships_beside_the_key()
    {
        var tips = KeyCollisions.Tips(new (string, string?, bool)[]
        {
            ("body (vesna)", "F6", false),
            ("hair (vesna)", "F6", true),
            ("coat (vesna)", "F7", true),
        });

        Assert.Equal("Same key as hair (vesna). They switch together. "
            + "Starts off is set on some, not all. They all start on.", tips[0]);
        Assert.Equal("Same key as body (vesna). They switch together. "
            + "Starts off is set on some, not all. They all start on.", tips[1]);
        Assert.Equal("", tips[2]);   // alone on F7: nothing to disagree with
    }

    /// <summary>The whole-mod key has no start box of its own and starts on, so a change that asks to start
    /// off while sharing it is the same disagreement — and the line reads the same on both controls.</summary>
    [Fact]
    public void A_change_starting_off_on_the_whole_mod_key_reads_the_same_outcome()
    {
        var tips = KeyCollisions.Tips(new (string, string?, bool)[]
        {
            (KeyCollisions.WholeModLabel, "F6", false),
            ("body (vesna)", "F6", true),
        });

        Assert.Equal("Same key as body (vesna). They switch together. "
            + "Starts off is set on some, not all. They all start on.", tips[0]);
        Assert.Equal("Same key as the whole mod. They switch together. "
            + "Starts off is set on some, not all. They all start on.", tips[1]);
    }

    [Fact]
    public void Sharers_that_agree_on_their_start_carry_only_the_sharing()
    {
        var tips = KeyCollisions.Tips(new (string, string?, bool)[]
        {
            ("body (vesna)", "F6", true),
            ("hair (vesna)", "F6", true),
        });

        Assert.All(tips, t => Assert.DoesNotContain(KeyCollisions.StartStateNote, t));
        Assert.Equal("Same key as hair (vesna). They switch together.", tips[0]);
    }

    // ---- the hide collapse's key ----

    [Fact]
    public void A_second_hide_on_one_hash_with_another_key_says_which_key_survives()
    {
        var w = ModBuilder.HideKeyCollisionWarning("c_vesna01_cloth1_lod0", "F6", "F8");

        Assert.NotNull(w);
        Assert.Contains("c_vesna01_cloth1_lod0", w);
        Assert.Contains("F6 applies", w);
    }

    [Fact]
    public void A_second_hide_arriving_on_an_unkeyed_one_says_no_key_applies() =>
        Assert.Contains("no key applies", ModBuilder.HideKeyCollisionWarning("mesh", null, "F8"));

    [Theory]
    [InlineData("F6", "f6")]      // the same binding, however it was typed
    [InlineData(null, null)]      // two unkeyed hides collapse with nothing to say
    public void A_hide_collapse_that_loses_no_key_warns_about_nothing(string? kept, string? incoming) =>
        Assert.Null(ModBuilder.HideKeyCollisionWarning("mesh", kept, incoming));

    // ---- emission shape (the golden test pins the bytes; this pins the intent) ----

    [Fact]
    public void A_keyed_overlay_mod_starts_on_and_toggles_with_the_standard_pattern()
    {
        string outDir = Path.Combine(_root, "keyed");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { "aaaa1111" }, modKey: "F6");
        var ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        string v = ModKeys.VariableFor("F6");

        Assert.Contains($"global ${v} = 1", ini);                // starts ON
        // the press binds the key to a command list; the flip lives THERE, since a variable assignment
        // written in a [Key…] section is dropped when 3DMigoto parses it as a KeyOverride
        Assert.Contains($"[Key_{v}]\nkey = no_modifiers F6\nrun = CommandListKey_{v}\n", ini);
        Assert.Contains($"[CommandListKey_{v}]\n${v} = 1 - ${v}\n", ini);   // the standard toggle
        Assert.Contains($"if ${v} == 1\nhandling = skip\nendif", ini);
    }

    /// <summary>A toggle is per-session: no key variable asks to be restored from a previous run, on either
    /// build route. Every keyed change re-reads its declared start at launch.</summary>
    [Fact]
    public void No_key_variable_is_declared_persistent_on_either_build_route()
    {
        string dds = Path.Combine(_root, "nokeep.dds");
        Directory.CreateDirectory(_root);
        FlatDds.Write(dds, (10, 20, 30, 255));

        string overlayDir = Path.Combine(_root, "nokeep-overlay");
        new MigotoEmitter().BuildOverlaysOnly(overlayDir,
            new[] { new RetexEntry("skin", "bbbb2222", dds, "F8") },
            hideHashes: new[] { "aaaa1111" }, modKey: "F6",
            hideKeys: new Dictionary<string, string> { ["aaaa1111"] = "F9" });
        Assert.DoesNotContain("persist", File.ReadAllText(Path.Combine(overlayDir, "mod.ini")));

        string pooledDir = Path.Combine(_root, "nokeep-pooled");
        SyntheticPool.WritePartDump(Path.Combine(_root, "np", "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = pooledDir,
            ToggleKey = "F6",
            HideHashes = new[] { "cccc3333" },
            HideKeys = new Dictionary<string, string> { ["cccc3333"] = "F9" },
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", Path.Combine(_root, "np", "alpha")) },
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                    ToggleKey = "F8",
                },
            },
        });
        Assert.DoesNotContain("persist", File.ReadAllText(Path.Combine(pooledDir, "mod.ini")));
    }

    /// <summary>The [Key…] section carries nothing but its binding and its run: 3DMigoto parses it as a
    /// KeyOverride, so an assignment written there is dropped at parse and the press toggles nothing.</summary>
    [Fact]
    public void The_key_section_carries_no_variable_assignment_of_its_own()
    {
        string outDir = Path.Combine(_root, "keyshape");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { "aaaa1111" }, modKey: "F6");
        var ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        var section = ini.Split("[Key_" + ModKeys.VariableFor("F6") + "]\n")[1].Split("\n\n")[0];
        Assert.DoesNotContain("$", section);
        Assert.Equal(new[] { "key = no_modifiers F6", $"run = CommandListKey_{ModKeys.VariableFor("F6")}" },
            section.Split('\n'));
    }

    /// <summary>A key that names its modifiers keeps them; only an unmodified one is bound
    /// <c>no_modifiers</c>, so CTRL F6 and a bare F6 in one mod never both fire on one press.</summary>
    [Fact]
    public void A_modified_key_keeps_its_modifiers_and_an_unmodified_one_is_bound_no_modifiers()
    {
        string outDir = Path.Combine(_root, "modifiers");
        string dds = Path.Combine(_root, "mod-rtx.dds");
        Directory.CreateDirectory(_root);
        FlatDds.Write(dds, (10, 20, 30, 255));
        new MigotoEmitter().BuildOverlaysOnly(outDir,
            new[] { new RetexEntry("skin", "bbbb2222", dds, "ctrl shift h") },
            hideHashes: new[] { "aaaa1111" }, modKey: "F6");
        var ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains("key = no_modifiers F6\n", ini);
        Assert.Contains("key = CTRL SHIFT H\n", ini);
        Assert.DoesNotContain("key = no_modifiers CTRL", ini);
    }

    [Fact]
    public void An_unkeyed_overlay_mod_emits_no_key_machinery_at_all()
    {
        string outDir = Path.Combine(_root, "unkeyed");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null, hideHashes: new[] { "aaaa1111" });
        var ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.DoesNotContain("[Key_", ini);
        Assert.DoesNotContain("[CommandListKey_", ini);
        Assert.DoesNotContain("persist", ini);
        Assert.Contains("hash = aaaa1111\nhandling = skip\n", ini);
    }

    [Fact]
    public void The_mods_key_and_a_changes_key_nest_so_either_one_switches_the_change_off()
    {
        string outDir = Path.Combine(_root, "two-tier");
        string dds = Path.Combine(_root, "rtx.dds");
        Directory.CreateDirectory(_root);
        FlatDds.Write(dds, (10, 20, 30, 255));
        new MigotoEmitter().BuildOverlaysOnly(outDir,
            new[] { new RetexEntry("skin", "bbbb2222", dds, "F8") }, hideHashes: null, modKey: "F6");
        var ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        string mod = ModKeys.VariableFor("F6"), change = ModKeys.VariableFor("F8");
        Assert.Contains($"if ${mod} == 1\nif ${change} == 1\nthis = ", ini);
        Assert.Contains("endif\nendif\n", ini);
        // one [Key] section per distinct key, whichever tier bound it, each running its own flip list
        Assert.Contains($"[Key_{mod}]\nkey = no_modifiers F6\nrun = CommandListKey_{mod}\n", ini);
        Assert.Contains($"[Key_{change}]\nkey = no_modifiers F8\nrun = CommandListKey_{change}\n", ini);
        Assert.Contains($"[CommandListKey_{mod}]\n${mod} = 1 - ${mod}\n", ini);
        Assert.Contains($"[CommandListKey_{change}]\n${change} = 1 - ${change}\n", ini);
    }
}
