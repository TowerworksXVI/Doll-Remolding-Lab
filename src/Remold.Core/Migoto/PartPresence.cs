using System;
using System.Collections.Generic;
using Remold.Core.Tables;

namespace Remold.Core.Migoto;

/// <summary>Which scenes a part is on screen in: everywhere, combat only, or dorm only.</summary>
public enum PresenceContext { Always, Fight, Dorm }

/// <summary>
/// When a roster part is guaranteed on screen, on two independent axes: the scene context its token's
/// tail binds it to, and the wardrobe variant (<see cref="PartScheme"/>) it belongs to. Context parts
/// are additive, not alternatives: a worn variant's base part shows in every scene, and its
/// <c>_Fight</c>/<c>_Dorm</c> siblings join it in their scene, so a base part is present whenever its
/// context sibling is and never the reverse. A recovery source must be on screen whenever the replaced
/// part is, or the frames drawing the replacement pose its bones from a buffer nothing refreshed.
/// </summary>
public readonly record struct PartPresence(PresenceContext Context, long VariantId)
{
    /// <summary>Not wardrobe-gated.</summary>
    public const long NoVariant = 0;

    /// <summary>A variant-shaped token the scheme doesn't list. Such a part can vouch for nothing, and
    /// only unconditional parts may pool for a Replace on it.</summary>
    public const long UnknownVariant = -1;

    /// <summary>On screen unconditionally.</summary>
    public static readonly PartPresence Always = new(PresenceContext.Always, NoVariant);

    /// <summary>True when a part with this presence is on screen every time a part with
    /// <paramref name="target"/>'s presence is.</summary>
    public bool Covers(PartPresence target) =>
        (Context == PresenceContext.Always || Context == target.Context)
        && (VariantId == NoVariant || (VariantId != UnknownVariant && VariantId == target.VariantId));

    /// <summary>
    /// Classify a part token against its outfit's wardrobe scheme (null = the outfit is not modular, or
    /// no scheme is available). The context tail is read off the token itself, which also covers the
    /// rare names that put the tail ahead of other suffixes. The wardrobe variant is the scheme resource
    /// token the context-stripped token extends, longest match winning, so <c>P1_body1</c> lands on the
    /// <c>P1_body1</c> resource and not on <c>P1_body</c>, while suffixed part names land on their base
    /// resource.
    /// </summary>
    public static PartPresence Classify(string token, IReadOnlyList<PartScheme.Slot>? schemeSlots)
    {
        var (stem, tail) = Model.MeshName.SplitVariant(token);
        var context = tail is null ? PresenceContext.Always
            : tail.Equals("Fight", StringComparison.OrdinalIgnoreCase) ? PresenceContext.Fight
            : PresenceContext.Dorm;

        long variant = NoVariant;
        int bestLen = -1;
        foreach (var slot in schemeSlots ?? Array.Empty<PartScheme.Slot>())
            foreach (var v in slot.Variants)
                foreach (var resource in v.Tokens)
                    if (resource.Length > bestLen
                        && stem.StartsWith(resource, StringComparison.OrdinalIgnoreCase))
                    {
                        bestLen = resource.Length;
                        variant = v.Id;
                    }
        if (bestLen < 0 && Model.OutfitLayout.IsModularToken(stem)) variant = UnknownVariant;
        return new PartPresence(context, variant);
    }
}
