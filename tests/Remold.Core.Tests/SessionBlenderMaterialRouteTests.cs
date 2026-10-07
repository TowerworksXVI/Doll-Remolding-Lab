using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.App.ViewModels;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

public sealed class SessionBlenderMaterialRouteTests
{
    [Fact]
    public void Blender_material_intake_binds_only_the_addressed_submesh_slot()
    {
        using var game = new TempGame();
        var session = Session(game, submeshes: 2);
        string picture = WritePng(game.At("returned.png"), new Rgba32(12, 34, 56, 255));
        int landed = 0;

        int published = PublishMaps(session,
            new[] { new SubmeshTextures { Submesh = 0, Albedo = picture } }, () => landed++);

        Assert.Equal(1, published);
        Assert.Equal(1, landed);
        var baseSlots = session.Slots("edit-long").Where(state =>
            state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.Input == TargetInputKind.BaseColor).OrderBy(state => state.Slot.SubmeshIndex).ToList();
        Assert.Equal(BindingKind.ProjectAsset, baseSlots[0].Binding.Kind);
        Assert.NotNull(baseSlots[0].ProjectAsset);
        Assert.Equal(BindingKind.InheritedLiveCarrier, baseSlots[1].Binding.Kind);
        Assert.Null(baseSlots[1].ProjectAsset);
        Assert.Single(session.Slots("edit-long"), state =>
            state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Binding.Kind == BindingKind.ProjectAsset);
    }

    [Fact]
    public void Blender_material_intake_never_publishes_onto_a_game_domain_slot()
    {
        using var game = new TempGame();
        var session = Session(game, submeshes: 1);
        string picture = WritePng(game.At("returned-game-domain.png"), new Rgba32(12, 34, 56, 255));
        var before = session.Slots("edit-long").Single(state => state.Slot.Id == "slot-base");
        Assert.Equal(TargetSlotDomain.Game, before.Slot.Domain);
        Assert.Equal(BindingKind.TargetGameValue, before.Binding.Kind);

        Assert.Equal(1, PublishMaps(session,
            new[] { new SubmeshTextures { Submesh = 0, Albedo = picture } }));

        var after = session.Slots("edit-long").Single(state => state.Slot.Id == "slot-base");
        Assert.Equal(TargetSlotDomain.Game, after.Slot.Domain);
        Assert.Equal(BindingKind.TargetGameValue, after.Binding.Kind);
        Assert.Null(after.Binding.ProjectAssetId);
        Assert.Null(after.ProjectAsset);
    }

    [Fact]
    public void Two_blender_materials_remain_independent_after_save_and_reopen()
    {
        using var game = new TempGame();
        var session = Session(game, submeshes: 2);
        string red = WritePng(game.At("red.png"), new Rgba32(200, 1, 2, 255));
        string blue = WritePng(game.At("blue.png"), new Rgba32(3, 4, 210, 255));

        Assert.Equal(2, PublishMaps(session, new[]
        {
            new SubmeshTextures { Submesh = 0, Albedo = red },
            new SubmeshTextures { Submesh = 1, Albedo = blue },
        }));
        AuthoredProjectSerializer.Save(session.Snapshot(), game.Root);
        var reopened = new AuthoredEditSession(AuthoredProjectSerializer.Load(game.Root));

        var baseSlots = reopened.Slots("edit-long").Where(state =>
            state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.Input == TargetInputKind.BaseColor).OrderBy(state => state.Slot.SubmeshIndex).ToList();
        Assert.Equal(2, baseSlots.Count);
        Assert.NotEqual(baseSlots[0].Binding.ProjectAssetId, baseSlots[1].Binding.ProjectAssetId);
        Assert.Equal(new Rgba32(200, 1, 2, 255), FirstPixel(game.At(baseSlots[0].ProjectAsset!.File)));
        Assert.Equal(new Rgba32(3, 4, 210, 255), FirstPixel(game.At(baseSlots[1].ProjectAsset!.File)));
    }

