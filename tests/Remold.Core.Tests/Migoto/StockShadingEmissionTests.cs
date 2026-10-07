using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Shading values and effect disables made on a material of a part the mod does NOT replace: the section keyed
/// on the part's mesh runs them at that material's own draw range, around the game's own draw, and puts back
/// after the draw only what it changed. The emitted text is pinned here — it is what the runtime parses.
/// </summary>
public class StockShadingEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "gf2-stock-shading-" + Guid.NewGuid().ToString("N"));

    public StockShadingEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string Ib = "aa11bb22", Program = "45dbffd6cb513d80", Outline = "123456789abcdef0";
    private const int Filter = 4_978_303;
    private static readonly DrawShape Range = new(120, 300);

    private static StockDrawSite Site(string id = "sd_body_lod0_m1", string ib = Ib, DrawShape? shape = null,
        string? key = null, string? latch = null, int? verdict = null) =>
        new(id, ib, shape ?? Range, "body", key is null ? (KeyRef?)null : new KeyRef(key), latch,
            TwinVerdict: verdict);

    private static MaterialPatchEmission Value(string site = "sd_body_lod0_m1", string key = "value_0",
        float value = 1f) =>
        new(site, 0, key, 2, Filter, new[] { Program }, 544,
            Writes: new[] { new MaterialPatchWrite("_Tint", 180, value) });

    private static MaterialPatchEmission Skip(string site = "sd_body_lod0_m1") =>
        new(site, 0, "outline_0", -1, 1, new[] { Outline }, 0, SkipDraw: true, IsEffect: true);

    private static MaterialPatchEmission Texture(string site = "sd_body_lod0_m1") =>
        new(site, 0, "detail_0", -1, 1, new[] { Program }, 0,
            new[] { new MaterialEffectTexture(4, 1, 0, 0, 1) }, IsEffect: true);

    private string Emit(IReadOnlyList<StockDrawSite> sites, IReadOnlyList<MaterialPatchEmission> patches,
        string tag = "run", IReadOnlyList<string>? hides = null, string? modKey = null,
        IReadOnlyList<TwinGuard>? guards = null)
    {
        string outDir = Path.Combine(_root, $"out-{tag}");
        Directory.CreateDirectory(outDir);
        new MigotoEmitter().BuildOverlaysOnly(outDir, entries: null, hideHashes: hides, modKey: modKey,
            twinGuards: guards, materialPatches: patches, stockDraws: sites);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        return ini;
    }

    private static string Section(string ini, string header = "[TextureOverride_RetexScope_")
    {
        int at = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{header} is not in the emitted ini");
        int end = ini.IndexOf("\n\n", at, StringComparison.Ordinal);
        return end < 0 ? ini[at..] : ini[at..(end + 1)];
    }

    // ---- the value patch --------------------------------------------------------------------------------

    /// <summary>The program tag and the patch resources are declared as the replacement route declares them,
    /// and the one section on the part's mesh carries the patch.</summary>
    [Fact]
    public void A_value_patch_declares_its_program_tag_and_resources_once()
    {
        string ini = Emit(new[] { Site() }, new[] { Value() });

        Assert.Contains($"[ShaderOverride_MaterialPass_{Program}]\nhash = {Program}\n"
            + $"filter_index = {Filter}\nallow_duplicate_hash = true\n", ini);
        Assert.Contains("[Resource_MaterialSource_sd_body_lod0_m1_s0]", ini);
        Assert.Contains("[Resource_MaterialDraw_sd_body_lod0_m1_s0]\ntype = Buffer\nbyte_width = 544\n", ini);
        Assert.Contains("[Resource_MaterialTarget_sd_body_lod0_m1_s0]\ntype = Buffer\nformat = R32G32B32A32_UINT\n"
            + "array = 34\nbind_flags = render_target\n", ini);
        Assert.Contains("[CustomShader_MaterialPatch_sd_body_lod0_m1_s0]\nvs = pose_fullscreen.hlsl\n", ini);
        Assert.Contains("ps = generated/material_pass_sd_body_lod0_m1_s0.hlsl\n"
            + "o0 = set_viewport Resource_MaterialView_sd_body_lod0_m1_s0\n"
            + "o0 = Resource_MaterialTarget_sd_body_lod0_m1_s0\ndraw = 3, 0\n", ini);
        Assert.DoesNotContain("cs-u0", ini);
        Assert.DoesNotContain("Dispatch", ini);
        Assert.Contains($"[TextureOverride_RetexScope_sd_body_lod0_m1]\nhash = {Ib}\nmatch_priority = 0\n", ini);
    }

    /// <summary>At the material's own draw, while its exact program is bound, one pass patches the live
    /// constants, the result is copied into the draw's constant buffer and bound for that draw, and the
    /// section's flag records that it did.</summary>
    [Fact]
    public void The_patch_runs_inside_the_draw_range_and_the_program_gate()
    {
        string section = Section(Emit(new[] { Site() }, new[] { Value() }));
        const string gid = "sd_body_lod0_m1_s0";

        Assert.Contains($"if first_index == {Range.First}\nif index_count == {Range.Count}\n"
            + $"$zz_material_ps_{gid} = ps\n"
            + $"if $zz_material_ps_{gid} == {Filter}\n"
            + $"run = CustomShader_MaterialPatch_{gid}\n"
            + $"Resource_MaterialDraw_{gid} = copy Resource_MaterialTarget_{gid}\n"
            + $"ps-cb2 = Resource_MaterialDraw_{gid}\n"
            + $"$zz_bcb_{gid} = 1\nendif\nendif\nendif\n", section);
    }

    /// <summary>The patch pass reads the constants the game bound at the group's slot as integers and writes
    /// each patched value over its component, and it compiles without a warning the loader would print.</summary>
    [Fact]
    public void The_patch_pass_writes_the_values_over_the_bound_constants_as_integers()
    {
        Emit(new[] { Site() }, new[] { Value() }, tag: "pass");
        string hlsl = File.ReadAllText(Path.Combine(_root, "out-pass", "generated", "material_pass_sd_body_lod0_m1_s0.hlsl"));

        Assert.Contains("cbuffer material_state : register(b2) { uint4 material_constants[34]; }\n", hlsl);
        Assert.Contains("    if (e == 11u) { v.y = 0x3f800000u; }\n", hlsl);
        HlslCheck.CompilesClean(hlsl, "material_pass", "ps_5_0");
    }

    /// <summary>Two patches of one group writing the same byte: the later one's value is the one written.</summary>
    [Fact]
    public void A_later_patch_in_the_group_wins_a_byte_both_write()
    {
        Emit(new[] { Site() }, new[] { Value(), Value(key: "value_1", value: 0.5f) }, tag: "later");
        string hlsl = File.ReadAllText(Path.Combine(_root, "out-later", "generated", "material_pass_sd_body_lod0_m1_s0.hlsl"));

        Assert.Contains("    if (e == 11u) { v.y = 0x3f000000u; }\n", hlsl);
        Assert.DoesNotContain("0x3f800000u", hlsl);
    }

    /// <summary>A write outside the patch's buffer, or off a component boundary, is refused rather than dropped.</summary>
    [Fact]
    public void A_write_outside_the_buffer_is_refused()
    {
        var past = Value() with { Writes = new[] { new MaterialPatchWrite("_Tint", 544, 1f) } };
        var odd = Value() with { Writes = new[] { new MaterialPatchWrite("_Tint", 182, 1f) } };

        Assert.Contains("outside its 544-byte buffer",
            Assert.Throws<InvalidOperationException>(() => Emit(new[] { Site() }, new[] { past })).Message);
        Assert.Throws<InvalidOperationException>(() => Emit(new[] { Site() }, new[] { odd }));
    }

    /// <summary>A value edit over two programs, one of which an effect disable is proven for, splits into two
    /// program classes. Each class runs the value's patch in a pass of its own that writes its own target.</summary>
    [Fact]
    public void A_value_spanning_two_program_classes_runs_in_each_classes_own_section()
    {
        var value = Value() with { PixelShaderHashes = new[] { Program, Outline } };
        var texture = Texture() with { PixelShaderHashes = new[] { Outline } };
        string ini = Emit(new[] { Site() }, new[] { value, texture });
        string section = Section(ini);

        foreach (string gid in new[] { "sd_body_lod0_m1_s0_c0", "sd_body_lod0_m1_s0_c1" })
        {
            Assert.Contains($"[CustomShader_MaterialPatch_{gid}]\n", ini);
            Assert.Contains($"o0 = Resource_MaterialTarget_{gid}\n", ini);
            Assert.Contains($"run = CustomShader_MaterialPatch_{gid}\n", section);
        }
    }

    /// <summary>A shading-only mod has no pose route to write the vertex shader its patch passes draw with, so
    /// the patch passes write it; every shader the mod names is in its folder and compiles clean.</summary>
    [Fact]
    public void A_shading_only_mod_ships_every_shader_its_passes_name()
    {
        string ini = Emit(new[] { Site() }, new[] { Value(), Texture() }, tag: "shaders");
        string outDir = Path.Combine(_root, "out-shaders");
        Assert.True(File.Exists(Path.Combine(outDir, "pose_fullscreen.hlsl")));
        HlslCheck.EveryShaderCompilesClean(ini, outDir);
    }

    /// <summary>The game's buffer is referenced before the range test, and put back after the draw only where
    /// this section bound its patched copy.</summary>
    [Fact]
    public void The_buffer_is_saved_first_and_restored_only_where_it_was_bound()
    {
        string section = Section(Emit(new[] { Site() }, new[] { Value() }));
        const string gid = "sd_body_lod0_m1_s0";

        int local = section.IndexOf($"local $zz_bcb_{gid} = 0\n", StringComparison.Ordinal);
        int save = section.IndexOf($"Resource_MaterialSource_{gid} = ref ps-cb2\n", StringComparison.Ordinal);
        int range = section.IndexOf("if first_index ==", StringComparison.Ordinal);
        int restore = section.IndexOf($"if $zz_bcb_{gid} == 1\npost ps-cb2 = Resource_MaterialSource_{gid}\nendif\n",
            StringComparison.Ordinal);
        Assert.True(local >= 0 && local < save && save < range && range < restore,
            $"local {local}, save {save}, range {range}, restore {restore}");
        Assert.Contains($"local $zz_material_ps_{gid}\n", section);
    }

    // ---- effects ------------------------------------------------------------------------------------

    /// <summary>An effect disable that omits the draw skips the game's own draw of that material, in the
    /// pass whose program it is proven for.</summary>
    [Fact]
    public void An_effect_that_omits_the_draw_skips_the_games_own_draw_in_its_pass()
    {
        string section = Section(Emit(new[] { Site() }, new[] { Skip() }));
        int outlineFilter = DerivedMaterialEvidence.FamilyFilterValue(new[] { Outline });

        Assert.Contains($"if $zz_material_ps_sd_body_lod0_m1_s0 == {outlineFilter}\nhandling = skip\nendif\n",
            section);
        Assert.DoesNotContain("post ps-", section);
    }

    /// <summary>An effect that binds a neutral texture binds it for the draw, raises that register's bound
    /// flag, and the register is restored from the section's one save of it.</summary>
    [Fact]
    public void An_effect_texture_is_bound_for_the_draw_and_restored_where_it_was_bound()
    {
        string outDir = Path.Combine(_root, "out-texture");
        string section = Section(Emit(new[] { Site() }, new[] { Texture() }, tag: "texture"));
        const string gid = "sd_body_lod0_m1_s0";

        Assert.Contains($"ps-t4 = Resource_MaterialTexture_{gid}_4\n$zz_bt4 = 1\n", section);
        Assert.Contains($"Resource_MaterialTextureSave_{gid}_4 = ref ps-t4\n", section);
        Assert.Contains($"if $zz_bt4 == 1\npost ps-t4 = Resource_MaterialTextureSave_{gid}_4\nendif\n", section);
        Assert.True(File.Exists(Path.Combine(outDir, "effect_neutral_1001.dds")));
    }

    // ---- gates ----------------------------------------------------------------------------------------

    [Fact]
    public void A_key_and_a_latch_gate_the_patch_and_not_the_save_or_restore()
    {
        string ini = Emit(new[] { Site(key: "F7", latch: "vesna") }, new[] { Value() }, modKey: "F6");
        string section = Section(ini);
        const string gid = "sd_body_lod0_m1_s0";

        Assert.Contains($"if $zz_material_ps_{gid} == {Filter}\nif ${ModKeys.VariableFor("F6")} == 0\n"
            + $"if ${ModKeys.VariableFor("F7")} == 0\nif $zz_gate_vesna == 1\n"
            + $"run = CustomShader_MaterialPatch_{gid}\n", section);
        Assert.True(section.IndexOf($"Resource_MaterialSource_{gid} = ref ps-cb2", StringComparison.Ordinal)
            < section.IndexOf("if $zz_key_", StringComparison.Ordinal));
        Assert.Contains($"global ${ModKeys.VariableFor("F7")} = 0\n", ini);
    }

    /// <summary>Where another mesh draws on the same section key, the patch waits for the twin guard's
    /// verdict naming this part's own mesh.</summary>
    [Fact]
    public void A_twin_verdict_holds_the_patch_to_the_parts_own_mesh()
    {
        var guard = new TwinGuard(Ib, MigotoEmitter.TwinVar(Ib), new[] { 1 },
            new[] { new TwinProbeTag("0badf00d", 1_234_567, 1), new TwinProbeTag("0badf00e", 1_234_568, 2) });
        string section = Section(Emit(new[] { Site(verdict: 1) }, new[] { Value() }, guards: new[] { guard }));

        int probe = section.IndexOf($"if $zz_t == 1234567\n${guard.Var} = 1\nendif\n", StringComparison.Ordinal);
        int open = section.IndexOf($"if ${guard.Var} == 1\n$zz_material_ps_", StringComparison.Ordinal);
        Assert.True(probe > 0 && open > probe);
    }

    // ---- several materials, a hidden mesh -------------------------------------------------------------

    /// <summary>Two materials of one part share the mesh's one section, each inside its own range.</summary>
    [Fact]
    public void Two_materials_of_one_part_share_the_section_and_keep_their_own_ranges()
    {
        string ini = Emit(
            new[] { Site(), Site(id: "sd_body_lod0_m0", shape: new DrawShape(0, 120)) },
            new[] { Value(), Value(site: "sd_body_lod0_m0", key: "value_1") });

        string section = Section(ini);
        int first = section.IndexOf("if first_index == 0\nif index_count == 120\n", StringComparison.Ordinal);
        int second = section.IndexOf($"if first_index == {Range.First}\nif index_count == {Range.Count}\n",
            StringComparison.Ordinal);
        Assert.True(first > 0 && second > first);
        Assert.True(section.IndexOf("run = CustomShader_MaterialPatch_sd_body_lod0_m0_s0\n", StringComparison.Ordinal) is var m0
            && m0 > first && m0 < second);
        Assert.True(section.IndexOf("run = CustomShader_MaterialPatch_sd_body_lod0_m1_s0\n", StringComparison.Ordinal) > second);
        Assert.Equal(1, ini.Split("[TextureOverride_RetexScope_").Length - 1);
    }

    /// <summary>A mesh one state hides and another state shades owns one section: the skip and the patch both
    /// live in the hide's section.</summary>
    [Fact]
    public void A_hidden_mesh_carrying_a_shading_change_folds_it_into_its_one_section()
    {
        string ini = Emit(new[] { Site() }, new[] { Value() }, tag: "hidden", hides: new[] { Ib });

        Assert.DoesNotContain("[TextureOverride_RetexScope_", ini);
        string section = Section(ini, "[TextureOverride_Hide_0]");
        Assert.Contains("handling = skip\n", section);
        Assert.Contains("run = CustomShader_MaterialPatch_sd_body_lod0_m1_s0\n", section);
    }

    // ---- what it refuses ------------------------------------------------------------------------------

    [Fact]
    public void A_patch_naming_a_material_position_of_a_stock_draw_is_refused()
    {
        var patch = Value() with { Submesh = 1 };

        var ex = Assert.Throws<InvalidOperationException>(() => Emit(new[] { Site() }, new[] { patch }));

        Assert.Contains("draws one material", ex.Message);
    }

    [Fact]
    public void A_stock_draw_naming_a_range_the_game_never_issues_is_refused() =>
        Assert.Throws<InvalidOperationException>(() =>
            Emit(new[] { Site(shape: new DrawShape(0, 0)) }, new[] { Value() }));

    [Fact]
    public void Two_stock_draws_under_one_name_are_refused() =>
        Assert.Throws<InvalidOperationException>(() =>
            Emit(new[] { Site(), Site(shape: new DrawShape(0, 120)) }, new[] { Value() }));

    [Fact]
    public void A_patch_on_a_guarded_key_without_its_own_verdict_is_refused()
    {
        var guard = new TwinGuard(Ib, MigotoEmitter.TwinVar(Ib), new[] { 1 },
            new[] { new TwinProbeTag("0badf00d", 1_234_567, 1) });

        Assert.Throws<InvalidOperationException>(() =>
            Emit(new[] { Site() }, new[] { Value() }, guards: new[] { guard }));
    }

    [Fact]
    public void Two_builds_of_one_change_emit_identical_text() =>
        Assert.Equal(Emit(new[] { Site() }, new[] { Value(), Texture() }, tag: "once"),
            Emit(new[] { Site() }, new[] { Value(), Texture() }, tag: "twice"));
}
