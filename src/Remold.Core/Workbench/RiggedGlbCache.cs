using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Remold.Core.Mesh;

namespace Remold.Core.Workbench;

/// <summary>
/// Persistent game-side rigged-GLB exports. One cache entry is one per-part route or one exact stock
/// composition route; entries with the same catalog/subject/roster identity live under one subject
/// directory and are evicted together.
///
/// <para><b>Correctness gate.</b> A hit requires this code's schema, the catalog version, the subject's
/// logical fingerprint, the canonical roster/spec fingerprint, and every bundle-content identity recorded
/// by the successful build. The first four select and verify the directory; <see cref="BundleReads"/>
/// revalidates the last against the current install before any file is served.</para>
///
/// <para><b>Game-side purity.</b> <see cref="TryStore"/> refuses a build that was cancelled, observed a
/// transient failure, or contains any project-authored input. Its completion manifest repeats those facts;
/// a reader requires them. A store failure is only a lost optimization. It never invalidates an otherwise
/// successful open.</para>
///
/// <para><b>All-or-nothing serve.</b> Every requested GLB/sidecar pair is copied and hash-checked in a new
/// sibling staging directory. Only a completed staging directory is renamed to the requested destination.
/// Any discrepancy is a cache miss and leaves that destination absent, so a caller can pay the full build
/// cost without mixing cached and freshly-built route files.</para>
/// </summary>
public sealed class RiggedGlbCache
{
    /// <summary>On-disk manifest/key schema. A bump selects a disjoint tree.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Pruning starts after the cache exceeds four gibibytes.</summary>
    public const long HighWaterBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Whole subjects are removed least-recently-used until no more than three gibibytes remain.</summary>
    public const long PruneTargetBytes = 3L * 1024 * 1024 * 1024;

    private const string CompletionName = "complete.json";
    private const string CachedGlbName = "rig.glb";
    private const string CachedMapsName = "rig.maps.json";
    private const string AccessName = ".access";
    private const string PurityGameSide = "game-side";
    private const string PurityPreparedPart = "content-addressed-prepared-part";
    private const int PruneSampleInterval = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _root;
    private readonly long _highWaterBytes;
    private readonly long _pruneTargetBytes;
    private readonly ConcurrentDictionary<string, object> _subjectGates =
        new(StringComparer.OrdinalIgnoreCase);
    private long _subjectPublications;
    private long _fullTreeEnumerations;

    /// <summary>The number of full cache-tree measurements performed by this instance. Exposed internally
    /// so the publication-boundary regression test can pin tree walks rather than elapsed time.</summary>
    internal long FullTreeEnumerations => System.Threading.Interlocked.Read(ref _fullTreeEnumerations);

    /// <summary>The install/subject identity whose exact values are repeated in every completion manifest.
    /// <paramref name="RosterSpecFingerprint"/> is minted by the caller from the canonical roster and
    /// rig-build specification; no display string is a cache key.</summary>
    public readonly record struct Identity(
        string CatalogVersion,
        string SubjectFingerprint,
        string RosterSpecFingerprint);

    /// <summary>One completed game-side GLB build offered to the cache. <paramref name="Key"/> is the stable route
    /// key within the subject; paths point at the successful open's files. A maps sidecar is optional because
    /// an export with no maps legitimately has none.</summary>
    public readonly record struct Artifact(string Key, string GlbPath, string? MapsPath,
        IReadOnlyList<string>? RequiredBundleReads = null,
        IReadOnlyList<StockTexture>? StockTextures = null);

    /// <summary>One durable stock-PNG dependency of a cached rig. The content/name/path-id triple selects
    /// the existing <see cref="Textures.StockTextureCache"/> entry; <paramref name="DestinationFileName"/>
    /// is the run-local name the rig sidecar references. Length and SHA-256 pin the exact PNG bytes the GLB
    /// was built over, so damage inside an otherwise whole PNG turns the route into a miss.</summary>
    public readonly record struct StockTexture(string BundleContentId, string TextureName, long PathId,
        string DestinationFileName, long Length, string Sha256);

    /// <summary>The dependencies returned only with a complete route hit. Required bundle reads are the
    /// build's recorded provenance (and the caller's non-empty completion gate) — a warm serve never
    /// re-reads them; stock textures rebuild the sibling run folder the cached sidecars address.</summary>
    public readonly record struct ServeDependencies(
        string BundleReads,
        IReadOnlyList<string> RequiredBundleReads,
        IReadOnlyList<StockTexture> StockTextures);

    /// <summary>One content-addressed prepared workspace offered to the cache. Its GLB and optional map
    /// record are made portable during publication, so no cached record retains a project/run path.</summary>
    public readonly record struct PreparedArtifact(string Key, string GlbPath);

