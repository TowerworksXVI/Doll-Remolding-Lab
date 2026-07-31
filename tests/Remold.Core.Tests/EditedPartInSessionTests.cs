using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Remold.App.ViewModels;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Tests.Support;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Re-opening a part that already carries an edit must hand Blender THAT edit, not the game copy. The
/// session glb is a multi-part build over one union armature, so an edited part joins through its own
/// workspace glb, whose authored mesh and skin are read back out and unioned with everyone else's.
///
/// <para>These pin the codec half — what a re-open carries — without a game install.</para>
/// </summary>
public class EditedPartInSessionTests
{
    // ---------------------------------------------------------------- reading a workspace glb back

    [Fact]
    public void ReadRiggedGlb_RecoversTheAuthoredGeometryAndTheSkinThatPosesIt()
    {
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", yShift: 5f), TwoBoneSkin(), h => Paths[h], ws);

        var got = MeshGltf.ReadRiggedGlb(ws);

        Assert.NotNull(got);
        var (mesh, skin) = got!.Value;
        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(new[] { 5f, 6f, 5f }, Ys(mesh));                       // the authored positions, Unity space
        Assert.Equal(new[] { HRoot, HArm }, skin.BoneHashes.ToArray());     // named by the joint nodes' hashes
        Assert.True(skin.IsSkinned);
        // the bind poses round-trip through the node rest worlds the export posed them at
        Assert.True(Close(skin.BindPoses[1], Matrix4x4.CreateTranslation(0, -1, 0)));
        // the per-vertex skin crosses as ordinary channels, so an edited part reads like a stock one
        Assert.Equal(4, mesh.Dims["BlendIndices"]);
        Assert.Equal(4, mesh.Dims["BlendWeight"]);
        Assert.Equal(1f, mesh.Channels["BlendIndices"][4]);                 // vertex 1 rides bone 1
        Assert.Equal(1f, mesh.Channels["BlendWeight"][4]);
    }

    [Fact]
    public void ReadRiggedGlb_AGeometryOnlyGlb_ReadsAsNothingToCombine()
    {
        // A file with no skin cannot join a union armature, so the caller opens the game copy for that part
        // and SAYS so, rather than dropping it out of the session.
        using var g = new TempGame();
        var ws = g.At("prop.glb");
        MeshGltf.ExportGlb(Part("prop", 0f), ws);

        Assert.Null(MeshGltf.ReadRiggedGlb(ws));
    }

    // ---------------------------------------------------------------- what a re-open hands Blender

