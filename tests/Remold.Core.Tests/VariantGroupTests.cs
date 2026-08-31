using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Xunit;
using static Remold.Core.Tests.Support.PoolFixtures;

namespace Remold.Core.Tests;

/// <summary>
/// Alternation coverage (<see cref="PoolDerive.VariantGroups"/>): no single variant or context part is a
/// pool candidate — each is unworn or off-scene some of the time — but a bone with an on-screen poser in
/// EVERY (variant, context) cell the target displays in is posed whatever the player wears and wherever the
/// scene sits. This covers the forming rules, the posed gate that reads them, the tier-carrier seam they
/// must NOT reach, and the export offer that mirrors them.
/// </summary>
public class VariantGroupTests
{
    // One two-variant slot, tokens shaped like the shipped corpus. Slot id 2 leaves room below it for the
    // ordering test's second slot.
    private static readonly IReadOnlyList<PartScheme.Slot> Scheme = new[]
    {
        new PartScheme.Slot(2, new[]
        {
            new PartScheme.Variant(21, true, new[] { "P1_dress" }),
            new PartScheme.Variant(22, false, new[] { "P2_dress" }),
        }),
    };

    /// <summary>A wardrobe part of <paramref name="variant"/> tabling and posing exactly
    /// <paramref name="posed"/>.</summary>
    private static PoolDerive.PartBones Member(string mesh, long variant, uint[] posed,
        PresenceContext context = PresenceContext.Always, bool narrow = false, bool shadows = true,
        VisibilityOverride visibility = VisibilityOverride.None) =>
        new(mesh, posed.ToHashSet(), Narrow: narrow, Presence: new PartPresence(context, variant),
            PosedBones: posed.ToHashSet(), CastsShadows: shadows, Visibility: visibility);

    /// <summary>The always-on body, tabling whatever a test hands it and posing 10 and 11. The shared
    /// <see cref="Roster"/> has it TABLE the wardrobe's bone 40 at zero weight; coverage reads no part's
    /// table, so that only keeps the pool the same shape across these tests rather than deciding any of
    /// them — the rosters that leave 40 out prove the two answer alike.</summary>
    private static PoolDerive.PartBones Body(params uint[] tabled) =>
        new("body", tabled.ToHashSet(), PosedBones: new HashSet<uint> { 10, 11 });

    private static readonly IReadOnlyList<PoolDerive.PartBones> Roster = new[]
    {
        Body(10, 11, 40),
        Member("P1_dress", 21, new uint[] { 40, 41 }),
        Member("P2_dress", 22, new uint[] { 40 }),
    };

    private static IReadOnlyList<PoolDerive.VariantGroup> Groups(
        IReadOnlyList<PoolDerive.PartBones> roster, string target,
        IReadOnlyList<PartScheme.Slot>? scheme,
        IReadOnlyList<PoolDerive.MissingPart>? heldBack = null)
    {
        var (candidates, _) = PoolDerive.PoolCandidates(roster, target);
        return PoolDerive.VariantGroups(roster, scheme,
            heldBack ?? Array.Empty<PoolDerive.MissingPart>(), candidates, target);
    }

    // ---------------------------------------------------------------------------- forming

    [Fact]
    public void A_slot_whose_every_variant_poses_a_bone_forms_a_group()
    {
        var g = Assert.Single(Groups(Roster, "body", Scheme));
        Assert.Equal(PoolDerive.CoverageGroupId, g.SlotId);
        // 41 is P1's alone: the player wearing P2 would leave it posed by nothing
        Assert.Equal(new uint[] { 40 }, g.GroupBones);
        Assert.Equal(new[] { "P1_dress", "P2_dress" }, g.Members.Select(p => p.Mesh));
    }

    /// <summary>The corpus shape the unified predicate exists for: a slot's variant family ships as
    /// CONTEXT-TAGGED pieces, so no variant has an always-on member and the old per-slot rule certified
    /// nothing for an always-on target — while every (variant, context) cell the target displays in does
    /// hold a poser. The old scene arms could not see it either: the posers are variant-tagged, and a base
    /// target's arms admit only untagged parts.</summary>
    [Fact]
    public void A_slot_whose_cells_are_all_answered_certifies_for_an_always_on_target()
    {
        var roster = new[]
        {
            Body(10, 11, 40),
            Member("P1_dress_Fight", 21, new uint[] { 40 }, context: PresenceContext.Fight),
            Member("P1_dress_Dorm", 21, new uint[] { 40 }, context: PresenceContext.Dorm),
            Member("P2_dress", 22, new uint[] { 40 }),
        };
        var g = Assert.Single(Groups(roster, "body", Scheme));
        Assert.Equal(new uint[] { 40 }, g.GroupBones);
        Assert.Equal(new[] { "P1_dress_Fight", "P1_dress_Dorm", "P2_dress" },
            g.Members.Select(p => p.Mesh));

        // …and the export offer mirrors it
        Assert.Contains(40u, AssetExporter.ValidTailBones(roster, "body", Scheme));

        // the control: one displayed cell unanswered — no dorm-scene poser while variant 21 is worn —
        // and nothing certifies, because that cell is exactly a state the bone would go unposed in
        var oneCellShort = new[] { roster[0], roster[1], roster[3] };
        Assert.Empty(Groups(oneCellShort, "body", Scheme));
        Assert.DoesNotContain(40u, AssetExporter.ValidTailBones(oneCellShort, "body", Scheme));
    }

