using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Skeleton;
using Xunit;
using static Remold.Core.Tests.Support.PoolFixtures;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="PoolDerive"/> — the recovery pool from the donor's weights: roster-order pool
/// membership, which roster parts a Replace may pool at all, the dominant-part anchor (ties → the replaced
/// part, else last), the anchor override, and the loud failures (unweighted donor, orphan bones).
/// </summary>
public class PoolDeriveTests
{
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
        Assert.Contains("that no part of this item has", e.Message);
        Assert.Contains(Remold.Core.Migoto.BuildLogDiagnostics.From(e),
            d => d.Contains("0x000003e7", StringComparison.Ordinal));
    }

    [Fact]
    public void Unweighted_donor_fails_loudly()
    {
        var p = MeshApply.Payload.Geometry(new UnityMesh { Name = "donor", VertexCount = 1 });
        var e = Assert.Throws<AuthoredRefusalException>(() => PoolDerive.Derive(p, Roster));
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
        Assert.Throws<AuthoredRefusalException>(() => PoolDerive.Derive(p, Roster));
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
    public void An_isolated_subjects_parts_admit_only_the_target()
    {
        // The weapon-family rule: parts are separate game objects with no co-draw guarantee, so a
        // Replace on any of them pools that part alone whatever the per-part rules would admit.
        var (candidates, excluded) = PoolDerive.PoolCandidates(NarrowHairRoster, "body",
            partsPoolAlone: true);

        Assert.Equal(new[] { "body" }, candidates.Select(p => p.Mesh));
        Assert.Equal(new[] { "face", "cloth", "hair" }, excluded.Select(m => m.Mesh));
        Assert.All(excluded, m => Assert.Contains("draw independently", m.Why));
    }

    [Fact]
    public void An_isolated_subject_forms_no_coverage_group()
    {
        // Even the target's own arm would lean on sibling posers, and no sibling of an isolated
        // subject is guaranteed on screen with the target.
        var (candidates, _) = PoolDerive.PoolCandidates(NarrowHairRoster, "body", partsPoolAlone: true);

        Assert.Empty(PoolDerive.VariantGroups(NarrowHairRoster, schemeSlots: null,
            System.Array.Empty<PoolDerive.MissingPart>(), candidates, "body", partsPoolAlone: true));
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
        Assert.Contains("Left out: 'hair' · it stores one influence per vertex", e.Message);
        Assert.DoesNotContain("different armature", e.Message);
    }

    [Fact]
    public void A_narrow_part_is_no_tier_coverage_carrier_for_another_part()
    {
        // The body's lod1 poses hash 30, which only the narrow hair carries. Ranking it as a carrier would
        // pool it for the body's Replace by the back door, so the row is classified MERGED instead.
        var (candidates, _) = PoolDerive.PoolCandidates(NarrowHairRoster, "body");
        var derived = PoolDerive.Derive(Donor(10, 11, 12), candidates, replacedPart: "body");
        var tiers = TiersOf(
            Draws("body", BodyPosed, Tier("body_lod1", "b1", 10, 30)),
            Draws("hair", new uint[] { 30 }, Tier("hair_lod1", "h1", 30)));

        var classified = PoolDerive.CoverTierBones(derived, candidates, tiers, maxParts: 8,
            replacedPart: "body", readableRoster: NarrowHairRoster);
        AssertTierVerdict(classified, PoolDerive.TierBoneClass.Merged, 30, "body_lod1", "hair");
        // the same tier over the whole roster is covered, so it is candidacy that decided this
        Assert.Equal(new[] { "body", "hair" },
            PoolDerive.CoverTierBones(PoolDerive.Derive(Donor(10, 11, 12), NarrowHairRoster),
                NarrowHairRoster, tiers, maxParts: 8, replacedPart: "body",
                readableRoster: NarrowHairRoster).Pool);
    }

    // ---- candidacy of a part outside the shadow pass ----------------------------------------------

    /// <summary>The shared roster with the hair's renderer marked shadow-casting Off. It tables hash 30
    /// alone, and hash 11 is the one it shares with the body.</summary>
    private static readonly IReadOnlyList<PoolDerive.PartBones> ShadowOffHairRoster = new[]
    {
        Part("face", 1, 2, 3),
        Part("body", 10, 11, 12, 13, 2),
        Part("cloth", 20, 21),
        new PoolDerive.PartBones("hair", new HashSet<uint> { 30, 11 }, CastsShadows: false),
    };

    [Fact]
    public void A_shadow_off_part_is_a_candidate_only_for_a_replace_on_itself()
    {
        var (forBody, excluded) = PoolDerive.PoolCandidates(ShadowOffHairRoster, "body");
        Assert.Equal(new[] { "face", "body", "cloth" }, forBody.Select(p => p.Mesh));
        Assert.Equal("hair", Assert.Single(excluded).Mesh);
        Assert.Contains("casts no shadow", excluded[0].Why);

        // the target is always admitted: its own capture fires exactly when the replacement is visible
        var (forHair, none) = PoolDerive.PoolCandidates(ShadowOffHairRoster, "hair");
        Assert.Equal(new[] { "face", "body", "cloth", "hair" }, forHair.Select(p => p.Mesh));
        Assert.Empty(none);
    }

    [Fact]
    public void A_shadow_off_part_left_out_takes_no_bone_of_another_parts_pool()
    {
        // Hash 11 is the body's, tabled by the shadow-off hair too. A Replace on the body pools the body
        // alone, where under the whole roster the hair would have joined it.
        var donor = Donor(10, 11);
        var (candidates, excluded) = PoolDerive.PoolCandidates(ShadowOffHairRoster, "body");
        Assert.Equal(new[] { "body" },
            PoolDerive.Derive(donor, candidates, missingParts: excluded, replacedPart: "body").Pool);
        Assert.Equal(new[] { "body", "hair" }, PoolDerive.Derive(donor, ShadowOffHairRoster).Pool);
    }

    [Fact]
    public void A_bone_only_a_left_out_shadow_off_part_owns_names_that_part()
    {
        // The user-visible payoff: the refusal names the part AND why it was held back.
        var (candidates, excluded) = PoolDerive.PoolCandidates(ShadowOffHairRoster, "body");
        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 30), candidates, missingParts: excluded, replacedPart: "body"));
        Assert.Contains("Left out: 'hair' · it casts no shadow", e.Message);
        Assert.DoesNotContain("different armature", e.Message);
    }

    [Fact]
    public void A_shadow_off_part_is_no_tier_coverage_carrier_for_another_part()
    {
        // Candidacy is the ONE seam: tier coverage reads the same set, so the shadow-off hair can't be
        // recruited to carry the body tier's bone by the back door.
        var (candidates, _) = PoolDerive.PoolCandidates(ShadowOffHairRoster, "body");
        var derived = PoolDerive.Derive(Donor(10, 11, 12), candidates, replacedPart: "body");
        var tiers = TiersOf(
            Draws("body", BodyPosed, Tier("body_lod1", "b1", 10, 30)),
            Draws("hair", new uint[] { 30 }, Tier("hair_lod1", "h1", 30)));

        var classified = PoolDerive.CoverTierBones(derived, candidates, tiers, maxParts: 8,
            replacedPart: "body", readableRoster: ShadowOffHairRoster);
        AssertTierVerdict(classified, PoolDerive.TierBoneClass.Merged, 30, "body_lod1", "hair");
        // the same tier over the whole roster is covered, so it is candidacy that decided this
        Assert.Equal(new[] { "body", "hair" },
            PoolDerive.CoverTierBones(PoolDerive.Derive(Donor(10, 11, 12), ShadowOffHairRoster),
                ShadowOffHairRoster, tiers, maxParts: 8, replacedPart: "body",
                readableRoster: ShadowOffHairRoster).Pool);
    }

    [Fact]
    public void A_part_failing_two_rules_reports_the_earlier_one()
    {
        // Narrow AND shadow-off: the narrow rule runs first, so that is the reason the modder is given.
        var roster = new[]
        {
            Part("body", 10, 11),
            new PoolDerive.PartBones("hair", new HashSet<uint> { 30 }, Narrow: true, CastsShadows: false),
        };
        var (_, excluded) = PoolDerive.PoolCandidates(roster, "body");
        Assert.Contains("one influence per vertex", Assert.Single(excluded).Why);
    }

    // ---- candidacy of a part the game's own scene logic can withhold ------------------------------

    /// <summary>The shared roster with the coat marked by the dorm context component's coat list. It
    /// tables hash 30 alone, and hash 11 is the one it shares with the body.</summary>
    private static IReadOnlyList<PoolDerive.PartBones> WithheldCoatRoster(VisibilityOverride why) => new[]
    {
        Part("face", 1, 2, 3),
        Part("body", 10, 11, 12, 13, 2),
        Part("cloth", 20, 21),
        new PoolDerive.PartBones("coat", new HashSet<uint> { 30, 11 }, Visibility: why),
    };

    [Theory]
    [InlineData(VisibilityOverride.CoatList, "dresses it on and off separately from the scene")]
    [InlineData(VisibilityOverride.DormHidden, "hides it in the dorm whatever its name says")]
    [InlineData(VisibilityOverride.LobbyHidden, "hides it on the crew deck whatever its name says")]
    [InlineData(VisibilityOverride.TimelineNamed, "a dorm scene can hide or reveal it mid-pose")]
    public void A_withheld_part_is_a_candidate_only_for_a_replace_on_itself(
        VisibilityOverride why, string expectedReason)
    {
        var roster = WithheldCoatRoster(why);
        var (forBody, excluded) = PoolDerive.PoolCandidates(roster, "body");
        Assert.Equal(new[] { "face", "body", "cloth" }, forBody.Select(p => p.Mesh));
        Assert.Equal("coat", Assert.Single(excluded).Mesh);
        // each mechanism keeps its own sentence, so a refusal teaches which of the game's data said so
        Assert.Contains(expectedReason, excluded[0].Why);

        // the target is always admitted: its own capture fires exactly when the replacement is visible
        var (forCoat, none) = PoolDerive.PoolCandidates(roster, "coat");
        Assert.Equal(new[] { "face", "body", "cloth", "coat" }, forCoat.Select(p => p.Mesh));
        Assert.Empty(none);
    }

    [Fact]
    public void A_withheld_part_left_out_takes_no_bone_of_another_parts_pool()
    {
        // Hash 11 is the body's, tabled by the withheld coat too. A Replace on the body pools the body
        // alone, where under the whole roster the coat would have joined it.
        var roster = WithheldCoatRoster(VisibilityOverride.CoatList);
        var donor = Donor(10, 11);
        var (candidates, excluded) = PoolDerive.PoolCandidates(roster, "body");
        Assert.Equal(new[] { "body" },
            PoolDerive.Derive(donor, candidates, missingParts: excluded, replacedPart: "body").Pool);
        Assert.Equal(new[] { "body", "coat" }, PoolDerive.Derive(donor, roster).Pool);
    }

    [Fact]
    public void A_bone_only_a_left_out_withheld_part_owns_names_that_part()
    {
        // The user-visible payoff: the refusal names the part AND why it was held back.
        var (candidates, excluded) =
            PoolDerive.PoolCandidates(WithheldCoatRoster(VisibilityOverride.LobbyHidden), "body");
        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 30), candidates, missingParts: excluded, replacedPart: "body"));
        Assert.Contains("Left out: 'coat' · the game hides it on the crew deck", e.Message);
        Assert.DoesNotContain("different armature", e.Message);
    }

    [Fact]
    public void A_withheld_part_is_no_tier_coverage_carrier_for_another_part()
    {
        // Candidacy is the ONE seam: tier coverage reads the same set, so the withheld coat can't be
        // recruited to carry the body tier's bone by the back door.
        var roster = WithheldCoatRoster(VisibilityOverride.CoatList);
        var (candidates, _) = PoolDerive.PoolCandidates(roster, "body");
        var derived = PoolDerive.Derive(Donor(10, 11, 12), candidates, replacedPart: "body");
        var tiers = TiersOf(
            Draws("body", BodyPosed, Tier("body_lod1", "b1", 10, 30)),
            Draws("coat", new uint[] { 30 }, Tier("coat_lod1", "c1", 30)));

        var classified = PoolDerive.CoverTierBones(derived, candidates, tiers, maxParts: 8,
            replacedPart: "body", readableRoster: roster);
        AssertTierVerdict(classified, PoolDerive.TierBoneClass.Merged, 30, "body_lod1", "coat");
        // the same tier over the whole roster is covered, so it is candidacy that decided this
        Assert.Equal(new[] { "body", "coat" },
            PoolDerive.CoverTierBones(PoolDerive.Derive(Donor(10, 11, 12), roster),
                roster, tiers, maxParts: 8, replacedPart: "body", readableRoster: roster).Pool);
    }

    [Fact]
    public void A_part_failing_the_shadow_rule_and_the_visibility_rule_reports_the_shadow_one()
    {
        // The visibility rule runs LAST, so an earlier rule that also catches the part keeps its say.
        var roster = new[]
        {
            Part("body", 10, 11),
            new PoolDerive.PartBones("coat", new HashSet<uint> { 30 },
                CastsShadows: false, Visibility: VisibilityOverride.CoatList),
        };
        var (_, excluded) = PoolDerive.PoolCandidates(roster, "body");
        Assert.Contains("casts no shadow", Assert.Single(excluded).Why);
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
        PoolDerive.CoverTierBones(derived, Roster, tiers, maxParts,
            replacedPart: "body", readableRoster: Roster);

    private static void AssertTierVerdict(PoolDerive.Result result, PoolDerive.TierBoneClass classification,
        uint bone, string tier, params string[] owners)
    {
        var verdict = Assert.Single(result.TierBoneVerdicts);
        Assert.Equal(classification, verdict.Classification);
        Assert.Equal(bone, verdict.Bone);
        Assert.Equal(tier, verdict.Tier);
        Assert.Equal(owners, verdict.OwningParts);
    }

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
            Draws("sash", new uint[] { 20, 21, 30 }, Tier("sash_lod1", "s1", 20, 21, 30))), maxParts: 8,
            replacedPart: "body", readableRoster: roster);
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
        // silent identity pose at that tier. It neither covers the row nor counts as merged geometry.
        var roster = Roster.Select(p => p.Mesh == "cloth"
            ? p with { PosedBones = new HashSet<uint> { 21 } }
            : p).ToArray();
        var classified = PoolDerive.CoverTierBones(PoolDerive.Derive(Donor(10, 11, 12), roster), roster,
            TiersOf(Draws("body", BodyPosed, Tier("body_lod1", "b1", 20)),
                    Draws("cloth", new uint[] { 21 }, Tier("cloth_lod1", "c1"))),
            maxParts: 8, replacedPart: "body", readableRoster: roster);
        AssertTierVerdict(classified, PoolDerive.TierBoneClass.Lod1Only, 20, "body_lod1");
    }

    [Theory]
    [InlineData(true, PoolDerive.TierBoneClass.Merged)]
    [InlineData(false, PoolDerive.TierBoneClass.Lod1Only)]
    public void The_same_sibling_posing_or_only_tabling_the_bone_classifies_merged_or_lod1_only(
        bool siblingPoses, PoolDerive.TierBoneClass expected)
    {
        var clothPosed = siblingPoses ? new uint[] { 20, 21 } : new uint[] { 21 };
        var roster = new[]
        {
            Part("body", 10, 11, 12),
            new PoolDerive.PartBones("cloth", new HashSet<uint> { 20, 21 },
                PosedBones: clothPosed.ToHashSet()),
        };
        var classified = PoolDerive.CoverTierBones(
            PoolDerive.Derive(Donor(10, 11, 12), roster, replacedPart: "body"), roster,
            TiersOf(
                Draws("body", new uint[] { 10, 11, 12 }, Tier("body_lod1", "b1", 20)),
                Draws("cloth", clothPosed)),
            maxParts: 8, replacedPart: "body", readableRoster: roster);

        AssertTierVerdict(classified, expected, 20, "body_lod1",
            siblingPoses ? new[] { "cloth" } : Array.Empty<string>());
    }

    [Fact]
    public void A_tabling_sibling_is_not_named_beside_a_posing_owner()
    {
        var roster = new[]
        {
            Part("body", 10, 11, 12),
            new PoolDerive.PartBones("cloth", new HashSet<uint> { 20 },
                PosedBones: new HashSet<uint> { 20 }),
            new PoolDerive.PartBones("sash", new HashSet<uint> { 20, 21 },
                PosedBones: new HashSet<uint> { 21 }),
        };
        var classified = PoolDerive.CoverTierBones(
            PoolDerive.Derive(Donor(10, 11, 12), roster, replacedPart: "body"), roster,
            TiersOf(
                Draws("body", new uint[] { 10, 11, 12 }, Tier("body_lod1", "b1", 20)),
                Draws("cloth", new uint[] { 20 }),
                Draws("sash", new uint[] { 21 })),
            maxParts: 8, replacedPart: "body", readableRoster: roster);

        AssertTierVerdict(classified, PoolDerive.TierBoneClass.Merged, 20, "body_lod1", "cloth");
    }

    [Fact]
    public void A_part_that_does_not_render_at_the_asking_tier_is_no_carrier()
    {
        // The tier chain pairs pool parts by LOD label. A part with nothing at the asking label falls back
        // to its lod0 recovery, whose capture never fires in a frame that draws only the far tier.
        var classified = Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster),
            TiersOf(Draws("body", BodyPosed, Tier("body_lod1", "b1", 20)),
                    Draws("cloth", new uint[] { 20, 21 })));
        AssertTierVerdict(classified, PoolDerive.TierBoneClass.Merged, 20, "body_lod1", "cloth");
    }

    [Fact]
    public void A_part_of_another_outfit_state_is_no_carrier()
    {
        // _Dorm and _Fight are distinct garments, not detail levels: the Dorm cloth never draws in the
        // frames the plain lod1 draws in, so its capture can't feed that tier's recovery.
        var roster = new[] { Part("body_lod0", 10, 11, 12), Part("cloth_lod0_Dorm", 20, 21) };
        var classified = PoolDerive.CoverTierBones(
            PoolDerive.Derive(Donor(10, 11, 12), roster), roster, TiersOf(
                Draws("body_lod0", new uint[] { 10, 11, 12 }, Tier("body_lod1", "b1", 20)),
                Draws("cloth_lod0_Dorm", new uint[] { 20, 21 }, Tier("cloth_lod1_Dorm", "c1", 20))),
            maxParts: 8, replacedPart: "body_lod0", readableRoster: roster);
        AssertTierVerdict(classified, PoolDerive.TierBoneClass.Merged, 20, "body_lod1", "cloth_lod0_Dorm");
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
            maxParts: 8, replacedPart: "body_lod0_Dorm", readableRoster: roster);
        Assert.Equal(new[] { "body_lod0_Dorm", "cloth_lod0_Dorm" }, covered.Pool);
    }

    [Fact]
    public void A_tier_bone_no_readable_sibling_tables_is_classified_lod1_only()
    {
        var classified = Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster),
            TiersOf(Draws("body", BodyPosed, Tier("body_lod1", "b1", 999))));
        AssertTierVerdict(classified, PoolDerive.TierBoneClass.Lod1Only, 999, "body_lod1");
    }

    [Fact]
    public void A_carrier_whose_matching_tier_does_not_pose_the_bone_classifies_merged()
    {
        // The cloth has a lod1 of the right label and variant, but that tier does not pose bone 20 — the
        // draw a far frame recovers the row from would leave it unwritten exactly when it is read.
        var classified = Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster),
            TiersOf(
                Draws("body", BodyPosed, Tier("body_lod1", "b1", 10, 20)),
                Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 21))));
        AssertTierVerdict(classified, PoolDerive.TierBoneClass.Merged, 20, "body_lod1", "cloth");
    }

    [Fact]
    public void A_pool_mates_uncovered_tier_row_is_classified_mate_tier_first()
    {
        var derived = PoolDerive.Derive(Donor(10, 20), Roster, replacedPart: "body");
        var classified = PoolDerive.CoverTierBones(derived, Roster, TiersOf(
            Draws("body", BodyPosed),
            Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 999))),
            maxParts: 8, replacedPart: "body", readableRoster: Roster);

        AssertTierVerdict(classified, PoolDerive.TierBoneClass.MateTier, 999, "cloth_lod1");
    }

    [Fact]
    public void A_shared_tier_capture_mints_one_verdict_for_each_asking_part()
    {
        var roster = new[]
        {
            Part("mate", 30),
            Part("body", 10),
            Part("cloth", 20),
        };
        var derived = PoolDerive.Derive(Donor(30, 10), roster, replacedPart: "body");
        var classified = PoolDerive.CoverTierBones(derived, roster, TiersOf(
            Draws("mate", new uint[] { 30 }, Tier("mate_lod1", "shared", 20)),
            Draws("body", new uint[] { 10 },
                Tier("body_lod1", "shared", 20), Tier("body_lod1", "shared", 20)),
            Draws("cloth", new uint[] { 20 })),
            maxParts: 8, replacedPart: "body", readableRoster: roster);

        Assert.Collection(classified.TierBoneVerdicts,
            mate =>
            {
                Assert.Equal("mate", mate.TierPart);
                Assert.Equal("mate_lod1", mate.Tier);
                Assert.Equal(20u, mate.Bone);
                Assert.Equal(PoolDerive.TierBoneClass.MateTier, mate.Classification);
            },
            own =>
            {
                Assert.Equal("body", own.TierPart);
                Assert.Equal("body_lod1", own.Tier);
                Assert.Equal(20u, own.Bone);
                Assert.Equal(PoolDerive.TierBoneClass.Merged, own.Classification);
                Assert.Equal(new[] { "cloth" }, own.OwningParts);
            });
    }

    [Fact]
    public void An_own_tier_classifies_each_residual_row_independently()
    {
        var classified = Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster),
            TiersOf(Draws("body", BodyPosed, Tier("body_lod1", "b1", 20, 999))));

        Assert.Collection(classified.TierBoneVerdicts,
            merged =>
            {
                Assert.Equal(20u, merged.Bone);
                Assert.Equal(PoolDerive.TierBoneClass.Merged, merged.Classification);
                Assert.Equal(new[] { "cloth" }, merged.OwningParts);
            },
            lod1Only =>
            {
                Assert.Equal(999u, lod1Only.Bone);
                Assert.Equal(PoolDerive.TierBoneClass.Lod1Only, lod1Only.Classification);
                Assert.Empty(lod1Only.OwningParts);
            });
    }

    [Fact]
    public void Covering_past_the_pool_cap_refuses_rather_than_shipping_an_unposeable_tier()
    {
        var e = Assert.Throws<InvalidDataException>(() => Cover(PoolDerive.Derive(Donor(10, 11, 12), Roster),
            TiersOf(Draws("body", BodyPosed, Tier("body_lod1", "b1", 20, 30)),
                    Draws("cloth", new uint[] { 20, 21 }, Tier("cloth_lod1", "c1", 20)),
                    Draws("hair", new uint[] { 30 }, Tier("hair_lod1", "h1", 30))), maxParts: 2));
        Assert.Contains("more than 2 parts at this detail level", e.Message);
        Assert.Contains("body_lod1", e.Message);
        Assert.Contains("1 bone this install's files do not name", e.Message);
        Assert.Contains("'hair'", e.Message);
        Assert.DoesNotContain("0x", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "Pool-cap refusal: tier 'body_lod1' uses no matching chain suffix (0x0000001e) from 'hair'.",
            Assert.Single(BuildLogDiagnostics.From(e)));
    }

    [Fact]
    public void Pool_cap_refusal_names_the_bone_and_its_owning_part_when_resolved()
    {
        const string suffix = "Hair01_R/Bone_M";
        uint mirrored = BoneTable.Hash(suffix);
        var roster = new[] { Part("body", 10), Part("shoes", mirrored) };
        var derived = PoolDerive.Derive(Donor(10), roster, replacedPart: "body");

        var e = Assert.Throws<InvalidDataException>(() => PoolDerive.CoverTierBones(
            derived, roster, TiersOf(
                Draws("body", new uint[] { 10 }, Tier("body_lod1", "b1", mirrored)),
                Draws("shoes", new[] { mirrored }, Tier("shoes_lod1", "s1", mirrored))),
            maxParts: 1, replacedPart: "body", readableRoster: roster,
            bonePaths: new Dictionary<uint, string>
            {
                [mirrored] = "Prefab/root/Root_M/Hair01_R/Bone_M",
            }));

        Assert.Contains("bone 'Bone_M' from 'shoes'", e.Message);
        Assert.DoesNotContain("Hair01_R/", e.Message, StringComparison.Ordinal);
        Assert.Contains("more than 1 part at this detail level", e.Message);
        Assert.DoesNotContain("0x", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            $"Pool-cap refusal: tier 'body_lod1' uses '{suffix}' (0x{mirrored:x8}) from 'shoes'.",
            Assert.Single(BuildLogDiagnostics.From(e)));
    }

    [Fact]
    public void Pool_cap_refusal_pairs_the_winning_carrier_with_a_bone_it_can_cover()
    {
        uint glove = BoneTable.Hash("Hand_L/Glove49");
        uint boot = BoneTable.Hash("Foot_L/Boot_L");
        uint sole = BoneTable.Hash("Foot_L/Sole_L");
        var roster = new[]
        {
            Part("body", 10),
            Part("glove", glove),
            Part("shoes", boot, sole),
        };
        var derived = PoolDerive.Derive(Donor(10), roster, replacedPart: "body");

        var e = Assert.Throws<InvalidDataException>(() => PoolDerive.CoverTierBones(
            derived, roster, TiersOf(
                Draws("body", new uint[] { 10 }, Tier("body_lod1", "b1", glove, boot, sole)),
                Draws("glove", new[] { glove }, Tier("glove_lod1", "g1", glove)),
                Draws("shoes", new[] { boot, sole }, Tier("shoes_lod1", "s1", boot, sole))),
            maxParts: 1, replacedPart: "body", readableRoster: roster,
            bonePaths: new Dictionary<uint, string>
            {
                [glove] = "Prefab/root/Hand_L/Glove49",
                [boot] = "Prefab/root/Foot_L/Boot_L",
                [sole] = "Prefab/root/Foot_L/Sole_L",
            }));

        Assert.Contains("bone 'Sole_L' from 'shoes'", e.Message);
        Assert.DoesNotContain("Glove49' from 'shoes'", e.Message);
    }
}
