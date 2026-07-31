using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// <see cref="SwapCompile.CompilePool"/> end to end: real synthetic bundles in, a rigged donor glb
/// through the importer, raw GPU streams out. Everything under it has tests of its own; this pins the
/// composed route — union order, layout conformance, and the emitted files a build hands to the
/// emitter.
/// </summary>
public class SwapCompilePoolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-cpool-" + Guid.NewGuid().ToString("N"));

    public SwapCompilePoolTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const uint A = 101, B = 102, C = 103, D = 104, E = 105;

    private static float[] Cloud(int n, int seed) =>
        Enumerable.Range(0, n * 3).Select(i => ((i * 31 + seed) % 17) / 5f - 1.5f).ToArray();

    private static int[] Tris(int n) => Enumerable.Range(0, n * 3).Select(i => i % n).ToArray();

    private string DonorGlb(uint[] bones)
    {
        const int verts = 9;
        var mesh = new UnityMesh
        {
            Name = "donor",
            VertexCount = verts,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = Cloud(verts, 7),
                ["Normal"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 0, 1, 0 }).ToArray(),
                ["Tangent"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 1, 0, 0, 1 }).ToArray(),
                ["TexCoord0"] = Enumerable.Range(0, verts).SelectMany(v => new float[] { v / 16f, v / 16f }).ToArray(),
                ["BlendWeight"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 1, 0, 0, 0 }).ToArray(),
                ["BlendIndices"] = Enumerable.Range(0, verts)
                    .SelectMany(v => new float[] { v % bones.Length, 0, 0, 0 }).ToArray(),
            },
            Dims = new Dictionary<string, int>
            {
                ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2,
                ["BlendWeight"] = 4, ["BlendIndices"] = 4,
            },
            Submeshes = new List<int[]> { new[] { 0, 1, 2, 3, 4, 5 }, new[] { 6, 7, 8 } },
        };
        var skin = new MeshSkin
        {
            BoneHashes = bones,
            BindPoses = bones.Select(_ => System.Numerics.Matrix4x4.Identity).ToArray(),
        };
        string glb = Path.Combine(_root, "donor.glb");
        MeshGltf.ExportRiggedGlb(mesh, skin, _ => null, glb);
        return glb;
    }

    [Fact]
    public void A_two_part_pool_compiles_a_donor_onto_the_union_and_emits_the_swap_files()
    {
        string pa = Path.Combine(_root, "alpha.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pa, "alpha_mesh", Cloud(12, 1), Tris(12), new[] { A, B, C, D });
        string pb = Path.Combine(_root, "beta.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pb, "beta_mesh", Cloud(8, 2), Tris(8), new[] { B, C, D, E });

        string glb = DonorGlb(new[] { A, B, C, D, E });
        string outDir = Path.Combine(_root, "out");
        var result = SwapCompile.CompilePool(new[]
        {
            new SwapCompile.PoolMesh(File.ReadAllBytes(pa), "alpha_mesh"),
            new SwapCompile.PoolMesh(File.ReadAllBytes(pb), "beta_mesh"),
        }, glb, outDir);

        // the union is first-seen over the parts in argument order, and the donor rode all of it
        Assert.Equal(5, result.UnionBones);
        Assert.Equal(9, result.VertexCount);
        Assert.Equal(2, result.SubmeshCount);

        // the files the emitter consumes, in the layout target's own stream shape
        foreach (var s in result.Streams)
            Assert.True(File.Exists(Path.Combine(outDir, $"stream{s.Stream}.buf")), $"stream{s.Stream}.buf missing");
        Assert.True(File.Exists(Path.Combine(outDir, "ib.buf")));
        Assert.True(new FileInfo(Path.Combine(outDir, "ib.buf")).Length > 0);

        using var meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "meta.json")));
        Assert.Equal(5, meta.RootElement.GetProperty("unionBones").GetInt32());
        Assert.Equal(9, meta.RootElement.GetProperty("verts").GetInt32());

        var order = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "unionorder.json")))
            .RootElement.EnumerateArray().Select(e => uint.Parse(e.GetString()!)).ToArray();
        Assert.Equal(new[] { A, B, C, D, E }, order);
    }

    [Fact]
    public void The_layout_target_choice_changes_the_stream_shape_not_the_union_order()
    {
        string pa = Path.Combine(_root, "alpha2.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pa, "alpha_mesh", Cloud(12, 3), Tris(12), new[] { A, B, C, D });
        string pb = Path.Combine(_root, "beta2.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pb, "beta_mesh", Cloud(8, 4), Tris(8), new[] { B, C, D, E });
        string glb = DonorGlb(new[] { A, B, C, D, E });

        var meshes = new[]
        {
            new SwapCompile.PoolMesh(File.ReadAllBytes(pa), "alpha_mesh"),
            new SwapCompile.PoolMesh(File.ReadAllBytes(pb), "beta_mesh"),
        };
        var first = SwapCompile.CompilePool(meshes, glb, Path.Combine(_root, "out0"), layoutTargetIndex: 0);
        var second = SwapCompile.CompilePool(meshes, glb, Path.Combine(_root, "out1"), layoutTargetIndex: 1);

        static uint[] Order(string dir) => JsonDocument
            .Parse(File.ReadAllText(Path.Combine(dir, "unionorder.json")))
            .RootElement.EnumerateArray().Select(e => uint.Parse(e.GetString()!)).ToArray();

        // one union-order authority whichever part hosts the layout
        Assert.Equal(Order(first.OutDir), Order(second.OutDir));
        Assert.Equal(first.UnionBones, second.UnionBones);
        Assert.Equal(first.VertexCount, second.VertexCount);
    }
}
