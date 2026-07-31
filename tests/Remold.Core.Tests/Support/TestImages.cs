using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Remold.Core.Tests.Support;

/// <summary>Real image files for the intake paths that read a file's HEADER rather than its name — a drop
/// check can't be driven with a path that doesn't exist, and "a JPEG called .png" only means anything if the
/// bytes really are a JPEG.</summary>
internal static class TestImages
{
    /// <summary>Write a solid-colour PNG and return its path.</summary>
    public static string WritePng(string path, int width = 8, int height = 8,
        byte r = 255, byte g = 0, byte b = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(width, height, new Rgba32(r, g, b, 255));
        image.SaveAsPng(path, new PngEncoder());
        return path;
    }

    /// <summary>Write JPEG bytes under whatever name the caller gives — including a <c>.png</c> one.</summary>
    public static string WriteJpegNamed(string path, int width = 8, int height = 8)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 255, 255));
        using var fs = File.Create(path);
        image.SaveAsJpeg(fs, new JpegEncoder());
        return path;
    }
}
