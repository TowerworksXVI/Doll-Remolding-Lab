using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    /// <summary>One region of a two-region part: <see cref="RegionVerts"/> vertices of their own, moved
    /// along x so two regions stand well clear of each other.</summary>
    private const int RegionVerts = 16;

    /// <summary>Indices one region's triangles take, which is also its submesh's index count.</summary>
    private const int RegionIndices = RegionVerts * 3;

    private static float[] Region(int seed, float shiftX)
    {
        var pos = Cloud(RegionVerts, seed);
        for (int i = 0; i < pos.Length; i += 3) pos[i] += shiftX;
        return pos;
    }

    private static float[] Regions(params float[][] regions) =>
        regions.SelectMany(r => r).ToArray();

    /// <summary>Triangles for two regions laid end to end, each wrapping inside its OWN vertices — so a
    /// submesh over the first half draws the first region alone. <paramref name="span"/> picks which
    /// vertex closes each triangle, which is how a tier gets an index buffer of its own: two meshes with
    /// the same indices are one draw signature, and the build reads them as one mesh.</summary>
    private static int[] RegionTris(int span = 2) =>
        Enumerable.Range(0, 2).SelectMany(region =>
            Enumerable.Range(0, RegionVerts).SelectMany(v => new[]
            {
                region * RegionVerts + v,
                region * RegionVerts + (v + 1) % RegionVerts,
                region * RegionVerts + (v + span) % RegionVerts,
            })).ToArray();

    /// <summary>The subject: one part with a lod1 sibling, drawn from bundles the caller shapes.
    /// <paramref name="skinWidth"/> of 0 builds a mesh with no skin channels at all (the prop shape, the
    /// only one this route takes); 1 and 2 the below-four widths that go pooled.
    /// <paramref name="implicitWeights"/> picks the one-influence spelling the game's own weapon parts
    /// ship: indices alone, each weight implicitly 1.</summary>
    /// <param name="twin">Adds a second static part on the frame's exact index buffer with geometry of
    /// its own, and gives the two base colors of their own — the shape where only the textures bound at
    /// the draw tell the two apart.</param>
    /// <param name="tierBindsAnotherMaterial">Gives the frame a material of its own and the TIER a
    /// different one, and puts the tier's geometry well away from the frame's. Nothing then says where the
    /// tier draws the frame's material region, which is the case the tier material map reads as
    /// unresolved.</param>
    private BuildEnv MakeEnv(out string lod0Hash, out string lod1Hash, int skinWidth = 0,
        bool implicitWeights = false, bool twin = false, bool effect = false,
        bool tierBindsAnotherMaterial = false, bool tierOrdersTwoMaterialsItsOwnWay = false,
        bool tierTwin = false, bool ambiguousTarget = false, bool ambiguousOtherPart = false,
        bool unnamedMaterial = false, bool secondFlaggedPart = false)
    {
        string b0 = Path.Combine(_root, "r0.bundle");
        string b1 = Path.Combine(_root, "r1.bundle");
        float[] tierCloud = tierBindsAnotherMaterial
            ? Cloud(24, 9).Select(v => v + 50f).ToArray()
            : Cloud(24, 9);
        if (tierOrdersTwoMaterialsItsOwnWay)
        {
            // Two regions well apart, one material each. The tier holds the same two regions with its
            // submeshes — and so its materials — the other way round, so the position a range drew at
            // says nothing about where its material is bound at the tier.
            SyntheticBundle.BuildOneMesh(b0, Part, Regions(Region(5, 0f), Region(7, 10f)), RegionTris(),
                submeshIndexCounts: new[] { RegionIndices, RegionIndices });
            SyntheticBundle.BuildOneMesh(b1, Tier, Regions(Region(7, 10f), Region(5, 0f)),
                RegionTris(span: 3), submeshIndexCounts: new[] { RegionIndices, RegionIndices });
        }
        else if (skinWidth == 0)
        {
            SyntheticBundle.BuildOneMesh(b0, Part, Cloud(32, 5), WrappedTris(32));
            SyntheticBundle.BuildOneMesh(b1, Tier, tierCloud, WrappedTris(24));
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
            }, AmbiguousMaterials: ambiguousOtherPart));
            addresses["addr_panel"] = "bundle2";
        }
        else if (tierTwin)
        {
            // The panel draws on the TIER's index buffer, so a section on the tier cannot tell the two
            // apart and the tier's draw stays inside the guard's verdict. The two bind base colours of
            // their own, which is what lets the guard be built at all.
            string bt = Path.Combine(_root, "rt.bundle");
            SyntheticBundle.Build(bt,
                new SyntheticBundle.TextureSpec("tex_frame_d", 8, 8,
                    SyntheticBundle.SolidRgba32(8, 8, 200, 100, 50, 255), ColorSpace: 1),
                new SyntheticBundle.TextureSpec("tex_panel_d", 8, 8,
                    SyntheticBundle.SolidRgba32(8, 8, 20, 210, 90, 255), ColorSpace: 1));
            bytes["bundleT"] = File.ReadAllBytes(bt);
            _frameTexHash = SyntheticBundle.StockTexHash(bytes["bundleT"], "tex_frame_d");
            _panelTexHash = SyntheticBundle.StockTexHash(bytes["bundleT"], "tex_panel_d");
            string b2 = Path.Combine(_root, "r2.bundle");
            SyntheticBundle.BuildOneMesh(b2, Panel, Cloud(24, 21), WrappedTris(24));
            bytes["bundle2"] = File.ReadAllBytes(b2);
            frameMaterials = new[]
            {
                new SubjectMaterial("m_frame", 1, "cab-frame",
                    new[] { new SubjectMap("_BaseMap", "tex_frame_d", "bundleT") }, Bundle: "bundleMat"),
            };
            parts.Add(new SubjectPart("panel", Panel, "addr_panel", new[]
            {
                new SubjectMaterial("m_panel", 2, "cab-panel",
                    new[] { new SubjectMap("_BaseMap", "tex_panel_d", "bundleT") }),
            }, AmbiguousMaterials: ambiguousOtherPart));
            addresses["addr_panel"] = "bundle2";
        }
        else if (effect)
        {
            string bt = Path.Combine(_root, "reffect.bundle");
            SyntheticBundle.BuildOneTexture(bt, "tex_frame_effect", 8, 8, 80, 120, 200, 255,
                colorSpace: 1);
            bytes["bundleT"] = File.ReadAllBytes(bt);
            frameMaterials = new[]
            {
                new SubjectMaterial("m_frame", 1, "cab-frame",
                    new[] { new SubjectMap("_BlendTex", "tex_frame_effect", "bundleT") }),
            };
        }
        if (secondFlaggedPart)
        {
            // A second part of the same subject, flagged like the first and on an index buffer of its
            // own, so one build can change both and each has something of its own to be told about.
            string b3 = Path.Combine(_root, "r3.bundle");
            SyntheticBundle.BuildOneMesh(b3, Panel, Cloud(20, 21), WrappedTris(20));
            bytes["bundle2"] = File.ReadAllBytes(b3);
            parts.Add(new SubjectPart("panel", Panel, "addr_panel", new[]
            {
                new SubjectMaterial("m_panel", 2, "cab-panel", Array.Empty<SubjectMap>(),
                    Bundle: "bundleMat"),
            }, AmbiguousMaterials: true));
            addresses["addr_panel"] = "bundle2";
        }

        var tierSlot = new RecipeTierSlot(Tier, "addr_frame_l1");
        if (tierTwin)
            // the tier binds a material of its own, so the map has something to say about this tier —
            // which is the point: the guard is what keeps it from being acted on
            tierSlot = tierSlot with
            {
                Materials = new[] { new TierMaterialRef("bundleMat", 2, true) },
            };
        if (tierOrdersTwoMaterialsItsOwnWay)
        {
            // one material per region at lod0; the tier binds the second one FIRST and something of its
            // own second, so one range has a place at the tier and the other has none
            frameMaterials = new[]
            {
                new SubjectMaterial("m_frame", 1, "cab-frame", Array.Empty<SubjectMap>(),
                    Bundle: "bundleMat"),
                new SubjectMaterial("m_trim", 2, "cab-frame", Array.Empty<SubjectMap>(),
                    Bundle: "bundleMat"),
            };
            tierSlot = tierSlot with
            {
                Materials = new[]
                {
                    new TierMaterialRef("bundleMat", 2, true),
                    new TierMaterialRef("bundleMat", 7, true),
                },
            };
        }
        if (tierBindsAnotherMaterial)
        {
            frameMaterials = new[]
            {
                // a material the read could not name is described by its position instead
                new SubjectMaterial(unnamedMaterial ? "" : "m_frame", 1, "cab-frame",
                    Array.Empty<SubjectMap>(), Bundle: "bundleMat"),
            };
            tierSlot = tierSlot with
            {
                Materials = new[] { new TierMaterialRef("bundleMat", 2, true) },
            };
        }
        parts.Insert(0, new SubjectPart("frame", Part, "addr_frame", frameMaterials,
            SiblingTiers: new[] { tierSlot }, AmbiguousMaterials: ambiguousTarget));

        var model = new SubjectModel("Crate", "CrateMk2", SubjectSource.Prefab, parts.ToArray(),
            Skeleton: null, Problems: Array.Empty<string>());
        return new BuildEnv(
            (c, s) => c == "Crate" && s == "CrateMk2" ? model : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0").Exact();
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

        var r = ReleasedBuild.Build(p, env, _out);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        // the vanilla draw is suppressed and the donor drawn in its place, at BOTH shipped tiers
        Assert.Contains($"[TextureOverride_Rigid_crate_frame]\nhash = {lod0Hash}\nmatch_priority = 0\n{ModBuilderTests.DrawGuard(ini, lod0Hash)}", ini);
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
        BuildWatermarkTests.AssertStamped(r);
    }

    /// <summary>The tier binds a material the lod0 renderer never had, so there is nowhere at that detail
    /// level the new mesh can be drawn under the material it was made for. The build says so where the
    /// modder can read it, and draws nothing there rather than drawing the range under the wrong
    /// material.</summary>
    [Fact]
    public void A_tier_with_no_place_for_a_new_mesh_says_so_and_draws_nothing_there()
    {
        var env = MakeEnv(out _, out string lod1Hash, skinWidth: 0, tierBindsAnotherMaterial: true);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        var warning = Assert.Single(r.Warnings, w => w.Contains("no place to draw"));
        Assert.Contains("At the furthest detail level", warning);
        Assert.Contains("'m_frame'", warning);
        Assert.EndsWith("so that part of it is not drawn there.", warning);
        Assert.Contains(r.Diagnostics, d => d.Contains($"{Tier}: 0→none unresolved"));

        // the tier's own section still suppresses the vanilla draw, and issues no donor draw at all
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        string tierSection = ini[ini.IndexOf($"hash = {lod1Hash}", StringComparison.Ordinal)..];
        tierSection = tierSection[..tierSection.IndexOf("\n[", StringComparison.Ordinal)];
        Assert.Contains("handling = skip", tierSection);
        Assert.DoesNotContain("run = CommandListRigid", tierSection);
        Assert.DoesNotContain($"[TextureOverride_Rigid_crate_frame_1_Draw", ini);
        // lod0 is untouched: both donor ranges still draw there
        Assert.Contains("run = CommandListRigid_crate_frame\n", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    /// <summary>A reduced detail level draws a donor range where the game binds the SAME material the
    /// range was authored under, and nowhere else. The tier here binds one of the part's two materials and
    /// orders it first: that range moves to the tier's first submesh, and the range whose material the
    /// tier does not bind at all is not drawn there — which the build says in the modder's words. The
    /// closest detail level keeps drawing range k at submesh k.</summary>
    [Fact]
    public void A_tier_draws_only_the_range_whose_material_it_binds_and_says_so_about_the_other()
    {
        var env = MakeEnv(out _, out string lod1Hash, tierOrdersTwoMaterialsItsOwnWay: true);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        // the map the two renderers produced: the second material pairs by identity onto the tier's
        // first position, and the first material has no place at this tier at all
        Assert.Contains(r.Diagnostics, d => d.Contains($"{Tier}: 0→none unresolved, 1→0 identity"));
        var warning = Assert.Single(r.Warnings, w => w.Contains("no place to draw"));
        Assert.Contains("At the furthest detail level", warning);
        Assert.Contains("'m_frame'", warning);
        Assert.EndsWith("so that part of it is not drawn there.", warning);

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        // at the tier the matched range draws at the position binding its material, and the unmatched
        // range draws at no position of that tier
        AssertTierRuns(ini, tierPosition: 0, donorRange: 1);
        Assert.DoesNotContain("[TextureOverride_Rigid_crate_frame_1_DrawS1]", ini);
        Assert.DoesNotContain($"run = CommandListRigidS0_crate_frame\n",
            TierSection(ini, "[TextureOverride_Rigid_crate_frame_1_DrawS0]"));
        // the closest detail level is untouched: range k still draws at its own submesh k
        Assert.Contains("run = CommandListRigidS0_crate_frame\n",
            TierSection(ini, "[TextureOverride_Rigid_crate_frame_DrawS0]"));
        Assert.Contains("run = CommandListRigidS1_crate_frame\n",
            TierSection(ini, "[TextureOverride_Rigid_crate_frame_DrawS1]"));
        Assert.Contains($"hash = {lod1Hash}", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    /// <summary>A material the read could not name is named by its position instead — in the same words
    /// and the same numbering the Edit page falls back to, so the modder who goes looking for it on the
    /// Edit page finds the material the sentence is about.</summary>
    [Fact]
    public void A_material_with_no_readable_name_is_named_the_way_the_edit_page_names_it()
    {
        var env = MakeEnv(out _, out _, tierBindsAnotherMaterial: true, unnamedMaterial: true);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        var warning = Assert.Single(r.Warnings, w => w.Contains("no place to draw"));
        Assert.Contains("that uses material 0,", warning);
    }

    /// <summary>The game files can hold more than one stock material configuration for a part. The build
    /// is made against one of them, so the modder is told — once for the part, however many changes touch
    /// it, in a sentence that names which part it is about.</summary>
    [Fact]
    public void A_part_with_more_than_one_stock_material_configuration_says_so_once_and_names_itself()
    {
        var env = MakeEnv(out _, out _, ambiguousTarget: true);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        var warning = Assert.Single(r.Warnings, w => w.Contains("stock material configuration"));
        Assert.Equal("'frame': This part has more than one stock material configuration in the game files. "
            + "The edit was built against the most common one; on the others, colors may sit on the wrong "
            + "pieces.", warning);
    }

    /// <summary>A build that changes two such parts has something to say about each of them, and says
    /// which is which: two sentences that differ, each naming its own part. Identical sentences would
    /// reach the modder as one line about neither.</summary>
    [Fact]
    public void Two_parts_with_more_than_one_stock_material_configuration_are_each_named()
    {
        var env = MakeEnv(out _, out _, ambiguousTarget: true, secondFlaggedPart: true);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle2", ObjectName = Panel,
            SubjectCharacter = "Crate", SubjectOutfit = "CrateMk2",
            ReplaceFile = "donor.glb",
        });

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        var said = r.Warnings.Where(w => w.Contains("stock material configuration")).ToList();
        Assert.Equal(2, said.Count);
        Assert.Equal(2, said.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(said, w => w.StartsWith("'frame': ", StringComparison.Ordinal));
        Assert.Single(said, w => w.StartsWith("'panel': ", StringComparison.Ordinal));
    }

    /// <summary>The same part, left alone: a mod that changes nothing about it has nothing to say about
    /// which of the game's own configurations is on screen.</summary>
    [Fact]
    public void A_part_this_build_does_not_change_says_nothing_about_its_material_configurations()
    {
        var env = MakeEnv(out _, out _, skinWidth: 0, twin: true, ambiguousOtherPart: true);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.DoesNotContain(r.Warnings, w => w.Contains("stock material configuration"));
    }

    /// <summary>A tier whose draws another mesh's cannot be told apart from keeps its draw inside the
    /// guard's verdict, so the material map is not acted on there however it reads. The modder is told
    /// that once, about the tier — never that a part of the new mesh is left undrawn there, which is what
    /// the per-material sentences would have said about an emission this tier does not get.</summary>
    [Fact]
    public void A_tier_that_cannot_be_told_apart_from_another_mesh_says_so_once_and_keeps_its_drawing()
    {
        var env = MakeEnv(out _, out string lod1Hash, tierTwin: true);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        var warning = Assert.Single(r.Warnings, w => w.Contains("can't be told apart from another mesh"));
        Assert.Contains("At the furthest detail level", warning);
        Assert.EndsWith("Some of it may show under the wrong material.", warning);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("no place to draw"));

        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        // the tier's own section still runs the whole donor behind the guard, and no per-range sections
        // are minted for it
        Assert.Contains("run = CommandListRigid_crate_frame\n",
            TierSection(ini, $"[TextureOverride_Rigid_crate_frame_1]\nhash = {lod1Hash}"));
        Assert.DoesNotContain("[TextureOverride_Rigid_crate_frame_1_Draw", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    private static void AssertTierRuns(string ini, int tierPosition, int donorRange)
    {
        string section = TierSection(ini, $"[TextureOverride_Rigid_crate_frame_1_DrawS{tierPosition}]");
        Assert.Contains($"run = CommandListRigidS{donorRange}_crate_frame\n", section);
        Assert.DoesNotContain($"run = CommandListRigidS{1 - donorRange}_crate_frame\n", section);
    }

    private static string TierSection(string ini, string header)
    {
        int start = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"section missing: {header}");
        int end = ini.IndexOf("\n[", start + header.Length, StringComparison.Ordinal);
        return end < 0 ? ini[start..] : ini[start..end];
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        int frame = MigotoEmitter.RetexTag(_frameTexHash), panel = MigotoEmitter.RetexTag(_panelTexHash);
        string v = $"zz_tw_{ModBuilderTests.SelectorKey(ini, lod0Hash)}";
        Assert.Contains($"[TextureOverride_TwinTag_{_frameTexHash}]\nhash = {_frameTexHash}\n"
            + $"filter_index = {frame}\nmatch_priority = 100\n", ini);
        Assert.Contains($"[TextureOverride_TwinTag_{_panelTexHash}]\nhash = {_panelTexHash}\n"
            + $"filter_index = {panel}\nmatch_priority = 100\n", ini);
        // declared once, written only by the probes: no per-frame reset takes the verdict away
        Assert.Contains($"global ${v} = 0\n", ini);
        Assert.Contains($"[TextureOverride_Rigid_crate_frame]\nhash = {lod0Hash}\nmatch_priority = 0\n{ModBuilderTests.DrawGuard(ini, lod0Hash)}"
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.Contains($"[TextureOverride_Cap_crate_frame]\nhash = {lod0Hash}\nmatch_priority = 0\n{ModBuilderTests.DrawGuard(ini, lod0Hash)}", ini);
        Assert.Contains($"[TextureOverride_Cap_crate_frame_lod1]\nhash = {lod1Hash}\nmatch_priority = 0\n", ini);
        // its own pool and its own anchor: the per-copy pose passes recover and skin it at each level
        Assert.Contains("CustomShaderPosePalette_crate_frame_crate_frame", ini);
        Assert.Contains("CustomShaderPosePalette_crate_frame_lod1_crate_frame", ini);
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
        HlslCheck.EveryShaderCompilesClean(ini, r.OutDir);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.Contains($"[TextureOverride_Cap_crate_frame]\nhash = {lod0Hash}\nmatch_priority = 0\n{ModBuilderTests.DrawGuard(ini, lod0Hash)}", ini);
        Assert.Contains("CustomShaderPosePalette_crate_frame_crate_frame", ini);
        Assert.DoesNotContain("Rigid", ini);
        Assert.Empty(Directory.GetFiles(r.OutDir, "rigid_*"));
        // the compiled donor's skin ships in the canonical shape the compute pass reads
        Assert.Equal(6 * 32, new FileInfo(Path.Combine(r.OutDir, "combined_skin_crate_frame.buf")).Length);
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, r.OutDir);
        HlslCheck.EveryShaderCompilesClean(ini, r.OutDir);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        // the draw list saves the ps-t range, probes for the slot, binds the encoded map, and restores
        Assert.Contains("Resource_SaveT0 = ref ps-t0", ini);
        Assert.Contains("$zz_slot_a = -1", ini);
        Assert.Contains("ps-t0 = Resource_Tex0", ini);
        Assert.Contains("[Resource_Tex0]\nfilename = donor_crate_frame_s0_a.dds", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }

    [Fact]
    public void An_effect_map_on_a_rigid_replacement_encodes_probes_binds_and_repairs()
    {
        var env = MakeEnv(out _, out _, skinWidth: 0, effect: true);
        var project = NewProject("Rigid effect");
        WriteDonorGlb();
        using (var image = new Image<Rgba32>(8, 8, new Rgba32(30, 60, 90, 255)))
            image.SaveAsPng(Path.Combine(_proj, "frame_effect.png"));
        AddReplaceTarget(project, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Blend = "frame_effect.png" },
        });

        var result = ReleasedBuild.Build(project, env, _out, zip: false);

        string ini = File.ReadAllText(Path.Combine(result.OutDir, "mod.ini"));
        Assert.Contains($"filter_index = {MigotoEmitter.FilterBlend}", ini);
        Assert.Contains("$zz_slot_b = -1", ini);
        Assert.Contains("ps-t2 = Resource_Tex0", ini);
        Assert.Single(Directory.GetFiles(result.OutDir, "donor_*_b.dds"));
        using var repair = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.OutDir, "repair.json")));
        Assert.Equal("Authored", repair.RootElement.GetProperty("changes")[0]
            .GetProperty("textures")[0].GetProperty("blend").GetProperty("origin").GetString());
    }

    [Fact]
    public void A_pooled_and_a_rigid_replace_ship_in_one_mod()
    {
        // Two routes, one ini: one header says what both replacements do, each owns its own sections and
        // shipped files, and no hash or resource name is claimed twice.
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.StartsWith("; Generated by Doll Remolding Lab test-1.0.\n; Replaces the meshes of 2 parts.\n"
            + "; At each draw of a replaced mesh, custom shader passes read the game's posed vertices,\n"
            + "; recover that draw's bone matrices (CustomShaderGather_*, CustomShaderPosePalette_*),\n"
            + "; skin the replacement with them (CustomShaderPoseSkin_*) and draw it in place of the\n"
            + "; original (CommandListDraw_*), so each copy of the part on screen is posed from its own draw.\n"
            + "; crate_frame's replacement is drawn in place of the original without per-vertex\n"
            + "; posing (CommandListRigid_*).\n",
            ini.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("[CommandListRigid_crate_frame]", ini);
        Assert.Contains("[CommandListDraw_vesna_body]", ini);
        Assert.Contains("[CustomShaderPosePalette_vesna_body_vesna_body]", ini);
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

        var mixed = rigid with
        {
            ResolveSubject = (c, s) => c == "Vesna" && s == "VesnaSSR01" ? pooledModel
                : rigid.ResolveSubject(c, s),
            ResolveAddress = a => a == "addr_body" ? "bundleS" : rigid.ResolveAddress(a),
            Deobfuscate = id => id == "bundleS" ? skinned : rigid.Deobfuscate(id),
        };
        return mixed.Exact();
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
                    TierLayouts = SyntheticPool.RigidTiers(SyntheticPool.PositionsLayout(), "aaaa0002"),
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
            + "local $zz_bt0 = 0\n", ini);
        Assert.Contains("[TextureOverride_Rigid_crate_frame_1]\nhash = aaaa0002\nmatch_priority = 0\n"
            + "handling = skip\nrun = CommandListRigid_crate_frame\n"
            + "local $zz_bt0 = 0\n", ini);
        Assert.Equal(2, CountOf(ini, "Resource_RtxSave0 = ref ps-t0\n"));
        // the probe/bind/restore shape rides along whole, once per anchored hash
        Assert.Equal(2, CountOf(ini, $"if $zz_rt == {MigotoEmitter.RetexTag(stock)}\n$zz_rslot = 0\nendif\n"));
        Assert.Equal(2, CountOf(ini, "if $zz_rslot == 0\nps-t0 = Resource_Rtx0\n$zz_bt0 = 1\nendif\n"));
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
                    TierLayouts = SyntheticPool.RigidTiers(SyntheticPool.PositionsLayout(), "aaaa0002"),
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
            + "  " + SyntheticPool.ChannelsJson(SyntheticPool.PositionsLayout()) + ",\n"
            + "  \"submeshes\": [{ \"firstByte\": 0, \"indexCount\": 3, \"baseVertex\": 0 }]\n}\n");
    }

    // ---- a tier storing a stream differently from the replaced part -----------------------------------
    // The donor draw at a tier is read through the tier mesh's input layout, so a stream sliced for the
    // replaced part is only readable there when the two layouts agree on it.

    /// <summary>Position float3 + one UV pair, all in stream 0: UVs half (stride 16) or float32 (stride
    /// 20), with an optional second UV pair after them.</summary>
    private static UnityMesh.ChannelDef[] StaticLayout(bool halfUv, bool secondUv = false)
    {
        var t = new UnityMesh.ChannelDef[14];
        t[0] = new(0, 0, 0, 3);
        t[4] = new(0, 12, halfUv ? 1 : 0, 2);
        if (secondUv) t[5] = new(0, halfUv ? 16 : 20, halfUv ? 1 : 0, 2);
        return t;
    }

    /// <summary>A compiled donor in <see cref="StaticLayout"/> with half UVs, recording that table.</summary>
    private static void WriteLayoutDonor(string dir)
    {
        Directory.CreateDirectory(dir);
        var vb = new byte[3 * 16];
        for (int v = 0; v < 3; v++)
        {
            for (int c = 0; c < 3; c++) BitConverter.GetBytes(v + c * 0.5f).CopyTo(vb, v * 16 + c * 4);
            BitConverter.GetBytes((Half)(0.25f * v)).CopyTo(vb, v * 16 + 12);
            BitConverter.GetBytes((Half)(1f - 0.25f * v)).CopyTo(vb, v * 16 + 14);
        }
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), vb);
        File.WriteAllBytes(Path.Combine(dir, "ib.buf"), new byte[] { 0, 0, 1, 0, 2, 0 });
        string channels = string.Join(", ", StaticLayout(halfUv: true).Select(c =>
            $"{{ \"stream\": {c.Stream}, \"offset\": {c.Offset}, \"format\": {c.Format}, \"dimension\": {c.Dimension} }}"));
        File.WriteAllText(Path.Combine(dir, "meta.json"),
            "{\n  \"mesh\": \"donor\", \"verts\": 3, \"boneCount\": 0,\n"
            + "  \"indexFormat\": \"R16_UINT\", \"indexBufferBytes\": 6,\n"
            + "  \"streams\": [{ \"stream\": 0, \"stride\": 16 }],\n"
            + $"  \"channels\": [{channels}],\n"
            + "  \"submeshes\": [{ \"firstByte\": 0, \"indexCount\": 3, \"baseVertex\": 0 }]\n}\n");
    }

    private string BuildLayoutRigid(params (string Hash, UnityMesh.ChannelDef[] Layout)[] tiers)
    {
        string donor = Path.Combine(_root, "layout-donor");
        WriteLayoutDonor(donor);
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            Pipelines = Array.Empty<ReplacePipeline>(),
            OutDir = _out,
            Rigids = new[]
            {
                new RigidReplace
                {
                    Suffix = "crate_frame", DonorDir = donor, Hash = "aaaa0001",
                    TierHashes = tiers.Select(t => t.Hash).ToArray(),
                    TierLayouts = tiers.ToDictionary(t => t.Hash,
                        t => new RigidTierLayout("frame_" + t.Hash, t.Layout)),
                },
            },
        });
        return File.ReadAllText(Path.Combine(_out, "mod.ini"));
    }

    [Fact]
    public void A_rigid_tier_storing_a_stream_differently_draws_a_reencoded_stream()
    {
        string ini = BuildLayoutRigid(("aaaa0002", StaticLayout(halfUv: false)), ("aaaa0003", StaticLayout(halfUv: true)));

        // the variant: positions verbatim, the half UVs widened to float32
        var primary = File.ReadAllBytes(Path.Combine(_out, "rigid_vb0_crate_frame.buf"));
        var variant = File.ReadAllBytes(Path.Combine(_out, "rigid_vb0_crate_frame_v1.buf"));
        Assert.Equal(3 * 20, variant.Length);
        for (int v = 0; v < 3; v++)
        {
            Assert.Equal(primary.AsSpan(v * 16, 12).ToArray(), variant.AsSpan(v * 20, 12).ToArray());
            Assert.Equal(0.25f * v, BitConverter.ToSingle(variant, v * 20 + 12));
            Assert.Equal(1f - 0.25f * v, BitConverter.ToSingle(variant, v * 20 + 16));
        }
        Assert.False(File.Exists(Path.Combine(_out, "rigid_vb0_crate_frame_v2.buf")));

        Assert.Contains("global $zz_rvb0_crate_frame = 0\n", ini);
        Assert.Contains("[Resource_RigidVB0_crate_frame_v1]\ntype = Buffer\nstride = 20\n"
            + "filename = rigid_vb0_crate_frame_v1.buf\n", ini);
        // every section of the replacement names its buffers, so no draw inherits another tier's choice
        Assert.Contains("hash = aaaa0001\nmatch_priority = 0\n$zz_rvb0_crate_frame = 0\n", ini);
        Assert.Contains("hash = aaaa0002\nmatch_priority = 0\n$zz_rvb0_crate_frame = 1\n", ini);
        Assert.Contains("hash = aaaa0003\nmatch_priority = 0\n$zz_rvb0_crate_frame = 0\n", ini);
        // vb3 carries stream 0 as vb0 does, so it follows the same choice
        Assert.Contains("vb0 = Resource_RigidVB0_crate_frame\nif $zz_rvb0_crate_frame == 1\n"
            + "vb0 = Resource_RigidVB0_crate_frame_v1\nendif\n"
            + "vb3 = Resource_RigidVB0_crate_frame\nif $zz_rvb0_crate_frame == 1\n"
            + "vb3 = Resource_RigidVB0_crate_frame_v1\nendif\nib = Resource_RigidIB_crate_frame\n", ini);
    }

    [Fact]
    public void Rigid_tiers_matching_the_replaced_parts_layout_emit_no_selector()
    {
        string ini = BuildLayoutRigid(("aaaa0002", StaticLayout(halfUv: true)));

        Assert.False(File.Exists(Path.Combine(_out, "rigid_vb0_crate_frame_v1.buf")));
        Assert.DoesNotContain("zz_rvb", ini);
        Assert.Contains("vb0 = Resource_RigidVB0_crate_frame\nvb3 = Resource_RigidVB0_crate_frame\n"
            + "ib = Resource_RigidIB_crate_frame\n", ini);
    }

    [Fact]
    public void A_rigid_tier_storing_a_uv_set_the_donor_lacks_gets_it_filled_from_the_first()
    {
        BuildLayoutRigid(("aaaa0002", StaticLayout(halfUv: true, secondUv: true)));

        var primary = File.ReadAllBytes(Path.Combine(_out, "rigid_vb0_crate_frame.buf"));
        var variant = File.ReadAllBytes(Path.Combine(_out, "rigid_vb0_crate_frame_v1.buf"));
        Assert.Equal(3 * 20, variant.Length);
        for (int v = 0; v < 3; v++)
        {
            Assert.Equal(primary.AsSpan(v * 16, 16).ToArray(), variant.AsSpan(v * 20, 16).ToArray());
            Assert.Equal(primary.AsSpan(v * 16 + 12, 4).ToArray(), variant.AsSpan(v * 20 + 16, 4).ToArray());
        }
    }

    [Fact]
    public void A_rigid_tier_storing_a_non_uv_channel_the_donor_lacks_refuses_the_build()
    {
        var tier = StaticLayout(halfUv: true);
        tier[1] = new(0, 16, 0, 3);   // normals the donor does not store
        var e = Assert.Throws<AuthoredRefusalException>(() => BuildLayoutRigid(("aaaa0002", tier)));
        Assert.Contains("frame_aaaa0002", e.Message);
        Assert.Contains("Normal", e.Message);
    }

    [Fact]
    public void A_rigid_tier_reading_a_stream_the_donor_does_not_ship_refuses_the_build()
    {
        // the donor is positions only; nothing would be bound at the tier's stream 1
        var tier = StaticLayout(halfUv: true);
        tier[4] = new(1, 0, 1, 2);
        var e = Assert.Throws<AuthoredRefusalException>(() => BuildLayoutRigid(("aaaa0002", tier)));
        Assert.Contains("frame_aaaa0002", e.Message);
        Assert.Contains("stream 1", e.Message);
    }

    [Fact]
    public void A_rigid_tier_with_no_recorded_layout_is_an_error_not_a_guess()
    {
        string donor = Path.Combine(_root, "unrecorded-donor");
        WriteLayoutDonor(donor);
        var e = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(new PoolBuildRequest
        {
            Pipelines = Array.Empty<ReplacePipeline>(),
            OutDir = _out,
            Rigids = new[]
            {
                new RigidReplace { Suffix = "crate_frame", DonorDir = donor, Hash = "aaaa0001", TierHashes = new[] { "aaaa0002" } },
            },
        }));
        Assert.Contains("aaaa0002", e.Message);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.Contains("if $zz_key_f6 == 0\nhandling = skip\nendif\n"
            + "if $zz_key_f6 == 0\nif $zz_key_f8 == 0\nrun = CommandListRigid_crate_frame\nendif\nendif\n", ini);
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));

        Assert.Contains("if $zz_key_f6 == 0\nif $zz_key_f8 == 0\n"
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

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        // The hide answers the part, so the build never emits the rigid replacement's own section for it
        string ini = File.ReadAllText(Path.Combine(r.OutDir, "mod.ini"));
        Assert.Contains($"hash = {lod0Hash}", ini);
        Assert.DoesNotContain("CommandListRigid", ini);
        ModBuilderTests.AssertNoDuplicateSections(ini);
    }
    [Fact]
    public void The_rigid_routes_repair_record_names_its_own_buffers_and_no_union()
    {
        // A static draw is not posed per vertex, so it ships no skin stream and there is no bone order for
        // one to address. What it does need read back is its channel table: the streams are sliced in the
        // target part's own stored layout, which the new install's asset would have to be re-read to learn.
        var env = MakeEnv(out _, out _, skinWidth: 0);
        var p = NewProject();
        WriteDonorGlb();
        AddReplaceTarget(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, "repair.json")));
        var change = doc.RootElement.GetProperty("changes").EnumerateArray()
            .Single(c => c.GetProperty("verb").GetString() == "replace");
        var geo = change.GetProperty("geometry");

        Assert.Equal("rigid", change.GetProperty("route").GetString());
        var streamFiles = geo.GetProperty("streams").EnumerateArray()
            .ToDictionary(e => e.GetProperty("stream").GetInt32(), e => e.GetProperty("file").GetString()!);
        foreach (var file in streamFiles.Values.Append(geo.GetProperty("index_file").GetString()!))
            Assert.True(File.Exists(Path.Combine(r.OutDir, file)),
                $"repair data names '{file}', which the mod does not ship");
        // stream 0 is named by its NUMBER, not by a route word: this route ships it as rigid_vb0 where the
        // pooled one ships it as combined_bind, and a reader joins on the channel table either way
        Assert.Contains(0, streamFiles.Keys);
        Assert.DoesNotContain(Mesh.SkinLayout.SkinStream, streamFiles.Keys);
        Assert.False(geo.TryGetProperty("union", out _));
        Assert.False(geo.TryGetProperty("pool", out _));
        // every live channel has a buffer to be read out of
        foreach (var c in geo.GetProperty("channels").EnumerateArray())
            if (c.GetProperty("dimension").GetInt32() > 0)
                Assert.Contains(c.GetProperty("stream").GetInt32(), streamFiles.Keys);
        Assert.NotEmpty(geo.GetProperty("channels").EnumerateArray());
    }

}
