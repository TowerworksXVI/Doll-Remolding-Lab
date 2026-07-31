using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Remold.Core.Export;
using Remold.Core.Materials;
using Remold.Core.Migoto;
using Remold.Core.Textures;
using Remold.Core.Workbench;

namespace Remold.App.ViewModels.Workbench;

/// <summary>Drives which inspector panel shows, and the node glyph.</summary>
public enum WorkbenchNodeKind
{
    Subject,
    Part,
    Material,
    Skeleton,
}

/// <summary>
/// One node of the Outfit Workbench tree. ONE node type carries a <see cref="Kind"/> plus that kind's
/// inspector payload, so the tree is one <c>TreeDataTemplate</c> and the inspector one set of panels switched
/// on <see cref="Kind"/>. Preview state lives on the owning Part or map row, so async producers settle
/// independently without rebuilding the tree.
///
/// <para>A node that couldn't be resolved carries a non-null <see cref="Problem"/>, shown as a dim error
/// line.</para>
/// </summary>
public sealed partial class WorkbenchNodeVm : ObservableObject
{
    public WorkbenchNodeVm()
    {
        // Map rows are added after construction, so watch each one: AnyThumbFailed must re-raise LIVE as a
        // map settles into (or out of) the failed state, not only on reselect.
        Maps.CollectionChanged += OnMapsChanged;
    }

