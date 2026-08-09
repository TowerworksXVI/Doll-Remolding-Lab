using System.Collections.Generic;
using System.Linq;
using Remold.Core.Model;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The localized display-name join, driven by synthetic tables: character name = GunCharacterData #2.1 →
/// GunId join, outfit name = model stem → ClothesData #25/#8 → #9.1, both through a LangPackage map. The
/// designed fallback (a nameless stem or character keeps its token) is a first-class case.
/// </summary>
public class DisplayNamesTests
{
    // Vesna has a localized character name and one named outfit; an enemy stem has no ClothesData row.
    private static (GameDatabase Db, DisplayNames Names) Build(TempGame g, string locale = "Enus")
    {
        var chars = new[]
        {
            TempGame.GunCharRowLoc(charId: 42, name: "Vesna", family: "VesnaSSR", gunId: 1071, dormCfg: 107199, nameTextId: 5001),
            TempGame.GunCharRowLoc(charId: 7,  name: "Neris", family: "NerisSR",  gunId: 2,    dormCfg: 0,      nameTextId: null), // no name wrapper
        };
        var clothes = new[]
        {
            TempGame.ClothesRow(gunId: 1071, baseStem: "VesnaSSR01", modelStem: "VesnaSSR0101", nameTextId: 5002),
        };
        var lang = new[]
        {
            TempGame.LangRow(5001, "Mirel"),        // Vesna's marketed name
            TempGame.LangRow(5002, "Plum Fizz"),    // the outfit name
        };
        g.WriteTable("GunCharacterData", TempGame.TableBytes(chars));
        g.WriteIntlTable("ClothesData", TempGame.TableBytes(clothes));
        var root = g.WriteTable($"LangPackageTable{locale}Data", TempGame.TableBytes(lang));
        var db = GameDatabase.FromGameDir(root);
        return (db, DisplayNames.Build(db, LocalizationDb.Load(db, locale)));
    }

    [Fact]
    public void Character_ResolvesLocalizedNameByGunId()
    {
        using var g = new TempGame();
        var (_, names) = Build(g);
        Assert.Equal("Mirel", names.Character(1071));
    }

    [Fact]
    public void Character_WithNoNameWrapper_ReturnsNull_TheTokenFallbackCase()
    {
        using var g = new TempGame();
        var (_, names) = Build(g);
        Assert.Null(names.Character(2));      // Neris has no #2.1 wrapper → caller keeps the internal name
    }

    [Fact]
    public void Outfit_ResolvesByExactModularStem()
    {
        using var g = new TempGame();
        var (_, names) = Build(g);
        Assert.Equal("Plum Fizz", names.Outfit("VesnaSSR0101"));
    }

    [Fact]
    public void Outfit_ResolvesByBaseStemToo()
    {
        using var g = new TempGame();
        var (_, names) = Build(g);
        // #8 (base stem) is indexed alongside #25 (exact), so a roster built off the base stem still names.
        Assert.Equal("Plum Fizz", names.Outfit("VesnaSSR01"));
    }

    [Fact]
    public void Outfit_UnknownStem_ReturnsNull_TheTokenFallbackCase()
    {
        using var g = new TempGame();
        var (_, names) = Build(g);
        Assert.Null(names.Outfit("EnemyGoblin01"));   // no ClothesData row → caller keeps the stem label
    }

    [Fact]
    public void Enrich_FillsDisplayName_WithoutTouchingTheGroupingName()
    {
        using var g = new TempGame();
        var (_, names) = Build(g);
        var roster = new List<Character>
        {
            new(CharId: 42, Name: "Vesna", Family: "VesnaSSR", GunId: 1071, DormModelConfigId: 107199,
                Outfits: new List<Outfit> { new(107101, "VesnaSSR0101", OutfitKind.Alt) }),
        };

        var enriched = names.Enrich(roster);

        var vesna = enriched.Single();
        Assert.Equal("Vesna", vesna.Name);            // internal grouping key untouched
        Assert.Equal("Mirel", vesna.DisplayName);     // localized name filled in
        Assert.Equal("Plum Fizz", vesna.Outfits.Single().DisplayName);
        Assert.Equal("VesnaSSR0101", vesna.Outfits.Single().Stem);   // stem untouched
    }

    [Fact]
    public void Enrich_NamelessRoster_LeavesDisplayNameNull()
    {
        using var g = new TempGame();
        var (_, names) = Build(g);
        var roster = new List<Character>
        {
            // an enemy with no DB name and a stem with no ClothesData row — both correctly nameless
            new(CharId: 0, Name: "Goblin", Family: "", GunId: 9999, DormModelConfigId: 0,
                Outfits: new List<Outfit> { new(0, "EnemyGoblin01", OutfitKind.Other) }),
        };

        var enriched = names.Enrich(roster);

        Assert.Null(enriched.Single().DisplayName);
        Assert.Null(enriched.Single().Outfits.Single().DisplayName);
    }

