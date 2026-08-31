using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetsTools.NET;

namespace Remold.Core.Export;

/// <summary>The MESH-DERIVED half of one part's candidacy: the bone table, whether the skin stores one
/// influence per vertex, and the bones the skin actually poses. A pure function of the mesh's bytes —
/// nothing here depends on the wardrobe, the prefab flags or the run — which is what makes it cacheable by
/// content. The other half (presence, shadow, visibility) comes off the roster row and the tables every
/// run, and is deliberately NOT cached.</summary>
internal readonly record struct MeshCandidacy(IReadOnlySet<uint> Table, bool Narrow, IReadOnlySet<uint> Posed);

/// <summary>
/// The on-disk memo behind <see cref="AssetExporter.BuildRiggedGlbs"/>'s candidacy pass, plus what that
/// pass actually had to do this run.
///
/// <para>Measuring a part's posed set means summing its whole skin stream, and the answer is fixed by the
/// mesh's bytes: the same bundle content measures the same way every open. Entries are therefore keyed by
/// the bundle's CONTENT identity plus the mesh's name and path id (<see cref="Key"/>), so a game update
/// misses exactly the bundles it changed and nothing else, and no catalog version appears in the key or
/// the file name. The content identity is the one the game's own manifest states — the stub's
/// <c>SubHash</c>, which sharing reuse already keys on — so a key costs a dictionary lookup and NO bundle
/// read: the caller can consult this memo before it opens anything, which is where the saving actually
/// is.</para>
///
/// <para><b>A cache must never fail an export or produce a wrong roster.</b> Every disk touch is
/// best-effort: an absent, unreadable, foreign-schema or corrupt file simply measures afresh, an entry
/// whose payload won't decode is treated as a miss, and a write that can't land leaves the previous file
/// alone. Only a measurement this run made ever rewrites the file — a run that could not READ it publishes
/// nothing over it, since a momentary lock and real corruption look the same from here. Nothing is written until the measurement succeeded, so a part whose
/// weights can't be read is never memoized as anything — it degrades identically on every run, warm cache
/// or cold.</para>
///
/// <para>One instance per export. <see cref="Load"/> happens lazily on the first lookup and
/// <see cref="Flush"/> writes once at the end, atomically through a unique temp — two exports racing the
/// same file publish whole files, and the loser only costs the next run a re-measure.</para>
/// </summary>
internal sealed class CandidacyCache
{
    /// <summary>Bumped when a row's meaning changes — INCLUDING how its key is derived, since a key that
    /// means something else addresses a different measurement under the same name. A file written by another
    /// schema is dropped whole rather than read row by row: a row is only ever as good as the rules that
    /// measured it. 2: the bundle's content identity became the manifest's stated one rather than a hash of
    /// the deobfuscated bytes.</summary>
    private const int Schema = 2;

    /// <summary>How many rows survive a save by default. The file is read in full on the first lookup of an
    /// open, so it must not grow without bound — an unbounded memo eventually costs more to parse than the
    /// scans it saves. Rows this run touched are kept ahead of rows only the file knew about, so ACROSS runs
    /// the cap evicts least-recently-used. ~500 rows is tens of subjects at a few hundred bones each, and a
    /// file of a megabyte or two.</summary>
    private const int DefaultMaxRows = 512;

    private readonly string? _file;
    private readonly int _maxRows;
    private Dictionary<string, Row>? _rows;
    /// <summary>Keys this run read or wrote, in FIRST-touch order: a key touched again keeps the position of
    /// its first touch. That order is only ever used to rank rows within one run's save, and every key here
    /// outranks every row only the file knew about, which is where the recency that matters lives.</summary>
    private readonly List<string> _touched = new();
    private readonly HashSet<string> _touchedSet = new(StringComparer.Ordinal);
    private bool _dirty;

    /// <summary>Bundles the GAP pass asked for the bytes of: roster rows the export loop did not already
    /// measure AND the memo could not answer. The whole point of keying on the manifest's content identity
    /// is that a memoized row never reaches this — a warm pass opens nothing. Counted per ASK, not per disk
    /// read: the run's byte cache may already hold the bundle, and what this measures is whether the memo
    /// spared the pass the ask at all.</summary>
    public int BundleReads;

