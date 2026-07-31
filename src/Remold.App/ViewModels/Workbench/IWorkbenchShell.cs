using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Remold.Core.Export;
using Remold.Core.Model;
using Remold.Core.Project;

namespace Remold.App.ViewModels.Workbench;

/// <summary>The subject context a workbench verb needs. Threaded onto every actionable node, so a verb
/// identifies its target without the node reaching back into the hosting VM.</summary>
public sealed record WorkbenchSubjectRef(string Character, string Stem, string MeshPrefix, Outfit Outfit);

/// <summary>A card drop that lands on a REPLACED part: the part it belongs to, the donor slot the image
/// ships on, and the donor submeshes it covers (material order IS submesh order, so one stock map dressing
/// several of the part's slots authors all of them). The part's vanilla draws are gone, so the same drop on
/// a game texture would ship nothing — this is the route that makes it the replacement's own map.</summary>
public sealed record DonorMapDrop(string PartToken, DonorMapSlot Slot, IReadOnlyList<int> Submeshes);

/// <summary>Why a donor-map drop can't be taken, worded once. The pre-confirm check and the re-check inside
/// the apply both answer for the same state, so a single home is what keeps them from describing it
/// differently. Each is the TAIL of a status line the caller prefixes with the dropped file's name.</summary>
public static class DonorDropRefusal
{
    /// <summary>Every submesh the card's map dresses is past the replacement's own submesh count, so there is
    /// no donor submesh to author for.</summary>
    public static string PastTheReplacement(string partToken) =>
        $"can't apply here. This map dresses submeshes {partToken}'s replacement doesn't have. "
        + $"Send {partToken} back from Blender to add them.";

    /// <summary>The part stopped carrying a mesh replacement between the drop and the write — a Revert
    /// landed while the confirm was open.</summary>
    public static string NotReplaced(string partToken) =>
        $"can't apply here. {partToken} is no longer replaced. Drop it again to edit the game texture.";
}

/// <summary>What a dropped PNG is about to land on: everything the confirm's body is written from, in one
/// payload rather than a widening parameter list. Exactly one of the three routes applies —
/// <see cref="Donor"/> set is the replacement's own map, <see cref="IsAuthored"/> is an overwrite of a map
/// the replacement already carries, and neither is an ordinary game-texture edit.</summary>
/// <param name="FileName">The dropped file's name, which the card's map name need not match.</param>
/// <param name="MapRole">The card's role label, so the dialog and the card read alike.</param>
/// <param name="TextureName">The GAME texture the card names. Meaningful on the game route; on the other
/// two the card shows it while the part draws something else.</param>
/// <param name="PartToken">The part the card's material belongs to; empty on a card outside a part tree.</param>
/// <param name="Donor">The replaced part's map this drop authors, or null on the other two routes.</param>
/// <param name="IsAuthored">The card already shows a map the replacement carries, so the drop overwrites
/// that file. No map-grain revert stands behind it.</param>
/// <param name="AuthoredLanding">Donor route only: how many of the landing submeshes ALREADY name a file on
/// this slot. Above zero, the drop overwrites maps that are on record — including ones authored from a card
/// the drop didn't land on.</param>
/// <param name="OtherWearers">Game route only: how many OTHER parts draw this texture, so the confirm can
/// state the edit's reach.</param>
/// <param name="SizeNote">One informational line about the incoming image's size differing from the map's,
/// shown as-is and never a reason to refuse. Null when the sizes agree or can't be read.</param>
public sealed record DroppedPngConfirm(string FileName, string MapRole, string TextureName, string PartToken,
    DonorMapDrop? Donor, bool IsAuthored, int AuthoredLanding, int OtherWearers, string? SizeNote);

