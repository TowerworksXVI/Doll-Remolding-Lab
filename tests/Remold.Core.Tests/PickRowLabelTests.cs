using System;
using System.Linq;
using Remold.App.ViewModels;
using Remold.Core.Model;
using Remold.Core.Tables;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// What a Pick roster row is LABELLED. The tree row is the only place a curated skin's own name reaches the
/// modder, and the fallback that stands in for a nameless outfit is the model stem — so a label path that
/// stops consuming <see cref="Outfit.DisplayName"/> shows a raw stem where a curated name belongs, and looks
/// like data loss rather than a formatting slip.
///
/// <para>The row is built from the roster <see cref="Outfit"/> itself, which is why the launch's two fill
/// routes — a fresh confirm fill and a snapshot hit — cannot label differently: the snapshot carries part
/// tokens keyed by ModelConfigId, never outfits, so both routes hand <see cref="OutfitVm"/> the same
/// object.</para>
/// </summary>
public class PickRowLabelTests
{
    private static string Label(Outfit outfit) =>
        new OutfitVm(outfit, new[] { "body" }, _ => { }).Label;

    [Fact]
    public void ADisplayNameWins_AndTheStemTrailsBehindIt()
    {
        var outfit = new Outfit(-1, "Wren_dorm", OutfitKind.Other) { DisplayName = "Barracks" };

        // OutfitKind.Other renders BARE, so the curated string is the whole leading label
        Assert.StartsWith("Barracks", Label(outfit), StringComparison.Ordinal);
        Assert.Contains("Wren_dorm", Label(outfit), StringComparison.Ordinal);   // the stem still identifies the asset
    }

    [Fact]
    public void AKindedOutfitKeepsItsKind_AheadOfTheDisplayName()
    {
        var outfit = new Outfit(101, "WrenSSR0101", OutfitKind.Alt) { DisplayName = "Plum Fizz" };
        Assert.StartsWith("Alt", Label(outfit), StringComparison.Ordinal);
        Assert.Contains("Plum Fizz", Label(outfit), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutADisplayName_TheStemIsTheLabel_AndDoesNotRepeat()
    {
        // the nameless case is the majority (most enemy/prop/dorm stems have no localized name), and the
        // stem must not then print twice — once as the label, once as the trailing detail
        Assert.Equal("WrenSSR01", Label(new Outfit(1, "WrenSSR01", OutfitKind.Other)));
    }

    [Fact]
    public void EveryShippedCuratedSkin_LeadsWithItsCuratedLabel_NotItsStem()
    {
        // the real table, because these strings ARE the feature: a row leading with "Mayling_dorm" instead
        // of "Barracks" is the regression this pins
        foreach (var skin in CuratedSkins.All)
            Assert.StartsWith(skin.OutfitDisplay, Label(skin.ToOutfit()), StringComparison.Ordinal);

        var mayling = CuratedSkins.All.Where(e => e.Character == "Mayling").ToList();
        Assert.Equal(new[] { "Barracks", "Crew Deck" },
            mayling.Select(e => Label(e.ToOutfit()).Split("  ·  ")[0]).ToArray());
    }
}
