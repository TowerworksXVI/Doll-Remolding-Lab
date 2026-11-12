using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using Remold.App;
using Remold.Core.Text;
using Remold.Tools.GenStrings;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The tests that change the process's UI language or turn pseudolocalization on. They run alone,
/// after every parallel test, because every other test reads the catalogs and expects English.</summary>
[CollectionDefinition("UiCulture", DisableParallelization = true)]
public sealed class UiCultureCollection { }

/// <summary>The string catalogs: each generated accessor matches its resx, every entry carries its comment,
/// translations keep the English holes, and the runtime reads, formats and joins as the app expects.</summary>
public class CatalogTests
{
    public static TheoryData<string> Catalogs => new() { nameof(CoreStrings), nameof(Strings) };

    private static Type Accessor(string catalog) => catalog switch
    {
        nameof(CoreStrings) => typeof(CoreStrings),
        nameof(Strings) => typeof(Strings),
        _ => throw new ArgumentOutOfRangeException(nameof(catalog)),
    };

    private static string ResxPath(string fileName) => Path.Combine(AppContext.BaseDirectory, "catalog", fileName);

    private static IReadOnlyList<CatalogEntry> Entries(string catalog)
    {
        var (entries, problems) = ResxCatalog.Read(ResxPath(catalog + ".resx"), catalog);
        Assert.Empty(problems);
        return entries;
    }

