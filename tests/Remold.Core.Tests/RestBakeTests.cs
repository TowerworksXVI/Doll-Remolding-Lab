using System;
using System.Numerics;
using Remold.Core.Mesh;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Prefab bodies that ship lying down get their measured bind→scene rotation snapped to an EXACT
/// axis-aligned matrix, baked into the editable glb and un-baked at package build. The load-bearing
/// property is BIT-EXACTNESS: the snapped rotation is all component swaps and negations, so an unedited
/// round-trip recovers the original Unity floats byte-for-byte.
/// </summary>
public class RestBakeTests
{
    /// <summary>−90° about X with the float noise a real measurement carries.</summary>
    private static Matrix4x4 NoisyUpright()
    {
        var m = Matrix4x4.CreateRotationX(-MathF.PI / 2);   // cos(π/2) ≈ −4e-8, not exactly 0
        m.M41 = 3e-6f; m.M42 = -2e-7f;                      // translation noise as measured
        return m;
    }

    private static readonly Matrix4x4 ExactUpright = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    [Fact]
    public void Snap_ReducesNoisyQuarterTurn_ToExactIntegerRotation()
    {
        var snapped = RestBake.Snap(NoisyUpright());
        Assert.NotNull(snapped);
        Assert.Equal(ExactUpright, snapped!.Value);   // exact equality — every entry snapped to {-1,0,1}
    }

    [Fact]
    public void Snap_Identity_ReturnsNull_NothingToBake()
    {
        var noisyIdentity = Matrix4x4.Identity;
        noisyIdentity.M12 = 1e-6f; noisyIdentity.M43 = -4e-7f;
        Assert.Null(RestBake.Snap(noisyIdentity));
    }

    [Fact]
    public void Snap_DropsMillimetreTranslationNoise_ButKeepsTheRotation()
    {
        // A face part: a clean −90°X uprighting with ~1mm of noise on the translation must still snap, the
        // noise dropped rather than baked. Rejecting it leaves that face lying down while its body stands up.
        var g = Matrix4x4.CreateRotationX(-MathF.PI / 2);
        g.M42 = 0.0013f;
        var snapped = RestBake.Snap(g);
        Assert.NotNull(snapped);
        Assert.Equal(ExactUpright, snapped!.Value);
        Assert.Equal(Vector3.Zero, snapped.Value.Translation);   // noise dropped, nothing baked from it
    }

    [Fact]
    public void Snap_RejectsWhatIsntACleanQuarterTurn()
    {
        // a real translation (a weapon mount offset) — orientation fine, don't bake
        var offset = Matrix4x4.CreateTranslation(-0.376f, 0.052f, 0.244f);
        Assert.Null(RestBake.Snap(offset));

        // a 45° rotation — entries at ±0.707, not on the integer lattice
        Assert.Null(RestBake.Snap(Matrix4x4.CreateRotationX(MathF.PI / 4)));

        // a reflection (det −1) — never a valid scene rest
        var mirror = Matrix4x4.CreateScale(-1, 1, 1);
        Assert.Null(RestBake.Snap(mirror));

        // scale 2 — one entry off the lattice
        Assert.Null(RestBake.Snap(Matrix4x4.CreateScale(2)));
    }

    [Fact]
    public void ApplyThenUnapply_IsBitExact_OnEveryDirectionalChannel()
    {
        var mesh = new UnityMesh
        {
            Name = "m",
            VertexCount = 3,
            Channels = new()
            {
                // deliberately awkward floats — subnormals-adjacent, negatives, repeating fractions
                ["Vertex"] = new[] { 0.1234567f, -0.9999999f, 1e-30f, 3.3333333f, -7e12f, 0.5f, 1f / 3f, -2f / 7f, 0.7071068f },
                ["Normal"] = new[] { 0.7071068f, -0.7071068f, 0f, 0.1f, 0.2f, 0.3f, -1f, 0f, 1e-8f },
                ["Tangent"] = new[] { 0.5f, -0.5f, 0.5f, -1f, 0.1f, 0.9f, -0.3f, 1f, 0.25f, 0.25f, 0.25f, -1f },
                ["TexCoord0"] = new[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f },
                ["Color"] = new[] { 1f, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 1 },
            },
            Dims = new() { ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2, ["Color"] = 4 },
            Submeshes = new() { new[] { 0, 1, 2 } },
        };
        var g = RestBake.Snap(NoisyUpright())!.Value;

        var baked = RestBake.Apply(mesh, g);
        Assert.NotEqual(mesh.Channels["Vertex"], baked.Channels["Vertex"]);   // it DID rotate

        var restored = RestBake.Unapply(baked, g);
        Assert.Equal(mesh.Channels["Vertex"], restored.Channels["Vertex"]);   // exact float[] equality
        Assert.Equal(mesh.Channels["Normal"], restored.Channels["Normal"]);
        Assert.Equal(mesh.Channels["Tangent"], restored.Channels["Tangent"]);
    }

