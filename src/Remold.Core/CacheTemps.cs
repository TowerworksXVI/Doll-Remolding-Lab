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

    /// <summary>The same sweep narrowed to the temps ONE atomically-published file mints: that file's own
    /// path, a dot, 32 hex digits, <c>.tmp</c>. For a cache that shares its folder with other caches —
    /// the index folder holds four — this is the form to use: nothing a user, another cache or another
    /// tool parked beside it is ever a candidate, so no age cutoff is needed to keep a neighbour's
    /// in-flight publish safe.
    ///
    /// <para>Call it only AFTER a successful publish. That is also what makes the window on a racing
    /// writer of the SAME file harmless: its temp is open (undeletable) while being written, and the
    /// moment between its close and its move costs at worst that writer its memo, never this one's.</para>
    ///
    /// <para>Every failure is skipped rather than reported — a leftover temp is inert either way.</para>
    /// </summary>
    internal static void SweepMinted(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) return;
            var prefix = Path.GetFileName(filePath) + ".";
            foreach (var path in Directory.EnumerateFiles(dir, prefix + "*.tmp"))
            {
                // The pattern is a filter, never the test: Windows matches some longer names against
                // "*.tmp", so the name is re-checked here in full before anything is deleted.
                var name = Path.GetFileName(path);
                if (name.Length != prefix.Length + 32 + 4) continue;
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                var middle = name.AsSpan(prefix.Length, 32);
                bool hex = true;
                foreach (var c in middle) if (!Uri.IsHexDigit(c)) { hex = false; break; }
                if (!hex) continue;
                try { File.Delete(path); } catch { /* in use or gone: inert either way */ }
            }
        }
        catch { /* the sweep is housekeeping; it may never cost a memo */ }
    }

    private static readonly ConcurrentDictionary<string, byte> Swept = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="Sweep"/> on the first call for a directory, for the caches held statically: the
    /// residue is another process's, so one pass per run answers it.</summary>
    internal static void SweepOnce(string dir)
    {
        if (Swept.TryAdd(dir, 0)) Sweep(dir);
    }
}
