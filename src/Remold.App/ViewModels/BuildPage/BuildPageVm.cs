using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Project;

namespace Remold.App.ViewModels.BuildPage;

/// <summary>The ③ Build page. Every library row, placement token and key card is a detached reading of the
/// session's edit-first <see cref="AuthoredEditSession.Outline"/>; every authored action goes back through a
/// session verb. Planning is generation-stamped and runs away from the UI thread.</summary>
public sealed partial class BuildPageVm : ObservableObject
{
    private readonly IBuildPageShell _shell;
    private readonly Action<Action> _dispatch;
    private readonly object _changeGate = new();
    private AuthoredEditSession? _session;
    private AuthoredEditOutline? _outline;
    private AuthoredBuildPlan? _plan;
    private long _appliedRevision = -1;
    private int _planGeneration;
    private CancellationTokenSource? _planCancellation;
    private int _previewGeneration;
    private bool _footerHeld;
    private IReadOnlyList<string>? _runWarnings;
    private IReadOnlyList<string>? _runInfos;
    private long? _builtRevision;
    private string? _builtPreviewStamp;
    private bool _runSurfaceCleared;
    private int _presentationGeneration;
    private IReadOnlyList<TargetPart> _compositionTargets = Array.Empty<TargetPart>();
    private IReadOnlyDictionary<string, AuthoredEditOutlineEntry> _compositionEdits =
        new Dictionary<string, AuthoredEditOutlineEntry>(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, PlannedPart> _compositionParts =
        new Dictionary<string, PlannedPart>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CompositionTarget, CompositionCacheEntry> _compositionCache = new();

    public BuildPageVm(IBuildPageShell shell, Action<Action>? dispatch = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dispatch = dispatch ?? (work => work());
        _progress = new ProgressSink(line => _dispatch(() =>
        {
            if (!_runSurfaceCleared)
            {
                _runSurfaceCleared = true;
                ClearBuildResult();
            }
            Footer = BuildFooter.Running(line);
        }));
        Always = new BuildAlwaysVm(this);
    }

    public ObservableCollection<BuildSubjectVm> Subjects { get; } = new();
    public ObservableCollection<BuildEditRowVm> NewGroupChoices { get; } = new();
    public ObservableCollection<BuildGroupVm> Groups { get; } = new();
    public ObservableCollection<BuildIssueVm> Issues { get; } = new();
    public ObservableCollection<BuildIssueVm> WarningRows { get; } = new();
    public ObservableCollection<BuildIssueVm> BlockedRows { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<string> Infos { get; } = new();
    public BuildAlwaysVm Always { get; }

    [ObservableProperty] private BuildEditRowVm? _selectedEdit;
    [ObservableProperty] private BuildMarkedTarget? _markedTarget;
    [ObservableProperty] private string _focusTarget = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isPlanning;
    [ObservableProperty] private bool _isWorkInFlight;
    [ObservableProperty] private BuildFooter _footer = BuildFooter.Idle;
    [ObservableProperty] private string? _buildDisabledReason = BuildGate.NothingAuthored;
    [ObservableProperty] private string? _installDisabledReason = InstallGate.NoBuild;
    [ObservableProperty] private string _lastBuildDir = "";
    [ObservableProperty] private string _lastZipPath = "";
    [ObservableProperty] private string _lastLogPath = "";
    [ObservableProperty] private string _builtPackage = "";
    [ObservableProperty] private string _lastInstallPath = "";
    [ObservableProperty] private bool _buildResultStale;
    [ObservableProperty] private string _wholeModKeyCollisionTip = "";
    [ObservableProperty] private BuildPreviewState _preview = new(null, null, false, "none");
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _previewDecoding;
    [ObservableProperty] private bool _previewUndecodable;

    public bool HasSubjects => Subjects.Count > 0;
    public bool HasGroups => Groups.Count > 0;
    public bool HasWarnings => Warnings.Count > 0;
    public bool HasBlocked => BlockedRows.Count > 0;
    public int BlockedCount => BlockedRows.Count;
    public int WarningCount => Warnings.Count(line => line != BuildWarningSource.LastBuildLead);
    public int InfoCount => Infos.Count;
    public bool HasDiagnostics => BlockedCount + WarningCount + InfoCount > 0;
    public bool HasWarningDiagnosticsOnly => BlockedCount == 0 && WarningCount > 0;
    public BuildIssueVm? PrimaryBlocked => BlockedRows.FirstOrDefault();
    public string PrimaryBlockedMessage => PrimaryBlocked?.Message ?? "";
    public string PrimaryBlockedSummary => PrimaryBlocked?.SummaryMessage ?? "";
    public IReadOnlyList<BuildPlacementChipVm> PrimaryBlockedPlacements =>
        PrimaryBlocked?.Placements.Take(1).ToArray() ?? Array.Empty<BuildPlacementChipVm>();
    public string DiagnosticCounts => string.Join(" · ", new[]
    {
        BlockedCount > 0 ? $"Blocked {BlockedCount}" : "",
        WarningCount > 0 ? $"Warnings {WarningCount}" : "",
        InfoCount > 0 ? $"Info {InfoCount}" : "",
    }.Where(value => value.Length > 0));
    public string BlockedSectionHeader => $"Blocked {BlockedCount}";
    public string WarningSectionHeader => $"Warnings {WarningCount}";
    public string InfoSectionHeader => $"Info {InfoCount}";
    public bool ShowBlockedVerdict => HasBlocked && Footer.State is BuildFooterState.Idle
        or BuildFooterState.Ready or BuildFooterState.Planning or BuildFooterState.Blocked;
    public bool ShowFooterVerdict => !ShowBlockedVerdict;

    /// <summary>The ⚠ over the Edits list means the rows under it: it appears only for a warning some edit
    /// in the list actually wears. A missing preview and a past run's lines mark no row, so they are
    /// reported where they are, in Warnings, and not by a glyph pointing at edits.</summary>
    public bool EditsNeedAttention => !HasBlocked && Issues.Any(issue =>
        !issue.BlocksBuild && issue.EditDefinitionIds.Count > 0);

    public bool HasInfos => Infos.Count > 0;
    public bool HasLastBuild => LastBuildDir.Length > 0;
    public bool HasBuildZip => LastZipPath.Length > 0;
    public bool HasBuildLog => LastLogPath.Length > 0;
    public bool HasFailureLog => HasBuildLog && !HasLastBuild;
    public bool HasLastInstall => LastInstallPath.Length > 0;
    public string FolderTip => LastBuildDir;
    public string InstalledFolderTip => LastInstallPath;
    public string? StatusTip => string.IsNullOrWhiteSpace(Status) ? null : Status;
    public bool CanBuild => !IsWorkInFlight && BuildDisabledReason is null;
    public bool CanInstall => !IsWorkInFlight && InstallDisabledReason is null;

    /// <summary>The board and the library answer nothing while a build runs. A tick or a captured key that
    /// lands mid-run shows a state the mod does not have — the change itself is refused — so the surfaces
    /// that author one are shut instead.</summary>
    public bool BoardEnabled => !IsWorkInFlight;
    public string BuildButtonTip => IsWorkInFlight ? BuildRunningReason : BuildDisabledReason ?? BuildGate.Ready;
    public string InstallButtonTip => IsWorkInFlight ? BuildRunningReason
        : InstallDisabledReason ?? InstallGate.Ready;
    public bool LoaderNeedsAttention => InstallGate.Reason(hasBuild: true, _loader) is not null;
    public string LoaderButtonLabel => string.IsNullOrWhiteSpace(_loader.LoaderExe)
        ? "Set 3DMigoto path…" : "Fix 3DMigoto path…";
    public string LoaderButtonTip => string.IsNullOrWhiteSpace(_loader.LoaderExe)
        ? InstallGate.SetLoader : InstallGate.Reason(hasBuild: true, _loader) ?? InstallGate.SetLoader;
    public bool PreviewEnabled => !IsWorkInFlight && _session?.Snapshot().RootDir is not null;
    public string PreviewPickTip => IsWorkInFlight ? BuildRunningReason
        : _session?.Snapshot().RootDir is null ? PreviewNeedsSave : PreviewPickReady;
    // With no preview set, the box's own placeholder already says so; a caption repeating it six pixels
    // below said it twice, so the caption renders only when it has a file to name.
    public bool HasPreviewTitle => PreviewTitle.Length > 0;
    public string PreviewTitle => Preview.RelativeFile is null ? ""
        : Preview.Missing ? $"{Preview.RelativeFile} missing"
        : PreviewImage is not null && Preview.PixelWidth is > 0 && Preview.PixelHeight is > 0
            ? $"{Preview.RelativeFile} · {Preview.PixelWidth}×{Preview.PixelHeight}"
            : Preview.RelativeFile;
    public bool PreviewMissing => Preview.Missing;
    public bool HasPreview => Preview.HasPreview;
    public bool HasNoPreview => Preview.HasNoPreview;
    public string BuildAgainLine => BuildResultStale ? "Build again to include the latest changes." : "";

    public const string BuildRunningReason = "A build is running.";
    public const string PreviewNeedsSave = "Save the mod before adding a preview image.";
    public const string PreviewPickReady = "Add or replace the image included with this mod.";
    public const string PreviewNotAnImage =
        "That file isn't a supported image type. Choose a PNG, JPG, JPEG, WEBP or BMP file.";
    public const string PreviewOneAtATime = "Drop one preview image at a time.";
    public const string PreviewNoFileInDrop = "That drop contained no image file.";
    public const string PreviewMissingWarning =
        "The preview image is missing. Replace it or remove it before building.";
    public const string PreviewRemoveQuestion = "Remove preview image?";
    public static readonly IReadOnlyList<string> PreviewExtensions =
        new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
    public const int PreviewDecodeWidth = 360;

    private BuildLoaderState _loader = new(null, false, null, default);
    private readonly IProgress<string> _progress;

    private sealed class ProgressSink : IProgress<string>
    {
        private readonly Action<string> _report;
        internal ProgressSink(Action<string> report) => _report = report;
        public void Report(string value) => _report(value);
    }

    private readonly record struct CompositionTarget(string GroupId, string StateId);
    private sealed record CompositionCacheEntry(int Generation, IReadOnlyList<BuildResolvedPartVm> Rows);
    private readonly record struct CompositionOutcome(TargetPart Target, string Answer,
        BuildResolvedPartState State);

    /// <summary>Switch the one session this page reads. A project switch drops every result and bitmap; a
    /// step hop calls <see cref="Enter"/> instead and keeps them.</summary>
    public void Load(AuthoredEditSession? session)
    {
        if (_session is not null) _session.Changed -= OnSessionChanged;
        _session = session;
        _appliedRevision = session?.Revision ?? -1;
        if (_session is not null) _session.Changed += OnSessionChanged;
        Interlocked.Increment(ref _planGeneration);
        CancelPlan(Volatile.Read(ref _planCancellation));
        _plan = null;
        _planning = new BuildPlanningResult();
        _outline = null;
        _runWarnings = null;
        _runInfos = null;
        _builtRevision = null;
        _builtPreviewStamp = null;
        _footerHeld = false;
        MarkedTarget = null;
        LastBuildDir = LastZipPath = LastLogPath = BuiltPackage = LastInstallPath = "";
        BuildResultStale = false;
        RaiseResultProperties();
        ReleasePreview();
        Status = "";
        Rebuild();
        _ = ReplanAsync();
    }

    /// <summary>Entering ③ re-reads every disk gate and starts a fresh generation-stamped plan. A completed
    /// run's footer and result bar survive the hop.</summary>
    public void Enter()
    {
        Rebuild();
        _ = ReplanAsync();
    }

    private void OnSessionChanged(object? sender, AuthoredProjectChangedEventArgs change)
    {
        if (!ReferenceEquals(sender, _session)) return;
        _dispatch(() => ApplySessionChange(sender, change));
    }

    private void ApplySessionChange(object? sender, AuthoredProjectChangedEventArgs change)
    {
        lock (_changeGate)
        {
            if (!ReferenceEquals(sender, _session)) return;
            // What a change made stale belongs to the delivered change, not to revision ordering — so the
            // question is asked ABOVE the revision check, exactly as ② asks it of its pictures. Another
            // subscriber can commit a newer identity-only revision re-entrantly before this callback sees
            // the original, and identity is the one answer that skips the plan: read below the check, a
            // plan-affecting change would be dropped as superseded and the readiness verdict, issue marks
            // and Build gate would all stay on screen derived from a project that has moved.
            bool replan = change.Invalidation.AffectsBuildPlan();
            if (change.Revision <= _appliedRevision)
            {
                // A plan reads the CURRENT snapshot rather than this change's, so re-deriving it on a
                // superseded change's behalf lands on the same answer the newer revision would have asked
                // for. The board is left alone: the newer revision already redrew it.
                if (replan) _ = ReplanAsync();
                return;
            }
            _appliedRevision = change.Revision;
            _shell.ProjectChanged(change.Revision);
            if (!IsWorkInFlight) _footerHeld = false;
            // The board always redraws: a rename moves the labels and tokens on it, and the staleness line is
            // read from the identity the change carried. Only the PLAN is gated — it is
            // derived from nothing the identity form writes, so a description typed one letter at a time must
            // not rerun the whole readiness check behind every pause.
            Rebuild();
            if (replan) _ = ReplanAsync();
        }
    }

    /// <summary>A fresh outline read. The collections are presentation rows only and are never written back.
    /// Serialized on the change gate: a replan continuation and a session-change application can otherwise
    /// interleave under an inline dispatcher and mutate the rows mid-enumeration.</summary>
    public void Rebuild()
    {
        lock (_changeGate) RebuildLocked();
    }

    private void RebuildLocked()
    {
        string? keep = SelectedEdit?.EditDefinitionId;
        _outline = _session?.Outline();
        _loader = _shell.LoaderState();
        ApplyCompositionPresentation(_outline, _plan);
        BuildLibrary(_outline);
        BuildBoard(_outline);
        SelectedEdit = AllEdits().FirstOrDefault(edit => string.Equals(edit.EditDefinitionId, keep,
            StringComparison.Ordinal)) ?? AllEdits().FirstOrDefault();
        RefreshCollisions();
        RefreshPreview();
        RefreshPlanPresentation();
        RefreshGates();
        RefreshStaleness();
        OnPropertyChanged(nameof(HasSubjects));
        OnPropertyChanged(nameof(HasGroups));
    }

    /// <summary>Plan the snapshot read at the start of this generation. A late result from an older revision
    /// or project is dropped.</summary>
    public async Task ReplanAsync()
    {
        int generation = Interlocked.Increment(ref _planGeneration);
        var owner = new CancellationTokenSource();
        var superseded = Interlocked.Exchange(ref _planCancellation, owner);
        CancelPlan(superseded);
        var session = _session;
        var snapshot = session?.Snapshot();
        IsPlanning = true;
        if (!_footerHeld && !IsWorkInFlight) Footer = BuildFooter.Planning;
        BuildPlanningResult result;
        try
        {
            // One dispatcher turn's worth of requests is one plan. A newer request cancels both this
            // debounce and, once running, the current-install work behind the shell.
            await Task.Delay(TimeSpan.FromMilliseconds(15), owner.Token);
            result = await Task.Run(() => _shell.PlanBuild(snapshot, owner.Token), owner.Token);
        }
        catch (OperationCanceledException) when (owner.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        { result = new BuildPlanningResult(Failure: AuthoredRefusal.ForScreen(e, PlanAction)); }
        finally
        {
            Interlocked.CompareExchange(ref _planCancellation, null, owner);
            owner.Dispose();
        }

        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatch(() =>
        {
            try
            {
                lock (_changeGate)
                {
                    if (generation != _planGeneration || !ReferenceEquals(session, _session)) return;
                    _planning = result;
                    _plan = result.Plan;
                    IsPlanning = false;
                    RebuildLocked();
                }
            }
            finally { applied.TrySetResult(); }
        });
        await applied.Task;
    }

    private static void CancelPlan(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private BuildPlanningResult _planning = new();

    /// <summary>The library, RECONCILED rather than rebuilt. Every authored change redraws this page, and a
    /// tick in a row's "Use in…" list is an authored change: rows replaced under an open checklist take the
    /// checklist with them, so a second tick has nothing left to land on. A row the outline still holds
    /// keeps its identity here, and only what actually moved is added, removed or reordered.</summary>
    private void BuildLibrary(AuthoredEditOutline? outline)
    {
        NewGroupChoices.Clear();
        if (outline is null) { Subjects.Clear(); return; }
        var wanted = new List<LibrarySubject>();
        foreach (var subjectRows in outline.Edits.GroupBy(edit =>
                     (edit.Target.Subject.ToUpperInvariant(), edit.Target.Outfit.ToUpperInvariant())))
        {
            var first = subjectRows.First();
            var parts = new List<LibraryPart>();
            foreach (var partRows in subjectRows.GroupBy(edit => edit.Target.RendererSlot,
                         StringComparer.OrdinalIgnoreCase))
            {
                var target = partRows.First().Target;
                string token = _shell.PartToken(target);
                parts.Add(new LibraryPart(partRows.Key.ToUpperInvariant(),
                    token.Length == 0 ? target.RendererSlot : token, target, partRows.ToArray()));
            }
            wanted.Add(new LibrarySubject($"{subjectRows.Key.Item1}\u001f{subjectRows.Key.Item2}",
                _shell.SubjectLabel(first.Target.Subject, first.Target.Outfit), parts));
        }

        Reconcile(Subjects, wanted, want => want.Key, row => row.Key,
            want => new BuildSubjectVm(want.Key, want.Label), (subject, want) =>
        {
            subject.Label = want.Label;
            Reconcile(subject.Parts, want.Parts, wantedPart => wantedPart.Key, row => row.Key,
                wantedPart => new BuildPartVm(wantedPart.Key, wantedPart.Label, wantedPart.Target),
                (part, wantedPart) =>
            {
                part.Label = wantedPart.Label;
                part.Target = wantedPart.Target;
                Reconcile(part.Edits, wantedPart.Edits, edit => edit.Id, row => row.EditDefinitionId,
                    edit => new BuildEditRowVm(this, edit, wantedPart.Label, Summary(edit),
                        PlacementChipLabels(edit, outline), PlacementChoices(edit, outline)),
                    (row, edit) => row.Sync(edit, wantedPart.Label, Summary(edit),
                        PlacementChipLabels(edit, outline), PlacementChoices(edit, outline)));
            });
        });
        foreach (var edit in AllEdits()) NewGroupChoices.Add(edit);

        string Summary(AuthoredEditOutlineEntry edit) =>
            ChangeSummary(edit.Kind, _session?.Slots(edit.Id) ?? Array.Empty<EditSlotState>());
    }

    /// <summary>What the library should hold, read off the outline before any row is touched.</summary>
    private sealed record LibrarySubject(string Key, string Label, IReadOnlyList<LibraryPart> Parts);

    private sealed record LibraryPart(string Key, string Label, TargetPart Target,
        IReadOnlyList<AuthoredEditOutlineEntry> Edits);

    /// <summary>Bring one row list to what the outline wants without replacing the rows it already has.
    /// Rows are matched by their own stable key, moved into place where the order changed, and only the
    /// surplus at the end is dropped.</summary>
    private static void Reconcile<TRow, TWanted>(ObservableCollection<TRow> rows,
        IReadOnlyList<TWanted> wanted, Func<TWanted, string> wantedKey, Func<TRow, string> rowKey,
        Func<TWanted, TRow> create, Action<TRow, TWanted> update)
    {
        for (int index = 0; index < wanted.Count; index++)
        {
            string want = wantedKey(wanted[index]);
            int found = -1;
            for (int scan = index; scan < rows.Count; scan++)
                if (string.Equals(rowKey(rows[scan]), want, StringComparison.Ordinal)) { found = scan; break; }
            if (found < 0) rows.Insert(index, create(wanted[index]));
            else if (found != index) rows.Move(found, index);
            update(rows[index], wanted[index]);
        }
        while (rows.Count > wanted.Count) rows.RemoveAt(rows.Count - 1);
    }

    /// <summary>The library row's dim account of what an edit actually changes: replaced geometry, authored
    /// pictures, picked ramps and shading values. A fresh edit that still inherits everything says nothing,
    /// and a hide's row already says hide. The line stays at counts grain — it never lists which maps or
    /// which values an edit sets.
    ///
    /// <para>A picture is counted by what the asset IS, not by the slot carrying it: a project asset can
    /// also be a RAMP or a structured VALUE (<see cref="ProjectAssetKind"/>), and both bind to non-geometry
    /// slots. A ramp counts only when a ramp ASSET answers a ramp slot — the modder's pick, never a carried
    /// or blanked ramp. A shading value is counted the other way round, by its slot's input, because a
    /// value copied from another material carries no asset at all — counting assets would leave a copy
    /// invisible, and counting every structured value would count a converted project's emissive-mask
    /// answer as one.</para></summary>
    internal static string ChangeSummary(EditDefinitionKind kind, IReadOnlyList<EditSlotState> slots)
    {
        if (kind != EditDefinitionKind.Content) return "";
        bool mesh = slots.Any(state => state.Slot.Input == TargetInputKind.Geometry
            && state.Binding.Kind != BindingKind.TargetGameValue);
        int pictures = slots.Where(state => state.Slot.Input != TargetInputKind.Geometry
                && state.ProjectAsset is { Kind: ProjectAssetKind.Picture })
            .Select(state => state.ProjectAsset!.Id).Distinct(StringComparer.Ordinal).Count();
        int ramps = slots.Where(state => state.Slot.Input == TargetInputKind.Ramp
                && state.ProjectAsset is { Kind: ProjectAssetKind.Ramp })
            .Select(state => state.ProjectAsset!.Id).Distinct(StringComparer.Ordinal).Count();
        int values = slots.Count(state => state.Slot.Input == TargetInputKind.MaterialValue
            && state.Binding.Kind is BindingKind.ProjectAsset or BindingKind.SourceSlot);
        var parts = new List<string>();
        if (mesh) parts.Add("mesh");
        if (pictures > 0) parts.Add(pictures == 1 ? "1 image" : $"{pictures} images");
        if (ramps > 0) parts.Add(ramps == 1 ? "1 ramp" : $"{ramps} ramps");
        if (values > 0) parts.Add(values == 1 ? "1 shading value" : $"{values} shading values");
        return string.Join(" · ", parts);
    }

    private void BuildBoard(AuthoredEditOutline? outline)
    {
        Always.Tokens.Clear();
        Always.AvailableEdits.Clear();
        Groups.Clear();
        if (outline is null) return;
        var edits = _compositionEdits;
        foreach (string editId in outline.Always)
            if (edits.TryGetValue(editId, out var edit)) Always.Tokens.Add(Token(edit, null, null));
        foreach (var edit in outline.Edits.Where(edit => !outline.Always.Contains(edit.Id, StringComparer.Ordinal)))
            Always.AvailableEdits.Add(EditChoice(edit, null, null));
        foreach (var target in outline.KnownParts.Where(target => !outline.Edits.Any(edit =>
                     edit.Kind == EditDefinitionKind.Hide && edit.Target.SameAs(target))))
            Always.AvailableEdits.Add(HideChoice(target, null, null));

        foreach (var group in outline.Groups)
        {
            var card = new BuildGroupVm(this, group);
            for (int index = 0; index < group.States.Count; index++)
            {
                var state = group.States[index];
                var row = new BuildStateVm(this, group, state, index, group.States.Count);
                foreach (string editId in state.ActiveEditIds)
                    if (edits.TryGetValue(editId, out var edit)) row.Tokens.Add(Token(edit, group, state));
                foreach (var edit in outline.Edits.Where(edit =>
                             !state.ActiveEditIds.Contains(edit.Id, StringComparer.Ordinal)))
                    row.AvailableEdits.Add(EditChoice(edit, group.Id, state.Id));
                foreach (var target in outline.KnownParts.Where(target => !outline.Edits.Any(edit =>
                             edit.Kind == EditDefinitionKind.Hide && edit.Target.SameAs(target))))
                    row.AvailableEdits.Add(HideChoice(target, group.Id, state.Id));
                card.States.Add(row);
            }
            Groups.Add(card);
        }
    }

    private BuildEditChoiceVm EditChoice(AuthoredEditOutlineEntry edit, string? groupId, string? stateId) =>
        new(this, edit.Id, edit.Target, edit.Kind, edit.Label, PartName(edit.Target), groupId, stateId);

    private BuildEditChoiceVm HideChoice(TargetPart target, string? groupId, string? stateId) =>
        new(this, "", target, EditDefinitionKind.Hide, "Hide", PartName(target), groupId, stateId);

    private BuildTokenVm Token(AuthoredEditOutlineEntry edit, KeyGroupOutline? group,
        KeyGroupStateOutline? state) => new(this, edit.Id, edit.Target, edit.Kind, edit.Label,
        PartName(edit.Target), group?.Id, state?.Id);

    private string PartName(TargetPart target)
    {
        string token = _shell.PartToken(target);
        return token.Length == 0 ? target.RendererSlot : token;
    }

    private IReadOnlyList<BuildPlacementChoiceVm> PlacementChoices(AuthoredEditOutlineEntry edit,
        AuthoredEditOutline outline)
    {
        var choices = new List<BuildPlacementChoiceVm>
        {
            new(this, edit.Id, edit.Target, edit.Kind, "Always", null, null,
                edit.Placements.Any(placement => placement.IsAlways)),
        };
        foreach (var group in outline.Groups)
            for (int i = 0; i < group.States.Count; i++)
            {
                var state = group.States[i];
                string label = PlacementNames.Place(group, state, i);
                choices.Add(new BuildPlacementChoiceVm(this, edit.Id, edit.Target, edit.Kind, label,
                    group.Id, state.Id, edit.Placements.Any(placement =>
                        string.Equals(placement.KeyGroupId, group.Id, StringComparison.Ordinal)
                        && string.Equals(placement.StateId, state.Id, StringComparison.Ordinal))));
            }
        return choices;
    }

    /// <summary>What an unused edit's one chip says, in ② Edit's own words for the same fact.</summary>
    public const string NotUsedChip = "Not used yet";

    private static IReadOnlyList<string> PlacementChipLabels(AuthoredEditOutlineEntry edit,
        AuthoredEditOutline outline)
    {
        if (edit.Placements.Count == 0) return new[] { NotUsedChip };
        var labels = new List<string>();
        if (edit.Placements.Any(placement => placement.IsAlways)) labels.Add(PlacementNames.Always);
        foreach (var groupRows in edit.Placements.Where(placement => !placement.IsAlways)
                     .GroupBy(placement => placement.KeyGroupId, StringComparer.Ordinal))
        {
            var group = outline.Groups.First(candidate => string.Equals(candidate.Id, groupRows.Key,
                StringComparison.Ordinal));
            var states = groupRows.Select(row => row.StateIndex!.Value + 1).OrderBy(value => value).ToList();
            labels.Add($"{PlacementNames.Group(group)} · {Ranges(states)}");
        }
        return labels;
    }

    private static string Ranges(IReadOnlyList<int> values)
    {
        if (values.Count == 0) return "";
        var ranges = new List<string>();
        int start = values[0], end = start;
        foreach (int value in values.Skip(1))
        {
            if (value == end + 1) { end = value; continue; }
            ranges.Add(start == end ? start.ToString() : $"{start}–{end}");
            start = end = value;
        }
        ranges.Add(start == end ? start.ToString() : $"{start}–{end}");
        return string.Join(", ", ranges);
    }

    /// <summary>Install the exact outline/plan pair this redraw presents. This generation advances only
    /// when inputs reach the presentation; starting a plan that has not returned does not invalidate an
    /// open disclosure backed by the still-applied pair.</summary>
    private void ApplyCompositionPresentation(AuthoredEditOutline? outline, AuthoredBuildPlan? plan)
    {
        _presentationGeneration++;
        _compositionCache.Clear();
        _compositionTargets = outline?.KnownParts.DistinctBy(CompositionPartKey,
                StringComparer.OrdinalIgnoreCase).ToArray()
            ?? Array.Empty<TargetPart>();
        _compositionEdits = outline?.Edits.ToDictionary(edit => edit.Id, StringComparer.Ordinal)
            ?? new Dictionary<string, AuthoredEditOutlineEntry>(StringComparer.Ordinal);
        _compositionParts = plan?.Parts.ToDictionary(part => CompositionPartKey(part.Target),
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, PlannedPart>(StringComparer.OrdinalIgnoreCase);
    }

    internal (int Active, int Hidden) CompositionCounts(string groupId, int stateIndex)
    {
        int active = 0, hidden = 0;
        foreach (var outcome in ResolveComposition(groupId, stateIndex))
        {
            if (outcome.State == BuildResolvedPartState.Active) active++;
            else if (outcome.State == BuildResolvedPartState.Hidden) hidden++;
        }
        return (active, hidden);
    }

    internal void OpenComposition(BuildStateVm state)
    {
        lock (_changeGate)
        {
            var target = new CompositionTarget(state.GroupId, state.Id);
            if (!_compositionCache.TryGetValue(target, out var cached)
                || cached.Generation != _presentationGeneration)
            {
                var rows = ResolveComposition(state.GroupId, state.Index)
                    .Select(outcome => new BuildResolvedPartVm(PartName(outcome.Target), outcome.Answer,
                        outcome.State)).ToArray();
                cached = new CompositionCacheEntry(_presentationGeneration, rows);
                _compositionCache[target] = cached;
            }
            state.Composition.Clear();
            foreach (var row in cached.Rows) state.Composition.Add(row);
        }
    }

    /// <summary>The state-local answer for every relevant known part. A part with no plan remains original;
    /// a planned part belongs here only when Always or this key group controls it. Other groups' operations
    /// cannot be summarized truthfully by this state and are omitted.</summary>
    private IEnumerable<CompositionOutcome> ResolveComposition(string groupId, int stateIndex)
    {
        foreach (var target in _compositionTargets)
        {
            if (!_compositionParts.TryGetValue(CompositionPartKey(target), out var part))
            {
                yield return new CompositionOutcome(target, "original", BuildResolvedPartState.Original);
                continue;
            }

            bool relevant = false;
            PlannedPartOperation? activeEdit = null;
            bool hidden = false;
            foreach (var operation in part.Operations)
            {
                bool operationRelevant = false;
                bool operationActive = false;
                foreach (var condition in operation.ActiveWhen)
                {
                    if (condition.IsAlways)
                    {
                        operationRelevant = true;
                        operationActive = true;
                        break;
                    }
                    if (!string.Equals(condition.GroupId, groupId, StringComparison.Ordinal)) continue;
                    operationRelevant = true;
                    if (condition.StateIndex == stateIndex) operationActive = true;
                }
                relevant |= operationRelevant;
                if (!operationActive) continue;
                if (operation.Disposition == PlannedPartDisposition.Hidden) hidden = true;
                else if (operation.Disposition == PlannedPartDisposition.Edit && activeEdit is null)
                    activeEdit = operation;
            }

            if (!relevant) continue;
            if (hidden)
            {
                yield return new CompositionOutcome(target, "hidden", BuildResolvedPartState.Hidden);
                continue;
            }
            if (activeEdit?.EditDefinitionId is { } editId)
            {
                string answer = _compositionEdits.TryGetValue(editId, out var edit) ? edit.Label : "edited";
                yield return new CompositionOutcome(target, answer, BuildResolvedPartState.Active);
                continue;
            }
            yield return new CompositionOutcome(target, "original", BuildResolvedPartState.Original);
        }
    }

    private static string CompositionPartKey(TargetPart target) =>
        $"{target.Subject}\u001f{target.Outfit}\u001f{target.RendererSlot}";

    private void RefreshPlanPresentation()
    {
        Issues.Clear();
        if (_outline is not null && _plan is not null)
        {
            foreach (var binding in _plan.Bindings.Where(binding => binding.Decision.BlocksBuild
                         || binding.RenderPlan?.BlocksBuild == true))
                AddIssue(BlockingReason(binding.Operation), new[] { binding.EditDefinitionId }, blocked: true);
            foreach (var part in _plan.Parts)
            {
                foreach (var operation in part.Operations.Where(operation =>
                             operation.Operation?.Decision.BlocksBuild == true
                             || operation.Operation?.RenderPlan?.BlocksBuild == true))
                    AddIssue(BlockingReason(operation.Operation!),
                        operation.EditDefinitionId is null ? Array.Empty<string>()
                            : new[] { operation.EditDefinitionId }, blocked: true);
                if (part.Suppression?.Decision.BlocksBuild == true
                    || part.Suppression?.RenderPlan?.BlocksBuild == true)
                    AddIssue(BlockingReason(part.Suppression),
                        part.Operations.Where(operation => operation.Disposition == PlannedPartDisposition.Hidden)
                            .Select(operation => operation.EditDefinitionId!).ToArray(), blocked: true);
                if (part.Lifecycle?.BlocksBuild == true)
                    AddIssue(part.Lifecycle.Reason, part.Operations
                        .Where(operation => operation.EditDefinitionId is not null)
                        .Select(operation => operation.EditDefinitionId!).Distinct(StringComparer.Ordinal).ToArray(),
                        blocked: true);
            }
            foreach (string conflict in _plan.Conflicts)
                AddIssue(conflict, Affected(conflict), blocked: true, AffectedGroups(conflict));
            foreach (string warning in _plan.Warnings)
                AddIssue(warning, Affected(warning), blocked: false);
            if (!_plan.CanBuild && !Issues.Any(issue => issue.BlocksBuild))
                AddIssue(BuildGate.UnnamedPlanBlocker, Array.Empty<string>(), blocked: true);
        }
        ApplyIssueMarks();
        RefreshWarnings();
        RefreshInfos();
    }

    private static string BlockingReason(BuildOperationResolution resolution) =>
        resolution.Decision.BlocksBuild ? resolution.Decision.Reason
            : resolution.RenderPlan?.Reason ?? resolution.Decision.Reason;

    /// <summary>The edits one plan line is about, as the plan itself recorded them. Reading it off the text
    /// is what this replaces: several parts are free to carry edits named alike, and a name as short as
    /// "Hidden" or "Edit 1" is free to appear inside a line about something else, so a text match marks rows
    /// the line was never about. A line the planner could not attribute marks nothing.</summary>
    private IReadOnlyList<string> Affected(string line) =>
        _plan is not null && _plan.IssueEditIds.TryGetValue(line, out var ids)
            ? ids : Array.Empty<string>();

    private IReadOnlyList<string> AffectedGroups(string line) =>
        _plan is not null && _plan.IssueGroupIds.TryGetValue(line, out var ids)
            ? ids : Array.Empty<string>();

    /// <summary>One row per distinct line. A line reached a second time keeps its row and takes the second
    /// owner's edits with it: dropping them leaves tokens the line is genuinely about wearing no mark.</summary>
    private void AddIssue(string reason, IReadOnlyList<string> editIds, bool blocked,
        IReadOnlyList<string>? groupIds = null)
    {
        var owned = editIds.Distinct(StringComparer.Ordinal).ToArray();
        var groups = (groupIds ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
        if (Issues.FirstOrDefault(issue => string.Equals(issue.RawMessage, reason, StringComparison.Ordinal))
            is { } standing)
        {
            var union = standing.EditDefinitionIds.Concat(owned).Distinct(StringComparer.Ordinal).ToArray();
            var groupUnion = standing.GroupIds.Concat(groups).Distinct(StringComparer.Ordinal).ToArray();
            bool blocks = standing.BlocksBuild || blocked;
            standing.Own(Display(reason, union, groupUnion, blocks),
                Summary(reason, union, groupUnion, blocks), union, groupUnion,
                Chips(union, groupUnion), blocks);
            return;
        }
        Issues.Add(new BuildIssueVm(reason, Display(reason, owned, groups, blocked),
            Summary(reason, owned, groups, blocked), blocked, owned, groups, Chips(owned, groups)));
    }

    private string Display(string reason, IReadOnlyList<string> editIds, IReadOnlyList<string> groupIds,
        bool blocked) => blocked && groupIds.Count == 0
        ? BuildIssueAttribution.Blocking(reason, editIds.Select(id =>
        {
            var edit = _outline?.Edits.FirstOrDefault(candidate => candidate.Id == id);
            return edit is null ? null : new BuildIssueOwner(edit.Id, edit.Label, PartName(edit.Target));
        }).Where(owner => owner is not null).Select(owner => owner!).ToArray())
        : reason;

    private string Summary(string reason, IReadOnlyList<string> editIds, IReadOnlyList<string> groupIds,
        bool blocked) => blocked && groupIds.Count == 0
        ? BuildIssueAttribution.BlockingSummary(reason, editIds.Select(id =>
        {
            var edit = _outline?.Edits.FirstOrDefault(candidate => candidate.Id == id);
            return edit is null ? null : new BuildIssueOwner(edit.Id, edit.Label, PartName(edit.Target));
        }).Where(owner => owner is not null).Select(owner => owner!).ToArray())
        : reason;

    private IReadOnlyList<BuildPlacementChipVm> Chips(IReadOnlyList<string> editIds,
        IReadOnlyList<string> groupIds) => groupIds.Select(GroupChip).Where(chip => chip is not null)
        .Select(chip => chip!).Concat(editIds.SelectMany(PlacementChips)).DistinctBy(chip => chip.Target,
            StringComparer.Ordinal).ToArray();

    private BuildPlacementChipVm? GroupChip(string groupId) =>
        _outline?.Groups.FirstOrDefault(group => group.Id == groupId) is { } group
            ? new BuildPlacementChipVm(this, DiagnosticGroupName(group), group.Id, null, isGroup: true)
            : null;

    private IEnumerable<BuildPlacementChipVm> PlacementChips(string editId)
    {
        if (_outline?.Edits.FirstOrDefault(edit => edit.Id == editId) is not { } edit) yield break;
        foreach (var placement in edit.Placements)
        {
            if (placement.IsAlways)
            { yield return new BuildPlacementChipVm(this, PlacementNames.Always, null, null); continue; }
            var group = _outline.Groups.First(candidate => candidate.Id == placement.KeyGroupId);
            int index = placement.StateIndex!.Value;
            yield return new BuildPlacementChipVm(this,
                $"{DiagnosticGroupName(group)} · {PlacementNames.State(group.States[index].Label, index)}",
                placement.KeyGroupId, placement.StateId);
        }
    }

    private static string DiagnosticGroupName(KeyGroupOutline group) =>
        !string.IsNullOrWhiteSpace(group.Label) ? group.Label.Trim()
        : !string.IsNullOrWhiteSpace(group.Key) ? $"Key {group.Key.Trim()}"
        : PlacementNames.UnnamedGroup;

    private void ApplyIssueMarks()
    {
        foreach (var edit in AllEdits())
        {
            edit.Warning = JoinIssues(edit.EditDefinitionId);
            edit.IsBlocked = BlocksEdit(edit.EditDefinitionId);
        }
        foreach (var token in Always.Tokens.Concat(Groups.SelectMany(group => group.States)
                     .SelectMany(state => state.Tokens)))
        {
            token.Warning = JoinIssues(token.EditDefinitionId);
            token.IsBlocked = BlocksEdit(token.EditDefinitionId);
        }
    }

    private string JoinIssues(string editId) => string.Join("\n", Issues.Where(issue =>
        issue.EditDefinitionIds.Contains(editId, StringComparer.Ordinal)).Select(issue => issue.RawMessage));

    private bool BlocksEdit(string editId) => Issues.Any(issue => issue.BlocksBuild
        && issue.EditDefinitionIds.Contains(editId, StringComparer.Ordinal));

    /// <summary>The two disclosure tiers. Blocking issues are live plan facts and never merge with a
    /// completed run's lines; warnings carry the run-vs-live split.</summary>
    private void RefreshWarnings()
    {
        BlockedRows.Clear();
        foreach (var issue in Issues.Where(issue => issue.BlocksBuild)) BlockedRows.Add(issue);
        var live = Issues.Where(issue => !issue.BlocksBuild).Select(issue => issue.Message).ToList();
        if (Preview.Missing) live.Add(PreviewMissingWarning);
        var merged = BuildWarningSource.Merge(_runWarnings, live);
        Warnings.Clear();
        WarningRows.Clear();
        foreach (string line in merged)
        {
            Warnings.Add(line);
            if (line == BuildWarningSource.LastBuildLead)
                WarningRows.Add(BuildIssueVm.Heading(line));
            else WarningRows.Add(Issues.FirstOrDefault(issue => !issue.BlocksBuild && issue.RawMessage == line)
                ?? new BuildIssueVm(line, line, line, false, Array.Empty<string>(), Array.Empty<string>(),
                    Array.Empty<BuildPlacementChipVm>()));
        }
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasBlocked));
        OnPropertyChanged(nameof(EditsNeedAttention));
        RaiseDiagnosticProperties();
    }

    private void RefreshInfos()
    {
        Infos.Clear();
        foreach (string line in (_runInfos ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal)) Infos.Add(line);
        OnPropertyChanged(nameof(HasInfos));
        RaiseDiagnosticProperties();
    }

    private void RaiseDiagnosticProperties()
    {
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(InfoCount));
        OnPropertyChanged(nameof(HasDiagnostics));
        OnPropertyChanged(nameof(HasWarningDiagnosticsOnly));
        OnPropertyChanged(nameof(PrimaryBlocked));
        OnPropertyChanged(nameof(PrimaryBlockedMessage));
        OnPropertyChanged(nameof(PrimaryBlockedSummary));
        OnPropertyChanged(nameof(PrimaryBlockedPlacements));
        OnPropertyChanged(nameof(DiagnosticCounts));
        OnPropertyChanged(nameof(BlockedSectionHeader));
        OnPropertyChanged(nameof(WarningSectionHeader));
        OnPropertyChanged(nameof(InfoSectionHeader));
        OnPropertyChanged(nameof(ShowBlockedVerdict));
        OnPropertyChanged(nameof(ShowFooterVerdict));
    }

    private void RefreshCollisions()
    {
        var rows = new List<KeyCollisions.Entry>
        {
            new(KeyCollisions.WholeModLabel, KeyCollisions.WholeModLabel, _shell.WholeModKey),
        };
        rows.AddRange(Groups.Select(group => new KeyCollisions.Entry(group.Id, group.DisplayName, group.Key)));
        var tips = KeyCollisions.Tips(rows);
        WholeModKeyCollisionTip = tips.GetValueOrDefault(KeyCollisions.WholeModLabel, "");
        foreach (var group in Groups) group.CollisionTip = tips.GetValueOrDefault(group.Id, "");
    }

    public void IdentityChanged()
    {
        RefreshCollisions();
        RefreshStaleness();
    }

    public void RefreshInstallState()
    {
        _loader = _shell.LoaderState();
        RefreshGates();
    }

    private void RefreshGates()
    {
        BuildDisabledReason = BuildGate.Reason(_planning, _outline, PrimaryBlockedMessage);
        InstallDisabledReason = InstallGate.Reason(HasLastBuild, _loader);
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(BuildButtonTip));
        OnPropertyChanged(nameof(InstallButtonTip));
        OnPropertyChanged(nameof(LoaderNeedsAttention));
        OnPropertyChanged(nameof(LoaderButtonLabel));
        OnPropertyChanged(nameof(LoaderButtonTip));
        if (!_footerHeld && !IsWorkInFlight && !IsPlanning)
        {
            int placements = _outline is null ? 0 : _outline.Always.Count + _outline.Groups.Sum(group =>
                group.States.Sum(state => state.ActiveEditIds.Count));
            Footer = BuildDisabledReason is { } blocked ? BuildFooter.Blocked(blocked)
                : BuildFooter.Ready(placements, _outline!.Groups.Count(group => group.Key is not null));
        }
    }

    private void RefreshStaleness()
    {
        BuildResultStale = HasLastBuild && (_builtRevision != _session?.Revision
            || !string.Equals(_builtPreviewStamp, Preview.Stamp, StringComparison.Ordinal));
        OnPropertyChanged(nameof(BuildAgainLine));
    }

    private void RefreshPreview()
    {
        Preview = _shell.ReadPreview(_session?.Snapshot());
        RefreshWarnings();
        RefreshStaleness();
        OnPropertyChanged(nameof(PreviewEnabled));
        OnPropertyChanged(nameof(PreviewPickTip));
        OnPropertyChanged(nameof(PreviewTitle)); OnPropertyChanged(nameof(HasPreviewTitle));
        OnPropertyChanged(nameof(PreviewMissing));
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(HasNoPreview));
        ReleasePreview();
        if (Preview.FullPath is not { } path || Preview.Missing) return;
        int generation = _previewGeneration;
        PreviewDecoding = true;
        _ = DecodePreviewAsync(generation, path);
    }

    private async Task DecodePreviewAsync(int generation, string path)
    {
        Bitmap? bitmap = null;
        int decodeWidth = Preview.PixelWidth is > 0
            ? Math.Min(Preview.PixelWidth.Value, PreviewDecodeWidth)
            : PreviewDecodeWidth;
        try { bitmap = await _shell.LoadPreviewAsync(path, decodeWidth); } catch { }
        _dispatch(() =>
        {
            if (generation != _previewGeneration) { bitmap?.Dispose(); return; }
            PreviewImage = bitmap;
            PreviewDecoding = false;
            PreviewUndecodable = bitmap is null;
            OnPropertyChanged(nameof(PreviewTitle)); OnPropertyChanged(nameof(HasPreviewTitle));
        });
    }

    public void ReleasePreview()
    {
        _previewGeneration++;
        PreviewImage?.Dispose();
        PreviewImage = null;
        PreviewDecoding = false;
        PreviewUndecodable = false;
        OnPropertyChanged(nameof(PreviewTitle)); OnPropertyChanged(nameof(HasPreviewTitle));
    }

    public void SelectEdit(EditRef edit)
    {
        SelectedEdit = AllEdits().FirstOrDefault(row => string.Equals(row.EditDefinitionId,
            edit.EditDefinitionId, StringComparison.Ordinal));
        if (SelectedEdit is null) Status = $"{edit.Label} isn't in the Edits list.";
        else
        {
            FocusTarget = "";
            FocusTarget = "edit:" + SelectedEdit.EditDefinitionId;
        }
    }

    [RelayCommand]
    private void OpenEdit(BuildEditRowVm? edit)
    {
        if (edit is not null) _shell.GoToEdit(edit.Edit);
    }

    [RelayCommand]
    private void OpenToken(BuildTokenVm? token)
    {
        if (token is not null) _shell.GoToEdit(token.Edit);
    }

    internal void CreateKeyGroupFrom(BuildEditRowVm edit)
    {
        if (_session is null) return;
        Mutate(() => _session.CreateKeyGroup(null, edit.EditDefinitionId),
            $"Added a key group for {edit.Label}.");
    }

    internal void TogglePlacement(string editId, TargetPart target, EditDefinitionKind kind,
        string? groupId, string? stateId, bool place)
    {
        if (_session is null) return;
        string where = PlacementDestination(groupId, stateId);
        if (!place)
        {
            string name = EditName(editId);
            Mutate(() => Unplace(editId, groupId, stateId), $"Removed {name} from {where}.");
            return;
        }
        // A hide edit is placed by the verb that places every other edit. What this branch still does is
        // mint the part's hide where the board offers one for a part that has none: the library has no
        // row to place in that case, so the choice itself is the minting.
        if (kind == EditDefinitionKind.Hide)
        {
            string minted = editId;
            Mutate(() =>
            {
                if (minted.Length == 0) minted = _session.CreateHideEdit(target);
                Place(minted, groupId, stateId);
            }, () => Placed($"Added {EditName(minted)} to {where}.", null));
            return;
        }
        // Read before the write: a part is answered once in any one place, so seating this edit takes the
        // seat from whatever answered there, and the sentence says so.
        string? displaced = Displaced(editId, groupId, stateId);
        Mutate(() => _session.SeatEdit(editId, groupId, stateId),
            () => Placed($"Added {EditName(editId)} to {where}.", displaced));
    }

    /// <summary>What a placement or a move took away, said after what it did. A replaced answer is not a
    /// silent loss: the edit that left is named where it left from.</summary>
    private static string Placed(string said, string? displaced) =>
        displaced is null ? said : $"{said} {displaced} is no longer used there.";

    /// <summary>The edit a placement here would unseat, by the model's own seat rule, or null. This asks
    /// only for the wording, so a place or an edit the model cannot find is answered with no name at all:
    /// the write that follows is the one that refuses, in the model's own sentence.</summary>
    private string? Displaced(string editId, string? groupId, string? stateId)
    {
        try
        {
            return _session?.SeatHolder(editId, groupId, stateId) is { } seated ? EditName(seated) : null;
        }
        catch (KeyNotFoundException) { return null; }
    }

    /// <summary>What a sentence calls one edit: the name the modder gave it.</summary>
    private string EditName(string editId) => _outline?.Edits.FirstOrDefault(edit =>
        string.Equals(edit.Id, editId, StringComparison.Ordinal))?.Label ?? "the edit";

    private void Place(string editId, string? groupId, string? stateId)
    {
        if (groupId is null) _session!.PlaceEdit(editId);
        else _session!.PlaceEdit(editId, groupId, stateId!);
    }

    private void Unplace(string editId, string? groupId, string? stateId)
    {
        if (groupId is null) _session!.UnplaceEdit(editId);
        else _session!.UnplaceEdit(editId, groupId, stateId!);
    }

    /// <summary>Where a placement change landed, named the way the chips and the flyout name it. Read before
    /// the write, off the outline the board was drawn from — the destination itself is untouched either
    /// way.</summary>
    private string PlacementDestination(string? groupId, string? stateId)
    {
        if (groupId is null) return PlacementNames.Always;
        if (_outline?.Groups.FirstOrDefault(group => group.Id == groupId) is not { } found) return "this state";
        int index = found.States.ToList().FindIndex(state =>
            string.Equals(state.Id, stateId, StringComparison.Ordinal));
        return index < 0 ? PlacementNames.Group(found)
            : PlacementNames.Place(found, found.States[index], index);
    }

    /// <summary>A placed token dropped somewhere else on the board. It answers the way a library drop
    /// answers — a destination that already has this edit refuses rather than swallowing the gesture, and
    /// the destination's one-content-per-part seat is taken the same way — but the source placement goes
    /// with it, in one transaction.</summary>
    public void DropToken(string editId, string? fromGroupId, string? fromStateId,
        string? toGroupId, string? toStateId)
    {
        if (_session is null
            || _outline?.Edits.FirstOrDefault(edit => edit.Id == editId) is not { } edit) return;
        if (string.Equals(fromGroupId, toGroupId, StringComparison.Ordinal)
            && string.Equals(fromStateId, toStateId, StringComparison.Ordinal)) return;
        if (edit.Placements.Any(placement =>
                string.Equals(placement.KeyGroupId, toGroupId, StringComparison.Ordinal)
                && string.Equals(placement.StateId, toStateId, StringComparison.Ordinal)))
        { Status = $"{edit.Label} is already there."; return; }
        string where = PlacementDestination(toGroupId, toStateId);
        string? displaced = Displaced(editId, toGroupId, toStateId);
        Mutate(() => _session.MovePlacement(editId, fromGroupId, fromStateId, toGroupId, toStateId),
            () => Placed($"Moved {edit.Label} to {where}.", displaced));
    }

    internal void AddTo(string editId, TargetPart target, EditDefinitionKind kind,
        string? groupId, string? stateId) => TogglePlacement(editId, target, kind, groupId, stateId, true);

    internal void RemoveFrom(string editId, TargetPart target, EditDefinitionKind kind,
        string? groupId, string? stateId) => TogglePlacement(editId, target, kind, groupId, stateId, false);

    internal void SetGroupKey(string groupId, string? key) =>
        Mutate(() => _session?.SetGroupKey(groupId, key), key is null ? "Key cleared." : $"Key set to {key}.");

    internal void RenameGroup(string groupId, string? label) =>
        Mutate(() => _session?.RenameGroup(groupId, label), "Key group renamed.");

    internal void RenameState(string groupId, string stateId, string? label) =>
        Mutate(() => _session?.RenameState(groupId, stateId, label), "State renamed.");

    internal void DuplicateLastState(BuildGroupVm group)
    {
        if (group.States.LastOrDefault() is { } last)
            Mutate(() => _session?.DuplicateState(group.Id, last.Id), "State added.");
    }

    /// <summary>Remove one state, behind the same confirm every sibling destructive act carries. The uses
    /// inside it go with it; the edits themselves do not.</summary>
    internal async Task RemoveStateAsync(BuildStateVm state)
    {
        if (_session is null) return;
        int uses = state.Tokens.Count;
        string body = (uses == 0
                ? "This state has no edits in it."
                : $"This removes {uses} use{(uses == 1 ? "" : "s")} of edits. "
                    + "The edits used in it are kept.")
            + "\n\nThis cannot be undone.";
        if (!await _shell.ConfirmAsync($"Remove {state.DisplayName}?", body, "Remove", dangerous: true))
            return;
        Mutate(() => _session.RemoveState(state.GroupId, state.Id), "State removed.");
    }

    internal void ReorderState(BuildStateVm state, int toIndex) =>
        Mutate(() => _session?.ReorderState(state.GroupId, state.Index, toIndex), "States reordered.");

    internal async Task DeleteGroupAsync(BuildGroupVm group)
    {
        if (_session is null || _outline is null) return;
        int placements = group.States.Sum(state => state.Tokens.Count);
        var groupEditIds = group.States.SelectMany(state => state.Tokens)
            .Select(token => token.EditDefinitionId).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var unused = _outline.Edits.Where(edit => groupEditIds.Contains(edit.Id)
                && edit.Placements.All(placement => string.Equals(placement.KeyGroupId, group.Id,
                    StringComparison.Ordinal)))
            .Select(edit => edit.Label).Distinct(StringComparer.Ordinal).ToList();
        string body = $"This removes {placements} use{(placements == 1 ? "" : "s")} of edits. "
            + (unused.Count == 0 ? "No edits become unused." : $"{KeyCollisions.NameList(unused)} "
                + $"{(unused.Count == 1 ? "becomes" : "become")} unused.")
            + "\n\nThis cannot be undone.";
        if (!await _shell.ConfirmAsync($"Delete {group.DisplayName}?", body, "Delete", dangerous: true)) return;
        Mutate(() => _session.DeleteKeyGroup(group.Id), "Key group deleted.");
    }

    private void Mutate(Action work, string success) => Mutate(work, () => success);

    /// <summary>One authored change and the sentence it leaves. The sentence is asked for AFTER the change
    /// commits, so a line naming something the change created reads the board as it now stands.</summary>
    private void Mutate(Action work, Func<string> success)
    {
        if (IsWorkInFlight) { Status = BuildRunningReason; return; }
        try { work(); Status = success(); }
        catch (Exception e) { Status = AuthoredRefusal.ForScreen(e, ChangeAction); Rebuild(); }
    }

    // ---- what a failure says ----
    //
    // One answer, from the model's own home: a refusal is worded for the person reading it and shows as it
    // is; anything else is a defect whose text names edit, slot, key-group and state ids, which the text
    // guide keeps off screen — so the page says what it could not do instead. The verb phrases below
    // complete "Couldn't …".

    public const string ChangeAction = "make that change";
    public const string PlanAction = "check this build";
    public const string BuildAction = "build this mod";
    public const string InstallAction = "install this mod";

    internal void FocusPlacement(BuildPlacementChipVm chip)
    {
        MarkedTarget = chip.IsGroup
            ? BuildMarkedTarget.Group(chip.GroupId!)
            : chip.GroupId is null
                ? BuildMarkedTarget.Always
                : BuildMarkedTarget.State(chip.GroupId, chip.StateId!);
        FocusTarget = "";
        FocusTarget = chip.Target;
        Status = BuildFooter.End($"Showing {chip.Label}");
    }

    /// <summary>The view calls this for input originating on the Behavior board. The diagnostic chip that
    /// established the mark lives outside that board, so its own pointer press cannot erase the mark before
    /// its command runs.</summary>
    public void ClearMarkedTarget() => MarkedTarget = null;

    public void DropEdit(string editId, string? groupId, string? stateId)
    {
        if (_outline?.Edits.FirstOrDefault(edit => edit.Id == editId) is not { } edit) return;
        if (IsUsedAt(editId, groupId, stateId))
        { Status = $"{edit.Label} is already there."; return; }
        AddTo(edit.Id, edit.Target, edit.Kind, groupId, stateId);
    }

    /// <summary>Whether this edit is already used in this place. The cursor asks it while a drag is still
    /// in the air, so a drop that would only refuse shows as refused instead of accepted and swallowed;
    /// the release asks it again, which is what actually refuses.</summary>
    public bool IsUsedAt(string editId, string? groupId, string? stateId) =>
        _outline?.Edits.FirstOrDefault(edit =>
                string.Equals(edit.Id, editId, StringComparison.Ordinal))?.Placements
            .Any(placement => string.Equals(placement.KeyGroupId, groupId, StringComparison.Ordinal)
                && string.Equals(placement.StateId, stateId, StringComparison.Ordinal)) == true;

    public void DropState(string groupId, string stateId, string targetStateId)
    {
        var group = Groups.FirstOrDefault(row => row.Id == groupId);
        int from = group?.States.ToList().FindIndex(state => state.Id == stateId) ?? -1;
        int to = group?.States.ToList().FindIndex(state => state.Id == targetStateId) ?? -1;
        if (from >= 0 && to >= 0 && from != to) ReorderState(group!.States[from], to);
    }

    [RelayCommand]
    private async Task Build()
    {
        if (!CanBuild) { Status = BuildButtonTip; return; }
        _runSurfaceCleared = false;
        IsWorkInFlight = true;
        _footerHeld = true;
        Footer = BuildFooter.Running("Saving…");
        RefreshGates();
        BuildRunResult result;
        try { result = await _shell.RunBuildAsync(_progress); }
        catch (Exception e)
        {
            result = new BuildRunResult(false, AuthoredRefusal.ForScreen(e, BuildAction), "", null,
                "", "", Array.Empty<string>(), Array.Empty<string>(), _session?.Revision ?? -1,
                Preview.Stamp);
        }
        IsWorkInFlight = false;
        if (!result.Succeeded)
        {
            // This run's log, or none: a failure that wrote no log must not leave the Log button offering
            // the last run's, which is a different build's account of a different outcome.
            LastLogPath = result.LogPath;
            Footer = BuildFooter.Failed(result.Failure ?? BuildGate.UnnamedFailure);
        }
        else
        {
            LastBuildDir = result.OutDir;
            LastZipPath = result.ZipPath ?? "";
            LastLogPath = result.LogPath;
            BuiltPackage = result.Package;
            _runWarnings = result.Warnings;
            _runInfos = result.Infos;
            _builtRevision = result.Revision;
            _builtPreviewStamp = result.PreviewStamp;
            RefreshWarnings();
            RefreshInfos();
            Footer = BuildFooter.Built(result.Package);
        }
        _loader = _shell.LoaderState();
        RefreshPreview();
        RefreshGates();
        RefreshStaleness();
        RaiseResultProperties();
    }

    private void ClearBuildResult()
    {
        LastBuildDir = LastZipPath = BuiltPackage = "";
        BuildResultStale = false;
        _runWarnings = null;
        _runInfos = null;
        _builtRevision = null;
        _builtPreviewStamp = null;
        RefreshWarnings();
        RefreshInfos();
        RefreshGates();
        RaiseResultProperties();
    }

    [RelayCommand]
    private async Task Install()
    {
        if (!CanInstall) { Status = InstallButtonTip; return; }
        var standingFooter = Footer;
        bool standingFooterHeld = _footerHeld;
        IsWorkInFlight = true;
        _footerHeld = true;
        Footer = BuildFooter.Running("Installing…");
        RefreshGates();
        BuildInstallResult result;
        try { result = await _shell.InstallBuildAsync(LastBuildDir, BuiltPackage); }
        catch (Exception e)
        { result = new BuildInstallResult(true, true, AuthoredRefusal.ForScreen(e, InstallAction)); }
        IsWorkInFlight = false;
        if (result.Completed)
        {
            if (result.InstalledDir is { } installed) LastInstallPath = installed;
            Footer = result.Failed ? BuildFooter.Failed(result.Line) : BuildFooter.Notice(result.Line);
        }
        else
        {
            Footer = standingFooter;
            _footerHeld = standingFooterHeld;
        }
        RefreshGates();
        RaiseResultProperties();
    }

    [RelayCommand]
    private async Task ChooseLoader()
    {
        if (IsWorkInFlight) { Status = BuildRunningReason; return; }
        await _shell.ChooseLoaderAsync();
        _loader = _shell.LoaderState();
        RefreshGates();
    }

    [RelayCommand]
    private void OpenFolder() => Open(BuildArtifactKind.Folder, LastBuildDir);
    [RelayCommand]
    private void OpenZip() => Open(BuildArtifactKind.Zip, LastZipPath);
    [RelayCommand]
    private void OpenLog() => Open(BuildArtifactKind.Log, LastLogPath);
    [RelayCommand]
    private void OpenInstalled() => Open(BuildArtifactKind.InstalledFolder, LastInstallPath);

    private void Open(BuildArtifactKind kind, string path)
    {
        if (path.Length == 0) return;
        try { _shell.OpenArtifact(kind, path); }
        catch (Exception e) { Status = AuthoredRefusal.ForScreen(e, $"open {Path.GetFileName(path)}"); }
    }

    [RelayCommand]
    private async Task BrowsePreview()
    {
        if (!PreviewEnabled) { Status = PreviewPickTip; return; }
        if (await _shell.PickPreviewAsync() is { } path) SetPreview(path);
    }

    public void DropPreview(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) { Status = PreviewNoFileInDrop; return; }
        if (paths.Count != 1) { Status = PreviewOneAtATime; return; }
        SetPreview(paths[0]);
    }

