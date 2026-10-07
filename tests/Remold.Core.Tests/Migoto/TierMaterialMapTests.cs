using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Which of a tier's material positions carries each lod0 material position's region. The game orders
/// every tier's renderer materials on its own, so lod0 position k and tier position k say nothing about
/// each other; these pin both readings that can establish the correspondence — the prefab's material
/// identities, and the two meshes' geometry where a caller supplies it. A build supplies none: it routes
/// on material identity alone.
/// </summary>
public class TierMaterialMapTests
{
    private static readonly TierMaterialRef A = new("bundle-a", 100, true);
    private static readonly TierMaterialRef B = new("bundle-b", 200, true);
    private static readonly TierMaterialRef C = new("bundle-c", 300, true);
    private static readonly TierMaterialRef Placeholder = new(null, 0, true);
    private static readonly TierMaterialRef Unreadable = new(null, 400, false);

    /// <summary>Shapes for <paramref name="count"/> drawable positions; only the position COUNT and which
    /// positions draw matter to the map, so the ranges are just distinct and non-empty.</summary>
    private static DrawShapeSet Shapes(int count) => new(
        Enumerable.Range(0, count).Select(k => new DrawShape(k * 10, 10)).ToList(), count * 10);

    private static GeometryVerdict NeverAsked(int position) =>
        throw new InvalidOperationException($"layer B was asked about position {position}");

    private static IReadOnlyList<(int Position, int? Carrier, TierMapRule Rule)> Read(TierMaterialMap map) =>
        map.Entries.Select(e => (e.Position, e.Carrier, e.Rule)).ToList();

    /// <summary>Every position placed where it already drew: the routing this map produces is the
    /// positional one the build emitted before any map existed.</summary>
    private static void AssertPositional(TierMaterialMap map) =>
        Assert.All(map.Entries, e => Assert.Equal(e.Position, e.Carrier));

    // ---- layer A: identity ------------------------------------------------------------------------

    /// <summary>The most common shape the roster sweep found: a tier binds every one of the lod0
    /// materials, each at a different position. Identity alone places every donor range, and no mesh is
    /// ever read.</summary>
    [Fact]
    public void A_permuted_tier_pairs_every_position_by_material_identity()
    {
        var map = TierMaterialMap.Build(new[] { A, B, C }, Shapes(3),
            new[] { B, C, A }, Shapes(3), NeverAsked);

        Assert.Equal(new[]
        {
            (0, (int?)2, TierMapRule.Identity),
            (1, (int?)0, TierMapRule.Identity),
            (2, (int?)1, TierMapRule.Identity),
        }, Read(map));
        Assert.Equal("0→2 identity, 1→0 identity, 2→1 identity", map.Diagnostic);
    }

    /// <summary>The tier that binds the same materials in the same order maps every position to itself,
    /// which is the routing the build already emitted.</summary>
    [Fact]
    public void A_tier_binding_the_same_materials_in_order_maps_each_position_to_itself()
    {
        var map = TierMaterialMap.Build(new[] { A, B, C }, Shapes(3),
            new[] { A, B, C }, Shapes(3), NeverAsked);

        AssertPositional(map);
        Assert.All(map.Entries, e => Assert.Equal(TierMapRule.Identity, e.Rule));
        Assert.Empty(map.UnresolvedPositions);
    }

    /// <summary>An empty renderer slot holds a position without naming a material, so it corresponds to
    /// another empty slot and to nothing else. The game draws no material there, so the correspondence
    /// carries nothing: the range is dropped rather than sent to a draw that never happens.</summary>
    [Fact]
    public void A_placeholder_pairs_only_with_a_placeholder_and_carries_nothing()
    {
        var map = TierMaterialMap.Build(new[] { Placeholder, A }, Shapes(2),
            new[] { A, Placeholder }, Shapes(2), NeverAsked);

        Assert.Equal(new[]
        {
            (0, (int?)null, TierMapRule.Identity),
            (1, (int?)0, TierMapRule.Identity),
        }, Read(map));
        Assert.Empty(map.UnresolvedPositions);
    }

    /// <summary>One material bound twice at each level pairs in position order — first with first, second
    /// with second — rather than both landing on one tier position.</summary>
    [Fact]
    public void A_material_bound_twice_pairs_greedily_in_position_order()
    {
        var map = TierMaterialMap.Build(new[] { A, A, B }, Shapes(3),
            new[] { B, A, A }, Shapes(3), NeverAsked);

        Assert.Equal(new[]
        {
            (0, (int?)1, TierMapRule.Identity),
            (1, (int?)2, TierMapRule.Identity),
            (2, (int?)0, TierMapRule.Identity),
        }, Read(map));
    }

