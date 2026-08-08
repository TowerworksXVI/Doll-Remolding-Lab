using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Remold.App.ViewModels;
using Remold.Core.Migoto;
using Remold.Core.Project;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The mod's preview image, driven through the view model without standing up the window. The contract the
/// pane holds: the image is COPIED into the project (never referenced where it lies), one project holds one
/// copy, a file that goes missing is its own state the build reports, and every one of these edits moves
/// the result bar's stale line exactly as a change tick does.
///
/// <para>What this file cannot reach: the drag-drop event plumbing, the file picker, and the bitmap decode —
/// those are the window's own and go to a smoke test. The decode is not merely awkward here but impossible:
/// <c>Bitmap</c> goes through the platform render backend, and with no AppBuilder standing (the test project
/// takes no Avalonia.Headless or Skia harness) every decode throws
/// <c>InvalidOperationException: Unable to locate 'Avalonia.Platform.IPlatformRenderInterface'</c>, which the
/// pane catches as "wouldn't decode". That is why the decode WIDTH is pinned through a pure function rather
/// than by measuring a decoded bitmap — a real-decode fixture cannot run in this host without adding a
/// headless rendering harness to the project.</para>
/// </summary>
[Collection("Dispatcher")]
public class BuildPreviewImageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "drl-preview-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string ModName = "preview mod";

    /// <summary>A pane on a SAVED project. The folder is named for the mod's own slug, so the autosave every
    /// preview edit runs has no rename to do.</summary>
    private MainWindowViewModel SavedPane(string? sub = null)
    {
        var folder = Path.Combine(sub is null ? _root : Path.Combine(_root, sub), ModNaming.Slug(ModName));
        Directory.CreateDirectory(folder);
        var vm = new MainWindowViewModel(startLoad: false);
        vm.PackageName = ModName;
        vm.PackageAuthor = "";
        vm.PackageVersion = "1.0";
        vm.PackageDescription = "";
        vm.PackageToggleKey = null;
        vm.OpenProject.Save(folder);
        vm.RefreshPreviewState();
        return vm;
    }

    /// <summary>A pane with a build result on screen, so the stale line has something to sit on.</summary>
    private static void LandABuild(MainWindowViewModel vm)
    {
        vm.CaptureBuildBaseline();
        vm.LastBuildDir = @"C:\published\preview mod";
        Assert.False(vm.BuildResultStale);
    }

    /// <summary>A file with the given extension and distinct bytes — nothing here decodes it.</summary>
    private string SourceImage(string name, int size = 64)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, Enumerable.Range(0, size).Select(i => (byte)i).ToArray());
        return path;
    }

    private static string ProjectFile(MainWindowViewModel vm, string relative) =>
        Path.Combine(vm.OpenProject.RootDir!, relative);

    [Fact]
    public void Setting_a_preview_copies_it_into_the_project_and_records_the_relative_name()
    {
        var vm = SavedPane();
        var source = SourceImage("shot.PNG");

        vm.SetPreviewFrom(source);

        Assert.Equal("preview.png", vm.OpenProject.Info.Preview);   // the source's extension, lowercased
        Assert.True(File.Exists(ProjectFile(vm, "preview.png")));
        Assert.True(vm.HasPreview);
        Assert.False(vm.PreviewMissing);
        Assert.False(vm.HasNoPreview);
        // persisted, not just held: the manifest on disk carries it
        Assert.Equal("preview.png", ModProject.Load(vm.OpenProject.RootDir!).Info.Preview);
        // and the source stays where it was — the project took a copy
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Replacing_under_another_extension_drops_the_previous_copy()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("first.png"));

        vm.SetPreviewFrom(SourceImage("second.jpg"));

        Assert.Equal("preview.jpg", vm.OpenProject.Info.Preview);
        Assert.True(File.Exists(ProjectFile(vm, "preview.jpg")));
        Assert.False(File.Exists(ProjectFile(vm, "preview.png")));   // one project, one copy
        Assert.True(vm.HasPreview);
    }

    [Fact]
    public void Replacing_under_the_same_extension_overwrites_the_one_copy()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("first.png", size: 32));

        vm.SetPreviewFrom(SourceImage("second.png", size: 200));

        Assert.Equal("preview.png", vm.OpenProject.Info.Preview);
        Assert.Equal(200, new FileInfo(ProjectFile(vm, "preview.png")).Length);
    }

    [Fact]
    public void Removing_clears_the_field_and_deletes_the_copy()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));

        vm.RemovePreviewCommand.Execute(null);

        Assert.Null(vm.OpenProject.Info.Preview);
        Assert.False(File.Exists(ProjectFile(vm, "preview.png")));
        Assert.True(vm.HasNoPreview);
        Assert.False(vm.HasPreview);
        Assert.Null(ModProject.Load(vm.OpenProject.RootDir!).Info.Preview);
    }

    [Fact]
    public void A_non_image_is_refused_on_the_panes_line_and_nothing_changes()
    {
        var vm = SavedPane();
        var source = SourceImage("notes.txt");

        vm.SetPreviewFrom(source);

        Assert.Equal(MainWindowViewModel.PreviewNotAnImage, vm.Footer.Text);
        Assert.Null(vm.OpenProject.Info.Preview);
        Assert.True(vm.HasNoPreview);
        Assert.False(File.Exists(ProjectFile(vm, "preview.txt")));
    }

    [Fact]
    public void A_refused_drop_leaves_an_existing_preview_standing()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));

        vm.DropPreview(new[] { SourceImage("notes.txt") });

        Assert.Equal(MainWindowViewModel.PreviewNotAnImage, vm.Footer.Text);
        Assert.Equal("preview.png", vm.OpenProject.Info.Preview);
        Assert.True(File.Exists(ProjectFile(vm, "preview.png")));
    }

    [Fact]
    public void A_drop_of_more_than_one_file_is_refused()
    {
        var vm = SavedPane();

        vm.DropPreview(new[] { SourceImage("a.png"), SourceImage("b.png") });

        Assert.Equal(MainWindowViewModel.PreviewOneAtATime, vm.Footer.Text);
        Assert.Null(vm.OpenProject.Info.Preview);
    }

    [Fact]
    public void An_empty_drop_is_refused_on_its_own_line_rather_than_the_count_one()
    {
        var vm = SavedPane();

        vm.DropPreview(Array.Empty<string>());

        // a browser/zip/mail drag carries no local file at all; "one at a time" would answer the wrong
        // question
        Assert.Equal(MainWindowViewModel.PreviewNoFileInDrop, vm.Footer.Text);
        Assert.Null(vm.OpenProject.Info.Preview);
    }

    [Fact]
    public void Replacing_lands_even_while_the_panes_own_decode_holds_the_copy_open()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("first.png", size: 32));

        // the share flags the pane's thumbnail decode opens the preview with — a swap that can't survive
        // them refuses every replace made before the previous decode finishes
        using var held = new FileStream(ProjectFile(vm, "preview.png"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        vm.SetPreviewFrom(SourceImage("second.png", size: 200));

        Assert.Equal(200, new FileInfo(ProjectFile(vm, "preview.png")).Length);
        Assert.Equal("preview.png", vm.OpenProject.Info.Preview);
        Assert.False(File.Exists(ProjectFile(vm, "preview.png.tmp")));
    }

    [Fact]
    public void A_build_in_flight_refuses_a_new_preview_and_leaves_the_copy_alone()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("first.png", size: 32));
        vm.IsModBuilding = true;

        vm.SetPreviewFrom(SourceImage("second.png", size: 200));

        Assert.Equal(MainWindowViewModel.BuildRunningReason, vm.Footer.Text);
        Assert.Equal("preview.png", vm.OpenProject.Info.Preview);
        Assert.Equal(32, new FileInfo(ProjectFile(vm, "preview.png")).Length);   // the run's copy is untouched
    }

    [Fact]
    public void A_build_in_flight_refuses_a_remove_and_leaves_the_copy_alone()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));
        vm.IsModBuilding = true;

        vm.RemovePreviewCommand.Execute(null);

        Assert.Equal(MainWindowViewModel.BuildRunningReason, vm.Footer.Text);
        Assert.Equal("preview.png", vm.OpenProject.Info.Preview);
        Assert.True(File.Exists(ProjectFile(vm, "preview.png")));
    }

    [Fact]
    public void A_copy_that_fails_leaves_the_previous_preview_whole_and_re_reads_the_state()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("first.png", size: 64));
        var dest = ProjectFile(vm, "preview.png");
        var before = File.ReadAllBytes(dest);

        // a DIRECTORY named .png passes the extension gate and fails the copy partway in
        var bogus = Path.Combine(_root, "folder.png");
        Directory.CreateDirectory(bogus);

        vm.SetPreviewFrom(bogus);

        Assert.StartsWith("Couldn't copy the image.", vm.Footer.Text);
        Assert.Equal(before, File.ReadAllBytes(dest));          // not truncated by the attempt
        Assert.Equal("preview.png", vm.OpenProject.Info.Preview);
        Assert.True(vm.HasPreview);                             // the control re-read reality
        Assert.False(vm.PreviewMissing);
        Assert.False(File.Exists(dest + ".tmp"));               // the staged copy is cleaned up
    }

    [Fact]
    public void Removing_a_preview_the_app_did_not_write_clears_the_field_and_keeps_the_file()
    {
        var vm = SavedPane();
        // a hand-edited or shared manifest, pointing the field at one of the project's own assets
        var sibling = ProjectFile(vm, "replace_body.glb");
        File.WriteAllBytes(sibling, new byte[] { 1, 2, 3 });
        vm.OpenProject.Info.Preview = "replace_body.glb";
        vm.RefreshPreviewState();

        vm.RemovePreviewCommand.Execute(null);

        Assert.Null(vm.OpenProject.Info.Preview);
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void Replacing_a_preview_the_app_did_not_write_keeps_that_file()
    {
        var vm = SavedPane();
        var sibling = ProjectFile(vm, "replace_body.glb");
        File.WriteAllBytes(sibling, new byte[] { 1, 2, 3 });
        vm.OpenProject.Info.Preview = "replace_body.glb";
        vm.RefreshPreviewState();

        vm.SetPreviewFrom(SourceImage("shot.png"));

        Assert.Equal("preview.png", vm.OpenProject.Info.Preview);
        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public void Every_accepted_extension_is_taken()
    {
        // Each extension gets its own project folder: recreating one path right after a recursive
        // delete races the pending delete on Windows.
        foreach (var ext in MainWindowViewModel.PreviewExtensions)
        {
            var vm = SavedPane(sub: "ext" + ext.TrimStart('.'));
            vm.SetPreviewFrom(SourceImage("shot" + ext));
            Assert.Equal("preview" + ext, vm.OpenProject.Info.Preview);
        }
    }

    [Fact]
    public void A_file_deleted_behind_the_app_reads_as_missing_and_warns_the_build()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));

        File.Delete(ProjectFile(vm, "preview.png"));
        vm.RefreshPreviewState();

        Assert.True(vm.PreviewMissing);
        Assert.False(vm.HasPreview);
        Assert.False(vm.HasNoPreview);                                    // Replace/Remove are the way out
        Assert.Equal("preview.png", vm.OpenProject.Info.Preview);          // the field is NOT cleared for it
        Assert.Contains(MainWindowViewModel.PreviewMissingWarning, vm.BuildWarnings);
        Assert.True(vm.HasBuildWarnings);
    }

    [Fact]
    public void Replacing_a_missing_preview_takes_the_warning_back_off()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));
        File.Delete(ProjectFile(vm, "preview.png"));
        vm.RefreshPreviewState();

        vm.SetPreviewFrom(SourceImage("again.png"));

        Assert.True(vm.HasPreview);
        Assert.False(vm.PreviewMissing);
        Assert.DoesNotContain(MainWindowViewModel.PreviewMissingWarning, vm.BuildWarnings);
    }

    [Fact]
    public void Removing_a_missing_preview_takes_the_warning_back_off()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));
        File.Delete(ProjectFile(vm, "preview.png"));
        vm.RefreshPreviewState();

        vm.RemovePreviewCommand.Execute(null);

        Assert.True(vm.HasNoPreview);
        Assert.Null(vm.OpenProject.Info.Preview);
        Assert.DoesNotContain(MainWindowViewModel.PreviewMissingWarning, vm.BuildWarnings);
    }

    [Fact]
    public void Setting_a_preview_makes_the_last_build_stale()
    {
        var vm = SavedPane();
        LandABuild(vm);

        vm.SetPreviewFrom(SourceImage("shot.png"));

        Assert.True(vm.BuildResultStale);
    }

    [Fact]
    public void Replacing_under_the_same_name_still_makes_the_last_build_stale()
    {
        // the name the sidecar carries doesn't change, so only the file's own stamp can tell the result bar
        // the built folder no longer holds this image
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("first.png", size: 32));
        LandABuild(vm);

        vm.SetPreviewFrom(SourceImage("second.png", size: 200));

        Assert.True(vm.BuildResultStale);
    }

    [Fact]
    public void Removing_the_preview_makes_the_last_build_stale()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));
        LandABuild(vm);

        vm.RemovePreviewCommand.Execute(null);

        Assert.True(vm.BuildResultStale);
    }

    [Fact]
    public void A_refused_drop_leaves_the_last_build_alone()
    {
        var vm = SavedPane();
        LandABuild(vm);

        vm.DropPreview(new[] { SourceImage("notes.txt") });

        Assert.False(vm.BuildResultStale);
    }

    [Fact]
    public void Putting_the_same_image_back_takes_the_stale_line_off_again()
    {
        var vm = SavedPane();
        var source = SourceImage("shot.png");
        vm.SetPreviewFrom(source);
        LandABuild(vm);

        vm.RemovePreviewCommand.Execute(null);
        Assert.True(vm.BuildResultStale);

        vm.SetPreviewFrom(source);   // same bytes, same name, same stamp as the build consumed

        Assert.False(vm.BuildResultStale);
    }

    [Fact]
    public void Without_a_project_folder_the_control_is_off_and_says_why()
    {
        var vm = new MainWindowViewModel(startLoad: false);   // New Mod: nothing minted yet
        vm.RefreshPreviewState();

        Assert.False(vm.PreviewEnabled);
        Assert.Equal(MainWindowViewModel.PreviewNeedsSave, vm.PreviewPickTip);

        vm.SetPreviewFrom(Path.Combine(_root, "shot.png"));

        Assert.Equal(MainWindowViewModel.PreviewNeedsSave, vm.Footer.Text);
        Assert.Null(vm.OpenProject.Info.Preview);
    }

    [Fact]
    public void A_saved_project_turns_the_control_on()
    {
        var vm = SavedPane();

        Assert.True(vm.PreviewEnabled);
        Assert.NotEqual(MainWindowViewModel.PreviewNeedsSave, vm.PreviewPickTip);
    }

    [Fact]
    public void The_missing_state_names_the_file_the_project_carries()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.jpg"));
        File.Delete(ProjectFile(vm, "preview.jpg"));
        vm.RefreshPreviewState();

        Assert.True(vm.PreviewMissing);
        Assert.Equal("preview.jpg missing", vm.PreviewMissingTitle);
    }

    [Fact]
    public void A_build_in_flight_answers_the_slots_tooltip_before_every_other_reason()
    {
        var vm = SavedPane();
        Assert.Equal(MainWindowViewModel.PreviewPickReady, vm.PreviewPickTip);

        vm.IsModBuilding = true;
        Assert.Equal(MainWindowViewModel.BuildRunningReason, vm.PreviewPickTip);

        vm.IsModBuilding = false;
        Assert.Equal(MainWindowViewModel.PreviewPickReady, vm.PreviewPickTip);
    }

    [Fact]
    public void The_slots_tooltip_re_raises_when_the_build_flag_flips()
    {
        var vm = SavedPane();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsModBuilding = true;

        // without this the tooltip keeps answering "ships with the mod" under a control a build turned off
        Assert.Contains(nameof(MainWindowViewModel.PreviewPickTip), raised);
    }

    [Fact]
    public async Task A_file_that_wont_decode_settles_into_its_own_visible_state()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));   // an accepted extension over bytes that aren't an image

        // while the decode is out the box is WAITING, not failed
        Assert.True(vm.HasPreview);
        Assert.True(vm.PreviewDecoding);
        Assert.False(vm.PreviewUndecodable);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.PreviewDecoding && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        // settled with nothing: the file is there, so the state stays "has preview" and the box says so
        Assert.False(vm.PreviewDecoding);
        Assert.Null(vm.PreviewImage);
        Assert.True(vm.HasPreview);
        Assert.True(vm.PreviewUndecodable);
    }

    [Fact]
    public void The_thumbnails_tip_carries_the_SOURCE_files_own_size_not_the_thumbnails()
    {
        var source = Path.Combine(_root, "shot.png");
        Directory.CreateDirectory(_root);
        using (var img = new Image<Rgba32>(12, 7)) img.SaveAsPng(source);

        // the on-screen bitmap is decoded to the slot's width; the size on the tip is the file's own
        Assert.Equal("preview.png · 12×7",
            MainWindowViewModel.PreviewTip("preview.png", source, decoded: true));
    }

    [Fact]
    public void A_file_that_did_not_decode_gets_no_size_claim_on_its_tip()
    {
        var source = Path.Combine(_root, "shot.png");
        Directory.CreateDirectory(_root);
        using (var img = new Image<Rgba32>(12, 7)) img.SaveAsPng(source);

        // the identify step will put a size on bytes no decoder can render; a size under a "no preview"
        // tile would be a claim the box itself contradicts
        Assert.Equal("preview.png", MainWindowViewModel.PreviewTip("preview.png", source, decoded: false));
    }

    [Fact]
    public async Task The_tip_names_the_file_from_the_moment_it_is_set()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));

        Assert.Equal("preview.png", vm.PreviewThumbTip);   // before the decode has settled

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (vm.PreviewDecoding && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.Equal("preview.png", vm.PreviewThumbTip);   // and after, with nothing decoded to size
    }

    [Fact]
    public void A_source_narrower_than_the_decode_width_is_decoded_at_its_own_width()
    {
        // Bitmap.DecodeToWidth has no natural-size clamp: it scales a narrower source UP to the width asked
        // for, and the hover tip draws that stretch at full size. A 64px icon must not hang in the flyout
        // blown up to 360.
        Assert.Equal(64, MainWindowViewModel.PreviewDecodeWidth(64));
        Assert.Equal(479, MainWindowViewModel.PreviewDecodeWidth(479));
    }

    [Fact]
    public void A_source_at_or_wider_than_the_decode_width_is_decoded_at_the_decode_width()
    {
        // the cap is what keeps a 4K source off the heap at full size
        Assert.Equal(MainWindowViewModel.PreviewThumbWidth,
            MainWindowViewModel.PreviewDecodeWidth(MainWindowViewModel.PreviewThumbWidth));
        Assert.Equal(MainWindowViewModel.PreviewThumbWidth, MainWindowViewModel.PreviewDecodeWidth(3840));
    }

    [Fact]
    public void A_source_whose_width_cannot_be_read_falls_back_to_the_full_decode_width()
    {
        // exactly what the decode did before the cap — a header that won't read must not turn a preview that
        // rendered into one that doesn't
        Assert.Equal(MainWindowViewModel.PreviewThumbWidth, MainWindowViewModel.PreviewDecodeWidth(null));
        // and a header claiming a nonsense width is refused the same way, rather than asking the decoder for
        // a zero-wide bitmap
        Assert.Equal(MainWindowViewModel.PreviewThumbWidth, MainWindowViewModel.PreviewDecodeWidth(0));
        Assert.Equal(MainWindowViewModel.PreviewThumbWidth, MainWindowViewModel.PreviewDecodeWidth(-8));
    }

    [Fact]
    public void Switching_project_drops_the_thumbnails_tip_with_the_pane()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));
        Assert.NotEqual("", vm.PreviewThumbTip);

        vm.NewMod();

        Assert.Equal("", vm.PreviewThumbTip);
    }

    [Fact]
    public void A_cancelled_remove_changes_nothing()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));
        vm.PreviewRemoveConfirm = () => Task.FromResult(false);

        vm.RemovePreviewCommand.Execute(null);

        Assert.Equal("preview.png", vm.OpenProject.Info.Preview);
        Assert.True(File.Exists(ProjectFile(vm, "preview.png")));
        Assert.True(vm.HasPreview);
        Assert.Equal("preview.png", ModProject.Load(vm.OpenProject.RootDir!).Info.Preview);
    }

    [Fact]
    public void A_confirmed_remove_clears_the_field_and_deletes_the_copy()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));
        vm.PreviewRemoveConfirm = () => Task.FromResult(true);

        vm.RemovePreviewCommand.Execute(null);

        Assert.Null(vm.OpenProject.Info.Preview);
        Assert.False(File.Exists(ProjectFile(vm, "preview.png")));
        Assert.True(vm.HasNoPreview);
    }

    [Fact]
    public void The_confirm_names_the_file_it_is_about_to_delete()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));

        Assert.Equal(
            MainWindowViewModel.PreviewRemoveQuestion + "\n\npreview.png is deleted from the mod's folder.",
            vm.PreviewRemoveBody);
    }

    [Fact]
    public void The_confirm_promises_no_delete_when_the_file_is_not_the_apps_to_delete()
    {
        var vm = SavedPane();
        File.WriteAllBytes(ProjectFile(vm, "replace_body.glb"), new byte[] { 1, 2, 3 });
        vm.OpenProject.Info.Preview = "replace_body.glb";
        vm.RefreshPreviewState();

        Assert.EndsWith(MainWindowViewModel.PreviewRemoveKeepsFiles, vm.PreviewRemoveBody);
    }

    [Fact]
    public void The_confirm_promises_no_delete_when_the_file_is_already_gone()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));
        File.Delete(ProjectFile(vm, "preview.png"));
        vm.RefreshPreviewState();

        Assert.EndsWith(MainWindowViewModel.PreviewRemoveKeepsFiles, vm.PreviewRemoveBody);
    }

    [Fact]
    public void A_build_in_flight_refuses_the_remove_before_it_asks()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));
        bool asked = false;
        vm.PreviewRemoveConfirm = () => { asked = true; return Task.FromResult(true); };
        vm.IsModBuilding = true;

        vm.RemovePreviewCommand.Execute(null);

        Assert.False(asked);   // never confirm something that would then be refused
        Assert.Equal(MainWindowViewModel.BuildRunningReason, vm.Footer.Text);
        Assert.True(File.Exists(ProjectFile(vm, "preview.png")));
    }

    [Fact]
    public void A_pane_with_no_preview_is_never_the_undecodable_state()
    {
        var vm = SavedPane();

        Assert.True(vm.HasNoPreview);
        Assert.False(vm.PreviewUndecodable);
    }

    [Fact]
    public void Switching_project_drops_the_preview_state_with_the_pane()
    {
        var vm = SavedPane();
        vm.SetPreviewFrom(SourceImage("shot.png"));
        Assert.True(vm.HasPreview);

        vm.NewMod();

        Assert.False(vm.HasPreview);
        Assert.False(vm.PreviewMissing);
        Assert.Null(vm.PreviewImage);
    }
}
