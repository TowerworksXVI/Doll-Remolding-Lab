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
/// The three ways a Blender-facing export used to carry data Blender's mesh/armature model cannot
/// represent — each silently normalized on import and read back as a spurious edit:
/// (1) a REFLECTED rest world (the game mirrors a left-hand weapon mount from the right; Blender bones
///     hold only translation+rotation, so everything under the mount imported point-reflected through it);
/// (2) DUPLICATE faces over one vertex set (a cloth region re-listed in the index buffer for density;
///     Blender's importer deletes the copies);
/// (3) authored normals on an all-zero-area (collapsed billboard) mesh, which cannot survive at all —
///     the mesh-edit gate refuses such a part's Blender opens on this detection (see PartSkinGate).
/// </summary>
public class BlenderRoundTripSafetyTests
{
    private const uint HRoot = 0x1111_1111, HHand = 0x2222_2222, HWeapon = 0x3333_3333;
    private static readonly Dictionary<uint, string> Paths = new()
    {
        [HRoot] = "root",
        [HHand] = "root/Hand",
        [HWeapon] = "root/Hand/mount/weapon",
    };
    private static readonly Dictionary<uint, Vector3> RestUnity = new()
    {
        [HRoot] = new(0, 0, 0),
        [HHand] = new(0.3f, 0.9f, 0),
        [HWeapon] = new(0, 0, 0),          // the weapon binds at its own origin, like the game's
    };

    // The mirrored mount: an orthonormal linear part with determinant −1 (a pure point reflection here),
    // resting away from the hand — the exact shape the game ships for a left-hand weapon point.
    private static readonly Matrix4x4 MirroredMountRest = new(
        -1, 0, 0, 0,
        0, -1, 0, 0,
        0, 0, -1, 0,
        1.0f, 0.5f, 0.2f, 1);

    private static MeshSkin BuildSkin(params uint[] order) => new()
    {
        BoneHashes = order,
        BindPoses = order.Select(h => Matrix4x4.CreateTranslation(-RestUnity[h])).ToList(),
    };

