using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Remold.Core.Project;

/// <summary>One key standing in one position of its cycle: the key itself and the state index a gate on it
/// tests for. A key's cycle is ordinal — one variable stepping 0, 1, … <see cref="KeyCycle.StateCount"/>-1
/// and wrapping — so a gate names the position rather than an on/off truth.
///
/// <para>A bare key string converts to state 0, which is where a two-state group's content sits: the shapes
/// that predate longer cycles keep saying what they always said.</para>
///
/// <para>CAUTION: the conversion has a struct to fill, so a null or blank key string becomes a reference
/// naming NO key rather than a null reference — and a bare <c>null</c> assigned to a
/// <c>KeyRef?</c> can be taken for that same thing. Nothing reads <see cref="Key"/> directly for that
/// reason: <see cref="ModKeys.NormalizeRef"/> is the one place either shape is turned into "no key", and
/// every gate, declaration and step goes through it.</para></summary>
public readonly record struct KeyRef(string Key, int State)
{
    public KeyRef(string key) : this(key, 0) { }

    public static implicit operator KeyRef(string key) => new(key, 0);
}

/// <summary>One emitted key's cycle: how many positions it steps through, which one it holds at load, and
/// whether the position it was left in survives a game restart. A key resets every session unless
/// <see cref="Persist"/> opts it out, so <see cref="StartState"/> is where a session starts when no saved
/// position stands — the first session, and every session of an unpersisted key.</summary>
public sealed record KeyCycle(string Key, int StateCount, int StartState, bool Persist = false);

/// <summary>
/// The one place a toggle-key string is normalized, compared and turned into an ini identifier. A key is
/// stored as the tokens 3DMigoto's own <c>key =</c> line takes — optional modifiers then the key itself,
/// space-separated (<c>F6</c>, <c>CTRL SHIFT H</c>, <c>PERIOD</c>). Null/blank means NO key: the thing it
/// would gate is always on, which is the unkeyed emission this build already produced.
///
/// <para>The accepted vocabulary mirrors 3DMigoto's own key-name table: every name in it, the punctuation
/// spellings it takes for the OEM keys (<c>.</c>, <c>[</c>, <c>;</c>, …), single letters and digits, and a
/// <c>0x</c> hex virtual-key code for anything the table leaves unnamed. Every spelling of one physical
/// key folds to ONE canonical token — <c>PGUP</c>, <c>PAGE_UP</c> and <c>0x21</c> are all <c>PRIOR</c> —
/// so however a key was captured, typed or loaded, it is the same binding, the same emitted line and the
/// same ini variable. A name the table doesn't hold is refused rather than passed through: 3DMigoto drops
/// a binding it can't parse at load time with nothing on screen.</para>
/// </summary>
public static class ModKeys
{
    /// <summary>The special token 3DMigoto expands to "none of the modifier keys held". It never survives
    /// normalization: a key with no modifiers is stored bare, and the emitter binds every bare key
    /// <c>no_modifiers</c> itself, so accepting the spelled-out form as a distinct key would give one
    /// binding two ini variables.</summary>
    private const string NoModifiers = "NO_MODIFIERS";

    /// <summary>Modifier tokens in their one canonical order. Order-folding is what keeps
    /// <c>SHIFT CTRL H</c> and <c>CTRL SHIFT H</c> — one binding in game, where held keys have no order —
    /// from declaring two variables that both step on one press.</summary>
    private static readonly string[] ModifierOrder =
        { "CTRL", "LCTRL", "RCTRL", "ALT", "LALT", "RALT", "SHIFT", "LSHIFT", "RSHIFT", "LWIN", "RWIN" };

