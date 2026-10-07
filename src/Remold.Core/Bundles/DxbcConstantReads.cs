using System;
using System.Collections.Generic;
using System.IO;

namespace Remold.Core.Bundles;

/// <summary>Which bytes of one constant buffer a compiled program actually reads. A shader declares
/// the whole buffer it binds, so the declaration cannot tell a value the program consumes from one it
/// never touches; the instruction stream can. <see cref="Dynamic"/> is set when the program indexes
/// the buffer with a computed register, which reaches every byte.</summary>
public sealed record ConstantBufferReads(IReadOnlySet<int> Offsets, bool Dynamic)
{
    public static readonly ConstantBufferReads None = new(new HashSet<int>(), false);

    /// <summary>True when any of the <paramref name="bytes"/> from <paramref name="offset"/> is read.</summary>
    public bool Reads(int offset, int bytes = 4)
    {
        if (Dynamic) return true;
        for (int at = offset; at < offset + bytes; at += 4)
            if (Offsets.Contains(at)) return true;
        return false;
    }
}

/// <summary>
/// Walks a DXBC container's shader-model 4/5 instruction stream (the <c>SHDR</c> or <c>SHEX</c> chunk)
/// and collects every constant-buffer operand the program reads. The bundled programs ship no
/// reflection chunk (<c>RDEF</c>), so operand usage is the only in-bundle evidence of a read. Token
/// layout follows the published D3D10/D3D11 tokenized program format: an opcode token carries the
/// instruction length in DWORDs, custom-data blocks carry their own length, and each operand token
/// names its type, component selection, and index representations.
/// </summary>
public static class DxbcConstantReads
{
    private const int OpcodeCustomData = 53;
    private const int OperandConstantBuffer = 8;
    private const int OperandImmediate32 = 4;
    private const int OperandImmediate64 = 5;

    /// <summary>The bytes of constant buffer <paramref name="constantBufferSlot"/> read anywhere in
    /// the program, as byte offsets. Throws <see cref="InvalidDataException"/> when the container or
    /// its instruction stream is malformed — a program that cannot be read proves nothing.</summary>
    public static ConstantBufferReads Scan(ReadOnlySpan<byte> dxbc, int constantBufferSlot)
    {
        if (dxbc.Length < 32 || !dxbc[..4].SequenceEqual("DXBC"u8))
            throw new InvalidDataException("The shader program is not a DXBC container.");
        int chunkCount = ReadInt(dxbc, 28);
        if (chunkCount < 0 || 32 + 4L * chunkCount > dxbc.Length)
            throw new InvalidDataException("The shader program's chunk table is malformed.");
        for (int index = 0; index < chunkCount; index++)
        {
            int at = ReadInt(dxbc, 32 + 4 * index);
            if (at < 0 || at + 8 > dxbc.Length)
                throw new InvalidDataException("The shader program's chunk table is malformed.");
            var fourcc = dxbc.Slice(at, 4);
            if (!fourcc.SequenceEqual("SHDR"u8) && !fourcc.SequenceEqual("SHEX"u8)) continue;
            int size = ReadInt(dxbc, at + 4);
            if (size < 8 || at + 8 + (long)size > dxbc.Length || size % 4 != 0)
                throw new InvalidDataException("The shader program's instruction chunk is malformed.");
            return ScanProgram(dxbc.Slice(at + 8, size), constantBufferSlot);
        }
        throw new InvalidDataException("The shader program has no instruction chunk.");
    }

    private static ConstantBufferReads ScanProgram(ReadOnlySpan<byte> program, int slot)
    {
        var tokens = new uint[program.Length / 4];
        for (int index = 0; index < tokens.Length; index++)
            tokens[index] = (uint)ReadInt(program, 4 * index);
        int declared = (int)tokens[1];
        if (declared != tokens.Length)
            throw new InvalidDataException("The shader program's declared length disagrees with its chunk.");
        var offsets = new HashSet<int>();
        bool dynamic = false;
        int position = 2;
        while (position < tokens.Length)
        {
            uint opcodeToken = tokens[position];
            int opcode = (int)(opcodeToken & 0x7ff);
            if (opcode == OpcodeCustomData)
            {
                if (position + 1 >= tokens.Length) throw Malformed();
                int customLength = (int)tokens[position + 1];
                if (customLength < 2 || position + customLength > tokens.Length) throw Malformed();
                position += customLength;
                continue;
            }
            int length = (int)((opcodeToken >> 24) & 0x7f);
            if (length < 1 || position + length > tokens.Length) throw Malformed();
            int end = position + length;
            if (!IsDeclaration(opcode))
            {
                int cursor = position + 1;
                for (bool extended = (opcodeToken >> 31) != 0; extended; cursor++)
                {
                    if (cursor >= end) throw Malformed();
                    extended = (tokens[cursor] >> 31) != 0;
                }
                while (cursor < end)
                    cursor = ReadOperand(tokens, cursor, end, slot, offsets, ref dynamic);
            }
            position = end;
        }
        return new ConstantBufferReads(offsets, dynamic);
    }

