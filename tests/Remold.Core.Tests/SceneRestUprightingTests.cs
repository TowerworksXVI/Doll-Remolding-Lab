using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.App.ViewModels;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Model;
using Remold.Core.Skeleton;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// A prefab body that ships lying down stands up in Blender by its scene rig's own uprighting, whether or
/// not a project ever recorded one; the file records the space it is in, the build restates the donor by
/// the same rule, and an edit sent back before its part opened upright is stood up on its way into a
/// session.
/// </summary>
public class SceneRestUprightingTests
{
    private static readonly float[] Tri = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] Idx = { 0, 1, 2 };
    private const string Logical = "llllllllllllllllllllllllllllllll1.bundle";
    private const string Phys = "55555555555555555555555555555555";
    private const string Mesh = "prop_lod0";
    private static readonly Outfit TheOutfit = new(0, "VesnaSSR01", OutfitKind.Base);

    /// <summary>A self-rigged part whose root rests a quarter turn about X from bind space — the shape a
    /// lying-down body ships in. Returns the vfs and the rig the export reads.</summary>
    private static (GameVfs Vfs, SceneRig Rig, UnityMesh Raw) LyingDownPart(TempGame g)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        uint hash = BoneTable.Hash("root/spine");
        SyntheticBundle.BuildSelfRiggedMesh(Path.Combine(abw, Phys + ".bundle"), Mesh, Tri, Idx,
            new[] { hash },
            new[]
            {
                new SyntheticBundle.RigNode("root", -1, 0f, 0f, 0f)
                    { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2) },
                new SyntheticBundle.RigNode("spine", 0, 0f, 0f, 0f),
            },
            skinBones: new[] { 1 }, bundleName: Logical);
        var vfs = TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null, (Logical, Phys));
        var dec = vfs.TryDeobfuscateLogical(Logical)!;
        var field = new BundleReader().GetMeshField(dec, Mesh)!;
        var rig = SceneRig.TryRead(dec, Mesh, MeshSkin.Decode(field))!;
        Assert.NotNull(rig.Uprighting);   // the fixture really does ship lying down
        return (vfs, rig, UnityMesh.Decode(field, Mesh));
    }

    private static List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)> Spec(
        string glbOut, IReadOnlyList<float>? bakedRest) => new()
    {
        ("prop", Logical, Mesh, glbOut, bakedRest, 0L, null),
    };

    [Fact]
    public void ALyingDownPart_OpensUpright_ByItsOwnSceneRig_WhenNothingIsRecorded()
    {
        using var g = new TempGame();
        var (vfs, rig, raw) = LyingDownPart(g);
        var G = rig.Uprighting!.Value;

        var lone = g.At(Path.Combine("meshes", "prop.glb"));
        var done = AssetExporter.BuildRiggedGlbs(g.Root, vfs, TheOutfit, "Vesna", Spec(lone, null), g.At("textures"));

        Assert.Contains("prop", done);
        // the geometry stands up by the scene rig's own snapped rotation, and the record says so
        Assert.Equal(RestBake.Apply(raw, G).Channels["Vertex"], MeshGltf.ImportGlb(lone).Channels["Vertex"]);
        Assert.Equal(RestBake.ToList(G), PreviewMaps.ReadBakedRest(lone));
    }

    [Fact]
    public void ARecordedRest_StillWins_OverTheSceneRig()
    {
        // A converted 0.3.x project states its part's space itself; an identity record is "nothing baked",
        // and the file then stays in bind space even though the rig would stand it up.
        using var g = new TempGame();
        var (vfs, _, raw) = LyingDownPart(g);

        var lone = g.At(Path.Combine("meshes", "prop.glb"));
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, TheOutfit, "Vesna",
            Spec(lone, RestBake.ToList(Matrix4x4.Identity)), g.At("textures"));

        Assert.Equal(raw.Channels["Vertex"], MeshGltf.ImportGlb(lone).Channels["Vertex"]);
        Assert.Null(PreviewMaps.ReadBakedRest(lone));
    }

    [Fact]
    public void TheEffectiveRest_IsTheRecord_ElseTheSceneRig()
    {
        var G = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var other = new Matrix4x4(0, 0, 1, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 0, 1);

        Assert.Equal(G, RestBake.Effective(null, G, out bool refused));
        Assert.False(refused);
        Assert.Equal(other, RestBake.Effective(RestBake.ToList(other), G, out refused));
        Assert.False(refused);
        Assert.Null(RestBake.Effective(RestBake.ToList(Matrix4x4.Identity), G, out refused));
        Assert.False(refused);
        var sheared = new List<float> { 1, 0, 0, 0, 0.3f, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        Assert.Null(RestBake.Effective(sheared, G, out refused));
        Assert.True(refused);
    }

    [Fact]
    public void TheRecord_CarriesTheBake_ThroughThePortableCopy()
    {
        using var g = new TempGame();
        var glb = g.At("a.glb");
        File.WriteAllBytes(glb, new byte[] { 1, 2, 3 });
        var rest = RestBake.ToList(new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1));

        // a bake with no maps is still a record
        PreviewMaps.WriteSidecar(glb, Array.Empty<PreviewMaps.Entry>(), Array.Empty<PreviewMaps.SubmeshSource>(),
            bakedRest: rest);
        Assert.Equal(rest, PreviewMaps.ReadBakedRest(glb));

        var copy = g.At(Path.Combine("copy", "a.glb"));
        PreviewMaps.CopyPortableWorkspace(glb, copy);
        Assert.Equal(rest, PreviewMaps.ReadBakedRest(copy));

        // nothing to record clears the sidecar, and a file with none reads as bind space
        var bare = g.At("b.glb");
        File.WriteAllBytes(bare, new byte[] { 1 });
        PreviewMaps.WriteSidecar(bare, Array.Empty<PreviewMaps.Entry>(), Array.Empty<PreviewMaps.SubmeshSource>());
        Assert.Null(PreviewMaps.ReadBakedRest(bare));
    }

    [Fact]
    public void TheBuild_RestatesTheDonor_InTheUnionsSpace()
    {
        var G = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var mesh = new UnityMesh
        {
            Name = "donor", VertexCount = 1,
            Channels = new Dictionary<string, float[]> { ["Vertex"] = new float[] { 1, 2, 3 } },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
            Submeshes = new List<int[]> { new[] { 0 } },
        };
        var payload = MeshApply.Payload.Geometry(mesh);
        float[] Of(MeshApply.Payload p) => p.Mesh.Channels["Vertex"];
        float[] up = RestBake.Apply(mesh, G).Channels["Vertex"];
        float[] down = RestBake.Unapply(mesh, G).Channels["Vertex"];

        // a scene-space union: a baked file is already there, a bind-space one takes the target's rest
        Assert.Equal(up, Of(ModBuilder.PayloadInUnionSpace(payload, sceneUnion: true, fileRest: null, targetRest: G)));
        Assert.Same(payload, ModBuilder.PayloadInUnionSpace(payload, sceneUnion: true, fileRest: G, targetRest: G));
        Assert.Same(payload, ModBuilder.PayloadInUnionSpace(payload, sceneUnion: true, fileRest: null, targetRest: null));
        // an anchor-space union: a baked file takes its rest back off, a bind-space one is left alone
        Assert.Equal(down, Of(ModBuilder.PayloadInUnionSpace(payload, sceneUnion: false, fileRest: G, targetRest: G)));
        Assert.Same(payload, ModBuilder.PayloadInUnionSpace(payload, sceneUnion: false, fileRest: null, targetRest: G));
    }

    [Fact]
    public void TheSubjectSkeleton_JudgesAgreement_InSceneSpace()
    {
        // A body that ships lying down and a hair that ships upright bind the head in two bind spaces and
        // one scene place: composed with each part's own uprighting they agree, and the head is one bone.
        var G = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var scene = Matrix4x4.CreateTranslation(0.1f, 1.2f, -0.02f);
        Assert.True(Matrix4x4.Invert(G, out var gInv));
        var lyingRest = scene * gInv;                        // rest · G == scene
        const uint head = 0x1234u;
        MeshSkin Skin(Matrix4x4 rest)
        {
            Assert.True(Matrix4x4.Invert(rest, out var bind));
            return new MeshSkin { BoneHashes = new[] { head }, BindPoses = new[] { bind } };
        }
        var parts = new List<(MeshSkin Skin, IReadOnlyList<string>? BonePaths, Matrix4x4? Uprighting)>
        {
            (Skin(lyingRest), new[] { "root/head" }, G),
            (Skin(scene), new[] { "root/head" }, null),
        };
        var bones = AssetExporter.SubjectSkeleton(parts, _ => null, out var disagreeing);
        Assert.Empty(disagreeing);
        var bone = Assert.Single(bones);
        Assert.Equal(lyingRest, bone.BindRest);   // the first owner's rest, in its own bind space
        Assert.Equal(G, bone.Uprighting);

        // …and a part that really places the bone elsewhere in the scene still loses it
        parts[1] = (Skin(scene * Matrix4x4.CreateTranslation(0, 0.05f, 0)), new[] { "root/head" }, null);
        bones = AssetExporter.SubjectSkeleton(parts, _ => null, out disagreeing);
        Assert.Equal(new[] { "root/head" }, disagreeing);
        Assert.Empty(bones);
    }

    [Fact]
    public void ABindSpaceEdit_StandsUp_OnItsWayIntoTheSession()
    {
        // An edit sent back under a release whose open never stood the part up sits in bind space with no
        // record. Prepared for a session that IS upright, it stands up with its armature, and the prepared
        // file records the space so the send-back marks its asset with it.
        using var g = new TempGame();
        var (vfs, rig, _) = LyingDownPart(g);
        var G = rig.Uprighting!.Value;
        var rigged = g.At(Path.Combine("run", "prop.rigged.glb"));
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, TheOutfit, "Vesna", Spec(rigged, null), g.At("textures"));
        // the same part built with "nothing baked" recorded: bind-space geometry and armature, as 0.4.0 wrote
        var bindSpaceEdit = g.At(Path.Combine("edit", "prop.glb"));
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, TheOutfit, "Vesna",
            Spec(bindSpaceEdit, RestBake.ToList(Matrix4x4.Identity)), g.At("textures"));
        Assert.Null(PreviewMaps.ReadBakedRest(bindSpaceEdit));

        var prepared = g.At(Path.Combine("run", "prop.glb"));
        Assert.True(MainWindowViewModel.PrepareSessionPartGlb(rigged, bindSpaceEdit, Mesh, prepared, null,
            editedBakedRest: null));

        Assert.Equal(MeshGltf.ImportGlb(rigged).Channels["Vertex"], MeshGltf.ImportGlb(prepared).Channels["Vertex"]);
        Assert.Equal(RestBake.ToList(G), PreviewMaps.ReadBakedRest(prepared));
        string joint = $"spine_{BoneTable.Hash("root/spine"):x8}";
        var expected = JointWorld(rigged, joint);
        var actual = JointWorld(prepared, joint);
        Assert.True(RestBake.RotationDiff(expected, actual) < 1e-5f && RestBake.TranslationDiff(expected, actual) < 1e-5f,
            $"the armature stood up with the mesh: expected {expected}, got {actual}");

        // an edit whose asset records its bake is already in the session's space and is left alone
        var marked = g.At(Path.Combine("run", "prop.marked.glb"));
        Assert.True(MainWindowViewModel.PrepareSessionPartGlb(rigged, rigged, Mesh, marked, null,
            editedBakedRest: RestBake.ToList(G)));
        Assert.Equal(MeshGltf.ImportGlb(rigged).Channels["Vertex"], MeshGltf.ImportGlb(marked).Channels["Vertex"]);
        Assert.Equal(RestBake.ToList(G), PreviewMaps.ReadBakedRest(marked));

        static Matrix4x4 JointWorld(string glb, string node) => SharpGLTF.Schema2.ModelRoot.Load(glb)
            .LogicalNodes.Single(n => n.Name == node).WorldMatrix;
    }
}
