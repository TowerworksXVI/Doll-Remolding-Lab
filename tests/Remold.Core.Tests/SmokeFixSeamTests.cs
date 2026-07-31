using System;
using System.Collections.Generic;
using System.Linq;
using Remold.App.ViewModels;
using Remold.Core.Model;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Seam tests for App VM behaviour exercisable without the hosting VM or the Avalonia runtime: the auto
/// mod-name convention and its folder slug, and the character-level checkbox tri-state with its batch
/// add/remove routing.
/// </summary>
public class SmokeFixSeamTests
{
    // ---- auto-naming an unnamed project from the first subject -------------------------------------------

    [Theory]
    [InlineData("Ostra", "Ostra mod", "ostra-mod")]
    [InlineData("Sable", "Sable mod", "sable-mod")]
    [InlineData("Mirel", "Mirel mod", "mirel-mod")]
    public void AutoModName_FollowsTheOldConvention_AndSlugsForTheFolder(string character, string expectedName, string expectedSlug)
    {
        // Name an unnamed project "{Character} mod" and slug that for the folder.
        var name = MainWindowViewModel.AutoModName(character);
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedSlug, ModNaming.Slug(name));
    }

    // ---- the character-level "grab the whole character" checkbox ----------------------------------------

    private static CharacterVm MultiOutfitCharacter(int outfitCount,
        out List<CharacterVm> bulkCalls, out List<bool> bulkAddAll)
    {
        var calls = new List<CharacterVm>();
        var addAll = new List<bool>();
        bulkCalls = calls; bulkAddAll = addAll;

        var outfits = Enumerable.Range(0, outfitCount)
            .Select(i => new Outfit(i + 1, $"Stem{i}", OutfitKind.Alt))
            .ToList();
        var model = new Character(1, "Vesna", "Vesna", 1000, 0, outfits);

        var vm = new CharacterVm(model,
            onSubjectToggle: (_, _) => { },                       // per-outfit toggle — not exercised here
            onCharacterToggle: (c, add) => { calls.Add(c); addAll.Add(add); });
        vm.Populate(outfits.Select(o => (o, (IEnumerable<string>)Array.Empty<string>())), lightUp: true);
        return vm;
    }

    [Fact]
    public void CharacterInMod_ReflectsAggregateOutfitState()
    {
        var vm = MultiOutfitCharacter(3, out _, out _);

        // none in mod → unchecked
        Assert.False(vm.CharacterInMod);
        Assert.True(vm.ShowCharacterBox);

        // one in mod → indeterminate DISPLAY (mixed)
        vm.Outfits[0].SetInModSilently(true);
        vm.RefreshSubjectState();
        Assert.Null(vm.CharacterInMod);

        // all in mod → checked
        vm.Outfits[1].SetInModSilently(true);
        vm.Outfits[2].SetInModSilently(true);
        vm.RefreshSubjectState();
        Assert.True(vm.CharacterInMod);
    }

    [Fact]
    public void ClickingCharacterBox_FromNone_or_Mixed_AddsAll_FromAll_RemovesAll()
    {
        var vm = MultiOutfitCharacter(3, out var calls, out var addAll);

        // from NONE: a click adds all
        vm.CharacterInMod = true;    // the value is ignored; the aggregate drives the decision
        Assert.Single(calls);
        Assert.True(addAll[^1]);

        // from MIXED: a click STILL adds all (never steps toward unchecked)
        vm.Outfits[0].SetInModSilently(true);
        vm.RefreshSubjectState();
        vm.CharacterInMod = false;   // three-state would cycle toward unchecked; we override to add-all
        Assert.Equal(2, calls.Count);
        Assert.True(addAll[^1]);

        // from ALL: a click removes all
        vm.Outfits[1].SetInModSilently(true);
        vm.Outfits[2].SetInModSilently(true);
        vm.RefreshSubjectState();
        vm.CharacterInMod = null;
        Assert.Equal(3, calls.Count);
        Assert.False(addAll[^1]);
    }

    [Fact]
    public void SingleOutfitCharacter_ShowsNoCharacterBox()
    {
        // a single-outfit character collapses to the subject checkbox; there's nothing to aggregate.
        var outfit = new Outfit(1, "Stem0", OutfitKind.Base);
        var model = new Character(1, "Solo", "Solo", 1000, 0, new List<Outfit> { outfit });
        var vm = new CharacterVm(model, (_, _) => { }, (_, _) => { });
        vm.Populate(new[] { (outfit, (IEnumerable<string>)Array.Empty<string>()) }, lightUp: true);

        Assert.True(vm.IsSingleOutfit);
        Assert.False(vm.ShowCharacterBox);
        Assert.True(vm.ShowSubjectBox);
    }
}
