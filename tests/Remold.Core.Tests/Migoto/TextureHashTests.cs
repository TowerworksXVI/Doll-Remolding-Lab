using System;
using Remold.Core.Migoto;
using Xunit;
using ATTextureFormat = AssetsTools.NET.Texture.TextureFormat;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// <see cref="TextureHash"/> against the two-stage definition RESTATED INDEPENDENTLY here: CRC32C over
/// mip 0 (with the texel-count length quirk), continued over the 44-byte description. The expected values
/// are computed from that definition, so the production path is checked against the specification.
/// </summary>
public class TextureHashTests
{
    // ---- the specification, restated ---------------------------------------------------------------

    private static uint Crc32c(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc ^= 0xffffffff;
        foreach (var b in data)
        {
            uint c = (crc ^ b) & 0xff;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0x82f63b78 : c >> 1;
            crc = c ^ (crc >> 8);
        }
        return crc ^ 0xffffffff;
    }

    private static uint Expected(byte[] data, int width, int height, int mips, int arraySize,
        uint dxgi, int mip0Length, uint misc = 0)
    {
        int lengthV12 = width * height * arraySize;
        int take = Math.Min(lengthV12 <= mip0Length ? lengthV12 : mip0Length, data.Length);
        uint crc = Crc32c(data.AsSpan(0, take));
        var desc = new byte[44];
        var fields = new uint[]
        {
            (uint)width, (uint)height, (uint)mips, (uint)arraySize, dxgi,
            1, 0, 0, 8, 0, misc,
        };
        for (int i = 0; i < fields.Length; i++) BitConverter.TryWriteBytes(desc.AsSpan(4 * i, 4), fields[i]);
        return Crc32c(desc, crc);
    }

