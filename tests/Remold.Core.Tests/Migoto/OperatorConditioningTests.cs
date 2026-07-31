using System;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Operator conditioning: a bone whose vertex support can't determine its palette row is tied rigidly to a
/// sound co-riding bone. The alternatives are a min-norm estimate (distorts donor geometry) or a sentinel to
/// its lod0 row (lives in the lod0 draw's SPACE, so it displaces in a two-placement frame). Sentinel remains
/// only when the mesh has no sound bone at all.
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

        // Both the 4 slim rows AND the anchor-vertex segment are byte-copies: coefficients are meaningless
        // without the vertices they index, so a tie must carry both.
        var cp = File.ReadAllBytes(Path.Combine(outDir, "alpha_cpinv.buf"));
        var sel = File.ReadAllBytes(Path.Combine(outDir, "alpha_sel.buf"));
        int k = sel.Length / 4 / 2;                 // 2 bones × K uint indices
        Assert.Equal(2 * 4 * k * 4, cp.Length);     // 4 rows of K floats per bone
        int rowBytes = k * 4;
        for (int r = 0; r < 4; r++)
        {
            var strongRow = cp.Skip((0 * 4 + r) * rowBytes).Take(rowBytes).ToArray();
            var weakRow = cp.Skip((1 * 4 + r) * rowBytes).Take(rowBytes).ToArray();
            Assert.Equal(strongRow, weakRow);
        }
        Assert.Equal(sel.Take(k * 4).ToArray(), sel.Skip(k * 4).Take(k * 4).ToArray());
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
        var sel = new FileInfo(Path.Combine(outDir, "big_sel.buf"));
        int k = (int)(sel.Length / 4 / 2);
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

        Assert.True(File.Exists(Path.Combine(outDir, "reported_sel.buf")), "the part failed to slim");
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
        var sel = new FileInfo(Path.Combine(outDir, "size_sel.buf"));
        var off = new FileInfo(Path.Combine(outDir, "size_off.buf"));
        Assert.Equal(slims, sel.Exists);
        Assert.Equal(slims, off.Exists);
        if (slims) Assert.True(cp.Length + sel.Length + off.Length < 4L * 1 * verts * 4);
        else Assert.Equal(4L * 1 * verts * 4, cp.Length);
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("slimming declined"));

        // The buffers are only half the verdict: the shader and the ini have to agree with them, or the
        // part ships dense rows behind a shader that indexes them through anchors that were never written.
        string hlsl = File.ReadAllText(Path.Combine(outDir, "recover_size_cs.hlsl"));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        if (!slims)
        {
            Assert.Contains($"static const uint N={verts}", hlsl);
            Assert.DoesNotContain("Sel", hlsl);
            Assert.DoesNotContain("Off", hlsl);
            Assert.DoesNotContain("[Resource_size_Sel]", ini);
            Assert.DoesNotContain("[Resource_size_Off]", ini);
            Assert.DoesNotContain("cs-t3", ini);
            Assert.DoesNotContain("cs-t4", ini);
        }
        else
        {
            Assert.DoesNotContain("static const uint N=", hlsl);
            Assert.Contains("[Resource_size_Sel]", ini);
            Assert.Contains("cs-t4 = Resource_size_Off", ini);
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
        Assert.True(File.Exists(Path.Combine(outDir, "unridable_sel.buf")), "the part failed to slim");

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
        var sel = new FileInfo(Path.Combine(outDir, "blend_sel.buf"));
        int k = (int)(sel.Length / 4 / 40);
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
        Assert.True(File.Exists(Path.Combine(outDir, "smallsel_sel.buf")), "the part failed to slim");
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
        Assert.True(File.Exists(Path.Combine(outDir, "trace_sel.buf")), "the part failed to slim");
        var diag = Assert.Single(result.Diagnostics, w => w.Contains("slim operator ships"));
        double worst = double.Parse(
            System.Text.RegularExpressions.Regex.Match(diag, @"worst defect (\S+) ").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(worst <= 1e-3, $"the shipped rows carry a defect of {worst}");

        // The number above excludes bones that widened to the vertex count, so on its own it would stay
        // green while the trace columns pushed bones off the narrow solve one by one. Both escape hatches
        // are shut here: none may widen, and the shipped selection stays a slim multiple of the bone count.
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("dense width"));
        long selRows = new FileInfo(Path.Combine(outDir, "trace_sel.buf")).Length / 4;
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
            + new FileInfo(Path.Combine(outDir, "pair_sel.buf")).Length
            + new FileInfo(Path.Combine(outDir, "pair_off.buf")).Length;
        Assert.True(slim < 16L * 2 * n, $"the ragged operator ({slim}B) did not undercut dense ({16 * 2 * n}B)");

        string hlsl = File.ReadAllText(Path.Combine(outDir, "recover_pair_cs.hlsl"));
        Assert.Contains("Off    : register(t4)", hlsl);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.Contains("[Resource_pair_Off]", ini);
        Assert.Contains("cs-t4 = Resource_pair_Off", ini);
    }

    [Fact]
    public void A_bone_no_selection_can_separate_ships_at_dense_width_alone()
    {
        // Proportional over every vertex it can anchor on, and no vertex carries its neighbour without it —
        // neither escalation nor discriminators reach a selection that separates the two. That bone takes
        // every vertex, and ONLY that bone: the part still slims around it.
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

        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("slimming declined"));
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("tied rigidly"));   // both bones are dense-sound
        var off = ReadOff(outDir, "wide");
        var (badBase, badWidth) = off[1];
        var (_, goodWidth) = off[0];
        Assert.Equal(n, badWidth);
        Assert.True(goodWidth < n, $"the sound bone widened too ({goodWidth} of {n})");

        // a dense-width bone ships the identity selection — no implicit special case for the shader to know
        var sel = ReadUints(outDir, "wide_sel.buf");
        for (int t = 0; t < n; t++) Assert.Equal((uint)t, sel[badBase + t]);
        Assert.Contains(result.Diagnostics, w => w.Contains($"wide: bone 0x{Weak:x8} ships at dense width")
            && w.Contains($"{n} rows"));

        // and the whole point: the part is still smaller than shipping dense for everyone
        long slim = new FileInfo(Path.Combine(outDir, "wide_cpinv.buf")).Length
            + new FileInfo(Path.Combine(outDir, "wide_sel.buf")).Length
            + new FileInfo(Path.Combine(outDir, "wide_off.buf")).Length;
        Assert.True(slim < 16L * 2 * n, $"the ragged operator ({slim}B) did not undercut dense ({16 * 2 * n}B)");
    }

    [Fact]
    public void A_tie_onto_a_dense_width_bone_names_the_width_it_inherited()
    {
        // A tie copies its target's anchor segment, so riding a bone that took every vertex costs the tied
        // bone 20·n bytes of its own. "tied rigidly" alone reads like the cheap outcome it usually is, and
        // the dense-width line names only the bone that widened — the rider goes unaccounted for.
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

        Assert.True(File.Exists(Path.Combine(outDir, "tiedwide_sel.buf")), "the part failed to slim");
        var tieDiag = Assert.Single(result.Diagnostics, w => w.Contains("tied rigidly"));
        Assert.Contains($"0x{103:x8}", tieDiag);                       // the rider
        Assert.Contains($"0x{Weak:x8}", tieDiag);                      // the bone it rides, which widened
        Assert.Contains($"at dense width ({n} rows)", tieDiag);

        var off = ReadOff(outDir, "tiedwide");
        Assert.Equal(n, off[1].Width);                                 // the ride target
        Assert.Equal(n, off[2].Width);                                 // and the rider, at the same cost
        Assert.True(off[0].Width < n, "the sound bone widened too");
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
    public void A_dense_width_bone_ships_the_dense_operators_own_rows()
    {
        // A bone that widens to every vertex takes the DENSE operator's rows — the ones the residual gate is
        // calibrated against. They reach the buffer through a per-row read of the factors rather than the
        // whole materialized matrix, and a disagreement between those two routes would ship a bone whose
        // coefficients no gate ever measured.
        const int mixed = 1100, skew = 200, n = mixed + skew;
        string ad = Path.Combine(_root, "denserows");
        SyntheticPool.WriteProportionalPairDump(ad, seed: 7, mixed: mixed, skew: skew, Strong, Weak);
        string outDir = Path.Combine(_root, "out-denserows");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("denserows", ad) },
                    CaptureHashes = new System.Collections.Generic.Dictionary<string, string> { ["denserows"] = "aaaa00fd" },
                },
            },
        });

        var off = ReadOff(outDir, "denserows");
        var (bas, width) = off[1];
        Assert.Equal(n, width);          // the bone that could not slim

        var (p, w, bi) = LoadDump(ad);
        var dense = PoolMath.PInv(PoolMath.BuildC(p, w, bi, nbones: 2));
        var shipped = ReadFloats(outDir, "denserows_cpinv.buf");
        for (int r = 0; r < 4; r++)
            for (int t = 0; t < n; t++)
                Assert.Equal(dense[(4 * 1 + r) * n + t], shipped[4 * bas + r * width + t]);
    }

    private static float[] ReadFloats(string outDir, string file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(outDir, file));
        var v = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
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
        Assert.Equal(expected * 4, new FileInfo(Path.Combine(outDir, "ragged_sel.buf")).Length);
        Assert.Equal(expected * 16, new FileInfo(Path.Combine(outDir, "ragged_cpinv.buf")).Length);
    }

    /// <summary>The (base, width) pairs of a slim part's off buffer, one per part-local bone.</summary>
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
