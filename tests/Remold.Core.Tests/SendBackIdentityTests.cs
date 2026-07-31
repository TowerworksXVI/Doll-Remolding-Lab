using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.Core.Blender;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// What a send-back changed, as against what it merely returned. A combined session's Send hands every
/// writable part back whichever one the modder was working on, so every half of "changed" needs an answer
/// that survives a transport round trip: the geometry and the skin against the part's own workspace glb, and
/// each map against the image the session embedded. A part that changed in ANY of them is taken whole; a
/// part that changed in none is left exactly as it was found.
/// </summary>
public class SendBackIdentityTests
{
    // ---------------------------------------------------------------- geometry and skin

    /// <summary>The send-all shape: a part that rode the session untouched comes back as a full re-export of
    /// itself. Rewriting its workspace glb would flag it edited and hand the build a replacement pipeline for
    /// geometry that is still the game's.</summary>
    [Fact]
    public void APartThatCameBackUnchanged_IsNoEdit_AndItsWorkspaceGlbIsNotTouched()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", yShift: 5f), TwoBoneSkin(), h => Paths[h], ws);
        var before = File.ReadAllBytes(ws);
        var returned = RoundTrip(g, ws, "cloth1_lod0");
        var backedUp = new List<string>();

        Assert.False(SendBackGeometry.Take(returned, "cloth1_lod0", ws, hasMapAsks: false, backedUp.Add));

