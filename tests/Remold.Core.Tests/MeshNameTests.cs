using Remold.Core.Model;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Mesh-name → part-token extraction (<c>Model/MeshName.Part</c>). Convention:
/// <c>c_&lt;stem&gt;_slg_&lt;part&gt;</c> with a trailing <c>_lod&lt;n&gt;</c> detail-level suffix that
/// must be stripped.
/// </summary>
public class MeshNameTests
{
    private const string VesnaPrefix = "c_VesnaSSR0101_slg_";

    [Theory]
    [InlineData("c_VesnaSSR0101_slg_P1_body1_lod0", "P1_body1")]
    [InlineData("c_VesnaSSR0101_slg_cloth1_lod3", "cloth1")]
    [InlineData("c_VesnaSSR0101_slg_body", "body")]                       // no LOD suffix at all
    [InlineData("c_VesnaSSR0101_slg_hair_lodm1", "hair")]                 // the _lodm<n> tier
    // Infixed LOD: the _Dorm/_Fight variant AFTER the marker is a distinct part, not a detail level, so
    // only the _lod<n> token is spliced out.
    [InlineData("c_VesnaSSR0101_slg_cloth1_lod0_Fight", "cloth1_Fight")]
    [InlineData("c_VesnaSSR0101_slg_P1_body1_lodm0_Dorm", "P1_body1_Dorm")]
    [InlineData("c_LireiSSR0101_slg_P3_body1_trans_lod0_Fight", "P3_body1_trans_Fight")]  // _trans before the marker
    public void Part_StripsPrefixAndLodTokenKeepingVariant(string meshName, string expected)
    {
        var prefix = meshName.StartsWith("c_Vesna", System.StringComparison.Ordinal)
            ? VesnaPrefix : "c_LireiSSR0101_slg_";
        Assert.Equal(expected, MeshName.Part(meshName, prefix));
    }

    [Fact]
    public void Part_PrefixMatchIsCaseInsensitive()
    {
        Assert.Equal("body", MeshName.Part("C_VESNASSR0101_SLG_body_lod0", VesnaPrefix));
    }

    [Fact]
    public void Part_KeepsInnerDigitsWhenNotALodMarker()
    {
        // "head3" is a part token, not a detail level — digits stay.
        Assert.Equal("P2_head3", MeshName.Part("c_VesnaSSR0101_slg_P2_head3", VesnaPrefix));
    }

    [Theory]
    [InlineData("c_VesnaSSR0101_slg_P1_body1_lod0", "lod0")]
    [InlineData("c_VesnaSSR0101_slg_cloth1_lod3", "lod3")]
    [InlineData("c_VesnaSSR0101_slg_hair_lodm0", "lodm0")]              // the mid _lodm<n> tier
    // the same mid tier on a variant garment, where the marker is INFIXED — the spelling a tail test
    // misreads as "_Dorm", and what ModBuilder's unshipped-tier skip keys on
    [InlineData("c_VesnaSSR0101_slg_P1_body1_lodm0_Dorm", "lodm0")]
    [InlineData("C_VESNASSR0101_SLG_BODY_LOD0", "lod0")]               // lower-cased
    [InlineData("c_VesnaSSR0101_slg_body_lod0_extra", "lod0")]        // first marker wins
    [InlineData("c_VesnaSSR0101_slg_body", "base")]                   // no LOD suffix → "base"
    public void Lod_ReturnsTheTierLabelOrBase(string meshName, string expected)
    {
        Assert.Equal(expected, MeshName.Lod(meshName));
    }

    [Theory]
    [InlineData("cloth1", "cloth1", null)]
    [InlineData("cloth1_Fight", "cloth1", "Fight")]
    [InlineData("P1_body_Dorm", "P1_body", "Dorm")]
    [InlineData("cloth4_trans", "cloth4_trans", null)]   // _trans is part identity, not a variant
    [InlineData("P3_body_trans_Fight", "P3_body_trans", "Fight")]
    public void SplitVariant_PeelsTrailingDormOrFight(string part, string expectBase, string? expectVariant)
    {
        var (b, v) = MeshName.SplitVariant(part);
        Assert.Equal(expectBase, b);
        Assert.Equal(expectVariant, v);
    }

    [Theory]
    [InlineData("c_VesnaSSR0101_slg_cloth1_lod1", null)]
    [InlineData("c_VesnaSSR0101_slg_cloth1_lod1_Fight", "Fight")]
    [InlineData("c_VesnaSSR0101_slg_P1_body1_lodm0_Dorm", "Dorm")]   // infixed marker, unrendered tier
    [InlineData("c_VesnaSSR0101_slg_cloth4_trans_lod0", null)]       // _trans is part identity
    [InlineData("cloth1_lod1_Dorm", "Dorm")]                         // no outfit prefix to strip
    public void Variant_ReadsTheOutfitStateOffAWholeMeshName(string meshName, string? expected)
    {
        Assert.Equal(expected, MeshName.Variant(meshName));
    }
}
