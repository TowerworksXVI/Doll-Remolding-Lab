using System.Collections.Generic;
using System.Linq;
using System.Text;
using Remold.Core.Bundles;

namespace Remold.Core.Skeleton;

/// <summary>
/// Bone-name-hash → bone path, so the rigged export can name and parent a Blender armature.
///
/// <para>Built from a whole-corpus scan, not from one character: rig transforms live in dedicated rig
/// bundles and the bone <i>set</i> is per-character (corrective bones, per-outfit cloth/prop dynamics),
/// so only the union covers the roster. Bone-refs that don't resolve here belong to PROP meshes whose
/// self-contained rig is anchored at its own local root, hashing locally-anchored paths this
/// root-anchored collection never generates; those are <see cref="SceneRig"/>'s domain.</para>
///
/// <para>Settled negatives: Unity's <c>Avatar.m_TOS</c> adds no paths this scan misses (most dolls ship
/// no Avatar; the ones that exist are prop/NPC sub-assets on a separate <c>Bip001</c> rig), and
/// AnimationClips store path hashes, not strings.</para>
///
/// <para>A runtime value, not a stored artifact — the map lives in the corpus scan's index snapshot,
/// which owns the only cache. An incomplete table never corrupts a mod: painted-weight joints map back
/// by the hash embedded in each glTF node name. Completeness only affects armature naming (UX).</para>
/// </summary>
public sealed class BoneTable
{
    public string CatalogVersion { get; init; } = "unknown";

    /// <summary>Bone-name hash → bone path (e.g. <c>0xb0e35784 → "root/Root_M/Spine1_M"</c>).</summary>
    public Dictionary<uint, string> HashToPath { get; init; } = new();

    public int Count => HashToPath.Count;

    /// <summary>The bone path for a hash, or null if this corpus has no transform that hashes to it.</summary>
    public string? Path(uint hash) => HashToPath.GetValueOrDefault(hash);

    public bool TryGet(uint hash, out string path) => HashToPath.TryGetValue(hash, out path!);

    /// <summary>How many of the given bone-name hashes this table resolves (coverage check helper).</summary>
    public int Resolved(IEnumerable<uint> hashes) => hashes.Count(HashToPath.ContainsKey);

    // ---- the hash rule -----------------------------------------------------

