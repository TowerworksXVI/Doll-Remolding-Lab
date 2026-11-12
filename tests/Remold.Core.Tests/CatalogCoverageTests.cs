using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The app's views carry no on-screen text of their own: every caption, tooltip and watermark is read
/// from the string catalog, so a language switch reaches all of it.</summary>
public class CatalogCoverageTests
{
    /// <summary>The attributes of the app's views that put text on screen.</summary>
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Title", "ToolTip.Tip", "Watermark", "EmptyLabel",
    };

    /// <summary>Literal values of those attributes that are not text a user reads, as "file: value". Each one
    /// is a reviewed exception; glyph-only values carry no letter and need no entry.</summary>
    private static readonly HashSet<string> AxamlAllowlist = new(StringComparer.Ordinal)
    {
        // the information glyph is a letter to Unicode, not a word
        "Views/EditPageView.axaml: ℹ",
    };

    /// <summary>Literal text assignments in the code-built windows and view-models that are not text a user
    /// reads, as "file: literal". Each one is a reviewed exception.</summary>
    private static readonly HashSet<string> CodeAllowlist = new(StringComparer.Ordinal)
    {
    };

    private static readonly Regex Letter = new(@"\p{L}", RegexOptions.CultureInvariant);

    private static readonly Regex CodeTextAssignment = new(
        @"\b(?:Title|Content|Text|Watermark|Header|EmptyLabel)\s*=\s*\$?@?""(?<value>(?:[^""\\]|\\.)*)""",
        RegexOptions.CultureInvariant);

    private static string AppSource => Path.Combine(SourceHygieneTests.RepoRoot(), "src", "Remold.App");

    private static IEnumerable<string> AppFiles(string pattern) =>
        Directory.EnumerateFiles(AppSource, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));

    [Fact]
    public void No_view_markup_sets_a_text_attribute_to_a_literal()
    {
        var found = new List<string>();
        foreach (string path in AppFiles("*.axaml"))
        {
            string file = Path.GetRelativePath(AppSource, path).Replace('\\', '/');
            foreach (var attribute in XDocument.Load(path).Descendants().SelectMany(element => element.Attributes()))
            {
                if (!TextAttributes.Contains(attribute.Name.LocalName)) continue;
                string value = attribute.Value;
                if (value.StartsWith('{') || !Letter.IsMatch(value)) continue;
                if (AxamlAllowlist.Contains($"{file}: {value}")) continue;
                found.Add($"{file}: {attribute.Name.LocalName}=\"{value}\"");
            }
        }

        Assert.True(found.Count == 0, "Literal text in the app's markup:\n" + string.Join("\n", found));
    }

    [Fact]
    public void No_code_built_control_assigns_its_text_a_literal()
    {
        var found = new List<string>();
        foreach (string path in AppFiles("*.cs"))
        {
            string file = Path.GetRelativePath(AppSource, path).Replace('\\', '/');
            foreach (Match match in CodeTextAssignment.Matches(File.ReadAllText(path)))
            {
                string value = match.Groups["value"].Value;
                if (!Letter.IsMatch(value)) continue;
                if (CodeAllowlist.Contains($"{file}: {value}")) continue;
                found.Add($"{file}: {match.Value}");
            }
        }

        Assert.True(found.Count == 0, "Literal text in the app's code-built controls:\n" + string.Join("\n", found));
    }
}
