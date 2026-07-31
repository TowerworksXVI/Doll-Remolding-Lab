using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Materials;
using Remold.Core.Model;

namespace Remold.Core.Workbench;

/// <summary>
/// Builds one <see cref="SubjectModel"/> — the read-only Outfit Workbench's whole picture of a subject.
/// Pure and synchronous: the view-model calls it OFF the UI thread and assigns the immutable result whole.
///
/// <para><b>Recipe-first.</b> Parts, materials, and skeleton come from the subject's assembly prefab via its
/// <see cref="SubjectScope"/>, using the same slot-match the renderer-first texture tier uses, so the tree
/// reflects what the game loads, CAB-exact, independent of what the project materialized. No assembly
/// prefab for the stem ⇒ a part-less model with a loud Problems note; the build never throws.</para>
///
/// <para><b>Loud, order-preserving.</b> Material order IS the submesh binding — an empty renderer slot
/// (PPtr 0:0) becomes a placeholder material <c>""</c> and is never dropped or reordered. Every resolution
/// failure surfaces as a per-node problem AND rolls up into <see cref="SubjectModel.Problems"/>.</para>
///
/// <para>Deobfuscated bundles are memoized for the build's duration, reusing the same byte[] reference so
/// <see cref="BundleReader"/>'s parse cache hits. No texture pixels or mesh vertices are decoded here.</para>
/// </summary>
public static class SubjectModelBuilder
{
    /// <summary>The subject-level problem for a prefab that resolved and yielded no usable renderer slot.
    /// Distinct from the absent-prefab sentence, because the two send the reader to different places.</summary>
    public const string NoReadableParts =
        "No readable parts in this subject's assembly prefab. Its renderer slots carry no mesh this app can read.";

    /// <param name="catalog">the game's parsed catalog (address → bundle + dependency closures).</param>
    /// <param name="tryDeobfuscate">non-throwing logical-bundle → plain bytes (null when absent/unreadable).</param>
    /// <param name="outfit">the subject's outfit/model (its <see cref="Outfit.Stem"/> + mesh prefix).</param>
    /// <param name="character">the character's internal key (rendered friendly by the VM, never shown raw).</param>
    public static SubjectModel Build(
        CatalogIndex catalog, Func<string, byte[]?> tryDeobfuscate, Outfit outfit, string character)
    {
        var problems = new List<string>();

        var cache = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        byte[]? Deobfuscate(string logical)
        {
            if (cache.TryGetValue(logical, out var hit)) return hit;
            byte[]? bytes;
            try { bytes = tryDeobfuscate(logical); } catch { bytes = null; }
            return cache[logical] = bytes;
        }

        // ---- locate the subject's assembly prefabs (the scope's formula-priority candidate order) ----
        // Fight/Dorm coats, props and shadow rigs live in stem-SIBLING roots, not the stem's own prefab, so
        // take EVERY scope candidate and merge their slots rather than the first that parses.
        var scope = SubjectScope.Build(catalog, Deobfuscate, outfit);
        // The scope's own reader, not a second one: the scope's CAB walk and the reads below open the same
        // bundles, and two readers means each of them parses every one of them again. Per-operation and
        // single-threaded either way, which is the scope's own rule.
        var reader = scope.Reader;
        var candidates = scope.Candidates.Select(c => new Candidate(c.Bundle, c.Dec, c.Prefab)).ToList();

        if (candidates.Count == 0)
        {
            // Two different failures, said apart: a prefab that was THERE and yielded no readable slot is a
            // gap in what this app can read, and saying "no prefab" about it sends the modder hunting an
            // asset that exists. Only a subject nothing resolved for gets the absent-prefab sentence.
            problems.Insert(0, scope.DeclinedRoot
                ? NoReadableParts
                : "No assembly prefab found for this subject. Its parts can't be read.");
            problems.AddRange(scope.Problems);
            return new SubjectModel(character, outfit.Stem, SubjectSource.Prefab,
                Array.Empty<SubjectPart>(), Skeleton: null, problems);
        }

        var primary = candidates[0];   // the highest-priority candidate — the skeleton and part-priority baseline

        // ---- skeleton (rig under the PRIMARY prefab's container root) ----
        // Scoped to that root's own subtree: a bundle shipping a second root ships a second copy of the
        // rig, and pooling both would make every bone name a duplicate (see PrefabRig.Subtree).
        // Exclude the non-bone Transforms every prefab also carries: the container root and each
        // renderer-slot GameObject (× LOD tiers). Exclusion is by NAME; a name two Transforms carry costs
        // the SKELETON (null + the read's own loud sentence) rather than dropping the second one silently.
        // The degradation stops there: the skeleton is one display node, and the parts stand without it —
        // which is why the sentence rides SkeletonProblem, its own channel, and never Problems.
        var rigExclude = new HashSet<string>(StringComparer.Ordinal) { primary.Prefab.RootName };
        foreach (var s in primary.Prefab.Slots) rigExclude.Add(s.Name);
        SubjectSkeleton? skeleton = PrefabRig.TryRead(reader, primary.Dec, primary.Prefab.RootName,
            rigExclude, out var rigProblem);
        string? skeletonProblem = skeleton is null
            ? rigProblem ?? "Skeleton structure unavailable. The assembly prefab carried no "
                + "readable rig hierarchy for this subject."
            : null;

        // recipe leaf name → mesh addressable, PER candidate (a part's mesh address comes from its OWNING
        // candidate's recipe, not the primary's).
        for (int i = 0; i < candidates.Count; i++)
        {
            var byLeaf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in candidates[i].Prefab.Recipe)
                if (!byLeaf.ContainsKey(Leaf(e.SlotPath))) byLeaf[Leaf(e.SlotPath)] = e.MeshAddress;
            candidates[i] = candidates[i] with { MeshAddressByLeaf = byLeaf };
        }

