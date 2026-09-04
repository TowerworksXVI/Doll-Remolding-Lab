using System.Collections.Generic;
using Remold.Core.Mesh;
using Xunit;

namespace Remold.Core.Tests;

public class SendBackGeometryTests
{
    [Fact]
    public void HigherUvEdit_IsContentEvenWhenUv0DidNotMove()
    {
        var baseline = Payload(UvMesh());
        var returned = Payload(UvMesh());
        returned.Channels["TexCoord1"][2] += 0.01f;

        Assert.False(SendBackGeometry.SameContent(returned, baseline));
    }

    [Fact]
    public void EveryTransportedHigherSet_IsComparedAtTriangleCorners()
    {
        var baseline = Payload(UvMesh());
        var returned = Payload(UvMesh());
        returned.Channels["TexCoord2"][5] -= 0.01f;

        Assert.False(SendBackGeometry.SameContent(returned, baseline));
    }

    [Fact]
    public void HigherUvUsesTheSameFlatToleranceAsUv0_EvenWhenTiled()
    {
        var baseline = Payload(UvMesh());
        var inside = Payload(UvMesh());
        var outside = Payload(UvMesh());
        baseline.Channels["TexCoord1"][0] = 50f;
        inside.Channels["TexCoord1"][0] = 50f + 0.00009f;
        outside.Channels["TexCoord1"][0] = 50f + 0.00012f;

        Assert.True(SendBackGeometry.SameContent(inside, baseline));
        Assert.False(SendBackGeometry.SameContent(outside, baseline));
    }

    [Fact]
    public void MissingTransportedHigherSet_IsChanged()
    {
        var baseline = Payload(UvMesh());
        var returned = Payload(UvMesh());
        returned.Channels.Remove("TexCoord1");
        returned.Dims.Remove("TexCoord1");

        Assert.False(SendBackGeometry.SameContent(returned, baseline));
    }

    [Fact]
    public void BlenderCreatedHigherSetOutsideBaselinePrefix_IsIgnoredByClassifier()
    {
        var baselineMesh = UvMesh();
        baselineMesh.Channels.Remove("TexCoord1");
        baselineMesh.Channels.Remove("TexCoord2");
        baselineMesh.Dims.Remove("TexCoord1");
        baselineMesh.Dims.Remove("TexCoord2");

        Assert.True(SendBackGeometry.SameContent(Payload(UvMesh()), Payload(baselineMesh)));
    }

    [Fact]
    public void GapInBaselineDoesNotCompactALaterSetIntoTheComparison()
    {
        var baselineMesh = UvMesh();
        baselineMesh.Channels.Remove("TexCoord1");
        baselineMesh.Dims.Remove("TexCoord1");
        var returnedMesh = UvMesh();
        returnedMesh.Channels["TexCoord2"][0] += 4f;

        Assert.True(SendBackGeometry.SameContent(Payload(returnedMesh), Payload(baselineMesh)));
    }

    /// <summary>A Blender round trip turns normals on zero-area faces by tens of degrees (their custom
    /// normals have no base to anchor to) while touching nothing else, so those corners' normals are
    /// outside the comparison — but ONLY those corners' normals: the same face's positions still compare,
    /// and a real face's normals still compare.</summary>
    [Fact]
    public void NormalsOnAZeroAreaFace_AreOutsideTheComparison()
    {
        var baseline = Payload(StrayMesh());
        var mangledStray = Payload(StrayMesh());
        // all three corners of the collapsed face (verts 3..5) turned 90 degrees
        for (int v = 3; v < 6; v++)
        {
            mangledStray.Channels["Normal"][v * 3] = 1f;
            mangledStray.Channels["Normal"][v * 3 + 2] = 0f;
        }
        Assert.True(SendBackGeometry.SameContent(mangledStray, baseline));

        var mangledReal = Payload(StrayMesh());
        mangledReal.Channels["Normal"][0] = 1f;      // a REAL face's corner turned the same way
        mangledReal.Channels["Normal"][2] = 0f;
        Assert.False(SendBackGeometry.SameContent(mangledReal, baseline));

        var movedStray = Payload(StrayMesh());
        movedStray.Channels["Vertex"][3 * 3] += 0.5f;   // moving a collapsed face is still an edit
        Assert.False(SendBackGeometry.SameContent(movedStray, baseline));
    }

    /// <summary>One real triangle plus a fully collapsed one — the corpus's stray-face shape (typically
    /// a handful of zero-area faces among thousands of real ones).</summary>
    private static UnityMesh StrayMesh() => new()
    {
        Name = "part",
        VertexCount = 6,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = new float[] { 0, 0, 0,  1, 0, 0,  0, 1, 0,  4, 4, 4,  4, 4, 4,  4, 4, 4 },
            ["Normal"] = new float[] { 0, 0, 1,  0, 0, 1,  0, 0, 1,  0, 1, 0,  0, 1, 0,  0, 1, 0 },
            ["TexCoord0"] = new float[] { 0, 0,  1, 0,  0, 1,  0.5f, 0.5f,  0.6f, 0.5f,  0.5f, 0.6f },
        },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["Normal"] = 3, ["TexCoord0"] = 2 },
        Submeshes = new List<int[]> { new[] { 0, 1, 2, 3, 4, 5 } },
    };

    private static MeshApply.Payload Payload(UnityMesh mesh) => MeshApply.Payload.Geometry(mesh);

    private static UnityMesh UvMesh() => new()
    {
        Name = "part",
        VertexCount = 3,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = new float[] { 0, 0, 0,  1, 0, 0,  0, 1, 0 },
            ["Normal"] = new float[] { 0, 0, 1,  0, 0, 1,  0, 0, 1 },
            ["TexCoord0"] = new float[] { 0, 0,  1, 0,  0, 1 },
            ["TexCoord1"] = new float[] { 2, 3,  4, 3,  2, 5 },
            ["TexCoord2"] = new float[] { 8, 9,  10, 9,  8, 11 },
        },
        Dims = new Dictionary<string, int>
        {
            ["Vertex"] = 3, ["Normal"] = 3,
            ["TexCoord0"] = 2, ["TexCoord1"] = 2, ["TexCoord2"] = 2,
        },
        Submeshes = new List<int[]> { new[] { 0, 1, 2 } },
    };
}
