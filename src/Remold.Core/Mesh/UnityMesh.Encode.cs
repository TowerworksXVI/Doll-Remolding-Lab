using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Mesh;

/// <summary>
/// The encode half of the vertex-blob codec: packs channel arrays back into the engine's
/// stream-interleaved vertex blob. Used by the mesh write-back path (<see cref="MeshApply"/>).
///
/// <para><see cref="Encode"/> encodes exactly against the <see cref="ChannelDef"/> list it is given, so
/// a decode→encode round-trip is byte-exact only if the caller feeds back the SAME <c>&amp; 0xF</c>-masked
/// defs it decoded with. <c>m_Channels</c> is never written back; the masking is read-side only.</para>
/// </summary>
public sealed partial class UnityMesh
{
    /// <summary>
    /// Inverse of <see cref="DecodeRaw"/>'s vertex step: pack channel arrays back into the
    /// stream-interleaved blob, matching the given channel layout/strides exactly.
    ///
    /// Channels in <paramref name="channels"/> but absent from <paramref name="arrays"/> are left zero
    /// (the write-back caller merges the target's originals first). <c>*Norm</c> formats scale back to
    /// the stored integer range, round <b>half-to-even</b>, then clamp; Int/Float casts truncate toward
    /// zero. Both are load-bearing for byte-parity with the original asset.
    /// </summary>
    public static byte[] Encode(IReadOnlyList<ChannelDef> channels, int vertexCount,
        IReadOnlyDictionary<string, float[]> arrays)
    {
        ValidateLayout("<encode>", channels, vertexCount);
        var (strides, starts, total) = StreamInfoWithTotal(channels, vertexCount);
        if (total > int.MaxValue)
            throw new FormatException($"mesh too large to encode: {total} bytes");
        var outBuf = new byte[total];
        for (int ci = 0; ci < channels.Count; ci++)
        {
            var ch = channels[ci];
            if (ch.Dimension == 0) continue;
            if (!arrays.TryGetValue(ChannelNames[ci], out var values)) continue;
            // a short array would otherwise throw a bare IndexOutOfRange; name the channel and the gap
            if (values.Length < (long)vertexCount * ch.Dimension)
                throw new FormatException(
                    $"channel '{ChannelNames[ci]}' has {values.Length} values but the layout needs " +
                    $"{(long)vertexCount * ch.Dimension} ({vertexCount}×{ch.Dimension})");
            int stride = strides[ch.Stream], start = starts[ch.Stream];
            int sz = FormatSize(ch.Format);
            for (int v = 0; v < vertexCount; v++)
            {
                int rowBase = start + v * stride + ch.Offset;
                for (int d = 0; d < ch.Dimension; d++)
                    WriteComponent(outBuf, rowBase + d * sz, ch.Format, values[v * ch.Dimension + d]);
            }
        }
        return outBuf;
    }

    /// <summary>Whether two channel tables store <paramref name="stream"/> identically: the same channels
    /// in it, each at the same offset, format and stored dimension. A buffer sliced for one table is read
    /// correctly through the other exactly when this holds.</summary>
    public static bool SameStreamLayout(IReadOnlyList<ChannelDef> a, IReadOnlyList<ChannelDef> b, int stream)
    {
        for (int ci = 0; ci < Math.Max(a.Count, b.Count); ci++)
        {
            var x = InStream(a, ci, stream);
            var y = InStream(b, ci, stream);
            if (x != y) return false;
        }
        return true;
    }

    static ChannelDef? InStream(IReadOnlyList<ChannelDef> channels, int ci, int stream) =>
        ci < channels.Count && channels[ci].Dimension != 0 && channels[ci].Stream == stream
            ? channels[ci] : null;

    const int TexCoord0 = 4, LastTexCoord = 11;

