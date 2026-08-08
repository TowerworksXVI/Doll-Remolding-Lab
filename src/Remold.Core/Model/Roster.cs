using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Model;

/// <summary>The one (character, stem) → <see cref="Outfit"/> route, for every pane that holds a stored
/// selection-ledger entry rather than a live row. Case-insensitive on both halves, because neither the
/// internal name nor the stem is a filesystem token and a stored entry's casing need not match the
/// roster's.</summary>
public static class RosterLookup
{
    /// <summary>The roster's outfit for <paramref name="character"/>/<paramref name="stem"/>; null when
    /// either half is unknown. Callers decide what an unknown pair means.</summary>
    public static Outfit? FindOutfit(IReadOnlyList<Character> roster, string character, string stem) =>
        roster.FirstOrDefault(c => string.Equals(c.Name, character, StringComparison.OrdinalIgnoreCase))
              ?.Outfits.FirstOrDefault(o => string.Equals(o.Stem, stem, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A playable character, from <c>GunCharacterData</c>.</summary>
public sealed record Character(
    long CharId,
    string Name,
    string Family,
    long GunId,
    long DormModelConfigId,
    List<Outfit> Outfits)
{
    /// <summary>The localized player-facing name (<c>GunCharacterData #2.1</c> → LangPackage), distinct
    /// from the internal <see cref="Name"/>. Null when unnamed or unresolved, and the UI falls back to
    /// <see cref="Name"/> — which stays the stable grouping key mesh stems match on.</summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// A curated subject's own route to its assembly prefab, for a skin the design DB does not surface and
/// whose prefab the stem-address formula (<see cref="Bundles.GameVfs.PrefabAddress"/>) therefore cannot
/// reach. Exactly one of two shapes, per <see cref="Addressable"/> / <see cref="DirectBundle"/>:
///
/// <list type="bullet">
///   <item><b>Addressable</b> — an explicit catalog address. Resolves exactly like a formula hit: the
///     owning bundle plus that row's full dependency closure, so materials and textures resolve
///     normally.</item>
///   <item><b>Direct bundle</b> — a logical bundle id plus the container ROOT the prefab ships under,
///     for a prefab the catalog addresses under no name this app can derive. The catalog offers no
///     dependency list for a bare bundle, so the scope is that bundle plus whatever
///     <see cref="ExtraBundles"/> names: anything the prefab references through an external CAB outside
///     that set resolves to nothing and surfaces as a loud per-part problem (see
///     <see cref="Workbench.SubjectModelBuilder"/>), never as a blank.</item>
/// </list>
///
/// <para>Null on every conventional outfit, which keeps the stem formula. A route is never a blacklist
/// bypass: <see cref="Bundles.RosterBlacklist"/> is tested on the STEM before any route is read.</para>
/// </summary>
public sealed record SubjectRoute
{
    private SubjectRoute(string? address, string? bundle, string rootName, IReadOnlyList<string>? extraBundles)
    {
        Address = address;
        Bundle = bundle;
        RootName = rootName;
        ExtraBundles = extraBundles ?? System.Array.Empty<string>();
    }

    /// <summary>The catalog address to resolve; null on a direct-bundle route.</summary>
    public string? Address { get; }

    /// <summary>The LOGICAL bundle id to read (the catalog's own identity, so it ends <c>.bundle</c>);
    /// null on an addressable route.</summary>
    public string? Bundle { get; }

    /// <summary>
    /// The container root to parse — ALWAYS set, on either shape. One bundle routinely holds several
    /// roots: a shipped example pairs a prefab with a prop-less variant of itself under a sibling name,
    /// and "the first root that parses" picks the variant, which is a different model with a different
    /// (here: empty) mesh recipe. The formula path can afford that default because its address and its
    /// root agree by construction; a curated route cannot, so it names the root outright.
    /// </summary>
    public string RootName { get; }

    /// <summary>
    /// The logical bundles this subject's prefab reaches through EXTERNAL references — its materials and
    /// their textures — standing in for the dependency closure a bare bundle has none of. Empty on an
    /// addressable route, whose catalog row carries a real closure.
    ///
    /// <para>Curated data, so it is exactly as complete as it was measured to be: a reference outside the
    /// set resolves to nothing and is reported per part, never guessed at.</para>
    /// </summary>
    public IReadOnlyList<string> ExtraBundles { get; }

    /// <summary>Route by explicit catalog address, parsing <paramref name="rootName"/> inside the bundle
    /// that address resolves to.</summary>
    public static SubjectRoute Addressable(string address, string rootName) =>
        string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(rootName)
            ? throw new System.ArgumentException(
                "a curated address route needs an address AND a root name", nameof(address))
            : new SubjectRoute(address, null, rootName, null);

    /// <summary>Route straight to a logical bundle and the container root inside it, with the
    /// material/texture bundles that bundle's prefab references (<see cref="ExtraBundles"/>).</summary>
    public static SubjectRoute DirectBundle(string bundle, string rootName,
        params string[] extraBundles) =>
        string.IsNullOrWhiteSpace(bundle) || string.IsNullOrWhiteSpace(rootName)
            ? throw new System.ArgumentException(
                "a curated bundle route needs a bundle AND a root name", nameof(bundle))
            : new SubjectRoute(null, bundle, rootName, extraBundles);
}

/// <summary>An outfit/model variant of a character. <see cref="Stem"/> is the model stem from
/// <c>ModelConfigData</c> (e.g. <c>VesnaSSR0101</c>); the runtime model name is <c>"c_" + Stem</c>.
/// </summary>
public sealed record Outfit(
    long ModelConfigId,
    string Stem,
    OutfitKind Kind)
{
    /// <summary>The mesh-name prefix shared by every part: <c>c_&lt;stem&gt;_slg_</c>, unless
    /// <see cref="MeshPrefixOverride"/> points at a different shipped model family (summons, whose DB
    /// stem names no mesh — see <see cref="Tables.SummonModels"/>). An override ends <em>before</em>
    /// <c>_slg_</c>, so a partless set (<c>c_&lt;stem&gt;_slg_lod0</c>) yields one stable part token
    /// (<see cref="Workbench.SubjectModelBuilder.PartlessToken"/>) rather than a per-LOD pseudo-part.</summary>
    public string MeshPrefix => MeshPrefixOverride ?? $"c_{Stem}_slg_";

    /// <summary>Non-null only when the outfit's meshes ship under a different naming family than
    /// <c>c_&lt;Stem&gt;_slg_</c> (curated summon models). Null for every conventional outfit.</summary>
    public string? MeshPrefixOverride { get; init; }

    /// <summary>Non-null only on a CURATED subject whose prefab the stem-address formula can't reach
    /// (see <see cref="SubjectRoute"/> and <see cref="Tables.CuratedSkins"/>). Null for every outfit the
    /// design DB names, which resolves by formula. Riding on the outfit is what lets every consumer —
    /// the launch existence gate, the confirm-fill, the workbench and the exporter — take the same route
    /// without each learning the curated table.</summary>
    public SubjectRoute? Route { get; init; }

    /// <summary>The localized outfit name (model stem → <c>ClothesData #25/#8</c> → <c>#9.1</c> →
    /// LangPackage). Null when the stem has no ClothesData row (enemies/props/dorm variants are correctly
    /// nameless) or none was resolved, and the UI labels off the <see cref="Stem"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The weapon-tab rarity (3=R, 4=SR, 5=SSR), shown as the row's tier badge. Null on
    /// every non-weapon outfit, which carries no rarity.</summary>
    public int? WeaponRarity { get; init; }

    /// <summary>True on weapon-family subjects, whose parts are separate game objects with no co-draw
    /// guarantee: every part pools alone, and no cross-part coverage group forms
    /// (<see cref="Migoto.PoolDerive.PoolCandidates"/>). False on every outfit whose parts dress one
    /// body.</summary>
    public bool PartsPoolAlone { get; init; }
}

public enum OutfitKind
{
    /// <summary>Base combat outfit: ModelConfigId == GunId.</summary>
    Base,
    /// <summary>Alternate skin: ModelConfigId == GunId*100 + NN.</summary>
    Alt,
    /// <summary>Dorm outfit: ModelConfigId == GunId*100 + 99.</summary>
    Dorm,
    /// <summary>Battle-summon model: a ModelConfig row <c>BattleSummonedData</c> summons, owned by the
    /// character whose base stem its own stem is prefixed with. Its id follows no scheme.</summary>
    Summon,
    /// <summary>Recognized model row that doesn't fit a known id scheme.</summary>
    Other,
}