        // every slot name across ALL parsed candidates — a recipe entry pointing outside this set is an orphan
        var allSlotNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
            foreach (var s in c.Prefab.Slots) allSlotNames.Add(s.Name);

        // Slot-level ownership (see SlotIsOwned). A dropped foreign-prefixed sibling slot is CORRECTLY
        // absent — another subject's part — so it earns no Problems entry: the loudness invariant is about
        // OUR failures to resolve OUR parts.
        bool OwnsSlot(Candidate c, string slotName) => SlotIsOwned(c.Prefab.RootName, slotName, outfit);

        // ---- parts: UNION the renderer slots across candidates, ONE part per token ----
        // Fight/Dorm variants stay distinct parts. Slots and tokens come from the SHARED OwnedMeshSlots +
        // TokenRule pair the Pick roster reads, so the two can't drift.
        var ownedSlots = new List<(int Candidate, PrefabSlot Slot)>();
        for (int i = 0; i < candidates.Count; i++)
            foreach (var s in OwnedMeshSlots(candidates[i].Prefab, outfit))
                ownedSlots.Add((i, s));
        var tokenOf = TokenRule(ownedSlots.Select(o => o.Slot.Name), outfit.MeshPrefix, problems);

        var specs = new List<PartSpec>();
        foreach (var g in ownedSlots.GroupBy(o => tokenOf(o.Slot.Name), StringComparer.OrdinalIgnoreCase))
        {
            // The owner is whoever carries the token's lod0 tier, and only then the highest-priority
            // candidate: the fold pools every candidate, so a variant lod0 can live in a LATER candidate
            // than the bare tier it absorbed. Candidate-first would crown the tier slot and the part would
            // lose the mesh it is actually made of.
            var pick = g.OrderBy(o => MeshName.Lod(o.Slot.Name) == "lod0" ? 0 : 1)
                        .ThenBy(o => o.Candidate).First();
            var cand = candidates[pick.Candidate];
            // representative = the token's lod0 slot; fall back to the slot the token was found on, since
            // FindSlot re-derives the raw token and misses the part-less token fallback
            var slot = RendererResolver.FindSlot(cand.Prefab, outfit.MeshPrefix, g.Key) ?? pick.Slot;
            specs.Add(new PartSpec(g.Key, cand, slot, Problem: null));
        }

