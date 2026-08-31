using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.Core.Blender;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The open-all round trip, END TO END: a real <see cref="BlenderSendWatcher"/> over a real mod folder, a
/// real structured glb standing in for Blender's Send, and the window's own apply behind it. Every other
/// test of this path takes one pure helper at a time; nothing until now ran the whole return, which is how
/// both the material collapse and the send-back freeze reached a shipped build.
///
/// <para>The apply is driven on a <see cref="PumpedUiThread"/> — one thread with a queue and a
/// synchronization context, exactly the shape the app's own dispatcher has. That is the load-bearing part
/// of these tests: work that comes back onto the window's thread from inside itself parks here for good,
/// so a timeout below is a deadlock and not a slow machine.</para>
///
/// <para>The subject is synthetic — a two-material cloth part and a one-material body — so no game install
/// is read. The window's own resolver answers null for both, which is the recorded-routes branch a new
/// target takes when the install cannot be reached: the parts are opened in the project up front and carry
/// no edit, which is exactly what an open-all offers a part route for.</para>
/// </summary>
[Collection("Dispatcher")]
public class BlenderReturnRoundTripTests
{
    private const string Character = "Vesna", Outfit = "VesnaSSR01";
    /// <summary>The name the mod a return belongs to is refused BY, where the test opens a second one.</summary>
    private const string FirstMod = "Vesna casual";
    private const string Cloth = "c_vesna_cloth_lod0", Body = "c_vesna_body_lod0";