    private void SetPreview(string source)
    {
        if (!PreviewEnabled) { Status = PreviewPickTip; return; }
        if (!PreviewExtensions.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
        { Status = PreviewNotAnImage; return; }
        try
        {
            _shell.SetPreviewFrom(_session!, source);
            Status = $"Preview saved as preview{Path.GetExtension(source).ToLowerInvariant()}.";
        }
        catch (Exception e) { Status = AuthoredRefusal.ForScreen(e, "copy the image"); }
        RefreshPreview();
    }

    [RelayCommand]
    private async Task RemovePreview()
    {
        if (IsWorkInFlight) { Status = BuildRunningReason; return; }
        if (Preview.RelativeFile is null || _session is null) return;
        bool owned = IsOwnedPreview(Preview.RelativeFile) && Preview.FullPath is not null && !Preview.Missing;
        string body = owned
            ? $"{Preview.RelativeFile} is deleted from the mod folder. This cannot be undone."
            : "The image file stays where it is. This mod stops using it.";
        if (!await _shell.ConfirmAsync(PreviewRemoveQuestion, body, "Remove", dangerous: true)) return;
        try { _shell.RemovePreviewFile(_session, Preview); Status = "Preview removed."; }
        catch (Exception e) { Status = AuthoredRefusal.ForScreen(e, "remove the preview"); }
        RefreshPreview();
    }

    internal static bool IsOwnedPreview(string relative) =>
        string.Equals(Path.GetFileName(relative), relative, StringComparison.Ordinal)
        && string.Equals(Path.GetFileNameWithoutExtension(relative), "preview", StringComparison.OrdinalIgnoreCase)
        && PreviewExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase);

