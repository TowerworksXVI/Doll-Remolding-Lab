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
/// Where the UV convention changes hands. A <see cref="UnityMesh"/> holds Unity UVs (v = 0 at the BOTTOM)
/// and a <c>.glb</c> holds glTF's (v = 0 at the TOP); the flip lives at that boundary and NOWHERE else,
/// which is what lets every image travel in one top-down orientation with no byte flip.
///
/// Two things must hold: an unedited round trip returns the UV channel bit-for-bit (the payload is compared
/// against the original), and the tangent frame stays consistent with the flipped UVs — the V flip reverses
/// the bitangent, so <c>tangent.w</c> is part of the same question.
/// </summary>
public class UvConventionTests
{
    /// <summary>The nearest half-precision value — what a game mesh's UV channel actually carries.</summary>
    private static float H(float v) => (float)(Half)v;

    /// <summary>Exactly half-representable UVs spanning the awkward cases: endpoints, values with no short
    /// binary form, the extremes just inside 0 and 1, and a tiling value outside [0, 1].</summary>
    private static readonly float[] HalfUvs =
    {
        0f, 1f,
        H(0.1f), H(0.7333f),
        (float)Half.Epsilon, H(1f - 1f / 2048f),
        2f, -0.5f,
    };

    private static UnityMesh UvQuad(float[] uvs) => new()
    {
        Name = "uvquad",
        VertexCount = 4,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = new float[] { 0, 0, 0,  1, 0, 0,  0, 1, 0,  1, 1, 0 },
            ["TexCoord0"] = uvs,
        },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["TexCoord0"] = 2 },
        Submeshes = new List<int[]> { new[] { 0, 1, 2, 2, 1, 3 } },
    };

    [Fact]
    public void Export_WritesGltfConventionUvs()
    {
        using var g = new TempGame();
        var path = g.At("uv-out.glb");
        var src = UvQuad(HalfUvs);
        MeshGltf.ExportGlb(src, path);

        var uv = ModelRoot.Load(path).LogicalMeshes.Single().Primitives.Single()
            .GetVertexAccessor("TEXCOORD_0")!.AsVector2Array();

        Assert.Equal(4, uv.Count);
        for (int i = 0; i < uv.Count; i++)
        {
            Assert.Equal(src.Channels["TexCoord0"][i * 2], uv[i].X);
            Assert.Equal(1f - src.Channels["TexCoord0"][i * 2 + 1], uv[i].Y);
        }
    }

    [Fact]
    public void ExportThenImportPayload_ReturnsHalfPrecisionUvsBitExactly()
    {
        using var g = new TempGame();
        var path = g.At("uv-roundtrip.glb");
        var src = UvQuad(HalfUvs);
        MeshGltf.ExportGlb(src, path);

        var back = MeshGltf.ImportPayload(path).Mesh;

        Assert.Equal(src.VertexCount, back.VertexCount);
        // Not "within tolerance": a half-representable v has an exact 1 − v in float32 both ways, so the
        // transport owes the payload identical BITS.
        Assert.Equal(src.Channels["TexCoord0"], back.Channels["TexCoord0"]);
    }

    /// <summary>The rigged writer is the second door onto the same boundary: it flips exactly like the plain
    /// one, or a weight-painting session hands its part back with mirrored UVs.</summary>
    [Fact]
    public void RiggedExport_CrossesTheSameBoundary()
    {
        using var g = new TempGame();
        var path = g.At("uv-rigged.glb");
        var src = UvQuad(HalfUvs);
        src.Channels["BlendIndices"] = new float[16];
        src.Channels["BlendWeight"] = new float[] { 1, 0, 0, 0,  1, 0, 0, 0,  1, 0, 0, 0,  1, 0, 0, 0 };
        src.Dims["BlendIndices"] = 4;
        src.Dims["BlendWeight"] = 4;
        var skin = new MeshSkin
        {
            BoneHashes = new uint[] { 0x1111_1111 },
            BindPoses = new[] { Matrix4x4.Identity },
        };
        MeshGltf.ExportRiggedGlb(src, skin, _ => "root", path);

        var uv = ModelRoot.Load(path).LogicalMeshes.Single().Primitives.Single()
            .GetVertexAccessor("TEXCOORD_0")!.AsVector2Array();
        for (int i = 0; i < uv.Count; i++)
            Assert.Equal(1f - src.Channels["TexCoord0"][i * 2 + 1], uv[i].Y);

        Assert.Equal(src.Channels["TexCoord0"], MeshGltf.ImportPayload(path).Mesh.Channels["TexCoord0"]);
    }

    // ---------------------------------------------------------------- the tangent frame

    /// <summary>
    /// The UV V flip and the tangent W negation are ONE decision: the consumer flips V a second time on
    /// import, deriving its bitangent in a space that runs against the exported UVs, so the stored handedness
    /// is inverted to meet it. Either operation ALONE inverts normal-mapped relief.
    ///
    /// <para>This models the consumer's re-flip and pins the two operations to each other — what a code
    /// change breaks by touching one. That BOTH must be present rather than neither is an empirical fact
    /// about the importer, measured outside this suite.</para>
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void UvFlipAndTangentW_TravelTogetherOrTheFrameInverts(float w)
    {
        using var g = new TempGame();
        var path = g.At($"uv-tangent{(w > 0 ? "p" : "n")}.glb");
        // A skewed patch, so a sign error can't hide behind symmetry. Tangent xyz is deliberately NOT the
        // exact ∂P/∂u — only the w sign is at stake.
        var src = new UnityMesh
        {
            Name = "tanpatch",
            VertexCount = 3,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = new float[] { 0, 0, 0,  2, 0.5f, 0,  0.5f, 1.5f, 0.25f },
                ["Normal"] = new float[] { 0, 0, 1,  0, 0, 1,  0, 0, 1 },
                ["Tangent"] = new float[] { 1, 0, 0, w,  1, 0, 0, w,  1, 0, 0, w },
                ["TexCoord0"] = new float[] { 0.25f, 0.125f,  0.875f, 0.25f,  0.5f, 0.75f },
            },
            Dims = new Dictionary<string, int>
            {
                ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2,
            },
            Submeshes = new List<int[]> { new[] { 0, 1, 2 } },
        };
        float unitySide = BitangentAgreement(
            Vec3(src, 0), Vec3(src, 1), Vec3(src, 2),
            Uv(src, 0), Uv(src, 1), Uv(src, 2),
            new Vector3(0, 0, 1), new Vector3(1, 0, 0), w);
        Assert.True(MathF.Abs(unitySide) > 1e-3f, "the source frame must have an unambiguous handedness");

        MeshGltf.ExportGlb(src, path);

        var prim = ModelRoot.Load(path).LogicalMeshes.Single().Primitives.Single();
        var pos = prim.GetVertexAccessor("POSITION")!.AsVector3Array();
        var uv = prim.GetVertexAccessor("TEXCOORD_0")!.AsVector2Array();
        var nrm = prim.GetVertexAccessor("NORMAL")!.AsVector3Array();
        var tan = prim.GetVertexAccessor("TANGENT")!.AsVector4Array();
        var idx = prim.GetIndices().Select(i => (int)i).ToArray();

        // what the consumer works from: it flips V again on the way in
        var seen = idx.Take(3).Select(i => new Vector2(uv[i].X, 1f - uv[i].Y)).ToArray();
        float consumerSide = BitangentAgreement(
            pos[idx[0]], pos[idx[1]], pos[idx[2]],
            seen[0], seen[1], seen[2],
            new Vector3(nrm[0].X, nrm[0].Y, nrm[0].Z),
            new Vector3(tan[0].X, tan[0].Y, tan[0].Z), tan[0].W);
        Assert.True(MathF.Sign(consumerSide) == MathF.Sign(unitySide),
            $"handedness inverted on the way to the consumer: Unity {unitySide}, consumer {consumerSide}");
    }

    [Fact]
    public void ExportThenImportPayload_ReturnsTheTangentChannelUnchanged()
    {
        using var g = new TempGame();
        var path = g.At("uv-tangent-rt.glb");
        var src = UvQuad(HalfUvs);
        src.Channels["Normal"] = new float[] { 0, 0, 1,  0, 0, 1,  0, 0, 1,  0, 0, 1 };
        src.Dims["Normal"] = 3;
        src.Channels["Tangent"] = new float[] { 1, 0, 0, -1,  1, 0, 0, -1,  1, 0, 0, 1,  1, 0, 0, 1 };
        src.Dims["Tangent"] = 4;
        MeshGltf.ExportGlb(src, path);

        var back = MeshGltf.ImportPayload(path).Mesh;

        Assert.Equal(src.Channels["Tangent"], back.Channels["Tangent"]);
    }

    /// <summary>How far the frame's bitangent agrees with the triangle's own ∂P/∂v. Only the SIGN is
    /// meaningful — which side of the surface it points to is the property the convention preserves.</summary>
    private static float BitangentAgreement(Vector3 p0, Vector3 p1, Vector3 p2,
        Vector2 t0, Vector2 t1, Vector2 t2, Vector3 n, Vector3 t, float w)
    {
        Vector3 e1 = p1 - p0, e2 = p2 - p0;
        Vector2 d1 = t1 - t0, d2 = t2 - t0;
        float det = d1.X * d2.Y - d2.X * d1.Y;
        Assert.True(MathF.Abs(det) > 1e-6f, "degenerate UV triangle");
        var dPdv = (e2 * d1.X - e1 * d2.X) / det;
        return Vector3.Dot(w * Vector3.Cross(n, t), dPdv);
    }

    private static Vector3 Vec3(UnityMesh m, int i) =>
        new(m.Channels["Vertex"][i * 3], m.Channels["Vertex"][i * 3 + 1], m.Channels["Vertex"][i * 3 + 2]);

    private static Vector2 Uv(UnityMesh m, int i) =>
        new(m.Channels["TexCoord0"][i * 2], m.Channels["TexCoord0"][i * 2 + 1]);
}
