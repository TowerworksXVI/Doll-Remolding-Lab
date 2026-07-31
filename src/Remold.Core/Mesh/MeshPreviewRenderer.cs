using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Remold.Core.Mesh;

/// <summary>
/// Software-rasterizes a mesh into a transparent square thumbnail. The fixed orthographic camera and
/// light make the output deterministic; bounds are fitted with a margin so differently-scaled meshes
/// fill the same area. A z-buffer keeps intersecting geometry stable with no 3D dependency.
/// </summary>
public static class MeshPreviewRenderer
{
    public const int DefaultSize = 256;

    // GFL2 character meshes face -Z at +X-ish in bundle space; the mirrored vector is a back view
    private static readonly Vector3 ViewForward = Vector3.Normalize(new Vector3(0.72f, -0.28f, -0.64f));
    private static readonly Vector3 ViewRight = Vector3.Normalize(Vector3.Cross(ViewForward, Vector3.UnitY));
    private static readonly Vector3 ViewUp = Vector3.Normalize(Vector3.Cross(ViewRight, ViewForward));
    private static readonly Vector3 LightDirection = Vector3.Normalize(new Vector3(0.45f, 0.75f, 0.48f));

    /// <summary>A decoded sampling texture for one submesh: the part's base-color map at thumb
    /// resolution. Rows are top-down; Unity UVs sample v=0 at the bottom.</summary>
    public readonly record struct PreviewTexture(Rgba32[] Pixels, int Width, int Height)
    {
        /// <summary>Decode PNG bytes into a sampling texture, or null when they won't decode — the
        /// submesh then degrades to the flat shade rather than failing the preview.</summary>
        public static PreviewTexture? TryFromPng(byte[] png)
        {
            try
            {
                using var img = Image.Load<Rgba32>(png);
                var pixels = new Rgba32[img.Width * img.Height];
                img.CopyPixelDataTo(pixels);
                return new PreviewTexture(pixels, img.Width, img.Height);
            }
            catch { return null; }
        }
    }

