using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remold.Core;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Textures;

namespace Remold.App.ViewModels.EditPage;

/// <summary>
/// The ② Edit page: <b>subject → part → edits</b> in the tree, and an inspector that edits the selected one.
    /// Everything it shows is read from one <see cref="AuthoredEditSession"/> — its
    /// <see cref="AuthoredEditSession.Outline"/> for what exists, its
/// <see cref="AuthoredEditSession.Slots"/> for what each edit binds — and every change it makes goes back
/// through that same session's verbs. There is no second store: the page holds no mutable copy of anything it
/// draws, and a rebuild is a fresh read.
///
/// <para>The imperative half — reading the install, running Blender or an image editor, decoding a picture,
/// asking a question, moving to ③ Build — lives behind <see cref="IEditPageShell"/>. Shell operations that
/// publish external bytes commit them through this same session at the exact supplied slot; the page learns
/// of every successful commit through the session's one revisioned change event.</para>
///
/// <para>Because a rebuild is a fresh read, nothing a verb needs to outlive it may live on a row. Which verb
/// is running, which part the install refused, and every picture already rendered are all keyed by identity
/// and held here, so the rows redrawn under a running verb still report it.</para>
///
/// <para>Refusals the model wrote for the screen are shown as they are: they are written for the person
/// reading them, and a second wording here would be a second opinion about what happened. Every OTHER
/// failure names the action the modder ran instead — its own message names slot ids, edit ids and file
/// handles, none of which mean anything on a status line. <see cref="AuthoredRefusal"/> is the one place
/// that choice is made.</para>
/// </summary>
public sealed partial class EditPageVm : ObservableObject
{
    private readonly IEditPageShell _shell;
    private readonly Action<Action> _dispatch;
    private readonly object _changeGate = new();
    private AuthoredEditSession? _session;
    private EditBoardSnapshot? _board;
    private long _appliedRevision = -1;

