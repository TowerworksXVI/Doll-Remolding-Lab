using System.Collections.Generic;

namespace Remold.Core.Workbench;

/// <summary>
/// Renders a <see cref="SubjectSkeleton"/>'s bones as a flat indented string list: DFS order, two spaces
/// per depth level, roots at depth 0.
///
/// <para>Malformed rigs: an out-of-range or self-referential parent index is a root; each bone is emitted
/// once so a cycle can't loop, and a bone the root walk misses is appended at depth 0 — never dropped.</para>
/// </summary>
public static class SkeletonOutline
{
    private const string Indent = "  ";

    public static IReadOnlyList<string> Indented(IReadOnlyList<SubjectBone> bones)
    {
        int n = bones.Count;
        var lines = new List<string>(n);
        if (n == 0) return lines;

        // children adjacency in original index order
        var kids = new List<int>[n];
        var isRoot = new bool[n];
        for (int i = 0; i < n; i++) { kids[i] = new List<int>(); }
        for (int i = 0; i < n; i++)
        {
            int p = bones[i].ParentIndex;
            if (p >= 0 && p < n && p != i) kids[p].Add(i);
            else isRoot[i] = true;
        }

        var visited = new bool[n];
        // iterative DFS (a deep rig must not blow the stack); push roots in reverse so they emit in order
        var stack = new Stack<(int Index, int Depth)>();
        for (int i = n - 1; i >= 0; i--)
            if (isRoot[i]) stack.Push((i, 0));

        Walk(stack, kids, visited, lines, bones);

        // cycle safety: a bone the root walk didn't reach surfaces at depth 0
        for (int i = 0; i < n; i++)
        {
            if (visited[i]) continue;
            var s = new Stack<(int, int)>();
            s.Push((i, 0));
            Walk(s, kids, visited, lines, bones);
        }
        return lines;
    }

    private static void Walk(Stack<(int Index, int Depth)> stack, List<int>[] kids, bool[] visited,
        List<string> lines, IReadOnlyList<SubjectBone> bones)
    {
        while (stack.Count > 0)
        {
            var (i, depth) = stack.Pop();
            if (visited[i]) continue;
            visited[i] = true;
            lines.Add(Repeat(depth) + bones[i].Name);
            var cs = kids[i];
            for (int c = cs.Count - 1; c >= 0; c--)   // reverse push → natural order on pop
                if (!visited[cs[c]]) stack.Push((cs[c], depth + 1));
        }
    }

    private static string Repeat(int depth)
    {
        if (depth <= 0) return "";
        return string.Concat(System.Linq.Enumerable.Repeat(Indent, depth));
    }

    /// <summary>The same DFS hierarchy <see cref="Indented"/> renders flat, as parent→children nodes.
    /// Same robustness: out-of-range/self parent is a root, a cycle island surfaces at depth 0, every bone
    /// appears exactly once. Recursion depth is bounded by the rig's chain length.</summary>
    public static IReadOnlyList<SkeletonBoneNode> Tree(IReadOnlyList<SubjectBone> bones)
    {
        int n = bones.Count;
        if (n == 0) return System.Array.Empty<SkeletonBoneNode>();

        var kids = new List<int>[n];
        var isRoot = new bool[n];
        for (int i = 0; i < n; i++) kids[i] = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int p = bones[i].ParentIndex;
            if (p >= 0 && p < n && p != i) kids[p].Add(i);
            else isRoot[i] = true;
        }

        var visited = new bool[n];
        var roots = new List<SkeletonBoneNode>();
        for (int i = 0; i < n; i++)
            if (isRoot[i] && !visited[i]) roots.Add(BuildNode(i, 0, kids, visited, bones));
        // cycle-island safety: a bone the root walk didn't reach surfaces at depth 0
        for (int i = 0; i < n; i++)
            if (!visited[i]) roots.Add(BuildNode(i, 0, kids, visited, bones));
        return roots;
    }

    private static SkeletonBoneNode BuildNode(int i, int depth, List<int>[] kids, bool[] visited,
        IReadOnlyList<SubjectBone> bones)
    {
        visited[i] = true;
        var children = new List<SkeletonBoneNode>();
        foreach (var c in kids[i])
            if (!visited[c]) children.Add(BuildNode(c, depth + 1, kids, visited, bones));
        return new SkeletonBoneNode(bones[i].Name, depth, children);
    }
}

/// <summary>One node of the read-only skeleton tree: bone name, depth (0 = root), child bones in original
/// index order. The VM (<c>SkeletonNodeVm</c>) owns expand/collapse state.</summary>
public sealed record SkeletonBoneNode(string Name, int Depth, IReadOnlyList<SkeletonBoneNode> Children)
{
    public bool HasChildren => Children.Count > 0;
}
