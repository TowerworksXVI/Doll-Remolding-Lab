using System.IO;
using Remold.Core;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>Persisted author handle (<c>LabSettings</c>) — round-trip + graceful defaults.</summary>
public class LabSettingsTests
{
    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var g = new TempGame();
        var s = LabSettings.Load(g.At("nope.json"));
        Assert.Equal("", s.Author);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAuthor()
    {
        using var g = new TempGame();
        var path = g.At("settings.json");
        new LabSettings { Author = "TestAuthor" }.Save(path);
        Assert.Equal("TestAuthor", LabSettings.Load(path).Author);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaults()
    {
        using var g = new TempGame();
        var path = g.At("settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.Equal("", LabSettings.Load(path).Author);
    }

    [Fact]
    public void Load_ExplicitNullMembers_CoalesceToDefaults()
    {
        // An explicit null overrides the field initializer and would NRE in any consumer, so Load must
        // coalesce it back.
        using var g = new TempGame();
        var path = g.At("settings.json");
        File.WriteAllText(path, "{ \"Author\": null, \"RecentMods\": null }");

        var s = LabSettings.Load(path);
        Assert.Equal("", s.Author);
        Assert.NotNull(s.RecentMods);
        s.AddRecent("C:\\mods\\x", "X");   // would NRE if RecentMods were null
        Assert.Single(s.RecentMods);
    }

    [Fact]
    public void ResolvedLibraryRoot_DefaultsBesideExe_OrConfigured()
    {
        // The portable default is the exe-adjacent library (LabPaths.DefaultLibraryRoot); a configured
        // LibraryRoot wins verbatim.
        Assert.Equal(LabPaths.DefaultLibraryRoot, new LabSettings().ResolvedLibraryRoot);
        Assert.EndsWith("mods", new LabSettings().ResolvedLibraryRoot);
        Assert.Equal(@"D:\mods", new LabSettings { LibraryRoot = @"D:\mods" }.ResolvedLibraryRoot);
    }

    [Fact]
    public void AddRecent_MovesToFront_Dedupes_AndCaps()
    {
        var s = new LabSettings();
        s.AddRecent(@"C:\a", "A");
        s.AddRecent(@"C:\b", "B");
        s.AddRecent(@"C:\a", "A");        // re-open A → back to front, no duplicate

        Assert.Equal(2, s.RecentMods.Count);
        Assert.Equal(@"C:\a", s.RecentMods[0].Path);
        Assert.Equal(@"C:\b", s.RecentMods[1].Path);

        for (int i = 0; i < LabSettings.MaxRecent + 5; i++) s.AddRecent($@"C:\m{i}", $"m{i}");
        Assert.Equal(LabSettings.MaxRecent, s.RecentMods.Count);
        Assert.Equal($@"C:\m{LabSettings.MaxRecent + 4}", s.RecentMods[0].Path);   // newest first
    }

    [Fact]
    public void Load_IgnoresUnknownKeys_ForwardCompat()
    {
        // A settings.json carrying keys this build doesn't know still loads — unknown keys are ignored.
        using var g = new TempGame();
        var path = g.At("settings.json");
        File.WriteAllText(path, """{ "Author": "TestAuthor", "DisplayNameLocale": "Jajp", "LoaderPath": "C:\\x.exe", "future": 1 }""");
        Assert.Equal("TestAuthor", LabSettings.Load(path).Author);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRecentAndLibraryRoot()
    {
        using var g = new TempGame();
        var path = g.At("settings.json");
        var s = new LabSettings { Author = "TestAuthor", LibraryRoot = @"D:\mods" };
        s.AddRecent(@"D:\mods\karst", "Karst Jacket");
        s.Save(path);

        var back = LabSettings.Load(path);
        Assert.Equal(@"D:\mods", back.LibraryRoot);
        Assert.Single(back.RecentMods);
        Assert.Equal("Karst Jacket", back.RecentMods[0].Name);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsForceRescanOwed()
    {
        using var g = new TempGame();
        var path = g.At("settings.json");
        new LabSettings { ForceRescanOwed = true }.Save(path);
        Assert.True(LabSettings.Load(path).ForceRescanOwed);
    }

    [Fact]
    public void Load_FileWithoutForceRescanOwed_OwesNothing()
    {
        // Every settings.json a released build wrote predates the key. Absent must read as "nothing owed":
        // the other way round, a first launch on an updated app would sweep its own caches unasked.
        using var g = new TempGame();
        var path = g.At("settings.json");
        File.WriteAllText(path, """{ "Author": "TestAuthor", "LibraryRoot": "D:\\mods" }""");
        Assert.False(LabSettings.Load(path).ForceRescanOwed);
    }
}
