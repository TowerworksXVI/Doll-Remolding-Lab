using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Skeleton;

namespace Remold.Core.Migoto;

/// <summary>
/// Derives a Replace verb's recovery <b>pool</b> from the donor's weights: the outfit parts owning the
/// bones the donor actually uses. Which mesh gets <i>replaced</i> decides membership nowhere; it breaks a
/// tie for the anchor, and it is what <see cref="PoolCandidates"/> admits a narrow part for.
///
/// <para>Pool order is ROSTER order, because the union bone order is built first-seen over the pool
/// (<see cref="SwapCompile.BuildUnionOrder"/>) and a stable input order is what makes rebuilds
/// reproducible. The anchor (hosts convert+skin+draw) defaults to the pool part owning the most
/// donor-used bones, ties to the replaced part when it is one of them and to the LAST in roster order
/// otherwise; an explicit override wins. A donor bone owned by NO roster part fails loudly — silently
/// dropping influences would deform the donor — and so does one every owner merely TABLES, since a bone
/// no pooled part poses has no sound palette row for the donor's vertices to ride. A bone a coverage GROUP
/// certifies answers both refusals: it rides an appended palette slot of its own outside the union, written
/// at the drawing member's dispatch, so it needs no pooled tabler and no pooled poser.</para>
///
/// <para>The donor's weights decide membership, but the pool's own LOD tiers decide whether that pool is
/// POSEABLE: <see cref="CoverTierBones"/> extends it with the parts whose top LOD carries the bones those
/// tiers rig.</para>
/// </summary>
public static class PoolDerive
{
    /// <summary>One roster part's bone identity: the mesh <c>m_Name</c> and its
    /// <c>m_BoneNameHashes</c> set. Supply in roster order.
    ///
    /// <para><paramref name="Narrow"/> marks a part storing ONE influence per vertex, which
    /// <see cref="PoolCandidates"/> keeps out of every pool but its own Replace's. Such a part rides its
    /// bones at weight 1 on every vertex, so it takes union ownership of a shared bone from the part that
    /// actually poses it (<see cref="PoolMath.BuildUnion"/> ranks by summed weight).</para>
    ///
    /// <para><paramref name="Presence"/> is when the part is on screen; <see cref="PoolCandidates"/>
    /// admits it for a Replace only when it covers the replaced part's presence. The default is
    /// unconditional.</para>
    ///
    /// <para><paramref name="PosedBones"/> is the subset of <paramref name="BoneHashes"/> the part
    /// actually POSES — the hashes carrying nonzero summed vertex weight, the quantity
    /// <see cref="StreamDump.WeightedBoneHashes"/> reads off a mesh. Union membership and every
    /// table-based rule keep reading <paramref name="BoneHashes"/>; only <see cref="Derive"/>'s posed
    /// gate reads this. Null says the caller measured no weights and leaves the gate reading the table,
    /// which is the pre-measurement behaviour and NOT a safe place for a caller that can measure them: a
    /// bone tabled at zero weight is exactly what the gate exists to catch.</para>
    ///
    /// <para><paramref name="CastsShadows"/> is the part's shadow-pass participation. A part that casts
    /// keeps issuing a depth-only draw while the camera can't see it, which is what lets a recovery source
    /// survive being culled; one that does not cast issues nothing off screen, so
    /// <see cref="PoolCandidates"/> keeps it out of every pool but its own Replace's. False requires a
    /// measured Off — the default admits every part whose renderer wasn't read.</para>
    ///
    /// <para><paramref name="Visibility"/> is the game-side mechanism, if any, that can leave the part
    /// undrawn even in the scene its name and wardrobe variant put it in. Anything but
    /// <see cref="Model.VisibilityOverride.None"/> keeps it out of every pool but its own Replace's, for the
    /// same reason presence does: a source the game may have withheld poses its owned bones from a buffer
    /// nothing refreshed. The default admits every part no such list named.</para></summary>
    public sealed record PartBones(string Mesh, IReadOnlySet<uint> BoneHashes, bool Narrow = false,
        PartPresence Presence = default, IReadOnlySet<uint>? PosedBones = null, bool CastsShadows = true,
        Model.VisibilityOverride Visibility = Model.VisibilityOverride.None)
    {
        /// <summary>The bones this part poses — its whole table when the caller measured none.</summary>
        public IReadOnlySet<uint> Posed => PosedBones ?? BoneHashes;
    }

    /// <summary>A roster part the caller could NOT offer as a pool candidate, and why in the caller's own
    /// words. <paramref name="BoneHashes"/> is null when the part's bone table is unknown too, which is what
    /// keeps it from being ruled out as the owner of a bone the pool ends up missing.
    ///
    /// <para><paramref name="Presence"/> is where the part would have sat in the wardrobe, which
    /// <see cref="VariantGroups"/> reads for its two kills and nothing finer. Null says the caller didn't
    /// classify it at all: every scheme arm then reads it as possibly its own missing poser, and only the
    /// target's own arm can certify. A <see cref="PartPresence.UnknownVariant"/> standing says the wardrobe
    /// doesn't list it, and nothing forms at all. Any other classified standing kills no coverage on its
    /// own — an unmeasured piece of a worn cell co-draws beside that cell's measured posers and displaces
    /// nothing.</para></summary>
    public sealed record MissingPart(string Mesh, string Why, IReadOnlySet<uint>? BoneHashes,
        PartPresence? Presence = null);