        // recipe entries whose slot leaf matches NO slot in ANY candidate: never silently dropped — a loud
        // placeholder row plus a subject Problems entry
        var orphanLeaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cand in candidates)
        {
            foreach (var e in cand.Prefab.Recipe)
            {
                var leaf = Leaf(e.SlotPath);
                if (!OwnsSlot(cand, leaf)) continue;   // sibling's own foreign recipe row → not our orphan
                if (allSlotNames.Contains(leaf) || !orphanLeaves.Add(leaf)) continue;
                var token = MeshName.Part(leaf, outfit.MeshPrefix);
                if (string.IsNullOrEmpty(token)) token = leaf;   // orphan fallback = the leaf itself
                var msg = $"Recipe entry '{leaf}' has no renderer slot. Its part can't be shown yet.";
                problems.Add(msg);
                specs.Add(new PartSpec(token, cand, Slot: null, Problem: msg));
            }
        }

        var parts = new List<SubjectPart>(specs.Count);
        foreach (var spec in specs.OrderBy(p => p.Token, StringComparer.OrdinalIgnoreCase))
        {
            var cand = spec.Owner;
            if (spec.Slot is null)
            {
                parts.Add(new SubjectPart(spec.Token, SlotName: "", MeshAddress: "",
                    Materials: Array.Empty<SubjectMaterial>(), SourceRoot: cand.Prefab.RootName, Problem: spec.Problem));
                continue;
            }
            var meshAddress = cand.MeshAddressByLeaf.TryGetValue(spec.Slot.Name, out var addr) ? addr : "";
            var materials = ResolveSlotMaterials(
                scope, reader, Deobfuscate, cand.Bundle, cand.Dec, spec.Slot, spec.Token, outfit, problems);

            // smr-body mesh identity: a slot with NO recipe address but a serialized renderer mesh resolves
            // its CAB to the owning scope bundle here, so the part carries bundle+path-id down to the
            // exporter. A CAB nothing in scope provides is a LOUD per-part problem, not a blank.
            string? partProblem = null;
            var (meshBundle, meshPathId) = meshAddress.Length > 0 || spec.Slot.Mesh is null
                ? (null, 0L)
                : ResolveSlotMesh(scope, cand, spec.Slot.Mesh, spec.Token, problems, out partProblem);

            // The part's OTHER tier slots (same token) for the prefab-exact LOD fan-out, single-sourced off
            // the SAME TokenRule grouping that claims the representative, so a part's tiers can't drift
            // between the tree and the exporter — variant fold included, so …_cloth2_lod1 lands on the
            // variant part owning …_cloth2_lod0_Fight instead of becoming its own part. Gathered across ALL
            // candidates, since a folded tier can sit in a different prefab from its lod0; each tier reads
            // its mesh identity from ITS OWN candidate. A slot name repeated across candidates is taken
            // ONCE, owning candidate first — the owner holds the lod0, so its copy is the real one. Same
            // OwnedMeshSlots gate as the part union: a filler slot carries no mesh to fan out.
            var tierNames = new HashSet<string>(StringComparer.Ordinal) { spec.Slot.Name };
            var siblingTiers = new List<RecipeTierSlot>();
            foreach (var tierCand in candidates.OrderBy(c => ReferenceEquals(c, cand) ? 0 : 1))
                foreach (var x in OwnedMeshSlots(tierCand.Prefab, outfit))
                {
                    if (!tokenOf(x.Name).Equals(spec.Token, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!tierNames.Add(x.Name)) continue;
                    var tierAddr = tierCand.MeshAddressByLeaf.GetValueOrDefault(x.Name, "");
                    if (tierAddr.Length > 0 || x.Mesh is null) { siblingTiers.Add(new RecipeTierSlot(x.Name, tierAddr)); continue; }
                    var (tb, tp) = ResolveSlotMesh(scope, tierCand, x.Mesh, spec.Token, problems, out _);
                    siblingTiers.Add(new RecipeTierSlot(x.Name, "", tb, tp));
                }
            // Mirror the exporter's whole-part texture miss: a part whose real materials ALL resolved cleanly
            // yet bind ZERO texture maps will export untextured, so flag it for the tree's ⚠ badge. A part
            // with any bound map, any resolution problem, or only placeholder slots is not this case.
            string? noTexProblem = null;
            if (materials.All(m => m.Maps.Count == 0)
                && materials.All(m => m.Problem is null)
                && materials.Any(m => !m.IsPlaceholder))
                noTexProblem = "No textures: this part's prefab material(s) bind no texture maps. It will export untextured.";
            parts.Add(new SubjectPart(spec.Token, spec.Slot.Name, meshAddress, materials,
                SourceRoot: cand.Prefab.RootName, Problem: partProblem ?? noTexProblem, SiblingTiers: siblingTiers,
                MeshBundle: meshBundle, MeshPathId: meshPathId,
                IsStatic: spec.Slot.Renderer == SlotRenderer.Static));
        }

        // Defensive: renderer slots existed but nothing became a part — loud rather than a silent empty
        // tree. Unreachable while SlotToken gives every slot a token, so it's a net, not a live path.
        if (parts.Count == 0 && candidates.Any(c => c.Prefab.Slots.Count > 0))
            problems.Add("The assembly prefab has renderer slots but none resolved to a part. Nothing to show.");

        // the scope accrues problems lazily — fold them in last so none are lost
        problems.AddRange(scope.Problems);

        return new SubjectModel(character, outfit.Stem, SubjectSource.Prefab,
            parts, skeleton, problems, PrimaryRoot: primary.Prefab.RootName, PrimaryBundle: primary.Bundle,
            SkeletonProblem: skeletonProblem);
    }

    /// <summary>The candidate's renderer slots that can become parts: OWNED (<see cref="SlotIsOwned"/>) and
    /// carrying SOME mesh identity — a recipe row addressing the slot, or a serialized renderer mesh. A slot
    /// with neither is game filler (the combat copies' empty <c>HPMesh</c>) that can never materialize, so
    /// it is CORRECTLY absent from the part union, not a resolution failure. A mesh in Unity's BUILT-IN
    /// resources (a shadow-blob or glow quad) counts as no mesh: those archives ship with the engine, not
    /// in the game's corpus, so nothing can read or replace them and the slot can never be a part. The ONE
    /// gate <see cref="Build"/>'s union and <see cref="OwnedSlotTokens"/> enumerate, in prefab slot
    /// order.</summary>
    internal static List<PrefabSlot> OwnedMeshSlots(CharacterPrefab prefab, Outfit outfit)
    {
        var recipeLeaves = prefab.Recipe.Select(e => Leaf(e.SlotPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return prefab.Slots
            .Where(s => SlotIsOwned(prefab.RootName, s.Name, outfit)
                     && (recipeLeaves.Contains(s.Name) || (s.Mesh is not null && !IsBuiltinMesh(s.Mesh))))
            .ToList();
    }

    /// <summary>Whether a serialized mesh reference points into one of Unity's engine-shipped archives
    /// rather than a game bundle.</summary>
    private static bool IsBuiltinMesh(Bundles.PrefabMeshRef mesh) =>
        mesh.Cab is { } cab
        && (cab.Equals("unity default resources", StringComparison.OrdinalIgnoreCase)
            || cab.Equals("unity_builtin_extra", StringComparison.OrdinalIgnoreCase)
            || cab.EndsWith("/unity default resources", StringComparison.OrdinalIgnoreCase)
            || cab.EndsWith("/unity_builtin_extra", StringComparison.OrdinalIgnoreCase));

    /// <summary>The subject's token rule as one function: slot name → part token, i.e.
    /// <see cref="SlotTokens"/> with <see cref="VariantFold"/> applied. Built once per subject over ALL its
    /// owned slots, since both the fold and the foreign-prefix rule are whole-subject questions, then read
    /// by the part union, the tier grouping, and <see cref="OwnedSlotTokens"/> — so the workbench tree and
    /// the Pick roster can't disagree about what the parts are.</summary>
    private static Func<string, string> TokenRule(IEnumerable<string> ownedSlotNames, string meshPrefix,
        List<string>? problems = null)
    {
        var names = ownedSlotNames.ToList();
        var slotToken = SlotTokens(names, meshPrefix);
        var fold = VariantFold(names, slotToken);
        return name =>
        {
            var token = slotToken(name);
            return fold.TryGetValue(token, out var into) ? into : token;
        };
    }

    /// <summary>The variant fold: bare part token → the outfit-state variant token whose LOD tiers it is
    /// really part OF. Some parts ship their lod0 tier ONLY under a variant name (<c>…_cloth2_lod0_Fight</c>
    /// plus <c>…_cloth2_lod1</c>, no plain <c>…_cloth2_lod0</c>). <see cref="MeshName.Part"/> keeps the
    /// variant suffix, so those derive different tokens and the bare one would enter the roster as a phantom
    /// part owning no mesh.
    ///
    /// <para>Rule: a token with no variant suffix, owning NO lod0-tier slot, whose base has EXACTLY ONE
    /// variant sibling that DOES own one, is not a part — its slots are extra LOD tiers of that sibling. A
    /// token that owns a lod0 tier is always a part, so two real garments stay two parts. Nothing folds when
    /// no variant sibling owns a lod0 tier, and nothing when TWO do: which garment the tier belongs to is
    /// then unknowable from the names.</para></summary>
    private static Dictionary<string, string> VariantFold(IEnumerable<string> ownedSlotNames,
        Func<string, string> slotToken)
    {
        // token → does it own a lod0-tier slot? (the variant's lod0 is INFIXED: …_lod0_Fight)
        var ownsLod0 = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ownedSlotNames)
        {
            var token = slotToken(name);
            var isLod0 = MeshName.Lod(name) == "lod0";
            ownsLod0[token] = ownsLod0.TryGetValue(token, out var had) ? had || isLod0 : isLod0;
        }

        var fold = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (token, hasLod0) in ownsLod0)
        {
            if (hasLod0 || MeshName.SplitVariant(token).Variant is not null) continue;
            string? only = null;
            bool ambiguous = false;
            foreach (var (other, otherHasLod0) in ownsLod0)
            {
                if (!otherHasLod0) continue;
                var (otherBase, otherVariant) = MeshName.SplitVariant(other);
                if (otherVariant is null || !otherBase.Equals(token, StringComparison.OrdinalIgnoreCase)) continue;
                if (only is not null) { ambiguous = true; break; }
                only = other;
            }
            if (!ambiguous && only is not null) fold[token] = only;
        }
        return fold;
    }

    /// <summary>Resolve an smr-body slot's serialized mesh reference to (owning logical bundle, path id): a
    /// local reference is the candidate's own bundle, an external CAB resolves scope-bounded. An
    /// unresolvable CAB yields (null, 0) with a loud per-part problem — the part still lists,
    /// marked.</summary>
    private static (string? Bundle, long PathId) ResolveSlotMesh(SubjectScope scope, Candidate cand,
        Bundles.PrefabMeshRef mesh, string token, List<string> problems, out string? partProblem)
    {
        partProblem = null;
        var bundle = mesh.Cab is null ? cand.Bundle : scope.BundleForCab(mesh.Cab);
        if (bundle is null)
        {
            partProblem = $"Part '{token}': mesh dependency (CAB '{mesh.Cab}') isn't in this install. Its mesh can't be read.";
            problems.Add(partProblem);
            return (null, 0);
        }
        return (bundle, mesh.PathId);
    }

    /// <summary>The part a part-less summon or prop reads as: its mesh set carries no part segment at all, so
    /// the token parses empty and there is nothing to name the single part by. This word is what the tree, the
    /// change list and every part-grain message call it, so it is chosen to be read rather than
    /// decoded — the game's own <c>_slg_</c> mesh naming is not it.</summary>
    public const string PartlessToken = "addon";

    /// <summary>
    /// Slot name → part token, before the variant fold. A slot carrying the subject's own mesh prefix is
    /// parsed against it; a slot whose name parses to an EMPTY token is the single part of a part-less
    /// summon/prop, named <see cref="PartlessToken"/> so it appears rather than vanishing.
    ///
    /// <para>A slot admitted by the root-name ownership clause (see <see cref="SlotIsOwned"/>) can carry a
    /// FOREIGN prefix — a shared mesh named for another skin's family — which the subject's own prefix
    /// cannot strip, leaving the whole raw name as the token. Such a slot is parsed against the prefix it
    /// carries ITSELF (<see cref="MeshName.OwnPrefix"/>), so it reads as the part it is. The short token is
    /// declined when one of the subject's own slots already derives it: merging two different meshes into
    /// one part would lose one, so the slot keeps its raw name — a complete answer, not a resolution
    /// problem; the full name in the tree is its own explanation. The twin lookup is
    /// <see cref="Materials.RendererResolver.FindSlot"/>, which resolves a token back to its slot by the
    /// same two prefixes.</para>
    ///
    /// <para>Internal rather than private because the ledger asks the same question of a materialized
    /// target's mesh name: <see cref="Materializer.MaterializedParts"/> reads it, so "which part is this
    /// mesh" has ONE answer for the tree, the roster and the project's targets alike.</para>
    /// </summary>
    internal static Func<string, string> SlotTokens(IReadOnlyList<string> ownedSlotNames, string meshPrefix)
    {
        string Raw(string name)
        {
            var token = MeshName.Part(name, meshPrefix);
            return string.IsNullOrEmpty(token) ? PartlessToken : token;
        }
        bool Foreign(string name) => !name.StartsWith(meshPrefix, StringComparison.OrdinalIgnoreCase);

        var ownTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ownedSlotNames)
            if (!Foreign(name)) ownTokens.Add(Raw(name));

        // short token → the foreign family that claimed it, so a second family's identical token is a
        // collision too, not a silent merge of two meshes into one part
        var claimedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shortOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in ownedSlotNames)
        {
            if (!Foreign(name) || !seen.Add(name)) continue;
            if (MeshName.OwnPrefix(name) is not { } carried) continue;
            var token = MeshName.Part(name, carried);
            if (string.IsNullOrEmpty(token)) continue;
            bool taken = ownTokens.Contains(token)
                || (claimedBy.TryGetValue(token, out var by)
                    && !by.Equals(carried, StringComparison.OrdinalIgnoreCase));
            if (taken) continue;
            claimedBy[token] = carried;
            shortOf[name] = token;
        }
        return name => shortOf.TryGetValue(name, out var t) ? t : Raw(name);
    }

    /// <summary>The slot-ownership rule, shared by <see cref="Build"/> and <see cref="OwnedSlotTokens"/>:
    /// a slot is this subject's iff it lives in the subject's own
    /// EXACT-STEM prefab root (whatever its slots are named) OR its name carries the subject's mesh prefix.
    /// A sibling's foreign-prefixed slots belong to a different subject and are dropped silently — correctly
    /// absent, not a resolution failure.</summary>
    internal static bool SlotIsOwned(string candidateRoot, string slotName, Outfit outfit) =>
        string.Equals(candidateRoot, outfit.Stem, StringComparison.Ordinal)
        || slotName.StartsWith(outfit.MeshPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The subject's part tokens as the Pick roster shows them: the union of OWNED renderer-slot
    /// tokens across the scope's parsed candidates, deduped and sorted. The SAME
    /// <see cref="OwnedMeshSlots"/> + <see cref="TokenRule"/> pair <see cref="Build"/> claims its parts with,
    /// so the Pick tree and the workbench can't drift. Recipe orphans are Build's loud-placeholder concern,
    /// not a pickable token.</summary>
    public static IReadOnlyList<string> OwnedSlotTokens(IReadOnlyList<SubjectCandidate> candidates, Outfit outfit)
    {
        var slotNames = candidates.SelectMany(c => OwnedMeshSlots(c.Prefab, outfit)).Select(s => s.Name).ToList();
        var tokenOf = TokenRule(slotNames, outfit.MeshPrefix);

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new List<string>();
        foreach (var name in slotNames)
        {
            var token = tokenOf(name);
            if (claimed.Add(token)) tokens.Add(token);
        }
        tokens.Sort(StringComparer.OrdinalIgnoreCase);
        return tokens;
    }

    /// <summary>One parsed candidate assembly prefab: the logical bundle + deobfuscated bytes it came from
    /// (materials/mesh addresses resolve against THESE, not the primary's), the parsed prefab, and its
    /// recipe leaf→mesh-address map, filled after parse.</summary>
    private sealed record Candidate(string Bundle, byte[] Dec, CharacterPrefab Prefab)
    {
        public IReadOnlyDictionary<string, string> MeshAddressByLeaf { get; init; }
            = new Dictionary<string, string>();
    }

    /// <summary>A resolved part before materials are read: its token, the owning candidate, its representative
    /// slot (null for a FindSlot anomaly or a recipe orphan), and any per-part problem.</summary>
    private readonly record struct PartSpec(string Token, Candidate Owner, PrefabSlot? Slot, string? Problem);

    /// <summary>Resolve one renderer slot's ordered <c>m_Materials</c> to <see cref="SubjectMaterial"/>s,
    /// preserving order and placeholders. Unlike the exporter's renderer tier, which silently emits an empty
    /// group for an absent dependency to keep submesh alignment, this is LOUD: a non-empty material that
    /// can't resolve gets a per-material problem that also rolls up to the subject. Engine-shared
    /// textures stay on the surface: a retexture of one is scoped at build time to the subject's own
    /// mesh draws (the sharing measurement decides), so exposing it is safe.</summary>
    private static List<SubjectMaterial> ResolveSlotMaterials(
        SubjectScope scope, BundleReader reader, Func<string, byte[]?> deobfuscate,
        string prefabBundle, byte[] prefabDec, PrefabSlot slot, string token, Outfit outfit,
        List<string> problems)
    {
        var result = new List<SubjectMaterial>(slot.Materials.Count);
        foreach (var mref in slot.Materials)
        {
            // empty renderer slot (PPtr 0:0): a placeholder that HOLDS the submesh order — never dropped
            if (mref.Cab is null && mref.PathId == 0)
            {
                result.Add(new SubjectMaterial("", 0, null, Array.Empty<SubjectMap>()));
                continue;
            }

            string? matBundle = mref.Cab is null ? prefabBundle : scope.BundleForCab(mref.Cab);
            if (matBundle is null)
            {
                var msg = $"Part '{token}': material dependency (CAB '{mref.Cab}') isn't in this install.";
                problems.Add(msg);
                result.Add(new SubjectMaterial("", mref.PathId, mref.Cab, Array.Empty<SubjectMap>(), msg));
                continue;
            }
            byte[]? matDec = matBundle == prefabBundle ? prefabDec : deobfuscate(matBundle);
            if (matDec is null)
            {
                var msg = $"Part '{token}': couldn't read the material's bundle.";
                problems.Add(msg);
                result.Add(new SubjectMaterial("", mref.PathId, mref.Cab, Array.Empty<SubjectMap>(), msg));
                continue;
            }
            var te = reader.GetMaterialTexEnvsByPathId(matDec, mref.PathId);
            if (te is null)
            {
                var msg = $"Part '{token}': no material found at the expected location in its bundle.";
                problems.Add(msg);
                result.Add(new SubjectMaterial("", mref.PathId, mref.Cab, Array.Empty<SubjectMap>(), msg));
                continue;
            }

            var maps = MaterialResolver
                .ResolveTexSlots(scope.BundleForCab, reader, deobfuscate, matBundle, matDec, te.Value.ExternalCabs, te.Value.Slots)
                .Select(m => new SubjectMap(m.Slot, m.TextureName, m.BundleHash))
                .ToList();
            result.Add(new SubjectMaterial(te.Value.Name, mref.PathId, mref.Cab, maps));
        }
        return result;
    }


    /// <summary>The leaf (last '/'-segment) of a recipe <c>TransfromPath</c> — the renderer slot's own
    /// GameObject name, matching <see cref="PrefabSlot.Name"/>.</summary>
    internal static string Leaf(string slotPath)
    {
        int i = slotPath.LastIndexOf('/');
        return i >= 0 ? slotPath[(i + 1)..] : slotPath;
    }
}
