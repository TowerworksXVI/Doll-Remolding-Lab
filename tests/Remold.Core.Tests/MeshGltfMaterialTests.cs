using System.Linq;
using Remold.Core.Mesh;
using Remold.Core.Tests.Support;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Blender-facing export can embed a preview PBR material so the part shows textured. It must stay
/// round-trip safe — riding alongside the geometry without disturbing the shared vertex pool — and never
/// appear on the material-free mod payload.
/// </summary>
public class MeshGltfMaterialTests
{
    // two submeshes on one 4-vertex pool — the layout that balloons if the material path re-mints
    // accessors per primitive
    private static UnityMesh TwoSubmeshPatch() => new()
    {
        Name = "patch",
        VertexCount = 4,
        Channels = new()
        {
            ["Vertex"] = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 },
            ["TexCoord0"] = new float[] { 0, 0, 1, 0, 0, 1, 1, 1 },
        },
        Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2 },
        Submeshes = new() { new[] { 0, 1, 2 }, new[] { 1, 3, 2 } },
    };

    private static string WritePng(string path) => WritePng(path, new Rgba32(200, 150, 100, 255));

    private static string WritePng(string path, Rgba32 color)
    {
        using var img = new Image<Rgba32>(4, 4, color);
        img.SaveAsPng(path);
        return path;
    }

    private static Rgba32 FirstPixel(byte[] png)
    {
        using var img = SixLabors.ImageSharp.Image.Load<Rgba32>(png);
        return img[0, 0];
    }

    [Fact]
    public void ExportWithMaterial_StillRoundTripsGeometry()
    {
        using var g = new TempGame();
        var path = g.At("mat.glb");
        var src = TwoSubmeshPatch();
        MeshGltf.ExportGlb(src, path, WritePng(g.At("base.png")), WritePng(g.At("nrm.png")));
        var back = MeshGltf.ImportGlb(path);
        // the material must not inflate the shared pool (a ×submesh-count blow-up)
        Assert.Equal(src.VertexCount, back.VertexCount);
        Assert.Equal(src.Submeshes.Count, back.Submeshes.Count);
    }

    [Fact]
    public void ExportWithMaterial_EmbedsBaseColorAndNormalTextures()
    {
        using var g = new TempGame();
        var path = g.At("mat.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, WritePng(g.At("base.png")), WritePng(g.At("nrm.png")));

        var model = ModelRoot.Load(path);
        Assert.NotEmpty(model.LogicalMaterials);
        var mat = model.LogicalMaterials[0];
        Assert.NotNull(mat.FindChannel("BaseColor")?.Texture);
        Assert.NotNull(mat.FindChannel("Normal")?.Texture);
    }

    [Fact]
    public void PerSubmesh_SameTexture_GetsDistinctMaterialsButOneSharedImage()
    {
        using var g = new TempGame();
        var path = g.At("split.glb");
        var basePng = WritePng(g.At("base.png"));
        var nrmPng = WritePng(g.At("nrm.png"));
        // both submeshes resolve to the SAME (base, normal) — the "unknown mapping" smear
        var perSubmesh = new (string?, string?, string?)[] { (basePng, nrmPng, null), (basePng, nrmPng, null) };
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, basePng, nrmPng, perSubmesh);

        var model = ModelRoot.Load(path);
        // one DISTINCT preview material per submesh, so Blender shows one slot per submesh (boundary visible)
        Assert.Equal(2, model.LogicalMaterials.Where(m => m.Name?.StartsWith("gf2_submesh") == true).Count());
        // ...but the shared base+normal embed once each (2 images), not once per submesh (would be 4)
        Assert.Equal(2, model.LogicalImages.Count);
    }

    /// <summary>The preview material carries base colour, normal and the ORM pair and NOTHING else. The
    /// emissive slot in particular stays empty: an image there renders as light in Blender's Material
    /// Preview, which reads as a part that glows.</summary>
    [Fact]
    public void ThePreviewMaterial_FillsNoSlotBeyondBaseColorNormalAndTheOrmPair()
    {
        using var g = new TempGame();
        var path = g.At("slots.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, WritePng(g.At("base.png")), WritePng(g.At("nrm.png")),
            new (string?, string?, string?)[] { (null, null, WritePng(g.At("rmo.png"))), default });

        var mat = ModelRoot.Load(path).LogicalMaterials.First(m => m.Name == "gf2_submesh0");
        Assert.NotNull(mat.FindChannel("Occlusion")?.Texture);
        Assert.NotNull(mat.FindChannel("MetallicRoughness")?.Texture);
        Assert.Null(mat.FindChannel("Emissive")?.Texture);
    }

    /// <summary>An image plugged into the emissive slot reaches no slot of the incoming set: the RMO rides
    /// the ORM pair, and no other channel's semantics match what the game's maps hold.</summary>
    [Fact]
    public void AnEmissiveImageInAReturnedGlb_ResolvesNoMap()
    {
        using var g = new TempGame();
        var path = g.At("emissive.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, WritePng(g.At("base.png")), WritePng(g.At("nrm.png")));

        // plug an image into the emissive slot of the written glb, as a Blender session would
        var model = ModelRoot.Load(path);
        var plugged = model.UseImageWithContent(System.IO.File.ReadAllBytes(WritePng(g.At("glow.png"))));
        model.LogicalMaterials[0].FindChannel("Emissive")?.SetTexture(0, plugged);
        model.SaveGLB(path);

        var maps = MeshGltf.ReadSubmeshMaps(path);
        Assert.NotEmpty(maps);
        // base colour and normal still resolve; the emissive image reaches nothing, and no RMO was embedded
        Assert.All(maps, m => Assert.Equal(MapOrigin.Vanilla, m.BaseColor.Origin));
        Assert.All(maps, m => Assert.Equal(MapOrigin.Vanilla, m.Normal.Origin));
        Assert.All(maps, m => Assert.Equal(MapOrigin.None, m.Rmo.Origin));
    }

    // ---- the ORM pair -------------------------------------------------------------------------------

    /// <summary>The RMO ships as ONE image on both halves of the ORM pair — the shape a stock glTF importer
    /// rebuilds as two texture nodes over one image datablock — with its channels in glTF's order.</summary>
    [Fact]
    public void TheRmo_FillsBothOrmChannelsFromOneImage()
    {
        using var g = new TempGame();
        var path = g.At("orm.glb");
        var rmo = WritePng(g.At("rmo.png"), new Rgba32(10, 20, 30, 40));   // roughness, metallic, occlusion, mask
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path,
            perSubmesh: new (string?, string?, string?)[] { (null, null, rmo), default });

        var mat = ModelRoot.Load(path).LogicalMaterials.First(m => m.Name == "gf2_submesh0");
        var mrImage = mat.FindChannel("MetallicRoughness")?.Texture?.PrimaryImage;
        Assert.NotNull(mrImage);
        Assert.Same(mrImage, mat.FindChannel("Occlusion")?.Texture?.PrimaryImage);
        // glTF ORM order: R occlusion, G roughness, B metallic; alpha rides untouched
        Assert.Equal(new Rgba32(30, 10, 20, 40), FirstPixel(mrImage!.Content.Content.ToArray()));
    }

    /// <summary>glTF multiplies each factor by its texture channel, so a material carrying an RMO must not
    /// keep the matte stand-in factors — a zero metallic factor would zero the map. A material with no RMO
    /// keeps them.</summary>
    [Fact]
    public void TheMatteFactors_AreKeptOnlyWhereNoRmoEmbeds()
    {
        using var g = new TempGame();
        var path = g.At("factors.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (null, null, WritePng(g.At("rmo.png"))),
            (WritePng(g.At("base.png")), null, null),
        });

        var mats = ModelRoot.Load(path).LogicalMaterials;
        var withRmo = mats.First(m => m.Name == "gf2_submesh0").FindChannel("MetallicRoughness")!.Value;
        var without = mats.First(m => m.Name == "gf2_submesh1").FindChannel("MetallicRoughness")!.Value;
        Assert.Equal(1f, withRmo.GetFactor("MetallicFactor"));
        Assert.Equal(1f, withRmo.GetFactor("RoughnessFactor"));
        Assert.Equal(0f, without.GetFactor("MetallicFactor"));
        Assert.Equal(1f, without.GetFactor("RoughnessFactor"));
    }

    /// <summary>Blender rebuilds the pair as two texture nodes, so a modder can plug a file into one and
    /// leave the other on the stock image. The metallic-roughness half decides the slot.</summary>
    [Fact]
    public void WhenTheOrmHalvesDisagree_TheMetallicRoughnessImageWins()
    {
        using var g = new TempGame();
        var path = g.At("disagree.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path,
            perSubmesh: new (string?, string?, string?)[] { (null, null, WritePng(g.At("rmo.png"))), default });

        var painted = WritePng(g.At("painted.png"), new Rgba32(1, 2, 3, 4));
        var model = ModelRoot.Load(path);
        var mat = model.LogicalMaterials.First(m => m.Name == "gf2_submesh0");
        mat.FindChannel("MetallicRoughness")?.SetTexture(0,
            model.UseImageWithContent(System.IO.File.ReadAllBytes(painted)));
        model.SaveGLB(path);

        var slot = MeshGltf.ReadSubmeshMaps(path)[0].Rmo;
        Assert.Equal(MapOrigin.Authored, slot.Origin);
        // the painted image, permuted back out of glTF order
        Assert.Equal(new Rgba32(2, 3, 1, 4), FirstPixel(slot.AuthoredPng!));
    }

    /// <summary>The occlusion half is read only where the metallic-roughness one carries nothing — a
    /// material a hand edit, or an importer that dropped one node, left half-filled.</summary>
    [Fact]
    public void WithNoMetallicRoughnessImage_TheOcclusionImageIsRead()
    {
        using var g = new TempGame();
        var path = g.At("occlusion_only.glb");
        var rmo = WritePng(g.At("rmo.png"));
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path,
            perSubmesh: new (string?, string?, string?)[] { (null, null, rmo), default });

        // rebind primitive 0 to a material carrying the ORM image on occlusion ALONE
        var model = ModelRoot.Load(path);
        var orm = model.LogicalMaterials.First(m => m.Name == "gf2_submesh0")
            .FindChannel("Occlusion")!.Value.Texture!.PrimaryImage!;
        var half = model.CreateMaterial("occlusion_only");
        half.WithPBRMetallicRoughness();
        half.FindChannel("Occlusion")?.SetTexture(0, orm);
        model.LogicalMeshes[0].Primitives[0].Material = half;
        model.SaveGLB(path);

        var slot = MeshGltf.ReadSubmeshMaps(path)[0].Rmo;
        Assert.Equal(MapOrigin.Vanilla, slot.Origin);
        Assert.Equal(System.IO.Path.GetFullPath(rmo), slot.StockPng);
    }

    [Fact]
    public void ExportWithoutTextures_HasNoMaterial()
    {
        using var g = new TempGame();
        var path = g.At("plain.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path);   // the geometry-only path is unchanged
        Assert.Empty(ModelRoot.Load(path).LogicalMaterials);
    }

}
