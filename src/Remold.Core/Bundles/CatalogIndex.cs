using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AddressablesTools;
using AddressablesTools.Catalog;
using AddressablesTools.Classes;

namespace Remold.Core.Bundles;

/// <summary>
/// The game's Addressables binary catalog (<c>catalog_main_*.bin</c>), reduced to: <b>which logical
/// bundle owns an address, and which object in it the address loads</b>. The corpus alone cannot answer
/// either — the same asset name ships as distinct copies across bundles and inside one, and only the
/// catalog says which one the game loads. Read-only, in-memory,
/// parsed once per session; an absent catalog returns null and callers degrade with a note.
/// </summary>
public sealed class CatalogIndex
{
    private readonly Dictionary<string, string> _keyToOwner;   // dash-hex primaryKey → logical bundle id
    // dash-hex primaryKey → the row's FULL ordered dependency list (owner first)
    private readonly Dictionary<string, string[]> _keyToDeps;
    // dash-hex primaryKey → the key the owning bundle's m_Container files the asset under
    private readonly Dictionary<string, string> _keyToLoadKey;

    public int AssetCount { get; }

    /// <summary>Logical bundle ids named by the catalog's bundle rows. Includes ids outside the managed
    /// bundle dir, so membership here does not imply on-disk presence.</summary>
    public int BundleCount { get; }

    /// <summary>Logical bundle id (corpus identity) → manifest <b>internalId</b> basename (what
    /// <see cref="GffManifest"/> stores) — different hash namespaces, and this map is their one join.
    /// Ambiguously-mapped names go to <see cref="ConflictedBundleNames"/> instead.</summary>
    public IReadOnlyDictionary<string, string> BundleNameToInternalId { get; }

    /// <summary>Logical bundle ids the catalog maps to MORE than one internalId: excluded from
    /// <see cref="BundleNameToInternalId"/> because a locate can't pick one.</summary>
    public IReadOnlyList<string> ConflictedBundleNames { get; }

    private CatalogIndex(Dictionary<string, string> keyToOwner, int bundleCount,
        Dictionary<string, string>? nameToInternalId = null, IReadOnlyList<string>? conflicted = null,
        Dictionary<string, string[]>? keyToDeps = null, Dictionary<string, string>? keyToLoadKey = null)
    {
        _keyToOwner = keyToOwner;
        _keyToDeps = keyToDeps ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
        _keyToLoadKey = keyToLoadKey ?? new Dictionary<string, string>(StringComparer.Ordinal);
        AssetCount = keyToOwner.Count;
        BundleCount = bundleCount;
        BundleNameToInternalId = nameToInternalId ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ConflictedBundleNames = conflicted ?? System.Array.Empty<string>();
    }

    /// <summary>The catalog primary key: <c>MD5(UTF-16-LE(address))</c>, uppercase dash-hex, with
    /// <c>\&lt;filename&gt;</c> appended for <c>.unity</c> scene addresses.</summary>
    public static string KeyForAddress(string address)
    {
        var digest = MD5.HashData(Encoding.Unicode.GetBytes(address));
        var key = BitConverter.ToString(digest);
        return address.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
            ? key + "\\" + address[(address.LastIndexOf('/') + 1)..]
            : key;
    }

    /// <summary>The logical bundle id owning <paramref name="address"/> (the first AssetBundleProvider
    /// dependency of its row), or null when the catalog has no such address. The id may still be absent
    /// from THIS install's corpus — dead catalog references exist — so membership is the caller's
    /// check.</summary>
    public string? ResolveAddress(string address) =>
        _keyToOwner.TryGetValue(KeyForAddress(address), out var owner) ? owner : null;

    /// <summary>The catalog's own address key to logical-owner rows. Internal so reuse records can compare
    /// a persisted keyed resolution without retaining or reconstructing the game-derived address.</summary>
    internal IReadOnlyDictionary<string, string> AddressOwners => _keyToOwner;

    /// <summary>The full ordered dependency list of <paramref name="address"/>'s row (owner first) — the
    /// game's load set, and the resolution scope for everything the asset references.</summary>
    public IReadOnlyList<string>? DepsForAddress(string address) =>
        _keyToDeps.TryGetValue(KeyForAddress(address), out var deps) ? deps : null;

    /// <summary>The key <paramref name="address"/>'s owning bundle files the asset under in its
    /// <c>m_Container</c> — the row's own internal id, which is what the game hands the bundle to load it.
    /// A bundle can ship several objects of one name; this key names the one the address loads. Null when
    /// the catalog has no such address, or its row carries no internal id.</summary>
    public string? LoadKeyForAddress(string address) =>
        _keyToLoadKey.TryGetValue(KeyForAddress(address), out var key) ? key : null;

