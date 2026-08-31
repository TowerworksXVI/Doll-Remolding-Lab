using System;
using System.Collections.Generic;

namespace Remold.Core.Mesh;

/// <summary>
/// Whether one part of a combined send-back carries anything to take.
///
/// <para>A send-all returns every writable part of the session whether or not the modder touched it, so
/// "it came back" is not "it changed". Taking every returned part as an edit flags parts still carrying the
/// game's mesh, and each flag costs the build a replacement pipeline for a part that needs none.</para>
/// </summary>
public static class SendBackGeometry
{
    /// <summary>Whether the mesh that came back IS the one that was handed out — geometry AND skin, since a
    /// repaint that moves no vertex is still an edit and only the weights carry it.
    ///
    /// <para><paramref name="baselineGlb"/> names the file that was handed out, and
    /// <paramref name="meshName"/> picks the part out of it. A COMBINED send needs it: the workspace glbs a
    /// combined session materializes carry geometry only, while the session it publishes is rigged, so
    /// comparing a rigged return against them reads every part of the outfit as re-skinned. The published
    /// combined embeds each part as it was last handed out — the edited geometry for a part edited earlier —
    /// so "unchanged against it" is "no NEW edit this session". Null compares against
    /// <paramref name="workspaceGlb"/> itself.</para>
    ///
    /// <para>A pair that cannot be read — either file unopenable, the name absent from either — answers false
    /// and takes the rewrite: an unanswerable question must never silently drop an edit that is really there,
    /// and the rewrite reports its own failure. Both reads are lenient, since either file can be one Blender
    /// wrote.</para></summary>
    public static bool Unchanged(MeshGltf.ParsedGlb returned, string? meshName, string workspaceGlb,
        string? baselineGlb = null)
    {
        try
        {
            var back = MeshGltf.ImportPayload(returned, meshName);
            var held = baselineGlb is null
                ? MeshGltf.ImportPayload(workspaceGlb, lenient: true)
                : MeshGltf.ImportPayload(baselineGlb, meshName, lenient: true);
            return SameContent(back, held);
        }
        catch { return false; }
    }

    /// <summary>The same question against a baseline the caller has ALREADY parsed. A combined send asks it
    /// once per part against ONE handed-out file, and re-reading that file per part is the whole combined glb
    /// — every part's geometry and every texture in it — read once for each part it carries. Both sides here
    /// are parsed once and read many times, which is what <see cref="MeshGltf.ParsedGlb"/> exists for.
    ///
    /// <para>Same contract as the path form otherwise: a pair that cannot be read answers false and takes the
    /// rewrite.</para></summary>
    public static bool Unchanged(MeshGltf.ParsedGlb returned, string? meshName, MeshGltf.ParsedGlb baseline)
    {
        try
        {
            return SameContent(MeshGltf.ImportPayload(returned, meshName),
                MeshGltf.ImportPayload(baseline, meshName));
        }
        catch { return false; }
    }

