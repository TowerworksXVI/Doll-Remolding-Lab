using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Remold.Core.Mesh;

namespace Remold.Core.Skeleton;

/// <summary>
/// The REAL skeleton of a prefab-shipped skinned mesh (enemies, NPC forms, props, summons), read from the
/// mesh's own bundle: its SkinnedMeshRenderer names the bones in mesh bone order (<c>m_Bones</c>), and the
/// Transform hierarchy carries their names, parenting, and rest pose. Two things the bind-pose-only rigged
/// export can't know:
///
/// <list type="bullet">
/// <item><b>Names/parenting for non-standard rigs.</b> Several prefab bodies ride a Biped (<c>Bip001</c>)
/// skeleton whose bone-name hashes anchor at <c>Bip001</c>, while the corpus <see cref="BoneTable"/> only
/// anchors at <c>root</c>/<c>Root_M</c>, so it resolves 0 of their bones and the armature degrades to flat
/// <c>bone_&lt;hash&gt;</c> nodes. <see cref="BonePaths"/> carries the true per-bone paths.</item>
/// <item><b>The scene-rest uprighting.</b> Some bodies ship lying down in bind space and are stood up by
/// the scene skeleton's rest pose (see <see cref="RestBake"/>). <see cref="Uprighting"/> is that transform,
/// snapped exact; null when already upright, or when it isn't a clean quarter-turn.</item>
/// </list>
///
/// Character outfit parts are runtime-assembled with NO SkinnedMeshRenderer in their bundles, so
/// <see cref="TryRead"/> returns null and the export keeps its BoneTable path.
/// </summary>
public sealed class SceneRig
{
    /// <summary>Per skin-bone path ('/'-joined, parents first), in the mesh's bone order — anchored at the
    /// node where the mesh's own bone-name hash verifies, so it matches what a hash lookup would name,
    /// falling back to the full hierarchy chain when no suffix hashes to it.</summary>
    public required IReadOnlyList<string> BonePaths { get; init; }

    /// <summary>The snapped bind→scene uprighting to bake (see <see cref="RestBake"/>); null = no bake
    /// (already upright, or not a clean axis-aligned rotation).</summary>
    public Matrix4x4? Uprighting { get; init; }

    /// <summary>The measured bind→scene transform whenever it is CONSISTENT across the mesh's bones — raw,
    /// not snapped, and ≈identity included, unlike the snapped <see cref="Uprighting"/> above. Null when the relation
    /// varies bone to bone (an articulated prop) or was never readable. What a pool build uses to restate
    /// this part's binds in a sibling's space: a delta composed of two measured rests needs no shared-bone
    /// corroboration, so it reaches parts a fitted delta cannot.</summary>
    public Matrix4x4? MeasuredRest { get; init; }

    /// <summary>Each skin bone's SCENE rest world (Unity row-vector space, mesh bone order) — where the
    /// prefab actually rests the bone, whether or not one bind→scene transform explains it. What the glb
    /// export poses a context part's joints with, so an articulated prop whose per-bone relation is
    /// inconsistent still sits where the prefab puts it.</summary>
    public IReadOnlyList<Matrix4x4>? BoneRestWorlds { get; init; }

    /// <summary>Rest worlds for the CONNECTOR nodes — path prefixes of <see cref="BonePaths"/> that aren't
    /// themselves skinned bones. A mesh's bind poses can't place them, so without this the armature parks
    /// them at the origin and Blender draws origin-to-first-child sticks. Keyed by the '/'-joined prefix, in
    /// BIND space (scene world · inverse(measured G)) so it composes with whatever uprighting the export
    /// applies. Empty when the bind→scene relation wasn't consistent.</summary>
    public IReadOnlyDictionary<string, Matrix4x4> ConnectorRests { get; init; } =
        new Dictionary<string, Matrix4x4>();

    /// <summary>One scene transform, decoupled from AssetsTools for the pure logic (and its tests).</summary>
    public readonly record struct SceneNode(long PathId, string Name, long Father, Vector3 Pos, Quaternion Rot, Vector3 Scale);

    /// <summary>Read the scene rig for <paramref name="meshName"/> out of its deobfuscated bundle. Null when
    /// the bundle has no SkinnedMeshRenderer for that mesh, when the bone list doesn't line up with the skin,
    /// or on any parse trouble — the caller then keeps the plain bind-pose export.</summary>
    public static SceneRig? TryRead(byte[] deobfuscatedBundle, string meshName, MeshSkin skin, long knownMeshPathId = 0)
    {
        if (!skin.IsSkinned) return null;
        try
        {
            var (am, inst) = Load(deobfuscatedBundle);

            // the Mesh asset's pathId — the SMR's m_Mesh must point at it locally. A caller that knows the
            // exact id passes it, since enemy bundles ship same-named copies.
            long meshPathId = knownMeshPathId;
            if (meshPathId == 0)
                foreach (var info in inst.file.AssetInfos.Where(i => i.TypeId == 43))
                    try { if (am.GetBaseField(inst, info)["m_Name"].AsString == meshName) { meshPathId = info.PathId; break; } }
                    catch { }
            if (meshPathId == 0) return null;

            // the mesh is LOCAL here, so the reference must be too
            return Read(am, inst, skin, smr =>
            {
                var p = smr["m_Mesh"];
                return p["m_FileID"].AsLong == 0 && p["m_PathID"].AsLong == meshPathId;
            });
        }
        catch { return null; }
    }

