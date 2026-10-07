using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>A picture on an original map changes every place the outfit draws that map, which is what the
/// card's consent tells the modder. The game-wide rebind reaches them by itself; the draw-scoped route, taken
/// when another outfit also wears the map, has to anchor on every part of the outfit that draws it. Driven
/// through the authored session, the planner and the build, as the app drives them.</summary>
public sealed class OutfitTextureReachTests : IDisposable
{
    private readonly ModBuilderTests _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public void A_picture_on_one_part_binds_at_every_part_of_the_outfit_that_draws_its_map()
    {
        var fixture = Fixture(ClothMap.SameTexture);
        Author(fixture, BaseColor, "skin.png", new Rgba32(10, 20, 30, 255));

        string ini = Ini(Build(fixture));

        Assert.DoesNotContain("[TextureOverride_Retex_", ini);
        var anchored = ScopedAnchorHashes(ini);
        Assert.Equal(new[] { fixture.BodyLod0, fixture.BodyLod1, fixture.ClothLod0 }.Order(), anchored.Order());
        // one image shipped, bound at each anchor behind that anchor's own probe
        Assert.DoesNotContain("[Resource_Rtx1]", ini);
        Assert.Equal(3, CountOf(ini, "if $zz_rslot == 0\nps-t0 = Resource_Rtx0\n$zz_bt0 = 1\nendif\n"));
    }

    [Fact]
    public void A_part_drawing_another_map_is_not_anchored()
    {
        var fixture = Fixture(ClothMap.OtherTexture);
        Author(fixture, BaseColor, "skin.png", new Rgba32(10, 20, 30, 255));

        string ini = Ini(Build(fixture));

        Assert.Equal(new[] { fixture.BodyLod0, fixture.BodyLod1 }.Order(), ScopedAnchorHashes(ini).Order());
    }

    [Fact]
    public void A_map_no_other_outfit_wears_keeps_one_game_wide_rebind()
    {
        var fixture = Fixture(ClothMap.SameTexture, otherOutfitWearsMap: false);
        Author(fixture, BaseColor, "skin.png", new Rgba32(10, 20, 30, 255));

        string ini = Ini(Build(fixture));

        Assert.Equal(1, CountOf(ini, "[TextureOverride_Retex_"));
        Assert.DoesNotContain("[TextureOverride_RetexScope_", ini);
    }

    [Fact]
    public void Two_parts_giving_one_shared_map_two_pictures_refuse_by_name()
    {
        var fixture = Fixture(ClothMap.SameTexture);
        Author(fixture, BaseColor, "skin.png", new Rgba32(10, 20, 30, 255));
        Author(fixture with { Edit = fixture.ClothEdit }, BaseColor, "cloth.png", new Rgba32(200, 20, 30, 255));

        var exception = Assert.Throws<InvalidOperationException>(() => Build(fixture));

        Assert.StartsWith("stock texture 'tex_body_d' is retextured with two different images on VesnaSSR01's ",
            exception.Message);
        Assert.EndsWith("One draw binds one image. Give both changes the same image", exception.Message);
    }

    [Fact]
    public void Two_parts_giving_one_shared_map_the_same_picture_bind_it_once_per_anchor()
    {
        var fixture = Fixture(ClothMap.SameTexture);
        string asset = Author(fixture, BaseColor, "skin.png", new Rgba32(10, 20, 30, 255));
        fixture.Session.ChooseProjectAsset(fixture.ClothEdit, Slot(fixture.Session, fixture.ClothEdit, BaseColor),
            asset);

        string ini = Ini(Build(fixture));

        Assert.Equal(3, ScopedAnchorHashes(ini).Count);
        Assert.Equal(3, CountOf(ini, "if $zz_rslot == 0\nps-t0 = Resource_Rtx0\n$zz_bt0 = 1\nendif\n"));
    }

