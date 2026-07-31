using System.Collections.Generic;
using System.Linq;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="SkeletonOutline.Indented"/> — the read-only skeleton inspector's DFS + two-space-indent
/// ordering.
/// </summary>
public class SkeletonOutlineTests
{
    private static SubjectBone B(string name, int parent) => new(name, parent);

    [Fact]
    public void Indented_EmitsDfsOrder_WithTwoSpacesPerDepth()
    {
        // route: SkeletonOutline.Indented — a small rig: root → hip → {spine, legL}, spine → head
        //   0 root(-1) 1 hip(0) 2 spine(1) 3 head(2) 4 legL(1)
        var bones = new List<SubjectBone>
        {
            B("root", -1), B("hip", 0), B("spine", 1), B("head", 2), B("legL", 1),
        };

        var lines = SkeletonOutline.Indented(bones);

        Assert.Equal(new[]
        {
            "root",
            "  hip",
            "    spine",
            "      head",
            "    legL",
        }, lines);
    }

    [Fact]
    public void Indented_MultipleRoots_EmitInIndexOrder_AtDepthZero()
    {
        // route: two roots (ParentIndex -1), each with one child
        var bones = new List<SubjectBone>
        {
            B("A", -1), B("A_child", 0), B("B", -1), B("B_child", 2),
        };

        var lines = SkeletonOutline.Indented(bones);

        Assert.Equal(new[] { "A", "  A_child", "B", "  B_child" }, lines);
    }

    [Fact]
    public void Indented_ParentOutOfRange_TreatedAsRoot()
    {
        // route: a parent index outside the read set (-1 semantics for "parent not captured")
        var bones = new List<SubjectBone> { B("orphan", 99), B("child", 0) };

        var lines = SkeletonOutline.Indented(bones);

        Assert.Equal(new[] { "orphan", "  child" }, lines);
    }

    [Fact]
    public void Indented_CycleSafe_EmitsEveryBoneOnce()
    {
        // route: a malformed parent cycle (0→1→0) must not loop and must not drop a bone
        var bones = new List<SubjectBone> { B("x", 1), B("y", 0) };

        var lines = SkeletonOutline.Indented(bones);

        Assert.Equal(2, lines.Count);
        Assert.Contains("x", lines[0] + lines[1]);
        Assert.Contains("y", lines[0] + lines[1]);
    }

    [Fact]
    public void Indented_Empty_ReturnsEmpty()
    {
        Assert.Empty(SkeletonOutline.Indented(new List<SubjectBone>()));
    }

    // ---- Tree (collapsible skeleton): nested nodes + default expansion (roots open, rest collapsed) ----

    [Fact]
    public void Tree_NestsChildrenUnderParents_InIndexOrder()
    {
        //   0 root(-1) 1 hip(0) 2 spine(1) 3 head(2) 4 legL(1)
        var bones = new List<SubjectBone>
        {
            B("root", -1), B("hip", 0), B("spine", 1), B("head", 2), B("legL", 1),
        };

        var roots = SkeletonOutline.Tree(bones);

        var root = Assert.Single(roots);
        Assert.Equal("root", root.Name);
        Assert.Equal(0, root.Depth);
        var hip = Assert.Single(root.Children);
        Assert.Equal("hip", hip.Name);
        Assert.Equal(new[] { "spine", "legL" }, hip.Children.Select(c => c.Name).ToArray());   // index order
        var spine = hip.Children[0];
        Assert.Equal(2, spine.Depth);
        Assert.Equal("head", Assert.Single(spine.Children).Name);
    }

    // Default expansion lives in the VM; SkeletonBoneNode carries no expansion hint.

    [Fact]
    public void Tree_MultipleRoots_AndCycleIsland_SurfaceAtDepthZero_EveryBoneOnce()
    {
        // two clean roots + a malformed 2-cycle island (3→4→3) that the root walk can't reach
        var bones = new List<SubjectBone>
        {
            B("A", -1), B("A_child", 0), B("B", -1), B("x", 4), B("y", 3),
        };

        var roots = SkeletonOutline.Tree(bones);

        // A, B, plus the island's entry bone — all at depth 0; every bone emitted exactly once
        var names = new List<string>();
        void Collect(SkeletonBoneNode n) { names.Add(n.Name); foreach (var c in n.Children) Collect(c); }
        foreach (var r in roots) Collect(r);
        Assert.Equal(5, names.Count);
        foreach (var b in bones) Assert.Contains(b.Name, names);
        Assert.All(roots, r => Assert.Equal(0, r.Depth));
    }

    [Fact]
    public void Tree_Empty_ReturnsEmpty()
    {
        Assert.Empty(SkeletonOutline.Tree(new List<SubjectBone>()));
    }
}
