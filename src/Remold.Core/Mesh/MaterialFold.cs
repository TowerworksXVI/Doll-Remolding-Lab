using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Mesh;

/// <summary>
/// How a mesh's submeshes land on the installed material positions of the part they stand for. The game
/// draws an N-material part once per material; a replacement's submesh k draws at material k's fire, and
/// every submesh past the last material draws at the last DRAWABLE material's — a position whose stock
/// submesh has no indices never fires, so nothing can draw there. One rule, shared by the edit's
/// per-submesh output slots, the outbound Blender inventory and the return's join, so a picture authored
/// for a folded submesh through any of the three reaches the same material.
/// </summary>
public static class MaterialFold
{
    /// <summary>The material position <paramref name="submesh"/> draws under, or -1 where no material can
    /// draw it. <paramref name="drawable"/> answers whether a position fires at all; null treats every
    /// position as drawable.</summary>
    public static int MaterialPosition(int submesh, int materialCount, Func<int, bool>? drawable = null)
    {
        if (materialCount <= 0 || submesh < 0) return -1;
        int folded = Math.Min(submesh, materialCount - 1);
        if (drawable is null || drawable(folded)) return folded;
        for (int position = materialCount - 1; position >= 0; position--)
            if (drawable(position)) return position;
        return -1;
    }

    /// <summary>Re-project an outbound record's rows onto <paramref name="primitiveCount"/> primitives: each
    /// material's property rows are re-keyed to every primitive that folds onto it, and a material no
    /// primitive folds onto keeps one primitive-less row per property so the inventory survives a send with
    /// fewer submeshes. The rows' own <see cref="PreviewMaps.TransportBinding.Drawable"/> answers the fold;
    /// a row written without it reads as drawable. A material the record holds no row for (one that binds
    /// no readable picture) still takes its own primitive, which then carries nothing; the highest material
    /// the record names is the last one, so extra primitives fold onto it.</summary>
    public static IReadOnlyList<PreviewMaps.TransportBinding> FoldOntoPrimitives(
        IReadOnlyList<PreviewMaps.TransportBinding> rows, int primitiveCount)
    {
        if (rows.Count == 0) return rows;
        var byMaterial = rows.GroupBy(row => row.MaterialIndex)
            .ToDictionary(group => group.Key, group => group
                .GroupBy(row => row.ShaderProperty, StringComparer.Ordinal)
                .Select(property => property.First()).ToList());
        int materialCount = byMaterial.Keys.Max() + 1;
        bool Drawable(int material) =>
            !byMaterial.TryGetValue(material, out var own) || own.Any(row => row.Drawable != false);
        var result = new List<PreviewMaps.TransportBinding>();
        var projected = new HashSet<int>();
        for (int primitive = 0; primitive < primitiveCount; primitive++)
        {
            int material = MaterialPosition(primitive, materialCount, Drawable);
            if (material < 0 || !byMaterial.TryGetValue(material, out var own)) continue;
            projected.Add(material);
            foreach (var row in own) result.Add(row with { PrimitiveIndex = primitive });
        }
        foreach (var (material, own) in byMaterial.OrderBy(pair => pair.Key))
            if (!projected.Contains(material))
                foreach (var row in own) result.Add(row with { PrimitiveIndex = null });
        return result;
    }
}
