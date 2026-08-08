using System;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Migoto;

namespace Remold.Core.Workbench;

/// <summary>
/// Whether a part's GAME mesh can be replaced at all — the routing rule
/// (<see cref="StreamDump.Route"/>) asked of a part's bundle identity instead of an already-read
/// mesh field. The build's own refusal is a separate surface; this is the one the pane reads so the verb is
/// never offered for a mesh it cannot end in.
///
/// <para>The read costs a bundle deobfuscate plus a Mesh type-tree deserialize, so callers ask LAZILY, at a
/// seam that already resolves the part's mesh, and memoize the answer.</para>
/// </summary>
public static class PartSkinGate
{
    /// <summary>Why this mesh's geometry can't be replaced, or null when it can be replaced by SOME
    /// route — a mesh the pooled swap can't take may still take the rigid one. A mesh riding a runtime
    /// spring chain refuses ahead of the skin rule: its skin is usually recoverable, and the refusal is
    /// about the simulation driving its bones, not the stream. Null too when the bundle won't deobfuscate
    /// or carries no such mesh: that is a DIFFERENT failure, with its own loud route, and answering
    /// "unreplaceable" for it would blame the mesh for a read that never happened.</summary>
    /// <param name="tryDeobfuscate">non-throwing logical-bundle → plain bytes (null when absent/unreadable).</param>
    /// <param name="pathId">the smr-body selector; 0 selects by <paramref name="meshName"/>.</param>
    public static StreamDump.SkinRefusal? Blocked(Func<string, byte[]?> tryDeobfuscate, string bundle,
        string meshName, long pathId = 0, BundleReader? reader = null)
    {
        if (string.IsNullOrEmpty(bundle) || (string.IsNullOrEmpty(meshName) && pathId == 0)) return null;
        try
        {
            var dec = tryDeobfuscate(bundle);
            if (dec is null) return null;
            var field = (reader ?? new BundleReader()).GetMeshField(dec, meshName, pathId);
            if (field is null) return null;
            if (Skeleton.BoneTable.HasSpringChain(
                    field["m_BoneNameHashes"]["Array"].Children.Select(c => c.AsUInt)))
                return StreamDump.SkinRefusal.SpringRig;
            if (StreamDump.Route(field) is not null) return null;
            return StreamDump.UnrecoverableSkin(field)?.Kind;
        }
        catch { return null; }
    }
}
