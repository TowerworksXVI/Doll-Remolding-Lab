using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// What a returned glb's resolved maps become on the target. A slot the modder left alone, or bound to the
/// part's OWN vanilla map, must not start binding — otherwise every existing mod's output changes. A
/// SIBLING part's vanilla map, linked deliberately, records as a reference.
/// </summary>
public class DonorTextureIntakeTests
{
    private static ResolvedMap Authored(string marker) =>
        new(MapOrigin.Authored, AuthoredPng: Encoding.UTF8.GetBytes(marker));

    private static readonly ResolvedMap Stock = new(MapOrigin.Vanilla, StockPng: "textures/stock_d.png");
    private static readonly ResolvedMap Absent = new(MapOrigin.None);

    private static string Rel(string root) => root;

    [Fact]
    public void UntouchedMaps_RecordNothing()
    {
        using var g = new TempGame();
        var maps = new List<IncomingMaps> { new(Stock, Stock), new(Stock, Absent) };

        var rows = DonorTextureIntake.Collect(maps, Path.Combine(g.Root, "textures"), "body", p => p);

        Assert.Null(rows);
        Assert.False(Directory.Exists(Path.Combine(g.Root, "textures")));   // nothing written either
    }

    [Fact]
    public void AuthoredMaps_AreWrittenAndRecordedPerSlot()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var maps = new List<IncomingMaps>
        {
            new(Authored("albedo0"), Authored("normal0")),
            new(Stock, Absent),                                // untouched submesh, skipped entirely
            new(Absent, Authored("normal2")),                  // normal only
        };

        var rows = DonorTextureIntake.Collect(maps, texDir, "body", p => Path.GetFileName(p));

        Assert.NotNull(rows);
        Assert.Equal(2, rows!.Count);

        Assert.Equal(0, rows[0].Submesh);
        Assert.Equal("body_s0_base.png", rows[0].Albedo);
        Assert.Equal("body_s0_nrm.png", rows[0].Normal);
        Assert.Equal("albedo0", File.ReadAllText(Path.Combine(texDir, "body_s0_base.png")));
        Assert.Equal("normal0", File.ReadAllText(Path.Combine(texDir, "body_s0_nrm.png")));

