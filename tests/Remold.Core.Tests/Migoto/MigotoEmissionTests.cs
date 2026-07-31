using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>Guards on the fixed emission contract: the flat-DDS header and the compute-template
/// stamping. These bytes are consumed by 3DMigoto and must not drift.</summary>
public class MigotoEmissionTests
{
    [Fact]
    public void FlatDds_NeutralNormal_HasExpectedHeaderAndPixels()
    {
        var dds = FlatDds.Build((128, 128, 255, 255), size: 8, srgb: false);
        Assert.Equal(404, dds.Length);                         // 148-byte DX10 header + 8*8*4 px
        Assert.Equal((byte)'D', dds[0]);
        Assert.Equal((byte)'D', dds[0x54]);                    // "DX10" fourcc starts at 0x54
        Assert.Equal((byte)'X', dds[0x55]);
        Assert.Equal(28u, System.BitConverter.ToUInt32(dds, 0x80));   // R8G8B8A8_UNORM (srgb=false)
        Assert.Equal(3u, System.BitConverter.ToUInt32(dds, 0x84));    // TEXTURE2D
        // first pixel
        Assert.Equal(128, dds[148]); Assert.Equal(128, dds[149]);
        Assert.Equal(255, dds[150]); Assert.Equal(255, dds[151]);
    }

    [Fact]
    public void FlatDds_Srgb_SetsSrgbFormatTag()
    {
        var dds = FlatDds.Build((255, 0, 200, 255), srgb: true);
        Assert.Equal(29u, System.BitConverter.ToUInt32(dds, 0x80));   // R8G8B8A8_UNORM_SRGB
    }

    [Fact]
    public void ComputeTemplates_StampCountsAndEmitLf()
    {
        var recover = ComputeTemplates.EmitRecover(356);
        Assert.Contains("static const uint ROWS=356;", recover);
        Assert.Contains("q[Sel[sbase+t]].position", recover);   // the slim indirection, not a full-mesh scan
        // widths are RAGGED: the shader reads (base,width) per bone instead of a stamped K
        Assert.Contains("Buffer<uint>          Off    : register(t4);", recover);
        Assert.Contains("uint sbase=Off[localBone<<1], width=Off[(localBone<<1)|1];", recover);
        Assert.Contains("uint cbase=(sbase<<2)+comp*width;", recover);
        Assert.Contains("for(uint t=0;t<width;t++)", recover);
        Assert.DoesNotContain("K=", recover);
        Assert.DoesNotContain("\r", recover);                 // LF only
        Assert.EndsWith("}\n", recover);

        var skin = ComputeTemplates.EmitSkin(116);
        Assert.Contains("static const uint VCOUNT=116;", skin);

        var convert = ComputeTemplates.EmitConvert(partCount: 3, unionBones: 50);
        Assert.Contains("cbuffer PartCB0 : register(b5) { float4 W0[4]; }", convert);
        Assert.Contains("cbuffer PartCB2 : register(b7) { float4 W2[4]; }", convert);
        Assert.Contains("static const uint ROWS=200;", convert);   // 4 * 50
        Assert.Contains("    if(pi==0) WP=float4x4(W0[0],W0[1],W0[2],W0[3]);", convert);
        Assert.Contains("    else if(pi==2) WP=float4x4(W2[0],W2[1],W2[2],W2[3]);", convert);
    }
}
