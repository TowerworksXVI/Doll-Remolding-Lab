using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.Views;
using Remold.Core;
using Remold.Core.Export;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Settings maintenance row that clears the rebuilt caches. It arms exactly the way the Recents-clear row
/// beside it does — the click only ARMS, the Save fires it — and what it arms is a REQUEST: the deletions run
/// when the rescan really runs, never while a build, scan, materialize or prewarm is reading the very caches
/// they would remove. Pinned at the view-model, because the failure the queue prevents is a timing one the
/// layout can't show.
/// </summary>
[Collection("Dispatcher")]   // serialized with every other settings.json reader/writer — one file, one bin dir
public class ForceRescanRowTests : IClassFixture<ForceRescanRowTests.FakeInstall>
{
    /// <summary>A folder the game locator accepts, living for the whole class rather than one test.
    /// <para>A Save that reloads starts the load on ANOTHER thread, and that thread resolves the game
    /// directory for itself. Hand it a path in a fixture the test then tears down and the resolve falls
    /// through to auto-detect — which on a developer's machine finds the REAL install, writes it into
    /// settings and scans it, from a test that believed it was finished. Held here, the path stays valid
    /// for as long as any of these tests' loads can look at it, and the load dies on its first table read
    /// the way it should.</para></summary>
    public sealed class FakeInstall : System.IDisposable
    {
        public string Root { get; }

        public FakeInstall()
        {
            Root = Path.Combine(Path.GetTempPath(), "remold-tests",
                "install-" + System.Guid.NewGuid().ToString("N"), "GIRLS' FRONTLINE 2 EXILIUM");
            var abw = Path.Combine(Root, "GF2_Exilium_Data", "LocalCache", "Data", "AssetBundles_Windows");
            Directory.CreateDirectory(abw);
            File.WriteAllText(Path.Combine(abw, "catalog_main_24535.bin"), "x");
            File.WriteAllText(Path.Combine(abw, "08dfe7d89b6fe56375d6dfec87ffcc8a.bundle"), "x");
        }

        public void Dispose()
        {
            try { Directory.Delete(Directory.GetParent(Root)!.FullName, recursive: true); } catch { /* best effort */ }
        }
    }

    private readonly FakeInstall _install;

    public ForceRescanRowTests(FakeInstall install) => _install = install;

    /// <summary>The words on the row, as the modder reads them. The detail line names what is kept as
    /// plainly as what goes: this row deletes, and the promise not to touch their work is the row.</summary>
    [Fact]
    public void The_row_says_what_it_clears_and_what_it_keeps()
    {
        Assert.Equal("Force rescan", SettingsWindow.ForceRescanLabel);
        Assert.Equal("Clears the app's rebuilt caches, thumbnails included, then re-reads the game. "
            + "Mods, projects, and edits are kept.", SettingsWindow.ForceRescanHint);
        Assert.Equal("Clear caches and rescan", SettingsWindow.ForceRescanRestingLabel);
        Assert.Equal("Caches clear on Save", SettingsWindow.ForceRescanArmedLabel);
    }

    /// <summary>The hint names the caches as a CATEGORY, not tree by tree. The sweep takes four of them and
    /// a line that lists a couple is false about the rest — so the one tree it does name is the thumbnails,
    /// which is the part the modder can watch go, and the others are covered by the category.</summary>
    [Fact]
    public void The_hint_names_the_caches_as_a_category_rather_than_listing_them()
    {
        Assert.True(LabPaths.DerivedCacheFolders.Count > 2, "a listing hint would now be false by omission");
        // no tree is named by its own name — "thumbnails" is the modder's word, not the folder's
        foreach (var folder in LabPaths.DerivedCacheFolders)
            Assert.DoesNotContain(folder, SettingsWindow.ForceRescanHint, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("caches", SettingsWindow.ForceRescanHint);
        Assert.Contains("thumbnails", SettingsWindow.ForceRescanHint);
    }

    /// <summary>The armed word is a STATEMENT of what the Save will do, like the Recents row's beside it —
    /// not a question. The button stays live once armed, so a question mark would read as the click asking
    /// for a confirmation that never comes.</summary>
    [Fact]
    public void The_armed_word_reports_what_the_save_does_rather_than_asking()
    {
        Assert.DoesNotContain("?", SettingsWindow.ForceRescanArmedLabel);
        Assert.EndsWith("clear on Save", SettingsWindow.ForceRescanArmedLabel);
        Assert.EndsWith("clear on Save", SettingsWindow.RecentsPendingLabel);
    }

    /// <summary>Arming is reversible on this row: the button toggles rather than latching, so the label goes
    /// back on a second click. The one word source both the opening form and every toggle read.</summary>
    [Fact]
    public void The_button_word_follows_the_row_back_out_of_armed()
    {
        Assert.Equal(SettingsWindow.ForceRescanRestingLabel, SettingsWindow.ForceRescanButtonLabel(armed: false));
        Assert.Equal(SettingsWindow.ForceRescanArmedLabel, SettingsWindow.ForceRescanButtonLabel(armed: true));
    }


    /// <summary>Nothing owed opens the row at rest. The pair with the test above: the row reports state, so
    /// it must be able to report "nothing pending" as plainly as it reports the sweep.</summary>
    [Fact]
    public void Nothing_owed_opens_the_row_at_rest()
    {
        using var s = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false };

        Assert.False(vm.BuildSettingsInput().ForceRescanOwed);
        Assert.Equal(SettingsWindow.ForceRescanRestingLabel, SettingsWindow.ForceRescanButtonLabel(armed: false));
    }

