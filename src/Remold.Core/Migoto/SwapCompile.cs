using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Remold.Core.Bundles;
using Remold.Core.Mesh;

namespace Remold.Core.Migoto;

/// <summary>
/// Compile NEW geometry (a Blender glb weighted to the target's armature — see
/// <see cref="SwapReference"/>) ONTO the target part, then emit
/// the raw GPU streams a 3DMigoto swap consumes: ImportPayload → MeshApply.Apply → MeshRaw.From, which
/// slices the result into stream0/1/2 + ib in the target's stride layout and bone order.
///
/// <para><see cref="CompilePool"/> targets the pooled UNION bone order, built FIRST-SEEN over the pool parts
/// in argument order (<see cref="BuildUnionOrder"/>, the single union-order authority). The LAYOUT-TARGET
/// part — the pipeline's ANCHOR, whose input layout the compiled streams must match, since parts differ
/// (stride-20 vs -28 vs -32 stream1) — is the layout/outline-conform target, with its
/// m_BoneNameHashes/m_BindPose overwritten by the union. The union's BIND SPACE is a separate choice:
/// scene-rest space when the anchor's measured rest is a snapped rotation, else the anchor's own (see
/// <see cref="BuildUnionOrder"/>). It additionally emits <c>unionorder.json</c>.</para>
///
/// The caller resolves which bundle; this operates on already-deobfuscated bytes.
/// </summary>
public static class SwapCompile
{
    public readonly record struct StreamInfo(int Stream, int Stride);

    /// <summary><paramref name="Warnings"/> are user-facing: the authored edit won't show or will look
    /// wrong, and there is something the author can do about it. <paramref name="Diagnostics"/> record what
    /// the compile did; they reach the build log and no UI surface. Both are forwarded from
    /// <see cref="MeshApply"/>.</summary>
    public readonly record struct Result(
        string Mesh, int VertexCount, int IndexFormat, int IndexBytes, int SubmeshCount,
        int UnionBones, IReadOnlyList<StreamInfo> Streams, IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Diagnostics, string OutDir);

    /// <summary>Compile <paramref name="weightedGlb"/> onto a single target part and emit
    /// stream*.buf + ib.buf + meta.json into <paramref name="outDir"/> (target bone order, outline baked,
    /// target layout). No union: the streams stand in for THIS part's own vanilla ones, so its own layout
    /// is the one they must match.
    ///
    /// <para>A target with no bone table takes the geometry-only compile — there is no bone order to map an
    /// authored skin onto, and a static draw has no per-vertex posing to carry one for.</para></summary>
    /// <param name="payload">The already-imported donor, when the caller has one in hand. The compile
    /// CONSUMES it (see <see cref="CompilePool"/>). Null imports the glb here.</param>
    /// <param name="reader">Shares one parse of the bundle with the caller's other reads; null opens it for
    /// this call alone.</param>
    public static Result CompilePart(byte[] deobfuscatedBundle, string meshName, string weightedGlb,
        string outDir, long pathId = 0, MeshApply.Payload? payload = null, BundleReader? reader = null)
    {
        if (!File.Exists(weightedGlb)) throw new FileNotFoundException($"weighted glb not found: {weightedGlb}");
        var field = (reader ?? new BundleReader()).GetMeshField(deobfuscatedBundle, meshName, pathId)
            ?? throw new InvalidDataException($"mesh '{meshName}' not found in bundle");

        // compile onto the target's format (mutates field in place), then slice raw streams
        payload ??= MeshGltf.ImportPayload(weightedGlb, lenient: true);   // tolerate morph-accessor quirks
        var apply = MeshSkin.Decode(field).BoneCount > 0
            ? MeshApply.Apply(field, payload)
            : MeshApply.ApplyGeometry(field, payload);
        var mesh = MeshRaw.From(field);
        Directory.CreateDirectory(outDir);

        WriteStreams(mesh, outDir);
        WriteMeta(outDir, $"{meshName}.swap", unionBones: null, mesh);
        return BuildResult($"{meshName}.swap", mesh, unionBones: 0, apply.Warnings, apply.Diagnostics, outDir);
    }

