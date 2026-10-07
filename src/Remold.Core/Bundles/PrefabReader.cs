using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Remold.Core.Model;

namespace Remold.Core.Bundles;

/// <summary>One recipe entry of the prefab's <c>RoleMeshRes.MeshResList</c>: the renderer-slot
/// GameObject name and the mesh addressable loaded into it at runtime.</summary>
public sealed record PrefabRecipeEntry(string SlotPath, string MeshAddress);

/// <summary>One ordered material reference off a renderer's <c>m_Materials</c>: resolve by
/// <see cref="PathId"/> in the prefab's own bundle when <see cref="Cab"/> is null, else in the bundle
/// holding it. Submesh <i>i</i> renders with entry <i>i</i> — the authoritative binding.</summary>
public sealed record PrefabMaterialRef(long PathId, string? Cab);

/// <summary>A renderer slot's serialized mesh reference (the <c>smr-body</c> class), resolved like
/// <see cref="PrefabMaterialRef"/>. Character outfit prefabs ship none.</summary>
public sealed record PrefabMeshRef(long PathId, string? Cab);

/// <summary>The saved rest pose (an Avatar) a rig's Animator names, resolved like
/// <see cref="PrefabMaterialRef"/>.</summary>
public sealed record PrefabAvatarRef(long PathId, string? Cab);

/// <summary>Which renderer class draws a slot. The distinction is the slot's, not the mesh's: it says
/// where the mesh reference was read from, and nothing about what the mesh contains.</summary>
public enum SlotRenderer
{
    /// <summary>A <c>SkinnedMeshRenderer</c>: mesh and materials both on the renderer.</summary>
    Skinned,

    /// <summary>A plain <c>MeshRenderer</c>: materials on the renderer, mesh on the MeshFilter sharing its
    /// GameObject.</summary>
    Static,
}

/// <summary>One renderer slot: the slot GameObject's name (which the recipe's
/// <see cref="PrefabRecipeEntry.SlotPath"/> matches), its ordered material references, and the
/// serialized mesh reference when the renderer ships one.</summary>
/// <param name="CastsShadows">The renderer takes part in the shadow pass (<c>m_CastShadows</c> is not
/// Off). A slot that casts keeps issuing a depth-only draw while the camera can't see it; one that does
/// not is drawn only when it is in frame, and issues nothing at all otherwise. False requires a MEASURED
/// Off — a renderer whose field can't be read reads as casting.</param>
public sealed record PrefabSlot(string Name, long PathId, IReadOnlyList<PrefabMaterialRef> Materials,
    PrefabMeshRef? Mesh, SlotRenderer Renderer = SlotRenderer.Skinned, bool CastsShadows = true,
    bool InRoot = true)
{
    /// <summary>True when the renderer ships a serialized mesh (an smr-body or static slot).</summary>
    public bool HasMesh => Mesh is not null;

    // InRoot: whether the renderer's GameObject sits under the parsed root's own Transform. Always true
    // for a one-root bundle, where the root owns every renderer in the file; in a bundle shipping several
    // container roots it is false for a sibling root's renderer, which the ownership rule's root-name
    // clause must not claim. Prefix ownership is independent of the renderer's root.
}

/// <summary>A parsed assembly prefab: root GameObject, mesh recipe, renderer slots with their CAB-exact
/// material bindings, and the dependency CABs.</summary>
/// <param name="VisibilityOverrides">Node name → the game-side mechanism that can leave that node undrawn,
/// read off the root's own dorm components. Null when the root carried none, which is NOT the same as an
/// empty map: both demote nothing, and neither may be read as evidence a node is always drawn.</param>
public sealed record CharacterPrefab(
    string RootName,
    IReadOnlyList<PrefabRecipeEntry> Recipe,
    IReadOnlyList<PrefabSlot> Slots,
    IReadOnlyList<string> ExternalCabs,
    bool HasReplaceableModel,
    IReadOnlyDictionary<string, VisibilityOverride>? VisibilityOverrides = null)
{
    /// <summary>The override on a node by name, or <see cref="VisibilityOverride.None"/>.</summary>
    public VisibilityOverride VisibilityOf(string nodeName) =>
        VisibilityOverrides is { } m && m.TryGetValue(nodeName, out var v) ? v : VisibilityOverride.None;
}