    /// <summary>The smr-body twin of <see cref="TryRead"/>: read the scene rig out of the subject's ASSEMBLY
    /// PREFAB bundle, whose SMR references the mesh by an external PPtr — the prefab carries the whole
    /// skeleton and the SMR's ordered <c>m_Bones</c> point into it positionally. The SMR is selected by its
    /// mesh reference's PATH ID, unique across a prefab's slots. Null on no matching SMR, non-local bones, a
    /// count mismatch, or any parse trouble.</summary>
    public static SceneRig? TryReadForMeshRef(byte[] deobfuscatedPrefabBundle, long meshPathId, MeshSkin skin)
    {
        if (!skin.IsSkinned || meshPathId == 0) return null;
        try
        {
            var (am, inst) = Load(deobfuscatedPrefabBundle);
            // the mesh lives in a dependency bundle, so the path id alone is the join
            return Read(am, inst, skin, smr => smr["m_Mesh"]["m_PathID"].AsLong == meshPathId);
        }
        catch { return null; }
    }

    private static (AssetsManager Manager, AssetsFileInstance Instance) Load(byte[] deobfuscatedBundle)
    {
        var am = new AssetsManager();
        var bun = am.LoadBundleFile(new MemoryStream(deobfuscatedBundle), "live.bundle");
        return (am, am.LoadAssetsFileFromBundle(bun, 0));
    }

    /// <summary>What both public reads do once their own SMR is picked out: the bundle's first SMR that
    /// <paramref name="selects"/> accepts (LOD tiers each have their own mesh + SMR, so first-match is the
    /// selector's problem, not this one), its ordered bone Transform pathIds, and the rig those place.
    /// Null when no SMR matches, when a bone lives outside this bundle — the Transform hierarchy here can't
    /// place it, so no rest pose is derivable — or when the bone list doesn't line up with the skin. A
    /// per-asset parse failure is skipped rather than fatal: a bundle carrying one unreadable renderer
    /// still has its own to offer.</summary>
    private static SceneRig? Read(AssetsManager am, AssetsFileInstance inst, MeshSkin skin,
        Func<AssetTypeValueField, bool> selects)
    {
        AssetTypeValueField? smr = null;
        foreach (var info in inst.file.AssetInfos.Where(i => i.TypeId == 137))
            try
            {
                var bf = am.GetBaseField(inst, info);
                if (selects(bf)) { smr = bf; break; }
            }
            catch { }
        if (smr is null) return null;

        var boneIds = new List<long>(skin.BoneCount);
        var bonesArr = smr["m_Bones"];
        var elems = bonesArr.Children.Count == 1 && bonesArr.Children[0].FieldName == "Array"
            ? bonesArr.Children[0].Children : bonesArr.Children;
        foreach (var e in elems)
        {
            if (e["m_FileID"].AsLong != 0) return null;   // bones must be local
            boneIds.Add(e["m_PathID"].AsLong);
        }
        if (boneIds.Count != skin.BoneCount) return null;

        return FromScene(CollectNodes(am, inst), boneIds, skin);
    }

