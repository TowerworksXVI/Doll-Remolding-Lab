using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Remold.Core.Mesh;

/// <summary>
/// Mesh compile: apply the authored geometry+skin into the live Mesh type tree at PACKAGE time, then
/// serialize the whole object as the blob injected verbatim.
///
/// <para>Weights are DERIVED IN BLENDER (prepare-on-Send), so a payload reaching this app already carries a
/// complete authored skin. One path only: read the authored JOINTS/WEIGHTS + joint bone-hashes, map each
/// joint onto the TARGET's bone order by hash, conform to the target's influence capacity + vertex layout,
/// serialize. Weights are never re-derived here. A payload without a skin, or referencing a bone the target
/// lacks, is an authoring error the build surfaces rather than papers over.</para>
/// </summary>
public static class MeshApply
{
    /// <summary>The authored geometry to apply, in raw Unity space: the <see cref="UnityMesh"/> plus the
    /// authored skin the compile maps onto the target.</summary>
    public sealed class Payload
    {
        public required UnityMesh Mesh { get; init; }
        /// <summary>Per-vertex glTF joint indices (length VertexCount*4), into <see cref="SkinJointHashes"/>
        /// order. Null when the payload carries no skin.</summary>
        public int[]? JointIndices { get; init; }
        /// <summary>Per-vertex skin weights (length VertexCount*4). Null when the payload carries no skin.</summary>
        public float[]? JointWeights { get; init; }
        /// <summary>Bone hash of each skin joint; 0 where unrecoverable. Null when no skin.</summary>
        public uint[]? SkinJointHashes { get; init; }

        public int VertexCount => Mesh.VertexCount;
        public Dictionary<string, float[]> Channels => Mesh.Channels;
        public Dictionary<string, int> Dims => Mesh.Dims;
        public List<int[]> Submeshes => Mesh.Submeshes;
        public bool Has(string channel) => Mesh.Has(channel);
        public bool HasSkin => JointIndices is not null && JointWeights is not null && SkinJointHashes is not null;

        /// <summary>Wrap a geometry-only <see cref="UnityMesh"/>, for callers where the skin is
        /// irrelevant.</summary>
        public static Payload Geometry(UnityMesh mesh) => new() { Mesh = mesh };
    }

    /// <summary>Summary of one applied mesh. <paramref name="Warnings"/> are user-facing: the authored
    /// edit won't show or will look wrong, and there is something the author can do about it.
    /// <paramref name="Diagnostics"/> record what the compile did to the payload; they reach
    /// the build log and no UI surface.</summary>
    public readonly record struct Result(
        string Name, int OrigVertexCount, int NewVertexCount,
        IReadOnlyList<string> Warnings, IReadOnlyList<string> Diagnostics);

    // Every non-skin channel the corpus layout can declare. The list must cover them ALL: a channel left out
    // of the built arrays is silently ZEROED by Encode. The transport only moves TexCoord0, so TexCoord1+
    // ride the nearest-original fill.
    private static readonly string[] GeometryChannels =
        { "Vertex", "Normal", "Tangent", "Color", "TexCoord0", "TexCoord1",
          "TexCoord2", "TexCoord3", "TexCoord4", "TexCoord5", "TexCoord6", "TexCoord7" };

    /// <summary>Apply <paramref name="payload"/> into <paramref name="meshField"/> (a Mesh base field),
    /// mutating it in place; the caller commits it.</summary>
    public static Result Apply(AssetTypeValueField meshField, Payload payload)
    {
        var original = UnityMesh.Decode(meshField);
        RequireStride3Positions(original);

        var built = BuildSkinned(original, payload, ReadBoneHashes(meshField));

        WriteBack(meshField, original, built);
        return new Result(original.Name, original.VertexCount, built.VertexCount,
                          built.Warnings, built.Diagnostics);
    }

    internal sealed class Built
    {
        public required Dictionary<string, float[]> Arrays;
        public required List<int[]> Submeshes;
        public required int VertexCount;
        /// <summary>User-facing: the authored edit won't show or will look wrong, and there is something
        /// the author can do about it.</summary>
        public List<string> Warnings = new();
        /// <summary>What the compile did to the payload — the build log only, never a UI surface.</summary>
        public List<string> Diagnostics = new();
        /// <summary>Payload-vertex → original-vertex map, used to fill a payload channel's missing
        /// components from the original (ConformChannels). <c>null</c> means identity (built vertex v ↔
        /// original vertex v) — only legal when <c>VertexCount == orig.VertexCount</c> (preserve).</summary>
        public int[]? NearestOriginal;
        /// <summary>True when the identity byte-restore ran (the payload IS the original): the write-back
        /// keeps the shipped submesh table + local AABB instead of recomputing. Unity's stored bounds aren't
        /// always the tight recompute (blendshape-carrying faces store wider ones), so rewriting them would
        /// change bytes on an unedited mesh.</summary>
        public bool IdentityRestored;
    }

    // ---- the compile -------------------------------------------------------