    /// <summary>A material whose CAB this install cannot resolve says nothing about what it is, so it
    /// pairs with nothing — not even with another unresolved one — and the position goes to the geometry
    /// reading instead.</summary>
    [Fact]
    public void An_unresolved_material_pairs_with_nothing()
    {
        int asked = -1;
        var map = TierMaterialMap.Build(new[] { Unreadable }, Shapes(1),
            new[] { Unreadable }, Shapes(1), position =>
            {
                asked = position;
                return new GeometryVerdict(0, TierMapRule.Geometry);
            });

        Assert.Equal(0, asked);
        Assert.Equal(new[] { (0, (int?)0, TierMapRule.Geometry) }, Read(map));
    }

    /// <summary>A tier position with no geometry issues no draw, so a range routed there would draw
    /// nothing. The correspondence is still known — this is what the game itself does — so the position is
    /// dropped without a warning and without a geometry read.</summary>
    [Fact]
    public void A_tier_position_that_draws_nothing_carries_nothing()
    {
        var tierShapes = new DrawShapeSet(new[] { new DrawShape(0, 20), new DrawShape(20, 0) }, 20);
        var map = TierMaterialMap.Build(new[] { A, B }, Shapes(2),
            new[] { B, A }, tierShapes, NeverAsked);

        Assert.Equal(new[]
        {
            (0, (int?)null, TierMapRule.Identity),
            (1, (int?)0, TierMapRule.Identity),
        }, Read(map));
        Assert.Empty(map.UnresolvedPositions);
    }

    /// <summary>A renderer can bind MORE materials than its mesh has submeshes: the game redraws the last
    /// submesh once per extra material, and those draws carry the last submesh's own shape, so the section
    /// keyed on that shape already fires for all of them. The extras are extra passes, not positions —
    /// they are not mapped, not unmatched, and not warned about.</summary>
    [Fact]
    public void Materials_past_the_last_submesh_are_extra_passes_not_positions()
    {
        var map = TierMaterialMap.Build(new[] { A, B }, Shapes(1),
            new[] { A }, Shapes(1), NeverAsked);

        Assert.Equal(new[] { (0, (int?)0, TierMapRule.Identity) }, Read(map));
        Assert.Empty(map.UnresolvedPositions);
        AssertPositional(map);
    }

    /// <summary>A mesh with more submeshes than the renderer binds materials for leaves its trailing
    /// positions outside the map, where the map gives NO answer — which is not the same as dropping the
    /// range, and leaves it the routing it has always taken.</summary>
    [Fact]
    public void The_map_gives_no_answer_for_a_position_it_does_not_cover()
    {
        var map = TierMaterialMap.Build(new[] { A }, Shapes(2), new[] { A }, Shapes(2), NeverAsked);

        Assert.Equal(new[] { (0, (int?)0, TierMapRule.Identity) }, Read(map));
        Assert.True(map.TryCarrier(0, out int? carried));
        Assert.Equal(0, carried);
        Assert.False(map.TryCarrier(1, out int? uncovered));
        Assert.Null(uncovered);
    }

    /// <summary>A carrier the game issues no draw for is no carrier, whichever way it fails: a submesh
    /// past the last one the tier's renderer binds a material for, and a submesh whose material slot is
    /// empty. Both would mint a section matching a draw that never happens, so the range would vanish
    /// with nothing said about it; the position is unresolved instead, which is a warning.</summary>
    [Theory]
    [InlineData(1)]   // the tier binds one material, so its second submesh is never drawn
    [InlineData(2)]   // past the tier's submeshes altogether
    public void A_carrier_at_a_position_the_game_never_draws_is_refused(int carrier)
    {
        var map = TierMaterialMap.Build(new[] { A }, Shapes(1),
            new[] { B }, Shapes(3), _ => new GeometryVerdict(carrier, TierMapRule.Geometry));

        Assert.Equal(new[] { (0, (int?)null, TierMapRule.Unresolved) }, Read(map));
        Assert.Single(map.UnresolvedPositions);
    }