    /// <summary>The derived pool: part mesh names in roster order, the anchor, and how many donor-used
    /// bones each pool part owns. A part pooled only to carry another part's tier bones owns none, and
    /// counts 0.</summary>
    public sealed record Result(IReadOnlyList<string> Pool, string Anchor,
        IReadOnlyDictionary<string, int> UsedBoneCounts)
    {
        /// <summary>The donor-used bones no pool part poses that the coverage group covers instead, each
        /// mapped to <see cref="CoverageGroupId"/> (see <see cref="VariantGroups"/>). These passed the posed
        /// gate on the group's certificate rather than on a pool part's own weights, so a caller that treats
        /// every pool bone alike would be treating them as owned. Empty on a derive that formed no
        /// group.</summary>
        public IReadOnlyDictionary<uint, long> GroupCovered { get; init; } = new Dictionary<uint, long>();

        /// <summary>Weighted tier rows that no eligible pool part can add to the union. Classified once
        /// here and carried to the emitter, which must consume this exact verdict rather than infer it
        /// again from a narrower view of the outfit.</summary>
        public IReadOnlyList<TierBoneVerdict> TierBoneVerdicts { get; init; } = Array.Empty<TierBoneVerdict>();
    }

    /// <summary>Why a weighted tier row may use the emitter's write-nothing scatter sentinel.</summary>
    public enum TierBoneClass
    {
        /// <summary>The tier belongs to a pool mate, whose original tier draw remains visible.</summary>
        MateTier,
        /// <summary>No readable sibling lod0 poses the bone; the tier alone re-weights that geometry.</summary>
        Lod1Only,
        /// <summary>One or more readable sibling lod0 parts pose the bone; their merged geometry is lost.</summary>
        Merged,
    }

    /// <summary>One authoritative weighted-row verdict. <paramref name="AffectedPart"/> is the Replace
    /// target, <paramref name="TierPart"/> the pool part whose <paramref name="Tier"/> asks for the row,
    /// and <paramref name="OwningParts"/> the readable sibling lod0 parts that table a MERGED bone.</summary>
    public sealed record TierBoneVerdict(string AffectedPart, string TierPart, string Tier, uint Bone,
        TierBoneClass Classification, IReadOnlyList<string> OwningParts);

    /// <summary>The outfit's one coverage group: the parts whose draws, between them, keep every
    /// <see cref="GroupBones"/> bone posed in every (wardrobe variant, scene context) state the target
    /// displays in.
    ///
    /// <para>No single member is a sound recovery source on its own — each is unworn or off-scene some of
    /// the time — but a bone certified here has, in EVERY state the target draws in, at least one member on
    /// screen posing it. Whichever member that is writes the bone's rows at its own draw, so overlapping
    /// members are just more writers of the same correct transform. <paramref name="GroupBones"/> is
    /// ascending by hash, already stripped of what the pool candidates pose themselves;
    /// <paramref name="Members"/> is every part a certifying cell holds, in roster order, each listed
    /// once.</para></summary>
    public sealed record VariantGroup(long SlotId, IReadOnlyList<PartBones> Members,
        IReadOnlyList<uint> GroupBones);

    /// <summary>The coverage group's identity in the space wardrobe slot ids share. The group certifies over
    /// variant×context cells rather than for any one wardrobe slot, so no slot's own id could name it.</summary>
    public const long CoverageGroupId = long.MaxValue;

    /// <summary>One roster part's LOD tiers as tier coverage reads them: the lod0 draw's capture hash, the
    /// bones that draw actually poses, then every renderable non-lod0 tier in the order the tier machinery
    /// reaches them. <paramref name="Lod0WeightedBones"/> is what qualifies the part to COVER a bone —
    /// union ownership is decided by summed weight, so a part listing a bone it does not pose would take
    /// the slot and leave the row unwritten.</summary>
    public sealed record PartTiers(string Lod0Hash, IReadOnlySet<uint> Lod0WeightedBones,
        IReadOnlyList<TierBones> Tiers);

    /// <summary>One LOD tier for coverage: its mesh name, the hash its capture section is keyed on, and the
    /// bones it actually poses. Weighted, not tabled: a zero-weight table entry moves no vertex of this
    /// tier, so it is no reason to extend a pool.</summary>
    public sealed record TierBones(string Mesh, string CaptureHash, IReadOnlySet<uint> WeightedBones);

