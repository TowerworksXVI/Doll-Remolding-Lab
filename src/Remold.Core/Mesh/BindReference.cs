using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MathNet.Numerics.LinearAlgebra;

namespace Remold.Core.Mesh;

/// <summary>
/// The ONE bind pose a replacement is posed under for each bone, and the constant that carries any mesh's
/// recovered rows onto it.
///
/// <para>A pooled replacement is posed with rows recovered off several of an item's meshes, and the game lets
/// each mesh bind a bone its own way: a whole mesh can sit in another space than its neighbours (an offset
/// outfit variant, a mask modelled away from the head), and a helper bone can be bound slightly off in one part
/// and on the skeleton in the next. A recovered row is the bind-included skin matrix <c>Bind_mesh · World</c>
/// (row-vector), so restating it under another bind is <c>C = Bind_reference · inverse(Bind_mesh)</c> on the
/// LEFT: exact for any invertible difference, and linear in the row, which is what lets a build fold it into
/// the mesh's recovery operator.</para>
///
/// <para>The reference makes the replacement move as the geometry it was modelled against. A bone the
/// replaced part weights takes that part's own bind, so an unedited send-back skins exactly as the original
/// part. Any other bone takes the bind of the first source that weights it, carried into the replaced part's
/// mesh space by the two parts' placements (<see cref="Skeleton.SceneRig.Placement"/>), so geometry painted
/// onto it moves exactly as that part's own geometry does. A part the rig's rest pose shrinks
/// (<see cref="Hidden"/>) is one the game keeps out of sight until an animation brings it on, so its rest
/// placement says nothing about where it draws: the Blender export opens it centred at full size instead, and
/// a bone crossing to or from its space is carried by that display placement (<see cref="Part.Display"/>),
/// where the modder saw it. Geometry authored before that export existed is posed as it was then: a bone
/// crossing to or from a hidden part keeps the bind it is stated under (the <c>carryHidden</c> flag of
/// <see cref="For"/>). A part scaled by some other amount (a few enemy and summon props) keeps that rule
/// whatever the flag says. Two parts driven by
/// different skeletons (a summoned creature beside its summoner) are animated apart, so their rest placements
/// relate them only where the bones both weight agree under that relation, or, sharing no bone, where the rig
/// places the two together; a bone crossing between two that don't has no reference. The Blender export places
/// joints by this same rule.</para>
/// </summary>
public static class BindReference
{
    /// <summary>One mesh as the rule reads it: its bone table and binds in table order (row-vector), the bones
    /// it POSES (nonzero summed vertex weight), where its mesh space sits in its rig, read only when a bone
    /// needs carrying between two spaces, and the skeleton that drives it (the saved pose asset its rig
    /// names; null when it names none, and such a part is set against no other skeleton).
    ///
    /// <para><paramref name="Display"/> is where the Blender export shows the part's mesh when the part is
    /// <see cref="Hidden"/>: its display placement <c>Q = U · T(−c)</c> (<see cref="Workbench.HiddenPart"/>),
    /// or the reason it can't be said. It is read only for a hidden part, and only when a bone needs carrying
    /// under it; a part given none has no display placement to be carried by.</para></summary>
    public sealed record Part(string Mesh, IReadOnlyList<uint> Hashes, IReadOnlyList<Matrix4x4> Binds,
        IReadOnlySet<uint> Posed, Lazy<(Matrix4x4? Placement, string? Problem)> Space,
        Lazy<string?>? Skeleton = null, Lazy<(Matrix4x4? Placement, string? Problem)>? Display = null);

    /// <summary>The settled reference, in the target's own mesh space. <paramref name="Unplaced"/> holds the
    /// bones only sources nothing places against the target weight, each with the source, the reason and
    /// which of <see cref="Unplacement"/> it is; they have no reference, and weight on one cannot be
    /// built.</summary>
    public sealed record Resolution(IReadOnlyDictionary<uint, Matrix4x4> Reference,
        IReadOnlyDictionary<uint, (string Mesh, string Problem, Unplacement Cause)> Unplaced);

    /// <summary>Why a source's bones have no reference. A caller telling the modder what to change reads it
    /// here rather than from the problem's wording.</summary>
    public enum Unplacement
    {
        /// <summary>The target's own placement can't be read, so no other part's bone can be carried into
        /// its space.</summary>
        Target,
        /// <summary>The source's placement can't be read or doesn't invert.</summary>
        Source,
        /// <summary>The two are driven by different skeletons and nothing they weight relates them.</summary>
        Skeletons,
    }

