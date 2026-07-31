using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The rig rebuild must read preview textures back from the SAME bundle-scoped workspace files every
/// producer writes; resolving them name-only silently strips every map from the rebuilt glb. These pin
/// <see cref="AssetExporter.ResolvePartPngs"/>: the scoped file and never the name-only one, a loud miss
/// when it is absent, and two same-named textures from different bundles kept distinct.
/// </summary>
public class AssetExporterTextureResolveTests
{
    /// <summary>The subject every workspace file here is scoped by — the same slug the producer folds in.</summary>
    private static readonly string Subj = ModNaming.SubjectSlug("Vesna", "VesnaSSR01");

    private static string WritePng(string path, Rgba32 color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var img = new Image<Rgba32>(4, 4, color);
        img.SaveAsPng(path);
        return path;
    }

    private static PartTextures OnePart(string baseName, string normalName, string bundle) => new(
        All: new[]
        {
            new TexTarget(baseName, bundle, IsBaseColor: true, IsNormal: false, "renderer"),
            new TexTarget(normalName, bundle, IsBaseColor: false, IsNormal: true, "renderer"),
        },
        Submeshes: new[] { new SubmeshMaps(baseName, normalName) });

    [Fact]
    public void ResolvePartPngs_ResolvesTheBundleScopedFile_NotTheNameOnlyFile()
    {
        using var g = new TempGame();
        var texDir = g.At("textures");
        // the real workspace file, AND a stray name-only file a name-only lookup would grab
        var scoped = WritePng(Path.Combine(texDir, TextureExport.BundleScopedName("aabb", "body_d", Subj)), new Rgba32(1, 2, 3, 255));
        WritePng(Path.Combine(texDir, "body_d.png"), new Rgba32(9, 9, 9, 255));   // must be IGNORED
        WritePng(Path.Combine(texDir, TextureExport.BundleScopedName("aabb", "body_n", Subj)), new Rgba32(4, 5, 6, 255));

        var (baseColor, normal, perSubmesh) = AssetExporter.ResolvePartPngs(texDir, Subj, OnePart("body_d", "body_n", "aabb"), "cloth1", null);

        Assert.Equal(scoped, baseColor);                                   // the bundle-scoped file, not body_d.png
        Assert.EndsWith($"body_d.aabb.{Subj}.png", baseColor);
        Assert.EndsWith($"body_n.aabb.{Subj}.png", normal);
        Assert.Equal(($"body_d.aabb.{Subj}.png", $"body_n.aabb.{Subj}.png"),
            (Path.GetFileName(perSubmesh[0].Item1), Path.GetFileName(perSubmesh[0].Item2)));
    }

    [Fact]
    public void ResolvePartPngs_AbsentScopedFile_ReturnsNull_AndFiresTheMiss_EvenWhenANameOnlyFileExists()
    {
        using var g = new TempGame();
        var texDir = g.At("textures");
        WritePng(Path.Combine(texDir, TextureExport.BundleScopedName("aabb", "body_d", Subj)), new Rgba32(1, 2, 3, 255));
        // the normal's scoped file is MISSING and only a name-only decoy is there, which would hide the gap
        WritePng(Path.Combine(texDir, "body_n.png"), new Rgba32(9, 9, 9, 255));

        var log = new ListLog();
        var (_, normal, perSubmesh) = AssetExporter.ResolvePartPngs(texDir, Subj, OnePart("body_d", "body_n", "aabb"), "cloth1", log);

        Assert.Null(normal);                        // name-only decoy ignored
        Assert.Null(perSubmesh[0].Item2);
        Assert.Contains(log.Lines, l => l.Contains("body_n") && l.Contains("not found"));   // the miss is loud
    }

    [Fact]
    public void ResolvePartPngs_AfterRescan_PrefersTheRecordedBundleFile_OverTheRendererPin()
    {
        // The rebuild re-runs the resolver against the CURRENT game, and a rescan can pin a DIFFERENT bundle
        // than the producer wrote to. Here the producer recorded "aaaa" and the renderer now pins "bbbb", so
        // re-deriving would look for an absent file. The RECORDED bundle must win.
        using var g = new TempGame();
        var texDir = g.At("textures");
        var original = WritePng(Path.Combine(texDir, TextureExport.BundleScopedName("aaaa", "body_d", Subj)), new Rgba32(1, 2, 3, 255));
        // NOTE: the bbbb-scoped file is deliberately NOT on disk — a re-derivation from the fresh pin would miss.
        var part = new PartTextures(
            All: new[] { new TexTarget("body_d", "bbbb", IsBaseColor: true, IsNormal: false, "renderer") },
            Submeshes: new[] { new SubmeshMaps("body_d", null) });
        var recorded = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.Ordinal)
        {
            ["body_d"] = new[] { "aaaa" },
        };

        var (baseColor, _, _) = AssetExporter.ResolvePartPngs(texDir, Subj, part, "cloth1", null, recorded);