    /// <summary>An untagged context part answers its scene's cell of EVERY variant: it is worn whatever
    /// the wardrobe says. Here variant 22's dorm cell is answered by the none-variant dorm cloth, which
    /// the old per-slot rule (exact variant match) could not admit.</summary>
    [Fact]
    public void A_none_variant_context_part_answers_that_cell_for_every_variant()
    {
        var roster = new[]
        {
            Body(10, 11, 40),
            Member("P1_dress", 21, new uint[] { 40 }),
            Member("P2_dress_Fight", 22, new uint[] { 40 }, context: PresenceContext.Fight),
            Ctx("cloth_Dorm", PresenceContext.Dorm, new uint[] { 40 }),
        };
        var g = Assert.Single(Groups(roster, "body", Scheme));
        Assert.Equal(new uint[] { 40 }, g.GroupBones);
        Assert.Equal(new[] { "P1_dress", "P2_dress_Fight", "cloth_Dorm" },
            g.Members.Select(p => p.Mesh));

        // the control: no fight-scene answer for variant 22 — the untagged dorm part sits that scene out
        Assert.Empty(Groups(new[] { roster[0], roster[1], roster[3] }, "body", Scheme));
    }

    [Fact]
    public void A_slot_one_variant_short_forms_nothing()
    {
        // P2 is unworn some of the time and poses the bone through nothing else, so the slot certifies
        // nothing at all — a partial set is the one case coverage exists to rule out.
        var roster = new[] { Body(10, 11, 40), Member("P1_dress", 21, new uint[] { 40 }) };
        Assert.Empty(Groups(roster, "body", Scheme));
    }

    [Fact]
    public void An_unreadable_scheme_forms_no_slot()
    {
        // The wardrobe is what states which parts are alternatives; without it there is no slot to certify.
        // No part of this roster is a context part, so the pair is no route back in either.
        Assert.Empty(Groups(Roster, "body", null));
        Assert.Empty(Groups(Roster, "body", Array.Empty<PartScheme.Slot>()));
    }

    /// <summary>Pieces of one worn variant are ADDITIVE: the unmeasurable sibling piece co-draws beside the
    /// variant's measured member and displaces nothing, so it cannot unsettle coverage that member certifies.
    /// The corpus shape this answers for is a variant whose body and head pieces store two influences and
    /// read as unmeasurable while its cloth pieces measure cleanly and pose the bones.</summary>
    [Fact]
    public void A_held_back_sibling_piece_leaves_a_measured_slots_coverage_standing()
    {
        // P2_dress_extra is a second piece of variant 22, whose own member P2_dress measures and poses 40
        var unread = new[]
        {
            new PoolDerive.MissingPart("P2_dress_extra", "its skin weights can't be read", null,
                new PartPresence(PresenceContext.Always, 22)),
        };
        Assert.Equal(new uint[] { 40 }, Assert.Single(Groups(Roster, "body", Scheme, unread)).GroupBones);

        // …as does a held-back part of no wardrobe slot: it can be the missing member of nothing.
        var elsewhere = new[]
        {
            new PoolDerive.MissingPart("hair", "its skin weights can't be read", null, PartPresence.Always),
        };
        Assert.Single(Groups(Roster, "body", Scheme, elsewhere));
    }

    /// <summary>The inverse control, and the hole the deleted rule used to cover: when a variant's ONLY parts
    /// are held back it has no measured member at all, and the per-variant member rule kills the slot without
    /// any held-back rule of its own. Coverage the unworn cases cannot answer for is still refused.</summary>
    [Fact]
    public void A_variant_whose_only_parts_are_held_back_forms_nothing()
    {
        // variant 22's only part is held back, so the roster offers the slot no member for it at all
        var roster = new[] { Body(10, 11, 40), Member("P1_dress", 21, new uint[] { 40 }) };
        var unread = new[]
        {
            new PoolDerive.MissingPart("P2_dress", "its skin weights can't be read", null,
                new PartPresence(PresenceContext.Always, 22)),
        };
        Assert.Empty(Groups(roster, "body", Scheme, unread));

        // the control: the SAME held-back variant, over a roster where variant 22 keeps a measured member
        // of its own. The kill above is the missing member's doing and not the held-back entry's.
        Assert.Single(Groups(Roster, "body", Scheme, new[]
        {
            new PoolDerive.MissingPart("P2_dress_extra", "its skin weights can't be read", null,
                new PartPresence(PresenceContext.Always, 22)),
        }));
    }

    [Fact]
    public void A_held_back_part_the_caller_did_not_classify_forms_nothing()
    {
        // Nothing says which slot it belonged to, so every slot reads it as possibly its own.
        var unread = new[] { new PoolDerive.MissingPart("ghost", "bundle 'x' isn't in this install", null) };
        Assert.Empty(Groups(Roster, "body", Scheme, unread));
    }

    [Fact]
    public void A_wardrobe_part_the_scheme_does_not_list_forms_nothing()
    {
        // Nothing states which slot it is an alternative of, so any slot's coverage may be the one it
        // takes a turn away from — the listed variants agreeing on a bone settles nothing.
        var roster = Roster.Append(Member("P3_dress", PartPresence.UnknownVariant, new uint[] { 60 }))
            .ToList();
        Assert.Empty(Groups(roster, "body", Scheme));
        Assert.DoesNotContain(40u, AssetExporter.ValidTailBones(roster, "body", Scheme));
    }

    [Fact]
    public void A_held_back_part_of_no_listed_slot_forms_nothing()
    {
        var unread = new[]
        {
            new PoolDerive.MissingPart("P3_dress", "its skin weights can't be read", null,
                new PartPresence(PresenceContext.Always, PartPresence.UnknownVariant)),
        };
        Assert.Empty(Groups(Roster, "body", Scheme, unread));
    }

