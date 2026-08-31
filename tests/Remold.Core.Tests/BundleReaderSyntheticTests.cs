using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// ListAssets + GetTexture over a from-scratch synthetic UnityFS bundle carrying a solid-colour RGBA32
/// texture. GetTexture decodes to BGRA32, top-left origin.
/// </summary>
public class BundleReaderSyntheticTests
{
    [Fact]
    public void ListAssets_ReturnsTheSyntheticTextures_WithNamePathIdAndClass()
    {
        using var t = new TempGame();
        string bundle = t.At("synthetic.bundle");
        var ids = SyntheticBundle.Build(bundle,
            new SyntheticBundle.TextureSpec("tex_a", 4, 4, SyntheticBundle.SolidRgba32(4, 4, 1, 2, 3, 255)),
            new SyntheticBundle.TextureSpec("tex_b", 8, 8, SyntheticBundle.SolidRgba32(8, 8, 9, 9, 9, 255)));
        byte[] plain = File.ReadAllBytes(bundle);   // already plain UnityFS — the reader takes deobfuscated bytes

        var assets = new BundleReader().ListAssets(plain, SyntheticBundle.ClassTexture2D);

        Assert.Equal(2, assets.Count);
        Assert.Equal(new[] { "tex_a", "tex_b" }, assets.Select(a => a.Name).OrderBy(n => n).ToArray());
        Assert.All(assets, a => Assert.Equal(SyntheticBundle.ClassTexture2D, a.ClassId));
        Assert.Equal(ids.OrderBy(x => x), assets.Select(a => a.PathId).OrderBy(x => x));
    }

    [Fact]
    public void GetTexture_DecodesTheSyntheticTexture_ToBgra32AtTheRightDimensions()
    {
        using var t = new TempGame();
        string bundle = t.At("synthetic.bundle");
        // a solid RGBA, so the decoded BGRA order pins independently of the writer
        SyntheticBundle.BuildOneTexture(bundle, "poc_texture", 4, 4, r: 0xC0, g: 0x30, b: 0x90, a: 0xFF);
        byte[] plain = File.ReadAllBytes(bundle);

        var dt = new BundleReader().GetTexture(plain, "poc_texture");

        Assert.NotNull(dt);
        Assert.Equal((4, 4), (dt!.Value.Width, dt.Value.Height));
        Assert.Equal("RGBA32", dt.Value.Format);
        Assert.Equal(4 * 4 * 4, dt.Value.Bgra.Length);
        // BGRA order: R=0xC0,G=0x30,B=0x90 stored ⇒ B,G,R,A = 0x90,0x30,0xC0,0xFF at every pixel
        Assert.Equal(new byte[] { 0x90, 0x30, 0xC0, 0xFF }, dt.Value.Bgra[..4]);
    }

    [Fact]
    public void GetTexture_ReturnsNull_ForAnAbsentName()
    {
        using var t = new TempGame();
        string bundle = t.At("synthetic.bundle");
        SyntheticBundle.BuildOneTexture(bundle, "poc_texture", 4, 4);
        byte[] plain = File.ReadAllBytes(bundle);

        Assert.Null(new BundleReader().GetTexture(plain, "not_here"));
    }

    [Fact]
    public void GetMaterialShading_reads_the_serialized_shader_keywords_floats_and_colors()
    {
        using var t = new TempGame();
        string bundle = t.At("material.bundle");
        SyntheticBundle.BuildOneMaterial(bundle, "material.logical", "coat_skinuber", 41,
            System.Array.Empty<(string, int, long)>(), new[] { "CAB-character" },
            shading: new SyntheticBundle.MaterialShadingSpec(1, 91,
                new[] { "_USE_STOCKING", "_GI_FLATTEN" },
                new Dictionary<string, float> { ["_GlitterDensity"] = 12.5f },
                new Dictionary<string, float[]>
                {
                    ["_StockingCenterColor"] = new[] { 0.25f, 0.5f, 0.75f, 1f },
                }));

        var shading = new BundleReader().GetMaterialShading(File.ReadAllBytes(bundle), 41);

        Assert.NotNull(shading);
        Assert.Equal("coat_skinuber", shading!.Name);
        Assert.Equal((1, 91), (shading.ShaderFileId, shading.ShaderPathId));
        Assert.Equal(new[] { "_GI_FLATTEN", "_USE_STOCKING" },
            shading.EnabledKeywords.OrderBy(keyword => keyword, System.StringComparer.Ordinal));
        Assert.Equal(new[] { "CAB-character" }, shading.ExternalCabs);
        Assert.Equal(12.5f, shading.Floats["_GlitterDensity"]);
        Assert.Equal(new[] { 0.25f, 0.5f, 0.75f, 1f },
            shading.Colors["_StockingCenterColor"]);
    }

