using System;
using System.IO;
using System.Linq;

namespace Remold.Core;

/// <summary>
/// Facts about a located game install. The asset-catalog version (the <c>N</c> in
/// <c>catalog_main_&lt;N&gt;.bin</c>) is the cache-invalidation key: it changes with the Addressables
/// bundle channel, NOT with text-only patches, which update on a separate channel.
/// </summary>
public static class GameInfo
{
    /// <summary>The catalog-version sentinel for an install with no readable catalog. An index/bone
    /// cache keyed on it would never invalidate, so "unknown" is never cached (build-only).</summary>
    public const string UnknownVersion = "unknown";

    /// <summary>The AssetBundles_Windows directory for a game root (the folder holding GF2_Exilium.exe).</summary>
    public static string BundleDir(string gameRoot)
    {
        var dir = GameLocator.BundleDirOf(gameRoot);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"no AssetBundles_Windows under {gameRoot}");
        return dir;
    }

    /// <summary>The asset-catalog version string (e.g. "24535"), or <see cref="UnknownVersion"/>. With
    /// several catalog files present the highest NUMERIC version wins — enumeration order or a string
    /// compare could key the cache to a stale catalog and serve an old index against new bundles.</summary>
    public static string CatalogVersion(string gameRoot)
    {
        var dir = BundleDir(gameRoot);
        long? best = null;
        foreach (var f in Directory.EnumerateFiles(dir, "catalog_main_*.bin"))
        {
            var n = Path.GetFileNameWithoutExtension(f).Split('_').LastOrDefault();
            if (long.TryParse(n, out var v) && (best is null || v > best)) best = v;
        }
        return best?.ToString() ?? UnknownVersion;
    }

    /// <summary>The current Addressables catalog file, or null. Must pick the highest NUMERIC version by
    /// the same rule as <see cref="CatalogVersion"/> — the two have to agree on which is current.</summary>
    public static string? CatalogPath(string gameRoot)
    {
        var dir = BundleDir(gameRoot);
        string? bestPath = null; long? best = null;
        foreach (var f in Directory.EnumerateFiles(dir, "catalog_main_*.bin"))
        {
            var n = Path.GetFileNameWithoutExtension(f).Split('_').LastOrDefault();
            if (long.TryParse(n, out var v) && (best is null || v > best)) { best = v; bestPath = f; }
        }
        return bestPath;
    }
}
