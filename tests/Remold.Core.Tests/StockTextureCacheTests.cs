using System.IO;
using System.Linq;
using Remold.Core;
using Remold.Core.Bundles;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The durable stock-PNG cache a Blender open links its <c>textures/</c> folder from. Decoding and
/// re-encoding a full-resolution game map is what an open costs, so the entries have to survive between
/// runs — and they are keyed by the bundle's CONTENT identity, so a game update misses exactly the bundles
/// it rewrote and nothing here can ever serve one game's pixels for another's.
/// </summary>
public class StockTextureCacheTests
{
    private const string Content = "0011223344556677";
    private const string Other = "8899aabbccddeeff";
    private const long Pid = 42;

    private static BundleReader.DecodedTexture Solid(byte b, int size = 4) =>
        new(SyntheticBundle.SolidRgba32(size, size, b, b, b, 255), size, size, "RGBA32");

    private static Rgba32 FirstPixel(string path)
    {
        using var img = Image.Load<Rgba32>(path);
        return img[0, 0];
    }

    [Fact]
    public void APublishedTextureIsServedBackForTheSameBundleContent()
    {
        using var g = new TempGame();
        var cache = new StockTextureCache(g.At("stocktex"));

        Assert.Null(cache.TryGet(Content, "body_d", Pid));            // cold: nothing to serve
        var written = cache.Publish(Solid(0x20), Content, "body_d", Pid);

        Assert.NotNull(written);
        Assert.Equal(written, cache.TryGet(Content, "body_d", Pid));  // warm: the very same file
        Assert.Equal(new Rgba32(0x20, 0x20, 0x20, 255), FirstPixel(written!));
    }

    /// <summary>A game update rewrites a bundle, so the manifest states new content for it — and the entry
    /// under the old identity is simply not asked for again. Nothing is invalidated, deleted or version-swept:
    /// a key that means different bytes IS a different key.</summary>
    [Fact]
    public void ADifferentBundleContent_MissesRatherThanServingTheOldPicture()
    {
        using var g = new TempGame();
        var cache = new StockTextureCache(g.At("stocktex"));
        cache.Publish(Solid(0x20), Content, "body_d", Pid);

        Assert.Null(cache.TryGet(Other, "body_d", Pid));              // same texture name, rewritten bundle
        Assert.NotNull(cache.TryGet(Content, "body_d", Pid));         // and the untouched bundle's entry still stands
    }

    /// <summary>Two textures of one bundle are two entries; the name is part of the key, not just the
    /// content identity.</summary>
    [Fact]
    public void TwoTexturesOfOneBundleAreDistinctEntries()
    {
        using var g = new TempGame();
        var cache = new StockTextureCache(g.At("stocktex"));
        cache.Publish(Solid(0x20), Content, "body_d", Pid);
        cache.Publish(Solid(0x40), Content, "body_n", Pid);

        Assert.Equal(new Rgba32(0x20, 0x20, 0x20, 255), FirstPixel(cache.TryGet(Content, "body_d", Pid)!));
        Assert.Equal(new Rgba32(0x40, 0x40, 0x40, 255), FirstPixel(cache.TryGet(Content, "body_n", Pid)!));
    }

    /// <summary>Two SAME-named textures of one bundle are two entries as well. The name is not a Texture2D's
    /// identity — its path id is — and a key without it would make one of them durable under the other's
    /// name, which is the wrong picture on every later open.</summary>
    [Fact]
    public void TwoSameNamedTexturesOfOneBundleAreDistinctEntries()
    {
        using var g = new TempGame();
        var cache = new StockTextureCache(g.At("stocktex"));
        cache.Publish(Solid(0x20), Content, "RampMap_Linear_RGBAHalf", 11);
        cache.Publish(Solid(0x40), Content, "RampMap_Linear_RGBAHalf", 12);

        Assert.Equal(new Rgba32(0x20, 0x20, 0x20, 255),
            FirstPixel(cache.TryGet(Content, "RampMap_Linear_RGBAHalf", 11)!));
        Assert.Equal(new Rgba32(0x40, 0x40, 0x40, 255),
            FirstPixel(cache.TryGet(Content, "RampMap_Linear_RGBAHalf", 12)!));
    }

    /// <summary>An entry dropped is gone, and a key nobody dropped is untouched. This is what the export does
    /// with an entry that passed every check this cache can afford and still turned out not to be a readable
    /// picture.</summary>
    [Fact]
    public void AnInvalidatedEntryIsGone_AndItsNeighboursAreNot()
    {
        using var g = new TempGame();
        var cache = new StockTextureCache(g.At("stocktex"));
        cache.Publish(Solid(0x20), Content, "body_d", Pid);
        cache.Publish(Solid(0x40), Content, "body_n", Pid);

        cache.Invalidate(Content, "body_d", Pid);

        Assert.Null(cache.TryGet(Content, "body_d", Pid));
        Assert.NotNull(cache.TryGet(Content, "body_n", Pid));
        cache.Invalidate(Content, "never_published", Pid);   // and dropping nothing is not an error
    }

