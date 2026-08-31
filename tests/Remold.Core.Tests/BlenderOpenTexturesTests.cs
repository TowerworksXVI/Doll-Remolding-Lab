using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.App.ViewModels;
using Remold.Core;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using SharpGLTF.Schema2;
using Image = SixLabors.ImageSharp.Image;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// What a Blender open puts on disk before it writes a glb. The run's <c>textures/</c> folder is the only
/// place a preview material's images come from, so an open that leaves it empty hands the modder a part
/// with no pictures AND — because a texture-less submesh used to get no material either — with its submesh
/// boundaries collapsed onto one slot, which the send back then re-splits onto one output position.
///
/// <para>Every fixture here is synthetic: a hand-authored prefab binding two materials, one texture each,
/// over a two-submesh skinned mesh — the multi-material shape the collapse showed up on.</para>
/// </summary>
public class BlenderOpenTexturesTests
{
    private const string Stem = "TestySSR01";
    private const string Character = "Testy";
    private const string Slot = "c_TestySSR01_slg_cloth_lod0";
    private const string BodySlot = "c_TestySSR01_slg_body_lod0";
    private const string Address = "Assets/X/c_TestySSR01_slg_cloth_lod0.mesh";
    private const string BodyAddress = "Assets/X/c_TestySSR01_slg_body_lod0.mesh";
    private const string PrefabLogical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1.bundle";
    private const string Mat1Logical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2.bundle";
    private const string Mat2Logical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa3.bundle";
    private const string MeshLogical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa4.bundle";
    private const string Mat3Logical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa5.bundle";
    private const string BodyMeshLogical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa6.bundle";
    private const byte Mat1Seed = 0x21;
    private const byte Mat2Seed = 0x22;
    /// <summary>Where <see cref="SyntheticBundle.BuildOneMaterial"/> puts a material's own first texture, and
    /// therefore the path id the cache keys that picture by. The second lands beside it.</summary>
    private const long LocalTex = 2, SecondLocalTex = 3;

    private static Outfit TheOutfit => new(1071, Stem, OutfitKind.Base);   // mesh prefix c_TestySSR01_slg_
    private static string Subj => ModNaming.SubjectSlug(Character, Stem);

    /// <summary>The content identity a fixture bundle's manifest stub states — sixteen bytes of its seed,
    /// which is the key the stock-texture cache addresses that bundle's pictures by.</summary>
    private static string ContentIdOf(byte seed) => string.Concat(Enumerable.Repeat(seed.ToString("x2"), 16));

