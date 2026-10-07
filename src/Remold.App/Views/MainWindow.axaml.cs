using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Remold.App.ViewModels;
using Remold.Core;

namespace Remold.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        // Tunnel, and window-wide: a Pick row's visible name is the CheckBox's own content, so by the time
        // a press bubbles the CheckBox has already eaten it — the open has to see the press on the way DOWN,
        // and it recognises Pick rows by their view-model type rather than by naming three trees.
        AddHandler(PointerPressedEvent, OnPickRowPointerPressed, RoutingStrategies.Tunnel);
        // THE RULE, and the reason these three sit on the HOME PANE rather than on the window: the Edit and
        // Build pages run their own drag handlers, and a window-level handler bubbles AFTER theirs and would
        // overwrite every page drag's effects with None. The home pane is their sibling, so a drag over a
        // page never reaches here at all — and the guards below still refuse to touch DragEffects unless
        // Home is showing, so nothing about a page's drop depends on where this is attached.
        //
        // DragEnter as well as DragOver: crossing an element boundary mid-drag raises ENTER, not Over, and an
        // unhandled DragEnter leaves the platform's permissive effects standing — the cursor flickers, and a
        // release on that frame delivers a drop the home screen meant to refuse.
        var home = this.FindControl<Border>("HomePane")
            ?? throw new InvalidOperationException("the window has no home pane to accept a dropped mod on");
        home.AddHandler(DragDrop.DragEnterEvent, OnHomeDragOver);
        home.AddHandler(DragDrop.DragOverEvent, OnHomeDragOver);
        home.AddHandler(DragDrop.DropEvent, OnHomeDrop);
    }

    /// <summary>The copy cursor only over the home screen, and only for one dropped folder or file: a drop
    /// while a mod is open changes nothing, and two things dropped together name no one mod to import.</summary>
    private void OnHomeDragOver(object? sender, DragEventArgs e)
    {
        if (!HomeShowing) return;   // never speak for a drag the pages own
        e.DragEffects = CanImportDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnHomeDrop(object? sender, DragEventArgs e)
    {
        if (!HomeShowing) return;
        if (!CanImportDrop(e)) { e.DragEffects = DragDropEffects.None; return; }
        if (DroppedPath(e) is not { } path) { e.DragEffects = DragDropEffects.None; return; }
        // Normalize what goes back to the drag source — unset, it returns the platform's Copy|Move|Link.
        e.DragEffects = DragDropEffects.Copy;
        // Fire-and-forget the top of the async chain — the import's own busy gate serializes the rest.
        _ = ImportDroppedAsync(path);
    }

    private bool HomeShowing => DataContext is MainWindowViewModel { ShowHome: true };

    private bool CanImportDrop(DragEventArgs e) =>
        HomeShowing && e.DataTransfer.Contains(DataFormat.File) && DroppedPath(e) is not null;

    /// <summary>The one local path a drop carries, or null when it carries none or more than one.</summary>
    private static string? DroppedPath(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return null;
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        return paths.Count == 1 ? paths[0] : null;
    }

    private async Task ImportDroppedAsync(string path)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!vm.ShowHome) return;
        if (!await vm.ConfirmLeaveProjectAsync()) return;
        await vm.ImportModAsync(path);
    }

    /// <summary>Import mod from zip… — read a built mod's distribution zip back into a new project.</summary>
    private async void OnImportModZip(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!await vm.ConfirmLeaveProjectAsync()) return;
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import mod from zip",
            AllowMultiple = false,
            SuggestedStartLocation = await LibraryStartAsync(),
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Zip files") { Patterns = new[] { "*.zip" } },
                new FilePickerFileType("All files") { Patterns = new[] { "*" } },
            },
        });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) await vm.ImportModAsync(path!);
    }

    /// <summary>Import mod folder… — read a built mod folder back into a new project.</summary>
    private async void OnImportModFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!await vm.ConfirmLeaveProjectAsync()) return;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Import mod folder",
            AllowMultiple = false,
            SuggestedStartLocation = await LibraryStartAsync(),
        });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) await vm.ImportModAsync(path!);
    }

    /// <summary>Double-click a Pick row → open it in Edit, checking it first. Handling the second press
    /// here keeps it from reaching the row's checkbox as a second toggle. A double-click landing on the
    /// checkbox's own box glyph stays a toggle and never opens (see <see cref="PickRowOpenTarget"/>).</summary>
    private void OnPickRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (DataContext is not MainWindowViewModel vm) return;
        if (!PickRowOpenTarget(e.Source as Control, out object? row)) return;
        e.Handled = true;
        vm.OpenSubjectInEdit(row!);
    }

    /// <summary>Whether a double-click landing on <paramref name="source"/> means "open this row": yes
    /// anywhere on an openable Pick row, its checkbox LABEL included — the label is the row's visible name
    /// and the natural target — but not on the checkbox's own box glyph, where a double-click is two
    /// deliberate toggles. The label lives inside the CheckBox as its content, so the two are told apart
    /// by the visual walk: content passes a ContentPresenter on the way up to the CheckBox, template
    /// chrome does not. A multi-outfit character header names no single subject and a row still resolving
    /// has nothing to open; neither is a target.</summary>
    internal static bool PickRowOpenTarget(Control? source, out object? row)
    {
        row = source?.DataContext;
        if (row is not (OutfitVm or CharacterVm { IsSingleOutfit: true })) return false;
        if (row is OutfitVm { IsLoading: true } or CharacterVm { IsLoading: true }) return false;
        bool sawContent = false;
        for (Visual? c = source; c is not null; c = c.GetVisualParent())
        {
            if (c is Avalonia.Controls.Presenters.ContentPresenter) sawContent = true;
            if (c is CheckBox) return sawContent;
        }
        return true;
    }

    // ---- File menu ----------------------------------------------------

    /// <summary>New Mod — a fresh in-memory project; the folder is created and auto-named on first export.</summary>
    private async void OnNewMod(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (await vm.ConfirmLeaveProjectAsync()) vm.NewMod();
    }

    /// <summary>Save Mod As… — copy the project under a new name, then switch to it.</summary>
    private async void OnSaveModAs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var name = await TextPromptWindow.Show(this, "Save mod as", "New mod name:", vm.PackageName,
            confirmLabel: "Save");
        if (name is not null) await vm.SaveModAs(name);
    }

    /// <summary>The mods-library folder as a picker start location, created if needed.</summary>
    private async Task<IStorageFolder?> LibraryStartAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return null;
        try { System.IO.Directory.CreateDirectory(vm.LibraryRoot); } catch { /* best effort */ }
        return await StorageProvider.TryGetFolderFromPathAsync(vm.LibraryRoot);
    }

    /// <summary>Open Mod… — the VM loads the picked folder from disk, with no re-export.</summary>
    private async void OnOpenMod(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (!await vm.ConfirmLeaveProjectAsync()) return;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open mod project folder",
            AllowMultiple = false,
            SuggestedStartLocation = await LibraryStartAsync(),
        });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) await vm.OpenModAsync(path!);
    }

    /// <summary>Settings — tool paths, the mods folder, and the new-mod author default.</summary>
    private async void OnSettings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var result = await SettingsWindow.Show(this, vm.BuildSettingsInput());
        if (result is not null) vm.ApplySettings(result);
    }

    /// <summary>Help → About — the app/schema versions and where the app's data lives.</summary>
    private async void OnAbout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await AboutWindow.Show(this, vm.BuildAboutInfo());
    }

    /// <summary>Tools → Locate game… — the manual fallback. The VM accepts the pick only if it is a real GF2
    /// install.</summary>
    private async void OnLocateGame(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Locate the GIRLS' FRONTLINE 2 EXILIUM install folder",
            AllowMultiple = false,
        });
        var path = picked.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(path)) vm.SetGameDir(path!);
    }

    private bool _closeConfirmed;

    /// <summary>The dirty gate: closing with unsaved changes SAVES rather than discards. The close is
    /// cancelled, the save-first flow runs async, and a go-ahead re-closes with the flag set so this doesn't
    /// loop.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is not MainWindowViewModel vm)
            return;
        if (_closeConfirmed)
            return;
        // Work in flight: ask before abandoning it, then run the normal save-first close. Precedes the dirty
        // gate — the work often produces the changes that gate would save.
        if (vm.IsWorkInFlight)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (!await vm.ConfirmCloseWithWorkAsync()) return;   // keep working — stay open
                if (!await vm.ConfirmAppCloseAsync()) return;        // the save-first gate said no — stay open
                _closeConfirmed = true;
                Close();
            });
            return;
        }
        if (vm.CanCloseSilently)
            return;
        e.Cancel = true;
        Dispatcher.UIThread.Post(async () =>
        {
            if (await vm.ConfirmAppCloseAsync()) { _closeConfirmed = true; Close(); }
        });
    }

    /// <summary>The window is gone: release the native bitmaps behind the recent rows.</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as MainWindowViewModel)?.ReleasePreviewBitmaps();
    }
}
