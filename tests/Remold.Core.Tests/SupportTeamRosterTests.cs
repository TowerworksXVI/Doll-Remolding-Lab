using System.Collections.Generic;
using System.Linq;
using Remold.Core.Model;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The support-team roster over synthetic <c>SupportData</c> rows: dedupe by stem, the primary row's id and
/// name win, member and weapon outfits carry their own routes, and the synthetic ids sit in their own band.
/// </summary>
public class SupportTeamRosterTests
{
    /// <summary>A SupportData row: #1 = id, #2 = {#1 = name text-id}, #13 = stem, #19 = primary flag.</summary>
    private static byte[] Row(long id, string? stem, long? nameTextId = null, bool primary = false)
    {
        var m = Pb.Msg().Varint(1, id);
        if (nameTextId is not null) m.Sub(2, Pb.Msg().Varint(1, nameTextId.Value));
        if (stem is not null) m.Str(13, stem);
        if (primary) m.Varint(19, 1);
        return m.ToArray();
    }

    private static SupportTeamRoster.TeamRow Team(long id, string stem, string? name = null, bool primary = false) =>
        new(id, stem, name, primary);

    [Fact]
    public void BuildTeams_DedupesByStem_PrimaryRowWins_ElseLowestId()
    {
        var teams = SupportTeamRoster.BuildTeams(new[]
        {
            Team(9001, "TEAM01", "Variant copy"),             // a mode variant of the same stem, lower id but not primary
            Team(1001, "TEAM01", "First Team", primary: true),
            Team(1101, "TEAM01", "Another copy"),
            Team(2202, "TEAM02", "Late copy"),                 // no primary row at all: the lowest id wins
            Team(1002, "TEAM02", "Second Team"),
        });

        Assert.Equal(2, teams.Count);
        var first = teams.Single(t => t.Name == "TEAM01");
        Assert.Equal(1001, first.CharId);
        Assert.Equal("First Team", first.DisplayName);
        var second = teams.Single(t => t.Name == "TEAM02");
        Assert.Equal(1002, second.CharId);
        Assert.Equal("Second Team", second.DisplayName);
    }

    [Fact]
    public void BuildTeams_OneCharacterPerTeam_ThreeMembersAndTheWeapon()
    {
        var team = SupportTeamRoster.BuildTeams(new[] { Team(1001, "TEAM01", "First Team") }).Single();

        Assert.Equal("TEAM01", team.Name);
        Assert.Equal(new[] { "TEAM01_MemberA", "TEAM01_MemberB", "TEAM01_MemberC", "TEAM01_WL" },
            team.Outfits.Select(o => o.Stem).ToArray());
        Assert.Equal(new[] { "Member A", "Member B", "Member C", "Weapon" }, team.Outfits.Select(o => o.DisplayName).ToArray());
        Assert.All(team.Outfits, o => Assert.Equal(OutfitKind.Other, o.Kind));
        // members are on screen together, which is what lets a build warn when an edit reaches two of them
        Assert.True(team.OutfitsAppearTogether);

        var a = team.Outfits[0];
        Assert.Equal("c_TEAM01_MemberA_slg_", a.MeshPrefix);
        Assert.Equal("Assets/ConfigPrefab/Character/SupportTeam/TEAM01/TEAM01_MemberA.prefab", a.Route!.Address);
        Assert.Equal("TEAM01_MemberA", a.Route.RootName);   // stem == container root, so the root-name ownership rule claims every slot
        Assert.False(a.PartsPoolAlone);

        var weapon = team.Outfits[3];
        Assert.Equal("cw_TEAM01_", weapon.MeshPrefix);
        Assert.Equal("Assets/ConfigPrefab/Weapon/SupportTeam/TEAM01/TEAM01_WL.prefab", weapon.Route!.Address);
        Assert.Equal("TEAM01_WL", weapon.Route.RootName);
        Assert.True(weapon.PartsPoolAlone);
    }

