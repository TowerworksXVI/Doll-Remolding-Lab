using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Remold.Core.Migoto;

namespace Remold.App.Textures;

/// <summary>
/// Turns a toon ramp's stored fp16 values into something a card can show.
///
/// <para>A ramp is not a picture: it is four bands of shading values the character shader samples by
/// lighting term, stored as RGBAHalf. The map-card thumbnail path decodes 8-bit PNGs and cannot read one at
/// all, so this is a route of its own — and everything it produces is for the SCREEN. The file's own bytes
/// never travel through here; a pick copies or writes them raw.</para>
///
/// <para>Both refusals a pick can hit are worded here, so the Import gate and the build's own gate turn away
/// the same files: the format (anything but fp16) and the extent (a ramp of another size draws a different
/// response rather than the same one at a lower resolution).</para>
/// </summary>
public static class RampImage
{
    /// <summary>Rows the preview grows each stored row into, so the four bands read as bands rather than as
    /// a 16-pixel strip. Nearest-neighbour: an interpolated edge would draw a gradient between two bands
    /// that the data does not have.</summary>
    private const int RowScale = 4;

    /// <summary>One ramp as the screen and the pick surface need it: its extent, and the largest stored
    /// level's bytes.</summary>
    public readonly record struct Read(int Width, int Height, byte[] Fp16);

    /// <summary>Read a <c>.dds</c> ramp off disk. Throws <see cref="InvalidDataException"/> naming what did
    /// not hold — the same strictness the build reads a named ramp with.
    ///
    /// <para>Opened SHARED, deliberately. A card's tile is re-read after every pick and the destination a
    /// pick lands at is deterministic per slot, so the next pick overwrites the very file this read holds —
    /// an exclusive read would turn that second pick into a sharing refusal.</para></summary>
    public static Read ReadDds(string path)
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var ms = new MemoryStream())
        {
            fs.CopyTo(ms);
            bytes = ms.ToArray();
        }
        var image = DdsReader.Parse(bytes, Path.GetFileName(path));
        return new Read(image.Width, image.Height, image.Levels[0]);
    }

    /// <summary>Why <paramref name="path"/> cannot be a toon ramp, or null when it can. States the extent or
    /// the format the file actually has, so a modder who picked the wrong export can see which it was.</summary>
    /// <summary>The one shape a toon ramp file has, in the words the Import tip states it in. Every refusal
    /// that isn't about the EXTENT says this rather than the reader's own diagnosis: a DXGI number and a
    /// sentence about "this reader" tell a modder nothing about the export they should have made.</summary>
    public const string Requirement = "A toon ramp is a 256×16 RGBAHalf .dds.";

    public static string? RefuseAsRamp(string path)
    {
        DdsImage image;
        try { image = DdsReader.Read(path); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(path)} isn't a toon ramp. {Requirement}";
        }
        if (image.Width != RampConversion.RampWidth || image.Height != RampConversion.RampHeight)
            return $"{Path.GetFileName(path)} is {image.Width}×{image.Height}. "
                + $"A toon ramp is {RampConversion.RampWidth}×{RampConversion.RampHeight}.";
        return null;
    }

    /// <summary>The preview bitmap for one ramp's stored fp16 bytes, or null when there is no tile to make
    /// of them. Safe off the UI thread, like every other thumbnail producer.
    ///
    /// <para>Tries, as the name says: bytes short of the extent they claim, and a machine that cannot make a
    /// bitmap at all, both answer null. Whatever asked for it is showing a LIST — one tile that couldn't be
    /// built must not cost the rows around it.</para></summary>
    public static Bitmap? TryPreview(int width, int height, byte[] fp16)
    {
        try { return Preview(width, height, fp16); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private static Bitmap? Preview(int width, int height, byte[] fp16)
    {
        if (width <= 0 || height <= 0) return null;
        long need = (long)width * height * 8;   // four half-floats per texel
        if (fp16.Length < need) return null;

        int outHeight = height * RowScale;
        var bmp = new WriteableBitmap(new PixelSize(width, outHeight), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        var row = new byte[width * 4];
        using var fb = bmp.Lock();
        for (int y = 0; y < height; y++)
        {
            int at = y * width * 8;
            for (int x = 0; x < width; x++)
            {
                int t = at + x * 8;
                // BGRA on the wire; alpha is forced opaque because a ramp's own alpha is shading data, not
                // coverage, and sampling it as transparency would draw the tile through the panel behind it
                row[x * 4 + 0] = Channel(fp16, t + 4);
                row[x * 4 + 1] = Channel(fp16, t + 2);
                row[x * 4 + 2] = Channel(fp16, t + 0);
                row[x * 4 + 3] = byte.MaxValue;
            }
            for (int r = 0; r < RowScale; r++)
                Marshal.Copy(row, 0, fb.Address + (y * RowScale + r) * fb.RowBytes, row.Length);
        }
        return bmp;
    }

    /// <summary>One stored half-float as a display byte: clamped into the range a screen has, then sRGB
    /// encoded, because the stored values are linear shading terms. Anything above 1 is a value no display
    /// can show, so it lands at white rather than wrapping.</summary>
    private static byte Channel(byte[] fp16, int offset)
    {
        float v = (float)BitConverter.ToHalf(fp16, offset);
        if (float.IsNaN(v) || v <= 0f) return 0;
        if (v >= 1f) return byte.MaxValue;
        double s = v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
        return (byte)Math.Clamp(Math.Round(s * 255), 0, 255);
    }
}
