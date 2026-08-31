using System;
using System.IO;
using Remold.App.ViewModels;
using Remold.Core.Project;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The window shell's preview-file half. The page controller tests pin the gestures; these facts
/// pin the project-owned copy, guarded deletion and content stamp without requiring an Avalonia renderer.</summary>
public sealed class BuildPagePreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "drl-build-page-preview-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (MainWindowViewModel Window, AuthoredEditSession Session, string ProjectRoot) Page()
    {
        string projectRoot = Path.Combine(_root, "mod");
        Directory.CreateDirectory(projectRoot);
        var project = AuthoredEditFixtures.Golden();
        project.RootDir = projectRoot;
        return (new MainWindowViewModel(startLoad: false), new AuthoredEditSession(project), projectRoot);
    }

    private string Source(string name, params byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes.Length == 0 ? new byte[] { 1, 2, 3 } : bytes);
        return path;
    }

    [Fact]
    public void Setting_preview_copies_it_under_the_owned_name_and_records_the_session_field()
    {
        var (window, session, root) = Page();
        string source = Source("cover.PNG", 1, 3, 5);

        window.SetPreviewFrom(session, source);

        Assert.Equal("preview.png", session.Snapshot().Info.Preview);
        Assert.Equal(new byte[] { 1, 3, 5 }, File.ReadAllBytes(Path.Combine(root, "preview.png")));
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(root, "preview.png.tmp")));
    }

    [Fact]
    public void Replacing_preview_under_another_extension_removes_only_the_previous_owned_copy()
    {
        var (window, session, root) = Page();
        window.SetPreviewFrom(session, Source("first.png"));

        window.SetPreviewFrom(session, Source("second.jpg", 8, 9));

        Assert.Equal("preview.jpg", session.Snapshot().Info.Preview);
        Assert.False(File.Exists(Path.Combine(root, "preview.png")));
        Assert.Equal(new byte[] { 8, 9 }, File.ReadAllBytes(Path.Combine(root, "preview.jpg")));
    }

    [Fact]
    public void Removing_a_preview_the_app_did_not_name_clears_the_field_and_keeps_the_file()
    {
        var (window, session, root) = Page();
        string sibling = Path.Combine(root, "art.png");
        File.WriteAllBytes(sibling, new byte[] { 4, 5, 6 });
        session.SetPreview("art.png");
        var preview = window.ReadPreview(session.Snapshot());

        window.RemovePreviewFile(session, preview);

        Assert.Null(session.Snapshot().Info.Preview);
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void Preview_stamp_changes_when_same_named_file_bytes_change()
    {
        var (window, session, root) = Page();
        window.SetPreviewFrom(session, Source("first.png", 1, 2));
        var first = window.ReadPreview(session.Snapshot());

        File.WriteAllBytes(Path.Combine(root, "preview.png"), new byte[] { 9, 8, 7 });
        var second = window.ReadPreview(session.Snapshot());

        Assert.NotEqual(first.Stamp, second.Stamp);
        Assert.Equal(first.RelativeFile, second.RelativeFile);
    }

    [Fact]
    public void Preview_read_carries_the_source_dimensions_for_the_page_caption()
    {
        var (window, session, root) = Page();
        string file = Path.Combine(root, "preview.png");
        using (var image = new Image<Rgba32>(12, 7)) image.SaveAsPng(file);
        session.SetPreview("preview.png");

        var preview = window.ReadPreview(session.Snapshot());

        Assert.Equal(12, preview.PixelWidth);
        Assert.Equal(7, preview.PixelHeight);
    }

}
