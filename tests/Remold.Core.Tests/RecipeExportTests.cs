using System;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The recipe-exact materialize route: the mesh is read by the recipe slot name in the CATALOG-pinned
/// bundle, NEVER re-derived from prefix+token, and every broken link (recipe → address → bundle → mesh)
/// fails LOUDLY with a named reason. Synthetic bundles resolved through the real forward mechanics.
/// </summary>
public class RecipeExportTests
{
    // One plain mesh bundle in g's game dir, plus a GameVfs resolving it through the given catalog rows.
    private static GameVfs MeshVfs(TempGame g, string meshName, string logical,
        (string Address, string OwnerBundle)[] rows)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        // the physical filename is arbitrary; the logical id is the embedded AssetBundle m_Name
        var phys = new string('a', 32);
        SyntheticBundle.BuildOneMesh(Path.Combine(abw, phys + ".bundle"), meshName,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, bundleName: logical);
        return TestVfs.Create(g.Root, rows, null, (logical, phys));
    }

    private static SubjectScope Scope(GameVfs vfs, Outfit outfit) =>
        SubjectScope.Build(vfs.Catalog, vfs.TryDeobfuscateLogical, outfit);

    private static Outfit Alt => new(0, "VeliraSSR0101", OutfitKind.Base);   // MeshPrefix c_VeliraSSR0101_slg_

    private static RecipePart Recipe(string slot, string address) =>
        new(MeshName.Part(slot, "c_VeliraSSR0101_slg_") is { Length: > 0 } t ? t : slot, slot, address,
            Array.Empty<RecipeTierSlot>());

    [Fact]
    public void HappyPath_OwnPrefixPart_ReadsByCatalogPinnedBundle()
    {
        const string slot = "c_VeliraSSR0101_slg_cloth1_lod0";
        const string logical = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1.bundle";
        const string address = "Assets/X/c_VeliraSSR0101_slg_cloth1_lod0.mesh";
        using var g = new TempGame();
        var vfs = MeshVfs(g, slot, logical, new[] { (address, logical) });
        var root = g.Root;
        var outDir = Path.Combine(root, "mod", "velira_VeliraSSR0101");

        var report = AssetExporter.ExportRecipePart(root, vfs, Scope(vfs, Alt), Alt, "Velira", Recipe(slot, address), outDir, sharedRoot: Path.Combine(root, "mod"));

        var mesh = Assert.Single(report.Files, f => f.Kind == "mesh");
        Assert.True(mesh.Ok, mesh.Note);
        Assert.Equal(slot, mesh.AssetName);           // read by the EXACT slot name
        Assert.Equal(logical, mesh.Bundle);           // from the catalog-pinned bundle
        Assert.Contains("cloth1", report.CompletedParts);
    }

    [Fact]
    public void RendererBindsNoTextures_ExportsMeshAndSurfacesLoudMiss()
    {
        // A scope with no assembly prefab renderer to bind textures: the mesh must still export, so mesh
        // editing stays possible, but the texture miss surfaces LOUDLY rather than ship untextured.
        const string slot = "c_VeliraSSR0101_slg_cloth1_lod0";
        const string logical = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa9.bundle";
        const string address = "Assets/X/c_VeliraSSR0101_slg_cloth1_lod0.mesh";
        using var g = new TempGame();
        var vfs = MeshVfs(g, slot, logical, new[] { (address, logical) });
        var root = g.Root;
        var outDir = Path.Combine(root, "mod", "velira_VeliraSSR0101");

        var report = AssetExporter.ExportRecipePart(root, vfs, Scope(vfs, Alt), Alt, "Velira", Recipe(slot, address), outDir, sharedRoot: Path.Combine(root, "mod"));

        var mesh = Assert.Single(report.Files, f => f.Kind == "mesh");
        Assert.True(mesh.Ok, mesh.Note);                         // the mesh committed — editing stays possible
        Assert.Contains("cloth1", report.CompletedParts);
        var miss = Assert.Single(report.Files, f => f.Kind == "texture" && !f.Ok);
        Assert.Contains("no textures resolved from its prefab renderer", miss.Note);
    }

    [Fact]
    public void MaterializePart_ZeroTextures_ReturnsCreatedWithWarning()
    {
        // Same over the fused Materializer path: the mesh materializes, but the bound-nothing miss rides out
        // as MaterializeResult.Warning rather than a silent untextured Created.
        const string slot = "c_VeliraSSR0101_slg_cloth1_lod0";
        const string logical = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1.bundle";
        const string address = "Assets/X/c_VeliraSSR0101_slg_cloth1_lod0.mesh";
        using var g = new TempGame();
        var vfs = MeshVfs(g, slot, logical, new[] { (address, logical) });
        var root = g.Root;
        var proj = new ModProject { RootDir = Path.Combine(root, "mod") };

        var r = Materializer.MaterializePart(proj, vfs, root, Alt, "Velira", proj.RootDir, Recipe(slot, address));

        Assert.Equal(MaterializeOutcome.Created, r.Outcome);
        Assert.Contains(r.Targets, t => t.AssetType == "Mesh");   // the mesh landed — editing stays possible
        Assert.NotNull(r.Warning);
        Assert.Contains("no textures resolved from its prefab renderer", r.Warning);
    }

    [Fact]
    public void MaterializePart_RendererBindsTextures_WarningNull()
    {
        // the healthy shape: a renderer slot binding a resolvable textured material materializes with NO
        // warning
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        const string prefabLogical = "ffffffffffffffffffffffffffffff1.bundle";
        const string matLogical    = "ffffffffffffffffffffffffffffff2.bundle";
        const string meshLogical   = "ffffffffffffffffffffffffffffff3.bundle";
        const string slot = "c_TestySSR01_slg_cloth_lod0";
        const string address = "Assets/X/c_TestySSR01_slg_cloth_lod0.mesh";

        SyntheticBundle.BuildPrefab(Path.Combine(abw, new string('1', 32) + ".bundle"),
            prefabLogical, rootName: "TestySSR01", slotName: slot,
            recipe: new[] { (slot, address) }, slotMaterials: new[] { (1, 21L) },
            externalCabs: new[] { "CAB-mat1" });
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            matLogical, materialName: "c_TestySSR01_slg_cloth_uber", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)),
            cabName: "CAB-mat1");
        SyntheticBundle.BuildOneMesh(Path.Combine(abw, new string('3', 32) + ".bundle"),
            slot, new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, bundleName: meshLogical);

        var root = g.Root;
        var prefabAddress = GameVfs.PrefabAddress("Character/Player", "TestySSR01");
        var vfs = TestVfs.Create(root,
            new[] { (address, meshLogical), (prefabAddress, prefabLogical) },
            new[] { (prefabAddress, new[] { prefabLogical, matLogical }) },
            (prefabLogical, new string('1', 32)), (matLogical, new string('2', 32)), (meshLogical, new string('3', 32)));
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);   // mesh prefix c_TestySSR01_slg_
        var recipe = new RecipePart("cloth", slot, address, Array.Empty<RecipeTierSlot>());
        var proj = new ModProject { RootDir = Path.Combine(root, "mod") };

        var r = Materializer.MaterializePart(proj, vfs, root, outfit, "Testy", proj.RootDir, recipe);

        Assert.Equal(MaterializeOutcome.Created, r.Outcome);
        Assert.Null(r.Warning);                                              // renderer bound a texture — no miss
        Assert.Contains(r.Targets, t => t.AssetType == "Mesh");
        Assert.Contains(proj.Targets, t => t.AssetType == "Texture2D");      // the renderer-bound texture materialized
    }

    [Fact]
    public void RecipePart_PartialTextureMiss_MaterializesWithMissFinding_ResolvedTextureStillStaged()
    {
        // TWO materials, one resolving and one failing. The mesh and the resolved texture still stage, but
        // the partial miss surfaces in the prep report so the untextured submesh is never silent.
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        const string prefabLogical = "99999999999999999999999999999991.bundle";
        const string matLogical    = "99999999999999999999999999999992.bundle";
        const string meshLogical   = "99999999999999999999999999999993.bundle";
        const string slot = "c_TestySSR01_slg_cloth_lod0";
        const string address = "Assets/X/c_TestySSR01_slg_cloth_lod0.mesh";

        SyntheticBundle.BuildPrefab(Path.Combine(abw, new string('1', 32) + ".bundle"),
            prefabLogical, rootName: "TestySSR01", slotName: slot,
            recipe: new[] { (slot, address) }, slotMaterials: new[] { (1, 21L), (2, 99L) },   // #2 → CAB-absent, FAILS
            externalCabs: new[] { "CAB-mat1", "CAB-absent" });
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            matLogical, materialName: "c_TestySSR01_slg_cloth_uber", materialPathId: 21,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("c_TestySSR01_slg_cloth_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0xAA, 0x22, 0x22, 0xFF)),
            cabName: "CAB-mat1");
        SyntheticBundle.BuildOneMesh(Path.Combine(abw, new string('3', 32) + ".bundle"),
            slot, new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, bundleName: meshLogical);

        var root = g.Root;
        var prefabAddress = GameVfs.PrefabAddress("Character/Player", "TestySSR01");
        var vfs = TestVfs.Create(root,
            new[] { (address, meshLogical), (prefabAddress, prefabLogical) },
            new[] { (prefabAddress, new[] { prefabLogical, matLogical }) },
            (prefabLogical, new string('1', 32)), (matLogical, new string('2', 32)), (meshLogical, new string('3', 32)));
        var outfit = new Outfit(1071, "TestySSR01", OutfitKind.Base);
        var recipe = new RecipePart("cloth", slot, address, Array.Empty<RecipeTierSlot>());

        // the prep report carries the partial-miss finding directly
        var report = AssetExporter.ExportRecipePart(root, vfs, Scope(vfs, outfit), outfit, "Testy", recipe,
            Path.Combine(root, "mod", "testy_TestySSR01"), sharedRoot: Path.Combine(root, "mod"));
        Assert.True(report.Files.Single(f => f.Kind == "mesh").Ok);                     // mesh committed
        Assert.Contains(report.Files, f => f.Kind == "texture" && f.Ok);               // the resolved texture staged
        var miss = Assert.Single(report.Files, f => f.Kind == "texture" && !f.Ok);
        Assert.Contains("some materials couldn't be resolved", miss.Note);

        // and the fused Materializer carries it out as the Created-with-Warning the shell surfaces
        var proj = new ModProject { RootDir = Path.Combine(root, "mod") };
        var r = Materializer.MaterializePart(proj, vfs, root, outfit, "Testy", proj.RootDir, recipe);
        Assert.Equal(MaterializeOutcome.Created, r.Outcome);
        Assert.Contains(r.Targets, t => t.AssetType == "Mesh");
        Assert.Contains(proj.Targets, t => t.AssetType == "Texture2D");                // the resolved texture still landed
        Assert.NotNull(r.Warning);
        Assert.Contains("some materials couldn't be resolved", r.Warning);
    }

    [Fact]
    public void CrossPrefixPart_BaseFaceOnAltOutfit_Materializes()
    {
        // an ALT outfit reusing the BASE outfit's face mesh — not under the alt's MeshPrefix, so a
        // prefix+token derivation finds nothing
        const string slot = "c_VeliraSSR01_slg_face_lod0";     // base prefix, on the alt subject
        const string logical = "ccccccccccccccccccccccccccccccc1.bundle";
        const string address = "Assets/X/c_VeliraSSR01_slg_face_lod0.mesh";
        using var g = new TempGame();
        var vfs = MeshVfs(g, slot, logical, new[] { (address, logical) });
        var root = g.Root;
        var recipe = new RecipePart("face", slot, address, Array.Empty<RecipeTierSlot>());
        var outDir = Path.Combine(root, "mod", "velira_VeliraSSR0101");

        Assert.False(slot.StartsWith(Alt.MeshPrefix, StringComparison.OrdinalIgnoreCase));   // genuinely cross-prefix
        var report = AssetExporter.ExportRecipePart(root, vfs, Scope(vfs, Alt), Alt, "Velira", recipe, outDir, sharedRoot: Path.Combine(root, "mod"));

        var mesh = Assert.Single(report.Files, f => f.Kind == "mesh");
        Assert.True(mesh.Ok, mesh.Note);
        Assert.Equal(slot, mesh.AssetName);
        Assert.Equal(logical, mesh.Bundle);
    }

    [Fact]
    public void CrossPrefixPart_ThroughMaterializer_LandsAMeshTarget()
    {
        const string slot = "c_VeliraSSR01_slg_face_lod0";
        const string logical = "ddddddddddddddddddddddddddddddd1.bundle";
        const string address = "Assets/X/c_VeliraSSR01_slg_face_lod0.mesh";
        using var g = new TempGame();
        var vfs = MeshVfs(g, slot, logical, new[] { (address, logical) });
        var root = g.Root;
        // "face", not the raw slot name: a slot carrying a FOREIGN prefix is parsed against the prefix it
        // carries itself, which is the token the workbench tree and the recipe both name it by.
        var recipe = new RecipePart("face", slot, address, Array.Empty<RecipeTierSlot>());
        var proj = new ModProject { RootDir = Path.Combine(root, "mod") };

        var r = Materializer.MaterializePart(proj, vfs, root, Alt, "Velira", proj.RootDir, recipe);

        Assert.Equal(MaterializeOutcome.Created, r.Outcome);
        Assert.Contains(r.Targets, t => t.AssetType == "Mesh" && t.ObjectName == slot && t.Bundle == logical);
        // idempotency derives the token from the recorded (cross-prefix) object name
        Assert.True(Materializer.IsPartMaterialized(proj, "Velira", Alt.Stem, Alt.MeshPrefix, "face"));
    }

    [Fact]
    public void LodSiblings_ResolvedRecipeExact_RecordedOnTheMeshTarget()
    {
        const string lod0 = "c_VeliraSSR0101_slg_cloth1_lod0";
        const string lodm0 = "c_VeliraSSR0101_slg_cloth1_lodm0";
        const string a0 = "Assets/X/lod0.mesh", am0 = "Assets/X/lodm0.mesh";
        const string b0 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee1.bundle", bm0 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeee2.bundle";

        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneMesh(Path.Combine(abw, new string('1', 32) + ".bundle"), lod0,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, bundleName: b0);
        SyntheticBundle.BuildOneMesh(Path.Combine(abw, new string('2', 32) + ".bundle"), lodm0,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, bundleName: bm0);
        var vfs = TestVfs.Create(g.Root,
            new[] { (a0, b0), (am0, bm0) }, null,
            (b0, new string('1', 32)), (bm0, new string('2', 32)));
        var recipe = new RecipePart("cloth1", lod0, a0, new[] { new RecipeTierSlot(lodm0, am0) });

        var report = AssetExporter.ExportRecipePart(g.Root, vfs, Scope(vfs, Alt), Alt, "Velira", recipe, Path.Combine(g.Root, "mod", "velira_VeliraSSR0101"), sharedRoot: Path.Combine(g.Root, "mod"));

        var mesh = Assert.Single(report.Files, f => f.Kind == "mesh" && f.Ok);
        var sib = Assert.Single(mesh.LodSiblings!);
        Assert.Equal(lodm0, sib.ObjectName);
        Assert.Equal(bm0, sib.Bundle);   // the sibling's OWN catalog-pinned bundle
    }

    // ---- loud-fail chain: recipe → address → bundle → mesh, each link named --------------------------

    [Fact]
    public void Fail_NotRecipeBacked_ReportsTheMissingRecipe_NeverFallsBack()
    {
        using var g = new TempGame();
        var vfs = MeshVfs(g, "c_VeliraSSR0101_slg_cloth1_lod0", "z1.bundle", Array.Empty<(string, string)>());
        var root = g.Root;
        var recipe = new RecipePart("cloth1", SlotName: "", MeshAddress: "", Array.Empty<RecipeTierSlot>());

        var report = AssetExporter.ExportRecipePart(root, vfs, Scope(vfs, Alt), Alt, "Velira", recipe, Path.Combine(root, "mod"), sharedRoot: Path.Combine(root, "mod"));

        var mesh = Assert.Single(report.Files, f => f.Kind == "mesh");
        Assert.False(mesh.Ok);
        Assert.Contains("carries no prefab mesh identity", mesh.Note);
        Assert.Empty(report.CompletedParts);
    }

    [Fact]
    public void Fail_CatalogMiss_NamesTheAddress()
    {
        const string slot = "c_VeliraSSR0101_slg_cloth1_lod0";
        using var g = new TempGame();
        var vfs = MeshVfs(g, slot, "z1.bundle", new[] { ("Assets/OTHER.mesh", "z1.bundle") });
        var root = g.Root;
        var report = AssetExporter.ExportRecipePart(root, vfs, Scope(vfs, Alt), Alt, "Velira",
            Recipe(slot, "Assets/X/missing.mesh"), Path.Combine(root, "mod"), sharedRoot: Path.Combine(root, "mod"));

        var mesh = Assert.Single(report.Files, f => f.Kind == "mesh");
        Assert.False(mesh.Ok);
        Assert.Contains("no catalog entry for mesh address 'Assets/X/missing.mesh'", mesh.Note);
    }

    [Fact]
    public void Fail_BundleAbsentFromInstall_NamesTheBundle()
    {
        const string slot = "c_VeliraSSR0101_slg_cloth1_lod0";
        const string address = "Assets/X/a.mesh";
        using var g = new TempGame();
        // catalog resolves the address to a bundle the forward view can't locate (no manifest entry)
        var vfs = MeshVfs(g, slot, "present.bundle", new[] { (address, "ghost-not-installed.bundle") });
        var root = g.Root;
        var report = AssetExporter.ExportRecipePart(root, vfs, Scope(vfs, Alt), Alt, "Velira",
            Recipe(slot, address), Path.Combine(root, "mod"), sharedRoot: Path.Combine(root, "mod"));

        var mesh = Assert.Single(report.Files, f => f.Kind == "mesh");
        Assert.False(mesh.Ok);
        Assert.Contains("ghost-not-installed.bundle", mesh.Note);
        Assert.Contains("isn't in this install", mesh.Note);
    }

    [Fact]
    public void Fail_MeshAbsentFromResolvedBundle_NamesTheMesh()
    {
        // bundle present & resolvable, but the recipe slot name isn't a mesh inside it
        const string other = "c_VeliraSSR0101_slg_cloth2_lod0";
        const string logical = "ffffffffffffffffffffffffffffff01.bundle";
        const string address = "Assets/X/a.mesh";
        using var g = new TempGame();
        var vfs = MeshVfs(g, other, logical, new[] { (address, logical) });
        var root = g.Root;
        var wantedSlot = "c_VeliraSSR0101_slg_cloth1_lod0";   // not in the bundle
        var report = AssetExporter.ExportRecipePart(root, vfs, Scope(vfs, Alt), Alt, "Velira",
            Recipe(wantedSlot, address), Path.Combine(root, "mod"), sharedRoot: Path.Combine(root, "mod"));

        var mesh = Assert.Single(report.Files, f => f.Kind == "mesh");
        Assert.False(mesh.Ok);
        Assert.Contains($"mesh '{wantedSlot}' not found", mesh.Note);
        Assert.Contains(logical, mesh.Note);
    }

    [Fact]
    public void Fail_NonRecipeBacked_ThroughMaterializer_SurfacesTheReason()
    {
        using var g = new TempGame();
        var vfs = MeshVfs(g, "c_VeliraSSR0101_slg_cloth1_lod0", "z1.bundle", Array.Empty<(string, string)>());
        var root = g.Root;
        var recipe = new RecipePart("cloth1", "", "", Array.Empty<RecipeTierSlot>());
        var proj = new ModProject { RootDir = Path.Combine(root, "mod") };

        var r = Materializer.MaterializePart(proj, vfs, root, Alt, "Velira", proj.RootDir, recipe);

        Assert.Equal(MaterializeOutcome.Failed, r.Outcome);
        Assert.Contains("carries no prefab mesh identity", r.Error);
        Assert.DoesNotContain(proj.Targets, t => t.AssetType == "Mesh");
    }
}
