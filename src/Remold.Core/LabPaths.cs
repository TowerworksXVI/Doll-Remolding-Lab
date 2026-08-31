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
    private const string StockTextureFolder = "stocktex";
    private const string RiggedGlbFolder = "rigs";
    private const string ThumbFolder = "thumbs";

    /// <summary>The regenerable derived-cache trees, as folder names under a cache root. THE definition the
    /// force-rescan sweep works from (<see cref="CacheReset.ClearDerivedCaches"/>) — names rather than full
    /// paths, so the sweep can be driven against a temp root. Anything a future cache adds is swept only
    /// once it is listed here.</summary>
    public static IReadOnlyList<string> DerivedCacheFolders { get; } =
        new[] { IndexFolder, OperatorFolder, TextureFolder, StockTextureFolder, RiggedGlbFolder, ThumbFolder };

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

    /// <summary>The compact parsed GFF-manifest snapshot. The file rechecks the source manifest's full path,
    /// length and mtime internally, so two installs carrying the same catalog version cannot serve one
    /// another's forward map.</summary>
    public static string GffManifestSnapshotFile(string catalogVersion) =>
        Path.Combine(IndexRoot, $"gff_{catalogVersion}.bin");

    /// <summary>The compact locale map used by roster display names. Its binary header identifies the
    /// locale table and every name-bearing roster table, so this stable filename cannot serve stale text.</summary>
    public static string DisplayNameSnapshotFile(string locale) =>
        Path.Combine(IndexRoot, $"display_names_{locale.ToLowerInvariant()}.bin");

    /// <summary>Completion records for exact-input builds. The records live outside both the authored
    /// project and the published mod, and the index rescan sweeps them with every other derived index.</summary>
    public static string BuildCompletionRoot => Path.Combine(IndexRoot, "builds");

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

    /// <summary>The sharing measurement's observation memo (<see cref="Workbench.AssetHashMemo"/>): the
    /// mesh and texture hashes it has already measured, keyed by the owning bundle's CONTENT identity plus
    /// the object selector. Deliberately NOT keyed by catalog version in the name, for the same reason as
    /// <see cref="CandidacyCacheFile"/>: a game update misses exactly the bundles it rewrote, so a row that
    /// has to be measured again still costs no read for the values behind it that did not move.</summary>
    public static string AssetHashMemoFile => Path.Combine(IndexRoot, "asset_hashes.json");

    /// <summary>The rigged export's candidacy memo (<see cref="Export.CandidacyCache"/>): per-mesh bone
    /// table, skin narrowness and posed set, so an open doesn't re-sum every subject part's skin stream.
    /// Deliberately NOT keyed by catalog version in the name — every entry is keyed by its bundle's own
    /// CONTENT, so a game update misses exactly the bundles it rewrote and a version-named file would throw
    /// away every still-valid entry alongside them.</summary>
    public static string CandidacyCacheFile => CandidacyCacheFileIn(CacheRoot);

    /// <summary>The <see cref="CandidacyCacheFile"/> rule under an explicit cache root, so a caller holding
    /// a redirected root (a test, or the force-rescan sweep's seam) names the same file the sweep clears.
    /// Without it a redirected writer memos into the real root while the sweep empties the redirected one,
    /// and the two disagree about which memo a run is answering from.</summary>
    public static string CandidacyCacheFileIn(string cacheRoot) =>
        Path.Combine(cacheRoot, IndexFolder, "candidacy.json");

    /// <summary>The preview/thumbnail cache root.</summary>
    public static string ThumbnailRoot => Path.Combine(CacheRoot, ThumbFolder);

    /// <summary>Solved palette-recovery operators, keyed by source-mesh identity and the conditioning
    /// algorithm that produced them.</summary>
    public static string OperatorCacheRoot => Path.Combine(CacheRoot, OperatorFolder);

    /// <summary>Encoded texture blobs, keyed by source content and encode settings.</summary>
    public static string EncodedTextureRoot => Path.Combine(CacheRoot, TextureFolder);

    /// <summary>Full-resolution stock texture PNGs (<see cref="Textures.StockTextureCache"/>), keyed by the
    /// owning bundle's manifest-stated content identity and the texture's name — what a Blender open links
    /// into its run folder instead of decoding the game's maps again. Deliberately NOT keyed by catalog
    /// version, for the same reason as <see cref="CandidacyCacheFile"/>: a game update misses exactly the
    /// bundles it rewrote.</summary>
    public static string StockTextureRoot => StockTextureRootIn(CacheRoot);

    /// <summary>The <see cref="StockTextureRoot"/> rule under an explicit cache root, so a caller holding a
    /// redirected root (a test, or the force-rescan sweep's seam) names the same tree the sweep does.</summary>
    public static string StockTextureRootIn(string cacheRoot) => Path.Combine(cacheRoot, StockTextureFolder);

    /// <summary>Game-side rigged-GLB exports (<see cref="Workbench.RiggedGlbCache"/>), keyed by the cache
    /// schema and the complete install/subject identity recorded in each completion manifest. The tree is
    /// wholly derived and is therefore part of <see cref="DerivedCacheFolders"/>.</summary>
    public static string RiggedGlbRoot => RiggedGlbRootIn(CacheRoot);

    /// <summary>The <see cref="RiggedGlbRoot"/> rule under an explicit cache root, shared by redirected
    /// writers and the force-rescan sweep.</summary>
    public static string RiggedGlbRootIn(string cacheRoot) => Path.Combine(cacheRoot, RiggedGlbFolder);

    /// <summary>The general app log: the technical detail behind what the screens say in plain words.
    /// One file per launch; the first write of a run moves the previous log to
    /// <see cref="AppLogPrevFile"/>.</summary>
    public static string AppLogFile => Path.Combine(CacheRoot, "app.log");

    /// <summary>The previous launch's <see cref="AppLogFile"/>, kept one deep.</summary>
    public static string AppLogPrevFile => Path.Combine(CacheRoot, "app.log.prev");

    /// <summary>The opt-in launch-timing log.</summary>
    public static string LaunchTimingLog => Path.Combine(CacheRoot, "launch_timing.log");

    /// <summary>The always-on Blender-open timing log: one phase-timed block per open, fresh each app
    /// launch. Diagnostic-only — nothing reads it back.</summary>
    public static string BlenderOpenTimingLog => Path.Combine(CacheRoot, "blender_open_timing.log");

    // ---- shipped with the app (beside the assemblies) ----

    /// <summary>The shipped asset-sharing measurement, in the same format as
    /// <see cref="SharingIndexFile"/>: the install's starting point when the cache holds nothing for the
    /// running catalog. It is CONTENT the build copies beside the assemblies, not user state, so it is
    /// anchored to <see cref="AppContext.BaseDirectory"/> — in the release layout that is the <c>app</c>
    /// folder, which is where the copy lands. The catalog version it was measured under is inside the file,
    /// never in the name.
    ///
    /// <para><b>Minting a release seed</b> is copying one install's cache artifacts, and it is two files —
    /// ALWAYS two, and always from ONE pass. On a current game install, clear the derived caches
    /// (Tools · Rescan game files) so the pass measures the whole population, let it finish, then copy
    /// <see cref="SharingIndexFile"/> for that catalog over this file and <see cref="AssetHashMemoFile"/>
    /// over <see cref="AssetHashSeedFile"/>. The pair is the unit: the index's rows are gated on what its
    /// own read records say the bundles held, and the memo is what spares a fresh install the reads behind
    /// the rows that no longer match — so a seed minted without its memo ships a measurement whose
    /// invalidations each cost a full bundle read again. <b>Any tooling that regenerates a seed outside
    /// this repo must produce the pair too</b>; a runner written before the memo existed will happily
    /// write half of it. Neither file carries a game-derived string, which is what makes one machine's
    /// measurement shippable, and the release pack refuses a pair whose schemas this build does not read
    /// (see <see cref="Workbench.ShippedMeasurement"/>).</para>
    /// </summary>
    public static string SharingSeedFile => Path.Combine(AppContext.BaseDirectory, SharingSeedRelativePath);

    /// <summary>The shipped seed's path relative to the folder the build lays it down in — beside the
    /// assemblies at runtime, and the publish folder the release pack reads. Named here because this is
    /// where the app's on-disk paths live, and both readers take it from here rather than spelling it
    /// again.</summary>
    public const string SharingSeedRelativePath = @"data\sharing_seed.json";

    /// <summary><see cref="AssetHashSeedFile"/>'s half of the same pair (see
    /// <see cref="SharingSeedRelativePath"/>).</summary>
    public const string AssetHashSeedRelativePath = @"data\asset_hashes_seed.json";

    /// <summary>The shipped observation memo, in the same format as <see cref="AssetHashMemoFile"/> and
    /// minted from it (see <see cref="SharingSeedFile"/>). It is what makes an invalidated seed row cheap:
    /// the row is measured again, but every mesh and texture whose bundle content the shipped measurement
    /// already saw comes back without a read.</summary>
    public static string AssetHashSeedFile =>
        Path.Combine(AppContext.BaseDirectory, AssetHashSeedRelativePath);

    /// <summary>The shipped shader slot catalog (<see cref="Migoto.ShaderSlotCatalog"/>) — the ps registers
    /// a build probes for each material input. Content the build copies beside the assemblies, like
    /// <see cref="SharingSeedFile"/>, and versioned inside the file rather than in its name.</summary>
    public static string ShaderSlotCatalogFile =>
        Path.Combine(AppContext.BaseDirectory, "data", "charps_slots.json");
}
