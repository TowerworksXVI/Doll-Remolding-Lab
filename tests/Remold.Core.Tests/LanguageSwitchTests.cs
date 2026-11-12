using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Remold.App;
using Remold.App.ViewModels;
using Remold.App.Views;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Remold.Core.Text;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The first-run window's language list: what it opens on, what a pick does to the process, and
/// what Accept stores. These change the process's UI language, so they run with the other tests that do.</summary>
[Collection("UiCulture")]
public class FirstRunLanguageTests
{
    private static readonly CultureInfo EnglishWindows = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo ChineseWindows = CultureInfo.GetCultureInfo("zh-CN");

    [Fact]
    public void With_no_stored_language_the_list_follows_a_chinese_windows_and_applies_it()
    {
        try
        {
            var language = new FirstRunLanguage(null, ChineseWindows);

            Assert.Equal(Catalog.SimplifiedChinese, language.Selected.Tag);
            Assert.Equal(Catalog.SimplifiedChinese, Catalog.CurrentTag);
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-TW")]
    [InlineData("fr-FR")]
    public void With_no_stored_language_any_other_windows_language_opens_on_english(string windows)
    {
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);
            var language = new FirstRunLanguage(null, CultureInfo.GetCultureInfo(windows));

            Assert.Null(language.Selected.Tag);
            Assert.Equal(Catalog.English, Catalog.CurrentTag);
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Fact]
    public void A_stored_language_wins_over_the_windows_language()
    {
        try
        {
            Assert.Null(new FirstRunLanguage("en", ChineseWindows).Selected.Tag);
            Assert.Equal(Catalog.English, Catalog.CurrentTag);

            Assert.Equal(Catalog.SimplifiedChinese, new FirstRunLanguage("zh-CN", EnglishWindows).Selected.Tag);
            Assert.Equal(Catalog.SimplifiedChinese, Catalog.CurrentTag);
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Fact]
    public void A_pick_applies_its_language_to_the_process_at_once()
    {
        try
        {
            var language = new FirstRunLanguage(null, EnglishWindows);
            var chinese = language.Choices.Single(choice => choice.Tag == Catalog.SimplifiedChinese);

            language.Pick(chinese);

            Assert.Same(chinese, language.Selected);
            Assert.Equal(Catalog.SimplifiedChinese, Catalog.CurrentTag);
            Assert.Equal(CultureInfo.GetCultureInfo("zh-Hans"), CultureInfo.CurrentUICulture);
            Assert.NotEqual(Catalog.Neutral(Strings.Rm, nameof(Strings.FirstRunAccept)), Strings.FirstRunAccept);

            language.Pick(language.Choices[0]);

            Assert.Equal(Catalog.English, Catalog.CurrentTag);
            Assert.Equal("Accept", Strings.FirstRunAccept);
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Fact]
    public void Accept_stores_the_picked_language_and_english_stays_null()
    {
        using var snapshot = new SettingsSnapshot();
        try
        {
            var language = new FirstRunLanguage(null, EnglishWindows);
            language.Pick(language.Choices.Single(choice => choice.Tag == Catalog.SimplifiedChinese));
            var vm = new MainWindowViewModel(startLoad: false);

            vm.AdoptFirstRunLanguage(language.Selected.Tag);

            Assert.Equal(Catalog.SimplifiedChinese, LabSettings.Load().Language);
            Assert.Equal(Catalog.SimplifiedChinese, vm.BuildSettingsInput().Language);

            vm.AdoptFirstRunLanguage(new FirstRunLanguage(null, EnglishWindows).Selected.Tag);

            Assert.Null(LabSettings.Load().Language);
        }
        finally { Catalog.Apply(Catalog.English); }
    }
}

/// <summary>The Settings form's Language value through the real routes: the form hands back what its row
/// shows, the view model stores it, and the next form opens on it.</summary>
[Collection("Dispatcher")]
public class SettingsLanguageRouteTests
{
    [Fact]
    public async Task The_language_round_trips_from_the_view_model_through_the_form_and_back()
    {
        using var snapshot = new SettingsSnapshot();
        using var session = HeadlessUnitTestSession.StartNew(typeof(App.App));
        await session.Dispatch(() =>
        {
            var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false };
            vm.ApplySettings(new SettingsResult { Language = "zh-CN", Author = vm.BuildSettingsInput().Author });
            Assert.Equal("zh-CN", vm.BuildSettingsInput().Language);

            var form = new SettingsWindow(vm.BuildSettingsInput());
            var result = form.Collect();

            Assert.Equal(Catalog.SimplifiedChinese, result.Language);
            vm.ApplySettings(result);
            Assert.Equal(Catalog.SimplifiedChinese, vm.BuildSettingsInput().Language);
            Assert.Equal(Catalog.SimplifiedChinese, LabSettings.Load().Language);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_tag_the_form_does_not_list_survives_a_save_that_leaves_the_row()
    {
        using var snapshot = new SettingsSnapshot();
        using var session = HeadlessUnitTestSession.StartNew(typeof(App.App));
        await session.Dispatch(() =>
        {
            var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false };
            vm.ApplySettings(new SettingsResult { Language = "fr-FR", Author = vm.BuildSettingsInput().Author });

            var form = new SettingsWindow(vm.BuildSettingsInput());
            var result = form.Collect();

            Assert.Equal("fr-FR", result.Language);
        }, CancellationToken.None);
    }
}

/// <summary>When a Settings Save offers to restart, and what the restart starts.</summary>
public class LanguageRestartTests
{
    [Theory]
    [InlineData(null, "zh-Hans", "en", true, true)]
    [InlineData("zh-Hans", null, "zh-Hans", true, true)]
    [InlineData(null, "zh-Hans", "en", false, false)]           // the settings file refused the write
    [InlineData("zh-Hans", "zh-Hans", "en", true, false)]      // an earlier choice the Save left alone
    [InlineData("zh-CN", "zh-Hans", "en", true, false)]        // the same language, written as the listed tag
    [InlineData("zh-Hans", null, "en", true, false)]           // back to the language already running
    [InlineData(null, "fr-FR", "en", true, false)]             // a tag that still selects English
    public void A_save_offers_a_restart_only_when_it_changes_the_language_and_lands(
        string? before, string? after, string running, bool saved, bool offered)
    {
        Assert.Equal(offered, MainWindowViewModel.OffersLanguageRestart(before, after, running, saved));
    }

    [Fact]
    public void A_requested_restart_starts_the_apps_own_executable_and_no_request_starts_nothing()
    {
        var start = Program.RelaunchStart(restartRequested: true, @"C:\Apps\Lab\Remold.App.exe");

        Assert.NotNull(start);
        Assert.Equal(@"C:\Apps\Lab\Remold.App.exe", start!.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Null(Program.RelaunchStart(restartRequested: false, @"C:\Apps\Lab\Remold.App.exe"));
        Assert.Null(Program.RelaunchStart(restartRequested: true, null));
    }

    [Theory]
    [InlineData(@"C:\Apps\Lab\Remold.App.exe", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)]
    [InlineData(@"C:\Program Files\dotnet\DOTNET.EXE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_apps_own_executable_can_start_it_again(string? processPath, bool relaunchable)
    {
        Assert.Equal(relaunchable, Program.CanRelaunch(processPath));
        Assert.Equal(relaunchable, Program.RelaunchStart(restartRequested: true, processPath) is not null);
    }

    [Fact]
    public void A_chinese_startup_or_a_first_run_names_the_simplified_chinese_fallback_face()
    {
        foreach (var options in new[]
                 {
                     Program.ChineseFallback(Catalog.SimplifiedChinese, termsGateOpens: false),
                     Program.ChineseFallback(Catalog.English, termsGateOpens: true),
                     Program.ChineseFallback(Catalog.Pseudo, termsGateOpens: true),
                 })
            Assert.Equal("Microsoft YaHei UI", Assert.Single(options!.FontFallbacks!).FontFamily.Name);
        Assert.Null(Program.ChineseFallback(Catalog.English, termsGateOpens: false));
        Assert.Null(Program.ChineseFallback(Catalog.Pseudo, termsGateOpens: false));
    }
}

/// <summary>The roster's names when the UI language's text table is missing or cannot be decoded.</summary>
public class RosterTextFallbackTests
{
    private static LocalizationDb Names(string locale, params (long Id, string Text)[] rows)
    {
        using var g = new TempGame();
        g.WriteTable(LocalizationDb.TableName(locale),
            TempGame.TableBytes(rows.Select(row => TempGame.LangRow(row.Id, row.Text)).ToArray()));
        return LocalizationDb.Load(new GameDatabase(g.At(@"GF2_Exilium_Data\LocalCache\Data\Table")), locale);
    }

    [Fact]
    public void A_chinese_table_that_throws_on_decode_falls_back_to_english_names_with_one_log_line_and_the_rescan_notice()
    {
        var lines = new List<string>();
        var english = Names(LocalizationDb.EnglishLocale, (701, "First Team"));

        var text = MainWindowViewModel.LoadRosterText(Catalog.SimplifiedChinese, _ => true,
            locale => locale == LocalizationDb.EnglishLocale ? english : throw new InvalidDataException("half-written"),
            (context, detail) => lines.Add(context + ": " + detail));

        Assert.Equal("First Team", text.Text!.Text(701));
        Assert.True(text.TableUnreadable);
        Assert.Contains("half-written", Assert.Single(lines), StringComparison.Ordinal);
        var notice = Assert.Single(MainWindowViewModel.RosterNamesNotice(text));
        Assert.Equal(Strings.RosterNamesEnglish, notice.Short);
        Assert.Equal(Strings.RosterNamesEnglishDetail, notice.Detail);
    }

    /// <summary>No rescan can add a table the install does not have, so the names show in English with the
    /// log line and no status-bar notice.</summary>
    [Fact]
    public void A_missing_chinese_table_shows_english_names_with_one_log_line_and_no_notice()
    {
        var lines = new List<string>();
        var english = Names(LocalizationDb.EnglishLocale, (701, "First Team"));

        var text = MainWindowViewModel.LoadRosterText(Catalog.SimplifiedChinese, _ => false,
            locale => locale == LocalizationDb.EnglishLocale ? english : throw new InvalidOperationException("not read"),
            (context, detail) => lines.Add(context + ": " + detail));

        Assert.Equal("First Team", text.Text!.Text(701));
        Assert.False(text.TableUnreadable);
        Assert.Single(lines);
        Assert.Empty(MainWindowViewModel.RosterNamesNotice(text));
    }

    [Fact]
    public void A_readable_chinese_table_and_an_english_ui_post_no_notice()
    {
        var chinese = Names(LocalizationDb.SimplifiedChineseLocale, (701, "第一小队"));
        var english = Names(LocalizationDb.EnglishLocale, (701, "First Team"));

        var inChinese = MainWindowViewModel.LoadRosterText(Catalog.SimplifiedChinese, _ => true,
            locale => locale == LocalizationDb.SimplifiedChineseLocale ? chinese : english, (_, _) => { });
        var inEnglish = MainWindowViewModel.LoadRosterText(Catalog.English, _ => true,
            locale => locale == LocalizationDb.EnglishLocale ? english : throw new InvalidOperationException("probed"),
            (_, _) => { });

        Assert.Equal("第一小队", inChinese.Text!.Text(701));
        Assert.Empty(MainWindowViewModel.RosterNamesNotice(inChinese));
        Assert.Equal("First Team", inEnglish.Text!.Text(701));
        Assert.Empty(MainWindowViewModel.RosterNamesNotice(inEnglish));
    }

    [Fact]
    public void An_unreadable_english_table_leaves_the_labels_on_their_tokens_without_the_notice()
    {
        var text = MainWindowViewModel.LoadRosterText(Catalog.SimplifiedChinese, _ => true,
            _ => throw new InvalidDataException("corrupt"), (_, _) => { });

        Assert.Null(text.Text);
        Assert.Empty(MainWindowViewModel.RosterNamesNotice(text));
    }

    /// <summary>The load's own wiring over an install: the Chinese table is present but is not a table, so
    /// its decode throws and the English table answers.</summary>
    [Fact]
    public void Over_an_install_a_corrupt_chinese_table_reads_the_english_one()
    {
        using var g = new TempGame();
        g.WriteTable(LocalizationDb.TableName(LocalizationDb.EnglishLocale),
            TempGame.TableBytes(new[] { TempGame.LangRow(701, "First Team") }));
        g.WriteTable(LocalizationDb.TableName(LocalizationDb.SimplifiedChineseLocale), new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0A, 0xFF });
        var db = new GameDatabase(g.At(@"GF2_Exilium_Data\LocalCache\Data\Table"));
        var lines = new List<string>();

        var text = MainWindowViewModel.LoadRosterText(db, Catalog.SimplifiedChinese,
            locale => g.At("snapshot-" + locale + ".bin"), (context, _) => lines.Add(context));

        Assert.Equal("First Team", text.Text!.Text(701));
        Assert.True(text.TableUnreadable);
        Assert.Single(lines);
    }
}

/// <summary>Display text the roster builds from code rather than from the game's tables, and the Build
/// footer's sentence close, under the Chinese catalog.</summary>
[Collection("UiCulture")]
public class ChineseRosterLabelTests
{
    [Fact]
    public void Curated_and_weapon_group_labels_follow_the_ui_language_and_their_keys_do_not()
    {
        var englishCharacters = CuratedSkins.All.Select(skin => skin.CharacterDisplay).ToList();
        var englishOutfits = CuratedSkins.All.Select(skin => skin.OutfitDisplay).ToList();
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);

            Assert.Equal(CoreStrings.CuratedCommanderMale,
                CuratedSkins.All.First(skin => skin.Character == "CommanderMale").CharacterDisplay);
            Assert.NotEqual(englishOutfits, CuratedSkins.All.Select(skin => skin.OutfitDisplay));
            Assert.Equal(CoreStrings.WeaponTypeMelee, WeaponType.DisplayName(7));
            Assert.Equal("HG", WeaponType.DisplayName(1));
            Assert.Equal(CoreStrings.WeaponTypeNumbered(9), WeaponType.DisplayName(9));
            Assert.Equal("Melee", WeaponType.Token(7));
            Assert.Equal("Type 9", WeaponType.Token(9));
        }
        finally { Catalog.Apply(Catalog.English); }

        Assert.Equal(englishCharacters, CuratedSkins.All.Select(skin => skin.CharacterDisplay));
        Assert.Equal("Commander (M)", CuratedSkins.All.First(skin => skin.Character == "CommanderMale").CharacterDisplay);
        Assert.Equal("Melee", WeaponType.DisplayName(7));
        Assert.Equal("Type 9", WeaponType.DisplayName(9));
    }

    [Theory]
    [InlineData("Stopped", "Stopped.")]
    [InlineData("Stopped.", "Stopped.")]
    [InlineData("Checking…", "Checking…")]
    [InlineData("确定吗？", "确定吗？")]
    public void An_english_footer_closes_a_line_with_a_full_stop_unless_it_already_ends_a_sentence(string line, string closed)
    {
        Assert.Equal(closed, Remold.App.ViewModels.BuildPage.BuildFooter.End(line));
    }

    [Theory]
    [InlineData("生成已停止", "生成已停止。")]
    [InlineData("生成已停止。", "生成已停止。")]
    [InlineData("确定吗？", "确定吗？")]
    [InlineData("完成！", "完成！")]
    [InlineData("正在检查…", "正在检查…")]
    [InlineData("无法生成“X”", "无法生成“X”。")]
    public void A_chinese_footer_closes_a_line_with_the_full_width_stop_unless_it_already_ends_a_sentence(string line, string closed)
    {
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);

            Assert.Equal(closed, Remold.App.ViewModels.BuildPage.BuildFooter.End(line));
            Assert.Equal(CoreStrings.BuildNotReady + "。",
                Remold.App.ViewModels.BuildPage.BuildFooter.Blocked(CoreStrings.BuildNotReady).Text);
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    /// <summary>A Blender return's joins under the Chinese catalog: one closing full stop gives way, so no
    /// sentence carries two ends.</summary>
    [Fact]
    public void Blender_return_joins_drop_one_closing_full_stop_in_either_script()
    {
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);
            var refusal = new Remold.Core.Project.AuthoredRefusalException("请重新打开该部件并再次发送。");

            Assert.Equal("请重新打开该部件并再次发送 (cloth)", MainWindowViewModel.BlenderPartReason("cloth", refusal));
            Assert.Equal(Strings.BlenderPublishFailure("请重新打开该部件并再次发送"),
                MainWindowViewModel.BlenderPublishFailure("请重新打开该部件并再次发送。"));
            Assert.DoesNotContain("。。", MainWindowViewModel.BlenderPublishFailure("请重新打开该部件并再次发送。"));
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Fact]
    public void A_curated_outfits_name_joins_its_labels_as_the_ui_language_writes_them()
    {
        var skin = CuratedSkins.All.First(entry => entry.Character == "CommanderMale");
        Assert.Equal(skin.CharacterDisplay + " " + skin.OutfitDisplay, skin.Display);
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);

            Assert.Equal(skin.CharacterDisplay + skin.OutfitDisplay, skin.Display);
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    /// <summary>The names the app's fixtures and its own curated and group labels show, in the order the
    /// English Pick tree has always listed them.</summary>
    private static readonly string[] FixtureNames =
    [
        "Mirel", "Vesna", "Ostra", "Sable", "First Team", "Second", "Plum Fizz", "Traveling Merchant",
        "Hyper Trigger", "Silver Line", "Barracks", "Commander (F)", "Commander (M)", "Battle Pass",
        "Skins · HG", "Skins · Melee", "Skins · Type 9", "Parts · Other", "Parts · Silencers", "XM-7",
        "a", "B", "c", "Crew Deck", "Neutral", "001", "002",
    ];

    /// <summary>Name shapes the roster holds that a culture collation reorders against the ordinal one: a
    /// curly-quoted title, a name led by punctuation, a Roman numeral beside its digit, an apostrophe or a
    /// hyphen beside a letter, and an accented letter.</summary>
    private static readonly string[] CollationSensitiveNames =
    [
        "\u201CEmber\u201D", ".45 Arc", "Arc Blade Type 4", "Arc Blade Type \u2163", "Kestrel's Training Outfit",
        "Kestrel-3", "Lynx Hunter - Elite", "L\u00F6wen", "Basic Muzzle Brake", "Zephyr",
    ];

    [Fact]
    public void The_english_pick_order_is_ordinal_and_chinese_names_order_by_pinyin()
    {
        string[] names = [.. FixtureNames, .. CollationSensitiveNames];
        var ordinal = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        var culture = names.OrderBy(name => name, StringComparer.Create(CultureInfo.GetCultureInfo("en"), ignoreCase: true)).ToList();
        Assert.NotEqual(ordinal, culture);   // the names tell the two orders apart
        Assert.Equal(ordinal, names.OrderBy(name => name, MainWindowViewModel.PickNameOrder()));
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);
            // 阿 (a), 波 (bo), 蔡 (cai), 大 (da): code point order would be 大, 波, 蔡, 阿
            string[] chinese = ["大", "波", "蔡", "阿"];

            Assert.Equal(["阿", "波", "蔡", "大"], chinese.OrderBy(name => name, MainWindowViewModel.PickNameOrder()));
        }
        finally { Catalog.Apply(Catalog.English); }
    }
}

/// <summary>A failure while one part of a Blender return is read carries that part, so the return's status
/// names it once.</summary>
public class BlenderPartFailureTests
{
    [Fact]
    public void A_refusal_stays_a_refusal_naming_its_part_and_anything_else_names_the_part_in_brackets()
    {
        var refusal = Assert.IsType<Remold.Core.Project.AuthoredRefusalException>(MainWindowViewModel.PartFailure("cloth",
            new Remold.Core.Project.AuthoredRefusalException("The base color on cloth came back marked unchanged.") { Part = "cloth" }));
        Assert.Equal("cloth", refusal.Part);
        Assert.Equal("The base color on cloth came back marked unchanged", refusal.Message);

        var unnamed = Assert.IsType<Remold.Core.Project.AuthoredRefusalException>(MainWindowViewModel.PartFailure("cloth",
            new Remold.Core.Project.AuthoredRefusalException("Couldn't read the mesh.")));
        Assert.Equal("cloth", unnamed.Part);
        Assert.Equal("Couldn't read the mesh (cloth)", unnamed.Message);

        var disk = new IOException("Disk full.");
        var other = Assert.IsType<InvalidOperationException>(MainWindowViewModel.PartFailure("cloth", disk));
        Assert.Same(disk, other.InnerException);
        Assert.Equal("Disk full (cloth)", other.Message);
    }
}

/// <summary>The restart a Settings Save offers after a language change: in the language just picked, only
/// when this process can start itself again, and closing through the app's own close flow.</summary>
[Collection("UiCulture")]
public class LanguageRestartFlowTests
{
    private const string AppExe = @"C:\Apps\Lab\Remold.App.exe";