    /// <summary>Map the authored glb skin onto the target's bone order by joint-name hash, taking topology and
    /// weights from the glb. Unresolved-bone vertices fall back to the nearest original vertex's weights so
    /// nothing yanks to the skeleton root; a missing geometry channel is filled from the original by
    /// proximity.</summary>
    internal static Built BuildSkinned(UnityMesh orig, Payload glb, uint[] targetBoneHashes)
    {
        if (!glb.HasSkin)
            throw new InvalidOperationException("the mesh payload carries no skin (JOINTS_0/WEIGHTS_0); " +
                                                "re-export from Blender so weights ride along");
        // NaN/Inf or negative weights would serialize into the blob as undefined deformation the downstream
        // gates can't catch. glTF weights are non-negative by spec, so a negative one is a broken export.
        foreach (var w in glb.JointWeights!)
            if (!float.IsFinite(w) || w < 0f)
                throw new InvalidOperationException("the mesh payload has invalid skin weights (NaN/Inf/negative); " +
                                                    "re-paint the affected vertices in Blender");
        // Resolve the authored joints onto the target bone order.
        var jr = ResolveAuthoredJoints(targetBoneHashes, glb.SkinJointHashes!, glb.JointIndices!, glb.JointWeights!, glb.VertexCount);

        int n = glb.VertexCount;
        var bi = new float[n * 4];
        var bw = new float[n * 4];
        for (int v = 0; v < n; v++)
        {
            bool droppedSome = false;
            for (int k = 0; k < 4; k++)
            {
                int gj = glb.JointIndices![v * 4 + k];
                float w = glb.JointWeights![v * 4 + k];
                int ti = gj >= 0 && gj < jr.JointToTarget.Length ? jr.JointToTarget[gj] : -1;
                if (ti < 0)
                {
                    // DROP the unresolved influence (don't pile its weight onto bone 0); keep the rest
                    bi[v * 4 + k] = 0; bw[v * 4 + k] = 0;
                    if (w > 0) droppedSome = true;
                }
                else { bi[v * 4 + k] = ti; bw[v * 4 + k] = w; }
            }
            // kept some resolved influence but dropped a weighted unresolved one: renormalize the survivors
            // so it doesn't partially collapse (fully-unresolved verts fall back below)
            if (droppedSome && !jr.FullyUnsafe[v])
            {
                float sum = 0f;
                for (int k = 0; k < 4; k++) sum += bw[v * 4 + k];
                if (sum > 0f) for (int k = 0; k < 4; k++) bw[v * 4 + k] /= sum;
            }
        }

        int[]? nn = null;
        // ONLY a vertex whose every unit of authored WEIGHT is on missing bones falls back to the nearest
        // original skin, so it doesn't yank to the root or ship an all-zero skin.
        if (jr.FullyUnsafeCount > 0 && orig.Has("BlendIndices") && orig.Has("BlendWeight"))
        {
            nn = NearestNeighbors(orig.Channels["Vertex"], orig.VertexCount, glb.Channels["Vertex"], n);
            var oi = orig.Channels["BlendIndices"]; var ow = orig.Channels["BlendWeight"];
            int od = orig.Dims["BlendWeight"];   // the original's STORED influence width (1–4), == BlendIndices dim
            for (int v = 0; v < n; v++)
            {
                if (!jr.FullyUnsafe[v]) continue;
                int s = nn[v];
                // read the original at ITS width (not a hardcoded 4 — a narrow original would over-read), pad to 4
                for (int k = 0; k < 4; k++)
                {
                    bi[v * 4 + k] = k < od ? oi[s * od + k] : 0f;
                    bw[v * 4 + k] = k < od ? ow[s * od + k] : 0f;
                }
            }
        }

        // The authored skin is always 4-wide (glTF JOINTS_0/WEIGHTS_0 are vec4); the game stores 1–4.
        // Reduce to the target's stored width by keeping the STRONGEST influences and renormalizing, or a
        // narrow target rejects the 4-wide skin at ConformChannels and the build aborts.
        int reducedVerts = 0;
        // A 1-influence layout may store BlendIndices WITHOUT a BlendWeight channel (weight implicitly 1),
        // so key on whichever skin channel the layout actually has.
        int targetWidth = orig.Has("BlendWeight") ? orig.Dims["BlendWeight"]
                        : orig.Has("BlendIndices") ? orig.Dims["BlendIndices"] : 4;
        if (targetWidth >= 1 && targetWidth < 4)
            (bi, bw, reducedVerts) = ReduceInfluences(bi, bw, n, targetWidth);

        var (arrays, bakeWarning, identity) = ComposeGeometry(orig, glb, n, ref nn);
        arrays["BlendIndices"] = bi;
        arrays["BlendWeight"] = bw;
        // The skin joins the identity byte-restore when it matches — a weight-only repaint differs and keeps
        // the authored skin.
        if (identity && SkinMatchesOriginal(bi, bw, orig))
        {
            arrays["BlendIndices"] = (float[])orig.Channels["BlendIndices"].Clone();
            arrays["BlendWeight"] = (float[])orig.Channels["BlendWeight"].Clone();
        }

        var built = new Built { Arrays = arrays, Submeshes = glb.Submeshes, VertexCount = n,
                                Warnings = WeightHealth(arrays, n), NearestOriginal = nn,
                                IdentityRestored = identity };
        if (bakeWarning is not null) built.Warnings.Add(bakeWarning);
        // Only a narrow target reduces, so a reduction always means vertices were crushed onto fewer bones
        // than the author weighted them to — deformation the author can see and act on, not compile detail.
        if (reducedVerts > 0)
            built.Warnings.Add($"{reducedVerts} vertex(es) had more than {targetWidth} bone influence(s); " +
                               $"reduced to the strongest {targetWidth} and renormalized");
        if (OutOfSkeletonWarning(jr) is { } outOfSkel)
            built.Warnings.Add(outOfSkel);
        return built;
    }

    /// <summary>The geometry half of a compile, shared by the skinned and skinless routes: the payload's own
    /// channels, the original's filled in by proximity wherever the payload has none, the outline bake, and
    /// the identity byte-restore. <paramref name="nn"/> is the payload→original nearest map, computed here
    /// when the caller has none yet and handed back either way — one nearest search per compile.</summary>
    private static (Dictionary<string, float[]> Arrays, string? BakeWarning, bool Identity) ComposeGeometry(
        UnityMesh orig, Payload glb, int n, ref int[]? nn)
    {
        var arrays = new Dictionary<string, float[]>();
        foreach (var ch in GeometryChannels)
            if (glb.Has(ch)) arrays[ch] = glb.Channels[ch];
        // Color and any absent geometry channel fill from the original by proximity. The outline channel
        // (Color) is NEVER carried through Blender: this fill supplies its WIDTH (Color.a), and BakeOutline
        // then recomputes the DIRECTION (Color.rgb) from the finished mesh's normals + tangent.
        nn ??= NearestNeighbors(orig.Channels["Vertex"], orig.VertexCount, glb.Channels["Vertex"], n);
        foreach (var ch in GeometryChannels)
            if (orig.Has(ch) && !arrays.ContainsKey(ch))
                arrays[ch] = GatherByNearest(orig.Channels[ch], orig.Dims[ch], nn);
        var bakeWarning = BakeOutline(arrays, orig, glb, n, nn);

        // Identity byte-restore: when the payload IS the original, the transport's float drift (the glTF UV
        // v-flip, nearest-fill donor ties between coincident verts) must not ship changed bytes. Every
        // original channel wins.
        bool identity = GeometryUnchanged(glb.Mesh, orig);
        if (identity)
            foreach (var ch in GeometryChannels)
                if (orig.Has(ch)) arrays[ch] = (float[])orig.Channels[ch].Clone();
        return (arrays, bakeWarning, identity);
    }

