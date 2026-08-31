using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.Core;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The Launch action's decisions, all pure: which half of the launch is missing, where the Mods folder
/// sits relative to the 3DMigoto loader, and whether an install launches through Steam or straight off its
/// exe. Starting the processes is thin glue over the OS and is exercised live, not here.
/// </summary>
// Serialized with every other settings.json reader/writer — one file, one bin dir.
[Collection("Dispatcher")]
public class GameLaunchTests
{
    /// <summary>A host whose ini tree carries the texture hook — what every reading here that is about
    /// something ELSE stands on, so no assertion about a path accidentally turns on the hook rule.</summary>
    private static readonly MigotoIniFacts Hooked = new(Found: true, StartsTheGame: false, HasTextureHook: true);

    // ---- the enablement matrix ------------------------------------------------

    [Fact]
    public void No_game_outranks_everything_else()
    {
        Assert.Equal(LaunchGate.NoGame,
            LaunchGate.Reason(gameLocated: false, loaderExe: null, loaderExists: false));
        // even with the 3DMigoto half fully known, the game is still the first thing to settle
        Assert.Equal(LaunchGate.NoGame,
            LaunchGate.Reason(false, @"C:\3dmigoto\3DMigoto Loader.exe", true));
    }

    [Fact]
    public void An_unset_or_missing_loader_reports_the_shared_loader_remedy()
    {
        Assert.Equal(LoaderGate.NoLoader, LaunchGate.Reason(true, null, false));
        Assert.Equal(LoaderGate.NoLoader, LaunchGate.Reason(true, "   ", false));
        Assert.Equal(LoaderGate.LoaderNotFound(@"C:\gone\Run.exe"),
            LaunchGate.Reason(true, @"C:\gone\Run.exe", false));
    }

    /// <summary>Launch runs the loader itself, so a missing Mods folder is Install's problem alone — the
    /// loader's NAME is whatever its distribution ships (Run.exe for SSMT), never part of the gate.</summary>
    [Fact]
    public void A_loader_that_exists_enables_launch_whatever_its_name()
    {
        Assert.Null(LaunchGate.Reason(true, @"C:\3dmigoto\3DMigoto Loader.exe", true));
        Assert.Null(LaunchGate.Reason(true, @"C:\ssmt\GF2\Run.exe", true));
    }


    // ---- the status bar's 3DMigoto cell ---------------------------------------

    [Fact]
    public void An_unset_loader_warns_and_says_what_actually_needs_it()
    {
        var f = MainWindowViewModel.MigotoFacet(null, loaderExists: false, modsFolder: null, default);

        Assert.Equal("⚠", f.Glyph);
        Assert.Contains("not set", f.Text);
        Assert.Contains("Launch and Install", f.Detail);
    }

    [Fact]
    public void A_set_but_missing_loader_names_the_path_that_moved()
    {
        var f = MainWindowViewModel.MigotoFacet(@"C:\gone\Run.exe", loaderExists: false, modsFolder: null, default);

        Assert.Equal("⚠", f.Glyph);
        Assert.Contains(@"C:\gone\Run.exe", f.Detail);
        Assert.Contains("Launch and Install", f.Detail);
    }

    [Fact]
    public void A_loader_with_no_mods_folder_says_launch_still_works()
    {
        var f = MainWindowViewModel.MigotoFacet(@"C:\tools\Run.exe", loaderExists: true, modsFolder: null, Hooked);

        Assert.Equal("⚠", f.Glyph);
        // the label says what the state costs; the Mods folder itself is named in the detail
        Assert.Contains("can't install mods", f.Text);
        Assert.Contains("Mods folder", f.Detail);
        Assert.Contains("Launch still works", f.Detail);
    }

    [Fact]
    public void A_loader_with_its_mods_folder_reads_clean()
    {
        var f = MainWindowViewModel.MigotoFacet(@"C:\3dmigoto\3DMigoto Loader.exe", true, @"C:\3dmigoto\Mods", Hooked);

        Assert.Equal("✓", f.Glyph);
        Assert.Equal("3DMigoto", f.Text);
        Assert.Equal("", f.Detail);
    }

    [Fact]
    public void A_blank_loader_reads_as_unset_not_as_missing()
    {
        Assert.Equal(MainWindowViewModel.MigotoFacet(null, false, null, default),
            MainWindowViewModel.MigotoFacet("   ", false, null, default));
    }

    // ---- the Mods folder beside the loader ------------------------------------

    [Fact]
    public void The_mods_folder_is_found_as_a_sibling_of_the_loader()
    {
        using var g = new TempGame();
        var migoto = Path.Combine(g.Root, "3dmigoto");
        var mods = Path.Combine(migoto, "Mods");
        Directory.CreateDirectory(mods);
        var exe = Path.Combine(migoto, "Run.exe");
        File.WriteAllText(exe, "x");

        Assert.Equal(mods, MigotoLoader.FindModsFolder(exe));
    }

    [Fact]
    public void A_loader_with_nothing_beside_it_yields_no_mods_folder()
    {
        using var g = new TempGame();
        var exe = Path.Combine(g.Root, "Run.exe");
        File.WriteAllText(exe, "x");
        Assert.Null(MigotoLoader.FindModsFolder(exe));
        Assert.Null(MigotoLoader.FindModsFolder(null));
        Assert.Null(MigotoLoader.FindModsFolder(" "));
        // a bare file name has no directory to look beside
        Assert.Null(MigotoLoader.FindModsFolder("Run.exe"));
    }

