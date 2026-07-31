using System.Collections.Generic;
using Remold.Core.Model;
using Remold.Core.Tables;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The shared key→label helper every UI surface routes a name through. Two entry points — the direct
/// <c>Label</c> over an enriched model, and the reverse lookup over an internal KEY — share one designed
/// fallback: a nameless key renders as its own token, never empty and never a guess.
/// </summary>
public class FriendlyNamesTests
{
    // Vireo has a localized name and one named outfit; the enemy has neither (the token-fallback case).
    private static List<Character> Roster() => new()
    {
        new(CharId: 42, Name: "Vireo", Family: "VireoSSR", GunId: 1071, DormModelConfigId: 107199,
            Outfits: new List<Outfit>
            {
                new(107101, "VireoSSR0101", OutfitKind.Alt) { DisplayName = "Plum Fizz" },
            }) { DisplayName = "Sable" },
        new(CharId: 0, Name: "Goblin", Family: "", GunId: 0, DormModelConfigId: 0,
            Outfits: new List<Outfit> { new(0, "EnemyGoblin01", OutfitKind.Other) }),   // nameless
    };

    // ---- direct Label (caller holds the enriched model) ----

    [Fact]
    public void Label_Character_UsesLocalizedNameWhenPresent()
    {
        var c = Roster()[0];
        Assert.Equal("Sable", FriendlyNames.Label(c));
    }

    [Fact]
    public void Label_Character_FallsBackToInternalName_WhenNameless()
    {
        var c = Roster()[1];
        Assert.Equal("Goblin", FriendlyNames.Label(c));   // the designed token fallback, not empty
    }

    [Fact]
    public void Label_Outfit_UsesLocalizedNameWhenPresent()
    {
        var o = Roster()[0].Outfits[0];
        Assert.Equal("Plum Fizz", FriendlyNames.Label(o));
    }

    [Fact]
    public void Label_Outfit_FallsBackToStem_WhenNameless()
    {
        var o = Roster()[1].Outfits[0];
        Assert.Equal("EnemyGoblin01", FriendlyNames.Label(o));   // stem token, not empty
    }

    // ---- KindAndLabel: the ONE "<kind> · <name>" home shared by every surface that shows one ----

    [Fact]
    public void KindAndLabel_LeadsWithKind_ThenLocalizedName()
    {
        var o = Roster()[0].Outfits[0];   // Alt outfit with a localized name
        Assert.Equal("Alt  ·  Plum Fizz", FriendlyNames.KindAndLabel(o));
    }

    [Fact]
    public void KindAndLabel_OtherKind_EmitsBareLabel_NoKindSegment()
    {
        // "Other" is the internal catch-all, not a marketing category, so it never leaks into the label.
        var o = Roster()[1].Outfits[0];   // nameless enemy outfit, OutfitKind.Other
        Assert.Equal("EnemyGoblin01", FriendlyNames.KindAndLabel(o));
    }

    [Fact]
    public void KindAndLabel_OtherKind_WithName_EmitsBareLabel()
    {
        // an Other-kind model that DOES carry a localized name still drops the kind segment.
        var o = new Outfit(0, "NpcMerchant01", OutfitKind.Other) { DisplayName = "Traveling Merchant" };
        Assert.Equal("Traveling Merchant", FriendlyNames.KindAndLabel(o));
    }

    [Fact]
    public void KindAndLabel_BaseOutfit_RendersBasePrefix()
    {
        // a Base outfit reads "Base · <name>".
        var o = new Outfit(2001, "HyperTriggerSSR01", OutfitKind.Base) { DisplayName = "Hyper Trigger" };
        Assert.Equal("Base  ·  Hyper Trigger", FriendlyNames.KindAndLabel(o));
    }

    // ---- reverse lookup (caller holds only the internal key — the ledger/subfolder/manifest case) ----

    [Fact]
    public void Character_ReverseLookup_ResolvesInternalNameToLocalized()
    {
        var f = FriendlyNames.FromRoster(Roster());
        // this is the "Vireo → Sable" render on the Edit/Package/mod-name surfaces
        Assert.Equal("Sable", f.Character("Vireo"));
    }

    [Fact]
    public void Character_ReverseLookup_IsCaseInsensitiveOnTheKey()
    {
        var f = FriendlyNames.FromRoster(Roster());
        // the selection-ledger match is OrdinalIgnoreCase, so the reverse map must be too
        Assert.Equal("Sable", f.Character("viREo"));
    }

    [Fact]
    public void Character_ReverseLookup_UnknownKeyFallsBackToItself()
    {
        var f = FriendlyNames.FromRoster(Roster());
        // a character the roster doesn't carry renders its own key, never empty
        Assert.Equal("Goblin", f.Character("Goblin"));
        Assert.Equal("Nonexistent", f.Character("Nonexistent"));
    }

    [Fact]
    public void Outfit_ReverseLookup_ResolvesStemToLocalized()
    {
        var f = FriendlyNames.FromRoster(Roster());
        Assert.Equal("Plum Fizz", f.Outfit("VireoSSR0101"));
    }

    [Fact]
    public void Outfit_ReverseLookup_UnknownStemFallsBackToStem()
    {
        var f = FriendlyNames.FromRoster(Roster());
        Assert.Equal("EnemyGoblin01", f.Outfit("EnemyGoblin01"));
    }

    [Fact]
    public void EmptyKeys_RoundTripUnchanged()
    {
        var f = FriendlyNames.FromRoster(Roster());
        Assert.Equal("", f.Character(""));
        Assert.Equal("", f.Outfit(""));
    }

    // ---- the pre-scan window: Empty resolver falls everything back to its token ----

    [Fact]
    public void Empty_ResolvesEverythingToItsOwnKey()
    {
        // Before the scan delivers names callers use FriendlyNames.Empty, so every label reads as the
        // internal key rather than special-casing a null resolver.
        Assert.Equal("Vireo", FriendlyNames.Empty.Character("Vireo"));
        Assert.Equal("VireoSSR0101", FriendlyNames.Empty.Outfit("VireoSSR0101"));
    }

    // ---- first-writer-wins on a duplicate internal key (the ledger match already tolerates this) ----

    [Fact]
    public void FromRoster_DuplicateInternalName_KeepsFirst_DoesNotThrow()
    {
        var roster = new List<Character>
        {
            new(1, "Dup", "", 1, 0, new List<Outfit>()) { DisplayName = "First" },
            new(2, "Dup", "", 2, 0, new List<Outfit>()) { DisplayName = "Second" },
        };
        var f = FriendlyNames.FromRoster(roster);   // must not throw on the repeated key
        Assert.Equal("First", f.Character("Dup"));
    }
}
