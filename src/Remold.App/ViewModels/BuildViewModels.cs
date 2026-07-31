using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remold.Core.Project;
using Remold.Core.Textures;

namespace Remold.App.ViewModels;

/// <summary>
/// The user-facing words for a derived verb, and the glyphed forms the change rows read. ONE home: the row
/// and the key-collision tip that names that row have to speak the same vocabulary, or a tip names a change
/// the modder can't find on the list.
/// </summary>
public static class BuildVerbWords
{
    public const string ReplaceWord = "new mesh";
    public const string RetextureWord = "new textures";
    public const string HideWord = "hidden";

    /// <summary>The change row's own labels: the glyph carries the state, the words are the detail.</summary>
    public const string ReplaceRow = "✎ " + ReplaceWord;
    public const string RetextureRow = "✎ " + RetextureWord;
    public const string HideRow = "∅ " + HideWord;

    /// <summary>A verb's words, falling back to the raw verb for anything the vocabulary doesn't cover.</summary>
    public static string Of(string verb) => verb switch
    {
        EditVerbs.Replace => ReplaceWord,
        EditVerbs.Retexture => RetextureWord,
        EditVerbs.Hide => HideWord,
        _ => verb,
    };
}

/// <summary>One subject's block of the change list, in derivation order. Built WHOLE off the UI thread, so
/// a row is never half-populated on screen.</summary>
public sealed partial class BuildGroupVm : ObservableObject
{
    public required string Character { get; init; }
    /// <summary>The grouping identity, never shown.</summary>
    public required string RawCharacter { get; init; }
    /// <summary>The grouping identity, and the label's fallback.</summary>
    public required string Outfit { get; init; }
    /// <summary>The outfit as the Edit pane labels it, or the raw stem.</summary>
    public required string OutfitLabel { get; init; }
    public List<BuildRowVm> Rows { get; } = new();
    /// <summary>The group counts what was AUTHORED; the footer counts what ships.</summary>
    public string ChangesLabel => $"{Rows.Count} change{(Rows.Count == 1 ? "" : "s")}";
    /// <summary>How many are unticked, so the header count can't disagree with the rows under it.</summary>
    public string LeftOutLabel
    {
        get { int n = Rows.Count(r => r.IsExcluded); return n > 0 ? $"· {n} left out" : ""; }
    }

    /// <summary>Re-raise the header counts after a tick.</summary>
    public void RefreshCounts() => OnPropertyChanged(nameof(LeftOutLabel));

    /// <summary>Tick every row of this group. Each row goes through its OWN
    /// <see cref="BuildRowVm.IsIncluded"/> setter, so the bulk gesture persists through exactly the route a
    /// single tick does; a row already in the wanted state is left alone and writes nothing.</summary>
    [RelayCommand]
    private void IncludeAll() => SetAll(true);

    /// <summary>Untick every row of this group. The rows stay listed, dimmed.</summary>
    [RelayCommand]
    private void ExcludeAll() => SetAll(false);

    private void SetAll(bool included)
    {
        foreach (var r in Rows)
            if (r.IsIncluded != included) r.IsIncluded = included;
    }
}

/// <summary>One derived edit as a change-list row. Identity is the <see cref="MeshEdit"/> triple plus its
/// verb, so a tick maps straight onto <see cref="ModProject.SetBuildExcluded"/>.</summary>
public sealed partial class BuildRowVm : ObservableObject
{
    public BuildRowVm(MeshEdit edit, string partLabel, bool included, string? toggleKey = null,
        bool hideWhenOff = false, bool startsOff = false)
    {
        Character = edit.Character;
        Outfit = edit.Outfit;
        Mesh = edit.Mesh;
        Verb = edit.Verb;
        PartLabel = partLabel;
        _isIncluded = included;
        _toggleKey = ModKeys.Normalize(toggleKey);
        _hideWhenOff = hideWhenOff;
        _startsOff = startsOff;
        // A retexture is authored on a map card, so its Edit hop asks for the material bound to the first
        // submesh it touches. Every other verb is authored on the part itself.
        EditSubmesh = Verb == EditVerbs.Retexture
            ? edit.Textures?.OrderBy(t => t.Submesh).FirstOrDefault()?.Submesh
            : null;
        Chips = Verb switch
        {
            EditVerbs.Replace => new[] { AlbedoChip(edit), NormalChip(edit), RmoChip(edit) },
            EditVerbs.Retexture => new[] { AlbedoChip(edit), NormalChip(edit), RmoChip(edit) },
            _ => Array.Empty<BuildChipVm>(),
        };
    }

    // Chip labels come from the same vocabulary the Edit pane's map cards read, so a chip and the card it
    // sends the modder to name one slot once.
    private static BuildChipVm AlbedoChip(MeshEdit e) =>
        new(TextureMap.BaseColorLabel, e.Textures?.Any(t => t.Albedo is not null) == true,
            Blanked: Blanks(e, f => f.Albedo));
    private static BuildChipVm NormalChip(MeshEdit e) =>
        new(TextureMap.NormalLabel, e.Textures?.Any(t => t.Normal is not null) == true, Info: null,
            Blanked: Blanks(e, f => f.Normal));
    private static BuildChipVm RmoChip(MeshEdit e) =>
        new(TextureMap.RmoLabel, e.Textures?.Any(t => t.Rmo is not null) == true,
            Workbench.WorkbenchMapVm.RmoChannels, Blanks(e, f => f.Rmo));

