using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="PoolDerive"/> — the recovery pool from the donor's weights: roster-order pool
/// membership, which roster parts a Replace may pool at all, the dominant-part anchor (ties → the replaced
/// part, else last), the anchor override, and the loud failures (unweighted donor, orphan bones).
/// </summary>
public class PoolDeriveTests
{
    private static PoolDerive.PartBones Part(string mesh, params uint[] hashes) =>
        new(mesh, hashes.ToHashSet());

    /// <summary>A donor whose vertices ride exactly <paramref name="usedHashes"/>. The remaining influences
    /// carry weight 0 and point at joint 0 — zero-weight influences must never pull a bone into the
    /// pool.</summary>
    private static MeshApply.Payload Donor(params uint[] usedHashes)
    {
        int n = Math.Max(1, usedHashes.Length);
        var ji = new int[n * 4];
        var jw = new float[n * 4];
        for (int v = 0; v < usedHashes.Length; v++) { ji[v * 4] = v; jw[v * 4] = 1f; }
        return new MeshApply.Payload
        {
            Mesh = new UnityMesh { Name = "donor", VertexCount = n },
            JointIndices = ji, JointWeights = jw,
            SkinJointHashes = usedHashes.Length > 0 ? usedHashes : new uint[] { 1 },
        };
    }

    private static readonly IReadOnlyList<PoolDerive.PartBones> Roster = new[]
    {
        Part("face", 1, 2, 3),
        Part("body", 10, 11, 12, 13, 2),   // shares hash 2 with face (bones are shared across parts)
        Part("cloth", 20, 21),
        Part("hair", 30),
    };

    [Fact]
    public void Pool_is_the_owning_parts_in_roster_order_anchor_is_dominant()
    {
        // donor rides body(3) + cloth(2) + hair(1) bones; face untouched
        var r = PoolDerive.Derive(Donor(10, 11, 12, 20, 21, 30), Roster);
        Assert.Equal(new[] { "body", "cloth", "hair" }, r.Pool);
        Assert.Equal("body", r.Anchor);
        Assert.Equal(3, r.UsedBoneCounts["body"]);
        Assert.Equal(1, r.UsedBoneCounts["hair"]);
    }

    [Fact]
    public void Single_part_donor_derives_a_pool_of_one()
    {
        var r = PoolDerive.Derive(Donor(20, 21), Roster);
        Assert.Equal(new[] { "cloth" }, r.Pool);
        Assert.Equal("cloth", r.Anchor);
    }

    [Fact]
    public void Shared_bone_pulls_every_owning_part_and_ties_anchor_to_the_last()
    {
        // hash 2 lives in face AND body → both pool, one used bone each → tie → last in roster order
        var r = PoolDerive.Derive(Donor(2), Roster);
        Assert.Equal(new[] { "face", "body" }, r.Pool);
        Assert.Equal("body", r.Anchor);
    }

    [Fact]
    public void Zero_weight_influences_never_join_the_pool()
    {
        // Influences 1..3 point at joint 0 with weight 0; give joint 0 a hair hash and full weight to a
        // cloth bone — hair must stay out.
        var p = new MeshApply.Payload
        {
            Mesh = new UnityMesh { Name = "donor", VertexCount = 1 },
            JointIndices = new[] { 1, 0, 0, 0 },
            JointWeights = new[] { 1f, 0f, 0f, 0f },
            SkinJointHashes = new uint[] { 30, 20 },   // joint 0 = hair (weight 0), joint 1 = cloth (weight 1)
        };
        var r = PoolDerive.Derive(p, Roster);
        Assert.Equal(new[] { "cloth" }, r.Pool);
    }

    [Fact]
    public void Anchor_override_wins_but_must_be_a_pool_part()
    {
        var r = PoolDerive.Derive(Donor(10, 20, 21), Roster, anchorOverride: "body");
        Assert.Equal("body", r.Anchor);
        var e = Assert.Throws<InvalidDataException>(() => PoolDerive.Derive(Donor(10), Roster, anchorOverride: "hair"));
        Assert.Contains("not a pool part", e.Message);
    }

    [Fact]
    public void Orphan_bones_fail_loudly()
    {
        var e = Assert.Throws<InvalidDataException>(() => PoolDerive.Derive(Donor(10, 999), Roster));
        Assert.Contains("owned by no part", e.Message);
        Assert.Contains("0x000003e7", e.Message);
    }