    /// <summary>Decodes one operand starting at <paramref name="at"/>, records a constant-buffer read
    /// when it names <paramref name="slot"/>, and returns the position after it.</summary>
    private static int ReadOperand(uint[] tokens, int at, int end, int slot, HashSet<int> offsets,
        ref bool dynamic)
    {
        if (at >= end) throw Malformed();
        uint token = tokens[at];
        int cursor = at + 1;
        for (bool extended = (token >> 31) != 0; extended; cursor++)
        {
            if (cursor >= end) throw Malformed();
            extended = (tokens[cursor] >> 31) != 0;
        }
        int components = (int)(token & 3);
        int type = (int)((token >> 12) & 0xff);
        if (type == OperandImmediate32) return Advance(cursor, components == 2 ? 4 : 1, end);
        if (type == OperandImmediate64) return Advance(cursor, components == 2 ? 8 : 2, end);
        int dimension = (int)((token >> 20) & 3);
        long?[] indices = new long?[dimension];
        for (int index = 0; index < dimension; index++)
        {
            int representation = (int)((token >> (22 + 3 * index)) & 7);
            switch (representation)
            {
                case 0:
                    if (cursor >= end) throw Malformed();
                    indices[index] = tokens[cursor++];
                    break;
                case 1:
                    if (cursor + 1 >= end) throw Malformed();
                    indices[index] = tokens[cursor] | ((long)tokens[cursor + 1] << 32);
                    cursor += 2;
                    break;
                case 2:
                    cursor = SkipRelative(tokens, cursor, end);
                    break;
                case 3:
                    cursor = SkipRelative(tokens, Advance(cursor, 1, end), end);
                    break;
                case 4:
                    cursor = SkipRelative(tokens, Advance(cursor, 2, end), end);
                    break;
                default:
                    throw Malformed();
            }
        }
        if (type != OperandConstantBuffer || dimension != 2 || indices[0] != slot) return cursor;
        if (indices[1] is not { } register)
        {
            dynamic = true;
            return cursor;
        }
        int selection = (int)((token >> 2) & 3);
        if (components != 2)
        {
            offsets.Add(checked((int)register * 16));
            return cursor;
        }
        switch (selection)
        {
            case 0:
                int mask = (int)((token >> 4) & 0xf);
                for (int component = 0; component < 4; component++)
                    if ((mask & (1 << component)) != 0) offsets.Add(checked((int)register * 16 + 4 * component));
                break;
            case 1:
                int swizzle = (int)((token >> 4) & 0xff);
                for (int component = 0; component < 4; component++)
                    offsets.Add(checked((int)register * 16 + 4 * ((swizzle >> (2 * component)) & 3)));
                break;
            case 2:
                offsets.Add(checked((int)register * 16 + 4 * (int)((token >> 4) & 3)));
                break;
            default:
                throw Malformed();
        }
        return cursor;
    }

    /// <summary>A relative index is itself an operand (a temp register, typically); it is skipped, never
    /// read, since a register never names a constant buffer.</summary>
    private static int SkipRelative(uint[] tokens, int at, int end)
    {
        bool ignored = false;
        return ReadOperand(tokens, at, end, -1, new HashSet<int>(), ref ignored);
    }

    private static int Advance(int cursor, int count, int end) =>
        cursor + count <= end ? cursor + count : throw Malformed();

    /// <summary>Declarations name the resources a program binds, not what it reads: the shader-model 4
    /// range <c>dcl_resource</c> (88) … <c>dcl_globalFlags</c> (106), the shader-model 5 range
    /// <c>dcl_stream</c> (143) … <c>dcl_resource_structured</c> (162), and <c>dcl_gsinstances</c>
    /// (206). The numbers are the tokenized-program enumeration as the stock decompiler's
    /// <c>tokens.h</c> lists it, with its fixed points <c>dcl_globalFlags</c> = 106, <c>hs_decls</c> = 113
    /// (after the two reserved entries), <c>ld_uav_typed</c> = 163 and 207 entries in all.</summary>
    private static bool IsDeclaration(int opcode) =>
        opcode is >= 88 and <= 106 or >= 143 and <= 162 or 206;

    private static int ReadInt(ReadOnlySpan<byte> bytes, int at) =>
        BitConverter.ToInt32(bytes.Slice(at, 4));

    private static InvalidDataException Malformed() =>
        new("The shader program's instruction stream is malformed.");
}
