using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using AssetsTools.NET;
using Remold.Core.Bundles;
using Remold.Core.Mesh;

namespace Remold.Core.Migoto;

/// <summary>
/// Dump a skinned bundle mesh into the raw buffers an offline 3DMigoto LBS build consumes. Each vertex
/// stream IS a GPU buffer and m_IndexBuffer IS the index buffer, sliced without decode — except the skin
/// stream, which goes out in the canonical float4/uint4 shape (<see cref="Mesh.SkinLayout"/>) so a mesh
/// stored at any influence width reads like any other. Emits into <c>outDir</c>:
///   stream0.buf / stream1.buf / stream2.buf   raw stream slices (pos·nrm·tan / color·uv / weights·indices)
///   ib.buf                                     raw m_IndexBuffer bytes
///   bindpose.json                              per-bone bindpose(16) + rest pivot(3)
///   meta.json                                  stream manifest + counts + index format + submeshes
/// Operates on already-deobfuscated bundle bytes.
/// </summary>
public static class StreamDump
{
    public readonly record struct StreamInfo(int Stream, int Stride, int Bytes);

    public readonly record struct Result(
        string Mesh, int VertexCount, int BoneCount, int IndexFormat, int IndexBytes,
        IReadOnlyList<StreamInfo> Streams, string OutDir);

    /// <summary>Why a mesh's geometry is not offered for Replace. The kinds are separate answers because
    /// they are separate facts about the mesh, and a caller phrasing them for the modder says different
    /// things about each. The first two are the halves of the recoverable-skin rule
    /// (<see cref="UnrecoverableSkin"/>); <see cref="SpringRig"/> is the gate's own rule, asked of the
    /// bone set rather than the skin stream.</summary>
    public enum SkinRefusal
    {
        /// <summary>The mesh carries blend shapes, so its posed vertices aren't pure LBS.</summary>
        BlendShapes,

        /// <summary>The skin stream carries no per-vertex influences recovery can read: it is absent, or it
        /// is spelled in a shape no lossless widening brings to float4 weights + uint4 indices.</summary>
        SkinLayout,

        /// <summary>The mesh is skinned to a runtime spring chain
        /// (<see cref="Skeleton.BoneTable.HasSpringChain"/>): the game simulates those bones on its own,
        /// so replacement geometry is withheld while retexture and hide stay open.</summary>
        SpringRig,
    }

    /// <summary>Why this mesh can't feed palette recovery, or null when it can, plus the blend-shape count on
    /// the <see cref="SkinRefusal.BlendShapes"/> branch (0 on the other). Recovery needs a skin stream it can
    /// read as float4 weights + uint4 indices (<see cref="Mesh.SkinLayout"/>, which admits every stored
    /// influence width that widens into that shape losslessly) and pure-LBS posing: static layouts carry no
    /// such stream, and a blend-shape mesh's morphs would be absorbed as bone error. THE one home for the
    /// rule — every phrasing of it derives from here.</summary>
    public static (SkinRefusal Kind, int BlendShapes)? UnrecoverableSkin(AssetTypeValueField meshField)
    {
        var shapesField = meshField["m_Shapes"];
        int shapes = shapesField.IsDummy ? 0 : shapesField["shapes"]["Array"].Children.Count;
        if (shapes > 0) return (SkinRefusal.BlendShapes, shapes);
        return Mesh.SkinLayout.Recoverable(meshField) ? null : (SkinRefusal.SkinLayout, 0);
    }

    /// <summary>How a replacement of a mesh's geometry reaches the screen.</summary>
    public enum ReplaceRoute
    {
        /// <summary>The pooled swap: the game poses this draw per vertex, so the donor is skinned into a
        /// palette recovered from the captured vanilla draws.</summary>
        Pooled,

        /// <summary>A direct geometry swap: the draw carries no per-vertex posing to reproduce, so the
        /// compiled donor streams stand in for the vanilla ones and nothing is recovered.</summary>
        Rigid,
    }