    private void OnMapsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (WorkbenchMapVm m in e.NewItems)
                m.PropertyChanged += OnMapPropertyChanged;
        // A row CAN be removed (LoadMapMeta drops an unauthorable HDR/float map), so unsubscribe or its late
        // thumb completion still pokes this node.
        if (e.OldItems is not null)
            foreach (WorkbenchMapVm m in e.OldItems)
                m.PropertyChanged -= OnMapPropertyChanged;
        OnPropertyChanged(nameof(AnyThumbFailed));
    }

    private void OnMapPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkbenchMapVm.IsThumbFailed))
        {
            OnPropertyChanged(nameof(AnyThumbFailed));
            OnPropertyChanged(nameof(ThumbFailureNote));
        }
    }

    /// <summary>Any map row is in the failed thumb state. Recomputes live as maps settle.</summary>
    public bool AnyThumbFailed => Maps.Any(m => m.IsThumbFailed);

    /// <summary>The cause and way out under a failed map set. A map whose authored file is gone can't be
    /// fixed by re-selecting — the file is simply not there. Either route that bound it can put it back, and
    /// the card does not record which one did, so the line names both.</summary>
    public string ThumbFailureNote => Maps.Any(m => m.IsThumbFailed && m.IsAuthoredFileMissing)
        ? "Previews unavailable. A map the replacement carries is gone. Send the part back from Blender, or drop a .png on the card."
        : "Previews unavailable. The game may be holding files. Select the material again to retry.";

    /// <summary>Two-way bound by the window-level TreeViewItem style.</summary>
    [ObservableProperty] private bool _isExpanded = true;

    /// <summary>Hidden when neither this node nor a descendant matches the filter.</summary>
    [ObservableProperty] private bool _isVisible = true;

    public required WorkbenchNodeKind Kind { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    /// <summary>A per-node resolution failure; null on a clean node.</summary>
    public string? Problem { get; init; }

    // ---- verb context ----
    /// <summary>The subject this node belongs to. Null on nodes that carry no verbs.</summary>
    public WorkbenchSubjectRef? Subject { get; init; }
    /// <summary>The part token this node acts on; a Material carries its owning part's.</summary>
    public string PartToken { get; init; } = "";
    /// <summary>The recipe-exact identity a Part carries to the materialize route, so the mesh is read by
    /// catalog-resolved bundle + exact name and NEVER re-derived from prefix+token.</summary>
    public RecipePart? Recipe { get; init; }
    /// <summary>Per-material base-color map in renderer slot order, which IS the submesh alignment (a
    /// placeholder or base-less slot is null). Feeds the mesh preview's texture sampling.</summary>
    public IReadOnlyList<Remold.Core.Workbench.SubjectMap?> SubmeshBaseMaps { get; init; } =
        Array.Empty<Remold.Core.Workbench.SubjectMap?>();

    /// <summary>Per-material RMO map, in the same renderer slot order as <see cref="SubmeshBaseMaps"/> (a
    /// placeholder or RMO-less slot is null). The emissive mask rides an RMO's alpha and glTF has no channel
    /// for it, so this is the stock map an authored RMO's mask is read back off when the returned glb has no
    /// record of its own.</summary>
    public IReadOnlyList<Remold.Core.Workbench.SubjectMap?> SubmeshRmoMaps { get; init; } =
        Array.Empty<Remold.Core.Workbench.SubjectMap?>();

    /// <summary>This material's index in its part's renderer slot order, which IS the donor submesh index a
    /// send-back's authored textures are recorded under. -1 elsewhere.</summary>
    public int MaterialIndex { get; init; } = -1;

    /// <summary>Part only — the authored donor-albedo overlay per CURRENT-mesh submesh (null = vanilla). May
    /// be longer than <see cref="SubmeshBaseMaps"/> when the edit added submeshes.</summary>
    public IReadOnlyList<string?> AuthoredBaseMaps { get; set; } = Array.Empty<string?>();

    /// <summary>Part only — the workspace PNG of each EDITED base-color map, aligned with
    /// <see cref="SubmeshBaseMaps"/> (null = the game texture is untouched). This is how a dropped or
    /// repainted map reaches the part's own preview instead of only its map card.</summary>
    public IReadOnlyList<string?> EditedBaseMaps { get; set; } = Array.Empty<string?>();

    /// <summary>Part only — at least one submesh samples a map the MODDER owns (an authored donor map, or
    /// an edited game texture). THE rule for the persisted mesh-thumb cache: a part this is true of
    /// neither reads nor writes it — the cache's key carries game identity only and would serve one
    /// project's pixels to every other. This answers the cache-READ side; a WRITE is decided from what the
    /// render's samplers actually took (<c>WorkbenchVm.BuildPreviewSamplers</c>): these lists mutate on
    /// the UI thread while previews render on workers, and only the write side poisons the cache.</summary>
    public bool HasOwnBaseMaps =>
        AuthoredBaseMaps.Any(p => p is not null) || EditedBaseMaps.Any(p => p is not null);

    /// <summary>Part only — the part's whole game-derived material list, captured when a send-back first
    /// reshapes the children, so a revert restores it without a tree rebuild. The children keep the SAME
    /// instances for every submesh the returned mesh still has; only the ones a send-back carrying FEWER
    /// submeshes left no place for live here alone. Null while no send-back shapes the children.</summary>
    public List<WorkbenchNodeVm>? StashedGameChildren { get; set; }

    /// <summary>Part only — the donor-material shape the children mirror, null when game-derived. Guards the
    /// reconcile against rebuilding identical children on every refresh.</summary>
    public string? DonorShapeKey { get; set; }

    // ---- edited / materialized state (badges + verb enablement) ----
    /// <summary>This node or, by rollup, a descendant is edited. Distinct from <see cref="MeshEdited"/>,
    /// which Part Revert keys on — the mesh alone, not the rollup.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditBadge))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    private bool _isEdited;

    /// <summary>Part only — mirrors <c>ModProject.Hidden</c>: the mesh doesn't draw in the built mod. Edits
    /// stay in the workspace and resume building on unhide.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HideLabel))]
    private bool _isHiddenInMod;

    public string HideLabel => IsHiddenInMod ? "Unhide" : "Hide in mod";

    /// <summary>A Part's own MESH target is edited — kept separate from the <see cref="IsEdited"/> rollup so
    /// a texture-only edit doesn't enable a no-op mesh Revert.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    private bool _meshEdited;

    /// <summary>An editable copy exists. Drives Revert enablement and the Open label.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    private bool _isMaterialized;

    /// <summary>This node's own verb is running — disables its buttons so a second click can't race it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    [NotifyPropertyChangedFor(nameof(CanOpenInBlender))]
    [NotifyPropertyChangedFor(nameof(CanOpenWithReferences))]
    [NotifyPropertyChangedFor(nameof(BlenderHint))]
    [NotifyPropertyChangedFor(nameof(BlenderAloneHint))]
    [NotifyPropertyChangedFor(nameof(ReferencesHint))]
    [NotifyPropertyChangedFor(nameof(MaterializeAllHint))]
    [NotifyPropertyChangedFor(nameof(RemoveSubjectHint))]
    private bool _isBusy;

    /// <summary>A Blender executable was located. Detection belongs to the shell, which pushes the answer
    /// down here because the button that needs it hangs off the node.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenInBlender))]
    [NotifyPropertyChangedFor(nameof(CanOpenWithReferences))]
    [NotifyPropertyChangedFor(nameof(BlenderHint))]
    [NotifyPropertyChangedFor(nameof(BlenderAloneHint))]
    [NotifyPropertyChangedFor(nameof(ReferencesHint))]
    private bool _blenderFound = true;

    /// <summary>Part only — which half of the recoverable-skin rule refuses this part's game mesh, null when
    /// it can be replaced OR while the answer hasn't been read yet. Read lazily, once, when the part is
    /// selected: the mesh read is a bundle deobfuscate plus a type-tree deserialize, and the Open button only
    /// renders for the selected node. Never set on a Subject node, whose Open-all carries an unreplaceable
    /// part as context rather than refusing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenInBlender))]
    [NotifyPropertyChangedFor(nameof(CanOpenWithReferences))]
    [NotifyPropertyChangedFor(nameof(BlenderHint))]
    [NotifyPropertyChangedFor(nameof(BlenderAloneHint))]
    [NotifyPropertyChangedFor(nameof(ReferencesHint))]
    private StreamDump.SkinRefusal? _meshReplaceBlock;

    /// <summary>The <see cref="MeshReplaceBlock"/> read, in flight or settled — the memo that keeps a
    /// re-selection from re-reading the mesh, and what a verb awaits when a click beats the read that
    /// selection started. A tree rebuild makes fresh nodes, so it re-reads there.</summary>
    internal Task<StreamDump.SkinRefusal?>? MeshReplaceGate { get; set; }

    /// <summary>Subject only — every part of this subject draws from a STATIC renderer slot, so the combined
    /// session it would open carries nothing: only skinned parts join a combined rigged glb. Set at
    /// construction from the prefab's renderer classes, so it never lags the button the way a lazy mesh read
    /// does. A MIXED subject is false and opens as before, carrying its skinned parts.</summary>
    public bool AllPartsStatic { get; init; }

    /// <summary>Part only — this part draws from a STATIC renderer slot, so it is not one of the parts a
    /// combined rigged session carries. Set at construction from the prefab's renderer class, the same read
    /// <see cref="AllPartsStatic"/> comes from, so it never lags the buttons.</summary>
    public bool IsStaticPart { get; init; }

    /// <summary>Part only — a Blender session opened from THIS part's row is still running. Both of the
    /// part's opens refuse while it is: two live sessions on one part send back to the same file, so the last
    /// Send would take it and the screen would say nothing. Pushed by the shell, which owns the process
    /// handle; other parts are unaffected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenInBlender))]
    [NotifyPropertyChangedFor(nameof(CanOpenWithReferences))]
    [NotifyPropertyChangedFor(nameof(BlenderHint))]
    [NotifyPropertyChangedFor(nameof(BlenderAloneHint))]
    [NotifyPropertyChangedFor(nameof(ReferencesHint))]
    private bool _isOpenInBlender;

    /// <summary>Open-in-Blender enablement and its hover reason, from the one
    /// <see cref="BlenderGate"/> answer. Drives the subject's open-all and the part's LONE open, which reach
    /// a static part; the references open answers <see cref="CanOpenWithReferences"/> instead.</summary>
    public bool CanOpenInBlender =>
        BlenderGate.Reason(BlenderFound, IsBusy, MeshReplaceBlock, AllPartsStatic, IsOpenInBlender) is null;

    /// <summary>The Open tooltip: why it's off when it is (shown via ToolTip.ShowOnDisabled), else what the
    /// verb does — the subject button opens the whole outfit, the part button one part with the outfit around
    /// it.</summary>
    public string BlenderHint => BlenderGate.Reason(BlenderFound, IsBusy, MeshReplaceBlock, AllPartsStatic, IsOpenInBlender)
        ?? (IsSubject ? BlenderGate.ReadyAll : BlenderGate.ReadyPart);

    /// <summary>The tooltip for the part's outfit-free Open. Same gate as <see cref="BlenderHint"/> — the
    /// two buttons are enabled and refused together — and a different ready line, since what they differ in is
    /// exactly what the modder is choosing between.</summary>
    public string BlenderAloneHint => BlenderGate.Reason(BlenderFound, IsBusy, MeshReplaceBlock, AllPartsStatic, IsOpenInBlender)
        ?? BlenderGate.ReadyPartAlone;

    /// <summary>The references open's own enablement: everything <see cref="CanOpenInBlender"/> answers, plus
    /// the static-part rule. The references session is the combined rigged glb, which carries the SKINNED
    /// parts only — opening a static part "with References" would hand back a session the part is not in.</summary>
    public bool CanOpenWithReferences =>
        BlenderGate.Reason(BlenderFound, IsBusy, MeshReplaceBlock, AllPartsStatic, IsOpenInBlender, IsStaticPart) is null;

    /// <summary>The references open's tooltip, on its own gate — a static part's refusal names the button
    /// that DOES open it.</summary>
    public string ReferencesHint =>
        BlenderGate.Reason(BlenderFound, IsBusy, MeshReplaceBlock, AllPartsStatic, IsOpenInBlender, IsStaticPart)
        ?? BlenderGate.ReadyPart;

    public bool HasEditBadge => IsEdited;
    /// <summary>There IS a mesh edit to undo: materialized AND its MESH edited. A texture-only edit does NOT
    /// count. Kept apart from <see cref="CanRevert"/> so the verb can refuse an ineligible node before it
    /// reports a wait, and so the hint can tell "nothing to revert" from "not right now".</summary>
    public bool HasEditToRevert => IsMaterialized && MeshEdited;
    /// <summary>Enabled only when there is a mesh edit to undo AND the node is idle.</summary>
    public bool CanRevert => HasEditToRevert && !IsBusy;
    /// <summary>The Revert tooltip: the hint when enabled, else why it's off (shown via
    /// ToolTip.ShowOnDisabled). ORDERED the way the verb refuses — what this button can never undo first, a
    /// wait after it, so a line promising "try again" is never shown for a click that will never work. A
    /// texture-only edit names the verb that DOES undo it, and "nothing to revert" is left for a node that
    /// truly has none.</summary>
    public string RevertHint => CanRevert ? "Restore the original game mesh"
        : IsEdited && !MeshEdited ? "Only textures are edited here. Revert them on the map cards."
        : HasEditToRevert ? BlenderGate.Busy   // eligible and still off: the wait is all that is left
        : "Nothing to revert yet";

    /// <summary>The subject Materialize-all tooltip: why it's off when it is (shown via
    /// ToolTip.ShowOnDisabled), else what the verb does. The row's own verb running is the only thing that
    /// turns the button off.</summary>
    public string MaterializeAllHint => IsBusy ? BlenderGate.Busy
        : "Prepare editable copies of every part and texture and add them to the mod";

    /// <summary>The subject Remove tooltip, on the same one gate as
    /// <see cref="MaterializeAllHint"/>.</summary>
    public string RemoveSubjectHint => IsBusy ? BlenderGate.Busy
        : "Drop this subject and its materialized/edited files from the mod";

    public ObservableCollection<WorkbenchNodeVm> Children { get; } = new();

    // ---- inspector payload (only the fields relevant to Kind are set) ----
    public string InspectorHeader { get; init; } = "";
    public string InspectorDetail { get; init; } = "";
    /// <summary>A secondary inspector note.</summary>
    public string? InspectorNote { get; init; }
    public IReadOnlyList<string> InspectorProblems { get; init; } = Array.Empty<string>();
    /// <summary>A scrollable string list — part metrics, clip names, loose-asset rows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInspectorLines))]
    private IReadOnlyList<string> _inspectorLines = Array.Empty<string>();
    /// <summary>The Skeleton node's read-only bone tree; empty on every other kind, and it supersedes
    /// <see cref="InspectorLines"/> there.</summary>
    public IReadOnlyList<SkeletonNodeVm> SkeletonTree { get; init; } = Array.Empty<SkeletonNodeVm>();
    public ObservableCollection<WorkbenchMapVm> Maps { get; } = new();

    // ---- view helpers ----
    public bool IsSubject => Kind == WorkbenchNodeKind.Subject;
    public bool IsPart => Kind == WorkbenchNodeKind.Part;
    public bool IsMaterial => Kind == WorkbenchNodeKind.Material;
    public bool IsSkeleton => Kind == WorkbenchNodeKind.Skeleton;

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public bool HasGlyph => Glyph.Length > 0;
    public bool HasProblem => !string.IsNullOrEmpty(Problem);
    public bool HasInspectorProblems => InspectorProblems.Count > 0;
    public bool HasInspectorNote => !string.IsNullOrEmpty(InspectorNote);
    public bool HasInspectorLines => InspectorLines.Count > 0;
    public bool HasSkeletonTree => SkeletonTree.Count > 0;
    public bool HasMaps => Maps.Count > 0;

    // ---- demand-driven async part mesh preview ----
    // The same mutually-exclusive states as map thumbnails: shimmer → bitmap OR quiet "no preview".

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeshPreview))]
    private Bitmap? _meshPreview;

    [ObservableProperty] private bool _isMeshPreviewLoading = true;
    [ObservableProperty] private bool _isMeshPreviewFailed;
    /// <summary>The failure came from game-file access/resolution. Geometry failures keep the quiet tile —
    /// retry guidance can't make malformed geometry renderable.</summary>
    [ObservableProperty] private bool _hasMeshPreviewFailureCause;

    /// <summary>Whether the current bitmap came from an edited workspace GLB.</summary>
    public bool IsPreviewingEditedMesh { get; private set; }
    private int _meshPreviewRequest;
    // The in-flight request came from the WARM-UP pass, not a selection/edit. Lets a real selection SUPERSEDE
    // it instead of memoizing behind it — a warm request must never make a click wait.
    private bool _meshWarm;

    public bool HasMeshPreview => MeshPreview is not null;

    /// <summary>Needs loading: nothing present, and either nothing requested, the last attempt failed, or the
    /// only request in flight is a warm-up one. A shown preview or an in-flight DEMAND load is memoized.</summary>
    public bool NeedsMeshPreview => !HasMeshPreview && (_meshPreviewRequest == 0 || IsMeshPreviewFailed || _meshWarm);

    /// <summary>Nothing loaded and nothing yet requested — warm-up never supersedes an in-flight or settled
    /// request; that's demand's job.</summary>
    public bool NeedsMeshPreviewWarmup => !HasMeshPreview && _meshPreviewRequest == 0;

    /// <summary>A DEMAND request: clears the warm flag, so a warm completion landing after it is rejected by
    /// the request-id guard.</summary>
    public int BeginMeshPreviewRequest(bool edited = false)
    {
        _meshWarm = false;
        MarkMeshPreviewRetrying(edited);
        return ++_meshPreviewRequest;
    }

    /// <summary>A WARM-UP request: identical to a demand one except it marks the in-flight request warm, so
    /// a later selection supersedes it.</summary>
    public int BeginMeshPreviewWarmup()
    {
        _meshWarm = true;
        MarkMeshPreviewRetrying(false);
        return ++_meshPreviewRequest;
    }

    public bool IsCurrentMeshPreviewRequest(int request) => request == _meshPreviewRequest;

    public void SetMeshPreview(Bitmap bitmap, int vertexCount, int? originalVertexCount = null, bool edited = false)
    {
        if (!ReferenceEquals(MeshPreview, bitmap)) MeshPreview?.Dispose();
        MeshPreview = bitmap;
        IsMeshPreviewLoading = false;
        IsMeshPreviewFailed = false;
        HasMeshPreviewFailureCause = false;
        IsPreviewingEditedMesh = edited;
        InspectorLines = new[] { MeshPreviewMetrics.VertexCountLine(originalVertexCount, vertexCount) };
    }

    public void MarkMeshPreviewFailed(int? vertexCount = null, bool edited = false,
        bool environmentFailure = false)
    {
        MeshPreview?.Dispose();
        MeshPreview = null;
        IsMeshPreviewLoading = false;
        IsMeshPreviewFailed = true;
        HasMeshPreviewFailureCause = environmentFailure;
        IsPreviewingEditedMesh = edited;
        InspectorLines = vertexCount is { } count
            ? new[] { MeshPreviewMetrics.VertexCountLine(null, count) }
            : Array.Empty<string>();
    }

    public void MarkMeshPreviewRetrying(bool edited = false)
    {
        IsMeshPreviewLoading = true;
        IsMeshPreviewFailed = false;
        HasMeshPreviewFailureCause = false;
        IsPreviewingEditedMesh = edited;
    }

    /// <summary>Drop the shown preview because a map it sampled changed on disk, and leave the row reading as
    /// unloaded so selecting it renders again. The request id still advances, so a completion for the
    /// pre-change maps is rejected rather than restoring what it drew. Distinct from
    /// <see cref="ReleaseMeshPreview"/>, which frees a tree being torn down and expects no reload.</summary>
    public void InvalidateMeshPreview()
    {
        _meshPreviewRequest++;
        // Not literally a warm request; this is the flag that makes NeedsMeshPreview true again so demand
        // picks the row up. (NeedsMeshPreviewWarmup stays false — a background sweep should not re-render
        // every part of the outfit for one repainted map.)
        _meshWarm = true;
        MeshPreview?.Dispose();
        MeshPreview = null;
        IsMeshPreviewLoading = true;
        IsMeshPreviewFailed = false;
        HasMeshPreviewFailureCause = false;
        IsPreviewingEditedMesh = false;
    }

    public void ReleaseMeshPreview()
    {
        _meshPreviewRequest++;
        _meshWarm = false;
        MeshPreview?.Dispose();
        MeshPreview = null;
        IsMeshPreviewLoading = true;
        IsMeshPreviewFailed = false;
        HasMeshPreviewFailureCause = false;
        IsPreviewingEditedMesh = false;
    }

    /// <summary>The tree glyph for this kind — a type marker, not a status badge.</summary>
    public string Glyph => Kind switch
    {
        WorkbenchNodeKind.Part => "◇",
        WorkbenchNodeKind.Material => "▦",
        WorkbenchNodeKind.Skeleton => "⌇",
        _ => "",
    };

    // Lower-cased filter haystack, memoized — but INVALIDATED whenever InspectorLines changes: preview
    // workers replace those lines AFTER the tree is built, so a frozen haystack would miss what the
    // inspector now shows.
    private string? _haystack;
    private string Haystack => _haystack ??= BuildHaystack();

    private string BuildHaystack()
    {
        var parts = new List<string> { Title, Subtitle };
        parts.AddRange(Maps.Select(m => m.TextureName));
        parts.AddRange(InspectorLines);
        return string.Join(" ", parts).ToLowerInvariant();
    }

    /// <summary>Invalidate the memoized haystack and notify the owner to re-run the active filter — else a
    /// filter typed while previews were settling freezes the pre-metric haystack.</summary>
    partial void OnInspectorLinesChanged(IReadOnlyList<string> value)
    {
        _haystack = null;
        HaystackInvalidated?.Invoke();
    }

    /// <summary>Set by <see cref="WorkbenchVm"/> when it owns this node. Null on a bare test node.</summary>
    internal Action? HaystackInvalidated;

    /// <summary>Every term hits this node's OWN text; the caller handles ancestor/descendant roll-up.</summary>
    public bool SelfMatches(IReadOnlyList<string> terms) => terms.All(t => Haystack.Contains(t));

    // ---- factory: turn a resolver map into a lazy-dimensions row VM ----
    /// <summary>The row's packed-ness is read off the LABEL, not the shader slot: a row the slot didn't name
    /// still reaches the RMO label through the texture-name suffix, and the legend and the opaque thumbnail
    /// both have to follow the label the card actually shows.</summary>
    internal static WorkbenchMapVm MapRow(Remold.Core.Workbench.SubjectMap map,
        WorkbenchSubjectRef? subject = null, IReadOnlyList<string>? ownerMeshNames = null,
        string partToken = "", IReadOnlyList<int>? boundSubmeshes = null)
    {
        var label = RoleLabel(map.Slot, map.TextureName);
        bool rmo = label == TextureMap.RmoLabel;
        return new WorkbenchMapVm(label, map.Slot, map.TextureName, map.BundleId)
        {
            Subject = subject,
            OwnerMeshNames = ownerMeshNames ?? Array.Empty<string>(),
            PartToken = partToken,
            BoundSubmeshes = boundSubmeshes ?? Array.Empty<int>(),
            IsRmo = rmo,
            MapInfo = rmo ? WorkbenchMapVm.RmoCardInfo : null,
        };
    }

    /// <summary>A friendly map role from the shader slot, falling back to the texture-name suffix vocabulary
    /// (<see cref="TextureMap"/>).</summary>
    private static string RoleLabel(string slot, string textureName)
    {
        if (MaterialResolver.IsBaseColor(slot)) return TextureMap.BaseColorLabel;
        if (MaterialResolver.IsNormal(slot)) return TextureMap.NormalLabel;
        if (MaterialResolver.IsRmo(slot)) return TextureMap.RmoLabel;
        return TextureMap.Label(textureName);
    }
}

