using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The pure rig-grouping behind the workbench Skeleton node: Transform nodes into a bone list with
/// parent-index links, in node order. The LIVE half is exercised in
/// <see cref="SubjectModelBuilderTests.PrefabModel_ReadsTheRigSkeleton"/>.
/// </summary>
public class PrefabRigTests
{
    private static BundleReader.TransformNode N(long pid, string name, long father) => new(pid, name, father);

    [Fact]
    public void FromTransforms_BuildsBoneCountNamesAndParentLinks()
    {
        // route: pure rig grouping — a chain root→pelvis→spine plus one orphan whose father is off-set
        var nodes = new[]
        {
            N(1, "root", 0),
            N(2, "pelvis", 1),
            N(3, "spine", 2),
            N(4, "loose", 999),   // father not among the nodes ⇒ treated as a root
        };
        var sk = PrefabRig.FromTransforms(nodes, null, out var problem);
        Assert.NotNull(sk);
        Assert.Null(problem);
        Assert.Equal(4, sk!.BoneCount);
        Assert.Equal(new[] { "root", "pelvis", "spine", "loose" }, sk.Bones.Select(b => b.Name).ToArray());
        Assert.Equal(-1, sk.Bones[0].ParentIndex);   // root
        Assert.Equal(0, sk.Bones[1].ParentIndex);    // pelvis ← root
        Assert.Equal(1, sk.Bones[2].ParentIndex);    // spine ← pelvis
        Assert.Equal(-1, sk.Bones[3].ParentIndex);   // orphan ← (off-set) root
    }

    [Fact]
    public void FromTransforms_EmptyOrNull_IsNull()
    {
        // route: no rig ⇒ null and NO problem sentence, so the caller reports its own "structure
        // unavailable" loudly (never a hollow 0)
        Assert.Null(PrefabRig.FromTransforms(Array.Empty<BundleReader.TransformNode>(), null, out var p1));
        Assert.Null(p1);
        Assert.Null(PrefabRig.FromTransforms(null!, null, out var p2));
        Assert.Null(p2);
    }

    [Fact]
    public void FromTransforms_ExcludesRootAndSlotCarriers()
    {
        // A real prefab's transform list is root + SMR-slot carriers + bones, and only the bones are the
        // rig; the named exclusion set drops the rest.
        var nodes = new[]
        {
            N(1, "KarstSSR01", 0),                       // container root
            N(2, "c_KarstSSR01_slg_body_lod0", 1),       // SMR slot carrier
            N(3, "c_KarstSSR01_slg_cloth1_lod0", 1),     // SMR slot carrier
            N(4, "c_KarstSSR01_slg_cloth1_lod1", 1),     // another LOD tier of the same slot
            N(10, "Bip001", 0),
            N(11, "Bip001 Spine", 10),
            N(12, "Bip001 L Hand", 11),
        };
        var exclude = new HashSet<string>(StringComparer.Ordinal)
        {
            "KarstSSR01", "c_KarstSSR01_slg_body_lod0",
            "c_KarstSSR01_slg_cloth1_lod0", "c_KarstSSR01_slg_cloth1_lod1",
        };
        var sk = PrefabRig.FromTransforms(nodes, exclude, out var problem);
        Assert.NotNull(sk);
        Assert.Null(problem);
        Assert.Equal(3, sk!.BoneCount);                                              // N bones, not the 7 nodes
        Assert.Equal(new[] { "Bip001", "Bip001 Spine", "Bip001 L Hand" }, sk.Bones.Select(b => b.Name).ToArray());
        Assert.DoesNotContain(sk.Bones, b => b.Name.Contains("_slg_"));              // no slot carrier survived
        Assert.DoesNotContain(sk.Bones, b => b.Name == "KarstSSR01");               // nor the container root
        Assert.Equal(-1, sk.Bones[0].ParentIndex);                                   // Bip001 is a root
        Assert.Equal(0, sk.Bones[1].ParentIndex);                                    // Spine ← Bip001 (re-indexed)
        Assert.Equal(1, sk.Bones[2].ParentIndex);                                    // Hand ← Spine
    }

    [Fact]
    public void FromTransforms_AnExcludedNameCarriedByTwoTransforms_IsNullWithAProblem()
    {
        // route: the collision the by-name exclusion cannot survive — a bone sharing the container root's
        // name. Dropping both would cost a bone with nothing to show for it, so the read declines to guess
        // and hands the caller a sentence instead. It does NOT throw: one unreadable display node must not
        // cost the caller the rest of the subject.
        var nodes = new[]
        {
            N(1, "KarstSSR01", 0),                       // container root
            N(2, "c_KarstSSR01_slg_body_lod0", 1),       // SMR slot carrier
            N(10, "Bip001", 0),
            N(11, "KarstSSR01", 10),                     // a rig bone that happens to share the root's name
        };
        var exclude = new HashSet<string>(StringComparer.Ordinal)
            { "KarstSSR01", "c_KarstSSR01_slg_body_lod0" };

        Assert.Null(PrefabRig.FromTransforms(nodes, exclude, out var problem));
        Assert.NotNull(problem);
        Assert.Contains("'KarstSSR01'", problem!);
        Assert.Contains("path ids 1 and 11", problem);   // both colliding Transforms are named
    }