    // ---- waiting for the loader -----------------------------------------------

    private static MainWindowViewModel.LoaderWait Step(
        bool stubAlive, bool successorRunning, double sinceStart,
        double sinceStubExit = 0, bool stubIsLoader = false, bool stubPidKnown = true) =>
        MainWindowViewModel.LoaderWaitStep(stubIsLoader, stubPidKnown, stubAlive, successorRunning,
            TimeSpan.FromSeconds(sinceStart), TimeSpan.FromSeconds(sinceStubExit));

    [Fact]
    public void A_stub_standing_with_nothing_behind_it_never_starts_the_game()
    {
        // The prompt is up and unanswered: the stub is alive, has hooked nothing, and no elevated copy
        // exists. Every tick short of the ceiling waits — none of them proceeds.
        Assert.Equal(MainWindowViewModel.LoaderWait.KeepWaiting, Step(stubAlive: true, successorRunning: false, 0.5));
        Assert.Equal(MainWindowViewModel.LoaderWait.KeepWaiting, Step(true, false, 5));
        Assert.Equal(MainWindowViewModel.LoaderWait.KeepWaiting, Step(true, false, 30));
        Assert.Equal(MainWindowViewModel.LoaderWait.KeepWaiting, Step(true, false, 59.5));
    }

    [Fact]
    public void An_unanswered_prompt_fails_at_the_ceiling()
    {
        Assert.Equal(MainWindowViewModel.LoaderWait.NotElevated, Step(stubAlive: true, successorRunning: false, 60));
    }

    [Fact]
    public void The_elevated_copy_behind_the_stub_is_what_confirms_the_loader()
    {
        // consent given: the stub spawned a copy under another pid and exits. Either sighting order confirms.
        Assert.Equal(MainWindowViewModel.LoaderWait.Ready, Step(stubAlive: true, successorRunning: true, 1));
        Assert.Equal(MainWindowViewModel.LoaderWait.Ready, Step(false, true, 1, sinceStubExit: 0.5));
        // and a successor found after the grace ran out still beats the failure it would otherwise be
        Assert.Equal(MainWindowViewModel.LoaderWait.Ready, Step(false, true, 30, sinceStubExit: 25));
    }

    [Fact]
    public void A_stub_that_exits_with_nothing_behind_it_fails_after_the_respawn_grace()
    {
        // refused consent looks exactly like the respawn handoff for an instant, so the exit gets a grace…
        Assert.Equal(MainWindowViewModel.LoaderWait.KeepWaiting,
            Step(stubAlive: false, successorRunning: false, 3, sinceStubExit: 1.5));
        // …and once nothing has taken its place, the launch fails rather than starting the game unhooked
        Assert.Equal(MainWindowViewModel.LoaderWait.Stopped, Step(false, false, 3.5, sinceStubExit: 2));
    }

    [Fact]
    public void Only_a_pid_that_was_not_running_before_the_start_is_a_successor()
    {
        // the pids up before the start, plus the stub's own, are what the launch already knows about
        var known = new HashSet<int> { 4242, 4243 };
        Assert.False(MainWindowViewModel.AnyPidOutside(new[] { 4242, 4243 }, known));
        Assert.True(MainWindowViewModel.AnyPidOutside(new[] { 4243, 4244 }, known));
        Assert.False(MainWindowViewModel.AnyPidOutside(Array.Empty<int>(), known));
        // and with nothing known, any pid at all answers — which is how the entry read asks "is one up?"
        Assert.True(MainWindowViewModel.AnyPidOutside(new[] { 4242 }, new HashSet<int>()));
        Assert.False(MainWindowViewModel.AnyPidOutside(Array.Empty<int>(), new HashSet<int>()));
    }

    [Fact]
    public void The_stub_is_the_loader_when_nothing_will_re_launch_it()
    {
        // an elevated app hands its elevation to the child, and an ini without require_admin asks for none:
        // either way no successor is ever coming, so the started process standing is the confirmation
        Assert.Equal(MainWindowViewModel.LoaderWait.Ready,
            Step(stubAlive: true, successorRunning: false, 0.5, stubIsLoader: true));
        // and its exit is still an exit — a loader that quit hooks nothing either
        Assert.Equal(MainWindowViewModel.LoaderWait.Stopped,
            Step(false, false, 3, sinceStubExit: 2, stubIsLoader: true));
        // a start with no readable handle is confirmed by the loader that appeared: nothing else was coming
        Assert.Equal(MainWindowViewModel.LoaderWait.Ready,
            Step(stubAlive: true, successorRunning: true, 0.5, stubIsLoader: true, stubPidKnown: false));
    }