    private IEnumerable<BuildEditRowVm> AllEdits() =>
        Subjects.SelectMany(subject => subject.Parts).SelectMany(part => part.Edits);

    private void RaiseResultProperties()
    {
        OnPropertyChanged(nameof(HasLastBuild));
        OnPropertyChanged(nameof(HasBuildZip));
        OnPropertyChanged(nameof(HasBuildLog));
        OnPropertyChanged(nameof(HasFailureLog));
        OnPropertyChanged(nameof(HasLastInstall));
        OnPropertyChanged(nameof(BuildAgainLine));
        OnPropertyChanged(nameof(FolderTip));
        OnPropertyChanged(nameof(InstalledFolderTip));
    }

    partial void OnIsWorkInFlightChanged(bool value)
    {
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(BuildButtonTip));
        OnPropertyChanged(nameof(InstallButtonTip));
        OnPropertyChanged(nameof(PreviewEnabled));
        OnPropertyChanged(nameof(PreviewPickTip));
        OnPropertyChanged(nameof(BoardEnabled));
    }

    partial void OnFooterChanged(BuildFooter value)
    {
        OnPropertyChanged(nameof(ShowBlockedVerdict));
        OnPropertyChanged(nameof(ShowFooterVerdict));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StatusTip));

    partial void OnMarkedTargetChanged(BuildMarkedTarget? value)
    {
        Always.NotifyMarkChanged();
        foreach (var group in Groups)
        {
            group.NotifyMarkChanged();
            foreach (var state in group.States) state.NotifyMarkChanged();
        }
    }

    partial void OnSelectedEditChanged(BuildEditRowVm? oldValue, BuildEditRowVm? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }
}

