using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Remold.Tools.GenStrings;

/// <summary>One catalog entry read from a neutral resx, with its comment parsed. <see cref="Params"/> holds
/// one meaning per hole, in hole order; its count is the accessor's arity.</summary>
public sealed record CatalogEntry(
    string Key, string Value, string Surface, string Where, IReadOnlyList<string> Params, string Budget);

/// <summary>The resx grammar the catalogs follow: the comment shape, the format holes, the key rules. The
/// generator and the catalog tests both read through this file, so the rule an entry is checked against is
/// the rule its accessor was generated from.</summary>
public static class ResxCatalog
{
    public static readonly IReadOnlyList<string> Surfaces =
        ["button", "label", "tooltip", "status", "error", "confirmation", "title", "empty-state", "note", "other"];

    public static readonly IReadOnlyList<string> Budgets = ["short", "sentence", "paragraph"];

    private static readonly Regex CommentShape = new(
        @"^surface: (?<surface>[a-z-]+) · where: (?<where>.+?)(?: · params: (?<params>.+?))? · budget: (?<budget>[a-z]+)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ParamSeparator = new(@"; (?=\{\d+\}=)", RegexOptions.CultureInvariant);

    private static readonly Regex ParamItem = new(@"^\{(?<index>\d+)\}=(?<meaning>.+)$", RegexOptions.CultureInvariant);

    /// <summary>Every <c>data</c> element of a resx as key and value, in file order. Translations are read
    /// this way: they carry no comment requirement of their own. <c>Typed</c> marks an element with a
    /// <c>type</c> or <c>mimetype</c> attribute, which is not a string; a catalog refuses it rather than
    /// passing over it.</summary>
    public static IReadOnlyList<(string Key, string Value, string? Comment, bool Typed)> ReadRaw(string resxPath)
    {
        var doc = XDocument.Load(resxPath, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new FormatException($"{resxPath} has no root element.");
        return root.Elements("data")
            .Select(data => (
                Key: (string?)data.Attribute("name") ?? "",
                Value: data.Element("value")?.Value ?? "",
                Comment: data.Element("comment")?.Value,
                Typed: data.Attribute("type") is not null || data.Attribute("mimetype") is not null))
            .ToList();
    }

    /// <summary>A neutral resx's entries with every rule checked. Returns the entries and, separately, one
    /// line per problem naming the key; a caller that gets any problem must not trust the entries.</summary>
    public static (IReadOnlyList<CatalogEntry> Entries, IReadOnlyList<string> Problems) Read(
        string resxPath, string className)
    {
        var entries = new List<CatalogEntry>();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value, comment, typed) in ReadRaw(resxPath))
        {
            if (typed) { problems.Add($"{key}: has a type or mimetype attribute; a catalog entry is a string."); continue; }
            if (!IsKey(key)) { problems.Add($"{key}: not a PascalCase identifier."); continue; }
            if (key == className || ReservedKeys.Contains(key)) { problems.Add($"{key}: reserved member name."); continue; }
            if (!seen.Add(key)) { problems.Add($"{key}: duplicate key."); continue; }

            SortedSet<int> holes;
            try { holes = Holes(value); }
            catch (FormatException e) { problems.Add($"{key}: {e.Message}"); continue; }
            int arity = holes.Count == 0 ? 0 : holes.Max + 1;
            try { _ = string.Format(CultureInfo.InvariantCulture, value, new object?[arity]); }
            catch (FormatException e) { problems.Add($"{key}: not a valid format string ({e.Message})."); continue; }

            if (string.IsNullOrWhiteSpace(comment)) { problems.Add($"{key}: missing <comment>."); continue; }
            if (ParseComment(comment) is not { } parsed)
            {
                problems.Add($"{key}: comment is not in the shape "
                    + "'surface: … · where: … · params: {0}=…; {1}=… · budget: …'.");
                continue;
            }
            if (!Surfaces.Contains(parsed.Surface)) problems.Add($"{key}: unknown surface '{parsed.Surface}'.");
            if (!Budgets.Contains(parsed.Budget)) problems.Add($"{key}: unknown budget '{parsed.Budget}'.");

            var meanings = new List<string>();
            for (int i = 0; i < arity; i++)
            {
                if (parsed.Params.TryGetValue(i, out var meaning) && meaning.Length > 0) meanings.Add(meaning);
                else problems.Add($"{key}: hole {{{i}}} is not described in params:.");
            }
            foreach (int described in parsed.Params.Keys.Where(i => i >= arity))
                problems.Add($"{key}: params: describes {{{described}}}, which the text does not have.");
            if (arity == 0 && parsed.HasParams) problems.Add($"{key}: params: on an entry with no holes.");

            entries.Add(new CatalogEntry(key, value, parsed.Surface, parsed.Where, meanings, parsed.Budget));
        }
        return (entries, problems);
    }

    /// <summary>The comment's four parts, or null when it is not in the required shape. Params maps each
    /// described hole index to its meaning.</summary>
    public static (string Surface, string Where, IReadOnlyDictionary<int, string> Params, bool HasParams, string Budget)?
        ParseComment(string comment)
    {
        var match = CommentShape.Match(comment.Trim());
        if (!match.Success) return null;
        var described = new Dictionary<int, string>();
        var paramsGroup = match.Groups["params"];
        if (paramsGroup.Success)
        {
            foreach (string piece in ParamSeparator.Split(paramsGroup.Value))
            {
                var item = ParamItem.Match(piece);
                if (!item.Success) return null;
                int index = int.Parse(item.Groups["index"].Value, CultureInfo.InvariantCulture);
                if (!described.TryAdd(index, item.Groups["meaning"].Value.Trim())) return null;
            }
        }
        return (match.Groups["surface"].Value, match.Groups["where"].Value, described, paramsGroup.Success,
            match.Groups["budget"].Value);
    }