    [Fact]
    public void FromTransforms_CollisionDetection_FollowsTheExcludeSetsComparer()
    {
        // The exclude set decides membership, so the collision scan must key the same way: under a
        // case-INSENSITIVE set, two names differing only in case are ONE excluded name carried twice.
        var nodes = new[]
        {
            N(1, "KarstSSR01", 0),
            N(11, "karstssr01", 10),   // same name to the set that owns membership
        };
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KarstSSR01" };

        Assert.Null(PrefabRig.FromTransforms(nodes, exclude, out var problem));
        Assert.NotNull(problem);
        Assert.Contains("path ids 1 and 11", problem!);
    }

    [Fact]
    public void Subtree_ScopesTheRigToOneContainerRoot_WhenTheBundleShipsTwo()
    {
        // A bundle can ship a model AND a variant root beside it, each carrying its own copy of the rig.
        // Pooled, every bone name is a duplicate and the exclusion scan refuses; scoped to the subject's
        // own root, the read is the ordinary single-rig one.
        var nodes = new[]
        {
            N(1, "TestySSR01", 0),
            N(2, "c_TestySSR01_slg_body_lod0", 1),
            N(3, "Bip001", 1),
            N(4, "Bip001 Spine", 3),
            N(10, "TestySSR01_nobag", 0),
            N(11, "c_TestySSR01_slg_body_lod0", 10),
            N(12, "Bip001", 10),
            N(13, "Bip001 Spine", 12),
        };
        var exclude = new HashSet<string>(StringComparer.Ordinal)
            { "TestySSR01", "c_TestySSR01_slg_body_lod0" };

        var scoped = PrefabRig.Subtree(nodes, "TestySSR01", out var scopeProblem);
        Assert.Null(scopeProblem);
        Assert.NotNull(scoped);
        Assert.Equal(new[] { 1L, 2L, 3L, 4L }, scoped.Select(n => n.PathId).ToArray());

        var sk = PrefabRig.FromTransforms(scoped, exclude, out var problem);
        Assert.Null(problem);                                       // no collision inside one root
        Assert.Equal(new[] { "Bip001", "Bip001 Spine" }, sk!.Bones.Select(b => b.Name).ToArray());
        Assert.Equal(0, sk.Bones[1].ParentIndex);

        // the OTHER root reads as its own rig, from the same bundle
        var other = PrefabRig.Subtree(nodes, "TestySSR01_nobag", out _);
        Assert.Equal(new[] { 10L, 11L, 12L, 13L }, other!.Select(n => n.PathId).ToArray());
    }

    [Fact]
    public void Subtree_ARootThatIsNotTopLevelOrNotThere_IsNullWithAProblem()
    {
        // A bone sharing the container root's name is not a container root: only the top-level Transform
        // can be one, so the subtree is picked from the real root and the bone stays a bone.
        var nodes = new[]
        {
            N(1, "TestySSR01", 0),
            N(2, "Bip001", 1),
            N(3, "TestySSR01", 2),     // a bone that happens to carry the root's name
        };
        var scoped = PrefabRig.Subtree(nodes, "TestySSR01", out var problem);
        Assert.Null(problem);
        Assert.Equal(3, scoped!.Count);

        // a root name the hierarchy doesn't carry: refuse, rather than read every model in the bundle
        Assert.Null(PrefabRig.Subtree(nodes, "OtherSSR01", out var missing));
        Assert.Contains("'OtherSSR01'", missing!);

        // two top-level Transforms under one name: which subtree is the subject's is unknowable
        var twins = new[] { N(1, "TestySSR01", 0), N(2, "TestySSR01", 0) };
        Assert.Null(PrefabRig.Subtree(twins, "TestySSR01", out var ambiguous));
        Assert.Contains("2 top-level", ambiguous!);
        Assert.Contains("path ids 1, 2", ambiguous);
    }

    [Fact]
    public void FromTransforms_AllExcluded_IsNull()
    {
        // route: a prefab that is ALL slot carriers + root and no rig ⇒ null (structure unavailable), not 0
        var nodes = new[] { N(1, "EnemyX", 0), N(2, "c_EnemyX_slg_body_lod0", 1) };
        var exclude = new HashSet<string>(StringComparer.Ordinal) { "EnemyX", "c_EnemyX_slg_body_lod0" };
        Assert.Null(PrefabRig.FromTransforms(nodes, exclude, out var problem));
        Assert.Null(problem);   // nothing to explain — the caller's own "structure unavailable" covers it
    }
}
