using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.ViewModels.Workbench;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// A PNG dropped on the map card of a REPLACED part. The card names a game texture the part no longer
/// draws, so the image is meant for the replacement — and it has to land as exactly the record a Blender
/// session's map produces, or the build's donor path has a second shape to learn. What is pinned here is
/// that record: the file under the donor naming convention, the authored slot on the part's own mesh
/// target, and the RMO's mask rebuild, which is the one slot whose file is not a copy of what was dropped.
/// </summary>
[Collection("Dispatcher")]
public class DonorMapDropTests
{
    private const string Mesh = "c_stem_slg_body1_lod0";
    private static readonly WorkbenchSubjectRef Subject =
        new("char", "stem", "c_stem_slg_", new Outfit(0, "stem", OutfitKind.Base));

    /// <summary>A mod holding one REPLACED part: a mesh target with no <c>originals/</c> copy on record,
    /// which is what an edited mesh reads as, carrying the donor materials its send-back recorded.
    /// <paramref name="tweak"/> reshapes the target before it is saved, for the states a race leaves.</summary>
    private static string Seed(TempGame g, int donorSubmeshes = 1, Action<ProjectTarget>? tweak = null)
    {
        const string name = "Drop Mod";
        var root = Path.Combine(g.Root, ModNaming.Slug(name));   // the folder the app expects for this name
        Directory.CreateDirectory(Path.Combine(root, "char_stem", "meshes"));
        var project = new ModProject { RootDir = root };
        project.Info.Name = name;
        project.Selection.Add(new SelectionEntry { Character = "char", Outfit = "stem" });
        var target = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = Mesh,
            ReplaceFile = "char_stem/meshes/body1.glb",
            SubjectCharacter = "char", SubjectOutfit = "stem",
            DonorMaterials = Enumerable.Range(0, donorSubmeshes).Select(i => $"M_{i}").ToList(),
        };
        tweak?.Invoke(target);
        project.Targets.Add(target);
        project.Save();
        return root;
    }

    /// <summary>The donor map file this drop writes for one submesh.</summary>
    private static string DonorMap(string root, int submesh, string slot = "base") =>
        Path.Combine(root, "textures", $"{ModNaming.Slug(Mesh)}_s{submesh}_{slot}.png");

    /// <summary>An opaque 2×2 image to drop, in a colour no default can be confused with.</summary>
    private static string Dropped(TempGame g, string name = "drop.png")
    {
        using var img = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30, 255));
        var path = Path.Combine(g.Root, name);
        img.SaveAsPng(path);
        return path;
    }

    /// <summary>An IProgress that records ON the calling thread. <see cref="Progress{T}"/> posts, so a line
    /// reported just before the await returns can still be in flight when the assert runs.</summary>
    private sealed class Lines : IProgress<string>
    {
        public readonly List<string> Reported = new();
        public void Report(string value) => Reported.Add(value);
        public string Last => Reported[^1];
    }

    private static ProjectTarget Reloaded(string root) =>
        ModProject.Load(root).Targets.Single(t => t.AssetType == "Mesh");

    [Fact]
    public async Task ADroppedBaseColor_LandsAsAnAuthoredSlotOnThePartsOwnTarget()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.BaseColor, new[] { 0 }), "Base color",
            Dropped(g), new Progress<string>());

        // the naming convention the Blender intake writes, so a re-send overwrites instead of orphaning
        var expected = Path.Combine(root, "textures", $"{ModNaming.Slug(Mesh)}_s0_base.png");
        Assert.True(File.Exists(expected), $"no donor map at {expected}");
        var row = Assert.Single(Reloaded(root).DonorTextures!);
        Assert.Equal(0, row.Submesh);
        Assert.Equal($"textures/{ModNaming.Slug(Mesh)}_s0_base.png", row.Albedo);
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);
        Assert.Null(row.Normal);
        Assert.Null(row.Rmo);
    }

    [Fact]
    public async Task ADroppedNormal_LandsOnTheNormalSlotAlone()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.Normal, new[] { 0 }), "Normal",
            Dropped(g), new Progress<string>());

        var row = Assert.Single(Reloaded(root).DonorTextures!);
        Assert.Equal($"textures/{ModNaming.Slug(Mesh)}_s0_nrm.png", row.Normal);
        Assert.Equal(SlotOrigin.Authored, row.NormalAsk);
        Assert.Null(row.Albedo);
    }

    /// <summary>The RMO is the one slot whose shipped file is not the dropped image: its alpha is the
    /// emissive mask, read off the stock map. With no stock RMO to read the mask is zero and the status says
    /// so — a blanked mask is never silent.</summary>
    [Fact]
    public async Task ADroppedRmo_GoesThroughTheMaskRebuild_AndSaysWhenThereIsNoMask()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));
        var reported = new Lines();   // Progress posts, so its callbacks can still be in flight at the assert

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.Rmo, new[] { 0 }), "RMO",
            Dropped(g), reported);

        var shipped = Path.Combine(root, "textures", $"{ModNaming.Slug(Mesh)}_s0_rmo.png");
        using var img = Image.Load<Rgba32>(shipped);
        Assert.Equal(0, img[0, 0].A);                      // a plain copy would have kept the source's 255
        Assert.Equal(new Rgba32(10, 20, 30, 0), img[0, 0]);  // colour untouched
        Assert.Equal(SlotOrigin.Authored, Assert.Single(Reloaded(root).DonorTextures!).RmoAsk);
        Assert.Contains(reported.Reported, m => m.Contains("emissive mask", StringComparison.Ordinal));
    }

    /// <summary>The re-drop is where the mask went missing: a copy over the authored file took the dropped
    /// image whole, alpha included, and an RMO's alpha IS the emissive mask. Re-authoring rebuilds it, so
    /// the second drop ships what the first one did.</summary>
    [Fact]
    public async Task AnRmoDroppedOverAMapThePartAlreadyCarries_RebuildsTheMask()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g, tweak: t => t.DonorTextures = new List<SubmeshTextures>
        {
            new()
            {
                Submesh = 0, Rmo = $"textures/{ModNaming.Slug(Mesh)}_s0_rmo.png",
                RmoOrigin = SlotOrigin.Authored,
            },
        });
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        using (var earlier = new Image<Rgba32>(2, 2, new Rgba32(200, 100, 50, 255)))
            earlier.SaveAsPng(DonorMap(root, 0, "rmo"));
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.Rmo, new[] { 0 }), "RMO",
            Dropped(g), new Progress<string>());

        using var landed = Image.Load<Rgba32>(DonorMap(root, 0, "rmo"));
        Assert.Equal(new Rgba32(10, 20, 30, 0), landed[0, 0]);   // a copy would have kept the source's 255
        Assert.Equal(SlotOrigin.Authored, Assert.Single(Reloaded(root).DonorTextures!).RmoAsk);
    }

    [Fact]
    public async Task ADropCoveringSeveralSubmeshes_WritesOneFilePerSubmesh()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g, donorSubmeshes: 3);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.BaseColor, new[] { 0, 2 }), "Base color",
            Dropped(g), new Progress<string>());

        var rows = Reloaded(root).DonorTextures!;
        Assert.Equal(new[] { 0, 2 }, rows.Select(r => r.Submesh).ToArray());
        foreach (var i in new[] { 0, 2 })
            Assert.True(File.Exists(Path.Combine(root, "textures", $"{ModNaming.Slug(Mesh)}_s{i}_base.png")));
    }

    /// <summary>A second drop on another slot of the same submesh joins the row it is already on — two rows
    /// for one donor submesh is what the build refuses outright.</summary>
    [Fact]
    public async Task ASecondSlotOnTheSameSubmesh_JoinsTheRowAlreadyThere()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.BaseColor, new[] { 0 }), "Base color",
            Dropped(g, "a.png"), new Progress<string>());
        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.Normal, new[] { 0 }), "Normal",
            Dropped(g, "b.png"), new Progress<string>());

        var row = Assert.Single(Reloaded(root).DonorTextures!);
        Assert.NotNull(row.Albedo);
        Assert.NotNull(row.Normal);
    }

    // ---- what the slots this drop does NOT touch are recorded as ----

    /// <summary>The drop's context is a part whose stock maps are all still plugged, which is what the cards
    /// show and what a Blender session leaving those images alone records. A fresh row has to say that, or
    /// the two untouched slots read as "no image at all" — and on a submesh that now asks for one, the
    /// build's relief rule puts its flat normal and RMO there instead of the part's real maps.</summary>
    [Fact]
    public async Task AFreshRow_RecordsTheUntouchedSlotsAsThePartsOwnStockMaps()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.BaseColor, new[] { 0 }), "Base color",
            Dropped(g), new Progress<string>());

        var row = Assert.Single(Reloaded(root).DonorTextures!);
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);
        Assert.Equal(SlotOrigin.VanillaOwn, row.NormalAsk);
        Assert.Equal(SlotOrigin.VanillaOwn, row.RmoAsk);
        // and the build's own rule agrees: nothing on this submesh ships flat, so the real maps keep drawing
        var flat = BlankedSlots.Of(row, EditVerbs.Replace);
        Assert.False(flat.Normal);
        Assert.False(flat.Rmo);
    }

    /// <summary>A row already on record carries a decision the modder made in Blender. The drop changes the
    /// slot it lands on and nothing else — a slot they deliberately blanked stays blanked.</summary>
    [Fact]
    public async Task AnExistingRow_KeepsTheOriginsItAlreadyRecorded()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g);
        var seeded = ModProject.Load(root);
        seeded.Targets.Single(t => t.AssetType == "Mesh").DonorTextures = new List<SubmeshTextures>
        {
            new() { Submesh = 0, NormalOrigin = SlotOrigin.ExplicitNeutral },
        };
        seeded.Save();
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.BaseColor, new[] { 0 }), "Base color",
            Dropped(g), new Progress<string>());

        var row = Assert.Single(Reloaded(root).DonorTextures!);
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);
        Assert.Equal(SlotOrigin.ExplicitNeutral, row.NormalAsk);   // their blank survives the drop
        Assert.Equal(SlotOrigin.None, row.RmoAsk);
        Assert.True(BlankedSlots.Of(row, EditVerbs.Replace).Normal);
    }

    // ---- overwriting a map that is already there ----

    /// <summary>The write is overwrite-all-landing by design: the image the modder dropped becomes the map of
    /// every submesh the card's stock map dressed, including one a Blender send already authored. What
    /// protects the earlier map is the confirm's count, not a carve-out here — a drop that skipped the
    /// authored submesh would leave the part drawing two different images off one card.</summary>
    [Fact]
    public async Task ADropCoveringAnAlreadyAuthoredSubmesh_OverwritesThatMapDeliberately()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g, donorSubmeshes: 3, tweak: t => t.DonorTextures = new List<SubmeshTextures>
        {
            new()
            {
                Submesh = 0, Albedo = $"textures/{ModNaming.Slug(Mesh)}_s0_base.png",
                AlbedoOrigin = SlotOrigin.Authored,
            },
        });
        Directory.CreateDirectory(Path.Combine(root, "textures"));
        using (var earlier = new Image<Rgba32>(2, 2, new Rgba32(200, 100, 50, 255)))
            earlier.SaveAsPng(DonorMap(root, 0));
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.BaseColor, new[] { 0, 2 }), "Base color",
            Dropped(g), new Progress<string>());

        using var landed = Image.Load<Rgba32>(DonorMap(root, 0));
        Assert.Equal(new Rgba32(10, 20, 30, 255), landed[0, 0]);   // the dropped image, not the earlier one
        Assert.True(File.Exists(DonorMap(root, 2)));
        Assert.Equal(new[] { 0, 2 }, Reloaded(root).DonorTextures!.Select(r => r.Submesh).ToArray());
    }

    // ---- the confirm window is open for as long as the modder leaves it ----

    /// <summary>A Revert can land while the confirm sits open, and it takes the replacement with it. Writing
    /// then would author maps for a part that has no replacement to carry them, and the part's own Revert —
    /// the only way back — has already run.</summary>
    [Fact]
    public async Task ADropWhoseTargetStoppedBeingReplaced_RefusesAndWritesNothing()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        // an original on record whose bytes match the workspace glb IS what "not replaced" means
        var root = Seed(g, tweak: t => t.OriginalFile = "char_stem/meshes/originals/body1.glb");
        foreach (var rel in new[] { "char_stem/meshes/body1.glb", "char_stem/meshes/originals/body1.glb" })
        {
            var abs = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, "the same bytes");
        }
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));
        var lines = new Lines();

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.BaseColor, new[] { 0 }), "Base color",
            Dropped(g), lines);

        Assert.Equal("drop.png can't apply here. body1 is no longer replaced. "
            + "Drop it again to edit the game texture.", lines.Last);
        Assert.False(Directory.Exists(Path.Combine(root, "textures")));
        Assert.Null(Reloaded(root).DonorTextures);
    }

    /// <summary>A send-back can land while the confirm sits open and come back carrying FEWER submeshes. The
    /// landing set was decided against the old shape; the build refuses a row past the donor's own count, so
    /// the write refuses first.</summary>
    [Fact]
    public async Task ADropWhoseTargetLostSubmeshes_RefusesAndWritesNothing()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g, donorSubmeshes: 1);   // the re-send's shape; the drop below carries the old one
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));
        var lines = new Lines();

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.BaseColor, new[] { 0, 2 }), "Base color",
            Dropped(g), lines);

        Assert.Equal("drop.png can't apply here. This map dresses submeshes body1's replacement doesn't have. "
            + "Send body1 back from Blender to add them.", lines.Last);
        Assert.False(File.Exists(DonorMap(root, 0)));
        Assert.Null(Reloaded(root).DonorTextures);
    }

    // ---- what the status line says ----

    /// <summary>The intake reads and re-encodes; on a several-submesh RMO drop it is the slowest thing the
    /// pane does. The game-texture route already says it started, and this one has to as well or the pane
    /// sits silent through it.</summary>
    [Fact]
    public async Task TheDrop_SaysItStartedBeforeItSaysItLanded()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));
        var lines = new Lines();

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.BaseColor, new[] { 0 }), "Base color",
            Dropped(g), lines);

        Assert.Equal("Preparing body1's Base color…", lines.Reported[0]);
        Assert.Equal("Applied drop.png as body1's Base color.", lines.Last);
    }

    /// <summary>The mask note names the PART, which is what the card and every other line call it — the
    /// workspace glb's stem is a file name the modder never chose. And every distinct note is reported: one
    /// submesh losing its mask says nothing about the next.</summary>
    [Fact]
    public async Task TheMaskNote_NamesThePart_AndEveryDistinctNoteIsReported()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g, donorSubmeshes: 2);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));
        var lines = new Lines();

        await vm.ApplyDroppedPngToDonorMapAsync(Subject,
            new DonorMapDrop("body1", DonorMapSlot.Rmo, new[] { 0, 1 }), "RMO",
            Dropped(g), lines);

        Assert.Contains("body1", lines.Last);
        Assert.DoesNotContain("body1.glb", lines.Last);
        Assert.DoesNotContain(ModNaming.Slug(Mesh), lines.Last);
    }

    // ---- the mask source's own failure ----

    /// <summary>A stock RMO the record names but disk doesn't have is the same loss as one that won't
    /// decode: the shipped map's alpha goes to zero. Silence there leaves a mask missing in game with
    /// nothing said anywhere.</summary>
    [Fact]
    public void AMaskSourceThatIsGone_IsReportedLikeOneThatWontRead()
    {
        using var g = new TempGame();
        var source = Path.Combine(g.Root, "authored.png");
        using (var img = new Image<Rgba32>(2, 2, new Rgba32(1, 2, 3, 255))) img.SaveAsPng(source);
        var reported = new List<string>();

        DonorTextureIntake.TakeOne(source, Path.Combine(g.Root, "textures"), "part", 0, DonorMapSlot.Rmo,
            stockRmoPng: Path.Combine(g.Root, "never-written.png"), report: reported.Add);

        Assert.Equal("Couldn't find never-written.png for its emissive mask. The RMO ships with none.",
            Assert.Single(reported));
    }

    /// <summary>Being handed NO stock map is not the same news: the caller already decided whether that slot
    /// had a mask to lose, and reporting here would double up on the note it wrote itself.</summary>
    [Fact]
    public void NoMaskSourceAtAll_ReportsNothingFromTheIntake()
    {
        using var g = new TempGame();
        var source = Path.Combine(g.Root, "authored.png");
        using (var img = new Image<Rgba32>(2, 2, new Rgba32(1, 2, 3, 255))) img.SaveAsPng(source);
        var reported = new List<string>();

        DonorTextureIntake.TakeOne(source, Path.Combine(g.Root, "textures"), "part", 0, DonorMapSlot.Rmo,
            stockRmoPng: null, report: reported.Add);

        Assert.Empty(reported);
    }

    // ---- the second drop, over a map the replacement already carries ----

    /// <summary>The authored route's status reports at the donor route's grain: the part and the slot. The
    /// file name it writes is the build's naming convention, which is not a name the modder ever chose.</summary>
    [Fact]
    public async Task ADropOverAnAuthoredMap_ReportsThePartAndSlot_NotTheGeneratedFileName()
    {
        using var g = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Seed(g);
        var authored = DonorMap(root, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(authored)!);
        using (var img = new Image<Rgba32>(2, 2, new Rgba32(200, 100, 50, 255))) img.SaveAsPng(authored);
        var vm = new MainWindowViewModel(startLoad: false);
        Assert.True(await vm.OpenModAsync(root));
        var lines = new Lines();

        await vm.ApplyDroppedPngToAuthoredAsync(authored, "body1", "Base color", Dropped(g), lines);

        Assert.Equal("Applied drop.png as body1's Base color.", Assert.Single(lines.Reported));
        using var landed = Image.Load<Rgba32>(authored);
        Assert.Equal(new Rgba32(10, 20, 30, 255), landed[0, 0]);
    }
}
