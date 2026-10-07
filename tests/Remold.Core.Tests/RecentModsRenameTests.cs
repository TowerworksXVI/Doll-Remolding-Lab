using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Renaming a mod moves its project folder, and the Recent mods row moves with it: one row per mod,
/// under its current name, at its current folder. Rows are read only under this test's own library,
/// because the recent list is the test host's shared settings.
/// </summary>
public class RecentModsRenameTests
{
    [UiFact]
    public async Task Renaming_a_mod_moves_its_recent_row_with_its_folder()
    {
        using var lib = new TempGame();
        var vm = await OpenAsync(lib, "Before Rename");

        vm.PackageName = "After";
        Assert.Null(vm.TryAutoSaveProject());
        vm.PackageName = "After Rename";
        Assert.Null(vm.TryAutoSaveProject());

        string moved = RootOf(vm);
        Assert.Equal("after-rename", Path.GetFileName(moved));
        var row = Assert.Single(RowsUnder(vm, lib));
        Assert.Equal(moved, row.Path);
        Assert.Equal("After Rename", row.Name);
    }

    [UiFact]
    public async Task Save_mod_as_straight_after_a_rename_lists_the_original_at_its_new_folder_and_the_copy()
    {
        using var lib = new TempGame();
        var vm = await OpenAsync(lib, "Before Rename");
        string original = RootOf(vm);

        vm.PackageName = "After Rename";   // no autosave in between: Save mod as is what moves the folder
        string copyName = "Copy " + Guid.NewGuid().ToString("N");
        await vm.SaveModAs(copyName);

        // The copy lands in the configured library, outside this test's folder.
        string copy = RootOf(vm);
        try
        {
            Assert.NotEqual(original, copy);
            string renamedOriginal = Path.Combine(Path.GetDirectoryName(original)!, "after-rename");
            Assert.True(Directory.Exists(renamedOriginal));
            var originalRow = Assert.Single(RowsUnder(vm, lib));
            Assert.Equal(renamedOriginal, originalRow.Path);
            Assert.Equal("After Rename", originalRow.Name);
            var copyRow = Assert.Single(vm.RecentMods, r => string.Equals(r.Path, copy, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(copyName, copyRow.Name);
        }
        finally
        {
            if (!copy.StartsWith(lib.Root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(copy))
                Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>A copy that fails partway leaves no folder in the projects folder, and the original stays
    /// open.</summary>
    [UiFact]
    public async Task Save_mod_as_that_fails_partway_leaves_no_copy_behind()
    {
        using var lib = new TempGame();
        var vm = await OpenAsync(lib, "Held Source");
        string original = RootOf(vm);
        string copyName = "Copy " + Guid.NewGuid().ToString("N");
        string dest = Path.Combine(LabSettings.Load().ResolvedLibraryRoot, ModNaming.Slug(copyName));

        // a file nothing else may open stops the copy after its folder exists
        using (new FileStream(Path.Combine(original, "held.txt"), FileMode.Create, FileAccess.ReadWrite,
                   FileShare.None))
            await vm.SaveModAs(copyName);

        try
        {
            Assert.False(Directory.Exists(dest));
            Assert.Equal(original, RootOf(vm));
            Assert.StartsWith("Couldn't save a copy.", vm.EditPage.Status);
        }
        finally
        {
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }

    /// <summary>A rename that cannot move the folder keeps the mod saved where it is and says so in the
    /// notice cell, until a later save's move succeeds and the notice goes.</summary>
    [UiFact]
    public async Task A_mod_folder_that_cannot_be_renamed_says_so_until_it_can()
    {
        using var lib = new TempGame();
        var vm = await OpenAsync(lib, "Before Rename");
        string original = RootOf(vm);

        using (new FileStream(Path.Combine(original, "held.txt"), FileMode.Create, FileAccess.ReadWrite,
                   FileShare.None))
        {
            vm.PackageName = "After Rename";
            Assert.Null(vm.TryAutoSaveProject());

            Assert.Equal(original, RootOf(vm));
            Assert.Contains("Couldn't rename the mod folder to 'after-rename'", vm.NoticeStatus.Detail);
            Assert.Contains("The mod is saved in 'before-rename'.", vm.NoticeStatus.Detail);
        }

        Assert.Null(vm.TryAutoSaveProject());

        Assert.Equal("after-rename", Path.GetFileName(RootOf(vm)));
        Assert.DoesNotContain("Couldn't rename the mod folder", vm.NoticeStatus.Detail);
    }

    /// <summary>Naming the mod back to its folder's name ends what the notice was about, so it goes.</summary>
    [UiFact]
    public async Task A_failed_rename_notice_goes_when_the_name_goes_back()
    {
        using var lib = new TempGame();
        var vm = await OpenAsync(lib, "Before Rename");
        string original = RootOf(vm);

        using (new FileStream(Path.Combine(original, "held.txt"), FileMode.Create, FileAccess.ReadWrite,
                   FileShare.None))
        {
            vm.PackageName = "After Rename";
            Assert.Null(vm.TryAutoSaveProject());
            Assert.Contains("Couldn't rename the mod folder", vm.NoticeStatus.Detail);

            vm.PackageName = "Before Rename";
            Assert.Null(vm.TryAutoSaveProject());

            Assert.Equal(original, RootOf(vm));
            Assert.DoesNotContain("Couldn't rename the mod folder", vm.NoticeStatus.Detail);
        }
    }

    /// <summary>A new mod's first save writes the round-trip id the mod was given, so an editor opened
    /// straight after it is found again on the next run.</summary>
    [UiFact]
    public async Task A_new_mods_first_save_writes_its_round_trip_id()
    {
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());
        vm.NewMod();
        vm.PackageName = "First save " + Guid.NewGuid().ToString("N");
        await Task.Yield();
        vm.SaveModCommand.Execute(null);
        string root = RootOf(vm);
        try
        {
            string? id = vm.ProjectDocument.Session.RoundTripId;
            Assert.True(RoundTripStore.IsId(id));
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ModProject.ManifestPathFor(root)));
            Assert.Equal(id, saved.RootElement.GetProperty("round_trip_id").GetString());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<MainWindowViewModel> OpenAsync(TempGame lib, string name)
    {
        string root = lib.At(ModNaming.Slug(name));
        var project = AuthoredEditFixtures.SlotsOnly();
        project.Info.Name = name;
        AuthoredProjectSerializer.Save(project, ModProject.ManifestPathFor(root));
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());
        Assert.True(await vm.OpenModAsync(root));
        return vm;
    }

    private static string RootOf(MainWindowViewModel vm) => vm.ProjectDocument.Session.Snapshot().RootDir!;

    private static System.Collections.Generic.List<RecentModVm> RowsUnder(MainWindowViewModel vm, TempGame lib) =>
        vm.RecentMods.Where(r => r.Path.StartsWith(lib.Root, StringComparison.OrdinalIgnoreCase)).ToList();
}
