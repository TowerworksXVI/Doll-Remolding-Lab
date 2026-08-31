using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Model;

namespace Remold.Core.Workbench;

/// <summary>One assembly prefab the scope resolved: its container-root name, the logical bundle it was
/// read from, the deobfuscated bytes (reused so <see cref="BundleReader"/>'s parse cache hits), and the
/// parsed prefab.</summary>
public sealed record SubjectCandidate(string Root, string Bundle, byte[] Dec, CharacterPrefab Prefab);

/// <summary>
/// The per-subject resolution scope: the bundles the game itself can load for one subject — the formula
/// prefab copies (<see cref="GameVfs.PrefabAddress"/> over <see cref="GameVfs.ContextRoots"/>) plus each hit
/// row's full catalog dependency closure. Everything a subject references resolves inside this set, so the
/// CAB→bundle join is scope-bounded instead of corpus-wide.
///
/// <para><b>Curated routes.</b> An outfit carrying a <see cref="SubjectRoute"/> replaces the formula: one
/// explicit address (same catalog mechanics, closure included), or one logical bundle read directly at a
/// named container root. The direct-bundle shape has NO dependency closure available, so its scope is that
/// single bundle — see <see cref="SubjectRoute"/> for what that costs.</para>
///
/// <para><b>Blacklist.</b> A blacklisted stem builds an EMPTY scope — no candidates, every CAB unresolvable.
/// Do not remove or weaken it.</para>
///
/// <para><b>Loudness.</b> A bundle whose prefab PARSE throws is skipped but recorded in
/// <see cref="Problems"/>, never silently. Deobfuscation exceptions are NOT swallowed here — the
/// caller-supplied delegate owns that contract, so an <see cref="System.IO.IOException"/> while the game
/// runs propagates as BUSY to the shell's retry.</para>
///
/// <para>Build one per subject per operation; it holds the deobfuscate delegate and a private
/// <see cref="BundleReader"/>, and is NOT thread-safe.</para>
/// </summary>
public sealed class SubjectScope
{
    private readonly Func<string, byte[]?> _tryDeobfuscate;
    private readonly BundleReader _reader = new();
    private readonly List<string> _problems = new();
    private readonly HashSet<string> _hitSet;
    private readonly IReadOnlyList<string> _hitBundles;
    private readonly Outfit _outfit;
    private readonly RosterFillCache? _fillCache;

    private List<SubjectCandidate>? _candidates;
    private Dictionary<string, VisibilityOverride>? _visibility;
    private List<string>? _visibilityContributors;
    // lazy CAB walk state: bundles [0.._nextToInspect) have had their CAB name read and cached
    private readonly Dictionary<string, string> _cabToBundle = new(StringComparer.Ordinal);
    private int _nextToInspect;
    // non-null only on a CURATED route (either shape): the ONE container root that is this subject
    private readonly string? _rootName;

    private SubjectScope(IReadOnlyList<string> hitBundles, IReadOnlyList<string> scopeBundles,
        Func<string, byte[]?> tryDeobfuscate, Outfit outfit, string? rootName = null,
        RosterFillCache? fillCache = null)
    {
        _hitBundles = hitBundles;
        _hitSet = new HashSet<string>(hitBundles, StringComparer.Ordinal);
        ScopeBundles = scopeBundles;
        _tryDeobfuscate = tryDeobfuscate;
        _outfit = outfit;
        _rootName = rootName;
        _fillCache = fillCache;
    }

    /// <summary>The scope's bundles: context-hit prefab bundles in <see cref="GameVfs.ContextRoots"/>
    /// priority order, then each hit address's dependency-closure entries in catalog order, deduped.</summary>
    public IReadOnlyList<string> ScopeBundles { get; }

    /// <summary>The reader this scope's own bundle reads go through, for a caller whose reads are part of the
    /// SAME operation: one parse cache then serves both, instead of each opening every bundle again. Carries
    /// the scope's thread rule with it — one scope, one operation, one thread.</summary>
    public BundleReader Reader => _reader;

