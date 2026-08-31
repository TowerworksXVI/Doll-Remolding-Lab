using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Remold.Core.Project;

public static class MaterialValueSemantics
{
    public const string UseGiFlatten = "_UseGIFlatten";
}

public enum MaterialValueClass
{
    SupportedPatch,
    DynamicLive,
    Unsupported,
}

public sealed record MaterialValuePolicy(string Semantic, MaterialValueClass Class, string Reason);

/// <summary>The stored shape of one authorable field: one float, or a four-float row (a colour, or a
/// tiling/offset vector — both are one vec4 in the buffer).</summary>
public enum MaterialValueKind
{
    Float,
    Color,
}

/// <summary>Where a field's GAME value comes from when a binding names a source material rather than a
/// typed-in number. Serialized floats/colours are read off the exact source material object; the
/// GI-flatten flag is recomputed by the game from the material-name family at load, so its serialized
/// float is dead weight and the family rule is the truth.</summary>
public enum MaterialValueSource
{
    SerializedValue,
    FamilyRule,
}

/// <summary>One authorable UnityPerMaterial field: its semantic, the plain-language label the UI shows,
/// its stored shape, where its game value comes from, a rank ordering the offer list, the value range
/// observed across the shipped material corpus (a hint, not a clamp), and its byte offset in each
/// patchable layout — null where a layout does not carry the field. Fragment-stage fields only: the
/// patch rebinds the PIXEL shader's constant buffer, and a vertex-stage field would read unpatched.</summary>
public sealed record MaterialValueField(
    string Semantic,
    string Label,
    MaterialValueKind Kind,
    MaterialValueSource Source,
    char Rank,
    float ObservedMin,
    float ObservedMax,
    int? Offset544,
    int? Offset592)
{
    public int Floats => Kind == MaterialValueKind.Color ? 4 : 1;

    public int? OffsetIn(int byteWidth) => byteWidth switch
    {
        544 => Offset544,
        592 => Offset592,
        _ => null,
    };
}

/// <summary>One observed UnityPerMaterial buffer shape and its patch allowlist. An active shader
/// contract must still prove each field; width never grants capability by itself.</summary>
public sealed record MaterialConstantLayout(
    string Id,
    int? PatchConstantBufferSlot,
    int ByteWidth,
    IReadOnlyDictionary<string, int> FloatOffsets);

/// <summary>The semantic/shape allowlist for material-value authoring. Runtime-reachable fields
/// remain carrier-owned, and an active render contract must prove every patched field.</summary>
public static class MaterialValueCatalog
{
    public const string UnityPerMaterial48 = "unity-per-material-v1-48";
    public const string UnityPerMaterial96 = "unity-per-material-v1-96";
    public const string UnityPerMaterial144 = "unity-per-material-v1-144";
    public const string UnityPerMaterial544 = "unity-per-material-v1-544";
    public const string UnityPerMaterial592 = "unity-per-material-v1-592";

