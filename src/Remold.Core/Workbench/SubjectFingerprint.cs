using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Remold.Core.Bundles;
using Remold.Core.Model;

namespace Remold.Core.Workbench;

/// <summary>
/// The identity of everything one subject's measurement can read: the catalog scope
/// (<see cref="SubjectScope.ScopeBundles"/> — the prefab hit plus its dependency closure), each scope
/// bundle joined to the manifest internalId the catalog maps it to, and the route that put the subject
/// there.
///
/// <para><b>Catalog-only.</b> No bundle is opened, so a pass can decide WHICH subjects need re-reading
/// before it reads anything. Both bundle namespaces go in, since either can re-mint without the other.
/// Neither is CONTENT identity: a game update can rewrite a bundle's bytes in place under an unchanged
/// logical id and internalId. So this fingerprint answers "does the subject's catalog scope still have
/// the same shape", and <see cref="BundleReads"/> — which carries the content hash behind each bundle a
/// measurement read — is what answers "is the content behind it still the same".</para>
///
/// <para><b>Game data only.</b> What the app derives from its own CODE — a mesh prefix, a token rule, the
/// part-ownership test — is deliberately outside: a fingerprint answers "has the game moved under this
/// subject", and a change on the app's side is a change to what the measurement MEANS, which the sharing
/// schema version is what invalidates.</para>
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
        foreach (var bundle in scope.ScopeBundles)
            sb.Append(bundle).Append('>')
              .Append(catalog.BundleNameToInternalId.TryGetValue(bundle, out var internalId) ? internalId : "")
              .Append('\n');
        return NameKey.Of(sb.ToString());
    }
}

/// <summary>
/// The identity of the bundles one subject's measurement ACTUALLY read, recorded on its row so the reuse
/// test covers what the scope fingerprint cannot see.
///
/// <para><b>Why it exists.</b> A part's mesh resolves through <see cref="CatalogIndex.ResolveAddress"/>,
/// which is catalog-WIDE: the owner bundle can sit outside the subject's dependency closure, so it can
/// re-mint without moving <see cref="SubjectFingerprint"/>. A row reused on the fingerprint alone would
/// then carry hashes of content that has moved.</para>
///
/// <para><b>Shape.</b> One hex string per row: the read bundles' triples, sorted and concatenated. Each is
/// the bundle's own key, then the key of the manifest internalId the catalog joins it to, then the key of
/// the CONTENT HASH the manifest's stub carries for that internalId. The two name namespaces catch a bundle
/// that re-minted; the content hash catches one rewritten under names that both survive — release-day polish
/// of an already-shipped character does exactly that, and without this key a row of stale mesh hashes reads
/// as current. Keys rather than names, because the row ships to other installs and carries no game-derived
/// string.</para>
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
/// 7,258 singles on a live install, without exception — so for a single the filename key only restates the
/// internalId key beside it, and the rewrite-in-place case walks straight through both.</para>
///
/// <para><b>Catalog-only.</b> The currency check is a dictionary lookup per recorded bundle against
/// <see cref="CurrentKeys"/>, whose own inputs are catalog dictionary hits and in-memory manifest stub
/// reads, so a pass decides what to re-read before it opens anything.</para>
/// </summary>
public static class BundleReads
{
    /// <summary>One key's length, and so the offset of the internalId key inside a triple.</summary>
    private const int KeyLength = 16;
    /// <summary>Bundle key, internalId key, content-hash key.</summary>
    private const int TripleLength = KeyLength * 3;
    /// <summary>The internalId and content keys together: what a bundle key resolves to on both sides of
    /// the check, so the comparison is one string equality per recorded bundle.</summary>
    private const int TailLength = KeyLength * 2;

    /// <summary>The key an absent half carries — a bundle the catalog does not map, or an internalId the
    /// manifest does not name. Either can happen without the bundle leaving the catalog, and that is a move
    /// like any other, so the absent case gets a value rather than being skipped.</summary>
    private static readonly string Absent = NameKey.Of("");

    /// <summary>What a bundle key absent from the current map resolves to: both halves absent.</summary>
    private static readonly string AbsentTail = Absent + Absent;

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

    /// <summary>The keys a single bundle resolves to right now: its internalId under
    /// <paramref name="catalog"/>, and the content <paramref name="contentHashOf"/> resolves that
    /// internalId to.
    ///
    /// <para>A bundle the catalog does not name takes <see cref="AbsentTail"/> outright rather than asking
    /// the delegate about an empty internalId: <see cref="StillCurrent"/> compares such a bundle against
    /// that same constant, and a delegate that answered anything for <c>""</c> would otherwise write rows
    /// no check could ever call current.</para></summary>
    private static string Tail(CatalogIndex catalog, Func<string, string?> contentHashOf, string bundleId)
    {
        if (!catalog.BundleNameToInternalId.TryGetValue(bundleId, out var internalId)) return AbsentTail;
        return NameKey.Of(internalId) + NameKey.Of(contentHashOf(internalId) ?? "");
    }

    /// <summary>The record for the bundles <paramref name="bundleIds"/> names, under
    /// <paramref name="catalog"/> and <paramref name="contentHashOf"/> as they stand now.</summary>
    public static string Of(CatalogIndex catalog, Func<string, string?> contentHashOf,
        IEnumerable<string> bundleIds)
    {
        var triples = bundleIds
            .Select(b => BundleKey(b) + Tail(catalog, contentHashOf, b))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        triples.Sort(StringComparer.Ordinal);
        return string.Concat(triples);
    }

    /// <summary>Bundle key → its internalId and content keys, over the whole catalog: what a recorded read
    /// set is checked against. Built once per pass, since every row asks the same map.</summary>
    public static IReadOnlyDictionary<string, string> CurrentKeys(CatalogIndex catalog,
        Func<string, string?> contentHashOf)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in catalog.BundleNameToInternalId)
            map[BundleKey(kv.Key)] = NameKey.Of(kv.Value) + NameKey.Of(contentHashOf(kv.Value) ?? "");
        return map;
    }

    /// <summary>Whether every bundle <paramref name="reads"/> records still joins to the same internalId
    /// AND the same content hash. A record whose length is not a whole number of triples is not one this
    /// code wrote, and reads as moved — the row is measured again rather than trusted.</summary>
    public static bool StillCurrent(IReadOnlyDictionary<string, string> currentKeys, string reads)
    {
        if (reads.Length % TripleLength != 0) return false;
        for (int i = 0; i < reads.Length; i += TripleLength)
        {
            var bundle = reads.Substring(i, KeyLength);
            var expected = reads.Substring(i + KeyLength, TailLength);
            if ((currentKeys.TryGetValue(bundle, out var now) ? now : AbsentTail) != expected)
                return false;
        }
        return true;
    }
}
