using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>The coarse Texture input at the authoring boundary: exact property identity survives into
/// repair and emission, while resource identity still decides whether a game edit can be global.</summary>
public sealed class GenericTextureAuthoringTests : IDisposable
{
    private readonly ModBuilderTests _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Two_properties_on_one_shared_resource_refuse_scoped_attribution()
    {
        var fixture = Fixture(shared: true);
        Author(fixture, "_DetailAlbedo", "weave.png", new Rgba32(10, 20, 30, 255));
        Author(fixture, "_DetailMask", "mask.png", new Rgba32(220, 210, 200, 255));

        var exception = Assert.Throws<InvalidOperationException>(() => Build(fixture));

        Assert.Equal("The original texture 'tex_body_d' is used by Detail color and Detail mask on "
            + "'c_vesna01_body_lod0', so this edit cannot reach Detail color alone at this draw. "
            + "Leave this picture out, or change the texture for every slot that draws it with a game-wide edit.",
            exception.Message);
    }

    [Fact]
    public void A_single_scoped_property_refuses_when_another_property_binds_its_resource()
    {
        var fixture = Fixture(shared: true);
        Author(fixture, "_DetailAlbedo", "weave.png", new Rgba32(10, 20, 30, 255));

        var exception = Assert.Throws<InvalidOperationException>(() => Build(fixture));

        Assert.Equal("The original texture 'tex_body_d' is used by Detail color and Detail mask on "
            + "'c_vesna01_body_lod0', so this edit cannot reach Detail color alone at this draw. "
            + "Leave this picture out, or change the texture for every slot that draws it with a game-wide edit.",
            exception.Message);
    }

