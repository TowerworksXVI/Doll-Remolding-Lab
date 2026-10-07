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
///
/// <para>The same instance answers the material's effects (which its drawn programs compile in, and the
/// exact input that disables each), the fields those programs read, and its original numeric values with
/// the exact shader's declared defaults. One instance belongs to one immutable install and reads each
/// material bundle once for all four answers; shader variant tables and numeric defaults are shared
/// process-wide by bundle content.</para>
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
    private readonly Func<byte[], long, BundleReader.ShaderNumericDefaults?> _readDefaults;
    private readonly object _gate = new();
    private readonly Dictionary<(string Bundle, long PathId), MaterialSource> _sources = new();
    private readonly Dictionary<(string Bundle, long PathId), MaterialRenderEvidence> _byMaterial
        = new();
    private readonly Dictionary<(string Bundle, long PathId), EffectSelection> _effectSelections = new();
    private readonly Dictionary<(string Bundle, long PathId), MaterialOriginalValues> _originals = new();
    private byte[]? _shaderBundleBytes;
    private ulong? _shaderBundleContent;
    private (string Logical, byte[] Bytes)? _lastMaterialBundle;

    /// <summary>Variant tables keyed by bundle content (FNV over the deobfuscated bytes) and shader
    /// path id — shared process-wide, so repeated plans re-read nothing but the bundle bytes.</summary>
    private static readonly ConcurrentDictionary<(ulong Content, long PathId),
        IReadOnlyList<ShaderVariant>> VariantCache = new();

    /// <summary>The exact shader's declared numeric defaults, keyed the same way: one property-table read
    /// per shader per install content, however many materials draw through it.</summary>
    private static readonly ConcurrentDictionary<(ulong Content, long PathId),
        BundleReader.ShaderNumericDefaults?> DefaultsCache = new();

    public DerivedMaterialEvidence(Func<string, byte[]?> deobfuscate)
    {
        _deobfuscate = deobfuscate ?? throw new ArgumentNullException(nameof(deobfuscate));
        _readMaterial = _reader.GetMaterialShading;
        _readVariants = _reader.GetShaderVariants;
        _readCab = _reader.GetBundleCab;
        _readDefaults = _reader.GetShaderNumericDefaults;
    }

    /// <summary>Exact reader seams for derivation tests. Production always uses one BundleReader for all
    /// four operations, preserving its bounded parse cache.</summary>
    internal DerivedMaterialEvidence(Func<string, byte[]?> deobfuscate,
        Func<byte[], long, BundleReader.MaterialShading?> readMaterial,
        Func<byte[], long, IReadOnlyList<ShaderVariant>?> readVariants,
        Func<byte[], string> readCab,
        Func<byte[], long, BundleReader.ShaderNumericDefaults?>? readDefaults = null)
    {
        _deobfuscate = deobfuscate ?? throw new ArgumentNullException(nameof(deobfuscate));
        _readMaterial = readMaterial ?? throw new ArgumentNullException(nameof(readMaterial));
        _readVariants = readVariants ?? throw new ArgumentNullException(nameof(readVariants));
        _readCab = readCab ?? throw new ArgumentNullException(nameof(readCab));
        _readDefaults = readDefaults ?? ((_, _) => null);
    }

    /// <summary>One material's serialized shading, the bytes of the bundle holding its shader object
    /// (its own bundle when the reference is local), and whether that shader is the character shader
    /// — the only one evidence and effects are derived for. Original values need only the shader
    /// object's property table, wherever it lives.</summary>
    private sealed record MaterialSource(BundleReader.MaterialShading Shading, byte[]? ShaderBytes,
        bool CharacterShader);

    /// <summary>A material's drawn programs and what they establish: the effect rows it offers, the exact
    /// operation behind each (auxiliary-pass operations included, so an authored off switch covers that
    /// pass whenever it is drawn), and the fields its drawn programs read.</summary>
    private sealed record EffectSelection(IReadOnlyList<string> Hashes,
        IReadOnlyList<string> PresentEffects, IReadOnlyList<MaterialEffectOperation> Operations,
        IReadOnlySet<string>? ReadSemantics);

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

    /// <summary>Exact disable operations, including conditional auxiliary passes. These preserve
    /// authored off switches; they do not establish which effect rows a material offers.</summary>
    public IReadOnlyList<MaterialEffectOperation>? ResolveEffects(TargetSlot slot) =>
        ResolveEffectSelection(slot)?.Operations;

    /// <summary>Material capabilities for the editor and Copy comparison. A latent auxiliary
    /// program alone does not grant a capability.</summary>
    public IReadOnlyList<string>? ResolvePresentEffects(TargetSlot slot) =>
        ResolveEffectSelection(slot)?.PresentEffects;

    /// <summary>The shading fields the material's drawn programs read. A field a program only declares
    /// is not the material's value: the editor does not show it, Copy does not carry it. Null when any
    /// drawn program's reads are unknown — nothing is hidden on a guess.</summary>
    public IReadOnlySet<string>? ResolveReadSemantics(TargetSlot slot) =>
        ResolveEffectSelection(slot)?.ReadSemantics;

    /// <summary>The material's own original numeric values, with the exact shader's declared defaults
    /// behind any row it does not state. Null where the material or its shader cannot be read.</summary>
    public MaterialOriginalValues? ResolveOriginals(GameAssetRef material)
    {
        if (material is null || string.IsNullOrWhiteSpace(material.LogicalBundle) || material.PathId == 0)
            return null;
        var key = (material.LogicalBundle, material.PathId);
        lock (_gate)
        {
            if (_originals.TryGetValue(key, out var known)) return known;
            MaterialSource? source;
            try { source = LoadMaterial(material.LogicalBundle, material.PathId); }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
            if (source is null || !source.Shading.NumericPropertiesComplete) return null;
            return _originals[key] = new MaterialOriginalValues(source.Shading, DefaultsFor(source));
        }
    }

    private BundleReader.ShaderNumericDefaults? DefaultsFor(MaterialSource source)
    {
        if (source.Shading.ShaderPathId == 0 || source.ShaderBytes is null) return null;
        var key = (ContentOf(source.ShaderBytes), source.Shading.ShaderPathId);
        if (DefaultsCache.TryGetValue(key, out var cached)) return cached;
        BundleReader.ShaderNumericDefaults? defaults;
        try { defaults = _readDefaults(source.ShaderBytes, source.Shading.ShaderPathId); }
        catch (OperationCanceledException) { throw; }
        // Saved values remain usable when the exact shader's defaults cannot be read; the miss is not
        // cached, so the next material through this shader tries again.
        catch { return null; }
        return DefaultsCache.GetOrAdd(key, defaults);
    }

    private EffectSelection? ResolveEffectSelection(TargetSlot slot)
    {
        var material = slot.Material;
        if (material is null || string.IsNullOrWhiteSpace(material.LogicalBundle) || material.PathId == 0)
            return null;
        lock (_gate)
        {
            var key = (material.LogicalBundle, material.PathId);
            if (_effectSelections.TryGetValue(key, out var known)) return known;
            try
            {
                var source = LoadMaterial(material.LogicalBundle, material.PathId);
                if (source is not { CharacterShader: true, ShaderBytes: { } shaderBytes }) return null;
                var variants = VariantsOf(shaderBytes, source.Shading.ShaderPathId);
                if (variants is not { Count: > 0 }) return null;
                if (DrawnVariants(variants, source.Shading.EnabledKeywords) is not { Count: > 0 } selected)
                    return null;
                var programs = selected.GroupBy(variant => variant.DxbcHash, StringComparer.OrdinalIgnoreCase)
                    .Select(group => (Hash: group.Key.ToLowerInvariant(),
                        Effects: MaterialEffectRules.Evaluate(group.ToArray())))
                    .OrderBy(program => program.Hash, StringComparer.Ordinal).ToArray();
                var effectsByHash = programs.ToDictionary(program => program.Hash,
                    program => program.Effects, StringComparer.OrdinalIgnoreCase);
                // The shared conditional hair pass reads the highlight inputs on every uber material and
                // cannot establish the capability by itself; an independently selected consuming program
                // must. Its operation is retained regardless, so an authored off switch covers the pass
                // whenever it is drawn.
                bool hasHairHighlight = selected.Where(variant => !MaterialEffectRules.IsAuxiliaryHairPass(variant))
                    .Any(variant => effectsByHash[variant.DxbcHash].Any(effect => effect.EffectId == "hair-highlight"));
                var present = programs.SelectMany(program => program.Effects.Select(effect => effect.EffectId))
                    .Where(id => id != "hair-highlight" || hasHairHighlight)
                    .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
                var operations = MaterialEffectRules.Operations(programs.Select(program =>
                    (program.Hash, program.Effects)));
                HashSet<string>? read = null;
                string? family = MaterialFamilyClassifier.Family(source.Shading.Name);
                if (selected.All(variant => variant.MaterialReads is not null))
                {
                    read = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var variant in selected)
                    {
                        if (MaterialEffectRules.IsAuxiliaryHairPass(variant) && !hasHairHighlight) continue;
                        // A pass the game never draws for this material reads nothing of its values.
                        if (!MaterialFamilyClassifier.GameDrawsPass(family, variant.PassName)) continue;
                        foreach (var (semantic, offset) in variant.VectorOffsets)
                            if (variant.MaterialReads!.Reads(offset, MaterialValueCatalog.Field(semantic)?.Kind
                                    == MaterialValueKind.Color ? 16 : 4))
                                read.Add(semantic);
                    }
                }
                var selection = new EffectSelection(programs.Select(program => program.Hash).ToArray(),
                    present, operations, read);
                _effectSelections[key] = selection;
                return selection;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
    }

    /// <summary>Reads the material bundle once for every answer this instance gives about the material.
    /// A read that yields nothing is not memoized, so a transient failure is retried by the next asker.</summary>
    private MaterialSource? LoadMaterial(string logicalBundle, long pathId)
    {
        var key = (logicalBundle, pathId);
        if (_sources.TryGetValue(key, out var known)) return known;
        // Materials arrive in part order, so consecutive asks usually share a bundle: the last bundle's
        // bytes are kept so the reader's reference-keyed parse serves them all.
        byte[]? materialBytes;
        if (_lastMaterialBundle is { } last && string.Equals(last.Logical, logicalBundle, StringComparison.Ordinal))
            materialBytes = last.Bytes;
        else
        {
            materialBytes = _deobfuscate(logicalBundle);
            if (materialBytes is not null) _lastMaterialBundle = (logicalBundle, materialBytes);
        }
        if (materialBytes is null) return null;
        var shading = _readMaterial(materialBytes, pathId);
        if (shading is null) return null;

        // Evidence needs the character shader: the shader object in that bundle directly, or referenced
        // through an external whose CAB is that bundle's own. A local shader elsewhere still states its
        // property defaults; an external that is not the character bundle states nothing usable.
        byte[]? shaderBytes = null;
        bool characterShader = false;
        if (shading.ShaderFileId == 0)
        {
            shaderBytes = materialBytes;
            characterShader = string.Equals(logicalBundle, CharacterShaderBundle, StringComparison.Ordinal);
        }
        else if (shading.ShaderFileId <= shading.ExternalCabs.Count)
        {
            _shaderBundleBytes ??= _deobfuscate(CharacterShaderBundle);
            // The shader bundle could not be read this time: answer from the material alone, and let
            // the next asker read it again rather than remembering the miss.
            if (_shaderBundleBytes is null) return new MaterialSource(shading, null, false);
            string wantCab = shading.ExternalCabs[shading.ShaderFileId - 1];
            if (string.Equals(_readCab(_shaderBundleBytes), wantCab, StringComparison.Ordinal))
            {
                shaderBytes = _shaderBundleBytes;
                characterShader = true;
            }
        }
        return _sources[key] = new MaterialSource(shading, shaderBytes, characterShader);
    }

    private MaterialRenderEvidence? Derive(string logicalBundle, long pathId)
    {
        var source = LoadMaterial(logicalBundle, pathId);
        if (source is not { CharacterShader: true, ShaderBytes: { } shaderBytes }) return null;
        var shading = source.Shading;
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

        // Every pass the game draws this material through carries the edit, each pass selected against
        // its own compiled axes: a back-face or fringe pass that never compiled one of the material's
        // keywords still draws this material and reads its values.
        if (DrawnVariants(variants, shading.EnabledKeywords) is not { } drawn) return null;
        string? family = MaterialFamilyClassifier.Family(shading.Name);
        var candidates = drawn.Where(variant => variant.MaterialBufferSlot == 2
                && MaterialFamilyClassifier.GameDrawsPass(family, variant.PassName))
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

    /// <summary>The programs the game can bind for this material in every pass. Passes compile different
    /// material axes (an outline with no detail variant still belongs to a detailed material), so each
    /// pass is matched against its own axes; runtime axes stay free. Null when a pass has no program
    /// for the material's keywords — what the game binds there is then unknown.</summary>
    private static List<ShaderVariant>? DrawnVariants(IReadOnlyList<ShaderVariant> variants,
        IReadOnlySet<string> enabledKeywords)
    {
        var selected = new List<ShaderVariant>();
        foreach (var pass in variants.GroupBy(variant => variant.Pass))
        {
            var axes = pass.SelectMany(variant => variant.Keywords)
                .Where(MaterialValueCatalog.MaterialDrivenKeywords.Contains)
                .ToHashSet(StringComparer.Ordinal);
            var want = enabledKeywords.Where(axes.Contains).ToHashSet(StringComparer.Ordinal);
            var candidates = pass.Where(variant => variant.Keywords.Where(axes.Contains)
                .ToHashSet(StringComparer.Ordinal).SetEquals(want)).ToArray();
            if (candidates.Length == 0) return null;
            selected.AddRange(candidates);
        }
        return selected;
    }

    private IReadOnlyList<ShaderVariant>? VariantsOf(byte[] shaderBundleBytes, long shaderPathId)
    {
        var key = (ContentOf(shaderBundleBytes), shaderPathId);
        if (VariantCache.TryGetValue(key, out var cached)) return cached;
        var variants = _readVariants(shaderBundleBytes, shaderPathId);
        if (variants is null) return null;
        return VariantCache.GetOrAdd(key, variants);
    }

    /// <summary>One instance belongs to one immutable install; its character-shader bundle is hashed once
    /// for all of its material objects. Any other bundle reaching here (a material whose shader object is
    /// local to its own bundle) is hashed as itself, never as the memoized character bundle.</summary>
    private ulong ContentOf(byte[] shaderBundleBytes) =>
        ReferenceEquals(shaderBundleBytes, _shaderBundleBytes)
            ? _shaderBundleContent ??= ShaderReflection.Fnv64(shaderBundleBytes)
            : ShaderReflection.Fnv64(shaderBundleBytes);

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
