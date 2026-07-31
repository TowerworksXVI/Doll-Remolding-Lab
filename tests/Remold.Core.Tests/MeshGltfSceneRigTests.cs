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
/// The rigged export with a scene rig: scenePaths name and parent the armature where the corpus BoneTable
/// can't, and the uprighting bakes the geometry while the bones pose at <c>inverse(bindPose)·G</c> — so a
/// body that ships lying down stands upright in Blender WITH its rig, undeformed and bit-exact out.
/// </summary>
public class MeshGltfSceneRigTests
{
    private static readonly Matrix4x4 G = new(   // the measured −90°-about-X uprighting, snapped
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    private const uint HPelvis = 0xAAAA_0001, HSpine = 0xAAAA_0002;
    private static readonly string[] ScenePaths = { "Bip001/Bip001_Pelvis", "Bip001/Bip001_Pelvis/Bip001_Spine" };

    /// <summary>Two bones whose SCENE rest worlds are pure translations, on a rig that stands a lying-down
    /// mesh up: bindPose = G · inverse(sceneWorld), so rest world = inverse(bindPose)·G = sceneWorld.</summary>
    private static MeshSkin LyingSkin()
    {
        var restScene = new[] { Matrix4x4.CreateTranslation(0, 0.9f, 0), Matrix4x4.CreateTranslation(0, 1.2f, 0) };
        var binds = restScene.Select(w => { Matrix4x4.Invert(w, out var wi); return G * wi; }).ToList();
        return new MeshSkin { BoneHashes = new[] { HPelvis, HSpine }, BindPoses = binds };
    }

    private static UnityMesh LyingTriangle() => new()
    {
        Name = "part",
        VertexCount = 3,
        Channels = new()
        {
            // authored Z-up: the "height" axis of this data is +Z (it would render face-down raw)
            ["Vertex"] = new[] { 0f, 0, 0, 0.2f, 0, 1.5f, 0, 0, 1.5f },
            ["BlendIndices"] = new float[] { 0, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
            ["BlendWeight"] = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
        },
        Dims = new() { ["Vertex"] = 3, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };

    private static ModelRoot ExportAndReload(out string keptPath)
    {
        keptPath = Path.Combine(Path.GetTempPath(), $"gf2_scene_{Guid.NewGuid():N}.glb");
        MeshGltf.ExportRiggedGlb(LyingTriangle(), LyingSkin(), _ => null /* BoneTable knows nothing */,
            keptPath, scenePaths: ScenePaths, uprighting: G);
        return ModelRoot.Load(keptPath);
    }

    [Fact]
    public void ScenePaths_NameAndParentTheArmature_WhereTheTableResolvesNothing()
    {
        var model = ExportAndReload(out var path);
        try
        {
            // no bone_<hash> soup: the real Bip001 chain, hash-suffixed leaves for remap recovery
            var pelvis = model.LogicalNodes.Single(n => n.Name == $"Bip001_Pelvis_{HPelvis:x8}");
            var spine = model.LogicalNodes.Single(n => n.Name == $"Bip001_Spine_{HSpine:x8}");
            var bip = model.LogicalNodes.Single(n => n.Name == "Bip001");
            Assert.Equal(pelvis, spine.VisualParent);
            Assert.Equal(bip, pelvis.VisualParent);
            Assert.DoesNotContain(model.LogicalNodes, n => n.Name.StartsWith("bone_", StringComparison.Ordinal));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Uprighting_BakesGeometryAndPosesBones_UprightTogether()
    {
        var model = ExportAndReload(out var path);
        try
        {
            // (0.2, 0, 1.5) ·G = (0.2, 1.5, 0), X-flipped to (−0.2, 1.5, 0): the height now lives on +Y
            var pos = model.LogicalMeshes.Single().Primitives.First()
                .GetVertexAccessor("POSITION").AsVector3Array();
            Assert.True(Math.Abs(pos[1].Y - 1.5f) < 1e-5f && Math.Abs(pos[1].Z) < 1e-5f, $"expected upright, got {pos[1]}");

            // bones: posed at inverse(bindPose)·G = the scene rest (pure translations up +Y), X-flipped
            var pelvis = model.LogicalNodes.Single(n => n.Name.StartsWith("Bip001_Pelvis_", StringComparison.Ordinal));
            Assert.True(Vector3.Distance(new(0, 0.9f, 0), pelvis.WorldMatrix.Translation) < 1e-4f);

            // and the rest pose is undeformed: world · inverseBind = identity for every joint
            var skin = model.LogicalSkins.Single();
            for (int i = 0; i < skin.JointsCount; i++)
            {
                var (joint, ibm) = skin.GetJoint(i);
                var d = joint.WorldMatrix * ibm - Matrix4x4.Identity;
                float max = new[]
                {
                    d.M11, d.M12, d.M13, d.M14, d.M21, d.M22, d.M23, d.M24,
                    d.M31, d.M32, d.M33, d.M34, d.M41, d.M42, d.M43, d.M44,
                }.Max(Math.Abs);
                Assert.True(max < 1e-4f, $"joint {i} deformed at rest (max dev {max})");
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConnectorRests_PlaceUnskinnedAncestors_InsteadOfParkingAtOrigin()
    {
        // "Bip001" is a path PREFIX, not a skinned bone: with no supplied rest it lands at the origin.
        var rest = new Dictionary<string, Matrix4x4> { ["Bip001"] = Matrix4x4.CreateTranslation(0, 0, 0.1f) };
        var path = Path.Combine(Path.GetTempPath(), $"gf2_conn_{Guid.NewGuid():N}.glb");
        try
        {
            MeshGltf.ExportRiggedGlb(LyingTriangle(), LyingSkin(), _ => null, path,
                scenePaths: ScenePaths, uprighting: G, connectorRests: rest);
            var model = ModelRoot.Load(path);
            var bip = model.LogicalNodes.Single(n => n.Name == "Bip001");
            // bind-space (0,0,0.1) · G (−90°X) = (0,0.1,0); X-flip leaves it unchanged
            Assert.True(Vector3.Distance(new(0, 0.1f, 0), bip.WorldMatrix.Translation) < 1e-5f,
                $"connector at {bip.WorldMatrix.Translation}");
            // and the joints still rest undeformed under it
            var skin = model.LogicalSkins.Single();
            var (joint, ibm) = skin.GetJoint(0);
            Assert.True((joint.WorldMatrix * ibm).Translation.Length() < 1e-4f);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void BakedGlb_ImportsAndUnbakes_BitExact()
    {
        var original = LyingTriangle();
        ExportAndReload(out var path);
        try
        {
            var imported = MeshGltf.ImportGlb(path);                 // Unity-space but still G-baked
            var unbaked = RestBake.Unapply(imported, G);
            Assert.Equal(original.Channels["Vertex"], unbaked.Channels["Vertex"]);   // exact float[] equality
        }
        finally { File.Delete(path); }
    }
}
