using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Remold.App.ViewModels.EditPage;

/// <summary>The word matching shared by the edit-page pick lists.</summary>
internal static class PickerTextFilter
{
    public static bool Matches(string filter, string lowerHaystack)
    {
        var terms = filter.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return terms.All(lowerHaystack.Contains);
    }
}

/// <summary>
/// Which toon ramp a pick lands on, in the three forms one can arrive in: the game's own (the opt-out, which
/// records a pick of nothing and ships nothing), a game texture in a bundle, or a <c>.dds</c> the modder
/// handed over.
/// </summary>
/// <param name="PathId">Which texture of <paramref name="Bundle"/> — the selector. <paramref name="Texture"/>
/// is its name, which a ramp library repeats across every ramp it holds, so the name alone would ship the
/// library's first ramp whatever the modder picked.</param>
public sealed record RampChoice(string? Bundle, string? Texture, string? File, long PathId = 0)
{
    /// <summary>Keep whatever ramp the game already binds. Not a file and not an absence: on a replaced
    /// submesh it is the recorded answer the conversion never overrules.</summary>
    public static readonly RampChoice KeepOwn = new(null, null, null);

    public bool IsKeepOwn => Bundle is null && File is null;

    /// <summary>How to read the picked game texture out of its bundle.</summary>
    public Remold.Core.Bundles.BundleReader.TextureRef Ref =>
        PathId != 0 ? Remold.Core.Bundles.BundleReader.TextureRef.ByPathId(PathId) : Texture ?? "";
}

/// <summary>One row of the pick list: a distinct shading curve, whatever it is named or wherever it was
/// read. Rows are grouped by the ramps' whole stored contents, so two materials binding the same values
/// offer one choice rather than two that differ in nothing.</summary>
public sealed partial class RampPickRowVm : ObservableObject
{
    public required RampChoice Choice { get; init; }

    /// <summary>What this row IS: the first place it is bound — part · material, with the character and
    /// outfit in front for another subject's — or the file it came from. NOT the texture's asset name: the
    /// game names every ramp texture alike, so a list titled by asset name says one thing N times.</summary>
    public required string Title { get; init; }

    /// <summary>The line under the title: how many more places the fold reaches ("and N more"), or how the
    /// ramp arrived where no place names it (carried, already in this mod, imported). Empty when the title
    /// says everything.</summary>
    public string Source { get; init; } = "";

    /// <summary>Every place the fold reaches, one a line, for the hover. Null when the visible lines
    /// already say everything — Avalonia shows no tooltip for a null tip.</summary>
    public string? SourcesTip { get; init; }

    /// <summary>The whole row on hover: the fold's every place when there is one, else the title and
    /// source lines themselves — both trim in the list's own column, so the full text lives here.</summary>
    public string RowTip => SourcesTip ?? (Source.Length > 0 ? Title + "\n" + Source : Title);

    /// <summary>The extent, in the size line's own form.</summary>
    public string Dimensions { get; init; } = "";

    /// <summary>This row is the ramp the material binds with nothing picked. Pinned first, and picking it is
    /// what takes a pick back off.</summary>
    public bool IsOwn { get; init; }

    /// <summary>This row is what the slot binds RIGHT NOW. Preselected, so opening the list to look and
    /// pressing Enter changes nothing, and marked, so the list says which one that is.</summary>
    public bool IsBound { get; init; }

    /// <summary>The mark the bound row carries beside its source.</summary>
    public string BoundNote => IsBound ? BoundLabel : "";
    public bool HasBoundNote => IsBound;

    /// <summary>What the bound row is marked with.</summary>
    public const string BoundLabel = "· in use";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumb))]
    private Bitmap? _thumbnail;

    [ObservableProperty] private bool _isThumbLoading = true;

    public bool HasThumb => Thumbnail is not null;

    public bool HasSource => Source.Length > 0;

    /// <summary>The row's own filter text, matched against the box — every source, so a fold's other
    /// places are findable, not only the one the title leads with.</summary>
    public string Haystack =>
        (Title + " " + Source + " " + (SourcesTip?.Replace('\n', ' ') ?? "")).ToLowerInvariant();

    /// <summary>Land the decoded strip (or the absence of one) and stop the shimmer. Hands the row back so
    /// a builder can settle it in one expression.</summary>
    public RampPickRowVm Settled(Bitmap? bmp)
    {
        Thumbnail = bmp;
        IsThumbLoading = false;
        return this;
    }
}

/// <summary>What the read hands the dialog: the rows, and the one line worth saying about what it could
/// NOT read — candidates dropped, an outfit that wouldn't resolve. Null when nothing was dropped, so a
/// short list can be believed.</summary>
public sealed record RampPickLoad(IReadOnlyList<RampPickRowVm> Rows, string? Note = null);

/// <summary>
/// The pick surface for one material's toon-ramp slot: the ramps already in play, one row per distinct
/// curve, with the material's own pinned first.
///
/// <para>The window owns the file dialogs and nothing else — every read, group and refusal is decided here,
/// so the same answers hold with no window at all.</para>
/// </summary>
public sealed partial class RampPickerVm : ObservableObject
{
    private readonly Func<CancellationToken, Task<RampPickLoad>> _load;
    private readonly Func<CancellationToken, IAsyncEnumerable<RampPickLoad>>? _stream;
    private readonly Func<string, string?> _refuseImport;
    private readonly CancellationTokenSource _cts = new();

