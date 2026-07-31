using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remold.Core;
using Remold.Core.Migoto;

namespace Remold.App.ViewModels;

/// <summary>
/// The header's Launch action: 3DMigoto first, then the game. 3DMigoto hooks the game process as it starts
/// and cannot attach to one already running, so the order is the whole point of the button.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>How long the confirmed loader gets before the game starts. 3DMigoto publishes no readiness
    /// signal a caller can wait on, so the gap is a fixed wait. Measured from the confirmation, never from
    /// the un-elevated stub: a stub standing on the UAC prompt has hooked nothing.</summary>
    private static readonly TimeSpan LoaderWarmup = TimeSpan.FromSeconds(5);

    /// <summary>How often the wait re-checks the loader's processes.</summary>
    private static readonly TimeSpan LoaderPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>The window the stub's exit gets to be followed by its elevated replacement. Both the exit and
    /// the replacement's first appearance are read from separate polls, so a gone stub is only a failure once
    /// nothing has taken its place by here.</summary>
    private static readonly TimeSpan LoaderRespawnGrace = TimeSpan.FromSeconds(2);

    /// <summary>The wait's ceiling: how long a stub that is still standing gets to be answered. It is the UAC
    /// prompt being waited on, so the ceiling is sized for a person reading a dialog, not for a warmup.</summary>
    private static readonly TimeSpan LoaderWaitLimit = TimeSpan.FromSeconds(60);

    /// <summary>Whether the configured loader exe is on disk — the read behind the gates' "not found"
    /// reason, re-taken whenever a gate is raised.</summary>
    private bool _loaderExeExists;

    /// <summary>The <c>Mods\</c> folder beside the configured loader, or null when there is none — the
    /// Install half of the same reads.</summary>
    private string? _modsFolder;

    /// <summary>What the configured loader's ini tree says it supports — the texture hook a built mod fires
    /// through, chief among them. Taken with the other loader reads rather than on every binding
    /// evaluation.</summary>
    private MigotoIniFacts _loaderIni;

    /// <summary>What the launch is doing, or why it failed. Its own channel beside the button: the status
    /// bar's notice cell carries the load's warnings, which a launch must not overwrite.</summary>
    [ObservableProperty] private StatusFacet _launchStatus = StatusFacet.None;

    /// <summary>Rides every terminal launch failure short of the game's own start: which stage failed says
    /// nothing about whether the game came up, and the header shows the label alone. "Game didn't launch"
    /// already says it and takes none.</summary>
    private const string NotLaunched = " · game not launched";

    /// <summary>A launch is in flight — the button stays off until it finishes, so a second click can't
    /// start a second loader (3DMigoto refuses one anyway, with its own error).</summary>
    [ObservableProperty] private bool _isLaunching;

    partial void OnIsLaunchingChanged(bool value) => OnPropertyChanged(nameof(CanLaunchGame));

    /// <summary>Why the Launch action is off while a watch follows a live game. 3DMigoto hooks the game as it
    /// starts and cannot attach to one already up, so a second launch behind this one has nothing to
    /// offer.</summary>
    internal const string GameAlreadyRunning = "The game is already running.";

    /// <summary>Why the Launch action is off, or null when it can run. A game this app is watching outranks
    /// the configuration gates: those describe what a launch would need, and this one is why there is nothing
    /// to launch.</summary>
    public string? LaunchDisabledReason =>
        IsWatchingLaunchedGame ? GameAlreadyRunning
        : LaunchGate.Reason(GameDir.Length > 0, _settings.MigotoLoaderExe, _loaderExeExists);
    public bool CanLaunchGame => !IsLaunching && LaunchDisabledReason is null;
    /// <summary>What Launch does, or why it's off (shown on the disabled button).</summary>
    public string LaunchButtonTip => LaunchDisabledReason ?? LaunchGate.Ready;

    /// <summary>The disk reads both loader gates stand on, taken when the gates are raised rather than
    /// on every binding evaluation. The loader is user-set only — nothing detects it. The ini read is the
    /// expensive one of the three: a measured SSMT profile splits its configuration across a couple of dozen
    /// small files, so the walk opens that many rather than the one the two existence checks suggest. They
    /// are small and they sit beside the exe, and this runs on a gate raise rather than on a binding.</summary>
    private void ReadModsFolderState()
    {
        var exe = _settings.MigotoLoaderExe;
        _loaderExeExists = exe is { } e && e.Trim().Length > 0 && File.Exists(e);
        _modsFolder = MigotoLoader.FindModsFolder(exe);
        _loaderIni = _loaderExeExists ? MigotoIni.Read(exe) : default;
    }

    /// <summary>The status bar's 3DMigoto cell, reflecting the configured loader. Recomputed by
    /// <see cref="RaiseModsFolderGates"/> off the same disk reads the gates stand on.</summary>
    [ObservableProperty] private StatusFacet _migotoStatus = StatusFacet.Loading("3DMigoto …");

    /// <summary>What the loader's state reads as on the status bar. The warn tone says something is
    /// missing, never that the app is blocked: Install and Launch are the only two things that want the
    /// loader, and a mod can be picked, edited and built without ever setting one. Pure — the disk reads
    /// are the caller's.</summary>
    internal static StatusFacet MigotoFacet(string? loaderExe, bool loaderExists, string? modsFolder,
        MigotoIniFacts ini) =>
        string.IsNullOrWhiteSpace(loaderExe)
            ? StatusFacet.Warn("3DMigoto · not set",
                "Set the 3DMigoto loader in Settings. Needed for Install and Launch only.")
        : !loaderExists
            ? StatusFacet.Warn("3DMigoto · loader missing",
                $"{InstallGate.LoaderNotFound(loaderExe)} Needed for Install and Launch only.")
        : modsFolder is null
            ? StatusFacet.Warn("3DMigoto · no Mods folder",
                $"{InstallGate.NoModsFolder(loaderExe)}. Install stays off; Launch still works.")
        : !ini.Found
            ? StatusFacet.Warn("3DMigoto · no ini",
                $"{InstallGate.NoLoaderIni(loaderExe)}. Install stays off; Launch still works.")
        : !ini.HasTextureHook
            ? StatusFacet.Warn("3DMigoto · no texture hook",
                $"{InstallGate.NoTextureHook} Install stays off; Launch still works.")
            : StatusFacet.Good("3DMigoto");

    /// <summary>Re-take the loader disk reads and re-raise BOTH gates standing on them — Install and
    /// Launch — plus the status-bar cell. ONE raiser for one pair of reads: re-raising only one gate
    /// would leave the other rendering a reason the reads had outgrown.</summary>
    private void RaiseModsFolderGates()
    {
        ReadModsFolderState();
        MigotoStatus = MigotoFacet(_settings.MigotoLoaderExe, _loaderExeExists, _modsFolder, _loaderIni);
        OnPropertyChanged(nameof(LaunchDisabledReason));
        OnPropertyChanged(nameof(CanLaunchGame));
        OnPropertyChanged(nameof(LaunchButtonTip));
        OnPropertyChanged(nameof(InstallDisabledReason));
        OnPropertyChanged(nameof(CanInstallBuild));
        OnPropertyChanged(nameof(InstallButtonTip));
        // whether the pane shows Install at all, or the way to set the path in its place
        OnPropertyChanged(nameof(HasMigotoLoader));
        OnPropertyChanged(nameof(NeedsMigotoLoader));
        OnPropertyChanged(nameof(SetMigotoPathLabel));
        OnPropertyChanged(nameof(SetMigotoPathTip));
    }

    /// <summary>Start 3DMigoto, wait for it, then start the game. A Steam-library install starts through
    /// <c>steam://rungameid/&lt;appid&gt;</c> so Steam owns the single launch; a standalone install starts
    /// the exe directly. Each stage reports on <see cref="LaunchStatus"/>; a failing stage stops the
    /// sequence and names itself.
    ///
    /// <para>When the loader's <c>d3dx.ini</c> sets <c>require_admin</c>, the started process is NOT the
    /// loader but a stub that re-launches itself through <c>runas</c>: on consent it spawns the elevated
    /// loader under a new pid and exits, on refusal it exits with nothing behind it, and while the prompt
    /// stands it lives on having hooked nothing. Readiness is therefore a same-name process under a pid
    /// that was not running before the start (name enumeration needs no elevation); the stub's own
    /// liveness is only evidence of an unanswered prompt. Without that setting — or from an
    /// already-elevated app — nothing re-launches, so the started process IS the loader and its liveness
    /// is the confirmation.</para>
    ///
    /// <para>Process starts, the launch-plan resolve and the loader's own ini read all run off the UI
    /// thread: a shell execute blocks for as long as the UAC prompt stands, and the window must keep
    /// painting.</para>
    ///
    /// <para>A host whose ini carries an active <c>launch</c> starts the game ITSELF the moment it is up, so
    /// this starts only the loader. Starting the game as well would bring up a second copy — or hand the
    /// first one to a launcher that already owns it — and the modder would be looking at two windows with no
    /// idea which one is hooked.</para></summary>
    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        if (!CanLaunchGame) return;
        string loader = _settings.MigotoLoaderExe!;
        string loaderName = Path.GetFileNameWithoutExtension(loader);
        string gameDir = GameDir;
        if (await Task.Run(() => GameLauncher.Resolve(gameDir)) is not { } plan)
        {
            LaunchStatus = StatusFacet.Bad($"No game executable{NotLaunched}",
                $"No game executable under {gameDir}. Use Tools · Locate game… to point at the install.");
            return;
        }

        IsLaunching = true;
        Process? migoto = null;
        try
        {
            LaunchStatus = StatusFacet.Loading("Starting 3DMigoto…");
            // Read before anything starts: it decides whether this launch starts the game at all, and with
            // it WHEN the game's pids have to be read. A host that starts the game itself can have it up
            // before the warmup ends, so the snapshot the watch measures against is taken here — after the
            // loader is running it would already carry the game this launch brought up, and the watch would
            // follow nothing.
            bool loaderStartsGame = await Task.Run(() => MigotoIni.Read(loader).StartsTheGame);
            string gameName = plan.ProcessName;
            var gameBefore = loaderStartsGame
                ? await Task.Run(() => PidsNamed(gameName))
                : null;
            // The loader's pids as they stand BEFORE anything is started: both the entry read and the set the
            // wait's successor test excludes. One read, so nothing that appears alongside the start can pass
            // for the elevated copy.
            var before = await Task.Run(() => PidsNamed(loaderName));
            LoaderProof proof;
            // A loader already up needs no second start — one would only hand the wait a stub it cannot
            // tell apart from the running loader. Under require_admin a foreign stub standing on a prompt
            // is accepted here as a loader; without it, a live loader process is a working one.
            if (before.Count > 0)
            {
                LaunchStatus = StatusFacet.Loading("3DMigoto already running. Warming up…");
                proof = LoaderProof.AlreadyRunning;
            }
            else
            {
                // Whether a re-launch is coming at all decides what the started process is, so it is read
                // before the start rather than assumed.
                bool stubIsLoader = IsElevated() || !await Task.Run(() => LoaderSelfElevates(loader));
                try
                {
                    // Its own folder as the working directory: the loader reads its ini from there.
                    migoto = await Task.Run(() => Process.Start(new ProcessStartInfo(loader)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(loader)!,
                    }));
                }
                catch (Exception e)
                {
                    LaunchStatus = StatusFacet.Bad($"3DMigoto didn't start{NotLaunched}", $"{loader} · {e.Message}");
                    return;
                }

                LaunchStatus = StatusFacet.Loading(stubIsLoader
                    ? "Starting 3DMigoto…"
                    : "Waiting for the Windows permission prompt…");
                switch (await WaitForLoaderAsync(loaderName, migoto, before, stubIsLoader))
                {
                    case LoaderWait.Stopped:
                        // 3DMigoto hooks the game as it starts, so no loader up means the game would come up
                        // unmodded. Say so instead of launching it.
                        KillStartedProcess(migoto);
                        LaunchStatus = StatusFacet.Bad($"3DMigoto stopped{NotLaunched}",
                            "Closed before the game started. The game was not launched.");
                        return;
                    case LoaderWait.NotElevated:
                        KillStartedProcess(migoto);
                        LaunchStatus = StatusFacet.Bad($"3DMigoto didn't elevate{NotLaunched}",
                            "The Windows permission prompt went unanswered. The game was not launched.");
                        return;
                }

                LaunchStatus = StatusFacet.Loading("3DMigoto is up. Warming up…");
                // The proof is the launch MODE, not whichever Ready rule happened to fire first: when the
                // started process is itself a loader, so is any successor under its name (a copy that forks
                // and exits, or quits as a duplicate beside a survivor), and the re-check accepts either.
                proof = stubIsLoader ? LoaderProof.StartedProcess : LoaderProof.Successor;
            }

            // The loader publishes no hook-ready signal, so the warmup is a fixed pause on a CONFIRMED loader.
            await Task.Delay(LoaderWarmup);

            // The warmup is a window the loader can die in, and nothing else reads the process table between
            // the confirmation and the game's start. A game started behind a dead loader comes up unmodded
            // under a ✓, so the confirmation is re-taken here on the same terms it was made.
            var known = KnownPids(before, StubPid(migoto));
            var still = await Task.Run(() => PidsNamed(loaderName));
            if (!LoaderStillConfirmed(proof, still.Count > 0, AnyPidOutside(still, known),
                    migoto is null || StartedHandleAlive(migoto)))
            {
                KillStartedProcess(migoto);
                LaunchStatus = StatusFacet.Bad($"3DMigoto stopped{NotLaunched}",
                    "Closed before the game started. The game was not launched.");
                return;
            }

            // A host that starts the game itself, that was ALREADY up when the button was pressed, with the
            // game already among the pids read at the entry: this launch starts nothing, and the copy on
            // screen is the one that host started.
            bool gameAlreadyUp = HostAlreadyStartedGame(proof == LoaderProof.AlreadyRunning,
                gameBefore is { Count: > 0 });

            StatusFacet launched;
            if (loaderStartsGame)
            {
                launched = gameAlreadyUp ? GameAlreadyStartedLine : LoaderStartsGameLine;
            }
            else
            {
                LaunchStatus = StatusFacet.Loading("Launching the game…");
                // The game's pids as they stand BEFORE the start, so a copy that was already up cannot pass
                // for the one this launch brings. Read here rather than at the entry: the loader wait sits
                // between, and a game started during it is not this launch's.
                gameBefore = await Task.Run(() => PidsNamed(gameName));
                try
                {
                    (await Task.Run(() => Process.Start(new ProcessStartInfo(plan.Target) { UseShellExecute = true })))
                        ?.Dispose();
                }
                catch (Exception e)
                {
                    LaunchStatus = StatusFacet.Bad("Game didn't launch", $"{plan.Target} · {e.Message}");
                    return;
                }
                launched = plan.Note is { } note
                    ? StatusFacet.Warn("Game launched", note)
                    : StatusFacet.Good("Game launched");
            }
            LaunchStatus = launched;
            // The start hands back no game process — through Steam it is the client's, and a host that
            // starts the game itself never hands one over at all — so the cell follows the game by NAME
            // among the pids the snapshot did not carry. The line just written goes with it, so the exit can
            // retire it without clobbering a newer one.
            //
            // When nothing was started the snapshot is EMPTY, which makes the game already up the pid the
            // watch adopts: the cell reads running at once, and the exit re-reads the install the same way
            // it does behind a start of this app's own. Following the entry snapshot instead would hunt for
            // its whole appear window for a process that is never coming.
            if (gameName.Length > 0)
                _ = WatchLaunchedGameAsync(gameName,
                    gameAlreadyUp ? new HashSet<int>() : gameBefore ?? new HashSet<int>(),
                    WatchedStartKind(plan.Kind, loaderStartsGame), launched);
        }
        finally
        {
            migoto?.Dispose();
            IsLaunching = false;
        }
    }

    /// <summary>What the launch reports for a host that starts the game itself: this app started only the
    /// loader, and the game is on its way from somewhere the app doesn't drive. The label says the state
    /// rather than claiming a start this app didn't make; the detail says why nothing else is coming, so the
    /// wait doesn't read as the button having done half its job.</summary>
    internal static StatusFacet LoaderStartsGameLine => StatusFacet.Good("3DMigoto is starting the game")
        with { Detail = "This 3DMigoto starts the game itself, so the Lab didn't start a second copy." };

    /// <summary>What the launch reports when the whole sequence had nothing left to do: a host that starts
    /// the game itself was already up, and the game it started is already running. Saying it is STARTING
    /// would promise something no one is going to do.</summary>
    internal static StatusFacet GameAlreadyStartedLine => StatusFacet.Good("Game is already running")
        with
        {
            Detail = "This 3DMigoto starts the game itself and was already running, so both were already up.",
        };

    /// <summary>Whether a launch behind a host that starts the game itself has anything left to start. The
    /// two readings are taken BEFORE anything is started: a loader that was already up had already had its
    /// chance to start the game, and a game standing at that same moment is the copy it started. Nothing in
    /// the sequence starts a process in that state, so the launch reports what is running rather than
    /// announcing a start and then watching its whole appear window for a pid that never comes.
    ///
    /// <para>Only asked where the HOST owns the game start. When this app starts the game, the snapshot is
    /// what tells its own copy from one that was already up, and a game already running is no reason not to
    /// start the one the modder just asked for.</para></summary>
    internal static bool HostAlreadyStartedGame(bool loaderWasAlreadyRunning, bool gameAlreadyUp) =>
        loaderWasAlreadyRunning && gameAlreadyUp;

    // ---- the game this app launched -----------------------------------------------------------------

    /// <summary>What the status bar's Game cell reads while the game is up. ONE wording for one state: the
    /// load reaches it by finding the install's files held open, this watch by the process it started.</summary>
    internal const string GameRunningLabel = "Game · running";

    /// <summary>How long the watch gives the game's own process to appear. A Steam launch goes through the
    /// client first, and the client may still have an update to apply before the game runs at all.</summary>
    private static readonly TimeSpan GameAppearLimit = TimeSpan.FromMinutes(5);

    /// <summary>How often the game watch re-reads the process table.</summary>
    private static readonly TimeSpan GameWatchPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long a followed pid may live before its exit is read as the game closing. A shorter life
    /// than this is what a launcher's handoff looks like — the exe start Steam answers by starting its own
    /// copy — so the watch waits for the replacement instead of reporting an exit.</summary>
    private static readonly TimeSpan GameReArmGrace = TimeSpan.FromSeconds(30);

    /// <summary>The Game cell's tooltip while a game is up. The cell's own label says the state; this says
    /// what it costs, which is why a build or a preview may fail while it stands.</summary>
    internal const string GameRunningDetail =
        "The game holds its files open. Builds and previews may fail until it closes.";

    private bool _watchingLaunchedGame;

    /// <summary>A watch is following a game this app started. One at a time: a watch begun while one is
    /// already running is dropped, so the Game cell has a single writer. It also gates the Launch button for
    /// the whole time the game is up — 3DMigoto can't hook a running game, so a second launch is no
    /// remedy.</summary>
    internal bool IsWatchingLaunchedGame
    {
        get => _watchingLaunchedGame;
        set
        {
            if (_watchingLaunchedGame == value) return;
            _watchingLaunchedGame = value;
            OnPropertyChanged(nameof(LaunchDisabledReason));
            OnPropertyChanged(nameof(CanLaunchGame));
            OnPropertyChanged(nameof(LaunchButtonTip));
        }
    }

    /// <summary>Follow the game this launch started: report it on the Game cell once a process the start
    /// did not already find comes up, and re-read the install when it exits, so the files the game held
    /// come back readable without a click.
    ///
    /// <para>The pid followed is one carrying the game's name that <paramref name="before"/> does not list —
    /// a name snapshot, not ownership: a copy the user starts inside the appear window is adopted just the
    /// same, and a launch that started nothing hands an EMPTY snapshot so the game already up is what gets
    /// followed. A pid that dies within <see cref="GameReArmGrace"/> of appearing is read as a launcher
    /// handoff, not an exit: the watch re-snapshots and waits once more, briefly. A process that never
    /// appears reports nothing — a launch Steam swallowed has no running game to announce.</para></summary>
    private async Task WatchLaunchedGameAsync(string processName, IReadOnlySet<int> before,
        GameLauncher.LaunchKind kind, StatusFacet launchLine)
    {
        if (IsWatchingLaunchedGame) return;
        IsWatchingLaunchedGame = true;
        // What the load left on the cell, so an exit with nothing to re-read puts it back rather than leaving
        // "running" standing over a game that is gone.
        var cellBeforeWatch = GameStatus;
        try
        {
            var known = before;
            var appearLimit = GameAppearLimit;
            bool reArmed = false, followed = false;
            while (true)
            {
                int pid = await WaitForGamePidAsync(processName, known, appearLimit);
                if (pid == NoPid) break;
                followed = true;
                GameStatus = StatusFacet.Warn(GameRunningLabel, GameRunningDetail);
                var lived = Stopwatch.StartNew();
                while (await Task.Run(() => PidsNamed(processName).Contains(pid)))
                    await Task.Delay(GameWatchPollInterval);
                if (!ShouldReArm(lived.Elapsed, kind, reArmed)) break;
                reArmed = true;
                // the replacement is what the launcher starts NEXT, so the pids standing now are what it is
                // not — including the dead one, which the table may not have released yet
                known = await Task.Run(() => PidsNamed(processName));
                appearLimit = GameReArmGrace;
            }
            if (followed) WatchedGameExited(cellBeforeWatch, launchLine);
        }
        catch (Exception)
        {
            // Nothing awaits this watch, so an unexpected process-API fault has no caller to surface on. The
            // cell keeps whatever it last read and the install is re-read by the notice cell's Rescan.
        }
        finally { IsWatchingLaunchedGame = false; }
    }

    /// <summary>What the watched game's exit settles on the header: the Game cell goes back to what the load
    /// left on it, the launch line retires, and the install is re-read if the load ended blocked.
    ///
    /// <para>Only THIS launch's own line retires (<paramref name="launchLine"/> by identity) — anything
    /// written since describes something newer, and a "Game launched" left standing over a closed game is
    /// exactly what the retirement is for.</para></summary>
    internal void WatchedGameExited(StatusFacet cellBeforeWatch, StatusFacet launchLine)
    {
        GameStatus = cellBeforeWatch;
        if (ReferenceEquals(LaunchStatus, launchLine)) LaunchStatus = StatusFacet.None;
        RefreshAfterGameExit();
    }

    /// <summary>Which kind of start the watch is following. A host that starts the game itself starts the
    /// EXE — nothing hands it a steam:// uri — so the launcher may still answer by starting its own copy,
    /// and the watch has to wait past that handoff exactly as it does for a direct start of this app's own.
    /// The resolved plan describes how THIS app would have started the game, which in that case it
    /// didn't.</summary>
    internal static GameLauncher.LaunchKind WatchedStartKind(GameLauncher.LaunchKind planned,
        bool loaderStartsGame) =>
        loaderStartsGame ? GameLauncher.LaunchKind.DirectExe : planned;

    /// <summary>Whether a followed pid's exit is a launcher handoff rather than the game closing, as a
    /// pure decision. Only a direct exe start can be answered by a launcher starting its own copy (Steam
    /// does this for an install no appmanifest named), so such a pid dying inside
    /// <see cref="GameReArmGrace"/> is waited past ONCE — a second handoff is indistinguishable from a
    /// game that keeps crashing, and that must reach the cell. A <c>steam://</c> start never re-arms.</summary>
    internal static bool ShouldReArm(TimeSpan lived, GameLauncher.LaunchKind kind, bool alreadyReArmed) =>
        !alreadyReArmed && kind == GameLauncher.LaunchKind.DirectExe && lived < GameReArmGrace;

    /// <summary>The pid of the lowest-numbered process under <paramref name="processName"/> that
    /// <paramref name="known"/> does not carry, or <see cref="NoPid"/> when the wait reaches
    /// <paramref name="limit"/> with none. The reads run off the UI thread so the window keeps painting while
    /// it waits.</summary>
    private static async Task<int> WaitForGamePidAsync(string processName, IReadOnlySet<int> known, TimeSpan limit)
    {
        var since = Stopwatch.StartNew();
        while (since.Elapsed < limit)
        {
            int pid = await Task.Run(() => FirstPidOutside(PidsNamed(processName), known));
            if (pid != NoPid) return pid;
            await Task.Delay(GameWatchPollInterval);
        }
        return NoPid;
    }

    /// <summary>Re-read the install the way the notice cell's Rescan does, once the game this app launched is
    /// gone. Only a load that ENDED BLOCKED on the game's files has anything to unblock, so a healthy session
    /// re-reads nothing: the reload drops the VFS and empties the trees, which is a working sitting thrown
    /// away for no gain. When something is holding the roster the re-read is queued behind it.</summary>
    internal void RefreshAfterGameExit()
    {
        switch (ExitReRead(GameRescanOffered, RescanMustWait))
        {
            case ExitAction.Now: ReloadRoster(); break;
            case ExitAction.Queue:
                _rescanAfterScan = true;
                // The blocked load's "The game is running" notice is standing over a game that just closed,
                // and the re-read that answers it can't run yet. Say the new state, the way the mid-scan
                // folder change does; the queued reload writes its own notice over this one.
                NoticeStatus = StatusFacet.Warn(GameClosedNotice, RescanQueuedDetail);
                break;
        }
    }

    /// <summary>The exit's own title over the shared queued-rescan detail: the game closing is what the
    /// modder just did, and it reads better than the generic wait.</summary>
    internal const string GameClosedNotice = "Game closed";

    /// <summary>What the exit of a game this app launched does about the install read.</summary>
    internal enum ExitAction
    {
        /// <summary>Nothing was blocked, so nothing is re-read.</summary>
        None,
        /// <summary>Re-read now.</summary>
        Now,
        /// <summary>Re-read once whatever is holding the roster lets go.</summary>
        Queue,
    }

    /// <summary>The exit's decision, as a pure rule. <paramref name="loadEndedBlocked"/> is the state the
    /// Rescan affordance stands on: the load found the game's files held open or unreadable, so a re-read is
    /// the remedy the exit can now perform on its own.</summary>
    internal static ExitAction ExitReRead(bool loadEndedBlocked, bool rescanMustWait) =>
        !loadEndedBlocked ? ExitAction.None
        : rescanMustWait ? ExitAction.Queue
        : ExitAction.Now;

    /// <summary>Poll until the loader is confirmed or the wait fails. Never returns
    /// <see cref="LoaderWait.KeepWaiting"/>. The process reads run off the UI thread so the window keeps
    /// painting while the UAC prompt stands.</summary>
    private static async Task<LoaderWait> WaitForLoaderAsync(string processName, Process? stub,
        IReadOnlySet<int> before, bool stubIsLoader)
    {
        int stubPid = StubPid(stub);
        bool stubPidKnown = stubPid != NoPid;
        var known = KnownPids(before, stubPid);

        var since = Stopwatch.StartNew();
        TimeSpan? stubExitedAt = null;
        while (true)
        {
            // A start that gave back no handle leaves nothing to watch, so the stub reads as standing.
            // Reading "no handle" as "the stub exited" would fail a launch that is only slow to show its
            // loader.
            var (stubAlive, successorRunning) = await Task.Run(() =>
                (stub is null || StartedHandleAlive(stub), AnyPidOutside(PidsNamed(processName), known)));
            if (!stubAlive) stubExitedAt ??= since.Elapsed;
            var step = LoaderWaitStep(stubIsLoader, stubPidKnown, stubAlive, successorRunning, since.Elapsed,
                stubExitedAt is { } at ? since.Elapsed - at : TimeSpan.Zero);
            if (step is not LoaderWait.KeepWaiting) return step;
            await Task.Delay(LoaderPollInterval);
        }
    }

    /// <summary>What one tick of the loader wait decides.</summary>
    internal enum LoaderWait
    {
        /// <summary>Poll again.</summary>
        KeepWaiting,
        /// <summary>The loader is confirmed — warm it up, then start the game.</summary>
        Ready,
        /// <summary>The stub is gone and nothing took its place: consent was refused, or the loader quit.</summary>
        Stopped,
        /// <summary>The wait reached its ceiling with the stub still standing and nothing that confirms an
        /// elevated copy behind it — the permission prompt was never answered.</summary>
        NotElevated,
    }

    /// <summary>One tick of the loader wait, as a pure decision. <paramref name="sinceStubExit"/> is read
    /// only while the stub is gone, <paramref name="sinceStart"/> only while it stands. When
    /// <paramref name="stubIsLoader"/> the started process is the loader itself, so its liveness confirms;
    /// otherwise only a process not running before the start does — the stub itself is exactly what an
    /// unanswered prompt looks like. Without <paramref name="stubPidKnown"/> the stub's pid can't be told
    /// from a successor's, so a waited-on re-launch is confirmed by nothing and fails at the ceiling.</summary>
    internal static LoaderWait LoaderWaitStep(bool stubIsLoader, bool stubPidKnown, bool stubAlive,
        bool successorRunning, TimeSpan sinceStart, TimeSpan sinceStubExit)
    {
        if (successorRunning && (stubPidKnown || stubIsLoader)) return LoaderWait.Ready;
        if (stubIsLoader && stubAlive) return LoaderWait.Ready;
        if (!stubAlive)
            return sinceStubExit >= LoaderRespawnGrace ? LoaderWait.Stopped : LoaderWait.KeepWaiting;
        return sinceStart >= LoaderWaitLimit ? LoaderWait.NotElevated : LoaderWait.KeepWaiting;
    }

    /// <summary>The pids a successor is NOT: everything that was already up, plus the process this app
    /// started. The wait's successor test and the warmup's re-check both exclude this set, so the rule that
    /// says which pid can be the elevated copy has one home.</summary>
    private static HashSet<int> KnownPids(IReadOnlySet<int> before, int stubPid)
    {
        var known = new HashSet<int>(before);
        if (stubPid != NoPid) known.Add(stubPid);
        return known;
    }

    /// <summary>What the launch accepted as its loader, so the warmup's re-read can re-test the same
    /// thing.</summary>
    internal enum LoaderProof
    {
        /// <summary>A loader was already up at the entry read; this app started nothing.</summary>
        AlreadyRunning,
        /// <summary>A process that was not running before the start — the elevated copy behind the stub.</summary>
        Successor,
        /// <summary>The process this app started IS the loader — and so is any successor under its name,
        /// since a copy that forks and exits, or quits as a duplicate beside a survivor, hands the role
        /// on. Either one standing is the confirmation.</summary>
        StartedProcess,
    }

    /// <summary>Whether the loader confirmed before the warmup is still up, re-tested on the terms of its
    /// MODE: already-running by any process under that name; an elevated copy by a pid the start did not
    /// know; a started loader by its own handle OR such a pid — the wait accepts either, so the re-check
    /// must too. False means the game must not be started: a loader that quit in the warmup window leaves
    /// an unmodded game behind a ✓.</summary>
    internal static bool LoaderStillConfirmed(LoaderProof proof, bool anyPidNamed, bool successorRunning,
        bool stubAlive) =>
        proof switch
        {
            LoaderProof.AlreadyRunning => anyPidNamed,
            LoaderProof.Successor => successorRunning,
            LoaderProof.StartedProcess => stubAlive || successorRunning,
            _ => false,
        };

    /// <summary>The loader's own ini, beside its exe.</summary>
    private const string LoaderIniName = "d3dx.ini";

    /// <summary>The ini setting that makes the loader re-launch itself elevated.</summary>
    private const string RequireAdminKey = "require_admin";

    /// <summary>Whether the loader re-launches itself elevated, from the <c>d3dx.ini</c> beside its exe. An
    /// ini that is missing or unreadable reads as no re-launch: the alternative is a wait for an elevated
    /// copy that is never coming, which ends a launch that would have worked.</summary>
    internal static bool LoaderSelfElevates(string loaderExe)
    {
        try
        {
            var ini = Path.Combine(Path.GetDirectoryName(loaderExe)!, LoaderIniName);
            return File.Exists(ini) && RequiresAdmin(File.ReadAllText(ini));
        }
        catch (Exception) { return false; }
    }

    /// <summary>Whether an ini's text sets <see cref="RequireAdminKey"/> to a true value. A commented line
    /// is not a setting, <c>;</c> opens a comment, and an explicit false or 0 means what it says: the loader
    /// will not re-launch, so waiting for an elevated copy would end a launch that works as it is.</summary>
    internal static bool RequiresAdmin(string iniText)
    {
        foreach (var raw in iniText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (!line.StartsWith(RequireAdminKey, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line[RequireAdminKey.Length..].TrimStart();
            if (!rest.StartsWith('=')) continue;
            var value = rest[1..].Trim();
            int comment = value.IndexOf(';');
            if (comment >= 0) value = value[..comment].TrimEnd();
            return !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0";
        }
        return false;
    }

    /// <summary>Whether this app runs elevated. Read once per launch rather than assumed: it decides whether
    /// the started process can be the loader itself.</summary>
    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    /// <summary>The pid a start with no readable handle leaves behind — no pid at all, so nothing the
    /// process table shows can be told apart from the process this app started.</summary>
    internal const int NoPid = -1;

    /// <summary>The started process's pid, or <see cref="NoPid"/> when there is no readable handle.</summary>
    private static int StubPid(Process? started)
    {
        try { return started?.Id ?? NoPid; }
        catch (Exception) { return NoPid; }
    }

    /// <summary>The pids of every running process carrying <paramref name="processName"/>. Name enumeration
    /// needs no elevation, so an elevated loader is visible from this (unelevated) app; every handle the
    /// enumeration hands back is disposed.</summary>
    private static IReadOnlySet<int> PidsNamed(string processName)
    {
        Process[] found;
        try { found = Process.GetProcessesByName(processName); }
        catch (Exception) { return new HashSet<int>(); }
        try { return ReadablePids(found); }
        finally { foreach (var p in found) p.Dispose(); }
    }

    /// <summary>The pids of an enumeration's handles. A handle whose pid won't read is left out rather than
    /// failing the whole read: one unreadable entry says nothing about the rest.</summary>
    private static HashSet<int> ReadablePids(Process[] found)
    {
        var pids = new HashSet<int>();
        foreach (var p in found)
        {
            try { pids.Add(p.Id); }
            catch (Exception) { }
        }
        return pids;
    }

    /// <summary>Whether any of <paramref name="pids"/> is one <paramref name="known"/> does not carry. This
    /// exclusion is the whole readiness test: the loader that appears after the start is the elevated copy,
    /// while everything that was already up — and the stub that spawned the copy — is not.</summary>
    internal static bool AnyPidOutside(IEnumerable<int> pids, IReadOnlySet<int> known) =>
        FirstPidOutside(pids, known) != NoPid;

    /// <summary>The LOWEST pid in <paramref name="pids"/> that <paramref name="known"/> does not carry, or
    /// <see cref="NoPid"/> — the game watch has to follow one process. Lowest rather than enumeration
    /// order, which is the process table's: two candidates would otherwise make the watch follow a
    /// different one on each read.</summary>
    internal static int FirstPidOutside(IEnumerable<int> pids, IReadOnlySet<int> known)
    {
        int pick = NoPid;
        foreach (var pid in pids)
            if (!known.Contains(pid) && (pick == NoPid || pid < pick)) pick = pid;
        return pick;
    }

    /// <summary>Whether the process this app started is still alive. A gone or unreadable handle counts as
    /// exited: from an un-elevated app that is either the self-elevation handoff or a refused prompt, and
    /// the process table decides which.</summary>
    private static bool StartedHandleAlive(Process? started)
    {
        try { return started is { HasExited: false }; }
        catch (Exception) { return false; }
    }

    /// <summary>Stop the process this app started, when it is still up after a wait that failed. The app owns
    /// that process, and leaving it standing would have the next launch read it as a loader already running.
    /// A kill that doesn't take changes nothing else about the failure.</summary>
    private static void KillStartedProcess(Process? started)
    {
        try
        {
            if (started is { HasExited: false }) started.Kill();
        }
        catch (Exception) { }
    }
}
