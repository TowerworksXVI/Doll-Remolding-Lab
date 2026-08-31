using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Remold.Core.Workbench;

/// <summary>
/// The disk-backed memo behind the sharing measurement's asset hashes: one entry per (bundle CONTENT,
/// object) pair, holding the 3DMigoto-style hash that measurement produced — or the settled "this format
/// has no offline hash" verdict.
///
/// <para><b>Why content-keyed.</b> A mesh's ib hash and a texture's content hash are pure functions of the
/// bytes they were measured from, so the answer is fixed by the bundle's content, never by which catalog
/// or which patch it arrived in. Keying entries on the content identity the game's own manifest states
/// (<see cref="BundleReads.ContentHashLookup"/> — the same identity sharing reuse and
/// <c>Export.CandidacyCache</c> key on, so the three cannot drift) makes every entry self-invalidating: a
/// bundle whose content moved has a new key and simply misses. Nothing here ever goes stale; entries only
/// go unused. That is what makes a game update cost bundle reads in proportion to the content that
/// actually moved — a row whose scope shifted has to be measured again, but the values behind it come back
/// out of this memo without opening anything.</para>
///
/// <para><b>But the VALUES are this app's.</b> A key names game content; the hash behind it is something
/// this code computed, and what that computation means moves with the app rather than with the game — which
/// no content key can notice. So a file also states the <see cref="SharingIndex.SchemaVersion"/> it was
/// written under and is dropped whole when that no longer matches: the measurement's own version invalidates
/// these values by construction, rather than by somebody remembering to. See <see cref="MemoFile"/>.</para>
///
/// <para><b>Only content facts are persisted.</b> A successful measurement and the unhashable-by-format
/// verdict are facts about the bytes. A bundle that could not be opened is a fact about the RUN (the game
/// holding its files), and is never written here — the pass keeps its own in-memory failure memo for
/// that, so a failure still fails every wearer of the asset within the run and is retried on the
/// next.</para>
///
/// <para><b>The cost of trusting the manifest.</b> Content swapped underneath an unchanged manifest stub
/// reads as unchanged here, exactly as it does for the reuse gate and the candidacy memo. That is the
/// price of a lookup that costs no bundle read, which is where the whole saving is.</para>
///
/// <para><b>A cache must never fail a measurement.</b> Every disk touch is best-effort: an absent,
/// unreadable, foreign-schema or corrupt file simply measures afresh, and a write that cannot land leaves
/// the previous file alone.</para>
///
/// <para>One instance per pass. Loading happens lazily on the first lookup and <see cref="Flush"/> writes
/// once at the end, atomically through a unique temp — two passes racing the same file publish whole
/// files, and the loser only costs the next pass a re-measure.</para>
/// </summary>
public sealed class AssetHashMemo
{
    /// <summary>Bumped when an entry's meaning changes — INCLUDING how its key is derived, since a key that
    /// means something else addresses a different measurement under the same name. A file written by
    /// another schema is dropped whole rather than read entry by entry.
    ///
    /// <para>This covers the memo's OWN shape only. What an entry's VALUE means is
    /// <see cref="SharingIndex.SchemaVersion"/>'s question, and a file states that number too — see
    /// <see cref="MemoFile"/>.</para></summary>
    public const int SchemaVersion = 1;

    /// <summary>How many entries survive a save by default. A whole roster measures on the order of twenty
    /// thousand of them (five hundred outfits, a dozen rendering tiers and a few dozen texture maps each),
    /// so this holds several catalog generations side by side before the least-recently-touched start to
    /// go — while still bounding a file that is read in full at the first lookup of every launch.</summary>
    private const int DefaultMaxEntries = 100_000;

    /// <summary>The value an unhashable-by-format entry carries: a settled verdict, not a missing one. A
    /// format with no DXGI mapping has no offline hash at all, which is a fact about the content and
    /// therefore worth keeping.</summary>
    private const string Unhashable = "";

    private readonly string? _file;
    private readonly string? _seedFile;
    private readonly int _maxEntries;
    private Dictionary<string, string>? _entries;
    /// <summary>Keys this pass read or wrote, in FIRST-touch order, ranked ahead of entries only the file
    /// knew about when the cap has to evict — which across passes is least-recently-used.</summary>
    private readonly List<string> _touched = new();
    private readonly HashSet<string> _touchedSet = new(StringComparer.Ordinal);
    private bool _dirty;

