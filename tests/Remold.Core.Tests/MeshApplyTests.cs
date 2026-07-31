using System.Collections.Generic;
using Remold.Core.Mesh;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The pure mesh-compile logic on synthetic fixtures — no glb, no bundle. ONE path: the authored skin is
/// mapped onto the target's bone order by joint-name hash, with an unresolved-bone fallback to the original
/// weights, channel conform, and the 16-bit index overflow refusal. The type-tree write-back is not
/// asserted here.
/// </summary>
public class MeshApplyTests
{
    private static UnityMesh Orig() => new()
    {
        Name = "target",
        VertexCount = 2,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 },
            ["Normal"] = new float[] { 0, 1, 0, 0, 1, 0 },
            ["Color"] = new float[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            ["BlendWeight"] = new float[] { 1, 0, 0, 0, 0, 1, 0, 0 },
            ["BlendIndices"] = new float[] { 5, 0, 0, 0, 7, 0, 0, 0 },
        },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["Normal"] = 3, ["Color"] = 4, ["BlendWeight"] = 4, ["BlendIndices"] = 4 },
        Submeshes = new List<int[]> { new[] { 0, 1, 0 } },
    };

    private static UnityMesh Mesh(int vertexCount, Dictionary<string, float[]> channels,
        Dictionary<string, int> dims, List<int[]> submeshes) =>
        new() { VertexCount = vertexCount, Channels = channels, Dims = dims, Submeshes = submeshes };

    [Fact]
    public void Skinned_WeightHealth_FlagsLowWeightVertices()
    {
        // Weights summing below 0.5 collapse the vertex to the bind pose and must be flagged. The authored
        // joints resolve, so no fallback masks the low sum.
        var orig = Orig();
        var glb = new MeshApply.Payload
        {
            Mesh = Mesh(2,
                new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 } },
                new Dictionary<string, int> { ["Vertex"] = 3 },
                new List<int[]> { new[] { 0, 1, 0 } }),
            SkinJointHashes = new uint[] { 5, 7 },                              // resolve to the target bones
            JointIndices = new[] { 0, 0, 0, 0, /*v1*/ 1, 0, 0, 0 },
            JointWeights = new float[] { 1, 0, 0, 0, /*v1*/ 0.2f, 0, 0, 0 },    // vert1 under-weighted
        };

        var built = MeshApply.BuildSkinned(orig, glb, new uint[] { 5, 7 });

        Assert.Contains(built.Warnings, w => w.Contains("1 vertex") && w.Contains("bind pose"));
    }

    [Fact]
    public void Skinned_ResolvesGlbJointsToTargetBoneOrderByHash()
    {
        var orig = Orig();
        var targetHashes = new uint[] { 0xAAAAAAAA, 0xBBBBBBBB, 0xCCCCCCCC };
        // a full normal/tangent frame, so the outline bake runs cleanly and only bone warnings are possible
        var glb = new MeshApply.Payload
        {
            Mesh = Mesh(2,
                new Dictionary<string, float[]>
                {
                    ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 },
                    ["Normal"] = new float[] { 0, 1, 0, 0, 1, 0 },
                    ["Tangent"] = new float[] { 1, 0, 0, 1, 1, 0, 0, 1 },
                },
                new Dictionary<string, int> { ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4 },
                new List<int[]> { new[] { 0, 1, 0 } }),
            SkinJointHashes = new uint[] { 0xCCCCCCCC, 0xBBBBBBBB },
            JointIndices = new[] { 0, 1, 0, 0, /*v1*/ 1, 0, 0, 0 },
            JointWeights = new float[] { 0.7f, 0.3f, 0, 0, /*v1*/ 1, 0, 0, 0 },
        };

        var built = MeshApply.BuildSkinned(orig, glb, targetHashes);

        Assert.Equal(new float[] { 2, 1, 2, 2, 1, 2, 2, 2 }, built.Arrays["BlendIndices"]);
        Assert.Equal(new float[] { 0.7f, 0.3f, 0, 0, 1, 0, 0, 0 }, built.Arrays["BlendWeight"]);
        Assert.Empty(built.Warnings);
    }

    [Fact]
    public void Skinned_FallsBackToOriginalWeights_WhenAGlbBoneIsMissingFromTarget()
    {
        var orig = Orig();
        var targetHashes = new uint[] { 0xAAAAAAAA, 0xBBBBBBBB };   // 0xDDDD is NOT here
        var glb = new MeshApply.Payload
        {
            Mesh = Mesh(2,
                new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 } },
                new Dictionary<string, int> { ["Vertex"] = 3 },
                new List<int[]> { new[] { 0, 1, 0 } }),
            SkinJointHashes = new uint[] { 0xDDDDDDDD },            // unresolved
            JointIndices = new[] { 0, 0, 0, 0, 0, 0, 0, 0 },
            JointWeights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0 }, // real weight on the missing bone
        };

        var built = MeshApply.BuildSkinned(orig, glb, targetHashes);

        Assert.Equal(orig.Channels["BlendIndices"], built.Arrays["BlendIndices"]);
        Assert.Equal(orig.Channels["BlendWeight"], built.Arrays["BlendWeight"]);
        Assert.NotEmpty(built.Warnings);
    }

    [Fact]
    public void Skinned_ZeroWeightResolvedSlot_DoesNotMaskTheFallback()
    {
        // v0 carries ALL its weight on a missing bone plus a ZERO-weight slot on a valid one. The
        // zero-weight slot must not count as "resolved", or the compile drops v0's only real influence and
        // ships an all-zero skin.
        var orig = Orig();
        var targetHashes = new uint[] { 0xAAAAAAAA };
        var glb = new MeshApply.Payload
        {
            Mesh = Mesh(2,
                new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 } },
                new Dictionary<string, int> { ["Vertex"] = 3 },
                new List<int[]> { new[] { 0, 1, 0 } }),
            SkinJointHashes = new uint[] { 0xAAAAAAAA, 0xDDDDDDDD },   // D is not in the target
            JointIndices = new[] { 0, 1, 0, 0, /*v1*/ 0, 0, 0, 0 },
            JointWeights = new float[] { 0f, 1f, 0, 0, /*v1*/ 1, 0, 0, 0 },   // v0: valid w=0, missing w=1
        };

        var built = MeshApply.BuildSkinned(orig, glb, targetHashes);

        var bi = built.Arrays["BlendIndices"]; var bw = built.Arrays["BlendWeight"];
        Assert.Equal(new float[] { 5, 0, 0, 0 }, new[] { bi[0], bi[1], bi[2], bi[3] });   // v0: original skin
        Assert.Equal(new float[] { 1, 0, 0, 0 }, new[] { bw[0], bw[1], bw[2], bw[3] });
        Assert.Equal(0f, bi[4]);   // v1: authored, resolved to target index 0
        Assert.Equal(1f, bw[4]);
        Assert.Contains(built.Warnings, w => w.Contains("1 fell back to the original weights"));
    }

    [Fact]
    public void Skinned_NegativeWeights_AreRefused()
    {
        // glTF weights are non-negative BY SPEC, so a negative one is a broken export: refused loudly like
        // NaN/Inf, never silently carried or dropped.
        var orig = Orig();
        var glb = new MeshApply.Payload
        {
            Mesh = Mesh(2,
                new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 } },
                new Dictionary<string, int> { ["Vertex"] = 3 },
                new List<int[]> { new[] { 0, 1, 0 } }),
            SkinJointHashes = new uint[] { 5, 7 },
            JointIndices = new[] { 0, 1, 0, 0, 1, 0, 0, 0 },
            JointWeights = new float[] { 1.5f, -0.5f, 0, 0, 1, 0, 0, 0 },
        };

        var ex = Assert.Throws<System.InvalidOperationException>(
            () => MeshApply.BuildSkinned(orig, glb, new uint[] { 5, 7 }));
        Assert.Contains("invalid skin weights", ex.Message);
    }

    [Fact]
    public void Skinned_ZeroWeightAbsentBone_StaysSilent()
    {
        // An absent bone carrying NO weight is harmless and must NOT warn. Only a WEIGHTED absent bone is.
        var orig = Orig();
        var targetHashes = new uint[] { 0xAAAAAAAA, 0xBBBBBBBB };
        var glb = new MeshApply.Payload
        {
            Mesh = Mesh(2,
                new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 } },
                new Dictionary<string, int> { ["Vertex"] = 3 },
                new List<int[]> { new[] { 0, 1, 0 } }),
            SkinJointHashes = new uint[] { 0xAAAAAAAA, 0xDDDDDDDD },   // 0xDDDD is absent from the target
            JointIndices = new[] { 0, 1, 0, 0, 0, 1, 0, 0 },
            JointWeights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0 },    // ...but the absent bone carries ZERO weight
        };

        var built = MeshApply.BuildSkinned(orig, glb, targetHashes);

        Assert.DoesNotContain(built.Warnings, w => w.Contains("skeleton doesn't have"));
    }

    [Fact]
    public void Skinned_WeightedAbsentBone_WarnsByAffectedVertices()
    {
        // A weighted influence on an absent bone IS flagged, worded by affected VERTICES rather than a raw
        // bone count.
        var orig = Orig();
        var targetHashes = new uint[] { 0xAAAAAAAA, 0xBBBBBBBB };
        var glb = new MeshApply.Payload
        {
            Mesh = Mesh(2,
                new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 } },
                new Dictionary<string, int> { ["Vertex"] = 3 },
                new List<int[]> { new[] { 0, 1, 0 } }),
            SkinJointHashes = new uint[] { 0xDDDDDDDD },
            JointIndices = new[] { 0, 0, 0, 0, 0, 0, 0, 0 },
            JointWeights = new float[] { 1, 0, 0, 0, 1, 0, 0, 0 },
        };

        var built = MeshApply.BuildSkinned(orig, glb, targetHashes);

        Assert.Contains(built.Warnings, w => w.Contains("2 vertex(es)") && w.Contains("skeleton doesn't have"));
    }

    [Fact]
    public void Skinned_Throws_WhenGlbHasNoSkin()
    {
        var glb = MeshApply.Payload.Geometry(Mesh(1,
            new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0 } },
            new Dictionary<string, int> { ["Vertex"] = 3 },
            new List<int[]>()));
        Assert.Throws<System.InvalidOperationException>(() => MeshApply.BuildSkinned(Orig(), glb, new uint[] { 1 }));
    }

    [Fact]
    public void Skinned_ReducesToNarrowTargetWidth_KeepingStrongest_Renormalized()
    {
        // The target stores dim-1 influences (the corpus majority) while the authored skin is always 4-wide,
        // so BuildSkinned reduces to width 1 keeping each vertex's STRONGEST bone, renormalized — rather
        // than abort at ConformChannels.
        var orig = new UnityMesh
        {
            Name = "narrow", VertexCount = 1,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = new float[] { 0, 0, 0 },
                ["BlendWeight"] = new float[] { 1.0f },   // dim 1 — a narrow target
                ["BlendIndices"] = new float[] { 0 },
            },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["BlendWeight"] = 1, ["BlendIndices"] = 1 },
            Submeshes = new List<int[]> { new[] { 0 } },
        };
        var glb = new MeshApply.Payload
        {
            Mesh = Mesh(1, new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0 } },
                new Dictionary<string, int> { ["Vertex"] = 3 }, new List<int[]> { new[] { 0 } }),
            SkinJointHashes = new uint[] { 5, 7 },
            JointIndices = new[] { 0, 1, 0, 0 },
            JointWeights = new float[] { 0.25f, 0.75f, 0, 0 },   // bone 7 is the strongest
        };

        var built = MeshApply.BuildSkinned(orig, glb, new uint[] { 5, 7 });

        Assert.Equal(new float[] { 1 }, built.Arrays["BlendIndices"]);    // kept the strongest (bone 7 → idx 1)
        Assert.Equal(new float[] { 1.0f }, built.Arrays["BlendWeight"]);  // renormalized to 1
        // dropping a 0.25 influence bends the mesh differently than the author weighted it — theirs to see
        Assert.Contains(built.Warnings, w => w.Contains("more than 1 bone influence"));
        Assert.DoesNotContain(built.Diagnostics, d => d.Contains("more than 1 bone influence"));
    }

    [Fact]
    public void Skinned_FourWideTarget_ReducesNothing_AndSaysNothing()
    {
        // The far side of the boundary: the authored skin is 4-wide at its widest (glTF WEIGHTS_0 is a vec4)
        // and a 4-wide target takes even that whole. No vertex loses an influence it was given, so there is
        // no reduction to report in either list — v0 below carries the full four.
        var glb = new MeshApply.Payload
        {
            Mesh = Mesh(2,
                new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 } },
                new Dictionary<string, int> { ["Vertex"] = 3 },
                new List<int[]> { new[] { 0, 1, 0 } }),
            SkinJointHashes = new uint[] { 5, 7, 9, 11 },
            JointIndices = new[] { 0, 1, 2, 3, /*v1*/ 1, 0, 0, 0 },
            JointWeights = new float[] { 0.4f, 0.3f, 0.2f, 0.1f, /*v1*/ 1, 0, 0, 0 },
        };

        var built = MeshApply.BuildSkinned(Orig(), glb, new uint[] { 5, 7, 9, 11 });   // Orig stores dim 4

        Assert.Equal(new float[] { 0.4f, 0.3f, 0.2f, 0.1f, 1, 0, 0, 0 }, built.Arrays["BlendWeight"]);
        Assert.DoesNotContain(built.Warnings, w => w.Contains("bone influence"));
        Assert.DoesNotContain(built.Diagnostics, d => d.Contains("bone influence"));
    }

    [Fact]
    public void Skinned_Throws_OnNonFiniteWeights()
    {
        var glb = new MeshApply.Payload
        {
            Mesh = Mesh(1, new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0 } },
                new Dictionary<string, int> { ["Vertex"] = 3 }, new List<int[]> { new[] { 0 } }),
            SkinJointHashes = new uint[] { 5 },
            JointIndices = new[] { 0, 0, 0, 0 },
            JointWeights = new float[] { float.NaN, 0, 0, 0 },
        };
        var ex = Assert.Throws<System.InvalidOperationException>(() => MeshApply.BuildSkinned(Orig(), glb, new uint[] { 5 }));
        Assert.Contains("invalid skin weights", ex.Message);
    }

    [Fact]
    public void ReduceInfluences_KeepsStrongest_RenormalizesToOne_Jointly()
    {
        // reduce to width 2 keeps the two strongest, renormalized to sum 1, each index paired with its own
        // weight
        var (bi, bw, reduced) = MeshApply.ReduceInfluences(
            bi4: new float[] { 10, 11, 12, 13 },
            bw4: new float[] { 0.1f, 0.3f, 0.1f, 0.5f },
            n: 1, width: 2);

        Assert.Equal(13f, bi[0]); Assert.Equal(11f, bi[1]);              // strongest two, in weight order
        Assert.Equal(0.625f, bw[0], 5); Assert.Equal(0.375f, bw[1], 5); // 0.5/0.8, 0.3/0.8
        Assert.Equal(1, reduced);                                        // the two dropped 0.1s were nonzero
    }

    // ---- 16-bit index overflow guard --------------------------------------

    [Fact]
    public void CheckIndexFits_Throws_OnUint16Target_WhenVertexCountExceeds65535()
    {
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => MeshApply.CheckIndexFits(0, 70000, new List<int[]> { new[] { 0, 1, 2 } }));
        Assert.Contains("16-bit", ex.Message);
        Assert.Contains("70000", ex.Message);
    }

    [Fact]
    public void CheckIndexFits_Throws_OnUint16Target_WhenAnIndexExceeds65535()
    {
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => MeshApply.CheckIndexFits(0, 10, new List<int[]> { new[] { 0, 1, 70000 } }));
        Assert.Contains("70000", ex.Message);
    }

    [Fact]
    public void CheckIndexFits_Allows_Uint16Target_AtTheBoundary()
    {
        MeshApply.CheckIndexFits(0, 65535, new List<int[]> { new[] { 0, 65535 } });
    }

    [Fact]
    public void CheckIndexFits_Allows_LargeMesh_OnUint32Target()
    {
        MeshApply.CheckIndexFits(1, 5_000_000, new List<int[]> { new[] { 0, 4_999_999 } });
    }

    // ---- ConformChannels: fit each payload channel to the target's stored layout dim ----------------
    // Positional channel order: Vertex(0), Normal(1), Tangent(2), Color(3), TexCoord0(4)…, BlendWeight(12),
    // BlendIndices(13). A packed Normal target stores dim 4.

    private static UnityMesh.ChannelDef[] LayoutSlots(int normalDim = 3, int blendWeightDim = 0, int texCoord0Dim = 0)
    {
        var s = new UnityMesh.ChannelDef[14];
        for (int i = 0; i < 14; i++) s[i] = new UnityMesh.ChannelDef(0, 0, 0, 0);
        s[0] = new UnityMesh.ChannelDef(0, 0, 0, 3);            // Vertex
        s[1] = new UnityMesh.ChannelDef(0, 0, 0, normalDim);   // Normal
        if (texCoord0Dim > 0) s[4] = new UnityMesh.ChannelDef(0, 0, 0, texCoord0Dim);
        if (blendWeightDim > 0) s[12] = new UnityMesh.ChannelDef(0, 0, 0, blendWeightDim);
        return s;
    }

    [Fact]
    public void Conform_WidensNarrowNormal_FillingFourthFromNearestOriginal()
    {
        var orig = new UnityMesh
        {
            VertexCount = 2,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 },
                ["Normal"] = new float[] { 0, 1, 0, 7,  1, 0, 0, 9 },
            },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["Normal"] = 4 },
        };
        var built = new MeshApply.Built
        {
            Arrays = new Dictionary<string, float[]> { ["Normal"] = new float[] { 2, 0, 0, 0, 3, 0 } },
            Submeshes = new List<int[]>(),
            VertexCount = 2,
            NearestOriginal = new[] { 1, 0 },
        };

        var outp = MeshApply.ConformChannels(LayoutSlots(normalDim: 4), 2, built, orig);

        // payload (3-comp): v0={2,0,0}, v1={0,3,0}. v0 4th from orig[nn=1][3]=9; v1 4th from orig[0][3]=7.
        Assert.Equal(new float[] { 2, 0, 0, 9, 0, 3, 0, 7 }, outp["Normal"]);
    }

    [Fact]
    public void Conform_WidensNarrowNormal_IdentityMapping_WhenNearestOriginalNull()
    {
        var orig = new UnityMesh
        {
            VertexCount = 2,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = new float[] { 0, 0, 0, 10, 0, 0 },
                ["Normal"] = new float[] { 0, 1, 0, 5,  1, 0, 0, 6 },
            },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["Normal"] = 4 },
        };
        var built = new MeshApply.Built
        {
            Arrays = new Dictionary<string, float[]> { ["Normal"] = new float[] { 1, 0, 0, 0, 1, 0 } },
            Submeshes = new List<int[]>(),
            VertexCount = 2,
            NearestOriginal = null,
        };

        var outp = MeshApply.ConformChannels(LayoutSlots(normalDim: 4), 2, built, orig);

        Assert.Equal(new float[] { 1, 0, 0, 5, 0, 1, 0, 6 }, outp["Normal"]);
    }

    [Fact]
    public void Conform_PassesThroughChannelsThatAlreadyMatch()
    {
        var orig = new UnityMesh
        {
            VertexCount = 1,
            Channels = new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0 }, ["Normal"] = new float[] { 0, 1, 0 } },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["Normal"] = 3 },
        };
        var arr = new float[] { 0, 0, 1 };
        var built = new MeshApply.Built
        {
            Arrays = new Dictionary<string, float[]> { ["Normal"] = arr },
            Submeshes = new List<int[]>(), VertexCount = 1, NearestOriginal = new[] { 0 },
        };
        var outp = MeshApply.ConformChannels(LayoutSlots(normalDim: 3), 1, built, orig);
        Assert.Same(arr, outp["Normal"]);
    }

    [Fact]
    public void Conform_TruncatesWiderNonWeightChannel()
    {
        var orig = new UnityMesh
        {
            VertexCount = 1,
            Channels = new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0 } },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
        };
        var built = new MeshApply.Built
        {
            Arrays = new Dictionary<string, float[]> { ["TexCoord0"] = new float[] { 0.1f, 0.2f, 0.3f, 0.4f } },
            Submeshes = new List<int[]>(), VertexCount = 1, NearestOriginal = new[] { 0 },
        };
        var outp = MeshApply.ConformChannels(LayoutSlots(texCoord0Dim: 2), 1, built, orig);
        Assert.Equal(new float[] { 0.1f, 0.2f }, outp["TexCoord0"]);
    }

    [Fact]
    public void Conform_Refuses_TruncatingBlendWeight()
    {
        var orig = new UnityMesh
        {
            VertexCount = 1,
            Channels = new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0 } },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
        };
        var built = new MeshApply.Built
        {
            Arrays = new Dictionary<string, float[]> { ["BlendWeight"] = new float[] { 0.4f, 0.3f, 0.2f, 0.1f } },
            Submeshes = new List<int[]>(), VertexCount = 1, NearestOriginal = new[] { 0 },
        };
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => MeshApply.ConformChannels(LayoutSlots(blendWeightDim: 2), 1, built, orig));
        Assert.Contains("influence", ex.Message);
    }

    [Fact]
    public void Conform_Throws_WhenOriginalCannotSupplyMissingComponents()
    {
        var orig = new UnityMesh
        {
            VertexCount = 1,
            Channels = new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0 }, ["Normal"] = new float[] { 0, 1, 0 } },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["Normal"] = 3 },
        };
        var built = new MeshApply.Built
        {
            Arrays = new Dictionary<string, float[]> { ["Normal"] = new float[] { 0, 0, 1 } },
            Submeshes = new List<int[]>(), VertexCount = 1, NearestOriginal = new[] { 0 },
        };
        Assert.Throws<System.InvalidOperationException>(
            () => MeshApply.ConformChannels(LayoutSlots(normalDim: 4), 1, built, orig));
    }

    // ---- Position-stride guard: Vertex is NEVER packed, so a stored Vertex dim ≠ 3 is refused loudly
    // rather than silently mis-strided by the nearest-neighbour / AABB / position math. ----

    [Fact]
    public void RequireStride3Positions_Throws_WhenVertexDimensionIsNot3()
    {
        var orig = new UnityMesh
        {
            Name = "c_weird",
            VertexCount = 1,
            Channels = new Dictionary<string, float[]> { ["Vertex"] = new float[] { 0, 0, 0, 0 } },
            Dims = new Dictionary<string, int> { ["Vertex"] = 4 },
        };
        var ex = Assert.Throws<System.InvalidOperationException>(() => MeshApply.RequireStride3Positions(orig));
        Assert.Contains("position channel", ex.Message);
        Assert.Contains("c_weird", ex.Message);
    }

    [Fact]
    public void RequireStride3Positions_Allows_StandardDimension3()
    {
        MeshApply.RequireStride3Positions(Orig());
    }
}