    /// <summary>Whether two payloads carry the same mesh CONTENT: the same surface, the same shading data on
    /// it, and the same skin holding it to the same bones.
    ///
    /// <para>The comparison walks TRIANGLE CORNERS, not the vertex buffer, because a returned part's vertex
    /// buffer is not the one that left. A glTF re-export splits a vertex into one copy per distinct
    /// UV/normal it carries, so an untouched part comes back with more vertices in a different order while
    /// every corner of every triangle still names the same position, normal, UV and weights. Reading the
    /// buffers side by side calls that a new mesh; reading the corners calls it what it is. The cost is that
    /// a pure re-weld or re-split — which changes no corner — is invisible here, and the part is left alone.
    /// Bit-exact identity is a different question, and not the one this comparison answers.</para>
    ///
    /// <para>Corners are paired IN ORDER, so a returned file whose faces, submesh contents, or triangle
    /// windings came back reordered reads as changed and is taken. That is the safe direction — the
    /// alternative would drop an edit — and it is also the measured transport: Blender writes corners back
    /// in the order it was handed them, so an untouched part pairs cleanly. Winding is semantic here (the
    /// compile ships the index buffer), which is the other reason order must count.</para>
    ///
    /// <para>Tangents are deliberately out: they are derived from the positions and every transported UV set
    /// already compared, and
    /// the transport recomputes them per split copy, so comparing them would report an edit on every seam of
    /// every untouched part. Vertex Color is out for the same reason it never travels — Blender does not
    /// carry it.</para></summary>
    internal static bool SameContent(MeshApply.Payload a, MeshApply.Payload b)
    {
        if (a.HasSkin != b.HasSkin) return false;
        if (!a.Has("Vertex") || !b.Has("Vertex")) return false;
        if (a.Has("Normal") != b.Has("Normal")) return false;
        // UV0 keeps the classifier's shipped symmetric presence rule. Higher sets are baseline-authoritative:
        // every transported baseline set must come back, while a Blender-created layer beyond that prefix is
        // ignored (and named by the return preparation) rather than making the part look edited.
        if (a.Has("TexCoord0") != b.Has("TexCoord0")) return false;
        int uvSets = MeshGltf.TransportedTexCoordCount(b.Mesh);
        for (int i = 1; i < uvSets; i++)
            if (!a.Has($"TexCoord{i}")) return false;
        if (a.Submeshes.Count != b.Submeshes.Count) return false;
        int corners = 0;
        for (int s = 0; s < a.Submeshes.Count; s++)
        {
            if (a.Submeshes[s].Length != b.Submeshes[s].Length) return false;
            corners += a.Submeshes[s].Length;
        }
        // No corners on either side is nothing compared, and a comparison that walked nothing must not
        // answer "the same": the safe direction takes the part rather than dropping an edit it never read.
        if (corners == 0) return false;

        // an influence outside its joint list is not a skin this can compare
        if (Reader.For(a, uvSets) is not { } ra || Reader.For(b, uvSets) is not { } rb) return false;

        Span<(uint Bone, float Weight)> wa = stackalloc (uint, float)[4];
        Span<(uint Bone, float Weight)> wb = stackalloc (uint, float)[4];
        for (int s = 0; s < a.Submeshes.Count; s++)
        {
            var ia = a.Submeshes[s];
            var ib = b.Submeshes[s];
            for (int k = 0; k < ia.Length; k++)
            {
                int va = ia[k], vb = ib[k];
                if (va < 0 || va >= ra.Count || vb < 0 || vb >= rb.Count) return false;
                if (!SameCorner(ra, va, rb, vb, wa, wb)) return false;
            }
        }
        return true;
    }

    /// <summary>How far a POSITION or a UV may move and still be the same value across the round trip. Every
    /// float a re-export writes is re-quantized, so a part nobody touched comes back with noise on both
    /// channels, and the tolerance has to clear it — while staying far enough below the smallest edit a
    /// modder can make by hand that a real one can never hide under it.
    ///
    /// <para>TWO RULES. Positions scale it with magnitude, floored at 1, since their error is proportional to
    /// the value. UV takes it FLAT: a UV tiled into the tens would otherwise be judged at tens of times this
    /// number, which on an atlas is a shift of several texels — visible, and no longer the same value in any
    /// sense the modder would agree with.</para>
    ///
    /// <para>Normals are not held to this at all. They come back off a round trip moved by orders more than
    /// either rule allows, and what a normal means is a DIRECTION, so they are judged as one
    /// (<see cref="NormalDriftDegrees"/>).</para></summary>
    private const float ContentDrift = 1e-4f;

    /// <summary>How far a normal may TURN and still be the same normal. A round trip turns an untouched
    /// part's normals by a fraction of a degree — orders more than it moves a position or a UV — and this
    /// clears that with room to spare while still catching any re-shading a modder would call an edit: the
    /// smallest of those, a shared edge split hard, turns its corners by the angle between the faces.
    /// </summary>
    private const float NormalDriftDegrees = 2f;

    /// <summary>The turn tolerance as the cosine the comparison actually tests.</summary>
    private static readonly float NormalAgreement = MathF.Cos(NormalDriftDegrees * (MathF.PI / 180f));

    /// <summary>A normal too short to point anywhere. Below this, direction is noise rather than a value.
    /// </summary>
    private const float NormalFloor = 1e-6f;

