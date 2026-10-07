using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AssetsTools.NET;
using Remold.Core.Bundles;
using Xunit;

namespace Remold.Core.Tests;

public sealed class ShaderReflectionTests
{
    [Fact]
    public void FragmentVariants_enumerates_a_serialized_d3d11_fragment_variant()
    {
        // a minimal container: one SHDR chunk holding a ps_4_0 program of a single ret
        uint[] tokens = { 0x00000040, 3, 62 | (1u << 24) };
        byte[] dxbc = new byte[32 + 4 + 8 + 12];
        Encoding.ASCII.GetBytes("DXBC").CopyTo(dxbc, 0);
        BitConverter.GetBytes(1u).CopyTo(dxbc, 20);
        BitConverter.GetBytes((uint)dxbc.Length).CopyTo(dxbc, 24);
        BitConverter.GetBytes(1u).CopyTo(dxbc, 28);
        BitConverter.GetBytes(36u).CopyTo(dxbc, 32);
        Encoding.ASCII.GetBytes("SHDR").CopyTo(dxbc, 36);
        BitConverter.GetBytes(12u).CopyTo(dxbc, 40);
        for (int index = 0; index < tokens.Length; index++)
            BitConverter.GetBytes(tokens[index]).CopyTo(dxbc, 44 + 4 * index);

        var variant = Assert.Single(ShaderReflection.FragmentVariants(ShaderField(dxbc)));

        Assert.Equal("Character/Test", variant.ShaderName);
        Assert.Equal((0, "Forward"), (variant.Pass, variant.PassName));
        Assert.Equal(new[] { "_USE_STOCKING" }, variant.Keywords);
        Assert.Equal(2, variant.MaterialBufferSlot);
        Assert.Equal(544, variant.MaterialBufferWidth);
        Assert.Equal(492, variant.VectorOffsets["_UseGIFlatten"]);
        Assert.Equal(ShaderReflection.Fnv64(dxbc).ToString("x16"), variant.DxbcHash);
        Assert.NotNull(variant.MaterialReads);
        Assert.Empty(variant.MaterialReads.Offsets);
        Assert.False(variant.MaterialReads.Dynamic);
        Assert.Empty(variant.TextureSlots!);
    }

    [Fact]
    public void A_program_the_walker_cannot_read_keeps_its_variant_with_unknown_reads()
    {
        // a container with no instruction chunk: the hash still comes off the bytes, the reads do not
        byte[] dxbc = new byte[32];
        Encoding.ASCII.GetBytes("DXBC").CopyTo(dxbc, 0);
        BitConverter.GetBytes(1u).CopyTo(dxbc, 20);
        BitConverter.GetBytes((uint)dxbc.Length).CopyTo(dxbc, 24);

        var variant = Assert.Single(ShaderReflection.FragmentVariants(ShaderField(dxbc)));

        Assert.Equal(ShaderReflection.Fnv64(dxbc).ToString("x16"), variant.DxbcHash);
        Assert.Equal(492, variant.VectorOffsets["_UseGIFlatten"]);
        Assert.Null(variant.MaterialReads);
    }

    private static AssetTypeValueField ShaderField(byte[] dxbc)
    {
        byte[] segment = new byte[16 + dxbc.Length];
        BitConverter.GetBytes(1u).CopyTo(segment, 0);
        BitConverter.GetBytes(16u).CopyTo(segment, 4);
        BitConverter.GetBytes((uint)dxbc.Length).CopyTo(segment, 8);
        BitConverter.GetBytes(0u).CopyTo(segment, 12);
        dxbc.CopyTo(segment, 16);
        byte[] compressed = LiteralBlock(segment);

        var names = Vector("m_NameIndices",
            Pair("UnityPerMaterial", 10), Pair("_UseGIFlatten", 20),
            Pair("_USE_STOCKING", 30));
        var subprogram = F("data", null,
            U32("m_BlobIndex", 0),
            Vector("m_GlobalKeywordIndices", I32("data", 30)),
            Vector("m_LocalKeywordIndices"),
            Vector("m_ConstantBufferBindings", F("data", null,
                I32("m_NameIndex", 10), I32("m_Index", 2))),
            Vector("m_ConstantBuffers", F("data", null,
                I32("m_NameIndex", 10), I32("m_Size", 544),
                Vector("m_VectorParams", F("data", null,
                    I32("m_NameIndex", 20), I32("m_Index", 492))))));
        var pass = F("data", null,
            names,
            F("m_State", null, Str("m_Name", "Forward")),
            F("progFragment", null, Vector("m_SubPrograms", subprogram)));

        return F("Base", null,
            F("m_ParsedForm", null,
                Str("m_Name", "Character/Test"),
                Vector("m_SubShaders", F("data", null,
                    Vector("m_Passes", pass)))),
            Vector("platforms", I32("data", 4)),
            NestedVector("offsets", U32("data", 0)),
            NestedVector("compressedLengths", U32("data", (uint)compressed.Length)),
            NestedVector("decompressedLengths", U32("data", (uint)segment.Length)),
            F("compressedBlob", null, Bytes("Array", compressed)));
    }

    private static byte[] LiteralBlock(byte[] bytes)
    {
        var encoded = new List<byte> { (byte)(Math.Min(bytes.Length, 15) << 4) };
        if (bytes.Length >= 15)
        {
            int remaining = bytes.Length - 15;
            while (remaining >= 255)
            {
                encoded.Add(255);
                remaining -= 255;
            }
            encoded.Add((byte)remaining);
        }
        encoded.AddRange(bytes);
        return encoded.ToArray();
    }

    private static AssetTypeValueField Pair(string name, int index) => F("data", null,
        Str("first", name), I32("second", index));

    private static AssetTypeValueField Vector(string name, params AssetTypeValueField[] values) =>
        F(name, null, F("Array", null, values));

    private static AssetTypeValueField NestedVector(string name, params AssetTypeValueField[] values) =>
        F(name, null, F("Array", null, F("data", null, F("Array", null, values))));

    private static AssetTypeValueField Str(string name, string value) =>
        F(name, new AssetTypeValue(value));

    private static AssetTypeValueField I32(string name, int value) =>
        F(name, new AssetTypeValue(AssetValueType.Int32, value));

    private static AssetTypeValueField U32(string name, uint value) =>
        F(name, new AssetTypeValue(AssetValueType.UInt32, value));

    private static AssetTypeValueField Bytes(string name, byte[] value) =>
        F(name, new AssetTypeValue(AssetValueType.ByteArray, value));

    private static AssetTypeValueField F(string name, AssetTypeValue? value = null,
        params AssetTypeValueField[] children)
    {
        var template = new AssetTypeTemplateField
        {
            Name = name,
            Type = "",
            ValueType = value?.ValueType ?? AssetValueType.None,
            Children = children.Select(child => child.TemplateField).ToList(),
        };
        return new AssetTypeValueField
        {
            TemplateField = template,
            Value = value,
            Children = children.ToList(),
        };
    }
}