public sealed partial class BuildSubjectVm : ObservableObject
{
    public BuildSubjectVm(string key, string label) { Key = key; _label = label; }
    /// <summary>What this row stays itself by across a redraw. Never on screen.</summary>
    internal string Key { get; }
    [ObservableProperty] private string _label;
    public ObservableCollection<BuildPartVm> Parts { get; } = new();
}

public sealed partial class BuildPartVm : ObservableObject
{
    public BuildPartVm(string key, string label, TargetPart target)
    { Key = key; _label = label; Target = target; }
    internal string Key { get; }
    [ObservableProperty] private string _label;
    public TargetPart Target { get; internal set; }
    public ObservableCollection<BuildEditRowVm> Edits { get; } = new();
}

public sealed partial class BuildEditRowVm : ObservableObject
{
    private readonly BuildPageVm _page;
    internal BuildEditRowVm(BuildPageVm page, AuthoredEditOutlineEntry edit, string partLabel,
        string changeSummary, IReadOnlyList<string> placementChips,
        IReadOnlyList<BuildPlacementChoiceVm> choices)
    {
        _page = page;
        EditDefinitionId = edit.Id;
        Kind = edit.Kind;
        Target = edit.Target;
        _label = edit.Label;
        _partLabel = partLabel;
        _changeSummary = changeSummary;
        _placementChips = placementChips;
        foreach (var choice in choices) UseIn.Add(choice);
    }
    public string EditDefinitionId { get; }
    public EditDefinitionKind Kind { get; }
    public TargetPart Target { get; private set; }
    [ObservableProperty] private string _label;
    [ObservableProperty] private string _partLabel;
    [ObservableProperty] private string _changeSummary;
    [ObservableProperty] private IReadOnlyList<string> _placementChips;
    public string Display => Label;
    public string FullDisplay => $"{PartLabel} · {Label}";
    public bool HasChangeSummary => ChangeSummary.Length > 0;
    public EditRef Edit => new(Target, EditDefinitionId, Label);
    public ObservableCollection<BuildPlacementChoiceVm> UseIn { get; } = new();
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private bool _isBlocked;
    public bool HasWarning => Warning.Length > 0;
    public string Mark => IsBlocked ? "✗" : "⚠";
    partial void OnLabelChanged(string value)
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(FullDisplay));
        OnPropertyChanged(nameof(Edit));
    }
    partial void OnPartLabelChanged(string value) => OnPropertyChanged(nameof(FullDisplay));
    partial void OnChangeSummaryChanged(string value) => OnPropertyChanged(nameof(HasChangeSummary));
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnIsBlockedChanged(bool value) => OnPropertyChanged(nameof(Mark));

    /// <summary>Bring this row level with the outline without replacing it, so anything open on it stays
    /// open. Its choices are matched to the ones already here by where each one puts the edit.</summary>
    internal void Sync(AuthoredEditOutlineEntry edit, string partLabel, string changeSummary,
        IReadOnlyList<string> placementChips, IReadOnlyList<BuildPlacementChoiceVm> choices)
    {
        Target = edit.Target;
        Label = edit.Label;
        PartLabel = partLabel;
        ChangeSummary = changeSummary;
        if (!placementChips.SequenceEqual(PlacementChips, StringComparer.Ordinal))
            PlacementChips = placementChips;
        for (int index = 0; index < choices.Count; index++)
        {
            var wanted = choices[index];
            int found = -1;
            for (int scan = index; scan < UseIn.Count; scan++)
                if (UseIn[scan].Addresses(wanted)) { found = scan; break; }
            if (found < 0) { UseIn.Insert(index, wanted); continue; }
            if (found != index) UseIn.Move(found, index);
            UseIn[index].Sync(wanted.Label, wanted.IsPlaced);
        }
        while (UseIn.Count > choices.Count) UseIn.RemoveAt(UseIn.Count - 1);
    }

    [RelayCommand] private void Open() => _page.OpenEditCommand.Execute(this);
    [RelayCommand] private void MakeKey() => _page.CreateKeyGroupFrom(this);
}

