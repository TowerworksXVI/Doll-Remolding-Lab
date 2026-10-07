using System.Globalization;

namespace Remold.Core.Bundles;

/// <summary>
/// Which Mesh object of a bundle a read takes, beside the mesh's name.
/// <list type="bullet">
/// <item>A <see cref="PathId"/> names one object exactly — the serialized-renderer route, whose bundles
/// ship same-named copies.</item>
/// <item>A <see cref="LoadKey"/> names the object a recipe address loads: the key the owning bundle's
/// <c>m_Container</c> files it under (<see cref="CatalogIndex.LoadKeyForAddress"/>). A bundle can ship
/// several Mesh objects of one name, and only this key says which one the game draws.</item>
/// <item>Neither reads the first Mesh of the given name.</item>
/// </list>
/// A path id converts implicitly, so a caller holding one passes it as it always has.
/// </summary>
public readonly record struct MeshSelector(long PathId, string? LoadKey = null)
{
    public static implicit operator MeshSelector(long pathId) => new(pathId);

    /// <summary>The object a recipe address loads, or a read by name where the catalog states no key.</summary>
    public static MeshSelector ByLoadKey(string? loadKey) =>
        new(0, string.IsNullOrEmpty(loadKey) ? null : loadKey);

    /// <summary>True when this selector names one object by its path id.</summary>
    public bool IsExact => PathId != 0;

    /// <summary>The selector's stable text, for cache keys and file names: the path id in invariant digits,
    /// <c>k</c> and the load key, or <c>0</c> for a read by name — the text a plain path id always gave.</summary>
    public string Token => PathId != 0 ? PathId.ToString(CultureInfo.InvariantCulture)
        : LoadKey is { } key ? "k" + key : "0";

    public override string ToString() => Token;
}