    /// <summary>The magnitude-scaled rule, for positions.</summary>
    private static bool Same(float x, float y) =>
        MathF.Abs(x - y) <= ContentDrift * MathF.Max(1f, MathF.Max(MathF.Abs(x), MathF.Abs(y)));

    /// <summary>The flat rule, for UV.</summary>
    private static bool SameFlat(float x, float y) => MathF.Abs(x - y) <= ContentDrift;

    /// <summary>Whether two normals point the same way, to within <see cref="NormalDriftDegrees"/> of turn.
    /// Length is divided out, so a re-export that re-normalizes changes nothing here.
    ///
    /// <para>A normal with no length has no direction to compare: one on each side is a pair, one against a
    /// normal that does point somewhere is a difference. A component that is not a number fails the
    /// comparison it lands in, which reads the corner as changed — the safe direction.</para></summary>
    private static bool SameNormal(float[] x, int xd, int vx, float[] y, int yd, int vy)
    {
        int p = vx * xd, q = vy * yd;
        float ax = x[p], ay = x[p + 1], az = x[p + 2];
        float bx = y[q], by = y[q + 1], bz = y[q + 2];
        float la = MathF.Sqrt(ax * ax + ay * ay + az * az);
        float lb = MathF.Sqrt(bx * bx + by * by + bz * bz);
        if (la <= NormalFloor || lb <= NormalFloor) return la <= NormalFloor && lb <= NormalFloor;
        return (ax * bx + ay * by + az * bz) / (la * lb) >= NormalAgreement;
    }

    private static bool SameCorner(in Reader a, int va, in Reader b, int vb,
        Span<(uint Bone, float Weight)> wa, Span<(uint Bone, float Weight)> wb)
    {
        if (!SameVector(a.Pos, a.PosDim, va, b.Pos, b.PosDim, vb, 3)) return false;
        if (a.Nrm is not null && !SameNormal(a.Nrm, a.NrmDim, va, b.Nrm!, b.NrmDim, vb)) return false;
        for (int i = 0; i < a.Uvs.Count; i++)
            if (!SameVector(a.Uvs[i].Values, a.Uvs[i].Dim, va,
                    b.Uvs[i].Values, b.Uvs[i].Dim, vb, 2, flat: true)) return false;
        if (a.Bones is null) return true;
        return SameSkin(wa[..Influences(a, va, wa)], wb[..Influences(b, vb, wb)]);
    }

