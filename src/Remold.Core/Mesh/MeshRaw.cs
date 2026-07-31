using System;
using System.Collections.Generic;
using System.Linq;
using AssetsTools.NET;

namespace Remold.Core.Mesh;

/// <summary>
/// Byte-exact raw slicing of a Unity mesh's interleaved vertex/index data, the shape the 3DMigoto path
/// consumes: each stream IS a GPU vertex buffer, the index blob IS the index buffer. No decode —
/// streams are sliced verbatim by the stored <c>m_Channels</c> layout.
/// </summary>
public sealed class MeshRaw
{
    public int VertexCount;
    public List<int> StreamIds = new();        // stream numbers present, ascending; ordinal 0 = vb0, 1 = vb1, …
    public int[] Strides = Array.Empty<int>();  // by ordinal
    public byte[] VData = Array.Empty<byte>();
    private int[] starts = Array.Empty<int>();  // stream start offset in VData, by ordinal
    public byte[] Index = Array.Empty<byte>();
    public int IndexFormat;                     // 0 = u16, 1 = u32
    public List<(uint FirstByte, uint IndexCount, uint BaseVertex)> Submeshes = new();

    public int Stride(int ordinal) => Strides[ordinal];
    public byte[] StreamBytes(int ordinal) => VData.AsSpan(starts[ordinal], VertexCount * Strides[ordinal]).ToArray();

    public static MeshRaw From(AssetTypeValueField field)
    {
        var vd = field["m_VertexData"];
        var m = new MeshRaw { VertexCount = vd["m_VertexCount"].AsInt };

        // per-stream stride = max(offset + size) over that stream's live channels
        var chans = vd["m_Channels"]["Array"].Children
            .Select(c => (stream: c["stream"].AsInt, offset: c["offset"].AsInt,
                          format: c["format"].AsInt, dim: c["dimension"].AsInt & 0xF));
        var strideByStream = new Dictionary<int, int>();
        foreach (var c in chans)
        {
            if (c.dim == 0) continue;
            int end = c.offset + FormatSize(c.format) * c.dim;
            strideByStream[c.stream] = Math.Max(strideByStream.GetValueOrDefault(c.stream), end);
        }
        m.StreamIds = strideByStream.Keys.OrderBy(s => s).ToList();
        m.Strides = m.StreamIds.Select(s => strideByStream[s]).ToArray();

        // Unity lays streams out sequentially, padding each intermediate stream up to 16 bytes
        m.VData = ReadBytes(vd["m_DataSize"]);
        m.starts = new int[m.StreamIds.Count];
        int cursor = 0;
        for (int i = 0; i < m.StreamIds.Count; i++)
        {
            m.starts[i] = cursor;
            int size = m.VertexCount * m.Strides[i];
            if (i < m.StreamIds.Count - 1) size = (size + 15) & ~15;
            cursor += size;
        }

        m.Index = ReadBytes(field["m_IndexBuffer"]);
        m.IndexFormat = field["m_IndexFormat"].AsInt;
        m.Submeshes = field["m_SubMeshes"]["Array"].Children
            .Select(s => (s["firstByte"].AsUInt, s["indexCount"].AsUInt, s["baseVertex"].AsUInt)).ToList();
        return m;
    }

    private static int FormatSize(int fmt) => fmt switch
    {
        0 or 10 or 11 => 4,
        1 or 4 or 5 or 8 or 9 => 2,
        2 or 3 or 6 or 7 => 1,
        _ => throw new FormatException($"unknown vertex format {fmt}"),
    };

    private static byte[] ReadBytes(AssetTypeValueField f)
    {
        try { if (f.AsByteArray is { } b) return b; } catch { }
        var arr = f["Array"];
        try { if (arr.AsByteArray is { } b2) return b2; } catch { }
        var kids = arr.Children;
        var bytes = new byte[kids.Count];
        for (int i = 0; i < kids.Count; i++) bytes[i] = (byte)kids[i].AsInt;
        return bytes;
    }
}