    /// <summary>The authorable field table, measured over the shipped material corpus: every
    /// fragment-stage UnityPerMaterial field of the 544/592 uber-family layouts that only the material
    /// supplies (runtime-reachable fields stay live in <see cref="DynamicFields"/>; vertex-stage fields
    /// are unreachable by a pixel-shader constant patch and are deliberately absent). Offsets are stable
    /// per layout across every declaring shader variant — measured, zero conflicts.</summary>
    public static readonly IReadOnlyList<MaterialValueField> Fields = new MaterialValueField[]
    {
        new("_StockingCenterColor", "Stocking centre colour",
            MaterialValueKind.Color, MaterialValueSource.SerializedValue, 'A', 0.0f, 1.0f, 80, 80),
        new("_StockingFalloffColor", "Stocking edge colour",
            MaterialValueKind.Color, MaterialValueSource.SerializedValue, 'A', 0.0f, 1.0f, 96, 96),
        new("_UseGIFlatten", "Skin lighting",
            MaterialValueKind.Float, MaterialValueSource.FamilyRule, 'A', 0.0f, 1.0f, 492, 492),
        new("_AnisotropicGXX", "Stretched highlight (also changes RMO alpha)",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', -0.943f, 1.0f, 496, 496),
        new("_Anisotropy", "Stretched highlight strength",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', 0.0f, 5.0f, 264, null),
        new("_AnisotropyShift", "Stretched highlight position",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', -0.461f, 0.1f, 268, null),
        new("_DetailAlbedoIntensity", "Detail colour strength",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', 0.0f, 1.0f, null, 568),
        new("_DetailNormalIntensity", "Detail normal strength",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', 0.0f, 1.449f, null, 572),
        new("_GlitterDensity", "Glitter density",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', 1.0f, 200.0f, 136, 136),
        new("_GlitterRimIntensity", "Glitter rim brightness",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', 0.0f, 10.0f, 144, 144),
        new("_GlitterSpecIntensity", "Glitter shine brightness",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', 0.0f, 10.0f, 148, 148),
        new("_InsideBaseColor", "Inner-side colour",
            MaterialValueKind.Color, MaterialValueSource.SerializedValue, 'B', 0.0f, 2.996078f, 400, null),
        new("_ReflectionFresnelF0", "Base reflectivity",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', -1.0f, 1.0f, 444, null),
        new("_ReflectionIntensity", "Reflection strength",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', 0.0f, 5.0f, 440, null),
        new("_StockingFalloffPower", "Stocking edge hardness",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', 0.1f, 5.0f, 112, 112),
        new("_UseGlitter", "Glitter on/off",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'B', 0.0f, 1.0f, 116, 116),
        new("_AdditionalLightShadow", "Secondary-light shadows",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.0f, 4, 4),
        new("_BaseInsideLerp", "Outer/inner colour blend",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', -1.0f, 0.708f, 432, null),
        new("_BlendSmoothness", "Blend-map smoothness",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 0.637f, 272, null),
        new("_BlendTex_ST", "Blend-map tiling and offset",
            MaterialValueKind.Color, MaterialValueSource.SerializedValue, 'C', -0.36f, 35.5f, 64, null),
        new("_DetailAlbedo_ST", "Detail-map tiling and offset",
            MaterialValueKind.Color, MaterialValueSource.SerializedValue, 'C', 0.0f, 1200.0f, null, 544),
        new("_DetailAlphaIntensity", "Detail alpha strength",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.0f, null, 564),
        new("_DetailAlphaMode", "Detail alpha mode",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.0f, null, 560),
        new("_DetailRMIntensity", "Detail roughness strength",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.0f, null, 576),
        new("_FakeIntensity", "Fake-light strength",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.828f, 436, null),
        new("_GlitterCurvature", "Glitter curvature response",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.0f, 160, 160),
        new("_GlitterMetallic", "Glitter metallic",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', -10.0f, 10.0f, 156, 156),
        new("_GlitterRimFalloff", "Glitter rim falloff",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.1f, 10.0f, 120, 120),
        new("_GlitterRimMax", "Glitter rim range max",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.5001f, 2.0f, 128, 128),
        new("_GlitterRimMin", "Glitter rim range min",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 0.5f, 124, 124),
        new("_GlitterRoughness", "Glitter roughness",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', -10.0f, 10.0f, 152, 152),
        new("_GlitterSpeed", "Glitter sparkle speed",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 6.53f, 140, 140),
        new("_GlitterViewWeight", "Glitter angle response",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.0f, 132, 132),
        new("_InsideColorBias", "Inner-side colour bias",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', -0.471f, 1.0f, 428, null),
        new("_InsideColorContrast", "Inner-side colour contrast",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.4f, 8.09f, 416, null),
        new("_InsideHeightBias", "Inner-side height bias",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', -1.0f, 0.509f, 424, null),
        new("_InsideHeightContrast", "Inner-side height contrast",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.89f, 420, null),
        new("_MatcapIntensity", "Matcap strength",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 1.0f, 10.0f, 452, null),
        new("_MatcapRimPower", "Matcap rim power",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.532f, 1.0f, 456, null),
        new("_PreMulAlpha", "Premultiplied alpha on/off",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.0f, 0, 0),
        new("_RMOTex_ST", "RMO tiling and offset",
            MaterialValueKind.Color, MaterialValueSource.SerializedValue, 'C', 0.0f, 700.0f, 32, 32),
        new("_UseMatcapRef", "Matcap reflection on/off",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.0f, 448, null),
        new("_UseSpecularUV2", "Specular from UV2 on/off",
            MaterialValueKind.Float, MaterialValueSource.SerializedValue, 'C', 0.0f, 1.0f, 488, null),
    };

    private static readonly IReadOnlyDictionary<string, MaterialValueField> FieldBySemantic =
        Fields.ToDictionary(field => field.Semantic, StringComparer.Ordinal);

    public static MaterialValueField? Field(string semantic) =>
        FieldBySemantic.TryGetValue(semantic, out var field) ? field : null;

    private static readonly IReadOnlyDictionary<string, int> NoSupportedValues
        = ReadOnly(new Dictionary<string, int>(StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, int> OffsetsFor(int byteWidth) =>
        ReadOnly(Fields.Where(field => field.OffsetIn(byteWidth) is not null)
            .ToDictionary(field => field.Semantic, field => field.OffsetIn(byteWidth)!.Value,
                StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<string, MaterialConstantLayout> LayoutById
        = new[]
        {
            Layout(UnityPerMaterial48, null, 48, NoSupportedValues),
            Layout(UnityPerMaterial96, null, 96, NoSupportedValues),
            Layout(UnityPerMaterial144, null, 144, NoSupportedValues),
            Layout(UnityPerMaterial544, 2, 544, OffsetsFor(544)),
            Layout(UnityPerMaterial592, 2, 592, OffsetsFor(592)),
        }.ToDictionary(layout => layout.Id, StringComparer.Ordinal);

    /// <summary>The shader keywords MATERIALS enable, measured as the union of every enabled-keyword
    /// list across the verified character-shader material corpus. Everything outside this set that a
    /// shader variant was compiled for is runtime-driven (shadow quality, fog, LOD, render features) —
    /// the axis evidence derivation must stay agnostic to, because it varies per machine and per
    /// scene.</summary>
    public static readonly IReadOnlySet<string> MaterialDrivenKeywords = new HashSet<string>(
        StringComparer.Ordinal)
    {
        "_RAMPMAP", "_RAMPMAP_INDI", "_ANISOTROPIC_SPECULAR", "_BLEND_UV2", "_USE_STOCKING",
        "_GI_FLATTEN", "_ALPHATEST_ON", "_DOUBLE_SIDED", "_SHADOW_ADDITIONAL_BIAS", "_USE_BLEND_TEX",
        "_PRE_MUL_ALPHA", "_USE_VOLUMETRIC", "_PLANAR_NONE", "_NORMALMAP", "_ALPHABLEND_ON",
        "_USE_FUR_SHELL", "_USEGLITTER_ON", "_USEMATCAPREF_ON", "_DETAIL_MAP", "_USEBLENDTEX_ON",
        "_ANISOTROPICSPECULAR_ON", "_ANISOTROPIC_GGX", "_EMISSION",
    };

    // These fields are reachable from runtime game code. Conservatively leaving all of them live
    // prevents a static material snapshot from freezing gameplay or effect state.
    private static readonly HashSet<string> DynamicFields = new(StringComparer.Ordinal)
    {
        "_FinalTint", "_BaseMap_ST", "_BaseColor", "_AoeSelectColor", "_BumpMap_ST",
        "_EmissiveIntensity", "_EnableHolographicScanline", "_HolographicIntensity",
        "_HolographicColor", "_HolographicWidth", "_ConcealLerp", "_ShadowBiasDistance",
        "_DissolveIntensity", "_DissolveTex_ST", "_TutorialColor", "_Tutorial", "_OnHitColor",
        "_AdjustShadowBias", "_Moisture", "_ColorOffset", "_PaintInfluence", "_WetFlowSpeed",
        "_WetFlowSize", "_WetFlowStrength", "_WetTraceStrength", "_Cutoff",
        "_FaceLightDirAdjustment", "_SpecularIntensity", "_ShadowIntensity", "_MainColor",
        "_CampColorIndex",
    };

    public static IReadOnlyList<MaterialConstantLayout> Layouts => LayoutById.Values
        .OrderBy(layout => layout.ByteWidth).ToArray();

    public static bool TryLayout(string? id, out MaterialConstantLayout layout)
    {
        layout = null!;
        return !string.IsNullOrWhiteSpace(id) && LayoutById.TryGetValue(id, out layout!);
    }

    public static MaterialValuePolicy Classify(string semantic,
        IReadOnlyList<RenderContract>? activeContracts = null)
    {
        if (FieldBySemantic.ContainsKey(semantic))
        {
            if (activeContracts is not { Count: > 0 })
                return new MaterialValuePolicy(semantic, MaterialValueClass.Unsupported,
                    "no active shader contract proves this semantic field");
            foreach (var contract in activeContracts)
                if (!TryPatchField(contract, semantic, out _, out _, out string reason))
                    return new MaterialValuePolicy(semantic, MaterialValueClass.Unsupported, reason);
            return new MaterialValuePolicy(semantic, MaterialValueClass.SupportedPatch,
                "every active shader contract proves the semantic patch field");
        }
        if (DynamicFields.Contains(semantic))
            return new MaterialValuePolicy(semantic, MaterialValueClass.DynamicLive,
                "the field is runtime-reachable and conservatively remains live");
        return new MaterialValuePolicy(semantic, MaterialValueClass.Unsupported,
            "this shading value cannot be changed");
    }

    public static bool TryPatchField(RenderContract contract, string semantic,
        out MaterialConstantLayout layout, out BuildMaterialValueField field, out string reason)
    {
        ArgumentNullException.ThrowIfNull(contract);
        layout = null!;
        field = null!;
        if (contract.PixelShaderHashes is not { Count: > 0 } shaderHashes
            || shaderHashes.Any(hash => hash is not { Length: 16 }
                || hash.Any(character => !Uri.IsHexDigit(character))))
        {
            reason = $"active shader contract '{contract.Id}' has no exact pixel-shader hashes";
            return false;
        }
        if (!TryLayout(contract.MaterialLayout, out layout))
        {
            reason = $"material layout '{contract.MaterialLayout}' is not supported";
            return false;
        }
        if (!layout.FloatOffsets.TryGetValue(semantic, out int expectedOffset)
            || layout.PatchConstantBufferSlot is not int expectedSlot)
        {
            reason = $"{semantic} is not declared by material layout '{layout.Id}'";
            return false;
        }
        int floats = Field(semantic)?.Floats ?? 1;
        if (expectedOffset + sizeof(float) * floats > layout.ByteWidth)
        {
            reason = $"{semantic} does not fit material layout '{layout.Id}'";
            return false;
        }
        var declarations = contract.MaterialValueFields?.Where(candidate =>
            string.Equals(candidate.Semantic, semantic, StringComparison.Ordinal)).ToList()
            ?? new List<BuildMaterialValueField>();
        if (declarations.Count != 1)
        {
            reason = $"active shader contract '{contract.Id}' does not prove {semantic}";
            return false;
        }
        field = declarations[0];
        if (field.ConstantBufferSlot != expectedSlot || field.ByteOffset != expectedOffset
            || string.IsNullOrWhiteSpace(field.Proof))
        {
            reason = $"active shader contract '{contract.Id}' disagrees with the {semantic} layout";
            return false;
        }
        reason = "the active shader contract proves the semantic field";
        return true;
    }

    private static MaterialConstantLayout Layout(string id, int? patchSlot, int width,
        IReadOnlyDictionary<string, int> offsets) => new(id, patchSlot, width, offsets);

    private static IReadOnlyDictionary<string, int> ReadOnly(Dictionary<string, int> values) =>
        new ReadOnlyDictionary<string, int>(values);
}

public enum MaterialDifferenceKind
{
    InputBinding,
    SemanticValue,
    Shader,
    Keyword,
    Pass,
    RenderState,
}

/// <summary>One measured difference between a proposed source and the current material carrier.</summary>
public sealed record MaterialDifferenceCandidate(
    string Id,
    string Label,
    MaterialDifferenceKind Kind,
    string TargetValue,
    string SourceValue,
    string? Semantic = null,
    Binding? ProposedBinding = null);

public static class MaterialSourceDifferenceResolver
{
    public static MaterialSourceProposal Propose(string editDefinitionId, string sourceLabel,
        IEnumerable<MaterialDifferenceCandidate> candidates,
        IReadOnlyList<RenderContract>? activeContracts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editDefinitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);
        ArgumentNullException.ThrowIfNull(candidates);
        var differences = new List<MaterialSourceDifference>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Id))
                throw new ArgumentException("material difference id is required", nameof(candidates));
            if (!ids.Add(candidate.Id))
                throw new ArgumentException($"duplicate material difference id '{candidate.Id}'",
                    nameof(candidates));
            if (string.IsNullOrWhiteSpace(candidate.Label))
                throw new ArgumentException($"material difference '{candidate.Id}' has no label",
                    nameof(candidates));
            if (!Enum.IsDefined(candidate.Kind))
                throw new ArgumentException($"material difference '{candidate.Id}' has an unknown kind",
                    nameof(candidates));
            if (string.Equals(candidate.TargetValue, candidate.SourceValue, StringComparison.Ordinal))
                continue;

            if (candidate.Kind == MaterialDifferenceKind.InputBinding)
            {
                if (candidate.ProposedBinding is null
                    || !string.Equals(candidate.ProposedBinding.SlotId, candidate.Id,
                        StringComparison.Ordinal))
                    throw new ArgumentException($"material input '{candidate.Id}' has no matching binding",
                        nameof(candidates));
                differences.Add(new MaterialSourceDifference(candidate.Id, candidate.Label,
                    MaterialDifferenceDisposition.Binding,
                    Difference(candidate) + "; this input can be bound independently",
                    candidate.ProposedBinding));
                continue;
            }

            if (candidate.Kind == MaterialDifferenceKind.SemanticValue)
            {
                if (string.IsNullOrWhiteSpace(candidate.Semantic))
                    throw new ArgumentException($"material value '{candidate.Id}' has no semantic",
                        nameof(candidates));
                var policy = MaterialValueCatalog.Classify(candidate.Semantic, activeContracts);
                if (policy.Class == MaterialValueClass.SupportedPatch)
                {
                    if (candidate.ProposedBinding is null
                        || !string.Equals(candidate.ProposedBinding.SlotId, candidate.Id,
                            StringComparison.Ordinal))
                        throw new ArgumentException(
                            $"supported material value '{candidate.Id}' has no matching binding",
                            nameof(candidates));
                    differences.Add(new MaterialSourceDifference(candidate.Id, candidate.Label,
                        MaterialDifferenceDisposition.Binding,
                        Difference(candidate) + "; Build proved this semantic on every active shader",
                        candidate.ProposedBinding));
                }
                else if (policy.Class == MaterialValueClass.DynamicLive)
                    differences.Add(new MaterialSourceDifference(candidate.Id, candidate.Label,
                        MaterialDifferenceDisposition.DynamicLive,
                        Difference(candidate) + "; " + policy.Reason));
                else
                    differences.Add(new MaterialSourceDifference(candidate.Id, candidate.Label,
                        MaterialDifferenceDisposition.Unsupported,
                        Difference(candidate) + "; " + policy.Reason));
                continue;
            }

            differences.Add(new MaterialSourceDifference(candidate.Id, candidate.Label,
                MaterialDifferenceDisposition.Unsupported,
                Difference(candidate) + "; shader, keyword, pass and render state remain carrier-owned"));
        }
        return new MaterialSourceProposal(editDefinitionId, sourceLabel, differences);
    }

    private static string Difference(MaterialDifferenceCandidate candidate) =>
        $"source is '{candidate.SourceValue}', carrier is '{candidate.TargetValue}'";
}

public enum MaterialPatchBase
{
    Unknown,
    LiveCarrierSnapshot,
}

public enum MaterialCarrierStateKind
{
    DynamicLive,
    Unsupported,
}

public sealed record MaterialCarrierState(
    MaterialCarrierStateKind Kind,
    string Name,
    string SourceValue,
    string CarrierValue,
    string Reason);

public sealed record MaterialPatchWrite(string Semantic, int ByteOffset, float Value);

/// <summary>A derived per-draw patch. It starts from the live carrier buffer and overwrites only the
/// listed semantic components, so every unowned byte retains its current runtime value.</summary>
public sealed record MaterialConstantBufferPatch(
    string Layout,
    int ConstantBufferSlot,
    int ByteWidth,
    MaterialPatchBase Base,
    IReadOnlyList<MaterialPatchWrite> Writes,
    IReadOnlyList<MaterialCarrierState> CarrierOwnedState);

public sealed record MaterialGameValueResolution(
    BuildPlanVerdict Verdict,
    string? Value,
    IReadOnlyList<MaterialCarrierState> CarrierOwnedState,
    string Reason);

public interface IMaterialGameValueReader
{
    MaterialGameValueResolution Resolve(TargetSlot sourceSlot, TargetSlot carrierSlot,
        string semantic);
}

public static class MaterialFamilyClassifier
{
    public static string? Family(string? materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName)) return null;
        string family = materialName.Trim();
        int underscore = family.LastIndexOf('_');
        if (underscore >= 0) family = family[(underscore + 1)..];
        int suffix = family.IndexOfAny(new[] { ' ', '(' });
        if (suffix >= 0) family = family[..suffix];
        return string.IsNullOrWhiteSpace(family) ? null : family;
    }

    public static bool UsesGiFlatten(string family) => family.Equals("skinuber",
        StringComparison.OrdinalIgnoreCase) || family.Equals("faceuber",
        StringComparison.OrdinalIgnoreCase) || family.Equals("eyelashuber",
        StringComparison.OrdinalIgnoreCase);
}

/// <summary>Resolves family-derived values only after exact material objects have been re-anchored.
/// The material name is then data consumed by the game's own family classifier, not object identity.</summary>
public sealed class MaterialFamilyValueReader : IMaterialGameValueReader
{
    public MaterialGameValueResolution Resolve(TargetSlot sourceSlot, TargetSlot carrierSlot,
        string semantic)
    {
        if (!string.Equals(semantic, MaterialValueSemantics.UseGiFlatten, StringComparison.Ordinal))
            return new MaterialGameValueResolution(BuildPlanVerdict.Unsupported, null,
                Array.Empty<MaterialCarrierState>(),
                $"material family resolution does not support '{semantic}'");
        string? sourceFamily = MaterialFamilyClassifier.Family(sourceSlot.Material?.Name);
        string? carrierFamily = MaterialFamilyClassifier.Family(carrierSlot.Material?.Name);
        if (sourceFamily is null || carrierFamily is null)
            return new MaterialGameValueResolution(BuildPlanVerdict.Unresolved, null,
                Array.Empty<MaterialCarrierState>(),
                "an exact current-install material has no family name");

        bool sourceFlatten = MaterialFamilyClassifier.UsesGiFlatten(sourceFamily);
        bool carrierFlatten = MaterialFamilyClassifier.UsesGiFlatten(carrierFamily);
        var residue = new List<MaterialCarrierState>
        {
            new(MaterialCarrierStateKind.DynamicLive, "runtime-material-fields",
                "live source state", "live carrier state",
                "runtime-reachable material fields remain owned by the current carrier"),
        };
        if (!string.Equals(sourceFamily, carrierFamily, StringComparison.OrdinalIgnoreCase))
            residue.Add(new MaterialCarrierState(MaterialCarrierStateKind.Unsupported,
                "material-family", sourceFamily, carrierFamily,
                "the semantic patch does not replace shader, pass, queue, stencil or cull state"));
        if (sourceFlatten != carrierFlatten)
            residue.Add(new MaterialCarrierState(MaterialCarrierStateKind.Unsupported,
                "_GI_FLATTEN", sourceFlatten ? "enabled" : "disabled",
                carrierFlatten ? "enabled" : "disabled",
                "the semantic patch does not change shader keywords"));

        return new MaterialGameValueResolution(BuildPlanVerdict.Resolved,
            sourceFlatten ? "1" : "0", residue,
            $"the exact source material resolves family '{sourceFamily}'");
    }
}

public static class MaterialFamilyDifferences
{
    public static IReadOnlyList<MaterialDifferenceCandidate> Compare(TargetSlot targetSlot,
        TargetSlot sourceSlot)
    {
        ArgumentNullException.ThrowIfNull(targetSlot);
        ArgumentNullException.ThrowIfNull(sourceSlot);
        if (targetSlot.Input != TargetInputKind.MaterialValue
            || sourceSlot.Input != TargetInputKind.MaterialValue
            || !string.Equals(targetSlot.Semantic, MaterialValueSemantics.UseGiFlatten,
                StringComparison.Ordinal)
            || !string.Equals(sourceSlot.Semantic, targetSlot.Semantic, StringComparison.Ordinal))
            throw new ArgumentException("family comparison requires matching GI-flatten material slots");
        string? targetFamily = MaterialFamilyClassifier.Family(targetSlot.Material?.Name);
        string? sourceFamily = MaterialFamilyClassifier.Family(sourceSlot.Material?.Name);
        if (targetFamily is null || sourceFamily is null)
            throw new InvalidOperationException("an exact material has no family name");
        string targetValue = MaterialFamilyClassifier.UsesGiFlatten(targetFamily) ? "1" : "0";
        string sourceValue = MaterialFamilyClassifier.UsesGiFlatten(sourceFamily) ? "1" : "0";
        return new[]
        {
            new MaterialDifferenceCandidate(targetSlot.Id, "GI flatten",
                MaterialDifferenceKind.SemanticValue, targetValue, sourceValue,
                MaterialValueSemantics.UseGiFlatten, new Binding
                {
                    SlotId = targetSlot.Id,
                    Kind = BindingKind.SourceSlot,
                    SourceSlot = new BindingSourceSlot { SlotId = sourceSlot.Id },
                }),
            new MaterialDifferenceCandidate(targetSlot.Id + ":family", "Material family",
                MaterialDifferenceKind.Shader, targetFamily, sourceFamily),
            new MaterialDifferenceCandidate(targetSlot.Id + ":keyword", "GI flatten keyword",
                MaterialDifferenceKind.Keyword,
                targetValue == "1" ? "enabled" : "disabled",
                sourceValue == "1" ? "enabled" : "disabled"),
        };
    }
}

/// <summary>Turns one resolved material-value binding into per-contract patch emissions. The broader
/// backend supplies the current render plan and exact source-game value.</summary>
public static class MaterialValueBuildSupport
{
    public const string OutputPurpose = "semantic material-value patch shader";

    public static BuildOperationResolution Resolve(BuildBindingRequest request,
        BuildRenderPlan renderPlan, IMaterialGameValueReader? gameValues = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(renderPlan);
        if (request.CurrentSlot.Input != TargetInputKind.MaterialValue
            || string.IsNullOrWhiteSpace(request.CurrentSlot.Semantic))
            return Guard("material-value support received a non-material target slot");
        string semantic = request.CurrentSlot.Semantic;
        if (renderPlan.Contracts is not { Count: > 0 })
            return Guard("the material-value patch has no render contracts");
        var policy = MaterialValueCatalog.Classify(semantic, renderPlan.Contracts);
        if (policy.Class == MaterialValueClass.DynamicLive)
            return Block(BuildPlanVerdict.Unsupported,
                "this value changes while the game runs, so it cannot be set to a fixed one");
        if (policy.Class == MaterialValueClass.Unsupported)
            return CannotSet(policy.Reason);

        string? encoded;
        IReadOnlyList<MaterialCarrierState> carrierOwned;
        string sourceReason;
        if (request.EffectiveValue.ProjectAsset is { } asset)
        {
            if (asset.Kind != ProjectAssetKind.StructuredValue || asset.Value is null)
                return Guard("the material-value project asset has no structured value");
            if (!string.Equals(asset.Value.Semantic, semantic, StringComparison.Ordinal))
                return Guard("the material-value project asset carries another semantic");
            encoded = asset.Value.Value;
            carrierOwned = ProjectValueResidue(request.CurrentSlot);
            sourceReason = "the project owns the semantic value";
        }
        else if (request.EffectiveValue.SourceGameSlot is { } source)
        {
            if (gameValues is null)
                return Guard("the exact source material value was not resolved",
                    BuildPlanVerdict.Unresolved);
            var resolved = gameValues.Resolve(source, request.CurrentSlot, semantic);
            if (resolved is null || string.IsNullOrWhiteSpace(resolved.Reason))
                return Guard("the material-value reader returned no complete result");
            if (resolved.Verdict != BuildPlanVerdict.Resolved)
            {
                var verdict = resolved.Verdict is BuildPlanVerdict.Unsupported
                    or BuildPlanVerdict.Unresolved or BuildPlanVerdict.NeedsRepair
                    or BuildPlanVerdict.Conflict
                    ? resolved.Verdict : BuildPlanVerdict.Conflict;
                return Block(verdict, resolved.Reason);
            }
            encoded = resolved.Value;
            carrierOwned = resolved.CarrierOwnedState ?? Array.Empty<MaterialCarrierState>();
            sourceReason = resolved.Reason;
        }
        else return Guard("the material-value binding resolved to an unsupported value source");

        if (!TryValues(semantic, encoded, out float[] values, out string canonical))
            return Block(BuildPlanVerdict.Unsupported,
                $"'{MaterialValueCatalog.Field(semantic)?.Label ?? semantic}' cannot be set to "
                + $"{encoded}");

        var patches = new List<(RenderContract Contract, MaterialConstantBufferPatch Patch)>();
        foreach (var contract in renderPlan.Contracts)
        {
            if (!MaterialValueCatalog.TryPatchField(contract, semantic, out var layout,
                    out var field, out string reason))
                return CannotSet(reason);
            patches.Add((contract, new MaterialConstantBufferPatch(layout.Id,
                field.ConstantBufferSlot, layout.ByteWidth, MaterialPatchBase.LiveCarrierSnapshot,
                values.Select((component, i) =>
                    new MaterialPatchWrite(semantic, field.ByteOffset + sizeof(float) * i, component))
                    .ToArray(), carrierOwned)));
        }

        string key = ArtifactKey(request.RowId + "|" + semantic + "|" + canonical);
        var emissions = new List<BuildRuntimeEmission>();
        var outputs = new List<BuildOutputArtifact>();
        for (int i = 0; i < patches.Count; i++)
        {
            var (contract, patch) = patches[i];
            string id = request.RowId + ":material-value:" + i.ToString(CultureInfo.InvariantCulture);
            var emission = new BuildRuntimeEmission(id, BuildEmissionKind.MaterialValuePatch,
                contract.TargetingProof, request.Gate,
                new[] { contract.Id },
                $"patches only {semantic}; {carrierOwned.Count} material differences remain carrier-owned",
                patch);
            emissions.Add(emission);
            outputs.Add(new BuildOutputArtifact(id + ":shader", OutputPurpose,
                $"material-patch:{semantic}:{canonical}:{patch.Layout}:live-carrier",
                $"generated/material_patch_{key}_{i.ToString(CultureInfo.InvariantCulture)}.hlsl",
                true, new[] { id }, sourceReason));
        }

        var action = request.EffectiveValue.Kind switch
        {
            EffectiveValueKind.ProjectAsset => BuildRuntimeAction.BindProjectAsset,
            EffectiveValueKind.SourceGameSlot => BuildRuntimeAction.BindGameSource,
            _ => BuildRuntimeAction.None,
        };
        if (action == BuildRuntimeAction.None)
            return Guard("the material-value source does not map to a runtime action");
        return new BuildOperationResolution(new BuildPlanDecision(BuildPlanVerdict.Resolved, action,
            emissions[0].TargetingProof,
            $"{semantic} resolves to {canonical}; other material state remains carrier-owned"),
            renderPlan, emissions, outputs);

        BuildOperationResolution Block(BuildPlanVerdict verdict, string reason, string? detail = null) =>
            new(BuildPlanDecision.Blocked(verdict, reason, detail), renderPlan,
                Array.Empty<BuildRuntimeEmission>(), Array.Empty<BuildOutputArtifact>());

        // What this seam says when the disagreement is its own: the caller handed it something the
        // contract forbids, or a reader answered incompletely. Nothing in it is the modder's, so the
        // verdict stands as it was and only the wording moves to the log.
        BuildOperationResolution Guard(string detail,
            BuildPlanVerdict verdict = BuildPlanVerdict.Conflict) =>
            Block(verdict, AuthoredBuildPlanner.InternalGuard, detail);

        // Every way a shader can fail to take this value is one thing to the modder: this material's
        // shader will not carry it here. Which contract, which layout and which declaration disagreed is
        // the catalog's account of the install, and it goes to the log under the same line.
        BuildOperationResolution CannotSet(string detail) => Block(BuildPlanVerdict.Unsupported,
            $"'{MaterialValueCatalog.Field(semantic)?.Label ?? semantic}' cannot be set on this "
            + "material's shader", detail);
    }

    /// <summary>Parse one authored value string for its field: one finite float, or four space- or
    /// comma-separated ones for a colour/vector row. A family-rule field (GI flatten) takes exactly 0
    /// or 1 — the game's own classifier only ever produces those two states, and an in-between float
    /// would ship a value no shipped material carries. <paramref name="canonical"/> is the round-trip
    /// re-encoding an asset records, so equal values compare equal as strings.</summary>
    public static bool TryValues(string semantic, string? encoded, out float[] values,
        out string canonical)
    {
        values = Array.Empty<float>();
        canonical = "";
        var field = MaterialValueCatalog.Field(semantic);
        if (field is null || string.IsNullOrWhiteSpace(encoded)) return false;
        var parts = encoded.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != field.Floats) return false;
        var parsed = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out parsed[i]) || !float.IsFinite(parsed[i]))
                return false;
            if (field.Source == MaterialValueSource.FamilyRule && parsed[i] is not (0f or 1f))
                return false;
        }
        values = parsed;
        canonical = string.Join(" ",
            parsed.Select(component => component.ToString("R", CultureInfo.InvariantCulture)));
        return true;
    }

    private static IReadOnlyList<MaterialCarrierState> ProjectValueResidue(TargetSlot carrierSlot)
    {
        string family = MaterialFamilyClassifier.Family(carrierSlot.Material?.Name)
            ?? "unresolved current carrier family";
        string keyword = family == "unresolved current carrier family"
            ? "unresolved current carrier keyword"
            : MaterialFamilyClassifier.UsesGiFlatten(family) ? "enabled" : "disabled";
        return new[]
        {
            new MaterialCarrierState(MaterialCarrierStateKind.DynamicLive,
                "runtime-material-fields", "not authored", "live carrier state",
                "runtime-reachable material fields remain owned by the current carrier"),
            new MaterialCarrierState(MaterialCarrierStateKind.Unsupported,
                "material-family", "not authored", family,
                "the semantic patch does not replace shader, pass, queue, stencil or cull state"),
            new MaterialCarrierState(MaterialCarrierStateKind.Unsupported,
                "_GI_FLATTEN", "not authored", keyword,
                "the semantic patch does not change shader keywords"),
        };
    }

    private static string ArtifactKey(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}

public sealed record GeneratedMaterialPatchFile(
    string OutputId,
    string File,
    string FunctionalIdentity,
    string Text);

/// <summary>Materializes the shader artifacts named by resolved Build-plan patch emissions. INI binding
/// remains part of the Build-emitter cutover; this output is the semantic patch program it consumes.</summary>
public static class MaterialValuePatchEmitter
{
    public static IReadOnlyList<GeneratedMaterialPatchFile> Emit(AuthoredBuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.CanBuild)
            throw new InvalidOperationException("cannot emit material patches from a blocking Build plan");
        var files = new List<GeneratedMaterialPatchFile>();
        foreach (var planned in plan.RuntimeEmissions.Where(emission =>
            emission.Verdict == BuildPlanVerdict.Resolved
            && emission.Emission.Kind == BuildEmissionKind.MaterialValuePatch))
        {
            var patch = planned.Emission.MaterialPatch
                ?? throw new InvalidOperationException(
                    $"material emission '{planned.Emission.Id}' has no patch payload");
            var outputs = plan.OutputArtifacts.Where(output => output.Artifact.Included
                && output.Artifact.EmissionIds.Contains(planned.Emission.Id, StringComparer.Ordinal)
                && string.Equals(output.Artifact.Purpose, MaterialValueBuildSupport.OutputPurpose,
                    StringComparison.Ordinal)).ToList();
            if (outputs.Count != 1 || outputs[0].Artifact.File is null)
                throw new InvalidOperationException(
                    $"material emission '{planned.Emission.Id}' has {outputs.Count} patch artifacts");
            files.Add(new GeneratedMaterialPatchFile(outputs[0].Artifact.Id,
                outputs[0].Artifact.File!,
                outputs[0].Artifact.FunctionalIdentity, EmitShader(patch)));
        }
        return files;
    }

    public static string EmitShader(MaterialConstantBufferPatch patch)
    {
        var errors = MaterialValuePatchValidator.Errors(patch);
        if (errors.Count > 0)
            throw new ArgumentException("invalid material patch: " + string.Join("; ", errors),
                nameof(patch));
        var text = new StringBuilder();
        text.Append("RWByteAddressBuffer material_state : register(u0);\n\n")
            .Append("[numthreads(1, 1, 1)]\n")
            .Append("void main(uint3 id : SV_DispatchThreadID)\n{\n")
            .Append("    if (id.x != 0) return;\n")
            .Append("    uint material_bytes;\n")
            .Append("    material_state.GetDimensions(material_bytes);\n")
            .Append("    if (material_bytes != ")
            .Append(patch.ByteWidth.ToString(CultureInfo.InvariantCulture)).Append("u) return;\n");
        foreach (var write in patch.Writes.OrderBy(write => write.ByteOffset))
        {
            uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(write.Value));
            text.Append("    material_state.Store(")
                .Append(write.ByteOffset.ToString(CultureInfo.InvariantCulture)).Append(", 0x")
                .Append(bits.ToString("x8", CultureInfo.InvariantCulture)).Append("u);\n");
        }
        text.Append("}\n");
        return text.ToString();
    }
}

public static class MaterialConstantBufferPatcher
{
    public static byte[] Apply(MaterialConstantBufferPatch patch, ReadOnlySpan<byte> liveCarrier)
    {
        var errors = MaterialValuePatchValidator.Errors(patch);
        if (errors.Count > 0)
            throw new ArgumentException("invalid material patch: " + string.Join("; ", errors),
                nameof(patch));
        if (liveCarrier.Length != patch.ByteWidth)
            throw new ArgumentException(
                $"live carrier has {liveCarrier.Length} bytes, expected {patch.ByteWidth}",
                nameof(liveCarrier));
        byte[] result = liveCarrier.ToArray();
        foreach (var write in patch.Writes)
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(write.ByteOffset, sizeof(float)),
                BitConverter.SingleToInt32Bits(write.Value));
        return result;
    }
}

internal static class MaterialValuePatchValidator
{
    internal static IReadOnlyList<string> Errors(MaterialConstantBufferPatch patch)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(patch.Layout)) errors.Add("patch has no material layout");
        if (patch.ConstantBufferSlot < 0) errors.Add("patch has an invalid constant-buffer slot");
        if (patch.ByteWidth <= 0 || patch.ByteWidth % 16 != 0)
            errors.Add("patch has an invalid constant-buffer width");
        if (patch.Base != MaterialPatchBase.LiveCarrierSnapshot)
            errors.Add("patch does not preserve a live carrier snapshot");
        MaterialConstantLayout? layout = null;
        if (!MaterialValueCatalog.TryLayout(patch.Layout, out layout))
            errors.Add($"patch names unknown material layout '{patch.Layout}'");
        else
        {
            if (layout.PatchConstantBufferSlot != patch.ConstantBufferSlot)
                errors.Add("patch constant-buffer slot disagrees with its layout");
            if (layout.ByteWidth != patch.ByteWidth)
                errors.Add("patch byte width disagrees with its layout");
        }
        if (patch.Writes is not { Count: > 0 }) errors.Add("patch has no semantic writes");
        else
        {
            var offsets = new HashSet<int>();
            foreach (var write in patch.Writes)
            {
                if (string.IsNullOrWhiteSpace(write.Semantic)) errors.Add("patch write has no semantic");
                if (write.ByteOffset < 0 || write.ByteOffset % sizeof(float) != 0
                    || write.ByteOffset + sizeof(float) > patch.ByteWidth)
                    errors.Add($"patch write '{write.Semantic}' has an invalid byte offset");
                else if (!offsets.Add(write.ByteOffset))
                    errors.Add($"patch writes byte offset {write.ByteOffset} more than once");
                if (!float.IsFinite(write.Value))
                    errors.Add($"patch write '{write.Semantic}' has a non-finite value");
            }
            // A multi-component field writes one float per component, contiguously from the layout's
            // declared offset — exactly all of them, so a partial colour can never ship.
            foreach (var group in patch.Writes.Where(write =>
                    !string.IsNullOrWhiteSpace(write.Semantic))
                .GroupBy(write => write.Semantic, StringComparer.Ordinal))
            {
                int floats = MaterialValueCatalog.Field(group.Key)?.Floats ?? 1;
                var wrote = group.Select(write => write.ByteOffset).OrderBy(offset => offset).ToList();
                if (layout is null) continue;
                if (!layout.FloatOffsets.TryGetValue(group.Key, out int expected))
                {
                    errors.Add($"patch write '{group.Key}' disagrees with its layout");
                    continue;
                }
                var want = Enumerable.Range(0, floats).Select(i => expected + sizeof(float) * i);
                if (!wrote.SequenceEqual(want))
                    errors.Add($"patch write '{group.Key}' disagrees with its layout");
            }
        }
        if (patch.CarrierOwnedState is not { Count: > 0 })
            errors.Add("patch has no carrier-owned state account");
        else foreach (var state in patch.CarrierOwnedState)
        {
            if (!Enum.IsDefined(state.Kind)) errors.Add("carrier-owned state has an unknown kind");
            if (string.IsNullOrWhiteSpace(state.Name)) errors.Add("carrier-owned state has no name");
            if (string.IsNullOrWhiteSpace(state.Reason)) errors.Add("carrier-owned state has no reason");
        }
        return errors;
    }
}