    [Fact]
    public void A_re_launch_with_no_readable_stub_pid_is_confirmed_by_nothing()
    {
        // The start gave back no handle, so the pid that showed up could be the stub itself standing on the
        // prompt. Waiting on a re-launch, that is no evidence at all: the wait holds…
        Assert.Equal(MainWindowViewModel.LoaderWait.KeepWaiting,
            Step(stubAlive: true, successorRunning: true, 30, stubPidKnown: false));
        // …and fails at the ceiling rather than starting the game on a loader nothing confirmed
        Assert.Equal(MainWindowViewModel.LoaderWait.NotElevated,
            Step(stubAlive: true, successorRunning: true, 60, stubPidKnown: false));
    }

    [Fact]
    public void A_started_loader_that_dies_behind_a_successor_still_launches_the_game()
    {
        // The started process is the loader AND it is gone — it forked, or it exited as a duplicate while
        // another copy survives. The wait confirms on the successor, and the re-check for this MODE accepts
        // either evidence: re-testing only the dead handle would refuse a launch with a working loader on
        // screen.
        Assert.Equal(MainWindowViewModel.LoaderWait.Ready,
            Step(stubAlive: false, successorRunning: true, 1, sinceStubExit: 0.5, stubIsLoader: true));
        Assert.True(MainWindowViewModel.LoaderStillConfirmed(MainWindowViewModel.LoaderProof.StartedProcess,
            anyPidNamed: true, successorRunning: true, stubAlive: false));
    }

    // ---- the loader is re-read after the warmup, before the game starts --------

    private static bool StillConfirmed(MainWindowViewModel.LoaderProof proof,
        bool anyPidNamed = false, bool successorRunning = false, bool stubAlive = false) =>
        MainWindowViewModel.LoaderStillConfirmed(proof, anyPidNamed, successorRunning, stubAlive);

    [Fact]
    public void An_already_running_loader_is_re_checked_by_any_process_under_its_name()
    {
        // Nothing was started, so the only evidence there ever was is a process under the loader's name…
        Assert.True(StillConfirmed(MainWindowViewModel.LoaderProof.AlreadyRunning, anyPidNamed: true));
        // …and once that is gone the game must not start: it would come up with nothing hooked.
        Assert.False(StillConfirmed(MainWindowViewModel.LoaderProof.AlreadyRunning,
            successorRunning: true, stubAlive: true));
    }

    [Fact]
    public void An_elevated_copy_is_re_checked_by_a_pid_the_start_did_not_already_know()
    {
        // Re-tested on the terms it was confirmed on. A name match alone is not it: the pids that were up
        // before the start are still up, and reading one of those as the elevated copy is the whole trap.
        Assert.True(StillConfirmed(MainWindowViewModel.LoaderProof.Successor,
            anyPidNamed: true, successorRunning: true));
        Assert.False(StillConfirmed(MainWindowViewModel.LoaderProof.Successor,
            anyPidNamed: true, stubAlive: true));
    }

    [Fact]
    public void A_started_loader_is_re_checked_by_its_own_handle_or_a_successor()
    {
        // The process this app started IS the loader — and so is any pid the start did not already know
        // (a copy that forked, or quit as a duplicate beside a survivor). Either standing confirms; a bare
        // name match proves nothing, since the pids that were up before the start are still up.
        Assert.True(StillConfirmed(MainWindowViewModel.LoaderProof.StartedProcess, stubAlive: true));
        Assert.True(StillConfirmed(MainWindowViewModel.LoaderProof.StartedProcess,
            anyPidNamed: true, successorRunning: true));
        // the mirror case: the foreign copy that confirmed the wait dies while the app's own still stands
        Assert.True(StillConfirmed(MainWindowViewModel.LoaderProof.StartedProcess,
            anyPidNamed: true, stubAlive: true));
        // and with both gone the refusal is real
        Assert.False(StillConfirmed(MainWindowViewModel.LoaderProof.StartedProcess, anyPidNamed: true));
    }

    // ---- require_admin, read from the loader's own ini -------------------------

    [Fact]
    public void An_uncommented_require_admin_is_the_setting_being_set()
    {
        Assert.True(MainWindowViewModel.RequiresAdmin("[Loader]\nrequire_admin = true\n"));
        Assert.True(MainWindowViewModel.RequiresAdmin("[Loader]\r\n  REQUIRE_ADMIN=1\r\n"));
    }

    [Fact]
    public void A_commented_or_absent_require_admin_is_not_set()
    {
        // a commented line is not a setting, however exactly it reads
        Assert.False(MainWindowViewModel.RequiresAdmin("[Loader]\n; require_admin = true\n"));
        Assert.False(MainWindowViewModel.RequiresAdmin("[Loader]\n;require_admin=true\n"));
        // nor is a line that only starts like one
        Assert.False(MainWindowViewModel.RequiresAdmin("[Loader]\nrequire_admin_delay = 1\n"));
        Assert.False(MainWindowViewModel.RequiresAdmin("[Loader]\nlaunch = game.exe\n"));
        Assert.False(MainWindowViewModel.RequiresAdmin(""));
    }

    [Fact]
    public void An_explicit_false_reads_as_no_re_launch()
    {
        Assert.False(MainWindowViewModel.RequiresAdmin("[Loader]\nrequire_admin = false\n"));
        Assert.False(MainWindowViewModel.RequiresAdmin("[Loader]\nrequire_admin = 0\n"));
        // a trailing comment does not flip the value either way
        Assert.False(MainWindowViewModel.RequiresAdmin("[Loader]\nrequire_admin = false ; keep off\n"));
        Assert.True(MainWindowViewModel.RequiresAdmin("[Loader]\nrequire_admin = true ; elevate\n"));
    }

