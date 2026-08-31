using System.Collections.Generic;
using System.Linq;
using Remold.Core.Mesh;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Import is the exact inverse of export: Unity→glTF is negate X + reverse winding, and back. A mesh that
/// goes out to a <c>.glb</c> and back comes home in canonical Unity space with its channels and triangles
/// intact — what guarantees a Blender edit lands in the left-handed space the game expects.
/// </summary>
public class MeshGltfRoundtripTests
{
    // 5 verts with position/normal/tangent/three UV sets, plus a Color the transport DROPS (the outline is re-baked at
    // package time). Asymmetric in X, so a missed or doubled negate-X shows up.
    private static UnityMesh Patch(List<int[]> submeshes) => new()
    {
        Name = "patch",
        VertexCount = 5,
        Channels = new Dictionary<string, float[]>
        {
            ["Vertex"] = new float[] { 0, 0, 0,  2, 0, 0,  0, 1, 0,  2, 1, 0,  1, 2, 0.5f },
            ["Normal"] = new float[] { 0, 0, 1,  0, 0, 1,  0, 0, 1,  0, 0, 1,  0, 0, 1 },
            ["Tangent"] = new float[] { 1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, -1 },
            ["TexCoord0"] = new float[] { 0, 0,  1, 0,  0, 1,  1, 1,  0.5f, 0.5f },
            ["TexCoord1"] = new float[] { 2, 3,  4, 3,  2, 5,  4, 5,  3, 4 },
            ["TexCoord2"] = new float[] { -1, 8,  0, 8,  -1, 9,  0, 9,  -0.5f, 8.5f },
            ["Color"] = new float[] { 0.1f, 0, 0, 1,  0.2f, 0, 0, 1,  0.3f, 0, 0, 1,  0.4f, 0, 0, 1,  0.5f, 0, 0, 1 },
        },
        Dims = new Dictionary<string, int>
        {
            ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4,
            ["TexCoord0"] = 2, ["TexCoord1"] = 2, ["TexCoord2"] = 2, ["Color"] = 4,
        },
        Submeshes = submeshes,
    };

    [Fact]
    public void ExportThenImport_SingleSubmesh_RecoversUnitySpaceExactly()
    {
        // One primitive → one shared vertex pool → every channel recovers byte-for-byte.
        var src = Patch(new List<int[]> { new[] { 0, 1, 2, 2, 1, 3, 2, 3, 4 } });
        using var g = new TempGame();
        var path = g.At("rt-single.glb");
        MeshGltf.ExportGlb(src, path);
        var back = MeshGltf.ImportGlb(path);

        Assert.Equal(src.VertexCount, back.VertexCount);
        foreach (var ch in new[] { "Vertex", "Normal", "Tangent", "TexCoord0", "TexCoord1", "TexCoord2" })
        {
            Assert.True(back.Has(ch), $"channel {ch} missing after round-trip");
            AssertClose(src.Channels[ch], back.Channels[ch], ch);
        }
        Assert.False(back.Has("Color"), "the outline channel is baked at package time, not carried through glTF");
        Assert.Equal(SpatialTriangles(src), SpatialTriangles(back));
    }

    [Fact]
    public void ExportThenImport_MultiSubmesh_SharesPoolAndRoundTripsExactly()
    {
        // Two material slots share ONE vertex pool. SharpGLTF's per-call WithVertexAccessor would mint a
        // pool per primitive, import would read them as distinct pools and concatenate, and the vertex count
        // would balloon ×(submesh count) — silently breaking the by-index preserve path and collapsing a
        // multi-submesh garment in-game.
        var src = Patch(new List<int[]> { new[] { 0, 1, 2, 2, 1, 3 }, new[] { 2, 3, 4 } });
        using var g = new TempGame();
        var path = g.At("rt-multi.glb");
        MeshGltf.ExportGlb(src, path);
        var back = MeshGltf.ImportGlb(path);

        Assert.Equal(src.VertexCount, back.VertexCount);   // NOT duplicated to 5 × 2 submeshes
        foreach (var ch in new[] { "Vertex", "Normal", "Tangent", "TexCoord0", "TexCoord1", "TexCoord2" })
        {
            Assert.True(back.Has(ch), $"channel {ch} missing after round-trip");
            AssertClose(src.Channels[ch], back.Channels[ch], ch);
        }
        Assert.False(back.Has("Color"), "the outline channel is baked at package time, not carried through glTF");
        Assert.Equal(src.Submeshes.Count, back.Submeshes.Count);
        Assert.Equal(SpatialTriangles(src), SpatialTriangles(back));
    }

