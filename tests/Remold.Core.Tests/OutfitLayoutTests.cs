using System.Linq;
using Remold.Core.Model;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Grouping raw part tokens into the Pick-step layout (<c>Model/OutfitLayout</c>). A modular part
/// renders as "Body 1 · Variant 3"; shared parts are humanized plainly. Modular tokens carry a
/// <c>P&lt;n&gt;_</c> prefix.
/// </summary>
public class OutfitLayoutTests
{
    private static PartGroup Group(OutfitLayout l, string label) => l.Groups.Single(g => g.Label == label);

    [Fact]
    public void Build_SplitsSharedFromModular_AndFlagsModular()
    {
        var layout = OutfitLayout.Build(new[] { "P1_body1", "P2_head3", "cloth2", "cloth1" });

        Assert.True(layout.IsModular);

        var shared = Group(layout, "Shared");
        Assert.Equal(0, shared.Order);
        Assert.Equal(new[] { "cloth1", "cloth2" }, shared.Parts.Select(p => p.Token));   // sorted by display
        Assert.All(shared.Parts, p => Assert.Null(p.Variant));

        var modular = Group(layout, "Modular");
        Assert.Equal(1, modular.Order);
        Assert.Equal(new int?[] { 1, 2 }, modular.Parts.Select(p => p.Variant).ToArray());
    }

    [Fact]
    public void Build_HumanizesDisplayLabels()
    {
        var layout = OutfitLayout.Build(new[] { "cloth4_trans", "P1_body1" });

        Assert.Equal("Cloth 4 Trans", Group(layout, "Shared").Parts.Single().Display);

        var modular = Group(layout, "Modular").Parts.Single();
        Assert.StartsWith("Body 1", modular.Display);
        Assert.Contains("Variant 1", modular.Display);
    }

    [Fact]
    public void Build_NonModularOutfit_HasOnlySharedGroup()
    {
        var layout = OutfitLayout.Build(new[] { "body", "cloth1" });

        Assert.False(layout.IsModular);
        Assert.Single(layout.Groups);
        Assert.Equal("Shared", layout.Groups[0].Label);
    }

    [Fact]
    public void Build_EmptyInput_HasNoGroups()
    {
        var layout = OutfitLayout.Build(System.Array.Empty<string>());
        Assert.False(layout.IsModular);
        Assert.Empty(layout.Groups);
    }

    [Fact]
    public void PartTokenOrder_SortsNumberedPartsNaturally_Not_Lexically()
    {
        // cloth10 must fall AFTER cloth2/cloth9 (numeric), not between cloth1 and cloth2 (lexical).
        var tokens = new[] { "cloth10", "cloth2", "cloth1", "cloth9" };
        var sorted = tokens.OrderBy(t => t, OutfitLayout.PartTokenOrder).ToArray();
        Assert.Equal(new[] { "cloth1", "cloth2", "cloth9", "cloth10" }, sorted);
    }

    [Fact]
    public void PartTokenOrder_GroupsBySubpart_AcrossModularVariants()
    {
        // The P<n>_ prefix is a variant of one slot, so order leads with the SLOT identity: all bodies,
        // then all cloths, then heads — not P1-everything then P2-everything.
        var tokens = new[] { "P2_head", "P1_cloth", "P2_body", "P1_body", "P1_head", "P2_cloth" };
        var sorted = tokens.OrderBy(t => t, OutfitLayout.PartTokenOrder).ToArray();
        Assert.Equal(new[] { "P1_body", "P2_body", "P1_cloth", "P2_cloth", "P1_head", "P2_head" }, sorted);
    }

    [Fact]
    public void PartTokenOrder_VariantOutranksSubNumber_WithinASlot()
    {
        // Within a slot the modular variant outranks the sub-body number, so P2_body and P2_body1 stay
        // together and P3_body must NOT fall between them.
        var tokens = new[] { "P3_body1_trans", "P2_body", "P3_body", "P1_body", "P2_body1" };
        var sorted = tokens.OrderBy(t => t, OutfitLayout.PartTokenOrder).ToArray();
        Assert.Equal(new[] { "P1_body", "P2_body", "P2_body1", "P3_body", "P3_body1_trans" }, sorted);
    }

    [Fact]
    public void PartTokenOrder_SharedPart_PrecedesItsModularVariants()
    {
        // A non-modular "body" sorts ahead of the same slot's P1/P2 variants (variant 0 first).
        var tokens = new[] { "P2_body", "body", "P1_body" };
        var sorted = tokens.OrderBy(t => t, OutfitLayout.PartTokenOrder).ToArray();
        Assert.Equal(new[] { "body", "P1_body", "P2_body" }, sorted);
    }

    [Fact]
    public void Build_OrdersSharedGroupNaturally()
    {
        // The whole pipeline carries the natural order through, so Pick sorts cloth10 after cloth2.
        var layout = OutfitLayout.Build(new[] { "cloth10", "cloth1", "cloth2", "body" });
        var shared = Group(layout, "Shared");
        Assert.Equal(new[] { "body", "cloth1", "cloth2", "cloth10" }, shared.Parts.Select(p => p.Token));
    }
}
