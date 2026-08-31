using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;
using static Remold.Core.Tests.Support.PoolFixtures;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="PoolDerive.Derive"/>'s posed gate: a donor bone every owner merely TABLES has no palette
/// row to recover, so the pool it asks for can't pose it. Tabling is what decides union membership;
/// posing is what decides whether the row is ever written, and only this gate reads the second.
/// </summary>
public class PoolPosedGateTests
{
    /// <summary>A part tabling <paramref name="tabled"/> and posing <paramref name="posed"/>.</summary>
    private static PoolDerive.PartBones Part(string mesh, uint[] tabled, uint[] posed) =>
        new(mesh, tabled.ToHashSet(), PosedBones: posed.ToHashSet());

    [Fact]
    public void A_donor_bone_every_owner_only_tables_refuses_naming_the_bone_and_the_parts()
    {
        // Both parts table bone 40 at zero weight — the corpus shape where a base cloth lists the whole
        // skeleton and poses a fraction of it. Nothing in this pool can write that bone's row.
        var roster = new[]
        {
            Part("body", new uint[] { 10, 11, 40 }, new uint[] { 10, 11 }),
            Part("cloth", new uint[] { 20, 40 }, new uint[] { 20 }),
        };
        var e = Assert.Throws<InvalidDataException>(() => PoolDerive.Derive(Donor(10, 20, 40), roster));
        Assert.Contains("no part of this item moves", e.Message);
        Assert.Contains(Remold.Core.Migoto.BuildLogDiagnostics.From(e),
            d => d.Contains("0x00000028", System.StringComparison.Ordinal));
        Assert.Contains("'body'", e.Message);
        Assert.Contains("'cloth'", e.Message);
    }

    [Fact]
    public void A_bone_one_part_tables_and_another_poses_passes()
    {
        // The body tables bone 40 without weight; the cloth poses it. One sound recovery is all the
        // union slot needs, and the pool derived is the one the donor's weights asked for.
        var roster = new[]
        {
            Part("body", new uint[] { 10, 11, 40 }, new uint[] { 10, 11 }),
            Part("cloth", new uint[] { 20, 40 }, new uint[] { 20, 40 }),
        };
        var r = PoolDerive.Derive(Donor(10, 11, 40), roster);
        Assert.Equal(new[] { "body", "cloth" }, r.Pool);
        Assert.Equal("body", r.Anchor);          // 3 tabled used bones to the cloth's 1
        Assert.Equal(3, r.UsedBoneCounts["body"]);
        Assert.Equal(1, r.UsedBoneCounts["cloth"]);
    }

    [Fact]
    public void A_part_the_donor_never_reaches_is_no_part_of_the_refusal()
    {
        // The face is on the roster and out of the pool. Naming it would send a modder re-weighting onto
        // a part whose bones this donor doesn't ride at all.
        var roster = new[]
        {
            Part("face", new uint[] { 1, 2 }, new uint[] { 1, 2 }),
            Part("body", new uint[] { 10, 40 }, new uint[] { 10 }),
        };
        var e = Assert.Throws<InvalidDataException>(() => PoolDerive.Derive(Donor(10, 40), roster));
        Assert.Contains("'body'", e.Message);
        Assert.DoesNotContain("'face'", e.Message);
    }

    [Fact]
    public void The_orphan_refusal_still_answers_first()
    {
        // A bone no part TABLES is a foreign-armature diagnosis, and the posed gate must not take that
        // refusal's place: the remedies are different (re-export the reference vs re-weight onto it).
        var roster = new[] { Part("body", new uint[] { 10, 40 }, new uint[] { 10 }) };
        var e = Assert.Throws<InvalidDataException>(() => PoolDerive.Derive(Donor(10, 999), roster));
        Assert.Contains("that no part of this item has", e.Message);
    }

    [Fact]
    public void A_roster_that_states_no_weights_is_gated_on_its_tables()
    {
        // The pre-measurement shape: a caller with no weight data leaves the gate reading the table, so
        // a pool that derived before the gate existed derives the same way now.
        var roster = new[] { new PoolDerive.PartBones("body", new HashSet<uint> { 10, 40 }) };
        var r = PoolDerive.Derive(Donor(10, 40), roster);
        Assert.Equal(new[] { "body" }, r.Pool);
    }

