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

    /// <summary>An ordinary Save arms nothing — the row is opt-in, and a settings edit must never sweep.</summary>
    [Fact]
    public void A_save_that_did_not_arm_the_row_owes_no_sweep()
    {
        using var s = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false, IsModBuilding = true };

        vm.ApplySettings(new SettingsResult { Author = "tester" });

        Assert.False(vm.ForceRescanPending);
    }

    /// <summary>The armed row under a hold: the request is recorded and the standing queued notice carries
    /// it. Nothing is deleted while the build is still reading — the sweep is still OWED, which is what
    /// "queued rather than run under a hold" means on this route.</summary>
    [Fact]
    public void A_force_rescan_under_a_hold_is_queued_with_its_sweep_still_owed()
    {
        using var s = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false)
        {
            IsScanning = false,
            IsModBuilding = true,   // a build is reading what the sweep would delete
        };

        vm.ApplySettings(new SettingsResult { ForceRescan = true });

        Assert.True(vm.ForceRescanPending);   // owed, not done
        Assert.Equal(MainWindowViewModel.RescanQueuedNotice, vm.NoticeStatus.Text);
        Assert.Equal(MainWindowViewModel.RescanQueuedForceDetail, vm.NoticeStatus.Detail);

        // …and the ordinary Tools rescan, clicked under the same hold, queues on its own flag without
        // consuming the sweep: the request has to survive until a reload really happens.
        vm.ReloadRosterCommand.Execute(null);

        Assert.True(vm.ForceRescanPending);
        Assert.Equal(MainWindowViewModel.RescanQueuedNotice, vm.NoticeStatus.Text);
        Assert.Equal(MainWindowViewModel.RescanQueuedForceDetail, vm.NoticeStatus.Detail);
    }

    /// <summary>A plain queued rescan keeps the standing words. The CELL reads the same either way — one
    /// wait, one word for it — and only the detail beneath it tells the two apart, because deleting caches
    /// is a bigger promise than re-reading files and the modder is owed the difference.</summary>
    [Fact]
    public void A_queued_rescan_with_no_sweep_owed_keeps_the_standing_detail()
    {
        using var s = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false)
        {
            IsScanning = false,
            IsModBuilding = true,
        };

        vm.ReloadRosterCommand.Execute(null);

        Assert.False(vm.ForceRescanPending);
        Assert.Equal(MainWindowViewModel.RescanQueuedNotice, vm.NoticeStatus.Text);
        Assert.Equal(MainWindowViewModel.RescanQueuedDetail, vm.NoticeStatus.Detail);
        Assert.NotEqual(MainWindowViewModel.RescanQueuedDetail, MainWindowViewModel.RescanQueuedForceDetail);
    }

    // ---- the debt outlives the session ------------------------------------------------------------

    /// <summary>A sweep armed under a hold and then closed on is NOT lost: the request is durable, so the
    /// next session's view-model comes up owing it and its first load is the rescan that pays it.</summary>
    [Fact]
    public void A_sweep_armed_under_a_hold_survives_a_restart()
    {
        using var s = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false)
        {
            IsScanning = false,
            IsModBuilding = true,   // the sweep can't run, so the app could be closed still owing it
        };

        vm.ApplySettings(new SettingsResult { ForceRescan = true });

        Assert.True(LabSettings.Load().ForceRescanOwed);   // written to the durable file, not just held

        // the next session, reading that same file
        var next = new MainWindowViewModel(startLoad: false);
        Assert.True(next.ForceRescanPending);
    }

    /// <summary>An ordinary Save leaves nothing owed on disk — the durable flag is the row's state, so a
    /// settings edit that never touched the row must not arm one for the next launch.</summary>
    [Fact]
    public void A_save_that_did_not_arm_the_row_owes_nothing_to_the_next_session()
    {
        using var s = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false, IsModBuilding = true };

        vm.ApplySettings(new SettingsResult { Author = "tester" });

        Assert.False(LabSettings.Load().ForceRescanOwed);
        Assert.False(new MainWindowViewModel(startLoad: false).ForceRescanPending);
    }

    // ---- reopening Settings while a sweep is owed -------------------------------------------------

    /// <summary>The dialog opens on the truth. A sweep still owed shows the row ARMED rather than offering
    /// to arm a request that already stands — and because arming has an inverse, disarming and saving is
    /// what takes it back, end to end and off the disk with it.</summary>
    [Fact]
    public void An_owed_sweep_opens_the_row_armed_and_a_disarming_save_clears_it()
    {
        using var s = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false, IsModBuilding = true };
        vm.ApplySettings(new SettingsResult { ForceRescan = true });

        Assert.True(vm.BuildSettingsInput().ForceRescanOwed);
        Assert.Equal(SettingsWindow.ForceRescanArmedLabel, SettingsWindow.ForceRescanButtonLabel(armed: true));

        // the modder clicks the armed button again and saves: the form hands back the row's whole state
        vm.ApplySettings(new SettingsResult { ForceRescan = false });

        Assert.False(vm.ForceRescanPending);
        Assert.False(vm.BuildSettingsInput().ForceRescanOwed);
        Assert.False(LabSettings.Load().ForceRescanOwed);
        Assert.False(new MainWindowViewModel(startLoad: false).ForceRescanPending);
    }

    /// <summary>Taking the request back rewrites the notice it left standing. The queued line is written
    /// once, at the click that queued it, so a sweep disarmed afterwards would go on promising cleared caches
    /// under a cell that never changed — the modder reading the same wait would be told the wrong thing about
    /// what it will do.</summary>
    [Fact]
    public void A_taken_back_sweep_rewrites_the_standing_queued_notice()
    {
        using var s = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false, IsModBuilding = true };

        vm.ApplySettings(new SettingsResult { ForceRescan = true });
        Assert.Equal(MainWindowViewModel.RescanQueuedForceDetail, vm.NoticeStatus.Detail);

        // reopened while the build still holds, disarmed, saved: the row hands back its whole state
        vm.ApplySettings(new SettingsResult { ForceRescan = false, ForceRescanWasOwed = true });

        Assert.False(vm.ForceRescanPending);
        Assert.False(LabSettings.Load().ForceRescanOwed);
        Assert.Equal(MainWindowViewModel.RescanQueuedNotice, vm.NoticeStatus.Text);   // still the one wait
        Assert.Equal(MainWindowViewModel.RescanQueuedDetail, vm.NoticeStatus.Detail);
    }

    /// <summary>The third route that queues a rescan: a game folder picked while a scan is in flight. It
    /// keeps its own title and leading sentence — the folder change is the news — and takes the shared detail
    /// after it, so a sweep owed while it stands is named here as it is everywhere else.</summary>
    [Fact]
    public void A_folder_change_under_a_scan_names_an_owed_sweep()
    {
        using var s = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false, IsModBuilding = true };
        vm.ApplySettings(new SettingsResult { ForceRescan = true });   // owed, queued behind the build
        vm.IsScanning = true;   // …and a load is in flight, which is what this route queues behind

        vm.SetGameDir(_install.Root);

        Assert.True(vm.ForceRescanPending);
        Assert.Equal(MainWindowViewModel.GameDirChangedNotice, vm.NoticeStatus.Text);
        Assert.StartsWith(MainWindowViewModel.GameDirChangedLead, vm.NoticeStatus.Detail);
        Assert.EndsWith(MainWindowViewModel.RescanQueuedForceDetail, vm.NoticeStatus.Detail);
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

    /// <summary>A Save that only CARRIES a request already owed re-reads nothing. The row hands back its whole
    /// state, so an unrelated settings edit made with a sweep pending looks exactly like arming one — and
    /// firing the reload on that would cost a full re-read of the install for a change that asked for none.
    /// The debt stands on both flags and the next rescan honours it.</summary>
    [Fact]
    public void A_save_that_only_carries_an_owed_sweep_re_reads_nothing()
    {
        using var g = new TempGame();
        using var s = new SettingsSnapshot();
        var cache = FakeCache(g.Root);
        var lib = Path.Combine(g.Root, "mods");

        var vm = new MainWindowViewModel(startLoad: false) { IsScanning = false, IsModBuilding = true };
        vm.CacheRootFor = () => cache;
        vm.ApplySettings(new SettingsResult
        {
            ForceRescan = true, LibraryRoot = lib, GamePath = _install.Root,
        });
        vm.IsModBuilding = false;   // the build ended; nothing holds the roster and the sweep is still owed

        // Settings reopened and saved with the row untouched: it opened armed, and hands that back
        vm.ApplySettings(new SettingsResult
        {
            ForceRescan = true, ForceRescanWasOwed = true, Author = "tester",
            LibraryRoot = lib, GamePath = _install.Root,
        });

        // A reload would have consumed the request on this very thread before returning, and started the scan.
        Assert.True(vm.ForceRescanPending);
        Assert.True(LabSettings.Load().ForceRescanOwed);
        Assert.False(vm.IsScanning);
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
