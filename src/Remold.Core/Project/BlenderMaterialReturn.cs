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
/// the transport. What each returned picture IS — untouched, authored, the neutral — was settled against
/// that target's own outbound map record before this call.</summary>
public static class BlenderMaterialReturn
{
    /// <param name="alphaSource">The RMO an authored RMO's alpha is rebuilt from, by submesh: the picture the
    /// session sent that submesh's RMO slot — the game's map, or the modder's own. Null where nothing was
    /// sent, which ships the mask empty.</param>
    public static IReadOnlyList<SubmeshTextures> Normalize(IReadOnlyList<IncomingMaps> maps,
        string stagingDirectory, Func<int, string?>? alphaSource = null, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var rows = new List<SubmeshTextures>();
        for (int submesh = 0; submesh < maps.Count; submesh++)
        {
            var albedo = Take(maps[submesh].BaseColor, stagingDirectory, submesh, "base", hasFlatMap: false);
            var normal = Take(maps[submesh].Normal, stagingDirectory, submesh, "normal", hasFlatMap: true);
            var rmo = TakeRmo(maps[submesh].Rmo, stagingDirectory, submesh, alphaSource, report);
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
                        blend = Take(texture.Map, stagingDirectory, submesh, texture.ShaderProperty,
                            hasFlatMap: false);
                    continue;
                }
                // Only the fixed normal and RMO slots have a flat map to go to; every other property that
                // lost its picture goes back to the original one.
                var taken = Take(texture.Map, stagingDirectory, submesh, texture.ShaderProperty,
                    hasFlatMap: false);
                if (taken.Origin != SlotOrigin.None)
                    textures.Add(new PropertyTextureBinding
                    {
                        ShaderProperty = texture.ShaderProperty,
                        File = taken.File,
                        Origin = taken.Origin,
                    });
            }
            // A row for every submesh whose slots ANSWERED — an untouched slot answers too, and its row is
            // what keeps the publish from reading its silence as "nothing here, inherit". Whether the row
            // asks for anything is its own question (SubmeshTextures.Asks); a submesh no slot answered on
            // gets no row, and every slot of it inherits.
            if (albedo.Origin == SlotOrigin.None && normal.Origin == SlotOrigin.None
                && rmo.Origin == SlotOrigin.None && blend.Origin == SlotOrigin.None
                && textures.Count == 0) continue;
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

    /// <param name="hasFlatMap">Whether the slot has a flat map to go to when its picture was taken off
    /// (<see cref="MapAnswer.Removed"/>): the fixed normal and RMO slots do, and land flat — what Blender
    /// showed; a slot without one lands on the original picture, the only other thing it can draw.</param>
    private static (string? File, SlotOrigin Origin) Take(ResolvedMap map, string root,
        int submesh, string input, bool hasFlatMap)
    {
        if (map.Answer == MapAnswer.Authored && map.AuthoredPng is not null)
        {
            string path = PathFor(root, submesh, input);
            TextureIngress.Publish(map.AuthoredPng, path);
            return (path, SlotOrigin.Authored);
        }
        if (map.Answer == MapAnswer.Neutral || (map.Answer == MapAnswer.Removed && hasFlatMap))
            return (null, SlotOrigin.ExplicitNeutral);
        // A slot still holding the picture the session sent it asks for nothing, and the publish leaves its
        // binding as it stands — the game's map on a slot that had the game's map, the modder's own asset on
        // a slot that had that. A stock map plugged into a slot it was not sent on never reaches here — another
        // part's, or another material of this one's — because the read before this call classifies it
        // Authored, and it ships as this slot's own map. That is exactly what carries a deliberate texture
        // link, either way.
        if (map.Answer == MapAnswer.Untouched) return (null, SlotOrigin.Untouched);
        return (null, SlotOrigin.None);
    }

    /// <summary>The RMO slot. Only the authored case differs from the others: the shipped map's alpha is
    /// rebuilt from the picture <paramref name="alphaSource"/> names — the RMO the session sent that submesh,
    /// the game's or the modder's own — rather than taken from Blender, and it is the only case that
    /// asks.</summary>
    private static (string? File, SlotOrigin Origin, RmoAlphaAnswer? Alpha) TakeRmo(
        ResolvedMap map, string root, int submesh, Func<int, string?>? alphaSource,
        Action<string>? report)
    {
        if (map.Answer != MapAnswer.Authored || map.AuthoredPng is null)
        {
            var taken = Take(map, root, submesh, "rmo", hasFlatMap: true);
            return (taken.File, taken.Origin, null);
        }
        string path = PathFor(root, submesh, "rmo");
        TextureIngress.Publish(WithAlphaOf(map.AuthoredPng, alphaSource?.Invoke(submesh), report), path);
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

    private static byte[] WithAlphaOf(byte[] authoredPng, string? alphaSource, Action<string>? report)
    {
        using var authored = Image.Load<Rgba32>(authoredPng);
        using var source = LoadAlphaSource(alphaSource, report);
        int width = Math.Max(authored.Width, source?.Width ?? 0);
        int height = Math.Max(authored.Height, source?.Height ?? 0);
        using var result = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var pixel = authored[Nearest(x, width, authored.Width), Nearest(y, height, authored.Height)];
                byte alpha = source is null ? (byte)0
                    : source[Nearest(x, width, source.Width), Nearest(y, height, source.Height)].A;
                result[x, y] = new Rgba32(pixel.R, pixel.G, pixel.B, alpha);
            }
        using var stream = new MemoryStream();
        result.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static Image<Rgba32>? LoadAlphaSource(string? alphaSource, Action<string>? report)
    {
        if (alphaSource is null) return null;
        if (!File.Exists(alphaSource))
        {
            report?.Invoke($"Couldn't find {Path.GetFileName(alphaSource)} for its emissive mask. "
                + "The RMO is saved without one.");
            return null;
        }
        try { return Image.Load<Rgba32>(alphaSource); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            report?.Invoke($"Couldn't read {Path.GetFileName(alphaSource)} for its emissive mask. "
                + $"The RMO is saved without one. ({e.Message})");
            return null;
        }
    }

    private static int Nearest(int destinationIndex, int destinationSize, int sourceSize) =>
        destinationSize == sourceSize ? destinationIndex
            : Math.Clamp((int)((long)destinationIndex * sourceSize / destinationSize), 0, sourceSize - 1);
}