    [Fact]
    public void An_ini_that_is_not_there_reads_as_no_re_launch()
    {
        using var g = new TempGame();
        var migoto = Path.Combine(g.Root, "3dmigoto");
        Directory.CreateDirectory(migoto);
        var exe = Path.Combine(migoto, "Run.exe");
        File.WriteAllText(exe, "x");

        // no ini beside the loader: the launch waits for no elevated copy rather than one that never comes
        Assert.False(MainWindowViewModel.LoaderSelfElevates(exe));
        File.WriteAllText(Path.Combine(migoto, "d3dx.ini"), "[Loader]\nrequire_admin = true\n");
        Assert.True(MainWindowViewModel.LoaderSelfElevates(exe));
    }

    // ---- the game exe ---------------------------------------------------------

    /// <summary>A Unity install root: <c>&lt;Name&gt;_Data</c> beside <c>&lt;Name&gt;.exe</c>, plus the
    /// crash handler a Unity build ships.</summary>
    private static string MakeGameRoot(string parent, string folderName, string baseName = "TestGame")
    {
        var root = Path.Combine(parent, folderName);
        Directory.CreateDirectory(Path.Combine(root, baseName + "_Data"));
        File.WriteAllText(Path.Combine(root, baseName + ".exe"), "x");
        File.WriteAllText(Path.Combine(root, "UnityCrashHandler64.exe"), "x");
        return root;
    }

    [Fact]
    public void The_game_exe_comes_from_the_data_folder_name()
    {
        using var g = new TempGame();
        var root = MakeGameRoot(g.Root, "SomeGame");
        Assert.Equal(Path.Combine(root, "TestGame.exe"), GameLauncher.FindExe(root));
    }