    private static UnityMesh OneTriangle(uint[] boneOrder, string name = "test_part")
    {
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

    // ---- (1) reflected rest worlds ---------------------------------------------------------------

    [Fact]
    public void MirroredConnectorRest_ExportsProper_WithEveryJointWorldPreserved()
    {
        var order = new[] { HRoot, HHand, HWeapon };
        var path = Path.Combine(Path.GetTempPath(), $"gf2_mirror_{Guid.NewGuid():N}.glb");
        try
        {
            MeshGltf.ExportRiggedGlb(OneTriangle(order), BuildSkin(order), h => Paths[h], path,
                connectorRests: new Dictionary<string, Matrix4x4> { ["root/Hand/mount"] = MirroredMountRest });
            var model = ModelRoot.Load(path);

            // no node — connector or joint — carries a reflection Blender would silently drop
            foreach (var node in model.LogicalNodes)
            {
                Assert.True(MeshGltf.LinearDeterminant(node.LocalMatrix) > 0,
                    $"{node.Name}: local carries a reflection");
                Assert.True(MeshGltf.LinearDeterminant(node.WorldMatrix) > 0,
                    $"{node.Name}: world carries a reflection");
            }

            // the mount keeps its POSITION (Unity (1.0, 0.5, 0.2) reflects to glTF (−1.0, 0.5, 0.2))…
            var mount = model.LogicalNodes.Single(n => n.Name == "mount");
            AssertClose(new Vector3(-1.0f, 0.5f, 0.2f), mount.WorldMatrix.Translation);
            // …and the weapon joint under it still rests at its own origin, not point-reflected to 2×mount
            var weapon = model.LogicalNodes.Single(n => n.Name.StartsWith("weapon_", StringComparison.Ordinal));
            AssertClose(Vector3.Zero, weapon.WorldMatrix.Translation);

            // rest pose stays undeformed for every joint
            var skin = model.LogicalSkins.Single();
            for (int i = 0; i < skin.JointsCount; i++)
            {
                var (joint, ibm) = skin.GetJoint(i);
                AssertClose(Matrix4x4.Identity, joint.WorldMatrix * ibm);
            }
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ProperRests_AreLeftUntouched()
    {
        var order = new[] { HRoot, HHand, HWeapon };
        var path = Path.Combine(Path.GetTempPath(), $"gf2_proper_{Guid.NewGuid():N}.glb");
        try
        {
            MeshGltf.ExportRiggedGlb(OneTriangle(order), BuildSkin(order), h => Paths[h], path);
            var model = ModelRoot.Load(path);
            var weapon = model.LogicalNodes.Single(n => n.Name.StartsWith("weapon_", StringComparison.Ordinal));
            AssertClose(Vector3.Zero, weapon.WorldMatrix.Translation);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---- (2) duplicate faces ---------------------------------------------------------------------

    private static UnityMesh DuplicateFaceMesh()
    {
        // 6 verts, 4 faces: (0,1,2) then its re-listed copy, a winding-flipped copy, and a face naming a
        // vertex twice — every shape Blender's importer would delete. (3,4,5) is the untouched control.
        return new UnityMesh
        {
            Name = "cloth_part",
            VertexCount = 6,
            Channels = new()
            {
                ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0, 2, 0, 0, 3, 0, 0, 2, 1, 0 },
                ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
            },
            Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2 },
            Submeshes = new() { new[] { 0, 1, 2, 0, 1, 2, 0, 2, 1, 3, 4, 5 }, new[] { 1, 1, 2 } },
        };
    }

    [Fact]
    public void SplitDuplicateFaces_GivesEveryFaceAUniqueVertexSet_WithCornersUntouched()
    {
        var mesh = DuplicateFaceMesh();
        var split = MeshGltf.SplitDuplicateFaces(mesh);

        // every corner still reads the same values, face for face
        for (int s = 0; s < mesh.Submeshes.Count; s++)
            for (int k = 0; k < mesh.Submeshes[s].Length; k++)
            {
                int src = mesh.Submeshes[s][k], dst = split.Submeshes[s][k];
                for (int c = 0; c < 3; c++)
                    Assert.Equal(mesh.Channels["Vertex"][src * 3 + c], split.Channels["Vertex"][dst * 3 + c]);
                for (int c = 0; c < 2; c++)
                    Assert.Equal(mesh.Channels["TexCoord0"][src * 2 + c],
                        split.Channels["TexCoord0"][dst * 2 + c]);
            }

        // no two faces share a vertex set, and no face names a vertex twice — across submeshes
        var sets = new HashSet<(int, int, int)>();
        foreach (var tri in split.Submeshes)
            for (int f = 0; f + 2 < tri.Length; f += 3)
            {
                var sorted = new[] { tri[f], tri[f + 1], tri[f + 2] }.OrderBy(i => i).ToArray();
                Assert.True(sorted[0] != sorted[1] && sorted[1] != sorted[2],
                    "a face still names a vertex twice");
                Assert.True(sets.Add((sorted[0], sorted[1], sorted[2])), "two faces still share a vertex set");
            }

        // idempotent over its own output
        Assert.Same(split, MeshGltf.SplitDuplicateFaces(split));

        // an already-unique mesh passes through by reference
        var clean = OneTriangle(new[] { HRoot });
        Assert.Same(clean, MeshGltf.SplitDuplicateFaces(clean));
    }

    [Fact]
    public void ExportGlb_ShipsDuplicateFacesOnSplitVertices_AndTheImportReadsThemBack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gf2_dup_{Guid.NewGuid():N}.glb");
        try
        {
            MeshGltf.ExportGlb(DuplicateFaceMesh(), path);
            var back = MeshGltf.ImportGlb(path);
            // all 5 faces survive (15 corners over two submeshes), none collapsed
            Assert.Equal(12, back.Submeshes[0].Length);
            Assert.Equal(3, back.Submeshes[1].Length);
            // the re-listed copy still draws the same corners as the original face
            for (int k = 0; k < 3; k++)
                for (int c = 0; c < 3; c++)
                    Assert.Equal(back.Channels["Vertex"][back.Submeshes[0][k] * 3 + c],
                        back.Channels["Vertex"][back.Submeshes[0][3 + k] * 3 + c]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---- (3) all-zero-area (billboard) meshes ----------------------------------------------------

    private static UnityMesh CollapsedBillboardMesh()
    {
        // two "quads", every corner of each collapsed onto one point — the pearl shape
        return new UnityMesh
        {
            Name = "pearl_part",
            VertexCount = 6,
            Channels = new()
            {
                ["Vertex"] = new[] { 1f, 2, 3, 1, 2, 3, 1, 2, 3, 4, 5, 6, 4, 5, 6, 4, 5, 6 },
                ["Normal"] = new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1 },
            },
            Dims = new() { ["Vertex"] = 3, ["Normal"] = 3 },
            Submeshes = new() { new[] { 0, 1, 2, 3, 4, 5 } },
        };
    }

    [Fact]
    public void AllFacesZeroArea_TellsBillboardsFromRealGeometry()
    {
        Assert.True(MeshGltf.AllFacesZeroArea(CollapsedBillboardMesh()));
        Assert.False(MeshGltf.AllFacesZeroArea(OneTriangle(new[] { HRoot })));

        // one real face among the collapsed ones keeps the mesh editable
        var mixed = CollapsedBillboardMesh();
        mixed.Channels["Vertex"][3] = 9f;   // vert1 off the point on X
        mixed.Channels["Vertex"][7] = 7f;   // vert2 off the point on Y — a genuine area
        Assert.False(MeshGltf.AllFacesZeroArea(mixed));

        // no faces at all is not a billboard
        var empty = CollapsedBillboardMesh();
        empty.Submeshes.Clear();
        Assert.False(MeshGltf.AllFacesZeroArea(empty));
    }

    private static void AssertClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        Span<float> e = stackalloc float[16];
        Span<float> a = stackalloc float[16];
        Write(expected, e);
        Write(actual, a);
        for (int i = 0; i < 16; i++)
            Assert.True(Math.Abs(e[i] - a[i]) < 1e-4f, $"matrix element {e[i]} vs {a[i]}");

        static void Write(Matrix4x4 m, Span<float> into)
        {
            into[0] = m.M11; into[1] = m.M12; into[2] = m.M13; into[3] = m.M14;
            into[4] = m.M21; into[5] = m.M22; into[6] = m.M23; into[7] = m.M24;
            into[8] = m.M31; into[9] = m.M32; into[10] = m.M33; into[11] = m.M34;
            into[12] = m.M41; into[13] = m.M42; into[14] = m.M43; into[15] = m.M44;
        }
    }

    private static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"{expected} vs {actual}");
}
