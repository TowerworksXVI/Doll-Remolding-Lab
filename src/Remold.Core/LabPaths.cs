using System;
using System.IO;

namespace Remold.Core;

/// <summary>
/// The single place the app derives its on-disk roots. Two roots, deliberately separate:
/// <list type="bullet">
/// <item><b>Durable</b> — settings, the mods library, first-run acceptance: anything the modder must not
/// lose. Lives BESIDE THE EXE, so a portable copy carries its state and an extract-over update keeps
/// it.</item>
/// <item><b>Cache</b> — index, thumbnails, logs: all regenerable from the game, so they stay under
/// <c>%LOCALAPPDATA%\DollRemoldingLab</c> and out of the portable zip.</item>
/// </list>
/// Every default path routes through here; no other type reads a user-profile folder.
/// </summary>
public static class LabPaths
{
    /// <summary>The cache-root folder name under LocalAppData.</summary>
    private const string CacheFolder = "DollRemoldingLab";

    /// <summary>Durable-state root: the app's own folder (exe-adjacent).</summary>
    public static string DurableRoot => AppContext.BaseDirectory;

    /// <summary>Regenerable-cache root: <c>%LOCALAPPDATA%\DollRemoldingLab</c>.</summary>
    public static string CacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CacheFolder);

    // ---- durable (beside the exe) ----

    /// <summary>The persisted <see cref="LabSettings"/> file.</summary>
    public static string SettingsFile => Path.Combine(DurableRoot, "settings.json");

    /// <summary>Where New Mod creates project folders when no library is set.</summary>
    public static string DefaultLibraryRoot => Path.Combine(DurableRoot, "mods");

    /// <summary>The first-run acceptance record (acceptable-use gate), beside the settings.</summary>
    public static string FirstRunAcceptanceFile => Path.Combine(DurableRoot, "first-run.json");

    // ---- cache (regenerable, LocalAppData) ----

    /// <summary>The parsed-catalog snapshot (<see cref="Bundles.CatalogIndex"/>): a re-encoding of one
    /// catalog file keyed to that file's identity, never a corpus product.</summary>
    public static string CatalogSnapshotFile(string catalogVersion) =>
        Path.Combine(CacheRoot, "index", $"catalog_{catalogVersion}.bin");

    /// <summary>The launch roster-fill snapshot (<see cref="Workbench.RosterSnapshot"/>), keyed to the
    /// asset-catalog version in the filename and re-checked inside.</summary>
    public static string RosterSnapshotFile(string catalogVersion) =>
        Path.Combine(CacheRoot, "index", $"roster_{catalogVersion}.json");

    /// <summary>The asset-sharing measurement (<see cref="Workbench.SharingIndex"/>), keyed to the
    /// asset-catalog version in the filename and re-checked inside.</summary>
    public static string SharingIndexFile(string catalogVersion) =>
        Path.Combine(CacheRoot, "index", $"sharing_{catalogVersion}.json");

    /// <summary>The shipped asset-sharing measurement, in the same format as
    /// <see cref="SharingIndexFile"/>: the install's starting point when the cache holds nothing for the
    /// running catalog. Ships beside the exe, so it is DURABLE rather than cache — an install that
    /// discards its cache still starts from here. The catalog version it was measured under is inside the
    /// file, never in the name.</summary>
    public static string SharingSeedFile => Path.Combine(DurableRoot, "data", "sharing_seed.json");

    /// <summary>The preview/thumbnail cache root.</summary>
    public static string ThumbnailRoot => Path.Combine(CacheRoot, "thumbs");

    /// <summary>Solved palette-recovery operators, keyed by source-mesh identity and the conditioning
    /// algorithm that produced them.</summary>
    public static string OperatorCacheRoot => Path.Combine(CacheRoot, "operators");

    /// <summary>Encoded texture blobs, keyed by source content and encode settings.</summary>
    public static string EncodedTextureRoot => Path.Combine(CacheRoot, "textures");

    /// <summary>The opt-in launch-timing log.</summary>
    public static string LaunchTimingLog => Path.Combine(CacheRoot, "launch_timing.log");
}
