using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Cutting a donor draw range into stream-sized pieces: the pieces drawn in order are the range's
/// triangles in the range's order, no piece holds more vertices than the cap, every local index names a
/// vertex of its own piece, and a vertex two pieces share is the same donor vertex in both.
/// </summary>
public class DonorPiecesTests
{
    /// <summary>A strip of <paramref name="triangles"/> triangles over consecutive vertices, so neighbours
    /// share two vertices and every cut duplicates a border.</summary>
    private static uint[] Strip(int triangles)
    {
        var ib = new List<uint>();
        for (int t = 0; t < triangles; t++) ib.AddRange(new[] { (uint)t, (uint)t + 1, (uint)t + 2 });
        return ib.ToArray();
    }

    private static uint[] Rebuilt(IReadOnlyList<DonorPieces.Piece> pieces) =>
        pieces.SelectMany(p => p.LocalIndices.Select(l => p.LocalToDonor[l])).ToArray();

    [Fact]
    public void A_range_that_fits_is_one_piece_in_the_range_s_own_order()
    {
        var ib = Strip(10);
        var pieces = DonorPieces.Cut(ib, baseVertex: 0, capVertices: 100);
        var piece = Assert.Single(pieces);
        Assert.Equal(12, piece.Vertices);
        Assert.Equal(30, piece.Indices);
        Assert.Equal(ib, Rebuilt(pieces));
        // first-use order: vertex v is local v on a strip
        Assert.Equal(Enumerable.Range(0, 12).Select(v => (uint)v), piece.LocalToDonor);
        Assert.Equal((12 * 40 + 15) / 16, piece.StreamElements);
    }

    [Fact]
    public void A_range_over_the_cap_is_cut_between_triangles_and_draws_the_same_sequence()
    {
        var ib = Strip(100);   // 102 vertices
        var pieces = DonorPieces.Cut(ib, baseVertex: 0, capVertices: 30);
        Assert.True(pieces.Count >= 4, $"{pieces.Count} pieces");
        Assert.All(pieces, p => Assert.InRange(p.Vertices, 3, 30));
        Assert.All(pieces, p => Assert.Equal(0, p.Indices % 3));
        Assert.All(pieces, p => Assert.All(p.LocalIndices, l => Assert.InRange((int)l, 0, p.Vertices - 1)));
        Assert.Equal(ib, Rebuilt(pieces));
        // a border is duplicated: the strip's two shared vertices appear in both neighbouring pieces
        for (int i = 1; i < pieces.Count; i++)
            Assert.Equal(2, pieces[i - 1].LocalToDonor.Intersect(pieces[i].LocalToDonor).Count());
        Assert.Equal(102 + 2 * (pieces.Count - 1), pieces.Sum(p => p.Vertices));
    }

    [Fact]
    public void The_base_vertex_is_folded_into_the_donor_numbers()
    {
        var ib = Strip(2);
        var pieces = DonorPieces.Cut(ib, baseVertex: 500, capVertices: 100);
        Assert.Equal(new uint[] { 500, 501, 502, 503 }, Assert.Single(pieces).LocalToDonor);
        Assert.Equal(ib.Select(i => i + 500), Rebuilt(pieces));
    }

    [Fact]
    public void A_triangle_reusing_a_vertex_counts_it_once()
    {
        // degenerate triangles never push a piece past its cap by counting one vertex twice
        var pieces = DonorPieces.Cut(new uint[] { 0, 0, 1, 1, 1, 1, 2, 2, 3 }, baseVertex: 0, capVertices: 4);
        Assert.Single(pieces);
        Assert.Equal(new uint[] { 0, 1, 2, 3 }, pieces[0].LocalToDonor);
    }

    [Fact]
    public void The_window_is_the_render_target_limit_at_the_stream_stride()
    {
        Assert.Equal(6553, DonorPieces.WindowVertices);
        Assert.Throws<ArgumentException>(() => DonorPieces.Cut(new uint[] { 0, 1 }, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => DonorPieces.Cut(new uint[] { 0, 1, 2 }, 0, 2));
    }
}
