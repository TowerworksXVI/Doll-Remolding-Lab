using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The schema-2 persistence boundary: exact game identity, independent named edits, explicit
/// activations and the refusal to overwrite a released schema-1 project.</summary>
public sealed class AuthoredProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remold-authored-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Exact_refs_two_edits_and_the_Always_activation_round_trip()
    {
        var project = CompleteProject();

        string json = AuthoredProjectSerializer.Serialize(project);
        var loaded = AuthoredProjectSerializer.Deserialize(json);

        Assert.Contains("\"schema\": 2", json);
        Assert.Contains("\"kind\": \"project_asset\"", json);
        Assert.Contains("\"always\":", json);
        Assert.Equal(new[] { "edit-long", "edit-short" }, loaded.EditDefinitions.Select(e => e.Id));
        Assert.Equal("edit-long", Assert.Single(loaded.Always));

        var refs = loaded.ProjectAssets.Select(a => a.Source!.GameAsset!).ToArray();
        Assert.Equal("RampMap_Linear_RGBAHalf", refs[1].Name);
        Assert.Equal("RampMap_Linear_RGBAHalf", refs[2].Name);
        Assert.NotEqual(refs[1].PathId, refs[2].PathId);
    }

    [Fact]
    public void A_schema_2_manifest_saves_atomically_and_reopens()
    {
        string dir = Path.Combine(_root, "project");
        var project = CompleteProject();
        AuthoredProjectSerializer.Save(project, dir);

        var first = AuthoredProjectSerializer.Load(dir);
        Assert.Equal(Path.GetFullPath(dir), first.RootDir);
        Assert.False(File.Exists(ModProject.BackupPathFor(dir)));

        first.Always[0] = "edit-short";
        AuthoredProjectSerializer.Save(first, dir);

        Assert.Equal("edit-short", Assert.Single(AuthoredProjectSerializer.Load(dir).Always));
        Assert.False(File.Exists(ModProject.BackupPathFor(dir)));
    }

    [Fact]
    public void Saving_authored_intent_refuses_to_overwrite_a_schema_1_manifest()
    {
        string dir = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(dir);
        string file = ModProject.ManifestPathFor(dir);
        const string legacy = "{\"schema\":1,\"info\":{},\"selection\":[],\"targets\":[],\"hidden\":[],\"build_excluded\":[],\"change_keys\":[]}";
        File.WriteAllText(file, legacy);

        var error = Assert.Throws<InvalidOperationException>(() =>
            AuthoredProjectSerializer.Save(CompleteProject(), dir));

        Assert.Contains("refuses to overwrite", error.Message);
        Assert.Equal(legacy, File.ReadAllText(file));
        Assert.False(File.Exists(file + ".bak"));
    }

    [Fact]
    public void A_property_keyed_texture_slot_round_trips_in_schema_2()
    {
        var project = CompleteProject();
        var ramp = project.TargetSlots.Single(slot => slot.Id == "slot-ramp");
        project.TargetSlots.Add(new TargetSlot
        {
            Id = "slot-detail",
            Part = ramp.Part,
            Input = TargetInputKind.Texture,
            ShaderProperty = "_DetailAlbedo",
            SubmeshIndex = ramp.SubmeshIndex,
            MaterialSlotIndex = ramp.MaterialSlotIndex,
            Renderer = ramp.Renderer,
            Material = ramp.Material,
        });
        foreach (var edit in project.EditDefinitions)
            edit.Bindings.Add(new Binding { SlotId = "slot-detail", Kind = BindingKind.TargetGameValue });

        string json = AuthoredProjectSerializer.Serialize(project);
        var loaded = AuthoredProjectSerializer.Deserialize(json);

        Assert.Contains("\"input\": \"texture\"", json);
        Assert.Contains("\"shader_property\": \"_DetailAlbedo\"", json);
        Assert.Equal("_DetailAlbedo", loaded.TargetSlots.Single(slot => slot.Id == "slot-detail")
            .ShaderProperty);
    }

    [Fact]
    public void A_propertyless_project_keeps_shader_property_absent_from_json()
    {
        Assert.DoesNotContain("shader_property", AuthoredProjectSerializer.Serialize(CompleteProject()));
    }

    [Theory]
    [InlineData(TargetInputKind.Texture, "has no shader property")]
    [InlineData(TargetInputKind.Unknown, "has no input kind")]
    public void Unidentified_texture_rows_remain_invalid(TargetInputKind input, string expected)
    {
        var project = CompleteProject();
        var slot = project.TargetSlots.Single(candidate => candidate.Id == "slot-ramp");
        slot.Input = input;
        slot.ShaderProperty = null;

        Assert.Contains(AuthoredProjectValidator.Errors(project), error =>
            error.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_name_without_exact_game_identity_is_invalid()
    {
        var project = CompleteProject();
        project.TargetSlots[0].Mesh!.PathId = 0;

        var error = Assert.Throws<InvalidDataException>(() => AuthoredProjectSerializer.Serialize(project));

        // The message is the one sentence a damaged project earns; the per-field account rides underneath
        // it, where the log takes it and no surface reads it.
        Assert.Equal(AuthoredProjectSerializer.DamagedProject, error.Message);
        Assert.Contains("mesh has no path id", error.InnerException!.Message);
    }

    [Fact]
    public void Binding_payloads_and_asset_kinds_are_checked_before_resolution()
    {
        var project = CompleteProject();
        var rampBinding = project.EditDefinitions[0].Bindings.Single(b => b.SlotId == "slot-ramp");
        rampBinding.ProjectAssetId = "mesh-long";
        rampBinding.SourceSlot = new BindingSourceSlot { SlotId = "slot-ramp" };

        var errors = AuthoredProjectValidator.Errors(project);

        Assert.Contains(errors, e => e.Contains("binds Geometry to Ramp", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("source slot with a project asset", StringComparison.Ordinal));
    }

    /// <summary>What makes a source a game slot is its domain and never who filed it. A save files a part's
    /// game slots under the edit that answers the part, so from a project's first save onward the slot a
    /// pick-from-the-game source names is owned, and asking the owner refused that idiom outright. An edit's
    /// own output is the one thing that is not the game's, whoever holds it, and a source that does name an
    /// edit must name the edit the slot is actually filed under.</summary>
    [Fact]
    public void A_source_slot_is_a_game_slot_by_its_domain_and_not_by_who_holds_it()
    {
        // One edit answering the body, with the part's game slots filed under it: any saved project's shape.
        // Its second ramp position takes the game's own ramp from the first.
        AuthoredProject Filed()
        {
            var project = CompleteProject();
            project.EditDefinitions.RemoveAll(edit => edit.Id == "edit-short");
            project.ProjectAssets.RemoveAll(asset => asset.Id is "mesh-short" or "ramp-cool");
            var ramp = project.TargetSlots.Single(slot => slot.Id == "slot-ramp");
            project.TargetSlots.Add(new TargetSlot
            {
                Id = "slot-ramp-second",
                Part = ramp.Part,
                Tier = ramp.Tier,
                SubmeshIndex = 1,
                MaterialSlotIndex = 1,
                Input = TargetInputKind.Ramp,
                Renderer = ramp.Renderer,
                Material = ramp.Material,
            });
            foreach (var slot in project.TargetSlots) slot.OwnerEditId = "edit-long";
            project.EditDefinitions[0].Bindings.Add(new Binding
            {
                SlotId = "slot-ramp-second",
                Kind = BindingKind.SourceSlot,
                SourceSlot = new BindingSourceSlot { SlotId = "slot-ramp" },
            });
            return project;
        }

        Assert.Empty(AuthoredProjectValidator.Errors(Filed()));

        var output = Filed();
        var asOutput = output.TargetSlots.Single(slot => slot.Id == "slot-ramp");
        asOutput.Domain = TargetSlotDomain.EditOutput;
        asOutput.Material = null;
        Assert.Contains(AuthoredProjectValidator.Errors(output),
            e => e.Contains("names an edit-output source slot as a game slot", StringComparison.Ordinal));

        var named = Filed();
        named.EditDefinitions[0].Bindings.Single(b => b.SlotId == "slot-ramp-second").SourceSlot!
            .EditDefinitionId = "edit-long";
        named.EditDefinitions[0].Bindings.RemoveAll(binding => binding.SlotId == "slot-ramp");
        Assert.Contains(AuthoredProjectValidator.Errors(named),
            e => e.Contains("source slot is not bound by its named edit", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_authored_binding_kind_round_trips_without_a_capability_verdict()
    {
        var project = CompleteProject();
        project.EditDefinitions.RemoveAt(1);
        var part = project.EditDefinitions[0].Target;
        var renderer = project.TargetSlots[0].Renderer;
        var material = project.TargetSlots[1].Material;
        project.TargetSlots.AddRange(new[]
        {
            MaterialSlot("slot-normal", TargetInputKind.Normal, 0),
            MaterialSlot("slot-rmo", TargetInputKind.Rmo, 0),
            MaterialSlot("slot-source-alpha", TargetInputKind.RmoAlpha, 0),
            MaterialSlot("slot-target-alpha", TargetInputKind.RmoAlpha, 1),
            new TargetSlot
            {
                Id = "slot-visible", Part = part,
                Input = TargetInputKind.Visibility,
                Renderer = renderer,
            },
        });
        project.EditDefinitions[0].Bindings = new List<Binding>
        {
            new() { SlotId = "slot-geometry", Kind = BindingKind.ProjectAsset, ProjectAssetId = "mesh-long" },
            new() { SlotId = "slot-ramp", Kind = BindingKind.TargetGameValue },
            new() { SlotId = "slot-normal", Kind = BindingKind.Neutral },
            new() { SlotId = "slot-rmo", Kind = BindingKind.InheritedLiveCarrier },
            new() { SlotId = "slot-source-alpha", Kind = BindingKind.TargetGameValue },
            new()
            {
                SlotId = "slot-target-alpha", Kind = BindingKind.SourceSlot,
                SourceSlot = new BindingSourceSlot { SlotId = "slot-source-alpha" },
            },
        };
        project.EditDefinitions.Add(new EditDefinition
        {
            Id = "edit-hide",
            Kind = EditDefinitionKind.Hide,
            Target = part,
            Label = "Hidden",
            Bindings = new List<Binding>
            {
                new() { SlotId = "slot-visible", Kind = BindingKind.Hidden },
            },
        });

        TargetSlot MaterialSlot(string id, TargetInputKind input, int materialSlot) => new()
        {
            Id = id,
            Part = part,
            Input = input,
            SubmeshIndex = materialSlot,
            MaterialSlotIndex = materialSlot,
            Renderer = renderer,
            Material = material,
        };

        var loaded = AuthoredProjectSerializer.Deserialize(AuthoredProjectSerializer.Serialize(project));

        Assert.Equal(new[]
        {
            BindingKind.ProjectAsset,
            BindingKind.TargetGameValue,
            BindingKind.Neutral,
            BindingKind.InheritedLiveCarrier,
            BindingKind.TargetGameValue,
            BindingKind.SourceSlot,
        }, loaded.EditDefinitions[0].Bindings.Select(b => b.Kind));
        Assert.Equal(BindingKind.Hidden, loaded.EditDefinitions.Single(edit =>
            edit.Kind == EditDefinitionKind.Hide).Bindings.Single().Kind);
    }

    [Fact]
    public void An_edit_cannot_leave_a_relevant_slot_implicit_but_may_be_unplaced()
    {
        var project = CompleteProject();
        project.EditDefinitions[0].Bindings.RemoveAll(b => b.SlotId == "slot-ramp");
        project.Always.Clear();

        var errors = AuthoredProjectValidator.Errors(project);

        Assert.Contains(errors, e => e.Contains("no binding for target route represented by slot 'slot-ramp'",
            StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("placement", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Two game slots on one route. A part's second edit holds a copy of every slot another edit is
    /// filed under, so two slots on one game object are the ordinary shape — under two different edits.
    /// Within one edit they are the same place recorded twice, which is one edit answering for a game object
    /// more than once.</summary>
    [Fact]
    public void One_edit_cannot_bind_two_game_slots_on_one_route()
    {
        var project = CompleteProject();
        var geometry = project.TargetSlots.Single(slot => slot.Id == "slot-geometry");
        var repeat = new TargetSlot
        {
            Id = "slot-geometry-again",
            Part = geometry.Part,
            Tier = geometry.Tier,
            Input = geometry.Input,
            Renderer = geometry.Renderer,
            Mesh = geometry.Mesh,
        };
        project.TargetSlots.Add(repeat);

        // Duplicate records are harmless until one edit tries to answer the same structural place twice.
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        project.EditDefinitions.Single(edit => edit.Id == "edit-long").Bindings.Add(new Binding
        {
            SlotId = repeat.Id, Kind = BindingKind.ProjectAsset, ProjectAssetId = "mesh-long",
        });
        Assert.Contains(AuthoredProjectValidator.Errors(project),
            e => e.Contains("binds the same route through slots 'slot-geometry' and "
                + "'slot-geometry-again'", StringComparison.Ordinal));

        // Moving the second edit to the repeated record is admitted: bindings, not filing metadata, associate it.
        var first = project.EditDefinitions.Single(edit => edit.Id == "edit-long");
        first.Bindings.RemoveAll(binding => binding.SlotId == repeat.Id);
        var second = project.EditDefinitions.Single(edit => edit.Id == "edit-short");
        second.Bindings.Single(binding => binding.SlotId == geometry.Id).SlotId = repeat.Id;
        Assert.Empty(AuthoredProjectValidator.Errors(project));
    }

    [Fact]
    public void A_placement_must_name_an_existing_edit()
    {
        var project = CompleteProject();
        project.Always[0] = "missing-edit";

        var errors = AuthoredProjectValidator.Errors(project);

        Assert.Contains(errors, e => e.Contains("missing edit definition 'missing-edit'", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unplaced_edit_round_trips()
    {
        var project = CompleteProject();
        project.Always.Clear();

        var loaded = AuthoredProjectSerializer.Deserialize(AuthoredProjectSerializer.Serialize(project));

        Assert.Empty(loaded.Always);
        Assert.Equal(2, loaded.EditDefinitions.Count);
    }

    [Fact]
    public void A_key_group_round_trips_with_stable_states_and_hide_placements()
    {
        var project = CompleteProject();
        var group = project.KeyFirstPart("F7", startsOff: true, offState: CompositionState.Hidden);
        group.Label = "Body style";
        group.States[0].Label = "Hidden";
        group.States[1].Label = "Long";

        var loaded = AuthoredProjectSerializer.Deserialize(AuthoredProjectSerializer.Serialize(project));

        var actual = Assert.Single(loaded.KeyGroups);
        Assert.Equal("F7", actual.Key);
        Assert.Equal("Body style", actual.Label);
        Assert.Equal(new[] { "state-0001", "state-0002" }, actual.States.Select(state => state.Id));
        Assert.Equal(EditDefinitionKind.Hide, loaded.EditDefinitions.Single(edit =>
            edit.Id == Assert.Single(actual.States[0].ActiveEditIds)).Kind);
        Assert.Equal("edit-long", Assert.Single(actual.States[1].ActiveEditIds));
        Assert.Empty(loaded.Always);
    }

    [Fact]
    public void A_saved_schema_2_project_is_byte_stable_across_a_reload()
    {
        var project = CompleteProject();
        project.KeyFirstPart("F7", startsOff: true, offState: CompositionState.Hidden);

        string first = AuthoredProjectSerializer.Serialize(project);
        string second = AuthoredProjectSerializer.Serialize(
            AuthoredProjectSerializer.Deserialize(first));

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_pinned_schema_1_fixture_loads_through_the_released_reader()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Project", "golden", "legacy_project_v1.json");

        var loaded = ModProject.Load(fixture);

        Assert.Equal(1, loaded.Schema);
        Assert.Equal("Fixture mod", loaded.Info.Name);
        Assert.Equal(2, loaded.Targets.Count);
        Assert.Equal(73002, loaded.Targets[0].PathId);
        Assert.Equal("textures/body_0_ramp.dds", loaded.Targets[0].DonorTextures![0].Ramp);
        Assert.Equal("F7", loaded.ChangeKeys[0].Key);
        Assert.Equal("textures/stock_ramp.dds", Assert.Single(loaded.StockRamps!).Ramp);
    }

    [Fact]
    public void The_pinned_schema_2_fixture_deserializes_independently_of_the_writer()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Project", "golden", "authored_project_v2.json");

        var loaded = AuthoredProjectSerializer.Load(fixture);

        Assert.Equal(2, loaded.Schema);
        Assert.Equal(2, loaded.EditDefinitions.Count);
        Assert.Equal("edit-long", Assert.Single(loaded.Always));
        Assert.Equal(new long[] { 91001, 91002 }, loaded.ProjectAssets.Where(a => a.Kind == ProjectAssetKind.Ramp)
            .Select(a => a.Source!.GameAsset!.PathId));
    }

    [Fact]
    public void Legacy_workspace_normalization_keeps_a_blend_picture_record()
    {
        var project = CompleteProject();
        var ramp = project.TargetSlots.Single(slot => slot.Id == "slot-ramp");
        var blendGame = Game(91003, "body_blend");
        project.TargetSlots.Add(new TargetSlot
        {
            Id = "slot-blend",
            Part = ramp.Part,
            Input = TargetInputKind.Blend,
            Tier = ramp.Tier,
            SubmeshIndex = ramp.SubmeshIndex,
            MaterialSlotIndex = ramp.MaterialSlotIndex,
            Renderer = ramp.Renderer,
            Material = ramp.Material,
        });
        project.ProjectAssets.Add(Asset("blend-painted", ProjectAssetKind.Picture, "Painted effect",
            "textures/blend.png", blendGame));
        project.EditDefinitions[0].Bindings.Add(new Binding
        {
            SlotId = "slot-blend",
            Kind = BindingKind.ProjectAsset,
            ProjectAssetId = "blend-painted",
        });
        project.WorkspaceIndex = new AuthoredWorkspaceIndex
        {
            LegacyTargets = new List<ProjectTarget>
            {
                new()
                {
                    AssetType = "Texture2D",
                    Bundle = blendGame.LogicalBundle,
                    ObjectName = blendGame.Name!,
                    PathId = blendGame.PathId,
                    ReplaceFile = "textures/blend.png",
                },
            },
        };

        AuthoredWorkspaceNormalizer.Normalize(project);

        var record = Assert.Single(project.WorkspaceIndex.Records);
        Assert.Equal(ProjectAssetKind.Picture, record.Kind);
        Assert.Equal("slot-blend", record.SlotId);
        Assert.Equal("textures/blend.png", record.ProjectFile);
        Assert.Equal(blendGame.PathId, record.GameAsset.PathId);
        Assert.Null(project.WorkspaceIndex.LegacyTargets);
    }

    [Fact]
    public void Unknown_or_missing_schemas_are_refused_before_deserialization()
    {
        Assert.Throws<InvalidDataException>(() => AuthoredProjectSerializer.Deserialize("{\"schema\":1}"));
        Assert.Throws<InvalidDataException>(() => AuthoredProjectSerializer.Deserialize("{\"schema\":3}"));
        Assert.Throws<InvalidDataException>(() => AuthoredProjectSerializer.Deserialize("{}"));
    }

    private static AuthoredProject CompleteProject()
    {
        var part = Part();
        var renderer = Game(70001, "c_vesna_body_lod0");
        return new AuthoredProject
        {
            AppVersion = "0.4.0",
            Info = new ProjectInfo { Name = "Authored fixture", Author = "TestAuthor" },
            AuthoredAgainst = new AuthoredAgainst { CatalogVersion = "26109" },
            ProjectAssets = new List<ProjectAsset>
            {
                Asset("mesh-long", ProjectAssetKind.Geometry, "Long body", "meshes/long.glb", Game(73001, "body_mesh")),
                Asset("ramp-warm", ProjectAssetKind.Ramp, "Warm ramp", "textures/warm.dds", Game(91001, "RampMap_Linear_RGBAHalf")),
                Asset("ramp-cool", ProjectAssetKind.Ramp, "Cool ramp", "textures/cool.dds", Game(91002, "RampMap_Linear_RGBAHalf")),
                Asset("mesh-short", ProjectAssetKind.Geometry, "Short body", "meshes/short.glb", Game(73002, "body_mesh")),
            },
            TargetSlots = new List<TargetSlot>
            {
                new()
                {
                    Id = "slot-geometry", Part = part, Input = TargetInputKind.Geometry,
                    Tier = "lod0", Renderer = renderer, Mesh = Game(72001, "body_mesh"),
                },
                new()
                {
                    Id = "slot-ramp", Part = part, Input = TargetInputKind.Ramp, Tier = "lod0",
                    SubmeshIndex = 0, MaterialSlotIndex = 0, Renderer = renderer,
                    Material = Game(74001, "body_material"),
                },
            },
            EditDefinitions = new List<EditDefinition>
            {
                Edit("edit-long", "Long body", part, "mesh-long", "ramp-warm"),
                Edit("edit-short", "Short body", part, "mesh-short", "ramp-cool"),
            },
            Always = new List<string> { "edit-long" },
        };
    }

    private static TargetPart Part() => new()
    {
        Subject = "Vesna",
        Outfit = "VesnaSSR01",
        RendererSlot = "c_vesna_body_lod0",
    };

    private static GameAssetRef Game(long pathId, string name) => new()
    {
        GameBuild = "26109",
        LogicalBundle = "characters/vesna_ssr01",
        PathId = pathId,
        Name = name,
    };

    private static ProjectAsset Asset(string id, ProjectAssetKind kind, string label, string file,
        GameAssetRef source) => new()
        {
            Id = id,
            Kind = kind,
            Label = label,
            File = file,
            Source = new ProjectAssetSource { GameAsset = source },
        };

    private static EditDefinition Edit(string id, string label, TargetPart part, string mesh, string ramp) => new()
    {
        Id = id,
        Label = label,
        Target = part,
        Bindings = new List<Binding>
        {
            new() { SlotId = "slot-geometry", Kind = BindingKind.ProjectAsset, ProjectAssetId = mesh },
            new() { SlotId = "slot-ramp", Kind = BindingKind.ProjectAsset, ProjectAssetId = ramp },
        },
    };
}
