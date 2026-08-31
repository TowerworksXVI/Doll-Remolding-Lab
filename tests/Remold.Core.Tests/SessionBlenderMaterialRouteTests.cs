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
