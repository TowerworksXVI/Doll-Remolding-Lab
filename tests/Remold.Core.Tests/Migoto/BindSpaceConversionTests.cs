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
/// space (bind and geometry together) instead of refused. A delta that is no rigid space change keeps
/// refusing: bone-name hashes collide across unrelated rigs, and converting on a coincidence would
/// deform the geometry.
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

    // ---- the conversion ----------------------------------------------------------------------------

    [Fact]
    public void A_part_in_a_rotated_bind_space_builds_as_if_it_were_authored_in_the_anchors()
    {
        var (refA, refB) = SingleSpacePool("ref");
        new MigotoEmitter().Build(Request("ref", refA, refB, out string refOut));

        var (cvA, cvB) = SingleSpacePool("cv");
        SyntheticPool.AuthorInSpace(cvB, QuarterTurnX);
        new MigotoEmitter().Build(Request("cv", cvA, cvB, out string cvOut));

        AssertSameTree(refOut, cvOut);
    }

    [Fact]
    public void A_bind_delta_carrying_a_shear_still_refuses()
    {
        var (ad, bd) = SingleSpacePool("shear");
        var shear = Matrix4x4.Identity;
        shear.M12 = 0.3f;
        SyntheticPool.MapBindPoses(bd, b => shear * b);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new MigotoEmitter().Build(Request("shear", ad, bd, out _)));
        Assert.Contains("inconsistent bind poses across pool parts", ex.Message);
    }

    [Fact]
    public void Parts_sharing_a_bone_hash_by_coincidence_still_refuse()
    {
        // a uniform delta over enough bones to be corroborated, carrying a large translation and no
        // rotation: the snap refuses it on its own, with no help from the corroboration floor
        var (ad, bd) = SingleSpacePool("lookalike");
        SyntheticPool.MapBindPoses(bd, b => T(12, 0, 0) * b);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new MigotoEmitter().Build(Request("lookalike", ad, bd, out _)));
        Assert.Contains("inconsistent bind poses across pool parts", ex.Message);
    }

    [Fact]
    public void A_pool_whose_binds_agree_builds_byte_identically_to_one_that_differs_below_the_gate()
    {
        var (exA, exB) = SingleSpacePool("exact");
        new MigotoEmitter().Build(Request("exact", exA, exB, out string exactOut));

        var (nrA, nrB) = SingleSpacePool("near");
        var nudge = Matrix4x4.Identity;
        nudge.M12 = 1e-7f;
        SyntheticPool.MapBindPoses(nrB, b => nudge * b);
        new MigotoEmitter().Build(Request("near", nrA, nrB, out string nearOut));

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
        new MigotoEmitter().Build(Request("mref", refA, refG, out string refOut));

        var (cvA, cvG) = OneSharedBonePool("mcv");
        SyntheticPool.AuthorInSpace(cvG, QuarterTurnX);
        new MigotoEmitter().Build(Request("mcv", cvA, cvG, out string cvOut,
            aRest: Matrix4x4.Identity, bRest: QuarterTurnX));

        AssertSameTree(refOut, cvOut);
    }

    [Fact]
    public void A_part_sharing_one_bone_still_refuses_without_measured_rests()
    {
        // the corroboration floor holds: one shared bone can't vouch for a fitted delta, and with no
        // measured rests there is nothing else to relate the spaces
        var (ad, gd) = OneSharedBonePool("mfloor");
        SyntheticPool.AuthorInSpace(gd, QuarterTurnX);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new MigotoEmitter().Build(Request("mfloor", ad, gd, out _)));
        Assert.Contains("inconsistent bind poses across pool parts", ex.Message);
    }

    [Fact]
    public void A_measured_delta_carrying_a_real_translation_still_refuses()
    {
        // measured rests that DON'T relate by a pure rotation (a mount offset): the measurement rules a
        // bind-space rotation out, so the refusal must stand rather than fall back to fitting
        var (ad, gd) = OneSharedBonePool("mtrans");
        SyntheticPool.MapBindPoses(gd, b => T(0.4f, 0, 0) * b);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new MigotoEmitter().Build(Request("mtrans", ad, gd, out _,
                aRest: Matrix4x4.Identity, bRest: T(0.4f, 0, 0))));
        Assert.Contains("inconsistent bind poses across pool parts", ex.Message);
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
        new MigotoEmitter().Build(req);

        Assert.True(File.Exists(Path.Combine(outDir, "alpha_lod1_cpinv.buf")));
    }

    [Fact]
    public void A_tier_whose_bind_delta_is_not_rigid_still_refuses()
    {
        var (ad, bd) = SingleSpacePool("tiershear");
        var shear = Matrix4x4.Identity;
        shear.M23 = 0.4f;
        string td = Tier("tiershear", b => shear * b);

        var req = Request("tiershear", ad, bd, out _,
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
        var ex = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(req));
        Assert.Contains("different bind pose than the lod0 of 'alpha' for bone 'Bone_M'", ex.Message);
        Assert.DoesNotContain("Hair01_L/", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("part's space. Remove this mesh edit", ex.Message);
        Assert.DoesNotContain("0x", ex.Message, StringComparison.OrdinalIgnoreCase);
        string diagnostic = Assert.Single(BuildLogDiagnostics.From(ex));
        Assert.Contains($"'Hair01_L/Bone_M' (0x{A:x8})", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolved_tier_bind_refusal_counts_the_unnamed_bone_without_a_hash()
    {
        var (ad, bd) = SingleSpacePool("tierunnamed");
        var shear = Matrix4x4.Identity;
        shear.M23 = 0.4f;
        string td = Tier("tierunnamed", b => shear * b);
        var req = Request("tierunnamed", ad, bd, out _,
            new[] { new PoolTier("alpha", "alpha_lod1", "lod1", td, "aaaa0002") });

        var ex = Assert.Throws<InvalidOperationException>(() => new MigotoEmitter().Build(req));

        Assert.Contains("different bind pose than the lod0 of 'alpha' for 1 bone this install's files do not name",
            ex.Message);
        Assert.EndsWith("part's space. Remove this mesh edit", ex.Message);
        Assert.DoesNotContain("0x", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- operator cache ----------------------------------------------------------------------------

    [Fact]
    public void A_converted_part_files_its_operator_apart_from_an_unconverted_one()
    {
        string cache = Path.Combine(_root, "opcache");
        int Entries() => Directory.Exists(cache) ? Directory.GetFiles(cache, "*.op").Length : 0;

        var (refA, refB) = SingleSpacePool("cref");
        new MigotoEmitter { OperatorCacheDir = cache }
            .Build(Request("cref", refA, refB, out _, null, "key-alpha", "key-beta"));
        Assert.Equal(2, Entries());

        // the same pool with beta authored a quarter turn away: alpha's entry still describes alpha, but
        // beta is solved on converted geometry and must not be served the unconverted solve
        var (cvA, cvB) = SingleSpacePool("ccv");
        SyntheticPool.AuthorInSpace(cvB, QuarterTurnX);
        new MigotoEmitter { OperatorCacheDir = cache }
            .Build(Request("ccv", cvA, cvB, out _, null, "key-alpha", "key-beta"));
        Assert.Equal(3, Entries());
    }

    // ---- the scene-space union: what lets two Replaces on one subject build together ---------------

    [Fact]
    public void A_pool_authored_away_from_scene_builds_byte_identically_to_one_authored_in_it()
    {
        // both parts authored a quarter turn from scene with the anchor's rest saying exactly that:
        // the union restates the whole pipeline into scene space, which IS the reference pool's space
        var (refA, refB) = SingleSpacePool("scref");
        new MigotoEmitter().Build(Request("scref", refA, refB, out string refOut));

        var (cvA, cvB) = SingleSpacePool("sccv");
        SyntheticPool.AuthorInSpace(cvA, QuarterTurnX);
        SyntheticPool.AuthorInSpace(cvB, QuarterTurnX);
        new MigotoEmitter().Build(Request("sccv", cvA, cvB, out string cvOut,
            aRest: QuarterTurnX, bRest: QuarterTurnX));

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
            new[] { alpha, beta }, new[] { "alpha", "beta" }, referenceIndex: 0);

        Assert.Equal(new[] { A, B, C, D, E }, hashes);
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(B)), binds[1]);
        // E is beta's alone: nothing corroborates it, and it rides the delta the shared three measured
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

        // without rests the fitted floor refuses it...
        Assert.Throws<InvalidDataException>(() => SwapCompile.BuildUnionOrder(
            new[] { alpha, gamma }, new[] { "alpha", "gamma" }, referenceIndex: 0));

        // ...and the measured delta restates it exactly
        var (hashes, binds) = SwapCompile.BuildUnionOrder(
            new[] { alpha, gamma }, new[] { "alpha", "gamma" }, referenceIndex: 0,
            new Matrix4x4?[] { Matrix4x4.Identity, QuarterTurnX });

        Assert.Equal(new[] { A, B, C, D, E }, hashes);
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(D)), binds[3]);   // the shared bone lands on alpha's bind
        Assert.Equal(BindSpace.ToUnityFloats(BindOf(E)), binds[4]);   // gamma's own bone rides the same delta
    }

    [Fact]
    public void The_bundle_side_union_still_refuses_a_measured_translation()
    {
        var (alpha, gamma) = OneSharedBoneSkinnedPair();
        var mount = T(0.4f, 0, 0);
        SetRig(alpha, new[] { A, B, C, D }, Matrix4x4.Identity);
        SetRig(gamma, new[] { D, E }, mount);

        var ex = Assert.Throws<InvalidDataException>(() => SwapCompile.BuildUnionOrder(
            new[] { alpha, gamma }, new[] { "alpha", "gamma" }, referenceIndex: 0,
            new Matrix4x4?[] { Matrix4x4.Identity, mount }));
        Assert.Contains("no measured or corroborated rigid rotation", ex.Message);
    }

    [Fact]
    public void The_bundle_side_union_still_refuses_a_non_rigid_delta()
    {
        var (alpha, beta) = SkinnedPair();
        var shear = Matrix4x4.Identity;
        shear.M12 = 0.3f;
        SetBind(alpha, 1, T(4, 5, 6));
        SetBind(beta, 0, shear * T(4, 5, 6));

        var ex = Assert.Throws<InvalidDataException>(() => SwapCompile.BuildUnionOrder(
            new[] { alpha, beta }, new[] { "alpha", "beta" }, referenceIndex: 0));
        Assert.Contains("bind pose differs across pool parts", ex.Message);
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
