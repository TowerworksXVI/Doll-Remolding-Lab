using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Remold.App.ViewModels;
using Remold.Core;
using Remold.Core.Migoto;
using Remold.Core.Text;
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
    /// <summary>The value a NEW mod's "Editable by others" starts on.</summary>
    public bool IncludeRepairData { get; init; } = true;
    public int RecentCount { get; init; }
    /// <summary>A force-rescan sweep is already owed — armed on an earlier Save and not yet run, here or in
    /// a previous session. The row OPENS armed on it, so the form shows what is actually pending rather than
    /// offering to arm a request that already stands.</summary>
    public bool ForceRescanOwed { get; init; }
    public int? EncoderCpuLimit { get; init; }             // null = all cores
    public string? Language { get; init; }                 // null = English
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
    /// <summary>The value a NEW mod's "Editable by others" starts on. Existing mods are never touched by
    /// it — each carries its own serialized choice.</summary>
    public bool IncludeRepairData { get; set; } = true;
    public bool ClearRecents { get; set; }
    /// <summary>Sweep the rebuilt caches and re-read the game. Like <see cref="ClearRecents"/> it is ARMED
    /// on the form and lands with the Save; the sweep itself waits for the rescan queue.
    /// <para>Unlike <see cref="ClearRecents"/> this is the row's whole STATE, not a one-way request: the row
    /// opens on <see cref="SettingsInput.ForceRescanOwed"/> and its button toggles, so false from a form that
    /// opened armed means the modder took the request back.</para></summary>
    public bool ForceRescan { get; set; }
    /// <summary>What the row OPENED on — <see cref="SettingsInput.ForceRescanOwed"/>, handed straight back.
    /// With <see cref="ForceRescan"/> it says whether this Save armed the request or merely carried one that
    /// already stood, which is the difference between a Save that owes a re-read of the install and one that
    /// leaves the standing debt for the next rescan to honour.</summary>
    public bool ForceRescanWasOwed { get; set; }
    public int? EncoderCpuLimit { get; set; }              // null = all cores
    /// <summary>The UI language tag, or null for English. Applies the next time the app starts.</summary>
    public string? Language { get; set; }
}

/// <summary>One item in the Language row's list: what it shows and the tag a Save stores for it, null for
/// English.</summary>
internal sealed record LanguageChoice(string Display, string? Tag)
{
    public override string ToString() => Display;
}

/// <summary>The Language row's list and what it opens on, kept off the window so it can be exercised
/// without a UI runtime.</summary>
internal static class LanguageRow
{
    /// <summary>The offered languages in menu order, English storing null. A stored tag opens on the listed
    /// language it selects at startup (<see cref="Catalog.Resolve"/>): <c>zh-CN</c>, <c>zh-SG</c> and any
    /// case of <c>zh-Hans</c> open on Simplified Chinese, and <c>en</c> in any case on English. Any other
    /// tag, such as the pseudolocale or a tag from a newer build, gets one more item showing the raw tag, and
    /// the row opens on it.</summary>
    internal static (IReadOnlyList<LanguageChoice> Choices, LanguageChoice Selected) Choices(string? stored)
    {
        var choices = Catalog.Languages
            .Select(language => new LanguageChoice(language.DisplayName,
                language.Tag == Catalog.English ? null : language.Tag))
            .ToList();
        if (string.IsNullOrEmpty(stored)) return (choices, choices[0]);
        string resolved = Catalog.Resolve(stored);
        var listed = resolved == Catalog.English
            ? (string.Equals(stored, Catalog.English, StringComparison.OrdinalIgnoreCase) ? choices[0] : null)
            : choices.FirstOrDefault(choice => choice.Tag == resolved);
        if (listed is not null) return (choices, listed);
        var raw = new LanguageChoice(stored, stored);
        choices.Add(raw);
        return (choices, raw);
    }

    /// <summary>What a Save stores: the tag of the item the row shows. A listed item writes its own tag, so a
    /// stored <c>zh-CN</c> the row opened on Simplified Chinese is written back as <c>zh-Hans</c>; the raw
    /// item carries the stored tag exactly as it was.</summary>
    internal static string? Stored(LanguageChoice? picked, LanguageChoice opened) => (picked ?? opened).Tag;
}

/// <summary>What one validated row shows at rest: the glyph beside its box, and the words that hang off it.
/// An empty glyph is a row with nothing to show — no mark, nothing to hover.</summary>
internal readonly record struct RowReading(string Glyph, string? Tooltip)
{
    internal static RowReading Nothing => new("", null);
}

/// <summary>The rules the Settings form applies to typed values — on Save, and on every reading the open form
/// takes of a row. Kept off the window so they can be exercised without a UI runtime.</summary>
internal static class SettingsValidation
{
    /// <summary>What the CPU-limit box means. Blank is all cores; a positive whole number is that many.
    /// Anything else refuses, and <paramref name="reason"/> is what the row's glyph says on hover.</summary>
    internal static bool CpuLimit(string? typed, out int? value, out string? reason)
    {
        value = null;
        reason = null;
        var text = typed?.Trim() ?? "";
        if (text.Length == 0) return true;
        if (int.TryParse(text, out int n) && n > 0) { value = n; return true; }
        reason = Strings.SettingsCpuNotANumber;
        return false;
    }


    /// <summary>Which rows can hold the form shut. Each of the three names a value the app goes on to act on
    /// with nothing to fall back to: a game path that resolves to no install, a projects folder that refuses
    /// the mods it will be asked to create, a CPU limit that is not a number.
    ///
    /// <para>The 3DMigoto row is deliberately NOT among them. Its verdict is advisory: the loader is
    /// optional, so a form held shut over it holds every unrelated edit on the form with it — and a path a
    /// released build already saved would leave the dialog unsaveable for good. The diagnosis also stands
    /// on the status bar's 3DMigoto cell.</para></summary>
    internal static bool SaveCommits(bool gamePathOk, bool projectsFolderOk, bool cpuLimitOk) =>
        gamePathOk && projectsFolderOk && cpuLimitOk;

    /// <summary>What a refused Save reads on the form-level line: it names the glyph the rows holding it shut
    /// are wearing, so the sentence and the rows point at each other. Built from
    /// <see cref="GlyphBlocking"/> itself — the line can't come to name a glyph the rows don't show.</summary>
    internal static string SaveRefused => Strings.SettingsSaveRefused(GlyphBlocking);

    /// <summary>The form-level line after a Save attempt, or null when there is nothing for it to say. A Save
    /// the rows let through closes the form, so the only thing left to report is the refusal — and reporting
    /// it is the whole difference between a held Save and a click that did nothing.</summary>
    internal static string? SaveStatusLine(bool gamePathOk, bool projectsFolderOk, bool cpuLimitOk) =>
        SaveCommits(gamePathOk, projectsFolderOk, cpuLimitOk) ? null : SaveRefused;

    // The glyphs a row's verdict shows beside its box — the app's severity vocabulary, the same three the
    // status bar and the Build footer paint.
    internal const string GlyphOk = "✓";
    internal const string GlyphBlocking = "⛔";
    internal const string GlyphAdvisory = "⚠";

