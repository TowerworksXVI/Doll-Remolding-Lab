using System;
using System.Collections.Generic;
using Remold.Core.Model;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="ShoeNodeMatch"/> — which parts one timeline show/hide entry reaches. The entries are authored
/// strings and the game resolves them two ways, so both rules are pinned here, along with the authored
/// entries that resolve NEITHER way and do nothing in the game either.
/// </summary>
public class ShoeNodeMatchTests
{
    private const string Stem = "TestySSR0101";
    private const string Prefix = "c_" + Stem + "_slg_";

    private static readonly IReadOnlySet<string> Tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "P1_coat2", "P2_coat2", "P1_cloth1", "P1_body",
    };

    [Fact]
    public void An_entry_naming_a_plain_node_exactly_reaches_that_part()
    {
        Assert.True(ShoeNodeMatch.Matches(Prefix + "cloth1_lod0_Dorm",
            slotName: Prefix + "cloth1_lod0_Dorm", partToken: "cloth1_Dorm", Tokens, Stem));

        // a different node of the same outfit is untouched
        Assert.False(ShoeNodeMatch.Matches(Prefix + "cloth1_lod0_Dorm",
            slotName: Prefix + "cloth2_lod0_Dorm", partToken: "cloth2_Dorm", Tokens, Stem));
    }

    [Fact]
    public void An_entry_carrying_the_modular_seam_never_matches_by_exact_name()
    {
        // The game's direct-child lookup skips these, so an entry naming a modular node exactly still
        // reaches nothing through the first rule. Here the token rule can't save it either: the part's
        // base token is not in the selector's set.
        Assert.False(ShoeNodeMatch.Matches(Prefix + "P4_coat9",
            slotName: Prefix + "P4_coat9", partToken: "P4_coat9", Tokens, Stem));
    }

    [Fact]
    public void An_entry_ending_in_a_bare_resource_token_reaches_that_tokens_base_container()
    {
        // the shape that suppresses a worn container outright
        Assert.True(ShoeNodeMatch.Matches(Prefix + "P1_coat2",
            slotName: Prefix + "P1_coat2_lod0", partToken: "P1_coat2", Tokens, Stem));

        // a token the selector does not hold reaches nothing, so a same-shaped name on a non-modular
        // outfit part is left alone
        Assert.False(ShoeNodeMatch.Matches(Prefix + "P1_coat9",
            slotName: Prefix + "P1_coat9_lod0", partToken: "P1_coat9", Tokens, Stem));
    }

    [Fact]
    public void An_entry_ending_in_a_token_and_a_context_tail_reaches_that_tokens_twins()
    {
        // the context-flip shape: one container holds a token's Dorm and Fight twins, and the entry's own
        // tail is what picks between them, so both twins are reachable
        Assert.True(ShoeNodeMatch.Matches(Prefix + "P1_cloth1_Dorm",
            slotName: Prefix + "P1_cloth1_lod0_Dorm", partToken: "P1_cloth1_Dorm", Tokens, Stem));
        Assert.True(ShoeNodeMatch.Matches(Prefix + "P1_cloth1_Fight",
            slotName: Prefix + "P1_cloth1_lod0_Fight", partToken: "P1_cloth1_Fight", Tokens, Stem));
    }

    [Fact]
    public void An_authored_entry_with_a_lod_token_inside_a_modular_name_is_inert()
    {
        // The seam rules it out of the exact lookup, and the trailing LOD token keeps it off the end of any
        // resource token, so it reaches nothing. This shape really is authored, and it does nothing in the
        // game either.
        Assert.False(ShoeNodeMatch.Matches(Prefix + "P1_body_lod0_Dorm",
            slotName: Prefix + "P1_body_lod0_Dorm", partToken: "P1_body_Dorm", Tokens, Stem));
    }

    [Fact]
    public void Without_a_resource_token_set_only_the_exact_rule_can_fire()
    {
        // No wardrobe scheme is the conservative direction: an entry that would have reached the selector
        // reaches nothing instead.
        Assert.False(ShoeNodeMatch.Matches(Prefix + "P1_coat2",
            slotName: Prefix + "P1_coat2_lod0", partToken: "P1_coat2", resourceTokens: null, Stem));

        Assert.True(ShoeNodeMatch.Matches(Prefix + "cloth1_lod0",
            slotName: Prefix + "cloth1_lod0", partToken: "cloth1", resourceTokens: null, Stem));
    }

    [Fact]
    public void MatchesAny_is_true_when_one_entry_of_a_list_reaches_the_part()
    {
        var entries = new[] { Prefix + "cloth9_lod0", Prefix + "P1_coat2", Prefix + "cloth8_lod0" };
        Assert.True(ShoeNodeMatch.MatchesAny(entries, Prefix + "P1_coat2_lod0", "P1_coat2", Tokens, Stem));
        Assert.False(ShoeNodeMatch.MatchesAny(entries, Prefix + "cloth1_lod0", "cloth1", Tokens, Stem));
        Assert.False(ShoeNodeMatch.MatchesAny(Array.Empty<string>(), Prefix + "cloth1_lod0", "cloth1", Tokens, Stem));
    }

    [Theory]
    [InlineData("c_X_slg_P1_body_lod0", true)]
    [InlineData("P2_coat1", true)]
    [InlineData("c_X_slg_cloth1_lod0", false)]
    [InlineData("c_X_slg_body_P_lod0", false)]   // no digits after the P
    public void The_modular_seam_is_the_P_digit_segment(string name, bool expected) =>
        Assert.Equal(expected, ShoeNodeMatch.CarriesModularSeam(name));

    // ---- stem scoping -------------------------------------------------------------------------------

    [Theory]
    [InlineData("c_TestySSR0101_slg_P1_coat2", "TestySSR0101")]
    [InlineData("c_TestyDorm_Cloth_Idle", "TestyDorm")]      // no _slg_ segment: still stem then tail
    [InlineData("P1_coat2", null)]                            // a bare token names no stem
    [InlineData("Lobby_Testy_Idle", null)]                    // not the c_ shape at all
    [InlineData("c_Testy", null)]                             // prefix but no tail to close the stem
    public void The_stem_an_entry_names_is_the_segment_after_its_c_prefix(string entry, string? expected) =>
        Assert.Equal(expected, ShoeNodeMatch.EntryStem(entry));

    [Fact]
    public void An_entry_naming_another_stem_of_the_same_character_reaches_nothing()
    {
        // THE CROSS-CONTAMINATION SHAPE. The wardrobe-change clips are addressed by CHARACTER token, so
        // every stem of one character resolves the same Cloth bundles and reads the others' entries. The
        // entry here names stem B and ends in a bare modular token that stem A also carries — without the
        // stem gate it would demote A's part on the strength of B's clip.
        const string stemA = "TestySSR0101", stemB = "TestyDorm";
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P1_coat2" };
        string entryNamingB = $"c_{stemB}_slg_P1_coat2";

        // stem A wears a part with that very token, and is left alone
        Assert.False(ShoeNodeMatch.Matches(entryNamingB,
            slotName: $"c_{stemA}_slg_P1_coat2_lod0", partToken: "P1_coat2", tokens, stemA));

        // …while the stem the entry actually names IS demoted, so the gate scopes rather than disables
        Assert.True(ShoeNodeMatch.Matches(entryNamingB,
            slotName: $"c_{stemB}_slg_P1_coat2_lod0", partToken: "P1_coat2", tokens, stemB));
    }

    [Fact]
    public void An_entry_carrying_no_stem_prefix_still_reaches_the_token_it_names()
    {
        // The conservative direction: an authored entry this can't attribute to a stem keeps matching,
        // so the gate never turns a demotion the game really makes into a miss.
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P1_coat2" };
        Assert.True(ShoeNodeMatch.Matches("Some_P1_coat2",
            slotName: Prefix + "P1_coat2_lod0", partToken: "P1_coat2", tokens, Stem));
    }

    [Fact]
    public void A_caller_that_does_not_know_the_stem_leaves_the_token_rule_ungated()
    {
        // Null stem = the old, wider behaviour. It demotes MORE, never less, so an unknowing caller
        // stays on the safe side of the rule.
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "P1_coat2" };
        Assert.True(ShoeNodeMatch.Matches("c_TestyDorm_slg_P1_coat2",
            slotName: Prefix + "P1_coat2_lod0", partToken: "P1_coat2", tokens, stem: null));
    }

    // ---- token boundary -----------------------------------------------------------------------------

    [Fact]
    public void An_entry_aimed_at_a_longer_token_does_not_reach_the_token_that_ends_it()
    {
        // MEASURED SHAPE: `Coat` is an underscore-delimited suffix of `Top_Coat`, so an entry ending at
        // the longer token's container ends the shorter one's tail too. The game keys the container by
        // the longest token the entry actually ends with, so only that one is reached.
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Top_Coat", "Coat" };
        string entry = Prefix + "Top_Coat";

        Assert.False(ShoeNodeMatch.Matches(entry,
            slotName: Prefix + "Coat_lod0", partToken: "Coat", tokens, Stem));
        Assert.True(ShoeNodeMatch.Matches(entry,
            slotName: Prefix + "Top_Coat_lod0", partToken: "Top_Coat", tokens, Stem));
    }

    [Fact]
    public void The_shorter_token_is_reached_when_no_longer_token_explains_the_entry()
    {
        // The control for the test above: the SAME token set and the same short token, with an entry
        // that ends at `Coat` and not at `Top_Coat`. So it is the longer token's rival match alone that
        // decided the rejection, not the presence of `Top_Coat` in the set.
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Top_Coat", "Coat" };

        Assert.True(ShoeNodeMatch.Matches(Prefix + "Under_Coat",
            slotName: Prefix + "Coat_lod0", partToken: "Coat", tokens, Stem));
    }

    [Fact]
    public void The_longest_token_wins_through_a_context_tail_too()
    {
        // The same boundary on the twin-container shape, so a context-tagged entry can't slip past it.
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Top_Coat", "Coat" };
        string entry = Prefix + "Top_Coat_Dorm";

        Assert.False(ShoeNodeMatch.Matches(entry,
            slotName: Prefix + "Coat_lod0_Dorm", partToken: "Coat_Dorm", tokens, Stem));
        Assert.True(ShoeNodeMatch.Matches(entry,
            slotName: Prefix + "Top_Coat_lod0_Dorm", partToken: "Top_Coat_Dorm", tokens, Stem));
    }
}
