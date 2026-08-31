using System.Collections.Generic;
using System.Linq;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// <see cref="SkeletonNodeVm"/> expansion: every node collapsed by default, and expanding a single-child
/// node chain-expands its child recursively while collapse never cascades. The cascade lives in the VM,
/// so these drive it with no Avalonia runtime.
/// </summary>
public class SkeletonNodeVmTests
{
    private static SkeletonBoneNode Node(string name, int depth, params SkeletonBoneNode[] kids) =>
        new(name, depth, kids);

    private static IEnumerable<SkeletonNodeVm> SelfAndDescendants(SkeletonNodeVm n)
    {
        yield return n;
        foreach (var c in n.Children)
            foreach (var d in SelfAndDescendants(c))
                yield return d;
    }

    [Fact]
    public void Default_AllNodesCollapsed_IncludingRoot()
    {
        // root → hip → spine, plus a second child on hip, so both a chain and a branch are covered
        var tree = Node("root", 0, Node("hip", 1, Node("spine", 2), Node("legL", 2)));
        var vm = new SkeletonNodeVm(tree);

        Assert.All(SelfAndDescendants(vm), n => Assert.False(n.IsExpanded));
    }

    [Fact]
    public void Expand_SingleChildChain_CascadesThroughThreeDeep()
    {
        // a straight single-child chain: root → a → b → c (c is a leaf)
        var vm = new SkeletonNodeVm(Node("root", 0, Node("a", 1, Node("b", 2, Node("c", 3)))));

        vm.IsExpanded = true;   // one click on the root

        var a = vm.Children[0];
        var b = a.Children[0];
        var c = b.Children[0];
        Assert.True(vm.IsExpanded);
        Assert.True(a.IsExpanded);
        Assert.True(b.IsExpanded);
        Assert.True(c.IsExpanded);   // whole chain opened in one click
    }

    [Fact]
    public void Expand_StopsAtFirstNodeWithTwoChildren()
    {
        // root (1 child) → a (2 children: b, c). Expanding root opens a; a has 2 children so the cascade stops.
        var vm = new SkeletonNodeVm(Node("root", 0, Node("a", 1, Node("b", 2), Node("c", 2))));

        vm.IsExpanded = true;

        var a = vm.Children[0];
        Assert.True(vm.IsExpanded);
        Assert.True(a.IsExpanded);            // a is the single child of root → opened
        Assert.False(a.Children[0].IsExpanded);   // a has 2 children → cascade stops, b stays collapsed
        Assert.False(a.Children[1].IsExpanded);   // c stays collapsed
    }

    [Fact]
    public void Expand_RootWithTwoChildren_DoesNotCascade()
    {
        // root has 2 children directly → expanding it opens nothing below
        var vm = new SkeletonNodeVm(Node("root", 0, Node("a", 1), Node("b", 1)));

        vm.IsExpanded = true;

        Assert.True(vm.IsExpanded);
        Assert.All(vm.Children, c => Assert.False(c.IsExpanded));
    }

    [Fact]
    public void Collapse_DoesNotCascade()
    {
        // open the whole single-child chain, then collapse only the root — descendants stay open
        var vm = new SkeletonNodeVm(Node("root", 0, Node("a", 1, Node("b", 2, Node("c", 3)))));
        vm.IsExpanded = true;
        var a = vm.Children[0];
        var b = a.Children[0];
        var c = b.Children[0];

        vm.IsExpanded = false;   // collapse the root only

        Assert.False(vm.IsExpanded);
        Assert.True(a.IsExpanded);   // collapse is not recursive
        Assert.True(b.IsExpanded);
        Assert.True(c.IsExpanded);
    }
}
