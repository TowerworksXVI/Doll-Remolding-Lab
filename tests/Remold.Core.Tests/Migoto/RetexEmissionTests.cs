using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Migoto;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// The Retexture emission: ONE section per stock texture, keyed on that texture's own resource hash and
/// rebinding it with <c>this =</c> — no slot binds, no pass branches, no save/restore. Covers standalone
/// vs appended-to-pool composition, where the pooled prefix stays golden-identical.
/// </summary>
public class RetexEmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-retex-" + Guid.NewGuid().ToString("N"));

    public RetexEmissionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Dds(string name)
    {
        string p = Path.Combine(_root, name);
        FlatDds.Write(p, (90, 90, 90, 255));
        return p;
    }

    [Fact]
    public void An_entry_on_a_guards_probe_tag_writes_the_verdict_at_bind_time()
    {
        // The rebind hides the stock texture from the guard's draw probe — the bound replacement
        // answers to no tag — so the retexture's section writes the tagged sibling's verdict itself,
        // outside the gate and with the tag, and no separate TwinTag section is minted on the hash.
        string outDir = Path.Combine(_root, "out-twin");
        string hash = "3ff9db6d";
        int tag = MigotoEmitter.RetexTag(hash);
        var r = new MigotoEmitter().BuildOverlaysOnly(outDir,
            new[] { new RetexEntry("ilse_face_a", hash, Dds("face_a.dds")) },
            hideHashes: new[] { "aaaa1111" },
            twinGuards: new[]
            {
                new TwinGuard("aaaa1111", MigotoEmitter.TwinVar("aaaa1111"), new[] { 1 },
                    new[]
                    {
                        new TwinProbeTag(hash, tag, 1),
                        new TwinProbeTag("22bb33cc", MigotoEmitter.RetexTag("22bb33cc"), 2),
                    }),
            });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.Contains(
            "[TextureOverride_Retex_ilse_face_a]\n"
            + $"hash = {hash}\n"
            + $"filter_index = {tag}\n"
            + "match_priority = 100\n"
            + "$zz_tw_aaaa1111 = 1\n"
            + "this = Resource_Rtx0\n", ini);
        Assert.DoesNotContain($"[TextureOverride_TwinTag_{hash}]", ini);
        // the unrepainted sibling's tag still gets its own minted section, with no verdict write
        Assert.Contains("[TextureOverride_TwinTag_22bb33cc]\nhash = 22bb33cc\n"
            + $"filter_index = {MigotoEmitter.RetexTag("22bb33cc")}\nmatch_priority = 100\n\n", ini);
    }

    [Fact]
    public void An_entry_emits_the_exact_section_and_nothing_pass_shaped()
    {
        string outDir = Path.Combine(_root, "out");
        var r = new MigotoEmitter().BuildOverlaysOnly(outDir, new[]
        {
            new RetexEntry("ilse_face_a", "3ff9db6d", Dds("face_a.dds")),
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        Assert.Contains(
            "[Resource_Rtx0]\n"
            + "filename = face_a.dds\n"
            + "\n"
            + "[TextureOverride_Retex_ilse_face_a]\n"
            + "hash = 3ff9db6d\n"
            + "this = Resource_Rtx0\n",
            ini);
        // the bind-time swap needs no pass knowledge at all
        Assert.DoesNotContain("$zz_pass", ini);
        Assert.DoesNotContain("ps-t", ini);
        Assert.DoesNotContain("Resource_RtxSave", ini);
        Assert.DoesNotContain("match_first_index", ini);
        Assert.True(File.Exists(Path.Combine(outDir, "face_a.dds")));
        Assert.Equal(0, r.UnionBones);
    }

    [Fact]
    public void Several_maps_of_one_part_are_several_independent_sections()
    {
        string outDir = Path.Combine(_root, "out");
        new MigotoEmitter().BuildOverlaysOnly(outDir, new[]
        {
            new RetexEntry("body_a", "aaaa0001", Dds("a.dds")),
            new RetexEntry("body_n", "aaaa0002", Dds("n.dds")),
            new RetexEntry("body_r", "aaaa0003", Dds("r.dds")),
        });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Contains("[TextureOverride_Retex_body_a]\nhash = aaaa0001\nthis = Resource_Rtx0\n", ini);
        Assert.Contains("[TextureOverride_Retex_body_n]\nhash = aaaa0002\nthis = Resource_Rtx1\n", ini);
        Assert.Contains("[TextureOverride_Retex_body_r]\nhash = aaaa0003\nthis = Resource_Rtx2\n", ini);
    }

    [Fact]
    public void One_replacement_shared_by_two_stock_textures_is_copied_once()
    {
        string outDir = Path.Combine(_root, "out");
        string shared = Dds("shared.dds");
        new MigotoEmitter().BuildOverlaysOnly(outDir, new[]
        {
            new RetexEntry("one", "aaaa0001", shared),
            new RetexEntry("two", "aaaa0002", shared),
        });
        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));

        Assert.Equal(1, CountOf(ini, "filename = shared.dds"));
        Assert.Contains("[TextureOverride_Retex_one]\nhash = aaaa0001\nthis = Resource_Rtx0\n", ini);
        Assert.Contains("[TextureOverride_Retex_two]\nhash = aaaa0002\nthis = Resource_Rtx0\n", ini);
    }

    [Fact]
    public void Appended_to_a_pooled_build_the_pooled_prefix_stays_golden_identical()
    {
        // The golden fixture plus one retexture: the ini must START with the exact golden bytes, since the
        // pooled emission is a hard prefix.
        string dumps = Path.Combine(_root, "dumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64, boneHashes: new uint[] { 101, 102 });
        SyntheticPool.WritePartDump(Path.Combine(dumps, "beta"), seed: 60, verts: 64, boneHashes: new uint[] { 201, 202 });
        string donor = Path.Combine(_root, "donor");
        SyntheticPool.WriteDonor(donor, verts: 8, unionBones: 4, submeshes: 4);

        string outDir = Path.Combine(_root, "out");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            HideHashes = new[] { "cccc3333", "dddd4444" },
            Retextures = new[] { new RetexEntry("face_a", "ffff0000", Dds("face_a.dds")) },
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")), new PoolPart("beta", Path.Combine(dumps, "beta")) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111", ["beta"] = "bbbb2222" },
                    SubTextures = MigotoEmitterGoldenTests.MixedMaps(_root),
                    StockMaps = new[]
                    {
                        new StockMapTag("f1f1a1a1", StockMapKind.Albedo),
                        new StockMapTag("f2f2b2b2", StockMapKind.Normal),
                        new StockMapTag("f3f3c3c3", StockMapKind.Rmo),
                    },
                },
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        string golden = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(GoldenSelfPath())!, "golden", "mod.ini"));
        Assert.StartsWith(golden, ini);
        Assert.Contains("[TextureOverride_Retex_face_a]\nhash = ffff0000\n", ini);
    }

    private static string GoldenSelfPath([System.Runtime.CompilerServices.CallerFilePath] string self = "") => self;

    [Fact]
    public void A_captured_anchor_carries_every_scoped_image_inside_its_own_section()
    {
        // The scoped anchor's ib is already a pipeline capture hash, so that section owns it: BOTH images'
        // blocks run there rather than minting an override on a hash that already has one. They share the
        // single probe and differ only in their latch gate, in the order the entry lists them.
        string dumps = Path.Combine(_root, "capdumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64, boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "capdonor");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2);

        string outDir = Path.Combine(_root, "capout");
        new MigotoEmitter().Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                    DonorDir = donor,
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                },
            },
            ScopedRetextures = new[]
            {
                new ScopedRetexEntry("face_a", "ffff0000", new[]
                {
                    new ScopedRetexImage(Dds("one.dds"), new[] { new ScopedAnchor("aaaa1111", "one", "outfit_a") }),
                    new ScopedRetexImage(Dds("two.dds"), new[] { new ScopedAnchor("aaaa1111", "two", "outfit_b") }),
                }),
            },
            Latches = new[]
            {
                new WitnessLatch("outfit_a", new[] { "1111aaaa" }),
                new WitnessLatch("outfit_b", new[] { "2222bbbb" }),
            },
        });

        string ini = File.ReadAllText(Path.Combine(outDir, "mod.ini"));
        ModBuilderTests.AssertNoDuplicateSections(ini);
        Assert.DoesNotContain("[TextureOverride_RetexScope_", ini);

        string cap = ini[ini.IndexOf("hash = aaaa1111", StringComparison.Ordinal)..];
        cap = cap[..cap.IndexOf("\n\n", StringComparison.Ordinal)];
        Assert.Contains("Resource_RtxSave0 = ref ps-t0\n", cap);
        Assert.Equal(1, CountOf(cap, "$zz_rslot = -1"));
        Assert.Contains($"if $zz_rt == {MigotoEmitter.RetexTag("ffff0000")}\n$zz_rslot = 0\nendif\n", cap);
        Assert.Contains("if $zz_gate_outfit_a == 1\nif $zz_rslot == 0\nps-t0 = Resource_Rtx0\nendif\n", cap);
        Assert.Contains("if $zz_gate_outfit_b == 1\nif $zz_rslot == 0\nps-t0 = Resource_Rtx1\nendif\n", cap);
        Assert.True(cap.IndexOf("$zz_gate_outfit_a", StringComparison.Ordinal)
            < cap.IndexOf("$zz_gate_outfit_b", StringComparison.Ordinal), "images emit in the entry's order");
        Assert.Contains("post ps-t0 = Resource_RtxSave0\n", cap);
    }

    [Fact]
    public void Loud_failures_duplicate_hash_and_basename_collision()
    {
        var e1 = Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().BuildOverlaysOnly(Path.Combine(_root, "o1"), new[]
            {
                new RetexEntry("x", "aaaa0000", Dds("x.dds")),
                new RetexEntry("y", "aaaa0000", Dds("y.dds")),
            }));
        Assert.Contains("both override texture hash aaaa0000", e1.Message);

        string subdir = Path.Combine(_root, "other");
        Directory.CreateDirectory(subdir);
        string a = Dds("same.dds");
        string b = Path.Combine(subdir, "same.dds");
        File.Copy(a, b);
        var e2 = Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().BuildOverlaysOnly(Path.Combine(_root, "o2"), new[]
            {
                new RetexEntry("x", "aaaa0000", a),
                new RetexEntry("y", "bbbb0000", b),
            }));
        Assert.Contains("basename", e2.Message);
    }

    // ---- the derived slot tag has to be unique inside one build -----------------------------------

    // The tag is a hash remainder mod 15,000,000, so these two stock hashes derive one value. Every
    // probe in the emission compares tag VALUES, and a shared one is indistinguishable at draw time.
    private const string TagTwin1 = "00000001", TagTwin2 = "00e4e1c1";

    [Fact]
    public void Two_scoped_stock_textures_deriving_one_tag_refuse()
    {
        Assert.Equal(MigotoEmitter.RetexTag(TagTwin1), MigotoEmitter.RetexTag(TagTwin2));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().BuildOverlaysOnly(Path.Combine(_root, "tagdupe"), entries: null,
                scopedEntries: new[]
                {
                    new ScopedRetexEntry("one", TagTwin1, new[]
                    {
                        new ScopedRetexImage(Dds("one.dds"), new[] { new ScopedAnchor("aaaa1111", "one") }),
                    }, "body"),
                    new ScopedRetexEntry("two", TagTwin2, new[]
                    {
                        new ScopedRetexImage(Dds("two.dds"), new[] { new ScopedAnchor("bbbb2222", "two") }),
                    }, "cloth1"),
                }));

        Assert.Contains("same slot tag", ex.Message);
        // the parts are what the author can find on the change list; the hashes appear nowhere else
        Assert.Contains($"{TagTwin1} on body", ex.Message);
        Assert.Contains($"{TagTwin2} on cloth1", ex.Message);
        // both hashes came in as retextures, so both are changes the author can leave out — named in the
        // words the change row itself shows
        Assert.EndsWith("Leave one row's new textures out of the build.", ex.Message);
    }

    [Fact]
    public void A_hidden_mesh_claimed_as_a_scoped_anchor_refuses_on_the_overlay_route()
    {
        // both verbs want a TextureOverride on the same ib hash, and the ini parse would keep only one:
        // the mesh either still draws or its retexture never fires, decided by section order. Refuse.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().BuildOverlaysOnly(Path.Combine(_root, "hidescope"), entries: null,
                hideHashes: new[] { "abcd1234" },
                scopedEntries: new[]
                {
                    new ScopedRetexEntry("skin_d", "f0f0f0f0", new[]
                    {
                        new ScopedRetexImage(Dds("hs.dds"),
                            new[] { new ScopedAnchor("abcd1234", "vesna_body_lod0") }),
                    }),
                }));

        Assert.Contains("'vesna_body_lod0' is hidden", ex.Message);
        Assert.Contains("'skin_d' is retextured on its draws", ex.Message);
        Assert.Contains("Drop the Hide or that texture edit", ex.Message);
    }

    [Fact]
    public void A_hidden_mesh_claimed_as_a_scoped_anchor_refuses_on_the_pooled_route()
    {
        string dumps = Path.Combine(_root, "hidescopedumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64, boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "hidescopedonor");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().Build(new PoolBuildRequest
            {
                OutDir = Path.Combine(_root, "hidescopeout"),
                HideHashes = new[] { "abcd1234" },
                Pipelines = new[]
                {
                    new ReplacePipeline
                    {
                        Suffix = "swap",
                        Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                        DonorDir = donor,
                        CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                    },
                },
                ScopedRetextures = new[]
                {
                    new ScopedRetexEntry("skin_d", "f0f0f0f0", new[]
                    {
                        new ScopedRetexImage(Dds("hsp.dds"),
                            new[] { new ScopedAnchor("abcd1234", "vesna_body_lod0") }),
                    }),
                },
            }));

        Assert.Contains("'vesna_body_lod0' is hidden", ex.Message);
        Assert.Contains("Drop the Hide or that texture edit", ex.Message);
    }

    [Fact]
    public void Two_slot_tagged_stock_maps_deriving_one_tag_refuse()
    {
        // A Replace draw accepts a tagged hash's derived tag as its own kind's probe answer, so the
        // collision reaches the pooled path through those acceptance lines with no retexture in sight.
        string dumps = Path.Combine(_root, "tagdumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64, boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "tagdonor");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().Build(new PoolBuildRequest
            {
                OutDir = Path.Combine(_root, "tagout"),
                Pipelines = new[]
                {
                    new ReplacePipeline
                    {
                        Suffix = "swap",
                        Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                        DonorDir = donor,
                        CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                        StockMaps = new[]
                        {
                            new StockMapTag(TagTwin1, StockMapKind.Albedo, "body"),
                            new StockMapTag(TagTwin2, StockMapKind.Normal, "body"),
                        },
                    },
                },
            }));

        // both are the anchor part's own stock maps, so the part the author has to find is named twice
        Assert.Contains($"{TagTwin1} on body", ex.Message);
        Assert.Contains($"{TagTwin2} on body", ex.Message);
        // no retexture is in this build: the fix must not send the author hunting for one to drop
        Assert.DoesNotContain("retexture", ex.Message);
        Assert.DoesNotContain("new textures", ex.Message);
        Assert.EndsWith("Leave a row with a new mesh out of the build.", ex.Message);
    }

    [Fact]
    public void A_twin_guards_minted_tag_colliding_with_a_scoped_tag_refuses()
    {
        // The guard's tag section carries a value derived the same way a scoped retexture's does. Sharing
        // one value would let the scoped texture identify the wrong sibling, so the walk that refuses over
        // the other tag families covers this one too.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().BuildOverlaysOnly(Path.Combine(_root, "tagtwinguard"), entries: null,
                hideHashes: new[] { "aaaa1111" },
                scopedEntries: new[]
                {
                    new ScopedRetexEntry("one", TagTwin1, new[]
                    {
                        new ScopedRetexImage(Dds("one.dds"), new[] { new ScopedAnchor("bbbb2222", "one") }),
                    }, "body"),
                },
                twinGuards: new[]
                {
                    new TwinGuard("aaaa1111", "zz_tw_aaaa1111", new[] { 1 }, new[]
                    {
                        new TwinProbeTag(TagTwin2, MigotoEmitter.RetexTag(TagTwin2), 1),
                    }),
                }));

        Assert.Contains("same slot tag", ex.Message);
        Assert.Contains($"{TagTwin1} on body", ex.Message);
        Assert.Contains(TagTwin2, ex.Message);
    }

    [Fact]
    public void A_tag_collision_with_no_part_labels_still_names_both_ways_out()
    {
        // A caller with no labels (a synthetic build, a fixture) gets the same three arms without them: the
        // refusal degrades to the hashes rather than to a sentence with a hole in it.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().BuildOverlaysOnly(Path.Combine(_root, "tagnolabel"), entries: null,
                scopedEntries: new[]
                {
                    new ScopedRetexEntry("one", TagTwin1, new[]
                    {
                        new ScopedRetexImage(Dds("one.dds"), new[] { new ScopedAnchor("aaaa1111", "one") }),
                    }),
                    new ScopedRetexEntry("two", TagTwin2, new[]
                    {
                        new ScopedRetexImage(Dds("two.dds"), new[] { new ScopedAnchor("bbbb2222", "two") }),
                    }),
                }));

        Assert.Contains($"Stock textures {TagTwin1} and {TagTwin2} derive the same slot tag", ex.Message);
        Assert.EndsWith("Leave one row's new textures out of the build.", ex.Message);
    }

    [Fact]
    public void A_slot_tag_colliding_with_a_retexture_names_both_ways_out()
    {
        string dumps = Path.Combine(_root, "tagmixdumps");
        SyntheticPool.WritePartDump(Path.Combine(dumps, "alpha"), seed: 10, verts: 64, boneHashes: new uint[] { 101, 102 });
        string donor = Path.Combine(_root, "tagmixdonor");
        SyntheticPool.WriteDonor(donor, verts: 6, unionBones: 2);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new MigotoEmitter().Build(new PoolBuildRequest
            {
                OutDir = Path.Combine(_root, "tagmixout"),
                Pipelines = new[]
                {
                    new ReplacePipeline
                    {
                        Suffix = "swap",
                        Parts = new[] { new PoolPart("alpha", Path.Combine(dumps, "alpha")) },
                        DonorDir = donor,
                        CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa1111" },
                        StockMaps = new[] { new StockMapTag(TagTwin1, StockMapKind.Albedo, "body") },
                    },
                },
                ScopedRetextures = new[]
                {
                    new ScopedRetexEntry("two", TagTwin2, new[]
                    {
                        new ScopedRetexImage(Dds("mix.dds"), new[] { new ScopedAnchor("bbbb2222", "two") }),
                    }, "cloth1"),
                },
            }));

        Assert.Contains("same slot tag", ex.Message);
        Assert.Contains($"{TagTwin1} on body", ex.Message);
        Assert.Contains($"{TagTwin2} on cloth1", ex.Message);
        // each way out names WHICH part is which: one row ships new textures, the other a new mesh
        Assert.EndsWith("Leave the new textures on cloth1 or the new mesh on body out of the build.",
            ex.Message);
    }

    private static int CountOf(string text, string token)
    {
        int n = 0;
        for (int i = text.IndexOf(token, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(token, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
