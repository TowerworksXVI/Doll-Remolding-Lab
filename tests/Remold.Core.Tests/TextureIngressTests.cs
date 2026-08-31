using System;
using System.IO;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>External picture bytes do not become project state until a complete RGBA decode and staged
/// canonical encode have succeeded.</summary>
public sealed class TextureIngressTests
{
    [Fact]
    public void An_invalid_drop_leaves_the_canonical_png_untouched()
    {
        using var g = new TempGame();
        var canonical = Png(Path.Combine(g.Root, "textures", "body.png"), new Rgba32(1, 2, 3, 4));
        var before = File.ReadAllBytes(canonical);
        var incoming = g.At("broken.png");
        File.WriteAllText(incoming, "not a png");

        Assert.ThrowsAny<Exception>(() => TextureIngress.Publish(incoming, canonical));

        Assert.Equal(before, File.ReadAllBytes(canonical));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(canonical)!, "~texture.*"));
    }

    [Fact]
    public void A_valid_drop_is_decoded_staged_and_published_as_rgba()
    {
        using var g = new TempGame();
        var canonical = Png(Path.Combine(g.Root, "textures", "body.png"), new Rgba32(1, 2, 3, 4));
        var incoming = Png(g.At("paint.png"), new Rgba32(90, 80, 70, 60));
        string? publishing = null;

        Assert.True(TextureIngress.Publish(incoming, canonical, beforePublish: p => publishing = p));

        Assert.Equal(Path.GetFullPath(canonical), publishing);
        using var image = Image.Load<Rgba32>(canonical);
        Assert.Equal(new Rgba32(90, 80, 70, 60), image[0, 0]);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(canonical)!, "~texture.*"));
    }

    [Fact]
    public void Project_copy_excludes_external_editor_and_ingress_sessions()
    {
        using var g = new TempGame();
        var root = g.At("source");
        Directory.CreateDirectory(root);
        var project = new ModProject { RootDir = root };
        project.Info.Name = "copy";
        project.Save();
        var canonical = Png(Path.Combine(root, "textures", "body.png"), new Rgba32(1, 1, 1, 1));
        var editor = Png(Path.Combine(root, ".editor", "picture-body", "session", "return.png"),
            new Rgba32(1, 1, 1, 1));
        var ingress = Path.Combine(root, ProjectAssetIngress.DirectoryName, "picture-body", "session", "return.png");
        Png(ingress, new Rgba32(2, 2, 2, 2));
        File.WriteAllText(Path.Combine(root, "textures", "~asset.interrupted.body.png"), "partial");

        var copy = project.CopyTo(g.At("copy"));

        Assert.True(File.Exists(copy.Resolve("textures/body.png")));
        Assert.False(File.Exists(Path.Combine(copy.RootDir!, Path.GetRelativePath(root, editor))));
        Assert.DoesNotContain(Directory.GetFiles(copy.RootDir!, "*", SearchOption.AllDirectories),
            p => p.Contains(".editor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Directory.GetFiles(copy.RootDir!, "*", SearchOption.AllDirectories),
            p => p.Contains(ProjectAssetIngress.DirectoryName, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Directory.GetFiles(copy.RootDir!, "*", SearchOption.AllDirectories),
            p => Path.GetFileName(p).StartsWith("~asset.", StringComparison.OrdinalIgnoreCase));
    }

    private static string Png(string path, Rgba32 pixel)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(2, 2, pixel);
        image.SaveAsPng(path);
        return path;
    }
}
