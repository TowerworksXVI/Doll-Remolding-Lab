using System;
using System.Collections.Generic;
using System.IO;
using Remold.Core.Bundles;
using Remold.Core.Migoto;
using Xunit;
using ATTextureFormat = AssetsTools.NET.Texture.TextureFormat;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The raw fp16 path a toon ramp travels: the hash's format mapping, the DDS container, and the read-back
/// that proves the bytes survived. Every fixture is SYNTHETIC — built here from half-float values — so the
/// suite states the format rather than depending on any installed asset.
/// </summary>
public class RampTextureFormatTests
{
    private const uint Fp16 = 10;         // DXGI_FORMAT_R16G16B16A16_FLOAT
    private const int RampWidth = 256, RampHeight = 16;

    // ---- synthetic fixtures ------------------------------------------------------------------------

    /// <summary>A ramp-shaped fp16 image: four horizontal bands of four rows, each band a distinct
    /// left-to-right gradient, alpha flat at 1. Distinct per band and per column, so a comparison that
    /// collapsed rows or channels would show.</summary>
    private static byte[] RampBytes(float bias = 0f)
    {
        var px = new byte[RampWidth * RampHeight * 8];
        int at = 0;
        for (int y = 0; y < RampHeight; y++)
        {
            int band = y / 4;
            for (int x = 0; x < RampWidth; x++)
            {
                float t = x / (float)(RampWidth - 1);
                Write(px, ref at, Clamp01(t + bias));
                Write(px, ref at, Clamp01(t * (band + 1) / 4f));
                Write(px, ref at, Clamp01(1f - t));
                Write(px, ref at, 1f);
            }
        }
        return px;

        static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
        static void Write(byte[] into, ref int at, float v)
        {
            BitConverter.TryWriteBytes(into.AsSpan(at, 2), (Half)v);
            at += 2;
        }
    }

    /// <summary>An fp16 level of arbitrary extent, filled from <paramref name="seed"/> so two levels never
    /// share bytes.</summary>
    private static byte[] Level(int width, int height, int seed)
    {
        var px = new byte[width * height * 8];
        for (int i = 0; i < width * height * 4; i++)
            BitConverter.TryWriteBytes(px.AsSpan(i * 2, 2), (Half)(((i + seed) % 37) / 36f));
        return px;
    }

    private static string Temp(string name) =>
        Path.Combine(Path.GetTempPath(), $"gf2-ramp-{Guid.NewGuid():N}-{name}");

    // ---- the format's arithmetic -------------------------------------------------------------------

    [Fact]
    public void A_ramp_level_is_eight_bytes_a_texel()
    {
        Assert.Equal(8, DdsWriter.BytesPerPixel(Fp16));
        Assert.Equal(RampWidth * 8, DdsWriter.LevelBytes(Fp16, RampWidth, 1));      // row pitch
        Assert.Equal(32_768, DdsWriter.LevelBytes(Fp16, RampWidth, RampHeight));
        Assert.Equal(32_768, RampBytes().Length);
    }

    [Fact]
    public void The_unity_fp16_format_maps_to_dxgi_ten_whichever_srgb_is_asked_for()
    {
        Assert.Equal(Fp16, TextureHash.Dxgi(ATTextureFormat.RGBAHalf, srgb: false));
        Assert.Equal(Fp16, TextureHash.Dxgi(ATTextureFormat.RGBAHalf, srgb: true));
        Assert.False(TextureHash.IsSrgb(Fp16));
    }

    /// <summary>The hash accepted this content at all — the same call threw for want of a pixel size before
    /// — and answers the same value every time. What it covers is the algorithm's business; this pins only
    /// that the production path takes the format and is deterministic.</summary>
    [Fact]
    public void The_hash_takes_fp16_content_and_is_deterministic()
    {
        var px = RampBytes();
        uint first = TextureHash.Compute(px, RampWidth, RampHeight, 1, Fp16);
        Assert.Equal(first, TextureHash.Compute(px, RampWidth, RampHeight, 1, Fp16));
        Assert.Equal(first, TextureHash.Compute(RampBytes(), RampWidth, RampHeight, 1, Fp16));
    }

    // ---- what a GAME ramp's stored bytes have to be before any of them is written -------------------
    // RampConversion.WriteRaw is the one route a game ramp reaches the container by, and it writes the
    // declared mip chain or nothing: a file half of a chain long is one the build ships and the runtime
    // draws with.

