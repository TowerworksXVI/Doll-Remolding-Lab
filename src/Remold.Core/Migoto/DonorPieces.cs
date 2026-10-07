using System;
using System.Collections.Generic;

namespace Remold.Core.Migoto;

/// <summary>
/// The pieces a donor draw range is cut into for the per-copy pose route, where the posed stream is
/// written by a pixel shader into a buffer render target: a buffer render-target view spans at most
/// <see cref="WindowElements"/> elements, 6,553 vertices at the game's 40-byte stride, so a larger range
/// is drawn as several pieces, each from its own stream buffer.
///
/// <para>A piece is a contiguous run of the range's triangles, so the pieces drawn in order draw the
/// range's triangle sequence unchanged (blend order included). Each piece names the donor vertices it
/// uses, in first-use order, and its triangles in those local numbers; a vertex used by triangles in two
/// pieces appears in both, written from the same inputs by the same shader, so its data is identical in
/// each. The cap is reached by the triangles themselves: a range that fits is one piece, and the cut
/// never lands inside a triangle.</para>
/// </summary>
public static class DonorPieces
{
    /// <summary>Elements one buffer render-target view can span (D3D11's render-to-buffer window).</summary>
    public const int WindowElements = 16384;

    /// <summary>Bytes of one element of the stream render target, a float4.</summary>
    public const int ElementBytes = 16;

    /// <summary>The posed stream's vertex stride: position, normal and tangent, 40 bytes.</summary>
    public const int VertexStride = 40;

    /// <summary>Vertices one stream buffer carries under the window: 6,553.</summary>
    public static int WindowVertices => WindowElements * ElementBytes / VertexStride;

    /// <summary>One piece: the donor vertex each local vertex is, and the range's triangles that fall in
    /// this piece, as local vertex numbers in the range's own order.</summary>
    public sealed record Piece(uint[] LocalToDonor, ushort[] LocalIndices)
    {
        public int Vertices => LocalToDonor.Length;
        public int Indices => LocalIndices.Length;
        /// <summary>Float4 elements the piece's stream buffer needs to hold its vertices at 40 bytes.</summary>
        public int StreamElements => (Vertices * VertexStride + ElementBytes - 1) / ElementBytes;
    }

    /// <summary>Cut one draw range into pieces of at most <paramref name="capVertices"/> vertices.
    /// <paramref name="indices"/> are the range's indices as the donor's index buffer holds them;
    /// <paramref name="baseVertex"/> is the draw's base vertex, folded into the donor numbers here so each
    /// piece draws at base 0. A range whose count is not a multiple of three, or a cap below three, is a
    /// caller error.</summary>
    public static IReadOnlyList<Piece> Cut(ReadOnlySpan<uint> indices, int baseVertex, int capVertices)
    {
        if (indices.Length % 3 != 0)
            throw new ArgumentException($"a draw range of {indices.Length} indices is not whole triangles", nameof(indices));
        if (capVertices < 3)
            throw new ArgumentOutOfRangeException(nameof(capVertices), capVertices, "a piece holds at least one triangle");
        if (capVertices > ushort.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(capVertices), capVertices, "a piece's local indices are 16-bit");
        var pieces = new List<Piece>();
        var local = new Dictionary<uint, ushort>();
        var order = new List<uint>();
        var tris = new List<ushort>();
        void Close()
        {
            pieces.Add(new Piece(order.ToArray(), tris.ToArray()));
            local.Clear(); order.Clear(); tris.Clear();
        }
        // one triangle's corners, allocated once: a stackalloc inside the loop would grow the stack by a
        // triangle's worth per iteration for the whole call, which a large donor could overflow
        Span<uint> corners = stackalloc uint[3];
        for (int t = 0; t < indices.Length; t += 3)
        {
            uint a = (uint)(indices[t] + baseVertex), b = (uint)(indices[t + 1] + baseVertex), c = (uint)(indices[t + 2] + baseVertex);
            int fresh = (local.ContainsKey(a) ? 0 : 1) + (local.ContainsKey(b) || b == a ? 0 : 1)
                      + (local.ContainsKey(c) || c == a || c == b ? 0 : 1);
            if (tris.Count > 0 && order.Count + fresh > capVertices) Close();
            corners[0] = a; corners[1] = b; corners[2] = c;
            foreach (uint v in corners)
            {
                if (!local.TryGetValue(v, out ushort l))
                {
                    l = (ushort)order.Count;
                    local[v] = l;
                    order.Add(v);
                }
                tris.Add(l);
            }
        }
        if (tris.Count > 0 || pieces.Count == 0) Close();
        return pieces;
    }
}