    private static byte[] Pattern(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 37 + 11);
        return b;
    }

    // ---- the checks --------------------------------------------------------------------------------

    [Fact]
    public void The_hardware_crc32c_agrees_with_the_table_on_every_length()
    {
        // SSE4.2's crc32 IS this polynomial, so the two paths are one function. The same build takes
        // different ones on different machines, and a disagreement would address one mesh by two hashes.
        Assert.Equal(0xE3069283u, BufferHash.Crc32c(System.Text.Encoding.ASCII.GetBytes("123456789")));

        // lengths on both sides of the 8-byte block the hardware path consumes, and the odd tail after it
        var data = Pattern(137);
        for (int len = 0; len <= data.Length; len++)
            Assert.Equal(BufferHash.Crc32cSoftware(data.AsSpan(0, len)), BufferHash.Crc32c(data.AsSpan(0, len)));

        // and the continuation form, which spans two buffers without joining them
        Assert.Equal(BufferHash.Crc32cSoftware(data.AsSpan(9), BufferHash.Crc32cSoftware(data.AsSpan(0, 9))),
                     BufferHash.Crc32c(data.AsSpan(9), BufferHash.Crc32c(data.AsSpan(0, 9))));
    }

    [Fact]
    public void Bc7_takes_the_block_packed_mip0_length()
    {
        // 8x8 BC7: 64 packed bytes versus 64 texels — equal, so either branch reads the same bytes.
        var data = Pattern(64 + 16 + 16);
        uint got = TextureHash.Compute(data, 8, 8, mipLevels: 4, dxgiFormat: 99);
        Assert.Equal(Expected(data, 8, 8, 4, 1, 99, mip0Length: 64), got);
    }

    [Fact]
    public void Bc1_hashes_the_texel_count_prefix_when_it_is_shorter()
    {
        // 16x16 BC1: 4x4 blocks of 8 bytes = 128 bytes, but 16*16*1 = 256 texels. The longer
        // candidate loses, so 128 bytes are hashed.
        var data = Pattern(128 + 32);
        uint got = TextureHash.Compute(data, 16, 16, mipLevels: 5, dxgiFormat: 72);
        Assert.Equal(Expected(data, 16, 16, 5, 1, 72, mip0Length: 128), got);
    }

    [Fact]
    public void Uncompressed_hashes_the_texel_count_prefix()
    {
        // 4x4 RGBA8: 64 packed bytes against 16 texels — for an uncompressed format the texel count is
        // never the longer candidate, so only 16 bytes are hashed.
        var data = Pattern(64);
        uint got = TextureHash.Compute(data, 4, 4, mipLevels: 1, dxgiFormat: 28);
        Assert.Equal(Expected(data, 4, 4, 1, 1, 28, mip0Length: 64), got);
    }

    [Fact]
    public void Mip_count_and_format_and_misc_flags_all_change_the_hash()
    {
        var data = Pattern(64);
        uint baseline = TextureHash.Compute(data, 8, 8, mipLevels: 4, dxgiFormat: 99);
        Assert.NotEqual(baseline, TextureHash.Compute(data, 8, 8, mipLevels: 1, dxgiFormat: 99));
        Assert.NotEqual(baseline, TextureHash.Compute(data, 8, 8, mipLevels: 4, dxgiFormat: 98));
        Assert.NotEqual(baseline, TextureHash.Compute(data, 8, 8, mipLevels: 4, dxgiFormat: 99, miscFlags: 4));
    }

    [Fact]
    public void A_cubemap_hashes_its_array_size_and_the_longer_data_run()
    {
        // ArraySize 6 makes the texel count 6*8*8 = 384, longer than the 64 packed bytes, so the
        // packed length wins and the desc carries ArraySize 6 + the cube misc flag.
        var data = Pattern(64 * 6);
        uint got = TextureHash.Compute(data, 8, 8, mipLevels: 1, dxgiFormat: 95, arraySize: 6, miscFlags: 4);
        Assert.Equal(Expected(data, 8, 8, 1, 6, 95, mip0Length: 64, misc: 4), got);
    }

    [Fact]
    public void Data_shorter_than_mip0_is_a_loud_failure()
    {
        var ex = Assert.Throws<ArgumentException>(() => TextureHash.Compute(Pattern(32), 16, 16, 1, 72));
        Assert.Contains("needs 128", ex.Message);
    }

    [Fact]
    public void Unity_formats_map_to_the_srgb_variant_only_where_one_exists()
    {
        Assert.Equal(72u, TextureHash.Dxgi(ATTextureFormat.DXT1, srgb: true));
        Assert.Equal(71u, TextureHash.Dxgi(ATTextureFormat.DXT1, srgb: false));
        Assert.Equal(99u, TextureHash.Dxgi(ATTextureFormat.BC7, srgb: true));
        Assert.Equal(98u, TextureHash.Dxgi(ATTextureFormat.BC7, srgb: false));
        Assert.Equal(83u, TextureHash.Dxgi(ATTextureFormat.BC5, srgb: true));   // no sRGB BC5 exists
        Assert.Equal(29u, TextureHash.Dxgi(ATTextureFormat.RGBA32, srgb: true));
        // crunched bytes are not the bytes the runtime uploads, so they have no offline hash
        Assert.Null(TextureHash.Dxgi(ATTextureFormat.DXT1Crunched, srgb: true));
    }

    [Fact]
    public void The_srgb_family_is_read_off_the_dxgi_number_an_authored_map_must_match()
    {
        foreach (uint srgb in new uint[] { 29, 72, 75, 78, 91, 93, 99 })
            Assert.True(TextureHash.IsSrgb(srgb), $"DXGI {srgb} is an _SRGB format");
        foreach (uint linear in new uint[] { 28, 71, 74, 77, 87, 92, 98, 80, 83, 95, 61, 56 })
            Assert.False(TextureHash.IsSrgb(linear), $"DXGI {linear} is not an _SRGB format");

        // a format with no _SRGB variant stays linear however the asset declares its colour space
        Assert.False(TextureHash.IsSrgb(TextureHash.Dxgi(ATTextureFormat.BC5, srgb: true)!.Value));
        Assert.True(TextureHash.IsSrgb(TextureHash.Dxgi(ATTextureFormat.DXT1, srgb: true)!.Value));
    }
}