/// <summary>The imperative plumbing a workbench verb reuses, implemented by <c>MainWindowViewModel</c>
/// and injected into <see cref="WorkbenchVm"/>. The verbs live on the workbench VM; the heavy work stays
/// in the hosting VM, which owns the watchers and settings — the workbench never reaches
/// <c>$parent[Window]</c>, it calls THROUGH this seam. After a mutation the host notifies it, so ✎ and
/// card state refresh with no rebuild. Every materializing verb is async and reports via
/// <see cref="IProgress{T}"/>; a sharing violation while the game runs is a transient BUSY, never a hard
/// failure.</summary>
public interface IWorkbenchShell
{
    /// <summary>Materialize ONE part at the mesh grain, idempotent and recipe-exact. The status line carries
    /// the reason on a hard failure; a part that lands with no textures resolved carries that as a non-fatal
    /// <see cref="PartMaterializeOutcome.Warning"/>. Autosaves on success.</summary>
    Task<PartMaterializeOutcome> MaterializePartAsync(WorkbenchSubjectRef subject, RecipePart recipe, IProgress<string> status, CancellationToken ct);

    /// <summary>Materialize ONE texture at the map grain, idempotent. <paramref name="ownerMeshNames"/> are
    /// the recipe lod0 mesh m_Names of the parts binding it — the target's Users. Autosaves on success.</summary>
    Task<bool> MaterializeTextureAsync(WorkbenchSubjectRef subject, string textureName, string bundleId,
        IReadOnlyList<string> ownerMeshNames, IProgress<string> status, CancellationToken ct);

    /// <summary>Open ONE part in Blender and arm the send-back watcher. The session carries the whole outfit
    /// on its union armature, so a weight can be painted against the full skeleton, while only
    /// <paramref name="recipe"/> can come back.</summary>
    Task OpenPartInBlenderAsync(WorkbenchSubjectRef subject, RecipePart recipe,
        IReadOnlyList<RecipePart> outfitRecipes, IProgress<string> status);

    /// <summary>Open ONE part in Blender on its own workspace glb, with no outfit around it. Same session
    /// description and send-back contract as <see cref="OpenPartInBlenderAsync"/> — the difference is what the
    /// session carries, not how an edit comes home.</summary>
    Task OpenPartAloneInBlenderAsync(WorkbenchSubjectRef subject, RecipePart recipe, IProgress<string> status);

    /// <summary>Materialize every listed part, build the union-armature glb, open the whole outfit at once.</summary>
    Task OpenAllPartsInBlenderAsync(WorkbenchSubjectRef subject, IReadOnlyList<RecipePart> recipes, IProgress<string> status);

    /// <summary>Materialize the texture, arm the watcher, open the workspace PNG in the image editor.</summary>
    Task OpenMapInEditorAsync(WorkbenchSubjectRef subject, string textureName, string bundleId,
        IReadOnlyList<string> ownerMeshNames, IProgress<string> status);

    /// <summary>Open a DONOR-AUTHORED map in the image editor. It carries no bundle and no Texture2D target,
    /// so there is nothing to materialize — the file already IS the editable copy.</summary>
    Task OpenAuthoredMapAsync(string authoredPath, IProgress<string> status);

    /// <summary>Materialize each of the subject's parts and textures, with per-item progress. Honors
    /// cancellation BETWEEN items. Returns the number materialized.</summary>
    Task<int> MaterializeAllAsync(WorkbenchSubjectRef subject, IReadOnlyList<MaterializeItem> items,
        IProgress<string> status, CancellationToken ct);

    /// <summary>Revert one part's glb to its pristine original, glb only. Autosaves.</summary>
    Task RevertPartAsync(WorkbenchSubjectRef subject, string partToken, IProgress<string> status);

    /// <summary>Open a texture's UV guide in the IMAGE EDITOR (layerable there; nothing watches it),
    /// building it on first touch with NO part materialization. Each mesh is drawn from the part's EDITED
    /// workspace glb when it has one, so the guide shows the mod's own UVs. Identified by (bundle, name),
    /// like <see cref="RevertMapAsync"/>.</summary>
    Task OpenMapUvGuideAsync(WorkbenchSubjectRef subject, string textureName, string bundleId,
        IReadOnlyList<(string MeshName, string MeshAddress, int Submesh, string? ModdedGlb)> samplers, IProgress<string> status);

    /// <summary>Revert one texture's PNG to its pristine original, suppressing the watcher's reaction to the
    /// self-write. Identified by (bundle, name) — a same-named map from another bundle is a distinct
    /// target. Autosaves.</summary>
    Task RevertMapAsync(WorkbenchSubjectRef subject, string textureName, string bundleId, IProgress<string> status);