    /// <summary>Re-encode one vertex stream from the layout it was sliced in to another: every channel
    /// <paramref name="to"/> stores in <paramref name="stream"/> is decoded out of <paramref name="source"/>
    /// through <paramref name="from"/> and written at <paramref name="to"/>'s offset and format. A
    /// narrowing format change (float32 to float16, float to a Norm) rounds and clamps as
    /// <see cref="Encode"/> does; a channel both layouts store in the same format and dimension is copied
    /// byte for byte, so it never passes through float.
    ///
    /// <para>A UV channel <paramref name="to"/> stores and the source lacks is filled from the source's
    /// TexCoord0 when that sits in the same stream at the same dimension, and named in
    /// <paramref name="filled"/>: a draw whose mesh lacks a UV set is fed its first one, so the copy
    /// states what the source's own draw already reads there.</para>
    ///
    /// <para>Throws <see cref="FormatException"/> when <paramref name="to"/> stores nothing in the stream,
    /// when it stores any other channel the source does not carry at the same dimension, or when
    /// <paramref name="source"/> is not exactly <paramref name="vertexCount"/> rows of
    /// <paramref name="from"/>'s stride.</para></summary>
    public static byte[] TranscodeStream(byte[] source, int vertexCount, IReadOnlyList<ChannelDef> from,
        IReadOnlyList<ChannelDef> to, int stream, out IReadOnlyList<string> filled)
    {
        var fromOnly = new ChannelDef[from.Count];
        for (int ci = 0; ci < from.Count; ci++) fromOnly[ci] = InStream(from, ci, stream) ?? default;
        var toOnly = new ChannelDef[to.Count];
        for (int ci = 0; ci < to.Count; ci++) toOnly[ci] = InStream(to, ci, stream) ?? default;

        if (toOnly.All(c => c.Dimension == 0))
            throw new FormatException($"the target layout stores nothing in stream {stream}");
        // per target channel, the source channel its values come from
        var src = new int[toOnly.Length];
        var fills = new List<string>();
        for (int ci = 0; ci < toOnly.Length; ci++)
        {
            src[ci] = ci;
            if (toOnly[ci].Dimension == 0) continue;
            int have = ci < fromOnly.Length ? fromOnly[ci].Dimension : 0;
            if (have == toOnly[ci].Dimension) continue;
            bool fillable = have == 0 && ci > TexCoord0 && ci <= LastTexCoord
                && TexCoord0 < fromOnly.Length && fromOnly[TexCoord0].Dimension == toOnly[ci].Dimension;
            if (!fillable)
                throw new FormatException(
                    $"channel '{ChannelNames[ci]}' is stored {toOnly[ci].Dimension} wide in the target layout " +
                    $"but {have} wide in the source");
            src[ci] = TexCoord0;
            fills.Add(ChannelNames[ci]);
        }
        filled = fills;
        ValidateLayout("<transcode>", fromOnly, vertexCount);
        var (strides, _, _) = StreamInfoWithTotal(fromOnly, vertexCount);
        long expected = (long)vertexCount * strides.GetValueOrDefault(stream);
        if (source.Length != expected)
            throw new FormatException(
                $"stream {stream} is {source.Length} bytes but its layout states {expected} " +
                $"({vertexCount}×{strides.GetValueOrDefault(stream)})");

        var decoded = DecodeRaw("<transcode>", vertexCount, fromOnly, source, 0, Array.Empty<byte>(),
            Array.Empty<SubMeshDef>());
        var arrays = new Dictionary<string, float[]>(decoded.Channels);
        for (int ci = 0; ci < toOnly.Length; ci++)
            if (src[ci] != ci) arrays[ChannelNames[ci]] = decoded.Channels[ChannelNames[src[ci]]];
        var recoded = Encode(toOnly, vertexCount, arrays);

        // same format and dimension: the stored bytes ARE the answer, and the float round trip is not
        // exact for every format (SNorm -128, integers past 2^24)
        int fromStride = strides[stream], toStride = recoded.Length / Math.Max(vertexCount, 1);
        for (int ci = 0; ci < toOnly.Length; ci++)
        {
            var t = toOnly[ci];
            if (t.Dimension == 0 || fromOnly[src[ci]].Format != t.Format) continue;
            int bytes = t.Dimension * FormatSize(t.Format);
            for (int v = 0; v < vertexCount; v++)
                Buffer.BlockCopy(source, v * fromStride + fromOnly[src[ci]].Offset, recoded, v * toStride + t.Offset, bytes);
        }
        return recoded;
    }

    /// <inheritdoc cref="TranscodeStream(byte[], int, IReadOnlyList{ChannelDef}, IReadOnlyList{ChannelDef}, int, out IReadOnlyList{string})"/>
    public static byte[] TranscodeStream(byte[] source, int vertexCount, IReadOnlyList<ChannelDef> from,
        IReadOnlyList<ChannelDef> to, int stream) =>
        TranscodeStream(source, vertexCount, from, to, stream, out _);

