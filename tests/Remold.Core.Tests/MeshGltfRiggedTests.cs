using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.Core.Mesh;
using SharpGLTF.Schema2;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The rigged glb export, all checked on a RELOADED glb so the properties survive serialization:
/// (1) rest pose is undeformed — every joint's <c>world · inverseBind ≈ I</c>;
/// (2) the armature is parented by bone path and bones are hash-named (remap-import needs both);
/// (3) the X reflection is carried onto the rig, so it lands inside the un-mirrored mesh;
/// (4) JOINTS_0 is a spec-valid integer accessor and BlendIndices map straight through.
/// </summary>
public class MeshGltfRiggedTests
{
    // A parent chain at known Unity rest-world positions, identity rotation — so the matrices carry only
    // translation and are easy to read back.
    private const uint HRoot = 0x1111_1111, HHip = 0x2222_2222, HHead = 0x3333_3333, HArm = 0x4444_4444;
    private static readonly Dictionary<uint, string> Paths = new()
    {
        [HRoot] = "root",
        [HHip] = "root/Hip_M",
        [HHead] = "root/Hip_M/Head_M",
        [HArm] = "root/Arm_M",
    };
    private static readonly Dictionary<uint, Vector3> RestUnity = new()
    {
        [HRoot] = new(0, 0, 0),
        [HHip] = new(0, 0.9f, 0),
        [HHead] = new(0.10f, 1.60f, 0),   // head is high +Y and off-centre +X
        [HArm] = new(0.30f, 1.20f, 0),
    };

    private static MeshSkin BuildSkin(params uint[] order)
    {
        // bindPose = inverse(restWorld); restWorld here is a pure translation, so bindPose = translate(-t).
        var binds = order.Select(h => Matrix4x4.CreateTranslation(-RestUnity[h])).ToList();
        return new MeshSkin { BoneHashes = order, BindPoses = binds };
    }

