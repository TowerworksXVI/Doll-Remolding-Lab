using System;
using System.Linq;
using Remold.Core.Project;

namespace Remold.Core.Migoto;

/// <summary>Game asset names a build refuses to touch, matched case-insensitively as substrings of a
/// subject, mesh or texture name. The listed characters are child NPCs: don't remove entries, and keep
/// the match on the bare name so a later game release or a new route needs no code change. Separate from
/// <see cref="Bundles.RosterBlacklist"/> so one edit can't disable both.</summary>
public static class BuildBlacklist
{
    private static readonly string[] Entries = { "Helena", "Melanie" };

    public static bool IsBlocked(string? name) =>
        name is not null && Entries.Any(e => name.Contains(e, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A build touched a blacklisted asset. Its own type so degrade-and-continue catches can
/// decline to swallow it: a blocked asset always fails the build, never softens to a warning. It is an
/// <see cref="AuthoredRefusalException"/> because its sentence names the asset the modder picked, so the
/// surface that reports a failed build shows it as it is.</summary>
public sealed class BlockedAssetException : AuthoredRefusalException
{
    public BlockedAssetException(string message) : base(message) { }
}
