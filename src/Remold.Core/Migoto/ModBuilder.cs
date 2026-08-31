using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Textures;
using Remold.Core.Workbench;

namespace Remold.Core.Migoto;

/// <summary>
/// The world the build reads, as delegates so the orchestration is testable without a live install. Every
/// bundle comes through here FORWARD-resolved: an address resolves through the catalog to the bundle the
/// game actually loads, never a hand-supplied bundle id — same-named twins in other bundles are a silent
/// wrong-geometry trap. Smr-backed parts carry their resolved bundle + path id from the prefab itself.
/// </summary>
public sealed record BuildEnv(
    /// <summary>(character, outfit stem) → the subject's live roster, or null (unknown subject).</summary>
    Func<string, string, SubjectModel?> ResolveSubject,
    /// <summary>Recipe mesh address → owning logical bundle id, or null (no catalog entry).</summary>
    Func<string, string?> ResolveAddress,
    /// <summary>Logical bundle id → deobfuscated bytes, or null (not in this install).</summary>
    Func<string, byte[]?> Deobfuscate,
    /// <summary>The live asset-catalog version (sidecar stamp); null when unknown.</summary>
    string? CatalogVersion,
    /// <summary>The building app's version (sidecar stamp); null when unknown.</summary>
    string? AppVersion,
    /// <summary>The roster's asset-sharing measurement, deciding each edit's scope (draw-scoped
    /// retextures, presence latches). Null = no measurement: every edit ships unscoped, noted in the
    /// user-visible build infos.</summary>
    Workbench.SharingIndex? Sharing = null,
    /// <summary>Outfit stem → its wardrobe scheme (<see cref="Tables.PartScheme"/>), or null for a
    /// non-modular outfit. Null resolver = no scheme available: wardrobe-shaped parts then classify as
    /// unknown, so they may be replaced but never lean on.</summary>
    Func<string, IReadOnlyList<Tables.PartScheme.Slot>?>? PartSchemeFor = null,
    /// <summary>Outfit stem → the node overrides carried by the dorm and lobby timelines that outfit plays
    /// (<see cref="Bundles.TimelineShoes"/>). Null resolver = this build measured no timelines, so no part
    /// is demoted for being named by one. Called at most once per outfit.</summary>
    Func<string, IReadOnlyList<Bundles.TimelineShoe>?>? TimelineShoesFor = null,
    /// <summary>Logical bundle id → the content identity the game's own manifest states for it
    /// (<see cref="Workbench.BundleReads.BundleContentHashLookup"/>), or null for a bundle it does not
    /// name. Recorded in the repair data so a later read can say which targets actually moved between game
    /// versions instead of re-deriving all of them. Null resolver = no content identity is recorded, which
    /// costs precision and nothing else.</summary>
    Func<string, string?>? BundleContentHash = null,
    /// <summary>The shader slot catalog file this build reads its ps registers from
    /// (<see cref="ShaderSlotCatalog"/>). Null = the one shipped beside the assemblies, which is what the
    /// app passes; naming another is how a build is run against a catalog that is absent or malformed.</summary>
    string? ShaderSlotCatalogFile = null,
    /// <summary>Identity of the catalog source, including the install context. Exact-build reuse is
    /// disabled when absent because the version label alone cannot distinguish two catalog files.</summary>
    string? CatalogIdentity = null,
    /// <summary>Optional compiler identity override for deterministic hosts and tests. The app uses the
    /// exact Core binary identity and schema set when this is null.</summary>
    string? CompilerIdentity = null,
    /// <summary>Whether any game-table read this build leaned on DEGRADED instead of failing — the
    /// wardrobe/timeline readers' pools-stay-conservative fallbacks, whose only trace is a note. Asked
    /// AFTER the build, when every lazy reader has had its say. A degraded build is a real build with a
    /// real package, but it must never publish an exact-build completion record: the degradation is a
    /// fact about the RUN (typically the game holding its files), not about the inputs the fingerprint
    /// hashes, and serving it back would defeat the note's own "close the game for a full pass".</summary>
    Func<bool>? ReadDegraded = null);

/// <summary>
/// Where a build may keep regenerable products (solved recovery operators, encoded textures). Both are
/// keyed so an entry can only be served to an identical input, and neither changes what a build emits —
/// only how long it takes. Null = no persistent caches: nothing read, nothing left behind.
/// </summary>
public sealed record BuildCaches(string OperatorDir, string TextureDir, string? CompletionDir = null)
{
    /// <summary>The app's own cache locations under <see cref="LabPaths.CacheRoot"/>.</summary>
    public static BuildCaches Default => new(
        LabPaths.OperatorCacheRoot, LabPaths.EncodedTextureRoot, LabPaths.BuildCompletionRoot);
}

/// <summary>
/// Builds the mod described by a settled authored execution package — the one thing it takes. It
/// resolves every touched mesh forward, derives each
/// Replace's pool from its donor's weights, dumps vanilla streams, compiles each donor onto its union,
/// encodes textures, hashes each retextured stock texture from its own bundle bytes, enumerates every
/// shipped tier's hashes for non-pool hides, and emits the self-contained 3DMigoto folder + <c>gf2mod.json</c>
/// sidecar (+ the distribution <c>.zip</c>). Each Replace becomes one emitter pipeline, processed in roster
/// order of its replaced part for stable rebuilds; a part pooled by several Replaces is dumped once. The
/// emitters stay bundle-agnostic — this is the single place game-install knowledge enters the build.
///
/// <para><b>Mid-failure story:</b> everything is written into a work dir and a temp mod dir; the final
/// folder and zip appear only after a fully successful build (delete-then-move swap). A failed build throws
/// with the work dirs cleaned up — never a half-mod at the final path.</para>
/// </summary>
public static class ModBuilder
{
    /// <summary>What one build produced. <paramref name="Warnings"/> are user-facing and actionable (the
    /// Build pane lists and counts them). <paramref name="Infos"/> are user-facing disclosures with nothing
    /// to fix: an edit's reach beyond the picked subject, and how the build scoped it.
    /// <paramref name="Diagnostics"/> are everything else, reaching the build log and no UI surface.</summary>
    public sealed record Result(string OutDir, string? ZipPath,
        IReadOnlyList<string> Warnings, IReadOnlyList<string> Infos, IReadOnlyList<string> Diagnostics);

    /// <summary>What the build needs to know about a stock texture it overrides or anchors on: the
    /// 3DMigoto resource hash that addresses it, and whether its DXGI format is an <c>_SRGB</c> one —
    /// the tag an authored replacement of that slot has to carry.</summary>
    private readonly record struct StockTex(string Hash, bool Srgb);

    /// <summary>The extent a named toon ramp has to be authored at (<see cref="RampConversion.RampWidth"/>).
    /// The ship gate checks it, since a wrong curve is invisible until it is in game.</summary>
    internal const int RampWidth = RampConversion.RampWidth, RampHeight = RampConversion.RampHeight;

    /// <summary>The sRGB family an authored donor map of <paramref name="kind"/> has to be tagged with: the
    /// map binds at the anchor's draw, in the slot the anchor's own map of that kind occupies, so the
    /// anchor's DXGI format decides it. A map that won't read warns — a wrong tag is a colour error the
    /// author would otherwise diagnose in game — and leaves <paramref name="byConvention"/> as the only
    /// answer, which <paramref name="diagnostics"/> records.
    ///
    /// <para>EVERY matching map of every anchor material is read, because the donor map binds into all of
    /// them while the file can carry only one tag. The first readable map wins, deterministically, and a
    /// disagreement is named: the losing material renders the donor map wrong.</para></summary>
    internal static bool AnchorSrgb(IReadOnlyList<Workbench.SubjectMaterial> anchorMaterials,
        Func<string, bool> isSlot, string kind, bool byConvention, string sfx, string part,
        Func<Workbench.SubjectMap, bool> srgbOf, List<string> warnings, List<string> diagnostics)
    {
        bool unreadable = false;
        string? wonBy = null;
        bool won = false;
        foreach (var material in anchorMaterials)
            foreach (var m in material.Maps.Where(x => isSlot(x.Slot)))
            {
                bool srgb;
                try { srgb = srgbOf(m); }
                catch (Exception ex) when (ex is not BlockedAssetException)
                {
                    unreadable = true;
                    warnings.Add($"Couldn't read the original {kind} map '{m.TextureName}' for "
                        + $"'{part}'. The edited {kind} may show with the wrong colours.");
                    continue;
                }
                if (wonBy is null) { wonBy = m.TextureName; won = srgb; }
                else if (srgb != won)
                    warnings.Add($"The original {kind} maps for '{part}' disagree. '{wonBy}' is "
                        + $"{Family(won)} and '{m.TextureName}' is {Family(srgb)}, so the edited {kind} is "
                        + $"written as {Family(won)} and shows with the wrong colours wherever it replaces "
                        + $"'{m.TextureName}'.");
            }
        if (wonBy is not null) return won;
        if (unreadable)
            diagnostics.Add($"donor ({sfx}): no anchor {kind} map answered, so the authored {kind} is "
                + $"tagged {Family(byConvention)} by convention");
        return byConvention;
    }

    /// <summary>The DXGI family's name for a warning.</summary>
    private static string Family(bool srgb) => srgb ? "sRGB" : "linear";

    /// <summary>One warning per hide hash claimed by two changes with different toggle keys, else null. The
    /// hash dedup leaves ONE skip section, so it can carry only one key — the first claimant's — and the
    /// second's is dropped. The message names the mesh two claims collapsed onto, the key that survives the
    /// collapse, and what to do about it. Two unkeyed claimants, or two on the same key, say nothing.</summary>
    internal static string? HideKeyCollisionWarning(string meshName, string? kept, string? incoming)
    {
        if (ModKeys.SameKey(kept, incoming)) return null;
        if (kept is null && incoming is null) return null;
        return HideKeyCollisionMessage(meshName, kept);
    }

    /// <summary>The same warning judged on the OR-LISTS the two claimants actually carry. A hide under a
    /// plan answers to every state that asks for it, so "its key" is a set — and reading one term off the
    /// front of that list would call two claimants agreed when they share only their first key, or
    /// disagreed when they name the same keys in another order. Two claimants naming no key say nothing,
    /// exactly as two unkeyed ones do above.</summary>
    internal static string? HideKeyCollisionWarning(string meshName, IReadOnlyList<KeyRef>? kept,
        IReadOnlyList<KeyRef>? incoming)
    {
        var keptKeys = HideKeyNames(kept);
        if (keptKeys.SetEquals(HideKeyNames(incoming))) return null;
        return HideKeyCollisionMessage(meshName, keptKeys.Count == 0
            ? null : string.Join(", ", keptKeys.OrderBy(key => key, StringComparer.Ordinal)));
    }

