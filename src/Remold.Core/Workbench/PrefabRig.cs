using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;

namespace Remold.Core.Workbench;

/// <summary>
/// Reads a subject's rig from its assembly-prefab bundle's Transform hierarchy
/// (<see cref="BundleReader.ListTransforms"/>) — no bind-pose/uprighting maths, which the workbench
/// doesn't need. <see cref="Subtree"/> and <see cref="FromTransforms"/> are the pure steps;
/// <see cref="TryRead"/> the live bundle path on top. A hierarchy this can't read yields null plus a
/// <c>problem</c> sentence for the caller to record loudly — never a silent 0-bone skeleton, and never a
/// throw: the skeleton is one display node, so its failure must not cost the caller the rest of the subject.
/// </summary>
public static class PrefabRig
{
    /// <summary>Read the rig under the container root <paramref name="rootName"/>, or null when the bundle
    /// carries no Transform hierarchy. <paramref name="excludeNames"/> drops non-bone Transforms (container
    /// root, renderer-slot GameObjects × LOD tiers) so they don't inflate the bone count — see
    /// <see cref="FromTransforms"/> for how a name collision there is reported, and <see cref="Subtree"/>
    /// for why the root scopes the read.</summary>
    /// <param name="problem">the sentence explaining a null result, or null when the rig simply isn't
    /// there (an absent hierarchy is the caller's own "structure unavailable" note).</param>
    public static SubjectSkeleton? TryRead(BundleReader reader, byte[] deobfuscatedBundle, string rootName,
        ISet<string>? excludeNames, out string? problem)
    {
        problem = null;
        var nodes = reader.ListTransforms(deobfuscatedBundle);
        if (nodes.Count == 0) return null;
        var scoped = Subtree(nodes, rootName, out problem);
        return scoped is null ? null : FromTransforms(scoped, excludeNames, out problem);
    }

    /// <summary>
    /// The Transforms under the container root named <paramref name="rootName"/>, that root included, in
    /// the node list's own order — or null with a <paramref name="problem"/> when the root can't be picked
    /// out.
    ///
    /// <para>A bundle can ship SEVERAL container roots (a model and a variant of it), each with its own
    /// copy of the same rig, so the bone names repeat across them. The read is scoped to one root because
    /// the subject IS one root: the other's Transforms are a different model, and by-name exclusion over
    /// the pooled set would see the two copies as collisions.</para>
    ///
    /// <para>Only a TOP-LEVEL Transform can be the container root, which is what keeps a bone that happens
    /// to share the root's name from being taken for it. Root names are unique among a bundle's top-level
    /// Transforms; a second one carrying the name is refused rather than picked between.</para>
    /// </summary>
    internal static List<BundleReader.TransformNode>? Subtree(
        IReadOnlyList<BundleReader.TransformNode> nodes, string rootName, out string? problem)
    {
        problem = null;
        var ids = new HashSet<long>(nodes.Count);
        foreach (var n in nodes) ids.Add(n.PathId);

        var roots = nodes.Where(n => (n.FatherPathId == 0 || !ids.Contains(n.FatherPathId))
                                     && string.Equals(n.Name, rootName, StringComparison.Ordinal)).ToList();
        if (roots.Count == 0)
        {
            problem = $"Skeleton structure unavailable: the assembly prefab's Transform hierarchy has no "
                + $"top-level '{rootName}' to read the rig under.";
            return null;
        }
        if (roots.Count > 1)
        {
            problem = $"Skeleton structure unavailable: '{rootName}' names {roots.Count} top-level "
                + $"Transforms (path ids {string.Join(", ", roots.Select(r => r.PathId))}), so which one "
                + "holds this subject's rig can't be told.";
            return null;
        }

        var childrenOf = new Dictionary<long, List<BundleReader.TransformNode>>();
        foreach (var n in nodes)
        {
            if (!childrenOf.TryGetValue(n.FatherPathId, out var kids))
                childrenOf[n.FatherPathId] = kids = new List<BundleReader.TransformNode>();
            kids.Add(n);
        }

        var kept = new HashSet<long> { roots[0].PathId };
        var pending = new Stack<long>();
        pending.Push(roots[0].PathId);
        while (pending.Count > 0)
        {
            if (!childrenOf.TryGetValue(pending.Pop(), out var kids)) continue;
            foreach (var kid in kids)
                if (kept.Add(kid.PathId)) pending.Push(kid.PathId);   // Add guards a cyclic parent chain
        }
        return nodes.Where(n => kept.Contains(n.PathId)).ToList();
    }