    /// <summary>The catalog's own address key to load-key rows, beside <see cref="AddressOwners"/>.</summary>
    internal IReadOnlyDictionary<string, string> AddressLoadKeys => _keyToLoadKey;

    /// <summary>Where one part tier's mesh is read, and which object of that bundle: the serialized renderer
    /// mesh it carries where it names both halves of one; else its recipe address's owning bundle and load
    /// key; else whatever bundle it names, read by name. Catalog-only — nothing is opened to answer, so a
    /// route asks it wherever it stands and reads the bundle only where it already did.</summary>
    public (string? Bundle, MeshSelector Which) TierMesh(string? address, string? serializedBundle,
        long serializedPathId) =>
        TierMesh(address, serializedBundle, serializedPathId, ResolveAddress, LoadKeyForAddress);

    /// <summary><see cref="TierMesh(string?, string?, long)"/> over any catalog's two address answers, for a
    /// caller that holds them as functions (a build environment).</summary>
    public static (string? Bundle, MeshSelector Which) TierMesh(string? address, string? serializedBundle,
        long serializedPathId, Func<string, string?> ownerOf, Func<string, string?>? loadKeyOf)
    {
        if (!string.IsNullOrEmpty(serializedBundle) && serializedPathId != 0)
            return (serializedBundle, serializedPathId);
        if (!string.IsNullOrEmpty(address) && ownerOf(address) is { } owner)
            return (owner, MeshSelector.ByLoadKey(loadKeyOf?.Invoke(address)));
        return (string.IsNullOrEmpty(serializedBundle) ? null : serializedBundle, default);
    }

    /// <summary>The load dependencies of every asset <paramref name="bundle"/> owns, deduped in catalog
    /// order, the bundle itself excluded: the closure a route that names a bundle rather than an address
    /// reaches. One pass over the catalog.</summary>
    public IReadOnlyList<string> DepsForBundle(string bundle)
    {
        var deps = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { bundle };
        foreach (var (key, owner) in _keyToOwner)
            if (string.Equals(owner, bundle, StringComparison.Ordinal) && _keyToDeps.TryGetValue(key, out var row))
                foreach (var dep in row)
                    if (seen.Add(dep)) deps.Add(dep);
        return deps;
    }

    /// <summary>Parse the game's current catalog, or null when no catalog file exists. A
    /// present-but-corrupt catalog THROWS rather than degrading silently.</summary>
    public static CatalogIndex? TryLoad(string anyGamePath)
    {
        var path = GameInfo.CatalogPath(anyGamePath);
        if (path is null) return null;
        return Parse(File.ReadAllBytes(path));
    }

    private static readonly object CacheLock = new();
    private static (string Path, DateTime Stamp, CatalogIndex Index)? _cached;

    /// <summary>Like <see cref="TryLoad"/> but memoized on (catalog path, last-write time) and backed by
    /// the on-disk snapshot — the full parse costs seconds on the launch path. The snapshot is keyed to
    /// the catalog's length + mtime; a stale or unreadable one silently reparses.</summary>
    public static CatalogIndex? LoadCached(string anyGamePath)
    {
        var path = GameInfo.CatalogPath(anyGamePath);
        if (path is null) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (CacheLock)
        {
            if (_cached is { } c && c.Path == path && c.Stamp == stamp) return c.Index;
            var fi = new FileInfo(path);
            var snapPath = LabPaths.CatalogSnapshotFile(GameInfo.CatalogVersion(anyGamePath));
            var idx = TryLoadSnapshot(snapPath, fi.Length, stamp.Ticks);
            if (idx is null)
            {
                idx = Parse(File.ReadAllBytes(path));
                try { idx.SaveSnapshot(snapPath, fi.Length, stamp.Ticks); }
                catch { /* cache-only; the parse result stands and next launch just reparses */ }
            }
            _cached = (path, stamp, idx);
            return idx;
        }
    }

    // ---- the parsed-catalog snapshot ------------------------------------------------------------
    // Binary re-encoding of the parse result; ids repeat across dep lists, so they serialize as indexes
    // into one string table. A schema bump forces a reparse — regenerable cache, not a format contract.

    private const uint SnapshotMagic = 0x50414E53;   // "SNAP"
    private const byte SnapshotSchema = 2;