    [Fact]
    public void Blender_rmo_intake_binds_its_alpha_answer_to_the_same_exact_submesh()
    {
        using var game = new TempGame();
        var session = Session(game, submeshes: 2);
        string rmo = WritePng(game.At("rmo.png"), new Rgba32(7, 8, 9, 10));

        Assert.Equal(1, PublishMaps(session, new[]
        {
            new SubmeshTextures
            {
                Submesh = 1,
                Rmo = rmo,
                RmoAlpha = RmoAlphaAnswer.Rebuild,
            },
        }));

        var states = session.Slots("edit-long");
        var rmoState = states.Single(state => state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.SubmeshIndex == 1 && state.Slot.Input == TargetInputKind.Rmo);
        var alphaState = states.Single(state => state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.SubmeshIndex == 1 && state.Slot.Input == TargetInputKind.RmoAlpha);
        Assert.Equal("rebuild-from-stock", alphaState.ProjectAsset!.Value!.Value);
        Assert.Equal(rmoState.ProjectAsset!.Id, alphaState.ProjectAsset.Source!.ProjectAssetId);
        Assert.Equal(rmoState.ProjectAsset.File, alphaState.ProjectAsset.File);
        Assert.Equal(BindingKind.InheritedLiveCarrier, states.Single(state =>
            state.Slot.Domain == TargetSlotDomain.EditOutput && state.Slot.SubmeshIndex == 0
            && state.Slot.Input == TargetInputKind.RmoAlpha).Binding.Kind);
    }

    [Fact]
    public void Blender_effect_and_generic_pictures_publish_to_their_exact_property_slots()
    {
        using var game = new TempGame();
        var session = Session(game, submeshes: 2);
        string effect = WritePng(game.At("effect.png"), new Rgba32(31, 32, 33, 255));
        string mask = WritePng(game.At("mask.png"), new Rgba32(71, 72, 73, 255));

        Assert.Equal(2, PublishMaps(session, new[]
        {
            new SubmeshTextures
            {
                Submesh = 0,
                Blend = effect,
                Textures = new()
                {
                    new PropertyTextureBinding { ShaderProperty = "_MaskTex", File = mask },
                },
            },
        }));

        var states = session.Slots("edit-long");
        var effectState = states.Single(state => state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.SubmeshIndex == 0 && state.Slot.Input == TargetInputKind.Blend);
        var maskState = states.Single(state => state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.SubmeshIndex == 0 && state.Slot.Input == TargetInputKind.Texture
            && state.Slot.ShaderProperty == "_MaskTex");
        Assert.Equal(BindingKind.ProjectAsset, effectState.Binding.Kind);
        Assert.Equal(BindingKind.ProjectAsset, maskState.Binding.Kind);
        Assert.Equal(new Rgba32(31, 32, 33, 255), FirstPixel(game.At(effectState.ProjectAsset!.File)));
        Assert.Equal(new Rgba32(71, 72, 73, 255), FirstPixel(game.At(maskState.ProjectAsset!.File)));
    }

