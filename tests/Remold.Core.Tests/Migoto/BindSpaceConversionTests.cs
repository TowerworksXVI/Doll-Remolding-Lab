using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsTools.NET;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Skeleton;
using Xunit;

namespace Remold.Core.Tests.Migoto;

/// <summary>
/// Pool parts authored in bind spaces one rigid rotation apart — a body upright, its cloth face-down.
/// The pooled union keeps one bindpose and one palette per bone, so a part is restated in the ANCHOR's
/// space (bind and geometry together). What no rigid restatement explains — a translated or scaled mesh
/// space, a helper bone one part binds elsewhere, a re-bound tier — is converted row by row onto the
/// pool's <see cref="BindReference"/> instead, and the tests here play the game's half against the
/// shipped files to prove the converted rows are the ones the donor is owed.
/// </summary>
public class BindSpaceConversionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-bindspace-" + Guid.NewGuid().ToString("N"));

    public BindSpaceConversionTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly uint A = BoneTable.Hash("Hair01_L/Bone_M");
    private static readonly uint B = BoneTable.Hash("Spine1_M");
    private static readonly uint C = BoneTable.Hash("Spine2_M");
    private static readonly uint D = BoneTable.Hash("Chest_M");
    private static readonly uint E = BoneTable.Hash("Head_M");

    /// <summary>−90° about X, row-vector, exact — the measured shape of a face-down part's delta.</summary>
    private static readonly Matrix4x4 QuarterTurnX = new(
        1, 0, 0, 0,
        0, 0, -1, 0,
        0, 1, 0, 0,
        0, 0, 0, 1);

    private static Matrix4x4 T(float x, float y, float z) => Matrix4x4.CreateTranslation(x, y, z);

    /// <summary>The bind each bone carries wherever a fixture below states one. Per-bone bindposes that do
    /// NOT commute with a rotation: identical bindposes would make the left and right quotients agree, and
    /// only the left one is the part→reference relation.</summary>
    private static readonly (uint Hash, Matrix4x4 Bind)[] Rig =
    {
        (A, T(1, 2, 3)), (B, T(4, 5, 6)), (C, T(7, 8, 9)), (D, T(-2, 7, 0.5f)), (E, T(0.25f, -3, 8)),
    };

    private static Matrix4x4 BindOf(uint hash) => Rig.First(r => r.Hash == hash).Bind;

    /// <summary>alpha (bones A,B,C,D — the anchor) + beta (bones B,C,D,E), both in ONE bind space. The
    /// overlap is three bones, the floor a conversion delta has to be fitted to, and each part keeps one
    /// bone of its own so the union is a real union.</summary>
    private (string Alpha, string Beta) SingleSpacePool(string tag)
    {
        string ad = WriteRigged(tag + "_alpha", 1, 32, new[] { A, B, C, D });
        string bd = WriteRigged(tag + "_beta", 2, 16, new[] { B, C, D, E });
        return (ad, bd);
    }

    /// <summary>A part dump rigged to <paramref name="bones"/>, each carrying its <see cref="Rig"/> bind.</summary>
    private string WriteRigged(string name, int seed, int verts, uint[] bones)
    {
        string dir = Path.Combine(_root, name);
        SyntheticPool.WritePartDump(dir, seed, verts, bones);
        SyntheticPool.NonZeroPositions(dir);
        foreach (var h in bones) SyntheticPool.SetBindPose(dir, h, BindOf(h));
        return dir;
    }

    private PoolBuildRequest Request(string tag, string alpha, string beta, out string outDir,
        PoolTier[]? tiers = null, string? aKey = null, string? bKey = null,
        Matrix4x4? aRest = null, Matrix4x4? bRest = null)
    {
        outDir = Path.Combine(_root, tag + "_out");
        return new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[]
                    {
                        new PoolPart("alpha", alpha, aKey, aRest),
                        new PoolPart("beta", beta, bKey, bRest),
                    },
                    Anchor = "alpha",
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001", ["beta"] = "bbbb0001" },
                    Tiers = tiers,
                },
            },
        };
    }

    /// <summary>The request with the bind reference the build would hand its one pipeline
    /// (<see cref="BindReference.For"/> over its dumps, the anchor the target), each part placed where
    /// <paramref name="placements"/> says — identity for a part it doesn't name, which is where a part authored
    /// in the anchor's own space sits.</summary>
    private static PoolBuildRequest Referenced(PoolBuildRequest req, IReadOnlyDictionary<string, Matrix4x4>? placements = null)
    {
        var pipe = req.Pipelines.Single();
        BindReference.Part PartOf(PoolPart p)
        {
            var (hashes, binds) = PosedPoolSim.ReadBinds(p.DumpDir);
            var placement = placements is not null && placements.TryGetValue(p.Name, out var g) ? g : Matrix4x4.Identity;
            return new BindReference.Part(p.Name, hashes, binds, PosedPoolSim.Posed(p.DumpDir, hashes),
                new Lazy<(Matrix4x4?, string?)>(() => (placement, null)));
        }
        var resolution = BindReference.For(PartOf(pipe.Parts.Single(p => p.Name == pipe.Anchor)),
            pipe.Parts.Select(PartOf).ToList(), carryHidden: true);
        Assert.Empty(resolution.Unplaced);
        return req with { Pipelines = new[] { pipe with { ReferenceBinds = resolution.Reference } } };
    }

    private static void AssertSameTree(string expected, string actual)
    {
        var want = Directory.GetFiles(expected).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var got = Directory.GetFiles(actual).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(want, got);
        foreach (var n in want)
            Assert.True(
                File.ReadAllBytes(Path.Combine(expected, n!)).SequenceEqual(File.ReadAllBytes(Path.Combine(actual, n!))),
                $"{n} differs");
    }

    /// <summary>The game's half, played against the shipped files (<see cref="PosedPoolSim"/>): every mesh
    /// posed with its OWN binds, the shipped operators run over it, and the worst element error of any union
    /// row against the skin matrix the donor is owed — <c>stated(bone) · World(bone)</c>. Every union row must
    /// have a writer, or a passing number would be vouching for rows nobody checked.
    /// <para>A slot past the union is a reserved WITNESS recovery — the bone a mate and the anchor both
    /// recover so the convert can solve the mate's draw space from the pair. It is held to the same
    /// statement: a witness row left under the mate's own bind would bend every row that mate owns.</para>
    /// </summary>
    private static float WorstRowError(string outDir, (string Name, string Dir)[] meshes, uint[] unionOrder,
        Func<uint, Matrix4x4> stated, uint? witness = null)
    {
        float worst = 0;
        var written = new HashSet<uint>();
        foreach (var (name, dir) in meshes)
            foreach (var (slot, row) in PosedPoolSim.Recover(outDir, "swap", name, dir))
            {
                uint h;
                if (slot < unionOrder.Length) { written.Add(slot); h = unionOrder[slot]; }
                else h = witness ?? throw new Xunit.Sdk.XunitException($"{name} writes reserved slot {slot}, and the test names no witness bone");
                worst = MathF.Max(worst, PosedPoolSim.Error(row, stated(h) * PosedPoolSim.WorldOf(h)));
            }
        Assert.Equal(unionOrder.Length, written.Count);
        return worst;
    }

    /// <summary>What an operator recovers when nothing needed converting — the floor a converted pool is
    /// held to, since the fold must not cost the recovery its accuracy.</summary>
    private float UnconvertedFloor(string tag)
    {
        var (a, b) = SingleSpacePool(tag);
        new MigotoEmitter().Build(Referenced(Request(tag, a, b, out string outDir)));
        return WorstRowError(outDir, new[] { ("alpha", a), ("beta", b) }, new[] { A, B, C, D, E }, BindOf, B);
    }

    /// <summary>A converted pool recovers every row to within a small multiple of the unconverted floor —
    /// and the floor itself is far below the smallest bind difference any fixture here introduces (0.3), so
    /// a row stated under the wrong bind cannot hide inside the allowance.</summary>
    private void AssertRowsExact(float worst, float floor)
    {
        Assert.True(floor < 1e-3f, $"the unconverted floor itself is {floor}");
        Assert.True(worst <= MathF.Max(8 * floor, 1e-4f), $"converted rows are off by {worst} (floor {floor})");
    }

    // ---- the conversion ----------------------------------------------------------------------------

    [Fact]
    public void A_part_in_a_rotated_bind_space_builds_as_if_it_were_authored_in_the_anchors()
    {
        var (refA, refB) = SingleSpacePool("ref");
        new MigotoEmitter().Build(Referenced(Request("ref", refA, refB, out string refOut)));

        var (cvA, cvB) = SingleSpacePool("cv");
        SyntheticPool.AuthorInSpace(cvB, QuarterTurnX);
        new MigotoEmitter().Build(Referenced(Request("cv", cvA, cvB, out string cvOut),
            new Dictionary<string, Matrix4x4> { ["beta"] = QuarterTurnX }));

        AssertSameTree(refOut, cvOut);
    }

    [Fact]
    public void A_mate_in_a_sheared_mesh_space_keeps_its_own_bind_for_the_bones_it_alone_poses()
    {
        // a shear stretches the mate's axes, so its placement is a scaled one: nothing is carried through it
        // (the game scales a part only to keep it out of sight), and E, which beta alone poses, keeps beta's
        // own bind — beta ships nothing that needs converting
        var (ad, bd) = SingleSpacePool("shear");
        var shear = Matrix4x4.Identity;
        shear.M12 = 0.3f;
        SyntheticPool.MapBindPoses(bd, b => shear * b);

        new MigotoEmitter().Build(Referenced(Request("shear", ad, bd, out string outDir),
            new Dictionary<string, Matrix4x4> { ["beta"] = shear }));

        AssertRowsExact(WorstRowError(outDir, new[] { ("alpha", ad), ("beta", bd) },
            new[] { A, B, C, D, E }, h => h == E ? shear * BindOf(E) : BindOf(h), B), UnconvertedFloor("shearfloor"));
        Assert.False(File.Exists(Path.Combine(outDir, "beta_cpinv_swap.buf")));
        Assert.True(File.Exists(Path.Combine(outDir, "beta_cpinv.buf")));   // beta binds its solved operator
    }

    [Fact]
    public void A_mate_whose_whole_mesh_is_offset_is_converted_bones_it_alone_has_included()
    {
        // the measured shape of a wardrobe family sitting centimetres off its base body: every shared bone
        // differs by one translation, and the bones only the mate has must ride that same relation
        var (ad, bd) = SingleSpacePool("offset");
        SyntheticPool.MapBindPoses(bd, b => T(12, 0, 0) * b);

        new MigotoEmitter().Build(Referenced(Request("offset", ad, bd, out string outDir),
            new Dictionary<string, Matrix4x4> { ["beta"] = T(12, 0, 0) }));

        AssertRowsExact(WorstRowError(outDir, new[] { ("alpha", ad), ("beta", bd) },
            new[] { A, B, C, D, E }, BindOf, B), UnconvertedFloor("offsetfloor"));
    }

    [Fact]
    public void A_helper_bone_one_part_binds_elsewhere_is_stated_under_the_anchors_bind()
    {
        // the measured shape of a stale helper bone: every other shared bone agrees, so the two meshes share
        // a space and only that bone's rows convert. The anchor poses it, so the ANCHOR's bind is the
        // statement — an unedited send-back must skin exactly as the stock part does.
        var (ad, bd) = SingleSpacePool("helper");
        SyntheticPool.SetBindPose(bd, C, T(0.3f, 0, 0) * BindOf(C));

        var result = new MigotoEmitter().Build(Referenced(Request("helper", ad, bd, out string outDir)));

        AssertRowsExact(WorstRowError(outDir, new[] { ("alpha", ad), ("beta", bd) },
            new[] { A, B, C, D, E }, BindOf, B), UnconvertedFloor("helperfloor"));
        // beta ships only E's rows, and it binds E as the rig does: nothing of its that ships needed
        // converting, so it binds the solved operator like any other pool
        Assert.False(File.Exists(Path.Combine(outDir, "beta_cpinv_swap.buf")));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("elsewhere than the replacement"));
    }

    [Fact]
    public void The_anchors_own_stale_bone_keeps_the_anchors_bind()
    {
        // the same stale bone seen from the other side: the ANCHOR is the part binding it off the rig, and
        // the mate — listed second, binding it as the rig does — must not win the statement. Stock parity
        // says the anchor's stands: its rows ship as solved, under the bind its own geometry is skinned with.
        var (ad, bd) = SingleSpacePool("anchorstale");
        SyntheticPool.SetBindPose(ad, B, T(0.3f, 0, 0) * BindOf(B));

        var result = new MigotoEmitter().Build(Referenced(Request("anchorstale", ad, bd, out string outDir)));

        AssertRowsExact(WorstRowError(outDir, new[] { ("alpha", ad), ("beta", bd) }, new[] { A, B, C, D, E },
            h => h == B ? T(0.3f, 0, 0) * BindOf(B) : BindOf(h), B), UnconvertedFloor("anchorstalefloor"));
        Assert.False(File.Exists(Path.Combine(outDir, "alpha_cpinv_swap.buf")));
        Assert.DoesNotContain(result.Diagnostics, d => d.Contains("alpha binds"));
    }

    [Fact]
    public void A_pool_whose_binds_agree_builds_byte_identically_to_one_that_differs_below_the_gate()
    {
        var (exA, exB) = SingleSpacePool("exact");
        new MigotoEmitter().Build(Referenced(Request("exact", exA, exB, out string exactOut)));

        var (nrA, nrB) = SingleSpacePool("near");
        var nudge = Matrix4x4.Identity;
        nudge.M12 = 1e-7f;
        SyntheticPool.MapBindPoses(nrB, b => nudge * b);
        new MigotoEmitter().Build(Referenced(Request("near", nrA, nrB, out string nearOut)));

        AssertSameTree(exactOut, nearOut);
    }

    // ---- measured rests: the delta two measurements compose, where fitting has too little to hold ----

    /// <summary>alpha (A,B,C,D — the anchor) + gamma (D,E): ONE shared bone, below the corroboration
    /// floor, so only a measured-rest delta can restate gamma. The real shape: an outfit's hair sharing
    /// just the head bone with the anchor.</summary>
    private (string Alpha, string Gamma) OneSharedBonePool(string tag)
    {
        string ad = WriteRigged(tag + "_alpha", 1, 32, new[] { A, B, C, D });
        string gd = WriteRigged(tag + "_gamma", 2, 16, new[] { D, E });
        return (ad, gd);
    }

    [Fact]
    public void A_part_sharing_one_bone_builds_when_measured_rests_relate_the_spaces()
    {
        var (refA, refG) = OneSharedBonePool("mref");
        new MigotoEmitter().Build(Referenced(Request("mref", refA, refG, out string refOut)));

        var (cvA, cvG) = OneSharedBonePool("mcv");
        SyntheticPool.AuthorInSpace(cvG, QuarterTurnX);
        new MigotoEmitter().Build(Referenced(Request("mcv", cvA, cvG, out string cvOut,
            aRest: Matrix4x4.Identity, bRest: QuarterTurnX),
            new Dictionary<string, Matrix4x4> { ["beta"] = QuarterTurnX }));

        AssertSameTree(refOut, cvOut);
    }

    /// <summary>What the one-shared-bone pool recovers with nothing to convert.</summary>
    private float OneSharedBoneFloor(string tag)
    {
        var (a, g) = OneSharedBonePool(tag);
        new MigotoEmitter().Build(Referenced(Request(tag, a, g, out string outDir)));
        return WorstRowError(outDir, new[] { ("alpha", a), ("beta", g) }, new[] { A, B, C, D, E }, BindOf, D);
    }

    [Fact]
    public void A_part_sharing_one_bone_is_converted_by_its_placement_when_nothing_restates_it()
    {
        // below the corroboration floor the rigid RESTATEMENT still declines to rotate a part's geometry on
        // one bone's say-so — but the rows need no restatement: the parts' placements relate the two spaces,
        // and gamma's own bone rides that relation back into the anchor's space
        var (ad, gd) = OneSharedBonePool("mfloor");
        SyntheticPool.AuthorInSpace(gd, QuarterTurnX);

        new MigotoEmitter().Build(Referenced(Request("mfloor", ad, gd, out string outDir),
            new Dictionary<string, Matrix4x4> { ["beta"] = QuarterTurnX }));

        AssertRowsExact(WorstRowError(outDir, new[] { ("alpha", ad), ("beta", gd) },
            new[] { A, B, C, D, E }, BindOf, D), OneSharedBoneFloor("mfloorfloor"));
    }

    [Fact]
    public void A_part_whose_mesh_space_is_translated_is_converted_rather_than_refused()
    {
        // measured rests that relate by a translation rule a bind-space ROTATION out, so nothing is
        // restated — and the rows convert all the same
        var (ad, gd) = OneSharedBonePool("mtrans");
        SyntheticPool.MapBindPoses(gd, b => T(0.4f, 0, 0) * b);

        new MigotoEmitter().Build(Referenced(Request("mtrans", ad, gd, out string outDir,
            aRest: Matrix4x4.Identity, bRest: T(0.4f, 0, 0)),
            new Dictionary<string, Matrix4x4> { ["beta"] = T(0.4f, 0, 0) }));

        AssertRowsExact(WorstRowError(outDir, new[] { ("alpha", ad), ("beta", gd) },
            new[] { A, B, C, D, E }, BindOf, D), OneSharedBoneFloor("mtransfloor"));
    }

    [Fact]
    public void Parts_sharing_no_bone_are_related_by_their_placements()
    {
        // the measured shape of a mask modelled away from the head it sits on: nothing is shared, so nothing
        // was ever compared, and the mask's bones were posed in the mask's own space. Each part's placement
        // in the rig says where its mesh space sits, and the two relate through them.
        string ad = WriteRigged("noshare_alpha", 1, 32, new[] { A, B, C, D });
        string pd = WriteRigged("noshare_prop", 2, 8, new[] { E });
        var propSpace = Matrix4x4.CreateRotationY(0.6f) * T(0.5f, 0.71f, 0);
        SyntheticPool.MapBindPoses(pd, b => propSpace * b);

        new MigotoEmitter().Build(Referenced(Request("noshare", ad, pd, out string outDir),
            new Dictionary<string, Matrix4x4> { ["beta"] = propSpace }));

        float worst = WorstRowError(outDir, new[] { ("alpha", ad), ("beta", pd) }, new[] { A, B, C, D, E }, BindOf);
        Assert.True(worst < 1e-3f, $"rows are off by {worst}");
    }

    /// <summary>One dumped mesh as the reference rule reads it, placed as <paramref name="space"/> says.</summary>
    private static BindReference.Part PartOf(string name, string dir, (Matrix4x4?, string?) space)
    {
        var (hashes, binds) = PosedPoolSim.ReadBinds(dir);
        return new BindReference.Part(name, hashes, binds, PosedPoolSim.Posed(dir, hashes),
            new Lazy<(Matrix4x4?, string?)>(() => space));
    }

    [Fact]
    public void A_bone_only_an_unplaceable_part_poses_has_no_reference_and_says_why()
    {
        string ad = WriteRigged("unplaced_alpha", 1, 32, new[] { A, B, C, D });
        string pd = WriteRigged("unplaced_prop", 2, 8, new[] { E });

        var resolution = BindReference.For(PartOf("alpha", ad, (Matrix4x4.Identity, null)),
            new[] { PartOf("prop", pd, (null, "its skeleton can't be read")) }, carryHidden: true);

        Assert.False(resolution.Reference.ContainsKey(E));
        var (source, problem, cause) = resolution.Unplaced[E];
        Assert.Equal("prop", source);
        Assert.Contains("its skeleton can't be read", problem);
        Assert.Equal(BindReference.Unplacement.Source, cause);
        // the anchor's own bones need no placement at all
        Assert.Equal(BindOf(A), resolution.Reference[A]);
    }

    /// <summary>The replaced part's own placement is what every carry starts from: when it can't be read, no
    /// bone another part lends has a reference, each says why, and the part's own bones need none.</summary>
    [Fact]
    public void When_the_replaced_part_cannot_be_placed_no_lent_bone_has_a_reference()
    {
        string ad = WriteRigged("anchorless_alpha", 1, 32, new[] { A, B, C, D });
        string pd = WriteRigged("anchorless_prop", 2, 8, new[] { E });

        var resolution = BindReference.For(PartOf("alpha", ad, (null, "its skeleton can't be read")),
            new[] { PartOf("prop", pd, (Matrix4x4.Identity, null)) }, carryHidden: true);

        Assert.False(resolution.Reference.ContainsKey(E));
        Assert.Contains("'alpha': its skeleton can't be read", resolution.Unplaced[E].Problem);
        Assert.Equal(BindReference.Unplacement.Target, resolution.Unplaced[E].Cause);
        Assert.Equal(BindOf(A), resolution.Reference[A]);
    }

    /// <summary>Sources are asked in order, and a bone the first can't place is placed by the next that
    /// can.</summary>
    [Fact]
    public void A_later_part_places_a_bone_an_earlier_one_could_not()
    {
        string ad = WriteRigged("later_alpha", 1, 32, new[] { A, B, C, D });
        string first = WriteRigged("later_first", 2, 8, new[] { E });
        string second = WriteRigged("later_second", 3, 8, new[] { E });
        var offset = T(0.04f, 0, 0);
        SyntheticPool.MapBindPoses(second, b => offset * b);

        var resolution = BindReference.For(PartOf("alpha", ad, (Matrix4x4.Identity, null)), new[]
        {
            PartOf("first", first, (null, "its skeleton can't be read")),
            PartOf("second", second, (offset, null)),
        }, carryHidden: true);

        Assert.Empty(resolution.Unplaced);
        Assert.True(BindReference.SameBind(BindOf(E), resolution.Reference[E]));
    }

    /// <summary>Two parts driven by different skeletons (a summoned creature beside its summoner) are
    /// animated apart, so their placements relate them only where the bones both weight agree, or, sharing
    /// none, where the rig places them together: weight across two skeletons is refused where the game
    /// files don't show where one sits against the other.</summary>
    [Fact]
    public void Two_skeletons_relate_only_where_shared_bones_agree_or_the_rig_places_them_together()
    {
        string ad = WriteRigged("skel_alpha", 1, 32, new[] { A, B, C, D });
        string agree = WriteRigged("skel_agree", 2, 16, new[] { D, E });
        string clash = WriteRigged("skel_clash", 3, 16, new[] { D, E });
        SyntheticPool.SetBindPose(clash, D, T(8, 0, 0) * BindOf(D));
        string apart = WriteRigged("skel_apart", 4, 8, new[] { E });
        BindReference.Part Driven(string name, string dir, Matrix4x4 placement, string? skeleton)
        {
            var (hashes, binds) = PosedPoolSim.ReadBinds(dir);
            return new BindReference.Part(name, hashes, binds, PosedPoolSim.Posed(dir, hashes),
                new Lazy<(Matrix4x4?, string?)>(() => (placement, null)), new Lazy<string?>(() => skeleton));
        }
        var alpha = Driven("alpha", ad, Matrix4x4.Identity, "body");
        BindReference.Resolution With(BindReference.Part source) => BindReference.For(alpha, new[] { source },
            carryHidden: true);

        Assert.Equal(BindOf(E), With(Driven("agree", agree, Matrix4x4.Identity, "summon")).Reference[E]);
        var clashed = With(Driven("clash", clash, Matrix4x4.Identity, "summon")).Unplaced[E];
        Assert.Contains("another skeleton", clashed.Problem);
        Assert.Equal(BindReference.Unplacement.Skeletons, clashed.Cause);
        // sharing no bone: placed together, it lends its own bind; placed apart, nothing places it
        Assert.Equal(BindOf(E), With(Driven("apart", apart, Matrix4x4.Identity, "summon")).Reference[E]);
        Assert.True(With(Driven("apart", apart, T(1.1f, 0, 0), "summon")).Unplaced.ContainsKey(E));
        // a part naming no skeleton is set against none, and one skeleton is never checked against itself
        Assert.True(With(Driven("apart", apart, T(1.1f, 0, 0), null)).Reference.ContainsKey(E));
        Assert.True(With(Driven("apart", apart, T(1.1f, 0, 0), "body")).Reference.ContainsKey(E));
    }

    /// <summary>The correction is folded into whichever layout the operator ships in. A dense operator files
    /// bone i's four rows at <c>(4i + r)·N</c>, one coefficient per vertex; each output row becomes
    /// <c>Σ_s C[r,s] · row_s</c>, and a bone with no constant keeps its rows.</summary>
    [Fact]
    public void The_fold_restates_a_dense_operators_rows_bone_by_bone()
    {
        const int n = 3;
        var cpinv = Enumerable.Range(1, 2 * 4 * n).Select(i => (float)i).ToArray();
        var art = new MigotoEmitter.OperatorArt(cpinv, new bool[2], new[] { -1, -1 }, new uint[] { A, B },
            new[] { 0, 1 }, Array.Empty<string>(), Sel: null, Off: null, N: n);
        double[] c = { 2, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 1, 2, 3, 1 };

        var folded = MigotoEmitter.FoldConstants(art, new[] { null, c });

        Assert.Equal(cpinv.Take(4 * n), folded.Take(4 * n));
        for (int r = 0; r < 4; r++)
            for (int t = 0; t < n; t++)
            {
                double expected = 0;
                for (int s2 = 0; s2 < 4; s2++) expected += c[r * 4 + s2] * cpinv[4 * n + s2 * n + t];
                Assert.Equal((float)expected, folded[4 * n + r * n + t]);
            }
    }

    /// <summary>One dumped mesh placed as <paramref name="space"/> says, shown in Blender at
    /// <paramref name="display"/>.</summary>
    private static BindReference.Part PartOf(string name, string dir, (Matrix4x4?, string?) space,
        (Matrix4x4?, string?)? display)
    {
        var (hashes, binds) = PosedPoolSim.ReadBinds(dir);
        return new BindReference.Part(name, hashes, binds, PosedPoolSim.Posed(dir, hashes),
            new Lazy<(Matrix4x4?, string?)>(() => space), Display: display is { } shown
                ? new Lazy<(Matrix4x4?, string?)>(() => shown) : null);
    }

    /// <summary>A part the game starts shrunk opens in Blender centred at full size, so weight painted across
    /// it and another part is posed where Blender showed the two: a bone the hidden part lends is carried
    /// into the target by its display placement, and a bone it borrows is carried into it by the same one.
    /// Here the hidden part is shown at its own origin shifted by the centre, so the carry is that shift and
    /// nothing else.</summary>
    [Fact]
    public void A_hidden_part_lends_and_borrows_binds_carried_by_where_Blender_shows_it()
    {
        string ad = WriteRigged("centred_alpha", 1, 32, new[] { A, B, C, D });
        string pd = WriteRigged("centred_prop", 2, 8, new[] { E });
        var centre = new Vector3(0.5f, 1.25f, -0.25f);
        (Matrix4x4?, string?) hidden = (Matrix4x4.CreateScale(0.01f) * T(0.5f, 1.25f, 0), null);
        (Matrix4x4?, string?) shown = (T(-centre.X, -centre.Y, -centre.Z), null);
        (Matrix4x4?, string?) body = (Matrix4x4.Identity, null);

        var lent = BindReference.For(PartOf("alpha", ad, body, null),
            new[] { PartOf("prop", pd, hidden, shown) }, carryHidden: true);
        Assert.Empty(lent.Unplaced);
        Assert.True(BindReference.SameBind(T(centre.X, centre.Y, centre.Z) * BindOf(E), lent.Reference[E]));

        var borrowed = BindReference.For(PartOf("prop", pd, hidden, shown),
            new[] { PartOf("alpha", ad, body, null) }, carryHidden: true);
        Assert.Empty(borrowed.Unplaced);
        Assert.True(BindReference.SameBind(T(-centre.X, -centre.Y, -centre.Z) * BindOf(A), borrowed.Reference[A]));
        // the part's own bone keeps its own bind whichever relation the geometry was authored under
        Assert.Equal(BindOf(E), borrowed.Reference[E]);
    }

    /// <summary>Geometry returned before hidden parts opened centred was modelled against the part where the
    /// old export put it, so it builds as it did then: no bone crosses to or from a hidden part carried,
    /// whatever its display placement would say.</summary>
    [Fact]
    public void Geometry_authored_before_centring_keeps_a_hidden_parts_binds_as_stated()
    {
        string ad = WriteRigged("uncentred_alpha", 1, 32, new[] { A, B, C, D });
        string pd = WriteRigged("uncentred_prop", 2, 8, new[] { E });
        (Matrix4x4?, string?) hidden = (Matrix4x4.CreateScale(0.001f), null);
        (Matrix4x4?, string?) shown = (T(-3, 0, 0), null);

        var lent = BindReference.For(PartOf("alpha", ad, (Matrix4x4.Identity, null), null),
            new[] { PartOf("prop", pd, hidden, shown) }, carryHidden: false);

        Assert.Equal(BindOf(E), lent.Reference[E]);
        Assert.Empty(lent.Unplaced);
    }

    /// <summary>A prop the rig scales by some other amount (the 0.6 to 4 of a few enemy and summon props) is
    /// not a part the game starts hidden, so it never opens centred and its binds cross unchanged under
    /// either relation.</summary>
    [Fact]
    public void A_part_scaled_but_not_hidden_crosses_unchanged_under_either_relation()
    {
        string ad = WriteRigged("scaled_alpha", 1, 32, new[] { A, B, C, D });
        string pd = WriteRigged("scaled_prop", 2, 8, new[] { E });
        (Matrix4x4?, string?) scaled = (Matrix4x4.CreateScale(0.6f) * T(0.5f, 1.25f, 0), null);

        foreach (bool carryHidden in new[] { true, false })
        {
            var lent = BindReference.For(PartOf("alpha", ad, (Matrix4x4.Identity, null), null),
                new[] { PartOf("prop", pd, scaled, (T(-1, 0, 0), null)) }, carryHidden);
            Assert.Equal(BindOf(E), lent.Reference[E]);
            Assert.Empty(lent.Unplaced);
        }
        Assert.False(BindReference.Hidden(Matrix4x4.CreateScale(0.6f)));
        Assert.True(BindReference.Hidden(Matrix4x4.CreateScale(0.01f) * T(4, 5, 6)));
    }

    /// <summary>A hidden part whose display placement can't be said has nothing to carry a crossing bone
    /// by, so that bone has no reference and says why, rather than being posed where the old export put
    /// the part.</summary>
    [Fact]
    public void A_hidden_part_that_cannot_be_centred_places_no_crossing_bone()
    {
        string ad = WriteRigged("uncentrable_alpha", 1, 32, new[] { A, B, C, D });
        string pd = WriteRigged("uncentrable_prop", 2, 8, new[] { E });

        var lent = BindReference.For(PartOf("alpha", ad, (Matrix4x4.Identity, null), null),
            new[] { PartOf("prop", pd, (Matrix4x4.CreateScale(0.01f), null), null) }, carryHidden: true);

        Assert.False(lent.Reference.ContainsKey(E));
        Assert.Equal(BindReference.Unplacement.Source, lent.Unplaced[E].Cause);
        Assert.Contains("centre", lent.Unplaced[E].Problem);
    }

    /// <summary>The game keeps a prop shrunk out of sight in the rig's rest pose until an animation brings it
    /// on, so that placement says nothing about where the prop draws. Under the relation geometry returned
    /// before hidden parts opened centred was authored in, a bone crossing either way between a scaled part
    /// and another keeps the bind it is stated under.</summary>
    [Fact]
    public void A_part_the_rest_pose_scales_lends_and_borrows_binds_unchanged()
    {
        string ad = WriteRigged("hidden_alpha", 1, 32, new[] { A, B, C, D });
        string pd = WriteRigged("hidden_prop", 2, 8, new[] { E });
        // the measured shape: shrunk to 1% about where it was modelled, a hand's height up
        (Matrix4x4?, string?) hidden = (Matrix4x4.CreateScale(0.01f) * T(0.5f, 1.25f, 0), null);
        (Matrix4x4?, string?) body = (Matrix4x4.Identity, null);

        var lent = BindReference.For(PartOf("alpha", ad, body), new[] { PartOf("prop", pd, hidden) },
            carryHidden: false);
        Assert.Equal(BindOf(E), lent.Reference[E]);
        Assert.Empty(lent.Unplaced);

        var borrowed = BindReference.For(PartOf("prop", pd, hidden), new[] { PartOf("alpha", ad, body) },
            carryHidden: false);
        Assert.Equal(BindOf(A), borrowed.Reference[A]);
        Assert.Empty(borrowed.Unplaced);
    }

    /// <summary>Float rounding is not a scale: a placement whose axes are off 1 by a few parts in ten million,
    /// as every unscaled placement in the corpus is, still carries.</summary>
    [Fact]
    public void A_placement_scaled_only_by_float_rounding_still_carries()
    {
        string ad = WriteRigged("rounding_alpha", 1, 32, new[] { A, B, C, D });
        string pd = WriteRigged("rounding_prop", 2, 8, new[] { E });
        var offset = Matrix4x4.CreateScale(1.0000007f) * T(0.04f, 0, 0);

        var resolution = BindReference.For(PartOf("alpha", ad, (Matrix4x4.Identity, null)),
            new[] { PartOf("prop", pd, (offset, null)) }, carryHidden: true);

        Assert.True(Matrix4x4.Invert(offset, out var inverse));
        Assert.True(BindReference.SameBind(inverse * BindOf(E), resolution.Reference[E]));
        Assert.False(BindReference.SameBind(BindOf(E), resolution.Reference[E]));
    }

    // ---- tiers -------------------------------------------------------------------------------------

    /// <summary>A tier dump matching alpha's lod0 bind space, then re-authored <paramref name="delta"/>
    /// away from it.</summary>
    private string Tier(string tag, Func<Matrix4x4, Matrix4x4> reauthor)
    {
        string td = WriteRigged(tag + "_alpha_l1", 3, 24, new[] { A, B, C, D });
        SyntheticPool.MapBindPoses(td, reauthor);
        return td;
    }

    [Fact]
    public void A_tier_authored_in_another_space_passes_the_tier_gate()
    {
        var (ad, bd) = SingleSpacePool("tier");
        string td = WriteRigged("tier_alpha_l1", 3, 24, new[] { A, B, C, D });
        SyntheticPool.AuthorInSpace(td, QuarterTurnX);

        var req = Request("tier", ad, bd, out string outDir,
            new[] { new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002") });
        new MigotoEmitter().Build(Referenced(req));

        Assert.True(File.Exists(Path.Combine(outDir, "alpha_lod1_cpinv.buf")));
    }

    [Fact]
    public void A_tier_bound_elsewhere_than_its_lod0_has_its_rows_converted_onto_the_lod0s_binds()
    {
        // a decimated tier is often re-bound; the donor it poses is the lod0's geometry, so the lod0's
        // binds are the statement and the tier's rows convert onto them
        var (ad, bd) = SingleSpacePool("tiershear");
        var shear = Matrix4x4.Identity;
        shear.M23 = 0.4f;
        string td = Tier("tiershear", b => shear * b);

        var req = Request("tiershear", ad, bd, out string outDir,
            new[] { new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002") });
        req = req with
        {
            Pipelines = new[]
            {
                req.Pipelines.Single() with
                {
                    BonePaths = new Dictionary<uint, string>
                    {
                        [A] = "Prefab/root/Root_M/Hair01_L/Bone_M",
                    },
                },
            },
        };
        var result = new MigotoEmitter().Build(Referenced(req));

        float floor = UnconvertedFloor("tiershearfloor");
        float worst = 0;
        foreach (var (slot, row) in PosedPoolSim.Recover(outDir, "swap", "alpha_lod1", td))
        {
            // every row the tier writes — the union rows it owns, and the anchor-side witness recovery
            uint h = slot < 5 ? new[] { A, B, C, D, E }[slot] : B;
            worst = MathF.Max(worst, PosedPoolSim.Error(row, BindOf(h) * PosedPoolSim.WorldOf(h)));
        }
        AssertRowsExact(worst, floor);
        Assert.True(File.Exists(Path.Combine(outDir, "alpha_lod1_cpinv_swap.buf")));
        // the build log names what was converted, by the bone's own name where the files give one
        string line = Assert.Single(result.Diagnostics, d => d.Contains("alpha_lod1 binds"));
        Assert.Contains("4 bones elsewhere than the replacement is posed from", line);
        Assert.Contains($"Hair01_L/Bone_M (0x{A:x8})", line);
        // and the ini binds the tier's converted copy, not the shared solved operator
        string ini = File.ReadAllText(Directory.GetFiles(outDir, "*.ini").Single());
        Assert.Contains("[Resource_alpha_lod1_Cpinv_swap]", ini);
        Assert.Contains("cs-t1 = Resource_alpha_lod1_Cpinv_swap\n", ini);
        Assert.DoesNotContain("[Resource_alpha_lod1_Cpinv]", ini);
    }

    // ---- operator cache ----------------------------------------------------------------------------

    [Fact]
    public void A_converted_part_files_its_operator_apart_from_an_unconverted_one()
    {
        string cache = Path.Combine(_root, "opcache");
        int Entries() => Directory.Exists(cache) ? Directory.GetFiles(cache, "*.op").Length : 0;

        var (refA, refB) = SingleSpacePool("cref");
        new MigotoEmitter { OperatorCacheDir = cache }
            .Build(Referenced(Request("cref", refA, refB, out _, null, "key-alpha", "key-beta")));
        Assert.Equal(2, Entries());

        // the same pool with beta authored a quarter turn away: alpha's entry still describes alpha, but
        // beta is solved on converted geometry and must not be served the unconverted solve
        var (cvA, cvB) = SingleSpacePool("ccv");
        SyntheticPool.AuthorInSpace(cvB, QuarterTurnX);
        new MigotoEmitter { OperatorCacheDir = cache }
            .Build(Referenced(Request("ccv", cvA, cvB, out _, null, "key-alpha", "key-beta"),
                new Dictionary<string, Matrix4x4> { ["beta"] = QuarterTurnX }));
        Assert.Equal(3, Entries());
    }

    // ---- the scene-space union: what lets two Replaces on one subject build together ---------------

    [Fact]
    public void A_pool_authored_away_from_scene_builds_byte_identically_to_one_authored_in_it()
    {
        // both parts authored a quarter turn from scene with the anchor's rest saying exactly that:
        // the union restates the whole pipeline into scene space, which IS the reference pool's space
        var (refA, refB) = SingleSpacePool("scref");
        new MigotoEmitter().Build(Referenced(Request("scref", refA, refB, out string refOut)));

        var (cvA, cvB) = SingleSpacePool("sccv");
        SyntheticPool.AuthorInSpace(cvA, QuarterTurnX);
        SyntheticPool.AuthorInSpace(cvB, QuarterTurnX);
        new MigotoEmitter().Build(Referenced(Request("sccv", cvA, cvB, out string cvOut,
            aRest: QuarterTurnX, bRest: QuarterTurnX),
            new Dictionary<string, Matrix4x4> { ["alpha"] = QuarterTurnX, ["beta"] = QuarterTurnX }));

        AssertSameTree(refOut, cvOut);
    }

    [Fact]
    public void One_dump_pulled_into_two_pipelines_builds_when_measured_rests_state_one_scene_space()
    {
        // the shape that used to refuse: one dump in two pipelines whose anchors sit in different
        // spaces. With every anchor's rest measured, both unions state scene space and the shared
        // dump converts the same way in each.
        string shared = WriteRigged("scshared", 1, 16, new[] { B, C, D });
        SyntheticPool.AuthorInSpace(shared, QuarterTurnX);

        string upright = WriteRigged("scupright", 2, 16, new[] { A, B, C, D });

        string turned = WriteRigged("scturned", 3, 16, new[] { B, C, D, E });
        SyntheticPool.AuthorInSpace(turned, Matrix4x4.Transpose(QuarterTurnX));

        string outDir = Path.Combine(_root, "sctwo_out");
        var req = new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "one",
                    Parts = new[]
                    {
                        new PoolPart("shared", shared, MeasuredRest: QuarterTurnX),
                        new PoolPart("upright", upright, MeasuredRest: Matrix4x4.Identity),
                    },
                    Anchor = "upright",
                    CaptureHashes = new Dictionary<string, string> { ["shared"] = "aaaa0001", ["upright"] = "bbbb0001" },
                },
                new ReplacePipeline
                {
                    Suffix = "two",
                    Parts = new[]
                    {
                        new PoolPart("shared", shared, MeasuredRest: QuarterTurnX),
                        new PoolPart("turned", turned, MeasuredRest: Matrix4x4.Transpose(QuarterTurnX)),
                    },
                    Anchor = "turned",
                    CaptureHashes = new Dictionary<string, string> { ["shared"] = "aaaa0001", ["turned"] = "cccc0001" },
                },
            },
        };

        new MigotoEmitter().Build(req);

        Assert.True(File.Exists(Path.Combine(outDir, "shared_cpinv.buf")));
        Assert.True(File.Exists(Path.Combine(outDir, "upright_cpinv.buf")));
        Assert.True(File.Exists(Path.Combine(outDir, "turned_cpinv.buf")));
    }

    [Fact]
    public void A_dump_already_in_scene_space_settles_as_no_conversion_from_both_sides()
    {
        // one pipeline reaches the shared dump with no deltas at all (upright anchor, same space); the
        // other carries it to its face-down anchor and back out to scene, a composition that lands on
        // exact identity. Both must read as the same "no conversion" — the real shape of a prop whose
        // body sits in scene space while one edited part is authored face-down.
        string shared = WriteRigged("idshared", 1, 16, new[] { B, C, D });

        string faceDown = WriteRigged("idfacedown", 2, 16, new[] { A, B, C, D });
        SyntheticPool.AuthorInSpace(faceDown, QuarterTurnX);

        string upright = WriteRigged("idupright", 3, 16, new[] { B, C, D, E });

        string outDir = Path.Combine(_root, "idtwo_out");
        var req = new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "one",
                    Parts = new[]
                    {
                        new PoolPart("shared", shared, MeasuredRest: Matrix4x4.Identity),
                        new PoolPart("facedown", faceDown, MeasuredRest: QuarterTurnX),
                    },
                    Anchor = "facedown",
                    CaptureHashes = new Dictionary<string, string> { ["shared"] = "aaaa0001", ["facedown"] = "dddd0001" },
                },
                new ReplacePipeline
                {
                    Suffix = "two",
                    Parts = new[]
                    {
                        new PoolPart("shared", shared, MeasuredRest: Matrix4x4.Identity),
                        new PoolPart("upright", upright, MeasuredRest: Matrix4x4.Identity),
                    },
                    Anchor = "upright",
                    CaptureHashes = new Dictionary<string, string> { ["shared"] = "aaaa0001", ["upright"] = "bbbb0001" },
                },
            },
        };

        new MigotoEmitter().Build(req);

        Assert.True(File.Exists(Path.Combine(outDir, "shared_cpinv.buf")));
        Assert.True(File.Exists(Path.Combine(outDir, "facedown_cpinv.buf")));
        Assert.True(File.Exists(Path.Combine(outDir, "upright_cpinv.buf")));
    }

    [Fact]
    public void One_dump_pulled_into_two_pipelines_with_different_reference_spaces_refuses()
    {
        string shared = Path.Combine(_root, "shared");
        SyntheticPool.WritePartDump(shared, 1, 16, new[] { B, C, D });
        SyntheticPool.MapBindPoses(shared, _ => QuarterTurnX);

        string upright = Path.Combine(_root, "upright");
        SyntheticPool.WritePartDump(upright, 2, 16, new[] { A, B, C, D });   // bindposes identity

        string turned = Path.Combine(_root, "turned");
        SyntheticPool.WritePartDump(turned, 3, 16, new[] { B, C, D, E });
        SyntheticPool.MapBindPoses(turned, _ => Matrix4x4.Transpose(QuarterTurnX));

        var req = new PoolBuildRequest
        {
            OutDir = Path.Combine(_root, "two_out"),
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "one",
                    Parts = new[] { new PoolPart("shared", shared), new PoolPart("upright", upright) },
                    Anchor = "upright",
                },
                new ReplacePipeline
                {
                    Suffix = "two",
                    Parts = new[] { new PoolPart("shared", shared), new PoolPart("turned", turned) },
                    Anchor = "turned",
                },
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(req));
        Assert.Contains("reference bind spaces differ", ex.Message);
    }

    // ---- the bundle-side union (the donor compile's own gate) --------------------------------------

    /// <summary>Two skinned meshes out of synthetic bundles: alpha rigs A,B,C,D and beta rigs B,C,D,E, so
    /// they share the three bones a conversion delta has to be fitted to. Bindposes come back identity;
    /// the caller states the ones under test.</summary>
    private (AssetTypeValueField Alpha, AssetTypeValueField Beta) SkinnedPair()
    {
        static float[] Cloud(int n) => Enumerable.Range(0, n * 3).Select(i => (i % 7) / 3f + 0.5f).ToArray();
        static int[] Tris(int n) => Enumerable.Range(0, n * 3).Select(i => i % n).ToArray();

        string pa = Path.Combine(_root, "alpha.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pa, "alpha_mesh", Cloud(12), Tris(12), new[] { A, B, C, D });
        string pb = Path.Combine(_root, "beta.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pb, "beta_mesh", Cloud(8), Tris(8), new[] { B, C, D, E });

        var reader = new Remold.Core.Bundles.BundleReader();
        return (reader.GetMeshField(File.ReadAllBytes(pa), "alpha_mesh")!,
                reader.GetMeshField(File.ReadAllBytes(pb), "beta_mesh")!);
    }

    /// <summary>State a whole part's binds by bone: alpha in the <see cref="Rig"/>'s own space, beta the
    /// same rig seen <paramref name="space"/> away from it.</summary>
    private static void SetRig(AssetTypeValueField mesh, uint[] bones, Matrix4x4 space)
    {
        for (int i = 0; i < bones.Length; i++) SetBind(mesh, i, space * BindOf(bones[i]));
    }

    private static void SetBind(AssetTypeValueField mesh, int bone, Matrix4x4 m)
    {
        var e = mesh["m_BindPose"]["Array"].Children[bone];
        var raw = BindSpace.ToUnityFloats(m);
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                e[$"e{r}{c}"].AsFloat = raw[r * 4 + c];
    }

    [Fact]
    public void The_bundle_side_union_is_stated_in_the_reference_parts_space()
    {
        var (alpha, beta) = SkinnedPair();
        SetRig(alpha, new[] { A, B, C, D }, Matrix4x4.Identity);
        SetRig(beta, new[] { B, C, D, E }, QuarterTurnX);

        var (hashes, binds) = SwapCompile.BuildUnionOrder(
            new[] { alpha, beta }, new[] { "alpha", "beta" }, referenceIndex: 0,
            bindReference: Rig.ToDictionary(r => r.Hash, r => r.Bind));

        Assert.Equal(new[] { A, B, C, D, E }, hashes);
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(B)), binds[1]);
        // E is beta's alone, and the delta the shared three measured restates it onto the reference exactly
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(E)), binds[4]);
    }

    [Fact]
    public void The_bundle_side_union_keeps_first_seen_order_when_the_reference_is_not_first()
    {
        var (alpha, beta) = SkinnedPair();
        SetRig(alpha, new[] { A, B, C, D }, Matrix4x4.Identity);
        SetRig(beta, new[] { B, C, D, E }, QuarterTurnX);

        var (hashes, binds) = SwapCompile.BuildUnionOrder(
            new[] { alpha, beta }, new[] { "alpha", "beta" }, referenceIndex: 1);

        Assert.Equal(new[] { A, B, C, D, E }, hashes);                              // order is first-seen
        Assert.Equal(BindSpace.ToUnityFloats(QuarterTurnX * BindOf(A)), binds[0]);   // space is beta's
        Assert.Equal(BindSpace.ToUnityFloats(QuarterTurnX * BindOf(B)), binds[1]);
    }

    /// <summary>The bundle-side one-shared-bone pair: alpha rigs A,B,C,D and gamma rigs D,E — below the
    /// fitted delta's corroboration floor, so only measured rests can restate gamma.</summary>
    private (AssetTypeValueField Alpha, AssetTypeValueField Gamma) OneSharedBoneSkinnedPair()
    {
        static float[] Cloud(int n) => Enumerable.Range(0, n * 3).Select(i => (i % 7) / 3f + 0.5f).ToArray();
        static int[] Tris(int n) => Enumerable.Range(0, n * 3).Select(i => i % n).ToArray();

        string pa = Path.Combine(_root, "alpha1.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pa, "alpha_mesh", Cloud(12), Tris(12), new[] { A, B, C, D });
        string pg = Path.Combine(_root, "gamma1.bundle");
        Support.SyntheticBundle.BuildOneSkinnedMesh(pg, "gamma_mesh", Cloud(6), Tris(6), new[] { D, E });

        var reader = new Remold.Core.Bundles.BundleReader();
        return (reader.GetMeshField(File.ReadAllBytes(pa), "alpha_mesh")!,
                reader.GetMeshField(File.ReadAllBytes(pg), "gamma_mesh")!);
    }

    [Fact]
    public void The_bundle_side_union_is_stated_in_scene_space_when_the_reference_rest_is_a_bake()
    {
        var (alpha, beta) = SkinnedPair();
        SetRig(alpha, new[] { A, B, C, D }, QuarterTurnX);
        SetRig(beta, new[] { B, C, D, E }, QuarterTurnX);

        var (hashes, binds) = SwapCompile.BuildUnionOrder(
            new[] { alpha, beta }, new[] { "alpha", "beta" }, referenceIndex: 0,
            new Matrix4x4?[] { QuarterTurnX, QuarterTurnX });

        Assert.Equal(new[] { A, B, C, D, E }, hashes);
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(A)), binds[0]);   // scene, not the reference's space
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(E)), binds[4]);
    }

    [Fact]
    public void The_bundle_side_union_keeps_the_reference_space_when_the_rest_is_a_placement()
    {
        // a rest carrying a real translation is no bake: the union must stay in the reference part's
        // own space rather than restate on a partial relation
        var (alpha, beta) = SkinnedPair();
        SetRig(alpha, new[] { A, B, C, D }, QuarterTurnX);
        SetRig(beta, new[] { B, C, D, E }, QuarterTurnX);
        var placed = T(0.4f, 0, 0) * QuarterTurnX;

        var (_, binds) = SwapCompile.BuildUnionOrder(
            new[] { alpha, beta }, new[] { "alpha", "beta" }, referenceIndex: 0,
            new Matrix4x4?[] { placed, placed });

        Assert.Equal(BindSpace.ToUnityFloats(QuarterTurnX * BindOf(A)), binds[0]);
    }

    [Fact]
    public void The_bundle_side_union_restates_a_one_shared_bone_part_by_its_measured_rests()
    {
        var (alpha, gamma) = OneSharedBoneSkinnedPair();
        SetRig(alpha, new[] { A, B, C, D }, Matrix4x4.Identity);
        SetRig(gamma, new[] { D, E }, QuarterTurnX);

        // without rests nothing is restated, and the reference the build hands over still states gamma's
        // own bone where the anchor would bind it
        var (_, unrested) = SwapCompile.BuildUnionOrder(
            new[] { alpha, gamma }, new[] { "alpha", "gamma" }, referenceIndex: 0,
            bindReference: Rig.ToDictionary(r => r.Hash, r => r.Bind));
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(D)), unrested[3]);
        AssertBindNear(BindOf(E), unrested[4]);

        // the measured delta restates it exactly
        var (hashes, binds) = SwapCompile.BuildUnionOrder(
            new[] { alpha, gamma }, new[] { "alpha", "gamma" }, referenceIndex: 0,
            new Matrix4x4?[] { Matrix4x4.Identity, QuarterTurnX });

        Assert.Equal(new[] { A, B, C, D, E }, hashes);
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(D)), binds[3]);   // the shared bone lands on alpha's bind
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(E)), binds[4]);   // gamma's own bone rides the same delta
    }

    /// <summary>A stated bind against the expected one, to float32's own resolution: a bind carried through
    /// an inverse lands within rounding of the original, not on its bits.</summary>
    private static void AssertBindNear(Matrix4x4 expected, float[] unityFloats)
    {
        var want = BindSpace.ToUnityFloats(expected);
        for (int i = 0; i < 16; i++)
            Assert.True(MathF.Abs(want[i] - unityFloats[i]) <= 1e-5f, $"element {i}: {unityFloats[i]} vs {want[i]}");
    }

    [Fact]
    public void The_bundle_side_union_states_a_translated_part_in_the_references_space()
    {
        var (alpha, gamma) = OneSharedBoneSkinnedPair();
        var mount = T(0.4f, 0, 0);
        SetRig(alpha, new[] { A, B, C, D }, Matrix4x4.Identity);
        SetRig(gamma, new[] { D, E }, mount);

        var (hashes, binds) = SwapCompile.BuildUnionOrder(
            new[] { alpha, gamma }, new[] { "alpha", "gamma" }, referenceIndex: 0,
            new Matrix4x4?[] { Matrix4x4.Identity, mount }, Rig.ToDictionary(r => r.Hash, r => r.Bind));

        Assert.Equal(new[] { A, B, C, D, E }, hashes);
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(D)), binds[3]);   // the shared bone is the reference's
        AssertBindNear(BindOf(E), binds[4]);                           // gamma's own bone, carried back
    }

    [Fact]
    public void The_bundle_side_union_states_a_bone_two_parts_bind_differently_under_the_references_bind()
    {
        var (alpha, beta) = SkinnedPair();
        var shear = Matrix4x4.Identity;
        shear.M12 = 0.3f;
        SetBind(alpha, 1, T(4, 5, 6));
        SetBind(beta, 0, shear * T(4, 5, 6));

        var (_, fromAlpha) = SwapCompile.BuildUnionOrder(
            new[] { alpha, beta }, new[] { "alpha", "beta" }, referenceIndex: 0,
            bindReference: new Dictionary<uint, Matrix4x4> { [B] = T(4, 5, 6) });
        var (_, fromBeta) = SwapCompile.BuildUnionOrder(
            new[] { alpha, beta }, new[] { "alpha", "beta" }, referenceIndex: 1,
            bindReference: new Dictionary<uint, Matrix4x4> { [B] = shear * T(4, 5, 6) });

        // the union ORDER is first-seen either way; the statement follows the reference it is handed
        Assert.Equal(BindSpace.ToUnityFloats(T(4, 5, 6)), fromAlpha[1]);
        Assert.Equal(BindSpace.ToUnityFloats(shear * T(4, 5, 6)), fromBeta[1]);
    }

    // ---- the quotient itself -----------------------------------------------------------------------

    [Fact]
    public void The_delta_is_the_left_quotient_and_rebasing_reproduces_the_reference_exactly()
    {
        // per-bone reference binds that do not commute with the rotation: the RIGHT quotient
        // inv(B_ref)·B_part is conjugated per bone and does not survive this
        var reference = new[] { T(4, 5, 6), T(-2, 7, 0.5f), Matrix4x4.CreateScale(1f) * T(0.25f, -3, 8) };
        var part = reference.Select(b => QuarterTurnX * b).ToArray();

        var delta = BindSpace.Delta(part.Zip(reference, (p, r) => (p, r)));
        Assert.Equal(QuarterTurnX, delta);

        for (int i = 0; i < reference.Length; i++)
            Assert.Equal(reference[i], BindSpace.Rebase(part[i], delta!.Value));
    }

    [Fact]
    public void A_delta_that_varies_across_the_shared_bones_is_no_space_difference()
    {
        var reference = new[] { T(4, 5, 6), T(-2, 7, 0.5f), T(0.25f, -3, 8) };
        var part = new[]
        {
            QuarterTurnX * reference[0], Matrix4x4.Transpose(QuarterTurnX) * reference[1],
            QuarterTurnX * reference[2],
        };

        Assert.Null(BindSpace.Delta(part.Zip(reference, (p, r) => (p, r))));
    }

    [Fact]
    public void A_delta_fitted_to_too_few_shared_bones_is_not_acted_on()
    {
        // one shared bone whose delta IS an exact quarter turn with no translation, so the snap accepts it
        // and the uniformity gate has nothing to compare it against. Only the corroboration floor is left,
        // and without it this coincidence would rotate the whole part.
        var reference = new[] { T(4, 5, 6) };
        var part = reference.Select(b => QuarterTurnX * b).ToArray();
        Assert.NotNull(RestBake.Snap(QuarterTurnX));

        Assert.Null(BindSpace.Delta(part.Zip(reference, (p, r) => (p, r))));

        // the same delta over the floor's worth of bones is a space difference and converts
        var wide = new[] { T(4, 5, 6), T(-2, 7, 0.5f), T(0.25f, -3, 8) };
        Assert.Equal(BindSpace.MinSharedBones, wide.Length);
        Assert.Equal(QuarterTurnX,
            BindSpace.Delta(wide.Select(b => (QuarterTurnX * b, b))));
    }

    [Fact]
    public void Parts_already_in_one_space_have_no_delta_to_apply()
    {
        var reference = new[] { T(4, 5, 6), T(-2, 7, 0.5f), T(0.25f, -3, 8) };
        Assert.Null(BindSpace.Delta(reference.Select(b => (b, b))));
    }
}
