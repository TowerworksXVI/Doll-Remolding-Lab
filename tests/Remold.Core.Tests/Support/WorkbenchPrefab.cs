using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Remold.Core.Bundles;

namespace Remold.Core.Tests.Support;

/// <summary>
/// A MULTI-slot assembly-prefab fixture. <see cref="SyntheticBundle"/> builds a single renderer slot; the
/// workbench needs several (LOD tiers collapsing to one token, several parts, ordered <c>m_Materials</c>
/// with an empty placeholder), so this is its own builder on the same technique — hand-authored type trees,
/// no game data.
/// </summary>
internal static class WorkbenchPrefab
{
    private const uint SerializedVersion = 17;
    private const string UnityVersion = "2019.4.29f1";

    public const int ClassGameObject = 1;
    public const int ClassTransform = 4;
    public const int ClassMaterial = 21;
    public const int ClassMeshRenderer = 23;
    public const int ClassMeshFilter = 33;
    public const int ClassMonoBehaviour = 114;
    public const int ClassSkinnedMeshRenderer = 137;
    public const int ClassAssetBundle = 142;

    /// <summary>One renderer slot: its GameObject name and ordered <c>m_Materials</c> PPtrs (fileId 0 =
    /// local, &gt;0 = external CAB by index; (0,0) = an empty placeholder the workbench preserves in order).
    /// <see cref="Mesh"/> is the serialized mesh reference (the smr-body shape); null writes the character
    /// shape's empty pointer, and such a slot needs a recipe row to be a part.
    /// <see cref="Renderer"/> picks the shape it is written in: a SkinnedMeshRenderer carrying its own
    /// mesh, or a MeshRenderer whose mesh sits on a MeshFilter beside it (the static prop shape).
    /// <see cref="CastShadows"/> writes <c>m_CastShadows</c> (0 = Off). Null on EVERY slot ships the field
    /// on no renderer at all, which is the shape a reader meets when it cannot measure the flag; null on
    /// one slot while another sets it writes 1 (On) here, since one type tree serves the whole
    /// class.</summary>
    public readonly record struct SlotSpec(string Name, (int FileId, long PathId)[] Materials,
        (int FileId, long PathId)? Mesh = null, SlotRenderer Renderer = SlotRenderer.Skinned,
        int? CastShadows = null);

    /// <summary>The dorm components' node lists, each naming SLOTS by name (the builder resolves each to
    /// that slot's Transform, which is what the shipped components reference). A null list is written as an
    /// empty one; leaving BOTH groups null ships neither component at all, which is the shape most prefabs
    /// have. <see cref="LobbyHideEnable"/> writes the component's serialized flag, so a fixture can prove
    /// the reader ignores it.
    ///
    /// <para>The two groups map to the two components: <see cref="DormNodes"/>/<see cref="FightNodes"/>/
    /// <see cref="ControlVisibleNodes"/> ship the dorm context component,
    /// <see cref="DormHideNodes"/>/<see cref="LobbyHideNodes"/>/<see cref="LobbyShowNodes"/> the hide
    /// one.</para></summary>
    public readonly record struct VisibilityLists(
        string[]? DormNodes = null,
        string[]? FightNodes = null,
        string[]? ControlVisibleNodes = null,
        string[]? DormHideNodes = null,
        string[]? LobbyHideNodes = null,
        string[]? LobbyShowNodes = null,
        int LobbyHideEnable = 1)
    {
        public bool HasContextComponent =>
            DormNodes is not null || FightNodes is not null || ControlVisibleNodes is not null;

        public bool HasHideComponent =>
            DormHideNodes is not null || LobbyHideNodes is not null || LobbyShowNodes is not null;
    }

