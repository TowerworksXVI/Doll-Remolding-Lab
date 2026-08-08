using System;
using System.Collections.Generic;
using System.Numerics;

namespace Remold.Core.Mesh;

/// <summary>
/// The scene-rest "uprighting" bake for prefab-shipped bodies (enemies, NPC forms, props).
///
/// <para>Why: many prefab-shipped bodies store vertex data face-down and the prefab's scene rest pose
/// applies a rigid uprighting <c>G</c> on top of bind space — measured at exactly −90° about X, zero
/// translation, consistent across every bone to 1e-5. The renderer shows <c>G·v</c>, so an export of
/// raw <c>v</c> is 90° wrong in Blender. Character and upright prefab bodies have <c>G = identity</c>.</para>
///
/// <para><see cref="Snap"/> reduces a measured <c>G</c> to an EXACT axis-aligned rotation when it is
/// one, making <see cref="Apply"/>/<see cref="Unapply"/> a bit-exact involution, so the unedited
/// round-trip still recovers the original Unity floats byte-for-byte. A <c>G</c> that doesn't snap is
/// NOT baked; the mesh exports in bind space.</para>
///
/// <para>Workspace glbs are in scene-rest space and every in-app comparison is
/// workspace-vs-workspace. The ONE Unity-space boundary is the mod build: a union stated in
/// scene-rest space takes the donor payload's floats as exported, and one stated in the anchor's
/// own bind space <see cref="Unapply"/>s the target's recorded <c>baked_rest</c> off the payload
/// first (the verdict is the anchor's measured rest — see <c>SwapCompile.TrySceneDelta</c>).</para>
/// </summary>
public static class RestBake
{
    /// <summary>How far a measured transform's ROTATION entries may sit from the exact ones and still be
    /// read as that rotation. The tight half of the split — rotation is what gets baked, so it has to be
    /// right. Every caller measuring against <see cref="RotationDiff"/> gates on this one value, or two
    /// stages of one build would disagree about whether a part's space relates rigidly to another's.</summary>
    public const float RotationTol = 1e-3f;

    /// <summary>The loose half of the split, for <see cref="TranslationDiff"/>: translation is DROPPED
    /// rather than baked, so it only has to be ignorable. Real faces carry ~1mm of Y noise the tight gate
    /// would reject, while a real placement offset (a weapon mount at ~0.376) stays well above this and
    /// still refuses the bake.</summary>
    public const float TranslationTol = 1e-2f;

    /// <summary>
    /// Snap a measured bind→scene transform to an exact axis-aligned rotation. Null (caller skips the
    /// bake) when it isn't one — significant translation, non-quarter-turn rotation, scale — or when it
    /// snaps to identity.
    /// </summary>
    public static Matrix4x4? Snap(Matrix4x4 g, float tol = RotationTol, float translationTol = TranslationTol)
    {
        // translation must be ignorable (it's dropped, not baked); then the projective row
        if (MathF.Abs(g.M41) > translationTol || MathF.Abs(g.M42) > translationTol || MathF.Abs(g.M43) > translationTol) return null;
        if (MathF.Abs(g.M14) > tol || MathF.Abs(g.M24) > tol || MathF.Abs(g.M34) > tol || MathF.Abs(g.M44 - 1) > tol) return null;

        // each linear entry must sit on {-1, 0, 1}
        Span<float> src = stackalloc float[9] { g.M11, g.M12, g.M13, g.M21, g.M22, g.M23, g.M31, g.M32, g.M33 };
        Span<float> snap = stackalloc float[9];
        for (int i = 0; i < 9; i++)
        {
            float r = MathF.Round(src[i]);
            if (MathF.Abs(r) > 1f || MathF.Abs(src[i] - r) > tol) return null;
            snap[i] = r;
        }

        // must be a proper rotation: one ±1 per row and per column, det +1
        for (int r = 0; r < 3; r++)
            if (MathF.Abs(snap[r * 3]) + MathF.Abs(snap[r * 3 + 1]) + MathF.Abs(snap[r * 3 + 2]) != 1f) return null;
        for (int c = 0; c < 3; c++)
            if (MathF.Abs(snap[c]) + MathF.Abs(snap[3 + c]) + MathF.Abs(snap[6 + c]) != 1f) return null;
        float det =
            snap[0] * (snap[4] * snap[8] - snap[5] * snap[7]) -
            snap[1] * (snap[3] * snap[8] - snap[5] * snap[6]) +
            snap[2] * (snap[3] * snap[7] - snap[4] * snap[6]);
        if (det != 1f) return null;

        var m = new Matrix4x4(
            snap[0], snap[1], snap[2], 0,
            snap[3], snap[4], snap[5], 0,
            snap[6], snap[7], snap[8], 0,
            0, 0, 0, 1);
        return m.IsIdentity ? null : m;
    }