    /// <summary>The reference for a replacement of <paramref name="target"/> whose other bones come from
    /// <paramref name="sources"/>, taken in the order given (<see cref="SourceOrder"/>).
    /// <paramref name="carryHidden"/> says which relation the geometry was authored under: true carries a
    /// bone to or from a <see cref="Hidden"/> part by that part's display placement, as the Blender export
    /// shows it today; false keeps such a bone's bind as it is stated, as geometry returned before that export
    /// existed was built.</summary>
    public static Resolution For(Part target, IReadOnlyList<Part> sources, bool carryHidden)
    {
        var reference = new Dictionary<uint, Matrix4x4>();
        for (int i = 0; i < target.Hashes.Count && i < target.Binds.Count; i++)
            if (target.Posed.Contains(target.Hashes[i])) reference.TryAdd(target.Hashes[i], target.Binds[i]);

        var unplaced = new Dictionary<uint, (string, string, Unplacement)>();
        foreach (var source in sources)
        {
            if (string.Equals(source.Mesh, target.Mesh, StringComparison.OrdinalIgnoreCase)) continue;
            Matrix4x4? carry = null;
            (string Text, Unplacement Cause)? problem = null;
            bool related = false;
            for (int i = 0; i < source.Hashes.Count && i < source.Binds.Count; i++)
            {
                uint h = source.Hashes[i];
                if (reference.ContainsKey(h) || !source.Posed.Contains(h)) continue;
                if (!related)
                {
                    carry = Carry(target, source, carryHidden, out problem);
                    related = true;
                }
                if (problem is { } why)
                {
                    unplaced.TryAdd(h, (source.Mesh, why.Text, why.Cause));
                    continue;
                }
                // Accepted: a bone not carried (the two share a space, or one is scaled) is stated here in the
                // source's own space and taken as the target's, the space the replacement is posed in. That is
                // right unless the source was stored rotated against the target. Measured absent for scaled
                // parts (0 of 6,397 pairs); leaving the bone out of the reference instead would let two meshes
                // state it differently with nothing converting either.
                reference[h] = carry is { } m ? m * source.Binds[i] : source.Binds[i];
                unplaced.Remove(h);
            }
        }
        return new Resolution(reference, unplaced);
    }

