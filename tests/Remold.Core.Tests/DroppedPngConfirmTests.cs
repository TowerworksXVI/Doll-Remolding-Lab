using System;
using Remold.App.ViewModels;
using Remold.App.ViewModels.Workbench;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// What the "Apply dropped image" dialog says. Three routes reach it and only one of them is reversible,
/// so the body is the one place the modder learns which they are about to take: a card on a replaced part
/// still shows the game texture's name, and nothing else on screen distinguishes the cases.
/// </summary>
public class DroppedPngConfirmTests
{
    private static DroppedPngConfirm Game(int otherWearers = 0, string? sizeNote = null) =>
        new("paint.png", "Base color", "c_stem_body1_d", "body1", null, false, 0, otherWearers, sizeNote);

    private static DroppedPngConfirm Donor(int submeshes = 1, int authoredLanding = 0,
        DonorMapSlot slot = DonorMapSlot.BaseColor) =>
        new("paint.png", slot == DonorMapSlot.Rmo ? "RMO" : "Base color", "c_stem_body1_d", "body1",
            new DonorMapDrop("body1", slot, Landing(submeshes)), false, authoredLanding, 0, null);

    private static DroppedPngConfirm Authored() =>
        new("paint.png", "Base color", "c_stem_body1_d", "body1", null, true, 0, 0, null);

    private static int[] Landing(int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i;
        return a;
    }

    private static string Body(DroppedPngConfirm ask) => MainWindowViewModel.DroppedPngConfirmBody(ask).Body;
    private static bool Danger(DroppedPngConfirm ask) => MainWindowViewModel.DroppedPngConfirmBody(ask).Danger;

    // ---- the game-texture route: reversible, and it stays plain ----