    [Fact]
    public void A_part_binding_the_map_under_two_properties_refuses_the_property_edit_by_its_mesh()
    {
        var fixture = Fixture(ClothMap.SameTextureTwoProperties, bodyProperty: "_DetailAlbedo");
        Author(fixture, Property("_DetailAlbedo"), "weave.png", new Rgba32(10, 20, 30, 255));

        var exception = Assert.Throws<InvalidOperationException>(() => Build(fixture));

        Assert.Equal("The original texture 'tex_body_d' is used by Detail color and Detail mask on "
            + "'c_vesna01_cloth_lod0', so this edit cannot reach Detail color alone at this draw. "
            + "Leave this picture out, or change the texture for every slot that draws it with a game-wide edit.",
            exception.Message);
    }

    // ---- the world: the body (two tiers) and a cloth part (one tier) of one outfit --------------------------

    private enum ClothMap { SameTexture, OtherTexture, SameTextureTwoProperties }

    private sealed record TestFixture(BuildEnv Env, AuthoredEditSession Session, string Edit, string ClothEdit,
        string Root, string BodyLod0, string BodyLod1, string ClothLod0);

    private TestFixture Fixture(ClothMap clothMap, bool otherOutfitWearsMap = true,
        string bodyProperty = "_BaseMap")
    {
        var env = _world.MakeEnv(out string bodyLod0, out string bodyLod1);
        var original = env.ResolveSubject("Vesna", "VesnaSSR01")!;
        var body = Assert.Single(original.Parts);
        var bodyMaterial = Assert.Single(body.Materials);
        var stock = Assert.Single(bodyMaterial.Maps);
        string root = _world.NewProject("Reach").RootDir!;

        // the cloth's own index buffer: a mesh of its own triangles
        string clothFile = Path.Combine(root, "cloth.bundle");
        SyntheticBundle.BuildOneMesh(clothFile, "c_vesna01_cloth_lod0",
            new float[] { 0, 0, 0, 2, 0, 0, 0, 2, 0, 2, 2, 0, 1, 3, 0 }, new[] { 0, 1, 2, 2, 1, 3, 2, 3, 4 });
        byte[] clothBundle = File.ReadAllBytes(clothFile);
        string clothLod0 = BufferHash.Compute(clothBundle, "c_vesna01_cloth_lod0").Ib.ToString("x8");
        var bundles = new Dictionary<string, byte[]> { ["bundleCloth"] = clothBundle };
        IReadOnlyList<SubjectMap> clothMaps;
        switch (clothMap)
        {
            case ClothMap.SameTexture:
                clothMaps = new[] { stock };
                break;
            case ClothMap.SameTextureTwoProperties:
                clothMaps = new[] { stock with { Slot = "_DetailAlbedo" }, stock with { Slot = "_DetailMask" } };
                break;
            default:
                string otherFile = Path.Combine(root, "cloth_d.bundle");
                long pathId = SyntheticBundle.BuildOneTexture(otherFile, "tex_cloth_d", 8, 8, 40, 80, 120, 255);
                bundles["bundleClothD"] = File.ReadAllBytes(otherFile);
                clothMaps = new[] { new SubjectMap("_BaseMap", "tex_cloth_d", "bundleClothD", pathId) };
                break;
        }
        var model = original with
        {
            Parts = new[]
            {
                body with
                {
                    Materials = new[] { bodyMaterial with { Maps = new[] { stock with { Slot = bodyProperty } } } },
                },
                new SubjectPart("cloth", "c_vesna01_cloth_lod0", "addr_cloth",
                    new[] { new SubjectMaterial("m_cloth", 1, "cab-cloth", clothMaps) }),
            },
        };
        var resolveAddress = env.ResolveAddress;
        var deobfuscate = env.Deobfuscate;
        env = (env with
        {
            ResolveSubject = (character, outfit) => character == "Vesna" && outfit == "VesnaSSR01" ? model : null,
            ResolveAddress = address => address == "addr_cloth" ? "bundleCloth" : resolveAddress(address),
            Deobfuscate = id => bundles.TryGetValue(id, out var bytes) ? bytes : deobfuscate(id),
            Sharing = SharingIndex.FromMeasurements("12345",
                new[]
                {
                    new SharingIndex.Wearer("Vesna", "Vesna", "VesnaSSR01", null),
                    new SharingIndex.Wearer("Karst", "Karst", "KarstDorm", null),
                },
                new Dictionary<string, int[]>
                {
                    [_world.StockTexHash] = otherOutfitWearsMap ? new[] { 0, 1 } : new[] { 0 },
                },
                new Dictionary<string, int[]>(), new Dictionary<int, string[]>()),
            ShaderSlotCatalogFile = Path.Combine(AppContext.BaseDirectory, "data", "charps_slots.json"),
        }).Exact();

        var resolver = new LegacyProjectResolver(env);
        var session = new AuthoredEditSession(new AuthoredProject { RootDir = root, TransportRoot = Path.Combine(root, "round-trips") });
        session.SetWorkspaceIndex(new AuthoredWorkspaceIndex
        {
            Selection = new List<SelectionEntry> { new() { Character = "Vesna", Outfit = "VesnaSSR01" } },
        });
        string EditOn(string renderer)
        {
            var target = new TargetPart { Subject = "Vesna", Outfit = "VesnaSSR01", RendererSlot = renderer };
            session.EnsurePartSlots(target, resolver.ResolvePart);
            return session.CreateEdit(target);
        }
        return new TestFixture(env, session, EditOn("c_vesna01_body_lod0"), EditOn("c_vesna01_cloth_lod0"),
            root, bodyLod0, bodyLod1, clothLod0);
    }

