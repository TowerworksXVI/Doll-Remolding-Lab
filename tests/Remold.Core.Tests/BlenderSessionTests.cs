using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.App.ViewModels;
using Remold.Core.Blender;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Skeleton;
using Remold.Core.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The session contract between the app and the Blender bridge, both directions.
///
/// <para>Outbound: every open carries the whole outfit on one armature, so the glb alone cannot say which
/// mesh the session may write back. The session file beside it does.</para>
///
/// <para>Inbound: a send carries only the parts that shipped, so an absent part means nothing — the
/// emptied-part list is the only thing that hides one. And because the session glb carries EVERY part's
/// maps, its map sidecar has to survive the publish, or a stock map read back resolves as authored.</para>
/// </summary>
public class BlenderSessionTests
{
    // ---------------------------------------------------------------- outbound: the session file

    [Fact]
    public void WriteSession_RoundTripsThePartAndTheEditedFlags()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        BlenderBridge.WriteSession(glb, "body1_lod0", new[]
        {
            new SessionPart("body1_lod0", Edited: true),
            new SessionPart("cloth1_lod0", Edited: false),
        });

        Assert.EndsWith("_combined.gf2session.json", BlenderBridge.SessionPath(glb));
        var session = BlenderBridge.ReadSessionDocument(glb)!;
        Assert.Equal("body1_lod0", session.Part);
        Assert.Equal(new[] { "body1_lod0", "cloth1_lod0" }, session.Parts.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { true, false }, session.Parts.Select(p => p.Edited).ToArray());
    }

    [Fact]
    public void WriteSession_RoundTripsTheLiveEditAndViewportContract()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        var edits = new[]
        {
            new BlenderSessionEdit("edit-long", "Long", HoldsAuthoredMesh: true),
            new BlenderSessionEdit("edit-maps", "Blue", HoldsAuthoredMesh: false),
        };
        var notices = MainWindowViewModel.BlenderOpenNotices(new[] { "hair" },
            wardrobeUnreadable: true, new[] { "cloth_d" });
        BlenderBridge.WriteSession(glb, "body1_lod0", new[]
        {
            new SessionPart("body1_lod0", Edited: true, EditId: "edit-long", Edits: edits,
                DefaultEditName: "Edit 3", ViewportVisible: false),
        }, sendAs: "return.glb", notices: notices);

        var session = BlenderBridge.ReadSessionDocument(glb);

        Assert.NotNull(session);
        Assert.Equal(1, session!.Revision);
        Assert.Equal("body1_lod0", session.Part);
        Assert.Equal("return.glb", session.SendAs);
        Assert.Equal(notices, session.Notices);
        Assert.All(session.Notices!, notice => Assert.DoesNotContain("n't", notice));
        var part = Assert.Single(session.Parts);
        Assert.Equal("edit-long", part.OpenedFromEditId);
        Assert.Equal(edits, part.Edits);
        Assert.Equal("Edit 3", part.DefaultEditName);
        Assert.False(part.IsViewportVisible);
        var json = File.ReadAllText(BlenderBridge.SessionPath(glb));
        Assert.Contains("\"holdsAuthoredMesh\": true", json);
        Assert.Contains("\"viewportVisible\": false", json);
    }

    [Fact]
    public void SessionRewriteFailure_NamesTheStalePanelConsequence()
    {
        using var g = new TempGame();
        string opened = g.At("composition.glb");

        Assert.Equal("Could not update composition.gf2session.json after this send — "
            + "the Blender panel may offer stale targets until reopened.",
            MainWindowViewModel.BlenderSessionRewriteFailure(opened));
    }

    [Fact]
    public void SupersededIngressCleanup_LogsAnUnsafePathWithoutDeletingIt()
    {
        using var g = new TempGame();
        string project = g.At("project");
        string outside = g.At("outside/return.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "keep");
        var log = new List<string>();

        MainWindowViewModel.DeleteSupersededBlenderIngress(project, outside, log.Add);

        Assert.True(File.Exists(outside));
        Assert.Contains("outside", Assert.Single(log));
    }

    [Fact]
    public void RewriteSession_AtomicallyAdvancesTheLiveRevisionAndPreservesTheRun()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        BlenderBridge.WriteSession(glb, "cloth1_lod0", new[]
        {
            new SessionPart("cloth1_lod0", Edited: false, DefaultEditName: "Edit 1"),
        }, sendAs: "return.glb");

        bool rewritten = BlenderBridge.RewriteSession(glb, session => session with
        {
            Parts = session.Parts.Select(part => part with
            {
                EditId = "edit-minted",
                Edits = new[] { new BlenderSessionEdit("edit-minted", "Edit 1", true) },
                DefaultEditName = "Edit 2",
            }).ToList(),
        });

        Assert.True(rewritten);
        var session = BlenderBridge.ReadSessionDocument(glb);
        Assert.NotNull(session);
        Assert.Equal(2, session!.Revision);
        Assert.Equal("cloth1_lod0", session.Part);
        Assert.Equal("return.glb", session.SendAs);
        var part = Assert.Single(session.Parts);
        Assert.Equal("edit-minted", part.OpenedFromEditId);
        Assert.Equal("Edit 2", part.DefaultEditName);
        Assert.Empty(Directory.EnumerateFiles(g.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void AcknowledgeReturn_AdvancesImmutableBaselinesAndWritesTargetsBeforeTheRevision()
    {
        using var g = new TempGame();
        string opened = g.At("run/composition.glb");
        string returned = g.At("run/return.glb");
        string originalWorkspace = g.At("run/parts/cloth.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(opened)!);
        Directory.CreateDirectory(Path.GetDirectoryName(originalWorkspace)!);
        File.WriteAllBytes(opened, new byte[] { 9 });
        File.WriteAllBytes(originalWorkspace, new byte[] { 8 });
        BlenderBridge.WriteSession(opened, null,
            new[] { new SessionPart("cloth", false, Edits: Array.Empty<BlenderSessionEdit>()) },
            "return.glb", new[]
            {
                new BlenderSessionTarget("cloth", "", originalWorkspace,
                    Subject: "Vesna", Outfit: "VesnaSSR01"),
            });

        string preparedDirectory = g.At("prepared/cloth");
        Directory.CreateDirectory(preparedDirectory);
        string prepared = Path.Combine(preparedDirectory, "workspace.glb");
        File.WriteAllBytes(prepared, new byte[] { 7 });
        string ingress = g.At("ingress/return.glb");
        var exact = new BlenderSessionTarget("cloth", "asset-2", prepared, "edit-2", "slot-2",
            ingress, BindingKind.ProjectAsset, Array.Empty<BlenderSlotBaseline>(),
            Subject: "Vesna", Outfit: "VesnaSSR01");

        File.WriteAllBytes(returned, new byte[] { 1 });
        Assert.True(BlenderBridge.AcknowledgeReturn(opened, returned,
            session => session with { Notices = new List<string> { "First" } },
            new[] { new BlenderTargetAcknowledgement(exact, prepared) }));

        string firstBaseline = BlenderBridge.ReadReturnBaseline(returned)!;
        Assert.NotEqual(Path.GetFullPath(returned), Path.GetFullPath(firstBaseline));
        Assert.NotEqual(Path.GetFullPath(opened), Path.GetFullPath(firstBaseline));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(firstBaseline));
        Assert.Equal(Path.GetFullPath(opened), Path.GetFullPath(
            BlenderBridge.ReadReturnSessionGlb(returned)!));
        var firstTarget = Assert.Single(BlenderBridge.ReadReturnTargets(returned));
        Assert.Equal("asset-2", firstTarget.ProjectAssetId);
        Assert.Equal("edit-2", firstTarget.EditDefinitionId);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(firstTarget.Workspace));
        Assert.Equal(2, BlenderBridge.ReadSessionDocument(opened)!.Revision);

        File.WriteAllBytes(returned, new byte[] { 2 });
        Assert.True(BlenderBridge.AcknowledgeReturn(opened, returned,
            session => session, new[] { new BlenderTargetAcknowledgement(exact, prepared) }));
        string secondBaseline = BlenderBridge.ReadReturnBaseline(returned)!;

        Assert.NotEqual(firstBaseline, secondBaseline);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(firstBaseline));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(secondBaseline));
        Assert.Equal(3, BlenderBridge.ReadSessionDocument(opened)!.Revision);
        Assert.Empty(Directory.EnumerateFiles(g.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void SessionReadAndRewrite_LeaveAnUnreadableFileUntouched()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        var path = BlenderBridge.SessionPath(glb);
        const string broken = "{not a complete session";
        File.WriteAllText(path, broken);

        Assert.Null(BlenderBridge.ReadSessionDocument(glb));
        Assert.False(BlenderBridge.RewriteSession(glb, session => session));
        Assert.Equal(broken, File.ReadAllText(path));
    }

    /// <summary>The bridge reads <c>sendAs</c> off the session json (its reader is a deliberate copy of
    /// this contract), so the property name and value are pinned here. Omitting it remains compatible with
    /// hand-written/older sessions, but app-created sessions name a distinct return artifact.</summary>
    [Fact]
    public void WriteSession_NamesTheSendFile_OnlyWhenAsked()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        var parts = new[] { new SessionPart("body1_lod0", Edited: false) };

        BlenderBridge.WriteSession(glb, null, parts, sendAs: AssetExporter.SessionSendGlbName);
        Assert.Contains($"\"sendAs\": \"{AssetExporter.SessionSendGlbName}\"",
            File.ReadAllText(BlenderBridge.SessionPath(glb)));

        BlenderBridge.WriteSession(glb, null, parts);
        Assert.DoesNotContain("sendAs", File.ReadAllText(BlenderBridge.SessionPath(glb)));
    }

    [Fact]
    public void APartSendPathIsDistinctAndMapsBackToItsWorkspaceGlb()
    {
        using var g = new TempGame();
        var workspace = g.At("body1_lod0.glb");

        var send = BlenderBridge.PartSendPath(workspace);

        Assert.Equal("body1_lod0.send.glb", BlenderBridge.PartSendName(workspace));
        Assert.NotEqual(Path.GetFullPath(workspace), Path.GetFullPath(send));
        Assert.Equal(Path.GetFullPath(workspace), Path.GetFullPath(BlenderBridge.WorkspaceForPartSend(send)!));
        Assert.Null(BlenderBridge.WorkspaceForPartSend(workspace));
    }

    [Fact]
    public void AnAppSessionMapsItsReturnBySessionAndProjectAssetIdentity()
    {
        using var g = new TempGame();
        var opened = g.At("opened.glb");
        var workspace = g.At("body1_lod0.glb");
        var send = BlenderBridge.PartSendPath(opened);

        BlenderBridge.WriteSession(opened, "body1_lod0",
            new[] { new SessionPart("body1_lod0", false) },
            sendAs: BlenderBridge.PartSendName(opened),
            targets: new[] { new BlenderSessionTarget("body1_lod0", "asset-body", workspace) });

        var target = Assert.Single(BlenderBridge.ReadReturnTargets(send));
        Assert.Equal("asset-body", target.ProjectAssetId);
        Assert.Equal(Path.GetFullPath(workspace), target.Workspace);
        Assert.Equal(Path.GetFullPath(workspace), BlenderBridge.WorkspaceForPartSend(send));
    }

    /// <summary>A part-addressable route carries the subject identity needed to resolve its send-time
    /// selection and the prepared glb; the reader admits it beside exact-slot compatibility rows.</summary>
    [Fact]
    public void A_part_route_target_round_trips_with_its_subject_identity()
    {
        using var g = new TempGame();
        var opened = g.At("opened.glb");
        string prepared = g.At("cloth1_lod0.glb");
        var send = BlenderBridge.PartSendPath(opened);

        BlenderBridge.WriteSession(opened, null,
            new[] { new SessionPart("cloth1_lod0", false) },
            sendAs: BlenderBridge.PartSendName(opened), targets: new[]
            {
                new BlenderSessionTarget("cloth1_lod0", "", prepared,
                    Subject: "Vesna", Outfit: "VesnaSSR01"),
            });

        var target = Assert.Single(BlenderBridge.ReadReturnTargets(send));
        Assert.True(target.IsPartRoute);
        Assert.False(target.IsExactSlot);
        Assert.Equal("Vesna", target.Subject);
        Assert.Equal("VesnaSSR01", target.Outfit);
        Assert.Equal(Path.GetFullPath(prepared), target.Workspace);
    }

    /// <summary>The route-shape contract that replaces launch-time action classification: modern exact
    /// rows are also part-addressable, stock rows are part-addressable only, and the old asset/workspace
    /// row is neither shape.</summary>
    [Fact]
    public void A_return_target_classifies_exact_and_part_route_shapes()
    {
        var exact = new BlenderSessionTarget("body", "asset", @"C:\w\body.glb",
            "edit-1", "slot-1", @"C:\i\return.glb", Subject: "Vesna", Outfit: "VesnaSSR01");
        var part = new BlenderSessionTarget("cloth", "", @"C:\w\cloth.glb",
            Subject: "Vesna", Outfit: "VesnaSSR01");
        var legacy = new BlenderSessionTarget("hair", "asset", @"C:\w\hair.glb");

        Assert.True(exact.IsExactSlot);
        Assert.True(exact.IsPartRoute);
        Assert.False(part.IsExactSlot);
        Assert.True(part.IsPartRoute);
        Assert.False(legacy.IsExactSlot);
        Assert.False(legacy.IsPartRoute);
    }

    /// <summary>Emptying a part and sending it back is the modder saying the part is not to draw, so the
    /// return puts its hide in Always. The library's activation rule alone would leave the hide unplaced on
    /// every part that already has an answer — which is nearly all of them — and the send would change
    /// nothing while reporting nothing.</summary>
    [Fact]
    public void An_emptied_parts_return_hides_the_part_and_counts_it()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        Assert.Equal(new[] { "edit-long" }, session.Snapshot().Always);

        int hidden = HideEmptied(session, AuthoredEditFixtures.Body);

        Assert.Equal(1, hidden);
        var hide = Assert.Single(session.Outline().Edits, edit => edit.Kind == EditDefinitionKind.Hide);
        Assert.Contains(hide.Id, session.Snapshot().Always);
        // The part's content edit is left exactly where it was: the send said hide it, not delete it.
        Assert.Contains("edit-long", session.Snapshot().Always);
    }

    /// <summary>Sending the same emptied part twice changes nothing the second time, and says so by
    /// counting nothing.</summary>
    [Fact]
    public void A_second_send_of_an_emptied_part_changes_nothing()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        HideEmptied(session, AuthoredEditFixtures.Body);
        long revision = session.Revision;

        int hidden = HideEmptied(session, AuthoredEditFixtures.Body);

        Assert.Equal(0, hidden);
        Assert.Equal(revision, session.Revision);
    }

    /// <summary>The hide route as the return itself takes it: inside the one compound transaction the whole
    /// send commits as.</summary>
    private static int HideEmptied(AuthoredEditSession session, TargetPart part)
    {
        int hidden = 0;
        session.Compound(change =>
            hidden = MainWindowViewModel.HideEmptiedParts(change, new[] { part }));
        return hidden;
    }

    [Fact]
    public void Exact_session_targets_round_trip_edit_slot_ingress_and_starting_binding()
    {
        using var g = new TempGame();
        string opened = g.At("opened.glb");
        string send = BlenderBridge.PartSendPath(opened);
        string ingress = g.At(Path.Combine(".ingress", "edit-2", "slot-geometry", "return.glb"));
        var materials = new[]
        {
            new BlenderSlotBaseline("slot-base-0", 0, TargetInputKind.BaseColor,
                BindingKind.ProjectAsset, "picture-2"),
            new BlenderSlotBaseline("slot-normal-0", 0, TargetInputKind.Normal,
                BindingKind.SourceSlot, SourceSlotId: "slot-normal-1", SourceEditDefinitionId: "edit-2"),
        };
        BlenderBridge.WriteSession(opened, "body1_lod0",
            new[] { new SessionPart("body1_lod0", true, EditId: "edit-2") },
            sendAs: BlenderBridge.PartSendName(opened), targets: new[]
            {
                new BlenderSessionTarget("body1_lod0", "asset-2", opened, "edit-2",
                    "slot-geometry", ingress, BindingKind.ProjectAsset, materials),
            });

        var target = Assert.Single(BlenderBridge.ReadReturnTargets(send));
        Assert.True(target.IsExactSlot);
        Assert.Equal("edit-2", target.EditDefinitionId);
        Assert.Equal("slot-geometry", target.SlotId);
        Assert.Equal(Path.GetFullPath(ingress), target.IngressReturn);
        Assert.Equal(BindingKind.ProjectAsset, target.SourceBindingKind);
        Assert.Equal(materials, target.MaterialSlots);
    }

    [Fact]
    public void Edit_2_resolves_its_own_geometry_instead_of_edit_1s()
    {
        using var g = new TempGame();
        var project = AuthoredEditFixtures.Golden();
        project.RootDir = g.Root;
        string meshes = g.At("meshes");
        Directory.CreateDirectory(meshes);
        File.WriteAllBytes(Path.Combine(meshes, "long.glb"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(meshes, "short.glb"), new byte[] { 2 });
        var session = new AuthoredEditSession(project);

        var first = MainWindowViewModel.GeometryFile(session.Slots("edit-long"), g.Root);
        var second = MainWindowViewModel.GeometryFile(session.Slots("edit-short"), g.Root);

        Assert.Equal(Path.Combine(meshes, "long.glb"), first.Path);
        Assert.Equal(Path.Combine(meshes, "short.glb"), second.Path);
        Assert.Null(first.Missing);
        Assert.Null(second.Missing);
    }

    [Fact]
    public void Scene_sources_separate_direct_edits_first_edit_open_all_and_stock_references()
    {
        var project = AuthoredEditFixtures.Golden();
        Assert.Equal("edit-long",
            MainWindowViewModel.ActiveOrFirstContentEdit(project, AuthoredEditFixtures.Body));

        project.Always.Clear();
        project.Always.Add("edit-short");
        Assert.Equal("edit-short",
            MainWindowViewModel.ActiveOrFirstContentEdit(project, AuthoredEditFixtures.Body));

        project.EditDefinitions.Insert(0, new EditDefinition
        {
            Id = "hide-body", Kind = EditDefinitionKind.Hide, Label = "Hidden",
            Target = AuthoredEditFixtures.Body,
        });
        project.Always.Clear();
        project.Always.Add("hide-body");
        project.EditDefinitions.Add(new EditDefinition
        {
            Id = "edit-hair", Kind = EditDefinitionKind.Content, Label = "Hair edit",
            Target = AuthoredEditFixtures.Hair,
        });
        project.EditDefinitions.Add(new EditDefinition
        {
            Id = "hide-hair", Kind = EditDefinitionKind.Hide, Label = "Hidden",
            Target = AuthoredEditFixtures.Hair,
        });
        project.Always.Add("hide-hair");

        // The edit entrance skips the active hide and takes the first content edit in authored order.
        Assert.Equal("edit-long",
            MainWindowViewModel.ActiveOrFirstContentEdit(project, AuthoredEditFixtures.Body));
        Assert.Equal("edit-long", MainWindowViewModel.SessionBlenderSourceEdit(project,
            AuthoredEditFixtures.Body, requested: null, requestedEditId: null,
            openAllFromFirstEdit: true));
        // The all-stock entrance and a reference beside a direct edit both stay stock.
        Assert.Null(MainWindowViewModel.SessionBlenderSourceEdit(project, AuthoredEditFixtures.Body,
            requested: null, requestedEditId: null, openAllFromFirstEdit: false));
        Assert.Null(MainWindowViewModel.SessionBlenderSourceEdit(project, AuthoredEditFixtures.Hair,
            AuthoredEditFixtures.Body, requestedEditId: "edit-short", openAllFromFirstEdit: false));
        Assert.False(MainWindowViewModel.SessionBlenderViewportVisible(project,
            AuthoredEditFixtures.Hair, carriesReferences: true));
        // Only the direct target gets the edit it explicitly addressed.
        Assert.Equal("edit-short", MainWindowViewModel.SessionBlenderSourceEdit(project,
            AuthoredEditFixtures.Body, AuthoredEditFixtures.Body, "edit-short",
            openAllFromFirstEdit: false));

        Assert.False(MainWindowViewModel.SessionBlenderViewportVisible(project,
            AuthoredEditFixtures.Body, carriesReferences: true));
        Assert.True(MainWindowViewModel.SessionBlenderViewportVisible(project,
            AuthoredEditFixtures.Body, carriesReferences: false));
    }

    [Fact]
    public void MalformedAppTargetMetadataNeverFallsBackToTheReturnFilename()
    {
        using var g = new TempGame();
        var workspace = g.At("body1_lod0.glb");
        var send = BlenderBridge.PartSendPath(workspace);
        File.WriteAllText(BlenderBridge.TargetPath(send), "{not valid json");

        Assert.True(BlenderBridge.ReturnTargetMetadataExists(send));
        Assert.Empty(BlenderBridge.ReadReturnTargets(send));
        Assert.Null(BlenderBridge.WorkspaceForPartSend(send));
    }

    /// <summary>The unskinned marker is the bridge's authority for exempting a part from the weight gate
    /// (its reader is a deliberate copy of this contract), so the property name and its omission are pinned
    /// here. A skinned part writes no key at all, so a session file stays byte-identical to what an
    /// all-skinned outfit has always written.</summary>
    [Fact]
    public void WriteSession_MarksAnUnskinnedPart_AndSaysNothingAboutASkinnedOne()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        BlenderBridge.WriteSession(glb, null, new[]
        {
            new SessionPart("prop_lod0", Edited: false, Unskinned: true),
            new SessionPart("body1_lod0", Edited: false),
        });

        var json = File.ReadAllText(BlenderBridge.SessionPath(glb));
        Assert.Contains("\"unskinned\": true", json);
        Assert.Equal(1, json.Split("\"unskinned\"").Length - 1);   // the skinned part writes no key

        var parts = BlenderBridge.ReadSessionDocument(glb)!.Parts;
        Assert.Equal(new[] { true, false }, parts.Select(p => p.Unskinned).ToArray());
    }

    /// <summary>A session file written before the marker existed reads as fully skinned, which is the
    /// state that still blocks — the exemption is only ever an explicit declaration.</summary>
    [Fact]
    public void ReadSessionDocument_APartWithNoUnskinnedKey_ReadsAsSkinned()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        File.WriteAllText(BlenderBridge.SessionPath(glb),
            """{"part":null,"parts":[{"name":"body1_lod0","edited":false,"writable":true}]}""");

        var parts = BlenderBridge.ReadSessionDocument(glb)!.Parts;
        Assert.False(Assert.Single(parts).Unskinned);
    }

    [Fact]
    public void WriteSession_NoNamedPart_MeansEveryPartIsWritable()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        BlenderBridge.WriteSession(glb, null, new[] { new SessionPart("body1_lod0", false) });

        var session = BlenderBridge.ReadSessionDocument(glb)!;
        Assert.Null(session.Part);                       // the bridge reads this as "all of them"
        Assert.Single(session.Parts);
    }


    /// <summary>The sidecar's default: a part written without the flag is writable, so the shape a session
    /// declares nothing about behaves exactly as it did.</summary>
    [Fact]
    public void ASessionPartWithNoWritableFlag_ReadsAsWritable()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        File.WriteAllText(BlenderBridge.SessionPath(glb),
            "{\"part\": null, \"parts\": [{\"name\": \"body1_lod0\", \"edited\": false}]}");

        var session = BlenderBridge.ReadSessionDocument(glb)!;

        var part = Assert.Single(session.Parts);
        Assert.True(part.IsWritable);
        Assert.True(part.IsViewportVisible);
        Assert.Null(part.OpenedFromEditId);
        Assert.Null(part.Edits);
        Assert.Null(part.DefaultEditName);
        Assert.Equal(0, session.Revision);
    }

    private static readonly float[] SessionTri = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] SessionIdx = { 0, 1, 2 };
    private static readonly uint[] SessionBones = { 11u, 22u };

    [Fact]
    public void ReadSessionDocument_NoFile_ReadsAsNoSession()
    {
        using var g = new TempGame();
        Assert.Null(BlenderBridge.ReadSessionDocument(g.At("nothing.glb")));
    }

    // ---------------------------------------------------------------- inbound: the emptied-part list

    [Fact]
    public void ReadSend_CarriesTheEmptiedPartsTheBridgeNamed()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb),
            "{\"source\":\"blender-send\",\"hiddenParts\":[\"cloth1_lod0\",\"hair_lod0\"]}");

        var edit = BlenderBridge.ReadSend(glb);

        Assert.Equal(new[] { "cloth1_lod0", "hair_lod0" }, edit.HiddenParts!.ToArray());
    }

    [Fact]
    public void ReadSend_SidecarNamesNoEmptiedParts_HidesNothing()
    {
        // Nothing hides by default: absence of a mesh is never the signal, so a sidecar naming no emptied
        // part leaves every Hide toggle exactly as the modder set it.
        using var g = new TempGame();
        var glb = g.At("body1_lod0.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");

        Assert.Empty(BlenderBridge.ReadSend(glb).HiddenParts!);
    }

    [Fact]
    public void ReadSend_AHideOnlySend_CarriesNoMeshAndStillNamesWhatToHide()
    {
        // Emptying the one part a per-part session holds is how it is hidden, so the send goes out with no
        // mesh at all. Reading that back as an unreadable glb would throw the Hide away.
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        var model = SharpGLTF.Schema2.ModelRoot.CreateModel();
        model.UseScene("scene").CreateNode("armature");
        model.SaveGLB(glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb),
            "{\"source\":\"blender-send\",\"hiddenParts\":[\"cloth2_lod0\"]}");

        var edit = BlenderBridge.ReadSend(glb);

        Assert.Null(edit.Mesh);
        Assert.Equal(new[] { "cloth2_lod0" }, edit.HiddenParts!.ToArray());
        Assert.Empty(MeshGltf.MeshNames(glb));   // …so no part reads as "came back" and none is rewritten
    }

    [Fact]
    public void MeshNames_ReportWhatASendActuallyCarried()
    {
        // How the app tells a part that came back from one that was only context: the target list cannot
        // say, since every part of the outfit has a target either way.
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0"), RiggedPart("cloth1_lod0") },
            h => Paths[h], glb);

        Assert.Equal(new[] { "body1_lod0", "cloth1_lod0" }, MeshGltf.MeshNames(glb).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void ASinglePartSend_AppliesToThatPartAlone()
    {
        // The two primitives the send-back's per-target decision rests on: what the glb carries, and that a
        // name it does NOT carry is a loud miss, not a silent read of the first mesh. Without both, a
        // one-part send into a multi-part outfit writes that part's geometry over its siblings.
        using var g = new TempGame();
        var returned = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("cloth1_lod0") }, h => Paths[h], returned);

        Assert.Equal(new[] { "cloth1_lod0" }, MeshGltf.MeshNames(returned).ToArray());
        MeshGltf.ReexportPartGlb(returned, "cloth1_lod0", g.At("cloth1.glb"));
        Assert.Contains("body1_lod0", Assert.Throws<InvalidOperationException>(
            () => MeshGltf.ReexportPartGlb(returned, "body1_lod0", g.At("body1.glb"))).Message);
    }

    [Fact]
    public void ACombinedContextPart_ExportsPosedAtItsPrefabSceneRest()
    {
        // The weapon shape: rigid geometry authored at its own origin, placed only by the prefab's rest.
        // A context part never round-trips, so its glb carries POSED bytes and its joint sits at the posed
        // world — in Blender mesh and bone agree, instead of the mesh at the origin under a floating bone.
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        const uint HW = 0x2222_2222;
        var mount = Matrix4x4.CreateTranslation(0, 0.5f, 2);
        var weapon = new MeshGltf.RiggedPart(Triangle("weapon_lod0"),
            new MeshSkin { BoneHashes = new[] { HW }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } },
            ContextPose: new[] { mount });
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0"), weapon },
            h => h == HRoot ? "root" : "root/weapon_01", glb);

        // the context mesh reads back posed; its sibling stays raw
        var raw = Triangle("weapon_lod0").Channels["Vertex"];
        var posed = MeshGltf.ImportGlb(glb, "weapon_lod0").Channels["Vertex"];
        for (int i = 0; i < raw.Length; i += 3)
        {
            Assert.Equal(raw[i], posed[i], 3);
            Assert.Equal(raw[i + 1] + 0.5f, posed[i + 1], 3);
            Assert.Equal(raw[i + 2] + 2f, posed[i + 2], 3);
        }
        Assert.Equal(Triangle("body1_lod0").Channels["Vertex"],
            MeshGltf.ImportGlb(glb, "body1_lod0").Channels["Vertex"]);

        // and the joint stands at the posed world, so posing it pivots the geometry that sits under it
        var model = SharpGLTF.Schema2.ModelRoot.Load(glb);
        var joint = model.LogicalNodes.Single(n => n.Name == "weapon_01_22222222");
        Assert.Equal(new System.Numerics.Vector3(0, 0.5f, 2), joint.WorldMatrix.Translation);
    }

    // ---------------------------------------------------------------- where each part class sits

    /// <summary>The open-weapon shape: a part the mod CAN replace, mounted by a translation the rest bake
    /// refuses. Its compile round-trips raw bind-space bytes, so the session poses neither its geometry nor
    /// its joints and the connector rests stay in bind space — mesh and armature together at its own
    /// origin.</summary>
    [Fact]
    public void ACombinedPose_AReplaceablePartWithATranslatedRest_TakesNoPose()
    {
        var rig = MountedRig();

        var (pose, connectors) = AssetExporter.CombinedPose(rig, uprighting: null, replaceable: () => true);

        Assert.Null(pose);
        Assert.Same(rig.ConnectorRests, connectors);
    }

    /// <summary>The closed-weapon shape: the SAME rest on a part no session can write back. It is context, so
    /// it poses at the prefab's scene rests and its connectors come back out of bind space.</summary>
    [Fact]
    public void ACombinedPose_AContextPartWithTheSameRest_PosesAtThePrefabSceneRests()
    {
        var rig = MountedRig();

        var (pose, connectors) = AssetExporter.CombinedPose(rig, uprighting: null, replaceable: () => false);

        Assert.Same(rig.BoneRestWorlds, pose);
        Assert.Equal(Mount.Translation, connectors!["hips"].Translation);
    }

    /// <summary>A part whose geometry already carries its rest takes no pose either way, and the gated read
    /// behind "replaceable" costs a bundle deobfuscate plus a mesh deserialize — so it is never asked where
    /// the answer cannot change the result.</summary>
    [Fact]
    public void ACombinedPose_ABakedPart_TakesNoPose_AndNeverPaysForTheGatedRead()
    {
        bool asked = false;

        var (pose, _) = AssetExporter.CombinedPose(MountedRig(), Matrix4x4.CreateRotationX(MathF.PI / 2),
            () => { asked = true; return false; });

        Assert.Null(pose);
        Assert.False(asked);
    }

    /// <summary>The decision through the export it feeds: with no pose the joint stands where the part's own
    /// bind pose puts it, over unposed geometry. That coincidence is what the modder sees as one object in
    /// Blender rather than a mesh at the origin under a bone floating at the mount.</summary>
    [Fact]
    public void ACombinedReplaceablePart_ExportsItsMeshAndItsJointAtItsOwnRest()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        const uint HW = 0x2222_2222;
        var (pose, connectors) = AssetExporter.CombinedPose(MountedRig(), uprighting: null, replaceable: () => true);
        var weapon = new MeshGltf.RiggedPart(Triangle("weapon_lod0"),
            new MeshSkin { BoneHashes = new[] { HW }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } },
            ConnectorRests: connectors, ContextPose: pose);

        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0"), weapon },
            h => h == HRoot ? "root" : "hips/weapon_01", glb);

        Assert.Equal(Triangle("weapon_lod0").Channels["Vertex"],
            MeshGltf.ImportGlb(glb, "weapon_lod0").Channels["Vertex"]);
        var model = SharpGLTF.Schema2.ModelRoot.Load(glb);
        var joint = model.LogicalNodes.Single(n => n.Name == "weapon_01_22222222");
        Assert.True(joint.WorldMatrix.Translation.Length() < 1e-5f,
            $"joint world translation: {joint.WorldMatrix.Translation}");
    }

    /// <summary>The same part opened ALONE, on its own workspace glb. That file is the one the compile round
    /// trips, so it carries no prefab placement either: the joint stands at the part's own bind rest over
    /// unposed geometry, exactly where the combined session puts a replaceable part. The two routes writing
    /// the same part to two different places is what a modder sees as the part jumping between sessions.</summary>
    [Fact]
    public void ALoneReplaceablePart_ExportsItsMeshAndItsJointAtItsOwnRest()
    {
        using var g = new TempGame();
        var ws = g.At("weapon_lod0.glb");
        const uint HW = 0x2222_2222;
        var rig = MountedRig();
        var skin = new MeshSkin { BoneHashes = new[] { HW }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } };

        MeshGltf.ExportRiggedGlb(Triangle("weapon_lod0"), skin, h => h == HRoot ? "root" : null, ws,
            scenePaths: rig.BonePaths, uprighting: null, connectorRests: rig.ConnectorRests);

        Assert.Equal(Triangle("weapon_lod0").Channels["Vertex"], MeshGltf.ImportGlb(ws).Channels["Vertex"]);
        var model = SharpGLTF.Schema2.ModelRoot.Load(ws);
        var joint = model.LogicalNodes.Single(n => n.Name == "weapon_01_22222222");
        Assert.True(joint.WorldMatrix.Translation.Length() < 1e-5f,
            $"joint world translation: {joint.WorldMatrix.Translation}");
        // the connector above it keeps its BIND-space rest: with no placement there is nothing to compose
        Assert.True(model.LogicalNodes.Single(n => n.Name == "hips").WorldMatrix.Translation.Length() < 1e-5f);
    }

    /// <summary>The two routes' answer for one part, side by side. A part that opens alone and the same part
    /// opened inside its outfit have to land in the same space, or an edit made in one session shows displaced
    /// in the other.</summary>
    [Fact]
    public void TheLoneAndCombinedRoutes_PlaceAReplaceablePartIdentically()
    {
        using var g = new TempGame();
        const uint HW = 0x2222_2222;
        var rig = MountedRig();
        var skin = new MeshSkin { BoneHashes = new[] { HW }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } };
        string Resolve(uint h) => h == HRoot ? "root" : "hips/weapon_01";

        var lone = g.At("weapon_lod0.glb");
        MeshGltf.ExportRiggedGlb(Triangle("weapon_lod0"), skin, Resolve, lone,
            scenePaths: rig.BonePaths, uprighting: null, connectorRests: rig.ConnectorRests);

        var combined = g.At("_combined.glb");
        var (pose, connectors) = AssetExporter.CombinedPose(rig, uprighting: null, replaceable: () => true);
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            RiggedPart("body1_lod0"),
            new MeshGltf.RiggedPart(Triangle("weapon_lod0"), skin, ConnectorRests: connectors, ContextPose: pose),
        }, Resolve, combined);

        Assert.Equal(MeshGltf.ImportGlb(combined, "weapon_lod0").Channels["Vertex"],
                     MeshGltf.ImportGlb(lone).Channels["Vertex"]);
        Assert.Equal(JointWorld(combined), JointWorld(lone));

        static Matrix4x4 JointWorld(string glb) => SharpGLTF.Schema2.ModelRoot.Load(glb)
            .LogicalNodes.Single(n => n.Name == "weapon_01_22222222").WorldMatrix;
    }

    /// <summary>Both routes out of ONE real rebuild. The part is rigged by its OWN bundle at a prefab MOUNT
    /// offset, and the rebuild writes its per-part glb (the lone session's file, and the one a compile round
    /// trips) alongside the union-armature glb (the outfit session's). The per-part file takes no placement
    /// from the mount: mesh and joint stand together at the part's own origin, exactly where the combined
    /// session puts a replaceable part. One part written to two different places is what a modder sees as it
    /// jumping between sessions.</summary>
    [Fact]
    public void TheRebuild_PlacesAPartsOwnGlbWhereTheCombinedOnePutsIt()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        const string weaponLogical = "wwwwwwwwwwwwwwwwwwwwwwwwwwwwwww1.bundle";
        const string bodyLogical = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1.bundle";
        const string weaponPhys = "11111111111111111111111111111111";
        const string bodyPhys = "22222222222222222222222222222222";
        // the hash anchors the path at "hips/weapon_01", so the mount node above the bone is a CONNECTOR
        uint weaponHash = BoneTable.Hash("hips/weapon_01");

        SyntheticBundle.BuildSelfRiggedMesh(Path.Combine(abw, weaponPhys + ".bundle"), "weapon_lod0",
            SessionTri, SessionIdx, new[] { weaponHash },
            new[]
            {
                new SyntheticBundle.RigNode("hips", -1, 0f, 0.5f, 2f),   // the prefab mount offset
                new SyntheticBundle.RigNode("weapon_01", 0, 0f, 0f, 0f),
            },
            skinBones: new[] { 1 }, bundleName: weaponLogical);
        // a second skinned part, so the same run has a union armature to build as well
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, bodyPhys + ".bundle"), "body1_lod0",
            SessionTri, SessionIdx, SessionBones, bundleName: bodyLogical);
        var vfs = TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (weaponLogical, weaponPhys), (bodyLogical, bodyPhys));

        // the fixture really does mount the part away from its bind origin, and unbakeably so — without a
        // measured offset there is no placement question for either route to answer wrong
        var weaponDec = vfs.TryDeobfuscateLogical(weaponLogical)!;
        var rig = SceneRig.TryRead(weaponDec, "weapon_lod0",
            MeshSkin.Decode(new Remold.Core.Bundles.BundleReader().GetMeshField(weaponDec, "weapon_lod0")!));
        Assert.Null(rig!.Uprighting);
        Assert.Equal(new Vector3(0, 0.5f, 2), rig.MeasuredRest!.Value.Translation);

        var lone = g.At(Path.Combine("meshes", "weapon_lod0.glb"));
        var combined = g.At(Path.Combine("meshes", "_combined.glb"));
        var spec = new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
        {
            ("weapon", weaponLogical, "weapon_lod0", lone, null, 0L, null),   // GlbOut ⇒ the lone route's file
            ("body1", bodyLogical, "body1_lod0", null, null, 0L, null),
        };

        var done = AssetExporter.BuildRiggedGlbs(g.Root, vfs, new Outfit(0, "VesnaSSR01", OutfitKind.Base),
            "Vesna", spec, g.At("textures"), combinedOut: combined);

        Assert.Contains("weapon", done);
        Assert.True(File.Exists(lone) && File.Exists(combined));
        var joint = $"weapon_01_{weaponHash:x8}";
        var loneWorld = JointWorld(lone, joint);
        Assert.True(loneWorld.Translation.Length() < 1e-5f,
            $"lone joint world translation: {loneWorld.Translation}");   // the mount is NOT applied
        Assert.Equal(loneWorld, JointWorld(combined, joint));
        // and the geometry is the raw bind-space bytes the compile round trips, on both
        Assert.Equal(MeshGltf.ImportGlb(combined, "weapon_lod0").Channels["Vertex"],
                     MeshGltf.ImportGlb(lone).Channels["Vertex"]);
        // the lone armature spans the SUBJECT: the body's bones stand in the weapon's rig too, so weight can
        // be painted onto them there — as zero-weighted joints appended AFTER the weapon's own bone, which
        // keeps that bone on the index its geometry names while the body's bones still import as bones
        var loneModel = SharpGLTF.Schema2.ModelRoot.Load(lone);
        foreach (var bodyBone in SessionBones)
            Assert.Contains(loneModel.LogicalNodes, n => n.Name == $"bone_{bodyBone:x8}");
        Assert.Equal(new[] { joint }.Concat(SessionBones.Select(b => $"bone_{b:x8}")).ToArray(),
            Enumerable.Range(0, loneModel.LogicalSkins.Single().JointsCount)
                      .Select(i => loneModel.LogicalSkins.Single().Joints[i].Name).ToArray());

        static Matrix4x4 JointWorld(string glb, string node) => SharpGLTF.Schema2.ModelRoot.Load(glb)
            .LogicalNodes.Single(n => n.Name == node).WorldMatrix;
    }

    /// <summary>A lone open reads every OTHER part of the outfit for its bones alone. Those reads are the
    /// only ones allowed to fail quietly: the part being opened decoded fine, so a sibling's bundle held by
    /// the game costs that sibling's bones, not the session. The alternative is an open that used to work
    /// refusing with the BUSY remedy over a file it never needed.</summary>
    [Fact]
    public void ALoneOpen_ASiblingsBundleLocked_OpensWithoutThatSiblingsBones()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        const string clothLogical = "ccccccccccccccccccccccccccccccc1.bundle";
        const string bodyLogical = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1.bundle";
        const string clothPhys = "33333333333333333333333333333333";
        const string bodyPhys = "44444444444444444444444444444444";
        var bodyFile = Path.Combine(abw, bodyPhys + ".bundle");
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, clothPhys + ".bundle"), "cloth1_lod0",
            SessionTri, SessionIdx, new[] { 33u }, bundleName: clothLogical);
        SyntheticBundle.BuildOneSkinnedMesh(bodyFile, "body1_lod0",
            SessionTri, SessionIdx, SessionBones, bundleName: bodyLogical);
        var vfs = TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (clothLogical, clothPhys), (bodyLogical, bodyPhys));
        // no combinedOut: the body row carries no GlbOut either, so it is read for its SKELETON alone
        List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)> Spec(string glbOut) => new()
        {
            ("cloth1", clothLogical, "cloth1_lod0", glbOut, null, 0L, null),
            ("body1", bodyLogical, "body1_lod0", null, null, 0L, null),
        };
        var outfit = new Outfit(0, "VesnaSSR01", OutfitKind.Base);

        // unlocked, the sibling's bones do stand in the lone part's rig — the fixture has something to lose
        var open = g.At(Path.Combine("meshes", "cloth1_lod0.glb"));
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, outfit, "Vesna", Spec(open), g.At("textures"));
        Assert.All(SessionBones, h => Assert.Contains(SharpGLTF.Schema2.ModelRoot.Load(open).LogicalNodes,
            n => n.Name == $"bone_{h:x8}"));

        var busy = g.At(Path.Combine("meshes", "busy.glb"));
        var log = new ListLog();
        using (File.Open(bodyFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var done = AssetExporter.BuildRiggedGlbs(g.Root, vfs, outfit, "Vesna", Spec(busy), g.At("textures"), log);
            Assert.Equal(new[] { "cloth1" }, done.ToArray());          // the open completed
        }

        var model = SharpGLTF.Schema2.ModelRoot.Load(busy);
        Assert.Equal(new[] { "bone_00000021" },                        // its own bone, and only its own
            Enumerable.Range(0, model.LogicalSkins.Single().JointsCount)
                      .Select(i => model.LogicalSkins.Single().Joints[i].Name).ToArray());
        Assert.All(SessionBones, h => Assert.DoesNotContain(model.LogicalNodes, n => n.Name == $"bone_{h:x8}"));
        // and never silently: one line, naming the part whose bones are missing
        Assert.Single(log.Lines, l => l.Contains("body1") && l.Contains("the game is using its files"));
    }

    /// <summary>A synchronous progress sink: <see cref="Progress{T}"/> posts through the sync context, so a
    /// test asserting on the lines could read them before they arrive.</summary>
    private sealed class ListLog : IProgress<string>
    {
        public List<string> Lines { get; } = new();
        public void Report(string value) => Lines.Add(value);
    }

    // ---------------------------------------------------------------- the session's map record

    /// <summary>A session record holds every part's maps, and it answers per SLOT: the same image, read out of
    /// the same record, is untouched on the slot it was exported on and an ask on a slot it never sat on.
    /// Moving an object between part collections in Blender is that ask — the object keeps the material it
    /// arrived with — and the map it brings ships as the receiving part's own, exactly as the per-part route
    /// publishes a deliberate cross-part link. Route: ExportCombinedRiggedGlb onto the session's own
    /// composition glb → ReadSubmeshMaps against the record beside it → BlenderMaterialReturn.Normalize.
    /// </summary>
    [Fact]
    public void ASessionRecord_AnswersPerSlot_SoASiblingsStockMapIsAnAsk()
    {
        using var g = new TempGame();
        var mapA = WritePng(g.At("body_d.png"), 1);
        var mapB = WritePng(g.At("cloth_d.png"), 90);
        var combined = g.At("composition.glb");

        // what the app handed Blender: one build of the session's parts, each with its own map
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", mapA), RiggedPart("cloth1_lod0", mapB) }, h => Paths[h], combined);
        var sessionSidecar = File.ReadAllBytes(PreviewMaps.SidecarPath(combined));

        // mapB on the slot it WAS exported on: cloth1's own, and nothing ships
        Assert.Equal(MapOrigin.Vanilla,
            MeshGltf.ReadSubmeshMaps(combined, "cloth1_lod0")[0].BaseColor.Origin);

        // after the two objects swapped part collections, body1 carries the material cloth1 arrived with
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", mapB), RiggedPart("cloth1_lod0", mapA) }, h => Paths[h], combined);
        File.WriteAllBytes(PreviewMaps.SidecarPath(combined), sessionSidecar);

        // the same image, the same record, a slot it never sat on: the modder's own work now
        var maps = MeshGltf.ReadSubmeshMaps(combined, "body1_lod0");
        Assert.Equal(MapOrigin.Authored, maps[0].BaseColor.Origin);
        var row = Assert.Single(BlenderMaterialReturn.Normalize(maps, g.At("body-return")));
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);
        AssertSamePixels(mapB, row.Albedo!);
    }

    /// <summary>The per-part workspace glb a re-split writes carries the part's own preview materials and map
    /// record, over the STOCK maps it came back on. An edited part is never rebuilt on a later open, so this
    /// file is what opening that part alone hands Blender: without the maps it opens untextured, and without
    /// the record its next send-back reads every untouched map as authored and rebuilds an authored RMO over
    /// a dead emissive mask.</summary>
    [Fact]
    public void ResplitWorkspaceGlb_CarriesThePartsOwnMapsAndTheirRecord()
    {
        using var g = new TempGame();
        var map = WritePng(g.At("body_d.png"), 5);
        var rmo = WritePng(g.At("body_r.png"), 11);
        var combined = g.At("_combined.glb");
        var resplit = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(Triangle("body1_lod0"),
                new MeshSkin { BoneHashes = new[] { HRoot }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } },
                map, PerSubmesh: new (string?, string?, string?)[] { (map, null, rmo) }),
            RiggedPart("cloth1_lod0", map),
        }, h => Paths[h], combined);

        MeshGltf.ReexportPartGlb(combined, "body1_lod0", resplit);

        Assert.All(MeshGltf.ReadSubmeshMaps(resplit), m =>
        {
            Assert.Equal(MapOrigin.Vanilla, m.BaseColor.Origin);
            Assert.Equal(Path.GetFullPath(map), m.BaseColor.StockPng);
            Assert.Equal(MapOrigin.Vanilla, m.Rmo.Origin);
        });
        // the alpha source an authored RMO is rebuilt over survives the re-split
        Assert.Equal(Path.GetFullPath(rmo), PreviewMaps.ReadSubmeshRmoSources(resplit, "body1_lod0")[0]);
    }

    /// <summary>A send-back's AUTHORED maps have to survive the re-split too, or re-opening the part alone
    /// hands the modder the game textures their own work covers. The authored file wins the slot, and the
    /// stock map it replaced is not re-embedded there.</summary>
    [Fact]
    public void ResplitWorkspaceGlb_EmbedsTheAuthoredMapOverTheStockOneItReplaced()
    {
        using var g = new TempGame();
        var stock = WritePng(g.At("body_d.png"), 5);
        var authored = WritePng(g.At("textures/body_s0_base.png"), 200);
        var combined = g.At("_combined.glb");
        var resplit = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", stock) }, h => Paths[h], combined);

        MeshGltf.ReexportPartGlb(combined, "body1_lod0", resplit,
            authoredMaps: new (string?, string?, string?)[] { (authored, null, null) });

        // what the part now opens on is the modder's file, at its own pixels
        var embedded = MeshGltf.ReadSubmeshMaps(resplit)[0].BaseColor;
        Assert.Equal(MapOrigin.Authored, embedded.Origin);
        Assert.Equal(FirstPixel(File.ReadAllBytes(authored)), FirstPixel(embedded.AuthoredPng!));
        Assert.NotEqual(FirstPixel(File.ReadAllBytes(stock)), FirstPixel(embedded.AuthoredPng!));
    }

    /// <summary>The record beside a re-split names the authored maps it embedded, but never as something a
    /// return can classify AGAINST: an image reproducing one of them is the modder's work, and reading it as
    /// stock would drop that work and put the game texture back on the slot.</summary>
    [Fact]
    public void AnAuthoredMapTheResplitEmbedded_ComesBackAuthoredNotStock()
    {
        using var g = new TempGame();
        var stock = WritePng(g.At("body_d.png"), 5);
        var authored = WritePng(g.At("textures/body_s0_base.png"), 200);
        var combined = g.At("_combined.glb");
        var resplit = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", stock) }, h => Paths[h], combined);
        MeshGltf.ReexportPartGlb(combined, "body1_lod0", resplit,
            authoredMaps: new (string?, string?, string?)[] { (authored, null, null) });

        // the record exists and names the file, so the neutrals and the submesh RMO sources still have a home
        Assert.Contains(Path.GetFileName(authored), File.ReadAllText(PreviewMaps.SidecarPath(resplit)));
        // …and it settles nothing: the classifying read never sees it
        Assert.DoesNotContain(PreviewMaps.ReadSidecar(resplit).Values,
            e => e.Source == Path.GetFullPath(authored));
        Assert.Equal(MapOrigin.Authored, MeshGltf.ReadSubmeshMaps(resplit)[0].BaseColor.Origin);
    }

    /// <summary>The stock map an authored one replaced must not be re-embedded on that slot. It would come
    /// back byte-identical, classify as untouched, and the authored map would be gone with no record of it.
    /// </summary>
    [Fact]
    public void TheStockMapAnAuthoredOneReplaced_IsNotReEmbeddedBesideIt()
    {
        using var g = new TempGame();
        var stock = WritePng(g.At("body_d.png"), 5);
        var authored = WritePng(g.At("textures/body_s0_base.png"), 200);
        var combined = g.At("_combined.glb");
        var resplit = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", stock) }, h => Paths[h], combined);

        MeshGltf.ReexportPartGlb(combined, "body1_lod0", resplit,
            authoredMaps: new (string?, string?, string?)[] { (authored, null, null) });

        Assert.DoesNotContain(Path.GetFileName(stock), File.ReadAllText(PreviewMaps.SidecarPath(resplit)));
    }

    /// <summary>A slot the send-back authored nothing for keeps the stock map it came back on, so an
    /// albedo-only edit still re-opens with its real relief showing.</summary>
    [Fact]
    public void AResplitWithOneAuthoredSlot_KeepsTheStockMapsOnTheOthers()
    {
        using var g = new TempGame();
        var stock = WritePng(g.At("body_d.png"), 5);
        var stockRmo = WritePng(g.At("body_r.png"), 11);
        var authored = WritePng(g.At("textures/body_s0_base.png"), 200);
        var combined = g.At("_combined.glb");
        var resplit = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(Triangle("body1_lod0"),
                new MeshSkin { BoneHashes = new[] { HRoot }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } },
                stock, PerSubmesh: new (string?, string?, string?)[] { (stock, null, stockRmo) }),
        }, h => Paths[h], combined);

        MeshGltf.ReexportPartGlb(combined, "body1_lod0", resplit,
            authoredMaps: new (string?, string?, string?)[] { (authored, null, null) });

        var back = MeshGltf.ReadSubmeshMaps(resplit)[0];
        Assert.Equal(MapOrigin.Authored, back.BaseColor.Origin);
        Assert.Equal(MapOrigin.Vanilla, back.Rmo.Origin);
        Assert.Equal(Path.GetFullPath(stockRmo), back.Rmo.StockPng);
        // the mask source rides the record either way, so an authored RMO next time still finds its alpha
        Assert.Equal(Path.GetFullPath(stockRmo), PreviewMaps.ReadSubmeshRmoSources(resplit, "body1_lod0")[0]);
    }

    /// <summary>A PNG's top-left pixel. A base colour round-trips as a pure re-encode, so which PICTURE came
    /// back is checkable off one pixel of these flat test maps without depending on the encoder's bytes.
    /// </summary>
    private static Rgba32 FirstPixel(byte[] png)
    {
        using var img = Image.Load<Rgba32>(png);
        return img[0, 0];
    }

    /// <summary>Which part a stock map belongs to is the whole difference between "bound to its own map" and
    /// a deliberate sibling link, and on a combined session the record beside the session glb is the only
    /// place that ownership is written down — a part opened in a session has no sidecar of its own. Read per
    /// owner, the SAME image is a link on the part that never had it and untouched on the part that did.</summary>
    [Fact]
    public void Exact_part_records_treat_a_siblings_stock_map_as_authored_only_for_the_linking_part()
    {
        using var g = new TempGame();
        var bodyMap = WritePng(g.At("body_d.png"), 1);
        var clothMap = WritePng(g.At("cloth_d.png"), 90);
        var bodyWorkspace = g.At("body1_lod0.glb");
        var clothWorkspace = g.At("cloth1_lod0.glb");
        var combined = g.At("_combined.glb");

        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", bodyMap) }, h => Paths[h], bodyWorkspace);
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("cloth1_lod0", clothMap) }, h => Paths[h], clothWorkspace);

        // what came back: body1's slot re-linked to the image cloth1 arrived on
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", clothMap), RiggedPart("cloth1_lod0", clothMap) }, h => Paths[h], combined);

        var linked = BlenderMaterialReturn.Normalize(
            MeshGltf.ReadSubmeshMaps(combined, "body1_lod0", bodyWorkspace), g.At("body-return"));
        var row = Assert.Single(linked);
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);
        AssertSamePixels(clothMap, row.Albedo!);

        // the control: cloth1 carries the same image and it is cloth1's own, so nothing ships
        Assert.Empty(BlenderMaterialReturn.Normalize(
            MeshGltf.ReadSubmeshMaps(combined, "cloth1_lod0", clothWorkspace), g.At("cloth-return")));
    }

    /// <summary>An open that could read every map says nothing extra; one that couldn't NAMES them, once, on
    /// the line that stays put. The names are what make the miss reportable — there is no log file behind this
    /// line — and past three the rest are counted. No fix clause: nothing the modder does changes it.</summary>
    [Fact]
    public void TheOpensStatusLine_NamesTheUnreadableTexturesOnceOrNotAtAll()
    {
        Assert.Empty(MainWindowViewModel.BlenderOpenNotices(Array.Empty<string>(), false,
            Array.Empty<string>()));
        Assert.Equal("Could not read cloth_a_d. That material opens untextured.", Notice("cloth_a_d"));
        Assert.Equal("Could not read cloth_a_d and cloth_b_d. Those materials open untextured.",
            Notice("cloth_b_d", "cloth_a_d"));
        Assert.Equal("Could not read body_d, cloth_a_d and cloth_b_d. Those materials open untextured.",
            Notice("cloth_a_d", "body_d", "cloth_b_d"));
        Assert.Equal("Could not read body_d, cloth_a_d, cloth_b_d and 2 more textures. "
            + "Those materials open untextured.",
            Notice("cloth_a_d", "body_d", "cloth_b_d", "face_d", "hair_d"));

        static string Notice(params string[] textures) => Assert.Single(
            MainWindowViewModel.BlenderOpenNotices(Array.Empty<string>(), false, textures));
    }

    /// <summary>The deliberate cross-part texture link, through the shape the app really produces: the target
    /// of a send is the part's OWN prepared glb, whose record holds only that part's own maps. A slot the
    /// modder re-pointed at a SIBLING part's map therefore comes back matching nothing the record holds, which
    /// is exactly what classifies it as the modder's own work — and it ships as that part's own map, pixel for
    /// pixel. Nothing else records the link, so this is the whole of it.</summary>
    [Fact]
    public void ASiblingPartsMapPluggedIntoAPartsSlot_ShipsAsThatPartsOwnMap()
    {
        using var g = new TempGame();
        var bodyMap = WritePng(g.At("body_d.png"), 1);
        var clothMap = WritePng(g.At("cloth_d.png"), 90);
        // what the open handed Blender for body1: its own prepared glb, on its own map
        var workspace = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", bodyMap) }, h => Paths[h], workspace);
        // what came back: the same part, its base colour re-pointed at cloth1's map
        var returned = g.At("return.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", clothMap) }, h => Paths[h], returned);

        var incoming = MeshGltf.ReadSubmeshMaps(returned, "body1_lod0", workspace);
        Assert.Equal(MapOrigin.Authored, incoming[0].BaseColor.Origin);
        var row = Assert.Single(BlenderMaterialReturn.Normalize(incoming, g.At("body-return")));
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);
        AssertSamePixels(clothMap, row.Albedo!);
    }

    /// <summary>The control on the same shape: the part's OWN untouched map is not a link, so nothing ships
    /// and the slot stays bound to the game's map.</summary>
    [Fact]
    public void ThePartsOwnUntouchedMap_StaysItsOwn()
    {
        using var g = new TempGame();
        var bodyMap = WritePng(g.At("body_d.png"), 1);
        var workspace = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", bodyMap) }, h => Paths[h], workspace);

        Assert.Empty(BlenderMaterialReturn.Normalize(
            MeshGltf.ReadSubmeshMaps(workspace, "body1_lod0", workspace), g.At("body-return")));
    }

    // ---------------------------------------------------------------- the link INSIDE one part

    /// <summary>The same gesture one part in: the modder plugs material 2's picture into material 1's slot.
    /// It is an ask exactly as a sibling PART's map is, and it ships as that slot's own map. Read against the
    /// whole record it matched "one of this part's stock maps" and vanished — the slot kept the game texture
    /// it was already on and no authored row was written at all. Route: ExportCombinedRiggedGlb (the open's
    /// workspace) → ReadSubmeshMaps(returned, part, workspace) → BlenderMaterialReturn.Normalize.</summary>
    [Fact]
    public void AMaterialsStockMapPluggedIntoAnotherSlotOfTheSamePart_ShipsAsThatSlotsOwnMap()
    {
        using var g = new TempGame();
        var mapA = WritePng(g.At("cloth_a_d.png"), 10);
        var mapB = WritePng(g.At("cloth_b_d.png"), 80);
        var workspace = g.At("body1_lod0.glb");
        var returned = g.At("return.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapA, mapB) },
            h => Paths[h], workspace);
        // what came back: material 1 re-pointed at material 2's picture
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapB, mapB) },
            h => Paths[h], returned);

        var incoming = MeshGltf.ReadSubmeshMaps(returned, "body1_lod0", workspace);

        Assert.Equal(MapOrigin.Authored, incoming[0].BaseColor.Origin);
        Assert.Equal(MapOrigin.Vanilla, incoming[1].BaseColor.Origin);   // material 2 is still on its own
        var row = Assert.Single(BlenderMaterialReturn.Normalize(incoming, g.At("return-maps")));
        Assert.Equal(0, row.Submesh);
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);
        AssertSamePixels(mapB, row.Albedo!);
    }

    /// <summary>The link the other way round lands on the other slot, so the answer follows the gesture rather
    /// than a fixed slot: material 1's picture in material 2's slot ships for material 2.</summary>
    [Fact]
    public void TheSameLinkTheOtherWayRound_ShipsForTheOtherSlot()
    {
        using var g = new TempGame();
        var mapA = WritePng(g.At("cloth_a_d.png"), 10);
        var mapB = WritePng(g.At("cloth_b_d.png"), 80);
        var workspace = g.At("body1_lod0.glb");
        var returned = g.At("return.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapA, mapB) },
            h => Paths[h], workspace);
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapA, mapA) },
            h => Paths[h], returned);

        var incoming = MeshGltf.ReadSubmeshMaps(returned, "body1_lod0", workspace);

        Assert.Equal(MapOrigin.Vanilla, incoming[0].BaseColor.Origin);
        Assert.Equal(MapOrigin.Authored, incoming[1].BaseColor.Origin);
        var row = Assert.Single(BlenderMaterialReturn.Normalize(incoming, g.At("return-maps")));
        Assert.Equal(1, row.Submesh);
        AssertSamePixels(mapA, row.Albedo!);
    }

    /// <summary>The whole part untouched, every picture re-encoded by the tool that wrote the file: nothing is
    /// an ask. The bytes cannot say so, so the pixels do — and the pixel comparison has to find each slot's
    /// OWN map, which is what makes the link case below more than a hash miss.</summary>
    [Fact]
    public void AReEncodedUntouchedReturn_StaysOnItsOwnMaps()
    {
        using var g = new TempGame();
        var mapA = WritePng(g.At("cloth_a_d.png"), 10);
        var mapB = WritePng(g.At("cloth_b_d.png"), 80);
        var workspace = g.At("body1_lod0.glb");
        var returned = g.At("return.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapA, mapB) },
            h => Paths[h], workspace);
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapA, mapB) },
            h => Paths[h], returned);
        ReEncodeEmbeddedImages(returned);

        var incoming = MeshGltf.ReadSubmeshMaps(returned, "body1_lod0", workspace);

        Assert.All(incoming, m => Assert.Equal(MapOrigin.Vanilla, m.BaseColor.Origin));
        Assert.Empty(BlenderMaterialReturn.Normalize(incoming, g.At("return-maps")));
    }

    /// <summary>…and the link survives the same re-encode: the returned picture is material 2's whatever
    /// bytes carry it, so it is still an ask for material 1's slot.</summary>
    [Fact]
    public void AReEncodedIntraPartLink_StillShipsAsThatSlotsOwnMap()
    {
        using var g = new TempGame();
        var mapA = WritePng(g.At("cloth_a_d.png"), 10);
        var mapB = WritePng(g.At("cloth_b_d.png"), 80);
        var workspace = g.At("body1_lod0.glb");
        var returned = g.At("return.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapA, mapB) },
            h => Paths[h], workspace);
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapB, mapB) },
            h => Paths[h], returned);
        ReEncodeEmbeddedImages(returned);

        var incoming = MeshGltf.ReadSubmeshMaps(returned, "body1_lod0", workspace);

        Assert.Equal(MapOrigin.Authored, incoming[0].BaseColor.Origin);
        Assert.Equal(MapOrigin.Vanilla, incoming[1].BaseColor.Origin);
        var row = Assert.Single(BlenderMaterialReturn.Normalize(incoming, g.At("return-maps")));
        Assert.Equal(0, row.Submesh);
        AssertSamePixels(mapB, row.Albedo!);
    }

    /// <summary>The send AFTER an intra-part link. The re-split re-opens material 1 on the modder's own copy of
    /// material 2's picture, so the workspace now holds two files with the same pixels — that copy on slot 0,
    /// the stock map still on slot 1. A send that touches nothing has to bring the copy back as the modder's
    /// work: matched against the stock map it reproduces, the link would revert to the game texture one send
    /// after it was made, silently.</summary>
    [Fact]
    public void TheSendAfterAnIntraPartLink_KeepsTheModdersOwnCopy()
    {
        using var g = new TempGame();
        var mapA = WritePng(g.At("cloth_a_d.png"), 10);
        var mapB = WritePng(g.At("cloth_b_d.png"), 80);
        var workspace = g.At("body1_lod0.glb");
        var returned = g.At("return.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapA, mapB) },
            h => Paths[h], workspace);
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapB, mapB) },
            h => Paths[h], returned);
        // the link applied: the file the intake writes, and the part re-opened over it
        var linked = Assert.Single(BlenderMaterialReturn.Normalize(
            MeshGltf.ReadSubmeshMaps(returned, "body1_lod0", workspace), g.At("return-maps")));
        var reopened = g.At("body1_lod0.reopened.glb");
        MeshGltf.ReexportPartGlb(returned, "body1_lod0", reopened, recordGlb: workspace,
            authoredMaps: new (string?, string?, string?)[] { (linked.Albedo, null, null), default });

        // the second send, with nothing touched in Blender
        var second = MeshGltf.ReadSubmeshMaps(reopened, "body1_lod0", reopened);

        Assert.Equal(MapOrigin.Authored, second[0].BaseColor.Origin);
        Assert.Equal(MapOrigin.Vanilla, second[1].BaseColor.Origin);
        var row = Assert.Single(BlenderMaterialReturn.Normalize(second, g.At("second-maps")));
        Assert.Equal(0, row.Submesh);
        AssertSamePixels(mapB, row.Albedo!);
    }

    /// <summary>A workspace from a release that recorded no per-slot stock keeps exactly the answer it always
    /// gave: the whole record settles every slot. Old workspaces still open and still round-trip — at the cost
    /// of the intra-part link they never could see.</summary>
    [Fact]
    public void AWorkspaceRecordWithNoSlotRows_KeepsTheWholeRecordAnswer()
    {
        using var g = new TempGame();
        var mapA = WritePng(g.At("cloth_a_d.png"), 10);
        var mapB = WritePng(g.At("cloth_b_d.png"), 80);
        var workspace = g.At("body1_lod0.glb");
        var returned = g.At("return.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapA, mapB) },
            h => Paths[h], workspace);
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapB, mapB) },
            h => Paths[h], returned);
        StripSlotRows(PreviewMaps.SidecarPath(workspace));

        var incoming = MeshGltf.ReadSubmeshMaps(returned, "body1_lod0", workspace);

        Assert.All(incoming, m => Assert.Equal(MapOrigin.Vanilla, m.BaseColor.Origin));
        Assert.Empty(BlenderMaterialReturn.Normalize(incoming, g.At("return-maps")));
    }

    /// <summary>A mesh renamed in Blender comes back under a name the record's slot rows do not carry, so the
    /// record cannot say what any of its slots was exported over. Unknowable, not empty: the part's own
    /// untouched maps stay untouched. Read as empty, a rename would ship every map of the part as a redundant
    /// copy of the game's own.</summary>
    [Fact]
    public void ARenameInBlender_LeavesThePartsOwnMapsUntouched()
    {
        using var g = new TempGame();
        var mapA = WritePng(g.At("cloth_a_d.png"), 10);
        var mapB = WritePng(g.At("cloth_b_d.png"), 80);
        var workspace = g.At("body1_lod0.glb");
        var returned = g.At("return.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0", mapA, mapB) },
            h => Paths[h], workspace);
        MeshGltf.ExportCombinedRiggedGlb(new[] { TwoMaterialPart("body1_lod0.001", mapA, mapB) },
            h => Paths[h], returned);

        var incoming = MeshGltf.ReadSubmeshMaps(returned, "body1_lod0.001", workspace);

        Assert.All(incoming, m => Assert.Equal(MapOrigin.Vanilla, m.BaseColor.Origin));
        Assert.Empty(BlenderMaterialReturn.Normalize(incoming, g.At("return-maps")));
    }

    /// <summary>The record's per-slot rows are all-or-nothing per MESH: a mesh it names at all carries a row
    /// for every (primitive, kind) the export bound a stock map to. The classifying read falls back per MESH
    /// but answers per (index, kind), so a record naming the mesh with rows for only SOME kinds would never
    /// reach the fallback — every untouched map of a missing kind would read as the modder's own and ship a
    /// redundant copy of the game's own picture. Route: ExportCombinedRiggedGlb → WriteSidecar →
    /// ReadSlotStock.</summary>
    [Fact]
    public void AnExportsSlotRows_CoverEveryStockBoundSlotOfTheMeshTheyName()
    {
        using var g = new TempGame();
        var albedo = WritePng(g.At("cloth_d.png"), 10);
        var normal = WritePng(g.At("cloth_n.png"), 40);
        var rmo = WritePng(g.At("cloth_r.png"), 70);
        // the two submeshes bind DIFFERENT kinds: all three on the first, no normal on the second
        var perSubmesh = new (string? Base, string? Normal, string? Rmo)[]
        {
            (albedo, normal, rmo),
            (albedo, null, rmo),
        };
        var workspace = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(TwoTriangles("body1_lod0"),
                new MeshSkin
                {
                    BoneHashes = new[] { HRoot },
                    BindPoses = new List<Matrix4x4> { Matrix4x4.Identity },
                },
                albedo, PerSubmesh: perSubmesh),
        }, h => Paths[h], workspace);

        // what the fixture bound stock maps to, by construction — one row is owed for each
        var owed = new List<(int, MapKind)>();
        for (int i = 0; i < perSubmesh.Length; i++)
        {
            if (perSubmesh[i].Base is not null) owed.Add((i, MapKind.BaseColor));
            if (perSubmesh[i].Normal is not null) owed.Add((i, MapKind.Normal));
            if (perSubmesh[i].Rmo is not null) owed.Add((i, MapKind.Rmo));
        }

        var rows = PreviewMaps.ReadSlotStock(workspace, "body1_lod0");

        Assert.NotNull(rows);
        Assert.Equal(owed.OrderBy(k => k.Item1).ThenBy(k => k.Item2).ToArray(),
                     rows!.Keys.OrderBy(k => k.Index).ThenBy(k => k.Kind).ToArray());
    }

    /// <summary>Re-encode every picture a glb carries without touching a pixel — what a tool that rewrites the
    /// file does to a map nobody edited. Asserts the bytes really changed, so a test resting on this cannot
    /// quietly fall back to the byte-identical path it means to avoid.</summary>
    private static void ReEncodeEmbeddedImages(string glb)
    {
        var model = SharpGLTF.Schema2.ModelRoot.Load(glb,
            new SharpGLTF.Schema2.ReadSettings { Validation = SharpGLTF.Validation.ValidationMode.Skip });
        foreach (var image in model.LogicalImages)
        {
            var before = image.Content.Content.ToArray();
            using var decoded = Image.Load<Rgba32>(before);
            using var stream = new MemoryStream();
            decoded.SaveAsPng(stream, new SixLabors.ImageSharp.Formats.Png.PngEncoder
            {
                CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestSpeed,
                FilterMethod = SixLabors.ImageSharp.Formats.Png.PngFilterMethod.None,
            });
            var after = stream.ToArray();
            Assert.NotEqual(before, after);
            image.Content = new SharpGLTF.Memory.MemoryImage(after);
        }
        model.SaveGLB(glb);
    }

    /// <summary>Drop the per-slot stock rows from a map record, leaving exactly what a release that never
    /// wrote them produced.</summary>
    private static void StripSlotRows(string sidecar)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sidecar))!.AsObject();
        Assert.True(doc.Remove("slots"));
        File.WriteAllText(sidecar, doc.ToJsonString());
    }

    /// <summary>A skinned part of TWO submeshes, each material on its own base colour — the multi-material
    /// shape an intra-part texture link needs.</summary>
    private static MeshGltf.RiggedPart TwoMaterialPart(string name, string first, string second) => new(
        TwoTriangles(name),
        new MeshSkin
        {
            BoneHashes = new[] { HRoot },
            BindPoses = new List<Matrix4x4> { Matrix4x4.Identity },
        },
        BaseColorPng: first,
        PerSubmesh: new (string?, string?, string?)[] { (first, null, null), (second, null, null) });

    private static UnityMesh TwoTriangles(string name) => new()
    {
        Name = name,
        VertexCount = 6,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0, 2, 0, 0, 3, 0, 0, 2, 1, 0 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
            ["BlendIndices"] = new float[24],
            ["BlendWeight"] = Enumerable.Range(0, 6).SelectMany(_ => new[] { 1f, 0, 0, 0 }).ToArray(),
        },
        Dims = new()
        {
            ["Vertex"] = 3,
            ["TexCoord0"] = 2,
            ["BlendIndices"] = 4,
            ["BlendWeight"] = 4,
        },
        Submeshes = new() { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } },
    };

    /// <summary>Sidecar rows carry the name a part was EXPORTED under, so a mesh renamed in Blender comes
    /// back under a name no row matches. The two namespaces stay apart: the glb-INTERNAL read keys what
    /// came back, the SIDECAR read keys the export-time name, and the authored RMO's mask still comes off
    /// the stock map the export recorded rather than falling through to a zero alpha.</summary>
    [Fact]
    public void ARenameInBlender_LeavesTheAuthoredRmosMaskOnItsExportTimeRow()
    {
        using var g = new TempGame();
        var stockRmo = WritePng(g.At("body_r.png"), 11);
        var authoredRmo = WritePng(g.At("hand_r.png"), 77);
        var ws = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", rmoPng: stockRmo) }, h => Paths[h], ws);
        var sidecar = File.ReadAllBytes(PreviewMaps.SidecarPath(ws));

        // what came back: the same part renamed, its RMO slot repainted
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0.001", rmoPng: authoredRmo) }, h => Paths[h], ws);
        File.WriteAllBytes(PreviewMaps.SidecarPath(ws), sidecar);

        Assert.Equal("body1_lod0.001", MeshGltf.MeshNames(ws)[0]);
        Assert.Equal(MapOrigin.Authored, MeshGltf.ReadSubmeshMaps(ws, null)[0].Rmo.Origin);
        Assert.Empty(PreviewMaps.ReadSubmeshRmoSources(ws, null));              // the returned name matches no row
        Assert.Equal(Path.GetFullPath(stockRmo), PreviewMaps.ReadSubmeshRmoSources(ws, "body1_lod0")[0]);
    }

    /// <summary>A combined session carries every part's submeshes in ONE file, so the stock RMO each was
    /// given is recorded per (mesh, submesh). Read for one part it answers that part's own map: an authored
    /// RMO wearing a sibling's emissive mask would ship wrong and say nothing.</summary>
    [Fact]
    public void ACombinedExport_RecordsEachPartsOwnRmoSource()
    {
        using var g = new TempGame();
        var bodyRmo = WritePng(g.At("body_r.png"), 11);
        var clothRmo = WritePng(g.At("cloth_r.png"), 44);
        var combined = g.At("_combined.glb");

        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            RiggedPart("body1_lod0", rmoPng: bodyRmo),
            RiggedPart("cloth1_lod0", rmoPng: clothRmo),
        }, h => Paths[h], combined);

        Assert.Equal(Path.GetFullPath(bodyRmo),
            Assert.Contains(0, PreviewMaps.ReadSubmeshRmoSources(combined, "body1_lod0")));
        Assert.Equal(Path.GetFullPath(clothRmo),
            Assert.Contains(0, PreviewMaps.ReadSubmeshRmoSources(combined, "cloth1_lod0")));
    }

    /// <summary>The map read and the RMO-source read are paired — one names a submesh's slots, the other the
    /// alpha those slots ship over — so with no mesh named they must key the SAME part. Keying the first part
    /// that happens to have recorded an RMO would pair one part's slots with another's emissive mask.</summary>
    [Fact]
    public void ANoNameRead_KeysTheGlbsFirstMesh_OnBothHalvesOfThePair()
    {
        using var g = new TempGame();
        var clothRmo = WritePng(g.At("cloth_r.png"), 44);
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            RiggedPart("body1_lod0"),                       // first, and carries no RMO
            RiggedPart("cloth1_lod0", rmoPng: clothRmo),
        }, h => Paths[h], combined);
        Assert.Equal("body1_lod0", MeshGltf.MeshNames(combined)[0]);

        Assert.Equal(MapOrigin.None, MeshGltf.ReadSubmeshMaps(combined)[0].Rmo.Origin);
        Assert.Empty(PreviewMaps.ReadSubmeshRmoSources(combined));
    }

    // ---------------------------------------------------------------- a lost map record is never silent

    /// <summary>A glb whose map record went missing resolves every image as authored — an untouched stock RMO
    /// included. Its emissive mask has no glTF material semantic, so with nothing to rebuild the
    /// mask from it ships dead. The intake asks per submesh instead, and the answer the caller resolves off
    /// the part's own renderer puts the mask back whole.</summary>
    [Fact]
    public void AnAuthoredRmoWithNoRecordBehindIt_TakesItsMaskFromTheStockMapTheCallerNames()
    {
        using var g = new TempGame();
        var stockRmo = WritePng(g.At("body_r.png"), 11);
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", rmoPng: stockRmo) }, h => Paths[h], combined);
        File.Delete(PreviewMaps.SidecarPath(combined));               // the publish couldn't move it

        var maps = MeshGltf.ReadSubmeshMaps(combined, "body1_lod0");
        Assert.Equal(MapOrigin.Authored, maps[0].Rmo.Origin);         // no record: the stock map reads authored
        Assert.Empty(PreviewMaps.ReadSubmeshRmoSources(combined, "body1_lod0"));

        var none = new ResolvedMap(MapOrigin.None);
        var returned = maps.Concat(new[] { new IncomingMaps(none, none) }).ToList();
        var asked = new List<int>();
        var row = Assert.Single(BlenderMaterialReturn.Normalize(returned, g.At("textures"),
            i => { asked.Add(i); return stockRmo; }));

        Assert.Equal(new[] { 0 }, asked.ToArray());                   // asked only where a mask had to be rebuilt
        AssertSamePixels(stockRmo, row.Rmo!);

        // the control: with nothing to answer, the same send ships the mask as a dead zero
        var blank = Assert.Single(BlenderMaterialReturn.Normalize(maps, g.At("blank")));
        using var shipped = Image.Load<Rgba32>(blank.Rmo!);
        Assert.Equal(0, shipped[0, 0].A);
    }

    // ---------------------------------------------------------------- the send read is lenient

    /// <summary>A Blender export can carry accessors this app never reads — a shape key's, say — that strict
    /// schema validation rejects outright. Every other read of a returned glb skips validation for that
    /// reason, and the send read is the one that must: the external artifact still has to reach the
    /// normalizer rather than costing the modder the edit it carries.</summary>
    [Fact]
    public void ReadSend_AGlbCarryingAnAccessorWeNeverRead_StillReadsTheEditBack()
    {
        using var g = new TempGame();
        var glb = g.At("body1_lod0.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        AddUnreadAccessor(glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");

        Assert.Throws<SharpGLTF.Validation.SchemaException>(() => SharpGLTF.Schema2.ModelRoot.Load(glb));

        var edit = BlenderBridge.ReadSend(glb, "body1_lod0");

        Assert.Equal(3, edit.Mesh!.VertexCount);
    }

    // ---------------------------------------------------------------- fixtures shared by the bridge tests

    private const uint HRoot = 0x1111_1111;
    private const uint HWeapon = 0x2222_2222;
    private static readonly Dictionary<uint, string> Paths = new()
    {
        [HRoot] = "root",
        [HWeapon] = "hips/weapon_01",
    };
    private static readonly Matrix4x4 Mount = Matrix4x4.CreateTranslation(0, 0.5f, 2);

    private static UnityMesh Triangle(string name) => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1 },
            ["BlendIndices"] = new[] { 0f, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
            ["BlendWeight"] = new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
        },
        Dims = new()
        {
            ["Vertex"] = 3,
            ["TexCoord0"] = 2,
            ["BlendIndices"] = 4,
            ["BlendWeight"] = 4,
        },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };

    private static MeshGltf.RiggedPart RiggedPart(string name, string? basePng = null,
        string? rmoPng = null) => new(
        Triangle(name),
        new MeshSkin
        {
            BoneHashes = new[] { HRoot },
            BindPoses = new List<Matrix4x4> { Matrix4x4.Identity },
        },
        BaseColorPng: basePng,
        PerSubmesh: new (string?, string?, string?)[] { (basePng, null, rmoPng) });

    private static SceneRig MountedRig() => new()
    {
        BonePaths = new[] { "hips/weapon_01" },
        MeasuredRest = Mount,
        BoneRestWorlds = new[] { Mount },
        ConnectorRests = new Dictionary<string, Matrix4x4> { ["hips"] = Matrix4x4.Identity },
    };

    private static string WritePng(string path, byte seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(4, 4,
            new Rgba32(seed, unchecked((byte)(seed + 1)), unchecked((byte)(seed + 2)),
                unchecked((byte)(seed + 3))));
        image.SaveAsPng(path);
        return path;
    }

    private static void AssertSamePixels(string expected, string actual)
    {
        using var a = Image.Load<Rgba32>(expected);
        using var b = Image.Load<Rgba32>(actual);
        Assert.Equal(a.Size, b.Size);
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                Assert.Equal(a[x, y], b[x, y]);
    }

    private static void AddUnreadAccessor(string glb)
    {
        byte[] source = File.ReadAllBytes(glb);
        using var input = new MemoryStream(source);
        using var reader = new BinaryReader(input);
        uint magic = reader.ReadUInt32();
        uint version = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        uint jsonLength = reader.ReadUInt32();
        uint jsonType = reader.ReadUInt32();
        byte[] jsonBytes = reader.ReadBytes(checked((int)jsonLength));
        byte[] remainder = reader.ReadBytes(checked((int)(input.Length - input.Position)));

        var root = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Encoding.UTF8.GetString(jsonBytes).TrimEnd(' ', '\0'))!.AsObject();
        var accessors = root["accessors"]!.AsArray();
        accessors.Add(new System.Text.Json.Nodes.JsonObject
        {
            ["componentType"] = 5126,
            ["count"] = 1,
            ["type"] = "SCALAR",
        });
        byte[] rewritten = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString());
        int paddedLength = (rewritten.Length + 3) & ~3;

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(magic);
        writer.Write(version);
        writer.Write(checked((uint)(12 + 8 + paddedLength + remainder.Length)));
        writer.Write(checked((uint)paddedLength));
        writer.Write(jsonType);
        writer.Write(rewritten);
        for (int i = rewritten.Length; i < paddedLength; i++) writer.Write((byte)' ');
        writer.Write(remainder);
        File.WriteAllBytes(glb, output.ToArray());
    }

}
