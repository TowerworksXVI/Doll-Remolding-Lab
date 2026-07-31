using System.Linq;
using Remold.Core.Model;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Reading the design DB into a roster, driven by synthetic <c>Table/*.bytes</c> built from the field
/// numbers and ID schemes — never real game tables. Worked example: Vesna, GunId 1071 (base=GunId,
/// alt=GunId·100+NN, dorm=GunId·100+99), plus one summon that BattleSummonedData references and whose
/// stem prefix is Vesna's base stem.
/// </summary>
public class TableRosterTests
{
    // Builds the standard three-table fixture and returns an open GameDatabase over it.
    private static GameDatabase BuildDb(TempGame g)
    {
        var chars = new[]
        {
            TempGame.GunCharRow(charId: 42, name: "Vesna", family: "VesnaSSR", gunId: 1071, dormCfg: 107199),
            TempGame.GunCharRow(charId: 7,  name: "Neris", family: "NerisSR",  gunId: 2,    dormCfg: 0),
            TempGame.GunCharRow(charId: 17, name: "Junia", family: "",         gunId: 101,  dormCfg: 0), // no model rows
            TempGame.GunCharRowRaw(charId: 5, name: null,    gunId: 9),    // no name → skipped
            TempGame.GunCharRowRaw(charId: 6, name: "Ghost", gunId: null), // no GunId → skipped
        };
        var models = new[]
        {
            TempGame.ModelConfigRow(1071,   "VesnaSSR01"),         // base
            TempGame.ModelConfigRow(107101, "VesnaSSR0101"),       // alt NN=01
            TempGame.ModelConfigRow(107150, "VesnaSSR0150"),       // alt NN=50
            TempGame.ModelConfigRow(107199, "VesnaDorm"),          // dorm (NN=99)
            TempGame.ModelConfigRow(10711,  "VesnaSSR01_Summon"),  // summon
            TempGame.ModelConfigRow(2,      "NerisSR01"),          // Neris base only
        };
        var battles = new[] { TempGame.BattleSummonedRow(1, 10711, null) };
        g.WriteTable("GunCharacterData", TempGame.TableBytes(chars));
        g.WriteTable("BattleSummonedData", TempGame.TableBytes(battles));
        var root = g.WriteTable("ModelConfigData", TempGame.TableBytes(models));
        return GameDatabase.FromGameDir(root);
    }

    // ---- the enemy roster's pure grouping/naming (GameDatabase.BuildEnemyRoster; the IO half reads
    // EnemyData #1/#17/#40/#23) ----
    [Fact]
    public void BuildEnemyRoster_GroupsByStem_VotesNames_ExcludesAndDropsUnjoined()
    {
        var stems = new System.Collections.Generic.Dictionary<long, string>
        {
            [6143] = "FB_Commander",
            [6246] = "ELID_Nemertea",
            [6248] = "ELID_Nemertea",          // two ModelConfig rows share one stem → one subject, lowest id
            [10651] = "SorelSSR01_Summon",     // playable-roster stem → excluded
        };
        var rows = new (long, long, string?)[]
        {
            (1, 6143, "Commander"),
            (2, 6143, "Commander (Elite)"),
            (3, 6143, "Commander"),            // vote: "Commander" ×2 wins
            (4, 6248, "Nemertea"),             // higher id first — the lower one must still win the outfit id
            (5, 6246, null),                   // nameless row still counts the model
            (6, 6246, "Nemertea"),
            (7, 9999, "Ghost"),                // no ModelConfig stem → dropped silently
            (8, 10651, "Talos"),               // excluded stem → dropped
        };
        var roster = GameDatabase.BuildEnemyRoster(rows, stems,
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "SorelSSR01_Summon" });

        Assert.Equal(2, roster.Count);

        var cmd = roster.Single(c => c.Name == "FB_Commander");
        Assert.Equal("Commander", cmd.DisplayName);            // most frequent name wins
        var cmdOutfit = Assert.Single(cmd.Outfits);
        Assert.Equal(6143, cmdOutfit.ModelConfigId);
        Assert.Equal(OutfitKind.Other, cmdOutfit.Kind);
        Assert.Equal("c_FB_Commander_", cmdOutfit.MeshPrefix); // the SHORT enemy prefix: LODs fold to one part

        var nem = roster.Single(c => c.Name == "ELID_Nemertea");
        Assert.Equal("Nemertea", nem.DisplayName);
        Assert.Equal(6246, Assert.Single(nem.Outfits).ModelConfigId);   // lowest id wins whatever order
    }

    [Fact]
    public void BuildEnemyRoster_TieBreaks_FirstSeenName_AndSortsByLabel()
    {
        var stems = new System.Collections.Generic.Dictionary<long, string> { [1] = "B_Stem", [2] = "A_Stem" };
        var rows = new (long, long, string?)[]
        {
            (1, 1, "Zeta"), (2, 1, "Alpha"),   // 1-1 tie → first-seen ("Zeta") wins deterministically
            (3, 2, null),                      // model with no named row → DisplayName null, labels off the stem
        };
        var roster = GameDatabase.BuildEnemyRoster(rows, stems);

        Assert.Equal(2, roster.Count);
        Assert.Equal("A_Stem", roster[0].Name);       // sorted by label (stem fallback for the nameless)
        Assert.Null(roster[0].DisplayName);
        Assert.Equal("Zeta", roster[1].DisplayName);
    }

    [Fact]
    public void ReadRows_SkipsHeaderAndCount_ReturnsEveryRow()
    {
        using var g = new TempGame();
        var chars = new[]
        {
            TempGame.GunCharRow(1, "A", "AF", 10, 0),
            TempGame.GunCharRow(2, "B", "BF", 20, 0),
        };
        var root = g.WriteTable("GunCharacterData", TempGame.TableBytes(chars, headerWord: 0x1234, withCount: true));

        var rows = TableFile.ReadRows(System.IO.Path.Combine(GameDatabase.ResolveTableRoot(root), "GunCharacterData.bytes"));

        Assert.Equal(2, rows.Count);                 // the leading varint count is NOT a row
        Assert.Equal("A", rows[0].Str(3));
        Assert.Equal(20UL, rows[1].Num(9));
    }

    [Fact]
    public void ReadRoster_DropsMalformedRows_AndSortsByName()
    {
        using var g = new TempGame();
        var roster = BuildDb(g).ReadRoster();

        Assert.Equal(new[] { "Junia", "Neris", "Vesna" }, roster.Select(c => c.Name).ToArray());
        Assert.DoesNotContain(roster, c => c.Name == "Ghost");
    }

    [Fact]
    public void ReadRoster_KeepsANamedRowThatResolvesNoOutfits()
    {
        // the table read reports what the table says; deciding a subject has no model is the
        // confirm-fill's job, and dropping it here would hide the row from that check
        using var g = new TempGame();
        var db = BuildDb(g);
        var junia = Assert.Single(db.ReadRoster(), c => c.Name == "Junia");
        Assert.Empty(junia.Outfits);
        Assert.NotNull(db.FindCharacter("Junia"));
    }

    [Fact]
    public void ReadRoster_PopulatesCharacterMetadata()
    {
        using var g = new TempGame();
        var vesna = BuildDb(g).ReadRoster().Single(c => c.Name == "Vesna");

        Assert.Equal(42, vesna.CharId);
        Assert.Equal("VesnaSSR", vesna.Family);
        Assert.Equal(1071, vesna.GunId);
        Assert.Equal(107199, vesna.DormModelConfigId);
    }

    [Fact]
    public void ResolveOutfits_ClassifiesEachIdScheme()
    {
        using var g = new TempGame();
        var vesna = BuildDb(g).ReadRoster().Single(c => c.Name == "Vesna");

        OutfitKind Kind(long id) => vesna.Outfits.Single(o => o.ModelConfigId == id).Kind;
        Assert.Equal(OutfitKind.Base, Kind(1071));
        Assert.Equal(OutfitKind.Alt, Kind(107101));
        Assert.Equal(OutfitKind.Alt, Kind(107150));
        Assert.Equal(OutfitKind.Dorm, Kind(107199));   // NN=99 is Dorm, never Alt
        Assert.Equal(OutfitKind.Summon, Kind(10711));
        Assert.Equal(5, vesna.Outfits.Count);
    }

    [Fact]
    public void ResolveOutfits_CarriesStem_AndMeshPrefix()
    {
        using var g = new TempGame();
        var vesna = BuildDb(g).ReadRoster().Single(c => c.Name == "Vesna");
        var alt = vesna.Outfits.Single(o => o.ModelConfigId == 107101);

        Assert.Equal("VesnaSSR0101", alt.Stem);
        Assert.Equal("c_VesnaSSR0101_slg_", alt.MeshPrefix);
    }

    [Fact]
    public void ReadRoster_OnlyListsOutfitsWithStems()
    {
        using var g = new TempGame();
        var neris = BuildDb(g).ReadRoster().Single(c => c.Name == "Neris");

        // Neris has only a base ModelConfig row; the probed alt/dorm ids aren't present, and no summon
        // names her base stem.
        Assert.Single(neris.Outfits);
        Assert.Equal(OutfitKind.Base, neris.Outfits[0].Kind);
        Assert.Equal("NerisSR01", neris.Outfits[0].Stem);
    }

    [Fact]
    public void FindCharacter_IsCaseInsensitive_AndReturnsNullForMisses()
    {
        using var g = new TempGame();
        var db = BuildDb(g);

        var found = db.FindCharacter("vEsNa");
        Assert.NotNull(found);
        Assert.Equal("Vesna", found!.Name);
        Assert.Equal(5, found.Outfits.Count);

        Assert.Null(db.FindCharacter("Nobody"));
    }

    // The summon fixture: three characters, and every shape the two tables put in front of the
    // enumeration — a battle-referenced summon, a config row no battle references, a summon whose id
    // arithmetic points at the wrong character, summons on Alt ids, an enemy-side summon, a battle row
    // naming a character's own base model, and a battle row naming nothing.
    private static GameDatabase BuildSummonDb(TempGame g)
    {
        g.WriteTable("GunCharacterData", TempGame.TableBytes(new[]
        {
            TempGame.GunCharRow(charId: 1, name: "Marlen",  family: "MarlenSSR",  gunId: 1064, dormCfg: 0),
            TempGame.GunCharRow(charId: 2, name: "Ottilie", family: "OttilieSSR", gunId: 1061, dormCfg: 0),
            TempGame.GunCharRow(charId: 3, name: "Neris",   family: "NerisSR",    gunId: 1060, dormCfg: 0),
        }));
        g.WriteTable("BattleSummonedData", TempGame.TableBytes(new[]
        {
            TempGame.BattleSummonedRow(1, 10642,  null),
            TempGame.BattleSummonedRow(2, 10642,  null),   // one model, many battle rows → one outfit
            TempGame.BattleSummonedRow(3, 10601,  null),
            TempGame.BattleSummonedRow(4, 106101, null),
            TempGame.BattleSummonedRow(5, 106111, null),
            TempGame.BattleSummonedRow(6, 7014,   null),
            TempGame.BattleSummonedRow(7, 1064,   null),
            TempGame.BattleSummonedRow(8, 9999,   null),
        }));
        return GameDatabase.FromGameDir(g.WriteTable("ModelConfigData", TempGame.TableBytes(new[]
        {
            TempGame.ModelConfigRow(1064,   "MarlenSSR01"),
            TempGame.ModelConfigRow(10641,  "MarlenSSR01_Summon"),     // no battle row
            TempGame.ModelConfigRow(10642,  "MarlenSSR01_Summon01"),
            TempGame.ModelConfigRow(10645,  "SomethingElse"),
            TempGame.ModelConfigRow(10601,  "MarlenSSR01_Summon02"),   // arithmetic points at Neris (1060)
            TempGame.ModelConfigRow(1061,   "OttilieSSR01"),
            TempGame.ModelConfigRow(106101, "OttilieSSR01_Summon_A"),  // Alt-scheme ids
            TempGame.ModelConfigRow(106111, "OttilieSSR01_Summon_B"),
            TempGame.ModelConfigRow(1060,   "NerisSR01"),
            TempGame.ModelConfigRow(7014,   "ELID_Nemertea_Summon01"), // enemy-side: prefix names no character
        })));
    }

    private static long[] SummonIds(Character c) =>
        c.Outfits.Where(o => o.Kind == OutfitKind.Summon).Select(o => o.ModelConfigId).OrderBy(i => i).ToArray();

    [Fact]
    public void ResolveOutfits_AttributesSummonsByStemPrefix_NotIdArithmetic()
    {
        using var g = new TempGame();
        var roster = BuildSummonDb(g).ReadRoster();

        // 10601 sits in Neris's GunId·10+N slots but its stem prefix is Marlen's base stem, so it is
        // Marlen's; a summon whose prefix names a character reaches THAT character and no other.
        Assert.Equal(new[] { 10601L, 10642L }, SummonIds(roster.Single(c => c.Name == "Marlen")));
        Assert.Empty(SummonIds(roster.Single(c => c.Name == "Neris")));
        Assert.Equal(new[] { 1060L }, roster.Single(c => c.Name == "Neris").Outfits.Select(o => o.ModelConfigId).ToArray());
    }

    [Fact]
    public void ResolveOutfits_KeepsOnlyBattleReferencedSummons()
    {
        using var g = new TempGame();
        var marlen = BuildSummonDb(g).ReadRoster().Single(c => c.Name == "Marlen");

        // 10641 is a config row no battle row summons — not a summon, and its GunId·10+N id buys it nothing.
        Assert.DoesNotContain(marlen.Outfits, o => o.ModelConfigId == 10641);
        Assert.DoesNotContain(marlen.Outfits, o => o.ModelConfigId == 10645);
    }

    [Fact]
    public void ResolveOutfits_SummonsOnAltIds_AreSummonsAndListedOnce()
    {
        using var g = new TempGame();
        var ottilie = BuildSummonDb(g).ReadRoster().Single(c => c.Name == "Ottilie");

        Assert.Equal(new[] { 106101L, 106111L }, SummonIds(ottilie));
        // the Alt scheme covers both ids; the summon set claims them, so neither is enumerated twice
        Assert.Equal(3, ottilie.Outfits.Count);
        Assert.Equal(OutfitKind.Base, ottilie.Outfits.Single(o => o.ModelConfigId == 1061).Kind);
    }

    [Fact]
    public void ResolveOutfits_DropsSummonsThatNameNoOwner()
    {
        using var g = new TempGame();
        var roster = BuildSummonDb(g).ReadRoster();

        // an enemy-side summon, and a battle row naming a character's own base model: neither yields an
        // owner prefix that is a base stem, so neither attaches to anybody
        Assert.DoesNotContain(roster.SelectMany(c => c.Outfits), o => o.ModelConfigId == 7014);
        Assert.Equal(OutfitKind.Base, roster.Single(c => c.Name == "Marlen").Outfits.Single(o => o.ModelConfigId == 1064).Kind);
    }

    [Fact]
    public void ReadRoster_FailsLoudly_WhenTheSummonTableIsMissing()
    {
        using var g = new TempGame();
        g.WriteTable("GunCharacterData", TempGame.TableBytes(new[]
        {
            TempGame.GunCharRow(charId: 1, name: "Marlen", family: "MarlenSSR", gunId: 1064, dormCfg: 0),
        }));
        var root = g.WriteTable("ModelConfigData", TempGame.TableBytes(new[]
        {
            TempGame.ModelConfigRow(1064, "MarlenSSR01"),
        }));

        // Summon membership has no fallback: an install missing the table gets an error, never a roster
        // silently short of everyone's summons.
        Assert.Throws<System.IO.FileNotFoundException>(() => GameDatabase.FromGameDir(root).ReadRoster());
    }

    [Fact]
    public void ResolveOutfits_AppliesCuratedSummonMeshPrefix()
    {
        using var g = new TempGame();
        g.WriteTable("GunCharacterData", TempGame.TableBytes(new[]
        {
            TempGame.GunCharRow(charId: 1, name: "Marlen", family: "MarlenSSR", gunId: 1064, dormCfg: 0),
        }));
        g.WriteTable("BattleSummonedData", TempGame.TableBytes(new[]
        {
            TempGame.BattleSummonedRow(1, 10641, null),
            TempGame.BattleSummonedRow(2, 10642, null),
        }));
        var root = g.WriteTable("ModelConfigData", TempGame.TableBytes(new[]
        {
            TempGame.ModelConfigRow(1064,  "MarlenSSR01"),
            TempGame.ModelConfigRow(10641, "MarlenSSR01_Summon"),   // no curated model link
            TempGame.ModelConfigRow(10642, "MarlenSSR01_Summon01"), // curated → the shipped model family
        }));
        var marlen = GameDatabase.FromGameDir(root).ReadRoster().Single();

        // The curated link points the summon at its real shipped model family; the DB stem names no mesh,
        // and an unmapped summon keeps the conventional (dead) prefix. The expected prefix is the curated
        // map's REAL value — functional data keyed on the real id, not on this test's invented name.
        Assert.Equal("c_FlorenceBear_", marlen.Outfits.Single(o => o.ModelConfigId == 10642).MeshPrefix);
        Assert.Equal("c_MarlenSSR01_Summon_slg_", marlen.Outfits.Single(o => o.ModelConfigId == 10641).MeshPrefix);
        Assert.Null(marlen.Outfits.Single(o => o.ModelConfigId == 1064).MeshPrefixOverride);
    }

    [Fact]
    public void FromGameDir_ResolvesTheTableDir_FromTheRoot_AndRejectsAMissingOne()
    {
        using var g = new TempGame();
        BuildDb(g);                                   // creates the Table dir under the install layout
        Assert.NotEmpty(GameDatabase.FromGameDir(g.Root).ReadRoster());

        using var empty = new TempGame();
        Assert.Throws<System.IO.DirectoryNotFoundException>(() => GameDatabase.FromGameDir(empty.Root));
    }
}