public sealed partial class BuildPlacementChoiceVm : ObservableObject
{
    private readonly BuildPageVm _page;
    private bool _ready;
    internal BuildPlacementChoiceVm(BuildPageVm page, string editId, TargetPart target, EditDefinitionKind kind,
        string label, string? groupId, string? stateId, bool placed)
    {
        _page = page; EditDefinitionId = editId; Target = target; Kind = kind; _label = label;
        GroupId = groupId; StateId = stateId; _isPlaced = placed; _ready = true;
    }
    public string EditDefinitionId { get; }
    public TargetPart Target { get; }
    public EditDefinitionKind Kind { get; }
    [ObservableProperty] private string _label;
    public string? GroupId { get; }
    public string? StateId { get; }
    [ObservableProperty] private bool _isPlaced;
    partial void OnIsPlacedChanged(bool value)
    {
        if (_ready) _page.TogglePlacement(EditDefinitionId, Target, Kind, GroupId, StateId, value);
    }

    /// <summary>Whether two rows put the edit in the same place. That, and not the name beside the tick, is
    /// what one choice IS: the name moves when a key group is renamed.</summary>
    internal bool Addresses(BuildPlacementChoiceVm other) =>
        string.Equals(GroupId, other.GroupId, StringComparison.Ordinal)
        && string.Equals(StateId, other.StateId, StringComparison.Ordinal);

