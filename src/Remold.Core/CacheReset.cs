using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.Core.Export;
using Remold.Core.Project;

namespace Remold.Core;

/// <summary>
/// The force-rescan sweep: drop everything the app can rebuild from the game and the modder's own files,
/// and nothing else. A regular rescan re-reads the install but keeps every disk cache, so a fingerprint or
/// snapshot written under a degraded session outlives it — this is the route that clears those.
/// <para>Two kinds of residue, both regenerable: the derived-cache trees under the cache root
/// (<see cref="LabPaths.DerivedCacheFolders"/>), and the combined-rig fingerprint sidecars living beside the
/// glbs inside mod projects. DURABLE state is never named here — settings, the first-run record, project
/// manifests, workspace glbs and textures, and the shipped sharing seed all stand.</para>
/// <para>Every removal is per-item best-effort: a file another process holds is skipped and the sweep
/// carries on. A cache that survives a sweep is a slower next open, never a wrong one — every survivor is
/// content-keyed or fingerprint-compared before it is served, so a stale one answers for nothing — and a
/// partial sweep therefore needs no rollback and reports nothing.</para>
/// <para>A REPARSE POINT (junction, symlink, mount point) is never walked through: a folder inside a cache
/// tree can point anywhere on the machine, and following one would take the sweep out of the roots named
/// here entirely. A directory link found inside a swept tree is removed AS A LINK — the thing it points at
/// is not the app's — and the walk stops at it.</para>
/// </summary>
public static class CacheReset
{
    /// <summary>The combined-rig cache key's filename: the sidecar that was written beside
    /// <see cref="AssetExporter.CombinedGlbName"/>, still derived from that name rather than spelled again.
    ///
    /// <para>NOTHING MINTS ONE ANY MORE — the combined-rig cache this keyed is gone, and a session builds its
    /// composition fresh into its own run folder every time — so the sweep below is here only to clear the
    /// sidecars earlier releases left behind in mod folders that are still on disk.</para></summary>
    public static readonly string CombinedFingerprintName =
        Path.ChangeExtension(AssetExporter.CombinedGlbName, ".fingerprint")!;

