using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The theme's accent override. Fluent paints every implicit accent surface — checkbox ticks, list selection,
/// TextBox focus and caret, tab indicators, combo boxes — from <c>SystemAccentColor</c> and the six shades
/// around it, and takes all seven from the OS personalization colour unless a palette answers them. App.axaml
/// stands on ONE Accent value answering all seven; this is what makes that true.
/// </summary>
public class ThemeAccentTests
{
    /// <summary>The HudAccent copper, as App.axaml sets it.</summary>
    private static readonly Color Copper = Color.Parse("#C97B55");

    /// <summary>The keys Fluent consumes, and the only ones a leaked OS accent can arrive through.</summary>
    private static readonly string[] AccentKeys =
    {
        "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    };

    [Fact]
    public void One_accent_answers_every_key_fluent_derives_from_it()
    {
        IResourceProvider palette = new ColorPaletteResources { Accent = Copper };

        foreach (var key in AccentKeys)
        {
            Assert.True(palette.TryGetResource(key, null, out var value), key);
            Assert.IsType<Color>(value);
        }
    }

    [Fact]
    public void The_shades_are_derived_from_the_accent_not_left_at_the_platform_one()
    {
        IResourceProvider palette = new ColorPaletteResources { Accent = Copper };

        Assert.True(palette.TryGetResource("SystemAccentColor", null, out var accent));
        Assert.Equal(Copper, accent);

        // each shade is its own tint of the accent — same hue, not the same colour and not the OS one
        Assert.True(palette.TryGetResource("SystemAccentColorLight2", null, out var light2));
        Assert.True(palette.TryGetResource("SystemAccentColorDark2", null, out var dark2));
        Assert.NotEqual(accent, light2);
        Assert.NotEqual(accent, dark2);
        Assert.NotEqual(light2, dark2);
    }
}