    [Fact]
    public async Task Switching_back_to_english_asks_in_english_and_a_yes_closes_through_the_close_flow()
    {
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);
            var vm = new MainWindowViewModel(startLoad: false);
            MainWindowViewModel.LanguageRestartPrompt? asked = null;

            bool close = await vm.OfferLanguageRestartAsync(null, AppExe,
                prompt => { asked = prompt; return Task.FromResult(true); });

            Assert.True(close);
            Assert.Equal("Restart Doll Remolding Lab?", asked!.Value.Title);
            Assert.Equal(Catalog.Neutral(Strings.Rm, nameof(Strings.RestartLanguageBody)), asked.Value.Body);
            Assert.Equal("Restart", asked.Value.Restart);
            Assert.Equal("Later", asked.Value.Later);
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Fact]
    public async Task Switching_to_chinese_asks_in_chinese_and_later_keeps_the_app_running()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        MainWindowViewModel.LanguageRestartPrompt? asked = null;

        bool close = await vm.OfferLanguageRestartAsync(Catalog.SimplifiedChinese, AppExe,
            prompt => { asked = prompt; return Task.FromResult(false); });

        Assert.False(close);
        Assert.Equal(Catalog.In(Catalog.SimplifiedChinese, Strings.Rm, nameof(Strings.RestartLanguageTitle)), asked!.Value.Title);
        Assert.NotEqual(Strings.RestartLanguageTitle, asked.Value.Title);
        Assert.Equal(Catalog.In(Catalog.SimplifiedChinese, Strings.Rm, nameof(Strings.RestartLanguageLater)), asked.Value.Later);
    }

    [Fact]
    public async Task A_process_that_cannot_start_itself_again_is_never_asked()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        bool asked = false;

        bool close = await vm.OfferLanguageRestartAsync(Catalog.SimplifiedChinese, @"C:\Program Files\dotnet\dotnet.exe",
            _ => { asked = true; return Task.FromResult(true); });

        Assert.False(close);
        Assert.False(asked);
    }
}

