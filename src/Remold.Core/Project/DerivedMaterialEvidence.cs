using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Remold.Core.Bundles;

namespace Remold.Core.Project;

/// <summary>
/// Derives material-value reflection evidence from the CURRENT install, replacing any build-pinned
/// record: the exact material's serialized keywords select its shader-variant family, Unity's own
/// serialized reflection proves where <c>UnityPerMaterial</c> binds and which fields each variant
/// declares, and the shipped DXBC hashes to the 3DMigoto shader hash offline (measured — the offline
/// hashes reproduce the frame-dump-observed ones).
///
/// <para>The evidence names the WHOLE candidate family — every variant the game can bind at this
/// material's draws that declares the patched layout, across the runtime keyword axes (shadow quality,
/// fog, LOD, render features) — because the bound variant differs per machine and per scene, and a
/// single-variant gate would ship a patch that silently never fires elsewhere. One derived filter
/// value covers the family. A material whose shader is not the character shader, whose candidates
/// disagree on layout, or whose family is empty resolves to null: the plan then blocks the binding
/// rather than shipping a guess.</para>
/// </summary>
public sealed class DerivedMaterialEvidence
{
    /// <summary>The character shader bundle — every verified character-family material's shader
    /// resolves into this one bundle (measured over the 3,604-material corpus; enemy and NPC uber
    /// materials included). A material pointing anywhere else is not on a supported shader.</summary>
    public const string CharacterShaderBundle = "b49672c1f108d4773433006407c81442.bundle";

    /// <summary>An upper bound no measured family approaches (the worst measured family is ~200
    /// variants). Reaching it means the selection rule broke, not that the shader grew.</summary>
    private const int CandidateCap = 512;

    private readonly Func<string, byte[]?> _deobfuscate;
    private readonly BundleReader _reader = new();
    private readonly Func<byte[], long, BundleReader.MaterialShading?> _readMaterial;
    private readonly Func<byte[], long, IReadOnlyList<ShaderVariant>?> _readVariants;
    private readonly Func<byte[], string> _readCab;
    private readonly object _gate = new();
    private readonly Dictionary<(string Bundle, long PathId), MaterialRenderEvidence> _byMaterial
        = new();
    private byte[]? _shaderBundleBytes;
    private ulong? _shaderBundleContent;

    /// <summary>Variant tables keyed by bundle content (FNV over the deobfuscated bytes) and shader
    /// path id — shared process-wide, so repeated plans re-read nothing but the bundle bytes.</summary>
    private static readonly ConcurrentDictionary<(ulong Content, long PathId),
        IReadOnlyList<ShaderVariant>> VariantCache = new();

    public DerivedMaterialEvidence(Func<string, byte[]?> deobfuscate)
    {
        _deobfuscate = deobfuscate ?? throw new ArgumentNullException(nameof(deobfuscate));
        _readMaterial = _reader.GetMaterialShading;
        _readVariants = _reader.GetShaderVariants;
        _readCab = _reader.GetBundleCab;
    }

    /// <summary>Exact reader seams for derivation tests. Production always uses one BundleReader for all
    /// three operations, preserving its bounded parse cache.</summary>
    internal DerivedMaterialEvidence(Func<string, byte[]?> deobfuscate,
        Func<byte[], long, BundleReader.MaterialShading?> readMaterial,
        Func<byte[], long, IReadOnlyList<ShaderVariant>?> readVariants,
        Func<byte[], string> readCab)
    {
        _deobfuscate = deobfuscate ?? throw new ArgumentNullException(nameof(deobfuscate));
        _readMaterial = readMaterial ?? throw new ArgumentNullException(nameof(readMaterial));
        _readVariants = readVariants ?? throw new ArgumentNullException(nameof(readVariants));
        _readCab = readCab ?? throw new ArgumentNullException(nameof(readCab));
    }

    /// <summary>Evidence for the slot's exact current material, or null when none can be derived —
    /// the caller blocks the binding either way, so null never ships a guess.</summary>
    public MaterialRenderEvidence? Resolve(TargetSlot slot)
    {
        var material = slot.Material;
        if (material is null || string.IsNullOrWhiteSpace(material.LogicalBundle)
            || material.PathId == 0)
            return null;
        var key = (material.LogicalBundle, material.PathId);
        lock (_gate)
        {
            if (_byMaterial.TryGetValue(key, out var known)) return known;
            MaterialRenderEvidence? derived;
            try { derived = Derive(material.LogicalBundle, material.PathId); }
            catch { return null; }
            if (derived is null) return null;
            return _byMaterial[key] = derived;
        }
    }