    [Fact]
    public void GameRoute_NamesTheCardAndCarriesNoWarning()
    {
        var body = Body(Game());

        Assert.Equal("Apply paint.png to Base color · c_stem_body1_d?", body);
        Assert.False(Danger(Game()));
        Assert.DoesNotContain("revert", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One game map often dresses several parts, and the card shows only the one it sits on. The
    /// edit reaches all of them, so its reach is said before it lands.</summary>
    [Fact]
    public void GameRoute_WithOtherWearers_StatesTheReach()
    {
        Assert.Contains("Also drawn by 3 other parts.", Body(Game(otherWearers: 3)));
        Assert.Contains("Also drawn by 1 other part.", Body(Game(otherWearers: 1)));
        Assert.DoesNotContain("Also drawn by", Body(Game(otherWearers: 0)));
    }

    [Fact]
    public void GameRoute_SizeNote_IsShownAsGiven()
    {
        Assert.Contains("\n\n16×16, the map is 64×32. It still applies; UVs stretch it to fit.",
            Body(Game(sizeNote: "16×16, the map is 64×32. It still applies; UVs stretch it to fit.")));
    }

    // ---- the donor route: the FIRST drop, which is what creates the irreversible state ----

    [Fact]
    public void DonorRoute_NamesThePartAndSaysTheGameTextureIsUntouched()
    {
        var body = Body(Donor());

        Assert.StartsWith("Apply paint.png as body1's Base color?", body);
        Assert.Contains("body1 is replaced, so this becomes the replacement's own map. "
            + "c_stem_body1_d is untouched.", body);
    }

    /// <summary>The first drop is what makes the part's maps unrevertible one at a time, so it is the drop
    /// that has to say so — not only the second one landing on an already-authored card. The line leads with
    /// what the modder loses, not with what the app can't offer.</summary>
    [Fact]
    public void DonorRoute_CarriesTheNoRevertLineAndTheDangerStyling()
    {
        Assert.EndsWith("\n\nThe only way back is reverting the part, which discards its mesh edit too.",
            Body(Donor()));
        Assert.True(Danger(Donor()));
    }

    [Fact]
    public void DonorRoute_SeveralSubmeshes_SaysHowMany()
    {
        Assert.Contains("Applies to 3 submeshes.", Body(Donor(submeshes: 3)));
        Assert.DoesNotContain("Applies to", Body(Donor(submeshes: 1)));
    }

    /// <summary>An RMO's alpha is the emissive mask and glTF cannot carry one, so the shipped file takes its
    /// alpha off the game map. A modder who painted a mask into the dropped image has to learn that before
    /// the drop, not from the picture afterwards.</summary>
    [Fact]
    public void DonorRoute_Rmo_DisclosesWhereTheAlphaComesFrom()
    {
        Assert.Contains("Alpha comes from the game map's emissive mask. The dropped file's own alpha doesn't ship.",
            Body(Donor(slot: DonorMapSlot.Rmo)));
        Assert.DoesNotContain("Alpha comes from", Body(Donor(slot: DonorMapSlot.BaseColor)));
        Assert.DoesNotContain("Alpha comes from", Body(Donor(slot: DonorMapSlot.Normal)));
    }

    /// <summary>The destructive count is the whole point of the warning: a landing submesh whose map was
    /// authored from ANOTHER card is overwritten just the same, and the dropped card's own state says
    /// nothing about it.</summary>
    [Fact]
    public void DonorRoute_LandingOnAuthoredSubmeshes_NamesHowMany()
    {
        Assert.Contains("Replaces the maps 2 submeshes already carry.", Body(Donor(3, authoredLanding: 2)));
        Assert.Contains("Replaces the map 1 submesh already carries.", Body(Donor(3, authoredLanding: 1)));
        Assert.DoesNotContain("already carr", Body(Donor(3, authoredLanding: 0)));
    }

    // ---- the SECOND drop, over a map the replacement already carries: the same donor route ----

    /// <summary>The card shows one of the replacement's own maps, so there is no game texture the drop
    /// leaves alone and naming one would only confuse. The route is named by what it does replace.</summary>
    [Fact]
    public void DonorRoute_OnAnAlreadyAuthoredCard_NamesTheMapItReplaces_NotAGameTexture()
    {
        var ask = Donor(authoredLanding: 1) with { IsAuthored = true };
        var body = Body(ask);

        Assert.StartsWith("Apply paint.png as body1's Base color?", body);
        Assert.Contains("This replaces body1's own Base color map.", body);
        Assert.DoesNotContain("is untouched", body);
        Assert.DoesNotContain("c_stem_body1_d", body);
        Assert.Contains("Replaces the map 1 submesh already carries.", body);
        Assert.True(Danger(ask));
    }

    /// <summary>An authored card's re-drop reaches every submesh the map dresses, same as a first drop, and
    /// the RMO's mask still comes off the game map. Where the overwrite covers that whole reach, ONE sentence
    /// carries the number: two counts of the same submeshes read as two different reaches.</summary>
    [Fact]
    public void DonorRoute_OnAnAlreadyAuthoredCard_CountsTheSameSubmeshesOnce()
    {
        var body = Body(Donor(submeshes: 2, authoredLanding: 2, slot: DonorMapSlot.Rmo) with { IsAuthored = true });

        Assert.DoesNotContain("Applies to", body);
        Assert.Contains("Replaces the maps 2 submeshes already carry.", body);
        Assert.Contains(MainWindowViewModel.DroppedRmoAlphaNote, body);
        Assert.EndsWith("\n\n" + MainWindowViewModel.DroppedMapNoRevert, body);
    }

    /// <summary>Where the two counts differ they are two facts: the drop reaches three submeshes and two of
    /// them already carry a map.</summary>
    [Fact]
    public void DonorRoute_ReachingMoreSubmeshesThanItOverwrites_StatesBoth()
    {
        var body = Body(Donor(submeshes: 3, authoredLanding: 2) with { IsAuthored = true });

        Assert.Contains("Applies to 3 submeshes.", body);
        Assert.Contains("Replaces the maps 2 submeshes already carry.", body);
    }

    // ---- the in-place overwrite, for a card whose replacement the build would no longer ship ----

    /// <summary>The card's game texture is not what this drop touches, and the file may have come from
    /// either route — a Blender send or an earlier drop — so the body names the part and the slot and
    /// attributes the file to the replacement rather than to the mesh edit.</summary>
    [Fact]
    public void AuthoredRoute_NamesThePartAndSlot_NotTheGameTexture()
    {
        var body = Body(Authored());

        Assert.StartsWith("Apply paint.png as body1's Base color?", body);
        Assert.DoesNotContain("c_stem_body1_d", body);
        Assert.DoesNotContain("mesh edit supplied", body);
    }

    /// <summary>Its reach is ONE submesh: the file behind this card is the only one rewritten, while the
    /// first drop covered every submesh the stock map dressed.</summary>
    [Fact]
    public void AuthoredRoute_StatesItsRealScope()
    {
        Assert.Contains("Replaces the map the replacement carries on this submesh. No other submesh changes.",
            Body(Authored()));
    }

    [Fact]
    public void AuthoredRoute_CarriesTheSameNoRevertLineAndDangerStyling()
    {
        Assert.EndsWith("\n\n" + MainWindowViewModel.DroppedMapNoRevert, Body(Authored()));
        Assert.True(Danger(Authored()));
    }

    /// <summary>A card built outside a part's tree names no part, and the body must not read "'s" onto an
    /// empty token.</summary>
    [Fact]
    public void AuthoredRoute_WithNoPartContext_StillReadsAsASentence()
    {
        var ask = Authored() with { PartToken = "" };

        Assert.StartsWith("Apply paint.png to Base color?", Body(ask));
    }
}
