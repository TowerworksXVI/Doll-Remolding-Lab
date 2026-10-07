using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;

namespace Remold.Core.Bundles;

/// <summary>One asset object located inside a bundle.</summary>
public readonly record struct AssetEntry(string Name, long PathId, int ClassId);

/// <summary>
/// Reads a live (obfuscated) GFL2 bundle: deobfuscates in memory, opens it with AssetsTools.NET, and
/// enumerates its assets. Relies on the bundles' EMBEDDED type trees — no shipped classdata.tpk, so a
/// GetBaseField throwing for lack of a class database means that assumption broke.
/// </summary>
public sealed class BundleReader
{
    // Opening a bundle decompresses it whole, and one bundle answers many reads in a row (a part's mesh,
    // its tiers' hashes, its materials' textures). An INSTANCE therefore keeps what it has opened, keyed by
    // the byte array it was handed — reference identity, so a caller that re-deobfuscates gets a fresh
    // parse and a caller reusing its buffer gets the open one. Every read returns its own value tree, so a
    // caller that rewrites a field never disturbs the next read (pinned by test). An instance is stateful
    // and NOT thread-safe: one reader per thread of work.
    //
    // The kept parses are BOUNDED (least-recently-used evicted, its manager unloaded): a decompressed
    // bundle is tens of megabytes, and a reader that outlives one operation — a subject scope, a whole
    // build — would otherwise pin every bundle it ever touched. A read that comes back to an evicted
    // bundle re-parses it, which is what an unshared reader would have done anyway.
    private const int MaxParsed = 8;

    private readonly Dictionary<byte[], (AssetsManager Manager, BundleFileInstance Bundle, AssetsFileInstance File)>
        _parsed = new((IEqualityComparer<byte[]>)ReferenceEqualityComparer.Instance);

    // Each kept parse's m_Container, reduced to the Mesh objects of this file each key files — decoded once
    // per parse, however many load keys are asked of it, and dropped with the parse.
    private readonly Dictionary<byte[], Dictionary<string, List<long>>> _containerMeshes =
        new((IEqualityComparer<byte[]>)ReferenceEqualityComparer.Instance);

    // most-recently-used last
    private readonly List<byte[]> _recency = new();

    private (AssetsManager Manager, BundleFileInstance Bundle, AssetsFileInstance File) Parse(byte[] deobfuscatedBundle)
    {
        if (_parsed.TryGetValue(deobfuscatedBundle, out var have))
        {
            Touch(deobfuscatedBundle);
            return have;
        }
        while (_recency.Count >= MaxParsed) Evict();
        var am = new AssetsManager();
        var bun = am.LoadBundleFile(new MemoryStream(deobfuscatedBundle), "live.bundle");
        _recency.Add(deobfuscatedBundle);
        return _parsed[deobfuscatedBundle] = (am, bun, am.LoadAssetsFileFromBundle(bun, 0));
    }

    private void Touch(byte[] key)
    {
        int at = _recency.FindIndex(b => ReferenceEquals(b, key));
        if (at < 0 || at == _recency.Count - 1) return;
        _recency.RemoveAt(at);
        _recency.Add(key);
    }

    /// <summary>Drop the least recently read parse, releasing its decompressed bundle. Every field this
    /// reader has handed out carries its own values, so an evicted parse takes nothing a caller still
    /// holds.</summary>
    private void Evict()
    {
        var oldest = _recency[0];
        _recency.RemoveAt(0);
        _containerMeshes.Remove(oldest);
        if (!_parsed.Remove(oldest, out var entry)) return;
        try { entry.Manager.UnloadAll(); } catch { /* already unloaded — the parse is gone either way */ }
    }

    public const int ClassGameObject = 1;
    public const int ClassTransform = 4;
    public const int ClassMesh = 43;
    public const int ClassTexture2D = 28;
    // prefab-ingestion class ids: the assembly-prefab detector reads these
    public const int ClassMeshRenderer = 23;
    /// <summary>A static renderer's mesh lives on the MeshFilter beside it, not on the renderer itself —
    /// the two are joined through the GameObject they share.</summary>
    public const int ClassMeshFilter = 33;
    public const int ClassMonoBehaviour = 114;
    public const int ClassSkinnedMeshRenderer = 137;

    /// <summary>One Transform in a bundle's scene hierarchy: its GameObject's <c>m_Name</c> and its parent
    /// Transform's path id (<c>0</c> = no parent). Reconstructing the chain to the root and CRC32-hashing it
    /// recovers a skeleton's bone-name hashes (see <see cref="Skeleton.BoneTable"/>).</summary>
    public readonly record struct TransformNode(long PathId, string Name, long FatherPathId);

    /// <summary>Enumerate a deobfuscated bundle's Transform hierarchy. Empty when the bundle carries no
    /// transforms, which most don't — the early-out avoids reading anything. Characters are
    /// runtime-assembled, so their skeleton transforms live in dedicated rig bundles, not in the mesh
    /// bundle.</summary>
    public List<TransformNode> ListTransforms(byte[] deobfuscatedBundle)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        if (!inst.file.AssetInfos.Any(i => i.TypeId == ClassTransform)) return new();

