using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="TimelineTemplates"/> — turning the tables' clip-name templates plus a model stem into the
/// timeline addresses worth probing. Pure: no tables and no catalog are read here.
/// </summary>
public class TimelineTemplatesTests
{
    [Theory]
    [InlineData("TestySSR0101", "Testy")]
    [InlineData("TestySR01", "Testy")]
    [InlineData("TestyR0102", "Testy")]
    [InlineData("TestyDorm99", "Testy")]
    [InlineData("TestySSR01", "Testy")]
    // a stem whose own name opens with a rarity letter still cuts at the LAST marker, not the first
    [InlineData("RollySSR0101", "Rolly")]
    // no marker at all: the stem is its own folder token
    [InlineData("Plainstem", "Plainstem")]
    public void The_folder_token_is_the_stem_up_to_its_rarity_marker(string stem, string expected) =>
        Assert.Equal(expected, TimelineTemplates.CharacterToken(stem));

    [Fact]
    public void A_template_becomes_an_address_under_the_characters_own_folder()
    {
        var templates = new TimelineTemplates(
            dorm: new[] { "c_{0}_Bedroom_01_Idle" },
            lobby: new[] { "Lobby_{0}_Idle_0_Loop" });

        var addresses = templates.AddressesFor("TestySSR0101");

        Assert.Contains(
            "Assets/ConfigPrefab/Nonbattle_Timeline/Dorm_Timeline/TestyDorm/c_TestySSR0101_Bedroom_01_Idle.prefab",
            addresses);
        Assert.Contains(
            "Assets/ConfigPrefab/Nonbattle_Timeline/Lobby_Timeline/TestyLobby/Lobby_TestySSR0101_Idle_0_Loop.prefab",
            addresses);
    }

    [Fact]
    public void The_wardrobe_change_clips_are_derived_without_any_template()
    {
        // the one list carrier no table template reaches, so it is built from the folder token alone
        var addresses = new TimelineTemplates(Array.Empty<string>(), Array.Empty<string>())
            .AddressesFor("TestySSR0101");

        Assert.Equal(new[]
        {
            "Assets/ConfigPrefab/Nonbattle_Timeline/Dorm_Timeline/TestyDorm/c_TestyDorm_Cloth_Before.prefab",
            "Assets/ConfigPrefab/Nonbattle_Timeline/Dorm_Timeline/TestyDorm/c_TestyDorm_Cloth_Idle.prefab",
            "Assets/ConfigPrefab/Nonbattle_Timeline/Dorm_Timeline/TestyDorm/c_TestyDorm_Cloth_After.prefab",
        }, addresses);
    }

    [Fact]
    public void Two_templates_formatting_alike_yield_one_address()
    {
        var addresses = new TimelineTemplates(
            dorm: new[] { "c_{0}_Bedroom_01_Idle", "c_{0}_Bedroom_01_Idle" },
            lobby: Array.Empty<string>()).AddressesFor("TestySSR0101");

        Assert.Single(addresses, a => a.EndsWith("c_TestySSR0101_Bedroom_01_Idle.prefab", StringComparison.Ordinal));
    }