    /// <summary>The roster a Replace on <paramref name="replacedPart"/> may pool over, and the parts this
    /// left out. THE one seam the candidacy rules are applied at: both pool derivation and tier coverage
    /// read the candidate set, so a part filtered here is neither pooled for another part's donor nor
    /// ranked to carry another part's tier bones. Everything admitted passes through in roster order.
    ///
    /// <para>Four per-part rules, under one subject-level rule. A subject whose parts draw independently
    /// (<paramref name="partsPoolAlone"/> — weapon-family subjects, whose parts are separate game objects
    /// with no co-draw guarantee) admits only the target, whatever the per-part rules would say. Then: a
    /// narrow part (one stored influence) pools only for its own Replace. A part
    /// pools only when its <see cref="PartPresence"/> covers the replaced part's: a recovery source
    /// that can be off screen while the replacement draws would pose its owned bones from a buffer
    /// nothing refreshed. And a part outside the shadow pass pools only for its own Replace: a culled
    /// part normally keeps issuing a depth-only draw, which is what refreshes it off screen, while one
    /// with shadow casting Off issues nothing at all there. Last, a part the game's own scene logic can
    /// withhold (<see cref="Model.VisibilityOverride"/>) pools only for its own Replace, since its name and
    /// wardrobe variant no longer settle whether it draws. The replaced part is always admitted under
    /// every rule; its own capture fires exactly when the replacement is visible.</para>
    ///
    /// <para>A part failing more than one rule reports the first that caught it, in the order above.</para>
    ///
    /// <para>The exclusions come back as <see cref="MissingPart"/> so a donor riding a bone only an
    /// excluded part owns is told which part it landed on, rather than blamed on a foreign armature.</para></summary>
    public static (IReadOnlyList<PartBones> Candidates, IReadOnlyList<MissingPart> Excluded) PoolCandidates(
        IReadOnlyList<PartBones> rosterParts, string replacedPart, bool partsPoolAlone = false)
    {
        var target = rosterParts.FirstOrDefault(p =>
            string.Equals(p.Mesh, replacedPart, StringComparison.OrdinalIgnoreCase));
        // an unlisted target admits only unconditional sources; nothing else is provably co-drawn
        var targetPresence = target?.Presence ?? PartPresence.Always;

        var candidates = new List<PartBones>();
        var excluded = new List<MissingPart>();
        foreach (var p in rosterParts)
        {
            bool isTarget = string.Equals(p.Mesh, replacedPart, StringComparison.OrdinalIgnoreCase);
            if (!isTarget && partsPoolAlone)
                excluded.Add(new MissingPart(p.Mesh,
                    "this item's parts draw independently, so only a mesh edit on that part itself "
                    + "can use it",
                    p.BoneHashes));
            else if (!isTarget && p.Narrow)
                excluded.Add(new MissingPart(p.Mesh,
                    "it stores one influence per vertex, so only a mesh edit on that part itself can use it",
                    p.BoneHashes));
            else if (!isTarget && !p.Presence.Covers(targetPresence))
                excluded.Add(new MissingPart(p.Mesh, NotCoDrawn(p.Presence, targetPresence), p.BoneHashes));
            else if (!isTarget && !p.CastsShadows)
                excluded.Add(new MissingPart(p.Mesh,
                    "it casts no shadow, so the game stops drawing it the moment it leaves the camera, "
                    + "and only a mesh edit on that part itself can use it",
                    p.BoneHashes));
            else if (!isTarget && p.Visibility != Model.VisibilityOverride.None)
                excluded.Add(new MissingPart(p.Mesh, WithheldByGame(p.Visibility), p.BoneHashes));
            else
                candidates.Add(p);
        }
        return (candidates, excluded);
    }

