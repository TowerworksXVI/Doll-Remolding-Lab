using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Materials;
using Remold.Core.Model;

namespace Remold.Core.Textures;

/// <summary>One texture to export for a part: its <see cref="Name"/>, the <see cref="Bundle"/> it lives
/// in (null ⇒ the reference couldn't be pinned, which every exporter fails loudly on), whether it is the
/// base-color / normal / RMO map, and <see cref="Source"/> for logging.
///
/// <para><see cref="PathId"/> is the Texture2D identity the renderer pinned, carried from
/// <see cref="Materials.ResolvedMap"/>: a name does not select one texture in a bundle (see
/// <see cref="Bundles.BundleReader.TextureRef"/>), so a read taken on the name alone can hand back a
/// same-named sibling's pixels. 0 where no reference supplied one, which reads by name as before.</para>
/// </summary>
public readonly record struct TexTarget(string Name, string? Bundle, bool IsBaseColor, bool IsNormal, string Source,
    bool IsRmo = false, long PathId = 0, bool IsRamp = false, bool IsBlend = false);

/// <summary>One exact shader-property binding in a renderer material. <see cref="Texture"/> identifies the
/// Texture2D resource; <see cref="ShaderProperty"/> identifies the material input. Two properties may point
/// at the same resource and remain two rows here.</summary>
public readonly record struct BoundTexture(string ShaderProperty, TexTarget Texture);

/// <summary>Every texture binding in one installed renderer-material position. This inventory is kept apart
/// from submesh projection: a surplus material has no primitive carrier, but its bindings still have an exact
/// owner and must not disappear from the transport record.</summary>
public readonly record struct MaterialTextureBindings(int MaterialIndex, string Material,
    IReadOnlyList<BoundTexture> Textures);

/// <summary>The base-color, normal and RMO texture names one submesh's preview material samples (any may
/// be null), for per-submesh material assignment in the glb. <see cref="AllMaps"/> is every texture that
/// material references, deduped — the UV-guide grouping key, since a submesh's islands land identically
/// on every map of its material (one UV0).</summary>
public readonly record struct SubmeshMaps(string? BaseColor, string? Normal, string? Rmo = null,
    IReadOnlyList<string>? AllMaps = null, TexTarget? BaseColorTarget = null, TexTarget? NormalTarget = null,
    TexTarget? RmoTarget = null);

/// <summary>Everything resolved for a part: <see cref="All"/> = every texture to export, deduped;
/// <see cref="Submeshes"/> = the per-submesh base/normal assignment (length == submesh count). An empty
/// <see cref="All"/> means the renderer bound no textures — a loud, user-visible miss.
/// <para><see cref="HasFailedMaterial"/> marks a PARTIAL miss: a non-empty material reference that
/// couldn't be read. The caller must surface it even when sibling slots resolved, or that submesh
/// exports untextured in silence.</para></summary>
public sealed record PartTextures(IReadOnlyList<TexTarget> All, IReadOnlyList<SubmeshMaps> Submeshes,
    bool HasFailedMaterial = false, IReadOnlyList<MaterialTextureBindings>? Materials = null);