    /// <summary>The named keys: canonical token, virtual-key code, then every other spelling the 3DMigoto
    /// table takes for that code. Letters, digits, NUMPAD0-9 and F1-F24 are added programmatically.
    /// The multi-word spellings the table carries for old configs ("Num 1", "Prnt Scrn") are absent on
    /// purpose: a space-separated key list cannot carry a token with a space in it.</summary>
    private static readonly (string Canonical, int Vk, string[] Aliases)[] NamedKeys =
    {
        ("LBUTTON", 0x01, Array.Empty<string>()),
        ("RBUTTON", 0x02, Array.Empty<string>()),
        ("CANCEL", 0x03, Array.Empty<string>()),
        ("MBUTTON", 0x04, Array.Empty<string>()),
        ("XBUTTON1", 0x05, Array.Empty<string>()),
        ("XBUTTON2", 0x06, Array.Empty<string>()),
        ("BACKSPACE", 0x08, new[] { "BACK", "BACK_SPACE" }),
        ("TAB", 0x09, Array.Empty<string>()),
        ("CLEAR", 0x0C, Array.Empty<string>()),
        ("ENTER", 0x0D, new[] { "RETURN" }),
        ("SHIFT", 0x10, Array.Empty<string>()),
        ("CTRL", 0x11, new[] { "CONTROL" }),
        ("ALT", 0x12, new[] { "MENU" }),
        ("PAUSE", 0x13, Array.Empty<string>()),
        ("CAPS_LOCK", 0x14, new[] { "CAPITAL", "CAPS", "CAPSLOCK" }),
        ("KANA", 0x15, new[] { "HANGUEL", "HANGUL" }),
        ("JUNJA", 0x17, Array.Empty<string>()),
        ("FINAL", 0x18, Array.Empty<string>()),
        ("KANJI", 0x19, new[] { "HANJA" }),
        ("ESCAPE", 0x1B, Array.Empty<string>()),
        ("CONVERT", 0x1C, Array.Empty<string>()),
        ("NONCONVERT", 0x1D, Array.Empty<string>()),
        ("ACCEPT", 0x1E, Array.Empty<string>()),
        ("MODECHANGE", 0x1F, Array.Empty<string>()),
        ("SPACE", 0x20, Array.Empty<string>()),
        ("PRIOR", 0x21, new[] { "PGUP", "PAGEUP", "PAGE_UP" }),
        ("NEXT", 0x22, new[] { "PGDN", "PAGEDOWN", "PAGE_DOWN" }),
        ("END", 0x23, Array.Empty<string>()),
        ("HOME", 0x24, Array.Empty<string>()),
        ("LEFT", 0x25, Array.Empty<string>()),
        ("UP", 0x26, Array.Empty<string>()),
        ("RIGHT", 0x27, Array.Empty<string>()),
        ("DOWN", 0x28, Array.Empty<string>()),
        ("SELECT", 0x29, Array.Empty<string>()),
        ("PRINT", 0x2A, Array.Empty<string>()),
        ("EXECUTE", 0x2B, Array.Empty<string>()),
        ("PRINT_SCREEN", 0x2C, new[] { "SNAPSHOT", "PRSCR", "PRINTSCREEN" }),
        ("INSERT", 0x2D, Array.Empty<string>()),
        ("DELETE", 0x2E, Array.Empty<string>()),
        ("HELP", 0x2F, Array.Empty<string>()),
        ("LWIN", 0x5B, new[] { "LEFT_WIN", "LEFT_WINDOWS" }),
        ("RWIN", 0x5C, new[] { "RIGHT_WIN", "RIGHT_WINDOWS" }),
        ("APPS", 0x5D, Array.Empty<string>()),
        ("SLEEP", 0x5F, Array.Empty<string>()),
        ("MULTIPLY", 0x6A, new[] { "*" }),
        ("ADD", 0x6B, new[] { "+" }),
        ("SEPARATOR", 0x6C, Array.Empty<string>()),
        ("SUBTRACT", 0x6D, new[] { "-" }),
        ("DECIMAL", 0x6E, Array.Empty<string>()),
        ("DIVIDE", 0x6F, Array.Empty<string>()),
        ("NUMLOCK", 0x90, Array.Empty<string>()),
        ("SCROLL", 0x91, Array.Empty<string>()),
        ("LSHIFT", 0xA0, new[] { "LEFT_SHIFT" }),
        ("RSHIFT", 0xA1, new[] { "RIGHT_SHIFT" }),
        ("LCTRL", 0xA2, new[] { "LCONTROL", "LEFT_CONTROL", "LEFT_CTRL" }),
        ("RCTRL", 0xA3, new[] { "RCONTROL", "RIGHT_CONTROL", "RIGHT_CTRL" }),
        ("LALT", 0xA4, new[] { "LMENU", "LEFT_MENU", "LEFT_ALT" }),
        ("RALT", 0xA5, new[] { "RMENU", "RIGHT_MENU", "RIGHT_ALT" }),
        ("BROWSER_BACK", 0xA6, Array.Empty<string>()),
        ("BROWSER_FORWARD", 0xA7, Array.Empty<string>()),
        ("BROWSER_REFRESH", 0xA8, Array.Empty<string>()),
        ("BROWSER_STOP", 0xA9, Array.Empty<string>()),
        ("BROWSER_SEARCH", 0xAA, Array.Empty<string>()),
        ("BROWSER_FAVORITES", 0xAB, Array.Empty<string>()),
        ("BROWSER_HOME", 0xAC, Array.Empty<string>()),
        ("VOLUME_MUTE", 0xAD, Array.Empty<string>()),
        ("VOLUME_DOWN", 0xAE, Array.Empty<string>()),
        ("VOLUME_UP", 0xAF, Array.Empty<string>()),
        ("MEDIA_NEXT_TRACK", 0xB0, Array.Empty<string>()),
        ("MEDIA_PREV_TRACK", 0xB1, Array.Empty<string>()),
        ("MEDIA_STOP", 0xB2, Array.Empty<string>()),
        ("MEDIA_PLAY_PAUSE", 0xB3, Array.Empty<string>()),
        ("LAUNCH_MAIL", 0xB4, Array.Empty<string>()),
        ("LAUNCH_MEDIA_SELECT", 0xB5, Array.Empty<string>()),
        ("LAUNCH_APP1", 0xB6, Array.Empty<string>()),
        ("LAUNCH_APP2", 0xB7, Array.Empty<string>()),
        ("SEMICOLON", 0xBA, new[] { "OEM_1", ";", ":", "COLON", "SEMI_COLON" }),
        ("EQUALS", 0xBB, new[] { "OEM_PLUS", "=", "PLUS" }),
        ("COMMA", 0xBC, new[] { "OEM_COMMA", ",", "<" }),
        ("MINUS", 0xBD, new[] { "OEM_MINUS", "UNDERSCORE", "_" }),
        ("PERIOD", 0xBE, new[] { "OEM_PERIOD", ".", ">" }),
        ("SLASH", 0xBF, new[] { "OEM_2", "/", "?", "FORWARD_SLASH", "QUESTION", "QUESTION_MARK" }),
        ("VK_OEM_3", 0xC0, new[] { "OEM_3", "`", "~", "TILDE", "GRAVE" }),
        ("VK_OEM_4", 0xDB, new[] { "OEM_4", "[", "{" }),
        ("BACKSLASH", 0xDC, new[] { "OEM_5", "\\", "|", "BACK_SLASH", "PIPE", "VERTICAL_BAR" }),
        ("VK_OEM_6", 0xDD, new[] { "OEM_6", "]", "}" }),
        ("QUOTE", 0xDE, new[] { "OEM_7", "'", "\"", "DOUBLE_QUOTE" }),
        ("VK_OEM_8", 0xDF, new[] { "OEM_8" }),
        ("VK_OEM_102", 0xE2, new[] { "OEM_102" }),
        ("PROCESSKEY", 0xE5, Array.Empty<string>()),
        ("ATTN", 0xF6, Array.Empty<string>()),
        ("CRSEL", 0xF7, Array.Empty<string>()),
        ("EXSEL", 0xF8, Array.Empty<string>()),
        ("EREOF", 0xF9, Array.Empty<string>()),
        ("PLAY", 0xFA, Array.Empty<string>()),
        ("ZOOM", 0xFB, Array.Empty<string>()),
        ("NONAME", 0xFC, Array.Empty<string>()),
        ("PA1", 0xFD, Array.Empty<string>()),
        ("OEM_CLEAR", 0xFE, Array.Empty<string>()),
    };

