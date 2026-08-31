using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>Opening and saving one project. A schema-2 manifest opens as itself; a schema-1 one is
/// converted at open — the only support that format has — and the first save migrates the file atomically,
/// keeping the outgoing manifest as a one-time backup. A conversion that cannot complete refuses the open
/// and says which of the two reasons it was.</summary>
public sealed class ProjectDocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-document-" + Guid.NewGuid().ToString("N"));

    public ProjectDocumentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Opening_a_released_project_converts_in_memory_and_the_first_save_migrates()
    {
        string dir = Path.Combine(_root, "convert-on-load");
        var legacy = new ModProject
        {
            RootDir = dir,
            Info = new ProjectInfo { Name = "Converted", Author = "Tester" },
        };
        legacy.Save();
        string file = ModProject.ManifestPathFor(dir);
        string before = File.ReadAllText(file);

        var document = AuthoredProjectDocument.Load(dir, _ => null);

        Assert.NotNull(document.Authored);
        Assert.True(document.OpenedLegacy);
        Assert.Equal(ModProject.CurrentSchema, AuthoredProjectSerializer.SchemaOf(dir));
        Assert.Equal(before, File.ReadAllText(file));   // opening never writes

        document.Save();

        Assert.Equal(AuthoredProject.CurrentSchema, AuthoredProjectSerializer.SchemaOf(dir));
        Assert.Equal(before, File.ReadAllText(file + ".bak"));
        Assert.False(document.OpenedLegacy);
        Assert.Equal("Converted", AuthoredProjectSerializer.Load(dir).Info.Name);
    }

    [Fact]
    public void Explicit_save_migrates_atomically_and_schema1_loaders_refuse_the_authored_manifest()
    {
        string dir = Path.Combine(_root, "legacy");
        var legacy = new ModProject
        {
            RootDir = dir,
            Info = new ProjectInfo { Name = "Legacy", Author = "Tester" },
        };
        legacy.Save();
        string file = ModProject.ManifestPathFor(dir);
        string before = File.ReadAllText(file);
        // The conversion is the OPEN's, not the save's, so the resolver goes in here.
        var document = AuthoredProjectDocument.Load(dir, _ => null);

        document.Save();

        Assert.Equal(AuthoredProject.CurrentSchema, AuthoredProjectSerializer.SchemaOf(dir));
        Assert.Equal(before, File.ReadAllText(file + ".bak"));
        Assert.Equal("Legacy", AuthoredProjectSerializer.Load(dir).Info.Name);
        Assert.Throws<InvalidDataException>(() => ModProject.Load(dir));

        var authored = AuthoredProjectSerializer.Load(dir);
        authored.Info.Name = "Edited after migration";
        AuthoredProjectSerializer.Save(authored, dir);
        Assert.Equal(before, File.ReadAllText(file + ".bak"));
        Assert.Equal("Edited after migration", AuthoredProjectSerializer.Load(dir).Info.Name);
    }

    [Fact]
    public void Save_keeps_the_session_instance_and_serializes_it_directly()
    {
        string dir = Path.Combine(_root, "stable");
        var legacy = new ModProject
        {
            RootDir = dir,
            Info = new ProjectInfo { Name = "Stable", Author = "Tester" },
        };
        legacy.Save();
        var document = AuthoredProjectDocument.Load(dir, _ => null);
        var session = document.Session!;
        document.Session!.SetName("Renamed");

        document.Save();

        Assert.Same(session, document.Session);
        Assert.Equal("Renamed", AuthoredProjectSerializer.Load(dir).Info.Name);
    }

    /// <summary>The conversion needs the game files to re-anchor every route against, and without them it
    /// cannot run at all. The open FAILS rather than handing back a project on the released shape: the mod
    /// is untouched on disk, and the sentence names the install — in the app's own words for that state, so
    /// the refusal cannot send the modder after an action ② Edit and ③ Build never mention.</summary>
    [Fact]
    public void A_schema_one_project_with_no_install_does_not_open()
    {
        string dir = BlockedProject("no-install");
        string before = File.ReadAllText(ModProject.ManifestPathFor(dir));

        var refusal = Assert.Throws<InvalidDataException>(() => AuthoredProjectDocument.Load(dir));

        Assert.Equal(AuthoredProjectDocument.NoInstall, refusal.Message);
        Assert.Contains(GameFilesGate.Unavailable, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(ModProject.ManifestPathFor(dir)));
        Assert.Equal(ModProject.CurrentSchema, AuthoredProjectSerializer.SchemaOf(dir));
    }

    /// <summary>The install is loaded and the conversion still cannot finish: one route this install cannot
    /// answer for. That is a defect, not user error, so the open refuses too — naming the PART, as the ②
    /// Edit page names one whose subject has not been read, and then the cause, which is the half a refusal
    /// built from the route alone threw away.</summary>
    [Fact]
    public void A_schema_one_project_this_install_cannot_re_anchor_does_not_open()
    {
        string dir = BlockedProject("unresolvable");
        string before = File.ReadAllText(ModProject.ManifestPathFor(dir));

        var refusal = Assert.Throws<InvalidDataException>(
            () => AuthoredProjectDocument.Load(dir, _ => null));

        Assert.Contains("Couldn't update these parts of it: c_vesna_body_lod0.", refusal.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Vesna / VesnaSSR01 /", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("This part is not in the current game files.", refusal.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(GameFilesGate.Unavailable, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(ModProject.ManifestPathFor(dir)));
    }

    /// <summary>A conversion that produced intent the model refuses files its problem against the whole
    /// project, which is no part at all: the refusal says so in its own sentence rather than naming
    /// "project" as though it were one of the mod's parts.</summary>
    [Fact]
    public void A_conversion_blocked_by_the_whole_project_gets_its_own_sentence()
    {
        var report = new MigrationReport();
        report.Add("build.key_start", MigrationDisposition.Unresolved, "project",
            "two keys switch the same change");

        string refusal = AuthoredProjectDocument.CannotUpdate(report);

        Assert.Contains("Couldn't update it. Two keys switch the same change.", refusal,
            StringComparison.Ordinal);
        Assert.DoesNotContain("parts", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain(": project", refusal, StringComparison.Ordinal);
    }

    /// <summary>Three parts are named and the rest counted, once each however many items a part blocked
    /// on — and the cause is stated once, so the modal is a sentence rather than a list of them.</summary>
    [Fact]
    public void A_refusal_names_three_parts_counts_the_rest_and_states_one_cause()
    {
        var report = new MigrationReport();
        foreach (string slot in new[] { "cloth1_lod0", "cloth1_lod0", "hair_lod0", "body_lod0", "face_lod0" })
            report.Add("identity.part", MigrationDisposition.Unresolved, $"Vesna / VesnaSSR01 / {slot}",
                "renderer slot did not re-anchor uniquely in the current install");

        string refusal = AuthoredProjectDocument.CannotUpdate(report);

        Assert.Contains("Couldn't update these parts of it: cloth1_lod0, hair_lod0, body_lod0, and 1 more.",
            refusal, StringComparison.Ordinal);
        Assert.Contains("Renderer slot did not re-anchor uniquely in the current install.", refusal,
            StringComparison.Ordinal);
    }

    /// <summary>A released project the current install cannot re-anchor: one mesh edit whose part no resolver
    /// answers for. Returns the project folder.</summary>
    private string BlockedProject(string name)
    {
        string dir = Path.Combine(_root, name);
        var legacy = new ModProject
        {
            RootDir = dir,
            Selection = new List<SelectionEntry>
            {
                new() { Character = "Vesna", Outfit = "VesnaSSR01" },
            },
            Targets = new List<ProjectTarget>
            {
                new()
                {
                    AssetType = "Mesh", ObjectName = "c_vesna_body_lod0",
                    Bundle = "old.bundle", PathId = 7,
                    SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
                    ReplaceFile = "edit.glb", OriginalFile = "orig.glb",
                },
            },
        };
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "edit.glb"), new byte[] { 1, 2 });
        File.WriteAllBytes(Path.Combine(dir, "orig.glb"), new byte[] { 3, 4 });
        legacy.Save();
        return dir;
    }

    [Fact]
    public void A_stale_hidden_entry_is_omitted_instead_of_blocking_migration()
    {
        string dir = Path.Combine(_root, "blocked");
        var legacy = new ModProject
        {
            RootDir = dir,
            Selection = new List<SelectionEntry>
            {
                new() { Character = "Vesna", Outfit = "VesnaSSR01" },
            },
            Targets = new List<ProjectTarget>
            {
                new()
                {
                    AssetType = "Mesh", ObjectName = "c_vesna_body_lod0",
                    Bundle = "old.bundle", PathId = 7,
                    SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
                    ReplaceFile = "same.glb", OriginalFile = "same.glb",
                },
            },
        };
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "same.glb"), new byte[] { 1 });
        legacy.SetHidden("Vesna", "VesnaSSR01", "c_vesna_body_lod0", true);
        legacy.Save();
        string file = ModProject.ManifestPathFor(dir);
        string before = File.ReadAllText(file);
        var document = AuthoredProjectDocument.Load(dir, _ => null);

        document.Save();

        Assert.Equal(before, File.ReadAllText(file + ".bak"));
        Assert.Equal(AuthoredProject.CurrentSchema, AuthoredProjectSerializer.SchemaOf(dir));
        Assert.Empty(AuthoredProjectSerializer.Load(dir).Always);
        Assert.Contains(document.LastMigrationReport!.Items,
            item => item.Code == "hidden.stale" && !item.BlocksSave);
    }

    [Fact]
    public void Migrated_copy_leaves_the_released_source_manifest_unchanged()
    {
        string source = Path.Combine(_root, "copy-source");
        string destination = Path.Combine(_root, "copy-destination");
        var legacy = new ModProject
        {
            RootDir = source,
            Info = new ProjectInfo { Name = "Source", Author = "Tester" },
        };
        legacy.Save();
        string before = File.ReadAllText(ModProject.ManifestPathFor(source));

        legacy.CopyTo(destination);
        var copy = AuthoredProjectDocument.Load(destination, _ => null);
        copy.Session!.SetName("Migrated copy");
        copy.Save(destination);

        Assert.Equal(before, File.ReadAllText(ModProject.ManifestPathFor(source)));
        Assert.Equal(ModProject.CurrentSchema, AuthoredProjectSerializer.SchemaOf(source));
        Assert.Equal(AuthoredProject.CurrentSchema, AuthoredProjectSerializer.SchemaOf(destination));
        Assert.Equal("Migrated copy", AuthoredProjectSerializer.Load(destination).Info.Name);
        Assert.True(File.Exists(ModProject.BackupPathFor(destination)));
    }

    [Fact]
    public void Authored_metadata_saves_without_a_mounted_game_install()
    {
        string dir = Path.Combine(_root, "recorded-save");
        var project = ProjectWithAlternative();
        project.RootDir = dir;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "active.glb"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "alt.glb"), new byte[] { 2 });
        var part = Part();
        project.WorkspaceIndex = new AuthoredWorkspaceIndex
        {
            Selection = new List<SelectionEntry>
                { new() { Character = part.Subject, Outfit = part.Outfit } },
            Records = new List<AuthoredWorkspaceRecord>(),
        };
        AuthoredProjectSerializer.Save(project, dir);
        var document = AuthoredProjectDocument.Load(dir);
        document.Session!.SetIdentity(project.Info.Name, project.Info.Version, project.Info.Author,
            "metadata-only edit", null, false, null, null);

        document.Save(dir);

        Assert.Equal("metadata-only edit", AuthoredProjectSerializer.Load(dir).Info.Description);
        Assert.Equal("edit-active",
            Assert.Single(AuthoredProjectSerializer.Load(dir).Always));
    }

    /// <summary>A conversion blocked only by a per-field validator line, or by the exception a part resolve
    /// threw, names the parts and stops. Neither of those details was written for anyone outside the code,
    /// and a refusal that pastes one leaves the modder reading the model's own identifiers.</summary>
    [Fact]
    public void A_refusal_never_states_a_cause_the_machinery_worded_for_itself()
    {
        var report = new MigrationReport();
        report.Add("intent.validation", MigrationDisposition.Unresolved, "project",
            "slot 'slot-0001' names no exact game object");
        report.Add("identity.resolve", MigrationDisposition.Unresolved, "Vesna / VesnaSSR01 / body",
            "Object reference not set to an instance of an object.");

        string refusal = AuthoredProjectDocument.CannotUpdate(report);

        Assert.DoesNotContain("slot-0001", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("Object reference", refusal, StringComparison.Ordinal);
        Assert.Contains("body", refusal, StringComparison.Ordinal);
        // …and the whole account is still there for the log the failed open writes.
        Assert.Contains("slot-0001", AuthoredProjectDocument.ReportForTheLog(report),
            StringComparison.Ordinal);
    }

    /// <summary>Why an install-less save needs no route check of its own: a route naming no exact object
    /// cannot be in a schema-2 project at all. MEASURED — the model refuses it at the serializer, so it never
    /// reaches a save to be caught there, and the save says which slot rather than which install.</summary>
    [Fact]
    public void A_route_with_no_recorded_exact_object_cannot_be_in_a_schema_two_project_at_all()
    {
        string dir = Path.Combine(_root, "unrecorded-save");
        var project = ProjectWithAlternative();
        project.RootDir = dir;
        Directory.CreateDirectory(dir);
        // The shape the adapter files for a route that did not re-anchor: named, and nowhere in this build.
        project.TargetSlots.Single(slot => slot.Id == "slot-active").Mesh = new GameAssetRef
        {
            GameBuild = "", LogicalBundle = "", PathId = 0, Name = "c_vesna_body_lod0_mesh",
        };

        var refused = Assert.Throws<InvalidDataException>(
            () => AuthoredProjectSerializer.Save(project, dir));
        Assert.Equal(AuthoredProjectSerializer.DamagedProject, refused.Message);
        Assert.Contains("slot-active", refused.InnerException!.Message);
        Assert.Contains("mesh has no path id", string.Join("; ", AuthoredProjectValidator.Errors(project)));
        // And the same verdict is what a session refuses to open it with, so no command can mint one either.
        Assert.Throws<InvalidDataException>(() => new AuthoredEditSession(project));
    }

    private static AuthoredProject ProjectWithAlternative()
    {
        var part = Part();
        var renderer = Game("prefab.bundle", 10, part.RendererSlot);
        var mesh = Game("mesh.bundle", 20, part.RendererSlot + "_mesh");
        return new AuthoredProject
        {
            Info = new ProjectInfo { Name = "Alternatives", Author = "Tester" },
            ProjectAssets = new List<ProjectAsset>
            {
                new() { Id = "asset-active", Kind = ProjectAssetKind.Geometry,
                    Label = "Active", File = "active.glb" },
                new() { Id = "asset-alt", Kind = ProjectAssetKind.Geometry,
                    Label = "Alternative", File = "alt.glb" },
            },
            TargetSlots = new List<TargetSlot>
            {
                new() { Id = "slot-active", Part = part,
                    Tier = "lod0", Input = TargetInputKind.Geometry, Renderer = renderer, Mesh = mesh },
                new() { Id = "slot-alt", Part = part,
                    Tier = "lod0", Input = TargetInputKind.Geometry, Renderer = renderer, Mesh = mesh },
            },
            EditDefinitions = new List<EditDefinition>
            {
                new() { Id = "edit-active", Target = part, Label = "Active", Bindings = new List<Binding>
                    { new() { SlotId = "slot-active", Kind = BindingKind.ProjectAsset,
                        ProjectAssetId = "asset-active" } } },
                new() { Id = "edit-alt", Target = part, Label = "Alternative", Bindings = new List<Binding>
                    { new() { SlotId = "slot-alt", Kind = BindingKind.ProjectAsset,
                        ProjectAssetId = "asset-alt" } } },
            },
            Always = new List<string> { "edit-active" },
        };
    }

    private static TargetPart Part() => new()
    {
        Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "c_vesna_body_lod0",
    };

    private static GameAssetRef Game(string bundle, long pathId, string name) => new()
    {
        GameBuild = "26109", LogicalBundle = bundle, PathId = pathId, Name = name,
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
