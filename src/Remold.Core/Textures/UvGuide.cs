using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Remold.Core.Mesh;
using Remold.Core.Project;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Remold.Core.Textures;

/// <summary>
/// Renders a selected UV set's wireframe to a transparent PNG — the guide a modder layers under their paint.
/// Edges are rasterized by hand (Bresenham) so only ImageSharp core is needed, not the
/// separately-licensed <c>.Drawing</c> package.
///
/// UVs are Unity-convention (v = 0 at the bottom) and flipped vertically into image space so the guide
/// lines up with the exported texture PNG.
/// </summary>
public static class UvGuide
{
    /// <summary>Canvas size used when a part has no existing texture to size the guide against.</summary>
    public const int DefaultSize = 2048;

    /// <summary>The evidenced game-shader UV set for one texture role. Ordinary PBR pictures sample UV0;
    /// the effect overlay samples UV1. A generic property has no evidenced answer and stays null rather
    /// than inheriting one from its name.</summary>
    public static int? TexCoordIndex(TargetInputKind input) => input switch
    {
        TargetInputKind.BaseColor or TargetInputKind.Normal or TargetInputKind.Rmo => 0,
        TargetInputKind.Blend => 1,
        _ => null,
    };

    /// <summary>The preview/transport semantic for one authored texture input.</summary>
    public static MapKind MapKindFor(TargetInputKind input) => input switch
    {
        TargetInputKind.BaseColor => MapKind.BaseColor,
        TargetInputKind.Normal => MapKind.Normal,
        TargetInputKind.Rmo => MapKind.Rmo,
        TargetInputKind.Blend => MapKind.Blend,
        _ => MapKind.Texture,
    };

    /// <summary>The guide channel for one input. A non-evidenced property retains the guide's historical
    /// UV0 fallback; transport callers use <see cref="TexCoordIndex"/> directly and therefore do not guess.</summary>
    public static string TexCoordChannel(TargetInputKind input) =>
        $"TexCoord{TexCoordIndex(input) ?? 0}";

    /// <summary>Render every submesh's UV wireframe to <paramref name="outPath"/> as a transparent
    /// <paramref name="width"/>×<paramref name="height"/> PNG. <paramref name="channel"/> picks the UV
    /// set — TexCoord0 for the ordinary maps, TexCoord1 for the effect overlay, which the corpus samples
    /// by its own second layout (measured: UV1 exists exactly on the parts that bind <c>_BlendTex</c>).
    /// False (and no write) when the mesh lacks the channel or the size is non-positive.</summary>
    public static bool TryRender(UnityMesh mesh, int width, int height, string outPath,
        string channel = "TexCoord0") =>
        Render(mesh, null, width, height, outPath, merge: false, channel);

    /// <summary>As <see cref="TryRender"/> for a SUBSET of submeshes, merge-plotting onto an existing
    /// same-size guide: a per-texture guide is shared, so every part sampling that texture adds its
    /// islands and the file converges to their union. A missing or size-mismatched file starts fresh —
    /// texture sizes are fixed per catalog, so a mismatch means a stale guide.</summary>
    public static bool TryRenderMerge(UnityMesh mesh, IReadOnlyList<int> submeshIndices, int width, int height,
        string outPath, string channel = "TexCoord0") =>
        Render(mesh, submeshIndices, width, height, outPath, merge: true, channel);

    private static bool Render(UnityMesh mesh, IReadOnlyList<int>? submeshIndices, int width, int height,
        string outPath, bool merge, string channel)
    {
        if (!mesh.Has(channel) || width <= 0 || height <= 0) return false;
        var uv = mesh.AsVector2(channel);

        Image<Rgba32>? existing = null;
        if (merge && File.Exists(outPath))
        {
            try
            {
                existing = Image.Load<Rgba32>(outPath);
                if (existing.Width != width || existing.Height != height) { existing.Dispose(); existing = null; }
            }
            catch { existing?.Dispose(); existing = null; }   // unreadable → fresh canvas
        }
        using var img = existing ?? new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 0));
        var line = new Rgba32(255, 255, 255, 255);

        void Plot(int x, int y)
        {
            if ((uint)x < (uint)width && (uint)y < (uint)height)
                img[x, y] = line;
        }

        (int X, int Y) ToPx(Vector2 t) =>
            ((int)MathF.Round(t.X * (width - 1)), (int)MathF.Round((1f - t.Y) * (height - 1)));

        var subs = submeshIndices is null
            ? mesh.Submeshes
            : submeshIndices.Where(i => i >= 0 && i < mesh.Submeshes.Count).Select(i => mesh.Submeshes[i]);
        foreach (var sub in subs)
            for (int i = 0; i + 2 < sub.Length; i += 3)
            {
                if (sub[i] >= uv.Count || sub[i + 1] >= uv.Count || sub[i + 2] >= uv.Count) continue;
                var a = ToPx(uv[sub[i]]);
                var b = ToPx(uv[sub[i + 1]]);
                var c = ToPx(uv[sub[i + 2]]);
                DrawLine(a, b, Plot);
                DrawLine(b, c, Plot);
                DrawLine(c, a, Plot);
            }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        img.SaveAsPng(outPath, TextureExport.FastPng);   // shared fast encoder (see TextureExport.FastPng)
        return true;
    }

    /// <summary>Bresenham line between two integer pixels, calling <paramref name="plot"/> per pixel.</summary>
    private static void DrawLine((int X, int Y) p0, (int X, int Y) p1, Action<int, int> plot)
    {
        int x0 = p0.X, y0 = p0.Y, x1 = p1.X, y1 = p1.Y;
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            plot(x0, y0);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