    [Fact]
    public void A_template_the_game_authored_with_stray_braces_costs_no_addresses()
    {
        // an unformattable template contributes its literal self rather than throwing the whole build's
        // timeline read away
        var addresses = new TimelineTemplates(dorm: new[] { "c_{oops}_Idle" }, lobby: Array.Empty<string>())
            .AddressesFor("TestySSR0101");

        Assert.Contains(addresses, a => a.EndsWith("c_{oops}_Idle.prefab", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_stems_of_one_character_resolve_the_SAME_wardrobe_change_addresses()
    {
        // Why the matcher has to be stem-gated: the Cloth clips are addressed by the CHARACTER token, so
        // every outfit of a character probes — and reads — exactly the same three bundles. Anything the
        // entries in them demote is decided by the ENTRY text, never by which stem did the resolving.
        var t = new TimelineTemplates(Array.Empty<string>(), Array.Empty<string>());

        Assert.Equal(t.AddressesFor("TestySSR01"), t.AddressesFor("TestyDorm"));
    }

    // ---- the table read -------------------------------------------------------------------------------

    /// <summary>All four timeline tables on disk, each written under the name the game ships it by —
    /// including the misspelled <c>DromInteractData</c>. Any table can be left out to exercise the
    /// unreadable path.</summary>
    private static GameDatabase Db(TempGame g, bool dormFormation = true, bool dromInteract = true,
        bool lobbyActionList = true, bool lobbyAction = true)
    {
        string root = g.Root;
        if (dormFormation)
            root = g.WriteTable("DormFormationData", TempGame.TableBytes(new[]
            {
                TempGame.DormFormationRow(1, 700, 900, "c_{0}_Bedroom_01_Idle"),
                // #10 also carries already-resolved names: no placeholder, so not a template
                TempGame.DormFormationRow(2, 700, 901, "c_TestyDorm_Bedroom_Fixed"),
            }));
        if (dromInteract)
            root = g.WriteTable("DromInteractData", TempGame.TableBytes(new[]
            {
                TempGame.DromInteractRow(700, 1, "c_{0}_Interact_01"),
            }));
        if (lobbyActionList)
            root = g.WriteTable("LobbyActionListData", TempGame.TableBytes(new[]
            {
                TempGame.LobbyActionListRow(1, clip6: "Lobby_{0}_Idle_0_Loop",
                    clip7: "Lobby_{0}_Enter", clip19: "Lobby_{0}_Exit"),
                // a field that is NOT read must contribute nothing, however template-shaped it looks
                TempGame.LobbyActionListRow(2, clip6: null, clip7: null, clip19: null),
            }));
        if (lobbyAction)
            root = g.WriteTable("LobbyActionData", TempGame.TableBytes(new[]
            {
                TempGame.LobbyActionRow(1, "Lobby_{0}_Wave"),
            }));
        return GameDatabase.FromGameDir(root);
    }

    [Fact]
    public void Load_reads_each_tables_own_clip_fields_under_the_name_the_game_ships()
    {
        // PINS THE SCHEMA: the four table names — including the game's own "Drom" misspelling — and the
        // field numbers under each. A well-meaning correction to any of them fails here rather than
        // silently costing the timelines it would have found.
        using var g = new TempGame();

        var t = TimelineTemplates.Load(Db(g));

        Assert.Equal(new[] { "c_{0}_Bedroom_01_Idle", "c_{0}_Interact_01" }, t.Dorm);
        Assert.Equal(
            new[] { "Lobby_{0}_Enter", "Lobby_{0}_Exit", "Lobby_{0}_Idle_0_Loop", "Lobby_{0}_Wave" },
            t.Lobby);
    }

    [Fact]
    public void A_value_carrying_no_placeholder_is_not_a_template()
    {
        // The same fields carry already-resolved names and unrelated scenery ids, so the `{0}` is what
        // makes a value a template. The fixture's fixed name sits in the very field the idle one does.
        using var g = new TempGame();

        var t = TimelineTemplates.Load(Db(g));

        Assert.DoesNotContain("c_TestyDorm_Bedroom_Fixed", t.Dorm);
    }

    [Theory]
    [InlineData("DormFormationData")]
    [InlineData("DromInteractData")]
    [InlineData("LobbyActionListData")]
    [InlineData("LobbyActionData")]
    public void A_table_that_cannot_be_read_fails_the_load(string missing)
    {
        // The failure must REACH the caller: the app's "timeline tables unreadable, pools stay
        // conservative" line is the one place that decides what an unreadable table costs, and swallowing
        // the read here would leave that line unreachable and the load quietly short of templates.
        using var g = new TempGame();
        var db = Db(g,
            dormFormation: missing != "DormFormationData",
            dromInteract: missing != "DromInteractData",
            lobbyActionList: missing != "LobbyActionListData",
            lobbyAction: missing != "LobbyActionData");

        Assert.ThrowsAny<IOException>(() => TimelineTemplates.Load(db));
    }

    [Fact]
    public void The_templates_a_readable_table_carries_survive_a_row_it_cannot_decode()
    {
        // Per-ROW tolerance is kept: one row the reader can make nothing of costs only the timelines it
        // would have carried, where a malformed TABLE costs the load. Here field #10 holds bytes that are
        // not decodable UTF-8, which is a row shape the game's own data can produce.
        using var g = new TempGame();
        g.WriteTable("DormFormationData", TempGame.TableBytes(new[]
        {
            TempGame.DormFormationRow(1, 700, 900, "c_{0}_Bedroom_01_Idle"),
            new byte[] { 0x52, 0x02, 0xFF, 0xFF },   // field #10, two bytes, invalid UTF-8
        }));
        g.WriteTable("DromInteractData", TempGame.TableBytes(Array.Empty<byte[]>()));
        g.WriteTable("LobbyActionListData", TempGame.TableBytes(Array.Empty<byte[]>()));
        var root = g.WriteTable("LobbyActionData", TempGame.TableBytes(Array.Empty<byte[]>()));

        var t = TimelineTemplates.Load(GameDatabase.FromGameDir(root));

        Assert.Contains("c_{0}_Bedroom_01_Idle", t.Dorm);
    }
}
