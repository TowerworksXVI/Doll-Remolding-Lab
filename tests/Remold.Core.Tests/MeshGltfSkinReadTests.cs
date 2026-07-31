using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.Core.Mesh;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The package-time importer reads the skin (JOINTS_0/WEIGHTS_0 + each joint's bone hash) back, so skinned
/// meshes pre-encode instead of being refused. The recovered payload matches the authored skin AND drives
/// <see cref="MeshApply.BuildSkinned"/> to the target bone order; a geometry-only export imports skinless.
/// </summary>
public class MeshGltfSkinReadTests
{
    private const uint HA = 0xA1A1A1A1, HB = 0xB2B2B2B2, HC = 0xC3C3C3C3;
    private static readonly Dictionary<uint, string> Paths = new()
    {
        [HA] = "root", [HB] = "root/b", [HC] = "root/b/c",
    };

    // 3 verts, vertex v fully weighted to bone-order slot v; distinct positions so any NN is unambiguous.
    private static UnityMesh OneTriangle()
    {
        var bi = new float[3 * 4];
        var bw = new float[3 * 4];
        for (int v = 0; v < 3; v++) { bi[v * 4] = v; bw[v * 4] = 1f; }
        return new UnityMesh
        {
            Name = "skin_part",
            VertexCount = 3,
            Channels = new()
            {
                ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 },
                ["BlendIndices"] = bi,
                ["BlendWeight"] = bw,
            },
            Dims = new() { ["Vertex"] = 3, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
            Submeshes = new() { new[] { 0, 1, 2 } },
        };
    }

    // Identity bind poses are enough here — the reader only needs joint names + per-vertex JOINTS/WEIGHTS.
    private static MeshSkin BuildSkin(params uint[] order) =>
        new() { BoneHashes = order, BindPoses = order.Select(_ => Matrix4x4.Identity).ToList() };

    private static MeshApply.Payload ExportRiggedThenImport(uint[] order)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gf2_skinread_{Guid.NewGuid():N}.glb");
        try
        {
            MeshGltf.ExportRiggedGlb(OneTriangle(), BuildSkin(order), h => Paths[h], path);
            return MeshGltf.ImportPayload(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ImportPayload_RecoversJointHashesIndicesAndWeights()
    {
        var order = new[] { HA, HB, HC };
        var p = ExportRiggedThenImport(order);

        Assert.True(p.HasSkin);
        Assert.Equal(order, p.SkinJointHashes);                    // joint order = bone order, hashes off node names
        for (int v = 0; v < 3; v++)
        {
            Assert.Equal(v, p.JointIndices![v * 4 + 0]);           // vertex v → slot v
            Assert.True(Math.Abs(p.JointWeights![v * 4 + 0] - 1f) < 1e-4f);   // full weight on slot 0
        }
    }

    [Fact]
    public void ImportPayload_OnGeometryOnlyGlb_HasNoSkin()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gf2_geom_{Guid.NewGuid():N}.glb");
        try
        {
            MeshGltf.ExportGlb(OneTriangle(), path);               // geometry-only export (no armature)
            var p = MeshGltf.ImportPayload(path);
            Assert.False(p.HasSkin);
            Assert.Null(p.JointIndices);
            Assert.Null(p.JointWeights);
            Assert.Null(p.SkinJointHashes);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RecoveredSkin_DrivesBuildSkinned_ToTargetBoneOrder()
    {
        var payload = ExportRiggedThenImport(new[] { HA, HB, HC });

        // The target lists the same bones REVERSED, so a correct remap translates each glb joint hash to
        // the target index; a pass-through lands on the wrong bone.
        var targetHashes = new[] { HC, HB, HA };
        var orig = new UnityMesh
        {
            Name = "target",
            VertexCount = 3,
            Channels = new()
            {
                ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 },
                ["BlendIndices"] = new float[3 * 4],
                ["BlendWeight"] = new float[3 * 4],
            },
            Dims = new() { ["Vertex"] = 3, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
            Submeshes = new() { new[] { 0, 1, 2 } },
        };

        var built = MeshApply.BuildSkinned(orig, payload, targetHashes);   // would throw without a skin

        var bi = built.Arrays["BlendIndices"];
        Assert.Equal(2f, bi[0 * 4 + 0]);   // vertex 0 → bone HA → target index 2
        Assert.Equal(1f, bi[1 * 4 + 0]);   // vertex 1 → bone HB → target index 1
        Assert.Equal(0f, bi[2 * 4 + 0]);   // vertex 2 → bone HC → target index 0
        Assert.True(Math.Abs(built.Arrays["BlendWeight"][0] - 1f) < 1e-4f);
    }

    [Fact]
    public void A_meshless_glb_is_refused_with_a_plain_message()
    {
        // an armature-only export — the common Blender mistake of selecting a parent empty
        var path = Path.Combine(Path.GetTempPath(), $"gf2_nomesh_{Guid.NewGuid():N}.glb");
        var model = SharpGLTF.Schema2.ModelRoot.CreateModel();
        model.UseScene(0).CreateNode("just_a_transform");
        model.SaveGLB(path);
        try
        {
            var e = Assert.Throws<InvalidOperationException>(() => MeshGltf.ImportPayload(path, lenient: true));
            Assert.Contains("no mesh data", e.Message);
        }
        finally { File.Delete(path); }
    }
}
