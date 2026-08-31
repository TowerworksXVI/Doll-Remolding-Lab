using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Mesh;
using Remold.Core.Textures;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Remold.Core.Project;

/// <summary>Normalizes the material nodes from one exact Blender return into transient, per-submesh files.
/// It assigns no authored ownership: the caller publishes each result to the exact session slot carried by
/// the transport. Stock identity is resolved against that target's own outbound map record before this call.</summary>
public static class BlenderMaterialReturn
{
    public static IReadOnlyList<SubmeshTextures> Normalize(IReadOnlyList<IncomingMaps> maps,
        string stagingDirectory, Func<int, string?>? stockRmoPng = null, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var rows = new List<SubmeshTextures>();
        for (int submesh = 0; submesh < maps.Count; submesh++)
        {
            var albedo = Take(maps[submesh].BaseColor, stagingDirectory, submesh, "base");
            var normal = Take(maps[submesh].Normal, stagingDirectory, submesh, "normal");
            var rmo = TakeRmo(maps[submesh].Rmo, stagingDirectory, submesh, stockRmoPng, report);
            (string? File, SlotOrigin Origin) blend = default;
            var textures = new List<PropertyTextureBinding>();
            var primaryKinds = new HashSet<MapKind>();
            foreach (var texture in maps[submesh].Textures ?? Array.Empty<IncomingTexture>())
            {
                bool fixedKind = texture.Kind is MapKind.BaseColor or MapKind.Normal or MapKind.Rmo
                    or MapKind.Blend;
                if (fixedKind && primaryKinds.Add(texture.Kind))
                {
                    if (texture.Kind == MapKind.Blend)
                        blend = Take(texture.Map, stagingDirectory, submesh, texture.ShaderProperty);
                    continue;
                }
                var taken = Take(texture.Map, stagingDirectory, submesh, texture.ShaderProperty);
                if (taken.Origin.IsAsk())
                    textures.Add(new PropertyTextureBinding
                    {
                        ShaderProperty = texture.ShaderProperty,
                        File = taken.File,
                        Origin = taken.Origin,
                    });
            }
            if (!albedo.Origin.IsAsk() && !normal.Origin.IsAsk() && !rmo.Origin.IsAsk()
                && !blend.Origin.IsAsk() && textures.Count == 0) continue;
            rows.Add(new SubmeshTextures
            {
                Submesh = submesh,
                Albedo = albedo.File,
                Normal = normal.File,
                Rmo = rmo.File,
                AlbedoOrigin = albedo.Origin,
                NormalOrigin = normal.Origin,
                RmoOrigin = rmo.Origin,
                RmoAlpha = rmo.Alpha,
                Blend = blend.File,
                BlendOrigin = blend.Origin,
                Textures = textures.Count == 0 ? null : textures,
            });
        }
        return rows;
    }

    private static (string? File, SlotOrigin Origin) Take(ResolvedMap map, string root,
        int submesh, string input)
    {
        if (map.Origin == MapOrigin.Authored && map.AuthoredPng is not null)
        {
            string path = PathFor(root, submesh, input);
            TextureIngress.Publish(map.AuthoredPng, path);
            return (path, SlotOrigin.Authored);
        }
        if (map.Origin == MapOrigin.Neutral) return (null, SlotOrigin.ExplicitNeutral);
        // A slot still bound to the map the export embedded THERE ships nothing: the build reads it off the
        // game. A stock map plugged into a slot it was not exported on never reaches here — another part's,
        // or another material of this one's — because the read before this call classifies it Authored, and it
        // ships as this slot's own map. That is exactly what carries a deliberate texture link, either way.
        if (map.Origin == MapOrigin.Vanilla) return (null, SlotOrigin.VanillaOwn);
        return (null, SlotOrigin.None);
    }

    /// <summary>The RMO slot. Only the authored case differs from the others: the shipped map's alpha is
    /// rebuilt from the stock map <paramref name="stockRmoPng"/> names rather than taken from Blender, and it
    /// is the only case that asks.</summary>
    private static (string? File, SlotOrigin Origin, RmoAlphaAnswer? Alpha) TakeRmo(
        ResolvedMap map, string root, int submesh, Func<int, string?>? stockRmoPng,
        Action<string>? report)
    {
        if (map.Origin != MapOrigin.Authored || map.AuthoredPng is null)
        {
            var taken = Take(map, root, submesh, "rmo");
            return (taken.File, taken.Origin, null);
        }
        string path = PathFor(root, submesh, "rmo");
        TextureIngress.Publish(WithStockAlpha(map.AuthoredPng, stockRmoPng?.Invoke(submesh), report), path);
        return (path, SlotOrigin.Authored, RmoAlphaAnswer.Rebuild);
    }

    private static string PathFor(string root, int submesh, string input)
    {
        string directory = Path.Combine(root, $"submesh-{submesh:D4}");
        Directory.CreateDirectory(directory);
        string safe = new(input.TrimStart('_').Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        if (safe.Length == 0) safe = "texture";
        return Path.Combine(directory, safe + ".png");
    }

    private static byte[] WithStockAlpha(byte[] authoredPng, string? stockRmo, Action<string>? report)
    {
        using var authored = Image.Load<Rgba32>(authoredPng);
        using var stock = LoadStockRmo(stockRmo, report);
        int width = Math.Max(authored.Width, stock?.Width ?? 0);
        int height = Math.Max(authored.Height, stock?.Height ?? 0);
        using var result = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var pixel = authored[Nearest(x, width, authored.Width), Nearest(y, height, authored.Height)];
                byte alpha = stock is null ? (byte)0
                    : stock[Nearest(x, width, stock.Width), Nearest(y, height, stock.Height)].A;
                result[x, y] = new Rgba32(pixel.R, pixel.G, pixel.B, alpha);
            }
        using var stream = new MemoryStream();
        result.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static Image<Rgba32>? LoadStockRmo(string? stockRmo, Action<string>? report)
    {
        if (stockRmo is null) return null;
        if (!File.Exists(stockRmo))
        {
            report?.Invoke($"Couldn't find {Path.GetFileName(stockRmo)} for its emissive mask. "
                + "The RMO is saved without one.");
            return null;
        }
        try { return Image.Load<Rgba32>(stockRmo); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            report?.Invoke($"Couldn't read {Path.GetFileName(stockRmo)} for its emissive mask. "
                + $"The RMO is saved without one. ({e.Message})");
            return null;
        }
    }

    private static int Nearest(int destinationIndex, int destinationSize, int sourceSize) =>
        destinationSize == sourceSize ? destinationIndex
            : Math.Clamp((int)((long)destinationIndex * sourceSize / destinationSize), 0, sourceSize - 1);
}
