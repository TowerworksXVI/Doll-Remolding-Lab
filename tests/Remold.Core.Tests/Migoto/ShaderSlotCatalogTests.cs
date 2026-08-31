using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Remold.Core;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The shipped shader slot catalog: the shape it commits to, the registers it derives, and the refusals
/// that keep a build from inventing a slot range when it can't read one.
/// </summary>
public class ShaderSlotCatalogTests
{
    // LF-normalized: the mutation theories match multiline JSON with "\n" literals, and the file's
    // on-disk endings depend on the checkout (a worktree writes LF, autocrlf materializes CRLF).
    private static string ShippedJson() =>
        File.ReadAllText(LabPaths.ShaderSlotCatalogFile).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static ShaderSlotCatalog Shipped()
    {
        var c = ShaderSlotCatalog.Parse(ShippedJson(), out var problem);
        Assert.Null(problem);
        return Assert.IsType<ShaderSlotCatalog>(c);
    }

    [Fact]
    public void The_shipped_catalog_names_its_own_measurement()
    {
        var c = Shipped();
        Assert.Equal("charps-26932-r3", c.CatalogId);
        Assert.Equal("26932", c.GameBuild);
        Assert.Equal("fa6e3457c2ec977f", c.SourceSha16);
    }

    /// <summary>The stock probe range is the union of every picture input. Blend reaches t10, which a
    /// base/normal/RMO-only range would silently miss.</summary>
    [Fact]
    public void The_stock_probe_range_covers_every_measured_stock_layout()
    {
        var c = Shipped();
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, c.StockMapSlots);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, c.Slots(ShaderSlotCatalog.BaseMap));
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7 }, c.Slots(ShaderSlotCatalog.BumpMap));
        Assert.Equal(new[] { 3, 4, 5, 6, 7, 8 }, c.Slots(ShaderSlotCatalog.RMOTex));
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10 },
            c.Slots(ShaderSlotCatalog.BlendTex));
    }

    [Fact]
    public void The_ramp_range_is_its_own_and_reaches_past_the_stock_one()
    {
        var c = Shipped();
        Assert.Equal(new[] { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, c.RampSlots);
        Assert.Equal(c.RampSlots, c.Slots(ShaderSlotCatalog.RampMap));
    }

    [Fact]
    public void The_26932_measurement_pins_every_property_specific_range()
    {
        var c = Shipped();
        static int[] Through(int first, int last) => Enumerable.Range(first, last - first + 1).ToArray();

        Assert.Equal(Through(4, 11), c.SlotsForProperty("_GlitterMap"));
        Assert.Equal(Through(4, 9), c.SlotsForProperty("_DetailAlbedo"));
        Assert.Equal(Through(5, 10), c.SlotsForProperty("_DetailNormalRM"));
        Assert.Equal(Through(6, 11), c.SlotsForProperty("_DetailMask"));
        Assert.Equal(Through(2, 5), c.SlotsForProperty("_MatcapTex"));
        Assert.Equal(Through(3, 6), c.SlotsForProperty("_MatcapNormalTex"));
        Assert.Equal(new[] { 1 }, c.SlotsForProperty("_Specularmap"));
        Assert.Equal(Through(0, 9), c.SlotsForProperty("_DissolveTex"));
        Assert.Equal(Through(4, 12), c.SlotsForProperty("_PaintMaskTexture"));

        Assert.Equal(Through(0, 12), c.PropertySlots.Values.SelectMany(slots => slots)
            .Distinct().OrderBy(slot => slot));
        Assert.Empty(c.SlotsForProperty("_SMO"));
        Assert.Empty(c.SlotsForProperty("_TurbulenceTex"));
    }

    /// <summary>The plan is the catalog's stated ranges, whichever install reads it. A register the
    /// measurement no longer describes answers no tag at draw time and binds nothing, so a stale catalog
    /// can only under-cover — while narrowing on the install's build would drop the ramp on every content
    /// patch, since the number a build compares against moves with the game's content.</summary>
    [Fact]
    public void The_plan_states_the_catalogs_ranges_whatever_build_the_install_is_on()
    {
        var c = Shipped();
        var plan = ShaderSlotPlan.For(c);

        Assert.Equal(c.StockMapSlots, plan.StockMaps);
        Assert.Equal(c.RampSlots, plan.Ramp);
        Assert.NotEmpty(plan.Ramp);
    }

    [Theory]
    [InlineData("\"schema\": 1", "\"schema\": 2", "schema 2")]
    [InlineData("\"filter_index_tag_probe\"", "\"guess_the_slot\"", "guess_the_slot")]
    [InlineData("\"catalog_id\": \"charps-26932-r3\"", "\"catalog_id\": \"\"", "names no id")]
    [InlineData("\"RampMap\": {\n      \"3\": 14", "\"RampMap\": {\n      \"-1\": 14", "register '-1'")]
    [InlineData("\"RampMap\": {\n      \"3\": 14", "\"RampMap\": {\n      \"3\": 0", "register '3'")]
    public void A_catalog_that_does_not_state_a_sound_range_is_refused(string from, string to, string says)
    {
        var json = ShippedJson().Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(json, ShippedJson());

        var c = ShaderSlotCatalog.Parse(json, out var problem);

        Assert.Null(c);
        Assert.Contains(says, problem);
    }

    [Fact]
    public void A_catalog_missing_an_input_entirely_is_refused()
    {
        var json = ShippedJson().Replace("\"RampMap\"", "\"SomethingElse\"", StringComparison.Ordinal);
        var c = ShaderSlotCatalog.Parse(json, out var problem);
        Assert.Null(c);
        Assert.Contains("RampMap", problem);
    }

    [Fact]
    public void An_older_catalog_without_BlendTex_keeps_its_existing_stock_range()
    {
        var stored = JsonNode.Parse(ShippedJson())!.AsObject();
        Assert.True(stored["inputs"]!.AsObject().Remove(ShaderSlotCatalog.BlendTex));
        string json = stored.ToJsonString();

        var catalog = ShaderSlotCatalog.Parse(json, out var problem);

        Assert.Null(problem);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, catalog!.StockMapSlots);
        Assert.Empty(catalog.Slots(ShaderSlotCatalog.BlendTex));
    }

    [Fact]
    public void Unreadable_json_and_an_absent_file_both_refuse_rather_than_throw()
    {
        Assert.Null(ShaderSlotCatalog.Parse("{ not json", out var bad));
        Assert.Contains("valid JSON", bad);

        var missing = Path.Combine(Path.GetTempPath(), $"gf2-no-catalog-{Guid.NewGuid():N}.json");
        Assert.Null(ShaderSlotCatalog.TryLoad(missing, out var absent));
        Assert.Contains("isn't in this install", absent);
    }
}
