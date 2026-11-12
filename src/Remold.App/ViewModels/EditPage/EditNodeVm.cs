using Remold.Core.Text;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Remold.Core.Project;
using Remold.Core.Workbench;

namespace Remold.App.ViewModels.EditPage;

/// <summary>Drives which inspector shows, and the row glyph. An edit's cards live on its own inspector;
/// each of its materials is also a child row of the edit, whose inspector is that one material's slice of
/// the same cards and shading row.</summary>
public enum EditNodeKind
{
    Subject,
    Part,
    Edit,
    Material,
    Skeleton,
}

/// <summary>One row of the ② Edit tree — <b>subject → part → edits → the edit's materials</b> — carrying
/// that kind's inspector payload, so the tree is one template and the inspector one set of panels switched
/// on <see cref="Kind"/>. The workbench's node idiom, at the new grain.</summary>
public sealed partial class EditNodeVm : ObservableObject
{
    public required EditNodeKind Kind { get; init; }

    /// <summary>The row's own name: the subject, the part's renderer slot, or the edit's label.</summary>
    public required string Title { get; init; }

    /// <summary>The dim line after the title.</summary>
    [ObservableProperty] private string _detail = "";

    /// <summary>Two-way bound by the window-level TreeViewItem style.</summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>Hidden when neither this row nor a descendant matches the filter.</summary>
    [ObservableProperty] private bool _isVisible = true;

    // ---- identity ----

    /// <summary>The part this row acts on. Set on part and edit rows; null on a subject or skeleton row.</summary>
    public TargetPart? Part { get; init; }

    /// <summary>The edit this row is. Null on every other kind.</summary>
    public string? EditDefinitionId { get; init; }

    public EditDefinitionKind EditKind { get; init; }

    /// <summary>The warning and notes from the latest mesh return that changed this edit.</summary>
    public string? ReturnWarning { get; init; }

    public bool HasReturnWarning => !string.IsNullOrWhiteSpace(ReturnWarning);

    /// <summary>Which subject's branch this row sits in, for grouping and for the skeleton read.</summary>
    public string Subject { get; init; } = "";
    public string Outfit { get; init; } = "";

    /// <summary>The edit this row addresses, in the form every seam call takes — the row itself on an edit,
    /// the OWNING edit on one of its material rows. Null on every other kind.</summary>
    public EditRef? Edit => Part is not null && EditDefinitionId is not null
        ? new EditRef(Part, EditDefinitionId, EditRefLabel ?? Title) : null;

    /// <summary>The owning edit's label, set on a material row — whose own <see cref="Title"/> is the
    /// material's name, not the edit's. Null on an edit row, where the title IS the label.</summary>
    public string? EditRefLabel { get; init; }

    /// <summary>Which of the edit's material groups this row is, in pane order. Set on material rows so a
    /// rebuild can put the selection back on the same material; -1 on every other kind.</summary>
    public int MaterialOrdinal { get; init; } = -1;

    // ---- badges ----

    /// <summary>The part has at least one content edit, or this row is one. Rolled up onto the subject.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEditRollup))]
    private bool _hasEditBadge;

