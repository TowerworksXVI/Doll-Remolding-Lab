using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsTools.NET;

namespace Remold.Core.Mesh;

/// <summary>
/// A skinned Mesh's per-BONE binding: <c>m_BoneNameHashes</c> (CRC32 of the bone path — see
/// <see cref="Skeleton.BoneTable"/>) paired with <c>m_BindPose</c>. The per-VERTEX side
/// (BlendIndices/BlendWeight, indexing into this bone list) lives on <see cref="UnityMesh"/> as
/// ordinary channels. Rigid props decode to an empty skin (<see cref="IsSkinned"/> = false).
///
/// <para>Unity stores a Matrix4x4 as <c>e{row}{col}</c> in the <i>column-vector</i> (M·v) convention.
/// <see cref="BindPoses"/> returns them TRANSPOSED into System.Numerics' row-vector convention, so
/// <c>Vector3.Transform(v, bindPose)</c> reproduces Unity's <c>M·v</c>, translation lands in
/// <c>M41..M43</c>, and invert/multiply compose as expected. A bone's rest-pose world transform is
/// <c>Invert(bindPose)</c>; bind poses are rigid corpus-wide, so no orthonormalization.</para>
/// </summary>
public sealed class MeshSkin
{
    /// <summary>Per-bone CRC32 name hash, in the mesh's bone order (the order BlendIndices index into).</summary>
    public IReadOnlyList<uint> BoneHashes { get; init; } = Array.Empty<uint>();

    /// <summary>Per-bone bind pose, System.Numerics (row-vector) convention; same order as <see cref="BoneHashes"/>.</summary>
    public IReadOnlyList<Matrix4x4> BindPoses { get; init; } = Array.Empty<Matrix4x4>();

    public int BoneCount => BoneHashes.Count;

    /// <summary>True when the mesh carries a usable skeleton binding (bones present, counts agree).</summary>
    public bool IsSkinned => BoneCount > 0 && BindPoses.Count == BoneCount;

    /// <summary>Read <c>m_BoneNameHashes</c> + <c>m_BindPose</c> off a Mesh type-tree field; either
    /// absent (rigid/static mesh) yields an empty skin.</summary>
    public static MeshSkin Decode(AssetTypeValueField mesh)
    {
        var hashes = ReadHashes(mesh);
        var binds = ReadBindPoses(mesh);
        return new MeshSkin { BoneHashes = hashes, BindPoses = binds };
    }

    private static List<uint> ReadHashes(AssetTypeValueField mesh)
    {
        try { return mesh["m_BoneNameHashes"]["Array"].Children.Select(c => c.AsUInt).ToList(); }
        catch { return new List<uint>(); }
    }

    private static List<Matrix4x4> ReadBindPoses(AssetTypeValueField mesh)
    {
        try
        {
            return mesh["m_BindPose"]["Array"].Children.Select(ToNumerics).ToList();
        }
        catch { return new List<Matrix4x4>(); }
    }

