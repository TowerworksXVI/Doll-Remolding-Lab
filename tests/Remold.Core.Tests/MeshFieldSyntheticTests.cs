using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Mesh;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The thin AssetsTools type-tree ADAPTER half of the mesh read, over a from-scratch synthetic Mesh. The
/// byte codec beneath is covered exhaustively by <c>MeshCodecTests</c>; this proves the field adapter
/// reads a hand-built Mesh type tree correctly.
/// </summary>
public class MeshFieldSyntheticTests
{
    [Fact]
    public void Two_reads_of_one_bundle_hand_back_independent_fields()
    {
        // A reader keeps a bundle open across its reads, and the compile path REWRITES what it is handed
        // (the pooled union overwrites the bone table). If the two reads shared a value tree, the second
        // caller would silently receive the first's edits.
        using var t = new TempGame();
        string bundle = t.At("shared.bundle");
        SyntheticBundle.BuildOneMesh(bundle, "poc_mesh", new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 });
        byte[] plain = File.ReadAllBytes(bundle);

        var reader = new BundleReader();
        var first = reader.GetMeshField(plain, "poc_mesh")!;
        first["m_Name"].AsString = "rewritten";

        var second = reader.GetMeshField(plain, "poc_mesh")!;
        Assert.Equal("poc_mesh", second["m_Name"].AsString);
    }

    [Fact]
    public void A_reader_past_its_parse_bound_re_reads_the_evicted_bundle_correctly()
    {
        // A parse pins a whole decompressed bundle, so a reader that outlives one operation keeps only the
        // last few. Everything the bound can go wrong about is here: a bundle read again after eviction has
        // to answer the same, and a field handed out BEFORE its parse was dropped has to stay readable —
        // callers hold them across other reads.
        using var t = new TempGame();
        var plains = new byte[12][];
        for (int i = 0; i < plains.Length; i++)
        {
            string path = t.At($"bounded{i}.bundle");
            SyntheticBundle.BuildOneMesh(path, $"mesh_{i}",
                new[] { 0f, 0f, i, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 });
            plains[i] = File.ReadAllBytes(path);
        }

        var reader = new BundleReader();
        var early = reader.GetMeshField(plains[0], "mesh_0")!;
        for (int i = 1; i < plains.Length; i++)
            Assert.Equal($"mesh_{i}", reader.GetMeshField(plains[i], $"mesh_{i}")!["m_Name"].AsString);

        // the field from the long-evicted first parse still carries its own values
        Assert.Equal("mesh_0", early["m_Name"].AsString);
        Assert.Equal(3, UnityMesh.Decode(early).VertexCount);
        // and re-reading that bundle re-parses it to the same answer
        Assert.Equal("mesh_0", reader.GetMeshField(plains[0], "mesh_0")!["m_Name"].AsString);
    }

    [Fact]
    public void GetMeshField_ThenDecode_ReadsVertexPositionsAndSubmeshIndices()
    {
        using var t = new TempGame();
        string bundle = t.At("synthetic.bundle");
        // a single triangle: three positions, one submesh (indices 0,1,2)
        float[] positions = { 0f, 0f, 0f,  1f, 0f, 0f,  0f, 1f, 0f };
        int[] tris = { 0, 1, 2 };
        SyntheticBundle.BuildOneMesh(bundle, "poc_mesh", positions, tris);
        byte[] plain = File.ReadAllBytes(bundle);

        var field = new BundleReader().GetMeshField(plain, "poc_mesh");
        Assert.NotNull(field);

        var mesh = UnityMesh.Decode(field!);
        Assert.Equal("poc_mesh", mesh.Name);
        Assert.Equal(3, mesh.VertexCount);
        Assert.True(mesh.Has("Vertex"));

        var verts = mesh.AsVector3("Vertex");
        Assert.Equal(3, verts.Count);
        Assert.Equal(0f, verts[0].X); Assert.Equal(0f, verts[0].Y); Assert.Equal(0f, verts[0].Z);
        Assert.Equal(1f, verts[1].X);
        Assert.Equal(1f, verts[2].Y);

        var sub = Assert.Single(mesh.Submeshes);
        Assert.Equal(new[] { 0, 1, 2 }, sub);
    }

    [Fact]
    public void GetMeshField_ReturnsNull_ForAnAbsentName()
    {
        using var t = new TempGame();
        string bundle = t.At("synthetic.bundle");
        SyntheticBundle.BuildOneMesh(bundle, "poc_mesh", new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f }, new[] { 0, 1, 2 });
        byte[] plain = File.ReadAllBytes(bundle);

        Assert.Null(new BundleReader().GetMeshField(plain, "not_here"));
    }
}
