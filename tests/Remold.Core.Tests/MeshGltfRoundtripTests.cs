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
    // 5 verts with position/normal/tangent/uv, plus a Color the transport DROPS (the outline is re-baked at
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
            ["Color"] = new float[] { 0.1f, 0, 0, 1,  0.2f, 0, 0, 1,  0.3f, 0, 0, 1,  0.4f, 0, 0, 1,  0.5f, 0, 0, 1 },
        },
        Dims = new Dictionary<string, int>
        {
            ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2, ["Color"] = 4,
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
        foreach (var ch in new[] { "Vertex", "Normal", "Tangent", "TexCoord0" })
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
        foreach (var ch in new[] { "Vertex", "Normal", "Tangent", "TexCoord0" })
        {
            Assert.True(back.Has(ch), $"channel {ch} missing after round-trip");
            AssertClose(src.Channels[ch], back.Channels[ch], ch);
        }
        Assert.False(back.Has("Color"), "the outline channel is baked at package time, not carried through glTF");
        Assert.Equal(src.Submeshes.Count, back.Submeshes.Count);
        Assert.Equal(SpatialTriangles(src), SpatialTriangles(back));
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
