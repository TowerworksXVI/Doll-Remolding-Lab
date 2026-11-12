using System;
using System.Collections.Generic;
using System.IO;
using Remold.App.ViewModels;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Remold.Core.Text;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>Which of the game's languages the Pick tree's names are read in, and what happens when the
/// install has no text table for it.</summary>
public class RosterLocaleTests
{
    [Theory]
    [InlineData(Catalog.SimplifiedChinese, "Cn")]
    [InlineData(Catalog.English, "Enus")]
    [InlineData(Catalog.Pseudo, "Enus")]
    public void The_ui_language_picks_the_games_locale(string uiTag, string expected)
    {
        Assert.Equal(expected, LocalizationDb.GameLocale(uiTag));
    }

    [Fact]
    public void A_locale_whose_table_is_present_is_used()
    {
        Assert.Equal("Cn", LocalizationDb.AvailableLocale("Cn", table => table == "LangPackageTableCnData", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void A_locale_whose_table_is_missing_falls_back_to_english_naming_the_table()
    {
        Assert.Equal("Enus", LocalizationDb.AvailableLocale("Cn", _ => false, out var missing));
        Assert.Equal("LangPackageTableCnData", missing);

        Assert.Equal("Enus", LocalizationDb.AvailableLocale("Cn", _ => throw new IOException("share gone"), out missing));
        Assert.Equal("LangPackageTableCnData", missing);
    }

    [Fact]
    public void English_is_never_probed_or_substituted()
    {
        Assert.Equal("Enus", LocalizationDb.AvailableLocale("Enus", _ => throw new InvalidOperationException("probed"), out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void The_roster_logs_one_line_when_the_ui_languages_table_is_missing_and_none_otherwise()
    {
        var lines = new List<string>();
        void Log(string context, string detail) => lines.Add(context + ": " + detail);

        Assert.Equal("Enus", MainWindowViewModel.RosterLocale(Catalog.SimplifiedChinese, _ => false, Log));
        var line = Assert.Single(lines);
        Assert.Contains("LangPackageTableCnData", line, StringComparison.Ordinal);

        lines.Clear();
        Assert.Equal("Cn", MainWindowViewModel.RosterLocale(Catalog.SimplifiedChinese, _ => true, Log));
        Assert.Equal("Enus", MainWindowViewModel.RosterLocale(Catalog.English, _ => false, Log));
        Assert.Empty(lines);
    }

    [Fact]
    public void A_chinese_appearance_name_loses_its_prefix_and_a_chinese_skin_name_stays_whole()
    {
        using var g = new TempGame();
        g.WriteTable("WeaponModCodGroupData", TempGame.TableBytes(new[]
        {
            TempGame.WeaponModCodRow(1011131, "Sights/WeaponPart_1_gungb_3"),
        }));
        g.WriteIntlTable("WeaponModSkinData", TempGame.TableBytes(new[]
        {
            TempGame.WeaponModSkinRow(7231035, 5, "Sights/Scope_SSR_Set005_3", nameTextId: 701),
            TempGame.WeaponModSkinRow(6311054, 4, nameTextId: 702, refId: 10111353),
        }));
        g.WriteTable("LangPackageTableCnData", TempGame.TableBytes(new[]
        {
            TempGame.LangRow(701, "涂装·灵海潮汐瞄准"),
            TempGame.LangRow(702, "涂装·全息瞄具1型"),
        }));
        var db = new GameDatabase(g.At(@"GF2_Exilium_Data\LocalCache\Data\Table"));

        var entries = WeaponRoster.ReadAttachments(db, LocalizationDb.Load(db, "Cn"));

        Assert.Equal("全息瞄具1型", Assert.Single(entries, e => !e.IsSkin).DisplayName);
        Assert.Equal("涂装·灵海潮汐瞄准", Assert.Single(entries, e => e.IsSkin).DisplayName);
    }

    [Fact]
    public void Only_english_and_chinese_appearance_names_carry_a_prefix_to_strip()
    {
        Assert.Equal("Skin - ", WeaponRoster.AppearancePrefix("Enus"));
        Assert.Equal("涂装·", WeaponRoster.AppearancePrefix("Cn"));
        Assert.Null(WeaponRoster.AppearancePrefix("Jajp"));
    }
}
