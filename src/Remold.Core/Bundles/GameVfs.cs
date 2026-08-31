using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Remold.Core.Bundles;

/// <summary>
/// The forward view of the game install — catalog + GFF manifest, no corpus scan:
/// <list type="number">
/// <item>Where a logical bundle lives: catalog name→internalId join + manifest stub
/// (<see cref="Locate"/>/<see cref="TryDeobfuscateLogical"/>).</item>
/// <item>Where an outfit's assembly prefab lives: the game's own address formula
/// (<see cref="PrefabAddress"/>). Each context is its own catalog row and bundle copy, and the catalog
/// pins the copies the game can load, so catalog-dead duplicates never surface.</item>
/// <item>What an asset may reference: its catalog row's dependency list
/// (<see cref="CatalogIndex.DepsForAddress"/>).</item>
/// </list>
/// One instance per session per game dir — the manifest read is heavy and never repeated. Read-only.
/// </summary>
public sealed class GameVfs
{
    /// <summary>Where a logical bundle physically lives: on-disk file (basename, no <c>.bundle</c>) and
    /// the segment's byte range. <see cref="WholeFile"/> = the segment IS the whole file.</summary>
    public readonly record struct SegmentRef(string PhysicalHash, long Offset, long Size, bool WholeFile);

    /// <summary>A prefab copy the formula resolved: context root, address, owning logical bundle.</summary>
    public sealed record PrefabHit(string ContextRoot, string Address, string Bundle);

    /// <summary>Context roots of the model-prefab address formula, in resolution priority order:
    /// combat player first (the copy outfit mods target), then barrack and summon (dorm and summon
    /// stems resolve ONLY there), then the rest.</summary>
    public static readonly IReadOnlyList<string> ContextRoots = new[]
    {
        "Character/Player", "BarrackModel/Character", "Character/Summon", "Character/Enemy",
        "Character/NPC", "CommandCenter/Character",
    };

    public string CatalogVersion { get; }
    /// <summary>Identity of the exact catalog source this view parsed. Path separates game installs;
    /// length and mtime are the same source guards used by the parsed-catalog snapshot.</summary>
    public string CatalogIdentity { get; }
    /// <summary>Stable identity of the game install containing this view. Unlike the catalog identity it
    /// survives game updates, so a prior-catalog local measurement can prove it came from this install.</summary>
    public string InstallIdentity { get; }
    public CatalogIndex Catalog { get; }
    public GffManifest Manifest { get; }

    private readonly string _bundleDir;

    private GameVfs(string bundleDir, string catalogVersion, string catalogIdentity,
        CatalogIndex catalog, GffManifest manifest)
    {
        _bundleDir = bundleDir;
        CatalogVersion = catalogVersion;
        CatalogIdentity = catalogIdentity;
        InstallIdentity = Workbench.NameKey.Of(Path.GetFullPath(bundleDir).ToLowerInvariant());
        Catalog = catalog;
        Manifest = manifest;
    }

    /// <summary>Parse the catalog (memoized per session) and read the GFF manifest. Null when either is
    /// absent (mid-update, or not a game dir); present-but-corrupt THROWS.</summary>
    public static GameVfs? TryLoad(string anyGamePath)
    {
        var catalog = CatalogIndex.LoadCached(anyGamePath);
        if (catalog is null) return null;
        var bundleDir = GameInfo.BundleDir(anyGamePath);
        var manifestPath = GffManifest.PathIn(bundleDir);
        if (manifestPath is null) return null;
        string catalogVersion = GameInfo.CatalogVersion(anyGamePath);
        string catalogPath = GameInfo.CatalogPath(anyGamePath)!;
        var catalogFile = new FileInfo(catalogPath);
        string catalogIdentity = string.Join("|", Path.GetFullPath(catalogPath),
            catalogFile.Length, catalogFile.LastWriteTimeUtc.Ticks);
        return new GameVfs(bundleDir, catalogVersion, catalogIdentity, catalog,
            GffManifest.LoadCached(manifestPath, LabPaths.GffManifestSnapshotFile(catalogVersion)));
    }

    /// <summary>Assemble a forward view from already-built pieces. Tests only — pairs a hand-authored
    /// <see cref="CatalogIndex.ForTest"/> with a synthetic manifest so locate/deobfuscate run the REAL
    /// forward mechanics.</summary>
    internal static GameVfs ForTest(string bundleDir, string catalogVersion, CatalogIndex catalog, GffManifest manifest) =>
        new(bundleDir, catalogVersion, "test|" + catalogVersion, catalog, manifest);