    /// <summary>Whether two vertices are held by the same bones at the same strengths. The two lists are
    /// walked together by bone, and a bone only one side names is compared against the NOTHING the other
    /// says about it — which passes while that weight is inside the tolerance, since a bone pulling that
    /// little is indistinguishable from one absent.
    ///
    /// <para>Reading an absent bone as a zero weight rather than dropping the light influences first is what
    /// keeps the tolerance from having an edge to sit on: a weight the transport renormalizes across the
    /// threshold would otherwise leave the two sides holding different numbers of influences and read as a
    /// repaint.</para></summary>
    private static bool SameSkin(ReadOnlySpan<(uint Bone, float Weight)> a,
        ReadOnlySpan<(uint Bone, float Weight)> b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i].Bone == b[j].Bone)
            {
                if (MathF.Abs(a[i].Weight - b[j].Weight) > MeshApply.SkinWeightDrift) return false;
                i++;
                j++;
            }
            else if (a[i].Bone < b[j].Bone) { if (a[i++].Weight > MeshApply.SkinWeightDrift) return false; }
            else if (b[j++].Weight > MeshApply.SkinWeightDrift) return false;
        }
        while (i < a.Length) if (a[i++].Weight > MeshApply.SkinWeightDrift) return false;
        while (j < b.Length) if (b[j++].Weight > MeshApply.SkinWeightDrift) return false;
        return true;
    }

    /// <summary><paramref name="flat"/> picks the UV rule over the magnitude-scaled one
    /// (<see cref="ContentDrift"/>).</summary>
    private static bool SameVector(float[] x, int xd, int vx, float[] y, int yd, int vy, int components,
        bool flat = false)
    {
        for (int c = 0; c < components; c++)
        {
            float u = x[vx * xd + c], v = y[vy * yd + c];
            if (!(flat ? SameFlat(u, v) : Same(u, v))) return false;
        }
        return true;
    }

    /// <summary>One vertex's influences as (bone, weight) pairs, sorted by bone, with a bone named twice
    /// carrying the sum of what its slots pull. Sorting is what lets a combined session's union armature
    /// compare against a part's own: the same influences arrive in another order there, and in a slot count
    /// the two need not agree on. A slot pulling nothing names no bone and is left out — and a weight that
    /// is negative or not a number pulls nothing: it is malformed data, read as absent so the tolerance
    /// rules judge the bones that do pull. Returns how many pairs were written.</summary>
    private static int Influences(in Reader r, int v, Span<(uint Bone, float Weight)> into)
    {
        int n = 0;
        for (int k = 0; k < 4; k++)
        {
            float w = r.Weights![v * 4 + k];
            if (!(w > 0f)) continue;
            uint bone = r.Bones![v * 4 + k];
            int at = 0;
            while (at < n && into[at].Bone < bone) at++;
            if (at < n && into[at].Bone == bone) { into[at] = (bone, into[at].Weight + w); continue; }
            for (int j = n; j > at; j--) into[j] = into[j - 1];
            into[at] = (bone, w);
            n++;
        }
        return n;
    }

    /// <summary>One payload's compared channels, resolved once: the arrays, their stored strides, and the
    /// skin named by BONE rather than by joint index. Null where an influence points outside its own joint
    /// list, which is not a skin this can compare.</summary>
    private readonly struct Reader
    {
        public required float[] Pos { get; init; }
        public required int PosDim { get; init; }
        public float[]? Nrm { get; init; }
        public int NrmDim { get; init; }
        public required IReadOnlyList<(float[] Values, int Dim)> Uvs { get; init; }
        public uint[]? Bones { get; init; }
        public float[]? Weights { get; init; }
        public required int Count { get; init; }

        public static Reader? For(MeshApply.Payload p, int uvSets)
        {
            uint[]? bones = null;
            if (p.HasSkin)
            {
                bones = BoneHashes(p);
                if (bones is null) return null;
            }
            int posDim = Dim(p, "Vertex", 3);
            if (posDim < 3 || p.Channels["Vertex"].Length < p.VertexCount * posDim) return null;
            int nrmDim = Dim(p, "Normal", 3);
            if (p.Has("Normal") && (nrmDim < 3 || p.Channels["Normal"].Length < p.VertexCount * nrmDim))
                return null;
            var uvs = new List<(float[] Values, int Dim)>();
            for (int i = 0; i < uvSets; i++)
            {
                string channel = $"TexCoord{i}";
                int dim = Dim(p, channel, 2);
                if (!p.Has(channel) || dim < 2 || p.Channels[channel].Length < p.VertexCount * dim) return null;
                uvs.Add((p.Channels[channel], dim));
            }
            return new Reader
            {
                Pos = p.Channels["Vertex"], PosDim = posDim,
                Nrm = p.Has("Normal") ? p.Channels["Normal"] : null, NrmDim = nrmDim,
                Uvs = uvs,
                Bones = bones, Weights = bones is null ? null : p.JointWeights,
                Count = p.VertexCount,
            };
        }

        /// <summary>A channel's stored stride. A hand-built mesh carries no Dims, so the caller's own
        /// component count stands in — reading a wider channel at the narrow stride would emit mis-strided
        /// values from vertex 1 on.</summary>
        private static int Dim(MeshApply.Payload p, string channel, int fallback) =>
            p.Dims.TryGetValue(channel, out var d) && d > 0 ? d : fallback;
    }

    /// <summary>Each influence's bone hash, in per-vertex order — the identity a joint index only stands in
    /// for. Null where an index points outside its own joint list.</summary>
    private static uint[]? BoneHashes(MeshApply.Payload p)
    {
        var hashes = p.SkinJointHashes!;
        var ji = p.JointIndices!;
        if (p.JointWeights!.Length != ji.Length) return null;
        var bones = new uint[ji.Length];
        for (int k = 0; k < ji.Length; k++)
        {
            if (ji[k] < 0 || ji[k] >= hashes.Length) return null;
            bones[k] = hashes[ji[k]];
        }
        return bones;
    }
}
