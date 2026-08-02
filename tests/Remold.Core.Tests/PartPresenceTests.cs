using System.Collections.Generic;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Tables;
using Xunit;
using static Remold.Core.Tests.Support.PoolFixtures;

namespace Remold.Core.Tests;

/// <summary>
/// Part presence: classification of tokens against a wardrobe scheme, the coverage relation, and the
/// candidacy filter it drives in <see cref="PoolDerive.PoolCandidates"/>.
/// </summary>
public class PartPresenceTests
{
    // A two-slot scheme: a single-token slot (variants 11/12) and a married-pair slot (21/22). Token
    // shapes mirror the shipped corpus: bare, indexed (P1_body1 beside P1_body), and suffixed parts
    // that must land on their base resource by longest match.
    private static readonly IReadOnlyList<PartScheme.Slot> Scheme = new[]
    {
        new PartScheme.Slot(1, new[]
        {
            new PartScheme.Variant(11, true, new[] { "P1_head" }),
            new PartScheme.Variant(12, false, new[] { "P2_head" }),
        }),
        new PartScheme.Slot(2, new[]
        {
            new PartScheme.Variant(21, true, new[] { "P1_body", "P1_body1" }),
            new PartScheme.Variant(22, false, new[] { "P2_body" }),
        }),
    };

    [Theory]
    [InlineData("body", PresenceContext.Always, PartPresence.NoVariant)]
    [InlineData("cloth1_Fight", PresenceContext.Fight, PartPresence.NoVariant)]
    [InlineData("cloth3_Dorm", PresenceContext.Dorm, PartPresence.NoVariant)]
    [InlineData("hair_Dorm", PresenceContext.Dorm, PartPresence.NoVariant)]     // tail read off the token, any suffix order
    [InlineData("P1_head", PresenceContext.Always, 11L)]
    [InlineData("P1_body", PresenceContext.Always, 21L)]
    [InlineData("P1_body1", PresenceContext.Always, 21L)]                       // longest match, not P1_body's prefix
    [InlineData("P1_body1_Fight", PresenceContext.Fight, 21L)]
    [InlineData("P2_body_Dorm", PresenceContext.Dorm, 22L)]
    [InlineData("P1_body2_trans", PresenceContext.Always, 21L)]                 // suffixed part lands on its base resource
    [InlineData("P3_body", PresenceContext.Always, PartPresence.UnknownVariant)] // wardrobe-shaped, not in the scheme
    public void Classify_ReadsContextTail_AndWardrobeVariant(string token, PresenceContext ctx, long variant)
    {
        Assert.Equal(new PartPresence(ctx, variant), PartPresence.Classify(token, Scheme));
    }

    [Fact]
    public void Classify_WithoutAScheme_MarksWardrobeShapedTokensUnknown()
    {
        Assert.Equal(new PartPresence(PresenceContext.Always, PartPresence.UnknownVariant),
            PartPresence.Classify("P1_body", null));
        Assert.Equal(PartPresence.Always, PartPresence.Classify("body", null));
        Assert.Equal(PartPresence.Always, PartPresence.Classify("Pearl_cloth", null));   // P without digits+underscore
    }

    [Theory]
    // unconditional covers everything
    [InlineData(PresenceContext.Always, 0L, PresenceContext.Fight, 21L, true)]
    // worn variant's base covers its own context siblings, never another variant
    [InlineData(PresenceContext.Always, 21L, PresenceContext.Fight, 21L, true)]
    [InlineData(PresenceContext.Always, 21L, PresenceContext.Always, 22L, false)]
    // a context part covers only its own context
    [InlineData(PresenceContext.Fight, 0L, PresenceContext.Fight, 21L, true)]
    [InlineData(PresenceContext.Fight, 0L, PresenceContext.Always, 0L, false)]
    [InlineData(PresenceContext.Fight, 21L, PresenceContext.Always, 21L, false)]
    // unknown vouches for nothing, and is covered only by the unconditional
    [InlineData(PresenceContext.Always, -1L, PresenceContext.Always, -1L, false)]
    [InlineData(PresenceContext.Always, 0L, PresenceContext.Always, -1L, true)]
    public void Covers_IsPresenceImplication(PresenceContext sc, long sv, PresenceContext tc, long tv, bool expect)
    {
        Assert.Equal(expect, new PartPresence(sc, sv).Covers(new PartPresence(tc, tv)));
    }

