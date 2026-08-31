using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Remold.Core.Migoto;

/// <summary>
/// Which <c>ps-t</c> registers a character pixel shader can bind each material input at, measured once
/// offline and shipped as data.
///
/// <para><b>Why data and not a constant.</b> The register a slot lands in varies per shader variant and per
/// scene — one part's base colour is at <c>t2</c> in one pass and <c>t3</c> in the next — so no fixed slot
/// is sound and no authored ceiling stays true. The build probes the candidate registers at draw time and
/// accepts only the one answering the tag it expects, which makes a wider candidate list cost emitted lines
/// and nothing else, and a SHORT one cost the bind entirely. The counts are how many variants bind that
/// input at that register; they are carried so a later measurement can be compared against this one rather
/// than merely replacing it.</para>
///
/// <para><b>No guessing.</b> A catalog that is absent or malformed states nothing, and the caller falls back
/// to the range that shipped before the measurement existed rather than inventing one. A catalog measured on
/// ANOTHER game build is still used whole: acceptance is decided by the draw-time tag, so a register the
/// measurement no longer describes binds nothing.</para>
/// </summary>
public sealed class ShaderSlotCatalog
{
    /// <summary>The material inputs a slot set is named by. These are the catalog's own keys, not shader
    /// slot names: <c>BaseMap</c> covers every base-colour slot the resolver classifies as one.</summary>
    public const string BaseMap = "BaseMap", BumpMap = "BumpMap", RMOTex = "RMOTex",
        RampMap = "RampMap", BlendTex = "BlendTex";

    /// <summary>The only validation policy this build implements: a candidate register is accepted only
    /// when the texture bound there answers the <c>filter_index</c> tag the build put on it.</summary>
    public const string TagProbePolicy = "filter_index_tag_probe";

    private readonly IReadOnlyDictionary<string, IReadOnlyList<int>> _slots;

    private ShaderSlotCatalog(string catalogId, string gameBuild, string sourceSha16,
        IReadOnlyDictionary<string, IReadOnlyList<int>> slots)
    {
        CatalogId = catalogId;
        GameBuild = gameBuild;
        SourceSha16 = sourceSha16;
        _slots = slots;
        StockMapSlots = Union(slots, BaseMap, BumpMap, RMOTex, BlendTex);
        RampSlots = Slots(RampMap);
    }

    /// <summary>The catalog's own identity, recorded in a built mod so its coverage is auditable without
    /// the measurement.</summary>
    public string CatalogId { get; }

    /// <summary>The game build the reflection ran over.</summary>
    public string GameBuild { get; }

    /// <summary>The first 16 hex of the reflection input's SHA-256 — the join back to the measurement.</summary>
    public string SourceSha16 { get; }

    /// <summary>Candidate registers for the stock picture-map kinds, ascending: what a draw saves, probes and
    /// restores when a replacement binds donor maps.</summary>
    public IReadOnlyList<int> StockMapSlots { get; }

    /// <summary>Candidate registers the toon ramp binds at, ascending.</summary>
    public IReadOnlyList<int> RampSlots { get; }

    /// <summary>Candidate registers for one input, ascending; empty for an input this catalog does not
    /// name.</summary>
    public IReadOnlyList<int> Slots(string input) =>
        _slots.TryGetValue(input, out var s) ? s : Array.Empty<int>();

    /// <summary>Every measured input row, keyed by the catalog spelling. Exposed read-only so a build plan
    /// can retain property-specific ranges without collapsing them into the fixed stock-map union.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> PropertySlots => _slots;

    /// <summary>The catalog key for one exact Unity shader property. The two base-color spellings are the
    /// one deliberate fold used by the measurement; every other property keeps its exact spelling after the
    /// single leading underscore.</summary>
    public static string CatalogKey(string shaderProperty) => shaderProperty switch
    {
        "_BaseMap" or "_MainTex" => BaseMap,
        "_BumpMap" => BumpMap,
        "_RMOTex" => RMOTex,
        "_RampMap" => RampMap,
        "_BlendTex" => BlendTex,
        { Length: > 1 } when shaderProperty[0] == '_' => shaderProperty[1..],
        _ => shaderProperty,
    };

