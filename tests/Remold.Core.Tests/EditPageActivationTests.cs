using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The window's half of the ② Edit page: which session it is holding, and when it is handed one.
///
/// <para>The page holds the SESSION rather than the document, so it cannot see a project open, close or
/// convert on its own — every one of those is the window handing it the new one. What each test asserts is
/// the page's own tree, since a page pointed at nothing draws nothing and a page pointed at intent draws a
/// row per subject in it.</para>
/// </summary>
[Collection("Dispatcher")]
public class EditPageActivationTests
{
    private const string Character = "Vesna", Outfit = "VesnaSSR01", Mesh = "c_vesna01_body_lod0";

    private static TargetPart Part => new() { Subject = Character, Outfit = Outfit, RendererSlot = Mesh };

    private static SubjectModel WarmModel() => new(Character, Outfit, SubjectSource.Prefab,
        new[] { new SubjectPart("body", Mesh, "body-address", Array.Empty<SubjectMaterial>()) },
        Skeleton: null, Problems: Array.Empty<string>());

    [Fact]
    public void Material_evidence_is_shared_within_one_install_and_replaced_with_the_install()
    {
        using var firstRoot = new TempGame();
        using var secondRoot = new TempGame();
        var firstVfs = TestVfs.Create(firstRoot.At("first"),
            Array.Empty<(string Address, string OwnerBundle)>(), null,
            ("first.bundle", new string('1', 32)));
        var secondVfs = TestVfs.Create(secondRoot.At("second"),
            Array.Empty<(string Address, string OwnerBundle)>(), null,
            ("second.bundle", new string('2', 32)));
        var vm = new MainWindowViewModel(startLoad: false);

        var first = vm.MaterialEvidenceFor(firstVfs);

        Assert.Same(first, vm.MaterialEvidenceFor(firstVfs));
        Assert.NotSame(first, vm.MaterialEvidenceFor(secondVfs));
        Assert.Same(vm.MaterialEvidenceFor(secondVfs), vm.MaterialEvidenceFor(secondVfs));
    }

    private sealed class ImmediateProgress : IProgress<string>
    {
        public string Value { get; private set; } = "";
        public void Report(string value) => Value = value;
    }

    private static (CharacterVm Character, OutfitVm Outfit) PickRows()
    {
        var outfit = new Outfit(1, Outfit, OutfitKind.Alt);
        var character = new Character(1, Character, "Vesna", 1, 1, new List<Outfit> { outfit });
        var vm = new CharacterVm(character, (_, _) => { }, (_, _) => { });
        vm.Populate(new[] { (outfit, (IEnumerable<string>)new[] { "body" }) });
        return (vm, Assert.Single(vm.Outfits));
    }

    private static AuthoredEditSession AuthorPart(MainWindowViewModel vm)
    {
        var session = vm.ProjectDocument.Session!;
        session.EnsurePartSlots(Part, part => new LegacyResolvedPart(part,
            new GameAssetRef { GameBuild = "26109", LogicalBundle = "vesna", PathId = 1, Name = Mesh },
            new GameAssetRef { GameBuild = "26109", LogicalBundle = "vesna", PathId = 2, Name = Mesh },
            Array.Empty<LegacyResolvedMaterial>()));
        session.CreateEdit(Part);
        return session;
    }

    /// <summary>Stepping onto ② Edit points the page at the open project's intent. The step is where the
    /// modder arrives from Pick, and a page still holding the previous project's session would be showing
    /// another mod's rows.</summary>
    [Fact]
    public void SteppingOntoEditPointsThePageAtTheOpenProject()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        AuthorPart(vm);

        vm.SelectedStep = "② Edit";

