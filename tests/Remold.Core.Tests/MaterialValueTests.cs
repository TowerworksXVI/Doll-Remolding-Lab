using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Remold.Core.Tests;

public sealed class MaterialValueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-material-values-" + Guid.NewGuid().ToString("N"));

    public MaterialValueTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Material_source_proposal_separates_bindings_live_values_and_unsupported_state()
    {
        var activeSlot = Slot("active-material", Part("body"), 9001, "body_skinuber");
        var proposal = MaterialSourceDifferenceResolver.Propose("edit-body", "Cloth source", new[]
        {
            new MaterialDifferenceCandidate("slot-base", "Base colour",
                MaterialDifferenceKind.InputBinding, "target-base", "source-base",
                ProposedBinding: Source("slot-base", "source-base")),
            new MaterialDifferenceCandidate("slot-gi", "GI flatten",
                MaterialDifferenceKind.SemanticValue, "1", "0",
                MaterialValueSemantics.UseGiFlatten, Source("slot-gi", "source-gi")),
            new MaterialDifferenceCandidate("slot-effect", "Selection colour",
                MaterialDifferenceKind.SemanticValue, "blue", "red", "_AoeSelectColor"),
            new MaterialDifferenceCandidate("value-ramp", "Ramp switch",
                MaterialDifferenceKind.SemanticValue, "1", "0", "_UseRampMap"),
            new MaterialDifferenceCandidate("keyword-gi", "GI keyword",
                MaterialDifferenceKind.Keyword, "enabled", "disabled"),
            new MaterialDifferenceCandidate("same-cull", "Cull",
                MaterialDifferenceKind.RenderState, "back", "back"),
        }, Render(activeSlot, MaterialValueCatalog.UnityPerMaterial544).Contracts);

        Assert.Equal(5, proposal.Differences.Count);
        Assert.Equal(MaterialDifferenceDisposition.Binding,
            proposal.Differences.Single(row => row.SlotId == "slot-base").Disposition);
        Assert.Equal(MaterialDifferenceDisposition.Binding,
            proposal.Differences.Single(row => row.SlotId == "slot-gi").Disposition);
        Assert.Equal(MaterialDifferenceDisposition.DynamicLive,
            proposal.Differences.Single(row => row.SlotId == "slot-effect").Disposition);
        Assert.Equal(MaterialDifferenceDisposition.Unsupported,
            proposal.Differences.Single(row => row.SlotId == "value-ramp").Disposition);
        Assert.Equal(MaterialDifferenceDisposition.Unsupported,
            proposal.Differences.Single(row => row.SlotId == "keyword-gi").Disposition);
        Assert.DoesNotContain(proposal.Differences, row => row.SlotId == "same-cull");
    }

    [Fact]
    public void Material_source_value_reader_resolves_serialized_float_and_color_rows()
    {
        string bundle = Path.Combine(_root, "source-material.bundle");
        SyntheticBundle.BuildOneMaterial(bundle, "material.logical", "coat_skinuber", 41,
            Array.Empty<(string, int, long)>(), Array.Empty<string>(),
            shading: new SyntheticBundle.MaterialShadingSpec(1, 91,
                Array.Empty<string>(),
                new Dictionary<string, float> { ["_GlitterDensity"] = 12.5f },
                new Dictionary<string, float[]>
                {
                    ["_StockingCenterColor"] = new[] { 0.25f, 0.5f, 0.75f, 1f },
                }));
        byte[] bytes = File.ReadAllBytes(bundle);
        var source = Slot("source", Part("source"), 40, "coat_skinuber");
        source.Material!.LogicalBundle = "material.logical";
        source.Material.PathId = 41;
        var carrier = Slot("carrier", Part("carrier"), 50, "coat_skinuber");
        var reader = new MaterialSourceValueReader(logical =>
            logical == "material.logical" ? bytes : null);

        var single = reader.Resolve(source, carrier, "_GlitterDensity");
        var color = reader.Resolve(source, carrier, "_StockingCenterColor");

        Assert.Equal((BuildPlanVerdict.Resolved, "12.5"), (single.Verdict, single.Value));
        Assert.Equal((BuildPlanVerdict.Resolved, "0.25 0.5 0.75 1"),
            (color.Verdict, color.Value));
        Assert.Single(single.CarrierOwnedState,
            state => state.Kind == MaterialCarrierStateKind.DynamicLive);
    }

    [Fact]
    public void Use_gi_flatten_patches_only_its_float_in_every_supported_layout()
    {
        foreach (var layout in MaterialValueCatalog.Layouts)
        {
            var request = ProjectValueRequest(MaterialValueSemantics.UseGiFlatten, "0");
            var operation = MaterialValueBuildSupport.Resolve(request,
                Render(request.CurrentSlot, layout.Id));

            bool supported = layout.ByteWidth is 544 or 592;
            if (!supported)
            {
                Assert.Equal(BuildPlanVerdict.Unsupported, operation.Decision.Verdict);
                Assert.Empty(operation.Emissions!);
                Assert.Contains("Skin lighting", operation.Decision.Reason);
                Assert.Contains("not declared", operation.Decision.Detail);
                continue;
            }

            Assert.Equal(BuildPlanVerdict.Resolved, operation.Decision.Verdict);
            var emission = Assert.Single(operation.Emissions!);
            var patch = Assert.IsType<MaterialConstantBufferPatch>(emission.MaterialPatch);
            var write = Assert.Single(patch.Writes);
            Assert.Equal(492, write.ByteOffset);
            Assert.Equal(0, write.Value);
            Assert.Equal(new[] { "runtime-material-fields", "material-family", "_GI_FLATTEN" },
                patch.CarrierOwnedState.Select(state => state.Name));
            Assert.Contains($"material_bytes != {layout.ByteWidth}u",
                MaterialValuePatchEmitter.EmitShader(patch));

            byte[] live = Enumerable.Range(0, layout.ByteWidth)
                .Select(i => unchecked((byte)(i * 37 + 11))).ToArray();
            byte[] patched = MaterialConstantBufferPatcher.Apply(patch, live);
            for (int i = 0; i < live.Length; i++)
            {
                if (i is >= 492 and < 496) Assert.Equal(0, patched[i]);
                else Assert.Equal(live[i], patched[i]);
            }
        }
    }

    [Fact]
    public void Cross_family_source_reaches_the_build_plan_as_a_semantic_patch()
    {
        var project = SourceProject();
        var proposal = MaterialSourceDifferenceResolver.Propose("edit-body", "Cloth source",
            MaterialFamilyDifferences.Compare(
                project.TargetSlots.Single(slot => slot.Id == "slot-gi"),
                project.TargetSlots.Single(slot => slot.Id == "source-gi")),
            Render(project.TargetSlots.Single(slot => slot.Id == "slot-gi"),
                MaterialValueCatalog.UnityPerMaterial544).Contracts);
        Assert.Equal(MaterialDifferenceDisposition.Binding,
            proposal.Differences.Single(row => row.SlotId == "slot-gi").Disposition);
        Assert.Equal(2, proposal.Differences.Count(row =>
            row.Disposition == MaterialDifferenceDisposition.Unsupported));
        var backend = new MaterialBackend(MaterialValueCatalog.UnityPerMaterial544,
            new MaterialFamilyValueReader());

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        var planned = Assert.Single(plan.RuntimeEmissions,
            emission => emission.Emission.Kind == BuildEmissionKind.MaterialValuePatch);
        var patch = Assert.IsType<MaterialConstantBufferPatch>(planned.Emission.MaterialPatch);
        Assert.Equal(MaterialPatchBase.LiveCarrierSnapshot, patch.Base);
        Assert.Equal(2, patch.ConstantBufferSlot);
        Assert.Equal(544, patch.ByteWidth);
        Assert.Contains(patch.CarrierOwnedState,
            state => state.Kind == MaterialCarrierStateKind.DynamicLive
                && state.Name == "runtime-material-fields");
        Assert.Contains(patch.CarrierOwnedState,
            state => state.Kind == MaterialCarrierStateKind.Unsupported
                && state.Name == "_GI_FLATTEN");

        var file = Assert.Single(MaterialValuePatchEmitter.Emit(plan));
        Assert.EndsWith(".hlsl", file.File, StringComparison.Ordinal);
        Assert.Contains("material-patch:_UseGIFlatten:0:", file.FunctionalIdentity);
        Assert.Contains("RWByteAddressBuffer material_state : register(u0);", file.Text);
        Assert.Contains("material_state.Store(492, 0x00000000u);", file.Text);
        Assert.DoesNotContain("ps-cb2", file.Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The emitter wraps a patched submesh's draw in EVERY list that issues it: the full draw
    /// list and — on a routed multi-material target — the per-range list its draw moved into. The gate
    /// reads the family filter value, every candidate variant carries a tag section, patches sharing the
    /// draw share one snapshot, and the game's own resource is restored after the draw.</summary>
    [Fact]
    public void The_emitter_wraps_the_patched_draw_in_every_list_that_issues_it()
    {
        string dumps = Path.Combine(_root, "dumps");
        Migoto.SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "donor");
        Migoto.SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 2);
        string outDir = Path.Combine(_root, "out");
        Directory.CreateDirectory(Path.Combine(outDir, "generated"));
        File.WriteAllText(Path.Combine(outDir, "generated", "patch_a.hlsl"), "// patch a");
        File.WriteAllText(Path.Combine(outDir, "generated", "patch_b.hlsl"), "// patch b");

        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                    AnchorShapes = new DrawShapeSet(
                        new[] { new DrawShape(0, 60), new DrawShape(60, 84) }, 144),
                },
            },
            MaterialPatches = new[]
            {
                new MaterialPatchEmission("swap", 1, "patcha", 2, "generated/patch_a.hlsl",
                    4978303, new[] { "45dbffd6cb513d80", "0175b3fa12ebdbc8" }, 544),
                new MaterialPatchEmission("swap", 1, "patchb", 2, "generated/patch_b.hlsl",
                    4978303, new[] { "45dbffd6cb513d80", "0175b3fa12ebdbc8" }, 544),
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Migoto.ModBuilderTests.AssertNoDuplicateSections(ini);
        // one tag section per candidate variant, all carrying the one family value
        Assert.Contains("[ShaderOverride_MaterialPass_45dbffd6cb513d80]\nhash = 45dbffd6cb513d80\n"
            + "filter_index = 4978303\nallow_duplicate_hash = true\n", ini);
        Assert.Contains("[ShaderOverride_MaterialPass_0175b3fa12ebdbc8]\nhash = 0175b3fa12ebdbc8\n"
            + "filter_index = 4978303\nallow_duplicate_hash = true\n", ini);
        // one snapshot/work/draw trio per patched draw; both patch shaders run on the one working copy
        Assert.Contains("[Resource_MaterialSource_swap_s1]", ini);
        string workResource = Section(ini, "[Resource_MaterialWork_swap_s1]");
        Assert.Contains("type = RWByteAddressBuffer\nstride = 0\n"
            + "bind_flags = unordered_access\nmisc_flags = buffer_allow_raw_views", workResource);
        Assert.DoesNotContain("byte_width", workResource);
        Assert.DoesNotContain("constant_buffer", workResource);
        Assert.Contains("[Resource_MaterialDraw_swap_s1]\ntype = Buffer\nbyte_width = 544\n"
            + "stride = 0\nbind_flags = constant_buffer", ini);
        Assert.Empty(RawUavCopyResourceDescriptorErrors(ini));
        Assert.Contains("[CustomShader_MaterialPatch_patcha]\ncs = generated/patch_a.hlsl\n"
            + "cs-u0 = Resource_MaterialWork_swap_s1\nDispatch = 1, 1, 1\n"
            + "post cs-u0 = null\n", ini);
        Assert.DoesNotContain("cs-u0 = copy Resource_MaterialWork", ini);
        Assert.Contains("[CustomShader_MaterialPatch_patchb]", ini);

        // the gated wrap, in the full list AND in submesh 1's per-range list; submesh 0 stays bare
        string wrap = "local $zz_material_ps_swap_s1 = ps\n"
            + "if $zz_material_ps_swap_s1 == 4978303\n"
            + "Resource_MaterialSource_swap_s1 = ref ps-cb2\n"
            + "Resource_MaterialWork_swap_s1 = copy ps-cb2\n"
            + "run = CustomShader_MaterialPatch_patcha\n"
            + "run = CustomShader_MaterialPatch_patchb\n"
            + "Resource_MaterialDraw_swap_s1 = copy Resource_MaterialWork_swap_s1\n"
            + "ps-cb2 = Resource_MaterialDraw_swap_s1\n"
            + "endif\n"
            + "drawindexed = 12, 12, 0\n"
            + "if $zz_material_ps_swap_s1 == 4978303\n"
            + "ps-cb2 = Resource_MaterialSource_swap_s1\n"
            + "endif\n";
        Assert.Equal(2, ini.Split(wrap).Length - 1);
        string full = Section(ini, "[CommandListDraw_swap]");
        string ranged = Section(ini, "[CommandListDrawS1_swap]");
        Assert.Contains(wrap, full);
        Assert.Contains(wrap, ranged);
        Assert.DoesNotContain("$zz_material", Section(ini, "[CommandListDrawS0_swap]"));
        // the wrap sits immediately around its one draw
        Assert.True(full.IndexOf("drawindexed = 12, 0, 0", StringComparison.Ordinal)
            < full.IndexOf("local $zz_material_ps_swap_s1", StringComparison.Ordinal));
    }

    [Fact]
    public void Raw_uav_resources_first_copied_from_cb_slots_require_explicit_descriptor_flags()
    {
        const string add88Shape = """
            [Resource_MaterialWork_X]
            type = RWByteAddressBuffer
            byte_width = 544
            stride = 0
            bind_flags = unordered_access

            [CommandListDraw_X]
            Resource_MaterialWork_X = copy ps-cb2
            run = CustomShader_MaterialPatch_X
            """;

        string error = Assert.Single(RawUavCopyResourceDescriptorErrors(add88Shape));
        Assert.Contains("misc_flags", error);
    }

    [Fact]
    public void A_patch_wraps_every_donor_draw_folded_onto_its_material_position()
    {
        string dumps = Path.Combine(_root, "fold-dumps");
        Migoto.SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "fold-donor");
        Migoto.SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 4);
        string outDir = Path.Combine(_root, "fold-out");
        Directory.CreateDirectory(Path.Combine(outDir, "generated"));
        File.WriteAllText(Path.Combine(outDir, "generated", "patch.hlsl"), "// patch");

        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                    AnchorShapes = new DrawShapeSet(
                        new[] { new DrawShape(0, 60), new DrawShape(60, 84) }, 144),
                },
            },
            MaterialPatches = new[]
            {
                new MaterialPatchEmission("swap", 1, "patch", 2, "generated/patch.hlsl",
                    4978303, new[] { "45dbffd6cb513d80" }, 544),
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        const string wrap = "local $zz_material_ps_swap_s1 = ps";
        string full = Section(ini, "[CommandListDraw_swap]");
        Assert.Equal(4, full.Split("drawindexed = ").Length - 1);
        // one declaration per section; the later wraps assign to the declared gate variable
        Assert.Equal(1, full.Split(wrap).Length - 1);
        Assert.Equal(3, full.Split("$zz_material_ps_swap_s1 = ps").Length - 1);
        Assert.DoesNotContain(wrap, Section(ini, "[CommandListDrawS0_swap]"));
        foreach (int draw in new[] { 1, 2, 3 })
            Assert.Contains(wrap, Section(ini, $"[CommandListDrawS{draw}_swap]"));
    }

    [Fact]
    public void A_single_material_patch_wraps_every_donor_draw()
    {
        string dumps = Path.Combine(_root, "single-dumps");
        Migoto.SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "single-donor");
        Migoto.SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 4);
        string outDir = Path.Combine(_root, "single-out");
        Directory.CreateDirectory(Path.Combine(outDir, "generated"));
        File.WriteAllText(Path.Combine(outDir, "generated", "patch.hlsl"), "// patch");

        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                    AnchorShapes = new DrawShapeSet(new[] { new DrawShape(0, 144) }, 144),
                },
            },
            MaterialPatches = new[]
            {
                new MaterialPatchEmission("swap", 0, "patch", 2, "generated/patch.hlsl",
                    4978303, new[] { "45dbffd6cb513d80" }, 544),
            },
        });

        string full = Section(File.ReadAllText(Path.Combine(outDir, "mod.ini")),
            "[CommandListDraw_swap]");
        Assert.Equal(4, full.Split("drawindexed = ").Length - 1);
        Assert.Equal(1, full.Split("local $zz_material_ps_swap_s0 = ps").Length - 1);
        Assert.Equal(4, full.Split("$zz_material_ps_swap_s0 = ps").Length - 1);
    }

    [Fact]
    public void A_zero_count_material_refuses_while_its_folded_neighbor_wraps_every_draw()
    {
        string dumps = Path.Combine(_root, "zero-dumps");
        Migoto.SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "zero-donor");
        Migoto.SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 3);
        var shapes = new DrawShapeSet(new[]
        {
            new DrawShape(0, 60),
            new DrawShape(60, 84),
            new DrawShape(144, 0),
        }, 144);
        PoolBuildRequest Request(string name, int materialPosition)
        {
            string outDir = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(outDir, "generated"));
            File.WriteAllText(Path.Combine(outDir, "generated", "patch.hlsl"), "// patch");
            return new PoolBuildRequest
            {
                OutDir = outDir,
                Pipelines = new[]
                {
                    new ReplacePipeline
                    {
                        Suffix = "swap",
                        Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                        DonorDir = donor,
                        CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                        AnchorShapes = shapes,
                    },
                },
                MaterialPatches = new[]
                {
                    new MaterialPatchEmission("swap", materialPosition, "patch", 2,
                        "generated/patch.hlsl", 4978303, new[] { "45dbffd6cb513d80" }, 544),
                },
            };
        }

        var refused = Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().Build(Request("zero-empty", 2)));
        Assert.Contains("receives no donor draws", refused.Message);
        Assert.False(File.Exists(Path.Combine(_root, "zero-empty", "mod.ini")));

        string outDir = Path.Combine(_root, "zero-drawable");
        new MigotoEmitter().Build(Request("zero-drawable", 1));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        const string wrap = "local $zz_material_ps_swap_s1 = ps";
        string full = Section(ini, "[CommandListDraw_swap]");
        Assert.Equal(1, full.Split(wrap).Length - 1);
        Assert.Equal(2, full.Split("$zz_material_ps_swap_s1 = ps").Length - 1);
        Assert.Contains(wrap, Section(ini, "[CommandListDrawS1_swap]"));
        Assert.Contains(wrap, Section(ini, "[CommandListDrawS2_swap]"));
    }

    [Fact]
    public void A_patch_naming_a_missing_draw_or_a_conflicting_group_contract_refuses()
    {
        string dumps = Path.Combine(_root, "dumps");
        Migoto.SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "donor");
        Migoto.SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 2);
        PoolBuildRequest Request(string outName, params MaterialPatchEmission[] patches)
        {
            string outDir = Path.Combine(_root, outName);
            Directory.CreateDirectory(Path.Combine(outDir, "generated"));
            File.WriteAllText(Path.Combine(outDir, "generated", "patch.hlsl"), "// patch");
            return new PoolBuildRequest
            {
                OutDir = outDir,
                Pipelines = new[]
                {
                    new ReplacePipeline
                    {
                        Suffix = "swap",
                        Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                        DonorDir = donor,
                        CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                        AnchorShapes = new DrawShapeSet(
                            new[] { new DrawShape(0, 60), new DrawShape(60, 84) }, 144),
                    },
                },
                MaterialPatches = patches,
            };
        }

        var wrongSuffix = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(
            Request("o1", new MaterialPatchEmission("nosuch", 0, "k1", 2, "generated/patch.hlsl",
                100, new[] { "45dbffd6cb513d80" }, 544))));
        Assert.Contains("does not draw", wrongSuffix.Message);

        var wrongSubmesh = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(
            Request("o2", new MaterialPatchEmission("swap", 7, "k1", 2, "generated/patch.hlsl",
                100, new[] { "45dbffd6cb513d80" }, 544))));
        Assert.Contains("receives no donor draws", wrongSubmesh.Message);

        var splitFilter = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(
            Request("o3",
                new MaterialPatchEmission("swap", 0, "k1", 2, "generated/patch.hlsl",
                    100, new[] { "45dbffd6cb513d80" }, 544),
                new MaterialPatchEmission("swap", 1, "k2", 2, "generated/patch.hlsl",
                    200, new[] { "45dbffd6cb513d80" }, 544))));
        Assert.Contains("one shader carries one value", splitFilter.Message);

        var splitWidth = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(
            Request("o4",
                new MaterialPatchEmission("swap", 0, "k1", 2, "generated/patch.hlsl",
                    100, new[] { "45dbffd6cb513d80" }, 544),
                new MaterialPatchEmission("swap", 0, "k2", 2, "generated/patch.hlsl",
                    100, new[] { "45dbffd6cb513d80" }, 592))));
        Assert.Contains("constant-buffer byte width", splitWidth.Message);

        var missingShader = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(
            Request("o5", new MaterialPatchEmission("swap", 0, "k1", 2, "generated/absent.hlsl",
                100, new[] { "45dbffd6cb513d80" }, 544))));
        Assert.Contains("not in the mod folder", missingShader.Message);
    }

    [Fact]
    public void Material_patch_contract_refuses_unsafe_keys_slots_and_shader_paths()
    {
        string dumps = Path.Combine(_root, "contract-dumps");
        Migoto.SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "contract-donor");
        Migoto.SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 2);
        PoolBuildRequest Request(string name, string key, int slot, string shader)
        {
            string outDir = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(outDir, "generated"));
            File.WriteAllText(Path.Combine(outDir, "generated", "patch.hlsl"), "// patch");
            return new PoolBuildRequest
            {
                OutDir = outDir,
                Pipelines = new[]
                {
                    new ReplacePipeline
                    {
                        Suffix = "swap",
                        Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                        DonorDir = donor,
                        CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                        AnchorShapes = new DrawShapeSet(new[] { new DrawShape(0, 144) }, 144),
                    },
                },
                MaterialPatches = new[]
                {
                    new MaterialPatchEmission("swap", 0, key, slot, shader, 100,
                        new[] { "45dbffd6cb513d80" }, 544),
                },
            };
        }

        int invalid = 0;
        foreach (string key in new[] { "bad key", "bad[", "bad]", "bad=key", "bad\nkey" })
        {
            var refused = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(
                Request("unsafe-key-" + invalid++, key, 2, "generated/patch.hlsl")));
            Assert.Contains("safe alphabet", refused.Message);
        }
        foreach (int slot in new[] { -1, 14 })
        {
            var refused = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(
                Request("unsafe-slot-" + slot, "safe", slot, "generated/patch.hlsl")));
            Assert.Contains("outside 0..13", refused.Message);
        }

        string outside = Path.Combine(_root, "outside.hlsl");
        File.WriteAllText(outside, "// outside");
        Assert.Contains("escapes the mod folder", Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().Build(Request("rooted-shader", "safe", 2, outside))).Message);
        Assert.Contains("escapes the mod folder", Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().Build(Request("escaped-shader", "safe", 2,
                "../outside.hlsl"))).Message);

        string accepted = Path.Combine(_root, "safe-contract");
        new MigotoEmitter().Build(Request("safe-contract", "safe-key_1", 13,
            "generated/patch.hlsl"));
        Assert.True(File.Exists(Path.Combine(accepted, "mod.ini")));
    }

    [Fact]
    public void Routed_draws_with_disagreeing_shape_sets_refuse()
    {
        string dumps = Path.Combine(_root, "routed-shape-dumps");
        Migoto.SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        Migoto.SyntheticPool.WritePartDump(Path.Combine(dumps, "beta"), seed: 20, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        string firstDonor = Path.Combine(_root, "routed-shape-first");
        string secondDonor = Path.Combine(_root, "routed-shape-second");
        Migoto.SyntheticPool.WriteDonor(firstDonor, verts: 8, unionBones: 2, submeshes: 2);
        Migoto.SyntheticPool.WriteDonor(secondDonor, verts: 8, unionBones: 2, submeshes: 2);
        string outDir = Path.Combine(_root, "routed-shape-out");
        Directory.CreateDirectory(outDir);

        var refused = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(
            new PoolBuildRequest
            {
                OutDir = outDir,
                Pipelines = new[]
                {
                    new ReplacePipeline
                    {
                        Suffix = "first",
                        Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                        DonorDir = firstDonor,
                        CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                        AnchorShapes = new DrawShapeSet(
                            new[] { new DrawShape(0, 60), new DrawShape(60, 84) }, 144),
                    },
                    new ReplacePipeline
                    {
                        Suffix = "second",
                        Parts = new[] { new PoolPart("beta", Path.Combine(dumps, "beta")) },
                        DonorDir = secondDonor,
                        CaptureHashes = new Dictionary<string, string> { ["beta"] = "aaaa1111" },
                        AnchorShapes = new DrawShapeSet(
                            new[] { new DrawShape(0, 72), new DrawShape(72, 72) }, 144),
                    },
                },
            }));

        Assert.Contains("disagree on their target draw shapes", refused.Message);
    }

    [Fact]
    public void Material_patches_accept_rigid_lod_shapes_with_the_same_fold_signature()
    {
        string donor = Path.Combine(_root, "rigid-shape-donor");
        Migoto.SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 2);
        File.WriteAllText(Path.Combine(donor, "meta.json"),
            "{ \"mesh\": \"donor\", \"verts\": 8, \"indexFormat\": \"R16_UINT\", "
            + "\"streams\": [{ \"stream\": 0, \"stride\": 40 }, "
            + "{ \"stream\": 1, \"stride\": 20 }], "
            + "\"submeshes\": [{ \"firstByte\": 0, \"indexCount\": 12, \"baseVertex\": 0 }, "
            + "{ \"firstByte\": 24, \"indexCount\": 12, \"baseVertex\": 0 }] }");
        string outDir = Path.Combine(_root, "rigid-shape-out");
        Directory.CreateDirectory(Path.Combine(outDir, "generated"));
        File.WriteAllText(Path.Combine(outDir, "generated", "patch.hlsl"), "// patch");

        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = Array.Empty<ReplacePipeline>(),
            Rigids = new[]
            {
                new RigidReplace
                {
                    Suffix = "swap", DonorDir = donor, Hash = "aaaa1111",
                    TierHashes = new[] { "bbbb2222" },
                    ShapesByHash = new Dictionary<string, DrawShapeSet>
                    {
                        ["aaaa1111"] = new(new[]
                            { new DrawShape(0, 60), new DrawShape(60, 84) }, 144),
                        ["bbbb2222"] = new(new[]
                            { new DrawShape(0, 30), new DrawShape(30, 42) }, 72),
                    },
                },
            },
            MaterialPatches = new[]
            {
                new MaterialPatchEmission("swap", 0, "patch", 2,
                    "generated/patch.hlsl", 100, new[] { "45dbffd6cb513d80" }, 544),
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        const string wrap = "local $zz_material_ps_swap_s0 = ps";
        Assert.Contains(wrap, Section(ini, "[CommandListRigidS0_swap]"));
        foreach (string header in new[]
                 {
                     "[TextureOverride_Rigid_swap_DrawS0]",
                     "[TextureOverride_Rigid_swap_1_DrawS0]",
                 })
            Assert.Contains("run = CommandListRigidS0_swap", Section(ini, header));
    }

    [Fact]
    public void Material_patches_refuse_rigid_hashes_with_disagreeing_fold_signatures()
    {
        string donor = Path.Combine(_root, "rigid-fold-donor");
        Migoto.SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 2);
        File.WriteAllText(Path.Combine(donor, "meta.json"),
            "{ \"mesh\": \"donor\", \"verts\": 8, \"indexFormat\": \"R16_UINT\", "
            + "\"streams\": [{ \"stream\": 0, \"stride\": 40 }, "
            + "{ \"stream\": 1, \"stride\": 20 }], "
            + "\"submeshes\": [{ \"firstByte\": 0, \"indexCount\": 12, \"baseVertex\": 0 }, "
            + "{ \"firstByte\": 24, \"indexCount\": 12, \"baseVertex\": 0 }] }");
        string outDir = Path.Combine(_root, "rigid-fold-out");
        Directory.CreateDirectory(Path.Combine(outDir, "generated"));
        File.WriteAllText(Path.Combine(outDir, "generated", "patch.hlsl"), "// patch");

        var refused = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(
            new PoolBuildRequest
            {
                OutDir = outDir,
                Pipelines = Array.Empty<ReplacePipeline>(),
                Rigids = new[]
                {
                    new RigidReplace
                    {
                        Suffix = "swap", DonorDir = donor, Hash = "aaaa1111",
                        TierHashes = new[] { "bbbb2222" },
                        ShapesByHash = new Dictionary<string, DrawShapeSet>
                        {
                            ["aaaa1111"] = new(new[]
                                { new DrawShape(0, 60), new DrawShape(60, 84) }, 144),
                            ["bbbb2222"] = new(new[]
                                { new DrawShape(0, 72), new DrawShape(72, 0) }, 72),
                        },
                    },
                },
                MaterialPatches = new[]
                {
                    new MaterialPatchEmission("swap", 0, "patch", 2,
                        "generated/patch.hlsl", 100, new[] { "45dbffd6cb513d80" }, 544),
                },
            }));

        Assert.Contains("hashes that disagree on target draw shapes", refused.Message);
    }

    [Fact]
    public void Material_patches_refuse_pipe_tiers_with_disagreeing_fold_signatures()
    {
        string dumps = Path.Combine(_root, "pipe-fold-dumps");
        string lod0 = Path.Combine(dumps, "alpha");
        string lod1 = Path.Combine(dumps, "alpha_lod1");
        Migoto.SyntheticPool.WritePartDump(lod0, seed: 10, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        Migoto.SyntheticPool.WritePartDump(lod1, seed: 11, verts: 64,
            boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "pipe-fold-donor");
        Migoto.SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 2);
        string outDir = Path.Combine(_root, "pipe-fold-out");
        Directory.CreateDirectory(Path.Combine(outDir, "generated"));
        File.WriteAllText(Path.Combine(outDir, "generated", "patch.hlsl"), "// patch");

        var refused = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(
            new PoolBuildRequest
            {
                OutDir = outDir,
                Pipelines = new[]
                {
                    new ReplacePipeline
                    {
                        Suffix = "swap",
                        Parts = new[] { new PoolPart("alpha", lod0) },
                        DonorDir = donor,
                        CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                        AnchorShapes = new DrawShapeSet(
                            new[] { new DrawShape(0, 60), new DrawShape(60, 84) }, 144),
                        Tiers = new[]
                        {
                            new PoolTier("alpha", "alpha_lod1", "lod1", lod1, "bbbb2222",
                                Shapes: new DrawShapeSet(
                                    new[] { new DrawShape(0, 72), new DrawShape(72, 0) }, 72)),
                        },
                    },
                },
                MaterialPatches = new[]
                {
                    new MaterialPatchEmission("swap", 0, "patch", 2,
                        "generated/patch.hlsl", 100, new[] { "45dbffd6cb513d80" }, 544),
                },
            }));

        Assert.Contains("tiers that disagree on target draw shapes", refused.Message);
    }

    private static string Section(string ini, string header)
    {
        int start = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"section missing: {header}");
        int end = ini.IndexOf("\n[", start + header.Length, StringComparison.Ordinal);
        return end < 0 ? ini[start..] : ini[start..end];
    }

    private static IReadOnlyList<string> RawUavCopyResourceDescriptorErrors(string ini)
    {
        string normalized = ini.Replace("\r\n", "\n", StringComparison.Ordinal);
        string[] lines = normalized.Split('\n');
        var errors = new List<string>();
        string[] sections = normalized.Split("\n[", StringSplitOptions.None);
        for (int index = 0; index < sections.Length; index++)
        {
            string section = index == 0 ? sections[index] : "[" + sections[index];
            if (!section.StartsWith("[Resource", StringComparison.Ordinal)) continue;
            int headerEnd = section.IndexOf(']');
            if (headerEnd < 0) continue;
            string resource = section[1..headerEnd];
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in section[(headerEnd + 1)..].Split('\n'))
            {
                string line = rawLine.Trim();
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                fields.TryAdd(line[..equals].Trim(), line[(equals + 1)..].Trim());
            }
            if (!fields.TryGetValue("type", out string? type)
                || !(type.StartsWith("RW", StringComparison.OrdinalIgnoreCase)
                    || type.Contains("ByteAddressBuffer", StringComparison.OrdinalIgnoreCase)))
                continue;

            string? firstFill = lines.Select(line => line.Trim()).FirstOrDefault(line =>
            {
                int equals = line.IndexOf('=');
                return equals > 0
                    && string.Equals(line[..equals].Trim(), resource, StringComparison.Ordinal);
            });
            if (firstFill is null) continue;
            string source = firstFill[(firstFill.IndexOf('=') + 1)..].Trim();
            string[] copyParts = source.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string slot = copyParts.Length == 2 ? copyParts[1] : "";
            bool shaderStage = slot.Length >= 2
                && (slot.AsSpan(0, 2).Equals("vs", StringComparison.OrdinalIgnoreCase)
                    || slot.AsSpan(0, 2).Equals("hs", StringComparison.OrdinalIgnoreCase)
                    || slot.AsSpan(0, 2).Equals("ds", StringComparison.OrdinalIgnoreCase)
                    || slot.AsSpan(0, 2).Equals("gs", StringComparison.OrdinalIgnoreCase)
                    || slot.AsSpan(0, 2).Equals("ps", StringComparison.OrdinalIgnoreCase)
                    || slot.AsSpan(0, 2).Equals("cs", StringComparison.OrdinalIgnoreCase));
            bool copiedFromCb = copyParts.Length == 2
                && string.Equals(copyParts[0], "copy", StringComparison.OrdinalIgnoreCase)
                && slot.Length >= 6
                && shaderStage
                && slot.AsSpan(2, 3).Equals("-cb", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(slot.AsSpan(5), out _);
            if (!copiedFromCb) continue;

            if (!fields.ContainsKey("bind_flags"))
                errors.Add($"[{resource}] is a raw/UAV resource first copied from a cb slot but has no bind_flags");
            if (!fields.ContainsKey("misc_flags"))
                errors.Add($"[{resource}] is a raw/UAV resource first copied from a cb slot but has no misc_flags");
        }
        return errors;
    }

    [Fact]
    public void A_colour_field_patches_four_contiguous_floats()
    {
        var request = ProjectValueRequest("_StockingCenterColor", "0.5 0.25 1 1");
        var operation = MaterialValueBuildSupport.Resolve(request,
            Render(request.CurrentSlot, MaterialValueCatalog.UnityPerMaterial544,
                semantic: "_StockingCenterColor"));

        Assert.Equal(BuildPlanVerdict.Resolved, operation.Decision.Verdict);
        var patch = Assert.IsType<MaterialConstantBufferPatch>(
            Assert.Single(operation.Emissions!).MaterialPatch);
        Assert.Equal(new[] { 80, 84, 88, 92 }, patch.Writes.Select(write => write.ByteOffset));
        Assert.All(patch.Writes, write => Assert.Equal("_StockingCenterColor", write.Semantic));

        byte[] live = Enumerable.Range(0, 544).Select(i => unchecked((byte)(i * 31 + 7))).ToArray();
        byte[] patched = MaterialConstantBufferPatcher.Apply(patch, live);
        Assert.Equal(0.5f, BitConverter.ToSingle(patched, 80));
        Assert.Equal(0.25f, BitConverter.ToSingle(patched, 84));
        Assert.Equal(1f, BitConverter.ToSingle(patched, 88));
        Assert.Equal(1f, BitConverter.ToSingle(patched, 92));
        for (int i = 0; i < live.Length; i++)
            if (i is < 80 or >= 96) Assert.Equal(live[i], patched[i]);
        Assert.Equal(4, MaterialValuePatchEmitter.EmitShader(patch)
            .Split("material_state.Store(").Length - 1);
    }

    [Fact]
    public void A_field_absent_from_the_active_layout_refuses()
    {
        // _Anisotropy sits only in the 544 shape; a 592 carrier cannot patch it
        var request = ProjectValueRequest("_Anisotropy", "2.5");
        var operation = MaterialValueBuildSupport.Resolve(request,
            Render(request.CurrentSlot, MaterialValueCatalog.UnityPerMaterial592,
                semantic: "_Anisotropy"));
        Assert.Equal(BuildPlanVerdict.Unsupported, operation.Decision.Verdict);
        // One sentence per screen, whichever way the shader turned the value down; the catalog's own
        // account of which layout and which declaration disagreed goes to the log.
        Assert.Contains("cannot be set on this material's shader", operation.Decision.Reason);
        Assert.Contains("not declared", operation.Decision.Detail);
    }

    [Theory]
    [InlineData("_UseGIFlatten", "0", true, "0")]
    [InlineData("_UseGIFlatten", "1", true, "1")]
    [InlineData("_UseGIFlatten", "0.37", false, "")]           // family rule is two-state
    [InlineData("_Anisotropy", "2.5", true, "2.5")]
    [InlineData("_Anisotropy", "1 2", false, "")]              // one float, not two
    [InlineData("_StockingCenterColor", "0.5, 0.25, 1, 1", true, "0.5 0.25 1 1")]
    [InlineData("_StockingCenterColor", "0.5 0.25", false, "")] // a colour is four floats
    [InlineData("_StockingCenterColor", "0.5 0.25 1 oops", false, "")]
    [InlineData("_NotAField", "1", false, "")]
    public void Value_parsing_follows_the_field_shape(string semantic, string encoded,
        bool ok, string canonical)
    {
        Assert.Equal(ok, MaterialValueBuildSupport.TryValues(semantic, encoded, out _,
            out string got));
        if (ok) Assert.Equal(canonical, got);
    }

    [Fact]
    public void Nonzero_value_has_a_nonzero_encoding_and_layout_guard()
    {
        var request = ProjectValueRequest(MaterialValueSemantics.UseGiFlatten, "1");
        var operation = MaterialValueBuildSupport.Resolve(request,
            Render(request.CurrentSlot, MaterialValueCatalog.UnityPerMaterial544));
        var patch = Assert.IsType<MaterialConstantBufferPatch>(
            Assert.Single(operation.Emissions!).MaterialPatch);

        byte[] patched = MaterialConstantBufferPatcher.Apply(patch, new byte[544]);
        Assert.Equal(new byte[] { 0, 0, 128, 63 }, patched[492..496]);
        string shader = MaterialValuePatchEmitter.EmitShader(patch);
        Assert.Contains("if (material_bytes != 544u) return;", shader);
        Assert.Contains("material_state.Store(492, 0x3f800000u);", shader);
    }

    [Fact]
    public void Semantic_proposal_and_build_require_an_active_shader_field_proof()
    {
        var request = ProjectValueRequest(MaterialValueSemantics.UseGiFlatten, "1");
        var unproved = Render(request.CurrentSlot, MaterialValueCatalog.UnityPerMaterial544,
            proveField: false);
        var proposal = MaterialSourceDifferenceResolver.Propose("edit-body", "Cloth source",
            new[]
            {
                new MaterialDifferenceCandidate(request.CurrentSlot.Id, "GI flatten",
                    MaterialDifferenceKind.SemanticValue, "0", "1",
                    MaterialValueSemantics.UseGiFlatten,
                    Source(request.CurrentSlot.Id, "source-gi")),
            }, unproved.Contracts);

        Assert.Equal(MaterialDifferenceDisposition.Unsupported,
            Assert.Single(proposal.Differences).Disposition);
        Assert.Contains("does not prove", Assert.Single(proposal.Differences).Detail);
        var operation = MaterialValueBuildSupport.Resolve(request, unproved);
        Assert.Equal(BuildPlanVerdict.Unsupported, operation.Decision.Verdict);
        Assert.Empty(operation.Emissions!);

        var shortLayout = Render(request.CurrentSlot,
            MaterialValueCatalog.UnityPerMaterial144);
        var shortProposal = MaterialSourceDifferenceResolver.Propose("edit-body", "Cloth source",
            new[]
            {
                new MaterialDifferenceCandidate(request.CurrentSlot.Id, "GI flatten",
                    MaterialDifferenceKind.SemanticValue, "0", "1",
                    MaterialValueSemantics.UseGiFlatten,
                    Source(request.CurrentSlot.Id, "source-gi")),
            }, shortLayout.Contracts);
        Assert.Equal(MaterialDifferenceDisposition.Unsupported,
            Assert.Single(shortProposal.Differences).Disposition);
        Assert.Contains("not declared", Assert.Single(shortProposal.Differences).Detail);
    }

    [Fact]
    public void Every_active_contract_gets_a_distinct_guarded_patch_artifact()
    {
        var request = ProjectValueRequest(MaterialValueSemantics.UseGiFlatten, "1");
        var render = Render(request.CurrentSlot, MaterialValueCatalog.UnityPerMaterial544);
        var second = render.Contracts[0] with
        {
            Id = "material-draw-second",
            TargetingProof = new BuildTargetingProof("draw-signature",
                "45dbffd6cb513d80/submesh-1"),
        };
        var operation = MaterialValueBuildSupport.Resolve(request,
            render with { Contracts = new[] { render.Contracts[0], second } });
        Assert.Equal(2, operation.Emissions!.Count);
        Assert.Equal(2, operation.OutputArtifacts!.Count);
        Assert.Equal(new[] { "edit-body:slot-value:material-value:0",
                "edit-body:slot-value:material-value:1" },
            operation.Emissions.Select(emission => emission.Id));

        var plan = new AuthoredBuildPlan
        {
            RuntimeEmissions = operation.Emissions.Select(emission => new PlannedRuntimeEmission(
                request.RowId, operation.Decision.Verdict, emission)).ToArray(),
            OutputArtifacts = operation.OutputArtifacts.Select(output => new PlannedOutputArtifact(
                request.RowId, operation.Decision.Verdict, output)).ToArray(),
        };
        var files = MaterialValuePatchEmitter.Emit(plan);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.File.EndsWith("_0.hlsl", StringComparison.Ordinal));
        Assert.Contains(files, file => file.File.EndsWith("_1.hlsl", StringComparison.Ordinal));
        Assert.All(files, file => Assert.Contains("material_bytes != 544u", file.Text));
    }

    [Fact]
    public void Explicit_dynamic_value_and_unknown_layout_block_without_output()
    {
        var dynamic = ProjectValueRequest("_AoeSelectColor", "1");
        var dynamicResult = MaterialValueBuildSupport.Resolve(dynamic,
            Render(dynamic.CurrentSlot, MaterialValueCatalog.UnityPerMaterial544));
        Assert.Equal(BuildPlanVerdict.Unsupported, dynamicResult.Decision.Verdict);
        Assert.Empty(dynamicResult.Emissions!);
        Assert.Empty(dynamicResult.OutputArtifacts!);

        var supported = ProjectValueRequest(MaterialValueSemantics.UseGiFlatten, "1");
        var unknownLayout = MaterialValueBuildSupport.Resolve(supported,
            Render(supported.CurrentSlot, "unmeasured-layout"));
        Assert.Equal(BuildPlanVerdict.Unsupported, unknownLayout.Decision.Verdict);
        Assert.Empty(unknownLayout.Emissions!);
        Assert.Empty(unknownLayout.OutputArtifacts!);
    }

    [Theory]
    [InlineData("body_skinuber", "skinuber", true)]
    [InlineData("body_faceuber (Instance)", "faceuber", true)]
    [InlineData("cloth_uber", "uber", false)]
    public void Material_family_reader_uses_the_runtime_family_token(string material,
        string expectedFamily, bool flatten)
    {
        string family = Assert.IsType<string>(MaterialFamilyClassifier.Family(material));
        Assert.Equal(expectedFamily, family);
        Assert.Equal(flatten, MaterialFamilyClassifier.UsesGiFlatten(family));
    }

    [Fact]
    public void Material_value_bindings_require_the_same_semantic()
    {
        var sourceProject = SourceProject();
        sourceProject.TargetSlots.Single(slot => slot.Id == "source-gi").Semantic = "_OtherValue";
        Assert.Contains(AuthoredProjectValidator.Errors(sourceProject),
            error => error.Contains("source slot has another material-value semantic",
                StringComparison.Ordinal));

        var assetProject = SourceProject();
        assetProject.EditDefinitions[0].Bindings[0] = new Binding
        {
            SlotId = "slot-gi",
            Kind = BindingKind.ProjectAsset,
            ProjectAssetId = "asset-wrong",
        };
        assetProject.ProjectAssets.Add(new ProjectAsset
        {
            Id = "asset-wrong",
            Kind = ProjectAssetKind.StructuredValue,
            Label = "Wrong value",
            File = "values/wrong.json",
            Value = new ProjectAssetValue { Semantic = "_OtherValue", Value = "0" },
        });
        Assert.Contains(AuthoredProjectValidator.Errors(assetProject),
            error => error.Contains("binds another material-value semantic", StringComparison.Ordinal));
    }

    [Fact]
    public void Material_patch_emission_requires_a_patch_payload()
    {
        var project = SourceProject();
        var backend = new MaterialBackend(MaterialValueCatalog.UnityPerMaterial544,
            new MaterialFamilyValueReader())
        {
            StripPatchPayload = true,
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        var binding = Assert.Single(plan.Bindings,
            row => row.AuthoredSlot.Input == TargetInputKind.MaterialValue);
        Assert.Equal(BuildPlanVerdict.Conflict, binding.Decision.Verdict);
        Assert.Contains("has no material patch", binding.Decision.Detail);
        Assert.False(plan.CanBuild);
    }

    [Fact]
    public void Material_value_at_a_position_past_the_replacement_geometry_is_dropped_with_a_warning()
    {
        var project = SourceProject(new[] { 3 });
        MoveMaterialValueTo(project, 1);

        var plan = AuthoredBuildPlanner.Plan(project,
            new MaterialBackend(MaterialValueCatalog.UnityPerMaterial544,
                new MaterialFamilyValueReader()));

        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        Assert.DoesNotContain(plan.RuntimeEmissions,
            row => row.Emission.Kind == BuildEmissionKind.MaterialValuePatch);
        Assert.Contains(plan.Warnings, warning => warning.Contains(
            "no faces in its replacement mesh use material 1", StringComparison.Ordinal));
        _ = AuthoredBuildExecution.Create(project, plan);
    }

    [Fact]
    public void Material_value_past_the_install_mesh_submeshes_is_dropped_before_emission()
    {
        var project = SourceProject(new[] { 3, 3 });
        MoveMaterialValueTo(project, 1);
        LegacyResolvedPart Resolve(TargetPart part)
        {
            var slot = project.TargetSlots.First(candidate => candidate.Part.SameAs(part)
                && candidate.Input == TargetInputKind.MaterialValue);
            return new LegacyResolvedPart(part, slot.Renderer!, slot.Mesh!, new[]
            {
                new LegacyResolvedMaterial(1, slot.Material!.Name ?? "material", slot.Material,
                    Array.Empty<LegacyResolvedTexture>()),
            }, MaterialIndexCounts: new[] { 3 });
        }
        var evidence = new MaterialRenderEvidence("character-color",
            new[] { "45dbffd6cb513d80" }, 4978303,
            MaterialValueCatalog.UnityPerMaterial544,
            new[]
            {
                new BuildMaterialValueField(MaterialValueSemantics.UseGiFlatten, 2, 492,
                    "fixture reflection"),
            }, "fixture reflection");
        var backend = new ProductionAuthoredBuildBackend(Resolve, _ => evidence,
            new MaterialFamilyValueReader());

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        Assert.DoesNotContain(plan.RuntimeEmissions,
            row => row.Emission.Kind == BuildEmissionKind.MaterialValuePatch);
        Assert.Contains(plan.Warnings, warning => warning.Contains(
            "no faces in its replacement mesh use material 1", StringComparison.Ordinal));
        Assert.Equal(0, plan.Bindings.Single(row => row.AuthoredSlot.Id == "slot-gi")
            .CurrentSlot!.DrawIndexCount);
        _ = AuthoredBuildExecution.Create(project, plan);
    }

    [Fact]
    public void Empty_install_position_drops_even_when_replacement_counts_cannot_be_read()
    {
        var project = SourceProject(new[] { 3, 3 });
        MoveMaterialValueTo(project, 1, drawIndexCount: 0);
        string geometry = project.ProjectAssets.Single(asset => asset.Kind == ProjectAssetKind.Geometry).File;
        File.WriteAllText(Path.Combine(_root,
            geometry.Replace('/', Path.DirectorySeparatorChar)), "not a glb");

        var plan = AuthoredBuildPlanner.Plan(project,
            new MaterialBackend(MaterialValueCatalog.UnityPerMaterial544,
                new MaterialFamilyValueReader()));

        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        Assert.DoesNotContain(plan.RuntimeEmissions,
            row => row.Emission.Kind == BuildEmissionKind.MaterialValuePatch);
        Assert.Contains(plan.Warnings, warning => warning.Contains(
            "no faces in its replacement mesh use material 1", StringComparison.Ordinal));
        _ = AuthoredBuildExecution.Create(project, plan);
    }

    [Fact]
    public void Material_value_without_a_replacement_is_dropped_with_a_warning()
    {
        var project = SourceProject(omitReplacement: true);
        AddOverlay(project);

        var plan = AuthoredBuildPlanner.Plan(project,
            new MaterialBackend(MaterialValueCatalog.UnityPerMaterial544,
                new MaterialFamilyValueReader()));

        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
        Assert.DoesNotContain(plan.RuntimeEmissions,
            row => row.Emission.Kind == BuildEmissionKind.MaterialValuePatch);
        Assert.Contains(plan.Warnings, warning => warning.Contains(
            "they apply only through this edit's own mesh replacement", StringComparison.Ordinal));
        var execution = AuthoredBuildExecution.Create(project, plan);
        Assert.Single(execution.Work);
    }

    [Fact]
    public void Incompatible_values_for_one_material_draw_conflict_at_plan_scope()
    {
        var project = SourceProject(new[] { 3, 3 });
        var target = project.TargetSlots.Single(slot => slot.Id == "slot-gi");
        // The part's second material position, carrying the same material as its first. Two places, one
        // draw: a slot is one position of one part, so a second value for the same draw arrives as its own
        // position rather than as a second record of the one the first value already holds.
        var second = Slot("slot-gi-second", target.Part, 1001, "body_skinuber");
        second.SubmeshIndex = second.MaterialSlotIndex = 1;
        project.TargetSlots.Add(second);
        project.ProjectAssets.AddRange(new[]
        {
            ValueAsset("asset-zero", "values/zero.json", "0"),
            ValueAsset("asset-one", "values/one.json", "1"),
        });
        Directory.CreateDirectory(Path.Combine(_root, "values"));
        File.WriteAllText(Path.Combine(_root, "values", "zero.json"), "0");
        File.WriteAllText(Path.Combine(_root, "values", "one.json"), "1");
        project.EditDefinitions[0].Bindings = new List<Binding>
        {
            AssetBinding("slot-geometry", "asset-geometry"),
            AssetBinding(target.Id, "asset-zero"),
            AssetBinding(second.Id, "asset-one"),
        };

        var plan = AuthoredBuildPlanner.Plan(project,
            new MaterialBackend(MaterialValueCatalog.UnityPerMaterial544,
                new MaterialFamilyValueReader())
            {
                RenderFactory = request => Render(request.CurrentSlot,
                    MaterialValueCatalog.UnityPerMaterial544, contractId: request.RowId,
                    carrierSlot: target),
            });

        Assert.Contains(plan.Conflicts,
            conflict => conflict.Contains("has conflicting values", StringComparison.Ordinal));
        Assert.False(plan.CanBuild);
    }

    [Fact]
    public void Shared_backend_contract_labels_do_not_conflict_across_distinct_draws()
    {
        var project = SourceProject(new[] { 3, 3 });
        var target = project.TargetSlots.Single(slot => slot.Id == "slot-gi");
        // Its own position and its own material: a distinct draw is a distinct place, not a second record
        // of the place the first draw already occupies.
        var second = Slot("slot-gi-second", target.Part, 3001, "body_skinuber");
        second.SubmeshIndex = second.MaterialSlotIndex = 1;
        project.TargetSlots.Add(second);
        project.ProjectAssets.AddRange(new[]
        {
            ValueAsset("asset-zero", "values/zero.json", "0"),
            ValueAsset("asset-one", "values/one.json", "1"),
        });
        Directory.CreateDirectory(Path.Combine(_root, "values"));
        File.WriteAllText(Path.Combine(_root, "values", "zero.json"), "0");
        File.WriteAllText(Path.Combine(_root, "values", "one.json"), "1");
        project.EditDefinitions[0].Bindings = new List<Binding>
        {
            AssetBinding("slot-geometry", "asset-geometry"),
            AssetBinding(target.Id, "asset-zero"),
            AssetBinding(second.Id, "asset-one"),
        };

        var plan = AuthoredBuildPlanner.Plan(project,
            new MaterialBackend(MaterialValueCatalog.UnityPerMaterial544,
                new MaterialFamilyValueReader()));

        Assert.DoesNotContain(plan.Conflicts,
            conflict => conflict.Contains("material patch", StringComparison.Ordinal));
        Assert.True(plan.CanBuild, string.Join(Environment.NewLine, plan.Conflicts));
    }

    [Fact]
    public void Different_toggle_keys_cannot_hide_overlapping_material_patch_conflicts()
    {
        var project = SourceProject();
        var sharedCarrier = project.TargetSlots.Single(slot => slot.Id == "slot-gi");
        var secondPart = Part("face");
        var second = Slot("slot-gi-second", secondPart, 3001, "face_skinuber");
        project.TargetSlots.Add(second);
        project.ProjectAssets.AddRange(new[]
        {
            ValueAsset("asset-zero", "values/zero.json", "0"),
            ValueAsset("asset-one", "values/one.json", "1"),
        });
        Directory.CreateDirectory(Path.Combine(_root, "values"));
        File.WriteAllText(Path.Combine(_root, "values", "zero.json"), "0");
        File.WriteAllText(Path.Combine(_root, "values", "one.json"), "1");
        project.EditDefinitions[0].Bindings = new List<Binding>
        {
            AssetBinding("slot-geometry", "asset-geometry"),
            AssetBinding(sharedCarrier.Id, "asset-zero"),
        };
        project.KeyFirstPart("F6");
        project.TargetSlots.Add(new TargetSlot
        {
            Id = "slot-geometry-face", Part = secondPart, Tier = "lod0",
            Input = TargetInputKind.Geometry,
            Renderer = Ref(secondPart.RendererSlot, 3001),
            Mesh = Ref(secondPart.RendererSlot + "_mesh", 3002),
        });
        project.EditDefinitions.Add(new EditDefinition
        {
            Id = "edit-face",
            Label = "Face",
            Target = secondPart,
            Bindings = new List<Binding>
            {
                AssetBinding("slot-geometry-face", "asset-geometry"),
                AssetBinding(second.Id, "asset-one"),
            },
        });
        project.Always.Add("edit-face");
        project.Keyed(secondPart, "F7");
        var backend = new MaterialBackend(MaterialValueCatalog.UnityPerMaterial544,
            new MaterialFamilyValueReader())
        {
            RenderFactory = request => Render(request.CurrentSlot,
                MaterialValueCatalog.UnityPerMaterial544, contractId: request.RowId,
                carrierSlot: sharedCarrier),
        };

        var plan = AuthoredBuildPlanner.Plan(project, backend);

        Assert.Contains(plan.Conflicts,
            conflict => conflict.Contains("has conflicting values on the same material",
                StringComparison.Ordinal));
    }

    private AuthoredProject SourceProject(int[]? replacementIndexCounts = null,
        bool omitReplacement = false)
    {
        var target = Part("body");
        var source = Part("cloth");
        var targetSlot = Slot("slot-gi", target, 1001, "body_skinuber");
        var sourceSlot = Slot("source-gi", source, 2001, "cloth_uber");
        var project = new AuthoredProject
        {
            RootDir = _root,
            Info = new ProjectInfo { Name = "Material fixture", Author = "TestAuthor" },
            AuthoredAgainst = new AuthoredAgainst { CatalogVersion = "fixture-build" },
            TargetSlots = new List<TargetSlot> { targetSlot, sourceSlot },
            EditDefinitions = new List<EditDefinition>
            {
                new()
                {
                    Id = "edit-body",
                    Label = "Cloth body",
                    Target = target,
                    Bindings = new List<Binding>
                    {
                        new()
                        {
                            SlotId = targetSlot.Id,
                            Kind = BindingKind.SourceSlot,
                            SourceSlot = new BindingSourceSlot { SlotId = sourceSlot.Id },
                        },
                    },
                },
            },
            Always = new List<string> { "edit-body" },
        };
        if (omitReplacement) return project;

        replacementIndexCounts ??= new[] { 3 };
        string relative = "assets/replacement.glb";
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        MeshGltf.ExportGlb(new UnityMesh
        {
            Name = "replacement",
            VertexCount = 3,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
            },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
            Submeshes = replacementIndexCounts.Select(_ => new[] { 0, 1, 2 }).ToList(),
        }, full);
        var geometry = new TargetSlot
        {
            Id = "slot-geometry",
            Part = target,
            Tier = "lod0",
            Input = TargetInputKind.Geometry,
            Renderer = Ref(target.RendererSlot, 1001),
            Mesh = Ref(target.RendererSlot + "_mesh", 1002),
        };
        project.TargetSlots.Add(geometry);
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "asset-geometry",
            Kind = ProjectAssetKind.Geometry,
            Label = "Replacement",
            File = relative,
        });
        project.EditDefinitions[0].Bindings.Insert(0,
            AssetBinding(geometry.Id, "asset-geometry"));
        return project;
    }

    private static void MoveMaterialValueTo(AuthoredProject project, int position,
        int? drawIndexCount = null)
    {
        foreach (var slot in project.TargetSlots.Where(slot => slot.Input == TargetInputKind.MaterialValue))
        {
            slot.SubmeshIndex = slot.MaterialSlotIndex = position;
            slot.DrawIndexCount = drawIndexCount;
        }
    }

    private void AddOverlay(AuthoredProject project)
    {
        var value = project.TargetSlots.Single(slot => slot.Id == "slot-gi");
        var overlay = new TargetSlot
        {
            Id = "slot-overlay", Part = value.Part, Tier = "lod0",
            SubmeshIndex = 0, MaterialSlotIndex = 0, Input = TargetInputKind.BaseColor,
            Renderer = value.Renderer, Mesh = value.Mesh, Material = value.Material,
        };
        project.TargetSlots.Add(overlay);
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "asset-overlay", Kind = ProjectAssetKind.Picture,
            Label = "Overlay", File = "textures/overlay.png",
            Source = new ProjectAssetSource { GameAsset = Ref("base", 4001) },
        });
        project.EditDefinitions[0].Bindings.Add(AssetBinding(overlay.Id, "asset-overlay"));
        string full = Path.Combine(_root, "textures", "overlay.png");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[] { 1 });
    }

    private static BuildBindingRequest ProjectValueRequest(string semantic, string value)
    {
        var part = Part("body");
        var slot = Slot("slot-value", part, 1001, "body_skinuber", semantic);
        var asset = new ProjectAsset
        {
            Id = "asset-value",
            Kind = ProjectAssetKind.StructuredValue,
            Label = semantic,
            File = "values/value.json",
            Value = new ProjectAssetValue { Semantic = semantic, Value = value },
        };
        return new BuildBindingRequest("edit-body:slot-value", "edit-body", slot, slot,
            new Binding
            {
                SlotId = slot.Id,
                Kind = BindingKind.ProjectAsset,
                ProjectAssetId = asset.Id,
            },
            new EffectiveBuildValue(EffectiveValueKind.ProjectAsset, asset, null,
                new[] { "edit-body:slot-value" }),
            BuildEmissionGate.Unconditional);
    }

    private static Binding Source(string slot, string source) => new()
    {
        SlotId = slot,
        Kind = BindingKind.SourceSlot,
        SourceSlot = new BindingSourceSlot { SlotId = source },
    };

    private static Binding AssetBinding(string slot, string asset) => new()
    {
        SlotId = slot,
        Kind = BindingKind.ProjectAsset,
        ProjectAssetId = asset,
    };

    private static ProjectAsset ValueAsset(string id, string file, string value) => new()
    {
        Id = id,
        Kind = ProjectAssetKind.StructuredValue,
        Label = "GI flatten " + value,
        File = file,
        Value = new ProjectAssetValue
        {
            Semantic = MaterialValueSemantics.UseGiFlatten,
            Value = value,
        },
    };

    private static TargetPart Part(string renderer) => new()
    {
        Subject = "Vesna",
        Outfit = "VesnaSSR01",
        RendererSlot = renderer,
    };

    private static TargetSlot Slot(string id, TargetPart part, long pathId, string material,
        string semantic = MaterialValueSemantics.UseGiFlatten) => new()
    {
        Id = id,
        Part = part,
        Tier = "lod0",
        SubmeshIndex = 0,
        MaterialSlotIndex = 0,
        Input = TargetInputKind.MaterialValue,
        Semantic = semantic,
        Renderer = Ref(part.RendererSlot, pathId),
        Mesh = Ref(part.RendererSlot + "_mesh", pathId + 1),
        Material = Ref(material, pathId + 2),
    };

    private static GameAssetRef Ref(string name, long pathId) => new()
    {
        GameBuild = "fixture-build",
        LogicalBundle = "fixture.bundle",
        PathId = pathId,
        Name = name,
    };

    private static BuildRenderPlan Render(TargetSlot slot, string layout, bool proveField = true,
        string contractId = "material-draw", TargetSlot? carrierSlot = null,
        string semantic = MaterialValueSemantics.UseGiFlatten)
    {
        var carrier = carrierSlot ?? slot;
        var proof = new BuildTargetingProof("draw-signature",
            "45dbffd6cb513d80/" + carrier.Id);
        int width = layout == MaterialValueCatalog.UnityPerMaterial544 ? 544
            : layout == MaterialValueCatalog.UnityPerMaterial592 ? 592 : 0;
        int? offset = width == 0 ? null : MaterialValueCatalog.Field(semantic)?.OffsetIn(width);
        IReadOnlyList<BuildMaterialValueField> fields = proveField && offset is { } at
            ? new[]
            {
                new BuildMaterialValueField(semantic, 2, at,
                    "DXBC reflection for the active color shader"),
            }
            : Array.Empty<BuildMaterialValueField>();
        return new BuildRenderPlan(new[]
        {
            Role(BuildRenderRoleKind.PoseAnchor, false, slot),
            Role(BuildRenderRoleKind.LayoutTarget, false, slot),
            Role(BuildRenderRoleKind.RenderCarrier, true, carrier, proof),
            Role(BuildRenderRoleKind.MaterialCarrier, true, carrier),
            Role(BuildRenderRoleKind.SuppressionTarget, false, slot),
        }, new[]
        {
            new RenderContract(contractId, carrier, carrier, proof, "character-color",
                "carrier-draw-space", "skinuber/45dbffd6cb513d80", layout, 2000,
                BuildTransparency.Opaque, "carrier-owned", BuildCullMode.Back,
                Enum.GetValues<BuildRenderPass>().Select(pass => new BuildPassCoverage(pass,
                    pass == BuildRenderPass.Color ? BuildCoverageState.Covered
                        : BuildCoverageState.NotApplicable,
                    pass == BuildRenderPass.Color ? "the value is read by the color pass"
                        : "this semantic is not consumed by the pass")).ToArray(),
                new BuildVisibilityDomain(new[] { "Fight", "Dorm" },
                    new[] { slot.Part.Outfit }, new[] { "lod0" },
                    slot.Part.Subject + "/" + slot.Part.Outfit,
                    "the carrier is scoped to one subject and outfit"),
                new BuildCarrierBounds(BuildBoundsBasis.Unavailable, null, null,
                    "a material patch does not change geometry bounds"), fields,
                new[] { "45dbffd6cb513d80" }),
        }, "the material carrier supplies one targetable color draw");
    }

    private static BuildRenderRole Role(BuildRenderRoleKind kind, bool covered, TargetSlot slot,
        BuildTargetingProof? proof = null) => covered
        ? new BuildRenderRole(kind, BuildCoverageState.Covered, slot, proof,
            "the current draw supplies " + kind)
        : new BuildRenderRole(kind, BuildCoverageState.NotApplicable, null, null,
            kind + " is not changed by a material-value patch");

    private sealed class MaterialBackend : IAuthoredBuildBackend
    {
        private readonly string _layout;
        private readonly IMaterialGameValueReader _reader;

        internal MaterialBackend(string layout, IMaterialGameValueReader reader)
        {
            _layout = layout;
            _reader = reader;
        }

        internal bool StripPatchPayload { get; init; }
        internal Func<BuildBindingRequest, BuildRenderPlan>? RenderFactory { get; init; }

        public BuildSlotResolution ResolveSlot(TargetSlot authoredSlot) => new(
            BuildPlanVerdict.Resolved, authoredSlot, "the structural slot resolves exactly");

        public BuildOperationResolution ResolveBinding(BuildBindingRequest request)
        {
            if (request.CurrentSlot.Input != TargetInputKind.MaterialValue)
            {
                var render = Render(request.CurrentSlot, "replacement-layout");
                var proof = render.Contracts[0].TargetingProof;
                bool geometry = request.CurrentSlot.Input == TargetInputKind.Geometry;
                string id = request.RowId + (geometry ? ":geometry" : ":resource");
                return new BuildOperationResolution(new BuildPlanDecision(BuildPlanVerdict.Resolved,
                        BuildRuntimeAction.BindProjectAsset, proof, "the replacement geometry resolves"),
                    render, new[]
                    {
                        new BuildRuntimeEmission(id, geometry ? BuildEmissionKind.GeometryReplacement
                                : BuildEmissionKind.ResourceBinding, proof,
                            request.Gate, new[] { render.Contracts[0].Id },
                            "the replacement geometry resolves"),
                    }, new[]
                    {
                        new BuildOutputArtifact(id + ":output", "compiled replacement", id, null,
                            true, new[] { id }, "the replacement geometry resolves"),
                    });
            }
            var operation = MaterialValueBuildSupport.Resolve(request,
                RenderFactory?.Invoke(request) ?? Render(request.CurrentSlot, _layout), _reader);
            if (!StripPatchPayload || operation.Emissions is not { Count: > 0 }) return operation;
            return operation with
            {
                Emissions = operation.Emissions.Select(emission =>
                    emission with { MaterialPatch = null }).ToArray(),
            };
        }

        public BuildOperationResolution ResolveVisibility(BuildVisibilityRequest request) =>
            throw new InvalidOperationException("fixture has no visibility operation");

        public BuildLifecycleResolution ResolveLifecycle(BuildLifecycleRequest request) => new(
            BuildPlanVerdict.Resolved,
            new BuildLifecyclePlan(request.LaunchCondition,
                Enum.GetValues<BuildLifecycleEvent>().Select(kind => new BuildLifecycleCoverage(kind,
                    kind == BuildLifecycleEvent.Toggle && request.LaunchCondition.IsAlways
                        ? BuildCoverageState.NotApplicable
                        : BuildCoverageState.Covered,
                    kind == BuildLifecycleEvent.Toggle && request.LaunchCondition.IsAlways
                        ? BuildLifecycleMechanism.NotApplicable
                        : kind == BuildLifecycleEvent.Toggle
                            ? BuildLifecycleMechanism.KeyGate
                        : kind == BuildLifecycleEvent.Reload
                            ? BuildLifecycleMechanism.ConfigurationReload
                            : BuildLifecycleMechanism.PerDrawMatch,
                    kind == BuildLifecycleEvent.Toggle && request.LaunchCondition.IsAlways
                        ? "no toggle is authored"
                        : kind == BuildLifecycleEvent.Toggle
                            ? "the authored key gates the material patch"
                        : "the runtime reevaluates the patch at this transition")).ToArray(),
                "the material patch follows the current draw lifecycle"),
            "the material patch has complete lifecycle coverage");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
    [Fact]
    public void Derived_evidence_resolves_nothing_without_readable_exact_objects()
    {
        // An unreadable bundle, a slot with no material ref, and a non-material path each resolve to
        // null — the plan then blocks the binding; nothing guesses.
        var unreadable = new DerivedMaterialEvidence(_ => null);
        Assert.Null(unreadable.Resolve(MaterialSlot("exact.bundle")));
        Assert.Null(unreadable.Resolve(new TargetSlot
        {
            Id = "no-material", Part = EvidencePart(), Input = TargetInputKind.MaterialValue,
            Semantic = MaterialValueSemantics.UseGiFlatten,
        }));
        // garbage bytes are a parse failure, not a crash
        var garbage = new DerivedMaterialEvidence(_ => new byte[] { 1, 2, 3, 4 });
        Assert.Null(garbage.Resolve(MaterialSlot("exact.bundle")));
    }

    [Fact]
    public void Derived_evidence_refuses_a_direct_shader_outside_the_character_bundle()
    {
        var evidence = EvidenceFor(_ => new byte[] { 11 }, DirectShading(),
            new[] { Variant(544, "1111111111111111") });

        Assert.Null(evidence.Resolve(MaterialSlot("other.bundle")));
    }

    [Fact]
    public void Derived_evidence_refuses_the_whole_keyword_family_when_any_layout_is_unsupported()
    {
        var evidence = EvidenceFor(_ => new byte[] { 12 }, DirectShading(), new[]
        {
            Variant(544, "2222222222222222"),
            Variant(48, "3333333333333333"),
        });

        Assert.Null(evidence.Resolve(MaterialSlot(DerivedMaterialEvidence.CharacterShaderBundle)));
    }

    [Fact]
    public void Derived_evidence_retries_a_failed_shader_bundle_read_and_then_memoizes_the_positive()
    {
        byte[] materialBytes = { 13 };
        byte[] shaderBytes = { 14 };
        int shaderReads = 0;
        var evidence = new DerivedMaterialEvidence(logical =>
            {
                if (logical == "material.bundle") return materialBytes;
                if (logical != DerivedMaterialEvidence.CharacterShaderBundle) return null;
                shaderReads++;
                return shaderReads == 1 ? null : shaderBytes;
            },
            (_, _) => ExternalShading(),
            (_, _) => new[] { Variant(544, "4444444444444444") },
            _ => "CAB-character");
        var slot = MaterialSlot("material.bundle");

        Assert.Null(evidence.Resolve(slot));
        Assert.NotNull(evidence.Resolve(slot));
        Assert.NotNull(evidence.Resolve(slot));
        Assert.Equal(2, shaderReads);
    }

    [Fact]
    public async Task Concurrent_evidence_reads_derive_and_store_one_positive_result()
    {
        byte[] bundle = Guid.NewGuid().ToByteArray();
        int active = 0, maximum = 0, materialReads = 0;
        var evidence = new DerivedMaterialEvidence(_ => bundle,
            (_, _) =>
            {
                Interlocked.Increment(ref materialReads);
                int now = Interlocked.Increment(ref active);
                int held;
                do
                {
                    held = Volatile.Read(ref maximum);
                    if (held >= now) break;
                }
                while (Interlocked.CompareExchange(ref maximum, now, held) != held);
                Thread.Sleep(30);
                Interlocked.Decrement(ref active);
                return DirectShading();
            },
            (_, _) => new[] { Variant(544, "5555555555555555") },
            _ => "CAB-character");
        var slot = MaterialSlot(DerivedMaterialEvidence.CharacterShaderBundle);
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return evidence.Resolve(slot);
        })).ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, maximum);
        Assert.Equal(1, materialReads);
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.NotNull(results[0]);
    }

    [Fact]
    public void The_family_filter_value_is_stable_and_exact_under_float_comparison()
    {
        var hashes = new[] { "45dbffd6cb513d80", "0175b3fa12ebdbc8" };
        int value = DerivedMaterialEvidence.FamilyFilterValue(hashes);
        Assert.Equal(value, DerivedMaterialEvidence.FamilyFilterValue(hashes.ToArray()));
        Assert.InRange(value, 1, 16_777_216);
        Assert.NotEqual(value,
            DerivedMaterialEvidence.FamilyFilterValue(new[] { "45dbffd6cb513d80" }));
    }

    private static DerivedMaterialEvidence EvidenceFor(Func<string, byte[]?> deobfuscate,
        BundleReader.MaterialShading shading, IReadOnlyList<ShaderVariant> variants) =>
        new(deobfuscate, (_, _) => shading, (_, _) => variants, _ => "CAB-character");

    private static BundleReader.MaterialShading DirectShading() =>
        Shading(shaderFileId: 0, Array.Empty<string>());

    private static BundleReader.MaterialShading ExternalShading() =>
        Shading(shaderFileId: 1, new[] { "CAB-character" });

    private static BundleReader.MaterialShading Shading(int shaderFileId,
        IReadOnlyList<string> externalCabs) => new("material",
        new HashSet<string>(StringComparer.Ordinal), shaderFileId, 91, externalCabs,
        new Dictionary<string, float>(), new Dictionary<string, float[]>());

    private static ShaderVariant Variant(int width, string hash) => new("character", 0, "Forward",
        new HashSet<string>(StringComparer.Ordinal), 2, width,
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [MaterialValueSemantics.UseGiFlatten] = 492,
        }, hash);

    private static TargetSlot MaterialSlot(string bundle) => new()
    {
        Id = "material-slot", Part = EvidencePart(), Input = TargetInputKind.MaterialValue,
        Semantic = MaterialValueSemantics.UseGiFlatten,
        Renderer = Game("prefab.bundle", 10, "renderer"),
        Mesh = Game("mesh.bundle", 20, "mesh"),
        Material = Game(bundle, 44, "same_material_name"),
    };

    private static TargetPart EvidencePart() => new()
    {
        Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "c_vesna_body_lod0",
    };

    private static GameAssetRef Game(string bundle, long pathId, string name) => new()
    {
        GameBuild = "26109", LogicalBundle = bundle, PathId = pathId, Name = name,
    };
}