    /// <summary>Candidate registers for one exact shader property, empty when the measurement names none.</summary>
    public IReadOnlyList<int> SlotsForProperty(string shaderProperty) => Slots(CatalogKey(shaderProperty));

    /// <summary>Read the shipped catalog, or null with <paramref name="problem"/> saying why not. A catalog
    /// that reads cleanly states a sound range, whoever is reading it.</summary>
    public static ShaderSlotCatalog? TryLoad(string path, out string? problem)
    {
        try
        {
            if (!File.Exists(path)) { problem = $"'{Path.GetFileName(path)}' isn't in this install"; return null; }
            return Parse(File.ReadAllText(path), out problem);
        }
        catch (Exception e)
        {
            problem = $"'{Path.GetFileName(path)}' couldn't be read ({e.Message})";
            return null;
        }
    }

    /// <summary>Parse catalog JSON. Same contract as <see cref="TryLoad"/>, for a caller holding the text.</summary>
    public static ShaderSlotCatalog? Parse(string json, out string? problem)
    {
        Stored? file;
        try { file = JsonSerializer.Deserialize<Stored>(json); }
        catch (JsonException e) { problem = $"the shader slot catalog isn't valid JSON ({e.Message})"; return null; }

        if (file is null) { problem = "the shader slot catalog is empty"; return null; }
        if (file.Schema != 1)
        {
            problem = $"the shader slot catalog is schema {file.Schema}, and this build reads schema 1";
            return null;
        }
        if (string.IsNullOrWhiteSpace(file.CatalogId) || string.IsNullOrWhiteSpace(file.GameBuild)
            || string.IsNullOrWhiteSpace(file.SourceSha16))
        {
            problem = "the shader slot catalog names no id, game build or source measurement";
            return null;
        }
        if (!string.Equals(file.ValidationPolicy, TagProbePolicy, StringComparison.Ordinal))
        {
            problem = $"the shader slot catalog asks for validation policy '{file.ValidationPolicy}', "
                    + $"and this build implements '{TagProbePolicy}'";
            return null;
        }
        var slots = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var (input, counts) in file.Inputs ?? new Dictionary<string, Dictionary<string, int>>())
        {
            var registers = new List<int>();
            foreach (var (slot, count) in counts)
            {
                if (!int.TryParse(slot, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int register)
                    || register < 0 || count <= 0)
                {
                    problem = $"the shader slot catalog states '{input}' at register '{slot}' "
                            + $"with {count} variants, which is not a register with evidence behind it";
                    return null;
                }
                registers.Add(register);
            }
            slots[input] = registers.Distinct().OrderBy(x => x).ToList();
        }
        foreach (var required in new[] { BaseMap, BumpMap, RMOTex, RampMap })
            if (!slots.TryGetValue(required, out var s) || s.Count == 0)
            {
                problem = $"the shader slot catalog names no registers for '{required}'";
                return null;
            }

        problem = null;
        return new ShaderSlotCatalog(file.CatalogId!, file.GameBuild!, file.SourceSha16!, slots);
    }

    private static IReadOnlyList<int> Union(IReadOnlyDictionary<string, IReadOnlyList<int>> slots,
        params string[] inputs) =>
        inputs.SelectMany(i => slots.TryGetValue(i, out var s) ? s : Array.Empty<int>())
            .Distinct().OrderBy(x => x).ToList();

    /// <summary>The file as it is stored. Register keys are STRINGS because JSON object keys are — they are
    /// parsed and range-checked rather than trusted.</summary>
    private sealed record Stored(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("catalog_id")] string? CatalogId,
        [property: JsonPropertyName("game_build")] string? GameBuild,
        [property: JsonPropertyName("source_reflection_sha256_16")] string? SourceSha16,
        [property: JsonPropertyName("validation_policy")] string? ValidationPolicy,
        [property: JsonPropertyName("inputs")] Dictionary<string, Dictionary<string, int>>? Inputs);
}
