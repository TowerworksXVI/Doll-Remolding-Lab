using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Remold.Core.Model;

/// <summary>
/// Which parts one timeline show/hide entry reaches. The timeline lists are authored as plain strings and
/// the game resolves each entry two ways, so this mirrors both — an entry matching under EITHER is one the
/// game can act on:
///
/// <list type="number">
/// <item>By exact node name against the model's direct children, skipping any entry carrying a
/// <c>_P&lt;n&gt;_</c> modular seam (those never reach the direct-child lookup).</item>
/// <item>Through the modular selector, whose worn containers are keyed by RESOURCE TOKEN: an entry ending
/// <c>_&lt;token&gt;_Dorm</c> or <c>_&lt;token&gt;_Fight</c> reaches that token's context container, and one
/// ending in the bare <c>_&lt;token&gt;</c> reaches its base container.</item>
/// </list>
///
/// <para>An entry that carries a LOD token inside a modular name matches NEITHER — the seam rules it out
/// of the first and the trailing LOD token keeps it off the end of any resource token in the second. Such
/// entries are inert in the game too, and they fall out of these two rules rather than being special
/// cased.</para>
///
/// <para>The token rule is gated on the outfit's own resource tokens. Without them (a non-modular outfit,
/// or no wardrobe scheme available) only the exact rule can fire, which is the conservative direction: an
/// unmatched entry demotes nothing.</para>
///
/// <para>Two boundaries keep the token rule from over-reaching. It is gated on the STEM the entry names,
/// because the wardrobe-change clips are addressed by character rather than by outfit and every stem of a
/// character therefore reads every other's entries. And a match is rejected when a LONGER resource token
/// of the same outfit ends the same entry tail, since a shorter token can be an underscore-delimited
/// suffix of a longer one and the game keys the container by the longest.</para>
/// </summary>
public static partial class ShoeNodeMatch
{
    /// <summary>The modular seam a slot name carries when it belongs to a swappable wardrobe piece. Same
    /// shape the sharing measurement reads names by, so the two agree on what is modular.</summary>
    [GeneratedRegex(@"(^|_)[Pp]\d+_", RegexOptions.Compiled)]
    private static partial Regex ModularSeam();

    /// <summary>Whether a node name carries the modular <c>_P&lt;n&gt;_</c> seam.</summary>
    public static bool CarriesModularSeam(string name) => ModularSeam().IsMatch(name);

    /// <summary>The model stem an entry names, or null when it carries no recognizable stem prefix.
    /// Entries are authored <c>c_&lt;stem&gt;_slg_&lt;tail&gt;</c> or <c>c_&lt;stem&gt;_&lt;tail&gt;</c>, and
    /// a model stem carries no underscore of its own, so the segment between the leading <c>c_</c> and the
    /// next <c>_</c> is the stem.</summary>
    public static string? EntryStem(string entry)
    {
        if (entry.Length < 3 || !entry.StartsWith("c_", StringComparison.OrdinalIgnoreCase)) return null;
        int end = entry.IndexOf('_', 2);
        return end > 2 ? entry[2..end] : null;
    }

    /// <summary>Whether <paramref name="entry"/> reaches a part drawn by <paramref name="slotName"/> and
    /// tokened <paramref name="partToken"/>. <paramref name="resourceTokens"/> is the outfit's modular
    /// resource-token set; null or empty leaves only the exact-name rule. <paramref name="stem"/> is the
    /// model stem being resolved, which gates the token rule; null means the caller doesn't know it, and
    /// leaves the token rule ungated (the conservative direction — it demotes more, never less).</summary>
    public static bool Matches(string entry, string slotName, string partToken,
        IReadOnlySet<string>? resourceTokens, string? stem)
    {
        if (string.IsNullOrEmpty(entry)) return false;

        if (!CarriesModularSeam(entry)
            && string.Equals(entry, slotName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (resourceTokens is null || resourceTokens.Count == 0) return false;
        // The wardrobe-change clips are addressed by the CHARACTER token, so every stem of one character
        // resolves the same bundles and reads the other stems' entries too. The exact rule above is
        // self-gating — a slot name carries its own stem — but an entry ending in a BARE modular token
        // would otherwise reach whichever outfit happens to carry a token of that name. An entry with no
        // recognizable stem prefix keeps matching, which is the conservative direction.
        if (stem is not null && EntryStem(entry) is { } named
            && !string.Equals(named, stem, StringComparison.OrdinalIgnoreCase))
            return false;

        // The container is keyed by the token WITHOUT its context tail: one container holds a token's
        // Dorm and Fight twins, and the entry's own tail is what picks between them.
        var (baseToken, _) = MeshName.SplitVariant(partToken);
        if (baseToken.Length == 0 || !resourceTokens.Contains(baseToken)) return false;
        if (!TailReaches(entry, baseToken)) return false;

        // Longest token wins. A shorter token can be an underscore-delimited SUFFIX of a longer one
        // (`Coat` inside `Top_Coat`), so an entry aimed at the longer token ends the shorter one's tail
        // too. The container the game keys is the one named by the longest token the entry actually ends
        // with, so a match the outfit's own longer token also explains belongs to that token, not here.
        foreach (var other in resourceTokens)
            if (other.Length > baseToken.Length && TailReaches(entry, other)) return false;
        return true;
    }

    /// <summary>Whether ANY entry of <paramref name="entries"/> reaches the part.</summary>
    public static bool MatchesAny(IEnumerable<string> entries, string slotName, string partToken,
        IReadOnlySet<string>? resourceTokens, string? stem)
    {
        foreach (var e in entries)
            if (Matches(e, slotName, partToken, resourceTokens, stem)) return true;
        return false;
    }

    /// <summary>Whether the entry ends at <paramref name="token"/>'s container — bare, or with either
    /// context tail the selector keys the token's twins by.</summary>
    private static bool TailReaches(string entry, string token) =>
        EndsWith(entry, "_" + token)
        || EndsWith(entry, "_" + token + "_Dorm")
        || EndsWith(entry, "_" + token + "_Fight");

    private static bool EndsWith(string s, string tail) =>
        s.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
}