    /// <summary>The coverage the outfit's own alternation answers for, for a Replace on
    /// <paramref name="replacedPart"/>: at most ONE group, certifying every bone that stays posed in every
    /// (wardrobe variant, scene context) cell the target displays in. A variant or context part is no pool
    /// candidate — it is unworn or off-scene some of the time — but a bone with an on-screen poser in EVERY
    /// displayed cell is posed whatever the player wears and wherever the scene sits, and that is a recovery
    /// source the pool rules alone can't see.
    ///
    /// <para>This is capture-only metadata. It decides no pool membership, no anchor and no tier carrier;
    /// <see cref="Derive"/> reads it at the posed gate and nowhere else.</para>
    ///
    /// <para>The displayed cells are a variant axis × a context axis. The context axis is both scenes for an
    /// always-on target and the target's own scene for a context-tagged one. The variant axis comes one ARM
    /// at a time: each wardrobe slot contributes its variant list, and the target's own wardrobe standing
    /// contributes a one-variant arm of its own — the generalization of the old scene-context pair, and the
    /// only arm a schemeless roster has. A cell (v, c) is answered by any part worn and on screen whenever
    /// that cell is up: wardrobe variant v or none, scene context c or always. Every part answering must
    /// also be one a pool could lean on — not narrow, in the shadow pass, named by no visibility list, with
    /// a MEASURED posed set — and never the target itself. An arm certifies a bone only when every one of
    /// its cells holds such a poser of it; one cell short and that arm certifies nothing, because the
    /// unworn and off-scene states are exactly what the coverage answers for. The group's bones are the
    /// union over arms, its members every part a certifying arm's cells hold.</para>
    ///
    /// <para><paramref name="heldBack"/> narrows this the way it always has. A part the caller classified
    /// kills nothing on its own: pieces of one worn cell are additive, so an unmeasured piece co-draws
    /// beside a measured poser and displaces nothing — while a cell whose every poser was held back simply
    /// has none, and its arm certifies nothing on the cell rule alone. A held-back part the caller did NOT
    /// classify silences every scheme arm, since nothing says which cell it was the missing poser of; the
    /// target's own arm still stands, because its cells vary only the scene, and contexts are additive —
    /// an unmeasured extra part displaces neither scene's dress. An unreadable or empty
    /// <paramref name="schemeSlots"/> leaves scheme arms unformed the same silent way: the wardrobe is what
    /// states which parts alternate.</para>
    ///
    /// <para><paramref name="poolCandidates"/> is this Replace's <see cref="PoolCandidates"/> output, and
    /// what those parts pose is subtracted: a bone the pool already covers needs no group behind it. The
    /// group's bones come back ascending by hash, which is the order every later stage reads them in, under
    /// <see cref="CoverageGroupId"/>.</para></summary>
    public static IReadOnlyList<VariantGroup> VariantGroups(IReadOnlyList<PartBones> rosterParts,
        IReadOnlyList<Tables.PartScheme.Slot>? schemeSlots, IReadOnlyList<MissingPart> heldBack,
        IReadOnlyList<PartBones> poolCandidates, string replacedPart, bool partsPoolAlone = false)
    {
        // A subject whose parts draw independently certifies nothing across them: even the target's own
        // arm leans on sibling posers, and no sibling here is guaranteed on screen with the target.
        if (partsPoolAlone) return Array.Empty<VariantGroup>();

        // A wardrobe-shaped part the scheme doesn't list is an alternative of SOME slot with nothing
        // stating which — several wardrobe ids can share one part token, so a subject can wear an option
        // the scheme it matched never names. A group certifying a bone while such a part is on the roster
        // would certify one that nothing poses whenever that unlisted option is the worn one, so the whole
        // roster forms nothing, the same total kill an unclassified held-back part is.
        // This runs before the scheme is read at all, because a roster with NO scheme is the same hole seen
        // from the other side: PartPresence.Classify hands a modular-shaped token UnknownVariant when
        // nothing lists it, and the target's own arm below forms without a scheme.
        if (rosterParts.Any(p => p.Presence.VariantId == PartPresence.UnknownVariant)
            || heldBack.Any(m => m.Presence is { VariantId: PartPresence.UnknownVariant }))
            return Array.Empty<VariantGroup>();

        // the same read PoolCandidates takes: an unlisted target is unconditional, so nothing vouches for it
        // that isn't unconditional itself
        var target = rosterParts.FirstOrDefault(p =>
            string.Equals(p.Mesh, replacedPart, StringComparison.OrdinalIgnoreCase));
        var targetPresence = target?.Presence ?? PartPresence.Always;

        // Every rule a cell poser must pass that says nothing about WHEN the part is on screen — the cell
        // itself adds that axis, which is the whole point of a group. The posed set must be MEASURED: a
        // part falling back to its bone table would certify coverage of bones it carries at zero weight,
        // which is what the posed gate exists to refuse.
        bool Sound(PartBones p) =>
            !string.Equals(p.Mesh, replacedPart, StringComparison.OrdinalIgnoreCase)
            && !p.Narrow && p.CastsShadows && p.Visibility == Model.VisibilityOverride.None
            && p.PosedBones is not null;

        var pooled = new HashSet<uint>();
        foreach (var c in poolCandidates) pooled.UnionWith(c.Posed);
        pooled.Add(0);   // 0 = unrecoverable hash (MeshApply.Payload contract) — never a covered bone

        // The context axis the target displays across. An always-on target draws in both scenes, so every
        // arm below must answer both; a context-tagged one draws in its own scene alone.
        var needCtx = targetPresence.Context == PresenceContext.Always
            ? new[] { PresenceContext.Fight, PresenceContext.Dorm }
            : new[] { targetPresence.Context };

        // A cell (v, c)'s posers, as roster indices: worn whenever variant v is (their own variant is v or
        // none) and on screen whenever scene c shows (their own context is c or always). The roster-wide
        // UnknownVariant kill already returned, so no poser here carries that sentinel.
        List<int> Cell(long v, PresenceContext c) => Enumerable.Range(0, rosterParts.Count)
            .Where(i => Sound(rosterParts[i])
                && (rosterParts[i].Presence.VariantId == PartPresence.NoVariant
                    || rosterParts[i].Presence.VariantId == v)
                && (rosterParts[i].Presence.Context == PresenceContext.Always
                    || rosterParts[i].Presence.Context == c)).ToList();

        // The variant arms. Each wardrobe slot is one arm — except a slot listing the target's own variant,
        // whose displayed cells narrow to that variant alone, which is exactly the target's own arm below.
        // An unclassified held-back part silences every scheme arm: nothing says which cell it was the
        // missing poser of. (A part the caller DID classify kills nothing here — pieces of one worn cell
        // are additive, and a cell whose every poser was held back simply comes up empty below.)
        var arms = new List<IReadOnlyList<long>>();
        bool schemeArmsSilenced = heldBack.Any(m => m.Presence is null);
        foreach (var slot in (schemeSlots ?? Array.Empty<Tables.PartScheme.Slot>()).OrderBy(s => s.Id))
        {
            if (schemeArmsSilenced || slot.Variants.Count == 0) continue;
            if (targetPresence.VariantId != PartPresence.NoVariant
                && slot.Variants.Any(v => v.Id == targetPresence.VariantId)) continue;
            arms.Add(slot.Variants.Select(v => v.Id).ToList());
        }
        // The target's own arm: one variant — the target's, or none — across the displayed contexts. This
        // is the old scene-context pair generalized: it forms over a schemeless roster (where the
        // non-modular outfits sit), needs no wardrobe beyond the target's own standing, and a held-back
        // part never silences it — its cells vary only the scene, and contexts are additive.
        arms.Add(new[] { targetPresence.VariantId });

        var certified = new SortedSet<uint>();
        var memberIdx = new SortedSet<int>();
        foreach (var arm in arms)
        {
            HashSet<uint>? shared = null;
            var armMembers = new List<int>();
            foreach (var v in arm)
            {
                foreach (var c in needCtx)
                {
                    var posers = Cell(v, c);
                    if (posers.Count == 0) { shared = null; goto armDone; }
                    var posed = new HashSet<uint>();
                    foreach (int i in posers) posed.UnionWith(rosterParts[i].Posed);
                    armMembers.AddRange(posers);
                    if (shared is null) shared = posed;
                    else shared.IntersectWith(posed);
                }
            }
            armDone:
            if (shared is null) continue;
            shared.ExceptWith(pooled);
            // What the pool TABLES is not consulted. A group bone is compiled onto an appended palette slot
            // past the union rather than onto a union row, and the drawing member writes it, so a bone only
            // the members carry is exactly the shape this certifies — the members are its tablers.
            if (shared.Count == 0) continue;
            certified.UnionWith(shared);
            foreach (int i in armMembers) memberIdx.Add(i);
        }

        if (certified.Count == 0) return Array.Empty<VariantGroup>();
        return new[]
        {
            new VariantGroup(CoverageGroupId,
                memberIdx.Select(i => rosterParts[i]).ToList(), certified.ToList()),
        };
    }