    /// <summary>Compile geometry alone onto a SKINLESS target: the layout carries no
    /// BlendIndices/BlendWeight channel, so there is no target bone order to map an authored skin onto and
    /// the payload's own is dropped rather than written somewhere it cannot be read from.</summary>
    internal static Built BuildGeometry(UnityMesh orig, Payload glb)
    {
        int n = glb.VertexCount;
        int[]? nn = null;
        var (arrays, bakeWarning, identity) = ComposeGeometry(orig, glb, n, ref nn);
        var built = new Built
        {
            Arrays = arrays, Submeshes = glb.Submeshes, VertexCount = n,
            NearestOriginal = nn, IdentityRestored = identity,
        };
        if (bakeWarning is not null) built.Warnings.Add(bakeWarning);
        return built;
    }

    /// <summary>Apply <paramref name="payload"/>'s geometry into a skinless <paramref name="meshField"/>,
    /// mutating it in place; the caller commits it. <see cref="Apply"/> is the route for a target that
    /// carries a bone table.</summary>
    public static Result ApplyGeometry(AssetTypeValueField meshField, Payload payload)
    {
        var original = UnityMesh.Decode(meshField);
        RequireStride3Positions(original);

        var built = BuildGeometry(original, payload);

        WriteBack(meshField, original, built);
        return new Result(original.Name, original.VertexCount, built.VertexCount,
                          built.Warnings, built.Diagnostics);
    }

    /// <summary>True when the mapped-and-reduced skin equals the original's stored skin. Only then may the
    /// identity byte-restore ship the original skin bytes.</summary>
    private static bool SkinMatchesOriginal(float[] bi, float[] bw, UnityMesh orig) =>
        orig.Has("BlendIndices") && orig.Has("BlendWeight")
        && SkinUnchanged(bi, bw, orig.Channels["BlendIndices"], orig.Channels["BlendWeight"]);

    /// <summary>True when two (index, weight) skins are the same skin: influences exactly, weights within
    /// renormalization drift. This is the skin-identity rule for a payload compiled against the mesh it was
    /// exported from, where the two sides share a vertex order.
    ///
    /// <para>The influence arrays are compared AS GIVEN, per vertex slot. A caller whose two sides index
    /// different joint lists — a combined session's union armature against a part's own — must map them onto
    /// a shared identity first, or the same skin reads as two.</para></summary>
    internal static bool SkinUnchanged(float[] ai, float[] aw, float[] bi, float[] bw)
    {
        if (ai.Length != bi.Length) return false;
        for (int i = 0; i < ai.Length; i++) if (ai[i] != bi[i]) return false;
        return WeightsUnchanged(aw, bw);
    }

    private static bool WeightsUnchanged(float[] aw, float[] bw)
    {
        if (aw.Length != bw.Length) return false;
        for (int i = 0; i < aw.Length; i++) if (MathF.Abs(aw[i] - bw[i]) > SkinWeightDrift) return false;
        return true;
    }

    /// <summary>How far a weight may move and still be the same weight: renormalizing after an influence
    /// reduction shifts weights by a little, and that is transport, not a repaint. One value for every skin
    /// comparison in the app, so the compile and the send-back cannot judge a repaint differently.</summary>
    internal const float SkinWeightDrift = 2e-3f;

    /// <summary>Reduce a 4-wide (index, weight) skin to <paramref name="width"/> (1–3) by keeping each
    /// vertex's strongest influences and renormalizing to sum 1. BlendIndices and BlendWeight reduce JOINTLY
    /// so the index↔weight pairing survives. An all-zero vertex stays zero (WeightHealth flags it). Returns
    /// the narrowed arrays plus the count of vertices that LOST a nonzero influence.</summary>
    internal static (float[] Indices, float[] Weights, int Reduced) ReduceInfluences(
        float[] bi4, float[] bw4, int n, int width)
    {
        var oi = new float[n * width];
        var ow = new float[n * width];
        int reduced = 0;
        Span<int> order = stackalloc int[4];
        for (int v = 0; v < n; v++)
        {
            for (int k = 0; k < 4; k++) order[k] = k;
            // insertion-sort the 4 slots by weight descending (stable enough for a 4-element rank)
            for (int a = 1; a < 4; a++)
                for (int b = a; b > 0 && bw4[v * 4 + order[b]] > bw4[v * 4 + order[b - 1]]; b--)
                    (order[b], order[b - 1]) = (order[b - 1], order[b]);
            // a nonzero weight in any DROPPED slot means this vertex genuinely lost an influence
            for (int k = width; k < 4; k++)
                if (bw4[v * 4 + order[k]] > 0f) { reduced++; break; }
            float sum = 0f;
            for (int k = 0; k < width; k++) sum += bw4[v * 4 + order[k]];
            for (int k = 0; k < width; k++)
            {
                oi[v * width + k] = bi4[v * 4 + order[k]];
                ow[v * width + k] = sum > 0f ? bw4[v * 4 + order[k]] / sum : 0f;
            }
        }
        return (oi, ow, reduced);
    }

    // ---- write-back into the type tree ------------------------------------

    /// <summary>Refuse an authored mesh whose indices don't fit the target's index width. On a 16-bit target
    /// (<paramref name="indexFormat"/> 0) any index &gt; 65535 silently wraps on the <c>(ushort)</c> cast and
    /// ships corrupt geometry that still re-parses, so the commit gate wouldn't catch it. Refuse rather than
    /// flip the target to 32-bit — an untested format change against the runtime.</summary>
    internal static void CheckIndexFits(int indexFormat, int vertexCount, List<int[]> submeshes)
    {
        if (indexFormat != 0) return;   // 32-bit target addresses far more than any real mesh
        const int max = ushort.MaxValue;   // 65535 — a uint16 index addresses 0..65535, so 65536 vertices fit
        if (vertexCount > max + 1)
            throw new InvalidOperationException(
                $"this mesh has {vertexCount} vertices but the game asset uses 16-bit indices; reduce to at most " +
                $"{max + 1}, or the geometry would be corrupted (retopologize/decimate in Blender)");
        foreach (var sl in submeshes)
            foreach (var idx in sl)
                if (idx > max)
                    throw new InvalidOperationException(
                        $"this mesh references vertex index {idx} but the game asset uses 16-bit indices; " +
                        $"reduce below {max + 1}, or the geometry would be corrupted");
    }

