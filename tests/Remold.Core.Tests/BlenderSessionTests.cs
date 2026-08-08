using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.App.ViewModels;
using Remold.Core.Blender;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Skeleton;
using Remold.Core.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The session contract between the app and the Blender bridge, both directions.
///
/// <para>Outbound: every open carries the whole outfit on one armature, so the glb alone cannot say which
/// mesh the session may write back. The session file beside it does.</para>
///
/// <para>Inbound: a send carries only the parts that shipped, so an absent part means nothing — the
/// emptied-part list is the only thing that hides one. And because the session glb carries EVERY part's
/// maps, its map sidecar has to survive the publish, or a stock map read back resolves as authored.</para>
/// </summary>
public class BlenderSessionTests
{
    // ---------------------------------------------------------------- outbound: the session file

    [Fact]
    public void WriteSession_RoundTripsThePartAndTheEditedFlags()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        BlenderBridge.WriteSession(glb, "body1_lod0", new[]
        {
            new SessionPart("body1_lod0", Edited: true),
            new SessionPart("cloth1_lod0", Edited: false),
        });

        Assert.EndsWith("_combined.gf2session.json", BlenderBridge.SessionPath(glb));
        var (part, parts) = BlenderBridge.ReadSession(glb);
        Assert.Equal("body1_lod0", part);
        Assert.Equal(new[] { "body1_lod0", "cloth1_lod0" }, parts.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { true, false }, parts.Select(p => p.Edited).ToArray());
    }

    /// <summary>The bridge reads <c>sendAs</c> off the session json (its reader is a deliberate copy of
    /// this contract), so the property name and value are pinned here. A session that names none keeps the
    /// key out entirely: the bridge then sends under the opened glb's own name, the part-session
    /// overwrite-in-place contract.</summary>
    [Fact]
    public void WriteSession_NamesTheSendFile_OnlyWhenAsked()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        var parts = new[] { new SessionPart("body1_lod0", Edited: false) };

        BlenderBridge.WriteSession(glb, null, parts, sendAs: AssetExporter.CombinedSendGlbName);
        Assert.Contains("\"sendAs\": \"_combined.send.glb\"", File.ReadAllText(BlenderBridge.SessionPath(glb)));

        BlenderBridge.WriteSession(glb, null, parts);
        Assert.DoesNotContain("sendAs", File.ReadAllText(BlenderBridge.SessionPath(glb)));
    }

    /// <summary>The unskinned marker is the bridge's authority for exempting a part from the weight gate
    /// (its reader is a deliberate copy of this contract), so the property name and its omission are pinned
    /// here. A skinned part writes no key at all, so a session file stays byte-identical to what an
    /// all-skinned outfit has always written.</summary>
    [Fact]
    public void WriteSession_MarksAnUnskinnedPart_AndSaysNothingAboutASkinnedOne()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        BlenderBridge.WriteSession(glb, null, new[]
        {
            new SessionPart("prop_lod0", Edited: false, Unskinned: true),
            new SessionPart("body1_lod0", Edited: false),
        });

        var json = File.ReadAllText(BlenderBridge.SessionPath(glb));
        Assert.Contains("\"unskinned\": true", json);
        Assert.Equal(1, json.Split("\"unskinned\"").Length - 1);   // the skinned part writes no key

        var (_, parts) = BlenderBridge.ReadSession(glb);
        Assert.Equal(new[] { true, false }, parts.Select(p => p.Unskinned).ToArray());
    }

    /// <summary>A session file written before the marker existed reads as fully skinned, which is the
    /// state that still blocks — the exemption is only ever an explicit declaration.</summary>
    [Fact]
    public void ReadSession_APartWithNoUnskinnedKey_ReadsAsSkinned()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        File.WriteAllText(BlenderBridge.SessionPath(glb),
            """{"part":null,"parts":[{"name":"body1_lod0","edited":false,"writable":true}]}""");

        var (_, parts) = BlenderBridge.ReadSession(glb);
        Assert.False(Assert.Single(parts).Unskinned);
    }

    [Fact]
    public void WriteSession_NoNamedPart_MeansEveryPartIsWritable()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        BlenderBridge.WriteSession(glb, null, new[] { new SessionPart("body1_lod0", false) });

        var (part, parts) = BlenderBridge.ReadSession(glb);
        Assert.Null(part);                       // the bridge reads this as "all of them"
        Assert.Single(parts);
    }

    /// <summary>A part whose GAME mesh can't be replaced rides the combined session as context: it exports, so
    /// the modder can edit the head against the face, but the session must not offer it back. The app is what
    /// declares that; the bridge turns an unwritable part into Reference scenery.</summary>
    [Fact]
    public void TheCombinedSessionDeclaresAnUnreplaceableParts_Unwritable()
    {
        using var g = new TempGame();
        SyntheticBundle.BuildOneSkinnedMesh(g.At("face.bundle"), "face_lod0",
            SessionTri, SessionIdx, SessionBones, blendShapes: 21);
        SyntheticBundle.BuildOneSkinnedMesh(g.At("hair.bundle"), "hair_lod0",
            SessionTri, SessionIdx, SessionBones);
        byte[]? Deobfuscate(string id) => id switch
        {
            "b_face" => File.ReadAllBytes(g.At("face.bundle")),
            "b_hair" => File.ReadAllBytes(g.At("hair.bundle")),
            _ => null,
        };

        var spec = new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
        {
            ("face", "b_face", "face_lod0", null, null, 0L, null),
            ("hair", "b_hair", "hair_lod0", null, null, 0L, null),
        };
        var parts = new List<SessionPart>
        {
            new("face_lod0", Edited: false),
            new("hair_lod0", Edited: false),
        };

        MainWindowViewModel.DeclareUnwritableParts(Deobfuscate, spec, parts);

        Assert.Equal(new[] { false, true }, parts.Select(p => p.IsWritable).ToArray());

        // …and it survives the sidecar the bridge reads
        var glb = g.At("_combined.glb");
        BlenderBridge.WriteSession(glb, null, parts);
        var (_, back) = BlenderBridge.ReadSession(glb);
        Assert.Equal(new[] { "hair_lod0" },
            back.Where(p => p.IsWritable).Select(p => p.Name).ToArray());
    }

    /// <summary>A mesh the read can't reach is left writable: that failure has its own route, and pulling the
    /// part into Reference over it would take away an edit that works.</summary>
    [Fact]
    public void ACombinedSessionsUnreadableMesh_StaysWritable()
    {
        var spec = new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
        {
            ("body", "b_gone", "body_lod0", null, null, 0L, null),
        };
        var parts = new List<SessionPart> { new("body_lod0", Edited: false) };

        MainWindowViewModel.DeclareUnwritableParts(_ => null, spec, parts);

        Assert.True(parts[0].IsWritable);
    }

    /// <summary>The sidecar's default: a part written without the flag is writable, so the shape a session
    /// declares nothing about behaves exactly as it did.</summary>
    [Fact]
    public void ASessionPartWithNoWritableFlag_ReadsAsWritable()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        File.WriteAllText(BlenderBridge.SessionPath(glb),
            "{\"part\": null, \"parts\": [{\"name\": \"body1_lod0\", \"edited\": false}]}");

        var (_, parts) = BlenderBridge.ReadSession(glb);

        Assert.True(Assert.Single(parts).IsWritable);
    }

    private static readonly float[] SessionTri = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] SessionIdx = { 0, 1, 2 };
    private static readonly uint[] SessionBones = { 11u, 22u };

    [Fact]
    public void ReadSession_NoFile_ReadsAsNoSession()
    {
        using var g = new TempGame();
        var (part, parts) = BlenderBridge.ReadSession(g.At("nothing.glb"));
        Assert.Null(part);
        Assert.Empty(parts);
    }

    // ---------------------------------------------------------------- inbound: the emptied-part list

    [Fact]
    public void ReadSend_CarriesTheEmptiedPartsTheBridgeNamed()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb),
            "{\"source\":\"blender-send\",\"hiddenParts\":[\"cloth1_lod0\",\"hair_lod0\"]}");

        var edit = BlenderBridge.ReadSend(glb);

        Assert.Equal(new[] { "cloth1_lod0", "hair_lod0" }, edit.HiddenParts!.ToArray());
    }

    [Fact]
    public void ReadSend_SidecarNamesNoEmptiedParts_HidesNothing()
    {
        // Nothing hides by default: absence of a mesh is never the signal, so a sidecar naming no emptied
        // part leaves every Hide toggle exactly as the modder set it.
        using var g = new TempGame();
        var glb = g.At("body1_lod0.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");

        Assert.Empty(BlenderBridge.ReadSend(glb).HiddenParts!);
    }

    [Fact]
    public void ReadSend_AHideOnlySend_CarriesNoMeshAndStillNamesWhatToHide()
    {
        // Emptying the one part a per-part session holds is how it is hidden, so the send goes out with no
        // mesh at all. Reading that back as an unreadable glb would throw the Hide away.
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        var model = SharpGLTF.Schema2.ModelRoot.CreateModel();
        model.UseScene("scene").CreateNode("armature");
        model.SaveGLB(glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb),
            "{\"source\":\"blender-send\",\"hiddenParts\":[\"cloth2_lod0\"]}");

        var edit = BlenderBridge.ReadSend(glb);

        Assert.Null(edit.Mesh);
        Assert.Equal(new[] { "cloth2_lod0" }, edit.HiddenParts!.ToArray());
        Assert.Empty(MeshGltf.MeshNames(glb));   // …so no part reads as "came back" and none is rewritten
    }

    [Fact]
    public void MeshNames_ReportWhatASendActuallyCarried()
    {
        // How the app tells a part that came back from one that was only context: the target list cannot
        // say, since every part of the outfit has a target either way.
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0"), RiggedPart("cloth1_lod0") },
            h => Paths[h], glb);

        Assert.Equal(new[] { "body1_lod0", "cloth1_lod0" }, MeshGltf.MeshNames(glb).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void ASinglePartSend_AppliesToThatPartAlone()
    {
        // The two primitives the send-back's per-target decision rests on: what the glb carries, and that a
        // name it does NOT carry is a loud miss, not a silent read of the first mesh. Without both, a
        // one-part send into a multi-part outfit writes that part's geometry over its siblings.
        using var g = new TempGame();
        var returned = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("cloth1_lod0") }, h => Paths[h], returned);

        Assert.Equal(new[] { "cloth1_lod0" }, MeshGltf.MeshNames(returned).ToArray());
        MeshGltf.ReexportPartGlb(returned, "cloth1_lod0", g.At("cloth1.glb"));
        Assert.Contains("body1_lod0", Assert.Throws<InvalidOperationException>(
            () => MeshGltf.ReexportPartGlb(returned, "body1_lod0", g.At("body1.glb"))).Message);
    }

    [Fact]
    public void ACombinedContextPart_ExportsPosedAtItsPrefabSceneRest()
    {
        // The weapon shape: rigid geometry authored at its own origin, placed only by the prefab's rest.
        // A context part never round-trips, so its glb carries POSED bytes and its joint sits at the posed
        // world — in Blender mesh and bone agree, instead of the mesh at the origin under a floating bone.
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        const uint HW = 0x2222_2222;
        var mount = Matrix4x4.CreateTranslation(0, 0.5f, 2);
        var weapon = new MeshGltf.RiggedPart(Triangle("weapon_lod0"),
            new MeshSkin { BoneHashes = new[] { HW }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } },
            ContextPose: new[] { mount });
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0"), weapon },
            h => h == HRoot ? "root" : "root/weapon_01", glb);

        // the context mesh reads back posed; its sibling stays raw
        var raw = Triangle("weapon_lod0").Channels["Vertex"];
        var posed = MeshGltf.ImportGlb(glb, "weapon_lod0").Channels["Vertex"];
        for (int i = 0; i < raw.Length; i += 3)
        {
            Assert.Equal(raw[i], posed[i], 3);
            Assert.Equal(raw[i + 1] + 0.5f, posed[i + 1], 3);
            Assert.Equal(raw[i + 2] + 2f, posed[i + 2], 3);
        }
        Assert.Equal(Triangle("body1_lod0").Channels["Vertex"],
            MeshGltf.ImportGlb(glb, "body1_lod0").Channels["Vertex"]);

        // and the joint stands at the posed world, so posing it pivots the geometry that sits under it
        var model = SharpGLTF.Schema2.ModelRoot.Load(glb);
        var joint = model.LogicalNodes.Single(n => n.Name == "weapon_01_22222222");
        Assert.Equal(new System.Numerics.Vector3(0, 0.5f, 2), joint.WorldMatrix.Translation);
    }

    // ---------------------------------------------------------------- where each part class sits

    /// <summary>The open-weapon shape: a part the mod CAN replace, mounted by a translation the rest bake
    /// refuses. Its compile round-trips raw bind-space bytes, so the session poses neither its geometry nor
    /// its joints and the connector rests stay in bind space — mesh and armature together at its own
    /// origin.</summary>
    [Fact]
    public void ACombinedPose_AReplaceablePartWithATranslatedRest_TakesNoPose()
    {
        var rig = MountedRig();

        var (pose, connectors) = AssetExporter.CombinedPose(rig, uprighting: null, replaceable: () => true);

        Assert.Null(pose);
        Assert.Same(rig.ConnectorRests, connectors);
    }

    /// <summary>The closed-weapon shape: the SAME rest on a part no session can write back. It is context, so
    /// it poses at the prefab's scene rests and its connectors come back out of bind space.</summary>
    [Fact]
    public void ACombinedPose_AContextPartWithTheSameRest_PosesAtThePrefabSceneRests()
    {
        var rig = MountedRig();

        var (pose, connectors) = AssetExporter.CombinedPose(rig, uprighting: null, replaceable: () => false);

        Assert.Same(rig.BoneRestWorlds, pose);
        Assert.Equal(Mount.Translation, connectors!["hips"].Translation);
    }

    /// <summary>A part whose geometry already carries its rest takes no pose either way, and the gated read
    /// behind "replaceable" costs a bundle deobfuscate plus a mesh deserialize — so it is never asked where
    /// the answer cannot change the result.</summary>
    [Fact]
    public void ACombinedPose_ABakedPart_TakesNoPose_AndNeverPaysForTheGatedRead()
    {
        bool asked = false;

        var (pose, _) = AssetExporter.CombinedPose(MountedRig(), Matrix4x4.CreateRotationX(MathF.PI / 2),
            () => { asked = true; return false; });

        Assert.Null(pose);
        Assert.False(asked);
    }

    /// <summary>The decision through the export it feeds: with no pose the joint stands where the part's own
    /// bind pose puts it, over unposed geometry. That coincidence is what the modder sees as one object in
    /// Blender rather than a mesh at the origin under a bone floating at the mount.</summary>
    [Fact]
    public void ACombinedReplaceablePart_ExportsItsMeshAndItsJointAtItsOwnRest()
    {
        using var g = new TempGame();
        var glb = g.At("_combined.glb");
        const uint HW = 0x2222_2222;
        var (pose, connectors) = AssetExporter.CombinedPose(MountedRig(), uprighting: null, replaceable: () => true);
        var weapon = new MeshGltf.RiggedPart(Triangle("weapon_lod0"),
            new MeshSkin { BoneHashes = new[] { HW }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } },
            ConnectorRests: connectors, ContextPose: pose);

        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0"), weapon },
            h => h == HRoot ? "root" : "hips/weapon_01", glb);

        Assert.Equal(Triangle("weapon_lod0").Channels["Vertex"],
            MeshGltf.ImportGlb(glb, "weapon_lod0").Channels["Vertex"]);
        var model = SharpGLTF.Schema2.ModelRoot.Load(glb);
        var joint = model.LogicalNodes.Single(n => n.Name == "weapon_01_22222222");
        Assert.True(joint.WorldMatrix.Translation.Length() < 1e-5f,
            $"joint world translation: {joint.WorldMatrix.Translation}");
    }

    /// <summary>The same part opened ALONE, on its own workspace glb. That file is the one the compile round
    /// trips, so it carries no prefab placement either: the joint stands at the part's own bind rest over
    /// unposed geometry, exactly where the combined session puts a replaceable part. The two routes writing
    /// the same part to two different places is what a modder sees as the part jumping between sessions.</summary>
    [Fact]
    public void ALoneReplaceablePart_ExportsItsMeshAndItsJointAtItsOwnRest()
    {
        using var g = new TempGame();
        var ws = g.At("weapon_lod0.glb");
        const uint HW = 0x2222_2222;
        var rig = MountedRig();
        var skin = new MeshSkin { BoneHashes = new[] { HW }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } };

        MeshGltf.ExportRiggedGlb(Triangle("weapon_lod0"), skin, h => h == HRoot ? "root" : null, ws,
            scenePaths: rig.BonePaths, uprighting: null, connectorRests: rig.ConnectorRests);

        Assert.Equal(Triangle("weapon_lod0").Channels["Vertex"], MeshGltf.ImportGlb(ws).Channels["Vertex"]);
        var model = SharpGLTF.Schema2.ModelRoot.Load(ws);
        var joint = model.LogicalNodes.Single(n => n.Name == "weapon_01_22222222");
        Assert.True(joint.WorldMatrix.Translation.Length() < 1e-5f,
            $"joint world translation: {joint.WorldMatrix.Translation}");
        // the connector above it keeps its BIND-space rest: with no placement there is nothing to compose
        Assert.True(model.LogicalNodes.Single(n => n.Name == "hips").WorldMatrix.Translation.Length() < 1e-5f);
    }

    /// <summary>The two routes' answer for one part, side by side. A part that opens alone and the same part
    /// opened inside its outfit have to land in the same space, or an edit made in one session shows displaced
    /// in the other.</summary>
    [Fact]
    public void TheLoneAndCombinedRoutes_PlaceAReplaceablePartIdentically()
    {
        using var g = new TempGame();
        const uint HW = 0x2222_2222;
        var rig = MountedRig();
        var skin = new MeshSkin { BoneHashes = new[] { HW }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } };
        string Resolve(uint h) => h == HRoot ? "root" : "hips/weapon_01";

        var lone = g.At("weapon_lod0.glb");
        MeshGltf.ExportRiggedGlb(Triangle("weapon_lod0"), skin, Resolve, lone,
            scenePaths: rig.BonePaths, uprighting: null, connectorRests: rig.ConnectorRests);

        var combined = g.At("_combined.glb");
        var (pose, connectors) = AssetExporter.CombinedPose(rig, uprighting: null, replaceable: () => true);
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            RiggedPart("body1_lod0"),
            new MeshGltf.RiggedPart(Triangle("weapon_lod0"), skin, ConnectorRests: connectors, ContextPose: pose),
        }, Resolve, combined);

        Assert.Equal(MeshGltf.ImportGlb(combined, "weapon_lod0").Channels["Vertex"],
                     MeshGltf.ImportGlb(lone).Channels["Vertex"]);
        Assert.Equal(JointWorld(combined), JointWorld(lone));

        static Matrix4x4 JointWorld(string glb) => SharpGLTF.Schema2.ModelRoot.Load(glb)
            .LogicalNodes.Single(n => n.Name == "weapon_01_22222222").WorldMatrix;
    }

    /// <summary>Both routes out of ONE real rebuild. The part is rigged by its OWN bundle at a prefab MOUNT
    /// offset, and the rebuild writes its per-part glb (the lone session's file, and the one a compile round
    /// trips) alongside the union-armature glb (the outfit session's). The per-part file takes no placement
    /// from the mount: mesh and joint stand together at the part's own origin, exactly where the combined
    /// session puts a replaceable part. One part written to two different places is what a modder sees as it
    /// jumping between sessions.</summary>
    [Fact]
    public void TheRebuild_PlacesAPartsOwnGlbWhereTheCombinedOnePutsIt()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        const string weaponLogical = "wwwwwwwwwwwwwwwwwwwwwwwwwwwwwww1.bundle";
        const string bodyLogical = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1.bundle";
        const string weaponPhys = "11111111111111111111111111111111";
        const string bodyPhys = "22222222222222222222222222222222";
        // the hash anchors the path at "hips/weapon_01", so the mount node above the bone is a CONNECTOR
        uint weaponHash = BoneTable.Hash("hips/weapon_01");

        SyntheticBundle.BuildSelfRiggedMesh(Path.Combine(abw, weaponPhys + ".bundle"), "weapon_lod0",
            SessionTri, SessionIdx, new[] { weaponHash },
            new[]
            {
                new SyntheticBundle.RigNode("hips", -1, 0f, 0.5f, 2f),   // the prefab mount offset
                new SyntheticBundle.RigNode("weapon_01", 0, 0f, 0f, 0f),
            },
            skinBones: new[] { 1 }, bundleName: weaponLogical);
        // a second skinned part, so the same run has a union armature to build as well
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, bodyPhys + ".bundle"), "body1_lod0",
            SessionTri, SessionIdx, SessionBones, bundleName: bodyLogical);
        var vfs = TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (weaponLogical, weaponPhys), (bodyLogical, bodyPhys));

        // the fixture really does mount the part away from its bind origin, and unbakeably so — without a
        // measured offset there is no placement question for either route to answer wrong
        var weaponDec = vfs.TryDeobfuscateLogical(weaponLogical)!;
        var rig = SceneRig.TryRead(weaponDec, "weapon_lod0",
            MeshSkin.Decode(new Remold.Core.Bundles.BundleReader().GetMeshField(weaponDec, "weapon_lod0")!));
        Assert.Null(rig!.Uprighting);
        Assert.Equal(new Vector3(0, 0.5f, 2), rig.MeasuredRest!.Value.Translation);

        var lone = g.At(Path.Combine("meshes", "weapon_lod0.glb"));
        var combined = g.At(Path.Combine("meshes", "_combined.glb"));
        var spec = new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>
        {
            ("weapon", weaponLogical, "weapon_lod0", lone, null, 0L, null),   // GlbOut ⇒ the lone route's file
            ("body1", bodyLogical, "body1_lod0", null, null, 0L, null),
        };

        var done = AssetExporter.BuildRiggedGlbs(g.Root, vfs, new Outfit(0, "VesnaSSR01", OutfitKind.Base),
            "Vesna", spec, g.At("textures"), combinedOut: combined);

        Assert.Contains("weapon", done);
        Assert.True(File.Exists(lone) && File.Exists(combined));
        var joint = $"weapon_01_{weaponHash:x8}";
        var loneWorld = JointWorld(lone, joint);
        Assert.True(loneWorld.Translation.Length() < 1e-5f,
            $"lone joint world translation: {loneWorld.Translation}");   // the mount is NOT applied
        Assert.Equal(loneWorld, JointWorld(combined, joint));
        // and the geometry is the raw bind-space bytes the compile round trips, on both
        Assert.Equal(MeshGltf.ImportGlb(combined, "weapon_lod0").Channels["Vertex"],
                     MeshGltf.ImportGlb(lone).Channels["Vertex"]);
        // the lone armature spans the SUBJECT: the body's bones stand in the weapon's rig too, so weight can
        // be painted onto them there — as zero-weighted joints appended AFTER the weapon's own bone, which
        // keeps that bone on the index its geometry names while the body's bones still import as bones
        var loneModel = SharpGLTF.Schema2.ModelRoot.Load(lone);
        foreach (var bodyBone in SessionBones)
            Assert.Contains(loneModel.LogicalNodes, n => n.Name == $"bone_{bodyBone:x8}");
        Assert.Equal(new[] { joint }.Concat(SessionBones.Select(b => $"bone_{b:x8}")).ToArray(),
            Enumerable.Range(0, loneModel.LogicalSkins.Single().JointsCount)
                      .Select(i => loneModel.LogicalSkins.Single().Joints[i].Name).ToArray());

        static Matrix4x4 JointWorld(string glb, string node) => SharpGLTF.Schema2.ModelRoot.Load(glb)
            .LogicalNodes.Single(n => n.Name == node).WorldMatrix;
    }

    /// <summary>A lone open reads every OTHER part of the outfit for its bones alone. Those reads are the
    /// only ones allowed to fail quietly: the part being opened decoded fine, so a sibling's bundle held by
    /// the game costs that sibling's bones, not the session. The alternative is an open that used to work
    /// refusing with the BUSY remedy over a file it never needed.</summary>
    [Fact]
    public void ALoneOpen_ASiblingsBundleLocked_OpensWithoutThatSiblingsBones()
    {
        using var g = new TempGame();
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        const string clothLogical = "ccccccccccccccccccccccccccccccc1.bundle";
        const string bodyLogical = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1.bundle";
        const string clothPhys = "33333333333333333333333333333333";
        const string bodyPhys = "44444444444444444444444444444444";
        var bodyFile = Path.Combine(abw, bodyPhys + ".bundle");
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, clothPhys + ".bundle"), "cloth1_lod0",
            SessionTri, SessionIdx, new[] { 33u }, bundleName: clothLogical);
        SyntheticBundle.BuildOneSkinnedMesh(bodyFile, "body1_lod0",
            SessionTri, SessionIdx, SessionBones, bundleName: bodyLogical);
        var vfs = TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (clothLogical, clothPhys), (bodyLogical, bodyPhys));
        // no combinedOut: the body row carries no GlbOut either, so it is read for its SKELETON alone
        List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)> Spec(string glbOut) => new()
        {
            ("cloth1", clothLogical, "cloth1_lod0", glbOut, null, 0L, null),
            ("body1", bodyLogical, "body1_lod0", null, null, 0L, null),
        };
        var outfit = new Outfit(0, "VesnaSSR01", OutfitKind.Base);

        // unlocked, the sibling's bones do stand in the lone part's rig — the fixture has something to lose
        var open = g.At(Path.Combine("meshes", "cloth1_lod0.glb"));
        AssetExporter.BuildRiggedGlbs(g.Root, vfs, outfit, "Vesna", Spec(open), g.At("textures"));
        Assert.All(SessionBones, h => Assert.Contains(SharpGLTF.Schema2.ModelRoot.Load(open).LogicalNodes,
            n => n.Name == $"bone_{h:x8}"));

        var busy = g.At(Path.Combine("meshes", "busy.glb"));
        var log = new ListLog();
        using (File.Open(bodyFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var done = AssetExporter.BuildRiggedGlbs(g.Root, vfs, outfit, "Vesna", Spec(busy), g.At("textures"), log);
            Assert.Equal(new[] { "cloth1" }, done.ToArray());          // the open completed
        }

        var model = SharpGLTF.Schema2.ModelRoot.Load(busy);
        Assert.Equal(new[] { "bone_00000021" },                        // its own bone, and only its own
            Enumerable.Range(0, model.LogicalSkins.Single().JointsCount)
                      .Select(i => model.LogicalSkins.Single().Joints[i].Name).ToArray());
        Assert.All(SessionBones, h => Assert.DoesNotContain(model.LogicalNodes, n => n.Name == $"bone_{h:x8}"));
        // and never silently: one line, naming the part whose bones are missing
        Assert.Single(log.Lines, l => l.Contains("body1") && l.Contains("the game is using its files"));
    }

    /// <summary>A synchronous progress sink: <see cref="Progress{T}"/> posts through the sync context, so a
    /// test asserting on the lines could read them before they arrive.</summary>
    private sealed class ListLog : IProgress<string>
    {
        public List<string> Lines { get; } = new();
        public void Report(string value) => Lines.Add(value);
    }

    // ---------------------------------------------------------------- the session's map list survives publish

    [Fact]
    public void PublishCombined_MovesTheMapSidecarOntoTheGlbItPublishes()
    {
        // The build writes the map sidecar beside whatever file it wrote — a TEMP path. Left there, the
        // published glb has no sidecar and every image in a send-back resolves as authored.
        using var g = new TempGame();
        var combined = g.At("_combined.glb");
        var tmp = combined + ".cafe.tmp";
        var fp = g.At("_combined.fingerprint");
        var map = WritePng(g.At("body_d.png"), 3);
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", map) }, h => Paths[h], tmp);
        Assert.True(File.Exists(PreviewMaps.SidecarPath(tmp)));   // written under the temp's name

        Assert.True(AssetExporter.PublishCombined(tmp, combined, fp, "fp-1"));

        Assert.True(File.Exists(PreviewMaps.SidecarPath(combined)));
        Assert.False(File.Exists(PreviewMaps.SidecarPath(tmp)));
        Assert.Contains(PreviewMaps.ReadSidecar(combined).Values,
                        e => e.Source == Path.GetFullPath(map));
    }

    [Fact]
    public void PublishCombined_MaplessBuild_ClearsAStaleSidecarAtTheDestination()
    {
        // A sidecar from an OLDER build resolves this glb's images against another build's origins. No
        // sidecar is the safe direction — everything reads authored, at the cost of a redundant copy.
        using var g = new TempGame();
        var combined = g.At("_combined.glb");
        var fp = g.At("_combined.fingerprint");
        File.WriteAllText(PreviewMaps.SidecarPath(combined), "{\"images\":[]}");
        var tmp = combined + ".f00d.tmp";
        File.WriteAllText(tmp, "FRESH");                     // a build that embedded no maps writes no sidecar

        Assert.True(AssetExporter.PublishCombined(tmp, combined, fp, "fp-1"));
        Assert.False(File.Exists(PreviewMaps.SidecarPath(combined)));
    }

    /// <summary>Moving an object between part collections re-assigns geometry, and the moved object keeps
    /// its material. The map it brings is stock for a DIFFERENT part of the same subject, so the identity
    /// check is scoped to every map the SESSION shipped — scoped to one part's own it matches nothing and
    /// the app writes a redundant copy as an authored donor.</summary>
    [Fact]
    public void ASiblingPartsStockMap_ResolvesAsVanilla_WhenTheCheckIsScopedToTheSession()
    {
        using var g = new TempGame();
        var mapA = WritePng(g.At("body_d.png"), 1);
        var mapB = WritePng(g.At("cloth_d.png"), 90);
        var combined = g.At("_combined.glb");

        // what the app handed Blender: each part with its own map, published from a temp build
        var tmp = combined + ".abcd.tmp";
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", mapA), RiggedPart("cloth1_lod0", mapB) }, h => Paths[h], tmp);
        Assert.True(AssetExporter.PublishCombined(tmp, combined, g.At("_combined.fingerprint"), "fp-1"));
        var sessionSidecar = File.ReadAllBytes(PreviewMaps.SidecarPath(combined));

        // after the two objects swapped part collections, body1 carries the material cloth1 arrived with
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", mapB), RiggedPart("cloth1_lod0", mapA) }, h => Paths[h], combined);
        File.WriteAllBytes(PreviewMaps.SidecarPath(combined), sessionSidecar);

        var maps = MeshGltf.ReadSubmeshMaps(combined, "body1_lod0");
        Assert.Equal(MapOrigin.Vanilla, maps[0].BaseColor.Origin);
        Assert.Equal(Path.GetFullPath(mapB), maps[0].BaseColor.StockPng);   // the sibling's stock map, kept stock

        // the control: scoped to body1 alone the same image matches nothing, and would ship as a donor
        var ownMapOnly = PreviewMaps.ReadSidecar(combined).Values
            .Where(e => e.Source == Path.GetFullPath(mapA))
            .ToDictionary(e => (e.Hash, e.Kind), e => e);
        Assert.Equal(MapOrigin.Authored,
            PreviewMaps.Resolve(PreviewMaps.ToPreview(mapB, MapKind.BaseColor), MapKind.BaseColor, ownMapOnly).Origin);
    }

    /// <summary>The per-part workspace glb a re-split writes carries the part's own preview materials and map
    /// record, over the STOCK maps it came back on. An edited part is never rebuilt on a later open, so this
    /// file is what opening that part alone hands Blender: without the maps it opens untextured, and without
    /// the record its next send-back reads every untouched map as authored and rebuilds an authored RMO over
    /// a dead emissive mask.</summary>
    [Fact]
    public void ResplitWorkspaceGlb_CarriesThePartsOwnMapsAndTheirRecord()
    {
        using var g = new TempGame();
        var map = WritePng(g.At("body_d.png"), 5);
        var rmo = WritePng(g.At("body_r.png"), 11);
        var combined = g.At("_combined.glb");
        var resplit = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(Triangle("body1_lod0"),
                new MeshSkin { BoneHashes = new[] { HRoot }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } },
                map, PerSubmesh: new (string?, string?, string?)[] { (map, null, rmo) }),
            RiggedPart("cloth1_lod0", map),
        }, h => Paths[h], combined);

        MeshGltf.ReexportPartGlb(combined, "body1_lod0", resplit);

        Assert.All(MeshGltf.ReadSubmeshMaps(resplit), m =>
        {
            Assert.Equal(MapOrigin.Vanilla, m.BaseColor.Origin);
            Assert.Equal(Path.GetFullPath(map), m.BaseColor.StockPng);
            Assert.Equal(MapOrigin.Vanilla, m.Rmo.Origin);
        });
        // the alpha source an authored RMO is rebuilt over survives the re-split
        Assert.Equal(Path.GetFullPath(rmo), PreviewMaps.ReadSubmeshRmoSources(resplit, "body1_lod0")[0]);
    }

    /// <summary>A send-back's AUTHORED maps have to survive the re-split too, or re-opening the part alone
    /// hands the modder the game textures their own work covers. The authored file wins the slot, and the
    /// stock map it replaced is not re-embedded there.</summary>
    [Fact]
    public void ResplitWorkspaceGlb_EmbedsTheAuthoredMapOverTheStockOneItReplaced()
    {
        using var g = new TempGame();
        var stock = WritePng(g.At("body_d.png"), 5);
        var authored = WritePng(g.At("textures/body_s0_base.png"), 200);
        var combined = g.At("_combined.glb");
        var resplit = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", stock) }, h => Paths[h], combined);

        MeshGltf.ReexportPartGlb(combined, "body1_lod0", resplit,
            authoredMaps: new (string?, string?, string?)[] { (authored, null, null) });

        // what the part now opens on is the modder's file, at its own pixels
        var embedded = MeshGltf.ReadSubmeshMaps(resplit)[0].BaseColor;
        Assert.Equal(MapOrigin.Authored, embedded.Origin);
        Assert.Equal(FirstPixel(File.ReadAllBytes(authored)), FirstPixel(embedded.AuthoredPng!));
        Assert.NotEqual(FirstPixel(File.ReadAllBytes(stock)), FirstPixel(embedded.AuthoredPng!));
    }

    /// <summary>The record beside a re-split names the authored maps it embedded, but never as something a
    /// return can classify AGAINST: an image reproducing one of them is the modder's work, and reading it as
    /// stock would drop that work and put the game texture back on the slot.</summary>
    [Fact]
    public void AnAuthoredMapTheResplitEmbedded_ComesBackAuthoredNotStock()
    {
        using var g = new TempGame();
        var stock = WritePng(g.At("body_d.png"), 5);
        var authored = WritePng(g.At("textures/body_s0_base.png"), 200);
        var combined = g.At("_combined.glb");
        var resplit = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", stock) }, h => Paths[h], combined);
        MeshGltf.ReexportPartGlb(combined, "body1_lod0", resplit,
            authoredMaps: new (string?, string?, string?)[] { (authored, null, null) });

        // the record exists and names the file, so the neutrals and the submesh RMO sources still have a home
        Assert.Contains(Path.GetFileName(authored), File.ReadAllText(PreviewMaps.SidecarPath(resplit)));
        // …and it settles nothing: the classifying read never sees it
        Assert.DoesNotContain(PreviewMaps.ReadSidecar(resplit).Values,
            e => e.Source == Path.GetFullPath(authored));
        Assert.Equal(MapOrigin.Authored, MeshGltf.ReadSubmeshMaps(resplit)[0].BaseColor.Origin);
    }

    /// <summary>The stock map an authored one replaced must not be re-embedded on that slot. It would come
    /// back byte-identical, classify as untouched, and the authored map would be gone with no record of it.
    /// </summary>
    [Fact]
    public void TheStockMapAnAuthoredOneReplaced_IsNotReEmbeddedBesideIt()
    {
        using var g = new TempGame();
        var stock = WritePng(g.At("body_d.png"), 5);
        var authored = WritePng(g.At("textures/body_s0_base.png"), 200);
        var combined = g.At("_combined.glb");
        var resplit = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", stock) }, h => Paths[h], combined);

        MeshGltf.ReexportPartGlb(combined, "body1_lod0", resplit,
            authoredMaps: new (string?, string?, string?)[] { (authored, null, null) });

        Assert.DoesNotContain(Path.GetFileName(stock), File.ReadAllText(PreviewMaps.SidecarPath(resplit)));
    }

    /// <summary>A slot the send-back authored nothing for keeps the stock map it came back on, so an
    /// albedo-only edit still re-opens with its real relief showing.</summary>
    [Fact]
    public void AResplitWithOneAuthoredSlot_KeepsTheStockMapsOnTheOthers()
    {
        using var g = new TempGame();
        var stock = WritePng(g.At("body_d.png"), 5);
        var stockRmo = WritePng(g.At("body_r.png"), 11);
        var authored = WritePng(g.At("textures/body_s0_base.png"), 200);
        var combined = g.At("_combined.glb");
        var resplit = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(Triangle("body1_lod0"),
                new MeshSkin { BoneHashes = new[] { HRoot }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } },
                stock, PerSubmesh: new (string?, string?, string?)[] { (stock, null, stockRmo) }),
        }, h => Paths[h], combined);

        MeshGltf.ReexportPartGlb(combined, "body1_lod0", resplit,
            authoredMaps: new (string?, string?, string?)[] { (authored, null, null) });

        var back = MeshGltf.ReadSubmeshMaps(resplit)[0];
        Assert.Equal(MapOrigin.Authored, back.BaseColor.Origin);
        Assert.Equal(MapOrigin.Vanilla, back.Rmo.Origin);
        Assert.Equal(Path.GetFullPath(stockRmo), back.Rmo.StockPng);
        // the mask source rides the record either way, so an authored RMO next time still finds its alpha
        Assert.Equal(Path.GetFullPath(stockRmo), PreviewMaps.ReadSubmeshRmoSources(resplit, "body1_lod0")[0]);
    }

    /// <summary>A PNG's top-left pixel. A base colour round-trips as a pure re-encode, so which PICTURE came
    /// back is checkable off one pixel of these flat test maps without depending on the encoder's bytes.
    /// </summary>
    private static Rgba32 FirstPixel(byte[] png)
    {
        using var img = Image.Load<Rgba32>(png);
        return img[0, 0];
    }

    /// <summary>Which part a stock map belongs to is the whole difference between "bound to its own map" and
    /// a deliberate sibling link, and on a combined session the record beside the session glb is the only
    /// place that ownership is written down — a part opened in a session has no sidecar of its own. Read per
    /// owner, the SAME image is a link on the part that never had it and untouched on the part that did.</summary>
    [Fact]
    public void ACombinedSessionsSiblingLink_ShipsTheSiblingsStockMap()
    {
        using var g = new TempGame();
        var bodyMap = WritePng(g.At("body_d.png"), 1);
        var clothMap = WritePng(g.At("cloth_d.png"), 90);
        var combined = g.At("_combined.glb");
        var textures = g.At("textures");

        // what the app handed Blender: each part on its own map
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", bodyMap), RiggedPart("cloth1_lod0", clothMap) }, h => Paths[h], combined);
        var sessionSidecar = File.ReadAllBytes(PreviewMaps.SidecarPath(combined));

        // what came back: body1's slot re-linked to the image cloth1 arrived on
        MeshGltf.ExportCombinedRiggedGlb(
            new[] { RiggedPart("body1_lod0", clothMap), RiggedPart("cloth1_lod0", clothMap) }, h => Paths[h], combined);
        File.WriteAllBytes(PreviewMaps.SidecarPath(combined), sessionSidecar);

        var linked = DonorTextureIntake.Collect(MeshGltf.ReadSubmeshMaps(combined, "body1_lod0"), textures,
            "body1", p => p, PreviewMaps.ReadOwnedStock(combined, "body1_lod0"));
        var row = Assert.Single(linked!);
        Assert.Equal(Path.GetFullPath(clothMap), row.Albedo);          // the sibling's PNG, referenced whole
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);

        // the control: cloth1 carries the same image and it is cloth1's own, so nothing ships
        Assert.Null(DonorTextureIntake.Collect(MeshGltf.ReadSubmeshMaps(combined, "cloth1_lod0"), textures,
            "cloth1", p => p, PreviewMaps.ReadOwnedStock(combined, "cloth1_lod0")));
    }

    /// <summary>A record written before the owner was recorded can name no owner. Ownership is then unknowable
    /// rather than empty: an empty answer would read every part's own map as a sibling link and ship the whole
    /// outfit's stock maps as donors.</summary>
    [Fact]
    public void AMapRecordWithNoOwner_LeavesOwnershipUnknowable()
    {
        using var g = new TempGame();
        var map = WritePng(g.At("body_d.png"), 7);
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", map) }, h => Paths[h], combined);
        var sidecar = PreviewMaps.SidecarPath(combined);
        File.WriteAllText(sidecar, File.ReadAllText(sidecar).Replace("\"owner\": \"body1_lod0\"", "\"owner\": \"\""));

        Assert.Null(PreviewMaps.ReadOwnedStock(combined, "body1_lod0"));
        Assert.Null(PreviewMaps.ReadOwnedStock(g.At("never_built.glb")));
    }

    /// <summary>A record that names owners but never the mesh asked for knows nothing about that part —
    /// a re-split writes ownership under the RETURNED mesh name, which a rename moves off the caller's.
    /// Unknowable, not empty: an empty answer reads every vanilla match as a sibling link and ships the
    /// part's own stock maps as donors.</summary>
    [Fact]
    public void AMapRecordThatNeverMentionsTheMesh_LeavesOwnershipUnknowable()
    {
        using var g = new TempGame();
        var map = WritePng(g.At("cloth_d.png"), 3);
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("cloth1_lod0", map) }, h => Paths[h], combined);

        Assert.NotNull(PreviewMaps.ReadOwnedStock(combined, "cloth1_lod0"));
        Assert.Null(PreviewMaps.ReadOwnedStock(combined, "body1_lod0"));
    }

    /// <summary>A record that DOES hold the mesh and owns no image for it answers with the empty set: it
    /// can say the part owns nothing, so every vanilla match on it really is a sibling link.</summary>
    [Fact]
    public void AMapRecordHoldingTheMeshAndOwningNothingForIt_AnswersEmpty()
    {
        using var g = new TempGame();
        var bodyRmo = WritePng(g.At("body_r.png"), 11);
        var clothMap = WritePng(g.At("cloth_d.png"), 90);
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            RiggedPart("body1_lod0", rmoPng: bodyRmo),
            RiggedPart("cloth1_lod0", clothMap),
        }, h => Paths[h], combined);
        // body1's images attributed elsewhere; its submesh row still names it
        var sidecar = PreviewMaps.SidecarPath(combined);
        File.WriteAllText(sidecar, File.ReadAllText(sidecar)
            .Replace("\"owner\": \"body1_lod0\"", "\"owner\": \"cloth1_lod0\""));

        Assert.Empty(PreviewMaps.ReadOwnedStock(combined, "body1_lod0")!);
    }

    /// <summary>Sidecar rows carry the name a part was EXPORTED under, so a mesh renamed in Blender comes
    /// back under a name no row matches. The two namespaces stay apart: the glb-INTERNAL read keys what
    /// came back, the SIDECAR read keys the export-time name, and the authored RMO's mask still comes off
    /// the stock map the export recorded rather than falling through to a zero alpha.</summary>
    [Fact]
    public void ARenameInBlender_LeavesTheAuthoredRmosMaskOnItsExportTimeRow()
    {
        using var g = new TempGame();
        var stockRmo = WritePng(g.At("body_r.png"), 11);
        var authoredRmo = WritePng(g.At("hand_r.png"), 77);
        var ws = g.At("body1_lod0.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", rmoPng: stockRmo) }, h => Paths[h], ws);
        var sidecar = File.ReadAllBytes(PreviewMaps.SidecarPath(ws));

        // what came back: the same part renamed, its RMO slot repainted
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0.001", rmoPng: authoredRmo) }, h => Paths[h], ws);
        File.WriteAllBytes(PreviewMaps.SidecarPath(ws), sidecar);

        Assert.Equal("body1_lod0.001", MeshGltf.MeshNames(ws)[0]);
        Assert.Equal(MapOrigin.Authored, MeshGltf.ReadSubmeshMaps(ws, null)[0].Rmo.Origin);
        Assert.Empty(PreviewMaps.ReadSubmeshRmoSources(ws, null));              // the returned name matches no row
        Assert.Equal(Path.GetFullPath(stockRmo), PreviewMaps.ReadSubmeshRmoSources(ws, "body1_lod0")[0]);
    }

    /// <summary>A combined session carries every part's submeshes in ONE file, so the stock RMO each was
    /// given is recorded per (mesh, submesh). Read for one part it answers that part's own map: an authored
    /// RMO wearing a sibling's emissive mask would ship wrong and say nothing.</summary>
    [Fact]
    public void ACombinedExport_RecordsEachPartsOwnRmoSource()
    {
        using var g = new TempGame();
        var bodyRmo = WritePng(g.At("body_r.png"), 11);
        var clothRmo = WritePng(g.At("cloth_r.png"), 44);
        var combined = g.At("_combined.glb");

        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            RiggedPart("body1_lod0", rmoPng: bodyRmo),
            RiggedPart("cloth1_lod0", rmoPng: clothRmo),
        }, h => Paths[h], combined);

        Assert.Equal(Path.GetFullPath(bodyRmo),
            Assert.Contains(0, PreviewMaps.ReadSubmeshRmoSources(combined, "body1_lod0")));
        Assert.Equal(Path.GetFullPath(clothRmo),
            Assert.Contains(0, PreviewMaps.ReadSubmeshRmoSources(combined, "cloth1_lod0")));
    }

    /// <summary>The map read and the RMO-source read are paired — one names a submesh's slots, the other the
    /// alpha those slots ship over — so with no mesh named they must key the SAME part. Keying the first part
    /// that happens to have recorded an RMO would pair one part's slots with another's emissive mask.</summary>
    [Fact]
    public void ANoNameRead_KeysTheGlbsFirstMesh_OnBothHalvesOfThePair()
    {
        using var g = new TempGame();
        var clothRmo = WritePng(g.At("cloth_r.png"), 44);
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            RiggedPart("body1_lod0"),                       // first, and carries no RMO
            RiggedPart("cloth1_lod0", rmoPng: clothRmo),
        }, h => Paths[h], combined);
        Assert.Equal("body1_lod0", MeshGltf.MeshNames(combined)[0]);

        Assert.Equal(MapOrigin.None, MeshGltf.ReadSubmeshMaps(combined)[0].Rmo.Origin);
        Assert.Empty(PreviewMaps.ReadSubmeshRmoSources(combined));
    }

    // ---------------------------------------------------------------- a lost map record is never silent

    /// <summary>A glb whose map record went missing resolves every image as authored — an untouched stock RMO
    /// included. Its emissive mask rides an alpha channel glTF cannot carry, so with nothing to rebuild the
    /// mask from it ships dead. The intake asks per submesh instead, and the answer the caller resolves off
    /// the part's own renderer puts the mask back whole.</summary>
    [Fact]
    public void AnAuthoredRmoWithNoRecordBehindIt_TakesItsMaskFromTheStockMapTheCallerNames()
    {
        using var g = new TempGame();
        var stockRmo = WritePng(g.At("body_r.png"), 11);
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", rmoPng: stockRmo) }, h => Paths[h], combined);
        File.Delete(PreviewMaps.SidecarPath(combined));               // the publish couldn't move it

        var maps = MeshGltf.ReadSubmeshMaps(combined, "body1_lod0");
        Assert.Equal(MapOrigin.Authored, maps[0].Rmo.Origin);         // no record: the stock map reads authored
        Assert.Empty(PreviewMaps.ReadSubmeshRmoSources(combined, "body1_lod0"));

        var asked = new List<int>();
        var row = Assert.Single(DonorTextureIntake.Collect(maps, g.At("textures"), "body1", p => p,
            null, i => { asked.Add(i); return stockRmo; })!);

        Assert.Equal(new[] { 0 }, asked.ToArray());                   // asked only where a mask had to be rebuilt
        AssertSamePixels(stockRmo, row.Rmo!);

        // the control: with nothing to answer, the same send ships the mask as a dead zero
        var blank = Assert.Single(DonorTextureIntake.Collect(maps, g.At("textures"), "blank", p => p)!);
        using var shipped = Image.Load<Rgba32>(blank.Rmo!);
        Assert.Equal(0, shipped[0, 0].A);
    }

    /// <summary>The glb publishes whether or not its map record makes it across, so a record that did not is
    /// the only thing that can say the next send-back will read untouched maps as authored.</summary>
    [Fact]
    public void PublishCombined_ARecordThatCouldNotMove_SaysSo()
    {
        using var g = new TempGame();
        var combined = g.At("_combined.glb");
        var tmp = combined + ".cafe.tmp";
        MeshGltf.ExportCombinedRiggedGlb(new[] { RiggedPart("body1_lod0", WritePng(g.At("body_d.png"), 3)) },
            h => Paths[h], tmp);
        File.WriteAllText(PreviewMaps.SidecarPath(combined), "{\"images\":[]}");
        bool lost = false;

        // the destination record held open: the move fails and so does the clear that follows it
        using (File.Open(PreviewMaps.SidecarPath(combined), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.True(AssetExporter.PublishCombined(tmp, combined, g.At("_combined.fingerprint"), "fp-1",
                onMapSidecarLost: () => lost = true));

        Assert.True(lost);
        Assert.True(File.Exists(combined));   // the edit still publishes — the record is what was lost
    }

    /// <summary>A build that embedded no maps writes no record, so there is none to lose and nothing to
    /// report: the clear at the destination is routine, not a failure.</summary>
    [Fact]
    public void PublishCombined_ABuildThatWroteNoRecord_ReportsNothingLost()
    {
        using var g = new TempGame();
        var combined = g.At("_combined.glb");
        var tmp = combined + ".f00d.tmp";
        File.WriteAllText(tmp, "FRESH");
        File.WriteAllText(PreviewMaps.SidecarPath(combined), "{\"images\":[]}");
        bool lost = false;

        Assert.True(AssetExporter.PublishCombined(tmp, combined, g.At("_combined.fingerprint"), "fp-1",
            onMapSidecarLost: () => lost = true));

        Assert.False(lost);
        Assert.False(File.Exists(PreviewMaps.SidecarPath(combined)));
    }

    // ---------------------------------------------------------------- the send read is lenient

    /// <summary>A Blender export can carry accessors this app never reads — a shape key's, say — that strict
    /// schema validation rejects outright. Every other read of a returned glb skips validation for that
    /// reason, and the send read is the one that must: Send has already overwritten the workspace file, so a
    /// refusal here costs the modder the edit that is on disk.</summary>
    [Fact]
    public void ReadSend_AGlbCarryingAnAccessorWeNeverRead_StillReadsTheEditBack()
    {
        using var g = new TempGame();
        var glb = g.At("body1_lod0.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        AddUnreadAccessor(glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");

        Assert.Throws<SharpGLTF.Validation.SchemaException>(() => SharpGLTF.Schema2.ModelRoot.Load(glb));

        var edit = BlenderBridge.ReadSend(glb, "body1_lod0");

        Assert.Equal(3, edit.Mesh!.VertexCount);
    }

    // ---------------------------------------------------------------- what a send-back reports

    private static readonly string[] Nothing = Array.Empty<string>();

    private static MainWindowViewModel.SendBackMaps Maps(int authored = 0, int blanked = 0,
        params string[] notes) => new(authored, blanked, notes);

    [Fact]
    public void SendBackSummary_OnePartApplied_NamesIt()
    {
        Assert.Equal("Applied Blender edit to body1.",
            MainWindowViewModel.SendBackSummary(new[] { "body1" }, Nothing, Nothing, Maps()));
    }

    [Fact]
    public void SendBackSummary_SeveralPartsApplied_Counts()
    {
        // "authored", not "authored in Blender": the count also carries a sibling map linked in the shader
        // editor and an untouched own map with a texture edit behind it, neither of which Blender painted.
        Assert.Equal("Applied Blender edits to 3 parts. 2 maps authored.",
            MainWindowViewModel.SendBackSummary(new[] { "a", "b", "c" }, Nothing, Nothing, Maps(authored: 2)));
    }

    /// <summary>A blanked slot ships no file, so counting it as an authored map promises one that never
    /// arrives. The two are separate news on one line.</summary>
    [Fact]
    public void SendBackSummary_BlankedSlots_AreCountedApartFromAuthoredMaps()
    {
        Assert.Equal("Applied Blender edit to body1. 2 slots blanked.",
            MainWindowViewModel.SendBackSummary(new[] { "body1" }, Nothing, Nothing, Maps(blanked: 2)));
        Assert.Equal("Applied Blender edit to body1. 1 map authored · 1 slot blanked.",
            MainWindowViewModel.SendBackSummary(new[] { "body1" }, Nothing, Nothing, Maps(1, 1)));
    }

    /// <summary>A map the intake had to give up on is named in the same line — the status is written once
    /// per send-back, so anything said earlier is gone by the time this lands.</summary>
    [Fact]
    public void SendBackSummary_WhatTheIntakeGaveUpOn_IsSaidOnce()
    {
        Assert.Equal("Applied Blender edits to 2 parts. 1 map authored. Couldn't read body_r.png.",
            MainWindowViewModel.SendBackSummary(new[] { "a", "b" }, Nothing, Nothing,
                Maps(1, 0, "Couldn't read body_r.png.", "Couldn't read body_r.png.")));
    }

    /// <summary>The launch line is where a session with no map record says so, and it says it in terms of what
    /// the modder will see: untouched maps coming back as authored copies.</summary>
    [Fact]
    public void MapRecordLostNote_SaysWhatTheSessionCanNoLongerTell()
    {
        Assert.Equal(" No texture record for this session. Untouched maps come back as authored copies.",
            MainWindowViewModel.MapRecordLostNote(true));
        Assert.Equal("", MainWindowViewModel.MapRecordLostNote(false));
    }

    /// <summary>A glb whose record went missing is asked about directly, because a REUSED session never runs
    /// the publish that reports the loss. Without this the second open of a session proceeds silently and
    /// every map it sends back classifies as authored.</summary>
    [Fact]
    public void CombinedMapRecordMissing_IsTheReuseRoutesOwnAnswer()
    {
        using var g = new TempGame();
        var combined = g.At("_combined.glb");
        File.WriteAllText(combined, "GLB");

        Assert.True(AssetExporter.CombinedMapRecordMissing(combined));

        File.WriteAllText(PreviewMaps.SidecarPath(combined), "{\"images\":[]}");
        Assert.False(AssetExporter.CombinedMapRecordMissing(combined));
    }

    [Fact]
    public void SendBackSummary_AnEmptiedPart_ReadsAsHidden_NotAsAFailure()
    {
        // The point of the explicit emptied list: a part back with no mesh is a deliberate Hide, and must
        // never read as "couldn't read it back".
        Assert.Equal("Applied Blender edit to body1. Hidden in the mod: cloth2.",
            MainWindowViewModel.SendBackSummary(new[] { "body1" }, Nothing, new[] { "cloth2" }, Maps()));
    }

    [Fact]
    public void SendBackSummary_AFailedPart_IsNamedAgainstTheTotalThatCameBack()
    {
        Assert.Equal("Applied Blender edits to 1 of 2 parts. Couldn't read hair back.",
            MainWindowViewModel.SendBackSummary(new[] { "body1" }, new[] { "hair" }, Nothing, Maps()));
    }

    [Fact]
    public void SendBackSummary_AddedGeometry_IsCounted()
    {
        // added submeshes land as donor-named rows in the Edit tree, and nothing else there ties them to the
        // send that made them
        Assert.Equal("Applied Blender edit to body1. The send added 1 submesh.",
            MainWindowViewModel.SendBackSummary(new[] { "body1" }, Nothing, Nothing, Maps(), newSubmeshes: 1));
        Assert.Equal("Applied Blender edits to 2 parts. The send added 3 submeshes.",
            MainWindowViewModel.SendBackSummary(new[] { "a", "b" }, Nothing, Nothing, Maps(), newSubmeshes: 3));
    }

    [Fact]
    public void SendBackSummary_NoAddedGeometry_SaysNothingAboutSubmeshes() =>
        Assert.DoesNotContain("submesh",
            MainWindowViewModel.SendBackSummary(new[] { "body1" }, Nothing, Nothing, Maps(authored: 2)));

    [Fact]
    public void SendBackSummary_NothingMatched_SaysSo()
    {
        Assert.Equal("Nothing in the Blender send matched a part of this mod.",
            MainWindowViewModel.SendBackSummary(Nothing, Nothing, Nothing, Maps()));
    }

    /// <summary>A send-all hands back every writable part, so the count is of what the send-back TOOK. Twenty
    /// parts back with one edit among them is one applied edit, not twenty — and twenty back with no edit at
    /// all is a different answer from a send that matched no part of the mod.</summary>
    [Fact]
    public void SendBackSummary_CountsWhatWasTaken_NotWhatCameBack()
    {
        Assert.Equal("Applied Blender edit to body1.",
            MainWindowViewModel.SendBackSummary(new[] { "body1" }, Nothing, Nothing, Maps(), leftAlone: 19));
        Assert.Equal("Nothing changed in the Blender send.",
            MainWindowViewModel.SendBackSummary(Nothing, Nothing, Nothing, Maps(), leftAlone: 20));
    }

    /// <summary>A hide IS news, so it speaks for the send rather than being prefixed by "nothing
    /// changed".</summary>
    [Fact]
    public void SendBackSummary_UnchangedPartsBesideAHide_SayOnlyTheHide()
    {
        Assert.Equal("Hidden in the mod: cloth2.",
            MainWindowViewModel.SendBackSummary(Nothing, Nothing, new[] { "cloth2" }, Maps(), leftAlone: 5));
    }

    /// <summary>The count the summary reads is the BUILD's blanked rule, not the explicit gesture alone: a
    /// Replace submesh that authored something and named no normal/RMO ships those flat, and the cards and
    /// chips both say so. A summary counting only the gesture would leave the common case unannounced.</summary>
    [Fact]
    public void BlankedSlotCount_CountsEveryBlankTheBuildWillShip()
    {
        // albedo only: the two relief slots it named no file for go flat
        Assert.Equal(2, MainWindowViewModel.BlankedSlotCount(new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "textures/body_s0_base.png" },
        }));
        // the explicit gesture is still counted, and its asking pulls the file-less normal flat beside it
        Assert.Equal(2, MainWindowViewModel.BlankedSlotCount(new List<SubmeshTextures>
        {
            new() { Submesh = 0, RmoOrigin = SlotOrigin.ExplicitNeutral },
        }));
        // no flat albedo exists, so a neutral-plugged albedo is not one of them
        Assert.Equal(2, MainWindowViewModel.BlankedSlotCount(new List<SubmeshTextures>
        {
            new() { Submesh = 0, AlbedoOrigin = SlotOrigin.ExplicitNeutral },
        }));
        // a submesh that asked for nothing at all inherits every slot
        Assert.Equal(0, MainWindowViewModel.BlankedSlotCount(new List<SubmeshTextures>
        {
            new() { Submesh = 0 },
        }));
        Assert.Equal(0, MainWindowViewModel.BlankedSlotCount(null));
        // and the count runs across every row of the send
        Assert.Equal(3, MainWindowViewModel.BlankedSlotCount(new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "textures/body_s0_base.png" },
            new() { Submesh = 1, Albedo = "textures/body_s1_base.png", Normal = "textures/body_s1_nrm.png" },
        }));
    }

    /// <summary>A combined send reports one total, so each part's asks roll into the session's.</summary>
    [Fact]
    public void SendBackMaps_RollUpAcrossParts()
    {
        var total = Maps(1, 2, "first") + Maps(3, 0, "second");

        Assert.Equal(4, total.Authored);
        Assert.Equal(2, total.Blanked);
        Assert.Equal(new[] { "first", "second" }, total.Notes);
    }

    // ---------------------------------------------------------------- fixtures

    private const uint HRoot = 0x1111_1111;
    private static readonly Dictionary<uint, string> Paths = new() { [HRoot] = "root" };

    /// <summary>A prefab mount offset: a pure translation, which the rest bake refuses (baking a float
    /// translation into vertex data breaks the bit-exact round trip), so it reaches the session as a scene
    /// rest rather than an uprighting.</summary>
    private static readonly Matrix4x4 Mount = Matrix4x4.CreateTranslation(0, 0.5f, 2);

    /// <summary>One bone under one connector, rested at <see cref="Mount"/>. The connector's recorded rest is
    /// bind-normalized by inverse(measured G), which for a bone whose bind pose is identity is the mount
    /// itself — so the stored rest is identity and composing G back recovers the scene world.</summary>
    private static SceneRig MountedRig() => new()
    {
        BonePaths = new[] { "hips/weapon_01" },
        BoneRestWorlds = new[] { Mount },
        MeasuredRest = Mount,
        ConnectorRests = new Dictionary<string, Matrix4x4> { ["hips"] = Matrix4x4.Identity },
    };

    private static UnityMesh Triangle(string name) => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1 },
            ["BlendIndices"] = new float[12],
            ["BlendWeight"] = new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
        },
        Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };

    private static MeshGltf.RiggedPart RiggedPart(string name, string? baseColorPng = null, string? rmoPng = null) =>
        new(Triangle(name),
            new MeshSkin { BoneHashes = new[] { HRoot }, BindPoses = new List<Matrix4x4> { Matrix4x4.Identity } },
            baseColorPng,
            PerSubmesh: rmoPng is null ? null : new (string?, string?, string?)[] { (null, null, rmoPng) });

    /// <summary>A deterministic non-uniform image, so two fixtures' maps can never hash alike.</summary>
    private static string WritePng(string path, int seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var img = new Image<Rgba32>(8, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                img[x, y] = new Rgba32((byte)(x * 31 + seed), (byte)(y * 17 + seed), (byte)(x * y + seed), (byte)(200 + x));
        img.SaveAsPng(path);
        return path;
    }

    /// <summary>Pixel-for-pixel equality, alpha included — the channel an emissive mask rides in, and the one
    /// a comparison of visible colour would miss losing.</summary>
    private static void AssertSamePixels(string expectedPath, string actualPath)
    {
        using var expected = Image.Load<Rgba32>(expectedPath);
        using var actual = Image.Load<Rgba32>(actualPath);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.Equal(expected[x, y], actual[x, y]);
    }

    /// <summary>Give a glb an accessor with no bufferView, referenced by nothing — the shape strict schema
    /// validation refuses and every read this app makes of a returned glb ignores. The JSON chunk is rewritten
    /// in place, so the binary chunk and every existing index survive untouched.</summary>
    private static void AddUnreadAccessor(string glbPath)
    {
        var glb = File.ReadAllBytes(glbPath);
        int jsonLength = BitConverter.ToInt32(glb, 12);
        var json = System.Text.Encoding.UTF8.GetString(glb, 20, jsonLength)
            .Replace("\"accessors\":[", "\"accessors\":[{\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"},")
            .Replace("\"POSITION\":0", "\"POSITION\":1")
            .Replace("\"TEXCOORD_0\":1", "\"TEXCOORD_0\":2")
            .Replace("\"indices\":2", "\"indices\":3");

        var text = System.Text.Encoding.UTF8.GetBytes(json);
        var chunk = new byte[text.Length + (4 - text.Length % 4) % 4];
        Array.Fill(chunk, (byte)' ');                                  // chunks pad to 4 bytes, JSON with spaces
        text.CopyTo(chunk, 0);
        var binary = glb[(20 + jsonLength)..];

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u);                                          // "glTF"
        w.Write(2u);
        w.Write(12 + 8 + chunk.Length + binary.Length);
        w.Write(chunk.Length);
        w.Write(0x4E4F534Au);                                          // "JSON"
        w.Write(chunk);
        w.Write(binary);
        w.Flush();
        File.WriteAllBytes(glbPath, ms.ToArray());
    }
}