    /// <summary>The route a Replace on this mesh takes, or null when its geometry can't be replaced at all.
    /// A mesh that can feed palette recovery is <see cref="ReplaceRoute.Pooled"/> — whatever width its skin
    /// is stored at, its draws are posed by exactly what the stream carries. Of the ones that can't, a mesh
    /// storing NO influences is <see cref="ReplaceRoute.Rigid"/>: nothing per vertex to reproduce. Blend
    /// shapes are refused, since their morphs are posing no geometry swap reproduces; so is a skin spelled
    /// in a shape recovery can't read, which IS posed per vertex and needs exactly the recovery its stream
    /// can't feed. THE one home for the routing rule.</summary>
    public static ReplaceRoute? Route(AssetTypeValueField meshField) =>
        UnrecoverableSkin(meshField) switch
        {
            null => ReplaceRoute.Pooled,
            (SkinRefusal.BlendShapes, _) => null,
            _ => StoredInfluences(meshField) == 0 ? ReplaceRoute.Rigid : null,
        };

    /// <summary>The mesh's stored per-vertex influence count: BlendWeight's stored dimension, or
    /// BlendIndices' when the layout stores indices alone (each weight implicitly 1), and 0 with neither
    /// channel — a static mesh. A channel's dimension byte carries the semantic count in its high nibble,
    /// so only the low one is the storage width.</summary>
    internal static int StoredInfluences(AssetTypeValueField meshField)
    {
        var ch = meshField["m_VertexData"]["m_Channels"]["Array"].Children;
        int Dim(int i) => i < ch.Count ? ch[i]["dimension"].AsByte & 0xF : 0;
        int weights = Dim(12);
        return weights > 0 ? weights : Dim(13);
    }

    /// <summary><see cref="UnrecoverableSkin"/> phrased for the build log, or null when the mesh can feed
    /// palette recovery. Every stored influence count 1–4 is one recovery accepts, so a layout refusal
    /// with influences present is about the shape they are spelled in — an index format, a stream, a
    /// channel sharing the stream — and only a mesh storing none is refused for the count itself.</summary>
    public static string? UnrecoverableSkinReason(AssetTypeValueField meshField) =>
        UnrecoverableSkin(meshField) switch
        {
            (SkinRefusal.BlendShapes, var n) => $"it carries {n} blend shapes (its posed vertices aren't pure LBS)",
            (SkinRefusal.SkinLayout, _) => StoredInfluences(meshField) == 0
                ? "it carries no skin stream (a rigid layout)"
                : "it carries a skin stream recovery can't read",
            _ => null,
        };

    /// <summary>The bone hashes this mesh's skin actually POSES: the ones carrying nonzero summed vertex
    /// weight. A hash listed in <c>m_BoneNameHashes</c> with no weight behind it moves no vertex, so the
    /// mesh asks a palette for nothing at that bone. The sum is the same one
    /// <see cref="PoolMath.BuildUnion"/> assigns ownership by and <see cref="MigotoEmitter.SummedWeights"/> reads
    /// off a dumped skin stream, so a caller reasoning about whether a bone's palette row would be WRITTEN
    /// reads the same quantity the writer does. Three sources, one quantity: they must agree, or a mesh's
    /// bones would be admitted by one gate and refused by another.
    ///
    /// <para>Throws <see cref="InvalidDataException"/> for a mesh the skin rule already refuses: without a
    /// skin stream <see cref="Mesh.SkinLayout"/> can read as float4 weights + uint4 indices there is nothing
    /// to sum.</para></summary>
    public static HashSet<uint> WeightedBoneHashes(AssetTypeValueField meshField)
    {
        if (UnrecoverableSkinReason(meshField) is { } why)
            throw new InvalidDataException($"the mesh's skin weights can't be read: {why}");
        var skin = MeshSkin.Decode(meshField);
        var s2 = Mesh.SkinLayout.Canonical(meshField);
        const int stride = Mesh.SkinLayout.CanonicalStride;
        var summed = new double[skin.BoneCount];
        for (int o = 0; o + stride <= s2.Length; o += stride)
            for (int k = 0; k < 4; k++)
            {
                float w = BitConverter.ToSingle(s2, o + k * 4);
                if (w <= 0) continue;
                uint local = BitConverter.ToUInt32(s2, o + 16 + k * 4);
                if (local < (uint)summed.Length) summed[local] += w;
            }
        var weighted = new HashSet<uint>();
        for (int b = 0; b < summed.Length; b++)
            if (summed[b] > 0) weighted.Add(skin.BoneHashes[b]);
        return weighted;
    }