        Assert.Equal(before, File.ReadAllBytes(ws));   // the byte-compare against originals/ still reads untouched
        Assert.Empty(backedUp);                        // and nothing was copied aside for an overwrite that never came
    }

    /// <summary>One vertex moved is an edit, and an edit takes the whole path: the file is copied aside,
    /// rewritten, and the geometry that lands in it is the one that came back.</summary>
    [Fact]
    public void APartWithOneVertexMoved_IsAnEdit_AndIsWrittenThrough()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", yShift: 5f), TwoBoneSkin(), h => Paths[h], ws);
        var moved = MeshGltf.ReadRiggedGlb(ws)!.Value;
        moved.Mesh.Channels["Vertex"][1] += 0.25f;
        var returned = Send(g, moved);
        var backedUp = new List<string>();

        Assert.True(SendBackGeometry.Take(returned, "cloth1_lod0", ws, hasMapAsks: false, backedUp.Add));

        Assert.Equal(new[] { ws }, backedUp.ToArray());
        Assert.Equal(5.25f, Ys(MeshGltf.ImportGlb(ws))[0], 4);
    }

    /// <summary>A weight repainted on a mesh whose vertices all stayed put. Nothing in the geometry can see
    /// it — the positions, normals, UVs and triangles are the ones that left — so a gate reading geometry
    /// alone drops the modder's weight paint on the floor.</summary>
    [Fact]
    public void APartWithOneWeightRepainted_IsAnEdit_AndTheNewWeightIsWrittenThrough()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", yShift: 5f), TwoBoneSkin(), h => Paths[h], ws);
        var restYs = Ys(MeshGltf.ImportGlb(ws));
        var repainted = MeshGltf.ReadRiggedGlb(ws)!.Value;
        repainted.Mesh.Channels["BlendIndices"][9] = 1f;      // vertex 2's second influence: the arm
        repainted.Mesh.Channels["BlendWeight"][8] = 0.25f;
        repainted.Mesh.Channels["BlendWeight"][9] = 0.75f;
        var returned = Send(g, repainted);

        Assert.True(SendBackGeometry.Take(returned, "cloth1_lod0", ws, hasMapAsks: false));

        var written = MeshGltf.ReadRiggedGlb(ws)!.Value;
        Assert.Equal(0.75f, written.Mesh.Channels["BlendWeight"][9], 3);
        Assert.Equal(HArm, written.Skin.BoneHashes[(int)written.Mesh.Channels["BlendIndices"][9]]);
        // and no vertex moved, so the geometry half on its own had nothing to report
        var afterYs = Ys(MeshGltf.ImportGlb(ws));
        for (int v = 0; v < restYs.Length; v++) Assert.Equal(restYs[v], afterYs[v], 4);
    }

    /// <summary>A faint influence whose weight the transport renormalized by a hair, across the very value
    /// the weight tolerance is. Counting each side's influences and requiring the counts to match puts an
    /// edge on that tolerance for a weight to sit on: one side keeps the influence, the other drops it, and a
    /// part nobody repainted comes back re-weighted.</summary>
    [Fact]
    public void AFaintInfluenceRenormalizedAcrossTheWeightTolerance_IsNoEdit()
    {
        var onTheEdge = Skinned(MeshApply.SkinWeightDrift);
        var aHairOver = Skinned(MathF.BitIncrement(MeshApply.SkinWeightDrift));

        Assert.True(SendBackGeometry.SameContent(onTheEdge, aHairOver));
        Assert.True(SendBackGeometry.SameContent(aHairOver, onTheEdge));
        // …and a bone pulling that little reads the same as one not named at all
        Assert.True(SendBackGeometry.SameContent(onTheEdge, Skinned(0f)));
    }

    /// <summary>The control on the weight rule: an influence worth seeing is still an influence, and losing
    /// it is a repaint.</summary>
    [Fact]
    public void AnInfluenceWorthSeeingThatCameBackGone_IsAnEdit()
    {
        Assert.False(SendBackGeometry.SameContent(Skinned(0.25f), Skinned(0f)));
        Assert.False(SendBackGeometry.SameContent(Skinned(0f), Skinned(0.25f)));
    }

    /// <summary>A weight that is not a number pulls nothing: it reads as absent, so against a real influence
    /// it is a repaint, and two sides equally malformed carry the same (empty) skin.</summary>
    [Fact]
    public void AWeightThatIsNotANumber_ReadsAsAbsent()
    {
        Assert.False(SendBackGeometry.SameContent(Skinned(float.NaN), Skinned(0.25f)));
        Assert.False(SendBackGeometry.SameContent(Skinned(0.25f), Skinned(float.NaN)));
        Assert.True(SendBackGeometry.SameContent(Skinned(float.NaN), Skinned(float.NaN)));
    }

    /// <summary>The shape a real send-back arrives in: a glTF re-export splits every shared vertex into one
    /// copy per triangle corner and re-quantizes every float on the way. Nothing about the mesh changed, so
    /// nothing may be written — even though the vertex buffer that came back is not the one that left.</summary>
    [Fact]
    public void ASeamReSplitWithTransportJitter_IsNoEdit_AndItsWorkspaceGlbIsNotTouched()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Quad("cloth1_lod0"), TwoBoneSkin(), h => Paths[h], ws);
        var before = File.ReadAllBytes(ws);
        var returned = Send(g, (ReSplit(Quad("cloth1_lod0"), TransportJitter), TwoBoneSkin()));

        // the file really does hold a re-split buffer a vertex-by-vertex read cannot match
        var back = MeshGltf.ImportPayload(returned, "cloth1_lod0");
        var held = MeshGltf.ImportPayload(ws, lenient: true);
        Assert.Equal(6, back.VertexCount);
        Assert.Equal(4, held.VertexCount);
        Assert.False(MeshApply.GeometryUnchanged(back.Mesh, held.Mesh));

        Assert.False(SendBackGeometry.Take(returned, "cloth1_lod0", ws, hasMapAsks: false));

        Assert.Equal(before, File.ReadAllBytes(ws));
    }

    /// <summary>A UV nudged inside a part that also came back re-split. The tolerance that absorbs the
    /// transport's re-quantization must not absorb the edit riding with it.</summary>
    [Fact]
    public void AUvMovedInsideAReSplitPart_IsAnEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Quad("cloth1_lod0"), TwoBoneSkin(), h => Paths[h], ws);
        var edited = ReSplit(Quad("cloth1_lod0"), TransportJitter);
        edited.Channels["TexCoord0"][5] += 0.05f;

        Assert.True(SendBackGeometry.Take(returned: Send(g, (edited, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));
    }

    /// <summary>A UV nudged on a part whose UVs are tiled into the tens — an atlas coordinate. A tolerance
    /// scaled by magnitude is tens of times wider out there, which is a shift of several texels on the map:
    /// visibly wrong, and no longer the same value in any sense the modder would agree with.</summary>
    [Fact]
    public void AUvMovedOnATiledPart_IsAnEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(TiledQuad("cloth1_lod0"), TwoBoneSkin(), h => Paths[h], ws);
        var edited = ReSplit(TiledQuad("cloth1_lod0"), TransportJitter);
        edited.Channels["TexCoord0"][5] += 1e-3f;

        Assert.True(SendBackGeometry.Take(returned: Send(g, (edited, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));
    }

    /// <summary>The other side of the tiled UV rule: the flat tolerance still has to clear the transport's
    /// own noise out there, or every untouched atlas part comes back "edited".</summary>
    [Fact]
    public void AUvMovedUnderTheToleranceOnATiledPart_IsNoEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(TiledQuad("cloth1_lod0"), TwoBoneSkin(), h => Paths[h], ws);
        var before = File.ReadAllBytes(ws);
        var nudged = ReSplit(TiledQuad("cloth1_lod0"), TransportJitter);
        nudged.Channels["TexCoord0"][5] += 5e-5f;

        Assert.False(SendBackGeometry.Take(returned: Send(g, (nudged, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));

        Assert.Equal(before, File.ReadAllBytes(ws));
    }

    /// <summary>Two payloads whose submeshes carry no triangle at all. The comparison walks corners, so it
    /// walked nothing — and nothing compared must never answer "the same": that is how an emptiness passes
    /// for a match and an edit is dropped.</summary>
    [Fact]
    public void PayloadsWithNoCornersToWalk_AreNotTheSameContent()
    {
        var empty = MeshApply.Payload.Geometry(new UnityMesh
        {
            Name = "cloth1_lod0",
            VertexCount = 1,
            Channels = new() { ["Vertex"] = new[] { 0f, 0, 0 } },
            Dims = new() { ["Vertex"] = 3 },
            Submeshes = new() { Array.Empty<int>() },
        });

        Assert.False(SendBackGeometry.SameContent(empty, empty));
    }

    /// <summary>A triangle re-pointed at another vertex: the same vertex count, the same face count, a
    /// different surface. The re-split above is the control — this fixture is that one with the topology
    /// changed, and nothing else.</summary>
    [Fact]
    public void ATriangleRePointedAtAnotherVertex_IsAnEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Quad("cloth1_lod0"), TwoBoneSkin(), h => Paths[h], ws);
        var retopo = Quad("cloth1_lod0");
        retopo.Submeshes[0] = new[] { 0, 1, 2, 1, 2, 3 };   // the second triangle now hangs off vertex 1
        var returned = Send(g, (ReSplit(retopo, TransportJitter), TwoBoneSkin()));

        Assert.True(SendBackGeometry.Take(returned, "cloth1_lod0", ws, hasMapAsks: false));
    }

    /// <summary>The smallest deliberate move still reads as one. A tolerance wide enough to swallow this
    /// would drop real work, so the gap between the transport's noise and an edit is pinned from both
    /// sides.</summary>
    [Fact]
    public void AVertexMovedFarAboveTheTransportsNoise_IsAnEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Quad("cloth1_lod0"), TwoBoneSkin(), h => Paths[h], ws);
        var nudged = ReSplit(Quad("cloth1_lod0"), TransportJitter);
        nudged.Channels["Vertex"][1] += 1e-3f;

        Assert.True(SendBackGeometry.Take(returned: Send(g, (nudged, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));
    }

    /// <summary>The part comes back with its triangles written in another order. Corners pair in order, so a
    /// reordering is taken as an edit — the safe direction: the measured transport preserves order, and a
    /// tool that rewrites it may have rewound windings too, which the shipped index buffer makes
    /// semantic.</summary>
    [Fact]
    public void APartWhoseFacesCameBackInAnotherOrder_IsTakenAsAnEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Strip("cloth1_lod0", quads: 8), TwoBoneSkin(), h => Paths[h], ws);
        var shuffled = ReSplit(Strip("cloth1_lod0", quads: 8), TransportJitter);
        shuffled.Submeshes[0] = Shuffle(shuffled.Submeshes[0]);

        Assert.True(SendBackGeometry.Take(returned: Send(g, (shuffled, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));
    }

    /// <summary>One triangle comes back with its winding reversed and everything else bit-equal. The flip is
    /// invisible corner-by-corner as a set — only order carries it — and the compiled swap ships the index
    /// buffer, so it must read as an edit.</summary>
    [Fact]
    public void ATriangleWhoseWindingCameBackReversed_IsTakenAsAnEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Strip("cloth1_lod0", quads: 8), TwoBoneSkin(), h => Paths[h], ws);
        var rewound = ReSplit(Strip("cloth1_lod0", quads: 8), TransportJitter);
        var tri = rewound.Submeshes[0];
        (tri[1], tri[2]) = (tri[2], tri[1]);   // reverse one triangle's cyclic order only

        Assert.True(SendBackGeometry.Take(returned: Send(g, (rewound, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));
    }

    /// <summary>A triangle that changed submeshes. The part carries the same corners in the same numbers as
    /// it left, and the same count in each submesh — only which submesh holds them moved, which is a
    /// different material on that surface and an edit.</summary>
    [Fact]
    public void ATriangleThatCameBackInAnotherSubmesh_IsAnEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Strip("cloth1_lod0", quads: 8, submeshes: 2), TwoBoneSkin(), h => Paths[h], ws);
        var swapped = ReSplit(Strip("cloth1_lod0", quads: 8, submeshes: 2), TransportJitter);
        var first = swapped.Submeshes[0];
        var second = swapped.Submeshes[1];
        for (int i = 0; i < 3; i++) (first[i], second[i]) = (second[i], first[i]);

        Assert.True(SendBackGeometry.Take(returned: Send(g, (swapped, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));
    }

    /// <summary>The part comes back carrying one corner twice where it carried two different ones, with the
    /// corner count unmoved. Every corner in it can be found on the other side; the numbers cannot both be
    /// right, and answering on "each one is in there somewhere" would drop the edit.</summary>
    [Fact]
    public void APartCarryingOneCornerTwiceOverAnother_IsAnEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Strip("cloth1_lod0", quads: 8), TwoBoneSkin(), h => Paths[h], ws);
        var doubled = ReSplit(Strip("cloth1_lod0", quads: 8), TransportJitter);
        doubled.Submeshes[0][4] = doubled.Submeshes[0][0];   // the corner at 4 is now a second copy of 0's

        Assert.True(SendBackGeometry.Take(returned: Send(g, (doubled, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));
    }

    /// <summary>A part whose triangle count came back short. Fewer corners is an edit whatever the ones that
    /// remain say.</summary>
    [Fact]
    public void APartWithATriangleRemoved_IsAnEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Strip("cloth1_lod0", quads: 8), TwoBoneSkin(), h => Paths[h], ws);
        var cut = ReSplit(Strip("cloth1_lod0", quads: 8), TransportJitter);
        cut.Submeshes[0] = cut.Submeshes[0][..^3];

        Assert.True(SendBackGeometry.Take(returned: Send(g, (cut, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));
    }

    /// <summary>A part whose positions sit exactly on round tolerance-sized steps, come back with the
    /// transport's own noise on every one. In-order pairing judges each pair by the drift rules alone, so a
    /// value's place on a round number decides nothing.</summary>
    [Fact]
    public void APartWhosePositionsSitOnRoundToleranceSteps_IsNoEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(OnGridSteps("cloth1_lod0"), TwoBoneSkin(), h => Paths[h], ws);
        var before = File.ReadAllBytes(ws);
        var nudged = ReSplit(OnGridSteps("cloth1_lod0"), -TransportJitter);

        Assert.False(SendBackGeometry.Take(returned: Send(g, (nudged, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));

        Assert.Equal(before, File.ReadAllBytes(ws));
    }

    /// <summary>A normal turned by less than a re-shade would turn it. A round trip rebuilds normals rather
    /// than carrying their floats through, so the turn an untouched part comes back with has to read as no
    /// turn at all — or every part of every send-all is flagged re-shaded.</summary>
    [Fact]
    public void ANormalTurnedUnderTheDriftTolerance_IsNoEdit_AndItsWorkspaceGlbIsNotTouched()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Quad("cloth1_lod0"), TwoBoneSkin(), h => Paths[h], ws);
        var before = File.ReadAllBytes(ws);
        var turned = ReSplit(Quad("cloth1_lod0"), TransportJitter);
        TurnNormals(turned, degrees: 1.5f);

        Assert.False(SendBackGeometry.Take(returned: Send(g, (turned, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));

        Assert.Equal(before, File.ReadAllBytes(ws));
    }

    /// <summary>The other side of the same rule: a hard edge split, a normal flipped, a re-shade of any kind
    /// turns corners by degrees, and a tolerance wide enough to swallow the transport must stay far below
    /// that.</summary>
    [Fact]
    public void ANormalTurnedFarAboveTheDriftTolerance_IsAnEdit()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Quad("cloth1_lod0"), TwoBoneSkin(), h => Paths[h], ws);
        var turned = ReSplit(Quad("cloth1_lod0"), TransportJitter);
        TurnNormals(turned, degrees: 10f);

        Assert.True(SendBackGeometry.Take(returned: Send(g, (turned, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false));
    }

    /// <summary>A normal with no length points nowhere, and comparing it by direction would divide by that
    /// nothing. Two of them are the same corner; one against a normal that does point somewhere is not.
    /// </summary>
    [Fact]
    public void ANormalWithNoDirection_MatchesOnlyAnotherWithNone()
    {
        var pointing = Shaded(new[] { 0f, 0, 1, 0, 0, 1, 0, 0, 1 });
        var none = Shaded(new float[9]);

        Assert.True(SendBackGeometry.SameContent(pointing, pointing));
        Assert.True(SendBackGeometry.SameContent(none, none));
        Assert.False(SendBackGeometry.SameContent(none, pointing));
        Assert.False(SendBackGeometry.SameContent(pointing, none));
    }

    /// <summary>A part the returned file does not carry cannot be compared, and neither can one whose
    /// workspace glb won't open. Both take the rewrite — an unanswerable question must not pass for "nothing
    /// changed" — and the rewrite refuses loudly.</summary>
    [Fact]
    public void APartThatCannotBeCompared_TakesTheRewriteAndItsFailure()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", yShift: 5f), TwoBoneSkin(), h => Paths[h], ws);
        var returned = RoundTrip(g, ws, "cloth1_lod0");

        Assert.Throws<InvalidOperationException>(
            () => SendBackGeometry.Take(returned, "body1_lod0", ws, hasMapAsks: false));
        File.WriteAllText(ws, "not a glb");
        Assert.True(SendBackGeometry.Take(returned, "cloth1_lod0", ws, hasMapAsks: false));
    }

    /// <summary>The kept-previous copy is the one way back to what the workspace glb held before the send, so
    /// a re-split that refuses must not have spent it. Everything that can refuse runs before the copy.</summary>
    [Fact]
    public void AReSplitThatRefuses_LeavesTheKeptPreviousCopyUnspent()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", yShift: 5f), TwoBoneSkin(), h => Paths[h], ws);
        var returned = RoundTrip(g, ws, "cloth1_lod0");
        var backedUp = new List<string>();

        Assert.Throws<InvalidOperationException>(
            () => SendBackGeometry.Take(returned, "body1_lod0", ws, hasMapAsks: true, backedUp.Add));

        Assert.Empty(backedUp);
    }

    // ---------------------------------------------------------------- maps

    /// <summary>An encoder that re-compresses an image it never edited changes its bytes and nothing else.
    /// Read by hash alone the map comes back authored, which ships a redundant copy of a stock texture and
    /// pays a block-compression encode for it.</summary>
    [Fact]
    public void AStockMapReEncodedByteForByteDifferent_StillResolvesAsStock()
    {
        using var g = new TempGame();
        var map = WritePng(g.At("body_d.png"), 5);
        var glb = g.At("body1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("body1_lod0", 0f), TwoBoneSkin(), h => Paths[h], glb, map);
        var embedded = PreviewMaps.ToPreview(map, MapKind.BaseColor);
        var reEncoded = ReEncode(embedded, edit: null);
        Assert.NotEqual(PreviewMaps.Hash(embedded), PreviewMaps.Hash(reEncoded));   // the hash really does miss

        var resolved = PreviewMaps.Resolve(reEncoded, MapKind.BaseColor, PreviewMaps.ReadSidecar(glb));

        Assert.Equal(MapOrigin.Vanilla, resolved.Origin);
        Assert.Equal(Path.GetFullPath(map), resolved.StockPng);
    }

    /// <summary>The control: one pixel apart is a painted map, and a painted map ships. Widening the stock
    /// answer to a re-encode may never widen it to an edit.</summary>
    [Fact]
    public void AMapOnePixelApartFromItsStock_ResolvesAsAuthored()
    {
        using var g = new TempGame();
        var map = WritePng(g.At("body_d.png"), 5);
        var glb = g.At("body1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("body1_lod0", 0f), TwoBoneSkin(), h => Paths[h], glb, map);
        var painted = ReEncode(PreviewMaps.ToPreview(map, MapKind.BaseColor),
            edit: img => img[3, 4] = new Rgba32(1, 2, 3, 255));

        var resolved = PreviewMaps.Resolve(painted, MapKind.BaseColor, PreviewMaps.ReadSidecar(glb));

        Assert.Equal(MapOrigin.Authored, resolved.Origin);
        Assert.NotNull(resolved.AuthoredPng);
    }

    /// <summary>Alpha is in the comparison: an RMO's emissive mask rides there and nothing else carries it,
    /// so a map that differs only in alpha is a changed map.</summary>
    [Fact]
    public void AMapDifferingOnlyInAlpha_ResolvesAsAuthored()
    {
        using var g = new TempGame();
        var rmo = WritePng(g.At("body_r.png"), 11);
        var glb = g.At("body1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("body1_lod0", 0f), TwoBoneSkin(), h => Paths[h], glb,
            perSubmesh: new (string?, string?, string?)[] { (null, null, rmo) });
        var masked = ReEncode(PreviewMaps.ToPreview(rmo, MapKind.Rmo), edit: img =>
        {
            var p = img[0, 0];
            img[0, 0] = new Rgba32(p.R, p.G, p.B, (byte)(p.A ^ 0xFF));
        });

        Assert.Equal(MapOrigin.Authored,
            PreviewMaps.Resolve(masked, MapKind.Rmo, PreviewMaps.ReadSidecar(glb)).Origin);
    }

    /// <summary>Plugging the shipped neutral normal in is the whole "blank this slot" gesture, and it survives
    /// an encoder that re-compressed it on the way back. The neutral's file is compared as it sits on disk,
    /// never through the transform a stock map's file needs — that is the whole reason a re-encoded one used
    /// to come back as a painted map.</summary>
    [Fact]
    public void AReEncodedNeutralNormal_StillReadsAsTheBlankGesture()
    {
        using var g = new TempGame();
        var textures = g.At("textures");
        Directory.CreateDirectory(textures);
        PreviewMaps.WriteNeutrals(textures);
        var stockNormal = WritePng(Path.Combine(textures, "body_n.png"), 5);
        var glb = g.At("body1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("body1_lod0", 0f), TwoBoneSkin(), h => Paths[h], glb,
            normalPng: stockNormal);
        var neutral = File.ReadAllBytes(Path.Combine(textures, PreviewMaps.NeutralN));
        var reEncoded = ReEncode(neutral, edit: null);
        Assert.NotEqual(PreviewMaps.Hash(neutral), PreviewMaps.Hash(reEncoded));   // the hash really does miss

        Assert.Equal(MapOrigin.Neutral,
            PreviewMaps.Resolve(reEncoded, MapKind.Normal, PreviewMaps.ReadSidecar(glb)).Origin);
    }

    /// <summary>Two parts' workspace maps holding one picture under different bytes. Answering with the
    /// ordinally first one hands a part its sibling's file, which the intake reads as a deliberate sibling
    /// link and ships — a donor the modder never asked for. Each slot gets its OWN map back.</summary>
    [Fact]
    public void APictureTwoPartsBothCarry_ResolvesToTheSlotsOwnMap()
    {
        using var g = new TempGame();
        var bodyMap = WritePng(g.At("body_d.png"), 1);
        var clothMap = WritePng(g.At("cloth_d.png"), 90);
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(Part("body1_lod0", 0f), TwoBoneSkin(), bodyMap),
            new MeshGltf.RiggedPart(Part("cloth1_lod0", 3f), TwoBoneSkin(), clothMap),
        }, h => Paths[h], combined);
        // the two files now hold the same picture, which the hashes recorded at export cannot see
        File.WriteAllBytes(bodyMap, ReEncode(File.ReadAllBytes(clothMap), edit: null));
        var sidecar = PreviewMaps.ReadSidecar(combined);
        var returned = ReEncode(PreviewMaps.ToPreview(clothMap, MapKind.BaseColor), edit: null);

        Assert.Equal(Path.GetFullPath(clothMap),
            PreviewMaps.Resolve(returned, MapKind.BaseColor, sidecar, owner: "cloth1_lod0").StockPng);
        Assert.Equal(Path.GetFullPath(bodyMap),
            PreviewMaps.Resolve(returned, MapKind.BaseColor, sidecar, owner: "body1_lod0").StockPng);
    }

    // ---------------------------------------------------------------- what the two halves decide together

    /// <summary>A texture-only edit on a part whose mesh came back exactly as it left. Deciding on the mesh
    /// alone leaves the part unflagged, and the build derives its Replace from that flag — so the linked map
    /// is recorded, ships nowhere, and the next lone re-open of the part destroys the record.</summary>
    [Fact]
    public void ASiblingLinkOnAGeometryIdenticalPart_IsTaken_AndBuildsAsAReplaceCarryingTheDonorRow()
    {
        using var g = new TempGame();
        var (root, project, t, ws) = Mod(g, out var bodyMap, out var clothMap);
        var session = Path.Combine(root, "meshes", "_combined.glb");
        // out: each part on its own map. back: body1's mesh as it left, its albedo re-linked to cloth1's
        Session(session, bodyMap, clothMap);
        var record = File.ReadAllBytes(PreviewMaps.SidecarPath(session));
        Session(session, clothMap, clothMap);
        File.WriteAllBytes(PreviewMaps.SidecarPath(session), record);
        Assert.False(project.IsEdited(t));

        var returned = MeshGltf.ParsedGlb.Open(session);
        Assert.True(SendBackGeometry.Unchanged(returned, "body1_lod0", ws));   // the mesh half has nothing
        var collected = Collect(root, returned, "body1_lod0");
        Assert.True(collected.Asks);                                           // the map half has the link

        Assert.True(TakeReturnedPart(project, t, returned, ws, collected));

        Assert.True(project.IsEdited(t));   // the rewrite is what makes the build see a replacement at all
        var replace = Assert.Single(
            VerbDerivation.DeriveAll(project, (_, _) => Subject(), new List<string>()),
            e => e.Verb == EditVerbs.Replace);
        Assert.Equal("body1_lod0", replace.Mesh);
        Assert.Equal(Path.GetFullPath(clothMap),
            Path.GetFullPath(Path.Combine(root, Assert.Single(replace.Textures!).Albedo!)));
    }

    /// <summary>The shipped neutral plugged into a slot asks the build to blank it, on a part whose mesh never
    /// moved. It is an ask like any other, and it takes the part.</summary>
    [Fact]
    public void APluggedNeutralOnAGeometryIdenticalPart_TakesThePart()
    {
        using var g = new TempGame();
        var (root, project, t, ws) = Mod(g, out var bodyMap, out var clothMap);
        var textures = Path.Combine(root, "textures");
        PreviewMaps.WriteNeutrals(textures);   // beside the stock maps, where the session's record finds it
        var session = Path.Combine(root, "meshes", "_combined.glb");
        Session(session, bodyMap, clothMap);

        // the slot as Blender hands it back: the neutral's own file, plugged into the normal
        var plugged = PreviewMaps.Resolve(File.ReadAllBytes(Path.Combine(textures, PreviewMaps.NeutralN)),
            MapKind.Normal, PreviewMaps.ReadSidecar(session));
        Assert.Equal(MapOrigin.Neutral, plugged.Origin);
        var rows = DonorTextureIntake.Collect(
            new[] { new IncomingMaps(new ResolvedMap(MapOrigin.Vanilla, StockPng: bodyMap), plugged) },
            textures, "body1", p => Rel(root, p), PreviewMaps.ReadOwnedStock(session, "body1_lod0"));
        var collected = new MainWindowViewModel.SendBackCollect(rows, null,
            MainWindowViewModel.SendBackMaps.None);
        Assert.True(collected.Asks);

        var returned = MeshGltf.ParsedGlb.Open(session);
        Assert.True(SendBackGeometry.Unchanged(returned, "body1_lod0", ws));
        Assert.True(TakeReturnedPart(project, t, returned, ws, collected));

        Assert.Equal(SlotOrigin.ExplicitNeutral, Assert.Single(t.DonorTextures!).NormalAsk);
    }

    /// <summary>The part a send-all carried and the modder never touched: same mesh, same skin, every slot on
    /// its own stock map. Nothing about it may be written — not the file, not the edited flag, not the record
    /// describing what is in the file.</summary>
    [Fact]
    public void APartUnchangedInBothHalves_IsLeftAloneEntirely()
    {
        using var g = new TempGame();
        var (root, project, t, ws) = Mod(g, out var bodyMap, out var clothMap);
        var session = Path.Combine(root, "meshes", "_combined.glb");
        Session(session, bodyMap, clothMap);
        var before = File.ReadAllBytes(ws);
        t.DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "textures/kept.png" } };

        var returned = MeshGltf.ParsedGlb.Open(session);
        var collected = Collect(root, returned, "body1_lod0");
        Assert.False(collected.Asks);

        Assert.False(TakeReturnedPart(project, t, returned, ws, collected));

        Assert.Equal(before, File.ReadAllBytes(ws));
        Assert.False(t.Edited);
        Assert.False(project.IsEdited(t));
        Assert.Equal("textures/kept.png", Assert.Single(t.DonorTextures!).Albedo);
    }

    // ------------------------------------------- the lone rebuild and its originals/ baseline

    /// <summary>Edited is a byte-compare against <c>originals/</c>, so a rebuild of a part's workspace glb from
    /// the GAME mesh is only safe while it reaches that baseline in the same breath. Published through the
    /// staged pair the part still reads untouched however the export's own shape moves — and its map record
    /// travels with the file it describes, since a rebuild written under a different name leaves a sidecar
    /// pointing at nothing.</summary>
    [Fact]
    public void ALoneRebuildPublishedThroughItsBaseline_LeavesAnUneditedPartUnedited()
    {
        using var g = new TempGame();
        var (root, project, t, ws) = Mod(g, out var bodyMap, out _);
        var orig = Path.Combine(root, "originals", "body1_lod0.glb");
        Assert.False(project.IsEdited(t));

        // the rig rebuild the lone open runs, here under an armature shape the baseline was not written with
        var staged = Path.Combine(root, "meshes", "~rebuild.body1_lod0.glb");
        void Stage() => MeshGltf.ExportRiggedGlb(Part("body1_lod0", 0f), TwoBoneSkin(), h => Paths[h], staged,
            bodyMap, scenePaths: new[] { "Bip001/pelvis", "Bip001/pelvis/arm" });

        Stage();
        Assert.Null(MainWindowViewModel.PublishRebuiltPartGlb(staged, ws, orig));

        Assert.False(project.IsEdited(t));                                    // the pair moved together
        Assert.Equal(File.ReadAllBytes(ws), File.ReadAllBytes(orig));
        Assert.False(File.Exists(staged));                                    // nothing staged is left behind
        Assert.False(File.Exists(PreviewMaps.SidecarPath(staged)));
        Assert.True(File.Exists(PreviewMaps.SidecarPath(ws)));                // …the record came with the glb

        // an untouched part stays untouched across a second identical rebuild
        Stage();
        Assert.Null(MainWindowViewModel.PublishRebuiltPartGlb(staged, ws, orig));
        Assert.False(project.IsEdited(t));
    }

    /// <summary>The baseline is written FIRST, so the write that can fail fails before anything the modder
    /// can see has moved. A read-only baseline, an anti-virus hold, a full disk: the workspace glb is still
    /// the file that matches it, the part is still unedited, and the refusal says which half went wrong.</summary>
    [Fact]
    public void ABaselineThatWontTake_LeavesBothFilesStanding_AndSaysSo()
    {
        using var g = new TempGame();
        var (root, project, t, ws) = Mod(g, out var bodyMap, out _);
        var orig = Path.Combine(root, "originals", "body1_lod0.glb");
        var before = File.ReadAllBytes(ws);
        var staged = Path.Combine(root, "meshes", "~rebuild.body1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("body1_lod0", 0f), TwoBoneSkin(), h => Paths[h], staged, bodyMap,
            scenePaths: new[] { "Bip001/pelvis", "Bip001/pelvis/arm" });

        string? said;
        using (new FileStream(orig, FileMode.Open, FileAccess.Read, FileShare.None))
            said = MainWindowViewModel.PublishRebuiltPartGlb(staged, ws, orig);

        Assert.NotNull(said);
        Assert.StartsWith("Couldn't refresh the part's baseline copy", said);
        Assert.EndsWith("Blender not opened.", said);
        Assert.Equal(before, File.ReadAllBytes(ws));    // the workspace glb was never touched
        Assert.False(project.IsEdited(t));              // …so the part claims no edit it didn't get

        MainWindowViewModel.DiscardStagedGlb(staged);
        Assert.False(File.Exists(staged));
    }

    /// <summary>The mirror failure: the baseline took but the workspace glb wouldn't move. An unedited part
    /// IS byte-equality, so the workspace file that never moved is itself the baseline's content — copying it
    /// back settles the pair rather than leaving the part reading edited.</summary>
    [Fact]
    public void AWorkspaceFileThatWontMove_PutsTheBaselineBack()
    {
        using var g = new TempGame();
        var (root, project, t, ws) = Mod(g, out var bodyMap, out _);
        var orig = Path.Combine(root, "originals", "body1_lod0.glb");
        var before = File.ReadAllBytes(ws);
        var staged = Path.Combine(root, "meshes", "~rebuild.body1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("body1_lod0", 0f), TwoBoneSkin(), h => Paths[h], staged, bodyMap,
            scenePaths: new[] { "Bip001/pelvis", "Bip001/pelvis/arm" });

        string? said;
        using (new FileStream(ws, FileMode.Open, FileAccess.Read, FileShare.Read))
            said = MainWindowViewModel.PublishRebuiltPartGlb(staged, ws, orig);

        Assert.NotNull(said);
        Assert.StartsWith("Couldn't rebuild the part's workspace file", said);
        Assert.Equal(before, File.ReadAllBytes(ws));
        Assert.Equal(before, File.ReadAllBytes(orig));   // restored from the file that never moved
        Assert.False(project.IsEdited(t));
    }

    /// <summary>A Revert restores the baseline's bytes over the workspace glb, which no rig stamp describes.
    /// The cache entry goes with the file: kept, the next open would launch the restored copy as though the
    /// rebuild had rigged it.</summary>
    [Fact]
    public async Task RevertingAPart_DropsItsRigCacheEntry()
    {
        using var g = new TempGame();
        var (root, _, _, ws) = Mod(g, out _, out _);
        var orig = Path.Combine(root, "originals", "body1_lod0.glb");
        File.WriteAllBytes(ws, new byte[] { 1, 2, 3 });        // whatever the last session left
        var rigged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [ws] = "texstamp" };

        await MainWindowViewModel.RevertMeshFileAsync(orig, ws, rigged);

        Assert.Equal(File.ReadAllBytes(orig), File.ReadAllBytes(ws));
        Assert.Empty(rigged);
    }

    /// <summary>How a lone open addresses its send. The session sits beside the part's OWN workspace glb and
    /// names no send file, so Send overwrites that very path — and the arriving path is what picks the target
    /// out of a project holding several mesh parts. A session that carried a send name, or landed under the
    /// combined one, would take the multi-part receive and address nothing.</summary>
    [Fact]
    public void ALoneSessionsSend_LandsOnThePartItWasOpenedFor()
    {
        using var g = new TempGame();
        var (root, project, t, ws) = Mod(g, out _, out _);
        // a sibling mesh part, so matching the target is a choice rather than the only row present
        project.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = "cloth1_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "meshes/cloth1_lod0.glb", OriginalFile = "originals/cloth1_lod0.glb",
        });

        BlenderBridge.WriteSession(ws, t.ObjectName, new[] { new SessionPart(t.ObjectName, project.IsEdited(t)) });

        var (part, parts) = BlenderBridge.ReadSession(ws);
        Assert.Equal("body1_lod0", part);                                   // the one mesh it may write back
        Assert.Equal(new[] { "body1_lod0" }, parts.Select(p => p.Name).ToArray());
        // no send name: the send comes home on the opened file's own path
        Assert.DoesNotContain("sendAs", File.ReadAllText(BlenderBridge.SessionPath(ws)));
        var arriving = BlenderBridge.GlbForSidecar(BlenderBridge.SidecarPath(ws));
        Assert.Equal(Path.GetFullPath(ws), Path.GetFullPath(arriving!));
        // …which is not the combined send, so the receive is the per-part one, keyed on that path
        Assert.NotEqual(AssetExporter.CombinedSendGlbName, Path.GetFileName(arriving));
        Assert.Same(t, project.Targets.Single(x => x.AssetType == "Mesh"
            && string.Equals(x.ReplaceFile, Rel(root, arriving!), StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------- the send arrives under a name the record isn't beside

    /// <summary>The shape the app actually receives: a combined send lands under its OWN filename, so the
    /// only map record in the folder is the one published beside the combined the app built. Classified
    /// against the arriving name there is no record at all, every returned map reads authored, and a session
    /// nobody textured asks the build to ship the whole outfit's stock maps back.</summary>
    [Fact]
    public void ACombinedSendUnderItsOwnName_ReadsAnUntouchedMapAsStock()
    {
        using var g = new TempGame();
        var (root, _, _, _) = Mod(g, out var bodyMap, out var clothMap);
        var meshes = Path.Combine(root, "meshes");
        var published = Path.Combine(meshes, AssetExporter.CombinedGlbName);
        Session(published, bodyMap, clothMap);
        var send = SendUnderItsOwnName(meshes, SessionParts(Part("body1_lod0", 0f), bodyMap, clothMap));
        // the fixture IS the app's folder: record beside the published combined, none beside the send
        Assert.True(File.Exists(PreviewMaps.SidecarPath(published)));
        Assert.False(File.Exists(PreviewMaps.SidecarPath(send)));
        Assert.Equal(MapOrigin.Authored,                              // read against itself the send is blind
            MeshGltf.ReadSubmeshMaps(send, "body1_lod0")[0].BaseColor.Origin);

        var maps = MeshGltf.ReadSubmeshMaps(MeshGltf.ParsedGlb.Open(send), "body1_lod0", published);

        Assert.Equal(MapOrigin.Vanilla, maps[0].BaseColor.Origin);
        Assert.Equal(Path.GetFullPath(bodyMap), maps[0].BaseColor.StockPng);
    }

    /// <summary>The control on the record: pointing the read at the published combined may not turn a map the
    /// modder painted into an untouched one. Only the image decides; the record just says what untouched
    /// looks like.</summary>
    [Fact]
    public void ACombinedSendUnderItsOwnName_StillReadsAPaintedMapAsAuthored()
    {
        using var g = new TempGame();
        var (root, _, _, _) = Mod(g, out var bodyMap, out var clothMap);
        var meshes = Path.Combine(root, "meshes");
        var published = Path.Combine(meshes, AssetExporter.CombinedGlbName);
        Session(published, bodyMap, clothMap);
        var painted = WritePng(Path.Combine(root, "textures", "painted_d.png"), 42);
        var send = SendUnderItsOwnName(meshes, SessionParts(Part("body1_lod0", 0f), painted, clothMap));

        var maps = MeshGltf.ReadSubmeshMaps(MeshGltf.ParsedGlb.Open(send), "body1_lod0", published);

        Assert.Equal(MapOrigin.Authored, maps[0].BaseColor.Origin);
        Assert.NotNull(maps[0].BaseColor.AuthoredPng);
        // and the part nobody touched still reads stock off the same record
        Assert.Equal(MapOrigin.Vanilla, MeshGltf
            .ReadSubmeshMaps(MeshGltf.ParsedGlb.Open(send), "cloth1_lod0", published)[0].BaseColor.Origin);
    }

    /// <summary>The re-split embeds the stock maps the part came back on, and those come from the same
    /// record the receive classified against. Without it the workspace glb is written with no maps and no
    /// record — the part opens untextured on its own, and its next send-back reads every untouched map as
    /// authored.</summary>
    [Fact]
    public void AReSplitOutOfACombinedSend_EmbedsThePublishedRecordsStockMapsInTheWorkspaceGlb()
    {
        using var g = new TempGame();
        var (root, project, t, ws) = Mod(g, out var bodyMap, out var clothMap);
        var meshes = Path.Combine(root, "meshes");
        var published = Path.Combine(meshes, AssetExporter.CombinedGlbName);
        Session(published, bodyMap, clothMap);
        // a geometry edit on body1, its maps untouched: the part is taken and the file rewritten
        var moved = Part("body1_lod0", 0f);
        moved.Channels["Vertex"][1] += 0.25f;
        var send = SendUnderItsOwnName(meshes, SessionParts(moved, bodyMap, clothMap));
        var returned = MeshGltf.ParsedGlb.Open(send);
        var collected = new MainWindowViewModel.SendBackCollect(null, null, MainWindowViewModel.SendBackMaps.None);

        Assert.True(TakeReturnedPart(project, t, returned, ws, collected, published));

        var written = MeshGltf.ReadSubmeshMaps(ws);
        Assert.Equal(MapOrigin.Vanilla, written[0].BaseColor.Origin);
        Assert.Equal(Path.GetFullPath(bodyMap), written[0].BaseColor.StockPng);
        Assert.Equal(0.25f, Ys(MeshGltf.ImportGlb(ws))[0], 4);   // and it is the returned geometry in there
    }

    /// <summary>The other half of the same write: a map the modder PAINTED in the session has to reach the
    /// workspace glb too. The intake writes it into <c>textures/</c> and the record names it, but the file the
    /// part re-opens from is this one — embed the stock map instead and the modder's own work is invisible
    /// every time they open that part alone.</summary>
    [Fact]
    public void AReSplitOutOfACombinedSend_EmbedsTheAuthoredMapTheIntakeWrote()
    {
        using var g = new TempGame();
        var (root, project, t, ws) = Mod(g, out var bodyMap, out var clothMap);
        var meshes = Path.Combine(root, "meshes");
        var published = Path.Combine(meshes, AssetExporter.CombinedGlbName);
        Session(published, bodyMap, clothMap);
        // body1 comes back with a painted albedo and its mesh exactly as it left
        var painted = WritePng(Path.Combine(g.Root, "painted_d.png"), 42);
        var send = SendUnderItsOwnName(meshes, SessionParts(Part("body1_lod0", 0f), painted, clothMap));
        var returned = MeshGltf.ParsedGlb.Open(send);
        var collected = Collect(root, returned, "body1_lod0", published);
        var authoredFile = Path.GetFullPath(Path.Combine(root, Assert.Single(collected.Rows!).Albedo!));
        Assert.True(File.Exists(authoredFile));   // the intake wrote it before the re-split ran

        Assert.True(TakeReturnedPart(project, t, returned, ws, collected, published));

        // the workspace glb now opens on the intake's file, and the stock map it covers is not recorded there
        var written = MeshGltf.ReadSubmeshMaps(ws);
        Assert.Equal(MapOrigin.Authored, written[0].BaseColor.Origin);
        Assert.DoesNotContain(PreviewMaps.ReadSidecar(ws).Values,
            e => e.Source == Path.GetFullPath(bodyMap) && e.Kind == MapKind.BaseColor);
        // the record still NAMES the authored file, which is what keeps it from being deleted as map-less
        Assert.Contains(Path.GetFileName(authoredFile), File.ReadAllText(PreviewMaps.SidecarPath(ws)));
    }

    /// <summary>What a combined session's parts are actually compared against. The workspace glbs such a
    /// session materializes carry GEOMETRY ONLY, and the session it publishes is rigged, so every part of a
    /// send-all comes back looking re-skinned when the workspace copy is the baseline — a zero-edit send-all
    /// then flags the whole outfit. The published combined is the file that was handed out, and against it
    /// an untouched part reads untouched.</summary>
    [Fact]
    public void ARiggedReturnAgainstASkinlessWorkspaceCopy_IsNoEdit_AgainstThePublishedSession()
    {
        using var g = new TempGame();
        var meshes = g.At("meshes");
        Directory.CreateDirectory(meshes);
        var ws = Path.Combine(meshes, "cloth1_lod0.glb");
        MeshGltf.ExportGlb(Quad("cloth1_lod0"), ws);
        var before = File.ReadAllBytes(ws);
        var published = Path.Combine(meshes, AssetExporter.CombinedGlbName);
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { Sibling(), new MeshGltf.RiggedPart(Quad("cloth1_lod0"), TwoBoneSkin()) },
            h => Paths[h], published);
        var returned = Send(g, (ReSplit(Quad("cloth1_lod0"), TransportJitter), TwoBoneSkin()));

        // the workspace copy really does carry no skin, which is what reads the rigged return as changed
        Assert.False(MeshGltf.ImportPayload(ws, lenient: true).HasSkin);
        Assert.False(SendBackGeometry.Unchanged(returned, "cloth1_lod0", ws));

        Assert.False(SendBackGeometry.Take(returned, "cloth1_lod0", ws, hasMapAsks: false,
            baselineGlb: published));

        Assert.Equal(before, File.ReadAllBytes(ws));
    }

    /// <summary>The control on the baseline swap: comparing against the published session may not turn an
    /// edit into an untouched part. The part that carries a moved vertex is still taken.</summary>
    [Fact]
    public void AnEditedPartComparedAgainstThePublishedSession_IsStillAnEdit()
    {
        using var g = new TempGame();
        var meshes = g.At("meshes");
        Directory.CreateDirectory(meshes);
        var ws = Path.Combine(meshes, "cloth1_lod0.glb");
        MeshGltf.ExportGlb(Quad("cloth1_lod0"), ws);
        var published = Path.Combine(meshes, AssetExporter.CombinedGlbName);
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { Sibling(), new MeshGltf.RiggedPart(Quad("cloth1_lod0"), TwoBoneSkin()) },
            h => Paths[h], published);
        var moved = ReSplit(Quad("cloth1_lod0"), TransportJitter);
        moved.Channels["Vertex"][1] += 0.25f;

        Assert.True(SendBackGeometry.Take(returned: Send(g, (moved, TwoBoneSkin())), "cloth1_lod0", ws,
            hasMapAsks: false, baselineGlb: published));
    }

    /// <summary>A baseline that isn't there, and one that is there without the part in it. Neither can answer
    /// "nothing changed", so both take the rewrite — the same safe direction an unreadable workspace glb
    /// takes.</summary>
    [Fact]
    public void ABaselineThatCannotAnswer_TakesTheRewrite()
    {
        using var g = new TempGame();
        var meshes = g.At("meshes");
        Directory.CreateDirectory(meshes);
        var ws = Path.Combine(meshes, "cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Quad("cloth1_lod0"), TwoBoneSkin(), h => Paths[h], ws);
        var returned = RoundTrip(g, ws, "cloth1_lod0");
        var absent = Path.Combine(meshes, AssetExporter.CombinedGlbName);       // never published
        var wrongPart = Path.Combine(meshes, "hair_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("hair_lod0", yShift: 9f), TwoBoneSkin(), h => Paths[h], wrongPart);

        Assert.True(SendBackGeometry.Unchanged(returned, "cloth1_lod0", ws));   // the control: it IS unchanged
        Assert.False(SendBackGeometry.Unchanged(returned, "cloth1_lod0", ws, absent));
        Assert.False(SendBackGeometry.Unchanged(returned, "cloth1_lod0", ws, wrongPart));

        Assert.True(SendBackGeometry.Take(returned, "cloth1_lod0", ws, hasMapAsks: false, baselineGlb: absent));
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>Take one returned part the way the combined receive does: the file work first, and the ledger
    /// record only where that wrote something. The receive splits the two because it runs them in different
    /// places; here they stay together, so a test asserting on both sees the order the app uses.
    ///
    /// <para><paramref name="recordGlb"/> is the published combined, which the receive hands over as both the
    /// map-origin record and the baseline the return is compared against.</para></summary>
    private static bool TakeReturnedPart(ModProject project, ProjectTarget t, MeshGltf.ParsedGlb returned,
        string workspaceGlb, MainWindowViewModel.SendBackCollect collected, string? recordGlb = null)
    {
        if (!SendBackGeometry.Take(returned, t.ObjectName, workspaceGlb, collected.Asks,
                MainWindowViewModel.KeepPreviousGlb, recordGlb,
                MainWindowViewModel.AuthoredMapPaths(project, collected.Rows), baselineGlb: recordGlb))
            return false;
        MainWindowViewModel.RecordTakenPart(project, t, workspaceGlb, collected);
        return true;
    }

    private const uint HRoot = 0x1111_1111, HArm = 0x2222_2222, HOther = 0x3333_3333;
    private static readonly Dictionary<uint, string> Paths =
        new() { [HRoot] = "root", [HArm] = "root/arm", [HOther] = "root/other" };

    /// <summary>The part as a session hands it back: read out of its workspace glb and written into a
    /// combined send through this repo's own export, with nothing edited on the way.</summary>
    private static MeshGltf.ParsedGlb RoundTrip(TempGame g, string workspaceGlb, string meshName) =>
        Send(g, MeshGltf.ReadRiggedGlb(workspaceGlb, meshName)!.Value);

    /// <summary>One part written into a combined send, with another part of the session ahead of it. The
    /// sibling's bones enter the union armature first, so the part under test binds to joint indices that are
    /// NOT the ones its own workspace glb uses — which is what the skin comparison has to see through.
    /// </summary>
    private static MeshGltf.ParsedGlb Send(TempGame g, (UnityMesh Mesh, MeshSkin Skin) part)
    {
        var returned = g.At("_combined.send.glb");
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { Sibling(), new MeshGltf.RiggedPart(part.Mesh, part.Skin) }, h => Paths[h], returned);
        return MeshGltf.ParsedGlb.Open(returned);
    }

    /// <summary>Another writable part of the same session, on a bone the part under test does not use.</summary>
    private static MeshGltf.RiggedPart Sibling() => new(Part("hair_lod0", yShift: 9f), new MeshSkin
    {
        BoneHashes = new[] { HRoot, HOther },
        BindPoses = new List<Matrix4x4> { Matrix4x4.Identity, Matrix4x4.CreateTranslation(1, 0, 0) },
    });

    /// <summary>A mod holding one writable part with its <c>originals/</c> copy beside it, plus the two stock
    /// maps a session of it embeds.</summary>
    private static (string Root, ModProject Project, ProjectTarget Target, string Workspace) Mod(
        TempGame g, out string bodyMap, out string clothMap)
    {
        var root = g.At("mod");
        Directory.CreateDirectory(Path.Combine(root, "meshes"));
        Directory.CreateDirectory(Path.Combine(root, "originals"));
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        bodyMap = WritePng(Path.Combine(root, "textures", "body_d.png"), 1);
        clothMap = WritePng(Path.Combine(root, "textures", "cloth_d.png"), 90);
        var ws = Path.Combine(root, "meshes", "body1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("body1_lod0", 0f), TwoBoneSkin(), h => Paths[h], ws, bodyMap);
        File.Copy(ws, Path.Combine(root, "originals", "body1_lod0.glb"));

        var project = new ModProject { RootDir = root };
        project.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        var t = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = "body1_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "meshes/body1_lod0.glb", OriginalFile = "originals/body1_lod0.glb",
        };
        project.Targets.Add(t);
        return (root, project, t, ws);
    }

    /// <summary>The outfit session: the writable part and one sibling, each on the map named for it.</summary>
    private static void Session(string path, string bodyMap, string clothMap) =>
        MeshGltf.ExportCombinedRiggedGlb(SessionParts(Part("body1_lod0", 0f), bodyMap, clothMap),
            h => Paths[h], path);

    /// <summary>The session's parts, with the writable one supplied so a send can carry it edited.</summary>
    private static IReadOnlyList<MeshGltf.RiggedPart> SessionParts(UnityMesh body, string bodyMap, string clothMap) =>
        new[]
        {
            new MeshGltf.RiggedPart(body, TwoBoneSkin(), bodyMap),
            new MeshGltf.RiggedPart(Part("cloth1_lod0", 3f), TwoBoneSkin(), clothMap),
        };

    /// <summary>The session as it comes back from Blender: written under the send's own filename, with no map
    /// record beside it. Nothing writes one there — the bridge doesn't, and the app's record was published
    /// beside the combined glb it built.</summary>
    private static string SendUnderItsOwnName(string meshesDir, IReadOnlyList<MeshGltf.RiggedPart> parts)
    {
        var send = Path.Combine(meshesDir, AssetExporter.CombinedSendGlbName);
        MeshGltf.ExportCombinedRiggedGlb(parts, h => Paths[h], send);
        File.Delete(PreviewMaps.SidecarPath(send));
        return send;
    }

    /// <summary>One part's map slots off the returned session, through the intake the app runs.
    /// <paramref name="recordGlb"/> is where the origin record lives when the send arrived under a name of its
    /// own; null reads it beside the returned glb.</summary>
    private static MainWindowViewModel.SendBackCollect Collect(string root, MeshGltf.ParsedGlb returned,
        string meshName, string? recordGlb = null)
    {
        var maps = MeshGltf.ReadSubmeshMaps(returned, meshName, recordGlb);
        var rows = DonorTextureIntake.Collect(maps, Path.Combine(root, "textures"), "body1",
            p => Rel(root, p), PreviewMaps.ReadOwnedStock(recordGlb ?? returned.Path, meshName));
        return new MainWindowViewModel.SendBackCollect(rows, maps.Select(m => m.MaterialName).ToList(),
            MainWindowViewModel.SendBackMaps.None);
    }

    private static string Rel(string root, string abs) =>
        Path.GetRelativePath(root, abs).Replace('\\', '/');

    private static SubjectModel Subject() => new("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
    {
        new SubjectPart("body", "body1_lod0", "addr_body", Array.Empty<SubjectMaterial>()),
        new SubjectPart("cloth", "cloth1_lod0", "addr_cloth", Array.Empty<SubjectMaterial>()),
    }, Skeleton: null, Problems: Array.Empty<string>());

    /// <summary>Encode the same pixels again, optionally changing some first. The encoder level differs from
    /// the one that wrote the embedded image, so the bytes differ whether or not a pixel does.</summary>
    private static byte[] ReEncode(byte[] png, Action<Image<Rgba32>>? edit)
    {
        using var img = Image.Load<Rgba32>(png);
        edit?.Invoke(img);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms, new PngEncoder { CompressionLevel = PngCompressionLevel.NoCompression });
        return ms.ToArray();
    }

    /// <summary>How far the transport moves a float it never edited. Sits at the top of what a glTF
    /// re-export's re-quantization produces, so a fixture carrying it is the worst untouched case.</summary>
    private const float TransportJitter = 4e-7f;

    /// <summary>Two triangles over four vertices sharing a diagonal — a mesh a re-export has something to
    /// split. One vertex rides the arm bone, so the skin comparison has an influence to follow across the
    /// union armature a combined session binds to.</summary>
    private static UnityMesh Quad(string name) => new()
    {
        Name = name,
        VertexCount = 4,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 },
            ["Normal"] = new[] { 0f, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 1, 1, 0, 1 },
            ["BlendIndices"] = new float[] { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            ["BlendWeight"] = new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
        },
        Dims = new() { ["Vertex"] = 3, ["Normal"] = 3, ["TexCoord0"] = 2, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
        Submeshes = new() { new[] { 0, 1, 2, 0, 2, 3 } },
    };

    /// <summary>A strip of quads, split evenly across <paramref name="submeshes"/>. Every vertex carries a
    /// position and a UV of its own, so no corner can stand in for another and a pairing that finds one is
    /// the pairing — and there are enough triangles that moving them around is a real reordering.</summary>
    private static UnityMesh Strip(string name, int quads, int submeshes = 1) =>
        Strip(name, quads, submeshes, c => c * 0.37f);

    /// <summary>The strip laid out so every position sits exactly on a step of the grid the comparison
    /// searches — <see cref="SendBackGeometry"/> steps it at twice its own drift tolerance — which is where a
    /// value the transport nudges lands on the far side of the step from the one it left.</summary>
    private static UnityMesh OnGridSteps(string name)
    {
        const float step = 2f * 1e-4f;
        return Strip(name, quads: 24, submeshes: 1, c => c * step);
    }

    private static UnityMesh Strip(string name, int quads, int submeshes, Func<int, float> across)
    {
        int cols = quads + 1, n = cols * 2;
        var pos = new float[n * 3];
        var nrm = new float[n * 3];
        var uv = new float[n * 2];
        var bi = new float[n * 4];
        var bw = new float[n * 4];
        for (int c = 0; c < cols; c++)
            for (int r = 0; r < 2; r++)
            {
                int v = c * 2 + r;
                pos[v * 3] = across(c);
                pos[v * 3 + 1] = r * 0.53f;
                nrm[v * 3 + 2] = 1f;
                uv[v * 2] = c * 0.07f;
                uv[v * 2 + 1] = r * 0.31f;
                bi[v * 4] = c % 2;                 // alternating bones, so the skin has something to follow
                bw[v * 4] = 1f;
            }
        var tris = new List<int>();
        for (int c = 0; c < quads; c++)
        {
            int a = c * 2;
            tris.AddRange(new[] { a, a + 1, a + 2, a + 1, a + 3, a + 2 });
        }
        var lists = new List<int[]>();
        int per = quads / submeshes * 3;
        for (int s = 0; s < submeshes; s++)
            lists.Add(tris.GetRange(s * per, s == submeshes - 1 ? tris.Count - s * per : per).ToArray());
        return new UnityMesh
        {
            Name = name,
            VertexCount = n,
            Channels = new()
            {
                ["Vertex"] = pos, ["Normal"] = nrm, ["TexCoord0"] = uv,
                ["BlendIndices"] = bi, ["BlendWeight"] = bw,
            },
            Dims = new() { ["Vertex"] = 3, ["Normal"] = 3, ["TexCoord0"] = 2, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
            Submeshes = lists,
        };
    }

    /// <summary>The triangles of one index list in another order, the same way every run.</summary>
    private static int[] Shuffle(int[] indices)
    {
        var faces = new List<int>();
        for (int f = 0; f < indices.Length / 3; f++) faces.Add(f);
        for (int i = faces.Count - 1; i > 0; i--)
        {
            int j = (i * 7 + 3) % (i + 1);
            (faces[i], faces[j]) = (faces[j], faces[i]);
        }
        var shuffled = new int[indices.Length];
        for (int f = 0; f < faces.Count; f++)
            for (int k = 0; k < 3; k++)
                shuffled[f * 3 + k] = indices[faces[f] * 3 + k];
        return shuffled;
    }

    /// <summary>Turn every normal by <paramref name="degrees"/>, about an axis perpendicular to itself so the
    /// length is untouched and only the direction moves.</summary>
    private static void TurnNormals(UnityMesh m, float degrees)
    {
        var n = m.Channels["Normal"];
        int d = m.Dims["Normal"];
        float radians = degrees * MathF.PI / 180f;
        for (int v = 0; v < m.VertexCount; v++)
        {
            var normal = new Vector3(n[v * d], n[v * d + 1], n[v * d + 2]);
            // an axis the normal is not parallel to, so the cross product is a real perpendicular
            var off = MathF.Abs(Vector3.Normalize(normal).X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var turned = Vector3.Transform(normal,
                Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(normal, off)), radians));
            n[v * d] = turned.X;
            n[v * d + 1] = turned.Y;
            n[v * d + 2] = turned.Z;
        }
    }

    /// <summary>One triangle rigged to the root bone, with <paramref name="faint"/> of every vertex pulled
    /// off onto the arm — so a comparison of two of them turns on the skin alone. The two are laid out on the
    /// four influence slots a glb carries, the unused ones weighted at nothing.</summary>
    private static MeshApply.Payload Skinned(float faint)
    {
        var indices = new int[3 * 4];
        var weights = new float[3 * 4];
        for (int v = 0; v < 3; v++)
        {
            indices[v * 4 + 1] = 1;
            weights[v * 4] = 1f - faint;
            weights[v * 4 + 1] = faint;
        }
        return new MeshApply.Payload
        {
            Mesh = new UnityMesh
            {
                Name = "cloth1_lod0",
                VertexCount = 3,
                Channels = new() { ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 } },
                Dims = new() { ["Vertex"] = 3 },
                Submeshes = new() { new[] { 0, 1, 2 } },
            },
            JointIndices = indices,
            JointWeights = weights,
            SkinJointHashes = new[] { HRoot, HArm },
        };
    }

    /// <summary>One triangle carrying the given normals and nothing else — no skin, no maps — so a
    /// comparison of two of them turns on the shading alone.</summary>
    private static MeshApply.Payload Shaded(float[] normals) => MeshApply.Payload.Geometry(new UnityMesh
    {
        Name = "cloth1_lod0",
        VertexCount = 3,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 },
            ["Normal"] = normals,
        },
        Dims = new() { ["Vertex"] = 3, ["Normal"] = 3 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    });

    /// <summary>The quad with its UVs tiled into the tens, as an atlas coordinate is.</summary>
    private static UnityMesh TiledQuad(string name)
    {
        var m = Quad(name);
        var uv = m.Channels["TexCoord0"];
        for (int i = 0; i < uv.Length; i++) uv[i] = uv[i] * 2f + 20f;
        return m;
    }

    /// <summary>The part as a glTF re-export hands it back: every triangle corner given its own vertex — what
    /// an exporter does at a UV seam or a hard edge — and every geometry float shifted by
    /// <paramref name="jitter"/>. The surface is untouched; only the buffer holding it is. Indices and
    /// weights are copied as they stand, since neither is re-quantized.</summary>
    private static UnityMesh ReSplit(UnityMesh m, float jitter)
    {
        var corners = m.Submeshes.SelectMany(s => s).ToArray();
        var channels = new Dictionary<string, float[]>();
        foreach (var (name, data) in m.Channels)
        {
            int d = m.Dims[name];
            bool shift = name is "Vertex" or "Normal" or "TexCoord0";
            var split = new float[corners.Length * d];
            for (int c = 0; c < corners.Length; c++)
                for (int k = 0; k < d; k++)
                    split[c * d + k] = data[corners[c] * d + k] + (shift ? jitter : 0f);
            channels[name] = split;
        }
        var submeshes = new List<int[]>();
        int next = 0;
        foreach (var s in m.Submeshes)
        {
            var indices = new int[s.Length];
            for (int i = 0; i < s.Length; i++) indices[i] = next++;
            submeshes.Add(indices);
        }
        return new UnityMesh
        {
            Name = m.Name, VertexCount = corners.Length, Channels = channels,
            Dims = new Dictionary<string, int>(m.Dims), Submeshes = submeshes,
        };
    }

    private static UnityMesh Part(string name, float yShift) => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, yShift, 0, 0.5f, yShift + 1, 0, 1, yShift, 0 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1 },
            ["BlendIndices"] = new float[] { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 },
            ["BlendWeight"] = new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
        },
        Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };

    private static MeshSkin TwoBoneSkin() => new()
    {
        BoneHashes = new[] { HRoot, HArm },
        BindPoses = new List<Matrix4x4> { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0) },
    };

    private static float[] Ys(UnityMesh m)
    {
        var ys = new float[m.VertexCount];
        for (int v = 0; v < m.VertexCount; v++) ys[v] = m.Channels["Vertex"][v * 3 + 1];
        return ys;
    }

    /// <summary>A deterministic non-uniform image, so two fixtures' maps can never hash alike.</summary>
    private static string WritePng(string path, int seed)
    {
        using var img = new Image<Rgba32>(8, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                img[x, y] = new Rgba32((byte)(x * 31 + seed), (byte)(y * 17 + seed), (byte)(x * y + seed), (byte)(200 + x));
        img.SaveAsPng(path);
        return path;
    }
}
