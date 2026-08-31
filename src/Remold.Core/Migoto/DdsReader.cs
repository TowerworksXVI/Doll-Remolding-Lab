using System;
using System.Collections.Generic;
using System.IO;

namespace Remold.Core.Migoto;

/// <summary>One decoded DX10-header DDS: the tag, the base level's extent, and the mip chain's raw bytes,
/// largest first. The bytes are exactly what the container carried — nothing is converted, resampled or
/// re-tagged on the way through.</summary>
public sealed record DdsImage(uint DxgiFormat, int Width, int Height, IReadOnlyList<byte[]> Levels);

/// <summary>
/// Reads back the DX10-header DDS files <see cref="DdsWriter"/> writes, for the formats whose bytes travel
/// verbatim: the fp16 <c>R16G16B16A16_FLOAT</c> a toon ramp is stored in.
///
/// <para>STRICT on purpose. A DDS whose declared layout and byte count disagree still parses as a DDS and
/// then fails texture creation at bind time, which surfaces as an override that silently never draws — so
/// every claim the header makes is checked against the payload and a mismatch is refused by name. Formats
/// outside the round-trip are refused rather than guessed at: this reader exists to prove bytes survived a
/// write, not to be a general DDS decoder.</para>
///
/// <para>Public because a ramp is picked in the app before any build reads it: the pick surface refuses a
/// file this reader refuses, so a ramp that would fail the build is never recorded in the first place.</para>
/// </summary>
public static class DdsReader
{
    private const uint DDPF_FOURCC = 0x4;
    private const uint DX10 = 0x30315844;          // 'D','X','1','0' little-endian
    private const uint DDS_MAGIC = 0x20534444;     // 'D','D','S',' ' little-endian
    private const uint DIMENSION_TEXTURE2D = 3;

    /// <summary>One little-endian UINT of the header at <paramref name="offset"/>.</summary>
    private static uint U(ReadOnlySpan<byte> bytes, int offset) =>
        BitConverter.ToUInt32(bytes[offset..(offset + 4)]);

    /// <summary>Parse <paramref name="path"/>. Throws <see cref="InvalidDataException"/> naming what did not
    /// hold.</summary>
    public static DdsImage Read(string path) => Parse(File.ReadAllBytes(path), path);

    /// <summary>Parse a whole DDS file's bytes. <paramref name="what"/> names the source in refusals.</summary>
    public static DdsImage Parse(ReadOnlySpan<byte> bytes, string what = "DDS")
    {
        if (bytes.Length < DdsWriter.HeaderBytes)
            throw new InvalidDataException(
                $"{what} is {bytes.Length} bytes, too short for a DX10 DDS header ({DdsWriter.HeaderBytes})");

        if (U(bytes, 0) != DDS_MAGIC) throw new InvalidDataException($"{what} does not start with the DDS magic");
        if (U(bytes, 4) != 124) throw new InvalidDataException($"{what} declares a {U(bytes, 4)}-byte header, not 124");

        // DDS_PIXELFORMAT sits at offset 72 of DDS_HEADER: dwSize, dwFlags, dwFourCC, ...
        if ((U(bytes, 4 + 72 + 4) & DDPF_FOURCC) == 0 || U(bytes, 4 + 72 + 8) != DX10)
            throw new InvalidDataException($"{what} carries no DX10 header; this reader accepts only DX10 DDS");

        int height = (int)U(bytes, 4 + 8), width = (int)U(bytes, 4 + 12);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"{what} declares dimensions {width}x{height}");

        // A zero count is what several DDS writers leave in the field for a single-level image, and the
        // format's own single-level shape; it means one level, not none.
        uint declaredMips = U(bytes, 4 + 24);
        uint wantMips = declaredMips == 0 ? 1u : declaredMips;
        int natural = Textures.TextureCodec.MipChainLength(width, height);
        if (wantMips > (uint)natural)
            throw new InvalidDataException(
                $"{what} declares {wantMips} mip levels for {width}x{height}, which admits only {natural}");
        int mips = (int)wantMips;

        uint dxgi = U(bytes, 4 + 124);
        if (dxgi != DdsWriter.R16G16B16A16_FLOAT)
            throw new InvalidDataException(
                $"{what} is DXGI format {dxgi}; this reader accepts only {DdsWriter.R16G16B16A16_FLOAT} "
                + "(R16G16B16A16_FLOAT)");
        uint dimension = U(bytes, 4 + 124 + 4), arraySize = U(bytes, 4 + 124 + 12);
        if (dimension != DIMENSION_TEXTURE2D)
            throw new InvalidDataException($"{what} is resource dimension {dimension}, not a 2D texture");
        if (arraySize != 1)
            throw new InvalidDataException($"{what} declares an array of {arraySize}; only a single image is read");

        var levels = new List<byte[]>(mips);
        int at = DdsWriter.HeaderBytes, lw = width, lh = height;
        for (int i = 0; i < mips; i++)
        {
            long want = DdsWriter.LevelBytes(dxgi, lw, lh);
            if (bytes.Length - at < want)
                throw new InvalidDataException(
                    $"{what} ends {want - (bytes.Length - at)} bytes into mip level {i} "
                    + $"({lw}x{lh} needs {want})");
            levels.Add(bytes.Slice(at, (int)want).ToArray());
            at += (int)want;
            lw = Math.Max(1, lw >> 1);
            lh = Math.Max(1, lh >> 1);
        }
        if (at != bytes.Length)
            throw new InvalidDataException(
                $"{what} carries {bytes.Length - at} bytes past its {mips}-level chain");
        return new DdsImage(dxgi, width, height, levels);
    }
}
