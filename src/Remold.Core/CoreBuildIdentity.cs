using System;
using System.IO;
using System.Security;
using System.Security.Cryptography;

namespace Remold.Core;

/// <summary>The short identity of the exact Core binary serving this process.</summary>
internal static class CoreBuildIdentity
{
    private const int ShortHashBytes = 6;
    private static readonly Lazy<string> CachedShortHash = new(ComputeShortHash);

    internal static string ShortHash => CachedShortHash.Value;

    private static string ComputeShortHash()
    {
        var assembly = typeof(CoreBuildIdentity).Assembly;
        try
        {
            string location = assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                using var stream = File.OpenRead(location);
                byte[] hash = SHA256.HashData(stream);
                return Convert.ToHexString(hash.AsSpan(0, ShortHashBytes)).ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or NotSupportedException
                                   or ArgumentException
                                   or SecurityException)
        {
            // A non-file-backed or unreadable assembly still has a deterministic module identity.
        }

        return assembly.ManifestModule.ModuleVersionId.ToString("N")[..(ShortHashBytes * 2)];
    }
}