    public EditPageVm(IEditPageShell shell, Action<Action>? dispatch = null,
        bool coalesceResolvedRebuilds = false)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        // Whether resolver-completion bursts batch onto the dispatcher is the CONSTRUCTION SITE's fact:
        // only the real window, whose dispatcher marshals to an actual UI queue, opts in. It is never
        // sniffed off the ambient SynchronizationContext — a test runner installs one on some threads and
        // the app's worker completions lack one on all of theirs — and never inferred from the dispatch
        // delegate, which headless consumers legitimately pass as an inline call.
        _coalesceResolvedRebuilds = coalesceResolvedRebuilds;
        _dispatch = dispatch ?? (work => work());
        _progress = new StatusSink(value => Status = value);
    }

    private readonly bool _coalesceResolvedRebuilds;

    /// <summary>The tree roots — one per subject.</summary>
    public ObservableCollection<EditNodeVm> Nodes { get; } = new();

    [ObservableProperty] private EditNodeVm? _selectedNode;

    [ObservableProperty] private string _status = "";

    [ObservableProperty] private string _filter = "";

    /// <summary>A non-empty filter hides every row — the panel says so rather than going silently blank.</summary>
    [ObservableProperty] private bool _noMatches;

    /// <summary>No mod open, or a mod with nothing in it yet.</summary>
    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>The install is still being read. The tree says so rather than reading as empty.</summary>
    [ObservableProperty] private bool _isReading;

    /// <summary>Why the install cannot be read, in the shell's own words, or null while it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnavailable))]
    private string? _unavailable;

    public bool IsUnavailable => !string.IsNullOrEmpty(Unavailable);

    public bool HasNodes => Nodes.Count > 0;

    /// <summary>Reports land on the status line synchronously, so a verb's own line is on screen before the
    /// verb returns. The shell raises them on the UI thread, as the workbench's does.</summary>
    private sealed class StatusSink : IProgress<string>
    {
        private readonly Action<string> _write;
        internal StatusSink(Action<string> write) => _write = write;
        public void Report(string value) => _write(value);
    }

    private readonly IProgress<string> _progress;

    /// <summary>A typed worker progress bridge. Reports are marshalled through the page dispatcher, then
    /// owner-checked at delivery so an old project's queued count cannot overwrite the new page's line.</summary>
    private sealed class DispatchProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        internal DispatchProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }

    // ---- lifecycle ----

    /// <summary>Open a mod's authored model on this page, or close it with null. Nothing survives the swap:
    /// a refusal, a running verb's gate and a rendered picture all belong to the project that produced
    /// them.</summary>
    public void Load(AuthoredEditSession? session)
    {
        if (_session is not null) _session.Changed -= OnSessionChanged;
        _session = session;
        _appliedRevision = session?.Revision ?? -1;
        if (_session is not null) _session.Changed += OnSessionChanged;
        Status = "";
        _refusals.Clear();
        _meshEditBlocks.Clear();
        _meshEditReads.Clear();
        _meshEditEpoch++;
        ForgetInstallReads();
        _busy.Clear();
        DropPreviews();
        Rebuild();
    }

    /// <summary>Re-enter this project without clearing work, refusals, previews, selection history or the
    /// page's status line.</summary>
    public void Enter() => Rebuild();

    private void OnSessionChanged(object? sender, AuthoredProjectChangedEventArgs change)
    {
        if (!ReferenceEquals(sender, _session)) return;
        // Publish/normalize work commonly commits on a worker thread. The page owns Avalonia collections,
        // so production supplies the UI dispatcher here; tests and other headless consumers can stay inline.
        // Revision filtering remains inside the dispatched callback because two queued notifications may
        // still be delivered in the opposite order from their commits.
        _dispatch(() => ApplySessionChange(sender, change));
    }

    private void ApplySessionChange(object? sender, AuthoredProjectChangedEventArgs change)
    {
        lock (_changeGate)
        {
            if (!ReferenceEquals(sender, _session)) return;
            // Invalidation belongs to the delivered change, not to revision ordering. Another subscriber can
            // commit a newer metadata revision re-entrantly before this callback sees the original change.
            bool forgotten = InvalidatePreviews(change);
            if (change.Revision <= _appliedRevision)
            {
                // No rebuild follows a superseded change, so nothing else asks for the pictures it just took
                // away: the row on screen would hold a loading shimmer with no producer behind it until the
                // modder selected away and back. Ask for them here. Every load is memoized behind its row's
                // request id, so a change that moved nothing this row draws costs one no-op.
                if (forgotten && SelectedNode is { } showing) _ = LoadPreviewsAsync(showing);
                return;
            }
            _appliedRevision = change.Revision;
            _shell.ProjectChanged(change.Revision);
            Rebuild();
        }
    }

    /// <summary>Put a sentence on this page's status line from outside a verb, such as an exact external-tool
    /// transport. The page's own verbs write <see cref="Status"/> directly; this is the way in for the
    /// window, which does the work those sentences are about.</summary>
    public void ReportStatus(string message) => Status = message;

    /// <summary>Re-read the session and redraw, keeping the selection where it survives the change.
    /// Serialized on the change gate: a warm notification and a session-change application can otherwise
    /// interleave under an inline dispatcher and mutate the tree mid-enumeration.</summary>
    public void Rebuild()
    {
        lock (_changeGate)
        {
            // A redraw asked for from INSIDE one runs after it, never inside it. An install read that
            // settles on the calling thread lands here mid-tree, and a nested rebuild would replace the
            // rows the outer one is still filling. Coalesced, so a burst of settling reads costs one extra
            // pass rather than one each.
            if (_rebuilding) { _rebuildOwed = true; return; }
            _rebuilding = true;
            try { do { _rebuildOwed = false; RebuildLocked(); } while (_rebuildOwed); }
            finally { _rebuilding = false; _rebuildOwed = false; }
        }
    }

    private bool _rebuilding;
    private bool _rebuildOwed;

    private void RebuildLocked()
    {
        var keepPart = SelectedNode?.Part;
        string? keepEdit = SelectedNode?.EditDefinitionId;
        var keepKind = SelectedNode?.Kind;
        string keepSubject = SelectedNode?.Subject ?? "";
        string keepOutfit = SelectedNode?.Outfit ?? "";
        int keepMaterial = SelectedNode?.MaterialOrdinal ?? -1;
        // Which edits' material branches are open. An edit row starts collapsed — the material level is a
        // drill-down, not the tree's resting shape — so an open one is the modder's own doing and survives
        // the redraw, the way the selection does.
        var openEdits = Flatten(Nodes)
            .Where(node => node.IsEdit && node.IsExpanded && node.Children.Count > 0
                && node.Part is not null && node.EditDefinitionId is not null)
            .Select(node => EditBusy(node.Part!, node.EditDefinitionId!))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var node in Nodes) node.Release();
        Nodes.Clear();
        SelectedNode = null;

        _livePreviews.Clear();
        var state = _shell.InstallState();
        IsReading = state.IsReading;
        Unavailable = state.Unavailable;

        _board = _session is null ? null : EditBoardSnapshot.Create(_session.Snapshot());
        if (_board is not null)
            foreach (var root in BuildSubjects(_board)) Nodes.Add(root);
        PrunePreviews();

        IsEmpty = Nodes.Count == 0;
        OnPropertyChanged(nameof(HasNodes));
        foreach (var node in Flatten(Nodes))
            if (node.IsEdit && node.Children.Count > 0
                && openEdits.Contains(EditBusy(node.Part!, node.EditDefinitionId!)))
                node.IsExpanded = true;
        ApplyFilter();
        ShowBusy();
        RestoreSelection(keepPart, keepEdit, keepKind, keepSubject, keepOutfit, keepMaterial);
    }

    private void Reselect(TargetPart part, string? editId, EditNodeKind? kind)
    {
        // A verb run from one of the edit's material rows leaves the modder on it. The verbs name the EDIT
        // because that is what they write through; the material row is the same edit at one material, and
        // the rebuild's own restore has already put the selection back there.
        if (kind == EditNodeKind.Edit && SelectedNode is { IsMaterial: true, Part: { } onPart } material
            && onPart.SameAs(part)
            && string.Equals(material.EditDefinitionId, editId, StringComparison.Ordinal))
            return;
        RestoreSelection(part, editId, kind, "", "");
    }

    /// <summary>Select one part's row from ANOTHER surface — ③ Build's row → Edit hop. The tree is a fresh
    /// read of the session and is rebuilt whenever the step is entered, so the row is found by identity here
    /// rather than held. A part this tree does not carry leaves the selection where it is and says so on this
    /// page's own line: the hop otherwise arrives with nothing selected and nothing said.</summary>
    public void SelectPart(TargetPart part, string? label = null)
    {
        if (Locate(part, null, null, "", "") is { } row) { SelectedNode = row; return; }
        // Named as the row would have been named: the install's short token, which is the part's title in
        // this tree. The renderer slot is the model's address for it and the row's dim subtitle at most.
        Status = $"{(string.IsNullOrEmpty(label) ? Token(part) : label)} isn't in this list.";
    }

    /// <summary>Land a ③ Build hop on the exact edit row, not merely its part.</summary>
    public void SelectEdit(EditRef edit)
    {
        if (Locate(edit.Part, edit.EditDefinitionId, EditNodeKind.Edit, "", "") is { } row)
        { SelectedNode = row; return; }
        Status = $"{edit.Label} isn't in this list.";
    }

    /// <inheritdoc cref="SelectPart"/>
    /// <summary>The same hop onto a whole subject — what opening one from ① Pick asks for.</summary>
    public void SelectSubject(string subject, string outfit)
    {
        if (Locate(null, null, EditNodeKind.Subject, subject, outfit) is { } row)
        { SelectedNode = row; return; }
        // The friendly label, through the shell's one naming home — the same one the row it failed to find
        // would have carried. An internal character key is not a name the modder ever chose or read.
        Status = $"{_shell.SubjectLabel(subject, outfit)} isn't in this list.";
    }

    /// <summary>Put the selection back on what it was on. A miss clears it, which is what a rebuild that
    /// dropped the row it was on means; a hop asked for from another surface keeps what is there instead and
    /// reports, which is why the search itself is <see cref="Locate"/>.</summary>
    private void RestoreSelection(TargetPart? part, string? editId, EditNodeKind? kind, string subject,
        string outfit, int materialOrdinal = -1)
    {
        // Marked as the page's own doing, not the modder's: a rebuild puts the selection back on the row it
        // was on, and anything that treats that as arriving on the row would run again on the redraw its
        // own work causes. What the modder selecting a row is allowed to do is in OnSelectedNodeChanged.
        _restoringSelection = true;
        try
        {
            SelectedNode = Locate(part, editId, kind, subject, outfit, materialOrdinal);
            // A selection landed on a material row is under its edit's expander; a restore that left the
            // branch closed would select a row the tree does not show.
            if (SelectedNode is { IsMaterial: true, Part: { } onPart } material)
                Flatten(Nodes).FirstOrDefault(n => n.IsEdit && n.Part is not null && n.Part.SameAs(onPart)
                        && string.Equals(n.EditDefinitionId, material.EditDefinitionId,
                            StringComparison.Ordinal))
                    ?.IsExpanded = true;
        }
        finally { _restoringSelection = false; }
    }

    private bool _restoringSelection;

    /// <summary>The row one identity names, or null. A part or edit is found by its own identity, an edit's
    /// material row by its place in the edit's pane; a subject or its skeleton row by which subject's branch
    /// it belongs to, since neither carries a part. A material the edit no longer has falls back to the edit
    /// row, and a gone edit to its part — the nearest surface that still exists.</summary>
    private EditNodeVm? Locate(TargetPart? part, string? editId, EditNodeKind? kind, string subject,
        string outfit, int materialOrdinal = -1)
    {
        var rows = Flatten(Nodes).ToList();
        if (part is not null)
            return rows.FirstOrDefault(n => n.Kind == kind && n.Part is not null && n.Part.SameAs(part)
                    && string.Equals(n.EditDefinitionId, editId, StringComparison.Ordinal)
                    && (kind != EditNodeKind.Material || n.MaterialOrdinal == materialOrdinal))
                ?? (kind == EditNodeKind.Material
                    ? rows.FirstOrDefault(n => n.IsEdit && n.Part is not null && n.Part.SameAs(part)
                        && string.Equals(n.EditDefinitionId, editId, StringComparison.Ordinal))
                    : null)
                ?? rows.FirstOrDefault(n => n.IsPart && n.Part is not null && n.Part.SameAs(part));
        return kind is EditNodeKind.Subject or EditNodeKind.Skeleton
            ? rows.FirstOrDefault(n => n.Kind == kind
                && string.Equals(n.Subject, subject, StringComparison.Ordinal)
                && string.Equals(n.Outfit, outfit, StringComparison.Ordinal))
            : null;
    }

    private static IEnumerable<EditNodeVm> Flatten(IEnumerable<EditNodeVm> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    // ---- tree ----

    /// <summary>The tree, over BOTH of the project's two answers about what is in the mod.
    ///
    /// <para>The authored outline knows a subject and a part only once something has been authored against
    /// it, and the only way to author a first thing is a part row — so a mod read from the outline alone can
    /// never mint anything. The other answer is the mod's own SELECTION and the install's parts under it,
    /// which is what a freshly picked subject has and all it has. Both are read here: every selected subject
    /// gets a row, every part the install names under one gets a row, and a part the project has authored
    /// against keeps its outline entry.</para>
    ///
    /// <para>A part the project holds that this install does not name is still the mod's and still has its
    /// edits, so it follows the install's own. With no install the whole tree is the outline again, over
    /// subject rows the selection puts there — and the unavailable line says why the parts are missing.</para>
    /// </summary>
    private IEnumerable<EditNodeVm> BuildSubjects(EditBoardSnapshot board)
    {
        var project = board.Project;
        var authored = board.KnownParts.Concat(board.Edits.Select(edit => edit.Target))
            .GroupBy(target => $"{target.Subject}\u001f{target.Outfit}\u001f{target.RendererSlot}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .GroupBy(target => (Subject: target.Subject, Outfit: target.Outfit))
            .ToList();
        var subjects = new List<(string Subject, string Outfit)>();
        var seenSubject = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The tree's subject order is the order they were added to the mod — the selection list, which is
        // insertion-ordered and stable. Authoring against a subject must not move its row, so the authored
        // groups only APPEND, for the one shape the selection cannot name (a legacy project holding edits
        // on a subject its workspace index does not list).
        foreach (var key in (project.WorkspaceIndex?.Selection
                         .Select(entry => (Subject: entry.Character, Outfit: entry.Outfit))
                         ?? Enumerable.Empty<(string Subject, string Outfit)>())
                     .Concat(authored.Select(group => group.Key)))
            if (seenSubject.Add(key.Subject + "" + key.Outfit)) subjects.Add(key);

        // Which of these rows the friendly label cannot tell apart. The stem is the app's internal key for
        // an outfit and the modder never wrote it, so it is off the row — except where two rows in THIS
        // list would otherwise read alike, which is the whole of what the stem was doing for the reader.
        // Asked of the tree rather than of the roster: a name shared with an outfit nowhere in this mod
        // costs the person reading this list nothing, and the roster is not something the page holds.
        var ambiguous = subjects
            .Select(subject => _shell.SubjectLabel(subject.Subject, subject.Outfit))
            .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var subject in subjects)
        {
            var held = authored.FirstOrDefault(group =>
                string.Equals(group.Key.Subject, subject.Subject, StringComparison.OrdinalIgnoreCase)
                && string.Equals(group.Key.Outfit, subject.Outfit, StringComparison.OrdinalIgnoreCase))
                ?.ToList() ?? new List<TargetPart>();
            var parts = new List<EditNodeVm>();
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in _shell.SubjectParts(subject.Subject, subject.Outfit))
            {
                named.Add(target.RendererSlot);
                parts.Add(BuildPart(board, target, board.EditsFor(target)));
            }
            foreach (var target in held.Where(part => !named.Contains(part.RendererSlot)))
                parts.Add(BuildPart(board, target, board.EditsFor(target)));

            string label = _shell.SubjectLabel(subject.Subject, subject.Outfit);
            var root = new EditNodeVm
            {
                Kind = EditNodeKind.Subject,
                Title = label,
                Subject = subject.Subject,
                Outfit = subject.Outfit,
                InspectorHeader = label,
            };
            root.Detail = Join(ambiguous.Contains(label) ? subject.Outfit : "",
                Count(parts.Count, "part"));
            root.InspectorDetail = root.Detail;
            foreach (var part in parts) root.Children.Add(part);

            if (_shell.ReadSkeleton(subject.Subject, subject.Outfit) is { } skeleton)
                root.Children.Add(new EditNodeVm
                {
                    Kind = EditNodeKind.Skeleton,
                    Title = "Skeleton",
                    Subject = subject.Subject,
                    Outfit = subject.Outfit,
                    Detail = Count(skeleton.BoneCount, "bone"),
                    InspectorHeader = "Skeleton",
                    InspectorDetail = Count(skeleton.BoneCount, "bone"),
                    SkeletonTree = skeleton.Bones,
                });

            root.HasEditBadge = parts.Any(p => p.HasEditBadge);
            // The badge rolls up; the sentence does not. A subject row saying one of its parts' refusals
            // reads as its own, and the fix is on that part.
            root.HasProblemUnder = parts.Any(p => p.HasProblemBadge);
            yield return root;
        }
    }

    private EditNodeVm BuildPart(EditBoardSnapshot board, TargetPart part,
        IReadOnlyList<AuthoredEditOutlineEntry> edits)
    {
        // The part's own name for the tree: the install's short token, with the renderer slot under it, which
        // is the shipped workbench's part row exactly. An install that cannot name the token leaves the
        // renderer slot standing as the title rather than an empty one.
        string token = Token(part);
        // The lod0 mesh the project recorded when it opened the part. It is a label, never identity, and it
        // is off the row now that the slot is the row's own subtitle — the filter still finds a part by it.
        string mesh = board.Lod0MeshName(part);

        // Content edits in the order the project holds them, then the hide, which is how the tree mockup
        // reads and how a part's answers rank: what it draws, then what takes it off screen.
        var ordered = edits.Where(e => e.Kind == EditDefinitionKind.Content)
            .Concat(edits.Where(e => e.Kind == EditDefinitionKind.Hide)).ToList();

        var node = new EditNodeVm
        {
            Kind = EditNodeKind.Part,
            Title = token,
            Part = part,
            Subject = part.Subject,
            Outfit = part.Outfit,
            InspectorHeader = token,
            Detail = part.RendererSlot,
            FilterExtra = Join(part.RendererSlot, mesh),
        };
        node.InspectorDetail = Join(part.RendererSlot,
            ordered.Count == 0 ? "no edits yet" : Count(ordered.Count, "edit"));
        node.Problem = _refusals.TryGetValue(Key(part), out string? refusal) ? refusal : null;
        node.MeshEditBlock = _meshEditBlocks.TryGetValue(Key(part), out string? meshBlock) ? meshBlock : null;

        for (int i = 0; i < ordered.Count; i++)
        {
            var edit = ordered[i];
            var child = BuildEdit(board, part, edit, i + 1, ordered.Count);
            node.Children.Add(child);
            node.Overview.Add(child);
        }

        // A part with no edits shows its ORIGINAL maps at the grain an edit's cards use. Their own Open and
        // Choose controls start the first edit; a drop can do the same as a secondary gesture. What the
        // install has said decides what stands there: the cards, the line that says the read is running, or
        // the line that says what settled.
        if (ordered.Count == 0)
        {
            var resolved = ResolvedPart(part);
            node.IsReadingOriginals = resolved is null && _resolveReads.ContainsKey(Key(part));
            foreach (var map in BuildOriginalMapGroups(part, resolved)) node.MapGroups.Add(map);
            // Four states, not three: a read that FAILED is not the game files being without the part.
            // The failed read is the one a retry can fix, and saying it as "this part isn't in the current
            // game files" is a lie about the install told for the rest of the session.
            node.OriginalsNote = node.IsReadingOriginals || node.MapGroups.Count > 0 ? null
                : _resolveFailures.Contains(Key(part)) ? EditNodeVm.OriginalsReadFailed
                : resolved is null ? EditNodeVm.OriginalsNotInstalled : EditNodeVm.OriginalsUnreadable;
        }

        node.HasEditBadge = ordered.Any(e => e.Kind == EditDefinitionKind.Content);
        // A part previews the original geometry: what its edits start from.
        AdoptMesh(node, MeshKey(part, null));
        return node;
    }

    private EditNodeVm BuildEdit(EditBoardSnapshot board, TargetPart part, AuthoredEditOutlineEntry edit,
        int position, int total)
    {
        var reference = new EditRef(part, edit.Id, edit.Label);
        // Whether Revert mesh has anything to take back, asked of the bindings rather than assumed: an edit
        // that asks the game for its own geometry is already where a revert would land.
        bool meshEdited = edit.Kind == EditDefinitionKind.Content
            && board.Slots(edit.Id).Any(state => state.Slot.Input == TargetInputKind.Geometry
                && state.Binding.Kind != BindingKind.TargetGameValue);
        var node = new EditNodeVm
        {
            Kind = EditNodeKind.Edit,
            Title = edit.Label,
            Part = part,
            EditDefinitionId = edit.Id,
            EditKind = edit.Kind,
            ReturnWarning = edit.ReturnWarning,
            Subject = part.Subject,
            Outfit = part.Outfit,
            InspectorHeader = $"{Token(part)} · {edit.Label}",
            Detail = edit.Kind == EditDefinitionKind.Hide ? "hides this part" : "",
            EditLabel = edit.Label,
            HasEditBadge = edit.Kind == EditDefinitionKind.Content,
            HasMeshEdit = meshEdited,
        };
        node.InspectorDetail = edit.Kind == EditDefinitionKind.Hide
            ? $"hide edit on {Token(part)}"
            : $"edit {position} of {total} on {Token(part)}";
        node.PlacementSummary = EditNodeVm.Uses(edit.Placements.Any(placement => placement.IsAlways),
            edit.Placements.Count(placement => !placement.IsAlways));

        // A hide binds visibility and nothing else, so it has no cards by construction — the model's own
        // rule, not a case handled here.
        if (edit.Kind == EditDefinitionKind.Content)
        {
            foreach (var map in BuildMapGroups(board, reference, meshEdited)) node.MapGroups.Add(map);
            AdoptMesh(node, MeshKey(part, edit.Id));
            // The edit inspector carries the same two Blender opens, so it carries the same gate.
            node.MeshEditBlock = _meshEditBlocks.TryGetValue(Key(part), out string? meshBlock)
                ? meshBlock : null;

            // Each of the edit's own materials is also a child row, closed by default, whose inspector is
            // that one material's slice of the pane above: the SAME group object, so its cards, thumbs and
            // shading row are the edit's rather than copies of them. The materials are the edit's — the
            // groups its pane draws — not a stock enumeration, so a replacement lists what its ranges fold
            // onto. The branch starts collapsed; RebuildLocked re-opens the rows the modder had open.
            for (int m = 0; m < node.MapGroups.Count; m++)
            {
                var group = node.MapGroups[m];
                var material = new EditNodeVm
                {
                    Kind = EditNodeKind.Material,
                    Title = group.Title,
                    Part = part,
                    EditDefinitionId = edit.Id,
                    EditKind = edit.Kind,
                    EditRefLabel = edit.Label,
                    MaterialOrdinal = m,
                    Subject = part.Subject,
                    Outfit = part.Outfit,
                    InspectorHeader = $"{Token(part)} · {edit.Label} · {group.Title}",
                };
                material.InspectorDetail =
                    $"material {m + 1} of {node.MapGroups.Count} on {edit.Label}";
                material.MapGroups.Add(group);
                node.Children.Add(material);
            }
            if (node.Children.Count > 0) node.IsExpanded = false;
        }
        return node;
    }

    /// <summary>The selected edit's cards at the installed material-position grain. Texture-only edits
    /// use stock slots; replacement edits use recorded output sets folded by the emitter's canonical rule.
    ///
    /// <para>A replacement with NO output rows of its own falls back to the part's game-domain positions.
    /// That is not a defect state: a released mod whose Replace recorded no donor textures converts to
    /// exactly this shape, and the alternative is an edit that draws no cards, no material groups and no
    /// shading rows at all. Those cards STAND IN: the build drops every game-texture picture bound on a
    /// replacement, so a picture landed there would never ship, and the shading row beside them is the one
    /// thing at that position the build does read. They say what they are and take nothing.</para>
    /// </summary>
    private IEnumerable<EditMapGroupVm> BuildMapGroups(EditBoardSnapshot board, EditRef edit,
        bool meshEdited)
    {
        var all = board.Slots(edit.EditDefinitionId);
        var slots = all
            .Where(state => IsTextureInput(state.Slot.Input)
                && state.Slot.MaterialBindingPresent != false)
            .ToList();

        var outputs = meshEdited
            ? slots.Where(state => state.Slot.Domain == TargetSlotDomain.EditOutput
                && state.Slot.SubmeshIndex is not null).ToList()
            : new List<EditSlotState>();

        if (outputs.Count == 0)
        {
            foreach (var group in slots.Where(state => state.Slot.Domain == TargetSlotDomain.Game)
                         .GroupBy(state => state.Slot.MaterialSlotIndex ?? state.Slot.SubmeshIndex ?? 0)
                         .OrderBy(group => group.Key))
            {
                var cards = group.OrderBy(state => Order(state.Slot.Input))
                    .ThenBy(state => state.Slot.ShaderProperty ?? "", StringComparer.Ordinal)
                    .Select(state => BuildCard(all, edit, state, gameMaterialPosition: null,
                        role: meshEdited ? EditCardRole.StandIn : EditCardRole.Edited))
                    .ToList();
                string title = group.Select(state => state.Slot.Material?.Name)
                    .FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? $"material {group.Key}";
                yield return new EditMapGroupVm(title, new[] { new EditMapSetVm("", cards) },
                    BuildShadingRow(edit, group.Key, title, all));
            }
            yield break;
        }

        var resolved = ResolvedPart(edit.Part);
        // The fold needs the install's drawable pattern. Without an install the cards still show —
        // they are the project's own files — each submesh standing at its own position uncorrected.
        DrawShapeSet? target = null;
        if (resolved?.MaterialIndexCounts is { Count: > 0 } counts)
        {
            int first = 0;
            var shapes = new List<DrawShape>(counts.Count);
            foreach (int count in counts)
            {
                shapes.Add(new DrawShape(first, count));
                first += count;
            }
            target = new DrawShapeSet(shapes, first);
        }
        var folded = outputs
            .GroupBy(state => state.Slot.SubmeshIndex!.Value)
            .Select(group => new
            {
                Submesh = group.Key,
                MaterialPosition = target is null ? group.Key
                    : DrawMaterialFold.TargetMaterialPosition(target, group.Key),
            })
            .Where(set => set.MaterialPosition >= 0)
            .Select(set => new
            {
                set.Submesh,
                set.MaterialPosition,
                Cards = outputs.Where(state => state.Slot.SubmeshIndex == set.Submesh)
                    .OrderBy(state => Order(state.Slot.Input))
                    .ThenBy(state => state.Slot.ShaderProperty ?? "", StringComparer.Ordinal)
                    .Select(state => BuildCard(all, edit, state, set.MaterialPosition)).ToList(),
            })
            .GroupBy(set => set.MaterialPosition)
            .OrderBy(group => group.Key)
            .ToList();

        foreach (var group in folded)
        {
            var donorSets = group.OrderBy(set => set.Submesh).ToList();
            bool foldMany = donorSets.Count > 1;
            var sets = donorSets.Select(set => new EditMapSetVm(
                foldMany ? $"submesh {set.Submesh}" : "", set.Cards)).ToList();
            string? materialName = resolved?.Materials?.FirstOrDefault(material =>
                material.MaterialSlotIndex == group.Key)?.Material.Name;
            string title = string.IsNullOrEmpty(materialName) ? $"material {group.Key}" : materialName;
            yield return new EditMapGroupVm(title, sets,
                BuildShadingRow(edit, group.Key, title, all));
        }

        // Keep the installed inventory truthful even where no replacement output folds onto a material.
        // These are stand-ins: the replacement draws none of their pictures. A measured surplus/zero-count
        // position additionally carries the no-draw refusal on each card rather than disappearing.
        var represented = folded.Select(group => group.Key).ToHashSet();
        foreach (var group in slots.Where(state => state.Slot.Domain == TargetSlotDomain.Game
                     && !represented.Contains(state.Slot.MaterialSlotIndex
                         ?? state.Slot.SubmeshIndex ?? 0))
                 .GroupBy(state => state.Slot.MaterialSlotIndex ?? state.Slot.SubmeshIndex ?? 0)
                 .OrderBy(group => group.Key))
        {
            var cards = group.OrderBy(state => Order(state.Slot.Input))
                .ThenBy(state => state.Slot.ShaderProperty ?? "", StringComparer.Ordinal)
                .Select(state => BuildCard(all, edit, state, gameMaterialPosition: null,
                    role: EditCardRole.StandIn)).ToList();
            string title = group.Select(state => state.Slot.Material?.Name)
                .FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? $"material {group.Key}";
            yield return new EditMapGroupVm(title, new[] { new EditMapSetVm("", cards) },
                BuildShadingRow(edit, group.Key, title, all));
        }
    }

    /// <summary>A bare part's own inspector cards: the ORIGINAL maps the install draws, one group per
    /// material position, at the same grain and in the same order an edit's are. Each card is where its own
    /// Open, Choose or drop can start the part's first edit, bound to exactly that map's place.
    ///
    /// <para>They come from the install rather than from the project, because a part nobody has touched has
    /// no slots in the project to read. A material position the install answers for but has no readable maps
    /// under keeps its heading and says so: the modder counts positions in this list, and a material that
    /// quietly vanished from it would move every one below it.</para></summary>
    private IEnumerable<EditMapGroupVm> BuildOriginalMapGroups(TargetPart part,
        LegacyResolvedPart? resolved)
    {
        if (resolved is null) yield break;
        foreach (var material in (resolved.Materials ?? Array.Empty<LegacyResolvedMaterial>())
                     .OrderBy(candidate => candidate.MaterialSlotIndex))
        {
            var inputs = new HashSet<string>(StringComparer.Ordinal);
            var cards = new List<EditMapCardVm>();
            foreach (var texture in (material.Textures ?? Array.Empty<LegacyResolvedTexture>())
                         .Where(candidate => IsTextureInput(candidate.Input))
                         .OrderBy(candidate => Order(candidate.Input))
                         .ThenBy(candidate => candidate.ShaderProperty ?? "", StringComparer.Ordinal))
            {
                string identity = !string.IsNullOrWhiteSpace(texture.ShaderProperty)
                    ? texture.ShaderProperty!
                    : "\u001f" + texture.Input;
                if (!inputs.Add(identity)) continue;
                cards.Add(BuildOriginalCard(part, material, texture, resolved));
            }
            string title = string.IsNullOrEmpty(material.Material.Name)
                ? $"material {material.MaterialSlotIndex}" : material.Material.Name;
            yield return new EditMapGroupVm(title, new[] { new EditMapSetVm("", cards) },
                BuildBareShadingRow(part, material.MaterialSlotIndex, title, material.Material),
                note: cards.Count == 0 ? EditMapGroupVm.NoMapsRead : null);
        }
    }

    /// <summary>One original card. It stands on a PART rather than on an edit, so the slot it carries names
    /// the part, the material position and the input and nothing else — there is no edit to address and no
    /// slot id to name yet. The shell resolves those structural coordinates only when a save or Apply has
    /// content to publish.</summary>
    private EditMapCardVm BuildOriginalCard(TargetPart part, LegacyResolvedMaterial material,
        LegacyResolvedTexture texture, LegacyResolvedPart resolved)
    {
        var reference = new EditSlotRef(new EditRef(part, "", ""), "", texture.Input,
            TargetSlotDomain.Game, material.MaterialSlotIndex, material.Material.Name, null,
            ShaderProperty: texture.ShaderProperty,
            HasDrawableCarrier: HasDrawableCarrier(resolved, material.MaterialSlotIndex));
        var sharing = SharingOf(reference);
        var card = new EditMapCardVm(reference, BindingKind.TargetGameValue, boundFile: null,
            gameTextureName: _shell.GameTextureName(reference), role: EditCardRole.FirstEdit,
            sharing: sharing.Kind, sharingUses: sharing.Uses, subjectRead: sharing.Read);
        AdoptThumb(card);
        return card;
    }

    /// <summary>How far an edit to the game texture behind one slot would reach across the item — what the
    /// install says about the item and its use count, read through the boundary's one rule.
    ///
    /// <para>An item the install has not answered for yet answers UNKNOWN rather than one use. The read
    /// lands on a worker after the page is already on screen, so treating the window before it as "nothing
    /// else draws this" is exactly how an every-part edit gets made without anything on screen saying
    /// so.</para>
    /// </summary>
    private readonly record struct CardSharing(EditTextureSharing Kind, int? Uses, EditSubjectRead Read);

    private CardSharing SharingOf(EditSlotRef slot)
    {
        int? uses = _shell.TextureUses(slot);
        var read = _shell.SubjectRead(slot.Edit.Part);
        return new CardSharing(EditMapCardVm.SharingFor(slot, read, uses), uses, read);
    }

    /// <summary>The shading row under one material group: what the edit sets at that position today.
    /// Whether the position supports any values at all is the install's answer, read when a dialog opens
    /// — the row itself must stay cheap enough to build on every redraw.</summary>
    private static EditShadingRowVm BuildShadingRow(EditRef edit, int index, string materialLabel,
        IReadOnlyList<EditSlotState> all)
    {
        var authored = new Dictionary<string, string>(StringComparer.Ordinal);
        var slotIds = new List<string>();
        foreach (var state in all.Where(state => state.Slot.Input == TargetInputKind.MaterialValue
                     && state.Slot.Domain == TargetSlotDomain.Game
                     && (state.Slot.MaterialSlotIndex ?? state.Slot.SubmeshIndex) == index
                     && state.Binding.Kind is BindingKind.ProjectAsset or BindingKind.SourceSlot))
        {
            if (state.Slot.Semantic is not { Length: > 0 } semantic) continue;
            authored[semantic] = state.Binding.Kind == BindingKind.ProjectAsset
                ? state.ProjectAsset?.Value?.Value ?? ""
                : "";
            slotIds.Add(state.Slot.Id);
        }
        return new EditShadingRowVm
        {
            Edit = edit,
            Part = edit.Part,
            MaterialSlotIndex = index,
            MaterialLabel = materialLabel,
            Material = all.Select(state => state.Slot).FirstOrDefault(slot =>
                slot.Domain == TargetSlotDomain.Game
                && (slot.MaterialSlotIndex ?? slot.SubmeshIndex) == index
                && slot.Material is not null)?.Material,
            AuthoredValues = authored,
            AuthoredSlotIds = slotIds,
        };
    }

    /// <summary>The same row shape for a part with no edit. Empty authored state disables only Revert; the
    /// two dialog commands bridge the missing edit after, and only after, a committed effective answer.</summary>
    private static EditShadingRowVm BuildBareShadingRow(TargetPart part, int index, string materialLabel,
        GameAssetRef material) =>
        new()
        {
            Edit = new EditRef(part, "", ""),
            Part = part,
            MaterialSlotIndex = index,
            MaterialLabel = materialLabel,
            Material = material,
            AuthoredValues = new Dictionary<string, string>(StringComparer.Ordinal),
            AuthoredSlotIds = Array.Empty<string>(),
        };

    /// <param name="gameMaterialPosition">Which installed material position this slot's output draws at,
    /// where the group's fold answered. Null on a game-domain slot, whose own position is the answer.</param>
    /// <param name="role">Which of the three cards this is. A game-domain card on a mesh-edited edit is a
    /// stand-in; everything else the session addresses is the edit's own.</param>
    private EditMapCardVm BuildCard(IReadOnlyList<EditSlotState> all, EditRef edit, EditSlotState state,
        int? gameMaterialPosition, EditCardRole role = EditCardRole.Edited)
    {
        var reference = new EditSlotRef(edit, state.Slot.Id, state.Slot.Input, state.Slot.Domain,
            state.Slot.MaterialSlotIndex, state.Slot.Material?.Name, state.ProjectAsset?.File,
            state.Binding.Kind,
            state.Binding.SourceSlot is { } source
                ? new EditSlotSource(source.EditDefinitionId, source.SlotId) : null,
            gameMaterialPosition,
            state.Slot.SubmeshIndex,
            state.Slot.ShaderProperty,
            HasDrawableCarrier(ResolvedPart(edit.Part), gameMaterialPosition
                ?? state.Slot.MaterialSlotIndex ?? state.Slot.SubmeshIndex));
        var sharing = role == EditCardRole.StandIn
            ? new CardSharing(EditTextureSharing.Private, null, _shell.SubjectRead(edit.Part))
            : SharingOf(reference);
        var card = new EditMapCardVm(reference, state.Binding.Kind, state.ProjectAsset?.File,
            // The game's own texture is the install's answer, not the project's: a slot naming no file of the
            // mod's would otherwise be the one card with nothing on it. An output KEEPING the original map,
            // and one taking the value of a slot that resolves to one, stand on that map's name for the same
            // reason.
            state.Binding.Kind is BindingKind.TargetGameValue or BindingKind.InheritedLiveCarrier
                or BindingKind.SourceSlot
                ? _shell.GameTextureName(reference) : null,
            state.Slot.Input == TargetInputKind.Rmo ? RmoAlphaOf(all, state.Slot) : null,
            role,
            // A stand-in takes no gesture at all, so what its texture is shared with decides nothing.
            sharing.Kind,
            sharing.Uses,
            sharing.Read,
            boundLabel: state.ProjectAsset?.Label);
        AdoptThumb(card);
        return card;
    }

    /// <summary>The emissive-mask answer recorded for one RMO card's submesh, or null where none is. The
    /// answer is its own slot in the model, so it is read from there rather than inferred from the picture
    /// beside it. The way to change it is the round trip that asked the question.</summary>
    private static RmoAlphaAnswer? RmoAlphaOf(IReadOnlyList<EditSlotState> all, TargetSlot rmo)
    {
        var alpha = all.FirstOrDefault(state => state.Slot.Input == TargetInputKind.RmoAlpha
            && state.Slot.Domain == rmo.Domain
            && state.Slot.SubmeshIndex == rmo.SubmeshIndex
            && state.Slot.MaterialSlotIndex == rmo.MaterialSlotIndex);
        return alpha?.ProjectAsset?.Value?.Value switch
        {
            "rebuild-from-stock" => RmoAlphaAnswer.Rebuild,
            "ship-as-authored" => RmoAlphaAnswer.ShipAsAuthored,
            _ => null,
        };
    }

    private static int Order(TargetInputKind input) => input switch
    {
        TargetInputKind.BaseColor => 0,
        TargetInputKind.Normal => 1,
        TargetInputKind.Rmo => 2,
        TargetInputKind.Blend => 3,
        TargetInputKind.Ramp => 4,
        _ => 5,
    };

    private static bool IsTextureInput(TargetInputKind input) => input is
        TargetInputKind.BaseColor or TargetInputKind.Normal or TargetInputKind.Rmo
        or TargetInputKind.Blend or TargetInputKind.Ramp or TargetInputKind.Texture;

    private static bool HasDrawableCarrier(LegacyResolvedPart? resolved, int? materialPosition)
    {
        if (resolved?.MaterialIndexCounts is not { } counts || materialPosition is null) return true;
        return materialPosition >= 0 && materialPosition < counts.Count
            && counts[materialPosition.Value] > 0;
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Join(params string[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>What the page calls one part: the install's short token where it has one, the renderer slot
    /// where it does not.</summary>
    private string Token(TargetPart part)
    {
        string token = _shell.PartToken(part) ?? "";
        return token.Length > 0 ? token : part.RendererSlot;
    }

    private string PartTitle(TargetPart part) => Token(part);

    // A part the install refused, kept by part so the sentence survives the next status line. Cleared for a
    // part the moment one of its routes succeeds.
    private readonly Dictionary<string, string> _refusals = new(StringComparer.Ordinal);

    // The mesh-edit gate's BLOCKED answers by part, so rebuilds keep the sentence without re-reading the
    // bundle. A clear answer is never kept here: the page cannot tell "the mesh is fine" from "the game
    // was holding the file", and the shell's own per-install gate already makes a re-ask cheap while
    // letting a locked bundle heal on the next selection. In-flight reads sit beside them so a click
    // awaits the read its selection already started rather than starting a second one.
    private readonly Dictionary<string, string?> _meshEditBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<string?>> _meshEditReads = new(StringComparer.Ordinal);
    // Bumped when the page is handed another session, so a read still in flight settles nothing there.
    private int _meshEditEpoch;

    /// <summary>The mesh-edit gate for one part: the settled answer, or the read the first ask
    /// started.</summary>
    private Task<string?> MeshEditBlockAsync(TargetPart part)
    {
        string key = Key(part);
        if (_meshEditBlocks.TryGetValue(key, out string? settled)) return Task.FromResult(settled);
        if (_meshEditReads.TryGetValue(key, out var inFlight)) return inFlight;
        var read = ReadMeshEditBlockAsync(part, key);
        if (!read.IsCompleted) _meshEditReads[key] = read;
        return read;
    }

    private async Task<string?> ReadMeshEditBlockAsync(TargetPart part, string key)
    {
        int epoch = _meshEditEpoch;
        string? answer;
        try { answer = await _shell.MeshEditBlockAsync(part); }
        catch { answer = null; }   // unreadable is not blocked; that failure has its own loud route
        if (epoch != _meshEditEpoch) return answer;   // another session took the page mid-read
        _meshEditReads.Remove(key);
        if (answer is not null) _meshEditBlocks[key] = answer;
        foreach (var node in Flatten(Nodes))
            if ((node.IsPart || node.IsContentEdit) && node.Part is { } p && Key(p) == key)
                node.MeshEditBlock = answer;
        return answer;
    }

    // The install's answer for one part, by part. A resolve deobfuscates that part's bundles on a cold
    // install — seconds — and every redraw asks for it: once for each mesh-edited edit's fold, once for each
    // bare part's original cards. So it is read OFF the UI thread and kept here, in-flight reads beside the
    // settled ones so a second ask joins the first rather than starting a second read. A null answer is
    // settled too: "this install does not have the part" is an answer, and re-asking it every redraw is the
    // same cost as never memoizing at all. What retires them is a new install or a new session, both of
    // which reach ForgetInstallReads.
    private readonly Dictionary<string, LegacyResolvedPart?> _resolvedParts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<LegacyResolvedPart?>> _resolveReads = new(StringComparer.Ordinal);
    // The parts whose read THREW rather than answering. Settled alongside the answers above so the redraw
    // it causes does not ask again and fail again in a loop — and kept apart from them because the two are
    // different news: one is an install without the part, the other is a read that can be run again.
    private readonly HashSet<string> _resolveFailures = new(StringComparer.Ordinal);
    private int _resolveEpoch;
    private int _resolveRebuildGeneration;

    /// <summary>Drop what the page memoized about the INSTALL. Called where the window replaces the install
    /// the answers were read off — a force rescan — and when the page is handed another session.</summary>
    public void ForgetInstallReads()
    {
        // Under the same gate the reads settle under: a read landing on a worker writes these three, and a
        // clear that interleaved with one would leave the settled answer of an install nobody holds any more.
        lock (_changeGate)
        {
            _resolvedParts.Clear();
            _resolveReads.Clear();
            _resolveFailures.Clear();
            _resolveEpoch++;
            Interlocked.Increment(ref _resolveRebuildGeneration);
        }
    }

    /// <summary>Ask the install for one part's original maps again, where the last ask FAILED. The card
    /// area's own way out — the failed-read line sends the modder back to the row, exactly as the mesh
    /// preview's does — so the answer is dropped and the redraw starts a fresh read.
    ///
    /// <para>Only a failure is retried, and the answer is whether one was: a settled "the install does not
    /// have this part" is an answer, and re-reading it on every visit to the row is the cost the memo
    /// exists to avoid.</para></summary>
    private bool RetryOriginalsRead(TargetPart part)
    {
        lock (_changeGate)
        {
            string key = Key(part);
            if (!_resolveFailures.Remove(key)) return false;
            _resolvedParts.Remove(key);
        }
        Rebuild();
        return true;
    }

    /// <summary>What the install says about one part, if the page already has the answer. Null means either
    /// "not settled yet" or "the install does not have this part", and both draw the same: the cards the
    /// project's own files can stand for, and nothing the install would have added. An unsettled part starts
    /// its read here, and the redraw that read lands on is what fills those cards in.</summary>
    private LegacyResolvedPart? ResolvedPart(TargetPart part)
    {
        string key = Key(part);
        if (_resolvedParts.TryGetValue(key, out var settled)) return settled;
        if (_resolveReads.ContainsKey(key)) return null;
        var read = ReadResolvedPartAsync(part, key);
        if (!read.IsCompleted) _resolveReads[key] = read;
        return _resolvedParts.TryGetValue(key, out var landed) ? landed : null;
    }

    private async Task<LegacyResolvedPart?> ReadResolvedPartAsync(TargetPart part, string key)
    {
        int epoch = _resolveEpoch;
        LegacyResolvedPart? answer = null;
        // A read that FAILED settles as null too, and is REMEMBERED as a failure. Left unsettled it would
        // have the redraw below ask again, fail again and redraw again with nothing in between; recorded
        // as a plain null answer it would say the install does not have this part, which is a different
        // fact with no way out of it. What retires it is a force rescan or the modder coming back to the
        // row (RetryOriginalsRead) — the way out the card area's own line names.
        bool threw = false;
        try { answer = await _shell.ResolvePartAsync(part); }
        catch { threw = true; }
        // Under the tree's own gate: the read can land on a worker while a redraw is reading these.
        lock (_changeGate)
        {
            if (epoch != _resolveEpoch) return answer;   // another session or install took the page mid-read
            _resolveReads.Remove(key);
            _resolvedParts[key] = answer;
            if (threw) _resolveFailures.Add(key); else _resolveFailures.Remove(key);
            // The cards this answer decides are built with the tree, so the tree is what redraws them. A
            // burst of answers shares one redraw; a rebuild that finds every part settled asks for nothing,
            // so the coalesced redraw cannot chase its own tail.
            QueueResolvedPartsRebuild();
        }
        return answer;
    }

    private void QueueResolvedPartsRebuild()
    {
        // Headless consumers (no dispatcher) keep their synchronous settlement and the thread-safety
        // contract they had before coalescing; the dispatcher-owning page batches a burst onto its queue.
        if (!_coalesceResolvedRebuilds)
        {
            Rebuild();
            return;
        }
        int generation = Interlocked.Increment(ref _resolveRebuildGeneration);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(15));
            if (generation != Volatile.Read(ref _resolveRebuildGeneration)) return;
            _dispatch(() =>
            {
                if (generation != Volatile.Read(ref _resolveRebuildGeneration)) return;
                Rebuild();
            });
        });
    }

    /// <summary>A stable dictionary key for one part. The model's own key is internal to Core, so the page
    /// spells the same three fields — separated, so two parts cannot collide by where one field ends and the
    /// next begins.</summary>
    private static string Key(TargetPart part) =>
        $"{part.Subject}\u0001{part.Outfit}\u0001{part.RendererSlot}".ToUpperInvariant();

    // ---- busy: keyed by what a verb acts on, not by the row that started it ----
    //
    // A rebuild replaces every row, so a flag on the row a verb was handed is gone the moment that verb
    // changes anything. These keys outlive the redraw: the rows read their busy state back off them, a
    // second click on the same thing is refused, and a verb elsewhere cannot clear another's gate.

    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);

    private static string PartBusy(TargetPart part) => Key(part);

    /// <summary>A verb on the whole subject. Its own key rather than a part's: what it acts on is every part
    /// under the subject, so a part verb must not be able to clear it and it must not be able to clear
    /// one.</summary>
    private static string SubjectBusy(string subject, string outfit) =>
        $"{subject}{outfit}".ToUpperInvariant();

    private static string EditBusy(TargetPart part, string editId) => Key(part) + "\u0001" + editId;

    private static string CardBusy(EditSlotRef slot) =>
        EditBusy(slot.Edit.Part, slot.Edit.EditDefinitionId) + "\u0001" + slot.SlotId;

    private bool SubjectIsBusy(string subject, string outfit) =>
        _busy.Contains(SubjectBusy(subject, outfit));

    /// <summary>A verb on this part itself is running. Everything under the part waits on it, because the
    /// part's own verbs are the ones that mint, hide and re-open it — and so does a verb on the subject
    /// above it, which acts on every part it holds.</summary>
    private bool PartIsBusy(TargetPart part) =>
        _busy.Contains(PartBusy(part)) || SubjectIsBusy(part.Subject, part.Outfit);

    private bool EditIsBusy(TargetPart part, string editId) =>
        PartIsBusy(part) || _busy.Contains(EditBusy(part, editId));

    private bool CardIsBusy(EditSlotRef slot) =>
        EditIsBusy(slot.Edit.Part, slot.Edit.EditDefinitionId) || _busy.Contains(CardBusy(slot));

    private string ShadingBusy(EditShadingRowVm row) =>
        row.IsFirstEdit ? PartBusy(row.Part) : EditBusy(row.Part, row.Edit.EditDefinitionId);

    private bool ShadingIsBusy(EditShadingRowVm row) =>
        row.IsFirstEdit ? PartIsBusy(row.Part) : EditIsBusy(row.Part, row.Edit.EditDefinitionId);

    /// <summary>Claim the gate for one verb, or refuse it in the same words the disabled button carries.</summary>
    private bool Take(string key, bool blocked)
    {
        if (blocked || !_busy.Add(key)) { Status = BlenderGate.Busy; return false; }
        ShowBusy();
        return true;
    }

    private void Release(string key)
    {
        _busy.Remove(key);
        ShowBusy();
    }

    /// <summary>Push the gates onto the rows currently drawn — including the ones a rebuild just made.</summary>
    private void ShowBusy()
    {
        foreach (var node in Flatten(Nodes))
        {
            node.IsBusy = node.Part is not null
                ? node.EditDefinitionId is { } id ? EditIsBusy(node.Part, id) : PartIsBusy(node.Part)
                : node.IsSubject && SubjectIsBusy(node.Subject, node.Outfit);
            foreach (var card in node.MapGroups.SelectMany(g => g.Cards)) card.IsBusy = CardIsBusy(card.Slot);
            // The shading row writes through its edit, so it waits on the edit's own gate — the same key
            // the cards above it and the row's own verbs are keyed by. A bare row waits on its part gate,
            // which survives the rebuild that mints its first edit.
            foreach (var shading in node.MapGroups.Select(g => g.Shading).OfType<EditShadingRowVm>())
                shading.IsBusy = ShadingIsBusy(shading);
        }
    }

    /// <summary>Hold the busy gate on one subject from OUTSIDE this page's own verbs, for as long as the
    /// returned handle lives. The Blender return is the one such verb: it is started by a send landing
    /// rather than by a click here, and while it runs it is changing exactly the rows a subject's own Open
    /// would be. It takes the gate that open takes, so the rows say ◌ and the buttons under them wait, and
    /// no second mechanism decides when a row is working.
    ///
    /// <para>It never refuses. What it marks is work already under way somewhere else; releasing a key
    /// this call did not add would clear a verb's own gate, so only what it took is given back.</para>
    ///
    /// <para>Taken and released on the page's thread, as every other write to the gate is.</para></summary>
    public IDisposable HoldSubjects(IReadOnlyList<(string Subject, string Outfit)> subjects)
    {
        var taken = subjects.Select(subject => SubjectBusy(subject.Subject, subject.Outfit))
            .Where(key => _busy.Add(key)).ToList();
        ShowBusy();
        return new SubjectHold(this, taken);
    }

    private sealed class SubjectHold : IDisposable
    {
        private readonly EditPageVm _page;
        private readonly IReadOnlyList<string> _keys;
        private bool _released;

        internal SubjectHold(EditPageVm page, IReadOnlyList<string> keys)
        {
            _page = page;
            _keys = keys;
        }

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            foreach (string key in _keys) _page._busy.Remove(key);
            _page.ShowBusy();
        }
    }

    // ---- filter ----

    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    /// <summary>Substring, case-insensitive, all terms must hit, over part names, edit labels and the texture
    /// names on their cards. A match reveals the row, its ancestors and its descendants.</summary>
    private void ApplyFilter()
    {
        var terms = (Filter ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var root in Nodes) FilterNode(root, terms, ancestorMatched: false);
        NoMatches = terms.Length > 0 && Nodes.Count > 0 && Nodes.All(n => !n.IsVisible);
    }

    private static bool FilterNode(EditNodeVm node, IReadOnlyList<string> terms, bool ancestorMatched)
    {
        if (terms.Count == 0)
        {
            node.IsVisible = true;
            foreach (var child in node.Children) FilterNode(child, terms, ancestorMatched: false);
            return true;
        }

        bool self = node.SelfMatches(terms);
        bool anyChild = false;
        foreach (var child in node.Children) anyChild |= FilterNode(child, terms, ancestorMatched || self);

        node.IsVisible = self || anyChild || ancestorMatched;
        if (anyChild) node.IsExpanded = true;
        return self || anyChild;
    }

    // ---- previews ----
    //
    // Rendered pictures belong to what they are OF, not to the row showing them, so they are held here by
    // identity and handed to each rebuild's rows. A rename redraws the tree and re-renders nothing. The rows
    // borrow; this owns, and disposes whatever the new tree did not take.

    private readonly Dictionary<string, EditMeshPreview> _meshPreviews = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Avalonia.Media.Imaging.Bitmap Image, string Dimensions)> _thumbs =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _livePreviews = new(StringComparer.Ordinal);

    /// <summary>One edit's geometry, or a bare part's own. The part is in the key so two parts' Edit 1 rows
    /// cannot share a render.</summary>
    private static string MeshKey(TargetPart part, string? editId) => Key(part) + "\u0001" + (editId ?? "");

    /// <summary>One card's picture. The bound file is in the key, so a rebind draws the new picture rather
    /// than the one the slot used to name.
    ///
    /// <para>A card that BORROWS another slot's answer names no file of its own, so its key carries the
    /// source's identity and the file that source binds today. Without them nothing in a borrower's key can
    /// move: rebinding or repainting the source would leave the card re-adopting the picture it is already
    /// showing. No route in the app authors a cross-edit borrow today — <see cref="SourceFile"/> answers null
    /// for every source binding the app itself writes — so this is what the model permits, keyed the way the
    /// page files the rest.</para>
    ///
    /// <para>The slot id and its separator stay the head of the key, because that prefix is what
    /// <see cref="InvalidateThumbs"/> drops a slot's pictures by.</para>
    ///
    /// <para>A bare part's original card names no slot yet, so it is filed by what it IS instead: the part,
    /// the material position and the input. Filed under the empty slot id, every one of them would be the
    /// same picture.</para></summary>
    private string ThumbKey(EditSlotRef slot) => slot.SlotId.Length > 0
        ? slot.SlotId + "" + (slot.ProjectRelativeFile ?? "") + BorrowedKeyPart(slot)
        : $"original{Key(slot.Edit.Part)}{slot.MaterialSlotIndex}{slot.Input}"
            + $"{slot.ShaderProperty}";

    /// <summary>What a borrowing card's key adds: which slot of which edit it takes its value from, and the
    /// file that slot binds right now. Empty on every other binding, so no direct card's key moves.</summary>
    private string BorrowedKeyPart(EditSlotRef slot) =>
        slot.Binding == BindingKind.SourceSlot && slot.Source is { } source
            ? "\u0001" + (source.EditDefinitionId ?? "") + "\u0001" + source.SlotId
                + "\u0001" + (SourceFile(source) ?? "")
            : "";

    /// <summary>The mod's own file the slot a source answer names binds today. Null where the source names
    /// the installed game rather than an edit — the game's own texture is fixed under one install, so the two
    /// ids alone say which picture that is — and null where the named edit is gone, which is a card whose
    /// picture is about to be rebuilt anyway.</summary>
    private string? SourceFile(EditSlotSource source)
    {
        if (_board is null || source.EditDefinitionId is not { Length: > 0 } editId) return null;
        try
        {
            return _board.Slot(editId, source.SlotId)?.ProjectAsset?.File;
        }
        catch { return null; }
    }

    private void AdoptMesh(EditNodeVm node, string key)
    {
        node.PreviewKey = key;
        _livePreviews.Add(key);
        if (_meshPreviews.TryGetValue(key, out var preview)) node.SetMeshPreview(preview);
    }

    private void AdoptThumb(EditMapCardVm card)
    {
        string key = ThumbKey(card.Slot);
        card.PreviewKey = key;
        _livePreviews.Add(key);
        if (_thumbs.TryGetValue(key, out var thumb)) card.SetThumb(thumb.Image, thumb.Dimensions);
    }

    private void PrunePreviews()
    {
        foreach (string key in _meshPreviews.Keys.Where(k => !_livePreviews.Contains(k)).ToList())
        {
            _meshPreviews[key].Image.Dispose();
            _meshPreviews.Remove(key);
        }
        foreach (string key in _thumbs.Keys.Where(k => !_livePreviews.Contains(k)).ToList())
        {
            _thumbs[key].Image.Dispose();
            _thumbs.Remove(key);
        }
    }

    /// <summary>Forget every picture the page holds. The rows are told before the bitmaps go: a row left
    /// holding one would draw a disposed handle AND believe it needs no render, which is how a picture the
    /// page has already thrown away survives on screen under an edit that has moved on.</summary>
    private void DropPreviews()
    {
        foreach (var node in Flatten(Nodes))
        {
            // Only rows that were filed under a key can be holding one of these pictures; a subject or
            // skeleton row draws no render and must not be put into a load state it will never leave.
            if (node.PreviewKey.Length > 0) node.ForgetMeshPreview();
            foreach (var card in node.MapGroups.SelectMany(group => group.Cards)) card.ReleaseThumb();
        }
        foreach (var preview in _meshPreviews.Values) preview.Image.Dispose();
        foreach (var (image, _) in _thumbs.Values) image.Dispose();
        _meshPreviews.Clear();
        _thumbs.Clear();
        _livePreviews.Clear();
    }

    /// <summary>Forget the pictures one committed change can have moved, and only those. True where it took
    /// any away, which is what tells the caller a row may now be waiting on a render nobody asked for.
    ///
    /// <para>What a render is made of is one edit's own bindings and the files they name — never another
    /// edit's, and never a placement — so a change that names the edits and slots it touched invalidates
    /// exactly those and leaves every other part's pictures where they are. A change naming neither, or one
    /// that recaptured the whole workspace inventory, drops everything: anything at all can be behind
    /// it.</para></summary>
    private bool InvalidatePreviews(AuthoredProjectChangedEventArgs change)
    {
        if ((change.Invalidation & AuthoredInvalidation.Preview) == 0) return false;
        if (!change.NamesWhatItMoved())
        {
            DropPreviews();
            return true;
        }
        foreach (string editId in change.EditDefinitionIds) InvalidateEditMesh(editId);
        foreach (string slotId in change.SlotIds) InvalidateThumbs(slotId);
        return true;
    }

    /// <summary>Forget one edit's render, because what it draws may have changed under it. The next selection
    /// asks for it again.</summary>
    private void InvalidateMesh(TargetPart part, string? editId) => ForgetMesh(MeshKey(part, editId));

    /// <summary>The same for an edit named on its own. A render is filed under its part AND its edit, and a
    /// committed change names only the edit, so the key is matched by its edit half — including keys only a
    /// live row still holds, which is the case a cache lookup alone would miss.</summary>
    private void InvalidateEditMesh(string editDefinitionId)
    {
        if (editDefinitionId.Length == 0) return;
        string suffix = "\u0001" + editDefinitionId;
        foreach (string key in _meshPreviews.Keys.Concat(Flatten(Nodes).Select(node => node.PreviewKey))
                     .Where(key => key.EndsWith(suffix, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal).ToList())
            ForgetMesh(key);
    }

    /// <summary>Drop one filed render and tell every row showing it. Both halves run whether or not the cache
    /// still holds it: a row can be carrying a picture the cache has already let go of.</summary>
    private void ForgetMesh(string key)
    {
        if (_meshPreviews.Remove(key, out var preview)) preview.Image.Dispose();
        _livePreviews.Remove(key);
        foreach (var node in Flatten(Nodes)) if (node.PreviewKey == key) node.ForgetMeshPreview();
    }

    /// <summary>Forget every picture filed against one slot, and tell every card showing one. A card left
    /// holding a disposed thumbnail is the same defect as a row left holding a disposed render.</summary>
    private void InvalidateThumbs(string slotId)
    {
        string prefix = slotId + "\u0001";
        foreach (string key in _thumbs.Keys
                     .Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _thumbs[key].Image.Dispose();
            _thumbs.Remove(key);
            _livePreviews.Remove(key);
        }
        foreach (var card in Flatten(Nodes).SelectMany(node => node.MapGroups)
                     .SelectMany(group => group.Cards))
            if (card.PreviewKey.StartsWith(prefix, StringComparison.Ordinal)) card.ReleaseThumb();
    }

    /// <summary>Ask again for whatever the selected row draws. The verbs that invalidate a picture BY NAME
    /// do it after their session change — whose event has already rebuilt the tree, restored the selection
    /// and started that row's preview loads — so the invalidation lands on the restored row and cancels
    /// those in-flight requests. Without this the row sits in its loading shimmer until the modder selects
    /// away and back. Every load is memoized behind its row's request id, so a picture the invalidation did
    /// not touch costs a no-op.</summary>
    private void ReloadSelectedPreviews()
    {
        if (SelectedNode is { } showing) _ = LoadPreviewsAsync(showing);
    }

    partial void OnSelectedNodeChanged(EditNodeVm? value)
    {
        if (value is null) return;
        // Arriving on a bare part whose map read failed asks the install again — the retry its own line
        // sends the modder back to the row for. A rebuild's restore is not an arrival, so the retry's own
        // failure cannot start the next one.
        // A retry redraws the tree, and the restore inside that redraw asks the row it lands on for its
        // pictures — so this row, which that redraw has already replaced, is not asked for them again.
        if (!_restoringSelection && value.IsBarePart && value.Part is { } part
            && RetryOriginalsRead(part)) return;
        _ = LoadPreviewsAsync(value);
    }

    /// <summary>Render what the selected row shows: one edit's own geometry — or a part's original — and
    /// its cards' pictures. Each load is memoized behind its row's request id, so
    /// re-selecting a row it already settled does nothing, and a failure lands on the row rather than on the
    /// status line, which belongs to the verb the modder ran.</summary>
    public async Task LoadPreviewsAsync(EditNodeVm node)
    {
        // Selecting a row with Blender opens also starts the mesh-edit gate's read, so the buttons settle
        // while the previews render; the answer lands on every row of the part.
        if ((node.IsPart || node.IsContentEdit) && node.Part is { } gated)
            _ = MeshEditBlockAsync(gated);

        var meshTask = LoadMeshPreviewAsync(node);
        var cardTask = LoadCardPreviewsAsync(node);
        await Task.WhenAll(meshTask, cardTask);
    }

    private async Task LoadMeshPreviewAsync(EditNodeVm node)
    {
        if (node.NeedsMeshPreview && MeshSource(node) is { } source)
        {
            int request = node.BeginMeshPreviewRequest();
            var (preview, threw) = await Guarded(source);
            if (node.IsCurrentMeshPreviewRequest(request))
            {
                if (preview is null) node.MarkMeshPreviewFailed(threw);
                else
                {
                    node.SetMeshPreview(preview);
                    Remember(node.PreviewKey, preview);
                }
            }
        }
    }

    private async Task LoadCardPreviewsAsync(EditNodeVm node)
    {
        var pending = new List<(EditMapCardVm Card, int Request)>();
        foreach (var card in node.MapGroups.SelectMany(g => g.Cards))
        {
            if (card.BeginThumbRequestIfNeeded() is not { } request) continue;
            pending.Add((card, request));
        }
        if (pending.Count == 0) return;

        IReadOnlyList<EditMapPreview?> previews;
        bool threw = false;
        try { previews = await _shell.LoadMapPreviewsAsync(pending.Select(item => item.Card.Slot).ToArray()); }
        catch
        {
            previews = Enumerable.Repeat<EditMapPreview?>(null, pending.Count).ToArray();
            threw = true;
        }
        for (int i = 0; i < pending.Count; i++)
        {
            var (card, request) = pending[i];
            var preview = i < previews.Count ? previews[i] : null;
            if (!card.IsCurrentThumbRequest(request))
            {
                preview?.Image?.Dispose();
                continue;
            }
            // A file the slot's answer names and the mod folder does not hold is its own tile: it is filed
            // under nothing, because there is no picture to hand the next redraw.
            if (preview?.MissingFile is { } gone) card.MarkThumbMissing(gone);
            else if (preview?.Image is null) card.MarkThumbFailed(
                preview?.Dimensions ?? EditMapCardVm.NoDimensions, threw);
            else
            {
                card.SetThumb(preview.Image, preview.Dimensions);
                if (card.PreviewKey.Length > 0) _thumbs[card.PreviewKey] = (preview.Image, preview.Dimensions);
            }
        }
    }

    private void Remember(string key, EditMeshPreview preview)
    {
        if (key.Length == 0) return;
        if (_meshPreviews.TryGetValue(key, out var held) && !ReferenceEquals(held.Image, preview.Image))
            held.Image.Dispose();
        _meshPreviews[key] = preview;
    }

    /// <summary>Which render this row wants, or null where it wants none. A content edit renders what it
    /// draws; a part with no edits renders the game's own, which is what a first edit would start from.</summary>
    private Func<Task<EditMeshPreview?>>? MeshSource(EditNodeVm node)
    {
        if (node.IsContentEdit && node.Edit is { } edit) return () => _shell.LoadEditMeshPreviewAsync(edit);
        if (node.IsPart && node.Part is { } part) return () => _shell.LoadPartMeshPreviewAsync(part);
        return null;
    }

    /// <summary>A preview producer's answer, and whether it threw rather than simply having nothing. Only the
    /// second carries a cause line — a picture that will not render is not fixed by trying again.</summary>
    private static async Task<(T? Value, bool Threw)> Guarded<T>(Func<Task<T?>> work) where T : class
    {
        try { return (await work(), false); }
        catch { return (null, true); }
    }

    // ---- verbs: the edit inspector ----

    [RelayCommand]
    private async Task OpenInBlender(EditNodeVm? node) => await OpenAsync(node, withReferences: false);

    [RelayCommand]
    private async Task OpenWithReferences(EditNodeVm? node) => await OpenAsync(node, withReferences: true);

    /// <summary>Open one part or edit in Blender. A part row opens the game's original without authoring
    /// anything; an edit row addresses that edit. The gate follows the identity the row already owns.</summary>
    private async Task OpenAsync(EditNodeVm? node, bool withReferences)
    {
        if (_session is null || node?.Part is null) return;
        CommitPendingRename();
        var part = node.Part;
        string? editId = node.EditDefinitionId;
        string key = editId is null ? PartBusy(part) : EditBusy(part, editId);
        if (!Take(key, editId is null ? PartIsBusy(part) : EditIsBusy(part, editId))) return;
        try
        {
            // A click that beats the selection's gate read awaits it here. Opening is read-only authored
            // state either way: the send chooses where a return lands.
            if (await MeshEditBlockAsync(part) is { } meshBlock)
            {
                Status = meshBlock;
                return;
            }
            var edit = node.Edit;
            if (edit is null)
                await _shell.OpenPartInBlenderAsync(part, withReferences, _progress);
            else
                await _shell.OpenInBlenderAsync(edit, withReferences, _progress);
            // A session that came home may have changed this edit's geometry, so its render is dropped rather
            // than kept as the answer to a question that has moved on — and re-asked for right away, since
            // the row is still on screen.
            if (edit is not null)
            {
                InvalidateMesh(part, edit.EditDefinitionId);
                ReloadSelectedPreviews();
            }
        }
        catch (Exception e) { Status = AuthoredRefusal.ForScreen(e,
            node.IsPart ? "open this part in Blender" : "open this edit in Blender"); }
        finally { Release(key); }
    }

    /// <summary>Put this edit's geometry back to the game's own — every tier it binds, so a revert does not
    /// leave one LOD replaced and another not. Confirm-guarded: a replacement is not recoverable from
    /// here.</summary>
    [RelayCommand]
    private async Task RevertMesh(EditNodeVm? node)
    {
        if (_session is null || node?.Edit is not { } edit || node.Part is null) return;
        CommitPendingRename();
        var part = node.Part;
        if (!await _shell.ConfirmAsync($"Revert the mesh on '{edit.Label}'?",
                Undoable($"'{edit.Label}' goes back to the original mesh, at every level of detail. "
                    + "Its maps are kept."), "Revert", dangerous: true)) return;
        Mutate(part, EditBusy(part, edit.EditDefinitionId), EditIsBusy(part, edit.EditDefinitionId),
            "revert the mesh", () =>
        {
            var slotIds = _session.Slots(edit.EditDefinitionId)
                .Where(state => state.Slot.Input == TargetInputKind.Geometry
                    && state.Binding.Kind != BindingKind.TargetGameValue)
                .Select(state => state.Slot.Id).ToArray();
            _session.Compound(change =>
            {
                foreach (string slotId in slotIds)
                    change.ChooseTargetGameValue(edit.EditDefinitionId, slotId);
            });
            InvalidateMesh(part, edit.EditDefinitionId);
            ReloadSelectedPreviews();
            return $"{edit.Label} is back to the original mesh.";
        });
    }

    [RelayCommand]
    private void DuplicateEdit(EditNodeVm? node)
    {
        if (_session is null || node?.Edit is not { } edit || node.Part is null) return;
        CommitPendingRename();
        var part = node.Part;
        string? created = null;
        Mutate(part, EditBusy(part, edit.EditDefinitionId), EditIsBusy(part, edit.EditDefinitionId),
            "copy this edit", () =>
            {
                created = _session.DuplicateEdit(edit.EditDefinitionId);
                return $"Copied {edit.Label}.";
            },
            after: () => { if (created is not null) Reselect(part, created, EditNodeKind.Edit); });
    }

    /// <summary>Delete one edit. The confirm states the one fact that matters at delete time: whether
    /// anything currently uses it.</summary>
    [RelayCommand]
    private async Task DeleteEdit(EditNodeVm? node)
    {
        if (_session is null || node?.Edit is not { } edit || node.Part is null) return;
        CommitPendingRename();
        var part = node.Part;
        var (always, states) = PlacementSplit(edit.EditDefinitionId);
        string body = !always && states == 0
            ? "This edit isn't used anywhere."
            : $"This edit is used in {EditNodeVm.Where(always, states)}.";

        if (!await _shell.ConfirmAsync($"Delete '{edit.Label}'?", Undoable(body), "Delete", dangerous: true))
            return;
        Mutate(part, EditBusy(part, edit.EditDefinitionId), EditIsBusy(part, edit.EditDefinitionId),
            "delete this edit", () =>
        {
            _session.DeleteEdit(edit.EditDefinitionId);
            InvalidateMesh(part, edit.EditDefinitionId);
            return $"Deleted {edit.Label}.";
        });
    }

    /// <summary>The tail a destructive confirm carries, in the app's own words.</summary>
    private static string Undoable(string body) => body + "\n\nThis cannot be undone.";

    /// <summary>The delete confirm's one activation fact. Always and each state count once.</summary>
    internal int PlacementCount(string editDefinitionId)
    {
        var (always, states) = PlacementSplit(editDefinitionId);
        return (always ? 1 : 0) + states;
    }

    /// <summary>Where one edit is used, in the two halves every sentence about it is built from.</summary>
    private (bool Always, int States) PlacementSplit(string editDefinitionId)
    {
        if (_session?.Outline().Edits.FirstOrDefault(edit =>
                string.Equals(edit.Id, editDefinitionId, StringComparison.Ordinal)) is not { } found)
            return (false, 0);
        return (found.Placements.Any(placement => placement.IsAlways),
            found.Placements.Count(placement => !placement.IsAlways));
    }

    /// <summary>Commit the inline rename. A blank name restores the default this edit would have been given,
    /// which for a hide edit is "Hidden". Every edit is renamed the same way.</summary>
    [RelayCommand]
    private void CommitRename(EditNodeVm? node)
    {
        // Only an edit row owns the rename box. A material row also addresses its edit, but its EditLabel
        // is nothing anyone typed — committing it would blank the edit's name.
        if (_session is null || node is not { IsRenameable: true } || node.Edit is not { } edit
            || node.Part is null) return;
        if (string.Equals(node.EditLabel, edit.Label, StringComparison.Ordinal)) return;
        var part = node.Part;
        Mutate(part, EditBusy(part, edit.EditDefinitionId), EditIsBusy(part, edit.EditDefinitionId),
            "rename this edit",
            () => { _session.RenameEdit(edit.EditDefinitionId, node.EditLabel); return null; });
    }

    private bool _renaming;

    /// <summary>Land whatever is in the rename box before anything else redraws under it. Running another verb
    /// is not a reason to lose what was typed; choosing a different row is the one thing that discards
    /// it.</summary>
    private void CommitPendingRename()
    {
        if (_renaming) return;
        _renaming = true;
        try { CommitRename(SelectedNode); }
        finally { _renaming = false; }
    }

    [RelayCommand]
    private void GoToBuild(EditNodeVm? node)
    {
        CommitPendingRename();
        _shell.GoToBuild(node?.Edit);
    }

    [RelayCommand]
    private async Task CopyName(string? text) => await _shell.CopyTextAsync(text);

    // ---- verbs: the part inspector ----

    /// <summary>＋ New edit: another complete answer for this part, fresh from vanilla.</summary>
    [RelayCommand]
    private void NewEdit(EditNodeVm? node)
    {
        if (node?.Part is null) return;
        CommitPendingRename();
        var part = node.Part;
        if (!Take(PartBusy(part), PartIsBusy(part))) return;
        try
        {
            if (MintEdit(part) is not { } created) return;
            Reselect(part, created.EditDefinitionId, EditNodeKind.Edit);
            Status = AddedEdit(created.EditDefinitionId, created.Label);
        }
        finally { Release(PartBusy(part)); }
    }

    /// <summary>Add the part's hide edit. A part that already has one is told so rather than told a second
    /// one was added: there is one, and where it is used is the fact that matters next.</summary>
    [RelayCommand]
    private void HidePart(EditNodeVm? node)
    {
        if (_session is null || node?.Part is null) return;
        CommitPendingRename();
        var part = node.Part;
        string? standing = HideEdit(part);
        string? created = null;
        Mutate(part, PartBusy(part), PartIsBusy(part), "hide this part",
            () =>
            {
                OpenPart(part);
                created = _session.AddHideEdit(part);
                var (always, states) = PlacementSplit(created);
                return standing is null ? AddedEdit(created, EditLabel(created))
                    : $"{EditLabel(created)} already exists. {EditNodeVm.Uses(always, states)}";
            },
            after: () => { if (created is not null) Reselect(part, created, EditNodeKind.Edit); });
    }

    /// <summary>What one new edit's line says. A part with no answer on the board takes its first edit into
    /// Always and is told it is used there; every later one waits in the library, and is told where to put
    /// it. The two outcomes read differently because they ARE different.</summary>
    private string AddedEdit(string editDefinitionId, string label)
    {
        var (always, states) = PlacementSplit(editDefinitionId);
        return !always && states == 0
            ? $"Added {label}. {EditNodeVm.NotUsedYet} {PlaceItInBuild}"
            : $"Added {label}. {EditNodeVm.Uses(always, states)}";
    }

    /// <summary>Where an edit nothing uses yet is given its place.</summary>
    public const string PlaceItInBuild = "Add it to Always or a state in ③ Build.";

    /// <summary>The part's hide edit, or null where it has none.</summary>
    private string? HideEdit(TargetPart part) => _session?.Outline().Edits.FirstOrDefault(edit =>
        edit.Kind == EditDefinitionKind.Hide && edit.Target.SameAs(part))?.Id;

    private string EditLabel(string editDefinitionId) => _session?.Outline().Edits.FirstOrDefault(edit =>
        string.Equals(edit.Id, editDefinitionId, StringComparison.Ordinal))?.Label ?? "the edit";

    // ---- verbs: the subject inspector ----
    //
    // These four act on everything under one subject rather than on any one edit, and none of them writes
    // authored intent — they open a session, write files out, reveal a folder, or drop the subject from the
    // mod. The gate is the SUBJECT's, so a part verb underneath cannot run alongside one.

    /// <summary>Open every part of this subject in one Blender session.</summary>
    [RelayCommand]
    private async Task OpenSubjectInBlender(EditNodeVm? node) =>
        await RunSubject(node, "open this item's parts in Blender",
            subject => _shell.OpenSubjectInBlenderAsync(subject.Subject, subject.Outfit, _progress));

    /// <summary>Open every part from its active or first content edit, falling back to the original.</summary>
    [RelayCommand]
    private async Task OpenSubjectFirstEditInBlender(EditNodeVm? node) =>
        await RunSubject(node, "open this item's first edits in Blender",
            subject => _shell.OpenSubjectFirstEditInBlenderAsync(
                subject.Subject, subject.Outfit, _progress));

    /// <summary>Drop this subject from the mod. The shell asks first; what comes back is a redraw either way,
    /// since a cancel leaves the mod exactly as it was and a removal takes the whole branch with it.</summary>
    [RelayCommand]
    private async Task RemoveSubject(EditNodeVm? node) =>
        await RunSubject(node, "remove this item from the mod", async subject =>
        {
            await _shell.RemoveSubjectAsync(subject.Subject, subject.Outfit);
        });

    /// <summary>Reveal this subject's files. Nothing changes and nothing waits, so it holds no gate.</summary>
    [RelayCommand]
    private void ShowSubjectFolder(EditNodeVm? node)
    {
        if (node is not { IsSubject: true }) return;
        CommitPendingRename();
        try { _shell.ShowSubjectFolder(node.Subject, node.Outfit); }
        catch (Exception e) { Status = AuthoredRefusal.ForScreen(e, "show this item's files"); }
    }

    /// <summary>Run one subject verb behind the subject's own gate. <paramref name="action"/> is what the
    /// verb does, for a failure with no wording of its own.</summary>
    private async Task RunSubject(EditNodeVm? node, string action, Func<EditNodeVm, Task> work)
    {
        if (node is not { IsSubject: true }) return;
        CommitPendingRename();
        string key = SubjectBusy(node.Subject, node.Outfit);
        if (!Take(key, SubjectIsBusy(node.Subject, node.Outfit))) return;
        try { await work(node); }
        catch (Exception e) { Status = AuthoredRefusal.ForScreen(e, action); }
        finally { Release(key); }
    }

    /// <summary>Selecting a row in the part inspector's overview selects that edit.</summary>
    [RelayCommand]
    private void SelectEdit(EditNodeVm? node)
    {
        if (node is null) return;
        SelectedNode = node;
    }

    // ---- verbs: the map cards ----

    [RelayCommand]
    private async Task OpenCard(EditMapCardVm? card)
    {
        if (card is null) return;
        // Before the editor is handed anything: opening a shared original copies the game's texture into the
        // mod's files on first touch, which is the very reach this boundary refuses.
        if (card.SharingRefusal is { } refused)
        {
            Status = refused;
            return;
        }
        if (card.Role == EditCardRole.FirstEdit)
        {
            await OpenOriginalCardAsync(card);
            return;
        }
        await RunCard(card, "open this map", async () =>
        {
            var opened = await _shell.OpenPictureAsync(card.Slot, _progress);
            ReportBound(card, opened.Published);
        });
    }

    /// <summary>Choose a <c>.png</c> from disk for one card. What happens to the pick is the DROP's own route,
    /// entered at the same door a dragged file comes through: the same first-edit question, the same refusals,
    /// the same result line. One file, one place, one answer, whichever way the modder handed it over.</summary>
    [RelayCommand]
    private async Task BrowseCard(EditMapCardVm? card)
    {
        if (card is null) return;
        if (card.SharingRefusal is { } refused)
        {
            Status = refused;
            return;
        }
        string? path;
        try { path = await _shell.PickPictureAsync(); }
        catch (Exception e) { Status = AuthoredRefusal.ForScreen(e, "choose an image"); return; }
        if (path is null) return;   // cancelled: nothing was chosen, so nothing is said
        await HandleDropAsync(new[] { path }, card);
    }

    [RelayCommand]
    private async Task OpenCardUvGuide(EditMapCardVm? card)
    {
        if (card is null) return;
        await RunCard(card, "open the UV guide", () => _shell.OpenUvGuideAsync(card.Slot, _progress));
    }

    /// <summary>Put one card back to the game's own picture. Only a game slot has one; a replacement's own
    /// map goes back with the mesh it belongs to, which the card's hover says.</summary>
    [RelayCommand]
    private void RevertCard(EditMapCardVm? card)
    {
        if (_session is null || card is null) return;
        // A game slot goes back to the game's own value. A replacement-slot RAMP record goes back to
        // unanswered — the carrier's live ramp — which is the one record those cards hold a way off of;
        // a replacement's pictures still have none of their own (the mesh is their way back).
        bool rampRecord = !card.IsGameSlot && card.IsRamp && card.RampState.HasRecord;
        if (!card.IsGameSlot && !rampRecord) return;
        CommitPendingRename();
        var slot = card.Slot;
        Mutate(slot.Edit.Part, CardBusy(slot), CardIsBusy(slot), "revert this map",
            () =>
            {
                if (rampRecord)
                {
                    _session.ChooseInheritedCarrier(slot.Edit.EditDefinitionId, slot.SlotId);
                    InvalidateThumbs(slot.SlotId);
                    ReloadSelectedPreviews();
                    return "Cleared the toon ramp choice. The replacement uses the original part's toon ramp again.";
                }
                _session.ChooseTargetGameValue(slot.Edit.EditDefinitionId, slot.SlotId);
                InvalidateThumbs(slot.SlotId);
                ReloadSelectedPreviews();
                return $"{card.MapLabel} is back to the original.";
            },
            after: () => Reselect(slot.Edit.Part, slot.Edit.EditDefinitionId, EditNodeKind.Edit));
    }

    [RelayCommand]
    private async Task ChooseRamp(EditMapCardVm? card)
    {
        if (card is null) return;
        if (card.SharingRefusal is { } refused)
        {
            Status = refused;
            return;
        }
        if (card.Role == EditCardRole.FirstEdit)
        {
            await ChooseRampOnOriginalCardAsync(card);
            return;
        }
        await RunCard(card, "choose a toon ramp", async () =>
        {
            if (await _shell.PickRampAsync(card.Slot) is not { } pick) return;
            if (pick.Picked is { } picked) { ReportBound(card, picked); return; }
            KeepGameOwnRamp(card.Slot);
        });
    }

    /// <summary>Record the pinned row's answer: this slot keeps whatever the game's own material binds here.
    ///
    /// <para>Which binding says that depends on what the slot addresses, which is the only test the model
    /// allows. A game-domain slot asks the game for its own value — the same answer Revert leaves it on. A
    /// replacement's own output slot names the game ramp slot it stands over, with no edit of its own, which
    /// is the one binding that tells a recorded decision apart from a ramp nobody has answered yet; the model
    /// refuses it by name where the material draws through no ramp at all.</para></summary>
    private void KeepGameOwnRamp(EditSlotRef slot)
    {
        if (_session is null) return;
        if (slot.Domain == TargetSlotDomain.Game)
            _session.ChooseTargetGameValue(slot.Edit.EditDefinitionId, slot.SlotId);
        else
            // The installed material position, not the submesh index: GameRampSlot filters by
            // material position, and on a part whose submeshes fold onto fewer materials the two are
            // different numbers — the fold's answer is what the card is standing at.
            _session.ChooseSourceSlot(slot.Edit.EditDefinitionId, slot.SlotId,
                _session.GameRampSlot(slot.Edit.Part,
                    slot.GameMaterialSlotIndex ?? slot.MaterialSlotIndex ?? 0));
        InvalidateThumbs(slot.SlotId);
        Reselect(slot.Edit.Part, slot.Edit.EditDefinitionId, EditNodeKind.Edit);
        ReloadSelectedPreviews();
        Status = KeptGameOwnRamp;
    }

    /// <summary>What the pinned row's answer reports. It names the state rather than the mechanism: what the
    /// modder chose is that the game's own ramp keeps drawing here.</summary>
    internal const string KeptGameOwnRamp = "This material uses the original toon ramp.";

    [RelayCommand]
    private void RevertRamp(EditMapCardVm? card) => RevertCard(card);

    /// <summary>Pick another material and copy its differing shading values onto this one. Each copied
    /// value binds the exact source material, so a rebuild reads the source's current numbers rather
    /// than a copy frozen at pick time.</summary>
    [RelayCommand]
    private async Task CopyShadingFromMaterial(EditShadingRowVm? row)
    {
        if (row is null || _session is null) return;
        CommitPendingRename();
        // The edit's own gate, held across the picker and the confirm as well as the write: the dialog's
        // answer is measured against the edit as it stands when it opens, and another verb landing on that
        // edit underneath would have it applied to a different one.
        string gate = ShadingBusy(row);
        if (!Take(gate, ShadingIsBusy(row))) return;
        try { await CopyShadingFromMaterialAsync(row); }
        finally { Release(gate); }
    }

    private async Task CopyShadingFromMaterialAsync(EditShadingRowVm row)
    {
        if (_session is null) return;
        EditShadingSource? source;
        try
        {
            source = await _shell.PickShadingSourceAsync(row.Part, row.MaterialSlotIndex,
                row.MaterialLabel, row.Material, SessionSubjects(), _progress);
        }
        catch (EditShadingFailureException failure)
        {
            Status = failure.Message;
            return;
        }
        catch
        {
            Status = CopyShadingFailed;
            return;
        }
        if (source is null) return;
        if (source.Rows.Count == 0)
        {
            Status = ShadingAlreadyMatches;
            return;
        }
        string list = string.Join(", ", source.Rows.Select(candidate => candidate.Label));
        string firstEdit = row.IsFirstEdit ? " " + AddsFirstEdit : "";
        // Named as the button and the pick list name the act, with the body carrying what is copied: the
        // question is asked away from the row, where "Shading" is no longer on screen beside it.
        if (!await _shell.ConfirmAsync($"Copy from {source.Label}?",
                $"Sets {Count(source.Rows.Count, "shading value")}: {list}. "
                    + $"Other values stay as they are.{firstEdit}",
                "Copy"))
            return;
        var session = _session;
        WriteShading(row, "copy these shading values", edit =>
            {
                session.CopyMaterialValues(edit.EditDefinitionId, row.Part,
                    row.MaterialSlotIndex, source.SourcePart, source.SourceMaterialSlotIndex,
                    source.Rows.Select(value => value.Semantic).ToArray(), _shell.ResolvePart);
                return $"Copied {Count(source.Rows.Count, "shading value")} from {source.Label}.";
            });
    }

    /// <summary>The two materials already agree on every value the shader reads, so a copy has nothing
    /// to set.</summary>
    internal const string ShadingAlreadyMatches =
        "The two materials already have the same shading values.";
    internal const string ShadingMatchesOriginal =
        "The shading values already match the original.";
    internal const string ReadingShadingValues = "Reading shading values…";
    internal const string CopyShadingFailed =
        "Couldn't open the material list. Try again.";
    internal const string ShadingSourceUnreadable =
        "Couldn't read the chosen material's shading values.";
    internal const string ShadingInstallUnavailable =
        "Couldn't read the game files, so shading values are unavailable.";
    internal const string EditShadingValuesFailed =
        "Couldn't open the shading values. Try again.";

    /// <summary>Open the shading-values dialog and apply what came back: typed values are set, cleared
    /// rows return to the original.</summary>
    [RelayCommand]
    private async Task EditShadingValues(EditShadingRowVm? row)
    {
        if (row is null || _session is null) return;
        CommitPendingRename();
        string gate = ShadingBusy(row);
        if (!Take(gate, ShadingIsBusy(row))) return;
        try { await EditShadingValuesAsync(row); }
        finally { Release(gate); }
    }

    private async Task EditShadingValuesAsync(EditShadingRowVm row)
    {
        if (_session is null) return;
        EditShadingValuesResult? answer;
        try
        {
            answer = await _shell.EditShadingValuesAsync(row.Edit, row.MaterialSlotIndex,
                row.MaterialLabel, row.AuthoredValues, row.IsFirstEdit);
        }
        catch (EditShadingFailureException failure)
        {
            Status = failure.Message;
            return;
        }
        catch
        {
            Status = EditShadingValuesFailed;
            return;
        }
        if (answer is null) return;
        if (answer.Edits.Count == 0)
        {
            if (answer.MatchesOriginal) Status = ShadingMatchesOriginal;
            return;
        }
        IReadOnlyList<EditShadingValueEdit> edits = answer.Edits;
        // Clearing an unset value on a bare row changes nothing. Do not mint an edit merely because a test
        // seam or future dialog reports that no-op explicitly.
        if (row.IsFirstEdit)
        {
            edits = edits.Where(value => value.Value is { Length: > 0 }).ToArray();
            if (edits.Count == 0) return;
        }
        var session = _session;
        WriteShading(row, "set these shading values", edit =>
            {
                int set = edits.Count(value => value.Value is { Length: > 0 });
                int cleared = edits.Count - set;
                session.ApplyMaterialValues(edit.EditDefinitionId, row.Part,
                    row.MaterialSlotIndex, edits.Select(value =>
                        new AuthoredMaterialValueEdit(value.Semantic, value.Value)).ToArray(),
                    _shell.ResolvePart);
                return set > 0 && cleared > 0
                    ? $"Set {Count(set, "value")}, returned {Count(cleared, "value")} to the original."
                    : set > 0 ? $"Set {Count(set, "value")}."
                    : $"Returned {Count(cleared, "value")} to the original.";
            });
    }

    /// <summary>Apply an effective shading answer at edit grain. Existing rows use the ordinary write path;
    /// bare rows mint under their standing part gate and discard that mint if the write refuses or fails.</summary>
    private void WriteShading(EditShadingRowVm row, string action, Func<EditRef, string?> change)
    {
        if (!row.IsFirstEdit)
        {
            Write(row.Part, action, () => change(row.Edit),
                after: () => Reselect(row.Part, row.Edit.EditDefinitionId, EditNodeKind.Edit));
            return;
        }

        EditRef? minted = MintEdit(row.Part);
        if (minted is null) return;
        try
        {
            string? line = change(minted);
            Reselect(row.Part, minted.EditDefinitionId, EditNodeKind.Edit);
            if (line is not null) Status = AddedEdit(minted.EditDefinitionId, minted.Label);
        }
        catch (Exception e)
        {
            string refusal = AuthoredRefusal.ForScreen(e, action);
            if (!DiscardShadingMint(row.Part, minted))
            {
                Status = refusal + " " + Status;
                return;
            }
            Status = refusal;
        }
    }

    /// <summary>Return every shading value this material position sets back to the original.</summary>
    [RelayCommand]
    private void RevertShading(EditShadingRowVm? row)
    {
        if (row is null || _session is null || row.AuthoredSlotIds.Count == 0) return;
        var session = _session;
        Mutate(row.Part, EditBusy(row.Part, row.Edit.EditDefinitionId),
            EditIsBusy(row.Part, row.Edit.EditDefinitionId), "revert these shading values", () =>
            {
                session.Compound(change =>
                {
                    foreach (var slotId in row.AuthoredSlotIds)
                        change.ChooseTargetGameValue(row.Edit.EditDefinitionId, slotId);
                });
                return $"Returned {Count(row.AuthoredSlotIds.Count, "value")} to the original.";
            },
            after: () => Reselect(row.Edit.Part, row.Edit.EditDefinitionId, EditNodeKind.Edit));
    }

    /// <summary>The subjects this mod holds, in tree order — what the shading-source picker offers
    /// materials from.</summary>
    private IReadOnlyList<(string Subject, string Outfit)> SessionSubjects()
        => Nodes.Where(node => node.IsSubject)
            .Select(node => (node.Subject, node.Outfit)).ToArray();

    /// <summary>A card can take the DRAG when it is idle and its drop route has an answer. Toon ramps and
    /// sharing-gated cards accept so the release can refuse in words — the platform delivers no release under
    /// a "no" cursor. A first-edit card does the same even when its ordinary dashed target is hidden by a
    /// gate. Stand-ins remain inert because their edit draws the replacement's maps instead.</summary>
    public bool CanAcceptDrop(EditMapCardVm? card) => card is not null
        && (card.IsOriginal
            ? card.Role == EditCardRole.FirstEdit && !PartIsBusy(card.Slot.Edit.Part)
            : !CardIsBusy(card.Slot));

    /// <summary>The one entry every drop comes through. Exactly one <c>.png</c> onto one card; anything
    /// else is refused on the status line with no shell call, so a stray multi-select cannot half-apply.</summary>
    public async Task HandleDropAsync(IReadOnlyList<string> paths, EditMapCardVm? card)
    {
        if (card is null)
        {
            Status = NoDropTargetHere();
            return;
        }
        if (card.IsRamp && paths.Count == 1
            && !paths[0].EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            Status = card.RampRefusal;
            return;
        }
        var pngs = paths.Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pngs.Count != 1)
        {
            Status = pngs.Count == 0 ? "Only a .png can replace a map." : "Drop one .png at a time.";
            return;
        }
        await DropOnCardAsync(card, pngs[0]);
    }

    /// <summary>Where a picture can land, said for what is on screen. Naming the map card is only true where
    /// there are map cards; on a row that has none, the actionable sentence is the one that gets to a row
    /// that does, and where even that is already said on the row itself the drop says nothing at all.</summary>
    private string NoDropTargetHere()
    {
        var node = SelectedNode;
        if (node is null || node.IsSubject || node.IsSkeleton) return SelectAPart;
        if (node.HasMapGroups) return NoDropTarget;
        // A part with edits shows its overview instead of cards; a part whose read is still running gets
        // the app's own wait sentence, because a gesture that clears the line and says nothing reads as a
        // drop that was taken. A part the install could not answer for already says which on the row.
        return node.IsPart && node.HasOverview ? SelectAnEdit
            : node.IsPart && node.IsReadingOriginals ? GameFilesGate.SubjectReading
            : "";
    }

    public const string NoDropTarget = "Drop a .png on a map card.";
    public const string SelectAPart = "Select a part to see its maps.";
    public const string SelectAnEdit = "Select an edit to see its maps.";

    /// <summary>A <c>.png</c> dropped on one card lands on that card's slot, whatever the file is called.
    /// What the card IS is asked before anything is decoded or published: a toon ramp is shading data and no
    /// picture is one, a stand-in belongs to an edit the build draws the replacement's maps for, and a
    /// texture the item has not been read for yet cannot be bound. A shared texture instead asks for consent
    /// naming its measured reach. Each refusal says so and stops.
    ///
    /// <para>This is the gesture's own gate and it reads a card built at the last redraw. The publish route
    /// compares the sharing answer the question showed with the live model, so a larger reach landing between
    /// the question and the bind cannot inherit consent for the smaller one.</para></summary>
    public async Task DropOnCardAsync(EditMapCardVm card, string path)
    {
        if (card.SharingRefusal is { } refused)
        {
            Status = refused;
            return;
        }
        if (card.IsRamp)
        {
            Status = $"Cannot apply {Path.GetFileName(path)} here. {card.RampRefusal}";
            return;
        }
        if (card.Role == EditCardRole.StandIn)
        {
            Status = $"Cannot apply {Path.GetFileName(path)} here. {EditMapCardVm.StandInNotDroppable}";
            return;
        }
        if (card.Role == EditCardRole.FirstEdit)
        {
            await DropOnOriginalCardAsync(card, path);
            return;
        }
        await RunCard(card, "apply this image", async () =>
        {
            var produced = await _shell.AcceptDroppedPictureAsync(card.Slot, path, _progress);
            ReportBound(card, produced);
        });
    }

    /// <summary>A <c>.png</c> dropped on a bare part's ORIGINAL card. It adds the part's first edit and binds
    /// the picture to exactly the place the card stands on — the material position and the map the modder
    /// dropped it on — then selects the new edit, so the result is seen where it landed.
    ///
    /// <para>The question comes FIRST, before anything is minted, and it asks about the whole act rather
    /// than about an edit: there is no edit to name yet, and a decline has to leave the part exactly as it
    /// was — no edit, no selection moved, nothing said that suggests otherwise. Past the question, every way
    /// this can end takes the mint back with it, so an empty edit is never what a refused picture leaves
    /// behind.</para>
    ///
    /// <para>The gate is the PART's and it outlives the mint: the card that started this belongs to a row the
    /// mint's rebuild replaces, so nothing on it can be waited on afterwards.</para></summary>
    private async Task DropOnOriginalCardAsync(EditMapCardVm card, string path)
    {
        string name = Path.GetFileName(path);
        // The MATERIAL is in it, not the map alone: a part with four materials has four base-colour cards,
        // and a question that names only the map is the same question on all four.
        string map = EditMapCardVm.MapInSentence(card.MapLabel, card.Slot.MaterialName);
        string consequence = card.Sharing == EditTextureSharing.Shared
            ? "\n\n" + EditMapCardVm.SharedConsequence(card.SharingUses!.Value)
            : "";
        // The sizes come off the card's own line and the dropped file's header, so a picture that does not
        // match the map is said before it is taken, never after.
        string size = EditMapCardVm.SizeNote(Remold.Core.Textures.PngInfo.TryPngSize(path),
            EditMapCardVm.ParseDimensions(card.Dimensions)) is { } note ? " " + note : "";
        bool confirmed;
        try
        {
            confirmed = await _shell.ConfirmAsync($"Apply {name}?",
                $"{name} becomes this part's {map}. {AddsFirstEdit}{size}{consequence}", "Apply");
        }
        catch (Exception e) { Status = AuthoredRefusal.ForScreen(e, "apply this image"); return; }
        if (!confirmed) return;
        await RunCard(card, "apply this image", async () =>
        {
            var produced = await _shell.AcceptDroppedPictureAsync(card.Slot, path, _progress,
                confirmed: true, offered: new EditTextureSharingOffer(card.Sharing, card.SharingUses));
            ReportBoundOnMaterial(card.Slot, card.MapLabel, produced);
        });
    }

    private async Task OpenOriginalCardAsync(EditMapCardVm card)
    {
        await RunCard(card, "open this map", async () =>
        {
            var opened = await _shell.OpenPictureAsync(card.Slot, _progress);
            if (opened.Published is { } published)
                ReportBoundOnMaterial(card.Slot, card.MapLabel, published);
        });
    }

    private async Task ChooseRampOnOriginalCardAsync(EditMapCardVm card)
    {
        await RunCard(card, "choose a toon ramp", async () =>
        {
            if (await _shell.PickRampAsync(card.Slot) is not { Picked: { } picked }) return;
            ReportBoundOnMaterial(card.Slot, card.MapLabel, picked);
        });
    }

    internal const string AddsFirstEdit = "This adds the part's first edit.";

    /// <summary>Take back a shading-row mint whose write did not complete. Picture and ramp cards no longer
    /// mint before their external action and never use this cleanup.</summary>
    private bool DiscardShadingMint(TargetPart part, EditRef edit)
    {
        try
        {
            _session!.DeleteEdit(edit.EditDefinitionId);
            Reselect(part, null, EditNodeKind.Part);
            return true;
        }
        catch (Exception e)
        {
            Status = AuthoredRefusal.ForScreen(e, "remove the empty edit");
            return false;
        }
    }

    private void ReportBound(EditMapCardVm card, EditAssetResult? produced)
        => ReportBound(card.Slot, card.MapLabel, produced);

    private void ReportBound(EditSlotRef slot, string mapLabel, EditAssetResult? produced)
        => ReportBoundSubject(slot, EditMapCardVm.MapInSentence(mapLabel, slot.MaterialName), produced);

    private void ReportBoundOnMaterial(EditSlotRef slot, string mapLabel, EditAssetResult? produced)
        => ReportBoundSubject(slot,
            EditMapCardVm.MapOnMaterial(slot, mapLabel, sentenceStart: false), produced);

    private void ReportBoundSubject(EditSlotRef slot, string subject, EditAssetResult? produced)
    {
        if (produced is null) return;
        slot = produced.Target ?? slot;
        InvalidateThumbs(slot.SlotId);
        Reselect(slot.Edit.Part, slot.Edit.EditDefinitionId, EditNodeKind.Edit);
        ReloadSelectedPreviews();
        // Named exactly as the question that led here named it — the map, on its material.
        string name = string.IsNullOrWhiteSpace(produced.Label)
            ? Path.GetFileName(produced.ProjectRelativeFile) : produced.Label.Trim();
        Status = $"{name} is now {slot.Edit.Label}'s {subject}.";
    }

    // ---- the zero-to-first-edit path ----

    /// <summary>Give the part somewhere to bind and add one edit. The two are separate transactions: a part
    /// that took its slots and then failed to take an edit is a part the project knows the slots of, which is
    /// an ordinary shape and not a half-written one.
    ///
    /// <para>The install is asked for the part every time, because that is where the places a value can go
    /// come from and the part may have been opened with only some of them. An install that cannot name the
    /// part at all is only fatal where the project holds no routes of its own: a part it already opened
    /// carries its exact references, and a second edit is copied from those, so adding one does not need the
    /// game mounted — the same rule the save path follows. An install that names the part but cannot name an
    /// exact object on one of its routes is the session's own refusal either way, said out loud rather than
    /// half-opening the part.</para>
    ///
    /// <para>Called only from inside a gate the caller already holds, because on a bare part the gate has to
    /// outlive the rebuild this causes.</para></summary>
    private EditRef? MintEdit(TargetPart part)
    {
        if (_session is null) return null;
        try
        {
            OpenPart(part);
            _refusals.Remove(Key(part));
            string id = _session.CreateEdit(part);
            string label = _session.Outline().Edits
                .First(edit => string.Equals(edit.Id, id, StringComparison.Ordinal)).Label;
            return new EditRef(part, id, label);
        }
        catch (Exception e)
        {
            // The model's own refusals are written for the person reading them; anything else names the
            // action instead, since its message names identities the model keeps for itself. Kept on the
            // part row too, so the reason is still there once the status line has moved on.
            string line = AuthoredRefusal.ForScreen(e, $"add an edit to {PartTitle(part)}");
            _refusals[Key(part)] = line;
            Status = line;
            Rebuild();
            Reselect(part, null, EditNodeKind.Part);
            return null;
        }
    }

    /// <summary>Give the part the places its answers bind at. EVERY route that mints an edit on a part goes
    /// through here first, a hide included: a hide binds visibility on one of the part's own routes, so a
    /// part the project has never opened has nothing for it to anchor on any more than it has anything for
    /// a content edit to start from.
    ///
    /// <para>The install is asked every time, and its absence is only fatal where the project holds no
    /// routes of its own — the rule <see cref="MintEdit"/> states in full.</para></summary>
    private void OpenPart(TargetPart part)
    {
        if (_session is null) return;
        var resolved = _shell.ResolvePart(part);
        if (resolved is not null) _session.EnsurePartSlots(part, _ => resolved);
        else if (!_session.Snapshot().TargetSlots.Any(slot => slot.Part.SameAs(part)))
            _session.EnsurePartSlots(part, _ => null);
    }

    // ---- one place every mutation and every shell call goes through ----

    /// <summary>Run one session change behind this verb's own gate. <paramref name="action"/> is what the
    /// verb does, in the modder's own words, for the sentence a failure with no wording of its own gets.</summary>
    private void Mutate(TargetPart part, string key, bool blocked, string action, Func<string?> change,
        Action? after = null)
    {
        if (_session is null) return;
        if (!Take(key, blocked)) return;
        try { Write(part, action, change, after); }
        finally { Release(key); }
    }

    /// <summary>Run one session change inside a gate the caller already holds and say what happened. A
    /// refusal the model wrote for the screen is shown as it is; anything else names the action instead,
    /// since its own message names identities the model keeps for itself.</summary>
    private void Write(TargetPart part, string action, Func<string?> change, Action? after = null)
    {
        if (_session is null) return;
        try
        {
            string? line = change();
            after?.Invoke();
            if (line is not null) Status = line;
        }
        catch (Exception e)
        {
            after?.Invoke();
            Status = AuthoredRefusal.ForScreen(e, action);
        }
    }

    private async Task RunCard(EditMapCardVm card, string action, Func<Task> work)
    {
        CommitPendingRename();
        string key = CardBusy(card.Slot);
        if (!Take(key, CardIsBusy(card.Slot))) return;
        try { await work(); }
        catch (Exception e) { Status = AuthoredRefusal.ForScreen(e, action); }
        finally { Release(key); }
    }
}