    /// <summary>Whether this memo persists anything. A caller skips computing keys when it does not — a
    /// lookup nobody will make is a SHA-256 per mesh and per texture map for nothing.</summary>
    public bool Enabled => _file is not null || _seedFile is not null;

    /// <summary>Measurements served from the memo rather than read out of a bundle.</summary>
    public int Hits { get; private set; }

    /// <summary>Measurements this pass made and memoized.</summary>
    public int Writes { get; private set; }

    /// <param name="file">This install's memo, or null for no persistence at all: nothing read, nothing
    /// written, every measurement fresh. The counters work either way.</param>
    /// <param name="seedFile">The SHIPPED memo, read alongside the install's own and never written to. A
    /// fresh install — or one whose cache a force rescan just swept — then opens only the bundles whose
    /// content the shipped measurement never saw.</param>
    /// <param name="maxEntries">How many entries a save keeps (see <see cref="DefaultMaxEntries"/>). Only
    /// a test reaching the cap deliberately passes this.</param>
    public AssetHashMemo(string? file, string? seedFile = null, int maxEntries = DefaultMaxEntries)
    {
        _file = file;
        _seedFile = seedFile;
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>One mesh's key: the bundle's content identity, the mesh's node name and its path-id
    /// selector — the same selector the measurement reads by. Null when the bundle's content identity is
    /// unknown, which is not an identity and must never be memoized under.</summary>
    public static string? MeshKey(string? bundleContentId, string meshName, long pathId) =>
        bundleContentId is null ? null : NameKey.Of($"m\n{bundleContentId}\n{meshName}\n{pathId}");

    /// <summary>One texture's key, on the same rule as <see cref="MeshKey"/>. The domain letter keeps the
    /// two keyspaces apart, so a mesh and a texture can never collide on one entry.</summary>
    public static string? TextureKey(string? bundleContentId, string textureName, long pathId) =>
        bundleContentId is null ? null : NameKey.Of($"t\n{bundleContentId}\n{textureName}\n{pathId}");

    /// <summary>The memoized measurement for <paramref name="key"/>. False when there is none — a missing
    /// key, a null key, or no persistence at all. True with a null <paramref name="hash"/> is the settled
    /// unhashable-by-format verdict, which is an answer and not a miss.</summary>
    public bool TryGet(string? key, out string? hash)
    {
        hash = null;
        if (key is null) return false;
        if (!Loaded().TryGetValue(key, out var value)) return false;
        Touch(key);
        Hits++;
        hash = value.Length == 0 ? null : value;
        return true;
    }

    /// <summary>Memoize one measurement: <paramref name="hash"/> null records the unhashable-by-format
    /// verdict. A null key is measured and not kept. Never call this for a read that FAILED — a bundle
    /// that would not open is a fact about the run, not about the content.</summary>
    public void Put(string? key, string? hash)
    {
        if (key is null) return;
        Loaded()[key] = hash ?? Unhashable;
        Touch(key);
        Writes++;
        _dirty = true;
    }

    /// <summary>Publish what this pass measured. Best-effort and silent: an unwritable memo costs the next
    /// pass a re-measure, never this pass's answer.
    ///
    /// <para>Entries the SEED supplied are written out too, so this install's file is a whole snapshot
    /// rather than a delta on the shipped one. That is what lets a release seed be minted by copying the
    /// cache artifacts off one full measure (see <c>LabPaths.SharingSeedFile</c>).</para></summary>
    public void Flush()
    {
        if (_file is null || !_dirty || _entries is null) return;
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // This pass's keys in reverse FIRST-touch order, then whatever the files already held, up to
            // the cap: an entry only a file knew about is the first to go.
            var kept = new Dictionary<string, string>(Math.Min(_entries.Count, _maxEntries),
                StringComparer.Ordinal);
            for (int i = _touched.Count - 1; i >= 0 && kept.Count < _maxEntries; i--)
                if (_entries.TryGetValue(_touched[i], out var v)) kept[_touched[i]] = v;
            foreach (var kv in _entries)
            {
                if (kept.Count >= _maxEntries) break;
                if (!_touchedSet.Contains(kv.Key)) kept[kv.Key] = kv.Value;
            }

            // Atomic publish through a UNIQUE temp: a fixed name is one shared file, so two passes racing
            // it publish a half-written memo.
            var tmp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool published = false;
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(
                    new MemoFile(SchemaVersion, SharingIndex.SchemaVersion, kept)));
                File.Move(tmp, _file, overwrite: true);
                _dirty = false;
                published = true;
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
            }
            // Temps a previous pass left behind: the `finally` above clears this pass's own, but a hard
            // kill between the write and the move cannot, and the litter accumulates in the index folder
            // forever. Name-scoped to what THIS file's publishes mint — the folder also holds the sharing
            // index, the catalog snapshot and the candidacy memo — and run only after a successful
            // publish. See CacheTemps.SweepMinted.
            if (published) CacheTemps.SweepMinted(_file);
        }
        catch { /* no memo is a slower pass, never a wrong one */ }
    }

    // ---- storage ---------------------------------------------------------------------------------------

    /// <summary>The file: two schemas and a flat key→value map. No game-derived string appears anywhere —
    /// a key is a one-way <see cref="NameKey"/> and a value is already a hash — which is what lets this
    /// file ship to every other install alongside the sharing seed.
    ///
    /// <para><b>Why two.</b> A key names GAME CONTENT only, so an entry self-invalidates when the content
    /// behind it moves — but the value is something this app COMPUTED, and what a computed value means
    /// moves with the app rather than with the game. That is exactly what
    /// <see cref="SharingIndex.SchemaVersion"/> tracks, so a file records the sharing schema it was written
    /// under and <see cref="Merge"/> drops it whole when that no longer matches. Without the second number,
    /// a bump made precisely BECAUSE the hashing changed would re-measure every sharing row and then serve
    /// every value back out of this memo unchanged — and stale values in the index make the build's
    /// by-value sharing join miss, which ships a shared texture as private. An older file states no such
    /// number at all, deserializes to 0, and is dropped on the same test.</para></summary>
    private sealed record MemoFile(int SchemaVersion, int SharingSchemaVersion,
        Dictionary<string, string> Entries);

    private Dictionary<string, string> Loaded()
    {
        if (_entries is not null) return _entries;
        _entries = new Dictionary<string, string>(StringComparer.Ordinal);
        // The seed first, so this install's own measurements win any key the two somehow disagree on.
        // Content-keyed entries cannot honestly disagree; the order is stated so the answer is not luck.
        Merge(_seedFile, own: false);
        Merge(_file, own: true);
        return _entries;
    }

    /// <param name="own">True for the file this pass may WRITE. A foreign schema there dirties the memo so
    /// one pass takes the file over; the same finding on the read-only SEED must not, or every launch would
    /// rewrite this install's own file over a shipped file it can never replace.</param>
    private void Merge(string? path, bool own)
    {
        if (path is null || _entries is null) return;
        try
        {
            if (!File.Exists(path)) return;
            var mf = JsonSerializer.Deserialize<MemoFile>(File.ReadAllText(path));
            // A foreign schema is not readable entry by entry — measure afresh and take the file over. The
            // take-over is what keeps this from repeating: left undirtied, a file from an older schema is
            // re-parsed and discarded on every launch, forever.
            if (mf is null || mf.SchemaVersion != SchemaVersion || mf.Entries is null
                || mf.SharingSchemaVersion != SharingIndex.SchemaVersion)
            {
                if (own) _dirty = true;
                return;
            }
            foreach (var kv in mf.Entries)
                if (kv.Key.Length > 0 && kv.Value is not null) _entries[kv.Key] = kv.Value;
        }
        catch
        {
            // Unreadable is treated as ABSENT, and deliberately does NOT dirty the file: a momentary
            // sharing violation and real corruption look the same from here, and only a measurement this
            // pass made (see Put) earns a rewrite.
        }
    }

    private void Touch(string key)
    {
        if (_touchedSet.Add(key)) _touched.Add(key);
    }
}
