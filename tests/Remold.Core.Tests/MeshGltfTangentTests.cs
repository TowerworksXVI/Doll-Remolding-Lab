using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Remold.Core.Mesh;
using Remold.Core.Tests.Support;
using SharpGLTF.Schema2;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Tangent conditioning at glb write time. Some game meshes ship TANGENT data glTF has no room for — a zero
/// or non-finite direction, a handedness that is not ±1 — and the writer refuses the whole file over a
/// single such vertex, which is a part that cannot materialize at all. The export fixes those vertices and
/// leaves every legal one exactly as it arrived.
/// </summary>
public class MeshGltfTangentTests
{
    /// <summary>Four verts facing +Z, one tangent per case: legal, zero direction, NaN direction, and a
    /// legal direction carrying a handedness of 0.5.</summary>
    private static UnityMesh MixedTangents(bool withNormals = true)
    {
        var mesh = new UnityMesh
        {
            Name = "tangent_patch",
            VertexCount = 4,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = new float[] { 0, 0, 0,  1, 0, 0,  0, 1, 0,  1, 1, 0 },
                ["Tangent"] = new[]
                {
                    1f, 0, 0, 1,
                    0f, 0, 0, 1,
                    float.NaN, float.NaN, float.NaN, 1,
                    0f, 2, 0, 0.5f,
                },
                ["TexCoord0"] = new float[] { 0, 0,  1, 0,  0, 1,  1, 1 },
            },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2 },
            Submeshes = new List<int[]> { new[] { 0, 1, 2, 2, 1, 3 } },
        };
        if (withNormals)
        {
            mesh.Channels["Normal"] = new float[] { 0, 0, 1,  0, 0, 1,  0, 0, 1,  0, 0, 1 };
            mesh.Dims["Normal"] = 3;
        }
        return mesh;
    }

    private static IReadOnlyList<Vector4> ExportedTangents(string path)
    {
        var prim = ModelRoot.Load(path).LogicalMeshes.Single().Primitives.First();
        return prim.GetVertexAccessor("TANGENT").AsVector4Array().ToArray();
    }

    private static void AssertLegal(Vector4 t)
    {
        var xyz = new Vector3(t.X, t.Y, t.Z);
        Assert.True(float.IsFinite(xyz.Length()), $"{t} is not finite");
        Assert.Equal(1f, xyz.Length(), 4);
        Assert.True(t.W == 1f || t.W == -1f, $"handedness {t.W} is not ±1");
    }

    [Fact]
    public void Export_TangentsGltfWouldReject_Succeeds_AndEveryVertexShipsLegal()
    {
        using var g = new TempGame();
        var path = g.At("tangents-mixed.glb");

        MeshGltf.ExportGlb(MixedTangents(), path);   // the unsanitized write throws "Invalid Tangent" here

        var tan = ExportedTangents(path);
        Assert.Equal(4, tan.Count);
        foreach (var t in tan) AssertLegal(t);
    }

    [Fact]
    public void Export_ALegalTangent_ShipsExactlyAsTheAxisConversionLeftIt()
    {
        using var g = new TempGame();
        var path = g.At("tangents-passthrough.glb");

        MeshGltf.ExportGlb(MixedTangents(), path);

        // vertex 0's (1,0,0,1) is already unit with ±1 handedness, so nothing but the Unity→glTF flip
        // touches it
        Assert.Equal(AxisConvention.Tangent(new Vector4(1, 0, 0, 1)), ExportedTangents(path)[0]);
    }

    [Fact]
    public void Export_AnUnusableDirection_IsRebuiltPerpendicularToTheVertexNormal()
    {
        using var g = new TempGame();
        var path = g.At("tangents-rebuilt.glb");

        MeshGltf.ExportGlb(MixedTangents(), path);

        var tan = ExportedTangents(path);
        var normal = AxisConvention.Normal(new Vector3(0, 0, 1));
        foreach (int v in new[] { 1, 2 })   // the zero direction and the NaN one
        {
            AssertLegal(tan[v]);
            Assert.Equal(0f, Vector3.Dot(new Vector3(tan[v].X, tan[v].Y, tan[v].Z), normal), 4);
            Assert.Equal(1f, tan[v].W);
        }
    }

    [Fact]
    public void Export_AUsableDirectionWithABadHandedness_KeepsItsDirection()
    {
        using var g = new TempGame();
        var path = g.At("tangents-handedness.glb");

        MeshGltf.ExportGlb(MixedTangents(), path);

        // (0,2,0) normalizes to (0,1,0); 0.5 is nearer +1 than −1, and the flip carries both across
        Assert.Equal(AxisConvention.Tangent(new Vector4(0, 1, 0, 1)), ExportedTangents(path)[3]);
    }

    [Fact]
    public void Export_WithNoNormalChannel_StillShipsALegalTangent()
    {
        using var g = new TempGame();
        var path = g.At("tangents-no-normals.glb");

        MeshGltf.ExportGlb(MixedTangents(withNormals: false), path);

        foreach (var t in ExportedTangents(path)) AssertLegal(t);
    }

    /// <summary>A mesh whose tangents are all legal comes home unchanged — the conditioning is per vertex
    /// and touches none of them.</summary>
    [Fact]
    public void ExportThenImport_LegalTangents_RoundTripUnchanged()
    {
        var src = MixedTangents();
        src.Channels["Tangent"] = new[]
        {
            1f, 0, 0, 1,
            0f, 1, 0, -1,
            0f, 0, 1, 1,
            -1f, 0, 0, -1,
        };
        using var g = new TempGame();
        var path = g.At("tangents-legal.glb");

        MeshGltf.ExportGlb(src, path);

        Assert.Equal(src.Channels["Tangent"], MeshGltf.ImportGlb(path).Channels["Tangent"]);
    }

    /// <summary>Inside the pass-through window: a direction 8e-5 off unit length is one the writer takes, so
    /// the export ships it as it arrived instead of renormalizing every vertex it touches. The window is what
    /// keeps a game mesh's own float noise from being rewritten, and this is what holds it open.</summary>
    [Fact]
    public void ExportThenImport_ATangentInsideTheUnitWindow_ShipsUntouched()
    {
        var src = MixedTangents();
        src.Channels["Tangent"] = new[]
        {
            1f + 8e-5f, 0, 0, 1,
            0f, 1f + 8e-5f, 0, -1,
            0f, 0, 1f - 8e-5f, 1,
            -1f, 0, 0, -1,
        };
        using var g = new TempGame();
        var path = g.At("tangents-near-unit.glb");

        MeshGltf.ExportGlb(src, path);

        Assert.Equal(src.Channels["Tangent"], MeshGltf.ImportGlb(path).Channels["Tangent"]);
    }

    /// <summary>The rigged writer builds its own vertex accessors, so it takes the conditioning
    /// separately.</summary>
    [Fact]
    public void ExportRigged_TangentsGltfWouldReject_Succeeds_AndEveryVertexShipsLegal()
    {
        var mesh = MixedTangents();
        mesh.Channels["BlendIndices"] = new float[4 * 4];
        mesh.Channels["BlendWeight"] = new float[4 * 4];
        for (int v = 0; v < 4; v++) mesh.Channels["BlendWeight"][v * 4] = 1f;
        mesh.Dims["BlendIndices"] = 4;
        mesh.Dims["BlendWeight"] = 4;
        var skin = new MeshSkin
        {
            BoneHashes = new uint[] { 0x1111_1111 },
            BindPoses = new List<Matrix4x4> { Matrix4x4.Identity },
        };
        using var g = new TempGame();
        var path = g.At("tangents-rigged.glb");

        MeshGltf.ExportRiggedGlb(mesh, skin, _ => "root", path);

        foreach (var t in ExportedTangents(path)) AssertLegal(t);
    }
}