    /// <summary>Which glyph a row shows for the verdict it just read. <paramref name="blocking"/> is whether
    /// a bad verdict on this row holds the form shut — the three rows <see cref="SaveCommits"/> names — so a
    /// stop glyph appears only where Save actually stops, and the advisory loader row reads as a caution.
    /// The verdict's own words go on the tooltip; this is the whole of what the row shows at rest.</summary>
    internal static string RowGlyph(bool ok, bool blocking) =>
        ok ? GlyphOk : blocking ? GlyphBlocking : GlyphAdvisory;

    /// <summary>A whole reading for a verdict a row's own rule read: the glyph from <see cref="RowGlyph"/>,
    /// the verdict's words on the tooltip. Everything the form shows for a value it JUDGED comes through
    /// here, so the severity is picked in one place whoever asked for the reading.</summary>
    internal static RowReading Reading(bool ok, bool blocking, string text) => new(RowGlyph(ok, blocking), text);


    // ── what an empty box says ────────────────────────────────────────────────────────────────────────
    // The form answers "what is this set to" for every row at once, so a blank box is a reading of its own
    // rather than an empty slot. Each is deliberate: three name the fallback the app will actually use, and
    // the loader — the one setting with no fallback and no default — shows nothing, because it is optional
    // and the Launch command is where an unset loader is worth saying something about.


    /// <summary>Blank game folder. A caution, not a stop: the form saves with it empty and the main screen
    /// asks for the folder, so this reports an unfinished setting rather than a refused one.</summary>
    internal static RowReading BlankGameRow() => new(GlyphAdvisory, Strings.SettingsGameNotSet);

    /// <summary>Blank projects folder. An all-clear naming the library that stands in, so the row answers
    /// where New Mod will put things rather than leaving the modder to know the default.</summary>
    internal static RowReading BlankProjectsRow(string defaultLibrary) =>
        new(GlyphOk, Strings.SettingsProjectsDefault(defaultLibrary));

    /// <summary>Blank CPU limit. An all-clear naming the number of cores it comes to.</summary>
    internal static RowReading BlankCpuRow(int processorCount) =>
        new(GlyphOk, Strings.SettingsCpuEveryCore(processorCount));

    /// <summary>Blank 3DMigoto loader: nothing to show. The loader is optional; Launch is the only live
    /// action that wants one, and it says so itself.</summary>
    internal static RowReading BlankLoaderRow() => RowReading.Nothing;

    /// <summary>The CPU row's whole reading, blank included. A cap that parses says so on the glyph rather
    /// than going quiet: on a form that answers for every row, a row saying nothing reads as a row nobody
    /// looked at.</summary>
    internal static RowReading CpuRow(string? typed, int processorCount) =>
        CpuLimit(typed, out var value, out var reason)
            ? value is { } n
                ? new RowReading(GlyphOk, Catalog.Plural(n, Strings.SettingsCpuCapped_One(n), Strings.SettingsCpuCapped_Other(n)))
                : BlankCpuRow(processorCount)
            : Reading(ok: false, blocking: true, reason!);




    internal static string LoaderNoHook => Strings.LoaderNoTextureHook + " " + Strings.SettingsLoaderStillSaveable;

    /// <summary>Whether the 3DMigoto box names a loader a built mod would actually fire on, or the plain
    /// reason it doesn't. Blank is allowed and always passes — the loader is optional, and Launch is the
    /// only live action that wants one. Each failure answers for itself: an exe that isn't
    /// there says nothing about its configuration, and a host with no ini says nothing about its hook.
    /// <para>ADVISORY: what this answers reaches the row's own glyph and nothing else — see
    /// <see cref="SaveCommits"/>.</para>
    /// <para>Reads the host's ini tree, so callers run it off the UI thread — the same shape as the
    /// projects-folder rule beside it.</para></summary>
    internal static string? MigotoLoaderExe(string? typed)
    {
        var text = typed?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (!File.Exists(text)) return Strings.SettingsLoaderNotThere;
        var ini = MigotoIni.Read(text);
        if (!ini.Found) return Strings.SettingsLoaderNoIni;
        if (!ini.HasTextureHook) return LoaderNoHook;
        return null;
    }

    /// <summary>The verdict on a typed projects folder, with the folders proving it cost.</summary>
    /// <param name="Created">What the check had to create, deepest first. The caller owns taking them back
    /// if the value never lands — see <see cref="RemoveCreatedFolders"/>.</param>
    internal readonly record struct ProjectsFolderCheck(bool Ok, string? Reason, IReadOnlyList<string> Created);


    /// <summary>Whether the projects-folder box names somewhere mods can actually be created. Blank is the
    /// default library and always passes. A named folder is created if it isn't there yet and then probed
    /// with a throwaway file: a path can exist, or even be created, and still refuse a write, and the value
    /// is otherwise not read again until the next New Mod. Touches the file system — a network path can hold
    /// for a share timeout — so callers run it off the UI thread.
    /// <para>A refusal leaves nothing behind: whatever this created goes back before it returns.</para>
    /// <para><paramref name="mutate"/> false is the reading the OPEN FORM takes, which owes the disk nothing:
    /// it creates no folder, so a row's glyph costs the modder no directory tree while they type. See
    /// <see cref="LiveProjectsFolder"/> for what it can and can't prove without one.</para></summary>
    internal static ProjectsFolderCheck ProjectsFolder(string? typed, bool mutate = true)
    {
        var text = typed?.Trim() ?? "";
        if (text.Length == 0) return new ProjectsFolderCheck(true, null, Array.Empty<string>());
        if (!mutate) return LiveProjectsFolder(text);
        IReadOnlyList<string> created = Array.Empty<string>();
        string? probe = null;
        try
        {
            created = MissingAncestors(text);
            Directory.CreateDirectory(text);
            probe = Path.Combine(text, "." + Guid.NewGuid().ToString("N") + ".probe");
            File.WriteAllBytes(probe, Array.Empty<byte>());
        }
        catch
        {
            DeleteProbe(probe);
            RemoveCreatedFolders(created);
            return new ProjectsFolderCheck(false, Strings.SettingsNotWritableFolder, Array.Empty<string>());
        }
        DeleteProbe(probe);
        return new ProjectsFolderCheck(true, null, created);
    }

    /// <summary>The same verdict without the create. A folder that is there is probed exactly as
    /// <see cref="ProjectsFolder"/> probes it — a throwaway file, gone before this returns — and a folder that
    /// isn't there is answered for by where it would go: a create needs an existing folder somewhere above it
    /// to land on, so a path whose missing levels run out first (a drive that isn't there) is refused in the
    /// same words Save refuses it in.
    /// <para>The reason is filled in for a PASS too, and the two passes say different things: a folder that
    /// took the probe, and a folder that doesn't exist yet.</para>
    /// <para>What this can't prove is the write into a folder nobody has made yet. Save creates it and probes
    /// it for real, and is where a path that takes the create and refuses the file is caught.</para></summary>
    private static ProjectsFolderCheck LiveProjectsFolder(string text)
    {
        try
        {
            if (!Directory.Exists(text))
                return CanBeCreated(text)
                    ? new ProjectsFolderCheck(true, Strings.SettingsWillBeCreated, Array.Empty<string>())
                    : new ProjectsFolderCheck(false, Strings.SettingsNotWritableFolder, Array.Empty<string>());
            var probe = Path.Combine(text, "." + Guid.NewGuid().ToString("N") + ".probe");
            try { File.WriteAllBytes(probe, Array.Empty<byte>()); }
            catch { return new ProjectsFolderCheck(false, Strings.SettingsNotWritableFolder, Array.Empty<string>()); }
            finally { DeleteProbe(probe); }
            return new ProjectsFolderCheck(true, Strings.SettingsWritableFolder, Array.Empty<string>());
        }
        catch
        {
            // a path no file system takes: the shape fails before anything is read
            return new ProjectsFolderCheck(false, Strings.SettingsNotWritableFolder, Array.Empty<string>());
        }
    }

