using System;
using System.IO;
using System.Linq;
using Remold.Core;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Game auto-detect, the filesystem-only pieces: the bundle-dir accept-test,
/// <c>libraryfolders.vdf</c> parsing, and the Steam-root → <c>common</c> expansion. The registry read in
/// <c>Find</c> is thin glue exercised live, not here.
/// </summary>
public class GameLocatorTests
{
    /// <summary>The game's VFS manifest filename — the same sentinel constant GameLocator anchors on.</summary>
    private const string VfsManifestFile = "08dfe7d89b6fe56375d6dfec87ffcc8a.bundle";

    /// <summary>An <c>AssetBundles_Windows</c> dir carrying both GF2 sentinels: a current catalog and the
    /// VFS manifest under its fixed name, opening with the GFF magic.</summary>
    private static string MakeBundleDir(string parent)
    {
        var abw = Path.Combine(parent, "AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        File.WriteAllText(Path.Combine(abw, "catalog_main_24535.bin"), "x");
        File.WriteAllBytes(Path.Combine(abw, VfsManifestFile),
            new byte[] { (byte)'G', (byte)'F', (byte)'F', 0, 1, 2, 3, 4 });
        return abw;
    }

    [Fact]
    public void Validate_AcceptsTheBundleDirItself_AndResolvesToTheRoot()
    {
        using var g = new TempGame();
        var data = Path.Combine(g.Root, "GF2_Exilium_Data", "LocalCache", "Data");
        var abw = MakeBundleDir(data);
        Assert.Equal(Path.GetFullPath(g.Root), GameLocator.Validate(abw));   // point at the bundle dir → the root
    }

    [Fact]
    public void Validate_RejectsACatalogOnlyDir_TheManifestSentinelIsRequired()
    {
        using var g = new TempGame();
        var abw = Path.Combine(g.Root, "AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        File.WriteAllText(Path.Combine(abw, "catalog_main_24535.bin"), "x");   // catalog but no VFS manifest
        Assert.Null(GameLocator.Validate(abw));
        Assert.Contains("file manifest", GameLocator.ValidateDetailed(abw).Problem);
    }

    [Fact]
    public void Validate_RejectsAPopulatedLookAlike_TheCatalogSentinelIsRequired()
    {
        using var g = new TempGame();
        var abw = Path.Combine(g.Root, "AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        File.WriteAllText(Path.Combine(abw, "deadbeef.bundle"), "x");   // another game's populated cache
        Assert.Null(GameLocator.Validate(abw));
        Assert.Contains("catalog", GameLocator.ValidateDetailed(abw).Problem);
    }

    [Fact]
    public void Validate_DoesNotOpenTheManifest_ExistenceIsEnough()
    {
        // The manifest's PRESENCE under its fixed name anchors the install; its contents are never read,
        // since a running game denies readers. Real corruption surfaces loudly at load instead.
        using var g = new TempGame();
        var data = Path.Combine(g.Root, "GF2_Exilium_Data", "LocalCache", "Data");
        var abw = MakeBundleDir(data);
        File.WriteAllText(Path.Combine(abw, VfsManifestFile), "not a manifest");
        Assert.Equal(Path.GetFullPath(g.Root), GameLocator.Validate(g.Root));
    }

    [Fact]
    public void Validate_AcceptsAndFlagsInUse_WhenTheManifestIsLockedAgainstReaders()
    {
        // Hold the manifest open denying readers, as the running game does. Validation is existence-only so
        // the folder still resolves, and GameFilesInUse reports the lock so the front-end can say why.
        using var g = new TempGame();
        var data = Path.Combine(g.Root, "GF2_Exilium_Data", "LocalCache", "Data");
        var abw = MakeBundleDir(data);
        Assert.False(GameLocator.GameFilesInUse(g.Root));   // nothing holding it
        using (File.Open(Path.Combine(abw, VfsManifestFile), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(Path.GetFullPath(g.Root), GameLocator.Validate(g.Root));
            Assert.True(GameLocator.GameFilesInUse(g.Root));
        }
        Assert.False(GameLocator.GameFilesInUse(g.Root));   // released
    }

    [Fact]
    public void Validate_ResolvesFromAGameRoot()
    {
        using var g = new TempGame();
        var data = Path.Combine(g.Root, "GF2_Exilium_Data", "LocalCache", "Data");
        MakeBundleDir(data);
        Assert.Equal(Path.GetFullPath(g.Root), GameLocator.Validate(g.Root));
    }

    [Fact]
    public void Validate_ResolvesFromASteamCommonDir()
    {
        using var g = new TempGame();   // g.Root stands in for steamapps/common
        var gameRoot = Path.Combine(g.Root, "GIRLS' FRONTLINE 2 EXILIUM");
        var data = Path.Combine(gameRoot, "GF2_Exilium_Data", "LocalCache", "Data");
        MakeBundleDir(data);
        Assert.Equal(Path.GetFullPath(gameRoot), GameLocator.Validate(g.Root));
    }

    [Fact]
    public void Validate_RejectsAnEmptyBundleDir()
    {
        using var g = new TempGame();
        var abw = Path.Combine(g.Root, "AssetBundles_Windows");
        Directory.CreateDirectory(abw);   // exists but holds no bundle/catalog files
        Assert.Null(GameLocator.Validate(abw));
    }

    [Fact]
    public void Validate_RejectsNullEmptyOrMissing_WithAReason()
    {
        Assert.Null(GameLocator.Validate(null));
        Assert.Null(GameLocator.Validate("   "));
        Assert.Null(GameLocator.Validate(@"Z:\nope\does\not\exist"));
        Assert.NotNull(GameLocator.ValidateDetailed(@"Z:\nope\does\not\exist").Problem);
    }

    [Fact]
    public void ParseLibraryPaths_ExtractsAndUnescapes()
    {
        // a trimmed libraryfolders.vdf; Steam doubles the backslashes, which we un-escape
        var vdf = "\"libraryfolders\"\n{\n" +
                  "  \"0\" { \"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\" }\n" +
                  "  \"1\" { \"path\"\t\t\"D:\\\\SteamLibrary\" }\n}";
        Assert.Equal(
            new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" },
            GameLocator.ParseLibraryPaths(vdf).ToArray());
    }

    [Fact]
    public void ParseLibraryPaths_EmptyOrNull_YieldsNothing()
    {
        Assert.Empty(GameLocator.ParseLibraryPaths(null));
        Assert.Empty(GameLocator.ParseLibraryPaths("no path keys in here"));
    }

    [Fact]
    public void SteamCommonDirsFrom_RootPlusVdfLibraries_DedupesRoots_InOrder()
    {
        var vdf = "\"libraryfolders\" { \"1\" { \"path\" \"D:\\\\Games\\\\Steam\" } }";
        string? Read(string p) =>
            string.Equals(p, Path.Combine(@"C:\Steam", "steamapps", "libraryfolders.vdf"), StringComparison.OrdinalIgnoreCase)
                ? vdf : null;

        // the same root passed twice (with/without trailing slash) must collapse to one
        var got = GameLocator.SteamCommonDirsFrom(new[] { @"C:\Steam", @"C:\Steam\" }, Read);

        Assert.Equal(
            new[]
            {
                Path.Combine(@"C:\Steam", "steamapps", "common"),         // the root's own library
                Path.Combine(@"D:\Games\Steam", "steamapps", "common"),  // the extra library from the vdf
            },
            got.ToArray());
    }

    [Fact]
    public void SteamCommonDirsFrom_NoVdf_StillReturnsRootCommon()
    {
        var got = GameLocator.SteamCommonDirsFrom(new[] { @"C:\Steam" }, _ => null);
        Assert.Equal(new[] { Path.Combine(@"C:\Steam", "steamapps", "common") }, got.ToArray());
    }
}