    /// <param name="load">gathers the rows, reading each candidate's stored values off the game. Runs once,
    /// off the UI thread, and the list stands for the life of the dialog.</param>
    /// <param name="refuseImport">why a chosen <c>.dds</c> is not a toon ramp, or null when it is.</param>
    public RampPickerVm(Func<CancellationToken, Task<RampPickLoad>> load,
        Func<string, string?> refuseImport, string materialLabel)
    {
        _load = load;
        _refuseImport = refuseImport;
        MaterialLabel = materialLabel;
    }

    public RampPickerVm(Func<CancellationToken, IAsyncEnumerable<RampPickLoad>> stream,
        Func<string, string?> refuseImport, string materialLabel)
    {
        _load = _ => Task.FromResult(new RampPickLoad(Array.Empty<RampPickRowVm>()));
        _stream = stream;
        _refuseImport = refuseImport;
        MaterialLabel = materialLabel;
    }

    /// <summary>Which material's slot is being picked for, as the header states it.</summary>
    public string MaterialLabel { get; }

    /// <summary>Set by the window: choose a <c>.dds</c> to import, or null when the modder cancelled.</summary>
    public Func<Task<string?>>? ChooseImportFile { get; set; }

    /// <summary>Set by the window: choose where to write the shown ramp, or null when cancelled.</summary>
    public Func<string, Task<string?>>? ChooseExportPath { get; set; }

    /// <summary>Set by the window: copy the shown ramp's exact bytes to the chosen path.</summary>
    public Func<RampChoice, string, Task<string?>>? ExportTo { get; set; }

    /// <summary>Set by the window: close it, true when a pick was applied.</summary>
    public Action<bool>? Close { get; set; }

    public ObservableCollection<RampPickRowVm> Rows { get; } = new();

