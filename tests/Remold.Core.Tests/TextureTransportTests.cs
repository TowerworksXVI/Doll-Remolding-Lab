using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Remold.App.ViewModels;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Textures;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

public class TextureTransportTests
{
    private static UnityMesh Patch(string name = "veil") => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new() { ["Vertex"] = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 } },
        Dims = new() { ["Vertex"] = 3 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };

    private static string Png(string path, byte seed)
    {
        using var image = new Image<Rgba32>(4, 4, new Rgba32(seed, (byte)(seed + 1), (byte)(seed + 2), 255));
        image.SaveAsPng(path);
        return path;
    }

    private static TextureTransportSource Source(string property, MapKind kind, string png,
        int? texCoord = null) => new("veil", 0, 0, property, kind, png, "shared", "bundle", 71, true,
            TexCoord: texCoord);

    [Fact]
    public void Memoized_transport_is_byte_identical_to_independently_pretransformed_input()
    {
        using var game = new TempGame();
        string png = Png(game.At("source.png"), 37);
        string seed = game.At("seed.glb");
        string expected = game.At("expected.glb");
        string actual = game.At("actual.glb");
        MeshGltf.ExportGlb(Patch(), seed);
        File.Copy(seed, expected);
        File.Copy(seed, actual);
        var sources = new[]
        {
            new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, png,
                "source", "bundle", 71, true),
            new TextureTransportSource("veil", 1, null, "_DetailNormalRM", MapKind.Normal, png,
                "source", "bundle", 71, false),
            new TextureTransportSource("veil", 2, null, "_MaskTex", MapKind.Rmo, png,
                "source", "bundle", 71, false),
        };
        var independentlyTransformed = sources.Select(source =>
        {
            byte[] preview = PreviewMaps.ToPreview(source.Png, source.Kind);
            return new TransformedTextureTransportSource(source, preview, PreviewMaps.Hash(preview));
        }).ToArray();

        var expectedRows = GltfTextureTransport.WriteTransformed(expected, independentlyTransformed);
        var actualRows = GltfTextureTransport.Write(actual, sources, previewMemo: new PreviewBlobMemo());

        Assert.Equal(expectedRows, actualRows);
        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
        var parsed = MeshGltf.ParsedGlb.Open(actual);
        File.Copy(seed, actual, overwrite: true);
        Assert.Equal(new[] { "_BaseMap", "_DetailNormalRM", "_MaskTex" },
            parsed.Transport.Bindings.Select(binding => binding.ShaderProperty));
    }

    /// <summary>The memo's ceiling costs time, never correctness: an evicted transform recomputes to the
    /// same bytes the independent path produces. Expectations come from PreviewMaps.ToPreview directly,
    /// never from a second memo.</summary>
    [Fact]
    public void Preview_memo_evicts_beyond_its_ceiling_and_recomputes_evicted_blobs_identically()
    {
        using var game = new TempGame();
        string a = Png(game.At("memo-a.png"), 10);
        string b = Png(game.At("memo-b.png"), 90);
        byte[] expectedA = PreviewMaps.ToPreview(a, MapKind.BaseColor);
        byte[] expectedB = PreviewMaps.ToPreview(b, MapKind.BaseColor);

        var tiny = new PreviewBlobMemo(maxRetainedBytes: 1);   // room for at most the newest blob
        var firstA = tiny.Get(a, MapKind.BaseColor);
        Assert.Equal(expectedA, firstA.Bytes);
        var firstB = tiny.Get(b, MapKind.BaseColor);           // evicts a's blob
        Assert.Equal(expectedB, firstB.Bytes);
        Assert.True(tiny.RetainedBytes <= firstB.Bytes.Length,
            "the memo retained more than the ceiling allows");

        var again = tiny.Get(a, MapKind.BaseColor);            // recomputed, not lost
        Assert.Equal(expectedA, again.Bytes);
        Assert.Equal(firstA.Hash, again.Hash);
    }

    [Fact]
    public void Carrier_keeps_two_properties_on_one_resource_and_surplus_owner()
    {
        using var game = new TempGame();
        string png = Png(game.At("shared.png"), 20);
        string glb = game.At("carrier.glb");
        var parameters = new PreviewMaps.TransportParameters
        {
            Floats = new() { ["future"] = 2f },
            Keywords = new() { "FUTURE" },
        };
        MeshGltf.ExportGlb(Patch(), glb, textureTransport: new[]
        {
            new TextureTransportSource("veil", 6, null, "_MaskTex", MapKind.Texture, png,
                "shared", "bundle", 71, false, Parameters: parameters),
            new TextureTransportSource("veil", 6, null, "_TurbulenceTex", MapKind.Texture, png,
                "shared", "bundle", 71, false, Parameters: parameters, TexCoord: 1),
        });

        var inside = GltfTextureTransport.Read(glb).Bindings;
        var beside = PreviewMaps.ReadTransportBindings(glb);

        Assert.Equal(new[] { "_MaskTex", "_TurbulenceTex" }, inside.Select(row => row.ShaderProperty));
        Assert.Equal(new int?[] { null, 1 }, inside.Select(row => row.TexCoord));
        Assert.All(inside, row =>
        {
            Assert.Null(row.PrimitiveIndex);
            Assert.Equal(6, row.MaterialIndex);
            Assert.Equal(71, row.Stock.PathId);
            Assert.False(row.Srgb);
            Assert.Equal("FUTURE", Assert.Single(row.Parameters!.Keywords!));
        });
        Assert.Equal(inside.Select(row => row.ShaderProperty), beside.Select(row => row.ShaderProperty));
        Assert.Equal(inside.Select(row => row.TexCoord), beside.Select(row => row.TexCoord));
    }

    [Fact]
    public void Rigged_build_spec_is_pinned_to_the_texture_transport_carrier_shape()
    {
        using var game = new TempGame();
        string glb = game.At("carrier-shape.glb");
        var parameters = new PreviewMaps.TransportParameters
        {
            Floats = new() { ["future"] = 2f },
            Keywords = new() { "FUTURE" },
        };
        MeshGltf.ExportGlb(Patch(), glb, textureTransport: new[]
        {
            new TextureTransportSource("veil", 6, 0, "_BlendTex", MapKind.Blend,
                Png(game.At("effect.png"), 20), "effect", "bundle", 71, true,
                MapOrigin.Authored, parameters, TexCoord: 1),
        });

        using var json = ReadGlbJson(glb);
        var carrier = json.RootElement.GetProperty("extras")
            .GetProperty(GltfTextureTransport.ExtrasKey);
        var binding = carrier.GetProperty("bindings").EnumerateArray().Single();

        Assert.Equal("rigged-build-spec-v5", AssetExporter.RiggedBuildSpec);
        Assert.Equal(1, carrier.GetProperty("version").GetInt32());
        Assert.Equal(new[]
        {
            "image", "origin", "outbound_hash", "owner", "parameters", "property", "semantic",
            "srgb", "stock", "texCoord",
        }, binding.EnumerateObject().Select(property => property.Name).OrderBy(name => name,
            StringComparer.Ordinal));
        Assert.Equal(new[] { "material", "mesh", "primitive" }, binding.GetProperty("owner")
            .EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(new[] { "bundle", "name", "path_id" }, binding.GetProperty("stock")
            .EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Non_typed_optional_tokens_do_not_discard_their_carrier_bindings()
    {
        using var game = new TempGame();
        string png = Png(game.At("stock.png"), 20);
        string glb = game.At("malformed-optionals.glb");
        MeshGltf.ExportGlb(Patch(), glb, textureTransport: new[]
        {
            new TextureTransportSource("veil", 0, 0, "_BadTexCoord", MapKind.Texture, png,
                "stock", "bundle", 71, true, TexCoord: 1),
            new TextureTransportSource("veil", 0, 0, "_BadSrgb", MapKind.Texture, png,
                "stock", "bundle", 71, true, TexCoord: 1),
        });
        RewriteGlbJson(glb, root =>
        {
            var bindings = root["extras"]![GltfTextureTransport.ExtrasKey]!["bindings"]!.AsArray();
            bindings[0]!["texCoord"] = "1";
            bindings[1]!["srgb"] = "true";
        });

        var bindings = GltfTextureTransport.Read(glb).Bindings;

        Assert.Equal(2, bindings.Count);
        var badTexCoord = Assert.Single(bindings, row => row.ShaderProperty == "_BadTexCoord");
        Assert.Null(badTexCoord.TexCoord);
        Assert.True(badTexCoord.Srgb);
        var badSrgb = Assert.Single(bindings, row => row.ShaderProperty == "_BadSrgb");
        Assert.Equal(1, badSrgb.TexCoord);
        Assert.Null(badSrgb.Srgb);
    }

    [Fact]
    public void Exact_property_join_keeps_effect_and_generic_semantics_apart()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string painted = Png(game.At("painted.png"), 90);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            Source("_BlendTex", MapKind.Blend, stock, 1),
            Source("_MaskTex", MapKind.Texture, stock),
        });
        // The returned row lies about the mask semantic. The outbound exact-property row remains authority.
        MeshGltf.ExportGlb(Patch(), returned, textureTransport: new[]
        {
            Source("_BlendTex", MapKind.Blend, painted, 1),
            Source("_MaskTex", MapKind.Blend, stock, 1),
            Source("_NotInSession", MapKind.Texture, painted),
        });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);
        var bindings = Assert.Single(maps).Textures!;
        var effect = Assert.Single(bindings, row => row.ShaderProperty == "_BlendTex");
        var mask = Assert.Single(bindings, row => row.ShaderProperty == "_MaskTex");

        Assert.Equal(MapKind.Blend, effect.Kind);
        Assert.Equal(MapOrigin.Authored, effect.Map.Origin);
        Assert.Equal(MapKind.Texture, mask.Kind);
        Assert.Equal(MapOrigin.Vanilla, mask.Map.Origin);
        Assert.Contains(notes, note => note.Contains("Not In Session", System.StringComparison.Ordinal)
            && note.Contains("wasn't in the file this session opened", System.StringComparison.Ordinal));

        var normalized = BlenderMaterialReturn.Normalize(maps, game.At("normalized"));
        var row = Assert.Single(normalized);
        Assert.NotNull(row.Blend);
        Assert.Equal(SlotOrigin.Authored, row.BlendAsk);
        Assert.Null(row.Textures);

        TextureTransportSource Source(string property, MapKind kind, string png, int? texCoord = null) =>
            new("veil", 0, 0, property, kind, png, "shared", "bundle", 71, true,
                TexCoord: texCoord);
    }

    [Fact]
    public void A_matching_hash_only_row_is_unchanged_without_returned_image_bytes()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            Source("_BaseMap", MapKind.BaseColor, stock),
        });
        MeshGltf.ExportGlb(Patch(), returned);
        var outbound = Assert.Single(PreviewMaps.ReadTransportBindings(opened));
        WriteHashOnlyRow(returned, outbound);

        var carried = Assert.Single(GltfTextureTransport.Read(returned).Bindings);
        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened);

        Assert.Null(carried.Png);
        Assert.Equal(outbound.OutboundHash, carried.OutboundHash);
        var map = Assert.Single(maps).BaseColor;
        Assert.Equal(MapOrigin.Vanilla, map.Origin);
        Assert.Equal(Path.GetFullPath(stock), map.StockPng);
        Assert.Empty(BlenderMaterialReturn.Normalize(maps, game.At("staging")));
    }

    [Fact]
    public void Hash_only_and_byte_returns_reexport_the_same_stock_map_and_outbound_row()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string opened = game.At("opened.glb");
        string byteReturned = game.At("byte-returned.glb");
        string hashReturned = game.At("hash-returned.glb");
        string byteResplit = game.At("byte-resplit.glb");
        string hashResplit = game.At("hash-resplit.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            Source("_BaseMap", MapKind.BaseColor, stock),
        });
        var changed = Patch();
        changed.Channels["Vertex"][0] = 0.5f;
        MeshGltf.ExportGlb(changed, byteReturned);
        GltfTextureTransport.Write(byteReturned, new[] { Source("_BaseMap", MapKind.BaseColor, stock) });
        MeshGltf.ExportGlb(changed, hashReturned);
        var outbound = Assert.Single(PreviewMaps.ReadTransportBindings(opened));
        WriteHashOnlyRow(hashReturned, outbound);

        MeshGltf.ReexportPartGlb(byteReturned, "veil", byteResplit, recordGlb: opened);
        MeshGltf.ReexportPartGlb(hashReturned, "veil", hashResplit, recordGlb: opened);

        var byteRow = Assert.Single(PreviewMaps.ReadTransportBindings(byteResplit));
        var hashRow = Assert.Single(PreviewMaps.ReadTransportBindings(hashResplit));
        Assert.Equal(byteRow, hashRow);
        Assert.Equal(Path.GetFullPath(stock), hashRow.Source);
        var byteImage = Assert.Single(GltfTextureTransport.Read(byteResplit).Bindings).Png;
        var hashImage = Assert.Single(GltfTextureTransport.Read(hashResplit).Bindings).Png;
        Assert.NotNull(byteImage);
        Assert.Equal(byteImage, hashImage);
    }

    [Fact]
    public void A_hash_only_row_without_a_map_record_is_refused_instead_of_losing_its_source()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            Source("_BaseMap", MapKind.BaseColor, stock),
        });
        var outbound = Assert.Single(PreviewMaps.ReadTransportBindings(opened));
        File.Delete(PreviewMaps.SidecarPath(opened));
        MeshGltf.ExportGlb(Patch(), returned);
        WriteHashOnlyRow(returned, outbound);

        var refusal = Assert.Throws<AuthoredRefusalException>(() =>
            MeshGltf.ReadSubmeshMaps(returned, "veil", opened));

        Assert.Equal("The Base color on veil came back marked unchanged, but it isn't the picture this "
            + "session sent. Open the part again from the Lab and send it once more", refusal.Message);
    }

    [Fact]
    public void A_hash_only_row_with_no_readable_outbound_record_is_refused()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string source = game.At("source.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), source, textureTransport: new[]
        {
            Source("_BaseMap", MapKind.BaseColor, stock),
        });
        var outbound = Assert.Single(PreviewMaps.ReadTransportBindings(source));
        MeshGltf.ExportGlb(Patch(), returned);
        WriteHashOnlyRow(returned, outbound);

        var refusal = Assert.Throws<AuthoredRefusalException>(() =>
            MeshGltf.ReadSubmeshMaps(returned, "veil", game.At("missing-record.glb")));

        Assert.Equal("The Base color on veil came back marked unchanged, but it isn't the picture this "
            + "session sent. Open the part again from the Lab and send it once more", refusal.Message);
    }

    [Fact]
    public void A_mismatched_hash_only_row_is_refused_instead_of_guessing_the_picture()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            Source("_BaseMap", MapKind.BaseColor, stock),
        });
        MeshGltf.ExportGlb(Patch(), returned);
        var outbound = Assert.Single(PreviewMaps.ReadTransportBindings(opened));
        WriteHashOnlyRow(returned, outbound with { OutboundHash = new string('b', 64) });

        var refusal = Assert.Throws<AuthoredRefusalException>(() =>
            MeshGltf.ReadSubmeshMaps(returned, "veil", opened));

        Assert.Equal("The Base color on veil came back marked unchanged, but it isn't the picture this "
            + "session sent. Open the part again from the Lab and send it once more", refusal.Message);
    }

    [Fact]
    public void A_siblings_hash_only_row_does_not_move_a_byte_carrying_part_off_its_legacy_read()
    {
        // The record-glb fallback is scoped to the part being read: with no sidecar record, a part whose
        // rows all carry bytes keeps the legacy standard-channel classification even when a sibling part
        // of the same return sent hash-only rows.
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            Source("_BaseMap", MapKind.BaseColor, stock),
        });
        var outbound = Assert.Single(PreviewMaps.ReadTransportBindings(opened));
        File.Delete(PreviewMaps.SidecarPath(opened));
        MeshGltf.ExportGlb(Patch(), returned,
            perSubmesh: new[] { ((string?)stock, (string?)null, (string?)null) });
        WriteHashOnlyRow(returned, outbound with { Mesh = "sibling" });

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened);

        // With no record at all, the legacy read classifies the returned picture as authored — the same
        // answer this fixture gets before hash-only rows existed. The pin is that the sibling's marker
        // neither refuses this part nor invents exact rows for it.
        Assert.Equal(MapOrigin.Authored, Assert.Single(maps).BaseColor.Origin);
        Assert.Null(Assert.Single(maps).Textures);
    }

    [Fact]
    public void A_hash_only_row_for_a_slot_the_open_never_sent_is_skipped_silently()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            Source("_BaseMap", MapKind.BaseColor, stock),
        });
        MeshGltf.ExportGlb(Patch(), returned);
        var outbound = Assert.Single(PreviewMaps.ReadTransportBindings(opened));
        WriteHashOnlyRow(returned, outbound with { ShaderProperty = "_MaskTex" });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);

        Assert.Single(maps);
        Assert.Empty(notes);
    }

    [Fact]
    public void An_untouched_authored_outbound_picture_is_a_baseline_not_a_new_ask()
    {
        using var game = new TempGame();
        string authored = Png(game.At("authored.png"), 20);
        string changed = Png(game.At("changed.png"), 90);
        var binding = new PreviewMaps.TransportBinding("veil", 0, 0, "_BaseMap",
            MapKind.BaseColor, authored, PreviewMaps.Hash(File.ReadAllBytes(authored)),
            new PreviewMaps.TransportStock("stock", "bundle", 71), Origin: MapOrigin.Authored);

        var untouched = PreviewMaps.ResolveTransport(File.ReadAllBytes(authored), binding);
        var repainted = PreviewMaps.ResolveTransport(File.ReadAllBytes(changed), binding);

        Assert.Equal(MapOrigin.Vanilla, untouched.Origin);
        Assert.Equal(Path.GetFullPath(authored), untouched.StockPng);
        Assert.Equal(MapOrigin.Authored, repainted.Origin);
        Assert.Empty(BlenderMaterialReturn.Normalize(new[]
        {
            new IncomingMaps(untouched, default, default, "material"),
        }, game.At("normalized")));
    }

    [Fact]
    public void Inventory_projection_keeps_a_surplus_material_and_every_census_property()
    {
        using var game = new TempGame();
        string texDir = game.At("textures");
        Directory.CreateDirectory(texDir);
        string[] properties =
        {
            "_GlitterMap", "_SMO", "_DetailAlbedo", "_DetailNormalRM", "_DetailMask",
            "_MatcapTex", "_MatcapNormalTex", "_Specularmap", "_MaskTex", "_TurbulenceTex",
            "_DissolveTex", "_VertexAnimTex",
        };
        var all = new List<TexTarget>();
        var bound = new List<BoundTexture>();
        for (int index = 0; index < properties.Length; index++)
        {
            string name = "texture" + index;
            var texture = new TexTarget(name, "bundle", false, false, "renderer", PathId: index + 1);
            all.Add(texture);
            bound.Add(new BoundTexture(properties[index], texture));
            Png(Path.Combine(texDir, TextureExport.BundleScopedName("bundle", name, "subject")), (byte)(20 + index));
        }
        var part = new PartTextures(all, new[] { new SubmeshMaps(null, null) }, Materials: new[]
        {
            new MaterialTextureBindings(0, "drawn", System.Array.Empty<BoundTexture>()),
            new MaterialTextureBindings(1, "surplus", bound),
        });

        var rows = AssetExporter.ResolveTextureTransport(texDir, "subject", "veil", part);

        Assert.Equal(properties, rows.Select(row => row.ShaderProperty));
        Assert.All(rows, row => Assert.Null(row.PrimitiveIndex));
    }

    [Fact]
    public void Inventory_stamps_only_evidenced_texture_coordinates()
    {
        using var game = new TempGame();
        string texDir = game.At("textures");
        Directory.CreateDirectory(texDir);
        var all = new[]
        {
            new TexTarget("base", "bundle", true, false, "renderer", PathId: 1),
            new TexTarget("normal", "bundle", false, true, "renderer", PathId: 2),
            new TexTarget("rmo", "bundle", false, false, "renderer", IsRmo: true, PathId: 3),
            new TexTarget("effect", "bundle", false, false, "renderer", PathId: 4, IsBlend: true),
            new TexTarget("mask", "bundle", false, false, "renderer", PathId: 5),
        };
        for (int index = 0; index < all.Length; index++)
            Png(Path.Combine(texDir, TextureExport.BundleScopedName("bundle", all[index].Name, "subject")),
                (byte)(20 + index));
        var material = new MaterialTextureBindings(0, "material", new[]
        {
            new BoundTexture("_BaseMap", all[0]),
            new BoundTexture("_BumpMap", all[1]),
            new BoundTexture("_RMOTex", all[2]),
            new BoundTexture("_BlendTex", all[3]),
            new BoundTexture("_MaskTex", all[4]),
        });
        var part = new PartTextures(all, new[] { new SubmeshMaps(null, null) },
            Materials: new[] { material });

        var rows = AssetExporter.ResolveTextureTransport(texDir, "subject", "veil", part);

        Assert.Equal(new (string Property, int? TexCoord)[]
        {
            ("_BaseMap", 0), ("_BumpMap", 0), ("_RMOTex", 0), ("_BlendTex", 1),
            ("_MaskTex", null),
        }, rows.Select(row => (row.ShaderProperty, row.TexCoord)));
    }

    [Fact]
    public void A_ramp_name_collision_exports_the_non_ramp_binding_without_a_false_missing_note()
    {
        using var game = new TempGame();
        string texDir = game.At("textures");
        Directory.CreateDirectory(texDir);
        var ramp = new TexTarget("shared", "bundle", false, false, "renderer", PathId: 71,
            IsRamp: true);
        var ordinary = new TexTarget("shared", "bundle", false, false, "renderer", PathId: 72);
        var materials = new[]
        {
            new MaterialTextureBindings(0, "material", new[]
            {
                new BoundTexture("_RampMap", ramp),
                new BoundTexture("_MaskTex", ordinary),
            }),
        };
        var inventory = PartTextureResolver.ExportInventory(materials);
        var part = new PartTextures(inventory, new[] { new SubmeshMaps(null, null) }, Materials: materials);
        Png(Path.Combine(texDir, TextureExport.BundleScopedName("bundle", "shared", "subject", 72)), 90);
        var missed = new List<string>();

        var rows = AssetExporter.ResolveTextureTransport(texDir, "subject", "veil", part, missed: missed);

        var row = Assert.Single(rows);
        Assert.Equal("_MaskTex", row.ShaderProperty);
        Assert.Equal(72, row.PathId);
        Assert.Empty(missed);
        Assert.Equal(2, inventory.Count);
        Assert.True(inventory.Single(texture => texture.PathId == 71).IsRamp);
        Assert.False(inventory.Single(texture => texture.PathId == 72).IsRamp);
    }

    [Fact]
    public void Same_named_distinct_resources_keep_their_own_pixels_per_binding()
    {
        using var game = new TempGame();
        string texDir = game.At("textures");
        Directory.CreateDirectory(texDir);
        var first = new TexTarget("shared", "bundle", false, false, "renderer", PathId: 71);
        var second = new TexTarget("shared", "bundle", false, false, "renderer", PathId: 72);
        var materials = new[]
        {
            new MaterialTextureBindings(0, "material", new[]
            {
                new BoundTexture("_MaskTex", first),
                new BoundTexture("_TurbulenceTex", second),
            }),
        };
        var inventory = PartTextureResolver.ExportInventory(materials);
        var part = new PartTextures(inventory, new[] { new SubmeshMaps(null, null) }, Materials: materials);
        Png(Path.Combine(texDir, TextureExport.BundleScopedName("bundle", "shared", "subject", 71)), 30);
        Png(Path.Combine(texDir, TextureExport.BundleScopedName("bundle", "shared", "subject", 72)), 180);
        var transport = AssetExporter.ResolveTextureTransport(texDir, "subject", "veil", part);
        string glb = game.At("same-name.glb");

        MeshGltf.ExportGlb(Patch(), glb, textureTransport: transport);
        var carried = GltfTextureTransport.Read(glb).Bindings;

        Assert.Equal(2, inventory.Count);
        Assert.Equal(new Rgba32(30, 31, 32, 255), FirstPixel(
            Assert.Single(carried, row => row.ShaderProperty == "_MaskTex").Png));
        Assert.Equal(new Rgba32(180, 181, 182, 255), FirstPixel(
            Assert.Single(carried, row => row.ShaderProperty == "_TurbulenceTex").Png));
    }

    [Fact]
    public void Legacy_standard_channels_still_read_without_a_property_carrier()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string glb = game.At("legacy.glb");
        MeshGltf.ExportGlb(Patch(), glb,
            perSubmesh: new[] { ((string?)stock, (string?)null, (string?)null) });

        var maps = MeshGltf.ReadSubmeshMaps(glb);

        Assert.Equal(MapOrigin.Vanilla, Assert.Single(maps).BaseColor.Origin);
        Assert.Null(Assert.Single(maps).Textures);
    }

    // ------------------------------------------------ standard channels in a keyed session
    //
    // Production always opens a part with exact-property transport (AssetExporter.ResolveTextureTransport),
    // so every return is a keyed session. A material built by hand in Blender, or a replacement mesh that
    // arrived textured, carries no tagged node: its pictures sit on Blender's own base colour / normal / ORM
    // channels, which the glTF exporter writes as the standard material channels and nothing else. The
    // returns below are built with perSubmesh only — standard channels, no carrier — over a keyed open.

    private static TextureTransportSource[] BaseAndNormalSlots(string stockBase, string stockNormal,
        string mesh = "veil") => new[]
    {
        new TextureTransportSource(mesh, 0, 0, "_BaseMap", MapKind.BaseColor, stockBase, "base", "bundle", 71, true),
        new TextureTransportSource(mesh, 0, 0, "_NormalMap", MapKind.Normal, stockNormal, "normal", "bundle", 72,
            false),
    };

    [Fact]
    public void A_hand_built_material_returns_its_standard_channel_pictures_as_the_slots_own_edits()
    {
        using var game = new TempGame();
        string stockBase = Png(game.At("stock-base.png"), 20);
        string stockNormal = Png(game.At("stock-normal.png"), 128);
        string paintedBase = Png(game.At("painted-base.png"), 90);
        string paintedNormal = Png(game.At("painted-normal.png"), 140);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: BaseAndNormalSlots(stockBase, stockNormal));
        MeshGltf.ExportGlb(Patch(), returned,
            perSubmesh: new[] { ((string?)paintedBase, (string?)paintedNormal, (string?)null) });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);

        var incoming = Assert.Single(maps);
        Assert.Empty(notes);
        Assert.Equal(MapOrigin.Authored, incoming.BaseColor.Origin);
        Assert.Equal(MapOrigin.Authored, incoming.Normal.Origin);
        var textures = Assert.IsAssignableFrom<IReadOnlyList<IncomingTexture>>(incoming.Textures);
        var baseSlot = Assert.Single(textures, t => t.ShaderProperty == "_BaseMap");
        Assert.Equal(MapOrigin.Authored, baseSlot.Map.Origin);
        Assert.Equal(new Rgba32(90, 91, 92, 255), FirstPixel(baseSlot.Map.AuthoredPng!));
        Assert.Equal(MapOrigin.Authored, Assert.Single(textures, t => t.ShaderProperty == "_NormalMap").Map.Origin);
        var row = Assert.Single(BlenderMaterialReturn.Normalize(maps, game.At("staging")));
        Assert.Equal(SlotOrigin.Authored, row.AlbedoAsk);
        Assert.Equal(SlotOrigin.Authored, row.NormalAsk);
        Assert.Equal(new Rgba32(90, 91, 92, 255), FirstPixel(row.Albedo!));
    }

    [Fact]
    public void A_standard_channel_still_showing_what_the_session_sent_is_not_an_ask()
    {
        using var game = new TempGame();
        string stockBase = Png(game.At("stock-base.png"), 20);
        string stockNormal = Png(game.At("stock-normal.png"), 128);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: BaseAndNormalSlots(stockBase, stockNormal));
        MeshGltf.ExportGlb(Patch(), returned,
            perSubmesh: new[] { ((string?)stockBase, (string?)stockNormal, (string?)null) });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);

        Assert.Empty(notes);
        var incoming = Assert.Single(maps);
        Assert.Equal(MapOrigin.Vanilla, incoming.BaseColor.Origin);
        Assert.Equal(MapOrigin.Vanilla, incoming.Normal.Origin);
        Assert.Empty(BlenderMaterialReturn.Normalize(maps, game.At("staging")));
    }

    [Fact]
    public void Another_slots_stock_picture_on_a_standard_channel_is_a_link_and_ships_for_that_slot()
    {
        using var game = new TempGame();
        string stockA = Png(game.At("stock-a.png"), 10);
        string stockB = Png(game.At("stock-b.png"), 80);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(TwoSubmeshPatch(), opened, textureTransport: new[]
        {
            new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, stockA, "a", "bundle", 71, true),
            new TextureTransportSource("veil", 1, 1, "_BaseMap", MapKind.BaseColor, stockB, "b", "bundle", 72, true),
        });
        MeshGltf.ExportGlb(TwoSubmeshPatch(), returned, perSubmesh: new[]
        {
            ((string?)stockB, (string?)null, (string?)null),
            ((string?)stockB, (string?)null, (string?)null),
        });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);

        Assert.Empty(notes);
        Assert.Equal(2, maps.Count);
        Assert.Equal(MapOrigin.Authored, maps[0].BaseColor.Origin);
        Assert.Equal(new Rgba32(80, 81, 82, 255), FirstPixel(maps[0].BaseColor.AuthoredPng!));
        Assert.Equal(MapOrigin.Vanilla, maps[1].BaseColor.Origin);
        var row = Assert.Single(BlenderMaterialReturn.Normalize(maps, game.At("staging")));
        Assert.Equal(0, row.Submesh);
    }

    [Fact]
    public void A_tagged_node_that_came_back_edited_keeps_its_slot_and_names_the_other_picture()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string onTaggedNode = Png(game.At("on-tagged-node.png"), 90);
        string onChannel = Png(game.At("on-channel.png"), 160);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, stock, "base", "bundle", 71, true),
        });
        // the tagged node carries one edited picture; Blender's base colour channel another
        MeshGltf.ExportGlb(Patch(), returned,
            perSubmesh: new[] { ((string?)onChannel, (string?)null, (string?)null) },
            textureTransport: new[]
            {
                new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, onTaggedNode, "base",
                    "bundle", 71, true),
            });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);

        var incoming = Assert.Single(maps);
        Assert.Equal(MapOrigin.Authored, incoming.BaseColor.Origin);
        Assert.Equal(new Rgba32(90, 91, 92, 255), FirstPixel(incoming.BaseColor.AuthoredPng!));
        string note = Assert.Single(notes);
        Assert.Contains("on-channel_base", note);
        Assert.Contains("already has an edited Base color image", note);
    }

    [Fact]
    public void A_tagged_node_and_the_channel_showing_the_same_edited_picture_say_nothing()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string painted = Png(game.At("painted.png"), 90);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, stock, "base", "bundle", 71, true),
        });
        MeshGltf.ExportGlb(Patch(), returned,
            perSubmesh: new[] { ((string?)painted, (string?)null, (string?)null) },
            textureTransport: new[]
            {
                new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, painted, "base",
                    "bundle", 71, true),
            });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);

        Assert.Empty(notes);
        Assert.Equal(MapOrigin.Authored, Assert.Single(maps).BaseColor.Origin);
    }

    [Fact]
    public void A_picture_on_a_channel_the_part_has_no_slot_for_is_ignored_by_name()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string paintedNormal = Png(game.At("painted-normal.png"), 140);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, stock, "base", "bundle", 71, true),
        });
        MeshGltf.ExportGlb(Patch(), returned,
            perSubmesh: new[] { ((string?)null, (string?)paintedNormal, (string?)null) });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);

        string note = Assert.Single(notes);
        Assert.Contains("painted-normal_nrm", note);
        Assert.Contains("has no Normal map slot", note);
        var incoming = Assert.Single(maps);
        Assert.Equal(MapOrigin.None, incoming.Normal.Origin);
        Assert.Equal(MapOrigin.None, incoming.BaseColor.Origin);
        Assert.Empty(BlenderMaterialReturn.Normalize(maps, game.At("staging")));
    }

    [Fact]
    public void A_part_the_session_never_opened_has_its_pictures_ignored_by_name()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string painted = Png(game.At("painted.png"), 90);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch("bound"), opened, textureTransport: new[]
        {
            new TextureTransportSource("bound", 0, 0, "_BaseMap", MapKind.BaseColor, stock,
                "base", "bundle", 71, true),
        });
        MeshGltf.ExportGlb(Patch("bindingless"), returned,
            perSubmesh: new[] { ((string?)painted, (string?)null, (string?)null) });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "bindingless", opened, notes.Add);

        string note = Assert.Single(notes);
        Assert.Contains("painted_base", note);
        Assert.Contains("has no Base color slot", note);
        Assert.Equal(MapOrigin.None, Assert.Single(maps).BaseColor.Origin);
        Assert.Null(Assert.Single(maps).Textures);
    }

    /// <summary>Blender's exporter composes the ORM channel afresh from the Separate Color links, so the
    /// channel never carries the session's own bytes even when the RMO node was never touched. A returned
    /// tagged RMO node is the whole answer for its slot; the channel beside it says nothing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_returned_tagged_rmo_node_is_the_whole_answer_whatever_the_orm_channel_carries(bool edited)
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock-rmo.png"), 20);
        string painted = Png(game.At("painted-rmo.png"), 90);
        string composed = Png(game.At("composed-orm.png"), 160);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            new TextureTransportSource("veil", 0, 0, "_RMOMap", MapKind.Rmo, stock, "rmo", "bundle", 71, false),
        });
        MeshGltf.ExportGlb(Patch(), returned,
            perSubmesh: new[] { ((string?)null, (string?)null, (string?)composed) },
            textureTransport: new[]
            {
                new TextureTransportSource("veil", 0, 0, "_RMOMap", MapKind.Rmo, edited ? painted : stock, "rmo",
                    "bundle", 71, false),
            });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);

        Assert.Empty(notes);
        var incoming = Assert.Single(maps);
        Assert.Equal(edited ? MapOrigin.Authored : MapOrigin.Vanilla, incoming.Rmo.Origin);
        if (edited) Assert.Equal(new Rgba32(90, 91, 92, 255), FirstPixel(incoming.Rmo.AuthoredPng!));
    }

    // ------------------------------------------------ more submeshes than the part has materials
    //
    // The game draws a part once per material; a replacement's extra submeshes draw at the last drawable
    // material's fire and the edit's cards slot them there. The outbound inventory, the re-split and the
    // return's join all apply that one fold (MaterialFold), so a picture for such a submesh returns.

    [Fact]
    public void The_outbound_inventory_folds_extra_primitives_onto_the_last_drawable_material()
    {
        using var game = new TempGame();
        string texDir = game.At("textures");
        Directory.CreateDirectory(texDir);
        var first = new TexTarget("first", "bundle", false, false, "renderer", PathId: 71);
        var second = new TexTarget("second", "bundle", false, false, "renderer", PathId: 72);
        var materials = new[]
        {
            new MaterialTextureBindings(0, "cloth", new[] { new BoundTexture("_BaseMap", first) }),
            new MaterialTextureBindings(1, "trim", new[] { new BoundTexture("_BaseMap", second) }),
        };
        var part = new PartTextures(PartTextureResolver.ExportInventory(materials),
            new[] { new SubmeshMaps(null, null), new SubmeshMaps(null, null) }, Materials: materials);
        Png(Path.Combine(texDir, TextureExport.BundleScopedName("bundle", "first", "subject")), 30);
        Png(Path.Combine(texDir, TextureExport.BundleScopedName("bundle", "second", "subject")), 180);

        // a four-submesh replacement over two drawable materials: the trailing two fold onto the last
        var folded = AssetExporter.ResolveTextureTransport(texDir, "subject", "veil", part,
            primitiveCount: 4, stockIndexCounts: new[] { 3, 3 });
        Assert.Equal(new int?[] { 0 }, folded.Where(r => r.MaterialIndex == 0).Select(r => r.PrimitiveIndex));
        Assert.Equal(new int?[] { 1, 2, 3 }, folded.Where(r => r.MaterialIndex == 1).Select(r => r.PrimitiveIndex));
        Assert.All(folded, row => Assert.True(row.Drawable));

        // the second material's stock submesh has no indices, so it never fires: every submesh draws under
        // the first, and the second rides along as surplus inventory
        var undrawable = AssetExporter.ResolveTextureTransport(texDir, "subject", "veil", part,
            primitiveCount: 4, stockIndexCounts: new[] { 3, 0 });
        Assert.Equal(new int?[] { 0, 1, 2, 3 },
            undrawable.Where(r => r.MaterialIndex == 0).Select(r => r.PrimitiveIndex));
        var surplus = Assert.Single(undrawable, r => r.MaterialIndex == 1);
        Assert.Null(surplus.PrimitiveIndex);
        Assert.False(surplus.Drawable);
    }

    [Fact]
    public void A_record_refolds_onto_any_primitive_count_and_keeps_a_materials_drawability()
    {
        PreviewMaps.TransportBinding Row(int material, int? primitive, bool? drawable) => new("veil", material,
            primitive, "_BaseMap", MapKind.BaseColor, "stock.png", "hash", new PreviewMaps.TransportStock("s", "b", 1),
            Drawable: drawable);
        var record = new[] { Row(0, 0, true), Row(1, 1, true) };

        // fewer submeshes: the second material keeps a primitive-less row, still drawable
        var narrowed = MaterialFold.FoldOntoPrimitives(record, 1);
        Assert.Equal(0, Assert.Single(narrowed, r => r.MaterialIndex == 0).PrimitiveIndex);
        var kept = Assert.Single(narrowed, r => r.MaterialIndex == 1);
        Assert.Null(kept.PrimitiveIndex);
        Assert.True(kept.Drawable);

        // ...so a later, wider send still folds its extras onto it rather than onto the first material
        var widened = MaterialFold.FoldOntoPrimitives(narrowed.ToList(), 3);
        Assert.Equal(new int?[] { 1, 2 }, widened.Where(r => r.MaterialIndex == 1).Select(r => r.PrimitiveIndex));

        // a material recorded as never drawing takes no primitive, whatever the count
        var closed = MaterialFold.FoldOntoPrimitives(new[] { Row(0, 0, true), Row(1, null, false) }, 3);
        Assert.Equal(new int?[] { 0, 1, 2 }, closed.Where(r => r.MaterialIndex == 0).Select(r => r.PrimitiveIndex));
        Assert.Null(Assert.Single(closed, r => r.MaterialIndex == 1).PrimitiveIndex);

        // the older record shape, written without the flag, reads as drawable
        var legacy = MaterialFold.FoldOntoPrimitives(new[] { Row(0, 0, null), Row(1, 1, null) }, 3);
        Assert.Equal(new int?[] { 1, 2 }, legacy.Where(r => r.MaterialIndex == 1).Select(r => r.PrimitiveIndex));
    }

    [Fact]
    public void A_return_with_more_submeshes_than_the_record_joins_the_extras_to_the_last_material()
    {
        using var game = new TempGame();
        string stockA = Png(game.At("stock-a.png"), 10);
        string stockB = Png(game.At("stock-b.png"), 80);
        string painted = Png(game.At("painted.png"), 200);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        string stockRmoB = Png(game.At("stock-rmo-b.png"), 40);
        MeshGltf.ExportGlb(SubmeshPatch(2), opened, textureTransport: new[]
        {
            new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, stockA, "a", "bundle", 71, true,
                Drawable: true),
            new TextureTransportSource("veil", 1, 1, "_BaseMap", MapKind.BaseColor, stockB, "b", "bundle", 72, true,
                Drawable: true),
            new TextureTransportSource("veil", 1, 1, "_RMOMap", MapKind.Rmo, stockRmoB, "rmo-b", "bundle", 73, false,
                Drawable: true),
        });
        // what came back: a three-submesh replacement, its third submesh carrying its own picture
        MeshGltf.ExportGlb(SubmeshPatch(3), returned, perSubmesh: new[]
        {
            ((string?)stockA, (string?)null, (string?)null),
            ((string?)stockB, (string?)null, (string?)null),
            ((string?)painted, (string?)null, (string?)null),
        });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);

        Assert.Empty(notes);
        Assert.Equal(3, maps.Count);
        Assert.Equal(MapOrigin.Vanilla, maps[0].BaseColor.Origin);
        Assert.Equal(MapOrigin.Vanilla, maps[1].BaseColor.Origin);
        Assert.Equal(MapOrigin.Authored, maps[2].BaseColor.Origin);
        var extra = Assert.Single(maps[2].Textures!, t => t.ShaderProperty == "_BaseMap");
        Assert.Equal((1, 2, "_BaseMap"), (extra.MaterialIndex, extra.PrimitiveIndex, extra.ShaderProperty));
        Assert.Equal(new Rgba32(200, 201, 202, 255), FirstPixel(extra.Map.AuthoredPng!));
        // the folded submesh knows which RMO its emissive mask is rebuilt over, though the record's
        // per-submesh RMO rows stop at the two it was written for
        Assert.Equal(Path.GetFullPath(stockRmoB), maps[2].RmoStockSource);
        Assert.Null(maps[0].RmoStockSource);
        var row = Assert.Single(BlenderMaterialReturn.Normalize(maps, game.At("staging")));
        Assert.Equal(2, row.Submesh);
    }

    [Fact]
    public void A_resplit_projects_the_record_over_the_returned_submeshes_with_each_primitives_own_picture()
    {
        using var game = new TempGame();
        string stockA = Png(game.At("stock-a.png"), 10);
        string stockB = Png(game.At("stock-b.png"), 80);
        string paintedB = Png(game.At("painted-b.png"), 120);
        string paintedC = Png(game.At("painted-c.png"), 200);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        string resplit = game.At("resplit.glb");
        MeshGltf.ExportGlb(SubmeshPatch(2), opened, textureTransport: new[]
        {
            new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, stockA, "a", "bundle", 71, true,
                Drawable: true),
            new TextureTransportSource("veil", 1, 1, "_BaseMap", MapKind.BaseColor, stockB, "b", "bundle", 72, true,
                Drawable: true),
        });
        MeshGltf.ExportGlb(SubmeshPatch(3), returned);

        // two submeshes drawn under one material, each with its own authored picture
        MeshGltf.ReexportPartGlb(returned, "veil", resplit, recordGlb: opened, authoredTextures: new[]
        {
            new TextureTransportOverride(1, "_BaseMap", paintedB, MapKind.BaseColor, PrimitiveIndex: 1),
            new TextureTransportOverride(1, "_BaseMap", paintedC, MapKind.BaseColor, PrimitiveIndex: 2,
                Label: "Painted C"),
        });

        var record = PreviewMaps.ReadTransportBindings(resplit);
        Assert.Equal(new int?[] { 0, 1, 2 }, record.Select(r => r.PrimitiveIndex).OrderBy(p => p));
        Assert.Equal(Path.GetFullPath(stockA), Assert.Single(record, r => r.PrimitiveIndex == 0).Source);
        Assert.Equal(Path.GetFullPath(paintedB), Assert.Single(record, r => r.PrimitiveIndex == 1).Source);
        Assert.Equal(Path.GetFullPath(paintedC), Assert.Single(record, r => r.PrimitiveIndex == 2).Source);
        Assert.All(record.Where(r => r.PrimitiveIndex != 0), r => Assert.Equal(MapOrigin.Authored, r.Origin));
        // and the file itself carries the third primitive's picture, tagged for its own slot
        var carried = GltfTextureTransport.Read(resplit).Bindings;
        var third = Assert.Single(carried, r => r.PrimitiveIndex == 2);
        Assert.Equal(new Rgba32(200, 201, 202, 255), FirstPixel(third.Png));
        // Blender lists the modder's picture under its project label, on the tagged node and the material alike
        Assert.Equal("Painted C", third.ImageName);
        using var json = ReadGlbJson(resplit);
        Assert.Contains(json.RootElement.GetProperty("images").EnumerateArray(),
            image => image.GetProperty("name").GetString() == "Painted C");
    }

    /// <summary>"Plug the neutral" is a gesture on every normal slot, including one whose outbound picture is
    /// the modder's own authored normal. That picture lives in the project, where no neutral_n.png sits,
    /// so the neutral has to come from the session record; the record names it beside any stock binding.
    /// Blender exports the plugged file as it is, so the return carries the neutral's own bytes.</summary>
    [Fact]
    public void The_neutral_normal_plugged_over_an_authored_normal_still_blanks_the_slot()
    {
        using var game = new TempGame();
        string texDir = game.At("textures");
        PreviewMaps.WriteNeutrals(texDir);
        string neutral = Path.Combine(texDir, PreviewMaps.NeutralN);
        string stockBase = Png(Path.Combine(texDir, "stock-base.png"), 20);
        Directory.CreateDirectory(game.At("project"));
        string authoredNormal = Png(game.At(Path.Combine("project", "authored-normal.png")), 140);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        var normalSlot = new TextureTransportSource("veil", 0, 0, "_NormalMap", MapKind.Normal, authoredNormal,
            "normal", "bundle", 72, false, Origin: MapOrigin.Authored);
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, stockBase, "base", "bundle", 71,
                true),
            normalSlot,
        });
        MeshGltf.ExportGlb(Patch(), returned);
        byte[] plugged = File.ReadAllBytes(neutral);
        GltfTextureTransport.WriteTransformed(returned, new[]
        {
            new TransformedTextureTransportSource(normalSlot with { Png = neutral }, plugged,
                PreviewMaps.Hash(plugged)),
        });
        var notes = new List<string>();

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened, notes.Add);

        Assert.Empty(notes);
        Assert.Equal(MapOrigin.Neutral, Assert.Single(maps).Normal.Origin);
        var row = Assert.Single(BlenderMaterialReturn.Normalize(maps, game.At("staging")));
        Assert.Equal(SlotOrigin.ExplicitNeutral, row.NormalAsk);
    }

    private static UnityMesh TwoSubmeshPatch(string name = "veil") => SubmeshPatch(2, name);

    /// <summary>A patch of <paramref name="count"/> disjoint triangles, one submesh each.</summary>
    private static UnityMesh SubmeshPatch(int count, string name = "veil") => new()
    {
        Name = name,
        VertexCount = count * 3,
        Channels = new()
        {
            ["Vertex"] = Enumerable.Range(0, count)
                .SelectMany(s => new float[] { 2 * s, 0, 0, 2 * s + 1, 0, 0, 2 * s, 1, 0 }).ToArray(),
            ["TexCoord0"] = Enumerable.Range(0, count).SelectMany(_ => new float[] { 0, 0, 1, 0, 0, 1 }).ToArray(),
        },
        Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2 },
        Submeshes = Enumerable.Range(0, count).Select(s => new[] { 3 * s, 3 * s + 1, 3 * s + 2 }).ToList(),
    };

    [Fact]
    public void BaseMap_and_MainTex_publish_without_ambiguity_and_round_trip_their_own_pictures()
    {
        using var game = new TempGame();
        string root = game.At("mod");
        Directory.CreateDirectory(root);
        string stockBase = Png(game.At("stock-base.png"), 20);
        string stockMain = Png(game.At("stock-main.png"), 40);
        string paintedBase = Png(game.At("painted-base.png"), 100);
        string paintedMain = Png(game.At("painted-main.png"), 160);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        TextureTransportSource Source(string property, string png, long pathId) =>
            new("veil", 0, 0, property, MapKind.BaseColor, png, property, "bundle", pathId, true);
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            Source("_BaseMap", stockBase, 71),
            Source("_MainTex", stockMain, 72),
        });
        MeshGltf.ExportGlb(Patch(), returned, textureTransport: new[]
        {
            Source("_BaseMap", paintedBase, 71),
            Source("_MainTex", paintedMain, 72),
        });

        var returnedMaps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened);
        var rows = BlenderMaterialReturn.Normalize(returnedMaps, game.At("normalized"));
        var normalized = Assert.Single(rows);
        Assert.NotNull(normalized.Albedo);
        Assert.Equal("_MainTex", Assert.Single(normalized.Textures!).ShaderProperty);

        var part = AuthoredParts.Part("Vesna", "VesnaSSR01", "veil");
        var original = AuthoredParts.Resolve(part);
        var material = Assert.Single(original.Materials);
        var first = material.Textures.Single(texture => texture.Input == TargetInputKind.BaseColor) with
            { ShaderProperty = "_BaseMap" };
        var secondTexture = new GameAssetRef
        {
            GameBuild = first.Texture.GameBuild,
            LogicalBundle = first.Texture.LogicalBundle,
            PathId = first.Texture.PathId + 1,
            Name = "main",
        };
        var second = new LegacyResolvedTexture(TargetInputKind.BaseColor, first.LegacyBundle,
            "main", secondTexture.PathId, secondTexture, "_MainTex");
        var resolved = original with
        {
            Materials = new[]
            {
                material with
                {
                    Textures = new[] { first, second }.Concat(material.Textures
                        .Where(texture => texture.Input != TargetInputKind.BaseColor)).ToList(),
                },
            },
        };
        var session = new AuthoredEditSession(new AuthoredProject { RootDir = root });
        session.EnsurePartSlots(part, _ => resolved);
        string edit = session.CreateEdit(part);
        var geometry = session.Slots(edit).Single(state => state.Slot.Domain == TargetSlotDomain.Game
            && state.Slot.Input == TargetInputKind.Geometry
            && (state.Slot.Tier is null || state.Slot.Tier == "lod0"));
        var geometryIngress = ProjectAssetIngress.Begin(session.Snapshot(), edit, geometry.Slot.Id, returned);
        var geometryPublish = session.PublishAssetForBinding(geometryIngress, ProjectAssetKind.Geometry,
            "Returned veil", ProjectAssetIngress.Binary, replacementSubmeshCount: 1);
        Assert.Equal(ProjectAssetPublishResult.Published, geometryPublish.Result);

        int published = 0;
        session.Compound(change => published = MainWindowViewModel.PublishBlenderMaps(change, edit, 1, rows));

        Assert.Equal(2, published);
        var outputs = session.Slots(edit).Where(state => state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.Input == TargetInputKind.BaseColor).ToList();
        var baseOutput = Assert.Single(outputs, state => state.Slot.ShaderProperty == "_BaseMap");
        var mainOutput = Assert.Single(outputs, state => state.Slot.ShaderProperty == "_MainTex");
        Assert.Equal(BindingKind.ProjectAsset, baseOutput.Binding.Kind);
        Assert.Equal(BindingKind.ProjectAsset, mainOutput.Binding.Kind);
        Assert.NotEqual(baseOutput.ProjectAsset!.File, mainOutput.ProjectAsset!.File);
        Assert.Equal(new Rgba32(100, 101, 102, 255), FirstPixel(Path.Combine(root, baseOutput.ProjectAsset.File)));
        Assert.Equal(new Rgba32(160, 161, 162, 255), FirstPixel(Path.Combine(root, mainOutput.ProjectAsset.File)));

        var plan = AuthoredBuildPlanner.Plan(session.Snapshot(), new ProductionAuthoredBuildBackend(_ => resolved));
        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts.Concat(plan.Bindings
            .Where(binding => binding.Decision.BlocksBuild).Select(binding => binding.Decision.Reason))));
    }

    [Fact]
    public void An_unkeyed_extra_image_is_ignored_with_its_name()
    {
        using var game = new TempGame();
        string glb = game.At("extra.glb");
        MeshGltf.ExportGlb(Patch(), glb);
        var model = ModelRoot.Load(glb);
        var loose = model.UseImageWithContent(new SharpGLTF.Memory.MemoryImage(
            File.ReadAllBytes(Png(game.At("loose.png"), 42))));
        loose.Name = "Loose paint reference";
        model.SaveGLB(glb);
        var notes = new List<string>();

        MeshGltf.ReadSubmeshMaps(glb, report: notes.Add);

        Assert.Contains("Ignored Loose paint reference from Blender: it isn't linked to a texture slot.",
            notes);
    }

    [Fact]
    public void An_unkeyed_image_is_reported_once_for_the_combined_return_session()
    {
        using var game = new TempGame();
        string glb = game.At("combined-extra.glb");
        MeshGltf.ExportGlb(Patch("veil"), glb);
        var model = ModelRoot.Load(glb);
        var loose = model.UseImageWithContent(new SharpGLTF.Memory.MemoryImage(
            File.ReadAllBytes(Png(game.At("loose-combined.png"), 42))));
        loose.Name = "Loose combined reference";
        model.SaveGLB(glb);
        var parsed = MeshGltf.ParsedGlb.Open(glb);
        var notes = new List<string>();

        MeshGltf.ReadSubmeshMaps(parsed, "veil", report: notes.Add, reportUnkeyed: false);
        MeshGltf.ReadSubmeshMaps(parsed, "unrelated", report: notes.Add, reportUnkeyed: false);
        foreach (string image in parsed.UnkeyedTextureImages)
            notes.Add($"Ignored {image} from Blender: it isn't linked to a texture slot.");

        Assert.Equal(new[]
        {
            "Ignored Loose combined reference from Blender: it isn't linked to a texture slot.",
        }, notes);
    }

    private static Rgba32 FirstPixel(string png)
    {
        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(png);
        return image[0, 0];
    }

    private static Rgba32 FirstPixel(byte[]? png)
    {
        Assert.NotNull(png);
        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(png);
        return image[0, 0];
    }

    /// <summary>Stamp a return glb with the one carrier row the new add-on sends for an untouched
    /// picture: the binding's identity and outbound hash, no image.</summary>
    private static void WriteHashOnlyRow(string glb, PreviewMaps.TransportBinding binding)
    {
        RewriteGlbJson(glb, root =>
        {
            var owner = new JsonObject
            {
                ["mesh"] = binding.Mesh,
                ["material"] = binding.MaterialIndex,
            };
            if (binding.PrimitiveIndex is { } primitive) owner["primitive"] = primitive;
            var row = new JsonObject
            {
                ["owner"] = owner,
                ["property"] = binding.ShaderProperty,
                ["semantic"] = binding.Kind switch
                {
                    MapKind.BaseColor => "baseColor",
                    MapKind.Normal => "normal",
                    MapKind.Rmo => "rmo",
                    MapKind.Blend => "blend",
                    _ => "texture",
                },
                ["outbound_hash"] = binding.OutboundHash,
                ["stock"] = new JsonObject
                {
                    ["name"] = binding.Stock.Name,
                    ["bundle"] = binding.Stock.Bundle,
                    ["path_id"] = binding.Stock.PathId,
                },
                ["origin"] = binding.Origin.ToString().ToLowerInvariant(),
            };
            if (binding.Srgb is { } srgb) row["srgb"] = srgb;
            if (binding.TexCoord is { } texCoord) row["texCoord"] = texCoord;
            var extras = root["extras"] as JsonObject ?? new JsonObject();
            root["extras"] = extras;
            if (extras[GltfTextureTransport.ExtrasKey] is JsonObject carrier
                && carrier["bindings"] is JsonArray existing)
            {
                existing.Add(row);
            }
            else
            {
                extras[GltfTextureTransport.ExtrasKey] = new JsonObject
                {
                    ["version"] = GltfTextureTransport.Version,
                    ["bindings"] = new JsonArray(row),
                };
            }
        });
    }

    private static JsonDocument ReadGlbJson(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        _ = reader.ReadUInt32();
        while (stream.Position < stream.Length)
        {
            uint length = reader.ReadUInt32();
            uint type = reader.ReadUInt32();
            var content = reader.ReadBytes(checked((int)length));
            if (type == 0x4E4F534Au) return JsonDocument.Parse(content);
        }
        throw new InvalidDataException("GLB has no JSON chunk.");
    }

    private static void RewriteGlbJson(string path, Action<JsonObject> mutate)
    {
        var chunks = new List<(uint Type, byte[] Content)>();
        using (var stream = File.OpenRead(path))
        using (var reader = new BinaryReader(stream))
        {
            Assert.Equal(0x46546C67u, reader.ReadUInt32());
            Assert.Equal(2u, reader.ReadUInt32());
            _ = reader.ReadUInt32();
            while (stream.Position < stream.Length)
            {
                uint length = reader.ReadUInt32();
                uint type = reader.ReadUInt32();
                chunks.Add((type, reader.ReadBytes(checked((int)length))));
            }
        }

        int jsonIndex = chunks.FindIndex(chunk => chunk.Type == 0x4E4F534Au);
        Assert.True(jsonIndex >= 0);
        var root = JsonNode.Parse(Encoding.UTF8.GetString(chunks[jsonIndex].Content).TrimEnd(' ', '\0'))!
            .AsObject();
        mutate(root);
        byte[] rawJson = Encoding.UTF8.GetBytes(root.ToJsonString());
        int paddedLength = (rawJson.Length + 3) & ~3;
        var paddedJson = Enumerable.Repeat((byte)' ', paddedLength).ToArray();
        rawJson.CopyTo(paddedJson, 0);
        chunks[jsonIndex] = (0x4E4F534Au, paddedJson);

        int totalLength = checked(12 + chunks.Sum(chunk => 8 + chunk.Content.Length));
        using var output = File.Create(path);
        using var writer = new BinaryWriter(output);
        writer.Write(0x46546C67u);
        writer.Write(2u);
        writer.Write((uint)totalLength);
        foreach (var chunk in chunks)
        {
            writer.Write((uint)chunk.Content.Length);
            writer.Write(chunk.Type);
            writer.Write(chunk.Content);
        }
    }
}
