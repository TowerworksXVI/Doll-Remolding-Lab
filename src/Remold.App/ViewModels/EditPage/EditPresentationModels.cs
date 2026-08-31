using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Remold.App.ViewModels.EditPage;

/// <summary>The authored state shown on one toon-ramp card.</summary>
public sealed record RampCardState(string Caption, string? Detail, bool HasPick, bool OptedOut = false)
{
    public bool HasRecord => HasPick || OptedOut;

    public const string VanillaCaption = "";
    public const string PickedCaption = "✎ chosen";

    /// <summary>What the recorded keep-the-original answer says. It is a state the modder chose, so the card
    /// states it rather than reading as a slot nobody has answered — and it carries no ownership marker,
    /// because what draws here is the original and the mod owns none of it.</summary>
    public const string KeptCaption = "Original toon ramp, kept";

    public static readonly RampCardState Vanilla = new(VanillaCaption, null, false);
    public static readonly RampCardState VanillaOptedOut = new(KeptCaption, null, false, OptedOut: true);

    public static RampCardState Picked(string? file) => new(PickedCaption, file, true);

    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public string Glyph =>
        Caption.StartsWith("⚠", StringComparison.Ordinal) ? "⚠"
        : Caption.StartsWith("✎", StringComparison.Ordinal) ? "✎"
        : "";
    public string CaptionDetail => Glyph.Length > 0 ? Caption[Glyph.Length..].TrimStart() : Caption;
    public bool HasGlyph => Glyph.Length > 0;
    public bool HasCaption => Caption.Length > 0;
    public bool IsProblem => Glyph == "⚠";
    public bool IsOwned => Glyph == "✎";
}

/// <summary>One node of the read-only skeleton tree shown by the Edit page.</summary>
public sealed partial class SkeletonNodeVm : ObservableObject
{
    public SkeletonNodeVm(Remold.Core.Workbench.SkeletonBoneNode node)
    {
        Name = node.Name;
        HasChildren = node.HasChildren;
        Children = node.Children.Select(child => new SkeletonNodeVm(child)).ToList();
    }

    public string Name { get; }
    public bool HasChildren { get; }
    public IReadOnlyList<SkeletonNodeVm> Children { get; }

    [ObservableProperty] private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && Children.Count == 1) Children[0].IsExpanded = true;
    }
}
