using System;
using System.Collections.Generic;
using System.IO;
using Remold.App.ViewModels;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The stale line under a build result. It reads a SIGNATURE of what the last build consumed against what
/// the list holds now, so every "show it" case has a matching "take it back off" case: the modder who
/// unticks a row and re-ticks it is looking at the same mod the build shipped.
/// </summary>
[Collection("Dispatcher")]
public class BuildStaleResultTests
{
    private static MeshEdit Edit(string verb, string mesh) => new()
    {
        Character = "Vesna", Outfit = "VesnaSSR01", Mesh = mesh, Verb = verb,
    };

    /// <summary>A pane with one built result on screen and two ticked rows under it.</summary>
    private static (MainWindowViewModel Vm, BuildRowVm A, BuildRowVm B) BuiltPane()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        var group = new BuildGroupVm
        {
            Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR01", OutfitLabel = "Base",
        };
        var a = new BuildRowVm(Edit(EditVerbs.Replace, "c_vesna01_body_lod0"), "body", included: true);
        var b = new BuildRowVm(Edit(EditVerbs.Hide, "c_vesna01_cloth1_lod0"), "cloth1", included: true);
        group.Rows.Add(a);
        group.Rows.Add(b);
        vm.AddBuildGroup(group);
        // the author field is remembered across sessions, so pin the whole form before the baseline
        vm.PackageName = "a mod";
        vm.PackageAuthor = "";
        vm.PackageVersion = "1.0";
        vm.PackageDescription = "";
        vm.PackageToggleKey = null;
        vm.CaptureBuildBaseline();                 // the run starts reading the list
        vm.LastBuildDir = @"C:\published\a mod";   // its result lands and commits that baseline
        Assert.False(vm.BuildResultStale);
        return (vm, a, b);
    }

    [Fact]
    public void With_no_build_on_screen_nothing_is_stale()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        var group = new BuildGroupVm
        {
            Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR01", OutfitLabel = "Base",
        };
        var row = new BuildRowVm(Edit(EditVerbs.Replace, "c_vesna01_body_lod0"), "body", included: true);
        group.Rows.Add(row);
        vm.AddBuildGroup(group);

        row.IsIncluded = false;
        vm.PackageName = "renamed";

        Assert.False(vm.BuildResultStale);   // there is no result bar for the line to sit on
    }

    [Fact]
    public void Unticking_a_row_shows_the_line_and_re_ticking_it_takes_the_line_back_off()
    {
        var (vm, a, _) = BuiltPane();

        a.IsIncluded = false;
        Assert.True(vm.BuildResultStale);

        a.IsIncluded = true;
        Assert.False(vm.BuildResultStale);
    }

    [Fact]
    public void Two_rows_out_and_only_one_back_leaves_the_line_up()
    {
        var (vm, a, b) = BuiltPane();

        a.IsIncluded = false;
        b.IsIncluded = false;
        b.IsIncluded = true;

        Assert.True(vm.BuildResultStale);   // 'a' is still out, so the folder still doesn't match
    }

    [Fact]
    public void Binding_a_change_key_shows_the_line_and_clearing_it_again_takes_it_off()
    {
        var (vm, a, _) = BuiltPane();

        a.ToggleKey = "F6";
        Assert.True(vm.BuildResultStale);

        a.ToggleKey = null;
        Assert.False(vm.BuildResultStale);
    }

    [Fact]
    public void A_key_re_read_in_another_case_is_the_same_key()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        var group = new BuildGroupVm
        {
            Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR01", OutfitLabel = "Base",
        };
        var row = new BuildRowVm(Edit(EditVerbs.Replace, "c_vesna01_body_lod0"), "body", included: true,
            toggleKey: "F6");
        group.Rows.Add(row);
        vm.AddBuildGroup(group);
        vm.CaptureBuildBaseline();
        vm.LastBuildDir = @"C:\published\a mod";

        row.ToggleKey = "f6";

        // the build emits the normalized key, so a case change is not a change to what shipped
        Assert.False(vm.BuildResultStale);
    }

    /// <summary>The build emits how a key starts and what its off state means, so both belong to what the
    /// result was measured against.</summary>
    [Fact]
    public void Changing_how_a_key_starts_or_what_off_means_makes_the_result_stale()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        var group = new BuildGroupVm
        {
            Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR01", OutfitLabel = "Base",
        };
        var row = new BuildRowVm(Edit(EditVerbs.Replace, "c_vesna01_body_lod0"), "body", included: true,
            toggleKey: "F6");
        group.Rows.Add(row);
        vm.AddBuildGroup(group);
        vm.CaptureBuildBaseline();
        vm.LastBuildDir = @"C:\published\a mod";

        row.StartsOff = true;
        Assert.True(vm.BuildResultStale);

        row.StartsOff = false;
        Assert.False(vm.BuildResultStale);

        row.HideWhenOff = true;
        Assert.True(vm.BuildResultStale);
    }

    [Fact]
    public void Editing_the_mod_identity_shows_the_line_and_typing_it_back_takes_it_off()
    {
        var (vm, _, _) = BuiltPane();

        vm.PackageName = "a mod 2";
        Assert.True(vm.BuildResultStale);

        vm.PackageName = "a mod";
        Assert.False(vm.BuildResultStale);
    }

    [Fact]
    public void The_whole_mod_key_and_the_other_identity_fields_count_too()
    {
        var (vm, _, _) = BuiltPane();

        foreach (var edit in new List<Action>
                 {
                     () => vm.PackageToggleKey = "F7",
                     () => vm.PackageAuthor = "someone",
                     () => vm.PackageVersion = "2.0",
                     () => vm.PackageDescription = "what it does",
                 })
        {
            edit();
            Assert.True(vm.BuildResultStale);
            vm.PackageToggleKey = null;
            vm.PackageAuthor = "";
            vm.PackageVersion = "1.0";
            vm.PackageDescription = "";
            Assert.False(vm.BuildResultStale);
        }
    }

    [Fact]
    public void A_second_build_takes_the_list_as_that_run_read_it_as_the_baseline()
    {
        var (vm, a, _) = BuiltPane();
        a.IsIncluded = false;
        Assert.True(vm.BuildResultStale);

        // what a build run does to the surface: it reads the list, the old result goes, the new one lands
        vm.CaptureBuildBaseline();
        vm.LastBuildDir = "";
        vm.LastBuildDir = @"C:\published\a mod";

        Assert.False(vm.BuildResultStale);
        a.IsIncluded = true;
        Assert.True(vm.BuildResultStale);   // back to what the FIRST build shipped, not the second
    }

    [Fact]
    public void An_edit_made_while_the_run_is_going_leaves_its_result_stale_the_moment_it_lands()
    {
        var (vm, a, _) = BuiltPane();

        // the Edit pane stays live through a build, so the list can move after the run has read it
        vm.CaptureBuildBaseline();
        a.IsIncluded = false;
        vm.LastBuildDir = "";
        vm.LastBuildDir = @"C:\published\a mod";

        // the folder that just landed was built without that edit, and says so
        Assert.True(vm.BuildResultStale);
    }

    // ---- the bar across a step hop ----

    [Fact]
    public void The_result_bar_survives_a_hop_out_of_the_step_and_back()
    {
        var (vm, _, _) = BuiltPane();

        vm.EnterBuildStep();   // ② Edit and back

        // the folder it names is still on disk and still installable, so the bar and its actions stay
        Assert.True(vm.HasLastBuild);
        Assert.Equal(@"C:\published\a mod", vm.LastBuildDir);
    }

    [Fact]
    public void A_returning_bar_reads_its_line_off_the_list_as_it_now_stands()
    {
        var (vm, a, _) = BuiltPane();
        var group = vm.BuildGroups[0];

        // This VM has no game files, so the re-entry's derivation stands the empty sentence in for the list —
        // and an empty list is a real difference from what shipped.
        vm.EnterBuildStep();
        Assert.True(vm.HasLastBuild);
        Assert.True(vm.BuildResultStale);

        // the same list back, untouched: the bar matches what it shipped again
        vm.AddBuildGroup(group);
        a.IsIncluded = false;
        a.IsIncluded = true;
        Assert.True(vm.HasLastBuild);
        Assert.False(vm.BuildResultStale);
    }

    [Fact]
    public void An_edit_made_while_away_leaves_the_returning_bar_stale()
    {
        var (vm, a, _) = BuiltPane();
        var group = vm.BuildGroups[0];

        a.IsIncluded = false;   // unticked from the Build pane, then away and back
        vm.EnterBuildStep();
        vm.AddBuildGroup(group);
        a.IsIncluded = true;
        a.IsIncluded = false;

        Assert.True(vm.HasLastBuild);
        Assert.True(vm.BuildResultStale);
    }

    [Fact]
    public void Switching_mod_drops_the_bar_the_step_hop_kept()
    {
        var (vm, _, _) = BuiltPane();

        vm.NewMod();

        // the folder it named belongs to the mod being left
        Assert.False(vm.HasLastBuild);
        Assert.False(vm.BuildResultStale);
    }

    /// <summary>A mod with one shipping mesh target and a built result on screen, both on disk under
    /// <paramref name="root"/> — the signature reads the target the same way the build does, which means
    /// asking whether its file is really there.</summary>
    private static (MainWindowViewModel Vm, ProjectTarget Target) ReplacedPane(string root, string file = "body.glb")
    {
        var vm = new MainWindowViewModel(startLoad: false);
        vm.OpenProject.RootDir = root;
        var target = Replaced(vm.OpenProject, root, file);
        var group = new BuildGroupVm
        {
            Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR01", OutfitLabel = "Base",
        };
        group.Rows.Add(new BuildRowVm(Edit(EditVerbs.Replace, "c_vesna01_body_lod0"), "body", included: true));
        vm.AddBuildGroup(group);
        vm.CaptureBuildBaseline();
        vm.LastBuildDir = @"C:\published\a mod";
        Assert.False(vm.BuildResultStale);
        return (vm, target);
    }

    /// <summary>A shipping mesh target for the body slot: its workspace file on disk, no original on record
    /// (which is what "edited" means for a replacement).</summary>
    private static ProjectTarget Replaced(ModProject project, string root, string file)
    {
        File.WriteAllText(Path.Combine(root, file), "glb");
        var target = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01", ReplaceFile = file, OriginalFile = null,
        };
        project.Targets.Add(target);
        return target;
    }

    private static List<SubmeshTextures> OneMap(string png) => new()
    {
        new() { Submesh = 0, Albedo = png, AlbedoOrigin = SlotOrigin.Authored },
    };

    /// <summary>A change row says WHICH part a build replaces, never which maps that replacement ships. An
    /// adoption or a card drop after the build changes the second without touching the first, and Install
    /// would otherwise copy the pre-edit folder under a ✓ that still reads clean.</summary>
    [Fact]
    public void A_map_the_replacement_took_on_after_the_build_makes_the_result_stale()
    {
        using var temp = new TempRoot();
        var (vm, target) = ReplacedPane(temp.Root);

        // what an adoption writes: the edited workspace PNG becomes the replacement's own base colour
        target.DonorTextures = OneMap("skin_d.png");
        vm.RefreshBuildResultStale();

        Assert.True(vm.BuildResultStale);

        // …and putting the map back the way the build found it takes the line off again
        target.DonorTextures = null;
        vm.RefreshBuildResultStale();
        Assert.False(vm.BuildResultStale);
    }

    /// <summary>One subject can hold more than one target for the same mesh slot — a stranded one an earlier
    /// materialize left behind, beside the one that ships. The signature has to read the SHIPPING one: a map
    /// taken on since the build would otherwise be answered for by a target the build never shipped, and the
    /// bar would read clean over a folder that no longer matches the mod.</summary>
    [Fact]
    public void With_two_targets_for_one_slot_the_signature_reads_the_one_that_ships()
    {
        using var temp = new TempRoot();
        var vm = new MainWindowViewModel(startLoad: false);
        vm.OpenProject.RootDir = temp.Root;

        // the stranded one comes FIRST in the list, so a pick by name alone lands on it
        var stranded = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01", ReplaceFile = "gone/body.glb",
        };
        vm.OpenProject.Targets.Add(stranded);
        var shipping = Replaced(vm.OpenProject, temp.Root, "body.glb");
        var group = new BuildGroupVm
        {
            Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR01", OutfitLabel = "Base",
        };
        group.Rows.Add(new BuildRowVm(Edit(EditVerbs.Replace, "c_vesna01_body_lod0"), "body", included: true));
        vm.AddBuildGroup(group);
        vm.CaptureBuildBaseline();
        vm.LastBuildDir = @"C:\published\a mod";
        Assert.False(vm.BuildResultStale);

        // a map on the target that ships is a real difference from what the build shipped
        shipping.DonorTextures = OneMap("skin_d.png");
        vm.RefreshBuildResultStale();
        Assert.True(vm.BuildResultStale);

        shipping.DonorTextures = null;
        vm.RefreshBuildResultStale();
        Assert.False(vm.BuildResultStale);

        // …and one on the stranded target is not: nothing about it reaches the built folder
        stranded.DonorTextures = OneMap("stranded.png");
        vm.RefreshBuildResultStale();
        Assert.False(vm.BuildResultStale);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "remold-stale-" + Guid.NewGuid().ToString("N"));
        public TempRoot() => Directory.CreateDirectory(Root);
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
    }

    [Fact]
    public void A_result_that_lands_before_any_run_has_read_the_list_owns_no_baseline()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        var group = new BuildGroupVm
        {
            Character = "Vesna", RawCharacter = "vesna", Outfit = "VesnaSSR01", OutfitLabel = "Base",
        };
        group.Rows.Add(new BuildRowVm(Edit(EditVerbs.Replace, "c_vesna01_body_lod0"), "body", included: true));
        vm.AddBuildGroup(group);

        vm.LastBuildDir = @"C:\published\a mod";

        // nothing consumed this list, so the bar cannot claim to match it
        Assert.True(vm.BuildResultStale);
    }
}
