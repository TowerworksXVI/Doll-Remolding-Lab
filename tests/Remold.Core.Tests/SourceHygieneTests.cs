using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// Repo-level invariants about the SOURCE itself rather than about what it computes. They exist because a
/// source file can be wrong in a way no amount of behavioural coverage sees: the code compiles, every test
/// is green, and the damage is to the tools that read the repo.
/// </summary>
public class SourceHygieneTests
{
    /// <summary>The repo root: the folder holding <c>Remold.slnx</c>, walked up from the test assembly.
    /// Asserted rather than skipped-on-miss — a run that cannot find the source is a coverage lie, not a
    /// pass. Shared with the other source-reading pins, so there is one answer to where the source is.</summary>
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Remold.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null,
            "no Remold.slnx above " + AppContext.BaseDirectory + " — the test layout moved");
        return dir!.FullName;
    }

    /// <summary>A raw NUL byte in a .cs file makes the WHOLE file binary to every tool that reads the repo
    /// as text: grep stops reporting matches in it, a diff shows "Binary files differ", and git skips the
    /// end-of-line normalization it applies to text — which is how one file in this repo came to be
    /// committed with line endings none of its siblings have. The compiler is perfectly happy either way,
    /// so nothing else would ever catch it. A NUL that a string constant genuinely needs is spelled
    /// <c>"\u0000"</c> (see <c>BundleReads</c>'s absent-catalog marker), which is the same string to the
    /// compiler and plain text on disk.</summary>
    [Fact]
    public void No_source_file_carries_a_raw_nul_byte()
    {
        var offenders = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs",
                     SearchOption.AllDirectories))
        {
            // build output is not source: it holds generated files nobody edits
            var rel = Path.GetRelativePath(RepoRoot(), path);
            if (rel.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal)
                || rel.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                continue;
            if (Array.IndexOf(File.ReadAllBytes(path), (byte)0) >= 0) offenders.Add(rel);
        }
        Assert.Empty(offenders);
    }
}
