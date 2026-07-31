using System;
using System.Security.Cryptography;
using System.Text;

namespace Remold.Core.Workbench;

/// <summary>
/// The one-way key a persisted measurement stores INSTEAD of a game-derived string. Deterministic across
/// machines and runs, so a file written on one install joins to the roster another install derives for
/// itself.
///
/// <para>64 bits of SHA-256, hex: wide enough that a roster-sized key set has no realistic collision, and a
/// loader that finds two roster subjects on one key refuses the whole file rather than attaching one
/// subject's measurement to another.</para>
/// </summary>
public static class NameKey
{
    /// <summary>The key for one string. Callers normalize case themselves — the key is over exactly the
    /// bytes given, so <c>"A"</c> and <c>"a"</c> are different keys.</summary>
    public static string Of(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