/// <summary>A Settings Save measures a language change against what the settings file last took.</summary>
[Collection("Dispatcher")]
public class LanguageRestartRetryTests
{
    [Fact]
    public void A_save_whose_write_failed_offers_the_restart_when_the_retry_lands()
    {
        using var snapshot = new SettingsSnapshot();
        new LabSettings().Save();
        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false };
        int writes = 0;
        vm.TrySaveSettings = settings => ++writes > 1 && settings.TrySave();
        SettingsResult Chinese() => new() { Language = Catalog.SimplifiedChinese, Author = vm.BuildSettingsInput().Author };

        Assert.False(vm.ApplySettings(Chinese()));   // the write failed: nothing to restart into yet
        Assert.True(vm.ApplySettings(Chinese()));    // the retry landed
        Assert.Equal(Catalog.SimplifiedChinese, LabSettings.Load().Language);
        Assert.False(vm.ApplySettings(Chinese()));   // the file already holds it
    }
}

/// <summary>The first-run window's language box: a pick rewrites the window's texts in place, so the box
/// the arrow keys are stepping through stays the same control and keeps focus.</summary>
[Collection("UiCulture")]
public class FirstRunGateWindowTests
{
    [Fact]
    public async Task A_pick_rewrites_the_texts_and_keeps_the_same_focused_language_box()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(App.App));
        try
        {
            await session.Dispatch(() =>
            {
                var language = new FirstRunLanguage(null, CultureInfo.GetCultureInfo("en-US"));
                var window = new FirstRunGateWindow(language);
                window.Show();
                var row = (StackPanel)((StackPanel)window.Content!).Children[0];
                var label = (TextBlock)row.Children[0];
                var box = (ComboBox)row.Children[1];
                box.Focus();
                Assert.Equal("Language", label.Text);

                box.SelectedItem = language.Choices.Single(choice => choice.Tag == Catalog.SimplifiedChinese);

                Assert.Equal(Catalog.SimplifiedChinese, Catalog.CurrentTag);
                Assert.Same(box, ((StackPanel)((StackPanel)window.Content!).Children[0]).Children[1]);
                Assert.True(box.IsFocused);
                Assert.Equal(Strings.FirstRunLanguageLabel, label.Text);
                Assert.Equal(Strings.FirstRunAccept,
                    ((Button)((StackPanel)((StackPanel)window.Content!).Children[3]).Children[0]).Content);

                box.SelectedItem = language.Choices[0];

                Assert.Equal("Language", label.Text);
                window.Close();
            }, CancellationToken.None);
        }
        finally { Catalog.Apply(Catalog.English); }
    }
}

