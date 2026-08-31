using System;
using System.Collections.Generic;
using System.IO;
using Remold.Core.Bundles;
using Remold.Core.Mesh;

namespace Remold.Core.Migoto;

/// <summary>
/// Export a rigged reference glb — mesh + armature (bone-hash node names) + weights — for a modder to
/// weight NEW geometry against in Blender, then compile with <see cref="SwapCompile"/>.
///
/// <para><see cref="ExportPool"/> merges several parts of one outfit into one session sharing ONE union
/// armature; compile with <see cref="SwapCompile.CompilePool"/> passing the SAME parts in the SAME
/// order.</para>
///
/// Operates on already-deobfuscated bundle bytes; the caller resolves bundle and bone names.
/// </summary>
public static class SwapReference
{
    /// <summary>Export a single part's rigged reference glb. <paramref name="resolveBone"/> maps a bone
    /// hash to a real bone path (null falls back to hash-named nodes).</summary>
    public static void ExportPart(byte[] deobfuscatedBundle, string meshName, string outGlb,
        Func<uint, string?>? resolveBone = null, long pathId = 0)
    {
        var field = new BundleReader().GetMeshField(deobfuscatedBundle, meshName, pathId)
            ?? throw new InvalidDataException(
                $"the game files no longer hold the mesh '{meshName}'. Rescan, then build again");

        var mesh = UnityMesh.Decode(field);
        var skin = MeshSkin.Decode(field);
        if (!skin.IsSkinned) throw new InvalidOperationException("mesh is skinless — no rig to weight against");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outGlb))!);
        MeshGltf.ExportRiggedGlb(mesh, skin, resolveBone ?? (_ => null), outGlb);
    }

    /// <summary>Export a pooled rigged reference glb: <paramref name="meshNames"/> merged into one union
    /// armature. <paramref name="resolveBone"/> maps a bone hash to a real bone path (null → hash-named).</summary>
    public static void ExportPool(byte[] deobfuscatedBundle, IReadOnlyList<string> meshNames,
        Func<uint, string?> resolveBone, string outGlb)
    {
        var reader = new BundleReader();
        var parts = new List<MeshGltf.RiggedPart>();
        foreach (var name in meshNames)
        {
            var field = reader.GetMeshField(deobfuscatedBundle, name)
                ?? throw new InvalidDataException(
                $"the game files no longer hold the mesh '{name}'. Rescan, then build again");
            var mesh = UnityMesh.Decode(field);
            var skin = MeshSkin.Decode(field);
            if (!skin.IsSkinned) throw new InvalidOperationException($"{name}: skinless — cannot join a pooled rig");
            parts.Add(new MeshGltf.RiggedPart(mesh, skin));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outGlb))!);
        MeshGltf.ExportCombinedRiggedGlb(parts, resolveBone, outGlb);
    }
}