        Assert.Equal(original, baseColor);                 // the recorded workspace file, not the fresh bbbb pin
        Assert.EndsWith($"body_d.aaaa.{Subj}.png", baseColor);
    }

    [Fact]
    public void ResolvePartPngs_UnmaterializedTexture_ResolvesViaTheRendererPin_WhenNotRecorded()
    {
        // A genuinely unmaterialized texture resolves by the renderer PPtr's pinned bundle — the same one
        // the producer would scope its file by — unaffected by records for other textures.
        using var g = new TempGame();
        var texDir = g.At("textures");
        var scoped = WritePng(Path.Combine(texDir, TextureExport.BundleScopedName("cccc", "other_d", Subj)), new Rgba32(7, 8, 9, 255));
        var part = new PartTextures(
            All: new[] { new TexTarget("other_d", "cccc", IsBaseColor: true, IsNormal: false, "renderer") },
            Submeshes: new[] { new SubmeshMaps("other_d", null) });
        var recorded = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.Ordinal)
        {
            ["body_d"] = new[] { "aaaa" },   // records a DIFFERENT texture; "other_d" is unmaterialized
        };

        var (baseColor, _, _) = AssetExporter.ResolvePartPngs(texDir, Subj, part, "cloth1", null, recorded);

        Assert.Equal(scoped, baseColor);                   // resolved via the renderer pin, unaffected by the record
    }

    [Fact]
    public void CombinedGlb_TwoSameNamedTexturesFromDifferentBundles_EmbedDistinctScopedImages()
    {
        using var g = new TempGame();
        var texDir = g.At("textures");
        // SAME texture name "body" in TWO parts, from DIFFERENT bundles — distinct game assets, distinct files
        var redFile = WritePng(Path.Combine(texDir, TextureExport.BundleScopedName("aaaa", "body", Subj)), new Rgba32(220, 20, 20, 255));
        var blueFile = WritePng(Path.Combine(texDir, TextureExport.BundleScopedName("bbbb", "body", Subj)), new Rgba32(20, 20, 220, 255));

        var (baseA, _, _) = AssetExporter.ResolvePartPngs(texDir, Subj, OneBaseOnly("body", "aaaa"), "partA", null);
        var (baseB, _, _) = AssetExporter.ResolvePartPngs(texDir, Subj, OneBaseOnly("body", "bbbb"), "partB", null);
        Assert.Equal(redFile, baseA);
        Assert.Equal(blueFile, baseB);
        Assert.NotEqual(baseA, baseB);

        var partA = new MeshGltf.RiggedPart(Tri("partA"), Skin(), baseA, null,
            PerSubmesh: new (string?, string?, string?)[] { (baseA, null, null) });
        var partB = new MeshGltf.RiggedPart(Tri("partB"), Skin(), baseB, null,
            PerSubmesh: new (string?, string?, string?)[] { (baseB, null, null) });
        var outPath = g.At("combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { partA, partB }, _ => "root", outPath);

        var model = ModelRoot.Load(outPath);
        Assert.Equal(2, model.LogicalImages.Count);   // both bundles' textures embed, distinctly (never collapsed to one)
        var blobs = model.LogicalImages.Select(i => i.Content.Content.ToArray()).ToList();
        Assert.False(blobs[0].SequenceEqual(blobs[1]), "the two bundles' textures must embed as distinct images");
    }

    [Fact]
    public void SingleGlb_ResolvedScopedTexture_EmbedsAsTheMaterial()
    {
        using var g = new TempGame();
        var texDir = g.At("textures");
        WritePng(Path.Combine(texDir, TextureExport.BundleScopedName("aabb", "body", Subj)), new Rgba32(200, 150, 100, 255));

        var (baseColor, _, perSubmesh) = AssetExporter.ResolvePartPngs(texDir, Subj, OneBaseOnly("body", "aabb"), "cloth1", null);
        var outPath = g.At("part.glb");
        MeshGltf.ExportGlb(Tri("cloth1"), outPath, baseColor, null, perSubmesh);

        var model = ModelRoot.Load(outPath);
        Assert.NotEmpty(model.LogicalMaterials);
        Assert.Single(model.LogicalImages);           // the scoped file resolved and embedded (not dropped as missing)
    }

    private static PartTextures OneBaseOnly(string name, string bundle) => new(
        All: new[] { new TexTarget(name, bundle, IsBaseColor: true, IsNormal: false, "renderer") },
        Submeshes: new[] { new SubmeshMaps(name, null) });

    private static UnityMesh Tri(string name) => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1 },
            ["BlendIndices"] = new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            ["BlendWeight"] = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
        },
        Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };

    private static MeshSkin Skin() => new()
    {
        BoneHashes = new uint[] { 0x1111_1111 },
        BindPoses = new List<Matrix4x4> { Matrix4x4.Identity },
    };

    private sealed class ListLog : System.IProgress<string>
    {
        public List<string> Lines { get; } = new();
        public void Report(string value) => Lines.Add(value);
    }
}