/// <summary>The English-names notice through the game load itself: a load that fails after its names were
/// read in English keeps the notice beside the failure, because the English names stay on screen.</summary>
[Collection("UiCulture")]
public class NamesNoticeLoadRouteTests
{
    [Fact]
    public async Task A_load_that_fails_after_reading_english_names_keeps_the_names_notice()
    {
        using var snapshot = new SettingsSnapshot();
        new LabSettings().Save();
        using var g = new TempGame();
        // An install the locator accepts: the catalog and the manifest exist, and the catalog is not one, so
        // the forward view's read fails after the roster's names were read.
        string bundles = Path.GetDirectoryName(g.At(@"GF2_Exilium_Data\LocalCache\Data\AssetBundles_Windows\x"))!;
        g.WriteGameDir();
        File.WriteAllText(Path.Combine(bundles, "08dfe7d89b6fe56375d6dfec87ffcc8a.bundle"), "x");
        g.WriteTable(LocalizationDb.TableName(LocalizationDb.EnglishLocale),
            TempGame.TableBytes(new[] { TempGame.LangRow(701, "First Team") }));
        g.WriteTable(LocalizationDb.TableName(LocalizationDb.SimplifiedChineseLocale), new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0A, 0xFF });
        g.WriteTable("GunCharacterData", TempGame.TableBytes(new[] { TempGame.GunCharRow(2, "Vesna", "VesnaSSR", 200, 0) }));
        g.WriteTable("ModelConfigData", TempGame.TableBytes(new[] { TempGame.ModelConfigRow(200, "VesnaSSR01") }));
        g.WriteTable("BattleSummonedData", TempGame.TableBytes(Array.Empty<byte[]>()));
        string? gameDir = Environment.GetEnvironmentVariable("GF2_GAME_DIR");
        Environment.SetEnvironmentVariable("GF2_GAME_DIR", g.Root);
        using var session = HeadlessUnitTestSession.StartNew(typeof(App.App));
        try
        {
            await session.Dispatch(async () =>
            {
                Catalog.Apply(Catalog.SimplifiedChinese);
                var vm = new MainWindowViewModel(startLoad: true);
                for (int i = 0; i < 1000 && !vm.GameRescanOffered; i++) await Task.Delay(10);

                Assert.True(vm.GameRescanOffered);
                Assert.Contains(Strings.RosterNamesEnglishDetail, vm.NoticeStatus.Detail, StringComparison.Ordinal);
                Assert.Contains(Strings.GameDataUnreadableDetail, vm.NoticeStatus.Detail, StringComparison.Ordinal);
            }, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GF2_GAME_DIR", gameDir);
            Catalog.Apply(Catalog.English);
        }
    }
}

