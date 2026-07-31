using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Remold.Core.Project;

/// <summary>
/// Dropping a built mod folder into a 3DMigoto <c>Mods\</c> folder, and the conflict read that runs first.
///
/// <para>Two mods that override the same hashes fight over the draw, and which one wins is not something
/// this app decides. So the overlap is REPORTED by folder name and the installing user chooses; nothing
/// already in the Mods folder is ever deleted to make room, except the folder this build replaces and an
/// older version of this same mod the installer confirmed.</para>
///
/// <para><b>Mid-failure story:</b> the copy lands in a staging folder beside the destination; the folder it
/// replaces is RENAMED aside rather than deleted, and only once the staging folder has taken its place is
/// the sideline dropped. A throw at any point after the aside-rename puts the sideline back. The Mods folder
/// therefore holds either the previous install or the new one, never neither.</para>
/// </summary>
public static class ModInstall
{
    /// <summary>The sidecar the build writes beside <c>mod.ini</c>, and the only file this reads.</summary>
    public const string SidecarName = "gf2mod.json";

    /// <summary>Staging folder prefix — a leading dot keeps it out of an alphabetical mod list, and it is
    /// deleted on the way out whether the install succeeded or threw.</summary>
    private const string StagingPrefix = ".remold-installing-";

    /// <summary>Sideline folder prefix: the folder being replaced lives here for the length of the swap.</summary>
    private const string SidelinePrefix = ".remold-replaced-";

    /// <summary>What an install left behind. <paramref name="LeftBehind"/> is the sideline folder the swap
    /// could not drop — the previous install's files, which 3DMigoto would load alongside the new ones.
    /// Null on the ordinary path.</summary>
    public readonly record struct Outcome(string InstalledDir, string? LeftBehind);

    /// <summary>An install that failed, carrying the sentence describing what the Mods folder now holds.
    /// Only <see cref="FolderUntouched"/> may claim nothing changed.</summary>
    public sealed class InstallFailedException : Exception
    {
        public InstallFailedException(string message, string folderState, Exception? inner = null)
            : base(message, inner) => FolderState = folderState;

        /// <summary>One sentence naming the state the Mods folder is in.</summary>
        public string FolderState { get; }

        public const string FolderUntouched = "The Mods folder is unchanged.";
        public const string PreviousRestored = "The previous install is back in place.";
    }

    /// <summary>What one folder in the Mods tree is to this build: an unrelated mod on the same hashes, or an
    /// older version of this same mod. <paramref name="Folder"/> is relative to the Mods root, so a nested
    /// mod reads as the path it sits at.</summary>
    public sealed record InstallScan(IReadOnlyList<string> Conflicts, IReadOnlyList<string> PriorVersions);

    /// <summary>The <c>override_hashes</c> a sidecar's JSON text records, lower-cased. An unparseable or
    /// field-less sidecar yields NOTHING rather than a guess: a mod whose hashes can't be read is reported
    /// as no overlap, and the install still shows the folder it is replacing.</summary>
    public static IReadOnlyList<string> OverrideHashes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("override_hashes", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            var list = new List<string>();
            foreach (var e in arr.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } h)
                    list.Add(h.ToLowerInvariant());
            return list;
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    /// <summary>A sidecar's mod identity — <c>name</c> and <c>author</c>, trimmed. Either half may be empty;
    /// a sidecar with no name at all yields null, and nothing matches it.</summary>
    public static (string Name, string Author)? Identity(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var name = Text(doc.RootElement, "name");
            if (name.Length == 0) return null;
            return (name, Text(doc.RootElement, "author"));
        }
        catch (JsonException) { return null; }

