using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsTools.NET;

namespace Remold.Core.Mesh;

/// <summary>
/// Decodes a GFL2 Unity Mesh's vertex/index data. GF2 meshes are uncompressed
/// (m_MeshCompression=0), so vertices live in <c>m_VertexData.m_DataSize</c> as a stream-interleaved
/// byte blob. <see cref="Decode"/> adapts an AssetsTools type-tree field; <see cref="DecodeRaw"/> is
/// the pure byte codec underneath (no AssetsTools) so strides, the 16-byte stream padding, every
/// <c>VertexFormat</c> and baseVertex folding stay unit-testable on hand-built byte arrays.
/// The encode half lives in a separate partial.
/// </summary>
public sealed partial class UnityMesh
{
    public string Name { get; init; } = "";
    public int VertexCount { get; init; }
    /// <summary>Channel name → flattened values (length = VertexCount * dim).</summary>
    public Dictionary<string, float[]> Channels { get; init; } = new();
    /// <summary>Channel name → component count.</summary>
    public Dictionary<string, int> Dims { get; init; } = new();
    /// <summary>Per-submesh triangle index lists, absolute into the vertex buffer.</summary>
    public List<int[]> Submeshes { get; init; } = new();

    public bool Has(string channel) => Channels.ContainsKey(channel);

    // AsVector* must read at the channel's OWN stored stride (Dims), never a hard-coded k: a packed
    // mesh's Normal is 4-wide, and a stride-k read of a wider channel emits mis-strided garbage from
    // vertex 1 on, silently. A stride smaller than k refuses loudly rather than reading out of bounds.

    /// <summary>Stored stride for <paramref name="channel"/>: its Dims entry, defaulting to
    /// <paramref name="k"/> for hand-built meshes that carry no Dims.</summary>
    private int Stride(string channel, int k)
    {
        int stride = Dims.TryGetValue(channel, out var s) ? s : k;
        if (stride < k)
            throw new InvalidOperationException(
                $"channel '{channel}' stores {stride} components per vertex but {k} were requested. " +
                "This mesh's layout can't supply them");
        return stride;
    }

    public IReadOnlyList<Vector3> AsVector3(string channel)
    {
        var d = Channels[channel];
        int st = Stride(channel, 3);
        var r = new Vector3[VertexCount];
        for (int i = 0; i < VertexCount; i++) r[i] = new Vector3(d[i * st], d[i * st + 1], d[i * st + 2]);
        return r;
    }

    public IReadOnlyList<Vector4> AsVector4(string channel)
    {
        var d = Channels[channel];
        int st = Stride(channel, 4);
        var r = new Vector4[VertexCount];
        for (int i = 0; i < VertexCount; i++) r[i] = new Vector4(d[i * st], d[i * st + 1], d[i * st + 2], d[i * st + 3]);
        return r;
    }

    public IReadOnlyList<Vector2> AsVector2(string channel)
    {
        var d = Channels[channel];
        int st = Stride(channel, 2);
        var r = new Vector2[VertexCount];
        for (int i = 0; i < VertexCount; i++) r[i] = new Vector2(d[i * st], d[i * st + 1]);
        return r;
    }

    /// <summary>One entry of <c>m_VertexData.m_Channels</c>. The list is POSITIONAL: index <c>i</c> maps
    /// to <see cref="ChannelNames"/><c>[i]</c>, unused slots carry <c>Dimension == 0</c>.</summary>
    public readonly record struct ChannelDef(int Stream, int Offset, int Format, int Dimension);

    /// <summary>One entry of <c>m_SubMeshes</c>.</summary>
    public readonly record struct SubMeshDef(int FirstByte, int IndexCount, int BaseVertex);

    // Unity VertexChannel order (2018+); the one source of truth for the positional index → name map.
    internal static readonly string[] ChannelNames =
    {
        "Vertex", "Normal", "Tangent", "Color",
        "TexCoord0", "TexCoord1", "TexCoord2", "TexCoord3",
        "TexCoord4", "TexCoord5", "TexCoord6", "TexCoord7",
        "BlendWeight", "BlendIndices",
    };

