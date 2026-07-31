using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Remold.Core;
using Remold.Core.Textures;

namespace Remold.App.Views;

/// <summary>The settings shown when the window opens: editable values plus the read-only auto-detect
/// displays the form needs.</summary>
public sealed class SettingsInput
{
    public string? GamePath { get; init; }
    public string? BlenderPath { get; init; }              // null = auto-detect
    public string? BlenderAuto { get; init; }              // auto-detected path, for display only
    public string? ImageEditorPath { get; init; }          // null = OS default
    public IReadOnlyList<string> DetectedEditors { get; init; } = Array.Empty<string>();
    public string? LibraryRoot { get; init; }              // null = default
    public string DefaultLibrary { get; init; } = "";
    public string? MigotoLoaderExe { get; init; }          // null = unset (no default, never detected)
    public string Author { get; init; } = "";
    public int RecentCount { get; init; }
    public int? EncoderCpuLimit { get; init; }             // null = all cores
}

/// <summary>The edited settings the user accepted. A null field means "fall back" — auto-detect, OS
/// default, default library.</summary>
public sealed class SettingsResult
{
    public string? GamePath { get; set; }
    public string? BlenderPath { get; set; }
    public string? ImageEditorPath { get; set; }
    public string? LibraryRoot { get; set; }
    public string? MigotoLoaderExe { get; set; }
    public string Author { get; set; } = "";
    public bool ClearRecents { get; set; }
    public int? EncoderCpuLimit { get; set; }              // null = all cores
}

/// <summary>
/// The Settings dialog, code-built so it needs no XAML and inherits the app theme. Returns the edited
/// <see cref="SettingsResult"/>, or null if cancelled.
/// </summary>
public sealed class SettingsWindow : Window
{
    // One short pointer for any bad pick — the path field is no place for cache diagnostics.
    private const string WrongFolderHint = "Select the folder that contains GF2_Exilium.exe.";

    private string? _gamePath;
    private string? _blenderPath;
    private string? _imageEditorPath;
    private string? _libraryRoot;
    private readonly string? _migotoLoaderExe;
    private bool _clearRecents;

    private readonly string _defaultLibrary;
    private readonly string? _blenderAuto;

    private readonly TextBox _gameBox;
    private readonly TextBlock _gameStatus;
    private readonly TextBox _blenderBox;
    private readonly TextBox _libraryBox;
    private readonly TextBox _migotoBox;
    private readonly TextBox _authorBox;
    private readonly ComboBox _editorBox;
    private readonly int? _encoderCpuLimit;
    private readonly Button _clearRecentsButton;
    private readonly List<EditorChoice> _editorChoices = new();

    private sealed record EditorChoice(string Display, string? Path)
    {
        public override string ToString() => Display;
    }

