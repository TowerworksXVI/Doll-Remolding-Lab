using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Remold.Core.Bundles;

namespace Remold.Core.Project;

public sealed record MaterialEffectDefinition(string Id, string Label, string? ParentId = null);

public sealed record AuthoredMaterialEffectEdit(string EffectId, bool Enabled);

/// <summary>One disable operation and the exact compiled pixel programs it is valid for. Programs that
/// share a recipe share one operation; a program absent from every operation of an effect has no proven
/// disable for it.</summary>
public sealed record MaterialEffectOperation(string EffectId, IReadOnlyList<string> PixelShaderHashes,
    IReadOnlyList<MaterialEffectBufferPatch> Buffers,
    IReadOnlyList<MaterialEffectTexture> Textures, bool SkipDraw = false);

/// <summary>Effect identities and how authored values relate to them.</summary>
public static class MaterialEffectCatalog
{
    public static IReadOnlyList<MaterialEffectDefinition> Definitions { get; } =
        Array.AsReadOnly(new[]
        {
            new MaterialEffectDefinition("stocking", "Stocking tint"),
            new MaterialEffectDefinition("body-anisotropy", "Body anisotropy"),
            new MaterialEffectDefinition("glitter", "Glitter"),
            new MaterialEffectDefinition("detail", "Detail"),
            new MaterialEffectDefinition("internal-volume", "Internal volume"),
            new MaterialEffectDefinition("matcap", "Matcap", "internal-volume"),
            new MaterialEffectDefinition("hair-highlight", "Hair highlight"),
            new MaterialEffectDefinition("fur", "Fur shells"),
            new MaterialEffectDefinition("outline", "Outline"),
            new MaterialEffectDefinition("eye-add", "Eye additive layer"),
            new MaterialEffectDefinition("eye-multiply", "Eye multiply layer"),
        });

    public static MaterialEffectDefinition? Definition(string? id) =>
        Definitions.FirstOrDefault(effect => string.Equals(effect.Id, id, StringComparison.Ordinal));

    public static string? EffectForSemantic(string semantic,
        IEnumerable<string>? presentEffects = null) => semantic switch
    {
        "_BlendTex_ST" => presentEffects?.FirstOrDefault(id => id is "internal-volume" or "fur"),
        "_AnisotropicGXX" => "body-anisotropy",
        "_Anisotropy" or "_AnisotropyShift" => "hair-highlight",
        "_UseGlitter" => "glitter",
        "_UseMatcapRef" => "matcap",
        "_BaseInsideLerp" or "_FakeIntensity" => "internal-volume",
        _ when semantic.StartsWith("_Stocking", StringComparison.Ordinal) => "stocking",
        _ when semantic.StartsWith("_Glitter", StringComparison.Ordinal) => "glitter",
        _ when semantic.StartsWith("_Detail", StringComparison.Ordinal) => "detail",
        _ when semantic.StartsWith("_Matcap", StringComparison.Ordinal) => "matcap",
        _ when semantic.StartsWith("_Inside", StringComparison.Ordinal)
            || semantic.StartsWith("_Reflection", StringComparison.Ordinal) => "internal-volume",
        _ when semantic.StartsWith("_Outline", StringComparison.Ordinal) => "outline",
        _ => null,
    };

    public static bool IsValueDisabled(EditDefinition edit, int materialSlotIndex, string semantic)
    {
        var disabled = edit.DisabledMaterialEffects?.Where(effect =>
            effect.MaterialSlotIndex == materialSlotIndex).Select(effect => effect.EffectId);
        return EffectForSemantic(semantic, disabled) is { } effectId
            && IsDisabled(edit, materialSlotIndex, effectId);
    }

    public static bool IsDisabled(EditDefinition edit, int materialSlotIndex, string effectId,
        bool includeParent = true)
    {
        if (edit.DisabledMaterialEffects?.Any(effect => effect.MaterialSlotIndex == materialSlotIndex
                && string.Equals(effect.EffectId, effectId, StringComparison.Ordinal)) == true)
            return true;
        return includeParent && Definition(effectId)?.ParentId is { } parent
            && IsDisabled(edit, materialSlotIndex, parent);
    }
}

