using System;
using System.Collections.Generic;
using System.IO;

namespace Remold.Core.Migoto;

/// <summary>What a 3DMigoto host's ini tree says about the two things this app has to know before it hands
/// the host a mod or starts it.</summary>
/// <param name="Found">An ini was read at all. False is a loader with no <c>d3dx.ini</c> beside it — an exe
/// that isn't a 3DMigoto loader, or an install missing its configuration.</param>
/// <param name="StartsTheGame">The <c>[Loader]</c> section carries an active <c>launch</c>, so the host
/// starts the game itself once it is up and a second start would bring up a second copy.</param>
/// <param name="HasTextureHook">The configuration carries an active <c>checktextureoverride</c>, which is
/// what makes the game's texture slots checked against the <c>TextureOverride</c> sections a built mod is
/// made of. Without it a mod installs and fires nothing.</param>
public readonly record struct MigotoIniFacts(bool Found, bool StartsTheGame, bool HasTextureHook);

/// <summary>
/// Reading the ini beside a 3DMigoto loader, INCLUDES followed. Distributions split their configuration
/// differently — the Nexus/GIMI build keeps everything in <c>d3dx.ini</c>, an SSMT profile keeps the hook in
/// a <c>Core\GIMI\main.ini</c> the per-game ini pulls in — so a reading that stopped at the first file would
/// answer about half a configuration.
///
/// <para><c>include</c> names one file, relative to the folder of the ini naming it.
/// <c>include_recursive</c> and <c>exclude_recursive</c> name the MODS tree, which is the installed mods
/// rather than the host's own configuration, and are skipped: walking it would read every mod on the
/// machine to answer a question about the host. The walk is depth-capped and cycle-guarded, so a
/// configuration that includes itself is a finite read rather than a hang.</para>
///
/// <para>Pure text rules, with the disk read handed in — the same shape as the rest of Core, and what lets
/// the two questions be pinned against synthetic trees.</para>
/// </summary>
public static class MigotoIni
{
    /// <summary>The loader's own ini, beside its exe.</summary>
    public const string FileName = "d3dx.ini";

    /// <summary>How many includes deep the walk goes. A host's configuration nests a level or two; past
    /// this it is a loop the cycle guard didn't catch or a tree nobody meant to build.</summary>
    public const int MaxDepth = 4;

    /// <summary>The section a host declares its own game launch in.</summary>
    private const string LoaderSection = "Loader";

    /// <summary>The command that makes a texture slot checked against the <c>TextureOverride</c> sections a
    /// built mod is made of. Asked of the WHOLE walked tree, not of one section: the measured hosts put the
    /// command in a plain command list — <c>[CommandListSkin]</c>, <c>[CommandListSkinTexture]</c>,
    /// <c>[CommandListCheck]</c> — that a <c>[ShaderRegex…]</c> section reaches by <c>run =</c>, so a rule
    /// keyed on the section holding the command answers no for every host that ships one. Following the
    /// <c>run =</c> chain would buy nothing: the command lists ARE the host's configuration, and the only
    /// text that could carry the command without being the host's own is the mods tree, which the include
    /// rules already leave out.</summary>
    private const string TextureHookCommand = "checktextureoverride";

    /// <summary>Read the ini tree beside a loader exe. Anything unreadable answers as absent — a host whose
    /// files can't be opened tells this app nothing, and guessing either way would be worse than saying so.</summary>
    public static MigotoIniFacts Read(string? loaderExe)
    {
        if (string.IsNullOrWhiteSpace(loaderExe)) return default;
        try
        {
            var dir = Path.GetDirectoryName(loaderExe.Trim());
            if (string.IsNullOrEmpty(dir)) return default;
            return Parse(Path.Combine(dir, FileName), ReadOrNull);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return default;
        }
    }

    /// <summary>The rules on an ini tree <paramref name="readFile"/> serves by absolute path, answering null
    /// for anything it hasn't got. The root file missing is <see cref="MigotoIniFacts.Found"/> false; an
    /// INCLUDE missing is just a branch with nothing in it — a host that names a file it doesn't ship still
    /// answers for what it does.</summary>
    public static MigotoIniFacts Parse(string rootPath, Func<string, string?> readFile)
    {
        bool found = false, launches = false, hook = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(string path, int depth)
        {
            if (depth > MaxDepth) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            { return; }
            if (!seen.Add(full)) return;   // a configuration that includes itself is read once
            if (readFile(full) is not { } text) return;
            found = true;
            string dir = Path.GetDirectoryName(full) ?? "";
            string section = "";
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;
                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    section = close > 1 ? line[1..close].Trim() : "";
                    continue;
                }
                if (In(section, LoaderSection) && Value(line, "launch") is { Length: > 0 })
                    launches = true;
                if (Command(line, TextureHookCommand))
                    hook = true;
                // include_recursive/exclude_recursive name the mods tree, not the host's configuration
                if (Value(line, "include") is { Length: > 0 } included)
                    Walk(Path.Combine(dir, included.Replace('\\', Path.DirectorySeparatorChar)), depth + 1);
            }
        }

        Walk(rootPath, 0);
        return new MigotoIniFacts(found, launches, hook);
    }

    private static bool In(string section, string name) =>
        string.Equals(section, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>An uncommented <c>key = value</c>'s value, trimmed of an inline comment, or null when the
    /// line is some other key. <c>key</c> must be the WHOLE key: <c>include_recursive</c> is not
    /// <c>include</c>, and reading it as one would walk every installed mod.</summary>
    private static string? Value(string line, string key)
    {
        if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = line[key.Length..].TrimStart();
        if (!rest.StartsWith('=')) return null;
        var value = rest[1..].Trim();
        int comment = value.IndexOf(';');
        if (comment >= 0) value = value[..comment].TrimEnd();
        return value;
    }

    /// <summary>Whether the line invokes a command list's named command. A command stands on its own or
    /// takes arguments after <c>=</c>, so the name has to end at a boundary — otherwise a longer command
    /// starting with the same letters would answer for it.</summary>
    private static bool Command(string line, string name)
    {
        if (!line.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
        if (line.Length == name.Length) return true;
        char next = line[name.Length];
        return next is '=' or ' ' or '\t';
    }

    private static string? ReadOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
