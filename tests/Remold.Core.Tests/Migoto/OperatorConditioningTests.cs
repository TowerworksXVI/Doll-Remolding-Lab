using System;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Operator conditioning: a bone whose vertex support can't determine its palette row is tied rigidly to a
/// sound co-riding bone, and the tie is corrected by what the bone's own vertices show, so those vertices
/// land where the game put them. The alternatives are a min-norm estimate (distorts donor geometry) or a
/// sentinel to its lod0 row (lives in the lod0 draw's SPACE, so it displaces in a two-placement frame).
/// Sentinel remains only when the mesh has no sound bone at all.
/// </summary>
public class OperatorConditioningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-cond-" + Guid.NewGuid().ToString("N"));

    public OperatorConditioningTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const uint Strong = 101, Weak = 102;

    [Fact]
    public void A_degenerate_bone_rides_its_coweighted_sound_bone()
    {
        string ad = Path.Combine(_root, "alpha");
        SyntheticPool.WriteCoWeightedDump(ad, Strong, Weak, strongVerts: 62);
        string outDir = Path.Combine(_root, "out");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                },
            },
        });

        var tieDiag = Assert.Single(result.Diagnostics, w => w.Contains("tied rigidly")
            && w.Contains($"0x{Weak:x8}") && w.Contains($"0x{Strong:x8}"));
        // conditioning observations are diagnostics, never user-facing warnings
        Assert.DoesNotContain(result.Warnings, w => w.Contains("tied rigidly"));

        // The err in a tie diagnostic is the DENSE residual — the verdict that produced the tie, and the only
        // number in the gate's units. This bone's anchor-local defect is ~0.95 against a dense ~0.83, and
        // the bound separates them, so a slim number leaking into the message fails here.
        double shown = double.Parse(System.Text.RegularExpressions.Regex.Match(tieDiag, @"err ([^)]+)\)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(shown, 0.01, 0.9);
        // this tie's target is narrow, so the message carries no width note
        Assert.DoesNotContain("dense width", tieDiag);
        Assert.Contains("corrected by 2 of its own vertices", tieDiag);

        // The weak bone's block opens with the stand-in's 4 slim rows AND its anchor-vertex segment, as
        // byte-copies: coefficients are meaningless without the vertices they index, so a tie carries both.
        // The correction follows them as a tail of its own.
        var off = ReadOff(outDir, "alpha");
        Assert.True(off[1].Width > off[0].Width, "the tied bone carries no correction tail");
        AssertOpensWith(outDir, "alpha", off[1], off[0]);
    }

    [Fact]
    public void A_tied_bones_own_vertices_land_where_the_game_posed_them()
    {
        // Two vertices cannot state a bone, so it rides the sound one; but they do state where the game put
        // THEM. A replacement that leaves them where they were, weighted as they were, must show them there
        // in any pose, which a plain tie misses by the whole of the bone's motion against its stand-in.
        // a second bone posed far from the first: the fixture's usual pair of hashes pose nearly alike
        const uint apart = 700;
        string ad = Path.Combine(_root, "own");
        SyntheticPool.WriteCoWeightedDump(ad, Strong, apart, strongVerts: 62);
        string outDir = Path.Combine(_root, "out-own");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("own", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["own"] = "aaaa0011" },
                },
            },
        });

        var miss = PosedPoolSim.OwnVertexMiss(outDir, "swap", "own", ad);
        Assert.True(miss.Max() < 1e-3f, $"a vertex of the part's own lands {miss.Max()} from where the game posed it");

        // the same two vertices under the plain tie: the pose moves the two bones apart by well over a unit
        var plain = PlainTieMiss(outDir, "own", ad);
        Assert.True(plain[62] > 0.1f && plain[63] > 0.1f, "the fixture's pose does not separate the two bones");
    }

    [Fact]
    public void A_replacement_weighted_to_a_tied_bone_away_from_its_vertices_keeps_the_plain_tie_and_says_so()
    {
        // The correction speaks for the bone's own two vertices and what lies between them. A replacement
        // that weights the bone anywhere else would be placed by a guess, so that pipeline ships the tie as
        // it was, and the build warns that the part leans on a bone it cannot place.
        string ad = Path.Combine(_root, "lean");
        SyntheticPool.WriteCoWeightedDump(ad, Strong, Weak, strongVerts: 62);
        string donor = Path.Combine(_root, "lean-donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2);
        string outDir = Path.Combine(_root, "out-lean");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("lean", ad) },
                    DonorDir = donor,
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["lean"] = "aaaa0012" },
                },
            },
        });

        var warning = Assert.Single(result.Warnings, w => w == Remold.Core.CoreStrings.EmitterTiedBones_One("lean", 1));
        Assert.Contains(result.Diagnostics, d => d.Contains($"bone 0x{Weak:x8} keeps its plain tie in lean") && d.Contains("past its own vertices"));
        Assert.Contains(result.Diagnostics, d => d.Contains("weight on a bone the build ties") && d.Contains($"0x{Weak:x8}"));

        // the pipeline's own copy of the operator: the stand-in's rows, and a tail of zeros
        var off = ReadOff(outDir, "lean");
        var cp = File.ReadAllBytes(Path.Combine(outDir, "lean_cpinv_swap.buf"));
        for (int r = 0; r < 4; r++)
        {
            for (int t = 0; t < off[0].Width; t++)
                Assert.Equal(BitConverter.ToSingle(cp, (4 * off[0].Base + r * off[0].Width + t) * 4),
                    BitConverter.ToSingle(cp, (4 * off[1].Base + r * off[1].Width + t) * 4));
            for (int t = off[0].Width; t < off[1].Width; t++)
                Assert.Equal(0f, BitConverter.ToSingle(cp, (4 * off[1].Base + r * off[1].Width + t) * 4));
        }
    }

    [Fact]
    public void A_replacement_that_keeps_a_tied_bones_vertices_where_they_were_takes_the_correction_silently()
    {
        // every replacement vertex on the tied bone stands on one of the bone's own, weighted as it is
        string ad = Path.Combine(_root, "kept");
        SyntheticPool.WriteCoWeightedDump(ad, Strong, Weak, strongVerts: 62);
        string donor = Path.Combine(_root, "kept-donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2);
        var s0 = File.ReadAllBytes(Path.Combine(ad, "stream0.buf"));
        var s2 = File.ReadAllBytes(Path.Combine(ad, "stream2.buf"));
        var d0 = File.ReadAllBytes(Path.Combine(donor, "stream0.buf"));
        var d2 = File.ReadAllBytes(Path.Combine(donor, "stream2.buf"));
        for (int v = 0; v < 8; v++)
        {
            int from = v < 2 ? 62 + v : v;     // the two tied vertices, then six of the sound bone's
            Array.Copy(s0, from * 40, d0, v * 40, 12);
            Array.Copy(s2, from * 32, d2, v * 32, 32);
        }
        File.WriteAllBytes(Path.Combine(donor, "stream0.buf"), d0);
        File.WriteAllBytes(Path.Combine(donor, "stream2.buf"), d2);
        string outDir = Path.Combine(_root, "out-kept");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("kept", ad) },
                    DonorDir = donor,
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["kept"] = "aaaa0013" },
                },
            },
        });

        Assert.Empty(result.Warnings);
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("keeps its plain tie"));
        Assert.False(File.Exists(Path.Combine(outDir, "kept_cpinv_swap.buf")));   // the solved operator ships
    }

    /// <summary>A tied bone's block opens with its stand-in's block: the same anchor vertices and the same
    /// four rows of coefficients, entry for entry.</summary>
    private static void AssertOpensWith(string outDir, string name, (int Base, int Width) tied, (int Base, int Width) standIn,
        string? operatorFile = null)
    {
        var cp = File.ReadAllBytes(Path.Combine(outDir, operatorFile ?? $"{name}_cpinv.buf"));
        var sel = SyntheticPool.ShippedSel(outDir, name)!;
        Assert.True(tied.Width >= standIn.Width, "the tied bone's block is narrower than its stand-in's");
        for (int t = 0; t < standIn.Width; t++)
        {
            Assert.Equal(sel[standIn.Base + t], sel[tied.Base + t]);
            for (int r = 0; r < 4; r++)
                Assert.Equal(BitConverter.ToSingle(cp, (4 * standIn.Base + r * standIn.Width + t) * 4),
                    BitConverter.ToSingle(cp, (4 * tied.Base + r * tied.Width + t) * 4));
        }
    }

    /// <summary>How far each vertex of a two-bone part lands from the game's pose when the second bone takes
    /// the first bone's recovered rows outright, which is what a plain tie ships.</summary>
    private static float[] PlainTieMiss(string outDir, string name, string dumpDir)
    {
        var rows = PosedPoolSim.Recover(outDir, "swap", name, dumpDir);
        var posed = PosedPoolSim.Pose(dumpDir);
        var s0 = File.ReadAllBytes(Path.Combine(dumpDir, "stream0.buf"));
        var s2 = File.ReadAllBytes(Path.Combine(dumpDir, "stream2.buf"));
        var miss = new float[posed.Length];
        for (int v = 0; v < posed.Length; v++)
        {
            var p = new System.Numerics.Vector3(BitConverter.ToSingle(s0, v * 40), BitConverter.ToSingle(s0, v * 40 + 4),
                BitConverter.ToSingle(s0, v * 40 + 8));
            var acc = System.Numerics.Vector3.Zero;
            for (int k = 0; k < 4; k++)
            {
                float w = BitConverter.ToSingle(s2, v * 32 + k * 4);
                if (w > 0) acc += w * System.Numerics.Vector3.Transform(p, rows[0]);
            }
            miss[v] = System.Numerics.Vector3.Distance(acc, posed[v]);
        }
        return miss;
    }

    /// <summary>A tied bone ships its tie's rows, so where the reference restates the tie, the tied bone's
    /// rows are restated by the TIE's statement too: the two blocks stay copies of one converted block.</summary>
    [Fact]
    public void A_tied_bone_is_restated_by_its_ties_statement()
    {
        string ad = Path.Combine(_root, "tied");
        SyntheticPool.WriteCoWeightedDump(ad, Strong, Weak, strongVerts: 62);
        var (hashes, binds) = PosedPoolSim.ReadBinds(ad);
        // the reference states the sound bone 4 cm off the mesh's own bind, and says nothing of the weak one
        var reference = new System.Collections.Generic.Dictionary<uint, System.Numerics.Matrix4x4>
        {
            [Strong] = System.Numerics.Matrix4x4.CreateTranslation(0.04f, 0, 0) * binds[System.Array.IndexOf(hashes, Strong)],
        };
        string outDir = Path.Combine(_root, "out-tied");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("tied", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["tied"] = "aaaa0004" },
                    ReferenceBinds = reference,
                },
            },
        });

        // restated at all, and the weak block opening with a byte-copy of the restated sound block: were
        // the weak bone restated by its own (absent) statement, it would still carry the solved rows. The
        // reference does not state the weak bone, so this pipeline cannot read a correction under it and
        // the tail ships as zeros.
        var off = ReadOff(outDir, "tied");
        AssertOpensWith(outDir, "tied", off[1], off[0], "tied_cpinv_swap.buf");
        var cp = File.ReadAllBytes(Path.Combine(outDir, "tied_cpinv_swap.buf"));
        Assert.True(off[1].Width > off[0].Width);
        for (int r = 0; r < 4; r++)
            for (int t = off[0].Width; t < off[1].Width; t++)
                Assert.Equal(0f, BitConverter.ToSingle(cp, (4 * off[1].Base + r * off[1].Width + t) * 4));
    }

    [Fact]
    public void The_slim_operator_is_small_and_no_healthy_bone_needs_the_weak_path()
    {
        // Dense is 4·nb·N coefficients (linear in vertex count, the dominant payload of a real build); slim
        // is 4·nb·K. This pins the size, and that a healthy mesh holds at low K.
        string ad = Path.Combine(_root, "big");
        SyntheticPool.WritePartDump(ad, seed: 9, verts: 512, boneHashes: new uint[] { 101, 102 });
        string outDir = Path.Combine(_root, "out-big");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("big", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["big"] = "aaaa0003" },
                },
            },
        });

        var cp = new FileInfo(Path.Combine(outDir, "big_cpinv.buf"));
        var sel = SyntheticPool.ShippedSelBytes(outDir, "big");
        int k = sel.Length / 4 / 2;
        Assert.Equal(4L * 2 * k * 4, cp.Length);
        Assert.True(k < 512, $"K={k} did not slim below the vertex count");
        // both shipped buffers count against dense: coefficients are useless without their anchors
        Assert.True(cp.Length + sel.Length < 4L * 2 * 512 * 4,
            $"slim operator ({cp.Length + sel.Length}B over two buffers) is not smaller than dense");
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("loses conditioning"));
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("tied rigidly"));
    }

    [Theory]
    // a finite defect always beats a NaN incumbent, whichever order the levels ran in
    [InlineData(0.5, double.NaN, true)]
    [InlineData(double.NaN, 0.5, false)]
    [InlineData(double.PositiveInfinity, double.NaN, true)]
    // between usable defects the smaller wins, and equals keep the incumbent (the widest level)
    [InlineData(0.2, 0.5, true)]
    [InlineData(0.5, 0.2, false)]
    [InlineData(0.5, 0.5, false)]
    [InlineData(double.NaN, double.NaN, false)]
    [InlineData(1.0, double.PositiveInfinity, true)]
    public void The_cap_level_search_ranks_NaN_below_every_usable_defect(double candidate, double incumbent, bool wins)
    {
        // `<` is false against NaN in BOTH directions, so an unguarded comparison lets whichever side holds
        // NaN stand and discards clean levels.
        Assert.Equal(wins, MigotoEmitter.BetterDefect(candidate, incumbent));
    }

    [Fact]
    public void A_shipped_slim_operator_reports_its_worst_defect_once()
    {
        // The gate is a bound, not a report: a part inside it still ships SOME defect, and the build states
        // it (with the dense residual it replaces) exactly once per slim part.
        string ad = Path.Combine(_root, "reported");
        SyntheticPool.WritePartDump(ad, seed: 21, verts: 512, boneHashes: new uint[] { 101, 102 });
        string outDir = Path.Combine(_root, "out-reported");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("reported", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["reported"] = "aaaa0008" },
                },
            },
        });

        Assert.True(SyntheticPool.ShippedSel(outDir, "reported") is not null, "the part failed to slim");
        var diag = Assert.Single(result.Diagnostics, w => w.Contains("slim operator ships"));
        Assert.StartsWith("reported: ", diag);
        Assert.Contains("worst defect", diag);
        Assert.Contains("(dense ", diag);
    }

    [Theory]
    // one bone anchored at K=32, so its block is 20·32 + 8 = 648B whatever the mesh is; dense is 16·n
    [InlineData(64, true)]      // 648B against 1024B dense
    [InlineData(40, false)]     // 648B against 640B — the slim layout is not smaller
    [InlineData(32, false)]     // the operator would be WIDER than the mesh it slims
    public void Slim_ships_only_when_all_three_of_its_buffers_undercut_dense(int verts, bool slims)
    {
        // Slim ships three buffers (4 float rows per bone, the anchor indices, and the offset pair that
        // locates them) against dense's 4 float rows of n. On a small enough mesh that is a bigger download
        // for a worse-conditioned operator, so the part ships dense — and a size verdict says nothing about
        // conditioning, so it says nothing.
        string ad = Path.Combine(_root, "size" + verts);
        SyntheticPool.WritePartDump(ad, seed: 4, verts: verts, boneHashes: new uint[] { 101 });
        string outDir = Path.Combine(_root, "out-size" + verts);
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("size", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["size"] = "aaaa000b" },
                },
            },
        });

        var cp = new FileInfo(Path.Combine(outDir, "size_cpinv.buf"));
        var sel = SyntheticPool.ShippedSel(outDir, "size");
        var off = new FileInfo(Path.Combine(outDir, "size_off.buf"));
        Assert.Equal(slims, sel is not null);
        Assert.Equal(slims, off.Exists);
        if (slims) Assert.True(cp.Length + 4L * sel!.Length + off.Length < 4L * 1 * verts * 4);
        else Assert.Equal(4L * 1 * verts * 4, cp.Length);
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("slimming declined"));

        // The buffers are only half the verdict: the shader and the ini have to agree with them, or the
        // part ships dense rows behind a shader that indexes them through anchors that were never written.
        // A part replaced on its own bones recovers inside its per-copy palette pass, which is stamped with
        // the same operator body the recover shader carries.
        string hlsl = File.ReadAllText(Path.Combine(outDir, "pose_palette_size_swap.hlsl"));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        if (!slims)
        {
            Assert.Contains($"static const uint N={verts}", hlsl);
            Assert.DoesNotContain("Sel", hlsl);
            Assert.DoesNotContain("Off", hlsl);
            Assert.DoesNotContain("[Resource_size_Sel]", ini);
            Assert.DoesNotContain("[Resource_size_Off]", ini);
            Assert.DoesNotContain("ps-t3", ini);
            Assert.DoesNotContain("ps-t4", ini);
        }
        else
        {
            Assert.DoesNotContain("static const uint N=", hlsl);
            // the kernel reads the vertex list through its packet, so the list itself is not declared
            Assert.Contains("[Resource_size_PacketSel]", ini);
            Assert.Contains("ps-t3 = Resource_size_PacketSel\nps-t4 = Resource_size_Off", ini);
            Assert.DoesNotContain("[Resource_size_Sel]", ini);
        }
    }

    [Fact]
    public void An_unridable_weak_bone_does_not_hold_back_the_rest_of_the_part()
    {
        // A weak bone with no tie target keeps its own rows and is still called out, but it costs no OTHER
        // bone its slim width: the part ships slim around it.
        string ad = Path.Combine(_root, "unridable");
        SyntheticPool.WritePartDump(ad, seed: 9, verts: 512, boneHashes: new uint[] { 101, 102, 103 },
            weightedBones: 2);                          // bone 103 is rigged but carries no weight
        string outDir = Path.Combine(_root, "out-unridable");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("unridable", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["unridable"] = "aaaa0009" },
                },
            },
        });

        Assert.Contains(result.Diagnostics, w => w.Contains($"unridable: bone 0x{103:x8}")
            && w.Contains("no sound bone to ride"));
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("slimming declined"));
        Assert.Contains(result.Diagnostics, w => w.Contains("slim operator ships"));
        Assert.True(SyntheticPool.ShippedSel(outDir, "unridable") is not null, "the part failed to slim");

        // the unweighted bone has nothing to anchor ON, so it takes a single zero row — which recovers the
        // same zero palette row the dense operator gives it, at 1/512th of the width
        var off = ReadOff(outDir, "unridable");
        Assert.Equal(1, off[2].Width);
        var cp = File.ReadAllBytes(Path.Combine(outDir, "unridable_cpinv.buf"));
        for (int r = 0; r < 4; r++)
            Assert.Equal(0f, BitConverter.ToSingle(cp, (4 * off[2].Base + r) * 4));
        Assert.True(off[0].Width < 512 && off[1].Width < 512, "the weighted bones widened with it");
    }

    [Fact]
    public void Widely_coweighted_blends_hold_conditioning_without_demotions()
    {
        // A wide co-weighting span floods each bone's anchor selection with trace-weight co-bones, whose
        // near-null columns the rcond cutoff truncates into min-norm bias. Mass-restricted local columns +
        // the defect gate must keep every dense-sound bone sound: demoting one the dense operator recovered
        // exactly is a shipped deformation regression, not a size win.
        string ad = Path.Combine(_root, "blend");
        SyntheticPool.WriteBlendedPartDump(ad, seed: 5, verts: 2000,
            boneHashes: Enumerable.Range(0, 40).Select(i => (uint)(1000 + i)).ToArray(), span: 20);
        string outDir = Path.Combine(_root, "out-blend");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("blend", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["blend"] = "aaaa0004" },
                },
            },
        });

        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("loses conditioning"));
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("tied rigidly"));
        // no bone may reach the gate by widening to the whole mesh — that is the escape hatch, not the win
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("dense width"));
        // slim must stay slim while doing it: escalation may raise widths, but not to the vertex count
        var sel = SyntheticPool.ShippedSelBytes(outDir, "blend");
        int k = sel.Length / 4 / 40;
        Assert.True(k <= 128, $"widths averaged {k}; the local solve is not holding at practical widths");
    }

    [Fact]
    public void Small_selections_keep_their_loadbearing_cobones()
    {
        // A fixed-ratio column cap drops LOAD-BEARING co-bones whenever a bone's candidate count is small
        // relative to its co-bone count (100 verts, 10 bones, span 10 → every bone fails, the part
        // declines), while keeping them all solves to working precision.
        string ad = Path.Combine(_root, "smallsel");
        SyntheticPool.WriteBlendedPartDump(ad, seed: 11, verts: 100,
            boneHashes: Enumerable.Range(0, 10).Select(i => (uint)(2000 + i)).ToArray(), span: 10);
        string outDir = Path.Combine(_root, "out-smallsel");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("smallsel", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["smallsel"] = "aaaa0006" },
                },
            },
        });

        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("slimming declined"));
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("tied rigidly"));
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("dense width"));
        Assert.True(SyntheticPool.ShippedSel(outDir, "smallsel") is not null, "the part failed to slim");
        // and no width exceeds the vertex count, however the escalation ran
        foreach (var (_, width) in ReadOff(outDir, "smallsel"))
            Assert.True(width <= 100, $"a width of {width} overshot the 100-vertex mesh");
    }

    [Fact]
    public void Trace_weights_do_not_poison_the_local_solve()
    {
        // A trace fourth influence (1e-4) on every vertex puts a near-null column in every unrestricted
        // local solve, which the rcond cutoff truncates into min-norm bias. The mass-ranked column cap keeps
        // the load-bearing co-bones and whatever it drops lands in the defect — and the guarantee under test
        // is that arbitration's outcome: every dense-sound bone still holds the gate.
        string ad = Path.Combine(_root, "trace");
        SyntheticPool.WriteBlendedPartDump(ad, seed: 13, verts: 2000,
            boneHashes: Enumerable.Range(0, 40).Select(i => (uint)(3000 + i)).ToArray(), span: 20,
            traceWeight: 1e-4f);
        string outDir = Path.Combine(_root, "out-trace");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("trace", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["trace"] = "aaaa0007" },
                },
            },
        });

        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("slimming declined"));
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("tied rigidly"));
        Assert.True(SyntheticPool.ShippedSel(outDir, "trace") is not null, "the part failed to slim");
        var diag = Assert.Single(result.Diagnostics, w => w.Contains("slim operator ships"));
        double worst = double.Parse(
            System.Text.RegularExpressions.Regex.Match(diag, @"worst defect (\S+) ").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(worst <= 1e-3, $"the shipped rows carry a defect of {worst}");

        // The number above excludes bones that widened to the vertex count, so on its own it would stay
        // green while the trace columns pushed bones off the narrow solve one by one. Both escape hatches
        // are shut here: none may widen, and the shipped selection stays a slim multiple of the bone count.
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("dense width"));
        long selRows = SyntheticPool.ShippedSelBytes(outDir, "trace").Length / 4;
        Assert.True(selRows <= 40L * 128, $"the 40 bones ship {selRows} anchor rows between them");
    }

    [Fact]
    public void A_bone_its_own_anchors_cannot_separate_slims_on_discriminator_rows()
    {
        // A bone whose entire anchor region is proportionally co-weighted with a neighbour is locally
        // indistinguishable from it. Vertices carrying the NEIGHBOUR and not the bone separate them, so the
        // bone pays only their width and the part still slims.
        const int n = 160;
        string ad = Path.Combine(_root, "pair");
        SyntheticPool.WriteCollinearPairDump(ad, seed: 7, aOnly: 120, mixed: 40, Strong, Weak);
        string outDir = Path.Combine(_root, "out-pair");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("pair", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["pair"] = "aaaa0005" },
                },
            },
        });

        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("slimming declined"));
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("tied rigidly"));   // nobody was demoted
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("dense width"));    // nobody had to widen
        Assert.Contains(result.Diagnostics, w => w.Contains("pair: slim operator ships"));

        var off = ReadOff(outDir, "pair");
        Assert.Equal(2, off.Length);
        // the bad bone carries its own anchors AND the discriminators, and stays well short of the mesh
        Assert.True(off[1].Width > off[0].Width, "the bad bone did not pay for discriminator rows");
        Assert.True(off[1].Width < n, $"the bad bone widened to {off[1].Width} of {n}");

        long slim = new FileInfo(Path.Combine(outDir, "pair_cpinv.buf")).Length
            + SyntheticPool.ShippedSelBytes(outDir, "pair").Length
            + new FileInfo(Path.Combine(outDir, "pair_off.buf")).Length;
        Assert.True(slim < 16L * 2 * n, $"the ragged operator ({slim}B) did not undercut dense ({16 * 2 * n}B)");

        string hlsl = File.ReadAllText(Path.Combine(outDir, "pose_palette_pair_swap.hlsl"));
        Assert.Contains("Off    : register(t4)", hlsl);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.Contains("[Resource_pair_Off]", ini);
        Assert.Contains("ps-t4 = Resource_pair_Off", ini);
    }

    [Fact]
    public void A_bone_no_selection_can_hold_rides_its_co_riding_bone_instead_of_every_vertex()
    {
        // Proportional over every vertex it can anchor on, and no vertex carries its neighbour without it —
        // neither escalation nor discriminators reach a selection that separates the two. Shipped at every
        // vertex, its recover row would run as long as the mesh is big; it rides the bone it moves with
        // instead, the same rigid tie a weak bone takes, and the part slims around it.
        // the 50/50 region has to outnumber the candidate pool the spread ranks over (4·K at the cap), or
        // the escalation reaches the 90/10 vertices and separates the pair after all
        const int mixed = 1100, skew = 200, n = mixed + skew;
        string ad = Path.Combine(_root, "wide");
        SyntheticPool.WriteProportionalPairDump(ad, seed: 7, mixed: mixed, skew: skew, Strong, Weak);
        string outDir = Path.Combine(_root, "out-wide");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("wide", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["wide"] = "aaaa000d" },
                },
            },
        });

        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("dense width"));
        var tieDiag = Assert.Single(result.Diagnostics, w => w.Contains("tied rigidly"));
        Assert.Contains($"wide: no small set of vertices recovers bone 0x{Weak:x8}", tieDiag);
        Assert.Contains($"co-riding bone 0x{Strong:x8}", tieDiag);

        // the tie opens with the stand-in's rows AND its anchor segment, and stays far under the mesh's
        // width with its correction
        var off = ReadOff(outDir, "wide");
        Assert.True(off[0].Width < n, $"the sound bone widened ({off[0].Width} of {n})");
        AssertOpensWith(outDir, "wide", off[1], off[0]);
        Assert.True(off[1].Width <= 256, $"the tied bone's block is {off[1].Width} wide");

        // its own vertices state it in full here, so the correction carries it the whole way: every vertex
        // of the part lands where the game posed it
        var miss = PosedPoolSim.OwnVertexMiss(outDir, "swap", "wide", ad);
        Assert.True(miss.Max() < 2e-3f, $"a vertex of the part's own lands {miss.Max()} from where the game posed it");
    }

    [Fact]
    public void A_weak_bone_whose_stand_in_took_a_tie_rides_where_the_stand_in_rides()
    {
        // The rider (two vertices of its own, co-weighted onto Weak) picks Weak as its stand-in, and Weak,
        // which no slim selection holds, rides Strong. The rider's rows and its reported tie must both be
        // Strong's: a tie naming Weak would convert the rider's rows under the wrong bone's bind.
        const int mixed = 1100, skew = 200, n = mixed + skew + 2;
        string ad = Path.Combine(_root, "tiedwide");
        SyntheticPool.WriteProportionalPairDump(ad, seed: 7, mixed: mixed, skew: skew, Strong, Weak, tiedHash: 103);
        string outDir = Path.Combine(_root, "out-tiedwide");
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("tiedwide", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["tiedwide"] = "aaaa000e" },
                },
            },
        });

        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("dense width"));
        var riderDiag = Assert.Single(result.Diagnostics, w => w.Contains("tied rigidly") && w.Contains($"0x{103:x8}"));
        Assert.Contains($"co-riding bone 0x{Strong:x8}", riderDiag);

        var off = ReadOff(outDir, "tiedwide");
        Assert.True(off[0].Width < n, "the sound bone widened");
        AssertOpensWith(outDir, "tiedwide", off[1], off[0]);
        AssertOpensWith(outDir, "tiedwide", off[2], off[0]);
    }

    [Fact]
    public void Discriminator_rows_rescue_a_bone_its_own_anchors_cannot_separate()
    {
        // The same collinear pair, at the level below: the bad bone's own anchors are 0.5/0.5 with its
        // neighbour everywhere, so no selection drawn from them can tell the two apart. Rows weighted on the
        // NEIGHBOUR and not on the bone pin the neighbour's columns, and the solve separates.
        string ad = Path.Combine(_root, "disc");
        SyntheticPool.WriteCollinearPairDump(ad, seed: 7, aOnly: 120, mixed: 40, Strong, Weak);
        var (p, w, bi) = LoadDump(ad);
        const int bone = 1, k = 16;

        var anchors = PoolMath.SelectAnchorRows(p, w, bi, bone, k);
        Assert.Equal(k, anchors.Length);
        var (_, plain) = PoolMath.LocalPInvRows(p, w, bi, bone, anchors, nbones: 2);
        Assert.True(plain > 1e-3, $"the plain selection already solved this bone (defect {plain}) — no rescue to test");

        var disc = PoolMath.SelectDiscriminatorRows(p, w, bi, bone, anchors, nbones: 2, kd: k);
        Assert.NotEmpty(disc);
        foreach (int v in disc)
        {
            Assert.DoesNotContain(v, anchors);
            for (int j = 0; j < 4; j++)
                if (bi[v, j] == bone) Assert.True(w[v, j] < 1e-4, $"vertex {v} carries target weight {w[v, j]}");
        }

        var wide = anchors.Concat(disc).OrderBy(v => v).ToArray();
        var (_, rescued) = PoolMath.LocalPInvRows(p, w, bi, bone, wide, nbones: 2);
        Assert.True(rescued <= 1e-3, $"discriminators left the bone at defect {rescued}");
    }

    [Fact]
    public void A_keyed_operator_is_read_back_from_the_cache_instead_of_re_solved()
    {
        // The dump is rewritten between the two builds, so a re-solve could not possibly produce the first
        // build's coefficients. What makes serving them sound is the KEY: the caller names the source mesh
        // and the catalog it came from, and only an identical name may read the entry.
        string ad = Path.Combine(_root, "cached");
        string cache = Path.Combine(_root, "opcache");
        var hashes = new uint[] { Strong, Weak };
        PoolBuildRequest Req(string outDir, string? opKey) => new()
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("cached", ad, opKey) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["cached"] = "aaaa00ff" },
                },
            },
        };

        SyntheticPool.WritePartDump(ad, seed: 9, verts: 512, boneHashes: hashes);
        var first = new MigotoEmitter { OperatorCacheDir = cache }
            .Build(Req(Path.Combine(_root, "out-c1"), "cat1|bundle0|mesh|0"));
        var solved = File.ReadAllBytes(Path.Combine(first.OutDir, "cached_cpinv.buf"));

        SyntheticPool.WritePartDump(ad, seed: 77, verts: 512, boneHashes: hashes);
        var second = new MigotoEmitter { OperatorCacheDir = cache }
            .Build(Req(Path.Combine(_root, "out-c2"), "cat1|bundle0|mesh|0"));
        Assert.Equal(solved, File.ReadAllBytes(Path.Combine(second.OutDir, "cached_cpinv.buf")));
        Assert.Equal(first.Diagnostics, second.Diagnostics);

        // an unkeyed part is solved from the dump in front of it, every time
        var unkeyed = new MigotoEmitter { OperatorCacheDir = cache }
            .Build(Req(Path.Combine(_root, "out-c3"), null));
        Assert.NotEqual(solved, File.ReadAllBytes(Path.Combine(unkeyed.OutDir, "cached_cpinv.buf")));
    }

    [Fact]
    public void A_cached_operator_decides_its_corrected_ties_as_the_solved_one_did()
    {
        // What a pipeline needs to judge a corrected tie (where its tail starts, and what the bone's own
        // vertices span) is part of the operator, so a build served from the cache keeps or zeroes the tail
        // exactly as the build that solved it did.
        string ad = Path.Combine(_root, "cachedtie");
        string cache = Path.Combine(_root, "opcache-tie");
        SyntheticPool.WriteCoWeightedDump(ad, Strong, Weak, strongVerts: 62);
        string donor = Path.Combine(_root, "cachedtie-donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2);
        PoolBuildRequest Req(string outDir, string? donorDir) => new()
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("cachedtie", ad, "cat1|bundle0|tie|0") },
                    DonorDir = donorDir,
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["cachedtie"] = "aaaa00fd" },
                },
            },
        };

        var solved = new MigotoEmitter { OperatorCacheDir = cache }.Build(Req(Path.Combine(_root, "out-t1"), donor));
        Assert.Single(Directory.GetFiles(cache, "*.op"));
        var cached = new MigotoEmitter { OperatorCacheDir = cache }.Build(Req(Path.Combine(_root, "out-t2"), donor));

        Assert.Equal(solved.Warnings, cached.Warnings);
        Assert.Equal(solved.Diagnostics, cached.Diagnostics);
        // the pipeline withdrew the tail, so it ships its own copy of the operator and not the solved one
        foreach (string file in new[] { "cachedtie_cpinv_swap.buf", "cachedtie_off.buf" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(solved.OutDir, file)),
                File.ReadAllBytes(Path.Combine(cached.OutDir, file)));
        Assert.Contains(cached.Diagnostics, d => d.Contains("keeps its plain tie"));
    }

    [Fact]
    public void A_cache_entry_claiming_more_than_it_holds_is_a_miss_and_the_build_carries_on()
    {
        // A length prefix in a cache entry is a number off disk, and the reader allocates for it. Nothing
        // stops that number being garbage — a bad sector, a truncated write, a file another tool wrote —
        // and an allocation sized by it kills a build that had a solve available all along.
        string ad = Path.Combine(_root, "garbage");
        string cache = Path.Combine(_root, "opcache-garbage");
        const string key = "cat1|bundle0|mesh|0";
        var hashes = new uint[] { Strong, Weak };
        PoolBuildRequest Req(string outDir) => new()
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("garbage", ad, key) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["garbage"] = "aaaa00fe" },
                },
            },
        };

        SyntheticPool.WritePartDump(ad, seed: 9, verts: 512, boneHashes: hashes);
        var first = new MigotoEmitter { OperatorCacheDir = cache }.Build(Req(Path.Combine(_root, "out-g1")));
        var solved = File.ReadAllBytes(Path.Combine(first.OutDir, "garbage_cpinv.buf"));

        // the coefficient array's own count, overwritten with a length no file could hold
        string entry = Assert.Single(Directory.GetFiles(cache, "*.op"));
        long at;
        using (var f = File.OpenRead(entry))
        using (var r = new System.IO.BinaryReader(f, System.Text.Encoding.UTF8))
        {
            r.ReadString(); r.ReadString(); r.ReadInt32();
            at = f.Position;
        }
        using (var f = File.Open(entry, FileMode.Open, FileAccess.Write))
        {
            f.Position = at;
            f.Write(BitConverter.GetBytes(int.MaxValue));
        }

        var second = new MigotoEmitter { OperatorCacheDir = cache }.Build(Req(Path.Combine(_root, "out-g2")));

        Assert.Equal(solved, File.ReadAllBytes(Path.Combine(second.OutDir, "garbage_cpinv.buf")));
        Assert.Equal(first.Diagnostics, second.Diagnostics);
        // the miss republished the entry, so the damage costs one solve rather than every later build
        Assert.NotEqual(int.MaxValue, ReadCountAt(entry, at));
    }

    /// <summary>The int32 at <paramref name="at"/> in a cache entry.</summary>
    private static int ReadCountAt(string entry, long at)
    {
        using var f = File.OpenRead(entry);
        f.Position = at;
        var buf = new byte[4];
        f.ReadExactly(buf);
        return BitConverter.ToInt32(buf);
    }

    [Fact]
    public void Ragged_offsets_tile_the_shipped_buffers_exactly()
    {
        // The offset table is the only thing that says where a bone's rows are. If it disagrees with the
        // buffers by one element the shader reads a neighbour's coefficients and says nothing.
        string ad = Path.Combine(_root, "ragged");
        SyntheticPool.WriteCollinearPairDump(ad, seed: 7, aOnly: 120, mixed: 40, Strong, Weak);
        string outDir = Path.Combine(_root, "out-ragged");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("ragged", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["ragged"] = "aaaa000c" },
                },
            },
        });

        var off = ReadOff(outDir, "ragged");
        int expected = 0;
        foreach (var (bas, width) in off)
        {
            Assert.True(width >= 1, "a bone shipped a zero-width block");
            Assert.Equal(expected, bas);          // blocks tile in bone order: no gaps, no overlap
            expected += width;
        }
        Assert.Equal(expected * 4, SyntheticPool.ShippedSelBytes(outDir, "ragged").Length);
        Assert.Equal(expected * 16, new FileInfo(Path.Combine(outDir, "ragged_cpinv.buf")).Length);
    }

    /// <summary>The (base, width) pairs of a slim part's off buffer, one per part-local bone.</summary>
    [Fact]
    public void A_bone_weighted_heaviest_on_a_thin_band_is_anchored_across_everything_it_carries()
    {
        // The posed vertices a row reads carry their own float rounding, new every frame. Anchors that all
        // lie on a thin band state the bone's motion across the band through the band's thickness alone, so
        // the row hands that rounding to the rest of what the bone carries hundreds of times over, and the
        // replacement shakes. The defect gate cannot see it: such a row is exact on exact input.
        const int band = 300, body = 1500;
        string ad = Path.Combine(_root, "banded");
        SyntheticPool.WriteBandedDump(ad, seed: 5, band: band, body: body, Strong, Weak);
        var p = PoolMath.ParsePositions(File.ReadAllBytes(Path.Combine(ad, "stream0.buf")), 40, 0);
        var (w, bi) = PoolMath.ParseSkin(File.ReadAllBytes(Path.Combine(ad, "stream2.buf")));

        // the fixture bites: the best-weighted anchors alone are inside the defect gate and far over the gain gate
        var onBand = PoolMath.SelectAnchorRows(p, w, bi, 0, 32);
        Assert.All(onBand, v => Assert.True(v < band));
        var (bandRows, bandDefect) = PoolMath.LocalPInvRows(p, w, bi, 0, onBand, 2);
        Assert.True(bandDefect <= 1e-3, $"the band solve's defect is {bandDefect}");
        Assert.True(PoolMath.RowGain(p, w, bi, 0, bandRows) > 64, "the band solve is already quiet");

        string outDir = Path.Combine(_root, "out-banded");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("banded", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["banded"] = "aaaa0021" },
                },
            },
        });

        // what ships: both bones inside the gain gate, on anchors that leave the band
        var off = ReadOff(outDir, "banded");
        var sel = SyntheticPool.ShippedSel(outDir, "banded")!;
        var cp = File.ReadAllBytes(Path.Combine(outDir, "banded_cpinv.buf"));
        for (int b = 0; b < 2; b++)
        {
            var rows = new double[4][];
            for (int r = 0; r < 4; r++)
                rows[r] = Enumerable.Range(0, off[b].Width)
                    .Select(t => (double)BitConverter.ToSingle(cp, (4 * off[b].Base + r * off[b].Width + t) * 4)).ToArray();
            // the shipped coefficients index packet entries or vertices alike for this measure: it reads the rows alone
            double gain = PoolMath.RowGain(p, w, bi, b, rows);
            Assert.True(gain <= 64, $"bone {b} ships multiplying its input's rounding {gain} times");
        }
        Assert.Contains(Enumerable.Range(0, off[0].Width), t => sel[off[0].Base + t] >= band);
        Assert.True(off[0].Width <= 256, $"the bone's block is {off[0].Width} wide");

        // and it is still an exact recovery: every vertex of the part lands where the game posed it
        var miss = PosedPoolSim.OwnVertexMiss(outDir, "swap", "banded", ad);
        Assert.True(miss.Max() < 1e-3f, $"a vertex of the part's own lands {miss.Max()} from where the game posed it");
    }

    [Fact]
    public void A_replacement_leaning_harder_on_a_bone_than_the_part_does_is_named_in_the_build_log()
    {
        // The gain gate holds a bone's rows on the vertices the PART carries, at the part's weights. A bone
        // the part leans on by half a percent is recovered through coefficients two hundred times its
        // motion, which the part's own weights scale back down; a replacement weighted to it in full gets
        // them whole. The build log measures the replacement's vertices and names the bone.
        string ad = Path.Combine(_root, "faint");
        SyntheticPool.WriteFaintBoneDump(ad, seed: 3, faint: 200, plain: 400, share: 0.005f, Strong, Weak);
        MigotoEmitter.Result Build(string tag, int donorRow)
        {
            string donor = Path.Combine(_root, $"faint-donor-{tag}");
            SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2);
            SyntheticPool.WeightDonorOn(donor, donorRow);
            return new MigotoEmitter().Build(new PoolBuildRequest
            {
                OutDir = Path.Combine(_root, $"out-faint-{tag}"),
                Pipelines = new[]
                {
                    new ReplacePipeline
                    {
                        Suffix = "swap",
                        Parts = new[] { new PoolPart("faint", ad) },
                        DonorDir = donor,
                        CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["faint"] = "aaaa0031" },
                    },
                },
            });
        }

        var onFaint = Build("b", donorRow: 1);
        Assert.DoesNotContain(onFaint.Diagnostics, d => d.Contains("tied rigidly"));   // the bone is recovered
        var line = Assert.Single(onFaint.Diagnostics, d => d.Contains("more than 64 times their input's rounding"));
        Assert.Contains($"mostly through bone 0x{Weak:x8}", line);

        // the same part under a replacement weighted to the bone it carries in full says nothing
        Assert.DoesNotContain(Build("a", donorRow: 0).Diagnostics, d => d.Contains("times their input's rounding"));
    }

    [Theory]
    // inside the defect gate beats outside it, whatever the gains
    [InlineData(5e-4, 900.0, 2e-3, 1.0, true)]
    [InlineData(2e-3, 1.0, 5e-4, 900.0, false)]
    // two inside it compare by gain, and a tie keeps the incumbent
    [InlineData(9e-4, 3.0, 1e-6, 40.0, true)]
    [InlineData(1e-6, 40.0, 9e-4, 3.0, false)]
    [InlineData(1e-6, 3.0, 1e-6, 3.0, false)]
    // inside the gain gate beats outside it, by however little
    [InlineData(1e-6, 60.0, 1e-6, 70.0, true)]
    [InlineData(1e-6, 70.0, 1e-6, 60.0, false)]
    // two outside the gain gate change hands only for a quarter less rounding
    [InlineData(1e-6, 200.0, 1e-6, 250.0, false)]
    [InlineData(1e-6, 150.0, 1e-6, 250.0, true)]
    // two outside it compare by defect
    [InlineData(0.01, 900.0, 0.5, 1.0, true)]
    // a NaN never wins
    [InlineData(double.NaN, 1.0, 0.5, 1.0, false)]
    [InlineData(1e-6, double.NaN, 1e-6, 40.0, false)]
    public void The_better_of_two_solves_is_the_exact_one_then_the_quieter_one(double defectA, double gainA,
        double defectB, double gainB, bool aIsBetter) =>
        Assert.Equal(aIsBetter, MigotoEmitter.BetterSolve(defectA, gainA, defectB, gainB));

    private static (int Base, int Width)[] ReadOff(string outDir, string part)
    {
        var raw = ReadUints(outDir, $"{part}_off.buf");
        var off = new (int, int)[raw.Length / 2];
        for (int b = 0; b < off.Length; b++) off[b] = ((int)raw[2 * b], (int)raw[2 * b + 1]);
        return off;
    }

    private static uint[] ReadUints(string outDir, string file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(outDir, file));
        var v = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }

    private static (double[,] P, double[,] W, int[,] BI) LoadDump(string dir)
    {
        var p = PoolMath.ParsePositions(File.ReadAllBytes(Path.Combine(dir, "stream0.buf")), 40, 0);
        var (w, bi) = PoolMath.ParseSkin(File.ReadAllBytes(Path.Combine(dir, "stream2.buf")));
        return (p, w, bi);
    }

    [Fact]
    public void A_tied_tier_bone_is_not_sentineled()
    {
        string ad = Path.Combine(_root, "alpha");
        SyntheticPool.WriteCoWeightedDump(ad, Strong, Weak);
        string td = Path.Combine(_root, "alpha_l1");
        SyntheticPool.WriteCoWeightedDump(td, Strong, Weak, strongVerts: 16);
        string outDir = Path.Combine(_root, "out");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                    Tiers = new[] { new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002") },
                },
            },
        });

        // both union slots stay live in the tier scatter — the weak bone recovers via its tie
        var map = File.ReadAllBytes(Path.Combine(outDir, "alpha_lod1_map_swap.buf"));
        Assert.NotEqual(PoolMath.Sentinel, BitConverter.ToUInt32(map, 0));
        Assert.NotEqual(PoolMath.Sentinel, BitConverter.ToUInt32(map, 4));
    }
}