    /// <summary>A file that is not a whole PNG is not this cache's answer — it reads as a miss, so the caller
    /// exports afresh and publishes over it. Serving half a picture as the game's own map is the one failure
    /// this cache may not have.</summary>
    [Fact]
    public void ATruncatedEntryReadsAsAMiss_AndAPublishReplacesIt()
    {
        using var g = new TempGame();
        var cache = new StockTextureCache(g.At("stocktex"));
        var path = cache.Publish(Solid(0x20), Content, "body_d", Pid)!;
        var whole = File.ReadAllBytes(path);
        File.WriteAllBytes(path, whole.Take(whole.Length / 2).ToArray());

        Assert.Null(cache.TryGet(Content, "body_d", Pid));

        cache.Publish(Solid(0x60), Content, "body_d", Pid);
        Assert.Equal(new Rgba32(0x60, 0x60, 0x60, 255), FirstPixel(cache.TryGet(Content, "body_d", Pid)!));
    }

    /// <summary>Garbage under a cache name is a miss too — the check is what the file IS, not that a file is
    /// there.</summary>
    [Fact]
    public void AnEntryThatIsNotAPngAtAllReadsAsAMiss()
    {
        using var g = new TempGame();
        var root = g.At("stocktex");
        var cache = new StockTextureCache(root);
        var path = cache.Publish(Solid(0x20), Content, "body_d", Pid)!;
        File.WriteAllText(path, "not a picture");

        Assert.Null(cache.TryGet(Content, "body_d", Pid));
    }

    /// <summary>Placing an entry gives the run folder its own name for the picture. Whether the filesystem
    /// took a hard link or a copy is not the contract — that the bytes are there under the asked-for name
    /// is.</summary>
    [Fact]
    public void PlacingAnEntryPutsThePictureAtTheAskedForPath()
    {
        using var g = new TempGame();
        var cache = new StockTextureCache(g.At("stocktex"));
        var cached = cache.Publish(Solid(0x33), Content, "body_d", Pid)!;
        var dest = g.At(Path.Combine("run", "textures", "body_d.aabb.vesna.png"));

        Assert.True(StockTextureCache.Place(cached, dest));
        Assert.Equal(new Rgba32(0x33, 0x33, 0x33, 255), FirstPixel(dest));
    }

    /// <summary>A place over an existing file replaces it. The run folder is rebuilt per open and a stale
    /// name there would embed last session's picture.</summary>
    [Fact]
    public void PlacingOverAnExistingFileReplacesIt()
    {
        using var g = new TempGame();
        var cache = new StockTextureCache(g.At("stocktex"));
        var dest = g.At(Path.Combine("run", "textures", "body_d.aabb.vesna.png"));
        StockTextureCache.Place(cache.Publish(Solid(0x11), Content, "body_d", Pid)!, dest);

        Assert.True(StockTextureCache.Place(cache.Publish(Solid(0x99), Other, "body_d", Pid)!, dest));
        Assert.Equal(new Rgba32(0x99, 0x99, 0x99, 255), FirstPixel(dest));
    }

    /// <summary>The cache is regenerable from the game, so a force rescan takes it — the same sweep that owns
    /// every other derived tree. A cache root the sweep does not name would outlive the rescan the modder
    /// asked for and keep answering with the pre-update pictures.</summary>
    [Fact]
    public void AForceRescanSweepsTheStockTextureTree()
    {
        using var g = new TempGame();
        var cacheRoot = g.At("cache");
        var cache = new StockTextureCache(LabPaths.StockTextureRootIn(cacheRoot));
        var entry = cache.Publish(Solid(0x20), Content, "body_d", Pid)!;
        Assert.True(File.Exists(entry));

        CacheReset.ClearDerivedCaches(cacheRoot);

        Assert.False(File.Exists(entry));
        Assert.False(Directory.Exists(LabPaths.StockTextureRootIn(cacheRoot)));
    }

    /// <summary>…and the tree the sweep clears is the one the app writes to, named once. The two are derived
    /// from the same folder name, so a rename cannot leave the sweep pointing at the old one.</summary>
    [Fact]
    public void TheStockTextureTreeIsOneOfTheSweptDerivedTrees()
    {
        Assert.Contains(LabPaths.DerivedCacheFolders,
            folder => string.Equals(Path.Combine("root", folder),
                LabPaths.StockTextureRootIn("root"), System.StringComparison.OrdinalIgnoreCase));
    }
}