    /// <summary>Take the answer the session now gives without committing it back: the tick is being brought
    /// level with the session, not asked to change it.</summary>
    internal void Sync(string label, bool placed)
    {
        Label = label;
        if (IsPlaced == placed) return;
        _ready = false;
        IsPlaced = placed;
        _ready = true;
    }
}

public enum BuildMarkedTargetKind
{
    Always,
    Group,
    State,
}

/// <summary>The exact board card selected by a diagnostic jump. State ids are only unique inside their key
/// group, so a state identity carries both ids even though the existing scroll trigger remains its state id.</summary>
public sealed record BuildMarkedTarget(BuildMarkedTargetKind Kind, string? GroupId, string? StateId)
{
    public static BuildMarkedTarget Always { get; } = new(BuildMarkedTargetKind.Always, null, null);
    public static BuildMarkedTarget Group(string groupId) => new(BuildMarkedTargetKind.Group, groupId, null);
    public static BuildMarkedTarget State(string groupId, string stateId) =>
        new(BuildMarkedTargetKind.State, groupId, stateId);

    public bool Matches(BuildGroupVm group) => Kind == BuildMarkedTargetKind.Group
        && string.Equals(GroupId, group.Id, StringComparison.Ordinal);
    public bool Matches(BuildStateVm state) => Kind == BuildMarkedTargetKind.State
        && string.Equals(GroupId, state.GroupId, StringComparison.Ordinal)
        && string.Equals(StateId, state.Id, StringComparison.Ordinal);
}

