using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Remold.Core.Bundles;

namespace Remold.Core.Textures;

/// <summary>
/// Encodes a decoded Texture2D to a PNG for editing (the decode itself is
/// <see cref="BundleReader.GetTexture"/>). AssetsTools returns Unity's bottom-up rows, so the write
/// flips vertically to top-down PNG orientation.
/// </summary>
public static class TextureExport
{
    /// <summary>
    /// Shared PNG encoder for every export-time PNG. These bytes never reach the game (the package
    /// re-derives the shipped texture from the edit), so size is traded for ENCODE SPEED: on
    /// ImageSharp's default (per-scanline filter search + level-6 deflate) PNG encoding was ~84% of a
    /// full-outfit Add. Fixed Paeth skips the search; BestSpeed deflate is fast.
    /// </summary>
    public static readonly PngEncoder FastPng = new()
    {
        CompressionLevel = PngCompressionLevel.BestSpeed,
        FilterMethod = PngFilterMethod.Paeth,
    };

    /// <summary>The filesystem name a workspace texture PNG lands under:
    /// <c>&lt;name&gt;.&lt;bundle&gt;.&lt;subject&gt;.png</c>. Two same-named textures from DIFFERENT
    /// bundles are distinct game assets, and one texture materialized under two SUBJECTS is two files —
    /// an edit to a file is an edit to that outfit alone, which is what lets the build scope it. The
    /// SINGLE naming rule every texture producer shares. The texture name is carried on the target,
    /// never re-parsed from this filename.</summary>
    public static string BundleScopedName(string bundleId, string textureName, string subject) =>
        $"{textureName}.{SanitizeSegment(bundleId)}.{SanitizeSegment(subject)}.png";

    private static string SanitizeSegment(string s)
    {
        if (string.IsNullOrEmpty(s)) return "_";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }

    /// <summary>What a live bundle texture is to a caller that only wants to describe it: its size, and
    /// whether an edit could be encoded back into its own format. An HDR/float map is not
    /// <paramref name="Authorable"/> — decoding it would flatten it to a lossy 8-bit image.</summary>
    public readonly record struct TextureProbe(int Width, int Height, bool Authorable);

    /// <summary>Read a Texture2D's size and authorability without decoding its pixels; null when the
    /// bundle holds no texture of that name. The one route to that answer, so the Unity format enum stays
    /// inside Core and callers ask the question rather than the format.</summary>
    public static TextureProbe? Probe(byte[] deobfuscatedBundle, string textureName) =>
        new BundleReader().GetTextureMeta(deobfuscatedBundle, textureName) is { } m
            ? new TextureProbe(m.Width, m.Height,
                TextureCodec.IsSupported((AssetsTools.NET.Texture.TextureFormat)m.Format))
            : null;

    /// <summary>Write a decoded texture to a PNG. Returns false if the texture wasn't found.</summary>
    public static bool ExportPng(BundleReader reader, byte[] deobfuscatedBundle, string textureName, string outPath)
    {
        var tex = reader.GetTexture(deobfuscatedBundle, textureName);
        if (tex is null) return false;
        WritePng(tex.Value, outPath);
        return true;
    }

    public static void WritePng(BundleReader.DecodedTexture tex, string outPath)
    {
        using var image = Image.LoadPixelData<Bgra32>(tex.Bgra, tex.Width, tex.Height);
        image.Mutate(x => x.Flip(FlipMode.Vertical));   // Unity bottom-up → top-down PNG
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        image.SaveAsPng(outPath, FastPng);
    }

    /// <summary>The same PNG with every pixel opaque, colours untouched, optionally fitted within
    /// <paramref name="maxDim"/> on its longer side (0 = full size). For a map whose alpha channel is DATA
    /// rather than coverage — the RMO's emissive mask — where a viewer sampling it as transparency renders
    /// the image ghostly. A display transform over a copy: the file this was read from is never written, so
    /// nothing the build ships changes.
    ///
    /// <para>The composite runs BEFORE the resize, never after: resampling is done on premultiplied alpha, so
    /// a downscale taken first washes the colour out of every texel the mask left transparent, and clearing
    /// alpha afterwards would only make that loss opaque. Doing it in this order also means a caller that
    /// wants a thumbnail encodes a thumbnail-sized image rather than a full-resolution one.</para></summary>
    public static byte[] OpaquePng(byte[] png, int maxDim = 0)
    {
        using var image = Image.Load<Rgba32>(png);
        image.ProcessPixelRows(rows =>
        {
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++) row[x].A = 255;
            }
        });
        // never upscaled: an image already within the bound is left at its own size
        if (maxDim > 0 && (image.Width > maxDim || image.Height > maxDim))
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(maxDim, maxDim),
                Mode = ResizeMode.Max,
            }));
        using var ms = new MemoryStream();
        image.SaveAsPng(ms, FastPng);
        return ms.ToArray();
    }
}