    /// <summary>Something on THIS row needs saying. Today the one producer is a part the install could not be
    /// opened for — the session's refusal, kept on the row after the status line has moved on. Plan verdicts
    /// belong to ③ Build.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    [NotifyPropertyChangedFor(nameof(HasProblemBadge))]
    [NotifyPropertyChangedFor(nameof(ProblemBadgeTip))]
    [NotifyPropertyChangedFor(nameof(PartRefusal))]
    [NotifyPropertyChangedFor(nameof(HasPartRefusal))]
    private string? _problem;

    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    /// <summary>What the part-level refusal row says: the install's own refusal where there is one, else the
    /// mesh-edit gate's reason on a part with no edits, and then, on every row of a part the game starts
    /// hidden, <see cref="HiddenPartNote"/> on a line of its own. Where the gate greys a hidden part's opens,
    /// every row of it says the gate's reason first and then only <see cref="Strings.EditHiddenPartShrunk"/>: the
    /// sentence about the part's position in Blender would contradict the greyed opens above it.
    ///
    /// <para>The gate's reason is on the two Blender opens' hover everywhere else, and that is enough where
    /// an edit row's own panels stand beside them. A bare part has no such panels: the opens are its whole
    /// action row, and a mesh the gate refuses is invisible until the pointer happens to rest on a disabled
    /// button. It says the same sentence in the same amber row the install's refusal uses.</para>
    ///
    /// <para>The hidden-part note is no refusal: it blocks nothing, and says that the part's position in
    /// Blender will not match the game, which the modder meets whether the part is bare or edited.</para></summary>
    public string? PartRefusal
    {
        get
        {
            if (!StartsHidden) return Problem ?? (IsBarePart ? MeshEditBlock : null);
            bool greyed = MeshEditBlock is not null;
            string? refusal = Problem ?? MeshEditBlock;
            string note = greyed ? Strings.EditHiddenPartShrunk : HiddenPartNote;
            return refusal is null ? note : refusal + "\n" + note;
        }
    }

    /// <summary>The note on every row of a part the game starts hidden: that it starts shrunk, and that its
    /// position in Blender will not match the game.</summary>
    internal static string HiddenPartNote => Strings.EditHiddenPartShrunk + " " + Strings.EditHiddenPartPosition;


    /// <summary>The game starts this row's part shrunk out of sight, so it opens in Blender centred at full
    /// size. Set on part and content-edit rows by the page, which reads it lazily per part and keeps the
    /// answer across rebuilds. Never set on a hide edit's row: a hide edit has no Blender opens, so the note
    /// about how the part opens there has nothing to explain.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PartRefusal))]
    [NotifyPropertyChangedFor(nameof(HasPartRefusal))]
    private bool _startsHidden;

    public bool HasPartRefusal => !string.IsNullOrEmpty(PartRefusal);

    /// <summary>A row under this one has a problem. The badge rolls up so a collapsed branch still shows one;
    /// the sentence does not, because a subject reading a part's refusal reads as its own and the fix is on
    /// that part.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblemBadge))]
    [NotifyPropertyChangedFor(nameof(ProblemBadgeTip))]
    private bool _hasProblemUnder;

    public bool HasProblemBadge => HasProblem || HasProblemUnder;

    public string ProblemBadgeTip => Problem ?? Strings.EditProblemUnder;

    /// <summary>A verb on what this row addresses is running — disables its buttons so a second click cannot
    /// race it. Pushed on by the page, which holds the gate by identity: a rebuild replaces this object, and
    /// a flag that lived here would be lost the moment the verb changed anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevertMesh))]
    [NotifyPropertyChangedFor(nameof(RevertMeshHint))]
    [NotifyPropertyChangedFor(nameof(OpenAllHint))]
    [NotifyPropertyChangedFor(nameof(OpenAllFirstEditHint))]
    [NotifyPropertyChangedFor(nameof(RemoveSubjectHint))]
    [NotifyPropertyChangedFor(nameof(CanOpenInBlender))]
    [NotifyPropertyChangedFor(nameof(OpenInBlenderHint))]
    [NotifyPropertyChangedFor(nameof(OpenWithReferencesHint))]
    [NotifyPropertyChangedFor(nameof(NewEditHint))]
    [NotifyPropertyChangedFor(nameof(HidePartHint))]
    private bool _isBusy;

    // ---- the mesh-edit gate ----

