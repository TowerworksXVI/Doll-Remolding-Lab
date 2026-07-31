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

    [Fact]
    public void AMeshWithAReducedSkinStreamIsRefusedOnTheLayoutBranch()
    {
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(_root, "body.bundle"), "body",
            TriPositions, TriIndices, Bones, skinWidth: 2);

        Assert.Equal(StreamDump.SkinRefusal.SkinLayout,
            PartSkinGate.Blocked(Bundles("b_body", "body.bundle"), "b_body", "body"));
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
