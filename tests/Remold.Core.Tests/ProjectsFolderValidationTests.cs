using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Remold.App.Views;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Settings "Projects folder" rule. The value is read again only at the next New Mod or rename, so a
/// folder that can't take a mod has to be caught while the form is still open — which means proving the
/// write, not just the path's shape. Proving it can create folders, so the check reports what it made and a
/// save that never commits takes them back.
/// </summary>
public class ProjectsFolderValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-libroot-" + Guid.NewGuid().ToString("N"));

    public ProjectsFolderValidationTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        ClearDenials();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankBox_Passes_AndFallsBackToTheDefaultLibrary(string? typed)
    {
        var check = SettingsValidation.ProjectsFolder(typed);

        Assert.True(check.Ok);
        Assert.Null(check.Reason);
        Assert.Empty(check.Created);
    }

    [Fact]
    public void AnExistingWritableFolder_Passes_AndLeavesNoProbeBehind()
    {
        var check = SettingsValidation.ProjectsFolder(_root);

        Assert.True(check.Ok);
        Assert.Null(check.Reason);
        Assert.Empty(check.Created);            // it was already there
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void AFolderThatIsNotThereYet_IsCreated()
    {
        var fresh = Path.Combine(_root, "nested", "library");

        var check = SettingsValidation.ProjectsFolder(fresh);

        Assert.True(check.Ok);
        Assert.True(Directory.Exists(fresh));
        Assert.Empty(Directory.GetFileSystemEntries(fresh));
        // both levels are the check's own work, deepest first — what a save that never commits takes back
        Assert.Equal(new[] { fresh, Path.Combine(_root, "nested") }, check.Created);
    }

    /// <summary>Proving the path costs a folder, and a save that never commits owes it back — the box can be
    /// refused by another row, or the form dropped, after this one passed.</summary>
    [Fact]
    public void ASaveThatDoesNotCommit_TakesBackWhatTheCheckCreated()
    {
        var fresh = Path.Combine(_root, "nested", "library");
        var check = SettingsValidation.ProjectsFolder(fresh);

        SettingsValidation.RemoveCreatedFolders(check.Created);

        Assert.False(Directory.Exists(fresh));
        Assert.False(Directory.Exists(Path.Combine(_root, "nested")));
        Assert.True(Directory.Exists(_root));   // a folder that was already there is never touched
    }

    /// <summary>Only an EMPTY folder goes back: anything that arrived in one meanwhile is the modder's, and a
    /// folder that stays keeps its parents.</summary>
    [Fact]
    public void AFolderSomethingLandedIn_StaysWithItsParents()
    {
        var fresh = Path.Combine(_root, "nested", "library");
        var check = SettingsValidation.ProjectsFolder(fresh);
        File.WriteAllText(Path.Combine(fresh, "a-mod.json"), "{}");

        SettingsValidation.RemoveCreatedFolders(check.Created);

        Assert.True(Directory.Exists(fresh));
        Assert.True(Directory.Exists(Path.Combine(_root, "nested")));
    }

    [Fact]
    public void ADriveThatIsNotThere_RefusesTheSave_WithAReason()
    {
        var drive = FreeDriveLetter();

        var check = SettingsValidation.ProjectsFolder($@"{drive}:\mods");

        Assert.False(check.Ok);
        Assert.Equal("Not a writable folder. Select one the app can create files in.", check.Reason);
    }

    /// <summary>A path with a character no file system takes: the shape fails before any write is
    /// attempted, and it refuses on the same line rather than escaping as an exception.</summary>
    [Fact]
    public void AMalformedPath_RefusesTheSave_WithAReason()
    {
        var check = SettingsValidation.ProjectsFolder(Path.Combine(_root, "bad|name"));

        Assert.False(check.Ok);
        Assert.NotNull(check.Reason);
    }

    /// <summary>A file standing where the folder should be: creating the directory throws, so the row
    /// refuses instead of the next New Mod doing it.</summary>
    [Fact]
    public void APathThatIsAFile_RefusesTheSave()
    {
        var file = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(file, "");

        var check = SettingsValidation.ProjectsFolder(file);

        Assert.False(check.Ok);
        Assert.NotNull(check.Reason);
    }

    /// <summary>The state the probe exists for: a folder that takes a create and refuses a file. Nothing
    /// about the path says so — only writing to it does — and the next New Mod is where it would otherwise
    /// surface.</summary>
    [Fact]
    public void AFolderThatTakesTheCreateAndRefusesTheWrite_RefusesTheSave()
    {
        if (!OperatingSystem.IsWindows()) return;   // the fixture is an NTFS deny entry
        var denied = Path.Combine(_root, "no-files");
        Directory.CreateDirectory(denied);
        DenyFileCreation(denied);

        var check = SettingsValidation.ProjectsFolder(denied);

        Assert.False(check.Ok);
        Assert.Equal("Not a writable folder. Select one the app can create files in.", check.Reason);
    }

    /// <summary>The same folder, one level down: the create succeeds — the deny covers files, not
    /// subfolders — and the probe is what refuses. The folder the check made goes back with it, so a refused
    /// save leaves the disk as it found it.</summary>
    [Fact]
    public void AProbeThatFails_TakesBackTheFolderItCreated()
    {
        if (!OperatingSystem.IsWindows()) return;   // the fixture is an NTFS deny entry
        var denied = Path.Combine(_root, "no-files-here");
        Directory.CreateDirectory(denied);
        DenyFileCreation(denied);
        var under = Path.Combine(denied, "library");

        var check = SettingsValidation.ProjectsFolder(under);

        Assert.False(check.Ok);
        Assert.Empty(check.Created);
        Assert.False(Directory.Exists(under));
    }

    // ---- and the same rule in the mode the open form reads it in ----
    // The form answers for this row on every pause in the typing, so that reading owes the disk nothing: a
    // glance at Settings must not leave a folder tree behind on a path the modder was halfway through typing.

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankBox_PassesInEitherMode(string? typed)
    {
        var check = SettingsValidation.ProjectsFolder(typed, mutate: false);

        Assert.True(check.Ok);
        Assert.Empty(check.Created);
    }

    [Fact]
    public void AnExistingWritableFolder_ReadsTheSameWayWithoutTheCreate()
    {
        var check = SettingsValidation.ProjectsFolder(_root, mutate: false);

        Assert.True(check.Ok);
        Assert.Equal(SettingsValidation.WritableFolder, check.Reason);
        Assert.Empty(check.Created);
        Assert.Empty(Directory.GetFileSystemEntries(_root));   // the probe is gone with it
    }

    /// <summary>The difference between the two modes: Save creates the folder to prove it, the open form
    /// says what Save will do and touches nothing.</summary>
    [Fact]
    public void AFolderThatIsNotThereYet_PassesWithoutBeingCreated()
    {
        var fresh = Path.Combine(_root, "nested", "library");

        var check = SettingsValidation.ProjectsFolder(fresh, mutate: false);

        Assert.True(check.Ok);
        Assert.Equal(SettingsValidation.WillBeCreated, check.Reason);
        Assert.Empty(check.Created);
        Assert.False(Directory.Exists(fresh));
        Assert.False(Directory.Exists(Path.Combine(_root, "nested")));
    }

    /// <summary>A create needs somewhere to land. A drive that isn't there takes no folder, so the row refuses
    /// it while the modder is looking at it rather than promising a create that can't happen.</summary>
    [Fact]
    public void ADriveThatIsNotThere_IsRefusedInBothModes()
    {
        var path = $@"{FreeDriveLetter()}:\mods";

        var check = SettingsValidation.ProjectsFolder(path, mutate: false);

        Assert.False(check.Ok);
        Assert.Equal(SettingsValidation.NotWritableFolder, check.Reason);
    }

    /// <summary>A file standing where the folder would go: no create lands on it, so it refuses rather than
    /// reading as a folder Save will make.</summary>
    [Fact]
    public void APathThatIsAFile_IsRefusedWithoutTheCreate()
    {
        var file = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(file, "");

        var check = SettingsValidation.ProjectsFolder(file, mutate: false);

        Assert.False(check.Ok);
        Assert.Equal(SettingsValidation.NotWritableFolder, check.Reason);
    }

    /// <summary>A file with a folder path hung off it: the create fails somewhere above the leaf, and the walk
    /// up finds the file before it finds anywhere to land.</summary>
    [Fact]
    public void APathUnderAFile_IsRefusedWithoutTheCreate()
    {
        var file = Path.Combine(_root, "a-file");
        File.WriteAllText(file, "");

        var check = SettingsValidation.ProjectsFolder(Path.Combine(file, "library"), mutate: false);

        Assert.False(check.Ok);
        Assert.Equal(SettingsValidation.NotWritableFolder, check.Reason);
    }

    [Fact]
    public void AMalformedPath_IsRefusedWithoutTheCreate()
    {
        var check = SettingsValidation.ProjectsFolder(Path.Combine(_root, "bad|name"), mutate: false);

        Assert.False(check.Ok);
        Assert.NotNull(check.Reason);
    }

    /// <summary>A folder that exists and refuses a write is caught in this mode too — that one the probe can
    /// still prove without creating anything.</summary>
    [Fact]
    public void AnExistingFolderThatRefusesTheWrite_IsRefusedWithoutTheCreate()
    {
        if (!OperatingSystem.IsWindows()) return;   // the fixture is an NTFS deny entry
        var denied = Path.Combine(_root, "no-files-live");
        Directory.CreateDirectory(denied);
        DenyFileCreation(denied);

        var check = SettingsValidation.ProjectsFolder(denied, mutate: false);

        Assert.False(check.Ok);
        Assert.Equal(SettingsValidation.NotWritableFolder, check.Reason);
    }

    /// <summary>Deny THIS account the creation of files in <paramref name="dir"/> and everything under it,
    /// leaving subfolder creation alone. A deny entry beats every allow the account has, so an elevated run
    /// is refused the same way.</summary>
    private static void DenyFileCreation(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.WriteData,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
            PropagationFlags.None, AccessControlType.Deny));
        info.SetAccessControl(acl);
    }

    /// <summary>Give the denied folders back before the fixture's own cleanup runs into them.</summary>
    private void ClearDenials()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var dir in Directory.Exists(_root)
                     ? Directory.GetDirectories(_root, "*", SearchOption.AllDirectories)
                     : Array.Empty<string>())
            try
            {
                var info = new DirectoryInfo(dir);
                var acl = info.GetAccessControl();
                acl.RemoveAccessRuleAll(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                    FileSystemRights.CreateFiles | FileSystemRights.WriteData, AccessControlType.Deny));
                info.SetAccessControl(acl);
            }
            catch { /* a folder with no denial on it needs none removed */ }
    }

    private static char FreeDriveLetter()
    {
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'D'; c--)
            if (!taken.Contains(c)) return c;
        throw new InvalidOperationException("every drive letter is in use");
    }
}
