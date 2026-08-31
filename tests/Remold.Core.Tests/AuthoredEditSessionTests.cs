using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Project;
using Remold.Core.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

public sealed class AuthoredEditSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-authored-edit-" + Guid.NewGuid().ToString("N"));

    public AuthoredEditSessionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Return_warnings_serialize_and_clear_only_when_the_edits_geometry_changes()
    {
        var session = new AuthoredEditSession(Fixture());
        session.SetReturnWarning("edit-long", "Blender kept an extra UV layer.");

        var loaded = AuthoredProjectSerializer.Deserialize(
            AuthoredProjectSerializer.Serialize(session.Snapshot()));
        Assert.Equal("Blender kept an extra UV layer.", loaded.EditDefinitions.Single(edit =>
            edit.Id == "edit-long").ReturnWarning);
        Assert.Equal("Blender kept an extra UV layer.", session.Outline().Edits.Single(edit =>
            edit.Id == "edit-long").ReturnWarning);

        string mapSlot = session.Slots("edit-long").First(state =>
            state.Slot.Input != TargetInputKind.Geometry
            && state.Binding.Kind != BindingKind.TargetGameValue).Slot.Id;
        session.ChooseTargetGameValue("edit-long", mapSlot);
        Assert.NotNull(session.Snapshot().EditDefinitions.Single(edit => edit.Id == "edit-long").ReturnWarning);
        session.ChooseTargetGameValue("edit-long", "slot-geometry");
        Assert.Null(session.Snapshot().EditDefinitions.Single(edit => edit.Id == "edit-long").ReturnWarning);
    }

    [Fact]
    public void Hide_is_a_first_class_placement_and_does_not_delete_the_content_edit()
    {
        var project = Fixture();
        project.EditDefinitions.RemoveAll(e => e.Id != "edit-long");
        var target = project.EditDefinitions[0].Target;
        var session = new AuthoredEditSession(project);

        session.UnplaceEdit("edit-long");
        Assert.Equal(CompositionState.Vanilla, session.Part(target).State);
        string hideId = session.AddHideEdit(target);
        Assert.Equal(CompositionState.Hidden, session.Part(target).State);
        var hidden = session.Snapshot().EditDefinitions.Single(edit =>
            edit.Kind == EditDefinitionKind.Hide);
        Assert.Equal(hideId, hidden.Id);
        Assert.Equal(hidden.Id, session.Part(target).EditDefinitionId);
        Assert.Equal(BindingKind.Hidden, Assert.Single(hidden.Bindings).Kind);
        session.UnplaceEdit(hidden.Id);
        session.PlaceEdit("edit-long");
        Assert.Equal(CompositionState.Edit, session.Part(target).State);
        Assert.Equal("edit-long", session.Part(target).EditDefinitionId);
        Assert.Equal(2, session.Snapshot().EditDefinitions.Count);
    }

    [Fact]
    public void Publishing_changes_only_the_acted_on_binding_and_game_identity_stays_immutable()
    {
        var project = Fixture();
        project.RootDir = _root;
        var rendererBefore = project.TargetSlots.Single(s => s.Id == "slot-geometry").Renderer;
        string currentAsset = Binding(project, "edit-long", "slot-geometry").ProjectAssetId!;
        string currentFile = project.ProjectAssets.Single(asset => asset.Id == currentAsset).File;
        string canonical = Path.Combine(_root, currentFile);
        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        File.WriteAllBytes(canonical, new byte[] { 1 });
        var session = new AuthoredEditSession(project);
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-geometry");
        File.WriteAllBytes(ingress.ReturnArtifact, new byte[] { 2 });

        var published = session.PublishAssetForBinding(ingress, ProjectAssetKind.Geometry,
            "Alternate body", ProjectAssetIngress.Binary);
        var snapshot = session.Snapshot();

        Assert.Equal(ProjectAssetPublishResult.Published, published.Result);
        Assert.Equal(published.ProjectAssetId,
            Binding(snapshot, "edit-long", "slot-geometry").ProjectAssetId);
        Assert.Equal("mesh-short", Binding(snapshot, "edit-short", "slot-geometry").ProjectAssetId);
        var rendererAfter = snapshot.TargetSlots.Single(s => s.Id == "slot-geometry").Renderer;
        Assert.Equal((rendererBefore.GameBuild, rendererBefore.LogicalBundle, rendererBefore.PathId),
            (rendererAfter.GameBuild, rendererAfter.LogicalBundle, rendererAfter.PathId));
    }

    [Fact]
    public void Geometry_picture_ramp_and_rmo_alpha_use_the_same_binding_commands()
    {
        var project = FixtureWithPictureSlots();
        var session = new AuthoredEditSession(project);

        session.ChooseTargetGameValue("edit-long", "slot-base");
        session.ChooseNeutral("edit-long", "slot-normal");
        session.ChooseInheritedCarrier("edit-long", "slot-rmo");
        session.ChooseProjectAsset("edit-long", "slot-rmo-alpha", "alpha-authored");
        session.ChooseProjectAsset("edit-long", "slot-ramp", "ramp-cool");

        var slots = session.Slots("edit-long").ToDictionary(s => s.Slot.Input);
        Assert.Equal(BindingKind.ProjectAsset, slots[TargetInputKind.Geometry].Binding.Kind);
        Assert.Equal(BindingKind.TargetGameValue, slots[TargetInputKind.BaseColor].Binding.Kind);
        Assert.Equal(BindingKind.Neutral, slots[TargetInputKind.Normal].Binding.Kind);
        Assert.Equal(BindingKind.InheritedLiveCarrier, slots[TargetInputKind.Rmo].Binding.Kind);
        Assert.Equal("alpha-authored", slots[TargetInputKind.RmoAlpha].ProjectAsset!.Id);
        Assert.Equal("ramp-cool", slots[TargetInputKind.Ramp].ProjectAsset!.Id);
    }

    [Fact]
    public void An_invalid_publish_transaction_leaves_no_asset_or_binding_residue()
    {
        var project = Fixture();
        project.RootDir = _root;
        var binding = Binding(project, "edit-long", "slot-geometry");
        binding.Kind = BindingKind.TargetGameValue;
        binding.ProjectAssetId = null;
        var session = new AuthoredEditSession(project);
        string source = Path.Combine(_root, "wrong-kind.png");
        WritePng(source, 20);
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-geometry", source);
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        Assert.Throws<InvalidDataException>(() => session.PublishAssetForBinding(ingress,
            ProjectAssetKind.Picture, "Wrong kind", ProjectAssetIngress.Png));

        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    /// <summary>An edit's own replacement output is the one place with no game value behind it to ask for.
    /// The refusal is on the slot's domain, not on whether anyone is filed against it: the same slot is
    /// unowned when a session mints it and owned from the first save on, so ownership would answer a
    /// different question every time.</summary>
    [Fact]
    public void An_edits_own_output_slot_has_no_game_value_to_ask_for()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.WithOwnedSlots());
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());

        var error = Assert.Throws<InvalidDataException>(() =>
            session.ChooseTargetGameValue("edit-long", "slot-owned"));

        Assert.Contains("asks an edit-output slot for a target game value", error.Message);
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
    }

    [Fact]
    public void Material_source_is_a_reviewable_batch_and_only_accepted_binding_rows_apply()
    {
        var project = FixtureWithPictureSlots();
        var session = new AuthoredEditSession(project);
        var proposal = new MaterialSourceProposal("edit-long", "cloth material", new[]
        {
            new MaterialSourceDifference("slot-base", "Base colour", MaterialDifferenceDisposition.Binding,
                "Use the source picture", new Binding
                {
                    SlotId = "slot-base", Kind = BindingKind.ProjectAsset, ProjectAssetId = "base-source",
                }),
            new MaterialSourceDifference("slot-rmo", "Wetness", MaterialDifferenceDisposition.DynamicLive,
                "The game continues to own this value"),
            new MaterialSourceDifference("slot-normal", "Shader keyword",
                MaterialDifferenceDisposition.Unsupported, "The current backend cannot transfer it"),
        });

        session.AcceptMaterialSource(proposal, new[] { "slot-base" });
        var snapshot = session.Snapshot();

        Assert.Equal("base-source", Binding(snapshot, "edit-long", "slot-base").ProjectAssetId);
        Assert.Equal(BindingKind.InheritedLiveCarrier, Binding(snapshot, "edit-long", "slot-rmo").Kind);
        Assert.Throws<InvalidOperationException>(() =>
            session.AcceptMaterialSource(proposal, new[] { "slot-normal" }));
        Assert.Throws<KeyNotFoundException>(() =>
            session.AcceptMaterialSource(proposal, new[] { "slot-not-reviewed" }));
    }

    [Fact]
    public void Publish_binds_exactly_one_slot()
    {
        var project = FixtureWithPictureSlots();
        project.RootDir = _root;
        Binding(project, "edit-short", "slot-base").ProjectAssetId = "base-source";
        string canonical = Path.Combine(_root, "textures", "base.png");
        WritePng(canonical, 10);
        var session = new AuthoredEditSession(project);
        var changes = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => changes.Add(change);
        string dropped = Path.Combine(_root, "drops", "replacement.png");
        WritePng(dropped, 20);
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-base", dropped);

        var published = session.PublishAssetForBinding(ingress, ProjectAssetKind.Picture,
            "Body base colour", ProjectAssetIngress.Png);
        var snapshot = session.Snapshot();

        Assert.Equal(ProjectAssetPublishResult.Published, published.Result);
        Assert.NotEqual("base-source", Binding(snapshot, "edit-long", "slot-base").ProjectAssetId);
        Assert.Equal("base-source", Binding(snapshot, "edit-short", "slot-base").ProjectAssetId);
        Assert.Equal(Pixel(10), ReadPixel(canonical));
        Assert.Equal(Pixel(20), ReadPixel(Path.Combine(_root, published.ProjectRelativeFile!)));
        var change = Assert.Single(changes);
        Assert.Equal(1, change.Revision);
        Assert.Contains("edit-long", change.EditDefinitionIds);
        Assert.Contains("slot-base", change.SlotIds);
        Assert.True(change.Invalidation.HasFlag(AuthoredInvalidation.Assets));
        Assert.True(change.Invalidation.HasFlag(AuthoredInvalidation.Preview));
    }

    [Fact]
    public void Cow_splits_a_shared_asset_on_first_mutation_and_unchanged_bytes_author_nothing()
    {
        var project = FixtureWithPictureSlots();
        project.RootDir = _root;
        Binding(project, "edit-short", "slot-base").ProjectAssetId = "base-source";
        string canonical = Path.Combine(_root, "textures", "base.png");
        WritePng(canonical, 30);
        var session = new AuthoredEditSession(project);
        int changes = 0;
        session.Changed += (_, _) => changes++;
        var unchanged = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-base");

        Assert.Equal(ProjectAssetPublishResult.Unchanged,
            session.PublishAssetForBinding(unchanged, ProjectAssetKind.Picture,
                "Body base colour", ProjectAssetIngress.Png).Result);
        Assert.Equal(0, changes);

        var changed = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-base");
        WritePng(changed.OutboundSnapshot, 31);
        var result = session.PublishAssetForBinding(changed, ProjectAssetKind.Picture,
            "Body base colour", ProjectAssetIngress.Png);
        var snapshot = session.Snapshot();

        Assert.Equal(1, changes);
        Assert.Equal("base-source", Binding(snapshot, "edit-short", "slot-base").ProjectAssetId);
        Assert.Equal(result.ProjectAssetId, Binding(snapshot, "edit-long", "slot-base").ProjectAssetId);
        Assert.Equal(Pixel(30), ReadPixel(canonical));
    }

    /// <summary>The transaction's promise is all or nothing, and what a caller SAYS when it refuses is
    /// "nothing was changed". Everything the transport is owed once the intent is live — its snapshots
    /// refreshed, its baseline advanced — runs after the change is announced and the slot is rebound, so a
    /// throw out of that half would make that sentence a lie over a change that fully happened.
    ///
    /// <para>The measured shape: an image editor is holding the outbound snapshot open, which is the file
    /// the app hands it and the one this publish then tries to refresh. The publish must SUCCEED.</para>
    ///
    /// <para>Mutation-proven: letting the after-commit copies out of their own catch makes this throw, with
    /// the asset published, the binding moved and the change already raised.</para></summary>
    [Fact]
    public void A_locked_transport_snapshot_cannot_take_a_committed_publish_down_with_it()
    {
        var project = FixtureWithPictureSlots();
        project.RootDir = _root;
        string canonical = Path.Combine(_root, "textures", "base.png");
        WritePng(canonical, 40);
        var session = new AuthoredEditSession(project);
        int changes = 0;
        session.Changed += (_, _) => changes++;
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-base");
        WritePng(ingress.OutboundSnapshot, 41);

        // What an image editor holding the file does: others may READ it, nobody may write over it. That is
        // exactly the after-commit refresh's shape and not the normalization's, which only reads.
        ExactAssetPublishResult published;
        using (File.Open(ingress.OutboundSnapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
            published = session.PublishAssetForBinding(ingress, ProjectAssetKind.Picture,
                "Body base colour", ProjectAssetIngress.Png);

        Assert.Equal(ProjectAssetPublishResult.Published, published.Result);
        Assert.Equal(1, changes);
        Assert.Equal(published.ProjectAssetId,
            Binding(session.Snapshot(), "edit-long", "slot-base").ProjectAssetId);
        Assert.Equal(Pixel(41), ReadPixel(Path.Combine(_root, published.ProjectRelativeFile!)));

        // …and the transport is still usable: the baseline advanced past the lock, so the same open editor
        // saving again is an ordinary second publish rather than a refusal.
        WritePng(ingress.OutboundSnapshot, 42);
        var again = session.PublishAssetForBinding(ingress, ProjectAssetKind.Picture,
            "Body base colour", ProjectAssetIngress.Png);
        Assert.Equal(ProjectAssetPublishResult.Published, again.Result);
        Assert.Equal(2, changes);
        Assert.Equal(Pixel(42), ReadPixel(Path.Combine(_root, again.ProjectRelativeFile!)));
    }

    /// <summary>A transaction that refuses leaves the folder as it found it, FOLDERS included: what it
    /// minted on the way — an edit's assets folder, a transport's edit and slot levels — goes back out with
    /// the files, so a modder opening the mod folder never finds a tree named for a batch that never
    /// happened. What was already there is never touched, whichever way the batch went.</summary>
    [Fact]
    public void A_refused_batch_takes_back_the_folders_it_minted_and_no_others()
    {
        var project = FixtureWithPictureSlots();
        project.RootDir = _root;
        WritePng(Path.Combine(_root, "textures", "base.png"), 50);
        var session = new AuthoredEditSession(project);
        string dropped = Path.Combine(_root, "drops", "replacement.png");
        WritePng(dropped, 51);
        string standing = Path.Combine(_root, ProjectAssetIngress.DirectoryName, "sources");
        Directory.CreateDirectory(standing);

        Assert.Throws<InvalidDataException>(() => session.Compound(change =>
        {
            var ingress = change.BeginIngress("edit-long", "slot-base", dropped);
            change.PublishAssetForBinding(ingress, ProjectAssetKind.Picture, "Dropped",
                ProjectAssetIngress.Png);
            throw new InvalidDataException("the batch refuses after its files landed");
        }));

        Assert.False(Directory.Exists(Path.Combine(_root, ProjectAssetIngress.DirectoryName, "edit-long")),
            "the refused batch left its transport folder behind");
        Assert.False(Directory.Exists(Path.Combine(_root, "assets", "edits", "edit-long")),
            "the refused batch left an assets folder behind");
        // …and nothing it did not mint moved: an empty folder that was already there, and the canonical
        // bytes the publish was going to replace
        Assert.True(Directory.Exists(standing), "the rollback swept a folder it never minted");
        Assert.Equal(Pixel(50), ReadPixel(Path.Combine(_root, "textures", "base.png")));
    }

    /// <summary>Handing bytes over skips the round trip's machinery but not its ANSWER: the carried
    /// identity has to decide changed-or-not exactly as decoding the result would, or a re-send of maps
    /// nobody repainted would mint a fresh copy of every one of them.
    ///
    /// <para>And the fast arm is refused on a LENT transport, whose return artifact is the only copy of
    /// what an outside program sent back.</para></summary>
    [Fact]
    public void Handed_over_bytes_publish_by_their_carried_identity_and_a_lent_transport_refuses_them()
    {
        var project = FixtureWithPictureSlots();
        project.RootDir = _root;
        WritePng(Path.Combine(_root, "textures", "base.png"), 60);
        var session = new AuthoredEditSession(project);
        int changes = 0;
        session.Changed += (_, _) => changes++;

        var published = Publish(61);
        Assert.Equal(ProjectAssetPublishResult.Published, published.Result);
        Assert.Equal(Pixel(61), ReadPixel(Path.Combine(_root, published.ProjectRelativeFile!)));
        Assert.Equal(1, changes);

        // the very same picture again: nothing was repainted, so nothing is authored
        Assert.Equal(ProjectAssetPublishResult.Unchanged, Publish(61).Result);
        Assert.Equal(1, changes);
        Assert.Equal(ProjectAssetPublishResult.Published, Publish(62).Result);
        Assert.Equal(2, changes);

        // …and a transport that was LENT rather than handed over cannot take the arm at all
        string lent = Path.Combine(_root, "drops", "lent.png");
        WritePng(lent, 63);
        var borrowed = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-base", lent);
        var refused = Assert.Throws<InvalidOperationException>(() =>
            session.PublishAssetForBinding(borrowed, ProjectAssetKind.Picture, "Lent",
                ProjectAssetIngress.Prepared(TextureIngress.PixelIdentity(lent))));
        Assert.Contains("handed over", refused.Message);

        ExactAssetPublishResult Publish(byte seed)
        {
            // Written the way the preparation writes one: an external picture through the project's own
            // canonical encoder, which is the claim the fast arm stands on.
            string raw = Path.Combine(_root, "prepared", Guid.NewGuid().ToString("N") + ".raw.png");
            WritePng(raw, seed);
            string prepared = Path.Combine(_root, "prepared", Guid.NewGuid().ToString("N") + ".png");
            TextureIngress.Publish(raw, prepared);
            string identity = TextureIngress.PixelIdentity(prepared);
            var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-base",
                prepared, handOver: true);
            Assert.False(File.Exists(prepared), "a handed-over transport left the caller's bytes behind");
            return session.PublishAssetForBinding(ingress, ProjectAssetKind.Picture, "Prepared",
                ProjectAssetIngress.Prepared(identity));
        }
    }

    /// <summary>The save-time sweep is a committer like every other one, and it carries the same flag: a
    /// save that ran inside an open transaction would write the live project out from under the candidate
    /// still in flight.</summary>
    [Fact]
    public void The_save_sweep_refuses_to_run_inside_an_open_transaction()
    {
        var project = Fixture();
        project.RootDir = _root;
        var session = new AuthoredEditSession(project);

        var refused = Assert.Throws<InvalidOperationException>(() =>
            session.Compound(_ => session.SweepStructuredValuesForSave()));
        Assert.Contains("authored transaction is already open", refused.Message);
    }

    private AuthoredProject PictureProject(string file)
    {
        var project = new AuthoredProject
        {
            RootDir = _root,
            Info = new ProjectInfo { Name = "Ingress fixture", Version = "1.0" },
            ProjectAssets = new List<ProjectAsset>
            {
                new() { Id = "picture-body", Kind = ProjectAssetKind.Picture, Label = "Body", File = file },
            },
        };
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }

    private static AuthoredProject Fixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Project", "golden", "authored_project_v2.json");
        return AuthoredProjectSerializer.Load(path);
    }

    private static AuthoredProject FixtureWithPictureSlots()
    {
        var project = Fixture();
        var geometry = project.TargetSlots.Single(s => s.Id == "slot-geometry");
        var ramp = project.TargetSlots.Single(s => s.Id == "slot-ramp");
        project.ProjectAssets.AddRange(new[]
        {
            new ProjectAsset { Id = "base-source", Kind = ProjectAssetKind.Picture,
                Label = "Base", File = "textures/base.png" },
            new ProjectAsset { Id = "alpha-authored", Kind = ProjectAssetKind.StructuredValue,
                Label = "Authored alpha", File = "textures/rmo.png",
                Source = new ProjectAssetSource { ProjectAssetId = "base-source" },
                Value = new ProjectAssetValue { Semantic = "rmo-alpha", Value = "ship-as-authored" } },
        });
        foreach (var (id, input) in new[]
                 {
                     ("slot-base", TargetInputKind.BaseColor),
                     ("slot-normal", TargetInputKind.Normal),
                     ("slot-rmo", TargetInputKind.Rmo),
                     ("slot-rmo-alpha", TargetInputKind.RmoAlpha),
                 })
        {
            project.TargetSlots.Add(new TargetSlot
            {
                Id = id, Part = geometry.Part, Input = input, SubmeshIndex = 0, MaterialSlotIndex = 0,
                Renderer = geometry.Renderer, Mesh = geometry.Mesh, Material = ramp.Material,
            });
        }
        foreach (var edit in project.EditDefinitions)
        {
            edit.Bindings.Add(new Binding { SlotId = "slot-base", Kind = BindingKind.ProjectAsset,
                ProjectAssetId = "base-source" });
            edit.Bindings.Add(new Binding { SlotId = "slot-normal", Kind = BindingKind.InheritedLiveCarrier });
            edit.Bindings.Add(new Binding { SlotId = "slot-rmo", Kind = BindingKind.InheritedLiveCarrier });
            edit.Bindings.Add(new Binding { SlotId = "slot-rmo-alpha", Kind = BindingKind.ProjectAsset,
                ProjectAssetId = "alpha-authored" });
        }
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }

    /// <summary>The identity form, the name Save As writes on its own, the preview and the catalog stamp.
    /// Each writes only its own field: a form that never shows the preview cannot clear it.</summary>
    [Fact]
    public void The_identity_verbs_each_write_their_own_field_and_leave_the_rest_alone()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());

        session.SetPreview("preview\\shot.png");
        session.SetIdentity("Vesna Coat", "1.2", "Tester", "A coat", "F7",
            includeRepairData: false, character: "Vesna", outfit: "VesnaSSR01");
        session.SetAuthoredAgainst("26109");
        var info = session.Snapshot().Info;

        Assert.Equal("Vesna Coat", info.Name);
        Assert.Equal("1.2", info.Version);
        Assert.Equal("Tester", info.Author);
        Assert.Equal("A coat", info.Description);
        Assert.Equal("F7", info.ToggleKey);
        Assert.False(info.IncludeRepairData);
        Assert.Equal("Vesna", info.Character);
        Assert.Equal("VesnaSSR01", info.Outfit);
        // The form does not carry the preview, so writing the form left it where it was.
        Assert.Equal("preview/shot.png", info.Preview);
        Assert.Equal("26109", session.Snapshot().AuthoredAgainst?.CatalogVersion);

        session.SetName("Renamed");
        Assert.Equal("Renamed", session.Snapshot().Info.Name);
        Assert.Equal("A coat", session.Snapshot().Info.Description);

        session.SetPreview("  ");
        Assert.Null(session.Snapshot().Info.Preview);
        session.SetAuthoredAgainst(null);
        Assert.Null(session.Snapshot().AuthoredAgainst);
    }

    /// <summary>The workspace inventory is taken by value and stripped of anything that would be a second
    /// opinion about intent — what is edited, and what a donor row asked for.</summary>
    [Fact]
    public void An_exact_workspace_index_detaches_from_its_caller()
    {
        var project = AuthoredEditFixtures.Golden();
        var geometry = project.TargetSlots.Single(slot => slot.Id == "slot-geometry");
        var session = new AuthoredEditSession(project);
        var index = new AuthoredWorkspaceIndex
        {
            Selection = { new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" } },
            Records =
            {
                new AuthoredWorkspaceRecord
                {
                    Id = "workspace-body", Kind = ProjectAssetKind.Geometry,
                    Part = geometry.Part, GameAsset = geometry.Mesh!, SlotId = geometry.Id,
                    ProjectFile = "meshes/body.glb",
                },
            },
        };

        session.SetWorkspaceIndex(index);
        index.Records[0].Part.RendererSlot = "written-after-the-capture";
        index.Selection.Clear();

        var captured = session.Snapshot().WorkspaceIndex!;
        Assert.Equal("Vesna", Assert.Single(captured.Selection).Character);
        var record = Assert.Single(captured.Records);
        Assert.Equal("c_vesna_body_lod0", record.Part.RendererSlot);
        Assert.Equal("slot-geometry", record.SlotId);
    }

    /// <summary>Where a picked toon ramp goes on an installed material, and the refusal where the material
    /// draws through no ramp at all — the shader has no input, so there is no place to bind one.</summary>
    [Fact]
    public void A_picked_ramp_addresses_the_game_ramp_slot_and_a_material_with_none_is_refused_by_name()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var body = AuthoredEditFixtures.Body;

        Assert.Equal("slot-ramp", session.GameRampSlot(body, 0));

        var error = Assert.Throws<AuthoredRefusalException>(() => session.GameRampSlot(body, 3));
        Assert.Contains("Material 3", error.Message);
        Assert.Contains("without a toon ramp", error.Message);
        // The address the model finds the part by is not what the person reading this calls it.
        Assert.DoesNotContain("c_vesna_body_lod0", error.Message);
    }

    private static Binding Binding(AuthoredProject project, string edit, string slot) =>
        project.EditDefinitions.Single(e => e.Id == edit).Bindings.Single(b => b.SlotId == slot);

    private static Rgba32 Pixel(byte seed) => new(seed, (byte)(seed + 1), (byte)(seed + 2), 255);

    private static void WritePng(string path, byte seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(2, 2, Pixel(seed));
        image.SaveAsPng(path);
    }

    private static Rgba32 ReadPixel(string path)
    {
        using var image = Image.Load<Rgba32>(path);
        return image[0, 0];
    }
}