    // ---- GetBundleName — the VFS logical-identity read ----------------------------

    [Fact]
    public void GetBundleName_ReturnsTheSelfDeclaredLogicalName_Verbatim()
    {
        using var t = new TempGame();
        string bundle = t.At("synthetic.bundle");
        // live logical names are 32-hex WITH the ".bundle" suffix and differ from the physical filename
        const string logical = "4803b0e956ab524d02e41aa2a3932b28.bundle";
        SyntheticBundle.BuildOneTexture(bundle, "poc_texture", 4, 4, bundleName: logical);
        byte[] plain = File.ReadAllBytes(bundle);

        Assert.Equal(logical, new BundleReader().GetBundleName(plain));
    }

    [Fact]
    public void GetBundleName_NoAssetBundleObject_RefusesLoudly()
    {
        using var t = new TempGame();
        string bundle = t.At("synthetic.bundle");
        SyntheticBundle.BuildOneTexture(bundle, "poc_texture", 4, 4);   // no self-identification object
        byte[] plain = File.ReadAllBytes(bundle);

        var ex = Assert.Throws<InvalidDataException>(() => new BundleReader().GetBundleName(plain));
        Assert.Contains("AssetBundle", ex.Message);
    }

    [Fact]
    public void GetBundleName_EmptyDeclaredName_RefusesLoudly()
    {
        using var t = new TempGame();
        string bundle = t.At("synthetic.bundle");
        SyntheticBundle.BuildOneTexture(bundle, "poc_texture", 4, 4, bundleName: "");
        byte[] plain = File.ReadAllBytes(bundle);

        Assert.Throws<InvalidDataException>(() => new BundleReader().GetBundleName(plain));
    }

    /// <summary>The locate mechanism end to end on a synthetic packed chain: walk the physical file, extract
    /// each segment plain, read each segment's OWN logical name — including the mid-chain obfuscated one,
    /// which needs per-segment key recovery.</summary>
    [Fact]
    public void GetBundleName_PackedChain_EachSegmentSelfIdentifies()
    {
        using var t = new TempGame();
        string[] logicals =
        {
            "aaaa000000000000000000000000000a.bundle",
            "bbbb000000000000000000000000000b.bundle",
            "cccc000000000000000000000000000c.bundle",
        };
        byte[] testKey = { 0xA5, 0x3C, 0x77, 0x01, 0xEE, 0x42, 0x19, 0xB0, 0x08, 0xD3, 0x5F, 0x66, 0x2A, 0x91, 0xC4, 0x7D };
        using var ms = new MemoryStream();
        for (int i = 0; i < 3; i++)
        {
            string p = t.At($"seg{i}.bundle");
            SyntheticBundle.BuildOneTexture(p, $"seg{i}_texture", 4, 4, bundleName: logicals[i]);
            byte[] bytes = File.ReadAllBytes(p);
            if (i == 1) BundleObfuscation.XorPrefix(bytes, testKey);   // per-segment obfuscation, mid-chain
            ms.Write(bytes);
        }
        byte[] raw = ms.ToArray();

        var walk = BundleSegments.Walk(raw);

        Assert.Equal(3, walk.Segments.Count);
        var reader = new BundleReader();
        for (int i = 0; i < 3; i++)
        {
            byte[] plain = BundleSegments.ExtractPlain(raw, walk.Segments[i]);
            Assert.Equal(logicals[i], reader.GetBundleName(plain));
        }
    }
}
