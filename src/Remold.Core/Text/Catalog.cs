using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Threading;

namespace Remold.Core.Text;

/// <summary>The runtime behind both string catalogs: which UI language is in force, and how a catalog entry
/// is read, formatted, pluralized and joined into a list. The generated accessors
/// (<see cref="CoreStrings"/> and the App's <c>Strings</c>) read every entry through here.</summary>
public static class Catalog
{
    public const string English = "en";
    public const string SimplifiedChinese = "zh-Hans";

    /// <summary>A diagnostic language: every catalog string comes back bracketed, so text that is not read
    /// through a catalog stands out on screen.</summary>
    public const string Pseudo = "qps-ploc";

    /// <summary>The languages the app offers, in menu order: tag and the name shown for it, written in that
    /// language.</summary>
    public static IReadOnlyList<(string Tag, string DisplayName)> Languages { get; } =
        [(English, "English"), (SimplifiedChinese, "中文（简体）")];

    public static string CurrentTag { get; private set; } = English;

    public static bool Pseudolocalize { get; private set; }

    /// <summary>Selects the UI language for this process. The app calls it at startup from the settings file,
    /// and from the first-run window before any view model or background work exists. The UI culture flows
    /// with each piece of work's execution context, so work already running keeps the language it started
    /// under, and a later call reaches only work started after it. That is why a language change made in
    /// Settings applies the next time the app starts.
    /// <para>The tag is read case-insensitively. A culture whose parent chain reaches <c>zh-Hans</c>
    /// (<c>zh-CN</c>, <c>zh-SG</c>) selects Simplified Chinese; null, empty or any other tag means English.
    /// Sets CultureInfo.DefaultThreadCurrentUICulture and CurrentThread.CurrentUICulture only; CurrentCulture
    /// is never touched, so numbers, dates and file names format as before.</para></summary>
    public static void Apply(string? tag)
    {
        string applied = Resolve(tag);
        var culture = CultureInfo.GetCultureInfo(applied == SimplifiedChinese ? SimplifiedChinese : English);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Pseudolocalize = applied == Pseudo;
        CurrentTag = applied;
    }

