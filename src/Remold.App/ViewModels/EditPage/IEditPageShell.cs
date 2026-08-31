using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Remold.Core.Project;

namespace Remold.App.ViewModels.EditPage;

/// <summary>One edit, in the terms every seam call addresses it by: the part it answers and the id the
/// session knows it under. The label rides along so a status line or a dialog can name it without a second
/// read.</summary>
public sealed record EditRef(TargetPart Part, string EditDefinitionId, string Label);

/// <summary>The slot one <see cref="BindingKind.SourceSlot"/> answer points at: the edit that owns it, or
/// null where it is an exact game slot nobody's edit owns.</summary>
public sealed record EditSlotSource(string? EditDefinitionId, string SlotId);

/// <summary>One exact place inside one edit, carried whole for the gesture that opened it. A long-lived
/// editor transport re-anchors this address to the session's current slot before each save, because ownership
/// can turn over after the first one. <see cref="Domain"/> is the only test for what the slot addresses — the
/// installed game's material, or the edit's own replacement output — exactly as the model law states.</summary>
/// <param name="MaterialName">The game material's own name where the slot addresses one, for the card's
/// group heading. Null on a replacement-output slot, which has no game material.</param>
/// <param name="ProjectRelativeFile">The file the binding names today, or null when the slot asks the game
/// for its own value.</param>
/// <param name="Binding">What this edit ASKS of the slot. The domain says what the slot addresses; this says
/// what answers it, and the two together are what decides which picture the card shows — an output keeping
/// the original map and an output carrying its own file are one domain and two pictures.</param>
/// <param name="Source">Where a <see cref="BindingKind.SourceSlot"/> answer takes its value from. Null on
/// every other binding.</param>
/// <param name="GameMaterialSlotIndex">The installed material position this output draws at, folded by the
/// page against the install's own drawable pattern. Null on a game-domain slot — its own
/// <paramref name="MaterialSlotIndex"/> IS the installed position — and on an output the page had no
/// install to fold against.</param>
/// <param name="SubmeshIndex">The edit's output submesh position. Every producer currently gives it the
/// material position too; it stays distinct for replacements whose later topology no longer has that match.</param>
/// <param name="ShaderProperty">The exact installed material property this texture slot addresses. Null
/// only for non-texture slots and property-less legacy known rows.</param>
/// <param name="HasDrawableCarrier">False only when current-install draw evidence proves that the material
/// position submits no geometry. The card remains visible but refuses authoring.</param>
public sealed record EditSlotRef(EditRef Edit, string SlotId, TargetInputKind Input,
    TargetSlotDomain Domain, int? MaterialSlotIndex, string? MaterialName, string? ProjectRelativeFile,
    BindingKind Binding = BindingKind.TargetGameValue, EditSlotSource? Source = null,
    int? GameMaterialSlotIndex = null, int? SubmeshIndex = null, string? ShaderProperty = null,
    bool HasDrawableCarrier = true);

/// <summary>A file an editor, picker or drop published and bound to the exact addressed slot.</summary>
public sealed record EditAssetResult(string ProjectRelativeFile, string Label, EditSlotRef? Target = null);

/// <summary>Whether an Open reached the external image editor. A pre-launch refusal or failure leaves no
/// transport behind; a launched editor is a completed Open even when it closes without saving. Tests and
/// other synchronous transports may also return the asset they published during that launch.</summary>
public enum EditPictureOpenOutcome
{
    NotLaunched,
    Launched,
}

public sealed record EditPictureOpenResult(EditPictureOpenOutcome Outcome,
    EditAssetResult? Published = null)
{
    public bool Launched => Outcome == EditPictureOpenOutcome.Launched;

    public static EditPictureOpenResult NotLaunched { get; } =
        new(EditPictureOpenOutcome.NotLaunched);

    public static EditPictureOpenResult LaunchedWithoutSave { get; } =
        new(EditPictureOpenOutcome.Launched);
}

/// <summary>What a toon-ramp pick came back with. The list has THREE outcomes, not two: a ramp file it
/// published, or the pinned row — keep whatever the game's own material binds here — which is an answer the
/// page records rather than a file it binds. A cancel is a null result, and is the one outcome that leaves
/// the project untouched.</summary>
/// <param name="Picked">The published ramp, or null when the pinned row was the choice.</param>
public sealed record EditRampPick(EditAssetResult? Picked)
{
    /// <summary>The pinned row was chosen. The card captions as vanilla either way, but the model holds it
    /// as a decision the modder made rather than a slot nobody has answered.</summary>
    public bool KeepsGameOwn => Picked is null;
}

