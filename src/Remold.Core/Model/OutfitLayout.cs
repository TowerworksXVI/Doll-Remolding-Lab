using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Remold.Core.Model;

/// <summary>One editable part of an outfit, with a friendly display label.</summary>
public sealed record PartEntry(string Token, string Display, int? Variant);

/// <summary>A group of parts shown together (a modular variant set, or the shared/base parts).</summary>
public sealed record PartGroup(string Label, int Order, IReadOnlyList<PartEntry> Parts);

/// <summary>
/// Turns an outfit's raw mesh-part tokens into a grouped, readable layout. Modular outfits carry
/// <c>P&lt;n&gt;_&lt;slot&gt;&lt;m&gt;</c> tokens (the DB's <c>ItemData</c> ClothesMod rows) whose slots
/// are <b>independent</b> — <c>P&lt;n&gt;</c> is one slot's variant, not a coherent "full look" — hence
/// two groups, <b>Shared</b> and <b>Modular</b>, with the part identity leading each modular label
/// (<c>"Body 1 · Variant 3"</c>). Labels derive from the token, not the DB's name text (a generic Chinese
/// fallback with no clean EN loc link), so they need no localization.
/// </summary>
public sealed partial class OutfitLayout
{
    public bool IsModular { get; }
    public IReadOnlyList<PartGroup> Groups { get; }

    private OutfitLayout(bool isModular, IReadOnlyList<PartGroup> groups)
    {
        IsModular = isModular;
        Groups = groups;
    }

    [GeneratedRegex(@"^P(\d+)_(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex VariantPrefix();

    [GeneratedRegex(@"^([A-Za-z]+)(\d*)$")]
    private static partial Regex WordNum();

    public static OutfitLayout Build(IEnumerable<string> partTokens)
    {
        var entries = partTokens.Select(Parse).ToList();
        bool modular = entries.Any(e => e.Variant is not null);

        // each group ordered by PartTokenOrder, so a slot's parts sit together and numbers read 1,2,…,10
        var groups = new List<PartGroup>();
        var shared = entries.Where(e => e.Variant is null)
                            .OrderBy(e => e.Token, PartTokenOrder).ToList();
        var mod = entries.Where(e => e.Variant is not null)
                         .OrderBy(e => e.Token, PartTokenOrder).ToList();
        if (shared.Count > 0) groups.Add(new PartGroup("Shared", 0, shared));
        if (mod.Count > 0) groups.Add(new PartGroup("Modular", 1, mod));

        return new OutfitLayout(modular, groups);
    }

    private static PartEntry Parse(string token)
    {
        int? variant = null;
        string rest = token;
        var m = VariantPrefix().Match(token);
        if (m.Success) { variant = int.Parse(m.Groups[1].Value); rest = m.Groups[2].Value; }
        var display = variant is null ? Humanize(rest) : $"{Humanize(rest)}  ·  Variant {variant}";
        return new PartEntry(token, display, variant);
    }

    /// <summary>"body1" → "Body 1", "cloth4_trans" → "Cloth 4 Trans", "head" → "Head".</summary>
    private static string Humanize(string rest)
    {
        var words = rest.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(seg =>
        {
            var wn = WordNum().Match(seg);
            if (wn.Success)
            {
                var word = Cap(wn.Groups[1].Value);
                var num = wn.Groups[2].Value;
                return num.Length > 0 ? $"{word} {num}" : word;
            }
            return Cap(seg);
        });
        return string.Join(' ', words);
    }

    private static string Cap(string s) =>
        s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..].ToLowerInvariant();

    /// <summary>
    /// The one part-token order Pick and Edit both list by, on three keys:
    /// <list type="number">
    /// <item><b>slot family</b> — leading letters minus the <c>P&lt;n&gt;_</c> modular prefix, so a slot's
    ///   parts sit together regardless of variant or sub-number;</item>
    /// <item><b>modular variant</b> (<c>P1 &lt; P2 &lt; P3</c>, shared = 0 so it leads);</item>
    /// <item>the rest of the base <b>naturally</b> (digit runs numeric), so <c>cloth2</c> precedes
    ///   <c>cloth10</c>.</item>
    /// </list>
    /// </summary>
    public static IComparer<string> PartTokenOrder { get; } = new PartTokenComparer();

    // "P2_body1" → ("body1", 2); a non-modular token → (token, 0), so shared sorts ahead of variants
    private static (string Base, int Variant) SplitVariantPrefix(string token)
    {
        var m = VariantPrefix().Match(token);
        return m.Success ? (m.Groups[2].Value, int.Parse(m.Groups[1].Value)) : (token, 0);
    }

    // leading letters of a base token ("body1_trans" → "body"), grouping a slot's numbered/state
    // sub-parts so the modular variant, not the sub-number, is the next sort key
    private static string SlotFamily(string baseToken)
    {
        int i = 0;
        while (i < baseToken.Length && char.IsLetter(baseToken[i])) i++;
        return baseToken[..i];
    }

    private sealed class PartTokenComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (x is null) return y is null ? 0 : -1;
            if (y is null) return 1;
            var (bx, vx) = SplitVariantPrefix(x);
            var (by, vy) = SplitVariantPrefix(y);
            int c = string.Compare(SlotFamily(bx), SlotFamily(by), StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;                       // 1) slot family (body / cloth / head)
            if (vx != vy) return vx - vy;               // 2) modular variant (P1 < P2 < P3; shared = 0)
            c = NaturalCompare(bx, by);                 // 3) the rest, naturally (body < body1; cloth2 < cloth10)
            if (c != 0) return c;
            return string.CompareOrdinal(x, y);
        }

        // segment by segment: digit runs numerically ("cloth2" < "cloth10"), the rest case-insensitively
        private static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    var na = a.AsSpan(si, i - si).TrimStart('0');
                    var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length - nb.Length;   // more digits = larger number
                    int cmp = na.SequenceCompareTo(nb);                         // equal width → lexical == numeric
                    if (cmp != 0) return cmp;
                    if ((i - si) != (j - sj)) return (i - si) - (j - sj);       // equal value, fewer leading zeros first
                }
                else
                {
                    int cmp = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                    if (cmp != 0) return cmp;
                    i++; j++;
                }
            }
            return (a.Length - i) - (b.Length - j);   // the shorter (prefix) one first
        }
    }
}
