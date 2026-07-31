using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Mesh;

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
/// dropping influences would deform the donor.</para>
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
    /// actually poses it (<see cref="PoolMath.BuildUnion"/> ranks by summed weight) — and these are the
    /// conditionally-drawn parts, whose captures don't fire in the frames they aren't drawn.</para></summary>
    public sealed record PartBones(string Mesh, IReadOnlySet<uint> BoneHashes, bool Narrow = false);

    /// <summary>A roster part the caller could NOT offer as a pool candidate, and why in the caller's own
    /// words. <paramref name="BoneHashes"/> is null when the part's bone table is unknown too, which is what
    /// keeps it from being ruled out as the owner of a bone the pool ends up missing.</summary>
    public sealed record MissingPart(string Mesh, string Why, IReadOnlySet<uint>? BoneHashes);

    /// <summary>The derived pool: part mesh names in roster order, the anchor, and how many donor-used
    /// bones each pool part owns. A part pooled only to carry another part's tier bones owns none, and
    /// counts 0.</summary>
    public sealed record Result(IReadOnlyList<string> Pool, string Anchor,
        IReadOnlyDictionary<string, int> UsedBoneCounts);

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
    /// left out. THE one seam the narrow rule is applied at: both pool derivation and tier coverage read the
    /// candidate set, so a part filtered here is neither pooled for another part's donor nor ranked to carry
    /// another part's tier bones. Everything canonical passes through in roster order.
    ///
    /// <para>The exclusions come back as <see cref="MissingPart"/> so a donor riding a bone only a narrow
    /// part owns is told which part it landed on, rather than blamed on a foreign armature.</para></summary>
    public static (IReadOnlyList<PartBones> Candidates, IReadOnlyList<MissingPart> Excluded) PoolCandidates(
        IReadOnlyList<PartBones> rosterParts, string replacedPart)
    {
        var candidates = new List<PartBones>();
        var excluded = new List<MissingPart>();
        foreach (var p in rosterParts)
            if (!p.Narrow || string.Equals(p.Mesh, replacedPart, StringComparison.OrdinalIgnoreCase))
                candidates.Add(p);
            else
                excluded.Add(new MissingPart(p.Mesh,
                    "it stores one influence per vertex, so only a Replace on that part itself pools it",
                    p.BoneHashes));
        return (candidates, excluded);
    }

    /// <summary>Derive the pool for <paramref name="donor"/> over <paramref name="rosterParts"/>.
    /// Throws <see cref="InvalidDataException"/> on an unweighted donor, a used bone no part owns, an
    /// override naming a part outside the pool, or a donor using no bones at all.
    ///
    /// <para><paramref name="missingParts"/> are roster parts the caller had to hold back, which is what
    /// decides how the orphan-bone refusal reads: a foreign armature is the honest diagnosis only when the
    /// roster came through whole, so any held-back part that could own an orphan is named instead.</para></summary>
    public static Result Derive(MeshApply.Payload donor, IReadOnlyList<PartBones> rosterParts,
        string? anchorOverride = null, IReadOnlyList<MissingPart>? missingParts = null,
        string? replacedPart = null)
    {
        if (donor.JointIndices is null || donor.JointWeights is null || donor.SkinJointHashes is null)
            throw new InvalidDataException(
                "the donor carries no skin. Weight it to the outfit's reference armature in Blender first");

        // the bone hashes the donor actually rides (nonzero-weight influences only)
        var used = new HashSet<uint>();
        for (int i = 0; i < donor.JointIndices.Length; i++)
            if (donor.JointWeights[i] > 0f)
                used.Add(donor.SkinJointHashes[donor.JointIndices[i]]);
        used.Remove(0);   // 0 = unrecoverable hash (MeshApply.Payload contract) — never an owner key
        if (used.Count == 0)
            throw new InvalidDataException("the donor's skin uses no recognizable bones (all hashes unrecoverable)");

        var pool = new List<string>();
        var counts = new Dictionary<string, int>();
        var owned = new HashSet<uint>();
        foreach (var part in rosterParts)
        {
            int n = part.BoneHashes.Count(used.Contains);
            if (n == 0) continue;
            pool.Add(part.Mesh);
            counts[part.Mesh] = n;
            owned.UnionWith(part.BoneHashes);
        }

        var orphans = used.Where(h => !owned.Contains(h)).ToList();
        if (orphans.Count > 0)
        {
            // a held-back part with an unknown bone table can't be ruled out, so it is always named
            var blame = (missingParts ?? Array.Empty<MissingPart>())
                .Where(m => m.BoneHashes is not { } b || orphans.Any(b.Contains)).ToList();
            throw new InvalidDataException(blame.Count == 0
                ? $"the donor rides {orphans.Count} bone(s) owned by no part of this outfit " +
                  $"(first: 0x{orphans[0]:x8}). It was weighted against a different armature; " +
                  "re-export this outfit's reference and re-weight"
                : $"the donor rides {orphans.Count} bone(s) owned by no pooled part of this outfit " +
                  $"(first: 0x{orphans[0]:x8}). Left out of the pool: " +
                  string.Join("; ", blame.Select(m => $"'{m.Mesh}' · {m.Why}")) +
                  ". Re-weight the donor onto the pooled parts, or drop this Replace");
        }

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
        return new Result(pool, anchor, counts);
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
    /// <para>Throws <see cref="InvalidDataException"/> when a missing bone has no eligible carrier, or
    /// when covering would take the pool past <paramref name="maxParts"/>.</para>
    /// </summary>
    public static Result CoverTierBones(Result derived, IReadOnlyList<PartBones> rosterParts,
        Func<string, PartTiers> tiersOf, int maxParts)
    {
        var pooled = new HashSet<string>(derived.Pool, StringComparer.OrdinalIgnoreCase);
        var chosen = new SortedSet<int>(Enumerable.Range(0, rosterParts.Count)
            .Where(i => pooled.Contains(rosterParts[i].Mesh)));
        if (chosen.Count != derived.Pool.Count)
            throw new InvalidDataException(
                $"the derived pool ({string.Join(", ", derived.Pool)}) doesn't match the roster it was derived over");

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

            var captured = new HashSet<string>(StringComparer.Ordinal);
            foreach (int i in chosen) captured.Add(tiersOf(rosterParts[i].Mesh).Lod0Hash);
            var tiers = new List<TierBones>();
            foreach (int i in chosen)
                foreach (var t in tiersOf(rosterParts[i].Mesh).Tiers)
                    if (captured.Add(t.CaptureHash)) tiers.Add(t);

            // outstanding bones, first asker first, so the refusals name the tier that needed one
            var missing = new List<(uint Bone, string Tier)>();
            var seen = new HashSet<uint>();
            foreach (var t in tiers)
                foreach (uint h in t.WeightedBones.OrderBy(x => x))
                    if (!carried.Contains(h) && seen.Add(h)) missing.Add((h, t.Mesh));
            if (missing.Count == 0) break;

            int best = -1, bestCovered = 0;
            for (int i = 0; i < rosterParts.Count; i++)
            {
                if (chosen.Contains(i)) continue;
                int n = missing.Count(m => CanCover(rosterParts[i], m.Bone, m.Tier));
                if (n > bestCovered) { bestCovered = n; best = i; }   // ties → earliest in roster order
            }
            if (best < 0)
                throw new InvalidDataException(
                    $"tier '{missing[0].Tier}' poses bone 0x{missing[0].Bone:x8} that no part of this outfit "
                    + "can supply at this detail level. Drop this Replace");
            if (chosen.Count >= maxParts)
                throw new InvalidDataException(
                    $"posing this outfit's LOD tiers needs more than {maxParts} pooled parts, the most a build "
                    + $"can bind. Tier '{missing[0].Tier}' rigs bone 0x{missing[0].Bone:x8}, carried only by parts "
                    + "the pool has no room for. Drop this Replace");
            chosen.Add(best);
        }

        if (chosen.Count == derived.Pool.Count) return derived;
        var pool = new List<string>();
        var counts = new Dictionary<string, int>(derived.UsedBoneCounts);
        foreach (int i in chosen)
        {
            pool.Add(rosterParts[i].Mesh);
            counts.TryAdd(rosterParts[i].Mesh, 0);
        }
        return derived with { Pool = pool, UsedBoneCounts = counts };
    }
}