/// <summary>An effect one compiled program carries, with the input that makes it an identity there.</summary>
public sealed record ProgramEffect(string EffectId, IReadOnlyList<MaterialEffectBufferPatch> Buffers,
    IReadOnlyList<MaterialEffectTexture> Textures, bool SkipDraw = false);

/// <summary>
/// Which effects a compiled character-shader program carries and the exact input that disables each,
/// decided from the program itself: its instruction stream's reads of the material buffer, its declared
/// layout and texture slots, its keywords, pass, and shader. Each rule reproduces the retained
/// catalog-26932 investigation across its 2,868 programs; the identities are the disable table's. A
/// program whose layout cannot express a proven identity carries no such effect here, so no control is
/// offered without its operation.
/// </summary>
public static class MaterialEffectRules
{
    /// <summary>The shared conditional hair pass. It reads the highlight inputs on every uber material,
    /// but the game draws it only on the fringe route, so it establishes no capability by itself.</summary>
    public const string AuxiliaryHairPass = "GFCharHairTransE";

    public static bool IsAuxiliaryHairPass(ShaderVariant variant) =>
        string.Equals(variant.PassName, AuxiliaryHairPass, StringComparison.Ordinal);

    private static readonly string[] DetailStrengths =
        { "_DetailAlbedoIntensity", "_DetailNormalIntensity", "_DetailRMIntensity" };
    private static readonly string[] DetailInputs =
    {
        "_DetailAlbedoIntensity", "_DetailNormalIntensity", "_DetailRMIntensity",
        "_DetailAlphaMode", "_DetailAlphaIntensity",
    };
    private static readonly string[] VolumeInputs =
    {
        "_InsideColorContrast", "_BaseInsideLerp", "_FakeIntensity", "_ReflectionIntensity",
        "_UseMatcapRef",
    };

    /// <summary>The effects of one compiled program. <paramref name="records"/> are every serialized
    /// variant row that ships this same bytecode: layout and reads come from the bytecode, so any row
    /// serves for them, while a keyword, pass, or shader predicate holds when any row states it.</summary>
    public static IReadOnlyList<ProgramEffect> Evaluate(IReadOnlyList<ShaderVariant> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return Array.Empty<ProgramEffect>();
        var variant = records[0];
        var reads = variant.MaterialReads ?? ConstantBufferReads.None;
        bool numeric = variant.MaterialBufferSlot == 2 && variant.MaterialBufferWidth is 544 or 592;
        bool Declared(string semantic) => variant.VectorOffsets.ContainsKey(semantic);
        bool Reads(string semantic) => numeric
            && variant.VectorOffsets.TryGetValue(semantic, out int offset) && reads.Reads(offset);
        int? Texture(string name) => variant.TextureSlots is { } slots
            && slots.TryGetValue(name, out int slot) ? slot : null;
        var effects = new List<ProgramEffect>();
        void Numeric(string id, IReadOnlyList<(string Semantic, float[] Values)> writes,
            IReadOnlyList<MaterialEffectTexture>? textures = null)
        {
            var patch = new List<MaterialPatchWrite>();
            foreach (var (semantic, values) in writes)
            {
                int offset = variant.VectorOffsets[semantic];
                for (int component = 0; component < values.Length; component++)
                    patch.Add(new MaterialPatchWrite(semantic, offset + 4 * component, values[component]));
            }
            effects.Add(new ProgramEffect(id, new[]
            {
                new MaterialEffectBufferPatch(2, variant.MaterialBufferWidth,
                    patch.OrderBy(write => write.ByteOffset).ToArray()),
            }, textures ?? Array.Empty<MaterialEffectTexture>()));
        }

        if (Reads("_StockingCenterColor") && Declared("_StockingFalloffColor")
            && Declared("_StockingFalloffPower"))
            Numeric("stocking", new[]
            {
                ("_StockingCenterColor", new[] { 1f, 1f, 1f }),
                ("_StockingFalloffColor", new[] { 1f, 1f, 1f }),
                ("_StockingFalloffPower", new[] { 1f }),
            });
        if (Reads("_AnisotropicGXX"))
            Numeric("body-anisotropy", new[] { ("_AnisotropicGXX", new[] { 0f }) });
        if (Reads("_UseGlitter")) Numeric("glitter", new[] { ("_UseGlitter", new[] { 0f }) });
        if (DetailStrengths.Any(Reads))
        {
            // A program that reads the coverage inputs also needs a neutral mask; without a mask slot the
            // scalar identity alone is unproven, so the effect is not offered there.
            bool coverage = Reads("_DetailAlphaMode") || Reads("_DetailAlphaIntensity");
            int? mask = coverage ? Texture("_DetailMask") : null;
            if (!coverage || mask is not null)
                Numeric("detail", DetailInputs.Where(Reads).Select(semantic => (semantic, new[] { 0f })).ToList(),
                    mask is { } slot ? new[] { new MaterialEffectTexture(slot, 1, 0, 0, 1) } : null);
        }
        if (Reads("_BaseInsideLerp") && VolumeInputs.All(Declared))
        {
            Numeric("internal-volume", VolumeInputs.Select(semantic => (semantic, new[] { 0f })).ToList());
            Numeric("matcap", new[] { ("_UseMatcapRef", new[] { 0f }) });
        }
        if (Reads("_Anisotropy")) Numeric("hair-highlight", new[] { ("_Anisotropy", new[] { 0f }) });
        if (records.Any(record => record.Keywords.Contains("_USE_FUR_SHELL"))
            && Texture("_BlendTex") is { } blend)
            effects.Add(new ProgramEffect("fur", Array.Empty<MaterialEffectBufferPatch>(),
                new[] { new MaterialEffectTexture(blend, 0, 0, 0, 0) }));
        if (records.Any(record => record.PassName.Contains("Outline", StringComparison.Ordinal)))
            effects.Add(Skip("outline"));
        if (records.Any(record => record.ShaderName.EndsWith("eyeblend_add", StringComparison.Ordinal)))
            effects.Add(Skip("eye-add"));
        if (records.Any(record => record.ShaderName.EndsWith("eyeblend_multiply", StringComparison.Ordinal)))
            effects.Add(Skip("eye-multiply"));
        return effects.AsReadOnly();

        static ProgramEffect Skip(string id) => new(id, Array.Empty<MaterialEffectBufferPatch>(),
            Array.Empty<MaterialEffectTexture>(), true);
    }