    private static void WriteBack(AssetTypeValueField mesh, UnityMesh orig, Built built)
    {
        var vd = mesh["m_VertexData"];
        // Mask dimension to its low nibble (the STORED stride) exactly as Decode does — the high nibble is
        // the semantic count (0x34 Normal etc.), and Encode must round-trip against the same masked layout
        // Decode read. The raw packed byte in the tree is never rewritten.
        var channelDefs = vd["m_Channels"]["Array"].Children
            .Select(c => new UnityMesh.ChannelDef(c["stream"].AsInt, c["offset"].AsInt, c["format"].AsInt, c["dimension"].AsInt & 0xF))
            .ToList();

        int n = built.VertexCount;
        vd["m_VertexCount"].AsInt = n;
        // Conform every payload-supplied channel to the target's stored layout dim before encoding: a
        // 3-component glb normal into a packed 4-component Normal channel would fail Encode's length check,
        // and a payload wider than the layout would mis-stride. Produces a NEW dictionary, so built.Arrays
        // stays intact for the pos/AABB math below (which reads Vertex at stride 3).
        var encodeArrays = ConformChannels(channelDefs, n, built, orig);
        WriteBytes(vd["m_DataSize"], UnityMesh.Encode(channelDefs, n, encodeArrays));

        // The vertex data is now inline in m_DataSize. Drop any streamed .resS reference so the game reads
        // the inline bytes, not the stale slice; the orphaned slice stays in the bundle, never re-read.
        ClearStreamData(mesh);

        var pos = built.Arrays["Vertex"];
        int indexFormat = mesh["m_IndexFormat"].AsInt;        // 0 = uint16, 1 = uint32
        int step = indexFormat == 0 ? 2 : 4;
        var subs = built.Submeshes;

        CheckIndexFits(indexFormat, built.VertexCount, subs);

        // one shared index buffer, each submesh a contiguous run (baseVertex folded to 0 = absolute indices)
        int totalIndices = subs.Sum(s => s.Length);
        var indexBuffer = new byte[totalIndices * step];
        int bytePos = 0;
        foreach (var sl in subs)
            foreach (var idx in sl)
            {
                if (indexFormat == 0) BitConverter.GetBytes((ushort)idx).CopyTo(indexBuffer, bytePos);
                else BitConverter.GetBytes((uint)idx).CopyTo(indexBuffer, bytePos);
                bytePos += step;
            }
        WriteBytes(mesh["m_IndexBuffer"], indexBuffer);

        if (!built.IdentityRestored)   // see Built.IdentityRestored: unedited bytes must stay unedited
        {
            WriteSubMeshes(mesh["m_SubMeshes"], subs, n, step, pos);
            SetAabb(mesh["m_LocalAABB"], pos, AllIndices(n));
        }
    }

    /// <summary>Zero a mesh's streamed <c>m_StreamData</c> reference (size/offset to 0, path to empty) so the
    /// game reads the edited inline <c>m_DataSize</c> vertex buffer rather than the stale <c>.resS</c>
    /// slice.</summary>
    private static void ClearStreamData(AssetTypeValueField mesh)
    {
        var sd = mesh["m_StreamData"];
        if (sd.IsDummy) return;
        var size = sd["size"];
        if (size.IsDummy) return;
        if (size.AsLong == 0) return;                          // already inline — nothing to clear
        size.AsLong = 0;
        var offset = sd["offset"];
        if (!offset.IsDummy) offset.AsLong = 0;
        var path = sd["path"];
        if (!path.IsDummy) path.AsString = "";
    }

    /// <summary>Refuse a target whose stored Vertex/position dimension isn't 3. The apply path hard-codes
    /// stride 3 everywhere positions are read, and Vertex is never packed in the corpus (only 0x03 seen), so
    /// an odd layout must be refused loudly rather than silently mis-strided. Called at Apply entry on the
    /// decoded original — the choke point every mode passes through.</summary>
    internal static void RequireStride3Positions(UnityMesh orig)
    {
        if (orig.Has("Vertex") && orig.Dims["Vertex"] != 3)
            throw new InvalidOperationException(
                $"mesh '{orig.Name}': the position channel stores {orig.Dims["Vertex"]} components, expected 3. " +
                "This mesh uses an unsupported vertex layout the editor can't apply to");
    }

    /// <summary>Conform every built channel to its target layout dimension and return a NEW dictionary for
    /// <see cref="UnityMesh.Encode"/>, leaving <c>built.Arrays</c> untouched. A channel narrower than the
    /// target layout has its missing components filled from the ORIGINAL mesh via the nearest-original-vertex
    /// map; a wider one is truncated — except BlendWeight/BlendIndices, where truncation is refused, since
    /// dropping a bone influence silently ships wrong deformation.</summary>
    internal static Dictionary<string, float[]> ConformChannels(
        IReadOnlyList<UnityMesh.ChannelDef> channelDefs, int n, Built built, UnityMesh orig)
    {
        var outArrays = new Dictionary<string, float[]>(built.Arrays.Count);
        // start with every built array so channels absent from the layout still pass through
        foreach (var kv in built.Arrays) outArrays[kv.Key] = kv.Value;

        for (int ci = 0; ci < channelDefs.Count && ci < UnityMesh.ChannelNames.Length; ci++)
        {
            int L = channelDefs[ci].Dimension;
            if (L == 0) continue;
            string name = UnityMesh.ChannelNames[ci];
            if (!built.Arrays.TryGetValue(name, out var arr)) continue;   // Encode leaves it zero

            if (n == 0)
            {
                if (arr.Length != 0)
                    throw new InvalidOperationException($"channel '{name}': {arr.Length} values but the mesh has 0 vertices");
                continue;
            }
            if (arr.Length % n != 0)
                throw new InvalidOperationException(
                    $"channel '{name}': {arr.Length} values isn't a whole number of components for {n} vertices");
            int have = arr.Length / n;
            if (have == L) { outArrays[name] = arr; continue; }   // exact — pass through

            if (have > L)
            {
                if (name is "BlendWeight" or "BlendIndices")
                    throw new InvalidOperationException(
                        $"the authored mesh carries {have} bone influences per vertex ('{name}') but the target " +
                        $"stores only {L}; dropping influences would silently break the skinning. Re-export " +
                        "with at most the target's influence count");
                var trunc = new float[(long)n * L <= int.MaxValue ? n * L : throw new InvalidOperationException($"channel '{name}' too large")];
                for (int v = 0; v < n; v++)
                    for (int d = 0; d < L; d++)
                        trunc[v * L + d] = arr[v * have + d];
                outArrays[name] = trunc;
                continue;
            }

            // have < L: widen, filling components [have, L) from the original by nearest-original vertex.
            if (!orig.Has(name) || orig.Dims[name] != L)
                throw new InvalidOperationException(
                    $"channel '{name}': the authored mesh has {have} components but the target needs {L}, and the " +
                    $"original mesh can't supply the rest ({(orig.Has(name) ? $"orig dim {orig.Dims[name]}" : "orig lacks the channel")}). " +
                    "This should be impossible for a mesh decoded from this layout");
            var widened = new float[n * L];
            var src = orig.Channels[name];
            int srcDim = orig.Dims[name];
            for (int v = 0; v < n; v++)
            {
                for (int d = 0; d < have; d++) widened[v * L + d] = arr[v * have + d];
                int ov = built.NearestOriginal?[v] ?? v;   // null = identity (preserve, same vertex count)
                for (int d = have; d < L; d++) widened[v * L + d] = src[ov * srcDim + d];
            }
            outArrays[name] = widened;
        }
        return outArrays;
    }

