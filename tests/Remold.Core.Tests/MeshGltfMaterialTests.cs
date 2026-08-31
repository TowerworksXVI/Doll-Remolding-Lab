using System.IO;
using System.Linq;
using System.Text.Json;
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

    // ---- material identity, and what the pictures are allowed to decide ------------------------------

    /// <summary>A submesh whose maps are all absent still gets its own named material. The material IS the
    /// submesh boundary, and the boundary belongs to the geometry: dropping it collapsed such a submesh onto
    /// whatever material the writer fell back to, and a send back then re-split the part onto one output
    /// position.</summary>
    [Fact]
    public void ASubmeshWithNoMapsKeepsItsOwnMaterial()
    {
        using var g = new TempGame();
        var path = g.At("half_textured.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (WritePng(g.At("base.png")), null, null),
            default,                                   // no map resolved for this one
        });

        var model = ModelRoot.Load(path);
        var prims = model.LogicalMeshes.Single().Primitives;
        Assert.Equal(new[] { "gf2_submesh0", "gf2_submesh1" }, prims.Select(p => p.Material?.Name).ToArray());
        Assert.Single(model.LogicalImages);
        Assert.Null(prims[1].Material!.FindChannel("BaseColor")?.Texture);
    }

    /// <summary>A base colour carrying a real cutout declares MASK at half, so a hair card or a lace panel
    /// shows its shape in Blender with the depth buffer intact. Route: ExportGlb → BuildSubmeshMaterials →
    /// BuildPreviewMaterial. Here 30 of the map's 100 pixels are cut away — 30%, far past the tenth of a
    /// percent that separates a cutout from compression noise.</summary>
    [Fact]
    public void ABaseColourWithACutout_DeclaresMaskAtHalf()
    {
        using var g = new TempGame();
        var path = g.At("cutout.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (WriteCutoutPng(g.At("lace.png"), cutPixels: 30), null, null),
            default,
        });

        var material = ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0");
        Assert.Equal(AlphaMode.MASK, material.Alpha);
        Assert.Equal(0.5f, material.AlphaCutoff);
    }

    /// <summary>A binary opening may carry one-pixel antialiasing bands on either edge. Those mid-alpha
    /// pixels have no 5x5 interior, so they do not turn the cutout into a blended material.</summary>
    [Fact]
    public void AThinAntialiasedBinaryCutout_DeclaresMask()
    {
        using var g = new TempGame();
        var path = g.At("antialiased_cutout.glb");
        var png = WriteAlphaPng(g.At("antialiased_cutout.png"), 100, 100, (x, _) => x switch
        {
            48 or 52 => 192,
            49 or 51 => 64,
            50 => 0,
            _ => 255,
        });
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path,
            perSubmesh: new (string?, string?, string?)[] { (png, null, null), default });

        Assert.Equal(AlphaMode.MASK,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>A large graded region crosses half opacity and retains a substantial mid-alpha interior
    /// after the 5x5 erosion. BLEND wins before the below-half cutout rule can call it MASK.</summary>
    [Fact]
    public void AnAreaFormingGradeCrossingHalf_DeclaresBlend()
    {
        using var g = new TempGame();
        var path = g.At("graded.glb");
        var png = WriteAreaGradePng(g.At("graded.png"));
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path,
            perSubmesh: new (string?, string?, string?)[] { (png, null, null), default });

        Assert.Equal(AlphaMode.BLEND,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>A veil whose entire alpha range stays at or above the MASK cutoff still has an area-forming
    /// mid-alpha core, so it declares BLEND rather than disappearing into OPAQUE.</summary>
    [Fact]
    public void AHighAlphaVeil_DeclaresBlend()
    {
        using var g = new TempGame();
        var path = g.At("veil.glb");
        var png = WriteAlphaPng(g.At("veil.png"), 64, 64,
            (x, _) => (byte)(128 + x * 126 / 63));
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path,
            perSubmesh: new (string?, string?, string?)[] { (png, null, null), default });

        Assert.Equal(AlphaMode.BLEND,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>A 5x14 mid-alpha rectangle leaves exactly ten core pixels after a two-pixel erosion. On a
    /// 100x100 image that is exactly 0.1%, pinning the BLEND comparison as inclusive.</summary>
    [Fact]
    public void ExactlyOneTenthPercentMidAlphaCore_DeclaresBlend()
    {
        using var g = new TempGame();
        var path = g.At("blend_boundary.glb");
        var png = WriteAlphaPng(g.At("blend_boundary.png"), 100, 100, (x, y) =>
            x is >= 10 and < 15 && y is >= 10 and < 24 ? (byte)192 : (byte)255);
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path,
            perSubmesh: new (string?, string?, string?)[] { (png, null, null), default });

        Assert.Equal(AlphaMode.BLEND,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>A four-pixel soft boundary is deliberately one pixel narrower than the erosion kernel. Its
    /// mid-alpha band erodes away completely while its transparent side still declares MASK.</summary>
    [Fact]
    public void ASoftEdgeTooThinForTheFiveByFiveCore_StaysMask()
    {
        using var g = new TempGame();
        var path = g.At("thin_soft_edge.glb");
        var png = WriteAlphaPng(g.At("thin_soft_edge.png"), 100, 100, (x, _) =>
            x < 24 ? (byte)0 : x < 26 ? (byte)64 : x < 28 ? (byte)192 : (byte)255);
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path,
            perSubmesh: new (string?, string?, string?)[] { (png, null, null), default });

        Assert.Equal(AlphaMode.MASK,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>The production return reader recognizes an untouched BLEND-classified synthetic image after
    /// a GLB load/save cycle. This pins the sidecar/content identity route without committing game pixels or
    /// requiring Blender inside the .NET suite.</summary>
    [Fact]
    public void ABlendClassifiedSyntheticGlb_ReturnsUntouchedThroughReadSubmeshMaps()
    {
        using var g = new TempGame();
        var record = g.At("graded_record.glb");
        var returned = g.At("graded_return.glb");
        var png = WriteAreaGradePng(g.At("graded.png"));
        MeshGltf.ExportGlb(TwoSubmeshPatch(), record,
            perSubmesh: new (string?, string?, string?)[] { (png, null, null), default });
        Assert.Equal(AlphaMode.BLEND,
            ModelRoot.Load(record).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);

        ModelRoot.Load(record).SaveGLB(returned);
        var incoming = MeshGltf.ReadSubmeshMaps(returned, "patch", record);
        Assert.Equal(MapOrigin.Vanilla,
            incoming.Single(m => m.MaterialName == "gf2_submesh0").BaseColor.Origin);
    }

    /// <summary>The bridge's alpha remap recognizes the graph Blender's glTF importer builds only when
    /// vertex colour is absent and the material alpha factor is one. Pin both writer invariants on the same
    /// synthetic BLEND glb that needs the remap.</summary>
    [Fact]
    public void ABlendPreviewGlb_HasNoColorZero_AndAUnitBaseColorFactorAlpha()
    {
        using var g = new TempGame();
        var path = g.At("blend_import_shape.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path,
            perSubmesh: new (string?, string?, string?)[]
            {
                (WriteAreaGradePng(g.At("graded.png")), null, null),
                default,
            });

        using var json = ReadGlbJson(path);
        foreach (var mesh in json.RootElement.GetProperty("meshes").EnumerateArray())
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
                Assert.False(primitive.GetProperty("attributes").TryGetProperty("COLOR_0", out _));

        foreach (var material in json.RootElement.GetProperty("materials").EnumerateArray())
        {
            var pbr = material.GetProperty("pbrMetallicRoughness");
            float alpha = pbr.TryGetProperty("baseColorFactor", out var factor)
                ? factor[3].GetSingle()
                : 1f; // glTF's omitted default
            Assert.Equal(1f, alpha);
        }
    }

    /// <summary>The game's own textures are BC-compressed and decode with whole uniform blocks of alpha 254.
    /// That is not coverage, and a map made entirely of it stays OPAQUE — read as transparency it put every
    /// solid surface of every character behind a blend, showing the modder its own backfaces.
    ///
    /// <para>The BELOW half of the threshold pin: a thousand pixels of that noise and not one of them under
    /// half opacity. The map is the same size as the one in
    /// <see cref="ABaseColourWithOnePixelInAThousand_DeclaresMask"/>, where a single cut pixel is enough — so
    /// the pair brackets the tenth of a percent exactly, and a threshold of zero would fail here.</para>
    /// </summary>
    [Fact]
    public void ABaseColourOfCompressionNoiseAlone_StaysOpaque()
    {
        using var g = new TempGame();
        var path = g.At("quantized.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (WriteThousandPixelPng(g.At("noisy.png"), cutPixels: 0, new Rgba32(200, 150, 100, 254)), null, null),
            default,
        });

        Assert.Equal(AlphaMode.OPAQUE,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>The ABOVE half of the threshold pin, at the boundary itself: one pixel in a thousand is a
    /// tenth of a percent, exactly what the rule asks for, and it declares MASK. The asymmetry the margin is
    /// set by: a cutout missed here renders the part as a solid sheet, while a map wrongly called MASK only
    /// loses pixels the viewer already draws at under half opacity.</summary>
    [Fact]
    public void ABaseColourWithOnePixelInAThousand_DeclaresMask()
    {
        using var g = new TempGame();
        var path = g.At("one_in_a_thousand.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (WriteThousandPixelPng(g.At("pinhole.png"), cutPixels: 1, new Rgba32(200, 150, 100, 255)), null, null),
            default,
        });

        Assert.Equal(AlphaMode.MASK,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>The case the threshold was lowered for: a cutout SHAPE that is small against the sheet it
    /// sits on. A 50×50 lace region half cut away on a 512×512 atlas is 0.48% of the pixels — the same share
    /// as the 200×200-on-2048² panel this stands in for — which reads as a shape at a tenth of a percent and
    /// read as compression noise at one percent, where the panel showed the modder a solid sheet.</summary>
    [Fact]
    public void ALaceRegionSmallAgainstItsAtlas_DeclaresMask()
    {
        using var g = new TempGame();
        var path = g.At("atlas.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (WriteAtlasWithLaceRegion(g.At("atlas.png"), atlasSize: 512, regionSize: 50), null, null),
            default,
        });

        Assert.Equal(AlphaMode.MASK,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>A genuine binary, hard-edged cutout at a size where the 5x5 classifier is active remains
    /// MASK. The measured cut share and the declared cutoff agree on its alpha-0 and alpha-255 regions.</summary>
    [Fact]
    public void AHardEdgedBinaryCutout_UsesTheMaskCutoff()
    {
        using var g = new TempGame();
        var cutout = WriteCutoutPng(g.At("binary_cutout.png"), cutPixels: 30);
        PreviewMaps.ToPreview(cutout, MapKind.BaseColor, out var cutShare);
        Assert.Equal(0.30, cutShare, 12);

        var path = g.At("boundary.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (cutout, null, null),
            default,
        });

        var material = ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0");
        Assert.Equal(AlphaMode.MASK, material.Alpha);
        Assert.True(255 / 255f >= material.AlphaCutoff);
        Assert.True(0 / 255f < material.AlphaCutoff);
    }

    /// <summary>The 5x5 erosion has no legal center on an image smaller than five pixels in either
    /// dimension. Uniform mid alpha therefore cannot declare BLEND there and falls through to MASK when it
    /// is below half. This is a stated classifier limit, not a cutout fixture.</summary>
    [Fact]
    public void AMapTooSmallForTheErosionKernel_CannotDeclareBlend()
    {
        using var g = new TempGame();
        var path = g.At("sub_kernel.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (WritePng(g.At("four_by_four_mid.png"), new Rgba32(200, 150, 100, 100)), null, null),
            default,
        });

        Assert.Equal(AlphaMode.MASK,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>A fully opaque base colour stays OPAQUE.</summary>
    [Fact]
    public void AnOpaqueBaseColour_StaysOpaque()
    {
        using var g = new TempGame();
        var path = g.At("opaque.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (WritePng(g.At("solid.png"), new Rgba32(200, 150, 100, 255)), null, null),
            default,
        });

        Assert.Equal(AlphaMode.OPAQUE,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>A base colour with no alpha channel at all stays OPAQUE — the decision must not depend on the
    /// alpha the decoder invents for a map that carries none.</summary>
    [Fact]
    public void ABaseColourWithNoAlphaChannel_StaysOpaque()
    {
        using var g = new TempGame();
        var path = g.At("no_alpha.glb");
        var png = g.At("rgb.png");
        using (var img = new Image<Rgb24>(4, 4, new Rgb24(200, 150, 100))) img.SaveAsPng(png);
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (png, null, null),
            default,
        });

        Assert.Equal(AlphaMode.OPAQUE,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>Only the base colour decides it. An RMO's alpha is the emissive mask and a packed normal's is
    /// the X component — here both are entirely under half, and reading either as coverage would put a cutout
    /// through most of a character.</summary>
    [Fact]
    public void AnRmoOrNormalAlpha_NeverDecidesTheMode()
    {
        using var g = new TempGame();
        var path = g.At("mask.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), path, perSubmesh: new (string?, string?, string?)[]
        {
            (WritePng(g.At("solid.png"), new Rgba32(200, 150, 100, 255)),
             WritePng(g.At("packed_n.png"), new Rgba32(255, 128, 128, 30)),
             WritePng(g.At("emissive_r.png"), new Rgba32(60, 80, 100, 0))),
            default,
        });

        Assert.Equal(AlphaMode.OPAQUE,
            ModelRoot.Load(path).LogicalMaterials.Single(m => m.Name == "gf2_submesh0").Alpha);
    }

    /// <summary>A 10×10 opaque map with exactly <paramref name="cutPixels"/> of its 100 pixels cut to alpha 0
    /// — a cutout's shape, stated by construction so the share is the test's own arithmetic.</summary>
    private static string WriteCutoutPng(string path, int cutPixels)
    {
        using var img = new Image<Rgba32>(10, 10, new Rgba32(200, 150, 100, 255));
        for (int i = 0; i < cutPixels; i++) img[i % 10, i / 10] = new Rgba32(200, 150, 100, 0);
        img.SaveAsPng(path);
        return path;
    }

    /// <summary>A 40×25 map — one thousand pixels, so its share reads directly as pixels-per-thousand —
    /// filled with <paramref name="fill"/> and with exactly <paramref name="cutPixels"/> of them cut to
    /// alpha 0. The denominator is the test's own arithmetic, not the code's.</summary>
    private static string WriteThousandPixelPng(string path, int cutPixels, Rgba32 fill)
    {
        using var img = new Image<Rgba32>(40, 25, fill);
        for (int i = 0; i < cutPixels; i++) img[i % 40, i / 40] = new Rgba32(fill.R, fill.G, fill.B, 0);
        img.SaveAsPng(path);
        return path;
    }

    /// <summary>An opaque <paramref name="atlasSize"/>² sheet carrying one
    /// <paramref name="regionSize"/>² region whose every other pixel is cut away — a lace panel's holes on a
    /// texture sheet that is mostly other things. Half the region is cut, so the share is
    /// regionSize² / (2 × atlasSize²) by construction.</summary>
    private static string WriteAtlasWithLaceRegion(string path, int atlasSize, int regionSize)
    {
        using var img = new Image<Rgba32>(atlasSize, atlasSize, new Rgba32(200, 150, 100, 255));
        for (int y = 0; y < regionSize; y++)
            for (int x = 0; x < regionSize; x++)
                if ((x + y) % 2 == 0) img[x, y] = new Rgba32(200, 150, 100, 0);
        img.SaveAsPng(path);
        return path;
    }

    /// <summary>A synthetic base colour whose alpha is supplied explicitly by the fixture.</summary>
    private static string WriteAlphaPng(string path, int width, int height,
        System.Func<int, int, byte> alphaAt)
    {
        using var img = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                img[x, y] = new Rgba32(200, 150, 100, alphaAt(x, y));
        img.SaveAsPng(path);
        return path;
    }

    /// <summary>A 40x40 area on a 100x100 opaque sheet, graded from alpha 32 through 224.</summary>
    private static string WriteAreaGradePng(string path) =>
        WriteAlphaPng(path, 100, 100, (x, y) => x is >= 20 and < 60 && y is >= 20 and < 60
            ? (byte)(32 + (x - 20) * 192 / 39)
            : (byte)255);

    private static JsonDocument ReadGlbJson(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32()); // glTF
        Assert.Equal(2u, reader.ReadUInt32());
        _ = reader.ReadUInt32();
        while (stream.Position < stream.Length)
        {
            uint length = reader.ReadUInt32();
            uint type = reader.ReadUInt32();
            var content = reader.ReadBytes(checked((int)length));
            if (type == 0x4E4F534Au) return JsonDocument.Parse(content); // JSON
        }
        throw new InvalidDataException("GLB has no JSON chunk.");
    }
}
