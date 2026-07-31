using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.App.ViewModels;
using Remold.Core.Project;
using Remold.Core.Textures;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Build pane's pure view-model seams — the row/chip mapping a derived <see cref="MeshEdit"/> gets on
/// screen, and the built-folder summary — driven without standing up the window.
/// </summary>
[Collection("Dispatcher")]
public class BuildPaneVmTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-buildvm-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static MeshEdit Edit(string verb, List<SubmeshTextures>? textures = null) => new()
    {
        Character = "Vesna", Outfit = "VesnaSSR01", Mesh = "c_vesna01_body_lod0",
        Verb = verb, Textures = textures,
    };

    private static BuildGroupVm Group() => new()
    {
        Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR01", OutfitLabel = "Base  ·  Plum Fizz",
    };

    // The chip vocabulary, read off the constants the panes read, so a label change can't leave these tests
    // asserting a name nothing shows.
    private const string BaseColor = TextureMap.BaseColorLabel;
    private const string Norm = TextureMap.NormalLabel;
    private const string Rmo = TextureMap.RmoLabel;
    private static string Blanked(string label) =>
        label + " " + Remold.App.ViewModels.Workbench.WorkbenchMapVm.BlankedNote;
    private static string Edited(string label) => label + " ✎";

    [Fact]
    public void A_blanked_normal_slot_reads_on_the_chip_instead_of_vanishing()
    {
        // the neutral gesture names no file, so an authored-only chip would show it as no change at all
        var row = new BuildRowVm(Edit(EditVerbs.Replace, new List<SubmeshTextures>
        {
            new() { Submesh = 0, NormalOrigin = SlotOrigin.ExplicitNeutral },
        }), "body", included: true);

        var normal = row.Chips.Single(c => c.Label == Norm);
        Assert.Equal(Blanked(Norm), normal.Text);
        Assert.StartsWith("Blanked in this mod", normal.Tip);
        // and the slots that asked for nothing still read as untouched
        Assert.Equal(BaseColor, row.Chips.Single(c => c.Label == BaseColor).Text);
    }

    [Fact]
    public void An_albedo_only_replace_reads_its_flat_normal_and_RMO_on_the_chips()
    {
        // the build's rule, not the neutral gesture's: a submesh that asked for anything draws on donor UVs,
        // so the two relief slots it named no file for ship FLAT. Chips reading them as untouched would
        // describe a different build from the one running.
        var row = new BuildRowVm(Edit(EditVerbs.Replace, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "textures/body_s0_base.png" },
        }), "body", included: true);

        Assert.Equal(Blanked(Norm), row.Chips.Single(c => c.Label == Norm).Text);
        Assert.Equal(Blanked(Rmo), row.Chips.Single(c => c.Label == Rmo).Text);
        Assert.Equal(Edited(BaseColor), row.Chips.Single(c => c.Label == BaseColor).Text);
    }

    [Fact]
    public void A_blanked_RMO_slot_reads_on_its_own_chip()
    {
        // the neutral gesture is not the normal slot's alone, and an RMO blanked on purpose names no file
        // either — an authored-only chip would show it as no change at all
        var row = new BuildRowVm(Edit(EditVerbs.Replace, new List<SubmeshTextures>
        {
            new() { Submesh = 0, RmoOrigin = SlotOrigin.ExplicitNeutral },
        }), "body", included: true);

        var rmo = row.Chips.Single(c => c.Label == Rmo);
        Assert.Equal(Blanked(Rmo), rmo.Text);
        Assert.StartsWith("Blanked in this mod", rmo.Tip);
        // its asking is what pulls the file-less normal flat beside it; no flat albedo stands in for one
        Assert.Equal(Blanked(Norm), row.Chips.Single(c => c.Label == Norm).Text);
        Assert.Equal(BaseColor, row.Chips.Single(c => c.Label == BaseColor).Text);
    }

    [Fact]
    public void A_retexture_takes_no_relief_substitution_on_the_chips()
    {
        // A retexture keeps the vanilla UVs, so the build leaves the slots it names no file for alone. The
        // relief rule belongs to the replace, and a chip that applied it here would report a blanking the
        // build never does.
        var row = new BuildRowVm(Edit(EditVerbs.Retexture, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "textures/body_s0_base.png" },
        }), "body", included: true);

        Assert.Equal(Norm, row.Chips.Single(c => c.Label == Norm).Text);
        Assert.Equal(Rmo, row.Chips.Single(c => c.Label == Rmo).Text);
    }

    [Fact]
    public void An_albedo_plugged_with_the_neutral_never_reads_as_blanked()
    {
        // There is no flat albedo to stand in for a base color, and the emitter refuses a row that asks for
        // one. A chip saying "blanked" would advertise a state the build rejects.
        var row = new BuildRowVm(Edit(EditVerbs.Replace, new List<SubmeshTextures>
        {
            new() { Submesh = 0, AlbedoOrigin = SlotOrigin.ExplicitNeutral },
        }), "body", included: true);

        var albedo = row.Chips.Single(c => c.Label == BaseColor);
        Assert.Equal(BaseColor, albedo.Text);
        Assert.False(albedo.Blanked);
        Assert.Equal("Not edited", albedo.Tip);
        // its ASKING still pulls the two relief slots flat, which is the rule that does apply here
        Assert.Equal(Blanked(Norm), row.Chips.Single(c => c.Label == Norm).Text);
    }

    [Fact]
    public void An_authored_normal_outranks_a_blanked_one_on_the_chip()
    {
        // work shipped on one submesh is what the row is ticked for, and the pencil is that state
        var row = new BuildRowVm(Edit(EditVerbs.Replace, new List<SubmeshTextures>
        {
            new() { Submesh = 0, NormalOrigin = SlotOrigin.ExplicitNeutral },
            new() { Submesh = 1, Normal = "textures/body_s1_nrm.png" },
        }), "body", included: true);

        var normal = row.Chips.Single(c => c.Label == Norm);
        Assert.Equal(Edited(Norm), normal.Text);
        // …and the half the glyph can't show is still owed, on the tip's own line
        Assert.True(normal.Authored);
        Assert.True(normal.Blanked);
        Assert.Equal("Edited in this mod\n" + BuildChipVm.MixedNote, normal.Tip);
    }

    [Fact]
    public void A_chip_in_one_state_only_says_one_thing()
    {
        // the mixed line is for a chip that did both; a chip that did one must not carry it
        var row = new BuildRowVm(Edit(EditVerbs.Replace, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Normal = "textures/body_s0_nrm.png", Rmo = "textures/body_s0_rmo.png" },
        }), "body", included: true);

        var normal = row.Chips.Single(c => c.Label == Norm);
        Assert.Equal("Edited in this mod", normal.Tip);
        // and the acronym's legend still lands under the state, blanked or not
        var rmo = row.Chips.Single(c => c.Label == Rmo);
        Assert.Equal("Edited in this mod\n" + Remold.App.ViewModels.Workbench.WorkbenchMapVm.RmoChannels, rmo.Tip);
    }

    [Fact]
    public void A_mixed_RMO_chip_says_both_states_and_still_carries_its_legend()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Replace, new List<SubmeshTextures>
        {
            new() { Submesh = 0, RmoOrigin = SlotOrigin.ExplicitNeutral },
            new() { Submesh = 1, Rmo = "textures/body_s1_rmo.png" },
        }), "body", included: true);

        var rmo = row.Chips.Single(c => c.Label == Rmo);
        Assert.Equal(Edited(Rmo), rmo.Text);
        Assert.Equal("Edited in this mod\n" + BuildChipVm.MixedNote + "\n"
            + Remold.App.ViewModels.Workbench.WorkbenchMapVm.RmoChannels, rmo.Tip);
    }

    [Fact]
    public void A_replace_row_reads_as_a_new_mesh_with_a_chip_per_map_it_can_ship()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true);

        Assert.True(row.IsReplace);
        Assert.False(row.IsHide);
        Assert.Equal(new[] { BaseColor, Norm, Rmo }, row.Chips.Select(c => c.Label));
        Assert.All(row.Chips, c => Assert.False(c.Authored));
    }

    [Fact]
    public void An_authored_RMO_on_a_replace_lights_its_chip()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Replace, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Rmo = "r.png" },
        }), "body", included: true);

        var rmo = Assert.Single(row.Chips, c => c.Label == Rmo);
        Assert.True(rmo.Authored);
        Assert.All(row.Chips.Where(c => c.Label != Rmo), c => Assert.False(c.Authored));
    }

    [Fact]
    public void A_map_authored_on_any_submesh_lights_its_chip()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Retexture, new List<SubmeshTextures>
        {
            new() { Submesh = 0 },
            new() { Submesh = 1, Rmo = "r.png" },
        }), "body", included: true);

        Assert.False(row.IsReplace);   // a retexture keeps the mesh; only the chips speak
        Assert.True(row.IsRetexture);
        Assert.Equal(new[] { BaseColor, Norm, Rmo }, row.Chips.Select(c => c.Label));
        Assert.Equal(new[] { false, false, true }, row.Chips.Select(c => c.Authored));
        Assert.Equal(Edited(Rmo), row.Chips[2].Text);
        Assert.Equal(BaseColor, row.Chips[0].Text);
    }

    [Fact]
    public void A_chip_says_whether_that_map_was_edited()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Retexture, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "a.png" },
        }), "body", included: true);

        Assert.Equal("Edited in this mod", row.Chips[0].Tip);
        Assert.Equal("Not edited", row.Chips[1].Tip);
    }

    /// <summary>"RMO" is the one chip whose label says nothing on its own, so its tip carries the channel
    /// legend under the edited state. The labels that read as words carry no second line.</summary>
    [Fact]
    public void Only_the_RMO_chip_spells_out_its_channels()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true);

        Assert.Equal("Not edited\n" + Remold.App.ViewModels.Workbench.WorkbenchMapVm.RmoChannels,
            Assert.Single(row.Chips, c => c.Label == Rmo).Tip);
        Assert.All(row.Chips.Where(c => c.Label != Rmo), c => Assert.Equal("Not edited", c.Tip));
    }

    [Fact]
    public void A_hide_row_carries_no_chips()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Hide), "cloth1", included: true);

        Assert.True(row.IsHide);
        Assert.False(row.HasChips);
    }

    [Fact]
    public void Restoring_the_persisted_tick_does_not_fire_the_toggle()
    {
        // the excluded state is a constructor argument, so rebuilding the list can't write back
        var row = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: false);
        var fired = new List<bool>();
        row.Toggled = (_, included) => fired.Add(included);

        Assert.True(row.IsExcluded);
        Assert.Empty(fired);

        row.IsIncluded = true;
        Assert.Equal(new[] { true }, fired);
        Assert.False(row.IsExcluded);
    }

    [Fact]
    public void A_group_counts_every_change_it_lists_ticked_or_not()
    {
        var group = new BuildGroupVm
        {
            Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR01", OutfitLabel = "Base  ·  Plum Fizz",
        };
        group.Rows.Add(new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true));
        Assert.Equal("1 change", group.ChangesLabel);

        group.Rows.Add(new BuildRowVm(Edit(EditVerbs.Hide), "cloth1", included: false));
        Assert.Equal("2 changes", group.ChangesLabel);
    }

    [Fact]
    public void A_group_header_says_how_many_of_its_changes_are_left_out()
    {
        var group = new BuildGroupVm
        {
            Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR01", OutfitLabel = "Base  ·  Plum Fizz",
        };
        var row = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true);
        group.Rows.Add(row);
        group.Rows.Add(new BuildRowVm(Edit(EditVerbs.Hide), "cloth1", included: true));

        Assert.Equal("", group.LeftOutLabel);   // nothing left out reads as nothing at all

        row.IsIncluded = false;
        group.RefreshCounts();
        Assert.Equal("· 1 left out", group.LeftOutLabel);
    }

    [Fact]
    public void A_bulk_tick_goes_through_each_rows_own_setter_and_skips_the_rows_already_there()
    {
        var group = Group();
        var included = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true);
        var excluded = new BuildRowVm(Edit(EditVerbs.Hide), "cloth1", included: false);
        group.Rows.Add(included);
        group.Rows.Add(excluded);
        var fired = new List<(string Part, bool Included)>();
        included.Toggled = (r, v) => fired.Add((r.PartLabel, v));
        excluded.Toggled = (r, v) => fired.Add((r.PartLabel, v));

        group.IncludeAllCommand.Execute(null);

        // only the row that actually changed persisted; the other wrote nothing
        Assert.Equal(new[] { ("cloth1", true) }, fired);
        Assert.All(group.Rows, r => Assert.True(r.IsIncluded));

        fired.Clear();
        group.ExcludeAllCommand.Execute(null);

        Assert.Equal(new[] { ("body", false), ("cloth1", false) }, fired);
        Assert.All(group.Rows, r => Assert.True(r.IsExcluded));
    }

    [Fact]
    public void A_retexture_rows_edit_hop_asks_for_the_submesh_its_texture_sits_on()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Retexture, new List<SubmeshTextures>
        {
            new() { Submesh = 2, Albedo = "a.png" },
            new() { Submesh = 1, Normal = "n.png" },
        }), "body", included: true);

        // the lowest textured submesh, so the hop is stable whatever order the sets were authored in
        Assert.Equal(1, row.EditSubmesh);
    }

    [Theory]
    [InlineData(EditVerbs.Replace)]
    [InlineData(EditVerbs.Hide)]
    public void Every_other_verb_is_authored_on_the_part_itself(string verb) =>
        Assert.Null(new BuildRowVm(Edit(verb, new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "a.png" } }),
            "body", included: true).EditSubmesh);

    [Fact]
    public void A_retexture_with_no_texture_set_yet_still_hops_to_the_part() =>
        Assert.Null(new BuildRowVm(Edit(EditVerbs.Retexture), "body", included: true).EditSubmesh);

    // ---- what a key means when it is off ----

    /// <summary>The off-meaning control stands on a bound key and on the one verb with a choice: a change
    /// with no key is always on, and a Hide has no replacement of its own to fall back from.</summary>
    [Fact]
    public void Only_a_keyed_replace_offers_a_choice_of_off_state()
    {
        var unkeyed = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true);
        Assert.False(unkeyed.ShowsKeyOffMode);

        unkeyed.ToggleKey = "F6";
        Assert.True(unkeyed.ShowsKeyOffMode);

        unkeyed.ToggleKey = null;
        Assert.False(unkeyed.ShowsKeyOffMode);

        Assert.False(new BuildRowVm(Edit(EditVerbs.Hide), "cloth1", included: true, toggleKey: "F6")
            .ShowsKeyOffMode);
        Assert.False(new BuildRowVm(Edit(EditVerbs.Retexture), "body", included: true, toggleKey: "F6")
            .ShowsKeyOffMode);
    }

    /// <summary>Every verb with a key gets a start state; only a Replace also gets a choice of what off
    /// means.</summary>
    [Fact]
    public void A_start_state_stands_on_any_bound_key()
    {
        var hide = new BuildRowVm(Edit(EditVerbs.Hide), "cloth1", included: true);
        Assert.False(hide.ShowsKeyState);

        hide.ToggleKey = "F6";
        Assert.True(hide.ShowsKeyState);
        Assert.False(hide.ShowsKeyOffMode);
    }

    /// <summary>Restoring a saved binding fires nothing, and any part of it changing afterwards raises the
    /// one handler that persists the whole binding.</summary>
    [Fact]
    public void Restoring_a_binding_is_silent_and_any_part_changing_writes_it_back()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true,
            toggleKey: "F6", hideWhenOff: true, startsOff: true);
        var written = new List<(string? Key, bool Hides, bool StartsOff)>();
        row.KeyBound = r => written.Add((r.ToggleKey, r.HideWhenOff, r.StartsOff));

        Assert.True(row.HideWhenOff);
        Assert.True(row.StartsOff);
        Assert.Empty(written);

        row.HideWhenOff = false;
        row.StartsOff = false;
        row.ToggleKey = "F7";
        Assert.Equal(new[]
        {
            ((string?)"F6", false, true), ("F6", false, false), ("F7", false, false),
        }, written);
    }

    [Fact]
    public void The_key_behaviour_controls_say_which_state_they_are_in()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true, toggleKey: "F6");
        Assert.Equal("Key off: the original part draws.", row.HideWhenOffTip);
        // the start line names the LAUNCH: a press holds for its own run, which the row shows nowhere else
        Assert.Equal("On at every launch.", row.StartsOffTip);

        row.HideWhenOff = true;
        Assert.Equal("Key off: nothing draws there.", row.HideWhenOffTip);

        row.StartsOff = true;
        Assert.Equal("Off at every launch.", row.StartsOffTip);
        // both ticked is a recipe of two states, so the off-mode tip carries both
        Assert.Equal("Key off: nothing draws there. Off at every launch.", row.HideWhenOffTip);
    }

    /// <summary>A row left out of the build has every key control off, and each one names that state rather
    /// than greying out under a tip describing a binding it can't take.</summary>
    [Fact]
    public void The_key_behaviour_controls_name_their_state_on_a_row_left_out()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true, toggleKey: "F6",
            hideWhenOff: true, startsOff: true);
        var raised = new List<string>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        row.IsIncluded = false;

        Assert.Equal(BuildRowVm.LeftOutKeyTip, row.StartsOffTip);
        Assert.Equal(BuildRowVm.LeftOutKeyTip, row.HideWhenOffTip);
        Assert.Equal(BuildRowVm.LeftOutKeyTip, row.ToggleKeyTip);
        Assert.Contains(nameof(BuildRowVm.StartsOffTip), raised);
        Assert.Contains(nameof(BuildRowVm.HideWhenOffTip), raised);

        row.IsIncluded = true;
        Assert.Equal("Off at every launch.", row.StartsOffTip);
    }

    /// <summary>The ✕ clears the whole binding, so a key bound again starts from the defaults the row shows
    /// — the same state a reload would restore. One gesture persists once.</summary>
    [Fact]
    public void Clearing_the_key_takes_its_behaviour_with_it_in_one_write()
    {
        var row = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true, toggleKey: "F6",
            hideWhenOff: true, startsOff: true);
        var written = new List<(string? Key, bool Hides, bool StartsOff)>();
        row.KeyBound = r => written.Add((r.ToggleKey, r.HideWhenOff, r.StartsOff));

        row.ClearKeyCommand.Execute(null);

        Assert.Null(row.ToggleKey);
        Assert.False(row.HideWhenOff);
        Assert.False(row.StartsOff);
        Assert.Equal(new[] { ((string?)null, false, false) }, written);

        row.ToggleKey = "F7";
        Assert.False(row.HideWhenOff);
        Assert.False(row.StartsOff);
    }

    // ---- a shared toggle key, on the controls that hold it ----

    /// <summary>A pane holding one group of two ticked, unkeyed rows.</summary>
    private static (MainWindowViewModel Vm, BuildRowVm A, BuildRowVm B) PaneWithTwoRows()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        var group = Group();
        var a = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true);
        var b = new BuildRowVm(Edit(EditVerbs.Hide), "cloth1", included: true);
        group.Rows.Add(a);
        group.Rows.Add(b);
        vm.AddBuildGroup(group);
        return (vm, a, b);
    }

    [Fact]
    public void Both_rows_on_one_key_carry_it_and_neither_says_anything_below_the_grid()
    {
        var (vm, a, b) = PaneWithTwoRows();

        a.ToggleKey = "F6";
        Assert.False(a.HasKeyCollision);   // the only thing on F6 so far

        b.ToggleKey = "f6";

        // the tip names the verb in the words the ROW shows — "hidden" / "new mesh", not the raw verb token
        Assert.Equal($"Same key as {BuildVerbWords.HideWord} on cloth1 (Vesna · {Group().OutfitLabel})."
            + " They switch together.", a.KeyCollisionTip);
        Assert.Equal($"Same key as {BuildVerbWords.ReplaceWord} on body (Vesna · {Group().OutfitLabel})."
            + " They switch together.", b.KeyCollisionTip);
        Assert.True(a.HasKeyCollision);
        Assert.True(b.HasKeyCollision);
        Assert.Empty(vm.BuildWarnings);
    }

    [Fact]
    public void Moving_one_of_them_off_the_key_clears_both()
    {
        var (_, a, b) = PaneWithTwoRows();
        a.ToggleKey = "F6";
        b.ToggleKey = "F6";

        b.ToggleKey = "F7";

        Assert.Equal("", a.KeyCollisionTip);
        Assert.Equal("", b.KeyCollisionTip);
    }

    [Fact]
    public void A_row_left_out_of_the_build_takes_its_key_out_of_the_read()
    {
        var (_, a, b) = PaneWithTwoRows();
        a.ToggleKey = "F6";
        b.ToggleKey = "F6";

        b.IsIncluded = false;

        // an unticked change binds nothing, so there is nothing left for the other to share with
        Assert.Equal("", a.KeyCollisionTip);
        Assert.Equal("", b.KeyCollisionTip);
    }

    [Fact]
    public void The_whole_mod_key_takes_part_and_says_so_on_its_own_control()
    {
        var (vm, a, _) = PaneWithTwoRows();
        a.ToggleKey = "F6";

        vm.PackageToggleKey = "F6";

        Assert.Equal($"Same key as {BuildVerbWords.ReplaceWord} on body (Vesna · {Group().OutfitLabel})."
            + " They switch together.", vm.PackageKeyCollisionTip);
        Assert.True(vm.HasPackageKeyCollision);
        Assert.Equal("Same key as the whole mod. They switch together.", a.KeyCollisionTip);
        Assert.Empty(vm.BuildWarnings);
    }

    [Fact]
    public void Two_outfits_of_one_character_on_one_key_name_each_others_outfit()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        var first = Group();
        var second = new BuildGroupVm
        {
            Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR02", OutfitLabel = "Skin  ·  Snowline",
        };
        // the same part, the same verb, the same character: only the outfit tells the two rows apart
        var a = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true);
        var b = new BuildRowVm(Edit(EditVerbs.Replace), "body", included: true);
        first.Rows.Add(a);
        second.Rows.Add(b);
        vm.AddBuildGroup(first);
        vm.AddBuildGroup(second);

        a.ToggleKey = "F6";
        b.ToggleKey = "F6";

        Assert.Equal($"Same key as {BuildVerbWords.ReplaceWord} on body (Vesna · {second.OutfitLabel})."
            + " They switch together.", a.KeyCollisionTip);
        Assert.Equal($"Same key as {BuildVerbWords.ReplaceWord} on body (Vesna · {first.OutfitLabel})."
            + " They switch together.", b.KeyCollisionTip);
    }

    [Fact]
    public void An_emptied_change_list_takes_the_whole_mod_glyph_with_it()
    {
        var (vm, a, _) = PaneWithTwoRows();
        a.ToggleKey = "F6";
        vm.PackageToggleKey = "F6";
        Assert.True(vm.HasPackageKeyCollision);
        vm.CaptureBuildBaseline();
        vm.LastBuildDir = @"C:\published\a mod";
        Assert.False(vm.BuildResultStale);

        // no game files, so the refresh stands the empty sentence in for the list
        vm.RefreshBuildPreview();

        Assert.False(vm.HasBuildRows);
        Assert.Equal("", vm.PackageKeyCollisionTip);   // the change it named is no longer listed
        Assert.True(vm.BuildResultStale);              // nor is any of what the build shipped
    }

    // ---- what a disabled Build/Install says while a run holds them off ----

    [Fact]
    public void A_run_in_flight_is_what_the_disabled_Build_and_Install_tips_say()
    {
        var vm = new MainWindowViewModel(startLoad: false);

        vm.IsModBuilding = true;

        // Both buttons are off FOR the run, so both have to name it — a readiness line under a dead button
        // reads as a broken button.
        Assert.False(vm.CanBuildMod);
        Assert.False(vm.CanInstallBuild);
        Assert.Equal(MainWindowViewModel.BuildRunningReason, vm.BuildDisabledReason);
        Assert.Equal(MainWindowViewModel.BuildRunningReason, vm.InstallDisabledReason);
        Assert.Equal(MainWindowViewModel.BuildRunningReason, vm.BuildButtonTip);
        Assert.Equal(MainWindowViewModel.BuildRunningReason, vm.InstallButtonTip);
        // the change list already answered this way; the three surfaces now give one answer
        Assert.Equal(MainWindowViewModel.BuildRunningReason, vm.BuildListReason);
    }

    [Fact]
    public void The_run_outranks_the_reason_that_would_stand_without_it()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        // with no game files and nothing built, each gate has its own standing reason
        var buildIdle = vm.BuildDisabledReason;
        var installIdle = vm.InstallDisabledReason;
        Assert.NotNull(buildIdle);
        Assert.NotNull(installIdle);

        vm.IsModBuilding = true;
        Assert.NotEqual(buildIdle, vm.BuildDisabledReason);
        Assert.NotEqual(installIdle, vm.InstallDisabledReason);

        // …and the standing reason comes back when the run ends, rather than the run's line sticking
        vm.IsModBuilding = false;
        Assert.Equal(buildIdle, vm.BuildDisabledReason);
        Assert.Equal(installIdle, vm.InstallDisabledReason);
    }

    // ---- which warnings the pane shows ----

    [Fact]
    public void A_warning_the_live_derivation_raises_reaches_the_pane_behind_a_completed_run()
    {
        // the field case: a build ran, then an edit made the list warn. A run that owned the surface alone
        // would draw its own empty warning set over the one line the modder has to read.
        var shown = BuildWarningSource.Current(
            runWarnings: Array.Empty<string>(),
            derivationWarnings: new[] { "'body' is replaced. Its replacement already carries its own RMO "
                + "map, so the texture edit is not in this build. Drop the edited image on the part's map "
                + "card to use it instead" });

        Assert.Single(shown.Lines);
        Assert.Equal(1, shown.Count);
    }

    [Fact]
    public void One_fact_reached_twice_in_a_run_is_one_line_on_the_pane()
    {
        // the same sentence is emitted once per material map and once per claimant of a shared resource
        const string same = "a donor map won't bind";
        var shown = BuildWarningSource.Current(
            runWarnings: new[] { same, "a tier can't serve the swap", same },
            derivationWarnings: Array.Empty<string>());

        Assert.Equal(new[] { BuildWarningSource.LastBuildLeadIn, same, "a tier can't serve the swap" },
            shown.Lines);
        // …and the footer's tally counts the warnings, not the lines: it must match the box it sits under
        Assert.Equal(2, shown.Count);
        Assert.Equal("Built a mod · 2 warning(s)", BuildFooter.Idle.Built("a mod", shown.Count).Text);
    }

    [Fact]
    public void The_built_folder_summary_counts_whole_megabytes_and_every_file()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllBytes(Path.Combine(_root, "a.buf"), new byte[1_600_000]);
        File.WriteAllBytes(Path.Combine(_root, "sub", "b.buf"), new byte[600_000]);

        // 2.2 MB over two files, one of them nested
        Assert.Equal("2 MB · 2 files", MainWindowViewModel.BuildOutputSummary(_root));
    }

    [Fact]
    public void The_built_folder_summary_is_empty_when_the_folder_cannot_be_walked()
    {
        Assert.Equal("", MainWindowViewModel.BuildOutputSummary(Path.Combine(_root, "gone")));
    }

    [Fact]
    public void The_built_folder_summary_rounds_a_half_megabyte_up()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "a.buf"), new byte[1024 * 1024 * 3 / 2]);

        // 1.5 MB reads as 2, never as 1 — a build never reports smaller than it is
        Assert.Equal("2 MB · 1 file", MainWindowViewModel.BuildOutputSummary(_root));
    }

    [Fact]
    public void A_one_file_build_reads_singular()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, "mod.ini"), new byte[10]);

        Assert.Equal("0 MB · 1 file", MainWindowViewModel.BuildOutputSummary(_root));
    }
}