    /// <summary>The order sources are asked in: the replaced part's pool candidates in roster order, then the
    /// members of its coverage group in roster order, each once. The build and the export both take it from
    /// here, so a bone two sources weight is placed from the same one on both sides.</summary>
    public static IReadOnlyList<string> SourceOrder(IEnumerable<string> candidates, IEnumerable<string> groupMembers)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in candidates.Concat(groupMembers))
            if (seen.Add(mesh)) order.Add(mesh);
        return order;
    }

    /// <summary>What carries a bind stated in <paramref name="source"/>'s mesh space into
    /// <paramref name="target"/>'s: <c>P_target · inverse(P_source)</c>, or null when the two share a space
    /// (their placements agree within the width binds are compared at) or either placement scales its mesh
    /// (<see cref="Scaled"/>). <c>P</c> is a part's placement <c>G</c>, or, with
    /// <paramref name="carryHidden"/>, a hidden part's display placement <c>Q</c>, which scales nothing.
    /// Between two skeletons it holds only where every bone both parts weight agrees under it, or, where they
    /// weight no bone in common, where their placements coincide; otherwise nothing places the source against
    /// the target.</summary>
    static Matrix4x4? Carry(Part target, Part source, bool carryHidden, out (string Text, Unplacement Cause)? problem)
    {
        problem = null;
        var (gt, targetProblem) = Placement(target, carryHidden);
        var (gs, sourceProblem) = Placement(source, carryHidden);
        if (gt is not { } t) { problem = ($"'{target.Mesh}': {targetProblem}", Unplacement.Target); return null; }
        if (gs is not { } s) { problem = ($"'{source.Mesh}': {sourceProblem}", Unplacement.Source); return null; }
        Matrix4x4? carry = null;
        if (!SameBind(t, s) && !Scaled(t) && !Scaled(s))
        {
            if (!Matrix4x4.Invert(s, out var sInv))
            {
                problem = ($"'{source.Mesh}': its placement doesn't invert", Unplacement.Source);
                return null;
            }
            carry = t * sInv;
        }
        if (target.Skeleton?.Value is { } targetSkeleton && source.Skeleton?.Value is { } sourceSkeleton
            && !string.Equals(targetSkeleton, sourceSkeleton, StringComparison.Ordinal)
            && !(SharedWeight(target, source, carry, out bool agree) ? agree : SameBind(t, s)))
        {
            problem = ($"'{source.Mesh}' is driven by another skeleton than '{target.Mesh}', and the bones both "
                + "weight don't show where it sits relative to it", Unplacement.Skeletons);
            return null;
        }
        return carry;
    }

    /// <summary>The placement a part is carried by: its rig placement, or where <paramref name="carryHidden"/>
    /// asks and the part is <see cref="Hidden"/>, its display placement, or the reason neither can be
    /// said.</summary>
    static (Matrix4x4? Placement, string? Problem) Placement(Part part, bool carryHidden)
    {
        var space = part.Space.Value;
        if (!carryHidden || space.Placement is not { } g || !Hidden(g)) return space;
        return part.Display?.Value ?? (null, "its geometry can't be read to centre it");
    }

    /// <summary>Whether the two parts weight any bone in common, and if so whether <paramref name="carry"/>
    /// (null: none) states every such bone where the target binds it, within the width binds are compared
    /// at.</summary>
    static bool SharedWeight(Part target, Part source, Matrix4x4? carry, out bool agree)
    {
        bool shared = false;
        agree = true;
        for (int i = 0; i < source.Hashes.Count && i < source.Binds.Count; i++)
        {
            uint h = source.Hashes[i];
            if (!source.Posed.Contains(h) || !target.Posed.Contains(h)) continue;
            int j = -1;
            for (int k = 0; k < target.Hashes.Count && j < 0; k++) if (target.Hashes[k] == h) j = k;
            if (j < 0 || j >= target.Binds.Count) continue;
            shared = true;
            if (!SameBind(carry is { } c ? c * source.Binds[i] : source.Binds[i], target.Binds[j])) agree = false;
        }
        return shared;
    }

    /// <summary>Whether a placement shrinks its part out of sight: every axis's length
    /// <see cref="HiddenScale"/> or less. The game hides a part until an animation shows it by shrinking it,
    /// to 1% or 0.1% across the corpus (a prop an animation moves into a hand, an alternate chest shape);
    /// every other placement's axes are 0.6 or longer, so the line sits well clear of both.</summary>
    public static bool Hidden(Matrix4x4 g) =>
        new Vector3(g.M11, g.M12, g.M13).Length() <= HiddenScale
        && new Vector3(g.M21, g.M22, g.M23).Length() <= HiddenScale
        && new Vector3(g.M31, g.M32, g.M33).Length() <= HiddenScale;

    /// <summary>The longest axis a <see cref="Hidden"/> placement leaves its part.</summary>
    public const float HiddenScale = 0.1f;

    /// <summary>Whether a placement scales its mesh: any axis's length off 1 by more than
    /// <see cref="BindSpace.MaxBindDisagreement"/>. The game hides a part until an animation shows it by
    /// shrinking it (<see cref="Hidden"/>), and a few enemy and summon props carry a scale of their own (0.6
    /// to 4); a scaled placement is not carried through, except that a hidden part is carried by its display
    /// placement where the relation asks for that. Every other placement holds scale 1 to float rounding
    /// (within 7e-7).</summary>
    static bool Scaled(Matrix4x4 g) =>
        Math.Abs(new Vector3(g.M11, g.M12, g.M13).Length() - 1) > BindSpace.MaxBindDisagreement
        || Math.Abs(new Vector3(g.M21, g.M22, g.M23).Length() - 1) > BindSpace.MaxBindDisagreement
        || Math.Abs(new Vector3(g.M31, g.M32, g.M33).Length() - 1) > BindSpace.MaxBindDisagreement;

    /// <summary>The constant carrying rows recovered under <paramref name="own"/> onto
    /// <paramref name="reference"/>: <c>C = reference · inverse(own)</c>, 16 doubles row-major, to LEFT-multiply
    /// a recovered skin matrix. Null when the two are one bind within <see cref="BindSpace.MaxBindDisagreement"/>,
    /// the width two statements of a bind have always been read as equal at, so a pool that needs no
    /// conversion ships the bytes it always has. Computed in double: the product feeds operator coefficients
    /// whose rounding is part of what ships.</summary>
    public static double[]? Constant(Matrix4x4 reference, Matrix4x4 own)
    {
        if (SameBind(reference, own)) return null;
        var r = ToDense(reference);
        var o = ToDense(own);
        if (Math.Abs(o.Determinant()) < 1e-30)
            throw new InvalidOperationException("a bind pose that won't invert states no bone space");
        var c = r * o.Inverse();
        var flat = new double[16];
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) flat[i * 4 + j] = c[i, j];
        return flat;
    }

    /// <summary>Two statements of one bind, at the width the pooled swap has always compared them.
    ///
    /// <para>Accepted: a mesh is compared with the reference, not with a neighbouring part, so two meshes each
    /// within this width of the reference can sit up to twice it apart and both ship unconverted, and a mesh
    /// just past it ships a corrected copy that draws the same. Across the corpus no shared-bone difference
    /// falls between 1e-5 and 1e-4, so no mod's bytes turn on it.</para></summary>
    public static bool SameBind(Matrix4x4 a, Matrix4x4 b)
    {
        float[] x = Flat(a), y = Flat(b);
        for (int i = 0; i < 16; i++)
            if (Math.Abs(x[i] - y[i]) > BindSpace.MaxBindDisagreement) return false;
        return true;
    }

    static float[] Flat(Matrix4x4 m) => new[]
    {
        m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
    };

    static Matrix<double> ToDense(Matrix4x4 m)
    {
        var f = Flat(m);
        var d = Matrix<double>.Build.Dense(4, 4);
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) d[i, j] = f[i * 4 + j];
        return d;
    }
}
