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

[Collection("Dispatcher")]
public class ModsFolderGateTests : IDisposable
{
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

    private static string FakeInstall(string parent)
    {
        var root = Path.Combine(parent, "GIRLS' FRONTLINE 2 EXILIUM");
        var bundles = Path.Combine(root, "GF2_Exilium_Data", "LocalCache", "Data", "AssetBundles_Windows");
        Directory.CreateDirectory(bundles);
        File.WriteAllText(Path.Combine(bundles, "catalog_main_24535.bin"), "x");
        File.WriteAllText(Path.Combine(bundles, "08dfe7d89b6fe56375d6dfec87ffcc8a.bundle"), "x");
        return root;
    }

    private static void WriteHookedIni(string migotoDir) =>
        File.WriteAllText(Path.Combine(migotoDir, MigotoIni.FileName),
            "[ShaderRegexEnableTextureOverrides]\nshader_model = ps_5_0\nchecktextureoverride = ps-t0\n");

    [Fact]
    public void A_loader_path_change_reraises_launch_and_the_status_cell()
    {
        using var game = new TempGame();
        string gameRoot = FakeInstall(game.Root);
        string migoto = Path.Combine(game.Root, "3dmigoto");
        string loader = Path.Combine(migoto, "Run.exe");
        var vm = new MainWindowViewModel(startLoad: false);

        vm.SetGameDir(gameRoot);
        vm.ApplySettings(new SettingsResult { GamePath = gameRoot, MigotoLoaderExe = loader });
        Assert.Equal(LoaderGate.LoaderNotFound(loader), vm.LaunchDisabledReason);
        Assert.False(vm.CanLaunchGame);

        Directory.CreateDirectory(migoto);
        File.WriteAllText(loader, "x");
        WriteHookedIni(migoto);
        var raised = new List<string>();
        vm.PropertyChanged += (_, args) => raised.Add(args.PropertyName!);

        vm.SetGameDir(gameRoot);

        Assert.Null(vm.LaunchDisabledReason);
        Assert.True(vm.CanLaunchGame);
        Assert.Contains(nameof(vm.LaunchDisabledReason), raised);
        Assert.Contains(nameof(vm.CanLaunchGame), raised);
        Assert.Contains(nameof(vm.LaunchButtonTip), raised);
        Assert.Contains(nameof(vm.MigotoStatus), raised);
    }

    [Fact]
    public void The_settings_row_refuses_a_loader_that_cannot_run_texture_mods()
    {
        using var game = new TempGame();
        string migoto = Path.Combine(game.Root, "3dmigoto");
        string loader = Path.Combine(migoto, "Run.exe");
        Directory.CreateDirectory(migoto);

        Assert.Null(SettingsValidation.MigotoLoaderExe(""));
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