    /// <summary>A second send that changes only the base colour brings the normal and RMO back as the
    /// session sent them: the modder's own pictures, untouched. Untouched is not an ask, and it is not the
    /// game's map either — the slots keep the assets the first send authored, and the RMO keeps the alpha
    /// answer recorded with it. Reading that answer as "bind the game's map" is how a rebuilt mod lost its
    /// authored relief while the editor still looked right.</summary>
    [Fact]
    public void An_untouched_authored_map_keeps_its_asset_and_alpha_answer_through_a_base_only_return()
    {
        using var game = new TempGame();
        var session = Session(game, submeshes: 2);
        string normal = WritePng(game.At("normal.png"), new Rgba32(1, 2, 3, 255));
        string rmo = WritePng(game.At("rmo.png"), new Rgba32(7, 8, 9, 10));
        string picture = WritePng(game.At("base.png"), new Rgba32(12, 34, 56, 255));
        Assert.Equal(2, PublishMaps(session, new[]
        {
            new SubmeshTextures { Submesh = 0, Normal = normal, Rmo = rmo, RmoAlpha = RmoAlphaAnswer.Rebuild },
        }));
        var before = Bindings(session, submesh: 0);
        Assert.Equal(BindingKind.ProjectAsset, before[(TargetInputKind.Normal, "_BumpMap")].Kind);
        Assert.Equal(BindingKind.ProjectAsset, before[(TargetInputKind.RmoAlpha, "")].Kind);

        int published = PublishMaps(session, new[]
        {
            new SubmeshTextures
            {
                Submesh = 0, Albedo = picture,
                NormalOrigin = SlotOrigin.Untouched, RmoOrigin = SlotOrigin.Untouched,
            },
        });

        Assert.Equal(1, published);
        var after = Bindings(session, submesh: 0);
        Assert.Equal(BindingKind.ProjectAsset, after[(TargetInputKind.BaseColor, "_BaseMap")].Kind);
        Assert.NotEqual(before[(TargetInputKind.BaseColor, "_BaseMap")], after[(TargetInputKind.BaseColor, "_BaseMap")]);
        Assert.Equal(before[(TargetInputKind.Normal, "_BumpMap")], after[(TargetInputKind.Normal, "_BumpMap")]);
        Assert.Equal(before[(TargetInputKind.Rmo, "_RMOTex")], after[(TargetInputKind.Rmo, "_RMOTex")]);
        Assert.Equal(before[(TargetInputKind.RmoAlpha, "")], after[(TargetInputKind.RmoAlpha, "")]);
        Assert.Equal("rebuild-from-stock", EditOutput(session, 0, TargetInputKind.RmoAlpha).ProjectAsset!.Value!.Value);
    }

    /// <summary>A neutral chosen in the Lab and a link to another slot go to Blender as the game's picture
    /// — the launch carries only the modder's own files — and come back untouched. Untouched asks nothing,
    /// so both choices stand, on the submesh the return changed and on the one it did not.</summary>
    [Fact]
    public void An_untouched_return_keeps_a_neutral_and_a_link_chosen_in_the_lab()
    {
        using var game = new TempGame();
        var session = Session(game, submeshes: 2);
        string picture = WritePng(game.At("base.png"), new Rgba32(12, 34, 56, 255));
        session.ChooseNeutral("edit-long", EditOutput(session, 1, TargetInputKind.Normal).Slot.Id);
        session.ChooseSourceSlot("edit-long", EditOutput(session, 1, TargetInputKind.BaseColor).Slot.Id,
            EditOutput(session, 0, TargetInputKind.BaseColor).Slot.Id, "edit-long");
        var before = Bindings(session, submesh: 1);
        Assert.Equal(BindingKind.Neutral, before[(TargetInputKind.Normal, "_BumpMap")].Kind);
        Assert.Equal(BindingKind.SourceSlot, before[(TargetInputKind.BaseColor, "_BaseMap")].Kind);

        Assert.Equal(1, PublishMaps(session, new[]
        {
            new SubmeshTextures { Submesh = 0, Albedo = picture },
            new SubmeshTextures
            {
                Submesh = 1, AlbedoOrigin = SlotOrigin.Untouched, NormalOrigin = SlotOrigin.Untouched,
                RmoOrigin = SlotOrigin.Untouched,
            },
        }));

        AssertSame(before, Bindings(session, submesh: 1));
    }