public sealed class BuildAlwaysVm : ObservableObject
{
    private readonly BuildPageVm _page;
    internal BuildAlwaysVm(BuildPageVm page) => _page = page;
    public string Label => "Always";
    public bool IsMarked => _page.MarkedTarget?.Kind == BuildMarkedTargetKind.Always;
    public ObservableCollection<BuildTokenVm> Tokens { get; } = new();
    public ObservableCollection<BuildEditChoiceVm> AvailableEdits { get; } = new();
    internal void NotifyMarkChanged() => OnPropertyChanged(nameof(IsMarked));
}

public sealed partial class BuildGroupVm : ObservableObject
{
    private readonly BuildPageVm _page;
    private bool _ready;
    internal BuildGroupVm(BuildPageVm page, KeyGroupOutline group)
    {
        _page = page; Id = group.Id; _key = group.Key; _label = group.Label ?? ""; _ready = true;
    }
    public string Id { get; }
    public bool IsMarked => _page.MarkedTarget?.Matches(this) == true;
    public string DisplayName => !string.IsNullOrWhiteSpace(Label) ? Label.Trim()
        : !string.IsNullOrWhiteSpace(Key) ? $"Key {Key}" : "Unnamed key group";
    public bool IsKeyless => string.IsNullOrWhiteSpace(Key);
    public ObservableCollection<BuildStateVm> States { get; } = new();
    [ObservableProperty] private string? _key;
    [ObservableProperty] private string _label;
    [ObservableProperty] private string _collisionTip = "";
    public bool HasCollision => CollisionTip.Length > 0;
    partial void OnKeyChanged(string? value)
    {
        OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(IsKeyless));
        if (_ready) _page.SetGroupKey(Id, value);
    }
    partial void OnLabelChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        if (_ready) _page.RenameGroup(Id, value);
    }
    partial void OnCollisionTipChanged(string value) => OnPropertyChanged(nameof(HasCollision));
    [RelayCommand] private Task Delete() => _page.DeleteGroupAsync(this);
    [RelayCommand] private void AddState() => _page.DuplicateLastState(this);
    internal void NotifyMarkChanged() => OnPropertyChanged(nameof(IsMarked));
}