        var goName = new Dictionary<long, string>();
        var raw = new List<(long pid, long go, long father)>();
        foreach (var info in inst.file.AssetInfos)
            CollectTransformInfo(am, inst, info, goName, raw);
        return AssembleNodes(raw, goName);
    }

    /// <summary>Fold one asset's header into the skeleton accumulators: a GameObject contributes its
    /// <c>m_Name</c>, a Transform its GameObject + parent path ids.</summary>
    private static void CollectTransformInfo(AssetsManager am, AssetsFileInstance inst, AssetFileInfo info,
        Dictionary<long, string> goName, List<(long pid, long go, long father)> raw)
    {
        if (info.TypeId == ClassGameObject)
        {
            try { goName[info.PathId] = am.GetBaseField(inst, info)["m_Name"].AsString; } catch { }
        }
        else if (info.TypeId == ClassTransform)
        {
            try
            {
                var bf = am.GetBaseField(inst, info);
                raw.Add((info.PathId, bf["m_GameObject"]["m_PathID"].AsLong, bf["m_Father"]["m_PathID"].AsLong));
            }
            catch { }
        }
    }

    /// <summary>Resolve each raw (transform, gameobject, father) tuple to a named
    /// <see cref="TransformNode"/>.</summary>
    private static List<TransformNode> AssembleNodes(List<(long pid, long go, long father)> raw,
        Dictionary<long, string> goName)
    {
        var result = new List<TransformNode>(raw.Count);
        foreach (var (pid, go, father) in raw)
            result.Add(new TransformNode(pid, goName.GetValueOrDefault(go, ""), father));
        return result;
    }

    /// <summary>The rig facts a part's placement is read from, off the skinned renderer at
    /// <paramref name="rendererPathId"/>: the Transform chain from the top of the file down to the renderer's
    /// root bone, each link with its saved local pose (row-vector), the Avatar named by the nearest Animator
    /// on the renderer's GameObject or an ancestor of it, and that Animator's saved world in the file
    /// (<paramref name="rendererPathId"/>'s <c>AvatarFrame</c>: where the Avatar's root sits among the file's
    /// other rigs). The Avatar is null, and the frame the identity, when no ancestor carries an Animator or
    /// the Animator names none. Null overall when the object is no skinned renderer or its root bone is not a
    /// Transform of this file.</summary>
    public (IReadOnlyList<(string Name, Matrix4x4 Local)> RootChain, PrefabAvatarRef? Avatar, Matrix4x4 AvatarFrame)?
        RendererRig(byte[] deobfuscatedBundle, long rendererPathId)
    {
        var (am, _, inst) = Parse(deobfuscatedBundle);
        AssetTypeValueField? Read(long pathId, int classId) =>
            inst.file.GetAssetInfo(pathId) is { } info && info.TypeId == classId ? am.GetBaseField(inst, info) : null;
        if (Read(rendererPathId, ClassSkinnedMeshRenderer) is not { } smr) return null;
        var root = smr["m_RootBone"];
        if (root["m_FileID"].AsInt != 0 || Read(root["m_PathID"].AsLong, ClassTransform) is null) return null;

        AssetTypeValueField? Father(AssetTypeValueField transform) =>
            transform["m_Father"]["m_FileID"].AsInt == 0 ? Read(transform["m_Father"]["m_PathID"].AsLong, ClassTransform) : null;

        var chain = new List<(string, Matrix4x4)>();
        var seen = new HashSet<long>();
        for (var t = Read(root["m_PathID"].AsLong, ClassTransform); t is not null; t = Father(t))
        {
            if (!seen.Add(t["m_GameObject"]["m_PathID"].AsLong)) return null;
            chain.Add((Read(t["m_GameObject"]["m_PathID"].AsLong, ClassGameObject)?["m_Name"].AsString ?? "",
                Trs(t["m_LocalPosition"], t["m_LocalRotation"], t["m_LocalScale"])));
        }
        chain.Reverse();

        seen.Clear();
        for (var go = Read(smr["m_GameObject"]["m_PathID"].AsLong, ClassGameObject); go is not null;)
        {
            AssetTypeValueField? transform = null, animator = null;
            foreach (var entry in UnwrapArray(go["m_Component"]))
            {
                if (FindPtr(entry) is not { } c || c["m_FileID"].AsInt != 0) continue;
                animator ??= Read(c["m_PathID"].AsLong, ClassAnimator);
                transform ??= Read(c["m_PathID"].AsLong, ClassTransform);
            }
            if (animator is not null)
            {
                var pose = animator["m_Avatar"];
                int file = pose["m_FileID"].AsInt;
                if (pose["m_PathID"].AsLong == 0) return (chain, null, Matrix4x4.Identity);
                // the Animator's own saved world, composed up to the top of the file
                var frame = Matrix4x4.Identity;
                var walked = new HashSet<long>();
                for (var t = transform; t is not null; t = Father(t))
                {
                    if (!walked.Add(t["m_GameObject"]["m_PathID"].AsLong)) return null;
                    frame *= Trs(t["m_LocalPosition"], t["m_LocalRotation"], t["m_LocalScale"]);
                }
                return (chain, new PrefabAvatarRef(pose["m_PathID"].AsLong, file == 0 ? null : ExternalCabs(inst)[file - 1]),
                    frame);
            }
            var up = transform is null ? null : Father(transform);
            go = up is null || !seen.Add(up["m_GameObject"]["m_PathID"].AsLong) ? null
                : Read(up["m_GameObject"]["m_PathID"].AsLong, ClassGameObject);
        }
        return (chain, null, Matrix4x4.Identity);
    }

    /// <summary>A saved local pose as a row-vector matrix: scale, then rotation, then translation.</summary>
    private static Matrix4x4 Trs(AssetTypeValueField t, AssetTypeValueField q, AssetTypeValueField s) =>
        Matrix4x4.CreateScale(s["x"].AsFloat, s["y"].AsFloat, s["z"].AsFloat)
        * Matrix4x4.CreateFromQuaternion(new Quaternion(q["x"].AsFloat, q["y"].AsFloat, q["z"].AsFloat, q["w"].AsFloat))
        * Matrix4x4.CreateTranslation(t["x"].AsFloat, t["y"].AsFloat, t["z"].AsFloat);

    public const int ClassAnimator = 95;
    public const int ClassAvatar = 90;

    /// <summary>The rest world of every bone an Avatar describes, keyed by the bone-name hash a skinned mesh
    /// stores for it (the Avatar's own node id), relative to the Avatar's root node — the Animator's own
    /// object, which <see cref="RendererRig"/>'s frame places in the file — row-vector. This is the pose the
    /// rig was set up in, which a prefab's saved Transforms need not be: an enemy prefab is often saved
    /// mid-pose. Throws when the object is no Avatar or its pose is malformed.</summary>
    public IReadOnlyDictionary<uint, Matrix4x4> RigRestPose(byte[] deobfuscatedBundle, long avatarPathId)
    {
        var (am, _, inst) = Parse(deobfuscatedBundle);
        if (inst.file.GetAssetInfo(avatarPathId) is not { TypeId: ClassAvatar } info)
            throw new InvalidDataException("the rig's saved pose is missing from its bundle");
        var data = am.GetBaseField(inst, info)["m_Avatar"];
        var nodes = UnwrapArray(data["m_AvatarSkeleton"]["data"]["m_Node"]);
        var ids = UnwrapArray(data["m_AvatarSkeleton"]["data"]["m_ID"]);
        var poses = UnwrapArray(data["m_AvatarSkeletonPose"]["data"]["m_X"]);
        if (nodes.Count == 0 || ids.Count != nodes.Count || poses.Count != nodes.Count)
            throw new InvalidDataException("the rig's saved pose has mismatched bone counts");
        var worlds = new Matrix4x4[nodes.Count];
        var rest = new Dictionary<uint, Matrix4x4>(nodes.Count);
        for (int i = 0; i < nodes.Count; i++)
        {
            // Unity serializes the skeleton parents-first, so a node's parent is always already placed
            int parent = nodes[i]["m_ParentId"].AsInt;
            if (parent >= i) throw new InvalidDataException("the rig's saved pose lists a bone before its parent");
            var local = Trs(poses[i]["t"], poses[i]["q"], poses[i]["s"]);
            worlds[i] = parent < 0 ? local : local * worlds[parent];
        }
        if (!Matrix4x4.Invert(worlds[0], out var fromRoot))
            throw new InvalidDataException("the rig's saved pose has a root that won't invert");
        for (int i = 0; i < nodes.Count; i++)
            if (!rest.TryAdd(ids[i].AsUInt, worlds[i] * fromRoot))
                throw new InvalidDataException("the rig's saved pose names one bone twice");
        return rest;
    }

    /// <summary>Deobfuscate a live bundle file from disk into plain UnityFS bytes.</summary>
    public static byte[] DeobfuscateFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        BundleObfuscation.Deobfuscate(bytes);
        return bytes;
    }

    /// <summary>Class id of the AssetBundle manifest object every shipped bundle carries. Its <c>m_Name</c>
    /// is the bundle's LOGICAL name — the id the game's VFS manifest and Addressables catalog address it by,
    /// distinct from the physical filename even for single-bundle files.</summary>
    public const int ClassAssetBundle = 142;

    /// <summary>A deobfuscated bundle's self-declared LOGICAL name: the <c>m_Name</c> of its AssetBundle
    /// object, verbatim (live bundles carry a <c>.bundle</c> suffix; names are unique corpus-wide). Every
    /// shipped segment carries exactly one such object; none, several, or a blank name is refused loudly,
    /// since keying an identity on a guess would mislabel the segment everywhere.</summary>
    public string GetBundleName(byte[] deobfuscatedBundle)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        var infos = inst.file.AssetInfos.Where(i => i.TypeId == ClassAssetBundle).ToList();
        if (infos.Count != 1)
            throw new InvalidDataException(
                $"bundle carries {infos.Count} AssetBundle objects (expected exactly 1). Cannot self-identify its logical name");
        string name;
        try { name = am.GetBaseField(inst, infos[0])["m_Name"].AsString; }
        catch (Exception ex)
        {
            throw new InvalidDataException("bundle's AssetBundle object has an unreadable m_Name", ex);
        }
        if (string.IsNullOrEmpty(name))
            throw new InvalidDataException("bundle's AssetBundle object declares an empty m_Name");
        return name;
    }

    /// <summary>Enumerate assets of the given class ids (default: Mesh) with their <c>m_Name</c>.</summary>
    public List<AssetEntry> ListAssets(byte[] deobfuscatedBundle, params int[] classIds)
        => ListAssetsWithCab(deobfuscatedBundle, classIds).Assets;

    /// <summary>Class id of a Material asset — its <c>m_SavedProperties.m_TexEnvs</c> PPtrs are the
    /// ground-truth mesh→texture link for outfits that ship one.</summary>
    public const int ClassMaterial = 21;

    /// <summary>Like <see cref="ListAssets"/>, but also returns the bundle's internal SerializedFile (CAB)
    /// name — the token an external <c>m_FileID</c> PPtr references, so a material's texture pointer can be
    /// followed across bundles. Reads asset file index 0: corpus-wide, file 0 holds EVERY
    /// <c>c_</c>/<c>cw_</c> model Mesh/Material/Texture2D.</summary>
    public (string Cab, List<AssetEntry> Assets) ListAssetsWithCab(byte[] deobfuscatedBundle, params int[] classIds)
    {
        var wanted = classIds.Length == 0 ? new[] { ClassMesh } : classIds;
        var wantedSet = wanted.ToHashSet();

        var (am, bun, inst) = Parse(deobfuscatedBundle);

        // Capture the CAB FIRST so a single unreadable asset never costs the bundle's CAB→hash entry — a
        // dependency target other bundles' PPtrs resolve through.
        var cab = inst.name ?? "";
        var result = new List<AssetEntry>();
        foreach (var info in inst.file.AssetInfos)
        {
            if (!wantedSet.Contains(info.TypeId)) continue;
            try { result.Add(new AssetEntry(ReadAssetName(am, inst, info), info.PathId, info.TypeId)); }
            catch { /* unreadable name (odd embedded type tree) — skip this asset, keep the rest + CAB */ }
        }
        return (cab, result);
    }

    /// <summary>The bundle's external dependency CAB names (1-based by <c>FileID</c>), matching
    /// <see cref="GetMaterialTexEnvs"/> — the token an external PPtr resolves through.</summary>
    private static IReadOnlyList<string> ExternalCabs(AssetsFileInstance inst) =>
        inst.file.Metadata.Externals.Select(e => e.PathName.Split('/')[^1]).ToList();

    /// <summary>A Unity vector/array field nests its elements under a single <c>Array</c> child; return
    /// the element list (empty for a dummy/absent field).</summary>
    internal static IReadOnlyList<AssetTypeValueField> UnwrapArray(AssetTypeValueField f)
    {
        if (f is null || f.IsDummy) return Array.Empty<AssetTypeValueField>();
        var arr = f.Children.Count == 1 && (f.Children[0].FieldName == "Array" || f.Children[0].FieldName == "data")
            ? f.Children[0] : f;
        return arr.Children;
    }

    /// <summary>A PPtr is either the field itself (has <c>m_PathID</c>) or wrapped one level down
    /// (e.g. a container entry's pair); return the field carrying it, or null.</summary>
    internal static AssetTypeValueField? FindPtr(AssetTypeValueField e)
    {
        if (e is null || e.IsDummy) return null;
        if (!e["m_PathID"].IsDummy) return e;
        foreach (var ch in e.Children)
            if (!ch["m_PathID"].IsDummy) return ch;
        return null;
    }

    /// <summary>The <c>m_Name</c> of a wanted asset, via the full
    /// <see cref="AssetsManager.GetBaseField"/> path.</summary>
    private static string ReadAssetName(AssetsManager am, AssetsFileInstance inst, AssetFileInfo info) =>
        am.GetBaseField(inst, info)["m_Name"].AsString;

    /// <summary>THE selection every read of one Mesh makes (<see cref="MeshSelector"/>): the object an exact
    /// path id names; else the one the load key files — among several a key files (a model's sub-assets),
    /// the one of this name; else the first Mesh of this name. A load key that files no single Mesh of this
    /// file selects nothing: the key is what the game loads, so a name read in its place would read an
    /// object the game may never draw.</summary>
    private AssetFileInfo? FindMesh(byte[] deobfuscatedBundle, AssetsManager am, AssetsFileInstance inst,
        string meshName, MeshSelector which) =>
        SelectMesh(am, inst, meshName, which, () => ContainerMeshes(deobfuscatedBundle, am, inst));

    /// <summary><see cref="FindMesh"/> over a file some other reader loaded, for a caller that parses a bundle
    /// itself. <paramref name="containerMeshes"/> is asked only for a load key.</summary>
    internal static AssetFileInfo? SelectMesh(AssetsManager am, AssetsFileInstance inst, string meshName,
        MeshSelector which, Func<Dictionary<string, List<long>>> containerMeshes)
    {
        if (which.IsExact)
            return inst.file.GetAssetInfo(which.PathId) is { TypeId: ClassMesh } exact ? exact : null;
        if (which.LoadKey is { } key)
        {
            if (!containerMeshes().TryGetValue(key, out var filed)) return null;
            var named = filed.Count == 1 ? filed
                : filed.Where(id => am.GetBaseField(inst, inst.file.GetAssetInfo(id))["m_Name"].AsString == meshName)
                    .ToList();
            return named.Count == 1 ? inst.file.GetAssetInfo(named[0]) : null;
        }
        foreach (var info in inst.file.AssetInfos)
            if (info.TypeId == ClassMesh && am.GetBaseField(inst, info)["m_Name"].AsString == meshName) return info;
        return null;
    }

    /// <summary>This parse's container keys to the Mesh objects of this file each files, keys compared
    /// case-insensitively as a bundle's own load compares them. Decoded once per kept parse.</summary>
    private Dictionary<string, List<long>> ContainerMeshes(byte[] deobfuscatedBundle, AssetsManager am,
        AssetsFileInstance inst)
    {
        if (_containerMeshes.TryGetValue(deobfuscatedBundle, out var have)) return have;
        return _containerMeshes[deobfuscatedBundle] = ReadContainerMeshes(am, inst);
    }

    /// <summary>A loaded file's container keys to the Mesh objects of this file each files.</summary>
    internal static Dictionary<string, List<long>> ReadContainerMeshes(AssetsManager am, AssetsFileInstance inst)
    {
        var map = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        var abInfo = inst.file.AssetInfos.FirstOrDefault(i => i.TypeId == ClassAssetBundle);
        if (abInfo is not null)
            foreach (var entry in UnwrapArray(am.GetBaseField(inst, abInfo)["m_Container"]))
            {
                var asset = FindPtr(entry["second"]["asset"].IsDummy ? entry["second"] : entry["second"]["asset"]);
                if (asset is null || asset["m_FileID"].AsInt != 0) continue;
                if (inst.file.GetAssetInfo(asset["m_PathID"].AsLong) is not { TypeId: ClassMesh } info) continue;
                string key = entry["first"].AsString;
                if (!map.TryGetValue(key, out var ids)) map[key] = ids = new List<long>();
                if (!ids.Contains(info.PathId)) ids.Add(info.PathId);
            }
        return map;
    }

    /// <summary>The path id of the Mesh <paramref name="which"/> selects, or null when it selects none —
    /// for a caller that records the exact object rather than reading it.</summary>
    public long? MeshPathId(byte[] deobfuscatedBundle, string meshName, MeshSelector which)
    {
        var (am, _, inst) = Parse(deobfuscatedBundle);
        return FindMesh(deobfuscatedBundle, am, inst, meshName, which)?.PathId;
    }

    /// <summary>The deserialized type-tree field for the Mesh <paramref name="which"/> selects (see
    /// <see cref="MeshSelector"/>). The field carries its own data, so it stays usable after this
    /// returns.</summary>
    public AssetTypeValueField? GetMeshField(byte[] deobfuscatedBundle, string meshName, MeshSelector which = default)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        if (FindMesh(deobfuscatedBundle, am, inst, meshName, which) is not { } info) return null;
        var bf = am.GetBaseField(inst, info);
        ResolveStreamedVertexData(bf, bun);   // pull a streamed vertex buffer inline so Decode works
        return bf;
    }

    /// <summary>A non-readable mesh stores its vertex buffer in a <c>.resS</c> resource (the mesh-level
    /// <c>m_StreamData</c>), leaving <c>m_VertexData.m_DataSize</c> empty, so a plain decode reads an empty
    /// buffer and throws. Read the resource slice out of the same bundle and inline it into
    /// <c>m_DataSize</c>. The index buffer stays inline — Unity streams only the vertex buffer. Best-effort:
    /// any missing piece leaves the field as-is and the decode then fails, recorded per-asset.</summary>
    private static void ResolveStreamedVertexData(AssetTypeValueField mesh, BundleFileInstance bun)
    {
        var sd = mesh["m_StreamData"];
        if (sd.IsDummy) return;
        long size = sd["size"].AsLong;
        if (size <= 0) return;
        var dataSize = mesh["m_VertexData"]["m_DataSize"];
        if ((dataSize.AsByteArray?.Length ?? 0) > 0) return;   // already inline — nothing to resolve

        var path = sd["path"].AsString;
        if (string.IsNullOrEmpty(path)) return;
        var resName = path.Split('/')[^1];                     // archive:/CAB-x/CAB-x.resS → CAB-x.resS
        int ri = bun.file.GetAllFileNames().IndexOf(resName);
        if (ri < 0) return;

        var res = BundleHelper.LoadAssetDataFromBundle(bun.file, ri);
        long offset = sd["offset"].AsLong;
        if (offset < 0 || offset + size > res.Length) return;
        var blob = new byte[size];
        Array.Copy(res, offset, blob, 0, size);
        dataSize.Value = new AssetTypeValue(AssetValueType.ByteArray, blob);
    }

    /// <summary><c>"sha256:&lt;hex&gt;"</c> of a named asset's serialized type-tree bytes (the source_hash
    /// scope). For a streamed Texture2D this covers the object + StreamingInfo, not the <c>.resS</c> pixels —
    /// the same object-bytes scope hashed at apply time.</summary>
    public string? GetAssetHash(byte[] deobfuscatedBundle, int classId, string name, long pathId = 0)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        foreach (var info in inst.file.AssetInfos)
        {
            if (info.TypeId != classId) continue;
            if (pathId != 0) { if (info.PathId != pathId) continue; }
            var bf = am.GetBaseField(inst, info);
            if (pathId != 0 || bf["m_Name"].AsString == name) return AssetHash.Sha256(bf.WriteToByteArray());
        }
        return null;
    }

    /// <summary>The asset's <b>raw on-disk serialized bytes</b> — the exact slice the SerializedFile stores
    /// for this object — by class + name. Used to check <c>source_hash</c> parity against apply-time
    /// reads.</summary>
    public byte[]? GetAssetRawBytes(byte[] deobfuscatedBundle, int classId, string name)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        foreach (var info in inst.file.AssetInfos)
        {
            if (info.TypeId != classId) continue;
            if (am.GetBaseField(inst, info)["m_Name"].AsString != name) continue;
            var reader = inst.file.Reader;
            reader.Position = info.GetAbsoluteByteOffset(inst.file);
            return reader.ReadBytes((int)info.ByteSize);
        }
        return null;
    }

    /// <summary>Decoded Texture2D pixels (BGRA32, top-left origin) plus dimensions.</summary>
    public readonly record struct DecodedTexture(byte[] Bgra, int Width, int Height, string Format);

    /// <summary>A live Texture2D's encode parameters: the Unity <c>m_TextureFormat</c> integer, dimensions,
    /// and mip level count — what the package-time pre-encode needs to produce an acceptable
    /// blob.</summary>
    public readonly record struct TextureMeta(int Format, int Width, int Height, int MipCount);

    /// <summary>Read a Texture2D's format/dimensions/mip count without decoding the pixels. Captured at
    /// export onto the project target so the package build can pre-encode offline; also the live fallback
    /// (gated on <c>source_hash</c>) for a project with no recorded metadata.</summary>
    public TextureMeta? GetTextureMeta(byte[] deobfuscatedBundle, TextureRef which)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        foreach (var info in Texture2Ds(am, inst, which))
        {
            var tex = TextureFile.ReadTextureFile(am.GetBaseField(inst, info));
            return new TextureMeta(tex.m_TextureFormat, tex.m_Width, tex.m_Height, tex.m_MipCount);
        }
        return null;
    }

    /// <summary>The live picture data of a Texture2D exactly as the bundle stores it (every mip, largest
    /// first, in the target's <c>m_TextureFormat</c>) plus its format/dims/mips — the copy an UNCHANGED
    /// target ships as its texture blob. Null when the texture is absent, or when its format isn't
    /// size-computable by the codec; the caller then aborts the build, matching the edited-texture
    /// abort.</summary>
    public (byte[] PictureData, TextureMeta Meta)? GetTexturePictureData(byte[] deobfuscatedBundle, string textureName)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        foreach (var info in inst.file.AssetInfos)
        {
            if (info.TypeId != ClassTexture2D) continue;
            var bf = am.GetBaseField(inst, info);
            if (bf["m_Name"].AsString != textureName) continue;
            var tex = TextureFile.ReadTextureFile(bf);
            if (!Textures.TextureCodec.IsSupported((TextureFormat)tex.m_TextureFormat)) return null;
            byte[] picture = tex.FillPictureData(inst);   // resolves streamed .resS into inline bytes
            return (picture, new TextureMeta(tex.m_TextureFormat, tex.m_Width, tex.m_Height, tex.m_MipCount));
        }
        return null;
    }

    /// <summary>Everything the offline 3DMigoto resource hash of a stock texture is computed from: its packed
    /// image data (every mip, largest first — the hash reads mip 0 off the front), its dimensions, its REAL
    /// mip count and Unity texture format, and whether it is sRGB (which decides the DXGI format number the
    /// runtime creates it with). No format gate: an unhashable format is the caller's loud
    /// failure.</summary>
    public readonly record struct TextureHashSource(
        byte[] PictureData, int Width, int Height, int MipCount, int Format, bool Srgb);

    /// <summary>Which Texture2D in a bundle a caller means. <see cref="PathId"/> is the game's own identity
    /// and selects exactly one object; the name does not — a bundle can ship many same-named textures (every
    /// toon ramp in a ramp library is called <c>RampMap_Linear_RGBAHalf</c>), and a name-selected read takes
    /// whichever comes first. Name selection exists for the routes that only ever have a name to go on: a
    /// texture target read back off a project file. Anything holding a live <see cref="Materials.ResolvedMap"/>
    /// has the pathId and must pass it.</summary>
    public readonly record struct TextureRef(string? Name, long PathId = 0)
    {
        public static TextureRef ByPathId(long pathId) => new(null, pathId);
        public static TextureRef ByName(string name) => new(name);
        public static implicit operator TextureRef(string name) => ByName(name);

        /// <summary>How this reference reads in a message to the modder.</summary>
        public override string ToString() => Name ?? $"path {PathId}";
    }

    /// <summary>Read a Texture2D's hash inputs. Returns null if the texture is absent.</summary>
    public TextureHashSource? GetTextureHashSource(byte[] deobfuscatedBundle, TextureRef which)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        foreach (var info in Texture2Ds(am, inst, which))
        {
            var bf = am.GetBaseField(inst, info);
            var tex = TextureFile.ReadTextureFile(bf);
            var colorSpace = bf["m_ColorSpace"];
            return new TextureHashSource(
                tex.FillPictureData(inst),   // resolves streamed .resS into inline bytes
                tex.m_Width, tex.m_Height, tex.m_MipCount, tex.m_TextureFormat,
                !colorSpace.IsDummy && colorSpace.AsInt == 1);
        }
        return null;
    }

    /// <summary>The Texture2D's own Unity color-space flag without reading or decoding its picture data.
    /// Null means the exact resource is absent. Generic Blender transport inherits this answer instead of
    /// assigning semantics from a property-name convention.</summary>
    public bool? GetTextureSrgb(byte[] deobfuscatedBundle, TextureRef which)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        foreach (var info in Texture2Ds(am, inst, which))
        {
            var field = am.GetBaseField(inst, info)["m_ColorSpace"];
            return !field.IsDummy && field.AsInt == 1;
        }
        return null;
    }

    /// <summary>Decode a Texture2D to BGRA32. Handles BCn formats and resolves streamed pixel data
    /// (the <c>.resS</c> resource inside the bundle).</summary>
    public DecodedTexture? GetTexture(byte[] deobfuscatedBundle, TextureRef which)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        foreach (var info in Texture2Ds(am, inst, which))
        {
            var bf = am.GetBaseField(inst, info);
            var tex = TextureFile.ReadTextureFile(bf);
            byte[] encoded = tex.FillPictureData(inst);          // resolves streamed .resS into pictureData
            byte[] bgra = tex.DecodeTextureRaw(encoded, useBgra: true);  // BCn → BGRA32 (base mip)
            return new DecodedTexture(bgra, tex.m_Width, tex.m_Height, ((TextureFormat)tex.m_TextureFormat).ToString());
        }
        return null;
    }

    /// <summary>The Texture2D(s) a <see cref="TextureRef"/> selects, in file order. A pathId selects at most
    /// one and never falls back to the name: a reference the bundle no longer holds is the caller's null,
    /// not a different texture that happens to share its name.</summary>
    private static IEnumerable<AssetFileInfo> Texture2Ds(AssetsManager am, AssetsFileInstance inst, TextureRef which)
    {
        foreach (var info in inst.file.AssetInfos)
        {
            if (info.TypeId != ClassTexture2D) continue;
            if (which.PathId != 0)
            {
                if (info.PathId != which.PathId) continue;
            }
            else if (am.GetBaseField(inst, info)["m_Name"].AsString != which.Name) continue;
            yield return info;
        }
    }

    /// <summary>One texture-env slot of a Material: the shader slot (<c>_BaseMap</c>/<c>_BumpMap</c>/…)
    /// and its Texture2D PPtr. <see cref="FileId"/> 0 ⇒ the texture is in the SAME bundle as the
    /// material; &gt;0 ⇒ it lives in external dependency <c>ExternalCabs[FileId-1]</c>.</summary>
    public readonly record struct TexSlot(string Slot, int FileId, long PathId);

    /// <summary>Read a Material's <c>m_SavedProperties.m_TexEnvs</c> by name: each populated slot's PPtr plus
    /// the bundle's external dependency CAB names (indexed 1-based by <c>FileId</c>).</summary>
    public (IReadOnlyList<string> ExternalCabs, IReadOnlyList<TexSlot> Slots)? GetMaterialTexEnvs(byte[] deobfuscatedBundle, string materialName)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        var externals = inst.file.Metadata.Externals.Select(e => e.PathName.Split('/')[^1]).ToList();
        foreach (var info in inst.file.AssetInfos)
        {
            if (info.TypeId != ClassMaterial) continue;
            var bf = am.GetBaseField(inst, info);
            if (bf["m_Name"].AsString != materialName) continue;
            var slots = new List<TexSlot>();
            try
            {
                var arr = bf["m_SavedProperties"]["m_TexEnvs"].Children[0];
                foreach (var pair in arr.Children)
                {
                    var slot = pair["first"].AsString;
                    var tx = pair["second"]["m_Texture"];
                    int fid = tx["m_FileID"].AsInt; long pid = tx["m_PathID"].AsLong;
                    if (fid == 0 && pid == 0) continue;           // empty slot
                    slots.Add(new TexSlot(slot, fid, pid));
                }
            }
            catch { /* malformed property block — return whatever parsed */ }
            return (externals, slots);
        }
        return null;
    }

    /// <summary>Like <see cref="GetMaterialTexEnvs"/> but selecting the Material by PATH ID — the prefab
    /// renderer's ordered <c>m_Materials</c> binds by exact object, and a name lookup could hit the wrong
    /// same-named copy. Also returns the material's <c>m_Name</c> for labeling.</summary>
    public (string Name, IReadOnlyList<string> ExternalCabs, IReadOnlyList<TexSlot> Slots)? GetMaterialTexEnvsByPathId(
        byte[] deobfuscatedBundle, long pathId)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        var info = inst.file.GetAssetInfo(pathId);
        if (info is null || info.TypeId != ClassMaterial) return null;
        var bf = am.GetBaseField(inst, info);
        var name = bf["m_Name"].AsString;
        var externals = ExternalCabs(inst);
        var slots = new List<TexSlot>();
        try
        {
            var arr = bf["m_SavedProperties"]["m_TexEnvs"].Children[0];
            foreach (var pair in arr.Children)
            {
                var slot = pair["first"].AsString;
                var tx = pair["second"]["m_Texture"];
                int fid = tx["m_FileID"].AsInt; long pid = tx["m_PathID"].AsLong;
                if (fid == 0 && pid == 0) continue;           // empty slot
                slots.Add(new TexSlot(slot, fid, pid));
            }
        }
        catch { /* malformed property block — return whatever parsed */ }
        return (name, externals, slots);
    }

    /// <summary>The <c>m_Name</c> of the Texture2D at <paramref name="pathId"/> in this bundle, or null
    /// (used to name the target of a material's texture PPtr).</summary>
    public string? TextureNameByPathId(byte[] deobfuscatedBundle, long pathId)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        foreach (var info in inst.file.AssetInfos)
            if (info.TypeId == ClassTexture2D && info.PathId == pathId)
                try { return am.GetBaseField(inst, info)["m_Name"].AsString; } catch { return null; }
        return null;
    }

    public const int ClassShader = 48;

    /// <summary>One material's shading state as serialized: its name, the shader keywords it enables,
    /// the shader PPtr it draws through (<c>FileId</c> 0 = this bundle, else 1-based into
    /// <see cref="MaterialShading.ExternalCabs"/>), and its serialized float and colour rows. Absent
    /// rows mean the shader default applies — absence is recorded by the row not existing, never as
    /// zero.</summary>
    public sealed record MaterialShading(
        string Name,
        IReadOnlySet<string> EnabledKeywords,
        int ShaderFileId,
        long ShaderPathId,
        IReadOnlyList<string> ExternalCabs,
        IReadOnlyDictionary<string, float> Floats,
        IReadOnlyDictionary<string, float[]> Colors,
        IReadOnlyDictionary<string, float[]>? TextureTransforms = null,
        bool NumericPropertiesComplete = true);

    public sealed record ShaderNumericDefaults(IReadOnlyDictionary<string, float> Floats,
        IReadOnlyDictionary<string, float[]> Colors);

    /// <summary>Read the Material at <paramref name="pathId"/>'s shading state — keywords, shader PPtr,
    /// serialized floats and colours. Null when the path id is not a Material here.</summary>
    public MaterialShading? GetMaterialShading(byte[] deobfuscatedBundle, long pathId)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        var info = inst.file.GetAssetInfo(pathId);
        if (info is null || info.TypeId != ClassMaterial) return null;
        var bf = am.GetBaseField(inst, info);
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        var valid = bf["m_ValidKeywords"];
        if (!valid.IsDummy)
            foreach (var keyword in valid["Array"].Children) keywords.Add(keyword.AsString);
        else
        {
            // the pre-2021 serialization: one space-joined string
            var legacy = bf["m_ShaderKeywords"];
            if (!legacy.IsDummy)
                foreach (var keyword in legacy.AsString.Split(' ',
                             StringSplitOptions.RemoveEmptyEntries))
                    keywords.Add(keyword);
        }
        var floats = new Dictionary<string, float>(StringComparer.Ordinal);
        var colors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        bool complete = true;
        try
        {
            foreach (var pair in bf["m_SavedProperties"]["m_Floats"].Children[0].Children)
                floats[pair["first"].AsString] = pair["second"].AsFloat;
            foreach (var pair in bf["m_SavedProperties"]["m_Colors"].Children[0].Children)
            {
                var color = pair["second"];
                colors[pair["first"].AsString] = new[]
                {
                    color["r"].AsFloat, color["g"].AsFloat, color["b"].AsFloat, color["a"].AsFloat,
                };
            }
        }
        catch { complete = false; }
        var transforms = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var environments = bf["m_SavedProperties"]["m_TexEnvs"];
        try
        {
            if (!environments.IsDummy)
            foreach (var pair in environments["Array"].Children)
            {
                var scale = pair["second"]["m_Scale"];
                var offset = pair["second"]["m_Offset"];
                if (scale.IsDummy || offset.IsDummy) continue;
                if (scale["x"].IsDummy || scale["y"].IsDummy
                    || offset["x"].IsDummy || offset["y"].IsDummy)
                    throw new InvalidDataException("The material texture transform is incomplete.");
                transforms[pair["first"].AsString + "_ST"] = new[]
                {
                    scale["x"].AsFloat, scale["y"].AsFloat,
                    offset["x"].AsFloat, offset["y"].AsFloat,
                };
            }
        }
        catch { complete = false; }
        return new MaterialShading(bf["m_Name"].AsString, keywords,
            bf["m_Shader"]["m_FileID"].AsInt, bf["m_Shader"]["m_PathID"].AsLong,
            ExternalCabs(inst), floats, colors, transforms, complete);
    }

    /// <summary>Numeric defaults declared by the exact Shader object's property table. Texture
    /// properties are excluded; texture transforms come from the material's texture environments.</summary>
    public ShaderNumericDefaults? GetShaderNumericDefaults(byte[] deobfuscatedBundle, long pathId)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        var info = inst.file.GetAssetInfo(pathId);
        if (info is null || info.TypeId != ClassShader) return null;
        var properties = am.GetBaseField(inst, info)["m_ParsedForm"]["m_PropInfo"]["m_Props"];
        if (properties.IsDummy) return null;
        var floats = new Dictionary<string, float>(StringComparer.Ordinal);
        var colors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in properties["Array"].Children)
        {
            int kind = property["m_Type"].AsInt;
            if (kind is not (0 or 1 or 2 or 3)) continue;
            string name = property["m_Name"].AsString;
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                throw new InvalidDataException("The shader property table has an empty or duplicate numeric name.");
            if (kind is 0 or 1)
                colors.Add(name, Enumerable.Range(0, 4)
                    .Select(component => property[$"m_DefValue[{component}]"].AsFloat).ToArray());
            else
                floats.Add(name, property["m_DefValue[0]"].AsFloat);
        }
        return new ShaderNumericDefaults(floats, colors);
    }

    /// <summary>The fragment shader variants of the Shader asset at <paramref name="pathId"/>, read
    /// through <see cref="ShaderReflection"/>. Null when the path id is not a Shader here.</summary>
    public IReadOnlyList<ShaderVariant>? GetShaderVariants(byte[] deobfuscatedBundle, long pathId)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        var info = inst.file.GetAssetInfo(pathId);
        if (info is null || info.TypeId != ClassShader) return null;
        return ShaderReflection.FragmentVariants(am.GetBaseField(inst, info));
    }

    /// <summary>This bundle's own CAB name — what another bundle's externals reference it by.</summary>
    public string GetBundleCab(byte[] deobfuscatedBundle)
    {
        var (am, bun, inst) = Parse(deobfuscatedBundle);
        return inst.name;
    }
}
