using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Remold.Core.Tables;

/// <summary>
/// Minimal protobuf wire-format reader. The GFL2 tables ship no schema, so decoding is by field NUMBER +
/// wire type and callers interpret the values — no <c>protoc</c>, no <c>.proto</c>.
/// </summary>
public enum WireType
{
    Varint = 0,
    Fixed64 = 1,
    Len = 2,
    Fixed32 = 5,
}

/// <summary>One occurrence of a field: its wire type plus the raw value.</summary>
public readonly struct PbValue
{
    public WireType Wire { get; }
    /// <summary>Set for Varint / Fixed32 / Fixed64.</summary>
    public ulong Num { get; }
    /// <summary>Set for Len (length-delimited): the raw payload bytes.</summary>
    public byte[]? Bytes { get; }

    public PbValue(WireType wire, ulong num, byte[]? bytes)
    {
        Wire = wire;
        Num = num;
        Bytes = bytes;
    }
}

/// <summary>
/// A parsed protobuf message: field number → its occurrences; scalar accessors return the first. One
/// field number can legitimately appear with different wire types (the table top-level reuses #1 as both
/// a leading varint count and the repeated row submessages), so accessors filter by wire type.
/// </summary>
public sealed class PbMessage
{
    private readonly Dictionary<int, List<PbValue>> _fields = new();

    // throws instead of substituting U+FFFD (as shared Encoding.UTF8 would), so Str() can honour its
    // "non-UTF-8 returns null" contract
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public IReadOnlyList<PbValue> Field(int number) =>
        _fields.TryGetValue(number, out var v) ? v : Array.Empty<PbValue>();

    public IEnumerable<int> FieldNumbers => _fields.Keys.OrderBy(k => k);

    /// <summary>First numeric value (varint or fixed) for a field, or null.</summary>
    public ulong? Num(int number)
    {
        foreach (var v in Field(number))
            if (v.Wire is WireType.Varint or WireType.Fixed32 or WireType.Fixed64)
                return v.Num;
        return null;
    }

    /// <summary>First length-delimited value decoded as UTF-8, or null. Non-UTF-8 bytes return null.</summary>
    public string? Str(int number)
    {
        foreach (var v in Field(number))
            if (v.Wire == WireType.Len && v.Bytes is not null)
            {
                try { return StrictUtf8.GetString(v.Bytes); }
                catch (DecoderFallbackException) { return null; }
            }
        return null;
    }

    /// <summary>First length-delimited value's raw bytes, or null.</summary>
    public byte[]? Raw(int number)
    {
        foreach (var v in Field(number))
            if (v.Wire == WireType.Len && v.Bytes is not null)
                return v.Bytes;
        return null;
    }

    /// <summary>Parse the first length-delimited value as a nested message, or null.</summary>
    public PbMessage? Sub(int number)
    {
        var raw = Raw(number);
        return raw is null ? null : Parse(raw);
    }

    /// <summary>Parse every length-delimited occurrence of a field as a nested message.</summary>
    public IEnumerable<PbMessage> Repeated(int number)
    {
        foreach (var v in Field(number))
            if (v.Wire == WireType.Len && v.Bytes is not null)
                yield return Parse(v.Bytes);
    }

    /// <summary>A length-delimited field read as a packed varint list (the Parts nesting tables hold a
    /// packed list of child ids).</summary>
    public IEnumerable<ulong> PackedVarints(int number)
    {
        var raw = Raw(number);
        if (raw is null) yield break;
        int i = 0;
        while (i < raw.Length)
            yield return ReadVarint(raw, ref i);
    }

    public static PbMessage Parse(byte[] data) => Parse(data.AsSpan());

    public static PbMessage Parse(ReadOnlySpan<byte> data)
    {
        var msg = new PbMessage();
        int i = 0;
        while (i < data.Length)
        {
            ulong tag = ReadVarint(data, ref i);
            int fieldNumber = (int)(tag >> 3);
            var wire = (WireType)(int)(tag & 7);
            switch (wire)
            {
                case WireType.Varint:
                    msg.Add(fieldNumber, new PbValue(wire, ReadVarint(data, ref i), null));
                    break;
                case WireType.Fixed64:
                    msg.Add(fieldNumber, new PbValue(wire, ReadFixed(data, ref i, 8), null));
                    break;
                case WireType.Fixed32:
                    msg.Add(fieldNumber, new PbValue(wire, ReadFixed(data, ref i, 4), null));
                    break;
                case WireType.Len:
                    int len = (int)ReadVarint(data, ref i);
                    var bytes = data.Slice(i, len).ToArray();
                    i += len;
                    msg.Add(fieldNumber, new PbValue(wire, 0, bytes));
                    break;
                default:
                    throw new FormatException($"unsupported protobuf wire type {(int)wire} at offset {i}");
            }
        }
        return msg;
    }

    private void Add(int number, PbValue value)
    {
        if (!_fields.TryGetValue(number, out var list))
            _fields[number] = list = new List<PbValue>();
        list.Add(value);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> b, ref int i)
    {
        ulong result = 0;
        int shift = 0;
        // a 64-bit varint is at most 10 bytes; more is corrupt input, and throwing here beats shifting
        // past 64 bits (silent garbage) or running off the end with a bare IndexOutOfRangeException
        for (int n = 0; n < 10; n++)
        {
            if (i >= b.Length) throw new FormatException("truncated varint (ran off the end of the buffer)");
            byte x = b[i++];
            result |= (ulong)(x & 0x7f) << shift;
            if ((x & 0x80) == 0) return result;
            shift += 7;
        }
        throw new FormatException("overlong varint (more than 10 bytes)");
    }

    private static ulong ReadFixed(ReadOnlySpan<byte> b, ref int i, int n)
    {
        ulong result = 0;
        for (int k = 0; k < n; k++)
            result |= (ulong)b[i + k] << (8 * k);
        i += n;
        return result;
    }
}
