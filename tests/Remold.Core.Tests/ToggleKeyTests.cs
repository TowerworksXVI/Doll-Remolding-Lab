using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Input;
using Remold.App.ViewModels;
using Remold.App.Views;
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

    [Theory]
    // punctuation and OEM spellings fold to the key table's one canonical token
    [InlineData(".", "PERIOD")]
    [InlineData("oem_period", "PERIOD")]
    [InlineData("VK_OEM_PERIOD", "PERIOD")]
    [InlineData("[", "VK_OEM_4")]
    [InlineData("oem_4", "VK_OEM_4")]
    [InlineData("~", "VK_OEM_3")]
    [InlineData("tilde", "VK_OEM_3")]
    [InlineData(";", "SEMICOLON")]
    [InlineData("/", "SLASH")]
    [InlineData("\\", "BACKSLASH")]
    [InlineData("'", "QUOTE")]
    [InlineData("]", "VK_OEM_6")]
    [InlineData("=", "EQUALS")]
    [InlineData("_", "MINUS")]
    // the bare operator characters keep the table's numpad meaning
    [InlineData("*", "MULTIPLY")]
    [InlineData("+", "ADD")]
    [InlineData("-", "SUBTRACT")]
    // named navigation/editing aliases
    [InlineData("pgup", "PRIOR")]
    [InlineData("page_down", "NEXT")]
    [InlineData("return", "ENTER")]
    [InlineData("back", "BACKSPACE")]
    [InlineData("delete", "DELETE")]
    // hex codes: a named code comes back as its name; an unnamed one keeps the exact
    // lower-case-0x shape hex is matched by
    [InlineData("0x42", "B")]
    [InlineData("0X70", "F1")]
    [InlineData("0xe1", "0xe1")]
    [InlineData("0xE1", "0xe1")]
    // modifiers fold to one order, a spelled-out no_modifiers folds away (a bare key already
    // means exactly that), and the sided modifier forms stand on their own
    [InlineData("shift ctrl h", "CTRL SHIFT H")]
    [InlineData("no_modifiers f6", "F6")]
    [InlineData("lshift .", "LSHIFT PERIOD")]
    [InlineData("lwin d", "LWIN D")]
    public void Every_spelling_of_a_key_folds_to_its_one_canonical_token(string typed, string expected) =>
        Assert.Equal(expected, ModKeys.Normalize(typed));

    [Theory]
    [InlineData("shift")]                // bare modifier: bound no_modifiers by the emitter, it could never fire
    [InlineData("LWIN")]
    [InlineData("ctrl ctrl h")]          // one modifier twice
    [InlineData("ctrl lctrl h")]         // a modifier beside its own sided form: one binding spelled two ways
    [InlineData("no_modifiers ctrl h")]  // a modifier excluded and required at once
    [InlineData("no_modifiers")]         // no key at all
    [InlineData("FOO")]                  // no such name in the key table
    [InlineData("h j")]                  // two keys: not modifiers-then-key
    [InlineData("0x00")]                 // outside the virtual-key range
    [InlineData("0x1ff")]
    [InlineData("0x")]
    public void A_key_the_game_could_not_bind_normalizes_to_no_key(string typed) =>
        Assert.Null(ModKeys.Normalize(typed));

    [Theory]
    // the parser's code names read as the keycap; everything already readable stays as it is
    [InlineData("period", ".")]
    [InlineData("VK_OEM_4", "[")]
    [InlineData("~", "~")]
    [InlineData("equals", "=")]
    [InlineData("ctrl shift .", "CTRL SHIFT .")]
    [InlineData("numpad5", "NUM 5")]
    [InlineData("multiply", "NUM *")]
    [InlineData("decimal", "NUM .")]
    [InlineData("page_up", "PGUP")]
    [InlineData("f6", "F6")]
    [InlineData("ctrl shift h", "CTRL SHIFT H")]
    [InlineData("space", "SPACE")]
    public void A_key_reads_on_screen_as_its_keycap_not_the_parsers_code(string stored, string shown) =>
        Assert.Equal(shown, ModKeys.Display(stored));

    [Fact]
    public void No_key_and_an_unusable_key_both_read_as_the_empty_label()
    {
        Assert.Equal("none", ModKeys.Display(null, "none"));
        Assert.Equal("none", ModKeys.Display("FOO", "none"));
    }

    [Fact]
    public void Every_spelling_of_one_key_is_one_variable()
    {
        Assert.Equal(ModKeys.VariableFor("."), ModKeys.VariableFor("VK_OEM_PERIOD"));
        Assert.Equal(ModKeys.VariableFor("0x42"), ModKeys.VariableFor("b"));
        Assert.Equal(ModKeys.VariableFor("ctrl shift h"), ModKeys.VariableFor("SHIFT CTRL H"));
        Assert.True(ModKeys.SameKey("pgup", "PRIOR"));
        Assert.False(ModKeys.SameKey("PERIOD", "COMMA"));
    }

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

    // ---- capture ----

    [Theory]
    [InlineData(Key.OemPeriod, "PERIOD")]
    [InlineData(Key.OemOpenBrackets, "VK_OEM_4")]
    [InlineData(Key.OemTilde, "VK_OEM_3")]
    [InlineData(Key.Multiply, "MULTIPLY")]
    [InlineData(Key.Decimal, "DECIMAL")]
    [InlineData(Key.Enter, "ENTER")]
    [InlineData(Key.MediaPlayPause, "MEDIA_PLAY_PAUSE")]
    public void A_captured_key_carries_the_tables_canonical_token(Key key, string token) =>
        Assert.Equal(token, KeyCaptureButton.Token(key, KeyModifiers.None));

    [Fact]
    public void A_captured_combo_spells_its_modifiers_in_canonical_order() =>
        Assert.Equal("CTRL SHIFT PERIOD",
            KeyCaptureButton.Token(Key.OemPeriod, KeyModifiers.Control | KeyModifiers.Shift));

    /// <summary>The capture whitelist and the normalizer are two statements of one vocabulary: every token
    /// capture can produce must come back from <see cref="ModKeys.Normalize"/> exactly as it went in, or a
    /// captured key and its own saved spelling would be two different bindings.</summary>
    [Fact]
    public void Every_capturable_token_normalizes_to_exactly_itself()
    {
        var seen = false;
        foreach (Key key in Enum.GetValues<Key>())
            foreach (var mods in new[]
                { KeyModifiers.None, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift })
                if (KeyCaptureButton.Token(key, mods) is { } token)
                {
                    seen = true;
                    Assert.Equal(token, ModKeys.Normalize(token));
                }
        Assert.True(seen);
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

    /// <summary>The whole-mod persistence choice rides the manifest, and a project that never made one
    /// writes no field — older manifests read back as the per-session mod they always were.</summary>
    [Fact]
    public void The_whole_mod_persistence_choice_survives_a_save_and_is_absent_until_made()
    {
        var proj = new ModProject { RootDir = Path.Combine(_root, "persist-info") };
        proj.Info.ToggleKey = "F6";
        proj.Save();
        Assert.DoesNotContain("toggle_key_persist",
            File.ReadAllText(ModProject.ManifestPathFor(proj.RootDir!)));
        Assert.False(ModProject.Load(proj.RootDir!).Info.PersistToggleKey);

        proj.Info.PersistToggleKey = true;
        proj.Save();
        Assert.True(ModProject.Load(proj.RootDir!).Info.PersistToggleKey);
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

        proj.RemoveSubjectChangeKeys("Vesna", "VesnaSSR01");

        Assert.Null(proj.GetChangeKey("Vesna", "VesnaSSR01", "body", EditVerbs.Replace));
        Assert.Equal("F7", proj.GetChangeKey("Other", "OtherSSR01", "body", EditVerbs.Replace));
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

    /// <summary>A hide under a plan answers to EVERY state that asks for it, so what collapsed is judged on
    /// the whole or-list rather than whichever term happens to sit at its front.</summary>
    [Fact]
    public void A_hide_collapse_judges_the_whole_list_of_positions_each_claimant_carries()
    {
        // two positions of one key against another key: what survives is the first claimant's key
        Assert.Contains("F6 applies", ModBuilder.HideKeyCollisionWarning("mesh",
            new[] { new KeyRef("F6", 0), new KeyRef("F6", 2) }, new[] { new KeyRef("F8", 1) }));
        // the same keys in another order are the same claim, and reading the front term would disagree
        Assert.Null(ModBuilder.HideKeyCollisionWarning("mesh",
            new[] { new KeyRef("F6", 0), new KeyRef("F8", 1) },
            new[] { new KeyRef("F8", 0), new KeyRef("F6", 2) }));
        // a claimant on two keys against one of them: reading the front term would call these agreed
        Assert.Contains("F6, F8 applies", ModBuilder.HideKeyCollisionWarning("mesh",
            new[] { new KeyRef("F6", 0), new KeyRef("F8", 0) }, new[] { new KeyRef("F6", 0) }));
        // an unkeyed claimant arriving on a keyed one, and two unkeyed ones with nothing to say
        Assert.Contains("no key applies",
            ModBuilder.HideKeyCollisionWarning("mesh", Array.Empty<KeyRef>(), new[] { new KeyRef("F8", 0) }));
        Assert.Null(ModBuilder.HideKeyCollisionWarning("mesh", Array.Empty<KeyRef>(), null));
    }

    // ---- emission shape (the golden test pins the bytes; this pins the intent) ----

    [Fact]
    public void A_keyed_overlay_mod_starts_on_and_toggles_with_the_standard_pattern()
    {
        string outDir = Path.Combine(_root, "keyed");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { "aaaa1111" }, modKey: "F6");
        var ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        string v = ModKeys.VariableFor("F6");

        Assert.Contains($"global ${v} = 0", ini);                // starts ON, at position 0 like any key
        // the press binds the key to a command list; the step lives THERE, since a variable assignment
        // written in a [Key…] section is dropped when 3DMigoto parses it as a KeyOverride
        Assert.Contains($"[Key_{v}]\nkey = no_modifiers F6\nrun = CommandListKey_{v}\n", ini);
        // two positions, so the step wraps straight back — the standard toggle, said ordinally
        Assert.Contains($"[CommandListKey_{v}]\n${v} = ${v} + 1\nif ${v} == 2\n${v} = 0\nendif\n", ini);
        Assert.Contains($"if ${v} == 0\nhandling = skip\nendif", ini);
    }

    /// <summary>An opted-in key is declared <c>persist</c> — the runtime then saves its position on exit
    /// and restores it from its user config at the next launch — and the restore lands AFTER the
    /// <c>[Constants]</c> pre list runs, so a build whose flags depend on key positions re-runs the shared
    /// recompute <c>post</c>.</summary>
    [Fact]
    public void An_opted_in_key_declares_persist_and_reruns_the_recompute_after_the_restore()
    {
        string outDir = Path.Combine(_root, "persist");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { "aaaa1111" }, modKey: "F6",
            keyCycles: new[] { new KeyCycle("F7", 3, 0, Persist: true), new KeyCycle("F9", 2, 0) },
            shownFlags: new[]
                { new ShownFlag("skin", new KeyRef[] { new("F7", 0), new("F7", 2) }) },
            persistToggleKey: true);
        var ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains("global persist $zz_key_f6 = 0\n", ini);
        Assert.Contains("global persist $zz_key_f7 = 0\n", ini);
        Assert.Contains("run = CommandListRecomputeHidden\npost run = CommandListRecomputeHidden\n", ini);
    }

    /// <summary>Persistence is per key: a key that did not opt in keeps the plain declaration beside one
    /// that did, and a build with no flag machinery gains no post run.</summary>
    [Fact]
    public void A_key_that_did_not_opt_in_stays_per_session_beside_one_that_did()
    {
        string outDir = Path.Combine(_root, "persist-mixed");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { "aaaa1111" }, modKey: "F6",
            hideKeys: new Dictionary<string, IReadOnlyList<KeyRef>>
                { ["aaaa1111"] = new KeyRef[] { "F7" } },
            keyCycles: new[] { new KeyCycle("F7", 2, 0, Persist: true) });
        var ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains("global $zz_key_f6 = 0\n", ini);
        Assert.Contains("global persist $zz_key_f7 = 0\n", ini);
        Assert.DoesNotContain("post run", ini);
    }

    /// <summary>A toggle is per-session unless its key opts in: by default no key variable asks to be
    /// restored from a previous run, on either build route. Every keyed change re-reads its declared start
    /// at launch.</summary>
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
            hideKeys: new Dictionary<string, IReadOnlyList<KeyRef>>
                { ["aaaa1111"] = new KeyRef[] { "F9" } });
        Assert.DoesNotContain("persist", File.ReadAllText(Path.Combine(overlayDir, "mod.ini")));

        string pooledDir = Path.Combine(_root, "nokeep-pooled");
        SyntheticPool.WritePartDump(Path.Combine(_root, "np", "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = pooledDir,
            ToggleKey = "F6",
            HideHashes = new[] { "cccc3333" },
            HideKeys = new Dictionary<string, IReadOnlyList<KeyRef>> { ["cccc3333"] = new KeyRef[] { "F9" } },
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

    /// <summary>A punctuation key crosses the whole path — capture spelling in, named table token out — so
    /// the emitted line is one the game's parser takes: a literal <c>.</c> would read as an unknown name
    /// and a literal <c>;</c> would end the line as a comment.</summary>
    [Fact]
    public void A_punctuation_key_is_emitted_as_its_named_token()
    {
        string outDir = Path.Combine(_root, "punct");
        string dds = Path.Combine(_root, "punct-rtx.dds");
        Directory.CreateDirectory(_root);
        FlatDds.Write(dds, (10, 20, 30, 255));
        new MigotoEmitter().BuildOverlaysOnly(outDir,
            new[] { new RetexEntry("skin", "bbbb2222", dds, ".") },
            hideHashes: new[] { "aaaa1111" }, modKey: "[");
        var ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains($"[Key_{ModKeys.VariableFor("[")}]\nkey = no_modifiers VK_OEM_4\n", ini);
        Assert.Contains($"[Key_{ModKeys.VariableFor(".")}]\nkey = no_modifiers PERIOD\n", ini);
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
        Assert.Contains("hash = aaaa1111\nmatch_priority = 0\nhandling = skip\n", ini);
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
        Assert.Contains($"if ${mod} == 0\nif ${change} == 0\nthis = ", ini);
        Assert.Contains("endif\nendif\n", ini);
        // one [Key] section per distinct key, whichever tier bound it, each running its own step list
        Assert.Contains($"[Key_{mod}]\nkey = no_modifiers F6\nrun = CommandListKey_{mod}\n", ini);
        Assert.Contains($"[Key_{change}]\nkey = no_modifiers F8\nrun = CommandListKey_{change}\n", ini);
        foreach (string v in new[] { mod, change })
            Assert.Contains($"[CommandListKey_{v}]\n${v} = ${v} + 1\nif ${v} == 2\n${v} = 0\nendif\n", ini);
    }
}
