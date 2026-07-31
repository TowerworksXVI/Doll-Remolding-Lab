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
    [InlineData("c_x_n", "Normal")]
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
}