    /// <summary>A slot that comes back with no picture at all keeps its old meaning. Beside an ask on the
    /// same submesh the normal and RMO go flat; alone, the slot inherits — the modder unlinked the image,
    /// and nothing untouched-shaped is read into that silence.</summary>
    [Fact]
    public void A_slot_returned_without_a_picture_still_blanks_beside_an_ask_and_inherits_alone()
    {
        using var game = new TempGame();
        var session = Session(game, submeshes: 2);
        string normal = WritePng(game.At("normal.png"), new Rgba32(1, 2, 3, 255));
        string rmo = WritePng(game.At("rmo.png"), new Rgba32(7, 8, 9, 10));
        string picture = WritePng(game.At("base.png"), new Rgba32(12, 34, 56, 255));
        Assert.Equal(4, PublishMaps(session, new[]
        {
            new SubmeshTextures { Submesh = 0, Normal = normal, Rmo = rmo },
            new SubmeshTextures { Submesh = 1, Normal = normal, Rmo = rmo },
        }));
        var rmo1 = Bindings(session, submesh: 1)[(TargetInputKind.Rmo, "_RMOTex")];

        PublishMaps(session, new[]
        {
            new SubmeshTextures { Submesh = 0, Albedo = picture },
            new SubmeshTextures { Submesh = 1, RmoOrigin = SlotOrigin.Untouched },
        });

        var first = Bindings(session, submesh: 0);
        Assert.Equal(BindingKind.Neutral, first[(TargetInputKind.Normal, "_BumpMap")].Kind);
        Assert.Equal(BindingKind.Neutral, first[(TargetInputKind.Rmo, "_RMOTex")].Kind);
        var second = Bindings(session, submesh: 1);
        Assert.Equal(BindingKind.InheritedLiveCarrier, second[(TargetInputKind.Normal, "_BumpMap")].Kind);
        Assert.Equal(rmo1, second[(TargetInputKind.Rmo, "_RMOTex")]);
    }

    /// <summary>A replacement submesh whose base colour is the modder's own draws on the replacement's UVs.
    /// A normal that comes back with no picture beside that base goes flat — the original relief through
    /// those UVs is not what Blender showed — whether the base was painted this send or kept from the last.
    /// Beside the original base the same silence inherits, as it always did.</summary>
    [Fact]
    public void A_slot_without_a_picture_beside_a_kept_authored_base_goes_flat()
    {
        using var game = new TempGame();
        var session = Session(game, submeshes: 2);
        string picture = WritePng(game.At("base.png"), new Rgba32(12, 34, 56, 255));
        string normal = WritePng(game.At("normal.png"), new Rgba32(1, 2, 3, 255));
        Assert.Equal(3, PublishMaps(session, new[]
        {
            new SubmeshTextures { Submesh = 0, Albedo = picture, Normal = normal },
            new SubmeshTextures { Submesh = 1, Normal = normal },
        }));
        Assert.Equal(BindingKind.ProjectAsset, Bindings(session, 0)[(TargetInputKind.Normal, "_BumpMap")].Kind);

        PublishMaps(session, new[]
        {
            new SubmeshTextures { Submesh = 0, AlbedoOrigin = SlotOrigin.Untouched, RmoOrigin = SlotOrigin.Untouched },
            new SubmeshTextures { Submesh = 1, AlbedoOrigin = SlotOrigin.Untouched, RmoOrigin = SlotOrigin.Untouched },
        });

        Assert.Equal(BindingKind.Neutral, Bindings(session, 0)[(TargetInputKind.Normal, "_BumpMap")].Kind);
        Assert.Equal(BindingKind.InheritedLiveCarrier, Bindings(session, 1)[(TargetInputKind.Normal, "_BumpMap")].Kind);
    }

    /// <summary>A generic property's picture is kept by the same rule as the fixed slots': untouched keeps
    /// the asset the property already binds.</summary>
    [Fact]
    public void An_untouched_generic_picture_keeps_its_asset()
    {
        using var game = new TempGame();
        var session = Session(game, submeshes: 2);
        string mask = WritePng(game.At("mask.png"), new Rgba32(71, 72, 73, 255));
        string picture = WritePng(game.At("base.png"), new Rgba32(12, 34, 56, 255));
        Assert.Equal(1, PublishMaps(session, new[]
        {
            new SubmeshTextures
            {
                Submesh = 0,
                Textures = new() { new PropertyTextureBinding { ShaderProperty = "_MaskTex", File = mask } },
            },
        }));
        var before = Bindings(session, submesh: 0)[(TargetInputKind.Texture, "_MaskTex")];
        Assert.Equal(BindingKind.ProjectAsset, before.Kind);

        Assert.Equal(1, PublishMaps(session, new[]
        {
            new SubmeshTextures
            {
                Submesh = 0, Albedo = picture,
                Textures = new()
                {
                    new PropertyTextureBinding { ShaderProperty = "_MaskTex", Origin = SlotOrigin.Untouched },
                },
            },
        }));

        Assert.Equal(before, Bindings(session, submesh: 0)[(TargetInputKind.Texture, "_MaskTex")]);
    }

