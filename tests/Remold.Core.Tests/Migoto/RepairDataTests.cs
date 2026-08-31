using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Remold.Core.Bundles;
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
/// The <c>repair.json</c> a build leaves beside the mod: whether a later read of the folder alone could
/// tell what the modder asked for, what they asked it of, and how to read the shipped buffers back. The
/// bar is what the FOLDER says, so every assertion here reads the written file and joins it against the
/// artifacts beside it — never against the build's own in-memory state.
///
/// <para>The world is local: the coverage-group case needs scene-context siblings that pose a bone the
/// pool cannot, and the shared fixture has no knob for that.</para>
/// </summary>
public class RepairDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-rd-" + Guid.NewGuid().ToString("N"));
    private readonly string _proj;
    private readonly string _out;

    public RepairDataTests()
    {
        _proj = Path.Combine(_root, "proj");
        _out = Path.Combine(_root, "build");
        Directory.CreateDirectory(_proj);
        Directory.CreateDirectory(_out);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // ---- the world ---------------------------------------------------------------------------------

    private static readonly uint[] BodyBones = { 0x00000101, 0x00000102 };
    private static readonly uint[] MateBones = { 0x00000105, 0x00000106 };

    /// <summary>Posed only by the two scene-context siblings, which are never pool candidates — so a donor
    /// riding it can only build through a coverage group, whose palette slots sit past the union.</summary>
    private const uint GroupBone = 0x0000020A;

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

    private static int[] WrappedTris(int verts)
    {
        var tris = new int[verts * 3];
        for (int i = 0; i < tris.Length; i++) tris[i] = i % verts;
        return tris;
    }

    /// <summary>Body (the Replace target, two tiers) + an always-on mate that joins the pool.
    /// <paramref name="contextSiblings"/> adds the <c>_Fight</c>/<c>_Dorm</c> pair posing
    /// <see cref="GroupBone"/>.</summary>
    private BuildEnv MakeEnv(bool contextSiblings = false)
    {
        var bytes = new Dictionary<string, byte[]>();
        void Mesh(string bundleKey, string file, string mesh, int verts, int seed, uint[] bones)
        {
            string path = Path.Combine(_root, file);
            SyntheticBundle.BuildOneSkinnedMesh(path, mesh, Cloud(verts, seed), WrappedTris(verts), bones);
            bytes[bundleKey] = File.ReadAllBytes(path);
        }
        Mesh("bundle0", "s0.bundle", "c_vesna01_body_lod0", 32, 5, BodyBones);
        Mesh("bundle1", "s1.bundle", "c_vesna01_body_lod1", 24, 9, BodyBones);
        Mesh("bundleM", "sm.bundle", "c_vesna01_mate_lod0", 20, 17, MateBones);
        string bt = Path.Combine(_root, "st.bundle");
        SyntheticBundle.BuildOneTexture(bt, "tex_body_d", 8, 8, 200, 100, 50, 255, colorSpace: 1);
        bytes["bundleT"] = File.ReadAllBytes(bt);

        var albedo = new List<SubjectMap> { new("_BaseMap", "tex_body_d", "bundleT") };
        SubjectMaterial Mat(string name, string cab) => new(name, 1, cab, albedo);
        var parts = new List<SubjectPart>
        {
            new("body", "c_vesna01_body_lod0", "addr_body", new[] { Mat("m_body", "cab-body") },
                SiblingTiers: new[] { new RecipeTierSlot("c_vesna01_body_lod1", "addr_body_l1") }),
            new("mate", "c_vesna01_mate_lod0", "addr_mate", new[] { Mat("m_mate", "cab-mate") }),
        };
        var addresses = new Dictionary<string, string>
        {
            ["addr_body"] = "bundle0", ["addr_body_l1"] = "bundle1", ["addr_mate"] = "bundleM",
        };
        if (contextSiblings)
        {
            // One per scene, each with a vertex count of its own so each has a capturable draw. Together
            // they cover GroupBone in every scene the always-on target displays in.
            Mesh("bundleF", "sf.bundle", "c_vesna01_scarf_lod0_Fight", 18, 23, new[] { GroupBone, 0x0000020Bu });
            Mesh("bundleD", "sd.bundle", "c_vesna01_scarf_lod0_Dorm", 16, 29, new[] { GroupBone, 0x0000020Bu });
            parts.Add(new SubjectPart("scarf_Fight", "c_vesna01_scarf_lod0_Fight", "addr_scarf_f",
                new[] { Mat("m_scarf", "cab-scarf-f") }));
            parts.Add(new SubjectPart("scarf_Dorm", "c_vesna01_scarf_lod0_Dorm", "addr_scarf_d",
                new[] { Mat("m_scarf", "cab-scarf-d") }));
            addresses["addr_scarf_f"] = "bundleF";
            addresses["addr_scarf_d"] = "bundleD";
        }

        var model = new SubjectModel("Vesna", "VesnaSSR01", SubjectSource.Prefab, parts,
            Skeleton: null, Problems: Array.Empty<string>());
        return new BuildEnv(
            (c, s) => c == "Vesna" && s == "VesnaSSR01" ? model : null,
            a => addresses.GetValueOrDefault(a),
            id => bytes.GetValueOrDefault(id),
            CatalogVersion: "12345",
            AppVersion: "test-1.0",
            BundleContentHash: id => id == "bundle0" ? "cafebabe" : null).Exact();
    }

    private ModProject NewProject(string name = "Repair")
    {
        var p = new ModProject { RootDir = _proj };
        p.Info.Name = name;
        p.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        return p;
    }

    private ProjectTarget AddReplace(ModProject p, List<SubmeshTextures>? textures = null)
    {
        var t = new ProjectTarget
        {
            AssetType = "Mesh", Bundle = "bundle0", ObjectName = "c_vesna01_body_lod0",
            SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01", ReplaceFile = "donor.glb",
            DonorTextures = textures,
            // the two fields the record sources off the PROJECT rather than off the compile
            OriginalVerts = 32,
            DonorMaterials = new List<string> { "mat_shell", "mat_trim" },
        };
        p.Targets.Add(t);
        return t;
    }

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

    private static JsonElement Repair(string outDir)
    {
        string text = File.ReadAllText(Path.Combine(outDir, "repair.json"));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static JsonElement OneChange(string outDir)
    {
        var changes = Repair(outDir).GetProperty("changes");
        Assert.Equal(1, changes.GetArrayLength());
        return changes[0];
    }

    /// <summary>Every palette slot the shipped skin stream indexes at nonzero weight.</summary>
    private static HashSet<uint> RiddenSlots(string outDir, string suffix)
    {
        var (w, bi) = PoolMath.ParseSkin(
            File.ReadAllBytes(Path.Combine(outDir, $"combined_skin_{suffix}.buf")));
        var slots = new HashSet<uint>();
        for (int v = 0; v < bi.GetLength(0); v++)
            for (int k = 0; k < 4; k++)
                if (w[v, k] > 0) slots.Add((uint)bi[v, k]);
        return slots;
    }

    // ---- the geometry key --------------------------------------------------------------------------

    [Fact]
    public void A_replace_records_the_bone_order_its_shipped_skin_indices_address()
    {
        // The one thing a mod does not otherwise say about its own geometry. The shipped streams ARE the
        // donor; they are readable only against the bone order their blend indices index and the channel
        // table their bytes are laid out by, so both have to survive in the folder.
        var env = MakeEnv();
        var p = NewProject();
        WriteDonorGlb(BodyBones.Append(MateBones[0]).ToArray());
        AddReplace(p, new List<SubmeshTextures> { new() { Submesh = 0 }, new() { Submesh = 1 } });

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        var change = OneChange(r.OutDir);
        var geo = change.GetProperty("geometry");

        Assert.Equal("replace", change.GetProperty("verb").GetString());
        Assert.Equal("pooled", change.GetProperty("route").GetString());
        Assert.Equal("bundle0", change.GetProperty("bundle").GetString());
        Assert.Equal("cafebabe", change.GetProperty("bundle_content").GetString());
        string sfx = change.GetProperty("suffix").GetString()!;

        // every named file is a file the mod actually ships, and every channel's stream has one
        var streamFiles = geo.GetProperty("streams").EnumerateArray()
            .ToDictionary(e => e.GetProperty("stream").GetInt32(), e => e.GetProperty("file").GetString()!);
        foreach (var file in streamFiles.Values.Append(geo.GetProperty("index_file").GetString()!))
            Assert.True(File.Exists(Path.Combine(r.OutDir, file)),
                $"repair data names '{file}', which the mod does not ship");
        foreach (var c in geo.GetProperty("channels").EnumerateArray())
            if (c.GetProperty("dimension").GetInt32() > 0)
                Assert.True(streamFiles.ContainsKey(c.GetProperty("stream").GetInt32()),
                    "a live channel names a stream the record ships no buffer for");

        // the project's own record of the change travels with it
        Assert.Equal(32, change.GetProperty("original_verts").GetInt32());
        // The replacement's submesh LAYOUT, read off its own output slots — this app's export names, which
        // is what a round trip that leaves Blender's slot list alone brings back.
        Assert.Equal(new[] { "gf2_submesh0", "gf2_submesh1" },
            change.GetProperty("donor_materials").EnumerateArray().Select(e => e.GetString()).ToArray());

        // the recorded order IS the one the emission states to the runtime, and the recorded pose count
        // pairs with it one for one
        var union = geo.GetProperty("union");
        var recorded = union.GetProperty("bones").EnumerateArray().Select(e => uint.Parse(e.GetString()!)).ToList();
        using var unionJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(r.OutDir, $"union_{sfx}.json")));
        var emitted = unionJson.RootElement.GetProperty("order").EnumerateArray()
            .Select(e => uint.Parse(e.GetString()!)).ToList();
        Assert.Equal(emitted, recorded);
        Assert.Equal(recorded.Count * 16 * sizeof(float),
            Convert.FromBase64String(union.GetProperty("bind_poses").GetString()!).Length);
        Assert.Equal("anchor", union.GetProperty("space").GetString());
        Assert.Equal("c_vesna01_body_lod0", geo.GetProperty("anchor").GetString());
        Assert.Contains("c_vesna01_mate_lod0",
            geo.GetProperty("pool").EnumerateArray().Select(e => e.GetString()));

        // and every slot the shipped skin rides is one the record can name
        foreach (uint slot in RiddenSlots(r.OutDir, sfx))
            Assert.True(slot < recorded.Count, $"slot {slot} is past the recorded bone order");

        // the shape the streams are sliced in: enough channels to cover all three streams, and the
        // submesh table the ib is split by
        var streams = geo.GetProperty("channels").EnumerateArray()
            .Where(c => c.GetProperty("dimension").GetInt32() > 0)
            .Select(c => c.GetProperty("stream").GetInt32()).Distinct().Order().ToList();
        Assert.Equal(new[] { 0, 1, 2 }, streams);
        Assert.Equal(2, geo.GetProperty("submeshes").GetArrayLength());
        Assert.Equal(6, geo.GetProperty("verts").GetInt32());
        Assert.Equal("R16_UINT", geo.GetProperty("index_format").GetString());
    }

    [Fact]
    public void A_coverage_groups_bones_are_recorded_at_the_palette_slots_the_shipped_skin_uses()
    {
        // A group bone's blend index is NOT a union index: the emission moves it onto a slot it reserved
        // past the union and past the witness slots, and only it knows where that region sits. Read back
        // against the union alone, those vertices would name the wrong bone — so the record has to carry
        // the slot, and this joins it to the bytes that use it.
        var env = MakeEnv(contextSiblings: true);
        var p = NewProject("Group");
        WriteDonorGlb(BodyBones.Append(GroupBone).ToArray());
        AddReplace(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        var geo = OneChange(r.OutDir).GetProperty("geometry");
        string sfx = OneChange(r.OutDir).GetProperty("suffix").GetString()!;

        var union = geo.GetProperty("union").GetProperty("bones").EnumerateArray()
            .Select(e => uint.Parse(e.GetString()!)).ToList();
        Assert.DoesNotContain(GroupBone, union);   // the premise: no pool part tables it

        var groupSlots = geo.GetProperty("group_slots").EnumerateArray()
            .Select(e => (Slot: e.GetProperty("slot").GetUInt32(), Bone: uint.Parse(e.GetProperty("bone").GetString()!)))
            .ToList();
        var forBone = Assert.Single(groupSlots, g => g.Bone == GroupBone);
        Assert.True(forBone.Slot >= union.Count, "a group slot sits past the union");

        // the join that matters: the donor's group-weighted vertices ride exactly that slot, and every
        // other slot they ride is a union one
        var ridden = RiddenSlots(r.OutDir, sfx);
        Assert.Contains(forBone.Slot, ridden);
        foreach (uint slot in ridden)
            Assert.True(slot < union.Count || groupSlots.Any(g => g.Slot == slot),
                $"slot {slot} is named by neither the union nor the group slots");
    }

    [Fact]
    public void A_replace_with_no_coverage_group_records_no_group_slots()
    {
        var env = MakeEnv();
        var p = NewProject();
        WriteDonorGlb(BodyBones);
        AddReplace(p);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.False(OneChange(r.OutDir).GetProperty("geometry").TryGetProperty("group_slots", out _));
    }

    // ---- the intent the ini cannot be read back into -----------------------------------------------

    [Fact]
    public void Each_authored_slot_names_the_shipped_map_it_became()
    {
        // The encode collapses equal images onto ONE file, named for whichever submesh claimed it first,
        // so the mapping from a donor submesh's slot to the .dds beside it is not derivable from names.
        var env = MakeEnv();
        var p = NewProject("Maps");
        WriteDonorGlb(BodyBones);
        foreach (var f in new[] { "s0.png", "s1.png" })
            using (var img = new Image<Rgba32>(8, 8, new Rgba32(200, 100, 50, 255)))
                img.SaveAsPng(Path.Combine(_proj, f));
        AddReplace(p, new List<SubmeshTextures>
        {
            new() { Submesh = 0, Albedo = "s0.png" },
            new() { Submesh = 1, Albedo = "s1.png" },
        });

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        var rows = OneChange(r.OutDir).GetProperty("textures").EnumerateArray().ToList();

        // one shipped map for two identical images, and BOTH rows name it
        string only = Path.GetFileName(Assert.Single(Directory.GetFiles(r.OutDir, "donor_*.dds")));
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            Assert.Equal("Authored", row.GetProperty("albedo").GetProperty("origin").GetString());
            Assert.Equal(only, row.GetProperty("albedo").GetProperty("file").GetString());
        }
    }

    [Fact]
    public void A_blanked_slot_is_told_apart_from_one_left_alone_and_from_one_never_asked_about()
    {
        // The emitted binds cannot separate these three, and the difference is the whole per-slot
        // contract: blank this, keep the part's own map, and say nothing about this slot at all.
        var env = MakeEnv();
        var p = NewProject("Origins");
        WriteDonorGlb(BodyBones);
        AddReplace(p, new List<SubmeshTextures>
        {
            new()
            {
                Submesh = 0,
                NormalOrigin = SlotOrigin.ExplicitNeutral,
                RmoOrigin = SlotOrigin.VanillaOwn,
            },
        });

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        var row = Assert.Single(OneChange(r.OutDir).GetProperty("textures").EnumerateArray());

        Assert.Equal(0, row.GetProperty("submesh").GetInt32());
        Assert.Equal("ExplicitNeutral", row.GetProperty("normal").GetProperty("origin").GetString());
        Assert.False(row.GetProperty("normal").TryGetProperty("file", out _));
        Assert.Equal("VanillaOwn", row.GetProperty("rmo").GetProperty("origin").GetString());
        // A slot with no ask of its own inherits, which is the same thing "keep the part's own map" says,
        // and the model records the two as one answer.
        Assert.Equal("VanillaOwn", row.GetProperty("albedo").GetProperty("origin").GetString());
        Assert.False(row.GetProperty("albedo").TryGetProperty("file", out _));
    }

    [Fact]
    public void A_toggle_key_travels_with_its_off_and_start_states()
    {
        var env = MakeEnv();
        var p = NewProject("Keys");
        p.Info.ToggleKey = "F6";
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        p.SetChangeKey("Vesna", "VesnaSSR01", "c_vesna01_body_lod0", EditVerbs.Replace, "F7",
            hideWhenOff: true, startsOff: true);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        var key = OneChange(r.OutDir).GetProperty("toggle_key");

        Assert.Equal("F6", Repair(r.OutDir).GetProperty("toggle_key").GetString());
        Assert.Equal("F7", key.GetProperty("key").GetString());
        Assert.True(key.GetProperty("hide_when_off").GetBoolean());
        Assert.True(key.GetProperty("starts_off").GetBoolean());
    }

    [Fact]
    public void A_retexture_records_the_game_texture_it_overrides()
    {
        // The shipped .dds says nothing about which asset it stands in for, and the emitted section names
        // only a resource hash — which is re-derived from the new install's bytes, so it cannot select.
        var env = MakeEnv();
        var p = NewProject("Retex");
        FlatDds.Write(Path.Combine(_proj, "skin.dds"), (1, 2, 3, 255));
        p.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "bundleT", ObjectName = "tex_body_d",
            ReplaceFile = "skin.dds", SubjectCharacter = "Vesna", SubjectOutfit = "VesnaSSR01",
            Users = new List<string> { "c_vesna01_body_lod0", "c_vesna01_mate_lod0" },
        });

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        var changes = Repair(r.OutDir).GetProperty("changes").EnumerateArray().ToList();
        var body = changes.Single(c => c.GetProperty("mesh").GetString() == "c_vesna01_body_lod0");
        var albedo = body.GetProperty("textures")[0].GetProperty("albedo");

        Assert.Equal("retexture", body.GetProperty("verb").GetString());
        Assert.True(File.Exists(Path.Combine(r.OutDir, albedo.GetProperty("file").GetString()!)));
        var stock = albedo.GetProperty("stock");
        Assert.Equal("bundleT", stock.GetProperty("bundle").GetString());
        Assert.Equal("tex_body_d", stock.GetProperty("name").GetString());
        Assert.Equal(new[] { "c_vesna01_body_lod0", "c_vesna01_mate_lod0" },
            stock.GetProperty("users").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void A_hide_is_recorded_under_the_mesh_it_suppresses()
    {
        var env = MakeEnv();
        var p = NewProject("Hide");
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_mate_lod0", true);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);
        var change = OneChange(r.OutDir);

        Assert.Equal("hide", change.GetProperty("verb").GetString());
        Assert.Equal("c_vesna01_mate_lod0", change.GetProperty("mesh").GetString());
        Assert.Equal("bundleM", change.GetProperty("bundle").GetString());
        // a hide ships no geometry and no maps, so it claims neither
        Assert.False(change.TryGetProperty("geometry", out _));
        Assert.False(change.TryGetProperty("textures", out _));
    }

    [Fact]
    public void A_build_excluded_change_is_not_in_the_record()
    {
        // The record describes what the folder HOLDS. An unticked change ships nothing, so nothing about
        // it could be reconstructed and naming it would promise otherwise.
        var env = MakeEnv();
        var p = NewProject("Excluded");
        WriteDonorGlb(BodyBones);
        AddReplace(p);
        p.SetHidden("Vesna", "VesnaSSR01", "c_vesna01_mate_lod0", true);
        p.SetBuildExcluded("Vesna", "VesnaSSR01", "c_vesna01_mate_lod0", EditVerbs.Hide, true);

        var r = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.Equal("replace", OneChange(r.OutDir).GetProperty("verb").GetString());
    }

    // ---- the writer's own guards -------------------------------------------------------------------

    [Fact]
    public void Bone_hashes_ride_as_text_so_the_top_of_the_range_survives_a_json_reader()
    {
        Assert.Equal("4294967295", RepairData.Bone(uint.MaxValue));
    }

    /// <summary>A key-group record whose counts disagree with its own list of positions describes a group
    /// nothing could have produced. Read on trust it would show a position that is not there, or file this
    /// change's content under the wrong one, so it is refused by name at the reading boundary.</summary>
    [Theory]
    [InlineData(3, 0, 0, "claims 3 positions and lists 2")]
    [InlineData(2, 5, 0, "carries position 5 of 2")]
    [InlineData(2, 0, 9, "launches at position 9 of 2")]
    public void A_key_group_whose_numbers_disagree_with_its_positions_is_refused(
        int stateCount, int stateIndex, int startState, string expected)
    {
        string dir = Path.Combine(_root, $"kg-{stateCount}-{stateIndex}-{startState}");
        Directory.CreateDirectory(dir);
        RepairData.Write(dir, new RepairData.Payload(
            RepairData.Schema, null, null, null,
            new[] { new RepairData.SubjectRef("Vesna", "VesnaSSR01") },
            new[]
            {
                new RepairData.ChangeRecord("replace", "Vesna", "VesnaSSR01", "c_vesna01_body_lod0",
                    null, KeyGroups: new[] { new RepairData.KeyGroupRecord("key-0001", "F7", stateCount,
                        startState, stateIndex, new[]
                        {
                            new RepairData.KeyGroupStateRecord(0, "edit", "edit-body"),
                            new RepairData.KeyGroupStateRecord(1, "vanilla"),
                        }) },
                    Intent: new RepairData.IntentRecord("edit", "edit-body",
                        Array.Empty<RepairData.IntentBindingRecord>())),
            }));

        var ex = Assert.Throws<InvalidDataException>(() => RepairData.Read(dir));

        Assert.Contains("'c_vesna01_body_lod0'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bind_pose_that_is_not_sixteen_floats_is_refused_rather_than_padded()
    {
        // A short pose would shift every bone after it, and the shifted table decodes cleanly — nothing
        // downstream could tell it apart from a correct one.
        var ex = Assert.Throws<InvalidDataException>(() =>
            RepairData.BindPoses(new[] { new float[16], new float[15] }));
        Assert.Contains("15 floats", ex.Message);
    }
    /// <summary>The production source of a change's <c>bundle_content</c>. Every build in this file feeds
    /// the record a stub lambda, so this is the one place the real two-hop lookup — logical bundle id →
    /// catalog internalId → the manifest stub's own content hash — is exercised.</summary>
    [Fact]
    public void The_bundle_content_lookup_resolves_a_logical_id_through_the_catalog_to_the_manifests_hash()
    {
        string phys = new string('a', 32), other = new string('b', 32);
        string manifestPath = Path.Combine(_root, "gff", GffManifest.ManifestHash + ".bundle");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        FakeGff.Write(manifestPath,
            (phys + ".bundle", FakeGff.Stub(phys, 0, 0, 1)),
            (other + ".bundle", FakeGff.Stub(other, 0, 0, 2)));
        var manifest = GffManifest.Read(manifestPath);
        var catalog = CatalogIndex.ForTest(
            Array.Empty<(string Address, string OwnerBundle)>(), null,
            new[] { ("mesh.bundle", phys + ".bundle"), ("mat.bundle", other + ".bundle") });

        var lookup = BundleReads.BundleContentHashLookup(catalog, manifest);

        // it answers the content identity the manifest states for that bundle, and tells two bundles apart
        Assert.Equal(BundleReads.ContentHashLookup(manifest)(phys + ".bundle"), lookup("mesh.bundle"));
        Assert.NotEqual(lookup("mesh.bundle"), lookup("mat.bundle"));
        // bundle ids come out of the catalog, which compares them case-insensitively — so does this
        Assert.Equal(lookup("mesh.bundle"), lookup("MESH.BUNDLE"));
        // a bundle neither lookup names answers nothing rather than a substitute
        Assert.Null(lookup("absent.bundle"));
    }

    // ---- the author's opt-out ----------------------------------------------------------------------

    [Fact]
    public void A_mod_built_without_repair_data_ships_none_and_is_otherwise_unchanged()
    {
        // The modder who does not want their work read back. Everything else about the folder has to be
        // identical: the opt-out withholds a record, it does not change what the mod does.
        var env = MakeEnv();
        var p = NewProject("OptOut");
        WriteDonorGlb(BodyBones);
        AddReplace(p);

        var with = ReleasedBuild.Build(p, env, _out, zip: false);
        var kept = Directory.GetFiles(with.OutDir)
            .ToDictionary(f => Path.GetFileName(f)!, File.ReadAllBytes);

        p.Info.IncludeRepairData = false;
        var without = ReleasedBuild.Build(p, env, _out, zip: false);

        Assert.False(File.Exists(Path.Combine(without.OutDir, "repair.json")));
        Assert.Equal(kept.Keys.Where(n => n != "repair.json").Order().ToArray(),
            Directory.GetFiles(without.OutDir).Select(Path.GetFileName).Order().ToArray());
        foreach (var (name, bytes) in kept.Where(k => k.Key != "repair.json"))
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(without.OutDir, name)));
        // the build log says the folder cannot be read back; no user-facing warning, since nothing is wrong
        Assert.Contains(without.Diagnostics, d => d.Contains("built without it"));
        Assert.DoesNotContain(without.Warnings, w => w.Contains("repair"));
    }

    [Fact]
    public void A_project_saved_before_the_option_existed_ships_repair_data()
    {
        // The manifest key is absent from every project written before the option, and absent must read as
        // ON: the alternative silently stops shipping the record for every existing project.
        string dir = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ModProject.FileName), """
            { "schema": 1, "info": { "name": "Legacy", "version": "1.0" }, "selection": [], "targets": [] }
            """);

        var loaded = ModProject.Load(dir);

        Assert.True(loaded.Info.IncludeRepairData);
    }

    [Fact]
    public void Repair_reader_accepts_schema_1_and_requires_intent_on_schema_2_changes()
    {
        string legacy = Path.Combine(_root, "legacy-repair.json");
        File.WriteAllText(legacy,
            "{\"schema\":1,\"subjects\":[],\"changes\":[]}");
        Assert.Equal(RepairData.LegacySchema, RepairData.Read(legacy).SchemaVersion);

        string invalid = Path.Combine(_root, "invalid-repair.json");
        File.WriteAllText(invalid,
            "{\"schema\":2,\"subjects\":[],\"changes\":[{\"verb\":\"hide\","
            + "\"character\":\"Vesna\",\"outfit\":\"VesnaSSR01\","
            + "\"mesh\":\"body\",\"bundle\":null}]}");
        Assert.Throws<InvalidDataException>(() => RepairData.Read(invalid));
    }
}