    /// <summary>Programs sharing one recipe for one effect fold into one operation, so a build emits each
    /// distinct recipe once and gates it on every program it serves.</summary>
    public static IReadOnlyList<MaterialEffectOperation> Operations(
        IEnumerable<(string PixelShaderHash, IReadOnlyList<ProgramEffect> Effects)> programs)
    {
        ArgumentNullException.ThrowIfNull(programs);
        var groups = new Dictionary<string, (ProgramEffect Effect, SortedSet<string> Hashes)>(
            StringComparer.Ordinal);
        foreach (var (hash, effects) in programs)
        foreach (var effect in effects)
        {
            string key = effect.EffectId + "\n" + Recipe(effect);
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = (effect, new SortedSet<string>(StringComparer.Ordinal));
            group.Hashes.Add(hash.ToLowerInvariant());
        }
        return groups.Values.OrderBy(group => group.Effect.EffectId, StringComparer.Ordinal)
            .ThenBy(group => group.Hashes.First(), StringComparer.Ordinal)
            .Select(group => new MaterialEffectOperation(group.Effect.EffectId, group.Hashes.ToArray(),
                group.Effect.Buffers, group.Effect.Textures, group.Effect.SkipDraw))
            .ToArray();
    }

    private static string Recipe(ProgramEffect effect)
    {
        var buffers = effect.Buffers.OrderBy(buffer => buffer.ConstantBufferSlot).Select(buffer =>
            buffer.ConstantBufferSlot + ":" + buffer.ByteWidth + ":" + string.Join(",",
                buffer.Writes.OrderBy(write => write.ByteOffset).Select(write =>
                    write.ByteOffset + "=" + write.Value.ToString("R", CultureInfo.InvariantCulture))));
        var textures = effect.Textures.OrderBy(texture => texture.Slot).Select(texture =>
            $"{texture.Slot}:{texture.R},{texture.G},{texture.B},{texture.A}");
        return string.Join(";", buffers) + "|" + string.Join(";", textures) + "|" + effect.SkipDraw;
    }
}
