using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The Stage-3 cache-only prewarm and its background-work ownership.</summary>
public class RiggedGlbPrewarmTests
{
    private const string BodyLogical = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb1.bundle";
    private const string BodyPhysical = "11111111111111111111111111111111";
    private const string BodyMesh = "body_lod0";
    private static readonly Outfit Outfit = new(0, "VesnaSSR01", OutfitKind.Base);

    private static (GameVfs Vfs, SubjectModel Model, AssetExporter.SubjectRoster Roster) Fixture(TempGame g)
    {
        var abw = g.At("AssetBundles_Windows");
        Directory.CreateDirectory(abw);
        SyntheticBundle.BuildOneSkinnedMesh(Path.Combine(abw, BodyPhysical + ".bundle"), BodyMesh,
            new float[] { 0, 0, 0, 1, 0, 0, 1, 1, 0 }, new[] { 0, 1, 2 }, new[] { 11u },
            bundleName: BodyLogical);
        var vfs = TestVfs.Create(g.Root, Array.Empty<(string, string)>(), null,
            (BodyLogical, BodyPhysical));
        var model = new SubjectModel("Vesna", Outfit.Stem, SubjectSource.Prefab,
            new[]
            {
                new SubjectPart("body", BodyMesh, "", Array.Empty<SubjectMaterial>(),
                    MeshBundle: BodyLogical),
            }, null, Array.Empty<string>());
        var roster = new AssetExporter.SubjectRoster(new[]
        {
            new AssetExporter.RosterPart(BodyMesh, "body", BodyLogical, 0, true,
                VisibilityOverride.None),
        });
        return (vfs, model, roster);
    }

