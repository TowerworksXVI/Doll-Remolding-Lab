using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The wardrobe-group runtime: a bone no pool part poses takes an APPENDED palette slot past the union and
/// the witness block, written at the drawing member's own dispatch rather than in the anchor's chain. Pins
/// where the slots land, what the fused sections and shaders say, how the donor's compiled indices are moved
/// onto them, and that a request carrying no group emits exactly what it emitted before.
/// </summary>
public class GroupEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-grp-" + Guid.NewGuid().ToString("N"));

    public GroupEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static string GoldenDir([System.Runtime.CompilerServices.CallerFilePath] string self = "") =>
        Path.Combine(Path.GetDirectoryName(self)!, "golden");

    private const uint A = 101, B = 102, C = 103, G = 301, G2 = 302;

    /// <summary>Rewrite a dump's positions as a generic (rank-4-support) cloud — the shared fixture's ramp
    /// positions are near-collinear, which the weak-support sentinel correctly rejects.</summary>
    private static void GenericPositions(string dir, int verts, int seed = 0)
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

    private string Dump(string name, int seed, int verts, uint[] bones, int weighted = 0)
    {
        string dir = Path.Combine(_root, name);
        SyntheticPool.WritePartDump(dir, seed, verts, bones, weighted);
        GenericPositions(dir, verts, seed);
        return dir;
    }

    /// <summary>The fixture: alpha (A,B) + beta (B,C) with beta the anchor, one alpha tier so the witness
    /// block reserves a slot ahead of the group region, and one wardrobe slot of TWO variants covering the
    /// donor bone G. Variant 1's member ships a lod0 and a tier; variant 2's ships a lod0 alone. The donor
    /// rides the whole union plus G, whose compiled index is the dense continuation unionBones + 0.</summary>
    private PoolBuildRequest Fixture(out string outDir, out string donorDir, bool withTier = true,
        uint[]? m1Bones = null)
    {
        string ad = Dump("alpha", 1, 32, new[] { A, B });
        string bd = Dump("beta", 2, 32, new[] { B, C });
        string at = Dump("alpha_lod1", 3, 32, new[] { A, B });
        string m1 = Dump("mv1", 4, 32, m1Bones ?? new[] { C, G });
        string m1t = Dump("mv1_lod1", 5, 32, m1Bones ?? new[] { C, G });
        string m2 = Dump("mv2", 6, 32, new[] { C, G });

        donorDir = Path.Combine(_root, "donor");
        var bones = new List<uint> { G };
        SyntheticPool.WriteDonor(donorDir, verts: 8, unionBones: 4, submeshes: 2);
        outDir = Path.Combine(_root, "out");

        var meshes1 = new List<PoolGroupMesh> { new("mv1", "", m1, "aaaa0011") };
        if (withTier) meshes1.Add(new PoolGroupMesh("mv1_lod1", "lod1", m1t, "aaaa0012"));

        return new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    DonorDir = donorDir,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    Tiers = new[] { new PoolTier("alpha", "alpha_lod1", "lod1", at, "aaaa0002") },
                    Groups = new[]
                    {
                        new PoolGroup(7, bones, new[]
                        {
                            new PoolGroupMember(1, PresenceContext.Always, "mv1", "mv1") { Meshes = meshes1 },
                            new PoolGroupMember(2, PresenceContext.Always, "mv2", "mv2")
                            {
                                Meshes = new[] { new PoolGroupMesh("mv2", "", m2, "bbbb0011") },
                            },
                        }),
                    },
                },
            },
        };
    }

    private static MigotoEmitter.Result Build(PoolBuildRequest req) => new MigotoEmitter().Build(req);

    // ---- zero diff ------------------------------------------------------------------------------------

    /// <summary>The hard invariant of this feature: a request carrying no group emits what it emitted
    /// before it existed. Null and empty must also be the same request — a caller handing an empty list
    /// rather than null is not asking for anything.</summary>
    [Fact]
    public void A_request_with_no_groups_emits_byte_identical_output()
    {
        string ad = Dump("alpha", 1, 32, new[] { A, B });
        string bd = Dump("beta", 2, 32, new[] { B, C });
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 3, submeshes: 2);

        string Run(string name, IReadOnlyList<PoolGroup>? groups)
        {
            string outDir = Path.Combine(_root, name);
            Build(new PoolBuildRequest
            {
                OutDir = outDir,
                Pipelines = new[]
                {
                    new ReplacePipeline
                    {
                        Suffix = "swap",
                        Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                        Anchor = "beta",
                        DonorDir = donor,
                        CaptureHashes = new Dictionary<string, string>
                            { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                        Groups = groups,
                    },
                },
            });
            return outDir;
        }

        string nullRun = Run("no_groups_null", null);
        string emptyRun = Run("no_groups_empty", Array.Empty<PoolGroup>());

        var byName = Directory.GetFiles(nullRun).Select(Path.GetFileName).OrderBy(f => f).ToList();
        Assert.Equal(byName, Directory.GetFiles(emptyRun).Select(Path.GetFileName).OrderBy(f => f).ToList());
        foreach (var f in byName)
            Assert.Equal(File.ReadAllBytes(Path.Combine(nullRun, f!)),
                File.ReadAllBytes(Path.Combine(emptyRun, f!)));

        // nothing of the feature reaches an ini that carries no group
        string ini = File.ReadAllText(Path.Combine(nullRun, "mod.ini"));
        Assert.DoesNotContain("zz_grp_seen", ini);
        Assert.DoesNotContain("zz_grp_cb", ini);
        Assert.DoesNotContain("CustomShaderGroup_", ini);
    }

    // ---- the palette region ---------------------------------------------------------------------------

    /// <summary>Union rows, then the witness block's reservations, then one slot per group bone. The seed
    /// sizes both palettes, so the appended region exists in the CONVERTED one the members write.</summary>
    [Fact]
    public void Group_bones_take_appended_slots_past_the_union_and_the_witness_block()
    {
        Build(Fixture(out string outDir, out _));
        // union 3 + one witness reservation (alpha's shared bone is not the anchor's to own) + 1 group bone
        Assert.Equal(5 * 64, new FileInfo(Path.Combine(outDir, "palette_seed_swap.buf")).Length);

        // the converts still dispatch over UNION rows only: the appended rows ride their copy round-trip
        string convert = File.ReadAllText(Path.Combine(outDir, "convert_cs_swap.hlsl"));
        Assert.Contains("static const uint ROWS=12;", convert);
        // …and the group shader writes from the slot past every reservation
        Assert.Contains("ROWS=4, BASE=4;",
            File.ReadAllText(Path.Combine(outDir, "grpfuse_mv1_swap.hlsl")));
    }

    /// <summary>TWO slots, each with a member and a bone of its own: the second group's slots continue where
    /// the first's stop, so the region stays contiguous and the donor's dense continuation (unionBones + k)
    /// maps onto it by a single offset. One group with two bones would not walk the per-group base at
    /// all.</summary>
    [Fact]
    public void A_second_group_continues_the_same_region()
    {
        string ad = Dump("alpha", 1, 32, new[] { A, B });
        string bd = Dump("beta", 2, 32, new[] { B, C });
        string at = Dump("alpha_lod1", 3, 32, new[] { A, B });
        string m1 = Dump("mv1", 4, 32, new[] { C, G });
        string w1 = Dump("wv1", 7, 32, new[] { C, G2 });
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 5, submeshes: 2);
        string outDir = Path.Combine(_root, "out");

        Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    Tiers = new[] { new PoolTier("alpha", "alpha_lod1", "lod1", at, "aaaa0002") },
                    Groups = new[]
                    {
                        new PoolGroup(7, new[] { G }, new[]
                        {
                            new PoolGroupMember(1, PresenceContext.Always, "mv1", "mv1")
                            {
                                Meshes = new[] { new PoolGroupMesh("mv1", "", m1, "aaaa0011") },
                            },
                        }),
                        new PoolGroup(8, new[] { G2 }, new[]
                        {
                            new PoolGroupMember(1, PresenceContext.Always, "wv1", "wv1")
                            {
                                Meshes = new[] { new PoolGroupMesh("wv1", "", w1, "bbbb0011") },
                            },
                        }),
                    },
                },
            },
        });

        // union 3 + one witness reservation + one slot per group bone, over BOTH groups
        Assert.Equal(6 * 64, new FileInfo(Path.Combine(outDir, "palette_seed_swap.buf")).Length);
        // the first group's base, then that base plus the first group's bone count
        Assert.Contains("ROWS=4, BASE=4;", File.ReadAllText(Path.Combine(outDir, "grpfuse_mv1_swap.hlsl")));
        Assert.Contains("ROWS=4, BASE=5;", File.ReadAllText(Path.Combine(outDir, "grpfuse_wv1_swap.hlsl")));
    }

    // ---- the donor's indices --------------------------------------------------------------------------

    /// <summary>The donor compiles onto unionBones + k and knows nothing of the witness slots between; the
    /// emission adds that offset at the one site that writes the skin stream.</summary>
    [Fact]
    public void The_donors_group_indices_move_onto_the_reserved_slots()
    {
        Build(Fixture(out string outDir, out string donorDir));
        var before = File.ReadAllBytes(Path.Combine(donorDir, "stream2.buf"));
        var after = File.ReadAllBytes(Path.Combine(outDir, "combined_skin_swap.buf"));
        Assert.Equal(before.Length, after.Length);

        uint Index(byte[] s, int v, int k) => BitConverter.ToUInt32(s, v * 32 + 16 + k * 4);
        int moved = 0;
        for (int v = 0; v * 32 < before.Length; v++)
        {
            uint was = Index(before, v, 0), now = Index(after, v, 0);
            if (was == 3) { Assert.Equal(4u, now); moved++; }   // the group bone: union 3 -> slot 4
            else Assert.Equal(was, now);                        // every union index is left alone
        }
        Assert.True(moved > 0, "the fixture donor rides the group bone");
    }

    // ---- the member sections --------------------------------------------------------------------------

    /// <summary>A member's lod0 rebases from draw CONSTANTS: its own vs-cb1, copied at its own draw, against
    /// the anchor's captured one.</summary>
    [Fact]
    public void A_member_lod0_section_rebases_from_the_two_constant_buffers()
    {
        Build(Fixture(out string outDir, out _));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);

        Assert.Contains("[TextureOverride_Cap_mv1]\nhash = aaaa0011\n"
                      + "Resource_mv1_Posed = ref vb0\nResource_mv1_CB = copy vs-cb1\n", ini);
        string sec = Section(ini, "[CustomShaderGroup_mv1_swap]");
        Assert.Contains("cs-u1 = copy Resource_PaletteConv_swap\n", sec);
        Assert.Contains("cs-t2 = Resource_mv1_GMap_swap\n", sec);
        Assert.Contains("cs-cb5 = Resource_mv1_CB\n", sec);
        Assert.Contains("cs-cb13 = Resource_beta_CB\n", sec);
        Assert.Contains("Resource_PaletteConv_swap = copy cs-u1\n", sec);
        Assert.DoesNotContain("cs-t5", sec);

        string shader = File.ReadAllText(Path.Combine(outDir, "grpfuse_mv1_swap.hlsl"));
        Assert.Contains("cbuffer MemberCB : register(b5)", shader);
        Assert.Contains("cbuffer AnchorCB : register(b13)", shader);
        Assert.DoesNotContain("palRaw", shader);
    }

    /// <summary>A member's TIER rebases from geometry instead: constants at a tier draw can be a window into
    /// a shared buffer that a whole-resource copy reads wrongly, so K comes from a witness bone both sides
    /// pose soundly — the member's side solved inline, the anchor's read out of the raw palette.</summary>
    [Fact]
    public void A_member_tier_section_rebases_from_witness_geometry()
    {
        Build(Fixture(out string outDir, out _));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        // the tier capture takes no constant buffer at all
        Assert.Contains("[TextureOverride_Cap_mv1_lod1]\nhash = aaaa0012\nResource_mv1_lod1_Posed = ref vb0\n", ini);
        string cap = Section(ini, "[TextureOverride_Cap_mv1_lod1]");
        Assert.DoesNotContain("copy vs-cb1", cap);

        string sec = Section(ini, "[CustomShaderGroup_mv1_lod1_swap]");
        Assert.Contains("cs-t5 = copy Resource_Palette_swap\n", sec);
        Assert.DoesNotContain("cs-cb", sec);

        string shader = File.ReadAllText(Path.Combine(outDir, "grpfuse_mv1_lod1_swap.hlsl"));
        Assert.Contains("StructuredBuffer<float4> palRaw : register(t5);", shader);
        Assert.Contains("static const uint WITM=", shader);
        Assert.Contains("static const uint WITA=", shader);
    }

    /// <summary>No bone the member and the anchor both pose soundly means no trustworthy K at a tier draw,
    /// and production's own posture is to emit nothing rather than a guess. The member's lod0 still
    /// writes.</summary>
    [Fact]
    public void A_member_sharing_no_sound_bone_with_the_anchor_emits_no_tier_section()
    {
        var result = Build(Fixture(out string outDir, out _, m1Bones: new[] { G, G2 }));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.DoesNotContain("[CustomShaderGroup_mv1_lod1_swap]", ini);
        Assert.False(File.Exists(Path.Combine(outDir, "grpfuse_mv1_lod1_swap.hlsl")));
        Assert.Contains("[CustomShaderGroup_mv1_swap]", ini);        // lod0 is untouched by the verdict
        Assert.Contains(result.Diagnostics, d => d.Contains("mv1") && d.Contains("no sound bone"));
    }

    /// <summary>A group bone the member's mesh cannot recover is dropped from that member's map rather than
    /// tied rigidly to a neighbour: a tie is sound for geometry riding the bone, and none of the donor's
    /// does.</summary>
    [Fact]
    public void A_group_bone_the_member_cannot_condition_is_dropped_and_named()
    {
        // mv1/mv2 carry C and G with weight only on C, so G recovers as min-norm noise
        string ad = Dump("alpha", 1, 32, new[] { A, B });
        string bd = Dump("beta", 2, 32, new[] { B, C });
        string m1 = Dump("mv1", 4, 32, new[] { C, G }, weighted: 1);
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 4, submeshes: 2);
        string outDir = Path.Combine(_root, "out");

        var result = Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    Groups = new[]
                    {
                        new PoolGroup(7, new[] { G }, new[]
                        {
                            new PoolGroupMember(1, PresenceContext.Always, "mv1", "mv1")
                            {
                                Meshes = new[] { new PoolGroupMesh("mv1", "", m1, "aaaa0011") },
                            },
                        }),
                    },
                },
            },
        });

        // every entry of its map sentinelled leaves the member nothing to write, so it ships no section
        Assert.DoesNotContain("[CustomShaderGroup_mv1_swap]", File.ReadAllText(Path.Combine(outDir, "mod.ini")));
        Assert.False(File.Exists(Path.Combine(outDir, "mv1_gmap_swap.buf")));
        Assert.Contains(result.Diagnostics,
            d => d.Contains("mv1") && d.Contains("0x0000012d") && d.Contains("ill-conditioned"));
    }

    /// <summary>The member's map is the restriction to THIS group's bones: one entry per group bone, holding
    /// the member's own local bone index.</summary>
    [Fact]
    public void The_member_map_holds_one_entry_per_group_bone()
    {
        Build(Fixture(out string outDir, out _));
        var map = File.ReadAllBytes(Path.Combine(outDir, "mv1_gmap_swap.buf"));
        Assert.Equal(4, map.Length);                       // one uint for the group's single bone
        Assert.Equal(1u, BitConverter.ToUInt32(map, 0));   // G is mv1's second bone
    }

    /// <summary>The witness K reads the ANCHOR's own recovery of the shared bone. Where another pool part
    /// owns that bone's union row, the anchor's recovery has nowhere to land, so a slot past the group
    /// region is reserved for it and the anchor's scatter map is patched onto it.</summary>
    [Fact]
    public void An_anchor_that_does_not_own_the_witness_bone_reserves_a_slot_for_its_recovery()
    {
        // alpha carries C alone and outweighs the anchor on it, so C's union row is alpha's
        string ad = Dump("alpha", 1, 48, new[] { C });
        string bd = Dump("beta", 2, 32, new[] { B, C });
        string m1 = Dump("mv1", 4, 32, new[] { C, G });
        string m1t = Dump("mv1_lod1", 5, 32, new[] { C, G });
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 3, submeshes: 1);
        string outDir = Path.Combine(_root, "out");

        Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    Groups = new[]
                    {
                        new PoolGroup(7, new[] { G }, new[]
                        {
                            new PoolGroupMember(1, PresenceContext.Always, "mv1", "mv1")
                            {
                                Meshes = new[]
                                {
                                    new PoolGroupMesh("mv1", "", m1, "aaaa0011"),
                                    new PoolGroupMesh("mv1_lod1", "lod1", m1t, "aaaa0012"),
                                },
                            },
                        }),
                    },
                },
            },
        });

        // union 2 (C, B) + 1 group slot + 1 reserved for the anchor's witness recovery
        Assert.Equal(4 * 64, new FileInfo(Path.Combine(outDir, "palette_seed_swap.buf")).Length);
        // …and the tier shader reads that slot's first row
        Assert.Contains("static const uint WITA=12;",
            File.ReadAllText(Path.Combine(outDir, "grpfuse_mv1_lod1_swap.hlsl")));
        // the anchor's own scatter map now sends C's rows there
        var betaMap = File.ReadAllBytes(Path.Combine(outDir, "beta_map_swap.buf"));
        Assert.Contains(3u, Enumerable.Range(0, betaMap.Length / 4)
            .Select(i => BitConverter.ToUInt32(betaMap, i * 4)));
    }

    // ---- the first-frame guard ------------------------------------------------------------------------

    /// <summary>Every member run-line waits on the state its own dispatch reads — a lod0 on the anchor's
    /// captured CONSTANTS, a tier on the anchor having drawn at all — and both flags are sticky: [Present]
    /// clears the per-frame chain flags and leaves these two alone.</summary>
    [Fact]
    public void Every_member_run_line_waits_on_the_anchors_first_capture()
    {
        Build(Fixture(out string outDir, out _));
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains("global $zz_grp_cb_swap = 0\n", ini);
        Assert.Contains("global $zz_grp_seen_swap = 0\n", ini);
        // both set where the anchor's constants land, not in the chain that runs after them
        Assert.Contains("Resource_beta_CB = copy vs-cb1\n$zz_grp_cb_swap = 1\n$zz_grp_seen_swap = 1\n", ini);

        string present = Section(ini, "[Present]");
        Assert.Contains("$zz_done_swap = 0\n", present);
        Assert.DoesNotContain("zz_grp_seen", present);
        Assert.DoesNotContain("zz_grp_cb", present);

        var lines = ini.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("run = CustomShaderGroup_", StringComparison.Ordinal))
            {
                bool tier = lines[i].Contains("_lod1_", StringComparison.Ordinal);
                Assert.Equal(tier ? "if $zz_grp_seen_swap == 1" : "if $zz_grp_cb_swap == 1", lines[i - 1]);
                Assert.Equal("endif", lines[i + 1]);
            }
        Assert.Equal(3, lines.Count(l => l.StartsWith("run = CustomShaderGroup_", StringComparison.Ordinal)));
    }

    /// <summary>The two waits are two preconditions, and an anchor that renders only at tier detail meets
    /// exactly one of them: its tier capture proves it drew (which fills the witness rows a member TIER
    /// rebases through) and copies no constants (which is all a member LOD0 rebase has). A session that
    /// conflated them left every member dispatch dead until the anchor's first lod0 draw.</summary>
    [Fact]
    public void An_anchor_tier_capture_reports_the_anchor_seen_but_not_its_constants()
    {
        string ad = Dump("alpha", 1, 32, new[] { A, B });
        string bd = Dump("beta", 2, 32, new[] { B, C });
        string bt = Dump("beta_lod1", 3, 32, new[] { B, C });
        string m1 = Dump("mv1", 4, 32, new[] { C, G });
        string m1t = Dump("mv1_lod1", 5, 32, new[] { C, G });
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 4, submeshes: 2);
        string outDir = Path.Combine(_root, "out");

        Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    // the ANCHOR's own other tier: the case no other fixture carries
                    Tiers = new[] { new PoolTier("beta", "beta_lod1", "lod1", bt, "bbbb0002") },
                    Groups = new[]
                    {
                        new PoolGroup(7, new[] { G }, new[]
                        {
                            new PoolGroupMember(1, PresenceContext.Always, "mv1", "mv1")
                            {
                                Meshes = new[]
                                {
                                    new PoolGroupMesh("mv1", "", m1, "aaaa0011"),
                                    new PoolGroupMesh("mv1_lod1", "lod1", m1t, "aaaa0012"),
                                },
                            },
                        }),
                    },
                },
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);

        string lod0Cap = Section(ini, "[TextureOverride_Cap_beta]");
        Assert.Contains("$zz_grp_cb_swap = 1\n", lod0Cap);
        Assert.Contains("$zz_grp_seen_swap = 1\n", lod0Cap);

        string tierCap = Section(ini, "[TextureOverride_Cap_beta_lod1]");
        Assert.Contains("$zz_grp_seen_swap = 1\n", tierCap);
        Assert.DoesNotContain("zz_grp_cb", tierCap);
        Assert.DoesNotContain("copy vs-cb1", tierCap);

        Assert.Contains("if $zz_grp_cb_swap == 1\nrun = CustomShaderGroup_mv1_swap\nendif\n",
            Section(ini, "[TextureOverride_Cap_mv1]"));
        Assert.Contains("if $zz_grp_seen_swap == 1\nrun = CustomShaderGroup_mv1_lod1_swap\nendif\n",
            Section(ini, "[TextureOverride_Cap_mv1_lod1]"));
    }

    /// <summary>A member dispatch is a dispatch: it sits inside the pipeline's draw gate exactly as the pool
    /// chains do, so the mod's stated "off dispatches nothing" holds. The sticky wait stays INSIDE the gate —
    /// the gate is about the key, the wait about state the dispatch reads.</summary>
    [Fact]
    public void Member_dispatches_sit_inside_the_pipelines_draw_gate()
    {
        var req = Fixture(out string outDir, out _);
        Build(req with { Pipelines = new[] { req.Pipelines[0] with { ToggleKey = "F8" } } });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains("if $zz_key_f8 == 1\nif $zz_grp_cb_swap == 1\n"
                      + "run = CustomShaderGroup_mv1_swap\nendif\nendif\n", ini);
        Assert.Contains("if $zz_key_f8 == 1\nif $zz_grp_seen_swap == 1\n"
                      + "run = CustomShaderGroup_mv1_lod1_swap\nendif\nendif\n", ini);

        var lines = ini.Split('\n');
        int dispatches = 0;
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].StartsWith("run = CustomShaderGroup_", StringComparison.Ordinal))
            {
                Assert.Equal("if $zz_key_f8 == 1", lines[i - 2]);
                dispatches++;
            }
        Assert.Equal(3, dispatches);
    }

    // ---- the capture-section merge --------------------------------------------------------------------

    /// <summary>A member mesh another pipeline pools is ONE section: duplicate-named overrides drop silently
    /// at parse time, so the member's lines join the unit that hash already owns. The pooling pipeline's
    /// suppression stays; the member adds none of its own.</summary>
    [Fact]
    public void A_member_another_pipeline_pools_joins_that_capture_section()
    {
        string ad = Dump("alpha", 1, 32, new[] { A, B });
        string bd = Dump("beta", 2, 32, new[] { B, C });
        string m1 = Dump("mv1", 4, 32, new[] { C, G });
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 4, submeshes: 2);
        string donor2 = Path.Combine(_root, "donor2");
        SyntheticPool.WriteDonor(donor2, verts: 8, unionBones: 2, submeshes: 1);
        string outDir = Path.Combine(_root, "out");

        Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    Groups = new[]
                    {
                        new PoolGroup(7, new[] { G }, new[]
                        {
                            new PoolGroupMember(1, PresenceContext.Always, "mv1", "mv1")
                            {
                                Meshes = new[] { new PoolGroupMesh("mv1", "", m1, "aaaa0011") },
                            },
                        }),
                    },
                },
                // a second Replace that POOLS the same mesh, and suppresses its draw
                new ReplacePipeline
                {
                    Suffix = "other",
                    Parts = new[] { new PoolPart("mv1", m1) },
                    DonorDir = donor2,
                    CaptureHashes = new Dictionary<string, string> { ["mv1"] = "aaaa0011" },
                },
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ini, @"^hash = aaaa0011$",
            System.Text.RegularExpressions.RegexOptions.Multiline));

        string cap = Section(ini, "[TextureOverride_Cap_mv1]");
        Assert.Equal(1, cap.Split("Resource_mv1_Posed = ref vb0\n").Length - 1);   // captured once
        Assert.Contains("handling = skip\n", cap);                                  // the pooling pipeline's
        Assert.Contains("run = CustomShaderGroup_mv1_swap\n", cap);                 // the member's, unsuppressed
    }

    /// <summary>A member the build also HIDES keeps its suppression: the hide pass leaves a hash a pipeline
    /// captures to the capture section, so the section that runs the member's rebase is where the skip has
    /// to be. Capture and rebase still run at the suppressed draw, as they do for a hidden pool part.</summary>
    [Fact]
    public void A_hidden_member_keeps_its_suppression_and_still_rebases()
    {
        var req = Fixture(out string outDir, out _, withTier: false);
        var pipe = req.Pipelines[0];
        var group = pipe.Groups![0];
        Build(req with
        {
            Pipelines = new[]
            {
                pipe with
                {
                    Groups = new[]
                    {
                        new PoolGroup(group.SlotId, group.GroupBones,
                            group.Members.Select(m => m with { Hidden = m.Mesh == "mv1" }).ToList()),
                    },
                },
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        string hidden = Section(ini, "[TextureOverride_Cap_mv1]");
        Assert.Contains("handling = skip\n", hidden);
        Assert.Contains("run = CustomShaderGroup_mv1_swap\n", hidden);
        // the other variant's member is untouched: nothing hides it
        Assert.DoesNotContain("handling = skip\n", Section(ini, "[TextureOverride_Cap_mv2]"));
    }

    /// <summary>The hide is owed to the MESH, not to the dispatch. A hidden member's draw is claimed at the
    /// hash the moment the build takes it, which takes it off the hide pass — so a mesh the emission then
    /// drops (here, a tier losing its section to the witness verdict) has this capture section as the only
    /// place left that can skip it, and would otherwise render normally with nothing saying so.</summary>
    [Fact]
    public void A_hidden_member_stays_hidden_where_its_fused_section_is_dropped()
    {
        var req = Fixture(out string outDir, out _, m1Bones: new[] { G, G2 });
        var pipe = req.Pipelines[0];
        var group = pipe.Groups![0];
        Build(req with
        {
            Pipelines = new[]
            {
                pipe with
                {
                    Groups = new[]
                    {
                        new PoolGroup(group.SlotId, group.GroupBones,
                            group.Members.Select(m => m with { Hidden = m.Mesh == "mv1" }).ToList()),
                    },
                },
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);

        // mv1 shares no sound bone with the anchor, so its tier ships no fused section at all
        Assert.DoesNotContain("[CustomShaderGroup_mv1_lod1_swap]", ini);
        string tier = Section(ini, "[TextureOverride_Cap_mv1_lod1]");
        Assert.Contains("handling = skip\n", tier);
        Assert.DoesNotContain("run = CustomShaderGroup_", tier);

        // …while the lod0 keeps both its skip and its dispatch
        string lod0 = Section(ini, "[TextureOverride_Cap_mv1]");
        Assert.Contains("handling = skip\n", lod0);
        Assert.Contains("run = CustomShaderGroup_mv1_swap\n", lod0);

        Assert.DoesNotContain("handling = skip\n", Section(ini, "[TextureOverride_Cap_mv2]"));
    }

    // ---- the pair-shaped group -------------------------------------------------------------------------

    /// <summary>The emitter is id-BLIND: a group carrying <see cref="PoolDerive.CoverageGroupId"/> and two
    /// members on their context arms emits exactly what any other member pair does. Nothing downstream of
    /// the build reads a group id as a wardrobe id, and the member ids are metadata, so this pins the
    /// ordinary emissions over the shape a schemeless outfit's coverage actually hands it: member sections
    /// at their own hashes, a fused shader each, the appended region sized one slot per group bone, and the
    /// donor's dense continuation shifted onto that region.</summary>
    [Fact]
    public void A_pair_shaped_group_emits_exactly_as_a_wardrobe_slots_does()
    {
        string ad = Dump("alpha", 1, 32, new[] { A, B });
        string bd = Dump("beta", 2, 32, new[] { B, C });
        string at = Dump("alpha_lod1", 3, 32, new[] { A, B });
        string mf = Dump("cloth_Fight", 4, 32, new[] { C, G });
        string md = Dump("cloth_Dorm", 6, 32, new[] { C, G });

        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 4, submeshes: 2);
        string outDir = Path.Combine(_root, "out");

        Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", ad), new PoolPart("beta", bd) },
                    Anchor = "beta",
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string>
                        { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    Tiers = new[] { new PoolTier("alpha", "alpha_lod1", "lod1", at, "aaaa0002") },
                    Groups = new[]
                    {
                        new PoolGroup(PoolDerive.CoverageGroupId, new[] { G }, new[]
                        {
                            new PoolGroupMember((long)PresenceContext.Fight, PresenceContext.Fight,
                                "cloth_Fight", "cloth_Fight")
                            {
                                Meshes = new[] { new PoolGroupMesh("cloth_Fight", "", mf, "aaaa0011") },
                            },
                            new PoolGroupMember((long)PresenceContext.Dorm, PresenceContext.Dorm,
                                "cloth_Dorm", "cloth_Dorm")
                            {
                                Meshes = new[] { new PoolGroupMesh("cloth_Dorm", "", md, "bbbb0011") },
                            },
                        }),
                    },
                },
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);

        // both arms capture at their own hashes, exactly as two variants of a slot do
        Assert.Contains("[TextureOverride_Cap_cloth_Fight]\nhash = aaaa0011\n"
                      + "Resource_cloth_Fight_Posed = ref vb0\nResource_cloth_Fight_CB = copy vs-cb1\n", ini);
        Assert.Contains("[TextureOverride_Cap_cloth_Dorm]\nhash = bbbb0011\n"
                      + "Resource_cloth_Dorm_Posed = ref vb0\nResource_cloth_Dorm_CB = copy vs-cb1\n", ini);

        // one fused shader per member, rebasing from the two constant buffers
        foreach (var arm in new[] { "cloth_Fight", "cloth_Dorm" })
        {
            string sec = Section(ini, $"[CustomShaderGroup_{arm}_swap]");
            Assert.Contains($"cs-t2 = Resource_{arm}_GMap_swap\n", sec);
            Assert.Contains($"cs-cb5 = Resource_{arm}_CB\n", sec);
            Assert.Contains("cs-cb13 = Resource_beta_CB\n", sec);

            string shader = File.ReadAllText(Path.Combine(outDir, $"grpfuse_{arm}_swap.hlsl"));
            Assert.Contains("cbuffer MemberCB : register(b5)", shader);
            // one group, one bone: both arms write the same appended slot, past union + witness
            Assert.Contains("ROWS=4, BASE=4;", shader);
        }

        // union 3 + one witness reservation + one slot for the pair's single group bone
        Assert.Equal(5 * 64, new FileInfo(Path.Combine(outDir, "palette_seed_swap.buf")).Length);

        // and the donor's dense continuation moves onto that slot, the same single offset a slot's group takes
        var before = File.ReadAllBytes(Path.Combine(donor, "stream2.buf"));
        var after = File.ReadAllBytes(Path.Combine(outDir, "combined_skin_swap.buf"));
        uint Index(byte[] s, int v) => BitConverter.ToUInt32(s, v * 32 + 16);
        int moved = 0;
        for (int v = 0; v * 32 < before.Length; v++)
        {
            uint was = Index(before, v), now = Index(after, v);
            if (was == 3) { Assert.Equal(4u, now); moved++; }
            else Assert.Equal(was, now);
        }
        Assert.True(moved > 0, "the fixture donor rides the group bone");
    }

    // ---- the text contract ----------------------------------------------------------------------------

    /// <summary>The emitted text of the whole feature, pinned: the ini and both fused shaders. To alter it
    /// on purpose, regenerate with <c>REMOLD_REGOLD=1</c> for one run and account for every hunk.</summary>
    [Fact]
    public void The_group_build_emits_the_pinned_text_contract()
    {
        Build(Fixture(out string outDir, out _));
        bool regold = Environment.GetEnvironmentVariable("REMOLD_REGOLD") == "1";
        foreach (var (emittedFile, goldenFile) in new[]
                 {
                     ("mod.ini", "mod_group.ini"),
                     ("grpfuse_mv1_swap.hlsl", "grpfuse_lod0.hlsl"),
                     ("grpfuse_mv1_lod1_swap.hlsl", "grpfuse_tier.hlsl"),
                 })
        {
            string emitted = File.ReadAllText(Path.Combine(outDir, emittedFile));
            string goldenPath = Path.Combine(GoldenDir(), goldenFile);
            if (regold)
            {
                Directory.CreateDirectory(GoldenDir());
                File.WriteAllText(goldenPath, emitted);
                continue;
            }
            Assert.True(File.Exists(goldenPath), $"golden asset missing: {goldenPath} (run once with REMOLD_REGOLD=1)");
            Assert.Equal(File.ReadAllText(goldenPath), emitted);
        }
        Assert.False(regold, "REMOLD_REGOLD run regenerated the goldens — rerun without it to compare");
    }

    /// <summary>The named ini section's body, up to the blank line that ends it.</summary>
    private static string Section(string ini, string header)
    {
        // matched at a line of its own: the emitted header comment names sections in prose
        int at = ini.IndexOf("\n" + header + "\n", StringComparison.Ordinal);
        Assert.True(at >= 0, $"section {header} is missing");
        at++;
        int end = ini.IndexOf("\n\n", at, StringComparison.Ordinal);
        return end < 0 ? ini[at..] : ini[at..(end + 1)];
    }
}
