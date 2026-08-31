using System;
using System.Collections.Generic;
using System.Text;
using Remold.Core.Project;

namespace Remold.Core.Textures;

/// <summary>
/// GFL2 character-texture map suffixes: maps are named <c>&lt;stem&gt;_&lt;map&gt;</c>. Turns that
/// trailing token into a friendly label for the Edit pane.
/// </summary>
public static class TextureMap
{
    /// <summary>The trailing map token of a texture name, lowercased (<c>c_…_body1_rmo</c> → <c>rmo</c>).</summary>
    public static string Suffix(string textureName)
    {
        var i = textureName.LastIndexOf('_');
        return (i >= 0 ? textureName[(i + 1)..] : textureName).ToLowerInvariant();
    }

    /// <summary>The packed map's label. Short like every label beside it, so the channel legend carries what
    /// the acronym doesn't say.</summary>
    public const string RmoLabel = "RMO map";

    /// <summary>The base-color map's label. ONE home: the map card, the change-list chip and this vocabulary
    /// all name the slot the same way, or the two panes describe one slot under two names.</summary>
    public const string BaseColorLabel = "Base color";

    /// <summary>The tangent-normal map's label, shared for the same reason as
    /// <see cref="BaseColorLabel"/>.</summary>
    public const string NormalLabel = "Normal map";

    /// <summary>The toon ramp's label, shared for the same reason as <see cref="BaseColorLabel"/>. Read off
    /// the shader slot, never off a name suffix: a ramp's texture name follows no convention this vocabulary
    /// could recognise.</summary>
    public const string RampLabel = "Toon ramp";

    /// <summary>The effect-overlay slot's label (<c>_BlendTex</c>), shared for the same reason as
    /// <see cref="BaseColorLabel"/>. What it holds varies by part — a hair specular band, a face blush
    /// tint — so the label names the role, not one content.</summary>
    public const string BlendLabel = "Effect map";

    /// <summary>Friendly names for shader properties whose role is known. This is presentation data only:
    /// material enumeration never consults it, and a property absent from the table still receives a card
    /// through <see cref="PropertyLabel"/>.</summary>
    public static IReadOnlyDictionary<string, string> ShaderPropertyLabels { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_BaseMap"] = BaseColorLabel,
            ["_MainTex"] = BaseColorLabel,
            ["_BumpMap"] = NormalLabel,
            ["_RMOTex"] = RmoLabel,
            ["_BlendTex"] = BlendLabel,
            ["_RampMap"] = RampLabel,
            ["_GlitterMap"] = "Glitter map",
            ["_SMO"] = "SMO map",
            ["_DetailAlbedo"] = "Detail color",
            ["_DetailNormalRM"] = "Detail normal and roughness",
            ["_DetailMask"] = "Detail mask",
            ["_MatcapTex"] = "Matcap",
            ["_MatcapNormalTex"] = "Matcap normal map",
            ["_Specularmap"] = "Specular map",
            ["_MaskTex"] = "Mask",
            ["_TurbulenceTex"] = "Turbulence",
        };

    /// <summary>A material property's deterministic fallback label. The exact rule is: remove one leading
    /// underscore; remove a trailing <c>Tex</c> or <c>Map</c> only when at least one character would remain;
    /// split underscores and case boundaries (including the boundary before an acronym); then uppercase the
    /// first displayed character. Thus <c>_TurbulenceTex</c> becomes “Turbulence”,
    /// <c>_MaskTex</c> becomes “Mask”, and <c>_DetailNormalRM</c> becomes “Detail Normal RM”.</summary>
    public static string PropertyLabel(string shaderProperty)
    {
        ArgumentNullException.ThrowIfNull(shaderProperty);
        if (ShaderPropertyLabels.TryGetValue(shaderProperty, out string? curated)) return curated;
        string value = shaderProperty.Length > 0 && shaderProperty[0] == '_'
            ? shaderProperty[1..] : shaderProperty;
        if (value.Length > 3 && value.EndsWith("Tex", StringComparison.Ordinal)) value = value[..^3];
        else if (value.Length > 3 && value.EndsWith("Map", StringComparison.Ordinal)) value = value[..^3];
        if (value.Length == 0) return shaderProperty;

        var label = new StringBuilder(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current is '_' or '-' or ' ')
            {
                if (label.Length > 0 && label[^1] != ' ') label.Append(' ');
                continue;
            }
            bool boundary = index > 0 && label.Length > 0 && label[^1] != ' '
                && char.IsUpper(current)
                && (char.IsLower(value[index - 1]) || char.IsDigit(value[index - 1])
                    || (char.IsUpper(value[index - 1]) && index + 1 < value.Length
                        && char.IsLower(value[index + 1])));
            if (boundary) label.Append(' ');
            label.Append(current);
        }
        while (label.Length > 0 && label[^1] == ' ') label.Length--;
        if (label.Length == 0) return shaderProperty;
        label[0] = char.ToUpperInvariant(label[0]);
        return label.ToString();
    }

    /// <summary>The card label for one coarse semantic and exact property. A curated property wins, a
    /// generic texture derives its property label, and a property-less legacy known row keeps its shipped
    /// name.</summary>
    public static string SlotLabel(TargetInputKind input, string? shaderProperty)
    {
        if (!string.IsNullOrWhiteSpace(shaderProperty))
            return PropertyLabel(shaderProperty);
        return input switch
        {
            TargetInputKind.BaseColor => BaseColorLabel,
            TargetInputKind.Normal => NormalLabel,
            TargetInputKind.Rmo => RmoLabel,
            TargetInputKind.Blend => BlendLabel,
            TargetInputKind.Ramp => RampLabel,
            _ => input.ToString(),
        };
    }

    /// <summary>What the ramp card's ℹ says. The four bands are the one thing the picture cannot show, and
    /// the last sentence is what keeps the preview from being read as the data: a ramp is float shading
    /// values, so what the tile draws is a rendering of them and not the file.</summary>
    public const string RampInfo =
        "Four bands of four rows: the character's shading curve. "
        + "The preview is tone mapped for display. The values stay float.";

    /// <summary>A human label for a texture's map (base color, normal, …); an unknown suffix is shown
    /// uppercased verbatim rather than guessed at.</summary>
    public static string Label(string textureName) => Suffix(textureName) switch
    {
        "d" => BaseColorLabel,
        "da" => BaseColorLabel + " + alpha",
        "n" => NormalLabel,
        "rmo" => RmoLabel,
        "spc" => "Specular",
        "trans" => "Transparency",
        "emi" or "e" => "Emission",
        var other => other.ToUpperInvariant(),
    };
}
