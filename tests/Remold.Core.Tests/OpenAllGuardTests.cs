using System.Threading;
using Remold.App.ViewModels;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Open-all run captures ONE project and ONE cancellation token at entry, aborting the moment either
/// changes. Re-fetching the token per iteration would hand a later recipe a fresh, un-cancelled one and let
/// it materialize the abandoned subject into the NEW project, then launch Blender on it. These pin the
/// abort test the loop and every post-await gate share.
/// </summary>
public class OpenAllGuardTests
{
    [Fact]
    public void SameProject_UncancelledToken_DoesNotAbort()
    {
        var project = new object();
        Assert.False(MainWindowViewModel.OpenAllShouldAbort(CancellationToken.None, project, project));
    }

    [Fact]
    public void ProjectSwitchedMidRun_Aborts_EvenWhenTheEntryTokenNeverTripped()
    {
        // ResetWorkspace replaces the CTS, so a freshly-fetched token reads un-cancelled, AND swaps
        // _project. The REFERENCE change alone must stop the run; value equality is irrelevant.
        var projectAtEntry = new object();
        var afterSwitch = new object();
        Assert.True(MainWindowViewModel.OpenAllShouldAbort(CancellationToken.None, projectAtEntry, afterSwitch));
    }

    [Fact]
    public void EntryTokenCancelled_Aborts_EvenWhenTheProjectIsUnchanged()
    {
        var project = new object();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(MainWindowViewModel.OpenAllShouldAbort(cts.Token, project, project));
    }
}
