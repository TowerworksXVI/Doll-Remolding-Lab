using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Materials;
using Remold.Core.Model;

namespace Remold.Core.Textures;

/// <summary>One texture to export for a part: its <see cref="Name"/>, the <see cref="Bundle"/> it lives
/// in (null ⇒ the reference couldn't be pinned, which every exporter fails loudly on), whether it is the
/// base-color / normal / RMO map, and <see cref="Source"/> for logging.</summary>
public readonly record struct TexTarget(string Name, string? Bundle, bool IsBaseColor, bool IsNormal, string Source,
    bool IsRmo = false);

/// <summary>The base-color, normal and RMO texture names one submesh's preview material samples (any may
/// be null), for per-submesh material assignment in the glb. <see cref="AllMaps"/> is every texture that
/// material references, deduped — the UV-guide grouping key, since a submesh's islands land identically
/// on every map of its material (one UV0).</summary>
public readonly record struct SubmeshMaps(string? BaseColor, string? Normal, string? Rmo = null,
    IReadOnlyList<string>? AllMaps = null);

/// <summary>Everything resolved for a part: <see cref="All"/> = every texture to export, deduped;
/// <see cref="Submeshes"/> = the per-submesh base/normal assignment (length == submesh count). An empty
/// <see cref="All"/> means the renderer bound no textures — a loud, user-visible miss.
/// <para><see cref="HasFailedMaterial"/> marks a PARTIAL miss: a non-empty material reference that
/// couldn't be read. The caller must surface it even when sibling slots resolved, or that submesh
/// exports untextured in silence.</para></summary>
public sealed record PartTextures(IReadOnlyList<TexTarget> All, IReadOnlyList<SubmeshMaps> Submeshes,
    bool HasFailedMaterial = false);

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
        var all = new List<TexTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name, string? bundle, bool baseC, bool normal, bool rmo, string source)
        {
            if (seen.Add(name)) all.Add(new TexTarget(name, bundle, baseC, normal, source, rmo));
        }

        var groups = new List<SubmeshMaps>();   // one (base,normal) group per renderer material slot, in submesh order

        // groups are taken verbatim from the renderer, empty slots included: order = alignment
        var byRenderer = RendererResolver.ResolveByRenderer(scope, reader, tryDeobfuscate, outfit, part);
        // a FAILED slot is a PARTIAL miss even when siblings bind maps; a whole-slot miss stays the
        // caller's separate all-fail report
        bool anyFailed = byRenderer.Any(mm => mm.Failed);
        if (byRenderer.Any(mm => mm.Maps.Count > 0))
        {
            foreach (var mm in byRenderer)
            {
                string? baseN = null, normN = null, rmoN = null;
                var maps = new List<string>();
                foreach (var m in mm.Maps)
                {
                    Add(m.TextureName, m.BundleHash, MaterialResolver.IsBaseColor(m.Slot),
                        MaterialResolver.IsNormal(m.Slot), MaterialResolver.IsRmo(m.Slot), "renderer");
                    if (MaterialResolver.IsBaseColor(m.Slot)) baseN ??= m.TextureName;
                    else if (MaterialResolver.IsNormal(m.Slot)) normN ??= m.TextureName;
                    else if (MaterialResolver.IsRmo(m.Slot)) rmoN ??= m.TextureName;
                    if (!maps.Contains(m.TextureName)) maps.Add(m.TextureName);
                }
                groups.Add(new SubmeshMaps(baseN, normN, rmoN, maps));
            }
        }

        return new PartTextures(all, AssignGroups(groups, submeshCount), anyFailed);
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
