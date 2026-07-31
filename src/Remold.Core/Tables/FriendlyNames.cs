using System.Collections.Generic;
using Remold.Core.Model;

namespace Remold.Core.Tables;

/// <summary>
/// The ONE render-time home for turning an internal character/outfit KEY into the friendly label the
/// modder reads. Two entry points, one fallback rule:
/// <list type="bullet">
///   <item><see cref="Label(Character)"/> / <see cref="Label(Outfit)"/> — caller holds the enriched
///     model: <c>DisplayName ?? Name/Stem</c>.</item>
///   <item><see cref="Character(string)"/> / <see cref="Outfit(string)"/> — caller holds only the
///     internal KEY; backed by lookup tables built from the enriched roster.</item>
/// </list>
///
/// <para><b>The keys stay internal.</b> Never changes what is persisted or matched on — the selection
/// ledger, the manifest's <c>character</c>/<c>outfit</c> and the export subfolders keep the internal
/// name/stem; a friendly value written there would fail to match the roster on reopen/import.</para>
///
/// <para>No localized name → the token, by design: most enemy/prop/dorm stems and NPC characters are
/// correctly nameless (see <see cref="DisplayNames"/>), so the internal token IS the right label.</para>
/// </summary>
public sealed class FriendlyNames
{
    private readonly Dictionary<string, string> _charByName;   // internal Character.Name → localized name
    private readonly Dictionary<string, string> _byStem;       // outfit Stem → localized name

    private FriendlyNames(Dictionary<string, string> charByName, Dictionary<string, string> byStem)
    {
        _charByName = charByName;
        _byStem = byStem;
    }

    /// <summary>Build the reverse lookup from an already-enriched roster
    /// (<see cref="DisplayNames.Enrich"/> has filled the display fields). Only keys that resolved to a
    /// localized name are stored; the rest fall back to their own token at lookup time. First writer wins
    /// on a duplicate name/stem, so a repeated key never throws.</summary>
    public static FriendlyNames FromRoster(IReadOnlyList<Character> roster)
    {
        var charByName = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        var byStem = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var c in roster)
        {
            if (!string.IsNullOrEmpty(c.DisplayName) && !string.IsNullOrEmpty(c.Name))
                charByName.TryAdd(c.Name, c.DisplayName!);
            foreach (var o in c.Outfits)
                if (!string.IsNullOrEmpty(o.DisplayName) && !string.IsNullOrEmpty(o.Stem))
                    byStem.TryAdd(o.Stem, o.DisplayName!);
        }
        return new FriendlyNames(charByName, byStem);
    }

    /// <summary>An empty resolver — every lookup falls back to its token. Used before the roster is
    /// available so callers never special-case a null resolver.</summary>
    public static FriendlyNames Empty { get; } = new(
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase));

    // ---- direct (caller holds the enriched model) ----

    /// <summary>Localized <see cref="Model.Character.DisplayName"/>, else the internal
    /// <see cref="Model.Character.Name"/>.</summary>
    public static string Label(Character c) =>
        string.IsNullOrEmpty(c.DisplayName) ? c.Name : c.DisplayName!;

    /// <summary>Localized <see cref="Model.Outfit.DisplayName"/>, else the
    /// <see cref="Model.Outfit.Stem"/>.</summary>
    public static string Label(Outfit o) =>
        string.IsNullOrEmpty(o.DisplayName) ? o.Stem : o.DisplayName!;

    /// <summary>The single "&lt;kind&gt; · &lt;name&gt;" home for the Characters tree and the Animation set
    /// nodes ("Base · Hyper Trigger"), so the two can't drift. Callers append their own trailing detail.
    /// <see cref="OutfitKind.Other"/> is an internal catch-all enum name, not a category, so it is dropped
    /// and the bare label stands alone.</summary>
    public static string KindAndLabel(Outfit o) =>
        o.Kind == OutfitKind.Other ? Label(o) : $"{o.Kind}  ·  {Label(o)}";

    // ---- reverse (caller holds only the internal key) ----

    /// <summary>The friendly label for an internal character name; an unknown/nameless key falls back to
    /// the name itself, never to an empty string.</summary>
    public string Character(string internalName) =>
        string.IsNullOrEmpty(internalName) ? internalName
            : _charByName.TryGetValue(internalName, out var n) ? n : internalName;

    /// <summary>The friendly label for an outfit stem; an unknown/nameless stem falls back to itself.</summary>
    public string Outfit(string stem) =>
        string.IsNullOrEmpty(stem) ? stem
            : _byStem.TryGetValue(stem, out var n) ? n : stem;

    /// <summary>The whole subject label — "&lt;character&gt; · &lt;kind&gt; · &lt;outfit&gt;" — for the
    /// workbench header and the confirm/status lines. The separator lives here alone, so no two surfaces
    /// can name the same subject differently.</summary>
    public string Subject(string internalName, Outfit outfit) =>
        $"{Character(internalName)}  ·  {KindAndLabel(outfit)}";
}