    /// <summary>Resolution failures inside the scope (a prefab or CAB read that threw), accrued as the
    /// lazy reads happen — fold into the caller's problem surface, never drop.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>True when the subject's OWN prefab source held a container root that the parse declined for
    /// carrying no readable slots. With no <see cref="Candidates"/>, this is what separates a subject whose
    /// prefab is absent from one whose prefab is present and yielded nothing. Answers only once
    /// <see cref="Candidates"/> has been read — the parses that set it are that walk's.
    ///
    /// <para>Scoped to the bundles the subject's own address or route resolves its prefab from. A
    /// dependency bundle's unrelated root says nothing about this subject: counting it would report a
    /// present-but-unreadable prefab for a subject that ships none.</para></summary>
    public bool DeclinedRoot { get; private set; }

    /// <summary>Resolve the scope for one subject. Takes the parsed catalog, not a <see cref="GameVfs"/>, so
    /// the mechanics are testable against a hand-authored catalog. <paramref name="tryDeobfuscate"/> is held
    /// for the lazy candidate/CAB reads — pass the operation's MEMOIZED delegate so deobfuscations and
    /// <see cref="BundleReader"/> parse caches are shared.</summary>
    public static SubjectScope Build(CatalogIndex catalog, Func<string, byte[]?> tryDeobfuscate, Outfit outfit,
        RosterFillCache? fillCache = null)
    {
        // FIRST, and on the STEM: a curated route must never become a way past the blacklist.
        if (RosterBlacklist.IsBlacklisted(outfit.Stem))
            return new SubjectScope(Array.Empty<string>(), Array.Empty<string>(), tryDeobfuscate, outfit,
                fillCache: fillCache);

        // A curated direct-bundle subject (see Model.SubjectRoute): the catalog names no address for this
        // prefab, so there is no dependency row to close over. The route's own ExtraBundles stand in for
        // that closure; anything the prefab reaches through an EXTERNAL CAB outside the set resolves to
        // nothing, which surfaces as the caller's loud per-part/per-material problem, never as a silent
        // blank. The root name is carried through because such a bundle can hold sibling roots and only
        // the named one is this subject. The catalog-names-the-bundle test is the same one the launch
        // existence gate applies (GameVfs.PrefabsFor), so the two agree about what a route reaches.
        if (outfit.Route is { Bundle: { } directBundle })
        {
            if (!catalog.BundleNameToInternalId.ContainsKey(directBundle))
                return new SubjectScope(Array.Empty<string>(), Array.Empty<string>(), tryDeobfuscate, outfit,
                    fillCache: fillCache);
            var direct = new List<string> { directBundle };
            var extras = new HashSet<string>(direct, StringComparer.Ordinal);
            foreach (var extra in outfit.Route.ExtraBundles)
                if (extras.Add(extra)) direct.Add(extra);
            return new SubjectScope(new[] { directBundle }, direct, tryDeobfuscate, outfit,
                outfit.Route.RootName, fillCache);
        }

        // A curated addressable subject resolves through exactly the same catalog mechanics as a formula
        // hit — owner plus dependency closure — over its one explicit address instead of the context roots.
        IEnumerable<string> addresses = outfit.Route?.Address is { } curated
            ? new[] { curated }
            : GameVfs.ContextRoots.Select(root => GameVfs.PrefabAddress(root, outfit.Stem));

        // A curated address pins its root too: the bundle behind it can hold sibling roots, and the
        // formula path's "first root that parses" default is only safe when address and root agree by
        // construction, which is exactly what a curated address does not guarantee.
        var hits = new List<string>();
        var scope = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var closures = new List<IReadOnlyList<string>>();
        foreach (var address in addresses)
        {
            if (catalog.ResolveAddress(address) is not { } owner) continue;
            if (seen.Add(owner)) { hits.Add(owner); scope.Add(owner); }
            if (catalog.DepsForAddress(address) is { } deps) closures.Add(deps);
        }
        foreach (var deps in closures)
            foreach (var b in deps)
                if (seen.Add(b)) scope.Add(b);
        return new SubjectScope(hits, scope, tryDeobfuscate, outfit, outfit.Route?.RootName, fillCache);
    }