    /// <summary>Render flat-shaded triangles on a transparent background. Invalid or empty geometry
    /// THROWS, so callers reach their no-preview state instead of caching a blank image.
    /// <paramref name="submeshTextures"/> (optional, index-aligned with <see cref="UnityMesh.Submeshes"/>)
    /// samples each submesh's base-color map through the mesh's UVs; a null entry, missing UV channel or
    /// short list degrades that submesh to the flat shade — texturing never fails a preview.</summary>
    public static Image<Rgba32> Render(UnityMesh mesh, int size = DefaultSize,
        IReadOnlyList<PreviewTexture?>? submeshTextures = null)
    {
        if (size < 8) throw new ArgumentOutOfRangeException(nameof(size), "preview size must be at least 8 pixels");
        if (!mesh.Has("Vertex") || mesh.VertexCount <= 0)
            throw new InvalidDataException("mesh has no vertex positions");

        var positions = mesh.AsVector3("Vertex");
        if (positions.Count == 0) throw new InvalidDataException("mesh has no vertex positions");

        var projected = new ProjectedVertex[positions.Count];
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        for (int i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            if (!IsFinite(p)) throw new InvalidDataException("mesh contains a non-finite vertex position");
            // screen X is negated so the preview matches the modder's Blender view; bundle space is
            // Unity-left-handed and would otherwise render horizontally flipped
            float x = -Vector3.Dot(p, ViewRight);
            float y = Vector3.Dot(p, ViewUp);
            float z = Vector3.Dot(p, ViewForward);
            projected[i] = new ProjectedVertex(x, y, z);
            minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
        }

        float width = maxX - minX, height = maxY - minY;
        if (width <= 1e-8f && height <= 1e-8f)
            throw new InvalidDataException("mesh bounds are degenerate");
        float margin = MathF.Max(2f, size * 0.075f);
        float scale = MathF.Min((size - margin * 2f) / MathF.Max(width, 1e-8f),
                                (size - margin * 2f) / MathF.Max(height, 1e-8f));
        float centerX = (minX + maxX) * 0.5f, centerY = (minY + maxY) * 0.5f;
        float imageCenter = (size - 1) * 0.5f;
        for (int i = 0; i < projected.Length; i++)
        {
            var p = projected[i];
            projected[i] = p with
            {
                X = (p.X - centerX) * scale + imageCenter,
                Y = imageCenter - (p.Y - centerY) * scale,
            };
        }

        var image = new Image<Rgba32>(size, size, new Rgba32(0, 0, 0, 0));
        var depth = new float[size * size];
        Array.Fill(depth, float.PositiveInfinity);
        int pixelsDrawn = 0;

        // UVs are needed only for texturing; a mesh without TexCoord0 renders untextured everywhere.
        var uvs = submeshTextures is not null && mesh.Has("TexCoord0") ? mesh.AsVector2("TexCoord0") : null;
        if (uvs is not null && uvs.Count != positions.Count) uvs = null;

        for (int si = 0; si < mesh.Submeshes.Count; si++)
        {
            var submesh = mesh.Submeshes[si];
            PreviewTexture? tex = submeshTextures is not null && si < submeshTextures.Count && uvs is not null
                ? submeshTextures[si] : null;
            for (int i = 0; i + 2 < submesh.Length; i += 3)
            {
                int ia = submesh[i], ib = submesh[i + 1], ic = submesh[i + 2];
                if ((uint)ia >= (uint)positions.Count || (uint)ib >= (uint)positions.Count ||
                    (uint)ic >= (uint)positions.Count)
                    continue;

                var a = projected[ia]; var b = projected[ib]; var c = projected[ic];
                float area = Edge(a.X, a.Y, b.X, b.Y, c.X, c.Y);
                if (MathF.Abs(area) < 1e-6f) continue;

                var normal = Vector3.Cross(positions[ib] - positions[ia], positions[ic] - positions[ia]);
                if (normal.LengthSquared() < 1e-12f) continue;
                normal = Vector3.Normalize(normal);
                float diffuse = MathF.Abs(Vector3.Dot(normal, LightDirection));
                byte gray = (byte)Math.Clamp((int)MathF.Round(72f + 116f * diffuse), 0, 255);
                var flat = new Rgba32(gray, gray, gray, 255);
                // same single-light model as the flat shade, as a luminance factor over the map's colors
                float lum = 0.45f + 0.55f * diffuse;

                int x0 = Math.Clamp((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0, size - 1);
                int x1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), 0, size - 1);
                int y0 = Math.Clamp((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0, size - 1);
                int y1 = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), 0, size - 1);

                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float wa = Edge(b.X, b.Y, c.X, c.Y, px, py) / area;
                        float wb = Edge(c.X, c.Y, a.X, a.Y, px, py) / area;
                        float wc = 1f - wa - wb;
                        if (wa < -1e-5f || wb < -1e-5f || wc < -1e-5f) continue;
                        float z = wa * a.Z + wb * b.Z + wc * c.Z;
                        int di = y * size + x;
                        if (z >= depth[di]) continue;
                        depth[di] = z;
                        if (image[x, y].A == 0) pixelsDrawn++;
                        image[x, y] = tex is { } t ? Sample(t, uvs!, ia, ib, ic, wa, wb, wc, lum) : flat;
                    }
            }
        }

        if (pixelsDrawn == 0)
        {
            image.Dispose();
            throw new InvalidDataException("mesh contains no rasterizable triangles");
        }
        return image;
    }

    /// <summary>Render directly to PNG bytes for UI consumers that must not retain ImageSharp objects.</summary>
    public static byte[] RenderPng(UnityMesh mesh, int size = DefaultSize,
        IReadOnlyList<PreviewTexture?>? submeshTextures = null)
    {
        using var image = Render(mesh, size, submeshTextures);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream, new PngEncoder { CompressionLevel = PngCompressionLevel.Level1 });
        return stream.ToArray();
    }

    /// <summary>Render a workspace glb's mesh exactly as it sits — NO transform. The workspace glb is
    /// written with the target's scene-rest bake already in its geometry and the vanilla thumb applies that
    /// same bake to the bundle mesh, so both routes stand in scene-rest orientation and an edited part
    /// cannot rotate relative to its own vanilla preview.</summary>
    public static byte[] RenderWorkspacePng(UnityMesh mesh, int size = DefaultSize,
        IReadOnlyList<PreviewTexture?>? submeshTextures = null) =>
        RenderPng(mesh, size, submeshTextures);

    /// <summary>Nearest-neighbor sample at the barycentric UV, wrap-repeat, lit by the triangle's flat
    /// luminance. Unity UVs put v=0 at the BOTTOM; the sampling texture's rows are top-down.</summary>
    private static Rgba32 Sample(PreviewTexture t, IReadOnlyList<Vector2> uvs, int ia, int ib, int ic,
        float wa, float wb, float wc, float lum)
    {
        float u = wa * uvs[ia].X + wb * uvs[ib].X + wc * uvs[ic].X;
        float v = wa * uvs[ia].Y + wb * uvs[ib].Y + wc * uvs[ic].Y;
        u -= MathF.Floor(u); v -= MathF.Floor(v);
        int col = Math.Clamp((int)(u * t.Width), 0, t.Width - 1);
        int row = Math.Clamp((int)((1f - v) * t.Height), 0, t.Height - 1);
        var p = t.Pixels[row * t.Width + col];
        return new Rgba32(
            (byte)Math.Clamp((int)(p.R * lum), 0, 255),
            (byte)Math.Clamp((int)(p.G * lum), 0, 255),
            (byte)Math.Clamp((int)(p.B * lum), 0, 255), 255);
    }

    private static float Edge(float ax, float ay, float bx, float by, float px, float py) =>
        (px - ax) * (by - ay) - (py - ay) * (bx - ax);

    private static bool IsFinite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    private readonly record struct ProjectedVertex(float X, float Y, float Z);
}
