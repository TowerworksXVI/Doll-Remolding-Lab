using System;
using System.Linq;

namespace Remold.Core.Bundles;

/// <summary>Asset names the app pretends do not exist, matched case-insensitively as substrings.
/// Enforced at the subject-resolution surface (<see cref="Workbench.SubjectScope"/> builds an empty
/// scope, <see cref="GameVfs.PrefabsFor"/> resolves nothing) so the roster and every lookup stay clean,
/// silently and with no user-visible trace. The listed characters are child NPCs: don't remove entries,
/// don't make the refusal visible, and keep the match on the bare name so a later game release or a new
/// route needs no code change. Separate from <see cref="Migoto.BuildBlacklist"/> so one edit can't disable
/// both.</summary>
public static class RosterBlacklist
{
    private static readonly string[] Entries = { "Helena", "Melanie" };

    public static bool IsBlacklisted(string name) =>
        Entries.Any(e => name.Contains(e, StringComparison.OrdinalIgnoreCase));
}