    private static TargetPart ClothPart => AuthoredParts.Part(Character, Outfit, Cloth);
    private static TargetPart BodyPart => AuthoredParts.Part(Character, Outfit, Body);

    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    /// <summary>(a) The whole create-on-send trip. One send, no edit anywhere in the project beforehand:
    /// the return has to reach the app at all, apply to COMPLETION rather than park on the thread it was
    /// dispatched from, mint the cloth part's edit, publish geometry whose bytes read back, and keep the
    /// two returned submeshes on their own output positions.</summary>
    [Fact]
    public async Task An_open_all_return_mints_the_edit_and_lands_both_material_slots()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        SendBack(temp, session, twoParts: true, emptied: Array.Empty<string>());

        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null),
            $"the return never finished applying — status: '{vm.EditPage.Status}'");
        Assert.True(ui.Idle(Settle), "the window's thread never went idle after the return");
        Assert.StartsWith("Blender sent back", vm.EditPage.Status);

        var edit = ContentEdit(session, ClothPart)!;
        // the geometry the send carried, published and readable back at both of its material slots
        var geometry = session.Slots(edit.Id).Single(state => state.Slot.Domain == TargetSlotDomain.Game
            && state.Slot.Input == TargetInputKind.Geometry
            && (state.Slot.Tier is null || state.Slot.Tier == "lod0"));
        Assert.Equal(BindingKind.ProjectAsset, geometry.Binding.Kind);
        string published = Path.Combine(session.Snapshot().RootDir!, geometry.ProjectAsset!.File);
        Assert.True(File.Exists(published), "the returned geometry was never published");
        Assert.Equal(2, MeshGltf.ReadSubmeshMaps(published).Count);

        // the collapse pin: two returned submeshes, two OWN output positions, two distinct files
        var bases = session.Slots(edit.Id)
            .Where(state => state.Slot.Domain == TargetSlotDomain.EditOutput
                && state.Slot.Input == TargetInputKind.BaseColor)
            .OrderBy(state => state.Slot.SubmeshIndex)
            .ToList();
        Assert.Equal(new int?[] { 0, 1 }, bases.Select(state => state.Slot.SubmeshIndex).ToArray());
        Assert.All(bases, state => Assert.Equal(BindingKind.ProjectAsset, state.Binding.Kind));
        Assert.Equal(2, bases.Select(state => state.ProjectAsset!.File).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(bases, state =>
            Assert.True(File.Exists(Path.Combine(session.Snapshot().RootDir!, state.ProjectAsset!.File))));
        Assert.Equal(new[] { "c_vesna_body_lod0_base", "c_vesna_cloth_lod0_base" },
            bases.Select(state => state.ProjectAsset!.Label).ToArray());
        Assert.DoesNotContain(bases, state => state.ProjectAsset!.Label.StartsWith("submesh ",
            StringComparison.OrdinalIgnoreCase));

        // …and the page the modder is looking at holds the part and the edit the send created
        var part = Assert.Single(Assert.Single(vm.EditPage.Nodes).Children,
            node => node.IsPart && node.Part!.RendererSlot == Cloth);
        Assert.Contains(part.Children, node => node.EditDefinitionId == edit.Id);
    }

    [Fact]
    public void A_blender_map_label_uses_the_returned_filename_then_material_and_slot_fallback()
    {
        Assert.Equal("painted_body", MainWindowViewModel.BlenderMapLabel(
            "painted_body.png", "M_body", TargetInputKind.BaseColor, "_BaseMap"));
        Assert.Equal("M_body Base color", MainWindowViewModel.BlenderMapLabel(
            null, "M_body", TargetInputKind.BaseColor, "_BaseMap"));
    }

    /// <summary>(b) The same return's OTHER half: a part the modder emptied in Blender comes back as a
    /// placed hide, on a part the project had never authored anything for. End-to-end twin of the
    /// session-grain pin — and the route that used to refuse, because a hide anchors on one of the part's
    /// own slots and the part route had never been given any.</summary>
    [Fact]
    public async Task An_emptied_part_in_the_same_return_lands_as_its_placed_hide()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        SendBack(temp, session, twoParts: true, emptied: new[] { Body });

        Assert.True(await Settled(vm, () => Hide(session, BodyPart) is not null),
            $"the emptied part never came back as a hide — status: '{vm.EditPage.Status}'");
        var hide = Hide(session, BodyPart)!;
        Assert.Contains(hide.Id, session.Snapshot().Always);
        Assert.Contains(hide.Placements, placement => placement.IsAlways);
        // the other part of the same send still landed its own edit
        Assert.NotNull(ContentEdit(session, ClothPart));
    }

    [Fact]
    public async Task A_send_targeting_an_existing_edit_lands_on_that_edit_only()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);
        string first = CreateEdit(ui, session, ClothPart, "First");
        string selected = CreateEdit(ui, session, ClothPart, "Selected");

        string returned = SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(),
            moved: new[] { Cloth }, targetFor: (part, prepared) => ExactTarget(session, first, part, prepared),
            editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.Existing(selected),
            });

        Assert.True(await Settled(vm, () => GeometrySlot(session.Slots(selected)).ProjectAsset is not null),
            $"the selected edit never received the return — status: '{vm.EditPage.Status}'");
        Assert.Null(GeometrySlot(session.Slots(first)).ProjectAsset);
        Assert.Equal(2, session.Snapshot().EditDefinitions.Count(edit =>
            edit.Kind == EditDefinitionKind.Content && edit.Target.SameAs(ClothPart)));
        var live = BlenderBridge.ReadSessionDocument(BlenderBridge.ReadReturnSessionGlb(returned)!);
        var livePart = Assert.Single(live!.Parts);
        Assert.Equal(first, livePart.OpenedFromEditId);
        Assert.False(Assert.Single(livePart.Edits!, edit => edit.Id == first).HoldsAuthoredMesh);
        Assert.True(Assert.Single(livePart.Edits!, edit => edit.Id == selected).HoldsAuthoredMesh);
    }

    [Fact]
    public async Task A_modern_send_to_the_opened_edit_keeps_the_launch_material_guard()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui, name: "mod");
        string editId = await MintedByAMovedSendAsync(temp, session, vm, ui);
        string geometryBefore = GeometryAssetId(session, editId);
        string mapSlot = session.Slots(editId).First(state =>
            state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.Input == TargetInputKind.Normal).Slot.Id;
        BindingKind changedMapBinding = session.Slots(editId).Single(state => state.Slot.Id == mapSlot)
            .Binding.Kind == BindingKind.Neutral ? BindingKind.InheritedLiveCarrier : BindingKind.Neutral;
        BlenderSessionTarget? launch = null;
        var selection = new Dictionary<string, BlenderPartTarget>
        {
            [Cloth] = BlenderPartTarget.Existing(editId),
        };

        string returned = SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "stale-map",
            moved: new[] { Cloth }, shift: 1.25f,
            targetFor: (part, prepared) => launch = ExactTarget(session, editId, part, prepared),
            editTargets: selection, writeSidecar: false,
            beforeSidecar: () =>
            {
                ui.Invoke(() =>
                {
                    if (changedMapBinding == BindingKind.Neutral) session.ChooseNeutral(editId, mapSlot);
                    else session.ChooseInheritedCarrier(editId, mapSlot);
                    Assert.True(launch!.IsExactSlot);
                    Assert.False(MainWindowViewModel.BlenderMaterialBaselines(session.Slots(editId))
                        .SequenceEqual(launch.MaterialSlots!));
                }, Settle);
                Assert.True(ui.Idle(Settle), "the page did not settle after the in-app map change");
            });
        Assert.True(BlenderBridge.ReturnTargetMetadataExists(returned));
        Assert.Equal(editId, Assert.Single(BlenderBridge.ReadReturnTargets(returned)).EditDefinitionId);
        string sidecarFixture = temp.At("typed-target.glb");
        BlenderBridge.WriteSendSidecar(sidecarFixture, Array.Empty<string>(), selection);
        var incoming = BlenderBridge.ReadSend(returned) with
        {
            EditIds = BlenderBridge.ReadEditIds(BlenderBridge.SidecarPath(sidecarFixture)),
        };
        await vm.QueueBlenderReturn(vm.ProjectDocument, incoming);

        Assert.True(await Settled(vm, () => vm.EditPage.Status.Contains("maps changed")),
            $"the launch material guard did not refuse the send — status: '{vm.EditPage.Status}'");
        Assert.Equal(geometryBefore, GeometryAssetId(session, editId));
        Assert.Equal(changedMapBinding, session.Slots(editId).Single(state =>
            state.Slot.Id == mapSlot).Binding.Kind);
    }

    [Fact]
    public async Task A_retarget_with_a_different_material_shape_refuses_atomically()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "one-slot",
            submeshes: 1, moved: new[] { Cloth });
        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null),
            $"the one-slot target was not created — status: '{vm.EditPage.Status}'");
        string target = ContentEdit(session, ClothPart)!.Id;
        ui.Invoke(() => session.RenameEdit(target, "One slot"), Settle);
        string targetAsset = GeometryAssetId(session, target);
        string opened = CreateEdit(ui, session, ClothPart, "Opened");

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "shape-refusal",
            submeshes: 2, moved: new[] { Cloth },
            targetFor: (part, prepared) => ExactTarget(session, opened, part, prepared),
            editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.Existing(target),
            });

        string expected = MainWindowViewModel.BlenderRetargetShapeMismatch("One slot", 1, 2);
        Assert.True(await Settled(vm, () => vm.EditPage.Status == expected),
            $"the shape mismatch did not refuse exactly — status: '{vm.EditPage.Status}'");
        Assert.Equal(targetAsset, GeometryAssetId(session, target));
        Assert.Null(GeometrySlot(session.Slots(opened)).ProjectAsset);
    }

    [Fact]
    public async Task An_unchanged_return_explicitly_retargeted_to_another_shape_lands_and_cleans_the_launch_ingress()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "target-shape",
            moved: new[] { Cloth }, shift: 0.5f);
        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null),
            $"the target shape was not created — status: '{vm.EditPage.Status}'");
        string target = ContentEdit(session, ClothPart)!.Id;
        ui.Invoke(() => session.RenameEdit(target, "Target shape"), Settle);

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "opened-shape",
            moved: new[] { Cloth }, shift: 1.25f,
            editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.New("Opened shape"),
            });
        Assert.True(await Settled(vm, () => session.Snapshot().EditDefinitions.Any(edit =>
                edit.Label == "Opened shape")),
            $"the opened shape was not created — status: '{vm.EditPage.Status}'");
        string opened = session.Snapshot().EditDefinitions.Single(edit => edit.Label == "Opened shape").Id;
        string targetBefore = GeometryAssetId(session, target);
        string openedBefore = GeometryAssetId(session, opened);
        BlenderSessionTarget? launch = null;

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "unchanged-retarget",
            unchanged: new[] { Cloth },
            targetFor: (part, prepared) => launch = ExactTarget(session, opened, part, prepared),
            editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.Existing(target),
            });

        Assert.True(await Settled(vm, () => GeometryAssetId(session, target) != targetBefore),
            $"the explicit unchanged retarget did not land — status: '{vm.EditPage.Status}'");
        Assert.Equal(openedBefore, GeometryAssetId(session, opened));
        Assert.False(Directory.Exists(Path.GetDirectoryName(launch!.IngressReturn!)!),
            "the superseded launch ingress was left behind after acknowledgement");
    }

    [Fact]
    public async Task A_new_edit_selected_from_an_exact_open_cleans_the_superseded_launch_ingress()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);
        string opened = CreateEdit(ui, session, ClothPart, "Opened");
        BlenderSessionTarget? launch = null;

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "exact-new",
            moved: new[] { Cloth },
            targetFor: (part, prepared) => launch = ExactTarget(session, opened, part, prepared),
            editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.New("New destination"),
            });

        Assert.True(await Settled(vm, () => session.Snapshot().EditDefinitions.Any(edit =>
                edit.Label == "New destination")),
            $"the new exact-open destination did not land — status: '{vm.EditPage.Status}'");
        Assert.Null(GeometrySlot(session.Slots(opened)).ProjectAsset);
        Assert.False(Directory.Exists(Path.GetDirectoryName(launch!.IngressReturn!)!),
            "the exact open's unused ingress was left behind after mint acknowledgement");
    }

    [Fact]
    public async Task A_missing_legacy_opened_edit_uses_opened_from_wording()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);
        string opened = CreateEdit(ui, session, ClothPart, "Temporary");

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "legacy-missing",
            moved: new[] { Cloth }, typedSession: false,
            targetFor: (part, prepared) => ExactTarget(session, opened, part, prepared),
            afterSessionWritten: () => ui.Invoke(() => session.DeleteEdit(opened), Settle));

        Assert.True(await Settled(vm, () => vm.EditPage.Status == MainWindowViewModel.BlenderOpenedEditMissing),
            $"the legacy missing edit used selection wording — status: '{vm.EditPage.Status}'");
        Assert.Empty(session.Snapshot().EditDefinitions);
    }

    [Fact]
    public async Task Case_colliding_sidecar_part_targets_refuse_before_intake()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);
        var targets = new Dictionary<string, BlenderPartTarget>(StringComparer.Ordinal)
        {
            [Cloth] = BlenderPartTarget.New("One"),
            [Cloth.ToUpperInvariant()] = BlenderPartTarget.New("Two"),
        };

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "case-collision",
            moved: new[] { Cloth }, editTargets: targets);

        Assert.True(await Settled(vm, () => vm.EditPage.Status.Contains(Cloth.ToUpperInvariant())),
            $"the duplicate sidecar part was not named — status: '{vm.EditPage.Status}'");
        Assert.Contains("more than once", vm.EditPage.Status);
        Assert.Contains("Nothing was changed", vm.EditPage.Status);
        Assert.Empty(session.Snapshot().EditDefinitions);
    }

    [Fact]
    public async Task A_duplicate_new_target_uses_the_live_default_and_rewrites_the_session_inventory()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);
        string existing = CreateEdit(ui, session, ClothPart, "Fresh");

        string returned = SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(),
            moved: new[] { Cloth }, editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.New("fresh"),
            });

        Assert.True(await Settled(vm, () => session.Snapshot().EditDefinitions.Count == 2),
            $"the requested new edit never landed — status: '{vm.EditPage.Status}'");
        var minted = Assert.Single(session.Snapshot().EditDefinitions, edit => edit.Id != existing);
        Assert.Equal("Edit 2", minted.Label);
        Assert.NotNull(GeometrySlot(session.Slots(minted.Id)).ProjectAsset);

        string opened = BlenderBridge.ReadReturnSessionGlb(returned)!;
        var live = BlenderBridge.ReadSessionDocument(opened);
        Assert.NotNull(live);
        Assert.Equal(2, live!.Revision);
        var part = Assert.Single(live.Parts);
        Assert.Null(part.OpenedFromEditId); // launch provenance is not the last send destination
        var liveEdits = part.Edits!;
        Assert.Equal(new[] { "Fresh", "Edit 2" }, liveEdits.Select(edit => edit.Label).ToArray());
        Assert.False(liveEdits[0].HoldsAuthoredMesh);
        Assert.True(liveEdits[1].HoldsAuthoredMesh);
        Assert.Equal("Edit 3", part.DefaultEditName);
    }

    [Fact]
    public async Task A_dead_selected_edit_refuses_the_whole_return_before_any_part_changes()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);
        string deleted = CreateEdit(ui, session, ClothPart, "Former cloth");

        SendBack(temp, session, twoParts: true, emptied: Array.Empty<string>(),
            moved: new[] { Cloth, Body }, editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.Existing(deleted),
                [Body] = BlenderPartTarget.New("Body return"),
            }, afterSessionWritten: () => ui.Invoke(() => session.DeleteEdit(deleted), Settle));

        Assert.True(await Settled(vm, () => vm.EditPage.Status.Contains("Former cloth")),
            $"the dead target was not reported — status: '{vm.EditPage.Status}'");
        Assert.Contains("Nothing was changed", vm.EditPage.Status);
        Assert.Empty(session.Snapshot().EditDefinitions);
    }

    [Fact]
    public async Task An_emptied_selected_content_edit_is_untouched_while_the_unique_hide_activates()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);
        string selected = CreateEdit(ui, session, BodyPart, "Body mesh");
        var before = GeometrySlot(session.Slots(selected));

        SendBack(temp, session, twoParts: false, part: Body, emptied: new[] { Body },
            editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Body] = BlenderPartTarget.Existing(selected),
            });

        Assert.True(await Settled(vm, () => Hide(session, BodyPart) is not null),
            $"the emptied part never activated its hide — status: '{vm.EditPage.Status}'");
        var after = GeometrySlot(session.Slots(selected));
        Assert.Equal(before.Binding.Kind, after.Binding.Kind);
        Assert.Equal(before.Binding.ProjectAssetId, after.Binding.ProjectAssetId);
        Assert.Equal(before.Binding.SourceSlot, after.Binding.SourceSlot);
        Assert.Equal(before.ProjectAsset?.Id, after.ProjectAsset?.Id);
        Assert.Single(session.Snapshot().EditDefinitions, edit =>
            edit.Kind == EditDefinitionKind.Content && edit.Target.SameAs(BodyPart));
        Assert.Single(session.Snapshot().EditDefinitions, edit =>
            edit.Kind == EditDefinitionKind.Hide && edit.Target.SameAs(BodyPart));
    }

    [Theory]
    [InlineData(true, false, ".gf2session.json")]
    [InlineData(false, true, ".gf2target.json")]
    public async Task Present_but_unreadable_address_metadata_refuses_instead_of_falling_back(
        bool corruptSession, bool corruptTarget, string namedFile)
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), moved: new[] { Cloth },
            editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.New("Returned"),
            }, corruptSession: corruptSession, corruptTarget: corruptTarget);

        Assert.True(await Settled(vm, () => vm.EditPage.Status.Contains(namedFile)),
            $"the unreadable metadata was not reported — status: '{vm.EditPage.Status}'");
        Assert.Contains("Nothing was changed", vm.EditPage.Status);
        Assert.Empty(session.Snapshot().EditDefinitions);
    }

    /// <summary>(c) The watcher's take-once contract across the new asynchronous boundary: a second send
    /// arriving while the first is still applying is QUEUED, not dropped and not interleaved. The window's
    /// thread is held shut while both land, which is the only way to be sure the second one really arrived
    /// mid-apply.</summary>
    [Fact]
    public async Task A_second_send_arriving_mid_apply_is_queued_rather_than_dropped()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        using var held = new ManualResetEventSlim();
        ui.Post(() => held.Wait(Settle));   // the window is busy: nothing can commit until this lets go

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "run-one");
        SendBack(temp, session, twoParts: false, emptied: new[] { Body }, run: "run-two", part: Body);
        // both sends are on disk and neither can have committed
        Assert.Null(ContentEdit(session, ClothPart));

        held.Set();

        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null
                && Hide(session, BodyPart) is not null),
            $"a send that arrived mid-apply was dropped — status: '{vm.EditPage.Status}'");
    }

    /// <summary>(d) What one return COSTS the app, pinned at the number. Every answer a send carries used
    /// to be its own authored transaction — a two-part send is a dozen, a fifteen-part open-all around a
    /// hundred and thirty — and each one raised a change both pages rebuilt from, autosaved the whole mod
    /// and replanned ③ Build off. That is the stretch where the window never came back.
    ///
    /// <para>A return is ONE modder action and commits as ONE compound transaction: one change
    /// notification, one save. Mutation-proven: committing the mints, publishes, per-submesh answers and
    /// hides one at a time makes both counts fail here rather than anywhere else.</para></summary>
    [Fact]
    public async Task A_whole_return_commits_as_one_change_and_one_save()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        // Save once first, so the form's own one-time write-back onto the project — the mod names itself
        // after its first subject — is behind us and what is counted below is the return alone.
        ui.Invoke(vm.AutoSaveProject, Settle);
        Assert.True(ui.Idle(Settle), "the window's thread never went idle after the first save");

        var changes = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => changes.Add(change);
        long revisionBefore = session.Revision;
        int savesBefore = vm.ProjectSaves;

        // one send carrying both halves: a two-material part's geometry and maps, and an emptied part
        SendBack(temp, session, twoParts: true, emptied: new[] { Body });

        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null
                && Hide(session, BodyPart) is not null),
            $"the return never finished applying — status: '{vm.EditPage.Status}'");
        Assert.True(ui.Idle(Settle), "the window's thread never went idle after the return");

        var committed = Assert.Single(changes);
        Assert.Equal(revisionBefore + 1, committed.Revision);
        Assert.Equal(1, vm.ProjectSaves - savesBefore);

        // …and the one change names everything it moved, which is what ② and ③ aim their rework with
        var edit = ContentEdit(session, ClothPart)!;
        Assert.Contains(edit.Id, committed.EditDefinitionIds);
        Assert.Contains(Hide(session, BodyPart)!.Id, committed.EditDefinitionIds);
        Assert.All(session.Slots(edit.Id).Where(state =>
                state.Slot.Domain == TargetSlotDomain.EditOutput
                && state.Slot.Input == TargetInputKind.BaseColor),
            state => Assert.Contains(state.Slot.Id, committed.SlotIds));
    }

    /// <summary>(e) What one return costs the WINDOW, bounded — the other half of (d). (d) pins the number
    /// of changes a return makes; this pins how long the one action that makes them holds the thread.
    ///
    /// <para>The measured freeze was not the transaction: it was the commit re-doing, on the window's
    /// thread, the decode-and-re-encode the preparation had already done off it, once per map. So the bound
    /// is stated in the only unit that means anything across machines — what re-normalizing this send's own
    /// maps costs, timed here, now, on the files it actually published. A commit that does that work cannot
    /// come in under it; a commit that consumes the preparation's answer comes in at a fraction of it, with
    /// the whole autosave and page rebuild the same action carries inside that fraction.</para>
    ///
    /// <para>Mutation-proven: publishing these maps through <c>ProjectAssetIngress.Png</c> — the arm that
    /// decodes and re-encodes — takes the hold from under a third of the yardstick to over it.</para></summary>
    [Fact]
    public async Task A_map_heavy_return_never_re_normalizes_what_the_preparation_already_did()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui, clothMaterials: SubmeshesPerSend);

        ui.Invoke(vm.AutoSaveProject, Settle);
        Assert.True(ui.Idle(Settle), "the window's thread never went idle after the first save");
        ui.ForgetTimings();

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(),
            submeshes: SubmeshesPerSend, pixels: MapPixels, everyMap: true);

        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null),
            $"the return never finished applying — status: '{vm.EditPage.Status}'");
        Assert.True(ui.Idle(Settle), "the window's thread never went idle after the return");
        var edit = ContentEdit(session, ClothPart)!;
        Assert.Equal(MapsPerSend, session.Slots(edit.Id).Count(state =>
            state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Binding.Kind == BindingKind.ProjectAsset
            && state.ProjectAsset?.Kind == ProjectAssetKind.Picture));

        // the yardstick, on this machine and at this size: this send's own maps, normalized once, which is
        // exactly the work the commit used to redo on the thread the window draws with
        var maps = session.Slots(edit.Id)
            .Where(state => state.Slot.Domain == TargetSlotDomain.EditOutput
                && state.ProjectAsset?.Kind == ProjectAssetKind.Picture)
            .Select(state => Path.Combine(session.Snapshot().RootDir!, state.ProjectAsset!.File))
            .ToList();
        var clock = Stopwatch.StartNew();
        foreach (string map in maps) TextureIngress.Publish(map, temp.At("yardstick.png"));
        var reNormalizing = clock.Elapsed;

        // Everything the one action carries — the transaction, the autosave, both pages' rebuild — under
        // what re-normalizing its maps alone would cost. Measured at about half of it; putting the
        // re-normalize back takes it to about one and a half times, so the ceiling has room on both sides.
        Assert.True(ui.LongestAction < reNormalizing,
            $"the return held the window for {ui.LongestAction.TotalMilliseconds:F0} ms, against "
            + $"{reNormalizing.TotalMilliseconds:F0} ms to normalize its {maps.Count} maps once");
    }

    /// <summary>The heavy send's shape: eight materials, each with all three maps filled, at a size a
    /// modder's own maps reach. Twenty-four images is a middling open-all part, not a worst case.</summary>
    private const int SubmeshesPerSend = 8, MapPixels = 256, MapsPerSend = SubmeshesPerSend * 3;

    /// <summary>(f) One part the game files cannot answer for costs the whole return — all or nothing is
    /// the ruling and it stays — so the refusal has to say WHICH. A fifteen-part open-all told "this part
    /// isn't in the current game files" and naming none of them leaves the modder to guess.
    ///
    /// <para>Both unanswerable parts are named, not just whichever the loop reached first, and the parts
    /// that WERE fine land nothing: the send is one change or none.</para></summary>
    [Fact]
    public async Task A_part_the_install_cannot_answer_for_is_named_in_the_whole_returns_refusal()
    {
        const string Gone = "c_vesna_cape_lod0", AlsoGone = "c_vesna_hat_lod0";
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        // two parts the session offered, that the project never opened and the install cannot answer for
        SendBack(temp, session, twoParts: true, emptied: Array.Empty<string>(),
            alsoOpen: new[] { Gone, AlsoGone });

        Assert.True(await Settled(vm, () => vm.EditPage.Status.Contains("Nothing was changed")),
            $"the return never reported — status: '{vm.EditPage.Status}'");
        Assert.Contains(Gone, vm.EditPage.Status);
        Assert.Contains(AlsoGone, vm.EditPage.Status);
        Assert.Null(ContentEdit(session, ClothPart));
        Assert.Empty(session.Snapshot().EditDefinitions);
    }

    /// <summary>(g) The open-all's headline: a send-all hands back EVERY writable part of the outfit, and
    /// only the ones the modder actually changed carry an edit. Route: the real
    /// <see cref="BlenderSendWatcher"/> → <see cref="MainWindowViewModel.QueueBlenderReturn"/> → the mint
    /// arm of the return's preparation → the compound commit.
    ///
    /// <para>Two parts open, both offered a mint on return, both shipped back. One comes back with its mesh
    /// moved; the other comes back carrying exactly what it was handed. The moved one mints; the other
    /// leaves nothing behind — no edit, no hide, and no folder of files behind either.</para></summary>
    [Fact]
    public async Task A_send_all_mints_only_the_part_it_changed()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        // the first save is the form's own write-back onto the project; what is counted below is the return
        ui.Invoke(vm.AutoSaveProject, Settle);
        Assert.True(ui.Idle(Settle), "the window's thread never went idle after the first save");
        var changes = new List<AuthoredProjectChangedEventArgs>();
        session.Changed += (_, change) => changes.Add(change);
        int savesBefore = vm.ProjectSaves;

        SendBack(temp, session, twoParts: true, emptied: Array.Empty<string>(),
            moved: new[] { Cloth }, unchanged: new[] { Body });

        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null),
            $"the moved part never landed its edit — status: '{vm.EditPage.Status}'");
        Assert.True(ui.Idle(Settle), "the window's thread never went idle after the return");

        // the part that came back exactly as it went out carries nothing at all
        Assert.Null(ContentEdit(session, BodyPart));
        Assert.Null(Hide(session, BodyPart));
        Assert.Single(session.Snapshot().EditDefinitions);
        Assert.DoesNotContain(session.Snapshot().ProjectAssets,
            asset => asset.Label.Contains(Body, StringComparison.OrdinalIgnoreCase));

        // …and the whole send is still one modder action: one change, one save
        var committed = Assert.Single(changes);
        Assert.Equal(ContentEdit(session, ClothPart)!.Id, Assert.Single(committed.EditDefinitionIds));
        Assert.Equal(1, vm.ProjectSaves - savesBefore);
    }

    /// <summary>(h) The same rule on a part that already HAS an edit, opened on the exact slot the launch
    /// addresses — the other arm of the return's preparation. A return that changed nothing about the part
    /// publishes nothing into it: no transport, no revision, no save, and the edit keeps the geometry it
    /// already had.
    ///
    /// <para>The edit is made by a first send whose mesh moved and whose maps came back as the session's own,
    /// so it has geometry to keep and no authored map to re-ask for.</para></summary>
    [Fact]
    public async Task An_exact_slot_return_that_changed_nothing_leaves_the_edit_alone()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);
        string editId = await MintedByAMovedSendAsync(temp, session, vm, ui);
        string before = GeometryAssetId(session, editId);
        long revisionBefore = session.Revision;
        int savesBefore = vm.ProjectSaves;

        BlenderSessionTarget? exact = null;
        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "second",
            unchanged: new[] { Cloth },
            targetFor: (part, prepared) => exact = ExactTarget(session, editId, part, prepared));

        Assert.True(await Settled(vm, () => vm.EditPage.Status
                == MainWindowViewModel.BlenderReturnCounts(0, 0, 1)),
            $"the unchanged return never reported — status: '{vm.EditPage.Status}'");
        // Nothing was even re-exported: the re-split writes the transport's return artifact, so its absence
        // is the part having been left alone rather than published over with the bytes already there.
        Assert.False(File.Exists(exact!.IngressReturn!),
            "the unchanged part was re-exported into the transport it opened");
        Assert.Equal(before, GeometryAssetId(session, editId));
        Assert.Equal(revisionBefore, session.Revision);
        Assert.Equal(savesBefore, vm.ProjectSaves);
    }

    /// <summary>(h, twin) The same exact-slot session, sent back with the mesh MOVED, publishes over the
    /// edit. Without this the test above passes on a session that could never have landed anything.</summary>
    [Fact]
    public async Task An_exact_slot_return_whose_mesh_moved_publishes_over_the_edit()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);
        string editId = await MintedByAMovedSendAsync(temp, session, vm, ui);
        string before = GeometryAssetId(session, editId);

        BlenderSessionTarget? exact = null;
        // A SECOND offset: the first send already landed the part at 0.5, and re-publishing bytes the
        // project already holds is not a publish at all.
        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "second",
            moved: new[] { Cloth }, shift: 1.25f,
            targetFor: (part, prepared) => exact = ExactTarget(session, editId, part, prepared),
            editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.Existing(editId),
            }, typedSession: false);

        Assert.True(await Settled(vm, () => GeometryAssetId(session, editId) != before),
            $"the moved return never published — status: '{vm.EditPage.Status}'");
        Assert.True(File.Exists(exact!.IngressReturn!), "the moved part never reached its transport");
        Assert.Single(session.Snapshot().EditDefinitions);
    }

    [Fact]
    public async Task The_same_exact_session_accepts_a_second_send_and_a_revert_to_launch_geometry()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);
        string editId = CreateEdit(ui, session, ClothPart, "Repeated");

        string returned = SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(),
            moved: new[] { Cloth }, targetFor: (part, prepared) =>
                ExactTarget(session, editId, part, prepared));
        Assert.True(await Settled(vm, () => GeometrySlot(session.Slots(editId)).ProjectAsset is not null),
            $"the first exact send never landed — status: '{vm.EditPage.Status}'");
        string firstAsset = GeometryAssetId(session, editId);
        await ReadyForResend(ui);

        // No target map: this deliberately takes the legacy exact-resume branch and proves the target row's
        // binding, project-asset and transport identity were advanced at the first acknowledgement.
        Resend(temp, returned, stock: true, moved: 1.25f);
        Assert.True(await Settled(vm, () => GeometryAssetId(session, editId) != firstAsset),
            $"the same-part second send was refused — status: '{vm.EditPage.Status}'");
        string secondAsset = GeometryAssetId(session, editId);
        Assert.Equal(3, BlenderBridge.ReadSessionDocument(
            BlenderBridge.ReadReturnSessionGlb(returned)!)!.Revision);
        await ReadyForResend(ui);

        // Back to the launch vertices. Against the frozen launch comparison this was "unchanged"; against
        // the acknowledged 1.25-position baseline it is a real revert and must publish.
        Resend(temp, returned, stock: true, moved: 0f);
        Assert.True(await Settled(vm, () => GeometryAssetId(session, editId) != secondAsset),
            $"the revert was mistaken for the launch baseline — status: '{vm.EditPage.Status}'");
        Assert.NotEqual(Path.GetFullPath(returned), Path.GetFullPath(
            BlenderBridge.ReadReturnBaseline(returned)!));
    }

    [Fact]
    public async Task A_second_send_after_mint_addresses_the_acknowledged_minted_edit()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        string returned = SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(),
            moved: new[] { Cloth }, editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.New("Minted in Blender"),
            });
        Assert.True(await Settled(vm, () => session.Snapshot().EditDefinitions.Any(edit =>
                edit.Label == "Minted in Blender")),
            $"the minting send never landed — status: '{vm.EditPage.Status}'");
        string editId = session.Snapshot().EditDefinitions.Single(edit =>
            edit.Label == "Minted in Blender").Id;
        string firstAsset = GeometryAssetId(session, editId);
        await ReadyForResend(ui);

        Resend(temp, returned, stock: true, moved: 1.25f,
            editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.Existing(editId),
            });

        Assert.True(await Settled(vm, () => GeometryAssetId(session, editId) != firstAsset),
            $"the post-mint send did not address the minted edit — status: '{vm.EditPage.Status}'");
        Assert.Single(session.Snapshot().EditDefinitions, edit => edit.Kind == EditDefinitionKind.Content);
        Assert.Equal(3, BlenderBridge.ReadSessionDocument(
            BlenderBridge.ReadReturnSessionGlb(returned)!)!.Revision);
    }

    [Fact]
    public async Task Untouched_authored_textures_on_the_next_send_publish_nothing()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        string returned = SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>());
        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null),
            $"the authored texture send never landed — status: '{vm.EditPage.Status}'");
        string editId = ContentEdit(session, ClothPart)!.Id;
        long revision = session.Revision;
        var assets = session.Snapshot().ProjectAssets.Select(asset => asset.Id).ToArray();
        await ReadyForResend(ui);

        Resend(temp, returned, stock: false, moved: 0f,
            editTargets: new Dictionary<string, BlenderPartTarget>
            {
                [Cloth] = BlenderPartTarget.Existing(editId),
            });

        Assert.True(await Settled(vm, () => vm.EditPage.Status == MainWindowViewModel.BlenderReturnNoChanges),
            $"the untouched authored pictures shipped again — status: '{vm.EditPage.Status}'");
        Assert.Equal(revision, session.Revision);
        Assert.Equal(assets, session.Snapshot().ProjectAssets.Select(asset => asset.Id).ToArray());
    }

    /// <summary>(i) A return belongs to the mod it arrived FOR. Two sends land in the first mod's folder and
    /// queue behind a held window; the modder then opens a second mod for the same outfit, and both returns
    /// come due in a session that has nothing to do with them. Part routes address a part by subject and
    /// outfit, which resolve just as well in the wrong mod — so nothing is what has to land, in either mod.
    ///
    /// <para>TWO sends, because the second is the one that carries the defect: the first had already started
    /// reading when the mod was swapped, and only a return still waiting its turn has nothing left to
    /// remember which mod it was for.</para>
    ///
    /// <para>And neither send is lost: the sidecar the watcher consumed the moment it read each file is put
    /// back beside the untouched return glb, which is exactly what
    /// <see cref="BlenderSendWatcher.ScanExisting"/> looks for.</para>
    ///
    /// <para>A mod the modder never named is refused BY the same words the rest of the app calls one, not by
    /// the slug of the folder it happens to sit in.</para></summary>
    [Theory]
    [InlineData(FirstMod, FirstMod)]
    [InlineData("", MainWindowViewModel.UntitledMod)]
    public async Task Returns_queued_for_a_mod_that_was_closed_land_nothing_and_are_put_back(
        string name, string refusedBy)
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, first) = await OpenProjectAsync(temp, ui, name: name);

        using var held = new ManualResetEventSlim();
        ui.Post(() => held.Wait(Settle));   // the window is busy: nothing can commit until this lets go
        string one = SendBack(temp, first, twoParts: false, emptied: Array.Empty<string>(), run: "run-one");
        Assert.True(await Queued(vm), "the first send never reached the return queue");
        var reading = vm.PendingBlenderReturns;
        string two = SendBack(temp, first, twoParts: false, emptied: Array.Empty<string>(),
            run: "run-two", part: Body);
        Assert.True(await WaitFor(() => !ReferenceEquals(vm.PendingBlenderReturns, reading)),
            "the second send never joined the queue behind the first");

        // the modder opens another mod while both sends wait — off the held thread, as an open's own work is
        var second = await OpenSecondModAsync(temp, vm);
        held.Set();

        Assert.True(await Settled(vm, () => vm.PendingBlenderReturns.IsCompleted),
            $"the returns never finished — status: '{vm.EditPage.Status}'");
        Assert.Equal(MainWindowViewModel.BlenderReturnModClosed(refusedBy), vm.EditPage.Status);
        Assert.Empty(first.Snapshot().EditDefinitions);
        Assert.Empty(second.Snapshot().EditDefinitions);
        Assert.True(File.Exists(BlenderBridge.SidecarPath(one)),
            "the first send was consumed rather than put back");
        Assert.True(File.Exists(BlenderBridge.SidecarPath(two)),
            "the second send was consumed rather than put back");
    }

    /// <summary>(i, the other half) Putting the send back is only worth anything if it is FOUND again. The
    /// mod that owned the refused return is reopened, and its own open path — the watcher's scan of sends
    /// that landed while it was closed — takes the send through exactly as a live one.</summary>
    [Fact]
    public async Task A_send_put_back_lands_when_its_mod_is_opened_again()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, first) = await OpenProjectAsync(temp, ui, name: FirstMod);

        using var held = new ManualResetEventSlim();
        ui.Post(() => held.Wait(Settle));
        SendBack(temp, first, twoParts: false, emptied: Array.Empty<string>());
        Assert.True(await Queued(vm), "the send never reached the return queue");
        await OpenSecondModAsync(temp, vm);
        held.Set();
        Assert.True(await Settled(vm, () => vm.PendingBlenderReturns.IsCompleted),
            $"the refused return never finished — status: '{vm.EditPage.Status}'");

        Assert.True(await vm.OpenModAsync(temp.At("mod")));
        var reopened = vm.ProjectDocument.Session;

        Assert.True(await Settled(vm, () => ContentEdit(reopened, ClothPart) is not null),
            $"the send put back was never found again — status: '{vm.EditPage.Status}'");
    }

    /// <summary>(i, twin) The queue is not poisoned by the return it refused: a send for the mod that IS open
    /// lands in it, on the same watcher and the same queue.</summary>
    [Fact]
    public async Task A_send_for_the_mod_now_open_still_lands_after_a_refused_one()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, first) = await OpenProjectAsync(temp, ui);

        using var held = new ManualResetEventSlim();
        ui.Post(() => held.Wait(Settle));
        SendBack(temp, first, twoParts: false, emptied: Array.Empty<string>());
        Assert.True(await Queued(vm), "the send never reached the return queue");
        var second = await OpenSecondModAsync(temp, vm);
        held.Set();
        Assert.True(await Settled(vm, () => vm.PendingBlenderReturns.IsCompleted),
            $"the refused return never finished — status: '{vm.EditPage.Status}'");

        SendBack(temp, second, twoParts: false, emptied: Array.Empty<string>(), run: "second-mod-run");

        Assert.True(await Settled(vm, () => ContentEdit(second, ClothPart) is not null),
            $"the send for the open mod never landed — status: '{vm.EditPage.Status}'");
        Assert.Empty(first.Snapshot().EditDefinitions);
    }

    /// <summary>(j) The page says it is working — once the work has been going long enough to be worth a
    /// line. A send-all's whole read runs off the window's thread, and until the transaction's own line
    /// lands the page said nothing at all about it.
    ///
    /// <para>The READ is held here, not the window: the preparation's first look at the project waits on the
    /// session's own transaction gate, so the window's thread stays free — which is the only state in which
    /// the page can say anything at all while a return is still being read.</para>
    ///
    /// <para>The elapsed check is the other half of the rule. Untouched parts are skipped now, so most
    /// returns land in well under a second, and a line that appears and is replaced that fast is a flicker
    /// rather than a report — so it waits until the read has actually been running.</para></summary>
    [Fact]
    public async Task The_page_says_it_is_applying_only_once_a_return_has_been_read_a_while()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holding = Task.Run(() => session.Compound(_ => { reading.Set(); release.Wait(Settle); }));
        Assert.True(reading.Wait(Settle), "the session was never held");

        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(),
            modRoot: temp.At("mod"));   // the session is held: it cannot be asked where the mod is
        Assert.True(await Queued(vm), "the send never reached the return queue");
        var since = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(await WaitFor(() => vm.EditPage.Status == MainWindowViewModel.BlenderReturnApplying),
            $"the page never said it was applying — status: '{vm.EditPage.Status}'");
        since.Stop();
        Assert.True(since.Elapsed >= TimeSpan.FromMilliseconds(250),
            $"the line went up straight away ({since.ElapsedMilliseconds} ms) rather than once the read "
            + "had been running");
        Assert.False(vm.PendingBlenderReturns.IsCompleted, "the return landed while the read was held");
        // …and the rows it is about to change say they are working, in the page's own busy convention —
        // the same gate a Blender open on that subject holds, rather than the status line alone.
        Assert.True(vm.EditPage.Nodes.Any(node => node.IsBusy),
            "no row said it was working while the return was being applied");

        release.Set();
        await holding;

        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null),
            $"the return never finished applying — status: '{vm.EditPage.Status}'");
        Assert.NotEqual(MainWindowViewModel.BlenderReturnApplying, vm.EditPage.Status);
        // The gate is given back with the return: a row left working would wait for the life of the app.
        Assert.True(await WaitFor(() => !vm.EditPage.Nodes.Any(node => node.IsBusy)),
            "a row was still saying it was working after the return finished");
    }

    /// <summary>(j, twin) …and it never reaches the page of a mod the return has nothing to do with. The
    /// read is held while the modder opens a second mod, so the working line comes due with the window free
    /// and a stranger's page in front of it — which is exactly where it landed when it was said before
    /// anyone had asked which mod the return was for.</summary>
    [Fact]
    public async Task The_working_line_never_reaches_the_page_of_a_mod_the_return_is_not_for()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, first) = await OpenProjectAsync(temp, ui, name: FirstMod);

        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holding = Task.Run(() => first.Compound(_ => { reading.Set(); release.Wait(Settle); }));
        Assert.True(reading.Wait(Settle), "the session was never held");

        SendBack(temp, first, twoParts: false, emptied: Array.Empty<string>(), modRoot: temp.At("mod"));
        Assert.True(await Queued(vm), "the send never reached the return queue");

        var second = await OpenSecondModAsync(temp, vm);
        var said = new List<string>();
        vm.EditPage.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(Remold.App.ViewModels.EditPage.EditPageVm.Status))
                lock (said) said.Add(vm.EditPage.Status);
        };
        // long enough that the line is genuinely due, with the window free to show it and the return still
        // being read: what keeps it off this page has to be the mod it names
        await Task.Delay(TimeSpan.FromMilliseconds(900));
        lock (said) Assert.DoesNotContain(MainWindowViewModel.BlenderReturnApplying, said);

        release.Set();
        await holding;
        Assert.True(await Settled(vm, () => vm.PendingBlenderReturns.IsCompleted),
            $"the return never finished — status: '{vm.EditPage.Status}'");
        Assert.Equal(MainWindowViewModel.BlenderReturnModClosed(FirstMod), vm.EditPage.Status);
        Assert.Empty(second.Snapshot().EditDefinitions);
    }

    /// <summary>(k) The file the launch handed Blender is opened only where a part's GEOMETRY has to be
    /// compared against it. Every part of this send came back painted, so every one of them answers the
    /// change question on its maps alone — and opening the baseline there would be the whole combined glb,
    /// every part's geometry and every texture in it, parsed a second time and held beside the first for a
    /// question nobody asked.
    ///
    /// <para>The witness is the file being GONE before the send lands: a baseline that was named and could
    /// not be opened makes the return say so, and this return says nothing of the kind.</para></summary>
    [Fact]
    public async Task A_return_whose_parts_all_came_back_painted_never_opens_the_baseline()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        SendBack(temp, session, twoParts: true, emptied: Array.Empty<string>(), dropTheOpenedGlb: true);

        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null
                && ContentEdit(session, BodyPart) is not null),
            $"the painted parts never landed — status: '{vm.EditPage.Status}'");
        Assert.DoesNotContain(MainWindowViewModel.BlenderReturnBaselineUnreadable, vm.EditPage.Status);
    }

    /// <summary>(l) …and when the comparison IS asked for and the file is gone, the return takes every part
    /// rather than dropping an edit it could not check — and says so, because the modder is otherwise handed
    /// a mod full of parts they never touched with no reason given.
    ///
    /// <para>Not a hypothetical: the mod folder is renamed whenever the mod is, and Blender being open does
    /// not hold that rename. Both parts here came back exactly as they went out, which with the baseline in
    /// hand is the send that lands nothing at all.</para></summary>
    [Fact]
    public async Task A_return_whose_baseline_is_gone_takes_every_part_and_says_so()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        SendBack(temp, session, twoParts: true, emptied: Array.Empty<string>(),
            unchanged: new[] { Cloth, Body }, dropTheOpenedGlb: true);

        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null
                && ContentEdit(session, BodyPart) is not null),
            $"a return that could not compare anything still dropped an edit — status: '{vm.EditPage.Status}'");
        Assert.Contains(MainWindowViewModel.BlenderReturnBaselineUnreadable, vm.EditPage.Status);
        Assert.All(session.Snapshot().EditDefinitions.Where(edit => edit.Kind == EditDefinitionKind.Content),
            edit => Assert.Contains(MainWindowViewModel.BlenderReturnBaselineUnreadable,
                edit.ReturnWarning));
    }

    [Fact]
    public async Task An_unreadable_prepared_part_degrades_only_that_parts_uv_filter_and_names_it()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        SendBack(temp, session, twoParts: true, emptied: Array.Empty<string>(),
            moved: new[] { Cloth }, unchanged: new[] { Body },
            inventedUv1: new[] { Cloth }, dropPrepared: new[] { Cloth });

        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null),
            $"the degraded part never landed — status: '{vm.EditPage.Status}'");
        Assert.Null(ContentEdit(session, BodyPart));
        Assert.Contains($"Couldn't read the file {Cloth} was opened from, so every UV layer that came "
            + "back was kept.", vm.EditPage.Status);

        var edit = ContentEdit(session, ClothPart)!;
        Assert.Contains($"Couldn't read the file {Cloth} was opened from", edit.ReturnWarning);
        var geometry = GeometrySlot(session.Slots(edit.Id));
        string published = Path.Combine(session.Snapshot().RootDir!, geometry.ProjectAsset!.File);
        Assert.True(MeshGltf.ImportGlb(published, Cloth, lenient: true).Has("TexCoord1"),
            "the unreadable prepared contract filtered the invented UV despite degrading to no filtering");
    }

    /// <summary>(m) A part whose only change was an Object-mode move. Blender writes that move on the
    /// object's NODE for an unskinned part and the geometry read ignores it, so the part comes back with
    /// byte-identical vertices, asks for no map, and is skipped — rightly, since there is nothing in it to
    /// save. What the modder must not be given is silence: they moved something in the viewport, and the
    /// page's own answer is that nothing came back.
    ///
    /// <para>So the note is resolved against every part the return CONSIDERED rather than the parts it
    /// landed, and stands beside the zero-change sentence rather than instead of it. The note's own wording
    /// is pinned where it is written.</para></summary>
    [Fact]
    public async Task A_part_moved_only_in_object_mode_is_skipped_and_still_reported()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        SendBackRigidPart(temp, session, Body, nudged: true);

        Assert.True(await Settled(vm, () => vm.EditPage.Status.StartsWith(
                MainWindowViewModel.BlenderReturnCounts(0, 0, 1) + " ", StringComparison.Ordinal)),
            $"the dropped move was never reported — status: '{vm.EditPage.Status}'");
        Assert.Contains(Body, vm.EditPage.Status);
        Assert.Empty(session.Snapshot().EditDefinitions);   // and it is still not an edit
    }

    /// <summary>(m, twin) The same lone rigid part, sent back without the move: nothing landed and nothing
    /// to report, so the page says only that. Without this the test above passes on a note the return
    /// carries no matter what came back.</summary>
    [Fact]
    public async Task A_lone_rigid_part_that_came_back_untouched_reports_only_the_zero_change_line()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        using var ui = new PumpedUiThread();
        var (vm, session) = await OpenProjectAsync(temp, ui);

        SendBackRigidPart(temp, session, Body, nudged: false);

        Assert.True(await Settled(vm, () => vm.EditPage.Status
                == MainWindowViewModel.BlenderReturnCounts(0, 0, 1)),
            $"the untouched return never reported — status: '{vm.EditPage.Status}'");
        Assert.Empty(session.Snapshot().EditDefinitions);
    }

    // ---- the project, opened the way the app opens one -------------------------------------------

    /// <summary>A saved mod whose two parts are OPENED and unauthored — the state an open-all offers a mint
    /// row for — brought up through the window's own open path so the page, the watcher and the mod root
    /// are wired exactly as they are in the app.</summary>
    private static async Task<(MainWindowViewModel Vm, AuthoredEditSession Session)> OpenProjectAsync(
        TempGame temp, PumpedUiThread ui, int clothMaterials = 2, string name = "")
    {
        string root = temp.At("mod");
        var seed = AuthoredProjectDocument.New().Session;
        seed.EnsurePartSlots(ClothPart, _ => PaintedPart(ClothPart, materials: clothMaterials));
        seed.EnsurePartSlots(BodyPart, _ => PaintedPart(BodyPart));
        var saved = seed.Snapshot();
        saved.Info.Name = name;
        AuthoredProjectSerializer.Save(saved, ModProject.ManifestPathFor(root));

        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: ui.Dispatch);
        Assert.True(await vm.OpenModAsync(root));
        var session = vm.ProjectDocument.Session;
        Assert.Empty(session.Snapshot().EditDefinitions);
        return (vm, session);
    }

    /// <summary>A SECOND saved mod for the same outfit, opened through the window's own open path so the
    /// first mod's document is the one replaced. Its parts are opened and unauthored, exactly as the
    /// first's are, so a part route from either mod would resolve here.</summary>
    private static async Task<AuthoredEditSession> OpenSecondModAsync(TempGame temp, MainWindowViewModel vm)
    {
        string root = temp.At("other-mod");
        var seed = AuthoredProjectDocument.New().Session;
        seed.EnsurePartSlots(ClothPart, _ => PaintedPart(ClothPart, materials: 2));
        seed.EnsurePartSlots(BodyPart, _ => PaintedPart(BodyPart));
        AuthoredProjectSerializer.Save(seed.Snapshot(), ModProject.ManifestPathFor(root));
        Assert.True(await vm.OpenModAsync(root));
        return vm.ProjectDocument.Session;
    }

    /// <summary>The round-trip fixture models the painted material assumed by this transport-era suite:
    /// base colour, normal and RMO are all real installed bindings. Stage 1 no longer invents the latter
    /// two for a material that does not bind them, so the fixture states that premise explicitly.</summary>
    private static LegacyResolvedPart PaintedPart(TargetPart part, int materials = 1)
    {
        var resolved = AuthoredParts.Resolve(part, materials);
        return resolved with
        {
            Materials = resolved.Materials.Select(material =>
            {
                var baseMap = material.Textures.Single(texture => texture.Input == TargetInputKind.BaseColor);
                string stem = baseMap.Texture.Name?.EndsWith("_base", StringComparison.Ordinal) == true
                    ? baseMap.Texture.Name[..^5]
                    : material.LegacyName;

                LegacyResolvedTexture Map(TargetInputKind input, string property, string suffix, long offset)
                {
                    var texture = new GameAssetRef
                    {
                        GameBuild = baseMap.Texture.GameBuild,
                        LogicalBundle = baseMap.Texture.LogicalBundle,
                        PathId = baseMap.Texture.PathId + offset,
                        Name = stem + suffix,
                    };
                    return new LegacyResolvedTexture(input, baseMap.LegacyBundle, texture.Name!, texture.PathId,
                        texture, property);
                }

                return material with
                {
                    Textures = material.Textures.Concat(new[]
                    {
                        Map(TargetInputKind.Normal, "_BumpMap", "_normal", 2),
                        Map(TargetInputKind.Rmo, "_RMOTex", "_rmo", 3),
                    }).ToList(),
                };
            }).ToList(),
        };
    }

    /// <summary>Land one content edit on the cloth part the way the app lands one: a send whose mesh moved
    /// and whose images came back as the session's own, so the edit holds published geometry and no authored
    /// map. Returns its id.</summary>
    private static async Task<string> MintedByAMovedSendAsync(TempGame temp, AuthoredEditSession session,
        MainWindowViewModel vm, PumpedUiThread ui)
    {
        SendBack(temp, session, twoParts: false, emptied: Array.Empty<string>(), run: "first",
            moved: new[] { Cloth });
        Assert.True(await Settled(vm, () => ContentEdit(session, ClothPart) is not null),
            $"the first send never landed — status: '{vm.EditPage.Status}'");
        Assert.True(ui.Idle(Settle), "the window's thread never went idle after the first send");
        return ContentEdit(session, ClothPart)!.Id;
    }

    /// <summary>The exact-slot row the launch writes for a part that already has an edit: a transport opened
    /// on the edit's own geometry slot, and the map baselines the return is checked against — both built out
    /// of the launch's own parts rather than restated here.</summary>
    private static BlenderSessionTarget ExactTarget(AuthoredEditSession session, string editId,
        string part, string prepared)
    {
        var slots = session.Slots(editId);
        var geometry = GeometrySlot(slots);
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), editId, geometry.Slot.Id, prepared);
        return new BlenderSessionTarget(part, ingress.SourceProjectAssetId ?? "", prepared, editId,
            geometry.Slot.Id, ingress.ReturnArtifact, geometry.Binding.Kind,
            MainWindowViewModel.BlenderMaterialBaselines(slots), Subject: Character, Outfit: Outfit);
    }

    private static EditSlotState GeometrySlot(IReadOnlyList<EditSlotState> slots) =>
        slots.Single(state => state.Slot.Domain == TargetSlotDomain.Game
            && state.Slot.Input == TargetInputKind.Geometry
            && (state.Slot.Tier is null || state.Slot.Tier == "lod0"));

    /// <summary>The project asset an edit's mesh is bound to — what a publish over it replaces.</summary>
    private static string GeometryAssetId(AuthoredEditSession session, string editId) =>
        GeometrySlot(session.Slots(editId)).ProjectAsset!.Id;

    // ---- what Blender left behind ----------------------------------------------------------------

    /// <summary>Write one complete send into the mod's own ingress folder, the way an open-all does: the
    /// composition the session opened, a prepared workspace glb per part, the part-addressable target rows,
    /// then Blender's output and — LAST, as the bridge writes it — the sidecar the
    /// watcher fires on. Returns the return glb's path.
    ///
    /// <para>By default every shipped part comes back the way a painted part does: its own new images, and
    /// no map record beside the return, which is what makes those images read as the modder's work.
    /// <paramref name="unchanged"/> and <paramref name="moved"/> name the parts that come back some other
    /// way — carrying exactly the images the session handed out, with the mesh left alone or shifted.</para>
    ///
    /// <para><paramref name="targetFor"/> replaces the part route a row would otherwise get, for a
    /// session whose parts already have edits and open on an exact slot.</para>
    ///
    /// <para><paramref name="dropTheOpenedGlb"/> deletes the composition the session was exported from
    /// before the send lands, which is what a mod folder renamed while Blender was open leaves behind.
    /// <paramref name="modRoot"/> spares the caller the session read this otherwise makes, for a test that
    /// is deliberately holding the session.</para></summary>
    private static string SendBack(TempGame temp, AuthoredEditSession session, bool twoParts,
        IReadOnlyList<string> emptied, string run = "run", string part = Cloth,
        int submeshes = 2, int pixels = 4, bool everyMap = false,
        IReadOnlyList<string>? alsoOpen = null, IReadOnlyList<string>? unchanged = null,
        IReadOnlyList<string>? moved = null, float shift = 0.5f,
        Func<string, string, BlenderSessionTarget>? targetFor = null,
        bool dropTheOpenedGlb = false, string? modRoot = null,
        IReadOnlyList<string>? inventedUv1 = null, IReadOnlyList<string>? dropPrepared = null,
        IReadOnlyDictionary<string, BlenderPartTarget>? editTargets = null,
        bool corruptSession = false, bool corruptTarget = false, bool typedSession = true,
        Action? afterSessionWritten = null, bool writeSidecar = true, Action? beforeSidecar = null)
    {
        string root = modRoot ?? session.Snapshot().RootDir!;
        string runDir = Path.Combine(root, ProjectAssetIngress.DirectoryName, "blender", run);
        string partsDir = Path.Combine(runDir, "parts");
        Directory.CreateDirectory(partsDir);
        var untouched = (unchanged ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remolded = (moved ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extraUv = (inventedUv1 ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingPrepared = (dropPrepared ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var opened = (twoParts ? new[] { Cloth, Body } : new[] { part })
            .Concat(alsoOpen ?? Array.Empty<string>()).ToArray();
        string composition = Path.Combine(runDir, "composition.glb");
        MeshGltf.ExportCombinedRiggedGlb(opened
                .Select(name => Rigged(temp, name, stock: true, submeshes, pixels, everyMap)).ToList(),
            hash => BonePaths[hash], composition);

        var targets = new List<BlenderSessionTarget>();
        var parts = new List<SessionPart>();
        foreach (string name in opened)
        {
            string prepared = Path.Combine(partsDir, name + ".glb");
            MeshGltf.ReexportPartGlb(composition, name, prepared, recordGlb: composition);
            var target = targetFor?.Invoke(name, prepared)
                ?? new BlenderSessionTarget(name, "", prepared, Subject: Character, Outfit: Outfit);
            targets.Add(target);
            var partTarget = AuthoredParts.Part(Character, Outfit, name);
            // The held-session tests pass modRoot specifically so this fixture does not query the live
            // session while its transaction gate is occupied. They model an older contract as before.
            AuthoredProject? snapshot = typedSession && modRoot is null ? session.Snapshot() : null;
            parts.Add(new SessionPart(name, Edited: !string.IsNullOrWhiteSpace(target.ProjectAssetId),
                EditId: target.EditDefinitionId,
                Edits: snapshot is not null
                    ? MainWindowViewModel.BlenderSessionEdits(snapshot, partTarget) : null,
                DefaultEditName: snapshot is not null
                    ? AuthoredEditSession.NewEditLabel(snapshot, partTarget, null) : null));
            if (missingPrepared.Contains(name)) File.Delete(prepared);
        }
        BlenderBridge.WriteSession(composition, null, parts, "return.glb", targets);
        afterSessionWritten?.Invoke();

        // Blender's Send: only the parts that still carry geometry.
        var shipped = opened.Where(name => !emptied.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        string returned = Path.Combine(runDir, "return.glb");
        if (shipped.Count == 0)
        {
            // every part of the session emptied: the send carries no mesh at all
            var empty = SharpGLTF.Schema2.ModelRoot.CreateModel();
            empty.UseScene("scene");
            empty.SaveGLB(returned);
        }
        else
        {
            MeshGltf.ExportCombinedRiggedGlb(shipped
                    .Select(name => Rigged(temp, name,
                        stock: untouched.Contains(name) || remolded.Contains(name),
                        submeshes, pixels, everyMap, moved: remolded.Contains(name) ? shift : 0f,
                        uv1: extraUv.Contains(name))).ToList(),
                hash => BonePaths[hash], returned);
            File.Delete(PreviewMaps.SidecarPath(returned));
        }

        if (dropTheOpenedGlb) File.Delete(composition);
        if (corruptSession) File.WriteAllText(BlenderBridge.SessionPath(composition), "{broken session");
        if (corruptTarget) File.WriteAllText(BlenderBridge.TargetPath(returned), "{broken target");

        beforeSidecar?.Invoke();
        if (writeSidecar) BlenderBridge.WriteSendSidecar(returned, emptied, editTargets);
        return returned;
    }

    /// <summary>Overwrite the SAME run's return artifact the way a still-open Blender scene sends again.
    /// The target and session documents are deliberately left in place: their acknowledged revision,
    /// comparison baseline and exact rows are the subject of the multi-send tests.</summary>
    private static void Resend(TempGame temp, string returned, bool stock, float moved,
        IReadOnlyDictionary<string, BlenderPartTarget>? editTargets = null)
    {
        if (File.Exists(returned)) File.Delete(returned);
        string mapRecord = PreviewMaps.SidecarPath(returned);
        if (File.Exists(mapRecord)) File.Delete(mapRecord);
        MeshGltf.ExportCombinedRiggedGlb(new[] { Rigged(temp, Cloth, stock, 2, 4, false, moved) },
            hash => BonePaths[hash], returned);
        if (File.Exists(mapRecord)) File.Delete(mapRecord);
        BlenderBridge.WriteSendSidecar(returned, Array.Empty<string>(), editTargets);
    }

    private static async Task ReadyForResend(PumpedUiThread ui)
    {
        Assert.True(ui.Idle(Settle), "the window's thread never went idle before the next send");
        await Task.Delay(600); // beyond the watcher's same-path Created/Changed burst debounce
    }

    /// <summary>A LONE part's session, the way the app opens one part on its own: Blender is handed that
    /// part's own glb and its Send writes a distinct return artifact beside it. The part is UNSKINNED, which
    /// is the state an Object-mode move survives in — glTF puts no transform on a skinned node, and Blender
    /// bakes the move into the vertices there instead.
    ///
    /// <para><paramref name="nudged"/> puts that move where Blender puts it: on the object's NODE, with the
    /// vertices exactly as they were handed out. That is the send the geometry comparison reads as
    /// "nothing changed" and the modder reads as a part they moved.</para></summary>
    private static string SendBackRigidPart(TempGame temp, AuthoredEditSession session, string part,
        bool nudged)
    {
        string runDir = Path.Combine(session.Snapshot().RootDir!, ProjectAssetIngress.DirectoryName,
            "blender", "rigid");
        Directory.CreateDirectory(runDir);
        var maps = new (string?, string?, string?)[]
            { (WritePng(temp.At($"maps/{part}.rigid.b.png"), 44, 4), null, null) };
        string opened = Path.Combine(runDir, part + ".glb");
        MeshGltf.ExportGlb(RigidTriangle(part), opened, perSubmesh: maps);

        string sendAs = BlenderBridge.PartSendName(opened);
        BlenderBridge.WriteSession(opened, null, new[] { new SessionPart(part, Edited: false) }, sendAs,
            new[] { new BlenderSessionTarget(part, "", opened, Subject: Character, Outfit: Outfit) });

        // Blender's Send: the same geometry and the same images, and no map record of its own.
        string returned = BlenderBridge.PartSendPath(opened);
        MeshGltf.ExportGlb(RigidTriangle(part), returned, perSubmesh: maps);
        File.Delete(PreviewMaps.SidecarPath(returned));
        if (nudged) MoveTheObject(returned, part);

        File.WriteAllText(BlenderBridge.SidecarPath(returned),
            "{\"source\":\"blender-send\",\"hiddenParts\":[]}");
        return returned;
    }

    /// <summary>What Blender leaves behind on an unskinned part moved in Object mode: a transform on the
    /// node that instances the mesh, and vertex positions untouched.</summary>
    private static void MoveTheObject(string glb, string part)
    {
        var model = SharpGLTF.Schema2.ModelRoot.Load(glb);
        foreach (var node in model.LogicalNodes)
            if (node.Mesh is { } mesh && mesh.Name == part)
                node.LocalMatrix = Matrix4x4.CreateTranslation(5, 0, 0);
        model.SaveGLB(glb);
    }

    /// <summary>One unskinned part: a triangle with a UV, which is what a rigid part is and the only shape
    /// a node transform survives on.</summary>
    private static UnityMesh RigidTriangle(string name) => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1 },
        },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["TexCoord0"] = 2 },
        Submeshes = new List<int[]> { new[] { 0, 1, 2 } },
    };

    /// <summary>One part for the combined glb. Cloth is the multi-material shape — <paramref name="submeshes"/>
    /// of them, a map of its own on each — and body is the plain one. <paramref name="stock"/> picks which set
    /// of images it carries: the ones the session handed Blender, or the different ones the modder sends back.
    /// <paramref name="everyMap"/> fills the normal and RMO slots too, which is what a real painted part
    /// carries and what makes a send's map count three per submesh rather than one.</summary>
    private static MeshGltf.RiggedPart Rigged(TempGame temp, string name, bool stock, int submeshes,
        int pixels, bool everyMap, float moved = 0f, bool uv1 = false)
    {
        string tag = stock ? "stock" : "sent";
        int count = name == Cloth ? submeshes : 1;
        var maps = Enumerable.Range(0, count).Select(submesh =>
        {
            byte seed = (byte)((stock ? 20 : 130) + submesh * 3);
            return ((string?)Map(submesh, "b", seed),
                everyMap ? Map(submesh, "n", (byte)(seed + 1)) : null,
                everyMap ? Map(submesh, "r", (byte)(seed + 2)) : null);
        }).ToList();
        return new MeshGltf.RiggedPart(Submeshes(name, count, moved, uv1),
            new MeshSkin
            {
                BoneHashes = new[] { RootBone },
                BindPoses = new List<Matrix4x4> { Matrix4x4.Identity },
            },
            BaseColorPng: maps[0].Item1,
            PerSubmesh: maps);

        string Map(int submesh, string input, byte seed) =>
            WritePng(temp.At($"maps/{name}.{tag}.{submesh}.{input}.png"), seed, pixels);
    }

    private const uint RootBone = 0x1111_1111;
    private static readonly Dictionary<uint, string> BonePaths = new() { [RootBone] = "root" };

    /// <summary>One triangle per submesh, side by side: the part whose materials a return has to keep
    /// apart, at whatever width the case needs. <paramref name="moved"/> shifts the whole part along X, by
    /// far more than the send-back comparison's own tolerance — the modder's hand on the mesh. Two sends of
    /// the same part need two DIFFERENT shifts: publishing bytes the project already holds changes
    /// nothing, and a second send at the same offset would prove nothing about the comparison.</summary>
    private static UnityMesh Submeshes(string name, int count, float moved = 0f, bool uv1 = false)
    {
        var vertex = new List<float>();
        var uv = new List<float>();
        var submeshes = new List<int[]>();
        for (int submesh = 0; submesh < count; submesh++)
        {
            float x = submesh * 2f + moved;
            vertex.AddRange(new[] { x, 0, 0, x + 1, 0, 0, x, 1, 0 });
            uv.AddRange(new[] { 0f, 0, 1, 0, 0, 1 });
            submeshes.Add(new[] { submesh * 3, submesh * 3 + 1, submesh * 3 + 2 });
        }
        int vertices = count * 3;
        var weights = new float[vertices * 4];
        for (int vert = 0; vert < vertices; vert++) weights[vert * 4] = 1f;
        var mesh = new UnityMesh
        {
            Name = name,
            VertexCount = vertices,
            Channels = new()
            {
                ["Vertex"] = vertex.ToArray(),
                ["TexCoord0"] = uv.ToArray(),
                ["BlendIndices"] = new float[vertices * 4],
                ["BlendWeight"] = weights,
            },
            Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
            Submeshes = submeshes,
        };
        if (uv1)
        {
            mesh.Channels["TexCoord1"] = uv.Select(value => value + 10f).ToArray();
            mesh.Dims["TexCoord1"] = 2;
        }
        return mesh;
    }

    private static string WritePng(string path, byte seed, int pixels = 4)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(pixels, pixels);
        // Noise, so the encoder has real work: a flat fill compresses to nothing and would make a map cost
        // far less here than the ones a modder actually paints.
        var random = new Random(seed);
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)(seed ^ random.Next(256)), (byte)random.Next(256),
                        (byte)random.Next(256), 255);
            }
        });
        image.SaveAsPng(path);
        return path;
    }

    // ---- reading the outcome ---------------------------------------------------------------------

    private static AuthoredEditOutlineEntry? ContentEdit(AuthoredEditSession session, TargetPart part) =>
        session.Outline().Edits.FirstOrDefault(edit => edit.Kind == EditDefinitionKind.Content
            && edit.Target.SameAs(part));

    private static AuthoredEditOutlineEntry? Hide(AuthoredEditSession session, TargetPart part) =>
        session.Outline().Edits.FirstOrDefault(edit => edit.Kind == EditDefinitionKind.Hide
            && edit.Target.SameAs(part));

    private static string CreateEdit(PumpedUiThread ui, AuthoredEditSession session, TargetPart part,
        string label)
    {
        string? id = null;
        ui.Invoke(() => id = session.CreateEdit(part, label), Settle);
        Assert.True(ui.Idle(Settle), "the window's thread never went idle after creating the edit");
        return id!;
    }

    /// <summary>Wait until the watcher has HANDED a send to the return queue. The file event travels a
    /// thread of its own, so a test that swaps the mod out from under a send has to know the send is really
    /// in flight first — and while the window's thread is held it cannot leave the queue again.</summary>
    private static Task<bool> Queued(MainWindowViewModel vm) =>
        WaitFor(() => !vm.PendingBlenderReturns.IsCompleted);

    private static async Task<bool> WaitFor(Func<bool> reached)
    {
        var deadline = DateTime.UtcNow + Settle;
        while (DateTime.UtcNow < deadline)
        {
            if (reached()) return true;
            await Task.Delay(25);
        }
        return false;
    }

    /// <summary>Wait for the return to have LANDED and the apply queue to have drained. An apply that came
    /// back onto the window's thread from inside itself never gets here, so this timing out is the deadlock
    /// pin rather than a flake.
    ///
    /// <para>Mutation-proven: a single wait inside the commit on a worker whose continuation needs the same
    /// thread (a <c>FromCurrentSynchronizationContext</c> continuation, which is the shape every
    /// UI-started <c>await</c> has) makes this exact assertion fail, at its timeout.</para></summary>
    private static async Task<bool> Settled(MainWindowViewModel vm, Func<bool> landed)
    {
        var deadline = DateTime.UtcNow + Settle;
        while (DateTime.UtcNow < deadline)
        {
            if (landed() && vm.PendingBlenderReturns.IsCompleted) return true;
            await Task.Delay(25);
        }
        return false;
    }
}
