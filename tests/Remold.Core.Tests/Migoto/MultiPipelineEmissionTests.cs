using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Multi-Replace emission: two pipelines whose pools OVERLAP on one part — the normal shape, since a donor
/// rides neighbour parts' bones. The shared part is captured in exactly ONE section serving both (skip = OR
/// of their suppression), its operator files exist once and its scatter map per pipeline, and each pipeline
/// gets its own complete chain at its own anchor.
/// </summary>
public class MultiPipelineEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-multi-" + Guid.NewGuid().ToString("N"));

    public MultiPipelineEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>alpha + shared under pipeline <c>a</c> (anchor alpha); shared + beta under <c>b</c> (anchor
    /// beta). <c>b</c> leaves shared running while <c>a</c> suppresses it, so the merged capture must
    /// still skip.</summary>
    private PoolBuildRequest Request(out string outDir, IReadOnlyList<string>? hides = null)
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 4, new uint[] { 101, 102 });
        string sd = Path.Combine(_root, "shared"); SyntheticPool.WritePartDump(sd, 2, 4, new uint[] { 102, 201 });
        string bd = Path.Combine(_root, "beta"); SyntheticPool.WritePartDump(bd, 3, 4, new uint[] { 301, 302 });
        outDir = Path.Combine(_root, "out");
        return new PoolBuildRequest
        {
            OutDir = outDir,
            HideHashes = hides,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "a",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("shared", sd) },
                    Anchor = "alpha",
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001", ["shared"] = "cccc0001" },
                },
                new ReplacePipeline
                {
                    Suffix = "b",
                    Parts = new[] { new PoolPart("shared", sd), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    CaptureHashes = new Dictionary<string, string> { ["shared"] = "cccc0001", ["beta"] = "bbbb0001" },
                    NoSkipParts = new[] { "shared" },
                },
            },
        };
    }

    [Fact]
    public void A_shared_part_gets_one_merged_capture_serving_both_pipelines()
    {
        new MigotoEmitter().Build(Request(out string outDir));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        // exactly one section carries the shared hash
        Assert.Single(Regex.Matches(ini, @"^hash = cccc0001$", RegexOptions.Multiline));
        int cap = ini.IndexOf("[TextureOverride_Cap_shared]", StringComparison.Ordinal);
        Assert.True(cap >= 0);
        string section = ini[cap..ini.IndexOf("\n\n", cap, StringComparison.Ordinal)];
        // captured once, for both pipelines to read
        Assert.Single(Regex.Matches(section, Regex.Escape("Resource_shared_Posed = ref vb0")));
        Assert.Single(Regex.Matches(section, Regex.Escape("Resource_shared_CB = copy vs-cb1")));
        // pipeline a suppresses shared, pipeline b leaves it — suppression wins in the merged section
        Assert.Contains("handling = skip", section);
        // shared anchors neither pipeline: no chain runs here
        Assert.DoesNotContain("run = CommandListDraw", section);

        // each pipeline's chain fires at its own anchor
        int capA = ini.IndexOf("[TextureOverride_Cap_alpha]", StringComparison.Ordinal);
        string secA = ini[capA..ini.IndexOf("\n\n", capA, StringComparison.Ordinal)];
        Assert.Contains("run = CustomShaderRecover_alpha_a", secA);
        Assert.Contains("run = CustomShaderRecover_shared_a", secA);
        Assert.Contains("run = CommandListDraw_a", secA);
        Assert.DoesNotContain("CommandListDraw_b", secA);

        int capB = ini.IndexOf("[TextureOverride_Cap_beta]", StringComparison.Ordinal);
        string secB = ini[capB..ini.IndexOf("\n\n", capB, StringComparison.Ordinal)];
        Assert.Contains("run = CustomShaderRecover_shared_b", secB);
        Assert.Contains("run = CustomShaderRecover_beta_b", secB);
        Assert.Contains("run = CommandListDraw_b", secB);

        // per-pipeline recover sections read the shared capture but their own palette + map
        Assert.Contains("[CustomShaderRecover_shared_a]\ncs = recover_shared_cs.hlsl\n"
                      + "cs-u1 = copy Resource_Palette_a\ncs-t0 = copy Resource_shared_Posed\n"
                      + "cs-t1 = Resource_shared_Cpinv\ncs-t2 = Resource_shared_Map_a\n", ini);
        Assert.Contains("[CustomShaderRecover_shared_b]\ncs = recover_shared_cs.hlsl\n"
                      + "cs-u1 = copy Resource_Palette_b\ncs-t0 = copy Resource_shared_Posed\n"
                      + "cs-t1 = Resource_shared_Cpinv\ncs-t2 = Resource_shared_Map_b\n", ini);

        // shared operator files once; scatter maps per pipeline; chains per pipeline
        var files = Directory.GetFiles(outDir).Select(Path.GetFileName).ToHashSet();
        foreach (var f in new[]
        {
            "shared_cpinv.buf", "recover_shared_cs.hlsl", "shared_map_a.buf", "shared_map_b.buf",
            "palette_seed_a.buf", "palette_seed_b.buf", "owner_part_a.buf", "owner_part_b.buf",
            "convert_cs_a.hlsl", "convert_cs_b.hlsl", "skin_cs_a.hlsl", "skin_cs_b.hlsl",
            "union_a.json", "union_b.json", "combined_ib_a.buf", "combined_ib_b.buf",
        })
            Assert.Contains(f, files);
        // resource declarations for the shared part exist exactly once — the slim pair included: a second
        // declaration would be a second [Resource] block over the same file, silently shadowing the first
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Resource_shared_Cpinv]")));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Resource_shared_Sel]")));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Resource_shared_Off]")));
        // but the BINDS are per pipeline: each chain reads the shared anchors through its own recover
        Assert.Equal(2, Regex.Matches(ini, Regex.Escape("cs-t3 = Resource_shared_Sel")).Count);
        Assert.Equal(2, Regex.Matches(ini, Regex.Escape("cs-t4 = Resource_shared_Off")).Count);
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Resource_shared_Posed]")));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Resource_shared_CB]")));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Resource_shared_Map_a]")));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Resource_shared_Map_b]")));
    }

    [Fact]
    public void A_hide_hash_that_is_also_a_capture_hash_fails_loudly()
    {
        var req = Request(out _, hides: new[] { "cccc0001" });
        var e = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(req));
        Assert.Contains("also a pipeline capture hash", e.Message);
    }

    [Fact]
    public void Duplicate_suffixes_fail_loudly()
    {
        var req = Request(out _);
        req = req with { Pipelines = req.Pipelines.Select(p => p with { Suffix = "same" }).ToList() };
        var e = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(req));
        Assert.Contains("unique", e.Message);
    }

    [Fact]
    public void Two_builds_of_one_request_emit_the_same_bytes_and_the_same_diagnostic_order()
    {
        // Operators are solved off the emission thread, so nothing about which one finishes first may reach
        // the output: every emitted file and the diagnostic SEQUENCE are properties of the request alone.
        var first = new MigotoEmitter().Build(Request(out string outA));
        var req = Request(out string outB);
        var second = new MigotoEmitter().Build(req with { OutDir = outB + "2" });

        Assert.Equal(first.Diagnostics, second.Diagnostics);
        var names = Directory.GetFiles(outA).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(names, Directory.GetFiles(second.OutDir).Select(Path.GetFileName).Order().ToArray());
        foreach (var n in names)
            Assert.Equal(File.ReadAllBytes(Path.Combine(outA, n!)),
                         File.ReadAllBytes(Path.Combine(second.OutDir, n!)));
    }

    /// <summary>One physical mesh reached through two outfits can be keyed on its vb1 in one signature index
    /// and on its ib in the other, so one part name arrives carrying two hashes. Each needs its own capture
    /// section, and two sections under one name would leave the second dropped at parse time — the pipeline
    /// behind it capturing nothing and posing its palette from garbage.</summary>
    [Fact]
    public void One_part_name_over_two_hashes_gets_two_distinctly_named_capture_sections()
    {
        var req = Request(out string outDir);
        var pipes = req.Pipelines.ToList();
        pipes[1] = pipes[1] with
        {
            CaptureHashes = new Dictionary<string, string> { ["shared"] = "dddd0001", ["beta"] = "bbbb0001" },
        };
        new MigotoEmitter().Build(req with { Pipelines = pipes });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        var headers = Regex.Matches(ini, @"^\[TextureOverride_[^\]]+\]$", RegexOptions.Multiline)
            .Select(m => m.Value).ToList();
        Assert.NotEmpty(headers);
        Assert.Equal(headers.Distinct(StringComparer.Ordinal).Count(), headers.Count);
        // both spellings of the shared mesh keep a section, so both pipelines capture
        Assert.Single(Regex.Matches(ini, @"^hash = cccc0001$", RegexOptions.Multiline));
        Assert.Single(Regex.Matches(ini, @"^hash = dddd0001$", RegexOptions.Multiline));
    }

    /// <summary>An anchor the pool doesn't carry is refused before anything is emitted, naming the anchor
    /// asked for.</summary>
    [Fact]
    public void An_anchor_that_is_not_a_pool_part_fails_loudly()
    {
        var req = Request(out _);
        var pipes = req.Pipelines.ToList();
        pipes[0] = pipes[0] with { Anchor = "ghost" };
        var e = Assert.Throws<InvalidOperationException>(
            () => new MigotoEmitter().Build(req with { Pipelines = pipes }));
        Assert.Contains("anchor 'ghost' is not a pool part", e.Message);
    }

    [Fact]
    public void One_part_name_over_two_different_dumps_fails_loudly()
    {
        var req = Request(out _);
        // point pipeline b's "shared" at a DIFFERENT dump than pipeline a's
        string sd2 = Path.Combine(_root, "shared2");
        SyntheticPool.WritePartDump(sd2, 9, 4, new uint[] { 102, 201 });
        var pipes = req.Pipelines.ToList();
        pipes[1] = pipes[1] with
        {
            Parts = new[] { new PoolPart("shared", sd2), pipes[1].Parts[1] },
        };
        var e = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(req with { Pipelines = pipes }));
        Assert.Contains("different dumps", e.Message);
    }
}