    /// <summary>The language <see cref="Apply"/> selects for <paramref name="tag"/>: one of
    /// <see cref="English"/>, <see cref="SimplifiedChinese"/> or <see cref="Pseudo"/>.</summary>
    public static string Resolve(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return English;
        if (string.Equals(tag, Pseudo, StringComparison.OrdinalIgnoreCase)) return Pseudo;
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(tag); }
        catch (CultureNotFoundException) { return English; }
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            if (string.Equals(c.Name, SimplifiedChinese, StringComparison.OrdinalIgnoreCase)) return SimplifiedChinese;
        return English;
    }

    /// <summary>One entry in the current UI language. An entry the current language does not translate comes
    /// back in English (the resource manager's own fallback to the neutral set). An entry the catalog does
    /// not have at all throws: a missing string is a defect, and an empty one on screen would hide it.</summary>
    public static string Get(ResourceManager rm, string key)
    {
        string value = Read(rm, key, culture: null);
        return Pseudolocalize ? "[" + value + "]" : value;
    }

    /// <summary>One entry in English whatever the UI language, never bracketed: for text whose exact English
    /// bytes are recorded, such as the accepted terms' stamp. A missing entry throws, as in
    /// <see cref="Get"/>.</summary>
    public static string Neutral(ResourceManager rm, string key) =>
        Read(rm, key, CultureInfo.GetCultureInfo(English));

    private static string Read(ResourceManager rm, string key, CultureInfo? culture)
    {
        string? value;
        try { value = rm.GetString(key, culture); }
        catch (MissingManifestResourceException e)
        {
            throw new InvalidOperationException(
                $"The string catalog '{rm.BaseName}' is not embedded, so '{key}' cannot be read.", e);
        }
        if (value is null)
            throw new InvalidOperationException($"The string catalog '{rm.BaseName}' has no entry '{key}'.");
        return value;
    }

    /// <summary>One entry in the language <see cref="Resolve"/> selects for <paramref name="tag"/>, whatever
    /// language the process runs in, with its holes filled when <paramref name="args"/> are given: for text
    /// that speaks the language the modder just picked. Bracketed only when the tag is the pseudolocale. A
    /// missing entry throws, as in <see cref="Get"/>.</summary>
    public static string In(string? tag, ResourceManager rm, string key, params object?[] args)
    {
        string applied = Resolve(tag);
        string value = Read(rm, key,
            CultureInfo.GetCultureInfo(applied == SimplifiedChinese ? SimplifiedChinese : English));
        if (args.Length > 0) value = string.Format(CultureInfo.CurrentCulture, value, args);
        return applied == Pseudo ? "[" + value + "]" : value;
    }

    /// <summary>An entry with its holes filled. Arguments format under CurrentCulture, which the language
    /// choice never changes.</summary>
    public static string Format(ResourceManager rm, string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(rm, key), args);

    /// <summary>Whether <paramref name="text"/> already ends a sentence in either script: its last character
    /// is <c>. ! ? 。 ！ ？ …</c>, or a closing quote that follows one of them. A closing quote after a name
    /// (<c>'X'</c>, <c>“X”</c>) does not end a sentence; the sentence end follows it.</summary>
    public static bool EndsSentence(string text)
    {
        string value = text.TrimEnd();
        if (value.Length == 0) return false;
        char last = value[^1];
        if (IsSentenceEnd(last)) return true;
        return last is '\'' or '"' or '’' or '”' && value.Length > 1 && IsSentenceEnd(value[^2]);
    }

    private static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or '。' or '！' or '？' or '…';

    /// <summary><paramref name="text"/> ready to be embedded in another sentence: trailing white space and
    /// ONE closing full stop (<c>.</c> or <c>。</c>) removed. Any other ending is kept.</summary>
    public static string Clause(string text)
    {
        string value = text.TrimEnd();
        return value.Length > 0 && value[^1] is '.' or '。' ? value[..^1] : value;
    }

    /// <summary><paramref name="text"/> closed as a sentence: as it is when it already ends one
    /// (<see cref="EndsSentence"/>), otherwise followed by the UI language's sentence end
    /// (<see cref="CoreStrings.SentenceEnd"/>). An empty text stays empty.</summary>
    public static string Sentence(string text)
    {
        string value = text.TrimEnd();
        return value.Length == 0 || EndsSentence(value) ? value : value + CoreStrings.SentenceEnd;
    }

    /// <summary>Picks the sentence for <paramref name="count"/>. Callers pass sentences already formatted from
    /// a <c>_One</c>/<c>_Other</c> entry pair, so both are read and formatted and one is discarded: the cost
    /// of keeping every plural a plain pair of entries.</summary>
    public static string Plural(int count, string one, string other) => count == 1 ? one : other;

    /// <summary>The items joined as a sentence lists them: "a", "a and b", "a, b and c". An empty list is the
    /// empty string.</summary>
    public static string List(IReadOnlyList<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return "";
        if (items.Count == 1) return items[0];
        string head = string.Join(CoreStrings.ListSeparator, Head(items, items.Count - 1));
        return head + CoreStrings.ListLastSeparator + items[^1];
    }

    /// <summary>As <see cref="List(IReadOnlyList{string})"/> for up to <paramref name="shown"/> items; past
    /// that, the first <paramref name="shown"/> items and how many more there are: "a, b and 3 more".</summary>
    public static string List(IReadOnlyList<string> items, int shown)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(shown, 1);
        if (items.Count <= shown) return List(items);
        string head = string.Join(CoreStrings.ListSeparator, Head(items, shown));
        int rest = items.Count - shown;
        return Plural(rest, CoreStrings.ListAndMore_One(head, rest), CoreStrings.ListAndMore_Other(head, rest));
    }

    private static IEnumerable<string> Head(IReadOnlyList<string> items, int count)
    {
        for (int i = 0; i < count; i++) yield return items[i];
    }
}
