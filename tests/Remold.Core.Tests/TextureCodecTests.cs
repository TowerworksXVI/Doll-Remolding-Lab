using System;
using Remold.Core.Textures;
using ATTextureFormat = AssetsTools.NET.Texture.TextureFormat;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The package-time texture codec with exact mip-count control: the blob byte-size math against real
/// BCnEncoder output, exact mip-count generation, and the format mapping / block-alignment refusals.
/// </summary>
public class TextureCodecTests
{
    private static byte[] Rgba(int w, int h, int seed = 1)
    {
        var b = new byte[w * h * 4];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Theory]
    // BC1 = 8 B/block; BC3/BC7 = 16 B/block; 4×4 blocks. mip0-only sizes:
    [InlineData((int)ATTextureFormat.DXT1, 64, 64, 1, 2048)]   // (64/4)^2 * 8
    [InlineData((int)ATTextureFormat.DXT5, 64, 64, 1, 4096)]   // (64/4)^2 * 16
    [InlineData((int)ATTextureFormat.BC7, 64, 64, 1, 4096)]
    [InlineData((int)ATTextureFormat.RGBA32, 16, 16, 1, 1024)] // 16*16*4
    public void Encode_BlobLength_MatchesBlobSizeMath_SingleMip(int fmt, int w, int h, int mips, int expected)
    {
        var meta = (ATTextureFormat)fmt;
        Assert.Equal(expected, TextureCodec.BlobSize(meta, w, h, mips));
        var enc = TextureCodec.Encode(Rgba(w, h), w, h, meta, mips);
        Assert.Equal(mips, enc.MipCount);
        Assert.Equal(expected, enc.Data.Length);
        Assert.Equal(TextureCodec.BlobSize(meta, w, h, mips), enc.Data.Length);
    }

    [Fact]
    public void Encode_FullMipChain_LengthMatchesTheSummedLevels()
    {
        // BC7 64×64 = 7 levels: 4096,1024,256,64,16,16,16 — BCn floors each level at one 4×4 block (16 B).
        int w = 64, h = 64, mips = 7;
        long expect = TextureCodec.BlobSize(ATTextureFormat.BC7, w, h, mips);
        Assert.Equal(4096 + 1024 + 256 + 64 + 16 + 16 + 16, expect);
        var enc = TextureCodec.Encode(Rgba(w, h), w, h, ATTextureFormat.BC7, mips);
        Assert.Equal(mips, enc.MipCount);
        Assert.Equal(expect, enc.Data.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void Encode_ProducesExactlyTheRequestedMipCount(int mips)
    {
        // 64×64 admits 7 natural levels; any count 1..7 must come out exactly.
        var enc = TextureCodec.Encode(Rgba(64, 64), 64, 64, ATTextureFormat.BC7, mips);
        Assert.Equal(mips, enc.MipCount);
        Assert.Equal(TextureCodec.BlobSize(ATTextureFormat.BC7, 64, 64, mips), enc.Data.Length);
    }

    [Fact]
    public void Encode_MipCount1_ProducesOnlyTheBaseLevel()
    {
        var enc = TextureCodec.Encode(Rgba(128, 128), 128, 128, ATTextureFormat.BC7, mipCount: 1);
        Assert.Equal(1, enc.MipCount);
        Assert.Equal(TextureCodec.BlobSize(ATTextureFormat.BC7, 128, 128, 1), enc.Data.Length);
    }

    [Fact]
    public void Encode_Rejects_MipCountBeyondTheNaturalChain()
    {
        // Asking for 8 levels of a 7-level size means the live texture's mip count doesn't match its
        // size — refuse rather than pad.
        Assert.Throws<ArgumentException>(() => TextureCodec.Encode(Rgba(64, 64), 64, 64, ATTextureFormat.BC7, mipCount: 8));
    }

    [Fact]
    public void Encode_Rejects_NonBlockAlignedDimensions_ForBcn()
    {
        Assert.Throws<ArgumentException>(() => TextureCodec.Encode(Rgba(30, 30), 30, 30, ATTextureFormat.BC7, 1));
    }

    [Fact]
    public void Encode_Decode_RoundTrip_PreservesBaseLevelApproximately()
    {
        // A solid colour survives the BC7 round-trip within a couple of code values, proving the encode
        // wrote the base level and the decode reads it back.
        int w = 16, h = 16;
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < rgba.Length; i += 4) { rgba[i] = 200; rgba[i + 1] = 100; rgba[i + 2] = 50; rgba[i + 3] = 255; }
        var enc = TextureCodec.Encode(rgba, w, h, ATTextureFormat.BC7, mipCount: 1, quality: BCnEncoder.Encoder.CompressionQuality.BestQuality);
        var back = TextureCodec.DecodeToRgba(enc.Data, w, h, ATTextureFormat.BC7);
        Assert.InRange(back[0], (byte)195, (byte)205);
        Assert.InRange(back[1], (byte)95, (byte)105);
        Assert.InRange(back[2], (byte)45, (byte)55);
    }

    [Theory]
    [InlineData((int)ATTextureFormat.BC7, true)]
    [InlineData((int)ATTextureFormat.DXT1, true)]
    [InlineData((int)ATTextureFormat.DXT5, true)]
    [InlineData((int)ATTextureFormat.BC4, true)]
    [InlineData((int)ATTextureFormat.BC5, true)]
    [InlineData((int)ATTextureFormat.RGBA32, true)]
    [InlineData((int)ATTextureFormat.RGB24, true)]
    [InlineData((int)ATTextureFormat.RHalf, false)]   // HDR/LUT — no managed encoder
    [InlineData((int)ATTextureFormat.RGBAHalf, false)]
    public void IsSupported_MapsTheCharacterTextureFormats_RefusesHdr(int fmt, bool supported)
    {
        Assert.Equal(supported, TextureCodec.IsSupported((ATTextureFormat)fmt));
    }
}
