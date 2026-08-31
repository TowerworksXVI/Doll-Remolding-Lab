using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The toon ramp's own emission: its tag, its probe over its own candidate registers, its draw-scoped
/// bind, and — just as load-bearing — the fact that a build shipping no ramp emits none of it.
/// </summary>
public class RampEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-ramp-emit-" + Guid.NewGuid().ToString("N"));

    public RampEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly IReadOnlyList<int> StockSlots = ShaderSlotPlan.Shipped.StockMaps;
    private static readonly IReadOnlyList<int> RampSlots = ShaderSlotPlan.Shipped.Ramp;

    private const string RampHash = "a1a1b2b2";

    /// <summary>Build one pooled Replace over synthetic geometry. <paramref name="ramp"/> ships a ramp on
    /// submesh 0; <paramref name="picture"/> ships an albedo there too, so the two economies can be asked
    /// about apart from each other.</summary>
    private string Emit(bool ramp, bool picture, string tag = "run")
    {
        string dump = Path.Combine(_root, $"part-{tag}");
        SyntheticPool.WritePartDump(dump, seed: 3, verts: 32, boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, $"donor-{tag}");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2);

        var slots = new SubmeshMaps();
        var tags = new List<StockMapTag>();
        if (picture)
        {
            string albedo = Path.Combine(_root, "sub0.dds");
            FlatDds.Write(albedo, (10, 220, 90, 255));
            slots = slots with { Albedo = MapSlot.From(albedo) };
            tags.Add(new StockMapTag("f1f1a1a1", StockMapKind.Albedo));
        }
        if (ramp)
        {
            string rampDds = Path.Combine(_root, "sub0_ramp.dds");
            WriteRamp(rampDds);
            slots = slots with { Ramp = MapSlot.From(rampDds) };
            tags.Add(new StockMapTag(RampHash, StockMapKind.Ramp));
        }

        string outDir = Path.Combine(_root, $"out-{tag}");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", dump) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                    SubTextures = slots.BindsNothing
                        ? null
                        : new Dictionary<int, SubmeshMaps> { [0] = slots },
                    StockMaps = tags.Count > 0 ? tags : null,
                },
            },
        });
        return File.ReadAllText(Path.Combine(outDir, "mod.ini"));
    }

    /// <summary>A 256x16 fp16 ramp, written through the same container a shipped one goes out in.</summary>
    private static void WriteRamp(string path)
    {
        var px = new byte[256 * 16 * 8];
        for (int i = 0; i < 256 * 16 * 4; i++)
            BitConverter.TryWriteBytes(px.AsSpan(i * 2, 2), (Half)((i % 41) / 40f));
        using var s = File.Create(path);
        DdsWriter.Write(s, DdsWriter.R16G16B16A16_FLOAT, 256, 16, new[] { px });
    }

    // ---- what a ramp emits -------------------------------------------------------------------------

    [Fact]
    public void The_anchors_ramp_is_tagged_with_its_own_filter_index()
    {
        string ini = Emit(ramp: true, picture: true);
        Assert.Contains($"[TextureOverride_SlotTag_{RampHash}]\nhash = {RampHash}\n"
            + "filter_index = 3304\nmatch_priority = 100\n", ini);
    }

    /// <summary>The ramp sweeps ITS registers, not the picture maps' — the measurement puts it as far out
    /// as t12, well past where any stock map binds.</summary>
    [Fact]
    public void The_ramp_probes_its_own_registers_and_accepts_only_its_own_tag()
    {
        string ini = Emit(ramp: true, picture: true);
        string list = DrawList(ini);

        Assert.Contains("$zz_slot_rm = -1\n", list);
        foreach (int s in RampSlots)
            Assert.Contains($"$zz_t = ps-t{s}\nif $zz_t == 3304\n$zz_slot_rm = {s}\nendif\n", list);
        // a register outside the ramp's range is never asked about for a ramp
        foreach (int s in Enumerable.Range(0, 32).Except(RampSlots))
            Assert.DoesNotContain($"$zz_t = ps-t{s}\nif $zz_t == 3304\n", list);
    }

    [Fact]
    public void The_ramp_binds_at_whichever_register_the_probe_found_it_in()
    {
        string ini = Emit(ramp: true, picture: true);
        string list = DrawList(ini);

        foreach (int s in RampSlots)
            Assert.Contains($"if $zz_slot_rm == {s}\nps-t{s} = ", list);
        // draw-scoped only: no global rebind of the ramp resource anywhere in the ini
        Assert.DoesNotContain("this = Resource_Tex", ini);
    }

    [Fact]
    public void A_ramp_build_saves_and_restores_the_union_of_both_ranges_once_each()
    {
        string ini = Emit(ramp: true, picture: true);
        var union = StockSlots.Concat(RampSlots).Distinct().OrderBy(s => s).ToList();

        foreach (int s in union)
        {
            // one declaration and one save per register: the two ranges overlap, and a second section on
            // one name would be dropped at parse time
            Assert.Equal(1, Count(ini, $"[Resource_SaveT{s}]\n"));
            Assert.Equal(1, Count(ini, $"Resource_SaveT{s} = ref ps-t{s}\n"));
            // restored at the end of the list, and wherever a later submesh takes its slot back
            Assert.True(Count(ini, $"ps-t{s} = Resource_SaveT{s}\n") >= 1);
        }
        // and nothing past the union
        Assert.DoesNotContain($"[Resource_SaveT{union[^1] + 1}]", ini);
        Assert.Contains("global $zz_slot_rm = 0\n", ini);
    }

    /// <summary>A ramp alone sweeps and saves only the ramp's registers: the picture maps' probe is a cost
    /// a build that binds no picture map does not pay.</summary>
    [Fact]
    public void A_ramp_with_no_picture_map_emits_no_stock_probe()
    {
        string ini = Emit(ramp: true, picture: false);

        Assert.Contains("$zz_slot_rm = -1\n", ini);
        Assert.DoesNotContain("$zz_slot_a = -1\n", ini);
        Assert.DoesNotContain("if $zz_t == 3301\n", ini);
        foreach (int s in StockSlots.Except(RampSlots))
            Assert.DoesNotContain($"[Resource_SaveT{s}]", ini);
    }

    // ---- what a build with no ramp emits: nothing at all -------------------------------------------

    [Fact]
    public void A_build_that_ships_no_ramp_carries_no_ramp_text_anywhere()
    {
        foreach (string ini in new[] { Emit(false, picture: true, tag: "tex"), Emit(false, false, "bare") })
        {
            Assert.DoesNotContain("3304", ini);
            Assert.DoesNotContain("zz_slot_rm", ini);
            foreach (int s in RampSlots.Except(StockSlots))
            {
                Assert.DoesNotContain($"ps-t{s}", ini);
                Assert.DoesNotContain($"Resource_SaveT{s}", ini);
            }
        }
    }

    /// <summary>Two builds of one input write the same text: the emission has no order that depends on a
    /// dictionary walk or a file-system listing.</summary>
    [Fact]
    public void Two_builds_of_one_ramped_input_emit_identical_text()
    {
        Assert.Equal(Emit(ramp: true, picture: true, tag: "once"),
                     Emit(ramp: true, picture: true, tag: "twice"));
    }

    [Fact]
    public void A_ramp_asking_for_the_flat_map_is_refused_rather_than_blanked()
    {
        string dump = Path.Combine(_root, "part-neutral");
        SyntheticPool.WritePartDump(dump, seed: 3, verts: 32, boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "donor-neutral");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2);

        var ex = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = Path.Combine(_root, "out-neutral"),
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", dump) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                    SubTextures = new Dictionary<int, SubmeshMaps> { [0] = new() { Ramp = MapSlot.Neutral } },
                },
            },
        }));
        Assert.Contains("neutral ramp", ex.Message);
    }

    /// <summary>A ROUTED pipeline's per-range lists each carry the ramp probe and their own submesh's
    /// bind: the list runs alone at its matched draw, so it cannot lean on a neighbour's sweep — the
    /// probe folds into every per-range list exactly as it sits in the full one.</summary>
    [Fact]
    public void A_routed_pipelines_per_range_lists_each_carry_the_ramp_probe()
    {
        string dump = Path.Combine(_root, "part-routed");
        SyntheticPool.WritePartDump(dump, seed: 3, verts: 32, boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "donor-routed");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2, submeshes: 2);
        string rampDds = Path.Combine(_root, "routed_ramp.dds");
        WriteRamp(rampDds);

        string outDir = Path.Combine(_root, "out-routed");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", dump) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                    SubTextures = new Dictionary<int, SubmeshMaps>
                    {
                        [0] = new() { Ramp = MapSlot.From(rampDds) },
                    },
                    StockMaps = new[] { new StockMapTag(RampHash, StockMapKind.Ramp) },
                    AnchorShapes = new DrawShapeSet(new[] { new DrawShape(0, 9), new DrawShape(9, 9) }, 18),
                },
            },
        });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);

        foreach (string list in new[] { RangeList(ini, 0), RangeList(ini, 1) })
        {
            Assert.Contains("$zz_slot_rm = -1\n", list);
            foreach (int s in RampSlots)
                Assert.Contains($"$zz_t = ps-t{s}\nif $zz_t == 3304\n$zz_slot_rm = {s}\nendif\n", list);
            Assert.Equal(1, Count(list, "drawindexed = "));
        }
        // range 0 binds its authored ramp; range 1 inherits, and alone in its list it binds nothing
        Assert.Contains("ps-t", RangeList(ini, 0)[RangeList(ini, 0).IndexOf("$zz_slot_rm =", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("= Resource_Tex", RangeList(ini, 1));
    }

    private static string RangeList(string ini, int di)
    {
        int start = ini.IndexOf($"[CommandListDrawS{di}_swap]", StringComparison.Ordinal);
        Assert.True(start >= 0, $"per-range list S{di} missing");
        int end = ini.IndexOf("\n[", start + 1, StringComparison.Ordinal);
        return end < 0 ? ini[start..] : ini[start..end];
    }

    private static string DrawList(string ini) =>
        ini[ini.IndexOf("[CommandListDraw_swap]", StringComparison.Ordinal)..];

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
