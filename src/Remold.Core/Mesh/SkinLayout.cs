using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Remold.Core.Mesh;

/// <summary>
/// The skin-stream shape palette recovery consumes — float4 BlendWeight at stream-2 offset 0, uint4
/// BlendIndices at offset 16 — and the widening that brings a ONE-influence layout into it.
///
/// <para>A mesh storing a single influence per vertex spells that skin two ways: BlendWeight and
/// BlendIndices both stored x1, or BlendIndices alone with each weight implicitly 1. Both carry exactly
/// the same skin as <c>(w,0,0,0)/(i,0,0,0)</c>, so widening is lossless and the pooled pipeline can read
/// them like any other skinned mesh. A REDUCED layout (2 or 3 stored influences) is not here: widening it
/// would be lossless too, but its draws are posed per vertex by influences the narrow stream no longer
/// carries, so recovery has nothing to reproduce them from.</para>
///
/// <para>THE one home for the narrow shape: the readers, the layout half of the recoverable-skin rule and
/// the compile all ask here rather than each spelling out the channel arithmetic.</para>
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

    /// <summary>True when the mesh stores exactly one influence per vertex in a layout
    /// <see cref="Widen"/> can bring to the canonical shape: uint32 indices on stream 2, weights either
    /// float32 alongside them or absent (implicitly 1), and nothing else sharing that stream.</summary>
    public static bool IsNarrow(AssetTypeValueField mesh) => IsNarrow(Channels(mesh));

    private static bool IsNarrow(List<Chan> ch)
    {
        if (ch.Count <= IndexChannel) return false;
        var idx = ch[IndexChannel];
        var wgt = ch[WeightChannel];
        if (idx.Dim != 1 || idx.Format != UInt32Format || idx.Stream != SkinStream) return false;
        if (wgt.Dim != 0 && (wgt.Dim != 1 || wgt.Format != Float32 || wgt.Stream != SkinStream)) return false;
        return !Shared(ch);
    }

    /// <summary>True when the mesh's skin stream can feed palette recovery, as it stands or after
    /// <see cref="Widen"/>.</summary>
    public static bool Recoverable(AssetTypeValueField mesh)
    {
        var ch = Channels(mesh);
        return IsCanonical(ch) || IsNarrow(ch);
    }

    /// <summary>The mesh's skin stream in the canonical stride-32 shape: returned verbatim when the mesh
    /// already stores it, widened to <c>(w,0,0,0)/(i,0,0,0)</c> when it stores one influence per vertex.
    /// Throws <see cref="InvalidDataException"/> for any other layout — there is no lossless answer — and
    /// for a canonical-channel mesh whose stream is stored at some other stride, whose records the returned
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
        if (!IsNarrow(ch))
            throw new InvalidDataException(
                "the mesh's skin stream isn't float4 weights + uint4 indices, and stores more than one "
                + "influence per vertex");

        int stride = raw.Stride(ordinal);
        bool hasWeight = ch[WeightChannel].Dim == 1;
        int wOff = ch[WeightChannel].Offset, iOff = ch[IndexChannel].Offset;
        var wide = new byte[(long)raw.VertexCount * CanonicalStride <= int.MaxValue
            ? raw.VertexCount * CanonicalStride
            : throw new InvalidDataException("mesh too large to widen its skin stream")];
        for (int v = 0; v < raw.VertexCount; v++)
        {
            int src = v * stride, dst = v * CanonicalStride;
            float w = hasWeight ? BitConverter.ToSingle(stored, src + wOff) : 1f;
            BitConverter.GetBytes(w).CopyTo(wide, dst);
            Array.Copy(stored, src + iOff, wide, dst + 16, 4);
        }
        return wide;
    }

    /// <summary>Rewrite a one-influence mesh's vertex data and channel table into the canonical shape, in
    /// place; false when the mesh has nothing to widen (already canonical, skinless, or a layout
    /// <see cref="IsNarrow"/> refuses). Every other stream is copied through byte for byte.
    ///
    /// <para>For the compile: <see cref="MeshApply"/> encodes an authored skin against the target's OWN
    /// stored layout, so a narrow target would take the donor's four influences down to one and slice a
    /// narrow stream out the far end. Widening first is what lets the compiled streams come out in the
    /// shape the pooled machinery reads.</para></summary>
    public static bool Widen(AssetTypeValueField mesh)
    {
        var ch = Channels(mesh);
        if (IsCanonical(ch) || !IsNarrow(ch)) return false;

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
