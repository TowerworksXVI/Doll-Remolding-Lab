using System.Collections.Generic;
using System.Numerics;
using Remold.Core.Mesh;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>Where a part the game starts shrunk opens in Blender, and the shift that puts it there.</summary>
public class HiddenPartTests
{
    private static UnityMesh Mesh(params float[] positions) => new()
    {
        Name = "prop", VertexCount = positions.Length / 3,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = positions,
            ["Normal"] = new float[positions.Length],
        },
        Dims = new Dictionary<string, int> { ["Vertex"] = 3, ["Normal"] = 3 },
        Submeshes = new List<int[]> { new[] { 0 } },
    };

    /// <summary>The centre is the middle of the stock geometry's bounding box in the space the export
    /// writes it in, after the part's uprighting, so the centred part sits on the origin in Blender.</summary>
    [Fact]
    public void The_centre_is_the_middle_of_the_uprighted_bounding_box()
    {
        var stock = Mesh(1, 2, 3, 3, 6, -1, 2, 4, 5);
        Assert.Equal(new Vector3(2, 4, 2), HiddenPart.Centre(stock, null));

        // −90° about X: (x, y, z) → (x, z, −y)
        var upright = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        Assert.Equal(new Vector3(2, 2, -4), HiddenPart.Centre(stock, upright));
    }

    /// <summary>The display placement moves a vertex by the uprighting and then by minus the centre, so the
    /// shifted mesh and a joint composed with it land in one space.</summary>
    [Fact]
    public void The_display_placement_uprights_then_shifts_by_the_centre()
    {
        var upright = new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
        var centre = new Vector3(2, 2, -4);
        var q = HiddenPart.Display(upright, centre);
        Assert.Equal(new Vector3(-1, 1, 2), Vector3.Transform(new Vector3(1, 2, 3), q));
        Assert.Equal(new Vector3(-2, -2, 4), q.Translation);
        Assert.Null(HiddenPart.For(Matrix4x4.CreateScale(0.6f), () => Mesh(0, 0, 0), null));
        Assert.Equal(new Vector3(1, 1, 1),
            HiddenPart.For(Matrix4x4.CreateScale(0.01f), () => Mesh(0, 0, 0, 2, 2, 2), null)!.Value.Centre);
    }

    /// <summary>The shift moves positions only, and undoing it gives back the floats it was handed wherever
    /// the arithmetic leaves no rounding behind.</summary>
    [Fact]
    public void A_shift_moves_positions_only_and_undoes_exactly()
    {
        var stock = Mesh(0.5f, 1.25f, -2f, 0.75f, 1.5f, -1f);
        stock.Channels["Normal"] = new float[] { 0, 1, 0, 1, 0, 0 };
        var centre = new Vector3(0.625f, 1.375f, -1.5f);

        var shifted = RestBake.Shift(stock, centre);
        Assert.Equal(new[] { -0.125f, -0.125f, -0.5f, 0.125f, 0.125f, 0.5f }, shifted.Channels["Vertex"]);
        Assert.Same(stock.Channels["Normal"], shifted.Channels["Normal"]);
        Assert.Equal(stock.Channels["Vertex"], RestBake.Unshift(shifted, centre).Channels["Vertex"]);
    }

    /// <summary>A part whose placement can't be read, or whose bundle can't be read, is not said to start
    /// hidden: the row note is information, and a read that failed states nothing.</summary>
    [Fact]
    public void A_part_whose_placement_cannot_be_read_is_not_said_to_start_hidden()
    {
        using var g = new Support.TempGame();
        string file = g.At("prop.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(file, "prop1_lod0", new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0 },
            new[] { 0, 1, 2 }, new[] { 7u });
        var bytes = System.IO.File.ReadAllBytes(file);
        var gate = new HiddenPartGate(id => id == "prop" ? bytes : null);

        Assert.False(gate.StartsHidden("prop", "prop1_lod0", 0, rendererBundle: null, 0, pose: null));
        Assert.False(gate.StartsHidden("gone", "prop1_lod0", 0, rendererBundle: "prop", 0, pose: null));
    }

    /// <summary>A part whose placement shrinks it is said to start hidden, and a part whose placement does
    /// not is said not to; both answers are kept, so a second ask reads neither the part's bundle nor its
    /// placement again.</summary>
    [Fact]
    public void A_part_the_placement_shrinks_is_said_to_start_hidden_and_each_answer_is_kept()
    {
        using var g = new Support.TempGame();
        string file = g.At("prop.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(file, "prop1_lod0", new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0 },
            new[] { 0, 1, 2 }, new[] { 7u });
        var bytes = System.IO.File.ReadAllBytes(file);
        int reads = 0, placements = 0;
        var gate = new HiddenPartGate(id => { reads++; return id == "prop" ? bytes : null; }, null,
            (renderer, _, _, _) =>
            {
                placements++;
                return new RigPlacement.Placed(renderer == "shrunk"
                    ? Matrix4x4.CreateScale(0.01f) * Matrix4x4.CreateTranslation(0.5f, 1.25f, 0)
                    : Matrix4x4.Identity, null, null);
            });

        Assert.True(gate.StartsHidden("prop", "prop1_lod0", 0, "shrunk", 5, pose: null));
        Assert.True(gate.StartsHidden("prop", "prop1_lod0", 0, "shrunk", 5, pose: null));
        Assert.Equal((1, 1), (reads, placements));
        Assert.False(gate.StartsHidden("prop", "prop1_lod0", 0, "shown", 6, pose: null));
        Assert.False(gate.StartsHidden("prop", "prop1_lod0", 0, "shown", 6, pose: null));
        Assert.Equal((2, 2), (reads, placements));
    }

    /// <summary>The persisted shift record is three floats; anything else states no shift.</summary>
    [Fact]
    public void A_shift_record_is_three_floats_or_nothing()
    {
        var centre = new Vector3(0.1f, -2f, 3.5f);
        Assert.Equal(centre, HiddenPart.FromList(HiddenPart.ToList(centre)));
        Assert.Null(HiddenPart.FromList(null));
        Assert.Null(HiddenPart.FromList(new[] { 1f, 2f }));
    }
}
