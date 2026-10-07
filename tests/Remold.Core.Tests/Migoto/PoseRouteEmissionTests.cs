using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The per-copy pose route: a Replace whose every recovered row comes from the replaced part itself
/// rebuilds its posed stream right before each of its draws by pixel passes (a palette pass per kernel
/// mesh, a skin pass per piece) and draws it directly, so each copy of the part draws from its own draw.
/// Where the draw routes per vanilla submesh, the passes ride the routed sections and run at no other
/// fire of the mesh. A pool reaching other parts keeps the once-per-frame chain. No compute runs on the
/// route and nothing the draw reads was written through an unordered-access view.
/// </summary>
public class PoseRouteEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-pose-" + Guid.NewGuid().ToString("N"));

    public PoseRouteEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const uint A = 101, B = 102, C = 103;

    /// <summary>Generic (rank-4-support) positions: the shared fixture's ramp positions are near-collinear,
    /// which the weak-support sentinel correctly rejects.</summary>
    private static void GenericPositions(string dir, int verts)
    {
        var s0 = File.ReadAllBytes(Path.Combine(dir, "stream0.buf"));
        for (int v = 0; v < verts; v++)
        {
            BitConverter.GetBytes((v * 13 % 17) / 4f).CopyTo(s0, v * 40);
            BitConverter.GetBytes((v * 7 % 23) / 5f).CopyTo(s0, v * 40 + 4);
            BitConverter.GetBytes((v * 11 % 29) / 6f).CopyTo(s0, v * 40 + 8);
        }
        File.WriteAllBytes(Path.Combine(dir, "stream0.buf"), s0);
    }

    /// <summary>One part, alpha, posing A and B, replaced by a two-submesh donor over its own bones.</summary>
    private PoolBuildRequest SinglePart(out string outDir, params PoolTier[] tiers) =>
        SinglePart(out outDir, anchorShapes: null, tiers);

    /// <summary>The same part, whose own mesh has two vanilla submeshes (<see cref="TwoShapes"/>), so its
    /// draw routes per submesh shape.</summary>
    private PoolBuildRequest RoutedSinglePart(out string outDir, params PoolTier[] tiers) =>
        SinglePart(out outDir, TwoShapes, tiers);

    private PoolBuildRequest SinglePart(out string outDir, DrawShapeSet? anchorShapes, PoolTier[] tiers)
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        GenericPositions(ad, 32);
        string donor = Path.Combine(_root, "donor"); SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 2);
        outDir = Path.Combine(_root, "out");
        return new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad) },
                    Anchor = "alpha",
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                    Tiers = tiers.Length > 0 ? tiers : null,
                    AnchorShapes = anchorShapes,
                },
            },
        };
    }

    /// <summary>alpha (A, B) with beta (B, C) alongside: rows beta owns are recovered at beta's draw, and B
    /// is the witness the two share.</summary>
    private PoolBuildRequest TwoParts(out string outDir, bool shareBone)
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        string bd = Path.Combine(_root, "beta"); SyntheticPool.WritePartDump(bd, 2, 16, shareBone ? new[] { B, C } : new[] { C });
        GenericPositions(ad, 32);
        GenericPositions(bd, 16);
        outDir = Path.Combine(_root, "out");
        return new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "alpha",
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                },
            },
        };
    }

    private static string Section(string ini, string header)
    {
        int at = ini.IndexOf(header, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{header} missing");
        int end = ini.IndexOf("\n\n", at, StringComparison.Ordinal);
        return end < 0 ? ini[at..] : ini[at..(end + 1)];
    }

    /// <summary>A one-bone part of <paramref name="verts"/> vertices replaced on its own bones: at 64
    /// vertices its operator ships slim, at 32 dense (the operator conditioning tests pin both).</summary>
    private PoolBuildRequest OneBone(string name, int verts, out string outDir)
    {
        string dir = Path.Combine(_root, name);
        SyntheticPool.WritePartDump(dir, seed: 4, verts: verts, boneHashes: new uint[] { A });
        outDir = Path.Combine(_root, "out-" + name);
        return new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart(name, dir) },
                    CaptureHashes = new Dictionary<string, string> { [name] = "aaaa000b" },
                },
            },
        };
    }

    private static uint[] ReadUInts(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        return words;
    }

    private static ushort[] ReadUShorts(string path, int count)
    {
        var bytes = File.ReadAllBytes(path);
        var words = new ushort[count];
        Buffer.BlockCopy(bytes, 0, words, 0, count * 2);
        return words;
    }

    private static byte[] Bytes(IEnumerable<uint> words) => words.SelectMany(BitConverter.GetBytes).ToArray();

    /// <summary>The packet of <paramref name="mesh"/> worked out here from the operator the build shipped,
    /// not by the emitter: the distinct vertices of its vertex list (<see cref="SyntheticPool.ShippedSel"/>)
    /// in ascending order, each vertex's entry, and <c>Sel</c> in entries; or, for a dense operator, every
    /// vertex.</summary>
    private static (uint[] Index, uint[] Lookup, uint[]? Sel) ExpectedPacket(string outDir, string mesh, int n)
    {
        if (SyntheticPool.ShippedSel(outDir, mesh) is not { } sel)
        {
            var every = Enumerable.Range(0, n).Select(v => (uint)v).ToArray();
            return (every, every, null);
        }
        uint[] index = new SortedSet<uint>(sel).ToArray();
        var entry = new Dictionary<uint, uint>();
        var lookup = new uint[n];
        for (int i = 0; i < index.Length; i++) { entry[index[i]] = (uint)i; lookup[index[i]] = (uint)i; }
        return (index, lookup, sel.Select(v => entry[v]).ToArray());
    }

    /// <summary>The draw of a gather section, line for line (unchanged by the route): a pass inside a block,
    /// naming only its own vertex shader and topology, its target and its binds.</summary>
    private static string GatherDraw(string section, string mesh, int p, string? source) =>
        $"[{section}]\nvs = gather_{mesh}.hlsl\nps = gather_ps.hlsl\ntopology = point_list\n"
        + $"o0 = set_viewport Resource_Packet_{mesh}\n"
        + $"ib = Resource_PacketIndex_{mesh}\nvs-t1 = Resource_PacketLookup_{mesh}\n"
        + (source is null ? "" : $"vb0 = {source}\n")
        + $"drawindexed = {p}, 0, 0\n"
        + "\n";

    /// <summary>The pixel-shader slots a pose block saves for a replacement whose passes bind
    /// <paramref name="slots"/>: 0 to 2, 5 and 6 always (the packet, operator and map, the bind geometry and
    /// weights), 3 and 4 where an operator is slim, 3 where a source's pick places it.</summary>
    internal static readonly int[] DenseSlots = { 0, 1, 2, 5, 6 }, SlimSlots = { 0, 1, 2, 3, 4, 5, 6 };

    /// <summary>The pose block of one section and pipeline, line for line up to its run lines: the shared
    /// state set once, the draw's binds at <paramref name="slots"/>, its index buffer and lookup slot (and
    /// the first vertex buffer where its gather rebinds it) saved, the depth target and the other colour
    /// targets cleared.</summary>
    internal static string BlockHead(string name, IReadOnlyList<int> slots, bool gatherRef = false) =>
        $"[{name}]\nvs = pose_fullscreen.hlsl\nhs = null\nds = null\ngs = null\n"
        + "topology = triangle_list\ncull = none\ndepth_enable = false\nblend = disable\n"
        + string.Concat(slots.Select(k => $"Resource_SavePST{k} = ref ps-t{k}\n"))
        + "Resource_SaveIB = ref ib\nResource_SaveVST1 = ref vs-t1\n" + (gatherRef ? "Resource_SaveVB0 = ref vb0\n" : "")
        + "od = null\no1 = null\no2 = null\no3 = null\no4 = null\no5 = null\no6 = null\no7 = null\n";

    /// <summary>The pose block's closing lines: the saved binds put back.</summary>
    internal static string BlockTail(IReadOnlyList<int> slots, bool gatherRef = false) =>
        string.Concat(slots.Select(k => $"ps-t{k} = Resource_SavePST{k}\n"))
        + "ib = Resource_SaveIB\nvs-t1 = Resource_SaveVST1\n" + (gatherRef ? "vb0 = Resource_SaveVB0\n" : "");

    /// <summary>The pose block section named <paramref name="stem"/> (the section's name after
    /// <c>Cap_</c>, then the pipeline's suffix), asserted to open and close as every block does, saving
    /// exactly the pixel-shader slots the passes it runs bind between them (and <paramref name="slots"/>,
    /// where a test names them), with the run lines between returned.</summary>
    internal static string PoseBlock(string ini, string stem, bool gatherRef = false, IReadOnlyList<int>? slots = null)
    {
        string name = $"CustomShaderPoseBlock_{stem}";
        string section = Section(ini, $"[{name}]");
        // the slots the passes bind, read off every section the block runs (both arms of a toggle included)
        var bound = new SortedSet<int>();
        foreach (Match run in Regex.Matches(section, @"^run = (CustomShader\S+)$", RegexOptions.Multiline))
            foreach (Match bind in Regex.Matches(Section(ini, $"[{run.Groups[1].Value}]"), @"^ps-t(\d) = ", RegexOptions.Multiline))
                bound.Add(int.Parse(bind.Groups[1].Value));
        Assert.True(DenseSlots.All(bound.Contains), $"{name} binds {string.Join(",", bound)}");
        var saved = Regex.Matches(section, @"Resource_SavePST(\d) = ref ps-t\1\n").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal(bound, saved);
        if (slots is not null) Assert.Equal(slots, saved);
        slots = saved;
        string head = BlockHead(name, slots, gatherRef), tail = BlockTail(slots, gatherRef);
        Assert.StartsWith(head, section);
        int end = section.IndexOf(tail, StringComparison.Ordinal);
        Assert.True(end > 0, $"{name} does not put the binds back:\n{section}");
        Assert.Equal(tail, section[end..].TrimEnd('\n') + "\n");
        return section[head.Length..end];
    }

    private static string GatherBlock(string mesh, int p) =>
        $"[Resource_Packet_{mesh}]\ntype = Texture2D\nformat = R32G32B32A32_FLOAT\nwidth = 64\nheight = {(p + 63) / 64}\n"
        + "array = 1\nmips = 1\nmsaa = 1\n"
        + "bind_flags = shader_resource render_target\n"
        + $"[Resource_PacketIndex_{mesh}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = packet_index_{mesh}.buf\n"
        + $"[Resource_PacketLookup_{mesh}]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = packet_lookup_{mesh}.buf\n"
        + "\n"
        + GatherDraw($"CustomShaderGather_{mesh}", mesh, p, null);

    private static string GatherRefBlock(string anchor, int p) =>
        GatherDraw($"CustomShaderGatherRef_{anchor}", anchor, p, $"Resource_{anchor}_Posed");

    /// <summary>The palette pass of <paramref name="mesh"/>, line for line: the fullscreen triangle with the
    /// palette shader, the packet, operator and map (and a slim operator's list and widths) bound over saved
    /// slots, the depth target and other colour targets cleared, the viewport set from the palette texture,
    /// the draw, and the slots put back.</summary>
    private static string PaletteBlock(string mesh, string sfx, bool slim, string cpinv)
    {
        var slots = new List<(int, string)> { (0, $"Resource_Packet_{mesh}"), (1, cpinv), (2, $"Resource_{mesh}_Map_{sfx}") };
        if (slim) slots.AddRange(new[] { (3, $"Resource_{mesh}_PacketSel"), (4, $"Resource_{mesh}_Off") });
        return PassBlock($"CustomShaderPosePalette_{mesh}_{sfx}", $"pose_palette_{mesh}_{sfx}.hlsl", slots,
            $"o0 = set_viewport Resource_PoseTex_{sfx}\n");
    }

    /// <summary>The skin pass of piece <paramref name="k"/>, line for line: the palette, the piece's map and
    /// the replacement's bind geometry and weights, the viewport borrowed from the helper texture, the
    /// piece's stream buffer bound as the target after it.</summary>
    private static string SkinBlock(string sfx, int k) =>
        PassBlock($"CustomShaderPoseSkin_{sfx}_p{k}", $"pose_skin_{sfx}.hlsl",
            new[] { (0, $"Resource_PoseTex_{sfx}"), (1, $"Resource_PieceMap_{sfx}_p{k}"), (5, $"Resource_NewBind_{sfx}"), (6, $"Resource_NewSkin_{sfx}") },
            $"o0 = set_viewport Resource_PoseView_{sfx}\no0 = Resource_PoseRT_{sfx}_p{k}\n");

    /// <summary>A pass inside a block, line for line: its pixel shader, its binds, its targets, the draw. The
    /// state, the slot saves and the target clears are the block's.</summary>
    private static string PassBlock(string section, string ps, IReadOnlyList<(int Slot, string Res)> slots, string targets) =>
        $"[{section}]\nps = {ps}\n"
        + string.Concat(slots.Select(s => $"ps-t{s.Slot} = {s.Res}\n"))
        + targets
        + "draw = 3, 0\n"
        + "\n";

    private static void AssertPacketFiles(string outDir, string mesh, (uint[] Index, uint[] Lookup, uint[]? Sel) want)
    {
        Assert.Equal(Bytes(want.Index), File.ReadAllBytes(Path.Combine(outDir, $"packet_index_{mesh}.buf")));
        Assert.Equal(Bytes(want.Lookup), File.ReadAllBytes(Path.Combine(outDir, $"packet_lookup_{mesh}.buf")));
        string selFile = Path.Combine(outDir, $"packet_sel_{mesh}.buf");
        if (want.Sel is null) Assert.False(File.Exists(selFile));
        else Assert.Equal(Bytes(want.Sel), File.ReadAllBytes(selFile));
    }

    /// <summary>The donor's draw ranges as the emitter read them: the shipped index buffer's indices per
    /// submesh, with each range's base vertex folded in.</summary>
    private static List<uint[]> DonorRanges(string outDir, string sfx)
    {
        var ib = File.ReadAllBytes(Path.Combine(outDir, $"combined_ib_{sfx}.buf"));
        // the synthetic donor ships 16-bit indices; the fixture's submeshes are consecutive runs of the
        // whole buffer at base 0, which the piece files must reproduce when concatenated
        int total = ib.Length / 2;
        var all = new uint[total];
        for (int i = 0; i < total; i++) all[i] = BitConverter.ToUInt16(ib, i * 2);
        return new List<uint[]> { all };
    }

    [Fact]
    public void A_single_part_replace_runs_the_gather_and_the_pose_passes_at_every_draw_and_draws_each_piece_directly()
    {
        var req = SinglePart(out string outDir);
        new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);
        HlslCheck.EveryShaderCompilesClean(ini, outDir);
        int packet = ExpectedPacket(outDir, "alpha", 32).Index.Length;
        bool slim = SyntheticPool.ShippedSel(outDir, "alpha") is not null;

        // the capture section: the draw's pose block, then the draw, and no frame flag anywhere; the block
        // holds the gather, the palette pass and one skin pass per piece (one piece per donor range here)
        string cap = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.Contains("run = CustomShaderPoseBlock_alpha_swap\nrun = CommandListDraw_swap\n", cap);
        Assert.Equal("run = CustomShaderGather_alpha\nrun = CustomShaderPosePalette_alpha_swap\n"
                      + "run = CustomShaderPoseSkin_swap_p0\nrun = CustomShaderPoseSkin_swap_p1\n",
            PoseBlock(ini, "alpha_swap", slots: slim ? SlimSlots : DenseSlots));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[CustomShaderPoseBlock_alpha_swap]")));
        Assert.DoesNotContain("zz_done_swap", ini);
        Assert.DoesNotContain("\n[Present]\n", ini);
        Assert.DoesNotContain("copy vs-cb1", ini);

        // the passes, in full; no compute section, no unordered-access view and no indirect draw anywhere
        Assert.Contains(GatherBlock("alpha", packet), ini);
        Assert.Contains(PaletteBlock("alpha", "swap", slim, "Resource_alpha_Cpinv"), ini);
        Assert.Contains(SkinBlock("swap", 0), ini);
        Assert.Contains(SkinBlock("swap", 1), ini);
        Assert.DoesNotContain("cs = ", ini);
        Assert.DoesNotContain("cs-u", ini);
        Assert.DoesNotContain("unordered_access", ini);
        Assert.DoesNotContain("drawindexedinstancedindirect", ini);
        Assert.DoesNotContain("CustomShaderCache_", ini);
        foreach (int s in new[] { 0, 1, 2, 3, 4, 5, 6 }) Assert.Contains($"\n[Resource_SavePST{s}]\n", ini);
        Assert.Contains("\n[Resource_SaveVST1]\n", ini);

        // the chain's sections and resources are gone with it, as is the old kernel's
        foreach (string absent in new[] { "[CustomShaderRecover_alpha_swap]", "[CustomShaderSkin_swap]",
                     "[CustomShaderConvertW_swap]", "[Resource_Palette_swap]", "[Resource_NewPosed_swap]",
                     "[Resource_Cache_swap]", "[Resource_CacheOut_swap]", "[Resource_CacheVB1_swap]", "[Resource_NewVB1_swap]" })
            Assert.DoesNotContain(absent, ini);
        Assert.False(File.Exists(Path.Combine(outDir, "skin_cs_swap.hlsl")));
        Assert.False(File.Exists(Path.Combine(outDir, "palette_seed_swap.buf")));
        Assert.False(File.Exists(Path.Combine(outDir, "convert_witness_swap.hlsl")));
        Assert.False(File.Exists(Path.Combine(outDir, "cache_alpha_swap.hlsl")));

        // the resources: the bind geometry and weights in the shapes the skin pass reads, the palette and
        // viewport textures, and per piece the typed stream buffer, its stride-40 alias, its index buffer,
        // its map and its UV rows
        Assert.Contains("[Resource_NewBind_swap]\ntype = StructuredBuffer\nstride = 40\nfilename = combined_bind_swap.buf\n", ini);
        Assert.Contains("[Resource_NewSkin_swap]\ntype = StructuredBuffer\nstride = 32\nfilename = combined_skin_swap.buf\n", ini);
        Assert.Contains("[Resource_PoseTex_swap]\ntype = Texture2D\nformat = R32G32B32A32_FLOAT\nwidth = 8\nheight = 1\n"
                      + "array = 1\nmips = 1\nmsaa = 1\nbind_flags = shader_resource render_target\n", ini);
        var ranges = DonorRanges(outDir, "swap");
        int verts0 = ReadUInts(Path.Combine(outDir, "piece0_map_swap.buf")).Length;
        int verts1 = ReadUInts(Path.Combine(outDir, "piece1_map_swap.buf")).Length;
        int elements = Math.Max((verts0 * 40 + 15) / 16, (verts1 * 40 + 15) / 16);
        Assert.Contains($"[Resource_PoseView_swap]\ntype = Texture2D\nformat = R32_FLOAT\nwidth = {elements}\nheight = 1\n"
                      + "array = 1\nmips = 1\nmsaa = 1\nbind_flags = render_target\n", ini);
        Assert.Contains($"[Resource_PoseRT_swap_p0]\ntype = Buffer\nformat = R32G32B32A32_FLOAT\narray = {(verts0 * 40 + 15) / 16}\n"
                      + "bind_flags = render_target vertex_buffer\n[Resource_PoseVB_swap_p0]\nstride = 40\n"
                      + "[Resource_PieceIB_swap_p0]\ntype = Buffer\nformat = DXGI_FORMAT_R16_UINT\nfilename = piece0_ib_swap.buf\n"
                      + "[Resource_PieceMap_swap_p0]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = piece0_map_swap.buf\n"
                      + "[Resource_PieceVB1_swap_p0]\ntype = Buffer\nstride = 20\nbind_flags = vertex_buffer\nfilename = piece0_vb1_swap.buf\n", ini);

        // the draw list points each alias at its stream buffer, then binds and draws each range's piece
        string list = Section(ini, "[CommandListDraw_swap]");
        Assert.Contains("Resource_PoseVB_swap_p0 = ref Resource_PoseRT_swap_p0\nResource_PoseVB_swap_p1 = ref Resource_PoseRT_swap_p1\n", list);
        int indices0 = ReadUShorts(Path.Combine(outDir, "piece0_ib_swap.buf"), 1).Length;   // existence; counts below
        string draw0 = Regex.Match(list, @"vb0 = Resource_PoseVB_swap_p0\nvb1 = Resource_PieceVB1_swap_p0\nvb3 = Resource_PoseVB_swap_p0\nib = Resource_PieceIB_swap_p0\ndrawindexed = (\d+), 0, 0\n").Groups[1].Value;
        string draw1 = Regex.Match(list, @"vb0 = Resource_PoseVB_swap_p1\nvb1 = Resource_PieceVB1_swap_p1\nvb3 = Resource_PoseVB_swap_p1\nib = Resource_PieceIB_swap_p1\ndrawindexed = (\d+), 0, 0\n").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(draw0) || string.IsNullOrEmpty(draw1), list);
        Assert.Equal(ranges[0].Length, int.Parse(draw0) + int.Parse(draw1));
        Assert.DoesNotContain("Resource_NewIB_swap", list);
        Assert.Contains("vb0 = Resource_SaveVB0\nvb1 = Resource_SaveVB1\nvb3 = Resource_SaveVB3\nib = Resource_SaveIB\n", list);

        // the piece files draw the donor's triangles in the donor's order: the two pieces' indices, each
        // through its map, concatenate to the shipped index buffer
        var rebuilt = new List<uint>();
        foreach (int k in new[] { 0, 1 })
        {
            uint[] map = ReadUInts(Path.Combine(outDir, $"piece{k}_map_swap.buf"));
            int count = int.Parse(k == 0 ? draw0 : draw1);
            ushort[] local = ReadUShorts(Path.Combine(outDir, $"piece{k}_ib_swap.buf"), count);
            Assert.All(local, l => Assert.InRange((int)l, 0, map.Length - 1));
            rebuilt.AddRange(local.Select(l => map[l]));
            // the UV rows are the donor's rows for the mapped vertices
            var vb1 = File.ReadAllBytes(Path.Combine(outDir, "combined_vb1_swap.buf"));
            var rows = File.ReadAllBytes(Path.Combine(outDir, $"piece{k}_vb1_swap.buf"));
            Assert.Equal(map.Length * 20, rows.Length);
            for (int i = 0; i < map.Length; i++)
                Assert.Equal(vb1.Skip((int)map[i] * 20).Take(20), rows.Skip(i * 20).Take(20));
        }
        Assert.Equal(ranges[0], rebuilt);
    }

    [Fact]
    public void The_palette_and_skin_shaders_are_stamped_from_the_build_and_compile_clean()
    {
        var req = SinglePart(out string outDir);
        new MigotoEmitter().Build(req);
        string palette = File.ReadAllText(Path.Combine(outDir, "pose_palette_alpha_swap.hlsl"));
        string skin = File.ReadAllText(Path.Combine(outDir, "pose_skin_swap.hlsl"));
        string vs = File.ReadAllText(Path.Combine(outDir, "pose_fullscreen.hlsl"));

        Assert.Contains("static const uint ROWS=8;", palette);
        Assert.Contains("float3 Q(uint i){ return q.Load(int3(i % 64, i / 64, 0)).xyz; }", palette);
        Assert.Contains("precise float3 a=float3(0,0,0), correction=float3(0,0,0);", palette);   // the compensated recover
        Assert.Contains("if (Map[b] == u) return Row(b, comp);", palette);
        Assert.DoesNotContain("PAIR", palette);
        Assert.Contains("static const uint VCOUNT=8;", skin);
        Assert.Contains("SkinVertex(min(map[v0], VCOUNT - 1), a);", skin);
        Assert.DoesNotContain("\r", palette);
        Assert.DoesNotContain("\r", skin);
        HlslCheck.CompilesClean(palette, "pose_palette_alpha_swap.hlsl", "ps_5_0");
        HlslCheck.CompilesClean(skin, "pose_skin_swap.hlsl", "ps_5_0");
        HlslCheck.CompilesClean(vs, "pose_fullscreen.hlsl", "vs_5_0");
    }

    [Fact]
    public void A_donor_past_the_stream_window_is_drawn_in_pieces_that_reproduce_its_triangles()
    {
        var req = SinglePart(out string outDir);
        // the window forced down to four vertices: the eight-vertex donor's ranges cut into several pieces
        var result = new MigotoEmitter { PoseWindowVertices = 4 }.Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);
        HlslCheck.EveryShaderCompilesClean(ini, outDir);

        var pieceSections = Regex.Matches(ini, @"\[CustomShaderPoseSkin_swap_p(\d+)\]").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.True(pieceSections.Count > 2, $"{pieceSections.Count} pieces");
        Assert.Equal(Enumerable.Range(0, pieceSections.Count), pieceSections);
        // the capture's block runs every skin pass, in piece order, before the draw
        string cap = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.Contains("run = CustomShaderPoseBlock_alpha_swap\nrun = CommandListDraw_swap\n", cap);
        Assert.EndsWith(string.Concat(pieceSections.Select(k => $"run = CustomShaderPoseSkin_swap_p{k}\n")), PoseBlock(ini, "alpha_swap"));
        // every piece holds at most the window, and the pieces drawn in order rebuild the donor's indices
        var rebuilt = new List<uint>();
        string list = Section(ini, "[CommandListDraw_swap]");
        foreach (int k in pieceSections)
        {
            uint[] map = ReadUInts(Path.Combine(outDir, $"piece{k}_map_swap.buf"));
            Assert.InRange(map.Length, 3, 4);
            int count = int.Parse(Regex.Match(list, $@"ib = Resource_PieceIB_swap_p{k}\ndrawindexed = (\d+), 0, 0\n").Groups[1].Value);
            Assert.Equal(0, count % 3);
            rebuilt.AddRange(ReadUShorts(Path.Combine(outDir, $"piece{k}_ib_swap.buf"), count).Select(l => map[l]));
        }
        Assert.Equal(DonorRanges(outDir, "swap")[0], rebuilt);
        Assert.Contains(result.Diagnostics, d => d.StartsWith("swap: the replacement is drawn in ", StringComparison.Ordinal));
        // the per-range lists, where a routed capture site exists, draw only that range's pieces: the full
        // list draws them all
        Assert.Equal(pieceSections.Count, Regex.Matches(list, "drawindexed = ").Count);
    }

    [Fact]
    public void An_anchor_tier_gets_its_own_palette_pass_and_shares_the_skin_passes()
    {
        string td = Path.Combine(_root, "alpha_l1"); SyntheticPool.WritePartDump(td, 3, 24, new[] { A, B });
        GenericPositions(td, 24);
        var req = SinglePart(out string outDir, new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002"));
        new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);
        HlslCheck.EveryShaderCompilesClean(ini, outDir);

        // the level gathers its own packet at its own draw, recovers its own palette and skins the pieces
        string tier = Section(ini, "[TextureOverride_Cap_alpha_lod1]");
        Assert.Contains("run = CustomShaderPoseBlock_alpha_lod1_swap\nrun = CommandListDraw_swap\n", tier);
        Assert.Equal("run = CustomShaderGather_alpha_lod1\nrun = CustomShaderPosePalette_alpha_lod1_swap\n"
                      + "run = CustomShaderPoseSkin_swap_p0\nrun = CustomShaderPoseSkin_swap_p1\n", PoseBlock(ini, "alpha_lod1_swap"));
        Assert.DoesNotContain("CustomShaderGather_alpha\n", PoseBlock(ini, "alpha_lod1_swap"));
        Assert.DoesNotContain("CustomShaderGatherRef_", ini);
        Assert.DoesNotContain("zz_done_swap", ini);

        var want0 = ExpectedPacket(outDir, "alpha", 32);
        var want1 = ExpectedPacket(outDir, "alpha_lod1", 24);
        Assert.Contains(GatherBlock("alpha", want0.Index.Length), ini);
        Assert.Contains(GatherBlock("alpha_lod1", want1.Index.Length), ini);
        AssertPacketFiles(outDir, "alpha", want0);
        AssertPacketFiles(outDir, "alpha_lod1", want1);
        bool slim1 = want1.Sel is not null;
        Assert.Contains(PaletteBlock("alpha_lod1", "swap", slim1, "Resource_alpha_lod1_Cpinv"), ini);
        // one palette texture and one set of pieces serve both levels
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Resource_PoseTex_swap]")));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[CustomShaderPoseSkin_swap_p0]")));
        string lod1 = File.ReadAllText(Path.Combine(outDir, "pose_palette_alpha_lod1_swap.hlsl"));
        Assert.Contains("ROWS=8;", lod1);
        HlslCheck.CompilesClean(lod1, "pose_palette_alpha_lod1_swap.hlsl", "ps_5_0");
        HlslCheck.CompilesClean(File.ReadAllText(Path.Combine(outDir, "gather_alpha_lod1.hlsl")), "gather_alpha_lod1.hlsl", "vs_5_0");
    }

    [Fact]
    public void A_pool_reaching_another_part_keeps_the_frame_chain_and_says_why()
    {
        var req = TwoParts(out string outDir, shareBone: true);
        var result = new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains("if $zz_done_swap == 0\n", ini);
        Assert.Contains("[CustomShaderSkin_swap]", ini);
        Assert.DoesNotContain("[CustomShaderPose", ini);
        Assert.DoesNotContain("piece0_", ini);
        Assert.Contains("drawindexed = ", ini);
        Assert.Contains(result.Diagnostics, d => d.Contains("share one pose") && d.Contains("come from other parts"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains(" - "));
    }

    [Fact]
    public void A_witness_converted_pool_copies_no_draw_constants()
    {
        var req = TwoParts(out string outDir, shareBone: true);
        new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains("run = CustomShaderConvertW_swap", ini);
        Assert.DoesNotContain("copy vs-cb1", ini);
    }

    [Fact]
    public void A_constants_converted_pool_still_copies_them_at_every_part()
    {
        var req = TwoParts(out string outDir, shareBone: false);
        new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains("run = CustomShaderConvert_swap", ini);
        Assert.Contains("Resource_alpha_CB = copy vs-cb1\n", ini);
        Assert.Contains("Resource_beta_CB = copy vs-cb1\n", ini);
    }

    [Fact]
    public void The_palette_template_compiles_in_its_dense_slim_and_tied_shapes()
    {
        string dense = ComputeTemplates.EmitPosePalette(rows: 40, slim: false, n: 3000, pairs: new[] { (7u, 3u), (9u, 3u) });
        Assert.Contains("PAIRS=2;", dense);
        Assert.Contains("static const uint2 PAIR[2] = { uint2(7,3), uint2(9,3) };", dense);
        Assert.Contains("    if (PAIR[0].x == u) u = PAIR[0].y;\n    if (PAIR[1].x == u) u = PAIR[1].y;\n", dense);
        Assert.Contains("[loop] for (uint b = 0; b < ROWS / 4; b++) {", dense);
        Assert.Contains("static const uint N=3000;", dense);
        HlslCheck.CompilesClean(dense, "dense.hlsl", "ps_5_0");
        string slim = ComputeTemplates.EmitPosePalette(rows: 12, slim: true, n: 100, pairs: Array.Empty<(uint, uint)>());
        Assert.Contains("Buffer<uint>          Sel    : register(t3);", slim);
        Assert.DoesNotContain("PAIR", slim);
        HlslCheck.CompilesClean(slim, "slim.hlsl", "ps_5_0");
        HlslCheck.CompilesClean(ComputeTemplates.EmitPoseSkin(4448), "skin.hlsl", "ps_5_0");
        HlslCheck.CompilesClean(ComputeTemplates.EmitPoseFullscreen(), "fullscreen.hlsl", "vs_5_0");
    }

    [Fact]
    public void A_weak_untied_tier_bone_is_tied_to_a_sound_bone_in_the_tier_palette()
    {
        // the tier rigs B but weights nothing to it: too weak to recover, and no co-riding bone to tie to
        string td = Path.Combine(_root, "alpha_l1"); SyntheticPool.WritePartDump(td, 3, 24, new[] { A, B }, weightedBones: 1);
        GenericPositions(td, 24);
        var req = SinglePart(out string outDir, new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002"));
        var result = new MigotoEmitter().Build(req);

        // the tier palette recovers A and fills B from A, so B's vertices draw articulated, not in bind pose
        string lod1 = File.ReadAllText(Path.Combine(outDir, "pose_palette_alpha_lod1_swap.hlsl"));
        Assert.Contains("PAIRS=1;", lod1);
        Assert.Contains("static const uint2 PAIR[1] = { uint2(1,0) };", lod1);
        HlslCheck.CompilesClean(lod1, "pose_palette_alpha_lod1_swap.hlsl", "ps_5_0");
        Assert.Contains(result.Diagnostics, d => d == "alpha_lod1: bone 0x00000066 has too little support in this lower-detail mesh to recover");
        Assert.Contains(result.Diagnostics, d => d.Contains("alpha_lod1: bone 0x00000066 has no row at this tier"));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("lod0 recovery is reused"));
    }

    [Fact]
    public void A_tier_supplying_no_bones_gathers_the_lod0_packet_from_the_lod0_capture_and_runs_the_lod0_palette()
    {
        // every vertex of the lower-detail mesh sits at one point, so neither bone has the support to recover
        // and, with no co-weight, neither can follow the other
        string td = Path.Combine(_root, "alpha_l1"); SyntheticPool.WritePartDump(td, 3, 24, new[] { A, B });
        var s0 = File.ReadAllBytes(Path.Combine(td, "stream0.buf"));
        for (int v = 0; v < 24; v++)
            for (int c = 0; c < 3; c++) BitConverter.GetBytes(0.5f).CopyTo(s0, v * 40 + c * 4);
        File.WriteAllBytes(Path.Combine(td, "stream0.buf"), s0);
        var req = SinglePart(out string outDir, new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002"));
        var result = new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);
        HlslCheck.EveryShaderCompilesClean(ini, outDir);
        string tier = Section(ini, "[TextureOverride_Cap_alpha_lod1]");
        Assert.True(tier.Contains("run = CustomShaderPoseBlock_alpha_lod1_swap\nrun = CommandListDraw_swap\n"),
            tier + "\n" + string.Join("\n", result.Diagnostics));
        // the block saves and puts back the first vertex buffer too, which its gather rebinds to the reference
        string block = PoseBlock(ini, "alpha_lod1_swap", gatherRef: true);
        Assert.Equal("run = CustomShaderGatherRef_alpha\nrun = CustomShaderPosePalette_alpha_swap\n"
                      + "run = CustomShaderPoseSkin_swap_p0\nrun = CustomShaderPoseSkin_swap_p1\n", block);
        Assert.DoesNotContain("CustomShaderGather_", block);
        Assert.Contains("Resource_alpha_Posed = ref vb0\n", Section(ini, "[TextureOverride_Cap_alpha]"));
        Assert.Contains(GatherRefBlock("alpha", ExpectedPacket(outDir, "alpha", 32).Index.Length), ini);
        Assert.DoesNotContain("[CustomShaderGather_alpha_lod1]", ini);
        Assert.False(File.Exists(Path.Combine(outDir, "gather_alpha_lod1.hlsl")));
        Assert.False(File.Exists(Path.Combine(outDir, "packet_index_alpha_lod1.buf")));
        Assert.False(File.Exists(Path.Combine(outDir, "pose_palette_alpha_lod1_swap.hlsl")));
        Assert.Contains(result.Diagnostics, d => d == "alpha_lod1: this lower-detail mesh supplies no bones of its own, "
            + "so copies of the replaced part share one pose at its draws");
    }

    [Fact]
    public void Recover_shaders_ship_only_where_a_chain_reads_them()
    {
        // on the route alone: the palette pass reads the operator files, and the recover shader is read by nothing
        new MigotoEmitter().Build(SinglePart(out string routed));
        Assert.False(File.Exists(Path.Combine(routed, "recover_alpha_cs.hlsl")));
        Assert.True(File.Exists(Path.Combine(routed, "alpha_cpinv.buf")));

        string ad = Path.Combine(_root, "alpha2"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        string bd = Path.Combine(_root, "beta2"); SyntheticPool.WritePartDump(bd, 2, 16, new[] { B, C });
        GenericPositions(ad, 32); GenericPositions(bd, 16);
        string donor = Path.Combine(_root, "donor2"); SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 2, submeshes: 2);
        string mixed = Path.Combine(_root, "out_mixed");
        // alpha is on the route in its own pipeline AND pooled under beta's chain in another: the shader stays
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = mixed,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "own", Parts = new[] { new PoolPart("alpha", ad) }, Anchor = "alpha", DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                },
                new ReplacePipeline
                {
                    Suffix = "pool", Parts = new[] { new PoolPart("beta", bd), new PoolPart("alpha", ad) }, Anchor = "beta",
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                },
            },
        });
        string ini = File.ReadAllText(Path.Combine(mixed, "mod.ini"));
        Assert.Contains("[CustomShaderPosePalette_alpha_own]", ini);
        Assert.Contains("[CustomShaderRecover_alpha_pool]\ncs = recover_alpha_cs.hlsl\n", ini);
        // the chain reading alpha keeps its copy of the posed buffer; only the route reads the packet
        Assert.Contains("cs-t0 = copy Resource_alpha_Posed\n", Section(ini, "[CustomShaderRecover_alpha_pool]"));
        Assert.Contains("ps-t0 = Resource_Packet_alpha\n", Section(ini, "[CustomShaderPosePalette_alpha_own]"));
        Assert.True(File.Exists(Path.Combine(mixed, "recover_alpha_cs.hlsl")));
        Assert.True(File.Exists(Path.Combine(mixed, "recover_beta_cs.hlsl")));
        Assert.True(File.Exists(Path.Combine(mixed, "packet_sel_alpha.buf")), "the fixture is meant to ship alpha slim");
        Assert.Contains("cs-t3 = Resource_alpha_Sel\n", Section(ini, "[CustomShaderRecover_alpha_pool]"));
        Assert.Contains("[Resource_alpha_Sel]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = alpha_sel.buf\n", ini);
        Assert.True(File.Exists(Path.Combine(mixed, "alpha_sel.buf")));
        ModBuilderTests.AssertEveryReferencedFileShips(ini, mixed);
        HlslCheck.EveryShaderCompilesClean(ini, mixed);
    }

    [Fact]
    public void A_vertex_list_ships_only_where_a_chain_binds_it()
    {
        // on the route alone: the palette pass reads the list through its packet and binds the offsets
        new MigotoEmitter().Build(OneBone("slim", verts: 64, out string routed));
        string ini = File.ReadAllText(Path.Combine(routed, "mod.ini"));
        Assert.True(File.Exists(Path.Combine(routed, "packet_sel_slim.buf")), "the fixture is meant to ship slim");
        Assert.False(File.Exists(Path.Combine(routed, "slim_sel.buf")));
        Assert.DoesNotContain("_Sel]", ini);
        Assert.DoesNotContain("Resource_slim_Sel", ini);
        Assert.True(File.Exists(Path.Combine(routed, "slim_off.buf")));
        Assert.Contains("[Resource_slim_Off]\n", ini);
        Assert.Contains("ps-t4 = Resource_slim_Off\n", Section(ini, "[CustomShaderPosePalette_slim_swap]"));
        ModBuilderTests.AssertEveryReferencedFileShips(ini, routed);
        HlslCheck.EveryShaderCompilesClean(ini, routed);

        // two parts under one chain: each recover binds its part's list
        new MigotoEmitter().Build(TwoParts(out string chain, shareBone: true));
        string chainIni = File.ReadAllText(Path.Combine(chain, "mod.ini"));
        foreach (string part in new[] { "alpha", "beta" })
        {
            Assert.True(File.Exists(Path.Combine(chain, $"{part}_sel.buf")), $"the fixture is meant to ship {part} slim");
            Assert.Contains($"cs-t3 = Resource_{part}_Sel\n", Section(chainIni, $"[CustomShaderRecover_{part}_swap]"));
            Assert.Contains($"[Resource_{part}_Sel]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = {part}_sel.buf\n", chainIni);
        }
        ModBuilderTests.AssertEveryReferencedFileShips(chainIni, chain);
        HlslCheck.EveryShaderCompilesClean(chainIni, chain);
    }

    [Fact]
    public void The_anchor_packet_is_the_distinct_vertices_the_operator_reads_in_ascending_order()
    {
        uint[] sel = { 7, 2, 7, 5, 2, 9 };
        var (index, lookup, packetSel) = ComputeTemplates.AnchorPacket(sel, n: 12);

        Assert.Equal(new uint[] { 2, 5, 7, 9 }, index);
        Assert.Equal(new uint[] { 0, 0, 0, 0, 0, 1, 0, 2, 0, 3, 0, 0 }, lookup);
        Assert.Equal(new uint[] { 2, 0, 2, 1, 0, 3 }, packetSel);

        var (dIndex, dLookup, dSel) = ComputeTemplates.AnchorPacket(null, n: 5);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, dIndex);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, dLookup);
        Assert.Null(dSel);

        Assert.Throws<ArgumentOutOfRangeException>(() => ComputeTemplates.AnchorPacket(new uint[] { 3, 12 }, n: 12));
        Assert.Throws<ArgumentException>(() => ComputeTemplates.AnchorPacket(Array.Empty<uint>(), n: 12));
    }

    [Fact]
    public void A_packet_texture_is_64_entries_wide_and_as_tall_as_its_entries_need()
    {
        Assert.Equal(1, ComputeTemplates.PacketHeight(1));
        Assert.Equal(1, ComputeTemplates.PacketHeight(64));
        Assert.Equal(2, ComputeTemplates.PacketHeight(65));
        Assert.Equal(3, ComputeTemplates.PacketHeight(130));
        string vs = ComputeTemplates.EmitGather(130);
        Assert.Contains("uint x = i % 64, y = i / 64;", vs);
        Assert.Contains("o.pos = float4((x + 0.5) / 64.0 * 2 - 1, 1 - (y + 0.5) / 3.0 * 2, 0.5, 1); o.p = v.pos;", vs);
        HlslCheck.CompilesClean(vs, "gather.hlsl", "vs_5_0");
        HlslCheck.CompilesClean(ComputeTemplates.EmitGatherPixel(), "gather_ps.hlsl", "ps_5_0");
    }

    [Fact]
    public void A_slim_part_gathers_the_vertices_its_operator_reads_and_the_palette_pass_reads_only_that_packet()
    {
        var req = OneBone("slim", verts: 64, out string outDir);
        new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);
        HlslCheck.EveryShaderCompilesClean(ini, outDir);
        Assert.True(File.Exists(Path.Combine(outDir, "packet_sel_slim.buf")), "the fixture is meant to ship slim");
        var want = ExpectedPacket(outDir, "slim", 64);
        int p = want.Index.Length;
        Assert.True(p < 64, $"the slim fixture reads {p} of 64 vertices");

        Assert.Contains(GatherBlock("slim", p), ini);
        Assert.Contains("run = CustomShaderPoseBlock_slim_swap\n", Section(ini, "[TextureOverride_Cap_slim]"));
        Assert.StartsWith("run = CustomShaderGather_slim\nrun = CustomShaderPosePalette_slim_swap\n", PoseBlock(ini, "slim_swap"));
        AssertPacketFiles(outDir, "slim", want);

        string pass = Section(ini, "[CustomShaderPosePalette_slim_swap]");
        Assert.Contains("ps-t0 = Resource_Packet_slim\n", pass);
        Assert.Contains("ps-t3 = Resource_slim_PacketSel\nps-t4 = Resource_slim_Off\n", pass);
        Assert.DoesNotContain("copy", pass);
        Assert.Contains("[Resource_slim_PacketSel]\ntype = Buffer\nformat = DXGI_FORMAT_R32_UINT\nfilename = packet_sel_slim.buf\n", ini);

        string hlsl = File.ReadAllText(Path.Combine(outDir, "pose_palette_slim_swap.hlsl"));
        Assert.Contains("float3 Q(uint i){ return q.Load(int3(i % 64, i / 64, 0)).xyz; }", hlsl);
        Assert.Contains("#define POS(i) Q(i)", hlsl);
        Assert.Contains("precise float3 term=Cpinv[cbase+t]*POS(Sel[sbase+t]);", hlsl);
        HlslCheck.CompilesClean(hlsl, "pose_palette_slim_swap.hlsl", "ps_5_0");
        string gather = File.ReadAllText(Path.Combine(outDir, "gather_slim.hlsl"));
        Assert.Contains($"1 - (y + 0.5) / {(p + 63) / 64}.0 * 2", gather);
        HlslCheck.CompilesClean(gather, "gather_slim.hlsl", "vs_5_0");
    }

    [Fact]
    public void A_dense_part_gathers_every_vertex_and_ships_no_remapped_list()
    {
        var req = OneBone("dense", verts: 32, out string outDir);
        new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);
        HlslCheck.EveryShaderCompilesClean(ini, outDir);
        Assert.Null(SyntheticPool.ShippedSel(outDir, "dense"));   // the fixture is meant to ship dense

        var identity = Enumerable.Range(0, 32).Select(v => (uint)v).ToArray();
        AssertPacketFiles(outDir, "dense", (identity, identity, null));
        Assert.Contains(GatherBlock("dense", 32), ini);
        Assert.DoesNotContain("PacketSel", ini);
        string pass = Section(ini, "[CustomShaderPosePalette_dense_swap]");
        Assert.Contains("ps-t0 = Resource_Packet_dense\n", pass);
        Assert.DoesNotContain("ps-t3", pass);

        string hlsl = File.ReadAllText(Path.Combine(outDir, "pose_palette_dense_swap.hlsl"));
        Assert.Contains("static const uint N=32;", hlsl);
        Assert.Contains("p[k0]=POS((v+k0));", hlsl);
        HlslCheck.CompilesClean(hlsl, "pose_palette_dense_swap.hlsl", "ps_5_0");
    }

    [Fact]
    public void Toggle_states_on_one_anchor_share_its_gather_and_each_runs_its_own_passes_in_its_own_gate()
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        GenericPositions(ad, 32);
        var pipelines = new List<ReplacePipeline>();
        for (int state = 0; state < 2; state++)
        {
            string donor = Path.Combine(_root, $"donor{state}");
            SyntheticPool.WriteDonor(donor, verts: 8 + state, unionBones: 2, submeshes: 2);
            pipelines.Add(new ReplacePipeline
            {
                Suffix = $"swap_s{state}",
                Parts = new[] { new PoolPart("alpha", ad) },
                Anchor = "alpha",
                DonorDir = donor,
                CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                ToggleKey = new KeyRef("F7", state),
            });
        }
        string outDir = Path.Combine(_root, "out-states");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = pipelines,
            KeyCycles = new[] { new KeyCycle("F7", 2, 0) },
        });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);
        HlslCheck.EveryShaderCompilesClean(ini, outDir);

        Assert.Single(Regex.Matches(ini, @"\[CustomShaderGather_alpha\]"));
        Assert.Single(Regex.Matches(ini, Regex.Escape("[Resource_SavePST0]")));
        string cap = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.Contains("run = CustomShaderPoseBlock_alpha_swap_s0\n", cap);
        Assert.Contains("run = CustomShaderPoseBlock_alpha_swap_s1\n", cap);
        Assert.StartsWith("run = CustomShaderGather_alpha\nrun = CustomShaderPosePalette_alpha_swap_s0\nrun = CustomShaderPoseSkin_swap_s0_p0\n", PoseBlock(ini, "alpha_swap_s0"));
        Assert.StartsWith("run = CustomShaderGather_alpha\nrun = CustomShaderPosePalette_alpha_swap_s1\nrun = CustomShaderPoseSkin_swap_s1_p0\n", PoseBlock(ini, "alpha_swap_s1"));
    }

    /// <summary>A replaced mesh of two vanilla submeshes: the draw routes per submesh shape.</summary>
    private static readonly DrawShapeSet TwoShapes =
        new(new[] { new DrawShape(0, 60), new DrawShape(60, 84) }, 144);

    // the donor's two ranges are two pieces: piece k belongs to range k, and range k folds onto submesh k
    private const string Lod0Prelude = "run = CustomShaderGather_alpha\nrun = CustomShaderPosePalette_alpha_swap\n";
    private const string Skin0 = "run = CustomShaderPoseSkin_swap_p0\n", Skin1 = "run = CustomShaderPoseSkin_swap_p1\n";

    [Fact]
    public void Where_the_draw_routes_per_submesh_the_passes_run_in_each_routed_section_and_nowhere_else()
    {
        var req = RoutedSinglePart(out string outDir);
        new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);

        // the capture section fires at every draw of the mesh, those drawing nothing of the replacement
        // included: it keeps the posed-stream reference and the skip, and runs nothing — not even an empty gate
        string cap = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.Contains("Resource_alpha_Posed = ref vb0\n", cap);
        Assert.Contains("handling = skip\n", cap);
        Assert.DoesNotContain("run = ", cap);
        Assert.DoesNotContain("endif", cap);

        // each routed section runs its own block, which runs the gather and the palette pass once, then skins
        // only the pieces of the ranges the section draws, ahead of its lists; the whole-mesh section draws
        // every range and skins every piece
        Assert.Contains("match_first_index = 0\nmatch_index_count = 60\nrun = CustomShaderPoseBlock_alpha_DrawS0_swap\nrun = CommandListDrawS0_swap\n",
            Section(ini, "[TextureOverride_Cap_alpha_DrawS0]"));
        Assert.Contains("match_first_index = 60\nmatch_index_count = 84\nrun = CustomShaderPoseBlock_alpha_DrawS1_swap\nrun = CommandListDrawS1_swap\n",
            Section(ini, "[TextureOverride_Cap_alpha_DrawS1]"));
        Assert.Contains("match_first_index = 0\nmatch_index_count = 144\nrun = CustomShaderPoseBlock_alpha_DrawFull_swap\nrun = CommandListDraw_swap\n",
            Section(ini, "[TextureOverride_Cap_alpha_DrawFull]"));
        Assert.Equal(Lod0Prelude + Skin0, PoseBlock(ini, "alpha_DrawS0_swap"));
        Assert.Equal(Lod0Prelude + Skin1, PoseBlock(ini, "alpha_DrawS1_swap"));
        Assert.Equal(Lod0Prelude + Skin0 + Skin1, PoseBlock(ini, "alpha_DrawFull_swap"));
        Assert.Equal(3, Regex.Matches(ini, Regex.Escape("run = CustomShaderPosePalette_alpha_swap\n")).Count);
        Assert.Equal(3, Regex.Matches(ini, Regex.Escape("run = CustomShaderGather_alpha\n")).Count);
    }

    [Fact]
    public void A_routed_anchor_tier_runs_its_own_gather_and_palette_pass_in_its_routed_sections()
    {
        string td = Path.Combine(_root, "alpha_l1"); SyntheticPool.WritePartDump(td, 3, 24, new[] { A, B });
        GenericPositions(td, 24);
        var tierShapes = new DrawShapeSet(new[] { new DrawShape(0, 30), new DrawShape(30, 42) }, 72);
        var req = RoutedSinglePart(out string outDir, new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002", Shapes: tierShapes));
        new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);

        const string tierPrelude = "run = CustomShaderGather_alpha_lod1\nrun = CustomShaderPosePalette_alpha_lod1_swap\n";
        string cap = Section(ini, "[TextureOverride_Cap_alpha_lod1]");
        Assert.Contains("Resource_alpha_lod1_Posed = ref vb0\n", cap);
        Assert.DoesNotContain("run = ", cap);
        Assert.Contains("match_first_index = 0\nmatch_index_count = 30\nrun = CustomShaderPoseBlock_alpha_lod1_DrawS0_swap\nrun = CommandListDrawS0_swap\n",
            Section(ini, "[TextureOverride_Cap_alpha_lod1_DrawS0]"));
        Assert.Contains("match_first_index = 30\nmatch_index_count = 42\nrun = CustomShaderPoseBlock_alpha_lod1_DrawS1_swap\nrun = CommandListDrawS1_swap\n",
            Section(ini, "[TextureOverride_Cap_alpha_lod1_DrawS1]"));
        Assert.Contains("match_first_index = 0\nmatch_index_count = 72\nrun = CustomShaderPoseBlock_alpha_lod1_DrawFull_swap\nrun = CommandListDraw_swap\n",
            Section(ini, "[TextureOverride_Cap_alpha_lod1_DrawFull]"));
        Assert.Equal(tierPrelude + Skin0, PoseBlock(ini, "alpha_lod1_DrawS0_swap"));
        Assert.Equal(tierPrelude + Skin1, PoseBlock(ini, "alpha_lod1_DrawS1_swap"));
        Assert.Equal(tierPrelude + Skin0 + Skin1, PoseBlock(ini, "alpha_lod1_DrawFull_swap"));
        // the lod0's own routed sections run the lod0 passes, not the tier's
        Assert.Contains("run = CustomShaderPoseBlock_alpha_DrawS0_swap\nrun = CommandListDrawS0_swap\n", Section(ini, "[TextureOverride_Cap_alpha_DrawS0]"));
        Assert.Equal(Lod0Prelude + Skin0, PoseBlock(ini, "alpha_DrawS0_swap"));
        Assert.DoesNotContain("alpha_lod1", Section(ini, "[TextureOverride_Cap_alpha_DrawS0]"));
    }

    [Fact]
    public void A_routed_tier_supplying_no_bones_gathers_the_lod0_packet_in_its_routed_sections()
    {
        string td = Path.Combine(_root, "alpha_l1"); SyntheticPool.WritePartDump(td, 3, 24, new[] { A, B });
        var s0 = File.ReadAllBytes(Path.Combine(td, "stream0.buf"));
        for (int v = 0; v < 24; v++)
            for (int c = 0; c < 3; c++) BitConverter.GetBytes(0.5f).CopyTo(s0, v * 40 + c * 4);
        File.WriteAllBytes(Path.Combine(td, "stream0.buf"), s0);
        var tierShapes = new DrawShapeSet(new[] { new DrawShape(0, 30), new DrawShape(30, 42) }, 72);
        var req = RoutedSinglePart(out string outDir, new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002", Shapes: tierShapes));
        var result = new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        ModBuilderTests.AssertEveryReferencedFileShips(ini, outDir);

        const string refPrelude = "run = CustomShaderGatherRef_alpha\nrun = CustomShaderPosePalette_alpha_swap\n";
        string s0Section = Section(ini, "[TextureOverride_Cap_alpha_lod1_DrawS0]");
        Assert.True(s0Section.Contains("match_first_index = 0\nmatch_index_count = 30\nrun = CustomShaderPoseBlock_alpha_lod1_DrawS0_swap\nrun = CommandListDrawS0_swap\n"),
            s0Section + "\n" + string.Join("\n", result.Diagnostics));
        Assert.Equal(refPrelude + Skin0, PoseBlock(ini, "alpha_lod1_DrawS0_swap", gatherRef: true));
        Assert.Contains("run = CustomShaderPoseBlock_alpha_lod1_DrawFull_swap\nrun = CommandListDraw_swap\n", Section(ini, "[TextureOverride_Cap_alpha_lod1_DrawFull]"));
        Assert.Equal(refPrelude + Skin0 + Skin1, PoseBlock(ini, "alpha_lod1_DrawFull_swap", gatherRef: true));
        Assert.DoesNotContain("run = ", Section(ini, "[TextureOverride_Cap_alpha_lod1]"));
        // the reference the tier's gather reads is still captured at the lod0 draw, which keeps nothing else
        Assert.Contains("Resource_alpha_Posed = ref vb0\n", Section(ini, "[TextureOverride_Cap_alpha]"));
        Assert.DoesNotContain("run = ", Section(ini, "[TextureOverride_Cap_alpha]"));
    }

    [Fact]
    public void A_tier_that_drops_a_donor_range_runs_the_passes_once_ahead_of_the_ranges_it_carries()
    {
        string td = Path.Combine(_root, "alpha_l1"); SyntheticPool.WritePartDump(td, 3, 24, new[] { A, B });
        GenericPositions(td, 24);
        // the tier has one drawable shape short of its whole mesh and binds only the first lod0 material,
        // so the second lod0 position, and the donor range folding onto it, is carried nowhere at this tier
        var tierShapes = new DrawShapeSet(new[] { new DrawShape(0, 72) }, 80);
        var first = new Remold.Core.Export.TierMaterialRef("bundle-a", 11, true);
        var second = new Remold.Core.Export.TierMaterialRef("bundle-a", 22, true);
        var map = TierMaterialMap.Build(new[] { first, second }, TwoShapes, new[] { first }, tierShapes,
            _ => new GeometryVerdict(null, TierMapRule.Absent));
        var req = RoutedSinglePart(out string outDir,
            new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002", Shapes: tierShapes, Map: map));
        new MigotoEmitter().Build(req);
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);

        // the dropped range's piece is skinned nowhere at this tier: nothing here draws it
        const string tierPrelude = "run = CustomShaderGather_alpha_lod1\nrun = CustomShaderPosePalette_alpha_lod1_swap\n";
        Assert.Contains("match_first_index = 0\nmatch_index_count = 72\nrun = CustomShaderPoseBlock_alpha_lod1_DrawS0_swap\nrun = CommandListDrawS0_swap\n",
            Section(ini, "[TextureOverride_Cap_alpha_lod1_DrawS0]"));
        Assert.Equal(tierPrelude + Skin0, PoseBlock(ini, "alpha_lod1_DrawS0_swap"));
        string full = Section(ini, "[TextureOverride_Cap_alpha_lod1_DrawFull]");
        Assert.Contains("match_first_index = 0\nmatch_index_count = 80\nrun = CustomShaderPoseBlock_alpha_lod1_DrawFull_swap\nrun = CommandListDrawS0_swap\n", full);
        Assert.DoesNotContain("run = CommandListDrawS1_swap", full);
        Assert.DoesNotContain("run = CommandListDraw_swap\n", full);
        Assert.Equal(tierPrelude + Skin0, PoseBlock(ini, "alpha_lod1_DrawFull_swap"));
        Assert.DoesNotContain("run = ", Section(ini, "[TextureOverride_Cap_alpha_lod1]"));
    }

    [Fact]
    public void Routed_toggle_states_each_run_their_own_passes_inside_their_own_gate_ahead_of_their_lists()
    {
        string ad = Path.Combine(_root, "alpha"); SyntheticPool.WritePartDump(ad, 1, 32, new[] { A, B });
        GenericPositions(ad, 32);
        var pipelines = new List<ReplacePipeline>();
        for (int state = 0; state < 2; state++)
        {
            string donor = Path.Combine(_root, $"donor{state}");
            SyntheticPool.WriteDonor(donor, verts: 8 + state, unionBones: 2, submeshes: 2);
            pipelines.Add(new ReplacePipeline
            {
                Suffix = $"swap_s{state}",
                Parts = new[] { new PoolPart("alpha", ad) },
                Anchor = "alpha",
                DonorDir = donor,
                CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                ToggleKey = new KeyRef("F7", state),
                AnchorShapes = TwoShapes,
            });
        }
        string outDir = Path.Combine(_root, "out-states");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = pipelines,
            KeyCycles = new[] { new KeyCycle("F7", 2, 0) },
        });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);

        string cap = Section(ini, "[TextureOverride_Cap_alpha]");
        Assert.DoesNotContain("run = ", cap);
        string s0 = Section(ini, "[TextureOverride_Cap_alpha_DrawS0]");
        foreach (int state in new[] { 0, 1 })
        {
            Assert.Matches($@"\nif [^\n]+\nrun = CustomShaderPoseBlock_alpha_DrawS0_swap_s{state}\nrun = CommandListDrawS0_swap_s{state}\nendif\n", s0);
            Assert.Matches($@"^run = CustomShaderGather_alpha\nrun = CustomShaderPosePalette_alpha_swap_s{state}\n"
                + $@"(run = CustomShaderPoseSkin_swap_s{state}_p\d+\n)+$", PoseBlock(ini, $"alpha_DrawS0_swap_s{state}"));
        }
        // one gather section serves both states; each state's block runs it under that state's gate
        Assert.Single(Regex.Matches(ini, @"\[CustomShaderGather_alpha\]"));
        Assert.Equal(2, Regex.Matches(s0, Regex.Escape("run = CustomShaderPoseBlock_alpha_DrawS0_swap_s")).Count);
    }

    [Fact]
    public void The_group_fuse_shaders_still_read_a_structured_buffer_through_the_shared_row_bodies()
    {
        foreach (bool slim in new[] { true, false })
        {
            string fuse = ComputeTemplates.EmitGroupFuse(groupBones: 3, slotBase: 5, slim, n: 40);
            string witness = ComputeTemplates.EmitGroupFuseWitness(groupBones: 3, slotBase: 5, slim, n: 40,
                witnessMemberBone: 1, witnessAnchorRow: 8);
            foreach (string hlsl in new[] { fuse, witness })
            {
                Assert.Contains("StructuredBuffer<Vtx>", hlsl);
                Assert.Contains("#define POS(i) q[i].position\n", hlsl);
                Assert.Contains(slim ? "*POS(Sel[sbase+t]);" : "p[k0]=POS((v+k0));", hlsl);
            }
            HlslCheck.CompilesClean(fuse, $"grpfuse_{slim}.hlsl");
            HlslCheck.CompilesClean(witness, $"grpfuse_w_{slim}.hlsl");
        }
        // a dense mesh reads its vertices in batches of eight and the rest one at a time; a loop the fixed
        // count would run once or never is written once or left out, so no count draws a compiler warning
        foreach (int n in Enumerable.Range(1, 17).Concat(new[] { 24, 25, 37, 40 }))
        {
            string fuse = ComputeTemplates.EmitGroupFuse(groupBones: 3, slotBase: 5, slim: false, n: n);
            Assert.Equal(n >= 8, fuse.Contains("p[k0]=POS((v+k0));", StringComparison.Ordinal));
            Assert.Equal(n % 8 != 0, fuse.Contains("precise float3 term=Cpinv[base+v]*POS(v);", StringComparison.Ordinal));
            Assert.Equal(n >= 16, fuse.Contains("for(;v+8<=N;v+=8)", StringComparison.Ordinal));
            Assert.Equal(n % 8 > 1, fuse.Contains("for(;v<N;v++)", StringComparison.Ordinal));
            HlslCheck.CompilesClean(fuse, $"grpfuse_dense_{n}.hlsl");
        }
    }
}