    /// <summary>Why a part the game's own scene logic can withhold stays out, named by the mechanism that
    /// withholds it so a refusal teaches which data said so.</summary>
    static string WithheldByGame(Model.VisibilityOverride why) => why switch
    {
        Model.VisibilityOverride.CoatList =>
            "the dorm dresses it on and off separately from the scene, so only a mesh edit on that "
            + "part itself can use it",
        Model.VisibilityOverride.DormHidden =>
            "the game hides it in the dorm whatever its name says, so only a mesh edit on that part "
            + "itself can use it",
        Model.VisibilityOverride.LobbyHidden =>
            "the game hides it on the crew deck whatever its name says, so only a mesh edit on that "
            + "part itself can use it",
        Model.VisibilityOverride.TimelineNamed =>
            "a dorm scene can hide or reveal it mid-pose, so only a mesh edit on that part itself can use it",
        // Every mechanism gets a sentence that names it, so a refusal teaches which data said so. A new
        // member falling through to a catch-all would inherit the timeline wording and misname its cause,
        // which is worse than failing here — this is called only after a mechanism already fired.
        _ => throw new ArgumentOutOfRangeException(nameof(why), why,
            "no refusal sentence for this visibility mechanism"),
    };

    /// <summary>Why a part that isn't reliably co-drawn stays out, in the change list's vocabulary —
    /// named by the axis that actually failed.</summary>
    static string NotCoDrawn(PartPresence source, PartPresence target)
    {
        bool variantFails = source.VariantId != PartPresence.NoVariant
            && (source.VariantId == PartPresence.UnknownVariant || source.VariantId != target.VariantId);
        if (variantFails)
            return source.VariantId == PartPresence.UnknownVariant
                ? "its wardrobe slot isn't in the game's tables, so nothing guarantees it is on screen"
                : "it is a wardrobe option worn only some of the time";
        return source.Context == PresenceContext.Fight
            ? "it is on screen only in combat"
            : "it is on screen only in the dorm";
    }

