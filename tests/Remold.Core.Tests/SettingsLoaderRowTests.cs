using System;
using System.IO;
using Remold.App.ViewModels;
using Remold.App.Views;
using Remold.Core;
using Remold.Core.Migoto;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Settings "3DMigoto loader" row. Its reading of the host is ADVISORY: it names what is wrong with the
/// path on the row's own line, and the form saves anyway. The loader is optional, so a form held shut over
/// it would hold every unrelated edit with it — and a path a released build already persisted would leave
/// the dialog unsaveable for good. Install is where a host a built mod would not fire on is refused.
/// </summary>
public class SettingsLoaderRowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "remold-loaderrow-" + Guid.NewGuid().ToString("N"));

    public SettingsLoaderRowTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>A loader exe with an ini beside it carrying <paramref name="ini"/>, or no ini when null. The
    /// <c>Mods\</c> folder is there too, so the ini is the only thing any of these read differently.</summary>
    private string Loader(string? ini)
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Mods"));
        var exe = Path.Combine(dir, "Run.exe");
        File.WriteAllText(exe, "");
        if (ini is not null) File.WriteAllText(Path.Combine(dir, MigotoIni.FileName), ini);
        return exe;
    }

    private const string Hooked = """
        [ShaderRegexEnableTextureOverrides]
        run = CommandListSkin

        [CommandListSkin]
        checktextureoverride = ps-t0
        """;

    private const string Hookless = "[Rendering]\noverride_cursor = 1\n";

    // ---- what the row says ----

    [Fact]
    public void ABlankBox_SaysNothing()
    {
        Assert.Null(SettingsValidation.MigotoLoaderExe(""));
        Assert.Null(SettingsValidation.MigotoLoaderExe(null));
    }

    [Fact]
    public void AHostCarryingTheHook_HasNothingToReport()
    {
        Assert.Null(SettingsValidation.MigotoLoaderExe(Loader(Hooked)));
    }

    [Theory]
    [InlineData(null, "no ini beside the exe")]
    [InlineData(Hookless, "an ini with no hook in its tree")]
    public void AHostAMbuiltModWouldNotFireOn_IsNamedOnTheRow(string? ini, string _)
    {
        Assert.NotNull(SettingsValidation.MigotoLoaderExe(Loader(ini)));
    }

    [Fact]
    public void APathThatIsNotThere_IsNamedOnTheRow()
    {
        Assert.Equal(SettingsValidation.LoaderNotThere,
            SettingsValidation.MigotoLoaderExe(Path.Combine(_root, "gone", "Run.exe")));
    }

    // ---- and what it does to the Save ----

    /// <summary>The whole point: whatever the loader row read, the form still commits. Only the three rows
    /// naming a value the app acts on with no fallback can hold it.</summary>
    [Fact]
    public void TheLoaderRowsReading_NeverHoldsTheSave()
    {
        Assert.True(SettingsValidation.SaveCommits(gamePathOk: true, projectsFolderOk: true, cpuLimitOk: true));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void AnyOfTheThreeBlockingRows_HoldsTheSave(bool game, bool library, bool cpu)
    {
        Assert.False(SettingsValidation.SaveCommits(game, library, cpu));
    }

    /// <summary>End to end past the dialog: the settings a Save hands over are applied WHOLE — the hookless
    /// loader path and the edited author handle both land on disk — and the host's diagnosis is not lost, it
    /// moves to the status bar's 3DMigoto cell where it stands until the path is fixed.</summary>
    [Fact]
    public void ASaveCarryingAHooklessLoader_PersistsItAndTheOtherEditsBesideIt()
    {
        using var settings = new SettingsSnapshot();
        var exe = Loader(Hookless);
        var vm = new MainWindowViewModel(startLoad: false);

        vm.ApplySettings(new SettingsResult { MigotoLoaderExe = exe, Author = "towerworks" });

        var saved = LabSettings.Load();
        Assert.Equal(exe, saved.MigotoLoaderExe);
        Assert.Equal("towerworks", saved.Author);
        Assert.Equal("3DMigoto · no texture hook", vm.MigotoStatus.Text);
    }
}