/// <summary>One texture-map row of a Material inspector. Dimensions are read LAZILY on node selection ("…"
/// until then, "unavailable" on failure); no pixels are decoded.</summary>
public sealed partial class WorkbenchMapVm : ObservableObject
{
    public WorkbenchMapVm(string label, string slot, string textureName, string bundleId)
    {
        MapLabel = label;
        SlotName = slot;
        TextureName = textureName;
        BundleId = bundleId;
    }

    public string MapLabel { get; }
    public string SlotName { get; }
    public string TextureName { get; }
    public string BundleId { get; }

    /// <summary>What a packed map's channels hold, for the role line's tooltip. Null on a row whose role
    /// name already says everything — Avalonia shows no tooltip for a null tip.</summary>
    public string? MapInfo { get; init; }

    /// <summary>This row is the packed RMO. Its alpha is the emissive mask, not coverage, which is why the
    /// thumbnail composites over it instead of sampling it as transparency.</summary>
    public bool IsRmo { get; init; }

    /// <summary>Shows the role line's ℹ — the cue that a legend answers a hover.</summary>
    public bool HasMapInfo => MapInfo is not null;

    /// <summary>The RMO's channel legend.</summary>
    public const string RmoChannels =
        "R roughness · G metallic · B occlusion · A emissive mask (specular level on stocking parts)";