    /// <summary>A subject of two parts. <c>cloth</c> has two submeshes and a renderer binding two materials,
    /// one base-colour texture each — the multi-material shape the material collapse showed up on; <c>body</c>
    /// is a plain one-material part, there so the combined session has two parts to put on one armature.
    /// <paramref name="mat1Alpha"/> gives cloth's first texture a uniform alpha, while
    /// <paramref name="mat1Cutout"/> makes it an 8x8 hard-edged binary cutout, which is what the glTF alpha
    /// mode has to answer for; <paramref name="breakMat2Texture"/> ships cloth's second texture with a
    /// pixel blob its declared size cannot be read out of, which is what an unreadable map looks like from
    /// here; <paramref name="twoSameNamedInMat1"/> ships cloth's first material's bundle with TWO textures of
    /// that name and pins the second on the renderer, which is what a ramp library's shape looks like from
    /// here.</summary>
    private static GameVfs Fixture(TempGame g, byte mat1Alpha = 255, bool mat1Cutout = false,
        bool breakMat2Texture = false, byte mat1Seed = Mat1Seed, bool twoSameNamedInMat1 = false)
    {
        int mat1Size = mat1Cutout ? 8 : 4;
        byte[] mat1Pixels = mat1Cutout
            ? HardEdgedCutoutRgba32(mat1Size, mat1Size, 0xAA, 0x22, 0x22)
            : SyntheticBundle.SolidRgba32(mat1Size, mat1Size, 0xAA, 0x22, 0x22, mat1Alpha);
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        WorkbenchPrefab.Build(Path.Combine(abw, new string('1', 32) + ".bundle"),
            bundleName: PrefabLogical, rootName: Stem,
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec(Slot, new[] { (1, 21L), (2, 31L) }),
                new WorkbenchPrefab.SlotSpec(BodySlot, new[] { (3, 41L) }),
            },
            recipe: new[] { (Slot, Address), (BodySlot, BodyAddress) },
            externalCabs: new[] { "CAB-mat1", "CAB-mat2", "CAB-mat3" });
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            Mat1Logical, materialName: "cloth_a_uber", materialPathId: 21,
            // the renderer pins the SECOND same-named texture when there are two
            texEnvs: new[] { ("_BaseMap", 0, twoSameNamedInMat1 ? SecondLocalTex : LocalTex) },
            externalCabs: Array.Empty<string>(),
            localTextures: twoSameNamedInMat1
                ? new[]
                {
                    new SyntheticBundle.TextureSpec("cloth_a_d", 4, 4,
                        SyntheticBundle.SolidRgba32(4, 4, 0x01, 0x01, 0x01, 255)),
                    new SyntheticBundle.TextureSpec("cloth_a_d", mat1Size, mat1Size, mat1Pixels),
                }
                : null,
            localTexture: new SyntheticBundle.TextureSpec("cloth_a_d", mat1Size, mat1Size, mat1Pixels),
            cabName: "CAB-mat1");
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('3', 32) + ".bundle"),
            Mat2Logical, materialName: "cloth_b_uber", materialPathId: 31,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("cloth_b_d", 4, 4,
                breakMat2Texture ? new byte[4] : SyntheticBundle.SolidRgba32(4, 4, 0x22, 0x22, 0xBB, 255)),
            cabName: "CAB-mat2");
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('5', 32) + ".bundle"),
            Mat3Logical, materialName: "body_uber", materialPathId: 41,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("body_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0x30, 0x60, 0x90, 255)),
            cabName: "CAB-mat3");
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('4', 32) + ".bundle"), Slot,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0, 2, 0, 0, 2, 1, 0 },
            new[] { 0, 1, 2, 3, 4, 5 }, new uint[] { 0x1111_1111 }, bundleName: MeshLogical,
            submeshIndexCounts: new[] { 3, 3 });
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('6', 32) + ".bundle"), BodySlot,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, new uint[] { 0x1111_1111 },
            bundleName: BodyMeshLogical);

        var prefabAddress = GameVfs.PrefabAddress("Character/Player", Stem);
        return TestVfs.CreateWith(g.Root,
            new[] { (Address, MeshLogical), (BodyAddress, BodyMeshLogical), (prefabAddress, PrefabLogical) },
            new[] { (prefabAddress, new[] { PrefabLogical, Mat1Logical, Mat2Logical, Mat3Logical }) },
            new TestVfs.Bundle(PrefabLogical, new string('1', 32), 0x11, true),
            new TestVfs.Bundle(Mat1Logical, new string('2', 32), mat1Seed, true),
            new TestVfs.Bundle(Mat2Logical, new string('3', 32), Mat2Seed, true),
            new TestVfs.Bundle(MeshLogical, new string('4', 32), 0x24, true),
            new TestVfs.Bundle(Mat3Logical, new string('5', 32), 0x25, true),
            new TestVfs.Bundle(BodyMeshLogical, new string('6', 32), 0x26, true));
    }

    /// <summary>A binary sheet split vertically between alpha 0 and 255. Both regions are wider than
    /// the classifier's erosion radius, so it is genuinely a cutout and not a sub-kernel fixture accident.</summary>
    private static byte[] HardEdgedCutoutRgba32(int width, int height, byte r, byte g, byte b)
    {
        var pixels = SyntheticBundle.SolidRgba32(width, height, r, g, b, 255);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width / 2; x++)
                pixels[(y * width + x) * 4 + 3] = 0;
        return pixels;
    }

    /// <summary>The lone open: cloth alone, written to its own rigged glb.</summary>
    private static IReadOnlyList<string> Open(TempGame g, GameVfs vfs, string glbOut, string texDir,
        string? cacheRoot = null, ICollection<string>? unreadable = null) =>
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, TheOutfit, Character,
            new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
            {
                ("cloth", MeshLogical, Slot, glbOut, null, 0L, null),
            },
            texDir, stockTextureCacheRoot: cacheRoot, unreadableTextures: unreadable);

    /// <summary>The several-parts open: both parts on one armature, into one composition glb.</summary>
    private static void OpenAll(TempGame g, GameVfs vfs, string combinedOut, string texDir,
        string? cacheRoot = null,
        IReadOnlyDictionary<string, IReadOnlyList<(string?, string?, string?)>>? authored = null,
        ICollection<string>? unreadable = null) =>
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, TheOutfit, Character,
            new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
            {
                ("cloth", MeshLogical, Slot, null, null, 0L, null),
                ("body", BodyMeshLogical, BodySlot, null, null, 0L, null),
            },
            texDir, combinedOut: combinedOut, stockTextureCacheRoot: cacheRoot, authoredMaps: authored,
            unreadableTextures: unreadable);

    private static string ScopedName(string bundle, string texture) =>
        TextureExport.BundleScopedName(bundle, texture, Subj);

    private static Rgba32 FirstPixel(string path)
    {
        using var img = Image.Load<Rgba32>(path);
        return img[0, 0];
    }

    // ---- the run folder ------------------------------------------------------------------------------

    /// <summary>The whole defect in one assertion: an open puts every renderer-bound map in its run folder
    /// and the glb embeds them, one material per submesh. An empty folder gave a part with no images and one
    /// material for the lot.</summary>
    [Fact]
    public void AnOpen_PutsEveryRendererBoundMapInTheRunFolder_AndEmbedsThemPerSubmesh()
    {
        using var g = new TempGame();
        var vfs = Fixture(g);
        var texDir = g.At(Path.Combine("run", "textures"));
        var glb = g.At(Path.Combine("run", "parts", "cloth.glb"));

        Assert.Equal(new[] { "cloth" }, Open(g, vfs, glb, texDir, g.At("stocktex")).ToArray());

        Assert.True(File.Exists(Path.Combine(texDir, ScopedName(Mat1Logical, "cloth_a_d"))));
        Assert.True(File.Exists(Path.Combine(texDir, ScopedName(Mat2Logical, "cloth_b_d"))));
        var model = ModelRoot.Load(glb);
        Assert.Equal(new[] { MeshGltf.SubmeshMaterialName(0), MeshGltf.SubmeshMaterialName(1) },
            model.LogicalMeshes.Single().Primitives.Select(p => p.Material?.Name).ToArray());
        Assert.Equal(2, model.LogicalImages.Count);   // both materials' pictures, distinct
    }

    /// <summary>The pictures are kept between runs under the owning bundle's stated content identity, so a
    /// second open links them instead of decoding the game again. Proved by putting a DIFFERENT picture in the
    /// cache under the key the open would ask for: the open must hand back that one.</summary>
    [Fact]
    public void ASecondOpen_TakesItsPicturesFromTheCache()
    {
        using var g = new TempGame();
        var vfs = Fixture(g);
        var cacheRoot = g.At("stocktex");
        Open(g, vfs, g.At(Path.Combine("run1", "cloth.glb")), g.At(Path.Combine("run1", "textures")), cacheRoot);
        // the first open published both; overwrite one entry so a cache read is distinguishable from a decode
        var cache = new StockTextureCache(cacheRoot);
        Assert.NotNull(cache.TryGet(ContentIdOf(Mat1Seed), "cloth_a_d", LocalTex));
        // a decoded texture's bytes are BGRA, so these land in the PNG as (3, 2, 1)
        cache.Publish(new BundleReader.DecodedTexture(SyntheticBundle.SolidRgba32(4, 4, 1, 2, 3, 255), 4, 4,
            "RGBA32"), ContentIdOf(Mat1Seed), "cloth_a_d", LocalTex);

        var texDir = g.At(Path.Combine("run2", "textures"));
        Open(g, vfs, g.At(Path.Combine("run2", "cloth.glb")), texDir, cacheRoot);

        using var placed = Image.Load<Rgba32>(Path.Combine(texDir, ScopedName(Mat1Logical, "cloth_a_d")));
        Assert.Equal(new Rgba32(3, 2, 1, 255), placed[0, 0]);
    }

    /// <summary>A game update rewrites a bundle, and the manifest then states different content for it. The
    /// open asks a key nothing answers and decodes the new bytes, rather than serving the picture the old
    /// ones made. Nothing is invalidated or swept for it: a key that means different bytes IS a different
    /// key.</summary>
    [Fact]
    public void AnUpdatedBundle_IsNotServedThePictureItsOldContentMade()
    {
        using var g = new TempGame();
        var cacheRoot = g.At("stocktex");
        // a picture in the cache under the bundle's CURRENT content identity — proof the open reads the cache
        new StockTextureCache(cacheRoot).Publish(
            new BundleReader.DecodedTexture(SyntheticBundle.SolidRgba32(4, 4, 1, 2, 3, 255), 4, 4, "RGBA32"),
            ContentIdOf(Mat1Seed), "cloth_a_d", LocalTex);
        var before = g.At(Path.Combine("before", "textures"));
        Open(g, Fixture(g), g.At(Path.Combine("before", "cloth.glb")), before, cacheRoot);
        Assert.Equal(new Rgba32(3, 2, 1, 255),
            FirstPixel(Path.Combine(before, ScopedName(Mat1Logical, "cloth_a_d"))));

        // the same install after an update: the manifest states other content for that very bundle
        using var h = new TempGame();
        var after = h.At(Path.Combine("after", "textures"));
        Open(h, Fixture(h, mat1Seed: 0x31), h.At(Path.Combine("after", "cloth.glb")), after, cacheRoot);

        Assert.Equal(new Rgba32(0xAA, 0x22, 0x22, 255),
            FirstPixel(Path.Combine(after, ScopedName(Mat1Logical, "cloth_a_d"))));
    }

    /// <summary>An open with no cache at all still populates its run folder — the cache is a saving, never
    /// the source. This is also the shape a test run and a locked-down profile take.</summary>
    [Fact]
    public void AnOpenWithNoCache_StillPopulatesItsRunFolder()
    {
        using var g = new TempGame();
        var vfs = Fixture(g);
        var texDir = g.At(Path.Combine("run", "textures"));

        Open(g, vfs, g.At(Path.Combine("run", "cloth.glb")), texDir, cacheRoot: null);

        Assert.True(File.Exists(Path.Combine(texDir, ScopedName(Mat1Logical, "cloth_a_d"))));
        Assert.True(File.Exists(Path.Combine(texDir, ScopedName(Mat2Logical, "cloth_b_d"))));
    }

    /// <summary>A map that cannot be read names its texture to the caller, so the open says once how many
    /// went missing instead of flashing a line per texture. Here the second material's bundle is gone from
    /// the install.</summary>
    [Fact]
    public void AMapThatCannotBeRead_NamesItsTextureToTheCaller()
    {
        using var g = new TempGame();
        var vfs = Fixture(g, breakMat2Texture: true);
        var texDir = g.At(Path.Combine("run", "textures"));
        var missed = new List<string>();

        Open(g, vfs, g.At(Path.Combine("run", "cloth.glb")), texDir, g.At("stocktex"), missed);

        Assert.Equal(new[] { "cloth_b_d" }, missed.ToArray());
        Assert.True(File.Exists(Path.Combine(texDir, ScopedName(Mat1Logical, "cloth_a_d"))));   // the readable one landed
    }

    /// <summary>…and the submesh whose map went missing keeps its OWN material. The boundary is the
    /// geometry's, not the picture's: collapsing it is what made a whole part re-split onto one output
    /// position.</summary>
    [Fact]
    public void AMissingMap_CostsItsSubmeshThePictureAndNotTheMaterial()
    {
        using var g = new TempGame();
        var vfs = Fixture(g, breakMat2Texture: true);
        var glb = g.At(Path.Combine("run", "cloth.glb"));

        Open(g, vfs, glb, g.At(Path.Combine("run", "textures")), g.At("stocktex"));

        var model = ModelRoot.Load(glb);
        var prims = model.LogicalMeshes.Single().Primitives;
        Assert.Equal(new[] { MeshGltf.SubmeshMaterialName(0), MeshGltf.SubmeshMaterialName(1) },
            prims.Select(p => p.Material?.Name).ToArray());
        Assert.Single(model.LogicalImages);                                   // only the readable map embedded
        Assert.Null(prims[1].Material!.FindChannel("BaseColor")?.Texture);     // and its submesh shows none
    }

    /// <summary>A game map that is a genuine cutout reaches the glb as a MASK material, so its shape shows in
    /// Blender — and its opaque neighbour is left alone.</summary>
    [Fact]
    public void AnOpen_DeclaresMaskOnlyForTheMapThatIsACutout()
    {
        using var g = new TempGame();
        var vfs = Fixture(g, mat1Cutout: true);
        var glb = g.At(Path.Combine("run", "cloth.glb"));

        Open(g, vfs, glb, g.At(Path.Combine("run", "textures")), g.At("stocktex"));

        var materials = ModelRoot.Load(glb).LogicalMaterials;
        Assert.Equal(AlphaMode.MASK,
            materials.Single(m => m.Name == MeshGltf.SubmeshMaterialName(0)).Alpha);
        Assert.Equal(AlphaMode.OPAQUE,
            materials.Single(m => m.Name == MeshGltf.SubmeshMaterialName(1)).Alpha);
    }

    /// <summary>The shipped game data's own case, end to end: a diffuse whose alpha is 254 rather than 255 —
    /// BC compression's quantization, not coverage — opens OPAQUE. Every material in the game read as
    /// translucent, and a translucent double-sided material shows the modder the inside of the part.</summary>
    [Fact]
    public void AnOpen_LeavesAQuantizedGameMapOpaque()
    {
        using var g = new TempGame();
        var vfs = Fixture(g, mat1Alpha: 254);
        var glb = g.At(Path.Combine("run", "cloth.glb"));

        Open(g, vfs, glb, g.At(Path.Combine("run", "textures")), g.At("stocktex"));

        Assert.All(ModelRoot.Load(glb).LogicalMaterials, m => Assert.Equal(AlphaMode.OPAQUE, m.Alpha));
    }

    /// <summary>A bundle can ship several Texture2Ds under one name, and only the path id says which one a
    /// material bound. The open reads the one the renderer PINNED — into the run folder and into the durable
    /// cache alike. Selecting on the name took whichever came first, so the wrong picture opened in Blender
    /// and, worse, became this texture's cached answer for every later open.</summary>
    [Fact]
    public void TwoSameNamedTextures_ExportAndCacheThePathIdTheRendererPinned()
    {
        using var g = new TempGame();
        var vfs = Fixture(g, twoSameNamedInMat1: true);
        var texDir = g.At(Path.Combine("run", "textures"));
        var cacheRoot = g.At("stocktex");

        Open(g, vfs, g.At(Path.Combine("run", "cloth.glb")), texDir, cacheRoot);

        // the pinned texture's own pixels, not the first same-named one's (0x01 grey)
        var pinned = new Rgba32(0xAA, 0x22, 0x22, 255);
        Assert.Equal(pinned, FirstPixel(Path.Combine(texDir, ScopedName(Mat1Logical, "cloth_a_d"))));
        var cache = new StockTextureCache(cacheRoot);
        Assert.Null(cache.TryGet(ContentIdOf(Mat1Seed), "cloth_a_d", LocalTex));   // no entry under the other one
        Assert.Equal(pinned, FirstPixel(cache.TryGet(ContentIdOf(Mat1Seed), "cloth_a_d", SecondLocalTex)!));
    }

    // ---- a cached picture that turns out not to be one -------------------------------------------------

    /// <summary>Damage a published cache entry INSIDE its PNG envelope: the signature and the IEND are intact,
    /// so every check the cache can afford says the file is whole, and it is served. The open that meets it
    /// has to survive — the part opens, that texture is named to the caller, and the entry is dropped so the
    /// NEXT open exports the map from the game again. Left standing, one damaged entry failed every later open
    /// of every subject binding that texture.</summary>
    [Fact]
    public void ACacheEntryDamagedInsideItsPngEnvelope_CostsOneMapAndHealsOnTheNextOpen()
    {
        using var g = new TempGame();
        var vfs = Fixture(g);
        var cacheRoot = g.At("stocktex");
        Open(g, vfs, g.At(Path.Combine("run1", "cloth.glb")), g.At(Path.Combine("run1", "textures")), cacheRoot);
        var entry = new StockTextureCache(cacheRoot).TryGet(ContentIdOf(Mat1Seed), "cloth_a_d", LocalTex)!;
        CorruptInsideThePng(entry);

        // the open that is served the damaged entry
        var missed = new List<string>();
        var glb = g.At(Path.Combine("run2", "cloth.glb"));
        var texDir = g.At(Path.Combine("run2", "textures"));
        Open(g, vfs, glb, texDir, cacheRoot, missed);

        Assert.Equal(new[] { "cloth_a_d" }, missed.ToArray());
        var model = ModelRoot.Load(glb);            // the part opened, and its submesh boundaries survived
        var prims = model.LogicalMeshes.Single().Primitives;
        Assert.Equal(new[] { MeshGltf.SubmeshMaterialName(0), MeshGltf.SubmeshMaterialName(1) },
            prims.Select(p => p.Material?.Name).ToArray());
        Assert.Null(prims[0].Material!.FindChannel("BaseColor")?.Texture);
        Assert.NotNull(prims[1].Material!.FindChannel("BaseColor")?.Texture);   // the sound map is untouched
        Assert.False(File.Exists(entry));                                       // and the entry is gone

        // the next open exports the map afresh and shows it again
        var thirdTex = g.At(Path.Combine("run3", "textures"));
        var again = new List<string>();
        Open(g, vfs, g.At(Path.Combine("run3", "cloth.glb")), thirdTex, cacheRoot, again);

        Assert.Empty(again);
        Assert.Equal(new Rgba32(0xAA, 0x22, 0x22, 255),
            FirstPixel(Path.Combine(thirdTex, ScopedName(Mat1Logical, "cloth_a_d"))));
    }

    /// <summary>The same on the several-parts route, which writes every part into ONE file: one damaged
    /// picture must not take the whole session down, and nothing raw may reach the modder. The combined glb is
    /// written, both parts are in it, and the damaged map costs its own material position alone.</summary>
    [Fact]
    public void ACacheEntryDamagedInsideItsPngEnvelope_CostsTheCombinedSessionOneMaterialsPicture()
    {
        using var g = new TempGame();
        var vfs = Fixture(g);
        var cacheRoot = g.At("stocktex");
        OpenAll(g, vfs, g.At(Path.Combine("run1", "composition.glb")), g.At(Path.Combine("run1", "textures")),
            cacheRoot);
        var entry = new StockTextureCache(cacheRoot).TryGet(ContentIdOf(Mat1Seed), "cloth_a_d", LocalTex)!;
        CorruptInsideThePng(entry);

        var combined = g.At(Path.Combine("run2", "composition.glb"));
        OpenAll(g, vfs, combined, g.At(Path.Combine("run2", "textures")), cacheRoot);

        var model = ModelRoot.Load(combined);
        Assert.Equal(2, model.LogicalMeshes.Count);          // the session survived the bad picture
        var cloth = model.LogicalMeshes.Single(m => m.Name == Slot).Primitives;
        Assert.Null(cloth[0].Material!.FindChannel("BaseColor")?.Texture);
        Assert.NotNull(cloth[1].Material!.FindChannel("BaseColor")?.Texture);
        Assert.False(File.Exists(entry));

        var third = g.At(Path.Combine("run3", "composition.glb"));
        OpenAll(g, vfs, third, g.At(Path.Combine("run3", "textures")), cacheRoot);
        Assert.NotNull(ModelRoot.Load(third).LogicalMeshes.Single(m => m.Name == Slot)
            .Primitives[0].Material!.FindChannel("BaseColor")?.Texture);
    }

    /// <summary>A map of the MODDER's own that will not decode costs its position the same picture — and
    /// nothing else. It is their file, not a copy of the game's: it is named to the caller so the open says
    /// so, and it is left exactly where it is, since deleting a modder's work over a failed read would be the
    /// worse failure by far.</summary>
    [Fact]
    public void AnUnreadableMapOfTheModdersOwn_IsNamedAndLeftWhereItIs()
    {
        using var g = new TempGame();
        var vfs = Fixture(g);
        var painted = g.At(Path.Combine("project", "painted_d.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(painted)!);
        File.WriteAllText(painted, "not a picture");
        var combined = g.At(Path.Combine("run", "composition.glb"));
        var missed = new List<string>();

        OpenAll(g, vfs, combined, g.At(Path.Combine("run", "textures")), g.At("stocktex"),
            authored: new Dictionary<string, IReadOnlyList<(string?, string?, string?)>>(StringComparer.OrdinalIgnoreCase)
            {
                ["cloth"] = new (string?, string?, string?)[] { (painted, null, null), default },
            },
            unreadable: missed);

        Assert.Equal(new[] { "painted_d" }, missed.ToArray());
        Assert.True(File.Exists(painted));                       // the modder's file is untouched
        var cloth = ModelRoot.Load(combined).LogicalMeshes.Single(m => m.Name == Slot).Primitives;
        Assert.Null(cloth[0].Material!.FindChannel("BaseColor")?.Texture);
        Assert.NotNull(cloth[1].Material!.FindChannel("BaseColor")?.Texture);
    }

    /// <summary>XOR the bytes between the header and the IEND chunk. The 8-byte signature and the 12-byte
    /// trailer stay exactly as they were, so the file still reads as a whole PNG to anything short of a
    /// decode — which is the case that got served forever.</summary>
    private static void CorruptInsideThePng(string path)
    {
        var bytes = File.ReadAllBytes(path);
        for (int i = 20; i < bytes.Length - 12; i++) bytes[i] ^= 0x5A;
        File.WriteAllBytes(path, bytes);
    }

    // ---- the combined route --------------------------------------------------------------------------

    /// <summary>The several-parts session embeds the modder's OWN maps where they have them, exactly as the
    /// lone route's re-export does. Without it an open-all showed the game texture under work the modder had
    /// already sent back. What each material position SHOWS is the whole claim: the combined glb's own record
    /// classifies nothing, because a send back is read against the part's own prepared glb.</summary>
    [Fact]
    public void TheCombinedRoute_EmbedsTheModdersOwnMapOverTheStockOne()
    {
        using var g = new TempGame();
        var vfs = Fixture(g);
        var painted = g.At(Path.Combine("project", "painted_d.png"));
        Directory.CreateDirectory(Path.GetDirectoryName(painted)!);
        using (var img = new Image<Rgba32>(4, 4, new Rgba32(7, 8, 9, 255))) img.SaveAsPng(painted);
        var combined = g.At(Path.Combine("run", "composition.glb"));
        var texDir = g.At(Path.Combine("run", "textures"));

        OpenAll(g, vfs, combined, texDir, g.At("stocktex"),
            authored: new Dictionary<string, IReadOnlyList<(string?, string?, string?)>>(StringComparer.OrdinalIgnoreCase)
            {
                ["cloth"] = new (string?, string?, string?)[] { (painted, null, null), default },
            });

        var incoming = MeshGltf.ReadSubmeshMaps(combined, Slot, combined);
        // submesh 0 shows the modder's painted file, not the game map underneath it
        Assert.Equal(Path.GetFullPath(painted), incoming[0].BaseColor.StockPng);
        // submesh 1 was never painted, so it stays on the game's own map
        Assert.Equal(Path.Combine(texDir, ScopedName(Mat2Logical, "cloth_b_d")),
            incoming[1].BaseColor.StockPng);
    }

    /// <summary>The control: with no authored maps threaded through, the same combined build embeds the game's
    /// maps in both positions.</summary>
    [Fact]
    public void TheCombinedRoute_WithNothingAuthored_EmbedsTheGamesOwnMaps()
    {
        using var g = new TempGame();
        var vfs = Fixture(g);
        var combined = g.At(Path.Combine("run", "composition.glb"));
        var texDir = g.At(Path.Combine("run", "textures"));

        OpenAll(g, vfs, combined, texDir, g.At("stocktex"));

        var incoming = MeshGltf.ReadSubmeshMaps(combined, Slot, combined);
        Assert.Equal(Path.Combine(texDir, ScopedName(Mat1Logical, "cloth_a_d")),
            incoming[0].BaseColor.StockPng);
        Assert.Equal(Path.Combine(texDir, ScopedName(Mat2Logical, "cloth_b_d")),
            incoming[1].BaseColor.StockPng);
    }

    // ---- what a populated folder makes possible downstream --------------------------------------------

    /// <summary>The open now records which stock RMO each submesh was built over, which is where an authored
    /// RMO's emissive mask comes from on the way back. With the folder empty there was no record at all and
    /// every freshly painted RMO shipped with a zero mask, silently.</summary>
    [Fact]
    public void AnOpenRecordsTheStockRmo_SoAnAuthoredOneKeepsTheGamesMask()
    {
        using var g = new TempGame();
        var vfs = FixtureWithRmo(g, maskAlpha: 77);
        var glb = g.At(Path.Combine("run", "cloth.glb"));
        Open(g, vfs, glb, g.At(Path.Combine("run", "textures")), g.At("stocktex"));

        var sources = PreviewMaps.ReadSubmeshRmoSources(glb, Slot);
        Assert.True(File.Exists(Assert.Contains(0, sources)));

        // what a session that repainted submesh 0's RMO hands back
        var rows = BlenderMaterialReturn.Normalize(new List<IncomingMaps>
        {
            new(new ResolvedMap(MapOrigin.None), new ResolvedMap(MapOrigin.None),
                new ResolvedMap(MapOrigin.Authored, AuthoredPng: FlatPng(new Rgba32(10, 20, 30, 255)))),
        }, g.At("return"), submesh => sources.GetValueOrDefault(submesh));

        using var shipped = Image.Load<Rgba32>(Assert.Single(rows).Rmo!);
        Assert.Equal(new Rgba32(10, 20, 30, 77), shipped[0, 0]);   // the game's mask, not a zero one
    }

    [Fact]
    public void Authored_map_overlays_use_replacement_submeshes_not_installed_material_positions()
    {
        using var g = new TempGame();
        string outputZero = Asset("output-zero.png", 10);
        string outputOne = Asset("output-one.png", 20);
        string outputTwo = Asset("output-two.png", 30);
        string gameZero = Asset("game-zero.png", 40);
        var states = new[]
        {
            State("game-0", TargetSlotDomain.Game, null, 0, TargetInputKind.BaseColor, gameZero),
            // Deliberately crossed: installed material positions say 1,0 while replacement primitives say 0,1.
            State("output-0", TargetSlotDomain.EditOutput, 0, 1, TargetInputKind.BaseColor, outputZero),
            State("output-1", TargetSlotDomain.EditOutput, 1, 0, TargetInputKind.BaseColor, outputOne),
            // The replacement has a third primitive the installed two-material mesh does not.
            State("output-2", TargetSlotDomain.EditOutput, 2, 0, TargetInputKind.Normal, outputTwo,
                "_BumpMap"),
        };

        var maps = MainWindowViewModel.SessionAuthoredMaps(states, g.Root)!;
        Assert.Equal(3, maps.Count);
        Assert.Equal(Path.GetFullPath(outputZero), maps[0].Base);
        Assert.Equal(Path.GetFullPath(outputOne), maps[1].Base);
        Assert.Equal(Path.GetFullPath(outputTwo), maps[2].Normal);
        Assert.NotEqual(Path.GetFullPath(gameZero), maps[0].Base); // edit output owns that replacement slot

        var textures = MainWindowViewModel.SessionAuthoredTextures(states, g.Root)!;
        Assert.Contains(textures, row => row.MaterialIndex == 0 && row.Png == Path.GetFullPath(outputZero));
        Assert.Contains(textures, row => row.MaterialIndex == 1 && row.Png == Path.GetFullPath(outputOne));
        Assert.Contains(textures, row => row.MaterialIndex == 2 && row.Png == Path.GetFullPath(outputTwo));

        string Asset(string name, byte value)
        {
            string path = g.At(Path.Combine("assets", name));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new[] { value });
            return path;
        }

        EditSlotState State(string id, TargetSlotDomain domain, int? submesh, int? material,
            TargetInputKind input, string file, string? property = null) => new(
            new TargetSlot
            {
                Id = id,
                Part = AuthoredParts.Part(Character, Stem, Slot),
                Domain = domain,
                SubmeshIndex = submesh,
                MaterialSlotIndex = material,
                Input = input,
                ShaderProperty = property,
            },
            new Binding { SlotId = id, Kind = BindingKind.ProjectAsset, ProjectAssetId = "asset-" + id },
            new ProjectAsset
            {
                Id = "asset-" + id,
                Kind = ProjectAssetKind.Picture,
                Label = id,
                File = Path.GetRelativePath(g.Root, file),
            });
    }

    private static byte[] FlatPng(Rgba32 color)
    {
        using var img = new Image<Rgba32>(4, 4, color);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>The same subject with an RMO on the first material, whose alpha carries the emissive
    /// mask.</summary>
    private static GameVfs FixtureWithRmo(TempGame g, byte maskAlpha)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildPrefab(Path.Combine(abw, new string('1', 32) + ".bundle"),
            PrefabLogical, rootName: Stem, slotName: Slot,
            recipe: new[] { (Slot, Address) }, slotMaterials: new[] { (1, 21L) },
            externalCabs: new[] { "CAB-mat1" });
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            Mat1Logical, materialName: "cloth_a_uber", materialPathId: 21,
            texEnvs: new[] { ("_RMOTex", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("cloth_a_r", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0x40, 0x50, 0x60, maskAlpha)),
            cabName: "CAB-mat1");
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, new string('4', 32) + ".bundle"), Slot,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, new uint[] { 0x1111_1111 },
            bundleName: MeshLogical);

        var prefabAddress = GameVfs.PrefabAddress("Character/Player", Stem);
        return TestVfs.CreateWith(g.Root,
            new[] { (Address, MeshLogical), (prefabAddress, PrefabLogical) },
            new[] { (prefabAddress, new[] { PrefabLogical, Mat1Logical }) },
            new TestVfs.Bundle(PrefabLogical, new string('1', 32), 0x11, true),
            new TestVfs.Bundle(Mat1Logical, new string('2', 32), Mat1Seed, true),
            new TestVfs.Bundle(MeshLogical, new string('4', 32), 0x24, true));
    }
}