    /// <summary>The hole indices a .NET composite format string uses. Doubled braces are literal; a lone
    /// closing brace or an unclosed hole is a <see cref="FormatException"/>.</summary>
    public static SortedSet<int> Holes(string value)
    {
        var holes = new SortedSet<int>();
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '}')
            {
                if (i + 1 < value.Length && value[i + 1] == '}') { i++; continue; }
                throw new FormatException($"lone '}}' at {i}.");
            }
            if (c != '{') continue;
            if (i + 1 < value.Length && value[i + 1] == '{') { i++; continue; }
            int close = value.IndexOf('}', i + 1);
            if (close < 0) throw new FormatException($"unclosed '{{' at {i}.");
            string hole = value[(i + 1)..close];
            int end = 0;
            while (end < hole.Length && char.IsAsciiDigit(hole[end])) end++;
            if (end == 0) throw new FormatException($"hole at {i} has no index.");
            holes.Add(int.Parse(hole[..end], CultureInfo.InvariantCulture));
            i = close;
        }
        return holes;
    }

    /// <summary>A parameter name from a hole's meaning: words camelCased, non-identifier characters dropped.
    /// <paramref name="taken"/> collects names already used so a repeat gets a numeric suffix.</summary>
    public static string ParameterName(string meaning, int index, ISet<string> taken)
    {
        var words = Regex.Split(meaning, @"[^\p{L}\p{Nd}]+")
            .Where(word => word.Length > 0).ToList();
        var name = new StringBuilder();
        for (int w = 0; w < words.Count; w++)
        {
            string word = words[w];
            if (w == 0)
                name.Append(word.All(ch => !char.IsLower(ch)) ? word.ToLowerInvariant()
                    : char.ToLowerInvariant(word[0]) + word[1..]);
            else name.Append(char.ToUpperInvariant(word[0])).Append(word[1..]);
        }
        string baseName = name.Length == 0 ? "arg" + index.ToString(CultureInfo.InvariantCulture)
            : char.IsAsciiDigit(name[0]) ? "_" + name : name.ToString();
        if (Keywords.Contains(baseName)) baseName = "@" + baseName;
        // A parameter named nameof turns the body's nameof(Key) into a call of that parameter, and an @
        // does not change the name it declares, so this one takes a suffix.
        else if (baseName == "nameof") baseName = "nameofValue";
        string candidate = baseName;
        for (int n = 2; !taken.Add(candidate); n++)
            candidate = baseName + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    /// <summary>The names the accessor's body reads by simple name inside the class, besides the class's own:
    /// an entry named after one would hide it and the accessor would not compile.</summary>
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.Ordinal)
    {
        "Rm", "Catalog", "ResourceManager",
    };

    /// <summary>The accessor class for <paramref name="entries"/>, as the generator writes it: LF line
    /// endings, members sorted by key, no timestamps. <paramref name="source"/> is the resx's folder and file
    /// name, named in the header.</summary>
    public static string Generate(IReadOnlyList<CatalogEntry> entries, string source, string ns,
        string className, string baseName)
    {
        var code = new StringBuilder();
        void Line(string text = "") => code.Append(text).Append('\n');

        Line($"// <auto-generated> Generated by tools/GenStrings from {source}. Do not edit; re-run the tool after editing the resx. </auto-generated>");
        Line("#nullable enable");
        Line("using System.Resources;");
        Line("using Remold.Core.Text;");
        Line($"namespace {ns};");
        Line($"public static class {className}");
        Line("{");
        Line($"    internal static readonly ResourceManager Rm = new(\"{baseName}\", typeof({className}).Assembly) {{ IgnoreCase = false }};");
        foreach (var entry in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var summary = new List<string> { entry.Surface, entry.Where, "\"" + entry.Value + "\"" };
            if (entry.Params.Count > 0)
                summary.Add(string.Join("; ", entry.Params.Select((meaning, i) =>
                    "{" + i.ToString(CultureInfo.InvariantCulture) + "}=" + meaning)));
            Line($"    /// <summary>{Doc(string.Join(" · ", summary))}</summary>");
            if (entry.Params.Count == 0)
            {
                Line($"    public static string {entry.Key} => Catalog.Get(Rm, nameof({entry.Key}));");
                continue;
            }
            var taken = new HashSet<string>(StringComparer.Ordinal);
            var names = entry.Params.Select((meaning, i) => ParameterName(meaning, i, taken)).ToList();
            string parameters = string.Join(", ", names.Select(name => "object? " + name));
            string arguments = string.Join(", ", names);
            Line($"    public static string {entry.Key}({parameters}) => Catalog.Format(Rm, nameof({entry.Key}), {arguments});");
        }
        Line("}");
        return code.ToString();
    }

    // Doc text: XML-escaped, and a line break in the English shows as \n so the summary stays on one line.
    private static string Doc(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    private static bool IsKey(string key) =>
        key.Length > 0 && char.IsAsciiLetterUpper(key[0]) && key.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit",
        "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int",
        "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
        "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };
}
