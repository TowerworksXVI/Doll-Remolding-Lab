using Remold.Core.Text;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Remold.Core;
using Remold.Core.Project;
using Remold.Core.Textures;

namespace Remold.App.ViewModels.EditPage;

/// <summary>Which of three things a map card is. They differ in one question — what a gesture on the card
/// does — and every enablement below is asked of this and of the slot's own binding.</summary>
public enum EditCardRole
{
    /// <summary>An edit's own card. It says what the edit asks of one slot and runs the edit's verbs.</summary>
    Edited,

    /// <summary>A part with no edits: the game's own map, with Open or Choose as its primary first-edit
    /// action and the UV guide available before painting. A picture drop can start the same edit as a
    /// secondary gesture.</summary>
    FirstEdit,

    /// <summary>An edit that replaces the part's mesh but recorded no maps of its own: the original part's
    /// maps stand in for what the build will draw. Nothing lands here — the build drops a picture bound to a
    /// game texture on a replacement — so the card takes no drop and offers no guide.</summary>
    StandIn,
}

/// <summary>How far an authored picture bound to one card's game texture would reach across the item. The
/// build rebinds a stock texture by its identity, so a picture bound at one use lands on every use of it —
/// which is why this is a card's own state rather than a detail of the drop.</summary>
public enum EditTextureSharing
{
    /// <summary>No game install is mounted, so the texture's reach cannot be measured.</summary>
    Unavailable,

    /// <summary>Exactly one use of the item draws it, so an edit here reaches exactly where it looks like it
    /// reaches. Also the answer on every card the boundary does not cover.</summary>
    Private,

    /// <summary>More than one use draws it. An edit here repaints all of them after consent.</summary>
    Shared,

    /// <summary>Nothing has read the item yet, so how far an edit would reach cannot be said. Authoring is
    /// refused until the measurement lands.</summary>
    Unknown,

    /// <summary>The read finished without this item, so how far an edit would reach can never be said until
    /// the game is read again. Refused like the two above, and refused in its OWN sentence: a wait is what
    /// the modder is owed on <see cref="Unknown"/> and the one thing that cannot help here.</summary>
    Unreadable,
}

/// <summary>The sharing answer a confirmation described: its classification and the position-grain count
/// shown to the modder. The publish route compares this offer with the live answer before granting consent.</summary>
public readonly record struct EditTextureSharingOffer(EditTextureSharing Kind, int? Uses);

