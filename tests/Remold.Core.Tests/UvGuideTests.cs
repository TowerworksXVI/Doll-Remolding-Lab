using System;
using System.Collections.Generic;
using System.IO;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="UvGuide"/> rasterizes a mesh's UV0 wireframe to a transparent PNG. UVs map Unity-convention
/// (v = 0 bottom) into image space with a VERTICAL FLIP, matching the exported texture PNG.
/// </summary>
public class UvGuideTests
{
    private static UnityMesh TriMesh(int[] tri, params (float u, float v)[] uvs)
    {
        var flat = new float[uvs.Length * 2];
        for (int i = 0; i < uvs.Length; i++) { flat[i * 2] = uvs[i].u; flat[i * 2 + 1] = uvs[i].v; }
        return new UnityMesh
        {
            Name = "t",
            VertexCount = uvs.Length,
            Channels = new() { ["TexCoord0"] = flat },
            Dims = new() { ["TexCoord0"] = 2 },
            Submeshes = new() { tri },
        };
    }

    private static byte AlphaAt(string path, int x, int y)
    {
        using var img = Image.Load<Rgba32>(path);
        return img[x, y].A;
    }

    [Fact]
    public void NoUv0_RendersNothing()
    {
        using var g = new TempGame();
        var mesh = new UnityMesh { Name = "t", VertexCount = 3, Submeshes = new() { new[] { 0, 1, 2 } } };
        var path = g.At("guide.png");
        Assert.False(UvGuide.TryRender(mesh, 16, 16, path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void RendersAtTheRequestedSize()
    {
        using var g = new TempGame();
        var mesh = TriMesh(new[] { 0, 1, 2 }, (0, 0), (1, 0), (0, 1));
        var path = g.At("guide.png");
        Assert.True(UvGuide.TryRender(mesh, 32, 24, path));
        using var img = Image.Load<Rgba32>(path);
        Assert.Equal(32, img.Width);
        Assert.Equal(24, img.Height);
    }

    [Fact]
    public void DrawsEdgesAndLeavesTheRestTransparent()
    {
        using var g = new TempGame();
        // a right triangle in UV space: (0,0) (1,0) (0,1)
        var mesh = TriMesh(new[] { 0, 1, 2 }, (0, 0), (1, 0), (0, 1));
        var path = g.At("guide.png");
        Assert.True(UvGuide.TryRender(mesh, 16, 16, path));

        // v=0 maps to the bottom row (vertical flip), v=1 to the top — so the corners are:
        Assert.Equal(255, AlphaAt(path, 0, 15));   // (0,0) → bottom-left vertex, on an edge
        Assert.Equal(255, AlphaAt(path, 0, 0));    // (0,1) → top-left vertex, on an edge
        // the top-right corner is outside the lower-left triangle and off every edge → untouched
        Assert.Equal(0, AlphaAt(path, 15, 0));
    }

    // ---- per-texture guides: merge-plot + material grouping ------------------------------------

    // Two one-triangle submeshes on distinct UV islands: A in the left half, B in the right half.
    private static readonly (float u, float v)[] UvA = { (0.1f, 0.5f), (0.3f, 0.5f), (0.2f, 0.7f) };
    private static readonly (float u, float v)[] UvB = { (0.7f, 0.5f), (0.9f, 0.5f), (0.8f, 0.7f) };

    private static UnityMesh TwoIslandMesh()
    {
        var uv = new float[12];
        for (int i = 0; i < 3; i++) { uv[i * 2] = UvA[i].u; uv[i * 2 + 1] = UvA[i].v; }
        for (int i = 0; i < 3; i++) { uv[6 + i * 2] = UvB[i].u; uv[6 + i * 2 + 1] = UvB[i].v; }
        return new UnityMesh
        {
            Name = "part",
            VertexCount = 6,
            Channels = new() { ["TexCoord0"] = uv },
            Dims = new() { ["TexCoord0"] = 2 },
            Submeshes = new() { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } },
        };
    }

    private static bool WhiteAtUv(string png, float u, float v)
    {
        using var img = Image.Load<Rgba32>(png);
        int x = (int)MathF.Round(u * (img.Width - 1)), y = (int)MathF.Round((1f - v) * (img.Height - 1));
        return img[x, y].A > 0;
    }

    [Fact]
    public void TryRenderMerge_PlotsOnlyTheGivenSubmeshes_AndUnionsOntoAnExistingGuide()
    {
        using var g = new TempGame();
        var mesh = TwoIslandMesh();
        var path = g.At("merge.uvguide.png");

        // first part-materialize: only submesh 0's island lands
        Assert.True(UvGuide.TryRenderMerge(mesh, new[] { 0 }, 100, 100, path));
        Assert.True(WhiteAtUv(path, UvA[0].u, UvA[0].v));
        Assert.False(WhiteAtUv(path, UvB[0].u, UvB[0].v));

        // a second materialize plotting submesh 1 UNIONS — both islands now present
        Assert.True(UvGuide.TryRenderMerge(mesh, new[] { 1 }, 100, 100, path));
        Assert.True(WhiteAtUv(path, UvA[0].u, UvA[0].v));
        Assert.True(WhiteAtUv(path, UvB[0].u, UvB[0].v));

        // a size mismatch means the existing guide predates a re-Add — fresh canvas, not a merge
        Assert.True(UvGuide.TryRenderMerge(mesh, new[] { 1 }, 64, 64, path));
        Assert.False(WhiteAtUv(path, UvA[0].u, UvA[0].v));
        Assert.True(WhiteAtUv(path, UvB[0].u, UvB[0].v));
    }

    [Fact]
    public void WriteTextureUvGuides_GroupsSubmeshesByTheTexturesTheirMaterialReferences()
    {
        // submesh 0 samples texA + shared, submesh 1 texB + shared — so the shared map carries BOTH islands
        using var g = new TempGame();
        var mesh = TwoIslandMesh();
        var pngByName = new Dictionary<string, string>
        {
            ["texA"] = g.At("texA.beef.png"),
            ["texB"] = g.At("texB.beef.png"),
            ["shared"] = g.At("shared.beef.png"),
        };
        var partTex = new PartTextures(
            All: Array.Empty<TexTarget>(),
            Submeshes: new[]
            {
                new SubmeshMaps("texA", null, AllMaps: new[] { "texA", "shared" }),
                new SubmeshMaps("texB", null, AllMaps: new[] { "texB", "shared" }),
            });

        AssetExporter.WriteTextureUvGuides(mesh, partTex, pngByName, log: null, part: "part");

        string Guide(string name) => AssetExporter.UvGuidePathFor(pngByName[name]);
        Assert.EndsWith("texA.beef.uvguide.png", Guide("texA"));   // the naming contract the card action derives
        Assert.True(WhiteAtUv(Guide("texA"), UvA[0].u, UvA[0].v));
        Assert.False(WhiteAtUv(Guide("texA"), UvB[0].u, UvB[0].v));
        Assert.True(WhiteAtUv(Guide("texB"), UvB[0].u, UvB[0].v));
        Assert.False(WhiteAtUv(Guide("texB"), UvA[0].u, UvA[0].v));
        Assert.True(WhiteAtUv(Guide("shared"), UvA[0].u, UvA[0].v));
        Assert.True(WhiteAtUv(Guide("shared"), UvB[0].u, UvB[0].v));
    }

    // ---- on-demand builder: draw from the EDITED mesh when there is one --------------------------

    // A single-triangle mesh at the given island; positions/normals only exist so ExportGlb accepts it.
    private static UnityMesh TriMeshWithGeometry((float u, float v)[] island) => new()
    {
        Name = "part",
        VertexCount = 3,
        Channels = new()
        {
            ["Vertex"] = new[] { island[0].u, island[0].v, 0f,  island[1].u, island[1].v, 0.1f,  island[2].u, island[2].v, 0.2f },
            ["Normal"] = new[] { 0f, 0, 1,  0, 0, 1,  0, 0, 1 },
            ["TexCoord0"] = new[] { island[0].u, island[0].v,  island[1].u, island[1].v,  island[2].u, island[2].v },
        },
        Dims = new() { ["Vertex"] = 3, ["Normal"] = 3, ["TexCoord0"] = 2 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };

    [Fact]
    public void PlotUvGuide_DrawsFromTheEditedGlb_NotVanilla()
    {
        using var g = new TempGame();
        // the modder's edited workspace glb carries island A; the vanilla resolver would give island B.
        var moddedGlb = g.At("part.glb");
        MeshGltf.ExportGlb(TriMeshWithGeometry(UvA), moddedGlb);
        bool vanillaAsked = false;
        UnityMesh? Vanilla(string name, string addr) { vanillaAsked = true; return TriMeshWithGeometry(UvB); }

        var guide = g.At("m.uvguide.png");
        var samplers = new List<(string, string, int, string?)> { ("part", "addr", 0, moddedGlb) };
        var problem = AssetExporter.PlotUvGuide(samplers, 100, 100, "tex", guide, Vanilla);

        Assert.Null(problem);
        Assert.False(vanillaAsked, "an edited part must draw from its glb, never fall back to the game mesh");
        Assert.True(WhiteAtUv(guide, UvA[0].u, UvA[0].v));    // the mod's own UVs are drawn
        Assert.False(WhiteAtUv(guide, UvB[0].u, UvB[0].v));   // not the vanilla layout
    }

    [Fact]
    public void PlotUvGuide_FallsBackToVanilla_WhenPartHasNoEdit()
    {
        using var g = new TempGame();
        var guide = g.At("v.uvguide.png");
        var samplers = new List<(string, string, int, string?)> { ("part", "addr", 0, null) };
        Assert.Null(AssetExporter.PlotUvGuide(samplers, 100, 100, "tex", guide, (_, _) => TriMeshWithGeometry(UvB)));
        Assert.True(WhiteAtUv(guide, UvB[0].u, UvB[0].v));
    }

    [Fact]
    public void PlotUvGuide_RebuildsFresh_ReplacingTheStaleVanillaGuide()
    {
        using var g = new TempGame();
        var guide = g.At("stale.uvguide.png");
        // a stale guide from the vanilla layout (island B) sits on disk (materialize wrote it)...
        Assert.Null(AssetExporter.PlotUvGuide(
            new List<(string, string, int, string?)> { ("part", "addr", 0, null) },
            100, 100, "tex", guide, (_, _) => TriMeshWithGeometry(UvB)));
        Assert.True(WhiteAtUv(guide, UvB[0].u, UvB[0].v));

        // …then the mesh is edited and the guide rebuilds: the stale island is GONE, not unioned onto —
        // an edit's new UV layout REPLACES the old one.
        var moddedGlb = g.At("part.glb");
        MeshGltf.ExportGlb(TriMeshWithGeometry(UvA), moddedGlb);
        Assert.Null(AssetExporter.PlotUvGuide(
            new List<(string, string, int, string?)> { ("part", "addr", 0, moddedGlb) },
            100, 100, "tex", guide, (_, _) => TriMeshWithGeometry(UvB)));
        Assert.True(WhiteAtUv(guide, UvA[0].u, UvA[0].v));
        Assert.False(WhiteAtUv(guide, UvB[0].u, UvB[0].v));
    }
}
