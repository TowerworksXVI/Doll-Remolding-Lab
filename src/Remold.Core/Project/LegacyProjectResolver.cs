using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Materials;
using Remold.Core.Mesh;
using Remold.Core.Migoto;
using Remold.Core.Workbench;

namespace Remold.Core.Project;

/// <summary>Re-anchors a schema-1 structural route against the same current-install world a build reads.
/// It establishes exact object identity only; compatibility and runtime capability belong to planning.</summary>
public sealed class LegacyProjectResolver
{
    private readonly BuildEnv _env;
    private readonly BundleReader _reader;
    private readonly Dictionary<string, byte[]?> _bundles = new(StringComparer.Ordinal);
    // One structural target has one current-install answer for the life of this resolver. Planning asks the
    // same part once per binding (and again for texture targeting); retaining the complete answer also retains
    // its MaterialIndexCounts parse rather than reopening the mesh field for each row.
    private readonly Dictionary<string, LegacyResolvedPart?> _parts = new(StringComparer.OrdinalIgnoreCase);

    public LegacyProjectResolver(BuildEnv env, BundleReader? reader = null)
    {
        _env = env;
        _reader = reader ?? new BundleReader();
    }

    /// <summary>Every renderer slot this install answers for on one subject. A released project's edited
    /// textures reach the parts of this roster that bind them — the live join the released derivation made
    /// — so the adapter asks for it beside <see cref="ResolvePart"/>. Empty where the subject itself does
    /// not resolve, which is the state the adapter reports as unre-anchored.</summary>
    public IReadOnlyList<string> RosterSlots(string character, string outfit) =>
        _env.ResolveSubject(character, outfit)?.Parts.Select(part => part.SlotName).ToList()
            ?? (IReadOnlyList<string>)Array.Empty<string>();

    public LegacyResolvedPart? ResolvePart(TargetPart target)
    {
        if (_parts.TryGetValue(target.Key, out var cached)) return cached;
        return _parts[target.Key] = ResolvePartOnce(target);
    }

    private LegacyResolvedPart? ResolvePartOnce(TargetPart target)
    {
        var model = _env.ResolveSubject(target.Subject, target.Outfit);
        if (model is null) return null;
        var matches = model.Parts.Where(p =>
            string.Equals(p.SlotName, target.RendererSlot, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count != 1) return null;
        var part = matches[0];

        var materials = part.Materials.Select((material, index) => new LegacyResolvedMaterial(
            index,
            material.Name,
            Ref(material.Bundle, material.PathId, material.Name),
            material.Maps.Select(map => new LegacyResolvedTexture(
                InputOf(map.Slot),
                map.BundleId,
                map.TextureName,
                map.PathId == 0 ? null : map.PathId,
                Ref(map.BundleId, map.PathId, map.TextureName),
                map.Slot)).ToList())).ToList();

        var tiers = new List<LegacyResolvedTier>();
        foreach (var tier in part.SiblingTiers ?? Array.Empty<Export.RecipeTierSlot>())
            tiers.Add(new LegacyResolvedTier(
                tier.SlotName,
                Model.MeshName.Lod(tier.SlotName) is { Length: > 0 } lod ? lod : tier.SlotName,
                Ref(tier.RendererBundle, tier.RendererPathId, tier.SlotName),
                ResolveMesh(tier.SlotName, tier.MeshAddress, tier.MeshBundle, tier.MeshPathId)));

        var mesh = ResolveMesh(part.SlotName, part.MeshAddress, part.MeshBundle, part.MeshPathId);
        return new LegacyResolvedPart(
            new TargetPart
            {
                Subject = target.Subject,
                Outfit = target.Outfit,
                RendererSlot = target.RendererSlot,
            },
            Ref(part.RendererBundle, part.RendererPathId, part.SlotName),
            mesh,
            materials,
            tiers,
            MaterialIndexCounts(mesh));
    }

    private IReadOnlyList<int>? MaterialIndexCounts(GameAssetRef mesh)
    {
        if (string.IsNullOrWhiteSpace(mesh.LogicalBundle) || mesh.PathId == 0) return null;
        try
        {
            byte[]? bytes = Bundle(mesh.LogicalBundle);
            var field = bytes is null ? null : _reader.GetMeshField(bytes, mesh.Name ?? "", mesh.PathId);
            return field is null ? null : MeshRaw.From(field).Submeshes
                .Select(submesh => checked((int)submesh.IndexCount)).ToArray();
        }
        catch { return null; }
    }

    private GameAssetRef ResolveMesh(string name, string address, string? directBundle, long directPathId)
    {
        if (!string.IsNullOrWhiteSpace(directBundle) && directPathId != 0)
            return Ref(directBundle, directPathId, name);
        string? bundle = string.IsNullOrWhiteSpace(address) ? null : _env.ResolveAddress(address);
        if (bundle is null) return Ref(null, 0, name);
        byte[]? bytes = Bundle(bundle);
        if (bytes is null) return Ref(bundle, 0, name);
        var matches = _reader.ListAssets(bytes, BundleReader.ClassMesh)
            .Where(a => string.Equals(a.Name, name, StringComparison.Ordinal)).ToList();
        return Ref(bundle, matches.Count == 1 ? matches[0].PathId : 0, name);
    }

    private byte[]? Bundle(string logicalBundle)
    {
        if (!_bundles.TryGetValue(logicalBundle, out var bytes))
            _bundles[logicalBundle] = bytes = _env.Deobfuscate(logicalBundle);
        return bytes;
    }

    private GameAssetRef Ref(string? logicalBundle, long pathId, string? name) => new()
    {
        GameBuild = _env.CatalogVersion ?? "",
        LogicalBundle = logicalBundle ?? "",
        PathId = pathId,
        Name = name,
    };

    private static TargetInputKind InputOf(string slot) =>
        MaterialResolver.IsBaseColor(slot) ? TargetInputKind.BaseColor
        : MaterialResolver.IsNormal(slot) ? TargetInputKind.Normal
        : MaterialResolver.IsRmo(slot) ? TargetInputKind.Rmo
        : MaterialResolver.IsRamp(slot) ? TargetInputKind.Ramp
        : MaterialResolver.IsBlend(slot) ? TargetInputKind.Blend
        : TargetInputKind.Texture;
}