/// <summary>One map card under the selected edit — the shipped card, addressed by the authored model instead
/// of by the workspace. The visual family is the workbench's: the same role line and ℹ legend, the same
/// shimmer → picture → quiet-no-preview thumbnail states behind a monotonic request id, the same ✎ corner
/// badge, the same <see cref="RampCardState"/> vocabulary on a ramp row, the same blanked and emissive-mask
/// lines, and the same Open / UV / Revert and Choose / Revert action rows.
///
/// <para>What is new is the address and the gates. A card names one exact <see cref="EditSlotRef"/> — one
/// edit, one slot — and every enablement below is asked of the slot's domain and its binding kind, which is
/// what the model law says decides these questions. The workbench card's own flags (materialized, edited,
/// authored path, blanked, donor submeshes) are the old store's vocabulary for the same screen; carrying them
/// here would give each of them a second meaning, so they are deliberately not carried.</para></summary>
public sealed partial class EditMapCardVm : ObservableObject
{
    /// <param name="gameTextureName">What the install draws here, on a slot that asks the game for its own
    /// value. The project holds no name for it, so it comes from the install or not at all.</param>
    /// <param name="rmoAlpha">The emissive-mask answer recorded for this submesh, on an RMO card.</param>
    /// <param name="role">Which of the three cards this is — see <see cref="EditCardRole"/>.</param>
    /// <param name="sharing">How far an edit to this card's game texture would reach across the item — and
    /// whether that is known at all.</param>
    public EditMapCardVm(EditSlotRef slot, BindingKind binding, string? boundFile,
        string? gameTextureName = null, RmoAlphaAnswer? rmoAlpha = null,
        EditCardRole role = EditCardRole.Edited,
        EditTextureSharing sharing = EditTextureSharing.Private,
        int? sharingUses = null,
        EditSubjectRead subjectRead = EditSubjectRead.Answered,
        string? boundLabel = null)
    {
        Slot = slot;
        Role = role;
        Sharing = sharing;
        SharingUses = sharingUses;
        SubjectRead = subjectRead;
        _binding = binding;
        _boundFile = boundFile;
        string? label = string.IsNullOrWhiteSpace(boundLabel) ? null : boundLabel.Trim();
        string storageName = boundFile is null ? "" : Path.GetFileName(boundFile);
        BoundLabel = label is null
            || string.Equals(label, storageName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(label, "image", StringComparison.OrdinalIgnoreCase)
                ? null : label;
        GameTextureName = gameTextureName ?? "";
        RmoAlpha = rmoAlpha;
        MapLabel = Label(slot.Input, slot.ShaderProperty);
        MapInfo = slot.Input switch
        {
            TargetInputKind.Rmo => Strings.MapRmoCardInfo,
            TargetInputKind.Ramp => TextureMap.RampInfo,
            _ => null,
        };
    }

    /// <summary>The one place this card acts on.</summary>
    public EditSlotRef Slot { get; }

    public string MapLabel { get; }

    /// <summary>What a packed map's channels hold, for the role line's tooltip. Null on a row whose role name
    /// already says everything — Avalonia shows no tooltip for a null tip.</summary>
    public string? MapInfo { get; }


    /// <summary>Which of the three cards this is.</summary>
    public EditCardRole Role { get; }

    /// <summary>This card shows the part's ORIGINAL map rather than anything the mod owns. A first-edit card
    /// can mint the edit its action needs; a stand-in remains display-only because its replacement draws
    /// maps of its own.</summary>
    public bool IsOriginal => Role is EditCardRole.FirstEdit or EditCardRole.StandIn;

    /// <summary>How far an edit to this card's game texture would reach across the item. Unmeasured states
    /// refuse authoring; a measured shared state asks for consent.</summary>
    public EditTextureSharing Sharing { get; }

    /// <summary>The measured number of material positions that draw the original map, where one was
    /// available. This is the same position-grain count the sharing classification uses.</summary>
    public int? SharingUses { get; }

    /// <summary>Whether the install has answered for this card's item. Unlike sharing, this remains relevant
    /// after the mod owns the picture because a UV guide still needs the item's mesh.</summary>
    public EditSubjectRead SubjectRead { get; }

    /// <summary>What a shared original map's consent question states, wherever it is asked. One sentence for
    /// one fact: an Open and a drop reach the same places, so they say so in the same words.</summary>
    public static string SharedConsequence(int uses) =>
        Strings.MapSharedConsequence(uses);

    public static string SharedConsentRequired(int uses) =>
        Strings.MapSharedConsentRequired(uses);

    public static string? ReadRefusalFor(EditSubjectRead read) => read switch
    {
        EditSubjectRead.Unavailable => GameFilesGate.Unavailable,
        EditSubjectRead.Reading => GameFilesGate.SubjectReading,
        EditSubjectRead.Unreadable => GameFilesGate.SubjectUnreadable,
        _ => null,
    };

    /// <summary>How far an edit to one slot's stock texture would reach, read from what the install says
    /// about the item and from its use count. The one place those answers are turned into the boundary's
    /// states: the page asks to build a card, and the publish route asks again at the bind, so the two can
    /// never disagree about what is shared. The slot itself carries the domain, the input and the binding,
    /// so neither caller can answer for a different place than the one it is acting on.
    ///
    /// <para>Only a slot standing on the game's own untouched value is covered. A slot already carrying the
    /// mod's own file has whatever reach it has; refusing it would strand work already done, and the
    /// boundary is drawn around CREATING that reach.</para>
    ///
    /// <para>A toon ramp is not covered either. A ramp is emitted at one draw by construction — the build
    /// anchors it on the part's own index buffer and material — so a ramp several parts share is still one
    /// part's shading when it ships, and refusing it would take the pick list away from nearly every ramp
    /// card for a reach that does not exist.</para>
    ///
    /// <para>The count is consulted only where the install ANSWERED. A missing count on an answered item is
    /// nothing this app can produce; it is read as unknown rather than as one use, since that is the reading
    /// that refuses.</para></summary>
    public static EditTextureSharing SharingFor(EditSlotRef slot, EditSubjectRead read, int? uses) =>
        slot.Input == TargetInputKind.Ramp
        || slot.Domain != TargetSlotDomain.Game
        || slot.Binding != BindingKind.TargetGameValue
            ? EditTextureSharing.Private
            : read switch
            {
                EditSubjectRead.Unavailable => EditTextureSharing.Unavailable,
                EditSubjectRead.Reading => EditTextureSharing.Unknown,
                EditSubjectRead.Unreadable => EditTextureSharing.Unreadable,
                _ => uses switch
                {
                    null => EditTextureSharing.Unknown,
                    > 1 => EditTextureSharing.Shared,
                    _ => EditTextureSharing.Private,
                },
            };

    /// <summary>The stock sentence for an unmeasured state, or null for a measured state. Shared is measured
    /// and proceeds through the gesture's consent question.</summary>
    public static string? RefusalFor(EditTextureSharing sharing) => sharing switch
    {
        EditTextureSharing.Unavailable => GameFilesGate.Unavailable,
        EditTextureSharing.Unknown => GameFilesGate.SubjectReading,
        EditTextureSharing.Unreadable => GameFilesGate.SubjectUnreadable,
        _ => null,
    };

    /// <summary>Why this card's game texture cannot be edited from here, or null when it can: the texture reach
    /// gates first, then the texture's own format.</summary>
    public string? SharingRefusal => Slot.HasDrawableCarrier
        ? RefusalFor(Sharing) ?? (Authorable ? null : Strings.MapFloatFormat)
        : Strings.MapNoDrawableCarrier;

    /// <summary>Whether the game texture behind this card can take an edit at all. A float-format map cannot:
    /// decoding it flattens it, and nothing could be encoded back. Read off the same header the size line is.
    /// True until the preview answers, so a card never refuses a gesture on a texture it has not read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharingRefusal))]
    [NotifyPropertyChangedFor(nameof(ShowsDropTarget))]
    [NotifyPropertyChangedFor(nameof(DropHint))]
    [NotifyPropertyChangedFor(nameof(CanBrowse))]
    [NotifyPropertyChangedFor(nameof(BrowseHint))]
    [NotifyPropertyChangedFor(nameof(CanOpen))]
    [NotifyPropertyChangedFor(nameof(OpenHint))]
    private bool _authorable = true;

    /// <summary>The one sentence a drop confirm adds when the picture's size differs from the map it
    /// replaces, or null when the sizes match or either is unknown.</summary>
    public static string? SizeNote((int Width, int Height)? dropped, (int Width, int Height)? current) =>
        dropped is { } d && current is { } c && (d.Width != c.Width || d.Height != c.Height)
            ? Strings.MapSizeNote(d.Width, d.Height, c.Width, c.Height)
            : null;

    /// <summary>The size a card's dimension line shows, as numbers; null while it is loading or unknown.</summary>
    public static (int Width, int Height)? ParseDimensions(string? dimensions)
    {
        if (dimensions is null) return null;
        var parts = dimensions.Split('×');
        return parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h)
            ? (w, h) : null;
    }

