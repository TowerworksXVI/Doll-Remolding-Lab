using System;
using System.Collections.Generic;
using System.IO;
using Remold.App.ViewModels;
using Remold.App.Views;
using Remold.Core;
using Remold.Core.Migoto;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Install and Launch are two buttons over ONE pair of Mods-folder disk reads, so a single raise has to
/// move both. Driven through the window's view-model rather than the pure gates: what this pins is a
/// RENDERED gate going stale, which the rules themselves can't show.
/// </summary>
[Collection("Dispatcher")]   // serialized with every other settings.json reader/writer — one file, one bin dir
public class ModsFolderGateTests : IDisposable
{
    // The view-model saves settings through the app's durable path, which under the test host is the test
    // binaries' own folder. Snapshot it so a run leaves it as it found it.
    private static readonly string SettingsPath = LabSettings.DefaultPath;
    private readonly byte[]? _settingsBefore = TryReadAll(SettingsPath);

    private static byte[]? TryReadAll(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
    }

    public void Dispose()
    {
        try
        {
            if (_settingsBefore is null) File.Delete(SettingsPath);
            else File.WriteAllBytes(SettingsPath, _settingsBefore);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A folder that passes the locator's accept-test: the standard descent plus the two GF2
    /// sentinel filenames. Existence only — nothing reads them.</summary>
    private static string FakeInstall(string parent)
    {
        var root = Path.Combine(parent, "GIRLS' FRONTLINE 2 EXILIUM");
        var abw = Path.Combine(root, "GF2_Exilium_Data", "LocalCache", "Data", "AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        File.WriteAllText(Path.Combine(abw, "catalog_main_24535.bin"), "x");
        File.WriteAllText(Path.Combine(abw, "08dfe7d89b6fe56375d6dfec87ffcc8a.bundle"), "x");
        return root;
    }

    /// <summary>The ini a working host carries beside its loader: the shader-regex section holding the
    /// texture hook a built mod fires through.</summary>
    private static void WriteHookedIni(string migotoDir) =>
        File.WriteAllText(Path.Combine(migotoDir, MigotoIni.FileName),
            "[ShaderRegexEnableTextureOverrides]\nshader_model = ps_5_0\nchecktextureoverride = ps-t0\n");

    [Fact]
    public void One_raise_moves_the_install_and_launch_gates_together()
    {
        using var g = new TempGame();
        var gameRoot = FakeInstall(g.Root);
        var migoto = Path.Combine(g.Root, "3dmigoto");
        var loader = Path.Combine(migoto, "Run.exe");

        var vm = new MainWindowViewModel(startLoad: false);
        vm.SetGameDir(gameRoot);                                     // Launch's game half is settled
        vm.ApplySettings(new SettingsResult { GamePath = gameRoot, MigotoLoaderExe = loader });
        vm.LastBuildDir = Path.Combine(g.Root, "built");             // Install's build half is settled

        // the loader is now the ONE thing both gates are waiting on, and both say the same sentence
        Assert.Equal(InstallGate.LoaderNotFound(loader), vm.InstallDisabledReason);
        Assert.Equal(InstallGate.LoaderNotFound(loader), vm.LaunchDisabledReason);
        Assert.False(vm.CanInstallBuild);
        Assert.False(vm.CanLaunchGame);

        // the loader (and the Mods folder beside it) appears, then ONE raise — here the Launch-side one,
        // from re-picking the install
        Directory.CreateDirectory(Path.Combine(migoto, "Mods"));
        File.WriteAllText(loader, "x");
        WriteHookedIni(migoto);

        var raised = new List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);
        vm.SetGameDir(gameRoot);

        Assert.Null(vm.InstallDisabledReason);
        Assert.Null(vm.LaunchDisabledReason);
        Assert.True(vm.CanInstallBuild);
        Assert.True(vm.CanLaunchGame);
        // …and both buttons were TOLD, or the one that wasn't keeps rendering the reason it just outgrew
        foreach (var p in new[]
                 {
                     nameof(vm.InstallDisabledReason), nameof(vm.CanInstallBuild), nameof(vm.InstallButtonTip),
                     nameof(vm.LaunchDisabledReason), nameof(vm.CanLaunchGame), nameof(vm.LaunchButtonTip),
                     nameof(vm.HasMigotoLoader), nameof(vm.NeedsMigotoLoader),
                 })
            Assert.Contains(p, raised);
    }

    /// <summary>Install renders only where there is somewhere to install TO. A loader nobody has pointed at
    /// is not a disabled button's problem — the pane shows the way to set the path instead, and takes it
    /// back the moment a usable loader lands.</summary>
    [Fact]
    public void With_no_usable_loader_the_pane_offers_the_path_instead_of_install()
    {
        using var g = new TempGame();
        var migoto = Path.Combine(g.Root, "3dmigoto");
        var loader = Path.Combine(migoto, "Run.exe");

        var vm = new MainWindowViewModel(startLoad: false);

        // nothing set at all: a step not taken, and the button says so
        Assert.False(vm.HasMigotoLoader);
        Assert.True(vm.NeedsMigotoLoader);
        Assert.Equal("Set 3DMigoto path", vm.SetMigotoPathLabel);
        Assert.Equal(InstallGate.SetLoader, vm.SetMigotoPathTip);

        // a path that isn't there, one with no Mods folder beside it, and one carrying no texture hook are
        // the same answer for the pane — and each says its own reason on the button that stands in
        vm.ApplySettings(new SettingsResult { MigotoLoaderExe = loader });
        Assert.True(vm.NeedsMigotoLoader);
        Assert.Equal("Fix 3DMigoto path", vm.SetMigotoPathLabel);
        Assert.Equal(InstallGate.LoaderNotFound(loader), vm.SetMigotoPathTip);

        Directory.CreateDirectory(migoto);
        File.WriteAllText(loader, "x");
        vm.ApplySettings(new SettingsResult { MigotoLoaderExe = loader });
        Assert.True(vm.NeedsMigotoLoader);
        Assert.Equal(InstallGate.NoModsFolder(loader), vm.SetMigotoPathTip);

        Directory.CreateDirectory(Path.Combine(migoto, "Mods"));
        vm.ApplySettings(new SettingsResult { MigotoLoaderExe = loader });
        Assert.True(vm.NeedsMigotoLoader);
        Assert.Equal(InstallGate.NoLoaderIni(loader), vm.SetMigotoPathTip);

        File.WriteAllText(Path.Combine(migoto, MigotoIni.FileName), "[Rendering]\noverride_cursor = 1\n");
        vm.ApplySettings(new SettingsResult { MigotoLoaderExe = loader });
        Assert.True(vm.NeedsMigotoLoader);
        Assert.Equal(InstallGate.NoTextureHook, vm.SetMigotoPathTip);

        WriteHookedIni(migoto);
        vm.ApplySettings(new SettingsResult { MigotoLoaderExe = loader });

        Assert.True(vm.HasMigotoLoader);
        Assert.False(vm.NeedsMigotoLoader);
        // Install is back and says the one thing left to do, rather than anything about the loader
        Assert.Equal(InstallGate.NoBuild, vm.InstallDisabledReason);
    }

    /// <summary>The Settings row answers for the same host the Build pane's gate does, at the grain the row
    /// can act on: the file, its ini, and the hook in that ini's tree. Blank stays allowed — the loader is
    /// optional to everything but Install and Launch.</summary>
    [Fact]
    public void The_settings_row_refuses_a_3DMigoto_a_built_mod_would_not_fire_on()
    {
        using var g = new TempGame();
        var migoto = Path.Combine(g.Root, "3dmigoto");
        var loader = Path.Combine(migoto, "Run.exe");
        Directory.CreateDirectory(migoto);

        Assert.Null(SettingsValidation.MigotoLoaderExe(""));
        Assert.Null(SettingsValidation.MigotoLoaderExe("   "));
        Assert.Equal(SettingsValidation.LoaderNotThere, SettingsValidation.MigotoLoaderExe(loader));

        File.WriteAllText(loader, "x");
        Assert.Equal(SettingsValidation.LoaderNoIni, SettingsValidation.MigotoLoaderExe(loader));

        File.WriteAllText(Path.Combine(migoto, MigotoIni.FileName),
            "[ShaderRegexEnableTextureOverrides]\n; checktextureoverride = ps-t0\n");
        Assert.Equal(SettingsValidation.LoaderNoHook, SettingsValidation.MigotoLoaderExe(loader));

        WriteHookedIni(migoto);
        Assert.Null(SettingsValidation.MigotoLoaderExe(loader));
    }
}
