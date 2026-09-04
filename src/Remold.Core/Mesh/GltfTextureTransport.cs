using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Remold.Core.Textures;

namespace Remold.Core.Mesh;

/// <summary>One workspace picture to carry through Blender under an exact material property. The primitive
/// is null when the installed material position has no drawable carrier; it still rides the inventory.
/// <paramref name="Drawable"/> records whether the material position fires at all (see
/// <see cref="MaterialFold"/>); null is the older record shape and reads as drawable.</summary>
public readonly record struct TextureTransportSource(string Mesh, int MaterialIndex, int? PrimitiveIndex,
    string ShaderProperty, MapKind Kind, string Png, string TextureName, string? Bundle, long PathId,
    bool? Srgb = null, MapOrigin Origin = MapOrigin.Vanilla,
    PreviewMaps.TransportParameters? Parameters = null, int? TexCoord = null, bool? Drawable = null,
    string? Label = null);

/// <summary>One transport source whose preview transform and outbound hash were already computed.</summary>
internal readonly record struct TransformedTextureTransportSource(TextureTransportSource Source,
    byte[] PreviewPng, string OutboundHash);

/// <summary>An authored picture laid over an outbound property binding when a workspace glb is rebuilt. A
/// replacement's picture names the PRIMITIVE it was authored for, since several of its submeshes can fold
/// onto one material; a game-domain picture names the material alone and reaches every primitive drawn
/// under it.</summary>
public readonly record struct TextureTransportOverride(int MaterialIndex, string ShaderProperty, string Png,
    MapKind? Kind = null, int? PrimitiveIndex = null, string? Label = null)
{
    /// <summary>Whether this picture is for the binding at <paramref name="materialIndex"/> /
    /// <paramref name="primitiveIndex"/>, before its property is compared.</summary>
    public bool Covers(int materialIndex, int? primitiveIndex) =>
        PrimitiveIndex is { } primitive ? primitiveIndex == primitive : MaterialIndex == materialIndex;
}

/// <summary>A property-keyed image read out of a glb carrier. <paramref name="Png"/> is null for a
/// hash-only row — a binding Blender sent back as "unchanged" carrying only the content identity the app
/// stamped at open, never the picture itself.</summary>
public readonly record struct TextureTransportImage(string Mesh, int MaterialIndex, int? PrimitiveIndex,
    string ShaderProperty, MapKind Kind, byte[]? Png, string ImageName, string OutboundHash,
    PreviewMaps.TransportStock Stock, bool? Srgb, MapOrigin Origin,
    PreviewMaps.TransportParameters? Parameters, int? TexCoord);

/// <summary>The carrier rows plus image nodes that neither the carrier nor a standard glTF material owns.</summary>
public sealed record TextureTransportRead(IReadOnlyList<TextureTransportImage> Bindings,
    IReadOnlyList<string> UnkeyedImages);

/// <summary>
/// Additive GLB carrier for shader-property identity. The convention is top-level
/// <c>extras.gf2_texture_transport</c>, version 1. Every binding names an image index, the exact property,
/// material/primitive owner, coarse transform semantic, stock resource identity, color space and an optional
/// parameters object. Image bytes live in ordinary glTF <c>images</c>/<c>bufferViews</c>, so the record is a
/// valid static asset even when no honest PBR graph exists for the game shader input.
/// </summary>
public static class GltfTextureTransport
{
    public const string ExtrasKey = "gf2_texture_transport";
    public const int Version = 1;

    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;
    private const uint GlbMagic = 0x46546C67;

