using System.Collections.Generic;

namespace Remold.Core.Tables;

/// <summary>
/// Curated summon <c>ModelConfigData</c> id → the mesh-name prefix its model ships under. Needed because
/// no <c>c_&lt;stem&gt;_slg_*</c> asset exists for any playable summon stem: the model ships under a
/// separate sub-model family, so the stem the tables name it by is not the name its meshes carry.
///
/// Only VERIFIED links belong here — an entry makes the summon exportable in Pick, and a wrong prefix
/// would export a different model as the summon. Verification = the sub-model's shipped assets tie it to
/// this summon id. Summons whose model ships prefab-style with donor enemy meshes or as bare prop bundles
/// can NOT be expressed as a prefix and are deliberately absent: the donor's stem would export against the
/// ENEMY's bundle, not the summon's copy.
/// </summary>
public static class SummonModels
{
    /// <summary>ModelConfigId → mesh-name prefix (ends before <c>_slg_</c>, so a partless set keeps its one
    /// stable part token — see <see cref="Model.Outfit.MeshPrefixOverride"/>).</summary>
    private static readonly Dictionary<long, string> ByModelConfigId = new()
    {
        [10642] = "c_FlorenceBear_",
        [10721] = "c_Liushih_Horse_",
        [10651] = "c_AlvaSSR01_emitter_",
    };

    /// <summary>The shipped mesh prefix for a summon ModelConfig id, or null when the link isn't
    /// established (the outfit then stays meshless and Pick drops it).</summary>
    public static string? MeshPrefixFor(long modelConfigId) =>
        ByModelConfigId.GetValueOrDefault(modelConfigId);
}