    [Fact]
    public void ReOpeningAnEditedPart_CarriesTheEditedGeometry_NotTheGameCopy()
    {
        using var g = new TempGame();
        var vanilla = Part("cloth1_lod0", yShift: 0f);
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", yShift: 5f), TwoBoneSkin(), h => Paths[h], ws);   // the edit
        var edited = MeshGltf.ReadRiggedGlb(ws)!.Value;

        // the session build: one part from the game, the edited one from its workspace glb
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(Part("body1_lod0", 0f), TwoBoneSkin()),
            new MeshGltf.RiggedPart(edited.Mesh, edited.Skin),
        }, h => Paths[h], combined);

        var back = MeshGltf.ImportGlb(combined, "cloth1_lod0");
        Assert.Equal(new[] { 5f, 6f, 5f }, Ys(back));                       // the modder's geometry
        Assert.NotEqual(Ys(vanilla), Ys(back));                             // …and not the game's
        Assert.Equal(Ys(MeshGltf.ImportGlb(ws)), Ys(back));                 // exactly what the workspace glb holds
        Assert.Equal(new[] { 0f, 1f, 0f }, Ys(MeshGltf.ImportGlb(combined, "body1_lod0")));   // the stock part is stock
    }

    [Fact]
    public void ReOpeningAnEditedPart_KeepsEveryInfluenceItWasPaintedWith()
    {
        // Including a bone the part's own game mesh never had: the session offers the whole skeleton, so a
        // weight painted onto another part's bone must survive the re-open or the next send drops it.
        using var g = new TempGame();
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", 5f, thirdBone: true), ThreeBoneSkin(), h => Paths[h], ws);
        var edited = MeshGltf.ReadRiggedGlb(ws)!.Value;

        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(Part("body1_lod0", 0f), TwoBoneSkin()),
            new MeshGltf.RiggedPart(edited.Mesh, edited.Skin),
        }, h => Paths[h], combined);

        var payload = MeshGltf.ImportPayload(combined, "cloth1_lod0");
        Assert.True(payload.HasSkin);
        // each vertex still resolves to the bone it was painted to, by hash
        Assert.Equal(new[] { HRoot, HArm, HExtra }, Enumerable.Range(0, 3)
            .Select(v => payload.SkinJointHashes![payload.JointIndices![v * 4]]).ToArray());
        Assert.All(Enumerable.Range(0, 3), v => Assert.Equal(1f, payload.JointWeights![v * 4]));
    }

    [Fact]
    public void AnEditedPartsMapsStillResolveAsStock()
    {
        // A workspace glb is geometry-only, so an edited part's preview maps come from the same workspace
        // PNGs a stock part's do. Without them every map reads as authored on the next send and ships a
        // redundant copy of an untouched image.
        using var g = new TempGame();
        var map = WritePng(g.At("cloth_d.png"));
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", 5f), TwoBoneSkin(), h => Paths[h], ws);
        Assert.All(MeshGltf.ReadSubmeshMaps(ws), m => Assert.Equal(MapOrigin.None, m.BaseColor.Origin));
        var edited = MeshGltf.ReadRiggedGlb(ws)!.Value;

        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(edited.Mesh, edited.Skin, BaseColorPng: map),
        }, h => Paths[h], combined);

        var maps = MeshGltf.ReadSubmeshMaps(combined, "cloth1_lod0");
        Assert.Equal(MapOrigin.Vanilla, maps[0].BaseColor.Origin);
        Assert.Equal(Path.GetFullPath(map), maps[0].BaseColor.StockPng);
    }

    [Fact]
    public void ReSplittingACombinedSendBack_WithConnectors_StillReadsBackAsTheEdit()
    {
        // The full round: combined open -> send-back -> per-part re-split -> re-open. A connector is
        // hierarchy glue, not a skinned bone; leaked into the workspace glb's SKIN the joint has no
        // recoverable bone hash, the next open refuses the file, and the edit is silently replaced.
        using var g = new TempGame();
        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(Part("cloth1_lod0", yShift: 5f), TwoBoneSkin(),
                ScenePaths: new[] { "hips/root", "hips/root/arm" },
                ConnectorRests: new Dictionary<string, Matrix4x4> { ["hips"] = Matrix4x4.CreateTranslation(0, 3, 0) }),
        }, h => Paths[h], combined);

        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ReexportPartGlb(combined, "cloth1_lod0", ws);

        var got = MeshGltf.ReadRiggedGlb(ws);
        Assert.NotNull(got);                                                // NOT the game-fallback signal
        Assert.Equal(new[] { 5f, 6f, 5f }, Ys(got!.Value.Mesh));            // the edit survived the round
        Assert.Equal(new[] { HRoot, HArm }, got.Value.Skin.BoneHashes.ToArray());
    }

    [Fact]
    public void ReOpeningAnEditedPart_WhenTheBoneTableNamesNothing_SharesTheStockPartsJoints()
    {
        // The Bip001 subject: the corpus bone table resolves NONE of its hashes (`_ => null`), so every
        // joint's name comes from the parts' scene rigs. A workspace glb carries hashes only — named from
        // what the subject's rigs say it lands on the stock part's joints; named from nothing every one of
        // its bones hangs off the armature as a flat `bone_<hash>` twin of a bone that is already there.
        using var g = new TempGame();

        // the edit's workspace glb holds the SESSION armature, so its skin carries a bone the part's own
        // game mesh never had
        var ws = g.At("cloth1_lod0.glb");
        MeshGltf.ExportRiggedGlb(Part("cloth1_lod0", 5f, thirdBone: true), ThreeBoneSkin(), _ => null, ws,
            scenePaths: new[] { BipRoot, BipPelvis, BipSpine });
        var edited = MeshGltf.ReadRiggedGlb(ws)!.Value;

        // what the subject's parts collectively say: the edited part's OWN game rig covers two of the three
        // bones, and the stock part's rig is the only source for the third
        var partRigs = new (MeshSkin Skin, IReadOnlyList<string>? BonePaths)[]
        {
            (TwoBoneSkin(), new[] { BipRoot, BipPelvis }),
            (ThreeBoneSkin(), new[] { BipRoot, BipPelvis, BipSpine }),
        };

        var combined = g.At("_combined.glb");
        MeshGltf.ExportCombinedRiggedGlb(new[]
        {
            new MeshGltf.RiggedPart(Part("body1_lod0", 0f, thirdBone: true), ThreeBoneSkin(),
                ScenePaths: new[] { BipRoot, BipPelvis, BipSpine }),
            new MeshGltf.RiggedPart(edited.Mesh, edited.Skin,
                ScenePaths: AssetExporter.EditedScenePaths(edited.Skin, partRigs)),
        }, _ => null, combined);

        var model = ModelRoot.Load(combined);
        var skin = Assert.Single(model.LogicalSkins);
        var jointNames = Enumerable.Range(0, skin.JointsCount)
            .Select(i => skin.GetJoint(i).Joint.Name).ToArray();

        // (i) no joint degraded to a flat hash node
        Assert.DoesNotContain(jointNames, n => Regex.IsMatch(n, "^bone_[0-9a-f]{8}$"));
        // (ii) one joint per bone — not the stock part's named joint PLUS the edited part's hash twin
        foreach (var h in new[] { HRoot, HArm, HExtra })
            Assert.Single(jointNames, n => n.EndsWith($"_{h:x8}", StringComparison.Ordinal));
        // (iii) a shared bone is the SAME union joint for both parts, so a weight means the same thing
        var jStock = MeshNamed(model, "body1_lod0").Primitives.First()
            .GetVertexAccessor("JOINTS_0").AsVector4Array();
        var jEdited = MeshNamed(model, "cloth1_lod0").Primitives.First()
            .GetVertexAccessor("JOINTS_0").AsVector4Array();
        foreach (var (v, h) in new[] { (0, HRoot), (1, HArm), (2, HExtra) })
        {
            Assert.Equal((int)jStock[v].X, (int)jEdited[v].X);
            Assert.EndsWith($"_{h:x8}", jointNames[(int)jEdited[v].X], StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- what the modder is told

    [Fact]
    public void GameFallbackNote_SaysNothingWhenEveryEditCameThrough()
    {
        Assert.Equal("", MainWindowViewModel.GameFallbackNote(Array.Empty<string>()));
    }

    [Fact]
    public void GameFallbackNote_NamesEveryPartItHadToTakeFromTheGame()
    {
        Assert.Equal(" Couldn't read the edit for cloth1. It opened from the game.",
            MainWindowViewModel.GameFallbackNote(new[] { "cloth1" }));
        Assert.Equal(" Couldn't read the edits for cloth1, hair. They opened from the game.",
            MainWindowViewModel.GameFallbackNote(new[] { "cloth1", "hair" }));
    }

    // ---------------------------------------------------------------- fixtures

    private const uint HRoot = 0x1111_1111, HArm = 0x2222_2222, HExtra = 0x3333_3333;
    private static readonly Dictionary<uint, string> Paths =
        new() { [HRoot] = "root", [HArm] = "root/arm", [HExtra] = "root/extra" };

    // a Biped rig's paths: what a scene rig supplies for a subject whose bone table names nothing
    private const string BipRoot = "Bip001";
    private const string BipPelvis = "Bip001/Bip001 Pelvis";
    private const string BipSpine = "Bip001/Bip001 Pelvis/Bip001 Spine";

    private static SharpGLTF.Schema2.Mesh MeshNamed(ModelRoot m, string name) =>
        m.LogicalMeshes.Single(x => x.Name == name);

    /// <summary>A 3-vertex part, each vertex on its own bone, lifted by <paramref name="yShift"/> so an
    /// "edited" copy is distinguishable by position alone.</summary>
    private static UnityMesh Part(string name, float yShift, bool thirdBone = false) => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, yShift, 0, 0.5f, yShift + 1, 0, 1, yShift, 0 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1 },
            ["BlendIndices"] = new float[] { 0, 0, 0, 0, 1, 0, 0, 0, thirdBone ? 2 : 0, 0, 0, 0 },
            ["BlendWeight"] = new[] { 1f, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 },
        },
        Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2, ["BlendIndices"] = 4, ["BlendWeight"] = 4 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };

    private static MeshSkin TwoBoneSkin() => new()
    {
        BoneHashes = new[] { HRoot, HArm },
        BindPoses = new List<Matrix4x4> { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0) },
    };

    private static MeshSkin ThreeBoneSkin() => new()
    {
        BoneHashes = new[] { HRoot, HArm, HExtra },
        BindPoses = new List<Matrix4x4>
        {
            Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, -1, 0), Matrix4x4.CreateTranslation(0, -2, 0),
        },
    };

    private static float[] Ys(UnityMesh m) =>
        Enumerable.Range(0, m.VertexCount).Select(v => m.Channels["Vertex"][v * 3 + 1]).ToArray();

    private static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        var d = a - b;
        return MathF.Abs(d.M11) < 1e-4f && MathF.Abs(d.M22) < 1e-4f && MathF.Abs(d.M33) < 1e-4f
            && MathF.Abs(d.M41) < 1e-4f && MathF.Abs(d.M42) < 1e-4f && MathF.Abs(d.M43) < 1e-4f;
    }

    private static string WritePng(string path)
    {
        using var img = new Image<Rgba32>(8, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                img[x, y] = new Rgba32((byte)(x * 31), (byte)(y * 17), (byte)(x * y), (byte)(200 + x));
        img.SaveAsPng(path);
        return path;
    }
}
