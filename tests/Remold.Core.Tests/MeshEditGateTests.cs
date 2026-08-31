using System;
using System.IO;
using Remold.Core.Migoto;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The memoized per-install mesh-edit gate: one bundle read settles a mesh's answer for the ② Edit verbs,
/// the Blender session's writability and the Build plan alike — and a bundle that cannot be read RIGHT NOW
/// settles nothing, so the game holding a file never turns into a permanent "this mesh is fine".
/// </summary>
public class MeshEditGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-mesheditgate-" + Guid.NewGuid().ToString("N"));

    public MeshEditGateTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly float[] TriPositions = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
    private static readonly int[] TriIndices = { 0, 1, 2 };
    private static readonly uint[] Bones = { 11u, 22u, 33u };

    [Fact]
    public void ABlendshapedMeshAnswersBlockedAndTheReadRunsOnce()
    {
        string file = Path.Combine(_root, "face.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(file, "face", TriPositions, TriIndices, Bones, blendShapes: 9);
        int reads = 0;
        var gate = new MeshEditGate(_ => { reads++; return File.ReadAllBytes(file); });

        Assert.Equal(StreamDump.SkinRefusal.BlendShapes, gate.Blocked("b_face", "face"));
        Assert.Equal(StreamDump.SkinRefusal.BlendShapes, gate.Blocked("b_face", "face"));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void AHealthyMeshAnswersClearAndTheReadRunsOnce()
    {
        string file = Path.Combine(_root, "body.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(file, "body", TriPositions, TriIndices, Bones);
        int reads = 0;
        var gate = new MeshEditGate(_ => { reads++; return File.ReadAllBytes(file); });

        Assert.Null(gate.Blocked("b_body", "body"));
        Assert.Null(gate.Blocked("b_body", "body"));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void AnUnreadableBundleIsNotSettledAndTheNextAskRetries()
    {
        // The game holding the file is a fact about NOW, not about the mesh: the clear answer it forces
        // must not memoize, or a face asked about once during play would pass the gate until a rescan.
        string file = Path.Combine(_root, "face2.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(file, "face2", TriPositions, TriIndices, Bones, blendShapes: 5);
        bool locked = true;
        var gate = new MeshEditGate(_ => locked ? null : File.ReadAllBytes(file));

        Assert.Null(gate.Blocked("b_face2", "face2"));
        locked = false;
        Assert.Equal(StreamDump.SkinRefusal.BlendShapes, gate.Blocked("b_face2", "face2"));
    }

    [Fact]
    public void AThrowingReadAnswersClearWithoutSettling()
    {
        string file = Path.Combine(_root, "face3.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(file, "face3", TriPositions, TriIndices, Bones, blendShapes: 5);
        bool locked = true;
        var gate = new MeshEditGate(_ => locked
            ? throw new IOException("the game is using this file") : File.ReadAllBytes(file));

        Assert.Null(gate.Blocked("b_face3", "face3"));
        locked = false;
        Assert.Equal(StreamDump.SkinRefusal.BlendShapes, gate.Blocked("b_face3", "face3"));
    }

    [Fact]
    public void AThrowingParseAnswersClearWithoutSettling()
    {
        string file = Path.Combine(_root, "face4.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(file, "face4", TriPositions, TriIndices, Bones, blendShapes: 5);
        bool corrupt = true;
        int reads = 0;
        var gate = new MeshEditGate(_ =>
        {
            reads++;
            return corrupt ? new byte[] { 0x13, 0x37, 0x42 } : File.ReadAllBytes(file);
        });

        Assert.Null(gate.Blocked("b_face4", "face4"));
        corrupt = false;
        Assert.Equal(StreamDump.SkinRefusal.BlendShapes, gate.Blocked("b_face4", "face4"));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void TwoMeshesInOneBundleSettleSeparately()
    {
        string face = Path.Combine(_root, "pairface.bundle");
        string body = Path.Combine(_root, "pairbody.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(face, "face", TriPositions, TriIndices, Bones, blendShapes: 3);
        SyntheticBundle.BuildOneSkinnedMesh(body, "body", TriPositions, TriIndices, Bones);
        var gate = new MeshEditGate(id => id == "b_face" ? File.ReadAllBytes(face)
            : id == "b_body" ? File.ReadAllBytes(body) : null);

        Assert.Equal(StreamDump.SkinRefusal.BlendShapes, gate.Blocked("b_face", "face"));
        Assert.Null(gate.Blocked("b_body", "body"));
    }

    [Fact]
    public void TheSentencesNameEachRefusalInTheUsersWords()
    {
        // The Edit page's one short sentence is the disabled opens' hover reason and the refused click's
        // status line; the plan's fragment states the mesh fact the ③ Blocked box renders beside the
        // edit. Neither may leak the internal vocabulary.
        foreach (var refusal in new[] { StreamDump.SkinRefusal.BlendShapes,
                     StreamDump.SkinRefusal.SpringRig, StreamDump.SkinRefusal.SkinLayout })
        {
            Assert.Contains("cannot be edited in Blender", PartSkinGate.EditRefusal(refusal));
            Assert.Single(PartSkinGate.EditRefusal(refusal).TrimEnd('.').Split('.'));
            Assert.DoesNotContain("blend shape", PartSkinGate.EditRefusal(refusal));
            Assert.DoesNotContain("LBS", PartSkinGate.PlanRefusal(refusal));
        }
        Assert.Contains("expressions", PartSkinGate.EditRefusal(StreamDump.SkinRefusal.BlendShapes));
        Assert.Contains("expressions", PartSkinGate.PlanRefusal(StreamDump.SkinRefusal.BlendShapes));
        Assert.Contains("spring bones", PartSkinGate.PlanRefusal(StreamDump.SkinRefusal.SpringRig));
    }
}
