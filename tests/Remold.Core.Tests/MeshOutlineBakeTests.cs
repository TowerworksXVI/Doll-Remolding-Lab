using System.Collections.Generic;
using System.Numerics;
using Remold.Core.Mesh;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The outline-normal bake: the vertex Color channel is a COMPUTED quantity re-derived from the finished
/// mesh, never carried through Blender. These pin the offline-checkable parts — the encode is the exact
/// inverse of the shader's decode, split copies at a shared position weld into one smoothed normal,
/// unchanged geometry keeps its outline + tangent byte-for-bit, and an edited vertex takes its width from
/// the nearest original.
/// </summary>
public class MeshOutlineBakeTests
{
    [Fact]
    public void EncodeThenDecode_RecoversTheSmoothedNormal()
    {
        // Encode a world direction into a non-trivial tangent frame, decode it back the way the shader
        // would, land on the same unit direction: the bake is the exact inverse of the documented decode.
        var normal = Vector3.Normalize(new Vector3(0.2f, 0.3f, 0.9f));
        var tangent = new Vector4(Vector3.Normalize(new Vector3(0.9f, -0.1f, -0.2f)), -1f);
        var smoothed = Vector3.Normalize(new Vector3(-0.3f, 0.8f, 0.5f));

        var (r, g, b) = MeshApply.EncodeOutlineNormal(smoothed, normal, tangent);
        Assert.InRange(r, 0f, 1f);
        Assert.InRange(g, 0f, 1f);
        Assert.InRange(b, 0f, 1f);

        var back = Vector3.Normalize(MeshApply.DecodeOutlineNormal(r, g, b, normal, tangent));
        Assert.True((back - smoothed).Length() < 1e-4f, $"decoded {back} != smoothed {smoothed}");
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void EncodeThenDecode_RecoversTheDirection_OnAHeavilySkewedFrame(float w)
    {
        // A skewed frame (T·N ≈ 0.707, 45° off orthogonal — where dot-product encoding is badly wrong) in
        // both handednesses. The case the 3×3 solve exists for.
        var normal = Vector3.UnitZ;
        var tangent = new Vector4(Vector3.Normalize(new Vector3(1, 0, 1)), w);
        var smoothed = Vector3.Normalize(new Vector3(0.5f, -0.7f, 0.5f));

        var (r, g, b) = MeshApply.EncodeOutlineNormal(smoothed, normal, tangent);
        var back = Vector3.Normalize(MeshApply.DecodeOutlineNormal(r, g, b, normal, tangent));

        Assert.True((back - smoothed).Length() < 1e-4f, $"decoded {back} != smoothed {smoothed} (w={w})");
    }

    [Fact]
    public void EncodeDegenerate_StoresTheProjectionTheDecodeCanRepresent()
    {
        // Tangent ∥ normal: the decode basis spans only N, so the encode stores exactly S's N-component
        // (zero T/B) and decodes to (S·N)·N, not a direction in a frame the shader never uses. And it must
        // stay finite for the fully-zero vertex.
        var N = Vector3.UnitZ;
        var S = Vector3.Normalize(new Vector3(0.6f, 0f, 0.8f));
        var (r, g, b) = MeshApply.EncodeOutlineNormal(S, N, new Vector4(0, 0, 1, 1));

        Assert.Equal(0.5f, r, 4);   // zero T coefficient
        Assert.Equal(0.5f, g, 4);   // zero B coefficient
        var back = MeshApply.DecodeOutlineNormal(r, g, b, N, new Vector4(0, 0, 1, 1));
        var expected = N * Vector3.Dot(S, N);   // the only direction the rank-deficient decode can produce
        Assert.True((back - expected).Length() < 1e-4f, $"decoded {back} != projection {expected}");

        var (zr, zg, zb) = MeshApply.EncodeOutlineNormal(Vector3.Zero, Vector3.Zero, Vector4.Zero);
        Assert.InRange(zr, 0f, 1f); Assert.InRange(zg, 0f, 1f); Assert.InRange(zb, 0f, 1f);
    }

    // The arrays as BuildSkinned assembles them just before the bake: geometry from the (edited) payload,
    // Color pre-filled from the nearest original (width in .a).
    private static Dictionary<string, float[]> Arrays(float[] vertex, float[] normal, float[] tangent, float[] color) =>
        new() { ["Vertex"] = vertex, ["Normal"] = normal, ["Tangent"] = tangent, ["Color"] = color };

    private static UnityMesh Original(float[] vertex, float[] normal, float[] tangent, float[] color, string name = "orig")
    {
        int n = vertex.Length / 3;
        return new UnityMesh
        {
            Name = name, VertexCount = n,
            Channels = new() { ["Vertex"] = vertex, ["Normal"] = normal, ["Tangent"] = tangent, ["Color"] = color },
            Dims = new() { ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4, ["Color"] = 4 },
            Submeshes = new(),
        };
    }

    private static MeshApply.Payload Payload(float[] vertex, List<int[]>? submeshes = null,
        float[]? normal = null, float[]? tangent = null)
    {
        var ch = new Dictionary<string, float[]> { ["Vertex"] = vertex };
        var dims = new Dictionary<string, int> { ["Vertex"] = 3 };
        if (normal is not null) { ch["Normal"] = normal; dims["Normal"] = 3; }
        if (tangent is not null) { ch["Tangent"] = tangent; dims["Tangent"] = 4; }
        return new()
        {
            Mesh = new UnityMesh
            {
                VertexCount = vertex.Length / 3,
                Channels = ch, Dims = dims,
                Submeshes = submeshes ?? new(),
            },
        };
    }

    [Fact]
    public void BakeOutline_UnchangedGeometry_KeepsOriginalOutlineAndTangent()
    {
        // The payload equals the original across every transported channel (down to transport float noise),
        // so the original outline stays valid and is kept byte-for-bit — together with the original tangent,
        // so the two never desync.
        var pos = new float[] { 0, 0, 0, 1, 0, 0 };
        var nrm = new float[] { 0, 0, 1, 0, 0, 1 };
        var origColor = new float[] { 0.6f, 0.4f, 0.5f, 0.9f, 0.7f, 0.3f, 0.5f, 0.2f };
        var origTangent = new float[] { 1, 0, 0, 1, 1, 0, 0, 1 };
        var orig = Original(pos, nrm, origTangent, origColor);

        // the round-tripped payload carries the same frame with sub-tolerance transport jitter
        var jitterTan = new float[] { 1, 1e-7f, 0, 1, 1, 0, 1e-7f, 1 };
        var arrays = Arrays(pos, (float[])nrm.Clone(), (float[])jitterTan.Clone(), (float[])origColor.Clone());

        MeshApply.BakeOutline(arrays, orig, Payload(pos, normal: nrm, tangent: jitterTan), 2, new[] { 0, 1 });

        Assert.Equal(origColor, arrays["Color"]);       // outline kept exactly
        Assert.Equal(origTangent, arrays["Tangent"]);   // and the original tangent restored (no desync)
    }

    [Fact]
    public void BakeOutline_NormalsOnlyEdit_Rebakes()
    {
        // Positions identical but normals changed ⇒ the stored outline↔frame pairing is stale, so NOT the
        // preserve path: re-bake into the edited frame, keeping the edited tangent.
        var pos = new float[] { 0, 0, 0, 1, 0, 0 };
        var origColor = new float[] { 0.6f, 0.4f, 0.5f, 0.9f, 0.7f, 0.3f, 0.5f, 0.2f };
        var origTangent = new float[] { 1, 0, 0, 1, 1, 0, 0, 1 };
        var orig = Original(pos, new float[] { 0, 0, 1, 0, 0, 1 }, origTangent, origColor);

        var editedNrm = new float[] { 0, 1, 0, 0, 1, 0 };   // normals-only edit
        var editedTan = new float[] { 1, 0, 0, -1, 1, 0, 0, -1 };
        var arrays = Arrays(pos, editedNrm, (float[])editedTan.Clone(), (float[])origColor.Clone());

        MeshApply.BakeOutline(arrays, orig, Payload(pos, normal: editedNrm, tangent: editedTan), 2, new[] { 0, 1 });

        Assert.NotEqual(origColor, arrays["Color"]);     // re-baked, not preserved
        Assert.Equal(editedTan, arrays["Tangent"]);      // edited tangent kept (no stale restore)
        // no faces → smoothed = own normal; encoding a normal into its own frame gives (.5,.5,1)
        Assert.True(System.MathF.Abs(arrays["Color"][2] - 1f) < 1e-4f, $"b={arrays["Color"][2]}");
    }

    [Fact]
    public void BakeOutline_TopologyOnlyEdit_Rebakes()
    {
        // Different triangles ⇒ different face normals, which the smoothed outline is built from, so
        // preserve must not fire.
        var pos = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
        var nrm = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
        var tan = new float[] { 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1 };
        var origColor = new float[] { 0.6f, 0.4f, 0.5f, 0.9f, 0.6f, 0.4f, 0.5f, 0.9f, 0.6f, 0.4f, 0.5f, 0.9f };
        var orig = Original(pos, nrm, tan, origColor);
        orig.Submeshes.Add(new[] { 0, 1, 2 });

        var arrays = Arrays(pos, (float[])nrm.Clone(), (float[])tan.Clone(), (float[])origColor.Clone());
        var flipped = new List<int[]> { new[] { 0, 2, 1 } };   // reversed winding = topology edit

        MeshApply.BakeOutline(arrays, orig,
            Payload(pos, submeshes: flipped, normal: nrm, tangent: tan), 3, new[] { 0, 1, 2 });

        Assert.NotEqual(origColor, arrays["Color"]);     // re-baked, not preserved
    }

    [Fact]
    public void BakeOutline_MissingFrame_WarnsAndShipsTheFill()
    {
        // An edited payload without normals/tangents can't be re-baked: the nearest-original fill ships and
        // the bake must SAY so, rather than fill stale in silence.
        var origPos = new float[] { 0, 0, 0, 1, 0, 0 };
        var editedPos = new float[] { 0, 0, 0, 2, 0, 0 };
        var fill = new float[] { 0.6f, 0.4f, 0.5f, 0.9f, 0.7f, 0.3f, 0.5f, 0.2f };
        var orig = Original(origPos, new float[] { 0, 0, 1, 0, 0, 1 }, new float[] { 1, 0, 0, 1, 1, 0, 0, 1 }, fill);

        var arrays = new Dictionary<string, float[]> { ["Vertex"] = editedPos, ["Color"] = (float[])fill.Clone() };

        var warning = MeshApply.BakeOutline(arrays, orig, Payload(editedPos), 2, new[] { 0, 1 });

        Assert.NotNull(warning);
        Assert.Contains("outline", warning, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fill, arrays["Color"]);   // the fill ships untouched (and the warning says so)
    }

    [Fact]
    public void BakeOutline_EditedGeometry_RebakesRgb_KeepsWidthFromNearestFill()
    {
        // A moved vertex → the re-bake path. No welding here, so the smoothed normal is the vertex's own
        // and encoding it into its own frame gives (.5,.5,1). The width (.a) stays the caller's fill.
        var origPos = new float[] { 0, 0, 0, 1, 0, 0 };
        var editedPos = new float[] { 0, 0, 0, 2, 0, 0 };   // vertex 1 moved → "edited"
        var width = new float[] { 0, 0, 0, 0.8f, 0, 0, 0, 0.35f };   // rgb junk; .a is the width to preserve
        var orig = Original(origPos, new float[] { 0, 0, 1, 0, 0, 1 },
            new float[] { 1, 0, 0, 1, 1, 0, 0, 1 }, width);
        var arrays = Arrays(editedPos, new float[] { 0, 0, 1, 0, 0, 1 },
            new float[] { 1, 0, 0, 1, 1, 0, 0, 1 }, (float[])width.Clone());

        MeshApply.BakeOutline(arrays, orig, Payload(editedPos), 2, new[] { 0, 1 });

        var c = arrays["Color"];
        for (int v = 0; v < 2; v++)
        {
            Assert.True(System.MathF.Abs(c[v * 4 + 0] - 0.5f) < 1e-4f, $"r[{v}]={c[v * 4]}");   // N·T = 0
            Assert.True(System.MathF.Abs(c[v * 4 + 1] - 0.5f) < 1e-4f, $"g[{v}]={c[v * 4 + 1]}"); // N·B = 0
            Assert.True(System.MathF.Abs(c[v * 4 + 2] - 1.0f) < 1e-4f, $"b[{v}]={c[v * 4 + 2]}"); // N·N = 1
        }
        Assert.Equal(0.8f, c[3], 5);    // width of vertex 0 kept
        Assert.Equal(0.35f, c[7], 5);   // width of vertex 1 kept
    }

    [Fact]
    public void BakeOutline_WhiteDisabledVertex_StaysWhite()
    {
        // A vertex whose nearest original had its outline DISABLED (white Color) stays white: the bake never
        // paints an outline where the game authored none.
        var editedPos = new float[] { 0, 0, 0, 2, 0, 0 };
        var origPos = new float[] { 0, 0, 0, 1, 0, 0 };
        var color = new float[] { 1, 1, 1, 1, /*v1 real*/ 0.2f, 0.2f, 0.9f, 0.5f };   // v0 white, v1 real
        var orig = Original(origPos, new float[] { 0, 0, 1, 0, 0, 1 }, new float[] { 1, 0, 0, 1, 1, 0, 0, 1 }, color);
        var arrays = Arrays(editedPos, new float[] { 0, 0, 1, 0, 0, 1 },
            new float[] { 1, 0, 0, 1, 1, 0, 0, 1 }, (float[])color.Clone());

        MeshApply.BakeOutline(arrays, orig, Payload(editedPos), 2, new[] { 0, 1 });

        var c = arrays["Color"];
        Assert.Equal(new float[] { 1, 1, 1, 1 }, new[] { c[0], c[1], c[2], c[3] });   // v0 left white (disabled)
        Assert.True(System.MathF.Abs(c[6] - 1.0f) < 1e-4f, $"v1 re-baked b={c[6]}");   // v1 re-baked (b≈1 for +Z)
    }

    [Fact]
    public void BakeOutline_HairMesh_CarriesAuthoredOutline_NotASurfaceNormal()
    {
        // Hair's outline is non-geometric, so the bake CARRIES the authored one (decode nearest original,
        // re-encode into this vertex's frame) rather than compute a surface normal. The original here decodes
        // well off the surface normal, so recovering that same direction proves it was carried, not replaced
        // by the +Z a geometric bake would produce.
        var origPos = new float[] { 0, 0, 0, 1, 0, 0 };
        var editedPos = new float[] { 0, 0, 0, 2, 0, 0 };
        var N = new Vector3(0, 0, 1);
        var tan4 = new Vector4(1, 0, 0, 1);
        var authored = Vector3.Normalize(new Vector3(0.6f, 0.2f, 0.5f));   // NOT the surface normal (+Z)
        var (ar, ag, ab) = MeshApply.EncodeOutlineNormal(authored, N, tan4);
        var origColor = new float[] { ar, ag, ab, 0.5f, ar, ag, ab, 0.5f };
        var orig = Original(origPos, new float[] { 0, 0, 1, 0, 0, 1 }, new float[] { 1, 0, 0, 1, 1, 0, 0, 1 },
            origColor, name: "c_Foo_slg_hair_lod0");
        var arrays = Arrays(editedPos, new float[] { 0, 0, 1, 0, 0, 1 },
            new float[] { 1, 0, 0, 1, 1, 0, 0, 1 }, (float[])origColor.Clone());

        MeshApply.BakeOutline(arrays, orig, Payload(editedPos), 2, new[] { 0, 1 });

        var c = arrays["Color"];
        var got = Vector3.Normalize(MeshApply.DecodeOutlineNormal(c[0], c[1], c[2], N, tan4));
        Assert.True((got - authored).Length() < 1e-4f, $"hair outline {got} should carry the authored {authored}");
        Assert.True((got - Vector3.UnitZ).Length() > 0.1f, "must NOT be the +Z surface normal");
    }

    [Fact]
    public void BakeOutline_HairCarry_PreservesWorldDirection_AcrossAChangedFrame()
    {
        // The carry must survive a frame change: a raw byte copy would swing the outline with the frame.
        // Carry decodes in the ORIGINAL frame and re-encodes into the NEW one, so decoding the result
        // through the new frame lands back on the same world direction.
        var origPos = new float[] { 0, 0, 0, 1, 0, 0 };
        var editedPos = new float[] { 0, 0, 0, 2, 0, 0 };   // moved → not the preserve path
        var oN = Vector3.UnitZ;
        var oT4 = new Vector4(1, 0, 0, 1);
        var authored = Vector3.Normalize(new Vector3(0.6f, 0.2f, 0.5f));
        var (ar, ag, ab) = MeshApply.EncodeOutlineNormal(authored, oN, oT4);
        var origColor = new float[] { ar, ag, ab, 0.5f, ar, ag, ab, 0.5f };
        var orig = Original(origPos, new float[] { 0, 0, 1, 0, 0, 1 }, new float[] { 1, 0, 0, 1, 1, 0, 0, 1 },
            origColor, name: "c_Foo_slg_hair_lod0");

        // the edited mesh's frame is rotated: normal +Y, tangent +Z (orthonormal but nothing like +Z/+X)
        var newNrm = new float[] { 0, 1, 0, 0, 1, 0 };
        var newTan = new float[] { 0, 0, 1, 1, 0, 0, 1, 1 };
        var arrays = Arrays(editedPos, newNrm, newTan, (float[])origColor.Clone());

        MeshApply.BakeOutline(arrays, orig, Payload(editedPos), 2, new[] { 0, 1 });

        var c = arrays["Color"];
        var got = Vector3.Normalize(MeshApply.DecodeOutlineNormal(c[0], c[1], c[2],
            new Vector3(0, 1, 0), new Vector4(0, 0, 1, 1)));
        Assert.True((got - authored).Length() < 1e-4f,
            $"carried direction {got} should equal the authored {authored} through the NEW frame");
        // and the stored bytes must differ from the original's (same bytes would mean frame-blind copying)
        Assert.False(System.MathF.Abs(c[0] - ar) < 1e-6f && System.MathF.Abs(c[1] - ag) < 1e-6f
                     && System.MathF.Abs(c[2] - ab) < 1e-6f, "bytes were copied, not re-encoded");
    }

    [Fact]
    public void SmoothNormals_WeldsSplitVerts_ToAreaWeightedFaceNormal()
    {
        // A UV-seam / hard-edge split: two triangles meet at (0,0,0) as v0 (+Z face) and v3 (+Y face). The
        // smoothed normal welds them to the area-weighted average of the two face normals (equal areas here
        // → the bisector), and both copies take it — which keeps the outline continuous across the split.
        var pos = new float[]
        {
            0, 0, 0,  1, 0, 0,  0, 1, 0,     // tri 1: v0,v1,v2  → face normal +Z
            0, 0, 0, -1, 0, 0,  0, 0, 1,     // tri 2: v3,v4,v5  → face normal +Y (v3 coincident with v0)
        };
        var nrm = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 1, 0, 0, 1, 0, 0, 1, 0 };
        var subs = new List<int[]> { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } };

        var s = MeshApply.SmoothNormalsByPosition(pos, nrm, 3, 6, subs);

        var expected = Vector3.Normalize(new Vector3(0, 1, 1));
        Assert.True((s[0] - expected).Length() < 1e-4f, $"v0 welded normal {s[0]} != {expected}");
        Assert.True((s[3] - expected).Length() < 1e-4f, $"v3 welded normal {s[3]} != {expected}");
        // a non-shared vertex keeps its own face normal (v1 belongs only to tri 1 → +Z)
        Assert.True((s[1] - Vector3.UnitZ).Length() < 1e-4f, $"v1 {s[1]}");
    }
}
