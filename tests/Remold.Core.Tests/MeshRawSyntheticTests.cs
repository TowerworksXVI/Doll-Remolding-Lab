using System;
using System.IO;
using Remold.Core.Bundles;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The raw stream/index slicing and the offline 3DMigoto buffer hashes over a from-scratch synthetic Mesh:
/// one Position channel (stream 0, Float32×3, stride 12), one uint16 submesh.
/// </summary>
public class MeshRawSyntheticTests
{
    [Fact]
    public void From_SlicesStream0Verbatim_AndReadsIndexAndSubmesh()
    {
        using var t = new TempGame();
        string bundle = t.At("synthetic.bundle");
        // a single triangle: three positions (stride 12), one submesh (indices 0,1,2)
        float[] positions = { 0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f };
        int[] tris = { 0, 1, 2 };
        SyntheticBundle.BuildOneMesh(bundle, "poc_mesh", positions, tris);
        byte[] plain = File.ReadAllBytes(bundle);

        var field = new BundleReader().GetMeshField(plain, "poc_mesh");
        Assert.NotNull(field);

        var raw = MeshRaw.From(field!);
        Assert.Equal(3, raw.VertexCount);
        Assert.Equal(new[] { 0 }, raw.StreamIds);
        Assert.Equal(12, raw.Stride(0));

        // stream0 is the tightly-packed Float32×3 blob, sliced verbatim
        var s0 = raw.StreamBytes(0);
        Assert.Equal(3 * 12, s0.Length);
        var expected = new byte[positions.Length * 4];
        Buffer.BlockCopy(positions, 0, expected, 0, expected.Length);
        Assert.Equal(expected, s0);

        // index buffer: uint16 (format 0), three indices = six bytes
        Assert.Equal(0, raw.IndexFormat);
        Assert.Equal(6, raw.Index.Length);

        var sub = Assert.Single(raw.Submeshes);
        Assert.Equal(0u, sub.FirstByte);
        Assert.Equal(3u, sub.IndexCount);
        Assert.Equal(0u, sub.BaseVertex);
    }

    [Fact]
    public void BufferHash_IsDeterministic_AndVb1NullForSingleStream()
    {
        using var t = new TempGame();
        string bundle = t.At("synthetic.bundle");
        float[] positions = { 0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f };
        SyntheticBundle.BuildOneMesh(bundle, "poc_mesh", positions, new[] { 0, 1, 2 });
        byte[] plain = File.ReadAllBytes(bundle);

        var raw = MeshRaw.From(new BundleReader().GetMeshField(plain, "poc_mesh")!);
        var h = BufferHash.Compute(raw);

        Assert.Equal(3, h.VertexCount);
        Assert.Equal(12, h.Stream0Stride);
        Assert.Null(h.Vb1);                    // only one vertex stream present
        // vb0 is a desc-only hash keyed on byte-width (verts * stride0); recomputes identically
        Assert.Equal(h.Vb0, BufferHash.Compute(raw).Vb0);
        Assert.Equal(h.Ib, BufferHash.Compute(raw).Ib);
    }
}
