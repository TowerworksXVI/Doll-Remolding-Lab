using System;
using System.Linq;

namespace Remold.Core.Migoto;

/// <summary>Game asset names a build refuses to touch, matched case-insensitively as substrings of a
/// subject, mesh or texture name. The listed character is a child NPC: don't remove entries, and keep
/// the match on the bare name so a later game release or a new route needs no code change. Separate
/// from <see cref="Bundles.RosterBlacklist"/> so one edit can't disable both.</summary>
public static class BuildBlacklist
{
    private static readonly string[] Entries = { "Helena" };

    public static bool IsBlocked(string? name) =>
        name is not null && Entries.Any(e => name.Contains(e, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A build touched a blacklisted asset. Its own type so degrade-and-continue catches can
/// decline to swallow it: a blocked asset always fails the build, never softens to a warning.</summary>
public sealed class BlockedAssetException : InvalidOperationException
{
    public BlockedAssetException(string message) : base(message) { }
}