    /// <summary>
    /// A root GameObject carrying a RoleMeshRes recipe (may be empty, for an enemy/NPC prefab body), one
    /// GameObject + renderer per slot, and the AssetBundle object with an <c>m_Container</c> mapping the
    /// root. <paramref name="externalCabs"/> is the external dependency list, 1-based by fileId.
    /// </summary>
    /// <param name="recipe">The RoleMeshRes rows. NULL ships no RoleMeshRes component at all — the prop
    /// shape, where the mesh identity is serialized on the renderers and there is no recipe to read.</param>
    /// <param name="bones">Optional rig: one (name, parentIndex) per bone (parentIndex into this array,
    /// -1 = parented to the container root, where a shipped prefab hangs its rig). Each becomes a
    /// GameObject + Transform, so <c>BundleReader.ListTransforms</c> reads the hierarchy (the workbench
    /// Skeleton node). Null/empty ⇒ the prefab ships no rig (skeleton unavailable).</param>
    public static void Build(string path, string bundleName, string rootName,
        SlotSpec[] slots, (string SlotPath, string MeshAddress)[]? recipe, string[] externalCabs,
        (string Name, int Parent)[]? bones = null, VisibilityLists? visibility = null)
    {
        // one type tree per class, so the field is present for every renderer as soon as any slot asks
        bool withCast = Array.Exists(slots, s => s.CastShadows is not null);
        var vis = visibility ?? default;
        var file = NewFile(externalCabs,
            scriptTypeCount: vis.HasContextComponent || vis.HasHideComponent ? HideScriptIndex + 1 : 0);
        file.Metadata.TypeTreeTypes.Add(GameObjectType());
        file.Metadata.TypeTreeTypes.Add(RecipeMbType());
        file.Metadata.TypeTreeTypes.Add(SmrType(withCast));
        file.Metadata.TypeTreeTypes.Add(MeshRendererType(withCast));
        file.Metadata.TypeTreeTypes.Add(MeshFilterType());
        // Always emit Transforms: real prefabs carry one on the root and every slot, so ListTransforms sees
        // them — and the workbench must EXCLUDE those from the bone count.
        file.Metadata.TypeTreeTypes.Add(TransformType());
        // The dorm components are their own MonoBehaviour SHAPES, so each needs its own type tree; the
        // index is carried explicitly, since class id alone would pick the recipe's tree for all three.
        int contextTypeIndex = -1, hideTypeIndex = -1;
        if (vis.HasContextComponent)
        { file.Metadata.TypeTreeTypes.Add(DormContextMbType()); contextTypeIndex = file.Metadata.TypeTreeTypes.Count - 1; }
        if (vis.HasHideComponent)
        { file.Metadata.TypeTreeTypes.Add(DormHideMbType()); hideTypeIndex = file.Metadata.TypeTreeTypes.Count - 1; }

        long pid = 1;
        long rootPid = pid++;
        long recipePid = pid++;
        long rootTrPid = pid++;
        long contextMbPid = vis.HasContextComponent ? pid++ : 0;
        long hideMbPid = vis.HasHideComponent ? pid++ : 0;

        // root GameObject: the recipe MB (absent on the prop shape) plus whichever dorm components the
        // fixture asked for, exactly as a shipped prefab hangs them off its container root
        AddObject(file, rootPid, ClassGameObject, bf =>
        {
            bf["m_Name"].AsString = rootName;
            var arr = bf["m_Component"]["Array"];
            var els = new List<AssetTypeValueField>();
            void Component(long target)
            {
                var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                el["m_FileID"].AsInt = 0; el["m_PathID"].AsLong = target;
                els.Add(el);
            }
            if (recipe is not null) Component(recipePid);
            if (contextMbPid != 0) Component(contextMbPid);
            if (hideMbPid != 0) Component(hideMbPid);
            arr.Children = els;
        });
        // recipe MonoBehaviour on the root
        if (recipe is not null) AddObject(file, recipePid, ClassMonoBehaviour, bf =>
        {
            bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = rootPid;
            bf["m_Name"].AsString = "RoleMeshRes";
            var arr = bf["MeshResList"]["Array"];
            var els = new List<AssetTypeValueField>();
            foreach (var (slotPath, meshAddress) in recipe)
            {
                var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                el["TransfromPath"].AsString = slotPath;
                el["MeshResPath"].AsString = meshAddress;
                els.Add(el);
            }
            arr.Children = els;
        });
        // the root's own Transform — a non-bone the workbench must exclude
        AddObject(file, rootTrPid, ClassTransform, bf =>
        {
            bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = rootPid;
            bf["m_Father"]["m_FileID"].AsInt = 0; bf["m_Father"]["m_PathID"].AsLong = 0;
        });
        // each slot: a GameObject + its SkinnedMeshRenderer + a Transform (parented to the root)
        var slotTransformByName = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var slot in slots)
        {
            long slotGoPid = pid++;
            long smrPid = pid++;
            long slotTrPid = pid++;
            slotTransformByName[slot.Name] = slotTrPid;
            AddObject(file, slotGoPid, ClassGameObject, bf => bf["m_Name"].AsString = slot.Name);
            void Materials(AssetTypeValueField bf)
            {
                bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = slotGoPid;
                if (withCast) bf["m_CastShadows"].AsInt = slot.CastShadows ?? 1;
                var arr = bf["m_Materials"]["Array"];
                var els = new List<AssetTypeValueField>();
                foreach (var (fid, mpid) in slot.Materials)
                {
                    var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                    el["m_FileID"].AsInt = fid; el["m_PathID"].AsLong = mpid;
                    els.Add(el);
                }
                arr.Children = els;
            }
            if (slot.Renderer == SlotRenderer.Static)
            {
                AddObject(file, smrPid, ClassMeshRenderer, Materials);
                // the static shape's mesh: a MeshFilter on the SAME GameObject, never on the renderer
                AddObject(file, pid++, ClassMeshFilter, bf =>
                {
                    bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = slotGoPid;
                    bf["m_Mesh"]["m_FileID"].AsInt = slot.Mesh?.FileId ?? 0;
                    bf["m_Mesh"]["m_PathID"].AsLong = slot.Mesh?.PathId ?? 0;
                });
            }
            else AddObject(file, smrPid, ClassSkinnedMeshRenderer, bf =>
            {
                Materials(bf);
                // character shape: an empty pointer the recipe fills at runtime; smr-body: a serialized ref
                bf["m_Mesh"]["m_FileID"].AsInt = slot.Mesh?.FileId ?? 0;
                bf["m_Mesh"]["m_PathID"].AsLong = slot.Mesh?.PathId ?? 0;
            });
            // an SMR carrier's Transform — also excluded from the bone count
            AddObject(file, slotTrPid, ClassTransform, bf =>
            {
                bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = slotGoPid;
                bf["m_Father"]["m_FileID"].AsInt = 0; bf["m_Father"]["m_PathID"].AsLong = rootTrPid;
            });
        }
        // optional rig: a GameObject + Transform per bone, parented per the bones[] parent indices, with
        // the rig's own roots hanging off the container root as a shipped prefab's do
        if (bones is { Length: > 0 })
        {
            var boneGoPid = new long[bones.Length];
            var boneTrPid = new long[bones.Length];
            for (int i = 0; i < bones.Length; i++) { boneGoPid[i] = pid++; boneTrPid[i] = pid++; }
            for (int i = 0; i < bones.Length; i++)
            {
                int bi = i;
                AddObject(file, boneGoPid[bi], ClassGameObject, bf => bf["m_Name"].AsString = bones[bi].Name);
                AddObject(file, boneTrPid[bi], ClassTransform, bf =>
                {
                    bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = boneGoPid[bi];
                    long father = bones[bi].Parent >= 0 ? boneTrPid[bones[bi].Parent] : rootTrPid;
                    bf["m_Father"]["m_FileID"].AsInt = 0; bf["m_Father"]["m_PathID"].AsLong = father;
                });
            }
        }
        // the dorm components, written after the slot pass so every node list can point at a real Transform
        void NodeList(AssetTypeValueField mb, string field, string[]? names)
        {
            var arr = mb[field]["Array"];
            var els = new List<AssetTypeValueField>();
            foreach (var name in names ?? Array.Empty<string>())
            {
                var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
                el["m_FileID"].AsInt = 0;
                el["m_PathID"].AsLong = slotTransformByName.TryGetValue(name, out var trPid) ? trPid : 0;
                els.Add(el);
            }
            arr.Children = els;
        }
        if (contextMbPid != 0)
            AddObjectOfType(file, contextMbPid, ClassMonoBehaviour, contextTypeIndex, bf =>
            {
                bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = rootPid;
                bf["m_Name"].AsString = "";
                NodeList(bf, "DormNodes", vis.DormNodes);
                NodeList(bf, "FightNodes", vis.FightNodes);
                NodeList(bf, "DormNodes_M0", null);
                NodeList(bf, "FightNodes_M0", null);
                NodeList(bf, "ControlVisibleNodes", vis.ControlVisibleNodes);
            });
        if (hideMbPid != 0)
            AddObjectOfType(file, hideMbPid, ClassMonoBehaviour, hideTypeIndex, bf =>
            {
                bf["m_GameObject"]["m_FileID"].AsInt = 0; bf["m_GameObject"]["m_PathID"].AsLong = rootPid;
                bf["m_Name"].AsString = "";
                NodeList(bf, "DormHideNodes", vis.DormHideNodes);
                bf["LobbyHideEnable"].AsInt = vis.LobbyHideEnable;
                NodeList(bf, "LobbyHideNodes", vis.LobbyHideNodes);
                NodeList(bf, "LobbyShowNodes", vis.LobbyShowNodes);
            });

