using System;
using System.Linq;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The raw-protobuf reader, exercised against the INDEPENDENT <see cref="Pb"/> encoder, so a bug in one
/// can't mask a bug in the other.
/// </summary>
public class ProtobufTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(128)]
    [InlineData(300)]
    [InlineData(16384)]
    [InlineData(0x1_0000_0000)]   // forces a 5+ byte varint
    public void Varint_RoundTrips(long value)
    {
        var msg = PbMessage.Parse(Pb.Msg().Varint(5, value).ToArray());
        Assert.Equal((ulong)value, msg.Num(5));
    }

    [Fact]
    public void Fixed32_DecodesLittleEndian()
    {
        var msg = PbMessage.Parse(Pb.Msg().Fixed32(2, 0xDEADBEEF).ToArray());
        Assert.Equal(0xDEADBEEFUL, msg.Num(2));
    }

    [Fact]
    public void Fixed64_DecodesLittleEndian()
    {
        var msg = PbMessage.Parse(Pb.Msg().Fixed64(3, 0x1122334455667788UL).ToArray());
        Assert.Equal(0x1122334455667788UL, msg.Num(3));
    }

    [Fact]
    public void Str_DecodesUtf8_AndNumIgnoresLenFields()
    {
        var msg = PbMessage.Parse(Pb.Msg().Str(7, "héllo").ToArray());
        Assert.Equal("héllo", msg.Str(7));
        Assert.Null(msg.Num(7));    // Num only reports numeric wire types
    }

    [Fact]
    public void Sub_ParsesNestedMessage()
    {
        var inner = Pb.Msg().Varint(1, 99).Str(2, "x");
        var msg = PbMessage.Parse(Pb.Msg().Sub(4, inner).ToArray());

        var sub = msg.Sub(4);
        Assert.NotNull(sub);
        Assert.Equal(99UL, sub!.Num(1));
        Assert.Equal("x", sub.Str(2));
    }

    [Fact]
    public void Repeated_YieldsEverySubmessageOccurrence()
    {
        var bytes = Pb.Msg()
            .Sub(9, Pb.Msg().Varint(1, 10))
            .Sub(9, Pb.Msg().Varint(1, 20))
            .ToArray();
        var rows = PbMessage.Parse(bytes).Repeated(9).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(10UL, rows[0].Num(1));
        Assert.Equal(20UL, rows[1].Num(1));
    }

    [Fact]
    public void PackedVarints_DecodesSequence()
    {
        var msg = PbMessage.Parse(Pb.Msg().Packed(10, 1, 2, 300, 4).ToArray());
        Assert.Equal(new ulong[] { 1, 2, 300, 4 }, msg.PackedVarints(10).ToArray());
    }

    [Fact]
    public void MissingField_GivesEmptyAndNull()
    {
        var msg = PbMessage.Parse(Pb.Msg().Varint(1, 1).ToArray());
        Assert.Empty(msg.Field(42));
        Assert.Null(msg.Num(42));
        Assert.Null(msg.Str(42));
        Assert.Null(msg.Sub(42));
    }

    [Fact]
    public void RepeatedScalar_KeepsAllOccurrences_NumReturnsFirst()
    {
        var msg = PbMessage.Parse(Pb.Msg().Varint(1, 10).Varint(1, 20).ToArray());
        Assert.Equal(2, msg.Field(1).Count);
        Assert.Equal(10UL, msg.Num(1));   // first occurrence wins
    }

    [Fact]
    public void FieldNumbers_AreSorted()
    {
        var msg = PbMessage.Parse(Pb.Msg().Varint(9, 1).Varint(3, 1).Varint(33, 1).ToArray());
        Assert.Equal(new[] { 3, 9, 33 }, msg.FieldNumbers.ToArray());
    }

    [Fact]
    public void Parse_Throws_OnUnsupportedWireType()
    {
        // Wire type 3 (group-start) isn't handled — the reader must reject, not silently skip.
        var bytes = Pb.Msg().RawTag(1, 3).ToArray();
        Assert.Throws<FormatException>(() => PbMessage.Parse(bytes));
    }

    [Fact]
    public void Parse_Throws_OnOverlongVarint()
    {
        // A varint that never clears the continuation bit past 10 bytes is corrupt: throw a clear
        // FormatException rather than shift past 64 bits into garbage.
        var bytes = new byte[] { 0x08 };                          // tag: field 1, wire 0 (varint)
        var overlong = Enumerable.Repeat((byte)0x80, 11).Append((byte)0x01).ToArray();  // 12 continuation bytes
        Assert.Throws<FormatException>(() => PbMessage.Parse(bytes.Concat(overlong).ToArray()));
    }

    [Fact]
    public void Parse_Throws_OnTruncatedVarint()
    {
        // a varint running off the end of the buffer throws FormatException, not IndexOutOfRangeException
        var bytes = new byte[] { 0x08, 0x80 };                    // field 1 varint, then a dangling 0x80
        Assert.Throws<FormatException>(() => PbMessage.Parse(bytes));
    }

    [Fact]
    public void Str_ReturnsNull_OnInvalidUtf8()
    {
        // the "non-UTF-8 returns null" contract: invalid bytes return null rather than substituting U+FFFD
        var bytes = Pb.Msg().Len(7, new byte[] { 0xFF, 0xFE, 0xFF }).ToArray();
        Assert.Null(PbMessage.Parse(bytes).Str(7));
    }
}
