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
/// THE invariant on the smr/static route: a part is named by its RENDERER SLOT, and the object it draws is
/// selected by path id. Enemy and prop prefabs point renderer slots at mesh objects whose own
/// <c>m_Name</c> is unrelated to the slot — and can point several slots at ONE object — so nothing
/// derivable from the asset name reaches the part the workbench, the roster and the build all speak of.
/// </summary>
public class SlotNameInvariantTests
{
    private const string Logical = "cccccccccccccccccccccccccccccc01.bundle";
    private static Outfit Prop => new(0, "CratePropA", OutfitKind.Base);   // MeshPrefix c_CratePropA_slg_

    /// <summary>A game dir holding ONE mesh bundle whose sole mesh carries <paramref name="meshName"/> at
    /// path id 1 — the object an smr/static slot references directly.</summary>
    private static GameVfs MeshVfs(TempGame g, string meshName) => MeshVfs(g, meshName, out _);

    /// <summary>The same, plus <paramref name="meshPathId"/>. With <paramref name="decoy"/> the bundle ships
    /// a SECOND mesh of that name first, so the id names one of two same-named objects rather than the only
    /// one there is.</summary>
    private static GameVfs MeshVfs(TempGame g, string meshName, out long meshPathId, bool decoy = false)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        var phys = new string('c', 32);
        meshPathId = SyntheticBundle.BuildOneMesh(Path.Combine(abw, phys + ".bundle"), meshName,
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 }, bundleName: Logical,
            // four vertices against the real mesh's three: whichever object the export read is readable off
            // the glb it wrote
            sameNamedFirst: decoy
                ? (new float[] { 0, 0, 0, 2, 0, 0, 0, 2, 0, 2, 2, 0 }, new[] { 0, 1, 2, 2, 1, 3 })
                : null);
        return TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null, (Logical, phys));
    }

    /// <summary>A released project whose mesh target names a divergent slot, re-anchored against a roster
    /// that answers for that slot. The failure this closes: a join made on the mesh ASSET name finds
    /// nothing for a part whose object is called something else, and the change is lost — silently, or as
    /// a refusal to convert a project that is perfectly sound.</summary>
    [Fact]
    public void A_divergent_slot_re_anchors_by_its_slot_name_and_path_id()
    {
        const string slot = "PROP_Shellcase_LOD0";
        using var g = new TempGame();
        var vfs = MeshVfs(g, "Plane001");            // the object's own name says nothing about the slot
        string modRoot = Path.Combine(g.Root, "mod");
        Directory.CreateDirectory(Path.Combine(modRoot, "meshes"));
        File.WriteAllText(Path.Combine(modRoot, "meshes", "shellcase.glb"), "an authored donor");
        File.WriteAllText(Path.Combine(modRoot, "meshes", "shellcase.orig.glb"), "the original");
        var project = new ModProject { RootDir = modRoot };
        project.Selection.Add(new SelectionEntry { Character = "Crate", Outfit = Prop.Stem });
        project.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", ObjectName = slot, Bundle = Logical, PathId = 1,
            SubjectCharacter = "Crate", SubjectOutfit = Prop.Stem,
            ReplaceFile = "meshes/shellcase.glb", OriginalFile = "meshes/shellcase.orig.glb",
        });
        var model = new SubjectModel("Crate", Prop.Stem, SubjectSource.Prefab, new[]
        {
            new SubjectPart("Shellcase", slot, "", Array.Empty<SubjectMaterial>(),
                RendererBundle: Logical, RendererPathId: 9, MeshBundle: Logical, MeshPathId: 1),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var resolver = new LegacyProjectResolver(new Remold.Core.Migoto.BuildEnv(
            (c, s) => c == "Crate" && s == Prop.Stem ? model : null,
            _ => null, vfs.TryDeobfuscateLogical, "26109", null));

        var adapted = LegacyProjectAdapter.Adapt(project, resolver.ResolvePart, resolver.RosterSlots);

        Assert.True(adapted.Report.CanSave,
            string.Join("; ", adapted.Report.Items.Where(i => i.BlocksSave).Select(i => i.Detail)));
        var edit = Assert.Single(adapted.Project.EditDefinitions, e => e.Kind == EditDefinitionKind.Content);
        Assert.Equal(slot, edit.Target.RendererSlot);
        var geometry = Assert.Single(adapted.Project.TargetSlots,
            s => s.Input == TargetInputKind.Geometry && s.Domain == TargetSlotDomain.Game);
        Assert.Equal(1L, geometry.Mesh!.PathId);     // the path id, never the name, is what selects it
        Assert.Equal(slot, geometry.Part.RendererSlot);
    }

    /// <summary>The other half, on the route that opens a part for editing: the glb a Blender open writes
    /// names its mesh by the RENDERER SLOT, not by the asset's own <c>m_Name</c>. Everything downstream
    /// joins on that name — the Blender collection the modder edits, the send-back's per-part match, the
    /// RMO source record — so a glb naming the asset instead would come back matching no part of the
    /// project. The read that produced it is selected by PATH ID: a second mesh of the same name at another
    /// id is what a name-only lookup takes, and it is not the object the renderer pinned.</summary>
    [Fact]
    public void An_opened_parts_glb_carries_the_slot_name_and_the_mesh_the_path_id_names()
    {
        const string slot = "PROP_Shellcase_LOD0";
        using var g = new TempGame();
        var vfs = MeshVfs(g, "Plane001", out long pathId, decoy: true);
        string glb = g.At(Path.Combine("run", "parts", "shellcase.glb"));
        Directory.CreateDirectory(Path.GetDirectoryName(glb)!);

        var done = AssetExporter.BuildRiggedGlbs(g.Root, vfs, Prop, "Crate",
            new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
            {
                ("Shellcase", Logical, slot, glb, null, pathId, null),
            },
            g.At(Path.Combine("run", "textures")));

        Assert.Equal(new[] { "Shellcase" }, done.ToArray());
        Assert.Equal(new[] { slot }, MeshGltf.MeshNames(glb).ToArray());
        // three vertices is the mesh at the recorded path id; the same-named decoy at path id 1 has four
        var primitive = SharpGLTF.Schema2.ModelRoot.Load(glb).LogicalMeshes.Single().Primitives.Single();
        Assert.Equal(3, primitive.GetVertexAccessor("POSITION").Count);
    }
}
