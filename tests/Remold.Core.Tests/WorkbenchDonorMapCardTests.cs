using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remold.App.ViewModels.Workbench;
using Remold.Core.Export;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The map-card verbs on a DONOR-DERIVED row: a map a send-back bound from the edited mesh, with an EMPTY
/// bundle id and an <c>AuthoredPath</c>. Nothing about such a row can go to a bundle — the UV guide has no
/// game mesh to draw and Materialize-all must not try to capture it, or each fails on "couldn't read the
/// bundle holding …".
///
/// <para><c>AuthoredPath</c> decides where Open and Drop land, bundle or not: on a replaced part the build
/// ships the authored PNG and drops the part's texture edits. The bundle still decides everything keyed on
/// the Texture2D target.</para>
/// </summary>
[Collection("Dispatcher")]
public class WorkbenchDonorMapCardTests
{
    private sealed class RecordingShell : IWorkbenchShell
    {
        public int ConfirmCalls;
        public int ApplyPngCalls;
        public int ApplyAuthoredPngCalls;
        public string? LastAuthoredTarget;
        public string? LastDroppedFile;
        public int OpenInEditorCalls;
        public int OpenAuthoredCalls;
        public int UvGuideCalls;
        public string? LastAuthoredPath;
        public string? LastOpenedTexture;

        public DroppedPngConfirm? LastConfirm;
        public bool LastConfirmAuthored => LastConfirm?.IsAuthored ?? false;
        public string? LastConfirmSizeNote => LastConfirm?.SizeNote;
        public DonorMapDrop? LastConfirmDonor => LastConfirm?.Donor;

        public Task<bool> ConfirmApplyDroppedPngAsync(DroppedPngConfirm ask)
        { ConfirmCalls++; LastConfirm = ask; return Task.FromResult(true); }

        public int ApplyDonorMapCalls;
        public DonorMapDrop? LastDonorDrop;

        public Task ApplyDroppedPngToDonorMapAsync(WorkbenchSubjectRef s, DonorMapDrop donor, string mapRole,
            string path, IProgress<string> status)
        { ApplyDonorMapCalls++; LastDonorDrop = donor; LastDroppedFile = path; return Task.CompletedTask; }

        public Task ApplyDroppedPngAsync(WorkbenchSubjectRef s, string t, string b,
            IReadOnlyList<string> o, string path, IProgress<string> status)
        { ApplyPngCalls++; return Task.CompletedTask; }

        public string? LastAuthoredPart;
        public string? LastAuthoredRole;

        public Task ApplyDroppedPngToAuthoredAsync(string authoredPath, string partToken, string mapRole,
            string path, IProgress<string> status)
        {
            ApplyAuthoredPngCalls++; LastAuthoredTarget = authoredPath; LastDroppedFile = path;
            LastAuthoredPart = partToken; LastAuthoredRole = mapRole; return Task.CompletedTask;
        }

        public Task OpenMapInEditorAsync(WorkbenchSubjectRef s, string t, string b,
            IReadOnlyList<string> o, IProgress<string> p)
        { OpenInEditorCalls++; LastOpenedTexture = t; return Task.CompletedTask; }

        public Task OpenAuthoredMapAsync(string authoredPath, IProgress<string> p)
        { OpenAuthoredCalls++; LastAuthoredPath = authoredPath; return Task.CompletedTask; }

        public Task OpenMapUvGuideAsync(WorkbenchSubjectRef subj, string t, string b,
            IReadOnlyList<(string, string, int, string?)> s, IProgress<string> p)
        { UvGuideCalls++; return Task.CompletedTask; }