    private static readonly Func<TargetSlot, bool> BaseColor = slot => slot.Input == TargetInputKind.BaseColor;

    private static Func<TargetSlot, bool> Property(string property) =>
        slot => string.Equals(slot.ShaderProperty, property, StringComparison.Ordinal);

    private static string Author(TestFixture fixture, Func<TargetSlot, bool> which, string file, Rgba32 colour)
    {
        string source = Path.Combine(fixture.Root, file);
        using (var image = new Image<Rgba32>(8, 8, colour)) image.SaveAsPng(source);
        string slot = Slot(fixture.Session, fixture.Edit, which);
        var ingress = ProjectAssetIngress.Begin(fixture.Session.Snapshot(), fixture.Edit, slot, source);
        var published = fixture.Session.PublishAssetForBinding(ingress, ProjectAssetKind.Picture,
            Path.GetFileNameWithoutExtension(file), ProjectAssetIngress.Png);
        Assert.Equal(ProjectAssetPublishResult.Published, published.Result);
        return published.ProjectAssetId!;
    }

    private static string Slot(AuthoredEditSession session, string edit, Func<TargetSlot, bool> which)
    {
        var project = session.Snapshot();
        var part = project.EditDefinitions.Single(definition => definition.Id == edit).Target;
        return project.TargetSlots.Single(slot => slot.Domain == TargetSlotDomain.Game
            && slot.Part.RendererSlot == part.RendererSlot && which(slot)).Id;
    }

    private ModBuilder.Result Build(TestFixture fixture)
    {
        var project = fixture.Session.Snapshot();
        var resolver = new LegacyProjectResolver(fixture.Env);
        var plan = AuthoredBuildPlanner.Plan(project, new ProductionAuthoredBuildBackend(resolver.ResolvePart));
        Assert.True(plan.CanBuild, string.Join("; ", plan.Conflicts.Concat(plan.Bindings
            .Where(binding => binding.Decision.BlocksBuild)
            .Select(binding => $"{binding.RowId}: {binding.Decision.Reason}"))));
        return ModBuilder.Build(AuthoredBuildExecution.Create(project, plan), fixture.Env, _world.OutRoot,
            zip: false);
    }

    private static string Ini(ModBuilder.Result result)
    {
        string ini = File.ReadAllText(Path.Combine(result.OutDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        return ini;
    }

    /// <summary>The index-buffer hash of every draw-scoped retexture section in the ini.</summary>
    private static List<string> ScopedAnchorHashes(string ini) => ini
        .Split("[TextureOverride_RetexScope_", StringSplitOptions.None).Skip(1)
        .Select(section => section.Split("\nhash = ")[1].Split('\n')[0])
        .ToList();

    private static int CountOf(string text, string token)
    {
        int count = 0;
        for (int index = text.IndexOf(token, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