/// <summary>The mod a language-change restart opens again: what the closing app hands over, how it reaches the
/// new process, and what the new process does with it.</summary>
[Collection("Dispatcher")]
public class LanguageRestartReopenTests
{
    private const string AppExe = @"C:\Apps\Lab\Remold.App.exe";

    private static string SavedMod(TempGame temp, string name)
    {
        string root = temp.At(name);
        AuthoredProjectSerializer.Save(AuthoredProjectDocument.New().Session.Snapshot(), ModProject.ManifestPathFor(root));
        return root;
    }

    [Theory]
    [InlineData(WorkflowStep.Pick)]
    [InlineData(WorkflowStep.Edit)]
    [InlineData(WorkflowStep.Build)]
    public void A_target_reads_back_from_the_arguments_it_writes(WorkflowStep step)
    {
        var target = new ReopenTarget(@"C:\Mods\My mod", step);

        Assert.Equal(target, ReopenTarget.Parse(target.ToArguments().ToList()));
    }

    public static TheoryData<string[]?> OtherLaunches => new()
    {
        null,
        Array.Empty<string>(),
        new[] { @"C:\Mods\My mod" },
        new[] { "--reopen", @"C:\Mods\My mod" },
        new[] { "--reopen", "", "Edit" },
        new[] { "--reopen", @"C:\Mods\My mod", "Export" },
        new[] { "--reopen", @"C:\Mods\My mod", "edit" },
        new[] { "--reopen", @"C:\Mods\My mod", "1" },
        new[] { "--reopen", @"C:\Mods\My mod", "Edit", "extra" },
    };

