using System.Collections.Generic;
using System.Numerics;
using Remold.Core.Mesh;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The in-memory channel accessors on <c>UnityMesh</c> (Has / AsVector2 / AsVector3 / AsVector4).
/// The byte-level <c>Decode</c> path needs a real AssetsTools type-tree field, so it lives in the
/// opt-in <c>LiveRoundtripTests</c>; here we build channel arrays by hand.
/// </summary>
public class UnityMeshTests
{
    private static UnityMesh TwoVertMesh() => new()
    {
        Name = "synthetic",
        VertexCount = 2,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = new float[] { 1, 2, 3, 4, 5, 6 },
            ["TexCoord0"] = new float[] { 0.1f, 0.2f, 0.3f, 0.4f },
            ["Tangent"] = new float[] { 1, 0, 0, 1, 0, 1, 0, 1 },
        },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["TexCoord0"] = 2, ["Tangent"] = 4 },
    };

    [Fact]
    public void Has_ReflectsPresentChannels()
    {
        var mesh = TwoVertMesh();
        Assert.True(mesh.Has("Vertex"));
        Assert.False(mesh.Has("Color"));
    }

    [Fact]
    public void AsVector3_ReinterpretsThreeFloatsPerVertex()
    {
        var v = TwoVertMesh().AsVector3("Vertex");
        Assert.Equal(2, v.Count);
        Assert.Equal(new Vector3(1, 2, 3), v[0]);
        Assert.Equal(new Vector3(4, 5, 6), v[1]);
    }

    [Fact]
    public void AsVector2_ReinterpretsTwoFloatsPerVertex()
    {
        var uv = TwoVertMesh().AsVector2("TexCoord0");
        Assert.Equal(new Vector2(0.1f, 0.2f), uv[0]);
        Assert.Equal(new Vector2(0.3f, 0.4f), uv[1]);
    }

    [Fact]
    public void AsVector4_ReinterpretsFourFloatsPerVertex()
    {
        var t = TwoVertMesh().AsVector4("Tangent");
        Assert.Equal(new Vector4(1, 0, 0, 1), t[0]);
        Assert.Equal(new Vector4(0, 1, 0, 1), t[1]);
    }
}
