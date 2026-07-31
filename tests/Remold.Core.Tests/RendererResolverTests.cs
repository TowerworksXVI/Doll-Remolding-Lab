using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Materials;
using Remold.Core.Model;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The renderer-first tier end to end over a synthetic corpus: prefab bundle → CAB → material bundle
/// (m_TexEnvs) → local texture. The slot join is TOKEN-based because variant names INFIX the LOD
/// (c_X_slg_cloth1_lod0_Fight = part cloth1_Fight), and stem-sibling prefabs own parts the stem prefab
/// doesn't carry.
/// </summary>
public class RendererResolverTests
{
    /// <summary>A catalog whose formula row resolves the stem's prefab and whose dep closure carries the
    /// subject's other bundles.</summary>
    private static CatalogIndex Catalog(string stem, string prefabLogical, params string[] closure)
    {
        var address = GameVfs.PrefabAddress("Character/Player", stem);
        return CatalogIndex.ForTest(
            new[] { (address, prefabLogical) },
            new[] { (address, new[] { prefabLogical }.Concat(closure).ToArray()) });
    }

    private static PrefabSlot Slot(string name, long pathId = 1) =>
        new(name, pathId, new[] { new PrefabMaterialRef(21, "CAB-x") }, Mesh: null);

    private static CharacterPrefab Prefab(params PrefabSlot[] slots) =>
        new("Testy", System.Array.Empty<PrefabRecipeEntry>(), slots,
            System.Array.Empty<string>(), HasReplaceableModel: false);

    [Fact]
    public void FindSlot_ExactLod0_WinsOverTokenMatch()
    {
        var p = Prefab(Slot("c_T_slg_cloth1_lod0_Fight"), Slot("c_T_slg_cloth1_lod0", pathId: 2));
        Assert.Equal(2, RendererResolver.FindSlot(p, "c_T_slg_", "cloth1")!.PathId);
    }

    [Fact]
    public void FindSlot_VariantPart_MatchesTheInfixedLodSlotByToken()
    {
        // the corpus shape: the LOD token is INFIXED before the variant, so prefix+part+"_lod0" never matches
        var p = Prefab(Slot("c_T_slg_cloth1_lod0"), Slot("c_T_slg_cloth1_lod0_Fight", pathId: 7));
        Assert.Equal(7, RendererResolver.FindSlot(p, "c_T_slg_", "cloth1_Fight")!.PathId);
    }

    [Fact]
    public void FindSlot_TokenMatch_PrefersTheLod0Slot()
    {
        var p = Prefab(Slot("c_T_slg_body_lod1_Dorm", pathId: 3), Slot("c_T_slg_body_lod0_Dorm", pathId: 4));
        Assert.Equal(4, RendererResolver.FindSlot(p, "c_T_slg_", "body_Dorm")!.PathId);
    }

    [Fact]
    public void FindSlot_BareToken_DoesNotClaimAVariantsInfixedLodSlot()
    {
        // The lod0 tier ships ONLY under the Fight name, so the bare token owns no lod0 of its own.
        // Reconstruction must stay ANCHORED: unanchored, 'cloth2' claims the Fight slot and binds another
        // garment.
        var p = Prefab(Slot("c_T_slg_cloth2_lod0_Fight", pathId: 5), Slot("c_T_slg_cloth2_lod1", pathId: 6));
        Assert.Equal(6, RendererResolver.FindSlot(p, "c_T_slg_", "cloth2")!.PathId);        // its own tier, not Fight's
        Assert.Equal(5, RendererResolver.FindSlot(p, "c_T_slg_", "cloth2_Fight")!.PathId);   // the variant still binds
    }

    [Fact]
    public void FindSlot_MidTierSpelling_StillReconstructs()
    {
        // the anchor accepts the whole LOD vocabulary (lod0/lodm0/lod1), just nothing AFTER the digits
        var p = Prefab(Slot("c_T_slg_hair_lodm0", pathId: 8));
        Assert.Equal(8, RendererResolver.FindSlot(p, "c_T_slg_", "hair")!.PathId);
    }

    [Fact]
    public void FindSlot_UnknownPart_IsNull()
    {
        var p = Prefab(Slot("c_T_slg_cloth1_lod0"));
        Assert.Null(RendererResolver.FindSlot(p, "c_T_slg_", "hair"));
    }
    [Fact]
    public void Resolve_BindsTexturesThroughThePrefabRendererChain()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        const string prefabLogical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1.bundle";
        const string matLogical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2.bundle";