    /// <summary>Every scope bundle that parses as an assembly prefab, deduped by ROOT NAME (first wins):
    /// context-hit bundles first in formula priority order, then closure-discovered candidates ordered by
    /// root name ordinal.</summary>
    public IReadOnlyList<SubjectCandidate> Candidates
    {
        get
        {
            if (_candidates is not null) return _candidates;
            var hits = new List<SubjectCandidate>();
            foreach (var b in _hitBundles) TryParse(b, hits, ownPrefabSource: true);
            var discovered = new List<SubjectCandidate>();
            foreach (var b in ScopeBundles)
                if (!_hitSet.Contains(b)) TryParse(b, discovered, ownPrefabSource: false);
            // Root name first, then the BUNDLE id — because the dedupe below is first-wins and List.Sort is
            // unstable, so two closure bundles carrying the same root name were being separated by the
            // packer's dependency order, and the winner could change on a repack that moved nothing. The
            // bundle id is a catalog fact and total over the set, so the tie now breaks the same way on
            // every install and across every patch.
            //
            // This CAN move a measurement — but only for a subject whose winner was already unstable across
            // repacks, which is the residual the fingerprint's own summary names. Sharing schema 7 is a
            // from-scratch generation on every install (no schema-6 row is reusable at any grain), so the
            // re-measure it would cost is one already being paid: it lands free now, and never again.
            discovered.Sort((a, b) =>
            {
                int byRoot = string.CompareOrdinal(a.Root, b.Root);
                return byRoot != 0 ? byRoot : string.CompareOrdinal(a.Bundle, b.Bundle);
            });

            // The dorm visibility lists are gathered across every parsed prefab that is a CONSTITUENT of this
            // subject (see SubjectModelBuilder.IsSubjectConstituent), ahead of the root-name dedupe below. The
            // context copies of one stem share a root name, so the dedupe keeps only the first — and the
            // components ride whichever copy the game authored them on, usually not that one; both copies are
            // already parsed here, so reading their lists costs no further I/O. The constituent gate is what
            // keeps a genuinely FOREIGN root out: a dependency closure holds other subjects' prefabs (another
            // doll's weapon, say), and a node name is not owned — theirs could collide with ours and withhold
            // a part the game draws. Skipping a non-constituent is silent on purpose, for the same reason a
            // sibling's foreign-prefixed slot is: it is correctly absent, not a failure to resolve OUR data.
            var visibility = new Dictionary<string, VisibilityOverride>(StringComparer.OrdinalIgnoreCase);
            var contributors = new List<string>();
            foreach (var c in hits.Concat(discovered))
                if (SubjectModelBuilder.IsSubjectConstituent(c.Prefab, _outfit)
                    && c.Prefab.VisibilityOverrides is { } overrides)
                {
                    // the bundle joins the contributor record even when its entries lose every min-wins
                    // fold: its content shaped the answer either way, and the record exists for a reader
                    // (SubjectModel.PrefabBundles) asking what a rewrite in place would change
                    if (overrides.Count > 0) contributors.Add(c.Bundle);
                    foreach (var (node, why) in overrides)
                        if (!visibility.TryGetValue(node, out var have) || why < have)
                            visibility[node] = why;
                }
            _visibility = visibility;
            _visibilityContributors = contributors;

            var seenRoots = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<SubjectCandidate>();
            foreach (var c in hits) if (seenRoots.Add(c.Root)) result.Add(c);
            foreach (var c in discovered) if (seenRoots.Add(c.Root)) result.Add(c);
            return _candidates = result;
        }
    }