    /// <summary>A drop on this card adds the part's first edit, and the card shows that it takes one. False
    /// on every card where a drop does nothing: an edit's own cards say it with their buttons, a stand-in
    /// belongs to an edit that draws the replacement's maps, a toon ramp is picked rather than painted, and
    /// an unmeasured texture is accepted at the pointer so its drop can explain the gate in words.</summary>
    public bool ShowsDropTarget => Role == EditCardRole.FirstEdit && !IsRamp && SharingRefusal is null;

    /// <summary>What hovering the card itself says: why this map cannot be edited, or what a drop on a
    /// first-edit card does. Null everywhere else, where the buttons say it instead.
    ///
    /// <para>It names the one file type a drop takes. The refusal said it and nothing else did, so the
    /// only place the constraint appeared was after a drop had already been refused.</para></summary>
    public string? DropHint => SharingRefusal
        ?? (ShowsDropTarget ? Strings.MapDropHint : null);

    /// <summary>The picture rows show on an edit's cards and on the ones a first edit starts from, where
    /// the UV guide is the live button and the rest say what is missing. A stand-in has nothing to run:
    /// the edit it belongs to draws the replacement's own maps.</summary>
    public bool ShowsMapActions => !IsRamp && Role != EditCardRole.StandIn;

    public bool ShowsRampActions => IsRamp && Role != EditCardRole.StandIn;

    /// <summary>A Revert is drawn at all. Only an edit's own card has something to take back; a part's
    /// original cards would carry a button that can never do anything, so they show one button fewer instead.
    /// It returns with the edit the card's other verbs mint.</summary>
    public bool ShowsRevert => Role == EditCardRole.Edited;

    /// <summary>Browse hands the card the same file a drop does, so it is live exactly where a drop is taken:
    /// not on a toon ramp, which is picked rather than painted, not on a stand-in, whose edit draws the
    /// replacement's own maps, and not while the item's texture reach is unmeasured.</summary>
    public bool CanBrowse => !IsRamp && !IsBusy && SharingRefusal is null && Role != EditCardRole.StandIn;

    /// <summary>What Browse says, in the words its neighbour Open uses for the same two states.</summary>
    public string BrowseHint => SharingRefusal is { } refused ? refused
        : IsBusy ? Strings.BlenderBusy
        : Role == EditCardRole.FirstEdit ? Strings.MapBrowseFirstEditTip
        : Strings.MapBrowseTip;

    /// <summary>Why this card refuses a picture, in the words the card's own surface can back up.</summary>
    public string RampRefusal => Strings.MapRampNotAnImage;

    public bool IsRamp => Slot.Input == TargetInputKind.Ramp;

