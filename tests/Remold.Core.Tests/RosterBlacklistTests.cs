using Remold.Core.Bundles;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The silent blacklist predicate (<c>Bundles/RosterBlacklist</c>) — the child-NPC content policy.
/// Load-bearing: a failure here means an enforcement point was removed, and that removal is the bug.
/// The resolution surfaces have their own tests (<see cref="SubjectScopeTests"/>: a blacklisted stem
/// builds an EMPTY scope).
/// </summary>
public class RosterBlacklistTests
{
    [Theory]
    [InlineData("c_Helena_body_lod0", true)]
    [InlineData("C_HELENA_BODY", true)]      // case-insensitive
    [InlineData("Helena", true)]
    [InlineData("c_Helen_body", false)]      // a different character, not a prefix of this one
    [InlineData("c_Wren_body", false)]
    public void IsBlacklisted_DropsTheBlockedNameSilently(string name, bool expected) =>
        Assert.Equal(expected, RosterBlacklist.IsBlacklisted(name));
}
