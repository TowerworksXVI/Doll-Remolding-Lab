using System;
using System.Collections.Generic;
using Remold.App;
using Remold.App.ViewModels;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Project;
using Remold.Core.Text;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The sentences the app composes from several catalog entries: each picks the entry its count or
/// state asks for, and fills it with what the surface names.</summary>
[Collection("Dispatcher")]
public class SentenceRouteTests
{
    [Fact]
    public void A_round_trip_id_that_cannot_be_saved_names_the_round_trip_and_steers_to_saving()
    {
        Assert.Equal(Strings.RoundTripIdNotSavedBlender + " " + Strings.SaveFailedSteer,
            MainWindowViewModel.RoundTripIdNotSaved(blender: true));
        Assert.Equal(Strings.RoundTripIdNotSavedImageEditor + " " + Strings.SaveFailedSteer,
            MainWindowViewModel.RoundTripIdNotSaved(blender: false));
    }

    [Fact]
    public void A_failed_save_on_quit_or_leave_names_the_mod_and_an_unnamed_one_as_untitled()
    {
        Assert.Equal(Strings.QuitSaveFailedBody("Vesna casual", "Disk full."),
            MainWindowViewModel.SaveFailedBody(quitting: true, " Vesna casual ", "Disk full."));
        Assert.Equal(Strings.LeaveSaveFailedBody("Vesna casual", "Disk full."),
            MainWindowViewModel.SaveFailedBody(quitting: false, "Vesna casual", "Disk full."));
        Assert.Equal(Strings.QuitSaveFailedBody(Strings.UntitledModDisplay, "Disk full."),
            MainWindowViewModel.SaveFailedBody(quitting: true, "  ", "Disk full."));
        Assert.Equal(Strings.LeaveSaveFailedBody(Strings.UntitledModDisplay, "Disk full."),
            MainWindowViewModel.SaveFailedBody(quitting: false, null, "Disk full."));
    }

    [Fact]
    public void An_opened_or_imported_mod_says_how_many_edits_came_with_it()
    {
        Assert.Equal(Strings.OpenedMod("Vesna casual"), MainWindowViewModel.LandedLine(false, "Vesna casual", 0));
        Assert.Equal(Strings.OpenedModEdits_One("Vesna casual", 1),
            MainWindowViewModel.LandedLine(false, "Vesna casual", 1));
        Assert.Equal(Strings.OpenedModEdits_Other("Vesna casual", 3),
            MainWindowViewModel.LandedLine(false, "Vesna casual", 3));
        Assert.Equal(Strings.ImportedMod("Vesna casual"), MainWindowViewModel.LandedLine(true, "Vesna casual", 0));
        Assert.Equal(Strings.ImportedModEdits_One("Vesna casual", 1),
            MainWindowViewModel.LandedLine(true, "Vesna casual", 1));
        Assert.Equal(Strings.ImportedModEdits_Other("Vesna casual", 2),
            MainWindowViewModel.LandedLine(true, "Vesna casual", 2));
    }

    [Fact]
    public void The_changed_parts_notice_names_each_part_by_its_slot_where_no_model_has_loaded()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        TargetPart Part(string slot) => new() { Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = slot };

        Assert.Equal(Strings.ChangedTargets_One(1, "c_body_lod0"),
            vm.ChangedTargetsDetail(new[] { Part("c_body_lod0") }));
        Assert.Equal(Strings.ChangedTargets_Other(2, "c_body_lod0" + CoreStrings.ListSeparator + "c_cape_lod0"),
            vm.ChangedTargetsDetail(new[] { Part("c_body_lod0"), Part("c_cape_lod0") }));
    }

    [Fact]
    public void Parts_with_nowhere_to_record_are_named_up_to_three_and_counted_past_that()
    {
        string sep = CoreStrings.ListSeparator;
        Assert.Equal(Strings.NowhereToRecord_One("a"), MainWindowViewModel.PartsWithNowhereToRecord(new[] { "a" }));
        Assert.Equal(Strings.NowhereToRecord_Other("a" + sep + "b" + sep + "c"),
            MainWindowViewModel.PartsWithNowhereToRecord(new[] { "a", "b", "c" }));
        Assert.Equal(Strings.NowhereToRecord_Other(Strings.NowhereToRecordAndMore_One("a" + sep + "b" + sep + "c", 1)),
            MainWindowViewModel.PartsWithNowhereToRecord(new[] { "a", "b", "c", "d" }));
        Assert.Equal(Strings.NowhereToRecord_Other(Strings.NowhereToRecordAndMore_Other("a" + sep + "b" + sep + "c", 2)),
            MainWindowViewModel.PartsWithNowhereToRecord(new[] { "a", "b", "c", "d", "e" }));
    }

    [Fact]
    public void A_shading_write_that_sets_and_returns_values_counts_each_half()
    {
        Assert.Equal(Strings.ShadingSetReturned_One_One(1, 1), EditPageVm.ShadingWriteLine(1, 1));
        Assert.Equal(Strings.ShadingSetReturned_One_Other(1, 2), EditPageVm.ShadingWriteLine(1, 2));
        Assert.Equal(Strings.ShadingSetReturned_Other_One(2, 1), EditPageVm.ShadingWriteLine(2, 1));
        Assert.Equal(Strings.ShadingSetReturned_Other_Other(3, 2), EditPageVm.ShadingWriteLine(3, 2));
        Assert.Equal(Strings.ShadingSetValues_Other(2), EditPageVm.ShadingWriteLine(2, 0));
        Assert.Equal(Strings.ShadingReturnedValues_One(1), EditPageVm.ShadingWriteLine(0, 1));
    }

    [Fact]
    public void A_copy_names_the_values_the_source_material_does_not_state()
    {
        Assert.Equal("", EditPageVm.CopySkippedLine(Array.Empty<string>()));
        Assert.Equal(Strings.EditCopyLeavesUnchanged_One("Glitter"), EditPageVm.CopySkippedLine(new[] { "Glitter" }));
        Assert.Equal(Strings.EditCopyLeavesUnchanged_Other("Glitter" + CoreStrings.ListSeparator + "Rim"),
            EditPageVm.CopySkippedLine(new List<string> { "Glitter", "Rim" }));
    }

    [Fact]
    public void A_picked_ramp_card_carries_the_owned_glyph_and_a_kept_original_does_not()
    {
        Assert.True(RampCardState.Picked("ramp.dds").IsOwned);
        Assert.Equal(RampCardState.OwnedGlyph, RampCardState.Picked("ramp.dds").Glyph);
        Assert.Equal(Strings.RampCardChosen, RampCardState.Picked("ramp.dds").Caption);
        Assert.False(RampCardState.VanillaOptedOut.IsOwned);
        Assert.Equal(Strings.RampCardKept, RampCardState.VanillaOptedOut.Caption);
        Assert.False(RampCardState.Vanilla.IsOwned);
    }
}
