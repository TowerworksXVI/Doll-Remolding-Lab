using System;
using System.Collections.Generic;
using Remold.Core.Export;
using Remold.Core.Model;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The export-phase content policy (<c>Export/ExportBlacklist</c>) — the child-NPC content policy at the
/// two entry points that take a subject directly and so can be reached without the roster surface.
/// Load-bearing: a failure here means an enforcement point was removed, and that removal is the bug.
/// </summary>
public class ExportBlacklistTests
{
    [Theory]
    [InlineData("c_Helena_body_lod0", true)]
    [InlineData("C_HELENA_BODY", true)]      // case-insensitive
    [InlineData("Helena", true)]
    [InlineData("c_Helen_body", false)]      // a different character, not a prefix of this one
    [InlineData("c_Wren_body", false)]
    [InlineData(null, false)]
    public void IsBlocked_MatchesTheBareNameOnly(string? name, bool expected) =>
        Assert.Equal(expected, ExportBlacklist.IsBlocked(name));

    // Both seam tests hand the entry point nulls for everything past the guard: the pin is that a
    // blocked subject returns EMPTY before the export touches the game, the scope, or the disk — any
    // access ahead of the guard fails the test loudly instead of silently exporting.

    [Theory]
    [InlineData("Helena", "HelenaNPC01")]
    [InlineData("Wren", "HelenaNPC01")]      // the stem alone blocks too
    public void ARiggedBuild_OnABlockedSubject_RigsNothingSilently(string character, string stem)
    {
        var done = AssetExporter.BuildRiggedGlbs(@"X:\nowhere", null!,
            new Outfit(0, stem, OutfitKind.Base), character,
            new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
            {
                ("body", "some.bundle", "c_body_lod0", null, null, 0L, null),
            },
            @"X:\nowhere\textures");
        Assert.Empty(done);
    }

    [Fact]
    public void ARecipeExport_OnABlockedSubject_ExportsNothingSilently()
    {
        var report = AssetExporter.ExportRecipePart(@"X:\nowhere", null!, null!,
            new Outfit(0, "HelenaNPC01", OutfitKind.Base), "Helena", null!, @"X:\nowhere\out");
        Assert.Empty(report.Files);
        Assert.Empty(report.CompletedParts);
    }
}