        static string Text(JsonElement root, string field) =>
            root.TryGetProperty(field, out var e) && e.ValueKind == JsonValueKind.String
                ? (e.GetString() ?? "").Trim()
                : "";
    }

    /// <summary>Two sidecars describe the same mod — the test that recognises an older version of this build
    /// under a different folder name. Case-insensitive on both halves, because neither is a filesystem
    /// token.</summary>
    public static bool SameMod((string Name, string Author)? a, (string Name, string Author)? b) =>
        a is { } x && b is { } y
        && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.Author, y.Author, StringComparison.OrdinalIgnoreCase);

    /// <summary>The installed folders whose hashes overlap <paramref name="ours"/>, in the order given.
    /// PURE — the disk read is <see cref="ScanConflicts"/>. A folder with no hashes on record never
    /// overlaps.</summary>
    public static IReadOnlyList<string> Overlapping(IReadOnlyList<string> ours,
        IEnumerable<(string Folder, IReadOnlyList<string> Hashes)> installed)
    {
        var mine = new HashSet<string>(ours.Select(h => h.ToLowerInvariant()), StringComparer.Ordinal);
        if (mine.Count == 0) return Array.Empty<string>();
        return installed
            .Where(x => x.Hashes.Any(h => mine.Contains(h.ToLowerInvariant())))
            .Select(x => x.Folder)
            .ToList();
    }

    /// <summary>What the Mods tree at <paramref name="modsRoot"/> already holds against the build at
    /// <paramref name="builtDir"/>: unrelated mods on the same hashes, and older versions of this same mod
    /// under their own folder names. 3DMigoto loads <c>Mods\</c> recursively, so this walks it to any depth;
    /// an unreadable folder is skipped, never guessed at.
    ///
    /// <para>The folder this install would REPLACE (same name) is neither — replacing it is the point.</para></summary>
    public static InstallScan ScanConflicts(string builtDir, string modsRoot)
    {
        var ours = OverrideHashes(ReadSidecar(builtDir));
        var ourIdentity = Identity(ReadSidecar(builtDir));
        if (!Directory.Exists(modsRoot)) return new InstallScan(Array.Empty<string>(), Array.Empty<string>());
        string ownName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(builtDir)));

        var candidates = new List<(string Folder, IReadOnlyList<string> Hashes)>();
        var priorVersions = new List<string>();
        foreach (var dir in InstalledModFolders(modsRoot))
        {
            string rel = Path.GetRelativePath(modsRoot, dir);
            if (string.Equals(rel, ownName, StringComparison.OrdinalIgnoreCase)) continue;
            var sidecar = ReadSidecar(dir);
            if (SameMod(ourIdentity, Identity(sidecar))) { priorVersions.Add(rel); continue; }
            candidates.Add((rel, OverrideHashes(sidecar)));
        }
        return new InstallScan(Overlapping(ours, candidates), priorVersions);
    }

    /// <summary>Every folder under <paramref name="modsRoot"/> carrying a sidecar, in path order. A mod folder
    /// is not descended into — the files under it belong to that mod, not to a nested one. The staging and
    /// sideline folders of an install in flight are skipped at any depth.</summary>
    private static IEnumerable<string> InstalledModFolders(string modsRoot)
    {
        var found = new List<string>();
        Walk(modsRoot, 0);
        return found;

        void Walk(string dir, int depth)
        {
            if (depth > 8) return;   // a mods tree this deep is not a layout, it is a loop
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            foreach (var sub in subs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith(StagingPrefix, StringComparison.Ordinal)
                    || name.StartsWith(SidelinePrefix, StringComparison.Ordinal)) continue;
                if (File.Exists(Path.Combine(sub, SidecarName))) found.Add(sub);
                else Walk(sub, depth + 1);
            }
        }
    }

    /// <summary>The confirm body naming the folders already on these hashes. One sentence of state, one of
    /// consequence — the choice itself is the dialog's two buttons.</summary>
    public static string ConflictBody(IReadOnlyList<string> folders)
    {
        string verb = folders.Count == 1 ? "touches" : "touch";
        return $"{NameList(folders)} {verb} the same character. Two mods on the same hashes fight over the draw.";
    }

    /// <summary>The confirm title for an older version of this same mod under its own folder name.</summary>
    public static string PriorVersionTitle(IReadOnlyList<string> folders) =>
        $"Replace the installed {NameList(folders)}?";

    /// <summary>The confirm body for an older version of this same mod. It is removed only AFTER the new
    /// folder is in place, so a failed install never costs the modder the copy they had.</summary>
    public static string PriorVersionBody(IReadOnlyList<string> folders) => folders.Count == 1
        ? "Same mod, older version. It is removed after the new one installs."
        : "Same mod, older versions. They are removed after the new one installs.";

    /// <summary>"a", "a and b", "a, b and c" — a readable list inside one sentence.</summary>
    private static string NameList(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
    };

    /// <summary>Copy <paramref name="builtDir"/> into <paramref name="modsRoot"/> under its own folder name,
    /// replacing a same-named folder. The replaced folder is renamed aside for the length of the swap and
    /// restored if anything throws, so the Mods folder always holds one complete copy of the mod.</summary>
    public static Outcome Install(string builtDir, string modsRoot)
    {
        if (!Directory.Exists(builtDir))
            throw new DirectoryNotFoundException($"the built mod folder is gone: {builtDir}");
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(builtDir)));
        string dest = Path.Combine(modsRoot, name);
        string staging = Path.Combine(modsRoot, StagingPrefix + name);
        string sideline = Path.Combine(modsRoot, SidelinePrefix + name);
        Directory.CreateDirectory(modsRoot);
        bool sidelined = false;
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            if (Directory.Exists(sideline)) Directory.Delete(sideline, recursive: true);
            CopyTree(builtDir, staging);
            if (Directory.Exists(dest))
            {
                Directory.Move(dest, sideline);
                sidelined = true;
            }
            Directory.Move(staging, dest);
        }
        catch (Exception e)
        {
            if (!sidelined) throw new InstallFailedException(e.Message,
                InstallFailedException.FolderUntouched, e);
            try
            {
                if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                Directory.Move(sideline, dest);
            }
            catch
            {
                throw new InstallFailedException(e.Message,
                    $"The previous install is in {SidelinePrefix + name}. Move it back by hand.", e);
            }
            throw new InstallFailedException(e.Message, InstallFailedException.PreviousRestored, e);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
        // The sideline was renamed a moment ago, so nothing was holding it then. If something does now, the
        // new mod is installed and the old copy is still loadable — that has to be said, not swallowed.
        if (!sidelined) return new Outcome(dest, null);
        try { Directory.Delete(sideline, recursive: true); }
        catch (Exception) { return new Outcome(dest, SidelinePrefix + name); }
        return new Outcome(dest, null);
    }

    /// <summary>Remove an installed mod folder named relative to <paramref name="modsRoot"/> — the older
    /// version of this same mod, once the new one is in place. Refuses anything outside the Mods root.</summary>
    public static void RemoveInstalled(string modsRoot, string relativeFolder)
    {
        string root = Path.GetFullPath(modsRoot);
        string target = Path.GetFullPath(Path.Combine(root, relativeFolder));
        if (target.Length <= root.Length
            || !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{relativeFolder}' is not inside the Mods folder");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    /// <summary>A folder's sidecar text, or null when it has none or can't be read.</summary>
    private static string? ReadSidecar(string dir)
    {
        try
        {
            var file = Path.Combine(dir, SidecarName);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void CopyTree(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(src, file)), overwrite: true);
    }
}
