using System;
using System.Linq;

namespace Remold.Core.Export;

/// <summary>Subjects a rigged export refuses to touch, matched case-insensitively as substrings of the
/// character name or the outfit stem. The subject-resolution surface already resolves these to nothing
/// (<see cref="Bundles.RosterBlacklist"/>), but the export entry point takes a character and outfit
/// directly and so can be reached without it; both answer the same way — an empty result, silently and
/// with no user-visible trace. The listed characters are child NPCs: don't remove entries, don't make the
/// refusal visible, and keep the match on the bare name so a later game release or a new route needs no
/// code change. Separate from <see cref="Bundles.RosterBlacklist"/> and
/// <see cref="Migoto.BuildBlacklist"/> so one edit can't disable the others.</summary>
public static class ExportBlacklist
{
    private static readonly string[] Entries = { "Helena", "Melanie" };

    public static bool IsBlocked(string? name) =>
        name is not null && Entries.Any(e => name.Contains(e, StringComparison.OrdinalIgnoreCase));
}
