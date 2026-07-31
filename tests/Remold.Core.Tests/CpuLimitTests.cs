using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Remold.App.Views;
using Remold.Core.Migoto;
using Remold.Core.Tests.Migoto;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The one CPU limit and what it reaches. The Settings box parses to the stored value (blank = every core),
/// and the build's operator solve honours it — capped or not, the emitted mod is the same bytes.
/// </summary>
public class CpuLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gf2-cpulimit-" + Guid.NewGuid().ToString("N"));

    public CpuLimitTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankBox_MeansEveryCore(string? typed)
    {
        Assert.True(SettingsValidation.CpuLimit(typed, out var value, out var reason));
        Assert.Null(value);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("4", 4)]
    [InlineData(" 6 ", 6)]
    [InlineData("1", 1)]
    public void APositiveWholeNumber_IsTheCap(string typed, int expected)
    {
        Assert.True(SettingsValidation.CpuLimit(typed, out var value, out _));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("two")]
    [InlineData("2.5")]
    public void AnythingElse_RefusesTheSave_WithAReason(string typed)
    {
        Assert.False(SettingsValidation.CpuLimit(typed, out var value, out var reason));
        Assert.Null(value);
        Assert.Equal(SettingsValidation.CpuNotANumber, reason);
    }

    /// <summary>The refusal is two sentences like every other row's: what is wrong, then what to do — and
    /// the remedy names BOTH ways out, because blanking the box is the one the modder who typed a word into
    /// it most likely wants.</summary>
    [Fact]
    public void TheRefusal_NamesTheRemedy()
    {
        Assert.Contains("Not a whole number above 0.", SettingsValidation.CpuNotANumber);
        Assert.Contains("Enter a number of cores", SettingsValidation.CpuNotANumber);
        Assert.Contains("blank", SettingsValidation.CpuNotANumber);
    }

    /// <summary>The cap is a scheduling bound and nothing else: the same pool solved on one core emits the
    /// files an uncapped solve does, byte for byte.</summary>
    [Fact]
    public void ACappedSolve_EmitsWhatAnUncappedOneDoes()
    {
        string dump = Path.Combine(_root, "alpha");
        SyntheticPool.WriteCoWeightedDump(dump, strongHash: 101, weakHash: 102, strongVerts: 62);

        string uncapped = Build(dump, "uncapped", cpuLimit: null);
        string capped = Build(dump, "capped", cpuLimit: 1);

        var want = Directory.GetFiles(uncapped).Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(want, Directory.GetFiles(capped).Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal).ToList());
        foreach (var name in want)
            Assert.True(
                File.ReadAllBytes(Path.Combine(uncapped, name!))
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(capped, name!))),
                $"{name} differs under a capped solve");
    }

    private string Build(string dump, string tag, int? cpuLimit)
    {
        string outDir = Path.Combine(_root, tag);
        new MigotoEmitter { CpuLimit = cpuLimit }.Build(new PoolBuildRequest
        {
            OutDir = outDir,
            Pipelines = new[]
            {
                new ReplacePipeline
                {
                    Suffix = "swap",
                    Parts = new[] { new PoolPart("alpha", dump) },
                    CaptureHashes = new Dictionary<string, string> { ["alpha"] = "aaaa0001" },
                },
            },
        });
        return outDir;
    }
}
