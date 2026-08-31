using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Remold.Core.Bundles;
using Remold.Core.Model;

namespace Remold.Core.Workbench;

/// <summary>
/// The SHAPE grain of the reuse test: the route that names one subject, plus the set of bundles its
/// measurement is allowed to reach (<see cref="SubjectScope.ScopeBundles"/> — the prefab hit plus its
/// dependency closure). Everything here is a LOGICAL name — a catalog address, a logical bundle id, a
/// container root — and nothing here is packaging identity.
///
/// <para><b>Why logical only.</b> A manifest internalId is minted by the packer: for a single-file bundle
/// it IS the physical content hash of the file it names, so every internalId in a subject's closure
/// re-mints when the game is repacked, whether or not one byte of the subject changed. A fingerprint
/// joined to internalIds therefore dies wholesale on any repack — measured over one real patch gap
/// (catalog 26109 → 26932), 6 of 510 subjects' fingerprints survived while their mesh sets were identical
/// for 504 of 510. Persisting a measurement ACROSS patches is the entire point of the shipped seed, so
/// packaging identity is out and this answers one question only: <b>does the subject still resolve the
/// same way, over the same set of bundles?</b></para>
///
/// <para><b>What it does NOT answer:</b> whether a part address outside that scope retargeted, or whether
/// content behind a bundle moved. Those are <see cref="PartAddressResolutions"/>'s and
/// <see cref="BundleReads"/>'s questions. All three form the reuse gate; none is sufficient alone.</para>
///
/// <para><b>A set, not a list.</b> The scope's ORDER comes out of the packer's dependency array, so a
/// reordering is packaging churn like a re-mint. Membership is the catalog fact — a closure that gained
/// or lost a bundle can feed the measurement something it never saw, even when every bundle it recorded
/// stands still. The residual cost is stated where it bites: a pure REORDER that changes which of two
/// unchanged prefab bundles wins the candidate walk is not seen here (the winner's own content record
/// would have to move for the row to notice).</para>
///
/// <para><b>Catalog-only.</b> No bundle is opened, so a pass can decide WHICH subjects need re-reading
/// before it reads anything.</para>
///
/// <para><b>Game data only.</b> What the app derives from its own CODE — a mesh prefix, a token rule, the
/// part-ownership test — is deliberately outside: this answers "has the game moved under this subject",
/// and a change on the app's side is a change to what the measurement MEANS, which the sharing schema
/// version is what invalidates.</para>
///
/// <para>Comparable only against a fingerprint taken the same way. A subject the catalog cannot place
/// (blacklisted, or an address the catalog does not name) still yields a stable fingerprint over its route
/// alone — an empty scope is a fact about the catalog like any other.</para>
/// </summary>
public static class SubjectFingerprint
{
    /// <summary>The fingerprint for one outfit under <paramref name="catalog"/>.</summary>
    public static string For(CatalogIndex catalog, Outfit outfit)
    {
        // The scope's deobfuscate delegate is held only for its LAZY prefab/CAB reads; ScopeBundles is
        // resolved from the catalog during Build, so no read is ever attempted here.
        var scope = SubjectScope.Build(catalog, static _ => null, outfit);
        var sb = new StringBuilder();
        if (outfit.Route is { } route)
            sb.Append(route.Address).Append('|').Append(route.Bundle).Append('|').Append(route.RootName)
              .Append('|').Append(string.Join(",", route.ExtraBundles)).Append('\n');
        var bundles = scope.ScopeBundles.Distinct(StringComparer.Ordinal).ToList();
        bundles.Sort(StringComparer.Ordinal);
        foreach (var bundle in bundles) sb.Append(bundle).Append('\n');
        return NameKey.Of(sb.ToString());
    }
}

/// <summary>
/// The RESOLUTION grain of the reuse test: which logical bundle each part-tier address resolved to when the
/// row was measured. A mesh owner is catalog-wide and can move between logical bundles without changing
/// the subject's dependency closure or the content of any bundle the old row read.
///
/// <para><b>Shape.</b> One fixed-width pair per resolved address, sorted and concatenated: a key over the
/// catalog's own address key, then a key over the logical owner bundle. Neither game-derived string lands
/// in a persisted row.</para>
///
/// <para>Only addresses the measurement actually resolves participate. A renderer that already carries an
/// embedded bundle and path id bypasses <see cref="CatalogIndex.ResolveAddress"/>; its prefab and bundle
/// content reads are the facts that gate it instead.</para>
/// </summary>
internal static class PartAddressResolutions
{
    private const int KeyLength = 16;
    private const int PairLength = KeyLength * 2;

    private static string AddressKey(string catalogAddressKey) => NameKey.Of(catalogAddressKey);
    private static string OwnerKey(string owner) => NameKey.Of(owner.ToLowerInvariant());

    /// <summary>The record for the successful address resolutions used to measure one outfit.</summary>
    internal static string Of(IEnumerable<(string Address, string Owner)> resolutions)
    {
        var pairs = resolutions
            .Select(r => AddressKey(CatalogIndex.KeyForAddress(r.Address)) + OwnerKey(r.Owner))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        pairs.Sort(StringComparer.Ordinal);
        return string.Concat(pairs);
    }

