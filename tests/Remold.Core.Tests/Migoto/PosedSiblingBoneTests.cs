using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Remold.Core.Export;
using Remold.Core.Materials;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The promise a rigged export makes when it ships the SUBJECT's whole skeleton rather than the opened
/// part's own: weight a modder paints onto a bone their part does not table at all still builds, as long
/// as some sibling of the outfit POSES that bone. Three merged pieces have to compose for that to hold —
/// the export offering the bone (extra armature nodes), <see cref="PoolDerive.Derive"/> pulling the
/// posing sibling into the pool by its bone TABLE while its posed gate lets the bone through, and
/// <see cref="SwapCompile.CompilePool"/> overwriting the layout target's bone table with the pool UNION
/// so <c>MeshApply.ResolveAuthoredJoints</c> resolves the painted hash against the union instead of the
/// target's own short list. Each piece has its own unit tests; nothing proved the composition through a
/// real <see cref="ModBuilder.Build"/>, which is what this does.
///
/// <para>The world is built here rather than borrowed from <c>ModBuilderTests</c>: the control needs a
/// sibling that TABLES the bone without posing it, and that shape has no knob in the shared fixture.</para>
/// </summary>
public class PosedSiblingBoneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-psb-" + Guid.NewGuid().ToString("N"));
    private readonly string _proj;
    private readonly string _out;

    public PosedSiblingBoneTests()
    {
        _proj = Path.Combine(_root, "proj");
        _out = Path.Combine(_root, "build");
        Directory.CreateDirectory(_proj);
        Directory.CreateDirectory(_out);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ---- the world: a Replace TARGET and one sibling that owns bones of its own ---------------------

    /// <summary>The target part's own bone table — all the donor could ride before the export started
    /// shipping the rest of the subject's skeleton.</summary>
    private static readonly uint[] BodyBones = { 0x00000101, 0x00000102 };

    /// <summary>The sibling's bones. Nothing of the target's mesh tables these, so
    /// <see cref="SiblingBone"/> is exactly the bone this test is about: paintable only because the
    /// export offered it, poseable only because the sibling moves it.</summary>
    private static readonly uint[] MateBones = { 0x00000105, 0x00000106 };

    /// <summary>The one the donor is weighted onto.</summary>
    private const uint SiblingBone = 0x00000105;

    /// <summary>Deterministic spread positions, so no two vertices coincide and the operator solve has
    /// real support. (The shared fixture's own cloud is private to it.)</summary>
    private static float[] Cloud(int verts, int seed)
    {
        var pos = new float[verts * 3];
        for (int i = 0; i < pos.Length; i++)
        {
            uint h = (uint)(seed * 97) + (uint)i * 2654435761u;
            h ^= h >> 13; h *= 2246822519u; h ^= h >> 16;
            pos[i] = h % 1000 / 250f - 2f;
        }
        return pos;
    }

    /// <summary>Triangles wrapping the vertex range, so every vertex is drawn and no index runs past it.
    /// The vertex COUNT is what gives each part an index buffer of its own.</summary>
    private static int[] WrappedTris(int verts)
    {
        var tris = new int[verts * 3];
        for (int i = 0; i < tris.Length; i++) tris[i] = i % verts;
        return tris;
    }

    /// <summary>The two-part skinned outfit. <paramref name="matePoses"/> false builds the sibling so it
    /// TABLES <see cref="SiblingBone"/> at zero weight instead of posing it — the control, and the corpus
    /// shape where a part lists more skeleton than it moves.
    ///
    /// <para><paramref name="timelineHides"/> supplies the build's timeline resolver, standing in for the
    /// dorm and lobby clips the doll plays. Null ships no resolver at all, which is a build that measured
    /// no timelines and demotes nothing for them.</para></summary>
    private BuildEnv MakeEnv(bool matePoses, string[]? timelineHides = null)
    {
        string b0 = Path.Combine(_root, "s0.bundle");
        string b1 = Path.Combine(_root, "s1.bundle");
        string bm = Path.Combine(_root, "sm.bundle");
        string bt = Path.Combine(_root, "st.bundle");

        SyntheticBundle.BuildOneSkinnedMesh(b0, "c_vesna01_body_lod0", Cloud(32, 5), WrappedTris(32), BodyBones);
        SyntheticBundle.BuildOneSkinnedMesh(b1, "c_vesna01_body_lod1", Cloud(24, 9), WrappedTris(24), BodyBones);
        if (matePoses)
            SyntheticBundle.BuildOneSkinnedMesh(bm, "c_vesna01_mate_lod0", Cloud(20, 17), WrappedTris(20),
                MateBones);
        else
            // the same TABLE, and every vertex parked on the other bone: 0x105 is listed and never moved
            SyntheticBundle.BuildOneSkinnedMesh(bm, "c_vesna01_mate_lod0", Cloud(20, 17), WrappedTris(20),
                new[] { MateBones[1] }, tabledOnlyBones: new[] { SiblingBone });
        SyntheticBundle.BuildOneTexture(bt, "tex_body_d", 8, 8, 200, 100, 50, 255, colorSpace: 1);

        var bytes = new Dictionary<string, byte[]>
        {
            ["bundle0"] = File.ReadAllBytes(b0),
            ["bundle1"] = File.ReadAllBytes(b1),
            ["bundleM"] = File.ReadAllBytes(bm),
            ["bundleT"] = File.ReadAllBytes(bt),
        };

        var albedo = new List<SubjectMap> { new("_BaseMap", "tex_body_d", "bundleT") };
        var parts = new[]
        {
            new SubjectPart("body", "c_vesna01_body_lod0", "addr_body",
                new[] { new SubjectMaterial("m_body", 1, "cab-body", albedo) },
                SiblingTiers: new[] { new RecipeTierSlot("c_vesna01_body_lod1", "addr_body_l1") }),
            new SubjectPart("mate", "c_vesna01_mate_lod0", "addr_mate",
                new[] { new SubjectMaterial("m_mate", 1, "cab-mate", albedo) }),
        };
        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, parts,
            Skeleton: null, Problems: Array.Empty<string>());

        var addresses = new Dictionary<string, string>
        {
            ["addr_body"] = "bundle0", ["addr_body_l1"] = "bundle1", ["addr_mate"] = "bundleM",
        };
        return new BuildEnv(
            (c, s) => c == "Vesna" && s == "VesnaSSR01" ? model : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0",
            TimelineShoesFor: timelineHides is null ? null
                : _ => new[] { new Remold.Core.Bundles.TimelineShoe(Array.Empty<string>(), timelineHides) });
    }

    private ModProject NewProject(string name)
    {
        var p = new ModProject { RootDir = _proj };
        p.Info.Name = name;
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01", ReplaceFile = "donor.glb",
        });
        return p;
    }

    /// <summary>The donor a send-back hands the build: real weight on <see cref="SiblingBone"/> beside the
    /// target's own two, carried the way a send-back carries it — the glb's joint NODES are hash-named, so
    /// the hash travels in <c>SkinJointHashes</c> and nothing but the hash resolves it.</summary>
    private void WriteDonorGlb(uint[] bones)
    {
        const int verts = 6;
        var mesh = new UnityMesh
        {
            Name = "donor",
            VertexCount = verts,
            Channels = new Dictionary<string, float[]>
            {
                ["Vertex"] = Cloud(verts, 11),
                ["Normal"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 0, 1, 0 }).ToArray(),
                ["Tangent"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 1, 0, 0, 1 }).ToArray(),
                ["TexCoord0"] = Enumerable.Range(0, verts).SelectMany(v => new float[] { v / 8f, v / 8f }).ToArray(),
                ["BlendWeight"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 1, 0, 0, 0 }).ToArray(),
                // v % bones.Length, so with three bones two of the six vertices ride the sibling's
                ["BlendIndices"] = Enumerable.Range(0, verts)
                    .SelectMany(v => new float[] { v % bones.Length, 0, 0, 0 }).ToArray(),
            },
            Dims = new Dictionary<string, int>
            {
                ["Vertex"] = 3, ["Normal"] = 3, ["Tangent"] = 4, ["TexCoord0"] = 2,
                ["BlendWeight"] = 4, ["BlendIndices"] = 4,
            },
            Submeshes = new List<int[]> { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } },
        };
        var skin = new MeshSkin
        {
            BoneHashes = bones,
            BindPoses = bones.Select(_ => System.Numerics.Matrix4x4.Identity).ToArray(),
        };
        MeshGltf.ExportRiggedGlb(mesh, skin, _ => null, Path.Combine(_proj, "donor.glb"));
    }

    /// <summary>The ib hash of a mesh in one of this world's bundles — what a capture section keys on.</summary>
    private string Ib(string bundleFile, string mesh) =>
        BufferHash.Compute(File.ReadAllBytes(Path.Combine(_root, bundleFile)), mesh).Ib.ToString("x8");

    /// <summary>The shipped union bone order, read back off the artifact that STATES it to the donor
    /// compile. Decimal strings, in union slot order.</summary>
    private static List<uint> UnionOrder(string outDir, string suffix)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, $"union_{suffix}.json")));
        return doc.RootElement.GetProperty("order").EnumerateArray()
            .Select(e => uint.Parse(e.GetString()!)).ToList();
    }

    /// <summary>True when some vertex of the compiled donor skin rides union slot <paramref name="slot"/>
    /// at nonzero weight. stream2 is float4 weights + uint4 indices, which is what
    /// <see cref="PoolMath.ParseSkin"/> reads.</summary>
    private static (int Verts, double Weight) RidersOf(string outDir, string suffix, int slot)
    {
        var (w, bi) = PoolMath.ParseSkin(
            File.ReadAllBytes(Path.Combine(outDir, $"combined_skin_{suffix}.buf")));
        int verts = 0;
        double total = 0;
        for (int v = 0; v < bi.GetLength(0); v++)
        {
            double onSlot = 0;
            for (int k = 0; k < 4; k++)
                if (bi[v, k] == slot) onSlot += w[v, k];
            if (onSlot > 0) { verts++; total += onSlot; }
        }
        return (verts, total);
    }

    [Fact]
    public void Weight_on_a_bone_only_a_sibling_poses_ships_in_the_built_mod()
    {
        // The whole promise in one build. The modder opened the body, painted onto a bone only the mate
        // moves — a bone the body's mesh does not table at all — and the mod that ships has to carry that
        // weight on that bone. Assert it at the two artifacts that state it: the union the donor was
        // compiled against, and the compiled skin stream itself.
        Assert.DoesNotContain(SiblingBone, BodyBones);   // the premise: not the target's own bone

        var env = MakeEnv(matePoses: true);
        var p = NewProject("PosedSibling");
        WriteDonorGlb(BodyBones.Append(SiblingBone).ToArray());
        var lines = new List<string>();

        var r = ModBuilder.Build(p, env, _out, lines.Add, zip: false);

        // 1. no refusal: the bone IS posed, by the mate — Build returned instead of throwing, and a mod
        // folder came out the far end rather than the half-built state a failed run cleans up
        Assert.True(Directory.Exists(r.OutDir));
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        // 2. the mate joined the pool, and is captured so its palette rows can be recovered
        Assert.Contains(lines, l => l ==
            "pool (vesna_body): c_vesna01_body_lod0, c_vesna01_mate_lod0 (anchor c_vesna01_body_lod0)");
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, r.OutDir);
        Assert.Contains($"[TextureOverride_Cap_vesna_mate]\nhash = {Ib("sm.bundle", "c_vesna01_mate_lod0")}\n", ini);

        // 3. the union carries the painted bone, and the compiled donor actually rides its slot
        var order = UnionOrder(r.OutDir, "vesna_body");
        int slot = order.IndexOf(SiblingBone);
        Assert.True(slot >= 0,
            $"the shipped union has no slot for 0x{SiblingBone:x8} (order: {string.Join(", ", order)})");
        // past the target's own table: the slot exists only because the mate joined the pool
        Assert.True(slot >= BodyBones.Length);
        // the donor put two of its six vertices fully on that bone; both arrive, at their full weight.
        // Counting rather than merely finding one is what separates this from a partial drop: an
        // unresolved influence is zeroed and the survivors renormalized, which would still leave SOME
        // vertex on the slot while silently deforming the rest.
        var riders = RidersOf(r.OutDir, "vesna_body", slot);
        Assert.Equal(2, riders.Verts);
        Assert.Equal(2.0, riders.Weight, 5);
    }

    [Fact]
    public void The_same_weight_refuses_when_the_sibling_only_tables_that_bone()
    {
        // The control for the test above: strip the mate's POSING of the bone and nothing else. The mate
        // still tables it, so it still pools and the bone is no orphan — and the build refuses on the
        // posed gate. That is what says the success case went through the gate's positive path rather
        // than around it.
        var env = MakeEnv(matePoses: false);
        var p = NewProject("PosedSiblingControl");
        WriteDonorGlb(BodyBones.Append(SiblingBone).ToArray());

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Equal("the donor rides 1 bone(s) that no pooled part of this outfit poses "
            + "(first: 0x00000105, carried at zero weight by 'c_vesna01_mate_lod0'). "
            + "Re-weight the donor onto the bones this outfit moves", ex.Message);
    }

    [Fact]
    public void A_timeline_naming_the_sibling_takes_it_out_of_the_pool_and_says_so()
    {
        // The build-time visibility class, end to end: the mate poses the bone exactly as in the success
        // case, but a dorm timeline can flip it on its own, so it may not be leaned on. The refusal has to
        // name the mate AND the mechanism, rather than blaming the donor's armature.
        var env = MakeEnv(matePoses: true, timelineHides: new[] { "c_vesna01_mate_lod0" });
        var p = NewProject("PosedSiblingTimeline");
        WriteDonorGlb(BodyBones.Append(SiblingBone).ToArray());

        var ex = Assert.Throws<InvalidDataException>(() => ModBuilder.Build(p, env, _out, zip: false));

        Assert.Contains("Left out of the pool: 'c_vesna01_mate_lod0' · a dorm scene can hide or reveal it "
            + "mid-pose", ex.Message);
        Assert.DoesNotContain("different armature", ex.Message);
    }

    [Fact]
    public void A_timeline_naming_nothing_of_this_outfit_leaves_the_pool_alone()
    {
        // The control: a resolver IS wired and returns a real hide list, but no entry reaches this
        // outfit's parts. The mate pools as it always did, so it is the MATCH that decided the refusal
        // above and not the mere presence of a timeline.
        var env = MakeEnv(matePoses: true, timelineHides: new[] { "c_vesna01_elsewhere_lod0" });
        var p = NewProject("PosedSiblingTimelineControl");
        WriteDonorGlb(BodyBones.Append(SiblingBone).ToArray());
        var lines = new List<string>();

        var r = ModBuilder.Build(p, env, _out, lines.Add, zip: false);

        Assert.True(Directory.Exists(r.OutDir));
        Assert.Contains(lines, l => l ==
            "pool (vesna_body): c_vesna01_body_lod0, c_vesna01_mate_lod0 (anchor c_vesna01_body_lod0)");
    }
}