    /// <summary>
    /// The pair reduced to the bones <paramref name="mesh"/> ACTUALLY RIDES: every bone carrying non-zero
    /// vertex weight, in its original bone order, with the mesh's <c>BlendIndices</c> remapped onto the
    /// shortened list. Returns null when nothing carries weight — there is no bone list to place such a
    /// mesh by, and an empty skin would take a union export down with it.
    ///
    /// <para>A rigged glb re-read by <see cref="MeshGltf.ReadRiggedGlb"/> hands back the file's WHOLE joint
    /// list, subject tail included (<see cref="MeshGltf.ExtraBone"/>), because a weight painted onto a tail
    /// joint has to ride through. A caller placing that skin among others — the combined export, where the
    /// first part naming a bone fixes its world for every later part — must reduce it here first, or the
    /// tail's stale worlds claim bones other parts pose. A painted tail bone carries weight, so it survives
    /// this reduction and stays that part's own joint.</para>
    ///
    /// <para>The weighted-vs-not reading is the one <see cref="MeshGltf"/>'s skin writers use, missing
    /// channels and all: no <c>BlendWeight</c> = full weight on influence 0, no <c>BlendIndices</c> = every
    /// vertex on bone 0. Its type-tree twin over a game mesh field is
    /// <see cref="Migoto.StreamDump.WeightedBoneHashes"/>; they answer the same question about different
    /// storage and must not drift. An influence indexing outside the bone list names no bone in either
    /// skin, so it parks on joint 0 — <see cref="MeshGltf.ReadRiggedGlb"/> refuses such a file outright,
    /// so the production feed never carries one.</para>
    /// </summary>
    public static (UnityMesh Mesh, MeshSkin Skin)? WeightedOnly(UnityMesh mesh, MeshSkin skin)
    {
        if (!skin.IsSkinned) return null;
        float[]? bi = mesh.Has("BlendIndices") ? mesh.Channels["BlendIndices"] : null;
        int biDim = bi is not null ? mesh.Dims.GetValueOrDefault("BlendIndices", 4) : 0;
        float[]? bw = mesh.Has("BlendWeight") ? mesh.Channels["BlendWeight"] : null;
        int bwDim = bw is not null ? mesh.Dims.GetValueOrDefault("BlendWeight", 4) : 0;

        var rides = new bool[skin.BoneCount];
        for (int v = 0; v < mesh.VertexCount; v++)
            for (int d = 0; d < 4; d++)
            {
                float w = bw is not null ? (d < bwDim ? bw[v * bwDim + d] : 0) : (d == 0 ? 1 : 0);
                if (w <= 0) continue;
                int bone = bi is not null && d < biDim ? (int)MathF.Round(bi[v * biDim + d]) : 0;
                if (bone >= 0 && bone < rides.Length) rides[bone] = true;
            }

        var oldToNew = new int[skin.BoneCount];
        var keep = new List<int>();
        for (int b = 0; b < skin.BoneCount; b++)
        {
            if (rides[b]) { oldToNew[b] = keep.Count; keep.Add(b); }
            else oldToNew[b] = -1;
        }
        if (keep.Count == 0) return null;
        if (keep.Count == skin.BoneCount) return (mesh, skin);

        var reduced = new MeshSkin
        {
            BoneHashes = keep.Select(b => skin.BoneHashes[b]).ToList(),
            BindPoses = keep.Select(b => skin.BindPoses[b]).ToList(),
        };
        if (bi is null) return (mesh, reduced);

        // A dropped bone only ever sits under a ZERO weight (that is what dropped it), so parking those
        // slots on joint 0 moves no vertex — and it keeps every index inside the shortened list, which the
        // union export's local→combined remap requires.
        var remapped = (float[])bi.Clone();
        for (int k = 0; k < remapped.Length; k++)
        {
            int bone = (int)MathF.Round(remapped[k]);
            remapped[k] = bone >= 0 && bone < oldToNew.Length && oldToNew[bone] >= 0 ? oldToNew[bone] : 0;
        }
        var channels = new Dictionary<string, float[]>(mesh.Channels) { ["BlendIndices"] = remapped };
        return (new UnityMesh
        {
            Name = mesh.Name,
            VertexCount = mesh.VertexCount,
            Channels = channels,
            Dims = new Dictionary<string, int>(mesh.Dims),
            Submeshes = mesh.Submeshes,
        }, reduced);
    }

    /// <summary>Unity Matrix4x4 field (<c>e{row}{col}</c>, column-vector) → System.Numerics row-vector,
    /// i.e. the transpose.</summary>
    public static Matrix4x4 ToNumerics(AssetTypeValueField m) => new(
        m["e00"].AsFloat, m["e10"].AsFloat, m["e20"].AsFloat, m["e30"].AsFloat,
        m["e01"].AsFloat, m["e11"].AsFloat, m["e21"].AsFloat, m["e31"].AsFloat,
        m["e02"].AsFloat, m["e12"].AsFloat, m["e22"].AsFloat, m["e32"].AsFloat,
        m["e03"].AsFloat, m["e13"].AsFloat, m["e23"].AsFloat, m["e33"].AsFloat);
}
