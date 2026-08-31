using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Model;

namespace Remold.Core.Workbench;

/// <summary>
/// The persisted result of the launch roster confirm-fill: KEPT outfit ModelConfigId → Pick part tokens.
/// Keyed to the asset-catalog VERSION alone (the <c>N</c> in <c>catalog_main_&lt;N&gt;.bin</c>), so it
/// re-derives on a game update but NOT when a mod operation rewrites the catalog bytes within a version.
/// With a valid snapshot the launch does no bundle reads. Regenerable cache: unreadable/stale ⇒ null ⇒
/// refill; a failed save is non-fatal.
///
/// <para><b>Absence means "Pick doesn't show it".</b> An outfit the fill dropped is simply not written and
/// a missing id loads as not-confirmed, which is what carries the drop across launches — the snapshot
/// branch does no prefab reads and could not re-derive it. The key is the ModelConfigId, not the stem,
/// because two ModelConfig rows can name the SAME stem and a stem-keyed entry would hand a dropped row its
/// twin's confirmation back.</para>
///
/// <para><b>Everything the fill's result depends on is in the key.</b> Absence-means-dropped is only safe
/// while the snapshot and the running build agree on which subjects were offered to the fill. Two inputs
/// decide that: the catalog (the version) and the CURATED table, which is code and moves independently of
/// any game update. Both are checked, so a subject that did not exist when the file was written makes the
/// load MISS instead of being answered "not confirmed" for as long as the catalog holds still.</para>
/// </summary>
public static class RosterSnapshot
{
    // Bump on any change to what the fill KEEPS or how a part token is derived — the stored lists would
    // otherwise be answered as if the current rules had produced them, and a subject POPULATION the file
    // predates (the weapon rosters) would read as dropped rather than unfilled. The curated set is keyed
    // separately (CuratedSet), so adding or re-routing an entry there needs no bump.
    private const int LegacySchema = 8;
    private const int Schema = 9;

    private sealed record CacheFile(int SchemaVersion, string CatalogVersion, string? CuratedSet,
        Dictionary<long, List<string>> PartsByModelConfigId);

    private sealed record RowCacheFile(int SchemaVersion, string CatalogVersion, string? CuratedSet,
        List<Row> Rows);

    /// <summary>One candidate outfit's clean fill result. Null parts means the fill examined and dropped
    /// the outfit; a missing row means it was not examined. Fingerprint guards catalog shape and Reads
    /// guards the content of every bundle the candidate walk opened.</summary>
    public sealed record Row(long ModelConfigId, string Fingerprint, string Reads, List<string>? Parts)
    {
        public bool Confirmed => Parts is not null;
    }

    /// <summary>Identity of the curated subject set this snapshot was filled over: id, stem and route of
    /// every <see cref="Tables.CuratedSkins"/> entry, hashed. Computed here rather than passed in, so no
    /// caller can forget it.</summary>
    internal static string CuratedSet()
    {
        var sb = new StringBuilder();
        foreach (var e in Tables.CuratedSkins.All)
            sb.Append(e.ModelConfigId).Append('|').Append(e.Stem).Append('|')
              .Append(e.Route?.Address).Append('|').Append(e.Route?.Bundle).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    /// <summary>The confirmed ModelConfigId → part-token map iff the snapshot was built for
    /// <paramref name="catalogVersion"/>, the current schema and the current curated set.</summary>
    public static IReadOnlyDictionary<long, List<string>>? TryLoad(string path, string catalogVersion)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var cf = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (cf is null || cf.SchemaVersion != LegacySchema || cf.CatalogVersion != catalogVersion
                || cf.CuratedSet != CuratedSet())
                return null;
            return cf.PartsByModelConfigId;
        }
        catch { return null; }
    }

    public static void Save(string path, string catalogVersion,
        IReadOnlyDictionary<long, List<string>> partsByModelConfigId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Atomic publish through a UNIQUE temp: a fixed name is one shared file, so two saves racing over
        // it publish a half-written snapshot.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheFile(LegacySchema, catalogVersion, CuratedSet(),
                new Dictionary<long, List<string>>(partsByModelConfigId))));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }

    /// <summary>Create the row a successful per-outfit fill writes. The caller passes the scope bundles
    /// actually walked and null parts for a cleanly rejected candidate.</summary>
    public static Row CreateRow(CatalogIndex catalog, Func<string, string?> contentHashOf,
        Outfit outfit, IEnumerable<string> readBundles, IReadOnlyList<string>? parts)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(contentHashOf);
        ArgumentNullException.ThrowIfNull(outfit);
        return new Row(outfit.ModelConfigId, SubjectFingerprint.For(catalog, outfit),
            BundleReads.Of(catalog, contentHashOf, readBundles),
            parts is null ? null : new List<string>(parts));
    }

    /// <summary>Load reusable rows from the newest local snapshots, including prior catalog versions.
    /// Each row is independently checked against the current catalog shape and bundle contents; stale or
    /// corrupt rows are misses and are left for the launch fill.</summary>
    public static IReadOnlyDictionary<long, Row> LoadReusable(string currentPath, CatalogIndex catalog,
        Func<string, string?> contentHashOf, IEnumerable<Outfit> candidates)
    {
        var byId = candidates.GroupBy(outfit => outfit.ModelConfigId)
            .ToDictionary(group => group.Key, group => group.First());
        if (byId.Count == 0) return new Dictionary<long, Row>();
        var currentKeys = BundleReads.CurrentKeys(catalog, contentHashOf);
        var reusable = new Dictionary<long, Row>();
        foreach (string path in SnapshotPaths(currentPath))
        {
            RowCacheFile? file;
            try
            {
                file = JsonSerializer.Deserialize<RowCacheFile>(File.ReadAllText(path));
                if (file is null || file.SchemaVersion != Schema || file.CuratedSet != CuratedSet()
                    || file.Rows is null) continue;
            }
            catch { continue; }

            foreach (var row in file.Rows)
            {
                if (reusable.ContainsKey(row.ModelConfigId)
                    || !byId.TryGetValue(row.ModelConfigId, out var outfit)
                    || string.IsNullOrEmpty(row.Reads)
                    || row.Fingerprint != SubjectFingerprint.For(catalog, outfit)
                    || !BundleReads.StillCurrent(currentKeys, row.Reads))
                    continue;
                reusable[row.ModelConfigId] = row;
            }
            if (reusable.Count == byId.Count) break;
        }
        return reusable;
    }

    /// <summary>Atomically publish the complete candidate population for this launch. Both confirmed and
    /// cleanly dropped rows are required; a partial/error fill is never passed here.</summary>
    public static void SaveRows(string path, string catalogVersion, IEnumerable<Row> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var ordered = rows.OrderBy(row => row.ModelConfigId).ToList();
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new RowCacheFile(Schema, catalogVersion, CuratedSet(), ordered)));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) try { File.Delete(tmp); } catch { }
        }
    }

    private static IEnumerable<string> SnapshotPaths(string currentPath)
    {
        string fullCurrent = Path.GetFullPath(currentPath);
        if (File.Exists(fullCurrent)) yield return fullCurrent;
        string directory = Path.GetDirectoryName(fullCurrent)!;
        if (!Directory.Exists(directory)) yield break;
        string[] prior;
        try
        {
            prior = Directory.EnumerateFiles(directory, "roster_*.json")
                .Where(path => !string.Equals(Path.GetFullPath(path), fullCurrent,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { yield break; }
        foreach (string path in prior) yield return path;
    }
}