/// <summary>One shading value the selected material's shader reads: its field name, the plain-language
/// label the dialog leads with, its shape, the value range seen across the game's own materials (a hint,
/// not a limit), and the material's own value — null where the material states none and the shader
/// default applies.</summary>
public sealed record EditShadingField(string Semantic, string Label,
    Remold.Core.Project.MaterialValueKind Kind, float ObservedMin, float ObservedMax,
    string? OriginalValue);

/// <summary>The shading values one material position supports, in offer order. Null from
/// <see cref="IEditPageShell.ReadShading"/> means the position has no supported values at all — not on
/// the character shader, or nothing the install can prove.</summary>
public sealed record EditShadingInfo(IReadOnlyList<EditShadingField> Fields);

/// <summary>One row of a shading copy: the field, and the two originals it would bridge.</summary>
public sealed record EditShadingCopyRow(string Semantic, string Label, string? CarrierValue,
    string SourceValue);

/// <summary>What a shading-source pick came back with: the exact source material position, the label a
/// confirm names it by, and the differing values the copy would set. An empty row list is a legal answer
/// — the two materials already agree on every supported value.</summary>
public sealed record EditShadingSource(TargetPart SourcePart, int SourceMaterialSlotIndex,
    string Label, IReadOnlyList<EditShadingCopyRow> Rows);

/// <summary>One decision from the shading-values dialog: set the field to <see cref="Value"/>, or null
/// to return it to the original. Only changed rows come back.</summary>
public sealed record EditShadingValueEdit(string Semantic, string? Value);

/// <summary>A committed shading-values answer. Empty edits are distinct from cancel; when every displayed
/// value is original, the page reports that settled no-effect state.</summary>
public sealed record EditShadingValuesResult(IReadOnlyList<EditShadingValueEdit> Edits,
    bool MatchesOriginal = false);

/// <summary>A shading command failed after it began. Null dialog results remain the separate, silent
/// cancellation answer; this exception carries only wording safe for the Edit page's status line.</summary>
internal sealed class EditShadingFailureException : Exception
{
    internal EditShadingFailureException(string message) : base(message) { }
}

/// <summary>A card's picture, or a card with none. <see cref="Dimensions"/> is what the card's size line
/// shows either way, so a row with nothing to measure says so rather than reading as a failed read.</summary>
/// <param name="MissingFile">The project-relative file this card's answer names, where the mod folder does
/// not hold it. Its own state on the card: a slot the modder answered with a file that is gone is not the
/// same card as one with no picture to show, and it is emphatically not a card the game's texture belongs
/// on.</param>
public sealed record EditMapPreview(Bitmap? Image, string Dimensions, string? MissingFile = null);

/// <summary>One edit's own geometry, rendered.</summary>
public sealed record EditMeshPreview(Bitmap Image, int VertexCount, int? OriginalVertexCount);

/// <summary>One subject's bone tree, for the row that stays at the bottom of each subject's branch.</summary>
public sealed record EditSkeletonOutline(int BoneCount, IReadOnlyList<SkeletonNodeVm> Bones);

/// <summary>Whether the install can be read right now. The tree shows the same two states the shipped pane
/// does rather than an empty panel: still reading, or unavailable with the reason on screen.</summary>
/// <param name="Unavailable">Why the install cannot be read, in words for the person reading them, or null
/// while it can.</param>
public sealed record EditInstallState(bool IsReading = false, string? Unavailable = null);

/// <summary>How far along the install is with ONE item — the question every answer about that item stands
/// on, since the same missing model means three different things to the person looking at the screen.</summary>
public enum EditSubjectRead
{
    /// <summary>No game install is mounted, so this item's texture reach cannot be measured at all.</summary>
    Unavailable,

    /// <summary>The install has said what it has to say about this item. What comes back beside this is the
    /// answer outright.</summary>
    Answered,

    /// <summary>The read has not landed yet. It lands on a worker after the page is already on screen, and
    /// again after every rescan, so this is an ordinary state rather than a rare one.</summary>
    Reading,

    /// <summary>The read FINISHED without this item and nothing retries until the game is read again — the
    /// roster does not carry it, or the files behind it could not be read. Every answer that would have come
    /// from its model is unavailable for good rather than for now.</summary>
    Unreadable,
}

/// <summary>The imperative plumbing the ② Edit page's verbs reuse. Everything that reads the game install,
/// runs an external tool, decodes a picture or moves the app to another step lives behind here; the page
/// itself owns the authored model, while mutating shell operations commit through that same session by the
/// exact edit and slot identities supplied here.
///
/// <para>That split is the point: external-tool plumbing is imperative, but it never owns a second project
/// model and never infers an authored address from a filename.</para></summary>
public interface IEditPageShell
{
    /// <summary>Re-anchor one part in the current install, in the exact form
    /// <see cref="AuthoredEditSession.EnsurePartSlots"/> consumes. Null when the install does not have the
    /// part at all; a route it cannot name an exact object for is the session's own refusal, by name.</summary>
    LegacyResolvedPart? ResolvePart(TargetPart target);

