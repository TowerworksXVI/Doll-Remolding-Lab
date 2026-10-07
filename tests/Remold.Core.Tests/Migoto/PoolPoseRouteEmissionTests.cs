using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The pooled pose route: a Replace that takes rows from other parts draws each copy of the replaced part
/// from that copy's own pose. Every draw of a part it takes rows from writes that copy's slot of a ring for
/// that mesh (the vertices its rows are solved from and the draw's object-to-world rows, stamped with the
/// frame) and marks the mesh as drawn this frame; at each draw of the replacement each source mesh that has
/// drawn this frame runs its palette pass, which chooses the slot standing where this copy places that part
/// and rebases that slot's rows into the replaced part's space. A source a bone's row places has its slot
/// chosen by a pick pass first. A pool that cannot place every part it takes rows from keeps the
/// once-per-frame chain and says why.
/// </summary>
public class PoolPoseRouteEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-poolpose-" + Guid.NewGuid().ToString("N"));

    public PoolPoseRouteEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // One skeleton for every fixture:  T ─ A ─ B ─ W ─ Q       T ─ R ─ S ─ U ─ V
    //                                       └─ P ─ C ─ D
    //                                           └─ X
    private const uint T = 200, A = 101, B = 102, P = 106, C = 103, D = 104, R = 111, S = 112, U = 113, V = 114,
        X = 109, G = 107, W = 115, Q = 116;

    private static readonly IReadOnlyDictionary<uint, string> Paths = new Dictionary<uint, string>
    {
        [T] = "T", [A] = "T/A", [B] = "T/A/B", [P] = "T/A/P", [C] = "T/A/P/C", [D] = "T/A/P/C/D",
        [R] = "T/R", [S] = "T/R/S", [U] = "T/R/S/U", [V] = "T/R/S/U/V", [X] = "T/A/P/X",
        [W] = "T/A/B/W", [Q] = "T/A/B/W/Q",
    };

    private static void GenericPositions(string dir, int verts, int seed)
    {
        var s0 = File.ReadAllBytes(Path.Combine(dir, "stream0.buf"));
        for (int v = 0; v < verts; v++)
        {
            BitConverter.GetBytes(((v + seed) * 13 % 17) / 4f).CopyTo(s0, v * 40);
            BitConverter.GetBytes(((v + seed) * 7 % 23) / 5f).CopyTo(s0, v * 40 + 4);
            BitConverter.GetBytes(((v + seed) * 11 % 29) / 6f).CopyTo(s0, v * 40 + 8);
        }
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);
    }

    private string Dump(string name, int seed, params uint[] bones)
    {
        string dir = Path.Combine(_root, name);
        SyntheticPool.WritePartDump(dir, seed, 32, bones);
        GenericPositions(dir, 32, seed);
        return dir;
    }

    private string Donor(string name, int unionBones, params int[] weighted)
    {
        string dir = Path.Combine(_root, name);
        SyntheticPool.WriteDonor(dir, verts: 8, unionBones: unionBones, submeshes: 2);
        if (weighted.Length > 0) SyntheticPool.WeightDonorOn(dir, weighted);
        return dir;
    }

    private static Dictionary<string, string> Hashes(params PoolPart[] parts) =>
        parts.Select((p, i) => (p.Name, Hash: $"{(char)('a' + i)}{(char)('a' + i)}{(char)('a' + i)}{(char)('a' + i)}0001"))
            .ToDictionary(x => x.Name, x => x.Hash);

    private PoolBuildRequest Pool(string outName, string donor, PoolPart[] parts, PoolTier[]? tiers = null,
        DrawShapeSet? anchorShapes = null, KeyRef? toggleKey = null) => new()
    {
        OutDir = Path.Combine(_root, outName),
        Pipelines = new[]
        {
            new ReplacePipeline
            {
                Suffix = "swap",
                Parts = parts,
                Anchor = parts[0].Name,
                DonorDir = donor,
                CaptureHashes = Hashes(parts),
                Tiers = tiers,
                BonePaths = Paths,
                AnchorShapes = anchorShapes,
                ToggleKey = toggleKey,
            },
        },
    };

    /// <summary>alpha (A, B) replaced, with beta (A, C) sharing its root A.</summary>
    private PoolBuildRequest SameRootPool(string outName = "out", DrawShapeSet? anchorShapes = null,
        KeyRef? toggleKey = null) =>
        Pool(outName, Donor("donor", 3), new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B)).Rooted(A, T),
            new PoolPart("beta", Dump("beta", 2, A, C)).Rooted(A, T),
        }, anchorShapes: anchorShapes, toggleKey: toggleKey);

    /// <summary>alpha (A, B, P) replaced; beta (C, D) is rooted at C under P, which the donor does not weight.
    /// Union order A, B, P, C, D.</summary>
    private PoolBuildRequest ParentPool(string outName = "out", PoolTier[]? alphaTiers = null, params PoolTier[] betaTiers)
    {
        string bd = Dump("beta", 2, C, D);
        SyntheticPool.SetRestOrigin(bd, C, new Vector3(0.25f, 1.5f, -0.75f));
        var tiers = (alphaTiers ?? Array.Empty<PoolTier>()).Concat(betaTiers).ToArray();
        return Pool(outName, Donor("donor", 5, 0, 1, 3, 4), new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B, P)).Rooted(A, T),
            new PoolPart("beta", bd).Rooted(C, P, A, T),
        }, tiers.Length > 0 ? tiers : null);
    }

    private static string Section(string ini, string header)
    {
        int at = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{header} missing");
        int end = ini.IndexOf("\n\n", at, StringComparison.Ordinal);
        return end < 0 ? ini[at..] : ini[at..(end + 1)];
    }

    /// <summary>The compact palette's bones in slot order, as the build recorded them.</summary>
    private static List<uint> UnionOrder(string outDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "union_swap.json")));
        return doc.RootElement.GetProperty("order").EnumerateArray().Select(e => uint.Parse(e.GetString()!)).ToList();
    }

    private static string Ini(string outDir) => File.ReadAllText(Path.Combine(outDir, "mod.ini"));

    private static void AssertLoads(string ini, string outDir)
    {
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);
        HlslCheck.EveryShaderCompilesClean(ini, outDir);
    }

    /// <summary>The lines a draw of <paramref name="mesh"/> writes its ring with: its ring block, then the
    /// flag saying it has drawn this frame.</summary>
    private static string RingRuns(string mesh) =>
        $"run = CustomShaderRingBlock_{mesh}\n$zz_drew_{mesh} = 1\n";

    /// <summary>The run lines of <paramref name="mesh"/>'s ring block: the number of the slot its counter
    /// names bound for the gather and the stamp, the gather, the stamp, then the counter advanced and wrapped
    /// at sixteen slots.</summary>
    private static string RingBlockRuns(string mesh) =>
        string.Concat(Enumerable.Range(0, 16).Select(k =>
            $"{(k == 0 ? "if" : "else if")} $zz_rslot_{mesh} == {k}\nvs-t2 = Resource_RingSlot_{k}\nps-t2 = Resource_RingSlot_{k}\n"))
        + $"endif\nrun = CustomShaderRingGather_{mesh}\nrun = CustomShaderRingStamp_{mesh}\n"
        + $"$zz_rslot_{mesh} = $zz_rslot_{mesh} + 1\nif $zz_rslot_{mesh} >= 16\n$zz_rslot_{mesh} = 0\nendif\n";

    /// <summary>The ring block section of <paramref name="mesh"/>, asserted to set the shared state with
    /// the capture vertex shader, save the slots its passes bind (the frame and slot number in the pixel
    /// stage, the index buffer, the lookup and slot number in the vertex stage), clear the other targets,
    /// and put the binds back; its run lines returned.</summary>
    private static string RingBlock(string ini, string mesh)
    {
        string name = $"CustomShaderRingBlock_{mesh}";
        string section = Section(ini, $"[{name}]");
        string head = $"[{name}]\nvs = pose_capture_vs.hlsl\nhs = null\nds = null\ngs = null\n"
            + "topology = triangle_list\ncull = none\ndepth_enable = false\nblend = disable\n"
            + "Resource_SavePST0 = ref ps-t0\nResource_SavePST2 = ref ps-t2\n"
            + "Resource_SaveIB = ref ib\nResource_SaveVST1 = ref vs-t1\nResource_SaveVST2 = ref vs-t2\n"
            + "od = null\no1 = null\no2 = null\no3 = null\no4 = null\no5 = null\no6 = null\no7 = null\n";
        const string tail = "ps-t0 = Resource_SavePST0\nps-t2 = Resource_SavePST2\n"
            + "ib = Resource_SaveIB\nvs-t1 = Resource_SaveVST1\nvs-t2 = Resource_SaveVST2\n";
        Assert.StartsWith(head, section);
        Assert.EndsWith(tail, section.TrimEnd('\n') + "\n");
        return section[head.Length..section.LastIndexOf(tail, StringComparison.Ordinal)];
    }

    /// <summary>A source mesh's lines at a draw of the replacement where its palette pass picks its copy
    /// itself: that one pass, reading the mesh's ring, run only once the mesh has drawn this frame.</summary>
    private static string Self(string mesh, string sfx = "swap") =>
        $"if $zz_drew_{mesh} == 1\nrun = CustomShaderPosePalette_{mesh}_{sfx}\nendif\n";

    /// <summary>A source mesh's lines at a draw of the replacement where a bone's row places it: its pick
    /// pass, then its palette pass, each reading the mesh's ring, run only once the mesh has drawn this
    /// frame.</summary>
    private static string Picked(string mesh, string sfx = "swap") =>
        $"if $zz_drew_{mesh} == 1\nrun = CustomShaderPosePick_{mesh}_{sfx}\nrun = CustomShaderPosePalette_{mesh}_{sfx}\nendif\n";

    /// <summary>The run lines of the anchor's pose block at the draw of <paramref name="section"/>
    /// (<see cref="PoseRouteEmissionTests.PoseBlock"/>).</summary>
    private static string PoseBlock(string ini, string section = "alpha", string sfx = "swap", IReadOnlyList<int>? slots = null) =>
        PoseRouteEmissionTests.PoseBlock(ini, $"{section}_{sfx}", slots: slots);

    /// <summary>The passes one draw of the replacement runs ahead of its skins: the anchor's gather, its rows'
    /// capture and its palette pass, then each source mesh's lines in order (<see cref="Self"/>,
    /// <see cref="Picked"/>).</summary>
    private static string Head(string anchor, params string[] sources) =>
        $"run = CustomShaderGather_{anchor}\nrun = CustomShaderPoseAnchorMat_swap\nrun = CustomShaderPosePalette_{anchor}_swap\n"
        + string.Concat(sources);

    private const string Skins = "run = CustomShaderPoseSkin_swap_p0\nrun = CustomShaderPoseSkin_swap_p1\n";

    /// <summary>Every line of <paramref name="ini"/> equal to <paramref name="line"/>, each with the <c>if</c>
    /// conditions open around it.</summary>
    private static List<string[]> GatesAround(string ini, string line)
    {
        var open = new List<string>();
        var found = new List<string[]>();
        foreach (var raw in ini.Split('\n'))
        {
            string l = raw.Trim();
            if (l.StartsWith('[')) open.Clear();
            else if (l.StartsWith("if ", StringComparison.Ordinal)) open.Add(l);
            else if (l == "endif" && open.Count > 0) open.RemoveAt(open.Count - 1);
            else if (l == line) found.Add(open.ToArray());
        }
        return found;
    }

    [Fact]
    public void A_source_draw_gathers_its_packet_and_stamps_its_rows_into_the_next_ring_slot_reading_nothing_rendered_this_frame()
    {
        var req = SameRootPool();
        new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        // every draw of the source runs its ring block and sets its flag, in that order, and keeps no
        // reference, latch or constants copy
        string beta = Section(ini, "[TextureOverride_Cap_beta]");
        Assert.Contains(RingRuns("beta"), beta);
        Assert.DoesNotContain("Resource_beta_Posed", ini);
        Assert.DoesNotContain("zz_seen_", ini);
        Assert.DoesNotContain("copy vs-cb1", ini);

        // the block: the slot the counter names bound, the gather, the stamp, the counter advanced
        Assert.Equal(RingBlockRuns("beta"), RingBlock(ini, "beta"));
        // the counter names the next slot and nothing more: it is never reset, and no ini variable carries the
        // frame number; no ini parameter is read or set
        Assert.Contains("global $zz_rslot_beta = 0\n", Section(ini, "[Constants]"));
        Assert.DoesNotContain("$zz_rslot_beta = 0\n", Section(ini, "\n[Present]\n"));
        Assert.Single(Regex.Matches(ini, Regex.Escape("$zz_rslot_beta = $zz_rslot_beta + 1\n")));
        Assert.DoesNotContain("$zz_frame", ini);
        Assert.DoesNotContain("IniParams", string.Concat(Directory.GetFiles(outDir, "*.hlsl").Select(File.ReadAllText)));
        Assert.DoesNotMatch(@"(?m)^x[0-9]* = ", ini);
        Assert.DoesNotMatch(@"(?m)= x[0-9]*$", ini);

        // one ring texture of sixteen slots, each slot its packet's rows and one row of object-to-world rows
        // and frame number; the slots' numbers in sixteen one-element buffers, each loaded from its own file
        int h = ComputeTemplates.PacketHeight(32);
        Assert.Contains($"[Resource_Ring_beta]\ntype = Texture2D\nformat = R32G32B32A32_FLOAT\nwidth = 64\nheight = {16 * (h + 1)}\n", ini);
        for (int k = 0; k < 16; k++)
        {
            Assert.Contains($"[Resource_RingSlot_{k}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = ring_slot_{k}.buf\n", ini);
            Assert.Equal(BitConverter.GetBytes((uint)k), File.ReadAllBytes(Path.Combine(outDir, $"ring_slot_{k}.buf")));
        }
        Assert.DoesNotContain("[Resource_RingSlot_16]", ini);
        foreach (string name in new[] { "[Resource_Ring_beta]", "[CustomShaderRingGather_beta]", "[CustomShaderRingStamp_beta]",
                     "[CustomShaderRingBlock_beta]", "[Resource_PacketIndex_beta]", "[Resource_PacketLookup_beta]",
                     "[Resource_RingSlot_0]", "[Resource_SaveVST2]" })
            Assert.Single(Regex.Matches(ini, Regex.Escape(name)));
        // a mesh that is only a source has no packet texture or plain gather of its own
        foreach (string gone in new[] { "Resource_Ring_beta_a", "Resource_Ring_beta_b", "RingWrite", "ring_write",
                     "[CustomShaderGather_beta]", "[Resource_Packet_beta]", "zz_ring_" })
            Assert.DoesNotContain(gone, ini);
        Assert.False(File.Exists(Path.Combine(outDir, "gather_beta.hlsl")));

        // the gather reads the draw's vertex buffer through the mesh's index and lookup and places each entry
        // in the slot whose number the block bound at vs-t2; it names only what differs from the block
        Assert.Equal("[CustomShaderRingGather_beta]\nvs = ring_gather_beta.hlsl\nps = gather_ps.hlsl\ntopology = point_list\n"
            + "o0 = set_viewport Resource_Ring_beta\nib = Resource_PacketIndex_beta\nvs-t1 = Resource_PacketLookup_beta\n"
            + "drawindexed = 32, 0, 0\n", Section(ini, "[CustomShaderRingGather_beta]"));
        string gather = File.ReadAllText(Path.Combine(outDir, "ring_gather_beta.hlsl"));
        Assert.Equal(ComputeTemplates.EmitRingGather(32, 16), gather);
        Assert.Contains($"uint x = i % 64, y = slot[0] * {h + 1} + i / 64;", gather);
        Assert.Contains($"1 - (y + 0.5) / {16 * (h + 1)}.0 * 2", gather);
        // the stamp: the block's capture vertex shader carries the draw's own rows, the pixel shader reads the
        // frame number and the slot number, into the same ring
        Assert.Equal("[CustomShaderRingStamp_beta]\nps = ring_stamp_beta.hlsl\nps-t0 = Resource_PoseFrame\n"
            + "o0 = set_viewport Resource_Ring_beta\ndraw = 3, 0\n", Section(ini, "[CustomShaderRingStamp_beta]"));
        string stamp = File.ReadAllText(Path.Combine(outDir, "ring_stamp_beta.hlsl"));
        Assert.Equal(ComputeTemplates.EmitRingStamp(32), stamp);
        Assert.Contains("if (y != slot[0] * S + H || x > 4) discard;", stamp);
        // nothing either pass reads was rendered in this frame: the vertex stage reads the draw's buffer and the
        // mod's files, the pixel stage the frame number (written in [Present]) and the slot number; the block
        // binds nothing else, and no ring pass reads the ring
        var binds = RingBlock(ini, "beta").Split('\n').Where(l => l.StartsWith("vs-t", StringComparison.Ordinal)
            || l.StartsWith("ps-t", StringComparison.Ordinal)).Distinct().ToList();
        Assert.All(binds, l => Assert.Matches(@"^(vs|ps)-t2 = Resource_RingSlot_\d+$", l));
        Assert.DoesNotContain("Resource_Ring_beta\n", Section(ini, "[CustomShaderRingGather_beta]").Replace("set_viewport Resource_Ring_beta\n", ""));
        Assert.DoesNotContain("ps-t", Section(ini, "[CustomShaderRingStamp_beta]").Replace("ps-t0 = Resource_PoseFrame\n", ""));
        Assert.DoesNotContain("Take", ini);
        Assert.Empty(Directory.GetFiles(outDir, "pose_take_*"));
    }

    [Fact]
    public void A_same_root_source_runs_one_palette_pass_at_the_replacements_draw_which_picks_its_copy_and_reads_that_copys_packet()
    {
        var req = SameRootPool();
        var result = new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        // the anchor's draw: its pose block, then the draw; the block: gather, rows, palette, the source's
        // palette pass once it has drawn, the skins
        Assert.Contains("run = CustomShaderPoseBlock_alpha_swap\nrun = CommandListDraw_swap\n", Section(ini, "[TextureOverride_Cap_alpha]"));
        Assert.Equal(Head("alpha", Self("beta")) + Skins, PoseBlock(ini));
        Assert.Equal("[CustomShaderPoseAnchorMat_swap]\nvs = pose_capture_vs.hlsl\nps = pose_capture_ps.hlsl\nps-t0 = Resource_PoseFrame\n"
            + "o0 = set_viewport Resource_AnchorMat_swap\ndraw = 3, 0\n", Section(ini, "[CustomShaderPoseAnchorMat_swap]"));

        // no pick pass, pick texture or take pass: one palette pass reads the ring (its packets and rows both)
        // and the anchor's rows
        Assert.DoesNotContain("PosePick", ini);
        Assert.DoesNotContain("Resource_Pick_", ini);
        Assert.False(File.Exists(Path.Combine(outDir, "pose_pick_beta_swap.hlsl")));
        string palette = Section(ini, "[CustomShaderPosePalette_beta_swap]");
        Assert.Contains("ps = pose_palette_beta_swap.hlsl\nps-t0 = Resource_Ring_beta\n", palette);
        Assert.Contains("ps-t4 = Resource_beta_Off\nps-t6 = Resource_AnchorMat_swap\n", palette);
        Assert.DoesNotContain("ps-t5", palette);
        for (int k = 0; k <= 6; k++) Assert.Contains($"\n[Resource_SavePST{k}]\n", ini);
        Assert.DoesNotContain("[Resource_SavePST7]", ini);

        // the palette shader runs the same-root rule itself, discards when it finds no copy, and solves from
        // the chosen slot's packet region, reading the slots' rows out of the same texture
        int h = ComputeTemplates.PacketHeight(32);
        string hlsl = File.ReadAllText(Path.Combine(outDir, "pose_palette_beta_swap.hlsl"));
        Assert.Contains("Live(s,stamp) && all(abs(Ring(s,0)-A0)<=SAME)", hlsl);
        Assert.Contains("int chosen = PickSlot(A0, A1, A2, A3, anc.Load(int3(4, 0, 0)).x);\n    if (chosen < 0) discard;\n", hlsl);
        Assert.Contains($"static const uint H={h};", hlsl);
        Assert.Contains("return q.Load(int3(i % 64, Slot * S + i / 64, 0)).xyz;", hlsl);
        Assert.Contains($"float4 Ring(uint k, uint r){{ return q.Load(int3(r, k * {h + 1} + {h}, 0)); }}", hlsl);
        Assert.DoesNotContain("register(t5)", hlsl);
        Assert.Contains("float4x4 W = float4x4(Ring(Slot, 0), Ring(Slot, 1), Ring(Slot, 2), Ring(Slot, 3));", hlsl);
        Assert.DoesNotContain("mask.Load", hlsl);
        Assert.DoesNotContain("pick.Load", hlsl);

        // the anchor's palette writes the source's row as its tie under the anchor's own ancestor of it
        var order = UnionOrder(outDir);
        Assert.Contains($"uint2({order.IndexOf(C)},{order.IndexOf(A)})", File.ReadAllText(Path.Combine(outDir, "pose_palette_alpha_swap.hlsl")));

        Assert.Contains(result.Diagnostics, d => d.Contains("each keep their own pose"));
        Assert.Contains(result.Diagnostics, d => d.Contains("'beta' is matched to each copy by sharing the replaced part's position"));
        Assert.Contains(result.Diagnostics, d => d.Contains("has not drawn yet in a pass")
            && d.Contains("moves rigidly with the nearest such bone, and any other keeps its bind pose."));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("share one pose"));
    }

    [Fact]
    public void A_source_mesh_runs_its_passes_at_the_replacements_draw_only_in_a_frame_it_has_drawn_in()
    {
        var req = ParentPool();
        new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        // one flag per source mesh, declared, reset at the start of every frame and set inside the ring block
        Assert.Contains("global $zz_drew_beta = 0\n", Section(ini, "[Constants]"));
        Assert.Contains("$zz_drew_beta = 0\n", Section(ini, "\n[Present]\n"));
        Assert.Single(Regex.Matches(ini, Regex.Escape("$zz_drew_beta = 1\n")));
        Assert.Contains(RingRuns("beta"), Section(ini, "[TextureOverride_Cap_beta]"));

        // the source's pick and palette passes sit in its flag's test; the anchor's own passes and the skins
        // do not
        foreach (string line in new[] { "run = CustomShaderPosePick_beta_swap", "run = CustomShaderPosePalette_beta_swap" })
        {
            var gates = GatesAround(ini, line);
            Assert.NotEmpty(gates);
            Assert.All(gates, g => Assert.Equal(new[] { "if $zz_drew_beta == 1" }, g));
        }
        foreach (string line in new[] { "run = CustomShaderGather_alpha", "run = CustomShaderPoseAnchorMat_swap",
                     "run = CustomShaderPosePalette_alpha_swap", "run = CustomShaderPoseSkin_swap_p0", "run = CommandListDraw_swap" })
        {
            var gates = GatesAround(ini, line);
            Assert.NotEmpty(gates);
            Assert.All(gates, g => Assert.DoesNotContain(g, c => c.Contains("zz_drew_")));
        }
    }

    [Fact]
    public void The_frame_number_is_a_texture_cleared_at_load_and_advanced_once_a_frame_by_a_pass_and_a_copy()
    {
        var req = SameRootPool();
        new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        // cleared once when the mod loads: the frame number to 1, every ring slot to frame 0 with no rows
        string constants = Section(ini, "[Constants]");
        Assert.Contains("clear = Resource_PoseFrame 1\nclear = Resource_Ring_beta\n", constants);
        // advanced in [Present]: the next number written beside it, then copied back over it
        Assert.Contains("run = CustomShaderPoseFrame\nResource_PoseFrame = copy Resource_PoseFrameNext\n", Section(ini, "\n[Present]\n"));
        string frame = Section(ini, "[CustomShaderPoseFrame]");
        Assert.Contains("ps = pose_frame_ps.hlsl\n", frame);
        Assert.Contains("ps-t0 = Resource_PoseFrame\n", frame);
        Assert.Contains("o0 = set_viewport Resource_PoseFrameNext\n", frame);
        Assert.Contains("[Resource_PoseFrame]\ntype = Texture2D\nformat = R32G32B32A32_FLOAT\nwidth = 1\nheight = 1\n", ini);
        Assert.Contains("[Resource_PoseFrameNext]\ntype = Texture2D\nformat = R32G32B32A32_FLOAT\nwidth = 1\nheight = 1\n", ini);
        Assert.Contains("return float4(next >= 8388608.0 ? 1.0 : next, 0, 0, 0);", File.ReadAllText(Path.Combine(outDir, "pose_frame_ps.hlsl")));
        // the replaced part's capture and every ring slot take the number from that texture
        Assert.Contains("float4(frame.Load(int3(0,0,0)).x,0,0,0)", File.ReadAllText(Path.Combine(outDir, "pose_capture_ps.hlsl")));
        Assert.Contains("float4(frame.Load(int3(0, 0, 0)).x, 0, 0, 0)", File.ReadAllText(Path.Combine(outDir, "ring_stamp_beta.hlsl")));
        // the frame pass runs on its own in [Present], so it sets its state and saves its slot itself
        Assert.StartsWith("[CustomShaderPoseFrame]\nvs = pose_fullscreen.hlsl\nhs = null\nds = null\ngs = null\n"
            + "topology = triangle_list\ncull = none\ndepth_enable = false\nblend = disable\nResource_SavePST0 = ref ps-t0\n"
            + "od = null\n", frame);
        Assert.EndsWith("ps = pose_frame_ps.hlsl\nps-t0 = Resource_PoseFrame\no0 = set_viewport Resource_PoseFrameNext\ndraw = 3, 0\n"
            + "ps-t0 = Resource_SavePST0\n", frame.TrimEnd('\n') + "\n");
    }

    [Fact]
    public void A_source_mesh_whose_ring_would_pass_the_texture_row_limit_stops_the_build()
    {
        int max = MigotoEmitter.MaxRingPacket;
        Assert.Equal(MigotoEmitter.MaxTextureRows, ComputeTemplates.RingSlotRows(max) * MigotoEmitter.PoseRingEntries);
        Assert.True(ComputeTemplates.RingSlotRows(max + 1) * MigotoEmitter.PoseRingEntries > MigotoEmitter.MaxTextureRows);
        MigotoEmitter.RequireRingFits("c_body", "c_cloth", max);
        var e = Assert.Throws<AuthoredRefusalException>(() => MigotoEmitter.RequireRingFits("c_body", "c_cloth", max + 1));
        Assert.Equal("'c_body' can't be replaced: it moves with 'c_cloth', which has too many vertices to follow. "
            + "Remove this mesh edit", e.Message);
    }

    [Fact]
    public void A_ring_slot_takes_the_packet_and_the_draws_rows_and_frame_and_the_pick_counts_one_copys_slots_once()
    {
        int h = ComputeTemplates.PacketHeight(300), s = ComputeTemplates.RingSlotRows(300);
        Assert.Equal(h + 1, s);
        string gather = ComputeTemplates.EmitRingGather(300, 16);
        string stamp = ComputeTemplates.EmitRingStamp(300);
        HlslCheck.CompilesClean(gather, "ring_gather.hlsl", "vs_5_0");
        HlslCheck.CompilesClean(stamp, "ring_stamp.hlsl", "ps_5_0");
        HlslCheck.CompilesClean(ComputeTemplates.EmitPoseFrame(8388608), "pose_frame_ps.hlsl", "ps_5_0");

        // the gather: entry i of the packet at pixel (i % 64, slot*S + i / 64) of a ring of 16 slots
        Assert.Contains("Buffer<uint> slot   : register(t2);", gather);
        Assert.Contains($"uint x = i % 64, y = slot[0] * {s} + i / 64;", gather);
        Assert.Contains($"o.pos = float4((x + 0.5) / 64.0 * 2 - 1, 1 - (y + 0.5) / {16 * s}.0 * 2, 0.5, 1);", gather);
        // the stamp: the four rows at pixels 0-3 and the frame at pixel 4 of the slot's last row, nothing else
        Assert.Contains($"static const uint H={h};", stamp);
        Assert.Contains("static const uint S=H+1;", stamp);
        Assert.Contains("if (y != slot[0] * S + H || x > 4) discard;", stamp);
        Assert.Contains("return x == 0 ? r0 : x == 1 ? r1 : x == 2 ? r2 : x == 3 ? r3 : float4(frame.Load(int3(0, 0, 0)).x, 0, 0, 0);", stamp);

        // the pick reads the slots straight out of the ring and counts slots holding the same rows as one copy
        // drawn in several passes: the next-nearest copy skips the nearest's own slots
        string pick = ComputeTemplates.EmitPosePick(16, 0.3f, 3f, 1e-4f, 1e-5f, 3, (0.1f, -2f, 1e-7f), 300);
        HlslCheck.CompilesClean(pick, "pose_pick.hlsl", "ps_5_0");
        Assert.Contains("static const uint RING=16;", pick);
        Assert.Contains("static const float DUP=1e-05;", pick);
        Assert.Contains($"float4 Ring(uint k, uint r){{ return ring.Load(int3(r, k * {s} + {h}, 0)); }}", pick);
        Assert.Contains("bool SameCopy(uint a, uint b){\n    return all(abs(Ring(a,0)-Ring(b,0))<=DUP)", pick);
        Assert.Contains("if(o==(uint)nearest || !Live(o,stamp) || SameCopy(o,(uint)nearest)) continue;", pick);
        Assert.Contains("if(next>AHEAD*best) pick=nearest;", pick);
        Assert.DoesNotContain("M0[", pick);
        Assert.DoesNotContain("live[", pick);
    }

    [Fact]
    public void A_source_under_a_bone_the_anchor_recovers_is_predicted_from_that_bones_row_where_the_mask_says_this_draw_recovered_it()
    {
        var req = ParentPool();
        var result = new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        // the donor does not weight P, yet the palette carries it, and the pick reads P's slot and carries
        // the source root's rest origin by it
        var order = UnionOrder(outDir);
        Assert.Contains(P, order);
        string pick = File.ReadAllText(Path.Combine(outDir, "pose_pick_beta_swap.hlsl"));
        int slot = order.IndexOf(P);
        Assert.Contains($"float4(0.25,1.5,-0.75,1), float4x4(pal.Load(int3({4 * slot},0,0)),pal.Load(int3({4 * slot + 1},0,0)),"
            + $"pal.Load(int3({4 * slot + 2},0,0)),pal.Load(int3({4 * slot + 3},0,0))))", pick);
        Assert.Contains("static const float CAP=0.3;", pick);
        Assert.Contains("static const float AHEAD=3.0;", pick);
        // and P's row places no copy unless this draw recovered it itself
        Assert.Contains("Texture2D<float4> mask : register(t2);", pick);
        Assert.Contains($"if(mask.Load(int3({4 * slot},0,0)).x<0.5) pick=-1;", pick);
        Assert.Contains("ps-t2 = Resource_PoseMask_swap\n", Section(ini, "[CustomShaderPosePick_beta_swap]"));
        // a pick pass, then the palette pass reading its answer at t5 and the ring at t0
        Assert.Equal(Head("alpha", Picked("beta")) + Skins, PoseBlock(ini));
        Assert.Contains("[Resource_Pick_beta_swap]\ntype = Texture2D\nformat = R32G32B32A32_FLOAT\nwidth = 5\nheight = 1\n", ini);
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Resource_Pick_beta_swap]")));
        string pickSection = Section(ini, "[CustomShaderPosePick_beta_swap]");
        Assert.Contains("ps = pose_pick_beta_swap.hlsl\nps-t0 = Resource_PoseTex_swap\nps-t1 = Resource_AnchorMat_swap\nps-t2 = Resource_PoseMask_swap\n"
            + "ps-t3 = Resource_Ring_beta\n", pickSection);
        Assert.Contains("o0 = set_viewport Resource_Pick_beta_swap\n", pickSection);
        string palette = Section(ini, "[CustomShaderPosePalette_beta_swap]");
        Assert.Contains("ps-t0 = Resource_Ring_beta\n", palette);
        Assert.Contains("ps-t5 = Resource_Pick_beta_swap\nps-t6 = Resource_AnchorMat_swap\n", palette);
        int h = ComputeTemplates.PacketHeight(32);
        Assert.Contains($"float4 Ring(uint k, uint r){{ return ring.Load(int3(r, k * {h + 1} + {h}, 0)); }}", pick);
        string paletteHlsl = File.ReadAllText(Path.Combine(outDir, "pose_palette_beta_swap.hlsl"));
        Assert.Contains("Texture2D<float4>       pick   : register(t5);", paletteHlsl);
        Assert.Contains("float4 chosen = pick.Load(int3(0, 0, 0));\n    if (chosen.x < 0) discard;\n    Slot = (uint)chosen.x;\n", paletteHlsl);
        Assert.DoesNotContain("PickSlot", paletteHlsl);
        // the pick arithmetic is the one function the palette pass of a source placed any other way runs
        Assert.Contains("int PickSlot(float4 A0, float4 A1, float4 A2, float4 A3, float stamp){", pick);

        Assert.Contains(result.Diagnostics, d => d == "swap: palette retains donor-unused placement row 'P' to match 'beta' to each copy");
        Assert.Contains(result.Diagnostics, d => d == "swap: 'beta' is matched to each copy by where the replaced part's bone 'P' places it.");
    }

    [Fact]
    public void Every_palette_pass_of_a_pooled_replacement_writes_the_row_mask_beside_the_palette()
    {
        var req = ParentPool();
        new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        Assert.Contains("[Resource_PoseMask_swap]\ntype = Texture2D\nformat = R32G32B32A32_FLOAT\nwidth = 20\nheight = 1\n", ini);
        foreach (string mesh in new[] { "alpha", "beta" })
        {
            Assert.Contains("o0 = set_viewport Resource_PoseTex_swap\no1 = Resource_PoseMask_swap\ndraw = 3, 0\n",
                Section(ini, mesh == "alpha" ? "[CustomShaderPosePalette_alpha_swap]" : "[CustomShaderPosePalette_beta_swap]"));
            string hlsl = File.ReadAllText(Path.Combine(outDir, $"pose_palette_{mesh}_swap.hlsl"));
            Assert.Contains("struct PaletteOut { float4 row : SV_Target0; float4 own : SV_Target1; };", hlsl);
            // 1 where the slot's row is this mesh's own recovery of the slot's bone, 0 at a tie redirect
            Assert.Contains("u == asked ? SOUND[b] : 0.0", hlsl);
        }
        // the anchor's pass writes 0 at every slot it does not recover itself: its identity rows and its ties
        Assert.Contains("return Write(float4(comp == 0 ? 1.0 : 0.0, comp == 1 ? 1.0 : 0.0, comp == 2 ? 1.0 : 0.0, comp == 3 ? 1.0 : 0.0), 0.0);",
            File.ReadAllText(Path.Combine(outDir, "pose_palette_alpha_swap.hlsl")));
    }

    [Fact]
    public void A_source_placed_by_a_bone_an_anchor_tier_does_not_carry_is_absent_at_that_tiers_draws()
    {
        // alpha's lower-detail mesh carries A and B but not P, the bone that places beta
        string ad1 = Dump("alpha_lod1", 7, A, B);
        var req = ParentPool("out", new[] { new PoolTier("alpha", "alpha_lod1", "lod1", ad1, "aaaa0002").Rooted(A, T) });
        var result = new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        // the tier's own palette pass writes the mask too, and never 1 at P's slot: its map does not reach it
        Assert.Contains("o1 = Resource_PoseMask_swap\n", Section(ini, "[CustomShaderPosePalette_alpha_lod1_swap]"));
        Assert.Contains(Picked("beta"), PoseBlock(ini, "alpha_lod1"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("resting position"));
        Assert.Contains(result.Diagnostics, d => d == "alpha_lod1: this lower-detail mesh does not recover bone 'P', so at its "
            + "draws 'beta' is treated as absent: each of its bones under a bone of the replaced part moves rigidly with the "
            + "nearest such bone, and any other keeps its bind pose");
    }

    [Fact]
    public void A_source_under_another_sources_bone_runs_after_that_source_and_reads_the_mask_at_that_bone()
    {
        // gamma is listed ahead of beta, but beta's bone S places gamma's root U, so beta's passes run first
        var req = Pool("out", Donor("donor", 6), new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B)).Rooted(A, T),
            new PoolPart("gamma", Dump("gamma", 3, U, V)).Rooted(U, S, R, T),
            new PoolPart("beta", Dump("beta", 2, R, S)).Rooted(R, T),
        });
        var result = new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        Assert.Equal(Head("alpha", Self("beta"), Picked("gamma")) + Skins, PoseBlock(ini));
        var order = UnionOrder(outDir);
        int slot = order.IndexOf(S);
        string pick = File.ReadAllText(Path.Combine(outDir, "pose_pick_gamma_swap.hlsl"));
        Assert.Contains($"pal.Load(int3({4 * slot},0,0))", pick);
        // beta's palette pass writes the mask 1 at S's slot only where it found a copy of beta: with none it
        // discards, the anchor's 0 stays, and gamma is absent too
        Assert.Contains($"if(mask.Load(int3({4 * slot},0,0)).x<0.5) pick=-1;", pick);
        Assert.DoesNotContain("gate", pick);
        Assert.Contains("ps-t2 = Resource_PoseMask_swap\n", Section(ini, "[CustomShaderPosePick_gamma_swap]"));
        Assert.Contains("if (chosen < 0) discard;", File.ReadAllText(Path.Combine(outDir, "pose_palette_beta_swap.hlsl")));
        Assert.Contains(result.Diagnostics, d => d == "swap: 'gamma' is matched to each copy by where bone 'S' of 'beta' places it.");
    }

    [Fact]
    public void A_pose_block_saves_the_pick_passs_slot_for_a_dense_bone_placed_source_and_no_slim_slots()
    {
        // one-bone parts at 32 vertices ship dense, so no palette pass binds slots 3 or 4; the pick pass of a
        // bone-placed source still reads the ring at slot 3, and the block saves that slot alone
        string bd = Dump("beta", 2, C);
        SyntheticPool.SetRestOrigin(bd, C, new Vector3(0.25f, 1.5f, -0.75f));
        var req = Pool("out", Donor("donor", 2, 0, 1), new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A)).Rooted(A, T),
            new PoolPart("beta", bd).Rooted(C, A, T),
        });
        var result = new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        Assert.Contains(result.Diagnostics, d => d == "swap: 'beta' is matched to each copy by where the replaced part's bone 'A' places it.");
        Assert.DoesNotContain("PacketSel", ini);
        Assert.Contains("ps-t3 = Resource_Ring_beta\n", Section(ini, "[CustomShaderPosePick_beta_swap]"));
        Assert.Equal(Head("alpha", Picked("beta")) + Skins, PoseBlock(ini, "alpha", slots: new[] { 0, 1, 2, 3, 5, 6 }));
    }

    [Fact]
    public void The_nearest_recovered_ancestor_places_a_source_before_a_farther_one_the_anchor_owns()
    {
        // gamma's root C sits under P, which beta owns, and under A, which the anchor owns: P is nearer
        var req = Pool("out", Donor("donor", 6), new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B)).Rooted(A, T),
            new PoolPart("beta", Dump("beta", 2, P, X)).Rooted(P, A, T),
            new PoolPart("gamma", Dump("gamma", 3, C, D)).Rooted(C, P, A, T),
        });
        var result = new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        Assert.Contains(result.Diagnostics, d => d == "swap: 'beta' is matched to each copy by where the replaced part's bone 'A' places it.");
        Assert.Contains(result.Diagnostics, d => d == "swap: 'gamma' is matched to each copy by where bone 'P' of 'beta' places it.");
        Assert.StartsWith(Head("alpha", Picked("beta"), Picked("gamma")), PoseBlock(ini));
        int slot = UnionOrder(outDir).IndexOf(P);
        Assert.Contains($"if(mask.Load(int3({4 * slot},0,0)).x<0.5) pick=-1;", File.ReadAllText(Path.Combine(outDir, "pose_pick_gamma_swap.hlsl")));
    }

    [Fact]
    public void A_bone_the_anchor_holds_only_at_every_vertex_places_no_source_and_the_next_ancestor_does()
    {
        // The anchor carries B only proportionally to A over every vertex: its full solve calls B sound, but
        // no small selection holds B, so the shipped operator would tie it. gamma's root W sits under B, then
        // under A, which the anchor holds: A places gamma.
        string ad = Path.Combine(_root, "anchor");
        SyntheticPool.WriteProportionalPairDump(ad, seed: 7, mixed: 1100, skew: 200, A, B);
        var req = Pool("out", Donor("donor", 4, 0, 2, 3), new[]
        {
            new PoolPart("alpha", ad).Rooted(A, T),
            new PoolPart("gamma", Dump("gamma", 3, W, Q)).Rooted(W, B, A, T),
        });
        var result = new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        Assert.Contains(result.Diagnostics, d => d == "swap: 'gamma' is matched to each copy by where the replaced part's bone 'A' places it.");
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("bone 'B' places it"));
        int slot = UnionOrder(outDir).IndexOf(A);
        Assert.Contains($"pal.Load(int3({4 * slot},0,0))", File.ReadAllText(Path.Combine(outDir, "pose_pick_gamma_swap.hlsl")));
    }

    [Fact]
    public void A_source_whose_root_nobody_recovers_an_ancestor_of_is_looked_for_near_the_anchors_position_within_a_body_length()
    {
        var req = Pool("out", Donor("donor", 4), new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B)).Rooted(A, T),
            new PoolPart("beta", Dump("beta", 2, R, S)).Rooted(R, T),
        });
        var result = new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        // one pass at the replacement's draw, its palette pass, which looks for the nearest copy within a
        // body length itself and reads that copy's packet region
        Assert.Equal(Head("alpha", Self("beta")) + Skins, PoseBlock(ini));
        Assert.DoesNotContain("PosePick", ini);
        Assert.DoesNotContain("Resource_Pick_", ini);
        Assert.Contains("ps-t0 = Resource_Ring_beta\n", Section(ini, "[CustomShaderPosePalette_beta_swap]"));
        Assert.Contains("ps-t6 = Resource_AnchorMat_swap\n", Section(ini, "[CustomShaderPosePalette_beta_swap]"));
        string pick = File.ReadAllText(Path.Combine(outDir, "pose_palette_beta_swap.hlsl"));
        Assert.Contains("float3 want=A3.xyz;", pick);
        Assert.Contains("static const float CAP=1.2;", pick);
        Assert.Contains("if(nearest>=0 && best<=CAP){", pick);
        Assert.Contains("if(next>AHEAD*best) pick=nearest;", pick);
        Assert.Contains("return q.Load(int3(i % 64, Slot * S + i / 64, 0)).xyz;", pick);
        Assert.DoesNotContain("pal.Load(", pick);
        Assert.DoesNotContain("mask.Load", pick);
        Assert.Contains(result.Diagnostics, d => d.Contains("'beta' is matched to each copy by its distance from the replaced part's position"));
    }

    [Fact]
    public void A_pool_with_a_source_of_unknown_root_keeps_the_frame_chain_and_says_why()
    {
        var req = Pool("out", Donor("donor", 4), new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B)).Rooted(A, T),
            new PoolPart("beta", Dump("beta", 2, C, D)),
        });
        var result = new MigotoEmitter().Build(req);
        AssertKeepsTheChain(req.OutDir, result, "Which bone 'beta' is attached to is not known");
    }

    [Fact]
    public void A_pool_with_a_source_whose_root_its_own_table_does_not_list_keeps_the_frame_chain_and_says_why()
    {
        var req = Pool("out", Donor("donor", 4), new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B)).Rooted(A, T),
            new PoolPart("beta", Dump("beta", 2, C, D)).Rooted(X, T),
        });
        var result = new MigotoEmitter().Build(req);
        AssertKeepsTheChain(req.OutDir, result, "'beta' does not list the bone it is attached to");
    }

    [Fact]
    public void A_pool_with_a_wardrobe_member_keeps_the_frame_chain_and_says_why()
    {
        // union A, B, C plus the group bone G as the donor's dense continuation (index 3)
        var req = Pool("out", Donor("donor", 4), new[]
        {
            new PoolPart("alpha", Dump("alpha", 1, A, B)).Rooted(A, T),
            new PoolPart("beta", Dump("beta", 2, A, C)).Rooted(A, T),
        });
        var pipe = req.Pipelines[0] with
        {
            Groups = new[]
            {
                new PoolGroup(7, new[] { G }, new[]
                {
                    new PoolGroupMember(1, PresenceContext.Always, "mv1", "mv1")
                    {
                        Meshes = new[] { new PoolGroupMesh("mv1", "", Dump("mv1", 4, C, G), "dddd0011") },
                    },
                }),
            },
        };
        var result = new MigotoEmitter().Build(req with { Pipelines = new[] { pipe } });
        AssertKeepsTheChain(req.OutDir, result, "Some of its bones come from a wardrobe member's own draw");
    }

    private static void AssertKeepsTheChain(string outDir, MigotoEmitter.Result result, string why)
    {
        string ini = Ini(outDir);
        ModBuilderTests.AssertNoDuplicateSections(ini);
        Assert.Contains("if $zz_done_swap == 0\n", ini);
        Assert.Contains("[CustomShaderSkin_swap]", ini);
        Assert.DoesNotContain("[CustomShaderPose", ini);
        Assert.DoesNotContain("run = CustomShaderPose", ini);
        Assert.DoesNotContain("[Resource_Ring", ini);
        Assert.DoesNotContain("Resource_PoseFrame", ini);
        Assert.Contains(result.Diagnostics, d => d.Contains("share one pose") && d.Contains(why));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("each keep their own pose"));
    }

    [Fact]
    public void A_source_with_its_own_recovering_tier_rings_and_picks_each_mesh_the_tier_first()
    {
        string td = Dump("beta_lod1", 5, C, D);
        SyntheticPool.SetRestOrigin(td, C, new Vector3(0.25f, 1.5f, -0.75f));
        var req = ParentPool("out", null, new PoolTier("beta", "beta_lod1", "lod1", td, "bbbb0002").Rooted(C, P, A, T));
        var result = new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        Assert.Contains(RingRuns("beta"), Section(ini, "[TextureOverride_Cap_beta]"));
        string tierCap = Section(ini, "[TextureOverride_Cap_beta_lod1]");
        Assert.Contains(RingRuns("beta_lod1"), tierCap);
        Assert.DoesNotContain("Resource_beta_lod1_Posed", ini);
        Assert.Equal(Head("alpha", Picked("beta_lod1"), Picked("beta")) + Skins, PoseBlock(ini));
        foreach (string mesh in new[] { "beta", "beta_lod1" })
        {
            Assert.Single(Regex.Matches(ini, Regex.Escape($"[Resource_Ring_{mesh}]")));
            Assert.Contains($"clear = Resource_Ring_{mesh}\n", ini);
            Assert.Single(Regex.Matches(ini, Regex.Escape($"[CustomShaderRingGather_{mesh}]")));
            Assert.Contains($"$zz_drew_{mesh} = 0\n", Section(ini, "\n[Present]\n"));
            Assert.True(File.Exists(Path.Combine(outDir, $"pose_pick_{mesh}_swap.hlsl")));
        }
        int slot = UnionOrder(outDir).IndexOf(P);
        Assert.Contains($"pal.Load(int3({4 * slot},0,0))", File.ReadAllText(Path.Combine(outDir, "pose_pick_beta_lod1_swap.hlsl")));
        Assert.Contains(result.Diagnostics, d => d.Contains("'beta_lod1' is matched to each copy"));
    }

    [Fact]
    public void Toggle_states_on_one_anchor_share_the_sources_ring_which_runs_while_either_is_on()
    {
        string ad = Dump("alpha", 1, A, B, P), bd = Dump("beta", 2, C, D);
        var parts = new[] { new PoolPart("alpha", ad).Rooted(A, T), new PoolPart("beta", bd).Rooted(C, P, A, T) };
        var pipelines = new List<ReplacePipeline>();
        for (int state = 0; state < 2; state++)
            pipelines.Add(new ReplacePipeline
            {
                Suffix = $"swap_s{state}",
                Parts = parts,
                Anchor = "alpha",
                DonorDir = Donor($"donor{state}", 5),
                CaptureHashes = Hashes(parts),
                BonePaths = Paths,
                ToggleKey = new KeyRef("F7", state),
            });
        string outDir = Path.Combine(_root, "out-states");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = pipelines,
            KeyCycles = new[] { new KeyCycle("F7", 2, 0) },
        });
        string ini = Ini(outDir);
        AssertLoads(ini, outDir);

        // one ring for the source, written once per draw while either state's pipeline is on
        Assert.Single(Regex.Matches(ini, Regex.Escape("[CustomShaderRingBlock_beta]")));
        Assert.Contains("if ($zz_key_f7 == 0) || ($zz_key_f7 == 1)\n" + RingRuns("beta") + "endif\n",
            Section(ini, "[TextureOverride_Cap_beta]"));
        Assert.Single(Regex.Matches(ini, Regex.Escape("run = CustomShaderRingBlock_beta\n")));
        // one flag and one slot counter for the mesh however many pipelines read it
        Assert.Single(Regex.Matches(ini, Regex.Escape("global $zz_drew_beta = 0\n")));
        Assert.Single(Regex.Matches(ini, Regex.Escape("global $zz_rslot_beta = 0\n")));
        Assert.Contains("if ($zz_key_f7 == 0) || ($zz_key_f7 == 1)\nrun = CustomShaderPoseFrame\n", Section(ini, "\n[Present]\n"));
        string alpha = Section(ini, "[TextureOverride_Cap_alpha]");
        foreach (int state in new[] { 0, 1 })
        {
            Assert.Matches($@"\nif [^\n]+\nrun = CustomShaderPoseBlock_alpha_swap_s{state}\nrun = CommandListDraw_swap_s{state}\nendif\n", alpha);
            Assert.Matches($@"^run = CustomShaderGather_alpha\nrun = CustomShaderPoseAnchorMat_swap_s{state}\n"
                + $@"run = CustomShaderPosePalette_alpha_swap_s{state}\n" + Regex.Escape(Picked("beta", $"swap_s{state}"))
                + $@"(run = CustomShaderPoseSkin_swap_s{state}_p\d+\n)+$", PoseBlock(ini, "alpha", $"swap_s{state}"));
        }
    }

    [Fact]
    public void While_the_pooled_replacement_is_off_no_ring_pass_or_frame_pass_runs()
    {
        // the replacement answers to position 0 of a two-position key; in position 1 nothing of it is on
        var req = SameRootPool(toggleKey: new KeyRef("F7"));
        new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        foreach (string line in new[] { "run = CustomShaderRingBlock_beta", "$zz_drew_beta = 1",
                     "run = CustomShaderPoseFrame", "Resource_PoseFrame = copy Resource_PoseFrameNext" })
        {
            var gates = GatesAround(ini, line);
            Assert.Single(gates);
            Assert.Contains("if $zz_key_f7 == 0", gates[0]);
        }
        Assert.Contains("if $zz_key_f7 == 0\n" + RingRuns("beta") + "endif\n", Section(ini, "[TextureOverride_Cap_beta]"));
        // the ring block's own lines run only through that gated line
        Assert.Single(Regex.Matches(ini, Regex.Escape("run = CustomShaderRingGather_beta\n")));
        Assert.Single(Regex.Matches(ini, Regex.Escape("run = CustomShaderRingBlock_beta\n")));
        // the anchor's pose block sits in the same gate, as its passes did, and the source's palette pass
        // inside the block under the mesh's flag
        var blockGates = GatesAround(ini, "run = CustomShaderPoseBlock_alpha_swap");
        Assert.Single(blockGates);
        Assert.Contains("if $zz_key_f7 == 0", blockGates[0]);
        Assert.Single(Regex.Matches(ini, Regex.Escape("run = CustomShaderPoseAnchorMat_swap\n")));
        Assert.All(GatesAround(ini, "run = CustomShaderPosePalette_beta_swap"), g => Assert.Contains("if $zz_drew_beta == 1", g));
    }

    /// <summary>A replaced mesh of two vanilla submeshes: the draw routes per submesh shape.</summary>
    private static readonly DrawShapeSet TwoShapes = new(new[] { new DrawShape(0, 60), new DrawShape(60, 84) }, 144);

    [Fact]
    public void Where_the_draw_routes_per_submesh_the_pooled_passes_run_ahead_of_each_routed_list_and_the_capture_only_skips()
    {
        var req = SameRootPool(anchorShapes: TwoShapes);
        new MigotoEmitter().Build(req);
        string outDir = req.OutDir, ini = Ini(outDir);
        AssertLoads(ini, outDir);

        string cap = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.Contains("handling = skip\n", cap);
        Assert.DoesNotContain("run = ", cap);
        Assert.DoesNotContain("Resource_alpha_Posed", ini);
        Assert.Contains("match_first_index = 0\nmatch_index_count = 60\nrun = CustomShaderPoseBlock_alpha_DrawS0_swap\nrun = CommandListDrawS0_swap\n",
            Section(ini, "[TextureOverride_Cap_alpha_DrawS0]"));
        Assert.Equal(Head("alpha", Self("beta")) + "run = CustomShaderPoseSkin_swap_p0\n", PoseBlock(ini, "alpha_DrawS0"));
        Assert.Contains("match_first_index = 60\nmatch_index_count = 84\nrun = CustomShaderPoseBlock_alpha_DrawS1_swap\nrun = CommandListDrawS1_swap\n",
            Section(ini, "[TextureOverride_Cap_alpha_DrawS1]"));
        Assert.Equal(Head("alpha", Self("beta")) + "run = CustomShaderPoseSkin_swap_p1\n", PoseBlock(ini, "alpha_DrawS1"));
        Assert.Contains("match_first_index = 0\nmatch_index_count = 144\nrun = CustomShaderPoseBlock_alpha_DrawFull_swap\nrun = CommandListDraw_swap\n",
            Section(ini, "[TextureOverride_Cap_alpha_DrawFull]"));
        Assert.Equal(Head("alpha", Self("beta")) + Skins, PoseBlock(ini, "alpha_DrawFull"));
        // the source's capture section still writes its ring at every draw
        Assert.Contains(RingRuns("beta"), Section(ini, "[TextureOverride_Cap_beta]"));
    }

    [Fact]
    public void Two_builds_of_one_pooled_fixture_write_the_same_bytes()
    {
        var first = ParentPool("out-a");
        new MigotoEmitter().Build(first);
        // the second build reads the very same dumps and donor
        var second = first with { OutDir = Path.Combine(_root, "out-b") };
        new MigotoEmitter().Build(second);
        var files = Directory.GetFiles(first.OutDir).Select(f => Path.GetFileName(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(files, Directory.GetFiles(second.OutDir).Select(f => Path.GetFileName(f)).OrderBy(f => f, StringComparer.Ordinal));
        foreach (string shipped in new[] { "pose_pick_beta_swap.hlsl", "ring_gather_beta.hlsl", "ring_stamp_beta.hlsl",
                     "ring_slot_0.buf", "ring_slot_15.buf", "pose_frame_ps.hlsl", "pose_capture_ps.hlsl" })
            Assert.Contains(shipped, files);
        foreach (string f in files)
            Assert.True(File.ReadAllBytes(Path.Combine(first.OutDir, f)).AsSpan()
                .SequenceEqual(File.ReadAllBytes(Path.Combine(second.OutDir, f))), $"{f} differs between two builds");
    }

    [Fact]
    public void A_witness_row_kept_for_the_chain_is_not_reported_where_the_pooled_route_converts_nothing_through_it()
    {
        // the donor weights B and C alone, so A is kept only as the witness the chain would convert beta by
        PoolBuildRequest Shared(string outName, bool rooted) => Pool(outName, Donor("donor-" + outName, 3, 1, 2), new[]
        {
            rooted ? new PoolPart("alpha", Dump("alpha", 1, A, B)).Rooted(A, T) : new PoolPart("alpha", Dump("alpha", 1, A, B)),
            rooted ? new PoolPart("beta", Dump("beta", 2, A, C)).Rooted(A, T) : new PoolPart("beta", Dump("beta", 2, A, C)),
        });
        string witness = "swap: palette retains donor-unused witness row 'A'";

        var chain = new MigotoEmitter().Build(Shared("chain", rooted: false));
        Assert.Contains(chain.Diagnostics, d => d.StartsWith(witness, StringComparison.Ordinal));

        var pooled = new MigotoEmitter().Build(Shared("pooled", rooted: true));
        Assert.Contains(pooled.Diagnostics, d => d.Contains("each keep their own pose"));
        Assert.DoesNotContain(pooled.Diagnostics, d => d.StartsWith(witness, StringComparison.Ordinal));
    }

    [Fact]
    public void The_pooled_route_declares_no_posed_reference_or_constants_copy_no_section_names()
    {
        var req = ParentPool();
        new MigotoEmitter().Build(req);
        string ini = Ini(req.OutDir);

        Assert.DoesNotContain("_Posed]", ini);
        Assert.DoesNotContain("_CB]", ini);
    }

    [Fact]
    public void A_renderers_root_chain_hashes_every_ancestor_from_the_link_the_meshs_own_table_starts_its_root_at()
    {
        var chain = new[] { "Prefab", "Root", "Hips", "Spine", "SkirtRoot" };
        static uint Hash(string path) => Remold.Core.Skeleton.BoneTable.Hash(path);

        // the mesh lists its root by its path from Root, so every ancestor is hashed from Root down
        Assert.Equal(new[] { Hash("Root/Hips/Spine/SkirtRoot"), Hash("Root/Hips/Spine"), Hash("Root/Hips"), Hash("Root") },
            ModBuilder.RootChainHashes(chain, new[] { Hash("Root/Hips"), Hash("Root/Hips/Spine/SkirtRoot") }));
        // a mesh not listing its root leaves the starting link unknown: hashed from the top, the root does not
        // match its table, which is what the emitter refuses on
        var unlisted = ModBuilder.RootChainHashes(chain, new[] { Hash("Root/Hips") });
        Assert.Equal(Hash("Prefab/Root/Hips/Spine/SkirtRoot"), unlisted[0]);
        Assert.Equal(5, unlisted.Count);
    }

    [Fact]
    public void A_renderer_rig_read_that_fails_on_the_file_stops_the_build_and_any_other_failure_is_logged_with_no_chain()
    {
        var reader = new Remold.Core.Bundles.BundleReader();
        var lines = new List<string>();
        string dump = Dump("alpha", 1, A, B);

        // the game holding the file, or the file missing from the install, stops the build by name
        Assert.Throws<IOException>(() => ModBuilder.ReadRootChain(reader, _ => throw new IOException("in use"),
            "rig.bundle", 5, dump, "alpha", lines));
        Assert.Throws<AuthoredRefusalException>(() => ModBuilder.ReadRootChain(reader,
            _ => throw new AuthoredRefusalException("the game files for the renderer of 'alpha' can't be read in this install"),
            "rig.bundle", 5, dump, "alpha", lines));
        Assert.Empty(lines);

        // a rig that reads wrong: no chain, and the build log says why
        Assert.Null(ModBuilder.ReadRootChain(reader, _ => throw new InvalidDataException("not a rig"), "rig.bundle", 5,
            dump, "alpha", lines));
        Assert.Equal("alpha: couldn't read its renderer's rig (not a rig)", Assert.Single(lines));
        // a part with no renderer is not read at all
        Assert.Null(ModBuilder.ReadRootChain(reader, _ => throw new IOException("not read"), null, 0, dump, "alpha", lines));
    }

    [Fact]
    public void The_pooled_route_shaders_compile_clean_at_their_stages_in_every_rule()
    {
        HlslCheck.CompilesClean(ComputeTemplates.EmitPoseCaptureVertex(), "pose_capture_vs.hlsl", "vs_5_0");
        HlslCheck.CompilesClean(ComputeTemplates.EmitPoseCapturePixel(), "pose_capture_ps.hlsl", "ps_5_0");
        HlslCheck.CompilesClean(ComputeTemplates.EmitRingGather(300, 16), "ring_gather.hlsl", "vs_5_0");
        HlslCheck.CompilesClean(ComputeTemplates.EmitRingGather(1, 16), "ring_gather_one.hlsl", "vs_5_0");
        HlslCheck.CompilesClean(ComputeTemplates.EmitRingStamp(300), "ring_stamp.hlsl", "ps_5_0");
        HlslCheck.CompilesClean(ComputeTemplates.EmitPosePick(16, 0.3f, 3f, 1e-4f, 1e-5f, 3, (0.1f, -2f, 1e-7f), 300), "pose_pick.hlsl", "ps_5_0");
        foreach (var rule in new[] { ComputeTemplates.PickRule.SameRoot, ComputeTemplates.PickRule.ByRow, ComputeTemplates.PickRule.ByPosition })
        {
            HlslCheck.CompilesClean(ComputeTemplates.EmitPoolPosePalette(40, slim: false, n: 300, new[] { (7u, 3u) },
                Enumerable.Range(0, 10).Select(b => b != 4).ToList(), 300, rule, 16, 0.3f, 3f, 1e-4f, 1e-5f), $"pose_pool_dense_{rule}.hlsl", "ps_5_0");
            HlslCheck.CompilesClean(ComputeTemplates.EmitPoolPosePalette(4, slim: true, n: 300, Array.Empty<(uint, uint)>(),
                new[] { true }, 300, rule, 16, 1.2f, 3f, 1e-4f, 1e-5f), $"pose_pool_slim_{rule}.hlsl", "ps_5_0");
        }
        HlslCheck.CompilesClean(ComputeTemplates.EmitPosePalette(40, slim: false, n: 300, new[] { (7u, 3u) },
            Enumerable.Range(0, 10).Select(b => b != 4).ToList()), "pose_mask_dense.hlsl", "ps_5_0");
        HlslCheck.CompilesClean(ComputeTemplates.EmitPosePalette(4, slim: true, n: 300, Array.Empty<(uint, uint)>(),
            new[] { false }), "pose_mask_slim.hlsl", "ps_5_0");
    }
}