    /// <summary>What one slot binds, as the facts a return must not move on a slot it asked nothing of.</summary>
    private readonly record struct Bound(BindingKind Kind, string? ProjectAssetId, string? SourceSlotId);

    private static Dictionary<(TargetInputKind Input, string Property), Bound> Bindings(
        AuthoredEditSession session, int submesh) =>
        session.Slots("edit-long")
            .Where(state => state.Slot.Domain == TargetSlotDomain.EditOutput && state.Slot.SubmeshIndex == submesh)
            .ToDictionary(state => (state.Slot.Input, state.Slot.ShaderProperty ?? ""),
                state => new Bound(state.Binding.Kind, state.Binding.ProjectAssetId,
                    state.Binding.SourceSlot?.SlotId));

    private static void AssertSame(Dictionary<(TargetInputKind Input, string Property), Bound> before,
        Dictionary<(TargetInputKind Input, string Property), Bound> after)
    {
        Assert.Equal(before.Count, after.Count);
        Assert.All(before, pair => Assert.Equal(pair.Value, after[pair.Key]));
    }

    private static EditSlotState EditOutput(AuthoredEditSession session, int submesh, TargetInputKind input) =>
        session.Slots("edit-long").Single(state => state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.SubmeshIndex == submesh && state.Slot.Input == input);

    /// <summary>The map route as the return itself takes it: inside one compound transaction, which is
    /// where every answer a send carries is written.</summary>
    private static int PublishMaps(AuthoredEditSession session, IReadOnlyList<SubmeshTextures> rows,
        Action? onPublished = null)
    {
        int published = 0;
        session.Compound(change => published =
            MainWindowViewModel.PublishBlenderMaps(change, "edit-long", 2, rows, null, onPublished));
        return published;
    }

    private static AuthoredEditSession Session(TempGame game, int submeshes)
    {
        var project = AuthoredEditFixtures.Saved();
        project.RootDir = game.Root;
        project.TransportRoot = Path.Combine(game.Root, "round-trips");
        var ramp = project.TargetSlots.Single(slot => slot.Id == "slot-ramp");
        ramp.ShaderProperty = "_RampMap";
        var installed = new[]
        {
            Slot("slot-base", TargetInputKind.BaseColor, "_BaseMap"),
            Slot("slot-normal", TargetInputKind.Normal, "_BumpMap"),
            Slot("slot-rmo", TargetInputKind.Rmo, "_RMOTex"),
            Slot("slot-blend", TargetInputKind.Blend, "_BlendTex"),
            Slot("slot-mask", TargetInputKind.Texture, "_MaskTex"),
        };
        project.TargetSlots.AddRange(installed);
        var edit = project.EditDefinitions.Single(candidate => candidate.Id == "edit-long");
        foreach (var slot in installed)
            edit.Bindings.Add(new Binding { SlotId = slot.Id, Kind = BindingKind.TargetGameValue });
        var session = new AuthoredEditSession(project);
        session.RecordReplacementOutputs("edit-long", submeshes);
        return session;

        TargetSlot Slot(string id, TargetInputKind input, string property) => new()
        {
            Id = id, Part = ramp.Part, Tier = ramp.Tier, SubmeshIndex = 0, MaterialSlotIndex = 0,
            Input = input, ShaderProperty = property, Renderer = ramp.Renderer, Material = ramp.Material,
        };
    }

    private static string WritePng(string path, Rgba32 color)
    {
        using var image = new Image<Rgba32>(4, 4, color);
        image.SaveAsPng(path);
        return path;
    }

    private static Rgba32 FirstPixel(string path)
    {
        using var image = Image.Load<Rgba32>(path);
        return image[0, 0];
    }
}