    /// <summary>Mesh fields the GAP pass had to fetch: roster rows the export loop did not already measure
    /// AND the memo could not answer. The export loop's own reads are not counted — it fetches those
    /// fields for the export itself.</summary>
    public int MeshReads;

    /// <summary>Weight scans actually performed (<see cref="Measure"/> bodies run).</summary>
    public int WeightScans;

    /// <summary>Measurements served from the memo.</summary>
    public int Hits;

    /// <param name="file">Where the memo lives, or null for no persistence at all: nothing read, nothing
    /// written, every measurement fresh. The counters work either way.</param>
    /// <param name="maxRows">How many rows a save keeps (see <see cref="DefaultMaxRows"/>). Only a test
    /// reaching the cap deliberately passes this.</param>
    public CandidacyCache(string? file, int maxRows = DefaultMaxRows)
    {
        _file = file;
        _maxRows = Math.Max(1, maxRows);
    }

    /// <summary>Whether this cache persists anything. A caller skips computing keys when it doesn't — a
    /// lookup nobody will make is pure cost.</summary>
    public bool Enabled => _file is not null;

    /// <summary>One entry's key: the bundle's content identity, the mesh's name and its path-id selector,
    /// hashed to a fixed-width opaque token. Hashed rather than concatenated for two reasons — the file
    /// then carries no game-derived string, and every key is the same width whatever the names are.
    ///
    /// <para><paramref name="bundleContentId"/> is what the game's manifest STATES the bundle holds, not a
    /// hash the caller took of the bytes. That is what lets a lookup happen before the bundle is opened, and
    /// it is the same identity <c>Workbench.BundleReads.ContentHashLookup</c> keys sharing reuse on — one
    /// identity home, so the two cannot drift. The cost is that content swapped underneath an unchanged
    /// manifest reads as unchanged here.</para></summary>
    public static string Key(string bundleContentId, string meshName, long pathId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            bundleContentId + "\n" + meshName + "\n" + pathId.ToString(CultureInfo.InvariantCulture))))
            .ToLowerInvariant();

    /// <summary>The memoized measurement for <paramref name="key"/>, or null when there is none — a
    /// missing key, a disabled cache, or a row whose payload won't decode (which is a miss, and the fresh
    /// measurement overwrites it). A hit is byte-for-byte what <see cref="Measure"/> would have produced
    /// from the same bytes.</summary>
    public MeshCandidacy? TryGet(string? key)
    {
        if (key is null) return null;
        if (!Loaded().TryGetValue(key, out var row)) return null;
        var table = Unpack(row.T);
        var posed = Unpack(row.P);
        // a row that won't decode is a miss, not an error: the measurement below replaces it
        if (table is null || posed is null) { Loaded().Remove(key); _dirty = true; return null; }
        Touch(key);
        Hits++;
        return new MeshCandidacy(table, row.N, posed);
    }

    /// <summary>Measure the mesh-derived triple off <paramref name="field"/> and memoize it under
    /// <paramref name="key"/> (null key ⇒ measured, not kept). Throws whatever the measurement throws — a
    /// mesh whose weights can't be read has no answer, and its caller holds the part back exactly as it
    /// does with no cache at all. Nothing is written for a throw.</summary>
    public MeshCandidacy Measure(string? key, AssetTypeValueField field)
    {
        WeightScans++;
        // The order the pre-cache pass used, kept: a mesh past every layout check can still fail at the
        // weight sum, and which failure a caller sees must not move.
        var table = field["m_BoneNameHashes"]["Array"].Children.Select(c => c.AsUInt).ToHashSet();
        var posed = Migoto.StreamDump.WeightedBoneHashes(field);
        var narrow = Mesh.SkinLayout.IsNarrow(field);
        var measured = new MeshCandidacy(table, narrow, posed);
        if (key is not null)
        {
            Loaded()[key] = new Row(key, narrow, Pack(table), Pack(posed));
            Touch(key);
            _dirty = true;
        }
        return measured;
    }

    /// <summary>Publish what this run measured. Best-effort and silent: an unwritable cache costs the next
    /// run a re-measure, never this run's answer.</summary>
    public void Flush()
    {
        if (_file is null || !_dirty || _rows is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // This run's keys in reverse FIRST-touch order, then whatever the file already held, up to the
            // cap: a row only the file knew about is the first to go, and a run that touched more rows than
            // fit keeps the ones it reached last. Within one run that order is arbitrary-but-stable rather
            // than true recency (a re-touch does not move a key); across runs, which is the axis a cap of
            // hundreds of rows is actually evicting on, it is least-recently-used.
            var rows = new List<Row>(Math.Min(_rows.Count, _maxRows));
            for (int i = _touched.Count - 1; i >= 0 && rows.Count < _maxRows; i--)
                if (_rows.TryGetValue(_touched[i], out var r)) rows.Add(r);
            foreach (var kv in _rows)
            {
                if (rows.Count >= _maxRows) break;
                if (!_touchedSet.Contains(kv.Key)) rows.Add(kv.Value);
            }

            // Atomic publish through a UNIQUE temp: a fixed name is one shared file, so two exports racing
            // it publish a half-written memo.
            var tmp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool published = false;
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheFile(Schema, rows)));
                File.Move(tmp, _file, overwrite: true);
                _dirty = false;
                published = true;
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
            }
            // Temps a previous export left behind. The `finally` above clears this run's own, but a hard
            // kill between the write and the move cannot, and the litter accumulates beside the memo
            // forever. The sweep is name-scoped to what THIS file's publishes mint, and runs only after a
            // successful publish — see CacheTemps.SweepMinted.
            if (published) CacheTemps.SweepMinted(_file);
        }
        catch { /* no memo is a slower open, never a wrong one */ }
    }

    // ---- storage ---------------------------------------------------------------------------------------

    /// <summary>One memoized measurement: its key, the narrow flag, and the two hash sets packed
    /// (<see cref="Pack"/>). No game-derived string appears here — the key is a one-way hash and the bone
    /// hashes are already hashes.</summary>
    private sealed record Row(string K, bool N, string T, string P);

    private sealed record CacheFile(int SchemaVersion, List<Row> Rows);

    private Dictionary<string, Row> Loaded()
    {
        if (_rows is not null) return _rows;
        _rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        if (_file is null) return _rows;
        try
        {
            if (!File.Exists(_file)) return _rows;
            var cf = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_file));
            // a foreign schema is not readable row by row — measure afresh and take the file over
            if (cf is null || cf.SchemaVersion != Schema || cf.Rows is null) { _dirty = true; return _rows; }
            foreach (var r in cf.Rows)
                if (r is { K.Length: > 0, T: not null, P: not null }) _rows[r.K] = r;
        }
        catch
        {
            // Unreadable is treated as ABSENT for this run — nothing partial is kept — and deliberately does
            // NOT dirty the file on its own. A momentary sharing violation (another export mid-publish, a
            // scanner holding the file) is indistinguishable here from real corruption, and marking it dirty
            // would let a run that read nothing rewrite the whole memo with only its own rows. Only a
            // measurement this run made (see Measure) earns a rewrite. The cost is that a genuinely corrupt
            // file survives until some run measures something — which is the run that had to pay for it
            // anyway.
            _rows.Clear();
        }
        return _rows;
    }

    private void Touch(string key)
    {
        if (_touchedSet.Add(key)) _touched.Add(key);
    }

    /// <summary>A hash set as base64 of its members, little-endian and ASCENDING — a set has no order of
    /// its own, and an arbitrary one would rewrite the file for entries that didn't change.</summary>
    private static string Pack(IReadOnlySet<uint> set)
    {
        var sorted = set.ToArray();
        Array.Sort(sorted);
        var bytes = new byte[sorted.Length * 4];
        for (int i = 0; i < sorted.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4, 4), sorted[i]);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>The <see cref="Pack"/> inverse, or null for anything that isn't a whole number of packed
    /// hashes — which the caller reads as a miss.</summary>
    private static HashSet<uint>? Unpack(string? packed)
    {
        if (packed is null) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(packed); }
        catch { return null; }
        if (bytes.Length % 4 != 0) return null;
        var set = new HashSet<uint>(bytes.Length / 4);
        for (int i = 0; i < bytes.Length; i += 4)
            set.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4)));
        return set;
    }
}