    /// <summary>
    /// Transform nodes → bone list with parent-index links, in the nodes' own order. A node whose father
    /// path id is 0 or not among the read nodes is a root (<see cref="SubjectBone.ParentIndex"/> = -1).
    /// Null for an empty node set, so the caller surfaces "structure unavailable" rather than a hollow
    /// 0-bone skeleton.
    ///
    /// <para><paramref name="excludeNames"/> removes matching Transforms BEFORE the bone list is built.
    /// Exclusion is by NAME, so an excluded name matching two Transforms would drop a real bone along with
    /// the carrier — invisibly, since a missing bone leaves no trace in the list. That case yields null and
    /// a <paramref name="problem"/> naming both Transforms, rather than guessing which match is the carrier.
    /// A bone whose parent was excluded becomes a root (−1).</para>
    ///
    /// <para>Membership is the caller's: names are matched with <paramref name="excludeNames"/>'s own
    /// comparer, so the collision scan keys on that same comparer when the set exposes one
    /// (<see cref="HashSet{T}.Comparer"/>) and on <see cref="StringComparer.Ordinal"/> otherwise — an
    /// exotic <c>ISet</c> with hidden case rules would under-report collisions, never mis-drop a bone.</para>
    /// </summary>
    /// <param name="problem">the collision sentence, or null (an empty/all-excluded node set is a plain
    /// null result, not a problem of its own).</param>
    public static SubjectSkeleton? FromTransforms(IReadOnlyList<BundleReader.TransformNode> nodes,
        ISet<string>? excludeNames, out string? problem)
    {
        problem = null;
        if (nodes is null || nodes.Count == 0) return null;

        IReadOnlyList<BundleReader.TransformNode> kept;
        if (excludeNames is null || excludeNames.Count == 0) kept = nodes;
        else
        {
            var comparer = excludeNames is HashSet<string> hs ? hs.Comparer : StringComparer.Ordinal;
            var firstSeen = new Dictionary<string, long>(comparer);
            foreach (var n in nodes)
            {
                if (!excludeNames.Contains(n.Name)) continue;
                if (firstSeen.TryGetValue(n.Name, out long first))
                {
                    problem = $"Skeleton structure unavailable: the name '{n.Name}' is carried by two "
                        + $"Transforms (path ids {first} and {n.PathId}). It names a renderer slot or the "
                        + "container root, so excluding it by name would delete the other Transform from "
                        + "the skeleton.";
                    return null;
                }
                firstSeen[n.Name] = n.PathId;
            }
            kept = nodes.Where(n => !excludeNames.Contains(n.Name)).ToList();
        }
        if (kept.Count == 0) return null;   // nothing but slot/root carriers — no rig to show

        // path id → its index in the emitted bone list (nodes are unique by path id within a file)
        var indexOf = new Dictionary<long, int>(kept.Count);
        for (int i = 0; i < kept.Count; i++) indexOf[kept[i].PathId] = i;

        var bones = new List<SubjectBone>(kept.Count);
        foreach (var n in kept)
        {
            int parent = n.FatherPathId != 0 && indexOf.TryGetValue(n.FatherPathId, out var pi) ? pi : -1;
            bones.Add(new SubjectBone(n.Name, parent));
        }
        return new SubjectSkeleton(bones);
    }
}