    /// <summary>Why this part's game mesh cannot be edited in Blender, or null while it can — or while the
    /// answer hasn't been read yet. Set on part and content-edit rows by the page, which reads it lazily
    /// per part and keeps the settled answer across rebuilds. It turns the two Blender opens off with the
    /// reason on hover, and renders as its own line in the inspector; maps, shading and Hide stay
    /// untouched by it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeshEditBlock))]
    [NotifyPropertyChangedFor(nameof(CanOpenInBlender))]
    [NotifyPropertyChangedFor(nameof(OpenInBlenderHint))]
    [NotifyPropertyChangedFor(nameof(OpenWithReferencesHint))]
    [NotifyPropertyChangedFor(nameof(PartRefusal))]
    [NotifyPropertyChangedFor(nameof(HasPartRefusal))]
    private string? _meshEditBlock;

    public bool HasMeshEditBlock => !string.IsNullOrEmpty(MeshEditBlock);

    /// <summary>The two Blender opens' shared enablement: not while a verb runs, and never on a mesh the
    /// gate refuses. The shipped pane's rule, at the session-native rows.</summary>
    public bool CanOpenInBlender => !IsBusy && !HasMeshEditBlock;

    /// <summary>The Open-in-Blender tooltip: the refusal when the mesh is blocked, the wait while a verb
    /// runs, else what the verb does on this row's kind.</summary>
    public string OpenInBlenderHint => MeshEditBlock
        ?? (IsBusy ? Strings.BlenderBusy
            : IsPart ? Strings.EditOpenInBlenderPartTip
            : Strings.EditOpenInBlenderEditTip);

    /// <summary>The Open-with-References tooltip, on the same gate.</summary>
    public string OpenWithReferencesHint => MeshEditBlock
        ?? (IsBusy ? Strings.BlenderBusy
            : IsPart ? Strings.EditOpenWithRefsPartTip
            : Strings.EditOpenWithRefsEditTip);

    // ---- the subject verbs' tooltips ----
    //
    // Why the button is off when it is (shown via ToolTip.ShowOnDisabled), else what the verb does — the
    // shipped pane's rule and its wording, so the two surfaces answer alike.

    public string OpenAllHint => IsBusy ? Strings.BlenderBusy : Strings.BlenderOpenAllReady;

    public string OpenAllFirstEditHint => IsBusy ? Strings.BlenderBusy
        : Strings.EditOpenAllFirstEditTip;

    public string RemoveSubjectHint => IsBusy ? Strings.BlenderBusy
        : Strings.EditRemoveSubjectTip;

    // ---- the part verbs' tooltips ----
    //
    // Both of these describe what the verb leaves behind, and a part with no edits has nothing for them to
    // describe: "another edit" and "its other edits" are both false there. Why the button is off comes
    // first, as it does on every other verb of this page: these two are disabled by the same gate the
    // opens beside them are, and a hover that describes the verb while the button refuses it says nothing
    // about the refusal.

    public string NewEditHint => IsBusy ? Strings.BlenderBusy
        : HasOverview
        ? Strings.EditNewEditAnotherTip
        : Strings.EditNewEditFirstTip;

    public string HidePartHint => IsBusy ? Strings.BlenderBusy
        : HasOverview
        ? Strings.EditHidePartWithEditsTip
        : Strings.EditHidePartTip;

    // ---- view helpers ----

    public bool IsSubject => Kind == EditNodeKind.Subject;
    public bool IsPart => Kind == EditNodeKind.Part;
    public bool IsEdit => Kind == EditNodeKind.Edit;
    public bool IsMaterial => Kind == EditNodeKind.Material;
    public bool IsSkeleton => Kind == EditNodeKind.Skeleton;

    /// <summary>A hide edit carries no cards and no mesh of its own. Everything an edit's inspector says
    /// about the edit ITSELF — its name, where it is used, deleting it — is the shared surface above; this
    /// picks out the one paragraph explaining what hiding does.</summary>
    public bool IsHideEdit => IsEdit && EditKind == EditDefinitionKind.Hide;

    /// <summary>A content edit — the one kind with geometry, cards and a mesh preview.</summary>
    public bool IsContentEdit => IsEdit && EditKind == EditDefinitionKind.Content;