    /// <summary>Append every readable source as a glTF image and return the identical sidecar rows. A bad
    /// picture costs only that binding and is named through <paramref name="onUnreadable"/>.</summary>
    public static IReadOnlyList<PreviewMaps.TransportBinding> Write(string glbPath,
        IEnumerable<TextureTransportSource>? sources, Action<string>? onUnreadable = null,
        PreviewBlobMemo? previewMemo = null)
    {
        var requested = sources?.ToList() ?? new List<TextureTransportSource>();
        if (requested.Count == 0) return Array.Empty<PreviewMaps.TransportBinding>();
        previewMemo ??= new PreviewBlobMemo();
        var unreadable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var transformed = new List<TransformedTextureTransportSource>();
        foreach (var source in requested)
        {
            PreviewBlobMemo.Blob preview;
            try { preview = previewMemo.Get(source.Png, source.Kind); }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                if (unreadable.Add(source.Png)) onUnreadable?.Invoke(source.Png);
                continue;
            }
            transformed.Add(new TransformedTextureTransportSource(source, preview.Bytes, preview.Hash));
        }
        return WriteTransformed(glbPath, transformed);
    }

    /// <summary>Append sources whose PNG transforms and hashes were already computed. This is the binary
    /// writer seam shared by the memoized path and byte-identity tests; it never reopens or re-encodes a PNG.</summary>
    internal static IReadOnlyList<PreviewMaps.TransportBinding> WriteTransformed(string glbPath,
        IEnumerable<TransformedTextureTransportSource>? sources)
    {
        var requested = sources?.ToList() ?? new List<TransformedTextureTransportSource>();
        if (requested.Count == 0) return Array.Empty<PreviewMaps.TransportBinding>();

        var parsed = ReadChunks(glbPath);
        var root = JsonNode.Parse(parsed.Json)!.AsObject();
        var images = root["images"] as JsonArray ?? new JsonArray();
        root["images"] = images;
        var views = root["bufferViews"] as JsonArray ?? new JsonArray();
        root["bufferViews"] = views;
        var buffers = root["buffers"] as JsonArray ?? new JsonArray();
        root["buffers"] = buffers;
        if (buffers.Count == 0) buffers.Add(new JsonObject());

        var bin = new List<byte>(parsed.Bin);
        var rows = new List<PreviewMaps.TransportBinding>();
        var carrier = new JsonArray();
        var knownImages = ExistingImageHashes(parsed.Json, parsed.Bin);
        foreach (var transformed in requested)
        {
            var source = transformed.Source;
            byte[] preview = transformed.PreviewPng;

            string hash = transformed.OutboundHash;
            if (!knownImages.TryGetValue(hash, out int image))
            {
                Pad(bin, 4, 0);
                int offset = bin.Count;
                bin.AddRange(preview);
                int view = views.Count;
                views.Add(new JsonObject
                {
                    ["buffer"] = 0,
                    ["byteOffset"] = offset,
                    ["byteLength"] = preview.Length,
                });
                image = images.Count;
                images.Add(new JsonObject
                {
                    ["name"] = CarrierImageName(source),
                    ["mimeType"] = "image/png",
                    ["bufferView"] = view,
                });
                knownImages[hash] = image;
            }

            var stock = new PreviewMaps.TransportStock(source.TextureName, source.Bundle ?? "", source.PathId);
            var row = new PreviewMaps.TransportBinding(source.Mesh, source.MaterialIndex, source.PrimitiveIndex,
                source.ShaderProperty, source.Kind, source.Png, hash, stock, source.Srgb, source.Origin,
                source.Parameters, source.TexCoord, source.Drawable);
            rows.Add(row);
            carrier.Add(CarrierRow(row, image));
        }

        if (rows.Count == 0) return rows;
        buffers[0]!["byteLength"] = bin.Count;
        var extras = root["extras"] as JsonObject ?? new JsonObject();
        root["extras"] = extras;
        extras[ExtrasKey] = new JsonObject
        {
            ["version"] = Version,
            ["bindings"] = carrier,
        };
        WriteChunks(glbPath, root.ToJsonString(), bin.ToArray());
        return rows;
    }

    /// <summary>Read the version-1 carrier. A glb without one returns no bindings and remains readable through
    /// its standard base/normal/ORM channels.</summary>
    public static TextureTransportRead Read(string glbPath)
    {
        var parsed = ReadChunks(glbPath);
        return Read(parsed);
    }

    internal static TextureTransportRead Read(byte[] glbBytes) => Read(ReadChunks(glbBytes));

    private static TextureTransportRead Read(GlbChunks parsed)
    {
        using var doc = JsonDocument.Parse(parsed.Json);
        var root = doc.RootElement;
        var images = root.TryGetProperty("images", out var imageRows) && imageRows.ValueKind == JsonValueKind.Array
            ? imageRows : default;
        var referenced = StandardImageIndices(root);
        var bindings = new List<TextureTransportImage>();
        if (root.TryGetProperty("extras", out var extras)
            && extras.TryGetProperty(ExtrasKey, out var carrier)
            && carrier.TryGetProperty("version", out var version) && version.GetInt32() == Version
            && carrier.TryGetProperty("bindings", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (!TryReadRow(row, root, parsed.Bin, out var binding, out int image)) continue;
                bindings.Add(binding);
                if (image >= 0) referenced.Add(image);
            }
        }

        var unkeyed = new List<string>();
        if (images.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var image in images.EnumerateArray())
            {
                if (!referenced.Contains(index))
                    unkeyed.Add(image.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString() ?? $"image {index}" : $"image {index}");
                index++;
            }
        }
        return new TextureTransportRead(bindings, unkeyed);
    }

    private static Dictionary<string, int> ExistingImageHashes(string json, byte[] bin)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return result;
        for (int index = 0; index < images.GetArrayLength(); index++)
        {
            try { result.TryAdd(PreviewMaps.Hash(ImageBytes(root, bin, index)), index); }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            { /* external or malformed image: it cannot be reused by this binary carrier */ }
        }
        return result;
    }

    private static JsonObject CarrierRow(PreviewMaps.TransportBinding row, int image)
    {
        var owner = new JsonObject
        {
            ["mesh"] = row.Mesh,
            ["material"] = row.MaterialIndex,
        };
        if (row.PrimitiveIndex is { } primitive) owner["primitive"] = primitive;
        var result = new JsonObject
        {
            ["owner"] = owner,
            ["property"] = row.ShaderProperty,
            ["semantic"] = KindName(row.Kind),
            ["image"] = image,
            ["outbound_hash"] = row.OutboundHash,
            ["stock"] = new JsonObject
            {
                ["name"] = row.Stock.Name,
                ["bundle"] = row.Stock.Bundle,
                ["path_id"] = row.Stock.PathId,
            },
            ["origin"] = OriginName(row.Origin),
        };
        if (row.Srgb is { } srgb) result["srgb"] = srgb;
        if (row.TexCoord is { } texCoord) result["texCoord"] = texCoord;
        if (row.Parameters is not null)
            result["parameters"] = JsonSerializer.SerializeToNode(row.Parameters,
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        return result;
    }

    private static bool TryReadRow(JsonElement row, JsonElement root, byte[] bin,
        out TextureTransportImage result, out int imageIndex)
    {
        result = default;
        imageIndex = -1;
        try
        {
            var owner = row.GetProperty("owner");
            string mesh = owner.GetProperty("mesh").GetString() ?? "";
            int material = owner.GetProperty("material").GetInt32();
            int? primitive = owner.TryGetProperty("primitive", out var p) ? p.GetInt32() : null;
            string property = row.GetProperty("property").GetString() ?? "";
            if (property.Length == 0) return false;
            var kind = ParseKind(row.GetProperty("semantic").GetString());
            byte[]? png = null;
            string imageName = property;
            if (row.TryGetProperty("image", out var imageRow) && imageRow.ValueKind == JsonValueKind.Number)
            {
                imageIndex = imageRow.GetInt32();
                png = ImageBytes(root, bin, imageIndex);
                var image = root.GetProperty("images")[imageIndex];
                imageName = image.TryGetProperty("name", out var n) ? n.GetString() ?? $"image {imageIndex}"
                    : $"image {imageIndex}";
            }
            string? outboundHash = row.TryGetProperty("outbound_hash", out var hashRow)
                && hashRow.ValueKind == JsonValueKind.String ? hashRow.GetString() : null;
            if (string.IsNullOrWhiteSpace(outboundHash))
            {
                // A row with neither picture nor identity keys nothing; a byte row without a stamp is a
                // legacy writer's, and its bytes are its identity.
                if (png is null) return false;
                outboundHash = PreviewMaps.Hash(png);
            }
            var stockRow = row.GetProperty("stock");
            var stock = new PreviewMaps.TransportStock(stockRow.GetProperty("name").GetString() ?? "",
                stockRow.GetProperty("bundle").GetString() ?? "", stockRow.GetProperty("path_id").GetInt64());
            bool? srgb = row.TryGetProperty("srgb", out var cs)
                && cs.ValueKind is JsonValueKind.True or JsonValueKind.False ? cs.GetBoolean() : null;
            var origin = row.TryGetProperty("origin", out var o) ? ParseOrigin(o.GetString()) : MapOrigin.Vanilla;
            PreviewMaps.TransportParameters? parameters = row.TryGetProperty("parameters", out var parametersRow)
                ? parametersRow.Deserialize<PreviewMaps.TransportParameters>() : null;
            int? texCoord = row.TryGetProperty("texCoord", out var texCoordRow)
                && texCoordRow.ValueKind == JsonValueKind.Number
                && texCoordRow.TryGetInt32(out int index) && index >= 0 ? index : null;
            result = new TextureTransportImage(mesh, material, primitive, property, kind, png, imageName,
                outboundHash, stock, srgb, origin, parameters, texCoord);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return false;
        }
    }

    private static byte[] ImageBytes(JsonElement root, byte[] bin, int imageIndex)
    {
        var image = root.GetProperty("images")[imageIndex];
        int viewIndex = image.GetProperty("bufferView").GetInt32();
        var view = root.GetProperty("bufferViews")[viewIndex];
        if (view.TryGetProperty("buffer", out var buffer) && buffer.GetInt32() != 0)
            throw new InvalidDataException("The texture transport image is not in GLB buffer 0.");
        int offset = view.TryGetProperty("byteOffset", out var start) ? start.GetInt32() : 0;
        int length = view.GetProperty("byteLength").GetInt32();
        return bin.AsSpan(offset, length).ToArray();
    }

    private static HashSet<int> StandardImageIndices(JsonElement root)
    {
        var result = new HashSet<int>();
        if (!root.TryGetProperty("textures", out var textures) || textures.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var material in materials.EnumerateArray())
        {
            Add(material, "normalTexture");
            Add(material, "occlusionTexture");
            Add(material, "emissiveTexture");
            if (material.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                Add(pbr, "baseColorTexture");
                Add(pbr, "metallicRoughnessTexture");
            }
        }
        return result;

        void Add(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var texture) || !texture.TryGetProperty("index", out var ti)) return;
            int textureIndex = ti.GetInt32();
            if ((uint)textureIndex >= (uint)textures.GetArrayLength()) return;
            var textureRow = textures[textureIndex];
            if (textureRow.TryGetProperty("source", out var source)) result.Add(source.GetInt32());
        }
    }

    /// <summary>What Blender lists the carried picture as: the modder's own picture under its project label,
    /// a stock one under its property and owner.</summary>
    private static string CarrierImageName(TextureTransportSource source)
    {
        if (source.Label is { Length: > 0 } label) return label;
        string property = source.ShaderProperty.TrimStart('_');
        property = new string(property.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        return $"gf2_{property}_m{source.MaterialIndex:D2}"
            + (source.PrimitiveIndex is { } p ? $"_p{p:D2}" : "_surplus");
    }

    private static string KindName(MapKind kind) => kind switch
    {
        MapKind.BaseColor => "baseColor",
        MapKind.Normal => "normal",
        MapKind.Rmo => "rmo",
        MapKind.Blend => "blend",
        _ => "texture",
    };

    private static MapKind ParseKind(string? kind) => kind switch
    {
        "baseColor" => MapKind.BaseColor,
        "normal" => MapKind.Normal,
        "rmo" => MapKind.Rmo,
        "blend" => MapKind.Blend,
        _ => MapKind.Texture,
    };

    private static string OriginName(MapOrigin origin) => origin switch
    {
        MapOrigin.Authored => "authored",
        MapOrigin.Neutral => "neutral",
        MapOrigin.None => "none",
        _ => "vanilla",
    };

    private static MapOrigin ParseOrigin(string? origin) => origin switch
    {
        "authored" => MapOrigin.Authored,
        "neutral" => MapOrigin.Neutral,
        "none" => MapOrigin.None,
        _ => MapOrigin.Vanilla,
    };

    private readonly record struct GlbChunks(string Json, byte[] Bin);

    private static GlbChunks ReadChunks(string path)
    {
        return ReadChunks(File.ReadAllBytes(path));
    }

    private static GlbChunks ReadChunks(byte[] bytes)
    {
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != GlbMagic
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2)
            throw new InvalidDataException("The texture transport requires a glTF 2.0 binary file.");
        string? json = null;
        byte[] bin = Array.Empty<byte>();
        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            offset += 8;
            if (length < 0 || offset + length > bytes.Length) throw new InvalidDataException("The GLB chunk is truncated.");
            if (type == JsonChunk) json = Encoding.UTF8.GetString(bytes, offset, length).TrimEnd(' ', '\0');
            else if (type == BinChunk) bin = bytes.AsSpan(offset, length).ToArray();
            offset += length;
        }
        if (json is null) throw new InvalidDataException("The GLB has no JSON chunk.");
        return new GlbChunks(json, bin);
    }

    private static void WriteChunks(string path, string json, byte[] bin)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json).ToList();
        Pad(jsonBytes, 4, 0x20);
        var binBytes = bin.ToList();
        Pad(binBytes, 4, 0);
        int total = checked(12 + 8 + jsonBytes.Count + (binBytes.Count == 0 ? 0 : 8 + binBytes.Count));
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(GlbMagic);
        writer.Write(2u);
        writer.Write((uint)total);
        writer.Write((uint)jsonBytes.Count);
        writer.Write(JsonChunk);
        writer.Write(jsonBytes.ToArray());
        if (binBytes.Count > 0)
        {
            writer.Write((uint)binBytes.Count);
            writer.Write(BinChunk);
            writer.Write(binBytes.ToArray());
        }
    }

    private static void Pad(List<byte> bytes, int alignment, byte value)
    {
        while (bytes.Count % alignment != 0) bytes.Add(value);
    }
}