    /// <summary>One submesh's donor maps as a signature field: the ask on each slot and the file it names,
    /// in submesh order. What the stale-result read folds in beside the row's own fields, so a map that
    /// changed under a change that didn't takes the ✓ bar's line back off the folder it no longer
    /// describes. Empty for a change shipping no donor maps at all.</summary>
    internal static string DonorMapSignature(IReadOnlyList<SubmeshTextures>? rows)
    {
        if (rows is not { Count: > 0 }) return "";
        const char part = (char)0x1d;
        return string.Join(part, rows.OrderBy(r => r.Submesh).Select(r => string.Join(part,
            r.Submesh, (int)r.AlbedoAsk, r.Albedo, (int)r.NormalAsk, r.Normal, (int)r.RmoAsk, r.Rmo)));
    }

    /// <summary>Whether the build ships a flat map on this slot for any submesh of the edit. A chip cannot
    /// read this off the file names: every way a slot goes flat names no file, so an authored-only chip
    /// would show the whole state as no change at all. The rule itself is the build's own
    /// (<see cref="BlankedSlots"/>), asked under this edit's verb.</summary>
    private static bool Blanks(MeshEdit e, Func<BlankedSlots, bool> slot) =>
        e.Textures?.Any(t => slot(BlankedSlots.Of(t, e.Verb))) == true;

    public string Character { get; }
    public string Outfit { get; }
    public string Mesh { get; }
    public string Verb { get; }
    public string PartLabel { get; }
    /// <summary>The submesh whose material the Edit hop should land on, or null to land on the part.</summary>
    public int? EditSubmesh { get; }
    public IReadOnlyList<BuildChipVm> Chips { get; }

    public bool IsReplace => Verb == EditVerbs.Replace;
    public bool IsRetexture => Verb == EditVerbs.Retexture;
    public bool IsHide => Verb == EditVerbs.Hide;
    public bool HasChips => Chips.Count > 0;

    /// <summary>Ticked = this change ships. Unticking persists the exclusion and keeps the row, dimmed.</summary>
    [ObservableProperty] private bool _isIncluded;
    public bool IsExcluded => !IsIncluded;

    /// <summary>This change's own toggle key (tier 2), or null for none — the change is then always on
    /// whenever it ships. Always a <see cref="ModKeys.Normalize"/>d string.</summary>
    [ObservableProperty] private string? _toggleKey;
    public bool HasToggleKey => !string.IsNullOrWhiteSpace(ToggleKey);
    /// <summary>What the key field reads when nothing is bound.</summary>
    public const string NoKeyLabel = "＋ key";
    public string ToggleKeyLabel => ModKeys.Display(ToggleKey, NoKeyLabel);
    /// <summary>What an unticked row's key field and its clear say. Both go off with the tick, and a control
    /// that greys out without a reason reads as a dead one.</summary>
    public const string LeftOutKeyTip = "Left out of the build. The key stays for when it returns.";
    public string ToggleKeyTip => IsExcluded ? LeftOutKeyTip
        : HasToggleKey ? $"{ToggleKey} toggles this change in game."
        : "Bind a key that toggles this change in game.";
    /// <summary>The clear affordance's own tip, off the same state as the key field beside it.</summary>
    public string ClearKeyTip => IsExcluded ? LeftOutKeyTip : "Clear the key.";

    /// <summary>Replace only: what the key leaves on screen when it is off. Ticked = the part is absent;
    /// unticked = the character's own part draws, which is how a replacement is compared against stock. A
    /// Hide has no donor of its own, so vanilla is its only off state.</summary>
    [ObservableProperty] private bool _hideWhenOff;
    /// <summary>The key-behaviour controls stand on a bound key: with none, the change is always on and has
    /// no off state to describe. Replace is the only verb with a choice of what off means.</summary>
    public bool ShowsKeyState => HasToggleKey;
    public bool ShowsKeyOffMode => IsReplace && HasToggleKey;
    /// <summary>What a suppressed off state leaves on screen.</summary>
    public const string HidesWhenOffLine = "Key off: nothing draws there.";
    /// <summary>Both boxes ticked is a recipe of two states, so the tip states both: what off leaves, then
    /// where every run begins.</summary>
    public string HideWhenOffTip => IsExcluded ? LeftOutKeyTip
        : !HideWhenOff ? "Key off: the original part draws."
        : StartsOff ? $"{HidesWhenOffLine} {StartsOffLine}"
        : HidesWhenOffLine;

    /// <summary>This change ships off and the first press turns it on. Per session: the next launch starts
    /// here again, whatever was pressed last run.</summary>
    [ObservableProperty] private bool _startsOff;
    /// <summary>The one thing the row can't otherwise show: a press holds for its own run, so the box names
    /// where EVERY launch begins rather than restating its own label.</summary>
    public const string StartsOffLine = "Off at every launch.";
    public string StartsOffTip => IsExcluded ? LeftOutKeyTip
        : StartsOff ? StartsOffLine : "On at every launch.";

