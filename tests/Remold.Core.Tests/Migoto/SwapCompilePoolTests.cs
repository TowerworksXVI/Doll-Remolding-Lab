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

    private const uint A = 101, B = 102, C = 103, D = 104, E = 105, F = 106;

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

    /// <summary>The extra is a bone the pool DOES table without posing, so the union row it would take has no
    /// writer and the hash appears twice in the rewritten table. Handed in as an extra, the donor's weight
    /// compiles onto the continuation slot — the last entry carrying the hash — rather than onto that dead
    /// union row, while the union itself, which is what the pool parts' palette is built over, is
    /// unchanged.</summary>
    [Fact]
    public void Extra_bones_compile_onto_a_dense_continuation_of_the_union()
    {
        string pa = Path.Combine(_root, "alphax.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pa, "alpha_mesh", Cloud(12, 5), Tris(12), new[] { A, B, C, D });
        string pb = Path.Combine(_root, "betax.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pb, "beta_mesh", Cloud(8, 6), Tris(8), new[] { B, C, D, E });

        string glb = DonorGlb(new[] { A, B, C, D, E });
        string outDir = Path.Combine(_root, "outx");
        var result = SwapCompile.CompilePool(new[]
        {
            new SwapCompile.PoolMesh(File.ReadAllBytes(pa), "alpha_mesh"),
            new SwapCompile.PoolMesh(File.ReadAllBytes(pb), "beta_mesh"),
        }, glb, outDir, extraBones: new[] { D });

        // the union and its record stay the pool's own
        Assert.Equal(5, result.UnionBones);
        Assert.Equal(5, JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "unionorder.json")))
            .RootElement.GetArrayLength());

        // the donor rides bone v % 5, so vertex 3 is the one weighted to D — and it compiles onto 5, the
        // continuation slot, not onto D's own union row
        var (_, bi) = PoolMath.ParseSkin(File.ReadAllBytes(Path.Combine(outDir, "stream2.buf")));
        Assert.Equal(5, bi[3, 0]);
        Assert.Equal(0, bi[0, 0]);      // every other index is the union one it always was
        Assert.Equal(4, bi[4, 0]);
    }

    /// <summary>The sibling case, and the corpus one: a coverage group's members are usually the ONLY parts
    /// tabling the bone they cover, so the extra is absent from the union entirely. It is then the single
    /// table entry carrying its hash, the donor's weight on it still compiles onto the continuation slot, and
    /// the union the pool's palette is built over neither grows nor reorders.</summary>
    [Fact]
    public void An_extra_bone_no_pool_part_tables_still_compiles_onto_the_continuation()
    {
        string pa = Path.Combine(_root, "alphaf.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pa, "alpha_mesh", Cloud(12, 8), Tris(12), new[] { A, B, C, D });
        string pb = Path.Combine(_root, "betaf.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pb, "beta_mesh", Cloud(8, 9), Tris(8), new[] { B, C, D, E });

        // the donor rides F, which neither pool part carries — the bone only a group's members table
        string glb = DonorGlb(new[] { A, B, C, D, E, F });
        string outDir = Path.Combine(_root, "outf");
        var result = SwapCompile.CompilePool(new[]
        {
            new SwapCompile.PoolMesh(File.ReadAllBytes(pa), "alpha_mesh"),
            new SwapCompile.PoolMesh(File.ReadAllBytes(pb), "beta_mesh"),
        }, glb, outDir, extraBones: new[] { F });

        // the union and its record are the pool's own, unchanged by an extra that is no part of it
        Assert.Equal(5, result.UnionBones);
        var order = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "unionorder.json")))
            .RootElement.EnumerateArray().Select(e => uint.Parse(e.GetString()!)).ToArray();
        Assert.Equal(new[] { A, B, C, D, E }, order);

        // the donor rides bone v % 6, so vertex 5 is the one weighted to F — and it compiles onto 5, the
        // continuation slot past the union's five rows
        var (_, bi) = PoolMath.ParseSkin(File.ReadAllBytes(Path.Combine(outDir, "stream2.buf")));
        Assert.Equal(5, bi[5, 0]);
        Assert.Equal(0, bi[0, 0]);      // every union index is the one it always was
        Assert.Equal(4, bi[4, 0]);
        Assert.Equal(0, bi[6, 0]);

        // nothing was dropped on the way: an unresolved influence is what the out-of-skeleton warning names
        Assert.DoesNotContain(result.Warnings, w => w.Contains("doesn't have"));
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
