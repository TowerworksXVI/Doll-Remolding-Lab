using System.IO;
using System.Linq;
using Remold.App;
using Remold.App.Views;
using Remold.Core.Tests.Support;
using Remold.Core.Text;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The Settings form's Language row: what it lists, what it opens on, and what a Save stores.</summary>
public class LanguageRowTests
{
    private static string ChineseName => Catalog.Languages.Single(l => l.Tag == Catalog.SimplifiedChinese).DisplayName;

    [Fact]
    public void The_row_lists_the_offered_languages_in_order_and_no_setting_opens_on_english()
    {
        var (choices, selected) = LanguageRow.Choices(null);

        Assert.Equal(Catalog.Languages.Select(l => l.DisplayName), choices.Select(c => c.Display));
        Assert.Equal("English", selected.Display);
        Assert.Null(selected.Tag);
    }

    [Fact]
    public void The_row_opens_on_the_stored_tag()
    {
        var (choices, selected) = LanguageRow.Choices("zh-Hans");

        Assert.Equal(Catalog.Languages.Count, choices.Count);
        Assert.Equal(ChineseName, selected.Display);
    }

    [Theory]
    [InlineData("qps-ploc")]
    [InlineData("fr-FR")]
    public void An_unlisted_tag_shows_as_one_more_item_and_a_save_that_leaves_the_row_keeps_it(string stored)
    {
        var (choices, selected) = LanguageRow.Choices(stored);

        Assert.Equal(Catalog.Languages.Count + 1, choices.Count);
        Assert.Same(choices[^1], selected);
        Assert.Equal(stored, selected.Display);
        Assert.Equal(stored, LanguageRow.Stored(selected, selected));
    }

    [Theory]
    [InlineData("ZH-hans")]
    [InlineData("zh-Hans")]
    [InlineData("zh-CN")]
    [InlineData("zh-sg")]
    public void A_tag_that_selects_simplified_chinese_opens_on_the_listed_item_and_a_save_writes_its_tag(string stored)
    {
        var (choices, selected) = LanguageRow.Choices(stored);

        Assert.Equal(Catalog.Languages.Count, choices.Count);
        Assert.Equal(ChineseName, selected.Display);
        Assert.Equal(Catalog.SimplifiedChinese, LanguageRow.Stored(selected, selected));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    public void An_english_tag_opens_on_english_and_a_save_stores_null(string stored)
    {
        var (choices, selected) = LanguageRow.Choices(stored);

        Assert.Equal(Catalog.Languages.Count, choices.Count);
        Assert.Same(choices[0], selected);
        Assert.Null(LanguageRow.Stored(selected, selected));
    }

    [Fact]
    public void Picking_chinese_stores_its_tag_and_picking_english_stores_null()
    {
        var (choices, opened) = LanguageRow.Choices(null);
        var chinese = choices.Single(c => c.Display == ChineseName);
        Assert.Equal(Catalog.SimplifiedChinese, LanguageRow.Stored(chinese, opened));

        var (fromChinese, openedOnChinese) = LanguageRow.Choices("zh-Hans");
        var english = fromChinese.Single(c => c.Display == "English");
        Assert.Null(LanguageRow.Stored(english, openedOnChinese));
    }
}

/// <summary>What the app selects at startup from the settings file. These change the process's UI language,
/// so they run with the other tests that do, and put English back.</summary>
[Collection("UiCulture")]
public class StartupLanguageTests
{
    [Theory]
    [InlineData("""{ "Language": "zh-Hans" }""", "zh-Hans")]
    [InlineData("""{ "Language": "qps-ploc" }""", "qps-ploc")]
    [InlineData("""{ "Language": null }""", "en")]
    [InlineData("""{ "Author": "TestAuthor" }""", "en")]
    [InlineData("{ not json", "en")]
    public void The_stored_language_is_applied_and_a_file_without_one_is_english(string json, string expected)
    {
        using var g = new TempGame();
        var path = g.At("settings.json");
        File.WriteAllText(path, json);
        try
        {
            Assert.Equal(expected, StartupLanguage.Apply(path));
            Assert.Equal(expected, Catalog.CurrentTag);
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Fact]
    public void A_missing_settings_file_is_english()
    {
        using var g = new TempGame();
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);
            Assert.Equal(Catalog.English, StartupLanguage.Apply(g.At("absent.json")));
        }
        finally { Catalog.Apply(Catalog.English); }
    }
}