    [Fact]
    public void With_no_matching_data_pair_the_crash_handler_is_skipped()
    {
        using var g = new TempGame();
        var root = Path.Combine(g.Root, "SomeGame");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "UnityCrashHandler64.exe"), "x");
        File.WriteAllText(Path.Combine(root, "TheGame.exe"), "x");
        Assert.Equal(Path.Combine(root, "TheGame.exe"), GameLauncher.FindExe(root));
    }

    [Fact]
    public void A_data_folder_whose_exe_is_gone_does_not_hand_back_the_dump_tool()
    {
        using var g = new TempGame();
        var root = Path.Combine(g.Root, "SomeGame");
        Directory.CreateDirectory(Path.Combine(root, "TestGame_Data"));   // the pair's exe is absent
        File.WriteAllText(Path.Combine(root, "UnityCrashHandler64.exe"), "x");
        File.WriteAllText(Path.Combine(root, "KernelDumpAnalyzer.exe"), "x");

        // Nothing here is the game, so nothing is named: the caller reports no executable rather than
        // starting a diagnostic tool as if it were the game.
        Assert.Null(GameLauncher.FindExe(root));
        Assert.Null(GameLauncher.Resolve(root));

        // and the filter must not eat the game once it IS there
        File.WriteAllText(Path.Combine(root, "TestGame.exe"), "x");
        Assert.Equal(Path.Combine(root, "TestGame.exe"), GameLauncher.FindExe(root));
    }

    [Fact]
    public void With_several_data_folders_the_one_whose_exe_exists_names_the_game()
    {
        using var g = new TempGame();
        var root = Path.Combine(g.Root, "SomeGame");
        // AaaTool_Data sorts first and has no exe; the game's pair is complete. The stray launcher is what
        // the fallback would hand back if a toolless _Data were allowed to shadow the real pair.
        Directory.CreateDirectory(Path.Combine(root, "AaaTool_Data"));
        Directory.CreateDirectory(Path.Combine(root, "TestGame_Data"));
        File.WriteAllText(Path.Combine(root, "AaaLauncher.exe"), "x");
        File.WriteAllText(Path.Combine(root, "TestGame.exe"), "x");
        Assert.Equal(Path.Combine(root, "TestGame.exe"), GameLauncher.FindExe(root));
    }

    [Fact]
    public void With_no_complete_data_pair_at_all_the_fallback_still_runs()
    {
        using var g = new TempGame();
        var root = Path.Combine(g.Root, "SomeGame");
        Directory.CreateDirectory(Path.Combine(root, "AaaTool_Data"));
        Directory.CreateDirectory(Path.Combine(root, "TestGame_Data"));
        File.WriteAllText(Path.Combine(root, "TheGame.exe"), "x");
        Assert.Equal(Path.Combine(root, "TheGame.exe"), GameLauncher.FindExe(root));
    }

    [Fact]
    public void An_absent_root_resolves_to_nothing()
    {
        using var g = new TempGame();
        Assert.Null(GameLauncher.FindExe(Path.Combine(g.Root, "not-here")));
        Assert.Null(GameLauncher.FindExe(null));
        Assert.Null(GameLauncher.Resolve(Path.Combine(g.Root, "not-here")));
    }

    // ---- Steam vs direct ------------------------------------------------------

    /// <summary>A Steam library shape: <c>steamapps/common/&lt;folder&gt;</c>, with the manifests the
    /// caller asks for written into <c>steamapps</c>.</summary>
    private static string MakeSteamRoot(string parent, string folderName, params (string AppId, string InstallDir)[] manifests)
    {
        var steamApps = Path.Combine(parent, "steamapps");
        var common = Path.Combine(steamApps, "common");
        Directory.CreateDirectory(common);
        foreach (var (appId, installDir) in manifests)
            File.WriteAllText(Path.Combine(steamApps, $"appmanifest_{appId}.acf"),
                "\"AppState\"\n{\n\t\"appid\"\t\"" + appId + "\"\n\t\"installdir\"\t\"" + installDir + "\"\n}\n");
        return MakeGameRoot(common, folderName);
    }

    [Fact]
    public void A_steam_install_with_a_matching_manifest_launches_through_steam()
    {
        using var g = new TempGame();
        var root = MakeSteamRoot(g.Root, "Some Game Folder",
            ("111000", "Another Game"), ("480123", "Some Game Folder"));

        var plan = GameLauncher.Resolve(root)!.Value;
        Assert.True(plan.ViaSteam);
        Assert.Equal("steam://rungameid/480123", plan.Target);
        Assert.Null(plan.Note);   // nothing fell back, so nothing to report
        // through Steam the started process is the client's, so the exe is the only thing that names the
        // game's own process. One resolve answers both, or a watch could follow another exe entirely.
        Assert.Equal(Path.Combine(root, "TestGame.exe"), plan.Exe);
        Assert.Equal("TestGame", plan.ProcessName);
    }

    [Fact]
    public void A_steam_shaped_install_with_no_matching_manifest_falls_back_to_the_exe_and_says_so()
    {
        using var g = new TempGame();
        var root = MakeSteamRoot(g.Root, "Some Game Folder", ("111000", "Another Game"));

        var plan = GameLauncher.Resolve(root)!.Value;
        Assert.False(plan.ViaSteam);
        Assert.Equal(Path.Combine(root, "TestGame.exe"), plan.Target);
        Assert.Contains("Steam install", plan.Note!);
    }

    [Fact]
    public void A_standalone_install_launches_the_exe_with_nothing_to_report()
    {
        using var g = new TempGame();
        var root = MakeGameRoot(Path.Combine(g.Root, "Games"), "Some Game Folder");

        var plan = GameLauncher.Resolve(root)!.Value;
        Assert.False(plan.ViaSteam);
        Assert.Equal(Path.Combine(root, "TestGame.exe"), plan.Target);
        Assert.Null(plan.Note);
        Assert.Equal(plan.Target, plan.Exe);   // launched directly: the target IS the exe
        Assert.Equal("TestGame", plan.ProcessName);
    }

    [Fact]
    public void The_appid_is_read_from_the_manifest_and_matched_case_insensitively()
    {
        var (appId, installDir) = GameLauncher.ParseAppManifest(
            "\"AppState\"\n{\n\t\"appid\"\t\"480123\"\n\t\"installdir\"\t\"Some Game Folder\"\n}\n");
        Assert.Equal("480123", appId);
        Assert.Equal("Some Game Folder", installDir);

        Assert.Equal("480123", GameLauncher.MatchAppId("SOME GAME FOLDER", new[] { (appId, installDir) }));
        Assert.Null(GameLauncher.MatchAppId("Another Folder", new[] { (appId, installDir) }));
        // a manifest missing either key can't name an install — it must not match by default
        Assert.Null(GameLauncher.MatchAppId("Some Game Folder", new[] { ((string?)null, installDir) }));
    }

    // ---- the game watch: which pid the launch may follow ----------------------

    [Fact]
    public void The_watch_follows_only_a_pid_the_start_brought_up()
    {
        var before = new HashSet<int> { 10, 20 };

        // nothing new in the table: there is no process this launch can claim
        Assert.Equal(MainWindowViewModel.NoPid,
            MainWindowViewModel.FirstPidOutside(new[] { 10, 20 }, before));
        // and an empty table says the same
        Assert.Equal(MainWindowViewModel.NoPid,
            MainWindowViewModel.FirstPidOutside(Array.Empty<int>(), before));

        // the one pid the pre-start read did not carry is the one to follow
        Assert.Equal(30, MainWindowViewModel.FirstPidOutside(new[] { 10, 30, 20 }, before));
    }

    [Fact]
    public void The_pid_answer_and_the_loader_wait_ask_one_question()
    {
        // the loader wait only needs to know one exists; the game watch needs which one. Two readings of
        // one rule, so a pid the wait accepts can never be one the watch declines.
        var before = new HashSet<int> { 7 };
        foreach (var pids in new[] { new[] { 7 }, new[] { 7, 8 }, Array.Empty<int>() })
            Assert.Equal(MainWindowViewModel.FirstPidOutside(pids, before) != MainWindowViewModel.NoPid,
                MainWindowViewModel.AnyPidOutside(pids, before));
    }

    [Fact]
    public void With_two_candidates_the_watch_picks_the_same_one_every_read()
    {
        // The pids come out of a set, whose order is nobody's contract. Two candidates picked by
        // enumeration order would have consecutive reads follow different processes.
        var before = new HashSet<int> { 10 };
        Assert.Equal(30, MainWindowViewModel.FirstPidOutside(new[] { 50, 30, 10, 44 }, before));
        Assert.Equal(30, MainWindowViewModel.FirstPidOutside(new[] { 30, 44, 50, 10 }, before));
        Assert.Equal(30, MainWindowViewModel.FirstPidOutside(new[] { 44, 50, 10, 30 }, before));
    }

    // ---- a launch behind a host that already started the game -------------------

    /// <summary>The one state where the whole sequence starts nothing: the host owns the game start, it was
    /// already up when the button was pressed, and the game was already among the pids read at the entry.
    /// Anything else still has a start to make, or a snapshot that tells this launch's copy from an older
    /// one.</summary>
    [Fact]
    public void A_host_already_up_with_the_game_already_running_has_nothing_left_to_start()
    {
        Assert.True(MainWindowViewModel.HostAlreadyStartedGame(
            loaderWasAlreadyRunning: true, gameAlreadyUp: true));

        // this launch started the loader, so the game it starts is still coming
        Assert.False(MainWindowViewModel.HostAlreadyStartedGame(
            loaderWasAlreadyRunning: false, gameAlreadyUp: true));
        // the host was up but has no game running yet — the start is the host's to make, and the watch has
        // a pid to wait for
        Assert.False(MainWindowViewModel.HostAlreadyStartedGame(
            loaderWasAlreadyRunning: true, gameAlreadyUp: false));
        Assert.False(MainWindowViewModel.HostAlreadyStartedGame(
            loaderWasAlreadyRunning: false, gameAlreadyUp: false));
    }

    /// <summary>Nothing was started, so the watch is handed an EMPTY snapshot and the game already running
    /// is the pid it adopts — the same rule the watch uses behind a real start, asked of nothing.</summary>
    [Fact]
    public void With_nothing_started_the_watch_adopts_the_game_that_is_already_up()
    {
        var running = new[] { 4242 };

        Assert.Equal(4242, MainWindowViewModel.FirstPidOutside(running, new HashSet<int>()));
        // …where following the entry snapshot would have found nothing, for the whole appear window
        Assert.Equal(MainWindowViewModel.NoPid,
            MainWindowViewModel.FirstPidOutside(running, new HashSet<int>(running)));
    }

    /// <summary>The two lines a host that starts the game itself can end on say different things: one is a
    /// start on its way, the other is a state already reached.</summary>
    [Fact]
    public void A_launch_that_started_nothing_does_not_claim_a_start_is_coming()
    {
        Assert.Equal("Game is already running", MainWindowViewModel.GameAlreadyStartedLine.Text);
        Assert.Equal("3DMigoto is starting the game", MainWindowViewModel.LoaderStartsGameLine.Text);
    }

    // ---- a pid that dies right after appearing ---------------------------------

    [Fact]
    public void A_direct_exe_start_the_launcher_answers_with_its_own_copy_is_waited_past_once()
    {
        // Steam relaunches an exe started outside it, so the pid the watch latches dies seconds later and
        // the real game comes up under another. Refreshing there fires at the wrong moment AND leaves the
        // game that IS running unwatched.
        Assert.True(MainWindowViewModel.ShouldReArm(TimeSpan.FromSeconds(3),
            GameLauncher.LaunchKind.DirectExe, alreadyReArmed: false));
        // …but only once: a second handoff is indistinguishable from a game that keeps dying on startup,
        // and that one has to reach the cell
        Assert.False(MainWindowViewModel.ShouldReArm(TimeSpan.FromSeconds(3),
            GameLauncher.LaunchKind.DirectExe, alreadyReArmed: true));
    }

    [Fact]
    public void A_game_that_ran_and_then_quit_is_an_exit_whatever_started_it()
    {
        // past the grace nothing is a handoff any more — this is the modder closing the game
        Assert.False(MainWindowViewModel.ShouldReArm(TimeSpan.FromSeconds(30),
            GameLauncher.LaunchKind.DirectExe, alreadyReArmed: false));
        Assert.False(MainWindowViewModel.ShouldReArm(TimeSpan.FromMinutes(20),
            GameLauncher.LaunchKind.DirectExe, alreadyReArmed: false));
        // and through a steam:// target the client owns the single start: the pid that appears is the
        // game's own, so a quick exit is a quick exit
        Assert.False(MainWindowViewModel.ShouldReArm(TimeSpan.FromSeconds(3),
            GameLauncher.LaunchKind.Steam, alreadyReArmed: false));
    }

    // ---- what the game's exit re-reads ----------------------------------------

    [Fact]
    public void A_healthy_load_has_nothing_for_the_exit_to_unblock()
    {
        // The re-read exists to retake reads the running game refused. A load that read everything has no
        // such reads, and the reload it would run drops the VFS, the sharing measurement and both trees.
        Assert.Equal(MainWindowViewModel.ExitAction.None,
            MainWindowViewModel.ExitReRead(loadEndedBlocked: false, rescanMustWait: false));
        Assert.Equal(MainWindowViewModel.ExitAction.None,
            MainWindowViewModel.ExitReRead(loadEndedBlocked: false, rescanMustWait: true));
    }

    [Fact]
    public void A_load_that_ended_blocked_is_re_read_once_nothing_is_holding_the_roster()
    {
        Assert.Equal(MainWindowViewModel.ExitAction.Now,
            MainWindowViewModel.ExitReRead(loadEndedBlocked: true, rescanMustWait: false));
        // a build or a materialize reads the very state the reload drops, so the re-read queues behind it
        Assert.Equal(MainWindowViewModel.ExitAction.Queue,
            MainWindowViewModel.ExitReRead(loadEndedBlocked: true, rescanMustWait: true));
    }


    [Fact]
    public void An_exit_with_nothing_to_re_read_writes_no_notice_at_all()
    {
        // the queued line describes a wait; a healthy session has none, and a notice about work that isn't
        // happening is worse than silence
        var quiet = StatusFacet.Warn("Something else", "still true");
        var vm = new MainWindowViewModel(startLoad: false)
        {
            IsScanning = false, GameRescanOffered = false, NoticeStatus = quiet,
        };

        vm.RefreshAfterGameExit();

        Assert.Equal(quiet, vm.NoticeStatus);
    }

    [Fact]
    public void Notice_merges_dedupe_by_identity_not_rendered_text()
    {
        var vm = new MainWindowViewModel(startLoad: false);

        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            "same-cause", "first wording", "first detail"));
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            "same-cause", "updated wording", "updated detail"));

        Assert.Equal("updated wording", vm.NoticeStatus.Text);
        Assert.Equal("updated detail", vm.NoticeStatus.Detail);
    }

    [Fact]
    public void A_merged_notice_uses_its_highest_member_severity_and_an_honest_label()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            "warning", "warning", "warning detail"));
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            "another-warning", "another warning", "another warning detail"));

        Assert.Equal("2 warnings", vm.NoticeStatus.Text);

        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            "error", "error", "error detail",
            Severity: MainWindowViewModel.NoticeSeverity.Error));

        Assert.Equal("✗", vm.NoticeStatus.Glyph);
        Assert.Equal(StatusFacet.Danger, vm.NoticeStatus.Color);
        Assert.Equal("3 notices", vm.NoticeStatus.Text);
        Assert.Contains("warning detail", vm.NoticeStatus.Detail);
        Assert.Contains("error detail", vm.NoticeStatus.Detail);
    }

    [Fact]
    public void Load_finalize_replaces_only_load_notices_and_preserves_project_migration()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            MainWindowViewModel.ProjectMigrationNoticeId, "Project updated", "migration detail",
            ProjectScoped: true, Severity: MainWindowViewModel.NoticeSeverity.Info));
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            "game.locate", "Locate the game", "stale load detail"));

        vm.ReplaceLoadNotices(new[]
        {
            new MainWindowViewModel.NoticeMessage(
                "game.files-missing", "1 game file missing", "load detail"),
        });

        Assert.Equal("2 notices", vm.NoticeStatus.Text);
        Assert.Contains("migration detail", vm.NoticeStatus.Detail);
        Assert.Contains("load detail", vm.NoticeStatus.Detail);
        Assert.DoesNotContain("stale load detail", vm.NoticeStatus.Detail);

        vm.ReplaceLoadNotices(Array.Empty<MainWindowViewModel.NoticeMessage>());

        Assert.Equal("Project updated", vm.NoticeStatus.Text);
        Assert.Equal("migration detail", vm.NoticeStatus.Detail);
        Assert.Equal("✓", vm.NoticeStatus.Glyph);
    }

    [Fact]
    public void A_project_switch_clears_project_notices_and_preserves_app_notices()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            "settings", "settings warning", "settings detail"));
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            MainWindowViewModel.AuthoredAgainstNoticeId, "made for a different game version",
            "stale project detail", ProjectScoped: true));

        vm.NewMod();

        Assert.Equal("settings warning", vm.NoticeStatus.Text);
        Assert.Equal("settings detail", vm.NoticeStatus.Detail);
    }

    [Fact]
    public void Retiring_the_authored_against_notice_preserves_unrelated_notices()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            "game", "game warning", "game detail"));
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            MainWindowViewModel.AuthoredAgainstNoticeId, "made for a different game version",
            "stale project detail", ProjectScoped: true));

        vm.RemoveNotice(MainWindowViewModel.AuthoredAgainstNoticeId);

        Assert.Equal("game warning", vm.NoticeStatus.Text);
        Assert.Equal("game detail", vm.NoticeStatus.Detail);
    }

    [Fact]
    public async Task A_project_switch_between_the_build_task_and_continuation_stamps_nothing()
    {
        using var game = new TempGame();
        string root = game.At("switchable-mod");
        Directory.CreateDirectory(root);
        var project = AuthoredEditFixtures.Golden();
        project.RootDir = root;
        project.AuthoredAgainst = null;
        AuthoredProjectSerializer.Save(project, root);
        var vm = new MainWindowViewModel(startLoad: false, pageDispatch: work => work());
        Assert.True(await vm.OpenModAsync(root));
        var builtSession = vm.EditSession;
        long builtRevision = builtSession.Revision;
        var buildTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continuation = buildTask.Task.ContinueWith(_ =>
            vm.StampSuccessfulBuild(builtSession, builtRevision, "26109"));

        vm.NewMod();
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            MainWindowViewModel.AuthoredAgainstNoticeId, "current notice", "current detail",
            ProjectScoped: true));
        var currentNotice = vm.NoticeStatus;
        buildTask.SetResult();

        Assert.Equal(builtRevision, await continuation);
        Assert.Null(builtSession.Snapshot().AuthoredAgainst);
        Assert.Null(AuthoredProjectSerializer.Load(root).AuthoredAgainst);
        Assert.Equal(currentNotice, vm.NoticeStatus);
    }

    [Fact]
    public void An_unknown_live_catalog_neither_stamps_nor_retires_the_notice()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        var session = vm.EditSession;
        session.SetAuthoredAgainst("24535");
        long revision = session.Revision;
        vm.MergeNoticeIntoCell(new MainWindowViewModel.NoticeMessage(
            MainWindowViewModel.AuthoredAgainstNoticeId, "made for a different game version",
            "stale project detail", ProjectScoped: true));
        var notice = vm.NoticeStatus;

        long builtRevision = vm.StampSuccessfulBuild(session, revision, GameInfo.UnknownVersion);

        Assert.Equal(revision, builtRevision);
        Assert.Equal(revision, session.Revision);
        Assert.Equal("24535", session.Snapshot().AuthoredAgainst?.CatalogVersion);
        Assert.Equal(notice, vm.NoticeStatus);
    }

    // ---- what the exit puts back on the header --------------------------------

    [Fact]
    public void The_watched_games_exit_retires_this_launchs_own_line()
    {
        // "Game launched" over a closed game is a header describing a state that ended
        var landed = StatusFacet.Good("Game launched");
        var beforeWatch = StatusFacet.Good("Game");
        var vm = new MainWindowViewModel(startLoad: false)
        {
            IsScanning = false, GameRescanOffered = false, LaunchStatus = landed,
            GameStatus = StatusFacet.Warn(MainWindowViewModel.GameRunningLabel),
        };

        vm.WatchedGameExited(beforeWatch, landed);

        Assert.Equal(StatusFacet.None, vm.LaunchStatus);
        Assert.Equal(beforeWatch, vm.GameStatus);
    }

    [Fact]
    public void A_line_written_since_the_launch_survives_the_exit()
    {
        // the exit retires what THIS launch wrote, never whatever happened after it
        var landed = StatusFacet.Warn("Game launched", "Steam install with no matching manifest.");
        var newer = StatusFacet.Bad("3DMigoto didn't start", "…");
        var vm = new MainWindowViewModel(startLoad: false)
        {
            IsScanning = false, GameRescanOffered = false, LaunchStatus = newer,
        };

        vm.WatchedGameExited(StatusFacet.Good("Game"), landed);

        Assert.Equal(newer, vm.LaunchStatus);
    }

    // ---- the Launch button while the game it started is up --------------------

    [Fact]
    public void A_live_watch_turns_the_launch_button_off_and_says_why()
    {
        // 3DMigoto hooks the game as it starts and cannot attach to one already up, so a second launch has
        // nothing to offer — and a button that greys out without a reason reads as a dead one.
        var vm = new MainWindowViewModel(startLoad: false) { IsWatchingLaunchedGame = true };

        Assert.False(vm.CanLaunchGame);
        Assert.Equal(MainWindowViewModel.GameAlreadyRunning, vm.LaunchDisabledReason);
        Assert.Equal(MainWindowViewModel.GameAlreadyRunning, vm.LaunchButtonTip);

        // and once the watch ends the button falls back to whatever the configuration gates say
        vm.IsWatchingLaunchedGame = false;
        Assert.NotEqual(MainWindowViewModel.GameAlreadyRunning, vm.LaunchButtonTip);
    }

    [Fact]
    public void A_running_game_says_on_the_cell_what_its_open_files_cost()
    {
        // the label alone reports the state; the tooltip is why a build or a preview may fail while it stands
        var f = StatusFacet.Warn(MainWindowViewModel.GameRunningLabel, MainWindowViewModel.GameRunningDetail);

        Assert.Equal("⚠", f.Glyph);
        Assert.Contains("Builds and previews may fail", f.Detail);
    }

    [Fact]
    public void The_exit_of_a_game_launched_from_a_healthy_session_changes_nothing()
    {
        // the wiring, not the rule: a reload here would empty the trees and clear the search under a modder
        // who was working the whole time the game ran
        var vm = new MainWindowViewModel(startLoad: false)
        {
            IsScanning = false,
            GameRescanOffered = false,   // the load read the install fine
            SearchText = "vesna",
        };

        vm.RefreshAfterGameExit();

        Assert.Equal("vesna", vm.SearchText);   // ReloadRoster clears it
        Assert.False(vm.IsScanning);            // ReloadRoster sets it
    }

    // ---- what the header keeps ------------------------------------------------

    [Fact]
    public void A_landed_launch_line_goes_out_with_the_workspace()
    {
        // launching is app-level, but a landed ✓ / ✗ describes a game start from the sitting being left
        var vm = new MainWindowViewModel(startLoad: false) { LaunchStatus = StatusFacet.Good("Game launched") };

        vm.NewMod();   // the route Close Mod and Open Mod both take back to a clean workspace

        Assert.Equal(StatusFacet.None, vm.LaunchStatus);
    }
}
