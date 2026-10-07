using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Export;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Donor ranges routed through a tier's material map. A range belongs to the lod0 material position it
/// folds onto; the map says which of the tier's own positions draws that region, so the range fires at
/// that tier's draw and at no other — and at none at all where the tier draws the region nowhere.
/// </summary>
public class TierMaterialRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-tier-routing-" + Guid.NewGuid().ToString("N"));

    public TierMaterialRoutingTests() => Directory.CreateDirectory(_root);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // A measured three-tier clothing part: the three tiers' vanilla draw shapes and the materials each
    // tier's renderer binds, taken from the roster sweep. Bundle names are stand-ins; only the identity
    // pairs matter.
    private static readonly DrawShapeSet Lod0Shapes = new(new[]
        { new DrawShape(0, 4254), new DrawShape(4254, 3852), new DrawShape(8106, 3132) }, 11238);
    private static readonly DrawShapeSet Lod1Shapes = new(new[]
        { new DrawShape(0, 564), new DrawShape(564, 414), new DrawShape(978, 756) }, 1734);
    private static readonly DrawShapeSet Lodm0Shapes = new(new[]
        { new DrawShape(0, 1254), new DrawShape(1254, 1542), new DrawShape(2796, 1752) }, 4548);

    private static readonly TierMaterialRef FullUvSkinuber = new("bundle-e77721a1", 6467201681950135105L, true);
    private static readonly TierMaterialRef ClothUbertrans = new("bundle-cloth", -4807448797916829051L, true);
    private static readonly TierMaterialRef ClothUber = new("bundle-cloth", 3713197801503913738L, true);
    private static readonly TierMaterialRef LodBodySkinuber = new("bundle-6f13d51f", -3759099610231797671L, true);

    /// <summary>The measurement named the tier's third material as the carrier of the skin region at both
    /// reduced tiers; injected here so the emission is pinned without a mesh read.</summary>
    private static GeometryVerdict SkinRegionCarriedAtPosition2(int position)
    {
        Assert.Equal(0, position);
        return new GeometryVerdict(2, TierMapRule.Geometry);
    }

    private static string Section(string ini, string header)
    {
        int start = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"section missing: {header}");
        int end = ini.IndexOf("\n[", start + header.Length, StringComparison.Ordinal);
        return end < 0 ? ini[start..] : ini[start..end];
    }

    private string BuildIni(DrawShapeSet anchorShapes, int donorSubmeshes,
        params (string Suffix, DrawShapeSet Shapes, TierMaterialMap? Map)[] tiers)
    {
        string dumps = Path.Combine(_root, "dumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        SyntheticPool.WritePartDump(Path.Combine(dumps, "beta"), seed: 60, verts: 64,
            boneHashes: new uint[] { 201, 202 });
        foreach (var tier in tiers)
            SyntheticPool.WritePartDump(Path.Combine(dumps, $"beta_{tier.Suffix}"), seed: 70, verts: 32,
                boneHashes: new uint[] { 201, 202 });
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 4, submeshes: donorSubmeshes);

        string outDir = Path.Combine(_root, "out");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[]
                    {
                        new PoolPart("alpha", Path.Combine(dumps, "alpha")),
                        new PoolPart("beta", Path.Combine(dumps, "beta")),
                    },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa1111", ["beta"] = "bbbb2222" },
                    Tiers = tiers.Select((tier, i) => new PoolTier("beta", $"beta_{tier.Suffix}",
                        tier.Suffix, Path.Combine(dumps, $"beta_{tier.Suffix}"), $"bbbb222{i + 3}",
                        Shapes: tier.Shapes, Map: tier.Map)).ToArray(),
                    AnchorShapes = anchorShapes,
                },
            },
        });
        return File.ReadAllText(Path.Combine(outDir, "mod.ini"));
    }

    /// <summary>The part binds three materials at every detail level and the game orders them differently
    /// at each, so drawing donor range k at the tier's submesh k rendered two of the three ranges under
    /// the wrong material. Each range now draws where the game draws that region — the run lines the
    /// hand-made fix for this part produced.</summary>
    [Fact]
    public void Each_donor_range_draws_at_the_tier_position_carrying_its_region()
    {
        var lod1Map = TierMaterialMap.Build(
            new[] { FullUvSkinuber, ClothUbertrans, ClothUber }, Lod0Shapes,
            new[] { ClothUber, ClothUbertrans, LodBodySkinuber }, Lod1Shapes,
            SkinRegionCarriedAtPosition2);
        var lodm0Map = TierMaterialMap.Build(
            new[] { FullUvSkinuber, ClothUbertrans, ClothUber }, Lod0Shapes,
            new[] { ClothUbertrans, ClothUber, LodBodySkinuber }, Lodm0Shapes,
            SkinRegionCarriedAtPosition2);

        // the maps the sweep measured, stated once so a wrong map cannot pass as a wrong emission
        Assert.Equal("0→2 geometry, 1→1 identity, 2→0 identity", lod1Map.Diagnostic);
        Assert.Equal("0→2 geometry, 1→0 identity, 2→1 identity", lodm0Map.Diagnostic);

        string ini = BuildIni(Lod0Shapes, donorSubmeshes: 3,
            ("lod1", Lod1Shapes, lod1Map), ("lodm0", Lodm0Shapes, lodm0Map));

        // lod0 is unchanged: range k draws at its own submesh k
        for (int k = 0; k < 3; k++)
            Assert.Contains($"run = CommandListDrawS{k}_swap\n",
                Section(ini, $"[TextureOverride_Cap_beta_DrawS{k}]"));

        // lod1 binds [cloth_uber, cloth_ubertrans, lod_body_skinuber]
        AssertRuns(ini, "beta_lod1", 0, 2);
        AssertRuns(ini, "beta_lod1", 1, 1);
        AssertRuns(ini, "beta_lod1", 2, 0);

        // lodm0 binds [cloth_ubertrans, cloth_uber, lod_body_skinuber]
        AssertRuns(ini, "beta_lodm0", 0, 1);
        AssertRuns(ini, "beta_lodm0", 1, 2);
        AssertRuns(ini, "beta_lodm0", 2, 0);

        // every range still has somewhere to draw, so a whole-mesh pass runs the whole donor
        Assert.Contains("run = CommandListDraw_swap\n",
            Section(ini, "[TextureOverride_Cap_beta_lod1_DrawFull]"));
    }

    private static void AssertRuns(string ini, string tier, int tierPosition, int donorRange)
    {
        string section = Section(ini, $"[TextureOverride_Cap_{tier}_DrawS{tierPosition}]");
        Assert.Contains($"run = CommandListDrawS{donorRange}_swap\n", section);
        for (int other = 0; other < 3; other++)
            if (other != donorRange)
                Assert.DoesNotContain($"run = CommandListDrawS{other}_swap\n", section);
    }

    /// <summary>A region the tier draws nowhere — gone at this detail level, or already drawn by another
    /// position — is not drawn there by the replacement either. That includes the whole-mesh pass, which
    /// runs the carried ranges one at a time rather than the whole donor.</summary>
    [Fact]
    public void A_range_the_tier_carries_nowhere_draws_in_no_section_of_that_tier()
    {
        var anchor = new DrawShapeSet(new[] { new DrawShape(0, 60), new DrawShape(60, 84) }, 144);
        var tierShapes = new DrawShapeSet(new[] { new DrawShape(0, 30), new DrawShape(30, 42) }, 72);
        var map = TierMaterialMap.Build(
            new[] { ClothUber, ClothUbertrans }, anchor,
            new[] { ClothUber, LodBodySkinuber }, tierShapes,
            _ => new GeometryVerdict(null, TierMapRule.Absent));
        Assert.Equal("0→0 identity, 1→none absent", map.Diagnostic);

        string ini = BuildIni(anchor, donorSubmeshes: 2, ("lod1", tierShapes, map));

        // the carried range draws at its tier position
        Assert.Contains("run = CommandListDrawS0_swap\n",
            Section(ini, "[TextureOverride_Cap_beta_lod1_DrawS0]"));
        // the dropped range's tier position issues no section at all
        Assert.DoesNotContain("[TextureOverride_Cap_beta_lod1_DrawS1]", ini);
        // and the whole-mesh pass runs only the carried range, never the whole donor
        string full = Section(ini, "[TextureOverride_Cap_beta_lod1_DrawFull]");
        Assert.Contains("run = CommandListDrawS0_swap\n", full);
        Assert.DoesNotContain("run = CommandListDrawS1_swap\n", full);
        Assert.DoesNotContain("run = CommandListDraw_swap\n", full);
        // lod0 keeps both ranges, including in its own whole-mesh pass
        Assert.Contains("run = CommandListDrawS1_swap\n",
            Section(ini, "[TextureOverride_Cap_beta_DrawS1]"));
        Assert.Contains("run = CommandListDraw_swap\n",
            Section(ini, "[TextureOverride_Cap_beta_DrawFull]"));
    }

    /// <summary>A tier with ONE drawable submesh never routed: every range drew at its single draw.
    /// Holding a range back needs the per-range lists, so a map that drops one makes such a tier route
    /// after all.</summary>
    [Fact]
    public void A_single_submesh_tier_routes_when_its_map_drops_a_range()
    {
        var anchor = new DrawShapeSet(new[] { new DrawShape(0, 60), new DrawShape(60, 84) }, 144);
        var tierShapes = new DrawShapeSet(new[] { new DrawShape(0, 72) }, 72);
        var map = TierMaterialMap.Build(
            new[] { ClothUber, ClothUbertrans }, anchor,
            new[] { ClothUber }, tierShapes,
            _ => new GeometryVerdict(null, TierMapRule.Coincident));
        Assert.Equal("0→0 identity, 1→none coincident", map.Diagnostic);

        string ini = BuildIni(anchor, donorSubmeshes: 2, ("lod1", tierShapes, map));

        // the tier's capture section no longer carries the whole-donor draw
        Assert.DoesNotContain("run = CommandListDraw_swap\n",
            Section(ini, "[TextureOverride_Cap_beta_lod1]"));
        string routed = Section(ini, "[TextureOverride_Cap_beta_lod1_DrawS0]");
        Assert.Contains("run = CommandListDrawS0_swap\n", routed);
        Assert.DoesNotContain("run = CommandListDrawS1_swap\n", routed);
    }

    /// <summary>A tier whose map places every range at the position it already drew at emits exactly what
    /// the build emitted before the map existed.</summary>
    [Fact]
    public void An_identity_map_emits_what_the_positional_routing_emitted()
    {
        var anchor = new DrawShapeSet(new[] { new DrawShape(0, 60), new DrawShape(60, 84) }, 144);
        var tierShapes = new DrawShapeSet(new[] { new DrawShape(0, 30), new DrawShape(30, 42) }, 72);
        var map = TierMaterialMap.Build(
            new[] { ClothUber, ClothUbertrans }, anchor,
            new[] { ClothUber, ClothUbertrans }, tierShapes,
            position => throw new InvalidOperationException($"identity covers position {position}"));
        Assert.All(map.Entries, e => Assert.Equal(e.Position, e.Carrier));

        string mapped = BuildIni(anchor, donorSubmeshes: 2, ("lod1", tierShapes, map));
        Directory.Delete(_root, recursive: true);
        Directory.CreateDirectory(_root);
        string unmapped = BuildIni(anchor, donorSubmeshes: 2, ("lod1", tierShapes, null));

        Assert.Equal(unmapped, mapped);
    }
}
