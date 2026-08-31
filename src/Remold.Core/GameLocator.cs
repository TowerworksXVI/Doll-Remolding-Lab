using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Remold.Core;

/// <summary>
/// Locates the GF2 <c>AssetBundles_Windows</c> directory without assuming Steam: GF2 ships both via
/// Steam and standalone, so auto-detect tries every Steam library (registry + <c>libraryfolders.vdf</c>),
/// then the standalone launcher's registry traces — resolved through the launcher's own
/// <c>config.ini</c>, since the game folder is freely named and movable — then a bounded sweep of
/// common roots for the launcher's default <c>GF2 Game</c> layout. A candidate is accepted only with
/// the GF2 sentinels — a current catalog plus the game's VFS manifest — so a look-alike cache is
/// refused with a reason.
/// </summary>
public static class GameLocator
{
    private const string GameFolder = "GIRLS' FRONTLINE 2 EXILIUM";

    /// <summary>The standalone launcher's default game folder name, inside the launcher's own directory.
    /// A default only — the user can name and place the game folder freely; the launcher's
    /// <c>config.ini</c> records where it really is.</summary>
    private const string StandaloneGameFolder = "GF2 Game";

    /// <summary>The launcher's config file, beside its exe. Its <c>game_install_path</c> line is the
    /// authority on where the game folder is.</summary>
    private const string LauncherConfigFile = "config.ini";
    private static readonly string Rel =
        Path.Combine("GF2_Exilium_Data", "LocalCache", "Data", "AssetBundles_Windows");

    /// <summary>The game's VFS manifest filename.</summary>
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

