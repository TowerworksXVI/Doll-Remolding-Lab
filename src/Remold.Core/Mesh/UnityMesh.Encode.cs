using System;
using System.Collections.Generic;

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
