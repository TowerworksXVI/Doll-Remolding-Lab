using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Model;
using Remold.Core.Tables;
using Remold.Core.Textures;

namespace Remold.App.ViewModels;

/// <summary>An outfit node — a picked SUBJECT, carrying ONE checkbox: is this (character · outfit) in the
/// mod? No part rows and no tri-state — the touched set is materialized in Edit, not picked here.</summary>
public partial class OutfitVm : ObservableObject
{
    private readonly Action<OutfitVm> _onInModToggled;
    private bool _suppressToggle;

    public Outfit Model { get; }
    public string Label { get; }
    public string Stem { get; }

    /// <summary>The roster fill hasn't confirmed this outfit yet: the row is a plain label with no
    /// checkbox, replaced by a fresh instance when the character is re-populated.</summary>
    public bool IsLoading { get; }

    /// <summary>A user toggle runs the add/remove path; <see cref="SetInModSilently"/> reflects the ledger
    /// without it.</summary>
    [ObservableProperty] private bool _isInMod;
    /// <summary>This subject has edited files — show the ✎ marker.</summary>
    [ObservableProperty] private bool _hasEdits;

    public bool ShowSubjectBox => !IsLoading;

    /// <summary>The weapon-tier badge (R / SR / SSR) with its tier color; null rows show none.</summary>
    public string? Badge { get; }
    public bool HasBadge => Badge is not null;
    public IBrush BadgeBrush { get; } = Brushes.Transparent;

    // Immutable brushes: the first OutfitVm is built on the loader's background thread, and a
    // mutable SolidColorBrush is thread-affine — its construction there fails the whole type.
    private static readonly IBrush RarityR = Brush.Parse("#5B9FD6");
    private static readonly IBrush RaritySr = Brush.Parse("#A97FD6");
    private static readonly IBrush RaritySsr = Brush.Parse("#D6A85B");

    /// <summary>Rarity code → the badge pair, shared by the outfit row and the collapsed character
    /// row. An unknown code shows nothing rather than a wrong tier.</summary>
    internal static (string Badge, IBrush Brush)? RarityBadge(int? rarity) => rarity switch
    {
        3 => ("R", RarityR),
        4 => ("SR", RaritySr),
        5 => ("SSR", RaritySsr),
        _ => null,
    };

    public OutfitVm(Outfit outfit, IEnumerable<string> partTokens, Action<OutfitVm> onInModToggled, bool isLoading = false)
    {
        Model = outfit;
        Stem = outfit.Stem;
        _onInModToggled = onInModToggled;
        IsLoading = isLoading;
        if (RarityBadge(outfit.WeaponRarity) is { } badge) (Badge, BadgeBrush) = badge;
        // "<kind> · <name> · <stem> [· modular]". The leading "kind · name" comes from the ONE shared home,
        // so this tree matches the workbench header order; the stem trails only when it differs.
        var layout = OutfitLayout.Build(partTokens);
        var kindAndName = FriendlyNames.KindAndLabel(outfit);
        var trailStem = string.Equals(FriendlyNames.Label(outfit), outfit.Stem, System.StringComparison.Ordinal)
            ? "" : $"  ·  {outfit.Stem}";
        Label = $"{kindAndName}{trailStem}{(layout.IsModular ? "  ·  modular" : "")}";
    }

    /// <summary>Set the checkbox WITHOUT running the add/remove path, for reflecting the ledger and for
    /// teardown. A user-driven toggle is the only add/remove.</summary>
    public void SetInModSilently(bool v)
    {
        if (IsInMod == v) return;
        _suppressToggle = true;
        IsInMod = v;
        _suppressToggle = false;
    }

    partial void OnIsInModChanged(bool value)
    {
        if (_suppressToggle) return;
        _onInModToggled(this);
    }
}

/// <summary>A character node, built in two phases: from the DB roster (name only), then
/// <see cref="Populate"/>d once the roster fill confirms its outfits. A single-outfit character COLLAPSES so
/// the character row IS the subject row; a multi-outfit one is a grouping header.</summary>
public partial class CharacterVm : ObservableObject
{
    private readonly Action<CharacterVm, OutfitVm> _onSubjectToggle;
    private readonly Action<CharacterVm, bool> _onCharacterToggle;

    public Character Model { get; }
    /// <summary>The internal roster name — the stable key the roster routes and dedups on, and the
    /// SelectionEntry's Character. NOT the row label; the tree shows <see cref="DisplayName"/>.</summary>
    public string Name { get; }
    /// <summary>The localized name when it resolved, else the internal <see cref="Name"/>.</summary>
    public string DisplayName { get; }
    public long GunId { get; }
    public bool IsExpanded { get; set; }
    public ObservableCollection<OutfitVm> Outfits { get; } = new();
    public bool HasOutfits => Outfits.Count > 0;

    /// <summary>A single-outfit character: the character row IS the subject row, carrying the checkbox.</summary>
    public bool IsSingleOutfit => Outfits.Count == 1;

    /// <summary>The outfit subject rows — except a single-outfit character collapses to a LEAF, so an enemy
    /// with one model renders as one tickable line rather than a nested dropdown.</summary>
    public IReadOnlyList<object> TreeChildren =>
        IsSingleOutfit ? System.Array.Empty<object>() : Outfits.Cast<object>().ToList();

    /// <summary>The collapsed row's outfit stem, shown only when it isn't already the row label.</summary>
    public string TreeDetail =>
        IsSingleOutfit && !string.Equals(Outfits[0].Stem, DisplayName, System.StringComparison.OrdinalIgnoreCase)
            ? Outfits[0].Stem : "";
    public bool HasTreeDetail => TreeDetail.Length > 0;

