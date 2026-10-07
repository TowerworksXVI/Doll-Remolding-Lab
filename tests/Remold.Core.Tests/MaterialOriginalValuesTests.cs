using System;
using System.Collections.Generic;
using System.IO;
using Remold.Core.Bundles;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

public sealed class MaterialOriginalValuesTests
{
    [Fact]
    public void Shader_property_defaults_use_the_exact_object_and_property_shape()
    {
        using var temp = new TempGame();
        string path = temp.At("shader.bundle");
        SyntheticBundle.BuildOneShader(path, 91, "CAB-fixture", Properties());
        var reader = new BundleReader();
        byte[] bytes = File.ReadAllBytes(path);

        var defaults = reader.GetShaderNumericDefaults(bytes, 91);

        Assert.NotNull(defaults);
        Assert.Equal(8f, defaults.Floats["_GlitterDensity"]);
        Assert.Equal(2f, defaults.Floats["_StockingFalloffPower"]);
        Assert.Equal(new[] { 0.25f, 0.5f, 0.75f, 1f }, defaults.Colors["_StockingCenterColor"]);
        Assert.Equal(new[] { 2f, 3f, 4f, 5f }, defaults.Colors["_Direction"]);
        Assert.DoesNotContain("_DetailAlbedo", defaults.Floats.Keys);
        Assert.DoesNotContain("_DetailAlbedo", defaults.Colors.Keys);
        Assert.Null(reader.GetShaderNumericDefaults(bytes, 92));
    }

    [Fact]
    public void Snapshot_uses_saved_values_texture_transforms_and_exact_external_shader_defaults()
    {
        using var temp = new TempGame();
        var bundles = Bundles(temp, "CAB-fixture");
        var original = new DerivedMaterialEvidence(bundle => bundles.GetValueOrDefault(bundle))
            .ResolveOriginals(Material());

        Assert.NotNull(original);
        Assert.Equal("12.5", original.Value(Field("_GlitterDensity")));
        Assert.Equal("2", original.Value(Field("_StockingFalloffPower")));
        Assert.Equal("0.25 0.5 0.75 1", original.Value(Field("_StockingCenterColor")));
        Assert.Equal("2 3 0.1 0.2", original.Value(Field("_DetailAlbedo_ST")));
        Assert.Equal("1", original.Value(Field(MaterialValueSemantics.UseGiFlatten)));
        Assert.Null(original.Value(Field("_GlitterRimIntensity")));
    }

    [Fact]
    public void An_external_cab_mismatch_keeps_saved_values_but_never_borrows_unrelated_defaults()
    {
        using var temp = new TempGame();
        var bundles = Bundles(temp, "CAB-unrelated");
        var original = new DerivedMaterialEvidence(bundle => bundles.GetValueOrDefault(bundle))
            .ResolveOriginals(Material());

        Assert.NotNull(original);
        Assert.Null(original.Defaults);
        Assert.Equal("12.5", original.Value(Field("_GlitterDensity")));
        Assert.Equal("2 3 0.1 0.2", original.Value(Field("_DetailAlbedo_ST")));
        Assert.Null(original.Value(Field("_StockingFalloffPower")));
    }

