using System;
using System.IO;
using Remold.Core.Migoto;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The per-part answer the Edit pane's Open verb reads: which half of the recoverable-skin rule refuses a
/// part's game mesh, resolved from its bundle identity. The two branches are separate answers because they
/// get different user-facing text, and a mesh the read can't reach is NOT an answer at all.
/// </summary>
public class PartSkinGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-skingate-" + Guid.NewGuid().ToString("N"));

    public PartSkinGateTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly float[] TriPositions = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] TriIndices = { 0, 1, 2 };
    private static readonly uint[] Bones = { 11u, 22u, 33u };

    /// <summary>A one-bundle deobfuscate delegate over a file written by the fixture.</summary>
    private Func<string, byte[]?> Bundles(string logical, string file) =>
        id => id == logical ? File.ReadAllBytes(Path.Combine(_root, file)) : null;

    [Fact]
    public void AMeshWithBlendShapesIsRefusedOnTheBlendShapeBranch()
    {
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "face.bundle"), "face",
            TriPositions, TriIndices, Bones, blendShapes: 17);

        Assert.Equal(StreamDump.SkinRefusal.BlendShapes,
            PartSkinGate.Blocked(Bundles("b_face", "face.bundle"), "b_face", "face"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AMeshWithABelowFourSkinIsNotRefused(int skinWidth)
    {
        // The stored influences are the whole skin at any width, widened where they are read, so the
        // pooled swap takes these and the verb must be offered.
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, $"body{skinWidth}.bundle"), "body",
            TriPositions, TriIndices, Bones, skinWidth: skinWidth);

        Assert.Null(PartSkinGate.Blocked(Bundles("b_body", $"body{skinWidth}.bundle"), "b_body", "body"));
    }

    [Fact]
    public void AMeshWhoseSkinStreamCarriesAThirdChannelIsRefusedOnTheLayoutBranch()
    {
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "shared.bundle"), "shared",
            TriPositions, TriIndices, Bones, extraSkinChannel: true);

        Assert.Equal(StreamDump.SkinRefusal.SkinLayout,
            PartSkinGate.Blocked(Bundles("b_shared", "shared.bundle"), "b_shared", "shared"));
    }

    [Fact]
    public void AMeshWithAOneInfluenceSkinIsNotRefused()
    {
        // One stored influence is a whole skin, widened where it is read, so the pooled swap takes it and
        // the verb must be offered.
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "weapon.bundle"), "weapon",
            TriPositions, TriIndices, Bones, skinWidth: 1);

        Assert.Null(PartSkinGate.Blocked(Bundles("b_weapon", "weapon.bundle"), "b_weapon", "weapon"));
    }

    [Fact]
    public void AStaticMeshIsNotRefused()
    {
        SyntheticBundle.BuildOneMesh(Path.Combine(_root, "prop.bundle"), "prop", TriPositions, TriIndices);

        Assert.Null(PartSkinGate.Blocked(Bundles("b_prop", "prop.bundle"), "b_prop", "prop"));
    }

    [Fact]
    public void AMeshWithTheFullSkinStreamAndNoShapesIsNotRefused()
    {
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "hair.bundle"), "hair",
            TriPositions, TriIndices, Bones);

        Assert.Null(PartSkinGate.Blocked(Bundles("b_hair", "hair.bundle"), "b_hair", "hair"));
    }

    [Fact]
    public void AMeshRidingASpringChainIsRefusedOnTheSpringBranch()
    {
        // 0x05f0c65f = the Spring01 chain root. The skin itself is recoverable (full-width stream), so
        // this refusal can only be the bone-set rule.
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "charm.bundle"), "charm",
            TriPositions, TriIndices, new uint[] { 0x9e3779b9, 0x05f0c65f });

        Assert.Equal(StreamDump.SkinRefusal.SpringRig,
            PartSkinGate.Blocked(Bundles("b_charm", "charm.bundle"), "b_charm", "charm"));
    }

    [Fact]
    public void ASpringChainRefusalBeatsTheRouteAnswer()
    {
        // A one-influence spring mesh has a valid pooled route; the spring rule must still refuse it.
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "charm1.bundle"), "charm1",
            TriPositions, TriIndices, new uint[] { 0x2b587a92 }, skinWidth: 1);

        Assert.Equal(StreamDump.SkinRefusal.SpringRig,
            PartSkinGate.Blocked(Bundles("b_charm1", "charm1.bundle"), "b_charm1", "charm1"));
    }

    [Fact]
    public void AMeshTheReadCannotReachIsNotCalledUnreplaceable()
    {
        // An unreadable bundle and an absent mesh are separate failures with their own routes. Answering
        // "unreplaceable" for either would disable the verb over a read that never happened.
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "hair2.bundle"), "hair2",
            TriPositions, TriIndices, Bones);

        Assert.Null(PartSkinGate.Blocked(_ => null, "b_gone", "hair2"));
        Assert.Null(PartSkinGate.Blocked(Bundles("b_hair2", "hair2.bundle"), "b_hair2", "not_here"));
        Assert.Null(PartSkinGate.Blocked(_ => throw new IOException("locked"), "b_hair2", "hair2"));
    }

    [Fact]
    public void ACollapsedPointsMeshRefusesBlenderEditingButNotReplacement()
    {
        // Every triangle zero-area (all corners on one point): a billboard cloud a game shader inflates.
        // Blender cannot carry its authored normals, so the Blender-edit answer refuses — while Blocked
        // stays null, because REPLACEMENT (the drop route, the build) still works on such a mesh.
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "pearl.bundle"), "pearl",
            new float[] { 1, 2, 3, 1, 2, 3, 1, 2, 3 }, TriIndices, Bones);

        Assert.True(PartSkinGate.TryBlenderEditAnswers(Bundles("b_pearl", "pearl.bundle"), "b_pearl",
            "pearl", 0, out var refusal, out bool collapsed));
        Assert.Null(refusal);
        Assert.True(collapsed);
        Assert.Null(PartSkinGate.Blocked(Bundles("b_pearl", "pearl.bundle"), "b_pearl", "pearl"));
    }

    [Fact]
    public void ARefusalBeatsTheCollapsedQuestion_AndItsGeometryIsNeverConsulted()
    {
        // Spring-chain bone table over collapsed positions: the refusal answers the whole question, so
        // the geometry half stays false and nothing that read could throw can disturb the refusal.
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "springpearl.bundle"), "springpearl",
            new float[] { 1, 2, 3, 1, 2, 3, 1, 2, 3 }, TriIndices, new uint[] { 0x9e3779b9, 0x05f0c65f });

        Assert.True(PartSkinGate.TryBlenderEditAnswers(Bundles("b_sp", "springpearl.bundle"), "b_sp",
            "springpearl", 0, out var refusal, out bool collapsed));
        Assert.Equal(StreamDump.SkinRefusal.SpringRig, refusal);
        Assert.False(collapsed);
    }

    [Fact]
    public void AnOrdinaryMeshIsNotCalledCollapsed_AndAnUnreachableReadSettlesNothing()
    {
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "body_real.bundle"), "body_real",
            TriPositions, TriIndices, Bones);

        Assert.True(PartSkinGate.TryBlenderEditAnswers(Bundles("b_real", "body_real.bundle"), "b_real",
            "body_real", 0, out var refusal, out bool collapsed));
        Assert.Null(refusal);
        Assert.False(collapsed);

        // an unreadable bundle is not an answer: the caller must not memoize it
        Assert.False(PartSkinGate.TryBlenderEditAnswers(_ => null, "b_gone", "body_real", 0,
            out _, out bool unreadCollapsed));
        Assert.False(unreadCollapsed);
    }

    [Fact]
    public void TheBuildLogPhrasingComesFromTheSameAnswer()
    {
        // One rule, two renderings: the build log's sentence must not be able to drift from the branch the
        // pane reads.
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "face2.bundle"), "face2",
            TriPositions, TriIndices, Bones, blendShapes: 28);
        var field = new Bundles.BundleReader()
            .GetMeshField(File.ReadAllBytes(Path.Combine(_root, "face2.bundle")), "face2");

        Assert.NotNull(field);
        Assert.Equal(StreamDump.SkinRefusal.BlendShapes, StreamDump.UnrecoverableSkin(field!)?.Kind);
        Assert.Equal(28, StreamDump.UnrecoverableSkin(field!)?.BlendShapes);
        Assert.Contains("28 blend shapes", StreamDump.UnrecoverableSkinReason(field!));
    }
}