    /// <summary><paramref name="reader"/> lets a caller dumping several meshes out of one bundle share the
    /// parse; null opens the bundle for this call alone.</summary>
    public static Result Dump(byte[] deobfuscatedBundle, string meshName, string outDir, long pathId = 0,
        BundleReader? reader = null)
    {
        var field = (reader ?? new BundleReader()).GetMeshField(deobfuscatedBundle, meshName, pathId)
            ?? throw new InvalidDataException($"mesh '{meshName}' not found in bundle");
        if (UnrecoverableSkinReason(field) is { } why)
            throw new InvalidDataException($"mesh '{meshName}' can't feed palette recovery: {why}");

        var mesh = MeshRaw.From(field);
        var skin = MeshSkin.Decode(field);
        Directory.CreateDirectory(outDir);

        // the skin stream goes out in the canonical shape recovery reads, whatever width the mesh stores it
        // at; every other stream is a verbatim slice
        var skinBytes = Mesh.SkinLayout.Canonical(field, mesh);
        for (int s = 0; s < mesh.StreamIds.Count; s++)
            File.WriteAllBytes(Path.Combine(outDir, $"stream{mesh.StreamIds[s]}.buf"),
                mesh.StreamIds[s] == Mesh.SkinLayout.SkinStream ? skinBytes : mesh.StreamBytes(s));
        File.WriteAllBytes(Path.Combine(outDir, "ib.buf"), mesh.Index);

        // bindpose.json — bindpose(16, row-major) + rest pivot (translation of the inverse bind pose)
        var sb = new StringBuilder();
        sb.Append("{\n  \"boneCount\": ").Append(skin.BoneCount).Append(",\n  \"bones\": [\n");
        for (int b = 0; b < skin.BoneCount; b++)
        {
            var bp = skin.BindPoses[b];
            Matrix4x4.Invert(bp, out var restW);
            var rp = restW.Translation;
            float[] mm = { bp.M11, bp.M12, bp.M13, bp.M14, bp.M21, bp.M22, bp.M23, bp.M24,
                           bp.M31, bp.M32, bp.M33, bp.M34, bp.M41, bp.M42, bp.M43, bp.M44 };
            sb.Append("    { \"hash\": ").Append(skin.BoneHashes[b])
              .Append(", \"rest\": [").Append(F(rp.X)).Append(',').Append(F(rp.Y)).Append(',').Append(F(rp.Z))
              .Append("], \"bindpose\": [").Append(string.Join(",", Array.ConvertAll(mm, F))).Append("] }")
              .Append(b + 1 < skin.BoneCount ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        File.WriteAllText(Path.Combine(outDir, "bindpose.json"), sb.ToString());

        // meta.json — stream manifest + counts + index format + submeshes
        var meta = new StringBuilder();
        meta.Append("{\n");
        meta.Append($"  \"mesh\": \"{meshName}\", \"verts\": {mesh.VertexCount}, \"boneCount\": {skin.BoneCount},\n");
        meta.Append($"  \"indexFormat\": \"{(mesh.IndexFormat == 0 ? "R16_UINT" : "R32_UINT")}\", \"indexBufferBytes\": {mesh.Index.Length},\n");
        // the emitted stride, which is the canonical one wherever the skin stream widened
        int Emitted(int s) => mesh.StreamIds[s] == Mesh.SkinLayout.SkinStream
            ? Mesh.SkinLayout.CanonicalStride : mesh.Stride(s);
        meta.Append("  \"streams\": [");
        for (int s = 0; s < mesh.StreamIds.Count; s++)
            meta.Append(s > 0 ? ", " : "").Append($"{{ \"stream\": {mesh.StreamIds[s]}, \"stride\": {Emitted(s)} }}");
        meta.Append("],\n  \"submeshes\": [");
        for (int s = 0; s < mesh.Submeshes.Count; s++)
            meta.Append(s > 0 ? ", " : "").Append($"{{ \"firstByte\": {mesh.Submeshes[s].FirstByte}, \"indexCount\": {mesh.Submeshes[s].IndexCount}, \"baseVertex\": {mesh.Submeshes[s].BaseVertex} }}");
        meta.Append("]\n}\n");
        File.WriteAllText(Path.Combine(outDir, "meta.json"), meta.ToString());

        var streams = new List<StreamInfo>();
        for (int s = 0; s < mesh.StreamIds.Count; s++)
            streams.Add(new StreamInfo(mesh.StreamIds[s], Emitted(s), mesh.VertexCount * Emitted(s)));
        return new Result(meshName, mesh.VertexCount, skin.BoneCount, mesh.IndexFormat, mesh.Index.Length, streams, outDir);
    }

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
}
