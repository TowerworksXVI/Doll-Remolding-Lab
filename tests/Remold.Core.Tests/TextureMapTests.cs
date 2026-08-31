using System.Linq;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Project;
using Remold.Core.Textures;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="TextureMap"/> turns a texture name's trailing map token into a friendly label. The
/// convention is <c>&lt;stem&gt;_{d,da,n,rmo,spc,trans,…}</c>.
/// </summary>
public class TextureMapTests
{
    [Theory]
    [InlineData("c_VesnaSSR0101_slg_P1_body1_d", "d")]
    [InlineData("c_VesnaSSR0101_slg_P1_body1_rmo", "rmo")]
    [InlineData("c_VesnaSSR0101_slg_P1_body1_DA", "da")]   // lowercased
    public void Suffix_IsTheTrailingToken(string name, string expected)
    {
        Assert.Equal(expected, TextureMap.Suffix(name));
    }

    [Theory]
    [InlineData("c_x_d", "Base color")]
    [InlineData("c_x_da", "Base color + alpha")]
    [InlineData("c_x_n", "Normal map")]
    [InlineData("c_x_rmo", TextureMap.RmoLabel)]
    [InlineData("c_x_spc", "Specular")]
    [InlineData("c_x_trans", "Transparency")]
    public void Label_NamesTheKnownMaps(string name, string expected)
    {
        Assert.Equal(expected, TextureMap.Label(name));
    }

    [Fact]
    public void Label_ShowsAnUnknownSuffixVerbatim_Uppercased()
    {
        // an unfamiliar map isn't guessed at — it's surfaced so the modder can see what it is
        Assert.Equal("XYZ", TextureMap.Label("c_x_xyz"));
    }

    [Theory]
    [InlineData("_BaseMap", "Base color")]
    [InlineData("_MainTex", "Base color")]
    [InlineData("_BumpMap", "Normal map")]
    [InlineData("_RMOTex", "RMO map")]
    [InlineData("_BlendTex", "Effect map")]
    [InlineData("_RampMap", "Toon ramp")]
    [InlineData("_GlitterMap", "Glitter map")]
    [InlineData("_SMO", "SMO map")]
    [InlineData("_DetailAlbedo", "Detail color")]
    [InlineData("_DetailNormalRM", "Detail normal and roughness")]
    [InlineData("_DetailMask", "Detail mask")]
    [InlineData("_MatcapTex", "Matcap")]
    [InlineData("_MatcapNormalTex", "Matcap normal map")]
    [InlineData("_Specularmap", "Specular map")]
    [InlineData("_MaskTex", "Mask")]
    [InlineData("_TurbulenceTex", "Turbulence")]
    public void PropertyLabel_UsesTheCuratedPresentationTable(string property, string expected)
    {
        Assert.Equal(expected, TextureMap.PropertyLabel(property));
    }

    [Theory]
    [InlineData("_DissolveNoiseTex", "Dissolve Noise")]
    [InlineData("_VertexAnimMaskMap", "Vertex Anim Mask")]
    [InlineData("_custom_detailMap", "Custom detail")]
    [InlineData("_Tex", "Tex")]
    [InlineData("_Map", "Map")]
    [InlineData("_", "_")]
    public void PropertyLabel_DerivesUnknownPropertiesByThePinnedRule(string property, string expected)
    {
        Assert.Equal(expected, TextureMap.PropertyLabel(property));
    }

    [Fact]
    public void SlotLabel_KeepsTheFiveShippedNamesWithoutAProperty()
    {
        Assert.Equal(new[] { "Base color", "Normal map", "RMO map", "Effect map", "Toon ramp" },
            new[]
            {
                TargetInputKind.BaseColor, TargetInputKind.Normal, TargetInputKind.Rmo,
                TargetInputKind.Blend, TargetInputKind.Ramp,
            }.Select(input => TextureMap.SlotLabel(input, null)));
    }

    [Theory]
    [InlineData("SMO map", "SMO map")]
    [InlineData("RMO map", "RMO map")]
    [InlineData("Base color", "base color")]
    [InlineData("123 Map", "123 map")]
    public void LabelInSentence_preserves_an_all_caps_first_word(string label, string expected)
    {
        Assert.Equal(expected, EditMapCardVm.LabelInSentence(label));
    }
}