    private static HashSet<string> HideKeyNames(IReadOnlyList<KeyRef>? terms)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms ?? Array.Empty<KeyRef>())
            if (ModKeys.NormalizeRef(term) is { } normalized) names.Add(normalized.Key);
        return names;
    }

    private static string HideKeyCollisionMessage(string meshName, string? kept) =>
        $"mesh '{meshName}' is hidden by two changes with different toggle keys. One section can "
        + $"carry one key, so {kept ?? "no key"} applies. Set the same key on both";

    /// <summary>The first pair of siblings a twin guard's probe cannot tell apart AND at least one of whose
    /// verdicts the guarded section claims, as (earlier, later) verdict numbers — or null when every claimed
    /// verdict carries a tag value of its own. <paramref name="values"/> holds one tag value per sibling,
    /// indexed by verdict minus one.
    ///
    /// <para>Two UNCLAIMED siblings sharing a value cost the section nothing: whichever of them draws, the
    /// variable lands on a verdict the section does not admit, so it stays closed at both — which is what
    /// it wants at a sibling's draw anyway.</para></summary>
    internal static (int A, int B)? TwinValueCollision(IReadOnlyList<int> values,
        IReadOnlyCollection<int> ownVerdicts)
    {
        var claimed = new HashSet<int>(ownVerdicts);
        for (int b = 1; b <= values.Count; b++)
            for (int a = 1; a < b; a++)
                if (values[a - 1] == values[b - 1] && (claimed.Contains(a) || claimed.Contains(b)))
                    return (a, b);
        return null;
    }

    /// <summary>The refusal when two changes ask one stock texture for different images and the mechanism
    /// has only one image to give: the game-wide rebind, or a draw-scoped section a game-wide claim also
    /// wants. Both claimants are named so the author can tell which two edits met.</summary>
    internal static InvalidOperationException ImageCollision(string textureName, string first, string second) =>
        new($"stock texture '{textureName}' is retextured with two different images: {first} and {second}. "
            + "This texture isn't scoped to one outfit, so one image would have to win. "
            + "Give both changes the same image");

    /// <summary>The refusal when a change measures its stock texture as SHARED with other outfits and an
    /// earlier change already overrode that texture game-wide. The draw-scoped mechanism the sharing calls
    /// for is spent — one hash owns one section — and joining the game-wide one is not a smaller version of
    /// the same thing: a second gate under that section repaints every other wearer of the texture in the
    /// states this change answers, where they showed the game's own picture before. Both claimants are
    /// named. The different-image case says <see cref="ImageCollision"/> instead: there the two claims
    /// disagree about the picture as well.</summary>
    internal static InvalidOperationException SharedTextureAlreadyWide(string textureName, string first,
        string second) =>
        new($"stock texture '{textureName}' is retextured by {first} and {second}, and outfits outside "
            + "this mod use it too. The first change already overrides it everywhere, so the second cannot "
            + "be limited to its own outfit. Drop one of the two");

    /// <summary>The refusal when a change is set up at a position of the MOD'S OWN key other than the one
    /// holding the mod on. Every emitted gate carries the mod key at position 0 on top of the change's own
    /// term, so such a change nests one variable at two values and no press can open it. Names the part and
    /// the key.</summary>
    internal static InvalidOperationException ModKeyOffPosition(string mesh, string key) =>
        new($"'{mesh}' is set up at a position where key '{ModKeys.Display(key)}' switches the whole mod "
            + "off, so it never shows. Move the change to the position where the mod is on, or give the "
            + "key group its own key");

    /// <summary>The refusal when ONE outfit asks one stock texture for two different images at one of its
    /// mesh draws: one draw binds one image, so the later write would win unannounced. Only the sources
    /// tell the images apart.</summary>
    internal static InvalidOperationException OneOutfitImageCollision(string textureName, string outfit,
        string mesh, string first, string second) =>
        new($"stock texture '{textureName}' is retextured with two different images on {outfit}'s '{mesh}': "
            + $"'{first}' and '{second}'. One draw binds one image. Give both changes the same image");

    /// <summary>The refusal when two outfits ask one stock texture for different images at a mesh they BOTH
    /// draw: the presence gate is one verdict per frame, so it cannot separate two binds on a shared mesh
    /// and the later one would win for both. Names the shared mesh and both claimants; outfits with anchor
    /// meshes of their own each keep their image.</summary>
    internal static InvalidOperationException SharedAnchorImageCollision(string textureName, string mesh,
        string first, string second) =>
        new($"stock texture '{textureName}' is retextured with two different images on mesh '{mesh}': "
            + $"{first} and {second} both draw it, so one image would have to win. "
            + "Give both changes the same image");

    /// <summary>The refusal when an exact material property shares its Texture2D with another property and
    /// the requested route can identify only that Texture2D at draw time. Both visible slot labels and the
    /// mesh are named; the remedy offers the two honest mechanisms.</summary>
    internal static InvalidOperationException PropertyProbeCannotIsolate(string textureName, string mesh,
        string requestedProperty, string otherProperty)
    {
        string requested = Textures.TextureMap.PropertyLabel(requestedProperty);
        string other = Textures.TextureMap.PropertyLabel(otherProperty);
        return new InvalidOperationException(
            $"The original texture '{textureName}' is used by {requested} and {other} on '{mesh}', so this "
            + $"edit cannot reach {requested} alone at this draw. Leave this picture out, or change the "
            + "texture for every slot that draws it with a game-wide edit.");
    }

    /// <summary>Whether two material rows name the same exact Texture2D. A path-id-bearing row is exact;
    /// the name is the compatibility identity only where an old row has no path id.</summary>
    private static bool SameTextureResource(SubjectMap left, SubjectMap right) =>
        string.Equals(left.BundleId, right.BundleId, StringComparison.OrdinalIgnoreCase)
        && (left.PathId != 0 && right.PathId != 0
            ? left.PathId == right.PathId
            : string.Equals(left.TextureName, right.TextureName, StringComparison.Ordinal));

    /// <summary>The first other property of this material that binds the same Texture2D, in material order.</summary>
    private static SubjectMap? OtherPropertyOnResource(SubjectMaterial material, SubjectMap requested) =>
        material.Maps.FirstOrDefault(candidate =>
            !string.Equals(candidate.Slot, requested.Slot, StringComparison.Ordinal)
            && SameTextureResource(candidate, requested));

    /// <summary>One GAME-WIDE retexture while the build accumulates it: the stock texture it overrides and
    /// one image per claim. <see cref="Name"/> is the FIRST claim's section suffix — the hash owns one
    /// section however many claims reach it.</summary>
    private sealed class RetexBuild
    {
        public required string Name;
        public required string Hash;
        public readonly List<RetexClaim> Images = new();
    }

    /// <summary>One image under a <see cref="RetexBuild"/>: what it binds and the gate it binds under —
    /// <see cref="Key"/> for a claim answering one key position, <see cref="ShownBy"/> for one answering
    /// several. <see cref="Positions"/> is the same gate as the set of key positions it is open in, which
    /// is what decides whether two images can ever contend; <see cref="Claimant"/> names the edit, for
    /// <see cref="ImageCollision"/>.</summary>
    private sealed class RetexClaim
    {
        public required string Dds;
        public required KeyRef? Key;
        public required string? ShownBy;
        public required IReadOnlyList<KeyRef> Positions;
        public required string Claimant;
        public string? ShaderProperty;
        public string? DisplayLabel;
    }

    /// <summary>One draw-scoped retexture while the build accumulates it: the stock texture it overrides
    /// and one image per claiming outfit. <see cref="Claimant"/> names the edit that chose the mechanism,
    /// for <see cref="ImageCollision"/>; <see cref="MeshAt"/> maps each anchor's ib hash to the mesh it is,
    /// for <see cref="SharedAnchorImageCollision"/>.</summary>
    private sealed class ScopedBuild
    {
        public required string Name;
        public required string Hash;
        public required string TextureName;
        public required string Claimant;
        /// <summary>The part label of the FIRST claimant, for a refusal that has to name a change row.</summary>
        public required string Part;
        /// <summary>Exact measured register candidates for a generic property; null keeps the fixed-kind
        /// stock-map union used by the legacy routes.</summary>
        public IReadOnlyList<int>? Registers;
        public readonly List<ScopedImage> Images = new();
        public readonly Dictionary<string, string> MeshAt = new(StringComparer.Ordinal);
    }

    /// <summary>One image under a <see cref="ScopedBuild"/>: what it binds, the key that gates it, and the
    /// anchors it binds at. <see cref="Seen"/> dedupes the anchors per (ib, latch); <see cref="OwnerAt"/>
    /// names the outfit that brought each anchor and <see cref="Source"/> the authored file, both for the
    /// anchor-exclusivity refusals.</summary>
    private sealed class ScopedImage
    {
        public required string Dds;
        /// <summary>The key position this image binds under, or null when nothing switches it.</summary>
        public required KeyRef? Key;
        /// <summary>The content flag this image binds under, where the change that asked for it answers
        /// more than one position of its group. Null where one position does.</summary>
        public string? ShownBy;
        public required string Source;
        public readonly List<ScopedAnchor> Anchors = new();
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> OwnerAt = new(StringComparer.Ordinal);
    }

    /// <summary>What a mesh-dump dir holds, for the "one dump name means one mesh" rule: the mesh NAME and
    /// its index-buffer hash. NOT the bundle it was read from — two subjects wearing one asset reach it
    /// through their own bundles, and that is one mesh, not two.</summary>
    internal readonly record struct DumpIdentity(string MeshName, string IbHash);

    /// <summary>The mesh a capture claim stands on: the bundle it reads from, its name and its path id —
    /// the same triple an index buffer is hashed by, so one hash reached twice is one mesh exactly when
    /// the triples match.</summary>
    internal readonly record struct CaptureMesh(string Bundle, string MeshName, long PathId);

    /// <summary>Who holds an ib hash's one capture section: the mesh claiming it, and the label a refusal
    /// names that holder by.</summary>
    /// <summary>One claim on a capture section's hash: the mesh it captures, who claimed it, and the
    /// gate the build plan gave that claim. Two claims on one hash refuse unless the plan proves they never
    /// act together.</summary>
    internal readonly record struct HashClaim(CaptureMesh Mesh, string Claimant, BuildEmissionGate Gate)
    {
        public HashClaim(CaptureMesh mesh, string claimant)
            : this(mesh, claimant, BuildEmissionGate.Unconditional) { }
    }

    /// <summary>How a twin guard on one mesh can tell its siblings apart. <paramref name="OwnVariant"/>
    /// is 0 where the textures bound at the draw answer, and the mesh's wardrobe variant id where a
    /// sighting elsewhere on screen does; <paramref name="Witnesses"/> then names the section key that
    /// witnesses each variant of the sibling set and the verdict it writes.</summary>
    private readonly record struct TwinRoute(long OwnVariant,
        IReadOnlyList<(string Key, int Verdict)> Witnesses);

    /// <summary>One site's request for a guard on a shared signature key, carrying the route it was
    /// accepted on. <see cref="Hide"/> marks the hide pass's requests — the one kind answerable
    /// together on a key.</summary>
    private sealed record TwinRequest(SubjectModel Model, string Key, string OwnToken,
        IReadOnlyList<string> Mates, bool Hide, TwinRoute Route)
    {
        /// <summary>The textures at the mesh's own draw answer the probe; false = the verdict arrives
        /// from wardrobe sightings.</summary>
        public bool IsTextureRoute => Route.OwnVariant == 0;
    }

    /// <summary>One mesh's draw-signature entry: the key its sections act on, its own ib hash (dump
    /// and sharing identity), whether the key names it alone, the mate a refusal names, and every mate
    /// token sharing the key.</summary>
    private readonly record struct MeshSig(string Key, string Ib, bool Unique, string Mate,
        IReadOnlyList<string> Mates);

    /// <summary>The refusal naming two meshes on one draw signature when a change cannot act on one
    /// without hitting the other.</summary>
    private static InvalidOperationException TwinShipRefusal(string a, string b) =>
        new($"'{a}' and '{b}' can't be told apart in game. Changing one would change the other, so "
            + "this mod can't be built");

    /// <summary>Strike sightings whose key was seen under more than one verdict — such a mesh is worn
    /// with several options, so its drawing proves nothing — and demand every verdict in
    /// <paramref name="required"/> keeps at least one witness. Null = some verdict lost them all: its
    /// option could never contradict the others, and the sticky variable would stand at its draws
    /// with another option's answer in it.</summary>
    private static List<(string Key, int Verdict)>? StrikeContradictedWitnesses(
        IReadOnlyList<(string Key, int Verdict)> sightings, IEnumerable<int> required)
    {
        var contradicted = sightings.GroupBy(w => w.Key, StringComparer.Ordinal)
            .Where(g => g.Select(w => w.Verdict).Distinct().Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var kept = sightings.Where(w => !contradicted.Contains(w.Key)).ToList();
        return required.Any(v => !kept.Any(w => w.Verdict == v)) ? null : kept;
    }

    /// <summary>The refusal when <paramref name="dumpName"/> already means a different mesh than
    /// <paramref name="incoming"/>, else null. A silent dir reuse across different meshes would feed a
    /// pipeline foreign geometry, so identical content shares the dump and differing content refuses.</summary>
    internal static string? DumpNameConflict(string dumpName, DumpIdentity held, DumpIdentity incoming) =>
        held == incoming ? null
            : $"two mesh edits use the part name '{dumpName}' for different meshes ('{held.MeshName}' and "
                + $"'{incoming.MeshName}'), so they can't be in one mod. Remove one of the two mesh edits";

    /// <summary>The refusal when two Replaces land on ONE vanilla draw, else null. Two overrides on one
    /// hash fight over it and which wins is not something this build can decide — the same rule the install
    /// conflict read applies between mods. The test is the index-buffer HASH, so two subjects wearing one
    /// byte-identical mesh are caught where two same-named different meshes are not. Named per subject,
    /// because the author picked subjects, not hashes.
    ///
    /// <para>A claim carries the gate the BUILD PLAN gave it, and two claims the plan proves can never act
    /// in one session state are not a fight: one key stands in one position at a time, so the two overrides
    /// never both run. The proof is the planner's
    /// (<see cref="Project.BuildEmissionGate.ProvablyExclusiveOf"/>) — this reads it rather than deriving a
    /// second answer nobody compares against. Everything else still refuses, with the same words.</para></summary>
    internal static string? ReplacedMeshConflict(
        IEnumerable<(string Subject, string IbHash, BuildEmissionGate Gate)> replaced)
    {
        var claimed = new Dictionary<string, List<(string Subject, BuildEmissionGate Gate)>>(
            StringComparer.Ordinal);
        foreach (var (subject, hash, gate) in replaced)
        {
            if (!claimed.TryGetValue(hash, out var already))
            {
                claimed[hash] = new List<(string, BuildEmissionGate)> { (subject, gate) };
                continue;
            }
            foreach (var (heldSubject, heldGate) in already)
                if (!heldGate.ProvablyExclusiveOf(gate))
                    return $"'{heldSubject}' and '{subject}' replace one mesh they share, and only one "
                        + "replacement could show. Remove one of the two mesh edits, or switch them with one key";
            already.Add((subject, gate));
        }
        return null;
    }

    /// <summary>The members of <paramref name="group"/> a pipeline actually carries: those posing at least
    /// one of <paramref name="carried"/>, one entry per MESH. The formation lists every poser of every
    /// certifying cell — including pool candidates, which answer cells their variant doesn't gate — but a
    /// member posing none of the bones the gate admitted would be dumped and hash-claimed for nothing (the
    /// emitter sentinels its every row), and its claims could refuse the build over a draw-signature
    /// collision in a mesh this Replace doesn't lean on. The per-mesh cap is the merged group's
    /// one-writer-per-mesh invariant, held here where the sections are minted: the formation dedupes by
    /// roster INDEX, and two rows sharing one mesh name would write one gmap file twice.</summary>
    internal static IReadOnlyList<PoolDerive.PartBones> CoveredMembers(PoolDerive.VariantGroup group,
        IReadOnlyList<uint> carried)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<PoolDerive.PartBones>();
        foreach (var p in group.Members)
            if (carried.Any(p.Posed.Contains) && seen.Add(p.Mesh)) kept.Add(p);
        return kept;
    }

    /// <summary>Fail the build on a blocked game asset. Don't weaken or drop the calls to this
    /// (<see cref="BuildBlacklist"/>).</summary>
    private static void RefuseBlocked(params string?[] names)
    {
        foreach (var n in names)
            if (BuildBlacklist.IsBlocked(n))
                throw new BlockedAssetException($"'{n}' is not a supported asset");
    }

    /// <summary>The encoded DDS an authored source of this sRGB family ships as: encoded on first claim,
    /// answered to every later claim of the same image (<paramref name="claimed"/> spans submeshes and
    /// pipelines). Identity is the source's CONTENT, never its name — the donor intake writes a copy per
    /// submesh, so a name-keyed claim would ship one painted image once per submesh. The sRGB family is in
    /// the identity because the tag is baked into the container. MAP KIND is deliberately NOT in the key:
    /// <see cref="AuthoredDds.Encode"/> applies no kind-dependent transform, so one image serving a normal
    /// and an RMO slot is one file, and sharing it is the point. <paramref name="onEncode"/> fires only on
    /// a real encode; a <c>.dds</c> source passes through untouched.</summary>
    internal static string EncodeOnce(Dictionary<string, string> claimed, string source, bool srgb,
        Func<string> newDest, Action onEncode,
        Action<string>? log, string? cacheDir, int? encoderCpuLimit,
        Dictionary<string, string>? sourceIdentities = null)
    {
        string identity = sourceIdentities is null
            ? AuthoredDds.SourceIdentity(source)
            : SourceIdentity(sourceIdentities, source);
        string key = $"{identity}|{srgb}";
        if (claimed.TryGetValue(key, out var have)) return have;
        string dst = newDest();
        if (!AuthoredDds.IsPassthrough(source)) onEncode();
        AuthoredDds.Encode(source, dst, srgb, log, cacheDir, encoderCpuLimit, identity);
        claimed[key] = dst;
        return dst;
    }

    /// <summary>One content identity per resolved authored path for a build. The project snapshot settles
    /// those files before materialization starts, so every donor/retexture consumer shares the first hash.</summary>
    internal static string SourceIdentity(Dictionary<string, string> known, string source) =>
        known.TryGetValue(source, out var identity)
            ? identity : known[source] = AuthoredDds.SourceIdentity(source);

    /// <summary>The build-log line naming a texture encoder that is NOT the hardware device, so a build that
    /// took minutes per map says which rung it ran on; null for the hardware device. Log only: the encode
    /// is correct either way, so there is nothing for the author to fix.</summary>
    internal static string? EncoderRungLine(Bc7Encoder.Rung rung) => rung switch
    {
        Bc7Encoder.Rung.Hardware => null,
        Bc7Encoder.Rung.Warp => "texture encode: no GPU device, encoding on the WARP software renderer",
        _ => "texture encode: no graphics device, encoding on the managed encoder",
    };

    /// <summary>Warn per map kind some submesh binds whose anchor equivalent could not be slot-tagged: with
    /// no tag the bind has nothing to land on, and the geometry swaps wearing the anchor's own map. The
    /// wording separates the two binds because they cost the author differently: an authored map is work
    /// that won't show; a flat map on an untouched slot only fails to blank it.</summary>
    internal static void WarnUnbindableDonorMaps(IReadOnlyDictionary<int, SubmeshMaps> subMaps,
        IReadOnlyList<StockMapTag> stockMaps, string part, List<string> warnings)
    {
        void Warn(Func<SubmeshMaps, MapSlot> pick, StockMapKind kind, string name, string cost)
        {
            bool authored = subMaps.Values.Any(m => pick(m).File is not null);
            bool neutral = subMaps.Values.Any(m => pick(m).IsNeutral);
            if ((!authored && !neutral) || stockMaps.Any(t => t.Kind == kind)) return;
            warnings.Add($"No original {name} on '{part}' could be matched to a texture slot, so the "
                + (authored ? $"edited {name}" : $"blank {name}")
                + $" won't show in game. {cost}");
        }
        // a picture map that doesn't bind leaves the anchor's own picture on foreign UVs; a ramp that
        // doesn't bind changes nothing about the surface at all — the part keeps shading as it always did
        const string wrongPicture = "The original map shows on the new mesh's UVs.";
        Warn(m => m.Albedo, StockMapKind.Albedo, "base color", wrongPicture);
        Warn(m => m.Normal, StockMapKind.Normal, "normal", wrongPicture);
        Warn(m => m.Rmo, StockMapKind.Rmo, "RMO", wrongPicture);
        Warn(m => m.Blend, StockMapKind.Blend, "effect map", wrongPicture);
        Warn(m => m.Ramp, StockMapKind.Ramp, "toon ramp", "The part keeps its original toon ramp.");
    }

    /// <summary>The anchor's own stock maps, hashed offline and tagged with the kind whose slot they occupy,
    /// for the draw's slot probe. A map that won't hash warns with what it costs the author (the donor maps
    /// have nothing to bind through, so the anchor's own maps show); <paramref name="diagnostics"/> carries
    /// the reason. Tags come out in the anchor's material order, albedo then normal then RMO within each.
    /// <paramref name="partLabel"/> is the change-list label carried onto each tag, so an emitter refusal
    /// over one of these hashes can name its row.</summary>
    /// <param name="pictures">tag the three picture maps.</param>
    /// <param name="ramp">tag the toon ramp. Asked apart from <paramref name="pictures"/> so a replacement
    /// that binds one kind and not the other emits only the tags its draws probe for.</param>
    internal static List<StockMapTag> TagStockMaps(IReadOnlyList<Workbench.SubjectMaterial> anchorMaterials,
        string sfx, string part, Func<Workbench.SubjectMap, string> hashOf, List<string> warnings,
        List<string> diagnostics,
        string partLabel = "", bool pictures = true, bool ramp = false, bool blend = false)
    {
        var tags = new List<StockMapTag>();
        foreach (var material in anchorMaterials)
        {
            void Tag(Func<string, bool> isSlot, StockMapKind kind)
            {
                foreach (var m in material.Maps.Where(x => isSlot(x.Slot)))
                    try { tags.Add(new StockMapTag(hashOf(m), kind, partLabel)); }
                    catch (Exception ex) when (ex is not BlockedAssetException)
                    {
                        warnings.Add($"Couldn't match the original map '{m.TextureName}' on '{part}' to "
                            + "a texture slot. Some edited maps may not show in game.");
                        diagnostics.Add($"anchor map '{m.TextureName}' ({sfx}) can't be slot-tagged: {ex.Message}");
                    }
            }
            if (pictures)
            {
                Tag(Materials.MaterialResolver.IsBaseColor, StockMapKind.Albedo);
                Tag(Materials.MaterialResolver.IsNormal, StockMapKind.Normal);
                Tag(Materials.MaterialResolver.IsRmo, StockMapKind.Rmo);
            }
            if (blend) Tag(Materials.MaterialResolver.IsBlend, StockMapKind.Blend);
            if (ramp) Tag(Materials.MaterialResolver.IsRamp, StockMapKind.Ramp);
        }
        return tags;
    }

    /// <summary>The one build entry. The compiler receives settled plan output; it does not derive authored
    /// verbs, inclusion or capability from anything else.</summary>
    public static Result Build(AuthoredBuildExecution execution, BuildEnv env, string outRoot,
        Action<string>? log = null, bool zip = true, BuildCaches? caches = null,
        int? encoderCpuLimit = null)
    {
        ArgumentNullException.ThrowIfNull(execution);
        BuildCompletionCache.Prepared? prepared = null;
        if (caches?.CompletionDir is { Length: > 0 } completionDir)
        {
            prepared = BuildCompletionCache.Prepare(execution, env, outRoot, zip, completionDir);
            if (prepared is not null
                && BuildCompletionCache.TryServe(prepared, env, log, out var completed))
                return completed;
        }

        var observedBundles = prepared is null ? null : new BuildCompletionCache.BundleObserver(env);
        var buildEnv = observedBundles?.ObservedEnv ?? env;
        var logLines = prepared is null ? null : new List<string>();
        void CapturingLog(string message)
        {
            logLines!.Add(message);
            log?.Invoke(message);
        }
        var result = BuildCore(execution, buildEnv, outRoot,
            logLines is null ? log : CapturingLog, zip, caches, encoderCpuLimit,
            prepared?.SourceIdentities);
        // A degraded read (env.ReadDegraded) is asked AFTER the build so every lazy reader has answered;
        // a run that leaned on a conservative fallback publishes nothing, or the note's own remedy —
        // close the game, build again — would be served the degraded package it just warned about.
        if (prepared is not null && observedBundles is not null && logLines is not null
            && env.ReadDegraded?.Invoke() != true)
            BuildCompletionCache.TryPublish(prepared, env, observedBundles.BundleIds, result, logLines);
        return result;
    }

    private static Result BuildCore(AuthoredBuildExecution execution, BuildEnv env, string outRoot,
        Action<string>? log, bool zip, BuildCaches? caches, int? encoderCpuLimit,
        IReadOnlyDictionary<string, string>? preparedSourceIdentities = null)
    {
        var project = execution.Project;
        var authoredPlan = execution.Plan;
        // The materialization facts the build reads: what the target mesh measured when it was exported,
        // and which renderer slots a materialized game texture serves.
        var workspace = new AuthoredWorkspaceFacts(project);
        var rampGates = execution.RampGates;
        var rampShownFlags = execution.RampShownFlags;
        // Every key this build declares, with the cycle its group gives it. Where a key LAUNCHES is that
        // group's own to say and the group says it by ORDERING: a part that ships off has its content in a
        // later position while position 0 holds what it returns to. So a key launches at the position its
        // cycle names, and nothing carries a second, contradictable answer beside it.
        var keyCycles = execution.KeyCycles;
        var hiddenFlags = execution.HiddenFlags;
        var shownFlags = execution.ShownFlags;
        if (project.RootDir is null)
            throw new AuthoredRefusalException("this mod hasn't been saved yet. Save it, then build");
        var warnings = new List<string>();
        var infos = new List<string>();
        var diagnostics = new List<string>();
        var edits = execution.Work;
        // A ramp picked on an unreplaced part is a change of its own: nothing about the part's geometry or
        // pictures moves, and a mod may consist of nothing else.
        var rampPicks = execution.StockRamps;
        // what the plan decided, carried into this build's own account of itself
        warnings.AddRange(authoredPlan.Warnings);
        diagnostics.AddRange(authoredPlan.Parts.Select(part =>
            $"plan {part.Target.Subject} / {part.Target.Outfit} / {part.Target.RendererSlot}: "
            + (part.ActiveDecision?.Reason ?? part.Disposition.ToString())));
        diagnostics.AddRange(authoredPlan.Bindings.Select(binding =>
            $"plan {binding.RowId}: {binding.Decision.Verdict} - {binding.Decision.Reason}"));
        diagnostics.AddRange(authoredPlan.ProjectArtifacts.Select(artifact =>
            $"plan file {artifact.File}: {artifact.Reason}"));
        if (edits.Count == 0 && rampPicks.Count == 0)
            throw new AuthoredRefusalException("nothing to build. No edited meshes, edited textures, "
                + "toon ramp picks, or hidden meshes");
        // A ramp picked on an unreplaced part is referenced exactly as an edit's own files are, so it is
        // checked here beside them: a pick whose file the modder deleted fails fast, by name, rather than
        // partway through a build that has already written a folder.
        var dangling = edits
            .SelectMany(e => e.ReferencedFiles().Select(f => (Owner: e.Mesh, File: f)))
            .Concat(rampPicks.Select(r => (Owner: r.Mesh, File: r.Ramp)))
            .Where(x => { try { return !File.Exists(project.Resolve(x.File)); } catch { return true; } })
            .ToList();
        if (dangling.Count > 0)
            throw new AuthoredRefusalException("these files are missing from the mod folder: "
                + string.Join(", ", dangling.Select(d => $"{d.Owner}: {d.File}")));
        // one published name for the folder and its zip; the transients sit beside them under the same root
        string packageName = ModNaming.PackageFolderName(project.Info);
        string workDir = Path.Combine(outRoot, $".work-{packageName}");
        string tmpMod = Path.Combine(outRoot, $".tmp-{packageName}");
        string finalDir = Path.Combine(outRoot, packageName);
        string zipPath = Path.Combine(outRoot, packageName + ".zip");

        // One reader for the build: it keeps each bundle parsed once, and every read below goes through it.
        var reader = new Bundles.BundleReader();
        // The subject-level reads a pool derivation stands on — the bundle cache, the tier resolution, the
        // wardrobe scheme, the visibility rules and the roster probe itself. They live outside the build so
        // the ramp conversion can reach the same pool ANCHOR the build will bind the donor's maps at, rather
        // than deriving a second answer nobody compares against.
        var probe = new SubjectPoolProbe(env, reader, diagnostics);
        byte[] Bundle(string id, string why) => probe.Bundle(id, why);

        try
        {
            foreach (var d in new[] { workDir, tmpMod })
            {
                if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
                Directory.CreateDirectory(d);
            }

            // Which ps registers this build probes, off the shipped catalog. With no readable catalog the
            // classic range is probed instead: every slot-aware section predates the measurement and worked
            // over that range, so probing nothing would take those down along with the ramp.
            var slotCatalog = ShaderSlotCatalog.TryLoad(
                env.ShaderSlotCatalogFile ?? LabPaths.ShaderSlotCatalogFile, out var slotProblem);
            var slotPlan = slotCatalog is null ? ShaderSlotPlan.StockFloor : ShaderSlotPlan.For(slotCatalog);
            if (slotCatalog is null)
            {
                warnings.Add("This app can't read its texture slot data. Some maps won't show on "
                    + "replaced meshes, though the mesh swap itself still works. Reinstall the app to "
                    + "restore it.");
                diagnostics.Add($"shader slot catalog unavailable: {slotProblem}");
            }
            // A slot plan naming no ramp register binds a ramp at no draw: the probe sweep is empty, so a
            // ramp shipped under it would be a file nothing reads, a global tag on a game texture nothing
            // answers, and a repair record for something the mod cannot apply. A toon ramp in the project
            // is an explicit choice — picked on an installed material, or carried onto a replacement — so a
            // build that can emit none of them refuses rather than shipping a mod whose shading silently
            // did not change. It is the app's own data that is missing, which is what the fix names.
            if (slotPlan.Ramp.Count == 0 && authoredPlan.Bindings.FirstOrDefault(binding =>
                    binding.AuthoredSlot.Input == TargetInputKind.Ramp
                    && binding.Decision.Verdict == BuildPlanVerdict.Resolved
                    && binding.EffectiveValue?.ProjectAsset is not null) is { } rampAsked)
                throw new AuthoredRefusalException("the toon ramp on "
                    + $"'{rampAsked.AuthoredSlot.Part.RendererSlot}' can't be built: this app's texture "
                    + "slot data names no toon ramp slot. Reinstall the app to restore it");

            // ---- resolve subjects and map each edit onto its live roster part -----------------------
            var subjects = new Dictionary<(string, string), SubjectModel>();
            SubjectModel Subject(string character, string stem)
            {
                var key = (character.ToLowerInvariant(), stem.ToLowerInvariant());
                if (subjects.TryGetValue(key, out var m)) return m;
                var model = env.ResolveSubject(character, stem)
                    ?? throw new AuthoredRefusalException(
                        $"'{character} · {stem}' isn't in the current game install. Rescan, then build again");
                return subjects[key] = model;
            }

            var work = new List<(BuildWorkItem Edit, SubjectModel Model, SubjectPart Part)>();
            foreach (var e in edits)
            {
                var model = Subject(e.Character, e.Outfit);
                var part = model.Parts.FirstOrDefault(p =>
                        string.Equals(p.SlotName, e.Mesh, StringComparison.OrdinalIgnoreCase))
                    ?? throw new AuthoredRefusalException(
                        $"'{e.Mesh}' is no longer a part of {e.Character} · {e.Outfit}. The game may have "
                        + "changed since this edit was made");
                RefuseBlocked(model.Character, model.Stem, part.SlotName, part.MeshAddress);
                work.Add((e, model, part));
            }

            // ---- repair data, accumulated as the build goes ------------------------------------------
            // What a later read of the shipped folder cannot recover from the folder alone: which files a
            // change's geometry landed in and in what shape, which shipped .dds an authored slot became,
            // and which game texture a retexture overrode. Keyed on the work item, which every route
            // below carries. Nothing here changes what the build emits.
            var repairRoutes = new Dictionary<BuildWorkItem, (string Sfx, string Route)>();
            var repairGeometry = new Dictionary<BuildWorkItem, RepairData.GeometryRecord>();
            var repairGroupBones = new Dictionary<BuildWorkItem, IReadOnlyList<uint>>();
            var repairMaps = new Dictionary<BuildWorkItem, Dictionary<(int, DonorMapSlot), string>>();
            var repairStock = new Dictionary<BuildWorkItem, Dictionary<(int, DonorMapSlot), RepairData.StockTextureRef>>();
            var repairPropertyMaps = new Dictionary<BuildWorkItem, Dictionary<(int, string), string>>();
            var repairPropertyStock = new Dictionary<BuildWorkItem,
                Dictionary<(int, string), RepairData.StockTextureRef>>();
            // A ramp the CONVERSION carried across rather than the modder picking, and the game texture it
            // stands in for — read straight off the row, which is where the two are told apart. The record
            // says which it was, because they read back differently: a carried ramp is a property of where
            // the geometry came from, and re-carrying it is what a re-import would do — a picked one is a
            // choice, and only the file preserves it.
            RepairData.StockTextureRef? CarriedRampSource(SubmeshTextures t) =>
                t.RampIsCarried
                    ? new RepairData.StockTextureRef(t.RampCarried!.Bundle, t.RampCarried.Name,
                        TextureUsersOf(t.RampCarried.Bundle, t.RampCarried.Name), t.RampCarried.PathId)
                    : null;

            void RecordDonorMaps(BuildWorkItem edit, IReadOnlyDictionary<int, SubmeshMaps> subMaps)
            {
                var shipped = new Dictionary<(int, DonorMapSlot), string>();
                foreach (var (submesh, maps) in subMaps)
                {
                    void Take(MapSlot slot, DonorMapSlot which)
                    {
                        if (slot.File is { } abs) shipped[(submesh, which)] = Path.GetFileName(abs);
                    }
                    Take(maps.Albedo, DonorMapSlot.BaseColor);
                    Take(maps.Normal, DonorMapSlot.Normal);
                    Take(maps.Rmo, DonorMapSlot.Rmo);
                    Take(maps.Ramp, DonorMapSlot.Ramp);
                    Take(maps.Blend, DonorMapSlot.Blend);
                    foreach (var property in maps.Properties ?? Array.Empty<PropertyMapSlot>())
                        if (property.Map.File is { } abs)
                        {
                            if (!repairPropertyMaps.TryGetValue(edit, out var propertyFiles))
                                repairPropertyMaps[edit] = propertyFiles = new Dictionary<(int, string), string>();
                            propertyFiles[(submesh, property.ShaderProperty)] = Path.GetFileName(abs);
                        }
                }
                repairMaps[edit] = shipped;
            }

            // One change as the repair record states it: the project's own identity for it, the identity of
            // the game asset it was pointed at, the intent that cannot be read back off the emitted ini,
            // and (for a Replace) how to read its shipped buffers.
            // The plan's account of one part.
            PlannedPart PlannedFor(SubjectModel model, SubjectPart part)
            {
                return authoredPlan.Parts.SingleOrDefault(candidate =>
                    string.Equals(candidate.Target.Subject, model.Character,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Target.Outfit, model.Stem,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.Target.RendererSlot, part.SlotName,
                        StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"repair intent has no Build-plan part for '{part.SlotName}'");
            }

            /// <summary>Every group containing a placement for this part, derived from the plan account.</summary>
            IReadOnlyList<RepairData.KeyGroupRecord>? RepairKeyGroups(PlannedPart planned,
                PlannedPartOperation? operation)
            {
                if (planned.GroupTouches.Count == 0) return null;
                return planned.GroupTouches.Select(touch =>
                {
                    var states = Enumerable.Range(0, touch.StateCount).Select(index =>
                    {
                        bool Placed(PlannedPartOperation candidate) => candidate.ActiveWhen.Any(condition =>
                            condition.IsAlways || string.Equals(condition.GroupId, touch.GroupId,
                                StringComparison.Ordinal) && condition.StateIndex == index);
                        var active = planned.Operations.Where(Placed).ToList();
                        var selected = active.FirstOrDefault(candidate =>
                                candidate.Disposition == PlannedPartDisposition.Hidden)
                            ?? active.FirstOrDefault(candidate =>
                                candidate.Disposition == PlannedPartDisposition.Edit);
                        return new RepairData.KeyGroupStateRecord(index,
                            IntentName(selected?.Disposition ?? PlannedPartDisposition.Vanilla),
                            selected?.EditDefinitionId, EditLabel(selected?.EditDefinitionId));
                    }).ToArray();
                    int stateIndex = operation?.ActiveWhen.FirstOrDefault(condition =>
                        string.Equals(condition.GroupId, touch.GroupId, StringComparison.Ordinal))?.StateIndex ?? 0;
                    return new RepairData.KeyGroupRecord(touch.GroupId, touch.Key, touch.StateCount, 0,
                        stateIndex, states);
                }).ToArray();
            }

            /// <summary>The author's own name for one edit, or null where the project has none.</summary>
            string? EditLabel(string? editDefinitionId) => editDefinitionId is null ? null
                : project.EditDefinitions.FirstOrDefault(edit =>
                    string.Equals(edit.Id, editDefinitionId, StringComparison.Ordinal))?.Label;

            // Read off the position THIS record carries, not the part's first answer: a part answered by
            // three positions writes three records, and the part-level fields name only the first — which
            // would file every position's bindings and disposition under position 0's.
            RepairData.IntentRecord? RepairIntent(PlannedPart planned, PlannedPartOperation? operation)
            {
                string? editId = operation?.EditDefinitionId ?? planned.EditDefinitionId;
                var disposition = operation?.Disposition ?? planned.Disposition;
                var rows = authoredPlan.Bindings.Where(binding =>
                        string.Equals(binding.EditDefinitionId, editId, StringComparison.Ordinal))
                    .Select(binding =>
                    {
                        var proof = binding.Decision.TargetingProof;
                        var sourceSlot = binding.EffectiveValue?.SourceGameSlot;
                        GameAssetRef? effectiveGame = sourceSlot is null ? null
                            : sourceSlot.Input == TargetInputKind.Geometry ? sourceSlot.Mesh
                            : sourceSlot.Material ?? sourceSlot.Mesh ?? sourceSlot.Renderer;
                        return new RepairData.IntentBindingRecord(
                            binding.AuthoredSlot.Id,
                            IntentName(binding.AuthoredSlot.Input),
                            binding.AuthoredSlot.Semantic,
                            new RepairData.IntentTargetSlotRecord(
                                IntentName(binding.CurrentSlot!.Domain),
                                binding.CurrentSlot!.Tier,
                                binding.CurrentSlot.SubmeshIndex,
                                binding.CurrentSlot.MaterialSlotIndex,
                                binding.CurrentSlot.Renderer,
                                binding.CurrentSlot.Mesh,
                                binding.CurrentSlot.Material),
                            IntentName(binding.RequestedBinding.Kind),
                            binding.RequestedBinding.ProjectAssetId,
                            binding.RequestedBinding.SourceSlot is { } requestedSource
                                ? new RepairData.IntentSourceSlotRecord(requestedSource.SlotId,
                                    requestedSource.EditDefinitionId) : null,
                            binding.EffectiveValue is { } effective
                                ? IntentName(effective.Kind) : "unresolved",
                            binding.EffectiveValue?.ProjectAsset?.Id,
                            effectiveGame,
                            IntentName(binding.Decision.Verdict),
                            binding.Decision.Reason,
                            proof is null ? null
                                : new RepairData.IntentProofRecord(proof.Kind, proof.Detail),
                            binding.Emissions.Select(emission => emission.Id).ToArray(),
                            binding.AuthoredSlot.Input == TargetInputKind.Texture
                                ? binding.AuthoredSlot.ShaderProperty : null);
                    }).ToList();
                return new RepairData.IntentRecord(IntentName(disposition), editId, rows);
            }

            RepairData.ChangeRecord RepairChange(BuildWorkItem edit, SubjectModel model, SubjectPart part)
            {
                string? bundle = null;
                long? pathId = null;
                try
                {
                    var (_, bid, pid) = Tiers(part)[0];
                    bundle = bid;
                    if (pid != 0) pathId = pid;
                }
                catch (Exception ex) when (ex is not BlockedAssetException)
                {
                    diagnostics.Add($"repair data: '{edit.Mesh}' has no resolvable source bundle "
                        + $"({ex.Message}), so its record names none");
                }
                var planned = PlannedFor(model, part);
                var operation = edit.Operation;
                var maps = repairMaps.GetValueOrDefault(edit);
                var stock = repairStock.GetValueOrDefault(edit);
                var propertyMaps = repairPropertyMaps.GetValueOrDefault(edit);
                var propertyStock = repairPropertyStock.GetValueOrDefault(edit);
                var route = repairRoutes.GetValueOrDefault(edit);
                var rows = RepairData.Submeshes(edit.Textures ?? new List<SubmeshTextures>(),
                    (t, which) => maps?.GetValueOrDefault((t.Submesh, which)),
                    (t, which) => which == DonorMapSlot.Ramp
                        ? CarriedRampSource(t) ?? stock?.GetValueOrDefault((t.Submesh, which))
                        : stock?.GetValueOrDefault((t.Submesh, which)),
                    (t, which) => which == DonorMapSlot.Ramp && t.RampIsCarried
                        ? RepairData.CarriedFromDonor : null,
                    (t, property) => propertyMaps?.GetValueOrDefault((t.Submesh, property)),
                    (t, property) => propertyStock?.GetValueOrDefault((t.Submesh, property)));
                bool replace = edit.Verb == EditVerbs.Replace;
                // Subject named off the RESOLVED roster, the same source the subject list takes, so the two
                // sides of that join read identically rather than differing in case (the edit carries the
                // casing the modder's own selection was saved under, and every join in the app is
                // case-insensitive, so a difference would surface only here).
                return new RepairData.ChangeRecord(edit.Verb, model.Character, model.Stem, edit.Mesh,
                    bundle, pathId,
                    BundleContent: bundle is null ? null : env.BundleContentHash?.Invoke(bundle),
                    Suffix: route.Sfx, Route: route.Route,
                    ToggleKey: edit.Toggle is { } b
                        ? new RepairData.KeyBinding(b.Key, b.HideWhenOff, b.StartsOff) : null,
                    KeyGroups: RepairKeyGroups(planned, operation),
                    BakedRest: replace ? edit.BakedRest : null,
                    OriginalVerts: replace ? edit.OriginalVerts : null,
                    DonorMaterials: replace ? edit.DonorMaterials : null,
                    Geometry: repairGeometry.GetValueOrDefault(edit),
                    Textures: rows.Count > 0 ? rows : null,
                    Intent: RepairIntent(planned, operation));
            }

            // The mesh list a materialized texture target carries, joined on the (name, bundle) identity
            // the game holds the asset under — the same join the verb derivation matches a texture edit to
            // a material's map by. Null for a texture no target records.
            IReadOnlyList<string>? TextureUsers(SubjectMap stock) =>
                TextureUsersOf(stock.BundleId, stock.TextureName);

            /// <inheritdoc cref="TextureUsers"/>
            IReadOnlyList<string>? TextureUsersOf(string bundleId, string textureName) =>
                workspace.TextureUsersOf(bundleId, textureName);

            // ---- toggle keys: tier 1 (the whole mod) + tier 2 (one change) ---------------------------
            // One key = one emitted variable, so two changes on the same key switch together. Sharing one is
            // the author's call to make; the emission is the same either way.
            string? modKey = ModKeys.Normalize(project.Info.ToggleKey);
            // The mod's own key is the whole-mod switch, and the emission holds it to two positions — on at
            // 0, off at 1 — whatever else is bound to it. A group cycling further on that same key would
            // wrap at 2 and leave every position past the first two unreachable, so the states nobody could
            // ever select are refused by name here rather than shipped.
            foreach (var cycle in keyCycles)
                if (ModKeys.SameKey(cycle.Key, modKey) && cycle.StateCount > 2)
                    throw new AuthoredRefusalException(
                        $"key '{ModKeys.Display(cycle.Key)}' switches the whole mod and also cycles "
                        + $"{cycle.StateCount} states of a key group. The whole-mod key toggles the mod on "
                        + "and off; a key cycling N states needs its own key");
            // ---- what one work item's gate is -------------------------------------------------------
            // The plan states it: the group position this content answers, and the positions of other
            // groups that take the part off screen.
            static KeyRef? ContentTerm(BuildWorkItem e) => e.Gate.Content;

            // The OR-list of key positions demanding a draw suppressed. A hide carries every state that
            // asks for it.
            static IReadOnlyList<KeyRef> HideTerms(BuildWorkItem e) => e.Gate.HiddenWhen;

            // The flag a part's content gate reads when ANOTHER group's state hides it, or null when
            // nothing else does.
            static string? HiddenByFlag(BuildWorkItem e) => e.Gate.HiddenBy;

            // The flag a change's own content gate stands on when MORE than one position of its group
            // answers with it, or null when one does. Where it is set the position term is not: the flag
            // is the or-of-positions, and naming one of them beside it would gate the draw to that one.
            static string? ShownByFlag(BuildWorkItem e) => e.Gate.ShownBy;

            // The position a change's content gate names, or null where a content flag names them all.
            static KeyRef? ContentGateTerm(BuildWorkItem e) =>
                ShownByFlag(e) is null ? ContentTerm(e) : null;

            // The positions a change's own content gate stands on, in the vocabulary the emitted ini tests:
            // the single key position it names, or every position that raises its content flag. EMPTY says
            // the gate rests on no key at all — a change no key switches, or one whose group has no key
            // bound — which is open in every session state and so provably disjoint from nothing.
            var shownPositions = shownFlags.ToDictionary(flag => flag.Name, flag => flag.WhenAny,
                StringComparer.Ordinal);
            IReadOnlyList<KeyRef> GatePositions(BuildWorkItem e)
            {
                var raw = ShownByFlag(e) is { } flag
                    ? shownPositions.GetValueOrDefault(flag) ?? Array.Empty<KeyRef>()
                    : ContentGateTerm(e) is { } one ? new[] { one } : Array.Empty<KeyRef>();
                var positions = new List<KeyRef>(raw.Count);
                foreach (var position in raw)
                {
                    // one position naming no usable key gates nothing, so the whole set proves nothing
                    if (ModKeys.NormalizeRef(position) is not { } named) return Array.Empty<KeyRef>();
                    positions.Add(named);
                }
                return positions;
            }

            // The mod's own key gates EVERY emitted block on top of whatever the block's own gate says, and
            // it gates them at position 0 — the position holding the mod on. So a change standing only on
            // positions of that same key past 0 nests one variable at two values: a block no press can
            // reach, which would ship its files and its section and never draw. Refused by name here, at
            // the one place every route's gate is read, rather than at any one route's accumulation.
            //
            // Read the way NeverTogether reads: on the EMITTED key, and only where the positions prove it.
            // An empty set rests on no key and gates nothing; a change covering position 0 as well as a
            // later one raises its content flag at 0 too, so its gate does open and it is left alone. The
            // longer-cycle tripwire above runs first, so a group cycling past two positions on this key is
            // already refused under its own account.
            foreach (var e in edits)
            {
                var positions = GatePositions(e);
                if (positions.Count > 0 && positions.All(position =>
                        ModKeys.SameKey(position.Key, modKey) && position.State != 0))
                    throw ModKeyOffPosition(e.Mesh, positions[0].Key);
            }

            // Whether two gates can NEVER be open in one session state. Provable only where both stand on
            // positions and every pair of them names ONE key at two of its positions: a key holds one
            // position at a time, while two keys stand wherever they like. Read off the emitted key rather
            // than the authored group, so two groups sharing one key are separated (they share the
            // variable) and a group with no key separates nothing (its states gate nothing). Conservative
            // by construction: what this cannot prove reads as "these two can meet".
            static bool NeverTogether(IReadOnlyList<KeyRef> a, IReadOnlyList<KeyRef> b) =>
                a.Count > 0 && b.Count > 0
                && a.All(x => b.All(y => ModKeys.SameKey(x.Key, y.Key) && x.State != y.State));

            // The extra guarded skips a change owes: the states of its own group answering the part
            // hidden, and every other group's state that hides it.
            static IReadOnlyList<KeyRef>? SuppressTerms(BuildWorkItem e) =>
                e.Gate.HiddenWhen.Count > 0 ? e.Gate.HiddenWhen : null;

            // Whether every state of this change's group suppresses the part, which is what the released
            // hide-while-off shape says and is emitted the same way.
            static bool SuppressesInEveryState(BuildWorkItem e) => e.Gate.SuppressesInEveryState;

            // The compiled work item one planned binding belongs to: the state whose own answer names
            // that change, not the part's first answer. A part answered by three states is three work
            // items, and the part-level field names only the first — reading it would file every state's
            // binding under the first state's carrier and install its patches under the wrong gate.
            BuildWorkItem? EditAnswering(string? editDefinitionId) => editDefinitionId is null ? null
                : edits.FirstOrDefault(item => string.Equals(item.Operation?.EditDefinitionId,
                    editDefinitionId, StringComparison.Ordinal));

            // The plan's own statement of when a work item acts, which the hash guards read exclusivity
            // off.
            static BuildEmissionGate PlanGate(BuildWorkItem e) => e.Gate.Gate;

            // A part answered by more than one state is more than one pipeline, and every shipped file,
            // resource and section is named by suffix — so the state joins the name where, and only where,
            // there is more than one. A part with a single answer keeps the name it has always had.
            //
            // The suffix names the POSITIONS an answer covers, and never the content flag that stands for
            // them: a flag is minted per build, while a shipped file's name has to come back the same on a
            // rebuild of the same project. An answer covering several positions is thus as nameable as one
            // covering a single position, which is what two of them on one part need in order to differ.
            var stateSuffixes = new Dictionary<BuildWorkItem, string>();
            foreach (var answers in edits
                         .Where(e => e.Verb is EditVerbs.Replace or EditVerbs.Retexture)
                         .GroupBy(PartKey, StringComparer.OrdinalIgnoreCase))
            {
                if (answers.Count() < 2) continue;
                // The positions alone tell two answers of one part apart, with nothing else in the name.
                // AuthoredBuildPlanner.ContentActivationConflicts refuses any two content edits of one part
                // whose gates it cannot prove exclusive, and BuildEmissionGate.ProvablyExclusiveOf proves
                // exclusivity only for terms of ONE group standing at different states — so two answers
                // that reach here name disjoint, non-empty position sets and cannot suffix alike. Should
                // that planner rule ever relax, the emitter's own suffix-uniqueness check refuses the build
                // by name rather than letting one answer's files overwrite the other's.
                foreach (var e in answers) stateSuffixes[e] = PositionSuffix(e);
            }
            // The positions themselves, sorted so the name does not depend on the order the plan listed
            // them in. Empty for an answer no key group switches, which is the only answer its part has.
            static string PositionSuffix(BuildWorkItem e)
            {
                var states = PlanGate(e).ActiveWhen.Where(term => !term.IsAlways)
                    .Select(term => term.StateIndex).Distinct().OrderBy(state => state).ToList();
                return states.Count == 0 ? "" : "_s" + string.Join("_", states);
            }
            static string PartKey(BuildWorkItem e) => $"{e.Character}\u001f{e.Outfit}\u001f{e.Mesh}";
            string StateSuffix(BuildWorkItem e) => stateSuffixes.GetValueOrDefault(e, "");

            // Only the flags some shipped change actually READS. A change held back on this install leaves
            // no gate behind, and a texture edit has no draw gate of its own to read one from — what a
            // hiding position does to such a part is said by the guarded skip on its draw instead. A flag
            // nothing tests would declare a variable and recompute it for nothing.
            var shippedFlags = hiddenFlags
                .Where(flag => edits.Any(e => e.Verb == EditVerbs.Replace
                    && string.Equals(HiddenByFlag(e), flag.Name, StringComparison.Ordinal))).ToList();
            // and the content flags some shipped change stands on. Every route can read one — a
            // replacement's draw gate, a retexture's bind, a ramp pick's — so the only question is whether
            // the change survived this install.
            var shippedShownFlags = shownFlags
                .Where(flag => edits.Any(e => string.Equals(ShownByFlag(e), flag.Name,
                    StringComparison.Ordinal))).ToList();

            // every tier of a part, forward-resolved: (mesh name, bundle id, path id)
            List<(string Name, string BundleId, long PathId)> Tiers(SubjectPart part) => probe.Tiers(part);

            // Hashing an index buffer parses the mesh out of its bundle; memoized because several sites
            // can ask about one out-of-index mesh.
            var ibHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            string IbHash(string bundleId, string meshName, long pathId)
            {
                string key = $"{bundleId}|{meshName}|{pathId}";
                if (ibHashes.TryGetValue(key, out var have)) return have;
                return ibHashes[key] = BufferHash
                    .Compute(Bundle(bundleId, $"mesh '{meshName}'"), meshName, pathId, reader).Ib.ToString("x8");
            }

            // A mesh's stable identity for the operator cache: the game's own address for it, under the
            // catalog version that decides what that address resolves to. No catalog version means nothing
            // pins the contents, so the part is solved fresh.
            string? OpKey(string bundleId, string meshName, long pathId) =>
                env.CatalogVersion is { } cv ? $"{cv}|{bundleId}|{meshName}|{pathId}" : null;

            // A replaced mesh's vanilla draw shapes: the (firstIndex, indexCount) of each submesh draw
            // the game issues, plus the full index count — what the emitter's per-submesh draw sections
            // match on. Parses the mesh out of its bundle; memoized like IbHash for the same reason.
            var drawShapes = new Dictionary<string, DrawShapeSet>(StringComparer.Ordinal);
            DrawShapeSet ShapesOf(string bundleId, string meshName, long pathId)
            {
                string key = $"{bundleId}|{meshName}|{pathId}";
                if (drawShapes.TryGetValue(key, out var have)) return have;
                var raw = Mesh.MeshRaw.From(
                    reader.GetMeshField(Bundle(bundleId, $"mesh '{meshName}'"), meshName, pathId)
                    ?? throw new AuthoredRefusalException(
                        $"the game files no longer hold the mesh '{meshName}'. Rescan, then build again"));
                int step = raw.IndexFormat == 0 ? 2 : 4;
                return drawShapes[key] = new DrawShapeSet(
                    raw.Submeshes.Select(s => new DrawShape((int)(s.FirstByte / step), (int)s.IndexCount)).ToList(),
                    raw.Index.Length / step);
            }

            // ---- draw-signature keys ---------------------------------------------------------------
            // Sections act by hash, and one subject can ship two DIFFERENT meshes on one index-buffer
            // hash (wardrobe remodels reuse a garment's topology), so a section on that hash fires on
            // both draws. Every section therefore keys on the mesh's SIGNATURE KEY: the ib hash, unless
            // another mesh of the subject shares it with different content — then each content class
            // keys on its vb1 hash instead. Byte-identical meshes under two names are one draw
            // signature legitimately and keep the shared key. A mesh left AMBIGUOUS (vb1 missing or
            // colliding too) keeps the ib key unmarked as unique when ANOTHER PART shares it: it may still
            // feed a pool the way it always has, but acting on it directly refuses, naming the part it
            // collides with. One part's own tiers are one subject, never twins of each other.
            // Each entry carries the mate a refusal names AND every mate token the class shares its key
            // with, which is what a discriminator has to be compared against.
            var sigIndexes = new Dictionary<(string, string), Dictionary<string, MeshSig>>();
            Dictionary<string, MeshSig> SignatureIndex(SubjectModel model)
            {
                var subjectKey = (model.Character.ToLowerInvariant(), model.Stem.ToLowerInvariant());
                if (sigIndexes.TryGetValue(subjectKey, out var have)) return have;

                var meshes = new List<(string Name, string Bid, long Pid, BufferHash.Hashes H, bool Vb1Bound, string Token)>();
                foreach (var p in model.Parts)
                {
                    List<(string Name, string BundleId, long PathId)> tiers;
                    try { tiers = Tiers(p); } catch { continue; }   // unreadable here = nothing to key a section on
                    foreach (var (name, bid, pid) in tiers)
                    {
                        try
                        {
                            var field = reader.GetMeshField(Bundle(bid, $"mesh '{name}'"), name, pid)
                                ?? throw new AuthoredRefusalException(
                        $"the game files no longer hold the mesh '{name}'. Rescan, then build again");
                            var raw = Mesh.MeshRaw.From(field);
                            // the vb1 hash is a usable key only when the mesh's SECOND stream is stream 1,
                            // the UV/colour buffer the draw binds — a skin stream in that ordinal is
                            // CPU-side and its hash matches nothing at runtime
                            bool vb1Bound = raw.StreamIds.Count > 1 && raw.StreamIds[1] == 1;
                            meshes.Add((name, bid, pid, BufferHash.Compute(raw), vb1Bound, p.Token));
                        }
                        catch { }
                    }
                }

                // ib groups → content classes → candidate keys; one triple can be reached twice (a tier
                // shared between parts), which is one mesh, not two
                var index = new Dictionary<string, MeshSig>(StringComparer.Ordinal);
                var byCandidate = new Dictionary<string, List<(string Trip, string ClassId, string Token, string Ib,
                    IReadOnlyList<string> ForcedMates)>>(StringComparer.Ordinal);
                int classSeq = 0;
                foreach (var group in meshes.GroupBy(m => m.H.Ib)
                             .Select(g => g.DistinctBy(m => (m.Bid, m.Name, m.Pid)).ToList()))
                {
                    // Partition the group into byte-identical content classes. Comparisons re-parse the
                    // meshes, so each group member is materialized once for the whole partition rather
                    // than once per comparison; a singleton group compares nothing and parses nothing.
                    var raws = new Dictionary<string, Mesh.MeshRaw>(StringComparer.Ordinal);
                    Mesh.MeshRaw RawOf(string bid, string name, long pid) =>
                        raws.TryGetValue($"{bid}|{name}|{pid}", out var have)
                            ? have
                            : raws[$"{bid}|{name}|{pid}"] = Mesh.MeshRaw.From(
                                reader.GetMeshField(Bundle(bid, $"mesh '{name}'"), name, pid)
                                ?? throw new AuthoredRefusalException(
                        $"the game files no longer hold the mesh '{name}'. Rescan, then build again"));
                    var classes = new List<List<(string Name, string Bid, long Pid, BufferHash.Hashes H, bool Vb1Bound, string Token)>>();
                    foreach (var m in group)
                    {
                        var home = classes.FirstOrDefault(c =>
                        {
                            try { return MeshBytesEqual(RawOf(c[0].Bid, c[0].Name, c[0].Pid), RawOf(m.Bid, m.Name, m.Pid)); }
                            catch { return false; }
                        });
                        if (home is null) classes.Add(new() { m }); else home.Add(m);
                    }
                    // In a shared group a class may key on its vb1 only when the draw binds one AND no
                    // sibling class answers to the same value; everything else keeps its own ib — which
                    // the siblings also draw on, so it can never be unique, even standing alone in its
                    // key bucket.
                    string GroupVb1(int i) => classes[i][0].Vb1Bound ? classes[i][0].H.Vb1?.ToString("x8") ?? "" : "";
                    for (int ci = 0; ci < classes.Count; ci++)
                    {
                        var cls = classes[ci];
                        string ib = cls[0].H.Ib.ToString("x8");
                        string vb1 = GroupVb1(ci);
                        bool separates = classes.Count > 1 && vb1.Length > 0
                            && Enumerable.Range(0, classes.Count).All(j => j == ci || GroupVb1(j) != vb1);
                        string candidate = separates ? vb1 : ib;
                        bool forced = classes.Count > 1 && !separates;
                        // every OTHER class of the ib group draws on this class's key too, so all their
                        // tokens are mates a discriminator has to tell this class apart from
                        IReadOnlyList<string> forcedMates = forced
                            ? Enumerable.Range(0, classes.Count).Where(j => j != ci)
                                .SelectMany(j => classes[j].Select(m => m.Token)).ToList()
                            : Array.Empty<string>();
                        string classId = $"c{classSeq++}";
                        if (!byCandidate.TryGetValue(candidate, out var list)) byCandidate[candidate] = list = new();
                        foreach (var m in cls)
                            list.Add(($"{m.Bid}|{m.Name}|{m.Pid}", classId, m.Token, ib, forcedMates));
                    }
                }
                // closure: a candidate shared by more than one content class is unique for nobody — a
                // vb1 landing on some other mesh's ib, or the reverse, demotes both here. A demoted or
                // forced mesh keys on its own ib: the honest legacy signature, and the one already-shipped
                // sidecars and dumps recorded.
                foreach (var (key, members) in byCandidate)
                {
                    foreach (var (trip, classId, token, ib, forcedMates) in members)
                    {
                        var mates = forcedMates
                            .Concat(members.Where(o => o.ClassId != classId).Select(o => o.Token))
                            .Where(t => !string.Equals(t, token, StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        bool unique = mates.Count == 0;
                        index[trip] = new MeshSig(unique ? key : ib, ib, unique,
                            mates.FirstOrDefault() ?? "", mates);
                    }
                }
                return sigIndexes[subjectKey] = index;
            }

            // The signature a mesh's sections key on. A mesh outside the index (an unreadable sibling)
            // keys on its ib: nothing measured contests it.
            MeshSig SigOf(SubjectModel model, string bid, string name, long pid)
            {
                if (SignatureIndex(model).TryGetValue($"{bid}|{name}|{pid}", out var sig)) return sig;
                string ib = IbHash(bid, name, pid);
                return new MeshSig(ib, ib, true, "", Array.Empty<string>());
            }

            // The subject-level answers the pool derivation and every wardrobe question read, all off the
            // one probe so a build and a conversion cannot arrive at different ones.
            IReadOnlyList<Tables.PartScheme.Slot>? SchemeOf(SubjectModel model) => probe.SchemeOf(model);

            // Sections whose signature key another mesh draws on too, recorded where each site meets one.
            // The guards themselves are built in one pass at the end: a verdict number spans every sibling
            // of the key, and a sibling's tag value is only knowable once the build's slot tags and scoped
            // retextures are settled.
            // Several hides on one key are answerable together — one section skipping on each hidden
            // sibling's verdict — where two sites wanting the section to ACT for them are not.
            var twinRequests = new List<TwinRequest>();
            void RecordTwinGuard(SubjectModel model, SubjectPart part, string key,
                IReadOnlyList<string> mates, TwinRoute route, bool hide = false) =>
                twinRequests.Add(new TwinRequest(model, key, part.Token, mates, hide, route));

            // The route a guard on this mesh can take, or null when none can. The textures bound at its
            // own draw answer first — cheapest, and they need no section of their own — and the wardrobe
            // route stands behind them. Answered per MESH; the finalization refuses a key whose requests
            // come back on both routes, since one variable can hold one kind of verdict.
            TwinRoute? TwinRouteFor(SubjectModel model, SubjectPart part, IReadOnlyList<string> mates)
            {
                if (TwinSeparable(model, part, mates))
                    return new TwinRoute(0, Array.Empty<(string, int)>());
                if (WardrobeWitnesses(model, part, mates) is not { } wardrobe) return null;
                return new TwinRoute(wardrobe.OwnVariant, wardrobe.Witnesses);
            }

            // Whether a guard on this mesh is possible, recording the request when it is. False leaves
            // the site its original refusal or degrade.
            bool RequestTwinGuard(SubjectModel model, SubjectPart part, string key,
                IReadOnlyList<string> mates, bool hide = false)
            {
                if (TwinRouteFor(model, part, mates) is not { } route) return false;
                RecordTwinGuard(model, part, key, mates, route, hide);
                return true;
            }

            // The wardrobe route: the siblings are different options of ONE outfit slot, worn one at a
            // time and stable during play, and each option marries meshes whose own signatures are
            // unique. Sighting one of those proves which option is on the doll, so the guarded section
            // needs no texture of its own. Answers this mesh's variant id and the section key that
            // witnesses each variant of the sibling set; null where the shape does not hold, which leaves
            // the site its own refusal or degrade.
            (long OwnVariant, IReadOnlyList<(string Key, int Verdict)> Witnesses)? WardrobeWitnesses(
                SubjectModel model, SubjectPart part, IReadOnlyList<string> mates)
            {
                if (mates.Count == 0) return null;
                long own = VariantOf(model, part);
                if (own <= 0) return null;
                // the whole signature group is off limits as a witness: its meshes are exactly the ones
                // whose draws cannot be told apart
                var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { part.Token };
                var variants = new List<long> { own };
                foreach (var mate in mates)
                {
                    if (PartOf(model, mate) is not { } matePart) return null;
                    long theirs = VariantOf(model, matePart);
                    if (theirs <= 0 || theirs == own || theirs / 100 != own / 100) return null;
                    group.Add(mate);
                    if (!variants.Contains(theirs)) variants.Add(theirs);
                }
                // each option's candidates first, then the ones no OTHER option also draws
                var perVariant = new List<(long Variant, List<string> Keys)>();
                foreach (long variant in variants)
                {
                    var keys = new List<string>();
                    foreach (var p in model.Parts)
                    {
                        if (group.Contains(p.Token) || VariantOf(model, p) != variant) continue;
                        // A part the game's own dorm or lobby logic can withhold answers for that logic
                        // rather than for the option worn, so sighting it settles nothing. This reads the
                        // PREFAB-RESIDENT marker only — a SubjectPart carries no timeline verdict, since
                        // the timelines are a build-time input the workbench model never sees. Timeline
                        // overrides are deliberately out of scope for witness duty: they demote a part
                        // from the POOL (through VisibilityOf on the roster probe) and stop there, and
                        // this prefab-resident gate is what the witness route rests on.
                        if (p.Visibility != Model.VisibilityOverride.None) continue;
                        List<(string Name, string BundleId, long PathId)> tiers;
                        // a part this build can't resolve — including one it refuses to touch — is no
                        // witness, exactly as the signature walk leaves it out of the index
                        try { tiers = Tiers(p); }
                        catch { continue; }
                        foreach (var (name, bid, pid) in tiers)
                        {
                            // A tier outside the shadow pass draws only while it is in frame, so sighting
                            // nothing at it proves nothing about which option is worn. Nor does a tier the
                            // dorm or lobby can withhold on its own.
                            if (!TierCastsShadows(p, name)) continue;
                            if (TierVisibility(p, name) != Model.VisibilityOverride.None) continue;
                            var sig = SigOf(model, bid, name, pid);
                            // an ambiguous witness would answer for its own siblings too, which is the
                            // question this route is trying to settle
                            if (sig.Unique && !keys.Contains(sig.Key, StringComparer.Ordinal))
                                keys.Add(sig.Key);
                        }
                    }
                    perVariant.Add((variant, keys));
                }
                // A mesh worn with SEVERAL options resolves to one signature key, and its section would
                // write a different verdict for each option that lists it — one line per option in one
                // section, the last of them standing. Drawing proves nothing about which option is on,
                // so it is struck from every list rather than trusted in one of them; an option the
                // strike leaves unsightable fails the whole route.
                var sighted = new List<(string Key, int Verdict)>();
                foreach (var (variant, keys) in perVariant)
                    foreach (var key in keys) sighted.Add((key, (int)(variant % 100)));
                if (StrikeContradictedWitnesses(sighted, variants.Select(v => (int)(v % 100)))
                        is not { } witnesses)
                    return null;
                return (own, witnesses);
            }

            // Whether the named tier of this part takes part in the shadow pass, joined by slot name:
            // the representative tier answers with the part's own flag, every other with its sibling
            // slot's. A name that joins to neither reads as casting, since the exclusion this feeds
            // rides a measured Off and never an unresolved one.
            bool TierCastsShadows(SubjectPart part, string tierName)
            {
                if (string.Equals(tierName, part.SlotName, StringComparison.Ordinal))
                    return part.CastsShadows;
                foreach (var t in part.SiblingTiers ?? Array.Empty<Export.RecipeTierSlot>())
                    if (string.Equals(t.SlotName, tierName, StringComparison.Ordinal))
                        return t.CastsShadows;
                return true;
            }

            // The visibility override on the named tier of this part, joined by slot name the same way. A
            // name that joins to neither tier reads as unwithheld, since the exclusion this feeds rides a
            // list that named the node and never an unresolved name.
            Model.VisibilityOverride TierVisibility(SubjectPart part, string tierName)
            {
                if (string.Equals(tierName, part.SlotName, StringComparison.Ordinal))
                    return part.Visibility;
                foreach (var t in part.SiblingTiers ?? Array.Empty<Export.RecipeTierSlot>())
                    if (string.Equals(t.SlotName, tierName, StringComparison.Ordinal))
                        return t.Visibility;
                return Model.VisibilityOverride.None;
            }

            // The wardrobe variant a part belongs to, classified the way the roster probe classifies it:
            // 0 = not wardrobe-gated, -1 = a variant-shaped token the scheme doesn't list.
            long VariantOf(SubjectModel model, SubjectPart part) =>
                PartPresence.Classify(part.Token, SchemeOf(model)).VariantId;

            // Acting on a mesh whose signature stays ambiguous would hit the other mesh's draws too,
            // unless the textures bound at the two draws tell them apart.
            void RefuseTwinTarget(SubjectModel model, SubjectPart part, string editMesh)
            {
                foreach (var (name, bid, pid) in Tiers(part))
                {
                    var sig = SigOf(model, bid, name, pid);
                    if (sig.Unique) continue;
                    if (RequestTwinGuard(model, part, sig.Key, sig.Mates)) continue;
                    throw new AuthoredRefusalException(
                        $"'{editMesh}' and '{sig.Mate}' can't be told apart in game, so changing one "
                        + "would change the other. This mesh edit can't be built");
                }
            }

            // Whether the base colors bound at a shared draw tell this part apart from every mate: it binds
            // one, each mate names a roster part binding one, and no mate binds the same image.
            bool TwinSeparable(SubjectModel model, SubjectPart part, IReadOnlyList<string> mates)
            {
                // no mate but this part's own: the draws that share the key are its own tiers, which bind
                // one material and therefore one base color. Nothing at draw time can part them.
                if (mates.Count == 0) return false;
                if (AlbedoHash(part) is not { } own) return false;
                foreach (var mate in mates)
                {
                    // an unnamed mate, or one whose albedo won't read, leaves nothing to compare against
                    if (PartOf(model, mate) is not { } matePart
                        || AlbedoHash(matePart) is not { } theirs) return false;
                    if (string.Equals(own, theirs, StringComparison.OrdinalIgnoreCase)) return false;
                }
                return true;
            }

            // The roster part a signature mate's token names, or null when nothing on the roster answers.
            SubjectPart? PartOf(SubjectModel model, string token) => model.Parts.FirstOrDefault(p =>
                string.Equals(p.Token, token, StringComparison.OrdinalIgnoreCase));

            // Whether any of this part's stock maps is the given texture. An unreadable map proves
            // nothing and says nothing, the same stance the witness walk takes on unreadable parts.
            bool BindsStock(SubjectPart p, string hash)
            {
                foreach (var material in p.Materials)
                    foreach (var m in material.Maps)
                    {
                        string h;
                        try { h = StockTexture(m, p.SlotName).Hash; }
                        catch { continue; }
                        if (string.Equals(h, hash, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                return false;
            }

            // A part's own base-color stock texture hash, or null when it binds none or it won't read.
            string? AlbedoHash(SubjectPart p)
            {
                foreach (var material in p.Materials)
                    foreach (var m in material.Maps)
                        if (Materials.MaterialResolver.IsBaseColor(m.Slot))
                        {
                            try { return StockTexture(m, p.SlotName).Hash; }
                            catch (Exception ex) when (ex is not BlockedAssetException) { return null; }
                        }
                return null;
            }

            // Whether two meshes serialize the same geometry: index bytes and every vertex stream equal.
            static bool MeshBytesEqual(Mesh.MeshRaw a, Mesh.MeshRaw b)
            {
                if (a.VertexCount != b.VertexCount || a.StreamIds.Count != b.StreamIds.Count
                    || !a.Index.AsSpan().SequenceEqual(b.Index)) return false;
                for (int s = 0; s < a.StreamIds.Count; s++)
                    if (a.Stride(s) != b.Stride(s) || !a.StreamBytes(s).AsSpan().SequenceEqual(b.StreamBytes(s)))
                        return false;
                return true;
            }

            // ---- edit scope: the sharing measurement decides each edit's reach -----------------------
            // A measured shared TEXTURE goes draw-scoped instead of hash-global; a measured shared MESH
            // anchor gets the outfit's presence latch. No measurement (or an unmeasured subject) means
            // unscoped edits, said once rather than silently.
            var sharing = env.Sharing;
            if (sharing is null)
                infos.Add("Shared meshes and textures haven't been measured. Every edit applies "
                    + "wherever its mesh or texture draws in game.");
            // An outfit that failed to measure is uncovered, so a texture it shares reads private here and
            // rebinds game-wide: the reported reach is a floor rather than the truth. A build-log fact,
            // never a user-facing line — it names no action and reads as breakage on builds it cannot
            // even concern.
            else if (sharing.FailedOutfits.Count > 0)
                diagnostics.Add("sharing reach is a floor: unmeasured outfit(s) "
                    + string.Join(", ", sharing.FailedOutfits));
            var measuredNoted = new HashSet<(string, string)>();
            bool Measured(SubjectModel m)
            {
                if (sharing is null) return false;
                if (sharing.Covers(m.Character, m.Stem)) return true;
                if (measuredNoted.Add((m.Character, m.Stem)))
                    infos.Add($"Shared meshes and textures haven't been measured for {m.Stem}. Its "
                        + "edits apply wherever those meshes and textures draw in game.");
                return false;
            }
            IReadOnlyList<Workbench.SharingIndex.Wearer> MeshOthers(string ib, SubjectModel m) =>
                Measured(m) ? sharing!.MeshOtherWearers(ib, m.Character, m.Stem)
                            : Array.Empty<Workbench.SharingIndex.Wearer>();
            static string WearerLabels(IReadOnlyList<Workbench.SharingIndex.Wearer> wearers)
            {
                var labels = wearers.Select(w => w.CharacterLabel).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return labels.Count <= 3 ? string.Join(", ", labels)
                    : $"{string.Join(", ", labels.Take(3))} and {labels.Count - 3} more";
            }

            // one presence latch per authored outfit that needs one; null = it has no private witness
            // mesh, so its shared-anchor edits ship ungated (disclosed once)
            var latchNames = new Dictionary<(string, string), string?>();
            var latchList = new List<WitnessLatch>();
            string? LatchFor(SubjectModel m)
            {
                var key = (m.Character.ToLowerInvariant(), m.Stem.ToLowerInvariant());
                if (latchNames.TryGetValue(key, out var have)) return have;
                var witnesses = sharing!.WitnessIbs(m.Character, m.Stem);
                if (witnesses.Count == 0)
                {
                    infos.Add($"{m.Stem} has no mesh of its own, so the mod cannot tell when it is on "
                        + "screen. Edits on its shared meshes apply wherever those meshes draw.");
                    return latchNames[key] = null;
                }
                string name = PartName(m.Stem, "").TrimEnd('_');
                while (latchList.Any(l => l.Name == name)) name += "_";
                latchList.Add(new WitnessLatch(name, witnesses));
                return latchNames[key] = name;
            }

            // ---- the Replaces: one emitter pipeline each — pool, dumps, donor compile, Leaves, textures
            var pipelines = new List<ReplacePipeline>();
            // every pipeline's pool-part captures, part name → hash, for the sidecar's override list alone.
            // A pipeline RECORD carries its own dictionary of just its own parts (see the pool loop below):
            // the record states what that pipeline captures, and a shared one states everyone's.
            var sidecarCaptures = new Dictionary<string, string>(StringComparer.Ordinal);
            var allCaptureHashes = new HashSet<string>(StringComparer.Ordinal);   // every pipeline's lod0 + tier hashes
            // ib hash → the mesh holding its capture section, ACROSS pipelines: one capture section serves
            // one hash, and the emitter merges by hash. The SAME mesh reached by another Replace rides the
            // section already claimed for it — the case every Replace past the first on one outfit hits,
            // since their pools share the outfit's parts. Two DIFFERENT meshes on one hash refuse: they
            // would share a posed capture and pose each other's bones from whichever draw fired last.
            var poolHashOwner = new Dictionary<string, HashClaim>(StringComparer.Ordinal);
            // The slot names hidden in EVERY session state — the shape a plain Hide has always been. A
            // pooling pipeline suppresses only what it itself replaces, its own target's vanilla draw under
            // its own gate, so another pipeline's replaced part keeps running vanilla when this pipeline's
            // key is off. An unconditionally hidden part is different: the hide section loop below leaves
            // pooled slots to the capture sections, so every pooling pipeline suppresses it, which is what
            // makes the hide hold whichever pipelines are on.
            var hiddenMeshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (e, _, _) in work)
                if (e.Verb is EditVerbs.Hide && e.Gate.HiddenWhen.Count == 0) hiddenMeshes.Add(e.Mesh);

            // What a part is hidden BY, per key position. A hide a key group narrows to some of its
            // positions is not the same statement as one that holds in every state: the part still draws
            // in the positions nobody hid it in, so its suppression is a guarded skip per hiding position
            // rather than the pipeline's own gate. The same account a part whose own change is a TEXTURE
            // edit owes — a retexture repaints the vanilla draw and suppresses nothing — so both verbs
            // accumulate here.
            //
            // Where the part is ALSO pooled by some Replace, the hide section loop below leaves it to the
            // capture sections and the skip has no section of its own to go in. These terms ride into
            // every pipeline that pools the part instead, as that part's own entry in SuppressWhen, which
            // is the per-pool-part account already emitted beside the pipeline's own gate. Keyed by emitted
            // PART NAME, the key space SuppressWhen and the emitter's pool walk both use. Unpooled parts
            // take the hide-section route unchanged.
            var pooledPartHides = new Dictionary<string, List<KeyRef>>(StringComparer.Ordinal);
            foreach (var (e, m, p) in work)
            {
                if (e.Verb is not (EditVerbs.Retexture or EditVerbs.Hide)
                    || SuppressTerms(e) is not { Count: > 0 } terms) continue;
                string key = PartName(m.Character, p.Token);
                if (!pooledPartHides.TryGetValue(key, out var acc))
                    pooledPartHides[key] = acc = new List<KeyRef>();
                // A part answered at several positions is several work items, each carrying the same
                // hiding positions; one skip per position is what the emission owes, so they union.
                foreach (var t in terms) if (!acc.Contains(t)) acc.Add(t);
            }
            // the emitted part names whose suppression reached at least one pipeline, by either route: the
            // per-position terms above, or the unconditional hide a pooling pipeline carries on its own
            // gate. It is what the hide walk asks, and its key space is the EMITTED part name, which
            // PartName spells from character and token — so it is CHARACTER-scoped, not subject-scoped:
            // two outfits of one character with a same-token part share one entry. What keeps that from
            // routing one outfit's hide off another outfit's mesh is ClaimDump's own refusal
            // (DumpNameConflict): one emitted name standing for two different meshes can't ship in one
            // mod at all. A part no pipeline took is absent here and falls through to the walk, which
            // anchors the hide or names the part, rather than losing it in silence.
            var routedPooledHides = new HashSet<string>(StringComparer.Ordinal);
            // The capture hashes whose sections carry a routed hide's own guarded skips. A part only
            // PARTLY carried (a coverage-group member that lost a tier) reaches the hide walk, and the
            // walk needs to tell the draws its own pipeline already answers for from the ones some other
            // Replace captured — the first are answered, the second have no place to carry a per-position
            // hide and are refused by name.
            var routedPooledHideHashes = new HashSet<string>(StringComparer.Ordinal);

            // a part pooled by several Replaces dumps once; see DumpIdentity for what "once" means
            var dumpedParts = new Dictionary<string, DumpIdentity>(StringComparer.Ordinal);
            string ClaimDump(string dumpName, string meshName, string bid, long pid, string ibHash)
            {
                string dumpDir = Path.Combine(workDir, "dumps", dumpName);
                var incoming = new DumpIdentity(meshName, ibHash);
                if (dumpedParts.TryGetValue(dumpName, out var prev))
                {
                    if (DumpNameConflict(dumpName, prev, incoming) is { } conflict)
                        throw new AuthoredRefusalException(conflict);
                    return dumpDir;
                }
                StreamDump.Dump(Bundle(bid, $"pool part '{dumpName}'"), meshName, dumpDir, pid, reader);
                dumpedParts[dumpName] = incoming;
                return dumpDir;
            }

            // (replaced mesh, tagged stock hash) where some donor submesh INHERITS that kind — checked
            // against the scoped retextures once those are assembled, for the disclosure below
            var inheritingStockTags = new List<(string Mesh, string Hash)>();

            // pipeline order = roster order of the replaced part (the stable-order authority), so a
            // rebuild from the same edit list emits identically regardless of edit-list order
            var replaceWork = work.Where(w => w.Edit.Verb == EditVerbs.Replace)
                .OrderBy(w => w.Model.Character, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.Model.Stem, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.Model.Parts.ToList().IndexOf(w.Part))
                .ToList();

            // Before any dumping or compiling: two Replaces on one vanilla draw can't both win, and the
            // refusal names the subjects the author picked rather than the dump name they collided on.
            // Compared on the signature KEY, so two subject-mates a key already separates both build.
            if (ReplacedMeshConflict(replaceWork.Select(w =>
                {
                    var (name, bid, pid) = Tiers(w.Part)[0];
                    return ($"{w.Model.Character} · {w.Model.Stem} · {w.Part.Token}",
                        SigOf(w.Model, bid, name, pid).Key, PlanGate(w.Edit));
                })) is { } clash)
                throw new AuthoredRefusalException(clash);

            // Each Replace takes the route its OWN target mesh admits. The MESH decides, not the renderer
            // class that drew it, so a skinned prop answers like any other skinned part and only a mesh with
            // no influences at all takes the rigid route — and a mesh neither route reaches is refused here,
            // before any donor is imported or any part dumped.
            var rigidWork = new List<(BuildWorkItem Edit, SubjectModel Model, SubjectPart Part)>();
            var pooledWork = new List<(BuildWorkItem Edit, SubjectModel Model, SubjectPart Part)>();
            foreach (var w in replaceWork)
            {
                var (name, bid, pid) = Tiers(w.Part)[0];
                var field = reader.GetMeshField(Bundle(bid, $"part '{w.Part.Token}'"), name, pid)
                    ?? throw new AuthoredRefusalException(
                        $"the game files no longer hold the mesh '{name}'. Rescan, then build again");
                // Both rig rules read the bone table, and both sit ahead of the routing rule: what drives
                // a mesh's bones decides whether it can be replaced at all, before the stream layout does.
                var boneNameHashes = field["m_BoneNameHashes"]["Array"].Children.Select(c => c.AsUInt).ToList();
                // a spring-chain mesh's skin is usually recoverable, and the refusal is about the
                // simulation driving its bones, not the stream
                if (Skeleton.BoneTable.HasSpringChain(boneNameHashes))
                    throw new AuthoredRefusalException($"'{w.Edit.Mesh}' can't be replaced: "
                        + "it moves on the game's own spring bones. Remove this mesh edit");
                if (Skeleton.BoneTable.HasUnsupportedRig(boneNameHashes))
                    throw new BlockedAssetException($"'{w.Part.SlotName}' is not a supported asset");
                switch (StreamDump.Route(field))
                {
                    case StreamDump.ReplaceRoute.Pooled: pooledWork.Add(w); break;
                    case StreamDump.ReplaceRoute.Rigid: rigidWork.Add(w); break;
                    default:
                        // a rigid layout took the branch above, so what reaches this carries influences
                        // spelled in a shape recovery can't read (or blend shapes)
                        throw new AuthoredRefusalException($"'{w.Edit.Mesh}' can't be replaced: "
                            + $"{StreamDump.UnrecoverableSkinReason(field)}. Remove this mesh edit");
                }
            }
            replaceWork = pooledWork;

            // hashing a stock texture reads and CRCs its whole mip 0, and one map is commonly bound
            // by several submeshes and subjects — do it once per texture. Shared by the Replace
            // slot tags below and the retexture overrides.
            var stockTextures = new Dictionary<string, StockTex>(StringComparer.Ordinal);
            // source content|srgb → the one encoded DDS, shared across submeshes and pipelines
            var donorTexEncoded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // resolved authored path → source content, shared by donor maps and retextures for this build
            var authoredSourceIdentities = preparedSourceIdentities is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(preparedSourceIdentities, StringComparer.OrdinalIgnoreCase);
            // ramp content → the one shipped DDS. The ramp copies rather than encodes, so it keeps its own
            // claim book; the rule is the encode's — identity is CONTENT, so however many submeshes name one
            // ramp, one file ships, one resource declares it and one bind block binds it
            var donorRampShipped = new Dictionary<string, string>(StringComparer.Ordinal);
            // noted on the first real ENCODE, so a build that only passes .dds sources through (or touches
            // no texture at all) says nothing about an encoder it never reached
            bool encoderNoted = false;
            void NoteEncoder()
            {
                if (encoderNoted) return;
                encoderNoted = true;
                if (EncoderRungLine(Bc7Encoder.Resolved) is { } line) diagnostics.Add(line);
            }

            // ---- the donor rows' recorded sources -------------------------------------------------------
            // A donor row records which game map its submesh came back bound to, and the project's own
            // texture target names the asset behind that file. The build reads no shading off that chain any
            // more (RampConversion does, and writes its answer onto the row), but the chain still reaches
            // another subject's model — and every route to one answers to the content policy.
            //
            // Walked exactly as the derivation walks it, through the derivation's own join: the FIRST slot
            // that resolves a game texture, and only on a row whose ramp slot is still a question. A sweep
            // over all three slots of every row refuses builds nothing ever read a subject from — a clean
            // base colour beside a source the policy blocks, or a row whose ramp the modder settled by hand.
            void RefuseBlockedDonorSources()
            {
                foreach (var e in edits)
                    foreach (var t in e.Textures ?? Array.Empty<SubmeshTextures>())
                    {
                        if (RampConversion.RampSettled(t)) continue;
                        if (RampConversion.DonorSourceOf(workspace, t) is not { } donor) continue;
                        var src = donor.Part;
                        // the recorded strings first, so a name this install can't resolve is still
                        // refused; then the RESOLVED identity, the way the roster's own resolve does
                        RefuseBlocked(src.Subject, src.Outfit);
                        SubjectModel? model = null;
                        try { model = Subject(src.Subject, src.Outfit); }
                        catch (Exception ex) when (ex is not BlockedAssetException) { }
                        if (model is not null) RefuseBlocked(model.Character, model.Stem);
                    }
            }
            RefuseBlockedDonorSources();

            // Donor textures, per donor submesh and per map slot. THE RULE, stated once for both replace
            // routes and driven by the slot's recorded origin rather than guessed from its siblings:
            //   Authored        → bind the encoded map at that submesh's draw
            //   ExplicitNeutral → bind the shipped flat map: the modder asked for the slot blanked
            //   VanillaOwn      → inherit, so the part's own real map keeps drawing
            //   None            → inherit, EXCEPT on a submesh that asked for something on another
            //                     slot, where normal/RMO take the flat map instead: that submesh draws
            //                     on donor UVs, and the anchor's real relief sampled through foreign
            //                     UVs reads as garbage. This is what protects a material-less or
            //                     hand-built donor. An unauthored albedo inherits either way — no flat
            //                     albedo exists to stand in for one.
            // A submesh with no row at all is untouched: every slot inherits.
            // The compiled donor UVs are Unity-convention like every other mesh the game samples,
            // so the maps flip on encode. Submeshes and replacements authored from the same image
            // share one shipped DDS (see EncodeOnce).
            //
            // <paramref name="anchor"/> is the part the donor maps bind AT: the pool's anchor on the pooled
            // route, and the replaced part itself on the rigid one, where the donor draws nowhere else.
            (Dictionary<int, SubmeshMaps> SubMaps, List<StockMapTag> StockMaps,
                List<StockPropertyTag> StockProperties) DonorMaps(
                BuildWorkItem edit, SubjectPart anchor, string sfx, int submeshCount)
            {
                var subMaps = new Dictionary<int, SubmeshMaps>();
                bool AnchorFamily(Func<string, bool> isSlot, string kind, bool byConvention) =>
                    AnchorSrgb(anchor.Materials, isSlot, kind, byConvention, sfx, edit.Mesh,
                        m => StockTexture(m, edit.Mesh).Srgb, warnings, diagnostics);

                // one family per kind per replacement, resolved on FIRST USE — reading the anchor's stock
                // maps of a kind nobody authored would cost bundle reads and report disagreements about a
                // slot the build never binds through
                bool? albedoSrgb = null, normalSrgb = null, rmoSrgb = null, blendSrgb = null;
                bool AlbedoSrgb() => albedoSrgb ??=
                    AnchorFamily(Materials.MaterialResolver.IsBaseColor, "base color", byConvention: true);
                bool NormalSrgb() => normalSrgb ??=
                    AnchorFamily(Materials.MaterialResolver.IsNormal, "normal", byConvention: false);
                bool RmoSrgb() => rmoSrgb ??=
                    AnchorFamily(Materials.MaterialResolver.IsRmo, "RMO", byConvention: false);
                bool BlendSrgb() => blendSrgb ??=
                    AnchorFamily(Materials.MaterialResolver.IsBlend, "effect map", byConvention: true);

                // sfx+submesh already separate the replacements and submeshes an image can reach, and a
                // replacement resolves one family per kind, so the two families never land on one name
                MapSlot Enc(string? rel, SlotOrigin ask, string kind, Func<bool> srgb, int submesh) => ask switch
                {
                    SlotOrigin.Authored when rel is not null =>
                        MapSlot.From(EncodeOnce(donorTexEncoded, project.Resolve(rel), srgb(),
                            () => Path.Combine(workDir, $"donor_{sfx}_s{submesh}_{kind}.dds"),
                            NoteEncoder, log, caches?.TextureDir, encoderCpuLimit,
                            authoredSourceIdentities)),
                    SlotOrigin.ExplicitNeutral => MapSlot.Neutral,
                    _ => MapSlot.Inherit,
                };
                // The ramp ships as the file names it, byte for byte. It is the game's own float format and
                // its values ARE the shading curve, so nothing re-encodes, resamples or gamma-corrects it;
                // the read is a validation, and a file that isn't a ramp is refused rather than shipped to
                // fail texture creation in the runtime. The extent is validated with the format, because a
                // well-formed fp16 image of another size creates fine and then draws the wrong curve.
                MapSlot ShipRamp(string? rel, SlotOrigin ask, string suffix, int submesh)
                {
                    if (ask != SlotOrigin.Authored || rel is null) return MapSlot.Inherit;
                    var src = project.Resolve(rel);
                    byte[] bytes;
                    try
                    {
                        bytes = File.ReadAllBytes(src);
                        var image = DdsReader.Parse(bytes, Path.GetFileName(src));
                        if (image.Width != RampWidth || image.Height != RampHeight)
                            throw new InvalidDataException($"{Path.GetFileName(src)} is "
                                + $"{image.Width}x{image.Height}, and a toon ramp is {RampWidth}x{RampHeight}");
                    }
                    catch (Exception e)
                    {
                        throw new AuthoredRefusalException(
                            $"the toon ramp on submesh {submesh} can't be built: {e.Message}");
                    }
                    string key = Convert.ToHexString(
                        System.Security.Cryptography.SHA256.HashData(bytes));
                    if (donorRampShipped.TryGetValue(key, out var have)) return MapSlot.From(have);
                    var dest = Path.Combine(workDir, $"donor_{suffix}_s{submesh}_ramp.dds");
                    File.Copy(src, dest, overwrite: true);
                    donorRampShipped[key] = dest;
                    return MapSlot.From(dest);
                }
                foreach (var t in edit.Textures ?? new List<SubmeshTextures>())
                {
                    if (t.Submesh < 0 || t.Submesh >= submeshCount)
                        throw new AuthoredRefusalException(
                            $"a texture set on '{edit.Mesh}' names submesh {t.Submesh}, but the new "
                            + $"mesh has {submeshCount}");
                    if (subMaps.ContainsKey(t.Submesh))
                        throw new AuthoredRefusalException(
                            $"the mesh edit on '{edit.Mesh}' has two texture sets for submesh {t.Submesh}");
                    var albedo = Enc(t.Albedo, t.AlbedoAsk, "a", AlbedoSrgb, t.Submesh);
                    var normal = Enc(t.Normal, t.NormalAsk, "n", NormalSrgb, t.Submesh);
                    var rmo = Enc(t.Rmo, t.RmoAsk, "r", RmoSrgb, t.Submesh);
                    var blend = Enc(t.Blend, t.BlendAsk, "b", BlendSrgb, t.Submesh);
                    // The ramp slot is settled by the ROW and nothing else: a file ships, and anything
                    // else inherits. A file that happens to hold the anchor's own bytes still ships,
                    // because naming it is the ask — the content gate belongs to the conversion that
                    // fills the slot (see RampConversion), not to the build that reads it.
                    var ramp = ShipRamp(t.Ramp, t.RampAsk, sfx, t.Submesh);
                    // The flat map goes wherever the shared rule says it does — the explicit blank, and the
                    // garbage relief a submesh drawing on donor UVs needs. Asked THROUGH that rule so the
                    // panes describing this emission read the same answer it binds. Albedo is not in it: no
                    // flat albedo exists, and an albedo asking for the neutral is refused by the emitter.
                    var flat = BlankedSlots.Of(t, EditVerbs.Replace);
                    if (flat.Normal) normal = MapSlot.Neutral;
                    if (flat.Rmo) rmo = MapSlot.Neutral;
                    // the ramp takes no flat substitution: there is no flat ramp, and a submesh drawing on
                    // donor UVs samples a ramp by lighting, not by UV, so the part's own stays right
                    var properties = new List<PropertyMapSlot>();
                    foreach (var property in (t.Textures ?? new List<PropertyTextureBinding>())
                                 .OrderBy(p => p.ShaderProperty, StringComparer.Ordinal))
                    {
                        if (property.Ask != SlotOrigin.Authored || property.File is null) continue;
                        if (anchor.Materials.Count == 0)
                            throw new AuthoredRefusalException(
                                $"{TextureMap.PropertyLabel(property.ShaderProperty)} on '{edit.Mesh}' cannot be applied. "
                                + "The installed mesh has no material for this picture. Refresh the part, or leave it out.");
                        var material = anchor.Materials[Math.Min(t.Submesh, anchor.Materials.Count - 1)];
                        var registers = slotPlan.ForProperty(property.ShaderProperty);
                        if (registers.Count == 0)
                            throw new AuthoredRefusalException(
                                $"{TextureMap.PropertyLabel(property.ShaderProperty)} on '{edit.Mesh}' cannot be applied. "
                                + $"No measured texture-register coverage exists for {property.ShaderProperty}. "
                                + "Update the app's game data, or leave this picture out.");
                        var stock = material.Maps.FirstOrDefault(m =>
                            string.Equals(m.Slot, property.ShaderProperty, StringComparison.Ordinal));
                        if (stock is null)
                            throw new AuthoredRefusalException(
                                $"{TextureMap.PropertyLabel(property.ShaderProperty)} on '{edit.Mesh}' cannot be applied. "
                                + $"The installed material no longer binds {property.ShaderProperty}. "
                                + "Refresh the part, or leave this picture out.");
                        if (OtherPropertyOnResource(material, stock) is { } other)
                            throw PropertyProbeCannotIsolate(stock.TextureName, edit.Mesh,
                                property.ShaderProperty, other.Slot);
                        string token = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            Encoding.UTF8.GetBytes(property.ShaderProperty)))[..8].ToLowerInvariant();
                        properties.Add(new PropertyMapSlot(property.ShaderProperty,
                            MapSlot.From(EncodeOnce(donorTexEncoded, project.Resolve(property.File),
                                StockTexture(stock, edit.Mesh).Srgb,
                                () => Path.Combine(workDir, $"donor_{sfx}_s{t.Submesh}_x{token}.dds"),
                                NoteEncoder, log, caches?.TextureDir, encoderCpuLimit,
                                authoredSourceIdentities)), registers));
                    }
                    subMaps[t.Submesh] = new SubmeshMaps(albedo, normal, rmo, ramp, blend,
                        properties.Count > 0 ? properties : null);
                }
                bool anyPictures = subMaps.Values.Any(m => !m.Albedo.IsInherit || !m.Normal.IsInherit
                    || !m.Rmo.IsInherit);
                if (anyPictures)
                    for (int i = 0; i < submeshCount; i++)
                    {
                        var m = subMaps.GetValueOrDefault(i);
                        var kept = new List<string>();
                        if (m is null || m.Albedo.IsInherit) kept.Add("base color");
                        if (m is null || m.Normal.IsInherit) kept.Add("normal");
                        if (m is null || m.Rmo.IsInherit) kept.Add("RMO");
                        if (kept.Count > 0)
                            diagnostics.Add($"donor ({sfx}) submesh {i}: no authored {string.Join("/", kept)}. "
                                + "It keeps the part's stock map there");
                    }

                // The anchor's stock map tags the draw's slot probe needs. Only a donor-textured replacement
                // needs them, and unhashable maps degrade rather than fail: the geometry still replaces.
                // A single map that won't hash warns per map, and a kind left with no tag at all warns
                // again — either way the donor maps don't bind in the draws that lost their tag.
                var stockMaps = new List<StockMapTag>();
                var stockProperties = new List<StockPropertyTag>();
                bool anyRamp = subMaps.Values.Any(m => !m.Ramp.IsInherit);
                bool anyBlend = subMaps.Values.Any(m => !m.Blend.IsInherit);
                var propertyNames = subMaps.Values.SelectMany(m => m.Properties ?? Array.Empty<PropertyMapSlot>())
                    .Select(p => p.ShaderProperty).Distinct(StringComparer.Ordinal)
                    .OrderBy(p => p, StringComparer.Ordinal).ToList();
                if (anyPictures || anyRamp || anyBlend)
                {
                    stockMaps = TagStockMaps(anchor.Materials, sfx, edit.Mesh,
                        m => StockTexture(m, edit.Mesh).Hash, warnings, diagnostics, anchor.Token,
                        pictures: anyPictures, ramp: anyRamp, blend: anyBlend);
                    WarnUnbindableDonorMaps(subMaps, stockMaps, edit.Mesh, warnings);
                    // Remembered for the retexture phase: a tagged stock map that also gets a scoped
                    // retexture leaves this replacement's INHERITING submeshes on the pre-retexture image
                    // (the draw restores the slot from its save), which deserves a disclosure.
                    foreach (var tag in stockMaps)
                    {
                        bool inherits = Enumerable.Range(0, submeshCount).Any(i =>
                            subMaps.GetValueOrDefault(i) is not { } m || tag.Kind switch
                            {
                                StockMapKind.Albedo => m.Albedo.IsInherit,
                                StockMapKind.Normal => m.Normal.IsInherit,
                                StockMapKind.Ramp => m.Ramp.IsInherit,
                                StockMapKind.Blend => m.Blend.IsInherit,
                                _ => m.Rmo.IsInherit,
                            });
                        if (inherits) inheritingStockTags.Add((edit.Mesh, tag.Hash));
                    }
                }
                foreach (string property in propertyNames)
                {
                    var registers = slotPlan.ForProperty(property);
                    foreach (var map in anchor.Materials.SelectMany(m => m.Maps)
                                 .Where(m => string.Equals(m.Slot, property, StringComparison.Ordinal)))
                        try
                        {
                            stockProperties.Add(new StockPropertyTag(StockTexture(map, edit.Mesh).Hash,
                                property, registers, anchor.Token));
                        }
                        catch (Exception ex) when (ex is not BlockedAssetException)
                        {
                            warnings.Add($"Couldn't match {TextureMap.PropertyLabel(property)} on "
                                + $"'{edit.Mesh}' to a texture slot, so that picture may not show in game.");
                            diagnostics.Add($"anchor property '{property}' ({sfx}) cannot be slot-tagged: {ex.Message}");
                        }
                }
                return (subMaps, stockMaps, stockProperties);
            }

            // The roster bone sets a pool derivation reads are a property of the SUBJECT: two Replaces on
            // one subject probe the same parts, and an unreadable part is one exclusion, not one per
            // Replace. Read through the probe, which is also what the ramp conversion asks.
            RosterProbeResult RosterProbe(SubjectModel model) => probe.Probe(model);

            // What a part's LOD tiers ask of the union palette, and what the part can answer for another's:
            // the lod0 draw's capture hash and the bones it poses, then each renderable tier's hash and the
            // bones IT poses. Weighted rather than tabled on both sides — a bone with no weight behind it
            // moves no vertex of that draw, so it neither asks for a pool slot nor qualifies to supply one.
            // A tier the probe can't read is left out: the tier walk below degrades an unreadable tier to
            // its vanilla draw, so it captures nothing and asks for nothing, and the emitter's own per-tier
            // check backs the whole rule either way.
            var partTiers = new Dictionary<string, PoolDerive.PartTiers>(StringComparer.OrdinalIgnoreCase);
            PoolDerive.PartTiers TierBonesOf(SubjectModel model, string slotName)
            {
                string key = $"{model.Character}|{model.Stem}|{slotName}";
                if (partTiers.TryGetValue(key, out var have)) return have;
                var tiers = Tiers(RosterProbe(model).BySlot[slotName]);
                var list = new List<PoolDerive.TierBones>();
                for (int ti = 1; ti < tiers.Count; ti++)
                {
                    var (name, bid, pid) = tiers[ti];
                    try
                    {
                        var field = reader.GetMeshField(Bundle(bid, $"tier '{name}'"), name, pid)
                            ?? throw new AuthoredRefusalException(
                        $"the game files no longer hold the mesh '{name}'. Rescan, then build again");
                        list.Add(new PoolDerive.TierBones(name, SigOf(model, bid, name, pid).Key,
                            StreamDump.WeightedBoneHashes(field)));
                    }
                    catch (Exception ex) when (ex is not BlockedAssetException)
                    {
                        diagnostics.Add($"tier '{name}' left out of pool coverage: {ex.Message}");
                    }
                }
                var (l0Name, l0Bid, l0Pid) = tiers[0];
                // The lod0's posed bones are the roster probe's own measurement of this same mesh, taken
                // once: a second read is a second chance for the two to disagree about which bones a part
                // can cover. Only a probed part reaches here — the line above already indexes the probe.
                var l0Weighted = RosterProbe(model).Bones
                    .First(b => string.Equals(b.Mesh, slotName, StringComparison.OrdinalIgnoreCase)).Posed;
                return partTiers[key] = new PoolDerive.PartTiers(
                    SigOf(model, l0Bid, l0Name, l0Pid).Key, l0Weighted, list);
            }

            foreach (var (edit, model, part) in replaceWork)
            {
                string sfx = PartName(model.Character, part.Token) + StateSuffix(edit);
                string donorAbs = project.Resolve(edit.DonorFile
                    ?? throw new AuthoredRefusalException(
                        $"the mesh edit on '{edit.Mesh}' has nothing sent back from Blender yet"));
                log?.Invoke($"Reading the mesh for {edit.Mesh}…");
                var payload = MeshGltf.ImportPayload(donorAbs, lenient: true);
                var recordedRest = Mesh.RestBake.FromList(edit.BakedRest, out bool restRefused);
                if (restRefused)
                    warnings.Add($"The rest pose recorded for '{edit.Mesh}' can't be applied, so the new "
                        + "mesh is built without it.");

                var (partBones, partBySlot, heldBack, partRests, partPaths) = RosterProbe(model);

                // The TARGET's own part being held back is answered here rather than left to fall out of pool
                // derivation, which would derive a pool the replaced part isn't in and anchor the pipeline
                // somewhere else. The skin rule cannot be the cause: a mesh no route admits is refused
                // before any donor is imported, so what reaches this is a roster probe that couldn't read
                // the part, and the message carries that read's own reason.
                if (heldBack.FirstOrDefault(m =>
                        string.Equals(m.Mesh, part.SlotName, StringComparison.OrdinalIgnoreCase)) is { } blocked)
                    throw new AuthoredRefusalException(
                        $"'{edit.Mesh}' can't be replaced: {blocked.Why}. Remove this mesh edit");

                RefuseTwinTarget(model, part, edit.Mesh);

                // The roster this Replace may pool over. A part is in it only when it is on screen
                // whenever the target is (a one-influence part only when it IS the target), and what it
                // is left out of is both halves at once — the derivation below and the tier coverage
                // after it read this one set.
                // …candidacy, the coverage group and the derivation, in that order, through the probe — the
                // one place those four calls are made, so the anchor a conversion reads is the anchor this
                // build binds the donor's maps at.
                // No authored anchor override exists: the pool's anchor is the derivation's own pick.
                var (candidates, leftOut, groups, derived) =
                    probe.Derive(model, part, payload, anchorOverride: null);
                if (leftOut.Count > 0)
                    diagnostics.Add($"pool ({sfx}): left out: "
                        + string.Join("; ", leftOut.Select(m => $"'{m.Mesh}' · {m.Why}")));

                // The pool the donor's weights ask for isn't always one the pool can POSE: a part's other
                // LOD tiers ride the union palette built from the pool's lod0 bone sets, and those tiers can
                // rig bones their own lod0 doesn't. The parts carrying them join here, ahead of every dump
                // and claim below, so they are set up exactly like the parts the donor pulled in — captured
                // for recovery, and left out of the suppression list further down.
                var pool = PoolDerive.CoverTierBones(derived, candidates, s => TierBonesOf(model, s),
                    MigotoEmitter.MaxPoolParts, replacedPart: part.SlotName, readableRoster: partBones,
                    bonePaths: partPaths);
                // Build-log only: the extension is recovery bookkeeping the modder cannot act on, and the
                // shipped mod changes nothing about these parts.
                foreach (var added in pool.Pool.Except(derived.Pool, StringComparer.OrdinalIgnoreCase))
                    diagnostics.Add($"'{partBySlot[added].SlotName}' is built alongside '{edit.Mesh}'. "
                        + "It is not changed");
                // Build-log only: which parts the pool spans and which of them anchors it is bookkeeping in
                // the build's own words, and the status line has the part's name for the modder instead.
                diagnostics.Add($"pool ({sfx}): {string.Join(", ", pool.Pool)} (anchor {pool.Anchor})");

                // The workspace glb sits in scene-rest space, and where the union is stated is decided by
                // the anchor's measured rest — the same verdict the pool compile and the emitter read. A
                // scene-space union takes the payload's floats as exported; an anchor-space union takes
                // the recorded uprighting back off HERE — the one Unity-space boundary — so the compiled
                // payload lands in bind space. Exact inverse: an unedited round trip recovers the
                // original floats.
                if (!SwapCompile.TrySceneDelta(partRests.GetValueOrDefault(pool.Anchor), out _)
                    && recordedRest is { } uprighting)
                    payload = new MeshApply.Payload
                    {
                        Mesh = Mesh.RestBake.Unapply(payload.Mesh, uprighting),
                        JointIndices = payload.JointIndices,
                        JointWeights = payload.JointWeights,
                        SkinJointHashes = payload.SkinJointHashes,
                    };

                // dumps + capture hashes, in pool (roster) order
                var poolParts = new List<PoolPart>();
                var poolMeshes = new List<SwapCompile.PoolMesh>();
                var noSkip = new List<string>();
                // hiding states a POOL MATE brings with it: a mate whose own change is a texture edit
                // has no draw of its own to gate the hide on (see pooledRetexHides)
                var poolHides = new Dictionary<string, IReadOnlyList<KeyRef>>(StringComparer.Ordinal);
                var poolTiers = new List<PoolTier>();
                var partDisplayNames = partBySlot.ToDictionary(pair => pair.Key,
                    pair => AuthoredBuildPlanner.PartName(new TargetPart
                    {
                        Subject = model.Character,
                        Outfit = model.Stem,
                        RendererSlot = pair.Key,
                    }), StringComparer.OrdinalIgnoreCase);
                var poolCaptures = new Dictionary<string, string>(StringComparer.Ordinal);   // THIS pipeline's pool parts, part name → hash
                var pipelineHashes = new HashSet<string>(StringComparer.Ordinal);   // THIS pipeline's captures (signature keys)
                var pipelineIbs = new HashSet<string>(StringComparer.Ordinal);      // the same meshes' ib hashes, for the sharing index
                DrawShapeSet? anchorShapes = null;
                foreach (var slotName in pool.Pool)
                {
                    var p = partBySlot[slotName];
                    string partName = PartName(model.Character, p.Token);
                    var (name, bid, pid) = Tiers(p)[0];
                    var sig = SigOf(model, bid, name, pid);
                    // the anchor hosts the donor draw, so its vanilla submesh shapes are what the
                    // emitter's per-submesh draw routing matches on
                    if (string.Equals(slotName, pool.Anchor, StringComparison.OrdinalIgnoreCase))
                        anchorShapes = ShapesOf(bid, name, pid);
                    // A recovery source must be capturable alone: on a shared signature the capture
                    // holds whichever mesh drew last, and the palette rows this part owns would be
                    // recovered against the wrong rest geometry. A guard on the part's own textures
                    // keeps the capture to its own draws where they separate the two.
                    if (!sig.Unique && !RequestTwinGuard(model, p, sig.Key, sig.Mates))
                        throw new AuthoredRefusalException(
                            $"'{p.Token}' and '{sig.Mate}' can't be told apart in game, and this mesh "
                            + $"edit needs '{p.Token}' on its own. It can't be built");
                    string h = sig.Key;
                    pipelineIbs.Add(sig.Ib);
                    // One capture section serves one signature key. This part reached by another Replace's
                    // pool is the same mesh, so it rides the section already claimed for it. Two DIFFERENT
                    // meshes on one key would point both parts' posed refs at whichever draw fired last —
                    // silent wrong geometry on animation. Refuse instead.
                    string claimant = $"{model.Character} · {model.Stem} · {p.Token}";
                    var claimed = new CaptureMesh(bid, name, pid);
                    // A DIFFERENT mesh on this key refuses — unless the plan proves the two claims never
                    // act in one session state, in which case only one section is ever live and the first
                    // claimant's is the one the other's gate closes over.
                    if (poolHashOwner.TryGetValue(h, out var owner) && owner.Mesh != claimed
                        && !owner.Gate.ProvablyExclusiveOf(PlanGate(edit)))
                        throw new AuthoredRefusalException(
                            $"'{owner.Claimant}' and '{claimant}' can't be told apart in game, so this "
                            + "mesh edit can't be built");
                    // the first claimant names the refusal
                    poolHashOwner.TryAdd(h, new HashClaim(claimed, claimant, PlanGate(edit)));
                    // dump identity is the MESH (name + ib), not the section key: one physical mesh
                    // reached via two outfits is one dump even when only one outfit gives it a twin
                    string dumpDir = ClaimDump(partName, name, bid, pid, sig.Ib);
                    poolParts.Add(new PoolPart(partName, dumpDir, OpKey(bid, name, pid),
                        partRests.GetValueOrDefault(slotName)));
                    poolMeshes.Add(new SwapCompile.PoolMesh(Bundle(bid, $"pool part '{p.Token}'"), name, pid,
                        partRests.GetValueOrDefault(slotName)));
                    poolCaptures[partName] = h;
                    sidecarCaptures[partName] = h;
                    pipelineHashes.Add(h);
                    allCaptureHashes.Add(h);
                    // this pipeline's OWN target and the hidden parts are the only draws it suppresses;
                    // every other pool part it merely captures for recovery
                    bool ownTarget = string.Equals(slotName, part.SlotName, StringComparison.OrdinalIgnoreCase);
                    // An unconditionally hidden mate rides this pipeline's own gate, which is the settled
                    // shape. A mate hidden only in SOME positions keeps drawing in the rest, so it stays in
                    // NoSkip and takes the per-position skips below instead.
                    bool alwaysHidden = hiddenMeshes.Contains(slotName);
                    if (!ownTarget && !alwaysHidden) noSkip.Add(partName);
                    // this pipeline suppresses its own target under its own gate, and an unconditionally
                    // hidden mate under the same one: either way that part's hide is carried
                    else routedPooledHides.Add(partName);
                    if (pooledPartHides.TryGetValue(partName, out var mateHides))
                    {
                        poolHides[partName] = mateHides;
                        routedPooledHides.Add(partName);
                    }
                }

                // Every pool part's other LOD tiers join the tier machinery. Suppressed parts' tiers are
                // REPLACED — LOD choice is not distance-only, so a hidden tier would blank the character in
                // every context that picks it. Leave parts' tiers are captured WITHOUT skip: in a frame that
                // renders only another tier the part's lod0 capture never fires, and an uncaptured recovery
                // input poses its owned bones with garbage. A tier that can't feed recovery, or shares a
                // content hash with an already-captured tier of this pipeline, is left running vanilla. A
                // tier whose hash ANOTHER pipeline already claimed refuses on the same terms as a pool
                // part: the emitter merges by hash, so one section would serve both pipelines' recoveries.
                // A tier left running vanilla still DRAWS, so its hash joins the part's presence latch as
                // an extra sighting — a part visibly on screen at that detail must never read as absent,
                // or the tie underlay would ride its bones rigidly over live articulation.
                var presenceExtras = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                void PresenceExtra(string forPart, string hash)
                {
                    if (!presenceExtras.TryGetValue(forPart, out var list))
                        presenceExtras[forPart] = list = new List<string>();
                    if (!list.Contains(hash, StringComparer.Ordinal)) list.Add(hash);
                }
                foreach (var slotName in pool.Pool)
                {
                    var p = partBySlot[slotName];
                    string partName = PartName(model.Character, p.Token);
                    var tiers = Tiers(p);
                    for (int ti = 1; ti < tiers.Count; ti++)
                    {
                        var (name, bid, pid) = tiers[ti];
                        var tierSig = SigOf(model, bid, name, pid);
                        string h = tierSig.Key;
                        pipelineIbs.Add(tierSig.Ib);
                        // a key already captured is the same mesh (or an ambiguous class still pooled
                        // on its shared ib) — its draws fire that section, so the sighting rides there
                        if (!pipelineHashes.Add(h)) { PresenceExtra(partName, h); continue; }
                        // an ambiguous tier nothing in this pipeline captured runs vanilla: a capture on
                        // the shared signature would hold whichever mesh drew last. Where the two parts'
                        // textures separate them, a guard keeps the tier's section on its own draws.
                        TwinRoute? tierRoute = null;
                        if (!tierSig.Unique)
                        {
                            tierRoute = TwinRouteFor(model, p, tierSig.Mates);
                            if (tierRoute is null)
                            {
                                pipelineHashes.Remove(h);
                                PresenceExtra(partName, h);
                                warnings.Add($"'{name}' can't be told apart from '{tierSig.Mate}' in "
                                    + "game, so it keeps its original mesh.");
                                continue;
                            }
                        }
                        // The tier suffix is the LOD label read with MeshName.Lod: a variant tier like
                        // …_lod1_Fight is the lod1 link of its chain, and the emitter pairs tiers across
                        // parts by this suffix.
                        string tierName = $"{partName}_{Remold.Core.Model.MeshName.Lod(name)}";
                        // Claimed only past the pipeline's own dedupe above, so a pipeline re-reaching a
                        // hash it already captured rides that capture instead of colliding with itself.
                        // Across pipelines the pool part's rule: the same tier mesh rides the section
                        // claimed for it, a DIFFERENT mesh on that hash refuses.
                        string tierClaimant = $"{model.Character} · {model.Stem} · {tierName}";
                        var tierClaimed = new CaptureMesh(bid, name, pid);
                        if (poolHashOwner.TryGetValue(h, out var tierOwner) && tierOwner.Mesh != tierClaimed
                            && !tierOwner.Gate.ProvablyExclusiveOf(PlanGate(edit)))
                            throw new AuthoredRefusalException(
                                $"'{tierOwner.Claimant}' and '{tierClaimant}' can't be told apart in "
                                + "game, so this mesh edit can't be built");
                        bool mintedClaim = poolHashOwner.TryAdd(h,
                            new HashClaim(tierClaimed, tierClaimant, PlanGate(edit)));
                        // the same rule ClaimDump claims on, ahead of the degrade-to-warning catch below:
                        // a tier name meaning two different meshes is a refusal, not a warning. Dump
                        // identity is the mesh (name + ib), never the section key.
                        string tierIb = tierSig.Ib;
                        if (dumpedParts.TryGetValue(tierName, out var prevTier)
                            && DumpNameConflict(tierName, prevTier, new DumpIdentity(name, tierIb)) is { } tierClash)
                            throw new AuthoredRefusalException(tierClash);
                        string dumpDir;
                        try { dumpDir = ClaimDump(tierName, name, bid, pid, tierIb); }
                        catch (Exception ex)
                        {
                            pipelineHashes.Remove(h);
                            // No capture on it, so the hash is free for another pipeline — but only a claim
                            // minted here may be withdrawn: a ridden claim's capture is another pipeline's,
                            // and that dump already succeeded under a name of its own.
                            if (mintedClaim) poolHashOwner.Remove(h);
                            PresenceExtra(partName, h);
                            warnings.Add($"Couldn't prepare '{name}': {ex.Message}. It keeps its "
                                + "original mesh.");
                            continue;
                        }
                        // anchor tiers host the donor draw at their own detail level, so they carry their
                        // mesh's own vanilla shapes for the same per-submesh routing as the anchor's lod0
                        var tierVerdicts = pool.TierBoneVerdicts.Where(v =>
                            string.Equals(v.TierPart, slotName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(v.Tier, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                        poolTiers.Add(new PoolTier(partName, tierName, Remold.Core.Model.MeshName.Lod(name), dumpDir, h,
                            OpKey: OpKey(bid, name, pid),
                            Shapes: string.Equals(slotName, pool.Anchor, StringComparison.OrdinalIgnoreCase)
                                ? ShapesOf(bid, name, pid) : null,
                            SourcePart: slotName, SourceMesh: name, BoneVerdicts: tierVerdicts,
                            PartDisplayNames: partDisplayNames));
                        allCaptureHashes.Add(h);
                        // recorded only now: a tier that degraded to a warning above leaves no guard and no
                        // tag section behind it
                        if (tierRoute is { } tierAccepted)
                            RecordTwinGuard(model, p, tierSig.Key, tierSig.Mates, tierAccepted);
                    }
                }

                // One wardrobe member as the emission needs it: every draw of it the swap can capture on its
                // own, claimed exactly the way a pool part's draws are — one dump per mesh, one section per
                // signature key, a hash another Replace already claimed for a DIFFERENT mesh refused. The
                // member's lod0 is load-bearing: the donor's weights ride bones only its variants pose, so a
                // lod0 the swap cannot capture separately is a refusal rather than a warning. Its other
                // tiers degrade — a tier left running vanilla costs the rows only at draws that pick it.
                PoolGroupMember GroupMemberOf(long variantId, PoolDerive.PartBones pb)
                {
                    var mp = partBySlot[pb.Mesh];
                    string memberName = PartName(model.Character, mp.Token);
                    var meshes = new List<PoolGroupMesh>();
                    var memberSeen = new HashSet<string>(StringComparer.Ordinal);
                    var memberTiers = Tiers(mp);
                    // A tier that degrades below leaves this member no capture section at that detail, so
                    // a hide of the member has nothing there to ride. The hide walk still owes that tier
                    // its own section; this records whether it does.
                    bool everyTierCaptured = true;
                    for (int ti = 0; ti < memberTiers.Count; ti++)
                    {
                        var (name, bid, pid) = memberTiers[ti];
                        bool lod0 = ti == 0;
                        var sig = SigOf(model, bid, name, pid);
                        if (!memberSeen.Add(sig.Key)) continue;
                        // Recorded only AFTER the tier's claims succeed, the pool tier loop's own rule: a
                        // tier that degrades below must leave no guard and no tag section behind it.
                        TwinRoute? memberTierRoute = null;
                        if (!sig.Unique)
                        {
                            if (lod0)
                            {
                                if (!RequestTwinGuard(model, mp, sig.Key, sig.Mates))
                                    throw new AuthoredRefusalException(
                                        $"'{mp.Token}' and '{sig.Mate}' can't be told apart in game, and "
                                        + $"this mesh edit needs '{mp.Token}' on its own. It can't be "
                                        + "built");
                            }
                            else if (TwinRouteFor(model, mp, sig.Mates) is { } route)
                                memberTierRoute = route;
                            else
                            {
                                warnings.Add($"'{name}' can't be told apart from '{sig.Mate}' in game, "
                                    + "so it keeps its original mesh.");
                                everyTierCaptured = false;
                                continue;
                            }
                        }
                        string meshName = lod0 ? memberName
                            : $"{memberName}_{Remold.Core.Model.MeshName.Lod(name)}";
                        string claimant = $"{model.Character} · {model.Stem} · {meshName}";
                        var claimed = new CaptureMesh(bid, name, pid);
                        if (poolHashOwner.TryGetValue(sig.Key, out var owner) && owner.Mesh != claimed
                            && !owner.Gate.ProvablyExclusiveOf(PlanGate(edit)))
                        {
                            // Two members of one group genuinely share tier signatures — a P-variant
                            // family's far LODs are often one mesh shape apiece — and one key holds ONE
                            // capture section. The first claimant (roster order) keeps it; a later
                            // member's TIER degrades exactly like any ambiguous tier, costing rows only
                            // at draws that pick it, while its lod0 stays load-bearing and refuses.
                            if (!lod0)
                            {
                                warnings.Add($"'{name}' can't be told apart from '{owner.Claimant}' in "
                                    + "game, so it keeps its original mesh.");
                                everyTierCaptured = false;
                                continue;
                            }
                            throw new AuthoredRefusalException(
                                $"'{owner.Claimant}' and '{claimant}' can't be told apart in game, so "
                                + "this mesh edit can't be built");
                        }
                        bool minted = poolHashOwner.TryAdd(sig.Key,
                            new HashClaim(claimed, claimant, PlanGate(edit)));
                        string dumpDir;
                        try { dumpDir = ClaimDump(meshName, name, bid, pid, sig.Ib); }
                        catch (Exception ex) when (!lod0)
                        {
                            if (minted) poolHashOwner.Remove(sig.Key);
                            warnings.Add($"Couldn't prepare '{name}': {ex.Message}. It keeps its "
                                + "original mesh.");
                            everyTierCaptured = false;
                            continue;
                        }
                        meshes.Add(new PoolGroupMesh(meshName,
                            lod0 ? "" : Remold.Core.Model.MeshName.Lod(name), dumpDir, sig.Key,
                            OpKey(bid, name, pid)));
                        allCaptureHashes.Add(sig.Key);
                        if (memberTierRoute is { } accepted)
                            RecordTwinGuard(model, mp, sig.Key, sig.Mates, accepted);
                    }
                    var memberHides = pooledPartHides.GetValueOrDefault(memberName);
                    if (hiddenMeshes.Contains(pb.Mesh) || memberHides is { Count: > 0 })
                    {
                        // A member's suppression rides its OWN capture sections, one per tier that landed
                        // a claim. So the hashes it reaches are exactly those, and a member that lost a
                        // tier above is only PARTLY carried: it stays out of the routed set and falls
                        // through to the hide walk, which skips the hashes some capture already claimed
                        // and emits a section for the tiers left drawing vanilla. Routing it whole would
                        // leave that tier on screen where a hide says it should be gone.
                        foreach (var mesh in meshes) routedPooledHideHashes.Add(mesh.CaptureHash);
                        if (everyTierCaptured) routedPooledHides.Add(memberName);
                    }
                    return new PoolGroupMember(variantId, pb.Presence.Context, mp.Token, pb.Mesh)
                    {
                        Meshes = meshes.Count > 0 ? meshes : null,
                        MeasuredRest = partRests.GetValueOrDefault(pb.Mesh),
                        // The hide pass leaves a hash a pipeline captures to the capture section's own
                        // skip, so a member the change list also hides carries that skip here — the same
                        // route a hidden POOL part takes, and told apart the same way: hidden in every
                        // state rides this pipeline's own gate, hidden in some positions gets one guarded
                        // skip per position and keeps drawing in the rest.
                        Hidden = hiddenMeshes.Contains(pb.Mesh),
                        HiddenWhen = memberHides,
                    };
                }

                // What the build actually leans on the coverage group for: the bones the posed gate admitted
                // on its certificate. A group whose bones the donor rides none of carries this pipeline
                // nothing, and the covered bones keep their ascending order.
                // Settled before the donor compile, which needs the bone order it states.
                var covering = groups
                    .Select(g => (Group: g, Bones: g.GroupBones
                        .Where(h => derived.GroupCovered.TryGetValue(h, out long owner) && owner == g.SlotId)
                        .ToList()))
                    .Where(x => x.Bones.Count > 0)
                    .ToList();
                // This trims the group to the bones the gate admitted and the members to the parts posing
                // them (CoveredMembers), then mints those — each a distinct mesh, so every certified bone
                // has exactly one gmap and one fused section per member. Never empty: every admitted bone
                // had a poser in every displayed cell, and those posers are members.
                var carriedGroups = covering
                    .Select(x => new PoolGroup(x.Group.SlotId, x.Bones,
                        CoveredMembers(x.Group, x.Bones)
                            .Select(p => GroupMemberOf(p.Presence.VariantId, p)).ToList()))
                    .ToList();
                // The order the emitter hands out palette slots in, and the order the donor's own indices
                // are compiled against — stated once, read by both.
                var groupBoneOrder = carriedGroups.SelectMany(g => g.GroupBones).ToList();

                log?.Invoke($"Fitting {edit.Mesh} to the shared skeleton…");
                string donorDir = Path.Combine(workDir, $"donor_{sfx}");
                // layout target = the ANCHOR: the compiled streams bind at the anchor's draw, whose input
                // layout expects that part's exact strides/formats — a hair-anchored pipeline conformed to
                // the body's narrower stream1 reads garbage UVs
                int anchorIdx = -1;
                for (int i = 0; i < pool.Pool.Count; i++)
                    if (string.Equals(pool.Pool[i], pool.Anchor, StringComparison.OrdinalIgnoreCase)) { anchorIdx = i; break; }
                if (anchorIdx < 0)
                    throw new InvalidOperationException($"anchor '{pool.Anchor}' is not in its own pool ({string.Join(", ", pool.Pool)})");
                var compiled = SwapCompile.CompilePool(poolMeshes, donorAbs, donorDir, anchorIdx, payload, reader,
                    groupBoneOrder.Count > 0 ? groupBoneOrder : null);
                warnings.AddRange(compiled.Warnings);
                diagnostics.AddRange(compiled.Diagnostics);

                var (subMaps, stockMaps, stockProperties) = DonorMaps(edit, partBySlot[pool.Anchor], sfx,
                    compiled.SubmeshCount);
                RecordDonorMaps(edit, subMaps);
                repairRoutes[edit] = (sfx, "pooled");
                repairGroupBones[edit] = groupBoneOrder;
                repairGeometry[edit] = RepairGeometry(compiled,
                    s => s switch
                    {
                        0 => $"combined_bind_{sfx}.buf",
                        1 => $"combined_vb1_{sfx}.buf",
                        2 => $"combined_skin_{sfx}.buf",
                        _ => null,
                    },
                    $"combined_ib_{sfx}.buf",
                    // by renderer slot name, the identity a re-resolve against another install joins on —
                    // not the collapsed emission name the shipped files carry
                    anchor: pool.Anchor, pool: pool.Pool.ToList(), sfx, diagnostics);

                // presence latch when other outfits also draw any of this pipeline's meshes: the
                // suppression and the donor draw then apply only while this outfit is on screen
                string? pipeLatch = null;
                var pipeOthers = pipelineIbs.SelectMany(h => MeshOthers(h, model))
                    .Distinct().ToList();
                if (pipeOthers.Count > 0 && (pipeLatch = LatchFor(model)) is not null)
                {
                    var cross = pipeOthers.Where(w =>
                        !w.Character.Equals(model.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (cross.Count > 0)
                        infos.Add($"'{edit.Mesh}' shares meshes with {WearerLabels(cross)}. The "
                            + $"replacement applies while {model.Stem} is on screen.");
                }

                pipelines.Add(new ReplacePipeline
                {
                    Suffix = sfx,
                    Parts = poolParts,
                    DonorDir = donorDir,
                    CaptureHashes = poolCaptures,
                    Anchor = PartName(model.Character, partBySlot[pool.Anchor].Token),
                    SubTextures = subMaps.Count > 0 ? subMaps : null,
                    NoSkipParts = noSkip.Count > 0 ? noSkip : null,
                    Tiers = poolTiers.Count > 0 ? poolTiers : null,
                    StockMaps = stockMaps.Count > 0 ? stockMaps : null,
                    StockProperties = stockProperties.Count > 0 ? stockProperties : null,
                    ToggleKey = ContentGateTerm(edit),
                    HideWhenOff = SuppressesInEveryState(edit),
                    HiddenBy = HiddenByFlag(edit),
                    ShownBy = ShownByFlag(edit),
                    // The pipeline's own gate suppresses the part it REPLACES, so the hiding states it
                    // owes are filed under that part alone and never under the pool it leans on. A pool
                    // MATE joins the same map only where its hide has nowhere else to go — a retextured
                    // part keeps drawing, so its hide is a guarded skip and this pipeline's capture
                    // section is the one section that draw has.
                    SuppressWhen = PoolSuppress(poolHides,
                        PartName(model.Character, part.Token), SuppressTerms(edit)),
                    Latch = pipeLatch,
                    Groups = carriedGroups.Count == 0 ? null : carriedGroups,
                    BonePaths = partPaths.Count > 0 ? partPaths : null,
                    PresenceHashes = presenceExtras.Count > 0
                        ? presenceExtras.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value)
                        : null,
                    AnchorShapes = anchorShapes,
                });
            }

            // ---- the rigid Replaces: compile onto the target's own layout and swap the buffers ---------
            // No pool, no dumps, no palette: the draw is not posed per vertex, so the compiled streams are
            // what the vanilla ones were and the section binds them under the same draw.
            var rigids = new List<RigidReplace>();
            foreach (var (edit, model, part) in rigidWork)
            {
                string sfx = PartName(model.Character, part.Token) + StateSuffix(edit);
                RefuseTwinTarget(model, part, edit.Mesh);
                string donorAbs = project.Resolve(edit.DonorFile
                    ?? throw new AuthoredRefusalException(
                        $"the mesh edit on '{edit.Mesh}' has nothing sent back from Blender yet"));
                log?.Invoke($"Reading the mesh for {edit.Mesh}…");
                var payload = MeshGltf.ImportPayload(donorAbs, lenient: true);
                var recordedRest = Mesh.RestBake.FromList(edit.BakedRest, out bool restRefused);
                if (restRefused)
                    warnings.Add($"The rest pose recorded for '{edit.Mesh}' can't be applied, so the new "
                        + "mesh is built without it.");
                // The workspace glb sits in scene-rest space and the target's own space is where the swap
                // lands, so the recorded uprighting comes back off here. There is no pool to state a shared
                // space for, which is what makes this unconditional rather than an anchor's verdict.
                if (recordedRest is { } uprighting)
                    payload = new MeshApply.Payload
                    {
                        Mesh = Mesh.RestBake.Unapply(payload.Mesh, uprighting),
                        JointIndices = payload.JointIndices,
                        JointWeights = payload.JointWeights,
                        SkinJointHashes = payload.SkinJointHashes,
                    };

                var (name, bid, pid) = Tiers(part)[0];
                log?.Invoke($"Fitting {edit.Mesh} to the game's mesh layout…");
                string rigidDir = Path.Combine(workDir, $"rigid_{sfx}");
                var compiled = SwapCompile.CompilePart(Bundle(bid, $"part '{part.Token}'"), name, donorAbs,
                    rigidDir, pid, payload, reader);
                warnings.AddRange(compiled.Warnings);
                diagnostics.AddRange(compiled.Diagnostics);

                // the donor binds at this part's own draws and nowhere else, so the part IS the anchor
                var (subMaps, stockMaps, stockProperties) = DonorMaps(edit, part, sfx,
                    compiled.SubmeshCount);
                RecordDonorMaps(edit, subMaps);
                repairRoutes[edit] = (sfx, "rigid");
                repairGeometry[edit] = RepairGeometry(compiled,
                    // the rigid emission copies out stream 0 and, when the compile produced one, stream 1
                    s => s switch
                    {
                        0 => $"rigid_vb0_{sfx}.buf",
                        1 => $"rigid_vb1_{sfx}.buf",
                        _ => null,
                    },
                    $"rigid_ib_{sfx}.buf", anchor: part.SlotName, pool: null, sfx, diagnostics);

                // Every renderable tier of the part: its own signature key first, then the siblings. All
                // of them are suppressed and redrawn, since a tier left alone would show the stock mesh
                // wherever the game picks it.
                var ownSig = SigOf(model, bid, name, pid);
                string ownHash = ownSig.Key;
                var tierHashes = new List<string>();
                var rigidIbs = new HashSet<string>(StringComparer.Ordinal) { ownSig.Ib };
                var claimed = new HashSet<string>(StringComparer.Ordinal) { ownHash };
                // each owned hash's vanilla shapes, for the emitter's per-submesh draw routing
                var rigidShapes = new Dictionary<string, DrawShapeSet>(StringComparer.Ordinal)
                {
                    [ownHash] = ShapesOf(bid, name, pid),
                };
                foreach (var (tName, tBid, tPid) in Tiers(part).Skip(1))
                {
                    var tierSig = SigOf(model, tBid, tName, tPid);
                    rigidIbs.Add(tierSig.Ib);
                    if (claimed.Add(tierSig.Key))
                    {
                        tierHashes.Add(tierSig.Key);   // a key already claimed is the same mesh
                        rigidShapes[tierSig.Key] = ShapesOf(tBid, tName, tPid);
                    }
                }
                foreach (var h in claimed) allCaptureHashes.Add(h);   // the hide pass leaves these sections alone

                // presence latch when other outfits also draw this part: the suppression and the donor draw
                // then apply only while this outfit is on screen. Walked in emission order, so the
                // disclosure a rebuild writes reads the same way twice. The sharing index reads ib hashes.
                string? rigidLatch = null;
                var rigidOthers = rigidIbs
                    .SelectMany(h => MeshOthers(h, model)).Distinct().ToList();
                if (rigidOthers.Count > 0 && (rigidLatch = LatchFor(model)) is not null)
                {
                    var cross = rigidOthers.Where(w =>
                        !w.Character.Equals(model.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (cross.Count > 0)
                        infos.Add($"'{edit.Mesh}' shares meshes with {WearerLabels(cross)}. The "
                            + $"replacement applies while {model.Stem} is on screen.");
                }

                diagnostics.Add($"'{edit.Mesh}' took the rigid replace route ({compiled.SubmeshCount} submesh(es))");

                rigids.Add(new RigidReplace
                {
                    Suffix = sfx,
                    DonorDir = rigidDir,
                    Hash = ownHash,
                    TierHashes = tierHashes.Count > 0 ? tierHashes : null,
                    SubTextures = subMaps.Count > 0 ? subMaps : null,
                    StockMaps = stockMaps.Count > 0 ? stockMaps : null,
                    StockProperties = stockProperties.Count > 0 ? stockProperties : null,
                    ToggleKey = ContentGateTerm(edit),
                    HideWhenOff = SuppressesInEveryState(edit),
                    HiddenBy = HiddenByFlag(edit),
                    ShownBy = ShownByFlag(edit),
                    SuppressWhen = SuppressTerms(edit),
                    Latch = rigidLatch,
                    ShapesByHash = rigidShapes,
                });
            }

            // ---- hides: every shipped tier of every suppressed NON-pool mesh. Suppressed pool parts'
            // tiers are captures (replaced draws), never hides. A pooled part hidden by an edit of its own
            // goes the same way, whether the donor's weights pooled it or tier coverage did: it is
            // suppressed inside every pipeline that pools it, under those pipelines' gates rather than its
            // own hide gate. Hide-when-off wins a shared pooled part — one draw can't be both suppressed
            // and captured, and the capture is what poses the swap. ----
            var hides = new List<string>();
            var hideSeen = new HashSet<string>(StringComparer.Ordinal);
            var unguardedHideWarnings = new HashSet<string>(StringComparer.Ordinal);
            // one hide hash can be reached by two edits (same mesh, two subjects): the FIRST edit to claim
            // the hash owns its toggle key, matching the hash-dedup right above it, and a second claimant
            // on a different key is named rather than dropped
            // The value is the OR-LIST of key positions demanding this draw suppressed — a two-state group
            // contributes one, a longer cycle one per hiding state — each emitted as its own guarded skip.
            var hideKeys = new Dictionary<string, IReadOnlyList<KeyRef>>(StringComparer.Ordinal);
            var hideLatches = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (e, model, part) in work)
            {
                // A part whose content is a TEXTURE edit owes the same suppression account. The retexture
                // rebinds a picture and suppresses nothing, so what takes the part off screen while a
                // hiding position stands is a guarded skip on the part's own draw — exactly what a part
                // with no content of its own emits. The retexture ships beside it unchanged: while the
                // draw is skipped there is nothing for it to repaint. Read off the PLAN only; on the
                // released route a change key says where content stands, never that anything is hidden.
                bool retexHide = e.Verb == EditVerbs.Retexture && SuppressTerms(e) is { Count: > 0 };
                if (e.Verb is not (EditVerbs.Replace or EditVerbs.Hide) && !retexHide) continue;
                // A part some pipeline already carries the suppression of, at every tier it renders, needs
                // no hide section of its own: its own replaced target, a mate hidden in every state under
                // that pipeline's gate, a wardrobe-group member whose every tier landed a capture, or a
                // per-position skip riding the capture section that draw already owns. Asked by EMITTED
                // PART NAME, which PartName spells from CHARACTER and token — the outfit is not in it, so
                // two outfits of one character with a same-token part ask by one name. The backstop is
                // ClaimDump's refusal (DumpNameConflict): one emitted name over two different meshes
                // cannot ship in one mod. Where no pipeline took it the walk below runs and either anchors
                // the hide or names the part, rather than letting it go quietly missing.
                if (routedPooledHides.Contains(PartName(model.Character, part.Token))) continue;
                bool anchored = false;
                foreach (var (name, bid, pid) in Tiers(part))
                {
                    var hideSig = SigOf(model, bid, name, pid);
                    string h = hideSig.Key;
                    if (allCaptureHashes.Contains(h))
                    {
                        // One hash owns one override section, and a pipeline already claimed this one for
                        // its capture. An UNCONDITIONAL hide rides that section's own skip, which is the
                        // settled hide-when-off rule and stays exactly as it was. A hide a key group
                        // narrows to some of its positions is a separate account the section has no place
                        // to carry, so it refuses by name rather than going quietly missing — unless the
                        // section is the part's OWN pooled capture, which already carries those positions
                        // as its member's guarded skips. That is the partly-carried member arriving here
                        // for the tiers it lost, and its claimed tiers are answered, not in conflict.
                        if ((e.Verb == EditVerbs.Hide || retexHide) && e.Gate.HiddenWhen.Count > 0
                            && !routedPooledHideHashes.Contains(h))
                            throw new AuthoredRefusalException(
                                $"'{e.Mesh}' is hidden by a key group and is also captured by a "
                                + "replacement on this outfit. One draw takes one override section, so "
                                + "the build can't emit both. Drop one of the two");
                        continue;
                    }
                    anchored = true;
                    // An ambiguous hide skips its sibling's draws too; a guard on the hidden part's own
                    // textures holds the skip to its own where they separate the two. Asked for AHEAD of
                    // the section claim below, so every hidden sibling's verdict reaches the guard: the
                    // one section carries them all, and a request dropped here would leave the second
                    // sibling on screen.
                    if (!hideSig.Unique && !RequestTwinGuard(model, part, h, hideSig.Mates, hide: true))
                    {
                        foreach (var mate in hideSig.Mates)
                        {
                            string mateMesh = PartOf(model, mate)?.SlotName ?? mate;
                            string warning = $"Hiding '{e.Mesh}' also hides '{mateMesh}' because their draws "
                                + "cannot be told apart.";
                            if (unguardedHideWarnings.Add(warning)) warnings.Add(warning);
                        }
                    }
                    if (!hideSeen.Add(h))
                    {
                        if (HideKeyCollisionWarning(name, hideKeys.GetValueOrDefault(h),
                            HideTerms(e)) is { } w)
                            warnings.Add(w);
                        continue;
                    }
                    hides.Add(h);
                    if (HideTerms(e) is { Count: > 0 } hk) hideKeys[h] = hk;
                    var others = MeshOthers(hideSig.Ib, model);
                    if (others.Count > 0 && LatchFor(model) is { } latch)
                    {
                        hideLatches[h] = latch;
                        // disclosure only where someone ELSE's model visibly co-changes; the same doll's
                        // other outfits are never on screen with this one outside a mirror
                        var cross = others.Where(w =>
                            !w.Character.Equals(model.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (cross.Count > 0)
                            infos.Add($"'{name}' is also drawn by {WearerLabels(cross)}. The hide "
                                + $"applies while {model.Stem} is on screen.");
                    }
                }
                // A hide needs a draw to gate. A part whose tiers this install never renders has none, so
                // the skip would have nowhere to land and the hide would go quietly missing — named here
                // rather than dropped. A Replace reaches its own capture section instead and a Hide with
                // no anchor is already refused where its slot fails to re-anchor.
                if (retexHide && !anchored)
                    throw new AuthoredRefusalException(
                        $"'{e.Mesh}' is hidden by a key group, and its only change is a texture edit. "
                        + "That leaves the part's own draw to gate, and this install renders no tier of "
                        + "it to put the gate on. Replace the part, or drop the hide");
                // The same account for a part hidden in SOME positions with no content of its own: no
                // pipeline pools it, so no capture section carries its guarded skips, and this install
                // renders no draw of its own to put them on. An unconditional hide of such a part is the
                // released shape and says nothing here.
                if (!anchored && e.Verb == EditVerbs.Hide && e.Gate.HiddenWhen.Count > 0)
                    throw new AuthoredRefusalException(
                        $"'{e.Mesh}' is hidden by a key group, and nothing in this build carries that "
                        + "hide: no replacement pools the part, and this install renders no tier of it to "
                        + "put the gate on. Replace the part, or drop the hide");
            }

            // ---- retextures: one override per STOCK TEXTURE, keyed on its own resource hash ----------
            // The identity is the texture, not the mesh or submesh: the override rebinds that resource
            // wherever it is sampled. Two submeshes, parts or subjects sharing one map land in a single
            // section — which carries one gated rebind per claim, so alternate states of one key group each
            // keep their own image. What the game-wide rebind cannot give is two images at once: claims
            // whose gates can be open together have one resource between them and are refused by name.
            var retexBuild = new List<RetexBuild>();
            var retexIdx = new Dictionary<string, int>(StringComparer.Ordinal);        // stock hash → retexBuild index
            // draw-scoped retextures (shared stock textures), by stock hash: one entry per stock texture
            // carrying one image per claiming outfit, whose anchors accumulate across the parts and
            // subjects that bind the texture
            var scopedBuild = new List<ScopedBuild>();
            var scopedIdx = new Dictionary<string, int>(StringComparer.Ordinal);        // hash + binding → scopedBuild index
            var crossAnchorNoted = new HashSet<string>(StringComparer.Ordinal);
            var rtxEncoded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // content|srgb → dst
            var rtxDstOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // dst → source

            // One claim's gated rebind under a stock texture's game-wide section. Identity is (image,
            // gate): two claims asking for one image in one position are one bind, while one image in two
            // positions is TWO — the section shows the game's own picture wherever no line of it runs, so
            // a position without its own bind is a state the edit silently misses.
            void AddWideImage(RetexBuild entry, string dds, BuildWorkItem claim, string claimant,
                string textureName, string? shaderProperty = null, string? displayLabel = null)
            {
                var key = ContentGateTerm(claim);
                string? shown = ShownByFlag(claim);
                if (entry.Images.Any(i => string.Equals(i.Dds, dds, StringComparison.OrdinalIgnoreCase)
                        && Nullable.Equals(i.Key, key)
                        && string.Equals(i.ShownBy, shown, StringComparison.Ordinal)))
                    return;
                var positions = GatePositions(claim);
                foreach (var img in entry.Images)
                    if (!string.Equals(img.Dds, dds, StringComparison.OrdinalIgnoreCase)
                        && !NeverTogether(img.Positions, positions))
                    {
                        if (shaderProperty is not null && img.ShaderProperty is not null
                            && !string.Equals(shaderProperty, img.ShaderProperty, StringComparison.Ordinal))
                            throw new AuthoredRefusalException(
                                $"{img.DisplayLabel} and {displayLabel} on '{claim.Mesh}' change the same original "
                                + $"texture '{textureName}' and cannot take two different pictures through this route. "
                                + "Give both slots the same picture, or leave one unchanged.");
                        throw ImageCollision(textureName, img.Claimant, claimant);
                    }
                entry.Images.Add(new RetexClaim
                {
                    Dds = dds, Key = key, ShownBy = shown, Positions = positions, Claimant = claimant,
                    ShaderProperty = shaderProperty, DisplayLabel = displayLabel,
                });
            }

            foreach (var (e, model, part) in work)
            {
                if (e.Verb != EditVerbs.Retexture) continue;
                if (e.Textures is not { Count: > 0 })
                    throw new AuthoredRefusalException(
                        $"the texture edit on '{e.Mesh}' has no pictures yet");
                string partName = PartName(model.Character, part.Token);
                if (part.Materials.Count == 0)
                    throw new AuthoredRefusalException($"the texture edit on '{e.Mesh}' has no material "
                        + "to work on, so there is no original texture to replace");
                foreach (var t in e.Textures)
                {
                    if (t.Submesh < 0)
                        throw new AuthoredRefusalException($"the texture edit on '{e.Mesh}' names "
                            + $"submesh {t.Submesh}, which does not exist");
                    // renderer m_Materials order IS the submesh binding; a shortfall repeats the last
                    // slot, the same rule the preview assigns maps by
                    var material = part.Materials[Math.Min(t.Submesh, part.Materials.Count - 1)];

                    string Enc(string src, string kind, bool srgb)
                    {
                        // one encode per (source CONTENT, sRGB family) — EncodeOnce's rule: two outfits
                        // given the same image collapse to one shipped file (and pass the same-image
                        // check below)
                        string resolved = project.Resolve(src);
                        string identity = SourceIdentity(authoredSourceIdentities, resolved);
                        string key = $"{identity}|{srgb}";
                        if (rtxEncoded.TryGetValue(key, out var have)) return have;
                        string stem = $"rtx_{Path.GetFileNameWithoutExtension(resolved)}_{kind}";
                        string dst = Path.Combine(workDir, stem + ".dds");
                        // the first family of a source keeps the plain name (the common case, and the one
                        // that has to stay stable across rebuilds); a second family is suffixed with it
                        if (rtxDstOwner.TryGetValue(dst, out var owner)
                            && string.Equals(owner, resolved, StringComparison.OrdinalIgnoreCase))
                            dst = Path.Combine(workDir, $"{stem}_{(srgb ? "srgb" : "lin")}.dds");
                        if (rtxDstOwner.TryGetValue(dst, out var other)
                            && !string.Equals(other, resolved, StringComparison.OrdinalIgnoreCase))
                            throw new AuthoredRefusalException(
                                $"two pictures in this mod share the file name '{other}'. Rename one, "
                                + "then build again");
                        rtxDstOwner[dst] = resolved;
                        // a passthrough never reaches the encoder, so it says nothing about one either
                        if (!AuthoredDds.IsPassthrough(resolved)) NoteEncoder();
                        AuthoredDds.Encode(resolved, dst, srgb, log, caches?.TextureDir,
                            encoderCpuLimit, identity);
                        rtxEncoded[key] = dst;
                        return dst;
                    }

                    void Map(string? authored, string kind, Func<string, bool> isSlot,
                        string? shaderProperty = null)
                    {
                        if (authored is null) return;
                        var stock = material.Maps.FirstOrDefault(m => shaderProperty is null
                            ? isSlot(m.Slot)
                            : string.Equals(m.Slot, shaderProperty, StringComparison.Ordinal));
                        if (stock is null)
                        {
                            // the kind letter is the shipped-file suffix; the warning speaks the card's word
                            string kindWord = shaderProperty is not null
                                ? TextureMap.PropertyLabel(shaderProperty)
                                : kind switch
                            {
                                "a" => "base color", "n" => "normal", "r" => "RMO", "b" => "effect",
                                _ => kind,
                            };
                            warnings.Add($"Material '{material.Name}' on submesh {t.Submesh} of "
                                + $"'{e.Mesh}' has no {kindWord} map, so the edited one has nothing to "
                                + "replace. It is skipped.");
                            return;
                        }
                        // the replacement inherits the stock texture's sRGB family: same resource, same slot
                        var info = StockTexture(stock, e.Mesh);
                        string hash = info.Hash;
                        string dds = Enc(authored, kind, info.Srgb);
                        // Recorded here rather than under either route below: both ship this image, and so
                        // does the collapse onto a section another change already claimed.
                        // the kinds a retexture ships, spelled out: an unnamed one landing on the RMO
                        // would record the wrong slot in a file a later read rebuilds a project from
                        DonorMapSlot? which = shaderProperty is not null ? null : kind switch
                        {
                            "a" => DonorMapSlot.BaseColor,
                            "n" => DonorMapSlot.Normal,
                            "r" => DonorMapSlot.Rmo,
                            "b" => DonorMapSlot.Blend,
                            _ => throw new InvalidOperationException($"unknown retexture map kind '{kind}'"),
                        };
                        var stockRef = new RepairData.StockTextureRef(
                            stock.BundleId, stock.TextureName, TextureUsers(stock));
                        if (which is { } fixedKind)
                        {
                            if (!repairMaps.TryGetValue(e, out var eMaps))
                                repairMaps[e] = eMaps = new Dictionary<(int, DonorMapSlot), string>();
                            eMaps[(t.Submesh, fixedKind)] = Path.GetFileName(dds);
                            if (!repairStock.TryGetValue(e, out var eStock))
                                repairStock[e] = eStock = new Dictionary<(int, DonorMapSlot), RepairData.StockTextureRef>();
                            eStock[(t.Submesh, fixedKind)] = stockRef;
                        }
                        else
                        {
                            if (!repairPropertyMaps.TryGetValue(e, out var eMaps))
                                repairPropertyMaps[e] = eMaps = new Dictionary<(int, string), string>();
                            eMaps[(t.Submesh, shaderProperty!)] = Path.GetFileName(dds);
                            if (!repairPropertyStock.TryGetValue(e, out var eStock))
                                repairPropertyStock[e] = eStock = new Dictionary<(int, string), RepairData.StockTextureRef>();
                            eStock[(t.Submesh, shaderProperty!)] = stockRef;
                        }
                        string claimant = $"'{e.Mesh}' on {model.Stem}";
                        // the sharing measurement decides the mechanism: a private texture rebinds
                        // hash-global (cheapest, covers every pass and LOD); a shared one rebinds at
                        // this subject's own mesh draws so no other wearer repaints
                        bool scopedRoute = Measured(model)
                            && sharing!.TexOtherWearers(hash, model.Character, model.Stem).Count > 0;
                        string bindingKey = $"{hash}\u001f{shaderProperty ?? kind}";
                        var registers = shaderProperty is null ? null : slotPlan.ForProperty(shaderProperty);
                        bool bindingAlreadyScoped = scopedIdx.ContainsKey(bindingKey);
                        if (scopedRoute && shaderProperty is not null
                            && OtherPropertyOnResource(material, stock) is { } other)
                            throw PropertyProbeCannotIsolate(stock.TextureName, e.Mesh,
                                shaderProperty, other.Slot);
                        if ((scopedRoute || bindingAlreadyScoped) && registers is { Count: 0 })
                            throw new AuthoredRefusalException(
                                $"{TextureMap.PropertyLabel(shaderProperty!)} on '{e.Mesh}' cannot be changed safely. "
                                + $"No measured texture-register coverage exists for {shaderProperty}. "
                                + "Update the app's game data, or leave this picture out.");

                        if (scopedIdx.TryGetValue(bindingKey, out int si))
                        {
                            // the scoped section carries one image per claiming outfit, so a second
                            // outfit's DIFFERENT image is a per-outfit disambiguation rather than a
                            // conflict. It has to be scoped too: the game-wide rebind this claim would
                            // otherwise take cannot live under the anchors' section.
                            var entry = scopedBuild[si];
                            if (!scopedRoute
                                && !entry.Images.Any(i => string.Equals(i.Dds, dds, StringComparison.OrdinalIgnoreCase)))
                                throw ImageCollision(stock.TextureName, entry.Claimant, claimant);
                            // the position that answers this claim, exactly as the fresh entry below reads
                            // it: the change-key binding says the same thing in the two-state vocabulary,
                            // and reading THAT instead would file a longer cycle's claim under position 0
                            // and file a plan's claim under no key at all
                            AddScopedAnchors(entry, dds, ContentGateTerm(e), ShownByFlag(e));
                            return;
                        }
                        if (retexIdx.TryGetValue(hash, out int ri))
                        {
                            // The stock texture's ONE section already stands, and this claim joins it with a
                            // gated rebind of its own. What the section cannot carry is two images open at
                            // once: the rebind is game-wide and whichever line ran last would hold the
                            // resource for the whole frame.
                            var wide = retexBuild[ri];
                            // A claim that MEASURED as sharing wants the draw-scoped mechanism, and the
                            // first claim on this hash has already spent the section on the game-wide one.
                            // NEITHER half is available to it. Its own image would repaint every other
                            // wearer of the texture; and a second gate on the image already shipping is not
                            // free either — the section shows the game's own picture wherever no line of it
                            // runs, so adding a gate paints those wearers in the states this claim answers,
                            // which is exactly where they showed vanilla before. Both are refused, and the
                            // messages differ only in whether the two claims also disagree about the image.
                            if (scopedRoute)
                                throw wide.Images.Any(i =>
                                        string.Equals(i.Dds, dds, StringComparison.OrdinalIgnoreCase))
                                    ? SharedTextureAlreadyWide(stock.TextureName, wide.Images[0].Claimant,
                                        claimant)
                                    : ImageCollision(stock.TextureName, wide.Images[0].Claimant, claimant);
                            AddWideImage(wide, dds, e, claimant, stock.TextureName, shaderProperty,
                                shaderProperty is null ? null : TextureMap.PropertyLabel(shaderProperty));
                            return;
                        }
                        if (!scopedRoute && !bindingAlreadyScoped)
                        {
                            retexIdx[hash] = retexBuild.Count;
                            var freshWide = new RetexBuild { Name = $"{partName}_{kind}_{hash}", Hash = hash };
                            retexBuild.Add(freshWide);
                            AddWideImage(freshWide, dds, e, claimant, stock.TextureName, shaderProperty,
                                shaderProperty is null ? null : TextureMap.PropertyLabel(shaderProperty));
                            return;
                        }
                        scopedIdx[bindingKey] = scopedBuild.Count;
                        var fresh = new ScopedBuild
                        {
                            Name = $"{partName}_{kind}_{hash}", Hash = hash,
                            TextureName = stock.TextureName, Claimant = claimant, Part = part.Token,
                            Registers = registers,
                        };
                        scopedBuild.Add(fresh);
                        AddScopedAnchors(fresh, dds, ContentGateTerm(e), ShownByFlag(e));

                        void AddScopedAnchors(ScopedBuild entry, string image, KeyRef? key, string? shown)
                        {
                            // identity is (image, key): one section carries every claiming outfit's bind,
                            // so two outfits asking for the same image on different keys stay separable
                            // rather than collapsing onto whichever key was seen first
                            var img = entry.Images.FirstOrDefault(i =>
                                string.Equals(i.Dds, image, StringComparison.OrdinalIgnoreCase)
                                && Nullable.Equals(i.Key, key)
                                && string.Equals(i.ShownBy, shown, StringComparison.Ordinal));
                            if (img is null)
                                entry.Images.Add(img = new ScopedImage
                                {
                                    Dds = image, Key = key, ShownBy = shown,
                                    Source = Path.GetFileName(authored),
                                });
                            foreach (var (name, bid, pid) in Tiers(part))
                            {
                                string ib = SigOf(model, bid, name, pid).Ib;
                                var others = MeshOthers(ib, model);
                                string? latch = others.Count > 0 ? LatchFor(model) : null;
                                entry.MeshAt[ib] = name;
                                // dedupe per (anchor, latch): several of this mod's outfits legitimately
                                // claim one shared anchor, and each contributes its OWN gated bind — the
                                // emitted OR is what makes the edit follow every claiming outfit
                                if (!img.Seen.Add($"{ib}|{latch}")) continue;
                                img.OwnerAt.TryAdd(ib, model.Stem);
                                img.Anchors.Add(new ScopedAnchor(ib,
                                    $"{partName}_{Remold.Core.Model.MeshName.Lod(name)}", latch));
                                // disclosure only where someone ELSE's model visibly co-changes
                                var cross = others.Where(w =>
                                    !w.Character.Equals(model.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                                if (latch is not null && cross.Count > 0
                                    && crossAnchorNoted.Add($"{entry.Hash}|{model.Stem}"))
                                    infos.Add($"'{stock.TextureName}' is on a mesh shared with "
                                        + $"{WearerLabels(cross)}. While {model.Stem} is on screen, theirs "
                                        + "shows this edit too.");
                            }
                        }
                    }

                    Map(t.Albedo, "a", Materials.MaterialResolver.IsBaseColor);
                    Map(t.Normal, "n", Materials.MaterialResolver.IsNormal);
                    Map(t.Rmo, "r", Materials.MaterialResolver.IsRmo);
                    Map(t.Blend, "b", Materials.MaterialResolver.IsBlend);
                    foreach (var property in (t.Textures ?? new List<PropertyTextureBinding>())
                                 .OrderBy(p => p.ShaderProperty, StringComparer.Ordinal))
                        Map(property.File, "x" + Convert.ToHexString(
                                System.Security.Cryptography.SHA256.HashData(
                                    Encoding.UTF8.GetBytes(property.ShaderProperty)))[..8].ToLowerInvariant(),
                            _ => false, property.ShaderProperty);
                }
            }
            // one anchor mesh, one image. The gate that separates two claims is a whole-frame verdict, so
            // where their anchors MEET it cannot pick between their binds and the later write wins for
            // both; claims on anchor meshes of their own stay separable and each keeps its image.
            foreach (var b in scopedBuild)
            {
                var claimed = new Dictionary<string, ScopedImage>(StringComparer.Ordinal);
                foreach (var img in b.Images)
                    foreach (var a in img.Anchors)
                    {
                        if (!claimed.TryGetValue(a.Hash, out var held)) { claimed[a.Hash] = img; continue; }
                        if (string.Equals(held.Dds, img.Dds, StringComparison.OrdinalIgnoreCase)) continue;
                        string first = held.OwnerAt[a.Hash], second = img.OwnerAt[a.Hash];
                        throw string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
                            ? OneOutfitImageCollision(b.TextureName, first, b.MeshAt[a.Hash],
                                held.Source, img.Source)
                            : SharedAnchorImageCollision(b.TextureName, b.MeshAt[a.Hash], first, second);
                    }
            }
            var retex = retexBuild
                .Select(b => new RetexEntry(b.Name, b.Hash,
                    b.Images.Select(i => new RetexImage(i.Dds, i.Key, i.ShownBy)).ToList()))
                .ToList();
            var scopedRetex = scopedBuild
                .Select(b => new ScopedRetexEntry(b.Name, b.Hash,
                    b.Images.Select(i => new ScopedRetexImage(i.Dds, i.Anchors, i.Key, i.ShownBy))
                        .ToList(), b.Part, b.Registers))
                .ToList();

            // A stock texture's hash, or null where this install can't produce one. The pick it was asked
            // for degrades with a warning of its own; the geometry and every other change still ship.
            string? TryHash(SubjectMap map, string mesh)
            {
                try { return StockTexture(map, mesh).Hash; }
                catch (Exception ex) when (ex is not BlockedAssetException)
                {
                    diagnostics.Add($"stock texture '{map.TextureName}' can't be hashed offline: {ex.Message}");
                    return null;
                }
            }

            StockTex StockTexture(SubjectMap stock, string mesh)
            {
                RefuseBlocked(stock.TextureName);
                string key = $"{stock.BundleId}|{stock.PathId}|{stock.TextureName}";
                if (stockTextures.TryGetValue(key, out var cached)) return cached;
                var src = reader.GetTextureHashSource(
                        Bundle(stock.BundleId, $"stock texture '{stock.TextureName}'"), stock.Ref)
                    ?? throw new InvalidDataException(
                        $"stock texture '{stock.TextureName}' (retexture on '{mesh}') isn't in bundle '{stock.BundleId}'");
                uint dxgi = TextureHash.Dxgi((AssetsTools.NET.Texture.TextureFormat)src.Format, src.Srgb)
                    ?? throw new InvalidOperationException(
                        $"stock texture '{stock.TextureName}' is in a format this build can't hash offline "
                        + $"(Unity format {src.Format})");
                return stockTextures[key] = new StockTex(
                    TextureHash.Compute(src.PictureData, src.Width, src.Height, src.MipCount, dxgi).ToString("x8"),
                    TextureHash.IsSrgb(dxgi));
            }


            // A scoped retexture on a stock map the Replace probe also tags: authored donor maps still
            // bind (the probe accepts the retexture's tag), but inheriting submeshes draw from the
            // slot's SAVE, taken before the scoped rebind — the pre-retexture image.
            if (scopedRetex.Count > 0 && inheritingStockTags.Count > 0)
            {
                var scopedStock = scopedRetex.Select(s => s.StockHash).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (mesh, hash) in inheritingStockTags.Where(t => scopedStock.Contains(t.Hash))
                             .DistinctBy(t => $"{t.Mesh}|{t.Hash}", StringComparer.OrdinalIgnoreCase))
                    infos.Add($"'{mesh}' keeps an original map that this mod also edits elsewhere. "
                        + "Submeshes with no edited map keep the original image.");
            }

            // ---- twin guards: one per shared signature, built once every site has spoken --------------
            // A section keyed on a signature several meshes draw on has to know WHICH sibling is on screen.
            // On the texture route each sibling gets a verdict number and the tag its own base color
            // carries, and the probe at the guarded draw writes the verdict it sees; on the wardrobe route
            // the verdict is the sibling's variant ordinal and the writers are the sections of the meshes
            // each option is worn with. Either way the variable keeps its verdict until another sighting
            // replaces it, so a pass identifying nothing acts on the last identification.
            var twinGuards = new List<TwinGuard>();
            var twinSightings = new List<TwinSighting>();
            if (twinRequests.Count > 0)
            {
                var retexturedStock = new HashSet<string>(retex.Select(r => r.Hash),
                    StringComparer.OrdinalIgnoreCase);
                // The emitter owns tag values: the probes compare against what its tag sections carry.
                var tagValueOf = MigotoEmitter.TwinTagValues(
                    pipelines.Select(pl => pl.StockMaps).Concat(rigids.Select(rg => rg.StockMaps))
                        .SelectMany(l => l ?? (IReadOnlyList<StockMapTag>)Array.Empty<StockMapTag>()),
                    scopedRetex.Select(s => s.StockHash));

                // The signature keys this build emits a section on: every capture (pooled or rigid,
                // the rigids' claims already in allCaptureHashes) and every hide. A request on any
                // other key was left behind by a site that stood down after asking — a tier degraded
                // to a vanilla draw, a capture claim withdrawn — and a guard built on it would declare
                // a variable, mint tag sections and log a diagnostic that name a section nobody writes.
                var guardedKeys = new HashSet<string>(allCaptureHashes, StringComparer.Ordinal);
                guardedKeys.UnionWith(hides);

                foreach (var group in twinRequests.Where(rq => guardedKeys.Contains(rq.Key))
                             .GroupBy(rq => rq.Key, StringComparer.Ordinal))
                {
                    // One section, one answer: two parts asking the same section to ACT for them cannot
                    // both be answered, and two subjects reaching one key are two parts. Hides are the
                    // exception — a section skipping at each hidden sibling's own draws is one section
                    // admitting several verdicts.
                    // Counted over EVERY request on the key, whichever route each was accepted on: a
                    // claimant left out of the count keeps its section and loses its verdict, which is a
                    // change acting on the sibling's draws instead of its own.
                    var claimants = new List<(SubjectModel Model, string Token)>();
                    foreach (var rq in group)
                        if (!claimants.Any(c => ReferenceEquals(c.Model, rq.Model)
                                && string.Equals(c.Token, rq.OwnToken, StringComparison.OrdinalIgnoreCase)))
                            claimants.Add((rq.Model, rq.OwnToken));
                    // one roster numbers the verdicts, so several claimants are answerable together only
                    // while they are siblings on it
                    bool together = group.All(rq => rq.Hide)
                        && claimants.All(c => ReferenceEquals(c.Model, claimants[0].Model));
                    if (claimants.Count > 1 && !together)
                        throw TwinShipRefusal(claimants[0].Token, claimants[1].Token);
                    // One variable, one meaning. A key some requests answer by the textures at the guarded
                    // draw and others only by the wardrobe has no single guard: the probe would write tag
                    // verdicts and the sightings option ordinals into the same variable, and each side's
                    // sections would open on the other's numbers.
                    var textureReqs = group.Where(rq => rq.IsTextureRoute).ToList();
                    var witnessReqs = group.Where(rq => !rq.IsTextureRoute).ToList();
                    if (textureReqs.Count > 0 && witnessReqs.Count > 0)
                        throw TwinShipRefusal(textureReqs[0].OwnToken, witnessReqs[0].OwnToken);
                    var model = claimants[0].Model;
                    if (witnessReqs.Count > 0)
                    {
                        // the wardrobe ordinal is the verdict, so one option's sections open exactly while
                        // the meshes it is worn with were the last thing sighted
                        string witVar = MigotoEmitter.TwinVar(group.Key);
                        var worn = witnessReqs.Select(rq => (int)(rq.Route.OwnVariant % 100)).Distinct()
                            .OrderBy(v => v).ToList();
                        // every request's sightings, merged now that the key has heard from all of them
                        var sighted = new List<(string Key, int Verdict)>();
                        foreach (var rq in witnessReqs)
                            foreach (var w in rq.Route.Witnesses)
                                if (!sighted.Contains(w)) sighted.Add(w);
                        // A hash two requests saw under DIFFERENT options is one mesh worn under both —
                        // struck rather than trusted — and every worn or sighted verdict must keep a
                        // witness, or the sticky verdict would stand at that option's draws with another
                        // option's answer in it.
                        if (StrikeContradictedWitnesses(sighted,
                                worn.Concat(sighted.Select(w => w.Verdict)).Distinct()) is not { } kept)
                        {
                            var stuck = witnessReqs.First(rq => ReferenceEquals(rq.Model, claimants[0].Model)
                                && string.Equals(rq.OwnToken, claimants[0].Token, StringComparison.OrdinalIgnoreCase));
                            throw TwinShipRefusal(claimants[0].Token, stuck.Mates[0]);
                        }
                        twinGuards.Add(new TwinGuard(group.Key, witVar, worn,
                            Array.Empty<TwinProbeTag>()));
                        foreach (var (key, verdict) in kept)
                            twinSightings.Add(new TwinSighting(key, witVar, verdict));
                        // one line per claimant, whatever number of sites asked on its behalf
                        foreach (var c in claimants)
                        {
                            var first = witnessReqs.First(rq => ReferenceEquals(rq.Model, c.Model)
                                && string.Equals(rq.OwnToken, c.Token, StringComparison.OrdinalIgnoreCase));
                            diagnostics.Add($"'{c.Token}' shares a draw signature with '{first.Mates[0]}'. "
                                + "Its sections act while its wardrobe option is sighted");
                        }
                        continue;
                    }
                    var ownTokens = new HashSet<string>(claimants.Select(c => c.Token),
                        StringComparer.OrdinalIgnoreCase);
                    var tokens = new HashSet<string>(ownTokens, StringComparer.OrdinalIgnoreCase);
                    foreach (var rq in textureReqs) foreach (var m in rq.Mates) tokens.Add(m);
                    // roster order, so the verdict numbers are the same on every build of this world
                    var siblings = model.Parts.Where(sp => tokens.Contains(sp.Token)).ToList();

                    var tags = new List<TwinProbeTag>();
                    var ownVerdicts = new List<int>();
                    foreach (var sibling in siblings)
                    {
                        // non-null for every sibling: a mate with no readable base color was refused at
                        // request time, and so was the own part
                        string hash = AlbedoHash(sibling)!;
                        tags.Add(new TwinProbeTag(hash, tagValueOf(hash), tags.Count + 1));
                        if (ownTokens.Contains(sibling.Token)) ownVerdicts.Add(tags.Count);
                    }
                    // every claimant has to BE one of the siblings the verdicts number: a claimant the
                    // roster walk never reached would be admitted by no verdict, and its draws would keep
                    // running where the change asked for the opposite
                    if (ownVerdicts.Count != claimants.Count)
                    {
                        string stranger = claimants.First(c => !siblings.Any(sp =>
                            string.Equals(sp.Token, c.Token, StringComparison.OrdinalIgnoreCase))).Token;
                        throw TwinShipRefusal(stranger, siblings[0].Token);
                    }
                    // Two siblings whose tags carry ONE value are one answer to the probe, which breaks
                    // only a section acting for one of that pair: a collision between two siblings it acts
                    // for neither of leaves it closed at both their draws, which is where it belongs.
                    if (TwinValueCollision(tags.Select(t => t.TagValue).ToList(), ownVerdicts) is { } oneValue)
                        throw TwinShipRefusal(siblings[oneValue.A - 1].Token, siblings[oneValue.B - 1].Token);
                    // A repainted probe tag hides its stock color from the draw probe — the rebound
                    // image answers to no tag — so the retexture's own section writes the verdict at
                    // bind time instead (the emitter pairs them by hash). That bind proves the drawer
                    // only for a wardrobe option's exclusive color: options are worn one at a time,
                    // where same-frame siblings both bind their colors every frame, and a color other
                    // options' meshes also bind would answer for draws that prove nothing.
                    foreach (var tag in tags)
                    {
                        if (!retexturedStock.Contains(tag.TexHash)) continue;
                        var sib = siblings[tag.Verdict - 1];
                        string mateName = siblings.First(s => !ReferenceEquals(s, sib)).Token;
                        long sibVariant = VariantOf(model, sib);
                        if (sibVariant <= 0)
                            throw new AuthoredRefusalException(
                                $"'{sib.Token}' and '{mateName}' can only be told apart by "
                                + $"'{sib.Token}'s base color, which this mod repaints. Once repainted, "
                                + "neither can be recognized, so this mod can't be built");
                        foreach (var other in model.Parts)
                            if (VariantOf(model, other) != sibVariant && BindsStock(other, tag.TexHash))
                                throw new AuthoredRefusalException(
                                    $"'{other.Token}' uses the same base color that tells '{sib.Token}' "
                                    + $"apart, and this mod repaints it. The repaint would change "
                                    + $"'{other.Token}' too, so this mod can't be built");
                    }
                    twinGuards.Add(new TwinGuard(group.Key, MigotoEmitter.TwinVar(group.Key), ownVerdicts, tags));
                    foreach (int own in ownVerdicts)
                    {
                        string ownToken = siblings[own - 1].Token;
                        string named = siblings.First(sp =>
                            !string.Equals(sp.Token, ownToken, StringComparison.OrdinalIgnoreCase)).Token;
                        diagnostics.Add($"'{ownToken}' shares a draw signature with '{named}'. "
                            + "Its sections act while its own textures answer for it");
                    }
                }
            }

            // ---- the toon ramps picked on materials of parts this build does not replace -------------
            // Nothing about the part changes but its shading, so there is no geometry, no encode and no
            // stock texture to override: the pick becomes one draw-scoped bind per rendered tier of the
            // part, gated on one of the material's ordinary maps being sighted at the draw. That map is
            // what says WHICH material is drawing — the ramp's own hash cannot, since the runtime reads
            // too little of a ramp for two of them to differ on it.
            var stockRampBinds = new List<StockRampBind>();
            // What the picks add to the two records a mod carries about itself: one repair row per pick the
            // build actually shipped, and the subjects those picks name — which is the ONLY thing that says
            // whose mod this is when the mod replaces and retextures nothing at all.
            var shippedPicks = new List<RepairData.StockRampRecord>();
            var pickSubjects = new List<(string Character, string Stem)>();
            if (rampPicks.Count > 0)
            {
                var picks = rampPicks;
                // Every hash this build already tags, by any mechanism. A probe reads the value the
                // section on that hash carries, so a map one of them owns would answer with something
                // other than the value this bind tests for and the ramp would never bind — silently.
                // Stock-ramp material tags are the exception: they derive the same value from the hash.
                var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var materialTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // …and, for the slot tags among them, the KIND each one carries. The ramp's own hash is
                // claimed too, and there the kind decides: a hash already tagged as a RAMP answers the ramp
                // probe with the very value it tests for, so the pick rides that tag. A hash tagged as
                // anything else answers with a value the probe never fires on, and the bind would go out
                // and do nothing — held back below instead. First tag wins, exactly as the emitter dedupes.
                var slotKinds = new Dictionary<string, StockMapKind>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in pipelines.SelectMany(x => x.StockMaps ?? Array.Empty<StockMapTag>())
                             .Concat(rigids.SelectMany(x => x.StockMaps ?? Array.Empty<StockMapTag>())))
                {
                    claimed.Add(t.Hash);
                    slotKinds.TryAdd(t.Hash, t.Kind);
                }
                foreach (var e in retex) claimed.Add(e.Hash);
                foreach (var e in scopedRetex) claimed.Add(e.StockHash);
                foreach (var h in MigotoEmitter.MintedTwinTagHashes(twinGuards)) claimed.Add(h);
                var materialTagRecognizers = new List<Func<string, bool>>
                {
                    Materials.MaterialResolver.IsBaseColor,
                    Materials.MaterialResolver.IsNormal,
                    Materials.MaterialResolver.IsRmo,
                };
                if (slotCatalog?.Slots(ShaderSlotCatalog.BlendTex).Count > 0)
                    materialTagRecognizers.Add(Materials.MaterialResolver.IsBlend);
                foreach (var pick in picks)
                {
                    RefuseBlocked(pick.Character, pick.Outfit, pick.Mesh);
                    // Read and gated FIRST, before any hold-back: a file the author got wrong must not
                    // build clean on one machine and fail on the next, and every hold-back below turns on
                    // what this install happens to hold.
                    string dds = Path.Combine(workDir, $"stockramp_{stockRampBinds.Count}.dds");
                    try
                    {
                        var src = project.Resolve(pick.Ramp);
                        var image = DdsReader.Parse(File.ReadAllBytes(src), Path.GetFileName(src));
                        if (image.Width != RampWidth || image.Height != RampHeight)
                            throw new InvalidDataException($"{Path.GetFileName(src)} is "
                                + $"{image.Width}x{image.Height}, and a toon ramp is {RampWidth}x{RampHeight}");
                        File.Copy(src, dds, overwrite: true);
                    }
                    catch (Exception e)
                    {
                        throw new AuthoredRefusalException("the toon ramp picked on "
                            + $"'{pick.Mesh}' · '{pick.Material}' can't be built: {e.Message}");
                    }
                    // The plan resolved this pick against the live roster, so the part and its material are
                    // the ones this install holds; nothing here re-decides that. What is left is what only
                    // the whole build knows — whether a ramp binds anywhere at all, and whether this ramp
                    // can be told apart from every other texture the build tags — and a pick that fails
                    // either is an explicit choice that cannot be emitted, so the build refuses it.
                    var model = Subject(pick.Character, pick.Outfit);
                    RefuseBlocked(model.Character, model.Stem);
                    var part = model.Parts.First(x =>
                        string.Equals(x.SlotName, pick.Mesh, StringComparison.OrdinalIgnoreCase));
                    RefuseBlocked(part.SlotName, part.MeshAddress);
                    var material = part.Materials.First(m =>
                        string.Equals(m.Name, pick.Material, StringComparison.OrdinalIgnoreCase));
                    var rampMap = material.Maps.First(m => Materials.MaterialResolver.IsRamp(m.Slot));
                    string? rampHash = TryHash(rampMap, pick.Mesh);
                    // The ramp's OWN hash has to carry the ramp kind value, which is what says which
                    // register holds a ramp at the draw. A hash something else in this build already tags
                    // carries that section's value instead — the ini parse keeps one section per hash — so
                    // the probe would read a value it never fires on and the bind would go out and do
                    // nothing. A slot tag of the ramp kind is the one claimant that answers correctly.
                    if (rampHash is not null && claimed.Contains(rampHash)
                        && !(slotKinds.TryGetValue(rampHash, out var already)
                             && already == StockMapKind.Ramp))
                        throw new AuthoredRefusalException("the toon ramp picked on "
                            + $"'{pick.Mesh}' · '{pick.Material}' can't be built: it cannot be told apart "
                            + "from another texture this mod changes. Remove the pick, or build the two "
                            + "changes as separate mods");
                    // Prefer the base colour, then whichever ordinary map answers. The tag identifies a
                    // material only when no sibling material on this part can sight the same hash; an
                    // unresolvable sibling leaves that uniqueness unproved and holds the pick back.
                    string? matHash = null;
                    foreach (var isSlot in materialTagRecognizers)
                    {
                        foreach (var m in material.Maps.Where(x => isSlot(x.Slot)))
                        {
                            if (TryHash(m, pick.Mesh) is not { } h
                                || (claimed.Contains(h)
                                    && (!materialTags.Contains(h) || slotKinds.ContainsKey(h)))
                                || !UniqueToMaterial(h)) continue;
                            matHash = h;
                            break;
                        }
                        if (matHash is not null) break;
                    }
                    if (rampHash is null || matHash is null)
                        throw new AuthoredRefusalException("the toon ramp picked on "
                            + $"'{pick.Mesh}' · '{pick.Material}' can't be built: " + (rampHash is null
                                ? "its own toon ramp cannot be recognized in game"
                                : "no other map on that material can be recognized in game")
                            + ". Remove the pick, or pick the ramp on another material");

                    bool UniqueToMaterial(string hash)
                    {
                        foreach (var sibling in part.Materials)
                        {
                            if (ReferenceEquals(sibling, material) || sibling.IsPlaceholder) continue;
                            if (sibling.Problem is not null) return false;
                            foreach (var map in sibling.Maps)
                            {
                                if (TryHash(map, pick.Mesh) is not { } siblingHash) return false;
                                if (string.Equals(hash, siblingHash, StringComparison.OrdinalIgnoreCase))
                                    return false;
                            }
                        }
                        return true;
                    }
                    claimed.Add(matHash);
                    materialTags.Add(matHash);
                    // The ramp's hash is tagged too, as the ramp KIND: a later pick naming the same ramp
                    // rides that tag. No material may take it as its identifying map.
                    claimed.Add(rampHash);
                    slotKinds.TryAdd(rampHash, StockMapKind.Ramp);
                    string label = PartName(model.Character, part.Token);
                    // The position the pick rides, which the plan states: the change that picked it names
                    // it, so one press switches the pick exactly as it switches that change's own binds.
                    KeyRef? rampKey = rampGates.GetValueOrDefault(pick);
                    // and where the change answers several positions, the content flag stands in for the
                    // position term exactly as it does in that change's own draw gate
                    string? rampShown = rampShownFlags.GetValueOrDefault(pick);
                    int boundBefore = stockRampBinds.Count;
                    foreach (var (name, bid, pid) in Tiers(part))
                    {
                        string ib = SigOf(model, bid, name, pid).Ib;
                        var others = MeshOthers(ib, model);
                        string? latch = others.Count > 0 ? LatchFor(model) : null;
                        stockRampBinds.Add(new StockRampBind(
                            $"{label}_{Remold.Core.Model.MeshName.Lod(name)}_ramp", ib, matHash, rampHash,
                            dds, rampKey, latch, part.Token, rampShown));
                        var cross = others.Where(w =>
                            !w.Character.Equals(model.Character, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (latch is not null && cross.Count > 0)
                            infos.Add($"'{part.Token}' is on a mesh shared with {WearerLabels(cross)}. "
                                + $"While {model.Stem} is on screen, theirs shades with this ramp too.");
                    }
                    // Recorded only where the pick actually BOUND somewhere: the record states what the mod
                    // carries, and a part whose every tier went unrendered ships no file and no bind.
                    if (stockRampBinds.Count > boundBefore)
                    {
                        shippedPicks.Add(new RepairData.StockRampRecord(model.Character, model.Stem,
                            part.SlotName, material.Name, Path.GetFileName(dds),
                            // a stock ramp is a pick on the PART, not on one position's content, so it
                            // reads the part's own answer rather than any one state's
                            RepairIntent(PlannedFor(model, part), null)));
                        pickSubjects.Add((model.Character, model.Stem));
                    }
                }
            }

            // A plan is a contract, not permission for the low-level compiler to re-decide a requested
            // file: a build refuses if a resolved ramp or map did not reach a concrete bind. Every route
            // makes that promise now — the released boundary that used to be excused from it is gone.
            if (shippedPicks.Count != rampPicks.Count)
                throw new InvalidOperationException(
                    $"Build-plan/emitter drift: {rampPicks.Count - shippedPicks.Count} resolved stock "
                    + "ramp binding(s) did not reach runtime emission");
            foreach (var binding in authoredPlan.Bindings.Where(binding =>
                         binding.Decision.Verdict == BuildPlanVerdict.Resolved
                         && binding.EffectiveValue?.ProjectAsset is not null
                         && binding.AuthoredSlot.Input is TargetInputKind.Geometry
                            or TargetInputKind.BaseColor or TargetInputKind.Normal
                            or TargetInputKind.Rmo or TargetInputKind.Blend or TargetInputKind.Ramp
                            or TargetInputKind.Texture))
            {
                // A ramp bound on a GAME slot is a pick on the installed material, which ships through the
                // stock-ramp binds and was counted above — it produces no donor map file to look for here,
                // whether or not the change that picked it also carries content.
                if (binding.AuthoredSlot.Domain == TargetSlotDomain.Game
                    && binding.AuthoredSlot.Input == TargetInputKind.Ramp) continue;
                var edit = EditAnswering(binding.EditDefinitionId);
                if (edit is null)
                    throw new InvalidOperationException(
                        $"Build-plan/emitter drift: '{binding.RowId}' has no compiled change");
                if (binding.AuthoredSlot.Input == TargetInputKind.Geometry)
                {
                    if (!repairRoutes.ContainsKey(edit))
                        throw new InvalidOperationException(
                            $"Build-plan/emitter drift: '{binding.RowId}' has no replacement route");
                    continue;
                }
                int? submesh = binding.AuthoredSlot.SubmeshIndex;
                DonorMapSlot? which = binding.AuthoredSlot.Input switch
                {
                    TargetInputKind.BaseColor => DonorMapSlot.BaseColor,
                    TargetInputKind.Normal => DonorMapSlot.Normal,
                    TargetInputKind.Rmo => DonorMapSlot.Rmo,
                    TargetInputKind.Blend => DonorMapSlot.Blend,
                    TargetInputKind.Ramp => DonorMapSlot.Ramp,
                    TargetInputKind.Texture => null,
                    _ => throw new InvalidOperationException("unexpected planned map input"),
                };
                bool produced = submesh is not null && (which is { } fixedKind
                    ? repairMaps.GetValueOrDefault(edit)?.ContainsKey((submesh.Value, fixedKind)) == true
                    : binding.AuthoredSlot.ShaderProperty is { } property
                      && repairPropertyMaps.GetValueOrDefault(edit)?.ContainsKey(
                          (submesh.Value, property)) == true);
                if (!produced)
                    throw new InvalidOperationException(
                        $"Build-plan/emitter drift: '{binding.RowId}' did not produce a bound file");
            }

            // ---- emit -------------------------------------------------------------------------------
            log?.Invoke("Assembling the mod files…");
            MigotoEmitter.Result emitted;
            var emitter = new MigotoEmitter
            {
                OperatorCacheDir = caches?.OperatorDir,
                CpuLimit = encoderCpuLimit,
                Slots = slotPlan,
            };
            // Authored material-value patches, resolved to their emitted carrier draws and handed to the
            // emitter as first-class input: the emitter wraps each patched submesh draw in every list
            // that issues it — the full draw list AND the routed per-range lists a multi-material target
            // moves its draws into. The generated patch shaders are written here, before emission, so
            // the emitter can refuse a request whose shader never landed.
            var materialPatches = new List<MaterialPatchEmission>();
            var patchFiles = MaterialValuePatchEmitter.Emit(authoredPlan)
                .ToDictionary(file => file.OutputId, StringComparer.Ordinal);
            foreach (var emission in authoredPlan.RuntimeEmissions.Where(item =>
                         item.Emission.Kind == BuildEmissionKind.MaterialValuePatch))
            {
                var binding = authoredPlan.Bindings.SingleOrDefault(row =>
                    string.Equals(row.RowId, emission.Consumer, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"material emission '{emission.Emission.Id}' has no binding row");
                var edit = EditAnswering(binding.EditDefinitionId) is { Verb: EditVerbs.Replace } made
                    ? made
                    : throw new InvalidOperationException(
                        $"material emission '{emission.Emission.Id}' has no mesh replacement to bind through");
                if (!repairRoutes.TryGetValue(edit, out var route))
                    throw new InvalidOperationException(
                        $"material emission '{emission.Emission.Id}' has no emitted route to bind through");
                int submesh = binding.CurrentSlot?.SubmeshIndex
                    ?? binding.CurrentSlot?.MaterialSlotIndex
                    ?? throw new InvalidOperationException(
                        $"material emission '{emission.Emission.Id}' has no submesh target");
                var contract = binding.RenderPlan?.Contracts.SingleOrDefault(candidate =>
                    emission.Emission.RenderContractIds.Contains(candidate.Id,
                        StringComparer.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"material emission '{emission.Emission.Id}' has no render contract");
                var shaderHashes = contract.PixelShaderHashes is { Count: > 0 } hashes
                    ? hashes
                    : throw new InvalidOperationException(
                        $"material emission '{emission.Emission.Id}' has no exact pixel-shader hashes");
                int shaderFilterIndex = contract.PixelShaderFilterIndex
                    ?? throw new InvalidOperationException(
                        $"material emission '{emission.Emission.Id}' has no exact filter_index");
                var patch = emission.Emission.MaterialPatch
                    ?? throw new InvalidOperationException(
                        $"material emission '{emission.Emission.Id}' has no patch payload");
                var output = authoredPlan.OutputArtifacts.SingleOrDefault(item =>
                        item.Artifact.Included
                        && item.Artifact.EmissionIds.Contains(emission.Emission.Id, StringComparer.Ordinal)
                        && string.Equals(item.Artifact.Purpose, MaterialValueBuildSupport.OutputPurpose,
                            StringComparison.Ordinal))?.Artifact
                    ?? throw new InvalidOperationException(
                        $"material emission '{emission.Emission.Id}' has no patch output");
                if (!patchFiles.TryGetValue(output.Id, out var generated))
                    throw new InvalidOperationException(
                        $"material emission '{emission.Emission.Id}' has no generated shader");
                string fullShader = Path.Combine(tmpMod,
                    generated.File.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullShader)!);
                File.WriteAllText(fullShader, generated.Text, new UTF8Encoding(false));
                materialPatches.Add(new MaterialPatchEmission(route.Sfx, submesh,
                    MaterialPatchKey(emission.Emission.Id), patch.ConstantBufferSlot,
                    generated.File, shaderFilterIndex, shaderHashes, patch.ByteWidth));
            }
            if (pipelines.Count > 0 || rigids.Count > 0)
            {
                emitted = emitter.Build(new PoolBuildRequest
                {
                    Pipelines = pipelines,
                    Rigids = rigids.Count > 0 ? rigids : null,
                    OutDir = tmpMod,
                    HideHashes = hides.Count > 0 ? hides : null,
                    Retextures = retex.Count > 0 ? retex : null,
                    ScopedRetextures = scopedRetex.Count > 0 ? scopedRetex : null,
                    ToggleKey = modKey,
                    HideKeys = hideKeys.Count > 0 ? hideKeys : null,
                    HideLatches = hideLatches.Count > 0 ? hideLatches : null,
                    Latches = latchList.Count > 0 ? latchList : null,
                    KeyCycles = keyCycles.Count > 0 ? keyCycles : null,
                    HiddenFlags = shippedFlags.Count > 0 ? shippedFlags : null,
                    ShownFlags = shippedShownFlags.Count > 0 ? shippedShownFlags : null,
                    TwinGuards = twinGuards.Count > 0 ? twinGuards : null,
                    TwinSightings = twinSightings.Count > 0 ? twinSightings : null,
                    StockRamps = stockRampBinds.Count > 0 ? stockRampBinds : null,
                    MaterialPatches = materialPatches.Count > 0 ? materialPatches : null,
                });
            }
            else
            {
                // Everything this mod carried was held back on this install — each with a warning of its
                // own above. Said plainly here, because the alternative is a mod folder that changes
                // nothing and reads as a build that worked.
                if (retex.Count == 0 && hides.Count == 0 && scopedRetex.Count == 0
                    && stockRampBinds.Count == 0)
                    throw new AuthoredRefusalException(
                        "nothing survived to build: every change in this mod was held back on this "
                        + "install. The warnings above say which, and why");
                emitted = emitter.BuildOverlaysOnly(tmpMod, retex, hides, modKey,
                    hideKeys.Count > 0 ? hideKeys : null,
                    scopedRetex.Count > 0 ? scopedRetex : null,
                    latchList.Count > 0 ? latchList : null,
                    hideLatches.Count > 0 ? hideLatches : null,
                    null,
                    twinGuards.Count > 0 ? twinGuards : null,
                    twinSightings.Count > 0 ? twinSightings : null,
                    stockRampBinds.Count > 0 ? stockRampBinds : null,
                    keyCycles.Count > 0 ? keyCycles : null,
                    shippedShownFlags.Count > 0 ? shippedShownFlags : null);
            }
            warnings.AddRange(emitted.Warnings);
            diagnostics.AddRange(emitted.Diagnostics);

            StampCoreBuild(tmpMod);

            WriteSidecar(project, env, tmpMod, work,
                sidecarCaptures.Values.Concat(rigids.SelectMany(r => r.Hashes)), hides, retex,
                scopedRetex, latchList, twinSightings, slotCatalog, slotPlan,
                stockRampBinds, pickSubjects);

            // ---- the repair record, unless the author asked for the mod to ship without one -------------
            // Gated whole rather than written and deleted: the completion pass below and RepairChange both
            // report what they could not record, and a mod that ships no record has nothing to report.
            if (project.Info.IncludeRepairData)
            {
                // The compiled donor records the full union; the emitted skin now addresses its compact,
                // order-preserving projection. Complete every pooled geometry record from the emitter's
                // authoritative source-row map, whether or not the pipeline also carries coverage groups.
                foreach (var (edit, geo) in repairGeometry.ToList())
                {
                    if (!repairRoutes.TryGetValue(edit, out var route) || route.Route != "pooled"
                        || geo.Union is not { } compiledUnion) continue;
                    if (emitted.Palette?.TryGetValue(route.Sfx, out var pal) != true)
                    {
                        diagnostics.Add($"repair data ({route.Sfx}): the emitted compact palette has no "
                            + "recorded row map, so its union is left out");
                        repairGeometry[edit] = geo with { Union = null };
                        continue;
                    }
                    if (compiledUnion.Bones.Count != pal.CompiledUnionBones)
                    {
                        diagnostics.Add($"repair data ({route.Sfx}): the compiled union and the emitted "
                            + "palette disagree on their source bone count, so the union is left out");
                        repairGeometry[edit] = geo with { Union = null };
                        continue;
                    }
                    repairGeometry[edit] = geo with
                    {
                        Union = CompactRepairUnion(compiledUnion, pal.UnionSourceRows),
                    };
                }

                // The appended coverage-group palette slots a pipeline's shipped skin indices land on: only
                // the emission knows where that region sits, so the geometry records are completed here,
                // once it has run, rather than guessed at compile time.
                foreach (var (edit, bones) in repairGroupBones)
                {
                    if (bones.Count == 0 || !repairGeometry.TryGetValue(edit, out var geo)) continue;
                    string groupSfx = repairRoutes[edit].Sfx;
                    // Everything below has to hold before a slot table is worth writing: a table stated
                    // against an address or a union the shipped bytes were not shifted by names the wrong
                    // bones, and decodes cleanly while doing it. Where it does not hold the record says
                    // nothing, which a reader can act on.
                    if (emitted.Palette?.TryGetValue(groupSfx, out var pal) != true)
                    {
                        diagnostics.Add($"repair data ({groupSfx}): the wardrobe-group palette region has "
                            + "no recorded address, so its bones are left out");
                        continue;
                    }
                    if (geo.Union is not { } compactUnion || compactUnion.Bones.Count != pal.UnionBones)
                    {
                        diagnostics.Add($"repair data ({groupSfx}): the compiled union and the emitted "
                            + "palette disagree on their bone count, so the wardrobe-group slots are left out");
                        continue;
                    }
                    repairGeometry[edit] = geo with
                    {
                        GroupSlots = pal.GroupSourceRows.Select((source, i) =>
                            new RepairData.GroupSlot(pal.GroupBase + (uint)i,
                                RepairData.Bone(bones[source]))).ToList(),
                    };
                }
                var intentAssetIds = authoredPlan.Bindings.SelectMany(binding => new[]
                    {
                        binding.RequestedBinding.ProjectAssetId,
                        binding.EffectiveValue?.ProjectAsset?.Id,
                    }).OfType<string>().ToHashSet(StringComparer.Ordinal);
                var intentAssets = authoredPlan.IntentAssets
                    .Where(asset => intentAssetIds.Contains(asset.Id))
                    .Select(asset => new RepairData.IntentAssetRecord(asset.Id,
                        IntentName(asset.Kind), asset.Label, asset.Source, asset.Value)).ToList();
                RepairData.Write(tmpMod, new RepairData.Payload(
                    RepairData.Schema,
                    env.CatalogVersion, env.AppVersion, modKey,
                    work.Select(w => (w.Model.Character, w.Model.Stem)).Concat(pickSubjects).Distinct()
                        .Select(s => new RepairData.SubjectRef(s.Character, s.Stem)).ToList(),
                    work.Select(w => RepairChange(w.Edit, w.Model, w.Part)).ToList(),
                    shippedPicks.Count > 0 ? shippedPicks : null,
                    intentAssets is { Count: > 0 } ? intentAssets : null));
            }
            else
                diagnostics.Add("repair data: this mod is built without it, so the folder cannot be read "
                    + "back into a project");

            // ---- swap into place, then the distribution zip -----------------------------------------
            // The previous build is renamed ASIDE, never deleted in place: a single locked file under it
            // fails a recursive delete halfway, which would leave the author with neither the build they
            // had nor the one they asked for. A failed publish puts the aside back.
            // An aside whose delete failed on an EARLIER build (a file was locked then) is swept here,
            // where the lock has usually let go — otherwise nothing ever reclaims it.
            try
            {
                foreach (var stale in Directory.GetDirectories(
                             Path.GetDirectoryName(finalDir)!, Path.GetFileName(finalDir) + ".old-*"))
                    try { Directory.Delete(stale, recursive: true); } catch { /* still locked — next build retries */ }
            }
            catch { /* parent unreadable — the publish below reports the real problem */ }
            string? aside = null;
            if (Directory.Exists(finalDir))
            {
                aside = finalDir + ".old-" + Guid.NewGuid().ToString("N");
                PublishMove(finalDir, aside);
            }
            try { PublishMove(tmpMod, finalDir); }
            catch
            {
                if (aside is not null) PublishMove(aside, finalDir);
                throw;
            }
            if (aside is not null)
                try { Directory.Delete(aside, recursive: true); }
                catch { /* the superseded build is regenerable; a leftover aside costs only disk */ }
            string? builtZip = null;
            if (zip)
            {
                PublishDistributionZip(finalDir, zipPath);
                builtZip = zipPath;
            }
            log?.Invoke($"Built {finalDir}.");
            return new Result(finalDir, builtZip, warnings, infos, diagnostics);
        }
        finally
        {
            foreach (var d in new[] { workDir, tmpMod })
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
        }
    }

    internal static void StampCoreBuild(string modDir)
    {
        string ini = Path.Combine(modDir, "mod.ini");
        if (!File.Exists(ini))
            throw new InvalidOperationException("the build emitted no mod.ini to stamp");

        const string markerPrefix = "; generated override set ";
        string text = File.ReadAllText(ini);
        if (text.Split('\n').Any(line => line.TrimEnd('\r').StartsWith(markerPrefix,
                StringComparison.Ordinal)))
            throw new InvalidOperationException("the emitted mod.ini already carries a Core build marker");

        int cursor = 0;
        int markerAt = -1;
        ReadHeader(MigotoEmitter.PooledIniHeader);
        ReadHeader(MigotoEmitter.RigidIniHeader);
        ReadHeader(MigotoEmitter.OverlayIniHeader);
        if (markerAt < 0)
            throw new InvalidOperationException(
                "the emitted mod.ini carries no generated comment header to place the Core build marker");

        string marker = markerPrefix + CoreBuildIdentity.ShortHash + '\n';
        File.WriteAllText(ini, text.Insert(markerAt, marker), new UTF8Encoding(false));

        void ReadHeader(string header)
        {
            if (!text.AsSpan(cursor).StartsWith(header, StringComparison.Ordinal)) return;
            cursor += header.Length;
            if (cursor >= text.Length || text[cursor] != '\n')
                throw new InvalidOperationException(
                    "the emitted mod.ini carries no generated comment header seam to place the Core build marker");
            markerAt = cursor;
            cursor++;
        }
    }

    /// <summary>Windows scanners may briefly hold a completed staging file between close and rename.</summary>
    private static void PublishMove(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException) when (attempt < 5 && Directory.Exists(source)
                && !Directory.Exists(destination))
            {
                System.Threading.Thread.Sleep(20 * (attempt + 1));
            }
        }
    }

    /// <summary>Write the distribution zip aside and move it onto <paramref name="zipPath"/>, so the
    /// published name only ever holds a complete archive: a write that dies partway (an unreadable source
    /// file) would otherwise leave a truncated zip under the real name beside a sound mod folder. The temp
    /// is swept on failure.</summary>
    internal static void PublishDistributionZip(string modDir, string zipPath)
    {
        string tmp = zipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDistributionZip(modDir, tmp);
            File.Move(tmp, zipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ } }
        }
    }

    /// <summary>The distribution zip: every file under <paramref name="modDir"/>, nested under the folder's
    /// own name so an extract lands one mod folder. Encoded textures are already block-compressed, so they
    /// are STORED; everything else (ini, hlsl, json, the operator and geometry buffers) is deflated.
    /// <b>Reproducible:</b> entry order is fixed and every entry's timestamp is pinned to
    /// <see cref="ZipEntryStamp"/>, so the same mod folder always zips to the same bytes.</summary>
    private static void WriteDistributionZip(string modDir, string zipPath)
    {
        string baseName = Path.GetFileName(modDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(modDir, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string entry = baseName + "/" + Path.GetRelativePath(modDir, file).Replace(Path.DirectorySeparatorChar, '/');
            // Built by hand rather than CreateEntryFromFile, which stamps the source file's mtime and seals
            // the entry against a later correction.
            var e = archive.CreateEntry(entry,
                file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                    ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            e.LastWriteTime = ZipEntryStamp;
            using var src = File.OpenRead(file);
            using var dst = e.Open();
            src.CopyTo(dst);
        }
    }

    /// <summary>The fixed timestamp every distribution-zip entry carries, so two builds of identical inputs
    /// produce identical bytes. Any constant would do as long as it is at or after 1980-01-01, which is the
    /// earliest the zip format's DOS time field can express.</summary>
    private static readonly DateTimeOffset ZipEntryStamp = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static string IntentName<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    /// <summary>One pipeline's per-pool-part hiding states: the mates that brought their own (a retextured
    /// pool part, whose hide has no other draw to land on) plus the pipeline's own replaced part. Where the
    /// replaced part is also a mate that brought terms the two union, since one draw owes one skip per
    /// hiding position however many changes named it. Null when nothing hides anything.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<KeyRef>>? PoolSuppress(
        IReadOnlyDictionary<string, IReadOnlyList<KeyRef>> mates, string ownPart,
        IReadOnlyList<KeyRef>? own)
    {
        var map = new Dictionary<string, IReadOnlyList<KeyRef>>(mates, StringComparer.Ordinal);
        if (own is { Count: > 0 })
        {
            var merged = new List<KeyRef>(own);
            if (map.TryGetValue(ownPart, out var brought))
                foreach (var t in brought) if (!merged.Contains(t)) merged.Add(t);
            map[ownPart] = merged;
        }
        return map.Count > 0 ? map : null;
    }

    /// <summary>Short part name for ini section names and filenames, which both eat it: lowercase character
    /// + part token, with every character that is not a letter or digit collapsed to <c>_</c>. Letters and
    /// digits are judged by Unicode, so a non-ASCII name keeps its own characters.</summary>
    internal static string PartName(string character, string token)
    {
        var sb = new StringBuilder();
        foreach (char c in $"{character}_{token}".ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    /// <summary>An ini-safe short name for one material patch emission, stable per plan: the emission id
    /// carries row identity, and the digest keeps section names bounded whatever it holds.</summary>
    private static string MaterialPatchKey(string emissionId) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(emissionId)).AsSpan(0, 8))
        .ToLowerInvariant();

    /// <summary>One compiled Replace as the repair record describes it: which shipped files hold its
    /// streams, the shape those streams are sliced in, and the bone order their blend indices address.
    /// <paramref name="pool"/> is null on the rigid route, which has no pool and no union.
    ///
    /// <para><paramref name="shippedStream"/> names the file a stream ordinal ships under, or null where
    /// the route ships that stream's bytes nowhere. The compile writes a buffer per stream the layout
    /// declares while each route copies a fixed set out, so a stream the emission leaves behind is recorded
    /// as a diagnostic rather than as a channel table a reader would slice short.</para></summary>
    private static RepairData.UnionRecord CompactRepairUnion(RepairData.UnionRecord source,
        IReadOnlyList<int> retainedRows)
    {
        const int bytesPerPose = 16 * sizeof(float);
        var all = Convert.FromBase64String(source.BindPoses);
        if (all.Length != source.Bones.Count * bytesPerPose)
            throw new InvalidDataException($"repair union has {source.Bones.Count} bones but "
                + $"{all.Length} bind-pose bytes");
        var compact = new byte[retainedRows.Count * bytesPerPose];
        for (int i = 0; i < retainedRows.Count; i++)
        {
            int sourceRow = retainedRows[i];
            if (sourceRow < 0 || sourceRow >= source.Bones.Count)
                throw new InvalidDataException($"repair union row {sourceRow} is outside "
                    + $"0..{source.Bones.Count - 1}");
            Buffer.BlockCopy(all, sourceRow * bytesPerPose, compact, i * bytesPerPose, bytesPerPose);
        }
        return new RepairData.UnionRecord(retainedRows.Select(i => source.Bones[i]).ToList(),
            Convert.ToBase64String(compact), source.Space);
    }

    private static RepairData.GeometryRecord RepairGeometry(SwapCompile.Result compiled,
        Func<int, string?> shippedStream, string indexFile, string anchor, IReadOnlyList<string>? pool,
        string sfx, List<string> diagnostics)
    {
        var streams = new List<RepairData.StreamFile>();
        foreach (var s in compiled.Streams.OrderBy(s => s.Stream))
        {
            if (shippedStream(s.Stream) is { } file) streams.Add(new RepairData.StreamFile(s.Stream, file));
            else
                diagnostics.Add($"repair data ({sfx}): stream {s.Stream} is in the compiled layout but ships "
                    + "in no buffer, so its channels cannot be read back");
        }
        var channels = (compiled.Channels ?? Array.Empty<UnityMesh.ChannelDef>())
            .Select(c => new RepairData.ChannelRecord(c.Stream, c.Offset, c.Format, c.Dimension)).ToList();
        var submeshes = (compiled.Submeshes ?? Array.Empty<UnityMesh.SubMeshDef>())
            .Select(s => new RepairData.SubmeshSpan(s.FirstByte, s.IndexCount, s.BaseVertex)).ToList();
        RepairData.UnionRecord? union = null;
        if (compiled.UnionBoneHashes is { Count: > 0 } bones)
        {
            var poses = compiled.UnionBindPoses ?? Array.Empty<float[]>();
            // A bind-pose list that does not pair one-to-one with the bone order would decode as a table
            // whose every row past the first gap names the wrong bone, and nothing downstream could tell.
            if (poses.Count != bones.Count)
                throw new InvalidDataException(
                    $"union has {bones.Count} bones but {poses.Count} bind poses");
            union = new RepairData.UnionRecord(bones.Select(RepairData.Bone).ToList(),
                RepairData.BindPoses(poses), compiled.UnionInSceneRestSpace ? "scene_rest" : "anchor");
        }
        return new RepairData.GeometryRecord(streams, indexFile, compiled.VertexCount,
            compiled.IndexFormat == 0 ? "R16_UINT" : "R32_UINT", submeshes, channels, anchor, pool, union);
    }

    /// <summary>The <c>gf2mod.json</c> sidecar: identity, provenance, the override hash list a mod manager
    /// can predict conflicts from, build-time versions, and the shader slot coverage the mod was built
    /// under. Stock 3DMigoto ignores it. It carries no timestamp, so identical inputs write an identical
    /// sidecar.
    ///
    /// <para>The schema NUMBER is frozen. New members ride under it: the manager reading this ignores
    /// members it does not know, so an addition costs a reader nothing, while a rename or a changed meaning
    /// would need the number to move.</para>
    ///
    /// <para>The slot record makes a published mod auditable without reading anyone's host configuration:
    /// which measurement named the registers, which registers this mod actually probes, and what it falls
    /// back to where a probe finds nothing. A build with no readable catalog records the fallback and no
    /// registers, which is the state a reader has to be able to tell from full coverage.</para></summary>
    /// <param name="stockRamps">the ramp binds this build shipped. Their hashes join the override list like
    /// every other hash a mod acts on, so a manager can predict a conflict with them.</param>
    /// <param name="pickSubjects">the subjects those picks name. A mod may consist of nothing but picks, and
    /// then this is the only thing that can say whose outfit the card is about.</param>
    private static void WriteSidecar(AuthoredProject project, BuildEnv env, string modDir,
        IReadOnlyList<(BuildWorkItem Edit, SubjectModel Model, SubjectPart Part)> work,
        IEnumerable<string> captureHashes, IEnumerable<string> hides, IEnumerable<RetexEntry> retex,
        IEnumerable<ScopedRetexEntry> scopedRetex, IEnumerable<WitnessLatch> latches,
        IEnumerable<TwinSighting> twinSightings, ShaderSlotCatalog? slotCatalog, ShaderSlotPlan slotPlan,
        IReadOnlyList<StockRampBind> stockRamps,
        IReadOnlyList<(string Character, string Stem)> pickSubjects)
    {
        string? preview = null;
        if (project.Info.Preview is { } prev)
        {
            var src = project.Resolve(prev);
            if (File.Exists(src))
            {
                preview = "preview" + Path.GetExtension(src).ToLowerInvariant();
                File.Copy(src, Path.Combine(modDir, preview), overwrite: true);
            }
        }

        var subjects = work.Select(w => (w.Model.Character, w.Model.Stem))
            .Concat(pickSubjects).Distinct().ToList();
        var doc = new
        {
            schema = 1,
            name = project.Info.Name,
            version = project.Info.Version,
            author = project.Info.Author,
            description = project.Info.Description,
            preview,
            character = project.Info.Character ?? (subjects.Count == 1 ? subjects[0].Character : null),
            outfit = project.Info.Outfit,
            source_outfit = subjects.Count == 1 ? subjects[0].Stem : null,
            override_hashes = captureHashes.Concat(hides).Concat(retex.Select(r => r.Hash))
                .Concat(scopedRetex.SelectMany(s => s.Images
                    .SelectMany(i => i.Anchors.Select(a => a.Hash)).Append(s.StockHash)))
                .Concat(latches.SelectMany(l => l.WitnessIbs))
                .Concat(twinSightings.Select(t => t.Hash))
                // a ramp bind acts on three: the draw it scopes to, the map it sights the material by, and
                // the ramp whose register it takes over. All three are hashes another mod can claim too
                .Concat(stockRamps.SelectMany(b => new[] { b.IbHash, b.MaterialHash, b.RampHash }))
                .Distinct(StringComparer.Ordinal).OrderBy(h => h, StringComparer.Ordinal).ToArray(),
            game_catalog = env.CatalogVersion,
            app_version = env.AppVersion,
            shader_slots = new
            {
                catalog = slotCatalog?.CatalogId,
                game_build = slotCatalog?.GameBuild,
                stock_ps_slots = slotPlan.StockMaps,
                ramp_ps_slots = slotPlan.Ramp,
                fallback = "inherit_stock_ramp",
            },
        };
        File.WriteAllText(Path.Combine(modDir, "gf2mod.json"),
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
    }
}
