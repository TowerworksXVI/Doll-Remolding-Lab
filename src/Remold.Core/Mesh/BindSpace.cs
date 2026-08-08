using System;
using System.Collections.Generic;
using System.Numerics;

namespace Remold.Core.Mesh;

/// <summary>
/// Bind-space reconciliation across the parts of one pooled swap. Parts of a single body can be authored
/// in bind spaces that differ by one rigid transform — a body upright, its cloth face-down — while the
/// pooled union keeps ONE bindpose and ONE palette row set per bone. Every part therefore has to be
/// expressed in a single chosen space, and the choice is the ANCHOR's: the donor payload un-bakes into
/// the anchor's space, so that is what the shared palette has to pose.
///
/// <para>Row-vector convention throughout (<see cref="MeshSkin"/> transposes Unity's storage into it).
/// For a bone a part and the reference both carry, the LEFT quotient <c>D = B_part · inv(B_ref)</c> is
/// the part→reference relation. Converting is <c>B' = inv(D)·B_part</c> for the stored bind and
/// <c>v' = v·D</c> for the geometry; the pair leaves <c>v·B</c> — the bone-space position skinning
/// consumes — unchanged, so the part poses exactly as before while sharing the reference's palette.</para>
///
/// <para>The conversion is only valid when D is the SAME on every shared bone, rigid after the snap, and
/// measured over enough shared bones to mean anything. Bone-name hashes collide across unrelated rigs, so
/// a delta that varies bone to bone, or carries a translation or a shear, is a coincidence rather than a
/// space difference — converting on it would deform the geometry. <see cref="Delta"/> returns null there
/// and the caller refuses.</para>
/// </summary>
public static class BindSpace
{
    /// <summary>How many bones a part and the reference must SHARE before a delta fitted to them may be
    /// acted on. The uniformity gate is a comparison against the seed bone, so below this a delta is
    /// unfalsifiable: the seed matches itself by construction, the snap is the only remaining test, and one
    /// coincidental signed permutation would authorize rotating a whole part's bindposes and geometry. The
    /// bones the part does NOT share are rebased on that verdict and never checked against anything. A real
    /// space difference is a shared rig and clears this many times over.</summary>
    public const int MinSharedBones = 3;

    /// <summary>How far two statements of ONE bone's bindpose may differ and still be the same bindpose.
    /// The last gate after every conversion this class can make: a pooled union keeps one bindpose per bone,
    /// so a difference past this is one no measured or corroborated rigid rotation explained, and posing
    /// geometry on it would deform it. Every consumer of that verdict — the union order, the tier scatter —
    /// gates at the same width, or a bone one stage admits the next refuses.</summary>
    public const double MaxBindDisagreement = 1e-5;

    /// <summary>The snapped part→reference delta over the bones the two share, or null when there is no
    /// conversion to make: fewer than <see cref="MinSharedBones"/> shared bones, a delta that varies across
    /// them, one that isn't an exact axis-aligned rotation, or identity (the two already share a space).
    ///
    /// <para>The uniformity gate mirrors <see cref="RestBake.Snap"/>'s tolerance split — rotation entries
    /// tight, translation loose — since the snap drops the translation anyway.</para>
    ///
    /// <para><paramref name="shared"/> must be enumerated in a caller-stable order so two builds of one
    /// pool agree on which bone seeds the delta.</para></summary>
    public static Matrix4x4? Delta(IEnumerable<(Matrix4x4 Part, Matrix4x4 Reference)> shared)
    {
        Matrix4x4? seed = null;
        int bones = 0;
        foreach (var (part, reference) in shared)
        {
            if (!Matrix4x4.Invert(reference, out var refInv)) return null;
            var d = part * refInv;
            bones++;
            if (seed is null) seed = d;
            else if (RestBake.RotationDiff(d, seed.Value) > RestBake.RotationTol
                     || RestBake.TranslationDiff(d, seed.Value) > RestBake.TranslationTol) return null;
        }
        return bones >= MinSharedBones && seed is { } m ? RestBake.Snap(m) : null;
    }

    /// <summary>One bindpose restated in the reference's space. A snapped delta is a pure rotation, so its
    /// inverse is its transpose — all-integer, which keeps every restated entry an exact copy of an
    /// original one.</summary>
    public static Matrix4x4 Rebase(Matrix4x4 bind, Matrix4x4 snappedDelta) =>
        Matrix4x4.Transpose(snappedDelta) * bind;

    // ---- the two serialized bindpose orderings -----------------------------------------------------

    /// <summary>A Unity <c>Matrix4x4f</c>'s serialized floats (declaration order <c>e00,e01,…,e33</c>,
    /// column-vector) as a row-vector matrix — the transpose, matching <see cref="MeshSkin.ToNumerics"/>.</summary>
    public static Matrix4x4 FromUnityFloats(IReadOnlyList<float> e) => new(
        e[0], e[4], e[8], e[12],
        e[1], e[5], e[9], e[13],
        e[2], e[6], e[10], e[14],
        e[3], e[7], e[11], e[15]);

    /// <summary>The inverse of <see cref="FromUnityFloats"/>: back to Unity's serialized field order.</summary>
    public static float[] ToUnityFloats(Matrix4x4 m) => new[]
    {
        m.M11, m.M21, m.M31, m.M41,
        m.M12, m.M22, m.M32, m.M42,
        m.M13, m.M23, m.M33, m.M43,
        m.M14, m.M24, m.M34, m.M44,
    };

    /// <summary>A bindpose stored row-major in row-vector convention (the mesh dump's <c>bindpose.json</c>
    /// order, the same one <see cref="RestBake.ToList"/> writes).</summary>
    public static Matrix4x4 FromRowMajor(IReadOnlyList<double> v) => new(
        (float)v[0], (float)v[1], (float)v[2], (float)v[3],
        (float)v[4], (float)v[5], (float)v[6], (float)v[7],
        (float)v[8], (float)v[9], (float)v[10], (float)v[11],
        (float)v[12], (float)v[13], (float)v[14], (float)v[15]);

    /// <summary>The inverse of <see cref="FromRowMajor"/>.</summary>
    public static double[] ToRowMajor(Matrix4x4 m) => new double[]
    {
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    };
}
