using System.Numerics;

namespace Remold.Core.Mesh;

/// <summary>
/// The Unity ⇄ glTF coordinate convention. GFL2 meshes are Unity LEFT-handed Y-up; glTF is RIGHT-handed
/// Y-up, so verbatim coordinates leave the model MIRRORED left/right in Blender and modders "fix" the
/// mirror and break their mesh.
///
/// Negating X on every directional channel cancels the mirror. That single-axis reflection inverts
/// triangle winding, so <see cref="ReverseWinding"/> flips each triangle back to glTF's CCW-front. Do
/// NOT rotate for Z-up — Blender's glTF importer applies that itself. Unity puts v = 0 at the BOTTOM,
/// glTF at the TOP, so <see cref="TexCoord"/> flips V; that flip and <see cref="Tangent"/>'s W negation
/// are one decision in two places.
///
/// Every operation here is an INVOLUTION, so the SAME functions convert both directions and an unedited
/// export→import round-trip recovers the original Unity arrays byte-for-byte.
///
/// Geometry channels only; the rigged export carries the same reflection onto each bone's rest-world
/// transform via <see cref="Reflect"/>.
/// </summary>
public static class AxisConvention
{
    /// <summary>The X-axis reflection as a 4×4 (System.Numerics, row-vector). Self-inverse.</summary>
    public static readonly Matrix4x4 ReflectX = new(
        -1, 0, 0, 0,
         0, 1, 0, 0,
         0, 0, 1, 0,
         0, 0, 0, 1);

    /// <summary>Carry the reflection onto a whole transform (a bone's rest-world matrix): the
    /// conjugation <c>M·B·M</c> with <c>M</c> = <see cref="ReflectX"/>. Applying the det −1 reflection
    /// twice keeps a rigid input rigid, so the result is a clean bone rest with no mirror or scale.
    /// Self-inverse.</summary>
    public static Matrix4x4 Reflect(Matrix4x4 m) => ReflectX * m * ReflectX;

    /// <summary>Position between Unity and glTF space (negate X). Self-inverse.</summary>
    public static Vector3 Position(Vector3 p) => new(-p.X, p.Y, p.Z);

    /// <summary>Normal between Unity and glTF space (negate X). Self-inverse.</summary>
    public static Vector3 Normal(Vector3 n) => new(-n.X, n.Y, n.Z);

    /// <summary>Convert a tangent (xyz direction + w handedness): negate X and W. Self-inverse.
    /// <para>The W negation is COUPLED to <see cref="TexCoord"/>: Blender's importer flips V a second
    /// time, so the frame it derives runs against the exported UVs and the stored handedness must be
    /// inverted to meet it. All-or-nothing — either alone inverts normal-mapped relief.</para></summary>
    public static Vector4 Tangent(Vector4 t) => new(-t.X, t.Y, t.Z, -t.W);

    /// <summary>Convert a UV between Unity's bottom-up V and glTF's top-down V. Self-inverse exactly for
    /// the half-precision UVs a game mesh carries, whose <c>1 − v</c> is representable in float32 — that
    /// is what returns the UV channel bit-for-bit on an unedited round-trip.</summary>
    public static Vector2 TexCoord(Vector2 uv) => new(uv.X, 1f - uv.Y);

    /// <summary>
    /// Swap each triangle's 2nd and 3rd index so faces stay front-facing after the single-axis
    /// reflection. Expects a multiple of 3; returns a new array. Self-inverse.
    /// </summary>
    public static int[] ReverseWinding(int[] triangles)
    {
        var r = (int[])triangles.Clone();
        for (int i = 0; i + 3 <= r.Length; i += 3)
            (r[i + 1], r[i + 2]) = (r[i + 2], r[i + 1]);
        return r;
    }
}
