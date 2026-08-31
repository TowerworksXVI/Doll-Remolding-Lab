using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

public sealed class LegacyProjectAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-legacy-adapter-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void The_pinned_schema_1_project_adapts_without_mutating_it()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out string manifest);
        string before = File.ReadAllText(manifest);

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        Assert.True(adapted.Report.CanSave, string.Join("; ", adapted.Report.Items.Select(i => i.Detail)));
        Assert.Equal(before, File.ReadAllText(manifest));
        Assert.Equal(1, ModProject.Load(manifest).Schema);

        // The released unticked body edit is retained but receives no placement; its stale key is not kept.
        Assert.Empty(adapted.Project.KeyGroups);
        var bodyEdit = adapted.Project.EditDefinitions.Single(e => e.Kind == EditDefinitionKind.Content
            && e.Target.RendererSlot == "c_vesna_body_lod0");
        Assert.DoesNotContain(bodyEdit.Id, adapted.Project.Always);

        var hideEdit = adapted.Project.EditDefinitions.Single(edit => edit.Kind == EditDefinitionKind.Hide
            && edit.Target.RendererSlot == "c_vesna_hair_lod0");
        Assert.Contains(hideEdit.Id, adapted.Project.Always);
        Assert.Equal(EditDefinitionKind.Hide, hideEdit.Kind);
        Assert.Equal(BindingKind.Hidden, Assert.Single(hideEdit.Bindings).Kind);
        Assert.Contains(adapted.Project.Always, id => adapted.Project.EditDefinitions.Single(e => e.Id == id)
            .Target.RendererSlot == "c_vesna_coat_lod0");

        Assert.Equal(7, bodyEdit.Bindings.Count);
        Assert.Equal(2, bodyEdit.Bindings.Count(b => Slot(adapted.Project, b).Input == TargetInputKind.Geometry));
        Assert.Equal(BindingKind.ProjectAsset, Binding(bodyEdit, adapted.Project, TargetInputKind.BaseColor).Kind);
        Assert.Equal(BindingKind.Neutral, Binding(bodyEdit, adapted.Project, TargetInputKind.Normal).Kind);
        Assert.Equal(BindingKind.Neutral, Binding(bodyEdit, adapted.Project, TargetInputKind.Rmo).Kind);
        Assert.Equal(BindingKind.ProjectAsset, Binding(bodyEdit, adapted.Project, TargetInputKind.Ramp).Kind);

        Assert.Equal(4, adapted.Project.ProjectAssets.Count);
        Assert.DoesNotContain(adapted.Project.ProjectAssets, a => a.File == "textures/body_base.png");
        Assert.Equal(2, adapted.Report.Items.Count(i => i.Code == "slot.implicit_neutral"));
        Assert.Empty(AuthoredProjectValidator.Errors(adapted.Project));
    }

    [Fact]
    public void Byte_difference_state_outranks_the_recorded_edited_flag()
    {
        var legacy = LoadFixture(meshEdited: false, textureEdited: false, out _);
        legacy.Targets[0].Edited = true;

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        Assert.DoesNotContain(adapted.Project.ProjectAssets, a => a.Kind == ProjectAssetKind.Geometry);
        Assert.DoesNotContain(adapted.Project.Always, id => adapted.Project.EditDefinitions.Any(e => e.Id == id
            && e.Target.RendererSlot == "c_vesna_body_lod0"));
        Assert.Empty(adapted.Project.KeyGroups);
        Assert.Contains(adapted.Report.Items, i => i.Code == "build.inactive_exclusion");
        Assert.Contains(adapted.Report.Items, i => i.Code == "build.inactive_key");
    }

    [Fact]
    public void An_unedited_mesh_does_not_resolve_or_block_migration()
    {
        var legacy = LoadFixture(meshEdited: false, textureEdited: false, out _);
        int bodyResolutions = 0;

        var adapted = LegacyProjectAdapter.Adapt(legacy, part =>
        {
            if (part.RendererSlot == "c_vesna_body_lod0") bodyResolutions++;
            return ResolveFixturePart(part);
        });

        Assert.Equal(0, bodyResolutions);
        Assert.True(adapted.Report.CanSave, string.Join("; ", adapted.Report.Items.Select(i => i.Detail)));
        Assert.DoesNotContain(adapted.Report.Items,
            i => i.Scope.Contains("c_vesna_body_lod0", StringComparison.OrdinalIgnoreCase) && i.BlocksSave);
    }

    [Fact]
    public void Unresolved_current_identity_blocks_migration_and_keeps_the_explicit_files()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: true, out string manifest);
        string before = File.ReadAllText(manifest);

        var adapted = LegacyProjectAdapter.Adapt(legacy, _ => null);

        Assert.False(adapted.Report.CanSave);
        Assert.Contains(adapted.Report.Items, i => i.Code == "identity.part" && i.BlocksSave);
        Assert.Contains(adapted.Project.ProjectAssets, a => a.File == "meshes/body.glb");
        Assert.Contains(adapted.Project.ProjectAssets, a => a.File == "textures/body_base.png");
        Assert.Contains(adapted.Project.ProjectAssets, a => a.File == "textures/stock_ramp.dds");
        Assert.Equal(before, File.ReadAllText(manifest));
    }

    [Fact]
    public void A_missing_workspace_file_is_blocking_unresolved_intent()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);
        File.Delete(legacy.Resolve("meshes/body.glb"));

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        Assert.False(adapted.Report.CanSave);
        Assert.Contains(adapted.Report.Items, i => i.Code == "edit.file_absent" && i.BlocksSave);
        Assert.Contains(adapted.Project.ProjectAssets, a => a.File == "meshes/body.glb");
    }

    [Fact]
    public void A_legacy_path_id_does_not_match_a_texture_from_another_bundle()
    {
        var legacy = LoadFixture(meshEdited: false, textureEdited: true, out _);

        var adapted = LegacyProjectAdapter.Adapt(legacy, part =>
        {
            var resolved = ResolveFixturePart(part);
            if (resolved is null || part.RendererSlot != "c_vesna_body_lod0") return resolved;
            var material = Assert.Single(resolved.Materials);
            var texture = Assert.Single(material.Textures);
            return resolved with
            {
                Materials = new[]
                {
                    material with { Textures = new[] { texture with { LegacyBundle = "another.bundle" } } },
                },
            };
        });

        Assert.True(adapted.Report.CanSave,
            string.Join("; ", adapted.Report.Items.Select(item => item.Detail)));
        Assert.Contains(adapted.Report.Items, i => i.Code == "texture.binding" && !i.BlocksSave);
        Assert.DoesNotContain(adapted.Project.ProjectAssets,
            asset => asset.File == "textures/body_base.png");
    }

    [Fact]
    public void Authored_rmo_alpha_becomes_its_own_project_value()
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "mesh.glb"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "mesh-original.glb"), new byte[] { 2 });
        File.WriteAllBytes(Path.Combine(dir, "rmo.png"), new byte[] { 3 });
        var legacy = new ModProject
        {
            RootDir = dir,
            AuthoredAgainst = new AuthoredAgainst { CatalogVersion = "26109" },
            Selection = new List<SelectionEntry>
            {
                new() { Character = "Vesna", Outfit = "VesnaSSR01" },
            },
            Targets = new List<ProjectTarget>
            {
                new()
                {
                    AssetType = "Mesh", Bundle = "old-body.bundle", ObjectName = "c_vesna_body_lod0",
                    PathId = 73002, SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
                    ReplaceFile = "mesh.glb", OriginalFile = "mesh-original.glb",
                    DonorTextures = new List<SubmeshTextures>
                    {
                        new() { Submesh = 0, Rmo = "rmo.png", RmoAlpha = RmoAlphaAnswer.ShipAsAuthored },
                    },
                },
            },
        };

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        var alpha = Assert.Single(adapted.Project.ProjectAssets,
            a => a.Kind == ProjectAssetKind.StructuredValue);
        var rmo = Assert.Single(adapted.Project.ProjectAssets,
            a => a.Kind == ProjectAssetKind.Picture && a.File == "rmo.png");
        Assert.Equal(rmo.Id, alpha.Source!.ProjectAssetId);
        var edit = Assert.Single(adapted.Project.EditDefinitions);
        Assert.Equal(alpha.Id, Binding(edit, adapted.Project, TargetInputKind.RmoAlpha).ProjectAssetId);
        Assert.DoesNotContain(adapted.Report.Items, i => i.Code == "rmo_alpha.absent");
        Assert.True(adapted.Report.CanSave, string.Join("; ", adapted.Report.Items.Select(i => i.Detail)));
    }

    /// <summary>The pinned corpus project, adapted and planned end to end on the route a released mod
    /// actually takes. It is the one fixture standing on real recorded data rather than a hand-built one,
    /// so it is where a conversion that silently drops an input shows up.</summary>
    [Fact]
    public void The_pinned_legacy_project_adapts_and_plans()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);
        legacy.BuildExcluded.Clear();
        var body = legacy.Targets.Single(t => t.AssetType == "Mesh"
            && t.ObjectName == "c_vesna_body_lod0");
        var row = Assert.Single(body.DonorTextures!);
        row.Rmo = "textures/body_0_rmo.png";
        File.WriteAllBytes(legacy.Resolve(row.Rmo), new byte[] { 7 });
        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        var plan = AuthoredBuildPlanner.Plan(adapted.Project,
            new AuthoredBuildPlannerTests.Backend());

        Assert.True(adapted.Report.CanSave,
            string.Join(Environment.NewLine, adapted.Report.Items.Select(i => i.Detail)));
        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        Assert.Contains(plan.Bindings, binding => binding.AuthoredSlot.Input == TargetInputKind.RmoAlpha);
        Assert.Contains(plan.ProjectArtifacts,
            artifact => artifact.File.EndsWith("body_0_rmo.png", StringComparison.Ordinal));
    }

    [Fact]
    public void Current_install_resolution_mints_exact_renderer_mesh_material_and_texture_refs()
    {
        string dir = Path.Combine(_root, "resolver");
        Directory.CreateDirectory(dir);
        string lod0File = Path.Combine(dir, "lod0.bundle");
        string lod1File = Path.Combine(dir, "lod1.bundle");
        SyntheticBundle.BuildOneMesh(lod0File, "c_vesna_body_lod0",
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 });
        SyntheticBundle.BuildOneMesh(lod1File, "c_vesna_body_lod1",
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 2, 1 });
        var bytes = new Dictionary<string, byte[]>
        {
            ["mesh-lod0.bundle"] = File.ReadAllBytes(lod0File),
            ["mesh-lod1.bundle"] = File.ReadAllBytes(lod1File),
        };
        var route = new TargetPart
        {
            Subject = "Vesna",
            Outfit = "VesnaSSR01",
            RendererSlot = "c_vesna_body_lod0",
        };
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab,
            new[]
            {
                new SubjectPart("body", route.RendererSlot, "addr-lod0",
                    new[]
                    {
                        new SubjectMaterial("body_material", 74001, "cab-body",
                            new[] { new SubjectMap("_BaseMap", "body_base", "textures.bundle", 81001) },
                            Bundle: "materials.bundle"),
                    },
                    SiblingTiers: new[]
                    {
                        new Remold.Core.Export.RecipeTierSlot("c_vesna_body_lod1", "addr-lod1",
                            RendererBundle: "prefab.bundle", RendererPathId: 70002),
                    },
                    RendererBundle: "prefab.bundle", RendererPathId: 70001),
            }, null, Array.Empty<string>());
        var env = new BuildEnv(
            (character, outfit) => character == "Vesna" && outfit == "VesnaSSR01" ? model : null,
            address => address == "addr-lod0" ? "mesh-lod0.bundle"
                : address == "addr-lod1" ? "mesh-lod1.bundle" : null,
            bundle => bytes.GetValueOrDefault(bundle),
            CatalogVersion: "26109", AppVersion: "test");

        var resolved = new LegacyProjectResolver(env).ResolvePart(route)!;
        var reader = new BundleReader();
        long lod0Path = Assert.Single(reader.ListAssets(bytes["mesh-lod0.bundle"], BundleReader.ClassMesh)).PathId;
        long lod1Path = Assert.Single(reader.ListAssets(bytes["mesh-lod1.bundle"], BundleReader.ClassMesh)).PathId;

        Assert.Equal(("prefab.bundle", 70001L),
            (resolved.Renderer.LogicalBundle, resolved.Renderer.PathId));
        Assert.Equal(("mesh-lod0.bundle", lod0Path),
            (resolved.Mesh.LogicalBundle, resolved.Mesh.PathId));
        var material = Assert.Single(resolved.Materials);
        Assert.Equal(("materials.bundle", 74001L),
            (material.Material.LogicalBundle, material.Material.PathId));
        var texture = Assert.Single(material.Textures);
        Assert.Equal(TargetInputKind.BaseColor, texture.Input);
        Assert.Equal(("textures.bundle", 81001L),
            (texture.Texture.LogicalBundle, texture.Texture.PathId));
        var tier = Assert.Single(resolved.Tiers!);
        Assert.Equal(("prefab.bundle", 70002L),
            (tier.Renderer.LogicalBundle, tier.Renderer.PathId));
        Assert.Equal(("mesh-lod1.bundle", lod1Path),
            (tier.Mesh.LogicalBundle, tier.Mesh.PathId));
    }

    [Fact]
    public void One_resolver_instance_resolves_each_target_part_once()
    {
        string dir = Path.Combine(_root, "resolver-memo");
        Directory.CreateDirectory(dir);
        string bundleFile = Path.Combine(dir, "body.bundle");
        SyntheticBundle.BuildOneMesh(bundleFile, "c_vesna_body_lod0",
            new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }, new[] { 0, 1, 2 });
        byte[] bundle = File.ReadAllBytes(bundleFile);
        long pathId = Assert.Single(new BundleReader().ListAssets(bundle, BundleReader.ClassMesh)).PathId;
        var route = new TargetPart
        {
            Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "c_vesna_body_lod0",
        };
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab,
            new[]
            {
                new SubjectPart("body", route.RendererSlot, "", Array.Empty<SubjectMaterial>(),
                    MeshBundle: "body.bundle", MeshPathId: pathId,
                    RendererBundle: "prefab.bundle", RendererPathId: 70001),
            }, null, Array.Empty<string>());
        int subjectResolutions = 0, bundleReads = 0;
        var env = new BuildEnv(
            (character, outfit) =>
            {
                subjectResolutions++;
                return model;
            },
            _ => null,
            logical =>
            {
                bundleReads++;
                return logical == "body.bundle" ? bundle : null;
            },
            CatalogVersion: "26109", AppVersion: "test");
        var resolver = new LegacyProjectResolver(env);

        var first = resolver.ResolvePart(route);
        var second = resolver.ResolvePart(new TargetPart
        {
            Subject = "vesna", Outfit = "vesnassr01", RendererSlot = "C_VESNA_BODY_LOD0",
        });

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, subjectResolutions);
        Assert.Equal(1, bundleReads);   // includes the one MaterialIndexCounts mesh parse
    }

    [Fact]
    public void Changes_sharing_one_key_become_one_group_over_every_part_they_answer_for()
    {
        var legacy = KeyedFixture(coatKey: "F7", coatStartsOff: false);

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        var group = Assert.Single(adapted.Project.KeyGroups);
        Assert.Equal("F7", group.Key);
        Assert.Equal(2, group.States.Count);
        Assert.Equal(new[] { "c_vesna_body_lod0", "c_vesna_coat_lod0" },
            group.States[0].ActiveEditIds.Select(id => adapted.Project.EditDefinitions.Single(e => e.Id == id)
                .Target.RendererSlot).OrderBy(s => s));
        Assert.Empty(group.States[1].ActiveEditIds);
        Assert.DoesNotContain(adapted.Project.Always, id => adapted.Project.EditDefinitions.Any(e => e.Id == id
            && e.Target.RendererSlot is "c_vesna_body_lod0" or "c_vesna_coat_lod0"));
        Assert.Empty(AuthoredProjectValidator.Errors(adapted.Project));
    }

    [Fact]
    public void A_shared_key_starts_off_only_when_every_change_on_it_asks_to()
    {
        var agreeing = LegacyProjectAdapter.Adapt(
            KeyedFixture(coatKey: "F7", coatStartsOff: true), ResolveFixturePart);
        var disagreeing = LegacyProjectAdapter.Adapt(
            KeyedFixture(coatKey: "F7", coatStartsOff: false), ResolveFixturePart);

        Assert.Empty(Assert.Single(agreeing.Project.KeyGroups).States[0].ActiveEditIds);
        Assert.Equal(2, Assert.Single(agreeing.Project.KeyGroups).States[1].ActiveEditIds.Count);
        Assert.DoesNotContain(agreeing.Report.Items, item => item.Code == "build.key_start");
        Assert.Equal(2, Assert.Single(disagreeing.Project.KeyGroups).States[0].ActiveEditIds.Count);
        Assert.Empty(Assert.Single(disagreeing.Project.KeyGroups).States[1].ActiveEditIds);
        var note = Assert.Single(disagreeing.Report.Items, item => item.Code == "build.key_start");
        Assert.Equal(MigrationDisposition.Inferred, note.Disposition);
    }

    [Fact]
    public void Two_keys_are_two_groups_and_only_a_replace_returns_to_hidden()
    {
        var legacy = KeyedFixture(coatKey: "F8", coatStartsOff: false);
        legacy.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna_body_lod0", EditVerbs.Replace,
            "F7", hideWhenOff: true, startsOff: true);
        legacy.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna_coat_lod0", EditVerbs.Ramp,
            "F8", hideWhenOff: true, startsOff: false);

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        Assert.Equal(2, adapted.Project.KeyGroups.Count);
        var body = adapted.Project.KeyGroups.Single(group => group.Key == "F7");
        var coat = adapted.Project.KeyGroups.Single(group => group.Key == "F8");
        Assert.Equal(EditDefinitionKind.Hide, adapted.Project.EditDefinitions.Single(edit =>
            edit.Id == Assert.Single(body.States[0].ActiveEditIds)).Kind);
        Assert.Equal(EditDefinitionKind.Content, adapted.Project.EditDefinitions.Single(edit =>
            edit.Id == Assert.Single(body.States[1].ActiveEditIds)).Kind);
        Assert.Empty(coat.States[1].ActiveEditIds);
        Assert.Contains(adapted.Report.Items, item => item.Code == "build.off_state"
            && item.Disposition == MigrationDisposition.Inferred);
    }

    [Fact]
    public void A_key_that_cannot_be_read_leaves_its_part_always_on_and_blocks()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);
        legacy.BuildExcluded.Clear();
        // Added directly: the released setter normalizes, so a key this shape only ever reaches the
        // adapter from a manifest written by hand or by an older build.
        legacy.ChangeKeys.Clear();
        legacy.ChangeKeys.Add(new ChangeKey
        {
            Character = "Vesna",
            Outfit = "VesnaSSR01",
            Mesh = "c_vesna_body_lod0",
            Verb = EditVerbs.Replace,
            Key = "F7 CTRL",
        });

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        Assert.Empty(adapted.Project.KeyGroups);
        Assert.Contains(adapted.Project.Always, id => adapted.Project.EditDefinitions.Single(edit => edit.Id == id)
            .Target.RendererSlot == "c_vesna_body_lod0");
        var refusal = Assert.Single(adapted.Report.Items, item => item.Code == "build.key");
        Assert.Equal(MigrationDisposition.Unsupported, refusal.Disposition);
        Assert.False(adapted.Report.CanSave);
    }

    [Fact]
    public void A_change_left_out_of_the_build_leaves_the_key_its_peers_stay_on()
    {
        var legacy = KeyedFixture(coatKey: "F7", coatStartsOff: true);
        legacy.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna_body_lod0", EditVerbs.Replace, true);

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        Assert.True(adapted.Report.CanSave);
        var note = Assert.Single(adapted.Report.Items, item => item.Code == "build.inactive_key");
        Assert.Equal(MigrationDisposition.Inferred, note.Disposition);
        Assert.Contains("c_vesna_body_lod0", note.Scope, StringComparison.Ordinal);
        var group = Assert.Single(adapted.Project.KeyGroups);
        Assert.Equal("c_vesna_coat_lod0",
            adapted.Project.EditDefinitions.Single(edit => edit.Id == Assert.Single(group.States[1].ActiveEditIds))
                .Target.RendererSlot);
        var body = adapted.Project.EditDefinitions.Single(edit =>
            edit.Target.RendererSlot == "c_vesna_body_lod0" && edit.Kind == EditDefinitionKind.Content);
        Assert.DoesNotContain(body.Id, adapted.Project.Always);
        Assert.DoesNotContain(group.States.SelectMany(state => state.ActiveEditIds), id => id == body.Id);
    }

    [Fact]
    public void A_key_whose_changes_are_all_left_out_retains_unplaced_edits_and_no_group()
    {
        var legacy = KeyedFixture(coatKey: "F7", coatStartsOff: true);
        legacy.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna_body_lod0", EditVerbs.Replace, true);
        legacy.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna_coat_lod0", EditVerbs.Ramp, true);

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        Assert.True(adapted.Report.CanSave);
        Assert.DoesNotContain(adapted.Report.Items, item => item.Code == "build.key_inclusion");
        Assert.Empty(adapted.Project.KeyGroups);
        Assert.DoesNotContain(adapted.Project.Always, id => adapted.Project.EditDefinitions.Any(edit =>
            edit.Id == id && edit.Target.RendererSlot is "c_vesna_body_lod0" or "c_vesna_coat_lod0"));
        Assert.Equal(2, adapted.Project.EditDefinitions.Count(edit => edit.Kind == EditDefinitionKind.Content));
    }

    /// <summary>A donor row that recorded "keep the toon ramp the game already binds". The decision reaches
    /// schema 2 as the project asking the installed game by address: a source slot naming the part's own ramp
    /// at that material position, with no edit definition of its own. It comes back through the compatibility
    /// surface as the same decision the released manifest held, which is what the ramp conversion reads to
    /// leave the row alone on every later open.</summary>
    [Fact]
    public void A_recorded_keep_the_games_ramp_becomes_the_parts_own_ramp_slot_and_comes_back()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);
        legacy.Targets[0].DonorTextures![0].KeepOwnRamp();

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveBodyWithRamp);

        Assert.True(adapted.Report.CanSave, string.Join("; ", adapted.Report.Items.Select(i => i.Detail)));
        var edit = adapted.Project.EditDefinitions.Single(e =>
            e.Target.RendererSlot == "c_vesna_body_lod0" && e.Kind == EditDefinitionKind.Content);
        var game = Assert.Single(adapted.Project.TargetSlots, slot =>
            slot.Part.RendererSlot == "c_vesna_body_lod0" && slot.Domain == TargetSlotDomain.Game
            && slot.Input == TargetInputKind.Ramp);
        Assert.Equal(0, game.MaterialSlotIndex);
        Assert.Equal(BindingKind.TargetGameValue, edit.Bindings.Single(b => b.SlotId == game.Id).Kind);
        var output = OutputRamp(edit, adapted.Project);
        Assert.Equal(BindingKind.SourceSlot, output.Kind);
        Assert.Equal(game.Id, output.SourceSlot!.SlotId);
        Assert.Null(output.SourceSlot.EditDefinitionId);
        // and the row the compiler folds out of those bindings names no ramp file, with the decision on it
        var row = Assert.Single(AuthoredDonorRows.Rows(DonorRows(edit, adapted.Project))!);
        Assert.Null(row.Ramp);
        Assert.Equal(SlotOrigin.VanillaOwn, row.RampOrigin);
    }

    [Fact]
    public void Adapted_content_edit_uses_the_session_default_label()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        var body = adapted.Project.EditDefinitions.Single(edit =>
            edit.Kind == EditDefinitionKind.Content
            && edit.Target.RendererSlot == "c_vesna_body_lod0");
        Assert.Equal("Edit 1", body.Label);
        Assert.NotEqual(body.Target.RendererSlot, body.Label);
        Assert.All(adapted.Project.EditDefinitions.Where(edit => edit.Kind == EditDefinitionKind.Hide),
            edit => Assert.Equal("Hidden", edit.Label));
    }

    [Fact]
    public void A_texture_only_adaptation_gets_the_parts_full_game_slots()
    {
        var legacy = LoadFixture(meshEdited: false, textureEdited: true, out _);

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveBodyWithRamp);

        Assert.True(adapted.Report.CanSave,
            string.Join("; ", adapted.Report.Items.Select(item => item.Detail)));
        var edit = adapted.Project.EditDefinitions.Single(candidate =>
            candidate.Target.RendererSlot == "c_vesna_body_lod0"
            && candidate.Kind == EditDefinitionKind.Content);
        var slots = edit.Bindings.Select(binding =>
            (Slot: Slot(adapted.Project, binding), Binding: binding)).ToList();
        var game = slots.Where(item => item.Slot.Domain == TargetSlotDomain.Game).ToList();

        var expected = new[]
        {
            ("lod0", TargetInputKind.Geometry, (int?)null),
            ("lod1", TargetInputKind.Geometry, null),
            (null, TargetInputKind.BaseColor, 0),
            (null, TargetInputKind.Ramp, 0),
        };
        Assert.Equal(expected.OrderBy(item => item.Item2).ThenBy(item => item.Item1),
            game.Select(item =>
                    (item.Slot.Tier, item.Slot.Input, item.Slot.MaterialSlotIndex))
                .OrderBy(item => item.Input).ThenBy(item => item.Tier));
        Assert.Equal(BindingKind.ProjectAsset,
            game.Single(item => item.Slot.Input == TargetInputKind.BaseColor).Binding.Kind);
        Assert.All(game.Where(item => item.Slot.Input != TargetInputKind.BaseColor),
            item => Assert.Equal(BindingKind.TargetGameValue, item.Binding.Kind));
        Assert.Single(edit.Bindings, binding => binding.Kind == BindingKind.ProjectAsset);
    }

    [Fact]
    public void Mixed_mesh_and_stock_retexture_skips_the_replaced_part_but_keeps_texture_only_parts()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: true, out _);
        var texture = legacy.Targets.Single(target =>
            string.Equals(target.AssetType, "Texture2D", StringComparison.OrdinalIgnoreCase));
        texture.Users!.Add("c_vesna_coat_lod0");

        LegacyResolvedPart? Resolve(TargetPart part)
        {
            var resolved = ResolveFixturePart(part);
            if (resolved is null || part.RendererSlot != "c_vesna_coat_lod0") return resolved;
            var material = Assert.Single(resolved.Materials);
            return resolved with
            {
                Materials = new[]
                {
                    material with
                    {
                        Textures = new[]
                        {
                            new LegacyResolvedTexture(TargetInputKind.BaseColor,
                                "characters/vesna_ssr01_textures", "body_base", 81001,
                                Game(81001, "body_base", "characters/vesna_ssr01_textures")),
                        },
                    },
                },
            };
        }

        var adapted = LegacyProjectAdapter.Adapt(legacy, Resolve);

        Assert.True(adapted.Report.CanSave,
            string.Join("; ", adapted.Report.Items.Select(item => item.Detail)));
        var body = adapted.Project.EditDefinitions.Single(edit =>
            edit.Kind == EditDefinitionKind.Content
            && edit.Target.RendererSlot == "c_vesna_body_lod0");
        Assert.DoesNotContain(body.Bindings.Select(binding =>
                (Binding: binding, Slot: Slot(adapted.Project, binding))), item =>
            item.Slot.Domain == TargetSlotDomain.Game
            && item.Slot.Input is TargetInputKind.BaseColor
                or TargetInputKind.Normal or TargetInputKind.Rmo
            && item.Binding.Kind != BindingKind.TargetGameValue);

        var coat = adapted.Project.EditDefinitions.Single(edit =>
            edit.Kind == EditDefinitionKind.Content
            && edit.Target.RendererSlot == "c_vesna_coat_lod0");
        var coatBase = coat.Bindings.Select(binding =>
                (Binding: binding, Slot: Slot(adapted.Project, binding)))
            .Single(item => item.Slot.Domain == TargetSlotDomain.Game
                && item.Slot.Input == TargetInputKind.BaseColor);
        Assert.Equal(BindingKind.ProjectAsset, coatBase.Binding.Kind);
        Assert.Contains(adapted.Project.ProjectAssets,
            asset => asset.File == "textures/body_base.png");
    }

    /// <summary>An edited texture reaches the parts the CURRENT ROSTER binds it on, which is the live join
    /// the released derivation shipped. <see cref="ProjectTarget.Users"/> is the workspace's own
    /// accumulation and names only the part this project replaces — the released build still retextured the
    /// coat, and a conversion reading the recorded users would drop the edit entirely.</summary>
    [Fact]
    public void An_edited_texture_lands_on_the_roster_parts_that_bind_it_not_the_recorded_users()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: true, out _);
        var texture = legacy.Targets.Single(target =>
            string.Equals(target.AssetType, "Texture2D", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "c_vesna_body_lod0" }, texture.Users);

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveWithCoatWearingTheBodyTexture,
            (character, outfit) => character == "Vesna" && outfit == "VesnaSSR01"
                ? new[] { "c_vesna_body_lod0", "c_vesna_hair_lod0", "c_vesna_coat_lod0" }
                : (IReadOnlyList<string>)Array.Empty<string>());

        Assert.True(adapted.Report.CanSave,
            string.Join("; ", adapted.Report.Items.Select(item => item.Detail)));
        var coat = adapted.Project.EditDefinitions.Single(edit =>
            edit.Kind == EditDefinitionKind.Content
            && edit.Target.RendererSlot == "c_vesna_coat_lod0");
        var coatBase = coat.Bindings.Select(binding =>
                (Binding: binding, Slot: Slot(adapted.Project, binding)))
            .Single(item => item.Slot.Domain == TargetSlotDomain.Game
                && item.Slot.Input == TargetInputKind.BaseColor);
        Assert.Equal(BindingKind.ProjectAsset, coatBase.Binding.Kind);
        Assert.Contains(adapted.Project.ProjectAssets, asset => asset.File == "textures/body_base.png");
        // and the part whose geometry this project replaces is NAMED, not skipped in silence
        Assert.Contains(adapted.Report.Items, item => item.Code == "texture.replaced"
            && item.Scope.Contains("c_vesna_body_lod0", StringComparison.Ordinal) && !item.BlocksSave);
    }

    /// <summary>One released file standing behind TWO current game textures becomes two project assets:
    /// schema 1 overrode the file wherever the install bound it, and schema 2 gives each object a picture of
    /// its own. The split is NAMED, because it changes what the modder's next edit of that picture does.</summary>
    [Fact]
    public void One_released_file_over_two_current_textures_splits_and_the_report_names_both()
    {
        var legacy = LoadFixture(meshEdited: false, textureEdited: true, out _);

        LegacyResolvedPart? Resolve(TargetPart part)
        {
            var resolved = ResolveFixturePart(part);
            if (resolved is null || part.RendererSlot != "c_vesna_body_lod0") return resolved;
            // Two materials, both reaching what the released manifest recorded as ONE texture; this
            // install answers with two distinct objects.
            return resolved with
            {
                Materials = new[]
                {
                    Assert.Single(resolved.Materials),
                    new LegacyResolvedMaterial(1, "body_material_alt",
                        Game(74009, "body_material_alt", "characters/vesna_ssr01_materials"),
                        new[]
                        {
                            new LegacyResolvedTexture(TargetInputKind.BaseColor,
                                "characters/vesna_ssr01_textures", "body_base", 81001,
                                Game(81009, "body_base_alt", "characters/vesna_ssr01_textures")),
                        }),
                },
            };
        }

        var adapted = LegacyProjectAdapter.Adapt(legacy, Resolve);

        Assert.True(adapted.Report.CanSave,
            string.Join("; ", adapted.Report.Items.Select(item => item.Detail)));
        var pictures = adapted.Project.ProjectAssets
            .Where(asset => asset.File == "textures/body_base.png").ToList();
        Assert.Equal(2, pictures.Count);
        Assert.Equal(new long[] { 81001, 81009 },
            pictures.Select(asset => asset.Source!.GameAsset!.PathId).OrderBy(id => id));
        var split = Assert.Single(adapted.Report.Items, item => item.Code == "asset.split");
        Assert.Equal(MigrationDisposition.Inferred, split.Disposition);
        Assert.Equal("textures/body_base.png", split.Scope);
        Assert.Contains("body_base", split.Detail, StringComparison.Ordinal);
        Assert.Contains("body_base_alt", split.Detail, StringComparison.Ordinal);
    }

    /// <summary>The fixture roster with the coat wearing the body's base colour — one texture two parts
    /// bind, which is the shape the released derivation fanned a retexture across.</summary>
    private static LegacyResolvedPart? ResolveWithCoatWearingTheBodyTexture(TargetPart part)
    {
        var resolved = ResolveFixturePart(part);
        if (resolved is null || part.RendererSlot != "c_vesna_coat_lod0") return resolved;
        var material = Assert.Single(resolved.Materials);
        return resolved with
        {
            Materials = new[]
            {
                material with
                {
                    Textures = new[]
                    {
                        new LegacyResolvedTexture(TargetInputKind.BaseColor,
                            "characters/vesna_ssr01_textures", "body_base", 81001,
                            Game(81001, "body_base", "characters/vesna_ssr01_textures")),
                    },
                },
            },
        };
    }

    /// <summary>The same row with nothing recorded about its ramp. It reaches schema 2 inheriting the live
    /// carrier and comes back as the question it was, which is the state the conversion is still free to
    /// fill. The two answers a released manifest can hold stay two answers.</summary>
    [Fact]
    public void An_unanswered_ramp_comes_back_as_a_question_rather_than_a_decision()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);
        legacy.Targets[0].DonorTextures![0].SetRamp(null);

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveBodyWithRamp);

        var edit = adapted.Project.EditDefinitions.Single(e =>
            e.Target.RendererSlot == "c_vesna_body_lod0" && e.Kind == EditDefinitionKind.Content);
        Assert.Equal(BindingKind.InheritedLiveCarrier, OutputRamp(edit, adapted.Project).Kind);
        var game = Assert.Single(adapted.Project.TargetSlots, slot =>
            slot.Part.RendererSlot == "c_vesna_body_lod0" && slot.Domain == TargetSlotDomain.Game
            && slot.Input == TargetInputKind.Ramp);
        Assert.Equal(BindingKind.TargetGameValue,
            edit.Bindings.Single(binding => binding.SlotId == game.Id).Kind);
        // the same fold leaves the row at the question rather than at a decision
        var row = Assert.Single(AuthoredDonorRows.Rows(DonorRows(edit, adapted.Project))!);
        Assert.Null(row.Ramp);
        Assert.Equal(SlotOrigin.None, row.RampOrigin);
    }

    /// <summary>A keep decision on a material this install binds no toon ramp at. The released surface could
    /// not have produced it: the decision is given on a ramp card, which exists only where the material has a
    /// ramp. So it is named as an omitted inference rather than answered by minting a place the game would
    /// never read, and the row keeps saving with its slot back at the question.</summary>
    [Fact]
    public void A_keep_decision_on_a_material_with_no_ramp_is_named_rather_than_invented()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);
        legacy.Targets[0].DonorTextures![0].KeepOwnRamp();

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);

        Assert.True(adapted.Report.CanSave, string.Join("; ", adapted.Report.Items.Select(i => i.Detail)));
        var omitted = Assert.Single(adapted.Report.Items, item => item.Code == "ramp.keep_own");
        Assert.Equal(MigrationDisposition.Inferred, omitted.Disposition);
        Assert.DoesNotContain(adapted.Project.TargetSlots, slot =>
            slot.Part.RendererSlot == "c_vesna_body_lod0" && slot.Domain == TargetSlotDomain.Game
            && slot.Input == TargetInputKind.Ramp);
        var edit = adapted.Project.EditDefinitions.Single(e =>
            e.Target.RendererSlot == "c_vesna_body_lod0" && e.Kind == EditDefinitionKind.Content);
        Assert.Equal(BindingKind.InheritedLiveCarrier, OutputRamp(edit, adapted.Project).Kind);
    }

    /// <summary>A stock ramp pick already answering the material the keep decision names. The pick owns that
    /// game slot, so pointing the row at it would turn "keep whatever the game binds" into the pick's own
    /// file; the decision is named as omitted instead and the slot stays a question.</summary>
    [Fact]
    public void A_keep_decision_defers_to_a_stock_ramp_pick_on_the_same_material()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);
        legacy.Targets[0].DonorTextures![0].KeepOwnRamp();
        legacy.SetStockRamp("Vesna", "VesnaSSR01", "c_vesna_body_lod0", "body_material",
            "textures/stock_ramp.dds");

        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveBodyWithRamp);

        Assert.Contains(adapted.Report.Items, item => item.Code == "ramp.keep_own"
            && item.Detail.Contains("A picked toon ramp already answers", StringComparison.Ordinal));
        var edit = adapted.Project.EditDefinitions.Single(e =>
            e.Target.RendererSlot == "c_vesna_body_lod0" && e.Kind == EditDefinitionKind.Content);
        Assert.Equal(BindingKind.InheritedLiveCarrier, OutputRamp(edit, adapted.Project).Kind);
        Assert.Empty(AuthoredProjectValidator.Errors(adapted.Project));
    }

    /// <summary>The keep decision as the PLAN states it. The released build emitted nothing for a row that
    /// keeps the game's ramp; the plan says the same effective value out loud, as a binding resolved to the
    /// part's own installed ramp by address. What the compiler reads is unchanged — the row it folds names
    /// no ramp file — so nothing ships that did not ship before.</summary>
    [Fact]
    public void The_keep_decision_is_a_named_plan_binding_on_the_parts_own_ramp()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);
        legacy.BuildExcluded.Clear();
        legacy.Targets[0].DonorTextures![0].KeepOwnRamp();
        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveBodyWithRamp);

        var plan = AuthoredBuildPlanner.Plan(adapted.Project, new AuthoredBuildPlannerTests.Backend());

        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        var ramp = Assert.Single(plan.Bindings, binding =>
            binding.AuthoredSlot.Input == TargetInputKind.Ramp
            && binding.AuthoredSlot.Domain == TargetSlotDomain.EditOutput);
        Assert.Equal(BuildPlanVerdict.Resolved, ramp.Decision.Verdict);
        Assert.Null(ramp.EffectiveValue!.ProjectAsset);
        var edit = adapted.Project.EditDefinitions.Single(e =>
            e.Target.RendererSlot == "c_vesna_body_lod0" && e.Kind == EditDefinitionKind.Content);
        Assert.Null(Assert.Single(AuthoredDonorRows.Rows(DonorRows(edit, adapted.Project))!).Ramp);
    }

    /// <summary>The pinned resolver with one toon ramp added to the body's material, which is what a keep
    /// decision presupposes: the released surface only offers the decision where the material has one.
    /// </summary>
    private static LegacyResolvedPart? ResolveBodyWithRamp(TargetPart part)
    {
        var resolved = ResolveFixturePart(part);
        if (resolved is null || part.RendererSlot != "c_vesna_body_lod0") return resolved;
        var material = resolved.Materials[0];
        return resolved with
        {
            Materials = new[]
            {
                material with
                {
                    Textures = material.Textures.Append(new LegacyResolvedTexture(TargetInputKind.Ramp,
                            "characters/vesna_ssr01_textures", "body_ramp", 91001,
                            Game(91001, "body_ramp", "characters/vesna_ssr01_textures")))
                        .ToArray(),
                },
            },
        };
    }

    /// <summary>What one edit asks of its replacement's OWN toon-ramp place. Separate from the general lookup
    /// because a keep decision leaves the edit holding two ramp bindings: the game slot it names by address,
    /// and the replacement output that names it.</summary>
    /// <summary>One edit's bindings as the runtime compiler folds them: the same rows
    /// <see cref="AuthoredDonorRows"/> reads to mint a work item's per-submesh textures.</summary>
    private static List<EditOutputRow> DonorRows(EditDefinition edit, AuthoredProject project) =>
        edit.Bindings.Select(binding => new EditOutputRow(binding, Slot(project, binding),
            binding.ProjectAssetId is null ? null
                : project.ProjectAssets.SingleOrDefault(a => a.Id == binding.ProjectAssetId))).ToList();

    private static Binding OutputRamp(EditDefinition edit, AuthoredProject project) =>
        edit.Bindings.Single(b => Slot(project, b).Input == TargetInputKind.Ramp
            && Slot(project, b).Domain == TargetSlotDomain.EditOutput);

    /// <summary>The released adapter still reads every fixed donor field, while a newly recorded replacement
    /// inventories the installed target material. This fixture's material binds only Base, so a new layout
    /// must not recreate the legacy row's absent Normal, RMO and Ramp places.</summary>
    [Fact]
    public void A_new_replacement_layout_uses_installed_properties_not_legacy_fixed_fields()
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);
        var adapted = LegacyProjectAdapter.Adapt(legacy, ResolveFixturePart);
        var body = adapted.Project.EditDefinitions.Single(edit =>
            edit.Target.RendererSlot == "c_vesna_body_lod0" && edit.Kind == EditDefinitionKind.Content);
        var slots = adapted.Project.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        var filed = body.Bindings.Select(binding => slots[binding.SlotId])
            .Where(slot => slot.Domain == TargetSlotDomain.EditOutput).ToList();
        Assert.NotEmpty(filed);

        // The same project with the adapter's own output slots taken back out, so the session mints them
        // against exactly the state a save would otherwise have left behind.
        var stripped = adapted.Project;
        var removed = filed.Select(slot => slot.Id).ToHashSet(StringComparer.Ordinal);
        stripped.TargetSlots.RemoveAll(slot => removed.Contains(slot.Id));
        foreach (var edit in stripped.EditDefinitions)
            edit.Bindings.RemoveAll(binding => removed.Contains(binding.SlotId));
        var session = new AuthoredEditSession(stripped);

        session.RecordReplacementOutputs(body.Id, filed.Max(slot => slot.SubmeshIndex!.Value) + 1);

        var snapshot = session.Snapshot();
        var recordedSlots = snapshot.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        var recordedEdit = snapshot.EditDefinitions.Single(edit => edit.Id == body.Id);
        var recorded = recordedEdit.Bindings.Select(binding => recordedSlots[binding.SlotId])
            .Where(slot => slot.Domain == TargetSlotDomain.EditOutput)
            .Select(Address).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new[]
        {
            TargetInputKind.BaseColor, TargetInputKind.Normal, TargetInputKind.Rmo, TargetInputKind.Ramp,
        }, filed.Select(slot => slot.Input));
        var actual = Assert.Single(recorded);
        Assert.Contains("/BaseColor/", actual, StringComparison.Ordinal);
        Assert.Contains(Address(filed.Single(slot => slot.Input == TargetInputKind.BaseColor)), recorded);
    }

    /// <summary>Everything a slot addresses, less the id nothing downstream reads it by.</summary>
    private static string Address(TargetSlot slot) =>
        $"{slot.Part.Key}/{slot.Tier}/{slot.SubmeshIndex}/{slot.MaterialSlotIndex}/{slot.Input}/"
        + $"{slot.ShaderProperty}/{slot.Domain}/{slot.Semantic}/{Ref(slot.Renderer)}/{Ref(slot.Mesh)}/"
        + Ref(slot.Material);

    private static string Ref(GameAssetRef? value) => value is null
        ? "-" : $"{value.GameBuild}:{value.LogicalBundle}:{value.PathId}";

    /// <summary>The pinned schema-1 project with its build exclusion cleared and a second change put on
    /// the coat, so one key can be made to span two parts.</summary>
    private ModProject KeyedFixture(string coatKey, bool coatStartsOff)
    {
        var legacy = LoadFixture(meshEdited: true, textureEdited: false, out _);
        legacy.BuildExcluded.Clear();
        legacy.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna_coat_lod0", EditVerbs.Ramp,
            coatKey, hideWhenOff: false, startsOff: coatStartsOff);
        return legacy;
    }

    private ModProject LoadFixture(bool meshEdited, bool textureEdited, out string manifest)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Project", "golden", "legacy_project_v1.json");
        string dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        manifest = ModProject.ManifestPathFor(dir);
        File.Copy(source, manifest);
        var project = ModProject.Load(dir);

        WritePair(dir, "meshes/body.glb", "originals/body.glb", meshEdited);
        WritePair(dir, "textures/body_base.png", "originals/body_base.png", textureEdited);
        File.WriteAllBytes(Path.Combine(dir, "textures/body_0_base.png"), new byte[] { 4 });
        File.WriteAllBytes(Path.Combine(dir, "textures/body_0_ramp.dds"), new byte[] { 5 });
        File.WriteAllBytes(Path.Combine(dir, "textures/stock_ramp.dds"), new byte[] { 6 });
        return project;
    }

    private static void WritePair(string root, string replacement, string original, bool differs)
    {
        string replacePath = Path.Combine(root, replacement);
        string originalPath = Path.Combine(root, original);
        Directory.CreateDirectory(Path.GetDirectoryName(replacePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        File.WriteAllBytes(replacePath, differs ? new byte[] { 1, 2 } : new byte[] { 1 });
        File.WriteAllBytes(originalPath, new byte[] { 1 });
    }

    private static LegacyResolvedPart? ResolveFixturePart(TargetPart part)
    {
        if (part.Subject != "Vesna" || part.Outfit != "VesnaSSR01") return null;
        var renderer = Game(70000 + Math.Abs(part.RendererSlot.GetHashCode(StringComparison.Ordinal)) % 1000,
            part.RendererSlot, "characters/vesna_ssr01_prefab");
        var mesh = Game(part.RendererSlot == "c_vesna_body_lod0" ? 73002 : 72000,
            part.RendererSlot + "_mesh", "characters/vesna_ssr01_meshes");
        if (part.RendererSlot == "c_vesna_body_lod0")
        {
            return new LegacyResolvedPart(part, renderer, mesh,
                new[]
                {
                    new LegacyResolvedMaterial(0, "body_material",
                        Game(74001, "body_material", "characters/vesna_ssr01_materials"),
                        new[]
                        {
                            new LegacyResolvedTexture(TargetInputKind.BaseColor,
                                "characters/vesna_ssr01_textures", "body_base", 81001,
                                Game(81001, "body_base", "characters/vesna_ssr01_textures")),
                        }),
                },
                new[]
                {
                    new LegacyResolvedTier("c_vesna_body_lod1", "lod1",
                        Game(70002, "c_vesna_body_lod1", "characters/vesna_ssr01_prefab"),
                        Game(73003, "c_vesna_body_lod1_mesh", "characters/vesna_ssr01_meshes")),
                });
        }
        if (part.RendererSlot == "c_vesna_coat_lod0")
        {
            return new LegacyResolvedPart(part, renderer, mesh,
                new[]
                {
                    new LegacyResolvedMaterial(0, "coat_material",
                        Game(74002, "coat_material", "characters/vesna_ssr01_materials"),
                        Array.Empty<LegacyResolvedTexture>()),
                });
        }
        if (part.RendererSlot == "c_vesna_hair_lod0")
            return new LegacyResolvedPart(part, renderer, mesh, Array.Empty<LegacyResolvedMaterial>());
        return null;
    }

    private static SubjectModel? ResolveFixtureSubject(string character, string outfit)
    {
        if (character != "Vesna" || outfit != "VesnaSSR01") return null;
        return new SubjectModel(character, outfit, SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_vesna_body_lod0", "body-address", new[]
            {
                new SubjectMaterial("body_material", 74001, "body-cab", new[]
                {
                    new SubjectMap("_BaseMap", "body_base", "characters/vesna_ssr01_textures",
                        81001),
                }),
            }),
            new SubjectPart("hair", "c_vesna_hair_lod0", "hair-address",
                Array.Empty<SubjectMaterial>()),
            new SubjectPart("coat", "c_vesna_coat_lod0", "coat-address", new[]
            {
                new SubjectMaterial("coat_material", 74002, "coat-cab",
                    Array.Empty<SubjectMap>()),
            }),
        }, null, Array.Empty<string>());
    }

    private static GameAssetRef Game(long pathId, string name, string bundle) => new()
    {
        GameBuild = "26109",
        LogicalBundle = bundle,
        PathId = pathId,
        Name = name,
    };

    private static Binding Binding(EditDefinition edit, AuthoredProject project, TargetInputKind input) =>
        edit.Bindings.Single(b => Slot(project, b).Input == input
            && Slot(project, b).Domain == TargetSlotDomain.EditOutput);

    private static TargetSlot Slot(AuthoredProject project, Binding binding) =>
        project.TargetSlots.Single(s => s.Id == binding.SlotId);
}
