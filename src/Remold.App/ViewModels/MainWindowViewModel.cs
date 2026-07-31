using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remold.App.Views;
using Remold.Core;
using Remold.Core.Blender;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Textures;
using Remold.Core.Workbench;
// The `Workbench` child namespace is shadowed in expression context by the WorkbenchVm `Workbench`
// property, so the one App-workbench type used in expressions is aliased.
using PartMaterializeOutcome = Remold.App.ViewModels.Workbench.PartMaterializeOutcome;

namespace Remold.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, Workbench.IWorkbenchShell
{
    /// <summary>The game root — the one canonical path every bundle/table path derives from. Empty until
    /// <see cref="ResolveGameDir"/> runs, and when no install was found.</summary>
    private string _gameDir = "";
    private string GameDir => _gameDir;

    /// <summary>Order: <c>GF2_GAME_DIR</c> override, then the remembered path, then auto-detect. A fresh
    /// auto-detect is remembered so later launches skip the scan. Empty string = not found.</summary>
    private string ResolveGameDir()
    {
        if (GameLocator.Validate(Environment.GetEnvironmentVariable("GF2_GAME_DIR")) is { } env) return env;
        if (GameLocator.Validate(_settings.GamePath) is { } saved) return saved;
        if (GameLocator.Find() is { } found)
        {
            _settings.GamePath = found;
            SaveSettings();
            return found;
        }
        return "";
    }

    // The glyph strings double as each step's identity — they are compared by value.
    public string[] Steps { get; } = { "① Pick", "② Edit", "③ Build" };

    [ObservableProperty] private string _selectedStep = "① Pick";

    // mod identity / lifecycle
    /// <summary>The home/landing screen; the background game load runs behind it.</summary>
    [ObservableProperty] private bool _showHome = true;
    [ObservableProperty] private bool _isDirty;
    public string ModTitleDisplay =>
        (string.IsNullOrWhiteSpace(PackageName) ? "untitled mod" : PackageName.Trim()) + (IsDirty ? " *" : "");
    public ObservableCollection<RecentMod> RecentMods { get; } = new();
    public bool HasRecentMods => RecentMods.Count > 0;

    // Status bar: three facets (game / roster / Blender), a background-work cell, a notice cell.
    // Long remedies and multi-warning lists go in the facet tooltip (Detail), never inline.
    [ObservableProperty] private StatusFacet _gameStatus = StatusFacet.Loading("Game …");
    [ObservableProperty] private string _statusChars = "Characters …";
    [ObservableProperty] private StatusFacet _blenderStatus = StatusFacet.Loading("Blender …");
    [ObservableProperty] private StatusFacet _noticeStatus = StatusFacet.None;

    /// <summary>The background-work cell: one line while long background disk work runs, blank
    /// otherwise.</summary>
    [ObservableProperty] private StatusFacet _backgroundStatus = StatusFacet.None;
    [ObservableProperty] private bool _isScanning = true;   // the background roster fill (LoadAsync phases 2–3)

    /// <summary>A load blocked on the game's files (running or unreadable): the status bar offers the
    /// Rescan button beside the notice.</summary>
    [ObservableProperty] private bool _gameRescanOffered;

    // Pick — Characters tab: the outfit tree; its search box filters the roster.
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isLoading = true;
    public ObservableCollection<CharacterVm> Characters { get; } = new();

    // One row per enemy MODEL, display-named by the most frequent enemy name pointing at it. Same VM
    // shape as Characters, and rides the same candidate/confirm fill.
    public ObservableCollection<CharacterVm> Enemies { get; } = new();
    [ObservableProperty] private string _enemySearchText = "";

    // Output folder (the mod's project dir) — for "Open output folder".
    [ObservableProperty] private string _exportOutDir = "";
    public string CharactersTabHeader
    {
        get { var n = _allCharacters.Sum(c => c.Outfits.Count(o => o.IsInMod)); return n > 0 ? $"Characters ({n})" : "Characters"; }
    }
    public string EnemiesTabHeader
    {
        get { var n = _allEnemies.Sum(c => c.Outfits.Count(o => o.IsInMod)); return n > 0 ? $"Enemies ({n})" : "Enemies"; }
    }
    private void RefreshTabHeaders()
    {
        OnPropertyChanged(nameof(CharactersTabHeader));
        OnPropertyChanged(nameof(EnemiesTabHeader));
    }

    // Edit — the Blender bridge
    [ObservableProperty] private string _blenderPath = "";
    private BlenderSendWatcher? _watcher;
    private TextureEditWatcher? _texWatcher;
    private string? _modRoot;

    // Mod identity form (the Name/Author/Version/Description carried in the project manifest).
    [ObservableProperty] private string _packageName = "";
    [ObservableProperty] private string _packageAuthor = "";
    [ObservableProperty] private string _packageDescription = "";
    [ObservableProperty] private string _packageVersion = "1.0";
    /// <summary>Tier-1 toggle key for the whole mod. Null = no key, always on.</summary>
    [ObservableProperty] private string? _packageToggleKey;
    public string PackageToggleKeyLabel => ModKeys.Display(PackageToggleKey, BuildRowVm.NoKeyLabel);
    public bool HasPackageToggleKey => !string.IsNullOrWhiteSpace(PackageToggleKey);
    public string PackageToggleKeyTip => HasPackageToggleKey
        ? $"{PackageToggleKey} toggles the whole mod in game."
        : "Bind a key that toggles the whole mod in game.";

    /// <summary>Writes the same null the capture field's Delete writes, so both clears take the one
    /// identity-edit route.</summary>
    [RelayCommand]
    private void ClearPackageToggleKey() => PackageToggleKey = null;

    /// <summary>True while the identity form is being POPULATED from a project, so the restore can't read
    /// as an edit and autosave the project back over itself.</summary>
    private bool _loadingIdentityForm;
    /// <summary>The mod-level status line. Its one on-screen home is the Build pane's footer
    /// (<see cref="Footer"/>), routed there by <see cref="OnBuildStatusChanged"/>.</summary>
    [ObservableProperty] private string _buildStatus = "";
    [ObservableProperty] private string _builtPath = "";
    /// <summary>An open-mod load is in flight — gates re-entry so a second open can't race the first.</summary>
    [ObservableProperty] private bool _isOpeningMod;

    private List<CharacterVm> _allCharacters = new();
    private List<CharacterVm> _allEnemies = new();
    /// <summary>Every roster-tab pick grid. INVARIANT: any new roster-shaped tab adds its backing list to
    /// this concat, and tab-shared behavior enumerates AllPickRows and never a per-tab list — otherwise the
    /// new tab's picks silently fall out of the queue/ledger/restore.</summary>
    private IEnumerable<CharacterVm> AllPickRows => _allCharacters.Concat(_allEnemies);
    // The forward view of the install (catalog + GFF manifest): the roster fill, the workbench and every
    // export resolve through it. Null (install unreadable) disables those routes behind their guards.
    private GameVfs? _vfs;
    // glb paths already rig-upgraded this session → the stamp of the textures that rig baked in
    // (AssetExporter.TextureStamps). Recorded ONLY after the build succeeds, so a failed build stays
    // retryable; a stamp mismatch re-rigs with the new image. Session-scoped.
    private readonly Dictionary<string, string> _riggedGlbs = new(StringComparer.OrdinalIgnoreCase);
    private string _pkgCharacter = "", _pkgOutfit = "";
    private readonly LabSettings _settings = LabSettings.Load();

    /// <summary>Render-time internal-key → friendly-label resolver. Empty until phase 1 builds it, so labels
    /// fall back to the internal token until then. The keys it maps FROM stay internal everywhere.</summary>
    private FriendlyNames _friendly = FriendlyNames.Empty;

    /// <summary>The current mod project. In-memory (no <see cref="ModProject.RootDir"/>) until the first
    /// export/save materializes a folder.</summary>
    private ModProject _project = new();

    /// <summary>The current roster: the full DB roster at phase 1, narrowed to confirmed outfits at the
    /// fill's finalize. The workbench resolves a ledger stem to its <see cref="Outfit"/> through it.</summary>
    private IReadOnlyList<Character> _roster = new List<Character>();

    /// <summary>The asset-sharing measurement: adopted from cache or shipped seed at load, repaired in the
    /// background. The build awaits it — a faulted or cancelled task builds unscoped. Null before the first
    /// load.</summary>
    private Task<SharingIndex?>? _sharingTask;
    private CancellationTokenSource? _sharingCts;

    // What BackgroundStatus is computed from. UI-THREAD ONLY: off-thread writers marshal first, so the
    // cell is never assembled from two half-states.
    private SharingProgress? _sharingProgress;
    private bool _prewarmRunning;
    /// <summary>The current pass ended in a fault. A cancelled pass never sets it — its successor owns the
    /// cell.</summary>
    private bool _sharingFailed;
    /// <summary>A build is parked on the measurement, so the pass's line goes on the Build footer too.</summary>
    private bool _buildWaitingOnSharing;

    /// <summary>The Outfit Workbench, hosted in the Edit step: read side via delegates, edit verbs through
    /// the shell, so it never reaches back into the hosting window. Built once in the ctor.</summary>
    public Workbench.WorkbenchVm Workbench { get; }
    /// <summary>A saved selection waiting for the roster fill to resolve outfits so it can re-check them.</summary>
    private List<SelectionEntry>? _pendingSelection;

    /// <summary>The one subject-model memo, shared with the Edit pane — an Edit-to-Build hop would
    /// otherwise read every subject's bundles twice. Created before <see cref="Workbench"/>, which gets the
    /// same instance.</summary>
    private readonly SubjectModelCache _subjectModels = new();

    /// <summary>The memo, for tests that pin the hit and the rescan's drop.</summary>
    internal SubjectModelCache SubjectModels => _subjectModels;

    /// <summary>The open mod, for tests that drive a seam reading it without a mod folder on disk.</summary>
    internal ModProject OpenProject => _project;

    public MainWindowViewModel() : this(startLoad: true) { }

    /// <summary>The app always constructs through the parameterless form. <paramref name="startLoad"/> is
    /// false only in tests — the game load reaches the registry, the install and the dispatcher.
    /// <paramref name="prewarmJob"/> replaces the queue's work likewise: the real job needs an install to
    /// read.</summary>
    internal MainWindowViewModel(bool startLoad,
        Func<SubjectKey, IProgress<string>, CancellationToken, Task>? prewarmJob = null)
    {
        Workbench = new Workbench.WorkbenchVm(
            () => _project, () => _vfs, () => _friendly, () => _roster, TryDeobfuscateBundle,
            () => string.IsNullOrEmpty(GameDir) ? null : CatalogIndex.LoadCached(GameDir), shell: this,
            subjectModels: _subjectModels);
        _prewarm = new PrewarmQueue<SubjectKey>(Tracked(prewarmJob ?? PrewarmOutfitAsync), SubjectKeyComparer.Instance);
        PackageAuthor = _settings.Author;   // remembered across sessions
        RefreshRecent();
        if (startLoad) _ = Task.Run(LoadAsync);
    }

    // ---- mod lifecycle ------------------------------------------------

    /// <summary>The mods-library folder — the open/import dialogs' start location.</summary>
    public string LibraryRoot => _settings.ResolvedLibraryRoot;

    /// <summary>Start a fresh mod: always an in-memory untitled project. The folder is minted (and named)
    /// on the first export/save, so New-and-never-export leaves no empty folder behind.</summary>
    public void NewMod()
    {
        ResetWorkspace();
        _project = new ModProject();
        _loadingIdentityForm = true;
        PackageName = "";
        PackageDescription = "";
        PackageVersion = "1.0";
        PackageAuthor = _settings.Author;
        PackageToggleKey = null;
        _loadingIdentityForm = false;
        SelectedStep = "① Pick";
        BuildStatus = "";
        IsDirty = false;
        ShowHome = false;
        Workbench.NotifyProjectChanged();   // clears any leftover tree
    }

    /// <summary>Open a project folder (or its <c>mod.drlproj</c>) WITHOUT re-exporting. The disk load runs
    /// off the UI thread, the VM assembly back on it. Returns true when a project actually opened.</summary>
    public async Task<bool> OpenModAsync(string folderOrFile)
    {
        if (IsOpeningMod) return false;   // an open is already in flight — ignore a second trigger
        IsOpeningMod = true;
        BuildStatus = "Opening…";
        try
        {
            ModProject proj;
            try { proj = await Task.Run(() => ModProject.Load(folderOrFile)); }
            catch (Exception e)
            {
                // Do NOT navigate steps and do NOT touch the current workspace: the failed open leaves the
                // current (untouched) mod loaded, so a modal notice is the only honest surface.
                BuildStatus = "";
                bool removed = RemoveDeadRecent(folderOrFile);   // only drops the row when the target is truly gone
                if (MainWindow is { } owner)
                {
                    var body = removed
                        ? $"{e.Message}\n\nRemoved from Recents (folder not found)."
                        : e.Message;
                    await ConfirmWindow.Notice(owner, "Couldn't open the mod", body);
                }
                return false;
            }
            ApplyOpenedProject(proj);
            return true;
        }
        finally { IsOpeningMod = false; }
    }