    /// <summary>The slot addresses an installed-game material rather than a replacement output.</summary>
    public bool IsGameSlot => Slot.Domain == TargetSlotDomain.Game;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditBadge))]
    [NotifyPropertyChangedFor(nameof(CanOpen))]
    [NotifyPropertyChangedFor(nameof(OpenHint))]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    [NotifyPropertyChangedFor(nameof(CanRevertRamp))]
    [NotifyPropertyChangedFor(nameof(RevertRampHint))]
    [NotifyPropertyChangedFor(nameof(RampState))]
    [NotifyPropertyChangedFor(nameof(IsBlanked))]
    [NotifyPropertyChangedFor(nameof(ShowsGameTextureName))]
    [NotifyPropertyChangedFor(nameof(OriginNote))]
    [NotifyPropertyChangedFor(nameof(HasOrigin))]
    [NotifyPropertyChangedFor(nameof(ShowsOwnedOrigin))]
    [NotifyPropertyChangedFor(nameof(ShowsQuietOrigin))]
    private BindingKind _binding;

    /// <summary>The file the card is standing on. A slot asking the game for its own value names none — the
    /// authored model records the place, and which texture the game binds there is the game's answer, not
    /// something the project holds a name for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextureName))]
    [NotifyPropertyChangedFor(nameof(BoundFileName))]
    [NotifyPropertyChangedFor(nameof(HasTextureName))]
    [NotifyPropertyChangedFor(nameof(FilterText))]
    [NotifyPropertyChangedFor(nameof(RampState))]
    private string? _boundFile;

    /// <summary>The project's friendly name for the bound file. Null on an original-game value and on a
    /// malformed or older project asset whose label is blank.</summary>
    public string? BoundLabel { get; }

    /// <summary>The friendly project-asset name the card shows. The storage basename is only a compatibility
    /// fallback for an asset with no usable label; <see cref="BoundFile"/> remains the preview, ramp and
    /// storage address.</summary>
    public string BoundFileName => BoundFile is null ? "" : Path.GetFileName(BoundFile);

    public string TextureName => BoundLabel ?? BoundFileName;

    public bool HasTextureName => TextureName.Length > 0;
    public bool HasMapInfo => MapInfo is not null;

    /// <summary>What the game draws here, where the install could name it. Which cards show it is
    /// <see cref="ShowsGameTextureName"/>'s answer.</summary>
    public string GameTextureName { get; }

    /// <summary>A source answer naming a slot NO edit owns. The model writes the recorded keep-the-original
    /// answer exactly that way, and it is the one source answer whose picture the mod does not own: what
    /// draws there is the game's own value, taken from a place this slot names rather than from its own. A
    /// source answer naming another EDIT's slot takes a file the mod made.</summary>
    private bool TakesTheGamesOwnValue => Binding == BindingKind.SourceSlot
        && Slot.Source?.EditDefinitionId is not { Length: > 0 };

    /// <summary>The install's own name for what draws here. Shown on the three answers that draw a game
    /// texture — asking the game for its own value, a replacement output keeping the carrier's map, and a
    /// recorded keep-the-original — and on no other, where the mod's own file is the card's answer and this
    /// would be a second name for it.</summary>
    public bool ShowsGameTextureName =>
        (Binding is BindingKind.TargetGameValue or BindingKind.InheritedLiveCarrier
            || TakesTheGamesOwnValue)
        && GameTextureName.Length > 0;

    /// <summary>The mod owns what this slot binds. An output keeping the carrier's map owns nothing — the
    /// picture on it is the original — and neither does a recorded keep-the-original, so neither carries an
    /// edited marker.</summary>
    public bool HasEditBadge =>
        Binding is not (BindingKind.TargetGameValue or BindingKind.InheritedLiveCarrier)
        && !TakesTheGamesOwnValue;

    /// <summary>Where this card's picture comes from. A stand-in says what it is standing in for; a
    /// replacement's own slot says the replacement, the original, or another of the edit's own maps,
    /// whichever the edit asks of it. Null on a game slot, whose texture name says it, and on a toon ramp,
    /// whose own state line does.</summary>
    public string? OriginNote => !Slot.HasDrawableCarrier ? Strings.MapNoDrawableCarrier
        : Role == EditCardRole.StandIn ? Strings.MapStandInOrigin
        : IsRamp || IsGameSlot ? null
        : Binding == BindingKind.InheritedLiveCarrier || TakesTheGamesOwnValue ? Strings.MapInheritedOrigin
        : Binding == BindingKind.SourceSlot ? Strings.MapSharedWithAnotherMap
        : Strings.MapReplacementOrigin;

    public bool HasOrigin => OriginNote is not null;

    /// <summary>The origin line marks something the mod owns, so it takes the ownership accent. The two
    /// that name the original are subtext beside the name they qualify.</summary>
    public bool ShowsQuietOrigin => !Slot.HasDrawableCarrier || Role == EditCardRole.StandIn
        || (!IsRamp && !IsGameSlot && (Binding == BindingKind.InheritedLiveCarrier || TakesTheGamesOwnValue));

    public bool ShowsOwnedOrigin => HasOrigin && !ShowsQuietOrigin;

    /// <summary>Nothing is bound here and nothing of the game's draws either: the build ships its own flat
    /// map. It names no file, so the line is what speaks for the card.</summary>
    public bool IsBlanked => Binding == BindingKind.Neutral;

    /// <summary>The emissive-mask answer this submesh's RMO was recorded with, or null where none is. Read
    /// out rather than asked here: the question belongs to the round trip that produced the map.</summary>
    public RmoAlphaAnswer? RmoAlpha { get; }

    /// <summary>What the recorded answer says, WITHOUT the ✎ it is shown beside. The marker is an element
    /// of its own in the accent colour everywhere else on this page — the row's roll-up, the card's corner
    /// badge, the ramp's own state line — and baked into a grey string it was the one ✎ that read as
    /// punctuation.</summary>
    public string? RmoAlphaNote => RmoAlpha switch
    {
        RmoAlphaAnswer.ShipAsAuthored => Strings.MapRmoAlphaKept,
        RmoAlphaAnswer.Rebuild => Strings.MapRmoAlphaRebuilt,
        _ => null,
    };

    /// <summary>The line's own hover, and the one place the rebuilt answer is stated in full. A picture
    /// plugged in from another material arrives here as this submesh's own map, and the mask that ships with
    /// it is this submesh's — never the one belonging to the material the picture came from. Nothing else on
    /// screen says so.</summary>
    public string? RmoAlphaTip => RmoAlpha switch
    {
        RmoAlphaAnswer.ShipAsAuthored => Strings.MapRmoAlphaKeptTip,
        RmoAlphaAnswer.Rebuild =>
            Strings.MapRmoAlphaRebuiltTip,
        _ => null,
    };

    public bool HasRmoAlphaNote => RmoAlphaNote is not null;

    /// <summary>What a ramp row's state line says, in the shipped card's own vocabulary. The model holds
    /// three answers and each is its own state: a bound asset is a pick — the schema records a ramp's
    /// lineage, not who authored the record, so a conversion's carry and a modder's pick are one state by
    /// design; a source-slot answer naming the game's own ramp is the recorded keep-the-game's, which
    /// captions exactly as vanilla because the modder chose that state; anything else is untouched.</summary>
    public RampCardState RampState => !IsRamp ? RampCardState.Vanilla
        : Binding == BindingKind.ProjectAsset ? RampCardState.Picked(BoundFile)
        : Binding == BindingKind.SourceSlot ? RampCardState.VanillaOptedOut
        : RampCardState.Vanilla;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpen))]
    [NotifyPropertyChangedFor(nameof(CanOpenUvGuide))]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    [NotifyPropertyChangedFor(nameof(CanChooseRamp))]
    [NotifyPropertyChangedFor(nameof(CanRevertRamp))]
    [NotifyPropertyChangedFor(nameof(RevertRampHint))]
    [NotifyPropertyChangedFor(nameof(OpenHint))]
    private bool _isBusy;

    public bool CanOpen => !IsRamp && !IsBlanked && !IsBusy && SharingRefusal is null
        && Role != EditCardRole.StandIn;

    public string OpenButtonLabel => Strings.MapOpen;

    public string OpenHint => SharingRefusal is { } refused ? refused
        : IsBlanked ? Strings.MapBlankedNotEditable
        : IsBusy ? Strings.BlenderBusy
        : Role == EditCardRole.FirstEdit ? Strings.MapOpenFirstEditTip
        : Strings.MapOpenTip;

    /// <summary>The guide draws the mesh that samples this map — the edit's own where the part carries a
    /// mesh edit, the game's otherwise — so it is live on game slots AND a replacement's own slots alike.
    /// Both routes need the item's read to have landed. A stand-in stays refused: its map dresses no draw, so
    /// a guide under paint meant for the replacement misleads. A ramp has no layout to draw.</summary>
    public bool CanOpenUvGuide =>
        !IsRamp && !IsBusy && Role != EditCardRole.StandIn && Slot.HasDrawableCarrier
        && SubjectRead == EditSubjectRead.Answered;

    /// <summary>The button's own label: the effect overlay samples the mesh's second UV set, so its guide
    /// is that layout and the button says which. Every other map samples the first set, which stays the
    /// unnumbered "UV".</summary>
    public string UvButtonLabel => UvGuide.TexCoordIndex(Slot.Input) is > 0 and { } texCoord
        ? $"UV{texCoord}" : "UV";

    public string UvHint => !Slot.HasDrawableCarrier ? Strings.MapNoDrawableCarrier
        : ReadRefusalFor(SubjectRead) is { } refused ? refused
        : Role == EditCardRole.StandIn ? Strings.MapNoUvOnStandIn
        : UvGuide.TexCoordIndex(Slot.Input) is > 0 and { } texCoord
        ? Strings.MapUvSecondSetTip(texCoord)
        : Strings.MapUvTip;

    /// <summary>There is something to take back: the slot addresses the game and this edit asks it for
    /// something other than the game's own value.</summary>
    public bool CanRevert => !IsRamp && IsGameSlot && Binding != BindingKind.TargetGameValue
        && !IsBusy && Role == EditCardRole.Edited;

    /// <summary>The Revert tooltip, ordered the way the verb refuses: what it can never undo first, a wait
    /// after it, so a line promising "try again" is never shown for a click that will never work.</summary>
    public string RevertHint => !IsGameSlot ? Strings.MapBelongsToReplacement
        : Binding == BindingKind.TargetGameValue ? Strings.NothingToRevert
        : IsBusy ? Strings.BlenderBusy
        : Strings.MapRevertTip;

    public bool CanChooseRamp => IsRamp && !IsBusy && SharingRefusal is null
        && Role != EditCardRole.StandIn;

    public string ChooseRampButtonLabel => Strings.RampChoose;

    public string ChooseRampHint => SharingRefusal is { } refused ? refused
        : IsBusy ? Strings.BlenderBusy
        : Role == EditCardRole.FirstEdit
            ? Strings.RampChooseFirstEditTip
        : Strings.RampChooseTip;

    /// <summary>Revert takes back a record: on a game slot anything past the game's own value, and on the
    /// replacement's own slot a pick, a carried ramp or the recorded keep-the-game's — those go back to
    /// unanswered, and the part shades with its carrier's ramp again. An untouched slot on either domain has
    /// nothing to take back.</summary>
    public bool CanRevertRamp => IsRamp && !IsBusy && Role == EditCardRole.Edited
        && (IsGameSlot ? Binding != BindingKind.TargetGameValue : RampState.HasRecord);

    public string RevertRampHint =>
        (IsGameSlot ? Binding == BindingKind.TargetGameValue : !RampState.HasRecord)
            ? Strings.NothingToRevert
        : IsBusy ? Strings.BlenderBusy
        : IsGameSlot ? Strings.RampRevertTip
        : Strings.RampTakeBackChoice;

    // ---- demand-driven async thumbnail: the workbench card's three mutually-exclusive states, behind a
    // monotonic request id so an out-of-order completion is rejected rather than landing a stale picture.
    //
    // The bitmap is BORROWED, as the row's mesh render is: the page holds pictures by which slot and which
    // file they are of, hands them back across a redraw, and disposes what the new tree did not take.

    /// <summary>What the page files this card's picture under. Empty until the page sets it.</summary>
    public string PreviewKey { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumb))]
    private Bitmap? _thumbnail;

    [ObservableProperty] private bool _isThumbLoading = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewCause))]
    private bool _isThumbFailed;

    /// <summary>The read itself failed, rather than there being nothing to read. Only that carries the cause
    /// line, since a retry cannot make an unreadable picture readable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewCause))]
    private bool _thumbThrew;

    public bool HasPreviewCause => IsThumbFailed && ThumbThrew;

    /// <summary>The file this card's answer names, where the mod folder does not hold it. The third thing an
    /// empty tile can mean, and the only one with a file to name: a slot with no picture behind it, a read
    /// that failed, and an answer whose file is gone are three different cards.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingFile))]
    [NotifyPropertyChangedFor(nameof(MissingNote))]
    [NotifyPropertyChangedFor(nameof(ThumbNote))]
    private string? _missingFile;

    public bool HasMissingFile => MissingFile is not null;

    /// <summary>What the empty tile itself says. A missing file is not "no preview": the modder answered this
    /// slot, and what is wrong is the file rather than the picture.</summary>
    public string ThumbNote => HasMissingFile ? Strings.MapMissingTile : Strings.MapNoPreview;

    /// <summary>The cause and the way out under a missing file, in the words the geometry's own missing-file
    /// line uses. Null on every other card.</summary>
    public string? MissingNote => MissingFile is null ? null : MapFileMissing(MissingFile);

    /// <summary>What a card says when the file its answer names is gone from the mod folder. Names the file,
    /// because putting it back is the whole remedy — the card's own buttons say the rest, and which of them
    /// are there depends on what the card is.</summary>
    internal static string MapFileMissing(string file) =>
        Strings.MapFileMissing(file);

    /// <summary>The lazily-read "W×H" — "…" while loading.</summary>
    [ObservableProperty] private string _dimensions = "…";

    public bool HasThumb => Thumbnail is not null;

    private int _request;

    /// <summary>The page took this card's picture away under it — the twin of a row's forgotten render, and
    /// carried for the same reason: the request ids have to keep climbing, so they cannot say it.</summary>
    private bool _forgotten;

    /// <summary>Begin a load only if this card still needs one: never loaded, the last attempt failed, or the
    /// picture it had was taken away. Null when a picture is present or a load is already in flight.</summary>
    public int? BeginThumbRequestIfNeeded()
    {
        if (HasThumb) return null;
        if (_request != 0 && !IsThumbFailed && !_forgotten) return null;
        IsThumbLoading = true;
        IsThumbFailed = false;
        ThumbThrew = false;
        MissingFile = null;
        _forgotten = false;
        return ++_request;
    }

    public bool IsCurrentThumbRequest(int request) => request == _request;

    public void SetThumb(Bitmap? image, string dimensions, bool authorable = true)
    {
        _forgotten = false;
        Thumbnail = image;
        Dimensions = dimensions;
        Authorable = authorable;
        IsThumbLoading = false;
        IsThumbFailed = false;
        ThumbThrew = false;
        MissingFile = null;
    }

    /// <summary>Settle into the quiet no-preview tile. <paramref name="dimensions"/> is what the size line
    /// shows: a row with nothing behind it names an em dash rather than reading as a failed read.</summary>
    public void MarkThumbFailed(string dimensions = NoDimensions, bool threw = false)
    {
        Thumbnail = null;
        Dimensions = dimensions;
        IsThumbLoading = false;
        IsThumbFailed = true;
        ThumbThrew = threw;
        MissingFile = null;
    }

    /// <summary>Settle into the tile for an answered slot whose file is gone. The empty tile is the same one,
    /// and what separates it from the quiet state is the file it names and the way back it offers.</summary>
    public void MarkThumbMissing(string file)
    {
        MarkThumbFailed();
        MissingFile = file;
    }

    /// <summary>What the size line says where there is no file to measure.</summary>
    public const string NoDimensions = "—";

    /// <summary>Let the picture go when a rebuild drops this card, and re-arm loading. Bumps the request id so
    /// an in-flight producer's completion is rejected rather than resurrecting the tile. Nothing is disposed:
    /// the page owns the picture and drops what the new tree did not take.</summary>
    public void ReleaseThumb()
    {
        _request++;
        _forgotten = true;
        Thumbnail = null;
        IsThumbLoading = true;
        IsThumbFailed = false;
        ThumbThrew = false;
        MissingFile = null;
    }

    /// <summary>What the filter matches on this card — including the game's own texture where the install
    /// named it, so a stock map is findable by the name the game gives it.</summary>
    public string FilterText => $"{MapLabel} {TextureName} {BoundFileName} {GameTextureName}";

    internal static string Label(TargetInputKind input, string? shaderProperty = null) =>
        TextureMap.DisplaySlotLabel(input, shaderProperty);

    /// <summary>The label as it reads mid-sentence: known-safe words are lowercased, while a label whose
    /// first word is an all-caps name such as RMO or SMO keeps its casing. English only: in any other
    /// language a label's case is the translation's, and it is shown as written.</summary>
    internal static string LabelInSentence(string label)
    {
        if (Catalog.CurrentTag != Catalog.English) return label;
        string first = label.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Any(char.IsLetter)
            && first.All(character => !char.IsLetter(character) || char.IsUpper(character))
            ? label : label.ToLowerInvariant();
    }

    /// <summary>How every sentence about one card names the PLACE it acts on: the map, and the material it
    /// sits under. The one home for it, because the question and its answer have to name the same thing —
    /// a four-material part raises four identical dialogs without the material, and "the base colour" is
    /// then a promise about a place the modder cannot pick out of the four on screen.
    ///
    /// <para>The material is named the way the ramp picker's title and both shading dialogs name one: by
    /// the game material's own name. A slot with no game material behind it — a replacement's own output —
    /// names the map alone, which is all there is to say about it.</para></summary>
    internal static string MapInSentence(string label, string? materialName) =>
        string.IsNullOrEmpty(materialName) ? LabelInSentence(label)
            : Strings.MapOnMaterial(LabelInSentence(label), materialName);

    /// <summary>The same, for a caller holding the slot rather than the two parts.</summary>
    internal static string MapInSentence(EditSlotRef slot) =>
        MapInSentence(Label(slot.Input), slot.MaterialName);

    /// <summary>A confirmation's opening subject. Shared Open always addresses a game material; when the
    /// install supplied no name, use the same positional material label the card group shows.</summary>
    internal static string MapOnMaterial(EditSlotRef slot, string? label = null, bool sentenceStart = true)
    {
        string material = string.IsNullOrEmpty(slot.MaterialName)
            ? Strings.MaterialByPosition(slot.MaterialSlotIndex ?? slot.GameMaterialSlotIndex ?? slot.SubmeshIndex ?? 0)
            : slot.MaterialName;
        string map = label ?? Label(slot.Input);
        return Strings.MapOnMaterial(sentenceStart ? map : LabelInSentence(map), material);
    }
}