    /// <summary>Address key to logical-owner key over the current catalog; no bundle is opened.</summary>
    internal static IReadOnlyDictionary<string, string> CurrentKeys(CatalogIndex catalog)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in catalog.AddressOwners)
            map[AddressKey(row.Key)] = OwnerKey(row.Value);
        return map;
    }

    /// <summary>Whether every recorded address still resolves to the same logical owner.</summary>
    internal static bool StillCurrent(IReadOnlyDictionary<string, string> currentKeys, string resolutions)
    {
        if (resolutions.Length % PairLength != 0) return false;
        for (int i = 0; i < resolutions.Length; i += PairLength)
        {
            var address = resolutions.Substring(i, KeyLength);
            var expected = resolutions.Substring(i + KeyLength, KeyLength);
            if (!currentKeys.TryGetValue(address, out var owner) || owner != expected) return false;
        }
        return true;
    }
}

/// <summary>
/// The CONTENT grain of the reuse test: what the bundles one subject's measurement actually read were
/// holding when it read them, recorded on its row.
///
/// <para><b>Why it exists.</b> A part's mesh resolves through <see cref="CatalogIndex.ResolveAddress"/>,
/// which is catalog-WIDE: the owner bundle can sit outside the subject's dependency closure, so
/// <see cref="SubjectFingerprint"/> never sees it at all. And a bundle's bytes can be rewritten where they
/// stand under a logical id the catalog still spells the same — release-day polish of an already-shipped
/// character does exactly that. A row kept without this check would go on reporting hashes of content that
/// is no longer there.</para>
///
/// <para><b>Shape.</b> One hex string per row: the read bundles' PAIRS, sorted and concatenated. Each is
/// the logical bundle's own key, then the key of the CONTENT HASH the manifest's stub carries for whatever
/// internalId the catalog currently joins that bundle to. Keys rather than names, because the row ships to
/// other installs and carries no game-derived string.</para>
///
/// <para><b>The internalId is deliberately NOT in the pair.</b> It was, and it only ever produced false
/// invalidations: an internalId is minted by the packer (for a single-file bundle it IS the physical
/// content hash of the file), so a repack re-mints every one of them while the content behind them is
/// untouched. A re-mint with unchanged content is precisely the case that has to survive a game update,
/// and content that genuinely changed already fails the hash half. So the pair asks the only question
/// worth asking: <b>is this logical bundle still holding what it held?</b></para>
///
/// <para><b>Which hash, and what is measured about it.</b> A 40-byte GFF stub carries two 16-byte hashes:
/// the physical FILENAME the bundle lives in, and — at offset 24 — the one this record keys on. Measured
/// on a live install: it is distinct across all 53,117 manifest entries, and it equals the MD5 of the
/// bundle's DEOBFUSCATED bytes for all 40 sampled across whole-file singles and packed slices — while the
/// MD5 of the RAW on-disk bytes matched none of the 40, which is what says it describes the CONTENT and
/// not the file. So it moves exactly when a bundle's content moves, and a patch that merely REPACKS the
/// same content into different physical files leaves it — and every row that read it — alone.</para>
///
/// <para><b>Not the physical filename</b>, which was the first thing tried here and cannot do this job: a
/// single-file bundle's manifest entry name IS its physical hash plus <c>.bundle</c> — measured over all
/// 7,258 singles on a live install, without exception — so the filename is packaging identity twice over
/// and the rewrite-in-place case walks straight through it.</para>
///
/// <para><b>Catalog-only.</b> The currency check is a dictionary lookup per recorded bundle against
/// <see cref="CurrentKeys"/>, whose own inputs are catalog dictionary hits and in-memory manifest stub
/// reads, so a pass decides what to re-read before it opens anything.</para>
/// </summary>
public static class BundleReads
{
    /// <summary>One key's length, and so the offset of the content key inside a pair.</summary>
    private const int KeyLength = 16;
    /// <summary>Bundle key, content-hash key.</summary>
    private const int PairLength = KeyLength * 2;

    /// <summary>The content key for a bundle the catalog names but whose content hash cannot be minted —
    /// an internalId the manifest does not name, or a lookup that threw. An absent hash is a state, never a
    /// substitute one, so it is recorded rather than skipped.</summary>
    private static readonly string NoContentHash = NameKey.Of("");

    /// <summary>The content key for a bundle the CATALOG does not name at all — deliberately distinct from
    /// <see cref="NoContentHash"/>, so a bundle leaving the catalog is a move even in the case where no
    /// content hash was resolvable for it either. Both sides of the check use this same constant.
    ///
    /// <para>The leading NUL is what puts the marker outside the space any real content hash could reach,
    /// and it is spelled as an ESCAPE on purpose: a raw NUL byte in the source makes the whole file binary
    /// to every tool that reads the repo as text — grep, and git's own end-of-line normalization, which is
    /// how this one file came to be committed with endings none of its siblings have. The escape compiles
    /// to the same string, so the key does not move, and it is pinned by VALUE in
    /// <c>SharingSeedTests.The_absent_catalog_marker_is_the_key_every_persisted_row_already_carries</c> —
    /// every row on disk, the shipped seed's included, records this exact key for a bundle that left the
    /// catalog, so a spelling that moved it would silently re-measure the whole population.</para></summary>
    private static readonly string Unnamed = NameKey.Of("\u0000not-in-catalog");