    // ---- the candidacy filter -------------------------------------------------------------------

    private static readonly PartPresence Var21 = new(PresenceContext.Always, 21);
    private static readonly PartPresence Var22 = new(PresenceContext.Always, 22);

    private static readonly IReadOnlyList<PoolDerive.PartBones> Roster = new[]
    {
        Part("body", PartPresence.Always, 1, 2),
        Part("cloth1_Fight", new PartPresence(PresenceContext.Fight, PartPresence.NoVariant), 1, 3),
        Part("P1_body", Var21, 1, 4),
        Part("P1_body1", Var21, 1, 7),
        Part("P1_body_Fight", new PartPresence(PresenceContext.Fight, 21), 1, 5),
        Part("P2_body", Var22, 1, 4),
        Part("P9_new", new PartPresence(PresenceContext.Always, PartPresence.UnknownVariant), 1, 6),
    };

    [Fact]
    public void An_always_on_target_pools_only_always_on_parts()
    {
        var (candidates, excluded) = PoolDerive.PoolCandidates(Roster, "body");
        Assert.Equal(new[] { "body" }, candidates.Select(p => p.Mesh));
        Assert.Equal(new[] { "cloth1_Fight", "P1_body", "P1_body1", "P1_body_Fight", "P2_body", "P9_new" },
            excluded.Select(m => m.Mesh));
        Assert.Contains("only in combat", excluded[0].Why);
        Assert.Contains("wardrobe option", excluded[1].Why);
        Assert.Contains("isn't in the game's tables", excluded[5].Why);
    }

    [Fact]
    public void A_wardrobe_target_pools_its_own_variant_and_the_always_on()
    {
        var (candidates, excluded) = PoolDerive.PoolCandidates(Roster, "P1_body");
        Assert.Equal(new[] { "body", "P1_body", "P1_body1" }, candidates.Select(p => p.Mesh));
        // the same-variant Fight sibling still isn't co-drawn everywhere the base is
        Assert.Contains(excluded, m => m.Mesh == "P1_body_Fight");
        Assert.Contains(excluded, m => m.Mesh == "P2_body");
    }

    [Fact]
    public void A_context_target_pools_its_context_and_the_always_on()
    {
        var (candidates, _) = PoolDerive.PoolCandidates(Roster, "cloth1_Fight");
        Assert.Equal(new[] { "body", "cloth1_Fight" }, candidates.Select(p => p.Mesh));
    }

    [Fact]
    public void A_wardrobe_context_target_pools_base_variant_context_and_itself()
    {
        var (candidates, _) = PoolDerive.PoolCandidates(Roster, "P1_body_Fight");
        Assert.Equal(new[] { "body", "cloth1_Fight", "P1_body", "P1_body1", "P1_body_Fight" },
            candidates.Select(p => p.Mesh));
    }

    [Fact]
    public void An_unknown_target_is_admitted_and_pools_only_the_unconditional()
    {
        var (candidates, _) = PoolDerive.PoolCandidates(Roster, "P9_new");
        Assert.Equal(new[] { "body", "P9_new" }, candidates.Select(p => p.Mesh));
    }

    [Fact]
    public void Presence_exclusions_name_the_part_in_an_orphan_bone_refusal()
    {
        // Hash 4 is owned only by wardrobe options. A body donor riding it is told which parts sat out
        // and why, the same channel the narrow rule reports through.
        var (candidates, excluded) = PoolDerive.PoolCandidates(Roster, "body");
        var e = Assert.Throws<System.IO.InvalidDataException>(() =>
            PoolDerive.Derive(Donor(1, 4), candidates, missingParts: excluded, replacedPart: "body"));
        Assert.Contains("'P1_body' · it is a wardrobe option", e.Message);
    }
}