    /// <summary>The bundle's Transform nodes with resolved GameObject names.</summary>
    private static List<SceneNode> CollectNodes(AssetsManager am, AssetsFileInstance inst)
    {
        var goName = new Dictionary<long, string>();
        var nodes = new List<SceneNode>();
        foreach (var info in inst.file.AssetInfos)
        {
            if (info.TypeId == 1)
            {
                try { goName[info.PathId] = am.GetBaseField(inst, info)["m_Name"].AsString; } catch { }
            }
            else if (info.TypeId == 4)
            {
                try
                {
                    var bf = am.GetBaseField(inst, info);
                    nodes.Add(new SceneNode(info.PathId, "", bf["m_Father"]["m_PathID"].AsLong,
                        V3(bf["m_LocalPosition"]), Quat(bf["m_LocalRotation"]), V3(bf["m_LocalScale"]))
                    { Name = Go(am, inst, bf) });
                }
                catch { }
            }
        }
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i].Name.StartsWith("go:", StringComparison.Ordinal)
                && long.TryParse(nodes[i].Name.AsSpan(3), out var goId)
                && goName.TryGetValue(goId, out var n))
                nodes[i] = nodes[i] with { Name = n };
        return nodes;
    }

    private static string Go(AssetsManager am, AssetsFileInstance inst, AssetTypeValueField transform)
    {
        // defer the name lookup: record the id, resolved once all GameObjects are collected
        try { return "go:" + transform["m_GameObject"]["m_PathID"].AsLong; } catch { return "go:0"; }
    }

    /// <summary>The pure logic, testable without a bundle: per-bone paths + the snapped uprighting.
    /// <c>G_i = bindPose_i · restWorld_i</c> (row-vector) must agree across bones — a rig where it doesn't
    /// isn't rigidly related to its bind space, so no bake.</summary>
    public static SceneRig? FromScene(IReadOnlyList<SceneNode> nodes, IReadOnlyList<long> boneIds, MeshSkin skin)
    {
        if (boneIds.Count != skin.BoneCount || skin.BoneCount == 0) return null;
        var byId = new Dictionary<long, SceneNode>(nodes.Count);
        foreach (var n in nodes) byId[n.PathId] = n;
        foreach (var id in boneIds)
            if (!byId.ContainsKey(id)) return null;

        // scene rest world, memoized (row-vector: world = local · parentWorld)
        var worldCache = new Dictionary<long, Matrix4x4>();
        Matrix4x4 WorldOf(long id)
        {
            if (worldCache.TryGetValue(id, out var w)) return w;
            var x = byId[id];
            var local = Matrix4x4.CreateScale(x.Scale)
                      * Matrix4x4.CreateFromQuaternion(x.Rot)
                      * Matrix4x4.CreateTranslation(x.Pos);
            var world = x.Father == 0 || !byId.ContainsKey(x.Father) ? local : local * WorldOf(x.Father);
            return worldCache[id] = world;
        }

        // G across all bones — bake only when consistent AND an exact axis-aligned rotation. The consistency
        // gate mirrors RestBake.Snap's split: rotation entries tight, translation loose, since per-bone
        // scene-alignment noise runs a few millimetres and the snap drops it anyway.
        Matrix4x4? g0 = null;
        bool consistent = true;
        for (int i = 0; i < boneIds.Count; i++)
        {
            var g = skin.BindPoses[i] * WorldOf(boneIds[i]);
            if (g0 is null) g0 = g;
            else if (RestBake.RotationDiff(g, g0.Value) > RestBake.RotationTol || RestBake.TranslationDiff(g, g0.Value) > RestBake.TranslationTol)
            { consistent = false; break; }
        }
        var uprighting = consistent && g0 is not null ? RestBake.Snap(g0.Value) : null;

        // per-bone path: the chain suffix whose CRC32 equals the mesh's stored bone-name hash, else the
        // full chain — parenting is real either way
        var paths = new string[boneIds.Count];
        var chains = new (List<string> Segs, List<long> Ids, int Anchor)[boneIds.Count];
        for (int i = 0; i < boneIds.Count; i++)
        {
            var segs = new List<string>();
            var ids = new List<long>();
            var seen = new HashSet<long>();
            for (long cur = boneIds[i]; cur != 0 && byId.TryGetValue(cur, out var x) && seen.Add(cur); cur = x.Father)
            {
                segs.Add(x.Name);
                ids.Add(cur);
            }
            segs.Reverse(); ids.Reverse();

            int anchor = 0;
            string? path = null;
            for (int k = 0; k < segs.Count; k++)
            {
                var cand = string.Join("/", segs.Skip(k));
                if (BoneTable.Hash(cand) == skin.BoneHashes[i]) { path = cand; anchor = k; break; }
            }
            paths[i] = path ?? string.Join("/", segs);
            chains[i] = (segs, ids, anchor);
        }

        // connector rests: every proper prefix of a bone path that isn't itself a skinned bone gets its true
        // rest world, normalized to BIND space so the export can compose it with any uprighting. Only
        // derivable when the bind→scene relation is consistent, i.e. G is well-defined.
        var connectors = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        if (consistent && g0 is not null && Matrix4x4.Invert(g0.Value, out var gInv))
        {
            var bonePathSet = new HashSet<string>(paths, StringComparer.Ordinal);
            foreach (var (segs, ids, anchor) in chains)
                for (int j = anchor; j < segs.Count - 1; j++)   // proper prefixes only — the leaf IS the bone
                {
                    var key = string.Join("/", segs.Skip(anchor).Take(j - anchor + 1));
                    if (bonePathSet.Contains(key) || connectors.ContainsKey(key)) continue;
                    connectors[key] = WorldOf(ids[j]) * gInv;
                }
        }

        var restWorlds = new Matrix4x4[boneIds.Count];
        for (int i = 0; i < boneIds.Count; i++) restWorlds[i] = WorldOf(boneIds[i]);

        return new SceneRig
        {
            BonePaths = paths, Uprighting = uprighting, ConnectorRests = connectors,
            MeasuredRest = consistent ? g0 : null, BoneRestWorlds = restWorlds,
        };
    }

    private static Vector3 V3(AssetTypeValueField f) => new(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat);
    private static Quaternion Quat(AssetTypeValueField f) => new(f["x"].AsFloat, f["y"].AsFloat, f["z"].AsFloat, f["w"].AsFloat);
}