        AddAssetBundleObject(file, pid, bundleName, rootPid, rootName);

        using var ms = new MemoryStream();
        using (var w = new AssetsFileWriter(ms)) file.Write(w);
        WriteBundle(path, ms.ToArray());
    }

    // ---- plumbing (self-contained; mirrors SyntheticBundle's technique) ----

    private static AssetsFile NewFile(string[] externalCabs, int scriptTypeCount = 0)
    {
        var file = new AssetsFile
        {
            Header = new AssetsFileHeader { Version = SerializedVersion, Endianness = false, MetadataSize = 0, FileSize = 0, DataOffset = 0 },
            Metadata = new AssetsFileMetadata
            {
                UnityVersion = UnityVersion, TargetPlatform = 5, TypeTreeEnabled = true,
                TypeTreeTypes = new List<TypeTreeType>(), AssetInfos = new List<AssetFileInfo>(),
                ScriptTypes = new List<AssetPPtr>(), Externals = new List<AssetsFileExternal>(),
                RefTypes = new List<TypeTreeType>(), UserInformation = "",
            },
        };
        // one entry per script index any MonoBehaviour type tree claims, so the indices resolve
        for (int i = 0; i < scriptTypeCount; i++)
            file.Metadata.ScriptTypes.Add(new AssetPPtr(0, 0));
        foreach (var cab in externalCabs)
            file.Metadata.Externals.Add(new AssetsFileExternal
            {
                PathName = $"archive:/{cab}/{cab}", VirtualAssetPathName = "", Guid = default,
                Type = AssetsFileExternalType.Normal, OriginalPathName = "",
            });
        return file;
    }

    private static void AddObject(AssetsFile file, long pathId, int classId, Action<AssetTypeValueField> fill)
    {
        var info = AssetFileInfo.Create(file, pathId, classId, classDatabase: null, preferEditor: false)
            ?? throw new InvalidOperationException($"AssetFileInfo.Create returned null (class {classId})");
        var tpl = new AssetTypeTemplateField();
        tpl.FromTypeTree(file.Metadata.TypeTreeTypes[info.TypeIdOrIndex]);
        var bf = ValueBuilder.DefaultValueFieldFromTemplate(tpl);
        fill(bf);
        info.SetNewData(bf);
        file.Metadata.AssetInfos.Add(info);
    }

    /// <summary>Add an object whose type tree is picked by INDEX rather than by class id — the three
    /// MonoBehaviour shapes share a class id, so the index is the only thing that tells them apart.</summary>
    private static void AddObjectOfType(AssetsFile file, long pathId, int classId, int typeIndex,
        Action<AssetTypeValueField> fill)
    {
        var info = AssetFileInfo.Create(file, pathId, classId, classDatabase: null, preferEditor: false)
            ?? throw new InvalidOperationException($"AssetFileInfo.Create returned null (class {classId})");
        info.TypeIdOrIndex = typeIndex;
        var tpl = new AssetTypeTemplateField();
        tpl.FromTypeTree(file.Metadata.TypeTreeTypes[typeIndex]);
        var bf = ValueBuilder.DefaultValueFieldFromTemplate(tpl);
        fill(bf);
        info.SetNewData(bf);
        file.Metadata.AssetInfos.Add(info);
    }

    /// <summary>The dorm context component: the per-scene node lists plus the coat list separate scene
    /// logic drives. Field NAMES are what identifies it, so they are written verbatim.</summary>
    private static TypeTreeType DormContextMbType() =>
        MbWithNodeLists(ContextScriptIndex,
            "DormNodes", "FightNodes", "DormNodes_M0", "FightNodes_M0", "ControlVisibleNodes");

    /// <summary>Script indices for the two dorm component shapes. They only have to differ from each other
    /// and from the recipe MB's (which carries none), so the reader keeps three templates apart.</summary>
    private const ushort ContextScriptIndex = 0;
    private const ushort HideScriptIndex = 1;

    /// <summary>The dorm/lobby hide component. The serialized <c>LobbyHideEnable</c> flag sits between the
    /// lists exactly as it is shipped, so a fixture setting it proves the reader passes it by.</summary>
    private static TypeTreeType DormHideMbType()
    {
        var b = MbHead(HideScriptIndex);
        NodeListField(b, "DormHideNodes");
        b.Value("UInt8", "LobbyHideEnable", 1, 1, align: true);
        NodeListField(b, "LobbyHideNodes");
        NodeListField(b, "LobbyShowNodes");
        return b.Build();
    }

    private static TypeTreeType MbWithNodeLists(ushort scriptIndex, params string[] fields)
    {
        var b = MbHead(scriptIndex);
        foreach (var f in fields) NodeListField(b, f);
        return b.Build();
    }

    /// <summary>The MonoBehaviour header every shipped component carries ahead of its own fields.</summary>
    private static Tb MbHead(ushort scriptIndex)
    {
        var b = new Tb(ClassMonoBehaviour, "MonoBehaviour", scriptIndex);
        b.Struct("PPtr<GameObject>", "m_GameObject", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.Value("UInt8", "m_Enabled", 1, 1, align: true);
        b.Struct("PPtr<MonoScript>", "m_Script", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.Str("m_Name", 1);
        return b;
    }

    private static void NodeListField(Tb b, string name) =>
        b.VectorOfStruct(name, "PPtr<Transform>", 1, e => e
            .Value("int", "m_FileID", 4, 4).Value("SInt64", "m_PathID", 4, 8));

    private static void AddAssetBundleObject(AssetsFile file, long pathId, string bundleName, long rootPid, string key)
    {
        file.Metadata.TypeTreeTypes.Add(AssetBundleType());
        var info = AssetFileInfo.Create(file, pathId, ClassAssetBundle, classDatabase: null, preferEditor: false)
            ?? throw new InvalidOperationException("AssetFileInfo.Create returned null (AssetBundle)");
        var tpl = new AssetTypeTemplateField();
        tpl.FromTypeTree(file.Metadata.TypeTreeTypes[info.TypeIdOrIndex]);
        var bf = ValueBuilder.DefaultValueFieldFromTemplate(tpl);
        bf["m_Name"].AsString = bundleName;
        var arr = bf["m_Container"]["Array"];
        var el = ValueBuilder.DefaultValueFieldFromArrayTemplate(arr);
        el["first"].AsString = key;
        el["second"]["asset"]["m_FileID"].AsInt = 0;
        el["second"]["asset"]["m_PathID"].AsLong = rootPid;
        arr.Children = new List<AssetTypeValueField> { el };
        info.SetNewData(bf);
        file.Metadata.AssetInfos.Add(info);
    }

    private static TypeTreeType GameObjectType()
    {
        var b = new Tb(ClassGameObject, "GameObject");
        b.VectorOfStruct("m_Component", "PPtr<Component>", 1, e => e
            .Value("int", "m_FileID", 4, 4).Value("SInt64", "m_PathID", 4, 8));
        b.Str("m_Name", 1);
        return b.Build();
    }

    private static TypeTreeType RecipeMbType()
    {
        var b = new Tb(ClassMonoBehaviour, "MonoBehaviour");
        b.Struct("PPtr<GameObject>", "m_GameObject", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.Value("UInt8", "m_Enabled", 1, 1, align: true);
        b.Struct("PPtr<MonoScript>", "m_Script", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.Str("m_Name", 1);
        b.VectorOfStruct("MeshResList", "MeshRes", 1, e => e
            .Str("TransfromPath", 4).Str("MeshResPath", 4));
        return b.Build();
    }

    private static TypeTreeType SmrType(bool withCastShadows = false)
    {
        var b = new Tb(ClassSkinnedMeshRenderer, "SkinnedMeshRenderer");
        b.Struct("PPtr<GameObject>", "m_GameObject", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        if (withCastShadows) b.Value("UInt8", "m_CastShadows", 1, 1, align: true);
        b.VectorOfStruct("m_Materials", "PPtr<Material>", 1, e => e
            .Value("int", "m_FileID", 4, 4).Value("SInt64", "m_PathID", 4, 8));
        b.Struct("PPtr<Mesh>", "m_Mesh", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        return b.Build();
    }

    /// <summary>The static prop shape's renderer: materials, and no mesh of its own.</summary>
    private static TypeTreeType MeshRendererType(bool withCastShadows = false)
    {
        var b = new Tb(ClassMeshRenderer, "MeshRenderer");
        b.Struct("PPtr<GameObject>", "m_GameObject", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        if (withCastShadows) b.Value("UInt8", "m_CastShadows", 1, 1, align: true);
        b.VectorOfStruct("m_Materials", "PPtr<Material>", 1, e => e
            .Value("int", "m_FileID", 4, 4).Value("SInt64", "m_PathID", 4, 8));
        return b.Build();
    }

    /// <summary>Where a static prop's mesh reference lives, beside its renderer on one GameObject.</summary>
    private static TypeTreeType MeshFilterType()
    {
        var b = new Tb(ClassMeshFilter, "MeshFilter");
        b.Struct("PPtr<GameObject>", "m_GameObject", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.Struct("PPtr<Mesh>", "m_Mesh", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        return b.Build();
    }

    private static TypeTreeType TransformType()
    {
        var b = new Tb(ClassTransform, "Transform");
        b.Struct("PPtr<GameObject>", "m_GameObject", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        b.Struct("PPtr<Transform>", "m_Father", 1);
        b.Value("int", "m_FileID", 2, 4);
        b.Value("SInt64", "m_PathID", 2, 8);
        return b.Build();
    }

    private static TypeTreeType AssetBundleType()
    {
        var b = new Tb(ClassAssetBundle, "AssetBundle");
        b.Str("m_Name", 1);
        b.VectorOfStruct("m_Container", "pair", 1, e =>
        {
            e.Str("first", 4);
            e.Struct("AssetInfo", "second", 4);
            e.Value("int", "preloadIndex", 5, 4);
            e.Value("int", "preloadSize", 5, 4);
            e.Struct("PPtr<Object>", "asset", 5);
            e.Value("int", "m_FileID", 6, 4);
            e.Value("SInt64", "m_PathID", 6, 8);
        });
        return b.Build();
    }

    private static void WriteBundle(string outPath, byte[] assetsBytes)
    {
        var bun = new AssetBundleFile
        {
            Header = new AssetBundleHeader
            {
                Signature = "UnityFS", Version = 7, GenerationVersion = "5.x.x", EngineVersion = UnityVersion,
                FileStreamHeader = new AssetBundleFSHeader
                {
                    TotalFileSize = 0, CompressedSize = 0, DecompressedSize = 0,
                    Flags = AssetBundleFSHeaderFlags.HasDirectoryInfo,
                },
            },
            BlockAndDirInfo = new AssetBundleBlockAndDirInfo
            {
                Hash = default, BlockInfos = Array.Empty<AssetBundleBlockInfo>(),
                DirectoryInfos = new List<AssetBundleDirectoryInfo>(),
            },
        };
        var dir = new AssetBundleDirectoryInfo { Offset = 0, DecompressedSize = 0, Flags = 4, Name = "CAB-gf2wbprefab" };
        dir.SetNewData(assetsBytes);
        bun.BlockAndDirInfo.DirectoryInfos.Add(dir);
        using var w = new AssetsFileWriter(outPath);
        bun.Write(w);
    }

    /// <summary>A trimmed hand-authored type-tree builder (the subset the prefab shapes need).</summary>
    private sealed class Tb
    {
        private readonly List<TypeTreeNode> _nodes = new();
        private readonly Buf _strings = new();
        private readonly int _typeId;
        private readonly ushort _scriptIndex;

        /// <param name="scriptIndex">Distinguishes two type trees sharing a class id. MonoBehaviour needs it:
        /// the reader resolves a template by (class id, script index), so same-indexed shapes would all read
        /// through whichever was registered first. <c>ushort.MaxValue</c> = no script, the plain case.</param>
        public Tb(int typeId, string baseTypeName, ushort scriptIndex = ushort.MaxValue)
        {
            _typeId = typeId;
            _scriptIndex = scriptIndex;
            Add(baseTypeName, "Base", 0, -1, TypeTreeNodeFlags.None, 0);
        }

        public Tb Value(string type, string name, byte level, int byteSize, bool align = false)
        { Add(type, name, level, byteSize, TypeTreeNodeFlags.None, align ? 0x4000u : 0u); return this; }

        public Tb Struct(string type, string name, byte level)
        { Add(type, name, level, -1, TypeTreeNodeFlags.None, 0); return this; }

        public Tb Str(string name, byte level)
        {
            Add("string", name, level, -1, TypeTreeNodeFlags.None, 0x4000u);
            Add("Array", "Array", (byte)(level + 1), -1, TypeTreeNodeFlags.Array, 0x4000u);
            Add("int", "size", (byte)(level + 2), 4, TypeTreeNodeFlags.None, 0);
            Add("char", "data", (byte)(level + 2), 1, TypeTreeNodeFlags.None, 0);
            return this;
        }

        public Tb VectorOfStruct(string name, string elemType, byte level, Action<Tb> element)
        {
            Add("vector", name, level, -1, TypeTreeNodeFlags.None, 0x4000u);
            Add("Array", "Array", (byte)(level + 1), -1, TypeTreeNodeFlags.Array, 0x4000u);
            Add("int", "size", (byte)(level + 2), 4, TypeTreeNodeFlags.None, 0);
            Add(elemType, "data", (byte)(level + 2), -1, TypeTreeNodeFlags.None, 0);
            element(this);
            return this;
        }

        private void Add(string type, string name, byte level, int byteSize, TypeTreeNodeFlags flags, uint meta)
            => _nodes.Add(new TypeTreeNode
            {
                Version = 1, Level = level, TypeFlags = flags,
                TypeStrOffset = _strings.Offset(type), NameStrOffset = _strings.Offset(name),
                ByteSize = byteSize, Index = 0, MetaFlags = meta, RefTypeHash = 0,
            });

        public TypeTreeType Build()
        {
            for (int i = 0; i < _nodes.Count; i++) _nodes[i].Index = (uint)i;
            return new TypeTreeType
            {
                TypeId = _typeId, IsStrippedType = false, ScriptTypeIndex = _scriptIndex,
                Nodes = _nodes, StringBufferBytes = _strings.ToBytes(),
                TypeDependencies = Array.Empty<int>(),
                TypeHash = new Hash128(new byte[16]), ScriptIdHash = new Hash128(new byte[16]),
            };
        }

        private sealed class Buf
        {
            private readonly StringBuilder _s = new();
            private readonly Dictionary<string, uint> _off = new();
            public uint Offset(string name)
            {
                if (_off.TryGetValue(name, out var have)) return have;
                uint off = (uint)_s.Length;
                _s.Append(name).Append('\0');
                _off[name] = off;
                return off;
            }
            public byte[] ToBytes() => Encoding.ASCII.GetBytes(_s.ToString());
        }
    }
}