    [Fact]
    public void A_declared_mip_chain_must_be_complete_before_any_dds_is_written()
    {
        var truncated = new BundleReader.TextureHashSource(new byte[RampWidth * RampHeight * 8 + 1],
            RampWidth, RampHeight, 2, (int)ATTextureFormat.RGBAHalf, false);
        var dest = Temp("truncated.dds");

        Assert.False(RampConversion.WriteRaw(truncated, dest));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void Bytes_outside_the_declared_mip_chain_are_refused_before_any_dds_is_written()
    {
        var oversized = new BundleReader.TextureHashSource(new byte[RampWidth * RampHeight * 8 + 1],
            RampWidth, RampHeight, 1, (int)ATTextureFormat.RGBAHalf, false);
        var dest = Temp("oversized.dds");

        Assert.False(RampConversion.WriteRaw(oversized, dest));
        Assert.False(File.Exists(dest));
    }

    // ---- the container -----------------------------------------------------------------------------

    [Fact]
    public void A_written_ramp_declares_fp16_at_the_ramps_pitch()
    {
        var path = Temp("ramp.dds");
        try
        {
            using (var s = File.Create(path))
                DdsWriter.Write(s, Fp16, RampWidth, RampHeight, new[] { RampBytes() });
            var raw = File.ReadAllBytes(path);

            Assert.Equal(DdsWriter.HeaderBytes + 32_768, raw.Length);
            Assert.Equal((uint)(RampWidth * 8), BitConverter.ToUInt32(raw, 4 + 16));    // pitch field
            Assert.Equal(0x8u, BitConverter.ToUInt32(raw, 4 + 4) & 0x8u);               // DDSD_PITCH
            Assert.Equal(Fp16, BitConverter.ToUInt32(raw, 4 + 124));                    // DXT10 format
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_ramps_bytes_survive_the_round_trip_exactly()
    {
        var px = RampBytes();
        var path = Temp("roundtrip.dds");
        try
        {
            using (var s = File.Create(path))
                DdsWriter.Write(s, Fp16, RampWidth, RampHeight, new[] { px });
            var back = DdsReader.Read(path);

            Assert.Equal(Fp16, back.DxgiFormat);
            Assert.Equal(RampWidth, back.Width);
            Assert.Equal(RampHeight, back.Height);
            Assert.Equal(px, Assert.Single(back.Levels));
        }
        finally { File.Delete(path); }
    }

    /// <summary>A single-level image whose writer left <c>dwMipMapCount</c> at zero is a legal DDS that
    /// other tools produce, and it carries one level. Refusing it would turn a modder's perfectly good ramp
    /// into a build failure with nothing to fix.</summary>
    [Fact]
    public void A_declared_mip_count_of_zero_reads_as_the_one_level_it_carries()
    {
        var px = RampBytes();
        var path = Temp("nomipcount.dds");
        try
        {
            using (var s = File.Create(path))
                DdsWriter.Write(s, Fp16, RampWidth, RampHeight, new[] { px });
            var raw = File.ReadAllBytes(path);
            BitConverter.TryWriteBytes(raw.AsSpan(4 + 24, 4), 0u);      // dwMipMapCount
            File.WriteAllBytes(path, raw);

            var back = DdsReader.Read(path);

            Assert.Equal(px, Assert.Single(back.Levels));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_whole_mip_chain_survives_the_round_trip_exactly()
    {
        var levels = new List<byte[]>();
        for (int i = 0, w = 8, h = 8; i < 4; i++, w >>= 1, h >>= 1) levels.Add(Level(w, h, i * 7 + 1));
        var path = Temp("chain.dds");
        try
        {
            using (var s = File.Create(path)) DdsWriter.Write(s, Fp16, 8, 8, levels);
            var back = DdsReader.Read(path);

            Assert.Equal(4, back.Levels.Count);
            for (int i = 0; i < levels.Count; i++) Assert.Equal(levels[i], back.Levels[i]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_level_whose_byte_count_is_not_the_formats_size_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DdsWriter.Write(Stream.Null, Fp16, RampWidth, RampHeight, new[] { new byte[32_767] }));
        Assert.Contains("mip level 0 is 32767 bytes", ex.Message);
    }

    // ---- the reader refuses rather than guessing ---------------------------------------------------

    [Fact]
    public void A_truncated_payload_is_refused_by_name()
    {
        var path = Temp("short.dds");
        try
        {
            using (var s = File.Create(path))
                DdsWriter.Write(s, Fp16, RampWidth, RampHeight, new[] { RampBytes() });
            var raw = File.ReadAllBytes(path);
            File.WriteAllBytes(path, raw[..(raw.Length - 16)]);

            var ex = Assert.Throws<InvalidDataException>(() => DdsReader.Read(path));
            Assert.Contains("mip level 0", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Bytes_past_the_declared_chain_are_refused()
    {
        var path = Temp("long.dds");
        try
        {
            using (var s = File.Create(path))
                DdsWriter.Write(s, Fp16, RampWidth, RampHeight, new[] { RampBytes() });
            var raw = File.ReadAllBytes(path);
            File.WriteAllBytes(path, [.. raw, 1, 2, 3, 4]);

            var ex = Assert.Throws<InvalidDataException>(() => DdsReader.Read(path));
            Assert.Contains("4 bytes past", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_format_outside_the_round_trip_is_refused_rather_than_decoded()
    {
        var path = Temp("rgba8.dds");
        try
        {
            using (var s = File.Create(path))
                DdsWriter.Write(s, DdsWriter.R8G8B8A8_UNORM, 4, 4, new[] { new byte[4 * 4 * 4] });

            var ex = Assert.Throws<InvalidDataException>(() => DdsReader.Read(path));
            Assert.Contains("DXGI format 28", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_file_too_short_to_hold_a_header_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => DdsReader.Parse(new byte[16]));
        Assert.Contains("too short", ex.Message);
    }
}