/// <summary>One donor submesh's cards inside a material group. Its label appears only when several
/// donor submeshes fold onto the same material position.</summary>
public sealed class EditMapSetVm
{
    public EditMapSetVm(string label, IReadOnlyList<EditMapCardVm> cards)
    {
        Label = label;
        Cards = new ObservableCollection<EditMapCardVm>(cards);
    }

    public string Label { get; }
    public bool HasLabel => Label.Length > 0;
    public ObservableCollection<EditMapCardVm> Cards { get; }
}

/// <summary>One installed material position under the selected edit. Its sets are backed by stock
/// slots for a texture-only edit or by folded edit-output slots for a replacement.</summary>
public sealed class EditMapGroupVm
{
    /// <param name="note">Why this group has no cards, where it has none. A material position whose maps
    /// could not be read keeps its heading and says so, rather than leaving the material out of a list the
    /// modder counts positions in.</param>
    public EditMapGroupVm(string title, IReadOnlyList<EditMapSetVm> sets,
        EditShadingRowVm? shading = null, string? note = null)
    {
        Title = title;
        Sets = new ObservableCollection<EditMapSetVm>(sets);
        Cards = new ObservableCollection<EditMapCardVm>(sets.SelectMany(set => set.Cards));
        Shading = shading;
        Note = note;
    }