    /// <summary>The corpus shape: a group's own members are usually the only parts tabling the bone they
    /// cover. Coverage asks nothing of the pool's tables — the bone rides an appended palette slot past the
    /// union, written at the drawing member's dispatch — so the members tabling it is enough.</summary>
    [Fact]
    public void A_bone_only_the_members_table_is_covered_all_the_same()
    {
        var roster = new[]
        {
            Body(10, 11),
            Member("P1_dress", 21, new uint[] { 40 }),
            Member("P2_dress", 22, new uint[] { 40 }),
        };
        Assert.Equal(new uint[] { 40 }, Assert.Single(Groups(roster, "body", Scheme)).GroupBones);
        Assert.Contains(40u, AssetExporter.ValidTailBones(roster, "body", Scheme));

        // the control: a bone NOBODY poses is covered by nothing at all — the members are what certify a
        // group, so a bone missing from their posed sets forms none of it
        Assert.DoesNotContain(41u, Assert.Single(Groups(roster, "body", Scheme)).GroupBones);
        Assert.DoesNotContain(41u, AssetExporter.ValidTailBones(roster, "body", Scheme));
    }

    [Theory]
    [InlineData("narrow")]
    [InlineData("shadow")]
    [InlineData("visibility")]
    public void A_member_no_pool_could_lean_on_disqualifies_its_variant(string rule)
    {
        var p2 = Member("P2_dress", 22, new uint[] { 40 },
            narrow: rule == "narrow",
            shadows: rule != "shadow",
            visibility: rule == "visibility" ? VisibilityOverride.DormHidden : VisibilityOverride.None);
        var roster = new[] { Body(10, 11, 40), Member("P1_dress", 21, new uint[] { 40 }), p2 };
        Assert.Empty(Groups(roster, "body", Scheme));
    }

    [Fact]
    public void An_unmeasured_member_disqualifies_its_variant()
    {
        // Its posed set would fall back to its bone TABLE, which would certify coverage of bones it carries
        // at zero weight — the one thing the posed gate exists to refuse.
        var roster = new[]
        {
            Body(10, 11, 40),
            Member("P1_dress", 21, new uint[] { 40 }),
            new PoolDerive.PartBones("P2_dress", new HashSet<uint> { 40 },
                Presence: new PartPresence(PresenceContext.Always, 22)),
        };
        Assert.Empty(Groups(roster, "body", Scheme));
    }

    [Fact]
    public void A_context_member_covers_only_a_target_of_its_own_context()
    {
        var roster = new[]
        {
            Body(10, 11, 40),
            new PoolDerive.PartBones("cloth1_Fight", new HashSet<uint> { 12 },
                Presence: new PartPresence(PresenceContext.Fight, PartPresence.NoVariant),
                PosedBones: new HashSet<uint> { 12 }),
            Member("P1_dress", 21, new uint[] { 40 }),
            Member("P2_dress_Fight", 22, new uint[] { 40 }, context: PresenceContext.Fight),
        };
        // an always-on target draws in frames the combat-only member sits out of
        Assert.Empty(Groups(roster, "body", Scheme));
        Assert.Equal(new uint[] { 40 }, Assert.Single(Groups(roster, "cloth1_Fight", Scheme)).GroupBones);
    }

    [Fact]
    public void Group_bones_leave_out_what_the_pool_poses_and_the_unrecoverable_hash()
    {
        var roster = new[]
        {
            Body(10, 11, 40),
            Member("P1_dress", 21, new uint[] { 0, 10, 40 }),
            Member("P2_dress", 22, new uint[] { 0, 10, 40 }),
        };
        // 10 is the body's own, 0 is never an owner key
        Assert.Equal(new uint[] { 40 }, Assert.Single(Groups(roster, "body", Scheme)).GroupBones);
    }

    [Fact]
    public void A_slot_covering_only_what_the_pool_poses_forms_nothing()
    {
        var roster = new[]
        {
            Body(10, 11),
            Member("P1_dress", 21, new uint[] { 10 }),
            Member("P2_dress", 22, new uint[] { 10 }),
        };
        Assert.Empty(Groups(roster, "body", Scheme));
    }

    [Fact]
    public void Two_slots_certify_into_one_group_with_bones_ascending()
    {
        // Two slots each certify their own bones; the group is ONE, its bones the union ascending by hash
        // and its members each listed once in roster order — the shape every later stage reads.
        var scheme = new[]
        {
            Scheme[0],
            new PartScheme.Slot(1, new[]
            {
                new PartScheme.Variant(11, true, new[] { "P1_head" }),
                new PartScheme.Variant(12, false, new[] { "P2_head" }),
            }),
        };
        var roster = new[]
        {
            Body(10, 11, 40, 41, 50, 51),
            Member("P1_dress", 21, new uint[] { 41, 40 }),
            Member("P2_dress", 22, new uint[] { 40, 41 }),
            Member("P1_head", 11, new uint[] { 51, 50 }),
            Member("P2_head", 12, new uint[] { 50, 51 }),
        };
        var g = Assert.Single(Groups(roster, "body", scheme));
        Assert.Equal(new uint[] { 40, 41, 50, 51 }, g.GroupBones);
        Assert.Equal(new[] { "P1_dress", "P2_dress", "P1_head", "P2_head" },
            g.Members.Select(p => p.Mesh));
    }

    [Fact]
    public void The_targets_own_slot_certifies_nothing_by_itself()
    {
        // A Replace on a variant part narrows its own slot's displayed cells to that variant alone, and
        // every poser answering those cells is a pool candidate already — the part a Replace lands on is
        // not its own recovery source, and its slot siblings alternate with it.
        Assert.Empty(Groups(Roster, "P1_dress", Scheme));
    }