    [Fact]
    public void ExportThenImport_ThreeComponentUv0_TransportsXyAtTheStoredStride()
    {
        var src = Patch(new List<int[]> { new[] { 0, 1, 2, 2, 1, 3 } });
        src.Channels["TexCoord0"] = new float[]
        {
            0, 0, 10,  1, 0, 11,  0, 1, 12,  1, 1, 13,  0.5f, 0.5f, 14,
        };
        src.Dims["TexCoord0"] = 3;

        using var g = new TempGame();
        var path = g.At("wide-uv0.glb");
        MeshGltf.ExportGlb(src, path);
        var back = MeshGltf.ImportGlb(path);

        Assert.Empty(MeshGltf.TexCoordTransportWarnings(src, "patch"));
        Assert.Equal(2, back.Dims["TexCoord0"]);
        AssertClose(new float[] { 0, 0,  1, 0,  0, 1,  1, 1,  0.5f, 0.5f },
            back.Channels["TexCoord0"], "TexCoord0");

        var built = MeshApply.BuildGeometry(src, MeshApply.Payload.Geometry(back));
        var layout = new UnityMesh.ChannelDef[5];
        layout[0] = new UnityMesh.ChannelDef(0, 0, 0, 3);
        layout[4] = new UnityMesh.ChannelDef(1, 0, 0, 3);
        var conformed = MeshApply.ConformChannels(layout, src.VertexCount, built, src);
        AssertClose(src.Channels["TexCoord0"], conformed["TexCoord0"], "widened TexCoord0");
    }

    [Fact]
    public void Export_WithUvGap_LeavesLaterSetAtGameAndNamesIt()
    {
        var src = Patch(new List<int[]> { new[] { 0, 1, 2 } });
        src.Channels.Remove("TexCoord1");
        src.Dims.Remove("TexCoord1");

        var warnings = MeshGltf.TexCoordTransportWarnings(src, "coat");
        Assert.Equal(new[]
        {
            "coat: UV2 cannot be edited in Blender because UV1 is missing. The game values will be kept.",
        }, warnings);

        using var g = new TempGame();
        var path = g.At("gap.glb");
        MeshGltf.ExportGlb(src, path);
        var back = MeshGltf.ImportGlb(path);
        Assert.True(back.Has("TexCoord0"));
        Assert.False(back.Has("TexCoord1"));
        Assert.False(back.Has("TexCoord2"));
    }

    [Fact]
    public void Export_WithWideHigherUv_TransportsItsXyAndEveryLaterSet()
    {
        var src = Patch(new List<int[]> { new[] { 0, 1, 2 } });
        src.Channels["TexCoord1"] = new float[]
        {
            2, 3, 10,  4, 3, 11,  2, 5, 12,  4, 5, 13,  3, 4, 14,
        };
        src.Dims["TexCoord1"] = 3;

        Assert.Empty(MeshGltf.TexCoordTransportWarnings(src, "coat"));

        using var g = new TempGame();
        var path = g.At("wide.glb");
        MeshGltf.ExportGlb(src, path);
        var back = MeshGltf.ImportGlb(path);
        Assert.True(back.Has("TexCoord0"));
        Assert.True(back.Has("TexCoord1"));
        Assert.True(back.Has("TexCoord2"));
        AssertClose(new float[] { 2, 3,  4, 3,  2, 5,  4, 5,  3, 4 },
            back.Channels["TexCoord1"], "TexCoord1");
    }

    [Fact]
    public void Export_WithOneComponentUv_LeavesTheBlockedPrefixAtGameAndWarnsOnceForThePart()
    {
        var src = Patch(new List<int[]> { new[] { 0, 1, 2 } });
        src.Channels["TexCoord1"] = new float[] { 2, 4, 6, 8, 10 };
        src.Dims["TexCoord1"] = 1;

        Assert.Equal(new[]
        {
            "coat: UV1 and UV2 cannot be edited in Blender because UV1 has 1 value per vertex instead "
            + "of at least two. The game values will be kept.",
        }, MeshGltf.TexCoordTransportWarnings(src, "coat"));

        using var g = new TempGame();
        var path = g.At("narrow.glb");
        MeshGltf.ExportGlb(src, path);
        var back = MeshGltf.ImportGlb(path);
        Assert.True(back.Has("TexCoord0"));
        Assert.False(back.Has("TexCoord1"));
        Assert.False(back.Has("TexCoord2"));
    }