        Assert.Equal(2, rows[1].Submesh);                      // the submesh INDEX, not the row index
        Assert.Null(rows[1].Albedo);
        Assert.Equal("body_s2_nrm.png", rows[1].Normal);
    }

    [Fact]
    public void ASiblingPartsVanillaMap_IsRecordedAsAReference_NotCopied()
    {
        // "use the body's map on this cloth": vanilla-of-the-SESSION but not this part's own, so it records
        // as a reference to the sibling's existing workspace PNG.
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var own = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { Path.GetFullPath("textures/cloth_d.png") };
        var sibling = new ResolvedMap(MapOrigin.Vanilla, StockPng: "textures/stock_d.png");   // body's map
        var ownMap = new ResolvedMap(MapOrigin.Vanilla, StockPng: "textures/cloth_d.png");    // this part's

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
        {
            new(sibling, Absent),      // deliberate cross-part link → reference
            new(ownMap, Absent),       // the part's own map → absent, the draw binds it anyway
        }, texDir, "cloth", p => p, own);

        Assert.NotNull(rows);
        var row = Assert.Single(rows!);
        Assert.Equal(0, row.Submesh);
        Assert.Equal("textures/stock_d.png", row.Albedo);
        Assert.False(Directory.Exists(texDir));   // a reference copies nothing
    }

    [Fact]
    public void WithoutAnOwnMapList_AVanillaMatchStaysAbsent()
    {
        // no per-part sidecar → ownership unknowable → the safe old behaviour
        using var g = new TempGame();
        var rows = DonorTextureIntake.Collect(new List<IncomingMaps> { new(Stock, Absent) },
            Path.Combine(g.Root, "textures"), "cloth", p => p, ownStockPngs: null);
        Assert.Null(rows);
    }

    /// <summary>The three file-less cases are three different asks, so each one is recorded rather than
    /// collapsed into "no file". "Stock, untouched" keeps the real map; "no image at all" is what the
    /// build's garbage relief acts on.</summary>
    [Fact]
    public void EachSlotRecordsItsOrigin_SoStockAndAbsentAreNotTheSameAsk()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var own = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { Path.GetFullPath("textures/stock_d.png") };

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
        {
            new(Authored("albedo0"), Stock),
        }, texDir, "body", p => Path.GetFileName(p), own);

        var row = Assert.Single(rows!);
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);
        Assert.Equal(SlotOrigin.VanillaOwn, row.NormalAsk);
        Assert.Null(row.Normal);
    }

    /// <summary>An untouched return means "keep what Blender showed". When the part's own workspace PNG
    /// carries a texture edit, Blender showed the EDITED map — so the slot records it as the replacement's
    /// own, referencing the workspace file, instead of falling back to the game's bytes.</summary>
    [Fact]
    public void AnEditedOwnMap_ComingBackUntouched_ShipsTheWorkspacePng()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var own = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { Path.GetFullPath("textures/cloth_d.png") };
        var ownMap = new ResolvedMap(MapOrigin.Vanilla, StockPng: "textures/cloth_d.png");

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps> { new(ownMap, Absent) },
            texDir, "cloth", p => p, own, isEditedStock: _ => true);

        var row = Assert.Single(rows!);
        Assert.Equal("textures/cloth_d.png", row.Albedo);
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);
        Assert.False(Directory.Exists(texDir));   // a reference copies nothing
    }

    [Fact]
    public void APristineOwnMap_StaysVanillaOwn_WithTheEditQuestionAnswered()
    {
        using var g = new TempGame();
        var own = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { Path.GetFullPath("textures/cloth_d.png") };
        var ownMap = new ResolvedMap(MapOrigin.Vanilla, StockPng: "textures/cloth_d.png");

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps> { new(ownMap, Absent) },
            Path.Combine(g.Root, "textures"), "cloth", p => p, own, isEditedStock: _ => false);

        Assert.Null(rows);
    }

    // ---- the RMO slot and its alpha ----------------------------------------------------------------

    /// <summary>A flat image whose alpha is its own marker, so an alpha that came from the wrong map is
    /// visible in the assertion.</summary>
    private static byte[] FlatPng(Rgba32 color, int size = 8)
    {
        using var img = new Image<Rgba32>(size, size, color);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static string WriteFlatPng(string path, Rgba32 color, int size = 8)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, FlatPng(color, size));
        return path;
    }

    private static Rgba32 FirstPixel(string path)
    {
        using var img = Image.Load<Rgba32>(path);
        return img[0, 0];
    }

    /// <summary>A map whose alpha differs in every pixel, so a mask that lost detail on the way through is
    /// visible rather than merely plausible.</summary>
    private static string WriteMaskedPng(string path, Rgba32 color, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var img = new Image<Rgba32>(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                img[x, y] = new Rgba32(color.R, color.G, color.B, MaskAlpha(x, y, size));
        img.SaveAsPng(path);
        return path;
    }

    private static byte MaskAlpha(int x, int y, int size) => (byte)(y * size + x);

    /// <summary>Alpha carries the emissive mask and does not survive Blender, so an authored RMO ships the
    /// modder's colour channels over the STOCK map's alpha.</summary>
    [Fact]
    public void AnAuthoredRmo_ShipsTheStocksAlphaOverTheAuthoredColour()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var stock = WriteFlatPng(Path.Combine(g.Root, "stock_r.png"), new Rgba32(9, 9, 9, 77));

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
        {
            new(Absent, Absent, new ResolvedMap(MapOrigin.Authored,
                AuthoredPng: FlatPng(new Rgba32(10, 20, 30, 255)))),
        }, texDir, "body", p => p, null, _ => stock);

        var row = Assert.Single(rows!);
        Assert.Equal(SlotOrigin.Authored, row.RmoAsk);
        Assert.Equal(new Rgba32(10, 20, 30, 77), FirstPixel(row.Rmo!));
    }

    /// <summary>A flat map plugged over a full-size stock must not cost the mask its resolution: the shipped
    /// map takes the STOCK's size, the mask arrives pixel for pixel, and the authored colour is sampled up to
    /// meet it.</summary>
    [Fact]
    public void ASmallAuthoredRmo_ShipsAtTheStocksSize_WithTheMaskIntact()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var stock = WriteMaskedPng(Path.Combine(g.Root, "stock_r.png"), new Rgba32(9, 9, 9, 0), size: 16);

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
        {
            new(Absent, Absent, new ResolvedMap(MapOrigin.Authored,
                AuthoredPng: FlatPng(new Rgba32(1, 2, 3, 255), size: 8))),
        }, texDir, "body", p => p, null, _ => stock);

        var row = Assert.Single(rows!);
        using var shipped = Image.Load<Rgba32>(row.Rmo!);
        Assert.Equal(16, shipped.Width);
        Assert.Equal(16, shipped.Height);
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(new Rgba32(1, 2, 3, MaskAlpha(x, y, 16)), shipped[x, y]);
    }

    /// <summary>A repaint at a higher resolution than the stock keeps its own size, and the stock alpha is
    /// sampled nearest-neighbour up onto it. Neither source is ever downsampled to meet the other.</summary>
    [Fact]
    public void AnAuthoredRmoOfADifferentSize_ResamplesTheStockAlpha()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var stock = WriteFlatPng(Path.Combine(g.Root, "stock_r.png"), new Rgba32(9, 9, 9, 200), size: 4);

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
        {
            new(Absent, Absent, new ResolvedMap(MapOrigin.Authored,
                AuthoredPng: FlatPng(new Rgba32(1, 2, 3, 255), size: 16))),
        }, texDir, "body", p => p, null, _ => stock);

        var row = Assert.Single(rows!);
        using var shipped = Image.Load<Rgba32>(row.Rmo!);
        Assert.Equal(16, shipped.Width);                       // the authored map's own size is kept
        Assert.Equal(new Rgba32(1, 2, 3, 200), shipped[15, 15]);
    }

    /// <summary>With no stock RMO behind the submesh there is no mask to carry, and Blender's alpha is not
    /// one — so the shipped map masks nothing.</summary>
    [Fact]
    public void AnAuthoredRmoWithNoStockBehindIt_ShipsAZeroAlpha()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
        {
            new(Absent, Absent, new ResolvedMap(MapOrigin.Authored,
                AuthoredPng: FlatPng(new Rgba32(10, 20, 30, 255)))),
        }, texDir, "body", p => p);

        var row = Assert.Single(rows!);
        Assert.Equal(new Rgba32(10, 20, 30, 0), FirstPixel(row.Rmo!));
    }

    /// <summary>The stock map is the only file the intake READS, and it can be unreadable while the send-back
    /// is perfectly good — damaged on disk, or held open by the image editor the app itself launches. The
    /// slot loses its mask; the part keeps every map that arrived with it, and the failure is named.</summary>
    [Fact]
    public void AnUnreadableStockRmo_CostsThatSlotItsMaskAndNothingElse()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var stock = Path.Combine(g.Root, "stock_r.png");
        File.WriteAllText(stock, "not a png");
        var said = new List<string>();

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
        {
            new(Authored("albedo0"), Authored("normal0"), new ResolvedMap(MapOrigin.Authored,
                AuthoredPng: FlatPng(new Rgba32(10, 20, 30, 255)))),
        }, texDir, "body", p => p, null, _ => stock, said.Add);

        var row = Assert.Single(rows!);
        Assert.Equal(new Rgba32(10, 20, 30, 0), FirstPixel(row.Rmo!));        // authored colour, no mask
        Assert.Equal("albedo0", File.ReadAllText(Path.Combine(texDir, "body_s0_base.png")));
        Assert.Equal("normal0", File.ReadAllText(Path.Combine(texDir, "body_s0_nrm.png")));
        Assert.Contains("stock_r.png", Assert.Single(said));
    }

    /// <summary>The same degrade for the state the app expects by design: the modder has the stock map open
    /// in an image editor that holds it exclusively.</summary>
    [Fact]
    public void AStockRmoHeldOpenExclusively_DegradesRatherThanThrows()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var stock = WriteFlatPng(Path.Combine(g.Root, "stock_r.png"), new Rgba32(9, 9, 9, 77));
        var said = new List<string>();

        using (File.Open(stock, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
            {
                new(Absent, Absent, new ResolvedMap(MapOrigin.Authored,
                    AuthoredPng: FlatPng(new Rgba32(10, 20, 30, 255)))),
            }, texDir, "body", p => p, null, _ => stock, said.Add);

            var row = Assert.Single(rows!);
            Assert.Equal(new Rgba32(10, 20, 30, 0), FirstPixel(row.Rmo!));
            Assert.Contains("stock_r.png", Assert.Single(said));
        }
        // the only files left behind are the ones the intake meant to write
        Assert.Equal(new[] { "body_s0_rmo.png" },
            Directory.GetFiles(texDir).Select(Path.GetFileName).ToArray());
    }

    /// <summary>A submesh whose ONLY ask is its RMO still gets a row — the slot is an ask like any
    /// other.</summary>
    [Fact]
    public void ARmoOnlyAsk_StillEmitsItsRow()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
        {
            new(Stock, Absent, new ResolvedMap(MapOrigin.Authored, AuthoredPng: FlatPng(new Rgba32(4, 5, 6, 255)))),
        }, texDir, "body", p => p);

        var row = Assert.Single(rows!);
        Assert.Equal(SlotOrigin.Authored, row.RmoAsk);
        Assert.Equal("body_s0_rmo.png", Path.GetFileName(row.Rmo!));
    }

    /// <summary>A deliberately linked sibling RMO IS a stock map — alpha included — so it is referenced as
    /// it stands, with no rebuild and no copy.</summary>
    [Fact]
    public void ASiblingPartsVanillaRmo_IsReferencedWholeWithNoAlphaRebuild()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var own = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            { Path.GetFullPath("textures/cloth_r.png") };
        var sibling = new ResolvedMap(MapOrigin.Vanilla, StockPng: "textures/body_r.png");

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
        {
            new(Absent, Absent, sibling),
        }, texDir, "cloth", p => p, own);

        var row = Assert.Single(rows!);
        Assert.Equal("textures/body_r.png", row.Rmo);
        Assert.Equal(SlotOrigin.Authored, row.RmoAsk);
        Assert.False(Directory.Exists(texDir));   // a reference copies nothing
    }

    /// <summary>Plugging a shipped neutral is an ask of its own: nothing ships, but the slot is recorded so
    /// the build binds its flat map there.</summary>
    [Fact]
    public void APluggedNeutral_RecordsAnExplicitNeutral_AndShipsNoFile()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");
        var neutral = new ResolvedMap(MapOrigin.Neutral);

        var rows = DonorTextureIntake.Collect(new List<IncomingMaps>
        {
            new(Absent, neutral),
        }, texDir, "body", p => p);

        var row = Assert.Single(rows!);
        Assert.Equal(SlotOrigin.ExplicitNeutral, row.NormalAsk);
        Assert.Null(row.Normal);
        Assert.False(Directory.Exists(texDir));   // a neutral is a bind, not a file
    }

    /// <summary>Re-sending the same edit must overwrite its files rather than leave a pile of orphans
    /// behind, so the names cannot carry anything that varies per send.</summary>
    [Fact]
    public void ResendingOverwritesRatherThanAccumulating()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");

        DonorTextureIntake.Collect(new List<IncomingMaps> { new(Authored("first"), Absent) }, texDir, "body", p => p);
        DonorTextureIntake.Collect(new List<IncomingMaps> { new(Authored("second"), Absent) }, texDir, "body", p => p);

        Assert.Single(Directory.GetFiles(texDir));
        Assert.Equal("second", File.ReadAllText(Path.Combine(texDir, "body_s0_base.png")));
    }

    /// <summary>Two parts in one combined session must not write over each other.</summary>
    [Fact]
    public void DistinctPartsGetDistinctFiles()
    {
        using var g = new TempGame();
        var texDir = Path.Combine(g.Root, "textures");

        DonorTextureIntake.Collect(new List<IncomingMaps> { new(Authored("b"), Absent) }, texDir, "body", p => p);
        DonorTextureIntake.Collect(new List<IncomingMaps> { new(Authored("h"), Absent) }, texDir, "hair", p => p);

        Assert.Equal("b", File.ReadAllText(Path.Combine(texDir, "body_s0_base.png")));
        Assert.Equal("h", File.ReadAllText(Path.Combine(texDir, "hair_s0_base.png")));
    }

    // ---- the other end: a record only outlives the mesh it describes until that file is replaced ----

    /// <summary>A new mesh over the workspace glb voids the record the previous one left: those materials
    /// and per-submesh maps describe geometry the file no longer holds, and the build would ship them onto
    /// the new mesh.</summary>
    [Fact]
    public void ReplacingTheWorkspaceFile_DropsThePreviousMeshsDonorRecord()
    {
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        var target = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = "c_stem_slg_body_lod0",
            ReplaceFile = "char_stem/body.glb",
            DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "textures/body_s0_base.png" } },
            DonorMaterials = new List<string> { "M_donor" },
        };
        project.Targets.Add(target);

        var hit = project.MarkFileReplaced(Path.Combine(g.Root, "char_stem", "body.glb"));

        Assert.Same(target, hit);
        Assert.True(target.Edited);
        Assert.Null(target.DonorTextures);
        Assert.Null(target.DonorMaterials);
    }

    [Fact]
    public void ReplacingAFileNoTargetOwns_ChangesNothing()
    {
        using var g = new TempGame();
        var project = new ModProject { RootDir = g.Root };
        var target = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "b0", ObjectName = "c_stem_slg_body_lod0",
            ReplaceFile = "char_stem/body.glb",
            DonorMaterials = new List<string> { "M_donor" },
        };
        project.Targets.Add(target);

        Assert.Null(project.MarkFileReplaced(Path.Combine(g.Root, "char_stem", "hair.glb")));
        Assert.False(target.Edited);
        Assert.NotNull(target.DonorMaterials);
    }
}