    [Fact]
    public void Prewarm_publishes_rigged_and_prepared_cache_files_without_touching_the_mod()
    {
        using var g = new TempGame();
        var fixture = Fixture(g);
        var mod = g.At("the-mod");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "sentinel.txt"), "authored");
        var before = Directory.EnumerateFileSystemEntries(mod, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(mod, path)).OrderBy(path => path).ToArray();
        var cacheRoot = g.At("cache");
        var cache = new RiggedGlbCache(Remold.Core.LabPaths.RiggedGlbRootIn(cacheRoot));

        Assert.Equal(MainWindowViewModel.RiggedGlbPrewarmOutcome.Ready,
            MainWindowViewModel.PrewarmRiggedGlbSubject(g.Root, fixture.Vfs, Outfit, "Vesna",
                fixture.Model, fixture.Roster, wardrobeUnreadable: false, cacheRoot, cache,
                CancellationToken.None));

        Assert.Equal(before, Directory.EnumerateFileSystemEntries(mod, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(mod, path)).OrderBy(path => path).ToArray());
        Assert.Equal("authored", File.ReadAllText(Path.Combine(mod, "sentinel.txt")));
        Assert.False(Directory.Exists(Path.Combine(mod, ".ingress")));
        Assert.Empty(Directory.EnumerateFiles(cacheRoot, "*.unused-prepared.glb", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(cacheRoot, ".prewarm", SearchOption.AllDirectories));
        var completions = Directory.EnumerateFiles(cacheRoot, "complete.json", SearchOption.AllDirectories)
            .Select(path => JsonDocument.Parse(File.ReadAllText(path))).ToList();
        try
        {
            Assert.Equal(2, completions.Count);
            Assert.Contains(completions, manifest =>
                manifest.RootElement.GetProperty("artifactKey").GetString() == BodyMesh);
            Assert.Contains(completions, manifest =>
                manifest.RootElement.GetProperty("artifactKey").GetString()!
                    .StartsWith("\u0001prepared-part-v1:", StringComparison.Ordinal));
        }
        finally { foreach (var completion in completions) completion.Dispose(); }
    }

    [Fact]
    public void A_cancelled_prebuild_leaves_no_completion_marker_or_transient_tree()
    {
        using var g = new TempGame();
        var fixture = Fixture(g);
        var cacheRoot = g.At("cache");
        var cache = new RiggedGlbCache(Remold.Core.LabPaths.RiggedGlbRootIn(cacheRoot));
        using var gone = new CancellationTokenSource();
        gone.Cancel();

        Assert.Throws<OperationCanceledException>(() => MainWindowViewModel.PrewarmRiggedGlbSubject(
            g.Root, fixture.Vfs, Outfit, "Vesna", fixture.Model, fixture.Roster,
            wardrobeUnreadable: false, cacheRoot, cache, gone.Token));

        Assert.False(Directory.Exists(cacheRoot)
            && Directory.EnumerateFiles(cacheRoot, "complete.json", SearchOption.AllDirectories).Any());
        Assert.False(Directory.Exists(Remold.Core.LabPaths.RiggedGlbRootIn(cacheRoot))
            && Directory.EnumerateDirectories(Remold.Core.LabPaths.RiggedGlbRootIn(cacheRoot),
                ".prewarm", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Failure_uses_only_the_background_cell_and_never_changes_authored_state()
    {
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: action => action());
        vm.EditPage.ReportStatus("verb-owned status");
        long revision = vm.EditSession.Revision;
        int saves = vm.ProjectSaves;

        vm.StartRiggedGlbPrewarm(new[]
        {
            new MainWindowViewModel.RiggedGlbPrewarmWork(
                _ => MainWindowViewModel.RiggedGlbPrewarmOutcome.CacheFailure),
        });
        await vm.RiggedGlbPrewarmTask;

        Assert.Equal(MainWindowViewModel.RiggedGlbPrewarmUnavailable, vm.BackgroundStatus.Text);
        Assert.Equal(MainWindowViewModel.RiggedGlbPrewarmUnavailableDetail, vm.BackgroundStatus.Detail);
        Assert.Equal("verb-owned status", vm.EditPage.Status);
        Assert.Equal(revision, vm.EditSession.Revision);
        Assert.Equal(saves, vm.ProjectSaves);
        Assert.Null(vm.EditSession.Snapshot().RootDir);
    }

    [Fact]
    public async Task Exporter_degradation_finishes_silently_without_a_background_facet()
    {
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: action => action());

        vm.StartRiggedGlbPrewarm(new[]
        {
            new MainWindowViewModel.RiggedGlbPrewarmWork(
                _ => MainWindowViewModel.RiggedGlbPrewarmOutcome.Skipped),
        });
        await vm.RiggedGlbPrewarmTask;

        Assert.Equal(StatusFacet.None, vm.BackgroundStatus);
    }

    [Fact]
    public async Task Real_population_route_skips_an_unreadable_wardrobe_without_a_warning_or_build()
    {
        using var g = new TempGame();
        var fixture = Fixture(g);
        var cacheRoot = g.At("cache");
        var vm = new MainWindowViewModel(startLoad: false, cacheRootFor: () => cacheRoot,
            pageDispatch: action => action()) { IsScanning = false };
        vm.SetLoadedInstallForTest(fixture.Vfs, g.Root, new[]
        {
            new Character(1, "Vesna", "Vesna", 1, 1, new List<Outfit> { Outfit }),
        });
        var index = new AuthoredWorkspaceIndex();
        index.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = Outfit.Stem });
        vm.EditSession.SetWorkspaceIndex(index);
        vm.SubjectModels.GetOrBuild("Vesna", Outfit.Stem, () => fixture.Model);
        vm.ExportSchemesByStem = _ => throw new FileNotFoundException("wardrobe table missing");

        vm.TryStartRiggedGlbPrewarm();
        await vm.RiggedGlbPrewarmTask;

        Assert.Equal(StatusFacet.None, vm.BackgroundStatus);
        Assert.False(Directory.Exists(Path.Combine(Remold.Core.LabPaths.RiggedGlbRootIn(cacheRoot), ".prewarm")));
        Assert.False(Directory.Exists(cacheRoot)
            && Directory.EnumerateFiles(cacheRoot, "complete.json", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Concurrent_export_scheme_callers_share_one_blocking_table_read()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        using var callersReady = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        using var readStarted = new ManualResetEventSlim();
        using var releaseRead = new ManualResetEventSlim();
        int reads = 0;
        vm.ExportSchemesByStem = _ =>
        {
            Interlocked.Increment(ref reads);
            readStarted.Set();
            releaseRead.Wait();
            return new Dictionary<string, IReadOnlyList<Remold.Core.Tables.PartScheme.Slot>>();
        };
        Task<(IReadOnlyList<Remold.Core.Tables.PartScheme.Slot>? Slots, bool Unreadable)> Call() => Task.Run(() =>
        {
            callersReady.Signal();
            start.Wait();
            return vm.ExportScheme("game", "stem");
        });

        var first = Call();
        var second = Call();
        Assert.True(callersReady.Wait(TimeSpan.FromSeconds(5)));
        start.Set();
        Assert.True(readStarted.Wait(TimeSpan.FromSeconds(5)));
        releaseRead.Set();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, reads);
        Assert.All(results, result => Assert.False(result.Unreadable));
    }

    [Fact]
    public async Task Interactive_open_claim_cancels_speculation_before_the_claim_returns()
    {
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: action => action());
        using var started = new ManualResetEventSlim();
        vm.StartRiggedGlbPrewarm(new[]
        {
            new MainWindowViewModel.RiggedGlbPrewarmWork(token =>
            {
                started.Set();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return MainWindowViewModel.RiggedGlbPrewarmOutcome.Ready;
            }),
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var owner = vm.RiggedGlbPrewarmCts!;
        var task = vm.RiggedGlbPrewarmTask;

        using (vm.BeginInteractiveRiggedGlbOpen())
        {
            Assert.True(owner.IsCancellationRequested);
            Assert.Null(vm.RiggedGlbPrewarmCts);
            Assert.Equal("", vm.BackgroundStatus.Text);
        }
        await task;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Project_replacement_and_rescan_cancel_the_current_owner(bool rescan)
    {
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: action => action());
        using var started = new ManualResetEventSlim();
        vm.StartRiggedGlbPrewarm(new[]
        {
            new MainWindowViewModel.RiggedGlbPrewarmWork(token =>
            {
                started.Set();
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return MainWindowViewModel.RiggedGlbPrewarmOutcome.Ready;
            }),
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var owner = vm.RiggedGlbPrewarmCts!;
        var task = vm.RiggedGlbPrewarmTask;

        if (rescan) vm.ReloadRosterCommand.Execute(null);
        else vm.NewMod();

        Assert.True(owner.IsCancellationRequested);
        await task;
    }

    [Fact]
    public async Task Interactive_open_keeps_a_standing_cache_failure_it_does_not_replace()
    {
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: action => action());
        vm.StartRiggedGlbPrewarm(new[]
        {
            new MainWindowViewModel.RiggedGlbPrewarmWork(
                _ => MainWindowViewModel.RiggedGlbPrewarmOutcome.CacheFailure),
        });
        await vm.RiggedGlbPrewarmTask;

        using (vm.BeginInteractiveRiggedGlbOpen())
            Assert.Equal(MainWindowViewModel.RiggedGlbPrewarmUnavailable, vm.BackgroundStatus.Text);

        Assert.Equal(MainWindowViewModel.RiggedGlbPrewarmUnavailable, vm.BackgroundStatus.Text);
    }

    [Fact]
    public void Load_finalize_drains_a_queued_rescan_before_speculation()
    {
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: action => action())
        {
            IsScanning = true,
            SearchText = "keep only if reload did not run",
        };
        vm.ReloadRosterCommand.Execute(null);
        vm.IsScanning = false;

        vm.FinishRosterLoadBackgroundWork();

        Assert.Equal("", vm.SearchText);
        Assert.True(vm.IsScanning);
        Assert.Null(vm.RiggedGlbPrewarmCts);
    }

    [Fact]
    public void Prewarm_has_the_approved_line_tooltip_and_priority_in_the_shared_cell()
    {
        var progress = new MainWindowViewModel.RiggedGlbPrewarmProgress(2, 7);
        var facet = MainWindowViewModel.BackgroundFacet(
            new SharingProgress(9, 10, Delta: true), sharingFailed: true,
            riggedGlbPrewarm: progress, riggedGlbPrewarmFailed: false);

        Assert.Equal("Preparing parts… 2/7", facet.Text);
        Assert.Equal(MainWindowViewModel.RiggedGlbPrewarmTip, facet.Detail);
        Assert.Contains("2/7", facet.Tip, StringComparison.Ordinal);

        var failed = MainWindowViewModel.BackgroundFacet(null, sharingFailed: false,
            riggedGlbPrewarm: null, riggedGlbPrewarmFailed: true);
        Assert.Equal("Blender opens will be slower", failed.Text);
    }
}