    /// <summary>Compile a whole-body donor <paramref name="weightedGlb"/> onto the pooled union of
    /// <paramref name="meshNames"/> and emit stream*.buf + ib.buf + meta.json + unionorder.json (UNION
    /// bone order) into <paramref name="outDir"/>.</summary>
    public static Result CompilePool(byte[] deobfuscatedBundle, IReadOnlyList<string> meshNames,
        string weightedGlb, string outDir) =>
        CompilePool(meshNames.Select(n => new PoolMesh(deobfuscatedBundle, n)).ToList(), weightedGlb, outDir);

    /// <summary>One pool part's mesh identity for the per-part-bundle overload: the (already
    /// deobfuscated, caller-FORWARD-resolved) bundle holding it, its <c>m_Name</c>, and the optional
    /// exact path-id selector (smr-backed parts). <paramref name="MeasuredRest"/> is the part's measured
    /// bind→scene transform when its scene rig read consistent (see
    /// <see cref="Skeleton.SceneRig.MeasuredRest"/>) — what lets the union restate this part in the
    /// anchor's space without fitting a delta to shared bones.</summary>
    public readonly record struct PoolMesh(byte[] DeobfuscatedBundle, string MeshName, long PathId = 0,
        Matrix4x4? MeasuredRest = null);

    /// <summary>The general pool compile: parts may live in DIFFERENT bundles — a cross-prefix part resolves
    /// to another bundle, and hand-feeding one bundle for all parts is the twin-bundle trap forward
    /// resolution kills. Union order stays first-seen in argument order.</summary>
    /// <param name="payload">The already-imported donor, when the caller has one in hand. The compile
    /// CONSUMES it — applying a payload may rewrite its vertex-colour channel in place — so a payload is
    /// handed to one compile and not reused after. Null imports the glb here.</param>
    /// <param name="reader">Shares one parse of a bundle across the pool parts that live in it; null opens
    /// each bundle for this call alone.</param>
    public static Result CompilePool(IReadOnlyList<PoolMesh> meshes, string weightedGlb, string outDir,
        int layoutTargetIndex = 0, MeshApply.Payload? payload = null, BundleReader? reader = null)
    {
        if (!File.Exists(weightedGlb)) throw new FileNotFoundException($"weighted glb not found: {weightedGlb}");
        if (layoutTargetIndex < 0 || layoutTargetIndex >= meshes.Count)
            throw new ArgumentOutOfRangeException(nameof(layoutTargetIndex),
                $"layout target {layoutTargetIndex} is outside the pool (0..{meshes.Count - 1})");

        reader ??= new BundleReader();
        var meshNames = meshes.Select(m => m.MeshName).ToList();
        var fields = new List<AssetTypeValueField>();
        foreach (var m in meshes)
        {
            var f = reader.GetMeshField(m.DeobfuscatedBundle, m.MeshName, m.PathId)
                ?? throw new InvalidDataException($"mesh '{m.MeshName}' not found in bundle");
            fields.Add(f);
        }

        var (unionHashes, unionBind) = BuildUnionOrder(fields, meshNames, layoutTargetIndex,
            meshes.Select(m => m.MeasuredRest).ToList());

        // ---- rewrite the layout-target field's bone table to the union, then run the proven pipeline ----
        var target = fields[layoutTargetIndex];
        // The compile encodes the authored skin against the TARGET's stored layout, so a one-influence
        // anchor would reduce the donor to its single influence and slice a narrow skin stream out the far
        // end. Widen it first: the emitted stream is then the float4/uint4 shape the pooled machinery reads.
        SkinLayout.Widen(target);
        var hArr = Arr(target["m_BoneNameHashes"]);
        var bArr = Arr(target["m_BindPose"]);
        hArr.Children.Clear();
        bArr.Children.Clear();
        for (int u = 0; u < unionHashes.Count; u++)
        {
            var he = ValueBuilder.DefaultValueFieldFromArrayTemplate(hArr);
            he.AsUInt = unionHashes[u];
            hArr.Children.Add(he);
            var be = ValueBuilder.DefaultValueFieldFromArrayTemplate(bArr);
            var leaves = FloatLeaves(be).ToList();
            if (leaves.Count != 16) throw new InvalidDataException($"bindpose template has {leaves.Count} float leaves");
            for (int i = 0; i < 16; i++) leaves[i].AsFloat = unionBind[u][i];
            bArr.Children.Add(be);
        }

        payload ??= MeshGltf.ImportPayload(weightedGlb, lenient: true);
        var result = MeshApply.Apply(target, payload);
        var mesh = MeshRaw.From(target);
        Directory.CreateDirectory(outDir);

        WriteStreams(mesh, outDir);
        WriteMeta(outDir, "pool.swap", unionHashes.Count, mesh);
        File.WriteAllText(Path.Combine(outDir, "unionorder.json"),
            "[" + string.Join(",", unionHashes.Select(h => $"\"{h}\"")) + "]\n");
        return BuildResult("pool.swap", mesh, unionHashes.Count, result.Warnings, result.Diagnostics, outDir);
    }

