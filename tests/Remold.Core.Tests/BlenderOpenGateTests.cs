using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.ViewModels.EditPage;
using Remold.Core;
using Remold.Core.Blender;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Migoto;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The shell's last mesh-edit gates, where a Blender open becomes a writable session. These use
/// the real open methods and inspect the bridge sidecars production writes, rather than restating the gate
/// predicate: a blocked lone open stops before a session exists, while open-all carries the blocked part as
/// read-only context and still offers its healthy sibling as a mint-on-return target.</summary>
[Collection("Dispatcher")]
public sealed class BlenderOpenGateTests
{
    private const string CharacterName = "GateTest";
    private const string OutfitStem = "GateTestSSR01";
    private const string BlockedSlot = "c_gatetest_face_lod0";
    private const string AllowedSlot = "c_gatetest_body_lod0";
    private const string PearlSlot = "c_gatetest_pearl_lod0";
    private const string BlockedBundle = "characters/gatetest_face.bundle";
    private const string AllowedBundle = "characters/gatetest_body.bundle";
    private const string PearlBundle = "characters/gatetest_pearl.bundle";
    private static readonly float[] Positions = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] Triangles = { 0, 1, 2 };
    private static readonly uint[] Bones = { 11u, 22u, 33u };

    [Fact]
    public async Task A_gate_blocked_lone_open_reports_the_refusal_and_mints_nothing()
    {
        using var settings = new SettingsSnapshot();
        using var game = new TempGame();
        var install = Install(game);
        var seed = AuthoredProjectDocument.New().Session;
        seed.EnsurePartSlots(install.BlockedPart, _ => AuthoredParts.Resolve(install.BlockedPart));
        string editId = seed.CreateEdit(install.BlockedPart);
        var (vm, session, root) = await Window(game, install, seed.Snapshot());
        long revision = session.Revision;
        int assets = session.Snapshot().ProjectAssets.Count;
        var status = new CapturedProgress();

        await vm.OpenInBlenderAsync(new EditRef(install.BlockedPart, editId, "Blocked face"),
            withReferences: false, status);

        Assert.Equal(PartSkinGate.EditRefusal(StreamDump.SkinRefusal.BlendShapes), status.Value);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(assets, session.Snapshot().ProjectAssets.Count);
        Assert.Empty(Directory.GetFiles(root, "*.gf2session.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Part_and_edit_lone_opens_write_stock_and_exact_sessions_without_minting()
    {
        using var settings = new SettingsSnapshot();
        using var game = new TempGame();
        var install = Install(game);
        var seed = AuthoredProjectDocument.New().Session;
        seed.EnsurePartSlots(install.AllowedPart, _ => AuthoredParts.Resolve(install.AllowedPart));
        string editId = seed.CreateEdit(install.AllowedPart, "Body edit");
        int editsBefore = seed.Snapshot().EditDefinitions.Count;
        var (vm, session, root) = await Window(game, install, seed.Snapshot());
        var status = new CapturedProgress();

        var before = SessionFiles(root);
        await vm.OpenPartInBlenderAsync(install.AllowedPart, withReferences: false, status);
        string stockOpened = OpenedFromNewSession(root, before);
        var stock = BlenderBridge.ReadSessionDocument(stockOpened)!;
        Assert.Null(Assert.Single(stock.Parts).OpenedFromEditId);
        Assert.True(Assert.Single(BlenderBridge.ReadReturnTargets(
            Path.Combine(Path.GetDirectoryName(stockOpened)!, AssetExporter.SessionSendGlbName))).IsPartRoute);

        before = SessionFiles(root);
        await vm.OpenInBlenderAsync(new EditRef(install.AllowedPart, editId, "Body edit"),
            withReferences: false, status);
        string editOpened = OpenedFromNewSession(root, before);
        Assert.Equal(editId, Assert.Single(
            BlenderBridge.ReadSessionDocument(editOpened)!.Parts).OpenedFromEditId);
        Assert.Equal(editId, Assert.Single(BlenderBridge.ReadReturnTargets(
            Path.Combine(Path.GetDirectoryName(editOpened)!, AssetExporter.SessionSendGlbName)))
            .EditDefinitionId);
        Assert.Equal(editsBefore, session.Snapshot().EditDefinitions.Count);
    }

    [Fact]
    public async Task Open_all_keeps_a_gate_blocked_slot_out_of_writes_and_mints_while_a_sibling_proceeds()
    {
        using var settings = new SettingsSnapshot();
        using var game = new TempGame();
        var install = Install(game);
        var seed = AuthoredProjectDocument.New().Session;
        seed.EnsurePartSlots(install.BlockedPart, _ => AuthoredParts.Resolve(install.BlockedPart));
        seed.EnsurePartSlots(install.AllowedPart, _ => AuthoredParts.Resolve(install.AllowedPart));
        seed.AddHideEdit(install.BlockedPart);
        int editsBefore = seed.Snapshot().EditDefinitions.Count;
        var (vm, session, root) = await Window(game, install, seed.Snapshot());
        var status = new CapturedProgress();

        await vm.OpenSubjectInBlenderAsync(CharacterName, OutfitStem, status);

        string sessionFile = Assert.Single(Directory.GetFiles(root, "*.gf2session.json",
            SearchOption.AllDirectories));
        const string suffix = ".gf2session.json";
        Assert.EndsWith(suffix, sessionFile, StringComparison.Ordinal);
        string opened = sessionFile[..^suffix.Length] + ".glb";
        var parts = BlenderBridge.ReadSessionDocument(opened)!.Parts;
        var blocked = Assert.Single(parts, part => part.Name == BlockedSlot);
        Assert.False(blocked.IsWritable);
        Assert.False(blocked.IsViewportVisible); // active hides remain present as presentation-only hidden rows
        Assert.True(Assert.Single(parts, part => part.Name == AllowedSlot).IsWritable);

        string returned = Path.Combine(Path.GetDirectoryName(opened)!, AssetExporter.SessionSendGlbName);
        var target = Assert.Single(BlenderBridge.ReadReturnTargets(returned));
        Assert.Equal(AllowedSlot, target.Part);
        Assert.True(target.IsPartRoute);
        Assert.Equal(editsBefore, session.Snapshot().EditDefinitions.Count); // only a Send may create content
    }

    [Fact]
    public async Task Open_all_stock_and_first_edit_write_distinct_destinations_without_minting()
    {
        using var settings = new SettingsSnapshot();
        using var game = new TempGame();
        var install = Install(game);
        var seed = AuthoredProjectDocument.New().Session;
        seed.EnsurePartSlots(install.BlockedPart, _ => AuthoredParts.Resolve(install.BlockedPart));
        seed.EnsurePartSlots(install.AllowedPart, _ => AuthoredParts.Resolve(install.AllowedPart));
        string blocked = seed.CreateEdit(install.BlockedPart, "Blocked face");
        string first = seed.CreateEdit(install.AllowedPart, "First body");
        string active = seed.CreateEdit(install.AllowedPart, "Active body");
        seed.UnplaceEdit(first);
        seed.PlaceEdit(active);
        var project = seed.Snapshot();
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "asset-blocked", Kind = ProjectAssetKind.Geometry,
            Label = "Blocked mesh", File = "meshes/missing-blocked.glb",
        });
        var blockedGeometry = project.TargetSlots.Single(slot => slot.Part.SameAs(install.BlockedPart)
            && slot.Input == TargetInputKind.Geometry);
        var blockedBinding = project.EditDefinitions.Single(edit => edit.Id == blocked).Bindings
            .Single(binding => binding.SlotId == blockedGeometry.Id);
        blockedBinding.Kind = BindingKind.ProjectAsset;
        blockedBinding.ProjectAssetId = "asset-blocked";
        int editsBefore = project.EditDefinitions.Count;
        var (vm, session, root) = await Window(game, install, project);
        var status = new CapturedProgress();

        var before = SessionFiles(root);
        await vm.OpenSubjectInBlenderAsync(CharacterName, OutfitStem, status);
        string stockOpened = OpenedFromNewSession(root, before);
        var stock = BlenderBridge.ReadSessionDocument(stockOpened)!;
        Assert.Null(Assert.Single(stock.Parts, part => part.Name == AllowedSlot).OpenedFromEditId);
        Assert.True(Assert.Single(BlenderBridge.ReadReturnTargets(
            Path.Combine(Path.GetDirectoryName(stockOpened)!, AssetExporter.SessionSendGlbName)),
            target => target.Part == AllowedSlot).IsPartRoute);

        before = SessionFiles(root);
        await vm.OpenSubjectFirstEditInBlenderAsync(CharacterName, OutfitStem, status);
        string editedOpened = OpenedFromNewSession(root, before);
        var edited = BlenderBridge.ReadSessionDocument(editedOpened)!;
        Assert.Null(Assert.Single(edited.Parts, part => part.Name == BlockedSlot).OpenedFromEditId);
        Assert.Equal(active, Assert.Single(edited.Parts,
            part => part.Name == AllowedSlot).OpenedFromEditId);
        Assert.Equal(active, Assert.Single(BlenderBridge.ReadReturnTargets(
            Path.Combine(Path.GetDirectoryName(editedOpened)!, AssetExporter.SessionSendGlbName)),
            target => target.Part == AllowedSlot).EditDefinitionId);
        Assert.Equal(editsBefore, session.Snapshot().EditDefinitions.Count);
    }

    [Fact]
    public async Task A_collapsed_points_lone_open_reports_its_own_refusal_and_mints_nothing()
    {
        using var settings = new SettingsSnapshot();
        using var game = new TempGame();
        var install = PearlInstall(game);
        var seed = AuthoredProjectDocument.New().Session;
        seed.EnsurePartSlots(install.BlockedPart, _ => AuthoredParts.Resolve(install.BlockedPart));
        var (vm, session, root) = await Window(game, install, seed.Snapshot());
        long revision = session.Revision;
        var status = new CapturedProgress();

        await vm.OpenPartInBlenderAsync(install.BlockedPart, withReferences: false, status);

        Assert.Equal(PartSkinGate.CollapsedBillboardRefusal, status.Value);
        Assert.Equal(revision, session.Revision);
        Assert.Empty(Directory.GetFiles(root, "*.gf2session.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Open_all_keeps_a_collapsed_points_slot_out_of_writes_while_a_sibling_proceeds()
    {
        using var settings = new SettingsSnapshot();
        using var game = new TempGame();
        var install = PearlInstall(game);
        var seed = AuthoredProjectDocument.New().Session;
        seed.EnsurePartSlots(install.BlockedPart, _ => AuthoredParts.Resolve(install.BlockedPart));
        seed.EnsurePartSlots(install.AllowedPart, _ => AuthoredParts.Resolve(install.AllowedPart));
        var (vm, session, root) = await Window(game, install, seed.Snapshot());
        var status = new CapturedProgress();

        await vm.OpenSubjectInBlenderAsync(CharacterName, OutfitStem, status);

        string sessionFile = Assert.Single(Directory.GetFiles(root, "*.gf2session.json",
            SearchOption.AllDirectories));
        string opened = sessionFile[..^".gf2session.json".Length] + ".glb";
        var parts = BlenderBridge.ReadSessionDocument(opened)!.Parts;
        Assert.False(Assert.Single(parts, part => part.Name == PearlSlot).IsWritable);
        Assert.True(Assert.Single(parts, part => part.Name == AllowedSlot).IsWritable);

        string returned = Path.Combine(Path.GetDirectoryName(opened)!, AssetExporter.SessionSendGlbName);
        var target = Assert.Single(BlenderBridge.ReadReturnTargets(returned));
        Assert.Equal(AllowedSlot, target.Part);
    }

    private static HashSet<string> SessionFiles(string root) =>
        Directory.GetFiles(root, "*.gf2session.json", SearchOption.AllDirectories)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string OpenedFromNewSession(string root, IReadOnlySet<string> before)
    {
        string sessionFile = Assert.Single(SessionFiles(root), path => !before.Contains(path));
        const string suffix = ".gf2session.json";
        Assert.EndsWith(suffix, sessionFile, StringComparison.Ordinal);
        return sessionFile[..^suffix.Length] + ".glb";
    }

    private static async Task<(MainWindowViewModel Vm, AuthoredEditSession Session, string Root)>
        Window(TempGame game, GateInstall install, AuthoredProject project)
    {
        string root = game.At("gate-open-mod");
        project.Info.Name = "mesh gate open";
        AuthoredProjectSerializer.Save(project, ModProject.ManifestPathFor(root));
        string bridgeScript = EnsureBridgeScript(game);
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work(),
            bridgeScriptPath: bridgeScript);
        Assert.True(await vm.OpenModAsync(root));
        SetField(vm, "_vfs", install.Vfs);
        SetField(vm, "_gameDir", game.Root);
        SetField(vm, "_roster", new List<Character> { install.Character });
        vm.SubjectModels.GetOrBuild(CharacterName, OutfitStem, () => install.Model);

        string blender = game.At("blender.exe");
        File.WriteAllBytes(blender, Array.Empty<byte>());
        var appSettings = (LabSettings)GetField(vm, "_settings")!;
        appSettings.PreferredBlender = blender;
        vm.BlenderPath = blender;
        return (vm, vm.ProjectDocument.Session, root);
    }

    private static GateInstall Install(TempGame game)
    {
        const string blockedHash = "11111111111111111111111111111111";
        const string allowedHash = "22222222222222222222222222222222";
        string abw = Path.Combine(game.Root, "AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        long blockedPath = SyntheticBundle.BuildOneSkinnedMesh(
            Path.Combine(abw, blockedHash + ".bundle"), BlockedSlot, Positions, Triangles, Bones,
            blendShapes: 5);
        long allowedPath = SyntheticBundle.BuildOneSkinnedMesh(
            Path.Combine(abw, allowedHash + ".bundle"), AllowedSlot, Positions, Triangles, Bones);
        var vfs = TestVfs.Create(game.Root,
            Array.Empty<(string Address, string OwnerBundle)>(), null,
            (BlockedBundle, blockedHash), (AllowedBundle, allowedHash));
        var blocked = new TargetPart
        {
            Subject = CharacterName, Outfit = OutfitStem, RendererSlot = BlockedSlot,
        };
        var allowed = new TargetPart
        {
            Subject = CharacterName, Outfit = OutfitStem, RendererSlot = AllowedSlot,
        };
        var model = new SubjectModel(CharacterName, OutfitStem, SubjectSource.Prefab, new[]
        {
            new SubjectPart("face", BlockedSlot, "", Array.Empty<SubjectMaterial>(),
                MeshBundle: BlockedBundle, MeshPathId: blockedPath),
            new SubjectPart("body", AllowedSlot, "", Array.Empty<SubjectMaterial>(),
                MeshBundle: AllowedBundle, MeshPathId: allowedPath),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var outfit = new Outfit(100, OutfitStem, OutfitKind.Base);
        var character = new Character(1, CharacterName, "GateTestSSR", 100, 0,
            new List<Outfit> { outfit });
        return new GateInstall(vfs, model, character, blocked, allowed);
    }

    /// <summary>Like <see cref="Install"/> but the blocked part is a collapsed-points billboard (every
    /// corner of its one triangle on a single position) with a healthy skin — refused by the
    /// collapsed-points answer, not by <see cref="PartSkinGate.Blocked"/>.</summary>
    private static GateInstall PearlInstall(TempGame game)
    {
        const string pearlHash = "33333333333333333333333333333333";
        const string allowedHash = "44444444444444444444444444444444";
        string abw = Path.Combine(game.Root, "AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        long pearlPath = SyntheticBundle.BuildOneSkinnedMesh(
            Path.Combine(abw, pearlHash + ".bundle"), PearlSlot,
            new float[] { 1, 2, 3, 1, 2, 3, 1, 2, 3 }, Triangles, Bones);
        long allowedPath = SyntheticBundle.BuildOneSkinnedMesh(
            Path.Combine(abw, allowedHash + ".bundle"), AllowedSlot, Positions, Triangles, Bones);
        var vfs = TestVfs.Create(game.Root,
            Array.Empty<(string Address, string OwnerBundle)>(), null,
            (PearlBundle, pearlHash), (AllowedBundle, allowedHash));
        var pearl = new TargetPart
        {
            Subject = CharacterName, Outfit = OutfitStem, RendererSlot = PearlSlot,
        };
        var allowed = new TargetPart
        {
            Subject = CharacterName, Outfit = OutfitStem, RendererSlot = AllowedSlot,
        };
        var model = new SubjectModel(CharacterName, OutfitStem, SubjectSource.Prefab, new[]
        {
            new SubjectPart("pearl", PearlSlot, "", Array.Empty<SubjectMaterial>(),
                MeshBundle: PearlBundle, MeshPathId: pearlPath),
            new SubjectPart("body", AllowedSlot, "", Array.Empty<SubjectMaterial>(),
                MeshBundle: AllowedBundle, MeshPathId: allowedPath),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var outfit = new Outfit(100, OutfitStem, OutfitKind.Base);
        var character = new Character(1, CharacterName, "GateTestSSR", 100, 0,
            new List<Outfit> { outfit });
        return new GateInstall(vfs, model, character, pearl, allowed);
    }

    private static object? GetField(MainWindowViewModel vm, string name) =>
        typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(vm);

    private static void SetField(MainWindowViewModel vm, string name, object value) =>
        typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, value);

    private static string EnsureBridgeScript(TempGame game)
    {
        string path = game.At(@"isolated-app\blender\remold_bridge.py");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "# test bridge placeholder");
        return path;
    }

    private sealed class CapturedProgress : IProgress<string>
    {
        public string Value { get; private set; } = "";
        public void Report(string value) => Value = value;
    }

    private sealed record GateInstall(GameVfs Vfs, SubjectModel Model, Character Character,
        TargetPart BlockedPart, TargetPart AllowedPart);
}
