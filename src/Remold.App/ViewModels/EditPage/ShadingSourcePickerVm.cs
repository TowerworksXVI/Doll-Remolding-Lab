using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Remold.Core;

namespace Remold.App.ViewModels.EditPage;

/// <summary>One material another material's shading can be copied from: where it is, and its name.</summary>
public sealed record ShadingSourceRow(string PartLabel, string MaterialName, object Tag)
{
    public string Haystack => (PartLabel + " " + MaterialName).ToLowerInvariant();

    public override string ToString() => $"{PartLabel} \u00b7 {MaterialName}";
}

/// <summary>One cache-only reading of the source list: every row available now, and whether every subject
/// has supplied its model. Reading means another snapshot can add rows; Unreadable is settled but cannot
/// truthfully be called an empty mod.</summary>
public sealed record ShadingSourceLoad(IReadOnlyList<ShadingSourceRow> Rows, EditSubjectRead Read,
    long CacheVersion = 0);

/// <summary>The searchable rows shown by the shading-source chooser.</summary>
public sealed partial class ShadingSourcePickerVm : ObservableObject
{
    public ObservableCollection<ShadingSourceRow> Rows { get; } = new();

    public ObservableCollection<ShadingSourceRow> Visible { get; } = new();
    private readonly HashSet<string> _sourceKeys = new(System.StringComparer.Ordinal);

    [ObservableProperty] private string _filter = "";

    [ObservableProperty] private ShadingSourceRow? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(StateLine))]
    private EditSubjectRead _read = EditSubjectRead.Reading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLine))]
    private string? _failure;

    public bool IsLoading => Read == EditSubjectRead.Reading;

    /// <summary>The fixed state slot's answer. A cold subject outranks both filtering and emptiness; only an
    /// answered snapshot of every subject may call the mod empty.</summary>
    public string StateLine => Failure is { Length: > 0 } failure ? failure
        : Read == EditSubjectRead.Reading ? GameFilesGate.SubjectReading
        : Read == EditSubjectRead.Unreadable ? GameFilesGate.SubjectUnreadable
        : Rows.Count == 0 ? NoRowsLine
        : MainWindowViewModel.NoMatchLine(Filter, Visible.Count);

    public const string NoRowsLine = "No other materials in this mod.";

    /// <summary>Poll the cache-only producer while any subject is still warming. Each snapshot is folded into
    /// the rows already on screen, so a warm subject is usable immediately and a later one never replaces it.</summary>
    public async Task LoadAsync(Func<CancellationToken, Task<ShadingSourceLoad>> load,
        CancellationToken gone, Func<CancellationToken, Task>? wait = null)
        => await LoadCoreAsync(load, gone, wait is null ? null : ((_, token) => wait(token)));

    public async Task LoadAsync(Func<CancellationToken, Task<ShadingSourceLoad>> load,
        CancellationToken gone, Func<long, CancellationToken, Task> waitForWarm)
        => await LoadCoreAsync(load, gone, waitForWarm);

    private async Task LoadCoreAsync(Func<CancellationToken, Task<ShadingSourceLoad>> load,
        CancellationToken gone, Func<long, CancellationToken, Task>? waitForWarm)
    {
        while (true)
        {
            var snapshot = await load(gone);
            gone.ThrowIfCancellationRequested();
            AddRows(snapshot.Rows);
            Read = snapshot.Read;
            if (!IsLoading) return;
            if (waitForWarm is null) return;
            await waitForWarm(snapshot.CacheVersion, gone);
        }
    }

    public void SetRows(IEnumerable<ShadingSourceRow> rows)
    {
        var desired = rows.ToArray();
        Reconcile(Rows, desired);
        _sourceKeys.Clear();
        foreach (var row in desired) _sourceKeys.Add(SourceKey(row));
        ApplyFilter();
    }

    private void AddRows(IEnumerable<ShadingSourceRow> rows)
    {
        bool changed = false;
        foreach (var row in rows)
        {
            if (!_sourceKeys.Add(SourceKey(row))) continue;
            Rows.Add(row);
            changed = true;
        }
        if (changed) ApplyFilter();
        else OnPropertyChanged(nameof(StateLine));
    }

    private static string SourceKey(ShadingSourceRow row)
    {
        if (row.Tag is (Remold.Core.Project.TargetPart part, int index,
                Remold.Core.Project.GameAssetRef))
            return $"{part.Subject}\u001f{part.Outfit}\u001f{part.RendererSlot}\u001f{index}".ToUpperInvariant();
        return row.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\u001f" + row;
    }

    partial void OnFilterChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(StateLine));
    }

    private void ApplyFilter()
    {
        var desired = Rows.Where(row => PickerTextFilter.Matches(Filter, row.Haystack)).ToArray();
        Reconcile(Visible, desired);
        if (Selected is not null && !Visible.Contains(Selected)) Selected = Visible.FirstOrDefault();
        OnPropertyChanged(nameof(StateLine));
    }

    private static void Reconcile(ObservableCollection<ShadingSourceRow> collection,
        IReadOnlyList<ShadingSourceRow> desired)
    {
        for (int i = collection.Count - 1; i >= 0; i--)
            if (!desired.Contains(collection[i])) collection.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < collection.Count && ReferenceEquals(collection[i], desired[i])) continue;
            int existing = collection.IndexOf(desired[i]);
            if (existing >= 0) collection.Move(existing, i); else collection.Insert(i, desired[i]);
        }
    }
}
