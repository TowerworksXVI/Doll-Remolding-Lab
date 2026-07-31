using System.IO;
using Remold.App.ViewModels;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The save-first close/leave flow: with unsaved changes the app SAVES, minting the folder on first save,
/// rather than prompt to discard; only a FAILED save falls back to a prompt. Pins both halves — the disk
/// save-or-mint primitive the flush funnels through, and the pure decision seams, testable without
/// standing up the view-model.
/// </summary>
public class CloseFlowSaveTests
{
    private static ModProject Sample() => new()
    {
        AppVersion = "0.1-test",
        Info = new ProjectInfo { Name = "Karst Jacket", Version = "1.0" },
        Selection = { new SelectionEntry { Character = "Karst", Outfit = "KarstSSR01" } },
    };

    // ---- the disk save-or-mint primitive the close flush funnels through -------------------------------

    [Fact]
    public void SaveOrMint_RootDirSet_WritesManifestToThatFolder()
    {
        // dirty + already materialized → the close flush just writes into the existing folder.
        using var lib = new TempGame();
        var existing = Path.Combine(lib.Root, "karst-jacket");
        Directory.CreateDirectory(existing);
        var proj = Sample();
        proj.RootDir = existing;

        var written = proj.SaveOrMint(lib.Root, ModNaming.Slug("Karst Jacket"));

        Assert.Equal(existing, written);
        Assert.True(File.Exists(Path.Combine(existing, ModProject.FileName)));   // manifest hit disk
    }

    [Fact]
    public void SaveOrMint_RootDirNull_MintsFolderUnderLibraryAndSaves()
    {
        // dirty + never-yet-saved (a Pick-only session) → the flush mints a folder, then the manifest exists.
        using var lib = new TempGame();
        var proj = Sample();
        Assert.Null(proj.RootDir);

        var minted = proj.SaveOrMint(lib.Root, ModNaming.Slug("Karst Jacket"));

        Assert.Equal(minted, proj.RootDir);
        Assert.StartsWith(lib.Root, minted);
        Assert.True(File.Exists(Path.Combine(minted, ModProject.FileName)));   // mod.drlproj under the minted folder
    }

    [Fact]
    public void SaveOrMint_UnnamedProject_MintsTheAutoNameSlug()
    {
        // an unnamed mod mints the "untitled mod" slug the machinery produces (a fallback slug is acceptable).
        using var lib = new TempGame();
        var proj = new ModProject { Info = new ProjectInfo { Name = "untitled mod" } };

        var minted = proj.SaveOrMint(lib.Root, ModNaming.Slug("untitled mod"));

        Assert.True(ModProject.FolderMatchesSlug(Path.GetFileName(minted), "untitled-mod"));
        Assert.True(File.Exists(Path.Combine(minted, ModProject.FileName)));
    }

    [Fact]
    public void SaveOrMint_UnwritableTarget_Throws()
    {
        // An unwritable library root (a FILE where the folder should go) fails the mint's write — the input
        // TrySaveProject maps to (false, reason), driving the prompt branch below.
        using var g = new TempGame();
        var fileAsRoot = Path.Combine(g.Root, "not-a-dir");
        File.WriteAllText(fileAsRoot, "x");   // a file — creating a subfolder under it throws
        var proj = Sample();

        Assert.ThrowsAny<IOException>(() => proj.SaveOrMint(fileAsRoot, "karst-jacket"));
    }

    // ---- the pure close/leave decision table -----------------------------------------------------------

    [Theory]
    [InlineData(false, false, false)]   // not dirty → no save needed → proceed silently
    [InlineData(false, true, false)]    // not dirty, on home → no save needed
    [InlineData(true, true, false)]     // dirty but on home (no live project) → no save needed
    [InlineData(true, false, true)]     // dirty + a live project → a save is needed before leaving
    public void LeaveNeedsSave_OnlyWhenDirtyAndAProjectIsOpen(bool isDirty, bool showHome, bool expected)
        => Assert.Equal(expected, MainWindowViewModel.LeaveNeedsSave(isDirty, showHome));

    [Fact]
    public void LeaveProceedsWithoutPrompt_NoSaveNeeded_ProceedsSilently()
        => Assert.True(MainWindowViewModel.LeaveProceedsWithoutPrompt(saveNeeded: false, saveOk: false));

    [Fact]
    public void LeaveProceedsWithoutPrompt_SaveSucceeded_ProceedsSilently()
        => Assert.True(MainWindowViewModel.LeaveProceedsWithoutPrompt(saveNeeded: true, saveOk: true));

    [Fact]
    public void LeaveProceedsWithoutPrompt_SaveFailed_FallsBackToPrompt()
        => Assert.False(MainWindowViewModel.LeaveProceedsWithoutPrompt(saveNeeded: true, saveOk: false));

    // ---- when a save may rename the project folder -----------------------------------------------------

    [Theory]
    [InlineData(false, false, false, true)]    // idle: the rename may land
    [InlineData(true, false, false, false)]    // a materialize captured the old folder and is still writing into it
    [InlineData(false, true, false, false)]    // a build reads the persisted workspace for its whole run
    [InlineData(false, false, true, false)]    // a send-back apply rewrites glbs it resolved before it left the UI thread
    [InlineData(true, true, true, false)]
    public void TheProjectFolderIsNeverRenamedUnderWorkThatIsReadingIt(
        bool materializing, bool building, bool applyingSend, bool expected)
        => Assert.Equal(expected,
            MainWindowViewModel.CanRenameProjectFolder(materializing, building, applyingSend));
}
