using System.Collections.Generic;
using System.Text;

namespace Remold.Core.Tests.Support;

/// <summary>
/// A minimal protobuf <b>encoder</b> written from the wire format: key = <c>tag&gt;&gt;3</c>, wiretype =
/// <c>tag&amp;7</c>, varint = 7 bits/byte LSB-first with high-bit continuation, len-delimited = tag,
/// length-varint, bytes. Deliberately NOT a port of <c>Tables/Protobuf.cs</c>, so the production decoder
/// is tested against an INDEPENDENT encoder rather than against itself.
/// </summary>
internal sealed class Pb
{
    private readonly List<byte> _b = new();

    public static Pb Msg() => new();
    public byte[] ToArray() => _b.ToArray();

    public Pb Varint(int field, long value) { Tag(field, 0); WriteVarint(_b, (ulong)value); return this; }
    public Pb Fixed64(int field, ulong v) { Tag(field, 1); for (int i = 0; i < 8; i++) _b.Add((byte)(v >> (8 * i))); return this; }
    public Pb Fixed32(int field, uint v) { Tag(field, 5); for (int i = 0; i < 4; i++) _b.Add((byte)(v >> (8 * i))); return this; }

    public Pb Len(int field, byte[] payload)
    {
        Tag(field, 2);
        WriteVarint(_b, (ulong)payload.Length);
        _b.AddRange(payload);
        return this;
    }

    public Pb Str(int field, string s) => Len(field, Encoding.UTF8.GetBytes(s));
    public Pb Sub(int field, Pb sub) => Len(field, sub.ToArray());

    /// <summary>A packed-varint length-delimited field (the form <c>PackedVarints</c> decodes).</summary>
    public Pb Packed(int field, params long[] values)
    {
        var inner = new List<byte>();
        foreach (var v in values) WriteVarint(inner, (ulong)v);
        return Len(field, inner.ToArray());
    }

    /// <summary>Emit a bare tag with a chosen wire type (for malformed-input tests).</summary>
    public Pb RawTag(int field, int wire) { Tag(field, wire); return this; }

    private void Tag(int field, int wire) => WriteVarint(_b, ((ulong)(uint)field << 3) | (uint)wire);

    private static void WriteVarint(List<byte> dst, ulong v)
    {
        while (v >= 0x80) { dst.Add((byte)(v | 0x80UL)); v >>= 7; }
        dst.Add((byte)v);
    }
}
