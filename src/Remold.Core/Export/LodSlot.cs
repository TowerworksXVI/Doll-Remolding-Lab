using System.Text.Json.Serialization;

namespace Remold.Core.Export;

/// <summary>One additional LOD-tier slot (<c>lodm0</c>, <c>lod1</c>) the part's single edited
/// high-detail <c>.glb</c> fans out into at build time. Each tier is a distinct original in its OWN
/// <see cref="Bundle"/> but shares the part's one replacement file.</summary>
public sealed class LodSlot
{
    [JsonPropertyName("object_name")] public string ObjectName { get; set; } = "";
    [JsonPropertyName("bundle")] public string Bundle { get; set; } = "";

    /// <summary>Exact selector for THIS tier's object, set on smr-body tiers (enemy bundles ship
    /// same-named copies). Null on recipe-backed tiers, where the name selects.</summary>
    [JsonPropertyName("path_id")]
    [JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public long? PathId { get; set; }

    public LodSlot() { }
    public LodSlot(string objectName, string bundle)
    {
        ObjectName = objectName;
        Bundle = bundle;
    }
}