    [Fact]
    public void Two_properties_on_distinct_shared_resources_use_shipped_overlapping_ranges_and_bind_independently()
    {
        var fixture = Fixture(shared: true, distinctResources: true);
        Author(fixture, "_DetailAlbedo", "weave.png", new Rgba32(10, 20, 30, 255));
        Author(fixture, "_DetailMask", "mask.png", new Rgba32(220, 210, 200, 255));

        var result = Build(fixture);

        string ini = File.ReadAllText(Path.Combine(result.OutDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        Assert.Equal(1, CountOf(ini, $"[TextureOverride_RetexTag_{_world.StockTexHash}]"));
        Assert.Equal(1, CountOf(ini, $"[TextureOverride_RetexTag_{fixture.SecondHash}]"));
        var scopedAnchors = ini.Split("[TextureOverride_RetexScope_", StringSplitOptions.None).Skip(1).ToList();
        Assert.Equal(2, scopedAnchors.Count);
        Assert.All(scopedAnchors, section => Assert.Equal(2, CountOf(section, "$zz_rslot = -1")));
        Assert.Contains("Resource_RtxSave4 = ref ps-t4", ini);
        Assert.Contains("Resource_RtxSave6 = ref ps-t6", ini);
        Assert.Contains("if $zz_rslot == 4\nps-t4 = Resource_Rtx0\nendif", ini);
        Assert.Contains("if $zz_rslot == 6\nps-t6 = Resource_Rtx1\nendif", ini);
    }

    [Fact]
    public void Two_properties_on_one_private_resource_refuse_two_different_pictures_by_label()
    {
        var fixture = Fixture(shared: false);
        Author(fixture, "_DetailAlbedo", "weave.png", new Rgba32(10, 20, 30, 255));
        Author(fixture, "_DetailMask", "mask.png", new Rgba32(220, 210, 200, 255));

        var exception = Assert.Throws<AuthoredRefusalException>(() => Build(fixture));

        Assert.Equal("Detail color and Detail mask on 'c_vesna01_body_lod0' change the same original "
            + "texture 'tex_body_d' and cannot take two different pictures through this route. "
            + "Give both slots the same picture, or leave one unchanged.", exception.Message);
    }

    [Fact]
    public void Two_properties_on_one_private_resource_may_take_the_same_picture()
    {
        var fixture = Fixture(shared: false);
        string asset = Author(fixture, "_DetailAlbedo", "shared.png", new Rgba32(10, 20, 30, 255));
        string maskSlot = Slot(fixture.Session, "_DetailMask");
        fixture.Session.ChooseProjectAsset(fixture.Edit, maskSlot, asset);

        var result = Build(fixture);

        string ini = File.ReadAllText(Path.Combine(result.OutDir, "mod.ini"));
        Assert.Equal(1, CountOf(ini, "[TextureOverride_Retex_"));
        Assert.DoesNotContain("[TextureOverride_RetexScope_", ini);
        using var repair = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.OutDir, "repair.json")));
        var properties = repair.RootElement.GetProperty("changes")[0].GetProperty("textures")[0]
            .GetProperty("textures").EnumerateArray()
            .Select(row => row.GetProperty("shader_property").GetString()).ToArray();
        Assert.Equal(new[] { "_DetailAlbedo", "_DetailMask" }, properties);
    }

    [Fact]
    public void A_shared_property_without_register_coverage_refuses_before_it_can_widen()
    {
        var fixture = Fixture(shared: true, propertyCoverage: false);
        Author(fixture, "_DetailAlbedo", "weave.png", new Rgba32(10, 20, 30, 255));

        var exception = Assert.Throws<AuthoredRefusalException>(() => Build(fixture));

        Assert.Equal("Detail color on 'c_vesna01_body_lod0' cannot be changed safely. "
            + "No measured texture-register coverage exists for _DetailAlbedo. "
            + "Update the app's game data, or leave this picture out.", exception.Message);
    }

    [Fact]
    public void A_private_fixed_claim_does_not_ride_another_subjects_scoped_property_hash()
    {
        var env = _world.MakeEnv(out _, out _);
        var original = env.ResolveSubject("Vesna", "VesnaSSR01")!;
        var part = Assert.Single(original.Parts);
        var material = Assert.Single(part.Materials);
        var stock = Assert.Single(material.Maps);
        var scopedModel = original with
        {
            Parts = new[]
            {
                part with
                {
                    Materials = new[]
                    {
                        material with { Maps = new[] { stock with { Slot = "_DetailMask" } } },
                    },
                },
            },
        };
        var privateModel = original with
        {
            Stem = "VesnaDorm",
            Parts = new[]
            {
                part with
                {
                    Materials = new[]
                    {
                        material with { Maps = new[] { stock with { Slot = "_BaseMap" } } },
                    },
                },
            },
        };
        var wearers = new[]
        {
            new SharingIndex.Wearer("Vesna", "Vesna", "VesnaSSR01", null),
            new SharingIndex.Wearer("Vesna", "Vesna", "VesnaDorm", null),
        };
        env = env with
        {
            ResolveSubject = (character, outfit) => character != "Vesna" ? null : outfit switch
            {
                "VesnaSSR01" => scopedModel,
                "VesnaDorm" => privateModel,
                _ => null,
            },
            Sharing = SharingIndex.FromMeasurements("12345", wearers,
                new Dictionary<string, int[]> { [_world.StockTexHash] = new[] { 1 } },
                new Dictionary<string, int[]>(), new Dictionary<int, string[]>()),
            ShaderSlotCatalogFile = Path.Combine(AppContext.BaseDirectory, "data", "charps_slots.json"),
        };

        string root = _world.NewProject("Scoped identity").RootDir!;
        var session = new AuthoredEditSession(new AuthoredProject { RootDir = root });
        var scopedTarget = new TargetPart
        {
            Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "c_vesna01_body_lod0",
        };
        var privateTarget = new TargetPart
        {
            Subject = "Vesna", Outfit = "VesnaDorm", RendererSlot = "c_vesna01_body_lod0",
        };
        session.SetWorkspaceIndex(new AuthoredWorkspaceIndex
        {
            Selection = new List<SelectionEntry>
            {
                new() { Character = scopedTarget.Subject, Outfit = scopedTarget.Outfit },
                new() { Character = privateTarget.Subject, Outfit = privateTarget.Outfit },
            },
        });
        var resolver = new LegacyProjectResolver(env);
        session.EnsurePartSlots(scopedTarget, resolver.ResolvePart);
        string scopedEdit = session.CreateEdit(scopedTarget);
        session.EnsurePartSlots(privateTarget, resolver.ResolvePart);
        string privateEdit = session.CreateEdit(privateTarget);
        Publish(scopedEdit, "_DetailMask", "scoped.png");
        Publish(privateEdit, "_BaseMap", "private.png");

        var project = session.Snapshot();
        var plan = AuthoredBuildPlanner.Plan(project, new ProductionAuthoredBuildBackend(resolver.ResolvePart));
        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts.Concat(plan.Bindings
            .Where(binding => binding.Decision.BlocksBuild)
            .Select(binding => $"{binding.RowId}: {binding.Decision.Reason}"))));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModBuilder.Build(AuthoredBuildExecution.Create(project, plan), env, _world.OutRoot, zip: false));

        Assert.Contains($"both override texture hash {_world.StockTexHash}", exception.Message);
        Assert.Contains("retexture entries", exception.Message);

        void Publish(string edit, string property, string file)
        {
            string source = Path.Combine(root, file);
            using (var image = new Image<Rgba32>(8, 8, new Rgba32(10, 20, 30, 255)))
                image.SaveAsPng(source);
            string slot = session.Snapshot().TargetSlots.Single(candidate =>
                candidate.Domain == TargetSlotDomain.Game
                && candidate.Part.SameAs(edit == scopedEdit ? scopedTarget : privateTarget)
                && candidate.ShaderProperty == property).Id;
            var ingress = ProjectAssetIngress.Begin(session.Snapshot(), edit, slot, source);
            var published = session.PublishAssetForBinding(ingress, ProjectAssetKind.Picture,
                property, ProjectAssetIngress.Png);
            Assert.Equal(ProjectAssetPublishResult.Published, published.Result);
        }
    }

    private sealed record TestFixture(BuildEnv Env, AuthoredEditSession Session, string Edit, string Root,
        string OutRoot, string? SecondHash = null);

    private TestFixture Fixture(bool shared, bool propertyCoverage = true, bool distinctResources = false)
    {
        var env = _world.MakeEnv(out _, out _);
        var original = env.ResolveSubject("Vesna", "VesnaSSR01")!;
        var part = Assert.Single(original.Parts);
        var material = Assert.Single(part.Materials);
        var stock = Assert.Single(material.Maps);
        string root = _world.NewProject("Generic").RootDir!;
        SubjectMap mask = stock with { Slot = "_DetailMask" };
        string? secondHash = null;
        if (distinctResources)
        {
            string bundleFile = Path.Combine(root, "mask.bundle");
            long pathId = SyntheticBundle.BuildOneTexture(bundleFile, "tex_body_mask", 8, 8,
                40, 80, 120, 255, colorSpace: 0);
            byte[] bundle = File.ReadAllBytes(bundleFile);
            secondHash = SyntheticBundle.StockTexHash(bundle, "tex_body_mask");
            var deobfuscate = env.Deobfuscate;
            env = env with { Deobfuscate = id => id == "bundleMask" ? bundle : deobfuscate(id) };
            mask = new SubjectMap("_DetailMask", "tex_body_mask", "bundleMask", pathId);
        }
        var exactMaterial = material with
        {
            Maps = propertyCoverage
                ? new[] { stock with { Slot = "_DetailAlbedo" }, mask }
                : new[] { stock with { Slot = "_DetailAlbedo" } },
        };
        var exactModel = original with
        {
            Parts = new[] { part with { Materials = new[] { exactMaterial } } },
        };
        string catalog;
        if (propertyCoverage)
            catalog = Path.Combine(AppContext.BaseDirectory, "data", "charps_slots.json");
        else
        {
            catalog = Path.Combine(root, "generic-slots.json");
            File.WriteAllText(catalog, """
            {
              "schema": 1,
              "catalog_id": "generic-test",
              "game_build": "test",
              "source_reflection_sha256_16": "0123456789abcdef",
              "validation_policy": "filter_index_tag_probe",
              "inputs": {
                "BaseMap": { "0": 1 },
                "BumpMap": { "2": 1 },
                "RMOTex": { "3": 1 },
                "RampMap": { "7": 1 },
                "BlendTex": { "2": 1 }
              }
            }
            """);
        }
        SharingIndex? sharing = null;
        if (shared)
        {
            var textureWearers = new Dictionary<string, int[]>
            {
                [_world.StockTexHash] = new[] { 0, 1 },
            };
            if (secondHash is not null) textureWearers[secondHash] = new[] { 0, 1 };
            sharing = SharingIndex.FromMeasurements("12345",
                new[]
                {
                    new SharingIndex.Wearer("Vesna", "Vesna", "VesnaSSR01", null),
                    new SharingIndex.Wearer("Karst", "Karst", "KarstDorm", null),
                },
                textureWearers,
                new Dictionary<string, int[]>(), new Dictionary<int, string[]>());
        }
        env = env with
        {
            ResolveSubject = (character, outfit) => character == "Vesna" && outfit == "VesnaSSR01"
                ? exactModel : null,
            Sharing = sharing,
            ShaderSlotCatalogFile = catalog,
        };
        var resolver = new LegacyProjectResolver(env);
        var target = new TargetPart
        {
            Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = "c_vesna01_body_lod0",
        };
        var session = new AuthoredEditSession(new AuthoredProject { RootDir = root });
        session.SetWorkspaceIndex(new AuthoredWorkspaceIndex
        {
            Selection = new List<SelectionEntry>
            {
                new() { Character = target.Subject, Outfit = target.Outfit },
            },
        });
        session.EnsurePartSlots(target, resolver.ResolvePart);
        string edit = session.CreateEdit(target);
        return new TestFixture(env, session, edit, root, _world.OutRoot, secondHash);
    }

    private static string Author(TestFixture fixture, string property, string file, Rgba32 colour)
    {
        string source = Path.Combine(fixture.Root, file);
        using (var image = new Image<Rgba32>(8, 8, colour)) image.SaveAsPng(source);
        string slot = Slot(fixture.Session, property);
        var ingress = ProjectAssetIngress.Begin(fixture.Session.Snapshot(), fixture.Edit, slot, source);
        var published = fixture.Session.PublishAssetForBinding(ingress, ProjectAssetKind.Picture,
            property, ProjectAssetIngress.Png);
        Assert.Equal(ProjectAssetPublishResult.Published, published.Result);
        return published.ProjectAssetId!;
    }

    private static string Slot(AuthoredEditSession session, string property) => session.Snapshot().TargetSlots
        .Single(slot => slot.Domain == TargetSlotDomain.Game && slot.ShaderProperty == property).Id;

    private static ModBuilder.Result Build(TestFixture fixture)
    {
        var project = fixture.Session.Snapshot();
        var resolver = new LegacyProjectResolver(fixture.Env);
        var plan = AuthoredBuildPlanner.Plan(project,
            new ProductionAuthoredBuildBackend(resolver.ResolvePart));
        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts.Concat(plan.Bindings
            .Where(binding => binding.Decision.BlocksBuild)
            .Select(binding => $"{binding.RowId}: {binding.Decision.Reason}"))));
        return ModBuilder.Build(AuthoredBuildExecution.Create(project, plan), fixture.Env,
            fixture.OutRoot, zip: false);
    }

    private static int CountOf(string text, string token)
    {
        int count = 0;
        for (int index = text.IndexOf(token, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
