using System;
using System.IO;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Anchor-preferred bone ownership through the whole emission: a shared bone the anchor recovers soundly
/// is recovered at the anchor's own draw even when another pool part carries more weight on it — the
/// shipped owner buffer, both scatter maps and the build diagnostics must all tell that one story. The
/// weight-argmax rule this adjusts is pinned at the PoolMath level (BuildUnion tests); this pins the
/// emitter actually applying the preference before any consumer reads ownership.
/// </summary>
public class AnchorOwnershipEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-anchorown-" + Guid.NewGuid().ToString("N"));

    public AnchorOwnershipEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const uint A = 101, B = 102, C = 103;

    [Fact]
    public void A_sound_anchor_bone_is_owned_by_the_anchor_over_the_weight_winner()
    {
        // alpha (bones A,B; 32 verts) outweighs beta (bones B,C; 16 verts) on the shared bone B, so the
        // argmax gives B to alpha — but beta is the anchor and recovers B soundly, so B is beta's.
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        string bd = Path.Combine(_root, "beta"); SyntheticPool.WritePartDump(bd, 2, 16, new[] { B, C });
        string outDir = Path.Combine(_root, "out");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                },
            },
        });

        // union order is first-seen [A, B, C]; owners [alpha, beta, beta]
        var owner = File.ReadAllBytes(Path.Combine(outDir, "owner_part_swap.buf"));
        Assert.Equal(0u, BitConverter.ToUInt32(owner, 0));
        Assert.Equal(1u, BitConverter.ToUInt32(owner, 4));
        Assert.Equal(1u, BitConverter.ToUInt32(owner, 8));

        // alpha's scatter loses B; the anchor's writes both its bones
        var alphaMap = File.ReadAllBytes(Path.Combine(outDir, "alpha_map_swap.buf"));
        Assert.Equal(0u, BitConverter.ToUInt32(alphaMap, 0));
        Assert.Equal(PoolMath.Sentinel, BitConverter.ToUInt32(alphaMap, 4));
        var betaMap = File.ReadAllBytes(Path.Combine(outDir, "beta_map_swap.buf"));
        Assert.Equal(1u, BitConverter.ToUInt32(betaMap, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(betaMap, 4));

        Assert.Contains(result.Diagnostics,
            d => d.Contains("1 union bone re-owned to the anchor", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bone_the_anchor_holds_only_at_every_vertex_is_recovered_from_a_part_that_holds_it_slim()
    {
        // beta, the anchor, carries B only proportionally to A over every vertex, so no small selection of
        // beta's holds B: beta would ship it at all 1300 vertices. alpha carries B on vertices of its own and
        // holds it with a few. B is recovered from alpha; A, which beta holds, stays beta's and serves as
        // the witness that rebases alpha's rows.
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 64, new[] { A, B });
        SyntheticPool.NonZeroPositions(ad);     // a spread cloud: the ramp fixture is near-collinear per bone
        string bd = Path.Combine(_root, "beta");
        SyntheticPool.WriteProportionalPairDump(bd, seed: 7, mixed: 1100, skew: 200, A, B);
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 9, unionBones: 2, submeshes: 1);
        string outDir = Path.Combine(_root, "out-slim");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    DonorDir = donor,
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                },
            },
        });

        // union order is first-seen [A, B]; owners [beta, alpha]
        var owner = File.ReadAllBytes(Path.Combine(outDir, "owner_part_swap.buf"));
        Assert.Equal(1u, BitConverter.ToUInt32(owner, 0));
        Assert.Equal(0u, BitConverter.ToUInt32(owner, 4));
        Assert.Contains(result.Diagnostics, d => d.Contains($"swap: bone 0x{B:x8} is recovered from 'alpha'")
            && d.Contains("instead of 'beta'"));

        // beta recovers A alone — B never reaches its operator, so nothing in it runs every vertex
        Assert.Equal(8, new FileInfo(Path.Combine(outDir, "beta_off.buf")).Length);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("dense width") || d.Contains("no small set"));

        // alpha's rows reach the anchor's space through the shared bone, recovered live at alpha's draw
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("shares no sound bone"));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.Contains("run = CustomShaderRecover_alpha_swap", ini);
    }
}