    /// <summary>The rows the filter admits, in list order.</summary>
    public ObservableCollection<RampPickRowVm> Visible { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRows))]
    private bool _isLoading = true;

    /// <summary>The read is done and nothing came back — and nothing was DROPPED either, or the note is
    /// the line that explains the emptiness and this one would misread it as a mod with no ramps.</summary>
    public bool HasNoRows => !IsLoading && Rows.Count == 0 && LoadNote is null;

    /// <summary>What an empty list says. Import is still on the footer, so the line reports the state
    /// rather than sending the modder anywhere.</summary>
    public const string NoRowsLine = "No toon ramps were found.";

    [ObservableProperty] private string _filter = "";

    /// <summary>What the read could not reach: dropped candidates, an outfit that wouldn't resolve, or the
    /// whole read failing. Null when everything listed — the line's absence is what lets a short list be
    /// believed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadNote))]
    [NotifyPropertyChangedFor(nameof(HasNoRows))]
    private string? _loadNote;

    public bool HasLoadNote => LoadNote is not null;

    /// <summary>What the whole read failing says. Distinct from a clean empty list, which reports a mod
    /// with no ramps rather than a read that never landed.</summary>
    public const string LoadFailedNote = "Couldn't read the toon ramps from this install.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private RampPickRowVm? _selected;

    /// <summary>A refusal from the last import, shown until the next one. Null when nothing was refused.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefusal))]
    private string? _refusal;

    public bool HasRefusal => Refusal is not null;

    /// <summary>What the last export did, shown until the next one. A write that landed said nothing at all,
    /// which reads as a dialog that ignored the click.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string? _note;

    public bool HasNote => Note is not null;

    /// <summary>The line one landed export reports. Names the file — the modder chose where it went, so the
    /// folder is not news.</summary>
    internal static string ExportedLine(string path) =>
        $"✓ Exported {System.IO.Path.GetFileName(path)}";

    public bool CanApply => Selected is not null;

    /// <summary>Export writes the SHOWN ramp's exact bytes, so it needs a row that names one. The material's
    /// own row names the game's, which is a file the modder may well want.</summary>
    public bool CanExport => Selected is not null;

    /// <summary>What the dialog resolved to, or null when it was cancelled.</summary>
    public RampChoice? Result { get; private set; }

    /// <summary>Start the read. Called by the window when it opens; safe to call once.</summary>
    public async Task LoadAsync()
    {
        if (_stream is not null)
        {
            try
            {
                await foreach (var update in _stream(_cts.Token).WithCancellation(_cts.Token))
                {
                    if (_disposed) { DisposeRows(update.Rows); return; }
                    ApplyLoad(update);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { LoadNote = LoadFailedNote; }
            IsLoading = false;
            OnPropertyChanged(nameof(HasNoRows));
            Selected ??= Rows.FirstOrDefault(r => r.IsBound)
                ?? Rows.FirstOrDefault(r => r.IsOwn) ?? Rows.FirstOrDefault();
            return;
        }
        RampPickLoad load;
        try { load = await _load(_cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { load = new RampPickLoad(Array.Empty<RampPickRowVm>(), LoadFailedNote); }
        var rows = load.Rows;
        // The window can have closed while the read ran, and the read decodes a strip per row: the rows land
        // on nothing, and their native bitmaps would be leaked by the Dispose that already ran.
        if (_disposed)
        {
            foreach (var r in rows) { r.Thumbnail?.Dispose(); r.Thumbnail = null; }
            return;
        }
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        LoadNote = load.Note;
        IsLoading = false;
        OnPropertyChanged(nameof(HasNoRows));
        ApplyFilter();
        // What the slot binds NOW, so opening the list to look and pressing Enter — Apply is the default
        // button — re-applies what is already there instead of silently discarding a pick.
        Selected ??= Rows.FirstOrDefault(r => r.IsBound)
            ?? Rows.FirstOrDefault(r => r.IsOwn) ?? Rows.FirstOrDefault();
    }

    private void ApplyLoad(RampPickLoad load)
    {
        var selectedChoice = Selected?.Choice;
        var desired = load.Rows;
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            var held = Rows[i];
            if (desired.Any(row => row.Choice == held.Choice)) continue;
            Rows.RemoveAt(i);
            held.Thumbnail?.Dispose();
            held.Thumbnail = null;
        }
        for (int i = 0; i < desired.Count; i++)
        {
            var row = desired[i];
            int existing = Rows.ToList().FindIndex(held => held.Choice == row.Choice);
            if (existing >= 0)
            {
                var old = Rows[existing];
                Rows.RemoveAt(existing);
                old.Thumbnail?.Dispose();
                old.Thumbnail = null;
            }
            Rows.Insert(Math.Min(i, Rows.Count), row);
        }
        LoadNote = load.Note;
        ApplyFilter();
        Selected = selectedChoice is null ? null : Rows.FirstOrDefault(row => row.Choice == selectedChoice);
        Selected ??= Rows.FirstOrDefault(row => row.IsBound)
            ?? Rows.FirstOrDefault(row => row.IsOwn) ?? Rows.FirstOrDefault();
    }

    private static void DisposeRows(IEnumerable<RampPickRowVm> rows)
    {
        foreach (var row in rows) { row.Thumbnail?.Dispose(); row.Thumbnail = null; }
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Visible.Clear();
        foreach (var row in Rows)
            // The material's own row is never filtered away: it is the way back to the game's ramp, and a
            // typed word must not take it off the list.
            if (row.IsOwn || PickerTextFilter.Matches(Filter, row.Haystack)) Visible.Add(row);
        if (Selected is not null && !Visible.Contains(Selected)) Selected = Visible.FirstOrDefault();
    }

    /// <summary>Take a <c>.dds</c> from disk as a row of its own, refusing anything that is not a ramp by
    /// what it actually is. An accepted file is selected at once — it is the only reason to have opened the
    /// dialog this way.</summary>
    [RelayCommand]
    private async Task Import()
    {
        if (ChooseImportFile is not { } choose) return;
        var path = await choose();
        if (path is null) return;
        if (_refuseImport(path) is { } refusal) { Refusal = refusal; return; }
        Refusal = null;
        var row = new RampPickRowVm
        {
            Choice = new RampChoice(null, null, path),
            Title = System.IO.Path.GetFileName(path),
            Source = ImportedSource,
            Dimensions = $"{Core.Migoto.RampConversion.RampWidth}×{Core.Migoto.RampConversion.RampHeight}",
        };
        row.Settled(SafePreview(path));
        Rows.Add(row);
        ApplyFilter();
        Selected = row;
    }

    /// <summary>What an imported row says it came from. Not a subject and not a bundle: a file the modder
    /// chose.</summary>
    public const string ImportedSource = "imported file";

    private static Bitmap? SafePreview(string path)
    {
        try
        {
            var read = Textures.RampImage.ReadDds(path);
            return Textures.RampImage.TryPreview(read.Width, read.Height, read.Fp16);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>Write the selected ramp's exact bytes out. Nothing re-encodes: the file that lands is the
    /// one a build would ship.</summary>
    [RelayCommand]
    private async Task Export()
    {
        if (Selected is not { } row || ChooseExportPath is not { } choose || ExportTo is not { } write) return;
        var suggested = row.Title.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
            ? row.Title : row.Title + ".dds";
        var path = await choose(suggested);
        if (path is null) return;   // cancelled: nothing was written, so nothing is said
        var failure = await write(row.Choice, path);
        Refusal = failure;
        Note = failure is null ? ExportedLine(path) : null;
    }

    [RelayCommand]
    private void Apply()
    {
        if (Selected is not { } row) return;
        Result = row.Choice;
        Close?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts.Cancel();
        Close?.Invoke(false);
    }

    /// <summary>Set once the window has gone, so a read still in flight lands on nothing and releases what
    /// it decoded rather than leaking it.</summary>
    private bool _disposed;

    /// <summary>Stop the read when the window goes, whichever way it went.</summary>
    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        foreach (var r in Rows) { r.Thumbnail?.Dispose(); r.Thumbnail = null; }
    }
}