    [Fact]
    public void BuildTeams_IdsAreNegative_InTheirOwnBand_AndDistinct()
    {
        var teams = SupportTeamRoster.BuildTeams(new[] { Team(1001, "TEAM01"), Team(1002, "TEAM02") });
        var ids = teams.SelectMany(t => t.Outfits).Select(o => o.ModelConfigId).ToList();

        Assert.Equal(8, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id <= -SupportTeamRoster.IdBand, $"{id} is outside the team band"));
        // the curated skins own the small negatives; the band starts far below them
        Assert.All(ids, id => Assert.True(id < -1000, $"{id} could collide with a curated skin id"));
        Assert.Equal(-(SupportTeamRoster.IdBand + 1001 * 10 + 0), teams.Single(t => t.Name == "TEAM01").Outfits[0].ModelConfigId);
        Assert.Equal(-(SupportTeamRoster.IdBand + 1001 * 10 + 3), teams.Single(t => t.Name == "TEAM01").Outfits[3].ModelConfigId);
    }

    [Fact]
    public void BuildTeams_SortsByDisplayLabel_FallingBackToStem()
    {
        var teams = SupportTeamRoster.BuildTeams(new[]
        {
            Team(1001, "ZZZ01", "Alpha Team"),
            Team(1002, "AAA01"),               // nameless → sorts by its stem
            Team(1003, "MMM01", "Middle Team"),
        });
        Assert.Equal(new[] { "AAA01", "ZZZ01", "MMM01" }, teams.Select(t => t.Name).ToArray());
        Assert.Null(teams[0].DisplayName);
    }

    [Fact]
    public void IsMemberStem_NamesTheThreeMembers_NotTheWeapon()
    {
        Assert.True(SupportTeamRoster.IsMemberStem("TEAM01_MemberA"));
        Assert.True(SupportTeamRoster.IsMemberStem("team01_memberc"));
        Assert.False(SupportTeamRoster.IsMemberStem("TEAM01_WL"));
        Assert.False(SupportTeamRoster.IsMemberStem("TEAM01"));
    }

    [Fact]
    public void ReadRows_DecodesTheTable_ResolvesNames_SkipsRowsWithoutIdOrStem()
    {
        using var g = new TempGame();
        g.WriteTable("SupportData", TempGame.TableBytes(new[]
        {
            Row(1001, "TEAM01", nameTextId: 501, primary: true),
            Row(1002, "TEAM02", nameTextId: 999),   // text-id absent from the locale table → nameless
            Row(1003, null),                        // no stem → skipped
            Row(1004, "   "),                       // blank stem → skipped
        }));
        g.WriteTable("LangPackageTableEnusData", TempGame.TableBytes(new[]
        {
            TempGame.LangRow(501, "First Team"),
        }));
        var db = new GameDatabase(g.At(@"GF2_Exilium_Data\LocalCache\Data\Table"));
        var loc = LocalizationDb.Load(db);

        var rows = SupportTeamRoster.ReadRows(db, loc);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new SupportTeamRoster.TeamRow(1001, "TEAM01", "First Team", true), rows[0]);
        Assert.Equal(new SupportTeamRoster.TeamRow(1002, "TEAM02", null, false), rows[1]);

        // and the whole read: nameless teams label by stem in the tree, through the null display name
        var teams = SupportTeamRoster.ReadTeams(db, loc);
        Assert.Equal("First Team", teams.Single(t => t.Name == "TEAM01").DisplayName);
        Assert.Null(teams.Single(t => t.Name == "TEAM02").DisplayName);
    }

    [Fact]
    public void ReadRows_WithoutLocalization_LeavesEveryTeamNameless()
    {
        using var g = new TempGame();
        g.WriteTable("SupportData", TempGame.TableBytes(new[] { Row(1001, "TEAM01", nameTextId: 501) }));
        var db = new GameDatabase(g.At(@"GF2_Exilium_Data\LocalCache\Data\Table"));

        var teams = SupportTeamRoster.ReadTeams(db, loc: null);

        Assert.Null(teams.Single().DisplayName);
    }
}
