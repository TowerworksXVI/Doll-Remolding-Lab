using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Mesh;
using Remold.Core.Textures;
using Remold.Core.Tests.Support;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;
using ImageSharp = SixLabors.ImageSharp.Image;

namespace Remold.Core.Tests;

/// <summary>
/// Export round-trips that need no game bundle: synthetic data out, read back with the same libraries the
/// app uses. Pins the vertex-attribute mapping, the per-submesh primitive split, and the texture flip.
/// </summary>
public class ExportRoundtripTests
{
    // A square (4 verts, 2 triangles) with positions + normals + UV; optionally split in two submeshes.
    private static UnityMesh Quad(bool twoSubmeshes = false)
    {
        var submeshes = twoSubmeshes
            ? new List<int[]> { new[] { 0, 1, 2 }, new[] { 2, 1, 3 } }
            : new List<int[]> { new[] { 0, 1, 2, 2, 1, 3 } };
        return new UnityMesh
        {
            Name = "quad",
            VertexCount = 4,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0 },
                ["Normal"] = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                ["TexCoord0"] = new float[] { 0, 0, 1, 0, 0, 1, 1, 1 },
            },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["Normal"] = 3, ["TexCoord0"] = 2 },
            Submeshes = submeshes,
        };
    }

    [Fact]
    public void ExportGlb_PreservesPositionsNormalsUv_AndIndices()
    {
        using var g = new TempGame();
        var path = g.At("quad.glb");
        MeshGltf.ExportGlb(Quad(), path);

        var model = ModelRoot.Load(path);
        var prim = model.LogicalMeshes.Single().Primitives.Single();

        var positions = prim.GetVertexAccessor("POSITION").AsVector3Array();
        Assert.Equal(4, positions.Count);
        // Unity (1,1,0) → glTF with X negated for the handedness flip (AxisConvention).
        Assert.Equal(new System.Numerics.Vector3(-1, 1, 0), positions[3]);

        Assert.NotNull(prim.GetVertexAccessor("NORMAL"));
        Assert.NotNull(prim.GetVertexAccessor("TEXCOORD_0"));
        // Winding reverses per triangle, to keep faces front-facing after the X reflection.
        Assert.Equal(new[] { 0u, 2u, 1u, 2u, 3u, 1u }, prim.GetIndices().ToArray());
    }

    [Fact]
    public void ExportGlb_EmitsOnePrimitivePerSubmesh()
    {
        using var g = new TempGame();
        var path = g.At("quad2.glb");
        MeshGltf.ExportGlb(Quad(twoSubmeshes: true), path);

        var model = ModelRoot.Load(path);
        Assert.Equal(2, model.LogicalMeshes.Single().Primitives.Count);
    }

    [Fact]
    public void ExportGlb_DoesNotEmitTheOutlineChannel()
    {
        // The outline channel is re-baked at package time and never carried through Blender, so the export
        // emits neither a custom attribute nor a standard COLOR_0.
        using var g = new TempGame();
        var path = g.At("colored.glb");
        var mesh = Quad();
        mesh.Channels["Color"] = new float[] { 1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1, 1, 1, 0, 1 };
        mesh.Dims["Color"] = 4;
        MeshGltf.ExportGlb(mesh, path);

        var prim = ModelRoot.Load(path).LogicalMeshes.Single().Primitives.Single();
        Assert.Null(prim.GetVertexAccessor("_GF2EDGE"));
        Assert.Null(prim.GetVertexAccessor("COLOR_0"));
    }

    [Fact]
    public void WritePng_FlipsVertically_UnityBottomUpToTopDown()
    {
        // Row 0 is Unity's BOTTOM row, so after the flip the PNG's top row must be colorB.
        const byte aR = 30, bR = 60;
        var bgra = new byte[]
        {
            10, 20, aR, 255,  10, 20, aR, 255,   // row 0 (bottom): colorA
            40, 50, bR, 255,  40, 50, bR, 255,   // row 1 (top):    colorB
        };
        using var g = new TempGame();
        var path = g.At("tex.png");
        TextureExport.WritePng(new BundleReader.DecodedTexture(bgra, 2, 2, "RGBA32"), path);

        using var img = ImageSharp.Load<Bgra32>(path);
        Assert.Equal(2, img.Width);
        Assert.Equal(2, img.Height);
        Assert.Equal(bR, img[0, 0].R);   // top row is colorB after the flip
        Assert.Equal(aR, img[0, 1].R);   // bottom row is colorA
    }
}