    /// <summary>This edit asks for geometry other than the game's own, so there is a mesh to take back.
    /// Without it Revert would be a button that does nothing.</summary>
    public bool HasMeshEdit { get; init; }

    public bool CanRevertMesh => HasMeshEdit && !IsBusy;

    public string RevertMeshHint => !HasMeshEdit ? Strings.NothingToRevert
        : IsBusy ? Strings.BlenderBusy
        : Strings.EditRevertMeshTip;

    /// <summary>The part has no edits at all: the inspector shows the bare-part shape, whose first action
    /// mints Edit 1.</summary>
    public bool IsBarePart => IsPart && Children.Count == 0;

    /// <summary>This row previews geometry: a content edit's own, or — on a part — the original,
    /// which is what its edits start from.</summary>
    public bool ShowsMeshPreview => IsContentEdit || IsPart;

    /// <summary>The row glyph — a type marker, not a status badge. <c>✎</c> for a content edit, <c>∅</c> for
    /// a hide edit, <c>▧</c> for one of an edit's materials, the workbench's own markers for a part and the
    /// skeleton.</summary>
    public string Glyph => Kind switch
    {
        EditNodeKind.Part => "◇",
        EditNodeKind.Edit => EditKind == EditDefinitionKind.Hide ? "∅" : "✎",
        EditNodeKind.Material => "▧",
        EditNodeKind.Skeleton => "⌇",
        _ => "",
    };

    public bool HasGlyph => Glyph.Length > 0;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// <summary>The ✎ roll-up badge is drawn. It rolls UP onto a part and its subject and stops there: an
    /// edit row already leads with ✎ as its type marker, and a second one beside it says the same thing
    /// twice.</summary>
    public bool ShowsEditRollup => HasEditBadge && !IsEdit;

    /// <summary>Labels are the modder's vocabulary for the answers they wrote, and every edit is named the
    /// same way — a hide starts out with the hide name in the UI language and a cleared name puts that back,
    /// which is what a default name is. ③ Build lists edits by name, so a mod that hides four parts is worth
    /// naming.</summary>
    public bool IsRenameable => IsEdit;

    public ObservableCollection<EditNodeVm> Children { get; } = new();

    // ---- inspector payload ----

    /// <summary>The inspector's title. On an edit it is <c>part · label</c>, which is what the rename box
    /// sits beside.</summary>
    public string InspectorHeader { get; init; } = "";

    /// <summary>The line under it.</summary>
    [ObservableProperty] private string _inspectorDetail = "";

    /// <summary>The edit's label, two-way bound by the inline rename box. The page commits it through the
    /// session's own rename; a blank name restores the default one it would have been given.</summary>
    [ObservableProperty] private string _editLabel = "";

    /// <summary>The part's edits in the part inspector's overview. Selecting a row selects that edit.</summary>
    public ObservableCollection<EditNodeVm> Overview { get; } = new();

    /// <summary>The part has edits to list. Filled with the tree, so it never changes under the
    /// inspector.</summary>
    public bool HasOverview => Overview.Count > 0;

    /// <summary>The selected edit's cards, grouped by material. Built with the tree, so the filter can match
    /// a texture name without the row having been selected.</summary>
    public ObservableCollection<EditMapGroupVm> MapGroups { get; } = new();

    public bool HasMapGroups => MapGroups.Count > 0;

    /// <summary>The skeleton row's read-only bone tree.</summary>
    public IReadOnlyList<SkeletonNodeVm> SkeletonTree { get; init; } = Array.Empty<SkeletonNodeVm>();

    public bool HasSkeletonTree => SkeletonTree.Count > 0;

    /// <summary>The cards below are the part's ORIGINAL maps rather than an edit's, so the set is labelled
    /// once instead of card by card.</summary>
    public bool ShowsOriginalMaps => IsBarePart && MapGroups.Count > 0;

