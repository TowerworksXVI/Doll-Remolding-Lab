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
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The VM-side drop routing: a single <c>.png</c> dropped ON a map card applies to THAT card's texture
/// regardless of filename, behind a confirm. That is the ONLY apply path — no card, several files, or any
/// non-png is refused on the status line with no shell call. The card hit-testing itself is in the view, so
/// these drive the VM with a resolved card (or a deliberate null for the no-card case).
/// </summary>
public class WorkbenchDropRoutingTests
{
    private sealed class RecordingShell : IWorkbenchShell
    {
        public bool ConfirmResult = true;
        public int ConfirmCalls;
        public int ApplyPngCalls;
        public string? LastApplyTexture;
        public DroppedPngConfirm? LastConfirm;

        public string? LastConfirmFile => LastConfirm?.FileName;
        public string? LastConfirmRole => LastConfirm?.MapRole;
        public string? LastConfirmTexture => LastConfirm?.TextureName;
        public bool LastConfirmAuthored => LastConfirm?.IsAuthored ?? false;
        public string? LastConfirmSizeNote => LastConfirm?.SizeNote;
        public DonorMapDrop? LastConfirmDonor => LastConfirm?.Donor;

        public int ApplyDonorMapCalls;
        public DonorMapDrop? LastDonorDrop;
        public string? LastDonorRole;

        public Task<bool> ConfirmApplyDroppedPngAsync(DroppedPngConfirm ask)
        {
            ConfirmCalls++;
            LastConfirm = ask;
            return Task.FromResult(ConfirmResult);
        }

        public Task ApplyDroppedPngToDonorMapAsync(WorkbenchSubjectRef subject, DonorMapDrop donor,
            string mapRole, string path, IProgress<string> status)
        {
            ApplyDonorMapCalls++;
            LastDonorDrop = donor;
            LastDonorRole = mapRole;
            return Task.CompletedTask;
        }

        public Task ApplyDroppedPngAsync(WorkbenchSubjectRef subject, string textureName, string bundleId,
            IReadOnlyList<string> ownerPartTokens, string path, IProgress<string> status)
        {
            ApplyPngCalls++;
            LastApplyTexture = textureName;
            return Task.CompletedTask;
        }

        public int ApplyAuthoredPngCalls;

        // ---- unused by these routing tests ----
        public string? LastAuthoredPart;
        public string? LastAuthoredRole;