    [Fact]
    public void Reexport_LegacyWorkspace_RefillsSupportedHigherUvsFromFreshBaseline()
    {
        using var g = new TempGame();
        var baselinePath = g.At("fresh.glb");
        var legacyPath = g.At("legacy.glb");
        var normalizedPath = g.At("normalized.glb");
        var baseline = Patch(new List<int[]> { new[] { 0, 1, 2, 2, 1, 3 } });
        var legacy = Patch(new List<int[]> { new[] { 0, 1, 2, 2, 1, 3 } });
        legacy.Channels.Remove("TexCoord1");
        legacy.Channels.Remove("TexCoord2");
        legacy.Dims.Remove("TexCoord1");
        legacy.Dims.Remove("TexCoord2");
        MeshGltf.ExportGlb(baseline, baselinePath);
        MeshGltf.ExportGlb(legacy, legacyPath);

        var fresh = MeshGltf.ParsedGlb.Open(baselinePath);
        var returned = MeshGltf.ParsedGlb.Open(legacyPath);
        Assert.Equal(new[]
        {
            "Restored UV1 on patch from the part's game mesh because that UV layer was deleted in Blender.",
            "Restored UV2 on patch from the part's game mesh because that UV layer was deleted in Blender.",
        }, MeshGltf.ReturnedTexCoordWarnings(returned, "patch",
            MeshGltf.TransportedTexCoordCount(fresh, "patch")));
        MeshGltf.ReexportPartGlb(legacyPath, "patch", normalizedPath, geometryBaseline: fresh);
        var normalized = MeshGltf.ImportGlb(normalizedPath, "patch", lenient: true);

        AssertClose(baseline.Channels["TexCoord1"], normalized.Channels["TexCoord1"], "TexCoord1");
        AssertClose(baseline.Channels["TexCoord2"], normalized.Channels["TexCoord2"], "TexCoord2");
    }

    [Fact]
    public void Reexport_DropsUnsupportedBlenderUvs_AndNamesPartAndLayers()
    {
        using var g = new TempGame();
        var baselinePath = g.At("one-uv.glb");
        var returnedPath = g.At("returned.glb");
        var normalizedPath = g.At("normalized.glb");
        var baseline = Patch(new List<int[]> { new[] { 0, 1, 2 } });
        baseline.Channels.Remove("TexCoord1");
        baseline.Channels.Remove("TexCoord2");
        baseline.Dims.Remove("TexCoord1");
        baseline.Dims.Remove("TexCoord2");
        MeshGltf.ExportGlb(baseline, baselinePath);
        MeshGltf.ExportGlb(Patch(new List<int[]> { new[] { 0, 1, 2 } }), returnedPath);

        var fresh = MeshGltf.ParsedGlb.Open(baselinePath);
        var returned = MeshGltf.ParsedGlb.Open(returnedPath);
        Assert.Equal(new[]
        {
            "Ignored UV1 on patch because that UV layer is not supported by the part's game mesh.",
            "Ignored UV2 on patch because that UV layer is not supported by the part's game mesh.",
        }, MeshGltf.ReturnedTexCoordWarnings(returned, "patch",
            MeshGltf.TransportedTexCoordCount(fresh, "patch")));

        MeshGltf.ReexportPartGlb(returned, "patch", normalizedPath, geometryBaseline: fresh);
        var normalized = MeshGltf.ImportGlb(normalizedPath, "patch", lenient: true);
        Assert.True(normalized.Has("TexCoord0"));
        Assert.False(normalized.Has("TexCoord1"));
        Assert.False(normalized.Has("TexCoord2"));
    }

