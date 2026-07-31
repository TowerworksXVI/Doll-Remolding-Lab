using System;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using ATTextureFormat = AssetsTools.NET.Texture.TextureFormat;

namespace Remold.Core.Textures;

/// <summary>
/// The pure texture codec (RGBA bytes ↔ encoded bytes) plus the format/size math the build needs.
///
/// <para><see cref="ToCompressionFormat"/> and <see cref="BlobSize"/>/<c>LevelSize</c> compute the byte size
/// a shipped blob must equal (format × dims × mips) independently of the encoder, so a test can pin the
/// encode output against it. <see cref="Encode"/>/<see cref="DecodeToRgba"/> compress at PACKAGE time to the
/// live texture's EXACT mip count, producing the bytes injected verbatim.</para>
///
/// Compression is pure-managed via <b>BCnEncoder.Net</b> (MIT). Reading the live target's
/// format/dimensions/mip count and writing the blob into the package is the packager's job.
/// </summary>
public static class TextureCodec
{
    /// <summary>Map a target Texture2D's Unity format onto a BCnEncoder format. Only the formats GFL2
    /// character textures use are mapped — the BCn family plus the uncompressed RGBA fallbacks. An unmapped
    /// format throws <see cref="NotSupportedException"/> so the pre-encode path skips that target with a
    /// build note rather than guessing.</summary>
    public static CompressionFormat ToCompressionFormat(ATTextureFormat fmt) => fmt switch
    {
        ATTextureFormat.DXT1 or ATTextureFormat.DXT1Crunched => CompressionFormat.Bc1,
        ATTextureFormat.DXT5 or ATTextureFormat.DXT5Crunched => CompressionFormat.Bc3,
        ATTextureFormat.BC4 => CompressionFormat.Bc4,
        ATTextureFormat.BC5 => CompressionFormat.Bc5,
        ATTextureFormat.BC7 => CompressionFormat.Bc7,
        ATTextureFormat.RGBA32 => CompressionFormat.Rgba,
        ATTextureFormat.BGRA32 or ATTextureFormat.BGRA32Old => CompressionFormat.Bgra,
        ATTextureFormat.RGB24 => CompressionFormat.Rgb,
        ATTextureFormat.Alpha8 or ATTextureFormat.R8 => CompressionFormat.R,
        _ => throw new NotSupportedException($"no managed encoder mapping for texture format {fmt}"),
    };

    /// <summary>True if <see cref="ToCompressionFormat"/> has a mapping for this format.</summary>
    public static bool IsSupported(ATTextureFormat fmt)
    {
        try { ToCompressionFormat(fmt); return true; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>Result of an encode: the flattened mip chain plus its level count and dimensions.</summary>
    public readonly record struct Encoded(byte[] Data, int MipCount, int Width, int Height);

    /// <summary>
    /// Encode bottom-up <paramref name="rgba"/> (8-bit RGBA, row-major, <c>width*height*4</c> bytes) into
    /// <paramref name="targetFormat"/>, producing EXACTLY <paramref name="mipCount"/> mip levels, largest
    /// first and concatenated in Unity's storage order. The caller supplies the target's live mip count so
    /// the blob matches what the bundle stores. Orientation is the caller's concern — Unity stores textures
    /// bottom-up, so the packager flips the top-down authored PNG first.
    ///
    /// <para>Compressed formats need 4×4-block-aligned dimensions; a non-aligned size throws. A
    /// <paramref name="mipCount"/> larger than the natural chain length is refused too: the live texture
    /// would be declaring more mips than its size admits, so the blob couldn't match it.</para>
    /// </summary>
    public static Encoded Encode(ReadOnlySpan<byte> rgba, int width, int height,
        ATTextureFormat targetFormat, int mipCount,
        CompressionQuality quality = CompressionQuality.Balanced, int? taskCount = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"invalid texture dimensions {width}x{height}");
        if (mipCount < 1)
            throw new ArgumentException($"mip count must be at least 1, got {mipCount}");
        long need = (long)width * height * 4;
        if (rgba.Length < need)
            throw new ArgumentException($"rgba buffer is {rgba.Length} bytes, need {need} for {width}x{height} RGBA");

        var cf = ToCompressionFormat(targetFormat);
        if (IsBlockCompressed(cf) && ((width & 3) != 0 || (height & 3) != 0))
            throw new ArgumentException(
                $"{targetFormat} is block-compressed and needs 4×4-aligned dimensions, got {width}x{height}");

        int natural = MipChainLength(width, height);
        if (mipCount > natural)
            throw new ArgumentException(
                $"target declares {mipCount} mips but {width}x{height} admits only {natural}. " +
                "The live texture's mip count doesn't match its dimensions (re-export to refresh)");

        var enc = new BcEncoder();
        enc.OutputOptions.Format = cf;
        enc.OutputOptions.Quality = quality;
        // BCn block encoding is deterministic regardless of worker count, so the cap changes ONLY how many
        // threads run, never the output bytes. Null ⇒ the encoder's default (all cores).
        if (taskCount is { } tc && tc >= 1) { enc.Options.IsParallel = true; enc.Options.TaskCount = tc; }
        enc.OutputOptions.GenerateMipMaps = mipCount > 1;
        // MaxMipMapLevel counts levels including the base (-1 = full chain): cap it to the count the target
        // declares, so a truncated-chain texture never gets a full chain to 1×1 encoded for it.
        enc.OutputOptions.MaxMipMapLevel = mipCount;

        byte[][] mips = enc.EncodeToRawBytes(rgba, width, height, PixelFormat.Rgba32);

        // defensive: honour mipCount exactly even if the encoder returns a different level count
        int levels = Math.Min(mipCount, mips.Length);
        int total = 0;
        for (int i = 0; i < levels; i++) total += mips[i].Length;
        var flat = new byte[total];
        int off = 0;
        for (int i = 0; i < levels; i++) { Buffer.BlockCopy(mips[i], 0, flat, off, mips[i].Length); off += mips[i].Length; }

        return new Encoded(flat, levels, width, height);
    }

    /// <summary>
    /// Encode bottom-up <paramref name="rgba"/> into <paramref name="targetFormat"/> as a FULL mip chain
    /// down to 1×1, returned largest-first and NOT flattened — a container writer needs each level's own
    /// byte count. Downsampling is per-channel with no alpha premultiply, so a map carrying non-colour
    /// channels (a packed normal, an RMO mask) downsamples correctly.
    ///
    /// <para>Any dimensions are accepted, unlike <see cref="Encode"/>: a standalone container stores whole
    /// 4×4 blocks and an edge block carries padding, whereas a Unity blob's byte size must equal
    /// format × dims × mips exactly.</para>
    ///
    /// <para>Capped at <see cref="MipChainLength"/> levels for the dimensions — the encoder can hand back
    /// more (a 1×1 input yields two), and a container declaring more mips than its size admits is rejected
    /// by the runtime that binds it.</para>
    /// </summary>
    public static byte[][] EncodeMipChain(ReadOnlySpan<byte> rgba, int width, int height,
        ATTextureFormat targetFormat, CompressionQuality quality = CompressionQuality.Balanced,
        int? taskCount = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"invalid texture dimensions {width}x{height}");
        long need = (long)width * height * 4;
        if (rgba.Length < need)
            throw new ArgumentException($"rgba buffer is {rgba.Length} bytes, need {need} for {width}x{height} RGBA");

        var enc = new BcEncoder();
        enc.OutputOptions.Format = ToCompressionFormat(targetFormat);
        enc.OutputOptions.Quality = quality;
        enc.OutputOptions.GenerateMipMaps = true;
        enc.OutputOptions.MaxMipMapLevel = -1;   // -1 = the full chain, terminating at 1×1
        // deterministic regardless of worker count: this changes thread count, never the output bytes
        enc.Options.IsParallel = true;
        if (taskCount is { } tc && tc >= 1) enc.Options.TaskCount = tc;
        byte[][] mips = enc.EncodeToRawBytes(rgba, width, height, PixelFormat.Rgba32);
        int natural = MipChainLength(width, height);
        return mips.Length <= natural ? mips : mips[..natural];
    }