    public string Title { get; }
    public ObservableCollection<EditMapSetVm> Sets { get; }
    public ObservableCollection<EditMapCardVm> Cards { get; }

    public string? Note { get; }

    public bool HasNote => Note is not null;

    /// <summary>The material's shading values row, or null where the position supports none — the row
    /// then does not show at all.</summary>
    public EditShadingRowVm? Shading { get; }

    public bool HasShading => Shading is not null;

    /// <summary>The material name is the one thing on this heading worth copying out.</summary>
    public string CopyText => Title;
}

/// <summary>The shading controls belonging to one material group. Writes go through the page's session.</summary>
public sealed partial class EditShadingRowVm : ObservableObject
{
    public EditShadingRowVm()
    {
        Effects.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasEffects));
            RefreshEffectAvailability();
        };
    }

    public required EditRef Edit { get; init; }
    public required TargetPart Part { get; init; }
    public required int MaterialSlotIndex { get; init; }
    public required string MaterialLabel { get; init; }

    /// <summary>The exact installed material when the row was built from already-loaded data. Null keeps the
    /// copy route's target resolve deferred until after a source pick.</summary>
    public GameAssetRef? Material { get; init; }

    /// <summary>A bare-part row has no edit identity yet. Its committed dialog result mints that identity;
    /// a cancel or no-effect result leaves this descriptor at part grain.</summary>
    public bool IsFirstEdit => Edit.EditDefinitionId.Length == 0;

    /// <summary>What the edit currently sets, by field: the typed value, or "" for a value copied from
    /// another material (its number is the source's, read at build time).</summary>
    public required IReadOnlyDictionary<string, string> AuthoredValues { get; init; }

    /// <summary>The bound slots behind <see cref="AuthoredValues"/>, for the revert.</summary>
    public required IReadOnlyList<string> AuthoredSlotIds { get; init; }

    public IReadOnlyList<string> DisabledEffectIds { get; init; } = Array.Empty<string>();

    public ObservableCollection<EditShadingEffectRowVm> Effects { get; } = new();

    public bool HasEffects => Effects.Count > 0;

    public IReadOnlyList<MaterialEffectOperation> EffectOperations { get; set; } =
        Array.Empty<MaterialEffectOperation>();

    /// <summary>The material this position's shading was last copied from, while anything copied
    /// remains. Provenance for the card; the numbers are the edit's own.</summary>
    public string? CopiedFrom { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEffectsNote))]
    private string _effectsNote = "";

    public bool HasEffectsNote => EffectsNote.Length > 0;

    public void SetEffects(IEnumerable<EditShadingEffect> effects)
    {
        Effects.Clear();
        foreach (var effect in effects)
            Effects.Add(new EditShadingEffectRowVm(this, effect));
        RefreshEffectAvailability();
    }

    internal void RefreshEffectAvailability()
    {
        foreach (var effect in Effects) effect.RefreshAvailability();
        OnPropertyChanged(nameof(CanDisableAllEffects));
        OnPropertyChanged(nameof(DisableAllEffectsHint));
    }

    public bool IsEdited => AuthoredSlotIds.Count > 0 || DisabledEffectIds.Count > 0;

    public string Summary => string.Join(" · ", new[]
    {
        AuthoredSlotIds.Count == 0 ? "" : Catalog.Plural(AuthoredSlotIds.Count,
            Strings.ShadingValuesSet_One(AuthoredSlotIds.Count), Strings.ShadingValuesSet_Other(AuthoredSlotIds.Count)),
        DisabledEffectIds.Count == 0 ? "" : Catalog.Plural(DisabledEffectIds.Count,
            Strings.ShadingEffectsDisabled_One(DisabledEffectIds.Count),
            Strings.ShadingEffectsDisabled_Other(DisabledEffectIds.Count)),
        CopiedFrom is { Length: > 0 } ? Strings.ShadingCopiedFrom(CopiedFrom) : "",
    }.Where(value => value.Length > 0));

    public bool HasSummary => IsEdited;

    /// <summary>A verb on this row's edit is running. Pushed on by the page off the same gate every other
    /// verb of this page is keyed by, for the same reason: these three write through the same edit, and a
    /// click that raced one of them would land a dialog's answer on a model the other has moved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(CopyFromMaterialHint))]
    [NotifyPropertyChangedFor(nameof(EditValuesHint))]
    [NotifyPropertyChangedFor(nameof(RevertHint))]
    [NotifyPropertyChangedFor(nameof(CanDisableAllEffects))]
    [NotifyPropertyChangedFor(nameof(DisableAllEffectsHint))]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => RefreshEffectAvailability();

    /// <summary>A Revert is drawn at all — the map cards' rule on this row: only a row that belongs to an
    /// edit has anything to take back, and a bare part's row hides the verb until its first edit mints.</summary>
    public bool ShowsRevert => !IsFirstEdit;

    public bool CanRevert => IsEdited && !IsBusy;

    public bool CanDisableAllEffects => !IsBusy && Effects.Any(effect => effect.IsActive);

    // Why the button is off when it is, else what the verb does — the page's rule, and the reason the
    // Revert below leads with what it can never do rather than with what it would do.

    public string CopyFromMaterialHint => IsBusy ? Strings.BlenderBusy
        : Strings.ShadingCopyFromTip;

    public string EditValuesHint => IsBusy ? Strings.BlenderBusy
        : Strings.ShadingAdvancedTip;

    public string DisableAllEffectsHint => IsBusy ? Strings.BlenderBusy
        : !HasEffects ? Strings.ShadingNoRemovableEffects
        : !Effects.Any(effect => effect.IsActive) ? Strings.ShadingAllDisabledTip
        : Strings.ShadingDisableAllTip;

    /// <summary>Why it is off first, in the map card Revert's own words for the same state.</summary>
    public string RevertHint => !IsEdited ? Strings.NothingToRevert
        : IsBusy ? Strings.BlenderBusy
        : Strings.ShadingRevertTip;
}

