using System;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;
using Remold.Core.Migoto;
using Remold.Core.Workbench;

namespace Remold.Core.Tests.Migoto;

/// <summary>The synthetic install, answering with EXACT object identity for every subject it resolves.
///
/// <para>A real install always does: a renderer, a material and a texture each have a logical bundle and a
/// path id, and the build stamps its catalog version on them. The build reads a released project by
/// re-anchoring every route against those identities, so a fixture whose roster names objects without them
/// is not a smaller install — it is one no route can be re-anchored in, and the whole boundary refuses it.
/// Texture path ids are read from the fixture's own bundle bytes, so a build that later loads one by id
/// finds the same asset; the identities nothing reads by are synthesized, stable per name.</para></summary>
internal static class ExactInstallEnv
{
    /// <summary>Where a mesh identity points when the fixture names the mesh but serves no bytes for it.
    /// It is an exact address, so every route can re-anchor on it — and one no install answers, so a build
    /// that actually reaches for the geometry still fails exactly where it did before.</summary>
    private const string UnservedBundle = "unserved-by-this-fixture";

    internal static BuildEnv Exact(this BuildEnv env)
    {
        var reader = new BundleReader();
        var models = new Dictionary<(string, string), SubjectModel?>();
        var textureIds = new Dictionary<(string, string), long>();
        var inner = env.ResolveSubject;

        long TextureId(string bundleId, string name)
        {
            if (textureIds.TryGetValue((bundleId, name), out long have)) return have;
            long found = 0;
            try
            {
                if (env.Deobfuscate(bundleId) is { } bytes)
                {
                    var matches = reader.ListAssets(bytes, BundleReader.ClassTexture2D)
                        .Where(asset => asset.Name == name).ToList();
                    if (matches.Count == 1) found = matches[0].PathId;
                }
            }
            catch { }
            return textureIds[(bundleId, name)] = found != 0 ? found : Id(bundleId + "|" + name);
        }

        // A mesh no bundle SERVES — the address answers nothing, its bundle carries no bytes, or those
        // bytes hold no mesh of that name — resolves to no exact object, and there is nothing to read one
        // out of. Such a fixture is naming its meshes rather than serving them, so the identity is
        // synthesized here and points at a bundle this install still cannot read: an exact address a build
        // reaching for the mesh fails on with the same words it always did. A bundle that really holds the
        // mesh keeps its real read, untouched.
        (string? Bundle, long PathId) MeshIdentity(string subject, string slotName, string address,
            string? bundle, long pathId)
        {
            if (!string.IsNullOrWhiteSpace(bundle) && pathId != 0) return (bundle, pathId);
            if (string.IsNullOrWhiteSpace(address)) return (bundle, pathId);
            string? owner;
            try
            {
                owner = env.ResolveAddress(address);
                if (owner is { } named && env.Deobfuscate(named) is { } bytes
                    && reader.ListAssets(bytes, BundleReader.ClassMesh)
                        .Count(asset => asset.Name == slotName) == 1)
                    return (bundle, pathId);
            }
            catch { return (bundle, pathId); }
            // the bundle the catalog names, so a read that fails still fails naming it
            return (owner ?? UnservedBundle, Id($"{subject}|mesh|{slotName}"));
        }

        SubjectModel? Resolve(string character, string stem)
        {
            var key = (character.ToLowerInvariant(), stem.ToLowerInvariant());
            if (models.TryGetValue(key, out var cached)) return cached;
            var model = inner(character, stem);
            if (model is null) return models[key] = null;
            string subject = $"{model.Character}|{model.Stem}";
            return models[key] = model with
            {
                Parts = model.Parts.Select(part =>
                {
                    var mesh = MeshIdentity(subject, part.SlotName, part.MeshAddress,
                        part.MeshBundle, part.MeshPathId);
                    return part with
                {
                    MeshBundle = mesh.Bundle,
                    MeshPathId = mesh.PathId,
                    RendererBundle = string.IsNullOrWhiteSpace(part.RendererBundle)
                        ? "bundle0" : part.RendererBundle,
                    RendererPathId = part.RendererPathId != 0
                        ? part.RendererPathId : Id($"{subject}|renderer|{part.SlotName}"),
                    SiblingTiers = part.SiblingTiers?.Select(tier =>
                    {
                        var tierMesh = MeshIdentity(subject, tier.SlotName, tier.MeshAddress,
                            tier.MeshBundle, tier.MeshPathId);
                        return tier with
                        {
                            MeshBundle = tierMesh.Bundle,
                            MeshPathId = tierMesh.PathId,
                            RendererBundle = string.IsNullOrWhiteSpace(tier.RendererBundle)
                                ? "bundle0" : tier.RendererBundle,
                            RendererPathId = tier.RendererPathId != 0
                                ? tier.RendererPathId : Id($"{subject}|renderer|{tier.SlotName}"),
                        };
                    }).ToArray(),
                    Materials = part.Materials.Select((material, index) => material.IsPlaceholder
                        ? material
                        : material with
                        {
                            Bundle = string.IsNullOrWhiteSpace(material.Bundle)
                                ? "bundle0" : material.Bundle,
                            PathId = material.PathId != 0
                                ? material.PathId
                                : Id($"{subject}|material|{part.SlotName}|{index}|{material.Name}"),
                            Maps = material.Maps.Select(map => map with
                            {
                                PathId = map.PathId != 0
                                    ? map.PathId : TextureId(map.BundleId, map.TextureName),
                            }).ToArray(),
                        }).ToArray(),
                    };
                }).ToArray(),
            };
        }

        return env with
        {
            ResolveSubject = Resolve,
            CatalogVersion = env.CatalogVersion ?? "test-catalog",
        };
    }

    /// <summary>A stable positive path id for one name. Only identities no read selects by are given one,
    /// so what it has to be is unique and the same on every run.</summary>
    private static long Id(string name)
    {
        unchecked
        {
            ulong hash = 14695981039346656037;
            foreach (char c in name)
            {
                hash ^= c;
                hash *= 1099511628211;
            }
            return (long)(hash & 0x3FFF_FFFF_FFFF) + 1_000_000;
        }
    }
}
