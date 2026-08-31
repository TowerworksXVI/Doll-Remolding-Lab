using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Remold.Core.Bundles;
using Remold.Core.Migoto;
using Remold.Core.Model;

namespace Remold.Core.Workbench;

/// <summary>The population one measurement covers: every roster subject, plus which characters are
/// enemy-door entries. The door side is the only one the duplicate-door rule can filter, so the two lists
/// stay distinguishable all the way into the derivation.</summary>
public sealed record SharingPopulation(
    IReadOnlyList<Character> Roster, IReadOnlyCollection<string> EnemyCharacters)
{
    /// <summary>Playable roster plus enemy-door roster, in that order.</summary>
    public static SharingPopulation Of(IReadOnlyList<Character> playable, IReadOnlyList<Character> enemies) =>
        new(playable.Concat(enemies).ToList(),
            enemies.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>A population with no door side: nothing is a duplicate door.</summary>
    public static SharingPopulation Of(IReadOnlyList<Character> roster) =>
        new(roster, Array.Empty<string>());
}

/// <summary>Measurement progress: outfits this pass has STARTED reading out of the outfits it has to read
/// — a report fires as each read begins, so the count names the outfit being read rather than the last one
/// finished — and whether a prior measurement supplied the rest (<paramref name="Delta"/>) or the pass
/// covers the whole population. A pass with nothing to read reports nothing at all.</summary>
public readonly record struct SharingProgress(int Done, int Total, bool Delta);

/// <summary>
/// Per-catalog measurement of asset sharing across the modding roster: which texture contents and which
/// mesh index buffers are worn by more than one outfit. 3DMigoto matches overrides by content hash, so an
/// edit's true reach is the hash's wearer set — this index lets a build scope shared edits and disclose
/// the reach it cannot scope.
///
/// <para>Built forward from the roster the way the workbench resolves subjects (prefab → parts → tiers →
/// materials → maps); no bundle enumeration. A pure function of the game data, so it SHIPS: the file is
/// the same one the app caches, and a subject whose catalog SHAPE (<see cref="SubjectFingerprint"/>),
/// part-address RESOLUTIONS and read-bundle CONTENT (<see cref="BundleReads"/>) still stand is never read
/// again — none is packaging identity, so a patch that repacks the game costs nothing.
/// <b>Observations are stored; relations are derived:</b> one row per outfit; wearer
/// sets, witness privacy and the duplicate-door filter recompute at load, which lets a single row be
/// replaced on its own.</para>
///
/// <para>Identity: mesh = the 3DMigoto <c>ib</c> hash — unique per TOPOLOGY, the radius an anchored
/// override fires on, so the correct grouping. Every renderer-slot tier participates, including
/// <c>lodm</c> tiers. Texture = the offline texture hash; formats with no DXGI mapping (the toon-ramp LUTs)
/// are unhashable, unmoddable, excluded.</para>
/// </summary>
public sealed class SharingIndex
{
    /// <summary>Bump on any change to what is measured, how wearers/witnesses are derived, or the row
    /// shape. This is also the lever over the app's OWN side of a measurement — a mesh prefix, a token
    /// rule, a part-ownership test, WHICH texture object a material's map resolves to — since a subject's
    /// fingerprint tracks the game's data and not this code.
    ///
    /// <para>A bump has to reach further than this file, and that reach is WIRED rather than remembered:
    /// <see cref="AssetHashMemo"/> persists values this code computed (ib and texture hashes) under keys
    /// that name game content alone, so a bump that correctly re-measures every row would otherwise serve
    /// every value straight back out of the memo. The memo's file states the sharing schema it was written
    /// under and is dropped whole when that no longer matches, so a bump here invalidates those values by
    /// construction. Public because the release pack refuses a shipped seed that states another schema —
    /// see <see cref="ShippedMeasurement"/>.</para>
    ///
    /// <para>The one row field this does not have to cover is R's SHAPE: a read record in any other format
    /// than the one <see cref="BundleReads"/> writes fails its length check and re-measures the row on its
    /// own. What the shape cannot say is which HASH each key means — schema 5 shipped two incompatible
    /// answers to that inside one arc (the physical filename, then the content hash), so the version is
    /// what tells them apart.</para>
    ///
    /// <para>7: the reuse gate stopped keying on packaging identity. The fingerprint dropped its internalId
    /// joins, the read record dropped its internalId key, and a row with no read record at all — the old
    /// bootstrap allowance, which let the shipped seed be kept on a fingerprint alone — is no longer
    /// reusable at any grain. Every row, the seed's included, now carries what it read.</para>
    ///
    /// <para>8: measurement now admits every renderer-slot tier, including <c>lodm</c> tiers. Rows measured
    /// under schema 7 can omit those mesh and witness hashes while their fingerprint and read-bundle
    /// content still match, so no schema-7 row is reusable. Each row also records which logical bundle
    /// every catalog-resolved part address named, closing the case where an address retargets while every
    /// bundle the old row read remains untouched.</para></summary>
    public const int SchemaVersion = 8;

    /// <summary>One roster outfit that wears an asset. Display fields fall back to the internal
    /// names when localization resolved nothing.</summary>
    public sealed record Wearer(string Character, string? CharacterDisplay, string Stem, string? StemDisplay)
    {
        public string CharacterLabel => string.IsNullOrEmpty(CharacterDisplay) ? Character : CharacterDisplay!;
    }

    /// <summary>One outfit's measurement: the mesh ibs and texture contents it wears, the subset of its
    /// meshes ELIGIBLE to witness its presence, the <see cref="SubjectFingerprint"/> of the catalog shape it
    /// was measured under, its catalog-resolved part addresses, and the <see cref="BundleReads"/> record of
    /// what the bundles it read were holding. <see cref="WitnessCandidates"/> is eligibility only — privacy
    /// is derived across the surviving population at load, so under-listing costs witnesses but can never
    /// mint a wrong one.
    /// <see cref="Reads"/> is carried by EVERY row: an empty record means the measurement depended on no
    /// bundle at all, never that the row may be trusted without one.</summary>
    public sealed record Observation(string Fingerprint, IReadOnlyList<string> Mesh,
        IReadOnlyList<string> Tex, IReadOnlyList<string> WitnessCandidates, string Reads,
        string AddressResolutions);

    private readonly Wearer[] _wearers;
    private readonly Observation[] _observations;                   // parallel to _wearers
    private readonly Dictionary<string, int> _ordinalByKey;         // "char|stem" lowercase → ordinal (ALL rows)
    private readonly HashSet<int> _duplicateDoors;                  // ordinals filtered out of the population
    private readonly Dictionary<string, int[]> _texWearers;         // texture hash (x8) → wearer ordinals
    private readonly Dictionary<string, int[]> _meshWearers;        // ib hash (x8) → wearer ordinals
    private readonly Dictionary<int, string[]> _witnesses;          // ordinal → private witness ib hashes

    public string CatalogVersion { get; }

    /// <summary>Per-outfit resolution failures recorded during the build. Diagnostics, not errors: a
    /// subject that failed to measure is simply not covered (<see cref="Covers"/>). Not persisted — a
    /// loaded index carries the failures as <see cref="FailedOutfits"/> and nothing else.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Outfits (as <c>character|stem</c>) that recorded any problem during their measurement.
    /// None of their data is committed — a partially-read outfit would understate an asset's reach, so
    /// the whole outfit stays uncovered and the build discloses that instead.</summary>
    public IReadOnlyList<string> FailedOutfits { get; }

    /// <summary>Outfits measured, committed, and inside the population — a duplicate door is none of the
    /// three.</summary>
    public int MeasuredOutfitCount => _wearers.Length - _duplicateDoors.Count;

    /// <summary>Deterministic identity of the observations and population rules a build can consult.
    /// This is an input to exact-build reuse: a later completed sharing pass can change scoping without
    /// changing the authored project or any game bundle.</summary>
    internal string BuildIdentity()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        Add(SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(CatalogVersion);
        for (int i = 0; i < _wearers.Length; i++)
        {
            var wearer = _wearers[i];
            var row = _observations[i];
            Add(wearer.Character);
            Add(wearer.CharacterDisplay);
            Add(wearer.Stem);
            Add(wearer.StemDisplay);
            Add(_duplicateDoors.Contains(i) ? "door" : "row");
            Add(row.Fingerprint);
            foreach (string value in row.Mesh) Add(value);
            Add("tex");
            foreach (string value in row.Tex) Add(value);
            Add("witness");
            foreach (string value in row.WitnessCandidates) Add(value);
            Add(row.Reads);
            Add(row.AddressResolutions);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Whether this file can safely save work as a prior-catalog delta base. A normal completed
    /// pass writes a nonempty read record on every row and is not published when failures exceed this same
    /// floor. Hand-authored, truncated, and failed-pass files therefore do not outrank the shipped seed.</summary>
    public bool IsCompleteLocalBase()
    {
        int total = MeasuredOutfitCount + FailedOutfits.Count;
        return MeasuredOutfitCount > 0
            && _observations.All(row => !string.IsNullOrEmpty(row.Reads))
            && FailedOutfits.Count <= Math.Max(3, total / 20);
    }

    private SharingIndex(string catalogVersion, Wearer[] wearers, Observation[] observations,
        IReadOnlyCollection<string> enemyCharacters, IReadOnlyList<string> problems,
        IReadOnlyList<string> failedOutfits)
    {
        CatalogVersion = catalogVersion;
        _wearers = wearers;
        _observations = observations;
        Problems = problems;
        FailedOutfits = failedOutfits;
        _ordinalByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < wearers.Length; i++)
            _ordinalByKey[Key(wearers[i].Character, wearers[i].Stem)] = i;

        _duplicateDoors = FindDuplicateDoors(wearers, observations, enemyCharacters);

        // ---- relations, derived over the surviving population ----
        var tex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var mesh = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int ord = 0; ord < observations.Length; ord++)
        {
            if (_duplicateDoors.Contains(ord)) continue;
            foreach (var ib in observations[ord].Mesh) Add(mesh, ib, ord);
            foreach (var t in observations[ord].Tex) Add(tex, t, ord);
        }
        _texWearers = Compact(tex);
        _meshWearers = Compact(mesh);

        // A witness must be PRIVATE: worn by exactly its own outfit across the population.
        _witnesses = new Dictionary<int, string[]>();
        for (int ord = 0; ord < observations.Length; ord++)
        {
            if (_duplicateDoors.Contains(ord)) continue;
            var priv = observations[ord].WitnessCandidates
                .Where(ib => _meshWearers.TryGetValue(ib, out var w) && w.Length == 1).ToArray();
            if (priv.Length > 0) _witnesses[ord] = priv;
        }

        // The last-element dedupe relies on rows being folded in ascending ordinal order, one row at a
        // time — an interleaved fold would need a set per hash instead.
        static void Add(Dictionary<string, List<int>> map, string hash, int ord)
        {
            if (!map.TryGetValue(hash, out var list)) map[hash] = list = new List<int>();
            if (list.Count == 0 || list[^1] != ord) list.Add(ord);
        }

        static Dictionary<string, int[]> Compact(Dictionary<string, List<int>> map) =>
            map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>The duplicate-door rule: an enemy-door row whose distinct mesh set is EXACTLY a playable
    /// outfit's is a second door onto content the roster already lists, filtered from the tab and this
    /// measurement alike. Partial overlap is not enough, and a mesh-less row is never a door.
    /// <b>Boundary: enemy against PLAYABLE only.</b> Two enemy rows that are exact twins both stay — each
    /// keeps the other's meshes non-private, so neither witnesses its own presence and their shared edits
    /// disclose the pair; only a playable outfit's mesh set makes a door redundant.</summary>
    private static HashSet<int> FindDuplicateDoors(Wearer[] wearers, Observation[] observations,
        IReadOnlyCollection<string> enemyCharacters)
    {
        var doors = new HashSet<int>();
        if (enemyCharacters.Count == 0) return doors;
        var isEnemy = enemyCharacters as HashSet<string>
            ?? new HashSet<string>(enemyCharacters, StringComparer.OrdinalIgnoreCase);

        static string? Signature(Observation o)
        {
            var distinct = o.Mesh.Distinct(StringComparer.Ordinal).ToList();
            if (distinct.Count == 0) return null;
            distinct.Sort(StringComparer.Ordinal);
            return string.Join(",", distinct);
        }

        var playable = new HashSet<string>(StringComparer.Ordinal);
        for (int ord = 0; ord < wearers.Length; ord++)
            if (!isEnemy.Contains(wearers[ord].Character) && Signature(observations[ord]) is { } sig)
                playable.Add(sig);
        if (playable.Count == 0) return doors;
        for (int ord = 0; ord < wearers.Length; ord++)
            if (isEnemy.Contains(wearers[ord].Character)
                && Signature(observations[ord]) is { } sig && playable.Contains(sig))
                doors.Add(ord);
        return doors;
    }

    private static string Key(string character, string stem) =>
        $"{character.ToLowerInvariant()}|{stem.ToLowerInvariant()}";

    /// <summary>True when the outfit was measured and is inside the population. An uncovered subject has no
    /// reach or witness data, and a build must say so rather than treat absence as privacy.</summary>
    public bool Covers(string character, string stem) =>
        _ordinalByKey.TryGetValue(Key(character, stem), out var ord) && !_duplicateDoors.Contains(ord);

    /// <summary>True when the outfit is a filtered duplicate door.</summary>
    public bool IsDuplicateDoor(string character, string stem) =>
        _ordinalByKey.TryGetValue(Key(character, stem), out var ord) && _duplicateDoors.Contains(ord);

    /// <summary>Wearers of the texture content beyond the given outfit (empty ⇒ private to it).</summary>
    public IReadOnlyList<Wearer> TexOtherWearers(string texHash, string character, string stem) =>
        OtherWearers(_texWearers, texHash, character, stem);

    /// <summary>Wearers of the mesh ib beyond the given outfit (empty ⇒ private to it).</summary>
    public IReadOnlyList<Wearer> MeshOtherWearers(string ibHash, string character, string stem) =>
        OtherWearers(_meshWearers, ibHash, character, stem);

    /// <summary>The outfit's presence witnesses: ib hashes of rendering tiers private to this outfit and
    /// eligible to signal presence. Empty when the outfit has none (or is not covered).</summary>
    public IReadOnlyList<string> WitnessIbs(string character, string stem) =>
        Covers(character, stem) && _witnesses.TryGetValue(_ordinalByKey[Key(character, stem)], out var w)
            ? w : Array.Empty<string>();

    private IReadOnlyList<Wearer> OtherWearers(Dictionary<string, int[]> map, string hash,
        string character, string stem)
    {
        if (!map.TryGetValue(hash, out var ords)) return Array.Empty<Wearer>();
        // -1 when the subject isn't covered, so an uncovered subject can't alias wearer ordinal 0
        int self = _ordinalByKey.TryGetValue(Key(character, stem), out var s) ? s : -1;
        var others = new List<Wearer>();
        foreach (var o in ords)
            if (o != self) others.Add(_wearers[o]);
        return others;
    }

    // ---- witness eligibility --------------------------------------------------------------------------

    // Four exclusions, none of them a reliable presence signal however private the ib. Modular outfit
    // pieces (P1_/P2_/P3_…) mix per player choice, so any combination can co-draw; a context-locked
    // variant (_Dorm/_Fight) draws in one scene class only; a renderer outside the shadow pass
    // (m_CastShadows Off, carried per tier) issues no draw at all once the camera leaves it, where a
    // casting one keeps a depth-only draw going; and a node the game's own dorm and lobby logic can
    // withhold draws on a condition its name does not carry. All four stay fully editable — this gates
    // only what may WITNESS presence. The name-shaped two answer here; the other two ride flags read off
    // the prefab, so neither costs a read of its own.
    //
    // The modular seam is ShoeNodeMatch's, not a copy: the timeline matcher and this measurement have to
    // agree on what "modular" means, so there is one home for the shape and both read it.
    private static bool ContextLocked(string name) =>
        name.EndsWith("_dorm", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("_fight", StringComparison.OrdinalIgnoreCase);

    internal static bool EligibleWitnessName(string name) =>
        !ShoeNodeMatch.CarriesModularSeam(name) && !ContextLocked(name);

    // ---- build ----------------------------------------------------------------------------------------

    /// <summary>
    /// Measures the population, reading only what <paramref name="previous"/> cannot supply: an outfit
    /// whose <see cref="SubjectFingerprint"/> still matches AND whose <see cref="BundleReads"/> still
    /// resolve to the same bundle CONTENT keeps its row; everything else (moved, never covered, FAILED) is
    /// read. Minutes
    /// on a whole population when nothing is kept; safe to cancel — no partial state is published.
    /// <paramref name="population"/> must carry the launch-shaped roster (display names enriched, curated
    /// skins merged) so curated subjects are covered. The plan pass is catalog-only, so a run with nothing
    /// to re-read opens no bundle and reports no progress.
    ///
    /// <para><paramref name="hashes"/> is the cross-pass observation memo: an outfit that HAS to be
    /// measured again still opens only the bundles whose content the memo has never seen, so the cost of a
    /// game update is proportional to the content that moved rather than to the rows it invalidated. Null
    /// measures everything afresh. The caller flushes it — this method touches no disk.</para>
    /// </summary>
    public static SharingIndex Build(SharingPopulation population, CatalogIndex catalog,
        Func<string, string?> contentHashOf, Func<string, byte[]?> tryDeobfuscate, string catalogVersion,
        SharingIndex? previous = null, IProgress<SharingProgress>? progress = null,
        CancellationToken ct = default, AssetHashMemo? hashes = null)
    {
        var roster = population.Roster;
        var reader = new BundleReader();
        var wearers = new List<Wearer>();
        var observations = new List<Observation>();
        var problems = new List<string>();
        var ibCache = new Dictionary<string, string>(StringComparer.Ordinal);      // bundle|name|pathId → x8
        // bundle|pathId|name → hash, or the failure to re-report per outfit: a memoized failure must fail
        // EVERY outfit that wears the texture, not just the first, or later wearers read as covered
        // while silently missing it. Hash and FailReason null together = unhashable, correctly absent.
        // This memo stays IN MEMORY whatever `hashes` does with the rest: a failure is a fact about the
        // run (a bundle the game is holding), and carrying one to the next pass would serve it forever.
        var texCache = new Dictionary<string, (string? Hash, string? FailReason)>(StringComparer.Ordinal);

        // The content identity behind a logical bundle, asked once per bundle per pass: it is what the
        // cross-pass memo keys on, and it is dictionary work — no bundle is opened to answer it.
        var contentIds = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? ContentId(string bundleId) =>
            contentIds.TryGetValue(bundleId, out var c) ? c
                : contentIds[bundleId] = BundleReads.ContentOf(catalog, contentHashOf, bundleId);

        // With no memo behind the pass, a memo key is a SHA-256 per mesh tier and per texture map that
        // nothing will ever look up: a null key is the "measured, not kept" value both TryGet and Put
        // already take, so the whole cost drops out. (Export.CandidacyCache.Enabled is the same rule.)
        bool memoizing = hashes is { Enabled: true };

        var failedOutfits = new List<string>();

        // Pass 1 — what has to be read. A reusable row needs a prior measurement of THAT outfit under an
        // unmoved catalog shape AND unmoved read-bundle CONTENT; a prior FAILURE is never reused, since a
        // failure is a fact about the run (a bundle held open) and not about the catalog. Catalog reads only.
        var readKeys = previous is null ? null : BundleReads.CurrentKeys(catalog, contentHashOf);
        var resolutionKeys = previous is null ? null : PartAddressResolutions.CurrentKeys(catalog);
        var plan = new List<(Character Character, Outfit Outfit, string Fingerprint, Observation? Reuse)>();
        foreach (var character in roster)
        foreach (var outfit in character.Outfits)
        {
            ct.ThrowIfCancellationRequested();
            string fingerprint = SubjectFingerprint.For(catalog, outfit);
            plan.Add((character, outfit, fingerprint,
                previous?.ReusableRow(character.Name, outfit.Stem, fingerprint, readKeys!, resolutionKeys!)));
        }
        int total = plan.Count(p => p.Reuse is null), done = 0;
        bool delta = previous is not null;
        // A pass with nothing to read is silent: the cell reports re-measures, not sweeps.
        if (total > 0) progress?.Report(new SharingProgress(0, total, delta));

        foreach (var (character, outfit, fingerprint, reuse) in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (reuse is not null)
            {
                wearers.Add(new Wearer(character.Name, character.DisplayName, outfit.Stem, outfit.DisplayName));
                observations.Add(reuse);
                continue;
            }
            progress?.Report(new SharingProgress(++done, total, delta));

            // Staged commit: the outfit's hashes land in per-outfit lists and only merge into the
            // index when the whole outfit measured cleanly. A partially-read outfit would understate
            // reach (its missing parts read as "nobody else wears this"), so ANY problem leaves the
            // outfit uncovered and the build says so instead.
            int baseProblems = problems.Count;
            var outfitMesh = new List<string>();
            var outfitTex = new List<string>();
            var outfitWitness = new List<string>();
            var outfitResolutions = new List<(string Address, string Owner)>();
            // Every bundle this outfit's measurement DEPENDS on, whether the value was read here or served
            // from a cross-outfit memo (a memo hit depends on the same bundle as the read that filled it):
            // the mesh and texture bundles gathered below, plus the assembly prefabs the model was parsed
            // out of and the bundles its materials were read out of. Those last two belong here even though
            // no hash is taken from them. A prefab decides which slots become parts, which tiers a part has,
            // and whether each renderer casts, so one rewritten in place changes this row's mesh set and its
            // witness list; a material decides which texture maps exist and what each binds, so one
            // rewritten in place changes this row's texture list. Either does it without any bundle whose
            // hashes were read having moved at all — a material's own bundle is only among the texture
            // bundles when the texture happens to live beside it.
            var outfitReads = new HashSet<string>(StringComparer.Ordinal);

            SubjectModel model;
            try { model = SubjectModelBuilder.Build(catalog, tryDeobfuscate, outfit, character.Name); }
            catch (Exception ex)
            {
                problems.Add($"{outfit.Stem}: {ex.Message}");
                failedOutfits.Add(Key(character.Name, outfit.Stem));
                continue;
            }
            // A build can succeed while RECORDING problems (an unreadable prefab yields a part-less model,
            // not a throw). Those join the staged-commit check below, or the outfit would commit as
            // covered with whatever partial part list survived — understating every asset it wears.
            // SubjectModel.Problems means exactly "the part data is curtailed or suspect"; a null
            // skeleton rides its own channel and costs nothing here, since this measurement reads
            // parts and textures only.
            foreach (var prob in model.Problems) problems.Add($"{outfit.Stem}: {prob}");
            foreach (var bundle in model.PrefabBundles ?? Array.Empty<string>()) outfitReads.Add(bundle);
            foreach (var bundle in model.MaterialBundles ?? Array.Empty<string>()) outfitReads.Add(bundle);

            var bundleBytes = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            byte[]? Bytes(string bundleId) =>
                bundleBytes.TryGetValue(bundleId, out var b) ? b : bundleBytes[bundleId] = tryDeobfuscate(bundleId);

            foreach (var part in model.Parts)
            {
                // The shadow-pass flag and the visibility override are both keyed per TIER: the list
                // interleaves the representative slot and the siblings, each tier is its own renderer with
                // its own m_CastShadows, and the dorm lists name tier nodes one at a time.
                var tiers = new List<(string Name, string Bundle, long PathId, bool Casts, VisibilityOverride Vis)>();
                void Tier(string name, string address, string? smrBundle, long smrPathId, bool casts,
                    VisibilityOverride vis)
                {
                    if (!string.IsNullOrEmpty(smrBundle) && smrPathId != 0) { tiers.Add((name, smrBundle!, smrPathId, casts, vis)); return; }
                    if (string.IsNullOrEmpty(address)) { problems.Add($"{outfit.Stem}: '{name}' has no mesh identity"); return; }
                    var owner = catalog.ResolveAddress(address);
                    if (owner is null) { problems.Add($"{outfit.Stem}: no catalog entry for '{address}'"); return; }
                    outfitResolutions.Add((address, owner));
                    tiers.Add((name, owner, 0, casts, vis));
                }
                Tier(part.SlotName, part.MeshAddress, part.MeshBundle, part.MeshPathId, part.CastsShadows,
                    part.Visibility);
                foreach (var t in part.SiblingTiers ?? Array.Empty<Export.RecipeTierSlot>())
                    Tier(t.SlotName, t.MeshAddress, t.MeshBundle, t.MeshPathId, t.CastsShadows, t.Visibility);

                // DELIBERATELY stricter than the build side's per-tier gates in
                // ModBuilder.WardrobeWitnesses: a part whose REPRESENTATIVE slot is shadow-off or
                // visibility-withheld is refused wholesale here even when a sibling tier is clean, because
                // this index answers "is this outfit worn" for a whole population and a part the game may
                // stop drawing is no basis for that claim at any tier — do not harmonize the two by
                // loosening this side.
                bool partEligible = EligibleWitnessName(part.Token) && part.CastsShadows
                    && part.Visibility == VisibilityOverride.None;
                foreach (var (name, bundleId, pathId, casts, vis) in tiers)
                {
                    outfitReads.Add(bundleId);
                    string key = $"{bundleId}|{name}|{pathId}";
                    if (!ibCache.TryGetValue(key, out var ib))
                    {
                        // The cross-pass memo before the bundle: an ib is a pure function of the bytes it
                        // was measured from, so a bundle whose content identity the memo already knows is
                        // never opened again. A hit carrying no hash is not one a mesh can produce (only a
                        // texture format can be unhashable), so it is treated as a miss and measured.
                        var memoKey = memoizing
                            ? AssetHashMemo.MeshKey(ContentId(bundleId), name, pathId)
                            : null;
                        if (hashes is not null && hashes.TryGet(memoKey, out var memoized)
                            && memoized is not null)
                            ib = memoized;
                        else
                        {
                            var bytes = Bytes(bundleId);
                            if (bytes is null) { problems.Add($"{outfit.Stem}: bundle missing for mesh '{name}'"); continue; }
                            try { ib = BufferHash.Compute(bytes, name, pathId, reader).Ib.ToString("x8"); }
                            catch (Exception ex) { problems.Add($"{outfit.Stem}: mesh '{name}': {ex.Message}"); continue; }
                            hashes?.Put(memoKey, ib);
                        }
                        ibCache[key] = ib;
                    }
                    outfitMesh.Add(ib);
                    if (partEligible && casts && vis == VisibilityOverride.None
                        && EligibleWitnessName(name) && !outfitWitness.Contains(ib))
                        outfitWitness.Add(ib);
                }

                foreach (var material in part.Materials)
                {
                    if (material.IsPlaceholder) continue;
                    foreach (var map in material.Maps)
                    {
                        outfitReads.Add(map.BundleId);
                        string key = $"{map.BundleId}|{map.PathId}|{map.TextureName}";
                        if (!texCache.TryGetValue(key, out var tex))
                        {
                            // The cross-pass memo before the bundle, as for a mesh. A hit carrying no hash
                            // IS an answer here — the unhashable-by-format verdict, which is a fact about
                            // the content and correctly absent from reach.
                            var memoKey = memoizing
                                ? AssetHashMemo.TextureKey(ContentId(map.BundleId), map.TextureName, map.PathId)
                                : null;
                            if (hashes is not null && hashes.TryGet(memoKey, out var memoized))
                                tex = (memoized, null);
                            else
                            {
                                tex = (null, null);
                                var bytes = Bytes(map.BundleId);
                                if (bytes is null)
                                    tex = (null, $"bundle missing for texture '{map.TextureName}'");
                                else if (reader.GetTextureHashSource(bytes, map.Ref) is { } src)
                                {
                                    // No DXGI mapping ⇒ unhashable ⇒ unmoddable; correctly absent from reach.
                                    if (TextureHash.Dxgi((AssetsTools.NET.Texture.TextureFormat)src.Format, src.Srgb) is { } dxgi)
                                        tex = (TextureHash.Compute(src.PictureData, src.Width, src.Height,
                                            src.MipCount, dxgi).ToString("x8"), null);
                                }
                                else
                                    tex = (null, $"texture '{map.TextureName}' not in its bundle");
                                // Only content facts are kept: the hash, or the settled unhashable verdict.
                                // A failure is about the run and stays in texCache alone.
                                if (tex.FailReason is null) hashes?.Put(memoKey, tex.Hash);
                            }
                            texCache[key] = tex;
                        }
                        if (tex.FailReason is not null) problems.Add($"{outfit.Stem}: {tex.FailReason}");
                        else if (tex.Hash is not null) outfitTex.Add(tex.Hash);
                    }
                }
            }

            if (problems.Count != baseProblems)
            {
                failedOutfits.Add(Key(character.Name, outfit.Stem));
                continue;
            }
            wearers.Add(new Wearer(character.Name, character.DisplayName, outfit.Stem, outfit.DisplayName));
            observations.Add(new Observation(fingerprint, outfitMesh, outfitTex, outfitWitness,
                BundleReads.Of(catalog, contentHashOf, outfitReads),
                PartAddressResolutions.Of(outfitResolutions)));
        }

        return new SharingIndex(catalogVersion, wearers.ToArray(), observations.ToArray(),
            population.EnemyCharacters, problems, failedOutfits);
    }

    /// <summary>The row to keep for an outfit whose measurement is still current, or null when there is
    /// none. Three tests, none sufficient alone and NONE of them about packaging:
    ///
    /// <list type="bullet">
    /// <item><b>Shape</b> — <see cref="SubjectFingerprint"/>: the outfit still resolves by the same route,
    /// over the same set of logical bundles. A closure that gained a bundle can feed the measurement
    /// something it never saw even when every bundle the row recorded stands still.</item>
    /// <item><b>Resolution</b>: every catalog-resolved part address still names the same logical owner.
    /// This catches a mesh moving to a new bundle while the old bundle remains untouched.</item>
    /// <item><b>Content</b> — <see cref="BundleReads"/>: every bundle the measurement depended on is still
    /// holding what it held. A mesh owner resolves catalog-wide and can sit outside the scope entirely, and
    /// a bundle's bytes can be rewritten under a logical id the catalog still spells the same.</item>
    /// </list>
    ///
    /// <para>A bundle that merely re-minted — new internalId, new physical file, same content — passes
    /// both, which is the point: that is what a repack does to every bundle in the game, and a measurement
    /// that could not survive it would have to be taken again from scratch after every patch.</para>
    ///
    /// <para>A duplicate door keeps its row like any other — the filter is derived, so a door that stops
    /// being one after the catalog moves comes back without a read.</para></summary>
    private Observation? ReusableRow(string character, string stem, string fingerprint,
        IReadOnlyDictionary<string, string> readKeys, IReadOnlyDictionary<string, string> resolutionKeys)
    {
        if (!_ordinalByKey.TryGetValue(Key(character, stem), out var ord)) return null;
        var row = _observations[ord];
        if (row.Fingerprint != fingerprint) return null;
        if (!PartAddressResolutions.StillCurrent(resolutionKeys, row.AddressResolutions)) return null;
        if (row.Reads.Length == 0) return null;
        return BundleReads.StillCurrent(readKeys, row.Reads) ? row : null;
    }

    /// <summary>Whether this index's rows are exactly <paramref name="other"/>'s — same catalog version,
    /// same outfits in the same order, same observations, same failures. What a delta pass that changed
    /// nothing asks before rewriting the cache: the file on disk already says this, and rewriting it every
    /// launch is churn.</summary>
    public bool SameRowsAs(SharingIndex other)
    {
        if (!string.Equals(CatalogVersion, other.CatalogVersion, StringComparison.Ordinal)) return false;
        if (_wearers.Length != other._wearers.Length) return false;
        if (FailedOutfits.Count != other.FailedOutfits.Count) return false;
        for (int i = 0; i < _wearers.Length; i++)
        {
            if (Key(_wearers[i].Character, _wearers[i].Stem)
                != Key(other._wearers[i].Character, other._wearers[i].Stem)) return false;
            var a = _observations[i];
            var b = other._observations[i];
            if (a.Fingerprint != b.Fingerprint || a.Reads != b.Reads
                || a.AddressResolutions != b.AddressResolutions
                || !a.Mesh.SequenceEqual(b.Mesh, StringComparer.Ordinal)
                || !a.Tex.SequenceEqual(b.Tex, StringComparer.Ordinal)
                || !a.WitnessCandidates.SequenceEqual(b.WitnessCandidates, StringComparer.Ordinal))
                return false;
        }
        for (int i = 0; i < FailedOutfits.Count; i++)
            if (!string.Equals(FailedOutfits[i], other.FailedOutfits[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>Test seam: an index from pre-derived wearer maps. The maps are inverted back into the
    /// per-outfit rows the index is really made of, so a fixture states relations and the derivation still
    /// runs for real.</summary>
    internal static SharingIndex FromMeasurements(string catalogVersion, IReadOnlyList<Wearer> wearers,
        Dictionary<string, int[]> tex, Dictionary<string, int[]> mesh, Dictionary<int, string[]> witnesses,
        IReadOnlyList<string>? failedOutfits = null, IReadOnlyCollection<string>? enemyCharacters = null)
    {
        var meshOf = new List<string>[wearers.Count];
        var texOf = new List<string>[wearers.Count];
        for (int i = 0; i < wearers.Count; i++) { meshOf[i] = new List<string>(); texOf[i] = new List<string>(); }
        foreach (var (hash, ords) in mesh) foreach (var o in ords) meshOf[o].Add(hash);
        foreach (var (hash, ords) in tex) foreach (var o in ords) texOf[o].Add(hash);
        var rows = new Observation[wearers.Count];
        for (int i = 0; i < wearers.Count; i++)
        {
            var candidates = witnesses.TryGetValue(i, out var w) ? w : Array.Empty<string>();
            // A stated witness is a mesh of its outfit, whether or not the fixture also listed it as one.
            foreach (var ib in candidates) if (!meshOf[i].Contains(ib)) meshOf[i].Add(ib);
            // No fingerprint and an empty read record: a hand-stated row is not one a pass may reuse, and
            // an outfit's real fingerprint is a hash, never the empty string.
            rows[i] = new Observation("", meshOf[i], texOf[i], candidates, "", "");
        }
        return new(catalogVersion, wearers.ToArray(), rows, enemyCharacters ?? Array.Empty<string>(),
            Array.Empty<string>(), failedOutfits ?? Array.Empty<string>());
    }

    // ---- persistence ----------------------------------------------------------------------------------

    // INVARIANT: nothing written here is a game-derived string. Every roster name goes through
    // NameKey, the bundle ids behind a read record go through it too, the asset identities are already
    // content hashes, and the diagnostic text that named stems is not persisted at all. That is what makes
    // one machine's measurement shippable to every other install — and it holds only as long as each new
    // field is checked against it.
    // R is what the row's bundles held when it was measured; A is its catalog-resolved part addresses.
    // Every row has both; a file whose row is missing either was not written by this schema's complete
    // shape, and that row is dropped at load rather than trusted.
    private sealed record OutfitRow(string K, string F, List<string> M, List<string> T, List<string> W,
        string? R, string? A);
    private sealed record CacheFile(int SchemaVersion, string CatalogVersion,
        List<OutfitRow> Outfits, List<string> Failed);

    /// <summary>What one cache-file load found, for diagnostics at the shipped-seed boundary. The row
    /// counts describe the file before and after its name-key join; a foreign schema drops every file row
    /// before that join. <see cref="Problem"/> is diagnostic-only and never changes the loader's safe
    /// absent-on-failure contract.</summary>
    public readonly record struct LoadReport(bool Exists, int? StatedSchema, bool SchemaAccepted,
        int RowsLoaded, int RowsJoined, int RowsDropped, string? Problem);

    /// <summary>The index in <paramref name="path"/>, re-joined to <paramref name="population"/>'s own
    /// names: a row whose key matches no roster subject is dropped, which is how a subject the catalog no
    /// longer has leaves. Null when the file is absent, unreadable, or written by another schema.
    ///
    /// <para><see cref="CatalogVersion"/> comes from the FILE, not the caller — a file measured under an
    /// older catalog is still the base a delta pass builds on, and only the caller knows whether it is
    /// looking at the current one.</para></summary>
    public static SharingIndex? TryLoad(string path, SharingPopulation population) =>
        TryLoad(path, population, out _);

    /// <summary><see cref="TryLoad(string, SharingPopulation)"/> plus the shape facts an app log needs to
    /// distinguish an accepted seed from a silent schema/row refusal.</summary>
    public static SharingIndex? TryLoad(string path, SharingPopulation population, out LoadReport report)
    {
        report = new LoadReport(false, null, false, 0, 0, 0, "the file is absent");
        if (!File.Exists(path)) return null;
        try
        {
            var cf = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (cf is null)
            {
                report = new LoadReport(true, null, false, 0, 0, 0,
                    "the file has no measurement object");
                return null;
            }
            int loaded = cf.Outfits?.Count ?? 0;
            if (cf.SchemaVersion != SchemaVersion)
            {
                report = new LoadReport(true, cf.SchemaVersion, false, loaded, 0, loaded,
                    $"this app reads schema {SchemaVersion}");
                return null;
            }
            if (cf.Outfits is null || cf.Failed is null)
            {
                report = new LoadReport(true, cf.SchemaVersion, true, loaded, 0, loaded,
                    "the file is missing its outfit or failure rows");
                return null;
            }

            var byKey = new Dictionary<string, (Character C, Outfit O)>(StringComparer.Ordinal);
            int subjects = 0;
            foreach (var c in population.Roster)
                foreach (var o in c.Outfits) { byKey[NameKey.Of(Key(c.Name, o.Stem))] = (c, o); subjects++; }
            // Two subjects on one key would attach one outfit's measurement to another. Refuse the whole
            // file instead: measuring afresh is slow, wrong reach is silent.
            if (byKey.Count != subjects)
            {
                report = new LoadReport(true, cf.SchemaVersion, true, loaded, 0, loaded,
                    "the current roster has duplicate measurement keys");
                return null;
            }

            var wearers = new List<Wearer>();
            var observations = new List<Observation>();
            foreach (var row in cf.Outfits)
            {
                if (!byKey.TryGetValue(row.K, out var hit)) continue;
                // A row missing either reuse record cannot be gated at that grain. Drop it so its outfit
                // measures again like any uncovered one.
                if (row.R is null || row.A is null) continue;
                wearers.Add(new Wearer(hit.C.Name, hit.C.DisplayName, hit.O.Stem, hit.O.DisplayName));
                observations.Add(new Observation(row.F, row.M, row.T, row.W, row.R, row.A));
            }
            var failed = new List<string>();
            foreach (var k in cf.Failed)
                if (byKey.TryGetValue(k, out var hit)) failed.Add(Key(hit.C.Name, hit.O.Stem));

            report = new LoadReport(true, cf.SchemaVersion, true, loaded, wearers.Count,
                loaded - wearers.Count, null);
            return new SharingIndex(cf.CatalogVersion, wearers.ToArray(), observations.ToArray(),
                population.EnemyCharacters, Array.Empty<string>(), failed);
        }
        catch (Exception e)
        {
            report = new LoadReport(true, null, false, 0, 0, 0,
                $"the file is unreadable ({e.GetType().Name})");
            return null;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rows = new List<OutfitRow>(_wearers.Length);
        for (int i = 0; i < _wearers.Length; i++)
            rows.Add(new OutfitRow(NameKey.Of(Key(_wearers[i].Character, _wearers[i].Stem)),
                _observations[i].Fingerprint,
                _observations[i].Mesh.ToList(), _observations[i].Tex.ToList(),
                _observations[i].WitnessCandidates.ToList(), _observations[i].Reads,
                _observations[i].AddressResolutions));
        var failed = FailedOutfits.Select(NameKey.Of).ToList();

        // Atomic publish through a UNIQUE temp: a fixed name is one shared file, so two saves racing over
        // it publish a half-written index.
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new CacheFile(SchemaVersion, CatalogVersion, rows, failed)));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }
}
