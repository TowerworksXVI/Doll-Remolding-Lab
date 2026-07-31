using System.IO;
using System.Linq;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="ImageEditorLocator"/> finds an installed image editor. The pure
/// <see cref="ImageEditorLocator.InstallDirCandidates"/> is exercised against a synthetic Program Files
/// tree, so no real install is touched.
/// </summary>
public class ImageEditorLocatorTests
{
    private static string Touch(string root, params string[] parts)
    {
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void NoEditorsInstalled_NoCandidates()
    {
        using var g = new TempGame();
        Assert.Empty(ImageEditorLocator.InstallDirCandidates(g.Root));
    }

    [Fact]
    public void FindsPaintDotNet()
    {
        using var g = new TempGame();
        var exe = Touch(g.Root, "paint.net", "paintdotnet.exe");
        Assert.Contains(exe, ImageEditorLocator.InstallDirCandidates(g.Root));
    }

    [Fact]
    public void FindsGimp_ByGlob_AndExcludesConsoleExe()
    {
        using var g = new TempGame();
        var gimp = Touch(g.Root, "GIMP 2", "bin", "gimp-2.10.exe");
        Touch(g.Root, "GIMP 2", "bin", "gimp-console-2.10.exe");
        var hits = ImageEditorLocator.InstallDirCandidates(g.Root);
        Assert.Contains(gimp, hits);
        Assert.DoesNotContain(hits, h => h.EndsWith("console-2.10.exe"));
    }

    [Fact]
    public void FindsPhotoshop_ByGlob_NewestVersionFirst()
    {
        using var g = new TempGame();
        var ps2023 = Touch(g.Root, "Adobe", "Adobe Photoshop 2023", "Photoshop.exe");
        var ps2024 = Touch(g.Root, "Adobe", "Adobe Photoshop 2024", "Photoshop.exe");
        var hits = ImageEditorLocator.InstallDirCandidates(g.Root);
        // both found; the newer one precedes the older
        Assert.True(hits.ToList().IndexOf(ps2024) < hits.ToList().IndexOf(ps2023));
    }

    [Fact]
    public void Find_PrefersTheOverrideWhenItExists()
    {
        using var g = new TempGame();
        var custom = Touch(g.Root, "custom", "myeditor.exe");
        Assert.Equal(custom, ImageEditorLocator.Find(custom));
    }

    [Theory]
    [InlineData(@"C:\Program Files\GIMP 2\bin\gimp-2.10.exe", "GIMP")]
    [InlineData(@"C:\Program Files\paint.net\paintdotnet.exe", "Paint.NET")]
    [InlineData(@"C:\Program Files\Krita (x64)\bin\krita.exe", "Krita")]
    [InlineData(@"C:\Program Files\Adobe\Adobe Photoshop 2024\Photoshop.exe", "Photoshop")]
    [InlineData(@"D:\tools\myeditor.exe", "myeditor")]   // custom pick → the exe name
    public void FriendlyName_MapsKnownEditors_FallsBackToFileName(string path, string expected)
    {
        Assert.Equal(expected, ImageEditorLocator.FriendlyName(path));
    }
}