    /// <summary>What a map CARD's legend says beyond the channels. The legend names four and the tile above it
    /// shows three: alpha is the emissive mask, and the thumbnail forces it opaque or the colour under the mask
    /// wouldn't be visible at all. The change-list chip carries the channels alone, having no tile to qualify.
    /// </summary>
    public const string RmoCardInfo = RmoChannels + ". The thumbnail shows RGB only.";

    /// <summary>Whether a GAME bundle backs this row; false on a donor-derived one (empty bundle id). THE
    /// single home of that rule — every bundle-keyed verb and lazy read routes to
    /// <see cref="AuthoredPath"/> instead, or stays off.</summary>
    public bool HasBundle => BundleId.Length > 0;

    // ---- verb context ----
    /// <summary>The subject this map belongs to, for the map-grain verbs.</summary>
    public WorkbenchSubjectRef? Subject { get; init; }
    /// <summary>The recipe lod0 mesh m_Names of the parts whose materials bind this texture — the target's
    /// Users, carried recipe-exact so a cross-prefix part's mesh isn't lost to a prefix+token derivation.</summary>
    public IReadOnlyList<string> OwnerMeshNames { get; init; } = Array.Empty<string>();

    /// <summary>The part this card's material belongs to. Empty on a card built outside a part's tree.</summary>
    public string PartToken { get; init; } = "";