    /// <summary>One cached GLB requested for a run. The destination name must be a filename ending in
    /// <c>.glb</c>; when the entry has a sidecar it is emitted beside it as <c>.maps.json</c>.</summary>
    public readonly record struct Request(string Key, string DestinationGlbName);

    /// <summary>The facts that decide whether a successful build is safe to share across projects.</summary>
    public readonly record struct BuildState(
        bool GameSideOnly,
        bool HadTransientFailures,
        bool WasCanceled,
        bool HadProjectAuthoredContent)
    {
        /// <summary>The only state eligible for publication.</summary>
        public static BuildState SuccessfulGameBuild { get; } = new(
            GameSideOnly: true,
            HadTransientFailures: false,
            WasCanceled: false,
            HadProjectAuthoredContent: false);

        internal bool MayPublish => GameSideOnly
            && !HadTransientFailures
            && !WasCanceled
            && !HadProjectAuthoredContent;
    }

    /// <param name="rootOverride">Cache tree; defaults to <see cref="LabPaths.RiggedGlbRoot"/>. Tests and
    /// redirected cache owners pass an explicit tree.</param>
    /// <param name="highWaterBytes">Prune trigger; production uses <see cref="HighWaterBytes"/>.</param>
    /// <param name="pruneTargetBytes">Post-prune target; production uses <see cref="PruneTargetBytes"/>.</param>
    public RiggedGlbCache(string? rootOverride = null,
        long highWaterBytes = HighWaterBytes,
        long pruneTargetBytes = PruneTargetBytes)
    {
        if (highWaterBytes < 0) throw new ArgumentOutOfRangeException(nameof(highWaterBytes));
        if (pruneTargetBytes < 0 || pruneTargetBytes > highWaterBytes)
            throw new ArgumentOutOfRangeException(nameof(pruneTargetBytes));
        _root = rootOverride ?? LabPaths.RiggedGlbRoot;
        _highWaterBytes = highWaterBytes;
        _pruneTargetBytes = pruneTargetBytes;
    }

    /// <summary>
    /// Best-effort publication of one completed, pure game-side GLB. Payloads are copied to unique temps,
    /// validated there, and moved into place before the completion manifest. A failure returns false and
    /// leaves no completion marker for a partial replacement.
    /// </summary>
    public bool TryStore(Identity identity, string bundleReads, Artifact artifact, BuildState build)
    {
        if (!build.MayPublish || !ValidIdentity(identity) || !ValidBundleReads(bundleReads)
            || string.IsNullOrWhiteSpace(artifact.Key) || string.IsNullOrWhiteSpace(artifact.GlbPath))
            return false;
        var requiredReads = NormalizeRequiredReads(artifact.RequiredBundleReads);
        var stockTextures = NormalizeStockTextures(artifact.StockTextures);
        if (requiredReads is null || stockTextures is null) return false;

        string subjectDir;
        string entryDir;
        try
        {
            subjectDir = SubjectDirectoryFor(identity);
            entryDir = ArtifactDirectoryFor(identity, artifact.Key);
        }
        catch { return false; }

        var gate = _subjectGates.GetOrAdd(subjectDir, static _ => new object());
        lock (gate)
        {
            string? glbTemp = null;
            string? mapsTemp = null;
            string? completionTemp = null;
            try
            {
                if (IsReparsePoint(subjectDir) || IsReparsePoint(entryDir)) return false;
                Directory.CreateDirectory(entryDir);

                var glbTarget = Path.Combine(entryDir, CachedGlbName);
                var mapsTarget = Path.Combine(entryDir, CachedMapsName);
                var completionTarget = Path.Combine(entryDir, CompletionName);
                var mint = Guid.NewGuid().ToString("N");
                glbTemp = glbTarget + "." + mint + ".tmp";
                mapsTemp = mapsTarget + "." + mint + ".tmp";
                completionTemp = completionTarget + "." + mint + ".tmp";

                File.Copy(artifact.GlbPath, glbTemp, overwrite: false);
                var glb = DescribeGlb(glbTemp);
                if (glb is null) return false;

                FileRecord? maps = null;
                if (artifact.MapsPath is not null)
                {
                    File.Copy(artifact.MapsPath, mapsTemp, overwrite: false);
                    maps = DescribeJson(mapsTemp);
                    if (maps is null) return false;
                }

                var manifest = new CompletionManifest
                {
                    SchemaVersion = SchemaVersion,
                    Purity = PurityGameSide,
                    BuildCompleted = true,
                    HadTransientFailures = false,
                    WasCanceled = false,
                    HadProjectAuthoredContent = false,
                    CatalogVersion = identity.CatalogVersion,
                    SubjectFingerprint = identity.SubjectFingerprint,
                    RosterSpecFingerprint = identity.RosterSpecFingerprint,
                    BundleReads = bundleReads,
                    ArtifactKey = artifact.Key,
                    Glb = glb,
                    Maps = maps,
                    RequiredBundleReads = requiredReads,
                    StockTextures = stockTextures,
                };
                File.WriteAllBytes(completionTemp,
                    JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));

                // Invalidate the previous generation before replacing either payload. A reader that already
                // loaded it verifies both its copied bytes and the unchanged manifest again before commit.
                if (File.Exists(completionTarget)) File.Delete(completionTarget);
                File.Move(glbTemp, glbTarget, overwrite: true);
                glbTemp = null;
                if (maps is not null)
                {
                    File.Move(mapsTemp, mapsTarget, overwrite: true);
                    mapsTemp = null;
                }
                else if (File.Exists(mapsTarget))
                {
                    File.Delete(mapsTarget);
                }
                File.Move(completionTemp, completionTarget, overwrite: true);
                completionTemp = null;

                Touch(subjectDir);
                CacheTemps.SweepMinted(glbTarget);
                CacheTemps.SweepMinted(mapsTarget);
                CacheTemps.SweepMinted(completionTarget);
            }
            catch
            {
                return false; // an unavailable cache only loses the optimization
            }
            finally
            {
                DeleteFile(glbTemp);
                DeleteFile(mapsTemp);
                DeleteFile(completionTemp);
            }
        }