    private static ResourceManager Rm(string catalog) =>
        (ResourceManager)Accessor(catalog).GetField("Rm", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static IEnumerable<PropertyInfo> Properties(Type accessor) =>
        accessor.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

    private static IEnumerable<MethodInfo> Methods(Type accessor) =>
        accessor.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName);

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void The_accessor_has_exactly_one_member_per_resx_entry(string catalog)
    {
        var keys = Entries(catalog).Select(entry => entry.Key).Order(StringComparer.Ordinal);
        var accessor = Accessor(catalog);
        var members = Properties(accessor).Select(p => p.Name).Concat(Methods(accessor).Select(m => m.Name))
            .Order(StringComparer.Ordinal);

        Assert.Equal(keys, members);
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void An_entry_with_holes_is_a_method_taking_one_argument_per_hole_and_a_plain_entry_is_a_property(
        string catalog)
    {
        var accessor = Accessor(catalog);
        var properties = Properties(accessor).ToDictionary(p => p.Name);
        var methods = Methods(accessor).ToDictionary(m => m.Name);
        foreach (var entry in Entries(catalog))
        {
            var holes = ResxCatalog.Holes(entry.Value);
            if (holes.Count == 0)
            {
                Assert.True(properties.ContainsKey(entry.Key), $"{entry.Key} has no holes and must be a property.");
                continue;
            }
            Assert.True(methods.TryGetValue(entry.Key, out var method), $"{entry.Key} has holes and must be a method.");
            Assert.Equal(holes.Max + 1, method!.GetParameters().Length);
        }
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void Every_entry_comment_is_in_the_catalog_shape_and_describes_every_hole(string catalog)
    {
        var (entries, problems) = ResxCatalog.Read(ResxPath(catalog + ".resx"), catalog);

        Assert.Empty(problems);
        Assert.NotEmpty(entries);
    }

    [Fact]
    public void A_comment_that_leaves_a_hole_undescribed_or_breaks_the_shape_is_a_problem_naming_the_key()
    {
        string path = Path.Combine(Path.GetTempPath(), "remold-tests", "catalog-" + Guid.NewGuid().ToString("N") + ".resx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <data name="TwoHoles" xml:space="preserve">
                <value>{0} of {1}</value>
                <comment>surface: label · where: a page · params: {0}=part · budget: short</comment>
              </data>
              <data name="NoComment" xml:space="preserve">
                <value>Plain</value>
              </data>
              <data name="Misshapen" xml:space="preserve">
                <value>Plain</value>
                <comment>a label somewhere</comment>
              </data>
            </root>
            """);
        try
        {
            var (_, problems) = ResxCatalog.Read(path, "Strings");

            Assert.Contains(problems, p => p.StartsWith("TwoHoles:", StringComparison.Ordinal) && p.Contains("{1}"));
            Assert.Contains(problems, p => p.StartsWith("NoComment:", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.StartsWith("Misshapen:", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    private static readonly IReadOnlyDictionary<string, (string Folder, string Namespace, string BaseName)> Generated =
        new Dictionary<string, (string, string, string)>
        {
            [nameof(CoreStrings)] = ("Remold.Core", "Remold.Core", "Remold.Core.Text.CoreStrings"),
            [nameof(Strings)] = ("Remold.App", "Remold.App", "Remold.App.Text.Strings"),
        };

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void The_committed_accessor_is_what_the_generator_writes_from_the_committed_resx(string catalog)
    {
        var (folder, ns, baseName) = Generated[catalog];
        string committed = File.ReadAllText(Path.Combine(SourceHygieneTests.RepoRoot(), "src", folder, "Text", catalog + ".cs"))
            .Replace("\r\n", "\n");

        string generated = ResxCatalog.Generate(Entries(catalog), "Text/" + catalog + ".resx", ns, catalog, baseName);

        Assert.True(committed == generated,
            $"{catalog}.cs is not what tools/GenStrings writes from {catalog}.resx; re-run the generator.");
    }

    /// <summary>Plural pairs whose singular sentence leaves the count out, as "catalog: base key" and the
    /// count's hole. Each one is a reviewed exception for that one hole: every other hole still has to match,
    /// and a translation of the pair still has to keep each entry's own holes.</summary>
    private static readonly Dictionary<string, int> PluralHoleAllowlist = new(StringComparer.Ordinal)
    {
        // "Remove {0}'s outfit?" names one outfit without a number
        ["Strings: RemoveCharacterTitle"] = 1,
    };

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void Every_one_entry_has_an_other_entry_with_the_same_holes_and_the_reverse(string catalog)
    {
        var values = Entries(catalog).ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            string? baseKey = key.EndsWith("_One", StringComparison.Ordinal) ? key[..^4]
                : key.EndsWith("_Other", StringComparison.Ordinal) ? key[..^6]
                : null;
            if (baseKey is null) continue;
            string twin = baseKey + (key.EndsWith("_One", StringComparison.Ordinal) ? "_Other" : "_One");
            Assert.True(values.TryGetValue(twin, out var other), $"{catalog}: '{key}' has no '{twin}'.");
            var holes = ResxCatalog.Holes(value);
            var twinHoles = ResxCatalog.Holes(other!);
            if (PluralHoleAllowlist.TryGetValue($"{catalog}: {baseKey}", out int countHole))
            {
                holes.Remove(countHole);
                twinHoles.Remove(countHole);
            }
            Assert.True(holes.SetEquals(twinHoles), $"{catalog}: '{key}' and '{twin}' do not use the same holes.");
        }
    }

    /// <summary>Read as bytes: an XML parse turns a CRLF into LF, so it cannot see the carriage return a
    /// checkout's line-ending conversion would put into a value. An encoded carriage return is refused too.</summary>
    [Fact]
    public void No_catalog_file_contains_a_carriage_return()
    {
        foreach (string resx in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "catalog"), "*.resx"))
        {
            byte[] bytes = File.ReadAllBytes(resx);
            Assert.False(Array.IndexOf(bytes, (byte)'\r') >= 0, $"{Path.GetFileName(resx)} contains a carriage return.");
            string text = System.Text.Encoding.UTF8.GetString(bytes);
            foreach (string encoded in new[] { "&#13;", "&#xD;", "&#xd;", "&#x0D;", "&#x0d;" })
                Assert.False(text.Contains(encoded, StringComparison.Ordinal),
                    $"{Path.GetFileName(resx)} contains an encoded carriage return.");
        }
    }

    /// <summary>The translations the app ships, one per neutral catalog.</summary>
    public static TheoryData<string> Translations => new() { nameof(CoreStrings), nameof(Strings) };

    [Theory]
    [MemberData(nameof(Translations))]
    public void The_simplified_chinese_catalog_translates_every_entry_and_nothing_else(string catalog)
    {
        var english = ResxCatalog.ReadRaw(ResxPath(catalog + ".resx")).Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        var chinese = ResxCatalog.ReadRaw(ResxPath(catalog + ".zh-Hans.resx")).Select(entry => entry.Key).ToList();

        Assert.Equal(chinese.Count, chinese.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(english.Except(chinese, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        Assert.Empty(chinese.Except(english, StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>The window builds the terms from their lines: a "** " line is a section header and an empty
    /// line is spacing. The Chinese terms keep the English layout line for line, then one blank line and two
    /// closing lines: which text governs, and how the translation was made.</summary>
    [Fact]
    public void The_chinese_terms_keep_the_english_sections_and_spacing_and_add_a_blank_line_and_two_closing_lines()
    {
        string[] englishLines = Value(nameof(Strings), nameof(Strings.AcceptableUseBody), ".resx").Split('\n');
        string[] chineseLines = Value(nameof(Strings), nameof(Strings.AcceptableUseBody), ".zh-Hans.resx").Split('\n');

        Assert.Equal(englishLines.Count(line => line.StartsWith("** ", StringComparison.Ordinal)),
            chineseLines.Count(line => line.StartsWith("** ", StringComparison.Ordinal)));
        Assert.Equal(englishLines.Length + 3, chineseLines.Length);
        Assert.Equal(englishLines.Select(Shape), chineseLines.Take(englishLines.Length).Select(Shape));
        Assert.Equal("ebb", string.Concat(chineseLines.Skip(englishLines.Length).Select(Shape)));

        static char Shape(string line) => line.Length == 0 ? 'e' : line.StartsWith("** ", StringComparison.Ordinal) ? 'h' : 'b';
    }

    /// <summary>A Chinese entry ends a sentence exactly where its English does, with the full-width twin of
    /// the English mark: a clause the English leaves open for a frame or a join to close stays open, so the
    /// frame's own sentence end is the only one. A closing quote after the mark counts as the mark.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_chinese_entry_ends_a_sentence_where_its_english_does_with_the_full_width_mark(string catalog)
    {
        var twin = new Dictionary<char, char> { ['.'] = '。', ['?'] = '？', ['!'] = '！', ['…'] = '…', [':'] = '：' };
        var english = ResxCatalog.ReadRaw(ResxPath(catalog + ".resx"))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        var wrong = new List<string>();
        foreach (var (key, value, _, _) in ResxCatalog.ReadRaw(ResxPath(catalog + ".zh-Hans.resx")))
        {
            char? englishEnd = End(english[key]);
            char? chineseEnd = End(value);
            char? expected = englishEnd is { } mark && twin.TryGetValue(mark, out char full) ? full : null;
            bool chineseCloses = chineseEnd is { } c && twin.ContainsValue(c);
            if (expected is null ? chineseCloses : chineseEnd != expected) wrong.Add(key);
        }
        Assert.Empty(wrong);

        // The last mark, looking through one closing quote that follows a sentence mark.
        static char? End(string text)
        {
            string value = text.TrimEnd();
            if (value.Length == 0) return null;
            char last = value[^1];
            bool quoteAfterMark = last is '\'' or '"' or '’' or '”' && value.Length > 1
                && value[^2] is '.' or '?' or '!' or '…' or ':' or '。' or '？' or '！' or '：';
            return quoteAfterMark ? value[^2] : last;
        }
    }

    private static string Value(string catalog, string key, string suffix) =>
        ResxCatalog.ReadRaw(ResxPath(catalog + suffix)).Single(entry => entry.Key == key).Value;

    [Fact]
    public void A_typed_entry_or_a_name_the_accessor_reads_is_a_problem_naming_the_key()
    {
        string path = Path.Combine(Path.GetTempPath(), "remold-tests", "catalog-" + Guid.NewGuid().ToString("N") + ".resx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <root>
              <data name="Picture" type="System.Byte[], mscorlib" mimetype="application/x-microsoft.net.object.bytearray.base64">
                <value>AAAA</value>
                <comment>surface: label · where: a page · budget: short</comment>
              </data>
              <data name="Catalog" xml:space="preserve">
                <value>Plain</value>
                <comment>surface: label · where: a page · budget: short</comment>
              </data>
              <data name="ResourceManager" xml:space="preserve">
                <value>Plain</value>
                <comment>surface: label · where: a page · budget: short</comment>
              </data>
            </root>
            """);
        try
        {
            var (entries, problems) = ResxCatalog.Read(path, "Strings");

            Assert.Empty(entries);
            Assert.Contains(problems, p => p.StartsWith("Picture:", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.StartsWith("Catalog:", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.StartsWith("ResourceManager:", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_parameter_name_that_would_break_the_accessor_is_escaped_or_suffixed()
    {
        Assert.Equal("@object", ResxCatalog.ParameterName("object", 0, new HashSet<string>()));
        Assert.Equal("nameofValue", ResxCatalog.ParameterName("nameof", 0, new HashSet<string>()));
        var entry = new CatalogEntry("Named", "{0}", "label", "a page", ["nameof"], "short");

        string code = ResxCatalog.Generate([entry], "Text/X.resx", "N", "X", "N.X");

        Assert.Contains("Named(object? nameofValue) => Catalog.Format(Rm, nameof(Named), nameofValue);", code, StringComparison.Ordinal);
        Assert.Contains("public static class X", code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_translation_has_only_keys_the_english_has_and_the_same_holes_in_each()
    {
        foreach (string translation in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "catalog"), "*.zh-Hans.resx"))
        {
            string neutralName = Path.GetFileName(translation).Split('.')[0];
            var neutral = ResxCatalog.ReadRaw(ResxPath(neutralName + ".resx"))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            foreach (var (key, value, _, typed) in ResxCatalog.ReadRaw(translation))
            {
                Assert.False(typed, $"{Path.GetFileName(translation)} '{key}' is not a string.");
                Assert.True(neutral.TryGetValue(key, out var english),
                    $"{Path.GetFileName(translation)} has '{key}', which {neutralName}.resx does not.");
                Assert.True(ResxCatalog.Holes(english!).SetEquals(ResxCatalog.Holes(value)),
                    $"{Path.GetFileName(translation)} '{key}' does not use the same holes as the English.");
            }
        }
    }

    [Fact]
    public void Reading_an_entry_the_catalog_does_not_have_throws_naming_the_key()
    {
        var missingKey = Assert.Throws<InvalidOperationException>(() => Catalog.Get(Rm(nameof(CoreStrings)), "NoSuchEntry"));
        Assert.Contains("NoSuchEntry", missingKey.Message, StringComparison.Ordinal);
        Assert.Contains("Remold.Core.Text.CoreStrings", missingKey.Message, StringComparison.Ordinal);

        var missingCatalog = Assert.Throws<InvalidOperationException>(() =>
            Catalog.Get(new ResourceManager("Remold.Core.Text.NoSuchCatalog", typeof(CoreStrings).Assembly), "ListSeparator"));
        Assert.Contains("ListSeparator", missingCatalog.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Catalogs))]
    public void The_assembly_embeds_the_catalog_under_the_base_name_its_accessor_reads(string catalog)
    {
        var rm = Rm(catalog);

        Assert.Contains(rm.BaseName + ".resources", Accessor(catalog).Assembly.GetManifestResourceNames());
        Assert.Equal(catalog == nameof(CoreStrings) ? "Remold.Core.Text.CoreStrings" : "Remold.App.Text.Strings", rm.BaseName);
    }

    [Fact]
    public void A_list_reads_as_a_sentence_lists_its_items()
    {
        Assert.Equal("", Catalog.List([]));
        Assert.Equal("a", Catalog.List(["a"]));
        Assert.Equal("a and b", Catalog.List(["a", "b"]));
        Assert.Equal("a, b and c", Catalog.List(["a", "b", "c"]));
    }

    [Fact]
    public void A_list_past_its_shown_count_names_the_first_items_and_how_many_more()
    {
        Assert.Equal("a and b", Catalog.List(["a", "b"], 2));
        Assert.Equal("a, b and 1 more", Catalog.List(["a", "b", "c"], 2));
        Assert.Equal("a, b and 3 more", Catalog.List(["a", "b", "c", "d", "e"], 2));
        Assert.Equal("a and 1 more", Catalog.List(["a", "b"], 1));
    }

    [Fact]
    public void Plural_picks_the_one_sentence_only_for_a_count_of_one()
    {
        Assert.Equal("one", Catalog.Plural(1, "one", "other"));
        Assert.Equal("other", Catalog.Plural(0, "one", "other"));
        Assert.Equal("other", Catalog.Plural(2, "one", "other"));
    }
}

/// <summary>What selecting a language does to the process. Every test puts English back.</summary>
[Collection("UiCulture")]
public class CatalogLanguageTests
{
    private static ResourceManager AppRm => (ResourceManager)typeof(Strings)
        .GetField("Rm", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static ResourceManager CoreRm => (ResourceManager)typeof(CoreStrings)
        .GetField("Rm", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void A_read_in_a_named_language_ignores_the_language_the_process_runs_in()
    {
        try
        {
            string chineseRestart = Catalog.In(Catalog.SimplifiedChinese, AppRm, nameof(Strings.RestartLanguageConfirm));
            Catalog.Apply(Catalog.SimplifiedChinese);
            Assert.Equal(Strings.RestartLanguageConfirm, chineseRestart);
            Assert.NotEqual("Restart", chineseRestart);

            Assert.Equal("Restart", Catalog.In(null, AppRm, nameof(Strings.RestartLanguageConfirm)));
            Assert.Equal("Restart", Catalog.In("en-US", AppRm, nameof(Strings.RestartLanguageConfirm)));
            Assert.Equal("Couldn't save this map: In use.",
                Catalog.In(Catalog.English, CoreRm, nameof(CoreStrings.CouldntActionCause), "save this map", "In use."));

            Catalog.Apply(Catalog.Pseudo);
            Assert.Equal("Restart", Catalog.In(Catalog.English, AppRm, nameof(Strings.RestartLanguageConfirm)));
            Assert.Equal("[Restart]", Catalog.In(Catalog.Pseudo, AppRm, nameof(Strings.RestartLanguageConfirm)));
            Assert.Throws<InvalidOperationException>(() => Catalog.In(Catalog.English, AppRm, "NoSuchEntry"));
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Theory]
    [InlineData("Stopped", "Stopped.")]
    [InlineData("Stopped.", "Stopped.")]
    [InlineData("Stopped?", "Stopped?")]
    [InlineData("Reading…", "Reading…")]
    [InlineData("Opened 'X'", "Opened 'X'.")]
    [InlineData("It said 'stop.'", "It said 'stop.'")]
    [InlineData("生成已停止。", "生成已停止。")]
    [InlineData("确定吗？", "确定吗？")]
    [InlineData("  ", "")]
    public void An_english_line_closes_with_a_full_stop_unless_it_already_ends_a_sentence(string line, string closed)
    {
        Assert.Equal(closed, Catalog.Sentence(line));
    }

    [Theory]
    [InlineData("生成已停止", "生成已停止。")]
    [InlineData("生成已停止。", "生成已停止。")]
    [InlineData("完成！", "完成！")]
    [InlineData("正在读取…", "正在读取…")]
    [InlineData("已打开“X”", "已打开“X”。")]
    [InlineData("他说“停止。”", "他说“停止。”")]
    [InlineData("Stopped.", "Stopped.")]
    public void A_chinese_line_closes_with_the_full_width_stop_unless_it_already_ends_a_sentence(string line, string closed)
    {
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);

            Assert.Equal(closed, Catalog.Sentence(line));
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Theory]
    [InlineData("Disk full.", "Disk full")]
    [InlineData("Disk full. ", "Disk full")]
    [InlineData("磁盘已满。", "磁盘已满")]
    [InlineData("Wait..", "Wait.")]
    [InlineData("Really?", "Really?")]
    [InlineData("Reading…", "Reading…")]
    public void A_clause_loses_one_closing_full_stop_in_either_script_and_keeps_any_other_end(string text, string clause)
    {
        Assert.Equal(clause, Catalog.Clause(text));
    }

    [Fact]
    public void Pseudolocalization_brackets_every_entry_and_keeps_a_formatted_entrys_arguments()
    {
        try
        {
            Catalog.Apply(Catalog.Pseudo);

            Assert.True(Catalog.Pseudolocalize);
            Assert.Equal("[About Doll Remolding Lab]", Strings.AboutTitle);
            Assert.Equal("[Couldn't save this map.]", CoreStrings.CouldntAction("save this map"));
            Assert.Equal("[Couldn't save this map: In use.]", CoreStrings.CouldntActionCause("save this map", "In use."));
            Assert.Equal(CultureInfo.GetCultureInfo("en"), CultureInfo.CurrentUICulture);
        }
        finally { Catalog.Apply(Catalog.English); }

        Assert.False(Catalog.Pseudolocalize);
        Assert.Equal("About Doll Remolding Lab", Strings.AboutTitle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xx")]
    [InlineData("en")]
    public void No_tag_or_an_unknown_tag_selects_english(string? tag)
    {
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);
            Catalog.Apply(tag);

            Assert.Equal(Catalog.English, Catalog.CurrentTag);
            Assert.Equal(CultureInfo.GetCultureInfo("en"), CultureInfo.CurrentUICulture);
            Assert.Equal(CultureInfo.GetCultureInfo("en"), CultureInfo.DefaultThreadCurrentUICulture);
            Assert.False(Catalog.Pseudolocalize);
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-hans")]
    [InlineData("ZH-HANS")]
    [InlineData("zh-CN")]
    [InlineData("zh-SG")]
    public void A_tag_whose_culture_reaches_simplified_chinese_selects_it_whatever_its_case(string tag)
    {
        try
        {
            Catalog.Apply(tag);

            Assert.Equal(Catalog.SimplifiedChinese, Catalog.CurrentTag);
            Assert.Equal(CultureInfo.GetCultureInfo("zh-Hans"), CultureInfo.CurrentUICulture);
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Theory]
    [InlineData("zh-TW", "en")]
    [InlineData("zh-Hant", "en")]
    [InlineData("QPS-PLOC", "qps-ploc")]
    [InlineData("EN", "en")]
    public void Resolve_reads_the_tag_case_insensitively_and_keeps_traditional_chinese_on_english(string tag, string expected)
    {
        Assert.Equal(expected, Catalog.Resolve(tag));
    }

    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("qps-ploc")]
    public void A_neutral_read_is_the_english_entry_unbracketed_in_any_language(string tag)
    {
        var rm = (System.Resources.ResourceManager)typeof(Strings)
            .GetField("Rm", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        try
        {
            Catalog.Apply(tag);

            Assert.Equal("About Doll Remolding Lab", Catalog.Neutral(rm, nameof(Strings.AboutTitle)));
            Assert.Throws<InvalidOperationException>(() => Catalog.Neutral(rm, "NoSuchEntry"));
        }
        finally { Catalog.Apply(Catalog.English); }
    }

    [Fact]
    public void Selecting_simplified_chinese_changes_only_the_ui_culture()
    {
        var formatting = CultureInfo.CurrentCulture;
        var defaultFormatting = CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            Catalog.Apply(Catalog.SimplifiedChinese);

            Assert.Equal(Catalog.SimplifiedChinese, Catalog.CurrentTag);
            Assert.Equal(CultureInfo.GetCultureInfo("zh-Hans"), CultureInfo.CurrentUICulture);
            Assert.Equal(CultureInfo.GetCultureInfo("zh-Hans"), CultureInfo.DefaultThreadCurrentUICulture);
            Assert.Same(formatting, CultureInfo.CurrentCulture);
            Assert.Same(defaultFormatting, CultureInfo.DefaultThreadCurrentCulture);
        }
        finally { Catalog.Apply(Catalog.English); }

        Assert.Equal(Catalog.English, Catalog.CurrentTag);
        Assert.Equal(CultureInfo.GetCultureInfo("en"), CultureInfo.CurrentUICulture);
    }
}