    // ---------------------------------------------------------------------------- the posed gate

    [Fact]
    public void The_posed_gate_admits_a_bone_the_wardrobe_covers()
    {
        var (candidates, leftOut) = PoolDerive.PoolCandidates(Roster, "body");
        var groups = PoolDerive.VariantGroups(Roster, Scheme, Array.Empty<PoolDerive.MissingPart>(),
            candidates, "body");

        var r = PoolDerive.Derive(Donor(10, 40), candidates, missingParts: leftOut, replacedPart: "body",
            groups: groups);

        Assert.Equal(new[] { "body" }, r.Pool);
        Assert.Equal(PoolDerive.CoverageGroupId, Assert.Single(r.GroupCovered).Value);
        Assert.Equal(40u, r.GroupCovered.Keys.Single());

        // …and the same derive without the groups is the refusal it has always been
        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 40), candidates, missingParts: leftOut, replacedPart: "body"));
        Assert.Contains("no part of this item moves", e.Message);
    }

    [Fact]
    public void The_posed_gate_still_refuses_a_bone_no_group_covers_in_the_same_words()
    {
        var roster = new[]
        {
            Body(10, 11, 40, 42),
            Member("P1_dress", 21, new uint[] { 40 }),
            Member("P2_dress", 22, new uint[] { 40 }),
        };
        var (candidates, leftOut) = PoolDerive.PoolCandidates(roster, "body");
        var groups = PoolDerive.VariantGroups(roster, Scheme, Array.Empty<PoolDerive.MissingPart>(),
            candidates, "body");

        // 40 is covered and 42 is not, so the refusal counts and names 42 alone
        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 40, 42), candidates, missingParts: leftOut, replacedPart: "body",
                groups: groups));
        // …and the wardrobe parts are no part of it: their tables settle that neither can be posing 42
        Assert.Equal("the new mesh uses 1 bone(s) that no part of this item moves. They are named by "
            + "'body' but never moved. Re-weight the mesh onto the bones this item moves", e.Message);
    }

    /// <summary>The corpus shape the orphan check used to turn down: the donor rides a bone ONLY the group's
    /// members table, so the pool neither poses it nor owns it. Both gates read the same certificate, and the
    /// bone comes back covered rather than blamed on a foreign armature.</summary>
    [Fact]
    public void The_orphan_check_passes_a_bone_only_the_members_table()
    {
        var roster = new[]
        {
            Body(10, 11),
            Member("P1_dress", 21, new uint[] { 40 }),
            Member("P2_dress", 22, new uint[] { 40 }),
        };
        var (candidates, leftOut) = PoolDerive.PoolCandidates(roster, "body");
        var groups = PoolDerive.VariantGroups(roster, Scheme, Array.Empty<PoolDerive.MissingPart>(),
            candidates, "body");

        var r = PoolDerive.Derive(Donor(10, 40), candidates, missingParts: leftOut, replacedPart: "body",
            groups: groups);
        Assert.Equal(new[] { "body" }, r.Pool);
        Assert.Equal(40u, r.GroupCovered.Keys.Single());
        Assert.Equal(PoolDerive.CoverageGroupId, r.GroupCovered[40u]);

        // …and the same roster with no groups is the orphan refusal it has always been, word for word
        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 40), candidates, missingParts: leftOut, replacedPart: "body"));
        Assert.Equal("the new mesh uses 1 bone(s) that no part this mod can build with has. "
            + "Left out: 'P1_dress' · it is a wardrobe option worn only some of the time; "
            + "'P2_dress' · it is a wardrobe option worn only some of the time. "
            + "Re-weight the mesh onto the parts that are in, or remove this mesh edit", e.Message);
    }

    /// <summary>A bone no group covers is still an orphan, and the exemption changes neither the count nor
    /// the wording: the covered bone drops out of the refusal and the uncovered one is named alone.</summary>
    [Fact]
    public void The_orphan_refusal_still_fires_for_what_no_group_covers()
    {
        var roster = new[]
        {
            Body(10, 11),
            Member("P1_dress", 21, new uint[] { 40 }),
            Member("P2_dress", 22, new uint[] { 40 }),
        };
        var (candidates, _) = PoolDerive.PoolCandidates(roster, "body");
        var groups = PoolDerive.VariantGroups(roster, Scheme, Array.Empty<PoolDerive.MissingPart>(),
            candidates, "body");

        // 40 is covered and 99 is owned by nothing at all, so the refusal counts and names 99 alone. No part
        // was held back that could own it, which is the foreign-armature wording.
        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 40, 99), candidates, replacedPart: "body", groups: groups));
        Assert.Equal("the new mesh uses 1 bone(s) that no part of this item has. It was weighted "
            + "against a different armature. Open this item in Blender again and re-weight the mesh",
            e.Message);
    }

    /// <summary>The hole the lifted orphan check opens, closed in words: with every used bone covered by a
    /// group, no roster part tables one and the pool comes out EMPTY. There is then no union to compile
    /// against and no draw to host the replacement, and the anchor selection would read off the end.</summary>
    [Fact]
    public void A_donor_riding_only_covered_bones_leaves_no_pool_and_is_refused()
    {
        var roster = new[]
        {
            Body(10, 11),
            Member("P1_dress", 21, new uint[] { 40 }),
            Member("P2_dress", 22, new uint[] { 40 }),
        };
        var (candidates, leftOut) = PoolDerive.PoolCandidates(roster, "body");
        var groups = PoolDerive.VariantGroups(roster, Scheme, Array.Empty<PoolDerive.MissingPart>(),
            candidates, "body");

        var e = Assert.Throws<AuthoredRefusalException>(() =>
            PoolDerive.Derive(Donor(40), candidates, missingParts: leftOut, replacedPart: "body",
                groups: groups));
        Assert.Equal("the new mesh uses only bones that belong to this item's other wardrobe or scene "
            + "options, so no part of it can carry the replacement. Re-weight the mesh onto the bones "
            + "this item's own parts move as well", e.Message);

        // the control: one bone of the body's own is all it takes for the pool to form, and the covered
        // bone rides along
        var ok = PoolDerive.Derive(Donor(40, 10), candidates, missingParts: leftOut, replacedPart: "body",
            groups: groups);
        Assert.Equal(new[] { "body" }, ok.Pool);
        Assert.Equal(40u, ok.GroupCovered.Keys.Single());
    }

    [Fact]
    public void A_derive_with_no_groups_reports_no_covered_bones()
    {
        var (candidates, _) = PoolDerive.PoolCandidates(Roster, "body");
        Assert.Empty(PoolDerive.Derive(Donor(10, 11), candidates, replacedPart: "body").GroupCovered);
    }

    // ------------------------------------------------------------- one mesh in two certifying slots

    /// <summary>Two scheme slots can list ONE variant id, and a roster part of that variant then answers
    /// cells of both. The two shipped-rule groups this used to form shared that mesh, and the emission
    /// could carry only one of them — the later slot's bones were dropped whole. One merged group has no
    /// second set of sections to clash with: the shared part is one member, writing rows for every
    /// certified bone it poses, and BOTH slots' bones survive.</summary>
    private static readonly IReadOnlyList<PartScheme.Slot> SharedVariantScheme = new[]
    {
        new PartScheme.Slot(7, new[]
        {
            new PartScheme.Variant(21, true, new[] { "P1_dress" }),
            new PartScheme.Variant(22, false, new[] { "P2_dress" }),
        }),
        new PartScheme.Slot(8, new[]
        {
            new PartScheme.Variant(21, true, new[] { "P1_dress" }),
            new PartScheme.Variant(23, false, new[] { "P3_dress" }),
        }),
    };

    /// <summary>The roster of <see cref="SharedVariantScheme"/>: P1 is variant 21 in BOTH slots, so slot 7
    /// certifies 40 (P1 and P2 agree) and slot 8 certifies 50 (P1 and P3 agree).</summary>
    private static readonly IReadOnlyList<PoolDerive.PartBones> SharedVariantRoster = new[]
    {
        Body(10, 11, 40, 50),
        Member("P1_dress", 21, new uint[] { 40, 50 }),
        Member("P2_dress", 22, new uint[] { 40 }),
        Member("P3_dress", 23, new uint[] { 50 }),
    };

    [Fact]
    public void A_part_answering_two_slots_is_one_member_and_both_slots_bones_survive()
    {
        var (candidates, leftOut) = PoolDerive.PoolCandidates(SharedVariantRoster, "body");
        var groups = PoolDerive.VariantGroups(SharedVariantRoster, SharedVariantScheme,
            Array.Empty<PoolDerive.MissingPart>(), candidates, "body");

        var g = Assert.Single(groups);
        Assert.Equal(new uint[] { 40, 50 }, g.GroupBones);
        // the shared part appears once, so every certified bone has one gmap per member mesh
        Assert.Equal(new[] { "P1_dress", "P2_dress", "P3_dress" }, g.Members.Select(p => p.Mesh));

        // the posed gate admits both slots' bones on the one certificate
        var r = PoolDerive.Derive(Donor(10, 40, 50), candidates, missingParts: leftOut,
            replacedPart: "body", groups: groups);
        Assert.Equal(new uint[] { 40, 50 }, r.GroupCovered.Keys.OrderBy(h => h));
    }

    // ---------------------------------------------------------------- what the build carries of a group

    /// <summary>The formation lists every poser of every certifying cell; the build carries only the
    /// members posing a bone the gate admitted, one per mesh. A member posing none of them would be
    /// dumped and hash-claimed for nothing (the emitter sentinels its every row), and its claims could
    /// refuse the build over a draw-signature collision in a mesh the Replace doesn't lean on. The
    /// per-mesh cap holds the one-writer-per-gmap invariant where the sections are minted.</summary>
    [Fact]
    public void The_build_carries_only_members_posing_an_admitted_bone_one_per_mesh()
    {
        var g = new PoolDerive.VariantGroup(PoolDerive.CoverageGroupId, new[]
        {
            Member("P1_dress", 21, new uint[] { 40 }),
            Member("P2_dress", 22, new uint[] { 50 }),
            Member("P2_dress", 22, new uint[] { 40 }),   // one mesh twice: one writer per gmap file
            Member("P3_hat", 23, new uint[] { 60 }),     // poses nothing the gate admitted
        }, new uint[] { 40, 50 });

        Assert.Equal(new[] { "P1_dress", "P2_dress" },
            ModBuilder.CoveredMembers(g, new uint[] { 40, 50 }).Select(p => p.Mesh));

        // …and the trim follows the gate's subset: a bone the donor doesn't ride carries nobody for it
        Assert.Equal(new[] { "P2_dress" },
            ModBuilder.CoveredMembers(g, new uint[] { 50 }).Select(p => p.Mesh));
    }

    // ---------------------------------------------------------------------------- tier carriers

    /// <summary>Tier coverage is ranked over the CANDIDATE set, so a part the wardrobe rule refused can
    /// never be picked to carry another part's tier bones — group or no group. The control proves the part
    /// could have covered it, which is what makes the candidate seam load-bearing rather than incidental.
    /// </summary>
    [Fact]
    public void A_variant_refused_part_is_never_a_tier_carrier()
    {
        var roster = new[]
        {
            new PoolDerive.PartBones("body", new HashSet<uint> { 10, 11 },
                PosedBones: new HashSet<uint> { 10, 11 }),
            Member("P1_dress", 21, new uint[] { 40 }),
            Member("P2_dress", 22, new uint[] { 40 }),
        };
        PoolDerive.PartTiers TiersOf(string mesh) => mesh switch
        {
            "body" => new PoolDerive.PartTiers("h_body0", new HashSet<uint> { 10, 11 },
                new[] { new PoolDerive.TierBones("body_lod1", "h_body1", new HashSet<uint> { 40 }) }),
            _ => new PoolDerive.PartTiers($"h_{mesh}", new HashSet<uint> { 40 },
                new[] { new PoolDerive.TierBones($"{mesh}_lod1", $"h_{mesh}_1", new HashSet<uint> { 40 }) }),
        };

        var (candidates, _) = PoolDerive.PoolCandidates(roster, "body");
        var derived = PoolDerive.Derive(Donor(10, 11), candidates, replacedPart: "body",
            groups: PoolDerive.VariantGroups(roster, Scheme, Array.Empty<PoolDerive.MissingPart>(),
                candidates, "body"));

        var classified = PoolDerive.CoverTierBones(derived, candidates, TiersOf,
            MigotoEmitter.MaxPoolParts, replacedPart: "body", readableRoster: roster);
        var verdict = Assert.Single(classified.TierBoneVerdicts);
        Assert.Equal(PoolDerive.TierBoneClass.Merged, verdict.Classification);
        Assert.Equal(new[] { "P1_dress", "P2_dress" }, verdict.OwningParts);

        // the control: over the whole roster the dress covers it, so the classification above is the
        // candidacy filter's doing and nothing else
        Assert.Equal(new[] { "body", "P1_dress" },
            PoolDerive.CoverTierBones(derived, roster, TiersOf, MigotoEmitter.MaxPoolParts,
                replacedPart: "body", readableRoster: roster).Pool);
    }

    // ---------------------------------------------------------------------------- the export offer

    [Fact]
    public void A_tail_offers_the_bones_the_wardrobe_covers()
    {
        // Without the wardrobe the bone reaches no tail: no single variant part is a candidate.
        Assert.DoesNotContain(40u, AssetExporter.ValidTailBones(Roster, "body"));
        Assert.Contains(40u, AssetExporter.ValidTailBones(Roster, "body", Scheme));

        // an unmeasurable SIBLING piece of a variant leaves the offer standing, mirroring the coverage: the
        // piece co-draws beside the variant's measured member and displaces nothing it certifies
        var sibling = new[]
        {
            new PoolDerive.MissingPart("P2_dress_extra", "its skin weights can't be read", null,
                new PartPresence(PresenceContext.Always, 22)),
        };
        Assert.Contains(40u, AssetExporter.ValidTailBones(Roster, "body", Scheme, sibling));

        // …but a variant left with NO measured member takes the offer back off, the same per-variant rule
        // that kills the group
        var roster = new[] { Body(10, 11, 40), Member("P1_dress", 21, new uint[] { 40 }) };
        var onlyPart = new[]
        {
            new PoolDerive.MissingPart("P2_dress", "its skin weights can't be read", null,
                new PartPresence(PresenceContext.Always, 22)),
        };
        Assert.DoesNotContain(40u, AssetExporter.ValidTailBones(roster, "body", Scheme, onlyPart));
    }

    // ------------------------------------------------------------------------ the scene-context pair

    /// <summary>A context part: on screen in its own scene only, and worn whatever the wardrobe says unless
    /// a variant is named.</summary>
    private static PoolDerive.PartBones Ctx(string mesh, PresenceContext context, uint[] posed,
        long variant = PartPresence.NoVariant) =>
        Member(mesh, variant, posed, context: context);

    /// <summary>A pair's world: the body tables 40 and 50 at zero weight, and the two context parts each
    /// pose both plus one of their own. No part of it belongs to <see cref="Scheme"/>'s slot, so every group
    /// here is the pair.</summary>
    private static readonly IReadOnlyList<PoolDerive.PartBones> PairRoster = new[]
    {
        Body(10, 11, 40, 50),
        Ctx("cloth_Fight", PresenceContext.Fight, new uint[] { 50, 40, 41 }),
        Ctx("cloth_Dorm", PresenceContext.Dorm, new uint[] { 40, 50, 42 }),
    };

    [Fact]
    public void A_fight_part_and_a_dorm_part_pair_into_a_group()
    {
        var g = Assert.Single(Groups(PairRoster, "body", Scheme));
        Assert.Equal(PoolDerive.CoverageGroupId, g.SlotId);
        // 41 is the fight side's alone and 42 the dorm side's: the scene dressing the other side leaves
        // each of them posed by nothing
        Assert.Equal(new uint[] { 40, 50 }, g.GroupBones);
        Assert.Equal(new[] { "cloth_Fight", "cloth_Dorm" }, g.Members.Select(p => p.Mesh));
    }

    [Fact]
    public void A_pair_needs_both_arms()
    {
        // The scenes on the other side dress the subject in something too, and nothing measured says that
        // something poses the bone.
        Assert.Empty(Groups(new[] { Body(10, 11, 40, 50), PairRoster[1] }, "body", Scheme));
        Assert.Empty(Groups(new[] { Body(10, 11, 40, 50), PairRoster[2] }, "body", Scheme));
    }

    [Theory]
    [InlineData("narrow")]
    [InlineData("shadow")]
    [InlineData("visibility")]
    [InlineData("unmeasured")]
    public void A_pair_member_no_pool_could_lean_on_disqualifies_its_arm(string rule)
    {
        // the arm's only member, so the rule that refuses it empties the arm
        var dorm = rule == "unmeasured"
            // its posed set would fall back to its bone TABLE, certifying bones it carries at zero weight
            ? new PoolDerive.PartBones("cloth_Dorm", new HashSet<uint> { 40, 50, 42 },
                Presence: new PartPresence(PresenceContext.Dorm, PartPresence.NoVariant))
            : Member("cloth_Dorm", PartPresence.NoVariant, new uint[] { 40, 50, 42 },
                context: PresenceContext.Dorm,
                narrow: rule == "narrow",
                shadows: rule != "shadow",
                visibility: rule == "visibility" ? VisibilityOverride.DormHidden : VisibilityOverride.None);
        Assert.Empty(Groups(new[] { Body(10, 11, 40, 50), PairRoster[1], dorm }, "body", Scheme));
    }

    [Fact]
    public void A_pair_member_is_worn_whenever_the_target_is()
    {
        // A context part of the target's OWN variant is on screen every time the target is, so it arms the
        // pair. The target's slot forms nothing itself, which leaves the pair the only group here.
        var sameVariant = new[]
        {
            Body(10, 11, 40, 50),
            Member("P1_dress", 21, new uint[] { 40 }),
            Ctx("cloth_Fight", PresenceContext.Fight, new uint[] { 40, 50 }, variant: 21),
            Ctx("cloth_Dorm", PresenceContext.Dorm, new uint[] { 40, 50 }),
        };
        var g = Assert.Single(Groups(sameVariant, "P1_dress", Scheme));
        Assert.Equal(PoolDerive.CoverageGroupId, g.SlotId);
        // 40 is the target's own to pose; 50 is what the target's own arm rescues
        Assert.Equal(new uint[] { 50 }, g.GroupBones);

        // …but that same part vouches for nothing to a target the wardrobe doesn't gate: the scenes it
        // dresses the other option in are exactly the ones it sits out
        var otherTarget = new[] { sameVariant[0], sameVariant[2], sameVariant[3] };
        Assert.Empty(Groups(otherTarget, "body", Scheme));

        // and an ungated member arms the pair for either target
        Assert.Single(Groups(PairRoster, "body", Scheme));
        Assert.Single(Groups(PairRoster.Append(Member("P1_dress", 21, new uint[] { 40 })).ToList(),
            "P1_dress", Scheme));
    }

    [Fact]
    public void Pair_bones_leave_out_what_the_pool_poses_and_the_unrecoverable_hash()
    {
        var roster = new[]
        {
            Body(10, 11, 40),
            Ctx("cloth_Fight", PresenceContext.Fight, new uint[] { 0, 10, 40 }),
            Ctx("cloth_Dorm", PresenceContext.Dorm, new uint[] { 0, 10, 40 }),
        };
        // 10 is the body's own, 0 is never an owner key
        Assert.Equal(new uint[] { 40 }, Assert.Single(Groups(roster, "body", Scheme)).GroupBones);
    }

    [Fact]
    public void A_bone_only_the_arms_table_is_covered_by_the_pair_all_the_same()
    {
        // the pair reads the pool's tables no more than a slot does: the arms are the bone's tablers, and
        // its palette slot is appended past the union rather than taken from it
        var roster = new[]
        {
            Body(10, 11),
            Ctx("cloth_Fight", PresenceContext.Fight, new uint[] { 40 }),
            Ctx("cloth_Dorm", PresenceContext.Dorm, new uint[] { 40 }),
        };
        Assert.Equal(new uint[] { 40 }, Assert.Single(Groups(roster, "body", Scheme)).GroupBones);
        Assert.Contains(40u, AssetExporter.ValidTailBones(roster, "body", Scheme));

        // the control: a bone only ONE arm poses is still covered by nothing — the scene dressing the other
        // side would leave it posed by no part at all, whoever tables it
        var lopsided = new[]
        {
            Body(10, 11),
            Ctx("cloth_Fight", PresenceContext.Fight, new uint[] { 40 }),
            Ctx("cloth_Dorm", PresenceContext.Dorm, new uint[] { 42 }),
        };
        Assert.Empty(Groups(lopsided, "body", Scheme));
    }

    [Fact]
    public void A_pair_forms_without_a_wardrobe_scheme()
    {
        // The target's own arm reads context parts, and the scenes are what state which one draws. A
        // non-modular outfit has no scheme at all, and that is the population this arm rescues.
        var g = Assert.Single(Groups(PairRoster, "body", null));
        Assert.Equal(PoolDerive.CoverageGroupId, g.SlotId);
        Assert.Equal(new uint[] { 40, 50 }, g.GroupBones);
        Assert.Equal(new[] { "cloth_Fight", "cloth_Dorm" }, g.Members.Select(p => p.Mesh));
    }

    [Fact]
    public void A_schemeless_roster_with_an_unlisted_wardrobe_part_forms_no_pair()
    {
        // The total kill precedes every source and reads no scheme itself: with nothing listing the
        // alternatives, a modular-shaped part is an option some scene may dress instead, and then neither
        // arm is what draws.
        var roster = PairRoster.Append(Member("P3_dress", PartPresence.UnknownVariant, new uint[] { 60 }))
            .ToList();
        Assert.Empty(Groups(roster, "body", null));
    }

    [Fact]
    public void A_wardrobe_part_the_scheme_does_not_list_forms_no_pair_either()
    {
        // The total kill precedes every source: an option nothing states the alternatives of can be the one
        // a scene dresses, and then neither arm is what draws.
        var roster = PairRoster.Append(Member("P3_dress", PartPresence.UnknownVariant, new uint[] { 60 }))
            .ToList();
        Assert.Empty(Groups(roster, "body", Scheme));
    }

    [Fact]
    public void A_bone_a_slot_and_the_scene_arms_both_certify_is_covered_once()
    {
        var roster = new[]
        {
            Body(10, 11, 40),
            Member("P1_dress", 21, new uint[] { 40 }),
            Member("P2_dress", 22, new uint[] { 40 }),
            Ctx("cloth_Fight", PresenceContext.Fight, new uint[] { 40 }),
            Ctx("cloth_Dorm", PresenceContext.Dorm, new uint[] { 40 }),
        };
        var (candidates, leftOut) = PoolDerive.PoolCandidates(roster, "body");
        var groups = PoolDerive.VariantGroups(roster, Scheme, Array.Empty<PoolDerive.MissingPart>(),
            candidates, "body");

        // two arms certify the same bone; the group carries it once, with every certifying part a member
        var g = Assert.Single(groups);
        Assert.Equal(new uint[] { 40 }, g.GroupBones);
        Assert.Equal(new[] { "P1_dress", "P2_dress", "cloth_Fight", "cloth_Dorm" },
            g.Members.Select(p => p.Mesh));

        // one palette row, every drawn member a writer of the same correct transform
        var r = PoolDerive.Derive(Donor(10, 40), candidates, missingParts: leftOut, replacedPart: "body",
            groups: groups);
        Assert.Equal(PoolDerive.CoverageGroupId, Assert.Single(r.GroupCovered).Value);
    }

    [Fact]
    public void The_posed_gate_admits_a_bone_the_pair_covers()
    {
        var (candidates, leftOut) = PoolDerive.PoolCandidates(PairRoster, "body");
        var groups = PoolDerive.VariantGroups(PairRoster, Scheme, Array.Empty<PoolDerive.MissingPart>(),
            candidates, "body");

        var r = PoolDerive.Derive(Donor(10, 40), candidates, missingParts: leftOut, replacedPart: "body",
            groups: groups);
        Assert.Equal(new[] { "body" }, r.Pool);
        Assert.Equal(PoolDerive.CoverageGroupId, Assert.Single(r.GroupCovered).Value);
        Assert.Equal(40u, r.GroupCovered.Keys.Single());

        // …and the same derive without it is the refusal it has always been
        var e = Assert.Throws<InvalidDataException>(() =>
            PoolDerive.Derive(Donor(10, 40), candidates, missingParts: leftOut, replacedPart: "body"));
        Assert.Contains("no part of this item moves", e.Message);
    }

    [Fact]
    public void A_tail_offers_the_bones_the_pair_covers()
    {
        // no part of this roster belongs to a scheme slot, so the offer is the pair's alone — and the pair
        // needs no scheme, so the offer stands with and without one
        Assert.Contains(40u, AssetExporter.ValidTailBones(PairRoster, "body"));
        Assert.Contains(40u, AssetExporter.ValidTailBones(PairRoster, "body", Scheme));
        Assert.Contains(50u, AssetExporter.ValidTailBones(PairRoster, "body", Scheme));

        // the control: a roster the pair can't form over offers nothing extra
        Assert.DoesNotContain(40u,
            AssetExporter.ValidTailBones(new[] { Body(10, 11, 40, 50), PairRoster[1] }, "body"));
    }

    // ---------------------------------------------------------------------------- bind agreement

    private const uint HRoot = 0x1111_1111, HShared = 0x3333_3333;

    private static MeshSkin SkinAt(params (uint Hash, float Y)[] bones) => new()
    {
        BoneHashes = bones.Select(b => b.Hash).ToArray(),
        BindPoses = bones.Select(b => Matrix4x4.CreateTranslation(0, -b.Y, 0)).ToList(),
    };

    private static IReadOnlyList<AssetExporter.SubjectBone> TwoVariantSubject(float p2Y) =>
        AssetExporter.SubjectSkeleton(
            new[]
            {
                (SkinAt((HRoot, 0f)), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                (SkinAt((HShared, 1.60f)), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                (SkinAt((HShared, p2Y)), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
            },
            _ => null, out _);

    /// <summary>A group bone is only as good as the place its members agree it stands. The subject skeleton
    /// drops a bone two parts bind apart, and BOTH export routes walk that skeleton, so a bone it dropped
    /// reaches no tail whatever the offer says.</summary>
    [Fact]
    public void A_bone_the_variants_bind_apart_is_not_offered_by_either_route()
    {
        var offer = new HashSet<uint> { HShared };
        var own = new[] { HRoot };
        var parts = new[] { new MeshGltf.RiggedPart(new UnityMesh { Name = "body_lod0" }, SkinAt((HRoot, 0f))) };

        // 4 cm apart in Y — plainly visible, and far above the placement tolerance
        var apart = TwoVariantSubject(1.64f);
        Assert.Empty(AssetExporter.ExtraBones(apart, own, uprighting: null, valid: offer));
        Assert.Empty(AssetExporter.CombinedExtraBones(apart, parts, offer));

        // siblings binding it identically place it once, and it is offered
        var agreed = TwoVariantSubject(1.60f);
        Assert.Equal(new[] { HShared },
            AssetExporter.ExtraBones(agreed, own, uprighting: null, valid: offer).Select(e => e.Hash));
        Assert.Equal(new[] { HShared },
            AssetExporter.CombinedExtraBones(agreed, parts, offer).Select(e => e.Hash));
    }
}