/// <summary>Deep-parses one assembly-prefab bundle from its plain (deobfuscated) bytes, on demand, when
/// Pick/Export needs its recipe and CAB-exact material bindings. Read-only.</summary>
public static class PrefabReader
{
    /// <inheritdoc cref="Read(byte[], string?, out bool)"/>
    public static CharacterPrefab? Read(byte[] deobfuscatedBundle, string? rootName = null) =>
        Read(deobfuscatedBundle, rootName, out _);

    /// <summary>
    /// Parse the assembly prefab in <paramref name="deobfuscatedBundle"/>; with
    /// <paramref name="rootName"/>, that container root specifically, else the first root that parses.
    /// Three accepted shapes: a GameObject carrying a <c>RoleMeshRes</c> recipe (character/RX), a
    /// recipe-less root whose SMR slots carry serialized mesh references (the enemy <c>smr-body</c>
    /// class), and a recipe-less root whose STATIC renderer slots do (the prop class). Null when none is
    /// present — the caller's mis-target signal. A structurally unreadable bundle throws.
    /// </summary>
    /// <param name="declinedRoot">True when a container root WAS found and declined for carrying no
    /// readable slots. That separates a bundle shipping no assembly prefab at all from one whose prefab
    /// this parse could make nothing of, which the two get different messages for.</param>
    public static CharacterPrefab? Read(byte[] deobfuscatedBundle, string? rootName, out bool declinedRoot)
    {
        declinedRoot = false;
        var am = new AssetsManager();
        var bun = am.LoadBundleFile(new MemoryStream(deobfuscatedBundle), "live.bundle");
        var inst = am.LoadAssetsFileFromBundle(bun, 0);
        var externals = inst.file.Metadata.Externals.Select(e => e.PathName.Split('/')[^1]).ToList();

        var abInfo = inst.file.AssetInfos.FirstOrDefault(i => i.TypeId == BundleReader.ClassAssetBundle)
            ?? throw new InvalidDataException("bundle has no AssetBundle object. Not a shipped bundle");
        var container = am.GetBaseField(inst, abInfo)["m_Container"];

        // The container's GameObject roots, in container order. Most bundles ship ONE; a bundle shipping
        // several (a support team's three members, a weapon beside its ammunition, a prefab beside a
        // prop-less variant of itself) holds every root's renderers in one file. The slot list stays
        // file-wide — a sibling root can carry this subject's own-prefixed slots — but each slot records
        // whether it sits under the parsed root's Transform (PrefabSlot.InRoot), which is what keeps a
        // root-name claim from taking a sibling root's renderers. A single root owns the whole file, so
        // the subtree walk — a read per Transform — is paid only where the file has something to tell apart.
        var roots = new List<(AssetFileInfo Info, AssetTypeValueField Field, string Name)>();
        foreach (var entry in BundleReader.UnwrapArray(container))
        {
            var asset = BundleReader.FindPtr(
                entry["second"]["asset"].IsDummy ? entry["second"] : entry["second"]["asset"]);
            if (asset is null || asset["m_FileID"].AsInt != 0) continue;
            var goInfo = inst.file.GetAssetInfo(asset["m_PathID"].AsLong);
            if (goInfo is null || goInfo.TypeId != BundleReader.ClassGameObject) continue;
            if (roots.Any(r => r.Info.PathId == goInfo.PathId)) continue;
            var goField = am.GetBaseField(inst, goInfo);
            roots.Add((goInfo, goField, goField["m_Name"].AsString));
        }

        foreach (var (goInfo, goField, name) in roots)
        {
            if (rootName is not null && !string.Equals(name, rootName, StringComparison.Ordinal)) continue;

            HashSet<long>? subtree = null;
            if (roots.Count > 1)
            {
                subtree = SubtreeGameObjects(am, inst, goInfo.PathId, name, out var why);
                if (subtree is null)
                {
                    // A root whose renderers cannot be told from its siblings' is not one this reader
                    // can read as a prefab. Asked for by name, that is a loud refusal; met while trying a
                    // bundle's roots in turn, it is a root that is not an assembly prefab, and the next
                    // root gets its turn. Never a root that silently owns nothing.
                    if (rootName is not null) throw new InvalidDataException(why);
                    declinedRoot = true;
                    continue;
                }
            }
            var parsed = ReadRoot(am, inst, name, goField, externals, subtree);
            if (parsed is not null) return parsed;
            declinedRoot = true;
        }
        return null;
    }

