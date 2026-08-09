using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The RIGID replace route: a draw with no per-vertex influences at all takes a direct geometry swap. The
/// vanilla draw is suppressed and the compiled donor is drawn in its place — no capture, no palette
/// recovery, no compute pass. Only a STATIC mesh lands here: a part storing influences at ANY width is
/// posed per vertex like any other skinned one and goes through palette recovery as a single-part pool.
/// </summary>
public class RigidReplaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-rigid-" + Guid.NewGuid().ToString("N"));
    private readonly string _proj;
    private readonly string _out;

    public RigidReplaceTests()
    {
        _proj = Path.Combine(_root, "proj");
        _out = Path.Combine(_root, "build");
        Directory.CreateDirectory(_proj);
        Directory.CreateDirectory(_out);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string Part = "p_CrateMk2_frame_lod0";
    private const string Tier = "p_CrateMk2_frame_lod1";
    private const string Panel = "p_CrateMk2_panel_lod0";
    private static readonly uint[] Bone = { 0x11111111u };

    /// <summary>The frame's and the panel's own base color hashes, once the twin world gives them one.</summary>
    private string _frameTexHash = "", _panelTexHash = "";

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

    private static int[] WrappedTris(int verts) =>
        Enumerable.Range(0, verts).SelectMany(v => new[] { v, (v + 1) % verts, (v + 2) % verts }).ToArray();

    /// <summary>The subject: one part with a lod1 sibling, drawn from bundles the caller shapes.
    /// <paramref name="skinWidth"/> of 0 builds a mesh with no skin channels at all (the prop shape, the
    /// only one this route takes); 1 and 2 the below-four widths that go pooled.
    /// <paramref name="implicitWeights"/> picks the one-influence spelling the game's own weapon parts
    /// ship: indices alone, each weight implicitly 1.</summary>
    /// <param name="twin">Adds a second static part on the frame's exact index buffer with geometry of
    /// its own, and gives the two base colors of their own — the shape where only the textures bound at
    /// the draw tell the two apart.</param>
    private BuildEnv MakeEnv(out string lod0Hash, out string lod1Hash, int skinWidth = 0,
        bool implicitWeights = false, bool twin = false)
    {
        string b0 = Path.Combine(_root, "r0.bundle");
        string b1 = Path.Combine(_root, "r1.bundle");
        if (skinWidth == 0)
        {
            SyntheticBundle.BuildOneMesh(b0, Part, Cloud(32, 5), WrappedTris(32));
            SyntheticBundle.BuildOneMesh(b1, Tier, Cloud(24, 9), WrappedTris(24));
        }
        else
        {
            SyntheticBundle.BuildOneSkinnedMesh(b0, Part, Cloud(32, 5), WrappedTris(32), Bone,
                skinWidth: skinWidth, implicitWeights: implicitWeights);
            SyntheticBundle.BuildOneSkinnedMesh(b1, Tier, Cloud(24, 9), WrappedTris(24), Bone,
                skinWidth: skinWidth, implicitWeights: implicitWeights);
        }
        var bytes = new Dictionary<string, byte[]>
        {
            ["bundle0"] = File.ReadAllBytes(b0),
            ["bundle1"] = File.ReadAllBytes(b1),
        };
        lod0Hash = BufferHash.Compute(bytes["bundle0"], Part).Ib.ToString("x8");
        lod1Hash = BufferHash.Compute(bytes["bundle1"], Tier).Ib.ToString("x8");

        var parts = new List<SubjectPart>();
        var addresses = new Dictionary<string, string>
        {
            ["addr_frame"] = "bundle0", ["addr_frame_l1"] = "bundle1",
        };
        var frameMaterials = Array.Empty<SubjectMaterial>();
        if (twin)
        {
            string bt = Path.Combine(_root, "rt.bundle");
            SyntheticBundle.Build(bt,
                new SyntheticBundle.TextureSpec("tex_frame_d", 8, 8,
                    SyntheticBundle.SolidRgba32(8, 8, 200, 100, 50, 255), ColorSpace: 1),
                new SyntheticBundle.TextureSpec("tex_panel_d", 8, 8,
                    SyntheticBundle.SolidRgba32(8, 8, 20, 210, 90, 255), ColorSpace: 1));
            bytes["bundleT"] = File.ReadAllBytes(bt);
            _frameTexHash = SyntheticBundle.StockTexHash(bytes["bundleT"], "tex_frame_d");
            _panelTexHash = SyntheticBundle.StockTexHash(bytes["bundleT"], "tex_panel_d");
            // the frame's exact triangle list over geometry of its own: one index buffer, two meshes
            string b2 = Path.Combine(_root, "r2.bundle");
            SyntheticBundle.BuildOneMesh(b2, Panel, Cloud(32, 21), WrappedTris(32));
            bytes["bundle2"] = File.ReadAllBytes(b2);
            frameMaterials = new[]
            {
                new SubjectMaterial("m_frame", 1, "cab-frame",
                    new[] { new SubjectMap("_BaseMap", "tex_frame_d", "bundleT") }),
            };
            parts.Add(new SubjectPart("panel", Panel, "addr_panel", new[]
            {
                new SubjectMaterial("m_panel", 2, "cab-panel",
                    new[] { new SubjectMap("_BaseMap", "tex_panel_d", "bundleT") }),
            }));
            addresses["addr_panel"] = "bundle2";
        }
        parts.Insert(0, new SubjectPart("frame", Part, "addr_frame", frameMaterials,
            SiblingTiers: new[] { new RecipeTierSlot(Tier, "addr_frame_l1") }));

        var model = new SubjectModel("Crate", "CrateMk2", SubjectSource.Prefab, parts.ToArray(),
            Skeleton: null, Problems: Array.Empty<string>());
        return new BuildEnv(
            (c, s) => c == "Crate" && s == "CrateMk2" ? model : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0");
    }

    private ModProject NewProject(string name = "Rigid Mod")
    {
        var p = new ModProject { RootDir = _proj };
        p.Info.Name = name;
        p.Selection.Add(new SelectionEntry { Character = "Crate", Outfit = "CrateMk2" });
        return p;
    }

    /// <summary>A donor with two submeshes. Its skin rides one bone; the geometry-only compile a skinless
    /// target takes drops it, which is the point.</summary>
    private void WriteDonorGlb(string file = "donor.glb")
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
                ["BlendIndices"] = Enumerable.Range(0, verts).SelectMany(_ => new float[] { 0, 0, 0, 0 }).ToArray(),
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
            BoneHashes = Bone,
            BindPoses = Bone.Select(_ => System.Numerics.Matrix4x4.Identity).ToArray(),
        };
        MeshGltf.ExportRiggedGlb(mesh, skin, _ => null, Path.Combine(_proj, file));
    }

    private void AddReplaceTarget(ModProject p, List<SubmeshTextures>? textures = null) =>
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = Part,
            SubjectCharacter = "Crate", SubjectOutfit = "CrateMk2",
            ReplaceFile = "donor.glb", DonorTextures = textures,
        });

    // ---- the route ----------------------------------------------------------------------------------

    [Fact]
    public void A_replace_on_an_unposed_draw_swaps_the_geometry_directly()
    {
        var env = MakeEnv(out string lod0Hash, out string lod1Hash, skinWidth: 0);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        // the vanilla draw is suppressed and the donor drawn in its place, at BOTH shipped tiers
        Assert.Contains($"[TextureOverride_Rigid_crate_frame]\nhash = {lod0Hash}\nmatch_priority = 0\n", ini);
        Assert.Contains($"[TextureOverride_Rigid_crate_frame_1]\nhash = {lod1Hash}\nmatch_priority = 0\n", ini);
        Assert.Contains("handling = skip\nrun = CommandListRigid_crate_frame\n", ini);
        Assert.Contains("[CommandListRigid_crate_frame]", ini);
        Assert.Contains("vb0 = Resource_RigidVB0_crate_frame", ini);
        Assert.Contains("ib = Resource_RigidIB_crate_frame", ini);
        // one drawindexed per donor submesh, and the game's own bindings put back after them
        Assert.Equal(2, ini.Split("drawindexed = ").Length - 1);
        Assert.Contains("vb0 = Resource_SaveVB0\nvb1 = Resource_SaveVB1\nvb3 = Resource_SaveVB3\nib = Resource_SaveIB", ini);

        // the shipped buffers ARE the compiled donor
        Assert.True(File.Exists(Path.Combine(r.OutDir, "rigid_vb0_crate_frame.buf")));
        Assert.True(File.Exists(Path.Combine(r.OutDir, "rigid_ib_crate_frame.buf")));

        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    [Fact]
    public void An_ambiguous_rigid_target_whose_base_colors_differ_swaps_behind_a_guard()
    {
        // The panel draws on the frame's index buffer, so the swap section fires on its draws too. The
        // two bind different base colors, so the section asks at draw time which one is drawing.
        var env = MakeEnv(out string lod0Hash, out _, skinWidth: 0, twin: true);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        int frame = MigotoEmitter.RetexTag(_frameTexHash), panel = MigotoEmitter.RetexTag(_panelTexHash);
        string v = $"zz_tw_{lod0Hash}";
        Assert.Contains($"[TextureOverride_TwinTag_{_frameTexHash}]\nhash = {_frameTexHash}\n"
            + $"filter_index = {frame}\nmatch_priority = 100\n", ini);
        Assert.Contains($"[TextureOverride_TwinTag_{_panelTexHash}]\nhash = {_panelTexHash}\n"
            + $"filter_index = {panel}\nmatch_priority = 100\n", ini);
        // declared once, written only by the probes: no per-frame reset takes the verdict away
        Assert.Contains($"global ${v} = 0\n", ini);
        Assert.Contains($"[TextureOverride_Rigid_crate_frame]\nhash = {lod0Hash}\nmatch_priority = 0\n"
            + $"$zz_t = ps-t0\nif $zz_t == {frame}\n${v} = 1\nendif\n"
            + $"if $zz_t == {panel}\n${v} = 2\nendif\n", ini);
        Assert.Contains($"if ${v} == 1\nhandling = skip\n"
            + "run = CommandListRigid_crate_frame\nendif\n", ini);
        Assert.Contains(r.Diagnostics, d => d.Contains("'frame' shares a draw signature with 'panel'"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    [Fact]
    public void The_rigid_route_recovers_no_palette_and_runs_no_compute()
    {
        var env = MakeEnv(out _, out _, skinWidth: 0);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.DoesNotContain("CustomShaderRecover", ini);
        Assert.DoesNotContain("CustomShaderConvert", ini);
        Assert.DoesNotContain("CustomShaderSkin", ini);
        Assert.DoesNotContain("Resource_Palette", ini);
        Assert.DoesNotContain("zz_done", ini);
        Assert.DoesNotContain("= ref vs-cb1", ini);   // no draw-constant capture: nothing is being posed
        // nothing solved: the operator and palette artefacts a pooled build ships are absent
        var shipped = Directory.GetFiles(r.OutDir).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain(shipped, f => f!.Contains("_cpinv") || f!.Contains("palette_seed")
            || f!.StartsWith("recover_") || f!.StartsWith("convert_"));
    }

    [Theory]
    [InlineData(false)]   // BlendWeight x1 + BlendIndices x1
    [InlineData(true)]    // BlendIndices alone, each weight implicitly 1
    public void A_one_influence_part_takes_the_pooled_route_as_a_single_part_pool(bool implicitWeights)
    {
        // Its one bone poses every vertex, so the draw the game issues IS posed and the direct swap would
        // freeze it. The part is its own pool and its own anchor: capture, recover, convert, then the
        // donor drawn through the palette — and no direct buffer swap anywhere. Both narrow spellings
        // carry the same skin, so both reach the same emission.
        var env = MakeEnv(out string lod0Hash, out string lod1Hash, skinWidth: 1, implicitWeights);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.Contains($"[TextureOverride_Cap_crate_frame]\nhash = {lod0Hash}\nmatch_priority = 0\n", ini);
        Assert.Contains($"[TextureOverride_Cap_crate_frame_lod1]\nhash = {lod1Hash}\nmatch_priority = 0\n", ini);
        Assert.Contains("CustomShaderRecover_crate_frame_crate_frame", ini);
        Assert.Contains("CustomShaderConvert_crate_frame", ini);
        Assert.Contains("CustomShaderSkin_crate_frame", ini);
        Assert.DoesNotContain("Rigid", ini);
        Assert.Empty(Directory.GetFiles(r.OutDir, "rigid_*"));
        // the pool is the part alone, so the union is its own one-bone table
        string union = File.ReadAllText(Path.Combine(r.OutDir, "union_crate_frame.json"));
        Assert.Contains("\"unionBones\": 1", union);
        Assert.Equal(1, union.Split("\"part\":").Length - 1);
        // the compiled donor's skin ships in the canonical shape the compute pass reads: 6 verts x 32 bytes,
        // which is what the narrow anchor layout would otherwise have cut to one influence
        Assert.Equal(6 * 32, new FileInfo(Path.Combine(r.OutDir, "combined_skin_crate_frame.buf")).Length);
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, r.OutDir);
    }

    [Fact]
    public void A_two_influence_part_takes_the_pooled_route_as_a_single_part_pool()
    {
        // The stored pair is the mesh's whole skin: the game poses the draw by exactly those influences,
        // so the part goes through capture and recovery like the one-influence case — and never the direct
        // swap, which would freeze it at its bind pose.
        var env = MakeEnv(out string lod0Hash, out _, skinWidth: 2);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ModBuilder.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.Contains($"[TextureOverride_Cap_crate_frame]\nhash = {lod0Hash}\nmatch_priority = 0\n", ini);
        Assert.Contains("CustomShaderRecover_crate_frame_crate_frame", ini);
        Assert.Contains("CustomShaderConvert_crate_frame", ini);
        Assert.DoesNotContain("Rigid", ini);
        Assert.Empty(Directory.GetFiles(r.OutDir, "rigid_*"));
        // the compiled donor's skin ships in the canonical shape the compute pass reads
        Assert.Equal(6 * 32, new FileInfo(Path.Combine(r.OutDir, "combined_skin_crate_frame.buf")).Length);
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, r.OutDir);
    }

    [Fact]
    public void Donor_maps_bind_per_submesh_on_a_rigid_draw()
    {
        var env = MakeEnv(out _, out _, skinWidth: 0);
        var p = NewProject();
        WriteDonorGlb();
        using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
            img.SaveAsPng(Path.Combine(_proj, "frame_base.png"));
        AddReplaceTarget(p, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "frame_base.png" },
        });

        var r = ModBuilder.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        // the draw list saves the ps-t range, probes for the slot, binds the encoded map, and restores
        Assert.Contains("Resource_SaveT0 = ref ps-t0", ini);
        Assert.Contains("$zz_slot_a = -1", ini);
        Assert.Contains("ps-t0 = Resource_Tex0", ini);
        Assert.Contains("[Resource_Tex0]\nfilename = donor_crate_frame_s0_a.dds", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    [Fact]
    public void A_pooled_and_a_rigid_replace_ship_in_one_mod()
    {
        // Two routes, one ini: each describes itself in the header, each owns its own sections and shipped
        // files, and no hash or resource name is claimed twice.
        var env = MixedEnv();
        var p = NewProject("Mixed");
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        WriteDonorGlb();
        WriteDonorGlb("donor_body.glb");
        AddReplaceTarget(p);
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundleS", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01", ReplaceFile = "donor_body.glb",
        });

        var r = ModBuilder.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.Contains("; Pooled mesh swap", ini);
        Assert.Contains("; Rigid mesh swap", ini);
        Assert.Contains("[CommandListRigid_crate_frame]", ini);
        Assert.Contains("[CommandListDraw_vesna_body]", ini);
        Assert.Contains("[CustomShaderSkin_vesna_body]", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    /// <summary>The rigid world plus a second, SKINNED subject whose Replace takes the pooled route.</summary>
    private BuildEnv MixedEnv()
    {
        var rigid = MakeEnv(out _, out _, skinWidth: 0);
        string bs = Path.Combine(_root, "rs.bundle");
        SyntheticBundle.BuildOneSkinnedMesh(bs, "c_vesna01_body_lod0", Cloud(28, 21), WrappedTris(28), Bone);
        var skinned = File.ReadAllBytes(bs);

        var pooledModel = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, new[]
        {
            new SubjectPart("body", "c_vesna01_body_lod0", "addr_body", Array.Empty<SubjectMaterial>()),
        }, Skeleton: null, Problems: Array.Empty<string>());

        return rigid with
        {
            ResolveSubject = (c, s) => c == "Vesna" && s == "VesnaSSR01" ? pooledModel
                : rigid.ResolveSubject(c, s),
            ResolveAddress = a => a == "addr_body" ? "bundleS" : rigid.ResolveAddress(a),
            Deobfuscate = id => id == "bundleS" ? skinned : rigid.Deobfuscate(id),
        };
    }

    // ---- a scoped retexture anchored on a rigid-replaced part ----------------------------------------

    /// <summary>One draw-scoped retexture: the stock hash tagged once, and a probe/bind block at each named
    /// anchor mesh.</summary>
    private static ScopedRetexEntry Scoped(string stockHash, string dds, params (string Hash, string Sfx)[] anchors) =>
        new(Name: "frame_a", StockHash: stockHash,
            Images: new[]
            {
                new ScopedRetexImage(dds,
                    anchors.Select(a => new ScopedAnchor(a.Hash, a.Sfx)).ToList()),
            },
            Part: "frame");

    /// <summary>A flat DDS the scoped retexture ships.</summary>
    private string NewDds(string file = "frame_new.dds")
    {
        string path = Path.Combine(_proj, file);
        FlatDds.Write(path, (1, 2, 3, 255));
        return path;
    }

    [Fact]
    public void A_scoped_retexture_anchored_on_a_rigid_replaced_draw_folds_into_that_draws_own_section()
    {
        // One ib hash owns ONE TextureOverride. The rigid replacement claims the frame's hashes, and a
        // scoped retexture anchored on the same ones runs its block INSIDE those sections rather than
        // minting a second override on the hash, which 3DMigoto drops at parse time without a word.
        var emitter = new MigotoEmitter();
        string donor = Path.Combine(_root, "fold-donor");
        WriteCompiledDonor(donor);
        const string stock = "a8d20afb";

        emitter.Build(new PoolBuildRequest
        {
            Pipelines = Array.Empty<ReplacePipeline>(),
            OutDir = _out,
            Rigids = new[]
            {
                new RigidReplace
                {
                    Suffix = "crate_frame", DonorDir = donor, Hash = "aaaa0001",
                    TierHashes = new[] { "aaaa0002" },
                },
            },
            ScopedRetextures = new[]
            {
                Scoped(stock, NewDds(), ("aaaa0001", "crate_frame_lod0"), ("aaaa0002", "crate_frame_lod1")),
            },
        });
        string ini = File.ReadAllText(Path.Combine(_out, "mod.ini"));

        // the tag ships, and the scoped retexture minted no section of its own
        Assert.Contains($"[TextureOverride_RetexTag_{stock}]", ini);
        Assert.DoesNotContain("[TextureOverride_RetexScope_", ini);
        // both roles in ONE section per hash, in the pooled twin's order: skip + donor draw, then the block
        Assert.Contains("[TextureOverride_Rigid_crate_frame]\nhash = aaaa0001\nmatch_priority = 0\n"
            + "handling = skip\nrun = CommandListRigid_crate_frame\n"
            + "Resource_RtxSave0 = ref ps-t0\n", ini);
        Assert.Contains("[TextureOverride_Rigid_crate_frame_1]\nhash = aaaa0002\nmatch_priority = 0\n"
            + "handling = skip\nrun = CommandListRigid_crate_frame\n"
            + "Resource_RtxSave0 = ref ps-t0\n", ini);
        // the probe/bind/restore shape rides along whole, once per anchored hash
        Assert.Equal(2, CountOf(ini, $"if $zz_rt == {MigotoEmitter.RetexTag(stock)}\n$zz_rslot = 0\nendif\n"));
        Assert.Equal(2, CountOf(ini, "if $zz_rslot == 0\nps-t0 = Resource_Rtx0\nendif\n"));
        Assert.Equal(2, CountOf(ini, "post ps-t0 = Resource_RtxSave0\n"));
        Assert.Equal(1, CountOf(ini, "hash = aaaa0001"));
        Assert.Equal(1, CountOf(ini, "hash = aaaa0002"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    [Fact]
    public void A_scoped_retexture_on_a_draw_nothing_replaced_still_mints_its_own_section()
    {
        // The fold is about a hash another section already owns. An anchor no replacement claims is the
        // scoped retexture's alone, and keeps the section it always had.
        var emitter = new MigotoEmitter();
        string donor = Path.Combine(_root, "unclaimed-donor");
        WriteCompiledDonor(donor);
        const string stock = "a8d20afb";

        emitter.Build(new PoolBuildRequest
        {
            Pipelines = Array.Empty<ReplacePipeline>(),
            OutDir = _out,
            Rigids = new[]
            {
                new RigidReplace { Suffix = "crate_frame", DonorDir = donor, Hash = "aaaa0001" },
            },
            ScopedRetextures = new[] { Scoped(stock, NewDds(), ("bbbb0001", "crate_lid_lod0")) },
        });
        string ini = File.ReadAllText(Path.Combine(_out, "mod.ini"));

        Assert.Contains("[TextureOverride_Rigid_crate_frame]\nhash = aaaa0001\nmatch_priority = 0\n", ini);
        Assert.Contains("[TextureOverride_RetexScope_crate_lid_lod0]\nhash = bbbb0001\nmatch_priority = 0\n", ini);
        // and the replacement's own section stays free of the block
        Assert.DoesNotContain("run = CommandListRigid_crate_frame\nResource_RtxSave0", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    private static int CountOf(string text, string token)
    {
        int n = 0;
        for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(token, i + token.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // ---- section naming ------------------------------------------------------------------------------

    [Fact]
    public void Two_rigid_replacements_whose_derived_tier_names_would_collide_keep_distinct_sections()
    {
        // The tier suffix is appended to the replacement's own: "t" at tier 1 and "t_1" at tier 0 both
        // derive Rigid_..._t_1. Unique SUFFIXES do not make unique derived NAMES, and a duplicate-named
        // section is dropped at parse time without a word.
        var emitter = new MigotoEmitter();
        string donor = Path.Combine(_root, "rigid-donor");
        WriteCompiledDonor(donor);

        var req = new PoolBuildRequest
        {
            Pipelines = Array.Empty<ReplacePipeline>(),
            OutDir = _out,
            Rigids = new[]
            {
                new RigidReplace
                {
                    Suffix = "c_t", DonorDir = donor, Hash = "aaaa0001",
                    TierHashes = new[] { "aaaa0002" },
                },
                new RigidReplace { Suffix = "c_t_1", DonorDir = donor, Hash = "aaaa0003" },
            },
        };

        emitter.Build(req);
        string ini = File.ReadAllText(Path.Combine(_out, "mod.ini"));

        Assert.Contains("[TextureOverride_Rigid_c_t]\nhash = aaaa0001\nmatch_priority = 0\n", ini);
        Assert.Contains("[TextureOverride_Rigid_c_t_1]\nhash = aaaa0002\nmatch_priority = 0\n", ini);
        Assert.Contains("[TextureOverride_Rigid_c_t_1_]\nhash = aaaa0003\nmatch_priority = 0\n", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    /// <summary>A compiled donor dir the rigid emission consumes: one submesh, positions only.</summary>
    private void WriteCompiledDonor(string dir)
    {
        Directory.CreateDirectory(dir);
        var verts = Cloud(3, 3);
        var vb = new byte[verts.Length * 4];
        Buffer.BlockCopy(verts, 0, vb, 0, vb.Length);
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), vb);
        File.WriteAllBytes(Path.Combine(dir, "ib.buf"), new byte[] { 0, 0, 1, 0, 2, 0 });
        File.WriteAllText(Path.Combine(dir, "meta.json"),
            "{\n  \"mesh\": \"donor\", \"verts\": 3, \"boneCount\": 0,\n"
            + "  \"indexFormat\": \"R16_UINT\", \"indexBufferBytes\": 6,\n"
            + "  \"streams\": [{ \"stream\": 0, \"stride\": 12 }],\n"
            + "  \"submeshes\": [{ \"firstByte\": 0, \"indexCount\": 3, \"baseVertex\": 0 }]\n}\n");
    }

    // ---- the change's toggle key, end to end ---------------------------------------------------------

    /// <summary>The off meaning authored on the row reaches the rigid section: the suppression answers to the
    /// mod's key alone while the donor draw keeps the change's own, so an off change leaves the draw absent
    /// rather than handing it back to the stock mesh.</summary>
    [Fact]
    public void A_keyed_rigid_replace_set_to_hide_when_off_gates_its_skip_apart_from_its_draw()
    {
        var env = MakeEnv(out _, out _, skinWidth: 0);
        var p = NewProject();
        p.Info.ToggleKey = "F6";
        WriteDonorGlb();
        AddReplaceTarget(p);
        p.SetChangeKey("Crate", "CrateMk2", Part, EditVerbs.Replace, "F8", hideWhenOff: true);

        var r = ModBuilder.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.Contains("if $zz_key_f6 == 1\nhandling = skip\nendif\n"
            + "if $zz_key_f6 == 1\nif $zz_key_f8 == 1\nrun = CommandListRigid_crate_frame\nendif\nendif\n", ini);
        Assert.DoesNotContain("handling = skip\nrun = CommandListRigid_crate_frame", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    /// <summary>The same row left on the vanilla off meaning keeps both roles under one gate — the emission
    /// a keyed rigid replacement has always had.</summary>
    [Fact]
    public void A_keyed_rigid_replace_reverting_to_vanilla_keeps_one_gate()
    {
        var env = MakeEnv(out _, out _, skinWidth: 0);
        var p = NewProject();
        p.Info.ToggleKey = "F6";
        WriteDonorGlb();
        AddReplaceTarget(p);
        p.SetChangeKey("Crate", "CrateMk2", Part, EditVerbs.Replace, "F8");

        var r = ModBuilder.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.Contains("if $zz_key_f6 == 1\nif $zz_key_f8 == 1\n"
            + "handling = skip\nrun = CommandListRigid_crate_frame\nendif\nendif\n", ini);
    }

    [Fact]
    public void A_hidden_part_and_a_rigid_replaced_one_ship_side_by_side()
    {
        // The rigid section owns its own hashes, so the hide pass must not mint a second section on them.
        var env = MakeEnv(out string lod0Hash, out _, skinWidth: 0);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);
        p.Hidden.Add(new HiddenMesh { Character = "Crate", Outfit = "CrateMk2", Mesh = Part });

        var warnings = new List<string>();
        var edits = VerbDerivation.Derive(p, env.ResolveSubject, warnings);

        // Hide wins over the edit on one mesh, so the build never sees both on it
        Assert.Equal(new[] { EditVerbs.Hide }, edits.Select(e => e.Verb).ToArray());
        Assert.Contains(warnings, w => w.Contains("is hidden. Its mesh edit is not in this build"));

        var r = ModBuilder.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"hash = {lod0Hash}", ini);
        Assert.DoesNotContain("CommandListRigid", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }
}