    /// <summary>Which of the OWNING PART's renderer material slots bind this texture on this card's slot
    /// kind — and so, since material order IS submesh order, which donor submeshes a map authored here lands
    /// on. Several when one stock map dresses several of the part's slots. Empty on a card with no part
    /// context.</summary>
    public IReadOnlyList<int> BoundSubmeshes { get; init; } = Array.Empty<int>();

    // ---- edited / materialized state ----
    /// <summary>The texture has an editable copy (a Texture2D target exists).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    private bool _isMaterialized;

    /// <summary>The texture's target is flagged edited.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditBadge))]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    private bool _isEdited;

    /// <summary>Absolute path of the map the replacement carries where this stock map was — a send-back's, a
    /// card drop's, or an adopted texture edit; null means the part samples the vanilla map here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditBadge))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    [NotifyPropertyChangedFor(nameof(HasOrigin))]
    [NotifyPropertyChangedFor(nameof(AuthoredFileName))]
    private string? _authoredPath;

    /// <summary>The card shows a map bound to the replacement, not the game texture its name line names — so
    /// the card's own text has to say what it is showing. False on a stock card.</summary>
    public bool HasOrigin => AuthoredPath is not null;

    /// <summary>The origin line itself: one phrase for every authored card, whether the map arrived on a
    /// Blender send-back or was dropped onto the card.</summary>
    public const string AuthoredOrigin = "the replacement's map";

