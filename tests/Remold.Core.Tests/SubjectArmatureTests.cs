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
using Remold.Core.Project;
using Remold.Core.Skeleton;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using SharpGLTF.Schema2;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The armature a rigged export carries spans the SUBJECT, not the geometry it draws: a bone another part
/// of the outfit poses stands in the rig too, so weight can be painted onto it and every part of one
/// subject shares one skeleton. The properties that has to hold onto:
/// (1) those bones are skin JOINTS, appended as a zero-weighted TAIL after every joint the geometry poses —
///     joints because Blender's glTF importer makes armature bones out of the skin's joints and their node
///     ancestors alone, and a bone carried any other way imports as a loose empty nothing can be painted
///     against. The tail is what keeps the joints the geometry poses on the indices and the order they would
///     have had with no extras, so a send that touched nothing re-splits onto the same bones and
///     JOINTS_0/WEIGHTS_0 don't move a byte. The prefix-and-order pair is the invariant; the glb's BYTES are
///     not, and the connector-prefix case below shows where they part company;
/// (2) they are hash-named, so weight painted onto one comes back on a joint the compile can resolve;
/// (3) a bone the geometry itself poses keeps its own placement, and one the subject's parts disagree about
///     joins no armature at all;
/// (4) a workspace glb written before any of this still round-trips.
/// </summary>
public class SubjectArmatureTests
{
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
        [HHead] = new(0.10f, 1.60f, 0),
        [HArm] = new(0.30f, 1.20f, 0),
    };

    private static string NodeName(uint hash) =>
        Paths[hash][(Paths[hash].LastIndexOf('/') + 1)..] + $"_{hash:x8}";

    // bindPose = inverse(restWorld); the rests here are pure translations, so bindPose = translate(-t)
    private static MeshSkin Skin(params uint[] order) => new()
    {
        BoneHashes = order,
        BindPoses = order.Select(h => Matrix4x4.CreateTranslation(-RestUnity[h])).ToList(),
    };

    /// <summary>Rests carrying ROTATION, where the rest of this file uses pure translations. Recomposing a
    /// child's world through a newly-placed parent is bit-exact for translations, so only a rotated rig
    /// shows what placing a connector actually costs.</summary>
    private static readonly Dictionary<uint, Matrix4x4> RotatedRest = new()
    {
        [HRoot] = Matrix4x4.Identity,
        [HHip] = Matrix4x4.CreateFromYawPitchRoll(0.7f, 0.3f, -0.2f) * Matrix4x4.CreateTranslation(0, 0.9f, 0),
        [HHead] = Matrix4x4.CreateFromYawPitchRoll(-0.4f, 1.1f, 0.6f) * Matrix4x4.CreateTranslation(0.1f, 1.6f, 0),
    };

    private static MeshSkin RotatedSkin(params uint[] order) => new()
    {
        BoneHashes = order,
        BindPoses = order.Select(h => { Matrix4x4.Invert(RotatedRest[h], out var ib); return ib; }).ToList(),
    };

    /// <summary>A triangle whose vertex v rides bone-order slot v (clamped), so every bone the skin names is
    /// actually pulled by something.</summary>
    private static UnityMesh Part(string name, int boneCount, float yShift = 0f)
    {
        var bi = new float[3 * 4];
        var bw = new float[3 * 4];
        for (int v = 0; v < 3; v++) { bi[v * 4] = Math.Min(v, boneCount - 1); bw[v * 4] = 1f; }
        return new UnityMesh
        {
            Name = name,
            VertexCount = 3,
            Channels = new()
            {
                ["Vertex"] = new[] { 0f, yShift, 0, 1, yShift, 0, 0, yShift + 1, 0 },
                ["BlendIndices"] = bi,
                ["BlendWeight"] = bw,
            },
            Dims = new() { ["Vertex"] = 3, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
            Submeshes = new() { new[] { 0, 1, 2 } },
        };
    }

    /// <summary>The subject as the rig build reads it: a cloth part on root+hip, a hair part on root+head.
    /// Neither poses the other's leaf bone.</summary>
    private static IReadOnlyList<AssetExporter.SubjectBone> TwoPartSubject() =>
        AssetExporter.SubjectSkeleton(
            new[]
            {
                (Skin(HRoot, HHip), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                (Skin(HRoot, HHead), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
            },
            h => Paths.GetValueOrDefault(h), out _);

    private static ModelRoot ExportAndReload(string path, MeshSkin skin,
        IReadOnlyList<MeshGltf.ExtraBone>? extras)
    {
        MeshGltf.ExportRiggedGlb(Part("cloth_lod0", skin.BoneCount), skin, h => Paths.GetValueOrDefault(h),
            path, extraBones: extras);
        return ModelRoot.Load(path);
    }

    // ---------------------------------------------------------------- the armature spans the subject

    [Fact]
    public void ALonePartsArmature_CarriesAnotherPartsBone_AsANodeOnItsRealPath()
    {
        using var g = new TempGame();
        var extras = AssetExporter.ExtraBones(TwoPartSubject(), new[] { HRoot, HHip }, uprighting: null);
        Assert.Equal(new[] { HHead }, extras.Select(e => e.Hash).ToArray());

        var model = ExportAndReload(g.At("cloth_lod0.glb"), Skin(HRoot, HHip), extras);

        var head = model.LogicalNodes.Single(n => n.Name == NodeName(HHead));
        Assert.Equal(NodeName(HHip), head.VisualParent!.Name);        // parented on root/Hip_M/Head_M
        // the rig is the mesh's own space: Unity rest with X reflected
        var t = head.WorldMatrix.Translation;
        Assert.Equal(-RestUnity[HHead].X, t.X, 4);
        Assert.Equal(RestUnity[HHead].Y, t.Y, 4);
    }

    [Fact]
    public void ASubjectBoneTheGeometryDoesNotPose_JoinsTheSkinAtTheTail_AndMovesNoJointIndex()
    {
        using var g = new TempGame();
        var skin = Skin(HRoot, HHip);
        var plain = ExportAndReload(g.At("plain.glb"), skin, extras: null);
        var plainJoints = plain.LogicalSkins.Single();
        var plainNames = Enumerable.Range(0, plainJoints.JointsCount).Select(i => plainJoints.Joints[i].Name).ToArray();

        var union = ExportAndReload(g.At("union.glb"), skin,
            AssetExporter.ExtraBones(TwoPartSubject(), new[] { HRoot, HHip }, uprighting: null));

        var unionSkin = union.LogicalSkins.Single();
        var unionNames = Enumerable.Range(0, unionSkin.JointsCount).Select(i => unionSkin.Joints[i].Name).ToArray();
        // the joints the geometry poses, unmoved — then the subject's remaining bone appended after them
        Assert.Equal(plainNames, unionNames.Take(plainNames.Length).ToArray());
        Assert.Equal(new[] { NodeName(HHead) }, unionNames.Skip(plainNames.Length).ToArray());
        for (int i = 0; i < plainJoints.JointsCount; i++)
            Assert.Equal(plainJoints.GetJoint(i).InverseBindMatrix, unionSkin.GetJoint(i).InverseBindMatrix);

        // the tail joint's inverse bind comes from its node's world like every other joint's…
        var head = union.LogicalNodes.Single(n => n.Name == NodeName(HHead));
        Matrix4x4.Invert(head.WorldMatrix, out var expected);
        var tailIbm = unionSkin.GetJoint(plainNames.Length).InverseBindMatrix;
        Assert.Equal(expected, tailIbm);
        // …and that world is the one the SUBJECT gave it, not merely self-consistent: the fixture's rests
        // are pure translations and the export reflects X, so the inverse is the negated reflected rest.
        Assert.Equal(RestUnity[HHead].X, tailIbm.Translation.X, 4);
        Assert.Equal(-RestUnity[HHead].Y, tailIbm.Translation.Y, 4);

        // …and no vertex names it: the per-vertex skin is the same data down to the accessor
        var (pj, pw) = SkinAccessors(plain);
        var (uj, uw) = SkinAccessors(union);
        Assert.Equal(pj.SourceBufferView.Content.ToArray(), uj.SourceBufferView.Content.ToArray());
        Assert.Equal((pw.Encoding, pw.Dimensions, pw.Normalized, pw.Count),
                     (uw.Encoding, uw.Dimensions, uw.Normalized, uw.Count));
        Assert.Equal(pw.AsVector4Array(), uw.AsVector4Array());
    }

    /// <summary>The JOINTS_0 / WEIGHTS_0 accessors of a model's single mesh.</summary>
    private static (Accessor Joints, Accessor Weights) SkinAccessors(ModelRoot model)
    {
        var prim = model.LogicalMeshes.Single().Primitives[0];
        return (prim.GetVertexAccessor("JOINTS_0"), prim.GetVertexAccessor("WEIGHTS_0"));
    }

    /// <summary>The with-vs-without pair where byte-stability does NOT hold: the part skins root and the
    /// head, so the hip between them is a bare CONNECTOR — and the subject's hip is exactly the extra bone
    /// this part is handed. Placing that connector re-expresses its children's locals and re-derives their
    /// inverse binds from a recomposed world, and names the node after a hash it had none of before. The
    /// joint PREFIX and its order still hold, which is what the re-split keys on; the bytes do not, and
    /// nothing may be pinned to them.
    ///
    /// <para>This is also the case that decides where the tail comes from: the hip is registered in the
    /// path ordering as a prefix of the head, ahead of the head itself, so a tail read off that ordering
    /// would land the hip at joint 1 and push the head's index out from under the geometry.</para></summary>
    [Fact]
    public void AnExtraBoneOnAConnectorPrefix_KeepsTheJointPrefixAndOrder_ButNotTheBytes()
    {
        using var g = new TempGame();
        var skin = RotatedSkin(HRoot, HHead);                          // root/Hip_M is a connector, unplaced
        var plain = ExportAndReload(g.At("plain.glb"), skin, extras: null);
        var plainSkin = plain.LogicalSkins.Single();

        var skeleton = AssetExporter.SubjectSkeleton(
            new[]
            {
                (RotatedSkin(HRoot, HHip), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                (skin, (IReadOnlyList<string>?)null, (Matrix4x4?)null),
            },
            h => Paths.GetValueOrDefault(h), out _);
        var extras = AssetExporter.ExtraBones(skeleton, new[] { HRoot, HHead }, uprighting: null);
        Assert.Equal(new[] { Paths[HHip] }, extras.Select(e => e.Path).ToArray());   // the connector's own path
        var union = ExportAndReload(g.At("union.glb"), skin, extras);
        var unionSkin = union.LogicalSkins.Single();

        string[] Joints(Skin s) => Enumerable.Range(0, s.JointsCount).Select(i => s.Joints[i].Name).ToArray();
        // the invariant: the joints the geometry poses, same list and same order, then the hip appended
        Assert.Equal(Joints(plainSkin), Joints(unionSkin).Take(plainSkin.JointsCount).ToArray());
        Assert.Equal(new[] { NodeName(HHip) }, Joints(unionSkin).Skip(plainSkin.JointsCount).ToArray());
        // …while the inverse binds only AGREE, to float epsilon. The head's is re-derived from a world
        // recomposed through the newly-placed hip: measured at ~1e-7 here, and no bit-compare's business.
        for (int i = 0; i < plainSkin.JointsCount; i++)
        {
            var (a, b) = (plainSkin.GetJoint(i).InverseBindMatrix, unionSkin.GetJoint(i).InverseBindMatrix);
            Assert.True(RestBake.RotationDiff(a, b) < 1e-5f && RestBake.TranslationDiff(a, b) < 1e-5f,
                $"joint {i} inverse bind drifted past epsilon: {a} vs {b}");
        }
        // the concrete reason a byte-compare would fail: the connector is a named bone in one file, not the other
        Assert.Contains(plain.LogicalNodes, n => n.Name == "Hip_M");
        Assert.Contains(union.LogicalNodes, n => n.Name == NodeName(HHip));
        Assert.DoesNotContain(union.LogicalNodes, n => n.Name == "Hip_M");
    }

    [Fact]
    public void AnExtraBoneOnAPathTheSkinAlreadyBinds_NeverMovesThatJoint()
    {
        using var g = new TempGame();
        // the subject's answer for the hip, offered against a part that poses the hip itself
        var wrong = new[] { new MeshGltf.ExtraBone(HHip, Paths[HHip], Matrix4x4.CreateTranslation(9, 9, 9)) };

        var model = ExportAndReload(g.At("cloth_lod0.glb"), Skin(HRoot, HHip), wrong);

        var hip = model.LogicalNodes.Single(n => n.Name == NodeName(HHip));
        Assert.Equal(RestUnity[HHip].Y, hip.WorldMatrix.Translation.Y, 4);
        Assert.True(hip.WorldMatrix.Translation.Length() < 1.0f, $"hip world: {hip.WorldMatrix.Translation}");
    }

    [Fact]
    public void ACombinedSessionsArmature_CarriesASubjectBoneNoPartPoses_AtTheTailOfTheUnionSkin()
    {
        using var g = new TempGame();
        var combined = g.At("_combined.glb");
        // a session of the cloth part alone, with the subject's hair bone standing in the rig beside it
        MeshGltf.ExportCombinedRiggedGlb(
            new[]
            {
                new MeshGltf.RiggedPart(Part("cloth_lod0", 2), Skin(HRoot, HHip)),
                new MeshGltf.RiggedPart(Part("boot_lod0", 2, yShift: 3f), Skin(HRoot, HArm)),
            },
            h => Paths.GetValueOrDefault(h), combined,
            new[] { new MeshGltf.ExtraBone(HHead, Paths[HHead], Matrix4x4.CreateTranslation(RestUnity[HHead])) });

        var model = ModelRoot.Load(combined);
        var skin = model.LogicalSkins.Single();
        var joints = Enumerable.Range(0, skin.JointsCount).Select(i => skin.Joints[i].Name).ToArray();
        // the parts' own bones on the indices their remapped JOINTS_0 point at, the subject's spare last
        Assert.Equal(new[] { NodeName(HRoot), NodeName(HHip), NodeName(HArm), NodeName(HHead) }, joints);
        var head = model.LogicalNodes.Single(n => n.Name == NodeName(HHead));
        Assert.Equal(NodeName(HHip), head.VisualParent!.Name);
        // no part's vertices reach the tail joint at all — it is carried, never bound
        foreach (var mesh in model.LogicalMeshes)
        {
            var ji = mesh.Primitives[0].GetVertexAccessor("JOINTS_0").AsVector4Array();
            for (int v = 0; v < ji.Count; v++)
                for (int k = 0; k < 4; k++)
                    Assert.True(ji[v][k] != 3f, $"{mesh.Name} vertex {v} slot {k} names the tail joint");
        }
    }

    // ---------------------------------------------------------------- the paint comes back

    /// <summary>Route: PrepareChangedPart → SendBackGeometry.Unchanged against the file the launch handed
    /// out, then — because it answers "changed" — MeshGltf.ReexportPartGlb onto the part's own workspace
    /// glb, which is the pair the return takes for every part it publishes.</summary>
    [Fact]
    public void WeightPaintedOnASubjectBoneTheGeometryDidNotPose_ComesBackOnAResolvableJoint()
    {
        using var g = new TempGame();
        var ws = g.At("cloth_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth_lod0", 2), Skin(HRoot, HHip), h => Paths.GetValueOrDefault(h), ws,
            extraBones: AssetExporter.ExtraBones(TwoPartSubject(), new[] { HRoot, HHip }, uprighting: null));

        // what Blender hands back once the modder paints the head bone: the node joins the skin under the
        // very name the export gave it, carrying half of vertex 2
        var painted = Part("cloth_lod0", 2);
        painted.Channels["BlendIndices"][8] = 1f;   // vertex 2, slot 0: the hip
        painted.Channels["BlendWeight"][8] = 0.5f;
        painted.Channels["BlendIndices"][9] = 2f;   // vertex 2, slot 1: the head, joint index 2
        painted.Channels["BlendWeight"][9] = 0.5f;
        var send = g.At("_combined.send.glb");
        MeshGltf.ExportRiggedGlb(painted, Skin(HRoot, HHip, HHead), h => Paths.GetValueOrDefault(h), send);

        var returned = MeshGltf.ParsedGlb.Open(send);
        Assert.False(SendBackGeometry.Unchanged(returned, "cloth_lod0", MeshGltf.ParsedGlb.Open(ws)));
        MeshGltf.ReexportPartGlb(returned, "cloth_lod0", ws);

        var payload = MeshGltf.ImportPayload(ws, lenient: true);
        int joint = Array.IndexOf(payload.SkinJointHashes!, HHead);
        Assert.True(joint >= 0, "the head bone survived the re-split as a hash-named joint");
        Assert.Equal(joint, payload.JointIndices![2 * 4 + 1]);
        Assert.Equal(0.5f, payload.JointWeights![2 * 4 + 1], 3);
        // and the compile resolves it onto a target that has the bone, exactly as any other joint
        var jr = MeshApply.ResolveAuthoredJoints(new[] { HRoot, HHip, HHead }, payload.SkinJointHashes!,
            payload.JointIndices!, payload.JointWeights!, payload.VertexCount);
        Assert.Equal(2, jr.JointToTarget[joint]);
        Assert.Equal(0, jr.FullyUnsafeCount);
    }

    /// <summary>Route: PrepareChangedPart → SendBackGeometry.Unchanged. Answering "unchanged" is what spares
    /// the part: the return then publishes nothing for it and never reaches the re-split at all.</summary>
    [Fact]
    public void AnUntouchedSendOfAUnionArmatureGlb_ReadsAsUnchanged()
    {
        using var g = new TempGame();
        var ws = g.At("cloth_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth_lod0", 2), Skin(HRoot, HHip), h => Paths.GetValueOrDefault(h), ws,
            extraBones: AssetExporter.ExtraBones(TwoPartSubject(), new[] { HRoot, HHip }, uprighting: null));
        // the workspace side carries a zero-weight tail joint of its own, which is what the compare has to
        // read past on BOTH sides (SendBackGeometry keys on bone hash, absent-as-zero)
        Assert.Contains(HHead, MeshGltf.ImportPayload(ws, lenient: true).SkinJointHashes!);

        // Blender binds every armature bone it imported, so the send comes back with the subject's bones in
        // the skin at zero weight. Nothing was painted onto them, so nothing is there to take.
        Assert.True(SendBackGeometry.Unchanged(ZeroWeightUnionSend(g, Part("cloth_lod0", 2)), "cloth_lod0",
            MeshGltf.ParsedGlb.Open(ws)));
    }

    /// <summary>Route: PrepareChangedPart → SendBackGeometry.Unchanged, then MeshGltf.ReexportPartGlb where
    /// it answers "changed" — over a workspace glb from before the union armature existed.</summary>
    [Fact]
    public void AWorkspaceGlbWrittenBeforeTheUnionArmature_StillTakesASendCarryingIt()
    {
        using var g = new TempGame();
        var ws = g.At("cloth_lod0.glb");
        // the OLD shape: the part's own bones and nothing else
        MeshGltf.ExportRiggedGlb(Part("cloth_lod0", 2), Skin(HRoot, HHip), h => Paths.GetValueOrDefault(h), ws);

        Assert.True(SendBackGeometry.Unchanged(ZeroWeightUnionSend(g, Part("cloth_lod0", 2)), "cloth_lod0",
            MeshGltf.ParsedGlb.Open(ws)));

        // …and an edit against that same old file still reads as changed and writes through, union joints
        // and all
        var moved = Part("cloth_lod0", 2);
        moved.Channels["Vertex"][1] += 0.25f;
        var send = ZeroWeightUnionSend(g, moved);
        Assert.False(SendBackGeometry.Unchanged(send, "cloth_lod0", MeshGltf.ParsedGlb.Open(ws)));
        MeshGltf.ReexportPartGlb(send, "cloth_lod0", ws);
        Assert.Equal(0.25f, MeshGltf.ImportGlb(ws).Channels["Vertex"][1], 4);
    }

    /// <summary>A send whose skin carries the subject's bones the part does not pose, all at zero weight —
    /// the shape a Blender export of a union armature takes when nobody painted onto it.</summary>
    private static MeshGltf.ParsedGlb ZeroWeightUnionSend(TempGame g, UnityMesh mesh)
    {
        var send = g.At("_combined.send.glb");
        MeshGltf.ExportRiggedGlb(mesh, Skin(HRoot, HHip, HHead, HArm), h => Paths.GetValueOrDefault(h), send);
        return MeshGltf.ParsedGlb.Open(send);
    }

    /// <summary>The re-split keeps every hash-named joint and remaps the per-vertex indices onto its own
    /// order. A send carrying zero-weight tail joints hands it a longer list than the part poses, and the
    /// joints the geometry DOES pose have to come out of it on the same indices, in the same order.</summary>
    [Fact]
    public void ReexportPartGlb_ASendCarryingTailJoints_KeepsThePosedJointsOrder()
    {
        using var g = new TempGame();
        var ws = g.At("cloth_lod0.glb");
        MeshGltf.ReexportPartGlb(ZeroWeightUnionSend(g, Part("cloth_lod0", 2)), "cloth_lod0", ws);

        var payload = MeshGltf.ImportPayload(ws, lenient: true);
        // every joint survives, in the send's own order: the posed pair first, the tail after it
        Assert.Equal(new[] { HRoot, HHip, HHead, HArm }, payload.SkinJointHashes!);
        // and the geometry still rides the joints it rode, index for index
        for (int v = 0; v < 3; v++)
        {
            Assert.Equal(Math.Min(v, 1), payload.JointIndices![v * 4]);
            Assert.Equal(1f, payload.JointWeights![v * 4], 3);
        }
    }

    /// <summary>A workspace glb re-read for a combined session now hands back the whole subject's joint
    /// list, not just the part's own bones. The read has to take it: those joints are what a later paint
    /// lands on, and refusing would degrade the part to its game copy.</summary>
    [Fact]
    public void ReadRiggedGlb_AWorkspaceGlbCarryingTailJoints_ReadsThemAsBones()
    {
        using var g = new TempGame();
        var ws = g.At("cloth_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth_lod0", 2), Skin(HRoot, HHip), h => Paths.GetValueOrDefault(h), ws,
            extraBones: AssetExporter.ExtraBones(TwoPartSubject(), new[] { HRoot, HHip }, uprighting: null));

        var read = MeshGltf.ReadRiggedGlb(ws);

        Assert.NotNull(read);
        Assert.Equal(new[] { HRoot, HHip, HHead }, read!.Value.Skin.BoneHashes.ToArray());
        // the tail bone's bind pose places it where the subject said it stands (rests here are pure
        // translations, and the read reflects back out of glTF space)
        Matrix4x4.Invert(read.Value.Skin.BindPoses[2], out var rest);
        Assert.Equal(RestUnity[HHead].X, rest.Translation.X, 4);
        Assert.Equal(RestUnity[HHead].Y, rest.Translation.Y, 4);
        // …and no vertex of the geometry it carries names it
        var bi = read.Value.Mesh.Channels["BlendIndices"];
        var bw = read.Value.Mesh.Channels["BlendWeight"];
        for (int k = 0; k < bi.Length; k++) if (bi[k] == 2f) Assert.Equal(0f, bw[k]);

        // The naming pass an edited part goes through still answers for every one of those joints — and the
        // tail bone's name can only come from ANOTHER part's rig, since the edited part's own game skin
        // never had it. Scene names unlike the bone table's, so a hit proves which rig answered.
        const string BipRoot = "Bip001", BipHip = "Bip001/Pelvis", BipHead = "Bip001/Pelvis/Head";
        var partRigs = new[]
        {
            (Skin(HRoot, HHip), (IReadOnlyList<string>?)new[] { BipRoot, BipHip }),
            (Skin(HRoot, HHead), (IReadOnlyList<string>?)new[] { BipRoot, BipHead }),
        };
        Assert.Equal(new[] { BipRoot, BipHip, BipHead },
            AssetExporter.EditedScenePaths(read.Value.Skin, partRigs));
        // …and drop that second rig and the tail goes unnamed, so the assertion above rests on it
        Assert.Equal(new[] { BipRoot, BipHip, null },
            AssetExporter.EditedScenePaths(read.Value.Skin, partRigs.Take(1).ToArray()));
    }

    // ------------------------------------------------- an edited part's tail, back inside a combined session

    /// <summary>The union armature's placement rule is "the FIRST part to name a bone fixes its world", and
    /// an edited part is read back off its workspace glb — which since the tail carries the whole subject's
    /// bones, at whatever worlds that file baked. Handed on unreduced, the edited part names bones it does
    /// not ride, wins the first claim for them, and stands a LATER part's joints in the edit's baked space
    /// instead of their own. So the re-read skin is reduced to the bones the geometry actually rides
    /// (<see cref="MeshSkin.WeightedOnly"/>) before it joins the others.
    ///
    /// <para>Through the real build: an edited part whose workspace glb bakes a 90° uprighting, and a later
    /// part with a scene rig of its own that bakes none. The later part's joint must stand exactly where the
    /// SAME session places it with the edit taken out, and it must carry no rotation at all — the fixture's
    /// rig is translation-only, so any rotation on that joint came from the edit's bake.</para></summary>
    [Fact]
    public void ACombinedSession_AnEditedPartsTailJoints_NeverPlaceALaterPartsBone()
    {
        using var g = new TempGame();
        var (vfs, weaponHash) = TwoPartSubjectBundles(g);
        var bake = Matrix4x4.CreateRotationX(MathF.PI / 2);

        // the edit on disk: the part's own bone, plus the subject's weapon bone as a tail joint — and the
        // whole file baked into upright space, which is the space the tail's world is recorded in
        var ws = g.At(Path.Combine("meshes", "cloth1_lod0.glb"));
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", 1), Skin(HRoot), _ => null, ws, uprighting: bake,
            extraBones: new[] { new MeshGltf.ExtraBone(weaponHash, WeaponBonePath, bake) });

        var withEdit = BuildSession(g, vfs, "_edited.glb", ws);
        var withoutEdit = BuildSession(g, vfs, "_stock.glb", editedGlb: null);

        var joint = $"weapon_01_{weaponHash:x8}";
        var placed = JointWorld(withEdit, joint);
        // (i) independent of the union path: the weapon's rig is translation-only and bakes nothing, so its
        // joint stands unrotated. The tail world the edit would have imposed is a quarter turn about X.
        Assert.True(RestBake.RotationDiff(placed, Matrix4x4.Identity) < 1e-4f,
            $"the weapon's joint came back rotated: {placed}");
        Assert.True(RestBake.RotationDiff(AxisConvention.Reflect(bake), Matrix4x4.Identity) > 0.9f,
            "the fixture's bake has to be visible for this to prove anything");
        // (ii) …and the same session without the edit puts it in the same place
        Assert.Equal(JointWorld(withoutEdit, joint), placed);
        // the edit itself still came through — the reduction touches the bone list, never the geometry
        Assert.Equal(MeshGltf.ImportGlb(ws).Channels["Vertex"],
                     MeshGltf.ImportGlb(withEdit, "cloth1_lod0").Channels["Vertex"]);
    }

    /// <summary>The other half of the same rule: a bone the modder PAINTED carries weight, so the reduction
    /// keeps it and the union skin binds that part to it. Losing this would silently discard the paint the
    /// tail joints exist to collect.</summary>
    [Fact]
    public void ACombinedSession_WeightPaintedOnAFormerTailBone_StaysThatPartsOwnJoint()
    {
        using var g = new TempGame();
        var (vfs, weaponHash) = TwoPartSubjectBundles(g);

        // vertex 2 half on the part's own bone, half on the weapon bone standing in its tail at joint 1
        var painted = Part("cloth1_lod0", 1);
        painted.Channels["BlendIndices"][8] = 0f;
        painted.Channels["BlendWeight"][8] = 0.5f;
        painted.Channels["BlendIndices"][9] = 1f;
        painted.Channels["BlendWeight"][9] = 0.5f;
        var ws = g.At(Path.Combine("meshes", "cloth1_lod0.glb"));
        MeshGltf.ExportRiggedGlb(painted, Skin(HRoot), _ => null, ws,
            extraBones: new[] { new MeshGltf.ExtraBone(weaponHash, WeaponBonePath, Matrix4x4.Identity) });

        var combined = BuildSession(g, vfs, "_edited.glb", ws);

        var payload = MeshGltf.ImportPayload(combined, "cloth1_lod0");
        int joint = Array.IndexOf(payload.SkinJointHashes!, weaponHash);
        Assert.True(joint >= 0, "the painted bone reached the union skin");
        Assert.Equal(joint, payload.JointIndices![2 * 4 + 1]);
        Assert.Equal(0.5f, payload.JointWeights![2 * 4 + 1], 3);
    }

    /// <summary>A cloth part and a self-rigged weapon of one subject, plus the vfs over them. The weapon's
    /// rig is a translation-only mount, so nothing about it can rotate its joint.</summary>
    private static (GameVfs Vfs, uint WeaponHash) TwoPartSubjectBundles(TempGame g)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, ClothPhys + ".bundle"), "cloth1_lod0",
            SessionTri, SessionIdx, new[] { HRoot }, bundleName: ClothLogical);
        uint weaponHash = BoneTable.Hash(WeaponBonePath);
        SyntheticBundle.BuildSelfRiggedMesh(Path.Combine(abw, WeaponPhys + ".bundle"), "weapon_lod0",
            SessionTri, SessionIdx, new[] { weaponHash },
            new[]
            {
                new SyntheticBundle.RigNode("hips", -1, 1.5f, 0.5f, 2f),   // the prefab mount
                new SyntheticBundle.RigNode("weapon_01", 0, 0f, 0f, 0f),
            },
            skinBones: new[] { 1 }, bundleName: WeaponLogical);
        return (TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (ClothLogical, ClothPhys), (WeaponLogical, WeaponPhys)), weaponHash);
    }

    /// <summary>One combined build of that subject, the cloth part optionally taken from an edit.</summary>
    private static string BuildSession(TempGame g, GameVfs vfs, string outName, string? editedGlb)
    {
        var combined = g.At(Path.Combine("meshes", outName));
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, new Outfit(0, "VesnaSSR01", OutfitKind.Base), "Vesna",
            new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
            {
                ("cloth1", ClothLogical, "cloth1_lod0", null, null, 0L, editedGlb),
                ("weapon", WeaponLogical, "weapon_lod0", null, null, 0L, null),
            },
            g.At("textures"), combinedOut: combined);
        return combined;
    }

    private static Matrix4x4 JointWorld(string glb, string node) =>
        ModelRoot.Load(glb).LogicalNodes.Single(n => n.Name == node).WorldMatrix;

    private const string WeaponBonePath = "hips/weapon_01";
    private const string ClothLogical = "ccccccccccccccccccccccccccccccc1.bundle";
    private const string WeaponLogical = "wwwwwwwwwwwwwwwwwwwwwwwwwwwwwww1.bundle";
    private const string ClothPhys = "55555555555555555555555555555555";
    private const string WeaponPhys = "66666666666666666666666666666666";
    private static readonly float[] SessionTri = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] SessionIdx = { 0, 1, 2 };

    // ---------------------------------------------------------------- the reduction the session rests on

    /// <summary>What <see cref="MeshSkin.WeightedOnly"/> owes its one caller: the ridden bones in their own
    /// order, the per-vertex indices moved onto the shortened list, and null where there is no ridden bone
    /// to place the mesh by.</summary>
    [Fact]
    public void WeightedOnly_KeepsTheRiddenBonesInOrder_AndMovesTheIndicesOntoThem()
    {
        var mesh = Part("cloth_lod0", 3);          // vertex v rides bone v
        mesh.Channels["BlendIndices"][4] = 0f;     // …until vertex 1 moves off bone 1, leaving it unridden

        var got = MeshSkin.WeightedOnly(mesh, Skin(HRoot, HHip, HHead));

        Assert.NotNull(got);
        Assert.Equal(new[] { HRoot, HHead }, got!.Value.Skin.BoneHashes.ToArray());
        Assert.Equal(Skin(HRoot, HHip, HHead).BindPoses[2], got.Value.Skin.BindPoses[1]);   // its own bind pose
        Assert.Equal(1f, got.Value.Mesh.Channels["BlendIndices"][8]);   // bone 2 → slot 1 of the shortened list
        Assert.Equal(0f, got.Value.Mesh.Channels["BlendIndices"][0]);   // and bone 0 stays put
        Assert.Equal(mesh.Channels["BlendWeight"], got.Value.Mesh.Channels["BlendWeight"]);
        Assert.Equal(2f, mesh.Channels["BlendIndices"][8]);             // the input is left alone
    }

    [Fact]
    public void WeightedOnly_AMeshThatRidesNothing_HasNoBoneListToPlaceItBy()
    {
        var mesh = Part("cloth_lod0", 2);
        for (int k = 0; k < mesh.Channels["BlendWeight"].Length; k++) mesh.Channels["BlendWeight"][k] = 0f;

        Assert.Null(MeshSkin.WeightedOnly(mesh, Skin(HRoot, HHip)));
        Assert.Null(MeshSkin.WeightedOnly(Part("cloth_lod0", 1), new MeshSkin()));
    }

    /// <summary>The extras computation reads "posed" off the parts' skins, so an unreduced edited part —
    /// whose skin spans the subject — leaves it with nothing to add. That is what would let a stale
    /// workspace file be the only thing placing the subject's spare bones.</summary>
    [Fact]
    public void CombinedExtraBones_WithAnEditedPart_FindsTheSubjectsSpareBones_OnlyOnceItIsReduced()
    {
        using var g = new TempGame();
        var ws = g.At("cloth_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth_lod0", 2), Skin(HRoot, HHip), h => Paths.GetValueOrDefault(h), ws,
            extraBones: AssetExporter.ExtraBones(TwoPartSubject(), new[] { HRoot, HHip }, uprighting: null));
        var read = MeshGltf.ReadRiggedGlb(ws)!.Value;

        var raw = AssetExporter.CombinedExtraBones(TwoPartSubject(),
            new[] { new MeshGltf.RiggedPart(read.Mesh, read.Skin) });
        Assert.Empty(raw);                                   // the whole subject reads as posed

        var reduced = MeshSkin.WeightedOnly(read.Mesh, read.Skin)!.Value;
        var healed = AssetExporter.CombinedExtraBones(TwoPartSubject(),
            new[] { new MeshGltf.RiggedPart(reduced.Mesh, reduced.Skin) });
        Assert.Equal(new[] { HHead }, healed.Select(e => e.Hash).ToArray());
    }

    /// <summary>A bone the subject's parts bind in different places joins no armature — and a stale
    /// workspace glb that still carries it in its tail must not be the loophole that puts it back.</summary>
    [Fact]
    public void ACombinedSession_ATailBoneTheSubjectDisagreesAbout_ReachesNoUnionJoint()
    {
        using var g = new TempGame();
        // the tail the workspace file was written with, back when the subject still agreed about the hip
        var ws = g.At("cloth_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth_lod0", 1), Skin(HRoot), h => Paths.GetValueOrDefault(h), ws,
            extraBones: new[]
            {
                new MeshGltf.ExtraBone(HHip, Paths[HHip], Matrix4x4.CreateTranslation(RestUnity[HHip])),
            });
        var read = MeshGltf.ReadRiggedGlb(ws)!.Value;
        Assert.Contains(HHip, read.Skin.BoneHashes);         // the file really does still carry it

        // …and the subject has since fallen out over where the hip stands, so it is off the skeleton
        var elsewhere = new MeshSkin
        {
            BoneHashes = new[] { HHip },
            BindPoses = new[] { Matrix4x4.CreateTranslation(-RestUnity[HHip] - new Vector3(0, 0.5f, 0)) },
        };
        var skeleton = AssetExporter.SubjectSkeleton(
            new[]
            {
                (Skin(HRoot, HHip), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                (elsewhere, (IReadOnlyList<string>?)null, (Matrix4x4?)null),
            },
            h => Paths.GetValueOrDefault(h), out var disagreeing);
        Assert.Equal(new[] { Paths[HHip] }, disagreeing.ToArray());

        var reduced = MeshSkin.WeightedOnly(read.Mesh, read.Skin)!.Value;
        var parts = new[]
        {
            new MeshGltf.RiggedPart(reduced.Mesh, reduced.Skin),
            new MeshGltf.RiggedPart(Part("boot_lod0", 1, yShift: 3f), Skin(HRoot)),
        };
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(parts, h => Paths.GetValueOrDefault(h), combined,
            AssetExporter.CombinedExtraBones(skeleton, parts));

        var skin = ModelRoot.Load(combined).LogicalSkins.Single();
        Assert.DoesNotContain(NodeName(HHip),
            Enumerable.Range(0, skin.JointsCount).Select(i => skin.Joints[i].Name).ToArray());
    }

    // ---------------------------------------------------------------- an extra with nowhere to stand

    /// <summary>Extras are skin JOINTS now, and two things invert a joint's rest world without checking:
    /// the inverse-bind matrix, and every child's local. <c>Matrix4x4.Invert</c> fills its result with NaN
    /// on failure (measured; the older note calling it a zero matrix was wrong), so one extra with a
    /// degenerate rest would poison an armature rather than be missing from it. It is refused instead, by
    /// name, and everything else exports.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnExtraBoneWhoseRestWorldWillNotInvert_IsRefusedByName_AndTheRestExports(bool combined)
    {
        using var g = new TempGame();
        var said = new List<string>();
        var extras = new[]
        {
            new MeshGltf.ExtraBone(HHead, Paths[HHead], new Matrix4x4(0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)),
            new MeshGltf.ExtraBone(HArm, Paths[HArm], Matrix4x4.CreateTranslation(RestUnity[HArm])),
        };
        var path = g.At("out.glb");
        var skin = Skin(HRoot, HHip);

        if (combined)
            MeshGltf.ExportCombinedRiggedGlb(
                new[] { new MeshGltf.RiggedPart(Part("cloth_lod0", 2), skin) },
                h => Paths.GetValueOrDefault(h), path, extras, said.Add);
        else
            MeshGltf.ExportRiggedGlb(Part("cloth_lod0", 2), skin, h => Paths.GetValueOrDefault(h), path,
                extraBones: extras, log: said.Add);

        var model = ModelRoot.Load(path);
        var glSkin = model.LogicalSkins.Single();
        var joints = Enumerable.Range(0, glSkin.JointsCount).Select(i => glSkin.Joints[i].Name).ToArray();
        // the sound extra is there, the degenerate one is not, and the part's own joints are untouched
        Assert.Equal(new[] { NodeName(HRoot), NodeName(HHip), NodeName(HArm) }, joints);
        Assert.DoesNotContain(model.LogicalNodes, n => n.Name == NodeName(HHead));
        // no NaN reached the skin the modder would have opened
        for (int i = 0; i < glSkin.JointsCount; i++)
        {
            var ibm = glSkin.GetJoint(i).InverseBindMatrix;
            Assert.False(float.IsNaN(ibm.M11 + ibm.M22 + ibm.M33 + ibm.M41 + ibm.M42 + ibm.M43),
                $"joint {joints[i]} carries a NaN inverse bind");
        }
        // …and it was said out loud, naming the bone
        Assert.Single(said, l => l.Contains(Paths[HHead]) && l.Contains("no usable rest pose"));
    }

    // ---------------------------------------------------------------- the skeleton the build assembles

    [Fact]
    public void SubjectSkeleton_SpansEveryPart_AndNamesEachBoneOnce()
    {
        var skeleton = TwoPartSubject();

        Assert.Equal(new[] { HRoot, HHip, HHead }, skeleton.Select(b => b.Hash).ToArray());
        Assert.Equal(Paths[HHead], skeleton.Single(b => b.Hash == HHead).Path);
        // the rest carried is bind space — inverse(bindPose), before any bake
        Assert.Equal(RestUnity[HHead], skeleton.Single(b => b.Hash == HHead).BindRest.Translation);
    }

    [Fact]
    public void SubjectSkeleton_PrefersASceneRigsNameOverTheBoneTables()
    {
        var skeleton = AssetExporter.SubjectSkeleton(
            new[] { (Skin(HRoot, HHip), (IReadOnlyList<string>?)new[] { "Bip001", "Bip001/Bip001 Pelvis" },
                     (Matrix4x4?)null) },
            h => Paths.GetValueOrDefault(h), out _);

        Assert.Equal(new[] { "Bip001", "Bip001/Bip001 Pelvis" }, skeleton.Select(b => b.Path).ToArray());
    }

    [Fact]
    public void SubjectSkeleton_ABoneTheSubjectsPartsBindDifferently_JoinsNoArmature()
    {
        var elsewhere = new MeshSkin
        {
            BoneHashes = new[] { HRoot, HHip },
            BindPoses = new List<Matrix4x4>
            {
                Matrix4x4.CreateTranslation(-RestUnity[HRoot]),
                Matrix4x4.CreateTranslation(-RestUnity[HHip] - new Vector3(0, 0.5f, 0)),   // half a metre off
            },
        };
        var skeleton = AssetExporter.SubjectSkeleton(
            new[]
            {
                (Skin(HRoot, HHip), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                (elsewhere, (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                (Skin(HRoot, HHead), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
            },
            h => Paths.GetValueOrDefault(h), out var disagreeing);

        Assert.Equal(new[] { Paths[HHip] }, disagreeing.ToArray());        // said out loud, not dropped quietly
        Assert.Equal(new[] { HRoot, HHead }, skeleton.Select(b => b.Hash).ToArray());
        // so a part that doesn't pose the hip is never handed one
        Assert.DoesNotContain(HHip,
            AssetExporter.ExtraBones(skeleton, new[] { HRoot }, uprighting: null).Select(e => e.Hash));
    }

    /// <summary>Placement agreement is its own tolerance, not the bake's refusal threshold. A bake may shrug
    /// off a whole centimetre of translation because it DROPS it; an armature stick a centimetre out of place
    /// is just wrong. A millimetre is already a disagreement here; inverse noise still is not.</summary>
    [Fact]
    public void SubjectSkeleton_AMillimetreApart_IsADisagreement_InverseNoiseIsNot()
    {
        IReadOnlyList<string> Skeleton(float yOff, out uint[] hashes)
        {
            var moved = new MeshSkin
            {
                BoneHashes = new[] { HRoot, HHip },
                BindPoses = new List<Matrix4x4>
                {
                    Matrix4x4.CreateTranslation(-RestUnity[HRoot]),
                    Matrix4x4.CreateTranslation(-RestUnity[HHip] - new Vector3(0, yOff, 0)),
                },
            };
            var bones = AssetExporter.SubjectSkeleton(
                new[]
                {
                    (Skin(HRoot, HHip), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                    (moved, (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                },
                h => Paths.GetValueOrDefault(h), out var said);
            hashes = bones.Select(b => b.Hash).ToArray();
            return said;
        }

        var mm = Skeleton(0.001f, out var afterMm);                    // 1 mm: a visible offset
        Assert.Equal(new[] { Paths[HHip] }, mm.ToArray());              // named, so the build reports it
        Assert.Equal(new[] { HRoot }, afterMm);                         // and the hip joins no armature

        var noise = Skeleton(1e-6f, out var afterNoise);                // inverse noise: still one bone
        Assert.Empty(noise);
        Assert.Equal(new[] { HRoot, HHip }, afterNoise);
    }

    /// <summary>The drop list drives the status bar, and a systematically-offset rig disagrees about every
    /// bone it has. Three get named; the rest get counted.</summary>
    [Fact]
    public void DisagreementLines_NameThreeBones_ThenCountTheRest()
    {
        Assert.Empty(AssetExporter.DisagreementLines(Array.Empty<string>()));

        var few = AssetExporter.DisagreementLines(new[] { "root/a", "root/b", "root/c" }).ToArray();
        Assert.Equal(3, few.Length);
        Assert.All(few, l => Assert.DoesNotContain("more bones", l));
        Assert.Contains("root/c", few[2]);

        var many = AssetExporter.DisagreementLines(
            Enumerable.Range(0, 40).Select(i => $"root/b{i}").ToArray()).ToArray();
        Assert.Equal(4, many.Length);
        Assert.Contains("root/b2", many[2]);
        Assert.Contains("…and 37 more bones", many[3]);
        Assert.DoesNotContain(many, l => l.Contains('—'));   // no em-dashes in status text
    }

    [Fact]
    public void ExtraBones_ComposeTheUprightingTheExportBakes()
    {
        var g = Matrix4x4.CreateRotationX(MathF.PI / 2);
        var upright = AssetExporter.ExtraBones(TwoPartSubject(), new[] { HRoot, HHip }, g).Single();

        Assert.Equal(RestUnity[HHead].Y, upright.RestWorld.Translation.Z, 4);   // +Y swung onto +Z
        Assert.Equal(0f, upright.RestWorld.Translation.Y, 4);
        Assert.Equal(RestUnity[HHead].X, upright.RestWorld.Translation.X, 4);   // the rotation axis is untouched
    }


    // ---------------------------------------------------------- the tail offers only PAINTABLE bones
    //
    // The armature spans the subject so weight can be painted onto another part's bone — but a build only
    // ACCEPTS that paint when some pool candidate of the part actually poses the bone (PoolDerive's posed
    // gate, over PoolDerive.PoolCandidates). Offering the rest hands the modder bones every send is certain
    // to be refused at. The tail is filtered to the valid set; the part's own joints never are.

    private const string FilterClothLogical = "f1111111111111111111111111111111.bundle";
    private const string FilterHairLogical = "f2222222222222222222222222222222.bundle";
    private const string FilterClothPhys = "77777777777777777777777777777777";
    private const string FilterHairPhys = "88888888888888888888888888888888";
    private const string ClothSlot = "cloth1_lod0";
    private const string HairSlot = "hair1_lod0";
    /// <summary><see cref="HairSlot"/> as a roster row that differs from the export row's mesh name in CASE
    /// alone — which the mesh lookup at path id 0 does not match.</summary>
    private const string MiscasedHairSlot = "HAIR1_LOD0";

    /// <summary>A two-part subject for the filter: a cloth part posing root+hip and a hair part posing
    /// root+head, each in its own bundle. <paramref name="hairTablesArm"/> gives the hair the arm bone at
    /// ZERO weight — tabled, posed by nobody. <paramref name="hairUnreadable"/> takes its weights past
    /// measurement while its bone table still reads, which is the shape the build holds a part back for.
    /// <paramref name="hairSubSeed"/> is what the manifest stub SAYS the hair bundle holds — the content
    /// identity, so a fixture changes it to stand for a game update rewriting that bundle.
    /// <paramref name="hairInManifest"/> false leaves the hair in the catalog and out of the manifest,
    /// which is a bundle nothing can locate.</summary>
    private static GameVfs FilterBundles(TempGame g, bool hairTablesArm = false, bool hairUnreadable = false,
        byte hairSubSeed = 2, bool hairInManifest = true)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, FilterClothPhys + ".bundle"), ClothSlot,
            SessionTri, SessionIdx, new[] { HRoot, HHip }, bundleName: FilterClothLogical);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, FilterHairPhys + ".bundle"), HairSlot,
            SessionTri, SessionIdx, new[] { HRoot, HHead }, bundleName: FilterHairLogical,
            tabledOnlyBones: hairTablesArm ? new[] { HArm } : null, unresolvableStream: hairUnreadable);
        return TestVfs.CreateWith(g.Root, Array.Empty<(string, string)>(), null,
            new TestVfs.Bundle(FilterClothLogical, FilterClothPhys, 1, true),
            new TestVfs.Bundle(FilterHairLogical, FilterHairPhys, hairSubSeed, hairInManifest));
    }

    /// <summary>That subject's candidacy roster as the app hands it over — every part, with the flags the
    /// four candidacy rules read. The hair is the one carrying the interesting flag in each case.
    /// <paramref name="hairMesh"/> overrides the slot name the hair row addresses its mesh by.</summary>
    private static AssetExporter.SubjectRoster FilterRoster(string hairToken = "hair1",
        bool hairCastsShadows = true, VisibilityOverride hairVisibility = VisibilityOverride.None,
        string? hairMesh = null) =>
        new(new[]
        {
            new AssetExporter.RosterPart(ClothSlot, "cloth1", FilterClothLogical, 0, true,
                VisibilityOverride.None),
            new AssetExporter.RosterPart(hairMesh ?? HairSlot, hairToken, FilterHairLogical, 0,
                hairCastsShadows, hairVisibility),
        });

    /// <summary>One LONE cloth export of that subject — the hair joins as a skeleton-only row, exactly as
    /// the app's sibling rows do — and the joint hashes the written glb carries, in file order.</summary>
    private static uint[] LoneClothJoints(TempGame g, GameVfs vfs, AssetExporter.SubjectRoster? roster,
        ICollection<string>? degraded = null, ICollection<string>? unreadable = null)
    {
        Directory.CreateDirectory(g.At("meshes"));
        var glb = g.At(Path.Combine("meshes", ClothSlot + ".glb"));
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, new Outfit(0, "VesnaSSR01", OutfitKind.Base), "Vesna",
            new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
            {
                ("cloth1", FilterClothLogical, ClothSlot, glb, null, 0L, null),
                ("hair1", FilterHairLogical, HairSlot, null, null, 0L, null),
            },
            g.At("textures"), roster: roster, rosterDegraded: degraded, rosterUnreadable: unreadable);
        return MeshGltf.ReadRiggedGlb(glb)!.Value.Skin.BoneHashes.ToArray();
    }

    /// <summary>The unfiltered control for an omission test: the same subject, same export, no roster — so
    /// the bone the filtered run must leave off is proved to have been on offer in the first place. Without
    /// it a test asserting absence passes just as well against a subject that never had the bone.</summary>
    private static uint[] UnfilteredClothJoints(bool hairTablesArm = false, bool hairUnreadable = false)
    {
        using var h = new TempGame();
        return LoneClothJoints(h, FilterBundles(h, hairTablesArm, hairUnreadable), roster: null);
    }

    [Fact]
    public void ALoneTail_OffersASiblingsBone_WhenTheSiblingCanPoolForThisPart()
    {
        using var g = new TempGame();
        var joints = LoneClothJoints(g, FilterBundles(g), FilterRoster());

        Assert.Equal(new[] { HRoot, HHip }, joints.Take(2).ToArray());   // its own joints, on their own indices
        Assert.Contains(HHead, joints);                                  // the hair's bone, paintable
    }

    /// <summary>The untouched-send invariant: the filter selects among the APPENDED bones only, so the
    /// part's own joint prefix is byte-for-byte the prefix it would have had with no filter and with no
    /// tail at all.</summary>
    [Fact]
    public void TheFilter_LeavesThePartsOwnJointPrefixAlone()
    {
        using var g = new TempGame();
        // the hair is Fight-only, so its bone is filtered out of an Always part's tail entirely
        var filtered = LoneClothJoints(g, FilterBundles(g), FilterRoster(hairToken: "hair1_Fight"));
        using var h = new TempGame();
        var unfiltered = LoneClothJoints(h, FilterBundles(h), roster: null);

        Assert.Equal(new[] { HRoot, HHip }, filtered);                     // nothing but its own joints left
        Assert.Equal(filtered, unfiltered.Take(filtered.Length).ToArray());  // …and they are the same joints
        Assert.Equal(new[] { HHead }, unfiltered.Skip(2).ToArray());       // which the unfiltered tail follows
    }

    [Fact]
    public void ALoneTail_OmitsABonePosedOnlyByAPresenceExcludedPart()
    {
        Assert.Contains(HHead, UnfilteredClothJoints());   // on offer with no roster to judge it

        using var g = new TempGame();
        // on screen only in combat, while the cloth draws everywhere: it can't vouch for a bone
        var joints = LoneClothJoints(g, FilterBundles(g), FilterRoster(hairToken: "hair1_Fight"));

        Assert.DoesNotContain(HHead, joints);
    }

    [Fact]
    public void ALoneTail_OmitsABonePosedOnlyByAShadowOffPart()
    {
        Assert.Contains(HHead, UnfilteredClothJoints());

        using var g = new TempGame();
        var joints = LoneClothJoints(g, FilterBundles(g), FilterRoster(hairCastsShadows: false));

        Assert.DoesNotContain(HHead, joints);
    }

    [Fact]
    public void ALoneTail_OmitsABonePosedOnlyByAPartTheGameCanWithhold()
    {
        Assert.Contains(HHead, UnfilteredClothJoints());

        using var g = new TempGame();
        var joints = LoneClothJoints(g, FilterBundles(g),
            FilterRoster(hairVisibility: VisibilityOverride.CoatList));

        Assert.DoesNotContain(HHead, joints);
    }

    /// <summary>Weighted, never tabled: a bone every candidate merely carries at zero weight is exactly what
    /// the build's posed gate refuses, so it never reaches a tail either — even though the subject's
    /// skeleton has it, read off that part's bone table.</summary>
    [Fact]
    public void ALoneTail_OmitsABoneTheCandidatesOnlyTable()
    {
        using var g = new TempGame();
        var vfs = FilterBundles(g, hairTablesArm: true);

        Assert.Contains(HArm, LoneClothJoints(g, vfs, roster: null));   // the skeleton really does carry it
        using var h = new TempGame();
        var joints = LoneClothJoints(h, FilterBundles(h, hairTablesArm: true), FilterRoster());

        Assert.Contains(HHead, joints);        // the hair POSES this one
        Assert.DoesNotContain(HArm, joints);   // …and only tables this one
    }

    /// <summary>A part whose weights can't be measured is left out of the candidacy roster altogether, as
    /// the build holds it back from pool derivation: offering it with its bone TABLE standing in for a posed
    /// set would put back the very bones the posed gate exists to refuse.</summary>
    [Fact]
    public void ALoneTail_OmitsABonePosedOnlyByAPartWhoseWeightsCannotBeRead()
    {
        // the same unmeasurable hair, with no roster: its bone is still on the skeleton and still offered
        Assert.Contains(HHead, UnfilteredClothJoints(hairUnreadable: true));

        using var g = new TempGame();
        var joints = LoneClothJoints(g, FilterBundles(g, hairUnreadable: true), FilterRoster());

        Assert.Equal(new[] { HRoot, HHip }, joints);
    }

    // ------------------------------------------------------- the same tail, measured as few times as it can be
    //
    // Nothing below may change WHAT a run offers — only how much reading and summing it took to get there.
    // Two savings, and one counter each: the export loop already fetched the mesh field for every part it
    // reads, so the candidacy pass reuses it instead of fetching it again (MeshReads); and the mesh-derived
    // half of candidacy is fixed by the bundle's bytes, so it is memoized by content across runs
    // (WeightScans). Every test here asserts the joints alongside the counter — a fast wrong tail is a
    // regression, not an optimization.

    /// <summary>The same lone cloth export as <see cref="LoneClothJoints"/>, on a candidacy cache the test
    /// owns so it can read the counters back. <paramref name="includeHair"/> false drops the hair from the
    /// EXPORT rows while leaving it in the roster — the shape where the candidacy pass has a row the export
    /// loop never read, and has to fetch a mesh field of its own.</summary>
    private static uint[] LoneClothJoints(TempGame g, GameVfs vfs, AssetExporter.SubjectRoster? roster,
        CandidacyCache cache, ICollection<string>? degraded = null, bool includeHair = true)
    {
        Directory.CreateDirectory(g.At("meshes"));
        var glb = g.At(Path.Combine("meshes", ClothSlot + ".glb"));
        var spec = new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
        {
            ("cloth1", FilterClothLogical, ClothSlot, glb, null, 0L, null),
        };
        if (includeHair) spec.Add(("hair1", FilterHairLogical, HairSlot, null, null, 0L, null));
        AssetExporter.BuildRiggedGlbsCore(g.Root, vfs, new Outfit(0, "VesnaSSR01", OutfitKind.Base), "Vesna",
            spec, g.At("textures"), null, null, null, roster, degraded, cache, default);
        return MeshGltf.ReadRiggedGlb(glb)!.Value.Skin.BoneHashes.ToArray();
    }

    /// <summary>OPT 1: a roster row the export loop already read costs the candidacy pass NO read of its
    /// own. Both parts of this subject are export rows — the cloth for its glb, the hair for its share of
    /// the skeleton — so the pass fetches nothing, and the tail is the one the filter wrote before.</summary>
    [Fact]
    public void TheCandidacyPass_ReadsNoMeshTheExportLoopAlreadyRead()
    {
        using var g = new TempGame();
        var cache = new CandidacyCache(null);   // no memo: the reuse alone has to account for the reads
        var joints = LoneClothJoints(g, FilterBundles(g), FilterRoster(), cache);

        Assert.Equal(0, cache.MeshReads);
        Assert.Equal(2, cache.WeightScans);     // both rows still measured — once each, in the loop
        Assert.Equal(new[] { HRoot, HHip }, joints.Take(2).ToArray());
        Assert.Contains(HHead, joints);
    }

    /// <summary>…and the counter is real: a roster row the export never reads still costs one bundle read
    /// and one field fetch, and its candidacy still lands (the presence-excluded hair takes its bone off the
    /// tail).</summary>
    [Fact]
    public void TheCandidacyPass_StillReadsARosterRowTheExportSkipped()
    {
        using var g = new TempGame();
        var cache = new CandidacyCache(null);
        var joints = LoneClothJoints(g, FilterBundles(g), FilterRoster(hairToken: "hair1_Fight"), cache,
            includeHair: false);

        Assert.Equal(1, cache.BundleReads);      // the hair's bundle, which no export row opened
        Assert.Equal(1, cache.MeshReads);        // the hair, which no export row read
        Assert.Equal(new[] { HRoot, HHip }, joints);
    }

    /// <summary>The in-loop reuse joins the roster on the slot name case-INSENSITIVELY, but the mesh lookup
    /// that hands it a field selects <c>m_Name</c> case-SENSITIVELY at path id 0. A roster row differing
    /// from its export row only in case therefore addresses a mesh the loop never read, and must take the
    /// same route — and reach the same answer — as a row the export never touched at all. Claiming it in the
    /// loop would answer a row the gap pass drops, and the tail and the degraded report would both move
    /// depending on whether the part happened to be exported.</summary>
    [Fact]
    public void AMiscasedRosterRow_MeasuresLikeARowTheExportNeverRead()
    {
        // the control that gives the assertions below their teeth: when the names agree, the row measures,
        // nothing degrades, and the hair's bone IS on offer
        using var ok = new TempGame();
        var agreeingDegraded = new List<string>();
        var agreeing = LoneClothJoints(ok, FilterBundles(ok), FilterRoster(), new CandidacyCache(null),
            agreeingDegraded);
        Assert.Contains(HHead, agreeing);
        Assert.Empty(agreeingDegraded);

        // the export loop reads "hair1_lod0"; the roster row names it in upper case
        using var g = new TempGame();
        var loopDegraded = new List<string>();
        var throughLoop = LoneClothJoints(g, FilterBundles(g), FilterRoster(hairMesh: MiscasedHairSlot),
            new CandidacyCache(null), loopDegraded);

        // the same roster where that row can ONLY reach the gap pass — the hair is not an export row at all
        using var h = new TempGame();
        var gapDegraded = new List<string>();
        var throughGap = LoneClothJoints(h, FilterBundles(h), FilterRoster(hairMesh: MiscasedHairSlot),
            new CandidacyCache(null), gapDegraded, includeHair: false);

        Assert.Equal(new[] { MiscasedHairSlot }, gapDegraded.ToArray());
        Assert.Equal(gapDegraded, loopDegraded);
        Assert.Equal(throughGap, throughLoop);
        Assert.DoesNotContain(HHead, throughLoop);   // unmeasured ⇒ not a candidate ⇒ not offered
    }

    /// <summary>OPT 2: the mesh-derived half of candidacy is a function of the bundle's bytes, so a second
    /// open of the same subject sums no skin stream at all — and writes the very same tail.</summary>
    [Fact]
    public void AWarmCandidacyMemo_SumsNoWeightsAndWritesTheSameTail()
    {
        using var g = new TempGame();
        var vfs = FilterBundles(g);
        var memo = g.At("candidacy.json");

        var cold = new CandidacyCache(memo);
        var first = LoneClothJoints(g, vfs, FilterRoster(), cold);
        Assert.Equal(2, cold.WeightScans);

        var warm = new CandidacyCache(memo);
        var second = LoneClothJoints(g, vfs, FilterRoster(), warm);

        Assert.Equal(0, warm.WeightScans);
        Assert.Equal(2, warm.Hits);
        Assert.Equal(first, second);
        Assert.Contains(HHead, second);

        // Keys are one-way and payloads are packed hashes, so nothing game-derived lands in the file.
        var text = File.ReadAllText(memo);
        Assert.DoesNotContain(ClothSlot, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(HairSlot, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A <see cref="CandidacyCache"/> UNIT test, straight against the cache: no export runs here
    /// and no tail is owed. The memo is read whole on the first lookup of an open, so it is capped, and the
    /// cap must evict the least recently touched — a subject the modder is working on now must not be thrown
    /// out for one they opened last month.</summary>
    [Fact]
    public void TheCandidacyMemo_KeepsItsMostRecentRowsWhenItFills()
    {
        using var g = new TempGame();
        var vfs = FilterBundles(g);
        var field = new BundleReader().GetMeshField(
            vfs.TryDeobfuscateLogical(FilterClothLogical)!, ClothSlot, 0)!;
        var memo = g.At("candidacy.json");
        // three measurements, room for two
        var keys = new[] { "content-a", "content-b", "content-c" }
            .Select(id => CandidacyCache.Key(id, ClothSlot, 0)).ToArray();

        var filling = new CandidacyCache(memo, maxRows: 2);
        foreach (var k in keys) filling.Measure(k, field);
        filling.Flush();

        var reloaded = new CandidacyCache(memo);
        Assert.Null(reloaded.TryGet(keys[0]));      // touched first, so the first to go
        Assert.NotNull(reloaded.TryGet(keys[1]));
        Assert.NotNull(reloaded.TryGet(keys[2]));
    }

    /// <summary>A memo hit spares the GAP pass the BUNDLE, not just the scan. The key is minted from the
    /// manifest's stated content identity, so the memo is asked before anything is opened: a roster row the
    /// export never read costs the second open no segment read, no de-XOR, no field fetch and no sum. Keyed
    /// on a hash of the bytes instead, every one of those rows would still be read and deobfuscated in full
    /// before the memo could be consulted at all, which is the dear half.</summary>
    [Fact]
    public void AWarmCandidacyMemo_SparesTheGapPassItsRead()
    {
        using var g = new TempGame();
        var vfs = FilterBundles(g);
        var memo = g.At("candidacy.json");
        var roster = FilterRoster(hairToken: "hair1_Fight");

        var cold = new CandidacyCache(memo);
        var first = LoneClothJoints(g, vfs, roster, cold, includeHair: false);
        Assert.Equal(1, cold.BundleReads);
        Assert.Equal(1, cold.MeshReads);

        var warm = new CandidacyCache(memo);
        var second = LoneClothJoints(g, vfs, roster, warm, includeHair: false);

        Assert.Equal(0, warm.BundleReads);
        Assert.Equal(0, warm.MeshReads);
        Assert.Equal(0, warm.WeightScans);
        Assert.Equal(first, second);
    }

    /// <summary>A memo that won't parse is not an error — it is a cold one. Everything is measured afresh,
    /// the tail is the same, and the file is taken over so the run after it is warm again.</summary>
    [Fact]
    public void ACorruptCandidacyMemo_ReMeasuresAndTakesTheFileOver()
    {
        using var g = new TempGame();
        var vfs = FilterBundles(g);
        var memo = g.At("candidacy.json");
        File.WriteAllText(memo, "{ this is not the memo you are looking for");

        var over = new CandidacyCache(memo);
        var joints = LoneClothJoints(g, vfs, FilterRoster(), over);

        Assert.Equal(2, over.WeightScans);
        Assert.Equal(0, over.Hits);
        Assert.Equal(new[] { HRoot, HHip }, joints.Take(2).ToArray());
        Assert.Contains(HHead, joints);

        var after = new CandidacyCache(memo);
        Assert.Equal(joints, LoneClothJoints(g, vfs, FilterRoster(), after));
        Assert.Equal(0, after.WeightScans);
    }

    /// <summary>The key is the bundle's CONTENT: the same names over different content is a miss. This is a
    /// game update in miniature — one bundle rewritten (different vertex blob, same mesh name, same bones)
    /// and the manifest restating what that bundle now holds, which is the identity the memo reads. The
    /// updated bundle re-measures, the untouched one is still served, and the tail must not move.</summary>
    [Fact]
    public void ChangedBundleContent_MissesTheMemoAndReMeasures()
    {
        using var g = new TempGame();
        var memo = g.At("candidacy.json");
        var first = LoneClothJoints(g, FilterBundles(g), FilterRoster(), new CandidacyCache(memo));

        // same bundle id, same mesh name, same bones — a manifest that states new content for the hair, and
        // then the new bytes themselves (in that order: FilterBundles re-seeds both fixture bundles)
        var updated = FilterBundles(g, hairSubSeed: 9);
        SyntheticBundle.BuildOneSkinnedMesh(
            Path.Combine(g.At("AssetBundles_Windows"), FilterHairPhys + ".bundle"), HairSlot,
            SessionTri, SessionIdx, new[] { HRoot, HHead }, bundleName: FilterHairLogical, uvSeed: 7);

        var after = new CandidacyCache(memo);
        var second = LoneClothJoints(g, updated, FilterRoster(), after);

        Assert.Equal(1, after.WeightScans);   // the hair re-measured…
        Assert.Equal(1, after.Hits);          // …the untouched cloth served from the memo
        Assert.Equal(first, second);
    }

    /// <summary>A memo that could not be READ this run must not be rewritten out from under the next one. A
    /// sharing violation — another export mid-publish, a scanner holding the file — is indistinguishable
    /// from corruption at the read, so an unreadable file is treated as absent for this run and left alone:
    /// only a measurement earns a rewrite. Without that, one momentary lock followed by any successful flush
    /// replaces every row the file held with this run's handful.</summary>
    [Fact]
    public void ACandidacyMemoThatWouldNotOpen_IsLeftAsItStands()
    {
        using var g = new TempGame();
        var vfs = FilterBundles(g);
        var field = new BundleReader().GetMeshField(
            vfs.TryDeobfuscateLogical(FilterClothLogical)!, ClothSlot, 0)!;
        var memo = g.At("candidacy.json");
        var key = CandidacyCache.Key("content-a", ClothSlot, 0);

        var wrote = new CandidacyCache(memo);
        wrote.Measure(key, field);
        wrote.Flush();
        var published = File.ReadAllText(memo);

        // the LOOKUP happens while somebody else holds the file; the lock is gone by the flush, so a
        // rewrite would land — which is exactly the shape that used to eat the file
        var blocked = new CandidacyCache(memo);
        using (File.Open(memo, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Null(blocked.TryGet(key));    // reads as absent…
        blocked.Flush();                         // …and measured nothing, so it publishes nothing

        Assert.Equal(published, File.ReadAllText(memo));
        Assert.NotNull(new CandidacyCache(memo).TryGet(key));   // the next run is warm again
    }

    /// <summary>A hard kill between a publish's write and its move strands a temp beside the memo forever.
    /// A successful publish sweeps the ones this class MINTS — the memo's name, a dot, 32 hex digits,
    /// <c>.tmp</c> — and nothing else in the folder.</summary>
    [Fact]
    public void APublishedCandidacyMemo_SweepsStrandedTempsAndNothingElse()
    {
        using var g = new TempGame();
        var vfs = FilterBundles(g);
        var field = new BundleReader().GetMeshField(
            vfs.TryDeobfuscateLogical(FilterClothLogical)!, ClothSlot, 0)!;
        var memo = g.At("candidacy.json");

        var stranded = memo + "." + new string('a', 32) + ".tmp";
        File.WriteAllText(stranded, "half a memo");
        // neither of these is a name this class ever mints
        var foreign = memo + ".notaguid.tmp";
        var neighbour = g.At("candidacy.json.keep");
        File.WriteAllText(foreign, "somebody else's");
        File.WriteAllText(neighbour, "somebody else's");

        var cache = new CandidacyCache(memo);
        cache.Measure(CandidacyCache.Key("content-a", ClothSlot, 0), field);
        cache.Flush();

        Assert.True(File.Exists(memo));
        Assert.False(File.Exists(stranded));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(neighbour));
    }

    /// <summary>The memo's keys come from ONE identity home — the manifest's own statement of what a bundle
    /// holds — and a bundle the manifest does not name has no key and is never memoized, this run or any
    /// other. No second identity is invented to cover it. In the app that row cannot be measured either:
    /// locating a bundle walks the very same catalog→manifest join, so a manifest miss is an unreadable
    /// bundle, and the row degrades identically on every run instead of quietly being cached as anything.
    /// The rest of the subject is memoized around it, untouched.</summary>
    [Fact]
    public void ABundleTheManifestDoesNotName_IsNeverMemoized()
    {
        using var g = new TempGame();
        var vfs = FilterBundles(g, hairInManifest: false);
        var memo = g.At("candidacy.json");

        var coldDegraded = new List<string>();
        var cold = new CandidacyCache(memo);
        var first = LoneClothJoints(g, vfs, FilterRoster(), cold, coldDegraded);
        Assert.Equal(new[] { HairSlot }, coldDegraded.ToArray());
        Assert.Equal(1, cold.WeightScans);            // the cloth only — the hair never measured

        var warmDegraded = new List<string>();
        var warm = new CandidacyCache(memo);
        var second = LoneClothJoints(g, vfs, FilterRoster(), warm, warmDegraded);

        Assert.Equal(coldDegraded, warmDegraded);     // the same report, warm as cold
        Assert.Equal(first, second);
        Assert.Equal(1, warm.Hits);                   // the cloth, whose bundle the manifest does name…
        Assert.Equal(0, warm.WeightScans);
        Assert.Equal(1, warm.BundleReads);            // …and the hair, asked for and unlocatable again
    }

    /// <summary>A <see cref="CandidacyCache"/> UNIT test of the same rule at the seam: a null key measures
    /// for real and keeps NOTHING — no row, no file, nothing for a later run to hit. It is the shape a
    /// caller with no content identity for a bundle hands over.</summary>
    [Fact]
    public void ANullCandidacyKey_MeasuresAndKeepsNothing()
    {
        using var g = new TempGame();
        var vfs = FilterBundles(g);
        var field = new BundleReader().GetMeshField(
            vfs.TryDeobfuscateLogical(FilterClothLogical)!, ClothSlot, 0)!;
        var memo = g.At("candidacy.json");

        var cache = new CandidacyCache(memo);
        Assert.Null(cache.TryGet(null));
        var measured = cache.Measure(null, field);

        Assert.Equal(2, measured.Posed.Count);      // the answer is real, not a degraded stand-in
        Assert.Contains(HRoot, measured.Posed);
        Assert.Contains(HHip, measured.Posed);
        Assert.Equal(1, cache.WeightScans);
        cache.Flush();

        Assert.False(File.Exists(memo));      // nothing measured under a null key is ever published
        Assert.Null(cache.TryGet(null));
        Assert.Equal(0, cache.Hits);
    }

    /// <summary>A part whose weights can't be measured is never memoized as anything, so it degrades on
    /// every run alike — the caller is told the same thing warm as it is cold, and offers the same tail.
    /// A memo that quietly stopped reporting a drop would let a caller keep a tail it must not keep.</summary>
    [Fact]
    public void AnUnmeasurableRow_DegradesTheSameWarmAsCold()
    {
        using var g = new TempGame();
        var vfs = FilterBundles(g, hairUnreadable: true);
        var memo = g.At("candidacy.json");

        var coldDegraded = new List<string>();
        var cold = LoneClothJoints(g, vfs, FilterRoster(), new CandidacyCache(memo), coldDegraded);

        var warmDegraded = new List<string>();
        var warm = new CandidacyCache(memo);
        var second = LoneClothJoints(g, vfs, FilterRoster(), warm, warmDegraded);

        Assert.Equal(new[] { HairSlot }, coldDegraded.ToArray());
        Assert.Equal(coldDegraded, warmDegraded);
        Assert.Equal(new[] { HRoot, HHip }, cold);
        Assert.Equal(cold, second);
        Assert.Equal(1, warm.Hits);           // the cloth is still served from the memo…
        // …and only the hair is tried again — twice, once where the export loop had its field in hand and
        // once in the gap pass, since a measurement that throws in the loop deliberately leaves the row to
        // the pass that reports it degraded.
        Assert.Equal(2, warm.WeightScans);
    }

    /// <summary>The combined session ships ONE armature every part binds to, so its tail can only be the
    /// UNION of what its parts could each paint. A bone valid for one included part and not another is
    /// offered; a lone session is where a part gets its exact set.</summary>
    [Fact]
    public void ACombinedTail_TakesTheUnionOfTheIncludedPartsValidSets()
    {
        var roster = new[]
        {
            new PoolDerive.PartBones(ClothSlot, new HashSet<uint> { HRoot, HHip },
                PosedBones: new HashSet<uint> { HRoot, HHip }),
            new PoolDerive.PartBones(HairSlot, new HashSet<uint> { HRoot, HHead },
                Presence: new PartPresence(PresenceContext.Fight, PartPresence.NoVariant),
                PosedBones: new HashSet<uint> { HRoot, HHead }),
        };
        // the combat-only hair pools for nobody but itself, so its bone is valid for it and not for the cloth
        Assert.DoesNotContain(HHead, AssetExporter.ValidTailBones(roster, ClothSlot));
        Assert.Contains(HHead, AssetExporter.ValidTailBones(roster, HairSlot));

        var parts = new[] { new MeshGltf.RiggedPart(Part("cloth_lod0", 1), Skin(HRoot)) };
        var union = new HashSet<uint>(AssetExporter.ValidTailBones(roster, ClothSlot));
        union.UnionWith(AssetExporter.ValidTailBones(roster, HairSlot));

        Assert.Equal(new[] { HHip, HHead },
            AssetExporter.CombinedExtraBones(TwoPartSubject(), parts, union).Select(e => e.Hash).ToArray());
        // …and one part's own set alone would have dropped it — which is what the union is there to prevent
        Assert.Equal(new[] { HHip },
            AssetExporter.CombinedExtraBones(TwoPartSubject(), parts,
                AssetExporter.ValidTailBones(roster, ClothSlot)).Select(e => e.Hash).ToArray());
    }

    /// <summary>The combined fixture: a cloth part TABLING the arm at zero weight on top of the two bones it
    /// poses, beside the hair. The arm is then a subject bone no candidate poses — on the skeleton, and off
    /// every filtered tail.</summary>
    private static GameVfs ArmTablingClothBundles(TempGame g)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, FilterClothPhys + ".bundle"), ClothSlot,
            SessionTri, SessionIdx, new[] { HRoot, HHip }, bundleName: FilterClothLogical,
            tabledOnlyBones: new[] { HArm });
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, FilterHairPhys + ".bundle"), HairSlot,
            SessionTri, SessionIdx, new[] { HRoot, HHead }, bundleName: FilterHairLogical);
        return TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (FilterClothLogical, FilterClothPhys), (FilterHairLogical, FilterHairPhys));
    }

    /// <summary>One COMBINED session over that fixture, written to <paramref name="name"/>, and the cloth's
    /// joint hashes in it. The cloth comes from a workspace glb riding one bone: reduced to the bones its
    /// geometry rides, it leaves hip and arm to the shared tail.</summary>
    private static uint[] CombinedJoints(TempGame g, GameVfs vfs, string name,
        AssetExporter.SubjectRoster? roster, ICollection<string>? degraded = null)
    {
        Directory.CreateDirectory(g.At("meshes"));
        var ws = g.At(Path.Combine("meshes", "cloth_edit.glb"));
        if (!File.Exists(ws))
            MeshGltf.ExportRiggedGlb(Part(ClothSlot, 1), Skin(HRoot), h => Paths.GetValueOrDefault(h), ws);
        var combined = g.At(Path.Combine("meshes", name + ".glb"));
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, new Outfit(0, "VesnaSSR01", OutfitKind.Base), "Vesna",
            new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
            {
                ("cloth1", FilterClothLogical, ClothSlot, null, null, 0L, ws),
                ("hair1", FilterHairLogical, HairSlot, null, null, 0L, null),
            },
            g.At("textures"), combinedOut: combined, roster: roster, rosterDegraded: degraded);
        return MeshGltf.ImportPayload(combined, ClothSlot).SkinJointHashes!;
    }

    /// <summary>The combined ROUTE really does apply it. An edited part's re-read skin is reduced to the
    /// bones its geometry rides, which is what leaves the rest of its game bone table standing as tail —
    /// including one it only tabled, which no candidate poses and the session must not offer.</summary>
    [Fact]
    public void ACombinedSession_FiltersTheTailItAppends()
    {
        using var g = new TempGame();
        var vfs = ArmTablingClothBundles(g);

        var unfiltered = CombinedJoints(g, vfs, "unfiltered", null);
        Assert.Contains(HHip, unfiltered);
        Assert.Contains(HArm, unfiltered);      // the bone nothing poses, offered by the old rule

        var filtered = CombinedJoints(g, vfs, "filtered", FilterRoster());
        Assert.Contains(HHip, filtered);        // the cloth poses this one, so it stays paintable
        Assert.DoesNotContain(HArm, filtered);  // …and nothing poses this one
    }

    // ------------------------------------------------------- unknown candidacy offers MORE, never less
    //
    // Filtering is only ever sound where the roster was actually read. Where it wasn't — every row failing,
    // or the exported part missing from the rows that did — the answer is "unknown", and unknown must widen
    // the offer back to the whole skeleton rather than narrow it to nothing.

    /// <summary>A roster whose rows all name bundles that don't exist. Every row fails to measure, so nothing
    /// was learned about the subject — which is a different answer from learning that its parts pose
    /// nothing, and an empty valid set would have filtered the entire tail away.</summary>
    private static AssetExporter.SubjectRoster UnreadableRoster() =>
        new(new[]
        {
            new AssetExporter.RosterPart(ClothSlot, "cloth1", "nosuchbundle1", 0, true, VisibilityOverride.None),
            // Fight-gated: had this row read, it would have taken the head bone off an Always part's tail,
            // so the head surviving proves the fallback and not a filter that happened to admit it
            new AssetExporter.RosterPart(HairSlot, "hair1_Fight", "nosuchbundle2", 0, true, VisibilityOverride.None),
        });

    [Fact]
    public void ALoneTail_ARosterNoRowOfWhichReads_IsUnfiltered()
    {
        using var g = new TempGame();
        var joints = LoneClothJoints(g, FilterBundles(g), UnreadableRoster());

        Assert.Equal(new[] { HRoot, HHip }, joints.Take(2).ToArray());   // its own joints, unmoved
        Assert.Contains(HHead, joints);                                  // …and the whole skeleton behind them
    }

    /// <summary>The caller is told twice over: the drop narrows the tail (degraded), and the bundle's bytes
    /// were unavailable this run (unreadable) — a rerun may read them, so a caller caching the result keyed
    /// on content identities alone must not keep this one.</summary>
    [Fact]
    public void ARosterRowWhoseBundleWillNotRead_IsReportedUnreadable()
    {
        using var g = new TempGame();
        var degraded = new List<string>();
        var unreadable = new List<string>();
        // the cloth row reads; only the hair names a bundle that isn't there
        var roster = new AssetExporter.SubjectRoster(new[]
        {
            new AssetExporter.RosterPart(ClothSlot, "cloth1", FilterClothLogical, 0, true, VisibilityOverride.None),
            new AssetExporter.RosterPart(HairSlot, "hair1", "nosuchbundle", 0, true, VisibilityOverride.None),
        });
        var joints = LoneClothJoints(g, FilterBundles(g), roster, degraded, unreadable);

        Assert.Equal(new[] { HairSlot }, degraded.ToArray());
        Assert.Equal(new[] { HairSlot }, unreadable.ToArray());
        Assert.DoesNotContain(HHead, joints);   // the surviving row still filtered — the drop is what's reported
    }

    /// <summary>A row whose bundle DID serve its bytes but which holds no such mesh is a fact of the
    /// content: the same catalog serves the same bytes to every rerun, so the drop repeats identically and
    /// the tail it shaped is cacheable. Degraded says the tail narrowed; unreadable stays empty — the
    /// distinction the combined-session cache gate rests on, since every character's face row degrades
    /// this deterministic way on every open.</summary>
    [Fact]
    public void ARosterRowTheBundleDoesNotHold_DegradesButIsNotUnreadable()
    {
        using var g = new TempGame();
        var degraded = new List<string>();
        var unreadable = new List<string>();
        var roster = new AssetExporter.SubjectRoster(new[]
        {
            new AssetExporter.RosterPart(ClothSlot, "cloth1", FilterClothLogical, 0, true, VisibilityOverride.None),
            new AssetExporter.RosterPart("ghost", "ghost1", FilterHairLogical, 0, true, VisibilityOverride.None),
        });
        LoneClothJoints(g, FilterBundles(g), roster, degraded, unreadable);

        Assert.Equal(new[] { "ghost" }, degraded.ToArray());
        Assert.Empty(unreadable);
    }

    [Fact]
    public void ALoneTail_AnExportedPartTheRosterDoesNotCarry_IsUnfiltered()
    {
        // control: with the cloth listed, the combat-only hair's bone is filtered off its tail
        using var c = new TempGame();
        Assert.DoesNotContain(HHead,
            LoneClothJoints(c, FilterBundles(c), FilterRoster(hairToken: "hair1_Fight")));

        using var g = new TempGame();
        var degraded = new List<string>();
        // the same roster minus the exported part's own row: PoolCandidates would read an unlisted target as
        // an unconditional non-target, excluding every conditional sibling AND losing the part's own posed
        // set. Unknown candidacy is not "nothing is valid".
        var roster = new AssetExporter.SubjectRoster(new[]
        {
            new AssetExporter.RosterPart(HairSlot, "hair1_Fight", FilterHairLogical, 0, true,
                VisibilityOverride.None),
        });
        var joints = LoneClothJoints(g, FilterBundles(g), roster, degraded);

        Assert.Equal(new[] { HRoot, HHip }, joints.Take(2).ToArray());
        Assert.Contains(HHead, joints);
        Assert.Contains(AssetExporter.RosterUnfiltered, degraded);
    }

    /// <summary>The combined route ships ONE armature, so an included slot whose candidacy is unknown takes
    /// the whole union to unfiltered: narrowing by the slots that did read would hide bones the unknown part
    /// may well have been able to paint.</summary>
    [Fact]
    public void ACombinedTail_AnIncludedSlotTheRosterDoesNotCarry_LeavesTheWholeUnionUnfiltered()
    {
        using var g = new TempGame();
        var vfs = ArmTablingClothBundles(g);
        var degraded = new List<string>();

        // control: both slots listed, so the bone nothing poses is filtered off the shared tail
        Assert.DoesNotContain(HArm, CombinedJoints(g, vfs, "listed", FilterRoster(), null));

        var hairOnly = new AssetExporter.SubjectRoster(new[]
        {
            new AssetExporter.RosterPart(HairSlot, "hair1", FilterHairLogical, 0, true, VisibilityOverride.None),
        });
        var joints = CombinedJoints(g, vfs, "unlisted", hairOnly, degraded);

        Assert.Contains(HArm, joints);
        Assert.Contains(AssetExporter.RosterUnfiltered, degraded);
    }

    // ------------------------------------------------- a dropped bone an offered bone hangs off comes back
    //
    // MeshGltf registers every '/'-split prefix of an offered bone's path as a node, so the filter cannot
    // actually remove an ANCESTOR of a bone it keeps — it can only strip its hash suffix and park it at the
    // origin. Blender imports joint ancestors as bones either way, and paint on a hash-less one is dropped
    // silently on the way back instead of meeting the build's posed gate. So the ancestor stays a real
    // hash-named tail joint, which is the pre-filter behaviour and the loud one.

    /// <summary>The review's probe: root posed by the exported part, root/Hip_M and root/Hip_M/Head_M posed
    /// by a sibling, root/Arm_M by another — and only the head admitted. The hip is the ancestor;
    /// the arm is the control, an omission with no offered descendant behind it.</summary>
    private static IReadOnlyList<AssetExporter.SubjectBone> AncestorProbeSkeleton() =>
        AssetExporter.SubjectSkeleton(
            new[]
            {
                (Skin(HRoot), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                (Skin(HHip, HHead), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
                (Skin(HArm), (IReadOnlyList<string>?)null, (Matrix4x4?)null),
            },
            h => Paths.GetValueOrDefault(h), out _);

    [Fact]
    public void ALoneTail_ABoneTheFilterDropsThatAnOfferedBoneHangsOff_StaysAHashNamedJoint()
    {
        using var g = new TempGame();
        var extras = AssetExporter.ExtraBones(AncestorProbeSkeleton(), new[] { HRoot }, uprighting: null,
            new HashSet<uint> { HHead });

        // skeleton order kept, the hip restored ahead of the head it parents, the arm still gone
        Assert.Equal(new[] { HHip, HHead }, extras.Select(e => e.Hash).ToArray());

        var model = ExportAndReload(g.At("cloth_lod0.glb"), Skin(HRoot), extras);
        var skin = model.LogicalSkins.Single();
        var joints = Enumerable.Range(0, skin.JointsCount).Select(i => skin.Joints[i].Name).ToArray();

        Assert.Equal(new[] { NodeName(HRoot), NodeName(HHip), NodeName(HHead) }, joints);
        // the failure this guards: the hip present as a bare, hash-less node paint would be dropped from
        Assert.DoesNotContain(model.LogicalNodes, n => n.Name == "Hip_M");
        Assert.DoesNotContain(model.LogicalNodes, n => n.Name == NodeName(HArm));
    }

    [Fact]
    public void ACombinedTail_ABoneTheFilterDropsThatAnOfferedBoneHangsOff_StaysAHashNamedJoint()
    {
        using var g = new TempGame();
        var parts = new[] { new MeshGltf.RiggedPart(Part("cloth_lod0", 1), Skin(HRoot)) };
        var extras = AssetExporter.CombinedExtraBones(AncestorProbeSkeleton(), parts,
            new HashSet<uint> { HHead });

        Assert.Equal(new[] { HHip, HHead }, extras.Select(e => e.Hash).ToArray());

        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(parts, h => Paths.GetValueOrDefault(h), combined, extras);
        var model = ModelRoot.Load(combined);
        var skin = model.LogicalSkins.Single();
        var joints = Enumerable.Range(0, skin.JointsCount).Select(i => skin.Joints[i].Name).ToArray();

        Assert.Equal(new[] { NodeName(HRoot), NodeName(HHip), NodeName(HHead) }, joints);
        Assert.DoesNotContain(model.LogicalNodes, n => n.Name == "Hip_M");
        Assert.DoesNotContain(model.LogicalNodes, n => n.Name == NodeName(HArm));
    }

    // --------------------------------------------- the shared armature offers the UNION, not one part's set
    //
    // A combined session ships ONE armature every part binds to, so a per-part tail is structurally
    // impossible: the filter has to widen to the union of the included parts' valid sets. The two halves of
    // that — it is at least each member's set, and it is still a filter — need a fixture where the members'
    // sets are DISJOINT, which is what the two scene contexts buy: a Fight part and a Dorm part are never
    // on screen together, so neither can vouch for the other and each certifies its own bones alone.

    /// <summary>Cloth in combat, hair in the dorm, each addressing its own bundle of the filter fixture.
    /// Neither covers the other's presence, so the cloth's valid set is its own posed bones and the hair's is
    /// its own — disjoint past the shared root.</summary>
    private static AssetExporter.SubjectRoster ContextSplitRoster() =>
        new(new[]
        {
            new AssetExporter.RosterPart(ClothSlot, "cloth1_Fight", FilterClothLogical, 0, true,
                VisibilityOverride.None),
            new AssetExporter.RosterPart(HairSlot, "hair1_Dorm", FilterHairLogical, 0, true,
                VisibilityOverride.None),
        });

    /// <summary>One COMBINED session over that fixture with BOTH parts opened from a workspace glb riding
    /// the root alone. Reduced that far, every other bone of the subject is left to the SHARED tail — which
    /// is the only place a union is observable, since a bone any included part still poses is that part's own
    /// joint whatever the filter says. Returns the shared armature's joint hashes.</summary>
    private static uint[] CombinedUnionJoints(TempGame g, GameVfs vfs, string name,
        AssetExporter.SubjectRoster? roster)
    {
        Directory.CreateDirectory(g.At("meshes"));
        var clothWs = g.At(Path.Combine("meshes", "cloth_edit.glb"));
        var hairWs = g.At(Path.Combine("meshes", "hair_edit.glb"));
        if (!File.Exists(clothWs))
            MeshGltf.ExportRiggedGlb(Part(ClothSlot, 1), Skin(HRoot), h => Paths.GetValueOrDefault(h), clothWs);
        if (!File.Exists(hairWs))
            MeshGltf.ExportRiggedGlb(Part(HairSlot, 1), Skin(HRoot), h => Paths.GetValueOrDefault(h), hairWs);
        var combined = g.At(Path.Combine("meshes", name + ".glb"));
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, new Outfit(0, "VesnaSSR01", OutfitKind.Base), "Vesna",
            new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
            {
                ("cloth1", FilterClothLogical, ClothSlot, null, null, 0L, clothWs),
                ("hair1", FilterHairLogical, HairSlot, null, null, 0L, hairWs),
            },
            g.At("textures"), combinedOut: combined, roster: roster);
        return MeshGltf.ImportPayload(combined, ClothSlot).SkinJointHashes!;
    }

    /// <summary>The shared tail carries a bone only the cloth vouches for AND a bone only the hair vouches
    /// for, and still refuses the one nothing poses. Either member's set alone would drop half of that, and
    /// no filter at all would keep the arm.
    ///
    /// <para>Route: OpenSessionBlenderAsync's combined build → AssetExporter.BuildRiggedGlbs with
    /// combinedOut → its per-slot ValidFor union → MeshGltf.ExportCombinedRiggedGlb's appended tail.</para></summary>
    [Fact]
    public void ACombinedTail_IsTheUnionOfTheIncludedPartsValidSets()
    {
        using var g = new TempGame();
        // the hair TABLES the arm without posing it, so the subject skeleton carries a bone no candidate can
        // ever certify — the control that says the union is still a filter
        var vfs = FilterBundles(g, hairTablesArm: true);

        // with no roster the whole skeleton is on offer, arm included
        Assert.Equal(new[] { HRoot, HHip, HHead, HArm }, CombinedUnionJoints(g, vfs, "unfiltered", null));

        var joints = CombinedUnionJoints(g, vfs, "filtered", ContextSplitRoster());

        // the hip is the combat cloth's alone, the head the dorm hair's alone, and both are offered
        Assert.Equal(new[] { HRoot, HHip, HHead }, joints);

        // …while the LONE cloth, judged by the very same roster, gets its own exact set and no more: the
        // hair is off screen whenever the cloth is on it, so it vouches for nothing here
        using var h = new TempGame();
        Assert.Equal(new[] { HRoot, HHip },
            LoneClothJoints(h, FilterBundles(h, hairTablesArm: true), ContextSplitRoster()));
    }

    // -------------------------------------------------- the app's half: the rows it hands the export
    //
    // The filter is only as good as the roster the app assembles for it. Two strings that are NOT the same
    // string (the representative slot name the roster is keyed on, and the part token presence classifies
    // from), two forward routes to a mesh (recipe address through the catalog, or bundle + path id), and two
    // prefab flags two of the four candidacy rules read.

    /// <summary>The catalog + subject model in, the roster rows out — including the SMR route's two halves,
    /// which are required together: a bundle with no path id cannot select among a bundle's same-named
    /// copies, so such a part falls back to its recipe address like any other.
    ///
    /// <para>A part neither route reaches stays out, and that is not a neutral omission: it measures
    /// nothing, so it vouches for no sibling, and every OTHER part's tail narrows by exactly what it would
    /// have covered. (Its own tail goes the other way — an unlisted part has unknown candidacy and is
    /// offered everything.) Which is why the resolution here has to be the build's own, half for half.</para>
    ///
    /// <para>Route: OpenSessionBlenderAsync → MainWindowViewModel.ExportRoster →
    /// MainWindowViewModel.ExportRosterRows.</para></summary>
    [Fact]
    public void ExportRosterRows_ResolveEachPartsBundle_AndCarryTheFlagsCandidacyReads()
    {
        var catalog = CatalogIndex.ForTest(new[] { ("addr_cloth", "cloth.bundle"), ("addr_belt", "belt.bundle") });
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            // recipe-backed: the address resolves through the catalog, and no path id selects it
            new SubjectPart("cloth1", ClothSlot, "addr_cloth", Array.Empty<SubjectMaterial>(),
                CastsShadows: false),
            // smr-backed: bundle + path id, its (absent) address never consulted
            new SubjectPart("hair1", HairSlot, "", Array.Empty<SubjectMaterial>(),
                MeshBundle: "hair.bundle", MeshPathId: 77, Visibility: VisibilityOverride.CoatList),
            // HALF the smr route: a bundle, but no path id to select inside it — the address decides
            new SubjectPart("belt1", "belt1_lod0", "addr_belt", Array.Empty<SubjectMaterial>(),
                MeshBundle: "belt_smr.bundle", MeshPathId: 0),
            // …and the same half with no address to fall back on reaches no mesh at all
            new SubjectPart("cuff1", "cuff1_lod0", "", Array.Empty<SubjectMaterial>(),
                MeshBundle: "cuff.bundle", MeshPathId: 0),
            // neither route reaches a mesh
            new SubjectPart("boot1", "boot1_lod0", "", Array.Empty<SubjectMaterial>()),
            // an address this catalog doesn't carry
            new SubjectPart("glove1", "glove1_lod0", "addr_missing", Array.Empty<SubjectMaterial>()),
        }, Skeleton: null, Problems: Array.Empty<string>(), PartsPoolAlone: true);
        var scheme = new[] { new PartScheme.Slot(1, new[] { new PartScheme.Variant(1, true, new[] { "cloth1" }) }) };

        var roster = MainWindowViewModel.ExportRosterRows(catalog, model, scheme);

        // keyed by SLOT name, in model order, and the three unreachable parts left out
        Assert.Equal(new[] { ClothSlot, HairSlot, "belt1_lod0" }, roster.Parts.Select(p => p.Mesh).ToArray());
        Assert.Same(scheme, roster.Scheme);
        // the subject's own pooling rule rides along: it decides whether a sibling may vouch at all
        Assert.True(roster.PartsPoolAlone);

        var cloth = roster.Parts[0];
        Assert.Equal("cloth1", cloth.Token);      // the token presence classifies from — NOT the slot name
        Assert.Equal("cloth.bundle", cloth.SourceBundle);
        Assert.Equal(0L, cloth.PathId);           // recipe-backed: the name selects the mesh, not an id
        Assert.False(cloth.CastsShadows);
        Assert.Equal(VisibilityOverride.None, cloth.Visibility);

        var hair = roster.Parts[1];
        Assert.Equal("hair1", hair.Token);
        Assert.Equal("hair.bundle", hair.SourceBundle);
        Assert.Equal(77L, hair.PathId);
        Assert.True(hair.CastsShadows);
        Assert.Equal(VisibilityOverride.CoatList, hair.Visibility);

        // the half-smr part took the ADDRESS's bundle, not the one its MeshBundle names, and no id selects
        // it — taking the smr route on a bundle alone would read a mesh by name out of a bundle that ships
        // same-named copies precisely because it cannot be read that way
        var belt = roster.Parts[2];
        Assert.Equal("belt.bundle", belt.SourceBundle);
        Assert.Equal(0L, belt.PathId);
    }

    /// <summary>What a failed wardrobe-table read costs, and how long it is believed for.
    ///
    /// <para>Unreadable tables are the WORSE of the two answers a null scheme can carry: every modular part
    /// classifies unknown, no sibling vouches for another, and the tail falls back to the part's own posed
    /// bones — bones a build would have accepted paint on are simply not offered. So an open says it.</para>
    ///
    /// <para>And a failure that will answer the same way every time is kept rather than re-read: only a
    /// lock earns another four-table read next open. Not-found is an IOException by inheritance and a fact
    /// of the install by nature, which is exactly where "retry every I/O failure" gets it wrong.</para>
    ///
    /// <para>Route: OpenSessionBlenderAsync → MainWindowViewModel.ExportRoster → ExportScheme's memo →
    /// the open's own status line.</para></summary>
    [Fact]
    public void UnreadableWardrobeTables_AreSaidOutLoud_AndReReadOnlyWhenARetryCouldDiffer()
    {
        Assert.Empty(MainWindowViewModel.BlenderOpenNotices(Array.Empty<string>(), false,
            Array.Empty<string>()));
        Assert.Contains("wardrobe tables", Assert.Single(MainWindowViewModel.BlenderOpenNotices(
            Array.Empty<string>(), true, Array.Empty<string>())));

        Assert.True(MainWindowViewModel.RetryTableRead(new IOException("the file is in use")));
        Assert.False(MainWindowViewModel.RetryTableRead(new FileNotFoundException("no such table")));
        Assert.False(MainWindowViewModel.RetryTableRead(new DirectoryNotFoundException("no such folder")));
        Assert.False(MainWindowViewModel.RetryTableRead(new InvalidDataException("the table won't parse")));
    }

    /// <summary>The subject model the open holds, as the app's own rows address the filter fixture: the two
    /// parts by recipe address, resolved through a catalog carrying exactly those two rows.
    /// <paramref name="hairToken"/> is the string presence is classified from, which is not the slot name the
    /// mesh is read by.</summary>
    private static AssetExporter.SubjectRoster AppAssembledRoster(string hairToken)
    {
        var catalog = CatalogIndex.ForTest(new[]
        {
            ("addr_cloth", FilterClothLogical),
            ("addr_hair", FilterHairLogical),
        });
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("cloth1", ClothSlot, "addr_cloth", Array.Empty<SubjectMaterial>()),
            new SubjectPart(hairToken, HairSlot, "addr_hair", Array.Empty<SubjectMaterial>()),
        }, Skeleton: null, Problems: Array.Empty<string>());
        return MainWindowViewModel.ExportRosterRows(catalog, model, scheme: null);
    }

    /// <summary>The two halves joined: rows the APP assembled off a subject model, handed to the real export,
    /// change what the tail offers. Both models differ in ONE string — the hair's part token — and the tail
    /// they produce differs by exactly that sibling's bone.
    ///
    /// <para>This is where the slot-name/token pairing earns its keep on the route rather than field by
    /// field: the slot name is what the roster row's mesh is READ by, and the token is what presence is
    /// classified from. Pair them the other way round and no row measures at all, so candidacy comes back
    /// unknown and both cases offer the whole skeleton.</para>
    ///
    /// <para>Route: OpenSessionBlenderAsync → MainWindowViewModel.ExportRosterRows →
    /// AssetExporter.BuildRiggedGlbs (per-part) → MeshGltf.ExportRiggedGlb's appended tail.</para></summary>
    [Fact]
    public void ARosterTheAppAssembles_IsWhatTheOpensTailIsFilteredBy()
    {
        // an unconditional sibling is on screen whenever the cloth is, so its bone is paintable
        using var g = new TempGame();
        var always = LoneClothJoints(g, FilterBundles(g), AppAssembledRoster("hair1"));
        Assert.Equal(new[] { HRoot, HHip }, always.Take(2).ToArray());
        Assert.Contains(HHead, always);

        // the same subject with the hair in combat only: it can vouch for nothing, and the tail is empty
        using var h = new TempGame();
        Assert.Equal(new[] { HRoot, HHip },
            LoneClothJoints(h, FilterBundles(h), AppAssembledRoster("hair1_Fight")));
    }

    /// <summary>Every rigged build the APP starts is given a roster and the candidacy memo. The two halves
    /// above are each pinned on their own, and joining them is the one step no behavioural test in this
    /// suite can take: the open they meet in (<c>OpenSessionBlenderAsync</c>) needs a loaded install, a
    /// located Blender and the bridge script before it reaches either call. So the argument lists themselves
    /// are the pin — which is exactly the regression that has already happened once, a refactor dropping the
    /// roster from both calls with every test still green and every Blender open back on the whole
    /// skeleton.</summary>
    [Fact]
    public void EveryRiggedBuildTheAppStarts_IsGivenACandidacyRosterAndTheMemo()
    {
        var calls = new List<(string File, string Args)>();
        foreach (var path in Directory.EnumerateFiles(
                     Path.Combine(SourceHygieneTests.RepoRoot(), "src", "Remold.App"), "*.cs",
                     SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(SourceHygieneTests.RepoRoot(), path);
            if (rel.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || rel.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            foreach (var args in CallArguments(WithoutComments(File.ReadAllText(path)),
                         "AssetExporter.BuildRiggedGlbs("))
                calls.Add((rel, args));
        }

        // a rename that emptied this would otherwise pass in silence
        Assert.NotEmpty(calls);
        Assert.Empty(calls.Where(c => !c.Args.Contains("roster:", StringComparison.Ordinal))
            .Select(c => c.File + ": a rigged build with no roster offers the whole skeleton"));
        Assert.Empty(calls.Where(c => !c.Args.Contains("candidacyCacheFile:", StringComparison.Ordinal))
            .Select(c => c.File + ": a rigged build with no candidacy memo re-sums every part's skin"));
        // …and NAMING the argument is not passing one: `roster: null` reads as a roster to a text scan and
        // offers the whole skeleton just the same, which is the regression this pin exists to catch.
        Assert.Empty(calls
            .Where(c => c.Args.Contains("roster: null", StringComparison.Ordinal)
                || c.Args.Contains("roster: default", StringComparison.Ordinal))
            .Select(c => c.File + ": a rigged build handed no roster offers the whole skeleton"));
    }

    /// <summary>The source with its comments blanked out, so a scan for a CALL cannot be answered by prose
    /// naming one. Blanked rather than deleted: nothing here needs offsets, but keeping the length makes the
    /// transform impossible to get subtly wrong. String and character literals are walked through intact —
    /// a <c>//</c> inside a path literal is not a comment — and a verbatim string's only escape is
    /// <c>""</c>.</summary>
    private static string WithoutComments(string source)
    {
        var chars = source.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            // COMMENTS first: everything below is a code-state reading, and an apostrophe in prose ("the
            // part's") is not the start of a character literal.
            if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                for (; i < chars.Length && chars[i] != '\n'; i++) chars[i] = ' ';
                continue;
            }
            if (c == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? chars.Length : end + 2;
                for (int k = i; k < end; k++) if (chars[k] != '\n') chars[k] = ' ';
                i = end - 1;
                continue;
            }
            if (c == '@' && i + 1 < chars.Length && chars[i + 1] == '"')
            {
                for (i += 2; i < chars.Length; i++)
                {
                    if (chars[i] != '"') continue;
                    if (i + 1 < chars.Length && chars[i + 1] == '"') { i++; continue; }
                    break;
                }
                continue;
            }
            if (c == '"' || c == '\'')
                for (i++; i < chars.Length && chars[i] != c; i++)
                    if (chars[i] == '\\') i++;
        }
        return new string(chars);
    }

    /// <summary>Every argument list following <paramref name="call"/> in <paramref name="source"/>, from the
    /// opening parenthesis to its match. Parentheses inside string and character literals are skipped, since
    /// an argument list here carries both.</summary>
    private static IEnumerable<string> CallArguments(string source, string call)
    {
        for (int at = source.IndexOf(call, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(call, at + 1, StringComparison.Ordinal))
        {
            int start = at + call.Length, depth = 1, i = start;
            for (; i < source.Length && depth > 0; i++)
            {
                char c = source[i];
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    for (i++; i < source.Length && source[i] != quote; i++)
                        if (source[i] == '\\') i++;
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')') depth--;
            }
            Assert.Equal(0, depth);   // an unbalanced call means this scanner, not the source, is wrong
            yield return source[start..(i - 1)];
        }
    }
}
