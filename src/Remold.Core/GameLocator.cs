using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Remold.Core;

/// <summary>
/// Locates the GF2 <c>AssetBundles_Windows</c> directory without assuming Steam: GF2 ships both via
/// Steam and standalone, so auto-detect tries every Steam library (registry + <c>libraryfolders.vdf</c>)
/// then common standalone roots. A candidate is accepted only with the GF2 sentinels — a current
/// catalog plus the game's VFS manifest — so a look-alike cache is refused with a reason.
/// </summary>
public static class GameLocator
{
    private const string GameFolder = "GIRLS' FRONTLINE 2 EXILIUM";
    private static readonly string Rel =
        Path.Combine("GF2_Exilium_Data", "LocalCache", "Data", "AssetBundles_Windows");

    /// <summary>The game's VFS manifest filename (<c>md5("AssetBundleFileSystem")</c>): a fixed name
    /// every GF2 install ships and no other game's cache has, so it anchors the accept-test.</summary>
    private const string VfsManifestFile = "08dfe7d89b6fe56375d6dfec87ffcc8a.bundle";

    private static readonly Regex LibPath = new("\"path\"\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>
    /// Resolve <paramref name="path"/> to the game root, or null. Accepts the bundle dir, a game root
    /// containing it, or a Steam <c>common</c> dir. The shared accept-test for the override, a
    /// remembered path, a detected path and a manual pick alike; callers owing the user a reason use
    /// <see cref="ValidateDetailed"/>.
    /// </summary>
    public static string? Validate(string? path) => ValidateDetailed(path).Dir;

    /// <summary>The game's root folder (holding <c>GF2_Exilium.exe</c>) for a resolved
    /// <c>AssetBundles_Windows</c> dir — the inverse of the <see cref="Rel"/> descent — or null when the
    /// path isn't the standard layout.</summary>
    public static string? GameRootOf(string? assetBundlesDir)
    {
        if (string.IsNullOrWhiteSpace(assetBundlesDir)) return null;
        var full = Path.GetFullPath(assetBundlesDir.TrimEnd(Path.DirectorySeparatorChar));
        if (!full.EndsWith(Path.DirectorySeparatorChar + Rel, StringComparison.OrdinalIgnoreCase)) return null;
        // ABW → Data → LocalCache → GF2_Exilium_Data → game root
        return new DirectoryInfo(full).Parent?.Parent?.Parent?.Parent?.FullName;
    }

    /// <summary>The <c>AssetBundles_Windows</c> dir for a game root (the <see cref="Rel"/> descent). The
    /// root is the one path stored and passed around; every bundle/table path derives from it.</summary>
    public static string BundleDirOf(string gameRoot) => Path.Combine(gameRoot, Rel);

    /// <summary>Resolve a path (a game root, the bundle dir, or a Steam <c>common</c> dir) to the game
    /// <b>root</b> — the one canonical location. <c>Problem</c> says why the best candidate failed when none
    /// passes ("best" = the first existing, correctly named dir).</summary>
    public static (string? Dir, string? Problem) ValidateDetailed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, "No folder given.");
        string? problem = null;
        foreach (var c in new[] { path, Path.Combine(path, Rel), Path.Combine(path, GameFolder, Rel) })
        {
            if (!Directory.Exists(c)) continue;
            var full = Path.GetFullPath(c.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.Equals(Path.GetFileName(full), "AssetBundles_Windows", StringComparison.Ordinal))
                continue;
            if (SentinelProblem(full) is { } p) { problem ??= p; continue; }
            if (GameRootOf(full) is { } root) return (root, null);
            problem ??= "This isn't a standard GF2 install layout.";
        }
        return (null, problem ??
            "No AssetBundles_Windows folder found here. Pick the game's install folder or its bundle cache.");
    }

    /// <summary>Null when <paramref name="dir"/> carries the GF2 sentinels, else why it doesn't:
    /// EXISTENCE only of a current catalog (<c>catalog_main*</c>) and the VFS manifest. Neither file's
    /// contents may be opened — the running game holds the manifest with a deny-read share mode, and the
    /// app must recognise its own install whether or not the game is up (<see cref="GameFilesInUse"/>
    /// distinguishes that case).</summary>
    private static string? SentinelProblem(string dir)
    {
        try
        {
            using var catalogs = Directory.EnumerateFiles(dir, "catalog_main*").GetEnumerator();
            // The cache is filled on the game's first run, so an install that has never launched reaches
            // here with the folder tree in place and no catalog in it. Both remedies are named.
            if (!catalogs.MoveNext())
                return "No GF2 catalog here (catalog_main…), so this isn't the game's bundle cache. "
                    + "Pick the game's install folder. Launch the game once if it has never run on this machine.";
            if (!File.Exists(Path.Combine(dir, VfsManifestFile)))
                return "This folder is missing the game's file manifest, so it isn't a GF2 install (or it's an incomplete copy).";
            return null;
        }
        catch (IOException) { return "The folder couldn't be read (a lock or permissions)."; }
        catch (UnauthorizedAccessException) { return "The folder couldn't be read (permissions)."; }
    }

    /// <summary>True when the game holds its files open — it opens the VFS manifest with a deny-read
    /// share mode, so a read fails with a sharing violation. <see cref="Validate"/> still accepts the
    /// folder; a front-end pairs this with a "game is running" warning. A missing manifest, a clean
    /// open, or a permissions fault all return false.</summary>
    public static bool GameFilesInUse(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return false;
        var manifest = Path.Combine(BundleDirOf(gameRoot), VfsManifestFile);
        if (!File.Exists(manifest)) return false;
        try { using var _ = File.OpenRead(manifest); return false; }
        catch (IOException) { return true; }                    // opened by the running game (deny-read share mode)
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Best-effort auto-detect → the game root, or null: every Steam library first, then common
    /// standalone roots. Overrides and remembered paths are the caller's concern.</summary>
    public static string? Find()
    {
        foreach (var common in SteamCommonDirs())
            if (Validate(Path.Combine(common, GameFolder)) is { } v) return v;
        foreach (var root in StandaloneRoots())
            if (Validate(Path.Combine(root, GameFolder)) is { } v) return v;
        return null;
    }

    /// <summary>The library paths inside a <c>libraryfolders.vdf</c> body. VDF escapes path separators
    /// as <c>\\</c>, so they are un-escaped to real paths.</summary>
    public static IEnumerable<string> ParseLibraryPaths(string? vdfText)
    {
        foreach (Match m in LibPath.Matches(vdfText ?? ""))
            yield return m.Groups[1].Value.Replace("\\\\", "\\");
    }

    /// <summary>
    /// Expand Steam install roots into their <c>steamapps/common</c> dirs: the root's own plus every
    /// library named in its <c>libraryfolders.vdf</c>. <paramref name="readVdf"/> returns a vdf body or
    /// null, injected so file I/O can be faked in tests. Roots de-duplicate case-insensitively.
    /// </summary>
    public static IReadOnlyList<string> SteamCommonDirsFrom(
        IEnumerable<string> steamRoots, Func<string, string?> readVdf)
    {
        var outp = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steam in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(steam)) continue;
            var root = Path.GetFullPath(steam.TrimEnd(Path.DirectorySeparatorChar));
            if (!seen.Add(root)) continue;
            outp.Add(Path.Combine(root, "steamapps", "common"));
            if (readVdf(Path.Combine(root, "steamapps", "libraryfolders.vdf")) is not { } vdf) continue;
            foreach (var lib in ParseLibraryPaths(vdf))
            {
                if (string.IsNullOrWhiteSpace(lib)) continue;
                var libRoot = Path.GetFullPath(lib.TrimEnd(Path.DirectorySeparatorChar));
                outp.Add(Path.Combine(libRoot, "steamapps", "common"));
            }
        }
        return outp;
    }

    /// <summary>Every Steam library's <c>steamapps/common</c>, from the registry + each library's vdf.</summary>
    public static IReadOnlyList<string> SteamCommonDirs() =>
        SteamCommonDirsFrom(SteamRootsFromRegistry(), ReadFileOrNull);

    private static string? ReadFileOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> SteamRootsFromRegistry()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var (hive, key, val) in new[]
        {
            (Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            (Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            string? root = null;
            try
            {
                using var k = hive.OpenSubKey(key);
                if (k?.GetValue(val) is string s && !string.IsNullOrWhiteSpace(s)) root = s;
            }
            catch (Exception) { /* registry unreadable → skip this source */ }
            if (root is not null) yield return root;
        }
    }

    private static IEnumerable<string> StandaloneRoots()
    {
        foreach (var v in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } r) yield return r;
        yield return @"C:\Games";
        yield return @"D:\Games";
        yield return @"E:\Games";
        yield return @"C:\";
        yield return @"D:\";
    }
}
