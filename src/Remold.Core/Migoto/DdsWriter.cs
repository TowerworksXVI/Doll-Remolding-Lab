using System;
using System.Collections.Generic;
using System.IO;
using Remold.Core.Textures;
using ATTextureFormat = AssetsTools.NET.Texture.TextureFormat;

namespace Remold.Core.Migoto;

/// <summary>
/// Serialises the DX10-header DDS files a generated mod ships. 3DMigoto loads a <c>.dds</c> verbatim and
/// generates nothing at runtime, so the container must carry the format tag and every mip level.
///
/// <para>Only the DXGI tags the build emits are accepted (R8G8B8A8 and BC7, each UNORM and <c>_SRGB</c>,
/// plus the fp16 <c>R16G16B16A16_FLOAT</c> the ramp path carries verbatim); an unknown tag throws rather
/// than guessing the <c>PITCH</c>/<c>LINEARSIZE</c> field. Likewise a chain longer than the dimensions
/// admit, or a level whose byte count isn't the format's size, is refused — such a file parses as a DDS
/// and then fails texture creation, surfacing as an override that silently never binds. A TRUNCATED chain
/// is legal (the flat maps ship one level).</para>
/// </summary>
internal static class DdsWriter
{
    /// <summary>Bytes before the first pixel: magic + <c>DDS_HEADER</c> + <c>DDS_HEADER_DXT10</c>.</summary>
    public const int HeaderBytes = 4 + 124 + 20;

    public const uint R16G16B16A16_FLOAT = 10, R8G8B8A8_UNORM = 28, R8G8B8A8_UNORM_SRGB = 29,
        BC7_UNORM = 98, BC7_UNORM_SRGB = 99;

    /// <summary>True for the block-compressed tags this writer emits: their header field is a
    /// whole-level byte count, where the uncompressed ones use a row pitch.</summary>
    public static bool IsBlockCompressed(uint dxgiFormat) => dxgiFormat is BC7_UNORM or BC7_UNORM_SRGB;

    /// <summary>Bytes one texel occupies in an uncompressed tag this writer accepts, or 0 for anything
    /// else — the row pitch's one multiplier, so the header field and the per-level size check cannot
    /// disagree.</summary>
    public static int BytesPerPixel(uint dxgiFormat) => dxgiFormat switch
    {
        R16G16B16A16_FLOAT => 8,
        R8G8B8A8_UNORM or R8G8B8A8_UNORM_SRGB => 4,
        _ => 0,
    };

    /// <summary>Tightly-packed byte count of one uncompressed level.</summary>
    public static long LevelBytes(uint dxgiFormat, int width, int height) =>
        (long)width * BytesPerPixel(dxgiFormat) * height;

    /// <summary>Write the header for <paramref name="levels"/> (largest first, one per mip) then the
    /// level bytes. <paramref name="width"/>/<paramref name="height"/> are the base level; the rest of
    /// the chain is implied by the level count.</summary>
    public static void Write(Stream stream, uint dxgiFormat, int width, int height, IReadOnlyList<byte[]> levels)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"invalid DDS dimensions {width}x{height}");
        if (levels.Count == 0) throw new ArgumentException("a DDS needs at least the base level", nameof(levels));
        bool block = IsBlockCompressed(dxgiFormat);
        if (!block && BytesPerPixel(dxgiFormat) == 0)
            throw new ArgumentException($"DXGI format {dxgiFormat} is not one this writer can tag", nameof(dxgiFormat));

        int natural = TextureCodec.MipChainLength(width, height);
        if (levels.Count > natural)
            throw new ArgumentException(
                $"{levels.Count} mip levels for {width}x{height}, which admits only {natural}", nameof(levels));
        int lw = width, lh = height;
        for (int i = 0; i < levels.Count; i++)
        {
            long want = block ? TextureCodec.BlobSize(ATTextureFormat.BC7, lw, lh, 1)
                              : LevelBytes(dxgiFormat, lw, lh);
            if (levels[i].Length != want)
                throw new ArgumentException(
                    $"mip level {i} is {levels[i].Length} bytes; DXGI {dxgiFormat} at {lw}x{lh} is {want}",
                    nameof(levels));
            lw = Math.Max(1, lw >> 1);
            lh = Math.Max(1, lh >> 1);
        }

        var w = new BinaryWriter(stream);
        w.Write(new byte[] { (byte)'D', (byte)'D', (byte)'S', (byte)' ' });
        const uint CAPS = 0x1, HEIGHT = 0x2, WIDTH = 0x4, PITCH = 0x8, PIXELFORMAT = 0x1000,
                   MIPMAPCOUNT = 0x20000, LINEARSIZE = 0x80000;
        uint flags = CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT | (block ? LINEARSIZE : PITCH);
        uint pitchOrLinearSize = block ? (uint)levels[0].Length : (uint)(width * BytesPerPixel(dxgiFormat));
        WriteUInts(w, 124, flags, (uint)height, (uint)width, pitchOrLinearSize, 0, (uint)levels.Count);
        w.Write(new byte[44]);                                  // dwReserved1[11]
        WriteUInts(w, 32, 0x4);                                 // DDS_PIXELFORMAT size, DDPF_FOURCC
        w.Write(new byte[] { (byte)'D', (byte)'X', (byte)'1', (byte)'0' });
        w.Write(new byte[20]);                                  // the rest of DDS_PIXELFORMAT
        const uint TEXTURE = 0x1000, COMPLEX = 0x8, MIPMAP = 0x400000;
        WriteUInts(w, TEXTURE | (levels.Count > 1 ? COMPLEX | MIPMAP : 0), 0, 0, 0, 0);   // caps..reserved2
        WriteUInts(w, dxgiFormat, 3, 0, 1, 0);                  // DXT10: format, TEXTURE2D, -, arraySize 1, -
        foreach (var level in levels) w.Write(level);
        w.Flush();
    }

    static void WriteUInts(BinaryWriter w, params uint[] values)
    {
        foreach (var v in values) w.Write(v);
    }
}