    /// <summary>A tier position whose renderer slot is empty binds no material, so the game draws nothing
    /// there however much geometry the submesh holds. The geometry reading may well name it — it is where
    /// the region's vertices went — but the range cannot be sent to a draw the game never issues.</summary>
    [Fact]
    public void A_placeholder_position_is_never_the_carrier_the_geometry_names()
    {
        var map = TierMaterialMap.Build(new[] { A }, Shapes(1),
            new[] { Placeholder, B }, Shapes(2), _ => new GeometryVerdict(0, TierMapRule.Geometry));

        Assert.Equal(new[] { (0, (int?)null, TierMapRule.Unresolved) }, Read(map));
    }

    // ---- layer B: geometry ------------------------------------------------------------------------

    private static Vector3[] Quad(float x, float y, float z) => new[]
    {
        new Vector3(x, y, z), new Vector3(x + 1, y, z),
        new Vector3(x, y + 1, z), new Vector3(x + 1, y + 1, z),
    };

    private static int[] QuadTris(int first) =>
        new[] { first, first + 1, first + 2, first + 2, first + 1, first + 3 };

    private static UnityMesh MeshOf(IReadOnlyList<Vector3> vertices, params int[][] submeshes) =>
        new()
        {
            Name = "synthetic",
            VertexCount = vertices.Count,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = vertices.SelectMany(v => new[] { v.X, v.Y, v.Z }).ToArray(),
            },
            Submeshes = submeshes.ToList(),
        };

    /// <summary>The measured case the roster sweep found most often: the tier binds a material lod0 never
    /// had, and the region the unmatched lod0 submesh draws is held by one tier submesh. That submesh is
    /// the carrier.</summary>
    [Fact]
    public void Geometry_names_the_tier_submesh_holding_an_unmatched_regions_vertices()
    {
        var lod0 = MeshOf(Quad(0, 0, 0).Concat(Quad(0, 0, 2)).ToArray(),
            QuadTris(0), QuadTris(4));
        // the tier holds the same two regions, in the opposite submesh order
        var tier = MeshOf(Quad(0, 0, 2.01f).Concat(Quad(0, 0, 0.01f)).ToArray(),
            QuadTris(0), QuadTris(4));
        var probe = new TierGeometryProbe(lod0, tier);

        var map = TierMaterialMap.Build(new[] { A, B }, Shapes(2),
            new[] { C, A }, Shapes(2), probe.Verdict);

        Assert.Equal(new[]
        {
            (0, (int?)1, TierMapRule.Identity),
            (1, (int?)0, TierMapRule.Geometry),
        }, Read(map));
    }

    /// <summary>The region is simply not at the tier: the tier holds the surviving geometry and nothing
    /// near the unmatched submesh. The game draws nothing there, so neither does the replacement — no
    /// carrier, and no warning.</summary>
    [Fact]
    public void A_region_with_no_tier_geometry_near_it_is_absent()
    {
        var lod0 = MeshOf(Quad(0, 0, 0).Concat(Quad(10, 0, 0)).ToArray(),
            QuadTris(0), QuadTris(4));
        var tier = MeshOf(Quad(0, 0, 0.02f), QuadTris(0));
        var probe = new TierGeometryProbe(lod0, tier);

        var map = TierMaterialMap.Build(new[] { A, B }, Shapes(2),
            new[] { A }, Shapes(1), probe.Verdict);

        Assert.Equal(new[]
        {
            (0, (int?)0, TierMapRule.Identity),
            (1, (int?)null, TierMapRule.Absent),
        }, Read(map));
        Assert.Empty(map.UnresolvedPositions);
    }

    /// <summary>The same absence, at a tier that reproduces the rest of the part EXACTLY. Measuring how
    /// far the region sits from the tier against how far the surviving geometry sits from it has no scale
    /// here — the surviving geometry sits on the tier, at distance zero — but nothing at the tier claims
    /// the region and the region lies on no surviving geometry either. The region is gone at this tier,
    /// so the range is not drawn there and the modder is told nothing, exactly as for any other
    /// absence.</summary>
    [Fact]
    public void A_region_absent_from_a_tier_that_reproduces_the_rest_exactly_is_absent()
    {
        var lod0 = MeshOf(Quad(0, 0, 0).Concat(Quad(10, 0, 0)).ToArray(),
            QuadTris(0), QuadTris(4));
        // the tier IS the surviving quad, vertex for vertex, and holds nothing near the other one
        var tier = MeshOf(Quad(0, 0, 0), QuadTris(0));
        var probe = new TierGeometryProbe(lod0, tier);

        var map = TierMaterialMap.Build(new[] { A, B }, Shapes(2),
            new[] { A }, Shapes(1), probe.Verdict);

        Assert.Equal(new[]
        {
            (0, (int?)0, TierMapRule.Identity),
            (1, (int?)null, TierMapRule.Absent),
        }, Read(map));
        Assert.Empty(map.UnresolvedPositions);
    }

    /// <summary>The unmatched lod0 submesh sits on geometry another position already draws — a second pass
    /// over the same surface. The tier draws that surface once, so routing the range there as well would
    /// draw it twice. No carrier, and no warning.</summary>
    [Fact]
    public void A_region_lying_on_surviving_geometry_is_coincident()
    {
        var lod0 = MeshOf(Quad(0, 0, 0).Concat(Quad(0, 0, 0)).ToArray(),
            QuadTris(0), QuadTris(4));
        var tier = MeshOf(Quad(0, 0, 0), QuadTris(0));
        var probe = new TierGeometryProbe(lod0, tier);

        var map = TierMaterialMap.Build(new[] { A, B }, Shapes(2),
            new[] { A }, Shapes(1), probe.Verdict);

        Assert.Equal(new[]
        {
            (0, (int?)0, TierMapRule.Identity),
            (1, (int?)null, TierMapRule.Coincident),
        }, Read(map));
        Assert.Empty(map.UnresolvedPositions);
    }

    /// <summary>A one-material part whose tier binds something else has no surviving geometry to weigh the
    /// region against: the tier draws the whole part under whatever it binds, and the one donor range has
    /// exactly one place to go.</summary>
    [Fact]
    public void A_whole_part_with_nothing_surviving_lands_on_the_tiers_own_submesh()
    {
        var lod0 = MeshOf(Quad(0, 0, 0), QuadTris(0));
        var tier = MeshOf(Quad(0, 0, 0.01f), QuadTris(0));
        var probe = new TierGeometryProbe(lod0, tier);

        var map = TierMaterialMap.Build(new[] { A }, Shapes(1),
            new[] { B }, Shapes(1), probe.Verdict);

        Assert.Equal(new[] { (0, (int?)0, TierMapRule.Geometry) }, Read(map));
    }

    /// <summary>Nothing in the geometry accounts for the region: no tier submesh claims it, it does not
    /// lie on surviving geometry, and the tier is too far from the rest of the part for its distance to
    /// read as the region being gone. That is the one verdict the build warns about.</summary>
    [Fact]
    public void A_region_the_geometry_cannot_account_for_is_unresolved()
    {
        var lod0 = MeshOf(Quad(0, 0, 0).Concat(Quad(10, 0, 0)).ToArray(),
            QuadTris(0), QuadTris(4));
        // the tier sits away from BOTH lod0 regions, so the surviving geometry is no closer to it than
        // the unmatched region is
        var tier = MeshOf(Quad(30, 0, 0), QuadTris(0));
        var probe = new TierGeometryProbe(lod0, tier);

        var map = TierMaterialMap.Build(new[] { A, B }, Shapes(2),
            new[] { A }, Shapes(1), probe.Verdict);

        Assert.Equal((1, (int?)null, TierMapRule.Unresolved), Read(map)[1]);
        Assert.Single(map.UnresolvedPositions);
    }

    /// <summary>Distances are measured however far apart the two meshes sit, rather than giving up once a
    /// query passes the size of the mesh being asked about. Here the tier stands well clear of the whole
    /// part, ten times farther from the region in question than from the geometry it keeps — a reading of
    /// "too far to say" for both would leave the position unresolved and warn the modder about a region
    /// the tier plainly does not draw.</summary>
    [Fact]
    public void Distances_are_measured_between_meshes_that_sit_far_apart()
    {
        // the dropped region at the origin, the surviving one just short of the tier
        var lod0 = MeshOf(Quad(0, 0, 0).Concat(Quad(900, 0, 0)).ToArray(),
            QuadTris(0), QuadTris(4));
        var tier = MeshOf(Quad(1000, 0, 0), QuadTris(0));
        var probe = new TierGeometryProbe(lod0, tier);

        Assert.Equal(new GeometryVerdict(null, TierMapRule.Absent), probe.Verdict(0));
    }
}
