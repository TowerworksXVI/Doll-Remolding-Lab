using System.Collections.Generic;
using Remold.Core.Materials;
using Remold.Core.Project;

namespace Remold.Core.Workbench;

/// <summary>Which donor slot a shader slot ships as, and which of a part's submeshes one map dresses. THE
/// binding rule: the Edit pane's map cards, a drop that authors one, and the derivation that adopts a
/// texture edit onto a replacement all land on the same submeshes because they all ask here.</summary>
public static class DonorMapBinding
{
    /// <summary>The donor slot a shader texture slot ships as, or null when the build has no slot for it, so
    /// the card, the drop and the record agree.</summary>
    public static DonorMapSlot? DonorSlotOf(string shaderSlot) =>
        MaterialResolver.IsBaseColor(shaderSlot) ? DonorMapSlot.BaseColor
        : MaterialResolver.IsNormal(shaderSlot) ? DonorMapSlot.Normal
        : MaterialResolver.IsRmo(shaderSlot) ? DonorMapSlot.Rmo
        : null;

    /// <summary>(map slot kind, texture, bundle) → the part's renderer material slots binding it, in slot
    /// order. Material order IS submesh order, so this is also which donor submeshes a map authored against
    /// one of those cards lands on: one stock map dressing three of the part's slots is one image on three
    /// submeshes, which is how the game draws it. A map whose slot is none of the three shippable kinds is
    /// absent — nothing can be authored for it.</summary>
    public static Dictionary<(DonorMapSlot Slot, string Texture, string Bundle), List<int>>
        BoundSubmeshesByMap(IReadOnlyList<SubjectMaterial> materials)
    {
        var bound = new Dictionary<(DonorMapSlot, string, string), List<int>>();
        for (int mi = 0; mi < materials.Count; mi++)
            foreach (var map in materials[mi].Maps)
            {
                if (DonorSlotOf(map.Slot) is not { } slot) continue;
                var key = (slot, map.TextureName, map.BundleId);
                if (!bound.TryGetValue(key, out var list)) bound[key] = list = new List<int>();
                if (!list.Contains(mi)) list.Add(mi);
            }
        return bound;
    }
}
