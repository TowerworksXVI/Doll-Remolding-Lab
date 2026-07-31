using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Model;
using Remold.Core.Workbench;

namespace Remold.Core.Materials;

/// <summary>
/// The renderer-first texture tier: resolve a part's submesh materials from the outfit's assembly
/// prefab, whose renderer slot's ordered <c>m_Materials</c> is the game's own submesh <i>i</i> →
/// material <i>i</i> binding, every reference CAB-exact. Candidate prefabs come pre-parsed from the
/// subject's <see cref="SubjectScope"/> in formula-priority order. Returns one entry per material slot
/// IN ORDER, empty entries included, so the caller's submesh alignment survives. An empty list means no
/// prefab or no matching slot — the caller reports the miss loudly, as this is the ONLY tier.
/// </summary>
public static class RendererResolver
{
    public static IReadOnlyList<MaterialMaps> ResolveByRenderer(
        SubjectScope scope, BundleReader reader, Func<string, byte[]?> tryDeobfuscate, Outfit outfit, string part)
    {
        foreach (var candidate in scope.Candidates)
        {
            var slot = FindSlot(candidate.Prefab, outfit.MeshPrefix, part);
            // an all-empty slot binds nothing; keep looking in sibling prefabs rather than returning a
            // result that shadows a real owner
            if (slot is null || !slot.Materials.Any(m => !(m.Cab is null && m.PathId == 0))) continue;

            var result = new List<MaterialMaps>(slot.Materials.Count);
            foreach (var mref in slot.Materials)
            {
                // an empty material slot (PPtr 0:0) stays an empty GROUP — order IS the submesh binding
                if (mref.Cab is null && mref.PathId == 0)
                { result.Add(new MaterialMaps("", Array.Empty<ResolvedMap>())); continue; }

                string? matBundle = mref.Cab is null ? candidate.Bundle : scope.BundleForCab(mref.Cab);
                var matDec = matBundle is null ? null
                    : matBundle == candidate.Bundle ? candidate.Dec : tryDeobfuscate(matBundle);
                var te = matDec is null ? null : reader.GetMaterialTexEnvsByPathId(matDec, mref.PathId);
                if (te is null)   // dependency bundle absent, or no Material at that path — keep alignment, but
                { result.Add(new MaterialMaps("", Array.Empty<ResolvedMap>(), Failed: true)); continue; }   // FAILED, not a placeholder

                var maps = MaterialResolver.ResolveTexSlots(
                    scope.BundleForCab, reader, tryDeobfuscate, matBundle!, matDec!, te.Value.ExternalCabs, te.Value.Slots);
                result.Add(new MaterialMaps(te.Value.Name, maps));
            }
            return result;
        }
        return Array.Empty<MaterialMaps>();
    }

    /// <summary>
    /// The renderer slot for a part (slots are named after their meshes): exact lod0 name, else the
    /// first <c>_lod*</c> reconstruction, else by part TOKEN — the LOD token is INFIXED on outfit-state
    /// variant names (<c>c_X_slg_cloth1_lod0_Fight</c> is part <c>cloth1_Fight</c>), which string
    /// reconstruction misses. Token matches prefer the lod0-tagged slot, then sort by name.
    /// Base-outfit-named parts on an alt outfit carry the BASE prefix and are not found here.
    ///
    /// <para>The reconstruction step must stay ANCHORED (<see cref="MeshName.IsLodTierOf"/>): only
    /// <c>&lt;prefix&gt;&lt;part&gt;_lod&lt;n&gt;</c> with nothing after the digits. Unanchored, a token
    /// whose own lod0 tier doesn't exist would claim the VARIANT's slot and bind another garment's
    /// materials.</para>
    ///
    /// <para>The last step reads the token off the prefix each slot carries ITSELF
    /// (<see cref="MeshName.OwnPrefix"/>), which is what reaches a shared mesh named for another skin's
    /// family — the same rule the workbench derives that part's token by. It runs only after every
    /// subject-prefix step has missed, so the subject's own part always wins the name.</para>
    /// </summary>
    internal static PrefabSlot? FindSlot(CharacterPrefab prefab, string meshPrefix, string part)
    {
        var exact = meshPrefix + part + "_lod0";
        return prefab.Slots.FirstOrDefault(s => string.Equals(s.Name, exact, StringComparison.OrdinalIgnoreCase))
            ?? prefab.Slots.FirstOrDefault(s => MeshName.IsLodTierOf(s.Name, meshPrefix, part))
            ?? ByToken(s => MeshName.Part(s.Name, meshPrefix))
            ?? ByToken(s => MeshName.OwnPrefix(s.Name) is { } own ? MeshName.Part(s.Name, own) : "");

        PrefabSlot? ByToken(Func<PrefabSlot, string> tokenOf) =>
            prefab.Slots.Where(s => tokenOf(s).Equals(part, StringComparison.OrdinalIgnoreCase))
                  .OrderBy(s => s.Name.Contains("_lod0", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                  .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                  .FirstOrDefault();
    }
}