    /// <summary>Rebuild the m_SubMeshes array: reuse existing entries (keeping their topology), trim extras,
    /// clone the template for added slots. Each entry gets tight local bounds.</summary>
    private static void WriteSubMeshes(AssetTypeValueField subMeshes, List<int[]> subs, int vertexCount, int step, float[] pos)
    {
        var array = subMeshes["Array"];
        int existing = array.Children.Count;
        var template = existing > 0 ? array.Children[0] : null;
        var children = new List<AssetTypeValueField>(subs.Count);
        int byteOffset = 0;
        for (int i = 0; i < subs.Count; i++)
        {
            var sm = i < existing ? array.Children[i]
                   : NewSubMesh(array, template ?? throw new InvalidDataException("mesh has no submesh template to extend"));
            int len = subs[i].Length;
            sm["firstByte"].AsUInt = (uint)byteOffset;
            sm["indexCount"].AsUInt = (uint)len;
            sm["baseVertex"].AsUInt = 0;
            sm["firstVertex"].AsUInt = 0;
            sm["vertexCount"].AsUInt = (uint)vertexCount;
            SetAabb(sm["localAABB"], pos, subs[i].Length == 0 ? Array.Empty<int>() : subs[i].Distinct().ToArray());
            children.Add(sm);
            byteOffset += len * step;
        }
        array.Children = children;
    }

    private static AssetTypeValueField NewSubMesh(AssetTypeValueField array, AssetTypeValueField template)
    {
        // topology is copied from the template so an added submesh keeps the mesh's type
        var fresh = ValueBuilder.DefaultValueFieldFromArrayTemplate(array);
        if (!template["topology"].IsDummy && !fresh["topology"].IsDummy)
            fresh["topology"].AsInt = template["topology"].AsInt;
        return fresh;
    }

    // ---- helpers -----------------------------------------------------------

    private static uint[] ReadBoneHashes(AssetTypeValueField mesh) =>
        mesh["m_BoneNameHashes"]["Array"].Children.Select(c => (uint)c.AsUInt).ToArray();

    /// <summary>Brute-force nearest source vertex (by squared distance) for each query vertex. O(q·s).</summary>
    private static int[] NearestNeighbors(float[] src, int srcN, float[] query, int queryN)
    {
        var nn = new int[queryN];
        for (int i = 0; i < queryN; i++)
        {
            float qx = query[i * 3], qy = query[i * 3 + 1], qz = query[i * 3 + 2];
            float best = float.MaxValue; int bi = 0;
            for (int j = 0; j < srcN; j++)
            {
                float dx = src[j * 3] - qx, dy = src[j * 3 + 1] - qy, dz = src[j * 3 + 2] - qz;
                float d = dx * dx + dy * dy + dz * dz;
                if (d < best) { best = d; bi = j; }
            }
            nn[i] = bi;
        }
        return nn;
    }

    private static float[] GatherByNearest(float[] src, int dim, int[] nn)
    {
        var outp = new float[nn.Length * dim];
        for (int i = 0; i < nn.Length; i++)
            for (int d = 0; d < dim; d++)
                outp[i * dim + d] = src[nn[i] * dim + d];
        return outp;
    }

    private static int[] AllIndices(int n)
    {
        var a = new int[n];
        for (int i = 0; i < n; i++) a[i] = i;
        return a;
    }

    /// <summary>Set an AABB (m_Center/m_Extent) to the tight bounds of the given vertex subset.</summary>
    private static void SetAabb(AssetTypeValueField aabb, float[] pos, int[] verts)
    {
        if (verts.Length == 0) return;
        float minx = float.MaxValue, miny = float.MaxValue, minz = float.MaxValue;
        float maxx = float.MinValue, maxy = float.MinValue, maxz = float.MinValue;
        foreach (int v in verts)
        {
            float x = pos[v * 3], y = pos[v * 3 + 1], z = pos[v * 3 + 2];
            if (x < minx) minx = x; if (x > maxx) maxx = x;
            if (y < miny) miny = y; if (y > maxy) maxy = y;
            if (z < minz) minz = z; if (z > maxz) maxz = z;
        }
        var c = aabb["m_Center"]; var e = aabb["m_Extent"];
        c["x"].AsFloat = (minx + maxx) / 2; c["y"].AsFloat = (miny + maxy) / 2; c["z"].AsFloat = (minz + maxz) / 2;
        e["x"].AsFloat = (maxx - minx) / 2; e["y"].AsFloat = (maxy - miny) / 2; e["z"].AsFloat = (maxz - minz) / 2;
    }

