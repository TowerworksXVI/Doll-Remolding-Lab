using System;
using System.Security.Cryptography;

namespace Remold.Core.Bundles;

/// <summary>
/// The <c>"sha256:&lt;lowercase hex&gt;"</c> content-hash format used for asset identity/staleness.
/// Scope for a game asset: the serialized type-tree object bytes
/// (<c>GetBaseField(...).WriteToByteArray()</c>), NOT the bundle file or the exported glb/png.
/// </summary>
public static class AssetHash
{
    /// <summary>Hash arbitrary bytes to the <c>sha256:&lt;hex&gt;</c> form.</summary>
    public static string Sha256(byte[] bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
