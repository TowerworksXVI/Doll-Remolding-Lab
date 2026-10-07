using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The operand walk over a shader-model 4 instruction stream, on hand-built token streams
/// laid out per the published tokenized-program format. The corpus check against every retained
/// character program (2,868 programs, 65,659 declared fields, zero disagreements with the decompiled
/// text) is a research-side measurement; these pin the encodings the walk relies on.</summary>
public sealed class DxbcConstantReadsTests
{
    private const uint Mov = 54, Add = 0, Ret = 62, CustomData = 53, DclConstantBuffer = 89;

    [Fact]
    public void Swizzles_masks_and_selects_name_the_bytes_a_program_reads()
    {
        var program = Container(
            Instruction(DclConstantBuffer, Operand(8, 2, 2, 12)),          // dcl_constantbuffer cb2[12]
            Instruction(Mov, Register(0, mask: 0b0111), Swizzled(2, 5, 0xE4)), // mov r0.xyz, cb2[5].xyzw
            Instruction(Add, Register(1, mask: 0b0001), Selected(2, 7, 1), Immediate(1f)), // add r1.x, cb2[7].y, l(1)
            Instruction(Mov, Register(2, mask: 0b0001), Selected(1, 3, 0)),   // mov r2.x, cb1[3].x
            Instruction(Ret));

        var reads = DxbcConstantReads.Scan(program, 2);

        Assert.False(reads.Dynamic);
        Assert.Equal(new[] { 80, 84, 88, 92, 116 }, reads.Offsets.OrderBy(offset => offset));
        Assert.True(reads.Reads(80));
        Assert.True(reads.Reads(112, 16));
        Assert.False(reads.Reads(48));
        Assert.Equal(new[] { 48 }, DxbcConstantReads.Scan(program, 1).Offsets);
        Assert.Empty(DxbcConstantReads.Scan(program, 3).Offsets);
    }

    [Fact]
    public void Custom_data_and_declarations_are_not_reads_and_a_computed_register_reaches_everything()
    {
        uint[] operandLikeData = { Swizzled(2, 9, 0xE4)[0], 2, 9 };
        var program = Container(
            Instruction(DclConstantBuffer, Operand(8, 2, 2, 12)),
            Custom(operandLikeData),
            Instruction(Mov, Register(0, mask: 0b0001), Selected(2, 1, 0)),
            Instruction(Ret));
        var reads = DxbcConstantReads.Scan(program, 2);
        Assert.Equal(new[] { 16 }, reads.Offsets);
        Assert.False(reads.Dynamic);

        // mov r2.x, cb2[r0.x + 3].x — the register index is computed, so every byte may be read
        var relative = Container(
            Instruction(Mov, Register(2, mask: 0b0001), RelativeSelected(2, 3, temp: 0)),
            Instruction(Ret));
        var dynamic = DxbcConstantReads.Scan(relative, 2);
        Assert.True(dynamic.Dynamic);
        Assert.True(dynamic.Reads(1000));
        Assert.False(DxbcConstantReads.Scan(relative, 1).Dynamic);
    }

    [Fact]
    public void A_malformed_container_or_stream_is_refused_rather_than_read_as_nothing()
    {
        Assert.Throws<InvalidDataException>(() => DxbcConstantReads.Scan(new byte[40], 2));
        var program = Container(Instruction(Ret));
        var truncated = program.Take(program.Length - 4).ToArray();
        BitConverter.GetBytes(program.Length - 4).CopyTo(truncated, 24);
        Assert.Throws<InvalidDataException>(() => DxbcConstantReads.Scan(truncated, 2));
        var lying = (byte[])program.Clone();
        BitConverter.GetBytes(99).CopyTo(lying, 36 + 8 + 4);
        Assert.Throws<InvalidDataException>(() => DxbcConstantReads.Scan(lying, 2));
    }

    // ---- token builders --------------------------------------------------------------------------------

    private static uint[] Instruction(uint opcode, params uint[][] operands)
    {
        var tokens = new List<uint> { 0 };
        foreach (var operand in operands) tokens.AddRange(operand);
        tokens[0] = opcode | ((uint)tokens.Count << 24);
        return tokens.ToArray();
    }

    private static uint[] Custom(uint[] data) =>
        new[] { CustomData, (uint)(data.Length + 2) }.Concat(data).ToArray();

    /// <summary>A four-component operand with two immediate indices, in mask mode.</summary>
    private static uint[] Operand(uint type, uint index0, uint index1, uint mask) =>
        new[] { 2u | (0u << 2) | (mask << 4) | (type << 12) | (2u << 20), index0, index1 };

    private static uint[] Register(uint index, uint mask) =>
        new[] { 2u | (0u << 2) | (mask << 4) | (0u << 12) | (1u << 20), index };

    private static uint[] Swizzled(uint slot, uint register, uint swizzle) =>
        new[] { 2u | (1u << 2) | (swizzle << 4) | (8u << 12) | (2u << 20), slot, register };

    private static uint[] Selected(uint slot, uint register, uint component) =>
        new[] { 2u | (2u << 2) | (component << 4) | (8u << 12) | (2u << 20), slot, register };

    /// <summary>cbN[rM.x + register]: the second index is immediate-plus-relative, carrying a nested
    /// temp-register operand.</summary>
    private static uint[] RelativeSelected(uint slot, uint register, uint temp) =>
        new[]
        {
            2u | (2u << 2) | (0u << 4) | (8u << 12) | (2u << 20) | (0u << 22) | (3u << 25),
            slot, register,
            2u | (2u << 2) | (0u << 4) | (0u << 12) | (1u << 20), temp,
        };

    private static uint[] Immediate(float value) =>
        new[] { 1u | (4u << 12), BitConverter.SingleToUInt32Bits(value) };

    private static byte[] Container(params uint[][] instructions)
    {
        var tokens = new List<uint> { 0x00000040, 0 };
        foreach (var instruction in instructions) tokens.AddRange(instruction);
        tokens[1] = (uint)tokens.Count;
        var program = tokens.SelectMany(BitConverter.GetBytes).ToArray();
        var bytes = new List<byte>();
        bytes.AddRange("DXBC"u8.ToArray());
        bytes.AddRange(new byte[16]);
        bytes.AddRange(BitConverter.GetBytes(1));
        bytes.AddRange(BitConverter.GetBytes(36 + 8 + program.Length));
        bytes.AddRange(BitConverter.GetBytes(1));
        bytes.AddRange(BitConverter.GetBytes(36));
        bytes.AddRange("SHDR"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes(program.Length));
        bytes.AddRange(program);
        return bytes.ToArray();
    }
}
