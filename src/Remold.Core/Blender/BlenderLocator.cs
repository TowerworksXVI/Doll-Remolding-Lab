using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Remold.Core.Blender;

/// <summary>
/// Finds a Blender executable: manual override → PATH → registry → common install dirs (incl. Steam).
/// Only the mesh-edit path needs Blender, so <see cref="Find"/> returning null is a soft state.
/// Presence-only detection — never spawn <c>blender --version</c>, which can block its whole timeout
/// on a cold start.
/// </summary>
public static class BlenderLocator
{
    /// <summary>Path to <c>blender.exe</c>, or null if none found.</summary>
    public static string? Find(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;
        return Candidates().FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> Candidates()
    {
        if (OnPath() is { } p) yield return p;
        foreach (var c in RegistryCandidates()) yield return c;
        foreach (var root in ProgramFilesRoots())
            foreach (var c in InstallDirCandidates(root)) yield return c;
    }

    /// <summary>
    /// Candidate <c>blender.exe</c> paths under one Program Files root: <c>Blender Foundation\Blender*</c>
    /// then Steam, newest-version first. Pure filesystem scan (no registry/env) so it is unit-testable
    /// against a synthetic tree.
    /// </summary>
    public static IReadOnlyList<string> InstallDirCandidates(string programFiles)
    {
        var foundation = new List<string>();
        var bf = Path.Combine(programFiles, "Blender Foundation");
        if (Directory.Exists(bf))
            foreach (var d in Directory.GetDirectories(bf, "Blender*"))
            {
                var exe = Path.Combine(d, "blender.exe");
                if (File.Exists(exe)) foundation.Add(exe);
            }
        foundation.Sort((a, b) =>
            CompareNewestFirst(Path.GetDirectoryName(a) ?? a, Path.GetDirectoryName(b) ?? b));

        var hits = new List<string>(foundation);
        var steam = Path.Combine(programFiles, "Steam", "steamapps", "common", "Blender", "blender.exe");
        if (File.Exists(steam)) hits.Add(steam);   // Steam install is the lowest-priority fallback
        return hits;
    }

    /// <summary>
    /// Registry version subkeys in the same newest-first, component-wise numeric order as the
    /// Program Files scan. Registry enumeration order is unspecified, so selecting its first hit
    /// without this normalization can prefer Blender 4.1 over an installed 4.3.
    /// </summary>
    internal static IReadOnlyList<string> RegistrySubkeysNewestFirst(IEnumerable<string> subkeys)
    {
        var ordered = subkeys.ToList();
        ordered.Sort(CompareNewestFirst);
        return ordered;
    }

    /// <summary>Newest-first ordering for two <c>Blender Foundation</c> install directories, COMPONENT-WISE
    /// numeric so 4.10 sorts ahead of 4.9. A directory whose name carries no trailing version sorts after
    /// every one that does — an unversioned or beta folder is no evidence of being the newest — and equal
    /// versions fall back to the name, so the order is total and the scan is deterministic.</summary>
    private static int CompareNewestFirst(string dirA, string dirB)
    {
        var a = TrailingVersion(dirA);
        var b = TrailingVersion(dirB);
        if (a is null || b is null)
            return a is null && b is null ? string.CompareOrdinal(dirB, dirA) : a is null ? 1 : -1;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return b[i].CompareTo(a[i]);
        return a.Length != b.Length ? b.Length.CompareTo(a.Length) : string.CompareOrdinal(dirB, dirA);
    }

    /// <summary>The dot-separated integers a directory name ends with ("Blender 4.10" → 4, 10), or null when
    /// it ends in anything else. A component too large for an <c>int</c> reads as no version rather than
    /// throwing.</summary>
    private static int[]? TrailingVersion(string dir)
    {
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        int i = name.Length;
        while (i > 0 && (char.IsAsciiDigit(name[i - 1]) || name[i - 1] == '.')) i--;
        var tail = name[i..].Trim('.');
        if (tail.Length == 0) return null;
        var parts = tail.Split('.');
        var nums = new int[parts.Length];
        for (int k = 0; k < parts.Length; k++)
            if (!int.TryParse(parts[k], out nums[k])) return null;
        return nums;
    }

    private static IEnumerable<string> ProgramFilesRoots()
    {
        foreach (var v in new[] { "ProgramFiles", "ProgramFiles(x86)" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } root)
                yield return root;
    }

    private static string? OnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator)
            .Where(d => d.Length > 0)
            .Select(d => Path.Combine(d, "blender.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> RegistryCandidates()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = hive.OpenSubKey(@"SOFTWARE\BlenderFoundation");
            if (key is null) continue;
            foreach (var ver in RegistrySubkeysNewestFirst(key.GetSubKeyNames()))
            {
                using var vk = key.OpenSubKey(ver);
                if (vk?.GetValue("InstallDir") is string inst)
                {
                    var exe = Path.Combine(inst, "blender.exe");
                    if (File.Exists(exe)) yield return exe;
                }
            }
        }
    }
}
