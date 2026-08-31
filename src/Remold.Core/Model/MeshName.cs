using System.Text.RegularExpressions;

namespace Remold.Core.Model;

/// <summary>Parsing for character mesh names of the form
/// <c>c_&lt;stem&gt;_slg_&lt;part&gt;_lod&lt;N&gt;</c> (e.g.
/// <c>c_VesnaSSR0101_slg_P1_body1_lod0</c>).</summary>
public static partial class MeshName
{
    [GeneratedRegex(@"_lodm?\d+", RegexOptions.IgnoreCase)]
    private static partial Regex LodToken();

    [GeneratedRegex(@"_(lodm?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex LodLabel();

    [GeneratedRegex(@"^_lodm?\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex LodTierTail();

    /// <summary>The prefix a mesh name carries in its OWN right, independent of any subject: everything
    /// ahead of the part segment in <c>&lt;head&gt;_&lt;part&gt;_lod&lt;N&gt;</c>, so
    /// <c>c_X_dorm_face_lod0</c> yields <c>c_X_dorm_</c> and <c>cw_X_WL_lod0</c> yields <c>cw_X_</c>. Null
    /// when the name carries no LOD token or nothing ahead of its part segment. Pair it with
    /// <see cref="Part"/> to read a part token off a name whose family is not the subject's own.</summary>
    public static string? OwnPrefix(string meshName)
    {
        var m = LodLabel().Match(meshName);
        if (!m.Success || m.Index == 0) return null;
        var head = meshName[..m.Index];
        int cut = head.LastIndexOf('_');
        return cut <= 0 ? null : head[..(cut + 1)];
    }

    /// <summary>The LOD tier label, lower-cased, or <c>"base"</c> when the name carries no LOD suffix —
    /// the companion of <see cref="Part"/>, which drops it. Detail order: <c>lod0</c> (near, full) &gt;
    /// <c>lodm0</c> (mid) &gt; <c>lod1</c> (far), one tier per bundle.</summary>
    /// <example><c>Lod("c_VesnaSSR0101_slg_cloth2_lodm0")</c> → <c>"lodm0"</c>.</example>
    public static string Lod(string meshName)
    {
        var m = LodLabel().Match(meshName);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : "base";
    }

    /// <summary>The part token: the name minus the outfit prefix and the LOD <em>token</em>, KEEPING any
    /// variant suffix after it. The LOD marker is commonly infixed, not a tail
    /// (<c>cloth1_lod0_Fight</c>, <c>P1_body1_lodm0_Dorm</c>), where <c>_Fight</c>/<c>_Dorm</c> are
    /// distinct garments, not detail levels — so splicing out only <c>_lod&lt;n&gt;</c> keeps
    /// <c>cloth1</c> and <c>cloth1_Fight</c> from collapsing.</summary>
    /// <example><c>Part("c_VesnaSSR0101_slg_P1_body1_lod0", "c_VesnaSSR0101_slg_")</c> → <c>"P1_body1"</c>;
    /// <c>Part("c_VesnaSSR0101_slg_cloth1_lod0_Fight", …)</c> → <c>"cloth1_Fight"</c>.</example>
    public static string Part(string meshName, string meshPrefix)
    {
        var rest = meshName.StartsWith(meshPrefix, System.StringComparison.OrdinalIgnoreCase)
            ? meshName[meshPrefix.Length..]
            : meshName;
        return LodToken().Replace(rest, "").Trim('_');
    }

    /// <summary>Whether <paramref name="meshName"/> is a LOD tier OF <paramref name="part"/>: exactly
    /// <c>&lt;meshPrefix&gt;&lt;part&gt;_lod&lt;n&gt;</c> (or <c>_lodm&lt;n&gt;</c>) with NOTHING after the
    /// digits. The anchor is load-bearing — the LOD marker is commonly INFIXED, so an unanchored prefix
    /// test would let bare <c>cloth2</c> claim <c>c_X_slg_cloth2_lod0_Fight</c>, a different
    /// garment.</summary>
    /// <example><c>IsLodTierOf("c_X_slg_cloth2_lod1", "c_X_slg_", "cloth2")</c> → true;
    /// <c>IsLodTierOf("c_X_slg_cloth2_lod0_Fight", "c_X_slg_", "cloth2")</c> → false.</example>
    public static bool IsLodTierOf(string meshName, string meshPrefix, string part)
    {
        var head = meshPrefix + part;
        return meshName.Length > head.Length
            && meshName.StartsWith(head, System.StringComparison.OrdinalIgnoreCase)
            && LodTierTail().IsMatch(meshName[head.Length..]);
    }

    /// <summary>The outfit-state variant tokens that ride <em>after</em> the LOD token. Game-wide this
    /// set is exactly {Dorm, Fight}; explicit so it can't be confused with a part-name segment.</summary>
    private static readonly string[] Variants = { "Dorm", "Fight" };

    /// <summary>Split a part token into base and trailing outfit-state variant: <c>cloth1_Fight</c> →
    /// <c>("cloth1", "Fight")</c>, <c>cloth1</c> → <c>("cloth1", null)</c>. The base is the stem a variant
    /// shares textures under when it has none of its own.</summary>
    public static (string Base, string? Variant) SplitVariant(string part)
    {
        foreach (var v in Variants)
            if (part.EndsWith("_" + v, System.StringComparison.OrdinalIgnoreCase))
                return (part[..^(v.Length + 1)], part[^v.Length..]);
        return (part, null);
    }

    /// <summary>The outfit-state variant riding a whole MESH NAME after its LOD token, or null for a name
    /// carrying none. Pair it with <see cref="Lod"/>: the two together say which DRAW a mesh belongs to,
    /// and two meshes agreeing on both are the ones that appear in a frame together.</summary>
    /// <example><c>Variant("c_VesnaSSR0101_slg_cloth_lod1_Dorm")</c> → <c>"Dorm"</c>;
    /// <c>Variant("c_VesnaSSR0101_slg_cloth_lod1")</c> → null.</example>
    public static string? Variant(string meshName) =>
        SplitVariant(Part(meshName, OwnPrefix(meshName) ?? "")).Variant;
}
