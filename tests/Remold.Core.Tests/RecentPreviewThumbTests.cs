using System;
using System.IO;
using Remold.App.ViewModels;
using Remold.Core;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The home screen's recent rows resolve a mod's preview from its MANIFEST alone — the screen reports on
/// mods it has not opened, so nothing it reads may build workspace state or raise an error surface. Every
/// way that read can go wrong resolves to "no preview", which the row renders as an empty slot.
///
/// <para>The decode itself needs an Avalonia runtime the test host has none of, so the bitmap and the row's
/// layout go to a smoke test; what is pinned here is the resolution and its silence.</para>
/// </summary>
public class RecentPreviewThumbTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "drl-recent-" + Guid.NewGuid().ToString("N"));

    public RecentPreviewThumbTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Project(string name, string? preview, bool writeFile)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var project = new ModProject { Info = { Name = name, Preview = preview } };
        project.Save(dir);
        if (preview is not null && writeFile) File.WriteAllBytes(Path.Combine(dir, preview), new byte[] { 1, 2, 3 });
        return dir;
    }

    private string SessionProject(string name, string preview)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var project = new AuthoredProject { Info = { Name = name, Preview = preview } };
        AuthoredProjectSerializer.Save(project, dir);
        File.WriteAllBytes(Path.Combine(dir, preview), new byte[] { 1, 2, 3 });
        return dir;
    }

    [Fact]
    public void A_project_with_a_preview_on_disk_resolves_to_that_file()
    {
        var dir = Project("with", "preview.png", writeFile: true);

        var path = MainWindowViewModel.RecentPreviewPath(dir);

        Assert.Equal(Path.Combine(dir, "preview.png"), path);
    }

    [Fact]
    public void A_schema_2_session_preview_is_read_without_the_schema_1_adapter()
    {
        var dir = SessionProject("session", "preview.png");

        var path = MainWindowViewModel.RecentPreviewPath(dir);

        Assert.Equal(Path.Combine(dir, "preview.png"), path);
    }

    [Fact]
    public void A_project_with_no_preview_resolves_to_none()
    {
        var dir = Project("without", preview: null, writeFile: false);

        Assert.Null(MainWindowViewModel.RecentPreviewPath(dir));
    }

    [Fact]
    public void A_preview_naming_a_file_that_isnt_there_resolves_to_none()
    {
        var dir = Project("gone", "preview.png", writeFile: false);

        Assert.Null(MainWindowViewModel.RecentPreviewPath(dir));
    }

    [Fact]
    public void A_folder_that_isnt_a_project_resolves_to_none_rather_than_throwing()
    {
        var dir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(dir);

        Assert.Null(MainWindowViewModel.RecentPreviewPath(dir));
    }

    [Fact]
    public void A_corrupt_manifest_resolves_to_none_rather_than_throwing()
    {
        var dir = Path.Combine(_root, "corrupt");
        Directory.CreateDirectory(dir);
        File.WriteAllText(ModProject.ManifestPathFor(dir), "{ not json");

        Assert.Null(MainWindowViewModel.RecentPreviewPath(dir));
    }

    [Fact]
    public void A_path_that_doesnt_exist_at_all_resolves_to_none()
    {
        Assert.Null(MainWindowViewModel.RecentPreviewPath(Path.Combine(_root, "no-such-mod")));
    }

    [Fact]
    public void A_row_reads_its_name_and_path_off_the_settings_record()
    {
        var row = new RecentModVm(new RecentMod { Name = "Karst Jacket", Path = @"C:\mods\karst" });

        Assert.Equal("Karst Jacket", row.Name);
        Assert.Equal(@"C:\mods\karst", row.Path);
        Assert.Null(row.Thumb);   // a row starts with no image and stays that way when the mod has none
    }

    [Fact]
    public void A_rows_clear_leaves_it_empty_and_is_safe_on_a_row_that_never_had_one()
    {
        // the route a fill takes when the resolve comes back null — the row goes back to empty rather than
        // keeping the thumbnail an earlier fill left standing
        var row = new RecentModVm(new RecentMod { Name = "Karst Jacket", Path = @"C:\mods\karst" });

        row.DisposeThumb();

        Assert.Null(row.Thumb);
    }
}