    /// <summary>The same answer, read OFF the UI thread. A first ask per install deobfuscates the part's
    /// bundles, which is seconds with the window frozen behind it if it runs where the redraw runs — and the
    /// redraw is what asks. The page memoizes the answer per part and consumes it synchronously once it has
    /// it, exactly as it does <see cref="MeshEditBlockAsync"/>.</summary>
    Task<LegacyResolvedPart?> ResolvePartAsync(TargetPart target);

    /// <summary>Every part the INSTALL says one subject has, in the install's own order. The authored model
    /// only knows the parts something has been authored against, so this is where a part the mod has never
    /// touched comes from — and a part row is the only place a first edit is minted, so without it a fresh
    /// mod could never make one.
    ///
    /// <para>A PEEK, like <see cref="PartToken"/>: a subject nothing has read yet answers empty, which leaves
    /// the subject row standing over whatever the project itself already holds.</para></summary>
    IReadOnlyList<TargetPart> SubjectParts(string subject, string outfit);

    /// <summary>One subject's skeleton, or null where the install cannot supply one. A null answer leaves the
    /// row out rather than showing an empty one.</summary>
    EditSkeletonOutline? ReadSkeleton(string subject, string outfit);

    /// <summary>Whether the install can be read at all right now. Read on every redraw, so the tree's global
    /// state is the install's own rather than a copy of it going stale.</summary>
    EditInstallState InstallState();

    /// <summary>One part's short name — <c>cloth1</c> — as against the renderer slot the model addresses it
    /// by. Empty where the install cannot name one, which leaves the renderer slot standing as the title.</summary>
    string PartToken(TargetPart part);

    /// <summary>What the game draws on one slot, for a card asking the game for its own value. Null where the
    /// install cannot name it: the project holds no name of its own for a game texture.</summary>
    string? GameTextureName(EditSlotRef slot);

    /// <summary>How many USES of the item draw the game texture behind one slot. One use is one material
    /// position of one part — the grain a picture is bound at — so a part drawing the same texture at two of
    /// its positions is two uses, not one. More than one means an authored picture there would repaint every
    /// one of them, which is the capability boundary the page refuses at.
    ///
    /// <para>A PEEK, like <see cref="PartToken"/> and <see cref="GameTextureName"/>: the answer comes from
    /// the subject model the shell already holds. NULL is NO COUNT — the install has not answered for this
    /// item, so how far a picture here would reach cannot be said, and the page refuses both gestures rather
    /// than treating an unread install as a private texture. <see cref="SubjectRead"/> says which of the two
    /// silences it is.</para>
    /// </summary>
    int? TextureUses(EditSlotRef slot);

    /// <summary>How far along the install is with the item behind one part. A PEEK like the rest: the answer
    /// is the shell's own read state, never a read.
    ///
    /// <para>It is asked beside <see cref="TextureUses"/> because a missing count is not one fact. An item
    /// still being read gets a wait; an item the read finished without gets the truth, which is that nothing
    /// will change until the game is read again — and a card that says "try again in a moment" forever is
    /// the lie this answer exists to stop.</para></summary>
    EditSubjectRead SubjectRead(TargetPart part);

    /// <summary>Render the geometry the GAME draws for one part — what a first edit would start from, and
    /// what a part with no edits shows.</summary>
    Task<EditMeshPreview?> LoadPartMeshPreviewAsync(TargetPart part);

    /// <summary>Decode one card's picture and measure it. A null image is the card's quiet no-preview
    /// state.</summary>
    Task<EditMapPreview?> LoadMapPreviewAsync(EditSlotRef slot);

    /// <summary>Load one selected inspector's cards as a batch. Production groups installed maps by bundle;
    /// the default keeps headless shells synchronous in behavior and requires no ambient-context test.</summary>
    async Task<IReadOnlyList<EditMapPreview?>> LoadMapPreviewsAsync(IReadOnlyList<EditSlotRef> slots) =>
        await Task.WhenAll(slots.Select(LoadMapPreviewAsync));

    /// <summary>Render the geometry THIS edit draws — its own replacement where it binds one, the game's mesh
    /// where it asks for the game's own value.</summary>
    Task<EditMeshPreview?> LoadEditMeshPreviewAsync(EditRef edit);

    /// <summary>Why one part's game mesh cannot be edited in Blender, in the page's own sentence, or null
    /// when it can. The read costs a bundle read, so it runs off the UI thread and the shell memoizes it
    /// per install; the page asks lazily, when a part is selected, and a verb awaits the answer rather
    /// than running past a read still in flight.</summary>
    Task<string?> MeshEditBlockAsync(TargetPart part);