    // ---- the fired route --------------------------------------------------------------------------
    // Everything below drives the sweep for real, so every root it can reach is redirected into a temp
    // folder first: the cache root through the view-model's seam (LabPaths.CacheRoot reads %LOCALAPPDATA%,
    // which nothing redirects under test), the library through the settings the same Save applies.

    /// <summary>A cache root laid out the way the app writes it, plus the opt-in log the sweep must not
    /// claim.</summary>
    private static string FakeCache(string parent)
    {
        var cache = Path.Combine(parent, "cache");
        foreach (var folder in LabPaths.DerivedCacheFolders)
        {
            var d = Path.Combine(cache, folder, "nested");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "deep.bin"), "x");
        }
        File.WriteAllText(Path.Combine(cache, "launch_timing.log"), "opt-in log");
        return cache;
    }

    /// <summary>A mod project as the app writes one: manifest, combined glb, its fingerprint sidecar, a
    /// texture.</summary>
    private static string FakeProject(string libraryRoot, string name)
    {
        var root = Path.Combine(libraryRoot, name);
        var meshes = Path.Combine(root, "subject", "meshes");
        Directory.CreateDirectory(meshes);
        File.WriteAllText(ModProject.ManifestPathFor(root), "{}");
        File.WriteAllText(Path.Combine(meshes, AssetExporter.CombinedGlbName), "glb");
        File.WriteAllText(Path.Combine(meshes, CacheReset.CombinedFingerprintName), "fingerprint");
        File.WriteAllText(Path.Combine(meshes, "mine.fingerprint"), "the modder's");
        return root;
    }

    /// <summary>The mainline: the row armed, Save pressed, nothing holding the roster. The sweep runs
    /// inside the reload and takes exactly what the app rebuilds — and the debt is settled, so the NEXT
    /// rescan sweeps nothing.</summary>
    [Fact]
    public async Task An_armed_save_with_nothing_holding_sweeps_the_rebuilt_caches_and_nothing_else()
    {
        using var g = new TempGame();
        using var s = new SettingsSnapshot();
        var cache = FakeCache(g.Root);
        var lib = Path.Combine(g.Root, "mods");
        var project = FakeProject(lib, "one");
        var meshes = Path.Combine(project, "subject", "meshes");

        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false };
        vm.CacheRootFor = () => cache;

        vm.ApplySettings(new SettingsResult
        {
            ForceRescan = true, LibraryRoot = lib, GamePath = _install.Root,
        });
        // The deletions run off the UI thread; this is the wait the load itself takes before it reads a
        // cache, so a test asserting on the disk takes the same one.
        await vm.PendingCachePurge;

        foreach (var folder in LabPaths.DerivedCacheFolders)
            Assert.False(Directory.Exists(Path.Combine(cache, folder)), folder + " survived the fired sweep");
        Assert.False(File.Exists(Path.Combine(meshes, CacheReset.CombinedFingerprintName)));
        // …and the durable look-alikes sitting beside every one of them stand
        Assert.True(Directory.Exists(cache));
        Assert.True(File.Exists(Path.Combine(cache, "launch_timing.log")));
        Assert.True(File.Exists(Path.Combine(meshes, AssetExporter.CombinedGlbName)));
        Assert.True(File.Exists(Path.Combine(meshes, "mine.fingerprint")));
        Assert.True(File.Exists(ModProject.ManifestPathFor(project)));
        Assert.False(vm.ForceRescanPending);   // consumed, so an ordinary rescan later sweeps nothing
        // …and the debt is settled on disk with it: a sweep that RAN must not run again next launch
        Assert.False(LabSettings.Load().ForceRescanOwed);
        Assert.False(new MainWindowViewModel(startLoad: false).ForceRescanPending);
    }

    /// <summary>The seam the load waits on. The deletions leave the UI thread, but nothing that reads or
    /// writes a cache may start until they are done: a reload that raced its own sweep would write a rebuilt
    /// snapshot into a folder still being emptied and leave the stale file standing, which is the exact
    /// thing this row exists to end.
    /// <para>The sweep is held open at its own seam, so what the load sees is pinned rather than timed: the
    /// caches stand while the sweep is held, and every one of them is gone by the time the wait returns.</para></summary>
    [Fact]
    public async Task The_load_reads_no_cache_until_the_sweep_has_finished()
    {
        using var g = new TempGame();
        using var s = new SettingsSnapshot();
        var cache = FakeCache(g.Root);
        var lib = Path.Combine(g.Root, "mods");
        var index = Path.Combine(cache, LabPaths.DerivedCacheFolders[0]);

        using var held = new ManualResetEventSlim(false);
        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false };
        vm.CacheRootFor = () => { held.Wait(); return cache; };

        vm.ApplySettings(new SettingsResult
        {
            ForceRescan = true, LibraryRoot = lib, GamePath = _install.Root,
        });

        // held open: the Save is back, and not one cache folder has gone
        Assert.False(vm.PendingCachePurge.IsCompleted);
        Assert.True(Directory.Exists(index));

        // what any reader of a cache does first — the same wait the load takes at its top
        var whatTheLoadSees = Task.Run(async () =>
        {
            await vm.PendingCachePurge;
            return Directory.Exists(index);
        });
        Assert.False(whatTheLoadSees.IsCompleted);   // nothing may read ahead of the sweep

        held.Set();
        Assert.False(await whatTheLoadSees);   // the reader saw the swept disk
        foreach (var folder in LabPaths.DerivedCacheFolders)
            Assert.False(Directory.Exists(Path.Combine(cache, folder)), folder + " survived the sweep");
    }

    /// <summary>The debt that survived an exit, paid by the app's FIRST load. The construction path fires its
    /// own sweep — the load starting behind it is the one that would otherwise rebuild the very caches it is
    /// owed — and that sweep is driven here against a temp root through the ctor's seam, because by the time
    /// a caller holds the instance it has already run.
    /// <para>The load itself is left to die where it always does (the fake install has no tables, and it
    /// parks on its first dispatcher hop in the test host); the sweep is what this pins, so the wait it
    /// publishes is the assertion seam.</para></summary>
    [Fact]
    public async Task A_debt_that_outlived_the_session_is_swept_by_the_first_load()
    {
        using var g = new TempGame();
        using var s = new SettingsSnapshot();
        var cache = FakeCache(g.Root);
        var lib = Path.Combine(g.Root, "mods");
        var project = FakeProject(lib, "one");
        var meshes = Path.Combine(project, "subject", "meshes");

        // the settings file the previous session closed on: a sweep armed, saved, and never run
        new LabSettings { ForceRescanOwed = true, GamePath = _install.Root, LibraryRoot = lib }.Save();

        var vm = new MainWindowViewModel(startLoad: true, cacheRootFor: () => cache);
        await vm.PendingCachePurge;

        foreach (var folder in LabPaths.DerivedCacheFolders)
            Assert.False(Directory.Exists(Path.Combine(cache, folder)), folder + " survived the first load's sweep");
        Assert.False(File.Exists(Path.Combine(meshes, CacheReset.CombinedFingerprintName)));
        Assert.True(File.Exists(Path.Combine(meshes, "mine.fingerprint")));   // and only what the app rebuilds
        Assert.False(vm.ForceRescanPending);
        Assert.False(LabSettings.Load().ForceRescanOwed);   // paid, so the session after this one owes nothing
    }

    /// <summary>A sweep that fell over ENTIRELY — not a held file skipped, the walk itself refusing — puts the
    /// request back rather than passing for one that ran. Session-only: the durable flag stays down, because a
    /// sweep that faults every time it runs would otherwise re-arm itself on disk and re-sweep on every launch
    /// for good.</summary>
    [Fact]
    public async Task A_sweep_that_fell_over_entirely_re_arms_the_request_for_the_next_rescan()
    {
        using var g = new TempGame();
        using var s = new SettingsSnapshot();
        var lib = Path.Combine(g.Root, "mods");

        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false };
        vm.CacheRootFor = () => throw new IOException("the cache root wouldn't answer");

        vm.ApplySettings(new SettingsResult
        {
            ForceRescan = true, LibraryRoot = lib, GamePath = _install.Root,
        });
        await vm.PendingCachePurge;   // the sweep swallows its own failure, so the wait still completes

        Assert.True(vm.ForceRescanPending);                 // owed again, for whichever rescan comes next
        Assert.False(LabSettings.Load().ForceRescanOwed);   // …but not across the exit
    }


    /// <summary>The sweep runs AFTER the sharing pass is cancelled, not before. That pass writes the
    /// sharing cache from a background thread under no hold of its own, so a sweep ahead of the cancel is a
    /// sweep the pass can undo — the cleared file comes back, holding rows measured before it.</summary>
    [Fact]
    public async Task The_sweep_waits_for_the_sharing_pass_to_be_cancelled()
    {
        using var g = new TempGame();
        using var s = new SettingsSnapshot();
        var cache = FakeCache(g.Root);
        var lib = Path.Combine(g.Root, "mods");

        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false };
        using var sharing = new CancellationTokenSource();
        vm.SharingPassCts = sharing;
        bool? passWasCancelled = null;
        vm.CacheRootFor = () => { passWasCancelled = sharing.IsCancellationRequested; return cache; };

        vm.ApplySettings(new SettingsResult
        {
            ForceRescan = true, LibraryRoot = lib, GamePath = _install.Root,
        });
        await vm.PendingCachePurge;   // the sweep runs off the UI thread; wait it out

        Assert.True(passWasCancelled.HasValue, "the sweep never ran");
        Assert.True(passWasCancelled!.Value, "the sweep ran while the sharing pass was still writing");
    }
}