    /// <summary>Whether a create of <paramref name="path"/> has somewhere to land: every level that isn't
    /// there is walked up to the first that is. A path that runs out of levels first sits on a root that
    /// doesn't exist, a level a FILE already stands on takes no folder, and a level named with a character
    /// no file system takes is a folder that can't be made. Each is a create this rule would otherwise
    /// promise and Save would then refuse.</summary>
    private static bool CanBeCreated(string path)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(path)); d is not null; d = d.Parent)
        {
            if (d.Exists) return true;
            if (File.Exists(d.FullName)) return false;
            if (d.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        }
        return false;
    }

    /// <summary>Take back folders a check created, deepest first and only while empty. A save that doesn't
    /// commit leaves the disk as it found it, and anything that arrived in one of them meanwhile stays.</summary>
    internal static void RemoveCreatedFolders(IReadOnlyList<string> created)
    {
        foreach (var dir in created)
        {
            try
            {
                if (!Directory.Exists(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
                Directory.Delete(dir, recursive: false);
            }
            catch { return; }   // a folder that won't go takes its parents with it: they are no longer empty
        }
    }

    /// <summary>The folders a create of <paramref name="path"/> has to make, deepest first. Read BEFORE the
    /// create, which is the only moment they can be told from folders that were already there.</summary>
    private static List<string> MissingAncestors(string path)
    {
        var missing = new List<string>();
        for (var d = new DirectoryInfo(Path.GetFullPath(path)); d is not null && !d.Exists; d = d.Parent)
            missing.Add(d.FullName);
        return missing;
    }

    private static void DeleteProbe(string? probe)
    {
        if (probe is null) return;
        try { File.Delete(probe); } catch { /* best-effort probe cleanup */ }
    }
}

/// <summary>Which of a row's readings is allowed to land. A rule that reads the disk finishes whenever it
/// finishes, and the box it answered for may hold different text by then — typing fast on the loader row
/// starts a tree-walk per pause, and the slow one must not overwrite the fast one.
/// <para>Every request takes the next id; a result applies only while nothing has been asked of the row
/// since. The counter is interlocked, so a result completing off the UI thread still reads the id that was
/// issued last.</para></summary>
internal sealed class RowGeneration
{
    private int _latest;

    /// <summary>The id for a new request on this row. Everything issued before it is stale from here.</summary>
    internal int Next() => Interlocked.Increment(ref _latest);

    /// <summary>Whether the result of request <paramref name="id"/> is still the one the row is waiting
    /// for.</summary>
    internal bool Applies(int id) => Volatile.Read(ref _latest) == id;
}

/// <summary>
/// The Settings dialog, code-built so it needs no XAML and inherits the app theme. Returns the edited
/// <see cref="SettingsResult"/>, or null if cancelled.
/// </summary>
public sealed class SettingsWindow : Window
{



    // The app's throbber, in the glyph and style class MainWindow's status bar and the Build footer pulse.
    private const string PulseGlyph = "◌";
    private const string PulseClass = "pulse";

    /// <summary>How long an edited row waits before its rule reads the value. Long enough that a path typed
    /// or pasted by hand is read once, when the typing stops, rather than on every keystroke — each reading
    /// of the game, projects and loader rows walks a folder tree.</summary>
    private static readonly TimeSpan EditDebounce = TimeSpan.FromMilliseconds(600);

    private string? _gamePath;
    private string? _blenderPath;
    private string? _imageEditorPath;
    private string? _libraryRoot;
    private readonly string? _migotoLoaderExe;
    private bool _clearRecents;
    private bool _forceRescan;
    /// <summary>What the force-rescan row opened on, kept beside the live state the button toggles: the two
    /// together are what tell an arming Save from one that only carried a request already owed.</summary>
    private readonly bool _forceRescanWasOwed;

    private readonly string _defaultLibrary;
    private readonly string? _blenderAuto;

    private readonly TextBox _gameBox;
    private readonly RowVerdict _gameVerdict = new();
    private readonly LiveRow _gameRow;
    private readonly TextBox _blenderBox;
    private readonly TextBox _libraryBox;
    private readonly RowVerdict _libraryVerdict = new();
    private readonly LiveRow _libraryRow;
    private readonly TextBox _migotoBox;
    private readonly RowVerdict _migotoVerdict = new();
    private readonly LiveRow _migotoRow;
    private readonly TextBox _authorBox;
    private readonly CheckBox _repairDataBox;
    private readonly ComboBox _editorBox;
    private readonly ComboBox _languageBox;
    private readonly LanguageChoice _languageOpened;
    private readonly TextBox _cpuBox;
    private readonly RowVerdict _cpuVerdict = new();
    private readonly LiveRow _cpuRow;
    private int? _encoderCpuLimit;
    /// <summary>The folders the projects-folder rule created to prove the path takes a write, deepest first.
    /// They stand while the save is still being decided and go back if it never commits.</summary>
    private IReadOnlyList<string> _libraryCreated = Array.Empty<string>();
    /// <summary>The form is gone — a rule still finishing off-thread has nobody to report to, and whatever it
    /// created is residue.</summary>
    private bool _closed;
    private readonly Button _clearRecentsButton;
    private readonly Button _forceRescanButton;
    private readonly List<EditorChoice> _editorChoices = new();

    private sealed record EditorChoice(string Display, string? Path)
    {
        public override string ToString() => Display;
    }

    internal SettingsWindow(SettingsInput input)
    {
        Title = Strings.SettingsTitle;
        // Wide enough for a row's hint to land on one line beside a label column that sizes to its longest
        // label — the paths themselves are longer than any width, and the boxes scroll.
        Width = 730;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        // The popup background IS the panel colour; the window chrome is the separation.
        Background = ResBrush("HudPanelBrush") ?? Brushes.Transparent;
        Styles.Add(PulseStyle());

        _gamePath = input.GamePath;
        _blenderPath = input.BlenderPath;
        _blenderAuto = input.BlenderAuto;
        _imageEditorPath = input.ImageEditorPath;
        _libraryRoot = input.LibraryRoot;
        _migotoLoaderExe = input.MigotoLoaderExe;
        _defaultLibrary = input.DefaultLibrary;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        int row = 0;

        // ── Language ──────────────────────────────────────────────────────────
        // The first row, so a modder who cannot read the UI language finds the way back where the form
        // starts. Read once at startup, before any window exists, so a Save changes nothing on screen until
        // the next launch; the hint says so.
        var (languages, openedOn) = LanguageRow.Choices(input.Language);
        _languageOpened = openedOn;
        _languageBox = new ComboBox
        {
            ItemsSource = languages,
            SelectedItem = openedOn,
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AddRow(grid, ref row, Strings.SettingsLanguageLabel, _languageBox, Hint(Strings.SettingsLanguageHint));

        // ── Game folder ───────────────────────────────────────────────────────
        // Read when the form opens and again after every edit. The game path is the ROOT (the folder holding
        // GF2_Exilium.exe) — the one canonical value everywhere.
        _gameBox = new TextBox { Text = _gamePath ?? "", Watermark = Strings.SettingsGameWatermark, VerticalAlignment = VerticalAlignment.Center };
        _gameRow = new LiveRow(_gameBox, _gameVerdict, ReadGameRow, offThread: true, EditDebounce, () => _closed);
        var gameRe = SmallButton(Strings.SettingsRedetect);
        gameRe.Click += (_, _) => RedetectGame();
        var gameBrowse = SmallButton(Strings.Browse);
        gameBrowse.Click += async (_, _) => await BrowseGame();
        AddRow(grid, ref row, Strings.SettingsGameLabel, WithVerdict(_gameBox, _gameVerdict), null, gameRe, gameBrowse);

        // ── Blender ───────────────────────────────────────────────────────────
        // Blank = auto-detect; an explicit exe when more than one Blender is installed.
        _blenderBox = new TextBox
        {
            Text = _blenderPath ?? "",
            Watermark = _blenderAuto is { } found
                ? Strings.SettingsBlenderAutoFound(found) : Strings.SettingsBlenderAutoNotFound,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var blenderAutoBtn = SmallButton(Strings.SettingsBlenderAuto);
        blenderAutoBtn.Click += (_, _) => _blenderBox.Text = "";   // blank → auto-detect
        var blenderBrowse = SmallButton(Strings.Browse);
        blenderBrowse.Click += async (_, _) => await BrowseExe(Strings.SettingsLocateBlender, Strings.SettingsBlenderFilter, new[] { "blender.exe" },
            p => _blenderBox.Text = p);
        var blenderHint = Hint(Strings.SettingsBlenderHint);
        AddRow(grid, ref row, Strings.SettingsBlenderLabel, _blenderBox, blenderHint, blenderAutoBtn, blenderBrowse);

        // ── Image editor ──────────────────────────────────────────────────────
        _editorBox = BuildEditorBox(input.DetectedEditors);
        var editorBrowse = SmallButton(Strings.Browse);
        editorBrowse.Click += async (_, _) => await BrowseExe(Strings.SettingsLocateImageEditor, Strings.SettingsProgramsFilter, new[] { "*.exe" },
            p => SelectCustomEditor(p));
        AddRow(grid, ref row, Strings.SettingsImageEditorLabel, _editorBox, null, editorBrowse);

        // ── Projects folder ───────────────────────────────────────────────────
        // Blank = the default library. This is where the app's own mod PROJECTS live — not the game's.
        _libraryBox = new TextBox { Text = _libraryRoot ?? "", Watermark = Strings.SettingsProjectsWatermark(_defaultLibrary), VerticalAlignment = VerticalAlignment.Center };
        _libraryRow = new LiveRow(_libraryBox, _libraryVerdict, ReadLibraryRow, offThread: true, EditDebounce, () => _closed);
        var libDefault = SmallButton(Strings.SettingsProjectsDefaultButton);
        // blank → default library, answered for on the spot rather than waiting out the debounce the edit
        // armed: the three escape buttons on this form (Clear, Default, All cores) name a fallback, and the
        // row says which one.
        libDefault.Click += (_, _) => { _libraryBox.Text = ""; _libraryRow.Evaluate(); };
        var libBrowse = SmallButton(Strings.Browse);
        // A picked folder is answered for on the spot, like the game path's own Browse: the pick is the moment
        // the modder is looking at the row, and a share that refuses writes is worth knowing then, not on Save.
        libBrowse.Click += async (_, _) =>
        {
            var p = await PickFolder(Strings.SettingsChooseProjectsFolder);
            if (p is null) return;
            _libraryBox.Text = p;
            int id = _libraryRow.Begin();   // this answer outranks the re-read the box's own edit armed
            _libraryRow.Pulse();   // the create-and-probe is disk, and a share can hold for its own timeout
            var check = await Task.Run(() => SettingsValidation.ProjectsFolder(p));
            SettingsValidation.RemoveCreatedFolders(check.Created);   // a browse commits nothing
            _libraryRow.Apply(id, SettingsValidation.Reading(check.Ok, blocking: true,
                check.Ok ? Strings.SettingsWritableFolder : check.Reason!));
        };
        var libHint = Hint(Strings.SettingsProjectsHint);
        AddRow(grid, ref row, Strings.SettingsProjectsLabel, WithVerdict(_libraryBox, _libraryVerdict),
            libHint, libDefault, libBrowse);

        // ── 3DMigoto loader ───────────────────────────────────────────────────
        // Never detected and never defaulted: a wrong guess writes a mod folder into someone else's
        // install. Blank means Launch stays off. The exe is the one path asked for — its name
        // varies by distribution — and the Mods folder beside it is derived.
        _migotoBox = new TextBox
        {
            Text = _migotoLoaderExe ?? "",
            Watermark = Strings.SettingsLoaderWatermark,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _migotoRow = new LiveRow(_migotoBox, _migotoVerdict, ReadMigotoRow, offThread: true, EditDebounce, () => _closed);
        var migotoClear = SmallButton(Strings.SettingsLoaderClear);
        // blank → unset, which this row shows as nothing at all: the loader is optional, and Launch is where
        // an unset one is worth a word.
        migotoClear.Click += (_, _) => { _migotoBox.Text = ""; _migotoRow.Evaluate(); };
        var migotoBrowse = SmallButton(Strings.Browse);
        // A picked exe is answered for on the spot, like the game and projects rows: the pick is the moment
        // the modder is looking at this row, and "this 3DMigoto can't fire a mod" is worth knowing then
        // rather than after a build. The ini read is disk, so it waits off-thread.
        migotoBrowse.Click += async (_, _) =>
        {
            if (await PickExe(Strings.SettingsLocateLoader, Strings.SettingsProgramsFilter, new[] { "*.exe" }) is not { } p) return;
            _migotoBox.Text = p;
            int id = _migotoRow.Begin();   // this answer outranks the re-read the box's own edit armed
            _migotoRow.Pulse();   // the ini tree is disk, and a loader on a share can hold for its timeout
            var why = await Task.Run(() => SettingsValidation.MigotoLoaderExe(p));
            _migotoRow.Apply(id, SettingsValidation.Reading(why is null, blocking: false,
                why ?? Strings.SettingsLoaderReady));
        };
        var migotoHint = Hint(Strings.SettingsLoaderHint);
        AddRow(grid, ref row, Strings.SettingsLoaderLabel, WithVerdict(_migotoBox, _migotoVerdict),
            migotoHint, migotoClear, migotoBrowse);

        // ── Author ────────────────────────────────────────────────────────────
        _authorBox = new TextBox { Text = input.Author, Watermark = Strings.SettingsAuthorWatermark, Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
        var authorHint = Hint(Strings.SettingsAuthorHint);
        AddRow(grid, ref row, Strings.SettingsAuthorLabel, _authorBox, authorHint);

        // ── Repair data ───────────────────────────────────────────────────────
        // Seeds new mods only. Each project carries its own serialized choice after creation.
        _repairDataBox = new CheckBox
        {
            Content = Strings.SettingsEditableCheck,
            IsChecked = input.IncludeRepairData,
        };
        var repairHint = Hint(Strings.SettingsEditableHint);
        AddRow(grid, ref row, Strings.SettingsEditableLabel, _repairDataBox, repairHint);

        // ── CPU limit ─────────────────────────────────────────────────────────
        // Blank = every logical processor. The one value behind all of the app's wide parallel work.
        _encoderCpuLimit = input.EncoderCpuLimit;
        _cpuBox = new TextBox
        {
            Text = _encoderCpuLimit?.ToString() ?? "",
            // the fallback named with the number it comes to, like the Blender and library rows name theirs
            Watermark = Strings.SettingsCpuWatermark(Environment.ProcessorCount),
            Width = 130,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        // The rule here is a parse, not a folder tree, so the row answers on the keystroke with no debounce
        // to wait out.
        _cpuRow = new LiveRow(_cpuBox, _cpuVerdict, ReadCpuRow, offThread: false, TimeSpan.Zero, () => _closed);
        var cpuAll = SmallButton(Strings.SettingsCpuAllCores);
        // blank → every logical processor, which the row names — the same escape as the loader's Clear and the
        // projects folder's Default.
        cpuAll.Click += (_, _) => _cpuBox.Text = "";
        var cpuHint = Hint(Strings.SettingsCpuHint);
        // The box is a fixed 130 rather than the line's width, so its glyph rides beside the box itself
        // instead of docking to the far right of the line, where it would read as the button's.
        var cpuLine = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { _cpuBox, _cpuVerdict.Glyph },
        };
        AddRow(grid, ref row, Strings.SettingsCpuLabel, cpuLine, cpuHint, cpuAll);

        // ── one-shot actions ──────────────────────────────────────────────────
        // Everything above is a value that STAYS; the two rows below are single actions that fire once with
        // the Save and are not settings at all. The break is what keeps them from reading as one more
        // preference sitting in the list.
        AddSectionBreak(grid, ref row);

        // ── Recent mods ───────────────────────────────────────────────────────
        // The list is cleared by Save, not by the click — Cancel drops the whole form, this row with it — so
        // the label reports what is PENDING rather than claiming it already happened.
        _clearRecentsButton = SmallButton(RecentLabel(input.RecentCount));
        _clearRecentsButton.IsEnabled = input.RecentCount > 0;
        _clearRecentsButton.Click += (_, _) => { _clearRecents = true; _clearRecentsButton.IsEnabled = false; _clearRecentsButton.Content = Strings.SettingsRecentsPending; };
        AddRow(grid, ref row, Strings.SettingsRecentsLabel, _clearRecentsButton);

        // ── Force rescan ──────────────────────────────────────────────────────
        // Armed like the Recents row above: the click only ARMS, Save fires it, and Cancel drops the form
        // with the arming in it. The sweep itself never runs here — the view-model queues it behind whatever
        // is holding the roster.
        //
        // Two things this row does that the Recents row doesn't. It OPENS on the state the app is in, so a
        // sweep still owed from an earlier Save (this session or a previous one) shows as armed rather than
        // as an offer to arm what already stands. And the arming is REVERSIBLE: the button stays live and a
        // second click takes the request back, which is the only way to undo a sweep owed across a restart —
        // Cancel drops the form, and dropping the form leaves the debt where it was.
        _forceRescan = _forceRescanWasOwed = input.ForceRescanOwed;
        _forceRescanButton = SmallButton(ForceRescanButtonLabel(_forceRescan));
        _forceRescanButton.Click += (_, _) =>
        {
            _forceRescan = !_forceRescan;
            _forceRescanButton.Content = ForceRescanButtonLabel(_forceRescan);
        };
        AddRow(grid, ref row, Strings.SettingsForceRescanLabel, _forceRescanButton, Hint(Strings.SettingsForceRescanHint));

        var save = new Button { Content = Strings.SettingsSave, IsDefault = true, Padding = new Thickness(16, 6) };
        save.Classes.Add("primary");
        var cancel = new Button { Content = Strings.Cancel, IsCancel = true, Padding = new Thickness(16, 6) };
        // Every row's own rule runs, so one Save reports each bad value rather than the first one only. The
        // library and loader rules touch the file system, so Save waits on them off-thread rather than
        // freezing the form for a share timeout — and a save that doesn't land takes back whatever proving
        // it created. Only the rows SaveCommits names can hold the form shut; the loader row reports.
        //
        // A held Save says so on the form-level line: the window simply not closing is the same screen as a
        // click that did nothing, and the marked rows are the only other thing that changed. The line is
        // wiped at the top of every attempt, so nothing from the attempt before it stands over this one.
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            ClearSaveStatus();
            try
            {
                bool game = ValidateGameOnSave();
                bool library = await ValidateLibraryOnSaveAsync();
                await ReportMigotoOnSaveAsync();
                bool cpu = ValidateCpuLimitOnSave();
                if (SettingsValidation.SaveCommits(game, library, cpu) && !_closed) { Close(Collect()); return; }
                SettingsValidation.RemoveCreatedFolders(_libraryCreated);
                _libraryCreated = Array.Empty<string>();
                // A form that went away while a rule was still running has nobody to tell.
                if (!_closed && SettingsValidation.SaveStatusLine(game, library, cpu) is { } refusal)
                    ShowSaveStatus(refusal);
            }
            // An async void handler that lets a throw escape takes the PROCESS down. Whatever a validator
            // hits — a dying share, a permissions wall — the form stays up and says so.
            // Whatever the rule hit is a diagnosis of a disk it was reading, not of a value on this form:
            // the line says the settings didn't land and leaves the rows to name what they refused.
            catch (Exception e)
            {
                AppLog.Write("Couldn't check the settings form's values", e);
                ShowSaveStatus(Strings.SettingsSaveCheckFailed);
            }
            finally { save.IsEnabled = true; }
        };
        cancel.Click += (_, _) =>
        {
            SettingsValidation.RemoveCreatedFolders(_libraryCreated);
            Close(null);
        };
        // Opening the form is a reading of every validated row: the modder sees what each value is worth
        // without touching anything. The four run independently and off the UI thread, so the form is live
        // while they land and a folder on a slow share holds up its own glyph only.
        Opened += (_, _) =>
        {
            _gameRow.Evaluate();
            _libraryRow.Evaluate();
            _migotoRow.Evaluate();
            _cpuRow.Evaluate();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            // a re-read still armed has nobody to report to
            _gameRow.Stop();
            _libraryRow.Stop();
            _migotoRow.Stop();
            _cpuRow.Stop();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                grid,
                _saveStatus,
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

    /// <summary>The form-level line for what a Save did to the FORM: that it was held shut, or that it threw
    /// outside any row's own rule. The rows report their own verdicts on the glyphs beside their boxes, so
    /// what lands here is the part none of them owns — a line of text with no row to sit in.</summary>
    private readonly TextBlock _saveStatus = SaveStatusLineBlock();

    private void ShowSaveStatus(string text)
    {
        _saveStatus.Text = text;
        _saveStatus.IsVisible = true;
    }

    /// <summary>Take the line down. Every Save attempt starts here, so what it goes on to say is about THIS
    /// attempt and a Save that commits leaves nothing behind.</summary>
    private void ClearSaveStatus()
    {
        _saveStatus.Text = "";
        _saveStatus.IsVisible = false;
    }

    /// <summary>A non-empty path must resolve to a real GF2 install — this guards a manual paste. Empty is
    /// allowed; the main window then prompts. On success the box is normalised to the resolved root.
    /// <para>Every outcome writes the row's glyph, refusal and pass alike: a Save another row is holding
    /// leaves this one on screen, and a glyph left over from the paste before it would name a path that has
    /// since been fixed.</para>
    /// <para>A refusal reads out the locator's own diagnosis, which is the sentence the status bar shows for
    /// the same folder — an install that has never been launched is told to launch it, not to go and find the
    /// folder it is already pointing at.</para></summary>
    private bool ValidateGameOnSave()
    {
        var typed = _gameBox.Text?.Trim() ?? "";
        if (typed.Length == 0) { _gamePath = null; _gameRow.Show(SettingsValidation.BlankGameRow()); return true; }
        var (resolved, problem) = GameLocator.ValidateDetailed(typed);   // accepts the game root (or its bundle dir); returns the game root
        if (resolved is null) { ShowGameVerdict(problem ?? Strings.SettingsWrongGameFolder, ok: false); return false; }
        _gamePath = resolved;          // resolved is the game root; normalise the field to it
        _gameBox.Text = resolved;
        // after the box, so this verdict outranks the re-read that writing to it armed
        ShowGameVerdict(Strings.SettingsGameFound, ok: true);
        return true;
    }

    /// <summary>A non-empty projects folder must be one the app can create mods in — the value is otherwise
    /// not read again until New Mod or a rename, and a folder that refuses writes fails there instead. Blank
    /// is allowed; the default library stands in. Runs off the UI thread: creating and probing a folder can
    /// hold for a network share's own timeout.
    /// <para>What proving it created is remembered, not removed — the value may still land. A save that
    /// doesn't commit, and a form closed instead, take them back.</para></summary>
    private async Task<bool> ValidateLibraryOnSaveAsync()
    {
        var typed = _libraryBox.Text;
        // Blank is the default library: nothing to read, so nothing to wait on and no throbber to flash.
        bool named = !string.IsNullOrWhiteSpace(typed);
        int id = _libraryRow.Begin();
        if (named) _libraryRow.Pulse();
        var check = await Task.Run(() => SettingsValidation.ProjectsFolder(typed));
        if (_closed) { SettingsValidation.RemoveCreatedFolders(check.Created); return false; }
        _libraryCreated = check.Created;
        // A named folder that took the probe says so; a blank box names the library standing in for it.
        _libraryRow.Apply(id, !check.Ok
            ? SettingsValidation.Reading(ok: false, blocking: true, check.Reason!)
            : named ? SettingsValidation.Reading(ok: true, blocking: true, Strings.SettingsWritableFolder)
                : SettingsValidation.BlankProjectsRow(_defaultLibrary));
        return check.Ok;
    }

    /// <summary>Put the 3DMigoto row's own reading on its glyph: whether the path names a loader a
    /// built mod would fire on — the file, its ini, and the texture hook in that ini's tree. Blank is
    /// allowed and says nothing; the loader is optional, and Launch is the only live action that
    /// wants one.
    ///
    /// <para>REPORTS ONLY. A bad reading here never holds the Save (see
    /// <see cref="SettingsValidation.SaveCommits"/>), so it reads as a caution rather than a stop, and the
    /// glyph is what a Save another row is holding shows — a save that goes through leaves the same
    /// diagnosis standing on the status bar's 3DMigoto cell.</para>
    ///
    /// <para>Runs off the UI thread: the ini tree is a couple of dozen file reads, and a loader on a network
    /// share can hold for that share's own timeout.</para></summary>
    private async Task ReportMigotoOnSaveAsync()
    {
        // Read the box HERE: controls are UI-thread-only, and a read inside the Task.Run lambda runs on
        // the pool.
        var typed = _migotoBox.Text;
        // Blank leaves Launch off: nothing to read, so nothing to wait on and no throbber to flash.
        bool named = !string.IsNullOrWhiteSpace(typed);
        int id = _migotoRow.Begin();
        if (named) _migotoRow.Pulse();
        // The same reading the open form takes of this row, so a Save and a pause in the typing land the
        // same words on the same host.
        var reading = await Task.Run(() => ReadMigotoRow(typed));
        _migotoRow.Apply(id, reading);
    }

    /// <summary>Blank is all cores; anything else must be a positive whole number. On success the parsed
    /// value is what <see cref="Collect"/> hands back, and the row shows the cap that value comes to.</summary>
    private bool ValidateCpuLimitOnSave()
    {
        bool ok = SettingsValidation.CpuLimit(_cpuBox.Text, out var value, out _);
        _cpuRow.Show(ReadCpuRow(_cpuBox.Text));
        if (!ok) return false;
        _encoderCpuLimit = value;
        return true;
    }

    // ── what each validated row reads off its own box ─────────────────────────
    // One reading per row, taken when the form opens, after every edit, and — where the rule is the same one
    // Save runs — by Save itself. The game, projects and loader rules walk the disk, so these run on the
    // thread pool: each takes the text it judges as an argument and touches no control.

    /// <summary>The game row. Normalises nothing — Browse, Re-detect and Save own writing the resolved root
    /// back into the box; this only reports on what is in it.</summary>
    private RowReading ReadGameRow(string? typed)
    {
        var text = typed?.Trim() ?? "";
        if (text.Length == 0) return SettingsValidation.BlankGameRow();
        var (resolved, problem) = GameLocator.ValidateDetailed(text);
        return SettingsValidation.Reading(resolved is not null, blocking: true,
            resolved is not null ? Strings.SettingsGameFound : problem ?? Strings.SettingsWrongGameFolder);
    }

    /// <summary>The projects row, in the mode that creates nothing: a folder tree is the modder's to make,
    /// not something a glance at the form leaves behind. Save is the pass that creates and probes for
    /// real.</summary>
    private RowReading ReadLibraryRow(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return SettingsValidation.BlankProjectsRow(_defaultLibrary);
        var check = SettingsValidation.ProjectsFolder(typed, mutate: false);
        return SettingsValidation.Reading(check.Ok, blocking: true, check.Reason!);
    }

    /// <summary>The 3DMigoto row. Advisory throughout: a host a mod would not fire on never holds the Save,
    /// so its refusal reads as a caution.</summary>
    private static RowReading ReadMigotoRow(string? typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return SettingsValidation.BlankLoaderRow();
        var why = SettingsValidation.MigotoLoaderExe(typed);
        return SettingsValidation.Reading(why is null, blocking: false, why ?? Strings.SettingsLoaderReady);
    }

    /// <summary>The CPU row: a parse, cheap enough to run on the keystroke.</summary>
    private static RowReading ReadCpuRow(string? typed) =>
        SettingsValidation.CpuRow(typed, Environment.ProcessorCount);

    /// <summary>Show modally over <paramref name="owner"/>; resolves to the edited settings or null.</summary>
    public static Task<SettingsResult?> Show(Window owner, SettingsInput input) =>
        new SettingsWindow(input).ShowDialog<SettingsResult?>(owner);

    internal SettingsResult Collect() => new()
    {
        GamePath = _gamePath,             // the resolved game root (set in ValidateGameOnSave); blank → prompt
        BlenderPath = _blenderBox.Text,   // blank → ApplySettings Empty2Nulls it to auto-detect
        ImageEditorPath = (_editorBox.SelectedItem as EditorChoice)?.Path,
        LibraryRoot = _libraryBox.Text,   // blank → ApplySettings Empty2Nulls it to the default library
        MigotoLoaderExe = _migotoBox.Text,    // blank → ApplySettings Empty2Nulls it back to unset
        Author = _authorBox.Text?.Trim() ?? "",
        IncludeRepairData = _repairDataBox.IsChecked == true,
        ClearRecents = _clearRecents,
        ForceRescan = _forceRescan,
        ForceRescanWasOwed = _forceRescanWasOwed,   // the row's opening state, so the Save can tell arming from carrying
        EncoderCpuLimit = _encoderCpuLimit,   // parsed in ValidateCpuLimitOnSave; blank → null
        Language = LanguageRow.Stored(_languageBox.SelectedItem as LanguageChoice, _languageOpened),
    };

    // ── editor dropdown ───────────────────────────────────────────────────────

    private ComboBox BuildEditorBox(IReadOnlyList<string> detected)
    {
        _editorChoices.Add(new EditorChoice(Strings.SettingsEditorOsDefault, null));
        foreach (var exe in detected)
            _editorChoices.Add(new EditorChoice(ImageEditorLocator.FriendlyName(exe), exe));
        // a custom override that isn't a detected install gets its own row
        if (_imageEditorPath is not null && !detected.Any(e => string.Equals(e, _imageEditorPath, StringComparison.OrdinalIgnoreCase)))
            _editorChoices.Add(new EditorChoice(Strings.SettingsEditorCustom(ImageEditorLocator.FriendlyName(_imageEditorPath)), _imageEditorPath));

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
            existing = new EditorChoice(Strings.SettingsEditorCustom(ImageEditorLocator.FriendlyName(exe)), exe);
            _editorChoices.Add(existing);
        }
        _editorBox.SelectedItem = existing;
    }

    // ── game folder actions ───────────────────────────────────────────────────

    /// <summary>Search the usual installs and take the row to what turns up. A miss CAUTIONS rather than
    /// stopping: it read nowhere the modder pointed it and wrote nothing into the box, so the value sitting
    /// there is untouched and may be exactly right — a stop glyph over it would be the row refusing a path it
    /// never looked at.</summary>
    private void RedetectGame()
    {
        if (GameLocator.Find() is { } found)
        {
            _gameBox.Text = found;   // the game root
            ShowGameVerdict(Strings.SettingsGameFound, ok: true);
        }
        // A caution whatever the row's own severity: the search read nowhere the modder pointed it and wrote
        // nothing into the box, so the value still sitting there was never the thing judged.
        else _gameRow.Show(new RowReading(SettingsValidation.GlyphAdvisory, Strings.SettingsNoGameDetected));
    }

    private async Task BrowseGame()
    {
        var picked = await PickFolder(Strings.SettingsLocateGame);
        if (picked is null) return;
        // The locator's own diagnosis, not a generic one: a picked folder that failed for a nameable reason —
        // a game that has never been launched, an incomplete copy — deserves that reason, and it is the same
        // sentence the status bar shows for the same folder.
        var (resolved, problem) = GameLocator.ValidateDetailed(picked);
        if (resolved is not null)
        {
            _gameBox.Text = resolved;   // the game root
            ShowGameVerdict(Strings.SettingsGameFound, ok: true);
        }
        else ShowGameVerdict(problem ?? Strings.SettingsWrongGameFolder, ok: false);
    }

    private void ShowGameVerdict(string text, bool ok) =>
        _gameRow.Show(SettingsValidation.Reading(ok, blocking: true, text));

    // ── pickers ───────────────────────────────────────────────────────────────

    private async Task<string?> PickFolder(string title)
    {
        var res = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return res.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task BrowseExe(string title, string filterName, string[] patterns, Action<string> onPicked)
    {
        if (await PickExe(title, filterName, patterns) is { } p) onPicked(p);
    }

    /// <summary>The picked exe's path, or null when the dialog was dismissed. Split from
    /// <see cref="BrowseExe"/> for a row whose pick is followed by a rule of its own to await.</summary>
    private async Task<string?> PickExe(string title, string filterName, string[] patterns)
    {
        var res = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(filterName) { Patterns = patterns } },
        });
        return res.FirstOrDefault()?.TryGetLocalPath();
    }

    // ── display helpers ───────────────────────────────────────────────────────

    private static string RecentLabel(int n) => n > 0 ? Strings.SettingsClearRecents(n) : Strings.SettingsNoRecents;



    /// <summary>The force-rescan button's word for the state the row is in. One place, so the label the form
    /// opens on and the label a toggle lands on can never disagree.</summary>
    internal static string ForceRescanButtonLabel(bool armed) =>
        armed ? Strings.SettingsForceRescanArmed : Strings.SettingsForceRescanResting;

    // ── layout helpers ────────────────────────────────────────────────────────

    private static IBrush Subtext() => ResBrush("HudSubtextBrush") ?? Brushes.Gray;

    /// <summary>The theme's accent — what the app's throbber is painted, everywhere it pulses.</summary>
    private static IBrush Accent() => ResBrush("HudAccentBrush") ?? Brushes.Peru;

    /// <summary>The theme's body text — the same brush the first-run gate paints its copy with, so a settings
    /// row and the rules it sits behind read as one app.</summary>
    private static IBrush BodyText() => ResBrush("HudTextBrush") ?? Brushes.White;

    private static IBrush? ResBrush(string key) =>
        Application.Current?.TryFindResource(key, out var r) == true && r is IBrush b ? b : null;

    /// <summary>The sub-line under a row's control: what the setting means, always shown.</summary>
    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Subtext(),
        Margin = new Thickness(0, 2, 0, 0),
    };

    /// <summary>The form-level line, hidden until a Save is held shut or throws. Both are the same news — the
    /// settings did not land — so it is painted the error colour from the start.</summary>
    private static TextBlock SaveStatusLineBlock() => new()
    {
        FontSize = 11,
        Foreground = ResBrush("HudErrorBrush") ?? Brushes.IndianRed,
        IsVisible = false,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0),
    };

    /// <summary>A validated row's verdict: the glyph beside its box and the words on its tooltip. It shows
    /// what it is handed — the severity is decided where the reading is built, by
    /// <see cref="SettingsValidation.RowGlyph"/>, so every place that reports on a row — the open form, an
    /// edit, a browse, a re-detect, a Save — shows the same glyph for the same outcome.</summary>
    private sealed class RowVerdict
    {
        /// <summary>The slot in the row's line. Always laid out and sized, so a verdict arriving or leaving
        /// never moves the box beside it; empty means the row has nothing to report.</summary>
        internal TextBlock Glyph { get; } = new()
        {
            Width = 18,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
        };

        /// <summary>Report a reading: the glyph carries the severity, the tooltip carries the words. One
        /// answer carries both, so the colour can't disagree with the glyph. A reading with no glyph is a row
        /// with nothing to report, and empties the slot.</summary>
        internal void Show(RowReading reading) => Paint(reading.Glyph, reading.Tooltip);

        /// <summary>An off-thread rule is reading the value: the app's throbber holds the slot until the
        /// verdict lands. Nothing to hover — there is no verdict yet.</summary>
        internal void Pulse()
        {
            Glyph.Text = PulseGlyph;
            // The verdict colours are set on the control, which outranks a style's setter — so the throbber's
            // accent is set the same way rather than left to the pulse style, which carries only the
            // animation. Otherwise a second wait would pulse in the colour of the verdict before it.
            Glyph.Foreground = Accent();
            if (!Glyph.Classes.Contains(PulseClass)) Glyph.Classes.Add(PulseClass);
            ToolTip.SetTip(Glyph, null);
        }

        /// <summary>The one write to the slot. Every path through it takes the throbber down first, so a
        /// verdict can never land on top of a still-animating dot.</summary>
        private void Paint(string glyph, string? text)
        {
            Glyph.Classes.Remove(PulseClass);
            Glyph.Text = glyph;
            // An empty slot has no colour to pick, and leaving the last one set keeps this off the severity
            // switch, where "" would have to answer as one of the three.
            if (glyph.Length > 0)
                Glyph.Foreground = glyph switch
                {
                    SettingsValidation.GlyphOk => StatusFacet.Ok,
                    SettingsValidation.GlyphBlocking => StatusFacet.Danger,
                    _ => StatusFacet.Caution,
                };
            ToolTip.SetTip(Glyph, text);
        }
    }

    /// <summary>A validated row wired to its own value: the box, the glyph beside it, and the rule that reads
    /// one to write the other. Every answer the row shows goes through here — the reading taken when the form
    /// opens, the one an edit asks for, and the ones a Browse, a Re-detect or a Save decide — so only the
    /// newest request for the row can land on it.
    ///
    /// <para>An edit doesn't clear the glyph, it re-reads: the row answers for whatever is in the box now.
    /// A rule that walks the disk waits out <see cref="EditDebounce"/> first, so a path typed by hand is read
    /// once when the typing stops, and the slot throbs meanwhile.</para></summary>
    private sealed class LiveRow
    {
        private readonly TextBox _box;
        private readonly RowVerdict _verdict;
        private readonly RowGeneration _generation = new();
        private readonly Func<string?, RowReading> _read;
        private readonly bool _offThread;
        private readonly Func<bool> _closed;
        private readonly DispatcherTimer? _debounce;

        /// <param name="read">The row's rule. Runs on the thread pool when <paramref name="offThread"/>, so it
        /// takes the text it judges as an argument and touches no control.</param>
        /// <param name="debounce">How long an edit waits before the rule reads it. Zero reads on the
        /// keystroke — what a row whose rule is a parse can afford.</param>
        /// <param name="closed">Whether the form has gone: a reading still finishing has nobody to tell.</param>
        internal LiveRow(TextBox box, RowVerdict verdict, Func<string?, RowReading> read, bool offThread,
            TimeSpan debounce, Func<bool> closed)
        {
            _box = box;
            _verdict = verdict;
            _read = read;
            _offThread = offThread;
            _closed = closed;
            if (debounce > TimeSpan.Zero)
            {
                _debounce = new DispatcherTimer { Interval = debounce };
                _debounce.Tick += (_, _) => Evaluate();
            }
            box.TextChanged += (_, _) => OnEdit();
        }

        /// <summary>Read the box now and show what the rule makes of it.</summary>
        internal void Evaluate()
        {
            int id = Begin();
            var text = _box.Text;   // controls are UI-thread-only: the value is taken here, judged elsewhere
            if (!_offThread) { Apply(id, Read(text)); return; }
            _verdict.Pulse();
            _ = ReadOffThread(id, text);
        }

        /// <summary>Show an answer the caller decided — a Browse, a Re-detect, a Save. It supersedes whatever
        /// the row had running or armed, so a re-read triggered by the text the caller just wrote into the box
        /// can't land after it with a second opinion.</summary>
        internal void Show(RowReading reading) => Apply(Begin(), reading);

        /// <summary>Claim the row for an answer that isn't ready yet — a Save's own rule, which is awaited.
        /// The id comes back to <see cref="Apply"/> with the result.</summary>
        internal int Begin()
        {
            _debounce?.Stop();
            return _generation.Next();
        }

        internal void Pulse() => _verdict.Pulse();

        /// <summary>Show the result of request <paramref name="id"/>, unless the value moved on or the form
        /// went away while it ran.</summary>
        internal void Apply(int id, RowReading reading)
        {
            if (_closed() || !_generation.Applies(id)) return;
            _verdict.Show(reading);
        }

        /// <summary>The form is gone: a re-read still armed has nothing to report to.</summary>
        internal void Stop() => _debounce?.Stop();

        /// <summary>The value changed. Whatever was in flight answered for text that is no longer in the box,
        /// so it is stale from here; the throbber holds the slot until the new reading lands.</summary>
        private void OnEdit()
        {
            if (_debounce is null) { Evaluate(); return; }
            _generation.Next();
            _verdict.Pulse();
            _debounce.Stop();
            _debounce.Start();
        }

        private async Task ReadOffThread(int id, string? text) => Apply(id, await Task.Run(() => Read(text)));

        /// <summary>The rule, with its own failures caught: a row on the open form is read on every pause in
        /// the typing, over half-typed paths a rule may never have been handed before. What it can't read it
        /// says so about, rather than taking the form down.</summary>
        private RowReading Read(string? text)
        {
            try { return _read(text); }
            catch { return new RowReading(SettingsValidation.GlyphAdvisory, Strings.SettingsCheckFailed); }
        }
    }

    /// <summary>The throbber's animation. A code-built window has no XAML style scope to inherit it from, so
    /// it carries its own copy of MainWindow's <c>TextBlock.pulse</c> — the same reason WorkbenchView does.
    /// The colour is set on the glyph itself (see <see cref="RowVerdict.Pulse"/>), so this is the motion
    /// alone.</summary>
    private static Style PulseStyle()
    {
        var anim = new Animation
        {
            Duration = TimeSpan.FromSeconds(1.2),
            IterationCount = IterationCount.Infinite,
        };
        anim.Children.Add(Frame(0d, 0.25));
        anim.Children.Add(Frame(0.5, 1.0));
        anim.Children.Add(Frame(1d, 0.25));

        var style = new Style(x => x.OfType<TextBlock>().Class(PulseClass));
        style.Animations.Add(anim);
        return style;

        static KeyFrame Frame(double cue, double opacity) =>
            new() { Cue = new Cue(cue), Setters = { new Setter(Visual.OpacityProperty, opacity) } };
    }

    /// <summary>A row's entry control with its verdict glyph beside it: the box takes the line, the glyph
    /// holds its fixed slot between the box and the row's buttons.</summary>
    private static Control WithVerdict(Control value, RowVerdict verdict)
    {
        var panel = new DockPanel();
        DockPanel.SetDock(verdict.Glyph, Dock.Right);
        panel.Children.Add(verdict.Glyph);
        panel.Children.Add(value);   // fills what the glyph leaves
        return panel;
    }

    private static Button SmallButton(string content) => new()
    {
        Content = content,
        Padding = new Thickness(10, 4),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A hairline across the whole form, ending one group of rows and starting the next. The app's
    /// own divider: a <see cref="Border"/> with a one-pixel top edge in <c>HudBorderBrush</c>, which is what
    /// MainWindow rules its panes off with.</summary>
    private static void AddSectionBreak(Grid grid, ref int row)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var rule = new Border
        {
            BorderBrush = ResBrush("HudBorderBrush") ?? Subtext(),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 10, 0, 4),
        };
        Grid.SetRow(rule, row);
        Grid.SetColumn(rule, 0);
        Grid.SetColumnSpan(rule, 2);   // the label column too: this rules off the form, not one row's value
        grid.Children.Add(rule);
        row++;
    }

    /// <summary>The label in column 0; the value control, an optional hint line under it and right-aligned
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
