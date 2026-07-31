using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Remold.Core;

/// <summary>
/// Resolves how to launch the game from its install root. Pure path/text logic (no process spawn) so it
/// stays testable and the Core stays free of side effects — the caller does the actual process start and
/// owns its own error surface.
///
/// <para>A Steam-library install must launch through Steam (<c>steam://rungameid/&lt;appid&gt;</c>): starting
/// the exe directly makes Steam relaunch it, a visible double-launch. The appid is derived from the install
/// itself — never guessed — by matching the game folder name against the <c>installdir</c> in a sibling
/// <c>steamapps/appmanifest_*.acf</c>. A standalone install, or a Steam-shaped one with no matching manifest,
/// launches the exe directly.</para>
/// </summary>
public static class GameLauncher
{
    /// <summary>The game exe for an install root: Unity's <c>&lt;Name&gt;_Data ↔ &lt;Name&gt;.exe</c>
    /// convention, falling back to the first exe in the root that is neither a crash handler nor a dump
    /// tool. Null when the root doesn't exist or holds no candidate.</summary>
    public static string? FindExe(string? gameRoot)
    {
        if (string.IsNullOrWhiteSpace(gameRoot)) return null;
        try
        {
            var root = new DirectoryInfo(gameRoot);
            if (!root.Exists) return null;
            // EVERY *_Data dir, not just the first: a root carrying more than one (a shipped tool beside
            // the game) is named by whichever pair is complete, so a toolless _Data can't shadow it.
            foreach (var data in root.EnumerateDirectories("*_Data"))
            {
                string cand = Path.Combine(root.FullName, data.Name[..^"_Data".Length] + ".exe");
                if (File.Exists(cand)) return cand;
            }
            // No <Name>_Data ↔ <Name>.exe pair to name the game: the crash handler and the dump analyzer
            // are the exes a Unity root reliably carries besides the game, so skipping both leaves the
            // game's own.
            return root.EnumerateFiles("*.exe").Select(f => f.FullName)
                .FirstOrDefault(f => !Path.GetFileName(f).Contains("crash", StringComparison.OrdinalIgnoreCase)
                                  && !Path.GetFileName(f).Contains("dump", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>How a caller should start the game.</summary>
    public enum LaunchKind
    {
        /// <summary>Start the exe path directly (standalone install, or a Steam-shaped one with no matching manifest).</summary>
        DirectExe,
        /// <summary>Start the <c>steam://rungameid/&lt;appid&gt;</c> URI so Steam owns the single launch.</summary>
        Steam,
    }

    /// <summary>A resolved launch: the <see cref="Target"/> to hand the process start (a steam:// URI or an
    /// exe path, both driven with <c>UseShellExecute = true</c>), and an optional <see cref="Note"/> the
    /// caller surfaces when a Steam-shaped install fell back to the exe because no appmanifest matched — a
    /// fallback, not a failure.
    ///
    /// <para><see cref="Exe"/> is the game executable this plan resolved to, carried even when the target is
    /// a steam:// URI: through Steam the started process is the client, so the game's own process is only
    /// findable by the exe's name. One resolve answers both, so the name a caller watches cannot describe a
    /// different exe than the one being launched.</para></summary>
    public readonly record struct LaunchPlan(LaunchKind Kind, string Target, string? Note, string Exe)
    {
        public bool ViaSteam => Kind == LaunchKind.Steam;

        /// <summary>The process name the game runs under — the exe's stem, which is what the process table
        /// carries.</summary>
        public string ProcessName => Path.GetFileNameWithoutExtension(Exe);
    }

    /// <summary>Resolve the launch plan for an install root, or null when no game exe is found (the caller
    /// reports "couldn't find the executable"). Steam-library install with a matching appmanifest → the
    /// rungameid URI; otherwise the exe directly, with a fallback note when the install looked Steam-shaped
    /// but no manifest named it.</summary>
    public static LaunchPlan? Resolve(string? gameRoot)
    {
        if (FindExe(gameRoot) is not { } exe) return null;
        var root = new DirectoryInfo(Path.GetDirectoryName(exe)!);
        if (SteamShape(root) is not { } shape)
            return new LaunchPlan(LaunchKind.DirectExe, exe, null, exe);   // standalone install: exe directly, no note

        if (SteamAppId(shape.SteamApps, shape.GameFolderName) is { } appid)
            return new LaunchPlan(LaunchKind.Steam, $"steam://rungameid/{appid}", null, exe);

        // Steam-shaped but no appmanifest names this folder: launch the exe (Steam will still relaunch it
        // once, but a direct launch is the honest fallback) and say so rather than fail.
        return new LaunchPlan(LaunchKind.DirectExe, exe,
            "Launched the game directly. This is a Steam install but no appmanifest names its folder, so the " +
            "double-launch fix could not apply.", exe);
    }

    // ---- Steam layout / appid ---------------------------------------------

    /// <summary>When <paramref name="root"/> is <c>…/steamapps/common/&lt;folder&gt;</c>, the <c>steamapps</c>
    /// dir plus the game folder name; else null (a standalone install).</summary>
    private static (DirectoryInfo SteamApps, string GameFolderName)? SteamShape(DirectoryInfo root)
    {
        var common = root.Parent;
        var steamApps = common?.Parent;
        if (common is null || steamApps is null) return null;
        if (!string.Equals(common.Name, "common", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(steamApps.Name, "steamapps", StringComparison.OrdinalIgnoreCase)) return null;
        return (steamApps, root.Name);
    }

    /// <summary>The appid whose <c>appmanifest_*.acf</c> in <paramref name="steamApps"/> has an
    /// <c>installdir</c> equal to <paramref name="gameFolderName"/>, or null when none matches (the caller
    /// falls back to the exe). Reads the acf files; parsing/matching is the pure, tested part below.</summary>
    private static string? SteamAppId(DirectoryInfo steamApps, string gameFolderName)
    {
        try
        {
            var manifests = steamApps.EnumerateFiles("appmanifest_*.acf")
                .Select(f => ReadOrNull(f.FullName))
                .Where(t => t is not null)
                .Select(t => ParseAppManifest(t!));
            return MatchAppId(gameFolderName, manifests);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string? ReadOrNull(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static readonly Regex AppIdKey = new("\"appid\"\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex InstallDirKey = new("\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Pull <c>appid</c> and <c>installdir</c> out of an <c>appmanifest_*.acf</c> body. Both are
    /// unique top-level keys in the VDF, so a plain key/value match reads them without a full VDF parse.</summary>
    public static (string? AppId, string? InstallDir) ParseAppManifest(string acfText)
    {
        var appid = AppIdKey.Match(acfText ?? "");
        var installdir = InstallDirKey.Match(acfText ?? "");
        return (appid.Success ? appid.Groups[1].Value : null,
                installdir.Success ? installdir.Groups[1].Value : null);
    }

    /// <summary>The appid of the first manifest whose <c>installdir</c> equals <paramref name="gameFolderName"/>
    /// (case-insensitive, the filesystem's rule on Windows), or null.</summary>
    public static string? MatchAppId(string gameFolderName, IEnumerable<(string? AppId, string? InstallDir)> manifests)
    {
        foreach (var (appId, installDir) in manifests)
            if (appId is { Length: > 0 } && installDir is { Length: > 0 } &&
                string.Equals(installDir, gameFolderName, StringComparison.OrdinalIgnoreCase))
                return appId;
        return null;
    }
}