        return true;
    }

    /// <summary>Best-effort publication of one content-addressed prepared part. The stored workspace is
    /// rewritten to use only cache-local picture dependencies, and every resulting file is recorded in the
    /// completion manifest. Unlike <see cref="TryStore"/>, authored input is allowed because the artifact
    /// key is minted from its bytes and bindings by the caller.</summary>
    public bool TryStorePrepared(Identity identity, string bundleReads, PreparedArtifact artifact)
    {
        if (!ValidIdentity(identity) || !ValidBundleReads(bundleReads)
            || string.IsNullOrWhiteSpace(artifact.Key) || string.IsNullOrWhiteSpace(artifact.GlbPath))
            return false;

        string subjectDir;
        string entryDir;
        string staging;
        try
        {
            subjectDir = SubjectDirectoryFor(identity);
            entryDir = ArtifactDirectoryFor(identity, artifact.Key);
            staging = entryDir + ".prepared." + Guid.NewGuid().ToString("N") + ".tmp";
        }
        catch { return false; }

        var gate = _subjectGates.GetOrAdd(subjectDir, static _ => new object());
        lock (gate)
        {
            try
            {
                if (IsReparsePoint(subjectDir) || IsReparsePoint(entryDir)) return false;
                Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                string cachedGlb = Path.Combine(staging, CachedGlbName);
                string assets = Path.Combine(".prepared-assets", HashKey(artifact.Key));
                PreviewMaps.CopyPortableWorkspace(artifact.GlbPath, cachedGlb, assets,
                    requireSelfContained: true);

                var glb = DescribeGlb(cachedGlb);
                if (glb is null) return false;
                string cachedMaps = Path.Combine(staging, CachedMapsName);
                FileRecord? maps = null;
                string portableMaps = PreviewMaps.SidecarPath(cachedGlb);
                if (File.Exists(portableMaps))
                {
                    maps = DescribeJson(cachedMaps);
                    if (maps is null) return false;
                }

                var portableFiles = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                    .Where(path => !string.Equals(path, cachedGlb, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(path, cachedMaps, StringComparison.OrdinalIgnoreCase))
                    .Select(path => DescribePortableFile(staging, path))
                    .ToList();
                if (portableFiles.Any(file => file is null)) return false;

                var manifest = new CompletionManifest
                {
                    SchemaVersion = SchemaVersion,
                    Purity = PurityPreparedPart,
                    BuildCompleted = true,
                    HadTransientFailures = false,
                    WasCanceled = false,
                    HadProjectAuthoredContent = true,
                    CatalogVersion = identity.CatalogVersion,
                    SubjectFingerprint = identity.SubjectFingerprint,
                    RosterSpecFingerprint = identity.RosterSpecFingerprint,
                    BundleReads = bundleReads,
                    ArtifactKey = artifact.Key,
                    Glb = glb,
                    Maps = maps,
                    PortableFiles = portableFiles.Select(file => file!).ToList(),
                };
                File.WriteAllBytes(Path.Combine(staging, CompletionName),
                    JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));

                if (Directory.Exists(entryDir)) CacheReset.DeleteTree(entryDir);
                Directory.Move(staging, entryDir);
                Touch(subjectDir);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (Directory.Exists(staging)) CacheReset.DeleteTree(staging);
            }
        }
        return true;
    }

    /// <summary>Serve one prepared artifact independently into an existing run's parts directory. A miss
    /// affects only this part. Payloads and all portable picture dependencies are hash-checked in staging;
    /// the destination GLB and sidecar are committed only after that validation completes.</summary>
    public bool TryServePrepared(Identity identity,
        IReadOnlyDictionary<string, string> currentBundleKeys, string artifactKey, string destinationGlb)
    {
        if (!ValidIdentity(identity) || currentBundleKeys is null || string.IsNullOrWhiteSpace(artifactKey)
            || string.IsNullOrWhiteSpace(destinationGlb)) return false;

        string subjectDir;
        string entryDir;
        string destination;
        string staging;
        try
        {
            subjectDir = SubjectDirectoryFor(identity);
            entryDir = ArtifactDirectoryFor(identity, artifactKey);
            destination = Path.GetFullPath(destinationGlb);
            string directory = Path.GetDirectoryName(destination)!;
            staging = Path.Combine(directory,
                ".prepared-part." + Guid.NewGuid().ToString("N") + ".tmp");
        }
        catch { return false; }

        var gate = _subjectGates.GetOrAdd(subjectDir, static _ => new object());
        lock (gate)
        {
            string? committedAssets = null;
            bool glbCommitted = false;
            bool mapsCommitted = false;
            try
            {
                string destinationMaps = PreviewMaps.SidecarPath(destination);
                if (File.Exists(destination) || File.Exists(destinationMaps) || IsReparsePoint(subjectDir)
                    || IsReparsePoint(entryDir)) return false;

                string completionPath = Path.Combine(entryDir, CompletionName);
                byte[] before = File.ReadAllBytes(completionPath);
                var manifest = JsonSerializer.Deserialize<CompletionManifest>(before, JsonOptions);
                if (!PreparedManifestMatches(manifest, identity, artifactKey, currentBundleKeys)) return false;
                if (manifest!.Maps is null) mapsCommitted = true;

                Directory.CreateDirectory(staging);
                string stagedGlb = Path.Combine(staging, CachedGlbName);
                File.Copy(Path.Combine(entryDir, CachedGlbName), stagedGlb, overwrite: false);
                if (!Matches(DescribeGlb(stagedGlb), manifest.Glb)) return false;

                string? stagedMaps = null;
                if (manifest.Maps is not null)
                {
                    stagedMaps = Path.Combine(staging, CachedMapsName);
                    File.Copy(Path.Combine(entryDir, CachedMapsName), stagedMaps, overwrite: false);
                    if (!Matches(DescribeJson(stagedMaps), manifest.Maps)) return false;
                }

                foreach (var file in manifest.PortableFiles)
                {
                    string source = PortablePath(entryDir, file.RelativePath);
                    string target = PortablePath(staging, file.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target, overwrite: false);
                    if (!Matches(DescribeFile(target), file)) return false;
                }
                if (!before.AsSpan().SequenceEqual(File.ReadAllBytes(completionPath))) return false;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string stagedAssetsRoot = Path.Combine(staging, ".prepared-assets");
                if (Directory.Exists(stagedAssetsRoot))
                {
                    string destinationAssetsRoot = Path.Combine(Path.GetDirectoryName(destination)!,
                        ".prepared-assets");
                    Directory.CreateDirectory(destinationAssetsRoot);
                    foreach (var assets in Directory.EnumerateDirectories(stagedAssetsRoot))
                    {
                        string target = Path.Combine(destinationAssetsRoot, Path.GetFileName(assets));
                        if (Directory.Exists(target) || File.Exists(target)) return false;
                        Directory.Move(assets, target);
                        committedAssets = target;
                    }
                }
                File.Move(stagedGlb, destination, overwrite: false);
                glbCommitted = true;
                if (stagedMaps is not null)
                {
                    File.Move(stagedMaps, destinationMaps, overwrite: false);
                    mapsCommitted = true;
                }
                Touch(subjectDir);
                return true;
            }
            catch { return false; }
            finally
            {
                if (!glbCommitted && committedAssets is not null && Directory.Exists(committedAssets))
                    CacheReset.DeleteTree(committedAssets);
                if (glbCommitted && !mapsCommitted)
                {
                    DeleteFile(destination);
                    if (committedAssets is not null && Directory.Exists(committedAssets))
                        CacheReset.DeleteTree(committedAssets);
                }
                if (Directory.Exists(staging)) CacheReset.DeleteTree(staging);
            }
        }
    }

    /// <summary>
    /// Attempt to serve every requested GLB into one new destination directory. Any missing, unreadable,
    /// stale, malformed, or changed entry is one route-wide miss. On false, the destination is absent.
    /// </summary>
    public bool TryServe(Identity identity,
        IReadOnlyDictionary<string, string> currentBundleKeys,
        IReadOnlyList<Request> requests,
        string destinationDirectory) =>
        TryServe(identity, currentBundleKeys, requests, destinationDirectory, out _);

    /// <summary><inheritdoc cref="TryServe(Identity, IReadOnlyDictionary{string, string}, IReadOnlyList{Request}, string)"/>
    /// A hit also returns the game reads and stock PNGs the caller must revalidate before committing its
    /// whole run directory.</summary>
    public bool TryServe(Identity identity,
        IReadOnlyDictionary<string, string> currentBundleKeys,
        IReadOnlyList<Request> requests,
        string destinationDirectory,
        out ServeDependencies dependencies)
    {
        dependencies = default;
        if (!ValidIdentity(identity) || currentBundleKeys is null || requests is null || requests.Count == 0
            || string.IsNullOrWhiteSpace(destinationDirectory))
            return false;

        var artifactKeys = new HashSet<string>(StringComparer.Ordinal);
        var destinationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            if (string.IsNullOrWhiteSpace(request.Key) || !artifactKeys.Add(request.Key)
                || !ValidGlbFileName(request.DestinationGlbName)
                || !destinationNames.Add(request.DestinationGlbName)
                || !destinationNames.Add(Path.ChangeExtension(request.DestinationGlbName, ".maps.json")!))
                return false;
        }

        string subjectDir;
        string destination;
        string staging;
        try
        {
            subjectDir = SubjectDirectoryFor(identity);
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
            var parent = Path.GetDirectoryName(destination);
            var leaf = Path.GetFileName(destination);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(leaf)) return false;
            staging = Path.Combine(parent,
                "." + leaf + ".rigcache." + Guid.NewGuid().ToString("N") + ".tmp");
        }
        catch { return false; }

        var gate = _subjectGates.GetOrAdd(subjectDir, static _ => new object());
        var requiredReads = new SortedSet<string>(StringComparer.Ordinal);
        var stockTextures = new Dictionary<string, StockTexture>(StringComparer.OrdinalIgnoreCase);
        string? bundleReads = null;
        lock (gate)
        {
            try
            {
                if (Directory.Exists(destination) || File.Exists(destination) || IsReparsePoint(subjectDir))
                    return false;
                Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                Directory.CreateDirectory(staging);

                foreach (var request in requests)
                {
                    var entryDir = ArtifactDirectoryFor(identity, request.Key);
                    var completionPath = Path.Combine(entryDir, CompletionName);
                    var before = File.ReadAllBytes(completionPath);
                    var manifest = JsonSerializer.Deserialize<CompletionManifest>(before, JsonOptions);
                    if (!ManifestMatches(manifest, identity, request.Key, currentBundleKeys)) return false;
                    if (bundleReads is not null
                        && !string.Equals(bundleReads, manifest!.BundleReads, StringComparison.Ordinal))
                        return false;
                    bundleReads = manifest!.BundleReads;
                    foreach (var bundle in manifest.RequiredBundleReads) requiredReads.Add(bundle);
                    foreach (var stock in manifest.StockTextures)
                    {
                        if (stockTextures.TryGetValue(stock.DestinationFileName, out var existing)
                            && existing != stock) return false;
                        stockTextures[stock.DestinationFileName] = stock;
                    }

                    var stagedGlb = Path.Combine(staging, request.DestinationGlbName);
                    File.Copy(Path.Combine(entryDir, CachedGlbName), stagedGlb, overwrite: false);
                    if (!Matches(DescribeGlb(stagedGlb), manifest!.Glb)) return false;

                    if (manifest.Maps is not null)
                    {
                        var stagedMaps = Path.ChangeExtension(stagedGlb, ".maps.json")!;
                        File.Copy(Path.Combine(entryDir, CachedMapsName), stagedMaps, overwrite: false);
                        if (!Matches(DescribeJson(stagedMaps), manifest.Maps)) return false;
                    }

                    // A concurrent/external writer that replaced the generation while it was copied turns
                    // this into a miss even when both generations are individually valid.
                    if (!before.AsSpan().SequenceEqual(File.ReadAllBytes(completionPath))) return false;
                }

                Directory.Move(staging, destination);
                Touch(subjectDir);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (Directory.Exists(staging)) CacheReset.DeleteTree(staging);
            }
        }

        dependencies = new ServeDependencies(bundleReads!, requiredReads.ToArray(),
            stockTextures.Values.OrderBy(value => value.DestinationFileName, StringComparer.Ordinal).ToArray());
        return true;
    }

    /// <summary>Finish one subject-wide publication. Per-part stores never prune: the first completion and
    /// every sampled completion thereafter perform at most one full measurement for the whole subject.</summary>
    public void CompleteSubjectPublication(Identity identity)
    {
        if (!ValidIdentity(identity)) return;
        long publication = System.Threading.Interlocked.Increment(ref _subjectPublications);
        if ((publication - 1) % PruneSampleInterval != 0) return;
        string protectedSubject;
        try { protectedSubject = SubjectDirectoryFor(identity); }
        catch { return; }
        PruneIfNeeded(protectedSubject);
    }

    /// <summary>Describe one stock-cache PNG for a rig completion manifest, or null when the path is not a
    /// readable file or the dependency identity is malformed.</summary>
    public static StockTexture? DescribeStockTexture(string cachedPng, string bundleContentId,
        string textureName, long pathId, string destinationFileName)
    {
        if (!ValidStockIdentity(bundleContentId, textureName, destinationFileName)) return null;
        try
        {
            var file = DescribeFile(cachedPng);
            return new StockTexture(bundleContentId, textureName, pathId, destinationFileName,
                file.Length, file.Sha256);
        }
        catch { return null; }
    }

    /// <summary>Whether a stock-cache file still contains exactly the PNG bytes recorded with a rig.</summary>
    public static bool MatchesStockTexture(string cachedPng, StockTexture expected)
    {
        if (!ValidStockTexture(expected)) return false;
        try
        {
            return Matches(DescribeFile(cachedPng), new FileRecord
            {
                Length = expected.Length,
                Sha256 = expected.Sha256,
            });
        }
        catch { return false; }
    }

    /// <summary>The subject directory selected by the schema/catalog/subject/roster key. Internal for tests
    /// that corrupt an entry to prove it becomes a miss; callers do not need to know the disk shape.</summary>
    internal string SubjectDirectoryFor(Identity identity) => Path.Combine(
        _root,
        "v" + SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        HashKey(identity.CatalogVersion),
        HashKey(identity.SubjectFingerprint, identity.RosterSpecFingerprint));

    internal string ArtifactDirectoryFor(Identity identity, string artifactKey) =>
        Path.Combine(SubjectDirectoryFor(identity), HashKey(artifactKey));

    private static bool ValidIdentity(Identity identity) =>
        !string.IsNullOrWhiteSpace(identity.CatalogVersion)
        && !string.Equals(identity.CatalogVersion, GameInfo.UnknownVersion, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(identity.SubjectFingerprint)
        && !string.IsNullOrWhiteSpace(identity.RosterSpecFingerprint);

    private static bool ValidBundleReads(string reads)
    {
        const int pairLength = 32; // NameKey bundle + NameKey content, BundleReads' persisted shape
        if (string.IsNullOrEmpty(reads) || reads.Length % pairLength != 0) return false;
        string? previous = null;
        for (int i = 0; i < reads.Length; i += pairLength)
        {
            var pair = reads.Substring(i, pairLength);
            foreach (var c in pair) if (!Uri.IsHexDigit(c)) return false;
            if (previous is not null && string.CompareOrdinal(previous, pair) >= 0) return false;
            previous = pair;
        }
        return true;
    }

    private static bool ValidGlbFileName(string name)
    {
        return ValidFileName(name, ".glb");
    }

    private static bool ValidFileName(string name, string extension)
    {
        if (string.IsNullOrWhiteSpace(name)
            || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
            || !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return false;
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static bool ManifestMatches(CompletionManifest? manifest, Identity identity, string artifactKey,
        IReadOnlyDictionary<string, string> currentBundleKeys) =>
        manifest is not null
        && manifest.SchemaVersion == SchemaVersion
        && string.Equals(manifest.Purity, PurityGameSide, StringComparison.Ordinal)
        && manifest.BuildCompleted
        && !manifest.HadTransientFailures
        && !manifest.WasCanceled
        && !manifest.HadProjectAuthoredContent
        && string.Equals(manifest.CatalogVersion, identity.CatalogVersion, StringComparison.Ordinal)
        && string.Equals(manifest.SubjectFingerprint, identity.SubjectFingerprint, StringComparison.Ordinal)
        && string.Equals(manifest.RosterSpecFingerprint, identity.RosterSpecFingerprint, StringComparison.Ordinal)
        && string.Equals(manifest.ArtifactKey, artifactKey, StringComparison.Ordinal)
        && ValidBundleReads(manifest.BundleReads)
        && BundleReads.StillCurrent(currentBundleKeys, manifest.BundleReads)
        && manifest.Glb is not null
        && NormalizeRequiredReads(manifest.RequiredBundleReads) is not null
        && NormalizeStockTextures(manifest.StockTextures) is not null;

    private static bool PreparedManifestMatches(CompletionManifest? manifest, Identity identity,
        string artifactKey, IReadOnlyDictionary<string, string> currentBundleKeys) =>
        manifest is not null
        && manifest.SchemaVersion == SchemaVersion
        && string.Equals(manifest.Purity, PurityPreparedPart, StringComparison.Ordinal)
        && manifest.BuildCompleted
        && !manifest.HadTransientFailures
        && !manifest.WasCanceled
        && string.Equals(manifest.CatalogVersion, identity.CatalogVersion, StringComparison.Ordinal)
        && string.Equals(manifest.SubjectFingerprint, identity.SubjectFingerprint, StringComparison.Ordinal)
        && string.Equals(manifest.RosterSpecFingerprint, identity.RosterSpecFingerprint, StringComparison.Ordinal)
        && string.Equals(manifest.ArtifactKey, artifactKey, StringComparison.Ordinal)
        && ValidBundleReads(manifest.BundleReads)
        && BundleReads.StillCurrent(currentBundleKeys, manifest.BundleReads)
        && manifest.Glb is not null
        && NormalizePortableFiles(manifest.PortableFiles) is not null;

    private static List<PortableFile>? NormalizePortableFiles(IReadOnlyList<PortableFile>? files)
    {
        if (files is null) return null;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<PortableFile>();
        foreach (var file in files)
        {
            if (!ValidPortablePath(file.RelativePath) || file.Length < 0
                || file.Sha256 is not { Length: 64 } || !file.Sha256.All(Uri.IsHexDigit)
                || !paths.Add(file.RelativePath)) return null;
            normalized.Add(file);
        }
        return normalized;
    }

    private static bool ValidPortablePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
        try
        {
            string normalized = relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return normalized.StartsWith(".prepared-assets" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                && normalized.Split(Path.DirectorySeparatorChar).All(part => part.Length > 0 && part != "."
                    && part != "..");
        }
        catch { return false; }
    }

    private static PortableFile? DescribePortableFile(string root, string path)
    {
        try
        {
            string relative = Path.GetRelativePath(root, path);
            if (!ValidPortablePath(relative)) return null;
            var described = DescribeFile(path);
            return new PortableFile
            {
                RelativePath = relative,
                Length = described.Length,
                Sha256 = described.Sha256,
            };
        }
        catch { return null; }
    }

    private static string PortablePath(string root, string relative)
    {
        if (!ValidPortablePath(relative)) throw new InvalidDataException("Invalid prepared cache path.");
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Prepared cache path escaped its workspace.");
        return full;
    }

    private static List<string>? NormalizeRequiredReads(IReadOnlyList<string>? reads)
    {
        if (reads is null) return new List<string>();
        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var read in reads)
        {
            if (string.IsNullOrWhiteSpace(read)) return null;
            normalized.Add(read);
        }
        return normalized.ToList();
    }

    private static List<StockTexture>? NormalizeStockTextures(IReadOnlyList<StockTexture>? textures)
    {
        if (textures is null) return new List<StockTexture>();
        var byDestination = new Dictionary<string, StockTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in textures)
        {
            if (!ValidStockTexture(texture)) return null;
            if (byDestination.TryGetValue(texture.DestinationFileName, out var existing)
                && existing != texture) return null;
            byDestination[texture.DestinationFileName] = texture;
        }
        return byDestination.Values.OrderBy(value => value.DestinationFileName, StringComparer.Ordinal).ToList();
    }

    private static bool ValidStockTexture(StockTexture texture) =>
        ValidStockIdentity(texture.BundleContentId, texture.TextureName, texture.DestinationFileName)
        && texture.Length > 0
        && texture.Sha256 is { Length: 64 }
        && texture.Sha256.All(Uri.IsHexDigit);

    private static bool ValidStockIdentity(string bundleContentId, string textureName,
        string destinationFileName) =>
        !string.IsNullOrWhiteSpace(bundleContentId)
        && !string.IsNullOrWhiteSpace(textureName)
        && ValidFileName(destinationFileName, ".png");

    private static FileRecord? DescribeGlb(string path)
    {
        try
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length < 12 || stream.Length > uint.MaxValue) return null;
                Span<byte> header = stackalloc byte[12];
                stream.ReadExactly(header);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x46546C67
                    || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 2
                    || BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != stream.Length)
                    return null;
            }
            return DescribeFile(path);
        }
        catch { return null; }
    }

    private static FileRecord? DescribeJson(string path)
    {
        try
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var document = JsonDocument.Parse(stream))
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            return DescribeFile(path);
        }
        catch { return null; }
    }

    private static FileRecord DescribeFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new FileRecord
        {
            Length = stream.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
        };
    }

    private static bool Matches(FileRecord? actual, FileRecord? expected) =>
        actual is not null && expected is not null
        && actual.Length == expected.Length
        && string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal);

    private static bool Matches(FileRecord actual, PortableFile expected) =>
        actual.Length == expected.Length
        && string.Equals(actual.Sha256, expected.Sha256, StringComparison.Ordinal);

    private static string HashKey(params string[] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return Directory.Exists(path)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch { return true; }
    }

    private static void DeleteFile(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); } catch { /* inert, uniquely-named cache temp */ }
    }

    private static void Touch(string subjectDir)
    {
        try
        {
            var path = Path.Combine(subjectDir, AccessName);
            if (!File.Exists(path)) File.WriteAllBytes(path, Array.Empty<byte>());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch { /* LRU precision may never cost a hit or successful store */ }
    }

    private void PruneIfNeeded(string protectedSubject)
    {
        try
        {
            var schemaDir = Path.Combine(_root,
                "v" + SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!Directory.Exists(schemaDir)) return;
            System.Threading.Interlocked.Increment(ref _fullTreeEnumerations);
            var subjects = new List<SubjectSize>();
            long total = 0;
            foreach (var catalogDir in Directory.EnumerateDirectories(schemaDir))
            {
                if (IsReparsePoint(catalogDir)) continue;
                foreach (var subjectDir in Directory.EnumerateDirectories(catalogDir))
                {
                    if (IsReparsePoint(subjectDir)) continue;
                    var bytes = TreeSize(subjectDir);
                    total = SaturatingAdd(total, bytes);
                    subjects.Add(new SubjectSize(subjectDir, bytes, AccessTime(subjectDir)));
                }
            }
            if (total <= _highWaterBytes) return;

            subjects.Sort(static (a, b) =>
            {
                int byTime = a.AccessUtc.CompareTo(b.AccessUtc);
                return byTime != 0 ? byTime : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path);
            });
            foreach (var subject in subjects)
            {
                if (total <= _pruneTargetBytes) break;
                if (string.Equals(subject.Path, protectedSubject, StringComparison.OrdinalIgnoreCase)) continue;
                var gate = _subjectGates.GetOrAdd(subject.Path, static _ => new object());
                if (!System.Threading.Monitor.TryEnter(gate)) continue;
                try
                {
                    long before = TreeSize(subject.Path);
                    CacheReset.DeleteTree(subject.Path);
                    long after = TreeSize(subject.Path);
                    total -= Math.Max(0, before - after);
                }
                finally { System.Threading.Monitor.Exit(gate); }
            }
        }
        catch { /* eviction is best-effort; serving remains content-validated */ }
    }

    private static long TreeSize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir) || IsReparsePoint(dir)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try { total = SaturatingAdd(total, new FileInfo(file).Length); }
                catch { /* a racing temp vanished */ }
            }
            foreach (var child in Directory.EnumerateDirectories(dir))
                total = SaturatingAdd(total, TreeSize(child));
            return total;
        }
        catch { return 0; }
    }

    private static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private static DateTime AccessTime(string subjectDir)
    {
        try
        {
            var marker = Path.Combine(subjectDir, AccessName);
            return File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : Directory.GetLastWriteTimeUtc(subjectDir);
        }
        catch { return DateTime.MinValue; }
    }

    private sealed record SubjectSize(string Path, long Bytes, DateTime AccessUtc);

    private sealed class CompletionManifest
    {
        public int SchemaVersion { get; set; }
        public string Purity { get; set; } = "";
        public bool BuildCompleted { get; set; }
        public bool HadTransientFailures { get; set; }
        public bool WasCanceled { get; set; }
        public bool HadProjectAuthoredContent { get; set; }
        public string CatalogVersion { get; set; } = "";
        public string SubjectFingerprint { get; set; } = "";
        public string RosterSpecFingerprint { get; set; } = "";
        public string BundleReads { get; set; } = "";
        public string ArtifactKey { get; set; } = "";
        public FileRecord? Glb { get; set; }
        public FileRecord? Maps { get; set; }
        public List<string> RequiredBundleReads { get; set; } = new();
        public List<StockTexture> StockTextures { get; set; } = new();
        public List<PortableFile> PortableFiles { get; set; } = new();
    }

    private sealed class FileRecord
    {
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
    }

    private sealed class PortableFile
    {
        public string RelativePath { get; set; } = "";
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
    }
}