    private static UnityMesh OneTriangle(uint[] boneOrder, string name = "test_part")
    {
        // 3 verts, each fully weighted to one bone (slot = vertex index, clamped to the bones present)
        var bi = new float[3 * 4];
        var bw = new float[3 * 4];
        for (int v = 0; v < 3; v++) { bi[v * 4] = Math.Min(v, boneOrder.Length - 1); bw[v * 4] = 1f; }
        return new UnityMesh
        {
            Name = name,
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

    private static ModelRoot ExportAndReload(uint[] order, Func<uint, string?> resolve)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gf2_rig_{Guid.NewGuid():N}.glb");
        try
        {
            MeshGltf.ExportRiggedGlb(OneTriangle(order), BuildSkin(order), resolve, path);
            return ModelRoot.Load(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RestPose_EveryJoint_WorldTimesInverseBind_IsIdentity()
    {
        var order = new[] { HRoot, HHip, HHead };
        var model = ExportAndReload(order, h => Paths[h]);
        var skin = model.LogicalSkins.Single();

        for (int i = 0; i < skin.JointsCount; i++)
        {
            var (joint, ibm) = skin.GetJoint(i);
            var rest = joint.WorldMatrix * ibm;             // skin matrix at rest = identity ⇒ undeformed
            AssertClose(Matrix4x4.Identity, rest);
        }
    }

    [Fact]
    public void Armature_IsParentedByPath_AndHashNamed()
    {
        var order = new[] { HRoot, HHip, HHead };
        var model = ExportAndReload(order, h => Paths[h]);

        var head = FindNode(model, "Head_M_33333333");
        var hip = FindNode(model, "Hip_M_22222222");
        var root = FindNode(model, "root_11111111");
        Assert.Equal(hip, head.VisualParent);              // root/Hip_M/Head_M chain preserved
        Assert.Equal(root, hip.VisualParent);
    }

    [Fact]
    public void Reflection_PutsBoneAtXNegatedRestPosition()
    {
        var order = new[] { HRoot, HHip, HHead };
        var model = ExportAndReload(order, h => Paths[h]);

        var head = FindNode(model, "Head_M_33333333");
        var t = head.WorldMatrix.Translation;
        // Unity rest = (0.10, 1.60, 0); Blender-facing rig negates X: (-0.10, 1.60, 0).
        AssertClose(new Vector3(-0.10f, 1.60f, 0f), t);
        Assert.True(t.Y > 1.0f, "head should sit high on +Y");
    }

    [Fact]
    public void UnresolvedBone_FallsBackToFlatHashNamedNode()
    {
        var order = new[] { HRoot, HHead };
        // Resolver knows root but not the head hash → a flat bone_<hash> node, disconnected. It must still
        // export (single armature ancestor) and sit at its bind-pose position, so it stays hash-paintable.
        var model = ExportAndReload(order, h => h == HRoot ? "root" : null);

        var fallback = FindNode(model, $"bone_{HHead:x8}");
        Assert.Equal("armature", fallback.VisualParent.Name);               // wrapped, not under "root"
        AssertClose(new Vector3(-0.10f, 1.60f, 0f), fallback.WorldMatrix.Translation);
    }

    [Fact]
    public void Joints0_IsIntegerEncoded_AndIndicesPassThroughInBoneOrder()
    {
        var order = new[] { HRoot, HHip, HHead };
        var model = ExportAndReload(order, h => Paths[h]);
        var prim = model.LogicalMeshes.Single().Primitives.First();

        var joints = prim.GetVertexAccessor("JOINTS_0");
        Assert.Equal(EncodingType.UNSIGNED_SHORT, joints.Encoding);          // spec-valid integer, not FLOAT
        Assert.False(joints.Normalized);

        // vertex v was weighted to slot v; skin.joints is in bone order, so the index is just v.
        var j = joints.AsVector4Array();
        Assert.Equal(0, (int)j[0].X);
        Assert.Equal(1, (int)j[1].X);
        Assert.Equal(2, (int)j[2].X);
    }

    // ---- combined (multi-mesh, union skeleton) -------------------------------------------------

    // Two parts SHARING root + head in DIFFERENT local bone orders, each bringing a distinct bone — so the
    // union must dedup and the JOINTS remap is non-trivial (a pass-through puts B's indices on wrong bones).
    private static ModelRoot ExportCombinedAndReload()
    {
        var partA = new MeshGltf.RiggedPart(OneTriangle(new[] { HRoot, HHip, HHead }, "partA"), BuildSkin(HRoot, HHip, HHead));
        var partB = new MeshGltf.RiggedPart(OneTriangle(new[] { HHead, HRoot, HArm }, "partB"), BuildSkin(HHead, HRoot, HArm));
        var path = Path.Combine(Path.GetTempPath(), $"gf2_rigc_{Guid.NewGuid():N}.glb");
        try
        {
            MeshGltf.ExportCombinedRiggedGlb(new[] { partA, partB }, h => Paths[h], path);
            return ModelRoot.Load(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Combined_UnionsSharedBones_IntoOneSkinSharedByBothMeshes()
    {
        var model = ExportCombinedAndReload();
        var skin = Assert.Single(model.LogicalSkins);
        Assert.Equal(4, skin.JointsCount);                       // root, Hip, Head, Arm — shared bones deduped (not 6)
        Assert.Equal(2, model.LogicalMeshes.Count);
        // both mesh nodes bind the SAME skin
        var meshNodes = model.LogicalNodes.Where(n => n.Mesh is not null).ToList();
        Assert.Equal(2, meshNodes.Count);
        Assert.All(meshNodes, n => Assert.Same(skin, n.Skin));
    }

    [Fact]
    public void Combined_RemapsEachPartsJointsIntoUnionOrder()
    {
        var model = ExportCombinedAndReload();
        var skin = model.LogicalSkins.Single();
        int Union(string leaf) => Enumerable.Range(0, skin.JointsCount)
            .Single(i => skin.GetJoint(i).Joint.Name.StartsWith(leaf + "_", StringComparison.Ordinal));

        // Part B's verts sit on local slots 0,1,2 = Head, root, Arm; after remap each points at that bone's
        // UNION index (a no-op remap would read 0,1,2 = root, Hip, Head).
        var jB = MeshNamed(model, "partB").Primitives.First().GetVertexAccessor("JOINTS_0").AsVector4Array();
        Assert.Equal(Union("Head_M"), (int)jB[0].X);
        Assert.Equal(Union("root"), (int)jB[1].X);
        Assert.Equal(Union("Arm_M"), (int)jB[2].X);
        // part A's order already matches: slots 0,1,2 = root, Hip, Head
        var jA = MeshNamed(model, "partA").Primitives.First().GetVertexAccessor("JOINTS_0").AsVector4Array();
        Assert.Equal(Union("root"), (int)jA[0].X);
        Assert.Equal(Union("Hip_M"), (int)jA[1].X);
        Assert.Equal(Union("Head_M"), (int)jA[2].X);
    }

    [Fact]
    public void Combined_RestPose_AllUnionJoints_Undeformed()
    {
        var model = ExportCombinedAndReload();
        var skin = model.LogicalSkins.Single();
        for (int i = 0; i < skin.JointsCount; i++)
        {
            var (joint, ibm) = skin.GetJoint(i);
            AssertClose(Matrix4x4.Identity, joint.WorldMatrix * ibm);
        }
    }

    [Fact]
    public void Combined_ImportGlbByName_ExtractsJustThatPart()
    {
        // The re-split splits the one glb by object name, so ImportGlb(path, name) returns ONLY that part's
        // geometry (3 verts), not both pools (6).
        var partA = new MeshGltf.RiggedPart(OneTriangle(new[] { HRoot, HHip, HHead }, "partA"), BuildSkin(HRoot, HHip, HHead));
        var partB = new MeshGltf.RiggedPart(OneTriangle(new[] { HHead, HRoot, HArm }, "partB"), BuildSkin(HHead, HRoot, HArm));
        var path = Path.Combine(Path.GetTempPath(), $"gf2_rigs_{Guid.NewGuid():N}.glb");
        try
        {
            MeshGltf.ExportCombinedRiggedGlb(new[] { partA, partB }, h => Paths[h], path);
            var a = MeshGltf.ImportGlb(path, "partA");
            var b = MeshGltf.ImportGlb(path, "partB");
            Assert.Equal(3, a.VertexCount);
            Assert.Equal(3, b.VertexCount);
            // round-trips through the axis convention back to the original Unity-space triangle
            Assert.Equal(new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 }, a.Channels["Vertex"]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Combined_ReexportPartGlb_PreservesTheSkinThroughTheResplit()
    {
        // The rewrite must carry the skin: the packager reads the workspace glb's JOINTS/WEIGHTS, so a
        // geometry-only re-split loses every weight Blender sent and the compile refuses the part. It keeps
        // the UNION armature, so a weight painted onto ANY outfit bone reaches the compile.
        var partA = new MeshGltf.RiggedPart(OneTriangle(new[] { HRoot, HHip, HHead }, "partA"), BuildSkin(HRoot, HHip, HHead));
        var partB = new MeshGltf.RiggedPart(OneTriangle(new[] { HHead, HRoot, HArm }, "partB"), BuildSkin(HHead, HRoot, HArm));
        var combined = Path.Combine(Path.GetTempPath(), $"gf2_rigr_{Guid.NewGuid():N}.glb");
        var resplit = Path.Combine(Path.GetTempPath(), $"gf2_rigr_{Guid.NewGuid():N}_b.glb");
        try
        {
            MeshGltf.ExportCombinedRiggedGlb(new[] { partA, partB }, h => Paths[h], combined);
            var returned = MeshGltf.ReexportPartGlb(combined, "partB", resplit);
            Assert.True(returned.HasSkin);                       // the caller's tolerance chain gets the real payload

            // each vertex still resolves to the SAME bone by hash, at the same weight
            var p = MeshGltf.ImportPayload(resplit);
            Assert.True(p.HasSkin);
            Assert.Equal(3, p.VertexCount);
            uint HashOfVertex(int v)
            {
                for (int k = 0; k < 4; k++)
                    if (p.JointWeights![v * 4 + k] > 0.99f) return p.SkinJointHashes![p.JointIndices![v * 4 + k]];
                throw new InvalidOperationException($"vertex {v} lost its full-weight influence");
            }
            Assert.Equal(HHead, HashOfVertex(0));
            Assert.Equal(HRoot, HashOfVertex(1));
            Assert.Equal(HArm, HashOfVertex(2));
            // geometry round-trips to the original Unity-space triangle, like the geometry-only path
            Assert.Equal(new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 }, p.Mesh.Channels["Vertex"]);

            // still rest-pose exact, so re-opening in Blender shows an undeformed mesh on a posed rig
            var model = ModelRoot.Load(resplit);
            var skin = model.LogicalSkins.Single();
            Assert.Equal(4, skin.JointsCount);                   // the union rig rode along
            for (int i = 0; i < skin.JointsCount; i++)
            {
                var (joint, ibm) = skin.GetJoint(i);
                AssertClose(Matrix4x4.Identity, joint.WorldMatrix * ibm);
            }
        }
        finally
        {
            if (File.Exists(combined)) File.Delete(combined);
            if (File.Exists(resplit)) File.Delete(resplit);
        }
    }

    private static SharpGLTF.Schema2.Mesh MeshNamed(ModelRoot m, string name) =>
        m.LogicalMeshes.Single(x => x.Name == name);

    private static Node FindNode(ModelRoot m, string name) =>
        m.LogicalNodes.Single(n => n.Name == name);

    private static void AssertClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        foreach (var (e, a) in Elems(expected).Zip(Elems(actual)))
            Assert.True(Math.Abs(e - a) < 1e-4f, $"matrix element {e} vs {a}");
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"{expected} vs {actual}");
    }

    private static IEnumerable<float> Elems(Matrix4x4 m)
    {
        yield return m.M11; yield return m.M12; yield return m.M13; yield return m.M14;
        yield return m.M21; yield return m.M22; yield return m.M23; yield return m.M24;
        yield return m.M31; yield return m.M32; yield return m.M33; yield return m.M34;
        yield return m.M41; yield return m.M42; yield return m.M43; yield return m.M44;
    }
}
