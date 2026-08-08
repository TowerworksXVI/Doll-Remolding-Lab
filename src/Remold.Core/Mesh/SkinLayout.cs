using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Remold.Core.Mesh;

/// <summary>
/// The skin-stream shape palette recovery consumes — float4 BlendWeight at stream-2 offset 0, uint4
/// BlendIndices at offset 16 — and the widening that brings any stored influence width (1–4) into it.
///
/// <para>Meshes store skins at several widths: the full four, a two-influence pair, and two
/// one-influence spellings (BlendWeight and BlendIndices both stored x1, or BlendIndices alone with each
/// weight implicitly 1). Whatever the width, the stored influences ARE the mesh's whole skin — its draws
/// are posed by exactly what the stream carries — so padding the missing slots with zero weight is
/// lossless and the pooled pipeline reads the result like any other skinned mesh.</para>
///
/// <para>THE one home for the skin-stream shape: the readers, the layout half of the recoverable-skin
/// rule and the compile all ask here rather than each spelling out the channel arithmetic.</para>
/// </summary>
public static class SkinLayout
{
    /// <summary>The stream the skin channels live on corpus-wide.</summary>
    public const int SkinStream = 2;

    /// <summary>Stride of the canonical skin stream: 4 weights + 4 indices, 4 bytes each.</summary>
    public const int CanonicalStride = 32;

    private const int WeightChannel = 12, IndexChannel = 13;
    private const int Float32 = 0, UInt32Format = 10;

    private readonly record struct Chan(int Stream, int Offset, int Format, int Dim);

    private static List<Chan> Channels(AssetTypeValueField mesh) =>
        mesh["m_VertexData"]["m_Channels"]["Array"].Children
            // dimension's low nibble is the STORED component count; the high one is the semantic count
            .Select(c => new Chan(c["stream"].AsByte, c["offset"].AsByte, c["format"].AsByte,
                                  c["dimension"].AsByte & 0xF))
            .ToList();

    /// <summary>True when the mesh already presents float4 weights @0 + uint4 indices @16 on stream 2, with
    /// nothing else sharing that stream.</summary>
    public static bool IsCanonical(AssetTypeValueField mesh) => IsCanonical(Channels(mesh));

    private static bool IsCanonical(List<Chan> ch) =>
        ch.Count > IndexChannel
        && ch[WeightChannel] == new Chan(SkinStream, 0, Float32, 4)
        && ch[IndexChannel] == new Chan(SkinStream, 16, UInt32Format, 4)
        && !Shared(ch);

    /// <summary>True when a channel other than the two skin ones has storage on the skin stream. The stream
    /// is read (and written) whole at <see cref="CanonicalStride"/>, so a third channel there is both bytes
    /// a reader would take for weights or indices and bytes a widening would overwrite.</summary>
    private static bool Shared(List<Chan> ch)
    {
        for (int i = 0; i < ch.Count; i++)
            if (i != WeightChannel && i != IndexChannel && ch[i].Dim > 0 && ch[i].Stream == SkinStream)
                return true;
        return false;
    }

    /// <summary>True when the mesh stores exactly one influence per vertex — either narrow spelling.
    /// Not a readability question (<see cref="Recoverable"/> is): a one-influence part rides its bones at
    /// weight 1 on every vertex, which is what the pool rules key this flag for.</summary>
    public static bool IsNarrow(AssetTypeValueField mesh) => IsNarrow(Channels(mesh));

    private static bool IsNarrow(List<Chan> ch) =>
        IsWidenable(ch) && ch[IndexChannel].Dim == 1;

    /// <summary>True when <see cref="Widen"/> can bring the mesh's skin stream to the canonical shape:
    /// uint32 indices stored x1–x4 on stream 2, weights float32 at the same width alongside them (or
    /// absent with x1 indices, each weight implicitly 1), and nothing else sharing that stream. A
    /// canonical layout passes too — it widens to itself.</summary>
    private static bool IsWidenable(List<Chan> ch)
    {
        if (ch.Count <= IndexChannel) return false;
        var idx = ch[IndexChannel];
        var wgt = ch[WeightChannel];
        if (idx.Format != UInt32Format || idx.Stream != SkinStream || idx.Dim is < 1 or > 4) return false;
        if (wgt.Dim == 0
            ? idx.Dim != 1
            : wgt.Dim != idx.Dim || wgt.Format != Float32 || wgt.Stream != SkinStream) return false;
        return !Shared(ch);
    }

    /// <summary>True when the mesh's skin stream can feed palette recovery, as it stands or after
    /// <see cref="Widen"/>.</summary>
    public static bool Recoverable(AssetTypeValueField mesh)
    {
        var ch = Channels(mesh);
        return IsCanonical(ch) || IsWidenable(ch);
    }

    /// <summary>The mesh's skin stream in the canonical stride-32 shape: returned verbatim when the mesh
    /// already stores it, widened with zero-weight padding when it stores fewer influences per vertex.
    /// Throws <see cref="InvalidDataException"/> for a layout no widening can read — and for a
    /// canonical-channel mesh whose stream is stored at some other stride, whose records the returned
    /// bytes would not be.</summary>
    public static byte[] Canonical(AssetTypeValueField mesh) => Canonical(Channels(mesh), MeshRaw.From(mesh));