    /// <summary>Write bytes into a Unity byte vector, handling TypelessData (bytes on the field) and a
    /// byte-optimized <c>vector&lt;UInt8&gt;</c> (bytes on the Array child).</summary>
    private static void WriteBytes(AssetTypeValueField f, byte[] bytes)
    {
        if (f.TemplateField.ValueType == AssetValueType.ByteArray) { f.AsByteArray = bytes; return; }
        var arr = f["Array"];
        if (!arr.IsDummy && arr.TemplateField.ValueType == AssetValueType.ByteArray) { arr.AsByteArray = bytes; return; }
        // non-optimized fallback: rebuild UInt8 element children
        var kids = new List<AssetTypeValueField>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            el.AsByte = bytes[i];
            kids.Add(el);
        }
        arr.Children = kids;
    }

    /// <summary>Flag vertices that will deform badly: influence sum &lt; 0.5 collapses to the bind pose. The
    /// per-vertex BlendWeight width is derived from <paramref name="vertexCount"/> — a narrow skin (dim 1–3,
    /// real in the corpus) would be mis-summed by a hard-coded 4.</summary>
    private static List<string> WeightHealth(Dictionary<string, float[]> arrays, int vertexCount)
    {
        var outp = new List<string>();
        if (!arrays.TryGetValue("BlendWeight", out var bw) || bw.Length == 0 || vertexCount <= 0) return outp;
        int dim = bw.Length / vertexCount;                     // actual stored influences per vertex (1..4)
        if (dim <= 0 || dim * vertexCount != bw.Length) return outp;   // ragged buffer — don't guess
        int bad = 0;
        for (int v = 0; v < vertexCount; v++)
        {
            float sum = 0f;
            for (int c = 0; c < dim; c++) sum += bw[v * dim + c];
            if (sum < 0.5f) bad++;
        }
        if (bad > 0)
            outp.Add($"{bad} vertex(es) have almost no weight (sum<0.5). They will collapse to the bind pose; " +
                     "assign/paint weights for them");
        return outp;
    }

    // ---- bone resolution diagnosis ----

    /// <summary>The result of mapping an authored skin's joints onto a target skeleton: the per-joint target
    /// index the compile uses, plus the counts the warning is worded from.
    /// <see cref="FullyUnsafe"/>/<see cref="FullyUnsafeCount"/> drive the nearest-original fallback — a vertex
    /// is unsafe when NO WEIGHTED influence resolved; a zero-weight resolved slot must not mask that, or the
    /// vertex ships an all-zero skin. The *-Weighted counts are the warning inputs: an absent bone is only
    /// worth warning about when an authored influence puts weight on it.</summary>
    internal sealed class JointResolution
    {
        public required int[] JointToTarget;      // per authored joint → target bone index, -1 if absent
        public required bool[] FullyUnsafe;       // per vertex: no WEIGHTED influence resolved to a target bone
        public int FullyUnsafeCount;              // count of FullyUnsafe (drives the fallback)
        public int FullyWeightedVerts;            // fully-unsafe AND carried weight on a missing bone
        public int PartialVerts;                  // kept weighted influence, dropped a weighted missing one
        public int UnresolvedWeightedBones;       // distinct missing joints that carry weight somewhere
    }

    /// <summary>Map each authored joint hash onto the target's bone order and tally the per-vertex weight
    /// impact of any bone the target lacks. Pure/read-only; <see cref="BuildSkinned"/> runs it to get the
    /// diagnosis Build reports.</summary>
    internal static JointResolution ResolveAuthoredJoints(
        uint[] targetBoneHashes, uint[] jointHashes, int[] jointIndices, float[] jointWeights, int vertexCount)
    {
        var hashToIndex = new Dictionary<uint, int>(targetBoneHashes.Length);
        for (int i = 0; i < targetBoneHashes.Length; i++) hashToIndex[targetBoneHashes[i]] = i;

        var jointToTarget = new int[jointHashes.Length];
        for (int j = 0; j < jointToTarget.Length; j++)
            jointToTarget[j] = hashToIndex.TryGetValue(jointHashes[j], out var ti) ? ti : -1;

        var fullyUnsafe = new bool[vertexCount];
        int fullyUnsafeCount = 0, fullyWeighted = 0, partial = 0;
        var weightedMissing = new HashSet<int>();
        for (int v = 0; v < vertexCount; v++)
        {
            // Weight decides safety, not slot resolution: a vertex whose only WEIGHT sits on missing bones
            // counts as fully unsafe, else the compile drops every real influence and ships an all-zero
            // skin. w != 0, not w > 0, so a negative weight (which Build refuses outright) counts.
            bool anyResolvedWeight = false, anyMissing = false, droppedWeighted = false;
            for (int k = 0; k < 4; k++)
            {
                int gj = jointIndices[v * 4 + k];
                float w = jointWeights[v * 4 + k];
                int ti = gj >= 0 && gj < jointToTarget.Length ? jointToTarget[gj] : -1;
                if (ti >= 0) { if (w != 0) anyResolvedWeight = true; }
                else
                {
                    anyMissing = true;
                    if (w != 0) { droppedWeighted = true; weightedMissing.Add(gj); }
                }
            }
            if (!anyResolvedWeight && anyMissing)
            {
                fullyUnsafe[v] = true; fullyUnsafeCount++;
                if (droppedWeighted) fullyWeighted++;
            }
            else if (droppedWeighted) partial++;
        }

        return new JointResolution
        {
            JointToTarget = jointToTarget, FullyUnsafe = fullyUnsafe, FullyUnsafeCount = fullyUnsafeCount,
            FullyWeightedVerts = fullyWeighted, PartialVerts = partial, UnresolvedWeightedBones = weightedMissing.Count,
        };
    }

    /// <summary>The out-of-skeleton warning, or null when nothing broke. Fires ONLY when an authored
    /// influence lands on a bone the target lacks AND carries weight. Non-blocking: an absent bone is
    /// warn-and-drop: Build ships the mesh and reports this.</summary>
    internal static string? OutOfSkeletonWarning(JointResolution jr)
    {
        if (jr.UnresolvedWeightedBones == 0) return null;
        int verts = jr.FullyWeightedVerts + jr.PartialVerts;
        return $"{verts} vertex(es) are weighted to {jr.UnresolvedWeightedBones} bone(s) the target skeleton " +
               $"doesn't have. {jr.FullyWeightedVerts} fell back to the original weights, {jr.PartialVerts} " +
               "dropped that influence and kept the rest. Paint them to a bone the outfit uses.";
    }

    // ---- outline bake (vertex Color) ----

    /// <summary>Recompute the outline channel (vertex Color) from the FINISHED mesh, in Unity space, after
    /// geometry/normals/tangents are final. Three cases, all encoded into the shipped tangent frame via
    /// <see cref="EncodeOutlineNormal"/>:
    /// <list type="bullet">
    /// <item><b>Unchanged geometry</b>: keep the original outline AND tangent byte-for-bit together, so the
    /// two never desync.</item>
    /// <item><b>Body/cloth/props</b>: <c>Color.rgb</c> = the area-weighted face normal welded by shared
    /// position (<see cref="SmoothNormalsByPosition"/>).</item>
    /// <item><b>Hair/face</b>: the shipped outline there is geometric-plus-authored, so CARRY it instead of
    /// baking over it — decode the nearest original vertex's outline and re-encode into this vertex's frame.
    /// Byte-preserving wherever the frame is unchanged, whatever space the data was authored in.</item>
    /// </list>
    /// A vertex whose nearest original had its outline disabled (white Color) is left disabled.
    /// <c>Color.a</c> (width) rides from the nearest original via the caller's fill. Returns a warning when
    /// the direction could not be re-baked (no normal/tangent frame).</summary>
    internal static string? BakeOutline(Dictionary<string, float[]> arrays, UnityMesh orig, Payload glb, int n, int[] nn)
    {
        if (n == 0 || !arrays.ContainsKey("Color")) return null;   // no outline channel on this target

        if (GeometryUnchanged(glb.Mesh, orig))
        {
            if (orig.Has("Color")) arrays["Color"] = (float[])orig.Channels["Color"].Clone();
            if (orig.Has("Tangent")) arrays["Tangent"] = (float[])orig.Channels["Tangent"].Clone();
            return null;
        }

        // Can't encode a direction without position + normal + tangent. The nearest-original fill ships,
        // which is stale for edited geometry, so say so rather than pass it off as a bake.
        const string noFrame = "outline not re-baked: the payload has no normal/tangent frame to encode " +
                               "against. Vertex Color was carried from the nearest original instead. " +
                               "Export with normals + tangents (Send to Lab does) for a correct outline.";
        if (!arrays.TryGetValue("Normal", out var nrm) || !arrays.TryGetValue("Tangent", out var tan)
            || !arrays.TryGetValue("Vertex", out var pos))
            return noFrame;

        int nd = nrm.Length / n, td = tan.Length / n, cd = arrays["Color"].Length / n;
        if (nd < 3 || td < 3 || cd < 3) return noFrame;

        // hair/face keep authored data on top of their geometric base — carry, don't bake (see the summary)
        bool carry = IsNonGeometricOutline(orig.Name)
                     && orig.Has("Color") && orig.Has("Normal") && orig.Has("Tangent");
        var smoothed = carry ? null : SmoothNormalsByPosition(pos, nrm, nd, n, glb.Submeshes);
        var oCol = carry ? orig.Channels["Color"] : null; int ocd = carry ? orig.Dims["Color"] : 0;
        var oNrm = carry ? orig.Channels["Normal"] : null; int ond = carry ? orig.Dims["Normal"] : 0;
        var oTan = carry ? orig.Channels["Tangent"] : null; int otd = carry ? orig.Dims["Tangent"] : 0;

        var color = arrays["Color"];
        for (int v = 0; v < n; v++)
        {
            // the nearest original had its outline disabled (white) → keep it disabled
            if (color[v * cd] > 0.9f && color[v * cd + 1] > 0.9f && color[v * cd + 2] > 0.9f) continue;

            var N = new Vector3(nrm[v * nd], nrm[v * nd + 1], nrm[v * nd + 2]);
            var T = new Vector4(tan[v * td], tan[v * td + 1], tan[v * td + 2], td >= 4 ? tan[v * td + 3] : 1f);

            Vector3 s;
            if (carry)
            {
                int o = nn[v];
                var oN = new Vector3(oNrm![o * ond], oNrm[o * ond + 1], oNrm[o * ond + 2]);
                var oT = new Vector4(oTan![o * otd], oTan[o * otd + 1], oTan[o * otd + 2], otd >= 4 ? oTan[o * otd + 3] : 1f);
                s = DecodeOutlineNormal(oCol![o * ocd], oCol[o * ocd + 1], oCol[o * ocd + 2], oN, oT);
            }
            else s = smoothed![v];

            var (r, g, b) = EncodeOutlineNormal(s, N, T);
            color[v * cd] = r; color[v * cd + 1] = g; color[v * cd + 2] = b;   // .a (width) kept from the fill
        }
        return null;
    }

    /// <summary>Hair and face meshes carry authored outline data on top of their geometric base (hair =
    /// angle-weighted smoothing + hand-fixed patches; faces add authored interior detail; pre-rework assets
    /// store object-space spherized directions), so the bake carries their shipped outline rather than
    /// recomputing a surface normal over it. Detected by the corpus part-naming convention.</summary>
    private static bool IsNonGeometricOutline(string name) =>
        name.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("face", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the authored mesh IS the original across everything the transport carries:
    /// positions, normals, tangents, UV0, and the triangle lists. Positions alone are NOT enough — a
    /// normals-, tangent-, UV- or topology-only edit also invalidates the stored outline↔tangent pairing and
    /// must take the re-bake path. The tolerance is RELATIVE (1e-6 of magnitude, floored at 1e-6 absolute)
    /// so it absorbs transport float noise on any scale: a tiled UV in the tens has ulps larger than an
    /// absolute 1e-6. Triangle indices compare exactly.
    ///
    /// <para>Both sides are read PER VERTEX INDEX, which is what the byte-restore needs — it clones the
    /// original's channels into the built arrays slot for slot — and what makes this the rule for a payload
    /// compiled against the very mesh it was exported from. It is not the rule for a file that came back
    /// through a glTF re-export, whose vertex buffer is re-split and reordered
    /// (<see cref="SendBackGeometry.SameContent"/>).</para></summary>
    internal static bool GeometryUnchanged(UnityMesh authored, UnityMesh orig)
    {
        if (orig.VertexCount != authored.VertexCount) return false;
        foreach (var ch in new[] { "Vertex", "Normal", "Tangent", "TexCoord0" })
        {
            if (authored.Has(ch) != orig.Has(ch)) return false;
            if (!authored.Has(ch)) continue;
            var a = authored.Channels[ch]; var b = orig.Channels[ch];
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (MathF.Abs(a[i] - b[i]) > 1e-6f * MathF.Max(1f, MathF.Max(MathF.Abs(a[i]), MathF.Abs(b[i]))))
                    return false;
        }
        if (authored.Submeshes.Count != orig.Submeshes.Count) return false;
        for (int s = 0; s < authored.Submeshes.Count; s++)
        {
            var a = authored.Submeshes[s]; var b = orig.Submeshes[s];
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>The smoothed outline normal per vertex: the <b>area-weighted average of the face normals</b>
    /// of every triangle touching the vertex's position, welded across every vertex sharing that exact
    /// position (UV-seam / hard-edge split copies) and oriented outward. Welding is what keeps the outline
    /// continuous across those splits. On outline-carrying vertices this reproduces the game's own direction
    /// to median 0.00°, p90 0.02° across 300k+ body/cloth/head/prop vertices. A vertex with no incident
    /// triangle falls back to its own normal.</summary>
    internal static Vector3[] SmoothNormalsByPosition(float[] pos, float[] nrm, int nd, int n, IReadOnlyList<int[]> submeshes)
    {
        static (float, float, float) Key(float x, float y, float z) =>
            (x == 0f ? 0f : x, y == 0f ? 0f : y, z == 0f ? 0f : z);   // collapse -0 so split copies bin together

        var groupId = new Dictionary<(float, float, float), int>();
        var gid = new int[n];
        for (int v = 0; v < n; v++)
        {
            var k = Key(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
            if (!groupId.TryGetValue(k, out var g)) groupId[k] = g = groupId.Count;
            gid[v] = g;
        }
        int ng = groupId.Count;
        var faceAcc = new Vector3[ng];   // Σ (cross = 2·area·unitFaceNormal) — area-weighting is automatic
        var vnAcc = new Vector3[ng];     // Σ vertex normals — the outward reference + the no-face fallback
        for (int v = 0; v < n; v++)
            vnAcc[gid[v]] += new Vector3(nrm[v * nd], nrm[v * nd + 1], nrm[v * nd + 2]);

        foreach (var tri in submeshes)
            for (int t = 0; t + 2 < tri.Length; t += 3)
            {
                int i0 = tri[t], i1 = tri[t + 1], i2 = tri[t + 2];
                if (i0 >= n || i1 >= n || i2 >= n) continue;
                var p0 = new Vector3(pos[i0 * 3], pos[i0 * 3 + 1], pos[i0 * 3 + 2]);
                var p1 = new Vector3(pos[i1 * 3], pos[i1 * 3 + 1], pos[i1 * 3 + 2]);
                var p2 = new Vector3(pos[i2 * 3], pos[i2 * 3 + 1], pos[i2 * 3 + 2]);
                var cross = Vector3.Cross(p1 - p0, p2 - p0);   // length = 2·area, direction = face normal
                faceAcc[gid[i0]] += cross; faceAcc[gid[i1]] += cross; faceAcc[gid[i2]] += cross;
            }

        var outN = new Vector3[n];
        for (int v = 0; v < n; v++)
        {
            int g = gid[v];
            var refN = SafeNormalize(vnAcc[g], new Vector3(nrm[v * nd], nrm[v * nd + 1], nrm[v * nd + 2]));
            if (faceAcc[g].LengthSquared() < 1e-20f) { outN[v] = refN; continue; }   // no incident face
            var s = SafeNormalize(faceAcc[g], refN);
            outN[v] = Vector3.Dot(s, refN) < 0 ? -s : s;   // orient outward (face winding is consistent per mesh)
        }
        return outN;
    }

    /// <summary>Encode a world direction into a vertex's tangent frame as the exact inverse of the shader's
    /// outline decode (<c>dir = rgb.x·T + rgb.y·B + rgb.z·N</c>, then <c>·0.5+0.5</c> into 0..1). The frame is
    /// the shader's OWN — the raw (un-orthogonalized) tangent, <c>B = cross(N,T)·tangent.w</c>, and N — so it
    /// is inverted with a 3×3 Cramer solve, not dot-products: dot-products are the inverse only on an
    /// orthonormal basis (T⊥N), and skewed frames do occur. A degenerate frame (tangent ∥ N) is
    /// rank-deficient — the decode can only reproduce the N-component — so the encode stores exactly that
    /// projection.</summary>
    internal static (float r, float g, float b) EncodeOutlineNormal(Vector3 smoothed, Vector3 normal, Vector4 tangent)
    {
        var N = SafeNormalize(normal, new Vector3(0, 0, 1));
        var T = SafeNormalize(new Vector3(tangent.X, tangent.Y, tangent.Z), AnyPerpendicular(N));
        var B = Vector3.Cross(N, T) * (tangent.W < 0 ? -1f : 1f);
        var S = SafeNormalize(smoothed, N);
        // Solve [T B N]·c = S. det = T·(B×N); Cramer replaces one column with S per coordinate.
        float det = Vector3.Dot(T, Vector3.Cross(B, N));
        if (MathF.Abs(det) < 1e-6f)
        {
            // Degenerate (tangent ∥ N): B = 0 and T = ±N, so the shader's decode spans only N. Storing S's
            // N-component with zero T/B decodes to exactly (S·N)·N; an invented orthonormal frame here would
            // encode coefficients the shader decodes into a DIFFERENT direction.
            return (0.5f, 0.5f, Vector3.Dot(S, N) * 0.5f + 0.5f);
        }
        float cx = Vector3.Dot(S, Vector3.Cross(B, N)) / det;
        float cy = Vector3.Dot(T, Vector3.Cross(S, N)) / det;
        float cz = Vector3.Dot(T, Vector3.Cross(B, S)) / det;
        return (cx * 0.5f + 0.5f, cy * 0.5f + 0.5f, cz * 0.5f + 0.5f);
    }

    /// <summary>Inverse of <see cref="EncodeOutlineNormal"/> and the shader's own decode: the linear
    /// combination in the raw <c>{T, cross(N,T)·w, N}</c> basis.</summary>
    internal static Vector3 DecodeOutlineNormal(float r, float g, float b, Vector3 normal, Vector4 tangent)
    {
        var N = SafeNormalize(normal, new Vector3(0, 0, 1));
        var T = SafeNormalize(new Vector3(tangent.X, tangent.Y, tangent.Z), AnyPerpendicular(N));
        var B = Vector3.Cross(N, T) * (tangent.W < 0 ? -1f : 1f);
        return (r * 2f - 1f) * T + (g * 2f - 1f) * B + (b * 2f - 1f) * N;
    }

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float len = v.Length();
        return len > 1e-8f ? v / len : fallback;
    }

    private static Vector3 AnyPerpendicular(Vector3 n) =>
        Vector3.Normalize(Vector3.Cross(MathF.Abs(n.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, n));
}
