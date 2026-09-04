using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Remold.Core.Mesh;
using SharpGLTF.Schema2;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The stride-aware <c>AsVector*</c> accessors. A packed mesh's Normal channel is 4-wide, and a hard-coded
/// stride-3 read emits mis-strided garbage from vertex 1 onward on every export — SILENTLY, because the
/// export normalizes afterward. Pins that the accessor reads at the channel's own stored stride, refuses
/// loudly when the stride can't supply the requested components, and carries the true normals end to end.
/// </summary>
public class PackedNormalExportTests
{
    /// <summary>3 verts, Normal stored 4-wide with DISTINCT 4th components, so a mis-stride visibly
    /// corrupts vertex 1's normal — (7,1,0) instead of (1,0,0) under a stride-3 read. The triangle is a
    /// real one (three distinct vertices): a face naming a vertex twice would be re-pointed at split
    /// copies on export (<see cref="MeshGltf.SplitDuplicateFaces"/>), which is not this test's
    /// subject.</summary>
    private static UnityMesh PackedNormalMesh() => new()
    {
        Name = "c_packed_export",
        VertexCount = 3,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0, 0, 10, 0 },
            ["Normal"] = new float[] { 0, 1, 0, 7, /*v1*/ 1, 0, 0, 9, /*v2*/ 0, 0, 1, 5 },
        },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["Normal"] = 4 },
        Submeshes = new List<int[]> { new[] { 0, 1, 2 } },
    };

    [Fact]
    public void AsVector3_ReadsAtTheChannelsOwnStoredStride()
    {
        var n = PackedNormalMesh().AsVector3("Normal");   // 4-wide channel, first 3 components per vertex
        Assert.Equal(new Vector3(0, 1, 0), n[0]);
        Assert.Equal(new Vector3(1, 0, 0), n[1]);         // stride-3 would read (7, 1, 0) here
    }

    [Fact]
    public void AsVector3_Refuses_AChannelNarrowerThanRequested()
    {
        var mesh = new UnityMesh
        {
            VertexCount = 2,
            Channels = new Dictionary<string, float[]> { ["TexCoord0"] = new float[] { 0.1f, 0.2f, 0.3f, 0.4f } },
            Dims = new Dictionary<string, int> { ["TexCoord0"] = 2 },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => mesh.AsVector3("TexCoord0"));
        Assert.Contains("TexCoord0", ex.Message);
        Assert.Contains("2 components", ex.Message);
    }

    [Fact]
    public void ExportGlb_PackedNormalMesh_CarriesTheTruePerVertexNormals()
    {
        var path = Path.Combine(Path.GetTempPath(), "remold-packednormal-" + Guid.NewGuid().ToString("N") + ".glb");
        try
        {
            MeshGltf.ExportGlb(PackedNormalMesh(), path);

            var model = ModelRoot.Load(path);
            var normals = model.LogicalMeshes[0].Primitives[0].GetVertexAccessor("NORMAL")!.AsVector3Array();

            // AxisConvention.Normal negates X then normalizes; all inputs are already unit here.
            Assert.Equal(3, normals.Count);
            AssertVec(new Vector3(0, 1, 0), normals[0]);
            AssertVec(new Vector3(-1, 0, 0), normals[1]);   // a stride-3 read would export ~(-0.99, 0.14, 0) garbage here
            AssertVec(new Vector3(0, 0, 1), normals[2]);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static void AssertVec(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }
}
