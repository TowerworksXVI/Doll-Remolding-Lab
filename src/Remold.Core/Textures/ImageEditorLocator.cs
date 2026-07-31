using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Remold.Core.Textures;

/// <summary>
/// Finds an image editor for "Open in editor": manual override → common install dirs (GIMP /
/// paint.net / Krita / Photoshop). Null is a soft state — the caller falls back to the OS default PNG
/// handler.
/// </summary>
public static class ImageEditorLocator
{
    /// <summary>Path to an image-editor exe, or null if none is installed.</summary>
    public static string? Find(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;
        return Detect().FirstOrDefault();
    }

    /// <summary>Every installed known editor exe, preference-ordered and de-duplicated;
    /// <see cref="Find"/> takes the first.</summary>
    public static IReadOnlyList<string> Detect() =>
        ProgramFilesRoots().SelectMany(InstallDirCandidates).Where(File.Exists).Distinct().ToList();

    /// <summary>A short display name for an editor exe, falling back to the file name for a custom
    /// pick.</summary>
    public static string FriendlyName(string exePath)
    {
        var f = Path.GetFileNameWithoutExtension(exePath);
        var lower = f.ToLowerInvariant();
        if (lower.StartsWith("gimp")) return "GIMP";
        if (lower == "paintdotnet") return "Paint.NET";
        if (lower == "krita") return "Krita";
        if (lower == "photoshop") return "Photoshop";
        return f;
    }

    /// <summary>Candidate editor exes under one Program Files root, in preference order: GIMP,
    /// paint.net, Krita, Photoshop. Versioned install dirs are globbed newest-first. Pure filesystem
    /// scan (no registry/env), so it is unit-testable.</summary>
    public static IReadOnlyList<string> InstallDirCandidates(string programFiles)
    {
        var hits = new List<string>();

        // GIMP: "GIMP 2\bin\gimp-2.10.exe" (the exe name carries the version, so glob it)
        foreach (var dir in Globbed(programFiles, "GIMP*"))
        {
            var bin = Path.Combine(dir, "bin");
            if (!Directory.Exists(bin)) continue;
            hits.AddRange(NewestFirst(Directory.GetFiles(bin, "gimp-*.exe")
                .Where(f => Path.GetFileName(f).IndexOf("console", StringComparison.OrdinalIgnoreCase) < 0)));
        }

        Add(hits, Path.Combine(programFiles, "paint.net", "paintdotnet.exe"));

        foreach (var dir in Globbed(programFiles, "Krita*"))
            Add(hits, Path.Combine(dir, "bin", "krita.exe"));

        // Photoshop: "Adobe\Adobe Photoshop 2024\Photoshop.exe"
        var adobe = Path.Combine(programFiles, "Adobe");
        foreach (var dir in Globbed(adobe, "Adobe Photoshop*"))
            Add(hits, Path.Combine(dir, "Photoshop.exe"));

        return hits;
    }

    private static IEnumerable<string> Globbed(string parent, string pattern) =>
        Directory.Exists(parent)
            ? NewestFirst(Directory.GetDirectories(parent, pattern))
            : Enumerable.Empty<string>();

    // ordinal-descending puts "Adobe Photoshop 2024" ahead of "…2023"
    private static IEnumerable<string> NewestFirst(IEnumerable<string> paths) =>
        paths.OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase);

    private static void Add(List<string> hits, string exe)
    {
        if (File.Exists(exe)) hits.Add(exe);
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        foreach (var v in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } root)
                yield return root;
    }
}