    /// <summary>The build ships its own flat map on this slot rather than an image or the part's stock one
    /// (<see cref="Remold.Core.Project.BlankedSlots"/>). Every way that happens names no file, so the card
    /// still shows the game texture the slot had — this word is the only place the state is visible.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    private bool _isBlanked;

    /// <summary>The blanked line itself: one word for every blanked card.</summary>
    public const string BlankedNote = "blanked";

    /// <summary>The authored file behind the origin line, for its tooltip; empty on a stock card.</summary>
    public string AuthoredFileName => AuthoredPath is null ? "" : Path.GetFileName(AuthoredPath);

    /// <summary>This card's own Open/Revert verb is running — disables its buttons.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    [NotifyPropertyChangedFor(nameof(CanOpenUvGuide))]
    [NotifyPropertyChangedFor(nameof(OpenHint))]
    private bool _isBusy;

    public bool HasEditBadge => IsEdited || AuthoredPath is not null;

    /// <summary>There IS a map edit to undo: materialized AND edited. Kept apart from
    /// <see cref="CanRevert"/> so the verb can refuse an ineligible card before it reports a wait, and so the
    /// hint can tell "nothing to revert" from "not right now".</summary>
    public bool HasEditToRevert => IsMaterialized && IsEdited;
    /// <summary>Enabled only when there is a map edit to undo AND the card is idle.</summary>
    public bool CanRevert => HasEditToRevert && !IsBusy;
    /// <summary>The Revert tooltip: the hint when enabled, else why it's off. ORDERED the way the verb
    /// refuses — what this button can never undo first, a wait after it, so a line promising "try again" is
    /// never shown for a click that will never work. Neither an authored map nor a blanked slot has a
    /// map-grain way back: both belong to the part's replacement, and the part's own Revert is what drops
    /// them.</summary>
    public string RevertHint => CanRevert
        ? AuthoredPath is not null
            // The card is BOTH an edited game texture and the replacement's adopted map, so one revert
            // settles both: the edit goes, and the slot returns to the stock map.
            ? "Restore the original texture. The replacement returns to the stock map too."
            : "Restore the original texture"
        : AuthoredPath is not null
            ? "This map belongs to the replacement. Revert the part to restore the game texture. "
              + "That discards the part's mesh edit too."
        : IsBlanked
            ? "This slot was blanked by the mesh edit. Revert the part to restore the game texture. "
              + "That discards the part's mesh edit too."
        : HasEditToRevert ? BlenderGate.Busy   // eligible and still off: the wait is all that is left
        : "Nothing to revert yet";