    /// <summary>Every accepted spelling to its canonical token, case-insensitively.</summary>
    private static readonly Dictionary<string, string> AliasToCanonical;

    /// <summary>Each named virtual-key code to its canonical token, so a hex spelling of a named key folds
    /// to the same token as its name.</summary>
    private static readonly Dictionary<int, string> VkToCanonical;

    static ModKeys()
    {
        AliasToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        VkToCanonical = new Dictionary<int, string>();
        void Add(string canonical, int vk, params string[] aliases)
        {
            AliasToCanonical.Add(canonical, canonical);
            foreach (var a in aliases) AliasToCanonical.Add(a, canonical);
            VkToCanonical.Add(vk, canonical);
        }
        foreach (var (canonical, vk, aliases) in NamedKeys) Add(canonical, vk, aliases);
        for (char c = 'A'; c <= 'Z'; c++) Add(c.ToString(), c);
        for (char c = '0'; c <= '9'; c++) Add(c.ToString(), c);
        for (int i = 0; i <= 9; i++) Add("NUMPAD" + i, 0x60 + i);
        for (int i = 1; i <= 24; i++) Add("F" + i, 0x6F + i);
        AliasToCanonical.Add(NoModifiers, NoModifiers);
        for (int i = 0; i <= 9; i++) DisplayNames.Add("NUMPAD" + i, "NUM " + i);
    }