    /// <summary>Derive the pool for <paramref name="donor"/> over <paramref name="rosterParts"/>.
    /// Throws <see cref="InvalidDataException"/> on an unweighted donor, a used bone no part owns and no
    /// group covers, a used bone every owner merely TABLES that no group covers, a donor whose every used
    /// bone a group covers (nothing joins the pool, so nothing can host the draw), an override naming a part
    /// outside the pool, or a donor using no bones at all.
    ///
    /// <para><paramref name="missingParts"/> are roster parts the caller had to hold back, which is what
    /// decides how the orphan-bone refusal reads: a foreign armature is the honest diagnosis only when the
    /// roster came through whole, so any held-back part that could own an orphan is named instead. The
    /// posed refusal reads the same list for the same reason — a bone the pool only tables may be posed by
    /// a part that was left out, and <see cref="MissingPart"/> carries a table rather than a posed set, so
    /// what it can say is that the part MIGHT be the one moving it.</para>
    ///
    /// <para><paramref name="groups"/> is the outfit's coverage group — the bones with an on-screen poser
    /// in every variant×context state the target displays in (<see cref="VariantGroups"/>). A used bone the
    /// pool doesn't pose passes the posed gate when the group covers it, and one no pool part even TABLES
    /// passes the orphan check the same way — the bone is compiled onto an appended slot outside the union,
    /// so the pool owes it neither a row nor a poser. Both come back in
    /// <see cref="Result.GroupCovered"/>; they change nothing else, since a group's members are not pool
    /// parts. Null leaves both gates reading the pool alone.</para></summary>
    public static Result Derive(MeshApply.Payload donor, IReadOnlyList<PartBones> rosterParts,
        string? anchorOverride = null, IReadOnlyList<MissingPart>? missingParts = null,
        string? replacedPart = null, IReadOnlyList<VariantGroup>? groups = null)
    {
        if (donor.JointIndices is null || donor.JointWeights is null || donor.SkinJointHashes is null)
            throw new AuthoredRefusalException(
                "the donor carries no skin. Weight it to the outfit's reference armature in Blender first");

        // the bone hashes the donor actually rides (nonzero-weight influences only)
        var used = new HashSet<uint>();
        for (int i = 0; i < donor.JointIndices.Length; i++)
            if (donor.JointWeights[i] > 0f)
                used.Add(donor.SkinJointHashes[donor.JointIndices[i]]);
        used.Remove(0);   // 0 = unrecoverable hash (MeshApply.Payload contract) — never an owner key
        if (used.Count == 0)
            throw new AuthoredRefusalException(
                "the new mesh's weights name no bone this item has");

        var pool = new List<string>();
        var pooled = new List<PartBones>();
        var counts = new Dictionary<string, int>();
        var owned = new HashSet<uint>();
        var posed = new HashSet<uint>();
        foreach (var part in rosterParts)
        {
            int n = part.BoneHashes.Count(used.Contains);
            if (n == 0) continue;
            pool.Add(part.Mesh);
            pooled.Add(part);
            counts[part.Mesh] = n;
            owned.UnionWith(part.BoneHashes);
            posed.UnionWith(part.Posed);
        }

        // The bones a coverage group certifies, each mapped to the group that certified it. Settled BEFORE
        // the orphan sweep because both gates below read exactly this set: a bone exempted from one and
        // refused by the other would be refused in words that name no tabler at all.
        var groupCovered = new Dictionary<uint, long>();
        foreach (var g in groups ?? Array.Empty<VariantGroup>())
            foreach (uint h in g.GroupBones) groupCovered.TryAdd(h, g.SlotId);

        // A used bone no pooled part TABLES has no union row, and the union is what the pool's palette is
        // built over — a weight compiled onto a row nothing lays out would land on a foreign bone. Refuse.
        // …unless a group covers it. A group bone is compiled onto an APPENDED slot past the union rather
        // than onto a union row (SwapCompile's extras), and the row is written by whichever member is on
        // screen, so the pool tabling it settles nothing: the members are the tablers, and a group member
        // tables every bone it poses. Exempted here, the bone flows on to the posed gate, which admits it on
        // the same certificate.
        var orphans = used.Where(h => !owned.Contains(h) && !groupCovered.ContainsKey(h)).ToList();
        if (orphans.Count > 0)
        {
            // a held-back part with an unknown bone table can't be ruled out, so it is always named
            var blame = (missingParts ?? Array.Empty<MissingPart>())
                .Where(m => m.BoneHashes is not { } b || orphans.Any(b.Contains)).ToList();
            throw BuildLogDiagnostics.Attach(new InvalidDataException(blame.Count == 0
                ? $"the new mesh uses {orphans.Count} bone(s) that no part of this item has. It was "
                  + "weighted against a different armature. Open this item in Blender again and "
                  + "re-weight the mesh"
                : $"the new mesh uses {orphans.Count} bone(s) that no part this mod can build with has. "
                  + "Left out: "
                  + string.Join("; ", blame.Select(m => $"'{m.Mesh}' · {m.Why}"))
                  + ". Re-weight the mesh onto the parts that are in, or remove this mesh edit"),
                $"Orphan-bone refusal: {orphans.Count} bone(s) owned by no part, first "
                + $"0x{orphans[0]:x8}.");
        }

        // Owning a bone is TABLING it; posing it is carrying weight on it, and a part may table the whole
        // skeleton while posing a fraction of it. A donor bone no pooled part poses has no sound recovery
        // anywhere in the pool: the union hands the slot to whichever part the weight argmax lands on with
        // nothing behind it, that part's operator finds the bone ill-conditioned, and the emitter ties its
        // rows rigidly to a neighbour. The donor's vertices there then ride a bone that never moves as
        // they were weighted to expect — wrong skinning behind a build-log line. Refuse instead.
        // …unless the coverage group covers it. Every variant×context state the target displays in holds
        // a poser of the bone, so whatever the player wears and wherever the scene sits, something on
        // screen is posing it — a recovery source the pool rules can't admit as a part because no single
        // poser is always the on-screen one.
        var admitted = new Dictionary<uint, long>();

        var unposed = new List<uint>();
        foreach (uint h in used.OrderBy(h => h))
        {
            if (posed.Contains(h)) continue;
            if (groupCovered.TryGetValue(h, out long slot)) admitted[h] = slot;
            else unposed.Add(h);
        }
        if (unposed.Count > 0)
        {
            // never empty: the bone is donor-used and passed the orphan check, so every part tabling it
            // took a pool slot for it
            var tablers = pooled.Where(p => p.BoneHashes.Contains(unposed[0])).Select(p => p.Mesh);
            // A held-back part that TABLES the bone may well be the one posing it, and a held-back part of
            // unknown bones can't be ruled out either. Saying the outfit doesn't move a bone the modder can
            // watch animate sends them re-weighting away from a hole the build made.
            var couldPose = (missingParts ?? Array.Empty<MissingPart>())
                .Where(m => m.BoneHashes is not { } b || b.Contains(unposed[0])).ToList();
            throw BuildLogDiagnostics.Attach(new InvalidDataException(
                $"the new mesh uses {unposed.Count} bone(s) that no part of this item moves. They are "
                + "named by " + string.Join(", ", tablers.Select(m => $"'{m}'")) + " but never moved"
                + (couldPose.Count == 0
                    ? ". Re-weight the mesh onto the bones this item moves"
                    : ". Left out: "
                      + string.Join("; ", couldPose.Select(m => $"'{m.Mesh}' · {m.Why}"))
                      + ". Re-weight the mesh onto the bones those parts move, or remove this mesh edit")),
                $"Unposed-bone refusal: {unposed.Count} bone(s) posed by no pooled part, first "
                + $"0x{unposed[0]:x8}.");
        }

        // Every used bone a group certified and none of them a roster part TABLES, so no part joined the
        // pool. There is no union to compile against and no draw to host the replacement — the anchor
        // selection below would read an empty pool. Refuse in words rather than index off the end.
        if (pool.Count == 0)
            throw new AuthoredRefusalException(
                "the new mesh uses only bones that belong to this item's other wardrobe or scene options, "
                + "so no part of it can carry the replacement. Re-weight the mesh onto the bones this "
                + "item's own parts move as well");

        string anchor;
        if (anchorOverride is not null)
        {
            if (!pool.Contains(anchorOverride, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"anchor override '{anchorOverride}' is not a pool part (pool: {string.Join(", ", pool)})");
            anchor = pool.First(p => string.Equals(p, anchorOverride, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // dominant part; ties → last in roster order
            anchor = pool[0];
            foreach (var p in pool)
                if (counts[p] >= counts[anchor]) anchor = p;
            // …except that the REPLACED part takes a tie it is in. The anchor hosts the donor's draw, so a
            // tie resolved onto a sibling sends the replacement to a mesh the author didn't pick — and where
            // the two are mutually exclusive draws it lands where nothing renders. A donor riding one bone
            // ties with every part tabling it, which is how often this decides nothing else.
            if (replacedPart is not null
                && pool.FirstOrDefault(p => string.Equals(p, replacedPart, StringComparison.OrdinalIgnoreCase))
                   is { } target
                && counts[target] == counts[anchor])
                anchor = target;
        }
        return new Result(pool, anchor, counts) { GroupCovered = admitted };
    }

    /// <summary>
    /// Extend <paramref name="derived"/> until the union palette can pose every LOD tier the pool takes
    /// on. The union bone order is built from the pool parts' lod0 bone sets alone, while each pool
    /// part's other tiers are captured and recovered against that same union, so a tier bone no pooled
    /// lod0 carries has no slot to be posed in. Roster parts whose lod0 carries the missing bones join
    /// the pool to supply them; they own no donor-used bone, so they are captured for recovery and
    /// nothing else.
    ///
    /// <para>A tier asks only for the bones it POSES. Union membership, by contrast, is the pool lod0s'
    /// whole bone TABLE — that is what <see cref="PoolMath.BuildUnion"/> lays out — so a tier bone already
    /// tabled by a pooled lod0 needs nothing here even at zero weight.</para>
    ///
    /// <para>A part may cover an asked-for bone only when it can actually write that bone's palette row at
    /// that tier's draws: its own lod0 must POSE the bone (union ownership goes to the pool part with the
    /// most summed weight, so a part merely tabling it would take the slot and leave the row unwritten),
    /// and it must have a renderable tier at the asking tier's LOD label AND variant tail — the tier chain
    /// pairs parts by that draw, and a part with nothing there falls back to a lod0 recovery whose capture
    /// a frame drawing only the tier never fires. Parts failing either test are not ranked.</para>
    ///
    /// <para>Each round covers the outstanding bones with the eligible part covering the most of them
    /// (ties → earliest in roster order), then re-reads the tiers, since a part that joins brings its own
    /// along. The extended pool comes back in ROSTER order, which is what keeps the union bone order and
    /// the emission reproducible. <paramref name="tiersOf"/> is asked about pooled parts and about the
    /// candidates weighed against them.</para>
    ///
    /// <para>Tier coverage mirrors the tier machinery's own reach: one capture per hash, every pool part's
    /// lod0 claimed before any tier, so a tier repeating a hash already captured asks the union for
    /// nothing. <paramref name="rosterParts"/> is the same <see cref="PoolCandidates"/> set the pool was
    /// derived over, in roster order — a part that can't feed palette recovery, or that this Replace may not
    /// pool, can't carry its tier bones either.</para>
    ///
    /// <para>A weighted row with no eligible carrier is classified in <see cref="Result.TierBoneVerdicts"/>
    /// for the emitter to discard. Throws <see cref="InvalidDataException"/> only when covering would take
    /// the pool past <paramref name="maxParts"/>.</para>
    /// </summary>
    public static Result CoverTierBones(Result derived, IReadOnlyList<PartBones> rosterParts,
        Func<string, PartTiers> tiersOf, int maxParts, string replacedPart,
        IReadOnlyList<PartBones> readableRoster,
        IReadOnlyDictionary<uint, string>? bonePaths = null)
    {
        var pooled = new HashSet<string>(derived.Pool, StringComparer.OrdinalIgnoreCase);
        var chosen = new SortedSet<int>(Enumerable.Range(0, rosterParts.Count)
            .Where(i => pooled.Contains(rosterParts[i].Mesh)));
        if (chosen.Count != derived.Pool.Count)
            throw new InvalidDataException(
                $"the derived pool ({string.Join(", ", derived.Pool)}) doesn't match the roster it was derived over");
        var tierBoneVerdicts = new List<TierBoneVerdict>();

        // Whether this part can supply the asked-for bone AT the asking tier's draw. The matching tier
        // must itself pose the bone: it is the capture a frame drawing at that LOD recovers the row
        // from, and a tier merely present there would leave the row unwritten exactly when it is read.
        bool CanCover(PartBones candidate, uint bone, string askingTier)
        {
            if (!candidate.BoneHashes.Contains(bone)) return false;
            var have = tiersOf(candidate.Mesh);
            if (!have.Lod0WeightedBones.Contains(bone)) return false;
            string lod = Model.MeshName.Lod(askingTier);
            string? variant = Model.MeshName.Variant(askingTier);
            return have.Tiers.Any(t =>
                string.Equals(Model.MeshName.Lod(t.Mesh), lod, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Model.MeshName.Variant(t.Mesh), variant, StringComparison.OrdinalIgnoreCase)
                && t.WeightedBones.Contains(bone));
        }

        while (true)
        {
            var carried = new HashSet<uint>();
            foreach (int i in chosen) carried.UnionWith(rosterParts[i].BoneHashes);

            var lod0Hashes = new HashSet<string>(StringComparer.Ordinal);
            foreach (int i in chosen) lod0Hashes.Add(tiersOf(rosterParts[i].Mesh).Lod0Hash);
            var tiers = new List<List<(PartBones Part, TierBones Tier)>>();
            var tiersByHash = new Dictionary<string, List<(PartBones Part, TierBones Tier)>>(StringComparer.Ordinal);
            foreach (int i in chosen)
                foreach (var t in tiersOf(rosterParts[i].Mesh).Tiers)
                {
                    if (lod0Hashes.Contains(t.CaptureHash)) continue;
                    if (!tiersByHash.TryGetValue(t.CaptureHash, out var askers))
                    {
                        askers = new List<(PartBones Part, TierBones Tier)>();
                        tiersByHash.Add(t.CaptureHash, askers);
                        tiers.Add(askers);
                    }
                    askers.Add((rosterParts[i], t));
                }

            // outstanding bones, first asker first, so the refusals name the tier that needed one
            var missingRows = new List<(PartBones Part, uint Bone, string Tier)>();
            foreach (var askers in tiers)
                foreach (var (tierPart, tier) in askers)
                    foreach (uint h in tier.WeightedBones.OrderBy(x => x))
                        if (!carried.Contains(h)) missingRows.Add((tierPart, h, tier.Mesh));
            if (missingRows.Count == 0) break;

            // Recruitment is still one question per bone. Once a carrier joins, its lod0 table adds the
            // row to the union for every tier that asks for it. The verdict carried downstream remains
            // per-row because every emitted tier must account for its own weighted off-union entries.
            var missing = new List<(uint Bone, string Tier)>();
            var seen = new HashSet<uint>();
            foreach (var row in missingRows)
                if (seen.Add(row.Bone)) missing.Add((row.Bone, row.Tier));

            int best = -1, bestCovered = 0;
            for (int i = 0; i < rosterParts.Count; i++)
            {
                if (chosen.Contains(i)) continue;
                int n = missing.Count(m => CanCover(rosterParts[i], m.Bone, m.Tier));
                if (n > bestCovered) { bestCovered = n; best = i; }   // ties → earliest in roster order
            }
            if (best < 0)
            {
                foreach (var row in missingRows)
                {
                    TierBoneClass classification;
                    IReadOnlyList<string> owners = Array.Empty<string>();
                    if (!string.Equals(row.Part.Mesh, replacedPart, StringComparison.OrdinalIgnoreCase))
                        classification = TierBoneClass.MateTier;
                    else
                    {
                        owners = readableRoster
                            .Where(p => !string.Equals(p.Mesh, row.Part.Mesh, StringComparison.OrdinalIgnoreCase)
                                && p.Posed.Contains(row.Bone))
                            .Select(p => p.Mesh)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToArray();
                        classification = owners.Count > 0 ? TierBoneClass.Merged : TierBoneClass.Lod1Only;
                    }
                    if (!tierBoneVerdicts.Any(v => v.Bone == row.Bone
                        && string.Equals(v.TierPart, row.Part.Mesh, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(v.Tier, row.Tier, StringComparison.OrdinalIgnoreCase)))
                        tierBoneVerdicts.Add(new TierBoneVerdict(replacedPart, row.Part.Mesh, row.Tier, row.Bone,
                            classification, owners));
                }
                break;
            }
            if (chosen.Count >= maxParts)
            {
                var refusalRow = missing.First(m => CanCover(rosterParts[best], m.Bone, m.Tier));
                string bone = bonePaths is not null
                    && bonePaths.TryGetValue(refusalRow.Bone, out var fullPath)
                    && BoneTable.MatchingLeaf(refusalRow.Bone, fullPath) is { } leaf
                        ? $"bone '{leaf}'"
                        : "1 bone this install's files do not name";
                string? suffix = bonePaths is not null
                    && bonePaths.TryGetValue(refusalRow.Bone, out var diagnosticPath)
                        ? BoneTable.MatchingSuffix(refusalRow.Bone, diagnosticPath)
                        : null;
                string diagnosticBone = suffix is not null
                    ? $"'{suffix}' (0x{refusalRow.Bone:x8})"
                    : $"no matching chain suffix (0x{refusalRow.Bone:x8})";
                throw BuildLogDiagnostics.Attach(new InvalidDataException(
                    $"This mesh edit can't be built because the item needs more than {maxParts} "
                    + $"part{(maxParts == 1 ? "" : "s")} at this detail level. "
                    + $"LOD '{refusalRow.Tier}' uses {bone} from '{rosterParts[best].Mesh}'. "
                    + "Remove this mesh edit"),
                    $"Pool-cap refusal: tier '{refusalRow.Tier}' uses {diagnosticBone} "
                    + $"from '{rosterParts[best].Mesh}'.");
            }
            chosen.Add(best);
        }

        if (chosen.Count == derived.Pool.Count && tierBoneVerdicts.Count == 0) return derived;
        var pool = new List<string>();
        var counts = new Dictionary<string, int>(derived.UsedBoneCounts);
        foreach (int i in chosen)
        {
            pool.Add(rosterParts[i].Mesh);
            counts.TryAdd(rosterParts[i].Mesh, 0);
        }
        return derived with
        {
            Pool = pool,
            UsedBoneCounts = counts,
            TierBoneVerdicts = tierBoneVerdicts,
        };
    }
}