    /// <summary>Delete the regenerable cache trees under <paramref name="cacheRoot"/>. The root itself and
    /// anything beside the trees (the opt-in launch-timing log) stay.</summary>
    public static void ClearDerivedCaches(string cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot)) return;
        foreach (var folder in LabPaths.DerivedCacheFolders)
            DeleteTree(Path.Combine(cacheRoot, folder));
    }

    /// <summary>Delete every combined-rig fingerprint sidecar under these project folders, and only those:
    /// the name is matched exactly, so the <c>.glb</c> the sidecar describes — and every other file the
    /// modder owns — is left where it is. Returns how many were removed.</summary>
    public static int ClearCombinedFingerprints(IEnumerable<string> projectRoots)
    {
        int removed = 0;
        foreach (var root in projectRoots ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            foreach (var file in SafeFind(root, CombinedFingerprintName))
            {
                // The wildcard-free pattern can still be answered by a short (8.3) name, so the real
                // filename is what decides — nothing outside the app's own sidecar name is deletable here.
                if (!string.Equals(Path.GetFileName(file), CombinedFingerprintName, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { File.Delete(file); removed++; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // held open or unreadable — nothing reads one any more, so a surviving sidecar costs
                    // the modder a few hundred bytes of disk and nothing else
                }
            }
        }
        return removed;
    }

    /// <summary>The mod projects a sweep may reach into: every folder directly under
    /// <paramref name="libraryRoot"/> holding a project manifest, plus each recent-mod entry that does — a
    /// project opened from outside the library carries the app's sidecars just the same. A path that isn't a
    /// project folder is not returned, so nothing outside the app's own workspaces is ever enumerated.
    /// De-duplicated by full path.</summary>
    public static IReadOnlyList<string> ProjectRoots(string? libraryRoot, IEnumerable<string>? recentPaths)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Offer(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            string full;
            // GetFullPath KEEPS a trailing separator, so the same folder offered as "X" and as "X\" — the
            // library enumeration and a recents entry rarely spell it the same way — would otherwise be two
            // roots and two walks of one project.
            try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return; }
            if (!File.Exists(ModProject.ManifestPathFor(full))) return;
            if (seen.Add(full)) found.Add(full);
        }

        if (!string.IsNullOrWhiteSpace(libraryRoot))
        {
            string[] children;
            try { children = Directory.Exists(libraryRoot) ? Directory.GetDirectories(libraryRoot) : Array.Empty<string>(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { children = Array.Empty<string>(); }
            foreach (var c in children) Offer(c);
        }

        foreach (var p in recentPaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            // Recents record the project FOLDER, but the open route also accepts the manifest file itself.
            bool isFile;
            try { isFile = File.Exists(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { isFile = false; }
            Offer(isFile ? Path.GetDirectoryName(p) : p);
        }
        return found;
    }

    /// <summary>Every file under <paramref name="root"/> named <paramref name="fileName"/>, snapshotted so a
    /// deletion can't disturb the walk, and empty rather than throwing when the tree can't be read. A
    /// project folder is the modder's own, so a junction inside it can point at anything they own — the
    /// walk stops at reparse points rather than deleting a sidecar somewhere else entirely.</summary>
    private static IReadOnlyList<string> SafeFind(string root, string fileName)
    {
        try
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();
            return Directory.EnumerateFiles(root, fileName, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,       // one unreadable subfolder must not end the walk
                // NOT the default (Hidden|System): a sidecar the modder marked hidden is still the app's
                // to rebuild, and skipping a hidden folder would leave a whole subtree's sidecars standing.
                // ReparsePoint is what must be skipped — it is the only attribute that moves the walk
                // outside the project root.
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchCasing = MatchCasing.CaseInsensitive,
            }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>Remove a cache tree, item by item. Shared by the force-rescan sweep and bounded derived
    /// caches when they evict one of their own entry directories. <see cref="Directory.Delete(string, bool)"/> gives up at
    /// the FIRST file it can't remove and leaves the rest of the tree standing, which is the opposite of what
    /// a sweep owes: here every item is tried on its own and only the ones still holding something a lock
    /// saved survive.
    /// <para>The descent is EXPLICIT, one directory at a time, rather than a single recursive enumeration:
    /// a recursive walk is materialized as a whole, so one unreadable spot part-way through it (a junction
    /// loop, a folder that vanished mid-walk) throws before any deletion is attempted and abandons the whole
    /// tree. Here a failure costs the directory it happened in and nothing else.</para>
    /// <para>Reparse points are never descended into — see the type remarks — and a directory link found on
    /// the way is removed with a NON-recursive delete, which unlinks it and leaves whatever it pointed at
    /// alone.</para></summary>
    internal static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        // The root itself can be a link — sweep the link, never the target behind it.
        if (TryUnlinkDirectory(dir)) return;

        var toRemove = new List<string> { dir };   // directories in discovery order: parents before children
        for (int i = 0; i < toRemove.Count; i++)
        {
            foreach (var entry in Snapshot(toRemove[i]))
            {
                try
                {
                    if (entry is DirectoryInfo)
                    {
                        if (!TryUnlinkDirectory(entry.FullName)) toRemove.Add(entry.FullName);
                        continue;
                    }
                    // A read-only file refuses File.Delete outright, and one of them keeps its whole folder
                    // standing. The bit is the modder's mark on a file the app wrote, not a reason to keep
                    // it. Not cleared on a file LINK: that would reach through to whatever it names, and
                    // deleting the link needs no attribute change anyway.
                    if ((entry.Attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint))
                        == FileAttributes.ReadOnly)
                        entry.Attributes &= ~FileAttributes.ReadOnly;
                    entry.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* held — leave it */ }
            }
        }

        // Deepest first: a child is always discovered after its parent, so reversing the discovery order
        // empties every folder before the one holding it, and a non-recursive delete then fails only on the
        // folders that really still hold something.
        for (int i = toRemove.Count - 1; i >= 0; i--)
        {
            try { Directory.Delete(toRemove[i], recursive: false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>One directory's entries, materialized so a deletion can't disturb the walk and so a failure
    /// mid-enumeration costs this directory alone. Empty rather than throwing.</summary>
    private static IReadOnlyList<FileSystemInfo> Snapshot(string dir)
    {
        try
        {
            // AttributesToSkip = none: hidden and system entries are swept like any other (a hidden
            // desktop.ini otherwise keeps its whole folder standing), and reparse points have to be SEEN
            // here so the link itself can be removed — skipping them would leave the link behind.
            return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Array.Empty<FileSystemInfo>(); }
    }

    /// <summary>True when <paramref name="dir"/> is a reparse point — in which case it has been unlinked (or
    /// the attempt failed and it stands), and either way the caller must NOT descend into it. A non-recursive
    /// <see cref="Directory.Delete(string, bool)"/> on a link removes the link and never its target.
    /// <para>An unreadable attribute answers TRUE: refusing to descend costs a surviving cache, while
    /// guessing "ordinary folder" at a link is how a sweep leaves its own roots.</para></summary>
    private static bool TryUnlinkDirectory(string dir)
    {
        try
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        try { Directory.Delete(dir, recursive: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }
}
