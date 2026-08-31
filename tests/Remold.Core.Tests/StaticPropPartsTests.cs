using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The static-renderer read path: a prop prefab carries plain MeshRenderers whose mesh sits on the
/// MeshFilter beside them, and those slots become parts exactly like skinned ones — so Hide and Retexture
/// reach them. Also the message split, since a prefab that resolves and yields nothing is a different
/// failure from a subject with no prefab at all.
/// </summary>
public class StaticPropPartsTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "gf2-props-" + Guid.NewGuid().ToString("N"));

    public StaticPropPartsTests() => Directory.CreateDirectory(_work);
    public void Dispose() { try { Directory.Delete(_work, recursive: true); } catch { } }

    private const string Stem = "CrateMk2";
    private const string Frame = "p_CrateMk2_frame_lod0";
    private const string Lid = "p_CrateMk2_lid_lod0";

    private static CatalogIndex Catalog(string stem, string prefabLogical, params string[] closure)
    {
        var address = GameVfs.PrefabAddress("Character/Player", stem);
        return CatalogIndex.ForTest(
            new[] { (address, prefabLogical) },
            new[] { (address, new[] { prefabLogical }.Concat(closure).ToArray()) });
    }

    /// <summary>A prop bundle: one container root, two STATIC renderer slots each with a MeshFilter mesh,
    /// and no RoleMeshRes component at all — the shape whose slots the skinned-only read found nothing in.
    /// The lid's renderer binds the empty PPtr, so the placeholder rule is exercised on a static slot
    /// too.</summary>
    private static void BuildPropPrefab(string abw, char fileFill) =>
        WorkbenchPrefab.Build(Path.Combine(abw, new string(fileFill, 32) + ".bundle"),
            bundleName: "prop.bundle", rootName: Stem,
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec(Frame, new[] { (1, 41L) }, Mesh: (0, 901L),
                    Renderer: SlotRenderer.Static),
                new WorkbenchPrefab.SlotSpec(Lid, new[] { (0, 0L) }, Mesh: (0, 902L),
                    Renderer: SlotRenderer.Static),
            },
            recipe: null,
            externalCabs: new[] { "CAB-matP" });

    private static Outfit PropOutfit() => new(9001, Stem, OutfitKind.Base);

    // ---- the read gap: static slots enumerate --------------------------------------------------------

    [Fact]
    public void A_static_prefabs_mesh_renderer_slots_become_parts()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        BuildPropPrefab(abw, '1');
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('2', 32) + ".bundle"),
            "matP.bundle", materialName: "M_crate", materialPathId: 41,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("p_CrateMk2_frame_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0x30, 0x60, 0x90, 0xFF)), cabName: "CAB-matP");

        var model = SubjectModelBuilder.Build(Catalog(Stem, "prop.bundle", "matP.bundle"),
            FixtureCrawl.DeobfuscateOver(abw), PropOutfit(), "Crate");

        Assert.Empty(model.Problems);
        Assert.Equal(new[] { "frame", "lid" }, model.Parts.Select(p => p.Token).ToArray());

        // the renderer class rides the part, and the subject reads as all-static: its Open-all is gated,
        // since a combined Blender session carries only skinned parts
        Assert.All(model.Parts, p => Assert.True(p.IsStatic));
        Assert.True(model.AllPartsStatic);

        var frame = model.Parts.Single(p => p.Token == "frame");
        Assert.Equal(Frame, frame.SlotName);
        // a static slot carries mesh identity like an smr-body one: the prefab's own bundle plus the path id
        Assert.Equal("prop.bundle", frame.MeshBundle);
        Assert.Equal(901L, frame.MeshPathId);
        Assert.Equal("", frame.MeshAddress);   // no recipe on this shape

        // the materials the renderer binds resolve, so a retexture has something to land on
        var material = Assert.Single(frame.Materials);
        Assert.Equal("M_crate", material.Name);
        Assert.Equal("p_CrateMk2_frame_d", Assert.Single(material.Maps).TextureName);
    }

    /// <summary>A renderer whose mesh lives in one of Unity's engine-shipped archives (a shadow-blob or
    /// glow quad) is not a part and records no problem: the archive is not in the game's corpus, so the
    /// mesh can never be read or replaced, and a subject wearing one must still measure clean.</summary>
    [Fact]
    public void A_slot_whose_mesh_lives_in_unitys_builtin_resources_is_not_a_part()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        WorkbenchPrefab.Build(Path.Combine(abw, new string('7', 32) + ".bundle"),
            bundleName: "prop.bundle", rootName: Stem,
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec(Frame, new[] { (1, 41L) }, Mesh: (0, 901L),
                    Renderer: SlotRenderer.Static),
                new WorkbenchPrefab.SlotSpec("p_CrateMk2_shadowplane_lod0", new[] { (0, 0L) },
                    Mesh: (2, 100L), Renderer: SlotRenderer.Static),
            },
            recipe: null,
            externalCabs: new[] { "CAB-matP", "unity default resources" });
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('8', 32) + ".bundle"),
            "matP.bundle", materialName: "M_crate", materialPathId: 41,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("p_CrateMk2_frame_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0x30, 0x60, 0x90, 0xFF)), cabName: "CAB-matP");

        var model = SubjectModelBuilder.Build(Catalog(Stem, "prop.bundle", "matP.bundle"),
            FixtureCrawl.DeobfuscateOver(abw), PropOutfit(), "Crate");

        Assert.Empty(model.Problems);
        Assert.Equal(new[] { "frame" }, model.Parts.Select(p => p.Token).ToArray());
    }

    [Fact]
    public void The_pick_roster_lists_the_same_static_tokens_the_workbench_builds()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        BuildPropPrefab(abw, '3');

        var outfit = PropOutfit();
        var scope = SubjectScope.Build(Catalog(Stem, "prop.bundle"),
            FixtureCrawl.DeobfuscateOver(abw), outfit);

        Assert.Equal(new[] { "frame", "lid" },
            SubjectModelBuilder.OwnedSlotTokens(scope.Candidates, outfit).ToArray());
    }

    [Fact]
    public void Hide_and_retexture_convert_on_a_static_part()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        BuildPropPrefab(abw, '4');
        SyntheticBundle.BuildOneMaterial(Path.Combine(abw, new string('5', 32) + ".bundle"),
            "matP.bundle", materialName: "M_crate", materialPathId: 41,
            texEnvs: new[] { ("_BaseMap", 0, 2L) }, externalCabs: Array.Empty<string>(),
            localTexture: new SyntheticBundle.TextureSpec("p_CrateMk2_frame_d", 4, 4,
                SyntheticBundle.SolidRgba32(4, 4, 0x30, 0x60, 0x90, 0xFF)), cabName: "CAB-matP");

        var model = SubjectModelBuilder.Build(Catalog(Stem, "prop.bundle", "matP.bundle"),
            FixtureCrawl.DeobfuscateOver(abw), PropOutfit(), "Crate");
        var texture = model.Parts.Single(p => p.Token == "frame").Materials[0].Maps[0];

        var project = new ModProject { RootDir = _work };
        project.Selection.Add(new SelectionEntry { Character = "Crate", Outfit = Stem });
        project.Hidden.Add(new HiddenMesh { Character = "Crate", Outfit = Stem, Mesh = Lid });
        File.WriteAllText(Path.Combine(_work, "frame_d.png"), "png");
        project.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = texture.BundleId, ObjectName = texture.TextureName,
            ReplaceFile = "frame_d.png", SubjectCharacter = "Crate", SubjectOutfit = Stem,
        });

        var env = new Remold.Core.Migoto.BuildEnv((c, s) => c == "Crate" && s == Stem ? model : null,
            _ => null, FixtureCrawl.DeobfuscateOver(abw), "26109", null);
        var resolver = new LegacyProjectResolver(env);

        var adapted = LegacyProjectAdapter.Adapt(project, resolver.ResolvePart, resolver.RosterSlots);

        Assert.True(adapted.Report.CanSave,
            string.Join("; ", adapted.Report.Items.Where(i => i.BlocksSave).Select(i => i.Detail)));
        var hide = Assert.Single(adapted.Project.EditDefinitions, e => e.Kind == EditDefinitionKind.Hide);
        Assert.Equal(Lid, hide.Target.RendererSlot);
        var retexture = Assert.Single(adapted.Project.EditDefinitions,
            e => e.Kind == EditDefinitionKind.Content);
        Assert.Equal(Frame, retexture.Target.RendererSlot);
        var slots = adapted.Project.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        var albedo = Assert.Single(retexture.Bindings,
            b => slots[b.SlotId].Input == TargetInputKind.BaseColor
                && slots[b.SlotId].Domain == TargetSlotDomain.Game);
        Assert.Equal("frame_d.png",
            adapted.Project.ProjectAssets.Single(a => a.Id == albedo.ProjectAssetId).File);
    }

    [Fact]
    public void A_subject_mixing_static_and_skinned_slots_does_not_read_as_all_static()
    {
        // The combined session carries the skinned parts, so a mixed subject still has one to open. Only a
        // subject with NO skinned part is authored one part at a time.
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        WorkbenchPrefab.Build(Path.Combine(abw, new string('a', 32) + ".bundle"),
            bundleName: "prop.bundle", rootName: Stem,
            slots: new[]
            {
                new WorkbenchPrefab.SlotSpec(Frame, new[] { (0, 0L) }, Mesh: (0, 901L),
                    Renderer: SlotRenderer.Static),
                new WorkbenchPrefab.SlotSpec("p_CrateMk2_hinge_lod0", new[] { (0, 0L) }, Mesh: (0, 903L)),
            },
            recipe: null, externalCabs: Array.Empty<string>());

        var model = SubjectModelBuilder.Build(Catalog(Stem, "prop.bundle"),
            FixtureCrawl.DeobfuscateOver(abw), PropOutfit(), "Crate");

        Assert.Equal(new[] { "frame", "hinge" }, model.Parts.Select(p => p.Token).ToArray());
        Assert.True(model.Parts.Single(p => p.Token == "frame").IsStatic);
        Assert.False(model.Parts.Single(p => p.Token == "hinge").IsStatic);
        Assert.False(model.AllPartsStatic);
    }

    // ---- the message split ---------------------------------------------------------------------------

    [Fact]
    public void A_prefab_that_yields_no_readable_slot_says_that_and_not_that_none_exists()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        // a container root whose one slot carries neither a recipe row nor a serialized mesh: the prefab
        // IS there, and nothing in it can be made a part
        WorkbenchPrefab.Build(Path.Combine(abw, new string('6', 32) + ".bundle"),
            bundleName: "prop.bundle", rootName: Stem,
            slots: new[] { new WorkbenchPrefab.SlotSpec(Frame, new[] { (0, 0L) }, Renderer: SlotRenderer.Static) },
            recipe: null, externalCabs: Array.Empty<string>());

        var model = SubjectModelBuilder.Build(Catalog(Stem, "prop.bundle"),
            FixtureCrawl.DeobfuscateOver(abw), PropOutfit(), "Crate");

        Assert.Empty(model.Parts);
        Assert.Equal(SubjectModelBuilder.NoReadableParts, model.Problems[0]);
        Assert.DoesNotContain(model.Problems, p => p.Contains("No assembly prefab", StringComparison.Ordinal));
    }

    [Fact]
    public void A_subject_whose_prefab_bundle_is_absent_keeps_the_no_prefab_message()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);

        var model = SubjectModelBuilder.Build(Catalog(Stem, "prop.bundle"),
            FixtureCrawl.DeobfuscateOver(abw), PropOutfit(), "Crate");

        Assert.Empty(model.Parts);
        Assert.Equal("No assembly prefab found for this subject. Its parts can't be read.", model.Problems[0]);
    }

    [Fact]
    public void A_declining_root_in_a_dependency_leaves_an_absent_prefab_saying_it_is_absent()
    {
        // The subject's OWN prefab bundle isn't there. A shared dependency in its closure carries someone
        // else's container root that declines, which says nothing about this subject: reporting an
        // unreadable prefab here sends the modder hunting an asset that doesn't exist.
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        WorkbenchPrefab.Build(Path.Combine(abw, new string('9', 32) + ".bundle"),
            bundleName: "shared.bundle", rootName: "SharedRig",
            slots: new[] { new WorkbenchPrefab.SlotSpec("shared_anchor", new[] { (0, 0L) }) },
            recipe: null, externalCabs: Array.Empty<string>());

        var model = SubjectModelBuilder.Build(Catalog(Stem, "prop.bundle", "shared.bundle"),
            FixtureCrawl.DeobfuscateOver(abw), PropOutfit(), "Crate");

        Assert.Empty(model.Parts);
        Assert.Equal("No assembly prefab found for this subject. Its parts can't be read.", model.Problems[0]);
        Assert.DoesNotContain(model.Problems, p => p == SubjectModelBuilder.NoReadableParts);
    }

    // ---- the parser's own answer ---------------------------------------------------------------------

    [Fact]
    public void The_parser_marks_a_static_slot_and_reads_its_mesh_off_the_filter()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        BuildPropPrefab(abw, '7');

        var prefab = PrefabReader.Read(
            File.ReadAllBytes(Path.Combine(abw, new string('7', 32) + ".bundle")));

        Assert.NotNull(prefab);
        Assert.All(prefab!.Slots, s => Assert.Equal(SlotRenderer.Static, s.Renderer));
        var frame = prefab.Slots.Single(s => s.Name == Frame);
        Assert.Equal(901L, frame.Mesh!.PathId);
        Assert.Null(frame.Mesh.Cab);
    }

    [Fact]
    public void A_skinned_slot_still_reads_as_skinned()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        WorkbenchPrefab.Build(Path.Combine(abw, new string('8', 32) + ".bundle"),
            bundleName: "smr.bundle", rootName: "TestySSR01",
            slots: new[] { new WorkbenchPrefab.SlotSpec("c_TestySSR01_slg_body_lod0", new[] { (0, 0L) }, Mesh: (0, 5L)) },
            recipe: Array.Empty<(string, string)>(), externalCabs: Array.Empty<string>());

        var prefab = PrefabReader.Read(
            File.ReadAllBytes(Path.Combine(abw, new string('8', 32) + ".bundle")));

        Assert.Equal(SlotRenderer.Skinned, Assert.Single(prefab!.Slots).Renderer);
    }
}