        public Task ApplyDroppedPngToAuthoredAsync(string authoredPath, string partToken, string mapRole,
            string path, IProgress<string> status)
        { ApplyAuthoredPngCalls++; LastAuthoredPart = partToken; LastAuthoredRole = mapRole; return Task.CompletedTask; }
        public Task<PartMaterializeOutcome> MaterializePartAsync(WorkbenchSubjectRef s, RecipePart r, IProgress<string> p, CancellationToken c) => Task.FromResult(PartMaterializeOutcome.Ready());
        public Task<bool> MaterializeTextureAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<string> o, IProgress<string> p, CancellationToken c) => Task.FromResult(true);
        public Task OpenPartInBlenderAsync(WorkbenchSubjectRef s, RecipePart r, IReadOnlyList<RecipePart> outfit, IProgress<string> p) => Task.CompletedTask;
        public Task OpenPartAloneInBlenderAsync(WorkbenchSubjectRef s, RecipePart r, IProgress<string> p) => Task.CompletedTask;
        public Task OpenAllPartsInBlenderAsync(WorkbenchSubjectRef s, IReadOnlyList<RecipePart> r, IProgress<string> p) => Task.CompletedTask;
        public Task OpenMapInEditorAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<string> o, IProgress<string> p) => Task.CompletedTask;
        public Task OpenAuthoredMapAsync(string authoredPath, IProgress<string> p) => Task.CompletedTask;
        public Task<int> MaterializeAllAsync(WorkbenchSubjectRef s, IReadOnlyList<MaterializeItem> i, IProgress<string> p, CancellationToken c) => Task.FromResult(0);
        public Task RevertPartAsync(WorkbenchSubjectRef s, string t, IProgress<string> p) => Task.CompletedTask;
        public Task OpenMapUvGuideAsync(WorkbenchSubjectRef subj, string t, string b, IReadOnlyList<(string, string, int, string?)> s, IProgress<string> p) => Task.CompletedTask;
        public Task RevertMapAsync(WorkbenchSubjectRef subj, string t, string b, IProgress<string> p) => Task.CompletedTask;
        public void PrewarmSubject(WorkbenchSubjectRef s) { }
        public void ShowSubjectInFolder(WorkbenchSubjectRef s) { }
        public Task RemoveSubjectAsync(WorkbenchSubjectRef s) => Task.CompletedTask;
        public Task CopyTextAsync(string? text) => Task.CompletedTask;
        public void GoToBuild() { }
        public void AutoSaveProject() { }
    }

    private static WorkbenchVm NewVm(RecordingShell shell, ModProject? project = null) => new(
        project: () => project ?? new ModProject(),
        vfs: () => null,
        friendly: () => FriendlyNames.Empty,
        roster: () => Array.Empty<Character>(),
        tryDeobfuscate: _ => null,
        catalog: null,
        shell: shell);

    private static WorkbenchMapVm Card(string textureName) => new("Base", "_MainTex", textureName, "bundle1")
    {
        Subject = new WorkbenchSubjectRef("char", "stem", "c_stem_slg_",
            new Outfit(0, "stem", OutfitKind.Base)),
    };

    [Fact]
    public async Task CardDrop_Png_ConfirmAccepted_AppliesToThatTexture_RegardlessOfFilename()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell { ConfirmResult = true };
        var vm = NewVm(shell);
        var card = Card("tex_face");

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("wrongname.png")) }, card);

        Assert.Equal(1, shell.ConfirmCalls);
        Assert.Equal("wrongname.png", shell.LastConfirmFile);
        Assert.Equal("Base", shell.LastConfirmRole);        // the card's own role, so the dialog names the card
        Assert.Equal("tex_face", shell.LastConfirmTexture);
        Assert.False(shell.LastConfirmAuthored);            // a game row: replacing it is revertible
        Assert.Equal(1, shell.ApplyPngCalls);
        Assert.Equal("tex_face", shell.LastApplyTexture);   // applied to the CARD's texture, not the filename
    }

    [Fact]
    public async Task CardDrop_Png_ConfirmDeclined_SaysNothingApplied()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell { ConfirmResult = false };
        var vm = NewVm(shell);

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("face.png")) }, Card("tex_face"));

        Assert.Equal(1, shell.ConfirmCalls);
        Assert.Equal(0, shell.ApplyPngCalls);        // declined → no apply
        // A declined confirm must not leave the PREVIOUS line standing — an old "Applied …" reads as success.
        Assert.Equal("Nothing applied.", vm.Status);
    }

    /// <summary>The extension is not evidence. A JPEG saved as <c>.png</c> gets past the payload check and
    /// would be copied over the modder's map, where the card and the build would both read it as a PNG.</summary>
    [Fact]
    public async Task CardDrop_RenamedJpeg_IsRefusedBeforeTheConfirm()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell);

        await vm.HandleDropAsync(new[] { TestImages.WriteJpegNamed(temp.At("face.png")) }, Card("tex_face"));

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.ApplyPngCalls);
        Assert.Equal("face.png isn't a readable .png.", vm.Status);
    }

    [Fact]
    public async Task CardDrop_FileThatIsNotThere_IsRefusedBeforeTheConfirm()
    {
        var shell = new RecordingShell();
        var vm = NewVm(shell);

        await vm.HandleDropAsync(new[] { @"C:\tmp\vanished.png" }, Card("tex_face"));

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.ApplyPngCalls);
        Assert.Equal("vanished.png isn't a readable .png.", vm.Status);
    }

    [Fact]
    public async Task CardDrop_NonPng_IsRefused_NoConfirm()
    {
        var shell = new RecordingShell();
        var vm = NewVm(shell);

        // A .glb applies nowhere, card or not: one status line, no dialog, no shell call.
        await vm.HandleDropAsync(new[] { @"C:\tmp\body.glb" }, Card("tex_face"));

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.ApplyPngCalls);
        Assert.Equal("Only a .png dropped on a texture card applies.", vm.Status);
    }

    [Fact]
    public async Task CardDrop_WhileBusy_ReportsBusy_NoConfirm()
    {
        var shell = new RecordingShell();
        var vm = NewVm(shell);
        var card = Card("tex_face");
        card.IsBusy = true;   // the card's own verb is already running

        await vm.HandleDropAsync(new[] { @"C:\tmp\face.png" }, card);   // never read: the gate refuses first

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.ApplyPngCalls);
        Assert.Contains("Busy", vm.Status);
    }

    [Fact]
    public async Task MultiFilePngDrop_OnCard_IsRefused()
    {
        // More than one file is a batch, not a targeted card drop — and a batch has no single card to mean.
        var shell = new RecordingShell();
        var vm = NewVm(shell);

        await vm.HandleDropAsync(new[] { @"C:\tmp\a.png", @"C:\tmp\b.png" }, Card("tex_face"));

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.ApplyPngCalls);
        Assert.Equal("Only a .png dropped on a texture card applies.", vm.Status);
    }

    // ---- a card on a REPLACED part: the drop authors the replacement's own map ----

    /// <summary>A card of a part that carries a Replace: the stock texture it names is not what the part
    /// draws any more, so the drop has to become the replacement's donor map.</summary>
    private static WorkbenchMapVm PartCard(string textureName, string slot = "_BaseMap",
        string label = "Base color", params int[] submeshes) =>
        new(label, slot, textureName, "bundle1")
        {
            Subject = new WorkbenchSubjectRef("char", "stem", "c_stem_slg_",
                new Outfit(0, "stem", OutfitKind.Base)),
            PartToken = "body1",
            BoundSubmeshes = submeshes.Length > 0 ? submeshes : new[] { 0 },
        };

    /// <summary>A project holding one REPLACED part of the subject: a mesh target with no <c>originals/</c>
    /// copy on record, which is what an edited mesh reads as, plus the <paramref name="donorSubmeshes"/>
    /// donor materials its send-back recorded.</summary>
    /// <param name="authoredAlbedo">the submeshes whose base colour a send-back or an earlier drop already
    /// named a file for.</param>
    private static ModProject ReplacedPart(int donorSubmeshes = 1, int[]? authoredAlbedo = null) => new()
    {
        Targets =
        {
            new ProjectTarget
            {
                AssetType = "Mesh", Bundle = "aa", ObjectName = "c_stem_slg_body1_lod0",
                ReplaceFile = "char_stem/meshes/body1.glb",
                SubjectCharacter = "char", SubjectOutfit = "stem",
                DonorMaterials = Enumerable.Range(0, donorSubmeshes).Select(i => $"M_{i}").ToList(),
                DonorTextures = authoredAlbedo is null ? null : authoredAlbedo
                    .Select(i => new SubmeshTextures
                    {
                        Submesh = i, Albedo = $"textures/body1_s{i}_base.png",
                        AlbedoOrigin = SlotOrigin.Authored,
                    })
                    .ToList(),
            },
        },
    };

    /// <summary>The same part MATERIALIZED but not replaced: its workspace glb still matches its
    /// <c>originals/</c> copy byte for byte, which is the question the edited flag is.</summary>
    private static ModProject UnreplacedPart(TempGame temp)
    {
        var project = ReplacedPart();
        project.RootDir = temp.Root;
        var t = project.Targets[0];
        t.OriginalFile = "char_stem/originals/body1.glb";
        foreach (var rel in new[] { t.ReplaceFile, t.OriginalFile })
        {
            var abs = Path.Combine(temp.Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, "same bytes");
        }
        return project;
    }

    [Fact]
    public async Task CardDrop_OnAReplacedPart_AuthorsTheReplacementsMap_NotTheGameTexture()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart());

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) }, PartCard("c_stem_body1_d"));

        Assert.Equal(1, shell.ApplyDonorMapCalls);
        Assert.Equal(0, shell.ApplyPngCalls);              // never the game-texture route
        Assert.Equal("body1", shell.LastDonorDrop!.PartToken);
        Assert.Equal(DonorMapSlot.BaseColor, shell.LastDonorDrop.Slot);
        Assert.Equal(new[] { 0 }, shell.LastDonorDrop.Submeshes);
        // the confirm has to describe what happens, since the card still shows the game texture's name
        Assert.NotNull(shell.LastConfirmDonor);
    }

    [Fact]
    public async Task CardDrop_OnAReplacedPart_TakesTheSlotKindFromTheCard()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart());

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("rough.png")) },
            PartCard("c_stem_body1_rmo", "_RMOTex", "RMO"));

        Assert.Equal(DonorMapSlot.Rmo, shell.LastDonorDrop!.Slot);
    }

    [Fact]
    public async Task CardDrop_OnAReplacedPart_CoversEverySubmeshTheStockMapDresses()
    {
        // One stock map bound by two of the part's material slots is one image on two donor submeshes —
        // dropping on either card has to author both, or the second keeps drawing the stock map.
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart(donorSubmeshes: 3));

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) },
            PartCard("c_stem_body1_d", submeshes: new[] { 0, 2 }));

        Assert.Equal(new[] { 0, 2 }, shell.LastDonorDrop!.Submeshes);
    }

    [Fact]
    public async Task CardDrop_OnAReplacedPart_WhoseSubmeshTheDonorLacks_IsRefusedBeforeTheConfirm()
    {
        // The build throws on a texture set past the donor's submesh count, so the drop refuses first.
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart(donorSubmeshes: 1));

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) },
            PartCard("c_stem_body1_d", submeshes: new[] { 2 }));

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.ApplyDonorMapCalls);
        Assert.Equal(0, shell.ApplyPngCalls);
        // the refusal says what is true of the replacement and what puts the submeshes there
        Assert.Equal("skin.png can't apply here. This map dresses submeshes body1's replacement doesn't have. "
            + "Send body1 back from Blender to add them.", vm.Status);
    }

    [Fact]
    public async Task CardDrop_OnAnUNreplacedPart_StillEditsTheGameTexture()
    {
        // The part is materialized but carries no mesh edit, so its vanilla draws are what the mod ships.
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, UnreplacedPart(temp));

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) }, PartCard("c_stem_body1_d"));

        Assert.Equal(1, shell.ApplyPngCalls);
        Assert.Equal(0, shell.ApplyDonorMapCalls);
        Assert.Null(shell.LastConfirmDonor);
    }

    [Fact]
    public async Task CardDrop_WithNoPartContext_StillEditsTheGameTexture()
    {
        // A card built outside a part's tree names no part, so there is no replacement to author for.
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart());

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) }, Card("c_stem_body1_d"));

        Assert.Equal(1, shell.ApplyPngCalls);
        Assert.Equal(0, shell.ApplyDonorMapCalls);
    }

    [Fact]
    public async Task CardDrop_OnAnAlreadyAuthoredCardOfAReplacedPart_KeepsTheAuthoredRoute()
    {
        // That file IS the record the build ships; overwriting it in place changes nothing else.
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart());
        var card = PartCard("c_stem_body1_d");
        card.AuthoredPath = temp.At("body1_s0_base.png");

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) }, card);

        Assert.Equal(1, shell.ApplyAuthoredPngCalls);
        Assert.Equal(0, shell.ApplyDonorMapCalls);
        Assert.Equal(0, shell.ApplyPngCalls);
    }

    // ---- what the confirm is told about the maps the drop is about to overwrite ----

    /// <summary>The repro the warning exists for: one stock map dresses material slots 0 and 2, a Blender
    /// send authored submesh 0, and the card standing on slot 2 has no authored file of its own. Its drop
    /// still lands on BOTH — so submesh 0's map is overwritten from a card that shows no sign of it, and the
    /// confirm has to count it.</summary>
    [Fact]
    public async Task CardDrop_LandingOnASubmeshAnotherCardAuthored_CountsItOnTheConfirm()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart(donorSubmeshes: 3, authoredAlbedo: new[] { 0 }));

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) },
            PartCard("c_stem_body1_d", submeshes: new[] { 0, 2 }));

        Assert.Equal(1, shell.LastConfirm!.AuthoredLanding);
        // and the write itself is unchanged: every landing submesh is authored, which is what makes the
        // dropped image the map of all of them
        Assert.Equal(new[] { 0, 2 }, shell.LastDonorDrop!.Submeshes);
        Assert.Equal(1, shell.ApplyDonorMapCalls);
    }

    [Fact]
    public async Task CardDrop_LandingOnlyOnUnauthoredSubmeshes_CountsNone()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart(donorSubmeshes: 3));

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) },
            PartCard("c_stem_body1_d", submeshes: new[] { 0, 2 }));

        Assert.Equal(0, shell.LastConfirm!.AuthoredLanding);
    }

    /// <summary>The count is per SLOT: a submesh whose normal is authored says nothing about its base
    /// colour, and warning on it would cry wolf over a map the drop never touches.</summary>
    [Fact]
    public async Task CardDrop_CountsOnlyTheSlotItLandsOn()
    {
        using var temp = new TempGame();
        var project = ReplacedPart(donorSubmeshes: 3);
        project.Targets[0].DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, Normal = "textures/body1_s0_nrm.png" },
        };
        var shell = new RecordingShell();
        var vm = NewVm(shell, project);

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) },
            PartCard("c_stem_body1_d", submeshes: new[] { 0, 2 }));

        Assert.Equal(0, shell.LastConfirm!.AuthoredLanding);
    }

    /// <summary>The asymmetry the two cards of one shared map have. The card whose own submesh is authored
    /// overwrites that file alone; its sibling, which shows no authored file, re-authors the whole landing
    /// set — the same gesture on two cards of the same image, with different reach.</summary>
    [Fact]
    public async Task CardDrop_OnTheAuthoredCard_ReachesOneSubmesh_OnItsSibling_ReachesBoth()
    {
        using var temp = new TempGame();
        var png = TestImages.WritePng(temp.At("skin.png"));
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart(donorSubmeshes: 3, authoredAlbedo: new[] { 0 }));

        var authored = PartCard("c_stem_body1_d", submeshes: new[] { 0, 2 });
        authored.AuthoredPath = temp.At("body1_s0_base.png");   // what the refresh hangs on submesh 0's card
        await vm.HandleDropAsync(new[] { png }, authored);

        Assert.Equal(1, shell.ApplyAuthoredPngCalls);
        Assert.Equal(0, shell.ApplyDonorMapCalls);
        Assert.Equal("body1", shell.LastAuthoredPart);
        Assert.Equal("Base color", shell.LastAuthoredRole);

        await vm.HandleDropAsync(new[] { png }, PartCard("c_stem_body1_d", submeshes: new[] { 0, 2 }));

        Assert.Equal(1, shell.ApplyDonorMapCalls);
        Assert.Equal(new[] { 0, 2 }, shell.LastDonorDrop!.Submeshes);
    }

    // ---- a slot the replacement never rebinds ----

    /// <summary>A Replace rebinds base colour, normal and RMO. Every other slot the material carries still
    /// draws its game texture at the replaced part, so a drop there is an ordinary texture edit — refusing it
    /// would leave the modder no way to touch a map their mod does ship.</summary>
    [Fact]
    public async Task CardDrop_OnASlotTheReplacementNeverRebinds_EditsTheGameTexture()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart());

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("glow.png")) },
            PartCard("c_stem_body1_e", "_EmissionMap", "Emission"));

        Assert.Equal(1, shell.ApplyPngCalls);
        Assert.Equal(0, shell.ApplyDonorMapCalls);
        Assert.Null(shell.LastConfirm!.Donor);
        Assert.Equal("c_stem_body1_e", shell.LastApplyTexture);
    }

    /// <summary>The one refusal this route still has names no slot at all: a card's LABEL can be guessed off
    /// a texture-name suffix and disagree with the shader slot, so a message built on it could contradict
    /// the card it refuses.</summary>
    [Fact]
    public async Task TheRefusal_NamesNoCardLabel()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell, ReplacedPart(donorSubmeshes: 1));

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) },
            PartCard("c_stem_body1_rmo", "_BaseMap", "RMO", submeshes: new[] { 2 }));

        Assert.DoesNotContain("RMO", vm.Status);
    }

    // ---- the game route's reach ----

    [Fact]
    public async Task GameCardDrop_TellsTheConfirmHowManyOtherPartsDrawTheTexture()
    {
        using var temp = new TempGame();
        var shell = new RecordingShell();
        var vm = NewVm(shell);
        var card = new WorkbenchMapVm("Base color", "_BaseMap", "c_stem_shared_d", "bundle1")
        {
            Subject = new WorkbenchSubjectRef("char", "stem", "c_stem_slg_",
                new Outfit(0, "stem", OutfitKind.Base)),
            // the owner list the tree build hands the card: its own part plus the two sharing the map
            OwnerMeshNames = new[] { "c_stem_slg_body1_lod0", "c_stem_slg_arm_lod0", "c_stem_slg_leg_lod0" },
        };

        await vm.HandleDropAsync(new[] { TestImages.WritePng(temp.At("skin.png")) }, card);

        Assert.Equal(2, shell.LastConfirm!.OtherWearers);
    }

    [Fact]
    public async Task PngDrop_WithNoCard_IsRefused()
    {
        // The pane-wide name match is cut: a png that lands on the background matches nothing by filename
        // any more, so it is refused outright rather than hunting for a same-named texture.
        var shell = new RecordingShell();
        var vm = NewVm(shell);

        await vm.HandleDropAsync(new[] { @"C:\tmp\tex_face.png" }, card: null);

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.ApplyPngCalls);
        Assert.Equal("Only a .png dropped on a texture card applies.", vm.Status);
    }
}