    /// <summary>Install-completeness check (no content reads): the physical files the manifest
    /// addresses that are missing on disk, empty = healthy. Non-empty means a mid-update or incomplete
    /// install — surface it rather than reading resolution misses as absent mod targets.</summary>
    public IReadOnlyList<string> MissingPhysicalFiles()
    {
        var present = new HashSet<string>(
            Directory.EnumerateFiles(_bundleDir, "*.bundle").Select(Path.GetFileNameWithoutExtension)!,
            StringComparer.OrdinalIgnoreCase);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var physicalHash in Manifest.PhysicalHashes)
            if (!present.Contains(physicalHash)) missing.Add(physicalHash);
        return missing.ToList();
    }

    /// <summary>Where a logical bundle physically lives, or null when the catalog doesn't name it or
    /// the manifest has no entry for its internalId (a catalog-dead bundle, or not this install's).</summary>
    public SegmentRef? Locate(string logical)
    {
        if (!Catalog.BundleNameToInternalId.TryGetValue(logical, out var internalId)) return null;
        if (!Manifest.TryLocate(internalId, out var located)) return null;
        var stub = located.Stub;
        if (stub.Size != 0)
        {
            // an explicit-size stub at offset 0 can still span its whole file, judged by the file's
            // length; a non-zero offset never can, so it needs no stat
            bool whole = false;
            if (stub.Offset == 0)
            {
                var p = Path.Combine(_bundleDir, stub.PhysHash + ".bundle");
                whole = File.Exists(p) && new FileInfo(p).Length == stub.Size;
            }
            return new SegmentRef(stub.PhysHash, stub.Offset, stub.Size, whole);
        }
        // whole-file bundle: the stub carries no size; the file's own length is the segment size
        var path = Path.Combine(_bundleDir, stub.PhysHash + ".bundle");
        if (!File.Exists(path)) return null;
        return new SegmentRef(stub.PhysHash, 0, new FileInfo(path).Length, WholeFile: true);
    }

    /// <summary>Read a logical bundle's plain (deobfuscated) bytes via the forward locate — the
    /// "referenced but absent ⇒ null" contract callers rely on.</summary>
    public byte[]? TryDeobfuscateLogical(string logical)
    {
        if (Locate(logical) is not { } seg) return null;
        var path = Path.Combine(_bundleDir, seg.PhysicalHash + ".bundle");
        if (!File.Exists(path)) return null;
        try { return BundleSegments.ReadSegmentPlain(path, seg.Offset, seg.Size); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>The game's address for a model prefab in one context.</summary>
    public static string PrefabAddress(string contextRoot, string stem) =>
        $"Assets/ConfigPrefab/{contextRoot}/{stem}/{stem}.prefab";

    /// <summary>Every prefab copy the formula resolves for a stem, in context priority order; empty
    /// when none resolves (a DB row with no model, or a mid-update install). Blacklisted stems resolve
    /// to nothing, ever — <see cref="RosterBlacklist"/> is enforced at this one surface, which the
    /// roster, the workbench and every prefab locate all pass through.</summary>
    public IReadOnlyList<PrefabHit> PrefabsFor(string stem)
    {
        if (RosterBlacklist.IsBlacklisted(stem)) return Array.Empty<PrefabHit>();
        var hits = new List<PrefabHit>();
        foreach (var root in ContextRoots)
        {
            var address = PrefabAddress(root, stem);
            if (Catalog.ResolveAddress(address) is { } owner)
                hits.Add(new PrefabHit(root, address, owner));
        }
        return hits;
    }

    /// <summary><see cref="PrefabHit.ContextRoot"/> for a curated route, which resolves under no context
    /// root of the address formula.</summary>
    public const string CuratedContextRoot = "";

    /// <summary>Every prefab copy that exists for an OUTFIT: its curated <see cref="Model.SubjectRoute"/>
    /// when it declares one, else the stem formula (<see cref="PrefabsFor(string)"/>). This is the twin of
    /// <see cref="Workbench.SubjectScope.Build"/> — the cheap existence gate the launch runs before the
    /// scope does the reading — so the two must agree about what a route reaches, and both check
    /// <see cref="RosterBlacklist"/> on the STEM first. Catalog and manifest dictionary hits only: no
    /// bundle content is read, so this stays on the launch path.</summary>
    public IReadOnlyList<PrefabHit> PrefabsFor(Model.Outfit outfit)
    {
        if (RosterBlacklist.IsBlacklisted(outfit.Stem)) return Array.Empty<PrefabHit>();
        if (outfit.Route is not { } route) return PrefabsFor(outfit.Stem);
        if (route.Address is { } address)
            return Catalog.ResolveAddress(address) is { } owner
                ? new[] { new PrefabHit(CuratedContextRoot, address, owner) }
                : Array.Empty<PrefabHit>();
        // Direct bundle: the catalog NAMING the bundle, which is the exact analogue of the formula path's
        // "the catalog names this address" — a dictionary hit, no manifest walk and no disk stat, so the
        // launch gate keeps its no-file-reads property. Like a formula hit, this does not promise the
        // bundle is in THIS install's corpus; the confirm-fill is what settles that by reading.
        return route.Bundle is { } bundle && Catalog.BundleNameToInternalId.ContainsKey(bundle)
            ? new[] { new PrefabHit(CuratedContextRoot, "", bundle) }
            : Array.Empty<PrefabHit>();
    }
}