    /// <summary>Apply a dropped <c>.png</c> to a texture: materialize if needed, copy over the workspace
    /// (watcher-suppressed), mark edited. Autosaves.</summary>
    Task ApplyDroppedPngAsync(WorkbenchSubjectRef subject, string textureName, string bundleId,
        IReadOnlyList<string> ownerMeshNames, string path, IProgress<string> status);

    /// <summary>Apply a dropped <c>.png</c> to a map the REPLACEMENT already carries, whichever route
    /// authored it. The file is already what the build ships for that submesh, so there is no target to
    /// materialize or flag — its bytes are the whole edit, and it reaches that submesh alone.
    /// <paramref name="partToken"/> and <paramref name="mapRole"/> are what the status line reports, at the
    /// same grain as the donor route: the generated file name is not the modder's vocabulary.</summary>
    Task ApplyDroppedPngToAuthoredAsync(string authoredPath, string partToken, string mapRole, string path,
        IProgress<string> status);

    /// <summary>Author a dropped <c>.png</c> as a REPLACED part's own map: write it into the mod's
    /// <c>textures/</c> under the donor naming convention and record the authored slot on the part's mesh
    /// target, producing exactly what a Blender session's map would have. The part's Revert drops it with the
    /// mesh edit it belongs to. Autosaves.</summary>
    Task ApplyDroppedPngToDonorMapAsync(WorkbenchSubjectRef subject, DonorMapDrop donor, string mapRole,
        string path, IProgress<string> status);

    /// <summary>Confirm applying a PNG dropped ON a map card, regardless of filename. The body is written
    /// from <paramref name="ask"/> alone, so each of the three routes states what it actually does — which is
    /// the one thing the modder cannot tell from the card, since a replaced part's card still shows the game
    /// texture. The two irreversible routes carry the app's danger styling. Declined resolves false.</summary>
    Task<bool> ConfirmApplyDroppedPngAsync(DroppedPngConfirm ask);

    /// <summary>The tree landed on a node of this subject: start preparing the whole outfit in the
    /// background. Speculative — it returns at once and nothing waits on it. A repeat for an outfit already
    /// preparing or prepared under the open mod is dropped; a preparation that fails drops its entry, so a
    /// later visit prepares the outfit again.</summary>
    void PrewarmSubject(WorkbenchSubjectRef subject);

    /// <summary>Reveal the subject's export folder in the OS file browser.</summary>
    void ShowSubjectInFolder(WorkbenchSubjectRef subject);

    /// <summary>Remove the whole subject — the Edit-header entry to the SAME subject-scoped remove the Pick
    /// uncheck runs. Confirms when it has materialized/edited content; a cancel leaves the mod untouched.</summary>
    Task RemoveSubjectAsync(WorkbenchSubjectRef subject);

    /// <summary>Clipboard copy, so the workbench's copy verb binds to its OWN VM.</summary>
    Task CopyTextAsync(string? text);

    /// <summary>Advance to the Build step, on the workbench VM so it binds to its own DataContext.</summary>
    void GoToBuild();

    /// <summary>Persist a workbench state mutation with no materializing verb of its own.</summary>
    void AutoSaveProject();
}

/// <summary>Whether the part is usable to edit, plus any non-fatal <see cref="Warning"/> — today, that the
/// prefab renderer bound no textures so the part exports untextured. A hard failure is <see cref="Failed"/>,
/// with the reason already on the status line.</summary>
public readonly record struct PartMaterializeOutcome(bool Ok, string? Warning)
{
    public static readonly PartMaterializeOutcome Failed = new(false, null);
    public static PartMaterializeOutcome Ready(string? warning = null) => new(true, warning);
}

/// <summary>A part (mesh grain) or a texture (map grain), with its per-item status label. A part carries its
/// recipe-exact <see cref="Recipe"/>; a texture carries its owning parts' <see cref="OwnerMeshNames"/>.</summary>
public sealed record MaterializeItem(bool IsTexture, string Token, string Label, string BundleId = "",
    IReadOnlyList<string>? OwnerMeshNames = null, RecipePart? Recipe = null);
