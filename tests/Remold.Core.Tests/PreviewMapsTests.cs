using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The submesh↔texture contract across a Blender round trip. A preview image is a DERIVATIVE of the
/// stock PNG, so the pair of transforms must be exact inverses, and identity on the way back is the
/// image's content measured against the sidecar written beside the glb.
/// </summary>
public class PreviewMapsTests
{
    private static UnityMesh TwoSubmeshPatch() => new()
    {
        Name = "patch",
        VertexCount = 4,
        Channels = new()
        {
            ["Vertex"] = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 },
            ["TexCoord0"] = new float[] { 0, 0, 1, 0, 0, 1, 1, 1 },
        },
        Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2 },
        Submeshes = new() { new[] { 0, 1, 2 }, new[] { 1, 3, 2 } },
    };

    /// <summary>A deterministic non-uniform image, so a flip or a channel swap cannot pass unnoticed.</summary>
    private static string WritePng(string path, int seed = 0)
    {
        using var img = new Image<Rgba32>(8, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                img[x, y] = new Rgba32((byte)(x * 31 + seed), (byte)(y * 17 + seed), (byte)(x * y + seed), (byte)(200 + x));
        img.SaveAsPng(path);
        return path;
    }

    private static Rgba32[] Pixels(byte[] png)
    {
        using var img = Image.Load<Rgba32>(png);
        var px = new Rgba32[img.Width * img.Height];
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
                px[y * img.Width + x] = img[x, y];
        return px;
    }

    // ---------------------------------------------------------------- the paired transforms

    [Fact]
    public void BaseColour_SurvivesToPreviewAndBack()
    {
        using var g = new TempGame();
        var src = WritePng(Path.Combine(g.Root, "d.png"));
        var back = PreviewMaps.FromPreview(PreviewMaps.ToPreview(src, MapKind.BaseColor), MapKind.BaseColor);
        Assert.Equal(Pixels(File.ReadAllBytes(src)), Pixels(back));
    }

    /// <summary>A packed normal carries X in alpha and Y in green; R is filler and B mirrors G, so the
    /// inverse REBUILDS those two rather than recovering them.</summary>
    [Fact]
    public void PackedNormal_RecoversItsDataChannelsExactly()
    {
        using var g = new TempGame();
        var src = WritePng(Path.Combine(g.Root, "n.png"));
        var original = Pixels(File.ReadAllBytes(src));
        var back = Pixels(PreviewMaps.FromPreview(PreviewMaps.ToPreview(src, MapKind.Normal), MapKind.Normal));

        Assert.Equal(original.Length, back.Length);
        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i].A, back[i].A);      // X
            Assert.Equal(original[i].G, back[i].G);      // Y
            Assert.Equal(255, back[i].R);                // filler, rebuilt
            Assert.Equal(back[i].G, back[i].B);          // mirrors G, rebuilt
        }
    }

    /// <summary>The data channels move as BYTES, so exactness holds for every value either can take —
    /// all 256×256 of them.</summary>
    [Fact]
    public void PackedNormal_RoundTripIsExactOverEveryByteValue()
    {
        using var g = new TempGame();
        var src = Path.Combine(g.Root, "all.png");
        using (var img = new Image<Rgba32>(256, 256))
        {
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                    img[x, y] = new Rgba32(0, (byte)y, 0, (byte)x);   // every (Y, X) pair
            img.SaveAsPng(src);
        }

        var original = Pixels(File.ReadAllBytes(src));
        var back = Pixels(PreviewMaps.FromPreview(PreviewMaps.ToPreview(src, MapKind.Normal), MapKind.Normal));

        for (int i = 0; i < original.Length; i++)
        {
            Assert.Equal(original[i].A, back[i].A);
            Assert.Equal(original[i].G, back[i].G);
        }
    }

    /// <summary>What the game reads must not drift when a normal makes the trip and comes back untouched:
    /// re-deriving the preview from the repacked map reproduces the preview exactly.</summary>
    [Fact]
    public void PackedNormal_PreviewIsStableAcrossARoundTrip()
    {
        using var g = new TempGame();
        var src = WritePng(Path.Combine(g.Root, "n.png"));
        var preview = PreviewMaps.ToPreview(src, MapKind.Normal);

        var repacked = Path.Combine(g.Root, "n2.png");
        File.WriteAllBytes(repacked, PreviewMaps.FromPreview(preview, MapKind.Normal));

        Assert.Equal(Pixels(preview), Pixels(PreviewMaps.ToPreview(repacked, MapKind.Normal)));
    }

    // ---------------------------------------------------------------- identity through a glb

    private static (string Glb, string Base, string Normal, string Rmo) ExportPatch(TempGame g,
        string glbName = "patch.glb", int baseSeed = 0)
    {
        var baseColor = WritePng(Path.Combine(g.Root, $"body_d{baseSeed}.png"), baseSeed);
        var normal = WritePng(Path.Combine(g.Root, "body_n.png"), 7);
        var rmo = WritePng(Path.Combine(g.Root, "body_r.png"), 13);
        var glb = Path.Combine(g.Root, glbName);
        MeshGltf.ExportGlb(TwoSubmeshPatch(), glb, perSubmesh: new (string?, string?, string?)[]
        {
            (baseColor, normal, rmo),
            (baseColor, null, null),    // second submesh shares the base map and has neither of the others
        });
        return (glb, baseColor, normal, rmo);
    }

    [Fact]
    public void Export_WritesASidecarForEveryEmbeddedImage()
    {
        using var g = new TempGame();
        var (glb, baseColor, normal, rmo) = ExportPatch(g);

        var sidecar = PreviewMaps.ReadSidecar(glb);
        Assert.True(File.Exists(PreviewMaps.SidecarPath(glb)));
        // the shared base map is embedded once, so three sources across four slot uses
        Assert.Equal(3, sidecar.Count);
        Assert.Contains(sidecar.Values, e => e.Source == Path.GetFullPath(baseColor) && e.Kind == MapKind.BaseColor);
        Assert.Contains(sidecar.Values, e => e.Source == Path.GetFullPath(normal) && e.Kind == MapKind.Normal);
        Assert.Contains(sidecar.Values, e => e.Source == Path.GetFullPath(rmo) && e.Kind == MapKind.Rmo);
    }

    [Fact]
    public void UntouchedGlb_ResolvesEverySlotAsVanilla()
    {
        using var g = new TempGame();
        var (glb, baseColor, normal, rmo) = ExportPatch(g);

        var maps = MeshGltf.ReadSubmeshMaps(glb);

        Assert.Equal(2, maps.Count);
        Assert.Equal(MapOrigin.Vanilla, maps[0].BaseColor.Origin);
        Assert.Equal(Path.GetFullPath(baseColor), maps[0].BaseColor.StockPng);
        Assert.Equal(MapOrigin.Vanilla, maps[0].Normal.Origin);
        Assert.Equal(Path.GetFullPath(normal), maps[0].Normal.StockPng);
        Assert.Equal(MapOrigin.Vanilla, maps[0].Rmo.Origin);
        Assert.Equal(Path.GetFullPath(rmo), maps[0].Rmo.StockPng);
        Assert.Equal(MapOrigin.Vanilla, maps[1].BaseColor.Origin);
        Assert.Equal(MapOrigin.None, maps[1].Normal.Origin);     // no normal on that submesh
        Assert.Equal(MapOrigin.None, maps[1].Rmo.Origin);        // nor an RMO
    }

    /// <summary>Swapped for another file or painted in place — both are a content miss, and both must ship
    /// as authored, converted back to stock-PNG space.</summary>
    [Fact]
    public void ChangedImage_ResolvesAsAuthoredInStockSpace()
    {
        using var g = new TempGame();
        var (glb, _, _, _) = ExportPatch(g);
        var sidecar = File.ReadAllBytes(PreviewMaps.SidecarPath(glb));

        // Re-export with a different base map, then restore the original sidecar: the glb now carries an
        // image the app never embedded, which is what a Blender swap produces.
        var (glb2, swapped, _, _) = ExportPatch(g, "patch.glb", baseSeed: 99);
        Assert.Equal(glb, glb2);
        File.WriteAllBytes(PreviewMaps.SidecarPath(glb), sidecar);

        var maps = MeshGltf.ReadSubmeshMaps(glb);

        Assert.Equal(MapOrigin.Authored, maps[0].BaseColor.Origin);
        Assert.Equal(MapOrigin.Vanilla, maps[0].Normal.Origin);            // the untouched slot is unaffected
        Assert.Equal(Pixels(File.ReadAllBytes(swapped)), Pixels(maps[0].BaseColor.AuthoredPng!));
    }

    /// <summary>No sidecar means nothing can be proven stock, so every slot ships authored — a redundant
    /// copy of a stock map, where the alternative loses an authored one.</summary>
    [Fact]
    public void MissingSidecar_ResolvesEverySlotAsAuthored()
    {
        using var g = new TempGame();
        var (glb, _, _, _) = ExportPatch(g);
        File.Delete(PreviewMaps.SidecarPath(glb));

        var maps = MeshGltf.ReadSubmeshMaps(glb);

        Assert.All(maps.Where(m => m.BaseColor.Origin != MapOrigin.None),
                   m => Assert.Equal(MapOrigin.Authored, m.BaseColor.Origin));
    }

    [Fact]
    public void MaterialFreeGlb_ResolvesEverySlotAsNoneAndLeavesNoSidecar()
    {
        using var g = new TempGame();
        var glb = Path.Combine(g.Root, "bare.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), glb);

        Assert.False(File.Exists(PreviewMaps.SidecarPath(glb)));
        Assert.All(MeshGltf.ReadSubmeshMaps(glb), m =>
        {
            Assert.Equal(MapOrigin.None, m.BaseColor.Origin);
            Assert.Equal(MapOrigin.None, m.Normal.Origin);
            Assert.Equal(MapOrigin.None, m.Rmo.Origin);
        });
    }

    /// <summary>A base map must never resolve against a normal entry that happens to hash the same, so the
    /// slot is part of the match.</summary>
    [Fact]
    public void Resolve_RequiresTheKindToMatch()
    {
        using var g = new TempGame();
        var png = WritePng(Path.Combine(g.Root, "m.png"));
        var bytes = PreviewMaps.ToPreview(png, MapKind.BaseColor);
        var sidecar = new System.Collections.Generic.Dictionary<(string, MapKind), PreviewMaps.Entry>
        {
            [(PreviewMaps.Hash(bytes), MapKind.Normal)] =
                new PreviewMaps.Entry(PreviewMaps.Hash(bytes), png, MapKind.Normal),
        };

        Assert.Equal(MapOrigin.Authored, PreviewMaps.Resolve(bytes, MapKind.BaseColor, sidecar).Origin);
        Assert.Equal(MapOrigin.Vanilla, PreviewMaps.Resolve(bytes, MapKind.Normal, sidecar).Origin);
    }

    // ---------------------------------------------------------------- base colour: no transform

    /// <summary>glTF reads a base map's channels exactly as the game packs them, so nothing may
    /// reinterpret them in either direction: the pair is the identity on every byte of every
    /// channel.</summary>
    [Fact]
    public void ABaseColour_TravelsVerbatimInItsOwnChannelLayout()
    {
        using var g = new TempGame();
        var src = WritePng(Path.Combine(g.Root, "d.png"));
        var preview = PreviewMaps.ToPreview(src, MapKind.BaseColor);
        var back = PreviewMaps.FromPreview(preview, MapKind.BaseColor);

        Assert.Equal(Pixels(File.ReadAllBytes(src)), Pixels(preview));
        Assert.Equal(Pixels(File.ReadAllBytes(src)), Pixels(back));
    }

    // ---------------------------------------------------------------- the RMO channel permutation

    /// <summary>The game packs R roughness, G metallic, B occlusion; glTF's ORM wants R occlusion,
    /// G roughness, B metallic. Alpha carries the emissive mask, which glTF has no slot for, and rides
    /// unchanged.</summary>
    [Fact]
    public void Rmo_IsPermutedIntoTheGltfOrmOrder()
    {
        using var g = new TempGame();
        var src = WritePng(Path.Combine(g.Root, "rmo.png"));
        var stock = Pixels(File.ReadAllBytes(src));
        var preview = Pixels(PreviewMaps.ToPreview(src, MapKind.Rmo));

        Assert.Equal(stock.Length, preview.Length);
        for (int i = 0; i < stock.Length; i++)
        {
            Assert.Equal(stock[i].B, preview[i].R);   // occlusion
            Assert.Equal(stock[i].R, preview[i].G);   // roughness
            Assert.Equal(stock[i].G, preview[i].B);   // metallic
            Assert.Equal(stock[i].A, preview[i].A);   // emissive mask
        }
    }

    /// <summary>A permutation is exactly invertible, so the pair is lossless on every byte an RMO can
    /// carry.</summary>
    [Fact]
    public void Rmo_SurvivesToPreviewAndBack()
    {
        using var g = new TempGame();
        var src = WritePng(Path.Combine(g.Root, "rmo.png"));
        var back = PreviewMaps.FromPreview(PreviewMaps.ToPreview(src, MapKind.Rmo), MapKind.Rmo);
        Assert.Equal(Pixels(File.ReadAllBytes(src)), Pixels(back));
    }

    /// <summary>Exactness is a property of the transform, not of the fixture: it must hold for arbitrary
    /// RGBA, so a deterministic pseudo-random image stands in for every value combination.</summary>
    [Fact]
    public void Rmo_RoundTripIsExactOverRandomPixels()
    {
        using var g = new TempGame();
        var src = Path.Combine(g.Root, "noise.png");
        var rng = new System.Random(20260727);
        using (var img = new Image<Rgba32>(64, 64))
        {
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                    img[x, y] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256),
                                           (byte)rng.Next(256), (byte)rng.Next(256));
            img.SaveAsPng(src);
        }

        var original = Pixels(File.ReadAllBytes(src));
        var back = Pixels(PreviewMaps.FromPreview(PreviewMaps.ToPreview(src, MapKind.Rmo), MapKind.Rmo));
        Assert.Equal(original, back);
    }

    /// <summary>The RMO embeds like any other slot, so the sidecar records it and an untouched one comes
    /// back stock.</summary>
    [Fact]
    public void AnExportEmbedsTheRmo_AndTheSidecarRecordsIt()
    {
        using var g = new TempGame();
        var (glb, baseColor, normal, rmo) = ExportPatch(g);

        var sidecar = PreviewMaps.ReadSidecar(glb);
        var embedded = sidecar.Values.Where(e => e.Origin == MapOrigin.Vanilla).ToList();
        Assert.Equal(3, embedded.Count);
        Assert.Contains(embedded, e => e.Source == Path.GetFullPath(baseColor) && e.Kind == MapKind.BaseColor);
        Assert.Contains(embedded, e => e.Source == Path.GetFullPath(normal) && e.Kind == MapKind.Normal);
        Assert.Contains(embedded, e => e.Source == Path.GetFullPath(rmo) && e.Kind == MapKind.Rmo);
    }

    // ---------------------------------------------------------------- the alpha source rides the sidecar

    private static byte[] FlatPngBytes(Rgba32 color, int size = 8)
    {
        using var img = new Image<Rgba32>(size, size, color);
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static string FlatPng(string path, Rgba32 color)
    {
        File.WriteAllBytes(path, FlatPngBytes(color));
        return path;
    }

    private static Rgba32 FirstPixel(string path)
    {
        using var img = Image.Load<Rgba32>(path);
        return img[0, 0];
    }

    /// <summary>What a Blender session that repainted an RMO hands back: the same glb carrying a DIFFERENT
    /// image on submesh 0's ORM pair, over the record the export wrote.</summary>
    private static string ExportThenRepaintRmo(TempGame g, Rgba32 stock, Rgba32 painted)
    {
        var glb = Path.Combine(g.Root, "rmo.glb");
        Export(FlatPng(Path.Combine(g.Root, "stock_r.png"), stock));
        var record = File.ReadAllBytes(PreviewMaps.SidecarPath(glb));
        Export(FlatPng(Path.Combine(g.Root, "painted_r.png"), painted));
        File.WriteAllBytes(PreviewMaps.SidecarPath(glb), record);
        return glb;

        void Export(string rmo) => MeshGltf.ExportGlb(TwoSubmeshPatch(), glb,
            perSubmesh: new (string?, string?, string?)[] { (null, null, rmo), default });
    }

    /// <summary>The intake as the app wires it: the alpha source is the returned glb's own record, read
    /// with nothing else consulted.</summary>
    private static List<SubmeshTextures>? CollectFrom(string glb, IReadOnlyList<IncomingMaps> maps, string texDir)
    {
        var sources = PreviewMaps.ReadSubmeshRmoSources(glb);
        return DonorTextureIntake.Collect(maps, texDir, "body", p => p, null,
            i => sources.TryGetValue(i, out var png) ? png : null);
    }

    /// <summary>Alpha carries the emissive mask and has no glTF channel to ride in, so the export records
    /// which stock RMO each submesh was given. A submesh that got none records none.</summary>
    [Fact]
    public void AnExportRecordsTheStockRmoBehindEachSubmesh()
    {
        using var g = new TempGame();
        var (glb, _, _, rmo) = ExportPatch(g);

        var sources = PreviewMaps.ReadSubmeshRmoSources(glb);

        Assert.Equal(Path.GetFullPath(rmo), Assert.Contains(0, sources));
        Assert.False(sources.ContainsKey(1));   // the second submesh was given no RMO
    }

    /// <summary>An authored RMO's mask comes off the map the EXPORT embedded on that submesh, read from the
    /// glb's own record — no view of the app's state enters the decision.</summary>
    [Fact]
    public void AnAuthoredRmo_TakesItsMaskFromTheGlbsOwnRecord()
    {
        using var g = new TempGame();
        var glb = ExportThenRepaintRmo(g, stock: new Rgba32(9, 9, 9, 77), painted: new Rgba32(10, 20, 30, 255));
        var maps = MeshGltf.ReadSubmeshMaps(glb);

        var rows = CollectFrom(glb, maps, Path.Combine(g.Root, "textures"));

        Assert.Equal(MapOrigin.Authored, maps[0].Rmo.Origin);
        Assert.Equal(new Rgba32(10, 20, 30, 77), FirstPixel(Assert.Single(rows!).Rmo!));
    }

    /// <summary>Same glb in, same mod out. The record travels with the file, so a second send of the very
    /// same returned glb cannot ship a different mask from the first.</summary>
    [Fact]
    public void CollectingTheSameReturnedGlbTwice_ShipsTheSameRmoBytes()
    {
        using var g = new TempGame();
        var glb = ExportThenRepaintRmo(g, stock: new Rgba32(9, 9, 9, 77), painted: new Rgba32(10, 20, 30, 255));
        var maps = MeshGltf.ReadSubmeshMaps(glb);

        var first = CollectFrom(glb, maps, Path.Combine(g.Root, "send1"));
        var second = CollectFrom(glb, maps, Path.Combine(g.Root, "send2"));

        Assert.Equal(File.ReadAllBytes(Assert.Single(first!).Rmo!),
                     File.ReadAllBytes(Assert.Single(second!).Rmo!));
    }

    /// <summary>A submesh past the recorded mapping — a brand-new one the modder added in Blender — has no
    /// mask of its own, so it ships a zero one rather than an unrelated submesh's.</summary>
    [Fact]
    public void AnAuthoredRmoOnAnUnrecordedSubmesh_ShipsAZeroMask()
    {
        using var g = new TempGame();
        var glb = ExportThenRepaintRmo(g, stock: new Rgba32(9, 9, 9, 77), painted: new Rgba32(10, 20, 30, 255));
        var none = new ResolvedMap(MapOrigin.None);

        var rows = CollectFrom(glb, new List<IncomingMaps>
        {
            new(none, none),
            new(none, none, new ResolvedMap(MapOrigin.Authored, AuthoredPng: FlatPngBytes(new Rgba32(1, 2, 3, 255)))),
        }, Path.Combine(g.Root, "textures"));

        var row = Assert.Single(rows!);
        Assert.Equal(1, row.Submesh);
        Assert.Equal(new Rgba32(1, 2, 3, 0), FirstPixel(row.Rmo!));
    }

    // ---------------------------------------------------------------- the shipped flat maps

    /// <summary>Only the neutral normal is a sentinel, so it is the only flat map the sidecar records. The
    /// flat RMO beside it is plain workspace content.</summary>
    [Fact]
    public void TheSidecarRecordsTheNeutralNormal_AndNotTheFlatRmo()
    {
        using var g = new TempGame();
        PreviewMaps.WriteNeutrals(g.Root);
        var (glb, _, _, _) = ExportPatch(g);

        var sidecar = PreviewMaps.ReadSidecar(glb);
        var neutral = Assert.Single(sidecar.Values, e => e.Origin == MapOrigin.Neutral);
        Assert.Equal(MapKind.Normal, neutral.Kind);
        Assert.Equal(PreviewMaps.NeutralN, Path.GetFileName(neutral.Source));
        // the stock entries are untouched by its arrival
        Assert.Equal(3, sidecar.Values.Count(e => e.Origin == MapOrigin.Vanilla));
    }

    /// <summary>Plugging the neutral normal into a normal slot is how a modder blanks it. It must not read as
    /// an authored map — that would ship a redundant flat DDS instead of the build's own neutral bind. The
    /// flat RMO carries no such meaning: an RMO holding its content is a map like any other.</summary>
    [Fact]
    public void APluggedNeutral_ResolvesAsTheNeutralOrigin_OnTheNormalSlotOnly()
    {
        using var g = new TempGame();
        PreviewMaps.WriteNeutrals(g.Root);
        var (glb, _, _, _) = ExportPatch(g);
        var sidecar = PreviewMaps.ReadSidecar(glb);

        var flatN = File.ReadAllBytes(Path.Combine(g.Root, PreviewMaps.NeutralN));
        var flatRmo = File.ReadAllBytes(Path.Combine(g.Root, PreviewMaps.NeutralRmo));

        Assert.Equal(MapOrigin.Neutral, PreviewMaps.Resolve(flatN, MapKind.Normal, sidecar).Origin);
        Assert.Equal(MapOrigin.Authored, PreviewMaps.Resolve(flatRmo, MapKind.Rmo, sidecar).Origin);
        // the kind is part of the match, so the neutral normal in a base-colour slot is just an image
        Assert.Equal(MapOrigin.Authored, PreviewMaps.Resolve(flatN, MapKind.BaseColor, sidecar).Origin);
    }

    /// <summary>The neutral normal is the build's own content, not a decoration: a slot filled with it has to
    /// blank exactly what the build's flat map blanks. Each of the two sits in the space its Blender slot
    /// reads — the normal flat in tangent space, the RMO in glTF's ORM order.</summary>
    [Fact]
    public void TheWorkspaceNeutrals_CarryTheFlatContentTheBuildBinds()
    {
        using var g = new TempGame();
        PreviewMaps.WriteNeutrals(g.Root);

        Assert.All(Pixels(File.ReadAllBytes(Path.Combine(g.Root, PreviewMaps.NeutralN))),
            p => Assert.Equal(new Rgba32(128, 128, 255, 255), p));
        Assert.All(Pixels(File.ReadAllBytes(Path.Combine(g.Root, PreviewMaps.NeutralRmo))),
            p => Assert.Equal(new Rgba32(255, 128, 0, 0), p));
    }

    /// <summary>The RMO permutation is the identity wherever R==G==B, so a grayscale map embedded in two
    /// slots hashes the same in both. Keyed on the hash alone one entry would swallow the other and an
    /// UNTOUCHED map would come back authored.</summary>
    [Fact]
    public void OneImageEmbeddedInTwoSlots_KeepsAnEntryPerSlot()
    {
        using var g = new TempGame();
        var gray = Path.Combine(g.Root, "gray.png");
        using (var img = new Image<Rgba32>(8, 8))
        {
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    img[x, y] = new Rgba32((byte)(x * 9), (byte)(x * 9), (byte)(x * 9), (byte)(y * 9));
            img.SaveAsPng(gray);
        }
        var glb = Path.Combine(g.Root, "gray.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), glb, perSubmesh: new (string?, string?, string?)[]
        {
            (gray, null, gray),
            default,
        });

        var sidecar = PreviewMaps.ReadSidecar(glb);

        Assert.Equal(2, sidecar.Count);
        Assert.Equal(MapOrigin.Vanilla, MeshGltf.ReadSubmeshMaps(glb)[0].Rmo.Origin);
        Assert.Equal(MapOrigin.Vanilla, MeshGltf.ReadSubmeshMaps(glb)[0].BaseColor.Origin);
    }
}
