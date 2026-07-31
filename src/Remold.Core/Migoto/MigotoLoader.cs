using System;
using System.IO;

namespace Remold.Core.Migoto;

/// <summary>
/// The external 3DMigoto loader — the program that has to be running before the game starts, since it hooks
/// the game process as it comes up and can't attach to one already running.
///
/// <para>It ships in the root of a 3DMigoto install, beside the <c>Mods\</c> folder, under a name that
/// varies by distribution (<c>3DMigoto Loader.exe</c>, <c>Run.exe</c>). The loader exe is the one 3DMigoto
/// path this app is given, so the Mods folder is derived from it rather than asked for twice.</para>
/// </summary>
public static class MigotoLoader
{
    /// <summary>The <c>Mods\</c> folder beside <paramref name="loaderExe"/>, or null when the path has no
    /// directory or no Mods folder sits there (an exe that isn't a 3DMigoto loader).</summary>
    public static string? FindModsFolder(string? loaderExe)
    {
        if (string.IsNullOrWhiteSpace(loaderExe)) return null;
        try
        {
            var dir = Path.GetDirectoryName(loaderExe.Trim());
            if (string.IsNullOrEmpty(dir)) return null;
            var mods = Path.Combine(dir, "Mods");
            return Directory.Exists(mods) ? mods : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
