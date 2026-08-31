using System.Collections.Generic;
using System.IO;
using Remold.App.ViewModels;
using Remold.Core.Mesh;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The cacheability read behind the bare part's 3D preview: a game map the plan named whose
/// sampler came back null marks the render transiently degraded, and a degraded render may be shown but
/// never cached as the part's game-identity picture.</summary>
public class EditMeshPreviewPlanTests
{
    private static MeshPreviewRenderer.PreviewTexture Png()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(200, 100, 50, 255));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return MeshPreviewRenderer.PreviewTexture.TryFromPng(stream.ToArray())!.Value;
    }

    [Fact]
    public void A_planned_game_map_with_no_sampler_reads_as_missing()
    {
        var plan = new List<(bool Own, string? File, string? Bundle, string? Texture)>
        {
            (false, null, "bundle", "tex_a"),
            (false, null, "bundle", "tex_b"),
        };

        Assert.True(MainWindowViewModel.MissingExpectedGameMaps(plan, null));
        Assert.True(MainWindowViewModel.MissingExpectedGameMaps(plan,
            new MeshPreviewRenderer.PreviewTexture?[] { Png(), null }));
        Assert.True(MainWindowViewModel.MissingExpectedGameMaps(plan,
            new MeshPreviewRenderer.PreviewTexture?[] { Png() }));
        Assert.False(MainWindowViewModel.MissingExpectedGameMaps(plan,
            new MeshPreviewRenderer.PreviewTexture?[] { Png(), Png() }));
    }

    [Fact]
    public void Own_slots_and_unresolved_materials_expect_nothing()
    {
        var plan = new List<(bool Own, string? File, string? Bundle, string? Texture)>
        {
            (true, null, null, null),      // the modder's slot: untextured by their own choice
            (false, null, null, null),     // no base colour resolved for this material at all
        };

        Assert.False(MainWindowViewModel.MissingExpectedGameMaps(plan, null));
    }
}