    [Fact]
    public void Summon_ResolvesFromBattleSummonedData_MostFrequentNameWins()
    {
        using var g = new TempGame();
        g.WriteTable("GunCharacterData", TempGame.TableBytes(System.Array.Empty<byte[]>()));
        g.WriteTable("BattleSummonedData", TempGame.TableBytes(new[]
        {
            // Three battle rows for one summon model: the marketed name twice, a dev-test name once.
            TempGame.BattleSummonedRow(rowId: 1, modelConfigId: 10642, nameTextId: 6001),
            TempGame.BattleSummonedRow(rowId: 2, modelConfigId: 10642, nameTextId: 6001),
            TempGame.BattleSummonedRow(rowId: 3, modelConfigId: 10642, nameTextId: 6002),
            TempGame.BattleSummonedRow(rowId: 4, modelConfigId: 10721, nameTextId: null),   // unnamed row
        }));
        var root = g.WriteTable("LangPackageTableEnusData", TempGame.TableBytes(new[]
        {
            TempGame.LangRow(6001, "Talos"),
            TempGame.LangRow(6002, "Talos (Controllable Test)"),
        }));
        var db = GameDatabase.FromGameDir(root);
        var names = DisplayNames.Build(db, LocalizationDb.Load(db, "Enus"));

        Assert.Equal("Talos", names.Summon(10642));
        Assert.Null(names.Summon(10721));   // rows exist but carry no name wrapper
        Assert.Null(names.Summon(99999));   // no rows at all
    }

    [Fact]
    public void Enrich_NamesSummonOutfitsByModelConfigId_ButOnlySummonKind()
    {
        using var g = new TempGame();
        g.WriteTable("GunCharacterData", TempGame.TableBytes(System.Array.Empty<byte[]>()));
        g.WriteTable("BattleSummonedData", TempGame.TableBytes(new[]
        {
            TempGame.BattleSummonedRow(rowId: 1, modelConfigId: 10642, nameTextId: 6001),
            // A battle row that reuses a BASE outfit's model id (some projections do this):
            // its name must never land on the real outfit.
            TempGame.BattleSummonedRow(rowId: 2, modelConfigId: 1058, nameTextId: 6003),
        }));
        var root = g.WriteTable("LangPackageTableEnusData", TempGame.TableBytes(new[]
        {
            TempGame.LangRow(6001, "Talos"),
            TempGame.LangRow(6003, "Nira Projection (temp)"),
        }));
        var db = GameDatabase.FromGameDir(root);
        var names = DisplayNames.Build(db, LocalizationDb.Load(db, "Enus"));
        var roster = new List<Character>
        {
            new(CharId: 1, Name: "Marlen", Family: "MarlenSSR", GunId: 1064, DormModelConfigId: 0,
                Outfits: new List<Outfit> { new(10642, "MarlenSSR01_Summon01", OutfitKind.Summon) }),
            new(CharId: 2, Name: "Nira", Family: "NiraSSR", GunId: 1058, DormModelConfigId: 0,
                Outfits: new List<Outfit> { new(1058, "NiraSSR01", OutfitKind.Base) }),
        };

        var enriched = names.Enrich(roster);

        Assert.Equal("Talos", enriched.Single(c => c.Name == "Marlen").Outfits.Single().DisplayName);
        Assert.Null(enriched.Single(c => c.Name == "Nira").Outfits.Single().DisplayName);
    }

    [Fact]
    public void Build_EmptyLocaleTable_YieldsEmptyResolver_WholeTreeFallsBackToTokens()
    {
        using var g = new TempGame();
        // A present-but-empty LangPackage: every text-id resolves to null and the whole tree falls back to
        // tokens — a visible all-null resolver, not a per-row swallow.
        g.WriteTable("GunCharacterData", TempGame.TableBytes(new[]
        {
            TempGame.GunCharRowLoc(42, "Vesna", "VesnaSSR", 1071, 107199, nameTextId: 5001),
        }));
        var root = g.WriteTable("LangPackageTableEnusData", TempGame.TableBytes(System.Array.Empty<byte[]>()));
        var db = GameDatabase.FromGameDir(root);

        var names = DisplayNames.Build(db, LocalizationDb.Load(db, "Enus"));

        Assert.Equal(0, names.CharacterCount);
        Assert.Null(names.Character(1071));
    }

    [Fact]
    public void Build_MissingClothesData_StillNamesCharacters()
    {
        using var g = new TempGame();
        // With no ClothesData, Build's per-table guard leaves outfits empty but still resolves characters.
        g.WriteTable("GunCharacterData", TempGame.TableBytes(new[]
        {
            TempGame.GunCharRowLoc(42, "Vesna", "VesnaSSR", 1071, 107199, nameTextId: 5001),
        }));
        var root = g.WriteTable("LangPackageTableEnusData", TempGame.TableBytes(new[] { TempGame.LangRow(5001, "Mirel") }));
        var db = GameDatabase.FromGameDir(root);

        var names = DisplayNames.Build(db, LocalizationDb.Load(db, "Enus"));

        Assert.Equal("Mirel", names.Character(1071));
        Assert.Equal(0, names.OutfitStemCount);
        Assert.Null(names.Outfit("VesnaSSR0101"));
    }
}