    [Theory]
    [MemberData(nameof(OtherLaunches))]
    public void Any_other_launch_names_no_mod(string[]? args)
    {
        Assert.Null(ReopenTarget.Parse(args));
    }

    [Fact]
    public void A_restart_with_a_target_starts_the_app_with_it_and_one_without_starts_it_bare()
    {
        var target = new ReopenTarget(@"C:\Mods\My mod", WorkflowStep.Build);

        var start = Program.RelaunchStart(restartRequested: true, AppExe, target);

        Assert.Equal(target.ToArguments(), start!.ArgumentList);
        Assert.Empty(Program.RelaunchStart(restartRequested: true, AppExe)!.ArgumentList);
        Assert.Null(Program.RelaunchStart(restartRequested: false, AppExe, target));
    }

    [Fact]
    public async Task A_saved_open_mod_is_handed_over_with_its_step()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        string root = SavedMod(temp, "mod");
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());
        Assert.True(await vm.OpenModAsync(root));
        vm.SelectedStep = WorkflowStep.Build;

        var target = vm.ReopenAfterRestart();

        Assert.Equal(Path.GetFullPath(root), Path.GetFullPath(target!.Folder));
        Assert.Equal(WorkflowStep.Build, target.Step);
    }

    [Fact]
    public async Task Home_or_unsaved_changes_hand_over_no_mod()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());
        Assert.Null(vm.ReopenAfterRestart());   // Home, nothing open

        Assert.True(await vm.OpenModAsync(SavedMod(temp, "mod")));
        vm.IsDirty = true;   // the state a failed save leaves
        Assert.Null(vm.ReopenAfterRestart());
    }

    [Fact]
    public async Task The_new_process_opens_the_handed_over_mod_on_its_step_once()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        string root = SavedMod(temp, "mod");
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work(),
            restartTarget: new ReopenTarget(root, WorkflowStep.Build));

        await vm.OpenRestartTargetAsync();

        Assert.False(vm.ShowHome);
        Assert.Equal(WorkflowStep.Build, vm.SelectedStep);
        vm.SelectedStep = WorkflowStep.Edit;
        await vm.OpenRestartTargetAsync();
        Assert.Equal(WorkflowStep.Edit, vm.SelectedStep);   // a second call opens nothing
    }

    [Fact]
    public async Task A_handed_over_mod_that_is_gone_leaves_home_showing()
    {
        using var settings = new SettingsSnapshot();
        using var temp = new TempGame();
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work(),
            restartTarget: new ReopenTarget(temp.At("gone"), WorkflowStep.Edit));

        await vm.OpenRestartTargetAsync();

        Assert.True(vm.ShowHome);
    }

    [Fact]
    public async Task An_ordinary_launch_opens_nothing()
    {
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());

        await vm.OpenRestartTargetAsync();

        Assert.True(vm.ShowHome);
    }
}
