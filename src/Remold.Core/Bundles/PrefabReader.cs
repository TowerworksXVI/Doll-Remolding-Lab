using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

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
public sealed record PrefabSlot(string Name, long PathId, IReadOnlyList<PrefabMaterialRef> Materials,
    PrefabMeshRef? Mesh, SlotRenderer Renderer = SlotRenderer.Skinned)
{
    /// <summary>True when the renderer ships a serialized mesh (an smr-body or static slot).</summary>
    public bool HasMesh => Mesh is not null;
}

/// <summary>A parsed assembly prefab: root GameObject, mesh recipe, renderer slots with their CAB-exact
/// material bindings, and the dependency CABs.</summary>
public sealed record CharacterPrefab(
    string RootName,
    IReadOnlyList<PrefabRecipeEntry> Recipe,
    IReadOnlyList<PrefabSlot> Slots,
    IReadOnlyList<string> ExternalCabs,
    bool HasReplaceableModel);

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

        foreach (var entry in BundleReader.UnwrapArray(container))
        {
            var asset = BundleReader.FindPtr(
                entry["second"]["asset"].IsDummy ? entry["second"] : entry["second"]["asset"]);
            if (asset is null || asset["m_FileID"].AsInt != 0) continue;
            var goInfo = inst.file.GetAssetInfo(asset["m_PathID"].AsLong);
            if (goInfo is null || goInfo.TypeId != BundleReader.ClassGameObject) continue;

            var goField = am.GetBaseField(inst, goInfo);
            var name = goField["m_Name"].AsString;
            if (rootName is not null && !string.Equals(name, rootName, StringComparison.Ordinal)) continue;

            var parsed = ReadRoot(am, inst, name, goField, externals);
            if (parsed is not null) return parsed;
            declinedRoot = true;
        }
        return null;
    }

    /// <summary>Parse one container root: its recipe MB, the ReplaceableModel marker, and every renderer
    /// slot in the file — skinned and static alike (one root's slots per bundle; SlotPath names tie them
    /// together regardless of hierarchy). A recipe-less root is accepted iff a slot carries a serialized
    /// mesh.</summary>
    private static CharacterPrefab? ReadRoot(AssetsManager am, AssetsFileInstance inst, string rootName,
        AssetTypeValueField goField, IReadOnlyList<string> externals)
    {
        List<PrefabRecipeEntry>? recipe = null;
        bool replaceable = false;
        foreach (var comp in BundleReader.UnwrapArray(goField["m_Component"]))
        {
            var ptr = BundleReader.FindPtr(comp);
            if (ptr is null || ptr["m_FileID"].AsInt != 0) continue;
            var target = inst.file.GetAssetInfo(ptr["m_PathID"].AsLong);
            if (target is null || target.TypeId != BundleReader.ClassMonoBehaviour) continue;

            AssetTypeValueField mb;
            try { mb = am.GetBaseField(inst, target); } catch { continue; }
            if (!mb["allReplaceableMeshList"].IsDummy) replaceable = true;
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
            slots.Add(new PrefabSlot(slotName, info.PathId, mats, meshRef, renderer));
        }

        // accept a recipe root, or a recipe-less root whose slots carry serialized meshes (smr-body, prop)
        if (recipe is null && !slots.Any(s => s.Mesh is not null)) return null;

        // path id breaks a name tie, so two slots a prefab gives one name keep a fixed order across reads
        slots.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name) is var c and not 0
            ? c : a.PathId.CompareTo(b.PathId));
        return new CharacterPrefab(rootName, recipe ?? new List<PrefabRecipeEntry>(), slots, externals, replaceable);
    }

    /// <summary>The dependency CAB a PPtr's fileID names: null for a local reference, else the external at
    /// that 1-based index. An index past the dependency list means the two disagree, which can only resolve
    /// to the wrong asset — so it throws rather than pick one.</summary>
    private static string? Cab(int fileId, IReadOnlyList<string> externals, string what, string slotName) =>
        fileId == 0 ? null
        : fileId - 1 < externals.Count ? externals[fileId - 1]
        : throw new InvalidDataException($"{what} PPtr fileID {fileId} out of range on slot '{slotName}'");
}