/// <summary>
/// The full texture set for an outfit part, resolved renderer-first and per-submesh from the outfit's
/// assembly prefab: the renderer slot's ordered <c>m_Materials</c> IS the game's submesh <i>i</i> →
/// material <i>i</i> binding, every reference CAB-exact (<see cref="Materials.RendererResolver"/>), and
/// empty slots are preserved because order is the alignment. The ONLY tier — no name-convention
/// fallback; a renderer that binds nothing yields an empty result the caller reports loudly.
/// </summary>
public static class PartTextureResolver
{
    public static PartTextures Resolve(
        Workbench.SubjectScope scope, BundleReader reader, Func<string, byte[]?> tryDeobfuscate, Outfit outfit, string part, int submeshCount)
    {
        submeshCount = Math.Max(1, submeshCount);
        var groups = new List<SubmeshMaps>();   // one (base,normal) group per renderer material slot, in submesh order
        var materials = new List<MaterialTextureBindings>();

        // groups are taken verbatim from the renderer, empty slots included: order = alignment
        var byRenderer = RendererResolver.ResolveByRenderer(scope, reader, tryDeobfuscate, outfit, part);
        // a FAILED slot is a PARTIAL miss even when siblings bind maps; a whole-slot miss stays the
        // caller's separate all-fail report
        bool anyFailed = byRenderer.Any(mm => mm.Failed);
        if (byRenderer.Any(mm => mm.Maps.Count > 0))
        {
            for (int materialIndex = 0; materialIndex < byRenderer.Count; materialIndex++)
            {
                var mm = byRenderer[materialIndex];
                string? baseN = null, normN = null, rmoN = null;
                TexTarget? baseTarget = null, normalTarget = null, rmoTarget = null;
                var maps = new List<string>();
                var bindings = new List<BoundTexture>();
                foreach (var m in mm.Maps)
                {
                    var target = new TexTarget(m.TextureName, m.BundleHash, MaterialResolver.IsBaseColor(m.Slot),
                        MaterialResolver.IsNormal(m.Slot), "renderer", MaterialResolver.IsRmo(m.Slot), m.PathId,
                        MaterialResolver.IsRamp(m.Slot), MaterialResolver.IsBlend(m.Slot));
                    bindings.Add(new BoundTexture(m.Slot, target));
                    if (MaterialResolver.IsBaseColor(m.Slot))
                    { baseN ??= m.TextureName; baseTarget ??= target; }
                    else if (MaterialResolver.IsNormal(m.Slot))
                    { normN ??= m.TextureName; normalTarget ??= target; }
                    else if (MaterialResolver.IsRmo(m.Slot))
                    { rmoN ??= m.TextureName; rmoTarget ??= target; }
                    if (!maps.Contains(m.TextureName)) maps.Add(m.TextureName);
                }
                groups.Add(new SubmeshMaps(baseN, normN, rmoN, maps, baseTarget, normalTarget, rmoTarget));
                materials.Add(new MaterialTextureBindings(materialIndex, mm.Material, bindings));
            }
        }

        return new PartTextures(ExportInventory(materials), AssignGroups(groups, submeshCount), anyFailed,
            materials);
    }

    /// <summary>One export row per exact Texture2D resource. Flags accumulate across bindings of that
    /// resource, except ramp-ness: a resource is ramp-only only when every binding of it is a ramp.</summary>
    internal static IReadOnlyList<TexTarget> ExportInventory(IReadOnlyList<MaterialTextureBindings> materials)
    {
        var all = new List<TexTarget>();
        var byResource = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in materials.SelectMany(material => material.Textures))
        {
            var target = binding.Texture;
            string key = $"{target.Bundle}\u001f{(target.PathId == 0 ? target.Name
                : target.PathId.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
            if (!byResource.TryGetValue(key, out int index))
            {
                byResource[key] = all.Count;
                all.Add(target);
                continue;
            }
            var current = all[index];
            all[index] = current with
            {
                IsBaseColor = current.IsBaseColor || target.IsBaseColor,
                IsNormal = current.IsNormal || target.IsNormal,
                IsRmo = current.IsRmo || target.IsRmo,
                IsRamp = current.IsRamp && target.IsRamp,
                IsBlend = current.IsBlend || target.IsBlend,
            };
        }
        return all;
    }

    /// <summary>Map ordered texture groups onto <paramref name="submeshCount"/> submeshes:
    /// material[i]→submesh[i], a shortfall repeats the last group, a surplus is truncated.</summary>
    public static SubmeshMaps[] AssignGroups(IReadOnlyList<SubmeshMaps> groups, int submeshCount)
    {
        var r = new SubmeshMaps[Math.Max(1, submeshCount)];
        if (groups.Count == 0) return r;                             // all empty → no preview
        for (int i = 0; i < r.Length; i++) r[i] = groups[Math.Min(i, groups.Count - 1)];
        return r;
    }
}
