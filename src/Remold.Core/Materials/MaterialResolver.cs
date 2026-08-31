using System;
using System.Collections.Generic;
using Remold.Core.Bundles;

namespace Remold.Core.Materials;

/// <summary>One texture a material binds: the shader <see cref="Slot"/>, the Texture2D
/// <see cref="TextureName"/>, and the <see cref="BundleHash"/> it lives in, which may be a shared bundle
/// the c_/cw_ texture index never surfaces.
///
/// <para><see cref="PathId"/> is the identity — the material's own texture PPtr. The NAME does not select:
/// one bundle can hold many same-named textures (a ramp library's are all
/// <c>RampMap_Linear_RGBAHalf</c>), so reading, hashing or exporting by name takes whichever comes first.
/// Carry the pathId to every read; the name is for showing the modder.</para></summary>
public readonly record struct ResolvedMap(string Slot, string TextureName, string BundleHash, long PathId);

/// <summary>The maps one material binds, in renderer <c>m_Materials</c> order so a multi-material part
/// can map each material to its own submesh. <see cref="Failed"/> separates a non-empty reference that
/// COULDN'T resolve from a deliberately-empty <c>0:0</c> placeholder — both carry no maps, but only the
/// former is a silent-untextured risk.</summary>
public readonly record struct MaterialMaps(string Material, IReadOnlyList<ResolvedMap> Maps, bool Failed = false);

/// <summary>
/// Shared material-following helpers: the base-color/normal slot tests and the <c>m_TexEnvs</c> →
/// (texture, bundle) walk, driven by <see cref="RendererResolver"/>. External texture PPtrs resolve via
/// CAB → bundle. All bundle reads go through a caller-supplied deobfuscate delegate so an export run's
/// cache is reused.
/// </summary>
public static class MaterialResolver
{
    /// <summary>Shader slots carrying the base-color (albedo) map.</summary>
    public static bool IsBaseColor(string slot) => slot is "_BaseMap" or "_MainTex";
    /// <summary>Shader slots carrying the normal map.</summary>
    public static bool IsNormal(string slot) => slot is "_BumpMap";
    /// <summary>Shader slots carrying the packed roughness/metallic/occlusion map.</summary>
    public static bool IsRmo(string slot) => slot is "_RMOTex";
    /// <summary>Shader slots carrying the toon ramp — the shading lookup, not a picture of the surface.</summary>
    public static bool IsRamp(string slot) => slot is "_RampMap";
    /// <summary>Shader slots carrying the effect overlay: the hair specular band, the face blush/expression
    /// tint. An ordinary picture the shader blends over the base colour.</summary>
    public static bool IsBlend(string slot) => slot is "_BlendTex";

    /// <summary>Follow a material's already-read <c>m_TexEnvs</c> slots to (texture, bundle, pathId)
    /// triples, resolving external PPtrs via <paramref name="cabToBundle"/> (the caller's scope-bounded CAB
    /// join), deduped only when the SAME shader property repeats the SAME texture identity. Two properties
    /// that intentionally bind one Texture2D remain two bindings: property identity answers which material
    /// input is addressed, while resource identity separately answers which pixels are shared. Deduping by
    /// NAME would also collapse same-named assets, which ramp libraries ship by design.</summary>
    internal static IReadOnlyList<ResolvedMap> ResolveTexSlots(
        Func<string, string?> cabToBundle, BundleReader reader, Func<string, byte[]?> tryDeobfuscate,
        string matBundle, byte[] matDec, IReadOnlyList<string> externalCabs, IReadOnlyList<BundleReader.TexSlot> slots)
    {
        var seen = new HashSet<(string Slot, string Bundle, long PathId)>();
        var outp = new List<ResolvedMap>();
        foreach (var s in slots)
        {
            string texBundle; byte[] texDec;
            if (s.FileId == 0) { texBundle = matBundle; texDec = matDec; }
            else
            {
                var cab = s.FileId - 1 < externalCabs.Count ? externalCabs[s.FileId - 1] : null;
                var bh = cab is null ? null : cabToBundle(cab);
                var dep = bh is null ? null : tryDeobfuscate(bh);
                if (bh is null || dep is null) continue;            // dependency not present in this install
                texBundle = bh; texDec = dep;
            }
            var name = reader.TextureNameByPathId(texDec, s.PathId);
            if (name is null || !seen.Add((s.Slot, texBundle, s.PathId))) continue; // unreadable/exact repeat
            outp.Add(new ResolvedMap(s.Slot, name, texBundle, s.PathId));
        }
        return outp;
    }
}
