using System.IO;
using Remold.Core;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The durable-root rule: state follows the exe only when the layout proves the exe owns the app —
/// the base directory is the exe's folder (flat layout) or its direct child (release layout, exe +
/// app subfolder). Any other shape is a foreign host and stays on the base directory.
/// </summary>
public class LabPathsTests
{
    [Fact]
    public void DurableRootFor_FlatLayout_UsesTheSharedFolder()
    {
        // dev runs and existing flat installs: exe and assemblies in one folder
        Assert.Equal(@"C:\apps\drl",
            LabPaths.DurableRootFor(@"C:\apps\drl\Remold.App.exe", @"C:\apps\drl"));
    }

    [Fact]
    public void DurableRootFor_ReleaseLayout_FollowsTheExeToTheRoot()
    {
        // the packed layout: exe at the root, assemblies under app\ — state belongs beside the exe
        Assert.Equal(@"C:\apps\drl",
            LabPaths.DurableRootFor(@"C:\apps\drl\Doll Remolding Lab.exe", @"C:\apps\drl\app"));
    }

    [Fact]
    public void DurableRootFor_TrailingSeparators_DoNotBreakTheMatch()
    {
        Assert.Equal(@"C:\apps\drl",
            LabPaths.DurableRootFor(@"C:\apps\drl\Doll Remolding Lab.exe", @"C:\apps\drl\app\"));
    }

    [Fact]
    public void DurableRootFor_AForeignHost_StaysOnTheBaseDirectory()
    {
        // the test runner's shape: the process exe lives nowhere near the assemblies
        Assert.Equal(@"C:\proj\tests\bin\Debug",
            LabPaths.DurableRootFor(@"C:\nuget\testhost\testhost.exe", @"C:\proj\tests\bin\Debug"));
    }

    [Fact]
    public void DurableRootFor_ADeeperNesting_StaysOnTheBaseDirectory()
    {
        // two levels down is not the release layout; following the exe there would scatter state
        Assert.Equal(@"C:\apps\drl\app\inner",
            LabPaths.DurableRootFor(@"C:\apps\drl\x.exe", @"C:\apps\drl\app\inner"));
    }

    [Fact]
    public void DurableRootFor_NoProcessPath_StaysOnTheBaseDirectory()
    {
        Assert.Equal(@"C:\anywhere", LabPaths.DurableRootFor(null, @"C:\anywhere"));
        Assert.Equal(@"C:\anywhere", LabPaths.DurableRootFor("", @"C:\anywhere"));
    }

    [Fact]
    public void DurableRoot_UnderTheTestRunner_IsTheTestBaseDirectory()
    {
        // the live property, exercised under this very runner: the foreign-host guard must hold here
        Assert.Equal(System.AppContext.BaseDirectory, LabPaths.DurableRoot);
    }

    [Fact]
    public void SharingSeed_SitsBesideTheAssemblies_NotAtTheDurableRoot()
    {
        // shipped content, copied beside the assemblies by the build: in the release layout that is app\,
        // while the durable root is the exe's folder above it. Anchoring it to the durable root would look
        // for a file the pack never puts there.
        Assert.Equal(Path.Combine(System.AppContext.BaseDirectory, "data", "sharing_seed.json"),
            LabPaths.SharingSeedFile);
    }

    [Fact]
    public void TheSeedsObservationMemo_SitsBesideTheAssembliesToo()
    {
        // The seed's other half, minted from the same pass and shipped the same way — so it answers to the
        // same anchor. Pinned separately because the two are read by different types, and a memo anchored
        // one folder up would go silently unfound: a missing memo is not an error, only reads the shipped
        // measurement already paid for.
        Assert.Equal(Path.Combine(System.AppContext.BaseDirectory, "data", "asset_hashes_seed.json"),
            LabPaths.AssetHashSeedFile);
    }

    /// <summary>The candidacy memo rides a REDIRECTED cache root the way the stock-texture tree does. The
    /// force-rescan sweep clears the index folder under whatever root it is handed, so a memo path that
    /// hard-coded the real root would be written to one tree and swept from another — the writer and the
    /// sweeper disagreeing about which file a run is answering from.</summary>
    [Fact]
    public void TheCandidacyMemo_FollowsTheCacheRootItIsGiven()
    {
        Assert.Equal(Path.Combine("root", "index", "candidacy.json"),
            LabPaths.CandidacyCacheFileIn("root"));
        // the production default is that same rule under the real root, not a second spelling of the path
        Assert.Equal(LabPaths.CandidacyCacheFileIn(LabPaths.CacheRoot), LabPaths.CandidacyCacheFile);
        // …and it sits inside a tree the sweep actually clears
        Assert.Contains(LabPaths.DerivedCacheFolders,
            folder => LabPaths.CandidacyCacheFileIn("root")
                .StartsWith(Path.Combine("root", folder), System.StringComparison.Ordinal));
    }

    [Fact]
    public void TheRiggedGlbCache_FollowsTheCacheRootAndIsForceRescanDerived()
    {
        Assert.Equal(Path.Combine("root", "rigs"), LabPaths.RiggedGlbRootIn("root"));
        Assert.Equal(LabPaths.RiggedGlbRootIn(LabPaths.CacheRoot), LabPaths.RiggedGlbRoot);
        Assert.Contains("rigs", LabPaths.DerivedCacheFolders);
    }
}