        // prefab: slot c_TestySSR01_slg_cloth_lod0 with ONE external material (fileId 1 → CAB-mat1, pathId 21)
        SyntheticBundle.BuildPrefab(Path.Combine(abw, new string('1', 32) + ".bundle"),
            prefabLogical, rootName: "TestySSR01", slotName: "c_TestySSR01_slg_cloth_lod0",
            recipe: new[] { ("c_TestySSR01_slg_cloth_lod0", "Assets/X/c_TestySSR01_slg_cloth_lod0.mesh") },
            slotMaterials: new[] { (1, 21L) },
            externalCabs: new[] { "CAB-mat1" });

        // dependency bundle carrying that material (pathId 21) + its local texture (pathId 2)
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            matLogical, materialName: "c_TestySSR01_slg_cloth_uber", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) },
            externalCabs: System.Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)),
            cabName: "CAB-mat1");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);

        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var reader = new BundleReader();
        var scope = SubjectScope.Build(Catalog("TestySSR01", prefabLogical, matLogical),
            deobfuscate, outfit);
        var result = PartTextureResolver.Resolve(
            scope, reader, deobfuscate, outfit, "cloth", submeshCount: 1);

        var tex = Assert.Single(result.All);
        Assert.Equal("c_TestySSR01_slg_cloth_d", tex.Name);
        Assert.Equal(matLogical, tex.Bundle);       // pinned to the exact dependency bundle, not by name
        Assert.True(tex.IsBaseColor);
        Assert.Equal("renderer", tex.Source);
        Assert.Equal("c_TestySSR01_slg_cloth_d", result.Submeshes[0].BaseColor);
    }

    [Fact]
    public void Resolve_VariantPart_BindsThroughTheInfixedLodSlot()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        const string prefabLogical = "ccccccccccccccccccccccccccccccc1.bundle";
        const string matLogical = "ccccccccccccccccccccccccccccccc2.bundle";

        // the corpus variant shape: LOD infixed, variant token after it
        SyntheticBundle.BuildPrefab(Path.Combine(abw, new string('4', 32) + ".bundle"),
            prefabLogical, rootName: "TestySSR01", slotName: "c_TestySSR01_slg_cloth1_lod0_Fight",
            recipe: new[] { ("c_TestySSR01_slg_cloth1_lod0_Fight", "Assets/X/c_TestySSR01_slg_cloth1_lod0_Fight.mesh") },
            slotMaterials: new[] { (1, 21L) },
            externalCabs: new[] { "CAB-matF" });
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('5', 32) + ".bundle"),
            matLogical, materialName: "c_TestySSR01_slg_cloth1_Fight_uber", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) },
            externalCabs: System.Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth1_Fight_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0x11, 0x22, 0x33, 0xFF)),
            cabName: "CAB-matF");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var scope = SubjectScope.Build(Catalog("TestySSR01", prefabLogical, matLogical),
            deobfuscate, outfit);
        var result = PartTextureResolver.Resolve(
            scope, new BundleReader(), deobfuscate, outfit, "cloth1_Fight", submeshCount: 1);

        var tex = Assert.Single(result.All);
        Assert.Equal("c_TestySSR01_slg_cloth1_Fight_d", tex.Name);
        Assert.Equal("renderer", tex.Source);
    }

    [Fact]
    public void Resolve_PartOwnedByASiblingSkinModelPrefab_Binds()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        const string prefabLogical = "ddddddddddddddddddddddddddddddd1.bundle";
        const string matLogical = "ddddddddddddddddddddddddddddddd2.bundle";

        // NO root named "TestySSR01" anywhere: the part lives only in the stem-sibling skin_model prefab
        SyntheticBundle.BuildPrefab(Path.Combine(abw, new string('6', 32) + ".bundle"),
            prefabLogical, rootName: "c_TestySSR01_slg_skin_model", slotName: "c_TestySSR01_slg_bomb_lod0",
            recipe: new[] { ("c_TestySSR01_slg_bomb_lod0", "Assets/X/c_TestySSR01_slg_bomb_lod0.mesh") },
            slotMaterials: new[] { (1, 21L) },
            externalCabs: new[] { "CAB-matS" });
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('7', 32) + ".bundle"),
            matLogical, materialName: "c_TestySSR01_slg_bomb_uber", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) },
            externalCabs: System.Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_bomb_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0x44, 0x55, 0x66, 0xFF)),
            cabName: "CAB-matS");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);

        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        // The sibling rides in through the dependency closure, so the scope finds its recipe root even
        // though no bundle carries a root named exactly "TestySSR01".
        var scope = SubjectScope.Build(Catalog("TestySSR01", prefabLogical, matLogical),
            deobfuscate, outfit);
        var result = PartTextureResolver.Resolve(
            scope, new BundleReader(), deobfuscate, outfit, "bomb", submeshCount: 1);

        var tex = Assert.Single(result.All);
        Assert.Equal("c_TestySSR01_slg_bomb_d", tex.Name);
        Assert.Equal("renderer", tex.Source);
    }

    [Fact]
    public void Resolve_MixedPart_ResolvedSlotBinds_FailedSlotFlagged_PlaceholderIsNotAFailure()
    {
        // THREE materials in order: resolved, an empty 0:0 placeholder, and a FAILED reference. The
        // resolved slot's maps survive and the partial failure is flagged so the caller can surface the
        // untextured submesh — while the deliberately-empty placeholder is NOT a failure.
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        const string prefabLogical = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1.bundle";
        const string matLogical = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2.bundle";

        // m_Materials = [ (fileId1→CAB-mat1, 21) resolved, (0:0) placeholder, (fileId2→CAB-absent, 99) FAILED ]
        SyntheticBundle.BuildPrefab(Path.Combine(abw, new string('8', 32) + ".bundle"),
            prefabLogical, rootName: "TestySSR01", slotName: "c_TestySSR01_slg_cloth_lod0",
            recipe: new[] { ("c_TestySSR01_slg_cloth_lod0", "Assets/X/c_TestySSR01_slg_cloth_lod0.mesh") },
            slotMaterials: new[] { (1, 21L), (0, 0L), (2, 99L) },
            externalCabs: new[] { "CAB-mat1", "CAB-absent" });   // CAB-absent is referenced but no bundle provides it
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('9', 32) + ".bundle"),
            matLogical, materialName: "c_TestySSR01_slg_cloth_uber", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: System.Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)),
            cabName: "CAB-mat1");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var scope = SubjectScope.Build(Catalog("TestySSR01", prefabLogical, matLogical),
            deobfuscate, outfit);
        var result = PartTextureResolver.Resolve(
            scope, new BundleReader(), deobfuscate, outfit, "cloth", submeshCount: 3);

        var tex = Assert.Single(result.All);                 // the resolved slot's texture survives intact
        Assert.Equal("c_TestySSR01_slg_cloth_d", tex.Name);
        Assert.True(result.HasFailedMaterial);               // the failed reference is surfaced as a partial miss
        Assert.Equal(3, result.Submeshes.Count);             // slot order preserved across all three materials
        Assert.Equal("c_TestySSR01_slg_cloth_d", result.Submeshes[0].BaseColor);   // resolved slot's map
        Assert.Null(result.Submeshes[1].BaseColor);          // placeholder — empty, not a failure
        Assert.Null(result.Submeshes[2].BaseColor);          // failed slot — untextured
    }

    [Fact]
    public void Resolve_FallsThroughWhenTheOutfitHasNoPrefab()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        // a mesh-only corpus: no prefab, no materials → nothing for the resolver to bind
        SyntheticBundle.BuildOneMesh(Path.Combine(abw, new string('3', 32) + ".bundle"),
            "c_Lonely_slg_cloth_lod0", new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 },
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1.bundle");

        var deobfuscate = FixtureCrawl.DeobfuscateOver(abw);
        var outfit = new Outfit(1, "Lonely", OutfitKind.Base);
        // no formula hit for the stem → an empty scope → the tier resolves nothing
        var scope = SubjectScope.Build(CatalogIndex.ForTest(System.Array.Empty<(string, string)>()),
            deobfuscate, outfit);
        var result = PartTextureResolver.Resolve(
            scope, new BundleReader(), deobfuscate, outfit, "cloth", submeshCount: 1);

        Assert.DoesNotContain(result.All, t => t.Source == "renderer");   // tier stayed silent, no throw
    }
}