        var subject = Assert.Single(vm.EditPage.Nodes);
        Assert.Equal(Character, subject.Subject);
        Assert.Equal(Outfit, subject.Outfit);
        Assert.True(vm.EditPage.HasNodes);
    }

    /// <summary>A page verb on a mod that has NEVER been saved. The autosave is the app's other route to a
    /// republished projection, and it returns before it gets there while there is no folder to write into —
    /// so without a regeneration of its own the change would be in the model and in nothing every other pane
    /// reads.</summary>
    [Fact]
    public void ASessionMutationOnAModWithNoFolderRedrawsWithoutAProjection()
    {
        using var settings = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false);
        var session = AuthorPart(vm);
        vm.SelectedStep = "② Edit";
        Assert.Null(session.Snapshot().RootDir);

        string hide = session.CreateHideEdit(Part);
        session.PlaceEdit(hide);

        Assert.Contains(hide, session.Snapshot().Always);
        Assert.Equal(EditDefinitionKind.Hide, session.Snapshot().EditDefinitions
            .Single(edit => edit.Id == hide).Kind);
        Assert.Contains(Assert.Single(vm.EditPage.Nodes).Children.Single(node => node.IsPart)
            .Children, node => node.IsHideEdit);
    }

    /// <summary>A refused open is an ordinary outcome — a mod on the older format with no game files to
    /// convert it against says so and stays shut — so the line the open put up has to come back down. Left
    /// standing, "Opening…" describes work that stopped, on a page still showing the mod that was already
    /// open.</summary>
    [Fact]
    public async Task A_refused_open_settles_the_status_line_it_put_up()
    {
        using var temp = new TempGame();
        string dir = temp.At("older-format");
        new ModProject { RootDir = dir, Info = { Name = "Older" } }.Save();
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());
        var before = vm.ProjectDocument;

        Assert.False(await vm.OpenModAsync(dir));

        Assert.Equal("", vm.EditPage.Status);
        Assert.Same(before, vm.ProjectDocument);   // the workspace is exactly as it was
    }

    /// <summary>Whether the tree has drawn a part row yet, read from OUTSIDE the redraw that fills it.
    ///
    /// <para>These tests hand the window a dispatch that runs where it is called, so the redraw the warm
    /// fires runs on that worker: a wait that walks the rows can walk them mid-refill and throw. In the app
    /// the same redraws are marshalled to the UI thread, where the walk is — so the catch is the harness's,
    /// and it answers "not yet", which is what a half-filled tree is.</para></summary>
    private static bool HasPartRow(MainWindowViewModel vm)
    {
        try { return vm.EditPage.Nodes.SelectMany(node => node.Children).Any(node => node.IsPart); }
        catch (InvalidOperationException) { return false; }
    }

    [Fact]
    public async Task Adding_a_cold_subject_reads_its_install_parts_without_reopening()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var vm = new MainWindowViewModel(startLoad: false, subjectModelWarm: (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return WarmModel();
        }, pageDispatch: work => work());
        vm.SelectedStep = "② Edit";
        var rows = PickRows();

        vm.AddSubject(rows.Character, rows.Outfit);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal("0 parts", Assert.Single(vm.EditPage.Nodes).Detail);
        var status = new ImmediateProgress();
        await vm.OpenSubjectInBlenderAsync(Character, Outfit, status);
        Assert.Equal(GameFilesGate.SubjectReading, status.Value);

        release.Set();
        for (int i = 0; i < 200 && !HasPartRow(vm); i++) await Task.Delay(5);

        var part = Assert.Single(Assert.Single(vm.EditPage.Nodes).Children, node => node.IsPart);
        Assert.Equal("body", part.Title);
    }

    [Fact]
    public async Task One_memo_warm_redraws_Edit_and_Build_part_names()
    {
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());
        AuthorPart(vm);
        vm.SelectedStep = "② Edit";
        vm.BuildPage.Enter();
        Assert.Equal(Mesh, Assert.Single(Assert.Single(vm.EditPage.Nodes).Children, node => node.IsPart).Title);
        Assert.Equal(Mesh, Assert.Single(Assert.Single(vm.BuildPage.Subjects).Parts).Label);

        vm.SubjectModels.GetOrBuild(Character, Outfit, WarmModel);
        vm.SubjectModelWarmCompleted();
        for (int i = 0; i < 200 && Assert.Single(Assert.Single(vm.BuildPage.Subjects).Parts).Label != "body"; i++)
            await Task.Delay(5);

        Assert.Equal("body", Assert.Single(Assert.Single(vm.EditPage.Nodes).Children, node => node.IsPart).Title);
        Assert.Equal("body", Assert.Single(Assert.Single(vm.BuildPage.Subjects).Parts).Label);
    }

    // ---- what a card's picture resolves to, through the REAL shell ----
    //
    // A replacement's own output slot is not one shape. Asked what to draw it answers three different
    // things, and the window's loader answered null to all of them until it read the binding: the card then
    // said "No preview" on every map of a fresh replacement, which is every map it has until the modder
    // paints one. These pin the resolution through the window's own loader rather than through a fake.

    private static SubjectModel MappedModel() => new(Character, GoldenOutfit, SubjectSource.Prefab,
        new[]
        {
            new SubjectPart("body", GoldenBody.RendererSlot, "body-address", new[]
            {
                new SubjectMaterial("body_material", 74001, "cab", new[]
                {
                    new SubjectMap("_BaseMap", "body_base", "characters/vesna_ssr01_textures", 81001),
                    new SubjectMap("_BumpMap", "body_normal", "characters/vesna_ssr01_textures", 81003),
                    new SubjectMap("_RampMap", "body_ramp", "characters/vesna_ssr01_textures", 81002),
                    new SubjectMap("_DetailAlbedo", "body_detail", "characters/vesna_ssr01_textures", 81004),
                    new SubjectMap("_DetailMask", "body_detail_mask", "characters/vesna_ssr01_textures", 81005),
                }),
            }),
        },
        Skeleton: null, Problems: Array.Empty<string>());

    private static LegacyResolvedPart MappedResolved(TargetPart part) => new(
        part, Asset(70001, part.RendererSlot), Asset(72001, "body_mesh"),
        new[]
        {
            new LegacyResolvedMaterial(0, "body_material", Asset(74001, "body_material"), new[]
            {
                new LegacyResolvedTexture(TargetInputKind.BaseColor, "textures", "body_base", 81001,
                    Asset(81001, "body_base"), "_BaseMap"),
                new LegacyResolvedTexture(TargetInputKind.Normal, "textures", "body_normal", 81003,
                    Asset(81003, "body_normal"), "_BumpMap"),
                new LegacyResolvedTexture(TargetInputKind.Ramp, "textures", "body_ramp", 81002,
                    Asset(81002, "body_ramp"), "_RampMap"),
                new LegacyResolvedTexture(TargetInputKind.Texture, "textures", "body_detail", 81004,
                    Asset(81004, "body_detail"), "_DetailAlbedo"),
                new LegacyResolvedTexture(TargetInputKind.Texture, "textures", "body_detail_mask", 81005,
                    Asset(81005, "body_detail_mask"), "_DetailMask"),
            }),
        }, MaterialIndexCounts: new[] { 3 });

    private static TargetPart GoldenBody => AuthoredEditFixtures.Body;
    private static string GoldenOutfit => GoldenBody.Outfit;

    /// <summary>The window with one pinned project OPEN — through its own open path, so the page and the
    /// loader are pointed at it the way the app points them — and an install that can name the body's
    /// material and its three maps.</summary>
    private static async Task<(MainWindowViewModel Window, AuthoredEditSession Session)> MappedWindowAsync(
        string root, AuthoredProject project)
    {
        Directory.CreateDirectory(root);
        AuthoredProjectSerializer.Save(project, ModProject.ManifestPathFor(root));
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());
        Assert.True(await vm.OpenModAsync(root));
        vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, MappedModel);
        return (vm, vm.ProjectDocument.Session!);
    }

    /// <summary>An output that keeps the carrier's value draws the part's ORIGINAL map. The shell names that
    /// map, which is what the card stands on and what its thumbnail is read from; before the loader read the
    /// binding, every such card had no name and no picture.</summary>
    [Fact]
    public async Task An_inherited_carrier_output_resolves_to_the_installed_map()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-inherited-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithOwnedSlots());
            session.EnsurePartSlots(GoldenBody, MappedResolved);
            session.RecordReplacementOutputs("edit-long", 1);
            var inherited = session.Slots("edit-long").Single(state =>
                state.Slot.Domain == TargetSlotDomain.EditOutput
                && state.Slot.SubmeshIndex == 0 && state.Slot.Input == TargetInputKind.Normal);
            Assert.Equal(BindingKind.InheritedLiveCarrier, inherited.Binding.Kind);

            string? named = ((IEditPageShell)vm).GameTextureName(new EditSlotRef(
                new EditRef(GoldenBody, "edit-long", "Long body"), inherited.Slot.Id,
                inherited.Slot.Input, inherited.Slot.Domain, inherited.Slot.MaterialSlotIndex, null, null,
                inherited.Binding.Kind, null, GameMaterialSlotIndex: 0,
                ShaderProperty: inherited.Slot.ShaderProperty));

            Assert.Equal("body_normal", named);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>An output answered by a SOURCE slot draws what that slot draws. Naming the part's game ramp
    /// slot — the recorded keep-the-original answer — resolves to the original ramp.</summary>
    [Fact]
    public async Task A_source_slot_output_naming_a_game_slot_resolves_through_it()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-sourceramp-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithOwnedSlots());
            session.EnsurePartSlots(GoldenBody, MappedResolved);
            session.RecordReplacementOutputs("edit-long", 1);
            var output = session.Slots("edit-long").Single(state =>
                state.Slot.Domain == TargetSlotDomain.EditOutput
                && state.Slot.SubmeshIndex == 0 && state.Slot.Input == TargetInputKind.Ramp);
            string gameRamp = session.GameRampSlot(GoldenBody, 0);
            session.ChooseSourceSlot("edit-long", output.Slot.Id, gameRamp);

            string? named = ((IEditPageShell)vm).GameTextureName(new EditSlotRef(
                new EditRef(GoldenBody, "edit-long", "Long body"), output.Slot.Id, output.Slot.Input,
                output.Slot.Domain, output.Slot.MaterialSlotIndex, null, null,
                BindingKind.SourceSlot, new EditSlotSource(null, gameRamp), GameMaterialSlotIndex: 0,
                ShaderProperty: output.Slot.ShaderProperty));

            Assert.Equal("body_ramp", named);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Ordinary_texture_lookup_uses_the_exact_shader_property()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-property-map-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, _) = await MappedWindowAsync(root, AuthoredEditFixtures.Saved());
            var edit = new EditRef(GoldenBody, "edit-long", "Long body");
            var detail = new EditSlotRef(edit, "detail", TargetInputKind.Texture,
                TargetSlotDomain.Game, 0, "body_material", null,
                ShaderProperty: "_DetailAlbedo");
            var mask = detail with { SlotId = "mask", ShaderProperty = "_DetailMask" };

            Assert.Equal("body_detail", ((IEditPageShell)vm).GameTextureName(detail));
            Assert.Equal("body_detail_mask", ((IEditPageShell)vm).GameTextureName(mask));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>A source answer that lands on the mod's own file draws THAT file. The loader answers with a
    /// picture rather than with nothing, which is what a card reading "No preview" over a picture the modder
    /// bound was.</summary>
    [Fact]
    public async Task A_source_slot_output_naming_an_authored_file_reads_that_file()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-sourcefile-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithOwnedSlots());
            Directory.CreateDirectory(Path.Combine(root, "textures"));
            File.WriteAllBytes(Path.Combine(root, "textures", "skin.png"), new byte[] { 1, 2, 3 });
            var borrowing = session.Slots("edit-long").Single(state =>
                state.Slot.Id == "slot-owned-2");
            Assert.Equal(BindingKind.SourceSlot, borrowing.Binding.Kind);

            var preview = await ((IEditPageShell)vm).LoadMapPreviewAsync(new EditSlotRef(
                new EditRef(GoldenBody, "edit-long", "Long body"), borrowing.Slot.Id, borrowing.Slot.Input,
                borrowing.Slot.Domain, borrowing.Slot.MaterialSlotIndex, null, null,
                BindingKind.SourceSlot,
                new EditSlotSource("edit-long", borrowing.Binding.SourceSlot!.SlotId),
                GameMaterialSlotIndex: 0));

            // A slot the loader cannot resolve answers null outright; this one answered with the file.
            Assert.NotNull(preview);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ---- the shared-stock capability boundary, through the window's own shell ----
    //
    // The build rebinds a stock texture by its resource identity, so a picture bound at one material
    // position lands on every position of the item that draws that texture — a second position of the SAME
    // part included. What counts as shared is therefore counted here, off the subject model, which is why
    // these drive the real shell rather than a number a fake handed back.

    private const string MapBundle = "characters/vesna_ssr01_textures";

    /// <summary>One material position binding one base map, pinned by the path id the build rebinds on.</summary>
    private static SubjectMaterial Position(string name, long materialPathId, long texturePathId) =>
        new(name, materialPathId, "cab",
            new[] { new SubjectMap("_BaseMap", "tex" + texturePathId, MapBundle, texturePathId) });

    private static SubjectPart InstallPart(TargetPart part, params SubjectMaterial[] positions) =>
        new(part.RendererSlot, part.RendererSlot, part.RendererSlot + "-address", positions);

    private static SubjectModel Install(params SubjectPart[] parts) =>
        new(Character, GoldenOutfit, SubjectSource.Prefab, parts, Skeleton: null,
            Problems: Array.Empty<string>());

    /// <summary>One texture (81001) drawn at BOTH of one part's material positions — the shape the
    /// count-the-parts gate read as private, since it is one part.</summary>
    private static SubjectModel TwoPositionsOfOnePart() => Install(InstallPart(GoldenBody,
        Position("body_material", 74001, 81001), Position("body_material_1", 74002, 81001)));

    /// <summary>The same texture drawn by two different parts.</summary>
    private static SubjectModel TwoParts() => Install(
        InstallPart(GoldenBody, Position("body_material", 74001, 81001)),
        InstallPart(AuthoredEditFixtures.Hair, Position("hair_material", 74003, 81001)));

    /// <summary>The same texture at three position-grain uses, for the consent ceiling.</summary>
    private static SubjectModel ThreePlaces() => Install(
        InstallPart(GoldenBody, Position("body_material", 74001, 81001),
            Position("body_material_1", 74002, 81001)),
        InstallPart(AuthoredEditFixtures.Hair, Position("hair_material", 74003, 81001)));

    /// <summary>One use of 81001 anywhere on the item.</summary>
    private static SubjectModel OneUse() => Install(InstallPart(GoldenBody,
        Position("body_material", 74001, 81001), Position("body_material_1", 74002, 81002)));

    private static GameAssetRef Asset(long pathId, string name) => new()
    {
        GameBuild = "26109", LogicalBundle = "characters/vesna_ssr01", PathId = pathId, Name = name,
    };

    /// <summary>What the install hands back for the body: two material positions, each with a base colour,
    /// so the project holds a slot at each of the positions the models above bind.</summary>
    private static LegacyResolvedPart InstalledBody(TargetPart part) => new(
        part, Asset(70001, part.RendererSlot), Asset(72001, "body_mesh"),
        new[]
        {
            new LegacyResolvedMaterial(0, "body_material", Asset(74001, "body_material"), new[]
            {
                new LegacyResolvedTexture(TargetInputKind.BaseColor, "characters/vesna_ssr01_textures",
                    "tex81001", null, Asset(81001, "tex81001")),
            }),
            new LegacyResolvedMaterial(1, "body_material_1", Asset(74002, "body_material_1"), new[]
            {
                new LegacyResolvedTexture(TargetInputKind.BaseColor, "characters/vesna_ssr01_textures",
                    "tex81001", null, Asset(81001, "tex81001")),
            }),
        },
        MaterialIndexCounts: new[] { 1, 1 });

    /// <summary>The mod these tests open. Its folder is NAMED for it, so the app's own rename-the-folder-to-
    /// match-the-name autosave has nothing to do and the root the test writes into stays the root.</summary>
    private const string BoundaryModName = "shared stock probe";

    /// <summary>A mod open on the body part with one edit on it, and nothing read for the subject yet.</summary>
    /// <param name="warm">What the install answers for a subject's model. Null hands the window NO warm
    /// delegate, which is the production shape — the models come from the scan itself, and a machine with a
    /// game spends the whole of its first scan, and of every rescan, in exactly that state.</param>
    /// <param name="scanning">Whether the app is still reading the game. True is the app's own starting
    /// value; false is a finished app, where a subject with no model is one the read never attempted or
    /// ended without.</param>
    private static async Task<MainWindowViewModel> BoundaryWindowAsync(string root,
        Func<string, string, SubjectModel?>? warm = null, bool scanning = true, bool createEdit = true)
    {
        Directory.CreateDirectory(root);
        // The slots and the edit are in the project BEFORE the open, so the page is drawn once, from the
        // whole thing, rather than racing the autosave a mutation after the open would fire.
        var authored = new AuthoredEditSession(AuthoredEditFixtures.SlotsOnly());
        authored.EnsurePartSlots(GoldenBody, InstalledBody);
        if (createEdit) authored.CreateEdit(GoldenBody);
        var project = authored.Snapshot();
        project.Info.Name = BoundaryModName;
        AuthoredProjectSerializer.Save(project, ModProject.ManifestPathFor(root));
        var vm = new MainWindowViewModel(startLoad: false, subjectModelWarm: warm,
            pageDispatch: work => work()) { IsScanning = scanning };
        Assert.True(await vm.OpenModAsync(root));
        vm.SelectedStep = "② Edit";
        return vm;
    }

    /// <summary>The edit's base-colour card at the part's first material position, as the page drew it.
    ///
    /// <para>Waited for, and read in ONE attempt that either finds the card or does not: a redraw empties the
    /// rows before it refills them, and these tests hand the window a dispatch that runs where it is called,
    /// so a redraw the open's own background work fires runs on that worker — between two reads of the tree
    /// as easily as before either. In the app those redraws are marshalled to the UI thread, where the read
    /// is, so the retry is the harness's own and nothing it hides can happen there.</para></summary>
    private static async Task<EditMapCardVm> BaseColorCardAsync(MainWindowViewModel vm)
    {
        for (int i = 0; i < 200; i++)
        {
            if (BaseColorCardOrNull(vm) is { } drawn) return drawn;
            await Task.Delay(5);
        }
        return BaseColorCardOrNull(vm)
            ?? throw new InvalidOperationException("the page never drew the body's base-colour card");
    }

    private static EditMapCardVm? BaseColorCardOrNull(MainWindowViewModel vm)
    {
        try
        {
            var part = vm.EditPage.Nodes.FirstOrDefault()?.Children
                .FirstOrDefault(node => node.IsPart && node.Part!.SameAs(GoldenBody));
            return (part?.Children.FirstOrDefault()?.MapGroups ?? part?.MapGroups)?
                .SelectMany(group => group.Cards)
                .FirstOrDefault(card => card.Slot.Input == TargetInputKind.BaseColor
                    && card.Slot.MaterialSlotIndex == 0);
        }
        catch (InvalidOperationException) { return null; }   // a redraw is refilling the rows
    }

    private static EditSlotRef BareSlot(AuthoredEditSession session, TargetInputKind input)
    {
        var state = session.Snapshot().TargetSlots.First(slot => slot.Part.SameAs(GoldenBody)
            && slot.Domain == TargetSlotDomain.Game && slot.Input == input
            && (slot.MaterialSlotIndex ?? slot.SubmeshIndex) == 0);
        return new EditSlotRef(new EditRef(GoldenBody, "", ""), "", state.Input, state.Domain,
            state.MaterialSlotIndex, state.Material?.Name, null, ShaderProperty: state.ShaderProperty);
    }

    private static async Task AssertAllPictureGesturesRefusedAsync(MainWindowViewModel vm, string root,
        string expected)
    {
        var card = await BaseColorCardAsync(vm);
        string png = TestImages.WritePng(Path.Combine(root, "in", "boundary.png"));
        var status = new ImmediateProgress();

        Assert.True(vm.EditPage.CanAcceptDrop(card));
        Assert.False(card.CanOpenUvGuide);
        Assert.Equal(expected, card.UvHint);
        await vm.EditPage.HandleDropAsync(new[] { png }, card);
        Assert.Equal(expected, vm.EditPage.Status);

        Assert.Null(await ((IEditPageShell)vm).AcceptDroppedPictureAsync(card.Slot, png, status,
            confirmed: true));
        Assert.Equal(expected, status.Value);
        Assert.False((await ((IEditPageShell)vm).OpenPictureAsync(card.Slot, status)).Launched);
        Assert.Equal(expected, status.Value);

        var session = vm.ProjectDocument.Session!;
        var transport = ProjectAssetIngress.Begin(session.Snapshot(),
            card.Slot.Edit.EditDefinitionId, card.Slot.SlotId, png);
        var ingress = new MainWindowViewModel.PictureIngress(card.Slot, session, transport, "boundary",
            null);
        vm.PublishPictureReturn(ingress, status);
        Assert.Equal(MainWindowViewModel.PictureSaveGateRefusal(card.Slot, expected), status.Value);
        Assert.DoesNotContain(session.Snapshot().ProjectAssets,
            asset => asset.Kind == ProjectAssetKind.Picture);
    }

    private static async Task<T> InTempMod<T>(string name, Func<string, Task<T>> work)
    {
        string parent = Path.Combine(Path.GetTempPath(), name + Guid.NewGuid().ToString("N"));
        try { return await work(Path.Combine(parent, ModNaming.Slug(BoundaryModName))); }
        finally { try { Directory.Delete(parent, recursive: true); } catch { } }
    }

    [Fact]
    public void The_bare_part_uv_route_never_looks_up_its_empty_edit_id()
    {
        using var temp = new TempGame();
        var project = new AuthoredProject { RootDir = temp.Root };
        var slot = new EditSlotRef(new EditRef(GoldenBody, "", ""), "",
            TargetInputKind.BaseColor, TargetSlotDomain.Game, 3, "body_material", null);

        var route = MainWindowViewModel.UvGuideRouteFor(slot,
            new AuthoredEditSession(project), project);

        Assert.Equal(3, route.Submesh);
        Assert.Null(route.ModdedGlb);
        Assert.Null(route.MissingGeometry);
    }

    [Fact]
    public void The_OpenUvGuide_route_for_a_ProjectAsset_output_uses_its_picture_and_edit_glb()
    {
        using var temp = new TempGame();
        var project = AuthoredEditFixtures.Golden();
        project.RootDir = temp.Root;
        string mesh = Path.Combine(temp.Root, "meshes", "long.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(mesh)!);
        File.WriteAllBytes(mesh, new byte[] { 1, 2, 3 });
        string picture = TestImages.WritePng(Path.Combine(temp.Root, "textures", "effect.png"), 37, 19);
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "guide-picture", Kind = ProjectAssetKind.Picture, Label = "Effect",
            File = Path.GetRelativePath(temp.Root, picture),
        });
        var session = new AuthoredEditSession(project);
        session.EnsurePartSlots(GoldenBody, InstalledBody);
        session.RecordReplacementOutputs("edit-long", 1);
        var output = session.Slots("edit-long").Single(state =>
            state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.Input == TargetInputKind.BaseColor);
        session.ChooseProjectAsset("edit-long", output.Slot.Id, "guide-picture");
        output = session.Slots("edit-long").Single(state => state.Slot.Id == output.Slot.Id);
        var slot = new EditSlotRef(new EditRef(GoldenBody, "edit-long", "Long body"), output.Slot.Id,
            output.Slot.Input, output.Slot.Domain, output.Slot.MaterialSlotIndex, null,
            output.ProjectAsset!.File, output.Binding.Kind, GameMaterialSlotIndex: 0,
            SubmeshIndex: output.Slot.SubmeshIndex);

        var route = MainWindowViewModel.UvGuideRouteFor(slot, session, session.Snapshot());

        Assert.Equal(0, route.Submesh);
        Assert.Equal(mesh, route.ModdedGlb);
        Assert.Null(route.MissingGeometry);
        Assert.Equal("effect.png", route.ReplacementName);
        Assert.Equal("effect.png", route.GuideSource);
        Assert.Equal((37, 19), route.CanvasSize);
    }

    /// <summary>A texture ONE part draws at TWO of its material positions is shared. The build binds a
    /// picture by the texture's identity, so an edit made at position 0 repaints position 1 too — the card
    /// promised one map and delivered two, with nothing on screen saying so.</summary>
    [Fact]
    public async Task A_texture_two_of_one_parts_positions_draw_is_shared_at_position_grain()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-onepart-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, TwoPositionsOfOnePart);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);

            // Two positions bind 81001 in the fixture above; the old gate counted the one PART and let it by.
            Assert.Equal(2, ((IEditPageShell)vm).TextureUses(card.Slot));
            Assert.Equal(EditTextureSharing.Shared, card.Sharing);
            Assert.Equal(2, card.SharingUses);
            Assert.True(card.CanOpen);
            Assert.True(vm.EditPage.CanAcceptDrop(card));
            return 0;
        });
    }

    /// <summary>The same texture drawn by two different parts: the boundary's original case, unchanged.</summary>
    [Fact]
    public async Task A_texture_two_parts_draw_is_shared_and_actionable_with_consent()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-twoparts-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, TwoParts);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);

            Assert.Equal(2, ((IEditPageShell)vm).TextureUses(card.Slot));
            Assert.Equal(EditTextureSharing.Shared, card.Sharing);
            Assert.True(vm.EditPage.CanAcceptDrop(card));
            return 0;
        });
    }

    /// <summary>A texture with ONE use on the item is one exact place, and the picture lands: the boundary
    /// is drawn at sharing rather than at stock textures, and the publish route agrees with the card.</summary>
    [Fact]
    public async Task A_texture_with_one_use_takes_the_picture_through_the_publish_route()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-private-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            Assert.Equal(1, ((IEditPageShell)vm).TextureUses(card.Slot));
            Assert.Equal(EditTextureSharing.Private, card.Sharing);
            Assert.True(vm.EditPage.CanAcceptDrop(card));

            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            var status = new ImmediateProgress();
            var published = await ((IEditPageShell)vm).AcceptDroppedPictureAsync(card.Slot, png, status,
                confirmed: true);

            Assert.NotNull(published);
            return 0;
        });
    }

    [Fact]
    public async Task A_bare_drop_mints_and_binds_in_one_session_change()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-bare-drop-", async root =>
        {
            var vm = await BoundaryWindowAsync(root, createEdit: false);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            var session = vm.ProjectDocument.Session!;
            var slot = BareSlot(session, TargetInputKind.BaseColor);
            Assert.Empty(session.Snapshot().EditDefinitions);
            int changes = 0;
            session.Changed += (_, _) => changes++;
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));

            var result = await ((IEditPageShell)vm).AcceptDroppedPictureAsync(slot, png,
                new ImmediateProgress(), confirmed: true);

            var edit = Assert.Single(session.Snapshot().EditDefinitions);
            Assert.Equal(1, changes);
            Assert.True(File.Exists(png));
            Assert.Equal(edit.Id, result!.Target!.Edit.EditDefinitionId);
            Assert.Equal(BindingKind.ProjectAsset, session.Slots(edit.Id).Single(state =>
                state.Slot.Id == result.Target.SlotId).Binding.Kind);
            return 0;
        });
    }

    [Fact]
    public async Task A_bare_picture_open_mints_nothing_until_its_first_save()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-bare-open-", async root =>
        {
            var vm = await BoundaryWindowAsync(root, createEdit: false);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            var session = vm.ProjectDocument.Session!;
            var slot = BareSlot(session, TargetInputKind.BaseColor);
            MainWindowViewModel.PictureIngress? ingress = null;
            vm.PictureIngressOpenedForTests = opened => ingress = opened;
            vm.LaunchImageEditorForTests = _ => true;
            vm.ExportGamePictureForTests = destination => TestImages.WritePng(destination);

            var status = new ImmediateProgress();
            var opened = await ((IEditPageShell)vm).OpenPictureAsync(slot, status);

            Assert.True(opened.Launched, status.Value);
            Assert.NotNull(ingress);
            Assert.Empty(session.Snapshot().EditDefinitions);
            int changes = 0;
            session.Changed += (_, _) => changes++;
            TestImages.WritePng(ingress!.Session.OutboundSnapshot, g: 77);
            vm.PublishPictureReturn(ingress, new ImmediateProgress());
            Assert.Single(session.Snapshot().EditDefinitions);
            Assert.Equal(1, changes);
            Assert.True(File.Exists(ingress.Session.OutboundSnapshot));

            TestImages.WritePng(ingress.Session.OutboundSnapshot, g: 88);
            vm.PublishPictureReturn(ingress, new ImmediateProgress());
            TestImages.WritePng(ingress.Session.OutboundSnapshot, g: 99);
            vm.PublishPictureReturn(ingress, new ImmediateProgress());

            string runDirectory = Path.GetDirectoryName(ingress.Session.ReturnArtifact)!;
            string slotDirectory = Directory.GetParent(runDirectory)!.FullName;
            Assert.Single(Directory.GetDirectories(slotDirectory));
            Assert.Equal(3, changes);
            return 0;
        });
    }

    [Fact]
    public async Task A_stale_bare_drop_reports_its_first_edit_refusal_without_an_image_decode_wrapper()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-bare-drop-stale-", async root =>
        {
            var vm = await BoundaryWindowAsync(root, createEdit: false);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            var session = vm.ProjectDocument.Session!;
            var stale = BareSlot(session, TargetInputKind.BaseColor) with { ShaderProperty = "_RemovedMap" };
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            var status = new ImmediateProgress();

            Assert.Null(await ((IEditPageShell)vm).AcceptDroppedPictureAsync(stale, png, status,
                confirmed: true));

            Assert.Equal("This map is no longer in the game files.", status.Value);
            Assert.Empty(session.Snapshot().EditDefinitions);
            return 0;
        });
    }

    [Fact]
    public async Task A_bare_ramp_apply_mints_and_binds_in_one_session_change()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-bare-ramp-", async root =>
        {
            var vm = await BoundaryWindowAsync(root, createEdit: false);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            var session = vm.ProjectDocument.Session!;
            var slot = BareSlot(session, TargetInputKind.Ramp);
            int changes = 0;
            session.Changed += (_, _) => changes++;
            string ramp = Path.Combine(root, "in", "ramp.dds");
            Directory.CreateDirectory(Path.GetDirectoryName(ramp)!);
            File.WriteAllBytes(ramp, new byte[] { 1, 2, 3, 4 });

            var result = vm.PublishFirstEditAsset(session, slot, ramp, ProjectAssetKind.Ramp,
                "Ramp", ProjectAssetIngress.Binary, null);

            var edit = Assert.Single(session.Snapshot().EditDefinitions);
            Assert.Equal(1, changes);
            Assert.True(File.Exists(ramp));
            Assert.Equal(edit.Id, result.Target!.Edit.EditDefinitionId);
            Assert.Equal(BindingKind.ProjectAsset, session.Slots(edit.Id).Single(state =>
                state.Slot.Id == result.Target.SlotId).Binding.Kind);
            return 0;
        });
    }

    /// <summary>The first-edit route's confirmed flag never invents shared consent from the live read. It
    /// carries the card snapshot the dialog described; if that offer did not say Shared, a later shared live
    /// answer refuses. A matching shared offer proceeds.</summary>
    [Fact]
    public async Task A_confirmed_drop_grants_only_the_shared_consent_its_card_snapshot_offered()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-race-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            var cold = await BaseColorCardAsync(vm);
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            var status = new ImmediateProgress();

            Assert.Null(((IEditPageShell)vm).TextureUses(cold.Slot));
            Assert.Equal(EditTextureSharing.Unknown, cold.Sharing);
            Assert.False(cold.CanOpen);
            Assert.True(vm.EditPage.CanAcceptDrop(cold));
            Assert.Equal(GameFilesGate.SubjectReading, cold.DropHint);
            Assert.Null(await ((IEditPageShell)vm).AcceptDroppedPictureAsync(cold.Slot, png, status,
                confirmed: true));
            Assert.Equal(GameFilesGate.SubjectReading, status.Value);

            // The read lands between the question and bind. The cold card did not offer shared consent, so
            // confirmed alone cannot bless the live shared answer.
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, TwoPositionsOfOnePart);

            Assert.Null(await ((IEditPageShell)vm).AcceptDroppedPictureAsync(cold.Slot, png, status,
                confirmed: true, offered: new EditTextureSharingOffer(cold.Sharing, cold.SharingUses)));
            Assert.Equal(EditMapCardVm.SharedConsentRequired(2), status.Value);
            Assert.False((await ((IEditPageShell)vm).OpenPictureAsync(cold.Slot, status,
                confirmed: true, offered: new EditTextureSharingOffer(cold.Sharing, cold.SharingUses))).Launched);
            Assert.Equal(EditMapCardVm.SharedConsentRequired(2), status.Value);
            Assert.DoesNotContain(vm.ProjectDocument.Session!.Snapshot().ProjectAssets,
                asset => asset.Kind == ProjectAssetKind.Picture);

            vm.EditPage.Rebuild();
            var shared = await BaseColorCardAsync(vm);
            Assert.Equal(EditTextureSharing.Shared, shared.Sharing);
            vm.SubjectModels.Clear();
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, ThreePlaces);
            Assert.Null(await ((IEditPageShell)vm).AcceptDroppedPictureAsync(shared.Slot, png, status,
                confirmed: true, offered: new EditTextureSharingOffer(shared.Sharing, shared.SharingUses)));
            Assert.Equal(EditMapCardVm.SharedConsentRequired(3), status.Value);
            Assert.False((await ((IEditPageShell)vm).OpenPictureAsync(shared.Slot, status,
                confirmed: true, offered: new EditTextureSharingOffer(shared.Sharing, shared.SharingUses))).Launched);
            Assert.Equal(EditMapCardVm.SharedConsentRequired(3), status.Value);

            vm.EditPage.Rebuild();
            shared = await BaseColorCardAsync(vm);
            Assert.NotNull(await ((IEditPageShell)vm).AcceptDroppedPictureAsync(shared.Slot, png, status,
                confirmed: true, offered: new EditTextureSharingOffer(shared.Sharing, shared.SharingUses)));
            Assert.Contains(vm.ProjectDocument.Session!.Snapshot().ProjectAssets,
                asset => asset.Kind == ProjectAssetKind.Picture);
            return 0;
        });
    }

    /// <summary>The app's own scan is one of the states with no answer. The tree turns interactive at the
    /// end of the load's first phase and the forward view lands in the second, so on a machine WITH a game
    /// there is a stretch — all of the first run, and all of every rescan — where nothing is in hand and
    /// nothing is coming yet. Read as "no install", that stretch answers zero uses, which is private, which
    /// takes the picture: the card offers the drop, the publish route agrees, and the mod ends up owning a
    /// texture the whole item draws.
    ///
    /// <para>The second half is what that reading was protecting, and it still holds: an app that has
    /// FINISHED with no install at all counts nothing, hides nothing, and lets the modder work.</para>
    /// </summary>
    [Fact]
    public async Task The_scan_before_the_install_lands_is_not_an_app_with_no_install()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-scanning-", async root =>
        {
            // No warm delegate and no forward view: the production shape, mid-scan.
            var scanning = await BoundaryWindowAsync(root);
            var card = await BaseColorCardAsync(scanning);
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            var status = new ImmediateProgress();

            Assert.True(scanning.IsScanning);
            Assert.Equal(EditTextureSharing.Unknown, card.Sharing);
            Assert.False(card.CanOpen);
            Assert.True(scanning.EditPage.CanAcceptDrop(card));
            Assert.False(card.CanOpenUvGuide);
            Assert.Equal(GameFilesGate.SubjectReading, card.UvHint);
            Assert.Null(await ((IEditPageShell)scanning).AcceptDroppedPictureAsync(card.Slot, png, status,
                confirmed: true));
            Assert.Equal(GameFilesGate.SubjectReading, status.Value);
            return 0;
        });
        await InTempMod("remold-uses-noinstall-", async root =>
        {
            var offline = await BoundaryWindowAsync(root, scanning: false);
            var card = await BaseColorCardAsync(offline);

            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            var status = new ImmediateProgress();
            Assert.Equal(EditTextureSharing.Unavailable, card.Sharing);
            Assert.False(card.CanOpen);
            Assert.True(offline.EditPage.CanAcceptDrop(card));
            Assert.Equal(GameFilesGate.Unavailable, card.OpenHint);
            Assert.False(card.CanOpenUvGuide);
            Assert.Equal(GameFilesGate.Unavailable, card.UvHint);
            Assert.Null(await ((IEditPageShell)offline).AcceptDroppedPictureAsync(card.Slot, png, status,
                confirmed: true));
            Assert.Equal(GameFilesGate.Unavailable, status.Value);
            return 0;
        });
    }

    /// <summary>An item the install will never answer for — the roster does not carry it, or its files could
    /// not be read. The read is not retried inside one forward view, so "still being read" on those cards
    /// was a promise the app could not keep for as long as it stayed open. The card says what is true
    /// instead, and so does the verb that needs the same model; a re-read of the game is what changes the
    /// answer, and when it does the card goes back to the ordinary boundary.</summary>
    [Fact]
    public async Task An_item_the_read_finished_without_says_so_instead_of_promising_a_wait()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-unreadable-", async root =>
        {
            // The install is there and answers nothing for this subject, and the app is not scanning: the
            // warm pass runs, fails, and nothing after it will try again.
            var vm = await BoundaryWindowAsync(root, warm: (_, _) => null, scanning: false);
            for (int i = 0; i < 200 && !vm.SubjectModels.IsUnreadable(Character, GoldenOutfit); i++)
                await Task.Delay(5);
            Assert.True(vm.SubjectModels.IsUnreadable(Character, GoldenOutfit));
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            var status = new ImmediateProgress();

            Assert.Equal(EditTextureSharing.Unreadable, card.Sharing);
            Assert.NotEqual(GameFilesGate.SubjectReading, card.DropHint);
            Assert.Equal(GameFilesGate.SubjectUnreadable, card.DropHint);
            Assert.Equal(GameFilesGate.SubjectUnreadable, card.OpenHint);
            Assert.True(vm.EditPage.CanAcceptDrop(card));
            Assert.False(card.CanOpenUvGuide);
            Assert.Equal(GameFilesGate.SubjectUnreadable, card.UvHint);
            Assert.Null(await ((IEditPageShell)vm).AcceptDroppedPictureAsync(card.Slot, png, status,
                confirmed: true));
            Assert.Equal(GameFilesGate.SubjectUnreadable, status.Value);

            // The same state on the verb that needs the same model, which said "game files unavailable" over
            // a game the app had already found.
            await ((IEditPageShell)vm).OpenSubjectInBlenderAsync(Character, GoldenOutfit, status);
            Assert.Equal(GameFilesGate.SubjectUnreadable, status.Value);

            // A re-read lands the model: the item is ordinary again, counted rather than refused.
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            vm.EditPage.Rebuild();
            var read = await BaseColorCardAsync(vm);

            Assert.Equal(EditTextureSharing.Private, read.Sharing);
            Assert.True(vm.EditPage.CanAcceptDrop(read));
            return 0;
        });
    }

    /// <summary>Opening the image editor on a shared texture is refused on the ROUTE, not only on the card.
    /// The open is what materializes the game's texture into the mod on first touch, so a click that got
    /// past the card — a stale card, a rescan under it — must find the same answer at the shell: nothing is
    /// prepared, no editor is handed anything, and the sentence lands on the status line the verb owns.</summary>
    [Fact]
    public async Task The_editor_open_asks_for_shared_consent_before_preparing_anything()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-open-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, TwoParts);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            var status = new ImmediateProgress();
            string? title = null, body = null, button = null;
            vm.ConfirmForTests = (t, b, c, _) =>
            {
                title = t; body = b; button = c;
                return Task.FromResult(false);
            };

            Assert.False((await ((IEditPageShell)vm).OpenPictureAsync(card.Slot, status)).Launched);

            Assert.Equal("Edit this map?", title);
            Assert.Equal($"Base color on {card.Slot.MaterialName}.\n\n"
                + "This outfit draws this original map in 2 places. The edit changes all of them.", body);
            Assert.Equal("Edit", button);
            title = body = button = null;
            Assert.False((await ((IEditPageShell)vm).OpenPictureAsync(
                card.Slot with { MaterialName = null }, status)).Launched);
            Assert.Equal("Edit this map?", title);
            Assert.Equal("Base color on material 0.\n\n"
                + "This outfit draws this original map in 2 places. The edit changes all of them.", body);
            Assert.Equal("Edit", button);
            // Nothing was opened: a transport would have minted the ingress folder under the mod.
            Assert.False(Directory.Exists(Path.Combine(root, ProjectAssetIngress.DirectoryName)));
            Assert.DoesNotContain(vm.ProjectDocument.Session!.Snapshot().ProjectAssets,
                asset => asset.Kind == ProjectAssetKind.Picture);
            return 0;
        });
    }

    [Fact]
    public async Task All_three_unmeasured_states_refuse_drop_open_and_save_back_with_their_gate_sentence()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-gate-reading-", async root =>
        {
            var vm = await BoundaryWindowAsync(root, scanning: true);
            await AssertAllPictureGesturesRefusedAsync(vm, root, GameFilesGate.SubjectReading);
            return 0;
        });
        await InTempMod("remold-gate-unavailable-", async root =>
        {
            var vm = await BoundaryWindowAsync(root, scanning: false);
            await AssertAllPictureGesturesRefusedAsync(vm, root, GameFilesGate.Unavailable);
            return 0;
        });
        await InTempMod("remold-gate-unreadable-", async root =>
        {
            var vm = await BoundaryWindowAsync(root, warm: (_, _) => null, scanning: false);
            for (int i = 0; i < 200 && !vm.SubjectModels.IsUnreadable(Character, GoldenOutfit); i++)
                await Task.Delay(5);
            Assert.True(vm.SubjectModels.IsUnreadable(Character, GoldenOutfit));
            await AssertAllPictureGesturesRefusedAsync(vm, root, GameFilesGate.SubjectUnreadable);
            return 0;
        });
    }

    [Fact]
    public async Task Removing_the_install_does_not_let_a_cached_measurement_keep_answering_private()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-gate-stale-cache-", async root =>
        {
            var vm = await BoundaryWindowAsync(root, scanning: true);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            vm.EditPage.Rebuild();
            Assert.Equal(EditTextureSharing.Private, (await BaseColorCardAsync(vm)).Sharing);

            vm.IsScanning = false;
            vm.EditPage.Rebuild();
            var offline = await BaseColorCardAsync(vm);

            Assert.Equal(EditTextureSharing.Unavailable, offline.Sharing);
            Assert.Equal(GameFilesGate.Unavailable, offline.OpenHint);
            Assert.True(vm.EditPage.CanAcceptDrop(offline));
            Assert.False(offline.CanOpenUvGuide);
            Assert.Equal(GameFilesGate.Unavailable, offline.UvHint);
            return 0;
        });
    }

    [Fact]
    public async Task A_shared_drop_asks_once_and_later_drops_on_the_bound_picture_do_not_repeat_the_reach()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-drop-consent-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, TwoParts);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            string? title = null, body = null, button = null;
            bool accept = false;
            vm.ConfirmForTests = (t, b, c, _) =>
            {
                title = t; body = b; button = c;
                return Task.FromResult(accept);
            };

            await vm.EditPage.HandleDropAsync(new[] { png }, card);
            Assert.Equal("Apply paint.png?", title);
            Assert.Equal($"paint.png becomes this edit's base color on {card.Slot.MaterialName}.\n\n"
                + "This outfit draws this original map in 2 places. The edit changes all of them.", body);
            Assert.Equal("Apply", button);
            Assert.DoesNotContain(vm.ProjectDocument.Session!.Snapshot().ProjectAssets,
                asset => asset.Kind == ProjectAssetKind.Picture);

            accept = true;
            await vm.EditPage.HandleDropAsync(new[] { png }, card);
            Assert.Contains(vm.ProjectDocument.Session!.Snapshot().ProjectAssets,
                asset => asset.Kind == ProjectAssetKind.Picture);

            vm.EditPage.Rebuild();
            var bound = await BaseColorCardAsync(vm);
            Assert.Equal(BindingKind.ProjectAsset, bound.Binding);
            string later = TestImages.WritePng(Path.Combine(root, "in", "later.png"), g: 77);
            body = null;
            await vm.EditPage.HandleDropAsync(new[] { later }, bound);
            Assert.Equal($"later.png becomes this edit's base color on {bound.Slot.MaterialName}.", body);
            Assert.DoesNotContain("places", body);
            return 0;
        });
    }

    /// <summary>The first save changes the session slot from the game's value to the mod's picture. A later
    /// save classifies that live binding, not the transport's launch record, so losing the install cannot
    /// strand an editor on a slot the mod already owns.</summary>
    [Fact]
    public async Task A_second_editor_save_uses_the_live_project_binding_after_the_install_disappears()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-saveback-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            var session = vm.ProjectDocument.Session!;
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            // The transport the open hands the image editor, opened on the same slot the card carries.
            var transport = ProjectAssetIngress.Begin(session.Snapshot(),
                card.Slot.Edit.EditDefinitionId, card.Slot.SlotId, png);
            var ingress = new MainWindowViewModel.PictureIngress(card.Slot, session, transport, "paint",
                null);
            var status = new ImmediateProgress();

            // One use: the save lands, exactly as it did before the check was added — and the line says
            // WHERE it landed, the way the drop's own result line does: the edit, the map, the material.
            vm.PublishPictureReturn(ingress, status);
            Assert.Contains("Saved", status.Value);
            Assert.Contains(card.Slot.Edit.Label, status.Value);
            Assert.Contains(card.Slot.MaterialName!, status.Value);
            Assert.DoesNotContain("this map", status.Value);
            int landed = session.Snapshot().ProjectAssets.Count(
                asset => asset.Kind == ProjectAssetKind.Picture);

            // The install disappears while the editor remains open. The captured card still says
            // TargetGameValue, but the session slot now says ProjectAsset.
            vm.SubjectModels.Clear();
            vm.IsScanning = false;
            TestImages.WritePng(transport.OutboundSnapshot, r: 0, g: 255);

            vm.PublishPictureReturn(ingress, status);

            Assert.StartsWith("Saved ", status.Value);
            Assert.DoesNotContain(GameFilesGate.Unavailable, status.Value);
            Assert.Equal(landed + 1, session.Snapshot().ProjectAssets.Count(
                asset => asset.Kind == ProjectAssetKind.Picture));
            return 0;
        });
    }

    [Fact]
    public async Task Reopening_an_owned_picture_preserves_its_friendly_label_on_save_back()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-picture-label-", async root =>
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithOwnedSlots());
            var state = session.Slots("edit-long").Single(candidate =>
                candidate.Slot.Id == "slot-owned");
            string canonical = Path.Combine(root, state.ProjectAsset!.File);
            Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
            TestImages.WritePng(canonical);
            var slot = new EditSlotRef(new EditRef(GoldenBody, "edit-long", "Long body"),
                state.Slot.Id, state.Slot.Input, state.Slot.Domain, state.Slot.MaterialSlotIndex,
                state.Slot.Material?.Name, state.ProjectAsset.File, state.Binding.Kind,
                SubmeshIndex: state.Slot.SubmeshIndex, ShaderProperty: state.Slot.ShaderProperty);

            string label = MainWindowViewModel.PictureIngressLabel(session.Snapshot(), slot, supplied: null);
            var transport = ProjectAssetIngress.Begin(session.Snapshot(),
                slot.Edit.EditDefinitionId, slot.SlotId);
            TestImages.WritePng(transport.OutboundSnapshot, g: 77);
            var ingress = new MainWindowViewModel.PictureIngress(slot, session, transport, label, null);

            var status = new ImmediateProgress();
            vm.PublishPictureReturn(ingress, status);

            var rebound = session.Slots("edit-long").Single(candidate =>
                candidate.Slot.Id == "slot-owned").ProjectAsset!;
            Assert.Equal("Skin", label);
            Assert.Equal("Skin", rebound.Label);
            Assert.StartsWith("asset-", Path.GetFileNameWithoutExtension(rebound.File));
            Assert.Equal("Saved Skin to Long body's base color.", status.Value);
            return 0;
        });
    }

    [Fact]
    public async Task The_shell_open_route_launches_the_transport_with_the_friendly_ingress_label()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-picture-open-label-", async root =>
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithOwnedSlots());
            var state = session.Slots("edit-long").Single(candidate => candidate.Slot.Id == "slot-owned");
            string canonical = Path.Combine(root, state.ProjectAsset!.File);
            Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
            TestImages.WritePng(canonical);
            var slot = new EditSlotRef(new EditRef(GoldenBody, "edit-long", "Long body"),
                state.Slot.Id, state.Slot.Input, state.Slot.Domain, state.Slot.MaterialSlotIndex,
                state.Slot.Material?.Name, state.ProjectAsset.File, state.Binding.Kind,
                SubmeshIndex: state.Slot.SubmeshIndex, ShaderProperty: state.Slot.ShaderProperty);
            MainWindowViewModel.PictureIngress? opened = null;
            string? launched = null;
            vm.PictureIngressOpenedForTests = ingress => opened = ingress;
            vm.LaunchImageEditorForTests = file => { launched = file; return true; };
            var status = new ImmediateProgress();

            var result = await ((IEditPageShell)vm).OpenPictureAsync(slot, status);

            Assert.True(result.Launched);
            Assert.NotNull(opened);
            Assert.Equal("Skin", opened!.Label);
            Assert.Equal(opened.Session.OutboundSnapshot, launched);
            Assert.Equal("Opened Skin in the image editor. Save to send it back.", status.Value);
            return 0;
        });
    }

    [Fact]
    public async Task Opening_a_stock_picture_carries_the_game_texture_name_on_save_back()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-stock-picture-label-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            var session = vm.ProjectDocument.Session!;
            string png = TestImages.WritePng(Path.Combine(root, "in", "stock.png"));
            string label = MainWindowViewModel.PictureIngressLabel(session.Snapshot(), card.Slot,
                supplied: null, gameTextureName: "body_base");
            var transport = ProjectAssetIngress.Begin(session.Snapshot(),
                card.Slot.Edit.EditDefinitionId, card.Slot.SlotId, png);
            var ingress = new MainWindowViewModel.PictureIngress(card.Slot, session, transport, label, null);

            var status = new ImmediateProgress();
            vm.PublishPictureReturn(ingress, status);

            var rebound = session.Slots(card.Slot.Edit.EditDefinitionId).Single(candidate =>
                candidate.Slot.Id == card.Slot.SlotId).ProjectAsset!;
            Assert.Equal("body_base", label);
            Assert.Equal("body_base", rebound.Label);
            Assert.Equal("Saved body_base to Edit 1's base color on body_material.", status.Value);
            return 0;
        });
    }

    [Fact]
    public async Task An_unconsented_editor_launch_still_refuses_a_new_live_shared_reach()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-saveback-live-shared-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            var session = vm.ProjectDocument.Session!;
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            var transport = ProjectAssetIngress.Begin(session.Snapshot(),
                card.Slot.Edit.EditDefinitionId, card.Slot.SlotId, png);
            var ingress = new MainWindowViewModel.PictureIngress(card.Slot, session, transport, "paint",
                null);
            var status = new ImmediateProgress();

            vm.SubjectModels.Clear();
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, TwoParts);
            vm.PublishPictureReturn(ingress, status);

            Assert.Equal(MainWindowViewModel.PictureSaveSharingRefusal(card.Slot, 2), status.Value);
            Assert.DoesNotContain(session.Snapshot().ProjectAssets,
                asset => asset.Kind == ProjectAssetKind.Picture);
            return 0;
        });
    }

    [Fact]
    public async Task A_shared_launch_with_consent_publishes_later_saves_without_asking_again()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-consented-save-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, TwoParts);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            var session = vm.ProjectDocument.Session!;
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            var transport = ProjectAssetIngress.Begin(session.Snapshot(),
                card.Slot.Edit.EditDefinitionId, card.Slot.SlotId, png);
            var ingress = new MainWindowViewModel.PictureIngress(card.Slot, session, transport, "paint",
                null, EditTextureSharing.Shared, 2, SharedConsent: true);
            int confirms = 0;
            vm.ConfirmForTests = (_, _, _, _) =>
            {
                confirms++;
                return Task.FromResult(false);
            };
            var status = new ImmediateProgress();

            vm.PublishPictureReturn(ingress, status);

            Assert.Equal(EditTextureSharing.Shared, ingress.LaunchSharing);
            Assert.Equal(2, ingress.LaunchUses);
            Assert.True(ingress.SharedConsent);
            Assert.Equal(0, confirms);
            Assert.Contains("Saved", status.Value);
            Assert.Contains(session.Snapshot().ProjectAssets,
                asset => asset.Kind == ProjectAssetKind.Picture);
            return 0;
        });
    }

    [Fact]
    public async Task Shared_save_consent_accepts_fewer_live_uses_but_refuses_a_larger_count()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-consent-fewer-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, TwoParts);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            var session = vm.ProjectDocument.Session!;
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            var transport = ProjectAssetIngress.Begin(session.Snapshot(),
                card.Slot.Edit.EditDefinitionId, card.Slot.SlotId, png);
            var ingress = new MainWindowViewModel.PictureIngress(card.Slot, session, transport, "paint",
                null, EditTextureSharing.Shared, 2, SharedConsent: true);
            var status = new ImmediateProgress();

            vm.SubjectModels.Clear();
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            vm.PublishPictureReturn(ingress, status);

            Assert.StartsWith("Saved ", status.Value);
            Assert.Contains(session.Snapshot().ProjectAssets,
                asset => asset.Kind == ProjectAssetKind.Picture);
            return 0;
        });
        await InTempMod("remold-uses-consent-more-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, TwoParts);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            var session = vm.ProjectDocument.Session!;
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            var transport = ProjectAssetIngress.Begin(session.Snapshot(),
                card.Slot.Edit.EditDefinitionId, card.Slot.SlotId, png);
            var ingress = new MainWindowViewModel.PictureIngress(card.Slot, session, transport, "paint",
                null, EditTextureSharing.Shared, 2, SharedConsent: true);
            var status = new ImmediateProgress();

            vm.SubjectModels.Clear();
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, ThreePlaces);
            vm.PublishPictureReturn(ingress, status);

            Assert.Equal(MainWindowViewModel.PictureSaveSharingRefusal(card.Slot, 3), status.Value);
            Assert.DoesNotContain(session.Snapshot().ProjectAssets,
                asset => asset.Kind == ProjectAssetKind.Picture);
            return 0;
        });
    }

    /// <summary>A save that arrives for a mod that is no longer open lands nothing and SAYS so, naming that
    /// mod. It used to return in silence: on a page showing another mod, an editor save that produced no
    /// line at all is indistinguishable from paint thrown away — and the editor still holds the file, which
    /// is the one thing the modder needs to know.</summary>
    [Fact]
    public async Task A_save_for_a_mod_that_is_no_longer_open_says_so_and_names_it()
    {
        using var settings = new SettingsSnapshot();
        await InTempMod("remold-uses-saveback-closed-", async root =>
        {
            var vm = await BoundaryWindowAsync(root);
            vm.SubjectModels.GetOrBuild(Character, GoldenOutfit, OneUse);
            vm.EditPage.Rebuild();
            var card = await BaseColorCardAsync(vm);
            string png = TestImages.WritePng(Path.Combine(root, "in", "paint.png"));
            // A transport opened on a DIFFERENT mod's session — the shape a save gets when the modder
            // opens another mod while the image editor is still up.
            var elsewhere = new AuthoredEditSession(new AuthoredProject());
            elsewhere.SetName("Other mod");
            var transport = ProjectAssetIngress.Begin(vm.ProjectDocument.Session!.Snapshot(),
                card.Slot.Edit.EditDefinitionId, card.Slot.SlotId, png);
            var ingress = new MainWindowViewModel.PictureIngress(card.Slot, elsewhere, transport, "paint",
                null);
            var status = new ImmediateProgress();

            vm.PublishPictureReturn(ingress, status);

            Assert.Equal(MainWindowViewModel.PictureSaveModClosed("Other mod"), status.Value);
            Assert.Contains("Other mod", status.Value);
            // Nothing was taken into the mod that IS open.
            Assert.DoesNotContain(vm.ProjectDocument.Session!.Snapshot().ProjectAssets,
                asset => asset.Kind == ProjectAssetKind.Picture);
            return 0;
        });
    }

    // ---- what the 3D preview SAMPLES, through the same shell ----
    //
    // Route: MainWindowViewModel.RenderMeshAsync reads the row's plan off SamplerPlan on the UI thread and
    // hands it to BuildSamplers on a worker. These call that plan builder, which is where the answer for
    // each submesh is decided.
    //
    // The subject part is the fixture's own MappedModel: one material, whose base map the install names
    // body_base in characters/vesna_ssr01_textures. The mod's own file is textures/skin.png, which
    // WithBorrowedSlot binds to slot-owned under edit-long and lets edit-short borrow through slot-base.

    private static SubjectPart MappedBody() => Assert.Single(MappedModel().Parts);

    /// <summary>An edit whose slot takes another edit's answer renders THAT edit's file. The card beside the
    /// render already followed the source; the render read the direct binding alone and drew the GAME's map,
    /// so one row showed two answers for one slot — and neither was what the build ships.</summary>
    [Fact]
    public async Task A_borrowed_base_colour_samples_the_source_edits_file()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-borrowplan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithBorrowedSlot());
            Directory.CreateDirectory(Path.Combine(root, "textures"));
            File.WriteAllBytes(Path.Combine(root, "textures", "skin.png"), new byte[] { 1, 2, 3 });

            var sampled = Assert.Single(vm.SamplerPlan(MappedBody(), session.Slots("edit-short"), root));

            Assert.True(sampled.Own);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "textures", "skin.png")), sampled.File);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>The borrowed answer keeps the direct answer's honesty rule: a file that will not resolve
    /// renders that submesh untextured. Falling back to the game's map would put the picture the edit
    /// replaced back on screen under the edit's own name.</summary>
    [Fact]
    public async Task A_borrowed_file_that_is_gone_renders_untextured_rather_than_stock()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-borrowgone-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The mod folder is never given textures/skin.png at all.
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithBorrowedSlot());

            var sampled = Assert.Single(vm.SamplerPlan(MappedBody(), session.Slots("edit-short"), root));

            Assert.True(sampled.Own);
            Assert.Null(sampled.File);
            Assert.Null(sampled.Bundle);
            Assert.Null(sampled.Texture);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>A source that answers with no file of the mod's is not an answer, and the submesh draws the
    /// game's own map — the same fallback an unanswered slot takes.</summary>
    [Fact]
    public async Task A_borrowed_answer_with_no_file_behind_it_draws_the_game_map()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-borrowstock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithBorrowedSlot());
            Directory.CreateDirectory(Path.Combine(root, "textures"));
            File.WriteAllBytes(Path.Combine(root, "textures", "skin.png"), new byte[] { 1, 2, 3 });
            // The source stops naming a file of the mod's, so what edit-short borrows is no longer one.
            session.ChooseInheritedCarrier("edit-long", "slot-owned");

            var sampled = Assert.Single(vm.SamplerPlan(MappedBody(), session.Slots("edit-short"), root));

            Assert.False(sampled.Own);
            Assert.Equal("characters/vesna_ssr01_textures", sampled.Bundle);
            Assert.Equal("body_base", sampled.Texture);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>The direct answer is untouched, and it still outranks a borrowed one at the same material
    /// position: edit-long binds the file itself AND borrows at position 0, and the plan is the file.</summary>
    [Fact]
    public async Task A_direct_binding_plans_its_own_file()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-directplan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithBorrowedSlot());
            Directory.CreateDirectory(Path.Combine(root, "textures"));
            File.WriteAllBytes(Path.Combine(root, "textures", "skin.png"), new byte[] { 1, 2, 3 });

            var sampled = Assert.Single(vm.SamplerPlan(MappedBody(), session.Slots("edit-long"), root));

            Assert.True(sampled.Own);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "textures", "skin.png")), sampled.File);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>An edit that answers nothing at the position draws the game's own map. The fallback the
    /// borrowed path must never reach when the modder HAS answered.</summary>
    [Fact]
    public async Task An_unanswered_position_draws_the_game_map()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-noanswer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.Golden());

            var sampled = Assert.Single(vm.SamplerPlan(MappedBody(), session.Slots("edit-long"), root));

            Assert.False(sampled.Own);
            Assert.Equal("characters/vesna_ssr01_textures", sampled.Bundle);
            Assert.Equal("body_base", sampled.Texture);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ---- a card whose answer names a file the mod folder does not hold ----
    //
    // Route: MainWindowViewModel.LoadMapPreviewAsync, the one producer of every card's picture.
    //
    // The slot is WithBorrowedSlot's slot-base: a GAME-domain base-colour slot at material position 0, which
    // is the position MappedModel's install answers with body_base. That is what makes the two answers
    // separable — the game HAS a map for this card, so a loader that cannot tell "no file bound" from "the
    // bound file is gone" hands back the original under the edit's own name.

    /// <summary>A file the edit binds and the mod folder does not hold is reported as that file, by name. It
    /// is not the game's texture: the record carries no picture at all, and the card's missing state is what
    /// the page reads off it.</summary>
    [Fact]
    public async Task A_bound_map_file_that_is_gone_is_reported_missing_by_name()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-mapgone-" + Guid.NewGuid().ToString("N"));
        try
        {
            // textures/skin.png is never written into the mod folder.
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithBorrowedSlot());
            var bound = session.Slots("edit-long").Single(state => state.Slot.Id == "slot-base");
            Assert.Equal(BindingKind.ProjectAsset, bound.Binding.Kind);
            Assert.Equal("textures/skin.png", bound.ProjectAsset!.File);
            var card = new EditSlotRef(new EditRef(GoldenBody, "edit-long", "Long body"), bound.Slot.Id,
                bound.Slot.Input, bound.Slot.Domain, bound.Slot.MaterialSlotIndex, null,
                bound.ProjectAsset.File, bound.Binding.Kind);
            // The install answers this exact card with a game texture, so the fall-through was reachable.
            Assert.Equal("body_base", ((IEditPageShell)vm).GameTextureName(card));

            var preview = await ((IEditPageShell)vm).LoadMapPreviewAsync(card);

            Assert.Equal("textures/skin.png", preview?.MissingFile);
            Assert.Null(preview!.Image);
            Assert.Equal(EditMapCardVm.NoDimensions, preview.Dimensions);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>And through a borrow: the file a source answer resolves to is this card's answer as surely as
    /// one bound here, so its absence is this card's missing state. Named by the file, which is the only place
    /// a borrowing card can say it — the card names no file of its own.</summary>
    [Fact]
    public async Task A_borrowed_map_file_that_is_gone_is_reported_missing_by_name()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-borrowmapgone-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithBorrowedSlot());
            var borrowing = session.Slots("edit-short").Single(state => state.Slot.Id == "slot-base");
            Assert.Equal(BindingKind.SourceSlot, borrowing.Binding.Kind);
            Assert.Null(borrowing.ProjectAsset);
            var card = new EditSlotRef(new EditRef(GoldenBody, "edit-short", "Short body"), borrowing.Slot.Id,
                borrowing.Slot.Input, borrowing.Slot.Domain, borrowing.Slot.MaterialSlotIndex, null, null,
                BindingKind.SourceSlot,
                new EditSlotSource(borrowing.Binding.SourceSlot!.EditDefinitionId,
                    borrowing.Binding.SourceSlot.SlotId), GameMaterialSlotIndex: 0);

            var preview = await ((IEditPageShell)vm).LoadMapPreviewAsync(card);

            Assert.Equal("textures/skin.png", preview?.MissingFile);
            Assert.Null(preview!.Image);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>A card with no file of the mod's behind it keeps the game's own map. Nothing is missing there:
    /// the slot names no file, so there is none to be gone, and the card stands on the install's texture.
    /// </summary>
    [Fact]
    public async Task An_unbound_game_card_claims_nothing_missing_and_keeps_the_game_map()
    {
        using var settings = new SettingsSnapshot();
        string root = Path.Combine(Path.GetTempPath(), "remold-mapstock-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (vm, session) = await MappedWindowAsync(root, AuthoredEditFixtures.WithBorrowedSlot());
            // The same slot as the missing pins, taken back to the game's own value.
            session.ChooseTargetGameValue("edit-long", "slot-base");
            var unbound = session.Slots("edit-long").Single(state => state.Slot.Id == "slot-base");
            Assert.Equal(BindingKind.TargetGameValue, unbound.Binding.Kind);
            var card = new EditSlotRef(new EditRef(GoldenBody, "edit-long", "Long body"), unbound.Slot.Id,
                unbound.Slot.Input, unbound.Slot.Domain, unbound.Slot.MaterialSlotIndex, null, null,
                unbound.Binding.Kind);

            var preview = await ((IEditPageShell)vm).LoadMapPreviewAsync(card);

            Assert.Null(preview?.MissingFile);
            Assert.Equal("body_base", ((IEditPageShell)vm).GameTextureName(card));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void Overlapping_Blender_prepare_scopes_hold_rescan_until_both_end()
    {
        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false };
        var first = vm.BeginSessionBlenderPrepare(combined: true);
        var second = vm.BeginSessionBlenderPrepare(combined: true);
        Assert.True(vm.RescanMustWait);

        first.Dispose();
        Assert.True(vm.RescanMustWait);

        second.Dispose();
        Assert.False(vm.RescanMustWait);
        first.Dispose();
        Assert.False(vm.RescanMustWait);
    }

    [Theory]
    [InlineData("disk full", "Stopped while saving the file sent back from Blender: disk full. Nothing was changed.")]
    [InlineData("The game is using these files.", "Stopped while saving the file sent back from Blender: The game is using these files. Nothing was changed.")]
    public void A_publish_failure_says_nothing_was_changed(string reason, string expected) =>
        Assert.Equal(expected, MainWindowViewModel.BlenderPublishFailure(reason));

    /// <summary>A return whose mod was closed while it waited names that mod, and closes on the same
    /// sentence every other refusal on this route closes on.</summary>
    [Theory]
    [InlineData("Vesna casual",
        "Couldn't apply the file sent back from Blender: Vesna casual is no longer open. Nothing was changed.")]
    [InlineData(MainWindowViewModel.UntitledMod,
        "Couldn't apply the file sent back from Blender: untitled mod is no longer open. "
        + "Nothing was changed.")]
    public void A_return_for_a_closed_mod_names_it(string mod, string expected) =>
        Assert.Equal(expected, MainWindowViewModel.BlenderReturnModClosed(mod));

    /// <summary>The two lines a return's own progress is told in. Running work takes the single ellipsis
    /// character, and both name the action rather than the transport behind it.</summary>
    [Fact]
    public void A_returns_own_lines_are_the_words_the_page_uses()
    {
        Assert.Equal("Applying the file sent back from Blender…", MainWindowViewModel.BlenderReturnApplying);
        Assert.Equal("Blender sent back no changes.", MainWindowViewModel.BlenderReturnNoChanges);
    }

    [Theory]
    [InlineData(1, 1, 0, "Blender sent back 1 edit and 1 changed image.")]
    [InlineData(2, 3, 1, "Blender sent back 2 edits and 3 changed images · 1 unchanged part.")]
    [InlineData(0, 0, 1, "Blender sent back no changes · 1 unchanged part.")]
    [InlineData(1, 0, 2, "Blender sent back 1 edit and 0 changed images · 2 unchanged parts.")]
    public void A_returns_final_count_line_includes_unchanged_parts(int edits, int images, int unchanged,
        string expected) =>
        Assert.Equal(expected, MainWindowViewModel.BlenderReturnCounts(edits, images, unchanged));

    /// <summary>A mod on the older project format, opened with no game files to convert it against. The
    /// conversion is the only support that format has, so the open FAILS and the workspace is left exactly
    /// as it was — the modder keeps whatever was open, rather than being handed a page that draws nothing
    /// over a mod that is full.</summary>
    [Fact]
    public async Task AModOnTheOlderFormatWithNoGameFilesDoesNotOpen()
    {
        using var settings = new SettingsSnapshot();
        var root = Path.Combine(Path.GetTempPath(), "remold-released-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var released = new ModProject { RootDir = root };
            released.Info.Name = "Released";
            released.Selection.Add(new SelectionEntry { Character = Character, Outfit = Outfit });
            released.Save();

            var vm = new MainWindowViewModel(startLoad: false);
            var standing = vm.ProjectDocument;

            Assert.False(await vm.OpenModAsync(root));

            Assert.Same(standing, vm.ProjectDocument);
            Assert.True(vm.ShowHome);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>A new mod replaces the document, and the page takes the new session with it. Left holding
    /// the old one it would keep drawing — and writing to — the project the modder just left.</summary>
    [Fact]
    public void StartingANewModEmptiesThePage()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        AuthorPart(vm);
        vm.SelectedStep = "② Edit";
        Assert.True(vm.EditPage.HasNodes);

        vm.NewMod();

        Assert.Empty(vm.EditPage.Nodes);
        Assert.True(vm.EditPage.IsEmpty);
    }
}