    /// <summary>The card's Open tooltip: why it's off when it is (shown via ToolTip.ShowOnDisabled), else
    /// what the verb does. The card's own verb running is the only thing that turns the button off — a row
    /// with no file to open stays live and answers on the status line.</summary>
    public string OpenHint => IsBusy ? BlenderGate.Busy : "Edit this texture in an image editor";

    /// <summary>The guide is drawn from the GAME mesh keyed by (bundle, name), so a donor-derived row has no
    /// guide to build.</summary>
    public bool CanOpenUvGuide => HasBundle && !IsBusy;

    /// <summary>The UV tooltip: what the guide is for when enabled, else why it's off.</summary>
    public string UvHint => HasBundle
        ? "Open this texture's UV guide: the white wireframe of the UV islands that land on this image, to layer under the paint"
        : NoUvGuideOnDonorMap;

    /// <summary>Why a donor-derived row has no UV guide. One sentence for the tooltip and the refused
    /// verb, so the card and the status line can't disagree.</summary>
    public const string NoUvGuideOnDonorMap = "No UV guide for the replacement's own map";

    /// <summary>The lazily-read "W×H" — "…" while loading, "unavailable" on a read failure.</summary>
    [ObservableProperty] private string _dimensions = "…";

    private int _dimsRequest;

    /// <summary>Begin a dimensions read, returning the generation to check on completion: a later read
    /// supersedes an earlier one, so an out-of-order completion can't land a stale size.</summary>
    public int BeginDimsRequest() => ++_dimsRequest;

    /// <summary>Whether <paramref name="request"/> is still the newest dimensions read.</summary>
    public bool IsCurrentDimsRequest(int request) => request == _dimsRequest;

    // ---- demand-driven async thumbnail ----
    // Three mutually-exclusive states: loading (shimmer) → loaded OR failed ("no preview"). Decoded off the UI
    // thread, assigned on the dispatcher; a failure is quiet and never cached, so re-selection retries. A
    // monotonically increasing request id rejects and disposes out-of-order completions, so an older decode
    // can't land on a newer file.

    /// <summary>The decoded bitmap once it lands; null while loading or on failure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumb))]
    private Bitmap? _thumbnail;

    /// <summary>The thumb is still building. Starts true.</summary>
    [ObservableProperty] private bool _isThumbLoading = true;