    /// <summary>The same answer for a caller that has already parsed <paramref name="mesh"/>'s vertex blob.
    /// <paramref name="raw"/> must be that mesh's own <see cref="MeshRaw"/>.</summary>
    public static byte[] Canonical(AssetTypeValueField mesh, MeshRaw raw) => Canonical(Channels(mesh), raw);

    private static byte[] Canonical(List<Chan> ch, MeshRaw raw)
    {
        int ordinal = raw.StreamIds.IndexOf(SkinStream);
        if (ordinal < 0) throw new InvalidDataException("the mesh carries no skin stream");
        var stored = raw.StreamBytes(ordinal);
        if (IsCanonical(ch))
            return raw.Stride(ordinal) == CanonicalStride ? stored
                : throw new InvalidDataException(
                    $"the mesh declares float4 weights + uint4 indices but stores its skin stream "
                    + $"{raw.Stride(ordinal)} bytes per vertex");
        if (!IsWidenable(ch))
            throw new InvalidDataException(
                "the mesh's skin stream isn't a weights + indices pair widening can read");

        int stride = raw.Stride(ordinal);
        int dim = ch[IndexChannel].Dim;
        bool hasWeight = ch[WeightChannel].Dim > 0;
        int wOff = ch[WeightChannel].Offset, iOff = ch[IndexChannel].Offset;
        var wide = new byte[(long)raw.VertexCount * CanonicalStride <= int.MaxValue
            ? raw.VertexCount * CanonicalStride
            : throw new InvalidDataException("mesh too large to widen its skin stream")];
        for (int v = 0; v < raw.VertexCount; v++)
        {
            int src = v * stride, dst = v * CanonicalStride;
            // the stored influences verbatim, the padding slots already zero from allocation
            if (hasWeight) Array.Copy(stored, src + wOff, wide, dst, dim * 4);
            else BitConverter.GetBytes(1f).CopyTo(wide, dst);
            Array.Copy(stored, src + iOff, wide, dst + 16, dim * 4);
        }
        return wide;
    }

    /// <summary>Rewrite a mesh's vertex data and channel table into the canonical skin shape, in place;
    /// false when the mesh has nothing to widen (already canonical, skinless, or a layout
    /// <see cref="Recoverable"/> refuses). Every other stream is copied through byte for byte.
    ///
    /// <para>For the compile: <see cref="MeshApply"/> encodes an authored skin against the target's OWN
    /// stored layout, so a target stored below four would crush the donor's influences down to its width
    /// and slice a narrow stream out the far end. Widening first is what lets the compiled streams come
    /// out in the shape the pooled machinery reads.</para></summary>
    public static bool Widen(AssetTypeValueField mesh)
    {
        var ch = Channels(mesh);
        if (IsCanonical(ch) || !IsWidenable(ch)) return false;

        var raw = MeshRaw.From(mesh);
        var wide = Canonical(ch, raw);

        // Unity lays streams out sequentially, padding each intermediate stream up to 16 bytes
        var strides = raw.StreamIds.Select((s, o) => s == SkinStream ? CanonicalStride : raw.Stride(o)).ToArray();
        var starts = new int[raw.StreamIds.Count];
        long total = 0;
        for (int i = 0; i < raw.StreamIds.Count; i++)
        {
            starts[i] = (int)total;
            long size = (long)raw.VertexCount * strides[i];
            if (i < raw.StreamIds.Count - 1) size = (size + 15) & ~15;
            total += size;
        }
        if (total > int.MaxValue) throw new InvalidDataException("mesh too large to widen its skin stream");
        var blob = new byte[total];
        for (int i = 0; i < raw.StreamIds.Count; i++)
        {
            var bytes = raw.StreamIds[i] == SkinStream ? wide : raw.StreamBytes(i);
            bytes.CopyTo(blob, starts[i]);
        }

        var vd = mesh["m_VertexData"];
        WriteBytes(vd["m_DataSize"], blob);
        var defs = vd["m_Channels"]["Array"].Children;
        Set(defs[WeightChannel], SkinStream, 0, Float32, 4);
        Set(defs[IndexChannel], SkinStream, 16, UInt32Format, 4);
        return true;
    }

    private static void Set(AssetTypeValueField c, byte stream, byte offset, byte format, byte dimension)
    {
        c["stream"].AsByte = stream;
        c["offset"].AsByte = offset;
        c["format"].AsByte = format;
        c["dimension"].AsByte = dimension;
    }

    /// <summary>Write a Unity byte vector, handling the byte-optimized and expanded-children shapes.</summary>
    private static void WriteBytes(AssetTypeValueField f, byte[] bytes)
    {
        if (f.TemplateField.ValueType == AssetValueType.ByteArray) { f.AsByteArray = bytes; return; }
        var arr = f["Array"];
        if (!arr.IsDummy && arr.TemplateField.ValueType == AssetValueType.ByteArray)
        {
            arr.AsByteArray = bytes;
            return;
        }
        var kids = new List<AssetTypeValueField>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
            el.AsByte = bytes[i];
            kids.Add(el);
        }
        arr.Children = kids;
    }
}