        // ---- unused by these card tests ----
        public Task<PartMaterializeOutcome> MaterializePartAsync(WorkbenchSubjectRef s, RecipePart r, IProgress<string> p, CancellationToken c) => Task.FromResult(PartMaterializeOutcome.Ready());
        public Task<bool> MaterializeTextureAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<string> o, IProgress<string> p, CancellationToken c) => Task.FromResult(true);
        public Task OpenPartInBlenderAsync(WorkbenchSubjectRef s, RecipePart r, IReadOnlyList<RecipePart> outfit, IProgress<string> p) => Task.CompletedTask;
        public Task OpenPartAloneInBlenderAsync(WorkbenchSubjectRef s, RecipePart r, IProgress<string> p) => Task.CompletedTask;
        public Task OpenAllPartsInBlenderAsync(WorkbenchSubjectRef s, IReadOnlyList<RecipePart> r, IProgress<string> p) => Task.CompletedTask;
        public Task<int> MaterializeAllAsync(WorkbenchSubjectRef s, IReadOnlyList<MaterializeItem> i, IProgress<string> p, CancellationToken c) => Task.FromResult(0);
        public Task RevertPartAsync(WorkbenchSubjectRef s, string t, IProgress<string> p) => Task.CompletedTask;
        public Task RevertMapAsync(WorkbenchSubjectRef subj, string t, string b, IProgress<string> p) => Task.CompletedTask;
        public void PrewarmSubject(WorkbenchSubjectRef s) { }
        public void ShowSubjectInFolder(WorkbenchSubjectRef s) { }
        public Task RemoveSubjectAsync(WorkbenchSubjectRef s) => Task.CompletedTask;
        public Task CopyTextAsync(string? text) => Task.CompletedTask;
        public void GoToBuild() { }
        public void AutoSaveProject() { }
        public string? AdoptSubjectTextureEdits(WorkbenchSubjectRef s, Remold.Core.Workbench.SubjectModel m) => null;
    }

    private static WorkbenchVm NewVm(RecordingShell shell, ModProject? project = null,
        Func<string, byte[]?>? tryDeobfuscate = null) => new(
        project: () => project ?? new ModProject(),
        vfs: () => null,
        friendly: () => FriendlyNames.Empty,
        roster: () => Array.Empty<Character>(),
        tryDeobfuscate: tryDeobfuscate ?? (_ => null),
        catalog: null,
        shell: shell);

    private static readonly WorkbenchSubjectRef Subject =
        new("char", "stem", "c_stem_slg_", new Outfit(0, "stem", OutfitKind.Base));

    /// <summary>A game map row: a real bundle behind it.</summary>
    private static WorkbenchMapVm GameCard(string textureName = "c_stem_body1_d") =>
        new("Base color", "_BaseMap", textureName, "bundle1") { Subject = Subject };

    /// <summary>A donor-derived row as <c>BuildDonorMaterialNode</c> makes one: empty bundle id, authored
    /// PNG path applied afterwards by the refresh.</summary>
    private static WorkbenchMapVm DonorCard(string authoredPath = @"C:\mod\textures\body_s0_base.png") =>
        new("Base color", "_BaseMap", "body_s0_base", "") { Subject = Subject, AuthoredPath = authoredPath };

    /// <summary>A game normal-map row, on the same bundle as its material's base color.</summary>
    private static WorkbenchMapVm NormalCard(string textureName) =>
        new("Normal", "_BumpMap", textureName, "bundle1") { Subject = Subject };

    private static WorkbenchMapVm RmoCard(string textureName) =>
        new(TextureMap.RmoLabel, "_RMOTex", textureName, "bundle1") { Subject = Subject, IsRmo = true };

    // ---- the bundle predicate: the one home of the rule ----

    [Fact]
    public void HasBundle_SeparatesGameRowsFromDonorRows()
    {
        Assert.True(GameCard().HasBundle);
        Assert.False(DonorCard().HasBundle);
    }

    // ---- UV: no guide for a map that never came from the game ----

    [Fact]
    public void DonorRow_UvButton_IsDisabled_AndSaysWhy()
    {
        var donor = DonorCard();
        Assert.False(donor.CanOpenUvGuide);
        Assert.Equal("No UV guide for the replacement's own map", donor.UvHint);
    }

    [Fact]
    public void GameRow_UvButton_IsEnabledWhenIdle_DisabledWhileBusy()
    {
        var game = GameCard();
        Assert.True(game.CanOpenUvGuide);
        game.IsBusy = true;
        Assert.False(game.CanOpenUvGuide);   // the existing busy rule still applies
    }

    [Fact]
    public async Task DonorRow_UvGuideVerb_RefusesWithoutCallingTheShell()
    {
        var shell = new RecordingShell();
        var vm = NewVm(shell);

        await vm.OpenMapUvGuideCommand.ExecuteAsync(DonorCard());

        Assert.Equal(0, shell.UvGuideCalls);
        Assert.Equal("No UV guide for the replacement's own map.", vm.Status);
    }

    // ---- Open: the authored file, never a bundle materialize ----

    [Fact]
    public async Task DonorRow_Open_LaunchesTheAuthoredFile()
    {
        var shell = new RecordingShell();
        var vm = NewVm(shell);

        await vm.OpenMapCommand.ExecuteAsync(DonorCard(@"C:\mod\textures\body_s0_base.png"));

        Assert.Equal(1, shell.OpenAuthoredCalls);
        Assert.Equal(@"C:\mod\textures\body_s0_base.png", shell.LastAuthoredPath);
        Assert.Equal(0, shell.OpenInEditorCalls);   // no bundle materialize on an empty bundle id
    }

    [Fact]
    public async Task GameRow_Open_StillGoesThroughMaterializeAndEditor()
    {
        var shell = new RecordingShell();
        var vm = NewVm(shell);

        await vm.OpenMapCommand.ExecuteAsync(GameCard("c_stem_body1_d"));

        Assert.Equal(1, shell.OpenInEditorCalls);
        Assert.Equal("c_stem_body1_d", shell.LastOpenedTexture);
        Assert.Equal(0, shell.OpenAuthoredCalls);
    }

    [Fact]
    public async Task RowWithBundleAndAuthoredPath_Open_GoesToTheAuthoredFile()
    {
        // A send-back can bind an authored PNG over a GAME map row, which keeps its bundle. The part is
        // replaced, so the build ships that PNG and drops texture edits — editing the game texture would
        // edit a file the mod never ships.
        var shell = new RecordingShell();
        var vm = NewVm(shell);
        var both = new WorkbenchMapVm("Base color", "_BaseMap", "c_stem_body1_d", "bundle1")
        { Subject = Subject, AuthoredPath = @"C:\mod\textures\body_s0_base.png" };

        await vm.OpenMapCommand.ExecuteAsync(both);

        Assert.Equal(1, shell.OpenAuthoredCalls);
        Assert.Equal(@"C:\mod\textures\body_s0_base.png", shell.LastAuthoredPath);
        Assert.Equal(0, shell.OpenInEditorCalls);
    }

    [Fact]
    public async Task BundlelessRow_WithNoAuthoredFile_Open_RefusesInsteadOfBundleHunting()
    {
        // the send-back's texture row went away but its material name didn't, so the row outlived its file
        var shell = new RecordingShell();
        var vm = NewVm(shell);
        var orphan = new WorkbenchMapVm("Base color", "_BaseMap", "body_s0_base", "") { Subject = Subject };

        await vm.OpenMapCommand.ExecuteAsync(orphan);

        Assert.Equal(0, shell.OpenAuthoredCalls);
        Assert.Equal(0, shell.OpenInEditorCalls);
        Assert.Contains("isn't on disk", vm.Status);
    }

    // ---- drop: into the file the card shows ----

    [Fact]
    public async Task DonorRow_CardDrop_ReplacesTheAuthoredFile()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell);
        var dropped = TestImages.WritePng(temp.At("paint.png"));

        await vm.HandleDropAsync(new[] { dropped }, DonorCard());

        Assert.Equal(1, shell.ConfirmCalls);
        Assert.True(shell.LastConfirmAuthored);   // the confirm flags the un-revertible case before it happens
        Assert.Equal(1, shell.ApplyAuthoredPngCalls);
        Assert.Equal(@"C:\mod\textures\body_s0_base.png", shell.LastAuthoredTarget);
        Assert.Equal(dropped, shell.LastDroppedFile);
        Assert.Equal(0, shell.ApplyPngCalls);   // no bundle materialize on an empty bundle id
    }

    /// <summary>A differently-sized image still applies — UVs are normalized and the shipped override is
    /// keyed by hash, not dimensions — but the confirm says so before it lands, and says nothing when the
    /// sizes agree.</summary>
    [Fact]
    public async Task CardDrop_SizeMismatch_AddsOneLineToTheConfirm()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell);
        var authored = TestImages.WritePng(temp.At("authored.png"), 64, 32);

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("small.png"), 16, 16) }, DonorCard(authored));

        Assert.Equal("16×16, the map is 64×32. It still applies; UVs stretch it to fit.",
            shell.LastConfirmSizeNote);
        Assert.Equal(1, shell.ApplyAuthoredPngCalls);   // informational, never a block
    }

    [Fact]
    public async Task CardDrop_MatchingSize_AddsNoLine()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell);
        var authored = TestImages.WritePng(temp.At("authored.png"), 64, 32);

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("same.png"), 64, 32) }, DonorCard(authored));

        Assert.Null(shell.LastConfirmSizeNote);
        Assert.Equal(1, shell.ApplyAuthoredPngCalls);
    }

    // ---- …and on the BUNDLE-BACKED route, which is the one production mostly takes ----

    /// <summary>A game map with no authored file and no edit yet: the size to compare against is the stock
    /// texture's, read out of the bundle exactly as the card's size line reads it.</summary>
    [Fact]
    public async Task GameCardDrop_SizeMismatch_NamesTheStockSizeFromTheBundle()
    {
        using var g = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, new ModProject { RootDir = g.Root }, StockBundle(g, 128, 64));

        await vm.HandleDropAsync(new[] { TestImages.WritePng(g.At("small.png"), 16, 16) }, GameCard());

        Assert.Equal("16×16, the map is 128×64. It still applies; UVs stretch it to fit.",
            shell.LastConfirmSizeNote);
        Assert.Equal(1, shell.ApplyPngCalls);   // the bundle route applied it, informational note and all
    }

    /// <summary>Once a card has been edited, the file it stands for is the WORKSPACE PNG — a drop already
    /// resized it, and the bundle still holds the stock dimensions. The confirm has to name the size of the
    /// image about to be replaced, not the one the game shipped.</summary>
    [Fact]
    public async Task EditedGameCardDrop_SizeMismatch_NamesTheWorkspaceSizeNotTheStockOne()
    {
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        // an earlier drop already put a 16×16 image where the bundle says the stock map is 128×64
        TestImages.WritePng(project.Resolve(EditedGameRel), 16, 16, r: 0, g: 255, b: 0);
        TestImages.WritePng(project.Resolve(EditedGameOrigRel), 128, 64);
        project.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bundle1", ObjectName = "c_stem_body1_d",
            ReplaceFile = EditedGameRel, OriginalFile = EditedGameOrigRel,
            SubjectCharacter = "char", SubjectOutfit = "stem",
        });
        var shell = new RecordingShell();
        var vm = NewVm(shell, project, StockBundle(g, 128, 64));
        var map = GameCardInATree(vm);
        Assert.True(map.IsEdited);   // the branch this test is about is only reachable on an edited row

        await vm.HandleDropAsync(new[] { TestImages.WritePng(g.At("wide.png"), 64, 8) }, map);

        Assert.Equal("64×8, the map is 16×16. It still applies; UVs stretch it to fit.",
            shell.LastConfirmSizeNote);
    }

    /// <summary>An authored path whose file has gone away is not what the card stands for any more — the
    /// size read falls through to the bundle behind it, like every other reader of a vanished authored
    /// file.</summary>
    [Fact]
    public async Task CardDrop_AuthoredFileGone_ReadsTheSizeFromTheBundleInstead()
    {
        using var g = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, new ModProject { RootDir = g.Root }, StockBundle(g, 128, 64));
        var gone = new WorkbenchMapVm("Base color", "_BaseMap", "c_stem_body1_d", "bundle1")
        { Subject = Subject, AuthoredPath = g.At("never-written.png") };

        await vm.HandleDropAsync(new[] { TestImages.WritePng(g.At("small.png"), 16, 16) }, gone);

        Assert.Equal("16×16, the map is 128×64. It still applies; UVs stretch it to fit.",
            shell.LastConfirmSizeNote);
    }

    // the workspace name every producer writes: <name>.<bundle>.<subject>.png
    private const string EditedGameRel = "textures/c_stem_body1_d.bundle1.char_stem.png";
    private const string EditedGameOrigRel = "originals/c_stem_body1_d.bundle1.char_stem.png";

    /// <summary>A deobfuscate that answers "bundle1" with a real synthetic bundle carrying the stock
    /// <c>c_stem_body1_d</c> at the given size — so the size read runs the production BundleReader path
    /// rather than stopping at a null.</summary>
    private static Func<string, byte[]?> StockBundle(TempGame g, int width, int height)
    {
        var path = g.At("stock.bundle");
        Directory.CreateDirectory(g.Root);
        SyntheticBundle.BuildOneTexture(path, "c_stem_body1_d", width, height);
        var bytes = File.ReadAllBytes(path);
        return id => id == "bundle1" ? bytes : null;
    }

    /// <summary>A game map card hung in a one-part, one-material tree and settled by
    /// <see cref="WorkbenchVm.RefreshNodeStates"/>, so its edited flag is the project's verdict rather than
    /// a value the test set.</summary>
    private static WorkbenchMapVm GameCardInATree(WorkbenchVm vm)
    {
        var map = GameCard();
        var material = new WorkbenchNodeVm
        { Kind = WorkbenchNodeKind.Material, Title = "mat0", Subject = Subject, PartToken = "body", MaterialIndex = 0 };
        material.Maps.Add(map);
        var part = new WorkbenchNodeVm
        { Kind = WorkbenchNodeKind.Part, Title = "body", Subject = Subject, PartToken = "body" };
        part.Children.Add(material);
        var subject = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "subject", Subject = Subject };
        subject.Children.Add(part);
        vm.Nodes.Add(subject);
        vm.RefreshNodeStates();
        return map;
    }

    /// <summary>The drag-over cursor reads the same rule the drop enforces, so no card offers a copy cursor
    /// and then refuses. Silent by contract: a hover must not write the status line.</summary>
    [Fact]
    public void CanAcceptDrop_MatchesTheRowsTheDropAccepts()
    {
        var vm = NewVm(new RecordingShell());
        var orphan = new WorkbenchMapVm("Base color", "_BaseMap", "body_s0_base", "") { Subject = Subject };

        Assert.True(vm.CanAcceptDrop(GameCard()));    // a bundle to materialize over
        Assert.True(vm.CanAcceptDrop(DonorCard()));   // an authored file to overwrite
        Assert.False(vm.CanAcceptDrop(orphan));       // neither — the drop refuses, so the cursor must too
        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task RowWithBundleAndAuthoredPath_CardDrop_ReplacesTheAuthoredFile()
    {
        // same rule as Open: the authored PNG is what ships for a replaced part
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell);
        var both = new WorkbenchMapVm("Base color", "_BaseMap", "c_stem_body1_d", "bundle1")
        { Subject = Subject, AuthoredPath = @"C:\mod\textures\body_s0_base.png" };

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("paint.png")) }, both);

        Assert.Equal(1, shell.ApplyAuthoredPngCalls);
        Assert.Equal(@"C:\mod\textures\body_s0_base.png", shell.LastAuthoredTarget);
        Assert.Equal(0, shell.ApplyPngCalls);
    }

    [Fact]
    public async Task BundlelessRow_WithNoAuthoredFile_CardDrop_RefusedBeforeTheConfirm()
    {
        var shell = new RecordingShell();
        var vm = NewVm(shell);
        var orphan = new WorkbenchMapVm("Base color", "_BaseMap", "body_s0_base", "") { Subject = Subject };

        await vm.HandleDropAsync(new[] { @"C:\tmp\paint.png" }, orphan);

        Assert.Equal(0, shell.ConfirmCalls);   // no pointless dialog
        Assert.Equal(0, shell.ApplyPngCalls);
        Assert.Equal(0, shell.ApplyAuthoredPngCalls);
        Assert.Contains("can't apply here", vm.Status);
    }

    [Fact]
    public void RowShowingAnAuthoredMap_RevertHint_NamesThePartsRevert()
    {
        // the map-grain Revert can't undo a map bound to the replacement; the hint has to name the verb that can
        Assert.False(DonorCard().CanRevert);
        // and it has to say what that verb costs beyond this map: the part's geometry edit goes with it
        Assert.Equal("This map belongs to the replacement. Revert the part to restore the game texture. "
            + "That discards the part's mesh edit too.", DonorCard().RevertHint);
    }

    // ---- the edited gate: donor state is read only off an edited mesh ----

    [Fact]
    public void UneditedMeshTarget_WithADonorRecord_LeavesTheGameRowsAlone()
    {
        // A record on a target whose workspace glb matches its original describes a mesh the mod no longer
        // carries. Without the edited gate it hangs an authored path on a game map row and puts the
        // send-back's material list over the renderer's slots.
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        string meshRel = Materializer.SubjectFolder("char", "stem") + "/body.glb";
        string origRel = Materializer.SubjectFolder("char", "stem") + "/originals/body.glb";
        const string authoredRel = "textures/body_s0_base.png";
        foreach (var rel in new[] { meshRel, origRel, authoredRel })
        {
            var abs = Path.Combine(g.Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, "same-bytes");   // workspace == original ⇒ not edited
        }
        project.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle1", ObjectName = "c_stem_slg_body_lod0",
            ReplaceFile = meshRel, OriginalFile = origRel, Edited = false,
            DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = authoredRel } },
            DonorMaterials = new List<string> { "M_donor" },
        });

        var vm = NewVm(new RecordingShell(), project);
        var map = GameCard();
        var material = new WorkbenchNodeVm
        { Kind = WorkbenchNodeKind.Material, Title = "M_game", Subject = Subject, PartToken = "body", MaterialIndex = 0 };
        material.Maps.Add(map);
        var part = new WorkbenchNodeVm
        { Kind = WorkbenchNodeKind.Part, Title = "body", Subject = Subject, PartToken = "body" };
        part.Children.Add(material);
        var subject = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "subject", Subject = Subject };
        subject.Children.Add(part);
        vm.Nodes.Add(subject);

        vm.RefreshNodeStates();

        Assert.Null(map.AuthoredPath);                                   // the game row keeps its game texture
        Assert.Same(material, Assert.Single(part.Children));             // and the renderer's material stands
        Assert.Equal("M_game", part.Children[0].Title);
        Assert.Empty(part.AuthoredBaseMaps);                             // nothing overlays the mesh preview
    }

    // ---- the donor swap falls through per SLOT ----

    /// <summary>A one-part tree whose mesh target the project counts as edited, carrying
    /// <paramref name="gameMaterials"/> game material children in renderer-slot order — each with a
    /// bundle-backed base-color card, plus a bundle-backed normal card when
    /// <paramref name="withNormal"/>. Each material's subtitle is the map count its cards add up to, as the
    /// real tree build writes it. The send-back record is set on the target for the test to move.</summary>
    private static (WorkbenchVm Vm, WorkbenchNodeVm Part, ProjectTarget Target, ModProject Project) EditedPartTree(
        TempGame g, int gameMaterials, bool withNormal = false, bool withRmo = false,
        RecordingShell? shell = null)
    {
        int mapCount = 1 + (withNormal ? 1 : 0) + (withRmo ? 1 : 0);
        var project = new ModProject { RootDir = g.Root };
        string meshRel = Materializer.SubjectFolder("char", "stem") + "/body.glb";
        string origRel = Materializer.SubjectFolder("char", "stem") + "/originals/body.glb";
        foreach (var rel in new[] { meshRel, origRel, DonorAlbedo(0), DonorAlbedo(1) })
        {
            var abs = Path.Combine(g.Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, "same-bytes");   // Edited is the flag's word, not a byte difference
        }
        var target = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle1", ObjectName = "c_stem_slg_body_lod0",
            ReplaceFile = meshRel, OriginalFile = origRel, Edited = true,
        };
        project.Targets.Add(target);

        var part = new WorkbenchNodeVm
        { Kind = WorkbenchNodeKind.Part, Title = "body", Subject = Subject, PartToken = "body" };
        for (int i = 0; i < gameMaterials; i++)
        {
            var material = new WorkbenchNodeVm
            {
                Kind = WorkbenchNodeKind.Material, Title = $"M_game{i}",
                Subtitle = mapCount == 1 ? "1 map" : $"{mapCount} maps",
                Subject = Subject, PartToken = "body", MaterialIndex = i,
            };
            material.Maps.Add(GameCard($"c_stem_body{i}_d"));
            if (withNormal) material.Maps.Add(NormalCard($"c_stem_body{i}_n"));
            if (withRmo) material.Maps.Add(RmoCard($"c_stem_body{i}_rmo"));
            part.Children.Add(material);
        }
        var subject = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "subject", Subject = Subject };
        subject.Children.Add(part);
        var vm = NewVm(shell ?? new RecordingShell(), project);
        vm.Nodes.Add(subject);
        return (vm, part, target, project);
    }

    private static string DonorAlbedo(int submesh) => $"textures/body_s{submesh}_base.png";

    /// <summary>Make the part's workspace glb differ from its original, which is the question every gate
    /// keyed on the BUILD's answer asks — the reconcile is happy with the target's own flag, the drop route
    /// is not.</summary>
    private static void MakeTheMeshReallyDiffer(TempGame g) =>
        File.WriteAllText(Path.Combine(g.Root, Materializer.SubjectFolder("char", "stem"), "body.glb"),
            "bytes the original does not have");

    private static List<SubmeshTextures> AuthoredRows(params int[] submeshes)
    {
        var rows = new List<SubmeshTextures>();
        foreach (var s in submeshes)
            rows.Add(new SubmeshTextures { Submesh = s, Albedo = DonorAlbedo(s) });
        return rows;
    }

    /// <summary>The geometry-only send-back: a mesh came back from Blender with no map authored on any
    /// submesh. Every game material has to stand exactly as it did, or the part's stock maps become
    /// invisible and uneditable behind a row of empty "inherits stock maps" placeholders.</summary>
    [Fact]
    public void GeometryOnlySendBack_LeavesEveryGameMaterialStanding()
    {
        using var g = new TempGame();
        var (vm, part, target, project) = EditedPartTree(g, gameMaterials: 2);
        var before = new[] { part.Children[0], part.Children[1] };
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        target.DonorTextures = null;

        vm.RefreshNodeStates();
        vm.RefreshNodeStates();   // the reconcile is idempotent: a second pass must not re-swap

        Assert.Equal(2, part.Children.Count);
        Assert.Same(before[0], part.Children[0]);
        Assert.Same(before[1], part.Children[1]);
        Assert.Equal("M_game0", part.Children[0].Title);
        Assert.True(part.Children[0].HasMaps);                   // the MAP SET block has rows to show
        Assert.Equal("bundle1", part.Children[0].Maps[0].BundleId);
        Assert.Null(part.Children[0].Maps[0].AuthoredPath);      // still the game texture, still editable
        Assert.True(part.Children[1].HasMaps);
    }

    /// <summary>A mixed send-back: one submesh authored in Blender, one left alone. Both keep their game
    /// material node; the authored index's own base-color CARD is what the send-back takes over.</summary>
    [Fact]
    public void MixedSendBack_DressesTheAuthoredSubmeshsOwnCard()
    {
        using var g = new TempGame();
        var (vm, part, target, project) = EditedPartTree(g, gameMaterials: 2);
        var authored = part.Children[0];
        var untouched = part.Children[1];
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        target.DonorTextures = AuthoredRows(0);

        vm.RefreshNodeStates();

        Assert.Equal(2, part.Children.Count);
        Assert.Same(authored, part.Children[0]);
        Assert.Equal(project.Resolve(DonorAlbedo(0)), part.Children[0].Maps[0].AuthoredPath);
        Assert.Same(untouched, part.Children[1]);
        Assert.Equal("bundle1", part.Children[1].Maps[0].BundleId);
        Assert.Null(part.Children[1].Maps[0].AuthoredPath);
    }

    /// <summary>The per-SLOT rule on a two-map game material: an albedo-only row takes over the base-color
    /// card and nothing else, so the submesh shows the authored base color AND its stock normal — the pair
    /// the build ships. A per-MATERIAL swap would replace the whole node and the stock normal would be gone
    /// from the tree, unopenable, with no UV guide and no way back.</summary>
    [Fact]
    public void AlbedoOnlyRow_ShowsTheAuthoredBaseColorBesideTheStockNormal()
    {
        using var g = new TempGame();
        var (vm, part, target, project) = EditedPartTree(g, gameMaterials: 1, withNormal: true);
        var material = part.Children[0];
        target.DonorMaterials = new List<string> { "M_donor0" };
        target.DonorTextures = AuthoredRows(0);

        vm.RefreshNodeStates();

        Assert.Same(material, Assert.Single(part.Children));
        Assert.Equal(2, material.Maps.Count);
        Assert.Equal("2 maps", material.Subtitle);               // the merged truth, not the authored count

        var baseColor = material.Maps[0];
        Assert.Equal(project.Resolve(DonorAlbedo(0)), baseColor.AuthoredPath);   // drop/open land on the PNG
        Assert.True(baseColor.HasEditBadge);

        var normal = material.Maps[1];
        Assert.Null(normal.AuthoredPath);
        Assert.Equal("bundle1", normal.BundleId);                // bundle identity: thumb, UV guide, revert
        Assert.True(normal.CanOpenUvGuide);
    }

    /// <summary>A submesh added in Blender has no game material behind it, so it gets the donor row that
    /// says what its maps do.</summary>
    [Fact]
    public void SendBackAddingASubmesh_GetsTheInheritsStockRowPastTheGameMaterials()
    {
        using var g = new TempGame();
        var (vm, part, target, project) = EditedPartTree(g, gameMaterials: 1);
        var only = part.Children[0];
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        target.DonorTextures = null;

        vm.RefreshNodeStates();

        Assert.Equal(2, part.Children.Count);
        Assert.Same(only, part.Children[0]);                     // the game material the mesh still has
        Assert.Equal("M_donor1", part.Children[1].Title);
        Assert.Equal("inherits stock maps", part.Children[1].Subtitle);
        Assert.False(part.Children[1].HasMaps);
    }

    /// <summary>Authoring a map for a submesh that had none reaches that index's own card, and every game
    /// material node stands through it. A revert then clears the authored paths and leaves the same nodes,
    /// thumbs and all.</summary>
    [Fact]
    public void AuthoringAFurtherSubmesh_ReachesThatIndexsCardAndKeepsEveryNode()
    {
        using var g = new TempGame();
        var (vm, part, target, project) = EditedPartTree(g, gameMaterials: 2);
        var game0 = part.Children[0];
        var game1 = part.Children[1];
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        target.DonorTextures = AuthoredRows(0);
        vm.RefreshNodeStates();
        Assert.Null(game1.Maps[0].AuthoredPath);

        target.DonorTextures = AuthoredRows(0, 1);   // same material names, one more authored index
        vm.RefreshNodeStates();

        Assert.Same(game1, part.Children[1]);
        Assert.Equal(project.Resolve(DonorAlbedo(1)), game1.Maps[0].AuthoredPath);

        // …and the revert path still restores the whole game surface
        target.Edited = false;
        target.DonorTextures = null;
        target.DonorMaterials = null;
        vm.RefreshNodeStates();

        Assert.Equal(2, part.Children.Count);
        Assert.Same(game0, part.Children[0]);
        Assert.Same(game1, part.Children[1]);
        Assert.Null(part.StashedGameChildren);
        Assert.Null(game1.Maps[0].AuthoredPath);
    }

    /// <summary>A send-back that added a submesh, then renamed only that submesh's material: the swap must
    /// touch index 2 alone. A kept node that leaves the collection and comes back loses the TreeView's
    /// selection, because the two-way SelectedItem binding writes null back on the removal.</summary>
    [Fact]
    public void RenamingOneAddedSubmeshsMaterial_LeavesTheKeptNodesInTheCollection()
    {
        using var g = new TempGame();
        var (vm, part, target, project) = EditedPartTree(g, gameMaterials: 2);
        var game0 = part.Children[0];
        var game1 = part.Children[1];
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1", "M_donor2" };
        vm.RefreshNodeStates();
        var added = part.Children[2];

        var touched = new List<WorkbenchNodeVm>();
        part.Children.CollectionChanged += (_, e) =>
        {
            foreach (var o in e.OldItems ?? (System.Collections.IList)Array.Empty<object>())
                touched.Add((WorkbenchNodeVm)o);
            foreach (var o in e.NewItems ?? (System.Collections.IList)Array.Empty<object>())
                touched.Add((WorkbenchNodeVm)o);
        };

        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1", "M_renamed2" };
        vm.RefreshNodeStates();

        Assert.Equal(3, part.Children.Count);
        Assert.Same(game0, part.Children[0]);
        Assert.Same(game1, part.Children[1]);
        Assert.Equal("M_renamed2", part.Children[2].Title);
        Assert.DoesNotContain(game0, touched);
        Assert.DoesNotContain(game1, touched);
        Assert.Contains(added, touched);
    }

    /// <summary>Two different donor shapes must never spell the same key: a material name carrying the
    /// separator characters a flat join uses would otherwise read as a longer list, and the reconcile would
    /// skip the swap that shape needs.</summary>
    [Fact]
    public void AMaterialNameCarryingSeparators_DoesNotAliasALongerShape()
    {
        using var g = new TempGame();
        var (vm, part, target, project) = EditedPartTree(g, gameMaterials: 2);
        target.DonorMaterials = new List<string> { "M_a\t--\nM_b" };   // ONE material, separators in its name
        vm.RefreshNodeStates();
        Assert.Single(part.Children);

        target.DonorMaterials = new List<string> { "M_a", "M_b" };     // two, and a flat join reads the same
        vm.RefreshNodeStates();

        Assert.Equal(2, part.Children.Count);
    }

    /// <summary>Revert after a geometry-only send-back: the children never left, and the part goes back to
    /// holding no stash.</summary>
    [Fact]
    public void RevertingAGeometryOnlySendBack_RestoresTheStashedSet()
    {
        using var g = new TempGame();
        var (vm, part, target, project) = EditedPartTree(g, gameMaterials: 2);
        var before = new[] { part.Children[0], part.Children[1] };
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        vm.RefreshNodeStates();

        target.Edited = false;
        target.DonorMaterials = null;
        vm.RefreshNodeStates();

        Assert.Equal(2, part.Children.Count);
        Assert.Same(before[0], part.Children[0]);
        Assert.Same(before[1], part.Children[1]);
        Assert.Null(part.StashedGameChildren);
        Assert.Equal("bundle1", part.Children[0].Maps[0].BundleId);
    }

    // ---- the watcher: an authored path doesn't hide the row's game texture ----

    [Fact]
    public void RowWithBundleAndAuthoredPath_GameTextureSave_StillRefreshesTheRow()
    {
        // The row is keyed BOTH ways, by authored file and by Texture2D target, so a save to the game
        // workspace PNG must still reach it.
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        string meshRel = Materializer.SubjectFolder("char", "stem") + "/body.glb";
        const string authoredRel = "textures/body_s0_base.png";
        const string gameRel = "textures/c_stem_body1_d.bundle1.char_stem.png";
        foreach (var rel in new[] { meshRel, authoredRel, gameRel })
        {
            var abs = Path.Combine(g.Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, "bytes");
        }
        project.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle1", ObjectName = "c_stem_slg_body_lod0",
            ReplaceFile = meshRel, Edited = true,
            DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = authoredRel } },
        });
        project.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bundle1", ObjectName = "c_stem_body1_d",
            ReplaceFile = gameRel, Edited = true,
            SubjectCharacter = "char", SubjectOutfit = "stem",
        });

        var vm = NewVm(new RecordingShell(), project);
        var map = new WorkbenchMapVm("Base color", "_BaseMap", "c_stem_body1_d", "bundle1")
        { Subject = Subject, AuthoredPath = project.Resolve(authoredRel) };
        var material = new WorkbenchNodeVm
        { Kind = WorkbenchNodeKind.Material, Title = "mat0", Subject = Subject, PartToken = "body", MaterialIndex = 0 };
        material.Maps.Add(map);
        var part = new WorkbenchNodeVm
        { Kind = WorkbenchNodeKind.Part, Title = "body", Subject = Subject, PartToken = "body" };
        part.Children.Add(material);
        var subject = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "subject", Subject = Subject };
        subject.Children.Add(part);
        vm.Nodes.Add(subject);

        vm.RefreshNodeStates();          // settle the authored path before the marker below
        map.MarkThumbFailed();

        vm.NotifyTextureFileChanged(project.Resolve(gameRel));

        Assert.False(map.IsThumbFailed);                       // the row was re-armed, not skipped
        Assert.True(map.IsThumbLoading);
        Assert.Equal(project.Resolve(authoredRel), map.AuthoredPath);   // and it still shows the authored image
    }

    // ---- the map set's cause line ----

    [Fact]
    public void MapSetNote_SeparatesAMissingAuthoredFileFromAHeldGameFile()
    {
        var material = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Material, Title = "mat0" };
        var card = DonorCard();
        material.Maps.Add(card);
        card.MarkThumbFailed();

        Assert.Contains("The game may be holding files", material.ThumbFailureNote);

        card.IsAuthoredFileMissing = true;   // set by the preview pass when the file check fails

        // either route can have bound that map, and the card doesn't record which — so both ways back are named
        Assert.Equal("Previews unavailable. A map the replacement carries is gone. "
            + "Send the part back from Blender, or drop a .png on the card.",
            material.ThumbFailureNote);
    }

    // ---- Materialize-all: a donor row is not a game texture ----

    [Fact]
    public void CollectMaterializeItems_SkipsDonorRows_KeepsGameRows()
    {
        var part = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Part, Title = "body", PartToken = "body" };
        var material = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Material, Title = "mat0" };
        material.Maps.Add(GameCard("c_stem_body1_d"));
        material.Maps.Add(DonorCard());
        part.Children.Add(material);
        var subject = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "subject" };
        subject.Children.Add(part);

        var items = WorkbenchVm.CollectMaterializeItems(subject);

        var textures = items.FindAll(i => i.IsTexture);
        Assert.Single(textures);
        Assert.Equal("c_stem_body1_d", textures[0].Token);
        Assert.DoesNotContain(textures, t => t.BundleId.Length == 0);
    }

    // ---- the RMO's channel legend ----

    /// <summary>"RMO" names a packed map without saying what it packs, so the role line carries the legend.
    /// Every other role name says everything, and carries no tip. A donor-derived row is built through the
    /// same factory, so both kinds of card say the same thing.</summary>
    [Theory]
    [InlineData("_RMOTex", true)]
    [InlineData("_BaseMap", false)]
    [InlineData("_BumpMap", false)]
    public void OnlyAnRmoRow_CarriesTheChannelLegend(string slot, bool legend)
    {
        var game = WorkbenchNodeVm.MapRow(new SubjectMap(slot, "c_stem_body1_r", "bundle1"));
        var donor = WorkbenchNodeVm.MapRow(new SubjectMap(slot, "body_s0_rmo.png", ""));

        Assert.Equal(legend ? WorkbenchMapVm.RmoCardInfo : null, game.MapInfo);
        Assert.Equal(game.MapInfo, donor.MapInfo);
    }

    [Fact]
    public void TheChannelLegend_NamesEveryChannelTheGameMapPacks()
    {
        Assert.Equal("R roughness · G metallic · B occlusion · A emissive mask (specular level on stocking parts)",
            WorkbenchMapVm.RmoChannels);
    }

    [Fact]
    public void AnRmoRowTheSlotDidntName_StillReadsAsOne()
    {
        // the label falls back to the texture-name suffix, and the legend and the opaque thumb both follow
        // the label the card shows, not the shader slot, which says nothing here
        var row = WorkbenchNodeVm.MapRow(new SubjectMap("_SomeOtherTex", "c_stem_body1_rmo", "bundle1"));

        Assert.Equal("RMO", row.MapLabel);
        Assert.True(row.IsRmo);
        Assert.Equal(WorkbenchMapVm.RmoCardInfo, row.MapInfo);
    }

    /// <summary>The legend names four channels and the tile above it forces alpha opaque, so it shows three. A
    /// legend that named the mask without saying that leaves the modder looking for it in the picture.</summary>
    [Fact]
    public void AMapCardsLegend_SaysTheThumbnailLeavesTheMaskOut()
    {
        Assert.Equal(WorkbenchMapVm.RmoChannels + ". The thumbnail shows RGB only.",
            WorkbenchMapVm.RmoCardInfo);
        // the change-list chip has no tile, so it carries the channels alone
        var chip = new Remold.App.ViewModels.BuildRowVm(new MeshEdit
        {
            Character = "char", Outfit = "stem", Mesh = "c_stem_slg_body_lod0", Verb = EditVerbs.Replace,
        }, "body", included: true).Chips.Single(c => c.Label == TextureMap.RmoLabel);
        Assert.EndsWith(WorkbenchMapVm.RmoChannels, chip.Tip);
        Assert.DoesNotContain("thumbnail", chip.Tip);
    }

    [Fact]
    public void ANonRmoRow_IsNotCompositedOpaque()
    {
        var row = WorkbenchNodeVm.MapRow(new SubjectMap("_BumpMap", "c_stem_body1_n", "bundle1"));

        Assert.False(row.IsRmo);
        Assert.Null(row.MapInfo);
    }

    // ---- the blanked normal slot ----

    [Fact]
    public void ANormalSlotBlankedByTheNeutralGesture_SaysSoOnItsCard()
    {
        // the gesture names no file, so the card still shows the game texture: without this the only
        // visible difference between a blanked slot and an untouched one is none
        using var g = new TempGame();
        var (vm, part, target, _) = EditedPartTree(g, gameMaterials: 1, withNormal: true);
        target.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, NormalOrigin = SlotOrigin.ExplicitNeutral },
        };

        vm.RefreshNodeStates();

        var maps = part.Children[0].Maps;
        Assert.True(maps.Single(m => m.SlotName == "_BumpMap").IsBlanked);
        Assert.False(maps.Single(m => m.SlotName == "_BaseMap").IsBlanked);   // its own slot asked for nothing
    }

    [Fact]
    public void AnAlbedoOnlySendBack_BlanksTheNormalAndRmoCardsItLeftEmpty()
    {
        // The build's rule, not the gesture's: a submesh that asked for ANYTHING draws on donor UVs, so the
        // normal and RMO it named no file for ship flat rather than keeping the part's stock maps. Cards
        // reading those two as untouched would describe a build that isn't the one running.
        using var g = new TempGame();
        var (vm, part, target, _) = EditedPartTree(g, gameMaterials: 1, withNormal: true, withRmo: true);
        target.DonorTextures = AuthoredRows(0);   // albedo only; normal and RMO origins stay None

        vm.RefreshNodeStates();

        var maps = part.Children[0].Maps;
        Assert.True(maps.Single(m => m.SlotName == "_BumpMap").IsBlanked);
        Assert.True(maps.Single(m => m.SlotName == "_RMOTex").IsBlanked);
        Assert.False(maps.Single(m => m.SlotName == "_BaseMap").IsBlanked);   // it named a file: authored
    }

    [Fact]
    public void ABlankedRmoSlot_SaysSoOnItsCardToo()
    {
        // the neutral gesture is not the normal slot's alone; an explicitly blanked RMO ships flat the same
        // way, and its own asking is what pulls the file-less normal flat beside it
        using var g = new TempGame();
        var (vm, part, target, _) = EditedPartTree(g, gameMaterials: 1, withNormal: true, withRmo: true);
        target.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, RmoOrigin = SlotOrigin.ExplicitNeutral },
        };

        vm.RefreshNodeStates();

        var maps = part.Children[0].Maps;
        Assert.True(maps.Single(m => m.SlotName == "_RMOTex").IsBlanked);
        Assert.True(maps.Single(m => m.SlotName == "_BumpMap").IsBlanked);
        Assert.False(maps.Single(m => m.SlotName == "_BaseMap").IsBlanked);   // no flat albedo stands in
    }

    [Fact]
    public void AnAlbedoPluggedWithTheNeutral_IsNeverShownAsBlanked()
    {
        // No flat albedo exists to stand in for a base color, and the emitter refuses a row that asks for one.
        // The card must not advertise a state the build rejects.
        using var g = new TempGame();
        var (vm, part, target, _) = EditedPartTree(g, gameMaterials: 1, withNormal: true, withRmo: true);
        target.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, AlbedoOrigin = SlotOrigin.ExplicitNeutral },
        };

        vm.RefreshNodeStates();

        var maps = part.Children[0].Maps;
        Assert.False(maps.Single(m => m.SlotName == "_BaseMap").IsBlanked);
        // its asking is still an ask, so the two relief slots it named no file for do go flat
        Assert.True(maps.Single(m => m.SlotName == "_BumpMap").IsBlanked);
        Assert.True(maps.Single(m => m.SlotName == "_RMOTex").IsBlanked);
    }

    [Fact]
    public void ABlankedCard_NamesThePartRevertAsTheWayBack()
    {
        // A blanked slot has no map-grain way back — the mesh edit put it there. "Nothing to revert yet" is
        // the tooltip of a card with nothing on it, which this one is not.
        using var g = new TempGame();
        var (vm, part, target, _) = EditedPartTree(g, gameMaterials: 1, withNormal: true);
        target.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, NormalOrigin = SlotOrigin.ExplicitNeutral },
        };

        vm.RefreshNodeStates();

        var normal = part.Children[0].Maps.Single(m => m.SlotName == "_BumpMap");
        Assert.False(normal.CanRevert);
        Assert.Equal("This slot was blanked by the mesh edit. Revert the part to restore the game texture. "
            + "That discards the part's mesh edit too.", normal.RevertHint);
        // an untouched card beside it still says what an empty card says
        Assert.Equal("Nothing to revert yet",
            part.Children[0].Maps.Single(m => m.SlotName == "_BaseMap").RevertHint);
    }

    // ---- a donor-derived material's own blanked slots ----

    /// <summary>A submesh past the game's material count gets a donor-derived node, and there is no stock card
    /// standing on it to carry a blanked slot's word. Without rows of its own the change-list chip is the only
    /// place the build's flat normal and RMO are visible at all.</summary>
    [Fact]
    public void ADonorMaterialsFlatSlots_GetCardsOfTheirOwn()
    {
        using var g = new TempGame();
        var (vm, part, target, project) = EditedPartTree(g, gameMaterials: 1);
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        target.DonorTextures = AuthoredRows(0, 1);   // albedo only on both submeshes

        vm.RefreshNodeStates();

        var added = part.Children[1];
        Assert.Equal("M_donor1", added.Title);
        // the authored base color, plus the two relief slots its asking pulls flat
        Assert.Equal(new[] { "_BaseMap", "_BumpMap", "_RMOTex" }, added.Maps.Select(m => m.SlotName));
        Assert.Equal(project.Resolve(DonorAlbedo(1)), added.Maps[0].AuthoredPath);
        Assert.False(added.Maps[0].IsBlanked);
        Assert.True(added.Maps[1].IsBlanked);
        Assert.True(added.Maps[2].IsBlanked);
        // and the labels are the Edit pane's own vocabulary, not a suffix guess off an absent file name
        Assert.Equal(new[] { TextureMap.BaseColorLabel, TextureMap.NormalLabel, TextureMap.RmoLabel },
            added.Maps.Select(m => m.MapLabel));
        Assert.Equal("1 map · 2 blanked", added.Subtitle);
        Assert.Null(added.InspectorNote);
    }

    /// <summary>A submesh whose ONLY ask is a blank still earns a row. Its material has no image at all, so
    /// "inherits stock maps" would be a plain contradiction of what the build does there.</summary>
    [Fact]
    public void ADonorMaterialThatOnlyBlanks_StillSaysSo()
    {
        using var g = new TempGame();
        var (vm, part, target, _) = EditedPartTree(g, gameMaterials: 1);
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        target.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 1, NormalOrigin = SlotOrigin.ExplicitNeutral },
        };

        vm.RefreshNodeStates();

        var added = part.Children[1];
        // the explicit blank, plus the RMO its asking pulls flat; no flat albedo stands in for a base color
        Assert.Equal(new[] { "_BumpMap", "_RMOTex" }, added.Maps.Select(m => m.SlotName));
        Assert.All(added.Maps, m => Assert.True(m.IsBlanked));
        Assert.Equal("2 blanked", added.Subtitle);
    }

    /// <summary>The reconcile keys on what each slot ASKS for, not on which named a file: a record that
    /// changes only an origin changes the rows a donor material needs, and a file-presence key would spell the
    /// same shape and skip the swap.</summary>
    [Fact]
    public void ARecordThatOnlyChangesAnOrigin_StillReshapesTheDonorMaterial()
    {
        using var g = new TempGame();
        var (vm, part, target, _) = EditedPartTree(g, gameMaterials: 1);
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        target.DonorTextures = null;
        vm.RefreshNodeStates();
        Assert.Empty(part.Children[1].Maps);

        // no file named either way — only the ask moved
        target.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 1, RmoOrigin = SlotOrigin.ExplicitNeutral },
        };
        vm.RefreshNodeStates();

        Assert.Equal(new[] { "_BumpMap", "_RMOTex" }, part.Children[1].Maps.Select(m => m.SlotName));
    }

    /// <summary>Open on a blanked donor row: there is no image behind it, which is a different answer from an
    /// authored file that went missing. The missing-file line names a file, and this row has none to name.</summary>
    [Fact]
    public async Task OpenOnABlankedDonorRow_SaysThereIsNoImageRatherThanNamingNothing()
    {
        using var g = new TempGame();
        var (vm, part, target, _) = EditedPartTree(g, gameMaterials: 1);
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        target.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 1, NormalOrigin = SlotOrigin.ExplicitNeutral },
        };
        vm.RefreshNodeStates();
        var blanked = part.Children[1].Maps.Single(m => m.SlotName == "_BumpMap");

        await vm.OpenMapCommand.ExecuteAsync(blanked);

        Assert.Equal(WorkbenchVm.BlankedSlotNotEditable, vm.Status);
    }

    // ---- a donor-added submesh's cards take a drop of their own ----

    /// <summary>A submesh the edit ADDED has no game material behind it, so its cards carry neither a bundle
    /// nor — until something lands there — a file. It still belongs to a replaced part, and its slot is one
    /// the build ships, so a drop authors it exactly as a card on a game material does. Without the part and
    /// the submesh on the row, the cursor refuses over it and says nothing.</summary>
    [Fact]
    public async Task ADropOnADonorAddedRowsCard_AuthorsThatSubmeshsSlot()
    {
        using var g = new TempGame();
        var shell = new RecordingShell();
        var (vm, part, target, _) = EditedPartTree(g, gameMaterials: 1, shell: shell);
        MakeTheMeshReallyDiffer(g);
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        target.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 1, NormalOrigin = SlotOrigin.ExplicitNeutral },
        };
        vm.RefreshNodeStates();
        var blanked = part.Children[1].Maps.Single(m => m.SlotName == "_BumpMap");
        Assert.False(blanked.HasBundle);
        Assert.Null(blanked.AuthoredPath);
        Assert.True(vm.CanAcceptDrop(blanked));   // the cursor offers it, so the drop has to take it

        await vm.HandleDropAsync(new[] { TestImages.WritePng(g.At("relief.png")) }, blanked);

        Assert.Equal(1, shell.ApplyDonorMapCalls);
        Assert.Equal("body", shell.LastDonorDrop!.PartToken);
        Assert.Equal(DonorMapSlot.Normal, shell.LastDonorDrop.Slot);
        Assert.Equal(new[] { 1 }, shell.LastDonorDrop.Submeshes);   // its own index IS the landing set
        Assert.Equal(0, shell.ApplyPngCalls);
    }

    /// <summary>A donor-added card whose slot ALREADY names a file keeps the authored route: that file is
    /// what the build ships for the submesh, and rewriting it in place changes nothing else.</summary>
    [Fact]
    public async Task ADropOnADonorAddedRowsAuthoredCard_KeepsTheAuthoredRoute()
    {
        using var g = new TempGame();
        var shell = new RecordingShell();
        var (vm, part, target, project) = EditedPartTree(g, gameMaterials: 1, shell: shell);
        target.DonorMaterials = new List<string> { "M_donor0", "M_donor1" };
        target.DonorTextures = AuthoredRows(0, 1);
        vm.RefreshNodeStates();
        var added = part.Children[1].Maps.Single(m => m.SlotName == "_BaseMap");

        await vm.HandleDropAsync(new[] { TestImages.WritePng(g.At("skin.png")) }, added);

        Assert.Equal(1, shell.ApplyAuthoredPngCalls);
        Assert.Equal(project.Resolve(DonorAlbedo(1)), shell.LastAuthoredTarget);
        Assert.Equal("body", shell.LastAuthoredPart);
        Assert.Equal(0, shell.ApplyDonorMapCalls);
    }

    [Fact]
    public void ABlankedNormal_ReadsAsUnblankedAgainOnceTheRecordGoes()
    {
        // a revert empties the record; the word has to go with it, not survive as the card's last state
        using var g = new TempGame();
        var (vm, part, target, _) = EditedPartTree(g, gameMaterials: 1, withNormal: true);
        target.DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, NormalOrigin = SlotOrigin.ExplicitNeutral },
        };
        vm.RefreshNodeStates();

        target.DonorTextures = null;
        vm.RefreshNodeStates();

        Assert.False(part.Children[0].Maps.Single(m => m.SlotName == "_BumpMap").IsBlanked);
    }
}
