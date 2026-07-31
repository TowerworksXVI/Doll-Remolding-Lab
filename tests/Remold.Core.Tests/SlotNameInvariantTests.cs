using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// THE invariant on the smr/static route: a Mesh target's object name is the RENDERER SLOT's name, and the
/// part's workspace glb carries the mesh under that same name. Enemy and prop prefabs point renderer slots
/// at mesh objects whose own <c>m_Name</c> is unrelated to the slot — and can point several slots at ONE
/// object — so nothing derivable from the asset name reaches the part the workbench, the roster and the
/// build all speak of. The path id, never the name, is what selects the object.
/// </summary>
public class SlotNameInvariantTests
{
    private const string Logical = "cccccccccccccccccccccccccccccc01.bundle";
    private static Outfit Prop => new(0, "CratePropA", OutfitKind.Base);   // MeshPrefix c_CratePropA_slg_

    /// <summary>A game dir holding ONE mesh bundle whose sole mesh carries <paramref name="meshName"/> at
    /// path id 1 — the object an smr/static slot references directly.</summary>
    private static GameVfs MeshVfs(TempGame g, string meshName)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        var phys = new string('c', 32);
        SyntheticBundle.BuildOneMesh(Path.Combine(abw, phys + ".bundle"), meshName,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, bundleName: Logical);
        return TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null, (Logical, phys));
    }

    /// <summary>An smr-backed part: no recipe address, identity is bundle + path id, and the token is the
    /// one the workbench tree derives from the slot name.</summary>
    private static RecipePart Smr(string slotName) =>
        new(SubjectModelBuilder.SlotTokens(new[] { slotName }, Prop.MeshPrefix)(slotName),
            slotName, "", Array.Empty<RecipeTierSlot>(), Logical, 1);

    private static ExportReport Export(TempGame g, GameVfs vfs, RecipePart recipe, string modRoot)
    {
        var scope = SubjectScope.Build(vfs.Catalog, vfs.TryDeobfuscateLogical, Prop);
        return AssetExporter.ExportRecipePart(g.Root, vfs, scope, Prop, "Crate", recipe,
            Path.Combine(modRoot, Materializer.SubjectFolder("Crate", Prop.Stem)), sharedRoot: modRoot);
    }

    // ---- the staged record -----------------------------------------------------------------------------

    [Fact]
    public void A_divergent_slot_stages_its_SLOT_name_not_the_mesh_asset_name()
    {
        const string slot = "PROP_Shellcase_LOD0";
        using var g = new TempGame();
        var vfs = MeshVfs(g, "Plane001");            // the object's own name says nothing about the slot
        var modRoot = Path.Combine(g.Root, "mod");

        var report = Export(g, vfs, Smr(slot), modRoot);

        var mesh = Assert.Single(report.Files, f => f.Kind == "mesh");
        Assert.True(mesh.Ok, mesh.Note);
        Assert.Equal(slot, mesh.AssetName);
        Assert.Equal(1L, mesh.PathId);               // the path id is what read it
    }

    [Fact]
    public void The_workspace_glb_carries_the_part_under_the_slot_name()
    {
        // Blender's collections, the send-back's part match and the map-origin record all join on the name
        // inside the glb; a glb named after the mesh asset would take every divergent part out of a
        // combined send.
        const string slot = "PROP_Shellcase_LOD0";
        using var g = new TempGame();
        var vfs = MeshVfs(g, "Plane001");
        var modRoot = Path.Combine(g.Root, "mod");

        var report = Export(g, vfs, Smr(slot), modRoot);

        var glb = Assert.Single(report.Files, f => f.Kind == "mesh").Path;
        Assert.Equal(new[] { slot }, MeshGltf.MeshNames(glb).ToArray());
    }

    // ---- the open path ---------------------------------------------------------------------------------

    [Fact]
    public void The_part_token_round_trips_through_the_ledger()
    {
        // The failure this closes: the token the workbench clicked with derives from the SLOT name, so a
        // target recorded under the mesh asset's name resolves to another token (or none) and the
        // materialize reports "no mesh for '<part>' in the export" over a mesh that exported cleanly.
        const string slot = "PROP_Shellcase_LOD0";
        using var g = new TempGame();
        var vfs = MeshVfs(g, "Plane001");
        var modRoot = Path.Combine(g.Root, "mod");
        var recipe = Smr(slot);
        var project = new ModProject { RootDir = modRoot };

        ProjectBuilder.AddExport(project, Export(g, vfs, recipe, modRoot), modRoot, "Crate", Prop.Stem);

        Assert.Equal("Shellcase", recipe.Token);
        Assert.True(Materializer.IsPartMaterialized(project, "Crate", Prop.Stem, Prop.MeshPrefix, recipe.Token));
        var target = Assert.Single(Materializer.PartTargets(project, "Crate", Prop.Stem, Prop.MeshPrefix, recipe.Token));
        Assert.Equal(slot, target.ObjectName);
    }

    [Fact]
    public void Two_slots_on_one_mesh_object_record_two_targets_under_their_own_names()
    {
        // A prefab may point several renderer slots at ONE mesh object. Each is its own part with its own
        // workspace file and its own edit, so keying the target on the path id alone would leave the second
        // one with nothing staged under its name.
        using var g = new TempGame();
        var vfs = MeshVfs(g, "dz_005_mesh");
        var modRoot = Path.Combine(g.Root, "mod");
        var first = Smr("PROP_Grenade1_LOD0");
        var second = Smr("PROP_Grenade2_LOD0");
        var project = new ModProject { RootDir = modRoot };

        ProjectBuilder.AddExport(project, Export(g, vfs, first, modRoot), modRoot, "Crate", Prop.Stem);
        ProjectBuilder.AddExport(project, Export(g, vfs, second, modRoot), modRoot, "Crate", Prop.Stem);

        Assert.Equal(new[] { "Grenade1", "Grenade2" }, new[] { first.Token, second.Token });
        var meshes = project.Targets.Where(t => t.AssetType == "Mesh").ToList();
        Assert.Equal(new[] { "PROP_Grenade1_LOD0", "PROP_Grenade2_LOD0" },
            meshes.Select(t => t.ObjectName).ToArray());
        Assert.All(meshes, t => Assert.Equal(1L, t.PathId));   // one object, two parts
        foreach (var part in new[] { first, second })
            Assert.Single(Materializer.PartTargets(project, "Crate", Prop.Stem, Prop.MeshPrefix, part.Token));
    }

    // ---- the build's own roster join -------------------------------------------------------------------

    [Fact]
    public void A_replace_on_a_divergent_part_derives_instead_of_throwing()
    {
        // VerbDerivation keys the live roster by SubjectPart.SlotName and the edited target by its object
        // name. Under the old staging those disagreed on a divergent part, so its Replace died at build with
        // "edited mesh '<name>' is not in … roster" even after the open was fixed.
        const string slot = "PROP_Shellcase_LOD0";
        using var g = new TempGame();
        var vfs = MeshVfs(g, "Plane001");
        var modRoot = Path.Combine(g.Root, "mod");
        var recipe = Smr(slot);
        var project = new ModProject { RootDir = modRoot };
        project.Selection.Add(new SelectionEntry { Character = "Crate", Outfit = Prop.Stem });
        ProjectBuilder.AddExport(project, Export(g, vfs, recipe, modRoot), modRoot, "Crate", Prop.Stem);
        // the workspace glb no longer matches its originals/ copy — an edited part, i.e. a Replace
        var edited = project.Targets.Single(t => t.AssetType == "Mesh");
        File.WriteAllText(Path.Combine(modRoot, edited.ReplaceFile), "an authored donor");

        var model = new SubjectModel("Crate", Prop.Stem, SubjectSource.Prefab, new[]
        {
            new SubjectPart(recipe.Token, slot, "", Array.Empty<SubjectMaterial>(),
                MeshBundle: Logical, MeshPathId: 1),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var warnings = new List<string>();

        var edits = VerbDerivation.DeriveAll(project, (c, s) => model, warnings);

        var e = Assert.Single(edits);
        Assert.Equal(EditVerbs.Replace, e.Verb);
        Assert.Equal(slot, e.Mesh);
        Assert.Equal(1L, e.PathId);
        Assert.Empty(warnings);
    }
}