    /// <summary>Open one part from the game's original mesh in Blender, with the outfit around it when
    /// <paramref name="withReferences"/>. No edit is created or addressed by this route.</summary>
    Task OpenPartInBlenderAsync(TargetPart part, bool withReferences, IProgress<string> status);

    /// <summary>Open one edit in Blender, with the outfit around it when
    /// <paramref name="withReferences"/>.</summary>
    Task OpenInBlenderAsync(EditRef edit, bool withReferences, IProgress<string> status);

    // ---- the subject's own verbs ----
    //
    // These act on everything under one subject rather than on any one edit, which is why they take a
    // subject rather than an EditRef.

    /// <summary>The friendly label for one subject — the roster's localized character and outfit names with
    /// the outfit kind, through the app's one naming home. Falls back to the internal tokens while the
    /// install is cold, so the tree never waits on it.</summary>
    string SubjectLabel(string subject, string outfit);

    /// <summary>Open every part of one subject from the game's originals in a single Blender session.</summary>
    Task OpenSubjectInBlenderAsync(string subject, string outfit, IProgress<string> status);

    /// <summary>Open every part of one subject from its active or first content edit where one exists,
    /// and from the game's original otherwise.</summary>
    Task OpenSubjectFirstEditInBlenderAsync(string subject, string outfit, IProgress<string> status);

    /// <summary>Reveal the subject's files in the OS file browser.</summary>
    void ShowSubjectFolder(string subject, string outfit);

    /// <summary>Drop the whole subject from the mod, after its own confirm. A cancel leaves the mod
    /// untouched.</summary>
    Task RemoveSubjectAsync(string subject, string outfit);

    /// <summary>Hand one card's picture to the image editor. A later save is published by that exact-slot
    /// transport and reaches the page through the session change event. The result distinguishes a refusal or
    /// pre-launch failure from an editor that launched and later closed without a save.</summary>
    /// <param name="confirmed">The page already asked the applicable shared-map question.</param>
    Task<EditPictureOpenResult> OpenPictureAsync(EditSlotRef slot, IProgress<string> status,
        bool confirmed = false, EditTextureSharingOffer? offered = null);

    /// <summary>Draw the UV guide for one card's picture.</summary>
    Task OpenUvGuideAsync(EditSlotRef slot, IProgress<string> status);

    /// <summary>Choose a <c>.png</c> from disk for one card, in the file dialog the mod's other picture
    /// surfaces open. Null when the modder cancelled. What the chosen file DOES is the drop route's
    /// business: this only names it.</summary>
    Task<string?> PickPictureAsync();

    /// <summary>Show the toon-ramp pick list. Resolves to what was chosen — a published ramp file, or the
    /// pinned keep-the-game's-own row — or null on a cancel.</summary>
    Task<EditRampPick?> PickRampAsync(EditSlotRef slot);

    /// <summary>Show the shading-values dialog for one material position. <paramref name="authored"/> is
    /// what the edit currently sets, by field; the edit identity resolves copied fields through their
    /// source slots. Resolves to the changed rows, or null on a cancel.</summary>
    Task<EditShadingValuesResult?> EditShadingValuesAsync(EditRef edit,
        int materialSlotIndex, string materialLabel, IReadOnlyDictionary<string, string> authored,
        bool addsFirstEdit);

    /// <summary>Show the shading-source pick list: the materials of every part the given subjects have,
    /// with what a copy onto the target position would change. Resolves to the pick, or null on a
    /// cancel.</summary>
    Task<EditShadingSource?> PickShadingSourceAsync(TargetPart part, int materialSlotIndex,
        string materialLabel, GameAssetRef? targetMaterial,
        IReadOnlyList<(string Subject, string Outfit)> subjects, IProgress<string> status);

    /// <summary>Take a <c>.png</c> dropped on one card: confirm, decode, publish. Resolves to the published
    /// project file, or null when the drop was declined or refused.</summary>
    /// <param name="confirmed">The page already asked the drop's Apply question.</param>
    Task<EditAssetResult?> AcceptDroppedPictureAsync(EditSlotRef slot, string path, IProgress<string> status,
        bool confirmed = false, EditTextureSharingOffer? offered = null);

    /// <summary>Ask a yes/no question. Declined resolves false.</summary>
    /// <param name="confirmLabel">What the button that goes through with it says — the verb itself, never
    /// "OK", so the answer to the dialog is readable without re-reading the title.</param>
    Task<bool> ConfirmAsync(string title, string body, string confirmLabel, bool dangerous = false);

    Task CopyTextAsync(string? text);

    /// <summary>Move to ③ Build, on the edit the page was showing where there is one.</summary>
    void GoToBuild(EditRef? edit);

    /// <summary>An accepted authored revision reached the page. Implementations persist it once and ignore
    /// any revision older than one they have already handled.</summary>
    void ProjectChanged(long revision);
}