    [Fact]
    public void A_narrow_part_left_out_is_named_as_a_possible_poser()
    {
        // The hair rides bone 40 at weight 1 on every vertex and pools only for a Replace on itself, so
        // the outfit DOES move that bone — the modder can watch it. Telling them nothing does is a lie
        // about a hole this build made.
        var roster = new[]
        {
            Part("body", new uint[] { 10, 40 }, new uint[] { 10 }),
            new PoolDerive.PartBones("hair", new HashSet<uint> { 40 }, Narrow: true,
                PosedBones: new HashSet<uint> { 40 }),
        };
        var (candidates, excluded) = PoolDerive.PoolCandidates(roster, "body");

        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 40), candidates, missingParts: excluded, replacedPart: "body"));

        Assert.Contains("Left out: 'hair' · it stores one influence per vertex", e.Message);
        Assert.Contains("or remove this mesh edit", e.Message);
    }

    [Fact]
    public void A_presence_excluded_part_left_out_is_named_as_a_possible_poser()
    {
        // Same hole, the other candidacy rule: the wardrobe option that poses the bone can be off screen
        // while the replacement draws, so it sat out — and the refusal says which rule took it.
        var roster = new[]
        {
            new PoolDerive.PartBones("body", new HashSet<uint> { 10, 40 },
                PosedBones: new HashSet<uint> { 10 }),
            new PoolDerive.PartBones("P1_dress", new HashSet<uint> { 40 },
                Presence: new PartPresence(PresenceContext.Always, 21),
                PosedBones: new HashSet<uint> { 40 }),
        };
        var (candidates, excluded) = PoolDerive.PoolCandidates(roster, "body");

        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 40), candidates, missingParts: excluded, replacedPart: "body"));

        Assert.Contains("Left out: 'P1_dress' · it is a wardrobe option worn only some of the time",
            e.Message);
    }

    [Fact]
    public void A_held_back_part_of_unknown_bones_is_named_over_any_unposed_bone()
    {
        // Nothing was read off the part, so nothing rules it out as the one posing the bone — the same
        // posture the orphan refusal takes over an unknown table.
        var roster = new[] { Part("body", new uint[] { 10, 40 }, new uint[] { 10 }) };
        var missing = new[] { new PoolDerive.MissingPart("ghost", "bundle 'x' isn't in this install", null) };

        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 40), roster, missingParts: missing));

        Assert.Contains("Left out: 'ghost' · bundle 'x' isn't in this install", e.Message);
    }

    [Fact]
    public void A_left_out_part_that_cant_pose_the_bone_is_no_part_of_the_refusal()
    {
        // Its table settles it: the part never listed bone 40, so it can't be what moves it, and naming it
        // would send the modder at a part this donor has no business with.
        var roster = new[] { Part("body", new uint[] { 10, 40 }, new uint[] { 10 }) };
        var missing = new[] { new PoolDerive.MissingPart("face", "it carries blend shapes", new HashSet<uint> { 1, 2 }) };

        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 40), roster, missingParts: missing));

        Assert.Equal("the new mesh uses 1 bone(s) that no part of this item moves. They are named by "
            + "'body' but never moved. Re-weight the mesh onto the bones this item moves", e.Message);
    }

    [Fact]
    public void Every_unposed_bone_is_counted_and_the_lowest_hash_is_named()
    {
        // The message leads with one bone, so the count is what says how much re-weighting is left; the
        // named bone is the lowest hash, so two runs over one roster read the same.
        var roster = new[] { Part("body", new uint[] { 10, 40, 41 }, new uint[] { 10 }) };
        var e = Assert.Throws<InvalidDataException>(() => PoolDerive.Derive(Donor(10, 41, 40), roster));
        Assert.Contains("uses 2 bone(s)", e.Message);
        Assert.Contains(Remold.Core.Migoto.BuildLogDiagnostics.From(e),
            d => d.Contains("0x00000028", System.StringComparison.Ordinal));
    }
}