    /// <summary>The label over a bare part's original-map cards. The ordinary heading carries no teaching
    /// line; only an install gate adds the short state that currently disables the cards.</summary>
    public string OriginalMapsLabel => MapGroups.SelectMany(group => group.Cards)
        .FirstOrDefault()?.SubjectRead switch
    {
        EditSubjectRead.Unavailable => Strings.EditOriginalMapsUnavailable,
        EditSubjectRead.Reading => Strings.EditOriginalMapsReading,
        EditSubjectRead.Unreadable => Strings.EditOriginalMapsUnreadable,
        _ => Strings.EditOriginalMaps,
    };

    /// <summary>The install is still being read for this part, so its original maps are not on screen yet.
    /// The card area says so rather than reading as a part with no maps.</summary>
    [ObservableProperty]
    private bool _isReadingOriginals;

    /// <summary>Why this part's original maps are not on screen, once the read has settled, or null where
    /// they are. A settled read with nothing behind it is an answer, and an answer is said out loud.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOriginalsNote))]
    private string? _originalsNote;

    public bool HasOriginalsNote => !string.IsNullOrEmpty(OriginalsNote);

    /// <summary>Where this edit is used, in the words ③ Build uses for the same fact. Every edit's inspector
    /// carries it: naming an edit and knowing whether anything selects it are the two questions asked of one
    /// here, and a hide is an edit like any other.</summary>
    [ObservableProperty] private string _placementSummary = "";

    /// <summary>Where an edit with <paramref name="always"/> and <paramref name="states"/> uses is used.
    /// Always and each state count once, the way the delete confirm counts them. One vocabulary for where an
    /// edit is used, here and in <see cref="DeleteUses"/>: Always and states, never a count of "places".</summary>
    internal static string Uses(bool always, int states) =>
        !always && states == 0 ? Strings.EditNotUsedYet
        : always && states == 0 ? Strings.EditUsedInAlways
        : !always ? Catalog.Plural(states, Strings.EditUsedInStates_One(states), Strings.EditUsedInStates_Other(states))
        : Catalog.Plural(states, Strings.EditUsedInAlwaysAndStates_One(states),
            Strings.EditUsedInAlwaysAndStates_Other(states));

    /// <summary>The delete confirm's one activation fact, in <see cref="Uses"/>'s vocabulary.</summary>
    internal static string DeleteUses(bool always, int states) =>
        !always && states == 0 ? Strings.EditDeleteUnused
        : always && states == 0 ? Strings.EditDeleteUsedInAlways
        : !always ? Catalog.Plural(states, Strings.EditDeleteUsedInStates_One(states),
            Strings.EditDeleteUsedInStates_Other(states))
        : Catalog.Plural(states, Strings.EditDeleteUsedInAlwaysAndStates_One(states),
            Strings.EditDeleteUsedInAlwaysAndStates_Other(states));


    // ---- demand-driven async mesh preview ----
    // The workbench's states, unchanged: shimmer → picture OR quiet "no preview", behind a monotonic request
    // id so an out-of-order completion is rejected rather than landing a stale render.
    //
    // The bitmap is BORROWED. The page holds rendered pictures by what they are of, so a redraw hands the
    // same one back rather than rendering it again; nothing here disposes what it did not make.