    /// <summary>The thumb couldn't be produced. Not cached — re-selecting the material retries.</summary>
    [ObservableProperty] private bool _isThumbFailed;

    /// <summary>The row shows an authored map whose file wasn't on disk at the last attempt — the one failure
    /// re-selecting cannot fix, as opposed to the transient game-holds-the-file case.</summary>
    [ObservableProperty] private bool _isAuthoredFileMissing;

    public bool HasThumb => Thumbnail is not null;

    private int _thumbRequest;
    // The in-flight request came from the WARM-UP pass. Lets a selection SUPERSEDE it instead of memoizing
    // behind it — a warm request must never make a click wait for its serial turn.
    private bool _thumbWarm;

    /// <summary>Begin a load ONLY if this row still needs one: never loaded, failed and retried, or held by a
    /// WARM-UP request a selection now supersedes. Null when a thumb is present or a DEMAND load is already in
    /// flight.</summary>
    public int? BeginThumbRequestIfNeeded()
    {
        if (HasThumb) return null;                                            // memoized
        if (_thumbRequest != 0 && !IsThumbFailed && !_thumbWarm) return null; // a DEMAND load is in flight
        _thumbWarm = false;
        MarkThumbRetrying();
        return ++_thumbRequest;
    }

    /// <summary>Begin a WARM-UP load ONLY if nothing is loaded and nothing yet requested. Warm-up never
    /// supersedes an in-flight or settled request; a selection does.</summary>
    public int? BeginThumbWarmupIfNeeded()
    {
        if (HasThumb) return null;                             // memoized
        if (_thumbRequest != 0 && !IsThumbFailed) return null; // any load in flight — don't double-issue
        _thumbWarm = true;
        MarkThumbRetrying();
        return ++_thumbRequest;
    }

    /// <summary>Begin a load unconditionally — an edit/revert refresh must supersede any in-flight vanilla or
    /// warm-up load.</summary>
    public int BeginThumbRequest()
    {
        _thumbWarm = false;
        MarkThumbRetrying();
        return ++_thumbRequest;
    }

    /// <summary>Whether <paramref name="request"/> is still the newest; a stale completion is rejected and
    /// its bitmap disposed by the assigning continuation.</summary>
    public bool IsCurrentThumbRequest(int request) => request == _thumbRequest;

    /// <summary>Off-thread producers call these ON the UI dispatcher to settle the thumb into one state.</summary>
    public void SetThumb(Bitmap bmp)
    {
        // Dispose before overwriting, or a double-producer (a stale batch and a retry both landing) leaks the
        // superseded bitmap.
        if (!ReferenceEquals(Thumbnail, bmp)) Thumbnail?.Dispose();
        Thumbnail = bmp;
        IsThumbLoading = false;
        IsThumbFailed = false;
    }

    public void MarkThumbFailed()
    {
        // Dispose before nulling, or a stale failing completion landing after a successful one drops a live
        // native bitmap on the floor.
        Thumbnail?.Dispose();
        Thumbnail = null;
        IsThumbLoading = false;
        IsThumbFailed = true;
    }

    /// <summary>Re-arm the loading state before a retry, so the shimmer returns instead of the stale tile.</summary>
    public void MarkThumbRetrying()
    {
        IsThumbLoading = true;
        IsThumbFailed = false;
    }

    /// <summary>Dispose the bitmap and re-arm loading, when a superseding rebuild drops this row's tree.
    /// Bumps the request id so an in-flight producer's completion is rejected rather than resurrecting the
    /// bitmap.</summary>
    public void ReleaseThumb()
    {
        _thumbRequest++;
        _thumbWarm = false;
        Thumbnail?.Dispose();
        Thumbnail = null;
        IsThumbLoading = true;
        IsThumbFailed = false;
    }
}

/// <summary>One node of the read-only skeleton tree: a view wrapper over
/// <see cref="Remold.Core.Workbench.SkeletonBoneNode"/>. Every node starts collapsed, and expanding one with
/// exactly ONE child chain-expands that child, so a straight bone chain opens in one click.</summary>
public sealed partial class SkeletonNodeVm : ObservableObject
{
    public SkeletonNodeVm(Remold.Core.Workbench.SkeletonBoneNode node)
    {
        Name = node.Name;
        HasChildren = node.HasChildren;
        // the backing field stays false, so the chain-expand handler doesn't fire at construction
        Children = node.Children.Select(c => new SkeletonNodeVm(c)).ToList();
    }

    public string Name { get; }
    public bool HasChildren { get; }
    public IReadOnlyList<SkeletonNodeVm> Children { get; }

    /// <summary>Collapsed by default; two-way bound by the window-level TreeViewItem style.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Chain-expand down a straight single-child chain, until the first node with 0 or 2+ children.
    /// Collapsing does NOT cascade — only the toggled node collapses.</summary>
    partial void OnIsExpandedChanged(bool value)
    {
        if (value && Children.Count == 1) Children[0].IsExpanded = true;
    }
}
