using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace Remold.Core.Textures;

/// <summary>
/// The pixel size of an image FILE from its header alone, no pixel decode — for the cases where the PNG
/// on disk is the only thing that knows its size (a donor-authored map, the UV guide).
/// </summary>
public static class PngInfo
{
    /// <summary>The image's (width, height), or null when the file is missing or unreadable — callers
    /// show "unavailable" rather than guess. Opened shared so an editor holding the file doesn't turn a
    /// readable header into a failure.</summary>
    public static (int Width, int Height)? TrySize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var info = Image.Identify(fs);
            return info is null ? null : (info.Width, info.Height);
        }
        catch { return null; }
    }

    /// <summary>The image's (width, height) but ONLY when the file's own header says PNG. The extension is
    /// not evidence — a JPEG renamed <c>.png</c> identifies fine as an image, so <see cref="TrySize"/>
    /// would accept it. Null when the file is missing, unreadable, or any other format. Intake paths that
    /// promise the modder a PNG (a card drop) gate on this; the readers of files this app itself wrote use
    /// <see cref="TrySize"/>.</summary>
    public static (int Width, int Height)? TryPngSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var info = Image.Identify(fs);
            return info?.Metadata.DecodedImageFormat is PngFormat ? (info.Width, info.Height) : null;
        }
        catch { return null; }
    }
}
