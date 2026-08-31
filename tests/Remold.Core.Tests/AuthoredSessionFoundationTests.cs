using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Remold.Core.Migoto;
using Remold.Core.Project;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

public sealed class AuthoredSessionFoundationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-session-foundation-" + Guid.NewGuid().ToString("N"));

    public AuthoredSessionFoundationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void Schema1_adapts_once_to_session()
    {
        string dir = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(dir);
        var released = new ModProject
        {
            RootDir = dir,
            Info = new ProjectInfo { Name = "Released", Version = "1.0" },
            Selection = new List<SelectionEntry>
            {
                new() { Character = "Vesna", Outfit = "VesnaSSR01" },
            },
            Targets = new List<ProjectTarget>
            {
                new()
                {
                    AssetType = "Mesh", Bundle = "characters/vesna", ObjectName = "body",
                    PathId = 9001, SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
                    ReplaceFile = "meshes/body.glb", OriginalFile = "originals/body.glb",
                },
            },
        };
        released.Save();
        WriteBytes(Path.Combine(dir, "meshes", "body.glb"), 2);
        WriteBytes(Path.Combine(dir, "originals", "body.glb"), 1);

        var document = AuthoredProjectDocument.Load(dir, part => new LegacyResolvedPart(
            part,
            Game(8001, "body", "characters/vesna"),
            Game(9001, "body_mesh", "characters/vesna"),
            Array.Empty<LegacyResolvedMaterial>()));
        Assert.True(document.Session is not null,
            string.Join("; ", document.LastMigrationReport?.Items.Select(item => item.Detail)
                ?? Array.Empty<string>()));
        var session = document.Session!;

        document.Save();

        Assert.Same(session, document.Session);
        Assert.Equal(AuthoredProject.CurrentSchema, AuthoredProjectSerializer.SchemaOf(dir));
        Assert.Equal("Released", AuthoredProjectSerializer.Load(dir).Info.Name);
    }

    [Fact]
    public void Save_serializes_session_without_capturing_projection()
    {
        string dir = Path.Combine(_root, "direct-save");
        var document = AuthoredProjectDocument.New();
        document.Session!.SetRootDir(dir);
        document.Session.SetName("Session owner");
        long beforeSave = document.Session.Revision;

        document.Save(dir);

        Assert.Equal(beforeSave, document.Session.Revision);
        Assert.Equal("Session owner", AuthoredProjectSerializer.Load(dir).Info.Name);
        Assert.Throws<InvalidDataException>(() => ModProject.Load(dir));
    }

    /// <summary>The execution package carries authored intent ITSELF — no projected workspace stands
    /// between the compiler and the project, so there is no second copy of the identity to drift.</summary>
    [Fact]
    public void Build_execution_carries_the_authored_project_itself()
    {
        var project = new AuthoredProject
        {
            RootDir = Path.Combine(_root, "execution"),
            Info = new ProjectInfo { Name = "Emission only", Version = "1.0" },
        };

        var execution = AuthoredBuildExecution.Create(project, new AuthoredBuildPlan());

        Assert.Same(project, execution.Project);
        Assert.Same(project.Info, execution.Project.Info);
        Assert.Empty(execution.Work);
    }

    [Fact]
    public void Unregistered_source_publishes_to_one_exact_slot()
    {
        var project = PictureSlots();
        var longEdit = project.EditDefinitions.Single(edit => edit.Id == "edit-long");
        longEdit.Bindings.Single(binding => binding.SlotId == "slot-base-0").Kind =
            BindingKind.TargetGameValue;
        longEdit.Bindings.Single(binding => binding.SlotId == "slot-base-0").ProjectAssetId = null;
        var session = new AuthoredEditSession(project);
        string source = Path.Combine(_root, "drop.png");
        WritePng(source, 10);
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-base-0", source);

        var result = session.PublishAssetForBinding(ingress, ProjectAssetKind.Picture, "Dropped base",
            ProjectAssetIngress.Png, new ProjectAssetSource
            {
                GameAsset = new GameAssetRef
                {
                    GameBuild = "26109", LogicalBundle = "textures/body", PathId = 71001,
                    Name = "body_base",
                },
            });
        var snapshot = session.Snapshot();

        Assert.Equal(ProjectAssetPublishResult.Published, result.Result);
        Assert.Equal(result.ProjectAssetId, Binding(snapshot, "edit-long", "slot-base-0").ProjectAssetId);
        Assert.Equal(new byte[] { 10, 10, 10, 255 }, PixelBytes(snapshot, result.ProjectAssetId!));
        Assert.Equal("shared-picture", Binding(snapshot, "edit-long", "slot-base-1").ProjectAssetId);
        Assert.Equal("shared-picture", Binding(snapshot, "edit-short", "slot-base-0").ProjectAssetId);
        Assert.StartsWith("assets/edits/edit-long/slots/slot-base-0/", result.ProjectRelativeFile,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Two_slots_on_one_part_stay_independent_across_save_and_reopen()
    {
        var project = PictureSlots();
        AuthoredProjectSerializer.Save(project, _root);
        var document = AuthoredProjectDocument.Load(_root);
        var session = document.Session!;

        Publish(session, "slot-base-0", 21);
        Publish(session, "slot-base-1", 31);
        document.Save();

        var reopened = AuthoredProjectDocument.Load(_root).Session!.Snapshot();
        string first = Binding(reopened, "edit-long", "slot-base-0").ProjectAssetId!;
        string second = Binding(reopened, "edit-long", "slot-base-1").ProjectAssetId!;
        Assert.NotEqual(first, second);
        Assert.Equal(new byte[] { 21, 21, 21, 255 }, PixelBytes(reopened, first));
        Assert.Equal(new byte[] { 31, 31, 31, 255 }, PixelBytes(reopened, second));
        Assert.Equal("shared-picture", Binding(reopened, "edit-short", "slot-base-0").ProjectAssetId);
        Assert.Equal("shared-picture", Binding(reopened, "edit-short", "slot-base-1").ProjectAssetId);
    }

    [Fact]
    public void Old_targets_normalize_once_and_never_reserialize()
    {
        var project = PictureSlots();
        project.WorkspaceIndex = null;
        var root = JsonNode.Parse(AuthoredProjectSerializer.Serialize(project))!.AsObject();
        root["workspace_index"] = new JsonObject
        {
            ["selection"] = new JsonArray(),
            ["targets"] = new JsonArray(JsonSerializer.SerializeToNode(new ProjectTarget
            {
                AssetType = "Mesh",
                Bundle = "characters/vesna",
                ObjectName = "c_vesna_body_lod0",
                PathId = 9001,
                SubjectCharacter = "Vesna",
                SubjectOutfit = "VesnaSSR01",
                ReplaceFile = "meshes/body.glb",
                OriginalFile = "originals/body.glb",
            })),
        };

        var normalized = AuthoredProjectSerializer.Deserialize(root.ToJsonString());
        string written = AuthoredProjectSerializer.Serialize(normalized);

        var record = Assert.Single(normalized.WorkspaceIndex!.Records);
        Assert.Equal("slot-geometry", record.SlotId);
        Assert.Equal(72001, record.GameAsset.PathId);
        Assert.DoesNotContain("\"targets\"", written, StringComparison.Ordinal);
        Assert.Contains("\"records\"", written, StringComparison.Ordinal);
        Assert.Null(normalized.WorkspaceIndex.LegacyTargets);
    }

    [Fact]
    public void Failed_transactions_raise_no_change_and_successes_raise_once()
    {
        var session = new AuthoredEditSession(PictureSlots());
        var events = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => events.Add(change);

        Assert.Throws<KeyNotFoundException>(() => session.RenameEdit("missing", "No"));
        Assert.Empty(events);
        Assert.Equal(0, session.Revision);

        session.RenameEdit("edit-long", "Longer");
        var change = Assert.Single(events);
        Assert.Equal(1, change.Revision);
        Assert.Equal(1, session.Revision);
        Assert.Contains("edit-long", change.EditDefinitionIds);
        Assert.True(change.Invalidation.HasFlag(AuthoredInvalidation.Bindings));

        session.RenameEdit("edit-long", "Longer");
        Assert.Single(events);
        Assert.Equal(1, session.Revision);
    }

    [Fact]
    public void Compound_commands_each_raise_one_transaction_notification()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Saved());
        var events = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => events.Add(change);

        session.RecordReplacementOutputs("edit-long", 1);
        Assert.Single(events);
        Assert.Equal(1, events[0].Revision);

        session.CreateKeyGroup("F7", "edit-long");
        Assert.Equal(2, events.Count);
        Assert.Equal(2, events[1].Revision);
        // Placement carries no picture: moving an edit onto a key-group state changes what ships and
        // nothing any row draws, so it does not ask a page to throw its renders away.
        Assert.Equal(AuthoredInvalidation.Composition, events[1].Invalidation);
    }

    /// <summary>A caller's OWN compound: many commands, one transaction. This is what a Blender return is —
    /// one modder action landing a hundred answers — and committing them one at a time made a page rebuild,
    /// an autosave and a build replan out of every one of them. The batch commits once, under one revision,
    /// and the change it announces names EVERYTHING it moved, which is what the pages' scoped invalidation
    /// aims with.</summary>
    [Fact]
    public void A_callers_compound_commits_once_and_names_everything_it_moved()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.MultiPart());
        var events = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => events.Add(change);

        string hair = "", cape = "", hide = "";
        session.Compound(change =>
        {
            hair = change.CreateEdit(AuthoredEditFixtures.Hair);
            cape = change.CreateEdit(AuthoredEditFixtures.Cape);
            hide = change.AddHideEdit(AuthoredEditFixtures.Body);
        });

        var committed = Assert.Single(events);
        Assert.Equal(1, committed.Revision);
        Assert.Equal(1, session.Revision);
        Assert.All(new[] { hair, cape, hide }, id => Assert.Contains(id, committed.EditDefinitionIds));
        var project = session.Snapshot();
        Assert.All(new[] { hair, cape, hide }, id =>
            Assert.Contains(project.EditDefinitions, edit => edit.Id == id));
        // the cape had no answer of its own, so its first edit takes the board the way any first one does
        Assert.Contains(cape, project.Always);
    }

    /// <summary>…and a compound that refuses anywhere commits NOTHING — not the rows that ran before it.
    /// The all-or-nothing a return promises is this, rather than a rollback anyone has to write.</summary>
    [Fact]
    public void A_refusal_inside_a_compound_leaves_the_whole_batch_uncommitted()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.MultiPart());
        var events = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => events.Add(change);

        Assert.Throws<AuthoredRefusalException>(() => session.Compound(change =>
        {
            change.CreateEdit(AuthoredEditFixtures.Cape);
            // a part the project holds no place for at all: nothing to build an edit from
            change.CreateEdit(AuthoredEditFixtures.Part("c_vesna_boots_lod0"));
        }));

        Assert.Empty(events);
        Assert.Equal(0, session.Revision);
        Assert.DoesNotContain(session.Snapshot().EditDefinitions,
            edit => edit.Target.SameAs(AuthoredEditFixtures.Cape));
    }

    /// <summary>A command made on the SESSION from inside a compound is refused rather than quietly lost.
    /// The lock is re-entrant, so it would otherwise commit against the live project while the batch's
    /// candidate is still in flight — and the batch's own commit would then overwrite it.</summary>
    [Fact]
    public void A_session_command_made_inside_a_compound_is_refused()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.MultiPart());
        var events = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => events.Add(change);

        Assert.Throws<InvalidOperationException>(() => session.Compound(change =>
        {
            change.CreateEdit(AuthoredEditFixtures.Cape);
            session.RenameEdit("edit-long", "From inside");
        }));

        Assert.Empty(events);
        Assert.Equal(0, session.Revision);
    }

    /// <summary>The invalidation matrix's ② half: how far each authored mutation's committed change reaches
    /// into what a page derived per edit and per slot. ③'s half is
    /// <see cref="BuildPageVmTests.Only_a_change_the_build_plan_is_derived_from_replans"/>, walking the same
    /// list; the page's use of this answer is pinned by the Edit page's two out-of-order invalidation
    /// tests.</summary>
    [Theory]
    [MemberData(nameof(InvalidationCases.Names), MemberType = typeof(InvalidationCases))]
    public void A_change_reaches_exactly_as_far_into_the_pictures_as_it_can_say(string name)
    {
        var scenario = InvalidationCases.Named(name);
        string root = Path.Combine(_root, "matrix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = InvalidationCases.Session(root);
        scenario.Arrange?.Invoke(session, root);
        AuthoredProjectChangedEventArgs? committed = null;
        session.Changed += (_, change) => committed = change;

        scenario.Act(session, root);

        var change = committed ?? throw new Xunit.Sdk.XunitException($"'{name}' committed no change");
        Assert.Equal(scenario.Replans, change.Invalidation.AffectsBuildPlan());
        bool movedAPicture = (change.Invalidation & AuthoredInvalidation.Preview) != 0;
        Assert.Equal(scenario.Reach != PreviewReach.None, movedAPicture);
        if (!movedAPicture) return;
        Assert.Equal(scenario.Reach == PreviewReach.Scoped, change.NamesWhatItMoved());
        if (scenario.Reach == PreviewReach.Scoped)
            Assert.Equal(new[] { scenario.TouchedEdit! }, change.EditDefinitionIds);
    }

    // ---- the reverse dependency: what BORROWS the slots a change named ----
    //
    // Route: every authored command commits through Change → Describe, and Describe is what the Changed
    // event carries. These drive a real command and read the change the session raised.

    /// <summary>A change to a slot another edit BORROWS names the borrowing slot, and the edit that borrows
    /// it. The dependency is written only on the borrower, so a change that named the source's own edit and
    /// slots left the borrower's card and render standing on the source's old answer.</summary>
    [Fact]
    public void A_change_reaches_the_slots_that_borrow_what_it_moved()
    {
        var project = AuthoredEditFixtures.WithCrossEditBorrow();
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "skin-other", Kind = ProjectAssetKind.Picture, Label = "Other",
            File = "textures/other.png",
        });
        var session = new AuthoredEditSession(project);
        AuthoredProjectChangedEventArgs? committed = null;
        session.Changed += (_, change) => committed = change;

        session.ChooseProjectAsset("edit-long", "slot-owned", "skin-other");

        var change = committed ?? throw new Xunit.Sdk.XunitException("the rebind committed no change");
        // edit-short binds slot-short-base and nothing else that moved, so nothing but the reverse
        // dependency can put either name in this change.
        Assert.Contains("slot-short-base", change.SlotIds);
        Assert.Contains("edit-short", change.EditDefinitionIds);
    }

    /// <summary>And it runs one way. A change to the BORROWER says nothing about the slot it borrows from:
    /// what the source draws is its own answer, and that did not move.</summary>
    [Fact]
    public void A_change_to_a_borrower_does_not_reach_back_into_its_source()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.WithCrossEditBorrow());
        AuthoredProjectChangedEventArgs? committed = null;
        session.Changed += (_, change) => committed = change;

        session.ChooseProjectAsset("edit-short", "slot-ramp", "ramp-warm");

        var change = committed ?? throw new Xunit.Sdk.XunitException("the rebind committed no change");
        Assert.DoesNotContain("edit-long", change.EditDefinitionIds);
        Assert.DoesNotContain("slot-owned", change.SlotIds);
        Assert.DoesNotContain("slot-owned-2", change.SlotIds);
    }

    /// <summary>A borrowed slot can itself be borrowed, and the change reaches the FAR end of the line. The
    /// fixture writes its links far end first, which is the order one pass down the list cannot follow: each
    /// pass carries the answer exactly one hop further, so the last link is reached only by running the
    /// expansion until it stops growing.</summary>
    [Fact]
    public void A_change_reaches_the_far_end_of_a_chain_of_borrowings()
    {
        var project = AuthoredEditFixtures.WithBorrowChain();
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "skin-other", Kind = ProjectAssetKind.Picture, Label = "Other",
            File = "textures/other.png",
        });
        var session = new AuthoredEditSession(project);
        AuthoredProjectChangedEventArgs? committed = null;
        session.Changed += (_, change) => committed = change;

        session.ChooseProjectAsset("edit-short", "chain-root", "skin-other");

        var change = committed ?? throw new Xunit.Sdk.XunitException("the rebind committed no change");
        Assert.Contains("chain-1", change.SlotIds);
        Assert.Contains("chain-2", change.SlotIds);
        Assert.Contains("chain-3", change.SlotIds);
        // The chain is edit-long's; the command named edit-short. Only the reverse pass can name the other.
        Assert.Contains("edit-long", change.EditDefinitionIds);
        // And naming the borrowing EDIT does not drag in the rest of what that edit binds: slot-owned-2
        // borrows slot-owned, and neither of them moved.
        Assert.DoesNotContain("slot-owned", change.SlotIds);
        Assert.DoesNotContain("slot-owned-2", change.SlotIds);
    }

    /// <summary>Deleting a borrowing edit names it and the slot it borrowed on, and still says nothing about
    /// the source it borrowed from — the one-way rule holds where the borrower leaves, not only where it
    /// changes. The source's own answer is exactly where it was, so a card of it must not be thrown away.
    /// </summary>
    [Fact]
    public void Deleting_a_borrower_names_it_and_leaves_its_source_alone()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.WithCrossEditBorrow());
        AuthoredProjectChangedEventArgs? committed = null;
        session.Changed += (_, change) => committed = change;

        session.DeleteEdit("edit-short");

        var change = committed ?? throw new Xunit.Sdk.XunitException("the delete committed no change");
        Assert.Contains("edit-short", change.EditDefinitionIds);
        Assert.Contains("slot-short-base", change.SlotIds);
        Assert.DoesNotContain("edit-long", change.EditDefinitionIds);
        Assert.DoesNotContain("slot-owned", change.SlotIds);
    }

    /// <summary>What the ③ replan gate is derived from, as a rule rather than as a list of verbs. The
    /// unclassified answer is the one worth stating: a difference no flag recognised must replan, because a
    /// stale readiness verdict on screen is the worse of the two costs.</summary>
    [Fact]
    public void Only_what_the_mod_calls_itself_leaves_the_build_plan_where_it_is()
    {
        Assert.False(AuthoredInvalidation.Identity.AffectsBuildPlan());
        Assert.True(AuthoredInvalidation.None.AffectsBuildPlan());
        foreach (var flag in Enum.GetValues<AuthoredInvalidation>()
                     .Where(value => value is not (AuthoredInvalidation.None or AuthoredInvalidation.Identity)))
        {
            Assert.True(flag.AffectsBuildPlan());
            Assert.True((AuthoredInvalidation.Identity | flag).AffectsBuildPlan());
        }
    }

    [Fact]
    public void Geometry_publish_and_replacement_layout_raise_one_notification()
    {
        var project = PictureSlots();
        var geometry = project.EditDefinitions.Single(edit => edit.Id == "edit-long").Bindings
            .Single(binding => binding.SlotId == "slot-geometry");
        geometry.Kind = BindingKind.TargetGameValue;
        geometry.ProjectAssetId = null;
        string source = Path.Combine(_root, "returned.glb");
        WriteBytes(source, 42);
        var session = new AuthoredEditSession(project);
        var changes = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => changes.Add(change);
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-geometry", source);

        session.PublishAssetForBinding(ingress, ProjectAssetKind.Geometry, "Returned body",
            ProjectAssetIngress.Binary, replacementSubmeshCount: 2);

        var change = Assert.Single(changes);
        Assert.Equal(1, change.Revision);
        Assert.Contains("slot-geometry", change.SlotIds);
        // Position 0 binds Base + Ramp and position 1 binds Base: the replacement mirrors those three
        // installed properties instead of manufacturing the old five-kind card set twice.
        Assert.Equal(3, session.Slots("edit-long").Count(state =>
            state.Slot.Domain == TargetSlotDomain.EditOutput));
    }

    [Fact]
    public void Transport_refuses_when_the_exact_non_file_binding_changed_after_open()
    {
        var project = PictureSlots();
        var binding = Binding(project, "edit-long", "slot-base-0");
        binding.Kind = BindingKind.TargetGameValue;
        binding.ProjectAssetId = null;
        string source = Path.Combine(_root, "stale-drop.png");
        WritePng(source, 51);
        var session = new AuthoredEditSession(project);
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-base-0", source);
        session.ChooseSourceSlot("edit-long", "slot-base-0", "slot-base-1");

        // Written for the modder, not for the log: they changed this map in the app while the image editor
        // held it, and the way through is the same as the file twin's.
        var error = Assert.Throws<AuthoredRefusalException>(() => session.PublishAssetForBinding(ingress,
            ProjectAssetKind.Picture, "Stale drop", ProjectAssetIngress.Png));

        Assert.Equal(ProjectAssetIngress.EditMovedWhileOpen, error.Message);
        Assert.Equal(BindingKind.SourceSlot,
            Binding(session.Snapshot(), "edit-long", "slot-base-0").Kind);
    }

    private void Publish(AuthoredEditSession session, string slotId, byte seed)
    {
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", slotId);
        WritePng(ingress.OutboundSnapshot, seed);
        Assert.Equal(ProjectAssetPublishResult.Published,
            session.PublishAssetForBinding(ingress, ProjectAssetKind.Picture, slotId,
                ProjectAssetIngress.Png).Result);
    }

    private AuthoredProject PictureSlots()
    {
        string shared = Path.Combine(_root, "textures", "shared.png");
        WritePng(shared, 1);
        var project = AuthoredEditFixtures.Golden();
        project.RootDir = _root;
        var geometry = project.TargetSlots.Single(slot => slot.Id == "slot-geometry");
        var material = project.TargetSlots.Single(slot => slot.Id == "slot-ramp").Material!;
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "shared-picture", Kind = ProjectAssetKind.Picture,
            Label = "Shared base", File = "textures/shared.png",
        });
        foreach (int index in new[] { 0, 1 })
        {
            string id = $"slot-base-{index}";
            project.TargetSlots.Add(new TargetSlot
            {
                Id = id, Part = geometry.Part, SubmeshIndex = index, MaterialSlotIndex = index,
                Input = TargetInputKind.BaseColor, Domain = TargetSlotDomain.Game,
                Renderer = geometry.Renderer, Mesh = geometry.Mesh, Material = material,
            });
            foreach (var edit in project.EditDefinitions.Where(edit => edit.Kind == EditDefinitionKind.Content))
                edit.Bindings.Add(new Binding
                {
                    SlotId = id, Kind = BindingKind.ProjectAsset, ProjectAssetId = "shared-picture",
                });
        }
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }

    private static Binding Binding(AuthoredProject project, string editId, string slotId) =>
        project.EditDefinitions.Single(edit => edit.Id == editId).Bindings
            .Single(binding => binding.SlotId == slotId);

    private byte[] PixelBytes(AuthoredProject project, string assetId)
    {
        string file = project.ProjectAssets.Single(asset => asset.Id == assetId).File;
        using var image = Image.Load<Rgba32>(Path.Combine(_root, file));
        var pixel = image[0, 0];
        return new[] { pixel.R, pixel.G, pixel.B, pixel.A };
    }

    private static void WritePng(string path, byte seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(2, 2, new Rgba32(seed, seed, seed, 255));
        image.SaveAsPng(path);
    }

    // ---- what a failed action says ----

    /// <summary>A failure the outside world wrote for a person keeps its own account. The operating system
    /// names the file another program is holding and the folder the account may not touch, and that half is
    /// the whole diagnosis: without it the modder is told an action did not happen and nothing about why.
    /// The stock error shape carries it.</summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void An_outside_world_failure_keeps_its_own_account_of_what_went_wrong(Type family)
    {
        const string osWrote = "The process cannot access the file because it is being used by another process";
        var failure = (Exception)Activator.CreateInstance(family, osWrote)!;

        string line = AuthoredRefusal.ForScreen(failure, "save this map");

        Assert.Equal($"Couldn't save this map: {osWrote}.", line);
    }

    /// <summary>Everything else is a defect, and a defect's message names slot ids, edit ids, file handles
    /// and COM results — none of which mean anything on a status line. It says what the action was and
    /// nothing more. A refusal the model wrote for the screen is shown exactly as it is.</summary>
    [Fact]
    public void A_defect_names_the_action_and_a_written_refusal_is_shown_as_it_is()
    {
        Assert.Equal("Couldn't save this map.", AuthoredRefusal.ForScreen(
            new InvalidOperationException("slot 'slot-0007' has no binding on edit 'edit-3f2a'"),
            "save this map"));

        Assert.Equal("This part has no material 4.", AuthoredRefusal.ForScreen(
            new AuthoredRefusalException("This part has no material 4."), "save this map"));

        // An outside-world failure with nothing to say falls back to the action rather than to a colon
        // with an empty half after it.
        Assert.Equal("Couldn't save this map.",
            AuthoredRefusal.ForScreen(new IOException("   "), "save this map"));
    }

    private static void WriteBytes(string path, byte value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new[] { value });
    }

    private static GameAssetRef Game(long pathId, string name, string bundle) => new()
    {
        GameBuild = "26109", LogicalBundle = bundle, PathId = pathId, Name = name,
    };
}
