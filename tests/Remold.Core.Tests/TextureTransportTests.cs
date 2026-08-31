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

        Assert.Equal("rigged-build-spec-v2", AssetExporter.RiggedBuildSpec);
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

    [Fact]
    public void A_carrier_session_never_falls_back_to_an_unkeyed_standard_channel()
    {
        using var game = new TempGame();
        string stock = Png(game.At("stock.png"), 20);
        string painted = Png(game.At("painted.png"), 90);
        string opened = game.At("opened.glb");
        string returned = game.At("returned.glb");
        MeshGltf.ExportGlb(Patch(), opened, textureTransport: new[]
        {
            new TextureTransportSource("veil", 0, 0, "_BaseMap", MapKind.BaseColor, stock,
                "base", "bundle", 71, true),
        });
        MeshGltf.ExportGlb(Patch(), returned,
            perSubmesh: new[] { ((string?)painted, (string?)null, (string?)null) });

        var maps = MeshGltf.ReadSubmeshMaps(returned, "veil", opened);

        Assert.Equal(MapOrigin.None, Assert.Single(maps).BaseColor.Origin);
    }

    [Fact]
    public void A_bindingless_part_in_a_keyed_session_accepts_geometry_without_classifying_standard_channels()
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

        var maps = MeshGltf.ReadSubmeshMaps(returned, "bindingless", opened);

        Assert.Equal(MapOrigin.None, Assert.Single(maps).BaseColor.Origin);
        Assert.Null(Assert.Single(maps).Textures);
    }

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

    private static Rgba32 FirstPixel(byte[] png)
    {
        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(png);
        return image[0, 0];
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
