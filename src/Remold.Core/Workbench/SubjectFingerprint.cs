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
/// before it reads anything. Both bundle namespaces go in: logical ids and internalIds are hash-named and
/// independently minted, so a bundle whose content moved changes at least one of them.</para>
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
/// <para><b>Shape.</b> One hex string per row: the read bundles' pairs, each the bundle's own key followed
/// by the key of the manifest internalId the catalog joins it to, sorted and concatenated. Both namespaces
/// go in for the same reason the fingerprint takes both — they are independently minted, so a bundle whose
/// content moved changes at least one. Keys rather than names, because the row ships to other installs and
/// carries no game-derived string.</para>
///
/// <para><b>Catalog-only.</b> The currency check is a dictionary lookup per recorded bundle against
/// <see cref="CurrentKeys"/>, so a pass decides what to re-read before it opens anything.</para>
/// </summary>
public static class BundleReads
{
    /// <summary>One key's length, and so the offset of the internalId half inside a pair.</summary>
    private const int KeyLength = 16;
    private const int PairLength = KeyLength * 2;

    /// <summary>The internalId key a bundle the catalog does not map carries. A bundle can leave the map
    /// (it becomes ambiguous, or the row goes) without leaving the catalog, and that is a move like any
    /// other — so the absent case gets a value rather than being skipped.</summary>
    private static readonly string Unmapped = NameKey.Of("");

    /// <summary>Bundle ids are catalog-produced and compared case-insensitively there, so the key is taken
    /// over one casing on both sides.</summary>
    private static string BundleKey(string bundleId) => NameKey.Of(bundleId.ToLowerInvariant());

    /// <summary>The record for the bundles <paramref name="bundleIds"/> names, under
    /// <paramref name="catalog"/> as it stands now.</summary>
    public static string Of(CatalogIndex catalog, IEnumerable<string> bundleIds)
    {
        var pairs = bundleIds
            .Select(b => BundleKey(b) + NameKey.Of(
                catalog.BundleNameToInternalId.TryGetValue(b, out var internalId) ? internalId : ""))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        pairs.Sort(StringComparer.Ordinal);
        return string.Concat(pairs);
    }

    /// <summary>Bundle key → internalId key over the whole catalog: what a recorded read set is checked
    /// against. Built once per pass, since every row asks the same map.</summary>
    public static IReadOnlyDictionary<string, string> CurrentKeys(CatalogIndex catalog)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in catalog.BundleNameToInternalId) map[BundleKey(kv.Key)] = NameKey.Of(kv.Value);
        return map;
    }

    /// <summary>Whether every bundle <paramref name="reads"/> records still joins to the same internalId.
    /// A record whose length is not a whole number of pairs is not one this code wrote, and reads as
    /// moved — the row is measured again rather than trusted.</summary>
    public static bool StillCurrent(IReadOnlyDictionary<string, string> currentKeys, string reads)
    {
        if (reads.Length % PairLength != 0) return false;
        for (int i = 0; i < reads.Length; i += PairLength)
        {
            var bundle = reads.Substring(i, KeyLength);
            var expected = reads.Substring(i + KeyLength, KeyLength);
            if ((currentKeys.TryGetValue(bundle, out var now) ? now : Unmapped) != expected) return false;
        }
        return true;
    }
}