    /// <summary>The path ids of every GameObject under <paramref name="rootGameObject"/>'s Transform,
    /// the root itself included: one Transform read per Transform in the file, father chains followed
    /// upward with the answers memoized. A Transform whose chain never reaches the root — another
    /// container root's, or an orphan — is outside.
    /// <para>Null, with <paramref name="why"/> set, when the subtree cannot be told: a Transform could not
    /// be read, or the root has no Transform at all — a UI root sits on a RectTransform, a different
    /// class, and a bundle in a subject's closure routinely ships several of those. The caller decides
    /// what that means: a root nobody asked for by name is simply not an assembly prefab, while a root
    /// pinned by a curated route is one this reader cannot read and says so loudly.</para></summary>
    private static HashSet<long>? SubtreeGameObjects(AssetsManager am, AssetsFileInstance inst, long rootGameObject,
        string rootName, out string? why)
    {
        why = null;
        var byTransform = new Dictionary<long, (long GameObject, long Father)>();
        foreach (var info in inst.file.AssetInfos)
        {
            if (info.TypeId != BundleReader.ClassTransform) continue;
            try
            {
                var bf = am.GetBaseField(inst, info);
                byTransform[info.PathId] = (bf["m_GameObject"]["m_PathID"].AsLong, bf["m_Father"]["m_PathID"].AsLong);
            }
            catch (Exception e)
            {
                why = $"Transform {info.PathId} in a bundle shipping several container roots couldn't be read, "
                    + $"so which renderers belong to '{rootName}' can't be told: {e.Message}";
                return null;
            }
        }
        long rootTransform = byTransform.FirstOrDefault(kv => kv.Value.GameObject == rootGameObject).Key;
        if (rootTransform == 0)
        {
            why = $"Container root '{rootName}' has no Transform in a bundle shipping several container roots, "
                + "so which renderers belong to it can't be told.";
            return null;
        }
        var inside = new Dictionary<long, bool>();
        bool Inside(long transform, int depth)
        {
            if (transform == rootTransform) return true;
            if (transform == 0 || depth > byTransform.Count) return false;   // an orphan chain, or a cycle
            if (inside.TryGetValue(transform, out var known)) return known;
            bool answer = byTransform.TryGetValue(transform, out var t) && Inside(t.Father, depth + 1);
            return inside[transform] = answer;
        }
        var result = new HashSet<long> { rootGameObject };
        foreach (var (transform, (go, _)) in byTransform)
            if (Inside(transform, 0)) result.Add(go);
        return result;
    }