    /// <summary>Drop a recent-mods entry ONLY when its target is genuinely gone; a transient open failure
    /// (file lock, parse hiccup, the game holding a bundle) leaves the row so the user can retry. Returns
    /// true when a row was actually removed.</summary>
    private bool RemoveDeadRecent(string folderOrFile)
    {
        if (Directory.Exists(folderOrFile) || File.Exists(folderOrFile)) return false;   // still on disk — not dead
        if (_settings.RecentMods.RemoveAll(m => string.Equals(m.Path, folderOrFile, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            SaveSettings();
            RefreshRecent();
            return true;
        }
        return false;
    }

    /// <summary>Assemble the opened project into the workspace. UI thread — it mutates bound collections.</summary>
    private void ApplyOpenedProject(ModProject proj)
    {
        ResetWorkspace();
        _project = proj;

        // form
        _loadingIdentityForm = true;
        PackageName = proj.Info.Name;
        PackageAuthor = string.IsNullOrWhiteSpace(proj.Info.Author) ? _settings.Author : proj.Info.Author!;
        PackageDescription = proj.Info.Description ?? "";
        PackageVersion = string.IsNullOrWhiteSpace(proj.Info.Version) ? "1.0" : proj.Info.Version;
        PackageToggleKey = ModKeys.Normalize(proj.Info.ToggleKey);
        _loadingIdentityForm = false;
        _pkgCharacter = proj.Info.Character ?? "";
        _pkgOutfit = proj.Info.Outfit ?? "";

        _modRoot = proj.RootDir;
        ExportOutDir = proj.RootDir ?? "";
        EnsureWatcher();

        // re-check the saved parts once the roster resolves them (or now, if it already has)
        _pendingSelection = proj.Selection;
        ApplyPendingSelection();

        RememberRecent();
        IsDirty = false;
        int replacementCount = proj.Targets.Count;
        BuildStatus = replacementCount > 0
            ? $"Opened \"{proj.Info.Name}\". {replacementCount} replacement(s)."
            : $"Opened \"{proj.Info.Name}\".";
        SelectedStep = "② Edit";
        ShowHome = false;   // enter the flow
        // OnSelectedStepChanged fires Activate only when the step VALUE changes; this covers a mod opened
        // while already on Edit.
        Workbench.NotifyProjectChanged();
        // Only when no scan is running: a scan in flight contributes the notice into its own list at
        // finalize, so it can't race or double-fire.
        if (!IsScanning) MaybeNoticeAuthoredAgainst();
    }

    [RelayCommand]
    private async Task OpenRecent(RecentMod? m)
    {
        if (m is null || !await ConfirmLeaveProjectAsync()) return;
        await OpenModAsync(m.Path);
    }

    /// <summary>Save, minting a folder under the library root on first save and renaming it to match a
    /// changed mod name.</summary>
    [RelayCommand]
    private void SaveMod()
    {
        if (ShowHome) return;   // no current mod on the home screen — don't materialize an empty folder
        TrySaveProject();       // BuildStatus is set inside on both success and failure
    }

    /// <summary>The single save-or-mint route (File → Save Mod and the close/leave flush both land here).
    /// Never throws: a failed write returns <c>(false, reason)</c> and leaves the project dirty in memory so
    /// the caller can prompt and the modder can retry.</summary>
    private (bool Ok, string? Reason) TrySaveProject()
    {
        try
        {
            SyncFormToProject();
            if (_project.RootDir is null)
            {
                _project.SaveOrMint(_settings.ResolvedLibraryRoot, ModNaming.Slug(ProjectName));
                _modRoot = _project.RootDir; ExportOutDir = _project.RootDir!;
            }
            else { EnsureFolderMatchesName(); _project.Save(); }
            RememberRecent();
            IsDirty = false;
            BuildStatus = $"Saved to {_project.RootDir}";
            return (true, null);
        }
        catch (Exception e) { BuildStatus = $"Save failed: {e.Message}"; return (false, e.Message); }
    }

    /// <summary>Save a copy under a new name and switch to it (the current mod stays on disk untouched).</summary>
    public async Task SaveModAs(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        try
        {
            SyncFormToProject();
            if (_project.RootDir is null)
                _project.RootDir = UniqueDir(_settings.ResolvedLibraryRoot, ModNaming.Slug(ProjectName));
            else EnsureFolderMatchesName();
            _project.Save();   // ensure the source is complete before copying

            var dest = UniqueDir(_settings.ResolvedLibraryRoot, ModNaming.Slug(newName));
            var copy = _project.CopyTo(dest);
            copy.Info.Name = newName.Trim();
            copy.Save();
            await OpenModAsync(dest);   // switch to the copy (clean reload from disk)
            BuildStatus = $"Saved a copy as '{newName.Trim()}'.";
        }
        catch (Exception e) { BuildStatus = $"Save As failed: {e.Message}"; }
    }

    /// <summary>Close the current mod and return home, staging a fresh in-memory project.</summary>
    [RelayCommand]
    private async Task CloseMod()
    {
        if (!await ConfirmLeaveProjectAsync()) return;
        NewMod();
        ShowHome = true;
    }

    /// <summary>Whether leaving has to flush first: unsaved changes AND a real project open. Pure so the
    /// decision is testable without standing up the VM.</summary>
    internal static bool LeaveNeedsSave(bool isDirty, bool showHome) => isDirty && !showHome;

    /// <summary>Whether the close/leave flow may skip the save-failed prompt. Only a needed save that FAILED
    /// forces it.</summary>
    internal static bool LeaveProceedsWithoutPrompt(bool saveNeeded, bool saveOk) => !saveNeeded || saveOk;

    /// <summary>The gate before anything that drops the current project (New / Open / Close): never prompt
    /// when a save can simply happen. Only a FAILED save prompts. True = go ahead.</summary>
    public Task<bool> ConfirmLeaveProjectAsync() => SaveOrConfirmLeaveAsync("Leave");

    /// <summary>True when the window can close with no flow at all; false routes through
    /// <see cref="ConfirmAppCloseAsync"/>, which saves rather than discards.</summary>
    public bool CanCloseSilently => !LeaveNeedsSave(IsDirty, ShowHome);

    /// <summary>Long-running work the modder ASKED for is in flight, so the close handler asks first. A
    /// speculative prewarm is not in this: the close cancels it and goes. An asked-for Open-all's rig build
    /// runs between materialize scopes rather than inside one, so it is read off its own flag; a send-back
    /// apply is in flight for as long as <see cref="_applyingSend"/> stands.</summary>
    public bool IsWorkInFlight =>
        WorkInFlight(Workbench.IsMaterializingAll, _materializing, _buildingCombinedRig, _applyingSend, IsModBuilding);

    /// <summary>The composition behind <see cref="IsWorkInFlight"/> — ANY holder counts, and each is its own
    /// flag because they start and end at different places. Pure so every contributing flag can be exercised
    /// without standing up the window.</summary>
    internal static bool WorkInFlight(bool materializingAll, bool materializing, bool buildingRig,
        bool applyingSend, bool building) =>
        materializingAll || materializing || buildingRig || applyingSend || building;

    /// <summary>Drop every speculative prewarm. Only a close that actually goes through calls it — a
    /// declined close leaves the app open on the subject the guess was preparing.</summary>
    public void CancelSpeculativeWork() => _prewarm.CancelAll();

    /// <summary>Whether THIS close pass actually leaves — the only kind that may drop speculative work. A
    /// pass that ends in a prompt may be declined; the confirmed re-close and the nothing-to-ask close are
    /// the two that go through.</summary>
    public static bool CloseDropsSpeculativeWork(bool closeConfirmed, bool workInFlight, bool canCloseSilently) =>
        closeConfirmed || (!workInFlight && canCloseSilently);

    /// <summary>Confirm closing while work runs. The button pair is the siblings' — a verb and a plain way
    /// back — and the body names the work that is actually running.</summary>
    public async Task<bool> ConfirmCloseWithWorkAsync()
    {
        if (MainWindow is not { } owner) return true;   // headless — don't trap the close
        return await ConfirmWindow.Show(owner, "Work in progress",
            CloseWithWorkBody(IsModBuilding, _applyingSend, _buildingCombinedRig),
            "Quit anyway", "Keep working", danger: true);
    }

    /// <summary>What quitting does to the work in flight, per state. ORDERED by what quitting COSTS: a mod
    /// build and a send-back apply are abandoned mid-run — neither carries a token to stop — so they lead and
    /// say what is left behind. The rig build and the materialize are cancelled cleanly, and say so. Pure, so
    /// the wording is settled without standing up the window.</summary>
    internal static string CloseWithWorkBody(bool building, bool applyingSend, bool buildingRig) =>
        building
            ? "A mod is still building. Quitting abandons the run. Its temporary files are cleaned by the next build."
        : applyingSend
            ? "A part from Blender is still being applied. Quitting abandons it partway and can leave that part "
              + "half-written. Send it again from Blender to redo it."
        : buildingRig
            ? "The outfit rig for Blender is still building. Quitting cancels it."
        : "Materializing files is still running. Quitting cancels it.";

    /// <summary>Cancel every in-flight materialize so a confirmed close stops the work cleanly.</summary>
    public void CancelInFlightWork()
    {
        Workbench.RequestCancelMaterializeAll();
        // Terminal (close-only), so the token is never re-armed.
        _materializeCts.Cancel();
    }

    /// <summary>The window-close variant of <see cref="ConfirmLeaveProjectAsync"/> — same save-first rule.</summary>
    public Task<bool> ConfirmAppCloseAsync() => SaveOrConfirmLeaveAsync("Quit");

    /// <summary>The one save-first close/leave route; returns true to proceed. Only a FAILED save prompts.
    /// There is deliberately NO discard path: the app autosaves after every meaningful step, so a
    /// "quit to discard" never reliably discarded.</summary>
    private async Task<bool> SaveOrConfirmLeaveAsync(string leaveVerb)
    {
        if (!LeaveNeedsSave(IsDirty, ShowHome)) return true;
        var (ok, reason) = TrySaveProject();
        if (LeaveProceedsWithoutPrompt(saveNeeded: true, saveOk: ok)) return true;
        if (MainWindow is not { } owner) return true;   // headless — can't prompt; don't trap the user
        return await ConfirmWindow.Show(owner, "Couldn't save mod",
            $"Saving \"{ProjectName}\" failed: {reason}\n\n{leaveVerb} anyway and lose these changes?",
            $"{leaveVerb} anyway", "Cancel", danger: true);
    }

    /// <summary>Move the mod folder to match a renamed mod and rebase the in-memory paths + watchers. A
    /// failed move (folder in use, cross-volume) is non-fatal — the old folder is kept.</summary>
    private void EnsureFolderMatchesName()
    {
        if (_project.RootDir is null) return;
        if (!CanRenameProjectFolder(_materializing || _prewarming || _buildingCombinedRig, IsModBuilding,
                _applyingSend)) return;
        var root = _project.RootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var desired = ModNaming.Slug(ProjectName);
        // A dedup form (`desired-2`, `desired-5`, …) is ALREADY the right home. Treating one as a mismatch
        // makes every autosave re-move the folder to a fresh UniqueDir and strand files behind it.
        if (desired.Length == 0 || ModProject.FolderMatchesSlug(Path.GetFileName(root), desired))
            return;

        var old = _project.RootDir;
        var target = UniqueDir(Path.GetDirectoryName(root)!, desired);
        try
        {
            _watcher?.Dispose(); _watcher = null;   // release the folder before moving it
            _texWatcher?.Dispose(); _texWatcher = null;
            _project.MoveTo(target);
        }
        catch { EnsureWatcher(); return; }          // couldn't move — keep the old folder

        // rebase the in-memory absolute paths to the new location
        _modRoot = _project.RootDir;
        ExportOutDir = _project.RootDir!;
        if (BuiltPath.StartsWith(old, StringComparison.OrdinalIgnoreCase))
            BuiltPath = _project.RootDir + BuiltPath[old.Length..];
        EnsureWatcher();
    }

    /// <summary>Whether the project folder may be renamed to match the mod name right now. Every holder
    /// captured the old path and is still writing into it: a materialize's background read, a build's
    /// whole run, a send-back apply's workspace rewrites. A speculative prewarm and the rig build that an
    /// Open-all runs off-thread both count as a materialize — the rule turns on the writing, not on who
    /// asked, and a rename under the rig build strands its glb in a folder the session sends back to. The
    /// next autosave picks the rename up.</summary>
    internal static bool CanRenameProjectFolder(bool materializing, bool building, bool applyingSend) =>
        !materializing && !building && !applyingSend;

    private string ProjectName => string.IsNullOrWhiteSpace(PackageName) ? "untitled mod" : PackageName.Trim();

    /// <summary>Tear down the current mod's transient UI state, between New/Open.</summary>
    private void ResetWorkspace()
    {
        _watcher?.Dispose(); _watcher = null;
        _texWatcher?.Dispose(); _texWatcher = null;
        Workbench.Reset();   // cancels the in-flight subject build + clears the tree
        _prewarm.CancelAll();   // every prewarm names a subject of the mod being left
        _prewarmed.Clear();     // …and so does every outfit recorded as prepared
        // The commit hop's project-identity guard is the real safety net; this only stops the abandoned
        // Open-in-Blender / Open-map / drop prepares early.
        _materializeCts.Cancel(); _materializeCts.Dispose(); _materializeCts = new();
        // Every queued send names a file of the mod being left; a running apply finds the queue empty
        // and winds down.
        _queuedSends.Clear();
        ExportOutDir = ""; _modRoot = null;
        _pkgCharacter = ""; _pkgOutfit = "";
        BuiltPath = "";
        ClearSelection();
        _pendingSelection = null;
        _authoredNoticeShownFor = null;   // the stale-version notice belongs to the mod we're leaving
        // A landed launch ✓ describes the sitting being left — it goes out with the workspace.
        LaunchStatus = StatusFacet.None;
        ResetBuildPane();
    }

    /// <summary>Clear every subject checkbox + ✎ marker on the Pick tree. Silent — teardown never runs the
    /// add/remove path (there is no project left to mutate).</summary>
    private void ClearSelection()
    {
        foreach (var c in AllPickRows)
        {
            foreach (var o in c.Outfits) { o.SetInModSilently(false); o.HasEdits = false; }
            c.RefreshSubjectState();
        }
        RefreshTabHeaders();
    }

    /// <summary>Reflect the saved selection ledger onto the Pick tree once the roster carries the
    /// outfits. UI thread.</summary>
    private void ApplyPendingSelection()
    {
        if (_pendingSelection is null || IsScanning) return;   // wait for PHASE 3 to resolve the outfits
        SyncSubjectsFromLedger();
        _pendingSelection = null;
    }

    /// <summary>Reflect the ledger onto the Pick tree. Sets each checkbox WITHOUT firing the user-toggle
    /// add/remove path.</summary>
    private void SyncSubjectsFromLedger()
    {
        foreach (var c in AllPickRows)
        {
            foreach (var o in c.Outfits)
            {
                bool inMod = _project.HasSubject(c.Name, o.Stem);
                o.SetInModSilently(inMod);
                o.HasEdits = inMod && SubjectRemoval.EditedFileCount(_project, c.Name, o.Stem, o.Model.MeshPrefix, RemainingSubjectPrefix) > 0;
            }
            c.RefreshSubjectState();   // the single-outfit collapse proxy checkbox + ✎
        }
        RefreshTabHeaders();
    }

    // ---- subject add / remove (the Pick checkbox drives the ledger) ---------------------------------

    /// <summary>A still-selected subject's real mesh prefix, so <see cref="SubjectRemoval"/> can tell whether
    /// a survivor claims a shared texture's users. Null for a stale ledger entry — the remove keeps on unsure.</summary>
    private string? RemainingSubjectPrefix(string character, string stem) =>
        AllPickRows.FirstOrDefault(c => string.Equals(c.Name, character, StringComparison.OrdinalIgnoreCase))
            ?.Outfits.FirstOrDefault(o => string.Equals(o.Stem, stem, StringComparison.OrdinalIgnoreCase))
            ?.Model.MeshPrefix;

    /// <summary>A Pick subject checkbox was toggled by the user. Unchecking confirms (and reverts on cancel)
    /// when the subject has materialized/edited content.</summary>
    private void OnSubjectToggled(CharacterVm character, OutfitVm outfit)
    {
        if (outfit.IsInMod) AddSubject(character, outfit);
        else _ = UncheckSubjectAsync(character, outfit);
    }

    /// <summary>The character-level checkbox: grab or drop the WHOLE character. Both directions run the
    /// normal per-subject paths, the remove behind ONE composed confirm — never N dialogs.</summary>
    private void OnCharacterToggled(CharacterVm character, bool addAll)
    {
        if (addAll) AddCharacter(character);
        else _ = RemoveCharacterAsync(character);
    }

    /// <summary>Add every not-yet-in-mod outfit, each through the normal AddSubject path.</summary>
    private void AddCharacter(CharacterVm character)
    {
        foreach (var o in character.Outfits.ToList())
            if (!o.IsInMod)
            {
                o.SetInModSilently(true);   // a batch action, not a per-row toggle
                AddSubject(character, o);
            }
        character.RefreshSubjectState();
    }

    /// <summary>Remove every in-mod outfit behind ONE summary confirm — never one dialog per outfit. An
    /// outfit whose materialize is in flight is EXCLUDED (a later commit would resurrect it); its idle
    /// siblings still remove and one footer line names the skipped outfit(s).</summary>
    private async Task RemoveCharacterAsync(CharacterVm character)
    {
        var inMod = character.Outfits.Where(o => o.IsInMod).ToList();
        if (inMod.Count == 0) { character.RefreshSubjectState(); return; }

        var busy = inMod.Where(o => IsSubjectBusy(character.Name, o.Stem)).ToList();
        var removable = inMod.Where(o => !IsSubjectBusy(character.Name, o.Stem)).ToList();
        if (removable.Count == 0)
        {
            Workbench.ReportStatus(BusyRemovalMessage(character, busy, removed: 0));
            character.RefreshSubjectState();   // restore the checkbox display — nothing was removed
            return;
        }

        var withContent = removable
            .Where(o => SubjectRemoval.HasMaterializedContent(_project, character.Name, o.Stem, o.Model.MeshPrefix, RemainingSubjectPrefix))
            .ToList();
        if (withContent.Count > 0)
        {
            if (MainWindow is not { } owner) { character.RefreshSubjectState(); return; }
            int totalEdited = withContent.Sum(o =>
                SubjectRemoval.EditedFileCount(_project, character.Name, o.Stem, o.Model.MeshPrefix, RemainingSubjectPrefix));
            var head = $"Remove all {removable.Count} of {character.DisplayName}'s outfits from the mod?";
            var body = totalEdited > 0
                ? $"{head}\n\nThis discards {totalEdited} edited file{(totalEdited == 1 ? "" : "s")} and drops their materialized assets. This can't be undone."
                : $"{head}\n\nThis drops their materialized assets. This can't be undone.";
            if (!await ConfirmWindow.Show(owner, "Remove from mod", body, "Remove all", "Cancel", danger: true))
            {
                character.RefreshSubjectState();   // restore the checkbox display — nothing was removed
                return;
            }
        }

        foreach (var o in removable)
        {
            o.SetInModSilently(false);
            await RemoveSubjectNoConfirmAsync(character.Name, o.Stem, o.Model.MeshPrefix, SubjectLabel(character.Name, o.Model));
        }
        // Reported LAST so it isn't overwritten by the per-outfit "Removed …" footer line.
        if (busy.Count > 0) Workbench.ReportStatus(BusyRemovalMessage(character, busy, removed: removable.Count));
        character.RefreshSubjectState();
    }

    /// <summary>The single footer line for a batch remove that skipped busy outfit(s), reusing
    /// <see cref="RemoveSubjectAsync"/>'s wording and reporting both halves of a partial batch.</summary>
    private string BusyRemovalMessage(CharacterVm character, IReadOnlyList<OutfitVm> busy, int removed)
    {
        var joined = string.Join(", ", busy.Select(o => SubjectLabel(character.Name, o.Model)));
        var wait = $"Wait for {joined}'s current work to finish before removing {(busy.Count == 1 ? "it" : "them")}.";
        return removed > 0
            ? $"Removed {removed} outfit{(removed == 1 ? "" : "s")}. {wait}"
            : wait;
    }

    /// <summary>Add a subject's ledger <c>SelectionEntry</c>, minting the mod folder so the entry has
    /// somewhere to persist.</summary>
    internal void AddSubject(CharacterVm character, OutfitVm outfit)
    {
        if (!_project.HasSubject(character.Name, outfit.Stem))
            _project.Selection.Add(new SelectionEntry { Character = character.Name, Outfit = outfit.Stem });
        outfit.HasEdits = false;   // freshly-checked — nothing edited yet
        character.RefreshSubjectState();
        Workbench.NotifyProjectChanged();
        RefreshTabHeaders();
        AutoNameFromSubject(character.Name);   // before the mint, so the slug matches
        // A failed mint says nothing: nothing is lost yet, and every route that writes into the folder
        // mints again and reports its own failure.
        EnsureModRoot();
        MarkDirty(); AutoSave();
    }

    /// <summary>A Pick UNCHECK: run the subject-scoped remove, restoring the checkbox if it was cancelled.</summary>
    private async Task UncheckSubjectAsync(CharacterVm character, OutfitVm outfit)
    {
        bool removed = await RemoveSubjectAsync(character.Name, outfit.Stem, outfit.Model.MeshPrefix,
            SubjectLabel(character.Name, outfit.Model));
        if (!removed) { outfit.SetInModSilently(true); character.RefreshSubjectState(); }
    }

    /// <summary>The ONE subject-scoped remove, behind both entry points (Pick uncheck and the Edit subject
    /// header). Confirms only when the subject has materialized/edited content. Returns false when the modder
    /// cancels, meaning nothing changed. UI thread.</summary>
    internal async Task<bool> RemoveSubjectAsync(string character, string stem, string meshPrefix, string label)
    {
        // Refuse while a materialize for this subject is preparing; the caller restores the checkbox on
        // the false return, the footer says why. A prewarm never sets this — the remove itself drains it.
        if (IsSubjectBusy(character, stem))
        {
            Workbench.ReportStatus($"Wait for {label}'s current work to finish before removing it.");
            return false;
        }
        if (SubjectRemoval.HasMaterializedContent(_project, character, stem, meshPrefix, RemainingSubjectPrefix))
        {
            if (MainWindow is not { } owner) return false;
            int edited = SubjectRemoval.EditedFileCount(_project, character, stem, meshPrefix, RemainingSubjectPrefix);
            var body = edited > 0
                ? $"Remove {label} from the mod?\n\nThis discards {edited} edited file{(edited == 1 ? "" : "s")} and drops the subject's materialized assets. This can't be undone."
                : $"Remove {label} from the mod?\n\nThis drops the subject's materialized assets. This can't be undone.";
            if (!await ConfirmWindow.Show(owner, "Remove from mod", body, "Remove", "Cancel", danger: true))
                return false;
        }

        await RemoveSubjectNoConfirmAsync(character, stem, meshPrefix, label);
        return true;
    }

    /// <summary>The subject-scoped remove WITHOUT the confirm — shared by <see cref="RemoveSubjectAsync"/>
    /// and the character-level batch remove, which composes its own. Drains this subject's prewarm and
    /// waits for it to unwind FIRST: a job still writing the files about to be deleted would leave some
    /// behind. That wait is why this is async. UI thread.</summary>
    private async Task RemoveSubjectNoConfirmAsync(string character, string stem, string meshPrefix, string label)
    {
        var key = new SubjectKey(character, stem);
        await _prewarm.CancelAsync(key);
        // After the drain, not before: for as long as the record stands, a visit landing mid-drain enqueues
        // nothing, so dropping it here is one fewer window for a preparation of the workspace about to go.
        // That workspace IS going, so a re-add is an outfit to prepare again.
        _prewarmed.Remove(key);
        SubjectRemoval.Remove(_project, character, stem, meshPrefix, RemainingSubjectPrefix);
        Workbench.NotifyProjectChanged();
        SyncSubjectsFromLedger();
        MarkDirty(); AutoSave();
        Workbench.ReportStatus($"Removed {label} from the mod.");
    }

    /// <summary>Ensure a subject is checked and jump to Edit, landing SELECTED on its root — opening an
    /// outfit is a visit, so the selection starts its preparation. Accepts an outfit row or a collapsed
    /// single-outfit character row.</summary>
    public void OpenSubjectInEdit(object? row)
    {
        (string Name, string Stem)? opened;
        switch (row)
        {
            case OutfitVm o:
                var owner = AllPickRows.FirstOrDefault(c => c.Outfits.Contains(o));
                if (owner is not null && !o.IsInMod) o.SetInModSilently(true);   // reflect, then add via the char
                if (owner is not null && !_project.HasSubject(owner.Name, o.Stem)) AddSubject(owner, o);
                opened = owner is null ? null : (owner.Name, o.Stem);
                break;
            case CharacterVm { IsSingleOutfit: true } c:
                if (!c.Outfits[0].IsInMod) { c.Outfits[0].SetInModSilently(true); c.RefreshSubjectState(); }
                if (!_project.HasSubject(c.Name, c.Outfits[0].Stem)) AddSubject(c, c.Outfits[0]);
                opened = (c.Name, c.Outfits[0].Stem);
                break;
            default:
                return;   // a multi-outfit header isn't itself a subject — expand it and pick an outfit
        }
        SelectedStep = "② Edit";
        if (opened is { } s) Workbench.RequestSelectPart(s.Name, s.Stem, "");
    }

    /// <summary>A concise subject label for confirms/status, in the workbench header's order.</summary>
    private string SubjectLabel(string character, Outfit outfit) => _friendly.Subject(character, outfit);


    /// <summary>(Re)build the key→label resolver from a name-enriched roster. UI thread; safe to repeat.</summary>
    private void RebuildFriendlyNames(IReadOnlyList<Character> enrichedRoster) =>
        _friendly = FriendlyNames.FromRoster(enrichedRoster);

    /// <summary>The identity form reduced to what a build consumes: blanks take defaults, the rest
    /// trimmed, the key normalized. ONE reading, so a save's write and the stale-result compare can't
    /// drift apart.</summary>
    private (string Name, string Version, string? Author, string? Description, string? ToggleKey) IdentityForm() => (
        ProjectName,
        string.IsNullOrWhiteSpace(PackageVersion) ? "1.0" : PackageVersion.Trim(),
        string.IsNullOrWhiteSpace(PackageAuthor) ? null : PackageAuthor.Trim(),
        string.IsNullOrWhiteSpace(PackageDescription) ? null : PackageDescription.Trim(),
        ModKeys.Normalize(PackageToggleKey));

    /// <summary>Copy the mod-identity form into the project's <see cref="ProjectInfo"/>.</summary>
    private void SyncFormToProject()
    {
        var form = IdentityForm();
        _project.Info.Name = form.Name;
        _project.Info.Version = form.Version;
        _project.Info.Author = form.Author;
        _project.Info.Description = form.Description;
        _project.Info.ToggleKey = form.ToggleKey;
        _project.Info.Character = string.IsNullOrWhiteSpace(_pkgCharacter) ? null : _pkgCharacter;
        _project.Info.Outfit = string.IsNullOrWhiteSpace(_pkgOutfit) ? null : _pkgOutfit;
    }

    /// <summary>Autosave after a meaningful step, once a folder exists. A failure must NOT pass silently:
    /// IsDirty is cleared only on success, so a failed write leaves the project dirty and says so. Returns
    /// the failure message, else null, for a caller with a surface of its own.</summary>
    private string? AutoSave()
    {
        // RootDir null = folder not minted yet: nothing to autosave INTO, so skip and leave IsDirty true.
        // The close/leave flush is the guaranteed terminal save and mints the folder then.
        if (_project.RootDir is null) return null;
        try { SyncFormToProject(); EnsureFolderMatchesName(); _project.Save(); RememberRecent(); IsDirty = false; }
        catch (Exception e)
        {
            var line = $"Autosave failed: {e.Message} Changes are still in memory. Use File · Save Mod to retry.";
            BuildStatus = line;
            Workbench.ReportStatus($"Autosave failed. Changes are unsaved. Use File · Save Mod. ({e.Message})");
            return line;
        }
        return null;
    }

    private void RememberRecent()
    {
        if (_project.RootDir is null) return;
        _settings.AddRecent(_project.RootDir, _project.Info.Name);
        SaveSettings();
        RefreshRecent();
    }

    private void RefreshRecent()
    {
        RecentMods.Clear();
        foreach (var m in _settings.RecentMods) RecentMods.Add(m);
        OnPropertyChanged(nameof(HasRecentMods));
    }

    /// <summary>A collision-free project folder under <paramref name="root"/> for <paramref name="slug"/>.</summary>
    private static string UniqueDir(string root, string slug) => ModProject.UniqueDir(root, slug);

    private void MarkDirty() => IsDirty = true;

    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(ModTitleDisplay));
    partial void OnPackageNameChanged(string value)
    {
        OnPropertyChanged(nameof(ModTitleDisplay));
        OnIdentityEdited();
    }
    partial void OnPackageAuthorChanged(string value) => OnIdentityEdited();
    partial void OnPackageDescriptionChanged(string value) => OnIdentityEdited();
    partial void OnPackageVersionChanged(string value) => OnIdentityEdited();
    partial void OnPackageToggleKeyChanged(string? value)
    {
        OnPropertyChanged(nameof(PackageToggleKeyLabel));
        OnPropertyChanged(nameof(HasPackageToggleKey));
        OnPropertyChanged(nameof(PackageToggleKeyTip));
        OnIdentityEdited();
    }

    /// <summary>An identity field changed: re-run the naming preview, mark dirty and autosave through the
    /// one route every Build-pane edit uses; a failed write reaches the pane's footer.</summary>
    private void OnIdentityEdited()
    {
        RefreshPublishedName();
        if (_loadingIdentityForm) return;   // populating the form from a project is not an edit
        MarkDirty();
        // identity is part of what ships (the folder name, the sidecar, the whole-mod key)
        RefreshBuildResultStale();
        RefreshKeyCollisions();
        QueueIdentitySave();
    }

    /// <summary>Coalesces identity keystrokes into ONE save — a save renames the project folder
    /// (<see cref="EnsureFolderMatchesName"/>), so per-keystroke saves would move it once per letter. A
    /// tick pending at close loses nothing: the close/leave flush is the guaranteed terminal save.</summary>
    private DispatcherTimer? _identitySaveTimer;

    private void QueueIdentitySave()
    {
        if (_identitySaveTimer is null)
        {
            _identitySaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _identitySaveTimer.Tick += (_, _) =>
            {
                // A save renames the folder, and a build or send-back apply is still writing into the
                // one it would vacate. Re-arm and land the save after.
                if (IsModBuilding || _applyingSend) return;
                _identitySaveTimer!.Stop();
                FlushIdentitySave();
            };
        }
        _identitySaveTimer.Stop();
        _identitySaveTimer.Start();
    }

    /// <summary>What one coalesced identity edit lands: the author default FIRST — so it lands even for a
    /// project with no folder yet, where the save is a no-op — then the project itself.</summary>
    internal void FlushIdentitySave()
    {
        RememberDefaultAuthor();
        TryAutoSaveProject();
    }

    /// <summary>The Build pane's Author field IS the default-author setting. Only a user edit reaches
    /// here (populating from a project is gated), so opening someone else's mod never rewrites it.</summary>
    private void RememberDefaultAuthor()
    {
        var author = PackageAuthor?.Trim() ?? "";
        if (string.Equals(author, _settings.Author, StringComparison.Ordinal)) return;
        _settings.Author = author;
        SaveSettings();
    }

    /// <summary>The mod-level line's ONE surface: the Build pane's footer. A ⛔ build failure outranks it
    /// (<see cref="BuildFooter.Notice"/>); an empty line reports nothing rather than blanking the pane.</summary>
    partial void OnBuildStatusChanged(string value)
    {
        if (value.Length > 0) Footer = Footer.Notice(value);
    }

    /// <summary>Copy text to the clipboard. No-op when headless or the text is empty.</summary>
    [RelayCommand]
    private async Task CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text) || MainWindow?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); } catch { /* clipboard unavailable — ignore */ }
    }

    // ---- export / edit / package -------------------------------------------

    [RelayCommand]
    private void GoToEdit() => SelectedStep = "② Edit";

    /// <summary>Rebuild a step's pane whenever it is entered, so it reflects the current state.</summary>
    partial void OnSelectedStepChanged(string value)
    {
        if (value == "① Pick") RefreshTabHeaders();
        else if (value == "② Edit") Workbench.Activate();
        else if (value == "③ Build") EnterBuildStep();
    }

    private void EnsureWatcher()
    {
        if (_modRoot is null) return;
        if (_watcher is null)
        {
            var w = new BlenderSendWatcher(_modRoot, includeSubdirectories: true);
            // Whether this ran inline tells a combined session's receive it is on the scan's thread and
            // must finish there.
            w.EditReceived += e => { StampSend(); OnUi(inline => OnEditReceived(e, inline)); };
            // A failed read-back is NOT a no-op: Blender has already overwritten the workspace glb, so the
            // target must flag edited or Revert stays disabled on the one part that most needs it.
            w.Error += (glb, msg) => { StampSend(); OnUi(_ => OnEditFailed(glb, msg)); };
            _watcher = w;   // assigned first: the scan's handlers re-enter here and must find it armed
            // A send that landed while the app was closed or another mod was open has no watcher event.
            // Taking it HERE — ahead of anything the caller rebuilds — keeps the modder's file from being
            // overwritten unseen.
            w.ScanExisting();
        }
        if (_texWatcher is null)
        {
            var tw = new TextureEditWatcher(_modRoot);
            tw.Changed += p => Dispatcher.UIThread.Post(() => OnTextureEdited(p));
            // A watcher-thread failure surfaces on the footer, never as a crash: if the watcher dies the
            // modder must see that saves stopped landing.
            tw.Error += msg => Dispatcher.UIThread.Post(() =>
                Workbench.ReportStatus($"Texture watch error: {msg}. Re-open the texture to retry."));
            _texWatcher = tw;
        }
    }

    /// <summary>Run on the UI thread, INLINE when the caller is already there, and tell the work which it
    /// got — the offline-send scan must land everything before its next line, which a queued post would
    /// lose to.</summary>
    private static void OnUi(Action<bool> work)
    {
        if (Dispatcher.UIThread.CheckAccess()) work(true);
        else Dispatcher.UIThread.Post(() => work(false));
    }

    /// <summary>When a Blender send was last taken, in UTC ticks. Stamped on the watcher's own thread as
    /// the send is read back, so <see cref="WatchBlenderExit"/> compares against arrival, not dispatcher
    /// order.</summary>
    private long _lastSendTicksUtc;

    private void StampSend() => Interlocked.Exchange(ref _lastSendTicksUtc, DateTime.UtcNow.Ticks);

    /// <summary>How long after Blender exits the "nothing sent" line waits — a send written just before
    /// exit still has to travel the watcher's file events.</summary>
    private static readonly TimeSpan BlenderExitSettle = TimeSpan.FromSeconds(2);

    /// <summary>Say so when a Blender THIS app launched closes having sent nothing back. Event-driven:
    /// nothing waits on the handle, each launch watches its own process, and the captured mod keeps a
    /// session outlived by a project switch from reporting onto the mod that replaced it.
    ///
    /// <para>A session opened from a PART's row also names that part, whose opens then refuse while this
    /// process lives — a second session on the same part sends back to the same file. Marked only once the
    /// exit is watchable, so the flag never outlives the event that clears it.</para></summary>
    private void WatchBlenderExit(Process proc, IProgress<string> status,
        Workbench.WorkbenchSubjectRef? sessionSubject = null, string? sessionPart = null)
    {
        var launchedTicks = DateTime.UtcNow.Ticks;
        var projectAtLaunch = _project;
        try { proc.EnableRaisingEvents = true; }
        // No handle to watch (already reaped, or the OS refused one): the send-back path is unaffected.
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        { proc.Dispose(); return; }
        if (sessionSubject is not null && sessionPart is not null)
            Workbench.SetPartSession(sessionSubject, sessionPart, alive: true);
        proc.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            proc.Dispose();
            if (sessionSubject is not null && sessionPart is not null)
                Workbench.SetPartSession(sessionSubject, sessionPart, alive: false);
            DispatcherTimer.RunOnce(() =>
            {
                if (!ReferenceEquals(projectAtLaunch, _project)) return;
                if (Interlocked.Read(ref _lastSendTicksUtc) <= launchedTicks)
                    status.Report("Blender closed · nothing sent");
            }, BlenderExitSettle);
        });
    }

    /// <summary>A "Send to Lab" landed; the bridge already decoded the glb.</summary>
    /// <param name="inline">The send arrived from the offline scan, on a caller's thread that must not
    /// return until the receive has landed. False lets a combined apply run off the UI thread.</param>
    private void OnEditReceived(IncomingEdit edit, bool inline)
    {
        if (Path.GetFileName(edit.GlbPath).Equals(AssetExporter.CombinedSendGlbName, StringComparison.OrdinalIgnoreCase))
        {
            // Inline is finished the moment it hands the task back, so this rethrows rather than blocks —
            // and it has to: the watcher reports what escapes here, and a discarded task reports nothing.
            if (inline) ApplyCombinedSendAsync(edit, offThread: false).GetAwaiter().GetResult();
            else BeginCombinedApply(edit);
            return;
        }
        // On a part's own workspace glb there is no "emptied" gesture, and the file is what the pipeline
        // compiles — so say nothing came back rather than record an empty part as an edit.
        if (edit.Mesh is null)
        {
            Workbench.ReportStatus($"The Blender send carried no mesh for {Path.GetFileNameWithoutExtension(edit.GlbPath)}. Nothing was changed.");
            return;
        }
        if (_project.RootDir is null) return;
        var rel = Rel(edit.GlbPath);
        var t = _project.Targets.FirstOrDefault(x => x.AssetType == "Mesh"
            && string.Equals(x.ReplaceFile, rel, StringComparison.OrdinalIgnoreCase));
        if (t is null) return;
        // The replacement transition voids the previous send-back's donor record; this one lands over it.
        MarkTargetEdited(edit.GlbPath);
        var subject = SubjectForMeshDir(SafeDir(Path.GetFullPath(edit.GlbPath)));
        SendBackCollect collected;
        // No mod folder in hand is no textures/ to intake into: the maps say nothing.
        if (_modRoot is null) collected = SendBackCollect.NoMaps;
        else
        {
            try { collected = CollectDonorTextures(_modRoot, t, MeshGltf.ParsedGlb.Open(edit.GlbPath),
                      GameStockRmo(subject, t), EditedStockPngs(_project)); }
            catch (Exception e) { collected = SendBackCollect.Unreadable(e); }
        }
        collected.CommitTo(t);
        var takenMaps = collected.Maps;
        // The glb carries the part's mesh, so an earlier Hide is over. (The emptied-collection direction
        // can't reach here — the bridge refuses a session that carries no geometry at all.)
        if (subject is { } subj)
            _project.SetHidden(subj.Character, subj.Stem, t.ObjectName, false);
        int added = NewSubmeshCount(subject, t);
        AutoSave();
        Workbench.NotifyMeshEdited(edit.GlbPath);
        WarnIfNodeTransformIgnored(edit, SendBackSummary(
            new[] { Path.GetFileNameWithoutExtension(edit.GlbPath) }, Array.Empty<string>(),
            Array.Empty<string>(), takenMaps, newSubmeshes: added));
    }

    /// <summary>How many submeshes a send-back added past the game part's own material count — 0 when it
    /// added none, and 0 when the Edit tree can't answer: without the tree's slot count there is no
    /// baseline, and a guessed summary is worse than silence.</summary>
    private int NewSubmeshCount((string Character, string Stem)? subject, ProjectTarget t)
    {
        if (subject is not { } s || t.DonorMaterials is not { Count: > 0 } donor) return 0;
        return Workbench.GameSubmeshCount(s.Character, s.Stem, t.ObjectName) is { } game && donor.Count > game
            ? donor.Count - game
            : 0;
    }

    /// <summary>Read the map slots a modder plugged in inside Blender, and HOLD the result. A slot left
    /// alone is not recorded, so an untouched edit builds exactly as before. Nothing lands on the target
    /// here: the slots are half of the per-part "take it?" decision, so
    /// <see cref="SendBackCollect.CommitTo"/> records only once the part is taken, after the flag that
    /// voids the file's previous record. An authored image does reach <c>textures/</c> as it is read.
    ///
    /// <para>Never throws — a failed texture read must not cost the geometry edit that arrived with it;
    /// what went wrong rides the return value, and the send-back summary writes the status line once.</para>
    ///
    /// <para><paramref name="recordGlb"/>: where the map-origin record lives when it isn't beside the
    /// arriving glb (a combined send lands under its own name); null reads beside the glb. Reads no
    /// UI-thread state and no field — <paramref name="gameRmo"/>, <paramref name="editedStock"/> and
    /// <paramref name="modRoot"/> are taken in advance, which lets a combined receive run this off the UI
    /// thread.</para></summary>
    private static SendBackCollect CollectDonorTextures(string modRoot, ProjectTarget t, MeshGltf.ParsedGlb glb,
        IReadOnlyList<StockRmoSlot> gameRmo, IReadOnlySet<string> editedStock, string? meshName = null,
        string? recordGlb = null)
    {
        var glbPath = glb.Path;
        string Rel(string abs) => Path.GetRelativePath(modRoot, abs).Replace('\\', '/');
        var notes = new List<string>();
        try
        {
            var record = recordGlb ?? glbPath;
            // Images off the arriving glb, origins off the record: a slot classified against a record that
            // isn't there reads authored, and every untouched stock map then asks the build to ship a copy.
            var maps = MeshGltf.ReadSubmeshMaps(glb, meshName, record);
            var stem = ModNaming.Slug(t.ObjectName ?? Path.GetFileNameWithoutExtension(glbPath));
            // The part's OWN stock maps, separating "bound to its own vanilla map" (absent → inherit)
            // from a deliberate sibling-map link — read off the session's record. The MESH NAME is the
            // owner, not the glb's first mesh: a rename in Blender must not re-point ownership.
            var ownStock = PreviewMaps.ReadOwnedStock(record, meshName ?? t.ObjectName);
            // An authored RMO's alpha comes off the stock map the session glb embedded on that submesh,
            // so the mask Blender cannot carry survives the trip — identically on a re-send. Sidecar rows
            // key the exported name, same as the ownership read; only the glb-internal read above keys
            // what came back.
            var stockRmoPngs = PreviewMaps.ReadSubmeshRmoSources(record, meshName ?? t.ObjectName);
            var rows = DonorTextureIntake.Collect(maps, Path.Combine(modRoot, "textures"), stem, Rel, ownStock,
                StockRmoSource(gameRmo, Path.GetFileNameWithoutExtension(t.ReplaceFile), stockRmoPngs, notes),
                notes.Add, png => editedStock.Contains(Path.GetFullPath(png)));
            return new SendBackCollect(rows,
                // Present the part by what the mesh carries: the renderer's slots are wrong the moment a
                // submesh is added.
                maps.Count > 0 ? maps.Select(m => m.MaterialName).ToList() : null,
                new SendBackMaps(Count(rows, SlotOrigin.Authored), BlankedSlotCount(rows), notes));
        }
        catch (Exception e)
        {
            notes.Add(SendBackCollect.UnreadableNote(e));
            return new SendBackCollect(null, null, SendBackMaps.None with { Notes = notes }, Read: false);
        }

        static int Count(List<SubmeshTextures>? rows, SlotOrigin ask)
        {
            int Is(SlotOrigin slot) => slot == ask ? 1 : 0;
            return rows?.Sum(r => Is(r.AlbedoAsk) + Is(r.NormalAsk) + Is(r.RmoAsk)) ?? 0;
        }
    }

    /// <summary>The workspace PNGs carrying a texture edit, as full paths — the byte-compare the Retexture
    /// verb keys on, snapshotted so a receive can consult it off the UI thread. A session map returned
    /// untouched that sits in this set is what the modder saw in Blender: the intake records it as the
    /// replacement's own map instead of letting the slot fall back to the game's bytes.</summary>
    private static IReadOnlySet<string> EditedStockPngs(ModProject project)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in project.Targets)
        {
            if (t.AssetType != "Texture2D" || !project.IsTargetPresent(t) || !project.IsEdited(t)) continue;
            // an unresolvable path can't match a session map either way; the slot keeps its fall-back
            try { set.Add(Path.GetFullPath(project.Resolve(t.ReplaceFile))); }
            catch (ArgumentException) { }
        }
        return set;
    }

    /// <summary>How many of a send-back's slots the build will ship FLAT, by the build's own rule
    /// (<see cref="BlankedSlots"/>). Counting explicit blanks alone would miss the common case — a submesh
    /// that authored something and named no normal/RMO.</summary>
    internal static int BlankedSlotCount(IReadOnlyList<SubmeshTextures>? rows)
    {
        int n = 0;
        foreach (var r in rows ?? Array.Empty<SubmeshTextures>())
        {
            var flat = BlankedSlots.Of(r, EditVerbs.Replace);
            if (flat.Albedo) n++;
            if (flat.Normal) n++;
            if (flat.Rmo) n++;
        }
        return n;
    }

    /// <summary>What the GAME renderer binds behind one submesh of a part, resolved to a file. A slot the
    /// Edit tree can speak for is present in <see cref="GameStockRmo"/>'s list; one past its length is a slot
    /// the tree has no answer for at all, which is not the same news as a slot that genuinely binds
    /// nothing.</summary>
    /// <param name="Png">The workspace PNG behind the slot's RMO, or null where there is none to point at.
    /// </param>
    /// <param name="BindsNone">The slot binds no RMO at all, so there is no mask to lose and nothing to
    /// report. A null <paramref name="Png"/> without this is a mask that should have been there.</param>
    private readonly record struct StockRmoSlot(string? Png, bool BindsNone);

    /// <summary>Resolve every RMO the game renderer binds behind a part, in submesh order. The Edit tree is
    /// UI-thread state and a combined receive's per-part work is not, so the answers are taken here in one
    /// pass and travel with the part. Empty where the tree does not hold the part.</summary>
    private IReadOnlyList<StockRmoSlot> GameStockRmo((string Character, string Stem)? subject, ProjectTarget t)
    {
        if (subject is not { } s
            || Workbench.GameRmoSlots(s.Character, s.Stem, t.ObjectName) is not { } slots || slots <= 0)
            return Array.Empty<StockRmoSlot>();
        var resolved = new StockRmoSlot[slots];
        for (int i = 0; i < slots; i++)
        {
            var (_, rmo) = Workbench.GameRmoMap(s.Character, s.Stem, t.ObjectName, i);
            if (rmo is null) { resolved[i] = new StockRmoSlot(null, BindsNone: true); continue; }
            string? file = null;
            if (Materializer.TextureTarget(_project, s.Character, s.Stem, rmo.BundleId, rmo.TextureName) is { } tex)
            {
                try { file = Path.GetFullPath(_project.Resolve(tex.ReplaceFile)); } catch { file = null; }
                if (file is not null && !File.Exists(file)) file = null;
            }
            resolved[i] = new StockRmoSlot(file, BindsNone: false);
        }
        return resolved;
    }

    /// <summary>Where an authored RMO's emissive mask is read from, per submesh: the returned glb's own
    /// record, else the GAME renderer's RMO for that slot resolved to its workspace PNG. The record wins;
    /// a glb with none (missing sidecar, a part re-split out of a session) would otherwise ship the mask
    /// as a dead zero. When neither answers, <paramref name="notes"/> says so — a blanked mask is never
    /// silent.</summary>
    private static Func<int, string?> StockRmoSource(IReadOnlyList<StockRmoSlot> game, string label,
        IReadOnlyDictionary<int, string> recorded, ICollection<string> notes) =>
        submesh =>
        {
            if (recorded.TryGetValue(submesh, out var png)) return png;
            var slot = submesh >= 0 && submesh < game.Count ? game[submesh] : default;
            if (slot.BindsNone) return null;   // the slot binds no RMO: there is no mask to lose
            if (slot.Png is not null) return slot.Png;
            notes.Add($"Couldn't find the stock RMO behind {label}. The RMO ships with no emissive mask.");
            return null;
        };

    /// <summary>A "Send to Lab" landed but wouldn't read back. A single-part send already overwrote the
    /// workspace glb, so its target is marked edited and Revert lights up. A combined session's send lands
    /// under its own name, so a failed combined read-back only reports.</summary>
    private void OnEditFailed(string glbPath, string message)
    {
        var name = Path.GetFileNameWithoutExtension(glbPath);
        if (_project.RootDir is not null
            && !Path.GetFileName(glbPath).Equals(AssetExporter.CombinedSendGlbName, StringComparison.OrdinalIgnoreCase))
        {
            var rel = Rel(glbPath);
            var t = _project.Targets.FirstOrDefault(x => x.AssetType == "Mesh"
                && string.Equals(x.ReplaceFile, rel, StringComparison.OrdinalIgnoreCase));
            if (t is not null)
            {
                MarkTargetEditedAfterFailedRead(glbPath);   // Edited reflects the on-disk overwrite
                AutoSave();
                Workbench.NotifyMeshEdited(glbPath);
                Workbench.ReportStatus(
                    $"Couldn't read {name} back from Blender: {message} The part's working file was overwritten. Revert restores the original.");
                return;
            }
        }
        Workbench.ReportStatus($"Couldn't read {name} back from Blender: {message}");
    }

    /// <summary>Post the send-back confirmation, warning when an object-mode move/rotate was dropped — the
    /// import reads geometry only, so a non-identity node transform would vanish silently.</summary>
    private void WarnIfNodeTransformIgnored(IncomingEdit edit, string applied)
    {
        Workbench.ReportStatus(edit.NodeTransformIgnored
            ? $"{applied} Object-mode move/rotate wasn't sent. Edit vertices in Edit mode instead."
            : applied);
    }

    /// <summary>A "Send to Lab" of an outfit session: split the multi-mesh glb by object name and rewrite
    /// each part's own workspace glb, so packaging stays per-part.
    ///
    /// <para>Four fates per target. In the returned glb AND carrying a change ⇒ taken. In the glb
    /// unchanged (same mesh, same skin, no map slot asking) ⇒ left as found — a send-all returns every
    /// writable part whether or not it was touched. Named in the send's emptied list ⇒ HIDDEN, workspace
    /// glb untouched. Anything else was context and is left alone. Absence is never intent — most of the
    /// outfit is absent from every send, which is why the emptied list is explicit.</para>
    ///
    /// <para>The parse, each map intake and each re-split run through <c>Step</c>:
    /// <see cref="Task.Run(Func{TResult})"/> when <paramref name="offThread"/>, a straight call when not
    /// (the offline scan needs the whole receive to land before its next line). Everything touching the
    /// project or the Edit tree stays on the UI thread either way — the modes differ in marshalling
    /// only.</para></summary>
    internal async Task ApplyCombinedSendAsync(IncomingEdit edit, bool offThread)
    {
        Task<T> Step<T>(Func<T> work) => offThread ? Task.Run(work) : Task.FromResult(work());

        // The project this receive is FOR. An apply that yields can come back to a mod the modder has since
        // closed or swapped, and every write below names files of the one it started on.
        var project = _project;
        // Re-asked after EVERY await, never once at the top: a swap landing between two of them would put the
        // rest of the receive — the ledger writes, the save, the summary — on whatever mod is open now.
        bool StillOpen() => ReferenceEquals(project, _project);

        if (project.RootDir is not { } modRoot) { Workbench.ReportStatus(SendModNotOpen); return; }
        var combinedGlb = edit.GlbPath;
        var dir = SafeDir(Path.GetFullPath(combinedGlb));
        var targets = project.Targets.Where(t => t.AssetType == "Mesh" && TargetInDir(modRoot, t, dir)).ToList();
        if (targets.Count == 0) { Workbench.ReportStatus(SendMatchedNothing); return; }
        // Parsed once for the whole receive: every per-part question is a read of this same file.
        MeshGltf.ParsedGlb returned;
        try { returned = await Step(() => MeshGltf.ParsedGlb.Open(combinedGlb)); }
        catch (Exception e)
        {
            Workbench.ReportStatus($"Couldn't read the Blender send back: {e.Message}");
            return;
        }
        // The map-origin record sits beside the app-published combined, not the send file, which lands under
        // a name of its own so it never overwrites the published build.
        var recordGlb = Path.Combine(dir, AssetExporter.CombinedGlbName);
        var present = new HashSet<string>(returned.MeshNames, StringComparer.Ordinal);
        var emptied = new HashSet<string>(edit.HiddenParts ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var subject = SubjectForMeshDir(dir);
        // Snapshotted once for the whole receive: the intake consults it off the UI thread.
        var editedStock = EditedStockPngs(project);
        var failed = new List<string>();   // one bad part must not abort the rest — but it must be SAID
        var failedWhy = new List<string>();
        var changed = new List<string>();
        var applied = new List<string>();
        var hidden = new List<string>();
        var takenMaps = SendBackMaps.None;
        int added = 0;
        int leftAlone = 0;   // came back carrying no change: reported as such, never as an applied edit
        foreach (var t in targets)
        {
            if (!StillOpen()) { Workbench.ReportStatus(SendModNotOpen); return; }
            var label = Path.GetFileNameWithoutExtension(t.ReplaceFile);
            if (emptied.Contains(t.ObjectName))
            {
                if (subject is { } hs) project.SetHidden(hs.Character, hs.Stem, t.ObjectName, true);
                hidden.Add(label);
                continue;              // no write: a hidden part keeps the workspace glb it already had
            }
            if (!present.Contains(t.ObjectName)) continue;   // context in this session, not a send-back
            try
            {
                var wsGlb = project.Resolve(t.ReplaceFile);
                var gameRmo = GameStockRmo(subject, t);
                // Textures ride the SESSION glb: stock for ANY part in the session is stock here too.
                // Read first and held — whether the slots asked is half of the part's fate.
                var collected = await Step(() =>
                    CollectDonorTextures(modRoot, t, returned, gameRmo, editedStock, t.ObjectName, recordGlb));
                if (!StillOpen()) { Workbench.ReportStatus(SendModNotOpen); return; }
                var authored = AuthoredMapPaths(project, collected.Rows);
                // The re-split reads the same record, so the stock maps it embeds are the ones this read
                // resolved — the only copy that survives a lone re-open. That published combined is also
                // the "did this part change" baseline: the file the session was opened from.
                var taken = await Step(() => SendBackGeometry.Take(returned, t.ObjectName, wsGlb, collected.Asks,
                    recordGlb: recordGlb, authoredMaps: authored, baselineGlb: recordGlb));
                if (!StillOpen()) { Workbench.ReportStatus(SendModNotOpen); return; }
                if (taken)
                {
                    RecordTakenPart(project, t, wsGlb, collected);
                    changed.Add(wsGlb);
                    takenMaps += collected.Maps;
                    added += NewSubmeshCount(subject, t);
                    applied.Add(label);
                }
                else leftAlone++;
                // The send carries this part's mesh, so it expresses "shown" for it — changed or not.
                if (subject is { } ss) project.SetHidden(ss.Character, ss.Stem, t.ObjectName, false);
            }
            // keep the reason — "couldn't read it back" with no why sends the modder hunting blind
            catch (Exception ex) { failed.Add(label); failedWhy.Add(ex.Message); }
        }
        // The last target's awaits are past the loop's own guard, and everything below writes or speaks for
        // the project: the save is of whatever _project is now, and the summary belongs to this one's tree.
        if (!StillOpen()) { Workbench.ReportStatus(SendModNotOpen); return; }
        AutoSave();
        Workbench.NotifyMeshesEdited(changed);
        WarnIfNodeTransformIgnored(edit,
            SendBackSummary(applied, failed, hidden, takenMaps, failedWhy, added, leftAlone));
    }

    /// <summary>Every combined-apply exit when the mod it was read for is no longer open. The footer is
    /// already saying the apply is running, so a silent exit leaves it claiming work that stopped.</summary>
    internal const string SendModNotOpen = "Send not applied: its mod is no longer open.";

    /// <summary>The send named no part of the open mod at all — a different answer from a send whose parts
    /// all came back carrying nothing.</summary>
    internal const string SendMatchedNothing = "Nothing in the Blender send matched a part of this mod.";

    /// <summary>The ledger half of taking a returned part, after <see cref="SendBackGeometry.Take"/> has
    /// rewritten its workspace glb: flag replaced (voiding the file's previous donor record), then land the
    /// held map record after that flag. A part left alone records nothing at all. Kept apart from the
    /// rewrite because the rewrite runs off the UI thread and the project is only written on it.</summary>
    internal static void RecordTakenPart(ModProject project, ProjectTarget t, string workspaceGlb,
        SendBackCollect collected)
    {
        project.MarkFileReplaced(workspaceGlb);
        collected.CommitTo(t);
    }

    /// <summary>A combined send-back is being applied. UI-thread only, like everything it gates.</summary>
    private bool _applyingSend;

    /// <summary>Sends that landed while another was being applied, oldest first. Refusing one would lose
    /// the modder's work — Blender already overwrote the send file and its sidecar is spent.
    ///
    /// <para>Keyed by FILE: a mod with two outfits has a send file per subject's <c>meshes/</c> folder,
    /// and a one-entry queue would drop a subject. A newer send REPLACES a queued one naming the same
    /// file — the apply re-reads the file and only the latest contents are on disk.</para></summary>
    private readonly List<IncomingEdit> _queuedSends = new();

    /// <summary>The live apply in flight, or a completed task. Held so a caller that must not overlap one can
    /// wait it out.</summary>
    private Task _sendApply = Task.CompletedTask;

    /// <inheritdoc cref="_sendApply"/>
    internal Task SendApplyInFlight => _sendApply;

    /// <summary>Start a live send-back's apply, or hand it to the one already running.</summary>
    internal void BeginCombinedApply(IncomingEdit edit)
    {
        if (_applyingSend)
        {
            QueueSend(edit);
            Workbench.ReportStatus("Another Blender send arrived. It applies when this one finishes.");
            return;
        }
        _sendApply = RunCombinedApplyAsync(edit);
    }

    /// <summary>Park a send behind the running apply: latest wins for a file already queued, arrival order
    /// otherwise.</summary>
    private void QueueSend(IncomingEdit edit)
    {
        int at = _queuedSends.FindIndex(q => SamePath(q.GlbPath, edit.GlbPath));
        if (at >= 0) _queuedSends[at] = edit;
        else _queuedSends.Add(edit);
    }

    /// <summary>The next send to apply, oldest first, or null when the queue holds nothing this mod can use.
    /// A queued send names a file of the mod it was written for; one stranded by a mod switch is DROPPED and
    /// said, since applying it would ask another mod's ledger about files that are not in it.</summary>
    private IncomingEdit? TakeQueuedSend()
    {
        while (_queuedSends.Count > 0)
        {
            var queued = _queuedSends[0];
            _queuedSends.RemoveAt(0);
            if (SendIsInOpenMod(queued.GlbPath)) return queued;
            Workbench.ReportStatus(SendModNotOpen);
        }
        return null;
    }

    /// <summary>Whether a send file sits inside the mod that is open NOW. What a queued send outlives is the
    /// mod that owned it, and nothing else about the file says which one that was.</summary>
    private bool SendIsInOpenMod(string glbPath)
    {
        if (_project.RootDir is not { } root) return false;
        try
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            return Path.GetFullPath(glbPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Apply live send-backs one at a time, holding the workbench's verb gate: the apply rewrites
    /// the same workspace glbs Open in Blender and Revert do. A verb that started BEFORE the hold is
    /// waited out rather than refused — it is already writing these files.</summary>
    private async Task RunCombinedApplyAsync(IncomingEdit edit)
    {
        _applyingSend = true;
        try
        {
            using var held = await Workbench.HoldVerbsAsync();
            var next = edit;
            while (true)
            {
                Workbench.ReportStatus("Applying the Blender send…");
                try { await ApplyCombinedSendAsync(next, offThread: true); }
                catch (Exception e) { Workbench.ReportStatus($"Couldn't apply the Blender send: {e.Message}"); }
                if (TakeQueuedSend() is not { } queued) return;
                next = queued;
            }
        }
        // Nothing awaits this task, so anything escaping the inner catch would die unobserved and the footer
        // would keep claiming the apply is running.
        catch (Exception e) { Workbench.ReportStatus($"Couldn't apply the Blender send: {e.Message}"); }
        finally
        {
            _applyingSend = false;
            RunQueuedRescan();   // this apply was one of the holds a rescan waits behind
        }
    }

    /// <summary>The intake's authored map files per submesh, absolute — what the re-split re-embeds so the
    /// part opens on the modder's own maps. Already written by the intake, so a path not on disk
    /// contributes nothing rather than a broken embed. Indexed by submesh.</summary>
    internal static IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? AuthoredMapPaths(
        ModProject project, IReadOnlyList<SubmeshTextures>? rows)
    {
        if (rows is not { Count: > 0 }) return null;
        int n = rows.Max(r => r.Submesh) + 1;
        if (n <= 0) return null;
        var byIndex = new (string?, string?, string?)[n];
        foreach (var r in rows)
            if (r.Submesh >= 0)
                byIndex[r.Submesh] = (Abs(r.Albedo), Abs(r.Normal), Abs(r.Rmo));
        return byIndex;

        string? Abs(string? rel)
        {
            if (rel is null) return null;
            try
            {
                var full = Path.GetFullPath(project.Resolve(rel));
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }
    }

    /// <summary>What one send-back's map slots asked for, plus intake notes. An authored map ships a file
    /// and a blanked slot ships none — separate news; the notes carry whatever degraded on the way.</summary>
    internal readonly record struct SendBackMaps(int Authored, int Blanked, IReadOnlyList<string> Notes)
    {
        public static readonly SendBackMaps None = new(0, 0, Array.Empty<string>());

        /// <summary>Roll one part's asks into the session's, so a combined send reports one total.</summary>
        public static SendBackMaps operator +(SendBackMaps a, SendBackMaps b) =>
            new(a.Authored + b.Authored, a.Blanked + b.Blanked, a.Notes.Concat(b.Notes).ToList());
    }

    /// <summary>One returned part's map slots, resolved and held before anything is written: the read
    /// comes first, the record lands only where the part is taken.</summary>
    /// <param name="Read">False where the slots could not be read at all — not the same answer as "asked
    /// for nothing".</param>
    internal sealed record SendBackCollect(List<SubmeshTextures>? Rows, List<string>? Materials,
        SendBackMaps Maps, bool Read = true)
    {
        /// <summary>A part whose maps could not be reached: nothing to record, nothing to learn.</summary>
        public static readonly SendBackCollect NoMaps = new(null, null, SendBackMaps.None);

        /// <summary>Whether the maps alone are reason to take the part: some slot asked, or the read could
        /// not say — a read that answered nothing must not pass for "asked for nothing".</summary>
        public bool Asks => !Read
            || Rows is not null && Rows.Any(r => r.AlbedoAsk.IsAsk() || r.NormalAsk.IsAsk() || r.RmoAsk.IsAsk());

        /// <summary>Write the held record onto the target. Runs AFTER
        /// <see cref="ModProject.MarkFileReplaced"/>, which voids whatever record the file's previous
        /// contents left, so this one stands rather than being cleared behind it.</summary>
        public void CommitTo(ProjectTarget t)
        {
            t.DonorTextures = Rows;
            t.DonorMaterials = Materials;
        }

        public static SendBackCollect Unreadable(Exception e) =>
            new(null, null, SendBackMaps.None with { Notes = new[] { UnreadableNote(e) } }, Read: false);

        public static string UnreadableNote(Exception e) =>
            $"Couldn't read the textures back from Blender: {e.Message} The mesh edit was kept.";
    }

    /// <summary>The status line for a finished outfit send-back — a single part's is the same line with
    /// one applied name. <paramref name="newSubmeshes"/>: submeshes the send carried past the game parts'
    /// material counts — nothing else ties the tree's donor-named rows to the send that made them.
    /// <paramref name="applied"/> is what the send-back TOOK, never what it merely carried (a send-all
    /// returns the whole outfit); parts carried but untouched are <paramref name="leftAlone"/>, which
    /// speaks only when nothing was taken.</summary>
    internal static string SendBackSummary(IReadOnlyList<string> applied, IReadOnlyList<string> failed,
        IReadOnlyList<string> hidden, SendBackMaps maps, IReadOnlyList<string>? failedWhy = null,
        int newSubmeshes = 0, int leftAlone = 0)
    {
        var bits = new List<string>();
        if (failed.Count > 0)
        {
            bits.Add($"Applied Blender edits to {applied.Count} of {applied.Count + failed.Count} parts. "
                   + $"Couldn't read {string.Join(", ", failed)} back.");
            // one reason, not a wall — distinct messages only
            if (failedWhy is { Count: > 0 })
                bits.Add(string.Join(" ", failedWhy.Distinct()));
        }
        else if (applied.Count == 1) bits.Add($"Applied Blender edit to {applied[0]}.");
        else if (applied.Count > 1) bits.Add($"Applied Blender edits to {applied.Count} parts.");
        if (newSubmeshes > 0)
            bits.Add($"The send added {newSubmeshes} submesh{(newSubmeshes == 1 ? "" : "es")}.");
        if (hidden.Count > 0) bits.Add($"Hidden in the mod: {string.Join(", ", hidden)}.");
        // a blanked slot ships nothing, so counting it as an authored map would promise a file
        var slots = new List<string>();
        // An authored slot is not always a map painted in Blender: a sibling part's map linked in the shader
        // editor and an untouched own map with a texture edit behind it are recorded the same way, and both
        // reach this count. The word covers all three.
        if (maps.Authored > 0) slots.Add($"{maps.Authored} map{S(maps.Authored)} authored");
        if (maps.Blanked > 0) slots.Add($"{maps.Blanked} slot{S(maps.Blanked)} blanked");
        if (slots.Count > 0) bits.Add(string.Join(" · ", slots) + ".");
        if (maps.Notes.Count > 0) bits.Add(string.Join(" ", maps.Notes.Distinct()));
        // Only where there is no other news — a different answer from a send that matched nothing.
        if (bits.Count == 0)
            bits.Add(leftAlone > 0 ? "Nothing changed in the Blender send." : SendMatchedNothing);
        return string.Join(" ", bits);

        static string S(int n) => n == 1 ? "" : "s";
    }

    /// <summary>The subject a <c>meshes/</c> folder belongs to, derived forward from the ledger (each
    /// entry's <see cref="Materializer.SubjectFolder"/> IS the folder name) rather than by taking a folder
    /// name apart. Null when no selected subject owns it.</summary>
    private (string Character, string Stem)? SubjectForMeshDir(string meshesDir)
    {
        string folder;
        try { folder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(meshesDir)) ?? "") ?? ""; }
        catch { return null; }
        if (folder.Length == 0) return null;
        foreach (var s in _project.Selection)
            if (string.Equals(Materializer.SubjectFolder(s.Character, s.Outfit), folder, StringComparison.OrdinalIgnoreCase))
                return (s.Character, s.Outfit);
        return null;
    }

    private static string SafeDir(string full) { try { return Path.GetDirectoryName(full) ?? full; } catch { return full; } }

    /// <summary>Does the target's workspace glb sit in <paramref name="dir"/> (the combined glb's meshes/
    /// dir)? <paramref name="modRoot"/> is handed in rather than read off the open project: the caller
    /// filters the targets of the project it CAPTURED, and resolving them against whichever mod is open now
    /// would answer about another tree.</summary>
    private static bool TargetInDir(string modRoot, ProjectTarget t, string dir)
    {
        try
        {
            return string.Equals(SafeDir(Path.GetFullPath(Path.Combine(modRoot, t.ReplaceFile))), dir,
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>An image editor saved over a workspace texture: flag the target, take whatever adoption the
    /// edit opens, refresh its card, and persist.</summary>
    private void OnTextureEdited(string pngPath)
    {
        var full = Path.GetFullPath(pngPath);
        var target = MarkTargetEdited(pngPath);
        var line = AdoptTextureEdit(target);
        Workbench.NotifyTextureFileChanged(full);   // the adoption moved donor rows the cards read
        AutoSave();
        if (line is not null) Workbench.ReportStatus(line);
    }

    /// <summary>Flag the target whose replace file is this glb as edited, dropping the donor record the
    /// file's previous contents left (<see cref="ModProject.MarkFileReplaced"/>). Returns the target, or
    /// null when no target owns the file.</summary>
    private ProjectTarget? MarkTargetEdited(string glbPath)
    {
        if (_project.RootDir is null) return null;
        return _project.MarkFileReplaced(glbPath);
    }

    /// <summary>Texture edits whose adoption a running build held back, each with the project it was made
    /// in: a mod switched while the run was in flight leaves entries that are no longer this project's, and
    /// writing them onto the mod now open would move rows the modder never edited.</summary>
    private readonly List<(ModProject Project, ProjectTarget Texture)> _heldAdoptions = new();

    /// <summary>A game texture just became edited, so the replacement that rebinds its slot takes it over as
    /// its own map HERE, where the modder acted: the project as it stands decides it, and ② Edit says what
    /// happened in one line. Returns that line, or null when there is nothing to say. The project is written
    /// but not persisted — the callers save the edit mark and the donor rows together.
    ///
    /// <para>A run in flight is reading the project, so the adoption is HELD and taken when the run ends. A
    /// subject whose model the workbench doesn't hold is skipped: the edit stays plain, and the build's own
    /// warning is the backstop that reports it.</para></summary>
    internal string? AdoptTextureEdit(ProjectTarget? texture)
    {
        if (texture is null || texture.AssetType != "Texture2D") return null;
        if (IsModBuilding) { _heldAdoptions.Add((_project, texture)); return null; }
        return TakeAdoptions(_project, new[] { texture }).Line;
    }

    /// <summary>The workbench built this subject's model, so the edits that were made while nothing held one
    /// get their pass through the same seam. Without it an edit made before the subject was ever opened —
    /// the model is a peek, never a build, on the UI thread — stays plain until the modder saves the file
    /// again; the tree landing is exactly the moment the missing input arrives. It also heals a replacement
    /// whose donor record a failed send-back read wiped: the adopted maps went with it, and this puts them
    /// back.
    ///
    /// <para>Only what was TAKEN is announced. A held slot is a standing state, not something that just
    /// happened, and the tree is rebuilt on every hop into ② Edit — the build pane's warning is where that
    /// state is reported, once, with its remedy.</para>
    ///
    /// <para>The line goes BACK to the caller rather than onto the status line. The tree build sweeps every
    /// subject and then writes the pane's one line itself, so anything written from here would be gone
    /// before it was read.</para></summary>
    public string? AdoptSubjectTextureEdits(Workbench.WorkbenchSubjectRef subject, SubjectModel model)
    {
        var edited = _project.Targets
            .Where(t => t.AssetType == "Texture2D"
                && string.Equals(t.SubjectCharacter, subject.Character, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.SubjectOutfit, subject.Stem, StringComparison.OrdinalIgnoreCase)
                && _project.IsTargetPresent(t) && _project.IsEdited(t))
            .ToList();
        if (edited.Count == 0) return null;
        if (IsModBuilding)
        {
            // the run is reading the project; the same hold the watcher's own edits take
            foreach (var t in edited) _heldAdoptions.Add((_project, t));
            return null;
        }
        var taken = new List<TextureAdoption>();
        foreach (var texture in edited)
            taken.AddRange(TextureAdoptions.Apply(_project,
                TextureAdoptions.CandidatesFor(_project, model, texture)));
        if (taken.Count == 0) return null;
        AutoSave();
        Workbench.RefreshNodeStates();
        return TextureAdoptions.Adopted(taken);
    }

    /// <summary>Take every adoption these texture edits open, and give back the one line ② Edit shows for
    /// them. BOTH halves are reported: a map shared across parts can adopt on one and find another's slot
    /// already spoken for, and announcing only the half that worked would report that as plain success. The
    /// adopted line leads, the held slots follow. <c>Changed</c> is true when the project was written and
    /// needs persisting.</summary>
    private (string? Line, bool Changed) TakeAdoptions(ModProject project,
        IReadOnlyList<ProjectTarget> textures)
    {
        var taken = new List<TextureAdoption>();
        var blocked = new List<AdoptionBlocked>();
        foreach (var texture in textures)
        {
            if (SubjectModelInHand(texture) is not { } model) continue;
            taken.AddRange(TextureAdoptions.Apply(project,
                TextureAdoptions.CandidatesFor(project, model, texture, blocked)));
        }
        var parts = new List<string>();
        if (taken.Count > 0) parts.Add(TextureAdoptions.Adopted(taken));
        if (TextureAdoptions.SlotTaken(blocked) is { Length: > 0 } held) parts.Add(held);
        return (parts.Count > 0 ? string.Join(" ", parts) : null, taken.Count > 0);
    }

    /// <summary>The subject model the workbench already holds for a target's subject, or null. A PEEK, never
    /// a build: this runs on the UI thread at the moment of an edit, and building a model there costs bundle
    /// deobfuscation plus prefab reads with the window frozen behind them.</summary>
    private SubjectModel? SubjectModelInHand(ProjectTarget target) =>
        target.SubjectCharacter is { } character && target.SubjectOutfit is { } stem
            ? _subjectModels.TryGet(character, stem)
            : null;

    /// <summary>Take the adoptions a run held back, now that it has ended. Same seam, one pass later: the
    /// candidates are computed against the project as the run left it, so an edit made mid-build reaches the
    /// replacement instead of being orphaned.</summary>
    internal void TakeHeldAdoptions()
    {
        if (_heldAdoptions.Count == 0) return;
        var pending = _heldAdoptions.Where(x => ReferenceEquals(x.Project, _project))
            .Select(x => x.Texture).ToList();
        _heldAdoptions.Clear();
        if (pending.Count == 0) return;
        var (line, changed) = TakeAdoptions(_project, pending);
        if (changed) { AutoSave(); Workbench.RefreshNodeStates(); }
        if (line is not null) Workbench.ReportStatus(line);
    }

    /// <summary>Flag the target edited after a send that would NOT read back. Which transition that is
    /// depends on the file: unparseable bytes are still Blender's own overwrite, so the donor record the
    /// previous mesh left is void; a file that won't open at all was never rewritten by this send — a replayed
    /// sidecar over a locked or vanished glb — and its record still describes what is on disk.</summary>
    private void MarkTargetEditedAfterFailedRead(string glbPath)
    {
        if (_project.RootDir is null) return;
        if (IsFileReadable(glbPath)) _project.MarkFileReplaced(glbPath);
        else _project.MarkFileEdited(glbPath);
    }

    /// <summary>Whether the file can be opened for reading right now. Missing, locked and unreadable all
    /// answer the same: nothing here can be read.</summary>
    private static bool IsFileReadable(string path)
    {
        try
        {
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---- Edit-page revert / delete (per-file) ------------------------------------------------------
    // Revert restores one file from its originals/ copy; Delete untracks a file (and, for a mesh, the whole
    // part) so an additive re-export won't bring it back.

    /// <summary>The app's main window, for parenting modal confirmations from a command.</summary>
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private string Rel(string absPath) =>
        Path.GetRelativePath(_project.RootDir!, absPath).Replace('\\', '/');

    private static string BridgeScriptPath() =>
        Path.Combine(AppContext.BaseDirectory, "blender", "remold_bridge.py");

    /// <summary>Re-read the game from scratch: drop the forward view and roster, then rerun
    /// <see cref="LoadAsync"/>. Waits on every hold in <see cref="RescanMustWait"/>. QUEUED rather than
    /// refused: a request that vanished reads as a dead button.</summary>
    [RelayCommand]
    private void ReloadRoster()
    {
        if (RescanMustWait)
        {
            _rescanAfterScan = true;
            NoticeStatus = StatusFacet.Warn(RescanQueuedNotice, RescanQueuedDetail);
            return;
        }
        GameRescanOffered = false;
        Characters.Clear();
        Enemies.Clear();
        _allCharacters = new();
        _allEnemies = new();
        _vfs = null;
        _subjectModels.Clear();   // a memoized model describes the forward view being dropped here
        _sharingCts?.Cancel();
        _sharingTask = null;
        SearchText = "";
        EnemySearchText = "";
        RefreshTabHeaders();   // the discarded trees held checked subjects — no stale "(N)" count
        IsLoading = true;
        IsScanning = true;
        _ = Task.Run(LoadAsync);   // off the UI thread; the read blocks
    }

    /// <summary>Set the game folder from a manual pick, accepting it only if it is a real GF2 install. A
    /// non-install folder is rejected with a notice and changes nothing.</summary>
    public void SetGameDir(string folder)
    {
        var (resolved, problem) = GameLocator.ValidateDetailed(folder);
        if (resolved is null)
        {
            // Leave the game facet reflecting the real install.
            NoticeStatus = StatusFacet.Bad("Not a GF2 install", problem ?? "The folder isn't a GF2 install.");
            return;
        }
        _settings.GamePath = resolved;
        SaveSettings();
        _gameDir = resolved;
        RaiseModsFolderGates();   // the game half of the Launch gate is known now, scan or no scan
        // An in-flight load captured its own game dir at start and won't pick this one up, so queue a
        // rescan for when it lands rather than silently no-op until the user rescans.
        if (IsScanning)
        {
            _rescanAfterScan = true;
            NoticeStatus = StatusFacet.Warn("Folder change pending",
                "New folder takes effect after this scan. Rescanning next.");
            return;
        }
        ReloadRoster();
    }

    /// <summary>A rescan became due while something was holding the roster — a game-folder change from
    /// <see cref="SetGameDir"/>, or the exit of a game this app launched. Consumed by
    /// <see cref="RunQueuedRescan"/> wherever one of those holds lets go.</summary>
    private bool _rescanAfterScan;

    /// <summary>Whether a rescan has to wait. <see cref="ReloadRoster"/> drops the VFS, cancels the
    /// sharing measurement and empties the trees, so anything reading them mid-flight — a load, a build,
    /// a materialize (speculative or asked for), an Open-all's rig build, a send-back apply — would fail on
    /// vanished state.</summary>
    private bool RescanMustWait => IsScanning || IsModBuilding || _materializing || _prewarming
        || _buildingCombinedRig || _applyingSend;

    /// <summary>The notice while a queued rescan waits on whatever is holding the roster. ONE line for every
    /// route that can queue one, so a wait the modder can't see the cause of always reads the same.</summary>
    internal const string RescanQueuedNotice = "Rescan queued";
    /// <inheritdoc cref="RescanQueuedNotice"/>
    internal const string RescanQueuedDetail = "Files re-read when the current work finishes.";

    /// <summary>Run a queued rescan once the roster's holds let go. Called at every load exit AFTER
    /// IsScanning clears and at the end of each build/materialize scope; it re-tests the hold rather than
    /// consuming the queue, so a drain under another standing hold leaves the rescan for that one.</summary>
    private void RunQueuedRescan()
    {
        if (!_rescanAfterScan || RescanMustWait) return;
        _rescanAfterScan = false;
        ReloadRoster();
    }

    // ---- Settings (Tools → Settings…) -------------------------------------------------------------

    // The read-only default the Settings dialog shows — the same one ResolvedLibraryRoot falls back to.
    private static string DefaultLibraryRoot => LabPaths.DefaultLibraryRoot;

    /// <summary>Snapshot the settings for the Settings dialog, including the read-only auto-detect
    /// displays.</summary>
    public SettingsInput BuildSettingsInput() => new()
    {
        GamePath = string.IsNullOrEmpty(_gameDir) ? _settings.GamePath : _gameDir,
        BlenderPath = _settings.PreferredBlender,
        BlenderAuto = BlenderLocator.Find(),                 // auto-detected (no override), for display
        ImageEditorPath = _settings.PreferredImageEditor,
        DetectedEditors = ImageEditorLocator.Detect(),
        LibraryRoot = _settings.LibraryRoot,
        DefaultLibrary = DefaultLibraryRoot,
        MigotoLoaderExe = _settings.MigotoLoaderExe,
        Author = _settings.Author,
        RecentCount = _settings.RecentMods.Count,
        EncoderCpuLimit = _settings.EncoderCpuLimit,
    };

    /// <summary>The support facts for the About dialog — only what the app can truthfully report.</summary>
    public Views.AboutInfo BuildAboutInfo() => new(
        AppVersion: System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
        ProjectSchema: ModProject.CurrentSchema,
        SettingsPath: LabSettings.DefaultPath,
        LibraryRoot: _settings.ResolvedLibraryRoot,
        GamePath: string.IsNullOrEmpty(_gameDir) ? _settings.GamePath : _gameDir);

    /// <summary>Apply the edited settings; a null means "fall back" (auto-detect / OS default / default
    /// library). Author applies to <i>new</i> mods only — the open project keeps its own.</summary>
    public void ApplySettings(SettingsResult r)
    {
        _settings.Author = r.Author;
        var newBlender = Empty2Null(r.BlenderPath);
        bool blenderChanged = !string.Equals(newBlender, _settings.PreferredBlender, StringComparison.OrdinalIgnoreCase);
        _settings.PreferredBlender = newBlender;
        _settings.PreferredImageEditor = Empty2Null(r.ImageEditorPath);
        _settings.LibraryRoot = Empty2Null(r.LibraryRoot);
        _settings.MigotoLoaderExe = Empty2Null(r.MigotoLoaderExe);
        _settings.EncoderCpuLimit = r.EncoderCpuLimit;
        RaiseModsFolderGates();

        var newGame = Empty2Null(r.GamePath);
        bool gameChanged = !string.Equals(newGame, _settings.GamePath, StringComparison.OrdinalIgnoreCase);
        _settings.GamePath = newGame;

        if (r.ClearRecents) { _settings.RecentMods.Clear(); RefreshRecent(); }
        SaveSettings();

        if (blenderChanged) RefreshBlenderStatus();
        if (gameChanged && newGame is { } g)
        {
            _gameDir = g;
            RaiseModsFolderGates();
            if (!IsScanning) ReloadRoster();
        }
    }

    /// <summary>Recompute the status-bar Blender line. Presence-only (no process spawn), so it runs
    /// inline.</summary>
    private void RefreshBlenderStatus()
    {
        var exe = BlenderLocator.Find(_settings.PreferredBlender);
        BlenderPath = exe ?? "";
        BlenderStatus = exe is null ? StatusFacet.Bad("Blender · not found", "Set the Blender path in Settings.") : StatusFacet.Good("Blender");
        Workbench.BlenderFound = exe is not null;   // the Edit pane's Open buttons gate on the same answer
    }

    private static string? Empty2Null(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (Directory.Exists(ExportOutDir))
            Process.Start(new ProcessStartInfo { FileName = ExportOutDir, UseShellExecute = true });
        else
            BuildStatus = "Mod folder is gone. Save the mod to recreate it.";
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnEnemySearchTextChanged(string value) => ApplyFilter();

    // Opt-in launch timing (GF2_LAUNCH_TIMING=1): each LoadAsync phase's wall time to a log file. The app
    // is a WinExe — no console, and Debug trace is invisible without a debugger.
    private static readonly bool TimeLaunch =
        Environment.GetEnvironmentVariable("GF2_LAUNCH_TIMING") is "1" or "true";
    private static readonly string LaunchTimingLog = LabPaths.LaunchTimingLog;
    private static void PhaseTime(Stopwatch sw, string label)
    {
        if (TimeLaunch)
        {
            var line = $"[launch] {sw.ElapsedMilliseconds,7:N0} ms  {label}";
            Debug.WriteLine(line);
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LaunchTimingLog)!);
                System.IO.File.AppendAllText(LaunchTimingLog, line + Environment.NewLine);
            }
            catch { /* diagnostic-only — never disturb launch */ }
        }
        sw.Restart();
    }

    private async Task LoadAsync()
    {
        var _lt = Stopwatch.StartNew();
        // Fresh log per launch.
        if (TimeLaunch) try { if (System.IO.File.Exists(LaunchTimingLog)) System.IO.File.Delete(LaunchTimingLog); } catch { }
        try
        {
            // Off the UI thread (the whole method runs under Task.Run) — the registry/library scan belongs
            // there.
            _gameDir = ResolveGameDir();
            if (_gameDir.Length == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsLoading = false;
                    IsScanning = false;
                    GameStatus = StatusFacet.Bad("Game · not found");
                    StatusChars = "";
                    NoticeStatus = StatusFacet.Warn("Locate the game",
                        "Pick the folder that contains the game's .exe (Tools · Locate game).");
                    ShowSettingsSaveNotice();   // this path never reaches the finalize aggregation
                    Workbench.NotifyGameFailed();   // the Edit pane can't build a tree — show the static unavailable state
                    RaiseModsFolderGates();
                    RunQueuedRescan();
                });
                return;
            }

            // PHASE 1 — the character name list straight from the DB (~13ms), so the roster appears while
            // the forward view loads. The DB is the roster's enumeration source, so a failed read is a real
            // load failure and propagates to the catch rather than silently emptying the Pick tree.
            PhaseTime(_lt, "ResolveGameDir");
            var nameDb = GameDatabase.FromGameDir(GameDir);
            // Localized display names for the Pick tree. Best-effort: a missing locale table leaves every
            // label on its token/stem. Loaded once here (~394k-row map) and reused for the enemy roster.
            LocalizationDb? loc = null;
            try { loc = LocalizationDb.Load(nameDb.TableRoot); }   // English (Enus) is fixed for v1
            catch { /* locale table unreadable — labels fall back to tokens */ }
            DisplayNames? names = null;
            if (loc is not null)
                try { names = DisplayNames.Build(nameDb, loc); }
                catch { /* labels fall back to tokens */ }
            PhaseTime(_lt, "Phase 1: DB + localization + DisplayNames");
            var dbRoster = nameDb.ReadRoster();
            if (names is not null) dbRoster = names.Enrich(dbRoster);
            // Curated skins the design DB can't enumerate (no ModelConfigData row names them). Folded in
            // AFTER enrichment: their labels are curated strings, not localization lookups. A curated
            // character the DB already names merges into that row rather than listing a second one.
            dbRoster = CuratedSkins.MergeInto(dbRoster);
            var vms = dbRoster.Select(c => new CharacterVm(c, OnSubjectToggled, OnCharacterToggled)).ToList();
            for (int i = 0; i < dbRoster.Count; i++)
                vms[i].Populate(dbRoster[i].Outfits.Select(o => (o, (IEnumerable<string>)Array.Empty<string>())), lightUp: false);

            // The Enemies tab roster. Best-effort like localization: an unreadable EnemyData table empties
            // the tab with a status note, never fails the load. Stems the playable roster already shows are
            // excluded, so a summon the enemy tables also reference can't appear in both tabs.
            List<Character> enemyRoster = new();
            string? enemyRosterError = null;
            try
            {
                var playableStems = dbRoster.SelectMany(c => c.Outfits).Select(o => o.Stem)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                enemyRoster = nameDb.ReadEnemyRoster(loc, playableStems);
            }
            catch (Exception e) { enemyRosterError = e.Message; }
            var enemyVms = enemyRoster.Select(c => new CharacterVm(c, OnSubjectToggled, OnCharacterToggled)).ToList();
            for (int i = 0; i < enemyRoster.Count; i++)
                enemyVms[i].Populate(enemyRoster[i].Outfits.Select(o => (o, (IEnumerable<string>)Array.Empty<string>())), lightUp: false);
            PhaseTime(_lt, "Phase 1: DB roster + enemy roster + VM construction");

            // Presence-only (no process spawn), so it stays on the launch path without blocking it.
            var blenderExe = BlenderLocator.Find(_settings.PreferredBlender);
            PhaseTime(_lt, "Blender detect");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allCharacters = vms;
                _allEnemies = enemyVms;
                _roster = dbRoster.Concat(enemyRoster).ToList();   // full roster until finalize narrows it
                // Built from the full roster so a mod OPENED DURING the load already reads friendly.
                RebuildFriendlyNames(dbRoster.Concat(enemyRoster).ToList());
                GameStatus = StatusFacet.Good("Game");
                StatusChars = "Reading game files…";
                NoticeStatus = StatusFacet.None;   // clear any notice from a prior failed load
                BlenderPath = blenderExe ?? "";
                BlenderStatus = blenderExe is null ? StatusFacet.Bad("Blender · not found", "Set the Blender path in Settings.") : StatusFacet.Good("Blender");
                Workbench.BlenderFound = blenderExe is not null;   // the Edit pane's Open buttons gate on the same answer
                IsLoading = false;     // the tree is interactive now
                IsScanning = true;
                RaiseModsFolderGates();     // the install resolved — the header's Launch can come alive
                ApplyFilter();
            });
            PhaseTime(_lt, "Phase 1: first UI marshal (tree interactive)");

            // PHASE 2 — the forward view (catalog + GFF manifest); the manifest read is the heavy part
            // (~1-2s). Null = no catalog/manifest at all; a corrupt one THROWS into the same state.
            var vfs = GameVfs.TryLoad(GameDir);
            if (vfs is null)
            {
                // A running game holds its files open, so separate "the game is up" from a genuine
                // mid-update before blaming the install. Off the UI thread.
                bool inUse = GameLocator.GameFilesInUse(GameDir);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsScanning = false;
                    // Only the running cell carries a detail: its notice is about THIS load, the tooltip
                    // is what the open files cost everything else.
                    GameStatus = StatusFacet.Warn(inUse ? GameRunningLabel : "Game · unreadable",
                        inUse ? GameRunningDetail : "");
                    StatusChars = "";
                    GameRescanOffered = true;
                    NoticeStatus = inUse
                        ? StatusFacet.Warn("The game is running",
                            "Can't read the game's files while it's open. Close the game, then Rescan.")
                        : StatusFacet.Warn("Game files unreadable",
                            "No catalog or manifest (the install may be mid-update). Rescan to retry.");
                    Workbench.NotifyGameFailed();
                    // The install is located either way, and the gates' disk reads may never have been
                    // taken — without this they render their unset defaults over a folder that exists.
                    RaiseModsFolderGates();
                    RunQueuedRescan();
                });
                return;
            }
            _vfs = vfs;
            PhaseTime(_lt, "Phase 2: GameVfs.TryLoad (catalog + manifest)");
            // Every physical file the manifest addresses must exist on disk; a non-empty result means the
            // install is mid-update, and is carried into the final status as a warning.
            var missing = vfs.MissingPhysicalFiles();
            PhaseTime(_lt, "Phase 2: missing-file check");

            // The install's existing sharing data, joined HERE and not earlier: the file carries no
            // names, only keys onto the roster's own. The measurement decides which enemy doors are
            // duplicates, so the roster needs it before it paints.
            var population = SharingPopulation.Of(dbRoster, enemyRoster);
            var sharingBase = LoadSharingBase(LabPaths.SharingIndexFile(vfs.CatalogVersion),
                LabPaths.SharingSeedFile, vfs.CatalogVersion, population);
            PhaseTime(_lt, "Phase 2: sharing base load");

            // PHASE 3, existence — a candidate iff the prefab-address formula resolves its stem in some
            // context (catalog dictionary hits, no file reads). Characters and enemies ride ONE candidate
            // list; IsEnemy only routes the row back to its own tab at the marshals.
            var candidates = new List<(CharacterVm Vm, Character Character, List<Outfit> Outfits, bool IsEnemy)>();
            for (int i = 0; i < dbRoster.Count; i++)
            {
                // by OUTFIT, not stem: a curated subject's prefab is found through its own route
                var outfits = dbRoster[i].Outfits.Where(o => vfs.PrefabsFor(o).Count > 0).ToList();
                if (outfits.Count > 0) candidates.Add((vms[i], dbRoster[i], outfits, false));
            }
            for (int i = 0; i < enemyRoster.Count; i++)
            {
                var outfits = enemyRoster[i].Outfits.Where(o => vfs.PrefabsFor(o).Count > 0).ToList();
                if (outfits.Count > 0) candidates.Add((enemyVms[i], enemyRoster[i], outfits, true));
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allCharacters = candidates.Where(c => !c.IsEnemy).Select(c => c.Vm).ToList();
                _allEnemies = candidates.Where(c => c.IsEnemy).Select(c => c.Vm).ToList();
                ApplyFilter();
                StatusChars = $"Reading models… 0/{candidates.Count}";
                // The forward view is up, so the workbench no longer waits on the roster fill.
                Workbench.NotifyGameReady();
            });
            PhaseTime(_lt, "Phase 3: candidate existence + fill-start marshal");

            // PHASE 3, confirm — snapshot first. The fill's result is a pure derivation of the catalog's
            // vanilla structure, so a snapshot keyed on catalog VERSION replaces the reads outright; only a
            // game update re-runs the fill.
            var catalog = vfs.Catalog;
            var fillErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var confirmedByVm = new System.Collections.Concurrent.ConcurrentDictionary<CharacterVm, List<(Outfit Outfit, IReadOnlyList<string> Parts)>>();
            // The Enemies TAB drops a duplicate door; nothing else does. See SplitDuplicateDoors.
            IReadOnlyList<(Outfit Outfit, IReadOnlyList<string> Parts)> Listed(
                Character character, bool isEnemy,
                IReadOnlyList<(Outfit Outfit, IReadOnlyList<string> Parts)> confirmed) =>
                SplitDuplicateDoors(sharingBase.Index, character.Name, isEnemy, confirmed, x => x.Outfit.Stem)
                    .Listed;

            var snapshotPath = LabPaths.RosterSnapshotFile(vfs.CatalogVersion);
            var snapshot = RosterSnapshot.TryLoad(snapshotPath, vfs.CatalogVersion);
            if (snapshot is not null)
            {
                foreach (var cand in candidates)
                {
                    var confirmed = new List<(Outfit Outfit, IReadOnlyList<string> Parts)>();
                    foreach (var outfit in cand.Outfits)
                        if (snapshot.TryGetValue(outfit.ModelConfigId, out var parts))
                            confirmed.Add((outfit, parts));
                    confirmedByVm[cand.Vm] = confirmed;
                }
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var cand in candidates)
                        if (Listed(cand.Character, cand.IsEnemy, confirmedByVm[cand.Vm]) is { Count: > 0 } listed)
                            cand.Vm.Populate(listed.Select(x => (x.Outfit, (IEnumerable<string>)x.Parts)));
                });
                PhaseTime(_lt, "Phase 3: roster snapshot hit (no reads)");
            }
            else
            {
                // The fill — parallel across characters (bounded). Each outfit gets its OWN deobfuscate
                // memo so one outfit's bundles don't stay pinned past its build. A read that throws
                // mid-fill drops that outfit LOUDLY, collected and surfaced in the final status.
                int confirmedChars = 0;
                Parallel.ForEach(candidates,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, _settings.EncoderCpuLimit ?? Environment.ProcessorCount),
                    },
                    cand =>
                    {
                        var confirmed = new List<(Outfit Outfit, IReadOnlyList<string> Parts)>();
                        foreach (var outfit in cand.Outfits)
                        {
                            try
                            {
                                var memo = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
                                byte[]? Deobfuscate(string logical) =>
                                    memo.TryGetValue(logical, out var hit) ? hit : memo[logical] = vfs.TryDeobfuscateLogical(logical);
                                var scope = SubjectScope.Build(catalog, Deobfuscate, outfit);
                                var prefabs = scope.Candidates;
                                // Confirms iff a candidate carries recipe rows (character/RX shape) OR
                                // mesh-bearing renderer slots, skinned (the enemy smr-body shape) or static
                                // (the prop shape). Neither = UNCONFIRMED: it never lights up and is
                                // removed at finalize.
                                if (!prefabs.Any(c => c.Prefab.Recipe.Count > 0 || c.Prefab.Slots.Any(s => s.HasMesh)))
                                    continue;
                                confirmed.Add((outfit, SubjectModelBuilder.OwnedSlotTokens(prefabs, outfit)));
                            }
                            catch (Exception e) { fillErrors.Enqueue($"{outfit.Stem}: {e.Message}"); }
                        }
                        confirmedByVm[cand.Vm] = confirmed;
                        var listed = Listed(cand.Character, cand.IsEnemy, confirmed);
                        if (listed.Count == 0) return;
                        // one post per character: confirmed outfits swap in resolved and the row lights up
                        Dispatcher.UIThread.Post(() =>
                        {
                            cand.Vm.Populate(listed.Select(x => (x.Outfit, (IEnumerable<string>)x.Parts)));
                            StatusChars = $"Reading models… {++confirmedChars}/{candidates.Count}";
                        });
                    });
                // Persist only a CLEAN fill: a partial result must not become this game state's truth.
                if (fillErrors.IsEmpty)
                    try
                    {
                        // Keyed by ModelConfigId, not stem: two ModelConfig rows can name one stem, and a
                        // stem-keyed entry would ride an unconfirmed row back into Pick next launch.
                        var byId = new Dictionary<long, List<string>>();
                        foreach (var confirmed in confirmedByVm.Values)
                            foreach (var (outfit, parts) in confirmed)
                                byId[outfit.ModelConfigId] = new List<string>(parts);
                        RosterSnapshot.Save(snapshotPath, vfs.CatalogVersion, byId);
                    }
                    catch { /* cache-only; next launch just refills */ }
                PhaseTime(_lt, "Phase 3: outfit confirm fill");
            }

            // PHASE 3, finalize — drop what never confirmed. The roster narrows the same way, so workbench
            // stem resolution matches what Pick shows.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var surviving = new List<CharacterVm>();
                var survivingEnemies = new List<CharacterVm>();
                var confirmedRoster = new List<Character>();
                foreach (var cand in candidates)
                {
                    if (!confirmedByVm.TryGetValue(cand.Vm, out var confirmed) || confirmed.Count == 0) continue;
                    // The roster keeps every confirmed outfit, filtered doors included — it is what resolves
                    // a picked subject — while the tab lists only what survives the filter.
                    confirmedRoster.Add(cand.Character with { Outfits = confirmed.Select(x => x.Outfit).ToList() });
                    if (Listed(cand.Character, cand.IsEnemy, confirmed).Count == 0) continue;
                    (cand.IsEnemy ? survivingEnemies : surviving).Add(cand.Vm);
                }
                _allCharacters = surviving;
                _allEnemies = survivingEnemies;
                _roster = confirmedRoster;

                // Rebuilt from the same phase-1 roster, so the resolver covers every subject the fill saw
                // and not just the ones that survived it.
                RebuildFriendlyNames(dbRoster.Concat(enemyRoster).ToList());

                StatusChars = $"Characters: {surviving.Count} · Enemies: {survivingEnemies.Count} · catalog v{vfs.CatalogVersion}";
                // Warnings ride the notice cell, not the roster line — full detail in its tooltip.
                var notices = new List<(string Short, string Detail)>();
                if (_settings.LoadedFromDefaultsAfterError)
                    notices.Add(("settings reset",
                        "Your settings file couldn't be read and was ignored; defaults are in use until you save."));
                // Carries a save failure from the off-UI-thread detection write, which has no cell to
                // reach; this assignment replaces the cell, so the merge form would be lost here anyway.
                if (_settingsSaveFailed)
                {
                    notices.Add((SettingsSaveFailedShort, SettingsSaveFailedDetail));
                    _settingsSaveNoticeShown = true;
                }
                if (enemyRosterError is not null)
                    notices.Add(("enemy roster unreadable",
                        $"The enemy roster couldn't be read: {enemyRosterError}"));
                if (missing.Count > 0)
                    notices.Add(($"{missing.Count} file(s) missing",
                        $"{missing.Count} game file(s) missing; the install looks mid-update. Verify the install."));
                if (!fillErrors.IsEmpty)
                {
                    fillErrors.TryPeek(out var first);
                    notices.Add(($"{fillErrors.Count} outfit(s) unreadable",
                        $"{fillErrors.Count} outfit(s) couldn't be read ({first}). Retry with Tools · Rescan game files."));
                }
                // The stale-version warning goes INTO this notices list rather than overwriting the cell
                // afterward: an overwrite silently loses whatever warning was already there.
                if (TakeAuthoredAgainstNotice() is { } stale) notices.Add(stale);
                NoticeStatus = notices.Count == 0
                    ? StatusFacet.None
                    : StatusFacet.Warn(
                        notices.Count == 1 ? notices[0].Short : $"{notices.Count} warnings",
                        string.Join("\n", notices.Select(n => "• " + n.Detail)));
                IsScanning = false;
                ApplyFilter();
                // Re-assert the ledger's checkboxes after ANY load. No MarkDirty — a load changes no mod
                // state.
                SyncSubjectsFromLedger();
                _pendingSelection = null;
                RunQueuedRescan();
            });
            PhaseTime(_lt, "Phase 3: finalize marshal");

            // The sharing pass, after the launch's own reads. Data measured under this catalog is the
            // answer outright; anything else repairs in the background over the whole modding roster —
            // enemies and props wear shared textures too. Builds await the task; nothing else does, so a
            // failure has no surface beyond the build saying "unscoped".
            StartSharingIndexJob(vfs, population, sharingBase);
        }
        catch (Exception e)
        {
            // A running game holds its files open, so distinguish that from a corrupt install and the
            // remedy stays "close the game". Probed off the UI thread, before the marshal.
            bool inUse = GameLocator.GameFilesInUse(GameDir);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoading = false;
                IsScanning = false;
                // Location ALREADY succeeded above, so this is a read failure and NOT a missing install:
                // the remedy is Rescan, never Locate. "not found" is reserved for ResolveGameDir.
                GameStatus = StatusFacet.Warn(inUse ? GameRunningLabel : "Game · unreadable",
                    inUse ? GameRunningDetail : "");
                StatusChars = "";
                GameRescanOffered = true;
                NoticeStatus = inUse
                    ? StatusFacet.Warn("The game is running",
                        "Can't read the game's files while it's open. Close the game, then Rescan.")
                    : StatusFacet.Warn("Game data unreadable",
                        $"Couldn't read the game data: {e.Message}. Rescan to retry; if it persists the install may be corrupt or an unsupported version.");
                Workbench.NotifyGameFailed();
                // Location ALREADY succeeded, so both gates' halves are knowable; a phase-1 failure would
                // otherwise leave them on the defaults they booted with.
                RaiseModsFolderGates();
                RunQueuedRescan();
            });
        }
    }

    private void ApplyFilter()
    {
        // Order by the label the modder READS, not the internal roster name — the backing lists are
        // internal-name-ordered, which would file "Mirel" under V (Vesna). Sorted here so the backing
        // lists keep their own order for keyed lookups.
        var q = SearchText?.Trim() ?? "";
        Characters.Clear();
        foreach (var c in _allCharacters.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
            if (q.Length == 0 || MatchesCharacter(c, q))
                Characters.Add(c);

        var eq = EnemySearchText?.Trim() ?? "";
        Enemies.Clear();
        foreach (var c in _allEnemies.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
            if (eq.Length == 0 || MatchesCharacter(c, eq))
                Enemies.Add(c);

        RefreshEmptyStates();
    }

    /// <summary>Filter match: the query hits the localized label OR the internal roster name, so "Mirel"
    /// and "Vesna" both find her.</summary>
    private static bool MatchesCharacter(CharacterVm c, string q) =>
        c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        c.Name.Contains(q, StringComparison.OrdinalIgnoreCase);

    /// <summary>One character's confirmed outfits, split into what the app can RESOLVE and what the
    /// Enemies tab LISTS. The two differ by one thing: an enemy duplicate door — a row whose mesh set is
    /// precisely a playable outfit's — is left out of the TAB, and of nothing else. A door picked before
    /// any measurement said it was one is a subject in someone's mod: it must keep resolving, building
    /// and listing, or the next launch refuses it blaming the game install. With no measurement the two
    /// answers are the same list.</summary>
    internal static (IReadOnlyList<T> Resolvable, IReadOnlyList<T> Listed) SplitDuplicateDoors<T>(
        SharingIndex? sharing, string character, bool isEnemy, IReadOnlyList<T> confirmed,
        Func<T, string> stemOf) =>
        !isEnemy || sharing is null
            ? (confirmed, confirmed)
            : (confirmed, confirmed.Where(x => !sharing.IsDuplicateDoor(character, stemOf(x))).ToList());

    // ---- the Pick tabs' empty states ---------------------------------------------------------------

    /// <summary>What a search that matched nothing leaves in the empty list, or "" when there is nothing to
    /// say. A list empty because the roster has no rows at all is not a dead-ended search — the tab's own
    /// loading and empty states own that.</summary>
    internal static string NoMatchLine(string? query, int shown)
    {
        var q = query?.Trim() ?? "";
        return shown == 0 && q.Length > 0 ? $"No match for \"{q}\"." : "";
    }

    /// <summary>Why an enemy the modder searched for may not be listed under its own name. The tab's
    /// duplicate-door filter is otherwise invisible, and a stated fact beats a hole.</summary>
    internal const string EnemyDoorNote =
        "Enemies that reuse a character's meshes are listed under that character.";

    public string CharactersNoMatch => NoMatchLine(SearchText, Characters.Count);
    public string EnemiesNoMatch => NoMatchLine(EnemySearchText, Enemies.Count);
    public bool HasCharactersNoMatch => CharactersNoMatch.Length > 0;
    public bool HasEnemiesNoMatch => EnemiesNoMatch.Length > 0;
    /// <summary><see cref="EnemyDoorNote"/> for the view, so the sentence has one home.</summary>
    public string EnemiesNoMatchNote => EnemyDoorNote;

    private void RefreshEmptyStates()
    {
        OnPropertyChanged(nameof(CharactersNoMatch));
        OnPropertyChanged(nameof(EnemiesNoMatch));
        OnPropertyChanged(nameof(HasCharactersNoMatch));
        OnPropertyChanged(nameof(HasEnemiesNoMatch));
    }

    /// <summary>The measurement data already on this machine for the loaded catalog: the cache first,
    /// then the shipped seed. Fast (a file read and a name join, no bundle reads), so the load path takes
    /// it inline. Its <see cref="SharingIndex.CatalogVersion"/> is what the data was MEASURED under —
    /// possibly older than the running game's, which makes it a delta base rather than an answer.</summary>
    internal static SharingBase LoadSharingBase(string cachePath, string seedPath, string catalogVersion,
        SharingPopulation population)
    {
        if (catalogVersion == GameInfo.UnknownVersion) return default;   // nothing pins a cache or a measurement
        if (SharingIndex.TryLoad(cachePath, population) is { } cached
            && cached.CatalogVersion == catalogVersion)
            return new SharingBase(cached, FromSeed: false);
        // A seed that joined to nothing is no base at all — it would read as "everything measured, nothing
        // shared" and every edit would ship as private.
        var seed = SharingIndex.TryLoad(seedPath, population);
        return seed is { MeasuredOutfitCount: > 0 } ? new SharingBase(seed, FromSeed: true) : default;
    }

    /// <summary>The measurement data a load starts from, and whether it came from the shipped seed rather
    /// than this install's own cache — which is what decides whether the cache still has to be written.</summary>
    internal readonly record struct SharingBase(SharingIndex? Index, bool FromSeed);

    /// <summary>Start (or restart) the background sharing pass over the FULL modding roster — enemies
    /// wear cross-subject textures (skin blends, ramp LUTs) like outfits do.
    ///
    /// <para><b>Every launch runs the pass</b>, whatever <paramref name="basis"/> was measured under; the
    /// pass decides from the catalog alone what to read, so a launch over current data opens no bundle
    /// and shows no line. That buys what an adopt-outright shortcut could not: a curated subject added
    /// since the basis gets measured, a FAILED outfit is retried, and the delta path is exercised every
    /// launch instead of first running unattended at the next game update.</para></summary>
    private void StartSharingIndexJob(GameVfs vfs, SharingPopulation population, SharingBase basis)
    {
        _sharingCts?.Cancel();
        var cts = _sharingCts = new CancellationTokenSource();
        string cv = vfs.CatalogVersion;
        if (cv == GameInfo.UnknownVersion) { _sharingTask = Task.FromResult<SharingIndex?>(null); return; }
        string path = LabPaths.SharingIndexFile(cv);
        var progress = new InOrderProgress<SharingProgress>(p => ReportSharingProgress(cts, p));
        _sharingTask = Task.Run<SharingIndex?>(() =>
        {
            SetSharingFailed(cts, false);
            try
            {
                var built = SharingIndex.Build(population, vfs.Catalog, TryDeobfuscateBundle, cv,
                    basis.Index, progress, cts.Token);
                if (ShouldWriteSharingCache(built, basis))
                    try { built.Save(path); } catch { /* cache write is best-effort; next launch remeasures */ }
                return built;
            }
            catch (OperationCanceledException) { throw; }   // superseded, not failed — the cell says nothing
            catch (Exception) { SetSharingFailed(cts, true); throw; }
            finally { ReportSharingProgress(cts, null); }
        }, cts.Token);
    }

    /// <summary>Whether a completed pass's result is written to this install's cache, as a pure rule.
    /// Many failed outfits is a transient condition (typically the game holding its bundles), not a fact
    /// about the catalog — caching it would serve those outfits as uncovered until the next game update;
    /// a handful is the real per-catalog floor and caches. A result identical to the install's OWN cache
    /// is not written; a basis adopted from the shipped seed always is — that mints the cache.</summary>
    internal static bool ShouldWriteSharingCache(SharingIndex built, SharingBase basis)
    {
        int totalOutfits = built.MeasuredOutfitCount + built.FailedOutfits.Count;
        if (built.FailedOutfits.Count > Math.Max(3, totalOutfits / 20)) return false;
        return basis.FromSeed || basis.Index is not { } cached || !built.SameRowsAs(cached);
    }

    /// <summary>Non-throwing LOGICAL-bundle deobfuscate; null when the bundle is absent/unreadable or
    /// there is no game.</summary>
    private byte[]? TryDeobfuscateBundle(string logical)
    {
        if (_vfs is null) return null;
        try { return _vfs.TryDeobfuscateLogical(logical); }
        catch { return null; }
    }

    // ---- IWorkbenchShell: the imperative plumbing the workbench verbs reuse -------------------------
    // The verbs home on WorkbenchVm; this side owns the watchers, settings, and project mutation, and
    // notifies the workbench after each one so ✎ and cards refresh.

    /// <summary>Clipboard copy for the workbench's copy verb.</summary>
    public Task CopyTextAsync(string? text) => CopyText(text);

    /// <summary>Advance to the Build step.</summary>
    public void GoToBuild() => SelectedStep = "③ Build";

    /// <summary>Persist a non-materializing workbench mutation (the Hide toggle).</summary>
    public void AutoSaveProject() { MarkDirty(); AutoSave(); }

    /// <summary>Autosave, handing back the failure text (null on success) for a caller with its own
    /// surface.</summary>
    private string? TryAutoSaveProject() { MarkDirty(); return AutoSave(); }

    /// <summary>Name an unnamed project from the FIRST subject it takes. Must run BEFORE the folder is
    /// minted so the slug matches; a user-named project is NEVER overwritten.</summary>
    private void AutoNameFromSubject(Workbench.WorkbenchSubjectRef subject) => AutoNameFromSubject(subject.Character);

    /// <inheritdoc cref="AutoNameFromSubject(Workbench.WorkbenchSubjectRef)"/>
    private void AutoNameFromSubject(string subjectCharacter)
    {
        if (!string.IsNullOrWhiteSpace(PackageName)) return;   // user- or already-named — never overwrite
        if (_project.RootDir is not null) return;              // folder already minted — the naming window passed
        var character = _friendly.Character(subjectCharacter);
        if (string.IsNullOrWhiteSpace(character)) character = subjectCharacter;
        if (string.IsNullOrWhiteSpace(character)) return;
        PackageName = AutoModName(character);
    }

    /// <summary>The auto mod name for a subject's character. Static so a test can pin the naming rule
    /// without constructing the VM.</summary>
    public static string AutoModName(string friendlyCharacter) => $"{friendlyCharacter} mod";

    /// <summary>Ensure the mod folder exists for a first-touch materialize; the folder is minted lazily,
    /// not at New Mod. False if it couldn't be created.</summary>
    private bool EnsureModRoot()
    {
        if (_project.RootDir is not null)
        {
            // The Build step can set RootDir without _modRoot, and a first materialize would then NRE on
            // _modRoot!. Resync off the persisted RootDir before the early return.
            if (!string.Equals(_modRoot, _project.RootDir, StringComparison.Ordinal))
            {
                _modRoot = _project.RootDir; ExportOutDir = _project.RootDir;
                EnsureWatcher();
            }
            return true;
        }
        // INVARIANT: a project's root is established ONCE and stays stable. _modRoot set with RootDir null
        // is a divergence bug — restore the established root LOUDLY rather than silently minting a SECOND
        // folder, which strands files.
        if (_modRoot is not null)
        {
            _project.RootDir = _modRoot; ExportOutDir = _modRoot;
            EnsureWatcher();
            BuildStatus = $"Recovered the mod folder ({Path.GetFileName(_modRoot)}) after it was lost mid-run.";
            return true;
        }
        try
        {
            var modRoot = UniqueDir(_settings.ResolvedLibraryRoot, ModNaming.Slug(ProjectName));
            Directory.CreateDirectory(modRoot);
            _project.RootDir = modRoot; ExportOutDir = modRoot; _modRoot = modRoot;
            EnsureWatcher();
            return true;
        }
        catch { return false; }
    }

    // A workbench materialize in flight. WorkbenchVm serializes verbs, so this is a single scope, not a
    // ref-count. While alive: the mod folder must not be renamed (a rename would race the background read's
    // captured modRoot and strand files); _busySubject makes a remove refuse the subject being prepared,
    // whose later commit would resurrect it; and a part/batch run drops both watchers for the write.
    // UI-thread only.
    //
    // _materializing is the EXPLICIT half — the modder is waiting on this. _prewarming is the speculative
    // half, and the two are kept apart because almost nothing may treat a guess as work in progress: it
    // costs the modder no wait, blocks no close, makes no subject busy, and keeps the watchers up. The
    // folder rename is the one rule both hold, since it turns on who is writing into the folder rather
    // than who asked.
    private bool _materializing;
    private bool _prewarming;
    /// <summary>An asked-for Open-all is building the union-armature rig. That phase sits between materialize
    /// scopes rather than inside one, so it carries its own flag — the close guard reads it. UI-thread only,
    /// like the two above.</summary>
    private bool _buildingCombinedRig;
    private (string Character, string Stem)? _busySubject;

    // Every materialize not already carrying the workbench build token runs under this one, so
    // ResetWorkspace can cancel an in-flight prepare. The project-identity guard at the commit is the real
    // guarantee; this only stops the abandoned prepare's wasted read/encode early.
    private CancellationTokenSource _materializeCts = new();

    /// <param name="background">A speculative run (the outfit prewarm). Leaves BOTH watchers armed — a
    /// Send or image-editor save landing while they are down is lost — announcing its own writes to the
    /// texture watcher instead, and leaves the subject un-busy.</param>
    internal MaterializeScope BeginMaterialize(Workbench.WorkbenchSubjectRef subject, bool disarmWatchers,
        bool background = false)
    {
        if (background) _prewarming = true; else _materializing = true;
        if (!background) _busySubject = (subject.Character, subject.Stem);
        bool disarm = MaterializeDisarmsWatchers(disarmWatchers, background);
        if (disarm) DisarmWatchers();
        return new MaterializeScope(this, disarm, background);
    }

    /// <summary>Whether a materialize drops the watchers for its own writes. A speculative run never
    /// does: it suppresses the paths it is about to write, so the modder's own saves keep landing.</summary>
    internal static bool MaterializeDisarmsWatchers(bool disarmWatchers, bool background) =>
        disarmWatchers && !background;

    /// <summary>Whether a materialize for this subject is in flight. Case-insensitive, matching the
    /// ledger's subject comparisons.</summary>
    private bool IsSubjectBusy(string character, string stem) =>
        _busySubject is { } b
        && string.Equals(b.Character, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(b.Stem, stem, StringComparison.OrdinalIgnoreCase);

    internal sealed class MaterializeScope : IDisposable
    {
        private readonly MainWindowViewModel _vm;
        private readonly bool _rearm;
        private readonly bool _background;
        public MaterializeScope(MainWindowViewModel vm, bool rearm, bool background)
        { _vm = vm; _rearm = rearm; _background = background; }
        public void Dispose()
        {
            if (_background) _vm._prewarming = false;
            else { _vm._materializing = false; _vm._busySubject = null; }
            if (_rearm) _vm.EnsureWatcher();
            _vm.RunQueuedRescan();   // this scope was one of the holds a rescan waits behind
        }
    }

    // ---- outfit prewarm ----------------------------------------------------------------------------
    // Opening ANY single part carries the whole outfit on its union armature, so the first open pays for
    // materializing and combining every part. The queue moves that cost off the click: the first Edit
    // visit starts the outfit preparing in the background, one at a time; an explicit action takes the
    // queue away rather than lining up behind it. The fingerprint cache is where the routes meet — a
    // finished prewarm is a cache hit for the open, and a failed/cancelled/never-run one leaves the open
    // to do the whole job. A prepared outfit costs a couple hundred MB of workspace, which is why the
    // VISIT is the trigger: the folder grows with the outfits actually worked on.

    /// <summary>The queue's work identity: the ledger's (character, outfit stem) pair.</summary>
    internal readonly record struct SubjectKey(string Character, string Stem);

    /// <summary>Case-insensitive, matching every other subject comparison in the ledger.</summary>
    internal sealed class SubjectKeyComparer : IEqualityComparer<SubjectKey>
    {
        public static readonly SubjectKeyComparer Instance = new();
        public bool Equals(SubjectKey a, SubjectKey b) =>
            string.Equals(a.Character, b.Character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Stem, b.Stem, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(SubjectKey k) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.Character),
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.Stem));
    }

    private readonly PrewarmQueue<SubjectKey> _prewarm;

    /// <summary>The outfits already preparing or prepared. The queue only dedupes RUNNING or waiting
    /// work, and the tree restores its selection on every hop into Edit, so a finished outfit would
    /// re-queue on most hops without this. A failed preparation drops its entry, so a later visit
    /// retries. Dropped per subject when its workspace is removed, wholesale on mod change. UI thread.</summary>
    private readonly HashSet<SubjectKey> _prewarmed = new(SubjectKeyComparer.Instance);

    /// <summary>The queue itself, for tests that pin what a visit, remove and open do to speculative work.</summary>
    internal PrewarmQueue<SubjectKey> Prewarm => _prewarm;

    /// <summary>The record itself, for tests that pin which runs leave an outfit counting as prepared — the
    /// give-up exits complete synchronously with no game files behind them, so the queue shows nothing.</summary>
    internal IReadOnlyCollection<SubjectKey> Prepared => _prewarmed;

    private static SubjectKey KeyOf(Workbench.WorkbenchSubjectRef subject) => new(subject.Character, subject.Stem);

    /// <inheritdoc />
    public void PrewarmSubject(Workbench.WorkbenchSubjectRef subject)
    {
        var key = KeyOf(subject);
        if (_prewarmed.Add(key)) _prewarm.Enqueue(key);
    }

    /// <summary>Drop an outfit's record for a run that did NOT prepare it, so a later visit retries. A
    /// PREEMPTED key is back at the head of the queue and will resume, so it keeps its record.
    /// Marshalled like the rest of the record's writers.</summary>
    private void UnmarkPrewarm(SubjectKey key)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        { Dispatcher.UIThread.Post(() => UnmarkPrewarm(key)); return; }
        if (_prewarm.Pending.Any(k => SubjectKeyComparer.Instance.Equals(k, key))) return;
        _prewarmed.Remove(key);
    }

    /// <summary>Hold the prewarm queue for an explicit action on this subject. Preempts a prewarm running
    /// for anything else; one running for THIS subject is awaited, its progress relayed to
    /// <paramref name="status"/> — unless <paramref name="preemptSameKey"/>, which a one-file action
    /// passes: the whole outfit's combine is a long wait to buy a single file, and the prewarm resumes on
    /// release. A speculative job only yields at its next checkpoint, so a claim can hold for seconds —
    /// the wait says so from its first instant; a claim that resolves at once says nothing.</summary>
    private Task<IPrewarmClaim> ClaimPrewarmAsync(Workbench.WorkbenchSubjectRef subject, IProgress<string> status,
        bool preemptSameKey = false, string? waitLead = null) =>
        _prewarm.ClaimAsync(KeyOf(subject), status, preemptSameKey,
            onWait: cancelling => status.Report(PrewarmWaitLine(cancelling, waitLead)));

    /// <summary>What an explicit action says while it waits for speculative work to yield.
    /// <paramref name="lead"/> names the action just asked for, so the wait reads as that action starting;
    /// with none the wait is the whole line.</summary>
    internal static string PrewarmWaitLine(bool cancelling, string? lead = null) =>
        lead is null
            ? (cancelling ? "Stopping outfit preparation…" : "Finishing outfit preparation…")
            : $"{lead} · {(cancelling ? "stopping" : "finishing")} outfit preparation";

    /// <summary>Wraps the queue's job so the status bar knows a speculative run is up, for its whole
    /// length — the job reads long before it writes anything, and the cell is about the disk being busy.
    /// A job that throws leaves the queue for good, so its outfit stops counting as prepared here.</summary>
    private Func<SubjectKey, IProgress<string>, CancellationToken, Task> Tracked(
        Func<SubjectKey, IProgress<string>, CancellationToken, Task> job) =>
        async (key, status, ct) =>
        {
            SetPrewarmRunning(true);
            try { await job(key, status, ct); }
            catch { UnmarkPrewarm(key); throw; }
            finally { SetPrewarmRunning(false); }
        };

    private void SetPrewarmRunning(bool running)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        { Dispatcher.UIThread.Post(() => SetPrewarmRunning(running)); return; }
        _prewarmRunning = running;
        RefreshBackgroundStatus();
    }

    /// <summary>Record a measurement pass's progress, or null when the pass has ended. Reports are
    /// dropped unless <paramref name="owner"/> is still the CURRENT pass: a superseded pass ends
    /// asynchronously — its final null posts after its successor's first count — and would blank the cell
    /// the new pass is writing.</summary>
    private void ReportSharingProgress(CancellationTokenSource owner, SharingProgress? progress)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        { Dispatcher.UIThread.Post(() => ReportSharingProgress(owner, progress)); return; }
        if (!ReferenceEquals(_sharingCts, owner)) return;
        _sharingProgress = progress;
        RefreshBackgroundStatus();
    }

    /// <summary>Record whether the CURRENT pass failed. Same identity guard as the progress reports: a
    /// superseded pass's ending says nothing about the one now running.</summary>
    private void SetSharingFailed(CancellationTokenSource owner, bool failed)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        { Dispatcher.UIThread.Post(() => SetSharingFailed(owner, failed)); return; }
        if (!ReferenceEquals(_sharingCts, owner)) return;
        _sharingFailed = failed;
        RefreshBackgroundStatus();
    }

    /// <summary>An <see cref="IProgress{T}"/> that runs on the REPORTING thread, so reports keep the order
    /// the work made them in. <see cref="Progress{T}"/> hands them to the thread pool when it is built off
    /// the UI thread, and a count that goes backwards is a lie about what the app is doing.</summary>
    private sealed class InOrderProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InOrderProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }

    /// <summary>The one background-work line, or "" for none. The measurement comes first: a build waits
    /// on it and on nothing else here.</summary>
    internal static string BackgroundWorkLine(SharingProgress? sharing, bool prewarming) =>
        sharing is { } s ? SharingLine(s) : prewarming ? PrewarmLine : "";

    private static string SharingLine(SharingProgress s) =>
        (s.Delta ? "Updating asset sharing" : "Measuring asset sharing")
        + (s.Total > 0 ? $"… {s.Done}/{s.Total}" : "…");

    internal const string PrewarmLine = "Preparing outfits…";

    /// <summary>What the measurement pass is for. The cell's label is a bare count, so the tooltip
    /// carries the whole answer.</summary>
    internal const string SharingCellTip =
        "Reading which outfits share textures and meshes. Only Build waits on it.";

    /// <summary>What a failed pass leaves on the cell: a build after this one discloses instead of
    /// scoping, and Rescan is the retry.</summary>
    internal const string SharingUnmeasured = "Asset sharing unmeasured";
    internal const string SharingUnmeasuredDetail = "Edits ship unscoped. Rescan game files to retry.";

    /// <summary>The background-work cell, as a pure rule. Running work outranks a past failure — a pass is
    /// answering the question the failure raised — and a failure that nothing is replacing STAYS, since a
    /// long visible run ending in a blank cell reads as success.</summary>
    internal static StatusFacet BackgroundFacet(SharingProgress? sharing, bool prewarming, bool sharingFailed)
    {
        if (sharing is { } s) return StatusFacet.Loading(SharingLine(s), SharingCellTip);
        if (prewarming) return StatusFacet.Loading(PrewarmLine);
        return sharingFailed ? StatusFacet.Warn(SharingUnmeasured, SharingUnmeasuredDetail) : StatusFacet.None;
    }

    /// <summary>Recompute <see cref="BackgroundStatus"/> from the current background work, and re-stream it
    /// onto a build that is waiting for the measurement. UI thread.</summary>
    private void RefreshBackgroundStatus()
    {
        BackgroundStatus = BackgroundFacet(_sharingProgress, _prewarmRunning, _sharingFailed);
        // One line for one wait: the footer says exactly what the cell says, count included.
        if (_buildWaitingOnSharing && BackgroundWorkLine(_sharingProgress, prewarming: false) is { Length: > 0 } line)
            Footer = Footer.Streaming(line);
    }

    /// <summary>The queue's job, marshalled onto the UI thread: the shell's materialize bookkeeping
    /// (<see cref="_busySubject"/>, the watcher disarm, the commit hop) is UI-thread state, and the heavy
    /// read/encode/build inside it is already off-thread.</summary>
    private Task PrewarmOutfitAsync(SubjectKey key, IProgress<string> status, CancellationToken ct)
    {
        if (Dispatcher.UIThread.CheckAccess()) return PrewarmOutfitOnUiAsync(key, status, ct);
        var landed = new TaskCompletionSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await PrewarmOutfitOnUiAsync(key, status, ct); landed.TrySetResult(); }
            catch (Exception e) { landed.TrySetException(e); }
        });
        return landed.Task;
    }

    /// <summary>Every exit that did NOT prepare the outfit un-marks it, so the record and the workspace
    /// agree — a record left standing would stop the outfit ever being prepared.</summary>
    private async Task PrewarmOutfitOnUiAsync(SubjectKey key, IProgress<string> status, CancellationToken ct)
    {
        var prepared = false;
        try
        {
            if (_vfs is null || string.IsNullOrEmpty(GameDir)) return;
            if (!_project.HasSubject(key.Character, key.Stem)) return;   // unchecked before its turn came
            if (PickOutfit(key.Character, key.Stem) is not { } outfit) return;
            var subject = new Workbench.WorkbenchSubjectRef(key.Character, outfit.Stem, outfit.MeshPrefix, outfit);
            var gameDir = GameDir;
            var projectAtEntry = _project;

            // The part list comes from the SAME builder the workbench tree uses, so a prewarm and an open agree
            // on what the outfit is; the tree itself may not exist yet (the modder is still on Pick).
            IReadOnlyList<RecipePart> recipes;
            try
            {
                recipes = await Task.Run(() =>
                {
                    var catalog = CatalogIndex.LoadCached(gameDir);
                    if (catalog is null) return (IReadOnlyList<RecipePart>)Array.Empty<RecipePart>();
                    return SubjectModelBuilder.Build(catalog, TryDeobfuscateBundle, outfit, key.Character)
                        .Parts.Select(p => p.ToRecipePart()).ToList();
                }, ct);
            }
            catch { return; }   // silent: an open reads the same structure and reports what it finds
            if (recipes.Count == 0 || ct.IsCancellationRequested) return;

            await OpenOutfitCoreAsync(subject, recipes, sessionPartToken: null, status, ct, launch: false);
            // The core returns silently when its run was cancelled, when the mod changed under it, and when
            // there is no mod folder to write into — so completion is read here, not assumed from the return.
            prepared = _modRoot is not null && !OpenAllShouldAbort(ct, projectAtEntry, _project);
        }
        finally { if (!prepared) UnmarkPrewarm(key); }
    }

    /// <summary>A picked outfit's roster <see cref="Outfit"/>, or null when the Pick tree doesn't carry
    /// it.</summary>
    private Outfit? PickOutfit(string character, string stem) =>
        AllPickRows.FirstOrDefault(c => string.Equals(c.Name, character, StringComparison.OrdinalIgnoreCase))
            ?.Outfits.FirstOrDefault(o => string.Equals(o.Stem, stem, StringComparison.OrdinalIgnoreCase))?.Model;

    /// <summary>The shell entry: claim the prewarm queue first, so a speculative outfit build can't write
    /// the same workspace. Callers ALREADY under a claim go straight to
    /// <see cref="MaterializePartInnerAsync"/> — a second claim for a running key would wait on itself.</summary>
    public async Task<PartMaterializeOutcome> MaterializePartAsync(Workbench.WorkbenchSubjectRef subject, RecipePart recipe,
        IProgress<string> status, CancellationToken ct)
    {
        // One part, so a prewarm on this very subject is preempted rather than waited out: the whole outfit's
        // combine costs far more than letting the prewarm restart after the release.
        using var claim = await ClaimPrewarmAsync(subject, status, preemptSameKey: true);
        return await MaterializePartInnerAsync(subject, recipe, status, ct);
    }

    private async Task<PartMaterializeOutcome> MaterializePartInnerAsync(Workbench.WorkbenchSubjectRef subject, RecipePart recipe,
        IProgress<string> status, CancellationToken ct, bool background = false)
    {
        if (_vfs is null || string.IsNullOrEmpty(GameDir)) { status.Report("Game files aren't loaded yet."); return PartMaterializeOutcome.Failed; }
        AutoNameFromSubject(subject);   // name an unnamed project from the first subject, before the mint
        if (!EnsureModRoot()) { status.Report("Couldn't create the mod folder."); return PartMaterializeOutcome.Failed; }
        if (Materializer.IsPartMaterialized(_project, subject.Character, subject.Stem, subject.MeshPrefix, recipe.Token))
            return PartMaterializeOutcome.Ready();   // idempotent — never enters the scope
        // A part export writes N files under the watched textures/ dir. A modder-driven run disarms for
        // them; a speculative one keeps watching and names its own writes instead.
        using var scope = BeginMaterialize(subject, disarmWatchers: true, background: background);
        return await MaterializePartCore(subject, recipe, status, ct,
            onSelfWrite: background ? SelfWriteSuppressor() : null);
    }

    /// <summary>Materialize one part; the caller holds the <see cref="MaterializeScope"/>. The read/export
    /// runs OFF the UI thread and the commit only lands if the subject is still selected.</summary>
    private async Task<PartMaterializeOutcome> MaterializePartCore(Workbench.WorkbenchSubjectRef subject,
        RecipePart recipe, IProgress<string> status, CancellationToken ct, Action<string>? onSelfWrite = null)
    {
        var partToken = recipe.Token;
        if (Materializer.IsPartMaterialized(_project, subject.Character, subject.Stem, subject.MeshPrefix, partToken))
            return PartMaterializeOutcome.Ready();
        status.Report($"Preparing files for {partToken}…");
        try
        {
            // The prepare captures THIS project; the commit only lands if New Mod / Open hasn't swapped
            // _project underneath it. A switch discards like a subject removal.
            var captured = _project;
            var result = await Materializer.MaterializePartAsync(captured, _vfs!, GameDir, subject.Outfit,
                subject.Character, _modRoot!, recipe,
                commit => Dispatcher.UIThread.Invoke(() => Materializer.CommitIfCurrentProject(captured, _project, commit)), null, ct,
                onSelfWrite);
            // null = the subject was removed between the read and the commit.
            if (result is null) { Workbench.ReportStatus($"{SubjectLabel(subject.Character, subject.Outfit)} was removed while preparing. {partToken} was skipped."); return PartMaterializeOutcome.Failed; }
            if (!result.Usable) { status.Report(result.Error ?? $"Couldn't prepare {partToken}."); return PartMaterializeOutcome.Failed; }
            if (result.Outcome == MaterializeOutcome.Created) { AfterMaterialize(); InvalidateCombinedGlb(subject); }
            // A mesh whose prefab renderer bound no textures must say so — the untextured export is never
            // silent (the ⚠ tree badge is the durable surface).
            status.Report(result.Warning is { } warn ? $"{partToken} ready to edit · {warn}" : $"{partToken} ready to edit.");
            return PartMaterializeOutcome.Ready(result.Warning);
        }
        catch (OperationCanceledException) { return PartMaterializeOutcome.Failed; }
        catch (IOException) { status.Report("The game is using these files. Close the game and try again."); return PartMaterializeOutcome.Failed; }
        catch (Exception e) { status.Report($"Couldn't prepare {partToken}: {e.Message}"); return PartMaterializeOutcome.Failed; }
    }

    /// <summary>The shell entry; claims the prewarm queue like <see cref="MaterializePartAsync"/> does, and
    /// preempts for the same reason — one map is not worth waiting out an outfit.</summary>
    public async Task<bool> MaterializeTextureAsync(Workbench.WorkbenchSubjectRef subject, string textureName,
        string bundleId, IReadOnlyList<string> ownerMeshNames, IProgress<string> status, CancellationToken ct)
    {
        using var claim = await ClaimPrewarmAsync(subject, status, preemptSameKey: true);
        return await MaterializeTextureInnerAsync(subject, textureName, bundleId, ownerMeshNames, status, ct);
    }

    private async Task<bool> MaterializeTextureInnerAsync(Workbench.WorkbenchSubjectRef subject, string textureName,
        string bundleId, IReadOnlyList<string> ownerMeshNames, IProgress<string> status, CancellationToken ct)
    {
        if (_vfs is null || string.IsNullOrEmpty(GameDir)) { status.Report("Game files aren't loaded yet."); return false; }
        AutoNameFromSubject(subject);   // name an unnamed project from the first subject, before the mint
        if (!EnsureModRoot()) { status.Report("Couldn't create the mod folder."); return false; }
        if (Materializer.IsTextureMaterialized(_project, subject.Character, subject.Stem, bundleId, textureName))
        {
            // The subject's map already exists: merge its owner meshes into Users (the Edit-nesting badge).
            if (Materializer.MergeTextureUsers(_project, subject.Character, subject.Stem, bundleId, textureName, ownerMeshNames)) { MarkDirty(); AutoSave(); }
            return true;   // idempotent — never enters the scope
        }
        // A texture write touches ONE file — suppress its path (inside the core) rather than disarm the
        // watcher, which stays armed for other open textures.
        using var scope = BeginMaterialize(subject, disarmWatchers: false);
        // Skipped (HDR/non-authorable) reads as false, so Open aborts silently. Only a real materialize
        // returns true.
        return await MaterializeTextureCore(subject, textureName, bundleId, ownerMeshNames, status, ct) == TexStep.Materialized;
    }

    /// <summary>Landed, hard-failed with a reported reason, or SKIPPED silently — an HDR/non-authorable
    /// format the codec can't re-encode, which yields no target and no error.</summary>
    private enum TexStep { Materialized, Failed, Skipped }

    /// <summary>Materialize one texture; the caller holds the <see cref="MaterializeScope"/>. Decode/encode
    /// runs OFF the UI thread and the commit only lands if the subject is still selected. Under a batch the
    /// watcher is already disarmed, so the SuppressPath is a no-op.</summary>
    private async Task<TexStep> MaterializeTextureCore(Workbench.WorkbenchSubjectRef subject, string textureName,
        string bundleId, IReadOnlyList<string> ownerMeshNames, IProgress<string> status, CancellationToken ct)
    {
        if (Materializer.IsTextureMaterialized(_project, subject.Character, subject.Stem, bundleId, textureName))
        {
            if (Materializer.MergeTextureUsers(_project, subject.Character, subject.Stem, bundleId, textureName, ownerMeshNames)) { MarkDirty(); AutoSave(); }
            return TexStep.Materialized;
        }
        status.Report($"Preparing {textureName}…");
        var modRoot = _modRoot!;
        // Suppress the watcher for this one PNG write or it flips the fresh target Edited=true ~400ms later.
        _texWatcher?.SuppressPath(Materializer.TextureWorkspacePath(modRoot, subject.Character, subject.Stem, bundleId, textureName));
        try
        {
            // Same project-identity guard as the part path.
            var captured = _project;
            var result = await Materializer.MaterializeTextureAsync(captured, _vfs!, modRoot, textureName,
                bundleId, ownerMeshNames, subject.Character, subject.Stem,
                commit => Dispatcher.UIThread.Invoke(() => Materializer.CommitIfCurrentProject(captured, _project, commit)), ct);
            if (result is null) { Workbench.ReportStatus($"{SubjectLabel(subject.Character, subject.Outfit)} was removed while preparing. {textureName} was skipped."); return TexStep.Failed; }
            // An HDR/non-authorable format: skip silently — no target, no error, no per-item batch noise.
            if (result.Outcome == MaterializeOutcome.Skipped) return TexStep.Skipped;
            if (!result.Usable) { status.Report(result.Error ?? $"Couldn't prepare {textureName}."); return TexStep.Failed; }
            if (result.Outcome == MaterializeOutcome.Created) AfterMaterialize();
            status.Report($"{textureName} ready to edit.");
            return TexStep.Materialized;
        }
        catch (OperationCanceledException) { return TexStep.Failed; }
        catch (IOException) { status.Report("The game is using these files. Close the game and try again."); return TexStep.Failed; }
        catch (Exception e) { status.Report($"Couldn't prepare {textureName}: {e.Message}"); return TexStep.Failed; }
    }

    /// <summary>Open ONE part in Blender with the rest of the outfit around it, on the union armature. What
    /// makes it single-part is the session description naming the part: the bridge puts that mesh in
    /// <c>Mod</c> and every other in <c>Reference</c>, so only the named part can come back.</summary>
    public Task OpenPartInBlenderAsync(Workbench.WorkbenchSubjectRef subject, RecipePart recipe,
        IReadOnlyList<RecipePart> outfitRecipes, IProgress<string> status) =>
        OpenOutfitInBlenderAsync(subject, outfitRecipes.Count > 0 ? outfitRecipes : new[] { recipe },
            recipe.Token, status);

    /// <summary>Open ONE part on its own workspace glb, with nothing else in the session — no union
    /// armature needed. The prewarm is PREEMPTED rather than waited out: a single-part session must not
    /// be gated on the whole outfit's combine, the wait this entry point exists to skip.</summary>
    public async Task OpenPartAloneInBlenderAsync(Workbench.WorkbenchSubjectRef subject, RecipePart recipe,
        IProgress<string> status)
    {
        using var claim = await ClaimPrewarmAsync(subject, status, preemptSameKey: true,
            waitLead: $"Opening {recipe.Token}");
        await LaunchLonePartAsync(subject, recipe.Token, new[] { recipe }, status);
    }

    /// <summary>The shell's open entry: take the prewarm claim, then run the same core the speculative
    /// prewarm runs. A finished prewarm leaves the fingerprint cache hot; one in flight for this subject
    /// is awaited (progress relayed here), never restarted; one for another subject is preempted.</summary>
    private async Task OpenOutfitInBlenderAsync(Workbench.WorkbenchSubjectRef subject,
        IReadOnlyList<RecipePart> recipes, string? sessionPartToken, IProgress<string> status)
    {
        using var claim = await ClaimPrewarmAsync(subject, status);
        await OpenOutfitCoreAsync(subject, recipes, sessionPartToken, status, CancellationToken.None, launch: true);
    }

    /// <summary>The Open-all abort guard: a run whose entry token tripped OR whose project is no longer
    /// the one it started on must stop before any further prepare/build/launch.</summary>
    internal static bool OpenAllShouldAbort(CancellationToken tokenAtEntry, object? projectAtEntry, object? currentProject) =>
        tokenAtEntry.IsCancellationRequested || !ReferenceEquals(projectAtEntry, currentProject);

    /// <summary>Open the whole outfit in Blender, every part writable.</summary>
    public Task OpenAllPartsInBlenderAsync(Workbench.WorkbenchSubjectRef subject, IReadOnlyList<RecipePart> recipes, IProgress<string> status) =>
        OpenOutfitInBlenderAsync(subject, recipes, sessionPartToken: null, status);

    /// <summary>The one open-in-Blender route: materialize every part, build or reuse the union-armature
    /// multi-part glb, describe the session beside it, launch. <paramref name="sessionPartToken"/> is the
    /// only difference between the two entry points — null opens every part writable, a token opens that one
    /// with the rest as context. Each part enters as its EDITED workspace glb when it carries an edit, and
    /// as the game mesh otherwise.
    ///
    /// <para><paramref name="launch"/> false is the background prewarm: identical preparation, stopping
    /// short of locating Blender, describing the session and launching. Nothing is reported on that route
    /// and nothing required of it — a failed prewarm leaves the launching route to do the whole job.</para></summary>
    /// <param name="extraCt">Cancelled alongside <c>_materializeCts</c>. The prewarm's own token; the
    /// launching route passes <see cref="CancellationToken.None"/>.</param>
    private async Task OpenOutfitCoreAsync(Workbench.WorkbenchSubjectRef subject,
        IReadOnlyList<RecipePart> recipes, string? sessionPartToken, IProgress<string> status,
        CancellationToken extraCt, bool launch)
    {
        var partTokens = recipes.Select(r => r.Token).ToList();
        // ONE project + ONE cancellation scope, captured HERE. Re-fetching _materializeCts.Token per
        // iteration would hand later iterations a FRESH, un-cancelled token and let them materialize this
        // now-abandoned subject into the NEW project, then launch Blender against it.
        var projectAtEntry = _project;
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(_materializeCts.Token, extraCt);
        var ct = runCts.Token;
        bool Aborted() => OpenAllShouldAbort(ct, projectAtEntry, _project);

        // A part that won't prepare doesn't stop the others — the rest of the outfit still opens around
        // it. The NAMED part is different: it is what this session is for, and its own reported reason is
        // the truthful error; carrying on would replace it with the session-description miss below.
        bool sessionPartFailed = false;
        foreach (var recipe in recipes)
        {
            if (Aborted()) return;   // don't prepare the next part into a swapped-out project
            var outcome = await MaterializePartInnerAsync(subject, recipe, status, ct, background: !launch);
            if (!outcome.Ok && string.Equals(recipe.Token, sessionPartToken, StringComparison.OrdinalIgnoreCase))
                sessionPartFailed = true;
        }
        if (sessionPartFailed) return;
        if (Aborted()) return;   // don't locate/build/launch Blender for an abandoned run
        string? blender = null, script = null;
        if (launch)
        {
            blender = BlenderLocator.Find(BlenderOverride());
            if (blender is null) { status.Report(BlenderGate.NotFound); return; }
            script = BridgeScriptPath();
            if (!File.Exists(script)) { status.Report("Bridge script missing from the app install."); return; }
        }
        if (_modRoot is null) return;
        EnsureWatcher();
        var combined = Path.Combine(_modRoot, Materializer.SubjectFolder(subject.Character, subject.Stem), "meshes", AssetExporter.CombinedGlbName);
        var fingerprintPath = Path.ChangeExtension(combined, ".fingerprint");

        // Computed once so the reuse gate and a rebuild share the same spec. An EDITED part contributes its
        // workspace glb as the source of geometry and skin; an unedited one contributes null and is read
        // from the game.
        var spec = new List<(string, string, string, string?, IReadOnlyList<float>?, long, string?)>();
        var sessionParts = new List<SessionPart>();
        string? sessionMesh = null;
        var staticTokens = recipes.Where(r => r.IsStatic).Select(r => r.Token)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tok in partTokens)
        {
            var t = PartMeshTarget(subject, tok);
            if (t is null) continue;
            string? editedGlb = null;
            bool edited = _project.IsEdited(t);
            if (edited) { try { editedGlb = _project.Resolve(t.ReplaceFile); } catch { editedGlb = null; } }
            spec.Add((tok, t.Bundle, t.ObjectName, (string?)null, t.BakedRest, t.PathId ?? 0, editedGlb));
            sessionParts.Add(new SessionPart(t.ObjectName, edited, Unskinned: staticTokens.Contains(tok)));
            if (string.Equals(tok, sessionPartToken, StringComparison.OrdinalIgnoreCase)) sessionMesh = t.ObjectName;
        }

        // An unnamed session opens EVERY mesh writable, so a send would overwrite the whole outfit from one
        // part's session. Refuse instead.
        if (sessionPartToken is not null && sessionMesh is null)
        { status.Report($"Couldn't find {sessionPartToken}'s mesh."); return; }

        // A part whose game mesh can't be replaced still EXPORTS into the combined glb, but the session
        // must not offer it back: declared unwritable here, the bridge gives it the Reference collection.
        // Off the UI thread, and only for the unnamed session — a NAMED one is scoped to its one mesh and
        // the pane refuses to open an unreplaceable part that way.
        if (launch && sessionPartToken is null)
        {
            await Task.Run(() => DeclareUnwritableParts(TryDeobfuscateBundle, spec, sessionParts));
            if (Aborted()) return;
        }

        var fellBackToGame = new List<string>();
        bool mapRecordLost = false;
        void Launch()
        {
            // Written BEFORE the launch so the bridge's deferred import always finds it. Without it every
            // mesh in the glb would be writable. The send name keeps the send off the published combined,
            // whose fingerprint and map sidecar describe the app's own build.
            try { BlenderBridge.WriteSession(combined, sessionMesh, sessionParts,
                      sendAs: AssetExporter.CombinedSendGlbName); }
            catch (Exception e) { status.Report($"Could not describe the Blender session: {e.Message}"); return; }
            try
            {
                // a named session was opened from that part's row; an open-all belongs to no single row
                WatchBlenderExit(BlenderBridge.Launch(blender!, script!, combined, Path.GetDirectoryName(combined)!), status,
                    subject, sessionPartToken);
                status.Report((sessionPartToken is null
                    ? "Editing the outfit in Blender. Send to Lab returns every part."
                    : $"Editing {sessionPartToken} in Blender. Send to Lab returns {sessionPartToken} only.")
                    + GameFallbackNote(fellBackToGame) + MapRecordLostNote(mapRecordLost));
            }
            catch (Exception e) { status.Report($"Could not launch Blender: {e.Message}"); }
        }
        // A path-id-selected part folds its id into the object component: same-named copies in one enemy
        // bundle are distinct objects, so a re-point must invalidate the cache. The embedded maps are
        // stamped alongside the geometry, so a repainted texture rebuilds instead of reopening the old bake.
        var fingerprint = AssetExporter.CombinedFingerprint(_vfs?.CatalogVersion ?? "unknown",
            spec.Select(s => (s.Item1, s.Item2, s.Item6 != 0 ? $"{s.Item3}#{s.Item6}" : s.Item3, s.Item7)),
            AssetExporter.EmbeddedTexturePaths(_project, spec.Select(s => s.Item3)));

        // Reuse ONLY when the sidecar still matches these inputs AND the file on disk is the one this app
        // published there — a stale spec, or a file Blender's Send overwrote, is rebuilt instead.
        if (AssetExporter.CombinedCacheHit(combined, fingerprintPath, fingerprint))
        {
            // A record lost when this glb was published is gone for every reusing session, and the reuse
            // skips the publish that would have said so — ask here or the maps that come back misclassify.
            mapRecordLost = AssetExporter.CombinedMapRecordMissing(combined);
            if (launch) Launch();
            return;
        }

        if (_vfs is null) { status.Report("Game files aren't loaded yet."); return; }
        if (spec.Count == 0) { status.Report("No parts to open for this subject."); return; }
        if (spec.Count == 1)
        {
            // A single-part subject has no outfit to carry — open its own workspace glb rather than
            // refuse. Its part is already materialized above, so the prewarm route is done.
            if (launch) await LaunchLonePartAsync(subject, sessionPartToken ?? spec[0].Item1, recipes, status);
            return;
        }
        var gameDir = GameDir; var vfs = _vfs;
        var texDir = Path.Combine(_modRoot, "textures"); var outfit = subject.Outfit;
        var recordedTex = AssetExporter.RecordedTextureBundles(_project);
        status.Report("Building the outfit rig for Blender…");
        // Build to a TEMP path so a failed/partial rebuild can never bless the stale _combined.glb on disk.
        // The temp's existence is the only success signal; a failure leaves the old file and fingerprint.
        var tmp = combined + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool combinedBusy = false;
        // Only the ASKED-FOR route: a speculative prewarm's rig build costs the modder no wait and blocks
        // no close, exactly as the prepares that precede it don't.
        _buildingCombinedRig = launch;
        try
        {
            await Task.Run(() =>
            {
                try { AssetExporter.BuildRiggedGlbs(gameDir, vfs, outfit, subject.Character, spec, texDir, status, combinedOut: tmp,
                          recordedTextureBundles: recordedTex, vanillaFallbacks: fellBackToGame, ct: ct); }
                // a game-locked read propagates from BuildRiggedGlbs — surface the BUSY remedy below
                catch (IOException) { combinedBusy = true; }
                catch { /* leave it unbuilt — the temp simply won't exist, so nothing is published below */ }
            });
        }
        // This phase is one of the holds a rescan waits behind, so releasing it drains the queue the way a
        // materialize scope's exit does.
        finally { _buildingCombinedRig = false; RunQueuedRescan(); }
        if (combinedBusy)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort temp cleanup */ }
            status.Report("The game is using these files. Close the game and try again.");
            return;
        }
        if (Aborted())   // a project switch mid-build: don't publish or launch for it
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort temp cleanup */ }
            return;
        }
        if (AssetExporter.PublishCombined(tmp, combined, fingerprintPath, fingerprint,
                onMapSidecarLost: () => mapRecordLost = true))
        {
            // A session where a part degraded to the game copy must not be reused: the fingerprint claims
            // it carries that part's edit. Drop the sidecar so the next open rebuilds and re-states it.
            if (fellBackToGame.Count > 0)
                try { if (File.Exists(fingerprintPath)) File.Delete(fingerprintPath); } catch { /* next open compares and misses anyway */ }
            if (launch) Launch();
            return;
        }
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort temp cleanup */ }
        // Fewer than two parts are skinned, so there is no union armature. Opening one part on its own is
        // still right; only an open-all has nothing left to do.
        if (!launch) return;
        if (sessionPartToken is not null) { await LaunchLonePartAsync(subject, sessionPartToken, recipes, status); return; }
        status.Report("No parts to open together for this outfit.");
    }

    /// <summary>Open a part on its OWN workspace glb: what the modder asks for directly through
    /// <see cref="OpenPartAloneInBlenderAsync"/>, and what the outfit route falls back to when there is no
    /// outfit around the part or too few skinned parts for a union armature. Same session description as the
    /// multi-part route, so the bridge and send-back contract are identical on both.</summary>
    private async Task LaunchLonePartAsync(Workbench.WorkbenchSubjectRef subject, string partToken,
        IReadOnlyList<RecipePart> recipes, IProgress<string> status)
    {
        var recipe = recipes.FirstOrDefault(r => string.Equals(r.Token, partToken, StringComparison.OrdinalIgnoreCase));
        if (recipe is null) { status.Report("No parts to open for this subject."); return; }
        // Inner: the open that reached here already holds the prewarm claim.
        if (!(await MaterializePartInnerAsync(subject, recipe, status, _materializeCts.Token)).Ok) return;
        var target = PartMeshTarget(subject, partToken);
        if (target is null) { status.Report($"Couldn't find {partToken}'s mesh."); return; }
        await LaunchPartInBlender(target, subject, partToken, recipe.IsStatic, status);
    }

    /// <summary>Declare every session part whose GAME mesh can't be replaced unwritable, so the bridge
    /// carries it as Reference scenery. <paramref name="spec"/> and <paramref name="parts"/> are filled in
    /// one lockstep loop, so an index names the same part in both. A mesh that won't read stays writable:
    /// that failure has its own route. Runs OFF the UI thread — each entry is a bundle deobfuscate plus a
    /// Mesh deserialize, shared through one reader.</summary>
    internal static void DeclareUnwritableParts(Func<string, byte[]?> tryDeobfuscate,
        IReadOnlyList<(string, string, string, string?, IReadOnlyList<float>?, long, string?)> spec,
        List<SessionPart> parts)
    {
        var reader = new BundleReader();
        for (int i = 0; i < parts.Count && i < spec.Count; i++)
            if (PartSkinGate.Blocked(tryDeobfuscate, spec[i].Item2, spec[i].Item3, spec[i].Item6, reader)
                is not null)
                parts[i] = parts[i] with { Writable = false };
    }

    /// <summary>The clause appended when a session had to take a part from the game because its edited
    /// file wouldn't read.</summary>
    internal static string GameFallbackNote(IReadOnlyList<string> parts) =>
        parts.Count == 0 ? ""
        : parts.Count == 1 ? $" Couldn't read the edit for {parts[0]}. It opened from the game."
        : $" Couldn't read the edits for {string.Join(", ", parts)}. They opened from the game.";

    /// <summary>The clause appended when the session has no map record. It states the state rather than
    /// the event, because both routes reach it: a publish whose record didn't make it across, and a reuse
    /// of a glb whose record went missing sessions ago.</summary>
    internal static string MapRecordLostNote(bool lost) =>
        lost ? " No texture record for this session. Untouched maps come back as authored copies." : "";

    public async Task OpenMapInEditorAsync(Workbench.WorkbenchSubjectRef subject, string textureName, string bundleId,
        IReadOnlyList<string> ownerMeshNames, IProgress<string> status)
    {
        using var claim = await ClaimPrewarmAsync(subject, status, preemptSameKey: true);
        if (!await MaterializeTextureInnerAsync(subject, textureName, bundleId, ownerMeshNames, status, _materializeCts.Token)) return;
        var target = Materializer.TextureTarget(_project, subject.Character, subject.Stem, bundleId, textureName);
        if (target is null || _project.RootDir is null) { status.Report($"Couldn't find {textureName}."); return; }
        EnsureWatcher();
        var png = _project.Resolve(target.ReplaceFile);
        if (!File.Exists(png)) { status.Report($"{textureName} isn't on disk. Materialize it again."); return; }
        LaunchInImageEditor(png, $"Opened {textureName} in the image editor. Save to send it back.",
            "Could not open the editor", status);
    }

    /// <summary>Open a donor-authored map (a send-back's own PNG) in the image editor. Nothing to
    /// materialize — the file IS the editable copy.</summary>
    public Task OpenAuthoredMapAsync(string authoredPath, IProgress<string> status)
    {
        var name = Path.GetFileName(authoredPath);
        if (!File.Exists(authoredPath))
        {
            status.Report($"{name} isn't on disk. Send the part back from Blender again.");
            return Task.CompletedTask;
        }
        EnsureWatcher();
        LaunchInImageEditor(authoredPath, $"Opened {name} in the image editor. Save to send it back.",
            "Could not open the editor", status);
        return Task.CompletedTask;
    }

    /// <summary>Launch one PNG in the preferred image editor, falling back to the OS default handler. Never
    /// throws out at the caller.</summary>
    private void LaunchInImageEditor(string png, string ok, string failure, IProgress<string> status)
    {
        var editor = ImageEditorLocator.Find(_settings.PreferredImageEditor);
        try
        {
            if (editor is not null)
            {
                var psi = new ProcessStartInfo(editor) { UseShellExecute = false };
                psi.ArgumentList.Add(png);
                Process.Start(psi);
            }
            else Process.Start(new ProcessStartInfo(png) { UseShellExecute = true });
            status.Report(ok);
        }
        catch (Exception e) { status.Report($"{failure}: {e.Message}"); }
    }

    public async Task OpenMapUvGuideAsync(Workbench.WorkbenchSubjectRef subject, string textureName, string bundleId,
        IReadOnlyList<(string MeshName, string MeshAddress, int Submesh, string? ModdedGlb)> samplers, IProgress<string> status)
    {
        // A throwaway paint aid, REBUILT fresh on every touch whenever game files are loaded: that keeps
        // the wireframe current through every mesh change with no staleness bookkeeping. Only when the game
        // files aren't loaded do we fall back to whatever guide already exists.
        if (!EnsureModRoot()) { status.Report("Couldn't create the mod folder."); return; }
        var guide = AssetExporter.UvGuidePathFor(
            Path.Combine(_modRoot!, "textures", TextureExport.BundleScopedName(bundleId, textureName,
                ModNaming.SubjectSlug(subject.Character, subject.Stem))));
        if (_vfs is { } vfs)
        {
            status.Report($"Drawing the UV guide for {textureName}…");
            var problem = await Task.Run(() =>
                AssetExporter.BuildUvGuideOnDemand(vfs, textureName, bundleId, samplers, guide));
            if (problem is not null) { status.Report(problem); return; }
        }
        else if (!File.Exists(guide)) { status.Report("Game files aren't loaded yet. Try again in a moment."); return; }
        // The IMAGE EDITOR, not the default viewer, which can't layer it and may not render the
        // transparency. No watcher: a saved guide is meaningless to the pipeline, so nothing tracks it.
        LaunchInImageEditor(guide, $"Opened the UV guide for {textureName}. Layer it under the paint.",
            "Could not open the UV guide", status);
    }

    public async Task<int> MaterializeAllAsync(Workbench.WorkbenchSubjectRef subject, IReadOnlyList<Workbench.MaterializeItem> items,
        IProgress<string> status, CancellationToken ct)
    {
        using var claim = await ClaimPrewarmAsync(subject, status);
        AutoNameFromSubject(subject);   // name an unnamed project from this subject, before the mint
        if (!EnsureModRoot()) { status.Report("Couldn't create the mod folder."); return 0; }
        int done = 0, created = 0, warned = 0, n = items.Count;
        // Two phases instead of one flat "i of n": a part costs seconds while a texture is one PNG write,
        // so a single counter creeps then leaps, reading as a stall and a jump.
        int partsTotal = items.Count(x => !x.IsTexture), texTotal = n - partsTotal;
        int partIdx = 0, texIdx = 0, partsDone = 0, texDone = 0, skippedTex = 0;
        // A gap between done and n is a real per-item FAILURE (AlreadyPresent counts as done); each one's
        // reason is collected so the final line isn't a bare "24 of 25". `created` separates real work from
        // a no-op run.
        var failures = new List<(string Label, string Reason)>();
        // ONE scope for the whole batch — items call the *Core methods, which don't open their own. The
        // subject stays busy and the watchers disarmed across every item, including the gaps between them.
        using (BeginMaterialize(subject, disarmWatchers: true))
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var it = items[i];
                status.Report(it.IsTexture
                    ? $"Writing texture {++texIdx} of {texTotal} · {it.Label}…"
                    : $"Materializing part {++partIdx} of {partsTotal} · {it.Label}…");
                bool present = it.IsTexture
                    ? Materializer.IsTextureMaterialized(_project, subject.Character, subject.Stem, it.BundleId, it.Token)
                    : Materializer.IsPartMaterialized(_project, subject.Character, subject.Stem, subject.MeshPrefix, it.Token);
                // SYNCHRONOUS capture: a Progress<string> posts its callback to the sync context and would
                // read stale right after the await.
                var sink = new LastMessageSink();
                bool ok;
                bool skipped = false;   // an HDR/non-authorable texture — a silent no-op, neither done nor failed
                if (it.IsTexture)
                {
                    var step = await MaterializeTextureCore(subject, it.Token, it.BundleId, it.OwnerMeshNames ?? System.Array.Empty<string>(), sink, ct);
                    ok = step == TexStep.Materialized;
                    skipped = step == TexStep.Skipped;
                }
                else if (it.Recipe is { } r)
                {
                    var outcome = await MaterializePartCore(subject, r, sink, ct);
                    ok = outcome.Ok;
                    if (outcome.Warning is not null) warned++;   // materialized, but its prefab renderer bound no textures
                }
                else { sink.Report($"{it.Label} isn't recipe-backed."); ok = false; }
                if (skipped)
                    // Counted aside so the summary can fold it out. This does NOT decrement the live
                    // denominator — the per-item "X of Y" keeps its total, so X can never exceed Y.
                    skippedTex++;
                else if (ok) { done++; if (!present) created++; if (it.IsTexture) texDone++; else partsDone++; }
                // a cancelled item returns false WITHOUT reporting — not a prepare failure
                else if (!ct.IsCancellationRequested) failures.Add((it.Label, ShortMaterializeReason(sink.Last)));
            }
        }
        // The summary speaks the same parts/textures vocabulary as the two per-item phases: a flat
        // "38 of 38" after a counter that visibly ran to 9 parts reads as a miscount.
        var where = _modRoot is not null ? Path.GetFileName(_modRoot) : "the mod";
        // Fold the skipped (un-authorable) maps out of the denominator — they were never materializable.
        int texMat = texTotal - skippedTex;
        string counts = texMat == 0 ? $"{partsDone} of {partsTotal} parts"
            : partsTotal == 0 ? $"{texDone} of {texMat} textures"
            : $"{partsDone} of {partsTotal} parts and {texDone} of {texMat} textures";
        // Parts that materialized with no textures resolved — named so the untextured export is never
        // silent (each also carries a ⚠ tree badge).
        string warnedClause = warned == 0 ? ""
            : $" · {warned} part{(warned == 1 ? "" : "s")} had no textures resolve (see {(warned == 1 ? "its" : "their")} ⚠ badge)";
        if (ct.IsCancellationRequested)
            status.Report($"Materialized {counts} into {where}{warnedClause} (stopped).");
        else if (failures.Count == 0 && created == 0 && skippedTex == 0)
            status.Report("Everything is already materialized.");
        else if (failures.Count == 0)
            status.Report($"Materialized {counts} into {where}{warnedClause}.");
        else
        {
            var first = failures[0];
            var line = $"Materialized {counts} into {where}. Couldn't prepare {first.Label}: {first.Reason}.";
            if (failures.Count > 1) line += $" …and {failures.Count - 1} more; see each item's message.";
            status.Report(line);
        }
        return done;
    }

    /// <summary>Keeps only the LAST reported message, captured SYNCHRONOUSLY — unlike
    /// <see cref="Progress{T}"/>, whose callback posts to the sync context and reads stale after an await.</summary>
    private sealed class LastMessageSink : IProgress<string>
    {
        public string? Last { get; private set; }
        public void Report(string value) => Last = value;
    }

    /// <summary>Trim a captured sub-call status to the bare cause: the sub-call prefixes "Couldn't prepare
    /// &lt;token&gt;: …", which the final line already says.</summary>
    private static string ShortMaterializeReason(string? captured)
    {
        var s = (captured ?? "").Trim();
        if (s.Length == 0) return "unknown error";
        const string prefix = "Couldn't prepare ";
        if (s.StartsWith(prefix, StringComparison.Ordinal))
        {
            int colon = s.IndexOf(": ", StringComparison.Ordinal);
            s = colon >= 0 ? s[(colon + 2)..] : "couldn't be prepared";
        }
        return s.TrimEnd('.');
    }

    /// <summary>The revert itself, past the confirm: the baseline goes back over the workspace glb, and
    /// the rig-cache entry for that path goes with it — the restored bytes are the baseline's, which no
    /// texture stamp describes, and a surviving entry would have the next open launch them as though the
    /// rebuild had rigged them.</summary>
    /// <param name="riggedGlbs">The rig-reuse cache keyed by workspace glb path (see
    /// <see cref="_riggedGlbs"/>).</param>
    internal static async Task RevertMeshFileAsync(string orig, string glb, IDictionary<string, string> riggedGlbs)
    {
        await Task.Run(() => File.Copy(orig, glb, overwrite: true));
        riggedGlbs.Remove(glb);
    }

    public async Task RevertPartAsync(Workbench.WorkbenchSubjectRef subject, string partToken, IProgress<string> status)
    {
        var target = PartMeshTarget(subject, partToken);
        if (target?.OriginalFile is null || _project.RootDir is null) { status.Report("No original on record."); return; }
        string glb, orig;
        try { glb = _project.Resolve(target.ReplaceFile); orig = _project.Resolve(target.OriginalFile); }
        catch { status.Report("No original on record."); return; }
        if (!File.Exists(orig)) { status.Report("No original on record."); return; }
        // Reverting overwrites the edited glb irreversibly. The authored maps belong to the mesh they were
        // bound to — whether a Blender session or a card drop authored them — so the prompt names both. An
        // ADOPTED map is different: its file is a texture target's own workspace PNG, so that edit outlives
        // the replacement and ships as a plain texture edit — the prompt must not promise it back to stock.
        bool authoredMaps = target.DonorTextures is { Count: > 0 };
        bool adoptedEditStays = target.DonorTextures?.Any(r =>
            ReferencesEditedTexture(r.Albedo) || ReferencesEditedTexture(r.Normal)
            || ReferencesEditedTexture(r.Rmo)) == true;
        if (MainWindow is not { } owner) return;
        if (!await ConfirmWindow.Show(owner, "Revert to original",
                (authoredMaps
                    ? $"Discard {partToken}'s mesh edit and its authored maps? The game mesh and its stock maps come back."
                    : $"Discard edits to {partToken} and restore the original game mesh?")
                + (adoptedEditStays ? " A texture edit the replacement adopted stays, and ships as its own edit." : ""),
                "Revert", "Cancel", danger: true)) return;
        try
        {
            await RevertMeshFileAsync(orig, glb, _riggedGlbs);
            target.Edited = false;
            // The donor record belongs to the edit, and the restored mesh is the game's. Both keys are
            // defined as null on an unedited mesh target.
            target.DonorTextures = null;
            target.DonorMaterials = null;
            AutoSave();
            Workbench.NotifyMeshEdited(glb);
            status.Report(
                authoredMaps && adoptedEditStays
                    ? $"Reverted {partToken}. Adopted texture edits stay as their own."
                    : authoredMaps ? $"Reverted {partToken}. Its maps are back to stock."
                    : $"Reverted {partToken}.");
        }
        catch (Exception e) { status.Report($"Revert failed: {e.Message}"); }
    }

    /// <summary>Whether a donor-row file is an EDITED texture target's own workspace PNG — an adopted map
    /// whose edit outlives the part's replacement as a plain texture edit.</summary>
    private bool ReferencesEditedTexture(string? donorFile) =>
        donorFile is not null && _project.Targets.Any(x =>
            x.AssetType == "Texture2D" && _project.IsEdited(x)
            && TextureAdoptions.SameFile(x.ReplaceFile, donorFile));

    public async Task RevertMapAsync(Workbench.WorkbenchSubjectRef subject, string textureName, string bundleId,
        IProgress<string> status)
    {
        var t = Materializer.TextureTarget(_project, subject.Character, subject.Stem, bundleId, textureName);
        if (t?.OriginalFile is null || _project.RootDir is null) { status.Report("No original on record."); return; }
        string png, orig;
        try { png = _project.Resolve(t.ReplaceFile); orig = _project.Resolve(t.OriginalFile); }
        catch { status.Report("No original on record."); return; }
        if (!File.Exists(orig)) { status.Report("No original on record."); return; }
        // Reverting overwrites the edited PNG irreversibly. On an adopted map it is also the modder
        // DECLINING the adoption, and the confirm says both halves.
        var meshTargets = Materializer.SubjectMeshTargets(_project, subject.Character, subject.Stem);
        bool adopted = TextureAdoptions.CarriesAdoption(meshTargets, t);
        if (MainWindow is not { } owner) return;
        if (!await ConfirmWindow.Show(owner, "Revert to original",
                adopted
                    ? $"Discard edits to {textureName} and restore the original? The replacement returns to the stock map too."
                    : $"Discard edits to {textureName} and restore the original?",
                "Revert", "Cancel", danger: true)) return;
        try
        {
            _texWatcher?.SuppressPath(png);   // our own write shouldn't re-trigger the watcher
            await Task.Run(() => File.Copy(orig, png, overwrite: true));
            t.Edited = false;
            // The donor slots shipping this file go back to stock with it, so the replacement and the card
            // agree the edit is gone.
            int returned = TextureAdoptions.Unadopt(meshTargets, t);
            AutoSave();
            Workbench.NotifyTextureFileChanged(Path.GetFullPath(png));
            // One map dressing several submeshes or several parts returns several slots, and a line saying
            // "the stock map" about that reports a smaller undo than the one that happened.
            status.Report(returned switch
            {
                0 => $"Reverted {textureName}.",
                1 => $"Reverted {textureName}. The replacement is back on the stock map.",
                _ => $"Reverted {textureName}. The replacement is back on the stock maps.",
            });
        }
        catch (Exception e) { status.Report($"Revert failed: {e.Message}"); }
    }

    public async Task ApplyDroppedPngAsync(Workbench.WorkbenchSubjectRef subject, string textureName, string bundleId,
        IReadOnlyList<string> ownerMeshNames, string path, IProgress<string> status)
    {
        using var claim = await ClaimPrewarmAsync(subject, status, preemptSameKey: true);
        if (!await MaterializeTextureInnerAsync(subject, textureName, bundleId, ownerMeshNames, status, _materializeCts.Token)) return;
        var t = Materializer.TextureTarget(_project, subject.Character, subject.Stem, bundleId, textureName);
        if (t is null || _project.RootDir is null) { status.Report($"Couldn't find {textureName}."); return; }
        var png = _project.Resolve(t.ReplaceFile);
        try
        {
            if (!SamePath(path, png)) { _texWatcher?.SuppressPath(png); File.Copy(path, png, overwrite: true); }
            var line = AdoptTextureEdit(MarkTargetEdited(png));
            AutoSave();
            Workbench.NotifyTextureFileChanged(Path.GetFullPath(png));   // the adoption moved donor rows too
            status.Report($"Applied {Path.GetFileName(path)} to {textureName}."
                + (line is null ? "" : " " + line));
        }
        catch (Exception e) { status.Report($"Import failed: {e.Message}"); }
    }

    public async Task ApplyDroppedPngToAuthoredAsync(string authoredPath, string partToken, string mapRole,
        string path, IProgress<string> status)
    {
        try
        {
            if (!SamePath(path, authoredPath))
            {
                _texWatcher?.SuppressPath(authoredPath);   // our own write shouldn't re-trigger the watcher
                await Task.Run(() => File.Copy(path, authoredPath, overwrite: true));
            }
            // The file IS the record — the target's DonorTextures already name it, so nothing in the
            // project changes.
            Workbench.NotifyTextureFileChanged(Path.GetFullPath(authoredPath));
            // The donor naming convention is the build's vocabulary, not the modder's: report the part and
            // the slot, the same grain the donor route reports.
            status.Report(partToken.Length > 0
                ? $"Applied {Path.GetFileName(path)} as {partToken}'s {mapRole}."
                : $"Applied {Path.GetFileName(path)} as the replacement's {mapRole}.");
        }
        catch (Exception e) { status.Report($"Import failed: {e.Message}"); }
    }

    /// <summary>What both replacement routes end on. Neither has a map-grain way back, so the line leads
    /// with the consequence of the only route that does: the part's Revert, which the card's own tooltip
    /// names the same way.</summary>
    internal const string DroppedMapNoRevert =
        "The only way back is reverting the part, which discards its mesh edit too.";

    /// <summary>Said before the drop rather than after: the emissive mask is rebuilt from the game map,
    /// and glTF has no channel for it, so a mask painted into the dropped image never ships.</summary>
    internal const string DroppedRmoAlphaNote =
        "Alpha comes from the game map's emissive mask. The dropped file's own alpha doesn't ship.";

    /// <summary>Confirm applying a PNG dropped ON a map card. Each route says what it does: a game-texture
    /// edit names the card's role/texture and the shared map's reach; a drop on a REPLACED part reads as the
    /// replacement's own map, and where the card already shows one of those maps it names that map rather
    /// than a game texture the drop never touches. A size mismatch adds a line
    /// stating what happens — the drop applies either way. Anything landing on the replacement is
    /// irreversible at the map grain and carries the danger styling. Declined resolves false so the caller
    /// no-ops.</summary>
    public async Task<bool> ConfirmApplyDroppedPngAsync(Workbench.DroppedPngConfirm ask)
    {
        if (MainWindow is not { } owner) return false;
        var (body, danger) = DroppedPngConfirmBody(ask);
        return await ConfirmWindow.Show(owner, "Apply dropped image", body, "Apply", "Cancel", danger);
    }

    /// <summary>The confirm's body and whether it is the destructive kind.</summary>
    internal static (string Body, bool Danger) DroppedPngConfirmBody(Workbench.DroppedPngConfirm ask)
    {
        bool replacement = ask.Donor is not null || ask.IsAuthored;
        var body = !replacement
            ? $"Apply {ask.FileName} to {ask.MapRole} · {ask.TextureName}?"
            : ask.PartToken.Length > 0
                ? $"Apply {ask.FileName} as {ask.PartToken}'s {ask.MapRole}?"
                : $"Apply {ask.FileName} to {ask.MapRole}?";
        if (ask.Donor is { } donor)
        {
            // A card already showing one of the replacement's own maps has no game texture left to leave
            // alone, so its route is named without one and what the drop overwrites is counted below.
            body += ask.IsAuthored
                ? $"\n\nThis replaces {donor.PartToken}'s own {ask.MapRole} map."
                : $"\n\n{donor.PartToken} is replaced, so this becomes the replacement's own map. "
                    + $"{ask.TextureName} is untouched.";
            // The reach is its own sentence only where it says something the overwrite count doesn't: two
            // counts of the same set of submeshes read as two different reaches.
            if (donor.Submeshes.Count > 1 && ask.AuthoredLanding != donor.Submeshes.Count)
                body += $"\n\nApplies to {donor.Submeshes.Count} submeshes.";
            if (donor.Slot == DonorMapSlot.Rmo) body += "\n\n" + DroppedRmoAlphaNote;
        }
        // The card shows one of the replacement's maps but the record no longer names a replacement the
        // build would ship, so the drop rewrites that one file in place and nothing else.
        else if (ask.IsAuthored)
            body += "\n\nReplaces the map the replacement carries on this submesh. No other submesh changes.";
        else if (ask.OtherWearers > 0)
            body += $"\n\nAlso drawn by {ask.OtherWearers} other part{(ask.OtherWearers == 1 ? "" : "s")}.";
        if (ask.SizeNote is not null) body += "\n\n" + ask.SizeNote;
        // Every landing submesh is overwritten, including ones authored from a card this drop didn't land
        // on — so the count comes off the part's record, not off the dropped card's own state.
        if (ask.AuthoredLanding > 0)
            body += ask.AuthoredLanding == 1
                ? "\n\nReplaces the map 1 submesh already carries."
                : $"\n\nReplaces the maps {ask.AuthoredLanding} submeshes already carry.";
        if (replacement) body += "\n\n" + DroppedMapNoRevert;
        return (body, replacement);
    }

    /// <summary>Author a dropped PNG as a replaced part's own map: write it into <c>textures/</c> under
    /// the donor naming convention through the SAME intake a Blender send-back's maps use (so an authored
    /// RMO's emissive mask is rebuilt from the stock map), then record the authored slot on each covered
    /// submesh. The result is byte-for-byte the record a session-authored map produces, so the build's
    /// donor path binds it with nothing new — and the part's Revert drops it with the mesh edit. A slot the
    /// part already carries a map on lands here too: the naming convention puts the rebuilt file back over
    /// the old one, which is what keeps the mask on a re-drop.</summary>
    public async Task ApplyDroppedPngToDonorMapAsync(Workbench.WorkbenchSubjectRef subject,
        Workbench.DonorMapDrop donor, string mapRole, string path, IProgress<string> status)
    {
        var target = PartMeshTarget(subject, donor.PartToken);
        if (target is null || _project.RootDir is not { } modRoot)
        { status.Report($"Couldn't find {donor.PartToken}'s mesh."); return; }
        // The decision to author was made before the confirm; a Revert or a Blender send-back can land while
        // it is open, and either one voids it. Re-ask both halves against the target as it stands NOW, or the
        // write lands on a part with no replacement or past the shape the replacement came back with.
        if (!_project.IsEdited(target))
        { status.Report($"{Path.GetFileName(path)} {ViewModels.Workbench.DonorDropRefusal.NotReplaced(donor.PartToken)}"); return; }
        int donorSubmeshes = target.DonorMaterials?.Count ?? 0;
        if (donor.Submeshes.Any(i => i < 0 || i >= donorSubmeshes))
        { status.Report($"{Path.GetFileName(path)} {ViewModels.Workbench.DonorDropRefusal.PastTheReplacement(donor.PartToken)}"); return; }
        status.Report($"Preparing {donor.PartToken}'s {mapRole}…");
        var stem = ModNaming.Slug(target.ObjectName);
        var texturesDir = Path.Combine(modRoot, "textures");
        var notes = new List<string>();
        // The mask source, resolved the way the send-back resolves it: the part's own export record first,
        // then what the game renderer binds on that submesh. Asked only for the RMO slot, which is the only
        // one with a mask to rebuild.
        Func<int, string?> stockRmo = _ => null;
        if (donor.Slot == DonorMapSlot.Rmo)
        {
            var recorded = PreviewMaps.ReadSubmeshRmoSources(_project.Resolve(target.ReplaceFile), target.ObjectName);
            // Labelled by the part, which is what the card and the status line name it by; the workspace
            // glb's stem is a file name the modder never chose.
            stockRmo = StockRmoSource(GameStockRmo((subject.Character, subject.Stem), target),
                donor.PartToken, recorded, notes);
        }
        List<(int Submesh, string File)> written;
        try
        {
            written = await Task.Run(() => donor.Submeshes
                .Select(i => (i, DonorTextureIntake.TakeOne(path, texturesDir, stem, i, donor.Slot,
                    stockRmo(i), notes.Add)))
                .ToList());
        }
        catch (Exception e) { status.Report($"Import failed: {e.Message}"); return; }

        var rows = target.DonorTextures ??= new List<SubmeshTextures>();
        foreach (var (submesh, file) in written)
        {
            var row = rows.FirstOrDefault(r => r.Submesh == submesh);
            // A submesh with no row yet has every slot still on the part's own stock maps — say so, or
            // the two untouched slots read as "no image at all" and ship the build's flat normal and RMO.
            // An EXISTING row keeps whatever it recorded.
            if (row is null)
                rows.Add(row = new SubmeshTextures
                {
                    Submesh = submesh,
                    AlbedoOrigin = SlotOrigin.VanillaOwn,
                    NormalOrigin = SlotOrigin.VanillaOwn,
                    RmoOrigin = SlotOrigin.VanillaOwn,
                });
            var rel = Rel(file);
            switch (donor.Slot)
            {
                case DonorMapSlot.BaseColor: row.Albedo = rel; row.AlbedoOrigin = SlotOrigin.Authored; break;
                case DonorMapSlot.Normal: row.Normal = rel; row.NormalOrigin = SlotOrigin.Authored; break;
                default: row.Rmo = rel; row.RmoOrigin = SlotOrigin.Authored; break;
            }
        }
        rows.Sort((a, b) => a.Submesh.CompareTo(b.Submesh));
        AutoSave();
        foreach (var (_, file) in written) Workbench.NotifyTextureFileChanged(Path.GetFullPath(file));
        var applied = $"Applied {Path.GetFileName(path)} as {donor.PartToken}'s {mapRole}"
                    + (written.Count > 1 ? $" · {written.Count} submeshes." : ".");
        // Every distinct note, as the send-back summary joins its own: one submesh losing its mask says
        // nothing about the others, and reporting only the first hides the rest.
        status.Report(notes.Count > 0 ? applied + " " + string.Join(" ", notes.Distinct()) : applied);
    }

    // ---- workbench-shell helpers ----

    /// <summary>Post-materialize bookkeeping: reflect the subject onto the Pick tree, persist.</summary>
    private void AfterMaterialize()
    {
        StampAuthoredAgainst();   // content was authored under the live catalog
        SyncSubjectsFromLedger();
        AutoSave();
    }

    /// <summary>Stamp the project's <c>authored_against</c> catalog version on the first materialize, and
    /// refresh it under a NEW catalog. A no-op under the same catalog, so the stamp never churns.</summary>
    private void StampAuthoredAgainst()
    {
        if (_vfs is null) return;
        var live = _vfs.CatalogVersion;
        var next = AuthoredAgainstPolicy.StampFor(_project.AuthoredAgainst?.CatalogVersion, live);
        if (string.Equals(next, _project.AuthoredAgainst?.CatalogVersion, StringComparison.Ordinal)) return;
        _project.AuthoredAgainst = new AuthoredAgainst { CatalogVersion = next };
        _authoredNoticeShownFor = null;   // re-authored — the stale notice can re-fire
    }

    /// <summary>The live catalog version the "authored against an older version" notice was already shown
    /// for, so it fires once per catalog change.</summary>
    private string? _authoredNoticeShownFor;

    /// <summary>The one-time "authored against an older version" notice, or null when it doesn't apply.
    /// Marks it shown when it returns a pair. BOTH surfacing paths go through here, so the one-shot
    /// semantics and the wording stay in one place.</summary>
    private (string Short, string Detail)? TakeAuthoredAgainstNotice()
    {
        if (ShowHome || _vfs is null) return null;
        var live = _vfs.CatalogVersion;
        if (!AuthoredAgainstPolicy.NeedsStaleNotice(_project.AuthoredAgainst?.CatalogVersion, live)) return null;
        if (string.Equals(_authoredNoticeShownFor, live, StringComparison.Ordinal)) return null;
        _authoredNoticeShownFor = live;
        return ("authored against an older version",
            "This mod was authored against an older game version. Check your edits, then build to refresh.");
    }

    // ---- settings persistence ----

    /// <summary>A settings write failed this run. Latched: a folder that refuses one write refuses the
    /// rest, so the failure is reported once rather than per save.</summary>
    private bool _settingsSaveFailed;

    /// <summary>The latched failure is already on the notice cell.</summary>
    private bool _settingsSaveNoticeShown;

    private const string SettingsSaveFailedShort = "settings not saved";
    private const string SettingsSaveFailedDetail =
        "Settings can't be saved. Changes are lost on exit. Move the app out of a protected folder.";

    /// <summary>Persist settings, reporting a failed write instead of dropping it. A silent failure is the
    /// bad case: the change looks applied and is gone at exit.</summary>
    private void SaveSettings()
    {
        if (_settings.TrySave()) return;
        _settingsSaveFailed = true;
        // ResolveGameDir saves off the UI thread, and the notice cell is UI-thread state. That save runs
        // inside the load, whose finalize folds the latch into its own notice list — so an off-thread
        // failure is reported there rather than raced onto the cell from here.
        if (Dispatcher.UIThread.CheckAccess()) ShowSettingsSaveNotice();
    }

    /// <summary>Put the latched settings-write failure on the notice cell, once. UI thread.</summary>
    private void ShowSettingsSaveNotice()
    {
        if (!_settingsSaveFailed || _settingsSaveNoticeShown) return;
        _settingsSaveNoticeShown = true;
        MergeNoticeIntoCell((SettingsSaveFailedShort, SettingsSaveFailedDetail));
    }

    /// <summary>Surface the stale-version notice on the plain-open path. It must never clobber a warning
    /// the load already put in the cell: replace only an EMPTY cell, otherwise merge.</summary>
    private void MaybeNoticeAuthoredAgainst()
    {
        if (TakeAuthoredAgainstNotice() is { } notice) MergeNoticeIntoCell(notice);
    }

    /// <summary>Fold one notice into the cell without losing what's there: an occupied cell is rebuilt as
    /// the same "N warnings" + "• "-bulleted facet the load-finalize aggregation renders.</summary>
    private void MergeNoticeIntoCell((string Short, string Detail) add)
    {
        if (!NoticeStatus.HasGlyph)
        {
            NoticeStatus = StatusFacet.Warn(add.Short, "• " + add.Detail);
            return;
        }
        var details = SplitNoticeDetails(NoticeStatus.Detail).Append(add.Detail).ToList();
        NoticeStatus = StatusFacet.Warn(
            details.Count == 1 ? add.Short : $"{details.Count} warnings",
            string.Join("\n", details.Select(d => "• " + d)));
    }

    /// <summary>The inverse of the "• "-bulleted join, so a merge re-aggregates cleanly.</summary>
    private static IEnumerable<string> SplitNoticeDetails(string detail) =>
        string.IsNullOrEmpty(detail)
            ? Enumerable.Empty<string>()
            : detail.Split('\n').Select(l => l.StartsWith("• ", StringComparison.Ordinal) ? l[2..] : l);

    /// <summary>Drop both watchers for a part/batch materialize's own writes, which land under the watched
    /// textures/ dir and would otherwise flip the fresh target Edited=true ~400ms later. Re-armed when the
    /// <see cref="MaterializeScope"/> ends. UI-thread only.</summary>
    private void DisarmWatchers()
    {
        _watcher?.Dispose(); _watcher = null;
        _texWatcher?.Dispose(); _texWatcher = null;
    }

    /// <summary>The texture watcher's self-write channel for a materialize that keeps it armed: the export
    /// announces each textures/ path before writing it, so those writes never read as the modder's own edit.
    /// Null when nothing is watching. Captured on the UI thread; the returned delegate is called from the
    /// export's thread, which <see cref="TextureEditWatcher.SuppressPath"/> allows.</summary>
    private Action<string>? SelfWriteSuppressor() =>
        _texWatcher is { } w ? w.SuppressPath : null;

    /// <summary>Drop the cached combined glb after a NEW part materialized. Best-effort — the authoritative
    /// staleness guard is the fingerprint in <see cref="OpenAllPartsInBlenderAsync"/>, so a locked file is
    /// still rebuilt rather than reused.</summary>
    private void InvalidateCombinedGlb(Workbench.WorkbenchSubjectRef subject)
    {
        if (_modRoot is null) return;
        var combined = Path.Combine(_modRoot, Materializer.SubjectFolder(subject.Character, subject.Stem),
            "meshes", AssetExporter.CombinedGlbName);
        try { if (File.Exists(combined)) File.Delete(combined); }
        catch { /* locked/busy — leave it; the next Open-all rebuilds when it can */ }
    }

    /// <summary>Reveal the subject's export folder, falling back to the mod root when nothing has
    /// materialized yet.</summary>
    public void ShowSubjectInFolder(Workbench.WorkbenchSubjectRef subject)
    {
        if (_modRoot is null) return;
        var dir = Path.Combine(_modRoot, Materializer.SubjectFolder(subject.Character, subject.Stem));
        try
        {
            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{dir}\"", UseShellExecute = true });
            else if (Directory.Exists(_modRoot))
                Process.Start(new ProcessStartInfo { FileName = _modRoot, UseShellExecute = true });
        }
        catch { /* best-effort reveal */ }
    }

    /// <summary>The Edit subject-header entry point, routing to the SAME
    /// <see cref="RemoveSubjectAsync(string,string,string,string)"/> the Pick uncheck uses.</summary>
    public Task RemoveSubjectAsync(Workbench.WorkbenchSubjectRef subject) =>
        RemoveSubjectAsync(subject.Character, subject.Stem, subject.MeshPrefix, SubjectLabel(subject.Character, subject.Outfit));

    private string? BlenderOverride() => !string.IsNullOrWhiteSpace(_settings.PreferredBlender) ? _settings.PreferredBlender
        : string.IsNullOrWhiteSpace(BlenderPath) ? null : BlenderPath;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The Mesh target for a subject part.</summary>
    private ProjectTarget? PartMeshTarget(Workbench.WorkbenchSubjectRef s, string token) =>
        Materializer.PartMeshTarget(_project, s.Character, s.Stem, s.MeshPrefix, token);


    /// <summary>Rig the part lazily when the game is loaded and the part is unedited/unrigged, then launch
    /// Blender on its workspace glb and arm the send-back watcher.</summary>
    private async Task LaunchPartInBlender(ProjectTarget target, Workbench.WorkbenchSubjectRef subject,
        string partToken, bool unskinned, IProgress<string> status)
    {
        var blender = BlenderLocator.Find(BlenderOverride());
        if (blender is null) { status.Report(BlenderGate.NotFound); return; }
        var script = BridgeScriptPath();
        if (!File.Exists(script)) { status.Report("Bridge script missing from the app install."); return; }
        if (_project.RootDir is null) return;
        EnsureWatcher();
        var glbPath = _project.Resolve(target.ReplaceFile);
        void Launch()
        {
            try { BlenderBridge.WriteSession(glbPath, target.ObjectName,
                      new[] { new SessionPart(target.ObjectName, _project.IsEdited(target), Unskinned: unskinned) }); }
            catch (Exception e) { status.Report($"Could not describe the Blender session: {e.Message}"); return; }
            try { WatchBlenderExit(BlenderBridge.Launch(blender, script, glbPath, Path.GetDirectoryName(glbPath)!), status,
                      subject, partToken);
                  status.Report($"Editing {partToken} in Blender. Send to Lab returns {partToken} only."); }
            catch (Exception e) { status.Report($"Could not launch Blender: {e.Message}"); }
        }
        // Gate on the byte-compare, not only the persisted flag: this route regenerates the workspace glb
        // from GAME geometry, so reaching it with an edited file on disk (however the flag went stale)
        // destroys that edit beyond recovery. An EDITED part launches the workspace glb exactly as it
        // stands; the texture stamp governs only the UNEDITED rebuild and is the reuse key — the rig
        // re-opens only while every texture it baked in is still the file it was built from.
        var texStamp = _modRoot is null ? "" :
            AssetExporter.TextureStamps(AssetExporter.EmbeddedTexturePaths(_project, new[] { target.ObjectName }));
        if (_vfs is null || target.Edited || _project.IsEdited(target) || _modRoot is null
            || (_riggedGlbs.TryGetValue(glbPath, out var riggedWith) && riggedWith == texStamp))
        { Launch(); return; }

        // Edited is a byte-compare of the workspace glb against its originals/ baseline, so this rebuild has
        // to land in BOTH files or an untouched part reads as edited from here on. Resolve the baseline
        // BEFORE anything is written: with nowhere to publish it, the rebuild must not start.
        string? origGlb = null;
        if (target.OriginalFile is { } baselineRel)
            try { origGlb = _project.Resolve(baselineRel); } catch { origGlb = null; }
        if (origGlb is null) { status.Report("Couldn't locate the part's baseline copy. Blender not opened."); return; }

        status.Report("Building the rig for Blender…");
        var gameDir = GameDir; var vfs = _vfs; var outfit = subject.Outfit;
        var texDir = Path.Combine(_modRoot, "textures");
        var recordedTex = AssetExporter.RecordedTextureBundles(_project);
        // Staged beside the workspace glb, never onto it: the preview-map sidecar records its images
        // RELATIVE to the glb's own folder, so a rebuild written anywhere else would carry paths that stop
        // resolving the moment it moves into place.
        var stagedGlb = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(glbPath))!,
            "~rebuild." + Path.GetFileName(glbPath));
        // EditedGlb stays null: this route REWRITES the workspace glb and only runs for an unedited part.
        var spec = new[] { (partToken, target.Bundle, target.ObjectName, (string?)stagedGlb, (IReadOnlyList<float>?)target.BakedRest, target.PathId ?? 0, (string?)null) };
        string? rigError = null;
        string? publishError = null;
        bool rigBusy = false;
        var done = await Task.Run<IReadOnlyList<string>?>(() =>
        {
            try
            {
                var d = AssetExporter.BuildRiggedGlbs(gameDir, vfs, outfit, target.SubjectCharacter ?? "", spec, texDir, status, recordedTextureBundles: recordedTex);
                // Nothing staged ⇒ the rig never landed, and both files stand as they were — which is what
                // the geometry-only refusal at the bottom is for.
                if (d is not { Count: > 0 } || !File.Exists(stagedGlb)) return null;
                publishError = PublishRebuiltPartGlb(stagedGlb, glbPath, origGlb);
                return d;
            }
            // Steer a game-locked read to the BUSY remedy — "rig build produced nothing" reads as a decode
            // failure. The publish above reports its own failures rather than throwing, so a locked
            // workspace file never arrives here dressed as a locked game file.
            catch (IOException) { rigBusy = true; return null; }
            catch (Exception e) { rigError = e.Message; return null; }
            finally { DiscardStagedGlb(stagedGlb); }
        });
        if (publishError is not null) { status.Report(publishError); return; }
        if (done is { Count: > 0 }) { _riggedGlbs[glbPath] = texStamp; Launch(); return; }
        if (rigBusy) { status.Report("The game is using these files. Close the game and try again."); return; }
        // The workspace glb is still the geometry-only export, so a Send from that session would come back
        // weightless. Refuse loudly rather than silently arm a doomed edit.
        status.Report(rigError is null
            ? $"Couldn't build the rig for {partToken} (its bundle didn't decode). Not opening Blender, a send-back would lose the weights."
            : $"Couldn't build the rig for {partToken}: {rigError}. Not opening Blender.");
    }

    /// <summary>Publish a staged part rebuild: the <c>originals/</c> baseline FIRST, the workspace glb
    /// second. Edited is a byte-compare of the two, so an untouched part stays untouched only while they
    /// move together — writing the workspace file first and failing on the baseline is what marks a part
    /// edited with no edit behind it. Returns null on success, else the line to report; on failure both
    /// files stand as they were and the caller does not launch. The preview-map sidecar travels with the
    /// glb, its ABSENCE included, since that is what retires a stale one.</summary>
    internal static string? PublishRebuiltPartGlb(string stagedGlb, string glbPath, string origGlb)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(origGlb))!);
            File.Copy(stagedGlb, origGlb, overwrite: true);
        }
        catch (Exception e) { return $"Couldn't refresh the part's baseline copy: {e.Message}. Blender not opened."; }
        try
        {
            // The map record moves first, so the glb move is the LAST thing that can fail and the workspace
            // glb is untouched on every failing path through here.
            var stagedMaps = PreviewMaps.SidecarPath(stagedGlb);
            var maps = PreviewMaps.SidecarPath(glbPath);
            if (File.Exists(stagedMaps)) File.Move(stagedMaps, maps, overwrite: true);
            else if (File.Exists(maps)) File.Delete(maps);
            File.Move(stagedGlb, glbPath, overwrite: true);
            return null;
        }
        catch (Exception e)
        {
            // The workspace glb never moved, so the baseline just published no longer matches it. This route
            // only runs for an UNEDITED part, which is byte-equality, so copying the workspace glb back
            // restores the pair exactly. Say so when even that fails: the part then reads edited.
            try { File.Copy(glbPath, origGlb, overwrite: true); }
            catch { return $"Couldn't rebuild the part's workspace file: {e.Message}. Its baseline copy is out of step. Blender not opened."; }
            return $"Couldn't rebuild the part's workspace file: {e.Message}. Blender not opened.";
        }
    }

    /// <summary>Drop a staged rebuild and its map sidecar. A publish already consumed them; what a failure
    /// leaves is inert either way, and the next rebuild stages over it.</summary>
    internal static void DiscardStagedGlb(string stagedGlb)
    {
        try { if (File.Exists(stagedGlb)) File.Delete(stagedGlb); } catch { /* inert leftover */ }
        var maps = PreviewMaps.SidecarPath(stagedGlb);
        try { if (File.Exists(maps)) File.Delete(maps); } catch { /* inert leftover */ }
    }
}
