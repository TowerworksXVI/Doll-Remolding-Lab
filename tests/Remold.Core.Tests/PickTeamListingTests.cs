using System.Collections.Generic;
using Remold.App.ViewModels;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The Support Teams tab's listing rule: a team lists once a MEMBER confirmed, and a team whose
/// only confirmed subject is its weapon lists nothing.</summary>
public class PickTeamListingTests
{
    private static IReadOnlyList<string> Listed(params string[] confirmedStems) =>
        MainWindowViewModel.ListedTeamSubjects(confirmedStems, stem => stem);

    [Fact]
    public void AMemberConfirmed_ListsEverythingConfirmed()
    {
        Assert.Equal(new[] { "TEAM01_MemberB", "TEAM01_WL" }, Listed("TEAM01_MemberB", "TEAM01_WL"));
        Assert.Equal(new[] { "TEAM01_MemberA" }, Listed("TEAM01_MemberA"));
    }

    [Fact]
    public void WeaponOnly_ListsNothing()
    {
        Assert.Empty(Listed("TEAM01_WL"));
        Assert.Empty(Listed());
    }
}