    /// <summary>Build the union bone order first-seen across <paramref name="fields"/> in argument order. A
    /// repeated hash must carry a byte-consistent bindpose (asserted within 1e-5), since the pooled union
    /// keeps ONE bindpose per bone. Returns the ordered hashes and their 16-float raw bindposes, copied from
    /// the first part defining each bone. The single union-order authority: the emitted indices line up with
    /// any consumer given the SAME parts in the SAME order.
    ///
    /// <para>The bind SPACE is a separate, explicit choice from that order: <paramref name="referenceIndex"/>
    /// names the anchor part, and the union is stated in SCENE-REST space when the anchor's measured rest
    /// relates the two by a snapped rotation (<see cref="TrySceneDelta"/>), else in the anchor's own
    /// space. Scene-rest space is a property of the subject rather than of any one Replace, so two
    /// pipelines pooling one dump state it identically — what lets two mesh edits on one subject build
    /// together. A part is restated first by the delta the parts' MEASURED scene rests compose when both
    /// carry one (<paramref name="measuredRests"/> — no shared bones needed), else by a delta fitted and
    /// corroborated over the bones it shares with the reference (see <see cref="Mesh.BindSpace"/>). The
    /// refusal below then covers only the differences neither could explain, which must keep refusing
    /// rather than deform geometry on a bone-hash coincidence.</para></summary>
    public static (List<uint> Hashes, List<float[]> BindPoses) BuildUnionOrder(
        IReadOnlyList<AssetTypeValueField> fields, IReadOnlyList<string> names, int referenceIndex = 0,
        IReadOnlyList<Matrix4x4?>? measuredRests = null)
    {
        // per part: bone hashes + 16 raw bindpose floats each, in Unity declaration order
        var partHashes = new List<List<uint>>();
        var partBinds = new List<List<float[]>>();
        for (int pi = 0; pi < fields.Count; pi++)
        {
            var hashes = Arr(fields[pi]["m_BoneNameHashes"]).Children.Select(c => c.AsUInt).ToList();
            var binds = Arr(fields[pi]["m_BindPose"]).Children;
            if (hashes.Count != binds.Count)
                throw new InvalidDataException($"{names[pi]}: {hashes.Count} bone hashes vs {binds.Count} bindposes");
            var raws = new List<float[]>(hashes.Count);
            for (int b = 0; b < hashes.Count; b++)
            {
                var raw = FloatLeaves(binds[b]).Select(l => l.AsFloat).ToArray();
                if (raw.Length != 16) throw new InvalidDataException($"{names[pi]} bone {b}: bindpose has {raw.Length} floats");
                raws.Add(raw);
            }
            partHashes.Add(hashes);
            partBinds.Add(raws);
        }

        RebaseToReference(partHashes, partBinds, referenceIndex, measuredRests);

        var unionHashes = new List<uint>();
        var unionBind = new List<float[]>();                       // 16 raw floats, declaration order
        var slotOf = new Dictionary<uint, int>();
        for (int pi = 0; pi < partHashes.Count; pi++)
            for (int b = 0; b < partHashes[pi].Count; b++)
            {
                var raw = partBinds[pi][b];
                if (slotOf.TryGetValue(partHashes[pi][b], out var slot))
                {
                    var d0 = unionBind[slot].Zip(raw, (a, x) => Math.Abs(a - x)).Max();
                    if (d0 > 1e-5f)
                        throw new InvalidDataException($"bone {partHashes[pi][b]} bind pose differs across pool parts " +
                            $"(max diff {d0:g4}); no measured or corroborated rigid rotation relates the two spaces, " +
                            "so the part can't be converted into the reference part's space");
                    continue;
                }
                slotOf[partHashes[pi][b]] = unionHashes.Count;
                unionHashes.Add(partHashes[pi][b]);
                unionBind.Add(raw);
            }
        return (unionHashes, unionBind);
    }