    [Fact]
    public void Local_shader_reference_reads_the_same_bundle_without_an_external_lookup()
    {
        using var temp = new TempGame();
        string path = temp.At("local.bundle");
        SyntheticBundle.BuildOneMaterial(path, "material.bundle", "coat_skinuber", 41,
            Array.Empty<(string, int, long)>(), Array.Empty<string>(),
            shading: new SyntheticBundle.MaterialShadingSpec(0, 91, Array.Empty<string>(),
                new Dictionary<string, float>(), new Dictionary<string, float[]>()),
            localShaderProperties: Properties());
        byte[] bytes = File.ReadAllBytes(path);
        int reads = 0;
        var original = new DerivedMaterialEvidence(bundle =>
        {
            Assert.Equal("material.bundle", bundle);
            reads++;
            return bytes;
        }).ResolveOriginals(Material());

        Assert.NotNull(original);
        Assert.Equal("8", original.Value(Field("_GlitterDensity")));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Existing_source_links_do_not_acquire_shader_default_fallback()
    {
        using var temp = new TempGame();
        var bundles = Bundles(temp, "CAB-fixture");
        var reader = new MaterialSourceValueReader(bundle => bundles.GetValueOrDefault(bundle));
        var source = new TargetSlot { Material = Material() };

        var value = reader.Resolve(source, source, "_StockingFalloffPower");
        var transform = reader.Resolve(source, source, "_DetailAlbedo_ST");

        Assert.Equal(BuildPlanVerdict.Unresolved, value.Verdict);
        Assert.Equal(BuildPlanVerdict.Unresolved, transform.Verdict);
    }

    [Fact]
    public void An_incomplete_transform_does_not_break_existing_numeric_source_links()
    {
        using var temp = new TempGame();
        string path = temp.At("material.bundle");
        SyntheticBundle.BuildOneMaterial(path, "material.bundle", "coat_skinuber", 41,
            new[] { ("_DetailAlbedo", 0, 0L) }, Array.Empty<string>(),
            shading: new SyntheticBundle.MaterialShadingSpec(0, 0, Array.Empty<string>(),
                new Dictionary<string, float> { ["_GlitterDensity"] = 12.5f },
                new Dictionary<string, float[]>()), incompleteTextureTransform: true);
        byte[] bytes = File.ReadAllBytes(path);
        var source = new TargetSlot { Material = Material() };

        var legacy = new MaterialSourceValueReader(_ => bytes).Resolve(source, source, "_GlitterDensity");

        Assert.Equal(BuildPlanVerdict.Resolved, legacy.Verdict);
        Assert.Equal("12.5", legacy.Value);
        Assert.Null(new DerivedMaterialEvidence(_ => bytes).ResolveOriginals(Material()));
    }

    [Fact]
    public void Cancellation_while_reading_defaults_is_not_reported_as_an_unknown_value()
    {
        using var temp = new TempGame();
        var bundles = Bundles(temp, "CAB-fixture");
        var evidence = new DerivedMaterialEvidence(bundle => bundle == "material.bundle"
            ? bundles[bundle] : throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => evidence.ResolveOriginals(Material()));
    }

    [Fact]
    public void The_shaders_defaults_are_read_once_for_every_material_drawing_through_it()
    {
        using var temp = new TempGame();
        var bundles = Bundles(temp, "CAB-fixture");
        string second = temp.At("second.bundle");
        SyntheticBundle.BuildOneMaterial(second, "second.bundle", "sleeve_skinuber", 42,
            new[] { ("_DetailAlbedo", 0, 0L) }, new[] { "CAB-fixture" },
            shading: new SyntheticBundle.MaterialShadingSpec(1, 91, Array.Empty<string>(),
                new Dictionary<string, float> { ["_GlitterDensity"] = 3f },
                new Dictionary<string, float[]>()));
        bundles["second.bundle"] = File.ReadAllBytes(second);
        var reader = new BundleReader();
        int defaultsReads = 0;
        var evidence = new DerivedMaterialEvidence(bundle => bundles.GetValueOrDefault(bundle),
            reader.GetMaterialShading, reader.GetShaderVariants, reader.GetBundleCab,
            (bytes, pathId) =>
            {
                defaultsReads++;
                return reader.GetShaderNumericDefaults(bytes, pathId);
            });

        var first = evidence.ResolveOriginals(Material());
        var other = evidence.ResolveOriginals(new GameAssetRef
        {
            GameBuild = "fixture", LogicalBundle = "second.bundle", PathId = 42, Name = "sleeve_skinuber",
        });

        Assert.NotNull(first);
        Assert.NotNull(other);
        Assert.Equal("12.5", first.Value(Field("_GlitterDensity")));
        Assert.Equal("3", other.Value(Field("_GlitterDensity")));
        Assert.Equal("2", other.Value(Field("_StockingFalloffPower")));
        Assert.True(defaultsReads <= 1, $"defaults were read {defaultsReads} times");
    }

    private static Dictionary<string, byte[]> Bundles(TempGame temp, string shaderCab)
    {
        string materialPath = temp.At("material.bundle");
        string shaderPath = temp.At("shader.bundle");
        SyntheticBundle.BuildOneMaterial(materialPath, "material.bundle", "coat_skinuber", 41,
            new[] { ("_DetailAlbedo", 0, 0L) }, new[] { "CAB-fixture" },
            shading: new SyntheticBundle.MaterialShadingSpec(1, 91, Array.Empty<string>(),
                new Dictionary<string, float>
                {
                    ["_GlitterDensity"] = 12.5f,
                    [MaterialValueSemantics.UseGiFlatten] = 0f,
                }, new Dictionary<string, float[]>(), new Dictionary<string, float[]>
                {
                    ["_DetailAlbedo_ST"] = new[] { 2f, 3f, 0.1f, 0.2f },
                }));
        SyntheticBundle.BuildOneShader(shaderPath, 91, shaderCab, Properties());
        return new Dictionary<string, byte[]>
        {
            ["material.bundle"] = File.ReadAllBytes(materialPath),
            [DerivedMaterialEvidence.CharacterShaderBundle] = File.ReadAllBytes(shaderPath),
        };
    }

    private static SyntheticBundle.ShaderPropertySpec[] Properties() => new[]
    {
        new SyntheticBundle.ShaderPropertySpec("_GlitterDensity", 2, new[] { 8f, 0f, 0f, 0f }),
        new SyntheticBundle.ShaderPropertySpec("_StockingFalloffPower", 3, new[] { 2f, 0f, 5f, 0f }),
        new SyntheticBundle.ShaderPropertySpec("_StockingCenterColor", 0, new[] { 0.25f, 0.5f, 0.75f, 1f }),
        new SyntheticBundle.ShaderPropertySpec("_Direction", 1, new[] { 2f, 3f, 4f, 5f }),
        new SyntheticBundle.ShaderPropertySpec("_DetailAlbedo", 4, new[] { 0f, 0f, 0f, 0f }),
        new SyntheticBundle.ShaderPropertySpec(MaterialValueSemantics.UseGiFlatten, 2, new[] { 0f, 0f, 0f, 0f }),
    };

    private static MaterialValueField Field(string semantic) => MaterialValueCatalog.Field(semantic)
        ?? throw new InvalidOperationException("Unknown fixture field: " + semantic);

    private static GameAssetRef Material() => new()
    {
        GameBuild = "fixture", LogicalBundle = "material.bundle", PathId = 41, Name = "coat_skinuber",
    };
}