    [Fact]
    public void Unweighted_donor_fails_loudly()
    {
        var p = MeshApply.Payload.Geometry(new UnityMesh { Name = "donor", VertexCount = 1 });
        var e = Assert.Throws<InvalidDataException>(() => PoolDerive.Derive(p, Roster));
        Assert.Contains("no skin", e.Message);
    }

    [Fact]
    public void All_unrecoverable_hashes_fail_loudly()
    {
        var p = new MeshApply.Payload
        {
            Mesh = new UnityMesh { Name = "donor", VertexCount = 1 },
            JointIndices = new[] { 0, 0, 0, 0 },
            JointWeights = new[] { 1f, 0f, 0f, 0f },
            SkinJointHashes = new uint[] { 0 },
        };
        Assert.Throws<InvalidDataException>(() => PoolDerive.Derive(p, Roster));
    }

    // ---- the replaced part: candidacy of a narrow part, and the anchor tie ------------------------

    /// <summary>The shared roster with the hair marked one-influence. It tables hash 30 alone, and hash 2
    /// is the one two canonical parts share.</summary>
    private static readonly IReadOnlyList<PoolDerive.PartBones> NarrowHairRoster = new[]
    {
        Part("face", 1, 2, 3),
        Part("body", 10, 11, 12, 13, 2),
        Part("cloth", 20, 21),
        new PoolDerive.PartBones("hair", new HashSet<uint> { 30, 11 }, Narrow: true),
    };

    [Fact]
    public void A_narrow_part_is_a_candidate_only_for_a_replace_on_itself()
    {
        var (forBody, excluded) = PoolDerive.PoolCandidates(NarrowHairRoster, "body");
        Assert.Equal(new[] { "face", "body", "cloth" }, forBody.Select(p => p.Mesh));
        Assert.Equal("hair", Assert.Single(excluded).Mesh);
        Assert.Contains("one influence per vertex", excluded[0].Why);

        var (forHair, none) = PoolDerive.PoolCandidates(NarrowHairRoster, "hair");
        Assert.Equal(new[] { "face", "body", "cloth", "hair" }, forHair.Select(p => p.Mesh));
        Assert.Empty(none);
    }

    [Fact]
    public void A_narrow_part_left_out_takes_no_bone_of_another_parts_pool()
    {
        // Hash 11 is the body's, tabled by the narrow hair too. A Replace on the body pools the body alone,
        // where under the whole roster the hair would have joined and outweighed it there.
        var donor = Donor(10, 11);
        var (candidates, excluded) = PoolDerive.PoolCandidates(NarrowHairRoster, "body");
        Assert.Equal(new[] { "body" },
            PoolDerive.Derive(donor, candidates, missingParts: excluded, replacedPart: "body").Pool);
        Assert.Equal(new[] { "body", "hair" }, PoolDerive.Derive(donor, NarrowHairRoster).Pool);
    }

