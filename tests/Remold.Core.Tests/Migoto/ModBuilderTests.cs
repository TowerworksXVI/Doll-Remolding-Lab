using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Remold.Core;
using Remold.Core.Export;
using Remold.Core.Materials;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Skeleton;
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

    /// <summary>The stock effect overlay's hash: what a ramp can use to recognize its material.</summary>
    private string _blendTexHash = "";

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
            AppVersion: "test-1.0").Exact();
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
            AppVersion: "test-1.0").Exact();
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
        string user = "c_vesna01_body_lod0", string bundle = "bundleT", string objectName = "tex_body_d",
        (byte R, byte G, byte B, byte A)? colour = null)
    {
        FlatDds.Write(Path.Combine(_proj, file), colour ?? (1, 2, 3, 255));
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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
            _ => null, id => bytes.GetValueOrDefault(id), CatalogVersion: "12345", AppVersion: "test-1.0").Exact();
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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
            _ => null, id => bytes.GetValueOrDefault(id), CatalogVersion: "12345", AppVersion: "test-1.0").Exact();
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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
        var a = ReleasedBuild.Build(first, env, _out, zip: false, caches: caches);
        var encoded = File.ReadAllBytes(Path.Combine(a.OutDir, "rtx_skin_a.dds"));
        Assert.Single(Directory.GetFiles(caches.TextureDir, "*.dds"));

        // a differently-named copy of the same image: same content, same entry
        File.Copy(Path.Combine(_proj, "skin.png"), Path.Combine(_proj, "skin_copy.png"));
        var second = NewProject("EncAgain");
        AddEditedPng(second, "skin_copy.png");
        var b = ReleasedBuild.Build(second, env, _out, zip: false, caches: caches);

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

        var cold = ReleasedBuild.Build(p, env, _out, zip: false, caches: caches);
        var good = File.ReadAllBytes(Path.Combine(cold.OutDir, "rtx_skin_a.dds"));
        var (entry, length) = TextureEntry(caches);

        // truncated: the header still reads, the recorded byte count does not
        File.WriteAllBytes(entry, good.Take(good.Length / 2).ToArray());
        var afterTruncation = ReleasedBuild.Build(p, env, _out, zip: false, caches: caches);
        Assert.Equal(good, File.ReadAllBytes(Path.Combine(afterTruncation.OutDir, "rtx_skin_a.dds")));
        Assert.Equal(good, File.ReadAllBytes(entry));                       // and the entry was republished
        Assert.Equal(good.Length.ToString(), File.ReadAllText(length));

        // corrupt header at the right length: only the magic separates it from a sound entry
        var headerless = good.ToArray();
        Array.Clear(headerless, 0, 4);
        File.WriteAllBytes(entry, headerless);
        var afterCorruption = ReleasedBuild.Build(p, env, _out, zip: false, caches: caches);
        Assert.Equal(good, File.ReadAllBytes(Path.Combine(afterCorruption.OutDir, "rtx_skin_a.dds")));
        Assert.Equal(good, File.ReadAllBytes(entry));

        // an entry with no recorded length at all is nothing this build published: it is not served either
        File.Delete(length);
        var afterLoss = ReleasedBuild.Build(p, env, _out, zip: false, caches: caches);
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

        var cold = ReleasedBuild.Build(p, env, _out, zip: false, caches: caches);
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

        var after = ReleasedBuild.Build(p, env, _out, zip: false, caches: caches);

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
        ReleasedBuild.Build(p, env, _out, zip: false, caches: caches);

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
        ReleasedBuild.Build(p, env, _out, cold.Add, zip: false, caches: caches);
        var warm = new List<string>();
        ReleasedBuild.Build(p, env, _out, warm.Add, zip: false, caches: caches);

        Assert.Contains(cold, l => l.Contains("Encoding skin.png"));
        Assert.DoesNotContain(cold, l => l.Contains("texture cache: reusing"));
        Assert.Contains(warm, l => l.Contains("texture cache: reusing skin.png"));
        Assert.DoesNotContain(warm, l => l.Contains("Encoding skin.png"));
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

        var cold = ReleasedBuild.Build(p, env, _out, zip: false, caches: caches);
        var snapshot = Path.Combine(_root, "cold-snapshot");
        Directory.CreateDirectory(snapshot);
        foreach (var f in Directory.GetFiles(cold.OutDir))
            File.Copy(f, Path.Combine(snapshot, Path.GetFileName(f)));

        var warm = ReleasedBuild.Build(p, env, _out, zip: false, caches: caches);

        var names = Directory.GetFiles(snapshot).Select(Path.GetFileName).Order().ToArray();
        Assert.NotEmpty(names);
        Assert.Equal(names, Directory.GetFiles(warm.OutDir).Select(Path.GetFileName).Order().ToArray());
        foreach (var n in names)
            Assert.Equal(File.ReadAllBytes(Path.Combine(snapshot, n!)),
                         File.ReadAllBytes(Path.Combine(warm.OutDir, n!)));
    }

    [Fact]
    public void Exact_build_cache_serves_the_complete_published_result_on_an_unchanged_rebuild()
    {
        var sourceEnv = MakeEnv(out _, out _);
        var project = NewProject("ExactHit");
        AddEditedTexture(project);
        var execution = Authored(project, sourceEnv, _ => { });
        int bundleReads = 0;
        var env = sourceEnv with
        {
            Deobfuscate = bundle => { bundleReads++; return sourceEnv.Deobfuscate(bundle); },
            BundleContentHash = bundle => "content-" + bundle,
            CatalogIdentity = "catalog-file-A",
            CompilerIdentity = "compiler-A",
        };
        var caches = new BuildCaches(Path.Combine(_root, "exact-op"), Path.Combine(_root, "exact-tex"),
            Path.Combine(_root, "exact-results"));
        var coldLog = new List<string>();
        var cold = ModBuilder.Build(execution, env, _out, coldLog.Add, zip: true, caches: caches);
        int afterCold = bundleReads;

        var warmLog = new List<string>();
        var warm = ModBuilder.Build(execution, env, _out, warmLog.Add, zip: true, caches: caches);

        Assert.Equal(afterCold, bundleReads);
        Assert.Equal(cold.OutDir, warm.OutDir);
        Assert.Equal(cold.ZipPath, warm.ZipPath);
        Assert.Equal(cold.Warnings, warm.Warnings);
        Assert.Equal(cold.Infos, warm.Infos);
        Assert.Equal(cold.Diagnostics, warm.Diagnostics);
        Assert.Equal(coldLog, warmLog);
        Assert.True(Directory.Exists(warm.OutDir));
        Assert.True(File.Exists(warm.ZipPath));
    }

    /// <summary>A run that leaned on a degraded game-table read (the pools-stay-conservative fallbacks —
    /// typically the game holding its files) is a real build, but it must not become the cached answer:
    /// the degradation is a fact about the run, not the fingerprint's inputs, so a served hit would defeat
    /// the note's own "close the game for a full pass".</summary>
    [Fact]
    public void A_degraded_read_publishes_no_completion_record_so_the_clean_rebuild_reruns()
    {
        var sourceEnv = MakeEnv(out _, out _);
        var project = NewProject("ExactDegraded");
        AddEditedTexture(project);
        var execution = Authored(project, sourceEnv, _ => { });
        int bundleReads = 0;
        bool degraded = true;
        var env = sourceEnv with
        {
            Deobfuscate = bundle => { bundleReads++; return sourceEnv.Deobfuscate(bundle); },
            BundleContentHash = bundle => "content-" + bundle,
            CatalogIdentity = "catalog-file-A",
            CompilerIdentity = "compiler-A",
            ReadDegraded = () => degraded,
        };
        var caches = new BuildCaches(Path.Combine(_root, "degraded-op"), Path.Combine(_root, "degraded-tex"),
            Path.Combine(_root, "degraded-results"));
        ModBuilder.Build(execution, env, _out, zip: true, caches: caches);
        int afterDegraded = bundleReads;

        degraded = false;
        ModBuilder.Build(execution, env, _out, zip: true, caches: caches);
        Assert.True(bundleReads > afterDegraded,
            "the clean rebuild was served the degraded run's package");

        int afterClean = bundleReads;
        ModBuilder.Build(execution, env, _out, zip: true, caches: caches);
        Assert.Equal(afterClean, bundleReads);
    }

    [Theory]
    [InlineData("app-version")]
    [InlineData("project-structure")]
    [InlineData("referenced-file")]
    [InlineData("catalog-identity")]
    [InlineData("bundle-content")]
    [InlineData("compiler-schema")]
    [InlineData("shader-slots")]
    public void Exact_build_cache_misses_when_a_fingerprint_ingredient_moves(string ingredient)
    {
        var sourceEnv = MakeEnv(out _, out _);
        var project = NewProject("ExactMiss" + ingredient);
        AddEditedTexture(project);
        var execution = Authored(project, sourceEnv, _ => { });
        int bundleReads = 0;
        var bundleContent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Content(string bundle) => bundleContent.GetValueOrDefault(bundle, "content-" + bundle);
        string shader = Path.Combine(_root, "slots-" + ingredient + ".json");
        File.Copy(LabPaths.ShaderSlotCatalogFile, shader, overwrite: true);
        var env = sourceEnv with
        {
            Deobfuscate = bundle => { bundleReads++; return sourceEnv.Deobfuscate(bundle); },
            BundleContentHash = Content,
            CatalogIdentity = "catalog-file-A",
            CompilerIdentity = "compiler-A",
            ShaderSlotCatalogFile = shader,
        };
        var caches = new BuildCaches(Path.Combine(_root, "miss-op-" + ingredient),
            Path.Combine(_root, "miss-tex-" + ingredient), Path.Combine(_root, "miss-results-" + ingredient));
        ModBuilder.Build(execution, env, _out, zip: true, caches: caches);
        int beforeMutation = bundleReads;

        switch (ingredient)
        {
            case "app-version": env = env with { AppVersion = "test-2.0" }; break;
            case "project-structure": execution.Project.Info.Description = "structure moved"; break;
            case "referenced-file": FlatDds.Write(Path.Combine(_proj, "skin.dds"), (9, 8, 7, 255)); break;
            case "catalog-identity": env = env with { CatalogIdentity = "catalog-file-B" }; break;
            case "bundle-content": bundleContent["bundleT"] = "content-bundleT-moved"; break;
            case "compiler-schema": env = env with { CompilerIdentity = "compiler-B" }; break;
            case "shader-slots": File.AppendAllText(shader, Environment.NewLine); break;
            default: throw new ArgumentOutOfRangeException(nameof(ingredient));
        }

        ModBuilder.Build(execution, env, _out, zip: true, caches: caches);

        Assert.True(bundleReads > beforeMutation, $"{ingredient} incorrectly served the completed build");
    }

    [Fact]
    public void Exact_build_cache_rebuilds_when_a_published_file_or_zip_is_not_intact()
    {
        var sourceEnv = MakeEnv(out _, out _);
        var project = NewProject("ExactOutputGuard");
        AddEditedTexture(project);
        var execution = Authored(project, sourceEnv, _ => { });
        int bundleReads = 0;
        var env = sourceEnv with
        {
            Deobfuscate = bundle => { bundleReads++; return sourceEnv.Deobfuscate(bundle); },
            BundleContentHash = bundle => "content-" + bundle,
            CatalogIdentity = "catalog-file-A",
            CompilerIdentity = "compiler-A",
        };
        var caches = new BuildCaches(Path.Combine(_root, "guard-op"), Path.Combine(_root, "guard-tex"),
            Path.Combine(_root, "guard-results"));
        var first = ModBuilder.Build(execution, env, _out, zip: true, caches: caches);
        int beforeFolderDamage = bundleReads;
        File.AppendAllText(Path.Combine(first.OutDir, "mod.ini"), Environment.NewLine);

        var repairedFolder = ModBuilder.Build(execution, env, _out, zip: true, caches: caches);
        Assert.True(bundleReads > beforeFolderDamage);
        int beforeMissingZip = bundleReads;
        File.Delete(repairedFolder.ZipPath!);

        ModBuilder.Build(execution, env, _out, zip: true, caches: caches);
        Assert.True(bundleReads > beforeMissingZip);
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

        var uncapped = ReleasedBuild.Build(p, env, _out, zip: false);
        var bytes = File.ReadAllBytes(Path.Combine(uncapped.OutDir, "rtx_skin_a.dds"));
        var capped = ReleasedBuild.Build(p, env, _out, zip: false, encoderCpuLimit: 1);

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

        ReleasedBuild.Build(p, env, _out, lines.Add, zip: false);

        // the line names the file and what is being made of it, and nothing about how
        int encoding = lines.FindIndex(l => l.Contains("Encoding skin.png") && l.Contains("8×8"));
        Assert.True(encoding >= 0, "no encode progress line in: " + string.Join(" | ", lines));
        // it streams DURING the encode, not as part of the final-assembly span
        Assert.True(encoding < lines.FindIndex(l => l.Contains("Assembling the mod files")));
    }

    /// <summary>An encode that did not run on the GPU costs minutes a build log otherwise never explains.
    /// Whichever rung this machine resolves to is the branch that runs here.</summary>
    [Fact]
    public void A_build_that_encodes_names_its_encoder_unless_it_is_the_hardware_device()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject("Rung");
        AddEditedPng(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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
            albedoOnly, "Body", w1);
        Assert.Contains("No original normal on 'Body' could be matched to a texture slot", Assert.Single(w1));

        var normalOnly = new[] { new StockMapTag("aabbccdd", StockMapKind.Normal) };
        var w2 = new List<string>();
        ModBuilder.WarnUnbindableDonorMaps(OneRow(MapSlot.From("a.dds"), MapSlot.From("n.dds")),
            normalOnly, "Body", w2);
        Assert.Contains("No original base color on 'Body' could be matched to a texture slot", Assert.Single(w2));

        // RMO is its own kind, and warns on its own
        var w3 = new List<string>();
        ModBuilder.WarnUnbindableDonorMaps(OneRow(rmo: MapSlot.From("r.dds")), albedoOnly, "Body", w3);
        Assert.Contains("No original RMO on 'Body' could be matched to a texture slot", Assert.Single(w3));

        // nothing bound of a kind is nothing to warn about
        var none = new List<string>();
        ModBuilder.WarnUnbindableDonorMaps(OneRow(), Array.Empty<StockMapTag>(), "Body", none);
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
            albedoOnly, "Body", authored);
        Assert.Contains("the edited RMO won't show in game", Assert.Single(authored));

        var defaulted = new List<string>();
        ModBuilder.WarnUnbindableDonorMaps(OneRow(MapSlot.From("a.dds"), rmo: MapSlot.Neutral),
            albedoOnly, "Body", defaulted);
        var line = Assert.Single(defaulted);
        Assert.Contains("the blank RMO won't show in game", line);
        Assert.DoesNotContain("edited RMO", line);
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

        var tags = ModBuilder.TagStockMaps(materials, "vesna_body", "Body",
            m => m.TextureName == "upper_d" ? "aabbccdd"
                : throw new InvalidDataException("isn't in bundle 'bundleT'"),
            warnings, diagnostics);

        // the survivor is tagged, and the failure is the author's to see, with what it costs them
        Assert.Equal(new[] { new StockMapTag("aabbccdd", StockMapKind.Albedo) }, tags);
        Assert.Contains(warnings, w => w.Contains("Couldn't match the original map 'lower_d' on 'Body'")
            && w.Contains("Some edited maps may not show in game."));
        // the reason it wouldn't tag is the build's own record
        Assert.Contains(diagnostics, d => d.Contains("anchor map 'lower_d' (vesna_body) can't be slot-tagged")
            && d.Contains("isn't in bundle 'bundleT'"));
        Assert.DoesNotContain(warnings, w => w.Contains("isn't in bundle"));

        // and the kind-level warning stays quiet, because the kind DID keep a tag — the whole point
        ModBuilder.WarnUnbindableDonorMaps(OneRow(MapSlot.From("a.dds")), tags, "vesna_body", warnings);
        Assert.DoesNotContain(warnings, w => w.Contains("No original base color on 'Body' could be matched to a texture slot"));
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

        Assert.Throws<BlockedAssetException>(() => ModBuilder.TagStockMaps(materials, "vesna_body", "Body",
            _ => throw new BlockedAssetException("'upper_d' is not a supported asset"), warnings, diagnostics));
        Assert.Throws<BlockedAssetException>(() => ModBuilder.AnchorSrgb(materials,
            MaterialResolver.IsBaseColor, "base color", byConvention: true, "vesna_body", "Body",
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

        var r = ReleasedBuild.Build(p, env, _out);

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

    /// <summary>An install whose shader slot data can't be read still probes the classic register range —
    /// the one every release before the measurement probed — so nothing built on the probe stops working.
    /// The sidecar records that range with no catalog beside it, which is how a reader tells this build
    /// from a measured one.</summary>
    [Fact]
    public void A_build_with_no_readable_slot_catalog_probes_the_classic_range()
    {
        var env = MakeEnv(out _, out _) with
        {
            ShaderSlotCatalogFile = Path.Combine(_root, "not-a-catalog.json"),
        };
        var p = NewProject("NoSlotData");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        var warning = Assert.Single(r.Warnings, w => w.Contains("texture slot data"));
        Assert.Contains("Some maps won't show on replaced meshes", warning);
        // what is actually lost, and nothing wider: the registers past the classic range. Nothing about
        // toon ramps — a mod that asks for one is refused outright rather than warned at.
        Assert.Contains("the mesh swap itself still works", warning);
        Assert.DoesNotContain("Toon ramp", warning);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        var slots = doc.RootElement.GetProperty("shader_slots");
        Assert.Equal(JsonValueKind.Null, slots.GetProperty("catalog").ValueKind);
        Assert.Equal(JsonValueKind.Null, slots.GetProperty("game_build").ValueKind);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6 },
            slots.GetProperty("stock_ps_slots").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.Empty(slots.GetProperty("ramp_ps_slots").EnumerateArray());
    }

    [Fact]
    public void An_explicit_hide_emits_every_lodm0_renderer_tier()
    {
        // The prefab's renderer slots are the coverage truth, including the opt-in medium tier.
        var env = MakeMidTierEnv(out var h0, out var hm, out var h1);
        var p = NewProject("Mid");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"hash = {h0}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.Contains($"hash = {hm}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.Contains($"hash = {h1}\nmatch_priority = 0\nhandling = skip", ini);
        AssertNoDuplicateSections(ini);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        var hashes = doc.RootElement.GetProperty("override_hashes")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { h0, hm, h1 }.Order().ToArray(), hashes);
    }

    [Fact]
    public void An_explicit_hide_emits_every_lodm0_renderer_tier_when_the_marker_is_infixed()
    {
        // The variant garments' LOD marker sits INSIDE the name (…_P1_body1_lodm0_Dorm); it is still a
        // renderer slot the hide must cover.
        var env = MakeMidTierEnv(out var h0, out var hm, out var h1, token: "P1_body1", variant: "_Dorm");
        var p = NewProject("MidInfixed");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_P1_body1_lod0_Dorm", true);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"hash = {h0}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.Contains($"hash = {hm}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.Contains($"hash = {h1}\nmatch_priority = 0\nhandling = skip", ini);
        AssertNoDuplicateSections(ini);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        var hashes = doc.RootElement.GetProperty("override_hashes")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { h0, hm, h1 }.Order().ToArray(), hashes);
    }

    [Fact]
    public void Edited_texture_builds_a_retexture_keyed_on_the_stock_textures_hash()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject("Retex");
        AddEditedTexture(p);
        p.Info.IncludeRepairData = false;
        var log = new List<string>();

        var r = ReleasedBuild.Build(p, env, _out, log.Add);

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
        BuildWatermarkTests.AssertStamped(r, expectRepair: false);
        Assert.DoesNotContain(log,
            line => line.Contains(CoreBuildIdentity.ShortHash, StringComparison.Ordinal));
    }

    [Fact]
    public void Subjects_sharing_a_stock_texture_retexture_it_once()
    {
        // A second subject resolving to the same model. Derivation passes both through; the build collapses
        // them by TEXTURE, since two sections on one hash override the same resource twice.
        var env = MakeEnv(out _, out _);
        var envShared = new BuildEnv(
            (c, s) => c == "Vesna" ? env.ResolveSubject("Vesna", "VesnaSSR01") : null,
            env.ResolveAddress, env.Deobfuscate, env.CatalogVersion, env.AppVersion).Exact();
        var p = NewProject("Shared");
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaAlt" });
        AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, envShared, _out);

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

        var r = ReleasedBuild.Build(p, env, _out);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
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
            byConvention: true, "vesna_body", "Body", m => family[m.TextureName], warnings, diagnostics);
        bool normal = ModBuilder.AnchorSrgb(AnchorMaterials, MaterialResolver.IsNormal, "normal",
            byConvention: false, "vesna_body", "Body", m => family[m.TextureName], warnings, diagnostics);

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
            byConvention: false, "vesna_body", "Body", m => family[m.TextureName], warnings, diagnostics);

        Assert.True(albedo);   // the FIRST readable map's family, not the convention
        Assert.Contains(warnings, w => w.Contains("disagree") && w.Contains("'upper_d' is sRGB")
            && w.Contains("'lower_d' is linear") && w.Contains("written as sRGB"));
    }

    [Fact]
    public void An_unreadable_anchor_map_warns_then_tags_by_the_kinds_convention()
    {
        var warnings = new List<string>();
        var diagnostics = new List<string>();

        bool albedo = ModBuilder.AnchorSrgb(AnchorMaterials, MaterialResolver.IsBaseColor, "base color",
            byConvention: true, "vesna_body", "Body",
            _ => throw new InvalidDataException("isn't in bundle 'bundleT'"), warnings, diagnostics);

        Assert.True(albedo);
        // the unreadable map is the author's problem; which tag the fallback picked is the build's own record
        Assert.Contains(warnings, w => w.Contains("Couldn't read the original base color map 'anchor_d'")
            && w.Contains("may show with the wrong colours"));
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
            byConvention: false, "vesna_body", "Body", _ => true, warnings, diagnostics);

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

    [Fact]
    public void Authored_source_identity_is_memoized_by_resolved_path_for_one_build()
    {
        string source = Path.Combine(_proj, "identity.png");
        using (var image = new Image<Rgba32>(4, 4, new Rgba32(20, 40, 60, 255)))
            image.SaveAsPng(source);
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string first = ModBuilder.SourceIdentity(known, source);
        using (var append = new FileStream(source, FileMode.Append, FileAccess.Write, FileShare.Read))
            append.WriteByte(1);
        string changed = AuthoredDds.SourceIdentity(source);
        string second = ModBuilder.SourceIdentity(known, source);

        Assert.NotEqual(first, changed);   // a second hash would have observed the rewrite
        Assert.Equal(first, second);
        Assert.Single(known);
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
    /// <paramref name="anchorRmo"/> give it a stock NORMAL and RMO too, and <paramref name="anchorRamp"/>
    /// a stock toon ramp, each of which a donor map of that kind needs to slot-tag against. <paramref name="meshTail"/> appends an outfit-state token
    /// after the LOD marker (…_lod0_Fight); <paramref name="twinPart"/> adds a second part whose mesh
    /// shares the body's index buffer; <paramref name="clothWearer"/> adds an unpooled part binding the
    /// same stock albedo; <paramref name="poolMate"/> adds a donor-ridden second part with its own index
    /// buffer, binding that same stock albedo; <paramref name="mateTierTwin"/> gives that part a tier
    /// drawing the BODY lod1's index buffer, <paramref name="mateTier"/> one drawing an index buffer
    /// of its own; <paramref name="midTier"/> puts a slot-addressed tier between the body's two ordinary
    /// ones, with its label and byte shape controlled by the <c>midTier*</c> arguments.
    /// <paramref name="bodyBlendShapes"/> and
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
    /// <paramref name="bodySharedMaterial"/> adds a second material to the body which binds the body's
    /// base colour, so that texture cannot identify either material's draw.
    /// <paramref name="wardrobe"/> adds two options of ONE wardrobe slot whose meshes share an index
    /// buffer, stream-1 bytes and base color — nothing at the draw parts them — each married to a
    /// companion mesh of its own; <see cref="WardrobeWorld"/>'s knobs shape the companions and extra
    /// siblings. Wire <see cref="WithWardrobeScheme"/> to classify them.</summary>
    private BuildEnv MakeSkinnedEnv(bool anchorNormal = false, bool anchorRmo = false,
        bool anchorRamp = false, bool rampDonor = false, bool anchorBlend = false,
        string meshTail = "", bool twinPart = false, bool clothWearer = false, bool poolMate = false,
        bool midTier = false, bool mateTierTwin = false, bool mateTier = false, int bodyBlendShapes = 0,
        int bodySkinWidth = 4, uint[]? bodyTabledOnly = null, bool facePart = false, bool ghostPart = false,
        bool unreadableWeightsPart = false, uint[]? bodyTierBones = null,
        uint[]? bodyTierTabledOnly = null, bool clothTier = false, string clothTail = "",
        bool narrowAccessory = false, bool bodySharedSkinStream = false, int twinPartUvSeed = 0,
        int twinPartPosSeed = 5, bool twinPartOwnAlbedo = false, bool twinPartSharedAlbedo = false,
        bool clothTwinsMate = false, bool clothOwnAlbedo = false, bool mateOwnAlbedo = false,
        bool twinPartShadowOff = false, bool twinPartRampAsAlbedo = false,
        Remold.Core.Model.VisibilityOverride twinPartVisibility = Remold.Core.Model.VisibilityOverride.None,
        WardrobeWorld? wardrobe = null, bool bodySharedMaterial = false,
        uint[]? mateTierBones = null, uint[]? clothBoneHashes = null, uint[]? clothTabledOnly = null,
        SubjectSkeleton? skeleton = null, bool bodyBlendOnly = false,
        bool bodySharedMaterialOwnBlend = false, bool poolMateFirst = false, int mateTierBlendShapes = 0,
        bool sharedGenericProperties = false, string midTierLod = "lodm1", int midTierVerts = 28,
        int midTierPosSeed = 11, string mateTierLod = "lod1")
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

        if (anchorBlend || bodySharedMaterialOwnBlend)
        {
            string bblend = Path.Combine(_root, "sblend.bundle");
            SyntheticBundle.BuildOneTexture(bblend, "tex_body_blend", 8, 8, 30, 80, 220, 255,
                colorSpace: 1);
            bytes["bundleBlend"] = File.ReadAllBytes(bblend);
            _blendTexHash = SyntheticBundle.StockTexHash(bytes["bundleBlend"], "tex_body_blend");
        }
        if (bodySharedMaterialOwnBlend)
        {
            string bblendOther = Path.Combine(_root, "sblend_other.bundle");
            SyntheticBundle.BuildOneTexture(bblendOther, "tex_other_blend", 8, 8, 210, 60, 25, 255,
                colorSpace: 1);
            bytes["bundleBlendOther"] = File.ReadAllBytes(bblendOther);
        }

        var maps = bodyBlendOnly
            ? new List<SubjectMap>()
            : new List<SubjectMap> { new("_BaseMap", "tex_body_d", "bundleT") };
        if (sharedGenericProperties)
        {
            maps.Clear();
            maps.Add(new SubjectMap("_DetailAlbedo", "tex_body_d", "bundleT"));
            maps.Add(new SubjectMap("_DetailMask", "tex_body_d", "bundleT"));
        }
        if (anchorNormal) maps.Add(new SubjectMap("_BumpMap", "tex_body_n", "bundleN"));
        if (anchorRmo) maps.Add(new SubjectMap("_RMOTex", "tex_body_r", "bundleR"));
        if (anchorBlend) maps.Add(new SubjectMap("_BlendTex", "tex_body_blend", "bundleBlend"));
        if (anchorRamp)
        {
            // the game's own shape: fp16 at the ramp extent, so the bytes a carried ramp is compared
            // against are the bytes a real one would be
            string bramp = Path.Combine(_root, "sramp.bundle");
            SyntheticBundle.Build(bramp, null, new SyntheticBundle.TextureSpec("tex_body_ramp",
                ModBuilder.RampWidth, ModBuilder.RampHeight,
                SyntheticBundle.RgbaHalfPixels(ModBuilder.RampWidth, ModBuilder.RampHeight, seed: 1),
                Format: SyntheticBundle.RgbaHalf));
            bytes["bundleRamp"] = File.ReadAllBytes(bramp);
            maps.Add(new SubjectMap("_RampMap", "tex_body_ramp", "bundleRamp"));
        }
        var materials = new List<SubjectMaterial> { new("m_body", 1, "cab-body", maps) };
        if (bodySharedMaterial)
        {
            var sharedMaps = bodyBlendOnly
                ? new List<SubjectMap>()
                : new List<SubjectMap> { new("_BaseMap", "tex_body_d", "bundleT") };
            if (bodySharedMaterialOwnBlend)
                sharedMaps.Add(new SubjectMap("_BlendTex", "tex_other_blend", "bundleBlendOther"));
            materials.Add(new SubjectMaterial("m_body_other", 2, "cab-body-other", sharedMaps));
        }
        var bodyTiers = new List<RecipeTierSlot>();
        var addresses = new Dictionary<string, string> { ["addr_body"] = "bundle0", ["addr_body_l1"] = "bundle1" };
        if (midTier)
        {
            string midName = $"c_vesna01_body_{midTierLod}{meshTail}";
            string bmid = Path.Combine(_root, "smid.bundle");
            SyntheticBundle.BuildOneSkinnedMesh(bmid, midName,
                Cloud(midTierVerts, midTierPosSeed), WrappedTris(midTierVerts), BodyBones);
            bytes["bundleMid"] = File.ReadAllBytes(bmid);
            bodyTiers.Add(new RecipeTierSlot(midName, "addr_body_mid"));
            addresses["addr_body_mid"] = "bundleMid";
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
            // one texture is a base colour to one material and a shading curve to another: this part wears
            // the body's ramp AS its base colour, which is what makes the two kinds collide on one hash
            if (twinPartRampAsAlbedo) twinMaps.Add(new SubjectMap("_BaseMap", "tex_body_ramp", "bundleRamp"));
            else if (twinPartOwnAlbedo) twinMaps.Add(new SubjectMap("_BaseMap", "tex_alt_d", "bundleAlt"));
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
                Cloud(clothVerts, 13), WrappedTris(clothVerts), clothBoneHashes ?? ClothBones,
                tabledOnlyBones: clothTabledOnly);
            bytes["bundleC"] = File.ReadAllBytes(bc);
            var clothTiers = new List<RecipeTierSlot>();
            if (clothTier)
            {
                // its own vertex count, so it is a tier capture of its own beside the body's lod1
                string bcl = Path.Combine(_root, "scl.bundle");
                SyntheticBundle.BuildOneSkinnedMesh(bcl, $"c_vesna01_cloth_lod1{clothTail}",
                    Cloud(14, 29), WrappedTris(14), clothBoneHashes ?? ClothBones,
                    tabledOnlyBones: clothTabledOnly);
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
                string mateTierName = $"c_vesna01_mate_{mateTierLod}";
                SyntheticBundle.BuildOneSkinnedMesh(bmt, mateTierName, Cloud(24, 19), WrappedTris(24),
                    mateTierBones ?? MateBones, blendShapes: mateTierBlendShapes);
                bytes["bundleMT"] = File.ReadAllBytes(bmt);
                mateTiers.Add(new RecipeTierSlot(mateTierName, "addr_mate_tier"));
                addresses["addr_mate_tier"] = "bundleMT";
            }
            if (mateTier)
            {
                // its own vertex count, so it is a tier capture of its own beside the body's lod1
                string bmo = Path.Combine(_root, "smo.bundle");
                SyntheticBundle.BuildOneSkinnedMesh(bmo, "c_vesna01_mate_lod1", Cloud(22, 19), WrappedTris(22),
                    mateTierBones ?? MateBones, blendShapes: mateTierBlendShapes);
                bytes["bundleMO"] = File.ReadAllBytes(bmo);
                mateTiers.Add(new RecipeTierSlot("c_vesna01_mate_lod1", "addr_mate_l1"));
                addresses["addr_mate_l1"] = "bundleMO";
            }
            var mateMaps = new List<SubjectMap>
            {
                mateOwnAlbedo
                    ? new SubjectMap("_BaseMap", "tex_alt_d", "bundleAlt")
                    : new SubjectMap("_BaseMap", "tex_body_d", "bundleT"),
            };
            var matePart = new SubjectPart("mate", "c_vesna01_mate_lod0", "addr_mate",
                new[] { new SubjectMaterial("m_mate", 1, "cab-mate", mateMaps) },
                SiblingTiers: mateTiers.Count > 0 ? mateTiers.ToArray() : null);
            if (poolMateFirst) parts.Insert(0, matePart);
            else parts.Add(matePart);
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
            Skeleton: skeleton, Problems: Array.Empty<string>());

        // A SECOND outfit, the one a cross-outfit graft's geometry came from. Two parts, each with its own
        // base colour and its own toon ramp, so a build that carried one part's ramp onto the other would
        // show. Its meshes are never read — only the material model is, which is all the ramp join needs.
        SubjectModel? donorModel = null;
        if (rampDonor)
        {
            string bdonor = Path.Combine(_root, "sdonor.bundle");
            SyntheticBundle.Build(bdonor, null,
                new SyntheticBundle.TextureSpec("tex_paloma_d", 8, 8,
                    SyntheticBundle.SolidRgba32(8, 8, 30, 60, 90, 255), ColorSpace: 1),
                new SyntheticBundle.TextureSpec("tex_paloma_ramp", ModBuilder.RampWidth, ModBuilder.RampHeight,
                    SyntheticBundle.RgbaHalfPixels(ModBuilder.RampWidth, ModBuilder.RampHeight, seed: 7),
                    Format: SyntheticBundle.RgbaHalf),
                new SyntheticBundle.TextureSpec("tex_paloma2_d", 8, 8,
                    SyntheticBundle.SolidRgba32(8, 8, 90, 60, 30, 255), ColorSpace: 1),
                new SyntheticBundle.TextureSpec("tex_paloma2_ramp", ModBuilder.RampWidth, ModBuilder.RampHeight,
                    SyntheticBundle.RgbaHalfPixels(ModBuilder.RampWidth, ModBuilder.RampHeight, seed: 10),
                    Format: SyntheticBundle.RgbaHalf));
            bytes["bundlePaloma"] = File.ReadAllBytes(bdonor);
            var donorParts = new List<SubjectPart>
            {
                new SubjectPart("body", "c_paloma01_body_lod0", "addr_paloma_body", new[]
                {
                    new SubjectMaterial("m_paloma_body", 1, "cab-paloma-body", new[]
                    {
                        new SubjectMap("_BaseMap", "tex_paloma_d", "bundlePaloma"),
                        new SubjectMap("_RampMap", "tex_paloma_ramp", "bundlePaloma"),
                    }),
                }),
                new SubjectPart("arm", "c_paloma01_arm_lod0", "addr_paloma_arm", new[]
                {
                    new SubjectMaterial("m_paloma_arm", 1, "cab-paloma-arm", new[]
                    {
                        new SubjectMap("_BaseMap", "tex_paloma2_d", "bundlePaloma"),
                        new SubjectMap("_RampMap", "tex_paloma2_ramp", "bundlePaloma"),
                    }),
                }),
            };
            donorModel = new SubjectModel("Paloma", "PalomaAA01", SubjectSource.Prefab, donorParts.ToArray(),
                Skeleton: null, Problems: Array.Empty<string>());
        }

        return new BuildEnv(
            (c, s) => c == "Vesna" && s == "VesnaSSR01" ? model
                : c == "Paloma" && s == "PalomaAA01" ? donorModel : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0").Exact();
    }

    /// <summary>Two different parts whose picked materials share one effect overlay. Each part also has a
    /// sibling material wearing the same base colour, leaving the shared blend map as the picked material's
    /// only discriminator within that part.</summary>
    private BuildEnv MakeSharedBlendRampPicksEnv()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, anchorBlend: true, clothWearer: true);
        string clothRampPath = Path.Combine(_root, "scloth_ramp.bundle");
        SyntheticBundle.Build(clothRampPath, null, new SyntheticBundle.TextureSpec("tex_cloth_ramp",
            ModBuilder.RampWidth, ModBuilder.RampHeight,
            SyntheticBundle.RgbaHalfPixels(ModBuilder.RampWidth, ModBuilder.RampHeight, seed: 19),
            Format: SyntheticBundle.RgbaHalf));
        byte[] clothRampBytes = File.ReadAllBytes(clothRampPath);

        var model = Assert.IsType<SubjectModel>(env.ResolveSubject("Vesna", "VesnaSSR01"));
        var body = model.Parts.Single(part => part.Token == "body");
        var cloth = model.Parts.Single(part => part.Token == "cloth");
        var bodyMaterial = Assert.Single(body.Materials);
        var clothMaterial = Assert.Single(cloth.Materials);
        var bodyBase = bodyMaterial.Maps.Single(map => MaterialResolver.IsBaseColor(map.Slot));
        var sharedBlend = bodyMaterial.Maps.Single(map => MaterialResolver.IsBlend(map.Slot));
        var clothBase = clothMaterial.Maps.Single(map => MaterialResolver.IsBaseColor(map.Slot));
        var clothRamp = new SubjectMap("_RampMap", "tex_cloth_ramp", "bundleClothRamp");
        var shaped = model with
        {
            Parts = model.Parts.Select(part => part.Token switch
            {
                "body" => part with
                {
                    Materials = new[]
                    {
                        bodyMaterial,
                        new SubjectMaterial("m_body_other", 0, "cab-body-other", new[] { bodyBase }),
                    },
                },
                "cloth" => part with
                {
                    Materials = new[]
                    {
                        clothMaterial with { Maps = new[] { clothBase, sharedBlend, clothRamp } },
                        new SubjectMaterial("m_cloth_other", 0, "cab-cloth-other", new[] { clothBase }),
                    },
                },
                _ => part,
            }).ToArray(),
        };
        var resolve = env.ResolveSubject;
        var deobfuscate = env.Deobfuscate;
        return (env with
        {
            ResolveSubject = (character, stem) => character == "Vesna" && stem == "VesnaSSR01"
                ? shaped : resolve(character, stem),
            Deobfuscate = bundle => bundle == "bundleClothRamp"
                ? clothRampBytes : deobfuscate(bundle),
        }).Exact();
    }

    /// <summary>The workspace PNG a donor row references for one of the second outfit's game textures, and
    /// the texture target that names the asset behind it — the record the ramp join reads. Returns the
    /// project-relative file name the donor row carries.</summary>
    private string AddDonorStockPng(ModProject p, string textureName, string file)
    {
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(30, 60, 90, 255)))
            img.SaveAsPng(Path.Combine(_proj, file));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bundlePaloma", ObjectName = textureName,
            ReplaceFile = file, SubjectCharacter = "Paloma", SubjectOutfit = "PalomaAA01",
        });
        return file;
    }

    /// <summary>The same, for a texture target whose subject the content policy refuses — what the build's
    /// own refusal is proved against when the question is WHICH slots it walks.</summary>
    private string AddBlockedStockPng(ModProject p, string file)
    {
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(40, 40, 40, 255)))
            img.SaveAsPng(Path.Combine(_proj, file));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bundleOther", ObjectName = "tex_other_d",
            ReplaceFile = file, SubjectCharacter = "Helena", SubjectOutfit = "HelenaNPC01",
        });
        return file;
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

    /// <summary>A ramp-shaped fp16 DDS in the workspace, returned as the project-relative name a donor row
    /// carries. <paramref name="seed"/> shifts the values so two ramps never share bytes.</summary>
    private string WriteRampDds(string file, int width = ModBuilder.RampWidth,
        int height = ModBuilder.RampHeight, int seed = 0)
    {
        var level = new byte[width * height * 8];
        for (int i = 0; i < level.Length; i += 2)
            BitConverter.TryWriteBytes(level.AsSpan(i, 2), (Half)(((i / 2 + seed) % 37) / 36f));
        using var s = File.Create(Path.Combine(_proj, file));
        DdsWriter.Write(s, 10, width, height, new[] { level });
        return file;
    }

    /// <summary>The ramp ships under the same rule the picture maps do: identity is the image's CONTENT, so
    /// several submeshes naming one ramp ship one file, declare one resource and bind that.</summary>
    [Fact]
    public void Two_submeshes_naming_one_ramp_ship_it_once()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("RampShared");
        WriteDonorGlb();
        string ramp = WriteRampDds("s0_ramp.dds");
        File.Copy(Path.Combine(_proj, ramp), Path.Combine(_proj, "s1_ramp.dds"));
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0, Ramp = ramp },
            new() { Submesh = 1, Ramp = "s1_ramp.dds" },
        });

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        var shipped = Directory.GetFiles(r.OutDir, "donor_*_ramp.dds");
        Assert.Single(shipped);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Equal(1, CountOf(ini, $"filename = {Path.GetFileName(shipped[0])}\n"));
    }

    /// <summary>A ramp is a lookup table sampled by lighting term, so its extent is part of the curve. A
    /// well-formed fp16 file of another size creates fine in the runtime and then draws wrong, which is
    /// exactly what a ship gate exists to catch.</summary>
    [Fact]
    public void A_ramp_of_the_wrong_extent_is_refused_by_name()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("RampSize");
        WriteDonorGlb();
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0, Ramp = WriteRampDds("small_ramp.dds", width: 4, height: 4) },
        });

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("can't be built", ex.Message);
        Assert.Contains("is 4x4", ex.Message);
        Assert.Contains($"{ModBuilder.RampWidth}x{ModBuilder.RampHeight}", ex.Message);
    }

    // ---- which donor sources the content refusal walks ----------------------------------------------
    // The build refuses on the subjects a donor row REACHES, and a row reaches exactly one: the first slot
    // that names a game texture, and only while its ramp is still a question. A sweep over more than that
    // fails builds that read no subject at all.

    /// <summary>A slot the derivation never asks about is not a route to another subject, so a source
    /// behind it is no reason to refuse the build.</summary>
    [Fact]
    public void A_blocked_source_behind_a_slot_the_join_never_reaches_still_builds()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, rampDonor: true);
        var p = NewProject("BlockedLaterSlot");
        WriteDonorGlb();
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new()
            {
                Submesh = 0,
                Albedo = AddDonorStockPng(p, "tex_paloma_d", "paloma_body.png"),
                Normal = AddBlockedStockPng(p, "other_body_n.png"),
            },
        });

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.True(File.Exists(Path.Combine(r.OutDir, "mod.ini")));
    }

    /// <summary>…and a row whose ramp the modder already settled reads no donor material at all, so its
    /// own first slot is no route either.</summary>
    [Fact]
    public void A_settled_ramp_row_with_a_blocked_source_still_builds()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, rampDonor: true);
        var p = NewProject("BlockedSettledRow");
        WriteDonorGlb();
        var row = new SubmeshTextures
        {
            Submesh = 0,
            Albedo = AddBlockedStockPng(p, "other_body_d.png"),
        };
        row.KeepOwnRamp();
        AddReplaceTarget(p, textures: new List<SubmeshTextures> { row });

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.True(File.Exists(Path.Combine(r.OutDir, "mod.ini")));
    }

    // ---- the ramp a donor brings with it ------------------------------------------------------------
    // Geometry grafted from another outfit was shaded by THAT outfit's toon ramp, and the anchor it draws
    // at binds its own. The build carries the donor's across, and only where the two actually differ.

    /// <summary>Record a ramp CARRIED from the outfit the donor geometry came off, on one donor row: the
    /// file in the workspace plus the game texture it was read out of. This is the shape the build meets —
    /// it ships the bytes, records the lineage and gates on the install's ramp registers — whichever surface
    /// wrote it there.</summary>
    private string CarryRamp(ModProject p, int submesh, string texture, byte[] bytes,
        string bundle = "bundlePaloma", long pathId = 4200)
    {
        string file = $"carried_s{submesh}_ramp.dds";
        using (var s = File.Create(Path.Combine(_proj, file)))
            DdsWriter.Write(s, 10, ModBuilder.RampWidth, ModBuilder.RampHeight, new[] { bytes });
        // bundle + path id IS the identity the carry read live off the other outfit's material, which is
        // what lets the conversion re-anchor a ramp this part's own materials never bind
        p.Targets.Single(t => t.AssetType == "Mesh").DonorTextures!.Single(x => x.Submesh == submesh)
            .SetRamp(file, new CarriedRamp { Bundle = bundle, Name = texture, PathId = pathId + submesh });
        return file;
    }

    /// <summary>A donor ramp's bytes: the game's own fp16 values, so what ships can be compared to what was
    /// read out of the install.</summary>
    private static byte[] DonorRampBytes(int seed = 7) =>
        SyntheticBundle.RgbaHalfPixels(ModBuilder.RampWidth, ModBuilder.RampHeight, seed);

    /// <summary>The ramp slot record of one submesh in a built mod's repair data.</summary>
    private static JsonElement RampRecord(ModBuilder.Result r, int submesh)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "repair.json")));
        return doc.RootElement.GetProperty("changes").EnumerateArray()
            .Single(c => c.GetProperty("verb").GetString() == "replace")
            .GetProperty("textures").EnumerateArray()
            .Single(t => t.GetProperty("submesh").GetInt32() == submesh)
            .GetProperty("ramp").Clone();
    }

    [Fact]
    public void A_carried_donor_ramp_ships_verbatim_and_is_recorded_as_the_game_texture_it_is()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("RampCarried");
        WriteDonorGlb();
        AddReplaceTarget(p, textures: new List<SubmeshTextures> { new() { Submesh = 0 } });
        var bytes = DonorRampBytes();
        CarryRamp(p, 0, "tex_paloma_ramp", bytes);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        var shipped = Assert.Single(Directory.GetFiles(r.OutDir, "donor_*_ramp.dds"));
        // the bytes are the game's, level for level, under the tag they were already in
        Assert.Equal(bytes, Assert.Single(DdsReader.Read(shipped).Levels));

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"filter_index = {MigotoEmitter.FilterRamp}", ini);
        Assert.Contains($"filename = {Path.GetFileName(shipped)}\n", ini);
        Assert.Contains("$zz_slot_rm = -1\n", ini);

        // the record says the shading came off another outfit and names the texture it came off, so a
        // change the modder didn't make is readable
        var record = RampRecord(r, 0);
        Assert.Equal("DonorVanilla", record.GetProperty("origin").GetString());
        Assert.Equal(Path.GetFileName(shipped), record.GetProperty("file").GetString());
        var stock = record.GetProperty("stock");
        Assert.Equal("bundlePaloma", stock.GetProperty("bundle").GetString());
        Assert.Equal("tex_paloma_ramp", stock.GetProperty("name").GetString());
    }

    /// <summary>Two donor submeshes off ONE donor material carry one ramp, and the file ships once — but
    /// both of them shade with it, so both say so. A record left off the second reads as a submesh that
    /// inherits, which is the opposite of what happened.</summary>
    [Fact]
    public void Two_submeshes_carrying_one_donor_ramp_both_record_it()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("RampCarriedShared");
        WriteDonorGlb();
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0 },
            new() { Submesh = 1 },
        });
        var bytes = DonorRampBytes();
        CarryRamp(p, 0, "tex_paloma_ramp", bytes);
        CarryRamp(p, 1, "tex_paloma_ramp", bytes);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        var shipped = Assert.Single(Directory.GetFiles(r.OutDir, "donor_*_ramp.dds"));
        foreach (int submesh in new[] { 0, 1 })
        {
            var record = RampRecord(r, submesh);
            Assert.Equal("DonorVanilla", record.GetProperty("origin").GetString());
            Assert.Equal(Path.GetFileName(shipped), record.GetProperty("file").GetString());
            Assert.Equal("tex_paloma_ramp", record.GetProperty("stock").GetProperty("name").GetString());
        }
    }

    /// <summary>An install whose slot measurement names no ramp register can bind one at no draw, so a mod
    /// asking for a ramp there refuses rather than shipping shading that silently did not change. A ramp on
    /// a replacement's own submesh is the modder's choice exactly as a pick on an installed material is, and
    /// one check covers both, which is what keeps the two answers from drifting apart.</summary>
    [Fact]
    public void A_named_ramp_is_refused_where_no_register_binds_one()
    {
        var env = MakeSkinnedEnv(anchorRamp: true) with
        {
            ShaderSlotCatalogFile = Path.Combine(_root, "not-a-catalog.json"),
        };
        var p = NewProject("RampNamedNoRegisters");
        WriteDonorGlb();
        // an authored base colour beside it, so the row is written at all and its silence about the ramp
        // is the thing under test rather than a row that never existed
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "painted.png"));
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "painted.png", Ramp = WriteRampDds("s0_ramp.dds") },
        });

        var ex = Assert.Throws<AuthoredRefusalException>(()
            => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("names no toon ramp slot", ex.Message);
        Assert.DoesNotContain(Directory.GetDirectories(_out),
            d => !Path.GetFileName(d).StartsWith('.'));
    }

    /// <summary>Resolving the subject behind a donor row is a route to another subject's model, and every
    /// route to one answers to the build's content policy. A blocked subject there fails the build, the way
    /// it does wherever else a build reaches for one — including on an install that can bind no ramp, where
    /// the row is reached for all the same and a refusal skipped would be a route around the policy.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_blocked_donor_subject_refuses_the_build(bool rampRegisters)
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        if (!rampRegisters)
            env = env with { ShaderSlotCatalogFile = Path.Combine(_root, "not-a-catalog.json") };
        var p = NewProject("RampBlockedDonor");
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(30, 60, 90, 255)))
            img.SaveAsPng(Path.Combine(_proj, "blocked_donor.png"));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bundlePaloma", ObjectName = "tex_paloma_d",
            ReplaceFile = "blocked_donor.png", SubjectCharacter = "Helena", SubjectOutfit = "HelenaNPC01",
        });
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "blocked_donor.png" },
        });

        // the build reaches the recorded strings for the ramp source whether or not this install can bind
        // a ramp at all, so the refusal is the same either way
        var ex = Assert.Throws<BlockedAssetException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("not a supported asset", ex.Message);
    }

    [Fact]
    public void The_production_builder_refuses_a_blocked_donor_source_without_the_released_test_sweep()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("RampBlockedDonorProduction");
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(30, 60, 90, 255)))
            img.SaveAsPng(Path.Combine(_proj, "blocked_donor_production.png"));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bundlePaloma", ObjectName = "tex_paloma_d",
            ReplaceFile = "blocked_donor_production.png", SubjectCharacter = "Helena",
            SubjectOutfit = "HelenaNPC01",
        });
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "blocked_donor_production.png" },
        });
        // Adapt and plan without ReleasedBuild.Build: its private sweep must not pre-empt the production
        // RefuseBlockedDonorSources call this regression test exists to protect. Keep the full workspace
        // slot join the released boundary normally supplies, because that join is what records the donor.
        var resolver = new LegacyProjectResolver(env);
        var adaptation = LegacyProjectAdapter.Adapt(p, resolver.ResolvePart, resolver.RosterSlots);
        Assert.True(adaptation.Report.CanSave,
            string.Join("; ", adaptation.Report.Items.Select(item => item.Detail)));
        // The native authored workspace records this materialized picture directly. The released legacy
        // boundary's private sweep reads LegacyTargets before adaptation, so create the production shape
        // explicitly and make the production join independently load-bearing.
        adaptation.Project.WorkspaceIndex!.Records.Add(new AuthoredWorkspaceRecord
        {
            Id = "workspace-blocked-donor",
            Kind = ProjectAssetKind.Picture,
            Part = new TargetPart
            {
                Subject = "Helena", Outfit = "HelenaNPC01", RendererSlot = "donor_material",
            },
            GameAsset = new GameAssetRef
            {
                GameBuild = "12345", LogicalBundle = "bundlePaloma", PathId = 1,
                Name = "tex_paloma_d",
            },
            ProjectFile = "blocked_donor_production.png",
        });
        var plan = AuthoredBuildPlanner.Plan(adaptation.Project,
            new ProductionAuthoredBuildBackend(resolver.ResolvePart));
        var execution = AuthoredBuildExecution.Create(adaptation.Project, plan);
        var donorRow = Assert.Single(execution.Work.SelectMany(item => item.Textures
            ?? Array.Empty<SubmeshTextures>()));
        Assert.False(RampConversion.RampSettled(donorRow));
        var donor = Assert.IsType<(TargetPart Part, DonorMapSlot Kind)>(
            RampConversion.DonorSourceOf(new AuthoredWorkspaceFacts(adaptation.Project), donorRow));
        Assert.Equal("Helena", donor.Part.Subject);

        var ex = Assert.Throws<BlockedAssetException>(() =>
            ModBuilder.Build(execution, env, _out, zip: false));

        Assert.Contains("not a supported asset", ex.Message);
    }

    /// <summary>Two donor submeshes carrying DIFFERENT ramps each ship their own file and record their own
    /// lineage: one file for the two would shade both submeshes with whichever curve was walked first.</summary>
    [Fact]
    public void Two_donor_submeshes_off_different_materials_each_carry_their_own_ramp()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("RampTwo");
        WriteDonorGlb();
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0 },
            new() { Submesh = 1 },
        });
        CarryRamp(p, 0, "tex_paloma_ramp", DonorRampBytes());
        CarryRamp(p, 1, "tex_paloma2_ramp", DonorRampBytes(9));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Equal(2, Directory.GetFiles(r.OutDir, "donor_*_ramp.dds").Length);
        Assert.Equal("tex_paloma_ramp", RampRecord(r, 0).GetProperty("stock").GetProperty("name").GetString());
        Assert.Equal("tex_paloma2_ramp", RampRecord(r, 1).GetProperty("stock").GetProperty("name").GetString());
        // each submesh's draw binds its own file, so the two are not collapsed
        string s0 = RampRecord(r, 0).GetProperty("file").GetString()!;
        string s1 = RampRecord(r, 1).GetProperty("file").GetString()!;
        Assert.NotEqual(s0, s1);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        foreach (var file in new[] { s0, s1 }) Assert.Contains($"filename = {file}\n", ini);
    }

    // ---- a ramp picked on a part this mod does NOT replace ------------------------------------------
    // Nothing about the part moves but its shading, so there is no geometry, no encode and no stock
    // texture to override: the pick becomes a draw-scoped bind at the part's own draws.

    /// <summary>Record a ramp pick on the one-part world's body material.</summary>
    private void PickStockRamp(ModProject p, string ramp, string material = "m_body") =>
        p.SetStockRamp("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", material, ramp);

    [Theory]
    [InlineData("recorded-pick")]
    [InlineData("resolved-subject")]
    [InlineData("resolved-part")]
    public void A_blocked_name_anywhere_on_a_stock_ramp_pick_refuses_the_production_build(string route)
    {
        var plannedEnv = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("BlockedStockRamp" + route);
        PickStockRamp(p, WriteRampDds("blocked_pick_" + route + ".dds"));
        var execution = Authored(p, plannedEnv, _ => { });
        var pick = Assert.Single(execution.StockRamps);
        var buildEnv = plannedEnv;
        var cleanModel = plannedEnv.ResolveSubject("Vesna", "VesnaSSR01")!;

        if (route == "recorded-pick")
            pick.Character = "Helena";
        else if (route == "resolved-subject")
            buildEnv = plannedEnv with
            {
                ResolveSubject = (_, _) => cleanModel with { Character = "Helena" },
            };
        else
            buildEnv = plannedEnv with
            {
                ResolveSubject = (_, _) => cleanModel with
                {
                    Parts = cleanModel.Parts.Select(part => part with
                    {
                        MeshAddress = part.SlotName == pick.Mesh ? "c_Helena_body_lod0" : part.MeshAddress,
                    }).ToList(),
                },
            };

        var ex = Assert.Throws<BlockedAssetException>(() =>
            ModBuilder.Build(execution, buildEnv, _out, zip: false));

        Assert.Contains("not a supported asset", ex.Message);
    }

    /// <summary>The released catalog shape before BlendTex was measured.</summary>
    private string CatalogWithoutBlend()
    {
        var stored = JsonNode.Parse(File.ReadAllText(LabPaths.ShaderSlotCatalogFile))!.AsObject();
        Assert.True(stored["inputs"]!.AsObject().Remove(ShaderSlotCatalog.BlendTex));
        string path = Path.Combine(_root, "charps_slots_without_blend.json");
        File.WriteAllText(path, stored.ToJsonString());
        return path;
    }

    [Fact]
    public void A_ramp_picked_on_an_unreplaced_part_binds_at_that_parts_own_draws()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRamp");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        // the material is sighted by its own base colour, and the ramp says which register holds one
        Assert.Contains($"filter_index = {MigotoEmitter.RetexTag(_stockTexHash)}", ini);
        Assert.Contains($"filter_index = {MigotoEmitter.FilterRamp}", ini);
        Assert.Contains("$zz_srm = 0\n", ini);
        Assert.Contains("$zz_slot_rm = -1\n", ini);
        Assert.Contains("if $zz_srm == 1\n", ini);
        // one section per rendered tier of the part, and the picked bytes ship verbatim
        Assert.Equal(2, CountOf(ini, "[TextureOverride_RetexScope_"));
        var shipped = Assert.Single(Directory.GetFiles(r.OutDir, "stockramp_*.dds"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_proj, "picked_ramp.dds")), File.ReadAllBytes(shipped));
        Assert.Empty(r.Warnings);
    }

    /// <summary>A material with no toon ramp of its own has no register for one to land in, so the plan
    /// judges the pick unsupported and the build refuses by name rather than writing a folder the pick is
    /// silently absent from.</summary>
    [Fact]
    public void A_pick_on_a_material_that_binds_no_ramp_is_refused_by_name()
    {
        var env = MakeSkinnedEnv();   // no anchorRamp: the body material states none
        var p = NewProject("StockRampNoRamp");
        // something else that would have shipped, so the refusal is the only thing under test rather
        // than a mod that turned out to carry nothing at all
        AddEditedTexture(p);
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        // ③ lists the planner's own reason and disables Build; this route is the backstop behind that,
        // so the reason rides the exception into the build log rather than onto the footer.
        Assert.Contains("draws without a toon ramp",
            string.Join("; ", BuildLogDiagnostics.From(ex)));
        Assert.Empty(Directory.GetFileSystemEntries(_out));
    }

    [Fact]
    public void Authored_production_plan_executes_through_the_real_builder()
    {
        var env = MakeEnv(out _, out _);
        var originalModel = env.ResolveSubject("Vesna", "VesnaSSR01")!;
        long texturePath = new Remold.Core.Bundles.BundleReader()
            .ListAssets(env.Deobfuscate("bundleT")!, Remold.Core.Bundles.BundleReader.ClassTexture2D)
            .Single(asset => asset.Name == "tex_body_d").PathId;
        var originalPart = Assert.Single(originalModel.Parts);
        var originalMaterial = Assert.Single(originalPart.Materials);
        var exactMaterial = originalMaterial with
        {
            Bundle = "bundle0", PathId = 901,
            Maps = originalMaterial.Maps.Select(map => map with { PathId = texturePath }).ToArray(),
        };
        var exactPart = originalPart with
        {
            Materials = new[] { exactMaterial }, RendererBundle = "bundle0", RendererPathId = 900,
            SiblingTiers = originalPart.SiblingTiers?.Select((tier, i) => tier with
            {
                RendererBundle = "bundle0", RendererPathId = 950 + i,
            }).ToArray(),
        };
        var exactModel = originalModel with { Parts = new[] { exactPart } };
        env = env with
        {
            ResolveSubject = (character, outfit) => character == "Vesna" && outfit == "VesnaSSR01"
                ? exactModel : null,
        };
        var legacy = NewProject("Authored production");
        AddEditedTexture(legacy);
        var resolver = new LegacyProjectResolver(env);
        var adaptation = LegacyProjectAdapter.Adapt(legacy, resolver.ResolvePart);
        Assert.True(adaptation.Report.CanSave,
            string.Join("; ", adaptation.Report.Items.Select(item => item.Detail)));
        var plan = AuthoredBuildPlanner.Plan(adaptation.Project,
            new ProductionAuthoredBuildBackend(resolver.ResolvePart));
        var execution = AuthoredBuildExecution.Create(adaptation.Project, plan);

        var result = ModBuilder.Build(execution, env, _out, zip: false);

        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts));
        Assert.Contains("[TextureOverride_Retex_", File.ReadAllText(Path.Combine(result.OutDir, "mod.ini")));
    }

    [Fact]
    public void A_replace_on_a_blendshaped_mesh_blocks_at_plan_altitude_and_names_the_mesh_reason()
    {
        // The wiring under test is the App's: a MeshEditGate over the install judges the geometry slot,
        // and the plan — not the emitter throw — is where the refusal reaches the ③ page, named per edit.
        var env = WithExactIdentities(MakeSkinnedEnv(facePart: true));
        var legacy = NewProject("FaceReplace");
        WriteDonorGlb();
        WriteDonorGlb("donor-face.glb", FaceBones);
        AddReplaceTarget(legacy);
        AddReplaceTarget(legacy, "c_vesna01_face_lod0");
        legacy.Targets[^1].ReplaceFile = "donor-face.glb";
        var resolver = new LegacyProjectResolver(env);
        var adaptation = LegacyProjectAdapter.Adapt(legacy, resolver.ResolvePart);
        Assert.True(adaptation.Report.CanSave,
            string.Join("; ", adaptation.Report.Items.Select(item => item.Detail)));
        var gate = new Remold.Core.Workbench.MeshEditGate(env.Deobfuscate);
        var backend = new ProductionAuthoredBuildBackend(resolver.ResolvePart,
            meshReplaceBlock: slot => slot.Mesh is { } mesh
                && gate.Blocked(mesh.LogicalBundle, mesh.Name ?? "", mesh.PathId) is { } why
                    ? Remold.Core.Workbench.PartSkinGate.PlanRefusal(why) : null);

        var plan = AuthoredBuildPlanner.Plan(adaptation.Project, backend);

        Assert.False(plan.CanBuild);
        string faceEdit = adaptation.Project.EditDefinitions
            .Single(edit => edit.Target.RendererSlot == "c_vesna01_face_lod0").Id;
        var blocked = plan.Bindings.Where(binding => binding.Decision.BlocksBuild).ToList();
        Assert.NotEmpty(blocked);
        Assert.All(blocked, binding => Assert.Equal(faceEdit, binding.EditDefinitionId));
        Assert.All(blocked, binding => Assert.Equal(BuildPlanVerdict.Unsupported,
            binding.Decision.Verdict));
        Assert.Contains(blocked, binding =>
            binding.Decision.Reason.Contains("expressions", StringComparison.Ordinal));
        // The healthy body Replace passes the same gate untouched.
        string bodyEdit = adaptation.Project.EditDefinitions
            .Single(edit => edit.Target.RendererSlot == "c_vesna01_body_lod0").Id;
        Assert.DoesNotContain(plan.Bindings, binding =>
            binding.EditDefinitionId == bodyEdit && binding.Decision.BlocksBuild);
    }

    [Fact]
    public void Identical_material_values_on_two_replaces_both_reach_the_emitted_mod()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(poolMate: true));
        var legacy = NewProject("Two material patches");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        WriteDonorGlb("donor-mate.glb", BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(legacy);
        AddReplaceTarget(legacy, "c_vesna01_mate_lod0");
        legacy.Targets[^1].ReplaceFile = "donor-mate.glb";
        var resolver = new LegacyProjectResolver(env);
        var adaptation = LegacyProjectAdapter.Adapt(legacy, resolver.ResolvePart);
        Assert.True(adaptation.Report.CanSave,
            string.Join("; ", adaptation.Report.Items.Select(item => item.Detail)));
        var session = new AuthoredEditSession(adaptation.Project);
        session.SetRootDir(_proj);
        var body = Slot("c_vesna01_body_lod0");
        var mate = Slot("c_vesna01_mate_lod0");

        Author(body);
        Author(mate);
        var project = session.Snapshot();
        var backend = new ProductionAuthoredBuildBackend(resolver.ResolvePart, _ =>
            new MaterialRenderEvidence("proved-material-family",
                new[] { "45dbffd6cb513d80" }, 4978303,
                MaterialValueCatalog.UnityPerMaterial544,
                new[]
                {
                    new BuildMaterialValueField(MaterialValueSemantics.UseGiFlatten, 2, 492,
                        "the reflected field matches the active material layout"),
                },
                "the active shader family proves the material field and carrier layout"));
        var plan = AuthoredBuildPlanner.Plan(project, backend);
        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts));
        var patchOutputs = plan.OutputArtifacts.Where(output => string.Equals(
            output.Artifact.Purpose, MaterialValueBuildSupport.OutputPurpose,
            StringComparison.Ordinal)).ToList();
        Assert.Equal(2, patchOutputs.Count);
        Assert.Single(patchOutputs.Select(output => output.Artifact.FunctionalIdentity).Distinct());

        var result = ModBuilder.Build(AuthoredBuildExecution.Create(project, plan), env, _out,
            zip: false);

        string ini = File.ReadAllText(Path.Combine(result.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        foreach (string suffix in new[] { "vesna_body", "vesna_mate" })
        {
            string draw = IniSection(ini, $"[CommandListDraw_{suffix}]");
            Assert.Equal(1, CountOf(draw, $"local $zz_material_ps_{suffix}_s0 = ps"));
            Assert.Equal(2, CountOf(draw, $"$zz_material_ps_{suffix}_s0 = ps"));
            Assert.Contains($"Resource_MaterialSource_{suffix}_s0", ini);
        }
        Assert.Equal(2, Directory.GetFiles(result.OutDir, "material_patch_*.hlsl",
            SearchOption.AllDirectories).Length);

        void Author(TargetPart target)
        {
            string edit = session.Snapshot().EditDefinitions.Single(candidate =>
                candidate.Target.SameAs(target)).Id;
            string slot = session.EnsureMaterialValueSlot(target, 0,
                MaterialValueSemantics.UseGiFlatten, resolver.ResolvePart);
            session.ChooseMaterialValue(edit, slot, "0");
        }
    }

    // ---- what a plan's gate says, read by every emission that answers to one ------------------------

    /// <summary>A part a key group's state takes off screen, whose only change is the game-wide texture
    /// rebind. The rebind replaces a resource wherever it is sampled and gates on nothing — while the part
    /// draws, it draws retextured. What the hiding position does is skip the part's OWN draw, which is the
    /// same guarded skip a part with no content of its own emits, on the anchor its plan resolves.</summary>
    [Fact]
    public void A_group_hidden_part_whose_change_is_a_game_wide_retexture_skips_its_own_draw()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(poolMate: true));
        var p = NewProject("HiddenGameWideRetex");
        WriteDonorGlb(bones: MateBones);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        AddEditedTexture(p);

        var r = ModBuilder.Build(
            Authored(p, env, HideTheBodyFrom("c_vesna01_mate_lod0")), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        // the retexture ships exactly as it did: a rebind of the stock resource, under no gate
        Assert.Contains("[TextureOverride_Retex_", ini);
        string retex = IniSection(ini, "[TextureOverride_Retex_");
        Assert.Contains("this = Resource_Rtx0\n", retex);
        Assert.DoesNotContain("if $zz_key", retex);
        // and the hiding position skips the part's own draw
        string hide = IniSection(ini, "[TextureOverride_Hide_0]");
        Assert.Contains("if $zz_key_f7 == 1\nhandling = skip\nendif\n", hide);
        Assert.Single(Regex.Matches(hide, Regex.Escape("handling = skip")));
        // nothing reads a hider flag here — a texture edit has no draw gate to read one from — so none
        // is declared or recomputed
        Assert.DoesNotContain("$zz_hid_", ini);
    }

    /// <summary>The same hide over a DRAW-SCOPED retexture. The probe and its bind are gated as before,
    /// and the hiding position's skip joins them in the one section that draw's hash owns.</summary>
    [Fact]
    public void A_group_hidden_part_whose_retexture_is_draw_scoped_skips_beside_its_probe()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(poolMate: true, clothWearer: true));
        env = env with
        {
            Sharing = Measured(new Dictionary<string, int[]>(), new Dictionary<int, string[]>(),
                new Dictionary<string, int[]> { [_stockTexHash] = new[] { 0, 1 } }),
        };
        var p = NewProject("HiddenScopedRetex");
        WriteDonorGlb(bones: MateBones);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        AddEditedTexture(p);

        var r = ModBuilder.Build(
            Authored(p, env, HideTheBodyFrom("c_vesna01_mate_lod0")), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string hide = IniSection(ini, "[TextureOverride_Hide_0]");
        Assert.Contains("if $zz_key_f7 == 1\nhandling = skip\nendif\n", hide);
        // the scoped probe and bind fold into that same section rather than minting a second one
        Assert.Contains("= Resource_Rtx0", hide);
        Assert.DoesNotContain("[TextureOverride_RetexScope_", ini);
        Assert.DoesNotContain("$zz_hid_", ini);
    }

    /// <summary>A group with no vanilla answer at all: the part's texture edit in one position, off
    /// screen in the other. A REPLACEMENT there needs no per-state skip — its own draw suppresses the
    /// vanilla wherever it draws, which is what the released hide-while-off shape says — but a retexture
    /// suppresses nothing, so the hiding position keeps a guarded skip of its own.</summary>
    [Fact]
    public void A_retextured_part_hidden_in_its_groups_other_position_still_skips_there()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("RetexOrHide");
        AddEditedTexture(p);

        var r = ModBuilder.Build(Authored(p, env, project =>
        {
            var body = Slot("c_vesna01_body_lod0");
            Anchor(project, body);
            project.Keyed(body, "F7", offState: CompositionState.Hidden);
        }), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string hide = IniSection(ini, "[TextureOverride_Hide_0]");
        Assert.Contains("if $zz_key_f7 == 1\nhandling = skip\nendif\n", hide);
        Assert.Single(Regex.Matches(hide, Regex.Escape("handling = skip")));
        Assert.Contains("[TextureOverride_Retex_", ini);
    }

    /// <summary>The same foreign hide over a retextured part the replacement also POOLS. Pooling holds
    /// the part's vanilla draw running — the retexture repaints it, and the pipeline suppresses only what
    /// it replaces — while the hide section loop leaves pooled slots to the capture sections. So the
    /// hiding position has exactly one place to land: the capture section that draw already owns, beside
    /// whatever the pipeline's own gate put there. Without it the authored hide does nothing at all, and
    /// nothing says so.</summary>
    [Fact]
    public void A_hidden_retextured_part_the_replacement_pools_skips_in_its_capture_section()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(poolMate: true));
        var p = NewProject("HiddenPooledRetex");
        // the donor rides BOTH parts' bones, so the mate's Replace pools the retextured body
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        AddEditedTexture(p);

        var r = ModBuilder.Build(
            Authored(p, env, HideTheBodyFrom("c_vesna01_mate_lod0")), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        // pooled, so the body has no hide section of its own to carry the skip
        Assert.DoesNotContain("[TextureOverride_Hide_", ini);
        // and the hiding position skips it in the one section its draw does own
        string cap = IniSection(ini, "[TextureOverride_Cap_vesna_body]");
        Assert.Contains("if $zz_key_f7 == 1\nhandling = skip\nendif\n", cap);
        // once: the pipeline's own gate suppresses what it REPLACES, and the body is not that
        Assert.Single(Regex.Matches(cap, Regex.Escape("handling = skip")));
        // the retexture ships exactly as it did — a rebind of the stock resource, under no gate
        string retex = IniSection(ini, "[TextureOverride_Retex_");
        Assert.Contains("this = Resource_Rtx0\n", retex);
        Assert.DoesNotContain("if $zz_key", retex);
    }

    /// <summary>THE acceptance specimen for a part answered three ways: its replacement at position 0, the
    /// game's own at position 1, nothing at position 2. Every suppression the part takes is gated on the
    /// position that asks for it — the replacement's own skip where the donor draws, the hide's skip where
    /// the part is off screen — and position 1 is left alone, which is what makes the vanilla draw come
    /// back. Held at the part's lod0 AND at a tier, because LOD choice is not distance-only: a tier the
    /// hide missed would put the part back the moment the renderer picked it.</summary>
    [Fact]
    public void A_part_answered_three_ways_is_suppressed_only_in_the_positions_that_ask()
    {
        var env = WithExactIdentities(MakeSkinnedEnv());
        var p = NewProject("ThreeWays");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(Authored(p, env,
                project => LongCycle(project, Slot("c_vesna01_body_lod0"), "F7")),
            env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        // the key cycles three positions and launches at the first
        Assert.Contains("global $zz_key_f7 = 0\n", ini);
        Assert.Contains("if $zz_key_f7 == 3\n", ini);
        foreach (string section in new[]
                 {
                     "[TextureOverride_Cap_vesna_body]", "[TextureOverride_Cap_vesna_body_lod1]",
                 })
        {
            var guards = SkipGuards(SectionBody(ini, section));
            Assert.Equal(2, guards.Count);
            // the replacement's own: the vanilla draw steps aside exactly where the donor draws
            Assert.Contains(guards, g => g.Contains("if $zz_key_f7 == 0"));
            // and the hide's: off screen in position 2, and nowhere else
            Assert.Contains(guards, g => g.Contains("if $zz_key_f7 == 2"));
            Assert.DoesNotContain(guards, g => g.Length == 0);
            Assert.DoesNotContain(guards, g => g.Contains("if $zz_key_f7 == 1"));
        }
    }

    /// <summary>The same account for a part with no content of its own that a replacement POOLS. The
    /// pooling pipeline captures its draw for recovery, so the hide has one section to land in — and it
    /// lands there as a guarded skip per hiding position, not as the pipeline's own suppression. The part
    /// keeps drawing in every position nobody hid it in.</summary>
    [Fact]
    public void A_pooled_parts_hide_gated_to_one_position_skips_only_there()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(poolMate: true));
        var p = NewProject("PooledGatedHide");
        // the donor rides BOTH parts' bones, so the mate's Replace pools the body
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", hidden: true);

        var r = ModBuilder.Build(Authored(p, env, project =>
        {
            var body = Slot("c_vesna01_body_lod0");
            string hide = project.EditDefinitions.Single(edit =>
                edit.Kind == EditDefinitionKind.Hide && edit.Target.SameAs(body)).Id;
            project.Always.Remove(hide);
            project.Keyed(Slot("c_vesna01_mate_lod0"), "F7").States[1].ActiveEditIds.Add(hide);
        }), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        // pooled, so the body has no hide section of its own to carry the skip
        Assert.DoesNotContain("[TextureOverride_Hide_", ini);
        var guards = SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_body]"));
        // exactly one, on the position that hides it: the pipeline suppresses what it REPLACES, and the
        // body is not that
        Assert.Equal(new[] { "if $zz_key_f7 == 1" }, Assert.Single(guards));
    }

    /// <summary>A released project's starts-off key, adapted. The key launches where its group's first
    /// position is and the change answers the SECOND, so the mod ships with the change off and the first
    /// press turns it on — the launch the released registry recorded, said by position rather than by a
    /// flag beside it.</summary>
    [Fact]
    public void A_released_starts_off_key_keeps_its_launch_through_the_adapter()
    {
        var env = MakeSkinnedEnv();
        var p = NewProject("ReleasedStartsOff");
        WriteDonorGlb();
        AddReplaceTarget(p);
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, "F7",
            startsOff: true);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains("global $zz_key_f7 = 0\n", ini);
        // two positions, stepped and wrapped the one way every key is
        Assert.Contains("$zz_key_f7 = $zz_key_f7 + 1\nif $zz_key_f7 == 2\n$zz_key_f7 = 0\nendif\n", ini);
        // and the content answers the position the launch is NOT in
        var guards = SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_body]"));
        Assert.Equal(new[] { "if $zz_key_f7 == 1" }, Assert.Single(guards));
        Assert.Contains("if $zz_key_f7 == 1\n", SectionBody(ini, "[TextureOverride_Cap_vesna_body]"));
    }

    // ---- the content flag, from the plan that mints it to the text that reads it -------------------

    /// <summary>One change answering TWO positions of a three-position group, with a geometry replacement
    /// for content. The two positions are one work item — one pipeline, one payload, one draw chain — and
    /// what stands in for a position term in its gate is the content flag, raised in each of the two
    /// positions by the same recompute the hider flags use. The plan layer proves the flag is minted and
    /// the emitter proves it is rendered; this is the builder in between.</summary>
    [Fact]
    public void A_replacement_answering_two_positions_ships_once_under_its_content_flag()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(poolMate: true));
        var p = NewProject("ShownReplace");
        WriteDonorGlb(bones: MateBones);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        AddEditedTexture(p);

        var r = ModBuilder.Build(Authored(p, env, project => TwoPositionsOneChange(project,
            Slot("c_vesna01_mate_lod0"), Slot("c_vesna01_body_lod0"), "F7")), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        const string flag = "$zz_shw_vesna_vesnassr01_c_vesna01_mate_lod0";
        // declared at load, beside the key it is computed from
        Assert.Contains($"global {flag} = 0\n", ini);
        // and recomputed there: cleared, then raised in each position that answers with this change
        Assert.Contains($"{flag} = 0\nif $zz_key_f7 == 0\n{flag} = 1\nendif\n"
            + $"if $zz_key_f7 == 2\n{flag} = 1\nendif\n",
            IniSection(ini, "[CommandListRecomputeHidden]"));
        // ONE pipeline for the two positions, not one per position
        Assert.Single(Regex.Matches(ini, Regex.Escape("[TextureOverride_Cap_vesna_mate]")));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[CommandListDraw_vesna_mate]")));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[CustomShaderSkin_vesna_mate]")));
        // and one payload: the compiled streams ship under one name each
        Assert.Single(Directory.GetFiles(r.OutDir, "combined_ib_*.buf"));
        Assert.Single(Directory.GetFiles(r.OutDir, "combined_bind_*.buf"));
        // the suppression and the draw chain beside it both read the flag
        string cap = IniSection(ini, "[TextureOverride_Cap_vesna_mate]");
        Assert.Contains($"if {flag} == 1\nhandling = skip\nendif\n", cap);
        Assert.Contains($"if {flag} == 1\nif $zz_done_vesna_mate == 0\n", cap);
        Assert.Contains("run = CommandListDraw_vesna_mate\n", cap);
        // and neither names a position: the flag IS the or-of-positions, and one of them named beside
        // it would gate the draw down to that one
        Assert.Empty(GatesReadingBothAContentFlagAndAKey(ini));
    }

    /// <summary>The same shape with the game-wide texture rebind for content. A retexture has no draw
    /// chain to gate, so what reads the flag is the bind itself — the one route whose whole gate is a
    /// single line, and the one most easily left always on.</summary>
    [Fact]
    public void A_retexture_answering_two_positions_binds_under_its_content_flag()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(poolMate: true));
        var p = NewProject("ShownRetex");
        WriteDonorGlb(bones: MateBones);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        AddEditedTexture(p);

        var r = ModBuilder.Build(Authored(p, env, project => TwoPositionsOneChange(project,
            Slot("c_vesna01_body_lod0"), Slot("c_vesna01_mate_lod0"), "F7")), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        const string flag = "$zz_shw_vesna_vesnassr01_c_vesna01_body_lod0";
        Assert.Contains($"global {flag} = 0\n", ini);
        Assert.Contains($"{flag} = 0\nif $zz_key_f7 == 0\n{flag} = 1\nendif\n"
            + $"if $zz_key_f7 == 2\n{flag} = 1\nendif\n",
            IniSection(ini, "[CommandListRecomputeHidden]"));
        // one section for the stock texture, and its rebind stands under the flag rather than always on
        Assert.Single(Regex.Matches(ini, Regex.Escape("[TextureOverride_Retex_")));
        Assert.Contains($"if {flag} == 1\nthis = Resource_Rtx0\nendif\n",
            IniSection(ini, "[TextureOverride_Retex_"));
        Assert.Empty(GatesReadingBothAContentFlagAndAKey(ini));
        // the OTHER part's change answers ONE position, so it keeps its own position term and mints no
        // flag: a flag stands in for an or-of-positions and for nothing else
        Assert.Single(Regex.Matches(ini, @"global \$zz_shw_"));
        Assert.Contains("if $zz_key_f7 == 0\n", IniSection(ini, "[TextureOverride_Cap_vesna_mate]"));
    }

    /// <summary>Every emitted gate standing on a content flag that names a key position as well. The two
    /// are alternatives: a change answering one position states that position, a change answering several
    /// states the flag that is 1 in each of them, and a position named beside the flag would gate the
    /// emission down to that one position. Assignment lines are not gates, so the recompute section
    /// raising the flag under each key position is not one of these. Sound only where the mod carries no
    /// whole-mod key, whose own term rides every gate legitimately.</summary>
    private static IReadOnlyList<string> GatesReadingBothAContentFlagAndAKey(string ini)
    {
        var mixed = new List<string>();
        var open = new List<string>();
        string section = "";
        foreach (string raw in ini.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) { section = line; open.Clear(); continue; }
            if (line == "endif") { if (open.Count > 0) open.RemoveAt(open.Count - 1); continue; }
            if (!line.StartsWith("if $", StringComparison.Ordinal)) continue;
            open.Add(line);
            if (open.Any(term => term.StartsWith("if $zz_shw_", StringComparison.Ordinal))
                && open.Any(term => term.StartsWith("if $zz_key_", StringComparison.Ordinal)))
                mixed.Add($"{section}: {string.Join(" / ", open)}");
        }
        return mixed;
    }

    // ---- what repair.json says about a key group ---------------------------------------------------

    /// <summary>A group longer than on and off has no two-state projection, so <c>toggle_key</c> is
    /// silent for it. The record states the group whole instead: every position and what the part shows
    /// there, which position THIS change record carries, and where the key launches.</summary>
    [Fact]
    public void A_longer_cycle_writes_its_whole_key_group_into_the_repair_record()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("RepairLongCycle");
        AddEditedTexture(p);

        var r = ModBuilder.Build(Authored(p, env,
                project => LongCycle(project, Slot("c_vesna01_body_lod0"), "F7")),
            env, _out, zip: false);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "repair.json")));
        var change = Assert.Single(doc.RootElement.GetProperty("changes").EnumerateArray());
        // nothing projects onto the released two-state field, so it stays absent
        Assert.False(change.TryGetProperty("toggle_key", out _));
        var group = Assert.Single(change.GetProperty("key_groups").EnumerateArray());
        Assert.Equal("F7", group.GetProperty("key").GetString());
        Assert.NotEmpty(group.GetProperty("group_id").GetString()!);
        Assert.Equal(3, group.GetProperty("state_count").GetInt32());
        Assert.Equal(0, group.GetProperty("start_state").GetInt32());
        // this record carries position 0's content — the position whose answer is this change
        Assert.Equal(0, group.GetProperty("state_index").GetInt32());
        var states = group.GetProperty("states").EnumerateArray().ToArray();
        Assert.Equal(new[] { 0, 1, 2 }, states.Select(x => x.GetProperty("state").GetInt32()));
        Assert.Equal(new[] { "edit", "vanilla", "hidden" },
            states.Select(x => x.GetProperty("disposition").GetString()));
        Assert.NotEmpty(states[0].GetProperty("edit_definition_id").GetString()!);
        // a position showing the game's own value or nothing names no edit
        Assert.False(states[1].TryGetProperty("edit_definition_id", out _));
        Assert.False(change.TryGetProperty("also_hidden_by", out _));
    }

    [Fact]
    public void Content_only_in_a_later_state_keeps_donor_materials_in_the_repair_record()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("RepairLaterState");
        WriteDonorGlb();
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0 },
        });
        p.Targets[^1].OriginalVerts = 4;
        p.Targets[^1].DonorMaterials = new List<string> { "gf2_submesh0", "gf2_submesh1" };

        var r = ModBuilder.Build(Authored(p, env, project =>
        {
            var target = Slot("c_vesna01_body_lod0");
            string edit = project.Always.Single(id => project.EditDefinitions.Single(candidate =>
                candidate.Id == id).Target.SameAs(target));
            project.Always.Remove(edit);
            project.KeyGroups.Add(new KeyGroup
            {
                Id = "key-later", Key = "F7", States =
                {
                    new KeyGroupState { Id = "state-0001" },
                    new KeyGroupState { Id = "state-0002" },
                    new KeyGroupState { Id = "state-0003", ActiveEditIds = { edit } },
                },
            });
        }), env, _out, zip: false);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "repair.json")));
        var change = Assert.Single(doc.RootElement.GetProperty("changes").EnumerateArray());
        Assert.Equal(4, change.GetProperty("original_verts").GetInt32());
        Assert.NotEmpty(change.GetProperty("donor_materials").EnumerateArray());
    }

    /// <summary>A part another group's state takes off screen records that touching group directly.</summary>
    [Fact]
    public void A_foreign_hide_is_recorded_against_the_part_it_takes_off_screen()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(poolMate: true));
        var p = NewProject("RepairForeignHide");
        WriteDonorGlb(bones: MateBones);
        AddReplaceTarget(p, "c_vesna01_mate_lod0");
        AddEditedTexture(p);

        var r = ModBuilder.Build(
            Authored(p, env, HideTheBodyFrom("c_vesna01_mate_lod0")), env, _out, zip: false);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "repair.json")));
        var body = Assert.Single(doc.RootElement.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("mesh").GetString() == "c_vesna01_body_lod0");
        var group = Assert.Single(body.GetProperty("key_groups").EnumerateArray());
        Assert.Equal("F7", group.GetProperty("key").GetString());
        Assert.Equal(2, group.GetProperty("state_count").GetInt32());
        Assert.Equal(new[] { "edit", "hidden" }, group.GetProperty("states").EnumerateArray()
            .Select(state => state.GetProperty("disposition").GetString()));
        // the mate owns the key, so ITS record still carries the two-state field the older surface reads
        var mate = Assert.Single(doc.RootElement.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("mesh").GetString() == "c_vesna01_mate_lod0");
        Assert.Equal("F7", mate.GetProperty("toggle_key").GetProperty("key").GetString());
        Assert.Equal("F7", Assert.Single(mate.GetProperty("key_groups").EnumerateArray())
            .GetProperty("key").GetString());
    }

    /// <summary>A section body, up to the blank line that ends it. The header may be a prefix, for the
    /// sections whose name carries a generated part token.</summary>
    private static string IniSection(string ini, string header)
    {
        int at = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{header} is not in the emitted ini");
        return ini[at..(ini.IndexOf("\n\n", at, StringComparison.Ordinal) + 1)];
    }

    /// <summary>Two changes reaching one draw-scoped stock texture. The second lands on the section the
    /// first minted, and its bind has to read the position that answers IT — a change-key binding is empty
    /// on this route, and reading that would leave the second bind always on beside the first's gate.
    /// </summary>
    [Fact]
    public void A_second_claim_on_one_scoped_texture_binds_under_its_own_position()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        env = env with
        {
            Sharing = Measured(new Dictionary<string, int[]>(), new Dictionary<int, string[]>(),
                new Dictionary<string, int[]> { [_stockTexHash] = new[] { 0, 1 } }),
        };
        var p = NewProject("ScopedTwoClaims");
        AddEditedTexture(p);
        p.Targets[^1].Users!.Add("c_vesna01_cloth_lod0");

        // The SECOND claim cycles further than on and off, so nothing projects a change key for it to be
        // read off — its position is the plan's to state or nobody's.
        var r = ModBuilder.Build(Authored(p, env, project =>
        {
            project.Keyed(Slot("c_vesna01_body_lod0"), "F7");
            LongCycle(project, Slot("c_vesna01_cloth_lod0"), "F8");
        }), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        var ungated = ScopedBindsOutsideAnyKey(ini);
        Assert.True(ungated.Count == 0,
            $"scoped binds no key gates: {string.Join(", ", ungated)}");
        Assert.Contains("if $zz_key_f7 == 0", ini);
        Assert.Contains("if $zz_key_f8 == 0", ini);
    }

    /// <summary>Two changes reaching one GAME-WIDE stock texture on DIFFERENT keys, with one image between
    /// them. Route: ModBuilder.Build over the authored execution, whose retexture pass is what accumulates
    /// a stock hash's claims. Both keys are the author's to press, so the section carries a rebind under
    /// each; one bind alone would show the game's own picture wherever the other key stood.</summary>
    [Fact]
    public void A_game_wide_stock_texture_rebinds_under_every_key_that_claims_it()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("GameWideTwoKeys");
        AddEditedTexture(p);
        p.Targets[^1].Users!.Add("c_vesna01_cloth_lod0");

        var r = ModBuilder.Build(Authored(p, env, project =>
        {
            LongCycle(project, Slot("c_vesna01_body_lod0"), "F7");
            project.Keyed(Slot("c_vesna01_cloth_lod0"), "F8");
        }), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        // one section, because one stock hash owns one TextureOverride whatever reaches it
        Assert.Single(Regex.Matches(ini, Regex.Escape("[TextureOverride_Retex_")));
        string retex = IniSection(ini, "[TextureOverride_Retex_");
        Assert.Equal(new[] { ("$zz_key_f7", 0), ("$zz_key_f8", 0) }, GatedBinds(retex).Select(b => b.Gate));
        // one image between the two claims, so one file ships and both binds name it
        Assert.Single(GatedBinds(retex).Select(b => b.Resource).Distinct());
        Assert.Empty(r.Warnings);
    }

    /// <summary>Two changes reaching one GAME-WIDE stock texture at two positions of ONE key. Route: the
    /// same. The positions are alternatives — the key stands in one of them at a time — so both binds ship
    /// and neither costs the other anything.</summary>
    [Fact]
    public void A_game_wide_stock_texture_rebinds_in_both_positions_that_claim_it()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("GameWideTwoPositions");
        AddEditedTexture(p);
        p.Targets[^1].Users!.Add("c_vesna01_cloth_lod0");

        var r = ModBuilder.Build(Authored(p, env, project => OppositePositions(project,
            Slot("c_vesna01_body_lod0"), Slot("c_vesna01_cloth_lod0"), "F7")), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[TextureOverride_Retex_")));
        string retex = IniSection(ini, "[TextureOverride_Retex_");
        Assert.Equal(new[] { ("$zz_key_f7", 0), ("$zz_key_f7", 1) }, GatedBinds(retex).Select(b => b.Gate));
        Assert.Single(GatedBinds(retex).Select(b => b.Resource).Distinct());
        Assert.Empty(r.Warnings);
    }

    /// <summary>The same two positions of one key, each claiming a DIFFERENT image. Route: the same. The
    /// key stands in one position at a time, so the two rebinds never contend and each position shows its
    /// own picture — the composition the board seats when one part is answered differently per state.</summary>
    [Fact]
    public void A_game_wide_stock_texture_binds_its_own_image_in_each_position()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("GameWideTwoImages");
        AddEditedTexture(p, colour: (200, 10, 10, 255));                       // red, on the body
        AddEditedTexture(p, file: "skin_blue.dds", user: "c_vesna01_cloth_lod0",
            colour: (10, 10, 200, 255));                                       // blue, on the cloth

        var r = ModBuilder.Build(Authored(p, env, project => OppositePositions(project,
            Slot("c_vesna01_body_lod0"), Slot("c_vesna01_cloth_lod0"), "F7")), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[TextureOverride_Retex_")));
        var binds = GatedBinds(IniSection(ini, "[TextureOverride_Retex_"));
        Assert.Equal(new[] { ("$zz_key_f7", 0), ("$zz_key_f7", 1) }, binds.Select(b => b.Gate));
        // two positions, two images: each position binds the picture ITS part was authored with, and both
        // files ship (the encode names a shipped file after the source it came from)
        Assert.Equal(new[] { "rtx_skin_a.dds", "rtx_skin_blue_a.dds" },
            binds.Select(b => ResourceFile(ini, b.Resource)));
        Assert.All(binds, b => Assert.True(File.Exists(Path.Combine(r.OutDir, ResourceFile(ini, b.Resource)))));
        Assert.Empty(r.Warnings);
    }

    /// <summary>Two images on one GAME-WIDE stock texture in the SAME position. Route: the same. One
    /// resource, one frame, both gates open: the later rebind would hold the texture and the earlier change
    /// would go quietly missing, so the build names both claims instead.</summary>
    [Fact]
    public void A_game_wide_stock_texture_refuses_two_images_in_one_position()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("GameWideOnePosition");
        AddEditedTexture(p, colour: (200, 10, 10, 255));
        AddEditedTexture(p, file: "skin_blue.dds", user: "c_vesna01_cloth_lod0",
            colour: (10, 10, 200, 255));

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(
            Authored(p, env, project => SamePosition(project, Slot("c_vesna01_body_lod0"),
                Slot("c_vesna01_cloth_lod0"), "F7")), env, _out, zip: false));

        Assert.Equal(ModBuilder.ImageCollision("tex_body_d",
            "'c_vesna01_body_lod0' on VesnaSSR01", "'c_vesna01_cloth_lod0' on VesnaSSR01").Message,
            ex.Message);
    }

    /// <summary>A keyed image on one GAME-WIDE stock texture beside a second image no key switches. Route:
    /// the same. An always-on rebind is open in every position, so nothing separates it from the keyed one
    /// and the pair is refused exactly as two claims on one position are.</summary>
    [Fact]
    public void A_game_wide_stock_texture_refuses_a_second_image_no_key_switches()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("GameWideAlways");
        AddEditedTexture(p, colour: (200, 10, 10, 255));
        AddEditedTexture(p, file: "skin_blue.dds", user: "c_vesna01_cloth_lod0",
            colour: (10, 10, 200, 255));

        // only the BODY's change is seated in a group; the cloth's stays in the always-on list, and an
        // always-on change is what the plan reaches first
        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(
            Authored(p, env, project => project.Keyed(Slot("c_vesna01_body_lod0"), "F7")),
            env, _out, zip: false));

        Assert.Equal(ModBuilder.ImageCollision("tex_body_d",
            "'c_vesna01_cloth_lod0' on VesnaSSR01", "'c_vesna01_body_lod0' on VesnaSSR01").Message,
            ex.Message);
    }

    /// <summary>Two changes on one GAME-WIDE stock texture, each answering SEVERAL positions of one key and
    /// no position twice. Route: the same. Neither claim has a single position to gate on, so each stands
    /// on the content flag its own positions raise; the sets are disjoint, so the two flags are never up
    /// together and each picture shows in the positions that asked for it.</summary>
    [Fact]
    public void A_game_wide_stock_texture_binds_two_multi_position_claims_under_their_own_flags()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("GameWideTwoMultiPositions");
        AddEditedTexture(p, colour: (200, 10, 10, 255));                       // red, on the body
        AddEditedTexture(p, file: "skin_blue.dds", user: "c_vesna01_cloth_lod0",
            colour: (10, 10, 200, 255));                                       // blue, on the cloth

        // four positions: the body answers 0 and 2, the cloth 1 and 3
        var r = ModBuilder.Build(Authored(p, env, project => AlternatingParts(project,
            Slot("c_vesna01_body_lod0"), Slot("c_vesna01_cloth_lod0"), "F7")), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[TextureOverride_Retex_")));
        var binds = ShownBinds(IniSection(ini, "[TextureOverride_Retex_"));
        // one bind per claim, each on a content flag of its own and each naming its own picture
        Assert.Equal(2, binds.Count);
        Assert.Equal(2, binds.Select(b => b.Flag).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { "rtx_skin_a.dds", "rtx_skin_blue_a.dds" },
            binds.Select(b => ResourceFile(ini, b.Resource)));
        Assert.All(binds, b =>
            Assert.True(File.Exists(Path.Combine(r.OutDir, ResourceFile(ini, b.Resource)))));
        Assert.Empty(r.Warnings);
        AssertNoDuplicateSections(ini);
    }

    /// <summary>The same two multi-position claims, with one position answering for BOTH parts. Route: the
    /// same. That position raises both content flags, so the two rebinds are open in one frame and the
    /// later one would hold the resource — the pair is refused exactly as two claims on one position
    /// are.</summary>
    [Fact]
    public void A_game_wide_stock_texture_refuses_two_multi_position_claims_that_overlap()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("GameWideOverlap");
        AddEditedTexture(p, colour: (200, 10, 10, 255));
        AddEditedTexture(p, file: "skin_blue.dds", user: "c_vesna01_cloth_lod0",
            colour: (10, 10, 200, 255));

        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(
            Authored(p, env, project => AlternatingParts(project, Slot("c_vesna01_body_lod0"),
                Slot("c_vesna01_cloth_lod0"), "F7", overlapAt: 2)), env, _out, zip: false));

        Assert.Equal(ModBuilder.ImageCollision("tex_body_d",
            "'c_vesna01_body_lod0' on VesnaSSR01", "'c_vesna01_cloth_lod0' on VesnaSSR01").Message,
            ex.Message);
    }

    /// <summary>A keyed image on one GAME-WIDE stock texture beside a second no key switches, with the
    /// KEYED change the one seated in a group and the always-on one belonging to the other part. Route: the
    /// same. The mirror of the pair above it, which keys the body and leaves the cloth always on: an
    /// always-on rebind is open in every position whichever part owns it, so the refusal cannot depend on
    /// which of the two the plan reaches first. The claimant order is the plan's to choose and is not what
    /// this pins.</summary>
    [Fact]
    public void A_game_wide_stock_texture_refuses_an_always_on_claim_beside_a_keyed_one()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("GameWideAlwaysSecond");
        AddEditedTexture(p, colour: (200, 10, 10, 255));
        AddEditedTexture(p, file: "skin_blue.dds", user: "c_vesna01_cloth_lod0",
            colour: (10, 10, 200, 255));

        // only the CLOTH's change is seated in a group; the body's stays in the always-on list
        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(
            Authored(p, env, project => project.Keyed(Slot("c_vesna01_cloth_lod0"), "F7")),
            env, _out, zip: false));

        const string body = "'c_vesna01_body_lod0' on VesnaSSR01";
        const string cloth = "'c_vesna01_cloth_lod0' on VesnaSSR01";
        Assert.Contains(ex.Message, new[]
        {
            ModBuilder.ImageCollision("tex_body_d", body, cloth).Message,
            ModBuilder.ImageCollision("tex_body_d", cloth, body).Message,
        });
    }

    /// <summary>Two changes claiming one GAME-WIDE stock texture with ONE image, in the SAME position.
    /// Route: the same. Identity is (image, gate), so the two claims are one bind: a second line naming the
    /// same resource under the same gate would say what the first already says.</summary>
    [Fact]
    public void A_game_wide_stock_texture_binds_once_for_two_claims_on_one_image_and_position()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("GameWideOneBind");
        AddEditedTexture(p);
        p.Targets[^1].Users!.Add("c_vesna01_cloth_lod0");

        var r = ModBuilder.Build(Authored(p, env, project => SamePosition(project,
            Slot("c_vesna01_body_lod0"), Slot("c_vesna01_cloth_lod0"), "F7")), env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[TextureOverride_Retex_")));
        var binds = GatedBinds(IniSection(ini, "[TextureOverride_Retex_"));
        Assert.Equal(new[] { ("$zz_key_f7", 0) }, binds.Select(b => b.Gate));
        Assert.DoesNotContain("[Resource_Rtx1]", ini);
        Assert.Empty(r.Warnings);
    }

    /// <summary>Every <c>this = Resource_Rtx…</c> rebind of one section standing on a CONTENT FLAG, with
    /// the flag it reads, in emission order. The counterpart of <see cref="GatedBinds"/> for the claims
    /// whose several positions have no single key term to name them.</summary>
    private static IReadOnlyList<(string Flag, string Resource)> ShownBinds(string section) =>
        Regex.Matches(section, @"if (\$zz_shw_\w+) == 1\nthis = (Resource_Rtx\d+)\nendif\n")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();

    /// <summary>Every <c>this = Resource_Rtx…</c> rebind of one section with the single key term it stands
    /// under, in emission order. A rebind under no key at all, or under more than one term, is not one of
    /// these — the tests that use it are about gates of exactly one key position.</summary>
    private static IReadOnlyList<((string Var, int State) Gate, string Resource)> GatedBinds(string section) =>
        Regex.Matches(section, @"if (\$zz_key_\w+) == (\d+)\nthis = (Resource_Rtx\d+)\nendif\n")
            .Select(m => ((m.Groups[1].Value, int.Parse(m.Groups[2].Value)), m.Groups[3].Value)).ToList();

    /// <summary>The file one declared <c>[Resource_Rtx…]</c> ships.</summary>
    private static string ResourceFile(string ini, string resource) =>
        Regex.Match(ini, $@"\[{Regex.Escape(resource)}\]\nfilename = (\S+)\n").Groups[1].Value;

    /// <summary>One part answered by two DIFFERENT edits at ALTERNATING positions of a four-state group:
    /// the first at positions 0 and 2, the second at 1 and 3. Route: ModBuilder.Build over the authored
    /// execution — the pass that names a part's pipelines, and the emitter's own uniqueness check on the
    /// far side of it. Neither answer has a single position to be named after, so the positions it covers
    /// name it; naming them after the content flag instead would put a per-build identity on shipped
    /// files, and leaving them unnamed is the composition that used to fail the build.</summary>
    [Fact]
    public void Two_alternating_answers_of_one_part_are_named_by_the_positions_they_cover()
    {
        var env = MakeSkinnedEnv();
        var p = NewProject("Alternating");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(Authored(p, env, project =>
            AlternatingAnswers(project, Slot("c_vesna01_body_lod0"), "F7")), env, _out, zip: false);

        var shipped = Directory.GetFiles(r.OutDir, "combined_ib_*.buf").Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "combined_ib_vesna_body_s0_2.buf", "combined_ib_vesna_body_s1_3.buf" },
            shipped.OrderBy(name => name, StringComparer.Ordinal));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        // both answers really do stand on a content flag rather than a position term — the shape whose
        // pipelines a name read off the position term alone cannot tell apart
        Assert.Equal(2, Regex.Matches(ini, @"global \$zz_shw_").Count);
        AssertNoDuplicateSections(ini);
    }

    /// <summary>The same part answered by two different edits at ONE position each. Route: the same. One
    /// position names its answer on its own, which is what every emission that predates alternatives said
    /// and what a rebuild of such a project has to keep saying.</summary>
    [Fact]
    public void Two_single_position_answers_of_one_part_are_named_by_their_own_position()
    {
        var env = MakeSkinnedEnv();
        var p = NewProject("OneEach");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(Authored(p, env, project =>
            OneAnswerEach(project, Slot("c_vesna01_body_lod0"), "F7")), env, _out, zip: false);

        var shipped = Directory.GetFiles(r.OutDir, "combined_ib_*.buf").Select(Path.GetFileName).ToList();
        Assert.Equal(new[] { "combined_ib_vesna_body_s0.buf", "combined_ib_vesna_body_s1.buf" },
            shipped.OrderBy(name => name, StringComparer.Ordinal));
        // one position each, so neither answer mints a content flag and the name is the position itself
        Assert.DoesNotContain("$zz_shw_", File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")));
    }

    /// <summary>A part with ONE answer. Route: the same. Its shipped files carry no state in their names,
    /// whether or not a key switches the answer — the names an already-released mod rebuilds under.</summary>
    [Fact]
    public void A_part_with_one_answer_ships_files_no_state_names()
    {
        var env = MakeSkinnedEnv();
        var p = NewProject("OneAnswer");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(Authored(p, env, project =>
            project.Keyed(Slot("c_vesna01_body_lod0"), "F7")), env, _out, zip: false);

        Assert.Equal(new[] { "combined_ib_vesna_body.buf" },
            Directory.GetFiles(r.OutDir, "combined_ib_*.buf").Select(Path.GetFileName));
    }

    /// <inheritdoc cref="Two_alternating_answers_of_one_part_are_named_by_the_positions_they_cover"/>
    private static void AlternatingAnswers(AuthoredProject project, TargetPart target, string key)
    {
        var group = project.Keyed(target, key);
        string first = Assert.Single(group.States[0].ActiveEditIds);
        string second = SecondAnswer(project, first);
        group.States[1].ActiveEditIds.Add(second);
        group.States.Add(new KeyGroupState
        {
            Id = "state-0003", ActiveEditIds = new List<string> { first },
        });
        group.States.Add(new KeyGroupState
        {
            Id = "state-0004", ActiveEditIds = new List<string> { second },
        });
    }

    /// <inheritdoc cref="Two_single_position_answers_of_one_part_are_named_by_their_own_position"/>
    private static void OneAnswerEach(AuthoredProject project, TargetPart target, string key)
    {
        var group = project.Keyed(target, key);
        group.States[1].ActiveEditIds.Add(
            SecondAnswer(project, Assert.Single(group.States[0].ActiveEditIds)));
    }

    /// <summary>A SECOND content edit of one part: the same answer under a new identity, which is what an
    /// alternative on one part is. Returns its id, for the state the caller seats it in.</summary>
    private static string SecondAnswer(AuthoredProject project, string firstEditId)
    {
        var source = project.EditDefinitions.Single(edit => edit.Id == firstEditId);
        var second = new EditDefinition
        {
            Id = firstEditId + "-alt",
            Kind = source.Kind,
            Target = source.Target,
            Label = "Alternate",
            Bindings = source.Bindings.Select(binding => new Binding
            {
                SlotId = binding.SlotId,
                Kind = binding.Kind,
                ProjectAssetId = binding.ProjectAssetId,
                SourceSlot = binding.SourceSlot,
            }).ToList(),
        };
        project.EditDefinitions.Add(second);
        return second.Id;
    }

    /// <summary>One two-state group answering for BOTH parts in the SAME position: the second part's change
    /// joins the first's state rather than the one opposite it.</summary>
    private static void SamePosition(AuthoredProject project, TargetPart first, TargetPart second,
        string key)
    {
        var group = project.Keyed(first, key);
        var edits = project.EditDefinitions.ToDictionary(edit => edit.Id, StringComparer.Ordinal);
        string secondEdit = project.Always.Single(id => edits[id].Kind == EditDefinitionKind.Content
            && edits[id].Target.SameAs(second));
        project.Always.Remove(secondEdit);
        group.States[0].ActiveEditIds.Add(secondEdit);
    }

    /// <summary>The mod's own key is the whole-mod switch and the emission holds it to two positions, so a
    /// key group cycling further on that same key would wrap before it ever reached its later states. The
    /// build says so by name rather than shipping positions nobody can select. A tripwire until the Build
    /// UI stops offering the mod's key to a group.</summary>
    [Fact]
    public void A_key_group_longer_than_two_states_on_the_mods_own_key_refuses_by_name()
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("ModKeyLongCycle");
        p.Info.ToggleKey = "F7";
        AddEditedTexture(p);

        var ex = Assert.Throws<AuthoredRefusalException>(() => ModBuilder.Build(
            Authored(p, env, project => LongCycle(project, Slot("c_vesna01_body_lod0"), "F7")),
            env, _out, zip: false));

        Assert.Contains("switches the whole mod", ex.Message, StringComparison.Ordinal);
        Assert.Contains("needs its own key", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A change seated at the position of the MOD'S OWN key that switches the mod off. Route:
    /// ModBuilder.Build over the authored execution, whose gate pass reads every route's positions. Every
    /// emitted block also stands under the mod key at the position holding the mod on, so this change's
    /// gate names one variable at two values and no press can open it — refused by name rather than shipped
    /// as a section that never draws. Pinned with two images, and with one, because the emission that used
    /// to ship differed between them and neither was reachable.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_change_at_the_mod_keys_off_position_refuses_by_name(bool twoImages)
    {
        var env = WithExactIdentities(MakeSkinnedEnv(clothWearer: true));
        var p = NewProject("ModKeyOffPosition");
        p.Info.ToggleKey = "F7";
        if (twoImages)
        {
            AddEditedTexture(p, colour: (200, 10, 10, 255));
            AddEditedTexture(p, file: "skin_blue.dds", user: "c_vesna01_cloth_lod0",
                colour: (10, 10, 200, 255));
        }
        else
        {
            AddEditedTexture(p);
            p.Targets[^1].Users!.Add("c_vesna01_cloth_lod0");
        }

        // the cloth's change is the one seated at position 1 — the position where F7 has the mod off
        var ex = Assert.Throws<InvalidOperationException>(() => ModBuilder.Build(
            Authored(p, env, project => OppositePositions(project, Slot("c_vesna01_body_lod0"),
                Slot("c_vesna01_cloth_lod0"), "F7")), env, _out, zip: false));

        Assert.Equal(ModBuilder.ModKeyOffPosition("c_vesna01_cloth_lod0", "F7").Message, ex.Message);
    }

    /// <summary>One two-state group answering for both parts in opposite positions: <paramref name="first"/>
    /// shows its change at position 0 and the game's own at 1, <paramref name="second"/> the other way
    /// round. One group, because two groups may not share a key — and one key at two positions is what a
    /// collision judged on key names alone cannot see.</summary>
    private static void OppositePositions(AuthoredProject project, TargetPart first, TargetPart second,
        string key)
    {
        var group = project.Keyed(first, key);
        var edits = project.EditDefinitions.ToDictionary(edit => edit.Id, StringComparer.Ordinal);
        string secondEdit = project.Always.Single(id => edits[id].Kind == EditDefinitionKind.Content
            && edits[id].Target.SameAs(second));
        project.Always.Remove(secondEdit);
        group.States[1].ActiveEditIds.Add(secondEdit);
    }

    /// <summary>A FOUR-position group in which two parts alternate: <paramref name="first"/> answers at
    /// positions 0 and 2, <paramref name="second"/> at 1 and 3. Several positions each and none in common,
    /// so both changes stand on content flags whose sets are disjoint. <paramref name="overlapAt"/> also
    /// seats <paramref name="second"/> in one of the first's positions, which is what makes the two sets
    /// meet.</summary>
    private static void AlternatingParts(AuthoredProject project, TargetPart first, TargetPart second,
        string key, int? overlapAt = null)
    {
        var group = project.Keyed(first, key);
        string firstEdit = Assert.Single(group.States[0].ActiveEditIds);
        var edits = project.EditDefinitions.ToDictionary(edit => edit.Id, StringComparer.Ordinal);
        string secondEdit = project.Always.Single(id => edits[id].Kind == EditDefinitionKind.Content
            && edits[id].Target.SameAs(second));
        project.Always.Remove(secondEdit);
        group.States[1].ActiveEditIds.Add(secondEdit);
        AddState(group, firstEdit);                                            // position 2
        AddState(group, secondEdit);                                           // position 3
        if (overlapAt is { } shared) group.States[shared].ActiveEditIds.Add(secondEdit);
    }

    /// <summary>One more position on a group, answered by the named changes.</summary>
    private static void AddState(KeyGroup group, params string[] editIds) =>
        group.States.Add(new KeyGroupState
        {
            Id = $"state-{group.States.Count + 1:D4}",
            ActiveEditIds = editIds.ToList(),
        });

    /// <summary>Move one part into a THREE-state group: its change, the game's own, then off screen. Three
    /// positions have no on/off projection, so the compatibility surface carries no change key for it and
    /// only the plan can say where its content stands. The part also gains the unowned geometry slot a hide
    /// re-anchors onto, answered by its own edit with the game's geometry.</summary>
    private static void LongCycle(AuthoredProject project, TargetPart target, string key)
    {
        Anchor(project, target);
        var group = project.Keyed(target, key);
        group.Key = key;
        group.States.Add(new KeyGroupState
        {
            Id = "state-0003",
            ActiveEditIds = new List<string> { project.Hide(target) },
        });
    }

    private static void Anchor(AuthoredProject project, TargetPart target)
    {
        if (project.TargetSlots.Any(slot => slot.Part.SameAs(target)
                && slot.Domain == TargetSlotDomain.Game && slot.Input == TargetInputKind.Geometry))
            return;
        var known = project.TargetSlots.First(slot => slot.Part.SameAs(target));
        string id = $"slot-anchor-{target.RendererSlot}";
        project.TargetSlots.Add(new TargetSlot
        {
            Id = id,
            Part = target,
            Tier = "lod0",
            Input = TargetInputKind.Geometry,
            Domain = TargetSlotDomain.Game,
            Renderer = known.Renderer,
            Mesh = known.Mesh,
        });
        project.EditDefinitions.First(edit => edit.Target.SameAs(target)).Bindings
            .Add(new Binding { SlotId = id, Kind = BindingKind.TargetGameValue });
    }

    /// <summary>The unowned geometry slot a natively authored project carries as a part's structural
    /// anchor, plus the part edit's answer for it. The migration mints one only under the edit that owns
    /// it, and a hide re-anchors onto an unowned one.</summary>
    /// <summary>A THREE-position group whose first and last positions answer with the SAME change and
    /// whose middle shows the game's own. Two positions saying the same thing about EVERY part are refused
    /// by validation, so <paramref name="other"/> parts them: it shows its own change at position 0 and
    /// the game's own in the two beyond it.</summary>
    private static void TwoPositionsOneChange(AuthoredProject project, TargetPart target, TargetPart other,
        string key)
    {
        Anchor(project, target);
        var group = project.Keyed(target, key);
        // position 2 repeats position 0's answer — the same edit definition, not a copy of it
        group.States.Add(new KeyGroupState
        {
            Id = "state-0003",
            ActiveEditIds = new List<string> { Assert.Single(group.States[0].ActiveEditIds) },
        });
        var edits = project.EditDefinitions.ToDictionary(edit => edit.Id, StringComparer.Ordinal);
        string otherEdit = project.Always.Single(id => edits[id].Kind == EditDefinitionKind.Content
            && edits[id].Target.SameAs(other));
        project.Always.Remove(otherEdit);
        group.States[0].ActiveEditIds.Add(otherEdit);
    }

    /// <summary>Put <paramref name="owner"/>'s change on key F7 and have its off state take the body off
    /// screen too — a part one group hides while another home owns its content.</summary>
    private static Action<AuthoredProject> HideTheBodyFrom(string owner) => project =>
    {
        var body = Slot("c_vesna01_body_lod0");
        Anchor(project, body);
        project.Keyed(Slot(owner), "F7").States[1].ActiveEditIds.Add(project.Hide(body));
    };

    /// <summary>Every <c>ps-t… = Resource_Rtx…</c> bind that no key gate stands over, by section. A scoped
    /// bind under an empty gate is always on, whatever key the change was given.</summary>
    private static IReadOnlyList<string> ScopedBindsOutsideAnyKey(string ini)
    {
        var loose = new List<string>();
        var open = new List<string>();
        string section = "";
        foreach (string raw in ini.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) { section = line; open.Clear(); continue; }
            if (line == "endif") { if (open.Count > 0) open.RemoveAt(open.Count - 1); continue; }
            if (line.StartsWith("if $", StringComparison.Ordinal)) { open.Add(line); continue; }
            if (line.Contains("= Resource_Rtx", StringComparison.Ordinal)
                && !line.StartsWith("post ", StringComparison.Ordinal)
                && !open.Any(term => term.StartsWith("if $zz_key_", StringComparison.Ordinal)))
                loose.Add($"{section}: {line}");
        }
        return loose;
    }

    /// <summary>The synthetic install's own subject model given the exact identities migration and the
    /// production backend demand. The renderer and material ids are never dereferenced, so invented ones
    /// carry; every stock texture's id IS read through, so it is looked up from the bundle.</summary>
    private static BuildEnv WithExactIdentities(BuildEnv env) => env.Exact();

    /// <summary>The released project migrated, bent into the shape under test, planned and settled.</summary>
    private static AuthoredBuildExecution Authored(ModProject legacy, BuildEnv env,
        Action<AuthoredProject> shape)
    {
        var resolver = new LegacyProjectResolver(env);
        var adaptation = LegacyProjectAdapter.Adapt(legacy, resolver.ResolvePart);
        Assert.True(adaptation.Report.CanSave,
            string.Join("; ", adaptation.Report.Items.Select(item => item.Detail)));
        shape(adaptation.Project);
        var errors = AuthoredProjectValidator.Errors(adaptation.Project);
        Assert.True(errors.Count == 0, string.Join("; ", errors));
        var plan = AuthoredBuildPlanner.Plan(adaptation.Project,
            new ProductionAuthoredBuildBackend(resolver.ResolvePart));
        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts
            .Concat(plan.Bindings.Where(binding => binding.Decision.BlocksBuild)
                .Select(binding => $"{binding.RowId}: {binding.Decision.Reason}"))
            .Concat(plan.Parts.SelectMany(part => part.Operations
                .Select(operation => operation.Operation?.Decision)
                .Append(part.Suppression?.Decision)
                .Where(decision => decision?.BlocksBuild == true)
                .Select(decision => $"{part.Target.Key}: {decision!.Reason}")))));
        return AuthoredBuildExecution.Create(adaptation.Project, plan);
    }

    private static TargetPart Slot(string renderer) => new()
    {
        Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = renderer,
    };

    /// <summary>A pick the runtime has no way to aim: the material's only ordinary map is one a
    /// neighbour draws too, so a bind gated on sighting it would shade the neighbour as well. The plan
    /// judges the capability and refuses by name, which is where a build finds out it cannot ship a
    /// requested file.</summary>
    [Fact]
    public void A_pick_whose_only_identifier_is_shared_by_a_neighbor_is_refused()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, clothWearer: true, bodySharedMaterial: true);
        var p = NewProject("StockRampSharedMaterialMap");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_cloth_lod0", hidden: true);
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("shares every one of its textures",
            string.Join("; ", BuildLogDiagnostics.From(ex)));
        Assert.Empty(Directory.GetFileSystemEntries(_out));
    }

    [Fact]
    public void A_pick_uses_the_next_ordinary_map_when_the_base_colour_is_shared_by_a_neighbor()
    {
        var env = MakeSkinnedEnv(anchorNormal: true, anchorRamp: true, bodySharedMaterial: true);
        var p = NewProject("StockRampUniqueNormal");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Single(Directory.GetFiles(r.OutDir, "stockramp_*.dds"));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.DoesNotContain($"[TextureOverride_StockRampTag_{_stockTexHash}]", ini);
        Assert.Contains("if $zz_srm == 1", ini);
        Assert.Empty(r.Warnings);
    }

    /// <summary>Two sibling materials bind identical base/normal/RMO sets, so the old recognizer refused
    /// the target. Their effect overlays differ, and the target material's overlay now identifies its draw
    /// through both the production plan and the released builder.</summary>
    [Fact]
    public void Siblings_with_identical_base_normal_and_rmo_sets_are_targetable_when_their_blend_maps_differ()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, anchorBlend: true,
            bodySharedMaterial: true, bodySharedMaterialOwnBlend: true);
        var p = NewProject("StockRampUniqueBlend");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"[TextureOverride_StockRampTag_{_blendTexHash}]", ini);
        Assert.DoesNotContain($"[TextureOverride_StockRampTag_{_stockTexHash}]", ini);
        Assert.Single(Directory.GetFiles(r.OutDir, "stockramp_*.dds"));
        Assert.Empty(r.Warnings);
    }

    /// <summary>Adding a fourth recognizer does not perturb a material that has no effect overlay: its
    /// base-color recognition section remains the exact bytes the released builder emitted before.</summary>
    [Fact]
    public void A_material_without_a_blend_map_keeps_its_base_identifier_bytes()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampNoBlendRecognition");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        byte[] ini = File.ReadAllBytes(Path.Combine(r.OutDir, "mod.ini"));
        byte[] expected = Encoding.UTF8.GetBytes($"[TextureOverride_StockRampTag_{_stockTexHash}]\n"
            + $"hash = {_stockTexHash}\nfilter_index = {MigotoEmitter.RetexTag(_stockTexHash)}\n"
            + "match_priority = 100\n");
        int start = Encoding.UTF8.GetString(ini).IndexOf(
            $"[TextureOverride_StockRampTag_{_stockTexHash}]", StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Equal(expected, ini.AsSpan(start, expected.Length).ToArray());
        Assert.Equal(1, CountOf(Encoding.UTF8.GetString(ini), "[TextureOverride_StockRampTag_"));
        Assert.Empty(r.Warnings);
    }

    /// <summary>A material need not bind base color, normal or RMO in order to receive a picked ramp. When
    /// its effect overlay is the only bound recognizer, that overlay identifies the material's draws.</summary>
    [Fact]
    public void A_blend_map_as_the_only_bound_recognizer_targets_the_material()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, anchorBlend: true, bodyBlendOnly: true);
        var p = NewProject("StockRampBlendOnlyRecognition");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"[TextureOverride_StockRampTag_{_blendTexHash}]", ini);
        Assert.DoesNotContain($"[TextureOverride_StockRampTag_{_stockTexHash}]", ini);
        Assert.Single(Directory.GetFiles(r.OutDir, "stockramp_*.dds"));
        Assert.Empty(r.Warnings);
    }

    /// <summary>Two stock-ramp material probes may reuse one tag: it carries the same hash-derived value,
    /// while each bind remains anchored at its own part's index buffer.</summary>
    [Fact]
    public void Two_picks_on_different_parts_reuse_one_shared_blend_material_tag()
    {
        var env = MakeSharedBlendRampPicksEnv();
        var p = NewProject("StockRampSharedBlendAcrossParts");
        PickStockRamp(p, WriteRampDds("body_picked_ramp.dds", seed: 3));
        p.SetStockRamp("Vesna", "VesnaSSR01", "c_vesna01_cloth_lod0", "m_cloth",
            WriteRampDds("cloth_picked_ramp.dds", seed: 7));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Equal(2, Directory.GetFiles(r.OutDir, "stockramp_*.dds").Length);
        Assert.Equal(1, CountOf(ini, $"[TextureOverride_StockRampTag_{_blendTexHash}]"));
        Assert.Contains($"[TextureOverride_RetexScope_vesna_body_lod0_ramp]\nhash = "
            + $"{SkinnedIb("s0.bundle", "c_vesna01_body_lod0")}\n", ini);
        Assert.Contains($"[TextureOverride_RetexScope_vesna_cloth_lod0_ramp]\nhash = "
            + $"{SkinnedIb("sc.bundle", "c_vesna01_cloth_lod0")}\n", ini);
        Assert.Empty(r.Warnings);
    }

    /// <summary>A blend hash cannot recognize a material when the catalog names no register at which the
    /// runtime sweep could sight a blend map.</summary>
    [Fact]
    public void A_blend_only_recognizer_is_refused_when_the_catalog_names_no_blend_register()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, anchorBlend: true, bodyBlendOnly: true) with
        {
            ShaderSlotCatalogFile = CatalogWithoutBlend(),
        };
        var p = NewProject("StockRampBlendOnlyWithoutCatalogRange");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var ex = Assert.Throws<AuthoredRefusalException>(()
            => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("no other map on that material can be recognized in game", ex.Message);
        Assert.DoesNotContain(Directory.GetDirectories(_out),
            directory => !Path.GetFileName(directory).StartsWith('.'));
    }

    /// <summary>The recognizer walks input kinds, not the material's serialized texture order: base colour
    /// stays ahead of a distinct blend map, and the plan names the same resource in its proof.</summary>
    [Fact]
    public void A_unique_base_colour_is_preferred_over_a_distinct_blend_map()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, anchorBlend: true);
        var originalResolve = env.ResolveSubject;
        var model = Assert.IsType<SubjectModel>(originalResolve("Vesna", "VesnaSSR01"));
        var blendFirst = model with
        {
            Parts = model.Parts.Select(part => part.Token != "body" ? part : part with
            {
                Materials = part.Materials.Select(material => material with
                {
                    Maps = material.Maps.OrderBy(map => MaterialResolver.IsBlend(map.Slot) ? 0 : 1)
                        .ToArray(),
                }).ToArray(),
            }).ToArray(),
        };
        env = env with
        {
            ResolveSubject = (character, stem) => character == "Vesna" && stem == "VesnaSSR01"
                ? blendFirst : originalResolve(character, stem),
        };
        var p = NewProject("StockRampBaseBeforeBlend");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));
        var execution = Authored(p, env, _ => { });
        var ramp = Assert.Single(execution.Plan.Bindings, binding =>
            binding.AuthoredSlot.Input == TargetInputKind.Ramp
            && binding.EffectiveValue?.ProjectAsset?.Kind == ProjectAssetKind.Ramp);

        var r = ModBuilder.Build(execution, env, _out, zip: false);

        Assert.Contains(":bundleT:", ramp.Decision.TargetingProof!.Detail);
        Assert.DoesNotContain(":bundleBlend:", ramp.Decision.TargetingProof.Detail);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"[TextureOverride_StockRampTag_{_stockTexHash}]", ini);
        Assert.DoesNotContain($"[TextureOverride_StockRampTag_{_blendTexHash}]", ini);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void A_shared_identifier_on_another_part_is_safe_because_the_bind_is_draw_scoped()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, clothWearer: true);
        var p = NewProject("StockRampSharedAcrossParts");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Single(Directory.GetFiles(r.OutDir, "stockramp_*.dds"));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string clothIb = SkinnedIb("sc.bundle", "c_vesna01_cloth_lod0");
        Assert.DoesNotContain($"hash = {clothIb}\n", ini);
        Assert.Equal(2, CountOf(ini, "[TextureOverride_RetexScope_"));
        Assert.Empty(r.Warnings);
    }

    /// <summary>An app whose shader slot data names no ramp register binds one at no draw. A picked ramp
    /// is the modder's own explicit choice, so a build that can put it nowhere refuses by name instead of
    /// shipping a mod whose shading silently did not change — and says which of the app's own data is
    /// missing, since that is the fix.</summary>
    [Fact]
    public void A_pick_is_refused_where_no_register_binds_a_ramp()
    {
        var env = MakeSkinnedEnv(anchorRamp: true) with
        {
            ShaderSlotCatalogFile = Path.Combine(_root, "not-a-catalog.json"),
        };
        var p = NewProject("StockRampNoRegisters");
        // something else to ship, so the refusal is the only thing under test rather than
        // a mod that turned out to carry nothing at all
        AddEditedTexture(p);
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var ex = Assert.Throws<AuthoredRefusalException>(()
            => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("names no toon ramp slot", ex.Message);
        Assert.Contains("Reinstall the app", ex.Message);
        Assert.DoesNotContain(Directory.GetDirectories(_out),
            d => !Path.GetFileName(d).StartsWith('.'));
    }

    /// <summary>…and the ship gate reads the file the project names, so a picked ramp of the wrong extent
    /// is refused by name exactly as a replacement's own is.</summary>
    [Fact]
    public void A_picked_ramp_of_the_wrong_extent_is_refused_by_name()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampSize");
        PickStockRamp(p, WriteRampDds("small_ramp.dds", width: 4, height: 4));

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("can't be built", ex.Message);
        Assert.Contains("is 4x4", ex.Message);
    }

    [Fact]
    public void A_pick_on_a_material_the_roster_no_longer_carries_is_said_and_skipped()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampGoneMaterial");
        // something else to ship, so the hold-back is the only thing under test rather than
        // a mod that turned out to carry nothing at all
        AddEditedTexture(p);
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"), material: "m_gone");

        var ex = Assert.Throws<InvalidOperationException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("The material 'm_gone' the toon ramp was picked on is not in the current "
            + "game files.", ex.Message);
    }

    /// <summary>A pick derives no verb — nothing about the part's geometry or pictures moves — so a mod
    /// that carries nothing else still has something to build.</summary>
    [Fact]
    public void A_mod_that_is_only_a_ramp_pick_still_builds()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampOnly");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Single(Directory.GetFiles(r.OutDir, "stockramp_*.dds"));
        Assert.Contains("if $zz_srm == 1\n", File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")));
    }

    /// <summary>The same pick made the way the APP makes one: a project authored as schema 2 from its first
    /// keystroke, the ramp published onto the game ramp slot through the ingress the ② Edit picker uses, and
    /// no adapter anywhere between it and the build. Every other pick test enters through the released
    /// boundary, so none of them touches this route — and the plan/emitter drift check refused it for every
    /// authored project carrying a pick until the check learned to ask the slot's DOMAIN.</summary>
    [Fact]
    public void An_authored_projects_ramp_pick_binds_through_the_production_build()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var resolver = new LegacyProjectResolver(env);
        string ramp = WriteRampDds("picked_ramp.dds");
        var target = Slot("c_vesna01_body_lod0");
        var session = new AuthoredEditSession(new AuthoredProject { RootDir = _proj });
        session.SetWorkspaceIndex(new AuthoredWorkspaceIndex
        {
            Selection = new List<SelectionEntry>
            {
                new() { Character = target.Subject, Outfit = target.Outfit },
            },
        });
        session.EnsurePartSlots(target, resolver.ResolvePart);
        string edit = session.CreateEdit(target);
        string rampSlot = session.GameRampSlot(target, 0);
        var ingress = ProjectAssetIngress.Begin(session.Snapshot(), edit, rampSlot,
            Path.Combine(_proj, ramp));
        Assert.Equal(ProjectAssetPublishResult.Published,
            session.PublishAssetForBinding(ingress, ProjectAssetKind.Ramp, "picked_ramp",
                ProjectAssetIngress.Binary).Result);
        var project = session.Snapshot();
        var plan = AuthoredBuildPlanner.Plan(project,
            new ProductionAuthoredBuildBackend(resolver.ResolvePart));

        var r = ModBuilder.Build(AuthoredBuildExecution.Create(project, plan), env, _out, zip: false);

        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts));
        var shipped = Assert.Single(Directory.GetFiles(r.OutDir, "stockramp_*.dds"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_proj, ramp)), File.ReadAllBytes(shipped));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"filter_index = {MigotoEmitter.FilterRamp}", ini);
        Assert.Contains("if $zz_srm == 1\n", ini);
        Assert.Empty(r.Warnings);
    }

    /// <summary>A pick on a part with no other change carries a change row of its own, so it carries that
    /// row's toggle key — read out of the same registry every other row's key is bound in.</summary>
    [Fact]
    public void A_pick_with_no_other_change_on_its_part_binds_under_its_own_rows_key()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampOwnKey");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Ramp, "F7",
            hideWhenOff: false, startsOff: false);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"if $zz_srm == 1\nif ${ModKeys.VariableFor("F7")} == 0\n", ini);
        Assert.Contains($"global ${ModKeys.VariableFor("F7")} = 0\n", ini);
    }

    /// <summary>A part that already changes has one row, one tick and one key. The pick rides them, so one
    /// press switches the whole part rather than half of it.</summary>
    [Fact]
    public void A_pick_on_a_part_that_already_changes_binds_under_that_changes_key()
    {
        // the NORMAL is retextured, so the material's base colour is still free to identify its draws
        var env = MakeSkinnedEnv(anchorRamp: true, anchorNormal: true);
        var p = NewProject("StockRampSharedKey");
        AddEditedTexture(p, file: "normal.dds", bundle: "bundleN", objectName: "tex_body_n");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Retexture, "F7",
            hideWhenOff: false, startsOff: false);
        // a key bound against a ramp row this list does not carry: the pick must not take it
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Ramp, "F8",
            hideWhenOff: false, startsOff: false);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"if $zz_srm == 1\nif ${ModKeys.VariableFor("F7")} == 0\n", ini);
        Assert.DoesNotContain(ModKeys.VariableFor("F8"), ini);
    }

    /// <summary>The pick's row is ticked like every other, and unticking it leaves the ramp out of the
    /// build — here, out of a build that then has nothing else to carry.</summary>
    [Fact]
    public void A_pick_row_left_out_of_the_build_takes_the_pick_with_it()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampRowExcluded");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));
        p.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Ramp, excluded: true);

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("nothing to build", ex.Message);
    }

    /// <summary>…and where the pick rides another change's row, that row's tick is the one that decides.
    /// </summary>
    [Fact]
    public void Unticking_the_row_a_pick_rides_takes_the_pick_with_it()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampRiddenExcluded");
        AddEditedTexture(p);
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));
        p.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Retexture, excluded: true);

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("nothing to build", ex.Message);
    }

    /// <summary>A pick's file is referenced exactly as a replaced submesh's ramp is
    /// (<c>BuildWorkItem.ReferencedFiles</c>), so a deleted one fails the build by NAME before anything is
    /// written — not partway through, over a folder already half published.</summary>
    [Fact]
    public void A_pick_whose_file_the_modder_deleted_fails_the_build_by_name()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampMissing");
        string ramp = WriteRampDds("picked_ramp.dds");
        PickStockRamp(p, ramp);
        File.Delete(Path.Combine(_proj, ramp));

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        string blocked = string.Join("; ", BuildLogDiagnostics.From(ex));
        Assert.Contains("is missing", blocked);
        Assert.Contains(ramp, blocked);
        Assert.Empty(Directory.GetFileSystemEntries(_out));   // no folder, no zip, no work dirs
    }

    /// <summary>…and the two records the mod carries about itself say so. The card has no work list to read
    /// a subject off, and a manager predicting conflicts has nothing but the hash list — a pick that
    /// published neither would install over another mod's ramp in silence.</summary>
    [Fact]
    public void A_pick_only_build_names_its_subject_and_publishes_the_hashes_it_binds()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampOnlySidecar");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        Assert.Equal("Vesna", doc.RootElement.GetProperty("character").GetString());
        Assert.Equal("VesnaSSR01", doc.RootElement.GetProperty("source_outfit").GetString());
        var hashes = doc.RootElement.GetProperty("override_hashes").EnumerateArray()
            .Select(x => x.GetString()!).ToList();

        // every hash the emitted sections act on is published, so another mod on any of them is predicted
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        var acted = ini.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("hash = ", StringComparison.Ordinal))
            .Select(l => l["hash = ".Length..]).Distinct().ToList();
        Assert.NotEmpty(acted);
        Assert.Empty(acted.Except(hashes, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "other" },
            ModInstall.Overlapping(hashes, new[] { ("other", (IReadOnlyList<string>)acted) }));
    }

    /// <summary>The repair record carries the pick too: which material shades with which shipped file. It
    /// is no change record — no verb, no geometry — so it rides on its own.</summary>
    [Fact]
    public void A_shipped_pick_is_in_the_repair_record()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampRepair");
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "repair.json")));
        var pick = Assert.Single(doc.RootElement.GetProperty("stock_ramps").EnumerateArray());
        Assert.Equal("Vesna", pick.GetProperty("character").GetString());
        Assert.Equal("VesnaSSR01", pick.GetProperty("outfit").GetString());
        Assert.Equal("c_vesna01_body_lod0", pick.GetProperty("mesh").GetString());
        Assert.Equal("m_body", pick.GetProperty("material").GetString());
        // the shipped file's own name inside the mod folder, which the project's path never gives
        string shipped = pick.GetProperty("ramp").GetString()!;
        Assert.True(File.Exists(Path.Combine(r.OutDir, shipped)));
        // the subject rides with it, so a pick-only mod's record still says whose outfit it is
        Assert.Equal("Vesna", Assert.Single(doc.RootElement.GetProperty("subjects").EnumerateArray())
            .GetProperty("character").GetString());
    }

    /// <summary>A pick the build held back writes nothing at all: the record states what the mod CARRIES,
    /// and a held-back pick is not that.</summary>
    [Fact]
    public void A_held_back_pick_leaves_no_repair_record()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampRepairHeld");
        WriteDonorGlb();
        AddReplaceTarget(p);
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "repair.json")));
        Assert.False(doc.RootElement.TryGetProperty("stock_ramps", out _));
    }

    /// <summary>A replacement on ANOTHER part of the subject slot-tags the base colour the two parts share,
    /// with its kind value — which a pick's probe would read as something else entirely. The pick takes the
    /// next ordinary map of its material instead, and refuses when there is none left: it cannot be aimed,
    /// and a mod that ships it would carry a bind that never fires.</summary>
    [Fact]
    public void A_pick_whose_base_colour_a_replacement_already_tags_is_refused()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, twinPart: true, twinPartSharedAlbedo: true);
        var p = NewProject("StockRampClaimed");
        WriteDonorGlb(bones: TwinBones);
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "painted.png"));
        AddReplaceTarget(p, mesh: "c_vesna01_body2_lod0", textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "painted.png" },
        });
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var ex = Assert.Throws<AuthoredRefusalException>(()
            => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("no other map on that material can be recognized in game", ex.Message);
    }

    /// <summary>The donor route owns the ramp on a part the mod REPLACES: the replacement already binds its
    /// own maps at that draw. Only the released shape can hold both — a replaced part shows its
    /// replacement's own slots and no installed-material ones — so the conversion names the pick it omits
    /// and the build never meets the pair at all.</summary>
    [Fact]
    public void A_pick_on_a_part_this_build_replaces_is_omitted_at_conversion()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("StockRampOnReplaced");
        WriteDonorGlb();
        AddReplaceTarget(p);
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var adaptation = LegacyProjectAdapter.Adapt(p,
            new LegacyProjectResolver(WithExactIdentities(env)).ResolvePart);
        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Contains(adaptation.Report.Items, item => item.Code == "ramp.replaced"
            && !item.BlocksSave);
        Assert.Empty(Directory.GetFiles(r.OutDir, "stockramp_*.dds"));
        Assert.DoesNotContain("zz_srm", File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")));
    }

    /// <summary>A ramp's own hash is what says which register holds a ramp at the draw. Where something
    /// else in the build already tags that hash, the one section on it carries the other value and the bind
    /// would go out and never fire — so the build refuses the pick.</summary>
    [Fact]
    public void A_pick_whose_ramp_hash_another_change_tags_is_refused()
    {
        // a second part wears the body's ramp as its BASE COLOUR, and is replaced: its anchor slot tag
        // claims that hash for the albedo kind, which the ramp probe never fires on
        var env = MakeSkinnedEnv(anchorRamp: true, twinPart: true, twinPartRampAsAlbedo: true);
        var p = NewProject("StockRampTagTaken");
        WriteDonorGlb(bones: TwinBones);
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "painted.png"));
        AddReplaceTarget(p, mesh: "c_vesna01_body2_lod0", textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "painted.png" },
        });
        PickStockRamp(p, WriteRampDds("picked_ramp.dds"));

        var ex = Assert.Throws<AuthoredRefusalException>(()
            => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("cannot be told apart from another texture this mod changes", ex.Message);
    }

    /// <summary>A ramp the modder NAMED on a replaced submesh is their own choice, so it ships as their own
    /// work: the record says Authored and names no game texture behind it, which is what tells it apart from
    /// a ramp that came across with the geometry.</summary>
    [Fact]
    public void A_ramp_the_modder_named_on_a_replacement_ships_as_their_own()
    {
        var env = MakeSkinnedEnv(anchorRamp: true);
        var p = NewProject("RampNamed");
        WriteDonorGlb();
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0, Ramp = WriteRampDds("chosen_ramp.dds") },
        });

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Single(Directory.GetFiles(r.OutDir, "donor_*_ramp.dds"));
        Assert.Equal("Authored", RampRecord(r, 0).GetProperty("origin").GetString());
        Assert.False(RampRecord(r, 0).TryGetProperty("stock", out _));
    }

    [Fact]
    public void A_replace_carrying_donor_maps_builds_byte_for_byte_the_same_twice()
    {
        var env = MakeSkinnedEnv(anchorRamp: true, rampDonor: true);
        var p = NewProject("RampDeterminism");
        WriteDonorGlb();
        AddReplaceTarget(p, textures: new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = AddDonorStockPng(p, "tex_paloma_d", "paloma_body.png") },
            new() { Submesh = 1, Albedo = AddDonorStockPng(p, "tex_paloma2_d", "paloma_arm.png") },
        });

        var first = Snapshot(ReleasedBuild.Build(p, env, _out, zip: false).OutDir);
        var second = Snapshot(ReleasedBuild.Build(p, env, _out, zip: false).OutDir);

        Assert.Equal(first.Keys.Order().ToArray(), second.Keys.Order().ToArray());
        foreach (var (name, bytes) in first) Assert.Equal(bytes, second[name]);

        static Dictionary<string, byte[]> Snapshot(string dir) =>
            Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
    }

    [Fact]
    public void A_representative_schema_1_output_folder_matches_the_frozen_fixture()
    {
        // The frozen folder proves the released build shape. Its catalog is fixture input too, so a
        // re-measurement cannot silently turn this into a golden for the current data.
        var env = MakeSkinnedEnv() with
        {
            ShaderSlotCatalogFile = Path.Combine(ProjectGoldenDir(), "frozen_charps_26109_r2.json"),
        };
        var project = NewProject("Legacy Output");
        WriteDonorGlb();
        AddReplaceTarget(project);
        project.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0",
            EditVerbs.Replace, "F7", hideWhenOff: true, startsOff: true);

        var built = ReleasedBuild.Build(project, env, _out, zip: false);
        string actual = OutputSnapshot(built.OutDir);
        string golden = Path.Combine(ProjectGoldenDir(), "legacy_build_v1.json");
        bool regold = Environment.GetEnvironmentVariable("REMOLD_REGOLD") == "1";
        if (regold) File.WriteAllText(golden, actual, new UTF8Encoding(false));

        Assert.True(File.Exists(golden), $"golden asset missing: {golden} (run once with REMOLD_REGOLD=1)");
        Assert.Equal(File.ReadAllText(golden), actual);
        Assert.False(regold, "REMOLD_REGOLD run regenerated the golden — rerun without it to compare");
    }

    [Fact]
    public void A_build_records_the_current_shipped_shader_catalog_identity()
    {
        var env = MakeSkinnedEnv();
        var project = NewProject("Current catalog sidecar");
        WriteDonorGlb();
        AddReplaceTarget(project);

        var built = ReleasedBuild.Build(project, env, _out);

        using var sidecar = JsonDocument.Parse(File.ReadAllText(Path.Combine(built.OutDir, "gf2mod.json")));
        var slots = sidecar.RootElement.GetProperty("shader_slots");
        Assert.Equal("charps-26932-r3", slots.GetProperty("catalog").GetString());
        Assert.Equal("26932", slots.GetProperty("game_build").GetString());
        BuildWatermarkTests.AssertStamped(built);
    }

    private static string ProjectGoldenDir([CallerFilePath] string self = "") =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(self))!, "Project", "golden");

    private static string OutputSnapshot(string dir)
    {
        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .OrderBy(f => Path.GetRelativePath(dir, f), StringComparer.Ordinal)
            .Select(file =>
            {
                byte[] bytes = File.ReadAllBytes(file);
                string relative = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (string.Equals(relative, "mod.ini", StringComparison.Ordinal))
                {
                    // The marker hashes the compiled Core binary, not the build shape this golden pins.
                    string normalized = Regex.Replace(Encoding.UTF8.GetString(bytes),
                        @"(?m)^; generated override set [0-9a-f]{12}(?=\r?$)",
                        "; generated override set 000000000000");
                    bytes = Encoding.UTF8.GetBytes(normalized);
                }
                return new
                {
                    path = relative,
                    length = bytes.LongLength,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                };
            });
        // LF on every platform. The golden is compared as text and its committed bytes are LF (pinned
        // `-text` in .gitattributes), so leaving the newline at the platform default would make this
        // comparison answer to the checkout's core.autocrlf rather than to what the build emitted.
        return JsonSerializer.Serialize(files,
            new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" });
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        // both maps ship, and the anchor's own normal is tagged so the authored one has a slot to bind at
        Assert.Equal(2, Directory.GetFiles(r.OutDir, "donor_*.dds").Length);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"filter_index = {MigotoEmitter.FilterNormal}", ini);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("No original normal on ") && w.Contains("could be matched to a texture slot"));

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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
        Assert.Contains(r.Warnings, w => w.Contains("No original RMO on ") && w.Contains("could be matched to a texture slot"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("No original normal on ") && w.Contains("could be matched to a texture slot"));
        Assert.DoesNotContain(r.Warnings, w => w.Contains("No original base color on ") && w.Contains("could be matched to a texture slot"));
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Single(Directory.GetFiles(r.OutDir, "donor_*.dds"));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"filter_index = {MigotoEmitter.FilterRmo}", ini);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("No original RMO on ") && w.Contains("could be matched to a texture slot"));

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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
        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(NewProject(), env, _out));
        Assert.Contains("nothing to build", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_hidden_mesh_outside_the_roster_warns_and_builds_nothing()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject();
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_ghost_lod0", true);
        // the stale hide is warned away by derivation, leaving an empty build — refused loudly
        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out));
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
        var ex = Assert.Throws<InvalidOperationException>(() => ReleasedBuild.Build(p, env, _out));
        Assert.Contains("c_vesna01_ghost_lod0", ex.Message);
        Assert.Contains("This part is not in the current game files.", ex.Message);
    }

    [Fact]
    public void A_failed_build_leaves_no_final_folder_or_zip()
    {
        // unresolvable sibling address → the failure hits AFTER work dirs are created
        var env = MakeEnv(out _, out _);
        var broken = new BuildEnv(env.ResolveSubject, a => a == "addr_body" ? "bundle0" : null,
            env.Deobfuscate, env.CatalogVersion, env.AppVersion).Exact();
        var p = NewProject("Doomed");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);

        Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, broken, _out));
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
            ("CommanderMale · CommanderMale · face", "210b832c", BuildEmissionGate.Unconditional),
            ("CommanderMale · CommanderNeutral · face", "210b832c",
                BuildEmissionGate.Unconditional),
        });

        Assert.Equal("'CommanderMale · CommanderMale · face' and 'CommanderMale · CommanderNeutral · face' "
            + "replace one mesh they share, and only one replacement could show. "
            + "Remove one of the two mesh edits, or switch them with one key", clash);
    }

    [Fact]
    public void Replaces_on_meshes_that_only_share_a_name_are_no_conflict()
    {
        // same slot name, different assets — the reason the test is the hash and not the name
        Assert.Null(ModBuilder.ReplacedMeshConflict(new[]
        {
            ("CommanderMale · CommanderMale · face", "210b832c", BuildEmissionGate.Unconditional),
            ("CommanderMale · CommanderMale03 · face", "00927735",
                BuildEmissionGate.Unconditional),
        }));
    }

    /// <summary>Two Replaces on one draw that a key group's own states keep apart are not a fight: one key
    /// stands in one position at a time, so only one override is ever live. The certificate is the plan's,
    /// read here rather than re-derived — and a claim it does not cover still refuses, word for word.</summary>
    [Fact]
    public void Two_Replaces_on_one_shared_mesh_build_when_one_key_keeps_them_apart()
    {
        BuildEmissionGate At(string group, int state) =>
            new BuildEmissionGate(new BuildGateTerm(group, "F7", state));

        Assert.Null(ModBuilder.ReplacedMeshConflict(new[]
        {
            ("Vesna · VesnaSSR01 · body", "210b832c", At("key-0001", 0)),
            ("Vesna · VesnaSSR02 · body", "210b832c", At("key-0001", 1)),
        }));

        // two different groups can stand anywhere at once, so their claims are not exclusive
        Assert.Equal("'Vesna · VesnaSSR01 · body' and 'Vesna · VesnaSSR02 · body' "
            + "replace one mesh they share, and only one replacement could show. "
            + "Remove one of the two mesh edits, or switch them with one key",
            ModBuilder.ReplacedMeshConflict(new[]
            {
                ("Vesna · VesnaSSR01 · body", "210b832c", At("key-0001", 0)),
                ("Vesna · VesnaSSR02 · body", "210b832c", At("key-0002", 1)),
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
        Assert.Contains("for different meshes", differs, StringComparison.Ordinal);

        // and so would a different mesh under it
        Assert.NotNull(ModBuilder.DumpNameConflict("commandermale_face", held,
            new ModBuilder.DumpIdentity("c_CommanderMale_dorm_hair_lod0", "210b832c")));
    }

    [Fact]
    public void Replacement_property_maps_on_one_anchor_resource_refuse_unattributable_probes()
    {
        var env = MakeSkinnedEnv(sharedGenericProperties: true);
        var project = NewProject("ReplacementSharedPropertyResource");
        WriteDonorGlb();
        using (var detail = new Image<Rgba32>(8, 8, new Rgba32(20, 40, 60, 255)))
            detail.SaveAsPng(Path.Combine(_proj, "donor_detail.png"));
        using (var mask = new Image<Rgba32>(8, 8, new Rgba32(220, 210, 200, 255)))
            mask.SaveAsPng(Path.Combine(_proj, "donor_mask.png"));
        AddReplaceTarget(project, textures: new List<SubmeshTextures>
        {
            new()
            {
                Submesh = 0,
                Textures = new List<PropertyTextureBinding>
                {
                    new() { ShaderProperty = "_DetailAlbedo", File = "donor_detail.png" },
                    new() { ShaderProperty = "_DetailMask", File = "donor_mask.png" },
                },
            },
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReleasedBuild.Build(project, env, _out, zip: false));

        Assert.Equal("The original texture 'tex_body_d' is used by Detail color and Detail mask on "
            + "'c_vesna01_body_lod0', so this edit cannot reach Detail color alone at this draw. "
            + "Leave this picture out, or change the texture for every slot that draws it with a game-wide edit.",
            exception.Message);
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
                // Only section-name companions the emitter itself mints may share one draw tuple.
                static string Stem(string s) => s.Trim('[', ']');
                static bool Companion(string candidate, string owner)
                {
                    if (!candidate.StartsWith(owner, StringComparison.OrdinalIgnoreCase)) return false;
                    string suffix = candidate[owner.Length..];
                    if (suffix.Equals("_Scope", StringComparison.OrdinalIgnoreCase)
                        || suffix.Equals("_DrawFull", StringComparison.OrdinalIgnoreCase))
                        return true;
                    const string draw = "_DrawS";
                    return suffix.StartsWith(draw, StringComparison.OrdinalIgnoreCase)
                        && suffix.Length > draw.Length
                        && suffix[draw.Length..].All(c => c is >= '0' and <= '9');
                }
                bool companions = Companion(Stem(section), Stem(held))
                    || Companion(Stem(held), Stem(section));
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

    [Fact]
    public void Duplicate_section_checker_allows_only_emitter_companion_suffixes()
    {
        const string allowed = "[TextureOverride_Cap_beta]\nhash = abcdef01\n"
            + "[TextureOverride_Cap_beta_Scope]\nhash = abcdef01\n"
            + "[TextureOverride_Cap_beta_DrawFull]\nhash = abcdef01\n"
            + "[TextureOverride_Cap_beta_DrawS12]\nhash = abcdef01\n";
        AssertNoDuplicateSections(allowed);

        const string unknown = "[TextureOverride_Cap_beta]\nhash = abcdef01\n"
            + "[TextureOverride_Cap_beta_lod1]\nhash = abcdef01\n";
        var refused = Assert.ThrowsAny<Exception>(() => AssertNoDuplicateSections(unknown));
        Assert.Contains("claimed by both", refused.Message, StringComparison.Ordinal);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));
        Assert.Contains("can't be told apart in game", ex.Message);
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

        var ex = Assert.ThrowsAny<Exception>(() => ReleasedBuild.Build(p, env, _out, zip: false));
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

        var ex = Assert.ThrowsAny<Exception>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_body2_lod0' · it casts no shadow, so the game stops drawing it the "
            + "moment it leaves the camera, and only a mesh edit on that part itself can use it", ex.Message);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));
        Assert.Contains("'c_vesna01_body_lod0' and 'body2' can't be told apart in game", ex.Message);
        Assert.Contains("would change the other", ex.Message);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        string tierIb = SkinnedIb("s1.bundle", "c_vesna01_body_lod1");
        Assert.Equal(SkinnedIb("smt.bundle", "c_vesna01_mate_lod1"), tierIb);
        string v = $"zz_tw_{tierIb}";
        Assert.DoesNotContain(r.Warnings, w => w.Contains("keeps its original mesh"));
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"[TextureOverride_Retex_vesna_body_a_{_stockTexHash}]\nhash = {_stockTexHash}\nmatch_priority = 0\n"
            + "if $zz_key_f9 == 0\nthis = Resource_Rtx0\nendif\n", ini);
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
    public void A_hide_on_an_ambiguous_mesh_wearing_one_base_color_warns_about_the_coaffected_twin()
    {
        // No discriminator, no guard: the skip stays plain and reaches both draws, but that co-effect is
        // disclosed by the two mesh names rather than silently shipped.
        var env = MakeSkinnedEnv(twinPart: true, twinPartPosSeed: 21, twinPartSharedAlbedo: true);
        var p = NewProject("HideTwinPlain");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body2_lod0", true);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.DoesNotContain("zz_tw_", ini);
        Assert.DoesNotContain("TwinTag", ini);
        Assert.Contains($"[TextureOverride_Hide_0]\nhash = "
            + $"{SkinnedIb("s2.bundle", "c_vesna01_body2_lod0")}\nmatch_priority = 0\nhandling = skip\n", ini);
        Assert.Contains("Hiding 'c_vesna01_body2_lod0' also hides 'c_vesna01_body_lod0' because their "
            + "draws cannot be told apart.", r.Warnings);
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

        var r = ReleasedBuild.Build(p, env, _out);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.Contains($"[TextureOverride_RetexTag_{_stockTexHash}]", ini);
        Assert.DoesNotContain($"[TextureOverride_TwinTag_{_stockTexHash}]", ini);
        Assert.Equal(1, CountOf(ini, $"hash = {_stockTexHash}\n"));
        BuildWatermarkTests.AssertStamped(r);
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

        var ex = Assert.ThrowsAny<Exception>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_body2_lod0' · the dorm dresses it on and off separately from the "
            + "scene, so only a mesh edit on that part itself can use it", ex.Message);
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

        var ex = Assert.ThrowsAny<Exception>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_mate_lod0' · a dorm scene can hide or reveal it mid-pose, so only a "
            + "mesh edit on that part itself can use it", ex.Message);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_dress1_lod0' and 'dress2' can't be told apart in game", ex.Message);
        Assert.Contains("would change the other", ex.Message);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_dress1_lod0' and 'dress2' can't be told apart in game", ex.Message);
        Assert.Contains("would change the other", ex.Message);
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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_dress1_lod0' and 'dress2' can't be told apart in game", ex.Message);
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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_dress1_lod0' and 'dress2' can't be told apart in game", ex.Message);
        Assert.Contains("would change the other", ex.Message);
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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("can't be told apart in game", ex.Message);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("'c_vesna01_dress1_lod0' and 'dress2' can't be told apart in game", ex.Message);
        Assert.Contains("would change the other", ex.Message);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var ex = Assert.Throws<InvalidOperationException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("'dress1' and 'dress2' can't be told apart in game", ex.Message);
        Assert.Contains("would change the other", ex.Message);
    }

    [Fact]
    public void Either_route_alone_on_the_split_signature_builds_as_it_always_did()
    {
        // The same three siblings, one hide at a time: the pair's hide is answered by the textures at
        // its own draw, the lone sibling's by the wardrobe. Neither is changed by the refusal above.
        var p1 = NewProject("WardrobeMixedTextureOnly");
        p1.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_dress1_lod0", true);
        var r1 = ReleasedBuild.Build(p1,
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
        var r2 = ReleasedBuild.Build(p2,
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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("uses the same base color that tells 'dress2' apart", ex.Message);
        Assert.Contains("so this mod can't be built", ex.Message);
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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("'body' and 'body2' can only be told apart by 'body's base color",
            ex.Message);
        Assert.Contains("so this mod can't be built", ex.Message);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, lines.Add, zip: false);

        // pooled like any measured part, not held back and not narrow-restricted
        Assert.Contains("pool (vesna_body): c_vesna01_body_lod0 (anchor c_vesna01_body_lod0)", r.Diagnostics);
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

        var r = ReleasedBuild.Build(p, env, _out, lines.Add, zip: false);

        Assert.Contains("pool (vesna_body): c_vesna01_body_lod0 (anchor c_vesna01_body_lod0)", r.Diagnostics);
        Assert.Contains(r.Diagnostics, d => d == "pool (vesna_body): left out: 'c_vesna01_acc_lod0' · "
            + "it stores one influence per vertex, so only a mesh edit on that part itself can use it");
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

        var r = ReleasedBuild.Build(p, env, _out, lines.Add, zip: false);

        // the body tables the shared bone too, so both pool — and the tie lands on the replaced part
        Assert.Contains("pool (vesna_acc): c_vesna01_body_lod0, c_vesna01_acc_lod0 (anchor c_vesna01_acc_lod0)",
            r.Diagnostics);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        AssertEveryReferencedFileShips(ini, r.OutDir);
        Assert.Contains("CustomShaderConvert_vesna_acc", ini);
        Assert.DoesNotContain("Rigid", ini);
    }

    [Theory]
    [InlineData(21, 4, false, "it has 21 blend shapes, which the swap can't reproduce")]
    [InlineData(0, 1, true, "its skin weights are stored in a shape this app can't read")]
    [InlineData(0, 2, true, "its skin weights are stored in a shape this app can't read")]
    [InlineData(0, 4, true, "its skin weights are stored in a shape this app can't read")]
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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Equal($"'c_vesna01_body_lod0' can't be replaced: {why}. Remove this mesh edit", ex.Message);
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

        var ex = Assert.Throws<InvalidDataException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("2 bone(s) that no part this mod can build with has", ex.Message);
        Assert.Contains("Left out: 'c_vesna01_face_lod0' · it has 21 blend shapes, which the swap "
            + "can't reproduce", ex.Message);
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

        var ex = Assert.Throws<InvalidDataException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("Left out: 'c_vesna01_ghost_lod0' · the game files for part", ex.Message);
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

        var ex = Assert.Throws<InvalidDataException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Equal("the new mesh uses 1 bone(s) that no part of this item has. It was weighted "
            + "against a different armature. Open this item in Blender again and re-weight the mesh",
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

        var ex = Assert.Throws<InvalidDataException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Equal("the new mesh uses 1 bone(s) that no part of this item moves. They are named by "
            + "'c_vesna01_body_lod0' but never moved. Re-weight the mesh onto the bones this item moves",
            ex.Message);
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

        var ex = Assert.Throws<InvalidDataException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Equal("the new mesh uses 1 bone(s) that no part of this item has. It was weighted "
            + "against a different armature. Open this item in Blender again and re-weight the mesh",
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

        var ex = Assert.Throws<InvalidDataException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("Left out: 'c_vesna01_unread_lod0' · its skin weights can't be read",
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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
        // The cloth is the part the retexture lands on — the body's own draw is replaced, and the
        // replacement carries its own maps. The workspace records the BODY as the texture's only user,
        // and the retexture reaches the cloth all the same: an edited texture lands on the roster parts
        // that bind it, which is the live join the released derivation made.
        AddEditedTexture(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        int tag = MigotoEmitter.RetexTag(_stockTexHash);
        Assert.DoesNotContain($"[TextureOverride_SlotTag_{_stockTexHash}]", ini);
        Assert.Contains($"[TextureOverride_RetexTag_{_stockTexHash}]", ini);
        Assert.Contains($"if $zz_t == {tag}\n$zz_slot_a = 0\nendif\n", ini);
        Assert.Contains("if $zz_slot_a == 0\nps-t0 = Resource_Tex0\nendif\n", ini);
        // the inheriting submesh keeps drawing the pre-retexture image at the replacement — disclosed
        Assert.Contains(r.Infos, i => i.Contains("keeps an original map"));
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
        var r1 = ReleasedBuild.Build(p1, MakeSkinnedEnv(), _out, zip: false);
        byte[] plain = File.ReadAllBytes(Directory.GetFiles(r1.OutDir, "combined_bind_*.buf").Single());

        var p2 = NewProject("SwapBaked");
        WriteDonorGlb(rotate: g);
        AddReplaceTarget(p2, bakedRest: RestBake.ToList(g));
        var r2 = ReleasedBuild.Build(p2, MakeSkinnedEnv(), _out, zip: false);
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

        var r = ReleasedBuild.Build(p, MakeSkinnedEnv(), _out, zip: false);

        Assert.Contains(r.Warnings, w => w.Contains("rest pose recorded for")
            && w.Contains("c_vesna01_body_lod0"));
        // and it landed where a target with no bake at all lands
        var plainProject = NewProject("SwapNoRest");
        WriteDonorGlb();
        AddReplaceTarget(plainProject);
        var plain = ReleasedBuild.Build(plainProject, MakeSkinnedEnv(), _out, zip: false);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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
            AppVersion: "test-1.0").Exact();
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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("can't be told apart in game", ex.Message);
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
            AppVersion: "test-1.0").Exact();
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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("can't be told apart in game", ex.Message);
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

        var ex = Assert.Throws<AuthoredRefusalException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("can't be told apart in game", ex.Message);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.Equal(1, SectionsOn(ini, SkinnedIb("s1.bundle", "c_vesna01_body_lod1")));
        Assert.Equal(1, SectionsOn(ini, SkinnedIb("smo.bundle", "c_vesna01_mate_lod1")));
        Assert.Contains("[TextureOverride_Cap_vesna_body_lod1]", ini);
        Assert.Contains("[TextureOverride_Cap_vesna_mate_lod1]", ini);
    }

    [Fact]
    public void A_zero_use_tier_carrier_ships_suppression_only_without_recovery_machinery()
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

        var r = ReleasedBuild.Build(p, env, _out, lines.Add, zip: false);

        // the cloth part joins the pool, at its roster position
        Assert.Contains(r.Diagnostics, d => d.StartsWith(
            "pool (vesna_body): c_vesna01_body_lod0, c_vesna01_cloth_lod0", StringComparison.Ordinal));
        // build-log only: the modder sees nothing about a part the mod doesn't change
        Assert.Contains(r.Diagnostics, d => d.Contains("'c_vesna01_cloth_lod0' is built alongside")
            && d.Contains("It is not changed"));
        Assert.DoesNotContain(r.Infos, i => i.Contains("is built alongside"));

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        // The draw section remains because routing/suppression ownership is independent of recovery.
        Assert.Contains($"[TextureOverride_Cap_vesna_cloth]\nhash = {SkinnedIb("sc.bundle", "c_vesna01_cloth_lod0")}\nmatch_priority = 0\n", ini);
        var clothSection = SectionBody(ini, "[TextureOverride_Cap_vesna_cloth]");
        Assert.DoesNotContain("Resource_vesna_cloth_Posed", clothSection);
        Assert.DoesNotContain("Resource_vesna_cloth_CB", clothSection);
        Assert.DoesNotContain("[CustomShaderRecover_vesna_cloth_vesna_body]", ini);
        Assert.False(File.Exists(Path.Combine(r.OutDir, "vesna_cloth_cpinv.buf")));
        Assert.False(File.Exists(Path.Combine(r.OutDir, "vesna_cloth_map_vesna_body.buf")));
        Assert.False(File.Exists(Path.Combine(r.OutDir, "recover_vesna_cloth_cs.hlsl")));
        var paletteLine = Assert.Single(r.Diagnostics,
            d => d.StartsWith("palette: vesna_body/vesna_cloth ", StringComparison.Ordinal));
        Assert.Contains("0/2 rows used - ships nothing", paletteLine, StringComparison.Ordinal);
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

        var r = ReleasedBuild.Build(p, env, _out, lines.Add, zip: false);

        Assert.Contains(r.Diagnostics, d => d == "pool (vesna_body): c_vesna01_body_lod0 (anchor c_vesna01_body_lod0)");
        Assert.DoesNotContain(r.Diagnostics, d => d.Contains("is built alongside"));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        Assert.DoesNotContain("[TextureOverride_Cap_vesna_cloth]", ini);
        // the tier still recovers — the unposed bone cost it nothing
        Assert.Contains("[TextureOverride_Cap_vesna_body_lod1]", ini);
    }

    [Fact]
    public void A_carrier_from_another_outfit_state_classifies_merged_and_builds_with_a_friendly_warning()
    {
        // The only part carrying the bone the body's lod1 poses is the DORM cloth, which never draws in
        // the frames this body draws in. Pooling it would pair the body's lod1 against a capture that
        // never fires, so its table classifies the row as merged without becoming a pool source. This
        // fixture has a null skeleton, pinning the build-log-only no-suffix diagnostic and its hash.
        var tierBones = BodyBones.Append(ClothBones[0]).ToArray();
        var env = MakeSkinnedEnv(clothWearer: true, clothTier: true, clothTail: "_Dorm",
            bodyTierBones: tierBones);
        var p = NewProject("TierCrossContext");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Equal(
            "'body' does not show some geometry from 'cloth_dorm' at longer view "
            + "distances. The build log names the bones.",
            Assert.Single(r.Warnings, w => w.Contains("does not show some geometry", StringComparison.Ordinal)));
        Assert.Contains(r.Diagnostics, d => d.Contains(
            "affected part 'c_vesna01_body_lod0'; tier mesh 'c_vesna01_body_lod1'; owning parts "
            + "'c_vesna01_cloth_lod0_Dorm'; bones no matching chain suffix (0x00000201)",
            StringComparison.Ordinal));
        var map = File.ReadAllBytes(Path.Combine(r.OutDir, "vesna_body_lod1_map_vesna_body.buf"));
        AssertSentinelAtCompactBone(map, tierBones, ClothBones[0]);
    }

    [Fact]
    public void A_carrier_that_does_not_draw_at_the_asking_tier_classifies_merged_and_builds()
    {
        // Same bone, same carrier, but the cloth ships only a top LOD: the tier chain would fall back to
        // its lod0 recovery, whose capture a frame drawing only the body's lod1 never fires.
        var tierBones = BodyBones.Append(ClothBones[0]).ToArray();
        var env = MakeSkinnedEnv(clothWearer: true, bodyTierBones: tierBones);
        var p = NewProject("TierNoLodMate");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Contains(r.Warnings, w => w.Contains(
            "some geometry from 'cloth'", StringComparison.Ordinal));
        var map = File.ReadAllBytes(Path.Combine(r.OutDir, "vesna_body_lod1_map_vesna_body.buf"));
        AssertSentinelAtCompactBone(map, tierBones, ClothBones[0]);
    }

    [Fact]
    public void A_tier_bone_no_readable_sibling_tables_classifies_lod1_only_and_builds_silently()
    {
        // Nothing in the outfit carries the bone, so there is no pool that poses this tier. The refusal
        // says what was needed and not found rather than reporting a union that came up short.
        var tierBones = BodyBones.Append(ClothBones[0]).ToArray();
        var env = MakeSkinnedEnv(bodyTierBones: tierBones);
        var p = NewProject("TierUncovered");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.DoesNotContain(r.Warnings, w => w.Contains("does not show some geometry", StringComparison.Ordinal));
        var map = File.ReadAllBytes(Path.Combine(r.OutDir, "vesna_body_lod1_map_vesna_body.buf"));
        AssertSentinelAtCompactBone(map, tierBones, ClothBones[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_same_tier_world_warns_for_a_posing_sibling_and_is_silent_for_a_tabling_only_sibling(
        bool siblingPoses)
    {
        var tierBones = BodyBones.Append(ClothBones[0]).ToArray();
        var env = MakeSkinnedEnv(clothWearer: true,
            bodyTierBones: tierBones,
            clothBoneHashes: siblingPoses ? ClothBones : new[] { ClothBones[1] },
            clothTabledOnly: siblingPoses ? null : new[] { ClothBones[0] });
        var p = NewProject(siblingPoses ? "TierSiblingPoses" : "TierSiblingTables");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        if (siblingPoses)
            Assert.Contains(r.Warnings, w => w.Contains(
                "some geometry from 'cloth'", StringComparison.Ordinal));
        else
            Assert.DoesNotContain(r.Warnings, w => w.Contains(
                "does not show some geometry", StringComparison.Ordinal));
        var map = File.ReadAllBytes(Path.Combine(r.OutDir, "vesna_body_lod1_map_vesna_body.buf"));
        AssertSentinelAtCompactBone(map, tierBones, ClothBones[0]);
    }

    [Fact]
    public void A_merged_own_tier_keeps_the_chain_suffix_in_the_log_and_out_of_the_warning()
    {
        const string suffix = "Shoes01_L/Shoes02_L";
        uint foldedBone = BoneTable.Hash(suffix);
        var tierBones = BodyBones.Append(foldedBone).ToArray();
        var skeleton = new SubjectSkeleton(new[]
        {
            new SubjectBone("Prefab", -1),
            new SubjectBone("Toes_L", 0),
            new SubjectBone("Shoes01_L", 1),
            new SubjectBone("Shoes02_L", 2),
        });
        var env = MakeSkinnedEnv(clothWearer: true,
            bodyTierBones: tierBones,
            clothBoneHashes: new[] { foldedBone }, skeleton: skeleton);
        var p = NewProject("TierMergedNamed");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string warning = Assert.Single(r.Warnings,
            w => w.Contains("does not show some geometry", StringComparison.Ordinal));
        Assert.Equal(
            "'body' does not show some geometry from 'cloth' at longer view "
            + "distances. The build log names the bones.",
            warning);
        Assert.DoesNotContain("Shoes01_L/Shoes02_L", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("0x", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Diagnostics, d => d.Contains("tier mesh 'c_vesna01_body_lod1'", StringComparison.Ordinal)
            && d.Contains($"'Shoes01_L/Shoes02_L' (0x{foldedBone:x8})", StringComparison.Ordinal));
        var map = File.ReadAllBytes(Path.Combine(r.OutDir, "vesna_body_lod1_map_vesna_body.buf"));
        AssertSentinelAtCompactBone(map, tierBones, foldedBone);
    }

    [Fact]
    public void A_pool_mates_uncovered_tier_row_builds_silently_and_keeps_the_original_tier_draw()
    {
        var tierBones = MateBones.Append(ClothBones[0]).ToArray();
        var env = MakeSkinnedEnv(poolMate: true, mateTier: true, mateTierBones: tierBones);
        var p = NewProject("TierMateRow");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.DoesNotContain(r.Warnings, w => w.Contains("does not show some geometry", StringComparison.Ordinal));
        var map = File.ReadAllBytes(Path.Combine(r.OutDir, "vesna_mate_lod1_map_vesna_body.buf"));
        AssertSentinelAtCompactBone(map, tierBones, ClothBones[0]);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.DoesNotContain("handling = skip",
            SectionBody(ini, "[TextureOverride_Cap_vesna_mate_lod1]"));
    }

    [Fact]
    public void A_target_merged_row_survives_a_mate_first_shared_tier_capture()
    {
        var sharedTierBones = MateBones.Append(ClothBones[0]).ToArray();
        var targetTierBones = BodyBones.Append(ClothBones[0]).ToArray();
        var env = MakeSkinnedEnv(clothWearer: true, poolMate: true, mateTierTwin: true,
            bodyTierBones: targetTierBones,
            mateTierBones: sharedTierBones, mateOwnAlbedo: true, poolMateFirst: true,
            mateTierBlendShapes: 1);
        var p = NewProject("TierMateFirstSharedCapture");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);
        var lines = new List<string>();

        var r = ReleasedBuild.Build(p, env, _out, lines.Add, zip: false);

        Assert.Contains(r.Diagnostics, d => d.StartsWith(
            "pool (vesna_body): c_vesna01_mate_lod0, c_vesna01_body_lod0",
            StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.Contains(
            "'body' does not show some geometry from 'cloth'",
            StringComparison.Ordinal));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains("[TextureOverride_Cap_vesna_body_lod1]", ini);
        var map = File.ReadAllBytes(Path.Combine(r.OutDir, "vesna_body_lod1_map_vesna_body.buf"));
        AssertSentinelAtCompactBone(map, targetTierBones, ClothBones[0]);
    }

    /// <summary>Assert the semantic row by its bone identity in the expected compact source order. A length
    /// check cannot distinguish retaining this write-nothing row from pruning it and retaining another.</summary>
    private static void AssertSentinelAtCompactBone(byte[] map, IReadOnlyList<uint> compactBoneOrder, uint bone)
    {
        int compactRow = compactBoneOrder.ToList().IndexOf(bone);
        Assert.True(compactRow >= 0, $"bone 0x{bone:x8} is absent from the expected compact order");
        Assert.Equal(PoolMath.Sentinel, BitConverter.ToUInt32(map, compactRow * sizeof(uint)));
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        var bodySkips = SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_body]"));
        var mateSkips = SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_mate]"));
        Assert.NotEmpty(bodySkips);
        Assert.NotEmpty(mateSkips);
        Assert.All(bodySkips, g =>
        {
            Assert.Contains("if $zz_key_f6 == 0", g);
            Assert.DoesNotContain("if $zz_key_f7 == 0", g);
        });
        Assert.All(mateSkips, g =>
        {
            Assert.Contains("if $zz_key_f7 == 0", g);
            Assert.DoesNotContain("if $zz_key_f6 == 0", g);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        var bodySkips = SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_body]"));
        var mateSkips = SkipGuards(SectionBody(ini, "[TextureOverride_Cap_vesna_mate]"));
        Assert.NotEmpty(bodySkips);
        Assert.All(bodySkips, g => Assert.Contains("if $zz_key_f6 == 0", g));
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string body = SectionBody(ini, "[TextureOverride_Cap_vesna_body]");
        Assert.All(SkipGuards(body), g =>
        {
            Assert.Contains("if $zz_key_f6 == 0", g);
            Assert.DoesNotContain("if $zz_key_f7 == 0", g);
        });
        Assert.Contains("if $zz_key_f6 == 0\nif $zz_key_f7 == 0\nif $zz_done_", body);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string body = SectionBody(File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")),
            "[TextureOverride_Cap_vesna_body]");
        Assert.NotEmpty(SkipGuards(body));
        Assert.All(SkipGuards(body), g =>
        {
            Assert.Contains("if $zz_key_f6 == 0", g);
            Assert.Contains("if $zz_key_f7 == 0", g);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        // The key launches at its group's FIRST position and the change answers the second, so it ships
        // off and the first press turns it on. Which position the content stands in is what says so; the
        // declaration is where the cycle begins, and every cycle begins at 0.
        Assert.Contains("global $zz_key_f7 = 0\n", ini);
        Assert.Contains("if $zz_key_f7 == 1\n", ini);
        // the mod's own key has no start control: a keyed mod behaves as an unkeyed one until pressed
        Assert.Contains("global $zz_key_f6 = 0\n", ini);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Contains("global $zz_key_f7 = 0\n", File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")));
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string shared = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains("global $zz_key_f7 = 0\n", shared);
        Assert.Contains("if $zz_key_f7 == 1\n", shared);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Contains("global $zz_key_f6 = 0\n", File.ReadAllText(Path.Combine(r.OutDir, "mod.ini")));
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string hideIni = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains("global $zz_key_f8 = 0\n", hideIni);
        Assert.Contains("if $zz_key_f8 == 1\nhandling = skip\nendif\n", hideIni);
    }

    [Fact]
    public void A_keyed_retexture_that_starts_off_declares_its_key_at_zero()
    {
        var env = MakeEnv(out _, out _);
        var p = NewProject("RetexStartsOff");
        AddEditedTexture(p);
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Retexture, "F9",
            startsOff: true);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string retexIni = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains("global $zz_key_f9 = 0\n", retexIni);
        Assert.Contains("if $zz_key_f9 == 1\n", retexIni);
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
        var first = ReleasedBuild.Build(p, env, _out, zip: false);
        var before = Directory.GetFiles(first.OutDir).Select(Path.GetFileName).Order().ToArray();
        Assert.NotEmpty(before);

        using (File.Open(Path.Combine(first.OutDir, "mod.ini"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => ReleasedBuild.Build(p, env, _out, zip: false));

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

        var r = ReleasedBuild.Build(p, env, _out);

        Assert.True(File.Exists(r.ZipPath));
        Assert.Single(Directory.GetFiles(_out, "*.zip*"));
    }

    // ---- renderer-slot tier coverage --------------------------------------------------------------

    [Fact]
    public void An_explicit_hide_emits_a_section_for_a_slot_addressed_lodm1_tier()
    {
        // Slot membership, not a hard-coded LOD vocabulary, decides which tiers the hide covers.
        var env = MakeMidTierEnv(out var h0, out var hm, out var h1, mid: "lodm1");
        var p = NewProject("MidOne");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", true);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"hash = {h0}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.Contains($"hash = {hm}\nmatch_priority = 0\nhandling = skip", ini);
        Assert.Contains($"hash = {h1}\nmatch_priority = 0\nhandling = skip", ini);
        AssertNoDuplicateSections(ini);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        var hashes = doc.RootElement.GetProperty("override_hashes")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { h0, hm, h1 }.Order().ToArray(), hashes);
    }

    [Fact]
    public void A_replace_captures_every_slot_addressed_tier()
    {
        // The pool's tier walk uses the same renderer-slot coverage truth as the hide walk.
        var env = MakeSkinnedEnv(midTier: true);
        var p = NewProject("SwapMid");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        AssertNoDuplicateSections(ini);
        var tierHashes = new[]
        {
            SkinnedIb("s0.bundle", "c_vesna01_body_lod0"),
            SkinnedIb("smid.bundle", "c_vesna01_body_lodm1"),
            SkinnedIb("s1.bundle", "c_vesna01_body_lod1"),
        };
        Assert.Equal(3, tierHashes.Distinct().Count());
        Assert.All(tierHashes, hash => Assert.Contains($"hash = {hash}\nmatch_priority = 0\n", ini));
    }

    [Fact]
    public void A_byte_identical_lodm0_sibling_on_its_parts_ib_collapses_to_one_unique_section()
    {
        var env = MakeSkinnedEnv(midTier: true, midTierLod: "lodm0", midTierVerts: 32,
            midTierPosSeed: 5);
        var p = NewProject("SameBytesMidTier");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string shared = SkinnedIb("s0.bundle", "c_vesna01_body_lod0");
        Assert.Equal(shared, SkinnedIb("smid.bundle", "c_vesna01_body_lodm0"));
        Assert.Equal(1, SectionsOn(ini, shared));
        Assert.DoesNotContain("global $zz_tw_", ini);
        Assert.DoesNotContain(r.Diagnostics, d => d.Contains("shares a draw signature"));
        AssertNoDuplicateSections(ini);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "gf2mod.json")));
        Assert.Equal(1, doc.RootElement.GetProperty("override_hashes").EnumerateArray()
            .Count(value => value.GetString() == shared));
    }

    [Fact]
    public void Different_byte_tiers_of_one_part_on_one_ib_are_not_twin_refused()
    {
        var env = MakeSkinnedEnv(midTier: true, midTierLod: "lodm0", midTierVerts: 32,
            midTierPosSeed: 21);
        var p = NewProject("SamePartMidTier");
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string shared = SkinnedIb("s0.bundle", "c_vesna01_body_lod0");
        Assert.Equal(shared, SkinnedIb("smid.bundle", "c_vesna01_body_lodm0"));
        Assert.Equal(1, SectionsOn(ini, shared));
        Assert.DoesNotContain("global $zz_tw_", ini);
        Assert.DoesNotContain(r.Diagnostics, d => d.Contains("shares a draw signature"));
        AssertNoDuplicateSections(ini);
    }

    [Fact]
    public void Different_byte_lodm0_meshes_across_parts_build_behind_a_texture_guard()
    {
        var env = MakeSkinnedEnv(midTier: true, midTierLod: "lodm0", midTierVerts: 24,
            poolMate: true, mateTierTwin: true, mateTierLod: "lodm0", mateOwnAlbedo: true);
        var p = NewProject("CrossPartMidGuard");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string shared = SkinnedIb("smid.bundle", "c_vesna01_body_lodm0");
        Assert.Equal(shared, SkinnedIb("smt.bundle", "c_vesna01_mate_lodm0"));
        Assert.Equal(1, SectionsOn(ini, shared));
        Assert.Contains($"global $zz_tw_{shared} = 0", ini);
        Assert.Contains(r.Diagnostics, d => d.Contains("shares a draw signature with 'mate'")
            && d.Contains("act while its own textures answer for it"));
        AssertTwinVerdictIsProbedWhereItIsTested(ini);
        AssertNoDuplicateSections(ini);
    }

    [Fact]
    public void Different_byte_lodm0_meshes_across_parts_still_refuse_without_a_guard_route()
    {
        var env = MakeSkinnedEnv(midTier: true, midTierLod: "lodm0", midTierVerts: 24,
            poolMate: true, mateTierTwin: true, mateTierLod: "lodm0");
        var p = NewProject("CrossPartMidRefusal");
        WriteDonorGlb(bones: BodyBones.Concat(MateBones).ToArray());
        AddReplaceTarget(p);

        var ex = Assert.Throws<AuthoredRefusalException>(
            () => ReleasedBuild.Build(p, env, _out, zip: false));

        Assert.Contains("can't be told apart in game", ex.Message);
        Assert.Contains("c_vesna01_body_lod0", ex.Message);
        Assert.Contains("mate", ex.Message);
    }
}
