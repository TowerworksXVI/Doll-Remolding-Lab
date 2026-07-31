using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Remold.Core.Mesh;
using Remold.Core.Skeleton;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// SceneRig.FromScene — the pure half of reading a prefab body's real skeleton. Two outputs matter:
/// (1) BonePaths anchored where the mesh's own bone-name hashes verify — a Bip001 rig's hashes anchor at
///     "Bip001", which the corpus BoneTable can't resolve at all;
/// (2) Uprighting = the snapped bind→scene rotation, present EXACTLY when the mesh ships lying down.
/// The scene reproduces the measured shape: a wrapper, a "Bip001" whose LOCAL rotation is the −90°-about-X
/// uprighting, and a bone chain under it.
/// </summary>
public class SceneRigTests
{
    private static readonly Matrix4x4 G = new(   // −90° about X, exact (the measured uprighting)
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    // wrapper(1) → Bip001(2, carries the uprighting rotation) → Pelvis(3) → Spine(4)
    private static List<SceneRig.SceneNode> Nodes() => new()
    {
        new(1, "c_Test_slg_skin", 0, Vector3.Zero, Quaternion.Identity, Vector3.One),
        new(2, "Bip001", 1, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2), Vector3.One),
        new(3, "Bip001_Pelvis", 2, new Vector3(0, 0.9f, 0), Quaternion.Identity, Vector3.One),
        new(4, "Bip001_Spine", 3, new Vector3(0, 0.3f, 0), Quaternion.Identity, Vector3.One),
    };

    /// <summary>Scene rest world, composed the way Unity does — deliberately mirroring the implementation's
    /// convention, which this pins.</summary>
    private static Matrix4x4 WorldOf(IReadOnlyList<SceneRig.SceneNode> nodes, long id)
    {
        var byId = nodes.ToDictionary(n => n.PathId);
        Matrix4x4 w = Matrix4x4.Identity;
        for (long cur = id; cur != 0 && byId.TryGetValue(cur, out var x); cur = x.Father)
        {
            var local = Matrix4x4.CreateScale(x.Scale)
                      * Matrix4x4.CreateFromQuaternion(x.Rot)
                      * Matrix4x4.CreateTranslation(x.Pos);
            w = w * local;   // accumulating child-out: w(local chain) — leaf-first composition
        }
        return w;
    }

    /// <summary>A skin whose bind poses satisfy <c>bind · sceneWorld = g</c> for the given bones —
    /// i.e. a mesh whose bind space differs from the scene rest by exactly <paramref name="g"/>.</summary>
    private static MeshSkin SkinFor(IReadOnlyList<SceneRig.SceneNode> nodes, long[] boneIds, Matrix4x4 g, uint[] hashes)
    {
        var binds = boneIds.Select(id =>
        {
            Matrix4x4.Invert(WorldOf(nodes, id), out var wInv);
            return g * wInv;
        }).ToList();
        return new MeshSkin { BoneHashes = hashes, BindPoses = binds };
    }

    private static uint[] Bip001Hashes => new[]
    {
        Skeleton.BoneTable.Hash("Bip001/Bip001_Pelvis"),
        Skeleton.BoneTable.Hash("Bip001/Bip001_Pelvis/Bip001_Spine"),
    };

    [Fact]
    public void LyingDownRig_YieldsSnappedUprighting_AndBip001AnchoredPaths()
    {
        var nodes = Nodes();
        var boneIds = new long[] { 3, 4 };
        var rig = SceneRig.FromScene(nodes, boneIds, SkinFor(nodes, boneIds, G, Bip001Hashes));

        Assert.NotNull(rig);
        Assert.Equal(G, rig!.Uprighting);   // snapped to the exact integer rotation
        // paths anchored at Bip001 — where the hashes verify — NOT at the wrapper and NOT flat
        Assert.Equal(new[] { "Bip001/Bip001_Pelvis", "Bip001/Bip001_Pelvis/Bip001_Spine" }, rig.BonePaths);
    }

    [Fact]
    public void ConnectorRests_PlaceUnskinnedAncestors_InBindSpace()
    {
        var nodes = Nodes();
        var boneIds = new long[] { 4 };   // ONLY the Spine is skinned — Bip001 and the Pelvis are connectors
        var hashes = new[] { Skeleton.BoneTable.Hash("Bip001/Bip001_Pelvis/Bip001_Spine") };
        var rig = SceneRig.FromScene(nodes, boneIds, SkinFor(nodes, boneIds, G, hashes));

        Assert.NotNull(rig);
        Assert.Equal(new[] { "Bip001", "Bip001/Bip001_Pelvis" }, rig!.ConnectorRests.Keys.OrderBy(k => k.Length));
        // bind space = scene world · inverse(G). Bip001's world IS the uprighting, so ≈ identity here.
        var bip = rig.ConnectorRests["Bip001"];
        Assert.True(bip.Translation.Length() < 1e-5f && Math.Abs(bip.M22 - 1f) < 1e-5f, $"Bip001 rest: {bip}");
        var pelvis = rig.ConnectorRests["Bip001/Bip001_Pelvis"];
        Assert.True(Vector3.Distance(new(0, 0.9f, 0), pelvis.Translation) < 1e-5f, $"Pelvis rest: {pelvis.Translation}");
    }