    /// <summary>Resolve a path (a game root, the bundle dir, a Steam <c>common</c> dir, or a standalone
    /// launcher directory holding the game in its <c>GF2 Game</c> subfolder) to the game <b>root</b> — the
    /// one canonical location. <c>Problem</c> says why the best candidate failed when none passes
    /// ("best" = the first existing, correctly named dir).</summary>
    public static (string? Dir, string? Problem) ValidateDetailed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, "No folder given.");
        string? problem = null;
        foreach (var c in new[]
        {
            path, Path.Combine(path, Rel),
            Path.Combine(path, GameFolder, Rel), Path.Combine(path, StandaloneGameFolder, Rel),
        })
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
            "No AssetBundles_Windows folder found here. Pick the game's install folder.");
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
                return "This folder doesn't have the game's files yet. Pick the game's install folder, "
                    + "and launch the game once if it has never run on this machine.";
            if (!File.Exists(Path.Combine(dir, VfsManifestFile)))
                return "This folder is missing some of the game's files, so it isn't a GF2 install (or it's an incomplete copy).";
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

    /// <summary>Best-effort auto-detect → the game root, or null. Order: every Steam library; the
    /// standalone launcher's registry traces (each resolved directly or through its <c>config.ini</c>);
    /// then a bounded one-level sweep of common roots for the launcher's default <c>GF2 Game</c> layout —
    /// the backup for a launcher that left no usable trace. The sentinel accept-test guards every
    /// candidate, so neither the loose registry match nor the sweep can accept a look-alike. Overrides
    /// and remembered paths are the caller's concern.</summary>
    public static string? Find()
    {
        foreach (var common in SteamCommonDirs())
            if (Validate(Path.Combine(common, GameFolder)) is { } v) return v;
        foreach (var cand in RegistryCandidates())
            if (ValidateLauncherCandidate(cand) is { } v) return v;
        foreach (var root in StandaloneRoots())
        {
            if (Validate(root) is { } v) return v;
            foreach (var cand in StandaloneGameDirsUnder(root))
                if (Validate(cand) is { } c) return c;
        }
        return null;
    }

    /// <summary>Resolve a directory that may be the game root, a launcher directory holding the game
    /// under its default name, or a launcher directory whose <c>config.ini</c> says where the game
    /// really is — the game folder is freely named and movable, so the config redirect is the
    /// authoritative route when the direct resolve misses.</summary>
    public static string? ValidateLauncherCandidate(string? dir)
    {
        if (Validate(dir) is { } v) return v;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        string? ini;
        try
        {
            var p = Path.Combine(dir, LauncherConfigFile);
            // A launcher config is tiny; a huge same-named file is not it, and is not worth reading.
            ini = File.Exists(p) && new FileInfo(p).Length <= 1_000_000 ? File.ReadAllText(p) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { ini = null; }
        return ini is null ? null : Validate(GameInstallPathFrom(ini));
    }

    /// <summary>The <c>game_install_path</c> value in a launcher <c>config.ini</c> body, or null.
    /// Line-oriented key=value; the key is matched case-insensitively and the value is trimmed of
    /// whitespace and quotes.</summary>
    public static string? GameInstallPathFrom(string? iniText)
    {
        const string key = "game_install_path";
        foreach (var raw in (iniText ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line[key.Length..].TrimStart();
            if (rest.Length == 0 || rest[0] != '=') continue;   // a longer key that merely starts the same
            var val = rest[1..].Trim().Trim('"');
            if (val.Length > 0) return val;
        }
        return null;
    }

    /// <summary>Candidate launcher/install directories the registry records, matched loosely because
    /// names vary by channel and region: GF2-named vendor keys (the launcher's own, holding path values
    /// like <c>InstPath</c>) and GF2-named uninstall entries (path/location values plus the
    /// <c>UninstallString</c>'s directory). A hit only nominates a candidate for the accept-test.</summary>
    private static IEnumerable<string> RegistryCandidates()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var (hive, key) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node"),
            (Registry.CurrentUser, @"Software"),
        })
            foreach (var dir in VendorKeyPathValues(hive, key))
                yield return dir;
        foreach (var (hive, key) in new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
            foreach (var dir in UninstallEntryPaths(hive, key))
                yield return dir;
    }

    /// <summary>Every path-named string value under GF2-named subkeys of <paramref name="keyPath"/> —
    /// the launcher registers itself as a vendor key (e.g. <c>WOW6432Node\GF2Exilium</c> with
    /// <c>InstPath</c>), and the value name is matched loosely for the same channel-variance reason as
    /// the key name. Only key names are enumerated broadly; values are read from matched keys alone.</summary>
    private static IEnumerable<string> VendorKeyPathValues(RegistryKey hive, string keyPath)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var found = new List<string>();
        try
        {
            using var k = hive.OpenSubKey(keyPath);
            if (k is not null)
                foreach (var name in k.GetSubKeyNames())
                {
                    if (!LooksLikeGf2(name)) continue;
                    using var sub = k.OpenSubKey(name);
                    if (sub is null) continue;
                    foreach (var vn in sub.GetValueNames())
                        if (vn.Contains("path", StringComparison.OrdinalIgnoreCase)
                            && sub.GetValue(vn) is string s && !string.IsNullOrWhiteSpace(s))
                            found.Add(s);
                }
        }
        catch (Exception) { /* registry unreadable → no candidates from this hive */ }
        foreach (var f in found) yield return f;
    }

    /// <summary>Directory candidates from GF2-named uninstall entries under <paramref name="keyPath"/>:
    /// path/location string values, plus the directory of the <c>UninstallString</c>'s executable — the
    /// uninstaller lives in the launcher folder, which is a candidate even when no location value is
    /// written.</summary>
    private static IEnumerable<string> UninstallEntryPaths(RegistryKey hive, string keyPath)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var found = new List<string>();
        try
        {
            using var k = hive.OpenSubKey(keyPath);
            if (k is not null)
                foreach (var name in k.GetSubKeyNames())
                {
                    using var e = k.OpenSubKey(name);
                    if (e is null) continue;
                    if (!LooksLikeGf2(name) && !LooksLikeGf2(e.GetValue("DisplayName") as string)) continue;
                    foreach (var vn in e.GetValueNames())
                        if ((vn.Contains("path", StringComparison.OrdinalIgnoreCase)
                             || vn.Contains("location", StringComparison.OrdinalIgnoreCase))
                            && e.GetValue(vn) is string s && !string.IsNullOrWhiteSpace(s))
                            found.Add(s);
                    if (ExeDirFromCommand(e.GetValue("UninstallString") as string) is { } ud)
                        found.Add(ud);
                }
        }
        catch (Exception) { /* registry unreadable → no candidates from this hive */ }
        foreach (var f in found) yield return f;
    }

    /// <summary>The directory of the executable a command line names — a quoted command up to its closing
    /// quote, an unquoted one up to the end of its <c>.exe</c> token (so an unquoted launcher path with
    /// spaces still parses; the recorded string is the uninstaller's path, e.g.
    /// <c>&lt;launcher&gt;\uninst.exe</c>), falling back to the first space — or null when nothing with a
    /// directory parses out. A misparse only costs a failed candidate.</summary>
    public static string? ExeDirFromCommand(string? command)
    {
        var c = command?.Trim();
        if (string.IsNullOrEmpty(c)) return null;
        string exe;
        if (c[0] == '"')
        {
            var close = c.IndexOf('"', 1);
            exe = close > 1 ? c[1..close] : c[1..];
        }
        else
        {
            var exeEnd = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            var sp = c.IndexOf(' ');
            exe = exeEnd > 0 ? c[..(exeEnd + ".exe".Length)] : sp > 0 ? c[..sp] : c;
        }
        try { return Path.GetDirectoryName(exe) is { Length: > 0 } d ? d : null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Whether a registry key name or display name reads as this game. Loose on purpose —
    /// channel and region namings vary — since a hit only nominates a candidate for the accept-test.</summary>
    public static bool LooksLikeGf2(string? name) =>
        name is { Length: > 0 } &&
        (name.Contains("frontline 2", StringComparison.OrdinalIgnoreCase)
         || name.Contains("exilium", StringComparison.OrdinalIgnoreCase)
         || name.Contains("gf2", StringComparison.OrdinalIgnoreCase));

    /// <summary>The most children examined per root by <see cref="StandaloneGameDirsUnder"/> — a hard
    /// bound so the backup sweep stays a handful of existence probes and can never grow into a crawl.</summary>
    private const int SweepChildCap = 512;

    /// <summary>The <c>GF2 Game</c> dirs one level under <paramref name="root"/> — the launcher's default
    /// layout under a directory whose own name only a sweep can find. One existence probe per child,
    /// capped at <see cref="SweepChildCap"/> children; an unreadable or missing root yields nothing.</summary>
    public static IEnumerable<string> StandaloneGameDirsUnder(string root)
    {
        string[] children;
        try { children = Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        var examined = 0;
        foreach (var child in children)
        {
            if (++examined > SweepChildCap) yield break;
            var cand = Path.Combine(child, StandaloneGameFolder);
            if (Directory.Exists(cand)) yield return cand;
        }
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
        foreach (var drive in FixedDriveRoots())
        {
            yield return Path.Combine(drive, "Games");
            yield return drive;
        }
    }

    /// <summary>Every ready fixed drive's root (<c>C:\</c>, <c>D:\</c>, …), so an install on any local
    /// disk is reachable; removable and network drives are not scan targets.</summary>
    private static IEnumerable<string> FixedDriveRoots()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var d in drives)
        {
            bool fixedReady;
            try { fixedReady = d.DriveType == DriveType.Fixed && d.IsReady; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (fixedReady) yield return d.RootDirectory.FullName;
        }
    }
}
