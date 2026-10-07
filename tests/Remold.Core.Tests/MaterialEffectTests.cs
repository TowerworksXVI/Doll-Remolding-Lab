using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

public sealed class MaterialEffectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "remold-shading-session-" + Guid.NewGuid().ToString("N"));

    private static readonly IReadOnlyList<MaterialEffectOperation> VolumeOperations = new[]
    {
        new MaterialEffectOperation("internal-volume", new[] { "00c38976368ab6ee" },
            new[] { new MaterialEffectBufferPatch(2, 544, new[]
            {
                new MaterialPatchWrite("_InsideColorContrast", 416, 0),
                new MaterialPatchWrite("_BaseInsideLerp", 432, 0),
                new MaterialPatchWrite("_FakeIntensity", 436, 0),
                new MaterialPatchWrite("_ReflectionIntensity", 440, 0),
                new MaterialPatchWrite("_UseMatcapRef", 448, 0),
            }) }, Array.Empty<MaterialEffectTexture>()),
        new MaterialEffectOperation("matcap", new[] { "00c38976368ab6ee" },
            new[] { new MaterialEffectBufferPatch(2, 544, new[]
            {
                new MaterialPatchWrite("_UseMatcapRef", 448, 0),
            }) }, Array.Empty<MaterialEffectTexture>()),
    };

    public MaterialEffectTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- what a compiled program carries, decided from the program ------------------------------------

    [Fact]
    public void Numeric_effects_come_from_the_programs_reads_not_its_declarations()
    {
        var program = Program(544, ("_StockingCenterColor", 80), ("_StockingFalloffColor", 96),
            ("_StockingFalloffPower", 112), ("_UseGlitter", 116), ("_AnisotropicGXX", 496));
        var effects = MaterialEffectRules.Evaluate(new[] { Reads(program, 80, 116) });

        Assert.Equal(new[] { "stocking", "glitter" }, effects.Select(effect => effect.EffectId));
        var stocking = Assert.Single(effects[0].Buffers);
        Assert.Equal(2, stocking.ConstantBufferSlot);
        Assert.Equal(544, stocking.ByteWidth);
        Assert.Equal(new[] { 80, 84, 88, 96, 100, 104, 112 }, stocking.Writes.Select(write => write.ByteOffset));
        Assert.All(stocking.Writes, write => Assert.Equal(1, write.Value));
        var glitter = Assert.Single(Assert.Single(effects[1].Buffers).Writes);
        Assert.Equal(("_UseGlitter", 116, 0f), (glitter.Semantic, glitter.ByteOffset, glitter.Value));

        // a program reading the anisotropic gate carries body anisotropy; one reading none of the
        // stocking colours carries no stocking tint however completely it declares them
        Assert.Equal(new[] { "body-anisotropy" },
            MaterialEffectRules.Evaluate(new[] { Reads(program, 496) }).Select(effect => effect.EffectId));
        Assert.Empty(MaterialEffectRules.Evaluate(new[] { program }));
    }

    [Fact]
    public void Detail_needs_a_neutral_mask_only_where_coverage_is_consumed()
    {
        var opaque = Reads(Program(592, ("_DetailAlbedoIntensity", 568), ("_DetailNormalIntensity", 572),
            ("_DetailRMIntensity", 576)), 568, 576);
        var detail = Assert.Single(MaterialEffectRules.Evaluate(new[] { opaque }));
        Assert.Equal("detail", detail.EffectId);
        Assert.Equal(new[] { 568, 576 }, Assert.Single(detail.Buffers).Writes.Select(write => write.ByteOffset));
        Assert.Empty(detail.Textures);

        var transparent = Reads(Program(592, ("_DetailAlbedoIntensity", 568), ("_DetailAlphaMode", 560),
            ("_DetailAlphaIntensity", 564)), 560, 564, 568) with
        {
            TextureSlots = new Dictionary<string, int> { ["_DetailMask"] = 6 },
        };
        var covered = Assert.Single(MaterialEffectRules.Evaluate(new[] { transparent }));
        Assert.Equal(new[] { 560, 564, 568 }, Assert.Single(covered.Buffers).Writes.Select(write => write.ByteOffset));
        Assert.Equal(new MaterialEffectTexture(6, 1, 0, 0, 1), Assert.Single(covered.Textures));

        // coverage consumed with no mask slot to neutralize is an unproven identity: no effect offered
        Assert.Empty(MaterialEffectRules.Evaluate(new[] { transparent with { TextureSlots = null } }));
    }

    [Fact]
    public void Internal_volume_carries_matcap_as_its_child_and_needs_the_whole_chain_declared()
    {
        var volume = Reads(Program(544, ("_InsideColorContrast", 416), ("_BaseInsideLerp", 432),
            ("_FakeIntensity", 436), ("_ReflectionIntensity", 440), ("_UseMatcapRef", 448)), 432);
        var effects = MaterialEffectRules.Evaluate(new[] { volume });

        Assert.Equal(new[] { "internal-volume", "matcap" }, effects.Select(effect => effect.EffectId));
        Assert.Equal(new[] { 416, 432, 436, 440, 448 },
            Assert.Single(effects[0].Buffers).Writes.Select(write => write.ByteOffset));
        Assert.Equal(448, Assert.Single(Assert.Single(effects[1].Buffers).Writes).ByteOffset);

        var partial = volume with { VectorOffsets = volume.VectorOffsets.Where(pair => pair.Key != "_FakeIntensity")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) };
        Assert.Empty(MaterialEffectRules.Evaluate(new[] { partial }));
    }

    [Fact]
    public void Pass_shader_and_keyword_rules_skip_draws_or_bind_neutral_textures()
    {
        var outline = Assert.Single(MaterialEffectRules.Evaluate(new[]
        {
            Program(0) with { PassName = "GFCharTransOutline", MaterialBufferSlot = null },
        }));
        Assert.Equal(("outline", true), (outline.EffectId, outline.SkipDraw));

        var eye = MaterialEffectRules.Evaluate(new[]
        {
            Program(96) with { ShaderName = "gf_shader/pbr/character/eyeblend_add", MaterialBufferSlot = 1 },
        });
        Assert.Equal("eye-add", Assert.Single(eye).EffectId);
        Assert.Equal("eye-multiply", Assert.Single(MaterialEffectRules.Evaluate(new[]
        {
            Program(96) with { ShaderName = "gf_shader/pbr/character/eyeblend_multiply", MaterialBufferSlot = 1 },
        })).EffectId);

        var fur = Program(544) with
        {
            Keywords = new HashSet<string> { "_USE_FUR_SHELL" },
            TextureSlots = new Dictionary<string, int> { ["_BlendTex"] = 4 },
        };
        var shells = Assert.Single(MaterialEffectRules.Evaluate(new[] { fur }));
        Assert.Equal("fur", shells.EffectId);
        Assert.Equal(new MaterialEffectTexture(4, 0, 0, 0, 0), Assert.Single(shells.Textures));
        Assert.Empty(MaterialEffectRules.Evaluate(new[] { fur with { TextureSlots = null } }));

        // a keyword stated by any row of the same bytecode counts
        Assert.Single(MaterialEffectRules.Evaluate(new[] { Program(544) with { TextureSlots = fur.TextureSlots }, fur }));
    }

    [Fact]
    public void Numeric_rules_require_the_material_buffer_at_the_measured_slot_and_width()
    {
        var program = Reads(Program(544, ("_UseGlitter", 116)), 116);
        Assert.Single(MaterialEffectRules.Evaluate(new[] { program }));
        Assert.Empty(MaterialEffectRules.Evaluate(new[] { program with { MaterialBufferSlot = 1 } }));
        Assert.Empty(MaterialEffectRules.Evaluate(new[] { program with { MaterialBufferWidth = 144 } }));
        Assert.Empty(MaterialEffectRules.Evaluate(new[] { program with { MaterialReads = null } }));
    }

    [Fact]
    public void Programs_sharing_one_recipe_fold_into_one_operation()
    {
        var a = Reads(Program(544, ("_UseGlitter", 116)), 116);
        var operations = MaterialEffectRules.Operations(new[]
        {
            ("BBBBBBBBBBBBBBBB", MaterialEffectRules.Evaluate(new[] { a })),
            ("aaaaaaaaaaaaaaaa", MaterialEffectRules.Evaluate(new[] { a })),
            ("cccccccccccccccc", MaterialEffectRules.Evaluate(new[] { a with { MaterialBufferWidth = 592 } })),
        });

        Assert.Equal(2, operations.Count);
        Assert.Equal(new[] { "aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb" }, operations[0].PixelShaderHashes);
        Assert.Equal(544, Assert.Single(operations[0].Buffers).ByteWidth);
        Assert.Equal(new[] { "cccccccccccccccc" }, operations[1].PixelShaderHashes);
        Assert.Equal(592, Assert.Single(operations[1].Buffers).ByteWidth);
        Assert.All(operations, operation => Assert.Empty(MaterialEffectBuildSupport.Errors(operation)));
    }

    // ---- what the edit keeps ------------------------------------------------------------------------------

    [Fact]
    public void Disabling_retains_authored_values_through_reopen_duplicate_and_reenable()
    {
        var session = Session();
        string edit = session.CreateEdit(AuthoredEditFixtures.Hair);
        session.ApplyMaterialShading(edit, AuthoredEditFixtures.Hair, 0,
            new[] { new AuthoredMaterialValueEdit("_MatcapIntensity", "2.5") },
            new[] { new AuthoredMaterialEffectEdit("matcap", false) }, Resolve, VolumeOperations);
        string numeric = NumericValue(session.Snapshot(), edit, "_MatcapIntensity");
        var reopened = AuthoredProjectSerializer.Deserialize(
            AuthoredProjectSerializer.Serialize(session.Snapshot()));
        var other = new AuthoredEditSession(reopened);
        other.SetRootDir(_root);
        string duplicate = other.DuplicateEdit(edit);
        Assert.True(MaterialEffectCatalog.IsDisabled(Find(other, duplicate), 0, "matcap"));
        other.ApplyMaterialShading(edit, AuthoredEditFixtures.Hair, 0,
            Array.Empty<AuthoredMaterialValueEdit>(),
            new[] { new AuthoredMaterialEffectEdit("matcap", true) }, Resolve);
        Assert.False(MaterialEffectCatalog.IsDisabled(Find(other, edit), 0, "matcap"));
        Assert.True(MaterialEffectCatalog.IsDisabled(Find(other, duplicate), 0, "matcap"));
        Assert.Equal(numeric, NumericValue(other.Snapshot(), edit, "_MatcapIntensity"));
        Assert.Equal("2.5", numeric);
    }

    [Fact]
    public void Parent_disable_preserves_child_intent_in_both_directions()
    {
        var session = Session();
        string edit = session.CreateEdit(AuthoredEditFixtures.Hair);
        void Set(string id, bool enabled) => session.ApplyMaterialShading(edit, AuthoredEditFixtures.Hair,
            0, Array.Empty<AuthoredMaterialValueEdit>(),
            new[] { new AuthoredMaterialEffectEdit(id, enabled) }, Resolve, VolumeOperations);
        Set("internal-volume", false);
        Assert.True(MaterialEffectCatalog.IsDisabled(Find(session, edit), 0, "matcap"));
        Assert.False(MaterialEffectCatalog.IsDisabled(Find(session, edit), 0, "matcap", includeParent: false));
        Set("internal-volume", true);
        Assert.False(MaterialEffectCatalog.IsDisabled(Find(session, edit), 0, "matcap"));
        Set("matcap", false);
        Set("internal-volume", false);
        Set("internal-volume", true);
        Assert.True(MaterialEffectCatalog.IsDisabled(Find(session, edit), 0, "matcap"));
        Assert.False(MaterialEffectCatalog.IsDisabled(Find(session, edit), 0, "internal-volume"));
    }

    [Fact]
    public void Shared_blend_coordinates_follow_the_material_effect_that_owns_them()
    {
        Assert.Equal("internal-volume", MaterialEffectCatalog.EffectForSemantic("_BlendTex_ST",
            new[] { "outline", "internal-volume", "matcap" }));
        Assert.Equal("fur", MaterialEffectCatalog.EffectForSemantic("_BlendTex_ST", new[] { "fur" }));
        Assert.Null(MaterialEffectCatalog.EffectForSemantic("_BlendTex_ST", new[] { "outline" }));
        var edit = new EditDefinition
        {
            DisabledMaterialEffects = new() { new DisabledMaterialEffect(0, "internal-volume") },
        };
        Assert.True(MaterialEffectCatalog.IsValueDisabled(edit, 0, "_BlendTex_ST"));
        Assert.False(MaterialEffectCatalog.IsValueDisabled(edit, 1, "_BlendTex_ST"));
        Assert.False(MaterialEffectCatalog.IsValueDisabled(edit, 0, "_RMOTex_ST"));
    }

    [Fact]
    public void A_refused_disable_leaves_no_partial_numeric_answer_or_file()
    {
        var session = Session();
        string edit = session.CreateEdit(AuthoredEditFixtures.Hair);
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());
        var refusal = Assert.Throws<AuthoredRefusalException>(() => session.ApplyMaterialShading(edit,
            AuthoredEditFixtures.Hair, 0,
            new[] { new AuthoredMaterialValueEdit("_MatcapIntensity", "2.5") },
            new[] { new AuthoredMaterialEffectEdit("detail", false) }, Resolve, VolumeOperations));
        Assert.Equal("Detail cannot be disabled on this material.", refusal.Message);
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
        Assert.False(Directory.Exists(Path.Combine(_root, "values")));
    }

    [Fact]
    public void A_missing_source_ramp_rolls_back_values_effects_and_new_slots()
    {
        var session = Session();
        string edit = session.CreateEdit(AuthoredEditFixtures.Hair);
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());
        Assert.Throws<AuthoredRefusalException>(() => session.ApplyMaterialShading(edit,
            AuthoredEditFixtures.Hair, 0,
            new[] { new AuthoredMaterialValueEdit("_MatcapIntensity", "2.5") },
            new[] { new AuthoredMaterialEffectEdit("matcap", false) }, Resolve, VolumeOperations,
            AuthoredEditFixtures.Cape));
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Numeric_snapshot_and_ramp_selection_commit_together()
    {
        var session = Session();
        string edit = session.CreateEdit(AuthoredEditFixtures.Hair);
        long revision = session.Revision;
        session.ApplyMaterialShading(edit, AuthoredEditFixtures.Hair, 0,
            new[] { new AuthoredMaterialValueEdit("_BaseColor", "0.2 0.4 0.6 1") },
            new[] { new AuthoredMaterialEffectEdit("matcap", false) }, Resolve, VolumeOperations,
            AuthoredEditFixtures.Body);
        Assert.Equal(revision + 1, session.Revision);
        var project = session.Snapshot();
        Assert.Equal("0.2 0.4 0.6 1", NumericValue(project, edit, "_BaseColor"));
        var ramp = project.TargetSlots.Single(slot => slot.Part.SameAs(AuthoredEditFixtures.Hair)
            && slot.Input == TargetInputKind.Ramp);
        var binding = Find(session, edit).Bindings.Single(value => value.SlotId == ramp.Id);
        Assert.Equal(BindingKind.SourceSlot, binding.Kind);
        Assert.True(project.TargetSlots.Single(slot => slot.Id == binding.SourceSlot!.SlotId)
            .Part.SameAs(AuthoredEditFixtures.Body));
    }

    [Fact]
    public void A_copy_records_its_source_until_nothing_copied_remains()
    {
        var session = Session();
        string edit = session.CreateEdit(AuthoredEditFixtures.Hair);
        var source = new MaterialShadingSource(AuthoredEditFixtures.Body, 1, "body_skinuber");
        session.ApplyMaterialShading(edit, AuthoredEditFixtures.Hair, 0,
            new[] { new AuthoredMaterialValueEdit("_MatcapIntensity", "2.5") },
            new[] { new AuthoredMaterialEffectEdit("matcap", false) }, Resolve, VolumeOperations,
            copiedFrom: source);
        var copy = Assert.Single(Find(session, edit).CopiedMaterialShading!);
        Assert.Equal((0, 1, "body_skinuber"), (copy.MaterialSlotIndex, copy.SourceMaterialSlotIndex, copy.SourceMaterialName));
        Assert.True(copy.SourcePart.SameAs(AuthoredEditFixtures.Body));
        Assert.Contains("copied_material_shading", AuthoredProjectSerializer.Serialize(session.Snapshot()));

        var reopened = new AuthoredEditSession(AuthoredProjectSerializer.Deserialize(
            AuthoredProjectSerializer.Serialize(session.Snapshot())));
        reopened.SetRootDir(_root);
        string duplicate = reopened.DuplicateEdit(edit);
        Assert.Single(Find(reopened, duplicate).CopiedMaterialShading!);

        // the record follows the last copy, and lasts while a copied value or off switch remains
        reopened.ApplyMaterialShading(edit, AuthoredEditFixtures.Hair, 0,
            Array.Empty<AuthoredMaterialValueEdit>(),
            new[] { new AuthoredMaterialEffectEdit("matcap", true) }, Resolve,
            copiedFrom: source with { MaterialName = "sleeve_skinuber" });
        Assert.Equal("sleeve_skinuber", Assert.Single(Find(reopened, edit).CopiedMaterialShading!).SourceMaterialName);
        reopened.ApplyMaterialShading(edit, AuthoredEditFixtures.Hair, 0,
            new[] { new AuthoredMaterialValueEdit("_MatcapIntensity", null) },
            Array.Empty<AuthoredMaterialEffectEdit>(), Resolve);
        Assert.Null(Find(reopened, edit).CopiedMaterialShading);
        Assert.Single(Find(reopened, duplicate).CopiedMaterialShading!);
        Assert.Equal(1, AuthoredProjectSerializer.Serialize(reopened.Snapshot())
            .Split("copied_material_shading").Length - 1);
    }

    [Fact]
    public void A_copy_that_carries_only_the_ramp_still_records_its_source()
    {
        var session = Session();
        string edit = session.CreateEdit(AuthoredEditFixtures.Hair);
        session.ApplyMaterialShading(edit, AuthoredEditFixtures.Hair, 0,
            Array.Empty<AuthoredMaterialValueEdit>(), Array.Empty<AuthoredMaterialEffectEdit>(), Resolve,
            sourceRampPart: AuthoredEditFixtures.Body,
            copiedFrom: new MaterialShadingSource(AuthoredEditFixtures.Body, 0, "body_skinuber"));
        Assert.Equal("body_skinuber", Assert.Single(Find(session, edit).CopiedMaterialShading!).SourceMaterialName);
    }

    [Fact]
    public void Old_projects_and_reenabled_effects_omit_the_optional_state()
    {
        var session = Session();
        string edit = session.CreateEdit(AuthoredEditFixtures.Hair);
        string original = AuthoredProjectSerializer.Serialize(session.Snapshot());
        Assert.DoesNotContain("disabled_material_effects", original);
        Assert.DoesNotContain("copied_material_shading", original);
        session.ApplyMaterialShading(edit, AuthoredEditFixtures.Hair, 0,
            Array.Empty<AuthoredMaterialValueEdit>(),
            new[] { new AuthoredMaterialEffectEdit("matcap", false) }, Resolve, VolumeOperations);
        Assert.Contains("disabled_material_effects", AuthoredProjectSerializer.Serialize(session.Snapshot()));
        session.ApplyMaterialShading(edit, AuthoredEditFixtures.Hair, 0,
            Array.Empty<AuthoredMaterialValueEdit>(),
            new[] { new AuthoredMaterialEffectEdit("matcap", true) }, Resolve);
        Assert.DoesNotContain("disabled_material_effects", AuthoredProjectSerializer.Serialize(session.Snapshot()));
        var project = session.Snapshot();
        project.EditDefinitions.Single(item => item.Id == edit).DisabledMaterialEffects = new();
        project.EditDefinitions.Single(item => item.Id == edit).CopiedMaterialShading = new();
        string serialized = AuthoredProjectSerializer.Serialize(project);
        Assert.DoesNotContain("disabled_material_effects", serialized);
        Assert.DoesNotContain("copied_material_shading", serialized);
    }

    [Theory]
    [InlineData("unknown", 0)]
    [InlineData("face-shading", 0)]
    [InlineData("matcap", -1)]
    public void Invalid_persisted_switches_fail_validation(string effectId, int index)
    {
        var project = AuthoredEditFixtures.Golden();
        project.EditDefinitions[0].DisabledMaterialEffects = new() { new(index, effectId) };
        Assert.NotEmpty(AuthoredProjectValidator.Errors(project));
    }

    [Fact]
    public void Invalid_copy_records_fail_validation()
    {
        var project = AuthoredEditFixtures.Golden();
        var valid = new CopiedMaterialShading(0, AuthoredEditFixtures.Body, 0, "body_skinuber");
        project.EditDefinitions[0].CopiedMaterialShading = new() { valid };
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        project.EditDefinitions[0].CopiedMaterialShading = new() { valid, valid with { SourceMaterialSlotIndex = 2 } };
        Assert.NotEmpty(AuthoredProjectValidator.Errors(project));
        project.EditDefinitions[0].CopiedMaterialShading = new() { valid with { MaterialSlotIndex = -1 } };
        Assert.NotEmpty(AuthoredProjectValidator.Errors(project));
    }

    private AuthoredEditSession Session()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.SetRootDir(_root);
        session.EnsurePartSlots(AuthoredEditFixtures.Hair, Resolve);
        return session;
    }

    private static EditDefinition Find(AuthoredEditSession session, string edit) =>
        session.Snapshot().EditDefinitions.Single(item => item.Id == edit);

    private static string NumericValue(AuthoredProject project, string edit, string semantic)
    {
        var slot = project.TargetSlots.Single(item => item.Part.SameAs(AuthoredEditFixtures.Hair)
            && item.Semantic == semantic);
        var binding = project.EditDefinitions.Single(item => item.Id == edit).Bindings
            .Single(item => item.SlotId == slot.Id);
        Assert.Equal(BindingKind.ProjectAsset, binding.Kind);
        return project.ProjectAssets.Single(asset => asset.Id == binding.ProjectAssetId).Value!.Value;
    }

    /// <summary>A forward pixel program of the uber shader declaring the named material fields.</summary>
    private static ShaderVariant Program(int width, params (string Semantic, int Offset)[] fields) =>
        new("gf_shader/pbr/character/uber", 0, "GFCharForward",
            new HashSet<string>(StringComparer.Ordinal), 2, width,
            fields.ToDictionary(field => field.Semantic, field => field.Offset, StringComparer.Ordinal),
            "0123456789abcdef", new Dictionary<string, int>(), ConstantBufferReads.None);

    private static ShaderVariant Reads(ShaderVariant program, params int[] offsets) => program with
    {
        MaterialReads = new ConstantBufferReads(offsets.ToHashSet(), false),
    };

    private static LegacyResolvedPart Resolve(TargetPart part) => new(part,
        Reference(part.SameAs(AuthoredEditFixtures.Body) ? 70001 : 70002, part.RendererSlot),
        Reference(part.SameAs(AuthoredEditFixtures.Body) ? 72001 : 72002, "cloth_mesh"),
        new[]
        {
            new LegacyResolvedMaterial(0, "cloth_material", Reference(84001, "cloth_material"),
                part.SameAs(AuthoredEditFixtures.Cape) ? Array.Empty<LegacyResolvedTexture>()
                    : new[] { new LegacyResolvedTexture(TargetInputKind.Ramp, "characters/vesna_ssr01",
                        "cloth_ramp", 91001, Reference(91001, "cloth_ramp")) }),
        });

    private static GameAssetRef Reference(long id, string name) => new()
    {
        GameBuild = "26109", LogicalBundle = "characters/vesna_ssr01", PathId = id, Name = name,
    };
}