    [ObservableProperty] private bool _isFound;
    public double NameOpacity => IsFound ? 1.0 : 0.45;
    partial void OnIsFoundChanged(bool value) => OnPropertyChanged(nameof(NameOpacity));
    partial void OnIsLoadingChanged(bool value) => RefreshSubjectState();

    /// <summary>True until the roster fill confirms this character's outfits — a subject checkbox is
    /// meaningless with no outfit resolved.</summary>
    [ObservableProperty] private bool _isLoading = true;

    public CharacterVm(Character model, Action<CharacterVm, OutfitVm> onSubjectToggle,
        Action<CharacterVm, bool> onCharacterToggle)
    {
        Model = model;
        Name = model.Name;
        DisplayName = FriendlyNames.Label(model);
        GunId = model.GunId;
        _onSubjectToggle = onSubjectToggle;
        _onCharacterToggle = onCharacterToggle;
    }

    /// <summary>Brighten the name as a "confirmed in the game" cue, for a caller that resolves a row without
    /// re-populating it.</summary>
    public void MarkFound() => IsFound = true;

    /// <summary>Fill in the outfit rows. Called TWICE: with the DB outfits (<paramref name="lightUp"/> false)
    /// then again once the fill confirms them (true). UI thread.</summary>
    public void Populate(IEnumerable<(Outfit Outfit, IEnumerable<string> Parts)> outfitData, bool lightUp = true)
    {
        Outfits.Clear();
        foreach (var d in outfitData)
            Outfits.Add(new OutfitVm(d.Outfit, d.Parts, o => _onSubjectToggle(this, o), isLoading: !lightUp));
        IsLoading = !lightUp;
        if (lightUp) IsFound = true;
        OnPropertyChanged(nameof(HasOutfits));
        OnPropertyChanged(nameof(TreeChildren));   // a recomputed snapshot — the tree re-binds
        OnPropertyChanged(nameof(TreeDetail));
        OnPropertyChanged(nameof(HasTreeDetail));
        RefreshSubjectState();
    }

    // ---- the collapsed single-outfit subject checkbox (proxies the one outfit) ----

    /// <summary>Only a resolved single-outfit character; a multi-outfit one is a header whose outfit rows
    /// carry the checkboxes.</summary>
    public bool ShowSubjectBox => !IsLoading && IsSingleOutfit;
    /// <summary>Show the character row as a grouping header. It ALSO carries <see cref="CharacterInMod"/>,
    /// so a whole character can be grabbed at once.</summary>
    public bool ShowGroupHeader => !IsLoading && !IsSingleOutfit;

    // ---- the multi-outfit character-level "grab the whole character" checkbox ----

    /// <summary>A resolved multi-outfit character; a single-outfit one collapses to
    /// <see cref="ShowSubjectBox"/> and has nothing to aggregate.</summary>
    public bool ShowCharacterBox => !IsLoading && !IsSingleOutfit && HasOutfits;

    /// <summary>Grab an entire character at once: checked when ALL outfits are in, unchecked when none,
    /// indeterminate when mixed. The indeterminate state is DISPLAY-ONLY — a click from any state adds every
    /// outfit, unless all are already in, which removes them all. Both branches run the NORMAL per-outfit
    /// paths, the remove behind ONE composed confirm.</summary>
    public bool? CharacterInMod
    {
        get
        {
            if (!ShowCharacterBox) return false;
            int inCount = Outfits.Count(o => o.IsInMod);
            return inCount == 0 ? false : inCount == Outfits.Count ? true : null;   // mixed = indeterminate display
        }
        set
        {
            // IGNORE the checkbox's cycled `value` and drive off the current aggregate, so a click from
            // MIXED checks-all rather than the three-state default of stepping to unchecked.
            bool addAll = !(Outfits.Count > 0 && Outfits.All(o => o.IsInMod));
            _onCharacterToggle(this, addAll);
        }
    }

    /// <summary>Any outfit under this character has edits.</summary>
    public bool CharacterHasEdits => ShowCharacterBox && Outfits.Any(o => o.HasEdits);

    /// <summary>Proxies the single outfit's <see cref="OutfitVm.IsInMod"/>, so toggling the character row
    /// runs the SAME add/remove path as an outfit row.</summary>
    public bool SubjectInMod
    {
        get => IsSingleOutfit && Outfits[0].IsInMod;
        set { if (IsSingleOutfit) Outfits[0].IsInMod = value; }
    }
    /// <summary>The single outfit's edited state.</summary>
    public bool SubjectHasEdits => IsSingleOutfit && Outfits[0].HasEdits;

    /// <summary>The collapsed row's tier badge — the single outfit's own.</summary>
    public string? SubjectBadge => IsSingleOutfit ? Outfits[0].Badge : null;
    public bool HasSubjectBadge => SubjectBadge is not null;
    public IBrush SubjectBadgeBrush => IsSingleOutfit ? Outfits[0].BadgeBrush : Brushes.Transparent;

    /// <summary>Re-raise the collapse proxies after the outfits or their in-mod/edited state changed.</summary>
    public void RefreshSubjectState()
    {
        OnPropertyChanged(nameof(IsSingleOutfit));
        OnPropertyChanged(nameof(ShowSubjectBox));
        OnPropertyChanged(nameof(ShowGroupHeader));
        OnPropertyChanged(nameof(SubjectInMod));
        OnPropertyChanged(nameof(SubjectHasEdits));
        OnPropertyChanged(nameof(SubjectBadge));
        OnPropertyChanged(nameof(HasSubjectBadge));
        OnPropertyChanged(nameof(SubjectBadgeBrush));
        OnPropertyChanged(nameof(ShowCharacterBox));
        OnPropertyChanged(nameof(CharacterInMod));
        OnPropertyChanged(nameof(CharacterHasEdits));
    }
}
