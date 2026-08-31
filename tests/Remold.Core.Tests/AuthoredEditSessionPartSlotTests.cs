using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>Opening a part the project has never touched. Nothing else mints a bare part's game slots — the
/// compatibility adapter files them only for targets a released project had already edited — so until this
/// command exists a fresh part has nothing to build an edit from and nothing for a hide to re-anchor on.
/// </summary>
public sealed class AuthoredEditSessionPartSlotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-part-slots-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Opening_a_bare_part_records_the_slots_the_install_answers_for()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var hair = AuthoredEditFixtures.Hair;

        session.EnsurePartSlots(hair, Resolve);
        var slots = session.Snapshot().TargetSlots.Where(slot => slot.Part.SameAs(hair)).ToList();

        // The adapter's shape: lod0 geometry, one geometry slot per tier, then each material's supported
        // inputs at its own position — and no visibility slot, which is a hide's to own.
        Assert.Equal(new[]
        {
            ("lod0", TargetInputKind.Geometry, (int?)null),
            ("lod1", TargetInputKind.Geometry, null),
            (null, TargetInputKind.BaseColor, 0),
            (null, TargetInputKind.Normal, 0),
            (null, TargetInputKind.Blend, 0),
            (null, TargetInputKind.Ramp, 1),
        }, slots.Select(slot => (slot.Tier, slot.Input, slot.MaterialSlotIndex)).ToArray());
        Assert.All(slots, slot => Assert.Equal(TargetSlotDomain.Game, slot.Domain));
        Assert.All(slots, slot => Assert.Null(slot.OwnerEditId));
        Assert.Equal(new[] { "slot-0001", "slot-0002", "slot-0003", "slot-0004", "slot-0005", "slot-0006" },
            slots.Select(slot => slot.Id).ToArray());
        var picture = slots.Single(slot => slot.Input == TargetInputKind.BaseColor);
        Assert.Equal(picture.SubmeshIndex, picture.MaterialSlotIndex);
        Assert.Equal(84001, picture.Material!.PathId);
        Assert.Equal(72002, picture.Mesh!.PathId);
        // Opening a part is not authoring or placing an edit for it.
        Assert.DoesNotContain(session.Snapshot().EditDefinitions, edit => edit.Target.SameAs(hair));
    }

    [Fact]
    public void An_opened_part_can_be_given_an_edit_and_hidden_by_a_key_group()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var hair = AuthoredEditFixtures.Hair;
        var body = AuthoredEditFixtures.Body;
        string group = session.CreateKeyGroup("F7", "edit-long");

        var refused = Assert.Throws<AuthoredRefusalException>(() => session.CreateEdit(hair));
        Assert.Equal(AuthoredEditSession.NowhereToRecord("an edit"), refused.Message);
        refused = Assert.Throws<AuthoredRefusalException>(() => session.CreateHideEdit(hair));
        Assert.Equal(AuthoredEditSession.NowhereToRecord("a hide"), refused.Message);

        session.EnsurePartSlots(hair, Resolve);

        string edit = session.CreateEdit(hair);
        Assert.Equal(6, session.Slots(edit).Count);
        Assert.Equal(edit, session.Part(hair).EditDefinitionId);
        session.PlaceEdit(session.CreateHideEdit(hair), group, 1);

        var project = session.Snapshot();
        project.RootDir = _root;
        Materialize(project);
        var backend = new AuthoredBuildPlannerTests.Backend();
        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        var planned = plan.Parts.Single(part => part.Target.SameAs(hair));
        Assert.Equal(BuildPlanVerdict.Resolved, planned.Suppression!.Decision.Verdict);
        // The hide re-anchors on the part's own game slots: the opened lod0 geometry is what it found.
        var anchor = Assert.Single(backend.VisibilityRequests,
            request => request.Target.SameAs(hair)).AuthoredSlot;
        Assert.Equal(("lod0", TargetInputKind.Visibility, TargetSlotDomain.Game),
            (anchor.Tier, anchor.Input, anchor.Domain));
    }

    [Fact]
    public void Opening_a_part_twice_adds_nothing_the_second_time()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var hair = AuthoredEditFixtures.Hair;

        session.EnsurePartSlots(hair, Resolve);
        string once = AuthoredProjectSerializer.Serialize(session.Snapshot());
        session.EnsurePartSlots(hair, Resolve);

        Assert.Equal(once, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    [Fact]
    public void Opening_records_every_property_binding_even_when_two_share_one_texture()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var part = AuthoredEditFixtures.Part("c_vesna_sparkle_lod0");
        var shared = Reference(82001, "shared_detail");
        var resolved = new LegacyResolvedPart(part, Reference(70004, part.RendererSlot),
            Reference(72004, "sparkle_mesh"),
            new[]
            {
                new LegacyResolvedMaterial(0, "sparkle", Reference(84004, "sparkle"),
                    new[]
                    {
                        new LegacyResolvedTexture(TargetInputKind.BaseColor, "bundle", "base", 82000,
                            Reference(82000, "base"), "_BaseMap"),
                        new LegacyResolvedTexture(TargetInputKind.Texture, "bundle", "shared_detail", 82001,
                            shared, "_DetailAlbedo"),
                        new LegacyResolvedTexture(TargetInputKind.Texture, "bundle", "shared_detail", 82001,
                            shared, "_DetailMask"),
                    }),
                new LegacyResolvedMaterial(1, "surplus", Reference(84005, "surplus"),
                    new[]
                    {
                        new LegacyResolvedTexture(TargetInputKind.Texture, "bundle", "glitter", 82002,
                            Reference(82002, "glitter"), "_GlitterMap"),
                    }),
            }, MaterialIndexCounts: new[] { 36 });

        session.EnsurePartSlots(part, _ => resolved);

        var textures = session.Snapshot().TargetSlots.Where(slot => slot.Part.SameAs(part)
            && slot.Input != TargetInputKind.Geometry).ToList();
        Assert.Equal(new[] { "_BaseMap", "_DetailAlbedo", "_DetailMask", "_GlitterMap" },
            textures.Select(slot => slot.ShaderProperty));
        Assert.Equal(2, textures.Count(slot => slot.Input == TargetInputKind.Texture
            && slot.MaterialSlotIndex == 0));
        Assert.Equal(36, textures.Single(slot => slot.ShaderProperty == "_DetailAlbedo").DrawIndexCount);
        Assert.Equal(0, textures.Single(slot => slot.ShaderProperty == "_GlitterMap").DrawIndexCount);
    }

    /// <summary>The same where edits have taken the part's slots in hand: a slot is the same place whoever
    /// holds it, so opening the part again adds nothing rather than giving one game object a further slot for
    /// every edit that has ever touched it.</summary>
    [Fact]
    public void Opening_a_part_whose_slots_edits_already_hold_adds_nothing()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var hair = AuthoredEditFixtures.Hair;
        session.EnsurePartSlots(hair, Resolve);
        session.CreateEdit(hair);
        session.CreateEdit(hair);
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        session.EnsurePartSlots(hair, Resolve);

        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
        // The second edit has exact output copies of the six game routes. Re-opening adds no third set.
        Assert.Equal(12, session.Snapshot().TargetSlots.Count(slot => slot.Part.SameAs(hair)));
    }

    [Fact]
    public void A_part_the_install_does_not_have_is_refused_and_leaves_no_residue()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        var error = Assert.Throws<AuthoredRefusalException>(() =>
            session.EnsurePartSlots(AuthoredEditFixtures.Part("c_vesna_nothing_lod0"), Resolve));

        Assert.Equal(AuthoredEditSession.PartNotInstalled, error.Message);
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    /// <summary>A route the install answers with no exact object is refused whole, the way the adapter's
    /// equivalent gap blocks a migration, rather than opening the part around the hole.</summary>
    [Fact]
    public void A_route_with_no_exact_object_is_refused_rather_than_half_opened()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        var error = Assert.Throws<AuthoredRefusalException>(() =>
            session.EnsurePartSlots(AuthoredEditFixtures.Cape, Resolve));

        Assert.Equal("Couldn't find this part's mesh in the current game files.", error.Message);
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    [Fact]
    public void A_shading_value_slot_mints_lazily_once_and_defaults_every_edit_to_the_games_value()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var hair = AuthoredEditFixtures.Hair;
        session.EnsurePartSlots(hair, Resolve);
        string edit = session.CreateEdit(hair);
        int before = session.Snapshot().TargetSlots.Count;

        string slotId = session.EnsureMaterialValueSlot(hair, 0, "_UseGIFlatten", Resolve);

        var project = session.Snapshot();
        var slot = project.TargetSlots.Single(candidate => candidate.Id == slotId);
        Assert.Equal(("_UseGIFlatten", TargetInputKind.MaterialValue, TargetSlotDomain.Game, 0, 0),
            (slot.Semantic, slot.Input, slot.Domain, slot.SubmeshIndex, slot.MaterialSlotIndex));
        Assert.Equal(84001, slot.Material!.PathId);
        Assert.Equal(before + 1, project.TargetSlots.Count);
        // every content edit answering the part holds the honest no-op default
        var binding = project.EditDefinitions.Single(candidate => candidate.Id == edit)
            .Bindings.Single(candidate => candidate.SlotId == slotId);
        Assert.Equal(BindingKind.TargetGameValue, binding.Kind);
        // minting again is the same place
        Assert.Equal(slotId, session.EnsureMaterialValueSlot(hair, 0, "_UseGIFlatten", Resolve));
        Assert.Equal(before + 1, session.Snapshot().TargetSlots.Count);
    }

    [Fact]
    public void A_second_edit_can_author_an_independent_value_on_the_shared_shading_slot()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.SetRootDir(_root);
        var hair = AuthoredEditFixtures.Hair;
        session.EnsurePartSlots(hair, Resolve);
        string first = session.CreateEdit(hair);
        string slotId = session.EnsureMaterialValueSlot(hair, 0, "_UseGIFlatten", Resolve);
        session.ChooseMaterialValue(first, slotId, "0");

        string second = session.CreateEdit(hair);
        Assert.Equal(slotId, session.EnsureMaterialValueSlot(hair, 0, "_UseGIFlatten", Resolve));
        session.ChooseMaterialValue(second, slotId, "1");

        var project = session.Snapshot();
        Assert.Single(project.TargetSlots, slot => slot.Part.SameAs(hair)
            && slot.Input == TargetInputKind.MaterialValue
            && slot.MaterialSlotIndex == 0
            && slot.Semantic == "_UseGIFlatten");
        var firstBinding = project.EditDefinitions.Single(edit => edit.Id == first)
            .Bindings.Single(binding => binding.SlotId == slotId);
        var secondBinding = project.EditDefinitions.Single(edit => edit.Id == second)
            .Bindings.Single(binding => binding.SlotId == slotId);
        Assert.NotEqual(firstBinding.ProjectAssetId, secondBinding.ProjectAssetId);
        Assert.Equal("0", project.ProjectAssets.Single(asset =>
            asset.Id == firstBinding.ProjectAssetId).Value!.Value);
        Assert.Equal("1", project.ProjectAssets.Single(asset =>
            asset.Id == secondBinding.ProjectAssetId).Value!.Value);
        Assert.NotEqual(project.ProjectAssets.Single(asset =>
                asset.Id == firstBinding.ProjectAssetId).File,
            project.ProjectAssets.Single(asset => asset.Id == secondBinding.ProjectAssetId).File);
        Assert.Empty(AuthoredProjectValidator.Errors(project));
    }

    [Fact]
    public void A_shading_slot_refuses_unknown_fields_and_missing_material_positions()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var hair = AuthoredEditFixtures.Hair;
        session.EnsurePartSlots(hair, Resolve);

        Assert.Contains("not an authorable shading value",
            Assert.Throws<ArgumentException>(() =>
                session.EnsureMaterialValueSlot(hair, 0, "_BaseColor", Resolve)).Message);
        Assert.Contains("no material 7",
            Assert.Throws<AuthoredRefusalException>(() =>
                session.EnsureMaterialValueSlot(hair, 7, "_UseGIFlatten", Resolve)).Message);
    }

    [Fact]
    public void A_typed_shading_value_authors_a_structured_asset_with_a_real_file()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.SetRootDir(_root);
        var hair = AuthoredEditFixtures.Hair;
        session.EnsurePartSlots(hair, Resolve);
        string edit = session.CreateEdit(hair);
        string flatten = session.EnsureMaterialValueSlot(hair, 0, "_UseGIFlatten", Resolve);
        string colour = session.EnsureMaterialValueSlot(hair, 0, "_StockingCenterColor", Resolve);

        session.ChooseMaterialValue(edit, flatten, "0");
        session.ChooseMaterialValue(edit, colour, "0.5, 0.25, 1, 1");

        var project = session.Snapshot();
        var editDef = project.EditDefinitions.Single(candidate => candidate.Id == edit);
        var flattenAsset = project.ProjectAssets.Single(asset => asset.Id == editDef.Bindings
            .Single(binding => binding.SlotId == flatten).ProjectAssetId);
        Assert.Equal(("_UseGIFlatten", "0"), (flattenAsset.Value!.Semantic, flattenAsset.Value.Value));
        var colourAsset = project.ProjectAssets.Single(asset => asset.Id == editDef.Bindings
            .Single(binding => binding.SlotId == colour).ProjectAssetId);
        Assert.Equal("0.5 0.25 1 1", colourAsset.Value!.Value);
        // the recorded file really exists, so the plan's missing-asset check passes
        foreach (var asset in new[] { flattenAsset, colourAsset })
            Assert.True(File.Exists(Path.Combine(_root,
                asset.File.Replace('/', Path.DirectorySeparatorChar))), asset.File);
        Assert.Empty(AuthoredProjectValidator.Errors(project));

        // a malformed value refuses with the field's shape, and authors nothing
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());
        Assert.Contains("not 0 or 1", Assert.Throws<ArgumentException>(() =>
            session.ChooseMaterialValue(edit, flatten, "0.4")).Message);
        Assert.Contains("not four numbers", Assert.Throws<ArgumentException>(() =>
            session.ChooseMaterialValue(edit, colour, "0.5")).Message);
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));

        // revert removes the now-unauthored material-value position entirely
        session.ChooseTargetGameValue(edit, flatten);
        Assert.DoesNotContain(session.Snapshot().TargetSlots, slot => slot.Id == flatten);
        Assert.DoesNotContain(session.Snapshot().EditDefinitions
            .Single(candidate => candidate.Id == edit).Bindings, binding => binding.SlotId == flatten);
    }

    [Fact]
    public void Reapplying_a_shading_field_reuses_one_asset_and_one_stable_file()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.SetRootDir(_root);
        var hair = AuthoredEditFixtures.Hair;
        session.EnsurePartSlots(hair, Resolve);
        string edit = session.CreateEdit(hair);
        string slot = session.EnsureMaterialValueSlot(hair, 0,
            MaterialValueSemantics.UseGiFlatten, Resolve);

        session.ChooseMaterialValue(edit, slot, "0");
        var first = session.Snapshot();
        var firstAsset = first.ProjectAssets.Single(asset =>
            asset.Kind == ProjectAssetKind.StructuredValue);
        long revision = session.Revision;

        session.ChooseMaterialValue(edit, slot, "0");
        Assert.Equal(revision, session.Revision);
        session.ChooseMaterialValue(edit, slot, "1");

        var changed = session.Snapshot();
        var changedAsset = Assert.Single(changed.ProjectAssets,
            asset => asset.Kind == ProjectAssetKind.StructuredValue);
        Assert.Equal(firstAsset.Id, changedAsset.Id);
        Assert.Equal(firstAsset.File, changedAsset.File);
        Assert.Equal("1", changedAsset.Value!.Value);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "values"), "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public void Refused_shading_set_and_copy_leave_no_slot_asset_or_file()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.SetRootDir(_root);
        var hair = AuthoredEditFixtures.Hair;
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        Assert.Throws<KeyNotFoundException>(() => session.ApplyMaterialValues("missing-edit", hair, 0,
            new[] { new AuthoredMaterialValueEdit(MaterialValueSemantics.UseGiFlatten, "0") }, Resolve));
        Assert.Throws<KeyNotFoundException>(() => session.CopyMaterialValues("missing-edit", hair, 0,
            hair, 0, new[] { MaterialValueSemantics.UseGiFlatten }, Resolve));

        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
        Assert.False(Directory.Exists(Path.Combine(_root, "values")));
    }

    [Fact]
    public void Revert_then_save_removes_the_value_slot_asset_and_file()
    {
        var project = AuthoredEditFixtures.Golden();
        AuthoredProjectSerializer.Save(project, _root);
        var document = AuthoredProjectDocument.Load(_root);
        var session = Assert.IsType<AuthoredEditSession>(document.Session);
        var hair = AuthoredEditFixtures.Hair;
        session.EnsurePartSlots(hair, Resolve);
        string edit = session.CreateEdit(hair);
        string slot = session.EnsureMaterialValueSlot(hair, 0,
            MaterialValueSemantics.UseGiFlatten, Resolve);
        session.ChooseMaterialValue(edit, slot, "0");
        string file = session.Snapshot().ProjectAssets.Single(asset =>
            asset.Kind == ProjectAssetKind.StructuredValue).File;
        string full = Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full));

        session.ChooseTargetGameValue(edit, slot);
        document.Save();

        var saved = Assert.IsType<AuthoredProject>(document.Authored);
        Assert.DoesNotContain(saved.TargetSlots,
            candidate => candidate.Input == TargetInputKind.MaterialValue);
        Assert.DoesNotContain(saved.ProjectAssets,
            asset => asset.Kind == ProjectAssetKind.StructuredValue);
        Assert.False(File.Exists(full));
    }

    [Fact]
    public void Save_sweep_advances_revision_without_raising_a_changed_event()
    {
        var project = AuthoredEditFixtures.Golden();
        AuthoredProjectSerializer.Save(project, _root);
        var document = AuthoredProjectDocument.Load(_root);
        var session = Assert.IsType<AuthoredEditSession>(document.Session);
        var hair = AuthoredEditFixtures.Hair;
        session.EnsurePartSlots(hair, Resolve);
        string edit = session.CreateEdit(hair);
        string slot = session.EnsureMaterialValueSlot(hair, 0,
            MaterialValueSemantics.UseGiFlatten, Resolve);
        session.ChooseMaterialValue(edit, slot, "0");
        session.ChooseTargetGameValue(edit, slot);
        long beforeSave = session.Revision;
        int changedEvents = 0;
        session.Changed += (_, _) => changedEvents++;

        document.Save();

        Assert.Equal(beforeSave + 1, session.Revision);
        Assert.Equal(0, changedEvents);
    }

    [Fact]
    public void Save_sweep_preserves_foreign_value_files_while_removing_its_asset_and_stages()
    {
        var project = AuthoredEditFixtures.Golden();
        string sourceRoot = Path.Combine(_root, "source-project");
        string destination = Path.Combine(_root, "save-as-destination");
        AuthoredProjectSerializer.Save(project, sourceRoot);
        var document = AuthoredProjectDocument.Load(sourceRoot);
        var session = Assert.IsType<AuthoredEditSession>(document.Session);
        var hair = AuthoredEditFixtures.Hair;
        session.EnsurePartSlots(hair, Resolve);
        string edit = session.CreateEdit(hair);
        string slot = session.EnsureMaterialValueSlot(hair, 0,
            MaterialValueSemantics.UseGiFlatten, Resolve);
        session.ChooseMaterialValue(edit, slot, "0");
        string removed = Path.Combine(sourceRoot, session.Snapshot().ProjectAssets.Single(asset =>
                asset.Kind == ProjectAssetKind.StructuredValue).File
            .Replace('/', Path.DirectorySeparatorChar));
        string sourceStage = Path.Combine(sourceRoot, "values", ".leftover.stage");
        File.WriteAllText(sourceStage, "staged");
        string foreignDir = Path.Combine(destination, "values", "foreign-project");
        Directory.CreateDirectory(foreignDir);
        string foreign = Path.Combine(foreignDir, "keep.json");
        File.WriteAllText(foreign, "foreign");
        session.ChooseTargetGameValue(edit, slot);

        document.Save(destination);

        Assert.False(File.Exists(removed));
        Assert.False(File.Exists(sourceStage));
        Assert.True(File.Exists(foreign));
    }

    private void Materialize(AuthoredProject project)
    {
        foreach (var asset in project.ProjectAssets)
        {
            string file = Path.Combine(_root, asset.File.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, asset.Id);
        }
    }

    /// <summary>The hair as the current install answers for it: a tier, one material with a base colour, a
    /// normal and an effect overlay, and a second material carrying only a ramp. The cape resolves to an
    /// object the install cannot name exactly.</summary>
    private static LegacyResolvedPart? Resolve(TargetPart part)
    {
        if (part.RendererSlot == "c_vesna_cape_lod0")
            return new LegacyResolvedPart(part, Reference(70003, part.RendererSlot),
                new GameAssetRef { GameBuild = "26109", LogicalBundle = "", PathId = 0, Name = "cape_mesh" },
                Array.Empty<LegacyResolvedMaterial>());
        if (part.RendererSlot == "c_vesna_body_lod0")
            return new LegacyResolvedPart(part, Reference(70001, part.RendererSlot),
                Reference(72001, "body_mesh"),
                new[]
                {
                    new LegacyResolvedMaterial(0, "body_material", Reference(74001, "body_material"),
                        new[] { Texture(TargetInputKind.Ramp, 91001, "body_ramp") }),
                });
        if (part.RendererSlot != "c_vesna_hair_lod0") return null;
        return new LegacyResolvedPart(part, Reference(70002, part.RendererSlot),
            Reference(72002, "hair_mesh"),
            new[]
            {
                new LegacyResolvedMaterial(0, "hair_material", Reference(84001, "hair_material"),
                    new[]
                    {
                        Texture(TargetInputKind.BaseColor, 81001, "hair_base"),
                        Texture(TargetInputKind.Normal, 81002, "hair_normal"),
                        // A second texture on an input the project already has a place for: one slot per
                        // material position and input, as the adapter keys them.
                        Texture(TargetInputKind.BaseColor, 81003, "hair_base_alt"),
                        Texture(TargetInputKind.Unknown, 81004, "hair_mystery"),
                        // The effect overlay (_BlendTex): an ordinary picture slot of its own.
                        Texture(TargetInputKind.Blend, 81005, "hair_spc"),
                    }),
                new LegacyResolvedMaterial(1, "hair_tips", Reference(84002, "hair_tips"),
                    new[] { Texture(TargetInputKind.Ramp, 91003, "hair_ramp") }),
            },
            new[]
            {
                new LegacyResolvedTier("c_vesna_hair_lod1", "lod1", Reference(70012, "c_vesna_hair_lod1"),
                    Reference(72012, "hair_lod1_mesh")),
            });
    }

    private static LegacyResolvedTexture Texture(TargetInputKind input, long pathId, string name) =>
        new(input, "characters/vesna_ssr01", name, pathId, Reference(pathId, name));

    private static GameAssetRef Reference(long pathId, string name) => new()
    {
        GameBuild = "26109",
        LogicalBundle = "characters/vesna_ssr01",
        PathId = pathId,
        Name = name,
    };
}