    /// <summary>
    /// CRC32 (zlib / IEEE 802.3, reflected poly <c>0xEDB88320</c>) of a bone path string — the value
    /// a skinned Mesh stores in <c>m_BoneNameHashes</c>.
    /// </summary>
    public static uint Hash(string bonePath)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in Encoding.UTF8.GetBytes(bonePath))
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// The first chain suffix of <paramref name="fullPath"/> whose CRC32 equals
    /// <paramref name="hash"/>, or null when the path does not name that bone.
    /// </summary>
    public static string? MatchingSuffix(uint hash, string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return null;

        var segments = fullPath.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            var suffix = string.Join("/", segments.Skip(i));
            if (Hash(suffix) == hash) return suffix;
        }
        return null;
    }

    /// <summary>The leaf segment of the matching bone-path suffix, for a user-facing single-bone name.</summary>
    public static string? MatchingLeaf(uint hash, string? fullPath)
    {
        var suffix = MatchingSuffix(hash, fullPath);
        if (suffix is null) return null;
        int separator = suffix.LastIndexOf('/');
        string leaf = suffix[(separator + 1)..];
        return leaf.Length > 0 ? leaf : null;
    }

    /// <summary>
    /// The hashed path of a bone: entry node down to the bone, '/'-joined, entry node included.
    /// Character rigs hang under a node named <c>root</c> (<c>root/Root_M/Spine1_M/…</c>); skinned
    /// prop/accessory rigs have no <c>root</c> wrapper and start at <c>Root_M</c>. Anchor on whichever
    /// appears, <c>root</c> first — anchoring rather than stripping a fixed depth keeps the hash correct
    /// however deeply the rig nests under wrappers, and yields exactly one canonical path per transform.
    /// Null for non-skeleton transforms (neither entry node, or <c>root</c> with nothing below it).
    /// </summary>
    public static string? CanonicalBonePath(IReadOnlyList<string> segmentsRootToLeaf)
    {
        int root = -1, rootM = -1;
        for (int i = 0; i < segmentsRootToLeaf.Count; i++)
        {
            if (root < 0 && segmentsRootToLeaf[i] == "root") root = i;
            if (rootM < 0 && segmentsRootToLeaf[i] == "Root_M") rootM = i;
        }
        if (root >= 0 && root + 1 < segmentsRootToLeaf.Count)   // character rig: root is above Root_M
            return string.Join("/", segmentsRootToLeaf.Skip(root));
        if (rootM >= 0)                                          // prop rig: no "root" wrapper
            return string.Join("/", segmentsRootToLeaf.Skip(rootM));
        return null;
    }

    /// <summary>The runtime spring-chain bone-path hashes: the <c>Spring01–06</c>, <c>SpringA01–07</c>
    /// and <c>SpringB01–07</c> chains of the charm accessory rigs, one hash per chain depth, all anchored
    /// at <c>Root_M</c> (catalog 26109). The game drives these chains with its own runtime simulation —
    /// they settle and swing on their own, with no animation clip behind them.</summary>
    private static readonly HashSet<uint> SpringChainBones = new()
    {
        0x05f0c65f, 0xfa2995bf, 0xb62e3dbd, 0xc2298838, 0x52ee5434, 0x68bd228f,             // Spring01–06
        0x34256032, 0xd5bafb58, 0x7fcf886a, 0x43e8a308, 0x7d29ffc4, 0x5c956b95, 0x02ae5487, // SpringA01–07
        0x3663de6b, 0x6a3629cf, 0xb6a663c0, 0xd196f9b7, 0x2663be76, 0x84b6fae4, 0x2b587a92, // SpringB01–07
    };

    /// <summary>Whether a mesh's bone set rides a runtime spring chain. Such a mesh takes its motion
    /// from simulation the build does not author against, so its geometry is not offered for Replace;
    /// retexture and hide are unaffected. Tested on hashes so the answer needs no name resolution.</summary>
    public static bool HasSpringChain(IEnumerable<uint> boneNameHashes) =>
        boneNameHashes.Any(SpringChainBones.Contains);

    /// <summary>The accessory-dynamics bone-path hashes this build path has no support for: per-outfit
    /// chains that hang outside the shared skeleton and take their motion from their own animation, so a
    /// pool read has no sibling space to restate them in (catalog 26109, measured over the corpus scan —
    /// none of these appears on a mesh the build supports).</summary>
    private static readonly HashSet<uint> UnsupportedRigBones = new()
    {
        0x051acc4a, 0x0fc15fa1, 0x212373c4, 0x90c776f9,
        0x94bf642a, 0x9bf044a8, 0xd6304faa, 0xff05caee,
    };

    /// <summary>Whether a mesh's bone set rides a rig outside the supported set. Such a mesh is declined
    /// like any other unsupported asset; retexture and hide are unaffected. Tested on hashes so the
    /// answer needs no name resolution.</summary>
    public static bool HasUnsupportedRig(IEnumerable<uint> boneNameHashes) =>
        boneNameHashes.Any(UnsupportedRigBones.Contains);

    /// <summary>Segment names root→leaf up a transform's parent chain. Visited set guards cycles in
    /// malformed hierarchies.</summary>
    private static List<string> ChainToRoot(Dictionary<long, BundleReader.TransformNode> byId, long pathId)
    {
        var segs = new List<string>();
        var seen = new HashSet<long>();
        long cur = pathId;
        while (cur != 0 && byId.TryGetValue(cur, out var node) && seen.Add(cur))
        {
            segs.Add(node.Name);
            cur = node.FatherPathId;
        }
        segs.Reverse();
        return segs;
    }

    // ---- building ----------------------------------------------------------

    /// <summary>Fold one bundle's Transform nodes into a partial hash→path map, so the corpus scan can
    /// feed it from the same deobfuscate pass that builds the asset index. First path wins per hash.</summary>
    public static void CollectNodes(IReadOnlyList<BundleReader.TransformNode> nodes, Dictionary<uint, string> partial)
    {
        if (nodes.Count == 0) return;
        var byId = nodes.ToDictionary(n => n.PathId);
        foreach (var n in nodes)
        {
            var path = CanonicalBonePath(ChainToRoot(byId, n.PathId));
            if (path is not null) partial.TryAdd(Hash(path), path);
        }
    }

    /// <summary>Wrap an already-merged hash→path map as a finished table.</summary>
    public static BoneTable FromMap(string catalogVersion, Dictionary<uint, string> map) =>
        new() { CatalogVersion = catalogVersion, HashToPath = map };
}