    /// <summary>What else this change's key switches, or empty when the key stands alone. Carried beside
    /// the key control as a ⚠ with this as its tooltip: one key is one emitted variable, so a shared key is
    /// a choice the author can make on purpose, and the row says who moves with it.</summary>
    [ObservableProperty] private string _keyCollisionTip = "";
    public bool HasKeyCollision => KeyCollisionTip.Length > 0;

    /// <summary>Raised on a tick. Set AFTER construction, so restoring the persisted state can't fire it.</summary>
    public Action<BuildRowVm, bool>? Toggled { get; set; }
    /// <summary>Raised when any part of the key binding changes — the key itself or what its off state
    /// means. The handler reads the whole binding off the row, so the two can never be persisted apart. Set
    /// AFTER construction, for the same reason the tick handler is.</summary>
    public Action<BuildRowVm>? KeyBound { get; set; }
    /// <summary>Raised by the row's Edit button — the hop to this change's part in the Edit step.</summary>
    public Action<BuildRowVm>? EditRequested { get; set; }

    [RelayCommand]
    private void Edit() => EditRequested?.Invoke(this);

    /// <summary>The key field's clear affordance. Writes the same null the capture field's Delete writes,
    /// so both clears persist and re-read the duplicate warnings through one route; the behaviour the key
    /// carried goes with it. The modes are reset without raising <see cref="KeyBound"/> — the key itself
    /// fires the one write, and a persist in between would state a binding half cleared.</summary>
    [RelayCommand]
    private void ClearKey()
    {
        _bindingSilent = true;
        try { HideWhenOff = false; StartsOff = false; }
        finally { _bindingSilent = false; }
        ToggleKey = null;
    }

    /// <summary>Set while one gesture writes several parts of the binding, so it persists once.</summary>
    private bool _bindingSilent;

    partial void OnIsIncludedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsExcluded));
        // every control of the key block goes off with the tick, so their tips change with it too
        OnPropertyChanged(nameof(ToggleKeyTip));
        OnPropertyChanged(nameof(ClearKeyTip));
        OnPropertyChanged(nameof(StartsOffTip));
        OnPropertyChanged(nameof(HideWhenOffTip));
        Toggled?.Invoke(this, value);
    }

    partial void OnKeyCollisionTipChanged(string value) => OnPropertyChanged(nameof(HasKeyCollision));

    partial void OnToggleKeyChanged(string? value)
    {
        OnPropertyChanged(nameof(HasToggleKey));
        OnPropertyChanged(nameof(ToggleKeyLabel));
        OnPropertyChanged(nameof(ToggleKeyTip));
        // the key-behaviour controls stand on the key, so clearing it takes them off the row with it
        OnPropertyChanged(nameof(ShowsKeyState));
        OnPropertyChanged(nameof(ShowsKeyOffMode));
        KeyBound?.Invoke(this);
    }

    partial void OnHideWhenOffChanged(bool value)
    {
        OnPropertyChanged(nameof(HideWhenOffTip));
        if (!_bindingSilent) KeyBound?.Invoke(this);
    }

    partial void OnStartsOffChanged(bool value)
    {
        OnPropertyChanged(nameof(StartsOffTip));
        // the suppressed off state reads as a recipe of both boxes, so its tip moves with this one
        OnPropertyChanged(nameof(HideWhenOffTip));
        if (!_bindingSilent) KeyBound?.Invoke(this);
    }
}

/// <summary>Authored = the map was edited on at least one of the edit's submeshes;
/// <paramref name="Blanked"/> = the build ships its own flat map on at least one (names no file).
/// <paramref name="Info"/> is what an acronym label doesn't say, carried on the tip's second line.
/// Authored outranks blanked in the glyph — ✎ is the state the row is ticked for; the tip carries the
/// other half (<see cref="MixedNote"/>).</summary>
public sealed record BuildChipVm(string Label, bool Authored, string? Info = null, bool Blanked = false)
{
    /// <summary>What an edit that did BOTH says beneath its glyph. A slot names a file or names none, so the
    /// two states can only have landed on different submeshes.</summary>
    public const string MixedNote = "Blanked on another submesh";

    /// <summary>The chip's own text. A blanked slot carries the WORD the map card marks it with rather than a
    /// glyph of its own: ∅ says "hidden" on the row verb beside it, and one glyph cannot mean two states a
    /// few pixels apart.</summary>
    public string Text => Authored ? Label + " ✎"
        : Blanked ? Label + " " + Workbench.WorkbenchMapVm.BlankedNote
        : Label;
    public string Tip => string.Join("\n", TipLines());
    private IEnumerable<string> TipLines()
    {
        yield return EditedState;
        if (Authored && Blanked) yield return MixedNote;
        if (Info is not null) yield return Info;
    }

    private string EditedState => Authored ? "Edited in this mod"
        : Blanked ? "Blanked in this mod" : "Not edited";
}
