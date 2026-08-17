using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Remold.Core.Export;
using Remold.Core.Materials;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
// aliased, not imported: Remold.Core.Textures also carries a SubmeshMaps, and this file means the Migoto one
using Bc7Encoder = Remold.Core.Textures.Bc7Encoder;
using Remold.Core.Workbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// <see cref="ModBuilder"/> over a synthetic world, driven the product way: verbs DERIVED from project
/// state. The hide, retexture and Replace builds end to end, plus the loud validation failures. The Replace
/// route runs on a skinned synthetic outfit and a donor glb weighted to it, which is enough for the
/// orchestration; the fidelity of what it emits is proven on real data in the private harness.
/// </summary>
public class ModBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-mb-" + Guid.NewGuid().ToString("N"));
    private readonly string _proj;
    private readonly string _out;

    public ModBuilderTests()
    {
        _proj = Path.Combine(_root, "proj");
        _out = Path.Combine(_root, "build");
        Directory.CreateDirectory(_proj);
        Directory.CreateDirectory(_out);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ---- the synthetic world: one subject, one part with a lod1 sibling, two bundles --------------

    private static readonly float[] Positions =
        { 0, 0, 0,  1, 0, 0,  0, 1, 0,  1, 1, 0 };
    private static readonly int[] Tris = { 0, 1, 2,  2, 1, 3 };

    /// <summary>The stock texture's own 3DMigoto resource hash — what the retexture override keys on.</summary>
    private string _stockTexHash = "";

    /// <summary>The second stock base color's hash: what a part binding a base color of its own carries.</summary>
    private string _altTexHash = "";

    /// <summary>Seams for sibling test classes driving the same synthetic world.</summary>
    internal string StockTexHash => _stockTexHash;
    internal string OutRoot => _out;

    internal BuildEnv MakeEnv(out string lod0Hash, out string lod1Hash, int stockColorSpace = 0)
    {
        string b0 = Path.Combine(_root, "b0.bundle");
        string b1 = Path.Combine(_root, "b1.bundle");
        string bt = Path.Combine(_root, "bt.bundle");
        SyntheticBundle.BuildOneMesh(b0, "c_vesna01_body_lod0", Positions, Tris);
        SyntheticBundle.BuildOneMesh(b1, "c_vesna01_body_lod1", Positions, Tris.Take(3).ToArray());
        // The override keys on the STOCK texture's hash, so the build reads it out of the game bundles and
        // not out of the authored replacement.
        SyntheticBundle.BuildOneTexture(bt, "tex_body_d", 8, 8, 200, 100, 50, 255, colorSpace: stockColorSpace);
        var bytes = new Dictionary<string, byte[]>
        {
            ["bundle0"] = File.ReadAllBytes(b0),
            ["bundle1"] = File.ReadAllBytes(b1),
            ["bundleT"] = File.ReadAllBytes(bt),
        };
        lod0Hash = BufferHash.Compute(bytes["bundle0"], "c_vesna01_body_lod0").Ib.ToString("x8");
        lod1Hash = BufferHash.Compute(bytes["bundle1"], "c_vesna01_body_lod1").Ib.ToString("x8");
        var stock = new Remold.Core.Bundles.BundleReader().GetTextureHashSource(bytes["bundleT"], "tex_body_d")!.Value;
        _stockTexHash = TextureHash.Compute(stock.PictureData, stock.Width, stock.Height, stock.MipCount,
            TextureHash.Dxgi((AssetsTools.NET.Texture.TextureFormat)stock.Format, stock.Srgb)!.Value).ToString("x8");

        // one material whose _BaseMap is the synthetic texture — what the retexture derivation maps through
        var materials = new[]
        {
            new SubjectMaterial("m_body", 1, "cab-body",
                new[] { new SubjectMap("_BaseMap", "tex_body_d", "bundleT") }),
        };
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_vesna01_body_lod0", "addr_body", materials,
                SiblingTiers: new[] { new RecipeTierSlot("c_vesna01_body_lod1", "addr_body_l1") }),
        }, Skeleton: null, Problems: Array.Empty<string>());

        var addresses = new Dictionary<string, string> { ["addr_body"] = "bundle0", ["addr_body_l1"] = "bundle1" };
        return new BuildEnv(
            (c, s) => c == "Vesna" && s == "VesnaSSR01" ? model : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0");
    }

    /// <summary>The same one-part world with a <c>lodm0</c> sibling between the two shipped tiers.
    /// <paramref name="token"/> + <paramref name="variant"/> shape the slot names: the LOD marker is
    /// commonly INFIXED in real data (<c>…_P1_body1_lodm0_Dorm</c>), so the same world can be built with
    /// the tier marker at the tail or inside the name.</summary>
    private BuildEnv MakeMidTierEnv(out string lod0Hash, out string midHash, out string lod1Hash,
        string token = "body", string variant = "", string mid = "lodm0")
    {
        string lod0Name = $"c_vesna01_{token}_lod0{variant}";
        string midName = $"c_vesna01_{token}_{mid}{variant}";
        string lod1Name = $"c_vesna01_{token}_lod1{variant}";

        string b0 = Path.Combine(_root, "m0.bundle");
        string bm = Path.Combine(_root, "mm.bundle");
        string b1 = Path.Combine(_root, "m1.bundle");
        SyntheticBundle.BuildOneMesh(b0, lod0Name, Positions, Tris);
        // each tier needs its OWN index buffer: the override hash is the index buffer's, so two tiers
        // drawing the same triangles would collapse to one hash and prove nothing
        SyntheticBundle.BuildOneMesh(bm, midName, Positions, new[] { 0, 1, 3 });
        SyntheticBundle.BuildOneMesh(b1, lod1Name, Positions, Tris.Take(3).ToArray());
        var bytes = new Dictionary<string, byte[]>
        {
            ["bundle0"] = File.ReadAllBytes(b0),
            ["bundleM"] = File.ReadAllBytes(bm),
            ["bundle1"] = File.ReadAllBytes(b1),
        };
        lod0Hash = BufferHash.Compute(bytes["bundle0"], lod0Name).Ib.ToString("x8");
        midHash = BufferHash.Compute(bytes["bundleM"], midName).Ib.ToString("x8");
        lod1Hash = BufferHash.Compute(bytes["bundle1"], lod1Name).Ib.ToString("x8");

        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart(token + variant, lod0Name, "addr_body", Array.Empty<SubjectMaterial>(),
                SiblingTiers: new[]
                {
                    new RecipeTierSlot(midName, "addr_body_m0"),
                    new RecipeTierSlot(lod1Name, "addr_body_l1"),
                }),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var addresses = new Dictionary<string, string>
        {
            ["addr_body"] = "bundle0", ["addr_body_m0"] = "bundleM", ["addr_body_l1"] = "bundle1",
        };
        return new BuildEnv(
            (c, s) => c == "Vesna" && s == "VesnaSSR01" ? model : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0");
    }

    internal ModProject NewProject(string name = "Test Mod")
    {
        var p = new ModProject { RootDir = _proj };
        p.Info.Name = name;
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        return p;
    }

    /// <summary>An EDITED texture target: no original on record = edited by definition. A real DDS, so the
    /// encode pass-through route accepts it.</summary>
    internal void AddEditedTexture(ModProject p, string file = "skin.dds",
        string user = "c_vesna01_body_lod0", string bundle = "bundleT", string objectName = "tex_body_d")
    {
        FlatDds.Write(Path.Combine(_proj, file), (1, 2, 3, 255));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = bundle, ObjectName = objectName,
            ReplaceFile = file, Users = new List<string> { user },
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
        });
    }

    /// <summary>The same target authored as a PNG, so the build encodes rather than passes through.</summary>
    private void AddEditedPng(ModProject p, string file = "skin.png")
    {
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, file));
        AddPngTarget(p, file);
    }

    /// <summary>The PNG target authored as an image with content in every 4×4 block and several mip levels
    /// — what an encode is actually asked to do.</summary>
    private void AddDetailedPng(ModProject p, string file = "skin.png", int size = 128)
    {
        using (var img = new Image<Rgba32>(size, size))
        {
            var rng = new Random(1234);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    img[x, y] = new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256),
                        (byte)(128 + rng.Next(128)));
            img.SaveAsPng(Path.Combine(_proj, file));
        }
        AddPngTarget(p, file);
    }

    private void AddPngTarget(ModProject p, string file)
    {
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bundleT", ObjectName = "tex_body_d",
            ReplaceFile = file, Users = new List<string> { "c_vesna01_body_lod0" },
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
        });
    }

    // ---- the routes ------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 98u)]   // linear stock  → BC7_UNORM
    [InlineData(1, 99u)]   // sRGB stock    → BC7_UNORM_SRGB
    public void An_authored_replacement_inherits_the_stock_textures_srgb_family(int stockColorSpace, uint expectedDxgi)
    {
        // The sRGB→linear conversion happens ON SAMPLE, so the tag belongs to the SLOT: the replacement has
        // to be in the same family as the stock texture's DXGI format.
        var env = MakeEnv(out _, out _, stockColorSpace);
        var p = NewProject("Srgb");
        AddEditedPng(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        var dds = File.ReadAllBytes(Path.Combine(r.OutDir, "rtx_skin_a.dds"));
        Assert.Equal(expectedDxgi, BitConverter.ToUInt32(dds, 128));   // DDS_HEADER_DXT10.dxgiFormat
    }

    /// <summary>One part binding TWO materials, one linear and one sRGB stock albedo — the shape where one
    /// authored image replaces slots of both families.</summary>
    private BuildEnv MakeTwoFamilyEnv()
    {
        string bt = Path.Combine(_root, "bt2.bundle");
        SyntheticBundle.Build(bt,
            new SyntheticBundle.TextureSpec("tex_lin_d", 8, 8,
                SyntheticBundle.SolidRgba32(8, 8, 200, 100, 50, 255), ColorSpace: 0),
            new SyntheticBundle.TextureSpec("tex_srgb_d", 8, 8,
                SyntheticBundle.SolidRgba32(8, 8, 50, 100, 200, 255), ColorSpace: 1));
        var bytes = new Dictionary<string, byte[]> { ["bundleT"] = File.ReadAllBytes(bt) };
        var materials = new[]
        {
            new SubjectMaterial("m_lin", 1, "cab-lin", new[] { new SubjectMap("_BaseMap", "tex_lin_d", "bundleT") }),
            new SubjectMaterial("m_srgb", 2, "cab-srgb", new[] { new SubjectMap("_BaseMap", "tex_srgb_d", "bundleT") }),
        };
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_vesna01_body_lod0", "addr_body", materials),
        }, Skeleton: null, Problems: Array.Empty<string>());
        return new BuildEnv((c, s) => c == "Vesna" && s == "VesnaSSR01" ? model : null,
            _ => null, id => bytes.GetValueOrDefault(id), CatalogVersion: "12345", AppVersion: "test-1.0");
    }

    [Fact]
    public void One_image_replacing_both_an_srgb_and_a_linear_slot_ships_both_tags()
    {
        // The tag is baked into the encoded file, so two families are two files — a normal authoring choice
        // (a mask reused as colour), not a conflict to refuse the build over.
        var env = MakeTwoFamilyEnv();
        var p = NewProject("Both");
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "skin.png"));
        foreach (var tex in new[] { "tex_lin_d", "tex_srgb_d" })
            p.Targets.Add(new ProjectTarget
            {
                AssetType = "Texture2D", Bundle = "bundleT", ObjectName = tex,
                ReplaceFile = "skin.png", Users = new List<string> { "c_vesna01_body_lod0" },
                SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            });

        var r = ModBuilder.Build(p, env, _out, zip: false);

        // the first family keeps the plain name; the second is suffixed with its own
        Assert.Equal(98u, Dxgi(Path.Combine(r.OutDir, "rtx_skin_a.dds")));         // BC7_UNORM
        Assert.Equal(99u, Dxgi(Path.Combine(r.OutDir, "rtx_skin_a_srgb.dds")));    // BC7_UNORM_SRGB
        Assert.Equal(2, Directory.GetFiles(r.OutDir, "rtx_*.dds").Length);
        // both stock textures are overridden, each pointing at its own file
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Equal(2, CountOf(ini, "[TextureOverride_Retex_"));
        Assert.Equal(2, CountOf(ini, "[Resource_Rtx"));
    }

    /// <summary>One material binding an albedo AND a normal slot in the same sRGB family — the shape where
    /// one authored image is asked to replace both.</summary>
    private BuildEnv MakeTwoKindEnv()
    {
        string bt = Path.Combine(_root, "bt3.bundle");
        SyntheticBundle.Build(bt,
            new SyntheticBundle.TextureSpec("tex_kind_d", 8, 8,
                SyntheticBundle.SolidRgba32(8, 8, 200, 100, 50, 255), ColorSpace: 0),
            new SyntheticBundle.TextureSpec("tex_kind_n", 8, 8,
                SyntheticBundle.SolidRgba32(8, 8, 128, 128, 255, 255), ColorSpace: 0));
        var bytes = new Dictionary<string, byte[]> { ["bundleT"] = File.ReadAllBytes(bt) };
        var materials = new[]
        {
            new SubjectMaterial("m_body", 1, "cab-body", new[]
            {
                new SubjectMap("_BaseMap", "tex_kind_d", "bundleT"),
                new SubjectMap("_BumpMap", "tex_kind_n", "bundleT"),
            }),
        };
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_vesna01_body_lod0", "addr_body", materials),
        }, Skeleton: null, Problems: Array.Empty<string>());
        return new BuildEnv((c, s) => c == "Vesna" && s == "VesnaSSR01" ? model : null,
            _ => null, id => bytes.GetValueOrDefault(id), CatalogVersion: "12345", AppVersion: "test-1.0");
    }

    [Fact]
    public void One_image_replacing_two_map_kinds_of_one_family_ships_a_single_file()
    {
        // The encoded bytes are a property of the pixels and the sRGB tag; nothing in them says "normal".
        // Two sections binding one file is the whole difference from encoding it twice.
        var env = MakeTwoKindEnv();
        var p = NewProject("Kinds");
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "skin.png"));
        foreach (var tex in new[] { "tex_kind_d", "tex_kind_n" })
            p.Targets.Add(new ProjectTarget
            {
                AssetType = "Texture2D", Bundle = "bundleT", ObjectName = tex,
                ReplaceFile = "skin.png", Users = new List<string> { "c_vesna01_body_lod0" },
                SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            });

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.Single(Directory.GetFiles(r.OutDir, "rtx_*.dds"));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Equal(2, CountOf(ini, "[TextureOverride_Retex_"));
        Assert.Equal(1, CountOf(ini, "[Resource_Rtx"));
    }

    [Fact]
    public void An_encode_is_reused_across_builds_from_the_texture_cache()
    {
        // Keyed by source CONTENT, so a second project authoring the same pixels reads the first build's
        // encode instead of paying for it again.
        var caches = new BuildCaches(Path.Combine(_root, "cache-op"), Path.Combine(_root, "cache-tex"));
        var env = MakeEnv(out _, out _);

        var first = NewProject("EncOnce");
        AddEditedPng(first);
        var a = ModBuilder.Build(first, env, _out, zip: false, caches: caches);
        var encoded = File.ReadAllBytes(Path.Combine(a.OutDir, "rtx_skin_a.dds"));
        Assert.Single(Directory.GetFiles(caches.TextureDir, "*.dds"));

        // a differently-named copy of the same image: same content, same entry
        File.Copy(Path.Combine(_proj, "skin.png"), Path.Combine(_proj, "skin_copy.png"));
        var second = NewProject("EncAgain");
        AddEditedPng(second, "skin_copy.png");
        var b = ModBuilder.Build(second, env, _out, zip: false, caches: caches);

        Assert.Equal(encoded, File.ReadAllBytes(Path.Combine(b.OutDir, "rtx_skin_copy_a.dds")));
        Assert.Single(Directory.GetFiles(caches.TextureDir, "*.dds"));
    }

    /// <summary>The one cached encode of the standard PNG target, and the byte count published beside
    /// it.</summary>
    private static (string Entry, string Length) TextureEntry(BuildCaches caches)
    {
        string entry = Assert.Single(Directory.GetFiles(caches.TextureDir, "*.dds"));
        return (entry, entry + ".len");
    }

    [Fact]
    public void A_damaged_dds_cache_entry_is_not_shipped_verbatim()
    {
        // A cache entry is copied into the mod and never read again: damage in it reaches the author's game
        // as a map that binds to nothing, under a build that reported success. Both halves of the check earn
        // their place — a truncation the recorded length catches, and a corrupt header the magic catches.
        var caches = new BuildCaches(Path.Combine(_root, "cache-op"), Path.Combine(_root, "cache-tex"));
        var env = MakeEnv(out _, out _);
        var p = NewProject("Damaged");
        AddEditedPng(p);

        var cold = ModBuilder.Build(p, env, _out, zip: false, caches: caches);
        var good = File.ReadAllBytes(Path.Combine(cold.OutDir, "rtx_skin_a.dds"));
        var (entry, length) = TextureEntry(caches);

        // truncated: the header still reads, the recorded byte count does not
        File.WriteAllBytes(entry, good.Take(good.Length / 2).ToArray());
        var afterTruncation = ModBuilder.Build(p, env, _out, zip: false, caches: caches);
        Assert.Equal(good, File.ReadAllBytes(Path.Combine(afterTruncation.OutDir, "rtx_skin_a.dds")));
        Assert.Equal(good, File.ReadAllBytes(entry));                       // and the entry was republished
        Assert.Equal(good.Length.ToString(), File.ReadAllText(length));

        // corrupt header at the right length: only the magic separates it from a sound entry
        var headerless = good.ToArray();
        Array.Clear(headerless, 0, 4);
        File.WriteAllBytes(entry, headerless);
        var afterCorruption = ModBuilder.Build(p, env, _out, zip: false, caches: caches);
        Assert.Equal(good, File.ReadAllBytes(Path.Combine(afterCorruption.OutDir, "rtx_skin_a.dds")));
        Assert.Equal(good, File.ReadAllBytes(entry));

        // an entry with no recorded length at all is nothing this build published: it is not served either
        File.Delete(length);
        var afterLoss = ModBuilder.Build(p, env, _out, zip: false, caches: caches);
        Assert.Equal(good, File.ReadAllBytes(Path.Combine(afterLoss.OutDir, "rtx_skin_a.dds")));
        Assert.True(File.Exists(length));
    }

    [Fact]
    public void An_entry_left_by_an_earlier_codec_version_is_not_served()
    {
        // The key names the codec that wrote the entry, so a change to what the encoder emits leaves the
        // old entries unreachable instead of shipping yesterday's bytes under today's build.
        var caches = new BuildCaches(Path.Combine(_root, "cache-op"), Path.Combine(_root, "cache-tex"));
        var env = MakeEnv(out _, out _);
        var p = NewProject("StaleCodec");
        AddEditedPng(p);

        var cold = ModBuilder.Build(p, env, _out, zip: false, caches: caches);
        var good = File.ReadAllBytes(Path.Combine(cold.OutDir, "rtx_skin_a.dds"));
        var (entry, length) = TextureEntry(caches);

        // Rename the entry's CODEC component, keeping every other part of the key, and give it payload
        // bytes of its own so serving it would be visible. It stays a sound entry in every other way: the
        // header reads and the recorded length matches, so only the key can rule it out.
        var parts = Path.GetFileName(entry).Split('.');
        parts[^2] = "an-earlier-codec";
        string stale = Path.Combine(caches.TextureDir, string.Join('.', parts));
        var different = good.ToArray();
        different[^1] ^= 0xFF;
        File.WriteAllBytes(stale, different);
        File.WriteAllText(stale + ".len", different.Length.ToString());
        File.Delete(entry);
        File.Delete(length);

        var after = ModBuilder.Build(p, env, _out, zip: false, caches: caches);

        var shipped = File.ReadAllBytes(Path.Combine(after.OutDir, "rtx_skin_a.dds"));
        Assert.Equal(good, shipped);
        Assert.NotEqual(different, shipped);
        Assert.True(File.Exists(entry), "the re-encode publishes under the current key");
    }

    [Fact]
    public void Entries_an_earlier_codec_version_left_behind_are_reclaimed()
    {
        // A version bump makes every earlier entry unreachable: nothing ever names that key again, so without
        // a sweep they sit in the cache at a full mip chain each for the life of the install.
        var caches = new BuildCaches(Path.Combine(_root, "cache-op"), Path.Combine(_root, "cache-tex"));
        Directory.CreateDirectory(caches.TextureDir);
        var stale = Path.Combine(caches.TextureDir, "0123456789abcdef.srgb.an-earlier-codec.dds");
        File.WriteAllBytes(stale, new byte[] { (byte)'D', (byte)'D', (byte)'S', (byte)' ', 1, 2, 3, 4 });
        File.WriteAllText(stale + ".len", "8");

        var env = MakeEnv(out _, out _);
        var p = NewProject("Reclaim");
        AddEditedPng(p);
        ModBuilder.Build(p, env, _out, zip: false, caches: caches);

        Assert.False(File.Exists(stale), "the stale entry is gone");
        Assert.False(File.Exists(stale + ".len"), "and its recorded length with it");
        // what remains is this codec's own entry, published by the build that swept
        var kept = Assert.Single(Directory.GetFiles(caches.TextureDir, "*.dds"));
        Assert.Contains("bc7-mips", Path.GetFileName(kept));
    }

    [Fact]
    public void A_texture_cache_hit_says_so_on_the_same_log_the_encode_uses()
    {
        // A warm build skips the tens of seconds the encode line exists to explain. Silence there reads as
        // the same hang the line was added for.
        var caches = new BuildCaches(Path.Combine(_root, "cache-op"), Path.Combine(_root, "cache-tex"));
        var env = MakeEnv(out _, out _);
        var p = NewProject("WarmLog");
        AddEditedPng(p);

        var cold = new List<string>();
        ModBuilder.Build(p, env, _out, cold.Add, zip: false, caches: caches);
        var warm = new List<string>();
        ModBuilder.Build(p, env, _out, warm.Add, zip: false, caches: caches);

        Assert.Contains(cold, l => l.Contains("encoding skin.png"));
        Assert.DoesNotContain(cold, l => l.Contains("texture cache: reusing"));
        Assert.Contains(warm, l => l.Contains("texture cache: reusing skin.png"));
        Assert.DoesNotContain(warm, l => l.Contains("encoding skin.png"));
    }

    [Fact]
    public void A_warm_build_ships_the_same_folder_byte_for_byte_as_a_cold_one()
    {
        // The caches only change how long a build takes. Anything they change about WHAT it ships is a
        // defect, and the mod folder is the whole answer — not just the file the cache holds.
        var caches = new BuildCaches(Path.Combine(_root, "cache-op"), Path.Combine(_root, "cache-tex"));
        var env = MakeEnv(out _, out _);
        var p = NewProject("ColdWarm");
        AddEditedPng(p);

        var cold = ModBuilder.Build(p, env, _out, zip: false, caches: caches);
        var snapshot = Path.Combine(_root, "cold-snapshot");
        Directory.CreateDirectory(snapshot);
        foreach (var f in Directory.GetFiles(cold.OutDir))
            File.Copy(f, Path.Combine(snapshot, Path.GetFileName(f)));

        var warm = ModBuilder.Build(p, env, _out, zip: false, caches: caches);

        var names = Directory.GetFiles(snapshot).Select(Path.GetFileName).Order().ToArray();
        Assert.NotEmpty(names);
        Assert.Equal(names, Directory.GetFiles(warm.OutDir).Select(Path.GetFileName).Order().ToArray());
        foreach (var n in names)
            Assert.Equal(File.ReadAllBytes(Path.Combine(snapshot, n!)),
                         File.ReadAllBytes(Path.Combine(warm.OutDir, n!)));
    }

    [Fact]
    public void An_encode_takes_the_same_bytes_under_a_cpu_cap()
    {
        // The cap changes how many workers the encoder runs, never what it produces. A flat image would
        // prove nothing: every block is the same block, and a mip chain of one colour is the one input BC7
        // cannot get wrong. This image gives each block its own content, over enough levels for the chain to
        // be worth splitting across workers.
        var env = MakeEnv(out _, out _);
        var p = NewProject("Capped");
        AddDetailedPng(p);

        var uncapped = ModBuilder.Build(p, env, _out, zip: false);
        var bytes = File.ReadAllBytes(Path.Combine(uncapped.OutDir, "rtx_skin_a.dds"));
        var capped = ModBuilder.Build(p, env, _out, zip: false, encoderCpuLimit: 1);

        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(capped.OutDir, "rtx_skin_a.dds")));
        // and it really is the detailed image: a solid one compresses to a fraction of this
        Assert.True(bytes.Length > 4096, $"the authored image encoded to {bytes.Length}B — too flat to test");
    }

    [Fact]
    public void An_encode_streams_a_progress_line_to_the_build_log()
    {
        // a full-size map costs tens of seconds; without this the UI shows nothing until the emit
        var env = MakeEnv(out _, out _);
        var p = NewProject("Progress");
        AddEditedPng(p);
        var lines = new List<string>();

        ModBuilder.Build(p, env, _out, lines.Add, zip: false);

        // the line names the file and what is being made of it, and nothing about how
        int encoding = lines.FindIndex(l => l.Contains("encoding skin.png") && l.Contains("8×8 BC7"));
        Assert.True(encoding >= 0, "no encode progress line in: " + string.Join(" | ", lines));
        // it streams DURING the encode, not as part of the final-assembly span
        Assert.True(encoding < lines.FindIndex(l => l.Contains("final assembly")));
    }

    /// <summary>An encode that did not run on the GPU costs minutes a build log otherwise never explains.
    /// Whichever rung this machine resolves to is the branch that runs here.</summary>
    [Fact]
    public void A_build_that_encodes_names_its_encoder_unless_it_is_the_hardware_device()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject("Rung");
        AddEditedPng(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        if (ModBuilder.EncoderRungLine(Bc7Encoder.Resolved) is { } expected)
            Assert.Equal(1, r.Diagnostics.Count(d => d == expected));
        else
            Assert.DoesNotContain(r.Diagnostics, d => d.StartsWith("texture encode:"));
        // and never a user-facing surface: there is nothing for the author to do about the rung
        Assert.DoesNotContain(r.Warnings, w => w.Contains("texture encode"));
        Assert.DoesNotContain(r.Infos, i => i.Contains("texture encode"));
    }

    [Fact]
    public void A_build_that_encodes_nothing_says_nothing_about_the_encoder()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject("Quiet");
        AddEditedTexture(p);   // a .dds source passes through; the encoder is never reached

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.DoesNotContain(r.Diagnostics, d => d.StartsWith("texture encode:"));
    }

    /// <summary>Every rung, since the log line is the only place a software encode is ever disclosed and the
    /// machine running the suite only ever exercises its own.</summary>
    [Fact]
    public void The_encoder_line_names_which_software_path_is_in_use_and_says_nothing_on_hardware()
    {
        Assert.Null(ModBuilder.EncoderRungLine(Bc7Encoder.Rung.Hardware));
        Assert.Equal("texture encode: no GPU device, encoding on the WARP software renderer",
            ModBuilder.EncoderRungLine(Bc7Encoder.Rung.Warp));
        Assert.Equal("texture encode: no graphics device, encoding on the managed encoder",
            ModBuilder.EncoderRungLine(Bc7Encoder.Rung.None));
    }

    private static IReadOnlyDictionary<int, SubmeshMaps> OneRow(MapSlot albedo = default,
        MapSlot normal = default, MapSlot rmo = default) =>
        new Dictionary<int, SubmeshMaps> { [0] = new SubmeshMaps(albedo, normal, rmo) };

    [Fact]
    public void An_authored_donor_map_whose_kind_has_no_anchor_tag_warns_per_kind()
    {
        // A donor map binds through the anchor's stock resource of the same kind; with no tag for it the
        // geometry still swaps, wearing the anchor's own map.
        var albedoOnly = new[] { new StockMapTag("aabbccdd", StockMapKind.Albedo) };
        var w1 = new List<string>();
        ModBuilder.WarnUnbindableDonorMaps(OneRow(MapSlot.From("a.dds"), MapSlot.From("n.dds")),
            albedoOnly, "vesna_body", w1);
        Assert.Contains("no anchor normal could be slot-tagged", Assert.Single(w1));

        var normalOnly = new[] { new StockMapTag("aabbccdd", StockMapKind.Normal) };
        var w2 = new List<string>();
        ModBuilder.WarnUnbindableDonorMaps(OneRow(MapSlot.From("a.dds"), MapSlot.From("n.dds")),
            normalOnly, "vesna_body", w2);
        Assert.Contains("no anchor base color could be slot-tagged", Assert.Single(w2));

        // RMO is its own kind, and warns on its own
        var w3 = new List<string>();
        ModBuilder.WarnUnbindableDonorMaps(OneRow(rmo: MapSlot.From("r.dds")), albedoOnly, "vesna_body", w3);
        Assert.Contains("no anchor RMO could be slot-tagged", Assert.Single(w3));

        // nothing bound of a kind is nothing to warn about
        var none = new List<string>();
        ModBuilder.WarnUnbindableDonorMaps(OneRow(), Array.Empty<StockMapTag>(), "vesna_body", none);
        Assert.Empty(none);
    }

    /// <summary>The warning says which of the two it is. A map the modder authored is work that will not
    /// show; a flat map the build put on an untouched slot only fails to blank it, and calling that "the
    /// donor RMO" tells the modder they lost something they never made.</summary>
    [Fact]
    public void The_unbindable_warning_separates_an_authored_map_from_a_defaulted_neutral()
    {
        var albedoOnly = new[] { new StockMapTag("aabbccdd", StockMapKind.Albedo) };

        var authored = new List<string>();
        ModBuilder.WarnUnbindableDonorMaps(OneRow(MapSlot.From("a.dds"), rmo: MapSlot.From("r.dds")),
            albedoOnly, "vesna_body", authored);
        Assert.Contains("the donor RMO will not bind", Assert.Single(authored));

        var defaulted = new List<string>();
        ModBuilder.WarnUnbindableDonorMaps(OneRow(MapSlot.From("a.dds"), rmo: MapSlot.Neutral),
            albedoOnly, "vesna_body", defaulted);
        var line = Assert.Single(defaulted);
        Assert.Contains("the flat RMO will not bind", line);
        Assert.DoesNotContain("donor RMO", line);
    }

    [Fact]
    public void One_anchor_map_of_a_kind_failing_to_slot_tag_still_warns_though_the_kind_keeps_a_tag()
    {
        // Two materials binding the same kind: one tags, one won't. The kind-level check below is satisfied
        // by the survivor, so without a per-map warning the author ships a mod whose donor albedo silently
        // fails to bind in every draw of the material that lost its tag.
        var materials = new[]
        {
            new SubjectMaterial("m_upper", 1, "cab-a", new[] { new SubjectMap("_BaseMap", "upper_d", "bundleT") }),
            new SubjectMaterial("m_lower", 2, "cab-b", new[] { new SubjectMap("_BaseMap", "lower_d", "bundleT") }),
        };
        var warnings = new List<string>();
        var diagnostics = new List<string>();

        var tags = ModBuilder.TagStockMaps(materials, "vesna_body",
            m => m.TextureName == "upper_d" ? "aabbccdd"
                : throw new InvalidDataException("isn't in bundle 'bundleT'"),
            warnings, diagnostics);

        // the survivor is tagged, and the failure is the author's to see, with what it costs them
        Assert.Equal(new[] { new StockMapTag("aabbccdd", StockMapKind.Albedo) }, tags);
        Assert.Contains(warnings, w => w.Contains("anchor map 'lower_d' can't be slot-tagged")
            && w.Contains("donor maps may not bind where it draws"));
        // the reason it wouldn't tag is the build's own record
        Assert.Contains(diagnostics, d => d.Contains("anchor map 'lower_d' (vesna_body) can't be slot-tagged")
            && d.Contains("isn't in bundle 'bundleT'"));
        Assert.DoesNotContain(warnings, w => w.Contains("isn't in bundle"));

        // and the kind-level warning stays quiet, because the kind DID keep a tag — the whole point
        ModBuilder.WarnUnbindableDonorMaps(OneRow(MapSlot.From("a.dds")), tags, "vesna_body", warnings);
        Assert.DoesNotContain(warnings, w => w.Contains("no anchor base color could be slot-tagged"));
    }

    [Fact]
    public void A_blocked_asset_fails_the_build_through_every_degrade_catch()
    {
        // The degrade-and-continue catches exist for unreadable maps. A BLOCKED asset must never ride
        // them down to a warning: the refusal outranks the build.
        var materials = new[]
        {
            new SubjectMaterial("m_upper", 1, "cab-a", new[] { new SubjectMap("_BaseMap", "upper_d", "bundleT") }),
        };
        var warnings = new List<string>();
        var diagnostics = new List<string>();

        Assert.Throws<BlockedAssetException>(() => ModBuilder.TagStockMaps(materials, "vesna_body",
            _ => throw new BlockedAssetException("'upper_d' is not a supported asset"), warnings, diagnostics));
        Assert.Throws<BlockedAssetException>(() => ModBuilder.AnchorSrgb(materials,
            MaterialResolver.IsBaseColor, "base color", byConvention: true, "vesna_body",
            _ => throw new BlockedAssetException("'upper_d' is not a supported asset"), warnings, diagnostics));
        Assert.Empty(warnings);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Hidden_mesh_builds_a_hide_covering_every_lod_tier_with_sidecar_and_zip()
    {
        var env = MakeEnv(out var h0, out var h1);
        var p = NewProject();
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);

        var r = ModBuilder.Build(p, env, _out);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"hash = {h0}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.Contains($"hash = {h1}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.DoesNotContain("[Constants]", ini);   // no retexture → no pass flags needed

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        var root = doc.RootElement;
        Assert.Equal("Test Mod", root.GetProperty("name").GetString());
        Assert.Equal("Vesna", root.GetProperty("character").GetString());
        Assert.Equal("VesnaSSR01", root.GetProperty("source_outfit").GetString());
        Assert.Equal("12345", root.GetProperty("game_catalog").GetString());
        var hashes = root.GetProperty("override_hashes").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { h0, h1 }.Order().ToArray(), hashes);

        Assert.True(File.Exists(r.ZipPath));
        using var zip = ZipFile.OpenRead(r.ZipPath!);
        Assert.Contains(zip.Entries, e => e.Name == "mod.ini");
        // every entry sits under the mod folder's own name, so an extract lands one folder
        Assert.All(zip.Entries, e => Assert.StartsWith("test-mod_v1_0/", e.FullName));
        // the published naming convention: name, author when there is one, then the version
        Assert.Equal(Path.Combine(_out, "test-mod_v1_0"), r.OutDir);
    }

    [Fact]
    public void An_explicit_hide_emits_no_lodm0_section()
    {
        // lodm0 tiers are not shipped: the PC client does not render them, so a hide covering one would
        // override a draw that never happens.
        var env = MakeMidTierEnv(out var h0, out var hm, out var h1);
        var p = NewProject("Mid");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"hash = {h0}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.Contains($"hash = {h1}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.DoesNotContain(hm, ini);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        var hashes = doc.RootElement.GetProperty("override_hashes")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { h0, h1 }.Order().ToArray(), hashes);
    }

    [Fact]
    public void An_explicit_hide_emits_no_lodm0_section_when_the_tier_marker_is_infixed()
    {
        // The variant garments' LOD marker sits INSIDE the name (…_P1_body1_lodm0_Dorm), so a tail test
        // reads the tier as _Dorm and ships the mid tier anyway. Same contract, infixed spelling.
        var env = MakeMidTierEnv(out var h0, out var hm, out var h1, token: "P1_body1", variant: "_Dorm");
        var p = NewProject("MidInfixed");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_P1_body1_lod0_Dorm", true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"hash = {h0}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.Contains($"hash = {h1}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.DoesNotContain(hm, ini);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        var hashes = doc.RootElement.GetProperty("override_hashes")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { h0, h1 }.Order().ToArray(), hashes);
    }

    [Fact]
    public void Edited_texture_builds_a_retexture_keyed_on_the_stock_textures_hash()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject("Retex");
        AddEditedTexture(p);

        var r = ModBuilder.Build(p, env, _out);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        // one bind-time swap of the stock resource: no pass flags, no slot binds, no draw scoping
        Assert.DoesNotContain("[Constants]", ini);
        Assert.DoesNotContain("$zz_pass", ini);
        Assert.DoesNotContain("match_first_index", ini);
        Assert.Contains($"[TextureOverride_Retex_vesna_body_a_{_stockTexHash}]\n"
            + $"hash = {_stockTexHash}\nmatch_priority = 0\nthis = Resource_Rtx0\n", ini);
        // the DDS is named for its SOURCE (one encode serves every stock texture it replaces)
        Assert.True(File.Exists(Path.Combine(r.OutDir, "rtx_skin_a.dds")));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        Assert.Equal(new[] { _stockTexHash },
            doc.RootElement.GetProperty("override_hashes").EnumerateArray().Select(e => e.GetString()).ToArray());
        // every LOD samples the same texture, so nothing tier-shaped is left to warn about
        Assert.DoesNotContain(r.Warnings, w => w.Contains("lod0"));
    }

    [Fact]
    public void Subjects_sharing_a_stock_texture_retexture_it_once()
    {
        // A second subject resolving to the same model. Derivation passes both through; the build collapses
        // them by TEXTURE, since two sections on one hash override the same resource twice.
        var env = MakeEnv(out _, out _);
        var envShared = new BuildEnv(
            (c, s) => c == "Vesna" ? env.ResolveSubject("Vesna", "VesnaSSR01") : null,
            env.ResolveAddress, env.Deobfuscate, env.CatalogVersion, env.AppVersion);
        var p = NewProject("Shared");
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaAlt" });
        AddEditedTexture(p);

        var r = ModBuilder.Build(p, envShared, _out);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Equal(1, CountOf(ini, $"hash = {_stockTexHash}"));
        Assert.Equal(1, CountOf(ini, "[TextureOverride_Retex_"));
        Assert.Single(Directory.GetFiles(r.OutDir, "rtx_*.dds"));
    }

    private static int CountOf(string text, string token)
    {
        int n = 0;
        for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(token, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void The_zip_stores_encoded_textures_and_deflates_the_rest()
    {
        // BC7 is already block-compressed: deflating it costs the whole payload's compression time for
        // nothing. The text and buffers do compress, so they still are.
        var env = MakeEnv(out _, out _);
        var p = NewProject("Zipped");
        AddEditedPng(p);

        var r = ModBuilder.Build(p, env, _out);

        using var zip = ZipFile.OpenRead(r.ZipPath!);
        var dds = Assert.Single(zip.Entries, e => e.Name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(dds.Length, dds.CompressedLength);
        var ini = Assert.Single(zip.Entries, e => e.Name == "mod.ini");
        Assert.True(ini.CompressedLength < ini.Length, "mod.ini was stored rather than deflated");
    }

    [Fact]
    public void Zip_can_be_disabled()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject("Combo");
        AddEditedTexture(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);
        Assert.Null(r.ZipPath);
        Assert.False(File.Exists(Path.Combine(_out, "combo_v1_0.zip")));
    }

    // ---- the donor (Replace) route's sRGB inheritance --------------------------------------------
    // A donor map binds at the anchor's draw, so the ANCHOR's stock map of that kind decides the tag. Driven
    // directly: a Replace build needs bone tables the synthetic bundles don't carry.

    private static readonly SubjectMaterial[] AnchorMaterials =
    {
        new("m_anchor", 1, "cab-anchor", new[]
        {
            new SubjectMap("_BaseMap", "anchor_d", "bundleT"),
            new SubjectMap("_BumpMap", "anchor_n", "bundleT"),
        }),
    };

    private static uint Dxgi(string ddsPath) => BitConverter.ToUInt32(File.ReadAllBytes(ddsPath), 128);

    [Fact]
    public void A_donor_map_is_tagged_per_the_anchors_map_of_the_same_kind()
    {
        // both anchor maps sit OPPOSITE their kind's convention, so matching convention would prove nothing
        var family = new Dictionary<string, bool> { ["anchor_d"] = false, ["anchor_n"] = true };
        var warnings = new List<string>();
        var diagnostics = new List<string>();

        bool albedo = ModBuilder.AnchorSrgb(AnchorMaterials, MaterialResolver.IsBaseColor, "base color",
            byConvention: true, "vesna_body", m => family[m.TextureName], warnings, diagnostics);
        bool normal = ModBuilder.AnchorSrgb(AnchorMaterials, MaterialResolver.IsNormal, "normal",
            byConvention: false, "vesna_body", m => family[m.TextureName], warnings, diagnostics);

        Assert.False(albedo);
        Assert.True(normal);
        Assert.Empty(warnings);
        Assert.Empty(diagnostics);

        // and that family is what the shipped container is tagged with
        string src = Path.Combine(_proj, "donor.png");
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255))) img.SaveAsPng(src);
        string a = Path.Combine(_proj, "donor_a.dds"), n = Path.Combine(_proj, "donor_n.dds");
        AuthoredDds.Encode(src, a, albedo);
        AuthoredDds.Encode(src, n, normal);
        Assert.Equal(98u, Dxgi(a));   // BC7_UNORM
        Assert.Equal(99u, Dxgi(n));   // BC7_UNORM_SRGB
    }

    [Fact]
    public void Anchor_materials_that_disagree_about_srgb_warn_and_the_first_wins()
    {
        // The slot tagger binds the donor map into EVERY matching map of every anchor material, but the file
        // carries ONE tag — the second material's draws render it wrong, and silently would be worse.
        var materials = new[]
        {
            new SubjectMaterial("m_upper", 1, "cab-a", new[] { new SubjectMap("_BaseMap", "upper_d", "bundleT") }),
            new SubjectMaterial("m_lower", 2, "cab-b", new[] { new SubjectMap("_BaseMap", "lower_d", "bundleT") }),
        };
        var family = new Dictionary<string, bool> { ["upper_d"] = true, ["lower_d"] = false };
        var warnings = new List<string>();
        var diagnostics = new List<string>();

        bool albedo = ModBuilder.AnchorSrgb(materials, MaterialResolver.IsBaseColor, "base color",
            byConvention: false, "vesna_body", m => family[m.TextureName], warnings, diagnostics);

        Assert.True(albedo);   // the FIRST readable map's family, not the convention
        Assert.Contains(warnings, w => w.Contains("disagree") && w.Contains("'upper_d' is sRGB")
            && w.Contains("'lower_d' is linear") && w.Contains("tagged sRGB"));
    }

    [Fact]
    public void An_unreadable_anchor_map_warns_then_tags_by_the_kinds_convention()
    {
        var warnings = new List<string>();
        var diagnostics = new List<string>();

        bool albedo = ModBuilder.AnchorSrgb(AnchorMaterials, MaterialResolver.IsBaseColor, "base color",
            byConvention: true, "vesna_body",
            _ => throw new InvalidDataException("isn't in bundle 'bundleT'"), warnings, diagnostics);

        Assert.True(albedo);
        // the unreadable map is the author's problem; which tag the fallback picked is the build's own record
        Assert.Contains(warnings, w => w.Contains("anchor base color map 'anchor_d' can't be read")
            && w.Contains("isn't in bundle 'bundleT'"));
        Assert.Contains(diagnostics, d => d.Contains("tagged sRGB by convention"));
        Assert.DoesNotContain(warnings, w => w.Contains("by convention"));
    }

    [Fact]
    public void An_anchor_with_no_map_of_the_kind_takes_the_convention_silently()
    {
        // nothing to inherit is the ordinary case, not a problem to report
        var warnings = new List<string>();
        var diagnostics = new List<string>();

        bool rmo = ModBuilder.AnchorSrgb(AnchorMaterials, MaterialResolver.IsRmo, "RMO",
            byConvention: false, "vesna_body", _ => true, warnings, diagnostics);

        Assert.False(rmo);
        Assert.Empty(warnings);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Submeshes_authored_from_one_image_ship_one_map_bound_from_both_draws()
    {
        // The intake writes its OWN copy of an authored map per submesh, so one painted image reaches the
        // build under a name per submesh. Claiming by name ships a full-size map per submesh; claiming by
        // content ships one, and every draw binds it.
        string authored = Path.Combine(_proj, "authored.png");
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255))) img.SaveAsPng(authored);
        string s0 = Path.Combine(_proj, "donor_s0_base.png");
        string s1 = Path.Combine(_proj, "donor_s1_base.png");
        File.Copy(authored, s0);
        File.Copy(authored, s1);

        string work = Path.Combine(_root, "donor-work");
        Directory.CreateDirectory(work);
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int encodes = 0;
        string Enc(string src, int submesh) => ModBuilder.EncodeOnce(claimed, src, srgb: true,
            () => Path.Combine(work, $"donor_swap_s{submesh}_a.dds"),
            () => encodes++,
            log: null, cacheDir: null, encoderCpuLimit: null);

        string first = Enc(s0, 0), second = Enc(s1, 1);
        Assert.Equal(first, second);
        Assert.Equal(1, encodes);   // the shared claim is one encode, not one per submesh
        Assert.Single(Directory.GetFiles(work, "*.dds"));

        string dump = Path.Combine(_root, "dump");
        SyntheticPool.WritePartDump(dump, seed: 3, verts: 32, boneHashes: new uint[] { 101, 102 });
        string donorDir = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donorDir, verts: 6, unionBones: 2);
        string outDir = Path.Combine(_root, "donor-out");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", dump) },
                    DonorDir = donorDir,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                    SubTextures = new Dictionary<int, SubmeshMaps>
                    {
                        [0] = new(MapSlot.From(first)),
                        [1] = new(MapSlot.From(second)),
                    },
                },
            },
        });

        Assert.Single(Directory.GetFiles(outDir, "donor_*.dds"));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.Equal(1, CountOf(ini, "[Resource_Tex0]"));
        Assert.DoesNotContain("Resource_Tex1", ini);
        // the first submesh binds that one resource and the second, wanting the same, rebinds nothing
        string list = ini[ini.IndexOf("[CommandListDraw_swap]", StringComparison.Ordinal)..];
        Assert.Equal(1, CountOf(list, "if $zz_slot_a == 0\nps-t0 = Resource_Tex0\nendif\n"));
        Assert.Equal(2, CountOf(list, "drawindexed = "));
    }

    // ---- the Replace route, end to end ------------------------------------------------------------

    /// <summary>The two bones the synthetic outfit's one part owns, and the donor rides.</summary>
    private static readonly uint[] BodyBones = { 0x00000101, 0x00000102 };

    /// <summary>The twin part's own bones (see <c>twinPart</c>), and the cloth wearer's (never donor-ridden).</summary>
    private static readonly uint[] TwinBones = { 0x00000103, 0x00000104 };
    private static readonly uint[] ClothBones = { 0x00000201, 0x00000202 };

    /// <summary>The pool mate's own bones (see <c>poolMate</c>): donor-ridden, so it pools, but its part
    /// is never the Replace target — a Leave whose vanilla draw keeps running.</summary>
    private static readonly uint[] MateBones = { 0x00000105, 0x00000106 };

    /// <summary>The expression part's own bones (see <c>facePart</c>): its mesh can't feed palette
    /// recovery, so the roster reaches pool derivation short of it and these bones unowned.</summary>
    private static readonly uint[] FaceBones = { 0x00000301, 0x00000302 };

    /// <summary>The unmeasurable part's own bones (see <c>unreadableWeightsPart</c>): its table reads, its
    /// weights don't, so these are the bones a refusal can still rule the part out by.</summary>
    private static readonly uint[] UnreadBones = { 0x00000501, 0x00000502 };

    /// <summary>The bones both wardrobe options own (see <see cref="WardrobeWorld"/>): the two options fill one
    /// slot, so they are rigged alike. A donor riding these pools the option it targets and nothing else.</summary>
    private static readonly uint[] DressBones = { 0x00000401, 0x00000402 };

    /// <summary>Each wardrobe option's companion bones: donor-ridden only where a test asks the companion
    /// into the pool.</summary>
    private static readonly uint[] Belt1Bones = { 0x00000403, 0x00000404 };
    private static readonly uint[] Belt2Bones = { 0x00000405, 0x00000406 };

    /// <summary>The accessory part's bone table (see <c>narrowAccessory</c>): the body's FIRST bone and
    /// nothing else. One influence per vertex means it rides that bone at weight 1 on all of them, which
    /// outweighs the body's share of it — the ownership the union would hand over if the part could
    /// pool for another part's Replace.</summary>
    private static readonly uint[] AccessoryBones = { 0x00000101 };

    /// <summary>Deterministic spread positions: a hash cloud, so no two vertices coincide and the operator
    /// solve has real support to work with.</summary>
    private static float[] Cloud(int verts, int seed)
    {
        var pos = new float[verts * 3];
        for (int i = 0; i < pos.Length; i++)
        {
            uint h = (uint)(seed * 97) + (uint)i * 2654435761u;
            h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
            pos[i] = h % 1000 / 250f - 2f;
        }
        return pos;
    }

    /// <summary>Triangles wrapping the vertex range, so every vertex is drawn and no index runs past it.</summary>
    private static int[] WrappedTris(int verts)
    {
        var tris = new int[verts * 3];
        for (int i = 0; i < tris.Length; i++) tris[i] = i % verts;
        return tris;
    }

    /// <summary>The wardrobe-twin world of <see cref="MakeSkinnedEnv"/>: which companion each of the
    /// two options is married to, and the extra siblings that reshape the signature classes.</summary>
    private sealed record WardrobeWorld
    {
        /// <summary>Dropping either option's companion leaves that variant nothing to sight it
        /// by.</summary>
        public bool Companion1 { get; init; } = true;
        public bool Companion2 { get; init; } = true;
        /// <summary>The second option's companion is byte-identical to the first's — the one accessory
        /// both options are worn with.</summary>
        public bool TwinCompanions { get; init; }
        /// <summary>A byte-identical accessory pair BESIDE the companions of their own.</summary>
        public bool SharedExtra { get; init; }
        /// <summary>A third sibling byte-identical to the first option: the pair is one content class
        /// with a single mate, separable by its own base color, while the lone sibling is not.</summary>
        public bool Mixed { get; init; }
        /// <summary>A companion this build refuses to touch, in the first option.</summary>
        public bool BlockedCompanion { get; init; }
        /// <summary>The SECOND option's companion is present and perfectly readable, and its renderer is
        /// outside the shadow pass: it draws only while it is in frame, so sighting nothing at it proves
        /// nothing. The option is left unsightable exactly as dropping the companion would be, by the
        /// measured flag alone.</summary>
        public bool ShadowOffCompanion2 { get; init; }
        /// <summary>The SECOND option's companion is present and perfectly readable, and the game's own
        /// dorm logic can withhold it: it answers for that logic rather than for the option worn, so
        /// sighting nothing at it proves nothing. The option is left unsightable exactly as dropping the
        /// companion would be, by the measured marker alone.</summary>
        public bool WithheldCompanion2 { get; init; }
        /// <summary>The second option's companion gains a lod1 TIER with an index buffer of its own — a
        /// second witnessable draw under one companion part.</summary>
        public bool Companion2Tier { get; init; }
        /// <summary>…and the marker sits on that TIER while the companion part itself stays clean, which
        /// is the only shape that tells the per-tier gate from the per-part one.</summary>
        public bool WithheldCompanion2Tier { get; init; }
    }

    /// <summary>The one-part world in SKINNED form — the shape a Replace needs: a bone table the donor's
    /// weights resolve against, and the full skin stream palette recovery consumes. One material, so the
    /// anchor has a stock albedo the donor maps can bind through; <paramref name="anchorNormal"/> and
    /// <paramref name="anchorRmo"/> give it a stock NORMAL and RMO too, which an authored donor map of
    /// that kind needs to slot-tag against. <paramref name="meshTail"/> appends an outfit-state token
    /// after the LOD marker (…_lod0_Fight); <paramref name="twinPart"/> adds a second part whose mesh
    /// shares the body's index buffer; <paramref name="clothWearer"/> adds an unpooled part binding the
    /// same stock albedo; <paramref name="poolMate"/> adds a donor-ridden second part with its own index
    /// buffer, binding that same stock albedo; <paramref name="mateTierTwin"/> gives that part a lod1
    /// tier drawing the BODY lod1's index buffer, <paramref name="mateTier"/> one drawing an index buffer
    /// of its own; <paramref name="midTier"/> puts an unrendered
    /// <c>lodm1</c> tier between the body's two shipped ones. <paramref name="bodyBlendShapes"/> and
    /// <paramref name="bodySkinWidth"/> put the Replace TARGET itself past the recoverable-skin rule;
    /// <paramref name="bodyTabledOnly"/> appends to the body lod0's bone table hashes no vertex of it
    /// rides, the corpus shape where a part lists more skeleton than it poses;
    /// <paramref name="facePart"/> adds a second part that fails it, owning bones of its own;
    /// <paramref name="ghostPart"/> adds one whose bundle this install doesn't carry, so not even its
    /// bones are known; <paramref name="unreadableWeightsPart"/> adds one past the skin rule whose vertex
    /// bytes are gone, so its TABLE reads and its weights don't. <paramref name="bodyTierBones"/> replaces the bone table of the body's lod1, the
    /// shape where a tier POSES what its own lod0 does not; <paramref name="bodyTierTabledOnly"/> appends
    /// to that table bones no vertex of the tier rides. <paramref name="clothTier"/> gives the cloth part
    /// a lod1 of its own, so it draws where the body's lod1 draws; <paramref name="clothTail"/> puts the
    /// cloth in another outfit state (…_lod0_Dorm), where it does not.
    /// <paramref name="narrowAccessory"/> adds a ONE-influence part tabling the body's first bone, in the
    /// spelling the game's accessories ship: recoverable, donor-ridden through that shared bone, and
    /// heavier on it than the body itself. <paramref name="bodySharedSkinStream"/> puts a third live
    /// channel on the target's skin stream, an influence count recovery accepts spelled in a shape it
    /// cannot read. <paramref name="twinPartOwnAlbedo"/> gives the twin part a base color of its OWN, the
    /// shape where the textures bound at the two draws tell the twins apart;
    /// <paramref name="twinPartSharedAlbedo"/> gives it the body's, where they cannot.
    /// <paramref name="clothTwinsMate"/> puts the cloth part on the pool mate's index buffer, so an
    /// unpooled mesh shares a pooled one's draw signature; <paramref name="clothOwnAlbedo"/> gives the
    /// cloth a base color of its own instead of the body's. <paramref name="mateOwnAlbedo"/> does the
    /// same for the pool mate, which is what separates its twinned TIER from the body's.
    /// <paramref name="twinPartShadowOff"/> puts the twin part's renderer outside the shadow pass: fully
    /// readable and donor-ridden, and the game stops drawing it the moment it leaves the camera, so it can
    /// refresh nothing for another part's palette recovery.
    /// <paramref name="twinPartVisibility"/> marks the twin part with a PREFAB-RESIDENT visibility
    /// mechanism — fully readable and donor-ridden, and the game's own dorm or lobby logic decides
    /// whether it draws.
    /// <paramref name="wardrobe"/> adds two options of ONE wardrobe slot whose meshes share an index
    /// buffer, stream-1 bytes and base color — nothing at the draw parts them — each married to a
    /// companion mesh of its own; <see cref="WardrobeWorld"/>'s knobs shape the companions and extra
    /// siblings. Wire <see cref="WithWardrobeScheme"/> to classify them.</summary>
    private BuildEnv MakeSkinnedEnv(bool anchorNormal = false, bool anchorRmo = false,
        string meshTail = "", bool twinPart = false, bool clothWearer = false, bool poolMate = false,
        bool midTier = false, bool mateTierTwin = false, bool mateTier = false, int bodyBlendShapes = 0,
        int bodySkinWidth = 4, uint[]? bodyTabledOnly = null, bool facePart = false, bool ghostPart = false,
        bool unreadableWeightsPart = false, uint[]? bodyTierBones = null,
        uint[]? bodyTierTabledOnly = null, bool clothTier = false, string clothTail = "",
        bool narrowAccessory = false, bool bodySharedSkinStream = false, int twinPartUvSeed = 0,
        int twinPartPosSeed = 5, bool twinPartOwnAlbedo = false, bool twinPartSharedAlbedo = false,
        bool clothTwinsMate = false, bool clothOwnAlbedo = false, bool mateOwnAlbedo = false,
        bool twinPartShadowOff = false,
        Remold.Core.Model.VisibilityOverride twinPartVisibility = Remold.Core.Model.VisibilityOverride.None,
        WardrobeWorld? wardrobe = null)
    {
        string b0 = Path.Combine(_root, "s0.bundle");
        string b1 = Path.Combine(_root, "s1.bundle");
        string bt = Path.Combine(_root, "st.bundle");
        string bn = Path.Combine(_root, "sn.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(b0, $"c_vesna01_body_lod0{meshTail}", Cloud(32, 5), WrappedTris(32), BodyBones,
            blendShapes: bodyBlendShapes, skinWidth: bodySkinWidth, tabledOnlyBones: bodyTabledOnly,
            extraSkinChannel: bodySharedSkinStream);
        SyntheticBundle.BuildOneSkinnedMesh(b1, $"c_vesna01_body_lod1{meshTail}", Cloud(24, 9), WrappedTris(24),
            bodyTierBones ?? BodyBones, tabledOnlyBones: bodyTierTabledOnly);
        SyntheticBundle.BuildOneTexture(bt, "tex_body_d", 8, 8, 200, 100, 50, 255, colorSpace: 1);
        SyntheticBundle.BuildOneTexture(bn, "tex_body_n", 8, 8, 128, 128, 255, 255, colorSpace: 0);
        string br = Path.Combine(_root, "sr.bundle");
        SyntheticBundle.BuildOneTexture(br, "tex_body_r", 8, 8, 128, 0, 255, 255, colorSpace: 0);
        var bytes = new Dictionary<string, byte[]>
        {
            ["bundle0"] = File.ReadAllBytes(b0),
            ["bundle1"] = File.ReadAllBytes(b1),
            ["bundleT"] = File.ReadAllBytes(bt),
            ["bundleN"] = File.ReadAllBytes(bn),
            ["bundleR"] = File.ReadAllBytes(br),
        };
        _stockTexHash = SyntheticBundle.StockTexHash(bytes["bundleT"], "tex_body_d");

        if (twinPartOwnAlbedo || clothOwnAlbedo || mateOwnAlbedo || wardrobe is { Mixed: true })
        {
            // a second stock base color, so a part can bind one no other part of the roster does
            string balt = Path.Combine(_root, "salt.bundle");
            SyntheticBundle.BuildOneTexture(balt, "tex_alt_d", 8, 8, 20, 210, 90, 255, colorSpace: 1);
            bytes["bundleAlt"] = File.ReadAllBytes(balt);
            _altTexHash = SyntheticBundle.StockTexHash(bytes["bundleAlt"], "tex_alt_d");
        }

        var maps = new List<SubjectMap> { new("_BaseMap", "tex_body_d", "bundleT") };
        if (anchorNormal) maps.Add(new SubjectMap("_BumpMap", "tex_body_n", "bundleN"));
        if (anchorRmo) maps.Add(new SubjectMap("_RMOTex", "tex_body_r", "bundleR"));
        var materials = new[] { new SubjectMaterial("m_body", 1, "cab-body", maps) };
        var bodyTiers = new List<RecipeTierSlot>();
        var addresses = new Dictionary<string, string> { ["addr_body"] = "bundle0", ["addr_body_l1"] = "bundle1" };
        if (midTier)
        {
            // its own vertex count gives it its own index buffer, so a walk that failed to skip it would
            // ship a section no other tier accounts for
            string bmid = Path.Combine(_root, "smid.bundle");
            SyntheticBundle.BuildOneSkinnedMesh(bmid, $"c_vesna01_body_lodm1{meshTail}",
                Cloud(28, 11), WrappedTris(28), BodyBones);
            bytes["bundleMid"] = File.ReadAllBytes(bmid);
            bodyTiers.Add(new RecipeTierSlot($"c_vesna01_body_lodm1{meshTail}", "addr_body_m1"));
            addresses["addr_body_m1"] = "bundleMid";
        }
        bodyTiers.Add(new RecipeTierSlot($"c_vesna01_body_lod1{meshTail}", "addr_body_l1"));
        var parts = new List<SubjectPart>
        {
            new("body", $"c_vesna01_body_lod0{meshTail}", "addr_body", materials,
                SiblingTiers: bodyTiers.ToArray()),
        };
        if (twinPart)
        {
            // its OWN bones, so the donor riding both parts pools both — but the identical triangle
            // list gives it the body's exact index buffer. A nonzero uvSeed gives it stream-1 bytes of
            // its own, the remodel-with-a-re-unwrap shape a vb1 key can separate; a twinPartPosSeed of
            // its own gives it different GEOMETRY on one index buffer, which nothing but the bound
            // textures separates.
            string b2 = Path.Combine(_root, "s2.bundle");
            SyntheticBundle.BuildOneSkinnedMesh(b2, "c_vesna01_body2_lod0", Cloud(32, twinPartPosSeed),
                WrappedTris(32), TwinBones, uvSeed: twinPartUvSeed);
            bytes["bundle2"] = File.ReadAllBytes(b2);
            var twinMaps = new List<SubjectMap>();
            if (twinPartOwnAlbedo) twinMaps.Add(new SubjectMap("_BaseMap", "tex_alt_d", "bundleAlt"));
            else if (twinPartSharedAlbedo) twinMaps.Add(new SubjectMap("_BaseMap", "tex_body_d", "bundleT"));
            parts.Add(new SubjectPart("body2", "c_vesna01_body2_lod0", "addr_body2",
                new[] { new SubjectMaterial("m_body2", 1, "cab-body2", twinMaps) },
                CastsShadows: !twinPartShadowOff, Visibility: twinPartVisibility));
            addresses["addr_body2"] = "bundle2";
        }
        if (clothWearer)
        {
            // bones the donor never rides keep it out of the pool; the material wears the SAME stock
            // albedo as the body's anchor material
            string bc = Path.Combine(_root, "sc.bundle");
            // the mate's vertex count gives the two the SAME index buffer with content of their own
            int clothVerts = clothTwinsMate ? 20 : 16;
            SyntheticBundle.BuildOneSkinnedMesh(bc, $"c_vesna01_cloth_lod0{clothTail}",
                Cloud(clothVerts, 13), WrappedTris(clothVerts), ClothBones);
            bytes["bundleC"] = File.ReadAllBytes(bc);
            var clothTiers = new List<RecipeTierSlot>();
            if (clothTier)
            {
                // its own vertex count, so it is a tier capture of its own beside the body's lod1
                string bcl = Path.Combine(_root, "scl.bundle");
                SyntheticBundle.BuildOneSkinnedMesh(bcl, $"c_vesna01_cloth_lod1{clothTail}",
                    Cloud(14, 29), WrappedTris(14), ClothBones);
                bytes["bundleCL"] = File.ReadAllBytes(bcl);
                clothTiers.Add(new RecipeTierSlot($"c_vesna01_cloth_lod1{clothTail}", "addr_cloth_l1"));
                addresses["addr_cloth_l1"] = "bundleCL";
            }
            parts.Add(new SubjectPart($"cloth{clothTail}", $"c_vesna01_cloth_lod0{clothTail}", "addr_cloth",
                new[] { new SubjectMaterial("m_cloth", 1, "cab-cloth",
                    new List<SubjectMap>
                    {
                        clothOwnAlbedo
                            ? new SubjectMap("_BaseMap", "tex_alt_d", "bundleAlt")
                            : new SubjectMap("_BaseMap", "tex_body_d", "bundleT"),
                    }) },
                SiblingTiers: clothTiers.Count > 0 ? clothTiers.ToArray() : null));
            addresses["addr_cloth"] = "bundleC";
        }
        if (facePart)
        {
            // blend shapes keep it out of pool derivation whatever the donor does; its own bones are what
            // a donor weighted across it lands on
            string bf = Path.Combine(_root, "sf.bundle");
            SyntheticBundle.BuildOneSkinnedMesh(bf, "c_vesna01_face_lod0", Cloud(18, 23), WrappedTris(18),
                FaceBones, blendShapes: 21);
            bytes["bundleF"] = File.ReadAllBytes(bf);
            parts.Add(new SubjectPart("face", "c_vesna01_face_lod0", "addr_face",
                new[] { new SubjectMaterial("m_face", 1, "cab-face", new List<SubjectMap>()) }));
            addresses["addr_face"] = "bundleF";
        }
        if (narrowAccessory)
        {
            // 40 vertices, all of them on the body's first bone at weight 1: more summed support there than
            // the body's own 16, which is what decides union ownership
            string ba = Path.Combine(_root, "sa.bundle");
            SyntheticBundle.BuildOneSkinnedMesh(ba, "c_vesna01_acc_lod0", Cloud(40, 31), WrappedTris(40),
                AccessoryBones, skinWidth: 1, implicitWeights: true);
            bytes["bundleA"] = File.ReadAllBytes(ba);
            parts.Add(new SubjectPart("acc", "c_vesna01_acc_lod0", "addr_acc",
                new[] { new SubjectMaterial("m_acc", 1, "cab-acc", new List<SubjectMap>()) }));
            addresses["addr_acc"] = "bundleA";
        }
        if (unreadableWeightsPart)
        {
            // Past every layout check and short of a measurement: the channel table, the bone hashes and
            // the skin rule all read as a sound part, and the vertex bytes its weights live in are gone.
            string bu = Path.Combine(_root, "su.bundle");
            SyntheticBundle.BuildOneSkinnedMesh(bu, "c_vesna01_unread_lod0", Cloud(12, 37), WrappedTris(12),
                UnreadBones, unresolvableStream: true);
            bytes["bundleU"] = File.ReadAllBytes(bu);
            parts.Add(new SubjectPart("unread", "c_vesna01_unread_lod0", "addr_unread",
                new[] { new SubjectMaterial("m_unread", 1, "cab-unread", new List<SubjectMap>()) }));
            addresses["addr_unread"] = "bundleU";
        }
        if (ghostPart)
        {
            // its address resolves, but the bundle behind it isn't in this install, so the probe learns
            // nothing about the part — not even which bones it owns
            parts.Add(new SubjectPart("ghost", "c_vesna01_ghost_lod0", "addr_ghost",
                new[] { new SubjectMaterial("m_ghost", 1, "cab-ghost", new List<SubjectMap>()) }));
            addresses["addr_ghost"] = "bundleGhost";
        }
        if (poolMate)
        {
            // donor-ridden bones put it IN the pool; its own triangle list keeps its index buffer distinct
            // from the body's, so the two are separate captures. Its material wears the body's stock albedo.
            string bm = Path.Combine(_root, "sm.bundle");
            SyntheticBundle.BuildOneSkinnedMesh(bm, "c_vesna01_mate_lod0", Cloud(20, 17), WrappedTris(20), MateBones);
            bytes["bundleM"] = File.ReadAllBytes(bm);
            var mateTiers = new List<RecipeTierSlot>();
            if (mateTierTwin)
            {
                // the body lod1's vertex count, so the two tiers hand the swap one index buffer
                string bmt = Path.Combine(_root, "smt.bundle");
                SyntheticBundle.BuildOneSkinnedMesh(bmt, "c_vesna01_mate_lod1", Cloud(24, 19), WrappedTris(24), MateBones);
                bytes["bundleMT"] = File.ReadAllBytes(bmt);
                mateTiers.Add(new RecipeTierSlot("c_vesna01_mate_lod1", "addr_mate_l1"));
                addresses["addr_mate_l1"] = "bundleMT";
            }
            if (mateTier)
            {
                // its own vertex count, so it is a tier capture of its own beside the body's lod1
                string bmo = Path.Combine(_root, "smo.bundle");
                SyntheticBundle.BuildOneSkinnedMesh(bmo, "c_vesna01_mate_lod1", Cloud(22, 19), WrappedTris(22), MateBones);
                bytes["bundleMO"] = File.ReadAllBytes(bmo);
                mateTiers.Add(new RecipeTierSlot("c_vesna01_mate_lod1", "addr_mate_l1"));
                addresses["addr_mate_l1"] = "bundleMO";
            }
            parts.Add(new SubjectPart("mate", "c_vesna01_mate_lod0", "addr_mate",
                new[] { new SubjectMaterial("m_mate", 1, "cab-mate", new List<SubjectMap>
                    {
                        mateOwnAlbedo
                            ? new SubjectMap("_BaseMap", "tex_alt_d", "bundleAlt")
                            : new SubjectMap("_BaseMap", "tex_body_d", "bundleT"),
                    }) },
                SiblingTiers: mateTiers.Count > 0 ? mateTiers.ToArray() : null));
            addresses["addr_mate"] = "bundleM";
        }
        if (wardrobe is { } w)
        {
            // Two options of ONE wardrobe slot. Same triangle list and the same UVs, so neither the index
            // buffer nor stream 1 keys them apart; positions of their own make them two content classes
            // all the same, and the shared stock albedo leaves nothing bound at the draw to ask. Each
            // option is worn with a companion whose vertex count gives it an index buffer of its own.
            string bd1 = Path.Combine(_root, "sd1.bundle"), bd2 = Path.Combine(_root, "sd2.bundle");
            SyntheticBundle.BuildOneSkinnedMesh(bd1, "c_vesna01_dress1_lod0", Cloud(26, 5),
                WrappedTris(26), DressBones);
            SyntheticBundle.BuildOneSkinnedMesh(bd2, "c_vesna01_dress2_lod0", Cloud(26, 21),
                WrappedTris(26), DressBones);
            bytes["bundleD1"] = File.ReadAllBytes(bd1);
            bytes["bundleD2"] = File.ReadAllBytes(bd2);
            foreach (var (token, bundleId) in new[] { ("dress1", "bundleD1"), ("dress2", "bundleD2") })
            {
                // the mixed world gives the FIRST option a base color no other sibling binds, so its own
                // draw answers for it while the other option's still has nothing to read
                parts.Add(new SubjectPart(token, $"c_vesna01_{token}_lod0", $"addr_{token}",
                    new[] { new SubjectMaterial($"m_{token}", 1, $"cab-{token}",
                        new List<SubjectMap>
                        {
                            w.Mixed && token == "dress1"
                                ? new SubjectMap("_BaseMap", "tex_alt_d", "bundleAlt")
                                : new SubjectMap("_BaseMap", "tex_body_d", "bundleT"),
                        }) }));
                addresses[$"addr_{token}"] = bundleId;
            }
            if (w.Mixed)
            {
                // A third sibling on the same index buffer, byte-identical to the first option: the two
                // are ONE content class, so that class is told about a single mate while the lone
                // sibling is told about both. It wears the shared base color, which is what leaves the
                // lone sibling inseparable by texture and the pair separable.
                string bd1b = Path.Combine(_root, "sd1b.bundle");
                SyntheticBundle.BuildOneSkinnedMesh(bd1b, "c_vesna01_dress1b_lod0", Cloud(26, 5),
                    WrappedTris(26), DressBones);
                bytes["bundleD1b"] = File.ReadAllBytes(bd1b);
                parts.Add(new SubjectPart("dress1b", "c_vesna01_dress1b_lod0", "addr_dress1b",
                    new[] { new SubjectMaterial("m_dress1b", 1, "cab-dress1b",
                        new List<SubjectMap> { new("_BaseMap", "tex_body_d", "bundleT") }) }));
                addresses["addr_dress1b"] = "bundleD1b";
            }
            if (w.Companion1) Companion("belt1", 18, 41, Belt1Bones, "bundleB1", "sb1.bundle");
            // twinned companions carry the first's vertex cloud, so the two are one mesh under two names
            if (w.Companion2)
                Companion("belt2", w.TwinCompanions ? 18 : 17, w.TwinCompanions ? 41 : 43,
                    Belt2Bones, "bundleB2", "sb2.bundle", casts: !w.ShadowOffCompanion2,
                    withheld: w.WithheldCompanion2, tier: w.Companion2Tier,
                    tierWithheld: w.WithheldCompanion2Tier);
            if (w.SharedExtra)
            {
                Companion("scarf1", 15, 47, Belt1Bones, "bundleS1", "ss1.bundle");
                Companion("scarf2", 15, 47, Belt2Bones, "bundleS2", "ss2.bundle");
            }
            if (w.BlockedCompanion)
                // its name alone fails the content policy, so nothing about it can ever be read
                parts.Add(new SubjectPart("belt1x", "c_Helena_belt_lod0", "addr_belt1x",
                    new[] { new SubjectMaterial("m_belt1x", 1, "cab-belt1x", new List<SubjectMap>()) }));

            void Companion(string token, int verts, int seed, uint[] bones, string bundleId, string file,
                bool casts = true, bool withheld = false, bool tier = false, bool tierWithheld = false)
            {
                string path = Path.Combine(_root, file);
                SyntheticBundle.BuildOneSkinnedMesh(path, $"c_vesna01_{token}_lod0", Cloud(verts, seed),
                    WrappedTris(verts), bones);
                bytes[bundleId] = File.ReadAllBytes(path);
                var tiers = new List<RecipeTierSlot>();
                if (tier)
                {
                    // a vertex count of its own, so the tier is a witnessable draw distinct from the lod0
                    string tierPath = Path.Combine(_root, $"t_{file}");
                    SyntheticBundle.BuildOneSkinnedMesh(tierPath, $"c_vesna01_{token}_lod1",
                        Cloud(verts + 5, seed + 100), WrappedTris(verts + 5), bones);
                    bytes[$"{bundleId}T"] = File.ReadAllBytes(tierPath);
                    tiers.Add(new RecipeTierSlot($"c_vesna01_{token}_lod1", $"addr_{token}_l1",
                        Visibility: tierWithheld
                            ? Remold.Core.Model.VisibilityOverride.DormHidden
                            : Remold.Core.Model.VisibilityOverride.None));
                    addresses[$"addr_{token}_l1"] = $"{bundleId}T";
                }
                parts.Add(new SubjectPart(token, $"c_vesna01_{token}_lod0", $"addr_{token}",
                    new[] { new SubjectMaterial($"m_{token}", 1, $"cab-{token}", new List<SubjectMap>()) },
                    SiblingTiers: tiers.Count > 0 ? tiers.ToArray() : null,
                    CastsShadows: casts,
                    Visibility: withheld
                        ? Remold.Core.Model.VisibilityOverride.DormHidden
                        : Remold.Core.Model.VisibilityOverride.None));
                addresses[$"addr_{token}"] = bundleId;
            }
        }
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, parts.ToArray(),
            Skeleton: null, Problems: Array.Empty<string>());
        return new BuildEnv(
            (c, s) => c == "Vesna" && s == "VesnaSSR01" ? model : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0");
    }

    /// <summary>A donor glb weighted to <paramref name="bones"/> (<see cref="BodyBones"/> by default),
    /// two submeshes over one vertex pool. <paramref name="rotate"/> bakes a scene-rest rotation into
    /// the geometry — the shape a workspace glb has over a prefab body's uprighting.</summary>
    private void WriteDonorGlb(string file = "donor.glb", uint[]? bones = null,
        System.Numerics.Matrix4x4? rotate = null)
    {
        var boneSet = bones ?? BodyBones;
        const int verts = 6;
        var mesh = new UnityMesh
        {
            Name = "donor",
            VertexCount = verts,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = Cloud(verts, 11),
                ["Normal"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 0, 1, 0 }).ToArray(),
                ["Tangent"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 1, 0, 0, 1 }).ToArray(),
                ["TexCoord0"] = Enumerable.Range(0, verts).SelectMany(v => new float[] { v / 8f, v / 8f }).ToArray(),
                ["BlendWeight"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 1, 0, 0, 0 }).ToArray(),
                ["BlendIndices"] = Enumerable.Range(0, verts)
                    .SelectMany(v => new float[] { v % boneSet.Length, 0, 0, 0 }).ToArray(),
            },
            Dims = new Dictionary<string, int>
            {
                ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2,
                ["BlendWeight"] = 4, ["BlendIndices"] = 4,
            },
            Submeshes = new List<int[]> { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } },
        };
        if (rotate is { } g) mesh = RestBake.Apply(mesh, g);
        var skin = new MeshSkin
        {
            BoneHashes = boneSet,
            BindPoses = boneSet.Select(_ => System.Numerics.Matrix4x4.Identity).ToArray(),
        };
        MeshGltf.ExportRiggedGlb(mesh, skin, _ => null, Path.Combine(_proj, file));
    }

    [Fact]
    public void A_replace_build_ships_one_map_for_two_submeshes_authored_from_one_image()
    {
        // The intake writes its OWN copy of an authored map per submesh, so one painted image reaches the
        // build under a name per submesh. This is the whole route — derivation, donor compile, encode,
        // emission — not just the encode claim underneath it.
        var env = MakeSkinnedEnv();
        var p = NewProject("Swap");
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "authored.png"));
        foreach (var copy in new[] { "donor_s0_base.png", "donor_s1_base.png" })
            File.Copy(Path.Combine(_proj, "authored.png"), Path.Combine(_proj, copy));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "donor.glb",
            DonorTextures = new List<SubmeshTextures>
            {
                new() { Submesh = 0, Albedo = "donor_s0_base.png" },
                new() { Submesh = 1, Albedo = "donor_s1_base.png" },
            },
        });

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.Single(Directory.GetFiles(r.OutDir, "donor_*.dds"));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Equal(1, CountOf(ini, "[Resource_Tex0]"));
        Assert.DoesNotContain("Resource_Tex1", ini);
        // the first submesh binds that one resource and the second, wanting the same, rebinds nothing
        string list = ini[ini.IndexOf("[CommandListDraw_", StringComparison.Ordinal)..];
        Assert.Equal(1, CountOf(list, "if $zz_slot_a == 0\nps-t0 = Resource_Tex0\nendif\n"));
        Assert.Equal(2, CountOf(list, "drawindexed = "));
    }

    [Fact]
    public void A_replace_ships_an_authored_normal_and_binds_it_on_its_own_submesh_only()
    {
        // Per-submesh, per-slot: the authored albedo and normal reach the draw of the submesh whose row
        // carried them, that row's unauthored RMO takes the neutral, and the other submesh — which
        // authored nothing — keeps the anchor's real maps on all three slots.
        var env = MakeSkinnedEnv(anchorNormal: true);
        var p = NewProject("Nrm");
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "s0_base.png"));
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(120, 130, 250, 255)))
            img.SaveAsPng(Path.Combine(_proj, "s0_nrm.png"));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "donor.glb",
            DonorTextures = new List<SubmeshTextures>
            {
                new() { Submesh = 0, Albedo = "s0_base.png", Normal = "s0_nrm.png" },
            },
        });

        var r = ModBuilder.Build(p, env, _out, zip: false);

        // both maps ship, and the anchor's own normal is tagged so the authored one has a slot to bind at
        Assert.Equal(2, Directory.GetFiles(r.OutDir, "donor_*.dds").Length);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"filter_index = {MigotoEmitter.FilterNormal}", ini);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("no anchor normal could be slot-tagged"));

        string list = ini[ini.IndexOf("[CommandListDraw_", StringComparison.Ordinal)..];
        var chunks = list.Split("drawindexed = ");
        string second = chunks[1][(chunks[1].IndexOf('\n') + 1)..];
        // submesh 0 binds both authored maps and the neutral its unauthored RMO defaults to; submesh 1
        // puts all three slots back to the anchor's own
        Assert.Contains("if $zz_slot_n == 0\nps-t0 = Resource_Tex1\nendif\n", chunks[0]);
        Assert.Contains("if $zz_slot_a == 0\nps-t0 = Resource_Tex0\nendif\n", chunks[0]);
        Assert.Contains("if $zz_slot_r == 0\nps-t0 = Resource_NeutralRMO\nendif\n", chunks[0]);
        Assert.Contains("if $zz_slot_n == 0\nps-t0 = Resource_SaveT0\nendif\n", second);
        Assert.Contains("if $zz_slot_a == 0\nps-t0 = Resource_SaveT0\nendif\n", second);
        Assert.Contains("if $zz_slot_r == 0\nps-t0 = Resource_SaveT0\nendif\n", second);
        // the normal was authored, so no flat normal is asked for, declared or shipped — only the RMO's
        Assert.DoesNotContain("Resource_NeutralN", ini);
        Assert.False(File.Exists(Path.Combine(r.OutDir, "neutral_n.dds")));
        Assert.True(File.Exists(Path.Combine(r.OutDir, "neutral_rmo.dds")));
    }

    [Fact]
    public void An_authored_albedo_alone_neutralises_its_own_submeshs_normal_and_rmo()
    {
        // The shape of a repaint donor: one authored albedo, nothing else. Its submesh draws on donor UVs,
        // so its normal and RMO take the flat maps rather than the anchor's relief read through foreign
        // UVs — and the untouched submesh next to it keeps every real map. The anchor has no RMO to
        // slot-tag, so the neutral RMO has nowhere to bind and the author is told.
        var env = MakeSkinnedEnv(anchorNormal: true);
        var p = NewProject("Flat");
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "s0_base.png"));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "donor.glb",
            DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "s0_base.png" } },
        });

        var r = ModBuilder.Build(p, env, _out, zip: false);

        // one authored map ships; both flat maps ship because that one row now asks for them
        Assert.Single(Directory.GetFiles(r.OutDir, "donor_*.dds"));
        Assert.True(File.Exists(Path.Combine(r.OutDir, "neutral_n.dds")));
        Assert.True(File.Exists(Path.Combine(r.OutDir, "neutral_rmo.dds")));

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string list = ini[ini.IndexOf("[CommandListDraw_", StringComparison.Ordinal)..];
        var chunks = list.Split("drawindexed = ");
        string second = chunks[1][(chunks[1].IndexOf('\n') + 1)..];
        Assert.Contains("if $zz_slot_a == 0\nps-t0 = Resource_Tex0\nendif\n", chunks[0]);
        Assert.Contains("if $zz_slot_n == 0\nps-t0 = Resource_NeutralN\nendif\n", chunks[0]);
        Assert.Contains("if $zz_slot_r == 0\nps-t0 = Resource_NeutralRMO\nendif\n", chunks[0]);
        Assert.Contains("if $zz_slot_a == 0\nps-t0 = Resource_SaveT0\nendif\n", second);
        Assert.Contains("if $zz_slot_n == 0\nps-t0 = Resource_SaveT0\nendif\n", second);
        Assert.Contains("if $zz_slot_r == 0\nps-t0 = Resource_SaveT0\nendif\n", second);

        // a neutral needs its kind's slot tag exactly as an authored map does: the anchor has albedo and
        // normal tagged, so only the RMO warns
        Assert.Contains(r.Warnings, w => w.Contains("no anchor RMO could be slot-tagged"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("no anchor normal could be slot-tagged"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("no anchor base color could be slot-tagged"));
    }

    /// <summary>Blanking a normal slot is one gesture across two homes: the neutral a modder plugs in Blender
    /// and the flat map the build binds have to carry the same content, or the session shows something the
    /// mod does not ship. The flat RMO answers a different ask — an unauthored RMO on a submesh that draws on
    /// donor UVs — so it stands on its own constant, in the order the game samples.</summary>
    [Fact]
    public void The_shipped_flat_maps_carry_the_pixels_a_blanked_slot_binds()
    {
        var env = MakeSkinnedEnv(anchorNormal: true);
        var p = NewProject("Flat");
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "s0_base.png"));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "donor.glb",
            DonorTextures = new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "s0_base.png" } },
        });

        var r = ModBuilder.Build(p, env, _out, zip: false);

        var texDir = Path.Combine(_proj, "textures");
        PreviewMaps.WriteNeutrals(texDir);
        // a tangent normal reads the same in both spaces, so the workspace PNG and the shipped DDS match outright
        AssertFlatDdsIs(Path.Combine(r.OutDir, "neutral_n.dds"),
            FirstPixelOf(Path.Combine(texDir, PreviewMaps.NeutralN)));
        // R roughness, G metallic, B occlusion, A emissive mask — the order the game samples
        AssertFlatDdsIs(Path.Combine(r.OutDir, "neutral_rmo.dds"), new Rgba32(128, 0, 255, 0));
    }

    private static Rgba32 FirstPixelOf(string png)
    {
        using var img = Image.Load<Rgba32>(png);
        return img[0, 0];
    }

    /// <summary>A single colour in the shipped map's own channel order, against the flat DDS itself — the DDS
    /// is uncompressed RGBA, so re-serialising that colour reproduces the shipped file byte for byte.</summary>
    private static void AssertFlatDdsIs(string flatDds, Rgba32 gameOrder)
    {
        Assert.Equal(FlatDds.Build((gameOrder.R, gameOrder.G, gameOrder.B, gameOrder.A), srgb: false),
            File.ReadAllBytes(flatDds));
    }

    /// <summary>The whole point of the intake's RMO slot: an RMO authored in Blender reaches its own
    /// submesh's draw, at whichever slot the probe found the anchor's RMO in.</summary>
    [Fact]
    public void An_authored_RMO_ships_and_binds_on_its_own_submesh()
    {
        var env = MakeSkinnedEnv(anchorNormal: true, anchorRmo: true);
        var p = NewProject("Rmo");
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(40, 90, 210, 255)))
            img.SaveAsPng(Path.Combine(_proj, "s0_rmo.png"));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "donor.glb",
            DonorTextures = new List<SubmeshTextures>
            {
                new() { Submesh = 0, Rmo = "s0_rmo.png", RmoOrigin = SlotOrigin.Authored },
            },
        });

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.Single(Directory.GetFiles(r.OutDir, "donor_*.dds"));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"filter_index = {MigotoEmitter.FilterRmo}", ini);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("no anchor RMO could be slot-tagged"));

        string list = ini[ini.IndexOf("[CommandListDraw_", StringComparison.Ordinal)..];
        var chunks = list.Split("drawindexed = ");
        string second = chunks[1][(chunks[1].IndexOf('\n') + 1)..];
        Assert.Contains("if $zz_slot_r == 0\nps-t0 = Resource_Tex0\nendif\n", chunks[0]);
        Assert.Contains("if $zz_slot_r == 0\nps-t0 = Resource_SaveT0\nendif\n", second);
    }

    /// <summary>A stock map the modder left alone is NOT an unfilled slot. It keeps drawing, which is what
    /// the Edit pane's card has always shown.</summary>
    [Fact]
    public void A_stock_untouched_normal_beside_an_authored_albedo_keeps_the_real_map()
    {
        var env = MakeSkinnedEnv(anchorNormal: true);
        var p = NewProject("Own");
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "s0_base.png"));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "donor.glb",
            DonorTextures = new List<SubmeshTextures>
            {
                new()
                {
                    Submesh = 0, Albedo = "s0_base.png",
                    NormalOrigin = SlotOrigin.VanillaOwn, RmoOrigin = SlotOrigin.VanillaOwn,
                },
            },
        });

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string list = ini[ini.IndexOf("[CommandListDraw_", StringComparison.Ordinal)..];
        var chunks = list.Split("drawindexed = ");
        Assert.Contains("if $zz_slot_a == 0\nps-t0 = Resource_Tex0\nendif\n", chunks[0]);
        // no flat map is asked for, declared or shipped: the anchor's own normal and RMO keep drawing
        Assert.DoesNotContain("Resource_NeutralN", ini);
        Assert.DoesNotContain("Resource_NeutralRMO", ini);
        Assert.False(File.Exists(Path.Combine(r.OutDir, "neutral_n.dds")));
        Assert.False(File.Exists(Path.Combine(r.OutDir, "neutral_rmo.dds")));
        Assert.DoesNotContain("$zz_slot_n == 0\nps-t0 = ", chunks[0]);
        Assert.DoesNotContain("$zz_slot_r == 0\nps-t0 = ", chunks[0]);
        // and nothing warns about an RMO the modder never authored
        Assert.DoesNotContain(r.Warnings, w => w.Contains("RMO"));
    }

    /// <summary>Plugging the shipped neutral into a slot is the "blank this" gesture: it binds the flat
    /// map, ships no texture of its own, and leaves the slots it did not name alone.</summary>
    [Fact]
    public void An_explicit_neutral_normal_binds_the_flat_map_and_ships_nothing()
    {
        var env = MakeSkinnedEnv(anchorNormal: true);
        var p = NewProject("Blank");
        WriteDonorGlb();
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "donor.glb",
            DonorTextures = new List<SubmeshTextures>
            {
                new()
                {
                    Submesh = 0,
                    AlbedoOrigin = SlotOrigin.VanillaOwn,
                    NormalOrigin = SlotOrigin.ExplicitNeutral,
                    RmoOrigin = SlotOrigin.VanillaOwn,
                },
            },
        });

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.Empty(Directory.GetFiles(r.OutDir, "donor_*.dds"));
        Assert.True(File.Exists(Path.Combine(r.OutDir, "neutral_n.dds")));
        Assert.False(File.Exists(Path.Combine(r.OutDir, "neutral_rmo.dds")));

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string list = ini[ini.IndexOf("[CommandListDraw_", StringComparison.Ordinal)..];
        var chunks = list.Split("drawindexed = ");
        Assert.Contains("if $zz_slot_n == 0\nps-t0 = Resource_NeutralN\nendif\n", chunks[0]);
        Assert.DoesNotContain("Resource_NeutralRMO", ini);
        Assert.DoesNotContain("$zz_slot_a == 0\nps-t0 = ", chunks[0]);
    }

    // ---- the loud failures -----------------------------------------------------------------------

    [Fact]
    public void Nothing_to_build_refuses()
    {
        var env = MakeEnv(out _, out _);
        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(NewProject(), env, _out));
        Assert.Contains("nothing to build", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_hidden_mesh_outside_the_roster_warns_and_builds_nothing()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject();
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_ghost_lod0", true);
        // the stale hide is warned away by derivation, leaving an empty build — refused loudly
        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out));
        Assert.Contains("nothing to build", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_edited_mesh_outside_the_roster_refuses_loudly()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject();
        File.WriteAllText(Path.Combine(_proj, "ghost.glb"), "x");
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_vesna01_ghost_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "ghost.glb",   // no original on record = edited
        });
        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out));
        Assert.Contains("not in", ex.Message);
    }

    [Fact]
    public void A_failed_build_leaves_no_final_folder_or_zip()
    {
        // unresolvable sibling address → the failure hits AFTER work dirs are created
        var env = MakeEnv(out _, out _);
        var broken = new BuildEnv(env.ResolveSubject, a => a == "addr_body" ? "bundle0" : null,
            env.Deobfuscate, env.CatalogVersion, env.AppVersion);
        var p = NewProject("Doomed");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);

        Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, broken, _out));
        Assert.False(Directory.Exists(Path.Combine(_out, "doomed_v1_0")));
        Assert.False(File.Exists(Path.Combine(_out, "doomed_v1_0.zip")));
        Assert.Empty(Directory.GetDirectories(_out));   // work/tmp dirs swept
    }

    // ---- one mesh worn by two subjects: which of the two rules answers ----------------------------
    // Driven directly, like the donor route above: a Replace build needs bone tables the synthetic
    // bundles don't carry.

    [Fact]
    public void Two_Replaces_on_one_shared_mesh_refuse_naming_both_subjects()
    {
        // Byte-identical meshes reached through two subjects ARE one vanilla draw. Two overrides on it
        // fight, and the build can't pick a winner.
        var clash = ModBuilder.ReplacedMeshConflict(new[]
        {
            ("CommanderMale · CommanderMale · face", "210b832c"),
            ("CommanderMale · CommanderNeutral · face", "210b832c"),
        });

        Assert.Equal("'CommanderMale · CommanderMale · face' and 'CommanderMale · CommanderNeutral · face' "
            + "replace one mesh they share. Two overrides on one hash fight over the draw. "
            + "Drop one of the two Replaces", clash);
    }

    [Fact]
    public void Replaces_on_meshes_that_only_share_a_name_are_no_conflict()
    {
        // same slot name, different assets — the reason the test is the hash and not the name
        Assert.Null(ModBuilder.ReplacedMeshConflict(new[]
        {
            ("CommanderMale · CommanderMale · face", "210b832c"),
            ("CommanderMale · CommanderMale03 · face", "00927735"),
        }));
    }

    [Fact]
    public void One_dump_name_reached_through_two_bundles_is_one_dump()
    {
        // The same mesh reached from two subjects comes out of each subject's OWN bundle. That is one
        // dump — the case a bundle-keyed identity called "two different meshes". Only differing CONTENT
        // is the refusal.
        var held = new ModBuilder.DumpIdentity("c_CommanderMale_dorm_face_lod0", "210b832c");
        Assert.Null(ModBuilder.DumpNameConflict("commandermale_face", held,
            new ModBuilder.DumpIdentity("c_CommanderMale_dorm_face_lod0", "210b832c")));

        // different content under one dump name would feed a pipeline foreign geometry
        var differs = ModBuilder.DumpNameConflict("commandermale_face", held,
            new ModBuilder.DumpIdentity("c_CommanderMale_dorm_face_lod0", "00927735"));
        Assert.Contains("maps to two different meshes", differs, StringComparison.Ordinal);

        // and so would a different mesh under it
        Assert.NotNull(ModBuilder.DumpNameConflict("commandermale_face", held,
            new ModBuilder.DumpIdentity("c_CommanderMale_dorm_hair_lod0", "210b832c")));
    }

    // ---- tier suffix, pool identity, tag coexistence, and the un-bake ------------------------------

    /// <summary>A Replace target on the synthetic world's body part.</summary>
    private void AddReplaceTarget(ModProject p, string mesh = "c_vesna01_body_lod0",
        List<SubmeshTextures>? textures = null, List<float>? bakedRest = null) =>
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = mesh,
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "donor.glb", DonorTextures = textures, BakedRest = bakedRest,
        });

    /// <summary>3DMigoto drops a duplicate-named section SILENTLY, so no emitted ini may carry one. On a
    /// shared hash the runtime runs EVERY section whose draw-call match filters pass, so two sections may
    /// share a hash only when their (match_first_index, match_index_count) tuples differ — the routed
    /// draw's shape. Two sections on one hash with the same tuple (or both unfiltered) would both fire on
    /// the same draws, which no emission intends.</summary>
    internal static void AssertNoDuplicateSections(string ini)
    {
        var lines = ini.Split('\n').Select(l => l.Trim()).ToList();
        static bool IsSection(string l) => l.StartsWith('[') && l.EndsWith(']');
        var dupes = lines.Where(IsSection)
            .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, $"duplicate ini sections: {string.Join(", ", dupes)}");

        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        string? hash = null, first = null, count = null;
        void Commit()
        {
            if (hash is null) return;
            string key = $"{hash}|{first ?? "*"}|{count ?? "*"}";
            if (owner.TryGetValue(key, out var held))
            {
                // A section whose name extends the other's is a deliberate same-hash companion — the
                // name extension IS the runtime ordering mechanism (equal priority runs sections in
                // name order), so an owner and its trailing companion legitimately share the tuple.
                static string Stem(string s) => s.Trim('[', ']');
                bool companions = Stem(section).StartsWith(Stem(held) + "_", StringComparison.OrdinalIgnoreCase)
                    || Stem(held).StartsWith(Stem(section) + "_", StringComparison.OrdinalIgnoreCase);
                if (!companions)
                    Assert.Fail($"hash {hash} (first_index {first ?? "any"}, index_count {count ?? "any"}) "
                        + $"is claimed by both {held} and {section}");
            }
            else owner[key] = section;
        }
        foreach (var l in lines)
        {
            if (IsSection(l)) { Commit(); section = l; hash = first = count = null; continue; }
            if (l.StartsWith("hash = ", StringComparison.Ordinal)) hash = l["hash = ".Length..];
            else if (l.StartsWith("match_first_index = ", StringComparison.Ordinal)) first = l["match_first_index = ".Length..];
            else if (l.StartsWith("match_index_count = ", StringComparison.Ordinal)) count = l["match_index_count = ".Length..];
        }
        Commit();
    }

    /// <summary>Every shader and buffer the ini names has to be in the folder beside it. 3DMigoto reports a
    /// missing file only in its own log, so a section referencing one that was never written costs the mod
    /// that whole pass in game with nothing to see offline.</summary>
    internal static void AssertEveryReferencedFileShips(string ini, string outDir)
    {
        var missing = ini.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("cs = ", StringComparison.Ordinal)
                     || l.StartsWith("filename = ", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf('=') + 2)..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !File.Exists(Path.Combine(outDir, f)))
            .ToList();
        Assert.True(missing.Count == 0, $"ini references files the build didn't ship: {string.Join(", ", missing)}");
    }

    [Fact]
    public void A_variant_tier_keys_its_chain_by_lod_label_not_trailing_token()
    {
        // …_lod1_Fight is the lod1 link of its part's tier chain: the suffix keys the emitter's
        // cross-part tier pairing and the per-frame flag, so the trailing token must not stand in.
        var env = MakeSkinnedEnv(meshTail: "_Fight");
        var p = NewProject("SwapVariant");
        WriteDonorGlb();
        AddReplaceTarget(p, "c_vesna01_body_lod0_Fight");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains("vesna_body_lod1", ini);
        Assert.DoesNotContain("vesna_body_Fight", ini);
    }

    [Fact]
    public void Two_pool_parts_sharing_one_index_buffer_refuse()
    {
        // One capture section serves one hash: two pool parts on the same ib would alias each
        // other's posed captures, so the build refuses rather than ship wrong geometry.
        var env = MakeSkinnedEnv(twinPart: true);
        var p = NewProject("SwapTwin");
        WriteDonorGlb(bones: BodyBones.Concat(TwinBones).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));
        Assert.Contains("share one draw signature", ex.Message);
    }

    [Fact]
    public void A_wardrobe_sibling_is_excluded_from_the_pool_and_named_in_the_refusal()
    {
        // End to end through BuildEnv.PartSchemeFor: the scheme marks body2 a wardrobe option, the
        // presence filter keeps it out of the body's pool, and a donor riding its bones is told so.
        // The uvSeed keeps the two signature-separable, so no twin refusal fires ahead of this one.
        var env = MakeSkinnedEnv(twinPart: true, twinPartUvSeed: 7);
        env = env with
        {
            PartSchemeFor = stem => stem == "VesnaSSR01"
                ? new[]
                {
                    new Remold.Core.Tables.PartScheme.Slot(1, new[]
                    {
                        new Remold.Core.Tables.PartScheme.Variant(11, true, new[] { "body2" }),
                        new Remold.Core.Tables.PartScheme.Variant(12, false, new[] { "body9" }),
                    }),
                }
                : null,
        };
        var p = NewProject("WardrobePool");
        WriteDonorGlb(bones: BodyBones.Concat(TwinBones).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.ThrowsAny<Exception>(() => ModBuilder.Build(p, env, _out, zip: false));
        Assert.Contains("'c_vesna01_body2_lod0' · it is a wardrobe option", ex.Message);
    }

    [Fact]
    public void A_shadow_off_sibling_is_excluded_from_the_pool_and_named_in_the_refusal()
    {
        // End to end through the roster probe: body2's renderer is outside the shadow pass, so the probe
        // must carry that flag onto PoolDerive.PartBones for the third exclusion to fire, and the donor
        // riding its bones is told which part it landed on. Pins ModBuilder's `CastsShadows: p.CastsShadows`
        // pass-through — without it every part reads as casting and body2 pools as normal.
        //
        // The control is Twins_whose_vb1_differs_build_with_vb1_keyed_sections: the SAME fixture with the
        // flag left alone builds clean, so the flag alone decided this refusal. The uvSeed keeps the two
        // signature-separable, so no twin refusal fires ahead of it, and no scheme is wired, so the
        // presence rule that precedes this one admits body2.
        var env = MakeSkinnedEnv(twinPart: true, twinPartUvSeed: 7, twinPartShadowOff: true);
        var p = NewProject("ShadowOffPool");
        WriteDonorGlb(bones: BodyBones.Concat(TwinBones).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.ThrowsAny<Exception>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_body2_lod0' · it casts no shadow, so the game stops drawing it the "
            + "moment it leaves the camera, and only a Replace on that part itself pools it", ex.Message);
    }

    [Fact]
    public void Twins_whose_vb1_differs_build_with_vb1_keyed_sections()
    {
        // Same triangle list, different geometry AND different stream-1 bytes: the signature key falls
        // back to each mesh's vb1 hash, so both capture separately and the shared ib appears nowhere.
        var env = MakeSkinnedEnv(twinPart: true, twinPartUvSeed: 7);
        var p = NewProject("SwapTwinVb1");
        WriteDonorGlb(bones: BodyBones.Concat(TwinBones).ToArray());
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string sharedIb = SkinnedIb("s0.bundle", "c_vesna01_body_lod0");
        Assert.Equal(SkinnedIb("s2.bundle", "c_vesna01_body2_lod0"), sharedIb);
        Assert.Contains($"[TextureOverride_Cap_vesna_body]\nhash = {SkinnedVb1("s0.bundle", "c_vesna01_body_lod0")}\nmatch_priority = 0\n", ini);
        Assert.Contains($"[TextureOverride_Cap_vesna_body2]\nhash = {SkinnedVb1("s2.bundle", "c_vesna01_body2_lod0")}\nmatch_priority = 0\n", ini);
        Assert.DoesNotContain($"hash = {sharedIb}", ini);
    }

    // ---- twins the bound textures tell apart -----------------------------------------------------

    /// <summary>The section owning <paramref name="hash"/>, header line included, up to the blank line
    /// that ends it.</summary>
    private static string SectionOn(string ini, string hash)
    {
        int at = ini.IndexOf($"\nhash = {hash}\nmatch_priority = 0\n", StringComparison.Ordinal);
        Assert.True(at >= 0, $"no section carries hash {hash}");
        int start = ini.LastIndexOf('[', at);
        int end = ini.IndexOf("\n\n", at, StringComparison.Ordinal);
        return end < 0 ? ini[start..] : ini[start..end];
    }

    /// <summary>The <c>[Present]</c> block, or empty when the ini has none.</summary>
    private static string PresentSection(string ini)
    {
        int at = ini.IndexOf("[Present]\n", StringComparison.Ordinal);
        if (at < 0) return "";
        int end = ini.IndexOf("\n\n", at, StringComparison.Ordinal);
        return end < 0 ? ini[at..] : ini[at..end];
    }

    /// <summary>Every section testing a sticky verdict runs the probe that writes it, ahead of the test.
    /// A section that only READ the variable would never correct it on its own draws, and would act on
    /// whatever the last guarded draw left behind.</summary>
    private static void AssertTwinVerdictIsProbedWhereItIsTested(string ini)
    {
        foreach (var section in ini.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            foreach (var line in section.Split('\n'))
            {
                if (!line.StartsWith("if $zz_tw_", StringComparison.Ordinal)) continue;
                string name = line["if $".Length..line.IndexOf(" ==", StringComparison.Ordinal)];
                int wrote = section.IndexOf($"${name} = ", StringComparison.Ordinal);
                Assert.True(wrote >= 0 && wrote < section.IndexOf(line, StringComparison.Ordinal),
                    $"a section tests ${name} without probing for it first");
            }
    }

    /// <summary>No sticky verdict is cleared per frame. A verdict reset in <c>[Present]</c> would leave
    /// every depth and shadow pass unidentified, which is exactly where no base color is bound.</summary>
    private static void AssertStickyVerdictsSurviveTheFrame(string ini)
    {
        string present = PresentSection(ini);
        foreach (var line in ini.Split('\n'))
            if (line.StartsWith("global $zz_tw_", StringComparison.Ordinal))
                Assert.DoesNotContain(line["global $".Length..line.IndexOf(" =", StringComparison.Ordinal)],
                    present);
    }

    [Fact]
    public void An_ambiguous_target_whose_base_colors_differ_builds_behind_a_draw_time_guard()
    {
        // Same index buffer, different geometry, no vb1 to separate them — but the two parts bind
        // different base colors, so the section can ask at draw time which one is on the slots.
        var env = MakeSkinnedEnv(twinPart: true, twinPartPosSeed: 21, twinPartOwnAlbedo: true);
        var p = NewProject("SwapTwinGuard");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string shared = SkinnedIb("s0.bundle", "c_vesna01_body_lod0");
        Assert.Equal(SkinnedIb("s2.bundle", "c_vesna01_body2_lod0"), shared);
        int own = MigotoEmitter.RetexTag(_stockTexHash), mate = MigotoEmitter.RetexTag(_altTexHash);
        string v = $"zz_tw_{shared}";
        // the verdict is declared once and never reset, so a pass binding no base color acts on the
        // last identification rather than standing down
        Assert.Contains($"global ${v} = 0\n", ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
        Assert.Contains($"[TextureOverride_TwinTag_{_stockTexHash}]\nhash = {_stockTexHash}\n"
            + $"filter_index = {own}\nmatch_priority = 100\n", ini);
        Assert.Contains($"[TextureOverride_TwinTag_{_altTexHash}]\nhash = {_altTexHash}\n"
            + $"filter_index = {mate}\nmatch_priority = 100\n", ini);
        string cap = SectionOn(ini, shared);
        // the probe answers for BOTH siblings, which is what corrects the verdict after a wardrobe change
        Assert.Contains($"$zz_t = ps-t0\nif $zz_t == {own}\n${v} = 1\nendif\n"
            + $"if $zz_t == {mate}\n${v} = 2\nendif\n", cap);
        // the capture AND the suppression sit inside the guard, and nothing else skips on this hash
        Assert.Contains($"if ${v} == 1\nResource_vesna_body_Posed = ref vb0\n"
            + "Resource_vesna_body_CB = copy vs-cb1\nhandling = skip\n", cap);
        Assert.Equal(1, CountOf(cap, "handling = skip"));
        Assert.EndsWith("run = CommandListDraw_vesna_body\nendif", cap);
        Assert.Contains(r.Diagnostics, d => d.Contains("'body' shares a draw signature with 'body2'")
            && d.Contains("act while its own textures answer for it"));
        AssertTwinVerdictIsProbedWhereItIsTested(ini);
    }

    [Fact]
    public void An_ambiguous_target_whose_own_albedo_is_slot_tagged_probes_the_slot_tag_value()
    {
        // Authored donor maps already tag the anchor's own base color with the ALBEDO kind value. The
        // guard probes for that value rather than minting a second section on the hash, which the ini
        // parse would drop.
        var env = MakeSkinnedEnv(twinPart: true, twinPartPosSeed: 21, twinPartOwnAlbedo: true);
        var p = NewProject("SwapTwinSlotTagged");
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "s0_base.png"));
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "s0_base.png" },
        });

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string shared = SkinnedIb("s0.bundle", "c_vesna01_body_lod0");
        string v = $"zz_tw_{shared}";
        // the slot tag stands, and it is the only section on that hash
        Assert.Contains($"[TextureOverride_SlotTag_{_stockTexHash}]\nhash = {_stockTexHash}\n"
            + "filter_index = 3301\nmatch_priority = 100\n", ini);
        Assert.DoesNotContain($"[TextureOverride_TwinTag_{_stockTexHash}]", ini);
        Assert.Equal(1, CountOf(ini, $"hash = {_stockTexHash}\n"));
        // the sibling's base color is tagged by nobody else, so it still mints one
        Assert.Contains($"[TextureOverride_TwinTag_{_altTexHash}]\nhash = {_altTexHash}\n"
            + $"filter_index = {MigotoEmitter.RetexTag(_altTexHash)}\nmatch_priority = 100\n", ini);
        string cap = SectionOn(ini, shared);
        Assert.Contains($"$zz_t = ps-t0\nif $zz_t == 3301\n${v} = 1\nendif\n", cap);
        Assert.Contains($"if ${v} == 1\nResource_vesna_body_Posed = ref vb0\n", cap);
        AssertTwinVerdictIsProbedWhereItIsTested(ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
    }

    [Fact]
    public void Ambiguous_twins_wearing_one_base_color_still_refuse()
    {
        // Nothing at draw time separates them, so a refusal naming the mesh it collides with is the
        // only honest answer.
        var env = MakeSkinnedEnv(twinPart: true, twinPartPosSeed: 21, twinPartSharedAlbedo: true);
        var p = NewProject("SwapTwinSame");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));
        Assert.Contains("'c_vesna01_body_lod0' and 'body2' share one draw signature", ex.Message);
        Assert.Contains("can't act on one without hitting the other", ex.Message);
    }

    [Fact]
    public void An_ambiguous_pool_part_whose_base_color_separates_it_captures_behind_a_guard()
    {
        // The ambiguous mesh is a Leave the Replace leans on for recovery, not the target. Its capture
        // would otherwise hold whichever of the two drew last; the guard keeps it to its own draws.
        var env = MakeSkinnedEnv(poolMate: true, clothWearer: true, clothTwinsMate: true, clothOwnAlbedo: true);
        var p = NewProject("SwapPoolGuard");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string shared = SkinnedIb("sm.bundle", "c_vesna01_mate_lod0");
        Assert.Equal(SkinnedIb("sc.bundle", "c_vesna01_cloth_lod0"), shared);
        string v = $"zz_tw_{shared}";
        // roster order numbers the siblings, so the cloth answers 1 and the pooled mate 2
        int cloth = MigotoEmitter.RetexTag(_altTexHash), mate = MigotoEmitter.RetexTag(_stockTexHash);
        string cap = SectionOn(ini, shared);
        Assert.Contains($"$zz_t = ps-t0\nif $zz_t == {cloth}\n${v} = 1\nendif\n"
            + $"if $zz_t == {mate}\n${v} = 2\nendif\n", cap);
        Assert.Contains($"if ${v} == 2\nResource_vesna_mate_Posed = ref vb0\n", cap);
        Assert.Contains(r.Diagnostics, d => d.Contains("'mate' shares a draw signature with 'cloth'"));
        AssertTwinVerdictIsProbedWhereItIsTested(ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
    }

    [Fact]
    public void An_ambiguous_pool_tier_whose_base_color_separates_it_is_guarded_rather_than_left_vanilla()
    {
        // The mate's lod1 draws on the body lod1's index buffer, so a capture on that hash would hold
        // whichever of the two drew last. The two parts bind different base colors, so the tier keeps
        // its section behind a guard instead of standing down to a vanilla draw.
        var env = MakeSkinnedEnv(poolMate: true, mateTierTwin: true, mateOwnAlbedo: true);
        var p = NewProject("TierTwinGuard");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string tierIb = SkinnedIb("s1.bundle", "c_vesna01_body_lod1");
        Assert.Equal(SkinnedIb("smt.bundle", "c_vesna01_mate_lod1"), tierIb);
        string v = $"zz_tw_{tierIb}";
        Assert.DoesNotContain(r.Warnings, w => w.Contains("vanilla draw is left running"));
        Assert.Contains("[TextureOverride_Cap_vesna_body_lod1]", ini);
        Assert.Contains($"global ${v} = 0\n", ini);
        string tier = SectionOn(ini, tierIb);
        Assert.Contains($"$zz_t = ps-t0\nif $zz_t == {MigotoEmitter.RetexTag(_stockTexHash)}\n${v} = 1\nendif\n"
            + $"if $zz_t == {MigotoEmitter.RetexTag(_altTexHash)}\n${v} = 2\nendif\n", tier);
        Assert.Contains($"if ${v} == 1\nResource_vesna_body_lod1_Posed = ref vb0\n", tier);
        AssertTwinVerdictIsProbedWhereItIsTested(ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
    }

    [Fact]
    public void A_sighting_on_a_guarded_capture_section_records_ahead_of_the_guard()
    {
        // Presence is what the sighting records, and EITHER sibling's draw proves the outfit is on
        // screen. Inside the guard it would miss every frame the other sibling drew, and the latch would
        // read the outfit as absent.
        var env = MakeSkinnedEnv(poolMate: true, clothWearer: true, clothTwinsMate: true, clothOwnAlbedo: true);
        string body = SkinnedIb("s0.bundle", "c_vesna01_body_lod0");
        string shared = SkinnedIb("sm.bundle", "c_vesna01_mate_lod0");
        env = env with
        {
            Sharing = Measured(new Dictionary<string, int[]> { [body] = new[] { 0, 1 } },
                new Dictionary<int, string[]> { [0] = new[] { shared } }),
        };
        var p = NewProject("SwapGuardSighting");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.DoesNotContain("[TextureOverride_Witness_", ini);
        // the OUTFIT sighting rides ahead of the guard (either sibling's draw proves the outfit is on
        // screen); the MESH latch sights INSIDE it — it witnesses the same event as the capture it
        // gates, and a sibling's draw captures nothing, so it must not read as presence
        Assert.Contains($"[TextureOverride_Cap_vesna_mate]\nhash = {shared}\nmatch_priority = 0\n"
            + "$zz_seen_vesnassr01 = 1\n$zz_t = ps-t0\n", ini);
        Assert.Contains("Resource_vesna_mate_Posed = ref vb0\n"
            + "Resource_vesna_mate_CB = copy vs-cb1\n$zz_seen_src_vesna_mate = 1\n",
            SectionOn(ini, shared));
        AssertTwinVerdictIsProbedWhereItIsTested(ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
    }

    [Fact]
    public void A_hide_on_an_ambiguous_mesh_is_guarded_when_the_base_colors_differ()
    {
        // A hide keyed on the shared signature skips the sibling's draws too, which blanks a mesh the
        // author never touched. The guard holds the skip to the hidden mesh's own draws.
        var env = MakeSkinnedEnv(twinPart: true, twinPartPosSeed: 21, twinPartOwnAlbedo: true);
        var p = NewProject("HideTwinGuard");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body2_lod0", true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string shared = SkinnedIb("s2.bundle", "c_vesna01_body2_lod0");
        string v = $"zz_tw_{shared}";
        int body = MigotoEmitter.RetexTag(_stockTexHash), hidden = MigotoEmitter.RetexTag(_altTexHash);
        Assert.Contains($"global $zz_t = 0\nglobal ${v} = 0\n", ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
        Assert.Contains($"[TextureOverride_TwinTag_{_altTexHash}]\nhash = {_altTexHash}\n"
            + $"filter_index = {hidden}\nmatch_priority = 100\n", ini);
        string hide = SectionOn(ini, shared);
        Assert.Contains($"$zz_t = ps-t0\nif $zz_t == {body}\n${v} = 1\nendif\n"
            + $"if $zz_t == {hidden}\n${v} = 2\nendif\n", hide);
        // one verdict opens on the variable itself: no scratch, declared or written
        Assert.EndsWith($"if ${v} == 2\nhandling = skip\nendif", hide);
        Assert.DoesNotContain("zz_twok", ini);
        AssertTwinVerdictIsProbedWhereItIsTested(ini);
    }

    [Fact]
    public void Hiding_both_meshes_of_one_draw_signature_skips_at_each_of_their_draws()
    {
        // One hash, one section, two hidden meshes: a guard admitting only the first claimant's verdict
        // would leave the second twin on screen, which is not what a hide on it asked for.
        var env = MakeSkinnedEnv(twinPart: true, twinPartPosSeed: 21, twinPartOwnAlbedo: true);
        var p = NewProject("HideBothTwins");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body2_lod0", true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string shared = SkinnedIb("s0.bundle", "c_vesna01_body_lod0");
        Assert.Equal(SkinnedIb("s2.bundle", "c_vesna01_body2_lod0"), shared);
        string v = $"zz_tw_{shared}";
        Assert.Contains($"global ${v} = 0\nglobal $zz_twok = 0\n", ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
        // both verdicts fold into the scratch, and the skip opens on it once
        string hide = SectionOn(ini, shared);
        Assert.EndsWith($"$zz_twok = 0\nif ${v} == 1\n$zz_twok = 1\nendif\n"
            + $"if ${v} == 2\n$zz_twok = 1\nendif\nif $zz_twok == 1\nhandling = skip\nendif", hide);
        // the shared signature and the body's own lod1: one skip each, and nothing else skips
        Assert.Equal(2, CountOf(ini, "handling = skip"));
        Assert.Contains(r.Diagnostics, d => d.Contains("'body' shares a draw signature with 'body2'"));
        Assert.Contains(r.Diagnostics, d => d.Contains("'body2' shares a draw signature with 'body'"));
        AssertTwinVerdictIsProbedWhereItIsTested(ini);
    }

    [Fact]
    public void A_plain_retexture_no_guard_probes_is_emitted_without_a_tag()
    {
        // The tag lines belong to the builds that probe for the hash. A build with no guard in it emits
        // the retexture section it always emitted.
        var env = MakeSkinnedEnv();
        var p = NewProject("RetexNoGuard");
        AddEditedTexture(p);
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Retexture, "F9");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"[TextureOverride_Retex_vesna_body_a_{_stockTexHash}]\nhash = {_stockTexHash}\nmatch_priority = 0\n"
            + "if $zz_key_f9 == 1\nthis = Resource_Rtx0\nendif\n", ini);
        Assert.DoesNotContain("filter_index", ini);
        Assert.DoesNotContain("match_priority = 100", ini);
    }

    [Fact]
    public void Two_siblings_on_one_tag_value_refuse_only_where_a_claimed_verdict_is_in_it()
    {
        // Siblings whose tags carry one value are one answer to the probe. A section acting for one of
        // that pair can't tell its own draws apart and refuses; a section acting for NEITHER stays closed
        // at both their draws, which is where it belongs, so it ships.
        Assert.Equal((1, 3), ModBuilder.TwinValueCollision(new[] { 700, 800, 700 }, new[] { 1 }));
        Assert.Equal((1, 3), ModBuilder.TwinValueCollision(new[] { 700, 800, 700 }, new[] { 3 }));
        Assert.Null(ModBuilder.TwinValueCollision(new[] { 700, 800, 700 }, new[] { 2 }));
        // a set of claimed verdicts is answered the same way, one member at a time
        Assert.Equal((2, 4), ModBuilder.TwinValueCollision(new[] { 700, 800, 900, 800 }, new[] { 1, 4 }));
        Assert.Null(ModBuilder.TwinValueCollision(new[] { 700, 800, 900, 800 }, new[] { 1, 3 }));
    }

    [Fact]
    public void A_hide_on_an_ambiguous_mesh_wearing_one_base_color_stays_a_plain_skip()
    {
        // No discriminator, no guard: the skip stays plain, and the hide reaches both draws.
        var env = MakeSkinnedEnv(twinPart: true, twinPartPosSeed: 21, twinPartSharedAlbedo: true);
        var p = NewProject("HideTwinPlain");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body2_lod0", true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.DoesNotContain("zz_tw_", ini);
        Assert.DoesNotContain("TwinTag", ini);
        Assert.Contains($"[TextureOverride_Hide_0]\nhash = "
            + $"{SkinnedIb("s2.bundle", "c_vesna01_body2_lod0")}\nmatch_priority = 0\nhandling = skip\n", ini);
    }

    [Fact]
    public void A_guard_tag_on_a_scoped_retextured_stock_map_emits_one_tag_section()
    {
        // Both mechanisms tag the same stock texture with the same derived value. Two sections on one
        // hash would leave the second dropped at parse time, so the scoped tag serves both.
        var env = MakeSkinnedEnv(twinPart: true, twinPartPosSeed: 21, twinPartOwnAlbedo: true, clothWearer: true);
        env = env with
        {
            Sharing = Measured(new Dictionary<string, int[]>(), new Dictionary<int, string[]>(),
                tex: new Dictionary<string, int[]> { [_stockTexHash] = new[] { 0, 1 } }),
        };
        var p = NewProject("HideTwinScoped");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);
        AddEditedTexture(p, user: "c_vesna01_cloth_lod0");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.Contains($"[TextureOverride_RetexTag_{_stockTexHash}]", ini);
        Assert.DoesNotContain($"[TextureOverride_TwinTag_{_stockTexHash}]", ini);
        Assert.Equal(1, CountOf(ini, $"hash = {_stockTexHash}\n"));
        // and the guard still probes for the value that one tag carries
        string v = $"zz_tw_{SkinnedIb("s0.bundle", "c_vesna01_body_lod0")}";
        Assert.Contains($"if $zz_t == {MigotoEmitter.RetexTag(_stockTexHash)}\n${v} = 1\n", ini);
        AssertTwinVerdictIsProbedWhereItIsTested(ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
    }

    // ---- twins the wardrobe tells apart ----------------------------------------------------------

    /// <summary>The wardrobe scheme of the twin world: one slot, two options, each married to companion
    /// meshes. The ids carry the arithmetic the build reads them by — slot = id / 100, and the last two
    /// digits are the option's place in the slot. Every companion the fixture can build is listed, and a
    /// token the world left out classifies nothing; tokens EXTENDING a listed one (<c>dress1b</c>,
    /// <c>belt1x</c>) land on it, which is how the scheme reads suffixed part names.</summary>
    private static BuildEnv WithWardrobeScheme(BuildEnv env) => env with
    {
        PartSchemeFor = stem => stem == "VesnaSSR01"
            ? new[]
            {
                new Remold.Core.Tables.PartScheme.Slot(700103, new[]
                {
                    new Remold.Core.Tables.PartScheme.Variant(70010301, true,
                        new[] { "dress1", "belt1", "scarf1" }),
                    new Remold.Core.Tables.PartScheme.Variant(70010302, false,
                        new[] { "dress2", "belt2", "scarf2" }),
                }),
            }
            : null,
    };

    /// <summary>A build whose timelines hide <paramref name="nodes"/>: one clip whose hide list names
    /// them. This is the BUILD-TIME input the workbench model never carries, which is why it arrives
    /// through the env and not on the parts.</summary>
    private static BuildEnv WithTimelineHides(BuildEnv env, params string[] nodes) => env with
    {
        TimelineShoesFor = _ => new[]
            { new Remold.Core.Bundles.TimelineShoe(Array.Empty<string>(), nodes) },
    };

    [Fact]
    public void A_prefab_marked_part_keeps_its_own_mechanism_when_a_timeline_names_it_too()
    {
        // PRECEDENCE. Both inputs name body2: the prefab's own coat list, and a timeline hide list. The
        // part's own marker answers first, so the refusal teaches the mechanism that is actually resident
        // on the model rather than the one the build happened to read afterwards. Pins the
        // `if (part.Visibility != None) return part.Visibility;` line — without it the answer falls
        // through to the timeline and the refusal names the wrong data.
        //
        // The control is A_shadow_off_sibling-style: the same fixture with no marker at all builds (see
        // Twins_whose_vb1_differs_build_with_vb1_keyed_sections), so the marker alone decided this.
        var env = WithTimelineHides(
            MakeSkinnedEnv(twinPart: true, twinPartUvSeed: 7,
                twinPartVisibility: Remold.Core.Model.VisibilityOverride.CoatList),
            "c_vesna01_body2_lod0");
        var p = NewProject("VisibilityPrecedence");
        WriteDonorGlb(bones: BodyBones.Concat(TwinBones).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.ThrowsAny<Exception>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_body2_lod0' · the dorm dresses it on and off separately from the "
            + "scene, so only a Replace on that part itself pools it", ex.Message);
        // …and NOT the timeline's sentence, which is what a lost precedence would print
        Assert.DoesNotContain("a dorm scene can hide or reveal it mid-pose", ex.Message);
    }

    [Fact]
    public void A_timeline_naming_only_a_parts_SIBLING_TIER_still_demotes_the_whole_part()
    {
        // A timeline that can flip any ONE of a part's draws makes the whole part unsafe to lean on, so
        // every tier name is offered to the match — not just the representative's. Here the hide list
        // names ONLY c_vesna01_mate_lod1, and the part's own slot name appears in no list. Pins the
        // sibling-tier arm of VisibilityOf; without it the mate reads as unwithheld and pools as normal.
        //
        // The control is A_timeline_naming_no_node_of_the_part_leaves_it_pooled below: the identical
        // fixture with the list naming something else builds clean.
        var env = WithTimelineHides(MakeSkinnedEnv(poolMate: true, mateTier: true),
            "c_vesna01_mate_lod1");
        var p = NewProject("TimelineTierDemotion");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.ThrowsAny<Exception>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_mate_lod0' · a dorm scene can hide or reveal it mid-pose, so only a "
            + "Replace on that part itself pools it", ex.Message);
    }

    [Fact]
    public void A_timeline_naming_no_node_of_the_part_leaves_it_pooled()
    {
        // The control for the test above: the SAME fixture and a timeline that really is read, naming a
        // node this outfit does not carry. So it is the tier's name matching, and not the mere presence
        // of a timeline resolver, that decided the demotion.
        var env = WithTimelineHides(MakeSkinnedEnv(poolMate: true, mateTier: true),
            "c_vesna01_nosuchnode_lod1");
        var p = NewProject("TimelineNoMatch");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.False(string.IsNullOrEmpty(r.OutDir));
    }

    [Fact]
    public void A_replace_on_a_wardrobe_twin_acts_while_its_option_is_sighted()
    {
        // Nothing bound at the shared draw tells the two options apart, but they are worn one at a time
        // and each marries a companion mesh of its own. The companions' sections write the verdict, and
        // the swap's own section acts on it without probing a slot.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new()));
        var p = NewProject("WardrobeWitness");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string shared = SkinnedIb("sd1.bundle", "c_vesna01_dress1_lod0");
        Assert.Equal(SkinnedIb("sd2.bundle", "c_vesna01_dress2_lod0"), shared);
        string v = $"zz_tw_{shared}";
        // each option's companion mints a section of its own and writes that option's ordinal
        Assert.Contains($"[TextureOverride_TwinWit_{SkinnedIb("sb1.bundle", "c_vesna01_belt1_lod0")}]\n"
            + $"hash = {SkinnedIb("sb1.bundle", "c_vesna01_belt1_lod0")}\nmatch_priority = 0\n${v} = 1\n", ini);
        Assert.Contains($"[TextureOverride_TwinWit_{SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0")}]\n"
            + $"hash = {SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0")}\nmatch_priority = 0\n${v} = 2\n", ini);
        // the guarded section reads the verdict and probes no slot for it
        string cap = SectionOn(ini, shared);
        Assert.DoesNotContain("ps-t0\n", cap);
        Assert.StartsWith($"[TextureOverride_Cap_vesna_dress1]\nhash = {shared}\nmatch_priority = 0\nif ${v} == 1\n"
            + "Resource_vesna_dress1_Posed = ref vb0\n", cap);
        Assert.Equal(1, CountOf(cap, "handling = skip"));
        Assert.Contains($"if ${v} == 1\nResource_vesna_dress1_Posed = ref vb0\n"
            + "Resource_vesna_dress1_CB = copy vs-cb1\nhandling = skip\n", cap);
        Assert.EndsWith("run = CommandListDraw_vesna_dress1\nendif", cap);
        // declared once, and never cleared: the option holds between wardrobe changes
        Assert.Contains($"global ${v} = 0\n", ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
        Assert.Contains(r.Diagnostics, d => d.Contains("'dress1' shares a draw signature with 'dress2'")
            && d.Contains("act while its wardrobe option is sighted"));
        // the companions carry sections of this mod's own, so a manager comparing two mods has to see
        // them: another mod overriding either hash fights this one for the draw
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        var hashes = doc.RootElement.GetProperty("override_hashes")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains(SkinnedIb("sb1.bundle", "c_vesna01_belt1_lod0"), hashes);
        Assert.Contains(SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0"), hashes);
    }

    [Fact]
    public void A_withheld_companion_takes_its_CLEAN_tiers_off_the_stand_with_it()
    {
        // The PART-level gate, and the only shape that makes it load-bearing. For a representative tier
        // the per-tier gate below already answers with the part's own marker, so a marked part with no
        // tiers is refused either way. Here belt2 is marked AND carries a lod1 tier that no list names:
        // that clean tier would vouch for option 2 on its own, so the part-level gate is what keeps the
        // whole withheld companion off the stand — and the option unsightable, which fails the route.
        // Pins `if (p.Visibility != None) continue;`; without it the clean tier sights option 2 and the
        // build succeeds.
        //
        // The control is The_same_companion_tier_witnesses_when_no_list_names_it: the same tiered fixture
        // with belt2 unmarked builds and mints sections for both of its draws.
        var env = WithWardrobeScheme(MakeSkinnedEnv(
            wardrobe: new() { WithheldCompanion2 = true, Companion2Tier = true }));
        var p = NewProject("WardrobeWithheldWitness");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_dress1_lod0' and 'dress2' share one draw signature", ex.Message);
        Assert.Contains("can't act on one without hitting the other", ex.Message);
    }

    [Fact]
    public void A_withheld_TIER_of_a_companion_mints_no_witness_section_while_its_lod0_still_does()
    {
        // PER TIER inside the witness route: belt2's part is clean and its lod0 vouches for option 2 as
        // usual, while the lod1 tier a dorm list names is struck from that option's witness list on its
        // own. Pins the per-tier `if (TierVisibility(p, name) != None) continue;` and the sibling arm of
        // the TierVisibility helper it calls — the part-level gate above cannot stand in for either,
        // since the part carries no marker at all here.
        var env = WithWardrobeScheme(MakeSkinnedEnv(
            wardrobe: new() { Companion2Tier = true, WithheldCompanion2Tier = true }));
        var p = NewProject("WardrobeWithheldTier");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string lod0 = SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0");
        string tier = SkinnedIb("t_sb2.bundle", "c_vesna01_belt2_lod1");
        Assert.NotEqual(lod0, tier);                            // the tier really is its own draw
        Assert.Contains($"[TextureOverride_TwinWit_{lod0}]", ini);
        Assert.DoesNotContain($"[TextureOverride_TwinWit_{tier}]", ini);
    }

    [Fact]
    public void The_same_companion_tier_witnesses_when_no_list_names_it()
    {
        // The control for the test above: the identical fixture with the tier unmarked, so the marker
        // alone decided the exclusion — and it shows the tier minting a section of its own.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { Companion2Tier = true }));
        var p = NewProject("WardrobeCleanTier");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"[TextureOverride_TwinWit_{SkinnedIb("t_sb2.bundle", "c_vesna01_belt2_lod1")}]", ini);
    }

    [Fact]
    public void A_wardrobe_twin_with_no_companions_refuses_as_before()
    {
        // Neither option marries a mesh of its own, so nothing on screen says which one drew. The
        // refusal the build always gave stands.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { Companion1 = false, Companion2 = false }));
        var p = NewProject("WardrobeNoWitness");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_dress1_lod0' and 'dress2' share one draw signature", ex.Message);
        Assert.Contains("can't act on one without hitting the other", ex.Message);
    }

    [Fact]
    public void A_wardrobe_twin_whose_sibling_option_has_no_companion_refuses()
    {
        // The targeted option can be sighted, its sibling cannot. A verdict nothing contradicts would
        // stand from the frame the target last drew, so the swap would keep acting after the player
        // changed into the other option.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { Companion2 = false }));
        var p = NewProject("WardrobeHalfWitness");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_dress1_lod0' and 'dress2' share one draw signature", ex.Message);
    }

    [Fact]
    public void A_wardrobe_twin_whose_sibling_option_is_sighted_only_by_a_shadow_off_tier_refuses()
    {
        // The sibling option HAS a companion, readable and signature-unique — and its renderer is outside
        // the shadow pass, so the game stops drawing it the moment it leaves the camera and sighting
        // nothing at it proves nothing about which option is worn. The tier is struck from that option's
        // witness list, which leaves the option unsightable and fails the whole route, exactly as dropping
        // the companion outright does. Pins ModBuilder's per-tier `if (!TierCastsShadows(p, name)) continue;`
        // in WardrobeWitnesses — without it belt2 is trusted and the build succeeds.
        //
        // The control is A_replace_on_a_wardrobe_twin_acts_while_its_option_is_sighted: the identical
        // fixture with belt2 casting builds, and mints belt2's witness section on that option's ordinal.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { ShadowOffCompanion2 = true }));
        var p = NewProject("WardrobeShadowOffWitness");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_dress1_lod0' and 'dress2' share one draw signature", ex.Message);
        Assert.Contains("can't act on one without hitting the other", ex.Message);
    }

    [Fact]
    public void A_tier_twin_outside_the_wardrobe_scheme_takes_no_witness_route()
    {
        // The witness route answers wardrobe OPTIONS — meshes worn one at a time. These two parts belong
        // to no slot the scheme lists, so nothing on screen proves which of them drew and the refusal
        // stands.
        var env = WithWardrobeScheme(MakeSkinnedEnv(poolMate: true, mateTierTwin: true));
        var p = NewProject("TierTwinNoWardrobe");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("share one draw signature", ex.Message);
        Assert.Contains("mate", ex.Message);
    }

    [Fact]
    public void A_pooled_companion_writes_its_verdict_inside_its_own_capture_section()
    {
        // The donor rides the companion too, so its ib already owns a capture section. A second override
        // on that hash would be dropped at parse time, so the sighting rides the capture — ahead of
        // everything else in it, as a latch sighting does.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new()));
        var p = NewProject("WardrobePooledWitness");
        WriteDonorGlb(bones: DressBones.Concat(Belt1Bones).ToArray());
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string v = $"zz_tw_{SkinnedIb("sd1.bundle", "c_vesna01_dress1_lod0")}";
        string belt1 = SkinnedIb("sb1.bundle", "c_vesna01_belt1_lod0");
        Assert.Contains($"[TextureOverride_Cap_vesna_belt1]\nhash = {belt1}\nmatch_priority = 0\n${v} = 1\n"
            + "Resource_vesna_belt1_Posed = ref vb0\n", ini);
        Assert.DoesNotContain($"[TextureOverride_TwinWit_{belt1}]", ini);
        // the unpooled option's companion still mints one
        Assert.Contains($"[TextureOverride_TwinWit_{SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0")}]", ini);
    }

    [Fact]
    public void A_hide_on_a_wardrobe_twin_skips_while_its_option_is_sighted()
    {
        // A hide keyed on the shared signature would blank the other option too. The companions' sections
        // say which option is worn, and the skip waits on that.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new()));
        var p = NewProject("WardrobeHide");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_dress1_lod0", true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string shared = SkinnedIb("sd1.bundle", "c_vesna01_dress1_lod0");
        string v = $"zz_tw_{shared}";
        Assert.Contains($"[TextureOverride_TwinWit_{SkinnedIb("sb1.bundle", "c_vesna01_belt1_lod0")}]\n"
            + $"hash = {SkinnedIb("sb1.bundle", "c_vesna01_belt1_lod0")}\nmatch_priority = 0\n${v} = 1\n", ini);
        Assert.Contains($"[TextureOverride_TwinWit_{SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0")}]\n"
            + $"hash = {SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0")}\nmatch_priority = 0\n${v} = 2\n", ini);
        string hide = SectionOn(ini, shared);
        Assert.Equal($"[TextureOverride_Hide_0]\nhash = {shared}\nmatch_priority = 0\nif ${v} == 1\nhandling = skip\nendif", hide);
        // no probe: an overlay build whose verdicts all arrive from sightings reads no slot at all
        Assert.DoesNotContain("zz_t = ps-t", ini);
        Assert.DoesNotContain("global $zz_t = 0", ini);
        Assert.Contains($"global ${v} = 0\n", ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
    }

    [Fact]
    public void A_hidden_companion_writes_its_verdict_inside_its_hide_section()
    {
        // The companion of the option NOT being replaced is hidden by an edit of its own, so its ib
        // already owns a skip section. The verdict rides that section, ahead of the skip and outside
        // every gate: a sighting silenced while a key was off would leave the verdict stale.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new()));
        var p = NewProject("WardrobeHiddenWitness");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_belt2_lod0", true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string v = $"zz_tw_{SkinnedIb("sd1.bundle", "c_vesna01_dress1_lod0")}";
        string belt2 = SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0");
        Assert.Contains($"[TextureOverride_Hide_0]\nhash = {belt2}\nmatch_priority = 0\n${v} = 2\nhandling = skip\n", ini);
        Assert.DoesNotContain($"[TextureOverride_TwinWit_{belt2}]", ini);
    }

    [Fact]
    public void A_companion_that_also_witnesses_a_latch_carries_both_lines_in_one_section()
    {
        // The presence latch mints a section on the companion's ib, and the wardrobe verdict wants one
        // on the same hash. Two overrides on one hash leave the second dropped at parse time, so both
        // lines ride the one section.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new()));
        string shared = SkinnedIb("sd1.bundle", "c_vesna01_dress1_lod0");
        string belt1 = SkinnedIb("sb1.bundle", "c_vesna01_belt1_lod0");
        env = env with
        {
            Sharing = Measured(new Dictionary<string, int[]> { [shared] = new[] { 0, 1 } },
                new Dictionary<int, string[]> { [0] = new[] { belt1 } }),
        };
        var p = NewProject("WardrobeLatchWitness");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.Contains($"[TextureOverride_Witness_vesnassr01_0]\nhash = {belt1}\nmatch_priority = 0\n"
            + $"$zz_seen_vesnassr01 = 1\n$zz_tw_{shared} = 1\n", ini);
        Assert.DoesNotContain($"[TextureOverride_TwinWit_{belt1}]", ini);
    }

    [Fact]
    public void A_companion_both_options_wear_witnesses_neither_of_them()
    {
        // One accessory, worn whichever option is on: its mesh is byte-identical under both names, so it
        // draws on ONE signature and its section would write a verdict per option with the last one
        // standing. It proves nothing, and with nothing else to sight the route has no answer.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { TwinCompanions = true }));
        var p = NewProject("WardrobeSharedCompanion");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_dress1_lod0' and 'dress2' share one draw signature", ex.Message);
        Assert.Contains("can't act on one without hitting the other", ex.Message);
    }

    [Fact]
    public void A_companion_both_options_wear_is_struck_and_the_private_ones_still_witness()
    {
        // Each option is worn with an accessory of its own AND with the one both share. The shared mesh
        // is struck — its single section can only carry one option's verdict — and the private ones
        // still say which option is on, so the route holds.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { SharedExtra = true }));
        var p = NewProject("WardrobeSharedExtra");
        WriteDonorGlb(bones: DressBones);
        AddReplaceTarget(p, "c_vesna01_dress1_lod0");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string v = $"zz_tw_{SkinnedIb("sd1.bundle", "c_vesna01_dress1_lod0")}";
        Assert.Contains($"hash = {SkinnedIb("sb1.bundle", "c_vesna01_belt1_lod0")}\nmatch_priority = 0\n${v} = 1\n", ini);
        Assert.Contains($"hash = {SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0")}\nmatch_priority = 0\n${v} = 2\n", ini);
        // one hash under two names, and no line on it at all
        string scarf = SkinnedIb("ss1.bundle", "c_vesna01_scarf1_lod0");
        Assert.Equal(SkinnedIb("ss2.bundle", "c_vesna01_scarf2_lod0"), scarf);
        Assert.DoesNotContain(scarf, ini);
        AssertStickyVerdictsSurviveTheFrame(ini);
    }

    [Fact]
    public void A_companion_this_build_refuses_to_touch_is_no_witness_rather_than_no_build()
    {
        // One of the first option's companions fails the content policy, so nothing about it can be
        // read and nothing may be emitted on it. It drops out of the candidates the way an unreadable
        // part does, and the companion beside it still witnesses the option.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { BlockedCompanion = true }));
        var p = NewProject("WardrobeBlockedCompanion");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_dress1_lod0", true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string v = $"zz_tw_{SkinnedIb("sd1.bundle", "c_vesna01_dress1_lod0")}";
        Assert.Contains($"[TextureOverride_TwinWit_{SkinnedIb("sb1.bundle", "c_vesna01_belt1_lod0")}]", ini);
        Assert.Contains($"[TextureOverride_TwinWit_{SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0")}]", ini);
        Assert.Contains($"global ${v} = 0\n", ini);
        Assert.DoesNotContain("Helena", ini);
    }

    [Fact]
    public void A_signature_the_two_routes_split_refuses_rather_than_dropping_a_claimant()
    {
        // Three siblings on one draw signature, two of them byte-identical. The pair is told about a
        // single mate and binds a base color that mate does not, so its draws answer for themselves;
        // the lone sibling is told about both and shares a color with one, so only the wardrobe answers
        // for it. One variable cannot hold both meanings, and dropping either claimant would leave its
        // hide acting on the other's draws.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { Mixed = true }));
        var p = NewProject("WardrobeMixedRoutes");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_dress1_lod0", true);
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_dress2_lod0", true);

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("'dress1' and 'dress2' share one draw signature", ex.Message);
        Assert.Contains("can't act on one without hitting the other", ex.Message);
    }

    [Fact]
    public void Either_route_alone_on_the_split_signature_builds_as_it_always_did()
    {
        // The same three siblings, one hide at a time: the pair's hide is answered by the textures at
        // its own draw, the lone sibling's by the wardrobe. Neither is changed by the refusal above.
        var p1 = NewProject("WardrobeMixedTextureOnly");
        p1.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_dress1_lod0", true);
        var r1 = ModBuilder.Build(p1,
            WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { Mixed = true })), _out, zip: false);

        string shared = SkinnedIb("sd1.bundle", "c_vesna01_dress1_lod0");
        string ini1 = File.ReadAllText(Path.Combine(r1.OutDir, "mod.ini"));
        string hide1 = SectionOn(ini1, shared);
        // the slot sweep the texture route reads, then the skip on the verdict it wrote
        Assert.StartsWith($"[TextureOverride_Hide_0]\nhash = {shared}\nmatch_priority = 0\n$zz_t = ps-t", hide1);
        Assert.EndsWith($"if $zz_tw_{shared} == 1\nhandling = skip\nendif", hide1);
        Assert.DoesNotContain("TextureOverride_TwinWit_", ini1);

        var p2 = NewProject("WardrobeMixedWitnessOnly");
        p2.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_dress2_lod0", true);
        var r2 = ModBuilder.Build(p2,
            WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { Mixed = true })), _out, zip: false);

        string ini2 = File.ReadAllText(Path.Combine(r2.OutDir, "mod.ini"));
        Assert.Equal($"[TextureOverride_Hide_0]\nhash = {shared}\nmatch_priority = 0\nif $zz_tw_{shared} == 2\n"
            + "handling = skip\nendif", SectionOn(ini2, shared));
        Assert.Contains($"hash = {SkinnedIb("sb1.bundle", "c_vesna01_belt1_lod0")}\nmatch_priority = 0\n"
            + $"$zz_tw_{shared} = 1\n", ini2);
        Assert.Contains($"hash = {SkinnedIb("sb2.bundle", "c_vesna01_belt2_lod0")}\nmatch_priority = 0\n"
            + $"$zz_tw_{shared} = 2\n", ini2);
        Assert.DoesNotContain("zz_t = ps-t", ini2);
    }

    [Fact]
    public void A_repainted_tag_that_other_wardrobes_meshes_bind_refuses()
    {
        // The lone sibling is identified by a base color the always-on body also binds. Repainted,
        // the bind-time write would fire at the body's draws under EVERY option, so the verdict
        // would claim the lone sibling's option regardless of what is worn.
        var env = WithWardrobeScheme(MakeSkinnedEnv(wardrobe: new() { Mixed = true }));
        var p = NewProject("WardrobeTagRepaintShared");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_dress1_lod0", true);
        AddEditedTexture(p);   // repaints tex_body_d — dress2's identifying color, and the body's

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("also binds the base color that identifies 'dress2'", ex.Message);
        Assert.Contains("so this build can't ship", ex.Message);
    }

    [Fact]
    public void A_repainted_tag_on_same_frame_twins_refuses()
    {
        // No wardrobe gates these siblings: both draw every frame, so a bind-time write proves
        // nothing, and the repaint has hidden the color the draw probe needed. The edit rides the
        // un-acted-on body, so it ships as a plain retexture rather than being adopted or dropped.
        var env = MakeSkinnedEnv(twinPart: true, twinPartPosSeed: 21, twinPartOwnAlbedo: true);
        var p = NewProject("TwinTagRepaint");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body2_lod0", true);
        AddEditedTexture(p);   // repaints tex_body_d — the body's identifying color at the shared draw

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("'body' and 'body2' share one draw signature, told apart by 'body's base color",
            ex.Message);
        Assert.Contains("so this build can't ship", ex.Message);
    }

    // ---- parts the recoverable-skin rule holds back ----------------------------------------------

    [Fact]
    public void A_one_influence_target_builds_through_palette_recovery()
    {
        // One stored influence is a full skin, not a missing one: the game poses the draw by that bone, so
        // the target goes through capture and recovery like any other pooled part rather than taking a
        // static swap that would freeze it at its bind pose.
        var env = MakeSkinnedEnv(bodySkinWidth: 1);
        var p = NewProject("SwapNarrow");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        AssertEveryReferencedFileShips(ini, r.OutDir);
        Assert.Contains($"[TextureOverride_Cap_vesna_body]\nhash = {SkinnedIb("s0.bundle", "c_vesna01_body_lod0")}\nmatch_priority = 0\n", ini);
        Assert.Contains("CustomShaderRecover_vesna_body_vesna_body", ini);
        Assert.Contains("CustomShaderConvert_vesna_body", ini);
        Assert.DoesNotContain("Rigid", ini);
        Assert.Empty(Directory.GetFiles(r.OutDir, "rigid_*"));
    }

    [Fact]
    public void A_two_influence_target_builds_through_palette_recovery()
    {
        // The stored pair is the mesh's whole skin — its draws are posed by exactly those two influences —
        // so it widens where it is read and the Replace derives and builds as a four-wide target does.
        var env = MakeSkinnedEnv(bodySkinWidth: 2);
        var p = NewProject("SwapPair");
        WriteDonorGlb();
        AddReplaceTarget(p);
        var lines = new List<string>();

        var r = ModBuilder.Build(p, env, _out, lines.Add, zip: false);

        // pooled like any measured part, not held back and not narrow-restricted
        Assert.Contains("pool (vesna_body): c_vesna01_body_lod0 (anchor c_vesna01_body_lod0)", lines);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        AssertEveryReferencedFileShips(ini, r.OutDir);
        Assert.Contains("CustomShaderRecover_vesna_body_vesna_body", ini);
        Assert.Contains("CustomShaderConvert_vesna_body", ini);
        Assert.DoesNotContain("Rigid", ini);
        Assert.Empty(Directory.GetFiles(r.OutDir, "rigid_*"));
    }

    [Fact]
    public void A_one_influence_part_stays_out_of_another_parts_pool()
    {
        // It rides the shared bone at weight 1 on every vertex, so pooling it would hand it that bone's
        // palette row — recovered from a draw that only fires while the accessory is on screen.
        var env = MakeSkinnedEnv(narrowAccessory: true);
        var p = NewProject("SwapNarrowMate");
        WriteDonorGlb();
        AddReplaceTarget(p);
        var lines = new List<string>();

        var r = ModBuilder.Build(p, env, _out, lines.Add, zip: false);

        Assert.Contains("pool (vesna_body): c_vesna01_body_lod0 (anchor c_vesna01_body_lod0)", lines);
        Assert.Contains(r.Diagnostics, d => d == "pool (vesna_body): left out: 'c_vesna01_acc_lod0' · "
            + "it stores one influence per vertex, so only a Replace on that part itself pools it");
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.DoesNotContain("vesna_acc", ini);
        // every union bone recovered from the body's own draw
        var owner = File.ReadAllBytes(Path.Combine(r.OutDir, "owner_part_vesna_body.buf"));
        Assert.All(Enumerable.Range(0, owner.Length / 4), i => Assert.Equal(0u, BitConverter.ToUInt32(owner, i * 4)));
    }

    [Fact]
    public void A_one_influence_part_still_pools_for_a_replace_on_itself()
    {
        // The rule is about whose pool it may join, not whether it can be replaced: its own Replace derives
        // and builds exactly as any other pooled target, and the tie for the anchor goes to it.
        var env = MakeSkinnedEnv(narrowAccessory: true);
        var p = NewProject("SwapNarrowSelf");
        WriteDonorGlb(bones: AccessoryBones);
        AddReplaceTarget(p, "c_vesna01_acc_lod0");
        var lines = new List<string>();

        var r = ModBuilder.Build(p, env, _out, lines.Add, zip: false);

        // the body tables the shared bone too, so both pool — and the tie lands on the replaced part
        Assert.Contains("pool (vesna_acc): c_vesna01_body_lod0, c_vesna01_acc_lod0 (anchor c_vesna01_acc_lod0)",
            lines);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        AssertEveryReferencedFileShips(ini, r.OutDir);
        Assert.Contains("CustomShaderConvert_vesna_acc", ini);
        Assert.DoesNotContain("Rigid", ini);
    }

    [Theory]
    [InlineData(21, 4, false, "it carries 21 blend shapes (its posed vertices aren't pure LBS)")]
    [InlineData(0, 1, true, "it carries a skin stream recovery can't read")]
    [InlineData(0, 2, true, "it carries a skin stream recovery can't read")]
    [InlineData(0, 4, true, "it carries a skin stream recovery can't read")]
    public void A_replace_whose_target_cant_feed_recovery_refuses_over_that_mesh(
        int blendShapes, int skinWidth, bool sharedSkinStream, string why)
    {
        // The target's own mesh failing the rule says nothing about how the donor was weighted, so the
        // refusal names the mesh and the rule rather than sending the author back to re-weight. Every
        // influence count 1–4 is one recovery accepts, so what reaches this is a count it does accept
        // spelled in a shape it can't read — and the line says so.
        var env = MakeSkinnedEnv(bodyBlendShapes: blendShapes, bodySkinWidth: skinWidth,
            bodySharedSkinStream: sharedSkinStream);
        var p = NewProject("SwapBlocked");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Equal($"'c_vesna01_body_lod0' can't be replaced: {why}. Drop this Replace", ex.Message);
        Assert.DoesNotContain("re-weight", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("armature", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pool", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_orphan_bone_a_held_back_part_owns_names_that_part_and_why()
    {
        // The build itself removed the part those bones belong to, so the armature reading would blame the
        // author for a hole the build made.
        var env = MakeSkinnedEnv(facePart: true);
        var p = NewProject("SwapAcross");
        WriteDonorGlb(bones: BodyBones.Concat(FaceBones).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("2 bone(s) owned by no pooled part of this outfit", ex.Message);
        Assert.Contains("Left out of the pool: 'c_vesna01_face_lod0' · it carries 21 blend shapes "
            + "(its posed vertices aren't pure LBS)", ex.Message);
        Assert.DoesNotContain("different armature", ex.Message);
    }

    [Fact]
    public void A_held_back_part_of_unknown_bones_is_named_over_any_orphan()
    {
        // Nothing read off the part, so nothing rules it out as the owner. Naming it beats an armature
        // reading that would be a guess over the same missing evidence.
        var env = MakeSkinnedEnv(ghostPart: true);
        var p = NewProject("SwapGhost");
        WriteDonorGlb(bones: BodyBones.Append(0x00000909u).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("Left out of the pool: 'c_vesna01_ghost_lod0' · bundle 'bundleGhost'", ex.Message);
        Assert.DoesNotContain("different armature", ex.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_orphan_bone_no_held_back_part_owns_still_reads_as_a_foreign_armature(bool facePart)
    {
        // A held-back part only re-diagnoses the orphans it could actually own — otherwise a roster that
        // always holds one back would talk every foreign-armature donor out of the one advice that fixes it.
        var env = MakeSkinnedEnv(facePart: facePart);
        var p = NewProject("SwapForeign");
        WriteDonorGlb(bones: BodyBones.Append(0x00000909u).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Equal("the donor rides 1 bone(s) owned by no part of this outfit (first: 0x00000909). "
            + "It was weighted against a different armature; re-export this outfit's reference and re-weight",
            ex.Message);
    }

    [Fact]
    public void A_donor_bone_the_outfit_only_tables_refuses_the_build()
    {
        // The whole route, not the rule in isolation: the probe measures which bones the body's lod0
        // actually poses, and a donor riding one it merely LISTS gets the refusal rather than a swap that
        // would tie those vertices to a bone the outfit never moves.
        var env = MakeSkinnedEnv(bodyTabledOnly: new uint[] { 0x00000107 });
        var p = NewProject("SwapTabled");
        WriteDonorGlb(bones: BodyBones.Append(0x00000107u).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Equal("the donor rides 1 bone(s) that no pooled part of this outfit poses "
            + "(first: 0x00000107, carried at zero weight by 'c_vesna01_body_lod0'). "
            + "Re-weight the donor onto the bones this outfit moves", ex.Message);
    }

    [Fact]
    public void A_part_whose_weights_cant_be_read_is_held_back_with_the_bones_it_owns()
    {
        // The weight read fails past the skin rule, and the bone table was already in hand. Losing it
        // would make the part a suspect in every orphan refusal on this subject and put the read's own
        // .NET text where the diagnosis belongs.
        var env = MakeSkinnedEnv(unreadableWeightsPart: true);
        var p = NewProject("SwapUnread");
        WriteDonorGlb(bones: BodyBones.Append(0x00000909u).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Equal("the donor rides 1 bone(s) owned by no part of this outfit (first: 0x00000909). "
            + "It was weighted against a different armature; re-export this outfit's reference and re-weight",
            ex.Message);
    }

    [Fact]
    public void A_part_whose_weights_cant_be_read_never_reaches_the_pool()
    {
        // Offered without a measured posed set, the part would answer the posed gate from its TABLE —
        // exactly the answer that gate exists to refuse. It stays out, and the build log carries the read.
        var env = MakeSkinnedEnv(unreadableWeightsPart: true);
        var p = NewProject("SwapUnreadPool");
        WriteDonorGlb(bones: BodyBones.Concat(UnreadBones).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("Left out of the pool: 'c_vesna01_unread_lod0' · its skin weights can't be read",
            ex.Message);
        Assert.DoesNotContain("poses", ex.Message);
    }

    [Fact]
    public void A_held_back_part_the_donor_never_rides_leaves_the_build_alone()
    {
        var env = MakeSkinnedEnv(facePart: true);
        var p = NewProject("SwapPastFace");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.Contains(r.Diagnostics, d =>
            d.Contains("part 'face' excluded from pool derivation") && d.Contains("21 blend shapes"));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.DoesNotContain("vesna_face", ini);
        AssertNoDuplicateSections(ini);
    }

    [Fact]
    public void A_scoped_retexture_on_a_probed_stock_map_keeps_the_donor_binds()
    {
        // The scoped retexture's tag outranks the kind tag on the same stock hash, so the slot tag
        // steps aside and the draw probe accepts the retexture's derived value as the same answer.
        var env = MakeSkinnedEnv(clothWearer: true);
        env = env with
        {
            Sharing = SharingIndex.FromMeasurements("12345",
                new[]
                {
                    new SharingIndex.Wearer("Vesna", null, "VesnaSSR01", null),
                    new SharingIndex.Wearer("Karst", null, "KarstDorm", null),
                },
                new Dictionary<string, int[]> { [_stockTexHash] = new[] { 0, 1 } },
                new Dictionary<string, int[]>(),
                new Dictionary<int, string[]>()),
        };
        var p = NewProject("SwapScoped");
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(10, 20, 30, 255)))
            img.SaveAsPng(Path.Combine(_proj, "donor_s0_base.png"));
        AddReplaceTarget(p, textures: new List<SubmeshTextures> { new() { Submesh = 0, Albedo = "donor_s0_base.png" } });
        AddEditedTexture(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        int tag = MigotoEmitter.RetexTag(_stockTexHash);
        Assert.DoesNotContain($"[TextureOverride_SlotTag_{_stockTexHash}]", ini);
        Assert.Contains($"[TextureOverride_RetexTag_{_stockTexHash}]", ini);
        Assert.Contains($"if $zz_t == {tag}\n$zz_slot_a = 0\nendif\n", ini);
        Assert.Contains("if $zz_slot_a == 0\nps-t0 = Resource_Tex0\nendif\n", ini);
        // the inheriting submesh keeps drawing the pre-retexture image at the replacement — disclosed
        Assert.Contains(r.Infos, i => i.Contains("inherits a stock map"));
        AssertNoDuplicateSections(ini);
    }

    [Fact]
    public void A_baked_rest_replace_compiles_the_donor_back_into_bind_space()
    {
        // A donor authored over a −90°X uprighting must land byte-identical to the same donor
        // authored with no bake: the un-bake is the exact inverse of the workspace bake.
        var g = new System.Numerics.Matrix4x4(
            1, 0, 0, 0,
            0, 0, -1, 0,
            0, 1, 0, 0,
            0, 0, 0, 1);
        var p1 = NewProject("SwapPlain");
        WriteDonorGlb();
        AddReplaceTarget(p1);
        var r1 = ModBuilder.Build(p1, MakeSkinnedEnv(), _out, zip: false);
        byte[] plain = File.ReadAllBytes(Directory.GetFiles(r1.OutDir, "combined_bind_*.buf").Single());

        var p2 = NewProject("SwapBaked");
        WriteDonorGlb(rotate: g);
        AddReplaceTarget(p2, bakedRest: RestBake.ToList(g));
        var r2 = ModBuilder.Build(p2, MakeSkinnedEnv(), _out, zip: false);
        byte[] unbaked = File.ReadAllBytes(Directory.GetFiles(r2.OutDir, "combined_bind_*.buf").Single());

        Assert.Equal(plain, unbaked);
    }

    [Fact]
    public void A_rest_pose_that_is_not_an_axis_aligned_rotation_warns_and_compiles_without_it()
    {
        // Un-baking inverts by transpose, which is the inverse only of the rotations the bake records. A
        // hand-edited project carrying anything else would otherwise ship skewed geometry under a build
        // that reported success.
        var p = NewProject("SwapSheared");
        WriteDonorGlb();
        AddReplaceTarget(p, bakedRest: new List<float>
        {
            1, 0, 0, 0,
            0.3f, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        });

        var r = ModBuilder.Build(p, MakeSkinnedEnv(), _out, zip: false);

        Assert.Contains(r.Warnings, w => w.Contains("is not an axis-aligned rotation")
            && w.Contains("c_vesna01_body_lod0"));
        // and it landed where a target with no bake at all lands
        var plainProject = NewProject("SwapNoRest");
        WriteDonorGlb();
        AddReplaceTarget(plainProject);
        var plain = ModBuilder.Build(plainProject, MakeSkinnedEnv(), _out, zip: false);
        Assert.Equal(File.ReadAllBytes(Directory.GetFiles(plain.OutDir, "combined_bind_*.buf").Single()),
                     File.ReadAllBytes(Directory.GetFiles(r.OutDir, "combined_bind_*.buf").Single()));
    }

    // ---- one hash, one TextureOverride ------------------------------------------------------------

    /// <summary>The ib hash of a mesh in one of the skinned world's bundles.</summary>
    private string SkinnedIb(string bundleFile, string mesh) =>
        BufferHash.Compute(File.ReadAllBytes(Path.Combine(_root, bundleFile)), mesh).Ib.ToString("x8");

    private string SkinnedVb1(string bundleFile, string mesh) =>
        BufferHash.Compute(File.ReadAllBytes(Path.Combine(_root, bundleFile)), mesh).Vb1!.Value.ToString("x8");

    private static SharingIndex Measured(Dictionary<string, int[]> mesh, Dictionary<int, string[]> witnesses,
        Dictionary<string, int[]>? tex = null) =>
        SharingIndex.FromMeasurements("12345",
            new[]
            {
                new SharingIndex.Wearer("Vesna", null, "VesnaSSR01", null),
                new SharingIndex.Wearer("Karst", null, "KarstDorm", null),
            },
            tex ?? new Dictionary<string, int[]>(), mesh, witnesses);

    [Fact]
    public void A_witness_that_is_the_pipelines_own_mesh_records_inside_that_capture_section()
    {
        // A latched Replace's own pool routinely contains the private mesh that witnesses the outfit. Two
        // sections on that ib would leave one of them dropped at parse time, and which one is not
        // something the emission decides — so the sighting rides the capture.
        var env = MakeSkinnedEnv();
        string lod0 = SkinnedIb("s0.bundle", "c_vesna01_body_lod0");
        string lod1 = SkinnedIb("s1.bundle", "c_vesna01_body_lod1");
        env = env with
        {
            Sharing = Measured(new Dictionary<string, int[]> { [lod0] = new[] { 0, 1 } },
                new Dictionary<int, string[]> { [0] = new[] { lod1 } }),
        };
        var p = NewProject("SwapWitness");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.DoesNotContain("[TextureOverride_Witness_", ini);
        Assert.Contains($"[TextureOverride_Cap_vesna_body_lod1]\nhash = {lod1}\nmatch_priority = 0\n"
            + "Resource_vesna_body_lod1_Posed = ref vb0\n$zz_seen_vesnassr01 = 1\n", ini);
        // and the latch still gates the suppression it was built for
        Assert.Contains("if $zz_gate_vesnassr01 == 1\nhandling = skip\n", ini);
    }

    [Fact]
    public void A_scoped_retexture_on_a_pooled_leave_binds_inside_that_parts_capture_section()
    {
        // The retextured part rides the Replace's pool as a Leave, so its ib already owns a capture
        // section. The scoped bind block is self-contained (save, probe, bind, restore), so it runs
        // there rather than minting a second override on the same hash.
        var env = MakeSkinnedEnv(poolMate: true);
        string mate = SkinnedIb("sm.bundle", "c_vesna01_mate_lod0");
        env = env with
        {
            Sharing = Measured(new Dictionary<string, int[]>(), new Dictionary<int, string[]>(),
                tex: new Dictionary<string, int[]> { [_stockTexHash] = new[] { 0, 1 } }),
        };
        var p = NewProject("SwapLeaveRetex");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);
        AddEditedTexture(p, user: "c_vesna01_mate_lod0");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.DoesNotContain("[TextureOverride_RetexScope_", ini);
        // the block landed in the capture section that owns the mate's hash, after its chain lines
        string cap = ini[ini.IndexOf($"hash = {mate}", StringComparison.Ordinal)..];
        cap = cap[..cap.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Contains("Resource_RtxSave0 = ref ps-t0\n", cap);
        Assert.Contains($"if $zz_rt == {MigotoEmitter.RetexTag(_stockTexHash)}\n$zz_rslot = 0\nendif\n", cap);
        Assert.Contains("if $zz_rslot == 0\nps-t0 = Resource_Rtx0\nendif\n", cap);
        Assert.Contains("post ps-t0 = Resource_RtxSave0\n", cap);
        // the tag section the probe reads back still stands on its own
        Assert.Contains($"[TextureOverride_RetexTag_{_stockTexHash}]", ini);
    }

    [Fact]
    public void An_overlay_build_reaching_one_hide_hash_twice_emits_one_section()
    {
        // The overlay path is the pooled path's twin: two hides on one hash would leave the second
        // section dropped at parse time, taking its own toggle key with it.
        string outDir = Path.Combine(_root, "overlay-dupe");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { "aaaa1111", "bbbb2222", "aaaa1111" });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.Contains("[TextureOverride_Hide_0]\nhash = aaaa1111\nmatch_priority = 0\n", ini);
        Assert.Contains("[TextureOverride_Hide_1]\nhash = bbbb2222\nmatch_priority = 0\n", ini);
        Assert.DoesNotContain("[TextureOverride_Hide_2]", ini);
    }

    [Fact]
    public void An_overlay_witness_that_is_also_a_hide_records_inside_the_hide_section()
    {
        // A hidden mesh can be exactly the private mesh that witnesses its outfit. One hash owns one
        // section, so the sighting rides the hide; the latch's other witness still mints its own.
        string outDir = Path.Combine(_root, "overlay-witness");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            hideHashes: new[] { "aaaa1111" },
            latches: new[] { new WitnessLatch("vesnassr01", new[] { "aaaa1111", "cccc3333" }) });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.DoesNotContain("[TextureOverride_Witness_vesnassr01_0]", ini);
        Assert.Contains("[TextureOverride_Witness_vesnassr01_1]\nhash = cccc3333\nmatch_priority = 0\n"
            + "$zz_seen_vesnassr01 = 1\n", ini);
        // ungated, and ahead of the skip: a latch reading its own witness must record every frame
        Assert.Contains("[TextureOverride_Hide_0]\nhash = aaaa1111\nmatch_priority = 0\n$zz_seen_vesnassr01 = 1\n"
            + "handling = skip\n", ini);
    }

    /// <summary>Two subjects whose pools' SECOND parts draw one index buffer. Their meshes differ, so one
    /// capture section cannot serve both: each pipeline's recovery would read whichever wearer drew
    /// last.</summary>
    private BuildEnv MakeTwoSubjectSharedPoolEnv()
    {
        var bytes = new Dictionary<string, byte[]>();
        var addresses = new Dictionary<string, string>();
        void Mesh(string bundleKey, string file, string mesh, int seed, int verts, uint[] bones, string address)
        {
            string path = Path.Combine(_root, file);
            SyntheticBundle.BuildOneSkinnedMesh(path, mesh, Cloud(verts, seed), WrappedTris(verts), bones);
            bytes[bundleKey] = File.ReadAllBytes(path);
            addresses[address] = bundleKey;
        }
        Mesh("vBody", "tv0.bundle", "c_vesna01_body_lod0", 5, 32, BodyBones, "addr_v_body");
        Mesh("kBody", "tk0.bundle", "c_karst01_torso_lod0", 7, 28, ClothBones, "addr_k_torso");
        // same vertex COUNT and so the same wrapped triangle list: two different meshes, one index buffer
        Mesh("vAcc", "tv1.bundle", "c_vesna01_acc_lod0", 13, 16, TwinBones, "addr_v_acc");
        Mesh("kAcc", "tk1.bundle", "c_karst01_acc_lod0", 21, 16, MateBones, "addr_k_acc");

        SubjectPart Part(string token, string mesh, string address) =>
            new(token, mesh, address, new[] { new SubjectMaterial($"m_{token}", 1, $"cab-{token}",
                new List<SubjectMap>()) });
        var vesna = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            Part("body", "c_vesna01_body_lod0", "addr_v_body"),
            Part("acc", "c_vesna01_acc_lod0", "addr_v_acc"),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var karst = new SubjectModel("Karst", "KarstDorm", SubjectSource.Prefab, new[]
        {
            Part("torso", "c_karst01_torso_lod0", "addr_k_torso"),
            Part("acc", "c_karst01_acc_lod0", "addr_k_acc"),
        }, Skeleton: null, Problems: Array.Empty<string>());

        return new BuildEnv(
            (c, s) => c == "Vesna" ? vesna : c == "Karst" ? karst : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0");
    }

    [Fact]
    public void Two_pipelines_pooling_meshes_that_share_an_index_buffer_refuse()
    {
        // The refusal has to span the whole build, not one Replace: the capture sections are merged by
        // hash ACROSS pipelines, so two subjects' pools colliding aliases just as badly as one pool's.
        var env = MakeTwoSubjectSharedPoolEnv();
        var p = NewProject("TwoPools");
        p.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstDorm" });
        WriteDonorGlb("donor_v.glb", BodyBones.Concat(TwinBones).ToArray());
        WriteDonorGlb("donor_k.glb", ClothBones.Concat(MateBones).ToArray());
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "vBody", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01", ReplaceFile = "donor_v.glb",
        });
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "kBody", ObjectName = "c_karst01_torso_lod0",
            SubjectCharacter = "Karst", SubjectOutfit = "KarstDorm", ReplaceFile = "donor_k.glb",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("share one draw signature", ex.Message);
        Assert.Contains("KarstDorm", ex.Message);
        Assert.Contains("VesnaSSR01", ex.Message);
    }

    /// <summary>Two subjects whose pools' second parts have DISTINCT lod0 index buffers but lod1 tiers that
    /// draw one. The capture merge is by hash whatever tier minted the section, so the collision aliases
    /// exactly as a pool part's does.</summary>
    private BuildEnv MakeTwoSubjectSharedTierEnv()
    {
        var bytes = new Dictionary<string, byte[]>();
        var addresses = new Dictionary<string, string>();
        void Mesh(string bundleKey, string file, string mesh, int seed, int verts, uint[] bones, string address)
        {
            string path = Path.Combine(_root, file);
            SyntheticBundle.BuildOneSkinnedMesh(path, mesh, Cloud(verts, seed), WrappedTris(verts), bones);
            bytes[bundleKey] = File.ReadAllBytes(path);
            addresses[address] = bundleKey;
        }
        Mesh("vBody", "sv0.bundle", "c_vesna01_body_lod0", 5, 32, BodyBones, "addr_v_body");
        Mesh("kBody", "sk0.bundle", "c_karst01_torso_lod0", 7, 28, ClothBones, "addr_k_torso");
        // distinct vertex counts, so the two pools' lod0 draws stay apart
        Mesh("vAcc", "sv1.bundle", "c_vesna01_acc_lod0", 13, 16, TwinBones, "addr_v_acc");
        Mesh("kAcc", "sk1.bundle", "c_karst01_acc_lod0", 21, 20, MateBones, "addr_k_acc");
        // the collision rides one tier down: same vertex COUNT, so one wrapped triangle list
        Mesh("vAccL1", "sv2.bundle", "c_vesna01_acc_lod1", 31, 24, TwinBones, "addr_v_acc_l1");
        Mesh("kAccL1", "sk2.bundle", "c_karst01_acc_lod1", 37, 24, MateBones, "addr_k_acc_l1");

        SubjectPart Part(string token, string mesh, string address, RecipeTierSlot[]? tiers = null) =>
            new(token, mesh, address, new[] { new SubjectMaterial($"m_{token}", 1, $"cab-{token}",
                new List<SubjectMap>()) }, SiblingTiers: tiers);
        var vesna = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            Part("body", "c_vesna01_body_lod0", "addr_v_body"),
            Part("acc", "c_vesna01_acc_lod0", "addr_v_acc",
                new[] { new RecipeTierSlot("c_vesna01_acc_lod1", "addr_v_acc_l1") }),
        }, Skeleton: null, Problems: Array.Empty<string>());
        var karst = new SubjectModel("Karst", "KarstDorm", SubjectSource.Prefab, new[]
        {
            Part("torso", "c_karst01_torso_lod0", "addr_k_torso"),
            Part("acc", "c_karst01_acc_lod0", "addr_k_acc",
                new[] { new RecipeTierSlot("c_karst01_acc_lod1", "addr_k_acc_l1") }),
        }, Skeleton: null, Problems: Array.Empty<string>());

        return new BuildEnv(
            (c, s) => c == "Vesna" ? vesna : c == "Karst" ? karst : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0");
    }

    [Fact]
    public void Two_pipelines_whose_tiers_share_an_index_buffer_refuse()
    {
        var env = MakeTwoSubjectSharedTierEnv();
        var p = NewProject("TwoTiers");
        p.Selection.Add(new SelectionEntry { Character = "Karst", Outfit = "KarstDorm" });
        WriteDonorGlb("donor_v.glb", BodyBones.Concat(TwinBones).ToArray());
        WriteDonorGlb("donor_k.glb", ClothBones.Concat(MateBones).ToArray());
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "vBody", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01", ReplaceFile = "donor_v.glb",
        });
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "kBody", ObjectName = "c_karst01_torso_lod0",
            SubjectCharacter = "Karst", SubjectOutfit = "KarstDorm", ReplaceFile = "donor_k.glb",
        });

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("share one draw signature", ex.Message);
        Assert.Contains("KarstDorm", ex.Message);
        Assert.Contains("VesnaSSR01", ex.Message);
        // the TIER is what collided, not either pool's lod0
        Assert.Contains("karst_acc_lod1", ex.Message);
        Assert.Contains("vesna_acc_lod1", ex.Message);
    }

    [Fact]
    public void A_target_whose_tier_shares_anothers_draw_signature_refuses()
    {
        // The mate's lod1 is a different mesh on the body lod1's index buffer. Every section on that
        // hash fires on both draws, so replacing the body would suppress and redraw the mate at that
        // detail level too. Refused, and the refusal names both parts.
        var env = MakeSkinnedEnv(poolMate: true, mateTierTwin: true);
        var p = NewProject("OneTierTwin");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("share one draw signature", ex.Message);
        Assert.Contains("c_vesna01_body_lod0", ex.Message);
        Assert.Contains("mate", ex.Message);
    }

    /// <summary>How many sections the ini opens on one ib hash.</summary>
    private static int SectionsOn(string ini, string ibHash) =>
        ini.Split('\n').Count(l => l.Trim() == $"hash = {ibHash}");

    [Fact]
    public void Two_replaces_on_one_outfit_ride_one_capture_per_shared_pool_part()
    {
        // Several Replaces on ONE outfit is the shape the app is for, and their pools span the same parts
        // by construction. The second pipeline re-reaches the first's hashes on the very same meshes, so
        // it rides those capture sections — a hash claimed twice by one mesh is not a collision.
        var env = MakeSkinnedEnv(poolMate: true);
        var p = NewProject("TwoOnOne");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        // both Replaces recover, skin and draw under their own per-frame flag
        Assert.Contains("if $zz_done_vesna_body == 0\n", ini);
        Assert.Contains("if $zz_done_vesna_mate == 0\n", ini);
        Assert.Contains("run = CommandListDraw_vesna_body\n", ini);
        Assert.Contains("run = CommandListDraw_vesna_mate\n", ini);
        // one capture per pooled part, merged across the two pipelines rather than emitted per pipeline
        Assert.Equal(1, SectionsOn(ini, SkinnedIb("s0.bundle", "c_vesna01_body_lod0")));
        Assert.Equal(1, SectionsOn(ini, SkinnedIb("sm.bundle", "c_vesna01_mate_lod0")));
    }

    [Fact]
    public void Two_replaces_on_one_outfit_ride_one_capture_per_shared_tier()
    {
        // The tier walk claims on the pool part's terms, so it rides the same way: both pipelines reach
        // both parts' lod1 tiers, and the second finds each hash held by the mesh it is claiming for.
        var env = MakeSkinnedEnv(poolMate: true, mateTier: true);
        var p = NewProject("TwoOnOneTiers");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.Equal(1, SectionsOn(ini, SkinnedIb("s1.bundle", "c_vesna01_body_lod1")));
        Assert.Equal(1, SectionsOn(ini, SkinnedIb("smo.bundle", "c_vesna01_mate_lod1")));
        Assert.Contains("[TextureOverride_Cap_vesna_body_lod1]", ini);
        Assert.Contains("[TextureOverride_Cap_vesna_mate_lod1]", ini);
    }

    [Fact]
    public void A_tier_bone_no_pooled_top_lod_carries_pools_its_carrier_for_capture_only()
    {
        // The union palette is built from the POOLED parts' top LODs, while each part's other tiers are
        // recovered against that same palette. Here the body's lod1 poses a cloth bone its own lod0 does
        // not, so the pool the donor's weights ask for has no slot to pose that tier in. The cloth draws
        // at lod1 too, so it can write that bone's row where the body's lod1 needs it.
        var env = MakeSkinnedEnv(clothWearer: true, clothTier: true,
            bodyTierBones: BodyBones.Append(ClothBones[0]).ToArray());
        var p = NewProject("TierCover");
        WriteDonorGlb();                     // rides the BODY's bones alone: the cloth is no donor's part
        AddReplaceTarget(p);
        var lines = new List<string>();

        var r = ModBuilder.Build(p, env, _out, lines.Add, zip: false);

        // the cloth part joins the pool, at its roster position
        Assert.Contains(lines, l => l.StartsWith(
            "pool (vesna_body): c_vesna01_body_lod0, c_vesna01_cloth_lod0", StringComparison.Ordinal));
        // build-log only: the modder sees nothing about a part the mod doesn't change
        Assert.Contains(r.Diagnostics, d => d.Contains("'c_vesna01_cloth_lod0' is built alongside")
            && d.Contains("It is not changed"));
        Assert.DoesNotContain(r.Infos, i => i.Contains("is built alongside"));

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        // captured exactly like a donor-ridden pool part...
        Assert.Contains($"[TextureOverride_Cap_vesna_cloth]\nhash = {SkinnedIb("sc.bundle", "c_vesna01_cloth_lod0")}\nmatch_priority = 0\n", ini);
        // ...and never suppressed: its vanilla draw is the recovery input for the bones it owns
        Assert.DoesNotContain("handling = skip", SectionBody(ini, "[TextureOverride_Cap_vesna_cloth]"));
        Assert.NotEmpty(SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_body]")));
        // and the tier that needed it recovers rather than being left vanilla
        Assert.Contains("[TextureOverride_Cap_vesna_body_lod1]", ini);
    }

    [Fact]
    public void A_tier_bone_the_tier_never_poses_leaves_the_pool_alone()
    {
        // The body's lod1 TABLES a cloth bone that moves none of its vertices. A cloth that could have
        // covered it stays out: the pool would have bought a capture, an operator and a cb slot to pose
        // nothing, and the emitter has to build the tier's scatter map without that bone's union slot.
        var env = MakeSkinnedEnv(clothWearer: true, clothTier: true,
            bodyTierTabledOnly: new[] { ClothBones[0] });
        var p = NewProject("TierTabledOnly");
        WriteDonorGlb();
        AddReplaceTarget(p);
        var lines = new List<string>();

        var r = ModBuilder.Build(p, env, _out, lines.Add, zip: false);

        Assert.Contains(lines, l => l == "pool (vesna_body): c_vesna01_body_lod0 (anchor c_vesna01_body_lod0)");
        Assert.DoesNotContain(r.Diagnostics, d => d.Contains("is built alongside"));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.DoesNotContain("[TextureOverride_Cap_vesna_cloth]", ini);
        // the tier still recovers — the unposed bone cost it nothing
        Assert.Contains("[TextureOverride_Cap_vesna_body_lod1]", ini);
    }

    [Fact]
    public void A_carrier_from_another_outfit_state_refuses_rather_than_covering()
    {
        // The only part carrying the bone the body's lod1 poses is the DORM cloth, which never draws in
        // the frames this body draws in. Pooling it would pair the body's lod1 against a capture that
        // never fires, so the build refuses instead.
        var env = MakeSkinnedEnv(clothWearer: true, clothTier: true, clothTail: "_Dorm",
            bodyTierBones: BodyBones.Append(ClothBones[0]).ToArray());
        var p = NewProject("TierCrossContext");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("c_vesna01_body_lod1", ex.Message);
        Assert.Contains("0x00000201", ex.Message);
    }

    [Fact]
    public void A_carrier_that_does_not_draw_at_the_asking_tier_refuses_rather_than_covering()
    {
        // Same bone, same carrier, but the cloth ships only a top LOD: the tier chain would fall back to
        // its lod0 recovery, whose capture a frame drawing only the body's lod1 never fires.
        var env = MakeSkinnedEnv(clothWearer: true,
            bodyTierBones: BodyBones.Append(ClothBones[0]).ToArray());
        var p = NewProject("TierNoLodMate");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("c_vesna01_body_lod1", ex.Message);
        Assert.Contains("0x00000201", ex.Message);
    }

    [Fact]
    public void A_tier_bone_no_poolable_part_carries_refuses_naming_the_tier_and_the_bone()
    {
        // Nothing in the outfit carries the bone, so there is no pool that poses this tier. The refusal
        // says what was needed and not found rather than reporting a union that came up short.
        var env = MakeSkinnedEnv(bodyTierBones: BodyBones.Append(ClothBones[0]).ToArray());
        var p = NewProject("TierUncovered");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("c_vesna01_body_lod1", ex.Message);
        Assert.Contains("0x00000201", ex.Message);
        Assert.Contains("can supply", ex.Message);
    }

    /// <summary>One section's body: the lines between its header and the next header. A substring assert
    /// over the whole ini can't tell one capture section's skip from another's.</summary>
    private static string SectionBody(string ini, string header)
    {
        int start = ini.IndexOf(header + "\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no section {header} in the ini");
        start += header.Length + 1;
        int next = ini.IndexOf("\n[", start, StringComparison.Ordinal);
        return next < 0 ? ini[start..] : ini[start..next];
    }

    /// <summary>Every <c>handling = skip</c> in a section body, each paired with the <c>if</c> conditions
    /// open around it — an empty array means the skip is unconditional.</summary>
    private static List<string[]> SkipGuards(string sectionBody)
    {
        var open = new List<string>();
        var found = new List<string[]>();
        foreach (var raw in sectionBody.Split('\n'))
        {
            string l = raw.Trim();
            if (l.StartsWith("if ", StringComparison.Ordinal)) open.Add(l);
            else if (l == "endif" && open.Count > 0) open.RemoveAt(open.Count - 1);
            else if (l == "handling = skip") found.Add(open.ToArray());
        }
        return found;
    }

    [Fact]
    public void Two_keyed_replaces_on_one_outfit_skip_each_part_under_its_own_key()
    {
        // Each pipeline suppresses only the part IT replaces. The other pipeline pools that part for
        // recovery, and a pooling pipeline that also suppressed it would put the mate's key on the body's
        // skip: turning the mate off would leave the body's vanilla draw skipped by nobody's replacement.
        var env = MakeSkinnedEnv(poolMate: true);
        var p = NewProject("TwoOnOneKeyed");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, "F6");
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_mate_lod0", EditVerbs.Replace, "F7");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        var bodySkips = SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_body]"));
        var mateSkips = SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_mate]"));
        Assert.NotEmpty(bodySkips);
        Assert.NotEmpty(mateSkips);
        Assert.All(bodySkips, g =>
        {
            Assert.Contains("if $zz_key_f6 == 1", g);
            Assert.DoesNotContain("if $zz_key_f7 == 1", g);
        });
        Assert.All(mateSkips, g =>
        {
            Assert.Contains("if $zz_key_f7 == 1", g);
            Assert.DoesNotContain("if $zz_key_f6 == 1", g);
        });
    }

    [Fact]
    public void An_unkeyed_replace_does_not_unconditionally_skip_its_pool_mates_draw()
    {
        // The un-keyed pipeline's gate is always-on, so a suppression it contributed to the OTHER
        // pipeline's part would collapse that part's skip to an unconditional one — the keyed part would
        // vanish outright the moment its key is off: no vanilla draw and no replacement.
        var env = MakeSkinnedEnv(poolMate: true);
        var p = NewProject("TwoOnOneHalfKeyed");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, "F6");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        var bodySkips = SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_body]"));
        var mateSkips = SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_mate]"));
        Assert.NotEmpty(bodySkips);
        Assert.All(bodySkips, g => Assert.Contains("if $zz_key_f6 == 1", g));
        // the un-keyed pipeline's own target keeps its unconditional skip
        Assert.Contains(mateSkips, g => g.Length == 0);
    }

    [Fact]
    public void A_replace_that_hides_when_off_keeps_its_key_off_the_suppression_and_on_the_draw()
    {
        // The off-meaning travels from the change list's own binding to the emitted gates: the suppression
        // answers to the mod's key alone, so the part is absent while the change's key is down.
        var env = MakeSkinnedEnv();
        var p = NewProject("HidesWhenOff");
        WriteDonorGlb();
        AddReplaceTarget(p);
        p.Info.ToggleKey = "F6";
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, "F7",
            hideWhenOff: true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string body = SectionBody(ini, "[TextureOverride_Cap_vesna_body]");
        Assert.All(SkipGuards(body), g =>
        {
            Assert.Contains("if $zz_key_f6 == 1", g);
            Assert.DoesNotContain("if $zz_key_f7 == 1", g);
        });
        Assert.Contains("if $zz_key_f6 == 1\nif $zz_key_f7 == 1\nif $zz_done_", body);
    }

    [Fact]
    public void A_replace_that_reverts_to_vanilla_keeps_its_key_on_both()
    {
        var env = MakeSkinnedEnv();
        var p = NewProject("RevertsToVanilla");
        WriteDonorGlb();
        AddReplaceTarget(p);
        p.Info.ToggleKey = "F6";
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, "F7");

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string body = SectionBody(File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")),
            "[TextureOverride_Cap_vesna_body]");
        Assert.NotEmpty(SkipGuards(body));
        Assert.All(SkipGuards(body), g =>
        {
            Assert.Contains("if $zz_key_f6 == 1", g);
            Assert.Contains("if $zz_key_f7 == 1", g);
        });
    }

    [Fact]
    public void A_change_that_starts_off_declares_its_key_at_zero()
    {
        var env = MakeSkinnedEnv();
        var p = NewProject("StartsOff");
        WriteDonorGlb();
        AddReplaceTarget(p);
        p.Info.ToggleKey = "F6";
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, "F7",
            startsOff: true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains("global $zz_key_f7 = 0\n", ini);
        // the mod's own key has no start control: a keyed mod behaves as an unkeyed one until pressed
        Assert.Contains("global $zz_key_f6 = 1\n", ini);
        Assert.Empty(r.Warnings);
    }

    /// <summary>One key is one emitted variable, so it takes one start: it starts off only where every
    /// change on it asks to. Which change is listed first decides nothing.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Changes_sharing_a_key_that_disagree_on_their_start_all_start_on(
        bool bodyStartsOff, bool mateStartsOff)
    {
        var env = MakeSkinnedEnv(poolMate: true);
        var p = NewProject("StartDisagreement");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, "F7",
            startsOff: bodyStartsOff);
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_mate_lod0", EditVerbs.Replace, "F7",
            startsOff: mateStartsOff);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.Contains("global $zz_key_f7 = 1\n", File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")));
        // the row's own ⚠ carries the disagreement; the warning list is for what the author can fix
        Assert.DoesNotContain(r.Warnings, w => w.Contains("start"));
    }

    [Fact]
    public void Changes_sharing_a_key_that_all_start_off_declare_it_at_zero()
    {
        var env = MakeSkinnedEnv(poolMate: true);
        var p = NewProject("StartAgreement");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, "F7",
            startsOff: true);
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_mate_lod0", EditVerbs.Replace, "F7",
            startsOff: true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.Contains("global $zz_key_f7 = 0\n", File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("start"));
    }

    [Fact]
    public void A_change_that_starts_off_on_the_mods_own_key_starts_on()
    {
        // the mod's own key is a claimant that starts on, so sharing it is the same disagreement as any
        // other: a mod keyed on F6 behaves as an unkeyed one until F6 is pressed
        var env = MakeSkinnedEnv();
        var p = NewProject("SharesTheModKey");
        WriteDonorGlb();
        AddReplaceTarget(p);
        p.Info.ToggleKey = "F6";
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, "F6",
            startsOff: true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.Contains("global $zz_key_f6 = 1\n", File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")));
    }

    /// <summary>Every verb's key carries its start, not just a Replace's: the binding travels the same
    /// route from the change list to the declaration whatever the change is.</summary>
    [Fact]
    public void A_keyed_hide_that_starts_off_declares_its_key_at_zero()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject("HideStartsOff");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Hide, "F8",
            startsOff: true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.Contains("global $zz_key_f8 = 0\n", File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")));
    }

    [Fact]
    public void A_keyed_retexture_that_starts_off_declares_its_key_at_zero()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject("RetexStartsOff");
        AddEditedTexture(p);
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Retexture, "F9",
            startsOff: true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        Assert.Contains("global $zz_key_f9 = 0\n", File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")));
    }

    // ---- publishing: the previous build and the zip -----------------------------------------------

    [Fact]
    public void A_locked_file_in_the_previous_build_leaves_it_whole_and_fails_the_run()
    {
        // A publish that deleted the previous folder in place would fail that delete halfway on one
        // locked file, leaving the author with neither the build they had nor the one they asked for.
        // The rename-aside fails before anything is destroyed.
        var env = MakeEnv(out _, out _);
        var p = NewProject("Locked");
        AddEditedTexture(p);
        var first = ModBuilder.Build(p, env, _out, zip: false);
        var before = Directory.GetFiles(first.OutDir).Select(Path.GetFileName).Order().ToArray();
        Assert.NotEmpty(before);

        using (File.Open(Path.Combine(first.OutDir, "mod.ini"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Equal(before, Directory.GetFiles(first.OutDir).Select(Path.GetFileName).Order().ToArray());
        Assert.Single(Directory.GetDirectories(_out));   // no aside, no work or tmp dirs left behind
    }

    [Fact]
    public void A_zip_whose_write_dies_partway_leaves_nothing_under_the_published_name()
    {
        // Written straight to the published name, a source that stopped reading mid-write would leave a
        // truncated archive there beside a sound mod folder.
        string modDir = Path.Combine(_root, "zip-src");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "a.txt"), new string('a', 8192));
        File.WriteAllText(Path.Combine(modDir, "z.txt"), "the one that won't open");
        string zipPath = Path.Combine(_root, "pack.zip");

        // entries go in ordinal path order, so the readable one is already in the archive when the
        // locked one refuses
        using (File.Open(Path.Combine(modDir, "z.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => ModBuilder.PublishDistributionZip(modDir, zipPath));

        Assert.False(File.Exists(zipPath));
        Assert.Empty(Directory.GetFiles(_root, "pack.zip*"));
    }

    [Fact]
    public void A_published_zip_leaves_no_temp_beside_it()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject("ZipClean");
        AddEditedTexture(p);

        var r = ModBuilder.Build(p, env, _out);

        Assert.True(File.Exists(r.ZipPath));
        Assert.Single(Directory.GetFiles(_out, "*.zip*"));
    }

    // ---- the unrendered tier predicate ------------------------------------------------------------

    [Fact]
    public void An_explicit_hide_emits_no_section_for_any_lodm_tier()
    {
        // lodm0 is the tier the corpus ships, but the rule is the family: no lodm* tier renders on PC, so
        // an override on one covers a draw that never happens.
        var env = MakeMidTierEnv(out var h0, out var hm, out var h1, mid: "lodm1");
        var p = NewProject("MidOne");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"hash = {h0}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.Contains($"hash = {h1}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.DoesNotContain(hm, ini);
    }

    [Fact]
    public void A_replace_captures_no_unrendered_tier()
    {
        // The pool's tier walk reads the same predicate as the hide walk: a capture on a draw the client
        // never makes costs a section and a conditioned payload for nothing.
        var env = MakeSkinnedEnv(midTier: true);
        var p = NewProject("SwapMid");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.Contains($"hash = {SkinnedIb("s1.bundle", "c_vesna01_body_lod1")}\nmatch_priority = 0\n", ini);
        Assert.DoesNotContain(SkinnedIb("smid.bundle", "c_vesna01_body_lodm1"), ini);
    }
}
