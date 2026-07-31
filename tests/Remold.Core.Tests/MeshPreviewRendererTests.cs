using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Remold.Core.Mesh;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

public class MeshPreviewRendererTests
{
    [Fact]
    public void Render_Tetrahedron_FitsBoundsWithNonEmptySilhouetteAndTransparentCorners()
    {
        var mesh = new UnityMesh
        {
            Name = "tetra",
            VertexCount = 4,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = new[]
                {
                    -1f, -1f, -1f,
                     1f, -1f, -1f,
                     0f,  1f, -1f,
                     0f,  0f,  1f,
                },
            },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
            Submeshes = new List<int[]> { new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3 } },
        };

        using var image = MeshPreviewRenderer.Render(mesh);

        int opaque = 0, minX = image.Width, minY = image.Height, maxX = -1, maxY = -1;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                if (image[x, y].A != 0)
                {
                    opaque++;
                    minX = System.Math.Min(minX, x); maxX = System.Math.Max(maxX, x);
                    minY = System.Math.Min(minY, y); maxY = System.Math.Max(maxY, y);
                }

        Assert.True(opaque > 1_000);
        Assert.InRange(minX, 8, 40); Assert.InRange(minY, 8, 40);
        Assert.InRange(maxX, 215, 247); Assert.InRange(maxY, 215, 247);
        Assert.Equal(new Rgba32(0, 0, 0, 0), image[0, 0]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), image[image.Width - 1, image.Height - 1]);
    }

    [Fact]
    public void Render_DegenerateMesh_ThrowsInvalidDataException()
    {
        var mesh = Mesh(new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f }, new[] { 0, 1, 2 });

        Assert.Throws<InvalidDataException>(() => MeshPreviewRenderer.Render(mesh));
    }

    [Fact]
    public void Render_OutOfRangeIndices_AreSkippedWithoutLosingValidTriangles()
    {
        var mesh = Mesh(new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f },
            new[] { 0, 99, 2, 0, 1, 2 });

        using var image = MeshPreviewRenderer.Render(mesh);

        bool anyOpaque = false;
        for (int y = 0; y < image.Height && !anyOpaque; y++)
            for (int x = 0; x < image.Width; x++)
                if (image[x, y].A != 0) { anyOpaque = true; break; }
        Assert.True(anyOpaque);
    }

    [Fact]
    public void RenderWorkspacePng_RendersTheWorkspaceMeshAsItSits_ApplyingNoTransform()
    {
        // A workspace glb is written already-uprighted and the vanilla thumb uprights the bundle mesh to
        // match, so this route must render what it is handed. Un-baking here would stand the edited part
        // 90° off its own vanilla preview.
        var raw = Mesh(new[]
        {
            -2f, -1f, 0f,
             1f, -1f, 0f,
             0f,  2f, 0f,
             0f,  0f, 1f,
        }, new[] { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3 });
        var rest = RestBake.Snap(Matrix4x4.CreateRotationX(-System.MathF.PI / 2))!.Value;
        var baked = RestBake.Apply(raw, rest);

        Assert.Equal(MeshPreviewRenderer.RenderPng(baked, 96), MeshPreviewRenderer.RenderWorkspacePng(baked, 96));
        // the bake is visible in this fixture, so the equality above is a real assertion about orientation
        Assert.NotEqual(MeshPreviewRenderer.RenderPng(raw, 96), MeshPreviewRenderer.RenderWorkspacePng(baked, 96));
    }

    [Fact]
    public void Render_SubmeshTextures_SampleTheirOwnMap_AndAbsentSamplerStaysNeutral()
    {
        // The tetrahedron split into two submeshes; constant UVs make every pixel sample one texel, so the
        // expectation is spec-level: submesh 0's pixels carry the red map, submesh 1's the blue, and the
        // untextured render stays achromatic. Which screen region each submesh lands in is NOT asserted
        // (that's camera detail, not contract).
        var mesh = new UnityMesh
        {
            Name = "tetra-tex",
            VertexCount = 4,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = new[] { -1f, -1f, -1f, 1f, -1f, -1f, 0f, 1f, -1f, 0f, 0f, 1f },
                ["TexCoord0"] = new[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f },
            },
            Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["TexCoord0"] = 2 },
            Submeshes = new List<int[]> { new[] { 0, 2, 1, 0, 1, 3 }, new[] { 1, 2, 3, 2, 0, 3 } },
        };
        var red = Solid(new Rgba32(255, 0, 0, 255));
        var blue = Solid(new Rgba32(0, 0, 255, 255));

        using var textured = MeshPreviewRenderer.Render(mesh, 256,
            new MeshPreviewRenderer.PreviewTexture?[] { red, blue });
        bool sawRed = false, sawBlue = false;
        for (int y = 0; y < textured.Height; y++)
            for (int x = 0; x < textured.Width; x++)
            {
                var p = textured[x, y];
                if (p.A == 0) continue;
                if (p.R > p.B + 20) sawRed = true;
                if (p.B > p.R + 20) sawBlue = true;
            }
        Assert.True(sawRed, "no pixel carried submesh 0's red map");
        Assert.True(sawBlue, "no pixel carried submesh 1's blue map");

        using var plain = MeshPreviewRenderer.Render(mesh, 256);
        for (int y = 0; y < plain.Height; y++)
            for (int x = 0; x < plain.Width; x++)
            {
                var p = plain[x, y];
                if (p.A == 0) continue;
                Assert.True(p.R == p.G && p.G == p.B, $"untextured pixel at {x},{y} is not achromatic");
            }
    }

    private static MeshPreviewRenderer.PreviewTexture Solid(Rgba32 color) =>
        new(new[] { color, color, color, color }, 2, 2);

    private static UnityMesh Mesh(float[] positions, int[] triangles) => new()
    {
        Name = "synthetic",
        VertexCount = positions.Length / 3,
        Channels = new Dictionary<string, float[]> { ["Vertex"] = positions },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3 },
        Submeshes = new List<int[]> { triangles },
    };
}