    [Fact]
    public void Reexport_BaselineWithoutUvs_DropsAndNamesBlenderCreatedUv0()
    {
        using var g = new TempGame();
        var baselinePath = g.At("no-uv.glb");
        var returnedPath = g.At("invented-uv0.glb");
        var normalizedPath = g.At("filtered-uv0.glb");
        var baseline = Patch(new List<int[]> { new[] { 0, 1, 2 } });
        var returnedMesh = Patch(new List<int[]> { new[] { 0, 1, 2 } });
        foreach (int i in new[] { 0, 1, 2 })
        {
            baseline.Channels.Remove($"TexCoord{i}");
            baseline.Dims.Remove($"TexCoord{i}");
        }
        foreach (int i in new[] { 1, 2 })
        {
            returnedMesh.Channels.Remove($"TexCoord{i}");
            returnedMesh.Dims.Remove($"TexCoord{i}");
        }
        MeshGltf.ExportGlb(baseline, baselinePath);
        MeshGltf.ExportGlb(returnedMesh, returnedPath);

        var fresh = MeshGltf.ParsedGlb.Open(baselinePath);
        var returned = MeshGltf.ParsedGlb.Open(returnedPath);
        Assert.Equal(new[]
        {
            "Ignored UV0 on patch because that UV layer is not supported by the part's game mesh.",
        }, MeshGltf.ReturnedTexCoordWarnings(returned, "patch",
            MeshGltf.TransportedTexCoordCount(fresh, "patch")));

        MeshGltf.ReexportPartGlb(returned, "patch", normalizedPath, geometryBaseline: fresh);
        Assert.False(MeshGltf.ImportGlb(normalizedPath, "patch", lenient: true).Has("TexCoord0"));
    }

    [Fact]
    public void Reexport_DeletedSupportedUv0_RestoresAndNamesIt()
    {
        using var g = new TempGame();
        var baselinePath = g.At("uv0-baseline.glb");
        var returnedPath = g.At("deleted-uv0.glb");
        var normalizedPath = g.At("restored-uv0.glb");
        var baseline = Patch(new List<int[]> { new[] { 0, 1, 2 } });
        var deleted = Patch(new List<int[]> { new[] { 0, 1, 2 } });
        foreach (int i in new[] { 1, 2 })
        {
            baseline.Channels.Remove($"TexCoord{i}"); baseline.Dims.Remove($"TexCoord{i}");
            deleted.Channels.Remove($"TexCoord{i}"); deleted.Dims.Remove($"TexCoord{i}");
        }
        deleted.Channels.Remove("TexCoord0");
        deleted.Dims.Remove("TexCoord0");
        MeshGltf.ExportGlb(baseline, baselinePath);
        MeshGltf.ExportGlb(deleted, returnedPath);

        var fresh = MeshGltf.ParsedGlb.Open(baselinePath);
        var returned = MeshGltf.ParsedGlb.Open(returnedPath);
        Assert.Equal(new[]
        {
            "Restored UV0 on patch from the part's game mesh because that UV layer was deleted in Blender.",
        }, MeshGltf.ReturnedTexCoordWarnings(returned, "patch",
            MeshGltf.TransportedTexCoordCount(fresh, "patch")));

        MeshGltf.ReexportPartGlb(returned, "patch", normalizedPath, geometryBaseline: fresh);
        var normalized = MeshGltf.ImportGlb(normalizedPath, "patch", lenient: true);
        AssertClose(baseline.Channels["TexCoord0"], normalized.Channels["TexCoord0"], "TexCoord0");
    }

    private static void AssertClose(float[] expected, float[] actual, string ch)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(System.Math.Abs(expected[i] - actual[i]) < 1e-5f,
                $"{ch}[{i}]: expected {expected[i]}, got {actual[i]}");
    }

    /// <summary>Every triangle as its vertex POSITIONS in winding order, rotation-normalized — robust to
    /// reindexing, so it compares geometry rather than buffer layout.</summary>
    private static HashSet<string> SpatialTriangles(UnityMesh m)
    {
        var pos = m.Channels["Vertex"];
        string P(int v) => $"{pos[v * 3]:0.000},{pos[v * 3 + 1]:0.000},{pos[v * 3 + 2]:0.000}";
        var set = new HashSet<string>();
        foreach (var sm in m.Submeshes)
            for (int i = 0; i + 3 <= sm.Length; i += 3)
            {
                var t = new[] { P(sm[i]), P(sm[i + 1]), P(sm[i + 2]) };
                int min = t.ToList().IndexOf(t.Min()!);   // rotate to smallest, keeping winding
                set.Add($"{t[min]}|{t[(min + 1) % 3]}|{t[(min + 2) % 3]}");
            }
        return set;
    }
}