/// <summary>The user's enabled choice for one effect. Parent state controls availability without
/// changing that choice.</summary>
public sealed partial class EditShadingEffectRowVm : ObservableObject
{
    public EditShadingEffectRowVm(EditShadingRowVm owner, EditShadingEffect effect)
    {
        Owner = owner;
        Id = effect.Id;
        Label = effect.Label;
        ParentId = effect.ParentId;
        HasEditedValues = effect.IsEdited;
        OriginalIsEnabled = effect.IsEnabled;
        _isEnabled = effect.IsEnabled;
    }

    public EditShadingRowVm Owner { get; }
    public string Id { get; }
    public string Label { get; }
    public string? ParentId { get; }
    public bool HasEditedValues { get; }
    public bool OriginalIsEnabled { get; }
    public bool IsChild => ParentId is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEdited))]
    private bool _isEnabled;

    public bool IsEdited => IsActive && HasEditedValues;

    public bool IsActive => IsEnabled && ParentIsEnabled;

    private bool ParentIsEnabled => ParentId is null ||
        Owner.Effects.Any(effect => effect.Id == ParentId && effect.IsActive);

    public bool IsAvailable => !Owner.IsBusy && ParentIsEnabled;

    public void ResetEnabled() => IsEnabled = OriginalIsEnabled;

    partial void OnIsEnabledChanged(bool value) => Owner.RefreshEffectAvailability();

    internal void RefreshAvailability()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsEdited));
    }
}