public sealed partial class BuildStateVm : ObservableObject
{
    private readonly BuildPageVm _page;
    private bool _ready;
    internal BuildStateVm(BuildPageVm page, KeyGroupOutline group, KeyGroupStateOutline state, int index,
        int stateCount)
    {
        _page = page; GroupId = group.Id; Id = state.Id; Index = index; StateCount = stateCount;
        _label = state.Label ?? "";
        (ActiveCount, HiddenCount) = page.CompositionCounts(GroupId, Index);
        _ready = true;
    }
    public string GroupId { get; }
    public string Id { get; }
    public bool IsMarked => _page.MarkedTarget?.Matches(this) == true;
    public int Index { get; }
    public int StateCount { get; }
    public bool CanMoveUp => Index > 0;
    public bool CanMoveDown => Index + 1 < StateCount;
    /// <summary>Two states are the session's floor — the UI greys ✕ at the floor rather than offering a
    /// click the session refuses.</summary>
    public bool CanRemove => StateCount > 2;
    public string RemoveTip => CanRemove ? "Remove this state. The edits used in it are kept."
        : AuthoredEditSession.TwoStateFloor;
    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"State {Index + 1}" : Label.Trim();
    public bool IsLaunch => Index == 0;
    public ObservableCollection<BuildTokenVm> Tokens { get; } = new();
    public ObservableCollection<BuildEditChoiceVm> AvailableEdits { get; } = new();
    public ObservableCollection<BuildResolvedPartVm> Composition { get; } = new();
    public int ActiveCount { get; }
    public int HiddenCount { get; }
    public string CountLine => string.Join(" · ", new[]
    {
        ActiveCount > 0 ? $"{ActiveCount} active" : "",
        HiddenCount > 0 ? $"{HiddenCount} hidden" : "",
    }.Where(value => value.Length > 0));
    [ObservableProperty] private string _label;
    partial void OnLabelChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        if (_ready) _page.RenameState(GroupId, Id, value);
    }
    [RelayCommand] private Task Remove() => _page.RemoveStateAsync(this);
    [RelayCommand] private void MoveUp() { if (Index > 0) _page.ReorderState(this, Index - 1); }
    [RelayCommand] private void MoveDown() => _page.ReorderState(this, Index + 1);
    [RelayCommand] private void OpenComposition() => _page.OpenComposition(this);
    internal void NotifyMarkChanged() => OnPropertyChanged(nameof(IsMarked));
}

public sealed partial class BuildEditChoiceVm
{
    private readonly BuildPageVm _page;
    internal BuildEditChoiceVm(BuildPageVm page, string editDefinitionId, TargetPart target,
        EditDefinitionKind kind, string label, string partLabel, string? groupId, string? stateId)
    {
        _page = page; EditDefinitionId = editDefinitionId; Target = target; Kind = kind; Label = label;
        PartLabel = partLabel; GroupId = groupId; StateId = stateId;
    }
    public string EditDefinitionId { get; }
    public TargetPart Target { get; }
    public EditDefinitionKind Kind { get; }
    public string Label { get; }
    public string PartLabel { get; }
    public string? GroupId { get; }
    public string? StateId { get; }
    public string Display => $"{PartLabel} · {Label}";
    [RelayCommand] private void Add() => _page.AddTo(EditDefinitionId, Target, Kind, GroupId, StateId);
}

public sealed partial class BuildTokenVm : ObservableObject
{
    private readonly BuildPageVm _page;
    internal BuildTokenVm(BuildPageVm page, string editId, TargetPart target, EditDefinitionKind kind,
        string label, string partLabel, string? groupId, string? stateId)
    {
        _page = page; EditDefinitionId = editId; Target = target; Kind = kind; Label = label;
        PartLabel = partLabel; GroupId = groupId; StateId = stateId;
    }
    public string EditDefinitionId { get; }
    public TargetPart Target { get; }
    public EditDefinitionKind Kind { get; }
    public string Label { get; }
    public string PartLabel { get; }
    public string? GroupId { get; }
    public string? StateId { get; }
    public string Display => $"{PartLabel} · {Label}";
    public EditRef Edit => new(Target, EditDefinitionId, Label);
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private bool _isBlocked;
    public bool HasWarning => Warning.Length > 0;
    public string Mark => IsBlocked ? "✗" : "⚠";
    partial void OnWarningChanged(string value) => OnPropertyChanged(nameof(HasWarning));
    partial void OnIsBlockedChanged(bool value) => OnPropertyChanged(nameof(Mark));
    [RelayCommand] private void Remove() => _page.RemoveFrom(EditDefinitionId, Target, Kind, GroupId, StateId);
    [RelayCommand] private void Open() => _page.OpenTokenCommand.Execute(this);
}

public enum BuildResolvedPartState
{
    Original,
    Active,
    Hidden,
}

public sealed record BuildResolvedPartVm(string Part, string Answer, BuildResolvedPartState State);

public sealed class BuildIssueVm
{
    internal BuildIssueVm(string rawMessage, string message, string summaryMessage, bool blocked,
        IReadOnlyList<string> editIds,
        IReadOnlyList<string> groupIds, IReadOnlyList<BuildPlacementChipVm> placements, bool heading = false)
    {
        RawMessage = rawMessage; Message = message; SummaryMessage = summaryMessage; BlocksBuild = blocked;
        EditDefinitionIds = editIds; GroupIds = groupIds; Placements = placements; IsHeading = heading;
    }
    public string RawMessage { get; }
    public string Message { get; private set; }
    public string SummaryMessage { get; private set; }
    public bool BlocksBuild { get; private set; }
    public IReadOnlyList<string> EditDefinitionIds { get; private set; }
    public IReadOnlyList<string> GroupIds { get; private set; }
    public IReadOnlyList<BuildPlacementChipVm> Placements { get; private set; }
    public bool HasPlacements => Placements.Count > 0;
    public bool IsHeading { get; }

    /// <summary>Take a second owner's edits. Called while the rows are still being built, before anything
    /// reads them, so one line keeps one row instead of the later owner being dropped.</summary>
    internal void Own(string message, string summaryMessage, IReadOnlyList<string> editIds,
        IReadOnlyList<string> groupIds, IReadOnlyList<BuildPlacementChipVm> placements, bool blocked)
    {
        Message = message;
        SummaryMessage = summaryMessage;
        EditDefinitionIds = editIds;
        GroupIds = groupIds;
        Placements = placements;
        BlocksBuild |= blocked;
    }

    internal static BuildIssueVm Heading(string line) => new(line, line, line, false, Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<BuildPlacementChipVm>(), heading: true);
}

public sealed partial class BuildPlacementChipVm : ObservableObject
{
    private readonly BuildPageVm _page;
    internal BuildPlacementChipVm(BuildPageVm page, string label, string? groupId, string? stateId,
        bool isGroup = false)
    {
        _page = page; Label = label; GroupId = groupId; StateId = stateId; IsGroup = isGroup;
    }
    public string Label { get; }
    public string? GroupId { get; }
    public string? StateId { get; }
    public bool IsGroup { get; }
    public string Target => IsGroup ? "group:" + GroupId : StateId ?? "always";
    public const string FocusTip = "Show this on the Behavior board.";
    [RelayCommand] private void Focus() => _page.FocusPlacement(this);
}
