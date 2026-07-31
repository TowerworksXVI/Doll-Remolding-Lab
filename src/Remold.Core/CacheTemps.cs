using System;
using System.Collections.Concurrent;
using System.IO;

namespace Remold.Core;

/// <summary>
/// The orphan-temp sweep every on-disk cache that publishes atomically needs. Publishing means writing a
/// unique <c>*.tmp</c> beside the target and moving it over, so a process killed between the two leaves the
/// temp behind forever — nothing else ever names it again.
/// </summary>
internal static class CacheTemps
{
    /// <summary>How old a temp must be before a sweep may remove it. A publish takes far less than this, so
    /// only residue outlives it.</summary>
    internal static readonly TimeSpan OrphanTempAge = TimeSpan.FromMinutes(1);

    /// <summary>Best-effort removal of orphaned atomic-publish temps (<c>*.tmp</c>) in a cache directory.
    /// Only temps older than <see cref="OrphanTempAge"/> are removed, so a concurrent worker's in-flight temp
    /// is never deleted out from under its own <c>File.Move</c>.</summary>
    internal static void Sweep(string dir)
    {
        try
        {
            var cutoff = DateTime.UtcNow - OrphanTempAge;
            foreach (var f in Directory.EnumerateFiles(dir, "*.tmp"))
            {
                try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                catch { /* another worker owns it, or it vanished — best-effort */ }
            }
        }
        catch { /* enumerate failed (dir vanished) — best-effort */ }
    }

    private static readonly ConcurrentDictionary<string, byte> Swept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="Sweep"/> on the first call for a directory, for the caches held statically: the
    /// residue is another process's, so one pass per run answers it.</summary>
    internal static void SweepOnce(string dir)
    {
        if (Swept.TryAdd(dir, 0)) Sweep(dir);
    }
}