    /// <summary>What the page files this row's render under. Empty on a row with no render of its own.</summary>
    public string PreviewKey { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeshPreview))]
    private Bitmap? _meshPreview;

    [ObservableProperty] private bool _isMeshPreviewLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewCause))]
    private bool _isMeshPreviewFailed;

    /// <summary>The render failed because reading it failed, not because there was nothing to draw. Only
    /// that carries the cause line: retry guidance cannot make unrenderable geometry render.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewCause))]
    private bool _meshPreviewThrew;

    public bool HasPreviewCause => IsMeshPreviewFailed && MeshPreviewThrew;

    /// <summary>The vertex-count line under the preview, in the workbench's own wording.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeshMetrics))]
    private string _meshMetrics = "";

    public bool HasMeshPreview => MeshPreview is not null;
    public bool HasMeshMetrics => MeshMetrics.Length > 0;

    private int _meshRequest;

    /// <summary>The page took this row's render away under it. A fresh row says the same thing with
    /// <c>_meshRequest == 0</c> and a forgotten one cannot: its request ids have to keep climbing for a
    /// producer still in flight to stay rejected, so the fact is carried on its own.</summary>
    private bool _meshForgotten;

    /// <summary>Needs loading: nothing present, and either nothing requested, the last attempt failed, or
    /// the render it had was taken away.</summary>
    public bool NeedsMeshPreview =>
        !HasMeshPreview && (_meshRequest == 0 || IsMeshPreviewFailed || _meshForgotten);

    public int BeginMeshPreviewRequest()
    {
        IsMeshPreviewLoading = true;
        IsMeshPreviewFailed = false;
        _meshForgotten = false;
        return ++_meshRequest;
    }

    public bool IsCurrentMeshPreviewRequest(int request) => request == _meshRequest;

    public void SetMeshPreview(EditMeshPreview preview)
    {
        _meshForgotten = false;
        MeshPreview = preview.Image;
        IsMeshPreviewLoading = false;
        IsMeshPreviewFailed = false;
        MeshPreviewThrew = false;
        MeshMetrics = MeshPreviewMetrics.VertexCountLine(preview.OriginalVertexCount, preview.VertexCount);
    }

    /// <summary>Settle into the quiet no-preview tile. <paramref name="threw"/> says the read itself failed,
    /// which is the one failure a retry can fix and the only one that carries a line about it.</summary>
    public void MarkMeshPreviewFailed(bool threw = false)
    {
        MeshPreview = null;
        IsMeshPreviewLoading = false;
        IsMeshPreviewFailed = true;
        MeshPreviewThrew = threw;
        MeshMetrics = "";
    }

    /// <summary>Drop the render this row is showing because what it draws may have changed under it. The next
    /// selection asks for it again.</summary>
    public void ForgetMeshPreview()
    {
        _meshRequest++;
        _meshForgotten = true;
        MeshPreview = null;
        IsMeshPreviewLoading = true;
        IsMeshPreviewFailed = false;
        MeshPreviewThrew = false;
        MeshMetrics = "";
    }

    /// <summary>Let this row go when a rebuild drops it, rejecting anything still in flight. The pictures are
    /// borrowed, so nothing is disposed here — the page owns them and drops what the new tree did not
    /// take.</summary>
    public void Release()
    {
        _meshRequest++;
        MeshPreview = null;
        IsMeshPreviewLoading = true;
        IsMeshPreviewFailed = false;
        MeshPreviewThrew = false;
        foreach (var group in MapGroups)
            foreach (var card in group.Cards) card.ReleaseThumb();
        foreach (var child in Children) child.Release();
    }

    // ---- filter ----

    /// <summary>Text this row is findable by that it does not display as its own name — a part's renderer
    /// slot and the mesh the project recorded for it.</summary>
    public string FilterExtra { get; init; } = "";

    // Lower-cased haystack, memoized. Rebuilt with the tree, so nothing invalidates it mid-life: the cards
    // are built with the row rather than settling into it later.
    private string? _haystack;

    private string Haystack => _haystack ??= string.Join(" ",
            new[] { Title, FilterExtra }
                .Concat(MapGroups.Select(g => g.Title))
                .Concat(MapGroups.SelectMany(g => g.Cards).Select(c => c.FilterText)))
        .ToLowerInvariant();

    /// <summary>Every term hits this row's own text; the caller handles ancestor/descendant roll-up.</summary>
    public bool SelfMatches(IReadOnlyList<string> terms) => terms.All(t => Haystack.Contains(t));
}