    [Fact]
    public void Apply_RotatesAPacked4WideNormal_LeavingTheFourthComponent()
    {
        // A packed 0x34 Normal stores 4 components (xyz + a zero pad) and must rotate like Tangent's xyz;
        // rotating only the dim==3 case leaves a lying-down rig's packed normals un-rotated.
        var mesh = new UnityMesh
        {
            Name = "m",
            VertexCount = 1,
            Channels = new()
            {
                ["Vertex"] = new[] { 0f, 1f, 0f },
                ["Normal"] = new[] { 0f, 1f, 0f, 0f },   // 4-wide, 4th is the zero pad
            },
            Dims = new() { ["Vertex"] = 3, ["Normal"] = 4 },
            Submeshes = new() { new[] { 0, 0, 0 } },
        };
        var baked = RestBake.Apply(mesh, ExactUpright);

        Assert.Equal(new[] { 0f, 0f, -1f, 0f }, baked.Channels["Normal"]);   // (0,1,0)·rotX(−90)=(0,0,−1), pad kept
        // and it round-trips bit-exact
        var restored = RestBake.Unapply(baked, ExactUpright);
        Assert.Equal(mesh.Channels["Normal"], restored.Channels["Normal"]);
    }

    [Fact]
    public void Apply_LeavesNonDirectionalChannels_AndTangentW_Untouched()
    {
        var mesh = new UnityMesh
        {
            Name = "m",
            VertexCount = 1,
            Channels = new()
            {
                ["Vertex"] = new[] { 0f, 1f, 0f },
                ["Tangent"] = new[] { 0f, 1f, 0f, -1f },
                ["TexCoord0"] = new[] { 0.25f, 0.75f },
                ["Color"] = new[] { 0.5f, 0.5f, 0.5f, 1f },
            },
            Dims = new() { ["Vertex"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2, ["Color"] = 4 },
            Submeshes = new() { new[] { 0, 0, 0 } },
        };
        var baked = RestBake.Apply(mesh, ExactUpright);

        Assert.Equal(new[] { 0f, 0f, -1f }, baked.Channels["Vertex"]);    // (0,1,0)·rotX(−90) = (0,0,−1)
        Assert.Equal(-1f, baked.Channels["Tangent"][3]);                  // handedness sign untouched
        Assert.Same(mesh.Channels["TexCoord0"], baked.Channels["TexCoord0"]);   // UVs pass through by reference
        Assert.Same(mesh.Channels["Color"], baked.Channels["Color"]);
        Assert.Same(mesh.Submeshes, baked.Submeshes);
    }

    [Fact]
    public void ListRoundTrip_AndTolerantFromList()
    {
        var g = ExactUpright;
        Assert.Equal(g, RestBake.FromList(RestBake.ToList(g), out bool refusedRound));
        Assert.False(refusedRound);
        Assert.Null(RestBake.FromList(null, out _));                          // no bake recorded
        Assert.Null(RestBake.FromList(new float[3], out _));                  // malformed → treated as none
        Assert.Null(RestBake.FromList(RestBake.ToList(Matrix4x4.Identity), out bool refusedIdentity));
        Assert.False(refusedIdentity);                                        // identity → nothing to do, not a refusal
    }
}
