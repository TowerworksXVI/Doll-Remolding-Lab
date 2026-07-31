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

    /// <summary>Unity Matrix4x4 field (<c>e{row}{col}</c>, column-vector) → System.Numerics row-vector,
    /// i.e. the transpose.</summary>
    public static Matrix4x4 ToNumerics(AssetTypeValueField m) => new(
        m["e00"].AsFloat, m["e10"].AsFloat, m["e20"].AsFloat, m["e30"].AsFloat,
        m["e01"].AsFloat, m["e11"].AsFloat, m["e21"].AsFloat, m["e31"].AsFloat,
        m["e02"].AsFloat, m["e12"].AsFloat, m["e22"].AsFloat, m["e32"].AsFloat,
        m["e03"].AsFloat, m["e13"].AsFloat, m["e23"].AsFloat, m["e33"].AsFloat);
}