    /// <summary>Restate every part authored in a different bind space in the reference's, in place. The
    /// delta comes from the parts' MEASURED scene rests when both carry one — a composition of two
    /// measurements needs no shared-bone corroboration, so it reaches a part sharing a single bone with the
    /// reference — else it is fitted and corroborated over the shared bones
    /// (<see cref="BindSpace.MinSharedBones"/>). A part neither route can relate rigidly is left alone for
    /// the union gate to judge.
    ///
    /// <para>When the reference part's own rest is a snapped rotation (<see cref="TrySceneDelta"/>), the
    /// whole set then restates once more into SCENE-REST space. That space is a property of the subject,
    /// not of any one part, so every Replace on the subject states its union there and a dump two
    /// pipelines share converts the same way in both. The extra restatement is one signed permutation
    /// applied uniformly, so every agreement and every refusal the gates would have read is
    /// preserved.</para></summary>
    static void RebaseToReference(List<List<uint>> partHashes, List<List<float[]>> partBinds, int referenceIndex,
        IReadOnlyList<Matrix4x4?>? measuredRests = null)
    {
        if (referenceIndex < 0 || referenceIndex >= partHashes.Count) return;
        var referenceOf = new Dictionary<uint, Matrix4x4>();
        for (int b = 0; b < partHashes[referenceIndex].Count; b++)
            referenceOf[partHashes[referenceIndex][b]] = BindSpace.FromUnityFloats(partBinds[referenceIndex][b]);

        for (int pi = 0; pi < partHashes.Count; pi++)
        {
            if (pi == referenceIndex) continue;
            if (TryMeasuredDelta(measuredRests, pi, referenceIndex, out var measured))
            {
                if (measured is { } md) Rebase(pi, md);
                continue;   // measured answer, including "same space" — the fitted path has nothing to add
            }
            var shared = new List<(Matrix4x4, Matrix4x4)>();
            for (int b = 0; b < partHashes[pi].Count; b++)
                if (referenceOf.TryGetValue(partHashes[pi][b], out var r))
                    shared.Add((BindSpace.FromUnityFloats(partBinds[pi][b]), r));
            if (BindSpace.Delta(shared) is not { } d) continue;
            Rebase(pi, d);
        }

        if (measuredRests is not null && referenceIndex < measuredRests.Count
            && TrySceneDelta(measuredRests[referenceIndex], out var scene))
            for (int pi = 0; pi < partBinds.Count; pi++)
                Rebase(pi, scene);

        void Rebase(int pi, Matrix4x4 d)
        {
            for (int b = 0; b < partBinds[pi].Count; b++)
                partBinds[pi][b] = BindSpace.ToUnityFloats(
                    BindSpace.Rebase(BindSpace.FromUnityFloats(partBinds[pi][b]), d));
        }
    }

    /// <summary>The reference part's own restatement into SCENE-REST space: true with the snapped
    /// rotation when the rest is a real quarter-turn bake, false when there is nothing to restate —
    /// no measured rest, a rest already ≈identity (bind space IS scene space), or a rest that is not a
    /// pure rotation (a translated placement), where the union must stay in the reference part's own
    /// space rather than deform on a partial relation. The same verdict decides whether the donor
    /// payload keeps its exported scene-space floats or un-bakes to the reference part's bind space, so
    /// the compiled streams and the palette state one space between them.</summary>
    internal static bool TrySceneDelta(Matrix4x4? rest, out Matrix4x4 delta)
    {
        delta = default;
        if (rest is not { } g) return false;
        if (RestBake.RotationDiff(g, Matrix4x4.Identity) <= 1e-3f
            && RestBake.TranslationDiff(g, Matrix4x4.Identity) <= 1e-2f) return false;
        if (RestBake.Snap(g) is not { } s) return false;
        delta = s;
        return true;
    }

