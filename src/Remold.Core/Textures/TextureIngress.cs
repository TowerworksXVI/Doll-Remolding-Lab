using System;
using System.IO;
using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Remold.Core.Textures;

/// <summary>Validates and canonically encodes external picture bytes. Addressing, stale-write checks and
/// publication ownership belong to <c>ProjectAssetIngress</c> and <c>AuthoredEditSession</c>.</summary>
public static class TextureIngress
{
    /// <summary>Decode an external PNG as RGBA8, encode it with the project's canonical encoder into
    /// staging, and publish it with one final same-directory move. Returns false only when
    /// <paramref name="skipIfPixelsEqual"/> was requested and the decoded pixels already match. A decode or
    /// staging failure leaves <paramref name="destinationPng"/> untouched.</summary>
    public static bool Publish(string sourcePng, string destinationPng, bool skipIfPixelsEqual = false,
        Action<string>? beforePublish = null)
    {
        using var image = Image.Load<Rgba32>(sourcePng);
        return Publish(image, destinationPng, skipIfPixelsEqual, beforePublish);
    }

    /// <summary>The byte-array form used after an external format has yielded a PNG.</summary>
    public static bool Publish(byte[] sourcePng, string destinationPng, bool skipIfPixelsEqual = false,
        Action<string>? beforePublish = null)
    {
        using var image = Image.Load<Rgba32>(sourcePng);
        return Publish(image, destinationPng, skipIfPixelsEqual, beforePublish);
    }

    private static bool Publish(Image<Rgba32> image, string destinationPng, bool skipIfPixelsEqual,
        Action<string>? beforePublish)
    {
        var destination = Path.GetFullPath(destinationPng);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var staged = Path.Combine(directory, $"~texture.{Guid.NewGuid():N}.{Path.GetFileName(destination)}");
        try
        {
            image.SaveAsPng(staged, TextureExport.FastPng);
            if (skipIfPixelsEqual && File.Exists(destination) && SamePixels(staged, destination)) return false;
            beforePublish?.Invoke(destination);
            File.Move(staged, destination, overwrite: true);
            return true;
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
        }
    }

    public static bool SamePixels(string leftPath, string rightPath)
    {
        using var left = Image.Load<Rgba32>(leftPath);
        using var right = Image.Load<Rgba32>(rightPath);
        if (left.Width != right.Width || left.Height != right.Height) return false;
        bool same = true;
        left.ProcessPixelRows(right, (leftRows, rightRows) =>
        {
            for (int y = 0; y < leftRows.Height && same; y++)
                if (!leftRows.GetRowSpan(y).SequenceEqual(rightRows.GetRowSpan(y))) same = false;
        });
        return same;
    }

    /// <summary>Decoded pixel identity, independent of PNG metadata and encoder choices.</summary>
    public static string PixelIdentity(string png)
    {
        using var image = Image.Load<Rgba32>(png);
        var pixels = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(pixels);
        return $"{image.Width}x{image.Height}:{Convert.ToHexString(SHA256.HashData(pixels))}";
    }
}