    /// <summary>How a canonical token reads on screen: the keycap's own character where the parser's name
    /// for it is a code (<c>PERIOD</c> is <c>.</c>, <c>VK_OEM_4</c> is <c>[</c>), the NUM-prefixed form
    /// for the numpad keys, and the token itself everywhere it already reads as the key. Display only —
    /// what is stored and emitted is always the canonical token.</summary>
    private static readonly Dictionary<string, string> DisplayNames = new(StringComparer.Ordinal)
    {
        ["PRIOR"] = "PGUP",
        ["NEXT"] = "PGDN",
        ["SEMICOLON"] = ";",
        ["EQUALS"] = "=",
        ["COMMA"] = ",",
        ["MINUS"] = "-",
        ["PERIOD"] = ".",
        ["SLASH"] = "/",
        ["VK_OEM_3"] = "~",
        ["VK_OEM_4"] = "[",
        ["VK_OEM_6"] = "]",
        ["BACKSLASH"] = "\\",
        ["QUOTE"] = "'",
        ["MULTIPLY"] = "NUM *",
        ["ADD"] = "NUM +",
        ["SUBTRACT"] = "NUM -",
        ["DECIMAL"] = "NUM .",
        ["DIVIDE"] = "NUM /",
    };

    /// <summary>One spelled token to its canonical form, or null for one the game's parser would not take:
    /// a direct table name, the same with the optional <c>VK_</c> prefix, or a <c>0x</c> hex virtual-key
    /// code — which comes back as the code's NAME when it has one, and otherwise in the exact
    /// lower-case-<c>0x</c> shape the parser matches hex by.</summary>
    private static string? CanonicalToken(string token)
    {
        if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (token.Length > 2
                && int.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int vk)
                && vk is >= 0x01 and <= 0xFE)
                return VkToCanonical.TryGetValue(vk, out var named) ? named : "0x" + vk.ToString("x2");
            return null;
        }
        if (AliasToCanonical.TryGetValue(token, out var c)) return c;
        if (token.Length > 3 && token.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)
            && AliasToCanonical.TryGetValue(token[3..], out c)) return c;
        return null;
    }

    private static bool IsModifierToken(string canonical) => Array.IndexOf(ModifierOrder, canonical) >= 0;

    /// <summary>A typed/captured key folded to its one canonical spelling, or null when it is blank, holds
    /// a token the game's parser has no name for, or isn't shaped like a binding. Refusing rather than
    /// passing an odd string through keeps a bad key out of the emitted ini, where it would fail silently
    /// at load time.
    ///
    /// <para>Shape: zero or more distinct modifiers then ONE non-modifier key token, the modifiers folded
    /// into canonical order. A modifier alone is refused because the emitter binds every bare key
    /// <c>no_modifiers</c>, and "SHIFT while no shift is held" can never fire. So is a modifier named
    /// beside its own sided form (<c>CTRL LCTRL H</c>) or beside <c>NO_MODIFIERS</c>: both spell one
    /// binding two ways or contradict themselves. A lone <c>NO_MODIFIERS</c> prefix folds away — a bare
    /// key already means exactly that.</para>
    ///
    /// <para>Canonical tokens are also what keeps <see cref="VariableFor"/> injective: it folds the token
    /// separator to <c>_</c>, and no canonical token contains a modifier name as a <c>_</c>-joined prefix,
    /// so two different keys can never share one variable and collapse into a single
    /// <c>[Constants]</c>/<c>[Key]</c> declaration.</para></summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var raw = key.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var tokens = new string[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            if (CanonicalToken(raw[i]) is not { } c) return null;
            tokens[i] = c;
        }

        string last = tokens[^1];
        if (last == NoModifiers || IsModifierToken(last)) return null;

        var mods = new List<string>();
        bool spelledNoModifiers = false;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            if (tokens[i] == NoModifiers) { spelledNoModifiers = true; continue; }
            if (!IsModifierToken(tokens[i]) || mods.Contains(tokens[i])) return null;
            mods.Add(tokens[i]);
        }
        if (spelledNoModifiers && mods.Count > 0) return null;
        foreach (var generic in new[] { "CTRL", "ALT", "SHIFT" })
            if (mods.Contains(generic) && (mods.Contains("L" + generic) || mods.Contains("R" + generic)))
                return null;

        if (mods.Count == 0) return last;
        mods.Sort((a, b) => Array.IndexOf(ModifierOrder, a) - Array.IndexOf(ModifierOrder, b));
        return string.Join(' ', mods.Append(last));
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
    /// Two DIFFERENT keys never share one: <see cref="Normalize"/> folds every spelling of a key to one
    /// canonical form and refuses the shapes that would collide here.</summary>
    public static string VariableFor(string key)
    {
        var n = Normalize(key) ?? throw new ArgumentException($"'{key}' is not a usable toggle key", nameof(key));
        var sb = new StringBuilder("zz_key_");
        foreach (var c in n)
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        return sb.ToString();
    }

    /// <summary>The same normalization <see cref="Normalize(string?)"/> applies, carrying the state index
    /// through untouched: a gate's position in its cycle is not something spelling can change. Null for a
    /// reference naming no usable key — including the one a null key string converts to, since the implicit
    /// conversion has a struct to fill and cannot answer "no key" by itself.
    ///
    /// <para>Named apart from <see cref="Normalize(string?)"/> rather than overloading it: a key string is
    /// convertible to a <see cref="KeyRef"/>, so an overload pair would let a call site silently take the
    /// wrong one.</para></summary>
    public static KeyRef? NormalizeRef(KeyRef? key) =>
        // the null branch is cast rather than written bare: an untyped null in a KeyRef?-typed position can
        // take the string conversion above and come back as a present reference naming nothing
        key is { } k && Normalize(k.Key) is { } n ? new KeyRef(n, k.State) : (KeyRef?)null;

    /// <summary>How a key reads on screen, or <paramref name="empty"/> when there is none. The friendly
    /// form: keycap characters and NUM-prefixed numpad names stand in for the parser's codes, token by
    /// token, so <c>CTRL SHIFT PERIOD</c> reads <c>CTRL SHIFT .</c>. Never fed back into storage,
    /// comparison or emission — those take the canonical form <see cref="Normalize"/> answers.</summary>
    public static string Display(string? key, string empty = "")
    {
        if (Normalize(key) is not { } n) return empty;
        return string.Join(' ', n.Split(' ').Select(t => DisplayNames.GetValueOrDefault(t, t)));
    }

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
