using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Remold.Core.Project;

/// <summary>Clears round-trip content nobody has touched for a while, so the round-trip folders do not grow
/// without bound.
/// <para>A folder is touched when anything in it was written: the newest last-write time of every file and
/// folder inside it, the folder itself included. Starting at each mod's folder, a folder that is stale as a
/// whole goes whole; a folder holding anything fresh stays, and its subfolders are judged one by one. A file
/// is never removed on its own from a folder that stays, because a live round trip can keep an old file
/// beside one saved a moment ago. Loose files directly in the root belong to no mod and are left
/// alone.</para>
/// <para>Links are never followed, neither to judge freshness nor to delete: a link's own time is its age,
/// and a stale link is removed as a link, leaving whatever it points at untouched.</para>
/// <para>What cannot be removed — a file another program holds, a path access is denied to — is logged and
/// left standing, together with the folders holding it, and the next run tries again. The folders it stands
/// in keep the times they had, so the sweep's own deletions never count as a touch.</para></summary>
public static class RoundTripSweep
{
    /// <summary>Remove everything under <paramref name="root"/> not touched within <paramref name="maxAge"/>
    /// of <paramref name="nowUtc"/>. Never throws: every failure goes to <paramref name="log"/>.</summary>
    public static void Run(string root, DateTime nowUtc, TimeSpan maxAge, Action<string, Exception>? log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            var sweep = new Sweeper(nowUtc - maxAge, log);
            var entries = sweep.List(new DirectoryInfo(Path.GetFullPath(root)));
            if (entries is null) return;
            foreach (var mod in entries.OfType<DirectoryInfo>())
                sweep.Visit(sweep.Scan(mod));
        }
        catch (Exception e)
        {
            SafeLog(log, "The round-trip sweep stopped", e);
        }
    }

    // The log is the sweep's only report channel; a logger that itself throws leaves nowhere to report to.
    private static void SafeLog(Action<string, Exception>? log, string what, Exception e)
    {
        try { log?.Invoke(what, e); }
        catch (Exception) { }
    }

    /// <summary>One folder as the scan found it. A link is a leaf: it is never descended into.</summary>
    private sealed class Node(DirectoryInfo dir, bool isLink)
    {
        public DirectoryInfo Dir { get; } = dir;
        public bool IsLink { get; } = isLink;
        public DateTime Written { get; } = dir.LastWriteTimeUtc;
        public DateTime Newest { get; set; } = dir.LastWriteTimeUtc;
        public List<FileInfo> Files { get; } = new();
        public List<Node> Folders { get; } = new();
    }

    private sealed class Sweeper(DateTime cutoff, Action<string, Exception>? log)
    {
        public Node Scan(DirectoryInfo dir)
        {
            var node = new Node(dir, (dir.Attributes & FileAttributes.ReparsePoint) != 0);
            if (node.IsLink) return node;
            var entries = List(dir);
            if (entries is null)
            {
                // Content the sweep cannot see might be fresh, so an unreadable folder counts as touched now
                // and it, and everything holding it, stays.
                node.Newest = DateTime.MaxValue;
                return node;
            }
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    var child = Scan(sub);
                    node.Folders.Add(child);
                    if (child.Newest > node.Newest) node.Newest = child.Newest;
                }
                else if (entry is FileInfo file)
                {
                    node.Files.Add(file);
                    if (file.LastWriteTimeUtc > node.Newest) node.Newest = file.LastWriteTimeUtc;
                }
            }
            return node;
        }

        /// <summary>Remove <paramref name="node"/> whole when it is stale, otherwise judge each of its
        /// subfolders. True when anything was removed from inside it or it was removed itself.</summary>
        public bool Visit(Node node)
        {
            if (node.Newest < cutoff) return Remove(node) != Outcome.Untouched;
            if (node.IsLink) return false;
            bool changed = false;
            foreach (var child in node.Folders)
                changed |= Visit(child);
            if (changed) KeepTime(node);
            return changed;
        }

        private enum Outcome { Untouched, Partly, Gone }

        /// <summary>Delete a stale folder: the files the scan found, then the folders, deepest first. A file
        /// written since the scan, or one that refuses, stays with every folder holding it.</summary>
        private Outcome Remove(Node node)
        {
            if (node.IsLink)
                return DeleteFolder(node.Dir) ? Outcome.Gone : Outcome.Untouched;

            bool all = true, any = false;
            foreach (var file in node.Files)
            {
                bool gone = DeleteFile(file);
                all &= gone;
                any |= gone;
            }
            foreach (var child in node.Folders)
            {
                var outcome = Remove(child);
                all &= outcome == Outcome.Gone;
                any |= outcome != Outcome.Untouched;
            }
            if (all && DeleteFolder(node.Dir)) return Outcome.Gone;
            if (!any) return Outcome.Untouched;
            KeepTime(node);
            return Outcome.Partly;
        }

        private bool DeleteFile(FileInfo file)
        {
            try
            {
                // Written after the scan judged it: somebody is using it after all.
                if (File.GetLastWriteTimeUtc(file.FullName) >= cutoff) return false;
                // A read-only file refuses deletion outright. Not cleared on a file link, where it would reach
                // through to whatever the link names; deleting the link needs no attribute change.
                var attributes = File.GetAttributes(file.FullName);
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                    File.SetAttributes(file.FullName, attributes & ~FileAttributes.ReadOnly);
                File.Delete(file.FullName);
                return true;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return true;   // already gone
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SafeLog(log, $"The round-trip sweep couldn't remove {file.FullName}", e);
                return false;
            }
        }

        // Non-recursive: an emptied folder goes, a folder something new landed in refuses, and a link is
        // removed as a link without touching what it points at.
        private bool DeleteFolder(DirectoryInfo dir)
        {
            try
            {
                Directory.Delete(dir.FullName, recursive: false);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;   // already gone
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SafeLog(log, $"The round-trip sweep couldn't remove {dir.FullName}", e);
                return false;
            }
        }

        /// <summary>Put back the time a folder had before the sweep removed something from it, so the next
        /// run judges it by its content and a folder a refused delete left standing is retried.</summary>
        private void KeepTime(Node node)
        {
            try
            {
                if (Directory.Exists(node.Dir.FullName))
                    Directory.SetLastWriteTimeUtc(node.Dir.FullName, node.Written);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SafeLog(log, $"The round-trip sweep couldn't restore the time of {node.Dir.FullName}", e);
            }
        }

        /// <summary>One folder's entries, hidden and system ones included and links seen rather than skipped,
        /// or null when the folder cannot be read.</summary>
        public List<FileSystemInfo>? List(DirectoryInfo dir)
        {
            try
            {
                return dir.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    AttributesToSkip = 0,
                }).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SafeLog(log, $"The round-trip sweep couldn't read {dir.FullName}", e);
                return null;
            }
        }
    }
}
