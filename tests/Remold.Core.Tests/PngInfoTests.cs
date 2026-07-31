using System.IO;
using Remold.Core.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The header-only image size read behind a map card's dimensions: report the real pixel size, survive the
/// file being held open by an image editor, and return NULL rather than guess when the file is missing or
/// isn't an image — the card shows "unavailable" off that null.
/// </summary>
public class PngInfoTests
{
    private static string WritePng(string dir, string name, int w, int h)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        using var img = new Image<Rgba32>(w, h);
        img.SaveAsPng(path);
        return path;
    }

    [Fact]
    public void TrySize_ReadsRealDimensions_NonSquare()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remold-pnginfo-" + Path.GetRandomFileName());
        try
        {
            // non-square: a width/height swap in the header read would pass a square fixture
            var png = WritePng(dir, "authored.png", 96, 40);
            Assert.Equal((96, 40), PngInfo.TrySize(png));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TrySize_ReadsFileHeldOpenByAnotherWriter()
    {
        // the modder's image editor keeps the PNG open while they paint; the dimensions read must not fail
        var dir = Path.Combine(Path.GetTempPath(), "remold-pnginfo-" + Path.GetRandomFileName());
        try
        {
            var png = WritePng(dir, "held.png", 64, 32);
            using var holder = new FileStream(png, FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            Assert.Equal((64, 32), PngInfo.TrySize(png));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TrySize_MissingFile_IsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), "remold-pnginfo-" + Path.GetRandomFileName(), "nope.png");
        Assert.Null(PngInfo.TrySize(missing));
    }

    /// <summary>Why the drop check needed its OWN reader: <see cref="PngInfo.TrySize"/> identifies any format
    /// ImageSharp knows, so a JPEG under a .png name reads back a perfectly good size. Only
    /// <see cref="PngInfo.TryPngSize"/> looks at what the header actually says.</summary>
    [Fact]
    public void TryPngSize_RefusesAnotherFormatUnderAPngName_WhereTrySizeAcceptsIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remold-pnginfo-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var disguised = Path.Combine(dir, "jpeg-in-disguise.png");
            using (var img = new Image<Rgba32>(48, 24))
                img.SaveAsJpeg(disguised);

            Assert.Equal((48, 24), PngInfo.TrySize(disguised));   // an image, yes — but not a PNG
            Assert.Null(PngInfo.TryPngSize(disguised));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TryPngSize_ReadsARealPng()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remold-pnginfo-" + Path.GetRandomFileName());
        try { Assert.Equal((96, 40), PngInfo.TryPngSize(WritePng(dir, "authored.png", 96, 40))); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TrySize_NotAnImage_IsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "remold-pnginfo-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(dir);
            var junk = Path.Combine(dir, "notreally.png");
            File.WriteAllText(junk, "this is not a png");
            Assert.Null(PngInfo.TrySize(junk));   // refuses rather than reporting a made-up size
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
