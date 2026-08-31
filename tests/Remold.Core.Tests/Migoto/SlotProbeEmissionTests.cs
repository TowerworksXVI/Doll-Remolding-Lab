using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The slot-probe emission a Replace build carries instead of a pixel-shader pass table: each stock map of
/// the anchor gets a <c>[TextureOverride]</c> tag with a per-kind <c>filter_index</c>, and the draw list
/// probes the registers the build's slot plan names for those indices AT THE DRAW. The bound state is
/// final by draw time; a
/// variable written by a PS-keyed section is one listed draw stale, because ShaderOverride lists run VS
/// before PS and the draw fires from the VS phase.
/// </summary>
public class SlotProbeEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-slotprobe-" + Guid.NewGuid().ToString("N"));

    public SlotProbeEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (string Ini, IReadOnlyList<string> Warnings, IReadOnlyList<string> Diagnostics) Emit(
        IReadOnlyList<StockMapTag>? stockMaps, bool donorTexed, IReadOnlyList<RetexEntry>? retex = null,
        bool donorNormal = false, IReadOnlyDictionary<int, SubmeshMaps>? subTextures = null,
        IReadOnlyList<StockPropertyTag>? stockProperties = null)
    {
        string dump = Path.Combine(_root, "alpha-" + Guid.NewGuid().ToString("N")[..8]);
        SyntheticPool.WritePartDump(dump, seed: 3, verts: 32, boneHashes: new uint[] { 101, 102 });
        string? donor = null, tex = null, nrm = null;
        if (donorTexed)
        {
            donor = Path.Combine(_root, "donor-" + Guid.NewGuid().ToString("N")[..8]);
            SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2);
            tex = Path.Combine(_root, "sub0.dds");
            FlatDds.Write(tex, (10, 220, 90, 255));
            if (donorNormal)
            {
                nrm = Path.Combine(_root, "donor_n.dds");
                FlatDds.Write(nrm, (128, 128, 255, 255));
            }
        }
        string outDir = Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N")[..8]);
        var result = new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Retextures = retex,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", dump) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                    SubTextures = subTextures ?? (tex is null ? null : new Dictionary<int, SubmeshMaps>
                    {
                        // submesh 0 authored, submesh 1 left untouched
                        [0] = new(MapSlot.From(tex),
                            nrm is null ? MapSlot.Neutral : MapSlot.From(nrm), MapSlot.Neutral),
                    }),
                    StockMaps = stockMaps,
                    StockProperties = stockProperties,
                },
            },
        });
        return (File.ReadAllText(Path.Combine(outDir, "mod.ini")), result.Warnings, result.Diagnostics);
    }

    private static readonly StockMapTag[] Tags =
    {
        new("f1f1a1a1", StockMapKind.Albedo),
        new("f2f2b2b2", StockMapKind.Normal),
        new("f3f3c3c3", StockMapKind.Rmo),
    };

    [Fact]
    public void Every_stock_map_gets_a_tag_with_its_kinds_filter_index()
    {
        var (ini, _, _) = Emit(Tags, donorTexed: true);
        Assert.Contains($"[TextureOverride_SlotTag_f1f1a1a1]\nhash = f1f1a1a1\nfilter_index = {MigotoEmitter.FilterAlbedo}\nmatch_priority = 100\n", ini);
        Assert.Contains($"[TextureOverride_SlotTag_f2f2b2b2]\nhash = f2f2b2b2\nfilter_index = {MigotoEmitter.FilterNormal}\nmatch_priority = 100\n", ini);
        Assert.Contains($"[TextureOverride_SlotTag_f3f3c3c3]\nhash = f3f3c3c3\nfilter_index = {MigotoEmitter.FilterRmo}\nmatch_priority = 100\n", ini);
        // no shader table at all: the probe replaced it
        Assert.DoesNotContain("[ShaderOverride", ini);
        Assert.DoesNotContain("$zz_pass", ini);
    }

    [Fact]
    public void The_draw_probes_every_slot_before_binding_and_falls_back_to_geometry_only()
    {
        var (ini, _, _) = Emit(Tags, donorTexed: true);
        int draw = ini.IndexOf("[CommandListDraw_swap]", StringComparison.Ordinal);
        Assert.True(draw > 0);
        string section = ini[draw..];

        // probe: every slot in the range is read into $zz_t before any ps-t assignment, and each kind
        // records its own slot
        for (int s = 0; s <= 6; s++)
        {
            Assert.Contains($"$zz_t = ps-t{s}\n", section);
            Assert.Contains($"if $zz_t == {MigotoEmitter.FilterAlbedo}\n$zz_slot_a = {s}\nendif\n", section);
            Assert.Contains($"if $zz_t == {MigotoEmitter.FilterNormal}\n$zz_slot_n = {s}\nendif\n", section);
            Assert.Contains($"if $zz_t == {MigotoEmitter.FilterRmo}\n$zz_slot_r = {s}\nendif\n", section);
        }
        int firstProbe = section.IndexOf("$zz_t = ps-t0", StringComparison.Ordinal);
        int firstBind = section.IndexOf("ps-t0 = ", StringComparison.Ordinal);
        Assert.True(firstProbe < firstBind, "the probe must read the slots before anything rebinds them");

        // each kind's bind is guarded by that kind's probe answer, so no bind assumes a fixed slot
        for (int s = 0; s <= 6; s++)
        {
            Assert.Contains($"if $zz_slot_a == {s}\nps-t{s} = Resource_Tex0\nendif\n", section);
            Assert.Contains($"if $zz_slot_n == {s}\nps-t{s} = Resource_NeutralN\nendif\n", section);
            Assert.Contains($"if $zz_slot_r == {s}\nps-t{s} = Resource_NeutralRMO\nendif\n", section);
        }
        // a kind no slot holds stays -1, so its binds fall through and the geometry draws anyway
        Assert.Contains("$zz_slot_a = -1\n", section);
        Assert.Contains("drawindexed = ", section);

        // the touched slots are saved and restored around the draw
        for (int s = 0; s <= 6; s++)
        {
            Assert.Contains($"Resource_SaveT{s} = ref ps-t{s}\n", section);
            Assert.Contains($"ps-t{s} = Resource_SaveT{s}\n", section);
        }
    }

    [Fact]
    public void An_untouched_submesh_after_an_authored_one_puts_every_stomped_slot_back()
    {
        // The command list is sequential: a bind outlives the draw that set it, so the second submesh —
        // which authored nothing — has to restore what the first stomped or it draws in its neighbour's maps.
        var (ini, _, _) = Emit(Tags, donorTexed: true);
        string section = ini[ini.IndexOf("[CommandListDraw_swap]", StringComparison.Ordinal)..];
        var chunks = section.Split("drawindexed = ");
        string secondDrawBinds = chunks[1][(chunks[1].IndexOf('\n') + 1)..];

        Assert.Contains("if $zz_slot_a == 0\nps-t0 = Resource_SaveT0\nendif\n", secondDrawBinds);
        Assert.Contains("if $zz_slot_n == 0\nps-t0 = Resource_SaveT0\nendif\n", secondDrawBinds);
        Assert.Contains("if $zz_slot_r == 0\nps-t0 = Resource_SaveT0\nendif\n", secondDrawBinds);
        Assert.DoesNotContain("Resource_Tex0", secondDrawBinds);
    }

    [Fact]
    public void An_authored_normal_binds_at_the_probed_slot_instead_of_the_neutral()
    {
        var (ini, _, _) = Emit(Tags, donorTexed: true, donorNormal: true);
        string section = ini[ini.IndexOf("[CommandListDraw_swap]", StringComparison.Ordinal)..];
        // the normal slot binds the authored map; no neutral normal is asked for, so none is declared
        Assert.Matches(@"if \$zz_slot_n == 0\nps-t0 = Resource_Tex\d+\nendif\n", section);
        Assert.DoesNotContain("Resource_NeutralN", ini);
        // this submesh still asks for the neutral RMO
        Assert.Contains("if $zz_slot_r == 0\nps-t0 = Resource_NeutralRMO\nendif\n", section);
        Assert.Contains("filename = donor_n.dds", ini);
    }

    [Fact]
    public void A_pipeline_without_donor_textures_neither_probes_nor_touches_texture_slots()
    {
        var (ini, _, _) = Emit(stockMaps: null, donorTexed: false);
        Assert.DoesNotContain("$zz_slot_", ini.Substring(ini.IndexOf("[CommandListDraw_swap]", StringComparison.Ordinal)));
        Assert.DoesNotContain("Resource_SaveT", ini);
        Assert.DoesNotContain("[TextureOverride_SlotTag_", ini);
        Assert.Contains("drawindexed = ", ini);
    }

    [Fact]
    public void A_generic_only_replacement_emits_property_probes_without_the_fixed_probe_sweep()
    {
        string generic = Path.Combine(_root, "generic-only.dds");
        FlatDds.Write(generic, (20, 40, 60, 255));
        var properties = new[]
        {
            new PropertyMapSlot("_DetailMask", MapSlot.From(generic), new[] { 6, 7 }),
        };
        var (ini, _, _) = Emit(stockMaps: null, donorTexed: true,
            subTextures: new Dictionary<int, SubmeshMaps>
            {
                [0] = new(Properties: properties),
            },
            stockProperties: new[]
            {
                new StockPropertyTag("f4f4d4d4", "_DetailMask", new[] { 6, 7 }, "alpha"),
            });

        string draw = ini[ini.IndexOf("[CommandListDraw_swap]", StringComparison.Ordinal)..];
        Assert.Contains("$zz_slot_x", draw);
        Assert.DoesNotContain("$zz_slot_a", draw);
        Assert.DoesNotContain("$zz_slot_n", draw);
        Assert.DoesNotContain("$zz_slot_r", draw);
        Assert.DoesNotContain("[TextureOverride_SlotTag_", ini);
        Assert.Contains("[TextureOverride_PropertyTag_f4f4d4d4]", ini);
    }

    [Fact]
    public void Duplicate_tags_collapse_and_a_kind_conflict_is_recorded()
    {
        var dupes = new[]
        {
            new StockMapTag("f1f1a1a1", StockMapKind.Albedo),
            new StockMapTag("f1f1a1a1", StockMapKind.Albedo),      // same hash+kind: silent dedupe
            new StockMapTag("f2f2b2b2", StockMapKind.Normal),
            new StockMapTag("f2f2b2b2", StockMapKind.Rmo),          // conflict: first kind wins, on the record
        };
        var (ini, warnings, diagnostics) = Emit(dupes, donorTexed: true);
        Assert.Equal(1, ini.Split("[TextureOverride_SlotTag_f1f1a1a1]").Length - 1);
        Assert.Contains($"[TextureOverride_SlotTag_f2f2b2b2]\nhash = f2f2b2b2\nfilter_index = {MigotoEmitter.FilterNormal}\nmatch_priority = 100\n", ini);
        // which kind won is the emitter's own bookkeeping, not something the author can act on
        Assert.Contains(diagnostics, d => d.Contains("f2f2b2b2") && d.Contains("Normal") && d.Contains("Rmo"));
        Assert.DoesNotContain(warnings, w => w.Contains("tagged both"));
    }

    /// <summary>Only normal and RMO have a flat map, so a row asking for a neutral base color is a caller
    /// fault. It is refused wherever it appears — including on a submesh index the pipeline doesn't have,
    /// where the range check would otherwise turn the fault into a skipped-row warning.</summary>
    [Fact]
    public void A_neutral_albedo_is_refused_before_the_range_check()
    {
        var inRange = Assert.Throws<InvalidOperationException>(() =>
            Emit(Tags, donorTexed: true,
                subTextures: new Dictionary<int, SubmeshMaps> { [0] = new(MapSlot.Neutral) }));
        Assert.Contains("neutral base color", inRange.Message);

        var outOfRange = Assert.Throws<InvalidOperationException>(() =>
            Emit(Tags, donorTexed: true,
                subTextures: new Dictionary<int, SubmeshMaps> { [99] = new(MapSlot.Neutral) }));
        Assert.Contains("submesh 99 asks for a neutral base color", outOfRange.Message);
    }

    /// <summary>An out-of-range row that asks for nothing impossible still degrades to a warning: the
    /// geometry swap is worth more than the row.</summary>
    [Fact]
    public void An_out_of_range_row_asking_for_a_bindable_map_is_skipped_with_a_diagnostic()
    {
        string dds = Path.Combine(_root, "far.dds");
        FlatDds.Write(dds, (5, 5, 5, 255));
        var (ini, warnings, diagnostics) = Emit(Tags, donorTexed: true,
            subTextures: new Dictionary<int, SubmeshMaps> { [99] = new(MapSlot.From(dds)) });
        // A pipeline suffix and a submesh position: the change list refuses this by name long before a
        // build reaches here, so the row is the emitter's own account and belongs in the log.
        Assert.Contains(diagnostics, d => d.Contains("submesh 99") && d.Contains("out of range"));
        Assert.DoesNotContain(warnings, w => w.Contains("out of range"));
        Assert.Contains("drawindexed = ", ini);
    }

    [Fact]
    public void Retexturing_a_slot_tagged_texture_merges_the_tag_into_the_retex_section()
    {
        // One hash, one section: the retexture's section carries the slot tag's kind value (with the
        // hash, ungated — the draw probes read it whether or not the rebind's key is on) and no
        // separate SlotTag section is minted, so the runtime sees no same-hash conflict.
        string dds = Path.Combine(_root, "rtx.dds");
        FlatDds.Write(dds, (200, 10, 10, 255));
        var (ini, warnings, _) = Emit(Tags, donorTexed: true,
            retex: new[] { new RetexEntry("alpha_a_f1f1a1a1", "f1f1a1a1", dds) });

        Assert.DoesNotContain("[TextureOverride_SlotTag_f1f1a1a1]", ini);
        Assert.Contains("[TextureOverride_Retex_alpha_a_f1f1a1a1]\nhash = f1f1a1a1\n"
            + $"filter_index = {MigotoEmitter.FilterAlbedo}\nmatch_priority = 100\nthis = Resource_Rtx0\n", ini);
        // the other kinds' tags stand untouched
        Assert.Contains($"[TextureOverride_SlotTag_f2f2b2b2]\nhash = f2f2b2b2\nfilter_index = {MigotoEmitter.FilterNormal}\nmatch_priority = 100\n", ini);
        Assert.DoesNotContain(warnings, w => w.Contains("f1f1a1a1"));
    }
}
