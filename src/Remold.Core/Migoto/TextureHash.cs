using System;
using ATTextureFormat = AssetsTools.NET.Texture.TextureFormat;

namespace Remold.Core.Migoto;

/// <summary>
/// The 3DMigoto Texture2D resource hash — what <c>[TextureOverride] hash=</c> matches on. Computed
/// from the stock texture's bundle bytes and metadata, so a retexture is authored fully offline.
///
/// <para>Two CRC32C stages over one running value: <b>mip 0 only</b> (never the mip chain), then the
/// 44-byte <c>D3D11_TEXTURE2D_DESC</c> as eleven little-endian UINTs. Data-stage quirk: a byte length of
/// <c>Width*Height*ArraySize</c> (texels, not bytes) is used when it is the shorter of the two candidate
/// lengths.</para>
///
/// <para>Mip count and format are INPUTS, not assumptions — textures ship with and without a full mip
/// chain, and the sRGB flag selects a different DXGI number. Both land in the desc stage, so both must
/// come from real metadata or the hash is wrong.</para>
/// </summary>
public static class TextureHash
{
    /// <summary>Hash a stock texture. <paramref name="pictureData"/> is packed image data, mip 0 first;
    /// <paramref name="mipLevels"/> and <paramref name="dxgiFormat"/> must be the texture's real values.
    /// <paramref name="arraySize"/>/<paramref name="miscFlags"/> are (1, 0) for a plain 2D texture,
    /// (6, 4) for a cubemap.</summary>
    public static uint Compute(ReadOnlySpan<byte> pictureData, int width, int height, int mipLevels,
        uint dxgiFormat, int arraySize = 1, uint miscFlags = 0)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "texture has no extent");
        if (mipLevels <= 0) throw new ArgumentOutOfRangeException(nameof(mipLevels), "texture has no mip level");
        if (arraySize <= 0) throw new ArgumentOutOfRangeException(nameof(arraySize));

        long length = Mip0Length(width, height, dxgiFormat);
        long lengthV12 = (long)width * height * arraySize;
        long take = lengthV12 <= length ? lengthV12 : length;
        if (take > pictureData.Length)
            throw new ArgumentException(
                $"texture data is {pictureData.Length} bytes but mip 0 of {width}x{height} (DXGI {dxgiFormat}) needs {take}",
                nameof(pictureData));

        uint crc = BufferHash.Crc32c(pictureData[..(int)take]);
        return BufferHash.Crc32c(Desc(width, height, mipLevels, arraySize, dxgiFormat, miscFlags), crc);
    }

    /// <summary>The DXGI format number the game creates this Unity format with, or null when unmapped
    /// (the caller reports rather than hashing something else). <paramref name="srgb"/> picks the
    /// <c>_SRGB</c> variant where one exists. Crunched formats are deliberately unmapped: their stored
    /// bytes are not what the runtime uploads, so they have no offline hash.</summary>
    public static uint? Dxgi(ATTextureFormat format, bool srgb) => format switch
    {
        ATTextureFormat.DXT1 => srgb ? 72u : 71u,                                   // BC1_UNORM(_SRGB)
        ATTextureFormat.DXT5 => srgb ? 78u : 77u,                                   // BC3_UNORM(_SRGB)
        ATTextureFormat.BC4 => 80u,                                                 // BC4_UNORM
        ATTextureFormat.BC5 => 83u,                                                 // BC5_UNORM
        ATTextureFormat.BC6H => 95u,                                                // BC6H_UF16
        ATTextureFormat.BC7 => srgb ? 99u : 98u,                                    // BC7_UNORM(_SRGB)
        ATTextureFormat.RGBA32 => srgb ? 29u : 28u,                                 // R8G8B8A8_UNORM(_SRGB)
        ATTextureFormat.BGRA32 or ATTextureFormat.BGRA32Old => srgb ? 91u : 87u,    // B8G8R8A8_UNORM(_SRGB)
        ATTextureFormat.Alpha8 or ATTextureFormat.R8 => 61u,                        // R8_UNORM
        ATTextureFormat.R16 => 56u,                                                 // R16_UNORM
        ATTextureFormat.RGBAHalf => 10u,                                            // R16G16B16A16_FLOAT
        _ => null,
    };

    /// <summary>True when a DXGI format number is an <c>_SRGB</c> variant — the flag an authored
    /// replacement must match, since the sRGB→linear conversion happens on sample by tag, not by BCn
    /// format. Formats with no <c>_SRGB</c> variant (BC4, BC5, R8) are linear whatever the asset
    /// declares.</summary>
    public static bool IsSrgb(uint dxgiFormat) => dxgiFormat is 29 or 72 or 75 or 78 or 91 or 93 or 99;

    /// <summary>Tightly-packed byte length of mip 0: whole 4x4 blocks for a block-compressed format,
    /// row pitch times height otherwise.</summary>
    static long Mip0Length(int width, int height, uint dxgiFormat)
    {
        int block = BlockSize(dxgiFormat);
        if (block > 0)
            return (long)((width + 3) & ~3) * ((height + 3) & ~3) / 16 * block;
        int bpp = BytesPerPixel(dxgiFormat);
        if (bpp == 0)
            throw new NotSupportedException($"DXGI format {dxgiFormat} has no known pixel size");
        return (long)width * bpp * height;
    }

    /// <summary>Bytes per 4x4 block, or 0 for an uncompressed format. BC1/BC4 pack 8, the rest 16.</summary>
    static int BlockSize(uint dxgi) => dxgi switch
    {
        >= 70 and <= 72 => 8,      // BC1
        >= 79 and <= 81 => 8,      // BC4
        >= 73 and <= 78 => 16,     // BC2, BC3
        >= 82 and <= 84 => 16,     // BC5
        >= 94 and <= 96 => 16,     // BC6H
        >= 97 and <= 99 => 16,     // BC7
        _ => 0,
    };

    static int BytesPerPixel(uint dxgi) => dxgi switch
    {
        10 => 8,                     // R16G16B16A16_FLOAT
        28 or 29 or 87 or 91 => 4,   // R8G8B8A8 / B8G8R8A8
        56 => 2,                     // R16_UNORM
        61 => 1,                     // R8_UNORM
        _ => 0,
    };

    /// <summary>D3D11_TEXTURE2D_DESC — eleven little-endian UINTs, no padding.</summary>
    static byte[] Desc(int width, int height, int mipLevels, int arraySize, uint dxgiFormat, uint miscFlags)
    {
        var d = new byte[44];
        var f = new uint[]
        {
            (uint)width, (uint)height, (uint)mipLevels, (uint)arraySize, dxgiFormat,
            1, 0,       // SampleDesc: one sample, quality 0
            0,          // D3D11_USAGE_DEFAULT
            8,          // D3D11_BIND_SHADER_RESOURCE
            0,          // no CPU access
            miscFlags,
        };
        for (int i = 0; i < f.Length; i++) BitConverter.TryWriteBytes(d.AsSpan(4 * i, 4), f[i]);
        return d;
    }
}
