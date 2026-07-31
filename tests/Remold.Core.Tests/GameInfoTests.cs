using System.IO;
using Remold.Core;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Game-path resolution: locate <c>AssetBundles_Windows</c> and read the catalog version from
/// <c>catalog_main_&lt;N&gt;.bin</c> — the cache-invalidation key, which bumps with the bundle channel
/// and not with text-only patches.
/// </summary>
public class GameInfoTests
{
    [Fact]
    public void BundleDir_DerivesTheBundleDirFromTheGameRoot()
    {
        using var g = new TempGame();
        var root = g.WriteGameDir(catalogVersion: null);
        Assert.Equal(GameLocator.BundleDirOf(root), GameInfo.BundleDir(root));
    }

    [Fact]
    public void BundleDir_ThrowsWhenTheBundleDirIsAbsent()
    {
        using var g = new TempGame();
        Assert.Throws<DirectoryNotFoundException>(() => GameInfo.BundleDir(g.Root));   // no install layout under it
    }

    [Fact]
    public void CatalogVersion_ParsesTheTrailingNumber()
    {
        using var g = new TempGame();
        var root = g.WriteGameDir(catalogVersion: "24535");
        Assert.Equal("24535", GameInfo.CatalogVersion(root));
    }

    [Fact]
    public void CatalogVersion_FallsBackToUnknownWhenNoCatalog()
    {
        using var g = new TempGame();
        var root = g.WriteGameDir(catalogVersion: null);
        Assert.Equal("unknown", GameInfo.CatalogVersion(root));
    }

    [Fact]
    public void CatalogVersion_PicksTheHighestNumericWhenSeveralCatalogsExist()
    {
        // An updater can leave the old catalog beside the new one, so the key is the NEWEST by NUMERIC
        // compare — "9001" > "24535" as strings, but not as versions.
        using var g = new TempGame();
        var root = g.WriteGameDir(catalogVersion: "24535");
        File.WriteAllBytes(Path.Combine(GameLocator.BundleDirOf(root), "catalog_main_9001.bin"), new byte[] { 1 });
        Assert.Equal("24535", GameInfo.CatalogVersion(root));
    }
}