    private SettingsWindow(SettingsInput input)
    {
        Title = "Settings";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // The popup background IS the panel colour; the window chrome is the separation.
        Background = ResBrush("HudPanelBrush") ?? Brushes.Transparent;

        _gamePath = input.GamePath;
        _blenderPath = input.BlenderPath;
        _blenderAuto = input.BlenderAuto;
        _imageEditorPath = input.ImageEditorPath;
        _libraryRoot = input.LibraryRoot;
        _migotoLoaderExe = input.MigotoLoaderExe;
        _defaultLibrary = input.DefaultLibrary;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        int row = 0;

        // ── Game folder ───────────────────────────────────────────────────────
        // Validated on Save. The game path is the ROOT (the folder holding GF2_Exilium.exe) — the one
        // canonical value everywhere.
        _gameBox = new TextBox { Text = _gamePath ?? "", Watermark = "Paste or Browse to the game folder", VerticalAlignment = VerticalAlignment.Center };
        _gameStatus = new TextBlock { FontSize = 11, Foreground = Subtext(), IsVisible = false, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var gameRe = SmallButton("Re-detect");
        gameRe.Click += (_, _) => RedetectGame();
        var gameBrowse = SmallButton("Browse…");
        gameBrowse.Click += async (_, _) => await BrowseGame();
        AddRow(grid, ref row, "Game folder", _gameBox, _gameStatus, gameRe, gameBrowse);

        // ── Blender ───────────────────────────────────────────────────────────
        // Blank = auto-detect; an explicit exe when more than one Blender is installed.
        _blenderBox = new TextBox
        {
            Text = _blenderPath ?? "",
            Watermark = $"Auto-detect · {_blenderAuto ?? "not found"}",
            VerticalAlignment = VerticalAlignment.Center,
        };
        var blenderAutoBtn = SmallButton("Auto");
        blenderAutoBtn.Click += (_, _) => _blenderBox.Text = "";   // blank → auto-detect
        var blenderBrowse = SmallButton("Browse…");
        blenderBrowse.Click += async (_, _) => await BrowseExe("Locate blender.exe", "Blender", new[] { "blender.exe" },
            p => _blenderBox.Text = p);
        var blenderHint = new TextBlock { Text = "Blank auto-detects. Set a path if more than one Blender is installed.", FontSize = 11, Foreground = Subtext(), Margin = new Thickness(0, 2, 0, 0) };
        AddRow(grid, ref row, "Blender", _blenderBox, blenderHint, blenderAutoBtn, blenderBrowse);

        // ── Image editor ──────────────────────────────────────────────────────
        _editorBox = BuildEditorBox(input.DetectedEditors);
        var editorBrowse = SmallButton("Browse…");
        editorBrowse.Click += async (_, _) => await BrowseExe("Locate an image editor", "Programs", new[] { "*.exe" },
            p => SelectCustomEditor(p));
        AddRow(grid, ref row, "Image editor", _editorBox, null, editorBrowse);

        // ── Projects folder ───────────────────────────────────────────────────
        // Blank = the default library. This is where the app's own mod PROJECTS live — not the game's.
        _libraryBox = new TextBox { Text = _libraryRoot ?? "", Watermark = $"Default · {_defaultLibrary}", VerticalAlignment = VerticalAlignment.Center };
        var libDefault = SmallButton("Default");
        libDefault.Click += (_, _) => _libraryBox.Text = "";   // blank → default library
        var libBrowse = SmallButton("Browse…");
        libBrowse.Click += async (_, _) => { var p = await PickFolder("Choose the projects folder"); if (p is not null) _libraryBox.Text = p; };
        AddRow(grid, ref row, "Projects folder", _libraryBox, null, libDefault, libBrowse);

        // ── 3DMigoto loader ───────────────────────────────────────────────────
        // Never detected and never defaulted: a wrong guess writes a mod folder into someone else's
        // install. Blank means Install and Launch stay off. The exe is the one path asked for — its name
        // varies by distribution — and the Mods folder beside it is derived.
        _migotoBox = new TextBox
        {
            Text = _migotoLoaderExe ?? "",
            Watermark = "Not set. Browse to the loader exe beside the Mods folder",
            VerticalAlignment = VerticalAlignment.Center,
        };
        var migotoClear = SmallButton("Clear");
        migotoClear.Click += (_, _) => _migotoBox.Text = "";
        var migotoBrowse = SmallButton("Browse…");
        migotoBrowse.Click += async (_, _) => await BrowseExe("Locate the 3DMigoto loader",
            "Programs", new[] { "*.exe" }, p => _migotoBox.Text = p);
        var migotoHint = new TextBlock { Text = "3DMigoto Loader.exe, or SSMT's per-game Run.exe. Install drops built mods in the Mods folder beside it.", FontSize = 11, Foreground = Subtext(), Margin = new Thickness(0, 2, 0, 0) };
        AddRow(grid, ref row, "3DMigoto loader", _migotoBox, migotoHint, migotoClear, migotoBrowse);

        // ── Author ────────────────────────────────────────────────────────────
        _authorBox = new TextBox { Text = input.Author, Watermark = "handle", Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        var authorHint = new TextBlock { Text = "Default for new mods. Existing mods keep their own.", FontSize = 11, Foreground = Subtext(), Margin = new Thickness(0, 2, 0, 0) };
        AddRow(grid, ref row, "Author", _authorBox, authorHint);
        _encoderCpuLimit = input.EncoderCpuLimit;

        // ── Recent mods ───────────────────────────────────────────────────────
        // The list is cleared by Save, not by the click — Cancel drops the whole form, this row with it — so
        // the label reports what is PENDING rather than claiming it already happened.
        _clearRecentsButton = SmallButton(RecentLabel(input.RecentCount));
        _clearRecentsButton.IsEnabled = input.RecentCount > 0;
        _clearRecentsButton.Click += (_, _) => { _clearRecents = true; _clearRecentsButton.IsEnabled = false; _clearRecentsButton.Content = RecentsPendingLabel; };
        AddRow(grid, ref row, "Recent mods", _clearRecentsButton);

        var save = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(16, 6) };
        save.Classes.Add("primary");
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 6) };
        save.Click += (_, _) => { if (ValidateGameOnSave()) Close(Collect()); };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                grid,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, save },
                },
            },
        };
    }

    /// <summary>A non-empty path must resolve to a real GF2 install — this guards a manual paste. Empty is
    /// allowed; the main window then prompts. On success the box is normalised to the resolved root.</summary>
    private bool ValidateGameOnSave()
    {
        var typed = _gameBox.Text?.Trim() ?? "";
        if (typed.Length == 0) { _gamePath = null; return true; }
        var (resolved, _) = GameLocator.ValidateDetailed(typed);   // accepts the game root (or its bundle dir); returns the game root
        if (resolved is null) { ShowGameStatus(WrongFolderHint, ok: false); return false; }
        _gamePath = resolved;          // resolved is the game root; normalise the field to it
        _gameBox.Text = resolved;
        return true;
    }

    /// <summary>Show modally over <paramref name="owner"/>; resolves to the edited settings or null.</summary>
    public static Task<SettingsResult?> Show(Window owner, SettingsInput input) =>
        new SettingsWindow(input).ShowDialog<SettingsResult?>(owner);

    private SettingsResult Collect() => new()
    {
        GamePath = _gamePath,             // the resolved game root (set in ValidateGameOnSave); blank → prompt
        BlenderPath = _blenderBox.Text,   // blank → ApplySettings Empty2Nulls it to auto-detect
        ImageEditorPath = (_editorBox.SelectedItem as EditorChoice)?.Path,
        LibraryRoot = _libraryBox.Text,   // blank → ApplySettings Empty2Nulls it to the default library
        MigotoLoaderExe = _migotoBox.Text,    // blank → ApplySettings Empty2Nulls it back to unset
        Author = _authorBox.Text?.Trim() ?? "",
        ClearRecents = _clearRecents,
        EncoderCpuLimit = _encoderCpuLimit,   // not managed here; the stored value passes through
    };

    // ── editor dropdown ───────────────────────────────────────────────────────

    private ComboBox BuildEditorBox(IReadOnlyList<string> detected)
    {
        _editorChoices.Add(new EditorChoice("OS default", null));
        foreach (var exe in detected)
            _editorChoices.Add(new EditorChoice(ImageEditorLocator.FriendlyName(exe), exe));
        // a custom override that isn't a detected install gets its own row
        if (_imageEditorPath is not null && !detected.Any(e => string.Equals(e, _imageEditorPath, StringComparison.OrdinalIgnoreCase)))
            _editorChoices.Add(new EditorChoice($"Custom · {ImageEditorLocator.FriendlyName(_imageEditorPath)}", _imageEditorPath));

        var box = new ComboBox { ItemsSource = _editorChoices, Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        box.SelectedItem = _editorChoices.FirstOrDefault(c =>
            string.Equals(c.Path, _imageEditorPath, StringComparison.OrdinalIgnoreCase)) ?? _editorChoices[0];
        return box;
    }

    private void SelectCustomEditor(string exe)
    {
        var existing = _editorChoices.FirstOrDefault(c => string.Equals(c.Path, exe, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new EditorChoice($"Custom · {ImageEditorLocator.FriendlyName(exe)}", exe);
            _editorChoices.Add(existing);
        }
        _editorBox.SelectedItem = existing;
    }

    // ── game folder actions ───────────────────────────────────────────────────

    private void RedetectGame()
    {
        if (GameLocator.Find() is { } found)
        {
            _gameBox.Text = found;   // the game root
            ShowGameStatus("Found.", ok: true);
        }
        else ShowGameStatus("No game found.", ok: false);
    }

    private async Task BrowseGame()
    {
        var picked = await PickFolder("Locate the GIRLS' FRONTLINE 2 EXILIUM install folder");
        if (picked is null) return;
        var (resolved, _) = GameLocator.ValidateDetailed(picked);
        if (resolved is not null)
        {
            _gameBox.Text = resolved;   // the game root
            ShowGameStatus("Found.", ok: true);
        }
        else ShowGameStatus(WrongFolderHint, ok: false);
    }

    private void ShowGameStatus(string text, bool ok)
    {
        _gameStatus.Text = text;
        _gameStatus.Foreground = ok ? Subtext() : (ResBrush("HudErrorBrush") ?? Brushes.IndianRed);
        _gameStatus.IsVisible = true;
    }

    // ── pickers ───────────────────────────────────────────────────────────────

    private async Task<string?> PickFolder(string title)
    {
        var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return res.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task BrowseExe(string title, string filterName, string[] patterns, Action<string> onPicked)
    {
        var res = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns } },
        });
        if (res.FirstOrDefault()?.TryGetLocalPath() is { } p) onPicked(p);
    }

    // ── display helpers ───────────────────────────────────────────────────────

    private static string RecentLabel(int n) => n > 0 ? $"Clear recent mods ({n})" : "No recent mods";

    /// <summary>What the row reads once the clear is armed: it lands with the rest of the form.</summary>
    internal const string RecentsPendingLabel = "Recents clear on Save";

    // ── layout helpers ────────────────────────────────────────────────────────

    private static IBrush Subtext() => ResBrush("HudSubtextBrush") ?? Brushes.Gray;

    /// <summary>The theme's body text — the same brush the first-run gate paints its copy with, so a settings
    /// row and the rules it sits behind read as one app.</summary>
    private static IBrush BodyText() => ResBrush("HudTextBrush") ?? Brushes.White;

    private static IBrush? ResBrush(string key) =>
        Application.Current?.TryFindResource(key, out var r) == true && r is IBrush b ? b : null;

    private static Button SmallButton(string content) => new()
    {
        Content = content,
        Padding = new Thickness(10, 4),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The label in column 0; the value control, an optional sub-status line and right-aligned
    /// action buttons in column 1.</summary>
    private static void AddRow(Grid grid, ref int row, string label, Control value, Control? sub = null, params Button[] actions)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var lbl = new TextBlock { Text = label, FontWeight = FontWeight.SemiBold, Foreground = BodyText(), Margin = new Thickness(0, 8, 16, 8), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(lbl, row);
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var line = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        if (actions.Length > 0)
        {
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var a in actions) btns.Children.Add(a);
            DockPanel.SetDock(btns, Dock.Right);
            line.Children.Add(btns);
        }
        line.Children.Add(value);   // fills the remaining space

        Control content = sub is null
            ? line
            : new StackPanel { Children = { line, sub } };

        Grid.SetRow(content, row);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        row++;
    }
}