    /// <summary>A bind space the scene rest already agrees with derives NO placement of any kind: nothing to
    /// bake, and the measured relation a consumer reads back is the raw ≈identity it measured. Everything the
    /// export does with this rig is therefore the part's own bind rest, unmoved.</summary>
    [Fact]
    public void UprightRig_YieldsNullUprighting_PathsStillAnchored()
    {
        var nodes = Nodes();
        // flatten the Bip001 rotation → scene rest == bind space (the FB_Ironside / StrangeDoll case)
        nodes[1] = nodes[1] with { Rot = Quaternion.Identity };
        var boneIds = new long[] { 3, 4 };
        var rig = SceneRig.FromScene(nodes, boneIds, SkinFor(nodes, boneIds, Matrix4x4.Identity, Bip001Hashes));

        Assert.NotNull(rig);
        Assert.Null(rig!.Uprighting);
        // present and ≈identity: the raw relation is reported, and there is nothing in it to place by
        Assert.NotNull(rig.MeasuredRest);
        Assert.True(RestBake.RotationDiff(rig.MeasuredRest!.Value, Matrix4x4.Identity) < 1e-3f
                    && RestBake.TranslationDiff(rig.MeasuredRest.Value, Matrix4x4.Identity) < 1e-2f,
            $"measured rest: {rig.MeasuredRest.Value}");
        Assert.Equal("Bip001/Bip001_Pelvis", rig.BonePaths[0]);
    }

    /// <summary>The WEAPON case: bind space differs from the scene rest by a pure MOUNT TRANSLATION.
    /// Unbakeable — a float translation baked into vertex data breaks the bit-exact round trip — so it is no
    /// uprighting, and the part's own glb takes no placement from it at all. It survives as the raw measured
    /// rest, which is what a pool build restates binds through.</summary>
    [Fact]
    public void PureTranslationG_IsNoUprighting_AndSurvivesOnlyAsTheMeasuredRest()
    {
        var nodes = Nodes();
        nodes[1] = nodes[1] with { Rot = Quaternion.Identity };
        var boneIds = new long[] { 3, 4 };
        var mount = Matrix4x4.CreateTranslation(0.376f, 0, 0.05f);
        var rig = SceneRig.FromScene(nodes, boneIds, SkinFor(nodes, boneIds, mount, Bip001Hashes));

        Assert.NotNull(rig);
        Assert.Null(rig!.Uprighting);                                    // never baked into vertex data
        Assert.NotNull(rig.MeasuredRest);
        Assert.True(Vector3.Distance(new(0.376f, 0, 0.05f), rig.MeasuredRest!.Value.Translation) < 1e-4f,
            $"measured rest translation: {rig.MeasuredRest.Value.Translation}");

        // the lying-down case is the one that bakes
        var lying = SceneRig.FromScene(Nodes(), boneIds, SkinFor(Nodes(), boneIds, G, Bip001Hashes));
        Assert.NotNull(lying!.Uprighting);
    }

    [Fact]
    public void HashThatMatchesNoSuffix_FallsBackToTheFullChain()
    {
        var nodes = Nodes();
        var boneIds = new long[] { 3 };
        var rig = SceneRig.FromScene(nodes, boneIds,
            SkinFor(nodes, boneIds, Matrix4x4.Identity, new uint[] { 0xDEADBEEF }));

        Assert.NotNull(rig);
        Assert.Equal("c_Test_slg_skin/Bip001/Bip001_Pelvis", rig!.BonePaths[0]);   // real parenting preserved
    }

    [Fact]
    public void InconsistentBindToScene_MeansNoBake_ButPathsSurvive()
    {
        var nodes = Nodes();
        var boneIds = new long[] { 3, 4 };
        var skin = SkinFor(nodes, boneIds, G, Bip001Hashes);
        // corrupt one bind pose so G differs grossly across bones — not rigidly related, must not bake
        var binds = skin.BindPoses.ToList();
        binds[1] = Matrix4x4.CreateRotationY(1.2f) * binds[1];
        var rig = SceneRig.FromScene(nodes, boneIds, new MeshSkin { BoneHashes = skin.BoneHashes, BindPoses = binds });

        Assert.NotNull(rig);
        Assert.Null(rig!.Uprighting);
        Assert.Equal(2, rig.BonePaths.Count);
    }

    [Fact]
    public void MissingBoneTransform_OrCountMismatch_YieldsNoRig()
    {
        var nodes = Nodes();
        var skin = SkinFor(nodes, new long[] { 3, 4 }, Matrix4x4.Identity, Bip001Hashes);
        Assert.Null(SceneRig.FromScene(nodes, new long[] { 3, 999 }, skin));   // bone id not in the scene
        Assert.Null(SceneRig.FromScene(nodes, new long[] { 3 }, skin));        // 1 bone id vs 2-bone skin
    }
}