    private void SaveSnapshot(string path, long catalogLength, long catalogMtimeTicks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // string table: every bundle id / internalId referenced anywhere
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        int Idx(string s) { if (!table.TryGetValue(s, out var i)) table[s] = i = table.Count; return i; }
        foreach (var deps in _keyToDeps.Values) foreach (var d in deps) Idx(d);
        foreach (var kv in BundleNameToInternalId) { Idx(kv.Key); Idx(kv.Value); }
        foreach (var cName in ConflictedBundleNames) Idx(cName);

        // Atomic publish through a UNIQUE temp: a fixed name is one shared file, so two saves racing over
        // it publish a half-written snapshot.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var w = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 20), Encoding.UTF8))
            {
                w.Write(SnapshotMagic); w.Write(SnapshotSchema);
                w.Write(catalogLength); w.Write(catalogMtimeTicks);
                w.Write(BundleCount);
                var strings = new string[table.Count];
                foreach (var (s, i) in table) strings[i] = s;
                w.Write(strings.Length);
                foreach (var s in strings) w.Write(s);
                w.Write(BundleNameToInternalId.Count);
                foreach (var kv in BundleNameToInternalId) { w.Write(table[kv.Key]); w.Write(table[kv.Value]); }
                w.Write(ConflictedBundleNames.Count);
                foreach (var cName in ConflictedBundleNames) w.Write(table[cName]);
                w.Write(_keyToDeps.Count);
                foreach (var (key, deps) in _keyToDeps)
                {
                    w.Write(key);
                    w.Write(deps.Length);
                    foreach (var d in deps) w.Write(table[d]);
                    w.Write(_keyToLoadKey.TryGetValue(key, out var loadKey) ? loadKey : "");
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }

    /// <summary>The snapshot iff it matches the live catalog's (length, mtime); null (⇒ reparse) on
    /// any mismatch or read/format problem — silent by design, it is regenerable cache.</summary>
    private static CatalogIndex? TryLoadSnapshot(string path, long catalogLength, long catalogMtimeTicks)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20), Encoding.UTF8);
            if (r.ReadUInt32() != SnapshotMagic || r.ReadByte() != SnapshotSchema) return null;
            if (r.ReadInt64() != catalogLength || r.ReadInt64() != catalogMtimeTicks) return null;
            int bundleCount = r.ReadInt32();
            var strings = new string[r.ReadInt32()];
            for (int i = 0; i < strings.Length; i++) strings[i] = r.ReadString();
            int joinCount = r.ReadInt32();
            var nameToInternalId = new Dictionary<string, string>(joinCount, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < joinCount; i++)
            { var k = strings[r.ReadInt32()]; nameToInternalId[k] = strings[r.ReadInt32()]; }
            int conflictedCount = r.ReadInt32();
            var conflicted = new List<string>(conflictedCount);
            for (int i = 0; i < conflictedCount; i++) conflicted.Add(strings[r.ReadInt32()]);
            int keyCount = r.ReadInt32();
            var keyToOwner = new Dictionary<string, string>(keyCount, StringComparer.Ordinal);
            var keyToDeps = new Dictionary<string, string[]>(keyCount, StringComparer.Ordinal);
            var keyToLoadKey = new Dictionary<string, string>(keyCount, StringComparer.Ordinal);
            for (int i = 0; i < keyCount; i++)
            {
                var key = r.ReadString();
                var deps = new string[r.ReadInt32()];
                for (int j = 0; j < deps.Length; j++) deps[j] = strings[r.ReadInt32()];
                keyToOwner[key] = deps[0];
                keyToDeps[key] = deps;
                var loadKey = r.ReadString();
                if (loadKey.Length > 0) keyToLoadKey[key] = loadKey;
            }
            return new CatalogIndex(keyToOwner, bundleCount, nameToInternalId, conflicted, keyToDeps,
                keyToLoadKey);
        }
        catch { return null; }
    }

    /// <summary>Build an index directly from address→owning-logical rows, keyed via
    /// <see cref="KeyForAddress"/>. Tests only — the real catalog is a binary blob no fixture can
    /// synthesise.</summary>
    internal static CatalogIndex ForTest(IEnumerable<(string Address, string OwnerBundle)> rows,
        IEnumerable<(string Address, string[] Deps)>? depRows = null,
        IEnumerable<(string Logical, string InternalId)>? bundleRows = null,
        IEnumerable<(string Address, string LoadKey)>? loadKeyRows = null)
    {
        var keyToOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var keyToDeps = new Dictionary<string, string[]>(StringComparer.Ordinal);
        // The fixture convention: an address loads the object its last path segment names, so a synthetic
        // mesh filed under its own name answers the address a fixture spells for it. Explicit rows override.
        var keyToLoadKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (address, _) in rows)
            keyToLoadKey[KeyForAddress(address)] = System.IO.Path.GetFileNameWithoutExtension(address);
        foreach (var (address, loadKey) in loadKeyRows ?? Array.Empty<(string, string)>())
            keyToLoadKey[KeyForAddress(address)] = loadKey;
        int bundles = 0;
        var seenBundles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (address, owner) in rows)
        {
            keyToOwner[KeyForAddress(address)] = owner;
            keyToDeps[KeyForAddress(address)] = new[] { owner };
            if (seenBundles.Add(owner)) bundles++;
        }
        foreach (var (address, deps) in depRows ?? Array.Empty<(string, string[])>())
            keyToDeps[KeyForAddress(address)] = deps;
        // the bundle-name join (logical → manifest internalId basename) for forward-locate fixtures
        Dictionary<string, string>? nameToInternalId = null;
        if (bundleRows is not null)
        {
            nameToInternalId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (logical, internalId) in bundleRows) nameToInternalId[logical] = internalId;
        }
        return new CatalogIndex(keyToOwner, bundles, nameToInternalId, keyToDeps: keyToDeps,
            keyToLoadKey: keyToLoadKey);
    }

    /// <summary>Parse catalog bytes (split out for tests and offline runs).</summary>
    public static CatalogIndex Parse(byte[] catalogBytes)
    {
        var ccd = AddressablesCatalogFileParser.FromBinaryData(catalogBytes);

        // allocation-free provider test; a Split-based compare is far slower across both passes
        static bool IsBundleProvider(string? provider) =>
            provider is not null && provider.EndsWith("AssetBundleProvider", StringComparison.Ordinal);

        // pass 1 — bundle rows: internalId → logical bundle id (BundleName + ".bundle", the corpus
        // identity). The reverse join keeps the internalId BASENAME (internalIds carry a leading "\"),
        // which is what the GFF manifest stores; ambiguously-mapped names are dropped from it.
        var internalToLogical = new Dictionary<string, string>(StringComparer.Ordinal);
        var nameToInternalId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in ccd.Resources)
            foreach (var loc in kv.Value)
            {
                if (!IsBundleProvider(loc.ProviderId)) continue;
                if (loc.InternalId is null ||
                    loc.Data is not WrappedSerializedObject { Object: AssetBundleRequestOptions abro } ||
                    string.IsNullOrEmpty(abro.BundleName)) continue;
                var logical = abro.BundleName.EndsWith(".bundle", StringComparison.Ordinal)
                    ? abro.BundleName : abro.BundleName + ".bundle";
                internalToLogical[loc.InternalId] = logical;
                var internalName = System.IO.Path.GetFileName(loc.InternalId);
                if (nameToInternalId.TryGetValue(logical, out var existing))
                {
                    if (!string.Equals(existing, internalName, StringComparison.OrdinalIgnoreCase))
                        conflicted.Add(logical);
                }
                else nameToInternalId[logical] = internalName;
            }
        foreach (var c in conflicted) nameToInternalId.Remove(c);

        // pass 2 — asset rows: primaryKey → owning logical (first AssetBundleProvider dependency) plus
        // the row's full bundle dep list
        var keyToOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        var keyToDeps = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var keyToLoadKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var depList = new List<string>();
        var depSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in ccd.Resources)
            foreach (var loc in kv.Value)
            {
                if (IsBundleProvider(loc.ProviderId)) continue;
                if (loc.PrimaryKey is null || keyToOwner.ContainsKey(loc.PrimaryKey)) continue;   // first location wins, like the game
                var deps = loc.Dependencies;
                if (deps is null && loc.DependencyKey is not null &&
                    ccd.Resources.TryGetValue(loc.DependencyKey, out var dl))
                    deps = dl;
                if (deps is null) continue;
                depList.Clear(); depSeen.Clear();
                foreach (var d in deps)
                    if (IsBundleProvider(d.ProviderId) && d.InternalId is not null &&
                        internalToLogical.TryGetValue(d.InternalId, out var logical) && depSeen.Add(logical))
                        depList.Add(logical);
                if (depList.Count == 0) continue;
                keyToOwner.Add(loc.PrimaryKey, depList[0]);
                keyToDeps.Add(loc.PrimaryKey, depList.ToArray());
                if (!string.IsNullOrEmpty(loc.InternalId)) keyToLoadKey.Add(loc.PrimaryKey, loc.InternalId);
            }

        return new CatalogIndex(keyToOwner, internalToLogical.Count, nameToInternalId,
            new List<string>(conflicted), keyToDeps, keyToLoadKey);
    }
}
