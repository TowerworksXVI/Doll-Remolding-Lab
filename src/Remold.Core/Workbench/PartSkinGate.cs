using System;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Mesh;
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
        TryBlocked(tryDeobfuscate, bundle, meshName, pathId, out var refusal, reader);
        return refusal;
    }

    /// <summary>Read the refusal and say whether that read settled an answer. False is the same fail-open
    /// null as <see cref="Blocked"/>, but identifies an unreadable bundle or a throwing mesh parse so a
    /// caller must not memoize that momentary answer.</summary>
    internal static bool TryBlocked(Func<string, byte[]?> tryDeobfuscate, string bundle,
        string meshName, long pathId, out StreamDump.SkinRefusal? refusal, BundleReader? reader = null) =>
        TryBlenderEditAnswers(tryDeobfuscate, bundle, meshName, pathId, out refusal, out _, reader,
            readGeometry: false);

    /// <summary>Both per-mesh answers the Blender-edit surfaces need, from ONE bundle read: the
    /// replaceability refusal (<see cref="Blocked"/>) and whether the mesh is drawn from COLLAPSED POINTS —
    /// every triangle exactly zero-area, a billboard cloud a game shader inflates at draw time. Blender
    /// cannot carry such a mesh's authored normals, so editing it there is meaningless and the Open verbs
    /// refuse; replacement itself stays possible, which is why this answer deliberately never joins
    /// <see cref="Blocked"/> — the Build plan's question. Same settled-read contract as
    /// <see cref="TryBlocked"/>: false means nothing was read and nothing may be memoized.</summary>
    internal static bool TryBlenderEditAnswers(Func<string, byte[]?> tryDeobfuscate, string bundle,
        string meshName, long pathId, out StreamDump.SkinRefusal? refusal, out bool collapsedBillboard,
        BundleReader? reader = null, bool readGeometry = true)
    {
        refusal = null;
        collapsedBillboard = false;
        if (string.IsNullOrEmpty(bundle) || (string.IsNullOrEmpty(meshName) && pathId == 0)) return true;
        try
        {
            var dec = tryDeobfuscate(bundle);
            if (dec is null) return false;
            var field = (reader ?? new BundleReader()).GetMeshField(dec, meshName, pathId);
            if (field is null) return true;
            if (Skeleton.BoneTable.HasSpringChain(
                    field["m_BoneNameHashes"]["Array"].Children.Select(c => c.AsUInt)))
                refusal = StreamDump.SkinRefusal.SpringRig;
            else if (StreamDump.Route(field) is null)
                refusal = StreamDump.UnrecoverableSkin(field)?.Kind;
            // The geometry decode is the one read here that can throw on data the header checks accept
            // (an unknown vertex format, a truncated stream). It runs only when no refusal is settled —
            // a refused part's geometry is never consulted — and its own failure reads as "not
            // collapsed" IN ITS OWN CATCH, so it can never discard the refusal above: a corrupt vertex
            // stream is not evidence the part is editable, and even less that it is a billboard.
            if (readGeometry && refusal is null)
                try { collapsedBillboard = MeshGltf.AllFacesZeroArea(UnityMesh.Decode(field)); }
                catch { collapsedBillboard = false; }
            return true;
        }
        catch { return false; }
    }

    /// <summary>The collapsed-points refusal as the ② Edit page says it — one home beside
    /// <see cref="EditRefusal"/> for the disabled opens' hover reason and the refused click's status
    /// line.</summary>
    public const string CollapsedBillboardRefusal =
        "This mesh is drawn from collapsed points and cannot be edited in Blender.";

    /// <summary>The refusal as the ② Edit page says it: the disabled opens' hover reason and the refused
    /// click's status line, one home so the two cannot drift apart. One short sentence — the live map,
    /// shading and Hide controls beside it already show what the part still takes.</summary>
    public static string EditRefusal(StreamDump.SkinRefusal refusal) => refusal switch
    {
        StreamDump.SkinRefusal.BlendShapes =>
            "This mesh uses expressions and cannot be edited in Blender.",
        StreamDump.SkinRefusal.SpringRig =>
            "This mesh moves on the game's spring bones and cannot be edited in Blender.",
        _ => "This mesh's skin is in a shape that cannot be edited in Blender.",
    };

    /// <summary>The refusal as a Build plan verdict reason: the ③ Blocked box renders it beside the edit it
    /// names, so it states the fact about the original mesh rather than pointing at buttons.</summary>
    public static string PlanRefusal(StreamDump.SkinRefusal refusal) => refusal switch
    {
        StreamDump.SkinRefusal.BlendShapes =>
            "the original mesh uses expressions, so its shape cannot be replaced",
        StreamDump.SkinRefusal.SpringRig =>
            "the original mesh moves on the game's own spring bones, so its shape cannot be replaced",
        _ => "the original mesh's skin is stored in a shape replacement cannot read",
    };
}
