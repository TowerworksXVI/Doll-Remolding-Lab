using System;
using System.Globalization;
using System.Linq;
using Remold.Core.Bundles;

namespace Remold.Core.Project;

/// <summary>
/// Resolves a copy-from-part binding's GAME value per the field's own truth: the GI-flatten flag through
/// the material-name family rule (the game recomputes it at load, so the serialized float is dead
/// weight), and every other authorable field from the exact source material's serialized floats/colours.
/// A source material that serializes no row for the field resolves Unresolved rather than guessing the
/// shader default — the modder picked a value the source itself does not state.
/// </summary>
public sealed class MaterialSourceValueReader : IMaterialGameValueReader
{
    private readonly Func<string, byte[]?> _deobfuscate;
    private readonly BundleReader _reader = new();
    private readonly MaterialFamilyValueReader _family = new();

    public MaterialSourceValueReader(Func<string, byte[]?> deobfuscate) =>
        _deobfuscate = deobfuscate ?? throw new ArgumentNullException(nameof(deobfuscate));

    public MaterialGameValueResolution Resolve(TargetSlot sourceSlot, TargetSlot carrierSlot,
        string semantic)
    {
        var field = MaterialValueCatalog.Field(semantic);
        if (field is null)
            return new MaterialGameValueResolution(BuildPlanVerdict.Unsupported, null,
                Array.Empty<MaterialCarrierState>(),
                $"'{semantic}' is not an authorable shading value");
        if (field.Source == MaterialValueSource.FamilyRule)
            return _family.Resolve(sourceSlot, carrierSlot, semantic);

        var material = sourceSlot.Material;
        if (material is null || string.IsNullOrWhiteSpace(material.LogicalBundle)
            || material.PathId == 0)
            return new MaterialGameValueResolution(BuildPlanVerdict.Unresolved, null,
                Array.Empty<MaterialCarrierState>(),
                "the source slot has no exact current-install material");
        BundleReader.MaterialShading? shading;
        try
        {
            byte[]? bytes = _deobfuscate(material.LogicalBundle);
            shading = bytes is null ? null : _reader.GetMaterialShading(bytes, material.PathId);
        }
        catch { shading = null; }
        if (shading is null)
            return new MaterialGameValueResolution(BuildPlanVerdict.Unresolved, null,
                Array.Empty<MaterialCarrierState>(),
                "the exact source material could not be read from the current install");

        string? encoded = null;
        if (field.Kind == MaterialValueKind.Color)
        {
            if (shading.Colors.TryGetValue(semantic, out var color))
                encoded = string.Join(" ",
                    color.Select(component => component.ToString("R", CultureInfo.InvariantCulture)));
        }
        else if (shading.Floats.TryGetValue(semantic, out float single))
            encoded = single.ToString("R", CultureInfo.InvariantCulture);
        if (encoded is null)
            return new MaterialGameValueResolution(BuildPlanVerdict.Unresolved, null,
                Array.Empty<MaterialCarrierState>(),
                $"the source material '{shading.Name}' does not state {semantic}; "
                + "the shader default applies there and cannot be copied");

        return new MaterialGameValueResolution(BuildPlanVerdict.Resolved, encoded, new[]
        {
            new MaterialCarrierState(MaterialCarrierStateKind.DynamicLive,
                "runtime-material-fields", "live source state", "live carrier state",
                "runtime-reachable material fields remain owned by the current carrier"),
        }, $"the exact source material '{shading.Name}' states the serialized value");
    }
}

/// <summary>One material's own value for one authorable field, canonicalized — the number a dialog
/// shows as "original" and a copy compares against. Read per the field's own truth: the family rule for
/// GI flatten, the serialized rows for everything else. Null where the material states none.</summary>
public static class MaterialShadingValues
{
    public static string? OriginalValue(BundleReader.MaterialShading shading, MaterialValueField field)
    {
        ArgumentNullException.ThrowIfNull(shading);
        ArgumentNullException.ThrowIfNull(field);
        if (field.Source == MaterialValueSource.FamilyRule)
        {
            string? family = MaterialFamilyClassifier.Family(shading.Name);
            return family is null ? null
                : MaterialFamilyClassifier.UsesGiFlatten(family) ? "1" : "0";
        }
        if (field.Kind == MaterialValueKind.Color)
            return shading.Colors.TryGetValue(field.Semantic, out var color)
                ? string.Join(" ",
                    color.Select(component => component.ToString("R", CultureInfo.InvariantCulture)))
                : null;
        return shading.Floats.TryGetValue(field.Semantic, out float single)
            ? single.ToString("R", CultureInfo.InvariantCulture)
            : null;
    }
}
