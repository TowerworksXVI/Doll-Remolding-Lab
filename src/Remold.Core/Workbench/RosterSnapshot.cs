using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    private const int Schema = 8;

    private sealed record CacheFile(int SchemaVersion, string CatalogVersion, string? CuratedSet,
        Dictionary<long, List<string>> PartsByModelConfigId);

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
            if (cf is null || cf.SchemaVersion != Schema || cf.CatalogVersion != catalogVersion
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheFile(Schema, catalogVersion, CuratedSet(),
                new Dictionary<long, List<string>>(partsByModelConfigId))));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }
}
