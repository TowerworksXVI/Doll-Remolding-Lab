using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Project;

namespace Remold.App.ViewModels;

/// <summary>
/// Which of the change list's toggle keys are shared, and who each sharer moves with. One key is ONE
/// emitted variable, so everything on it switches together — a reasonable thing to author deliberately,
/// which is why this reports per control rather than as a warning about the list.
/// </summary>
public static class KeyCollisions
{
    /// <summary>How the tier-1 (whole-mod) key names itself.</summary>
    public const string WholeModLabel = "the whole mod";

    /// <summary>How one change names itself, in the change list's own "{character} · {outfit}" idiom, with the
    /// verb in the words the row itself shows (<see cref="BuildVerbWords"/>). The VERB and the OUTFIT are both
    /// part of the name: one part can carry a retexture and a hide at once, and two outfits of one character
    /// carry the same part names, so without them a tip can name the very row it sits on.</summary>
    public static string OwnerLabel(string partLabel, string character, string outfitLabel, string verb) =>
        $"{BuildVerbWords.Of(verb)} on {partLabel} ({character} · {outfitLabel})";

    /// <summary>"a", "a and b", "a, b and c" — a readable list inside one sentence.</summary>
    public static string NameList(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>What one key's sharers say when they disagree on how they start. The key is one emitted
    /// switch with one start, so the line states what ships rather than a rule to apply: the start box has
    /// to be set on every change on the key for it to hold.</summary>
    public const string StartStateNote = "Starts off is set on some, not all. They all start on.";

    /// <summary>The line each entry of <paramref name="bound"/> carries, index-aligned and empty where the
    /// key stands alone or there is no key. Keys are compared after <see cref="ModKeys.Normalize"/>, so
    /// "f6" and "F6" collide exactly as they would in game; the others are named in listed order.
    ///
    /// <para><c>StartsOff</c> is how that entry asks to start. Sharers of one key that disagree on it carry
    /// <see cref="StartStateNote"/> as well, since one variable can only take one start.</para></summary>
    public static IReadOnlyList<string> Tips(IReadOnlyList<(string Label, string? Key, bool StartsOff)> bound)
    {
        var tips = new string[bound.Count];
        Array.Fill(tips, "");
        var groups = Enumerable.Range(0, bound.Count)
            .Select(i => (Index: i, Key: ModKeys.Normalize(bound[i].Key)))
            .Where(x => x.Key is not null)
            .GroupBy(x => x.Key!, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var members = g.Select(x => x.Index).ToList();
            if (members.Count < 2) continue;
            bool disagree = members.Select(j => bound[j].StartsOff).Distinct().Count() > 1;
            foreach (var i in members)
            {
                var others = members.Where(j => j != i).Select(j => bound[j].Label).ToList();
                tips[i] = $"Same key as {NameList(others)}. They switch together."
                    + (disagree ? " " + StartStateNote : "");
            }
        }
        return tips;
    }
}