    /// <summary>Node name → the game-side mechanism that can leave it undrawn, unioned across the prefabs in
    /// this scope that parsed AND are constituents of this subject
    /// (<see cref="SubjectModelBuilder.IsSubjectConstituent"/>) — including the context copies
    /// <see cref="Candidates"/> drops for sharing a root name, and excluding the other subjects' prefabs a
    /// dependency closure also holds. The lowest-valued mechanism wins a node several lists name, so one node
    /// answers with one reason however the scope happens to order its bundles. Answers only once
    /// <see cref="Candidates"/> has been read, which is the walk that parses them.</summary>
    public IReadOnlyDictionary<string, VisibilityOverride> VisibilityOverrides
    {
        get
        {
            _ = Candidates;
            return _visibility!;
        }
    }

    /// <summary>The bundles whose prefabs CONTRIBUTED to <see cref="VisibilityOverrides"/> — constituents
    /// carrying at least one list entry, including the context copies <see cref="Candidates"/> drops for
    /// sharing a root name. A reader recording what this subject's answer DEPENDS on needs these beyond the
    /// candidates: the dedupe keeps one copy per root, while the lists ride whichever copy the game authored
    /// them on, so a contributor rewritten in place changes the model with no candidate bundle having moved.
    /// Answers only once <see cref="Candidates"/> has been read.</summary>
    public IReadOnlyList<string> VisibilityContributors
    {
        get
        {
            _ = Candidates;
            return _visibilityContributors!;
        }
    }

    /// <param name="ownPrefabSource">True for a bundle the subject's own address or route resolves its
    /// prefab from, which is what may answer <see cref="DeclinedRoot"/> — and where a curated route's
    /// pinned root name applies. A closure-discovered dependency parses first-root like the formula
    /// path's own dependencies: the pin is about the ADDRESSED bundle holding sibling roots, and a
    /// weapon-style subject ships real constituent roots (its attachment assemblies) in dependency
    /// bundles the pin would otherwise skip.</param>
    private void TryParse(string bundle, List<SubjectCandidate> into, bool ownPrefabSource)
    {
        // deobfuscation exceptions propagate (BUSY contract); null = absent, not a candidate
        var dec = _tryDeobfuscate(bundle);
        if (dec is null) return;
        CharacterPrefab? prefab;
        bool declined;
        // _rootName pins a curated route's container root in the bundle its address resolves to; the
        // stem formula takes the bundle's first root that parses, which is safe there because address
        // and root agree by construction
        try
        {
            prefab = _fillCache is null
                ? PrefabReader.Read(dec, ownPrefabSource ? _rootName : null, out declined)
                : _fillCache.Parse(bundle, dec, ownPrefabSource ? _rootName : null, out declined);
        }
        catch (Exception e)
        {
            _problems.Add($"Assembly prefab in bundle '{bundle}' couldn't be read ({e.Message}).");
            return;
        }
        if (declined && ownPrefabSource) DeclinedRoot = true;
        if (prefab is null) return;   // no readable root — a dependency bundle, not an assembly prefab
        into.Add(new SubjectCandidate(prefab.RootName, bundle, dec, prefab));
    }

    /// <summary>Scope-bounded CAB→bundle: walk <see cref="ScopeBundles"/> in order, reading each
    /// not-yet-inspected bundle's CAB name and caching the pairs, stopping at the match. A CAB nothing in
    /// scope provides ⇒ null, the caller's loud FAILED-material path.</summary>
    public string? BundleForCab(string cab)
    {
        if (_cabToBundle.TryGetValue(cab, out var known)) return known;
        while (_nextToInspect < ScopeBundles.Count)
        {
            var bundle = ScopeBundles[_nextToInspect++];
            var dec = _tryDeobfuscate(bundle);   // deobfuscation exceptions propagate (BUSY contract)
            if (dec is null) continue;
            string bundleCab;
            try { bundleCab = _reader.ListAssetsWithCab(dec).Cab; }
            catch (Exception e)
            {
                _problems.Add($"Bundle '{bundle}' couldn't be read while resolving its CAB name ({e.Message}).");
                continue;
            }
            _cabToBundle.TryAdd(bundleCab, bundle);
            if (string.Equals(bundleCab, cab, StringComparison.Ordinal)) return bundle;
        }
        return null;
    }
}