    /// <summary>Parse one container root: its recipe MB, the ReplaceableModel marker, and every renderer
    /// slot in the file — skinned and static alike (one root's slots per bundle; SlotPath names tie them
    /// together regardless of hierarchy). A recipe-less root is accepted iff a slot carries a serialized
    /// mesh.</summary>
    /// <param name="subtree">The GameObjects under this root's Transform, or null when the root owns the
    /// whole file (the one-root bundle). A renderer whose GameObject is outside is another root's slot.</param>
    private static CharacterPrefab? ReadRoot(AssetsManager am, AssetsFileInstance inst, string rootName,
        AssetTypeValueField goField, IReadOnlyList<string> externals, HashSet<long>? subtree)
    {
        List<PrefabRecipeEntry>? recipe = null;
        bool replaceable = false;
        Dictionary<string, VisibilityOverride>? visibility = null;
        Dictionary<long, string>? nodeNameByTransform = null;

        // Node name per Transform path id, built only once a visibility list is actually found — the
        // overwhelming majority of prefabs carry none and pay nothing for this.
        Dictionary<long, string> NodeNames()
        {
            if (nodeNameByTransform is not null) return nodeNameByTransform;
            var goNames = new Dictionary<long, string>();
            foreach (var i in inst.file.AssetInfos)
                if (i.TypeId == BundleReader.ClassGameObject)
                    try { goNames[i.PathId] = am.GetBaseField(inst, i)["m_Name"].AsString; } catch { }
            var map = new Dictionary<long, string>();
            foreach (var i in inst.file.AssetInfos)
            {
                if (i.TypeId != BundleReader.ClassTransform) continue;
                try
                {
                    var owner = am.GetBaseField(inst, i)["m_GameObject"];
                    if (owner["m_FileID"].AsInt == 0
                        && goNames.TryGetValue(owner["m_PathID"].AsLong, out var n) && n.Length > 0)
                        map[i.PathId] = n;
                }
                catch { }
            }
            return nodeNameByTransform = map;
        }

        // Fold one Transform list into the map. The lists hold LOCAL references, so a pointer into another
        // file names no node of this prefab and is dropped rather than guessed at. The lowest-valued
        // mechanism wins a node two lists both name, which keeps one node answering with one reason
        // whatever order the prefab lists its components in.
        void MarkNodes(AssetTypeValueField list, VisibilityOverride why)
        {
            // An EMPTY list names no node, so the map it would be resolved against is never built. The
            // components ship on far more prefabs than actually carry entries, and building the map costs
            // a deserialization per GameObject and per Transform in the file.
            var entries = BundleReader.UnwrapArray(list);
            if (entries.Count == 0) return;
            var names = NodeNames();
            foreach (var e in entries)
            {
                var p = BundleReader.FindPtr(e);
                if (p is null || p["m_FileID"].AsInt != 0) continue;
                if (!names.TryGetValue(p["m_PathID"].AsLong, out var name)) continue;
                visibility ??= new Dictionary<string, VisibilityOverride>(StringComparer.OrdinalIgnoreCase);
                if (!visibility.TryGetValue(name, out var have) || why < have) visibility[name] = why;
            }
        }

        foreach (var comp in BundleReader.UnwrapArray(goField["m_Component"]))
        {
            var ptr = BundleReader.FindPtr(comp);
            if (ptr is null || ptr["m_FileID"].AsInt != 0) continue;
            var target = inst.file.GetAssetInfo(ptr["m_PathID"].AsLong);
            if (target is null || target.TypeId != BundleReader.ClassMonoBehaviour) continue;

            AssetTypeValueField mb;
            try { mb = am.GetBaseField(inst, target); } catch { continue; }
            if (!mb["allReplaceableMeshList"].IsDummy) replaceable = true;

            // The dorm components, identified by field shape rather than script name. Only the lists that
            // can SUBTRACT a draw are read: the context lists (DormNodes/FightNodes and their lodm0 twins)
            // are already modelled by the nodes' own name tails, LobbyShowNodes only ever adds a draw, and
            // the serialized lobby-hide flag is overwritten at every apply, so it says nothing.
            if (mb["ControlVisibleNodes"] is { IsDummy: false } coat)
                MarkNodes(coat, VisibilityOverride.CoatList);
            if (mb["DormHideNodes"] is { IsDummy: false } dormHide)
                MarkNodes(dormHide, VisibilityOverride.DormHidden);
            if (mb["LobbyHideNodes"] is { IsDummy: false } lobbyHide)
                MarkNodes(lobbyHide, VisibilityOverride.LobbyHidden);

            var list = mb["MeshResList"];
            if (list.IsDummy) continue;

            recipe = new List<PrefabRecipeEntry>();
            foreach (var e in BundleReader.UnwrapArray(list))
            {
                // the game's own field name is misspelled ("TransfromPath"); accept both spellings so a
                // game-side fix doesn't blank every slot
                var slot = !e["TransfromPath"].IsDummy ? e["TransfromPath"].AsString
                         : !e["TransformPath"].IsDummy ? e["TransformPath"].AsString
                         : throw new InvalidDataException(
                             $"recipe entry on '{rootName}' has no TransfromPath/TransformPath field");
                recipe.Add(new PrefabRecipeEntry(slot, e["MeshResPath"].AsString));
            }
        }
        // A static renderer keeps its mesh on the MeshFilter beside it, so the filters are indexed by the
        // GameObject they hang off before the renderer pass joins the two.
        var meshByGameObject = new Dictionary<long, AssetTypeValueField>();
        foreach (var info in inst.file.AssetInfos)
        {
            if (info.TypeId != BundleReader.ClassMeshFilter) continue;
            AssetTypeValueField filter;
            try { filter = am.GetBaseField(inst, info); } catch { continue; }
            var owner = BundleReader.FindPtr(filter["m_GameObject"]);
            // path id 0 is an unresolvable pointer, not an identity: indexing under it would let this filter
            // join any renderer whose own GameObject pointer failed to resolve
            if (owner is null || owner["m_FileID"].AsInt != 0 || owner["m_PathID"].AsLong == 0) continue;
            // A second filter on one GameObject is not a shape Unity produces; keeping the first is the only
            // deterministic answer, and taking a later one would silently change which mesh a slot means.
            meshByGameObject.TryAdd(owner["m_PathID"].AsLong, filter["m_Mesh"]);
        }

        var slots = new List<PrefabSlot>();
        foreach (var info in inst.file.AssetInfos)
        {
            var kind = info.TypeId == BundleReader.ClassSkinnedMeshRenderer ? SlotRenderer.Skinned
                     : info.TypeId == BundleReader.ClassMeshRenderer ? SlotRenderer.Static
                     : (SlotRenderer?)null;
            if (kind is not { } renderer) continue;
            AssetTypeValueField rend;
            try { rend = am.GetBaseField(inst, info); } catch { continue; }

            // slot name = the renderer's GameObject name (local by construction on these prefabs)
            string slotName = "?";
            long slotGoPathId = 0;
            var goPtr = BundleReader.FindPtr(rend["m_GameObject"]);
            if (goPtr is not null && goPtr["m_FileID"].AsInt == 0 &&
                inst.file.GetAssetInfo(goPtr["m_PathID"].AsLong) is { } slotGo &&
                slotGo.TypeId == BundleReader.ClassGameObject)
            {
                slotGoPathId = goPtr["m_PathID"].AsLong;
                try { slotName = am.GetBaseField(inst, slotGo)["m_Name"].AsString; } catch { }
            }
            // under this root's Transform, or another container root's renderer (see Read); a slot whose
            // GameObject did not resolve keeps the one-root answer rather than being judged on nothing
            bool inRoot = subtree is null || slotGoPathId == 0 || subtree.Contains(slotGoPathId);

            var mats = new List<PrefabMaterialRef>();
            foreach (var m in BundleReader.UnwrapArray(rend["m_Materials"]))
            {
                var p = BundleReader.FindPtr(m);
                if (p is null) continue;
                int fid = p["m_FileID"].AsInt;
                long pid = p["m_PathID"].AsLong;
                if (fid == 0 && pid == 0) { mats.Add(new PrefabMaterialRef(0, null)); continue; }  // empty slot kept: order IS the binding
                mats.Add(new PrefabMaterialRef(pid, Cab(fid, externals, "material", slotName)));
            }

            // skinned: the renderer's own m_Mesh; static: the MeshFilter's, joined on the shared GameObject.
            // A static renderer with no filter carries no mesh identity at all, which reads as a mesh-less
            // slot — the same "game filler, never materializable" answer an empty SMR slot gets.
            AssetTypeValueField? meshField = renderer == SlotRenderer.Skinned ? rend["m_Mesh"]
                : slotGoPathId != 0 && meshByGameObject.TryGetValue(slotGoPathId, out var filterMesh)
                    ? filterMesh : null;
            var meshPtr = meshField is null ? null : BundleReader.FindPtr(meshField);
            PrefabMeshRef? meshRef = null;
            if (meshPtr is not null)
            {
                int mfid = meshPtr["m_FileID"].AsInt;
                long mpid = meshPtr["m_PathID"].AsLong;
                if (mfid != 0 || mpid != 0)
                    meshRef = new PrefabMeshRef(mpid, Cab(mfid, externals, "mesh", slotName));
            }
            // m_CastShadows lives on the Renderer base, so both classes carry it. Only a readable 0 (Off)
            // takes a slot out of the shadow pass; a missing field leaves it casting, since the exclusions
            // this feeds must ride a measured Off and never an unread one.
            var castField = rend["m_CastShadows"];
            bool castsShadows = castField.IsDummy || castField.AsInt != 0;
            slots.Add(new PrefabSlot(slotName, info.PathId, mats, meshRef, renderer, castsShadows, inRoot));
        }

        // accept a recipe root, or a recipe-less root whose slots carry serialized meshes (smr-body, prop)
        if (recipe is null && !slots.Any(s => s.Mesh is not null)) return null;

        // path id breaks a name tie, so two slots a prefab gives one name keep a fixed order across reads
        slots.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name) is var c and not 0
            ? c : a.PathId.CompareTo(b.PathId));
        return new CharacterPrefab(rootName, recipe ?? new List<PrefabRecipeEntry>(), slots, externals,
            replaceable, visibility);
    }

    /// <summary>The dependency CAB a PPtr's fileID names: null for a local reference, else the external at
    /// that 1-based index. An index past the dependency list means the two disagree, which can only resolve
    /// to the wrong asset — so it throws rather than pick one.</summary>
    private static string? Cab(int fileId, IReadOnlyList<string> externals, string what, string slotName) =>
        fileId == 0 ? null
        : fileId - 1 < externals.Count ? externals[fileId - 1]
        : throw new InvalidDataException($"{what} PPtr fileID {fileId} out of range on slot '{slotName}'");
}
