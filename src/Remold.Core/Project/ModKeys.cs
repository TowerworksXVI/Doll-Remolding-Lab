using System;
using System.Collections.Generic;
using System.Text;

namespace Remold.Core.Project;

/// <summary>
/// The one place a toggle-key string is normalized, compared and turned into an ini identifier. A key is
/// stored as the tokens 3DMigoto's own <c>key =</c> line takes — optional modifiers then the key itself,
/// space-separated and upper-case (<c>F6</c>, <c>CTRL SHIFT H</c>). Null/blank means NO key: the thing it
/// would gate is always on, which is the unkeyed emission this build already produced.
/// </summary>
public static class ModKeys
{
    /// <summary>The modifier tokens a <c>key =</c> line takes ahead of the key itself.</summary>
    private static readonly string[] Modifiers = { "CTRL", "ALT", "SHIFT", "NO_MODIFIERS" };

    /// <summary>Trim, upper-case and single-space a typed/captured key, or null when it is blank, carries a
    /// character an ini <c>key =</c> line can't take, or isn't shaped like one. Refusing rather than passing
    /// an odd string through keeps a bad key out of the emitted ini, where it would fail silently at load
    /// time.
    ///
    /// <para>Shape: zero or more modifiers then ONE key token. The shape test is also what keeps
    /// <see cref="VariableFor"/> injective — it folds the token separator to <c>_</c>, so a leading token
    /// that isn't a modifier, or a key token wearing a modifier's name as a <c>_</c>-prefix, would let two
    /// different keys share one variable and collapse into a single <c>[Constants]</c>/<c>[Key]</c>
    /// declaration.</para></summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var tokens = key.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        foreach (var t in tokens)
            foreach (var c in t)
                if (!char.IsLetterOrDigit(c) && c != '_')
                    return null;
        for (int i = 0; i < tokens.Length - 1; i++)
            if (Array.IndexOf(Modifiers, tokens[i]) < 0) return null;
        foreach (var m in Modifiers)
            if (tokens[^1].StartsWith(m + "_", StringComparison.Ordinal)) return null;
        return string.Join(' ', tokens);
    }

    /// <summary>Two keys are the same binding. Both sides are normalized first, so a stored "f6" and a
    /// captured "F6" are one key, exactly as they would be in game.</summary>
    public static bool SameKey(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return na is not null && nb is not null && string.Equals(na, nb, StringComparison.Ordinal);
    }

    /// <summary>The ini variable a key gates through: <c>zz_key_</c> plus the key's own tokens, lower-cased
    /// with the separators collapsed to <c>_</c>. Derived FROM the key, so two changes bound to one key
    /// share one variable and toggle together — the shared-key state each key control reports beside itself.
    /// Two DIFFERENT keys never share one: <see cref="Normalize"/> refuses the shapes that would collide
    /// here.</summary>
    public static string VariableFor(string key)
    {
        var n = Normalize(key) ?? throw new ArgumentException($"'{key}' is not a usable toggle key", nameof(key));
        var sb = new StringBuilder("zz_key_");
        foreach (var c in n)
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        return sb.ToString();
    }

    /// <summary>How a key reads on screen, or <paramref name="empty"/> when there is none.</summary>
    public static string Display(string? key, string empty = "") => Normalize(key) ?? empty;

    /// <summary>Every distinct key in <paramref name="keys"/>, in first-seen order, blanks dropped. The
    /// emitter's <c>[Constants]</c>/<c>[Key…]</c> pass walks this so a shared key declares once.</summary>
    public static IReadOnlyList<string> Distinct(IEnumerable<string?> keys)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var k in keys)
            if (Normalize(k) is { } n && seen.Add(n)) list.Add(n);
        return list;
    }
}