    // VertexFormat → component byte size.
    private static int FormatSize(int fmt) => fmt switch
    {
        0 or 10 or 11 => 4,        // Float32 / UInt32 / SInt32
        1 or 4 or 5 or 8 or 9 => 2,// Float16 / UNorm16 / SNorm16 / UInt16 / SInt16
        2 or 3 or 6 or 7 => 1,     // (S/U)Norm8 / (S/U)Int8
        _ => throw new FormatException($"unknown vertex format {fmt}"),
    };

    /// <summary>Adapt an AssetsTools Mesh type-tree field and decode it. <paramref name="name"/> overrides
    /// the object's own <c>m_Name</c> for the decoded mesh's <see cref="Name"/> — the export routes pass the
    /// RENDERER SLOT name, which is what every glb the app writes carries its part under and what the
    /// project ledger keys on. Null keeps the asset's own name.</summary>
    public static UnityMesh Decode(AssetTypeValueField mesh, string? name = null)
    {
        var vd = mesh["m_VertexData"];
        int n = vd["m_VertexCount"].AsInt;
        var channels = vd["m_Channels"]["Array"].Children
            // dimension's low nibble is the STORED component count (the storage stride), the high nibble
            // the semantic count (0x34 on Normal = stored 4, semantic 3, 4th a zero pad). Mask to stored.
            .Select(c => new ChannelDef(c["stream"].AsInt, c["offset"].AsInt, c["format"].AsInt, c["dimension"].AsInt & 0xF))
            .ToList();
        byte[] vertexData = ReadByteArray(vd["m_DataSize"]);

        int indexFormat = mesh["m_IndexFormat"].AsInt;       // 0 = uint16, 1 = uint32
        byte[] indexBuffer = ReadByteArray(mesh["m_IndexBuffer"]);
        var subMeshes = mesh["m_SubMeshes"]["Array"].Children
            .Select(sm => new SubMeshDef((int)sm["firstByte"].AsUInt, (int)sm["indexCount"].AsUInt, (int)sm["baseVertex"].AsUInt))
            .ToList();

        return DecodeRaw(name ?? mesh["m_Name"].AsString, n, channels, vertexData, indexFormat, indexBuffer, subMeshes);
    }

    /// <summary>The pure codec: stream-interleaved vertex blob + index buffer to channel arrays and
    /// per-submesh index lists.</summary>
    public static UnityMesh DecodeRaw(string name, int vertexCount,
        IReadOnlyList<ChannelDef> channels, byte[] vertexData,
        int indexFormat, byte[] indexBuffer, IReadOnlyList<SubMeshDef> subMeshes)
    {
        var (strides, starts) = StreamInfo(channels, vertexCount);

        // the inferred stream layout must fit the blob; a read past m_VertexData means a corrupt header,
        // so fail clearly rather than as an IndexOutOfRange deep in the per-vertex loop
        if (vertexCount > 0)
            foreach (var ch in channels)
            {
                if (ch.Dimension == 0) continue;
                int sz = FormatSize(ch.Format);
                int maxByte = starts[ch.Stream] + (vertexCount - 1) * strides[ch.Stream]
                              + ch.Offset + ch.Dimension * sz;
                if (maxByte > vertexData.Length)
                    throw new System.IO.InvalidDataException(
                        $"vertex stream overruns m_VertexData: channel needs {maxByte} bytes but the blob " +
                        $"is {vertexData.Length} (stride {strides[ch.Stream]}, {vertexCount} verts). Corrupt mesh header.");
            }

        var outChannels = new Dictionary<string, float[]>();
        var outDims = new Dictionary<string, int>();
        for (int ci = 0; ci < channels.Count; ci++)
        {
            var ch = channels[ci];
            if (ch.Dimension == 0) continue;
            int stride = strides[ch.Stream], start = starts[ch.Stream];
            int sz = FormatSize(ch.Format);
            var values = new float[vertexCount * ch.Dimension];
            for (int v = 0; v < vertexCount; v++)
            {
                int rowBase = start + v * stride + ch.Offset;
                for (int d = 0; d < ch.Dimension; d++)
                    values[v * ch.Dimension + d] = ReadComponent(vertexData, rowBase + d * sz, ch.Format);
            }
            outChannels[ChannelNames[ci]] = values;
            outDims[ChannelNames[ci]] = ch.Dimension;
        }

        return new UnityMesh
        {
            Name = name,
            VertexCount = vertexCount,
            Channels = outChannels,
            Dims = outDims,
            Submeshes = DecodeSubmeshes(indexFormat, indexBuffer, subMeshes),
        };
    }

