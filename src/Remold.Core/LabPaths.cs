using System;
using System.Collections.Generic;
using System.IO;

namespace Remold.Core;

/// <summary>
/// The single place the app derives its on-disk paths. Three anchors, deliberately separate by what may be
/// lost:
/// <list type="bullet">
/// <item><b>Durable</b> — settings, the mods library, first-run acceptance: anything the modder must not
/// lose. Lives BESIDE THE EXE, so a portable copy carries its state and an extract-over update keeps
/// it.</item>
/// <item><b>Cache</b> — index, thumbnails, logs: all regenerable from the game, so they stay under
/// <c>%LOCALAPPDATA%\DollRemoldingLab</c> and out of the portable zip.</item>
/// <item><b>Shipped</b> — content the build lays down beside the assemblies, replaced wholesale by the next
/// install. Anchored to <see cref="AppContext.BaseDirectory"/>, which in the release layout is the
/// <c>app</c> folder rather than the root the modder sees.</item>
/// </list>
/// Every default path routes through here; no other type reads a user-profile folder.
/// </summary>
public static class LabPaths
{
    /// <summary>The cache-root folder name under LocalAppData.</summary>
    private const string CacheFolder = "DollRemoldingLab";

    // The regenerable trees under the cache root, named once. A force rescan sweeps exactly these, so a
    // folder that isn't named here (the opt-in launch-timing log) is never swept.
    private const string IndexFolder = "index";
    private const string OperatorFolder = "operators";
    private const string TextureFolder = "textures";
    private const string ThumbFolder = "thumbs";

    /// <summary>The regenerable derived-cache trees, as folder names under a cache root. THE definition the
    /// force-rescan sweep works from (<see cref="CacheReset.ClearDerivedCaches"/>) — names rather than full
    /// paths, so the sweep can be driven against a temp root. Anything a future cache adds is swept only
    /// once it is listed here.</summary>
    public static IReadOnlyList<string> DerivedCacheFolders { get; } =
        new[] { IndexFolder, OperatorFolder, TextureFolder, ThumbFolder };

    /// <summary>Durable-state root: the folder holding the app EXE. In the release layout the exe sits
    /// alone at the root with the assemblies in an <c>app</c> subfolder (so the modder's state — mods,
    /// settings — stays at the root they see); in a flat layout the exe's folder and
    /// <see cref="AppContext.BaseDirectory"/> are the same place. A foreign host process (the test
    /// runner) fails the structural guard and stays on the base directory.</summary>
    public static string DurableRoot => DurableRootFor(Environment.ProcessPath, AppContext.BaseDirectory);

    /// <summary>The <see cref="DurableRoot"/> rule on explicit inputs: the exe's folder when the base
    /// directory IS that folder or sits directly under it, else the base directory. The direct-child
    /// shape is the release layout (<c>exe + app\</c>); anything looser would follow an unrelated host
    /// process to its own folder.</summary>
    public static string DurableRootFor(string? exePath, string baseDir)
    {
        string? exeDir;
        try { exeDir = Path.GetDirectoryName(exePath); }
        catch (ArgumentException) { return baseDir; }
        if (string.IsNullOrEmpty(exeDir)) return baseDir;
        var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir));
        var e = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exeDir));
        if (string.Equals(b, e, StringComparison.OrdinalIgnoreCase)) return baseDir;
        if (string.Equals(Path.GetDirectoryName(b), e, StringComparison.OrdinalIgnoreCase)) return exeDir;
        return baseDir;
    }

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
        Path.Combine(IndexRoot, $"catalog_{catalogVersion}.bin");

    /// <summary>The catalog-keyed snapshot folder holding <see cref="CatalogSnapshotFile"/>,
    /// <see cref="RosterSnapshotFile"/> and <see cref="SharingIndexFile"/>.</summary>
    public static string IndexRoot => Path.Combine(CacheRoot, IndexFolder);

    /// <summary>The launch roster-fill snapshot (<see cref="Workbench.RosterSnapshot"/>), keyed to the
    /// asset-catalog version in the filename and re-checked inside.</summary>
    public static string RosterSnapshotFile(string catalogVersion) =>
        Path.Combine(IndexRoot, $"roster_{catalogVersion}.json");

    /// <summary>The asset-sharing measurement (<see cref="Workbench.SharingIndex"/>), keyed to the
    /// asset-catalog version in the filename and re-checked inside.</summary>
    public static string SharingIndexFile(string catalogVersion) =>
        Path.Combine(IndexRoot, $"sharing_{catalogVersion}.json");

    /// <summary>The rigged export's candidacy memo (<see cref="Export.CandidacyCache"/>): per-mesh bone
    /// table, skin narrowness and posed set, so an open doesn't re-sum every subject part's skin stream.
    /// Deliberately NOT keyed by catalog version in the name — every entry is keyed by its bundle's own
    /// CONTENT, so a game update misses exactly the bundles it rewrote and a version-named file would throw
    /// away every still-valid entry alongside them.</summary>
    public static string CandidacyCacheFile => Path.Combine(IndexRoot, "candidacy.json");

    /// <summary>The preview/thumbnail cache root.</summary>
    public static string ThumbnailRoot => Path.Combine(CacheRoot, ThumbFolder);

    /// <summary>Solved palette-recovery operators, keyed by source-mesh identity and the conditioning
    /// algorithm that produced them.</summary>
    public static string OperatorCacheRoot => Path.Combine(CacheRoot, OperatorFolder);

    /// <summary>Encoded texture blobs, keyed by source content and encode settings.</summary>
    public static string EncodedTextureRoot => Path.Combine(CacheRoot, TextureFolder);

    /// <summary>The opt-in launch-timing log.</summary>
    public static string LaunchTimingLog => Path.Combine(CacheRoot, "launch_timing.log");

    // ---- shipped with the app (beside the assemblies) ----

    /// <summary>The shipped asset-sharing measurement, in the same format as
    /// <see cref="SharingIndexFile"/>: the install's starting point when the cache holds nothing for the
    /// running catalog. It is CONTENT the build copies beside the assemblies, not user state, so it is
    /// anchored to <see cref="AppContext.BaseDirectory"/> — in the release layout that is the <c>app</c>
    /// folder, which is where the copy lands. The catalog version it was measured under is inside the file,
    /// never in the name.</summary>
    public static string SharingSeedFile =>
        Path.Combine(AppContext.BaseDirectory, "data", "sharing_seed.json");
}
