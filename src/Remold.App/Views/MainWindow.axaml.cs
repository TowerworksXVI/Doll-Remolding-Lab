using System.Linq;
using System.Threading.Tasks;
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
    }

    /// <summary>Double-click a Pick row → open it in Edit, checking it first. A double-tap landing ON the
    /// checkbox is a mis-detected toggle, not "open", and a row still resolving has no subject to open — both
    /// are ignored.</summary>
    private void OnPickRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (e.Source is Control src && src.FindAncestorOfType<CheckBox>(includeSelf: true) is not null) return;
        if ((e.Source as Control)?.DataContext is { } row)
        {
            if (row is OutfitVm { IsLoading: true } or CharacterVm { IsLoading: true }) return;
            vm.OpenSubjectInEdit(row);
        }
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
        var name = await TextPromptWindow.Show(this, "Save Mod As", "New mod name:", vm.PackageName,
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

    /// <summary>Tools → Settings… — tool paths, the mods folder, the new-mod author default. The Build
    /// pane's "Set 3DMigoto path" opens the same dialog: the loader row's Browse is the app's one picker for
    /// that exe, so the pane offers it rather than forking a second one.</summary>
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
        // Speculative prewarm work is dropped only on a pass the window actually leaves on. A close the
        // modder backs out of leaves the guess running — the app stays open on the subject it was prepared
        // for. It never earns a prompt or a wait either way.
        if (MainWindowViewModel.CloseDropsSpeculativeWork(_closeConfirmed, vm.IsWorkInFlight, vm.CanCloseSilently))
            vm.CancelSpeculativeWork();
        if (_closeConfirmed)
            return;
        // Work in flight: ask before abandoning it, then run the normal save-first close. Precedes the dirty
        // gate — the work often produces the changes that gate would save. The cancel is LAST, past every
        // confirm: its token is terminal, so a window that stays open behind a declined confirm would keep a
        // dead one and silently no-op every later open or materialize.
        if (vm.IsWorkInFlight)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (!await vm.ConfirmCloseWithWorkAsync()) return;   // keep working — stay open
                if (!await vm.ConfirmAppCloseAsync()) return;        // the save-first gate said no — stay open
                vm.CancelInFlightWork();
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
}
