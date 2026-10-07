using System;
using System.IO;
using System.Runtime.Intrinsics.X86;
using Remold.Core.Bundles;
using Remold.Core.Mesh;

namespace Remold.Core.Migoto;

/// <summary>
/// The 3DMigoto D3D11 buffer hashes (ib / vb0 / vb1 / vb2) for a character mesh, computed straight from its
/// bundle — NO frame dump, so a mod is authored fully offline.
///
/// 3DMigoto hashes each buffer at CreateBuffer as standard CRC32C (Castagnoli, init 0xFFFFFFFF, final
/// invert) over  [initial data bytes] || D3D11_BUFFER_DESC (24 bytes, little-endian). So:
///   vb0  DYNAMIC, created with NO initial data -> hash = CRC32C(desc) alone, depending only on
///        ByteWidth. It therefore COLLIDES for any two parts with the same vertex count.
///   vb1  color/UV, static -> hash includes the stream-1 content bytes.
///   vb2  original static skin/attribute stream; never a repacked donor stream.
///   ib   topology, static -> includes the index bytes. Unique per TOPOLOGY, not per mesh: distinct
///        meshes sharing an index buffer (one garment pattern, per-character vertex remodels) share
///        it. Still the robust match key — that wider radius is what an anchored override fires on.
/// </summary>
public static class BufferHash
{
    /// <summary>The offline buffer hashes plus the mesh facts that determine them. Static slot hashes are
    /// null when the original mesh has no corresponding stream ID.</summary>
    public readonly record struct Hashes(
        int VertexCount, int Stream0Stride, int IndexBytes, int IndexFormat,
        uint Ib, uint Vb0, uint? Vb1, uint? Vb2 = null)
    {
        // VB0 describes the posed-buffer allocation rather than static vertex content.
        // Match topology plus the original static attributes.
        public DrawSelector Selector => new(Ib.ToString("x8"), null,
            Vb1?.ToString("x8"), Vb2?.ToString("x8"));
    }

    /// <summary><paramref name="reader"/> lets a caller hashing several meshes out of one bundle share the
    /// parse; null opens the bundle for this call alone.</summary>
    public static Hashes Compute(byte[] deobfuscatedBundle, string meshName, MeshSelector which = default,
        BundleReader? reader = null)
    {
        var field = (reader ?? new BundleReader()).GetMeshField(deobfuscatedBundle, meshName, which)
            ?? throw new InvalidDataException(
                $"the game files no longer hold the mesh '{meshName}'. Rescan, then build again");
        return Compute(MeshRaw.From(field));
    }

    public static Hashes Compute(MeshRaw mesh)
    {
        if (mesh.StreamIds.Count == 0) throw new InvalidDataException("mesh has no vertex streams");

        int stream0 = mesh.StreamIds.IndexOf(0);
        // Accepted: no roster mesh lacks stream 0, so only a malformed asset reaches this. The signature
        // index swallows it as an unreadable mesh and the sharing pass records a problem row; a build meets
        // it only through the out-of-index fallback, as a plain failure naming nothing more.
        if (stream0 < 0) throw new InvalidDataException("mesh has no position stream");
        int stride0 = mesh.Stride(stream0);
        uint vb0 = HashDynamicVertexBuffer((uint)(mesh.VertexCount * stride0));
        uint ib = HashStatic(mesh.Index, bind: 2);                                       // D3D11_BIND_INDEX_BUFFER
        uint? StaticSlot(int slot) => mesh.StreamIds.IndexOf(slot) is var ordinal && ordinal >= 0
            ? HashStatic(mesh.StreamBytes(ordinal), bind: 1) : null;
        return new Hashes(mesh.VertexCount, stride0, mesh.Index.Length, mesh.IndexFormat, ib, vb0,
            StaticSlot(1), StaticSlot(2));
    }

    // D3D11_USAGE: DEFAULT=0, DYNAMIC=2.  BindFlags: VERTEX=1, INDEX=2.  CPUAccess: WRITE=0x10000.
    static uint HashDynamicVertexBuffer(uint byteWidth) =>
        Crc32c(Desc(byteWidth, usage: 2, bind: 1, cpu: 0x10000));

    // a static (DEFAULT, no CPU access) buffer created with its content as initial data
    static uint HashStatic(byte[] content, uint bind)
    {
        var buf = new byte[content.Length + 24];
        content.CopyTo(buf, 0);
        Desc((uint)content.Length, usage: 0, bind: bind, cpu: 0).CopyTo(buf, content.Length);
        return Crc32c(buf);
    }

    static byte[] Desc(uint byteWidth, uint usage, uint bind, uint cpu)
    {
        var d = new byte[24];                                  // D3D11_BUFFER_DESC, 6 x UINT, little-endian
        BitConverter.TryWriteBytes(d.AsSpan(0, 4), byteWidth);
        BitConverter.TryWriteBytes(d.AsSpan(4, 4), usage);
        BitConverter.TryWriteBytes(d.AsSpan(8, 4), bind);
        BitConverter.TryWriteBytes(d.AsSpan(12, 4), cpu);
        // MiscFlags @16 and StructureByteStride @20 are 0.
        return d;
    }

    static readonly uint[] Crc32cTable = BuildCrc32cTable();
    static uint[] BuildCrc32cTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0x82f63b78 : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>Standard CRC32C (Castagnoli, init/final invert). <paramref name="crc"/> is a previous
    /// result to CONTINUE from, so a hash can span several buffers without joining them.
    ///
    /// <para>SSE4.2's <c>crc32</c> instruction computes this exact polynomial, so the hardware and table
    /// paths are two spellings of one function: a mesh addressed on a machine with the instruction and one
    /// without must land on the same hash, or the same mod would key on two different meshes.</para></summary>
    internal static uint Crc32c(ReadOnlySpan<byte> data, uint crc = 0)
    {
        if (!Sse42.IsSupported) return Crc32cSoftware(data, crc);
        crc ^= 0xffffffff;
        int i = 0;
        if (Sse42.X64.IsSupported)
        {
            ulong wide = crc;
            for (; i + 8 <= data.Length; i += 8)
                wide = Sse42.X64.Crc32(wide, BitConverter.ToUInt64(data.Slice(i, 8)));
            crc = (uint)wide;
        }
        for (; i < data.Length; i++) crc = Sse42.Crc32(crc, data[i]);
        return crc ^ 0xffffffff;
    }

    /// <summary>The table path on its own — the fallback where the instruction is absent, and the reference
    /// the equivalence test measures the hardware path against.</summary>
    internal static uint Crc32cSoftware(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc ^= 0xffffffff;
        foreach (var b in data) crc = (crc >> 8) ^ Crc32cTable[(crc ^ b) & 0xff];
        return crc ^ 0xffffffff;
    }
}
