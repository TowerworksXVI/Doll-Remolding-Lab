using System;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ATTextureFormat = AssetsTools.NET.Texture.TextureFormat;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// <see cref="AuthoredDds"/>: BC7, tagged sRGB or UNORM per the slot, carrying the WHOLE mip chain (nothing
/// generates one at runtime), in the row order the game samples — Unity Vs against bottom-up rows, while
/// every workspace image is top-down. A <c>.dds</c> source stays a verbatim passthrough: it is already
/// native-ordered and already in a chosen format.
/// </summary>
public class AuthoredDdsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-add-" + Guid.NewGuid().ToString("N"));

    public AuthoredDdsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const int HeaderBytes = DdsWriter.HeaderBytes;   // magic + DDS_HEADER + DX10 header

    /// <summary>The DDS header fields this suite reads back, by their byte offsets in the file.</summary>
    private sealed record Header(uint Flags, int Height, int Width, uint PitchOrLinearSize, int MipCount,
        uint Caps, uint DxgiFormat, long PayloadBytes)
    {
        public static Header Read(string path)
        {
            var b = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { (byte)'D', (byte)'D', (byte)'S', (byte)' ' }, b[..4]);
            uint U(int off) => BitConverter.ToUInt32(b, off);
            return new Header(U(8), (int)U(12), (int)U(16), U(20), (int)U(28), U(108), U(128),
                b.Length - HeaderBytes);
        }
    }

    private string WritePng(int w, int h, Action<Image<Rgba32>>? paint = null)
    {
        string p = Path.Combine(_root, $"src{w}x{h}.png");
        using var img = new Image<Rgba32>(w, h, new Rgba32(90, 140, 200, 255));
        paint?.Invoke(img);
        img.SaveAsPng(p);
        return p;
    }

    [Fact]
    public void A_decoded_source_becomes_BC7_with_the_full_mip_chain()
    {
        string dst = Path.Combine(_root, "out.dds");
        AuthoredDds.Encode(WritePng(64, 64), dst, srgb: true);

        var h = Header.Read(dst);
        Assert.Equal(99u, h.DxgiFormat);        // BC7_UNORM_SRGB
        Assert.Equal(64, h.Width);
        Assert.Equal(64, h.Height);
        // 64×64 chains down to 1×1 in 7 levels, and every one of them ships
        Assert.Equal(7, h.MipCount);
        Assert.Equal(TextureCodec.MipChainLength(64, 64), h.MipCount);
        Assert.Equal(TextureCodec.BlobSize(ATTextureFormat.BC7, 64, 64, 7), h.PayloadBytes);
        // block-compressed headers carry the base level's byte count, not a row pitch
        Assert.Equal(4096u, h.PitchOrLinearSize);
        Assert.Equal(0x80000u, h.Flags & 0x80000u);   // DDSD_LINEARSIZE
        Assert.Equal(0u, h.Flags & 0x8u);             // not DDSD_PITCH
        Assert.Equal(0x20000u, h.Flags & 0x20000u);   // DDSD_MIPMAPCOUNT
        Assert.Equal(0x400008u, h.Caps & 0x400008u);  // DDSCAPS_COMPLEX | DDSCAPS_MIPMAP
    }

    [Fact]
    public void An_independent_dds_reader_walks_the_whole_chain_level_by_level()
    {
        // The assertions above read offsets this suite chose; this hands the file to a parser written
        // elsewhere and checks it agrees about the format and every level's extent.
        string dst = Path.Combine(_root, "read.dds");
        AuthoredDds.Encode(WritePng(64, 32), dst, srgb: true);

        using var s = File.OpenRead(dst);
        var dds = BCnEncoder.Shared.ImageFiles.DdsFile.Load(s);
        Assert.Equal(BCnEncoder.Shared.ImageFiles.DxgiFormat.DxgiFormatBc7UnormSrgb, dds.dx10Header.dxgiFormat);
        var face = Assert.Single(dds.Faces);
        Assert.Equal(64u, face.Width);
        Assert.Equal(32u, face.Height);
        Assert.Equal(TextureCodec.MipChainLength(64, 32), face.MipMaps.Length);

        uint w = 64, h = 32;
        foreach (var mip in face.MipMaps)
        {
            Assert.Equal(w, mip.Width);
            Assert.Equal(h, mip.Height);
            Assert.Equal(TextureCodec.BlobSize(ATTextureFormat.BC7, (int)w, (int)h, 1), mip.Data.Length);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
    }

    [Theory]
    [InlineData(true, 99u)]    // BC7_UNORM_SRGB
    [InlineData(false, 98u)]   // BC7_UNORM
    public void The_srgb_flag_picks_the_dxgi_format_the_slot_expects(bool srgb, uint expected)
    {
        string dst = Path.Combine(_root, $"out{srgb}.dds");
        AuthoredDds.Encode(WritePng(16, 16), dst, srgb);
        Assert.Equal(expected, Header.Read(dst).DxgiFormat);
    }

    [Fact]
    public void Dimensions_off_the_block_grid_still_encode_whole_blocks()
    {
        // A DDS stores whole 4×4 blocks with padded edges — an authored map need not be a multiple of four.
        string dst = Path.Combine(_root, "odd.dds");
        AuthoredDds.Encode(WritePng(30, 30), dst, srgb: true);

        var h = Header.Read(dst);
        Assert.Equal(30, h.Width);
        Assert.Equal(30, h.Height);
        Assert.Equal(TextureCodec.MipChainLength(30, 30), h.MipCount);
        Assert.Equal(TextureCodec.BlobSize(ATTextureFormat.BC7, 30, 30, h.MipCount), h.PayloadBytes);
    }

    [Fact]
    public void A_one_pixel_source_ships_a_single_level_container()
    {
        // 1×1 admits exactly ONE level, but the encoder hands back two. A container declaring two mips for
        // a 1×1 fails texture creation outright, so the override never binds and the build still calls
        // itself a success.
        string dst = Path.Combine(_root, "one.dds");
        AuthoredDds.Encode(WritePng(1, 1), dst, srgb: true);

        var h = Header.Read(dst);
        Assert.Equal(1, h.MipCount);
        Assert.Equal(16, h.PayloadBytes);   // one BC7 block
        Assert.Equal(1, h.Width);
        Assert.Equal(1, h.Height);
    }

    [Fact]
    public void The_writer_refuses_a_chain_longer_than_the_dimensions_admit()
    {
        var ex = Assert.Throws<ArgumentException>(() => DdsWriter.Write(new MemoryStream(),
            DdsWriter.BC7_UNORM, 1, 1, new[] { new byte[16], new byte[16] }));
        Assert.Contains("admits only 1", ex.Message);
    }

    [Fact]
    public void The_writer_refuses_a_level_that_is_not_the_formats_size()
    {
        // a level short of its format size still parses as a DDS and then reads past its own payload
        var ex = Assert.Throws<ArgumentException>(() => DdsWriter.Write(new MemoryStream(),
            DdsWriter.R8G8B8A8_UNORM, 8, 8, new[] { new byte[100] }));
        Assert.Contains("mip level 0 is 100 bytes", ex.Message);
    }

    [Fact]
    public void A_decoded_source_writes_the_png_bottom_row_first()
    {
        // Top half red, bottom half blue: the base level's FIRST block row decodes BLUE, so the flip to
        // Unity's bottom-up order happened before the encode.
        string src = WritePng(8, 8, img =>
        {
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    img[x, y] = y < 4 ? new Rgba32(255, 0, 0, 255) : new Rgba32(0, 0, 255, 255);
        });
        string dst = Path.Combine(_root, "flip.dds");
        AuthoredDds.Encode(src, dst, srgb: true);

        var payload = File.ReadAllBytes(dst)[HeaderBytes..];
        var rgba = TextureCodec.DecodeToRgba(payload, 8, 8, ATTextureFormat.BC7);
        Assert.True(rgba[2] > 200 && rgba[0] < 55, "first stored row should be blue (the PNG's bottom)");
        int last = (7 * 8) * 4;
        Assert.True(rgba[last] > 200 && rgba[last + 2] < 55, "last stored row should be red (the PNG's top)");
    }

    [Fact]
    public void The_mip_chain_downsamples_alpha_without_premultiplying()
    {
        // A packed normal carries X in ALPHA, so a chain that premultiplied or thresholded alpha would
        // corrupt every level below the base.
        var rgba = new byte[2 * 2 * 4];
        for (int i = 0; i < 4; i++)
        {
            rgba[i * 4 + 0] = 200; rgba[i * 4 + 1] = 100; rgba[i * 4 + 2] = 50;
            rgba[i * 4 + 3] = i < 2 ? (byte)0 : (byte)255;
        }
        var levels = TextureCodec.EncodeMipChain(rgba, 2, 2, ATTextureFormat.RGBA32);
        Assert.Equal(2, levels.Length);
        var mip1 = levels[1];
        Assert.Equal(4, mip1.Length);
        Assert.InRange(mip1[3], (byte)120, (byte)136);   // alpha averaged, not premultiplied to 0
        Assert.InRange(mip1[0], (byte)195, (byte)205);   // colour untouched by the alpha
        Assert.InRange(mip1[1], (byte)95, (byte)105);
        Assert.InRange(mip1[2], (byte)45, (byte)55);
    }

    [Fact]
    public void The_encode_is_deterministic_so_a_rebuild_ships_identical_bytes()
    {
        string src = WritePng(32, 32, img =>
        {
            var rnd = new Random(11);
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                    img[x, y] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
        });
        string a = Path.Combine(_root, "a.dds"), b = Path.Combine(_root, "b.dds");
        AuthoredDds.Encode(src, a, srgb: true);
        AuthoredDds.Encode(src, b, srgb: true);
        Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
    }

    [Fact]
    public void A_dds_source_passes_through_verbatim()
    {
        string src = Path.Combine(_root, "native.dds");
        FlatDds.Write(src, (1, 2, 3, 255), size: 4);   // format and content are the author's choice
        string dst = Path.Combine(_root, "out.dds");
        AuthoredDds.Encode(src, dst, srgb: true);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dst));
    }

    [Fact]
    public void A_source_named_dds_that_carries_no_dds_header_throws()
    {
        // The passthrough trusts the EXTENSION for the format, so the bytes must agree — a renamed PNG
        // would otherwise ship as a container nothing can bind.
        string src = Path.Combine(_root, "renamed.dds");
        File.Copy(WritePng(8, 8), src);
        var ex = Assert.Throws<InvalidDataException>(() =>
            AuthoredDds.Encode(src, Path.Combine(_root, "out.dds"), srgb: true));
        Assert.Contains("no DDS header", ex.Message);
    }

    [Fact]
    public void A_missing_source_throws_rather_than_shipping_no_map()
    {
        // The wording reaches the build footer, so it names the workspace file the author has to restore
        // rather than the runtime's own "could not find file".
        string absent = Path.Combine(_root, "absent.png");
        var ex = Assert.Throws<FileNotFoundException>(() =>
            AuthoredDds.Encode(absent, Path.Combine(_root, "out.dds"), srgb: true));
        Assert.StartsWith($"texture not found: {absent}", ex.Message);
    }

    [Fact]
    public void Identifying_a_missing_source_reads_the_same_as_encoding_one()
    {
        // A caller that identifies an image before encoding it reaches the missing file FIRST; one wording
        // for both, or the same deleted PNG reads two ways depending on which route the build took.
        string absent = Path.Combine(_root, "gone.png");
        var ex = Assert.Throws<FileNotFoundException>(() => AuthoredDds.SourceIdentity(absent));
        Assert.StartsWith($"texture not found: {absent}", ex.Message);
    }
}