    /// <summary>The byte size a <paramref name="mipCount"/>-level chain of <paramref name="format"/> occupies
    /// at <paramref name="width"/>×<paramref name="height"/> — what a stored blob must equal. Computed
    /// independently of the encoder so a test can pin the encode output against it.</summary>
    public static long BlobSize(ATTextureFormat format, int width, int height, int mipCount)
    {
        var cf = ToCompressionFormat(format);
        long total = 0;
        int w = width, h = height;
        for (int i = 0; i < mipCount; i++)
        {
            total += LevelSize(cf, w, h);
            w = Math.Max(1, w >> 1);
            h = Math.Max(1, h >> 1);
        }
        return total;
    }

    /// <summary>Natural mip-chain length for a size: levels down to 1×1 (max of the two dimension chains),
    /// matching Unity's mip generation. A 2048×2048 texture has 12 levels (2048→1).</summary>
    public static int MipChainLength(int width, int height)
    {
        int max = Math.Max(width, height);
        int levels = 1;
        while (max > 1) { max >>= 1; levels++; }
        return levels;
    }

    /// <summary>Decode the base mip of <paramref name="data"/> back to bottom-up 8-bit RGBA — the inverse
    /// the round-trip tests verify encode fidelity with. Trailing mip levels are ignored.</summary>
    public static byte[] DecodeToRgba(byte[] data, int width, int height, ATTextureFormat format)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException($"invalid texture dimensions {width}x{height}");
        var cf = ToCompressionFormat(format);
        long need = LevelSize(cf, width, height);
        if (data.Length < need)
            throw new FormatException(
                $"texture data is {data.Length} bytes but {format} at {width}x{height} needs {need} " +
                "(truncated or corrupt)");
        ColorRgba32[] pixels = new BcDecoder().DecodeRaw(data, width, height, cf);
        var outp = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            outp[i * 4 + 0] = pixels[i].r;
            outp[i * 4 + 1] = pixels[i].g;
            outp[i * 4 + 2] = pixels[i].b;
            outp[i * 4 + 3] = pixels[i].a;
        }
        return outp;
    }

    private static bool IsBlockCompressed(CompressionFormat cf) => cf switch
    {
        CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha or CompressionFormat.Bc2 or
        CompressionFormat.Bc3 or CompressionFormat.Bc4 or CompressionFormat.Bc5 or
        CompressionFormat.Bc6U or CompressionFormat.Bc6S or CompressionFormat.Bc7 => true,
        _ => false,
    };

    /// <summary>Bytes one mip level occupies: 4×4 blocks for BCn (8 or 16 B each), or
    /// width*height*bytesPerPixel for the uncompressed families this codec maps.</summary>
    private static long LevelSize(CompressionFormat cf, int width, int height)
    {
        if (IsBlockCompressed(cf))
        {
            long blocks = ((long)(width + 3) / 4) * ((height + 3) / 4);
            int perBlock = cf is CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha or CompressionFormat.Bc4 ? 8 : 16;
            return blocks * perBlock;
        }
        int bpp = cf switch
        {
            CompressionFormat.Rgba or CompressionFormat.Bgra => 4,
            CompressionFormat.Rgb => 3,
            CompressionFormat.Rg => 2,
            CompressionFormat.R => 1,
            _ => 4,
        };
        return (long)width * height * bpp;
    }
}