    /// <summary>The part→reference delta the two parts' measured scene rests compose:
    /// <c>D = G_part · inv(G_ref)</c> (row-vector; each part shows as <c>G·v</c> in the scene, so equal
    /// scene positions relate their bind spaces by exactly this). False when either rest is unknown — the
    /// fitted path decides then. True answers even with a null <paramref name="delta"/>: ≈identity means
    /// the spaces already agree, and a delta that won't snap (a real translation, e.g. a weapon mount) is
    /// not a pure bind-space rotation and must reach the union gate's refusal rather than the fitted path —
    /// the measurement has already ruled a rigid-rotation relation out.</summary>
    internal static bool TryMeasuredDelta(IReadOnlyList<Matrix4x4?>? rests, int part, int reference,
        out Matrix4x4? delta)
    {
        delta = null;
        if (rests is null || part >= rests.Count || reference >= rests.Count) return false;
        if (rests[part] is not { } gp || rests[reference] is not { } gr) return false;
        if (!Matrix4x4.Invert(gr, out var grInv)) return false;
        var raw = gp * grInv;
        if (RestBake.RotationDiff(raw, Matrix4x4.Identity) > 1e-3f
            || RestBake.TranslationDiff(raw, Matrix4x4.Identity) > 1e-2f)
            delta = RestBake.Snap(raw);
        return true;
    }

    static void WriteStreams(MeshRaw mesh, string outDir)
    {
        for (int s = 0; s < mesh.StreamIds.Count; s++)
            File.WriteAllBytes(Path.Combine(outDir, $"stream{mesh.StreamIds[s]}.buf"), mesh.StreamBytes(s));
        File.WriteAllBytes(Path.Combine(outDir, "ib.buf"), mesh.Index);
    }

    static void WriteMeta(string outDir, string meshField, int? unionBones, MeshRaw mesh)
    {
        var meta = new StringBuilder();
        meta.Append("{\n");
        if (unionBones is int u)
            meta.Append($"  \"mesh\": \"{meshField}\", \"verts\": {mesh.VertexCount}, \"unionBones\": {u},\n");
        else
            meta.Append($"  \"mesh\": \"{meshField}\", \"verts\": {mesh.VertexCount},\n");
        meta.Append($"  \"indexFormat\": \"{(mesh.IndexFormat == 0 ? "R16_UINT" : "R32_UINT")}\", \"indexBufferBytes\": {mesh.Index.Length},\n");
        meta.Append("  \"streams\": [");
        for (int s = 0; s < mesh.StreamIds.Count; s++)
            meta.Append(s > 0 ? ", " : "").Append($"{{ \"stream\": {mesh.StreamIds[s]}, \"stride\": {mesh.Stride(s)} }}");
        meta.Append("],\n  \"submeshes\": [");
        for (int s = 0; s < mesh.Submeshes.Count; s++)
            meta.Append(s > 0 ? ", " : "").Append($"{{ \"firstByte\": {mesh.Submeshes[s].FirstByte}, \"indexCount\": {mesh.Submeshes[s].IndexCount}, \"baseVertex\": {mesh.Submeshes[s].BaseVertex} }}");
        meta.Append("]\n}\n");
        File.WriteAllText(Path.Combine(outDir, "meta.json"), meta.ToString());
    }

    static Result BuildResult(string meshName, MeshRaw mesh, int unionBones,
        IReadOnlyList<string> warnings, IReadOnlyList<string> diagnostics, string outDir)
    {
        var streams = new List<StreamInfo>();
        for (int s = 0; s < mesh.StreamIds.Count; s++)
            streams.Add(new StreamInfo(mesh.StreamIds[s], mesh.Stride(s)));
        return new Result(meshName, mesh.VertexCount, mesh.IndexFormat, mesh.Index.Length,
            mesh.Submeshes.Count, unionBones, streams, warnings, diagnostics, outDir);
    }

    private static AssetTypeValueField Arr(AssetTypeValueField f) => f["Array"];

    private static IEnumerable<AssetTypeValueField> FloatLeaves(AssetTypeValueField f)
    {
        if (f.TemplateField.ValueType is AssetValueType.Float) { yield return f; yield break; }
        foreach (var c in f.Children)
            foreach (var l in FloatLeaves(c)) yield return l;
    }
}