    [Fact]
    public void A_bone_only_a_left_out_narrow_part_owns_names_that_part()
    {
        var (candidates, excluded) = PoolDerive.PoolCandidates(NarrowHairRoster, "body");
        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 30), candidates, missingParts: excluded, replacedPart: "body"));
        Assert.Contains("Left out of the pool: 'hair' · it stores one influence per vertex", e.Message);
        Assert.DoesNotContain("different armature", e.Message);
    }

    [Fact]
    public void A_narrow_part_is_no_tier_coverage_carrier_for_another_part()
    {
        // The body's lod1 poses hash 30, which only the narrow hair carries. Ranking it as a carrier would
        // pool it for the body's Replace by the back door, so the tier refuses instead.
        var (candidates, _) = PoolDerive.PoolCandidates(NarrowHairRoster, "body");
        var derived = PoolDerive.Derive(Donor(10, 11, 12), candidates, replacedPart: "body");
        var tiers = TiersOf(
            Draws("body", BodyPosed, Tier("body_lod1", "b1", 10, 30)),
            Draws("hair", new uint[] { 30 }, Tier("hair_lod1", "h1", 30)));

        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.CoverTierBones(derived, candidates, tiers, maxParts: 8));

        Assert.Contains("poses bone 0x0000001e that no part of this outfit can supply", e.Message);
        // the same tier over the whole roster is covered, so it is candidacy that decided this
        Assert.Equal(new[] { "body", "hair" },
            PoolDerive.CoverTierBones(PoolDerive.Derive(Donor(10, 11, 12), NarrowHairRoster),
                NarrowHairRoster, tiers, maxParts: 8).Pool);
    }

    [Fact]
    public void The_replaced_part_takes_a_tie_for_the_anchor()
    {
        // hash 2 lives in face AND body, one used bone each — the tie the roster order would give to body
        var r = PoolDerive.Derive(Donor(2), Roster, replacedPart: "face");
        Assert.Equal(new[] { "face", "body" }, r.Pool);
        Assert.Equal("face", r.Anchor);
    }

    [Fact]
    public void A_replaced_part_short_of_dominant_leaves_the_roster_rule_alone()
    {
        // cloth owns two used bones to hair's one, so the dominant part hosts the draw whoever is replaced
        var r = PoolDerive.Derive(Donor(20, 21, 30), Roster, replacedPart: "hair");
        Assert.Equal("cloth", r.Anchor);
    }

    [Fact]
    public void An_anchor_override_still_wins_over_the_replaced_part()
    {
        var r = PoolDerive.Derive(Donor(2), Roster, anchorOverride: "body", replacedPart: "face");
        Assert.Equal("body", r.Anchor);
    }

    [Fact]
    public void A_replaced_part_outside_the_pool_changes_no_anchor()
    {
        // A Replace whose target the donor's weights never reach anchors where the weights say.
        var r = PoolDerive.Derive(Donor(2), Roster, replacedPart: "hair");
        Assert.Equal("body", r.Anchor);
    }

    // ---- tier coverage: the pool the donor asks for vs the pool that can pose it -------------------

    /// <summary>One renderable tier: its mesh name, its capture hash, and the bones it POSES.</summary>
    private static PoolDerive.TierBones Tier(string mesh, string hash, params uint[] posed) =>
        new(mesh, hash, posed.ToHashSet());

    /// <summary>What one part draws: its lod0 poses <paramref name="lod0Posed"/> and it renders at
    /// <paramref name="tiers"/>. Its lod0 capture hash is its own name plus <c>_lod0</c>.</summary>
    private static (string Part, PoolDerive.PartTiers Draws) Draws(string part, uint[] lod0Posed,
        params PoolDerive.TierBones[] tiers) =>
        (part, new PoolDerive.PartTiers($"{part}_lod0", lod0Posed.ToHashSet(), tiers));

    /// <summary>The bones the shared roster's body poses at its top LOD — its whole table.</summary>
    private static readonly uint[] BodyPosed = { 10, 11, 12, 13, 2 };

    /// <summary>A tier lookup over <paramref name="map"/>. A part with no entry poses nothing at its lod0
    /// and renders at no other tier, so it can neither ask for a cover part nor be one — every part a case
    /// wants in either role states what it draws.</summary>
    private static Func<string, PoolDerive.PartTiers> TiersOf(
        params (string Part, PoolDerive.PartTiers Draws)[] map)
    {
        var byPart = map.ToDictionary(m => m.Part, m => m.Draws, StringComparer.OrdinalIgnoreCase);
        return part => byPart.TryGetValue(part, out var t) ? t
            : new PoolDerive.PartTiers($"{part}_lod0", new HashSet<uint>(),
                Array.Empty<PoolDerive.TierBones>());
    }

    private static PoolDerive.Result Cover(PoolDerive.Result derived,
        Func<string, PoolDerive.PartTiers> tiers, int maxParts = 8) =>
        PoolDerive.CoverTierBones(derived, Roster, tiers, maxParts);

    [Fact]
    public void A_pool_whose_tiers_the_union_already_covers_is_left_alone()
    {
        var derived = PoolDerive.Derive(Donor(10, 11, 12), Roster);
        var covered = Cover(derived, TiersOf(Draws("body", BodyPosed, Tier("body_lod1", "b1", 10, 13))));
        Assert.Same(derived, covered);
    }

    [Fact]
    public void A_tier_bone_no_pooled_top_lod_carries_pulls_its_carrier_in_for_recovery_only()
    {
        // the body's lod1 poses a cloth bone; nothing in a body-only pool can pose it
        var derived = PoolDerive.Derive(Donor(10, 11, 12), Roster);
        Assert.Equal(new[] { "body" }, derived.Pool);

        var covered = Cover(derived, TiersOf(
            Draws("body", BodyPosed, Tier("body_lod1", "b1", 10, 20)),
            Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 20, 21))));

        Assert.Equal(new[] { "body", "cloth" }, covered.Pool);
        Assert.Equal("body", covered.Anchor);          // the donor's dominant part still hosts the draw
        Assert.Equal(3, covered.UsedBoneCounts["body"]);
        Assert.Equal(0, covered.UsedBoneCounts["cloth"]);   // it owns no donor-used bone
    }

    [Fact]
    public void A_tier_bone_its_own_draw_does_not_pose_asks_for_nothing()
    {
        // Bone 20 sits in the body lod1's bone TABLE and moves none of its vertices. A pool slot for it
        // would pose nothing, and buying one costs a capture, an operator and a cb slot.
        var derived = PoolDerive.Derive(Donor(10, 11, 12), Roster);
        var covered = Cover(derived, TiersOf(
            Draws("body", BodyPosed, Tier("body_lod1", "b1", 10)),
            Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 20))));
        Assert.Same(derived, covered);
    }

    [Fact]
    public void A_cover_part_joins_at_its_roster_position()
    {
        // The union bone order is first-seen over the pool, so a cover part inserted anywhere but its
        // roster slot would reorder the palette for the same inputs.
        var derived = PoolDerive.Derive(Donor(20, 21), Roster);
        var covered = Cover(derived, TiersOf(
            Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 20, 1)),
            Draws("face", new uint[] { 1, 2, 3 }, Tier("face_lod1", "f1", 1))));
        Assert.Equal(new[] { "face", "cloth" }, covered.Pool);
    }

    [Fact]
    public void A_cover_parts_own_tiers_are_covered_in_turn()
    {
        // Joining brings a part's whole tier chain with it, so coverage is not one pass.
        var covered = Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster), TiersOf(
            Draws("body", BodyPosed, Tier("body_lod1", "b1", 20)),
            Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 20, 30)),
            Draws("hair", new uint[] { 30 }, Tier("hair_lod1", "h1", 30))));
        Assert.Equal(new[] { "body", "cloth", "hair" }, covered.Pool);
    }

    [Fact]
    public void Coverage_takes_the_fewest_parts_that_carry_the_missing_bones()
    {
        // One part covering three beats two parts covering three between them: every pool part costs a
        // capture, an operator and one of the eight cb slots.
        var roster = new[]
        {
            Part("face", 1, 2, 3),
            Part("body", 10, 11, 12),
            Part("cloth", 20, 21),
            Part("hair", 30),
            Part("sash", 20, 21, 30),
        };
        var derived = PoolDerive.Derive(Donor(10, 11, 12), roster);
        var covered = PoolDerive.CoverTierBones(derived, roster, TiersOf(
            Draws("body", new uint[] { 10, 11, 12 }, Tier("body_lod1", "b1", 20, 21, 30)),
            Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 20, 21)),
            Draws("hair", new uint[] { 30 }, Tier("hair_lod1", "h1", 30)),
            Draws("sash", new uint[] { 20, 21, 30 }, Tier("sash_lod1", "s1", 20, 21, 30))), maxParts: 8);
        Assert.Equal(new[] { "body", "sash" }, covered.Pool);
    }

    [Fact]
    public void Equal_cover_ties_break_on_roster_order()
    {
        // Two parts each covering one bone: the pick has to be the same on every rebuild, or the emitted
        // union order moves under identical inputs.
        var derived = PoolDerive.Derive(Donor(10, 11, 12), Roster);
        var tiers = TiersOf(
            Draws("body", BodyPosed, Tier("body_lod1", "b1", 20, 30)),
            Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 20)),
            Draws("hair", new uint[] { 30 }, Tier("hair_lod1", "h1", 30)));
        var first = Cover(derived, tiers);
        Assert.Equal(new[] { "body", "cloth", "hair" }, first.Pool);
        Assert.Equal(first.Pool, Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster), tiers).Pool);
    }

    [Fact]
    public void A_tier_riding_a_hash_already_captured_asks_the_union_for_nothing()
    {
        // One capture serves one hash, lod0 first: a tier whose draw is already captured never reaches
        // the tier machinery, so its bone table is not the pool's problem.
        var derived = PoolDerive.Derive(Donor(2), Roster);
        Assert.Equal(new[] { "face", "body" }, derived.Pool);
        var covered = Cover(derived, TiersOf(
            Draws("face", new uint[] { 1, 2, 3 }, Tier("face_lod1", "body_lod0", 20))));
        Assert.Same(derived, covered);
    }

    [Fact]
    public void A_part_that_only_tables_the_missing_bone_is_no_carrier()
    {
        // Union ownership goes to the pooled part with the most summed weight on a bone. A part that
        // tables the bone and poses none of it takes the slot and leaves the row unwritten, which is a
        // silent identity pose at that tier — refuse instead.
        var e = Assert.Throws<InvalidDataException>(() => Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster),
            TiersOf(Draws("body", BodyPosed, Tier("body_lod1", "b1", 20)),
                    Draws("cloth", new uint[] { 21 }, Tier("cloth_lod1", "c1")))));
        Assert.Contains("body_lod1", e.Message);
        Assert.Contains("0x00000014", e.Message);
    }

    [Fact]
    public void A_part_that_does_not_render_at_the_asking_tier_is_no_carrier()
    {
        // The tier chain pairs pool parts by LOD label. A part with nothing at the asking label falls back
        // to its lod0 recovery, whose capture never fires in a frame that draws only the far tier.
        var e = Assert.Throws<InvalidDataException>(() => Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster),
            TiersOf(Draws("body", BodyPosed, Tier("body_lod1", "b1", 20)),
                    Draws("cloth", new uint[] { 20, 21 }))));
        Assert.Contains("body_lod1", e.Message);
        Assert.Contains("0x00000014", e.Message);
    }

    [Fact]
    public void A_part_of_another_outfit_state_is_no_carrier()
    {
        // _Dorm and _Fight are distinct garments, not detail levels: the Dorm cloth never draws in the
        // frames the plain lod1 draws in, so its capture can't feed that tier's recovery.
        var roster = new[] { Part("body_lod0", 10, 11, 12), Part("cloth_lod0_Dorm", 20, 21) };
        var e = Assert.Throws<InvalidDataException>(() => PoolDerive.CoverTierBones(
            PoolDerive.Derive(Donor(10, 11, 12), roster), roster, TiersOf(
                Draws("body_lod0", new uint[] { 10, 11, 12 }, Tier("body_lod1", "b1", 20)),
                Draws("cloth_lod0_Dorm", new uint[] { 20, 21 }, Tier("cloth_lod1_Dorm", "c1", 20))),
            maxParts: 8));
        Assert.Contains("body_lod1", e.Message);
    }

    [Fact]
    public void A_part_of_the_same_outfit_state_covers()
    {
        // The same shape one variant tail along: the Dorm cloth does draw with the Dorm body's far tier.
        var roster = new[] { Part("body_lod0_Dorm", 10, 11, 12), Part("cloth_lod0_Dorm", 20, 21) };
        var covered = PoolDerive.CoverTierBones(
            PoolDerive.Derive(Donor(10, 11, 12), roster), roster, TiersOf(
                Draws("body_lod0_Dorm", new uint[] { 10, 11, 12 }, Tier("body_lod1_Dorm", "b1", 20)),
                Draws("cloth_lod0_Dorm", new uint[] { 20, 21 }, Tier("cloth_lod1_Dorm", "c1", 20))),
            maxParts: 8);
        Assert.Equal(new[] { "body_lod0_Dorm", "cloth_lod0_Dorm" }, covered.Pool);
    }

    [Fact]
    public void A_tier_bone_no_poolable_part_carries_refuses_naming_the_tier_and_the_bone()
    {
        var e = Assert.Throws<InvalidDataException>(() => Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster),
            TiersOf(Draws("body", BodyPosed, Tier("body_lod1", "b1", 999)))));
        Assert.Contains("body_lod1", e.Message);
        Assert.Contains("0x000003e7", e.Message);
        Assert.Contains("can supply", e.Message);
    }

    [Fact]
    public void A_carrier_whose_matching_tier_does_not_pose_the_bone_refuses()
    {
        // The cloth has a lod1 of the right label and variant, but that tier does not pose bone 20 — the
        // draw a far frame recovers the row from would leave it unwritten exactly when it is read.
        var e = Assert.Throws<InvalidDataException>(() => Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster),
            TiersOf(
                Draws("body", BodyPosed, Tier("body_lod1", "b1", 10, 20)),
                Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 21)))));
        Assert.Contains("body_lod1", e.Message);
        Assert.Contains("can supply", e.Message);
    }

    [Fact]
    public void Covering_past_the_pool_cap_refuses_rather_than_shipping_an_unposeable_tier()
    {
        var e = Assert.Throws<InvalidDataException>(() => Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster),
            TiersOf(Draws("body", BodyPosed, Tier("body_lod1", "b1", 20, 30)),
                    Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 20)),
                    Draws("hair", new uint[] { 30 }, Tier("hair_lod1", "h1", 30))), maxParts: 2));
        Assert.Contains("more than 2 pooled parts", e.Message);
        Assert.Contains("body_lod1", e.Message);
    }
}
