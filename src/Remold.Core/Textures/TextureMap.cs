using System;

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
    public const string RmoLabel = "RMO";

    /// <summary>The base-color map's label. ONE home: the map card, the change-list chip and this vocabulary
    /// all name the slot the same way, or the two panes describe one slot under two names.</summary>
    public const string BaseColorLabel = "Base color";

    /// <summary>The tangent-normal map's label, shared for the same reason as
    /// <see cref="BaseColorLabel"/>.</summary>
    public const string NormalLabel = "Normal";

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
