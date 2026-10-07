using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// A toon ramp picked on a material of a part the mod does NOT replace: a section keyed on the part's mesh
/// that runs at that material's own draw range, finds the register holding its ramp, swaps for the draw and
/// puts it back where it swapped. The emitted text is pinned here — it is what the runtime parses.
/// </summary>
public class StockRampEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "gf2-stock-ramp-" + Guid.NewGuid().ToString("N"));

    public StockRampEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly IReadOnlyList<int> StockSlots = ShaderSlotPlan.Shipped.StockMaps;
    private static readonly IReadOnlyList<int> RampSlots = ShaderSlotPlan.Shipped.Ramp;

    private const string Ib = "aa11bb22", RampHash = "d1ce0002";
    private static readonly DrawShape Range = new(120, 300);

    /// <summary>A ramp-shaped fp16 DDS, written through the container a shipped one goes out in.</summary>
    private string Ramp(string name = "picked_ramp.dds")
    {
        string path = Path.Combine(_root, name);
        var px = new byte[256 * 16 * 8];
        for (int i = 0; i < px.Length; i += 2)
            BitConverter.TryWriteBytes(px.AsSpan(i, 2), (Half)((i / 2 % 31) / 30f));
        using var s = File.Create(path);
        DdsWriter.Write(s, DdsWriter.R16G16B16A16_FLOAT, 256, 16, new[] { px });
        return path;
    }

    private string Emit(IReadOnlyList<StockRampBind> binds, string tag = "run",
        IReadOnlyList<string>? hides = null, string? modKey = null, IReadOnlyList<TwinGuard>? guards = null)
    {
        string outDir = Path.Combine(_root, $"out-{tag}");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null, hideHashes: hides, modKey: modKey,
            twinGuards: guards, stockRamps: binds);
        return File.ReadAllText(Path.Combine(outDir, "mod.ini"));
    }

    private StockRampBind Bind(string? key = null, string? latch = null, string name = "body_lod0_ramp",
        DrawShape? shape = null, int? verdict = null) =>
        new(name, Ib, shape ?? Range, RampHash, Ramp(), key is null ? (KeyRef?)null : new KeyRef(key), latch,
            TwinVerdict: verdict);

    // ---- the tag ----------------------------------------------------------------------------------

    /// <summary>The ramp answers WHICH REGISTER holds a ramp, by the kind value every ramp bind in this
    /// build reads. Nothing tags another map of the material: the draw range says which material it is.</summary>
    [Fact]
    public void Only_the_ramp_carries_a_tag()
    {
        string ini = Emit(new[] { Bind() });

        Assert.Contains($"[TextureOverride_SlotTag_{RampHash}]\nhash = {RampHash}\n"
            + $"filter_index = {MigotoEmitter.FilterRamp}\nmatch_priority = 100\n", ini);
        Assert.DoesNotContain("StockRampTag", ini);
        Assert.DoesNotContain("zz_srm", ini);
    }

    // ---- the section ----------------------------------------------------------------------------------

    [Fact]
    public void The_bind_lives_in_one_section_keyed_on_the_parts_mesh()
    {
        string ini = Emit(new[] { Bind() });

        Assert.Contains($"[TextureOverride_RetexScope_body_lod0_ramp]\nhash = {Ib}\nmatch_priority = 0\n", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
        // draw-scoped only: nothing rebinds the ramp resource game-wide
        Assert.DoesNotContain("this = Resource_", ini);
    }

    /// <summary>One mesh draws every material of the part, each over an index range of its own, so the bind
    /// runs only inside the test for its own material's range — probe, bind and bound flag alike.</summary>
    [Fact]
    public void The_swap_runs_only_at_the_materials_own_draw_range()
    {
        string section = Section(Emit(new[] { Bind() }));

        int open = section.IndexOf($"if first_index == {Range.First}\nif index_count == {Range.Count}\n",
            StringComparison.Ordinal);
        Assert.True(open > 0);
        Assert.Contains("$zz_slot_rm = -1\n", section);
        foreach (int s in RampSlots)
        {
            Assert.Contains($"$zz_sr = ps-t{s}\nif $zz_sr == {MigotoEmitter.FilterRamp}\n"
                + $"$zz_slot_rm = {s}\nendif\n", section);
            int bind = section.IndexOf($"if $zz_slot_rm == {s}\nps-t{s} = Resource_Rtx0\n$zz_bt{s} = 1\nendif\n",
                StringComparison.Ordinal);
            Assert.True(bind > open, $"the bind at ps-t{s} must sit inside the draw-range test");
        }
    }

    /// <summary>Every ramp register is saved before anything binds, and put back after the draw only where
    /// this section bound it: a section that bound nothing leaves the register to whoever did.</summary>
    [Fact]
    public void Each_ramp_register_is_restored_only_where_the_section_bound_it()
    {
        string ini = Emit(new[] { Bind() });
        string section = Section(ini);

        Assert.Contains(RampSlots, s => s > 6);   // the shipped plan exercises the high-register path
        foreach (int s in RampSlots)
        {
            Assert.Contains($"[Resource_SrSave{s}]\n", ini);
            Assert.Contains($"local $zz_bt{s} = 0\n", section);
            Assert.Contains($"Resource_SrSave{s} = ref ps-t{s}\n", section);
            Assert.Contains($"if $zz_bt{s} == 1\npost ps-t{s} = Resource_SrSave{s}\nendif\n", section);
            Assert.DoesNotContain($"\npost ps-t{s} = Resource_SrSave{s}\n\n", section);
        }
        // the saves come before the range test, the restores after it closes
        int range = section.IndexOf("if first_index ==", StringComparison.Ordinal);
        Assert.True(section.IndexOf($"Resource_SrSave{RampSlots[0]} = ref", StringComparison.Ordinal) < range);
        Assert.True(section.IndexOf($"post ps-t{RampSlots[0]}", StringComparison.Ordinal) > range);
        // and nothing is saved outside the ramp's own range: the picture maps are untouched here
        foreach (int s in StockSlots.Except(RampSlots))
            Assert.DoesNotContain($"[Resource_SrSave{s}]", ini);
    }

    /// <summary>A register outside the ramp's measured range is never asked about, and never written.</summary>
    [Fact]
    public void No_register_outside_the_ramps_own_range_is_touched()
    {
        // the section, not the whole ini: [Constants] declares the variable at 0, which is a register
        // number only by coincidence
        string section = Section(Emit(new[] { Bind() }));

        foreach (int s in Enumerable.Range(0, 32).Except(RampSlots))
        {
            Assert.DoesNotContain($"$zz_slot_rm = {s}\n", section);
            Assert.DoesNotContain($"if $zz_slot_rm == {s}\n", section);
        }
    }

    [Fact]
    public void The_picked_ramp_ships_verbatim_beside_the_ini()
    {
        string outDir = Path.Combine(_root, "out-file");
        var bind = Bind();
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null, stockRamps: new[] { bind });

        string shipped = Path.Combine(outDir, Path.GetFileName(bind.DdsFile));
        Assert.Equal(File.ReadAllBytes(bind.DdsFile), File.ReadAllBytes(shipped));
        Assert.Contains($"filename = {Path.GetFileName(bind.DdsFile)}\n",
            File.ReadAllText(Path.Combine(outDir, "mod.ini")));
    }

    // ---- gates ----------------------------------------------------------------------------------------

    [Fact]
    public void A_key_and_a_latch_gate_the_bind_and_not_the_restore()
    {
        string ini = Emit(new[] { Bind(key: "F7", latch: "vesna") }, modKey: "F6");
        string section = Section(ini);

        Assert.Contains($"if ${ModKeys.VariableFor("F6")} == 0\n"
            + $"if ${ModKeys.VariableFor("F7")} == 0\nif $zz_gate_vesna == 1\n", section);
        foreach (int s in RampSlots)
            Assert.Contains($"if $zz_bt{s} == 1\npost ps-t{s} = Resource_SrSave{s}\nendif\n", section);
        // the keys the emission declares include the bind's own
        Assert.Contains($"global ${ModKeys.VariableFor("F7")} = 0\n", ini);
    }

    /// <summary>An unkeyed pick emits no gate at all, so its block is the bare probe and bind.</summary>
    [Fact]
    public void An_unkeyed_pick_emits_no_gate()
    {
        string ini = Emit(new[] { Bind() });

        Assert.DoesNotContain("[Key_", ini);
        Assert.Contains($"$zz_slot_rm = {RampSlots[^1]}\nendif\nif $zz_slot_rm == {RampSlots[0]}\n", Section(ini));
    }

    /// <summary>Where another mesh draws on the same section key, the block waits for the twin guard's verdict
    /// naming this part's own mesh, and the probe that writes it runs first.</summary>
    [Fact]
    public void A_twin_verdict_holds_the_block_to_the_parts_own_mesh()
    {
        var guard = new TwinGuard(Ib, MigotoEmitter.TwinVar(Ib), new[] { 2 },
            new[] { new TwinProbeTag("0badf00d", 1_234_567, 1), new TwinProbeTag("0badf00e", 1_234_568, 2) });
        string section = Section(Emit(new[] { Bind(verdict: 2) }, guards: new[] { guard }));

        int probe = section.IndexOf($"if $zz_t == 1234568\n${guard.Var} = 2\nendif\n", StringComparison.Ordinal);
        int open = section.IndexOf($"if ${guard.Var} == 2\n$zz_slot_rm = -1\n", StringComparison.Ordinal);
        Assert.True(probe > 0 && open > probe);
    }

    [Fact]
    public void A_pick_on_a_guarded_key_without_its_own_verdict_is_refused()
    {
        var guard = new TwinGuard(Ib, MigotoEmitter.TwinVar(Ib), new[] { 2 },
            new[] { new TwinProbeTag("0badf00d", 1_234_567, 1) });

        Assert.Throws<InvalidOperationException>(() => Emit(new[] { Bind() }, guards: new[] { guard }));
        Assert.Throws<InvalidOperationException>(() =>
            Emit(new[] { Bind(verdict: 1) }, tag: "wrong", guards: new[] { guard }));
    }

    // ---- several picks, several meshes ----------------------------------------------------------------

    /// <summary>Two materials of ONE part share the part's mesh, so both binds live in the one section that
    /// key owns — each inside its own material's draw range.</summary>
    [Fact]
    public void Two_materials_of_one_part_share_the_section_and_keep_their_own_ranges()
    {
        var other = new DrawShape(0, 120);
        string ini = Emit(new[]
        {
            Bind(name: "body_lod0_ramp"),
            new StockRampBind("body_lod0_ramp2", Ib, other, "beef0004", Ramp("second.dds")),
        });

        ModBuilderTests.AssertNoDuplicateSections(ini);
        string section = Section(ini);
        int first = section.IndexOf("if first_index == 0\nif index_count == 120\n", StringComparison.Ordinal);
        int second = section.IndexOf($"if first_index == {Range.First}\nif index_count == {Range.Count}\n",
            StringComparison.Ordinal);
        Assert.True(first > 0 && second > first);
        Assert.True(section.IndexOf("= Resource_Rtx1\n", StringComparison.Ordinal) is var one && one > first && one < second);
        Assert.True(section.IndexOf("= Resource_Rtx0\n", StringComparison.Ordinal) > second);
        // one save/restore pair for the section, whatever it carries
        Assert.Equal(1, Count(ini, $"Resource_SrSave{RampSlots[0]} = ref ps-t{RampSlots[0]}\n"));
        Assert.Equal(1, Count(ini, $"post ps-t{RampSlots[0]} = Resource_SrSave{RampSlots[0]}\n"));
    }

    /// <summary>The part's other LOD tiers each draw on a mesh of their own, and each takes its own
    /// section — a tier left out shades with the game's ramp wherever the game picks it.</summary>
    [Fact]
    public void Each_tier_takes_its_own_section()
    {
        string dds = Ramp();
        string ini = Emit(new[]
        {
            new StockRampBind("body_lod0_ramp", Ib, Range, RampHash, dds),
            new StockRampBind("body_lod1_ramp", "aa11bb33", new DrawShape(40, 90), RampHash, dds),
        });

        ModBuilderTests.AssertNoDuplicateSections(ini);
        Assert.Contains($"[TextureOverride_RetexScope_body_lod0_ramp]\nhash = {Ib}\n", ini);
        Assert.Contains("[TextureOverride_RetexScope_body_lod1_ramp]\nhash = aa11bb33\n", ini);
        // one shipped file for the one ramp, however many draws bind it
        Assert.Equal(1, Count(ini, "[Resource_Rtx0]\n"));
        Assert.DoesNotContain("[Resource_Rtx1]", ini);
        // …and the tag section is minted once per hash, not once per section
        Assert.Equal(1, Count(ini, $"[TextureOverride_SlotTag_{RampHash}]\n"));
    }

    // ---- a hidden mesh, and a build that carries none --------------------------------------------------

    /// <summary>A mesh one key-group state hides and another state shades owns one section: the skip and the
    /// ramp block both live in it. While the hiding state stands the draw is skipped, so the two never
    /// contradict each other.</summary>
    [Fact]
    public void A_hidden_mesh_carrying_a_ramp_pick_folds_the_bind_into_its_one_section()
    {
        string ini = Emit(new[] { Bind() }, tag: "hidden", hides: new[] { Ib });

        ModBuilderTests.AssertNoDuplicateSections(ini);
        Assert.DoesNotContain("[TextureOverride_RetexScope_", ini);
        string section = ini[ini.IndexOf("[TextureOverride_Hide_0]", StringComparison.Ordinal)..];
        Assert.Contains("handling = skip\n", section);
        Assert.Contains($"if first_index == {Range.First}\n", section);
    }

    /// <summary>The whole mechanism is declared only where a pick ships.</summary>
    [Fact]
    public void A_build_with_no_pick_carries_none_of_the_mechanism()
    {
        string outDir = Path.Combine(_root, "out-none");
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null, hideHashes: new[] { Ib });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.DoesNotContain("zz_sr =", ini);
        Assert.DoesNotContain("zz_slot_rm", ini);
        Assert.DoesNotContain("zz_bt", ini);
        Assert.DoesNotContain("first_index", ini);
        Assert.DoesNotContain("Resource_SrSave", ini);
    }

    // ---- a section carrying both a scoped retexture and a ramp bind -----------------------------------
    // The probe sweep and the ramp's candidate registers OVERLAP. A save taken after the scoped binds would
    // capture the mod's own image on a shared register, and a second restore on that register would leave
    // it bound past the draw — the game's own texture never coming back.

    /// <summary>Every save is taken before the first bind, so no save can capture what this section itself
    /// put in a register.</summary>
    [Fact]
    public void A_section_carrying_both_takes_every_save_before_any_bind()
    {
        var lines = BothSection().Split('\n').Select(l => l.Trim()).ToList();

        int lastSave = lines.FindLastIndex(l => l.Contains(" = ref ps-t", StringComparison.Ordinal));
        int firstBind = lines.FindIndex(l =>
            l.StartsWith("ps-t", StringComparison.Ordinal)
            && l.Contains("= Resource_Rtx", StringComparison.Ordinal));
        Assert.True(lastSave >= 0 && firstBind >= 0);
        Assert.True(lastSave < firstBind,
            $"a save at line {lastSave} is taken after the bind at line {firstBind}");
    }

    /// <summary>…and each register the section can touch is put back exactly once, from the save of the
    /// family that took it first. Post commands run in source order, so a register named twice is restored
    /// from whichever restore came last.</summary>
    [Fact]
    public void A_section_carrying_both_restores_each_register_once()
    {
        var restores = BothSection().Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith("post ps-t", StringComparison.Ordinal)).ToList();

        var registers = restores
            .Select(l => int.Parse(l["post ps-t".Length..].Split(' ')[0])).ToList();
        Assert.Equal(StockSlots.Union(RampSlots).OrderBy(s => s), registers.OrderBy(s => s));
        foreach (int s in StockSlots)
            Assert.Contains($"post ps-t{s} = Resource_RtxSave{s}", restores);
    }

    /// <summary>A scoped bind raises the bound flag of the register it wrote, the same as a ramp bind.</summary>
    [Fact]
    public void A_scoped_bind_raises_its_registers_bound_flag()
    {
        string section = BothSection();

        foreach (int s in StockSlots)
            Assert.Contains($"if $zz_rslot == {s}\nps-t{s} = Resource_Rtx0\n$zz_bt{s} = 1\nendif\n", section);
    }

    /// <summary>One mesh drawn with both a draw-scoped retexture and a picked ramp: one section, both
    /// blocks inside it.</summary>
    private string BothSection()
    {
        string outDir = Path.Combine(_root, "out-both");
        string image = Path.Combine(_root, "scoped.dds");
        FlatDds.Write(image, (9, 8, 7, 255));
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null,
            scopedEntries: new[]
            {
                new ScopedRetexEntry("scoped", "5c0ded01",
                    new[] { new ScopedRetexImage(image, new[] { new ScopedAnchor(Ib, "body_lod0") }) }),
            },
            stockRamps: new[] { Bind() });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        return Section(ini);
    }

    [Fact]
    public void Two_builds_of_one_pick_emit_identical_text() =>
        Assert.Equal(Emit(new[] { Bind() }, tag: "once"), Emit(new[] { Bind() }, tag: "twice"));

    private static string Section(string ini) =>
        ini[ini.IndexOf("[TextureOverride_RetexScope_", StringComparison.Ordinal)..];

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