    /// <summary>Largest absolute difference between two transforms' LINEAR entries — the tight half of
    /// <see cref="Snap"/>'s tolerance split, for callers deciding whether a measured transform is
    /// consistent enough to snap at all.</summary>
    public static float RotationDiff(Matrix4x4 a, Matrix4x4 b)
    {
        var d = a - b;
        float m = 0;
        Span<float> e = stackalloc float[9] { d.M11, d.M12, d.M13, d.M21, d.M22, d.M23, d.M31, d.M32, d.M33 };
        foreach (var v in e) m = MathF.Max(m, MathF.Abs(v));
        return m;
    }

    /// <summary>Largest absolute difference between two transforms' TRANSLATIONS — the loose half of
    /// <see cref="Snap"/>'s tolerance split.</summary>
    public static float TranslationDiff(Matrix4x4 a, Matrix4x4 b)
    {
        var d = a - b;
        return MathF.Max(MathF.Abs(d.M41), MathF.Max(MathF.Abs(d.M42), MathF.Abs(d.M43)));
    }

    /// <summary>Bake: rotate every directional channel (Vertex, Normal, Tangent.xyz) by the snapped
    /// rotation. Returns a NEW mesh; untouched channels pass through by reference.</summary>
    public static UnityMesh Apply(UnityMesh mesh, Matrix4x4 snapped) => Transform(mesh, snapped);

    /// <summary>Un-bake: the inverse of <see cref="Apply"/> — a pure rotation's inverse is its
    /// transpose, still all-integer, so unapply(apply(m)) is bit-identical to m.</summary>
    public static UnityMesh Unapply(UnityMesh mesh, Matrix4x4 snapped) => Transform(mesh, Matrix4x4.Transpose(snapped));

    private static UnityMesh Transform(UnityMesh mesh, Matrix4x4 rot)
    {
        var channels = new Dictionary<string, float[]>(mesh.Channels);
        // Position is always stride 3 (RequireStride3Positions). Normal is 3 OR 4 — a packed 0x34 Normal
        // stores xyz + a zero pad — so key on the stored dim, not a hardcoded 3, or a packed-normal mesh
        // loses its shading through the bake.
        if (channels.TryGetValue("Vertex", out var pos) && mesh.Dims.GetValueOrDefault("Vertex") == 3)
            channels["Vertex"] = Rotate(pos, 3, rot);
        if (channels.TryGetValue("Normal", out var nrm))
        {
            int nd = mesh.Dims.GetValueOrDefault("Normal");
            if (nd is 3 or 4) channels["Normal"] = Rotate(nrm, nd, rot);   // xyz rotates, 4th (pad) untouched
        }
        if (channels.TryGetValue("Tangent", out var tan) && mesh.Dims.GetValueOrDefault("Tangent") == 4)
            channels["Tangent"] = Rotate(tan, 4, rot);   // xyz rotates, w (handedness) untouched

        return new UnityMesh
        {
            Name = mesh.Name,
            VertexCount = mesh.VertexCount,
            Channels = channels,
            Dims = new Dictionary<string, int>(mesh.Dims),
            Submeshes = mesh.Submeshes,
        };
    }

    private static float[] Rotate(float[] data, int dim, Matrix4x4 rot)
    {
        var r = new float[data.Length];
        for (int i = 0; i + dim <= data.Length; i += dim)
        {
            float x = data[i], y = data[i + 1], z = data[i + 2];
            // row-vector v·M with integer entries: each output is a signed pick of one input component
            r[i] = x * rot.M11 + y * rot.M21 + z * rot.M31;
            r[i + 1] = x * rot.M12 + y * rot.M22 + z * rot.M32;
            r[i + 2] = x * rot.M13 + y * rot.M23 + z * rot.M33;
            if (dim == 4) r[i + 3] = data[i + 3];
        }
        return r;
    }

    // ---- ProjectTarget serialization (baked_rest: 16 floats, row-major System.Numerics) ----

    public static List<float> ToList(Matrix4x4 m) => new()
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };

    /// <summary>Null (no bake) for a null/malformed/identity list — tolerant of a hand-edited project.
    /// Also null for a matrix <see cref="Snap"/> refuses: <see cref="Unapply"/> inverts by transpose, which
    /// is the inverse only of the axis-aligned rotations the bake records, so anything else would ship
    /// skewed geometry. <paramref name="refused"/> separates that case from "nothing recorded", so every
    /// caller has to decide what to say about a record it could not honour.</summary>
    public static Matrix4x4? FromList(IReadOnlyList<float>? v, out bool refused)
    {
        refused = false;
        if (v is null || v.Count != 16) return null;
        var m = new Matrix4x4(
            v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7],
            v[8], v[9], v[10], v[11], v[12], v[13], v[14], v[15]);
        if (m.IsIdentity) return null;
        if (Snap(m) is null) { refused = true; return null; }
        return m;
    }
}
