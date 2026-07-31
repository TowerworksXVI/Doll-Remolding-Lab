using System;
using System.Collections.Generic;
using System.Linq;
using AssetsTools.NET;
using Remold.Core.Mesh;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The packed m_Channels.dimension byte with NO live game: a hand-built Mesh field whose Normal channel
/// carries the raw byte 0x34 (stored dim 4 in the low nibble, semantic 3 in the high). Deleting the
/// <c>&amp; 0xF</c> mask at Decode makes the first test throw; deleting it at WriteBack makes the apply
/// round-trip throw at Encode — so the mask can't regress on a machine without the game. The round-trip
/// also pins that nobody rewrites m_Channels: the raw byte survives an Apply verbatim.
/// </summary>
public class PackedDimensionTests
{
    private const int PackedNormalByte = 0x34;   // stored 4 (low nibble), semantic 3 (high nibble)

    /// <summary>A minimal synthetic type-tree field: template (name + value type) with value and children.</summary>
    private static AssetTypeValueField F(string name, AssetTypeValue? value = null, params AssetTypeValueField[] children)
    {
        var template = new AssetTypeTemplateField
        {
            Name = name,
            Type = "",
            ValueType = value?.ValueType ?? AssetValueType.None,
            Children = children.Select(c => c.TemplateField).ToList(),
        };
        return new AssetTypeValueField { TemplateField = template, Value = value, Children = children.ToList() };
    }

    private static AssetTypeValueField I32(string name, int v) => F(name, new AssetTypeValue(AssetValueType.Int32, v));
    private static AssetTypeValueField U32(string name, uint v) => F(name, new AssetTypeValue(AssetValueType.UInt32, v));
    private static AssetTypeValueField F32(string name, float v) => F(name, new AssetTypeValue(AssetValueType.Float, v));
    private static AssetTypeValueField Bytes(string name, byte[] b) => F(name, new AssetTypeValue(AssetValueType.ByteArray, b));

    private static AssetTypeValueField Aabb(string name) => F(name, null,
        F("m_Center", null, F32("x", 0), F32("y", 0), F32("z", 0)),
        F("m_Extent", null, F32("x", 0), F32("y", 0), F32("z", 0)));

    private static AssetTypeValueField Channel(int stream, int offset, int format, int rawDimension) => F("data", null,
        I32("stream", stream), I32("offset", offset), I32("format", format), I32("dimension", rawDimension));

    // 2 verts, one stream: Vertex f32x3 @0, Normal f32x4 @12 (stride 28), the Normal dimension byte RAW
    // 0x34. The 4th normal components are distinct, so a mis-stride shows.
    private static readonly float[] Positions = { 1, 2, 3, /*v1*/ 4, 5, 6 };
    private static readonly float[] Normals4 = { 0, 1, 0, 7, /*v1*/ 1, 0, 0, 9 };

    private static byte[] VertexBlob()
    {
        var blob = new byte[2 * 28];
        for (int v = 0; v < 2; v++)
        {
            for (int d = 0; d < 3; d++) BitConverter.GetBytes(Positions[v * 3 + d]).CopyTo(blob, v * 28 + d * 4);
            for (int d = 0; d < 4; d++) BitConverter.GetBytes(Normals4[v * 4 + d]).CopyTo(blob, v * 28 + 12 + d * 4);
        }
        return blob;
    }

    private static byte[] IndexBufferU16() =>
        new byte[] { 0, 0, 1, 0, 0, 0 };   // uint16 {0,1,0}

    private static AssetTypeValueField PackedMeshField() => F("Base", null,
        F("m_Name", new AssetTypeValue("c_packed_test")),
        F("m_VertexData", null,
            I32("m_VertexCount", 2),
            F("m_Channels", null,
                F("Array", null,
                    Channel(0, 0, 0, 3),                       // Vertex f32x3
                    Channel(0, 12, 0, PackedNormalByte))),     // Normal f32, RAW dim 0x34
            Bytes("m_DataSize", VertexBlob())),
        I32("m_IndexFormat", 0),
        Bytes("m_IndexBuffer", IndexBufferU16()),
        F("m_SubMeshes", null,
            F("Array", null,
                F("data", null,
                    U32("firstByte", 0), U32("indexCount", 3), U32("baseVertex", 0),
                    U32("firstVertex", 0), U32("vertexCount", 2),
                    Aabb("localAABB")))),
        // The apply path maps the authored skin onto this bone order, so the field needs m_BoneNameHashes.
        F("m_BoneNameHashes", null, F("Array", null, U32("data", 100))),
        Aabb("m_LocalAABB"));

    [Fact]
    public void Decode_MasksPackedDimensionByte_ToStoredStride()
    {
        var mesh = UnityMesh.Decode(PackedMeshField());

        Assert.Equal(2, mesh.VertexCount);
        Assert.Equal(4, mesh.Dims["Normal"]);          // 0x34 & 0xF — the stored component count
        Assert.Equal(3, mesh.Dims["Vertex"]);
        Assert.Equal(Positions, mesh.Channels["Vertex"]);
        Assert.Equal(Normals4, mesh.Channels["Normal"]);   // incl. the 4th components 7 and 9
    }

    [Fact]
    public void Apply_RoundTrip_LeavesRawPackedByteInTreeUntouched()
    {
        var field = PackedMeshField();

        // An identity-topology edit: positions shifted +0.5 in X, so the 4-wide Normal comes from the
        // original through the conform pass-through. The authored skin resolves cleanly, so nothing falls
        // back — the Normal channel is what this is about.
        var payload = new MeshApply.Payload
        {
            Mesh = new UnityMesh
            {
                Name = "c_packed_test",
                VertexCount = 2,
                Channels = new Dictionary<string, float[]>
                {
                    ["Vertex"] = new[] { Positions[0] + 0.5f, Positions[1], Positions[2],
                                         Positions[3] + 0.5f, Positions[4], Positions[5] },
                },
                Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
                Submeshes = new List<int[]> { new[] { 0, 1, 0 } },
            },
            SkinJointHashes = new uint[] { 100 },
            JointIndices = new[] { 0, 0, 0, 0, /*v1*/ 0, 0, 0, 0 },
            JointWeights = new[] { 1f, 0, 0, 0, /*v1*/ 1f, 0, 0, 0 },
        };

        var result = MeshApply.Apply(field, payload);
        Assert.Equal(2, result.NewVertexCount);

        // The raw packed byte survives the apply verbatim — nobody writes m_Channels back.
        int rawDim = field["m_VertexData"]["m_Channels"]["Array"].Children[1]["dimension"].AsInt;
        Assert.Equal(PackedNormalByte, rawDim);

        // The Normal channel is still 4-wide and intact, proving Encode wrote at the MASKED stride.
        var again = UnityMesh.Decode(field);
        Assert.Equal(4, again.Dims["Normal"]);
        Assert.Equal(Normals4, again.Channels["Normal"]);
        Assert.Equal(Positions[0] + 0.5f, again.Channels["Vertex"][0], 3);
        Assert.Equal(Positions[3] + 0.5f, again.Channels["Vertex"][3], 3);
    }
}