    /// <summary>Bundle ids are catalog-produced and compared case-insensitively there, so the key is taken
    /// over one casing on both sides.</summary>
    private static string BundleKey(string bundleId) => NameKey.Of(bundleId.ToLowerInvariant());

    /// <summary>The internalId → content-hash lookup <see cref="Of"/> and <see cref="CurrentKeys"/> take,
    /// over a game install's VFS manifest; null for an internalId the manifest does not name. The hash is
    /// the stub's own <see cref="GffManifest.Stub.SubHash"/>, hex — the content identity, NOT the physical
    /// filename beside it (see this class's summary for why the filename cannot serve). Dictionary and
    /// in-memory stub reads only — no bundle is opened, which is what keeps the plan pass catalog-only.
    /// </summary>
    public static Func<string, string?> ContentHashLookup(GffManifest manifest) =>
        internalId => manifest.TryLocate(internalId, out var located)
            ? Convert.ToHexString(located.Stub.SubHash).ToLowerInvariant()
            : null;

    /// <summary>The same content hash reached from a LOGICAL bundle id — the id every catalog resolve and
    /// every build read names a bundle by — by way of the internalId the catalog maps it to. Null for a
    /// bundle either lookup does not name, and for a read that throws: a hash that cannot be minted is an
    /// absent hash, never a substitute one. Dictionary and in-memory stub reads only; no bundle is opened.
    /// </summary>
    public static Func<string, string?> BundleContentHashLookup(CatalogIndex catalog, GffManifest manifest)
    {
        var byInternalId = ContentHashLookup(manifest);
        return bundleId => ContentOf(catalog, byInternalId, bundleId);
    }

    /// <summary>The content hash a LOGICAL bundle id resolves to right now — the catalog's internalId join
    /// followed by <paramref name="contentHashOf"/> — or null when either side does not name it. THE home
    /// for that two-step join: the read record, the plan pass's current-key map and the sharing pass's
    /// observation memo all address a bundle's content through this one route, so none of them can key on
    /// a different identity than the others.</summary>
    public static string? ContentOf(CatalogIndex catalog, Func<string, string?> contentHashOf,
        string bundleId)
    {
        try
        {
            return catalog.BundleNameToInternalId.TryGetValue(bundleId, out var internalId)
                ? contentHashOf(internalId) : null;
        }
        catch { return null; }
    }

    /// <summary>The content key a single bundle carries right now.
    ///
    /// <para>A bundle the catalog does not name takes <see cref="Unnamed"/> outright rather than asking the
    /// delegate about an empty internalId: <see cref="StillCurrent"/> compares such a bundle against that
    /// same constant, and a delegate that answered anything for <c>""</c> would otherwise write rows no
    /// check could ever call current.</para></summary>
    private static string Tail(CatalogIndex catalog, Func<string, string?> contentHashOf, string bundleId)
    {
        if (!catalog.BundleNameToInternalId.TryGetValue(bundleId, out var internalId)) return Unnamed;
        return NameKey.Of(contentHashOf(internalId) ?? "");
    }

    /// <summary>The record for the bundles <paramref name="bundleIds"/> names, under
    /// <paramref name="catalog"/> and <paramref name="contentHashOf"/> as they stand now.</summary>
    public static string Of(CatalogIndex catalog, Func<string, string?> contentHashOf,
        IEnumerable<string> bundleIds)
    {
        var pairs = bundleIds
            .Select(b => BundleKey(b) + Tail(catalog, contentHashOf, b))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        pairs.Sort(StringComparer.Ordinal);
        return string.Concat(pairs);
    }

    /// <summary>Bundle key → its content key, over the whole catalog: what a recorded read set is checked
    /// against. Built once per pass, since every row asks the same map.</summary>
    public static IReadOnlyDictionary<string, string> CurrentKeys(CatalogIndex catalog,
        Func<string, string?> contentHashOf)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in catalog.BundleNameToInternalId)
            map[BundleKey(kv.Key)] = NameKey.Of(contentHashOf(kv.Value) ?? "");
        return map;
    }

    /// <summary>Whether every bundle <paramref name="reads"/> records still holds the same content. A
    /// record whose length is not a whole number of pairs is not one this code wrote, and reads as moved —
    /// the row is measured again rather than trusted.</summary>
    public static bool StillCurrent(IReadOnlyDictionary<string, string> currentKeys, string reads)
    {
        if (reads.Length % PairLength != 0) return false;
        for (int i = 0; i < reads.Length; i += PairLength)
        {
            var bundle = reads.Substring(i, KeyLength);
            var expected = reads.Substring(i + KeyLength, KeyLength);
            if ((currentKeys.TryGetValue(bundle, out var now) ? now : Unnamed) != expected)
                return false;
        }
        return true;
    }
}