    private MaterialRenderEvidence? Derive(string logicalBundle, long pathId)
    {
        byte[]? materialBytes = _deobfuscate(logicalBundle);
        if (materialBytes is null) return null;
        var shading = _readMaterial(materialBytes, pathId);
        if (shading is null) return null;

        // The shader must live in the character shader bundle: in it directly, or referenced through
        // an external whose CAB is that bundle's own.
        byte[]? shaderBytes;
        if (shading.ShaderFileId == 0)
        {
            if (!string.Equals(logicalBundle, CharacterShaderBundle, StringComparison.Ordinal))
                return null;
            shaderBytes = materialBytes;
        }
        else
        {
            _shaderBundleBytes ??= _deobfuscate(CharacterShaderBundle);
            if (_shaderBundleBytes is null) return null;
            if (shading.ShaderFileId > shading.ExternalCabs.Count) return null;
            string wantCab = shading.ExternalCabs[shading.ShaderFileId - 1];
            if (!string.Equals(_readCab(_shaderBundleBytes), wantCab,
                    StringComparison.Ordinal))
                return null;
            shaderBytes = _shaderBundleBytes;
        }

        var variants = VariantsOf(shaderBytes, shading.ShaderPathId);
        if (variants is not { Count: > 0 }) return null;

        // This point is behind the proved character-shader boundary above. MaterialDrivenKeywords is a
        // corpus-wide allowlist for that one patchable shader, never an ownership claim about another
        // shader. Everything outside the set remains a runtime axis a candidate may hold in any state.
        var shaderKeywords = variants.SelectMany(variant => variant.Keywords)
            .ToHashSet(StringComparer.Ordinal);
        var want = shading.EnabledKeywords
            .Where(keyword => MaterialValueCatalog.MaterialDrivenKeywords.Contains(keyword)
                && shaderKeywords.Contains(keyword))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = variants.Where(variant =>
                variant.MaterialBufferSlot == 2
                && variant.Keywords
                    .Where(MaterialValueCatalog.MaterialDrivenKeywords.Contains)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(want))
            .ToList();
        if (candidates.Count == 0) return null;
        if (candidates.Any(candidate => candidate.MaterialBufferWidth is not (544 or 592)))
            return null;
        int width = candidates[0].MaterialBufferWidth;
        if (candidates.Any(candidate => candidate.MaterialBufferWidth != width)) return null;

        var hashes = candidates.Select(candidate => candidate.DxbcHash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(hash => hash, StringComparer.Ordinal).ToList();
        if (hashes.Count > CandidateCap) return null;

        // Every catalog field the width carries that at least one candidate declares, each verified
        // against the measured offset — a declaring variant at ANOTHER offset would mean the layout
        // moved, and the whole material refuses rather than patching a guessed byte.
        var fields = new List<BuildMaterialValueField>();
        foreach (var field in MaterialValueCatalog.Fields)
        {
            if (field.OffsetIn(width) is not { } offset) continue;
            int declaring = 0;
            foreach (var candidate in candidates)
            {
                if (!candidate.VectorOffsets.TryGetValue(field.Semantic, out int declared)) continue;
                if (declared != offset) return null;
                declaring++;
            }
            if (declaring == 0) continue;
            fields.Add(new BuildMaterialValueField(field.Semantic, 2, offset,
                $"serialized reflection: {declaring} of {candidates.Count} candidate variants declare "
                + $"the field at ps-cb2+{offset}"));
        }
        if (fields.Count == 0) return null;

        string layout = width == 592
            ? MaterialValueCatalog.UnityPerMaterial592
            : MaterialValueCatalog.UnityPerMaterial544;
        string identity = want.Count == 0
            ? $"{candidates[0].ShaderName}"
            : $"{candidates[0].ShaderName} [{string.Join(" ", want.OrderBy(x => x, StringComparer.Ordinal))}]";
        return new MaterialRenderEvidence(identity, hashes, FamilyFilterValue(hashes), layout, fields,
            $"serialized shader reflection over the current install: {hashes.Count} candidate variants "
            + $"bind UnityPerMaterial at ps-cb2 with {width} bytes");
    }

    private IReadOnlyList<ShaderVariant>? VariantsOf(byte[] shaderBundleBytes, long shaderPathId)
    {
        // One instance belongs to one immutable install, and every supported route above proves these
        // bytes are its character-shader bundle. Hash that bundle once for all of its material objects.
        var key = (_shaderBundleContent ??= ShaderReflection.Fnv64(shaderBundleBytes), shaderPathId);
        if (VariantCache.TryGetValue(key, out var cached)) return cached;
        var variants = _readVariants(shaderBundleBytes, shaderPathId);
        if (variants is null) return null;
        return VariantCache.GetOrAdd(key, variants);
    }

    /// <summary>One filter value per candidate family, derived from the sorted hash set so every build
    /// of the same family agrees, in a range exact under the runtime's float comparison.</summary>
    internal static int FamilyFilterValue(IReadOnlyList<string> sortedHashes)
    {
        ulong digest = 0;
        foreach (string hash in sortedHashes)
            foreach (char c in hash)
            {
                digest = unchecked(digest * 0x100000001b3UL);
                digest ^= (byte)c;
            }
        return 1_000_000 + (int)(digest % 15_000_000UL);
    }
}
