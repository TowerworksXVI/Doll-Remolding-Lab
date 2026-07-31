using System.Numerics;
using Remold.Core.Mesh;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Unity ⇄ glTF handedness convention. Two properties matter: every op is an INVOLUTION, which is what
/// makes an unedited round-trip recover the original data exactly; and the X reflection plus winding
/// reversal keep a triangle's geometric normal consistent with its stored one, so faces aren't inside-out.
/// </summary>
public class AxisConventionTests
{
    [Fact]
    public void Position_And_Normal_NegateX_AndSelfInvert()
    {
        var p = new Vector3(1.5f, -2.25f, 3f);
        Assert.Equal(new Vector3(-1.5f, -2.25f, 3f), AxisConvention.Position(p));
        Assert.Equal(p, AxisConvention.Position(AxisConvention.Position(p)));      // involution
        Assert.Equal(p, AxisConvention.Normal(AxisConvention.Normal(p)));
    }

    /// <summary>The W negation is half of a pair — <see cref="UvConventionTests"/> pins that it and the
    /// UV V flip must travel together.</summary>
    [Fact]
    public void Tangent_NegatesXandW_AndSelfInverts()
    {
        var t = new Vector4(0.3f, 0.4f, 0.5f, 1f);
        Assert.Equal(new Vector4(-0.3f, 0.4f, 0.5f, -1f), AxisConvention.Tangent(t));
        Assert.Equal(t, AxisConvention.Tangent(AxisConvention.Tangent(t)));        // involution
    }

    [Fact]
    public void TexCoord_FlipsV_AndSelfInverts()
    {
        var uv = new Vector2(0.25f, 0.75f);
        Assert.Equal(new Vector2(0.25f, 0.25f), AxisConvention.TexCoord(uv));
        Assert.Equal(uv, AxisConvention.TexCoord(AxisConvention.TexCoord(uv)));    // involution
    }

    [Fact]
    public void ReverseWinding_SwapsSecondThird_AndSelfInverts()
    {
        var tris = new[] { 0, 1, 2, 2, 1, 3 };
        Assert.Equal(new[] { 0, 2, 1, 2, 3, 1 }, AxisConvention.ReverseWinding(tris));
        Assert.Equal(tris, AxisConvention.ReverseWinding(AxisConvention.ReverseWinding(tris)));
        Assert.Equal(new[] { 0, 1, 2, 2, 1, 3 }, tris);   // input not mutated
    }

    [Fact]
    public void Reflection_PlusWindingReversal_KeepsFaceConsistentWithStoredNormal()
    {
        // A CCW triangle whose geometric and stored normals are both +Z. After the convention, the normal
        // recomputed from the transformed positions and reversed winding must still agree with the
        // transformed stored one, or the face renders inside-out in Blender.
        Vector3 v0 = new(0, 0, 0), v1 = new(1, 0, 0), v2 = new(0, 1, 0);
        var stored = new Vector3(0, 0, 1);
        Assert.True(Vector3.Dot(GeoNormal(v0, v1, v2), stored) > 0);   // sanity: consistent before

        var (w0, w1, w2) = (AxisConvention.Position(v0), AxisConvention.Position(v1), AxisConvention.Position(v2));
        var rewound = AxisConvention.ReverseWinding(new[] { 0, 1, 2 });
        var verts = new[] { w0, w1, w2 };
        var geo = GeoNormal(verts[rewound[0]], verts[rewound[1]], verts[rewound[2]]);

        Assert.True(Vector3.Dot(geo, AxisConvention.Normal(stored)) > 0);   // still consistent after
    }

    private static Vector3 GeoNormal(Vector3 a, Vector3 b, Vector3 c) =>
        Vector3.Cross(b - a, c - a);
}