    /// <summary>Per-stream stride + 16-aligned start offsets.</summary>
    private static (Dictionary<int, int> strides, Dictionary<int, int> starts) StreamInfo(
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
        int off = 0;
        var order = strides.Keys.OrderBy(s => s).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            int s = order[i];
            starts[s] = off;
            int size = n * strides[s];
            if (i < order.Count - 1) size = (size + 15) & ~15;  // pad intermediate streams to 16, not the last
            off += size;
        }
        return (strides, starts);
    }

    /// <summary>Per-submesh index lists, baseVertex folded in.</summary>
    private static List<int[]> DecodeSubmeshes(int fmt, byte[] raw, IReadOnlyList<SubMeshDef> subMeshes)
    {
        int step = fmt == 0 ? 2 : 4;       // 0 = uint16, 1 = uint32
        var result = new List<int[]>();
        foreach (var sm in subMeshes)
        {
            int start = sm.FirstByte / step;
            var idx = new int[sm.IndexCount];
            for (int i = 0; i < sm.IndexCount; i++)
            {
                int pos = (start + i) * step;
                int raw0 = fmt == 0 ? BitConverter.ToUInt16(raw, pos) : (int)BitConverter.ToUInt32(raw, pos);
                idx[i] = raw0 + sm.BaseVertex;
            }
            result.Add(idx);
        }
        return result;
    }

    /// <summary>Read a Unity byte vector, handling both shapes.</summary>
    private static byte[] ReadByteArray(AssetTypeValueField f)
    {
        // TypelessData (m_DataSize) carries the bytes on the field itself; vector<UInt8>
        // (m_IndexBuffer) on its "Array" child. Either may be byte-optimized or expanded as children.
        try { if (f.AsByteArray is { } b) return b; } catch { }
        var arr = f["Array"];
        try { if (arr.AsByteArray is { } b2) return b2; } catch { }
        var kids = arr.Children;
        var bytes = new byte[kids.Count];
        for (int i = 0; i < kids.Count; i++) bytes[i] = (byte)kids[i].AsInt;
        return bytes;
    }

    private static float ReadComponent(byte[] b, int pos, int fmt) => fmt switch
    {
        0 => BitConverter.ToSingle(b, pos),
        1 => (float)BitConverter.ToHalf(b, pos),
        2 => b[pos] / 255f,                                   // UNorm8
        3 => Math.Max((sbyte)b[pos] / 127f, -1f),            // SNorm8
        4 => BitConverter.ToUInt16(b, pos) / 65535f,         // UNorm16
        5 => Math.Max(BitConverter.ToInt16(b, pos) / 32767f, -1f), // SNorm16
        6 => b[pos],                                          // UInt8
        7 => (sbyte)b[pos],                                   // SInt8
        8 => BitConverter.ToUInt16(b, pos),                  // UInt16
        9 => BitConverter.ToInt16(b, pos),                   // SInt16
        10 => BitConverter.ToUInt32(b, pos),                 // UInt32
        11 => BitConverter.ToInt32(b, pos),                  // SInt32
        _ => throw new FormatException($"unknown vertex format {fmt}"),
    };
}