    /// <summary>Reject absurd channel metadata from a corrupt type tree BEFORE any size arithmetic, so
    /// downstream math can't overflow and writes stay in bounds.</summary>
    private static void ValidateLayout(string name, IReadOnlyList<ChannelDef> channels, int vertexCount)
    {
        if (vertexCount < 0)
            throw new FormatException($"mesh '{name}': negative vertex count {vertexCount}");
        foreach (var ch in channels)
        {
            if (ch.Dimension == 0) continue;
            if (ch.Dimension < 0 || ch.Dimension > 4)
                throw new FormatException($"mesh '{name}': channel dimension {ch.Dimension} out of range (0..4)");
            if (ch.Offset < 0 || ch.Offset > 0xFFFF)
                throw new FormatException($"mesh '{name}': channel offset {ch.Offset} out of range");
            if (ch.Stream < 0 || ch.Stream > 0xFF)
                throw new FormatException($"mesh '{name}': channel stream {ch.Stream} out of range");
            FormatSize(ch.Format);   // throws FormatException on an unknown VertexFormat
        }
    }

    /// <summary>Encode-side twin of <c>StreamInfo</c>: per-stream stride, 16-aligned starts, and total
    /// size accumulated in <c>long</c> so a corrupt type tree can't silently wrap.</summary>
    private static (Dictionary<int, int> strides, Dictionary<int, int> starts, long total) StreamInfoWithTotal(
        IReadOnlyList<ChannelDef> channels, int n)
    {
        var strides = new Dictionary<int, int>();
        foreach (var ch in channels)
        {
            if (ch.Dimension == 0) continue;
            int end = ch.Offset + ch.Dimension * FormatSize(ch.Format);
            strides[ch.Stream] = Math.Max(strides.GetValueOrDefault(ch.Stream), end);
        }
        var starts = new Dictionary<int, int>();
        long off = 0;
        var order = new List<int>(strides.Keys);
        order.Sort();
        for (int i = 0; i < order.Count; i++)
        {
            int s = order[i];
            starts[s] = (int)off;   // safe: total ≤ blob length ≤ int.MaxValue ⇒ every start fits int
            long size = (long)n * strides[s];
            if (i < order.Count - 1) size = (size + 15) & ~15;  // pad intermediate streams to 16, not the last
            off += size;
        }
        return (strides, starts, off);
    }

    /// <summary>Inverse of <see cref="ReadComponent"/>: write one component at <paramref name="pos"/>.
    /// Norm formats scale+round-half-to-even+clamp; Int/Float casts truncate toward zero.</summary>
    private static void WriteComponent(byte[] b, int pos, int fmt, float value)
    {
        switch (fmt)
        {
            case 0: BitConverter.GetBytes(value).CopyTo(b, pos); break;               // Float32
            case 1: BitConverter.GetBytes((Half)value).CopyTo(b, pos); break;         // Float16
            case 2: b[pos] = (byte)NormRound(value, 255.0, false); break;             // UNorm8
            case 3: b[pos] = unchecked((byte)(sbyte)NormRound(value, 127.0, true)); break;   // SNorm8
            case 4: BitConverter.GetBytes((ushort)NormRound(value, 65535.0, false)).CopyTo(b, pos); break; // UNorm16
            case 5: BitConverter.GetBytes((short)NormRound(value, 32767.0, true)).CopyTo(b, pos); break;   // SNorm16
            case 6: b[pos] = (byte)(uint)value; break;                               // UInt8
            case 7: b[pos] = unchecked((byte)(sbyte)(int)value); break;              // SInt8
            case 8: BitConverter.GetBytes((ushort)(uint)value).CopyTo(b, pos); break; // UInt16
            case 9: BitConverter.GetBytes((short)(int)value).CopyTo(b, pos); break;   // SInt16
            case 10: BitConverter.GetBytes((uint)value).CopyTo(b, pos); break;        // UInt32
            case 11: BitConverter.GetBytes((int)value).CopyTo(b, pos); break;         // SInt32
            default: throw new FormatException($"unknown vertex format {fmt}");
        }
    }

    /// <summary>Scale a canonical float into the stored integer range: round half-to-even, then clamp to
    /// [-div,div] (SNorm) or [0,div] (UNorm). The multiply must stay float64 for byte-identical
    /// results.</summary>
    private static double NormRound(float value, double div, bool snorm)
    {
        double scaled = Math.Round((double)value * div, MidpointRounding.ToEven);
        return Math.Clamp(scaled, snorm ? -div : 0.0, div);
    }
}
