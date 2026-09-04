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
using Remold.Core.Migoto;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Textures;
using Remold.Core.Workbench;
namespace Remold.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
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
        (string.IsNullOrWhiteSpace(PackageName) ? "Untitled mod" : PackageName.Trim()) + (IsDirty ? " *" : "");
    public ObservableCollection<RecentModVm> RecentMods { get; } = new();
    public bool HasRecentMods => RecentMods.Count > 0;

    // Status bar: three facets (game / roster / Blender), a background-work cell, a notice cell.
    // Long remedies and multi-warning lists go in the facet tooltip (Detail), never inline.
    [ObservableProperty] private StatusFacet _gameStatus = StatusFacet.Loading("Game…");
    [ObservableProperty] private string _statusChars = "Characters…";
    [ObservableProperty] private StatusFacet _blenderStatus = StatusFacet.Loading("Blender…");
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

    // The Weapons tab: weapon groups per owner character (plus the Battle Pass loose group), then the
    // per-type standalone-skin groups. Same VM shape, same candidate/confirm fill.
    public ObservableCollection<CharacterVm> Weapons { get; } = new();
    [ObservableProperty] private string _weaponSearchText = "";

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
    public string WeaponsTabHeader
    {
        get { var n = _allWeapons.Sum(c => c.Outfits.Count(o => o.IsInMod)); return n > 0 ? $"Weapons ({n})" : "Weapons"; }
    }
    private void RefreshTabHeaders()
    {
        OnPropertyChanged(nameof(CharactersTabHeader));
        OnPropertyChanged(nameof(EnemiesTabHeader));
        OnPropertyChanged(nameof(WeaponsTabHeader));
    }

    // Edit — the Blender bridge
    [ObservableProperty] private string _blenderPath = "";
    private BlenderSendWatcher? _watcher;
    private readonly List<EditPage.PictureTransportWatcher> _pictureTransports = new();
    private string? _modRoot;

    // Mod identity form (the Name/Author/Version/Description carried in the project manifest).
    [ObservableProperty] private string _packageName = "";
    [ObservableProperty] private string _packageAuthor = "";
    [ObservableProperty] private string _packageDescription = "";
    [ObservableProperty] private string _packageVersion = "1.0";
    /// <summary>Tier-1 toggle key for the whole mod. Null = no key, always on.</summary>
    [ObservableProperty] private string? _packageToggleKey;

    /// <summary>Whether the whole-mod on/off position survives a game restart. Shown only while a key is
    /// set; the choice is kept when the key is cleared, so re-binding one does not forget it.</summary>
    [ObservableProperty] private bool _packagePersistToggleKey;

    /// <summary>Whether a future build should ship the record that lets this project be imported again.
    /// Defaults on, and a project saved before the option existed loads on.</summary>
    [ObservableProperty] private bool _packageIncludesRepairData = true;

    /// <summary>True while the identity form is being POPULATED from a project, so the restore can't read
    /// as an edit and autosave the project back over itself.</summary>
    private bool _loadingIdentityForm;
    /// <summary>An open-mod load is in flight — gates re-entry so a second open can't race the first.</summary>
    [ObservableProperty] private bool _isOpeningMod;

    private List<CharacterVm> _allCharacters = new();
    private List<CharacterVm> _allEnemies = new();
    private List<CharacterVm> _allWeapons = new();
    /// <summary>Every roster-tab pick grid. INVARIANT: any new roster-shaped tab adds its backing list to
    /// this concat, and tab-shared behavior enumerates AllPickRows and never a per-tab list — otherwise the
    /// new tab's picks silently fall out of the queue/ledger/restore.</summary>
    private IEnumerable<CharacterVm> AllPickRows => _allCharacters.Concat(_allEnemies).Concat(_allWeapons);
    // The forward view of the install. Null (install unreadable) disables session resolver routes.
    private GameVfs? _vfs;
    private string _pkgCharacter = "", _pkgOutfit = "";
    private readonly LabSettings _settings = LabSettings.Load();

    /// <summary>Render-time internal-key → friendly-label resolver. Empty until phase 1 builds it, so labels
    /// fall back to the internal token until then. The keys it maps FROM stay internal everywhere.</summary>
    private FriendlyNames _friendly = FriendlyNames.Empty;

    /// <summary>The open project's persistence owner: the one authored session. Replaced — never mutated
    /// in place — when a different project opens, which is the signal every ReferenceEquals guard in the
    /// app reads as "the modder switched projects".</summary>
    private AuthoredProjectDocument _projectDocument = AuthoredProjectDocument.New();

    private AuthoredProject AuthoredSnapshot => _projectDocument.Session.Snapshot();
    private string? CurrentProjectRoot => AuthoredSnapshot.RootDir;
    private ProjectInfo CurrentProjectInfo => AuthoredSnapshot.Info;

    /// <summary>Apply one change to the sole authored session.</summary>
    private void EditProject(Action<AuthoredEditSession> change) =>
        change(_projectDocument.Session);

    private static TargetPart PartOf(string character, string outfit, string rendererSlot) => new()
    {
        Subject = character,
        Outfit = outfit,
        RendererSlot = rendererSlot,
    };

    private IReadOnlyList<SelectionEntry> CurrentSelection()
    {
        var project = AuthoredSnapshot;
        return project.WorkspaceIndex?.Selection
            ?? project.EditDefinitions.Select(edit => new SelectionEntry
                { Character = edit.Target.Subject, Outfit = edit.Target.Outfit })
                .DistinctBy(entry => (entry.Character.ToUpperInvariant(), entry.Outfit.ToUpperInvariant()))
                .ToList();
    }

    private bool ProjectHasSubject(string character, string outfit) => CurrentSelection().Any(entry =>
        string.Equals(entry.Character, character, StringComparison.OrdinalIgnoreCase)
        && string.Equals(entry.Outfit, outfit, StringComparison.OrdinalIgnoreCase));

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
    /// <summary>The current pass ended in a fault. A cancelled pass never sets it — its successor owns the
    /// cell.</summary>
    private bool _sharingFailed;
    /// <summary>A saved selection waiting for the roster fill to resolve outfits so it can re-check them.</summary>
    private List<SelectionEntry>? _pendingSelection;

    /// <summary>The one subject-model memo, shared with the Edit pane — two readers of one subject would
    /// otherwise read its bundles twice.</summary>
    private readonly SubjectModelCache _subjectModels = new();
    private readonly object _textureUseIndexGate = new();
    private readonly Dictionary<SubjectModel, IReadOnlyDictionary<string, int>> _textureUseIndexes =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _shadingSourceCacheGate = new();
    private readonly Dictionary<string, IReadOnlyList<EditPage.ShadingSourceRow>> _shadingSourceRows =
        new(StringComparer.OrdinalIgnoreCase);
    private object? _shadingSourceCacheInstall;
    private readonly EditPage.InstallRampCache _rampCache = new();
    private readonly Func<string, string, SubjectModel?>? _subjectModelWarm;
    private readonly Action<Action> _pageDispatch;
    private readonly string _bridgeScriptPath;
    private readonly Core.Workbench.ThumbnailCache _thumbnailCache = new();
    private readonly EditPage.EditPreviewService _editPreviews;
    private readonly object _riggedGlbCacheGate = new();
    private RiggedGlbCache? _riggedGlbCache;
    private string? _riggedGlbCacheRoot;

    /// <summary>Construction seam for the session rig cache. The app keeps one instance per active cache
    /// root; tests redirect it without touching the user's LocalAppData tree.</summary>
    internal Func<string, RiggedGlbCache> RiggedGlbCacheFactory = root => new(root);

    internal RiggedGlbCache RiggedGlbCacheAt(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        lock (_riggedGlbCacheGate)
        {
            if (_riggedGlbCache is null
                || !string.Equals(_riggedGlbCacheRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                _riggedGlbCache = RiggedGlbCacheFactory(root);
                _riggedGlbCacheRoot = root;
            }
            return _riggedGlbCache;
        }
    }

    /// <summary>The memo, for tests that pin the hit and the rescan's drop.</summary>
    internal SubjectModelCache SubjectModels => _subjectModels;

    /// <summary>Install-state seam for tests that drive the real speculative-work population route without
    /// running the registry/database load. Production assigns the same three fields in <see cref="LoadAsync"/>.</summary>
    internal void SetLoadedInstallForTest(GameVfs vfs, string gameDir, IReadOnlyList<Character> roster)
    {
        _vfs = vfs;
        _gameDir = gameDir;
        _roster = roster;
    }

    public MainWindowViewModel() : this(startLoad: true, coalesceResolvedRebuilds: true) { }

    /// <summary>The app always constructs through the parameterless form. <paramref name="startLoad"/> is
    /// false only in tests — the game load reaches the registry, the install and the dispatcher.
    /// <paramref name="cacheRootFor"/> redirects the sweep's cache root, which the CONSTRUCTION path
    /// fires before any caller holds the instance — see <see cref="CacheRootFor"/>. Tests may redirect
    /// <paramref name="bridgeScriptPath"/> so they never write into the shared build output.
    /// <paramref name="coalesceResolvedRebuilds"/> is the EXPLICIT app-vs-headless mode for the Edit
    /// board's resolver-burst batching — only the parameterless app ctor sets it, and it is never
    /// inferred from the dispatch delegate or any ambient context; the safe headless default is the
    /// synchronous behavior.</summary>
    internal MainWindowViewModel(bool startLoad, Func<string>? cacheRootFor = null,
        Func<string, string, SubjectModel?>? subjectModelWarm = null, Action<Action>? pageDispatch = null,
        string? bridgeScriptPath = null, bool coalesceResolvedRebuilds = false)
    {
        _subjectModelWarm = subjectModelWarm;
        _pageDispatch = pageDispatch ?? OnUi;
        _bridgeScriptPath = bridgeScriptPath ?? BridgeScriptPath();
        _editPreviews = new EditPage.EditPreviewService(() => _vfs,
            () => string.IsNullOrEmpty(GameDir) ? null : CatalogIndex.LoadCached(GameDir),
            TryDeobfuscateBundle, _thumbnailCache);
        EditPage = new EditPage.EditPageVm(this, _pageDispatch,
            coalesceResolvedRebuilds: coalesceResolvedRebuilds);
        BuildPage = new BuildPage.BuildPageVm(this, _pageDispatch);
        PackageAuthor = _settings.Author;   // remembered across sessions
        PackageIncludesRepairData = _settings.IncludeRepairData;   // the same, for the untitled first project
        RefreshRecent();
        // Settled before the sweep below can read it. The field stays assignable after construction — that
        // is how a test drives the RELOAD's sweep — but the construction path's sweep runs inside this ctor,
        // where no caller has the instance to assign to yet, so it can only be redirected from here.
        if (cacheRootFor is not null) CacheRootFor = cacheRootFor;
        LoadEditPage();
        // A sweep owed from an earlier session: armed on a Save, queued behind a hold, and closed on before
        // any rescan ran. The debt is durable, so the FIRST load of this session is the rescan that honours
        // it — and it is handed off here rather than left for a later reload, because the load starting
        // below is the one that would otherwise rebuild the very caches it is owed.
        //
        // The reload's ordering rules (every declared hold down, the sharing pass cancelled) have nothing to
        // stand for at this line: nothing has been started yet, there is no VFS, no sharing pass and no
        // build. The one reader that follows is that load, and it waits the sweep out — see PendingCachePurge.
        _forceRescanPending = _settings.ForceRescanOwed;
        if (startLoad)
        {
            BeginForceRescanPurge();
            _ = Task.Run(LoadAsync);
        }
    }

    // ---- mod lifecycle ------------------------------------------------

    /// <summary>The mods-library folder — the open/import dialogs' start location.</summary>
    public string LibraryRoot => _settings.ResolvedLibraryRoot;

    /// <summary>Start a fresh mod: always an in-memory untitled project. The folder is minted (and named)
    /// on the first export/save, so New-and-never-export leaves no empty folder behind.</summary>
    public void NewMod()
    {
        ResetWorkspace();
        _projectDocument = AuthoredProjectDocument.New();
        LoadEditPage();
        _loadingIdentityForm = true;
        PackageName = "";
        PackageDescription = "";
        PackageVersion = "1.0";
        PackageAuthor = _settings.Author;
        PackageToggleKey = null;
        PackagePersistToggleKey = false;
        PackageIncludesRepairData = _settings.IncludeRepairData;
        _loadingIdentityForm = false;
        BuildPage.IdentityChanged();
        SelectedStep = "① Pick";
        IsDirty = false;
        ShowHome = false;
        // The session-native page was loaded above. The workbench holder has no session projection to read.
    }

    /// <summary>Open a project folder (or its <c>mod.drlproj</c>) WITHOUT re-exporting. The disk load runs
    /// off the UI thread, the VM assembly back on it. Returns true when a project actually opened.</summary>
    public async Task<bool> OpenModAsync(string folderOrFile)
    {
        if (IsOpeningMod) return false;   // an open is already in flight — ignore a second trigger
        IsOpeningMod = true;
        EditPage.ReportStatus("Opening…");
        try
        {
            AuthoredProjectDocument document;
            try
            {
                var vfs = _vfs;   // captured: the scan can finish mid-open
                document = await Task.Run(() =>
                {
                    if (vfs is null) return AuthoredProjectDocument.Load(folderOrFile);
                    var resolver = new LegacyProjectResolver(NewResolverEnvironment(vfs));
                    return AuthoredProjectDocument.Load(folderOrFile, resolver.ResolvePart,
                        resolver.RosterSlots);
                });
            }
            catch (Exception e)
            {
                // Do NOT navigate steps and do NOT touch the current workspace: the failed open leaves the
                // current (untouched) mod loaded, so a modal notice is the only honest surface. The status
                // line goes back to saying nothing for the same reason — a refusal is an ordinary outcome
                // now, and "Opening…" left standing describes work that is not happening.
                EditPage.ReportStatus("");
                AppLog.Write($"Couldn't open the mod at {folderOrFile}", e);
                bool removed = RemoveDeadRecent(folderOrFile);   // only drops the row when the target is truly gone
                if (MainWindow is { } owner)
                {
                    var body = removed
                        ? $"{OpenFailedBody(e)}\n\nRemoved from Recent mods because the folder no longer exists."
                        : OpenFailedBody(e);
                    await ConfirmWindow.Notice(owner, "Couldn't open the mod", body);
                }
                return false;
            }
            ApplyOpenedProject(document);
            return true;
        }
        finally { IsOpeningMod = false; }
    }

    /// <summary>What a refused open says. A project the app read and would not open answers for itself —
    /// those refusals are written for the modder — but everything else that can come out of a read (a file
    /// held open, a folder that won't be entered, bytes that aren't a project) arrives as a diagnosis of the
    /// read rather than of the mod, so the dialog says what happened and where to look instead.</summary>
    private static string OpenFailedBody(Exception e) => e is InvalidDataException
        ? e.Message
        : "This mod couldn't be read. It may be damaged, or open in another program.";

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
    private void ApplyOpenedProject(AuthoredProjectDocument document)
    {
        // The conversion's only guaranteed record: the notice cell shows it only on the first save, and a
        // clean conversion has nothing to show there at all.
        if (document.OpenedLegacy)
            AppLog.Write($"Opened {document.Authored.Info.Name} from an older project format",
                document.LastMigrationReport is { } report
                    ? AuthoredProjectDocument.ReportForTheLog(report) : "No adjustments were needed.");
        ResetWorkspace(clearSelection: false);
        _projectDocument = document;
        LoadEditPage();
        var authored = document.Authored;
        var info = authored.Info;

        // form
        _loadingIdentityForm = true;
        PackageName = info.Name;
        PackageAuthor = string.IsNullOrWhiteSpace(info.Author) ? _settings.Author : info.Author!;
        PackageDescription = info.Description ?? "";
        PackageVersion = string.IsNullOrWhiteSpace(info.Version) ? "1.0" : info.Version;
        PackageToggleKey = ModKeys.Normalize(info.ToggleKey);
        PackagePersistToggleKey = info.PersistToggleKey;
        PackageIncludesRepairData = info.IncludeRepairData;
        _loadingIdentityForm = false;
        BuildPage.IdentityChanged();
        _pkgCharacter = info.Character ?? "";
        _pkgOutfit = info.Outfit ?? "";

        _modRoot = authored.RootDir;
        ExportOutDir = _modRoot ?? "";
        EnsureWatcher();

        // re-check the saved parts once the roster resolves them (or now, if it already has)
        _pendingSelection = CurrentSelection().Select(entry => new SelectionEntry
            { Character = entry.Character, Outfit = entry.Outfit }).ToList();
        ApplyPendingSelection();

        RememberRecent();
        IsDirty = false;
        int replacementCount = authored.EditDefinitions.Count(
            edit => edit.Kind == EditDefinitionKind.Content);
        EditPage.ReportStatus(replacementCount > 0
            ? $"Opened '{info.Name}'. {replacementCount} edit{(replacementCount == 1 ? "" : "s")}."
            : $"Opened '{info.Name}'.");
        SelectedStep = "② Edit";
        ShowHome = false;   // enter the flow
        // Not awaited: the page is usable while it reads, and every row it draws stands on the project's own
        // names until the model behind it lands.
        _ = WarmSubjectModelsAsync();
        // Not awaited: the pane is usable while it reads, and what it has to say goes to the Edit status
        // line the cards it changed live on.
        // Only when no scan is running: a scan in flight contributes the notice into its own list at
        // finalize, so it can't race or double-fire.
        if (!IsScanning) MaybeNoticeAuthoredAgainst();
    }

    [RelayCommand]
    private async Task OpenRecent(RecentModVm? m)
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
        TrySaveProject();
    }

    /// <summary>Where a failed write to the mod folder sends the modder. The write's own exception names an
    /// OS condition on a path they never typed; what they can act on is the folder the app was told to keep
    /// projects in, so every save refusal carries this and not the diagnosis.</summary>
    internal const string SaveFailedSteer =
        "Check that the projects folder still exists and isn't read-only.";

    /// <summary>The single save-or-mint route (File → Save Mod and the close/leave flush both land here).
    /// Never throws: a failed write returns <c>(false, reason)</c> and leaves the project dirty in memory so
    /// the caller can prompt and the modder can retry.</summary>
    private (bool Ok, string? Reason) TrySaveProject()
    {
        try
        {
            bool migrating = _projectDocument.OpenedLegacy;
            SyncFormToProject();
            if (CurrentProjectRoot is null)
            {
                _projectDocument.RebaseRoot(UniqueDir(_settings.ResolvedLibraryRoot, ModNaming.Slug(ProjectName)));
                PersistProject();
                _modRoot = CurrentProjectRoot; ExportOutDir = CurrentProjectRoot!;
            }
            else { EnsureFolderMatchesName(); PersistProject(); }
            RememberRecent();
            IsDirty = false;
            EditPage.ReportStatus($"Saved to {CurrentProjectRoot}.");
            // The first save after an open that converted is the save that migrates the file, so it is the
            // one that reports what the conversion inferred.
            if (migrating) ShowMigrationReport(_projectDocument.LastMigrationReport);
            return (true, null);
        }
        catch (Exception e)
        {
            AppLog.Write("Couldn't save the mod", e);
            EditPage.ReportStatus($"Couldn't save the mod. {SaveFailedSteer}");
            return (false, SaveFailedSteer);
        }
    }

    /// <summary>Persist the open project. An authored one writes its own intent, install or no install:
    /// there is nothing to re-anchor, because the model itself is what is written.</summary>
    private void PersistProject()
    {
        if (CurrentProjectRoot is null)
            throw new InvalidOperationException("project has no folder");
        SaveDocument(_projectDocument, CurrentProjectRoot);
    }

    /// <summary>Write one document. Every open project is authored intent — a schema-1 manifest converted
    /// at open, or refused there — so a save writes what the session holds, install or no install.</summary>
    private static void SaveDocument(AuthoredProjectDocument document, string path) =>
        document.Save(path);

    private void ShowMigrationReport(MigrationReport? report)
    {
        if (report is null) return;
        string detail = report.Items.Count == 0
            ? "The old project file is kept as mod.drlproj.bak."
            : "The old project file is kept as mod.drlproj.bak. "
                + string.Join(" ", report.Items.Select(item => item.Detail));
        ReplaceNoticeCell(new NoticeMessage(ProjectMigrationNoticeId, "Project updated", detail,
            ProjectScoped: true, Severity: NoticeSeverity.Info));
    }

    /// <summary>Save a copy under a new name and switch to it (the current mod stays on disk untouched).</summary>
    public async Task SaveModAs(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        try
        {
            SyncFormToProject();
            if (CurrentProjectRoot is null)
                _projectDocument.RebaseRoot(UniqueDir(_settings.ResolvedLibraryRoot, ModNaming.Slug(ProjectName)));
            else EnsureFolderMatchesName();

            var dest = UniqueDir(_settings.ResolvedLibraryRoot, ModNaming.Slug(newName));
            PersistProject();   // ensure the source is complete (and current-schema) before copying
            var copy = _projectDocument.CopyTo(dest);
            copy.Session.SetName(newName.Trim());
            SaveDocument(copy, dest);
            await OpenModAsync(dest);   // switch to the copy (clean reload from disk)
            EditPage.ReportStatus($"Saved a copy as '{newName.Trim()}'.");
        }
        catch (Exception e)
        {
            AppLog.Write("Couldn't save a copy", e);
            EditPage.ReportStatus($"Couldn't save a copy. {SaveFailedSteer}");
        }
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

    /// <summary>A mod build or combined Blender composition is still running.</summary>
    public bool IsWorkInFlight => WorkInFlight(BuildingCombinedRig, IsModBuilding);

    /// <summary>The composition behind <see cref="IsWorkInFlight"/> — ANY holder counts, and each is its own
    /// flag because they start and end at different places. Pure so every contributing flag can be exercised
    /// without standing up the window.</summary>
    internal static bool WorkInFlight(bool buildingRig, bool buildingMod) => buildingRig || buildingMod;

    /// <summary>Confirm closing while work runs. The button pair is the siblings' — a verb and a plain way
    /// back — and the body names the work that is actually running.</summary>
    public async Task<bool> ConfirmCloseWithWorkAsync()
    {
        if (MainWindow is not { } owner) return true;   // headless — don't trap the close
        return await ConfirmWindow.Show(owner, "Work in progress",
            CloseWithWorkBody(BuildingCombinedRig, IsModBuilding),
            "Quit anyway", "Keep working", danger: true);
    }

    /// <summary>What quitting does to the work in flight, per state. ORDERED by what quitting COSTS: a mod
    /// build is abandoned mid-run, so it leads and says what is left behind. The Blender composition is
    /// cancelled cleanly, and says so. Pure, so the wording is settled without standing up the window.</summary>
    internal static string CloseWithWorkBody(bool buildingRig, bool buildingMod) => buildingMod
        ? "A mod is still building. Quitting abandons this run."
        : buildingRig ? "The Blender file is still being prepared. Quitting stops it."
        : "The work has already finished.";

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
        return await ConfirmWindow.Show(owner, "Couldn't save the mod",
            $"Couldn't save '{ProjectName}'. {reason}\n\n{leaveVerb} anyway and lose these changes?",
            $"{leaveVerb} anyway", "Cancel", danger: true);
    }

    /// <summary>Move the mod folder to match a renamed mod and rebase the in-memory paths + watchers. A
    /// failed move (folder in use, cross-volume) is non-fatal — the old folder is kept.</summary>
    private void EnsureFolderMatchesName()
    {
        if (CurrentProjectRoot is not { } currentRoot) return;
        if (!CanRenameProjectFolder(BuildingCombinedRig, IsModBuilding,
                !PendingBlenderReturns.IsCompleted)) return;
        var root = currentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var desired = ModNaming.Slug(ProjectName);
        // A dedup form (`desired-2`, `desired-5`, …) is ALREADY the right home. Treating one as a mismatch
        // makes every autosave re-move the folder to a fresh UniqueDir and strand files behind it.
        if (desired.Length == 0 || ModProject.FolderMatchesSlug(Path.GetFileName(root), desired))
            return;

        var old = currentRoot;
        var target = UniqueDir(Path.GetDirectoryName(root)!, desired);
        try
        {
            _watcher?.Dispose(); _watcher = null;   // release the folder before moving it
            _projectDocument.MoveTo(target);
        }
        catch { EnsureWatcher(); return; }          // couldn't move — keep the old folder

        // rebase the in-memory absolute paths to the new location
        _modRoot = CurrentProjectRoot;
        ExportOutDir = CurrentProjectRoot!;
        EnsureWatcher();
    }

    /// <summary>Whether the project folder may be renamed to match the mod name right now. Every holder
    /// captured the old path and is still writing into it — the rig build that an Open-all runs off-thread
    /// included: the rule turns on the writing, not on who asked, and a rename under it strands its glb in
    /// a folder the session sends back to. A rig-cache prewarm does NOT count: it has no project path and
    /// writes only below the derived cache root. The next autosave picks a deferred rename up.
    ///
    /// <para>A Blender return being applied is the same holder from the other end: every path it carries —
    /// the prepared workspace glbs, the ingress artifacts, its staging — was made absolute against the root
    /// it started on, and its own first publish is what fires the autosave that would move that root. A
    /// first open-all send into an unnamed mod is exactly that shape, and the rows after the first would
    /// land nowhere.</para></summary>
    internal static bool CanRenameProjectFolder(bool buildingCombinedRig, bool buildingMod,
        bool applyingBlenderReturn) =>
        !buildingCombinedRig && !buildingMod && !applyingBlenderReturn;

    /// <summary>What a mod the modder never named is called, everywhere the app has to name one.</summary>
    internal const string UntitledMod = "untitled mod";

    private string ProjectName => string.IsNullOrWhiteSpace(PackageName) ? UntitledMod : PackageName.Trim();

    /// <summary>Tear down the current mod's transient UI state, between New/Open.</summary>
    private void ResetWorkspace(bool clearSelection = true)
    {
        // A prewarm belongs to the selection/document that scheduled it. It has no authored output, but it
        // must not keep reading for a project the app has replaced.
        CancelRiggedGlbPrewarm();
        _watcher?.Dispose(); _watcher = null;
        foreach (var transport in _pictureTransports) transport.Dispose();
        _pictureTransports.Clear();
        ExportOutDir = ""; _modRoot = null;
        _pkgCharacter = ""; _pkgOutfit = "";
        if (clearSelection) ClearSelection();
        _pendingSelection = null;
        ResetProjectNoticeLifecycle();
        // A landed launch ✓ describes the sitting being left — it goes out with the workspace.
        LaunchStatus = StatusFacet.None;
    }

    /// <summary>Clear every subject checkbox + ✎ marker on the Pick tree. Silent — teardown never runs the
    /// add/remove path (there is no project left to mutate).</summary>
    private void ClearSelection()
    {
        if (ApplySubjectLedger(new AuthoredProject(), AllPickRows)) RefreshTabHeaders();
    }

    /// <summary>Reflect the saved selection ledger onto the Pick tree once the roster carries the
    /// outfits. UI thread.</summary>
    private void ApplyPendingSelection()
    {
        if (_pendingSelection is null) return;
        SyncSubjectsFromLedger();
        // During a scan this first diff clears any previous project's checks immediately; phase 3 applies
        // it again after replacing placeholder outfit rows with the confirmed roster.
        if (!IsScanning) _pendingSelection = null;
    }

    /// <summary>Reflect the ledger onto the Pick tree. Sets each checkbox WITHOUT firing the user-toggle
    /// add/remove path.</summary>
    private void SyncSubjectsFromLedger()
    {
        var project = _projectDocument.Session.Snapshot();
        if (ApplySubjectLedger(project, AllPickRows)) RefreshTabHeaders();
    }

    private sealed class SubjectIdentityComparer : IEqualityComparer<(string Character, string Outfit)>
    {
        internal static readonly SubjectIdentityComparer Instance = new();

        public bool Equals((string Character, string Outfit) x, (string Character, string Outfit) y) =>
            string.Equals(x.Character, y.Character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Outfit, y.Outfit, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Character, string Outfit) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Character),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Outfit));
    }

    /// <summary>Apply one detached ledger read to one roster walk. Returns whether any bound state moved,
    /// so an unchanged restore raises no aggregate-row or tab-header notifications.</summary>
    internal static bool ApplySubjectLedger(AuthoredProject project, IEnumerable<CharacterVm> rows)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(rows);
        IEnumerable<SelectionEntry> selection = project.WorkspaceIndex?.Selection
            ?? project.EditDefinitions.Select(edit => new SelectionEntry
                { Character = edit.Target.Subject, Outfit = edit.Target.Outfit });
        var selected = selection.Select(entry => (entry.Character, entry.Outfit))
            .ToHashSet(SubjectIdentityComparer.Instance);
        var edited = project.EditDefinitions
            .Where(edit => edit.Kind == EditDefinitionKind.Content)
            .Select(edit => (edit.Target.Subject, edit.Target.Outfit))
            .ToHashSet(SubjectIdentityComparer.Instance);

        bool anyChanged = false;
        foreach (var character in rows)
        {
            bool characterChanged = false;
            foreach (var outfit in character.Outfits)
            {
                var identity = (character.Name, outfit.Stem);
                bool inMod = selected.Contains(identity);
                bool hasEdits = inMod && edited.Contains(identity);
                if (outfit.IsInMod != inMod)
                {
                    outfit.SetInModSilently(inMod);
                    characterChanged = true;
                }
                if (outfit.HasEdits != hasEdits)
                {
                    outfit.HasEdits = hasEdits;
                    characterChanged = true;
                }
            }
            if (!characterChanged) continue;
            character.RefreshSubjectState();
            anyChanged = true;
        }
        return anyChanged;
    }

    // ---- subject add / remove (the Pick checkbox drives the session ledger) -------------------------

    private static int SubjectEditCount(AuthoredProject project, string character, string outfit) =>
        project.EditDefinitions.Count(edit =>
            string.Equals(edit.Target.Subject, character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(edit.Target.Outfit, outfit, StringComparison.OrdinalIgnoreCase));

    private void OnSubjectToggled(CharacterVm character, OutfitVm outfit)
    {
        if (outfit.IsInMod) AddSubject(character, outfit);
        else _ = UncheckSubjectAsync(character, outfit);
    }

    internal void OnCharacterToggled(CharacterVm character, bool addAll)
    {
        if (addAll) AddCharacter(character);
        else _ = RemoveCharacterAsync(character);
    }

    private void AddCharacter(CharacterVm character)
    {
        var uncheckedOutfits = character.Outfits.Where(outfit => !outfit.IsInMod).ToList();
        if (uncheckedOutfits.Count == 0) { character.RefreshSubjectState(); return; }

        var session = _projectDocument.Session;
        var project = session.Snapshot();
        var index = project.WorkspaceIndex ?? new AuthoredWorkspaceIndex();
        var selected = index.Selection.Select(entry => (entry.Character, entry.Outfit))
            .ToHashSet(SubjectIdentityComparer.Instance);
        var added = uncheckedOutfits.Where(outfit => selected.Add((character.Name, outfit.Stem))).ToList();
        foreach (var outfit in added)
            index.Selection.Add(new SelectionEntry { Character = character.Name, Outfit = outfit.Stem });
        if (added.Count > 0)
        {
            session.Compound(change => change.SetWorkspaceIndex(index));
            _ = WarmSubjectModelsAsync();
        }

        foreach (var outfit in uncheckedOutfits)
        {
            outfit.SetInModSilently(true);
            outfit.HasEdits = false;
        }
        character.RefreshSubjectState();
        RefreshTabHeaders();
        AutoNameFromSubject(character.Name);
        EnsureModRoot();
    }

    private async Task RemoveCharacterAsync(CharacterVm character)
    {
        var session = _projectDocument.Session;
        var inMod = character.Outfits.Where(outfit => outfit.IsInMod).ToList();
        if (inMod.Count == 0) { character.RefreshSubjectState(); return; }

        var snapshot = session.Snapshot();
        int edits = inMod.Sum(outfit => SubjectEditCount(snapshot, character.Name, outfit.Stem));
        if (edits > 0)
        {
            if (MainWindow is not { } owner) { character.RefreshSubjectState(); return; }
            string title = $"Remove {character.DisplayName}'s "
                + $"{(inMod.Count == 1 ? "outfit" : $"{inMod.Count} outfits")}?";
            string body = $"Their {edits} edit{(edits == 1 ? " goes" : "s go")} with them. "
                + "Their files stay in the mod folder.\n\nThis cannot be undone.";
            if (!await ConfirmWindow.Show(owner, title, body, "Remove all", "Cancel", danger: true))
            {
                character.RefreshSubjectState();
                return;
            }
        }

        session.Compound(change =>
        {
            foreach (var outfit in inMod)
                change.ForgetSubject(character.Name, outfit.Stem);
        });
        SyncSubjectsFromLedger();
        TryStartRiggedGlbPrewarm();
        EditPage.ReportStatus($"Removed {inMod.Count} outfit{(inMod.Count == 1 ? "" : "s")} from the mod.");
    }

    internal void AddSubject(CharacterVm character, OutfitVm outfit)
    {
        var session = _projectDocument.Session;
        var project = session.Snapshot();
        var index = project.WorkspaceIndex ?? new AuthoredWorkspaceIndex();
        if (!index.Selection.Any(entry =>
                string.Equals(entry.Character, character.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.Outfit, outfit.Stem, StringComparison.OrdinalIgnoreCase)))
        {
            index.Selection.Add(new SelectionEntry { Character = character.Name, Outfit = outfit.Stem });
            session.Compound(change => change.SetWorkspaceIndex(index));
            _ = WarmSubjectModelsAsync();
        }

        outfit.HasEdits = false;
        character.RefreshSubjectState();
        RefreshTabHeaders();
        AutoNameFromSubject(character.Name);
        EnsureModRoot();
    }

    private async Task UncheckSubjectAsync(CharacterVm character, OutfitVm outfit)
    {
        bool removed = await RemoveSubjectAsync(character.Name, outfit.Stem, outfit.Model.MeshPrefix,
            SubjectLabel(character.Name, outfit.Model));
        if (!removed) { outfit.SetInModSilently(true); character.RefreshSubjectState(); }
    }

    /// <summary>One subject's remove-confirm body. The Pick uncheck and the ② page's Remove verb ask the
    /// same question of the same thing, so they share the sentence.</summary>
    internal static string RemoveSubjectConfirmBody(int edits) =>
        $"Its {edits} edit{(edits == 1 ? " goes" : "s go")} with it. Its files stay in the mod folder."
        + "\n\nThis cannot be undone.";

    internal async Task<bool> RemoveSubjectAsync(string character, string stem, string meshPrefix, string label)
    {
        var session = _projectDocument.Session;
        int edits = SubjectEditCount(session.Snapshot(), character, stem);
        if (edits > 0)
        {
            if (MainWindow is not { } owner) return false;
            if (!await ConfirmWindow.Show(owner, $"Remove {label}?", RemoveSubjectConfirmBody(edits),
                    "Remove", "Cancel", danger: true))
                return false;
        }

        RemoveSubjectNoConfirm(session, character, stem);
        EditPage.ReportStatus($"Removed {label} from the mod.");
        return true;
    }

    private void RemoveSubjectNoConfirm(AuthoredEditSession session, string character, string stem)
    {
        session.ForgetSubject(character, stem);
        SyncSubjectsFromLedger();
        // Replace the selection snapshot the speculative job owns. Already-published game-side entries are
        // harmless; no further work is spent on a subject the mod no longer carries.
        TryStartRiggedGlbPrewarm();
    }

    public void OpenSubjectInEdit(object? row)
    {
        (string Character, string Stem)? opened;
        switch (row)
        {
            case OutfitVm outfit:
                var owner = AllPickRows.FirstOrDefault(character => character.Outfits.Contains(outfit));
                if (owner is not null && !outfit.IsInMod) outfit.SetInModSilently(true);
                if (owner is not null && !ProjectHasSubject(owner.Name, outfit.Stem)) AddSubject(owner, outfit);
                opened = owner is null ? null : (owner.Name, outfit.Stem);
                break;
            case CharacterVm { IsSingleOutfit: true } character:
                var only = character.Outfits[0];
                if (!only.IsInMod) { only.SetInModSilently(true); character.RefreshSubjectState(); }
                if (!ProjectHasSubject(character.Name, only.Stem)) AddSubject(character, only);
                opened = (character.Name, only.Stem);
                break;
            default:
                return;
        }

        SelectedStep = "② Edit";
        if (opened is { } subject) EditPage.SelectSubject(subject.Character, subject.Stem);
    }

    private string SubjectLabel(string character, Outfit outfit) => _friendly.Subject(character, outfit);


    /// <summary>(Re)build the key→label resolver from a name-enriched roster. UI thread; safe to repeat.</summary>
    private void RebuildFriendlyNames(IReadOnlyList<Character> enrichedRoster) =>
        _friendly = FriendlyNames.FromRoster(enrichedRoster);

    /// <summary>The identity form reduced to what the project serializes: blanks take defaults, the rest
    /// trimmed, and the key normalized.</summary>
    private (string Name, string Version, string? Author, string? Description, string? ToggleKey,
        bool PersistToggleKey, bool IncludeRepairData) IdentityForm() => (
        ProjectName,
        string.IsNullOrWhiteSpace(PackageVersion) ? "1.0" : PackageVersion.Trim(),
        string.IsNullOrWhiteSpace(PackageAuthor) ? null : PackageAuthor.Trim(),
        string.IsNullOrWhiteSpace(PackageDescription) ? null : PackageDescription.Trim(),
        ModKeys.Normalize(PackageToggleKey),
        PackagePersistToggleKey,
        PackageIncludesRepairData);

    /// <summary>Copy the mod-identity form into the project's own <see cref="ProjectInfo"/>.</summary>
    private void SyncFormToProject()
    {
        var form = IdentityForm();
        string? character = string.IsNullOrWhiteSpace(_pkgCharacter) ? null : _pkgCharacter;
        string? outfit = string.IsNullOrWhiteSpace(_pkgOutfit) ? null : _pkgOutfit;
        var info = _projectDocument.Session.Snapshot().Info;
        if (string.Equals(info.Name, form.Name, StringComparison.Ordinal)
            && string.Equals(info.Version, form.Version, StringComparison.Ordinal)
            && string.Equals(info.Author, form.Author, StringComparison.Ordinal)
            && string.Equals(info.Description, form.Description, StringComparison.Ordinal)
            && string.Equals(info.ToggleKey, form.ToggleKey, StringComparison.Ordinal)
            && info.PersistToggleKey == form.PersistToggleKey
            && info.IncludeRepairData == form.IncludeRepairData
            && string.Equals(info.Character, character, StringComparison.Ordinal)
            && string.Equals(info.Outfit, outfit, StringComparison.Ordinal))
            return;
        EditProject(session => session.SetIdentity(form.Name, form.Version, form.Author,
            form.Description, form.ToggleKey, form.IncludeRepairData, character, outfit,
            form.PersistToggleKey));
    }

    /// <summary>Autosave after a meaningful step, once a folder exists. A failure must NOT pass silently:
    /// IsDirty is cleared only on success, so a failed write leaves the project dirty and says so. Returns
    /// the failure message, else null, for a caller with a surface of its own.</summary>
    private string? AutoSave()
    {
        // RootDir null = folder not minted yet: nothing to autosave INTO, so skip and leave IsDirty true.
        // The close/leave flush is the guaranteed terminal save and mints the folder then.
        if (CurrentProjectRoot is null) return null;
        try
        {
            SyncFormToProject(); EnsureFolderMatchesName(); PersistProject(); RememberRecent();
            ProjectSaves++;
            IsDirty = false;
        }
        catch (Exception e)
        {
            AppLog.Write("Autosave failed", e);
            var line = "Couldn't save automatically. Your changes are unsaved. "
                + "Use File · Save mod to try again.";
            EditPage.ReportStatus(line);
            return line;
        }
        return null;
    }

    private void RememberRecent()
    {
        if (CurrentProjectRoot is not { } root) return;
        _settings.AddRecent(root, CurrentProjectInfo.Name);
        SaveSettings();
        RefreshRecent();
    }

    private void RefreshRecent()
    {
        // the rows are being replaced wholesale, so their native bitmaps go with them
        foreach (var row in RecentMods) row.DisposeThumb();
        RecentMods.Clear();
        foreach (var m in _settings.RecentMods) RecentMods.Add(new RecentModVm(m));
        OnPropertyChanged(nameof(HasRecentMods));
        FillRecentThumbs();
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
    partial void OnPackageIncludesRepairDataChanged(bool value)
        => OnIdentityEdited();
    partial void OnPackageToggleKeyChanged(string? value)
        => OnIdentityEdited();
    partial void OnPackagePersistToggleKeyChanged(bool value)
        => OnIdentityEdited();

    /// <summary>An identity field changed: re-run the naming preview, mark dirty and autosave through the
    /// one route every Build-pane edit uses; a failed write reaches the pane's footer.</summary>
    private void OnIdentityEdited()
    {
        if (_loadingIdentityForm) return;   // populating the form from a project is not an edit
        MarkDirty();
        BuildPage.IdentityChanged();
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
                _identitySaveTimer!.Stop();
                FlushIdentitySave();
            };
        }
        _identitySaveTimer.Stop();
        _identitySaveTimer.Start();
    }

    /// <summary>What one coalesced identity edit lands: the author default FIRST — so it lands even for a
    /// project with no folder yet, where the save is a no-op — then the project itself.</summary>
    private void FlushIdentitySave()
    {
        TryAutoSaveProject();
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
        else if (value == "② Edit") EditPage.Enter();
        else if (value == "③ Build") BuildPage.Enter();
    }

    private void EnsureWatcher()
    {
        if (_modRoot is null) return;
        if (_watcher is null)
        {
            var w = new BlenderSendWatcher(_modRoot, includeSubdirectories: true);
            // The mod this watcher belongs to, captured HERE. A send lands physically inside its own mod's
            // folder and this watcher is rooted there, so the document open when the watcher was armed is
            // the document that send addresses — for as long as it stays open. Reading the field later,
            // when the queued return finally starts, reads whichever mod is open THEN, which is how a
            // return from one mod came to be minted into another.
            var owner = _projectDocument;
            // The return is HANDED OVER rather than applied here: everything it has to read — the glb
            // parse, the per-part re-exports and normalization, the new-target install reads — runs on a
            // worker, and only the authored transaction hops onto the window's thread. The watcher raises
            // this synchronously on its own thread (the scan's contract depends on that), so the handover
            // must not block: it appends to the return queue and returns.
            w.EditReceived += e => { StampSend(); QueueBlenderReturn(owner, e); };
            // A transport failure reports only; it never tries to infer author state from a path.
            w.Error += (glb, failure) => { StampSend(); _pageDispatch(() => OnEditFailed(glb, failure)); };
            _watcher = w;   // assigned first: the scan's handlers re-enter here and must find it armed
            // A send that landed while the app was closed or another mod was open has no watcher event.
            // Taking it HERE puts every one of them on the return queue before this line returns, in the
            // order they were written; what makes them land BEFORE a later action can compose around a
            // stale snapshot is the wait that action takes on PendingBlenderReturns.
            w.ScanExisting();
        }
    }

    /// <summary>Run on the UI thread, inline when the caller is already there — which is what a caller
    /// already holding that thread needs, and what keeps a report raised from it in order with the rest.</summary>
    private static void OnUi(Action work)
    {
        if (Dispatcher.UIThread.CheckAccess()) work();
        else Dispatcher.UIThread.Post(work);
    }

    /// <summary>When a Blender send was last taken, in UTC ticks. Stamped on the watcher's own thread as
    /// the send is read back, so <see cref="WatchBlenderExit"/> compares against arrival, not dispatcher
    /// order.</summary>
    private long _lastSendTicksUtc;

    private void StampSend() => Interlocked.Exchange(ref _lastSendTicksUtc, DateTime.UtcNow.Ticks);

    /// <summary>How long after Blender exits the "nothing sent" line waits — a send written just before
    /// exit still has to travel the watcher's file events.</summary>
    private static readonly TimeSpan BlenderExitSettle = TimeSpan.FromSeconds(2);

    /// <summary>Say so when a Blender instance launched by this app closes having sent nothing back.
    /// Nothing waits on the handle, and the captured session prevents a late exit from reporting onto a
    /// project that replaced it.</summary>
    private void WatchBlenderExit(Process proc, IProgress<string> status)
    {
        var launchedTicks = DateTime.UtcNow.Ticks;
        var documentAtLaunch = _projectDocument;
        try { proc.EnableRaisingEvents = true; }
        // No handle to watch (already reaped, or the OS refused one): the send-back path is unaffected.
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        { proc.Dispose(); return; }
        proc.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            proc.Dispose();
            DispatcherTimer.RunOnce(() =>
            {
                if (!ReferenceEquals(documentAtLaunch, _projectDocument)) return;
                if (Interlocked.Read(ref _lastSendTicksUtc) <= launchedTicks)
                    status.Report("Blender closed. Nothing was sent back.");
            }, BlenderExitSettle);
        });
    }

    /// <summary>What a return says when it carries no app-minted address at all: it belongs to the removed
    /// workbench protocol, and a destination cannot be inferred from a filename.</summary>
    internal const string BlenderReturnUnaddressed =
        "The file sent back from Blender doesn't say which edit it belongs to. "
        + "Nothing was changed. Use Open in Blender to start again.";

    /// <summary>…and when it carries an address this build no longer understands.</summary>
    internal const string BlenderReturnFromAnOlderLab =
        "The file sent back from Blender was made by an older version of Doll Remolding Lab. "
        + "Nothing was changed. Open this part in Blender again, then send it back.";

    /// <summary>What the ② page says while a return is being read and applied — the parse, every re-export,
    /// every install read — which for a whole outfit is the stretch the page otherwise spends saying
    /// nothing at all. Replaced by the transaction's own line the moment the return answers.</summary>
    internal const string BlenderReturnApplying = "Applying the file sent back from Blender…";

    /// <summary>How long a return is read before the page says it is being read. A send-all's read is
    /// seconds and is owed the line; a lone part's is now a blink, and a line that appears and is replaced
    /// inside a second is a flicker where a report should be — the text guide gives a transient that short
    /// a budget of one or two words, if anything.</summary>
    private static readonly TimeSpan BlenderReturnApplyingAfter = TimeSpan.FromMilliseconds(400);

    /// <summary>…and when reading it fell over for a reason the return itself has no word for. The failure
    /// is a diagnosis of the read, not of the modder's work, so the line says what happened to their edit and
    /// what to do with it instead.</summary>
    internal const string BlenderReturnUnreadable =
        "Couldn't apply the file sent back from Blender. Nothing was changed. "
        + "Open it in Blender again, then send it back.";

    /// <summary>What a return says when it landed nothing — every part came back exactly as it was handed
    /// out, which is what a send-all does with the parts the modder never touched.</summary>
    internal const string BlenderReturnNoChanges = "Blender sent back no changes.";

    /// <summary>What a return says when the mod it belongs to was closed while it waited. Its intent is left
    /// alone rather than landed on whatever is open now: a part route addresses by subject and outfit,
    /// and those resolve just as well in the wrong mod.</summary>
    internal static string BlenderReturnModClosed(string mod) =>
        $"Couldn't apply the file sent back from Blender: {mod} is no longer open. Nothing was changed.";

    private readonly object _blenderReturnGate = new();

    /// <summary>The returns this session has taken, applied ONE AFTER ANOTHER. A send that lands while
    /// another is still being read is queued, never dropped and never interleaved: two returns can address
    /// the same edit, and two transactions half-applied against each other is not a state the model has a
    /// word for.
    ///
    /// <para>The chain is continued on <see cref="TaskScheduler.Default"/>, so every link starts on the
    /// pool with no synchronization context — which is the whole guarantee that no worker on this path can
    /// come back onto the window's thread except through the explicit hop
    /// <see cref="OnPageThreadAsync"/> makes, and never while holding anything.</para></summary>
    private Task _blenderReturns = Task.CompletedTask;

    /// <summary>What is still owed by returns already taken. Awaited by the Blender open, which composes an
    /// outbound session from a snapshot a pending return would make stale, and by tests that pin the round
    /// trip. Never faults: <see cref="ApplyBlenderReturnAsync"/> reports rather than throws.</summary>
    internal Task PendingBlenderReturns { get { lock (_blenderReturnGate) return _blenderReturns; } }

    /// <summary>Take one Blender return, for the mod <paramref name="document"/> names. Returns as soon as
    /// the work is queued — the caller is the watcher's own thread, and the scan calls it once per
    /// unconsumed send.
    ///
    /// <para>The document travels WITH the return rather than being read when the return's turn comes: two
    /// sends can sit in this queue while the modder opens another mod, and the second one's turn arrives in
    /// a session that has nothing to do with it.</para></summary>
    internal Task QueueBlenderReturn(AuthoredProjectDocument document, IncomingEdit edit)
    {
        lock (_blenderReturnGate)
            return _blenderReturns = _blenderReturns
                .ContinueWith(_ => ApplyBlenderReturnAsync(document, edit), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default)
                .Unwrap();
    }

    /// <summary>One return, end to end: read it whole on a worker, then commit it in one hop on the
    /// window's thread. Both halves run against <paramref name="document"/> — the mod that owned the watcher
    /// the send arrived on — and the commit refuses outright if that mod is no longer the open one, the same
    /// rule <see cref="WatchBlenderExit"/> applies to a late exit.</summary>
    private async Task ApplyBlenderReturnAsync(AuthoredProjectDocument document, IncomingEdit edit)
    {
        PreparedBlenderReturnPlan? plan = null;
        // Whether this return has had its say. Written and read on the PAGE's thread and nowhere else,
        // which is what makes the working line and the return's own line two ordered actions on one queue
        // rather than a race between a timer and a commit.
        bool answered = false;
        using var reading = new CancellationTokenSource();
        SayApplyingIfStillReading(document, () => answered, reading.Token);
        // The rows this return is about to change, marked working for as long as it runs — the same gate
        // the subject's own Open holds, so the ◌ and the waiting buttons mean one thing on this page
        // whether the work was started by a click here or by a send landing. Taken before the read, which
        // is the long half; the addresses come from the small sidecar written beside the glb, not from the
        // glb itself.
        IDisposable? working = null;
        var owners = BlenderReturnSubjects(edit, document.Session);
        await OnPageThreadAsync(() =>
        {
            if (ReferenceEquals(document, _projectDocument)) working = EditPage.HoldSubjects(owners);
        });
        try
        {
            plan = await PrepareBlenderReturnAsync(edit, document.Session);
            var settled = plan;
            await OnPageThreadAsync(() =>
            {
                answered = true;
                if (!ReferenceEquals(document, _projectDocument)) { RefuseClosedModReturn(document, edit); return; }
                if (settled.Refusal is { } refusal) { EditPage.ReportStatus(refusal); return; }
                CommitBlenderReturn(edit, document.Session, settled);
            });
        }
        catch (Exception e)
        {
            AppLog.Write("Couldn't apply the file sent back from Blender", e);
            // Nothing above is allowed to break the queue: the next return still has to land. A refusal
            // was written for the modder and is shown; everything else reports the read and stays in the log.
            await OnPageThreadAsync(() =>
            {
                answered = true;
                if (!ReferenceEquals(document, _projectDocument)) { RefuseClosedModReturn(document, edit); return; }
                EditPage.ReportStatus(e is AuthoredRefusalException
                    ? $"Couldn't apply the file sent back from Blender: {Reason(e)} Nothing was changed."
                    : BlenderReturnUnreadable);
            });
        }
        finally
        {
            reading.Cancel();
            // Given back on the page's thread, where it was taken — and after the commit, so the rows stop
            // saying they are working at the same moment the return's own line lands.
            if (working is { } held) await OnPageThreadAsync(held.Dispose);
            if (plan?.StagingRoot is { } staging) await Task.Run(() => DeleteBlenderStaging(staging));
        }
    }

    /// <summary>Which subject rows one return will change. A part route carries its own subject; an
    /// exact row carries the edit it was opened on, and the session says whose part that edit is. Read off
    /// the return's address sidecar, which is a small JSON file beside the glb — the glb itself is opened
    /// by the preparation, on a worker, and is far too expensive to touch for this.
    ///
    /// <para>An unreadable or unaddressed return names nothing, and marks nothing: that return is about to
    /// be refused without changing a row.</para></summary>
    private static IReadOnlyList<(string Subject, string Outfit)> BlenderReturnSubjects(IncomingEdit edit,
        AuthoredEditSession session)
    {
        IReadOnlyList<BlenderSessionTarget> targets;
        try { targets = BlenderBridge.ReadReturnTargets(edit.GlbPath); }
        catch (Exception e) when (e is not OutOfMemoryException)
        { return Array.Empty<(string, string)>(); }
        if (targets.Count == 0) return Array.Empty<(string, string)>();

        // The session is asked only where a target cannot say for itself. Every row of an open-all carries
        // its own subject, and reading the project here would put this behind whatever else is holding the
        // session — which is exactly the stretch the ◌ is owed for.
        IReadOnlyList<EditDefinition> edits = Array.Empty<EditDefinition>();
        if (targets.Any(target => target.Subject is not { Length: > 0 }
                || target.Outfit is not { Length: > 0 }))
            try { edits = session.Snapshot().EditDefinitions; }
            catch { edits = Array.Empty<EditDefinition>(); }
        return BlenderReturnSubjects(targets, edits);
    }

    /// <inheritdoc cref="BlenderReturnSubjects(IncomingEdit, AuthoredEditSession)"/>
    /// <summary>The mapping itself, over addresses already read: which subject each target belongs to, once
    /// each, in the order the return names them.</summary>
    internal static IReadOnlyList<(string Subject, string Outfit)> BlenderReturnSubjects(
        IReadOnlyList<BlenderSessionTarget> targets, IReadOnlyList<EditDefinition> edits)
    {
        var owners = new List<(string Subject, string Outfit)>();
        // The pair itself, upper-cased: one string of the two joined would let two different pairs collide
        // on where the first name ends.
        var seen = new HashSet<(string, string)>();
        foreach (var target in targets)
        {
            (string Subject, string Outfit)? owner =
                target.Subject is { Length: > 0 } subject && target.Outfit is { Length: > 0 } outfit
                    ? (subject, outfit)
                    : edits.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, target.EditDefinitionId, StringComparison.Ordinal))
                        ?.Target is { } part ? (part.Subject, part.Outfit) : null;
            if (owner is { } found
                && seen.Add((found.Subject.ToUpperInvariant(), found.Outfit.ToUpperInvariant())))
                owners.Add(found);
        }
        return owners;
    }

    /// <summary>Say the ② page is reading a return — but only once it has been reading a while, and only on
    /// the page that return belongs to.
    ///
    /// <para>Both conditions are what the line was missing. Said at the start it said it on whatever mod was
    /// open, so a return whose own mod had been closed announced itself on a stranger's page and then
    /// refused there. And a return that lands in a blink — which is most of them now that untouched parts
    /// are skipped — showed the line only long enough to flicker.</para>
    ///
    /// <para><paramref name="answered"/> is the return's own report, read on the page's thread where the
    /// return sets it: whichever of the two reaches that thread first, this line can never land on top of
    /// the answer.</para></summary>
    private void SayApplyingIfStillReading(AuthoredProjectDocument document, Func<bool> answered,
        CancellationToken done)
    {
        _ = Task.Delay(BlenderReturnApplyingAfter, done).ContinueWith(_ => _pageDispatch(() =>
        {
            if (answered() || !ReferenceEquals(document, _projectDocument)) return;
            EditPage.ReportStatus(BlenderReturnApplying);
        }), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    /// <summary>Give a return back to the mod that owns it, having landed nothing. The send's own sidecar —
    /// which the watcher consumed the moment it read the file — is written again beside the return glb, so
    /// the mod's next open finds an unhandled send exactly where Blender left one and takes it through the
    /// ordinary scan. The glb was never touched, so what is put back is the whole send.
    ///
    /// <para>What keeps this from looping is not that nobody is watching — reopening the SAME mod re-ingests
    /// the restored send at once, which is the whole point of putting it back, and it lands correctly there.
    /// It is that the send can only ever be ingested by a watcher rooted on ITS OWN mod: a watcher whose
    /// root merely contains that mod's folder skips a sidecar sitting under a folder with a project of its
    /// own (<see cref="BlenderSendWatcher"/>), so a restored send is never taken for the wrong
    /// document.</para>
    ///
    /// <para>Best-effort in full: a send that cannot be put back has still landed nothing, and the raw
    /// return glb is where Blender left it either way.</para></summary>
    private void RefuseClosedModReturn(AuthoredProjectDocument document, IncomingEdit edit)
    {
        try { BlenderBridge.WriteSendSidecar(edit.GlbPath, edit.HiddenParts, edit.EditIds); }
        catch (Exception e) when (e is not OutOfMemoryException) { /* best-effort */ }
        EditPage.ReportStatus(BlenderReturnModClosed(ClosedModName(document)));
    }

    /// <summary>What to call a mod that is no longer open: the name the modder gave it, or the same
    /// <see cref="UntitledMod"/> the rest of the app calls an unnamed one. The folder it happens to live in
    /// is not one of the answers — the modder never wrote that name, and the page calling one mod by its
    /// title and the next by a folder slug is two names for one thing.</summary>
    private static string ClosedModName(AuthoredProjectDocument document) =>
        ClosedModName(document.Session);

    /// <inheritdoc cref="ClosedModName(AuthoredProjectDocument)"/>
    /// <summary>The same, for a caller holding the session alone — the image editor's transport, which is
    /// addressed to a session rather than to a document.</summary>
    private static string ClosedModName(AuthoredEditSession session)
    {
        string named = session.Snapshot().Info.Name.Trim();
        return named.Length > 0 ? named : UntitledMod;
    }

    /// <summary>Run one action on the page's own thread and hand back a task that completes when it has.
    /// The action's own failure travels on the task rather than out of the dispatcher, where the app has
    /// no one to catch it.</summary>
    private Task OnPageThreadAsync(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pageDispatch(() =>
        {
            try { work(); done.TrySetResult(); }
            catch (Exception e) { done.TrySetException(e); }
        });
        return done.Task;
    }

    /// <summary>A transport failed before exact-slot intake could parse it. No author state is touched. A
    /// refusal was written for the modder and is shown as it is; everything else is a diagnosis of the read
    /// — a file handle the watcher gave up on names the same file twice and tells the modder nothing to do
    /// — so the line names the file once and says what to do with it, and the reason goes to the log.</summary>
    private void OnEditFailed(string glbPath, Exception failure)
    {
        AppLog.Write($"Couldn't read {Path.GetFileName(glbPath)} from Blender", failure);
        EditPage.ReportStatus(failure is AuthoredRefusalException
            ? $"Couldn't read {Path.GetFileName(glbPath)} from Blender: {Reason(failure)} Nothing was changed."
            : $"Couldn't read {Path.GetFileName(glbPath)} from Blender. Nothing was changed. Send it again.");
    }
    /// <summary>The app's main window, for parenting modal confirmations from a command.</summary>
    private static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static string BridgeScriptPath() =>
        Path.Combine(AppContext.BaseDirectory, "blender", "remold_bridge.py");

    /// <summary>Re-read the game from scratch: drop the forward view and roster, then rerun
    /// <see cref="LoadAsync"/>. Waits on every hold in <see cref="RescanMustWait"/>. QUEUED rather than
    /// refused: a request that vanished reads as a dead button.</summary>
    [RelayCommand]
    private void ReloadRoster()
    {
        // Speculation yields before the hold is tested. Its worker drains through RiggedGlbPrewarmRunning;
        // when that count reaches zero it calls RunQueuedRescan and this request continues.
        CancelRiggedGlbPrewarm();
        if (RescanMustWait)
        {
            _rescanAfterScan = true;
            ShowQueuedRescanNotice(RescanQueuedNotice);
            return;
        }
        GameRescanOffered = false;
        Characters.Clear();
        Enemies.Clear();
        Weapons.Clear();
        _allCharacters = new();
        _allEnemies = new();
        _allWeapons = new();
        _vfs = null;
        _ = BuildPage.ReplanAsync();
        _subjectModels.Clear();   // a memoized model describes the forward view being dropped here
        ClearEditPageReads();     // …as does everything the ② Edit page derived from one
        _sharingCts?.Cancel();
        _sharingTask = null;
        // A force rescan's deletions run HERE and nowhere else, and AFTER the sharing pass above is
        // cancelled: that pass writes the sharing cache from a background thread under no hold at all, so a
        // sweep ahead of the cancel is a sweep the pass can undo — it would re-write the very file the
        // modder asked to clear, seeding the next measurement from pre-sweep rows.
        //
        // Every hold the app declares in RescanMustWait has let go by this line.
        // The DELETIONS themselves leave this thread (~1s per 10k cache files); what stays here is the
        // decision and the order. See BeginForceRescanPurge for the guarantee that ties the two back
        // together: the load started below waits the sweep out before it reads or writes a single cache.
        BeginForceRescanPurge();
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
            ReplaceNoticeCell(new NoticeMessage("game.invalid-folder", "Not a GF2 install",
                problem ?? "The folder isn't a GF2 install.", Severity: NoticeSeverity.Error));
            return;
        }
        _settings.GamePath = resolved;
        SaveSettings();
        CancelRiggedGlbPrewarm();
        _gameDir = resolved;
        RaiseModsFolderGates();   // the game half of the Launch gate is known now, scan or no scan
        // An in-flight load captured its own game dir at start and won't pick this one up, so queue a
        // rescan for when it lands rather than silently no-op until the user rescans.
        //
        // Its own title and leading sentence — the folder change is what the modder just did, and the notice
        // answers that — with the SHARED queued detail after it: this is the same wait every other route
        // queues, so a sweep owed while it stands has to be named here too.
        if (IsScanning)
        {
            _rescanAfterScan = true;
            ShowQueuedRescanNotice(GameDirChangedNotice, GameDirChangedLead);
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
    /// a prewarm, or a session composition for Blender — would fail on vanished state.</summary>
    internal bool RescanMustWait => IsScanning || BuildingCombinedRig || IsModBuilding
        || RiggedGlbPrewarmRunning;

    /// <summary>The notice while a queued rescan waits on whatever is holding the roster. ONE line for every
    /// route that can queue one, so a wait the modder can't see the cause of always reads the same.</summary>
    internal const string RescanQueuedNotice = "Rescan queued";
    /// <inheritdoc cref="RescanQueuedNotice"/>
    internal const string RescanQueuedDetail = "Game files are re-read when the current work finishes.";
    /// <summary>The same wait, when a force rescan is the thing waiting: deleting caches is a bigger promise
    /// than re-reading files, so the detail says so. The CELL still reads "Rescan queued" — one wait, one
    /// word for it — and this rides the tooltip beneath it.</summary>
    internal const string RescanQueuedForceDetail =
        "Caches are cleared and game files re-read when the current work finishes.";

    /// <summary>The game folder changed under a scan in flight. Its own title, because what the modder just
    /// did is the news; the wait itself is described by the shared detail after the lead.</summary>
    internal const string GameDirChangedNotice = "Folder change pending";
    /// <inheritdoc cref="GameDirChangedNotice"/>
    internal const string GameDirChangedLead = "The new folder takes effect after this scan.";

    /// <summary>Which detail the queued-rescan notice carries: the force variant while a sweep is owed.</summary>
    private string QueuedRescanDetail => _forceRescanPending ? RescanQueuedForceDetail : RescanQueuedDetail;

    /// <summary>The standing queued-rescan notice's fixed half: the title and leading sentence it was
    /// written with. Kept so the DETAIL can be re-rendered when what the wait will do changes under it —
    /// a sweep taken back after the notice went up. Null until a queued line is written.</summary>
    private (string Title, string Lead)? _queuedNoticeShape;

    /// <summary>Write the queued-rescan notice: the site's own title and optional leading sentence, then the
    /// shared detail that names the sweep while one is owed. THE one home for it, so the three routes that
    /// can queue a rescan — the reload's own hold, a folder change under a scan, the game closing — can
    /// never describe the same wait differently.</summary>
    private void ShowQueuedRescanNotice(string title, string lead = "")
    {
        _queuedNoticeShape = (title, lead);
        ReplaceNoticeCell(new NoticeMessage("game.rescan-queued", title,
            lead.Length == 0 ? QueuedRescanDetail : lead + " " + QueuedRescanDetail));
    }

    /// <summary>Re-render the standing queued-rescan notice against what is owed NOW. The notice is written
    /// once, at the click that queued it, and keeps the wording it was written with; this is how a sweep
    /// armed or taken back afterwards stops the standing line promising the wrong thing. A cell holding
    /// anything else has been written over since and is left alone.</summary>
    private void RefreshQueuedRescanNotice()
    {
        if (_queuedNoticeShape is { } shape && NoticeStatus.Text == shape.Title)
            ShowQueuedRescanNotice(shape.Title, shape.Lead);
    }

    /// <summary>Run a queued rescan once the roster's holds let go. Called at every load exit AFTER
    /// IsScanning clears and at the end of each build/materialize scope; it re-tests the hold rather than
    /// consuming the queue, so a drain under another standing hold leaves the rescan for that one.</summary>
    private void RunQueuedRescan()
    {
        if (!_rescanAfterScan || RescanMustWait) return;
        _rescanAfterScan = false;
        ReloadRoster();
    }

    /// <summary>A force rescan is owed: the rebuilt caches are to be swept the next time
    /// <see cref="ReloadRoster"/> really reloads. Set at Save, consumed there — never at the click, because
    /// a build, scan, materialize or prewarm may be reading the very caches the sweep removes.
    /// <para>Mirrors <see cref="LabSettings.ForceRescanOwed"/>, which is where the debt waits out an exit:
    /// this field is seeded from it at construction, so a request armed under a build and then closed on is
    /// honoured by the next session's first load rather than lost.</para></summary>
    private bool _forceRescanPending;

    /// <summary>Whether the force-rescan sweep is still owed, for tests that pin a queued force rescan
    /// deleting nothing until the rescan runs.</summary>
    internal bool ForceRescanPending => _forceRescanPending;

    /// <summary>Where the sweep's derived-cache trees live. A seam because <see cref="LabPaths.CacheRoot"/>
    /// is a static read of <c>%LOCALAPPDATA%</c>, which nothing redirects under test: a test that drove the
    /// fired sweep against it would delete the caches of whoever ran the suite.
    /// <para>Read INSIDE the sweep, on the pool thread, so a test can also hold the sweep open here and pin
    /// what waits on it — see <see cref="PendingCachePurge"/>.</para>
    /// <para>Assignable after construction for the sweep a RELOAD fires, and settable at construction (the
    /// ctor's <c>cacheRootFor</c>) for the one the construction path fires, which runs before any caller
    /// holds the instance.</para></summary>
    internal Func<string> CacheRootFor = () => LabPaths.CacheRoot;

    /// <summary>The sharing pass's cancellation source, for the test that pins the sweep running AFTER the
    /// pass is cancelled — the pass writes its cache under no hold, so the order is the whole guarantee.</summary>
    internal CancellationTokenSource? SharingPassCts { get => _sharingCts; set => _sharingCts = value; }

    /// <summary>The sweep's file deletions, running off the UI thread. <see cref="Task.CompletedTask"/> when
    /// no sweep has been started — a load that finds nothing owed waits on nothing.</summary>
    private Task _forceRescanPurge = Task.CompletedTask;

    /// <summary>What the load waits out before it reads or writes ANY cache. The whole point of the seam:
    /// the deletions run in the background, but the reload that follows them must not race its own cache
    /// writes against a sweep still removing the folder underneath — a rebuilt snapshot written mid-sweep is
    /// exactly the stale file the modder asked to be rid of.
    /// <para>Awaited at the top of <see cref="LoadAsync"/>, which is the one entry to every cache read the
    /// reload takes. Internal so a test can pin the wait rather than the wording of a comment.</para></summary>
    internal Task PendingCachePurge => _forceRescanPurge;

    /// <summary>Consume an owed force rescan and hand its deletions to the thread pool. A no-op when nothing
    /// is owed, so the two callers — the reload's ordering point, and the app's first load for a debt that
    /// survived an exit — can call it unconditionally.
    ///
    /// <para>The debt is settled BEFORE the sweep rather than after it. A sweep interrupted part-way leaves
    /// caches the next open rebuilds, which is the same cost as the held file this sweep already skips —
    /// whereas a debt cleared only on completion is a debt re-armed by every crash, sweeping again on every
    /// launch until one run finishes.</para>
    ///
    /// <para>ONE sweep is ever in flight, so the task this replaces is always finished with: the load it was
    /// started for sets <see cref="IsScanning"/> before it begins, and every route back to here waits on that
    /// through <see cref="RescanMustWait"/>.</para></summary>
    private void BeginForceRescanPurge()
    {
        if (!_forceRescanPending) return;
        _forceRescanPending = false;
        if (_settings.ForceRescanOwed) { _settings.ForceRescanOwed = false; SaveSettings(); }
        // The settings are read HERE: the recents are a live list the pool must not be handed to enumerate
        // later, and the library root goes with them. The cache root is a static path read and is taken
        // inside the sweep, which is also where a test holds the sweep open (see CacheRootFor).
        var recents = _settings.RecentMods.Select(m => m.Path).ToList();
        var libraryRoot = _settings.ResolvedLibraryRoot;
        _forceRescanPurge = Task.Run(() =>
        {
            // Nothing escapes: the load AWAITS this task, so a fault here would come out of the load rather
            // than out of the sweep — a rescan that never finishes over a cache folder that wouldn't go.
            // CacheReset already skips what it can't remove item by item; this is the outer edge of the same
            // rule, and what it costs is a surviving cache, i.e. a slower next open.
            try
            {
                CacheReset.ClearDerivedCaches(CacheRootFor());
                CacheReset.ClearCombinedFingerprints(CacheReset.ProjectRoots(libraryRoot, recents));
            }
            catch
            {
                // Reaching HERE is the total failure — the walk itself fell over, so a sweep that removed
                // nothing would otherwise be indistinguishable from one that finished. The request goes back
                // on so the next rescan retries it, and so the queued notice and the reopened Settings row
                // keep telling the truth about what is still owed.
                //
                // The SESSION flag only. The durable one stays down deliberately: a sweep that faults every
                // time it runs would otherwise be re-armed on disk forever and re-sweep on every launch, and
                // a debt this session couldn't pay is not one the next session should inherit.
                //
                // Written from the pool. It is a bool, and the reader that matters is behind this task's
                // completion (the load awaits it), so a UI-thread read either sees the retry or is one this
                // sweep's own reload would have overwritten anyway.
                _forceRescanPending = true;
            }
        });
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
        IncludeRepairData = _settings.IncludeRepairData,
        RecentCount = _settings.RecentMods.Count,
        // The row opens on the truth: a sweep already owed — armed under a hold, or carried over from a
        // session that closed before it ran — shows as armed rather than as an offer to arm it again.
        ForceRescanOwed = _forceRescanPending,
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
    /// library). Author and the repair-data default apply to <i>new</i> mods only — the open project keeps
    /// its own, both of them.</summary>
    public void ApplySettings(SettingsResult r)
    {
        _settings.Author = r.Author;
        _settings.IncludeRepairData = r.IncludeRepairData;
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
        // Owed BEFORE any reload below, so whichever rescan runs is the one that sweeps. The recents this
        // read enumerates are the post-clear list — a cleared entry's project keeps its sidecars, which the
        // next open rebuilds anyway.
        //
        // ASSIGNED, not just set: the row opens on the state the app is in and its button toggles, so the
        // form hands back the row's whole state and false from it means the request was taken back. (The
        // form reads that state when it opens; a queued sweep that drains while the dialog is up therefore
        // gets re-armed by a Save that never touched the row. It costs one more sweep of caches the app
        // rebuilds, which is the harmless direction to be wrong in — and it costs only that, because the
        // reload below is gated on the Save having ARMED the request rather than merely carrying it.)
        //
        // Durable, so a request armed under a build and then closed on is honoured by the next session
        // rather than lost — settled again where the sweep is handed off (BeginForceRescanPurge).
        _forceRescanPending = r.ForceRescan;
        _settings.ForceRescanOwed = r.ForceRescan;
        SaveSettings();
        // A queued notice may be standing over this Save — the request was armed under a build, and the
        // build is still running. It was written with the wording of the moment it went up, so a sweep taken
        // back here would leave it promising cleared caches for a wait that will now only re-read files.
        RefreshQueuedRescanNotice();

        if (blenderChanged) RefreshBlenderStatus();
        bool reloaded = false;
        if (gameChanged && newGame is { } g)
        {
            _gameDir = g;
            RaiseModsFolderGates();
            if (!IsScanning) { ReloadRoster(); reloaded = true; }
        }
        // The reload is what FIRES a newly armed sweep: ReloadRoster answers a busy app itself — the standing
        // "Rescan queued" notice and the flag RunQueuedRescan drains — so the sweep waits with it rather than
        // running under a hold.
        //
        // NEWLY armed, though, and not merely owed. A reload is a full re-read of the install, and the Save
        // that hands back a request it opened with (the row untouched, or a queued sweep that drained while
        // the dialog was up) has asked for nothing new: the debt stands on both flags, and the next rescan
        // that happens for its own reasons honours it. Costing an unrelated settings edit a whole re-read is
        // the thing this gate exists to prevent.
        if (r.ForceRescan && !r.ForceRescanWasOwed && !reloaded) ReloadRoster();
    }

    /// <summary>Recompute the status-bar Blender line. Presence-only (no process spawn), so it runs
    /// inline.</summary>
    private void RefreshBlenderStatus()
    {
        var exe = BlenderLocator.Find(_settings.PreferredBlender);
        BlenderPath = exe ?? "";
        BlenderStatus = exe is null ? StatusFacet.Bad("Blender · not found", "Set the Blender path in Settings.") : StatusFacet.Good("Blender");
    }

    private static string? Empty2Null(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (Directory.Exists(ExportOutDir))
            Process.Start(new ProcessStartInfo { FileName = ExportOutDir, UseShellExecute = true });
        else
            EditPage.ReportStatus("The mod folder is gone. Save the mod to create it again.");
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnEnemySearchTextChanged(string value) => ApplyFilter();
    partial void OnWeaponSearchTextChanged(string value) => ApplyFilter();

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
            // THE GUARANTEE: a force rescan's deletions run off this thread, but no cache is read or written
            // until they are done. Every cache the load touches — the catalog snapshot, the roster snapshot,
            // the sharing measurement, thumbnails, the rig fingerprints — is behind this line, so the load
            // can never write a rebuilt file into a folder the sweep is still emptying and leave the stale
            // copy standing. Completed already when nothing was owed. See BeginForceRescanPurge for the
            // handoff.
            //
            // INSIDE the try: the sweep swallows its own failures today, but a wait that ever did fault
            // ahead of this line would leave IsScanning set with nothing left to clear it — the app scanning
            // forever, with no notice and no working Rescan. The catch below is the load's one exit for that.
            await PendingCachePurge.ConfigureAwait(false);
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
                    ReplaceNoticeCell(new NoticeMessage("game.locate", "Locate the game",
                        "Choose the folder that contains the game's .exe. Use Tools · Locate game…"));
                    ShowSettingsSaveNotice();   // this path never reaches the finalize aggregation
                    // Neither surface can read the install: both say so rather than reading as empty.
                    EditPage.Rebuild();
                    _ = BuildPage.ReplanAsync();
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
            try
            {
                loc = LocalizationDb.LoadRosterCached(nameDb,
                    LabPaths.DisplayNameSnapshotFile("Enus"), out _);
            }   // English (Enus) is fixed for v1
            catch { /* locale table unreadable — labels fall back to tokens */ }
            DisplayNames? names = null;
            if (loc is not null)
                try { names = DisplayNames.Build(nameDb, loc); }
                catch { /* labels fall back to tokens */ }
            PhaseTime(_lt, "Phase 1: DB + localization + DisplayNames");
            var dbRoster = PhaseOnePlayableRoster(nameDb, names, out var playableStems);
            var vms = dbRoster.Select(c => new CharacterVm(c, OnSubjectToggled, OnCharacterToggled)).ToList();
            for (int i = 0; i < dbRoster.Count; i++)
                vms[i].Populate(dbRoster[i].Outfits.Select(o => (o, (IEnumerable<string>)Array.Empty<string>())), lightUp: false);

            // The Enemies tab roster. Best-effort like localization: an unreadable EnemyData table empties
            // the tab with a status note, never fails the load. Stems the playable roster already shows are
            // excluded, so a summon the enemy tables also reference can't appear in both tabs.
            List<Character> enemyRoster = new();
            bool enemyRosterUnreadable = false;
            try
            {
                enemyRoster = nameDb.ReadEnemyRoster(loc, playableStems);
            }
            catch (Exception e) { enemyRosterUnreadable = true; AppLog.Write("The enemy list couldn't be read", e); }
            var enemyVms = enemyRoster.Select(c => new CharacterVm(c, OnSubjectToggled, OnCharacterToggled)).ToList();
            for (int i = 0; i < enemyRoster.Count; i++)
                enemyVms[i].Populate(enemyRoster[i].Outfits.Select(o => (o, (IEnumerable<string>)Array.Empty<string>())), lightUp: false);

            // The Weapons tab rosters: weapons grouped by owner character, standalone skins grouped by
            // weapon type, generic attachment models grouped by slot category. Best-effort like the
            // enemy roster — unreadable weapon tables empty the tab with a status note, never fail the
            // load.
            List<Character> weaponRoster = new();
            bool weaponRosterUnreadable = false;
            try
            {
                weaponRoster = WeaponRoster
                    .BuildWeaponsByCharacter(WeaponRoster.ReadWeapons(nameDb, loc), dbRoster)
                    .Concat(WeaponRoster.BuildSkinsByType(WeaponRoster.ReadSkins(nameDb, loc)))
                    .Concat(WeaponRoster.BuildAttachmentsBySlot(WeaponRoster.ReadAttachments(nameDb, loc)))
                    .ToList();
            }
            catch (Exception e) { weaponRosterUnreadable = true; AppLog.Write("The weapon list couldn't be read", e); }
            var weaponVms = weaponRoster.Select(c => new CharacterVm(c, OnSubjectToggled, OnCharacterToggled)).ToList();
            for (int i = 0; i < weaponRoster.Count; i++)
                weaponVms[i].Populate(weaponRoster[i].Outfits.Select(o => (o, (IEnumerable<string>)Array.Empty<string>())), lightUp: false);
            PhaseTime(_lt, "Phase 1: DB roster + enemy/weapon rosters + VM construction");

            // Presence-only (no process spawn), so it stays on the launch path without blocking it.
            var blenderExe = BlenderLocator.Find(_settings.PreferredBlender);
            PhaseTime(_lt, "Blender detect");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allCharacters = vms;
                _allEnemies = enemyVms;
                _allWeapons = weaponVms;
                _roster = dbRoster.Concat(enemyRoster).Concat(weaponRoster).ToList();   // full roster until finalize narrows it
                // Built from the full roster so a mod OPENED DURING the load already reads friendly.
                RebuildFriendlyNames(dbRoster.Concat(enemyRoster).Concat(weaponRoster).ToList());
                GameStatus = StatusFacet.Good("Game");
                StatusChars = "Reading game files…";
                ReplaceLoadNotices(Array.Empty<NoticeMessage>());   // retire notices from a prior load
                BlenderPath = blenderExe ?? "";
                BlenderStatus = blenderExe is null ? StatusFacet.Bad("Blender · not found", "Set the Blender path in Settings.") : StatusFacet.Good("Blender");
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
                    ReplaceLoadNotices(new[] { inUse
                        ? new NoticeMessage("game.running", "The game is running",
                            "Cannot read the game's files while it's open. Close the game, then Rescan.")
                        : new NoticeMessage("game.files-unreadable", "Game files unreadable",
                            "The game's file list is missing, so nothing could be read. "
                            + "The install may be mid-update. Rescan to try again.") });
                    EditPage.Rebuild();
                    _ = BuildPage.ReplanAsync();
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
            // duplicates, so the roster needs it before it paints. The weapon roster stays OUT: weapon
            // parts draw independently of everything, so they hold no sharing rows and no witness roles.
            var population = SharingPopulation.Of(dbRoster, enemyRoster);
            var sharingBase = LoadSharingBase(LabPaths.SharingIndexFile(vfs.CatalogVersion),
                LabPaths.SharingSeedFile, vfs.CatalogVersion, population, AppLog.Write,
                vfs.InstallIdentity);
            PhaseTime(_lt, "Phase 2: sharing base load");

            // PHASE 3, existence — a candidate iff the prefab-address formula resolves its stem in some
            // context (catalog dictionary hits, no file reads). All three tabs ride ONE candidate list;
            // the Tab field only routes the row back to its own grid at the marshals.
            var candidates = new List<(CharacterVm Vm, Character Character, List<Outfit> Outfits, bool IsEnemy, bool IsWeapon)>();
            for (int i = 0; i < dbRoster.Count; i++)
            {
                // by OUTFIT, not stem: a curated subject's prefab is found through its own route
                var outfits = dbRoster[i].Outfits.Where(o => vfs.PrefabsFor(o).Count > 0).ToList();
                if (outfits.Count > 0) candidates.Add((vms[i], dbRoster[i], outfits, false, false));
            }
            for (int i = 0; i < enemyRoster.Count; i++)
            {
                var outfits = enemyRoster[i].Outfits.Where(o => vfs.PrefabsFor(o).Count > 0).ToList();
                if (outfits.Count > 0) candidates.Add((enemyVms[i], enemyRoster[i], outfits, true, false));
            }
            for (int i = 0; i < weaponRoster.Count; i++)
            {
                var outfits = weaponRoster[i].Outfits.Where(o => vfs.PrefabsFor(o).Count > 0).ToList();
                if (outfits.Count > 0) candidates.Add((weaponVms[i], weaponRoster[i], outfits, false, true));
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allCharacters = candidates.Where(c => !c.IsEnemy && !c.IsWeapon).Select(c => c.Vm).ToList();
                _allEnemies = candidates.Where(c => c.IsEnemy).Select(c => c.Vm).ToList();
                _allWeapons = candidates.Where(c => c.IsWeapon).Select(c => c.Vm).ToList();
                ApplyFilter();
                StatusChars = $"Reading models… 0/{candidates.Count}";
                // …and the Edit page, whose rows read the install through the model memo: redrawn now for
                // the state line, and the subjects of whatever mod is open read into that memo behind it.
                EditPage.Rebuild();
                _ = BuildPage.ReplanAsync();
                _ = WarmSubjectModelsAsync();
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
            var contentHashOf = BundleReads.ContentHashLookup(vfs.Manifest);
            var candidateOutfits = candidates.SelectMany(candidate => candidate.Outfits).ToList();
            var reusableRows = RosterSnapshot.LoadReusable(
                snapshotPath, catalog, contentHashOf, candidateOutfits);
            var missingRows = candidateOutfits.Select(outfit => outfit.ModelConfigId)
                .Where(id => !reusableRows.ContainsKey(id)).ToHashSet();
            foreach (var cand in candidates)
            {
                var confirmed = new List<(Outfit Outfit, IReadOnlyList<string> Parts)>();
                foreach (var outfit in cand.Outfits)
                {
                    if (reusableRows.TryGetValue(outfit.ModelConfigId, out var row)
                        && row.Parts is { } parts)
                        confirmed.Add((outfit, parts));
                }
                confirmedByVm[cand.Vm] = confirmed;
            }
            if (missingRows.Count == 0)
            {
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
                var fillCache = new RosterFillCache(vfs.TryDeobfuscateLogical);
                var filledRows = new System.Collections.Concurrent.ConcurrentDictionary<long, RosterSnapshot.Row>(
                    reusableRows);
                Parallel.ForEach(candidates.Where(candidate =>
                        candidate.Outfits.Any(outfit => missingRows.Contains(outfit.ModelConfigId))),
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, _settings.EncoderCpuLimit ?? Environment.ProcessorCount),
                    },
                    cand =>
                    {
                        var confirmed = confirmedByVm[cand.Vm];
                        foreach (var outfit in cand.Outfits.Where(outfit =>
                                     missingRows.Contains(outfit.ModelConfigId)))
                        {
                            try
                            {
                                var scope = SubjectScope.Build(catalog, fillCache.Read, outfit, fillCache);
                                var prefabs = scope.Candidates;
                                // Confirms iff a candidate carries recipe rows (character/RX shape) OR
                                // mesh-bearing renderer slots, skinned (the enemy smr-body shape) or static
                                // (the prop shape). Neither = UNCONFIRMED: it never lights up and is
                                // removed at finalize.
                                List<string>? parts = null;
                                if (prefabs.Any(c => c.Prefab.Recipe.Count > 0
                                                     || c.Prefab.Slots.Any(s => s.HasMesh)))
                                {
                                    parts = SubjectModelBuilder.OwnedSlotTokens(prefabs, outfit).ToList();
                                    confirmed.Add((outfit, parts));
                                }
                                filledRows[outfit.ModelConfigId] = RosterSnapshot.CreateRow(
                                    catalog, contentHashOf, outfit, scope.ScopeBundles, parts);
                            }
                            catch (Exception e) { fillErrors.Enqueue($"{outfit.Stem}: {e.Message}"); }
                        }
                        // The reused pre-pass seeded this list and the loop above appended the fresh
                        // fills, which on a PARTIAL reuse interleaves a character's outfits out of their
                        // roster order. Reassemble in cand.Outfits order — the order Pick renders.
                        var confirmedById = confirmed.ToDictionary(entry => entry.Outfit.ModelConfigId);
                        confirmed = cand.Outfits
                            .Where(outfit => confirmedById.ContainsKey(outfit.ModelConfigId))
                            .Select(outfit => confirmedById[outfit.ModelConfigId]).ToList();
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
                        // Confirmed and cleanly dropped rows are both explicit. Missing means unfilled and
                        // can never be mistaken for a reusable rejection on the next launch.
                        int expected = candidateOutfits.Select(outfit => outfit.ModelConfigId).Distinct().Count();
                        if (filledRows.Count == expected)
                            RosterSnapshot.SaveRows(snapshotPath, vfs.CatalogVersion, filledRows.Values);
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
                var survivingWeapons = new List<CharacterVm>();
                var confirmedRoster = new List<Character>();
                foreach (var cand in candidates)
                {
                    if (!confirmedByVm.TryGetValue(cand.Vm, out var confirmed) || confirmed.Count == 0) continue;
                    // The roster keeps every confirmed outfit, filtered doors included — it is what resolves
                    // a picked subject — while the tab lists only what survives the filter.
                    confirmedRoster.Add(cand.Character with { Outfits = confirmed.Select(x => x.Outfit).ToList() });
                    if (Listed(cand.Character, cand.IsEnemy, confirmed).Count == 0) continue;
                    (cand.IsEnemy ? survivingEnemies : cand.IsWeapon ? survivingWeapons : surviving).Add(cand.Vm);
                }
                _allCharacters = surviving;
                _allEnemies = survivingEnemies;
                _allWeapons = survivingWeapons;
                _roster = confirmedRoster;

                // Rebuilt from the same phase-1 roster, so the resolver covers every subject the fill saw
                // and not just the ones that survived it.
                RebuildFriendlyNames(dbRoster.Concat(enemyRoster).Concat(weaponRoster).ToList());

                StatusChars = $"Characters: {surviving.Count} · Enemies: {survivingEnemies.Count} · Weapons: {survivingWeapons.Count} · game data v{vfs.CatalogVersion}";
                // Warnings ride the notice cell, not the roster line — full detail in its tooltip.
                var notices = new List<NoticeMessage>();
                if (_settings.LoadedFromDefaultsAfterError)
                    notices.Add(new NoticeMessage("settings.reset", "Settings reset",
                        "Your settings file couldn't be read. Default settings are in use until you save.",
                        BulletWhenAlone: true));
                // Carries a save failure from the off-UI-thread detection write, which has no cell to
                // reach. Load finalization replaces only load-owned identities, so include this standing
                // app notice in the new set explicitly.
                if (_settingsSaveFailed)
                {
                    notices.Add(SettingsSaveFailedNotice());
                    _settingsSaveNoticeShown = true;
                }
                if (enemyRosterUnreadable)
                    notices.Add(new NoticeMessage("game.enemy-list-unreadable", "Enemy list unreadable",
                        "The enemy list couldn't be read, so the Enemies tab is empty. "
                        + "Use Tools · Rescan game files to try again.", BulletWhenAlone: true));
                if (weaponRosterUnreadable)
                    notices.Add(new NoticeMessage("game.weapon-list-unreadable", "Weapon list unreadable",
                        "The weapon list couldn't be read, so the Weapons tab is empty. "
                        + "Use Tools · Rescan game files to try again.", BulletWhenAlone: true));
                if (missing.Count > 0)
                {
                    string files = $"game file{(missing.Count == 1 ? "" : "s")}";
                    notices.Add(new NoticeMessage("game.files-missing", $"{missing.Count} {files} missing",
                        $"{missing.Count} {files} {(missing.Count == 1 ? "is" : "are")} missing. "
                        + "The install may be mid-update. Verify the game files.", BulletWhenAlone: true));
                }
                if (!fillErrors.IsEmpty)
                {
                    AppLog.Write("Outfits couldn't be read", string.Join(Environment.NewLine, fillErrors));
                    string outfits = $"outfit{(fillErrors.Count == 1 ? "" : "s")}";
                    notices.Add(new NoticeMessage("game.outfits-unreadable",
                        $"{fillErrors.Count} {outfits} unreadable",   // count-led, like the files-missing label
                        $"{fillErrors.Count} {outfits} couldn't be read, so they aren't listed. "
                        + "Use Tools · Rescan game files to try again.", BulletWhenAlone: true));
                }
                // The stale-version warning goes INTO this notices list rather than overwriting the cell
                // afterward: an overwrite silently loses whatever warning was already there.
                if (TakeAuthoredAgainstNotice() is { } stale) notices.Add(stale);
                ReplaceLoadNotices(notices);
                IsScanning = false;
                ApplyFilter();
                // Re-assert the ledger's checkboxes after ANY load. No MarkDirty — a load changes no mod
                // state.
                SyncSubjectsFromLedger();
                _pendingSelection = null;
                FinishRosterLoadBackgroundWork();
            });
            PhaseTime(_lt, "Phase 3: finalize marshal");

            // The sharing pass, after the launch's own reads. Data measured under this catalog is the
            // answer outright; anything else repairs in the background over the whole modding roster —
            // enemies and props wear shared textures too. Builds await the task; nothing else does, so a
            // failure has no surface beyond the build saying "unscoped".
            StartSharingIndexJob(vfs, population, sharingBase);
        }
        catch (Exception)
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
                ReplaceLoadNotices(new[] { inUse
                    ? new NoticeMessage("game.running", "The game is running",
                        "Cannot read the game's files while it's open. Close the game, then Rescan.")
                    : new NoticeMessage("game.data-unreadable", "Game data unreadable",
                        "Couldn't read the game data. Rescan to try again. "
                        + "If it keeps happening, the install may be damaged or an unsupported version.") });
                EditPage.Rebuild();
                _ = BuildPage.ReplanAsync();
                // Location ALREADY succeeded, so both gates' halves are knowable; a phase-1 failure would
                // otherwise leave them on the defaults they booted with.
                RaiseModsFolderGates();
                RunQueuedRescan();
            });
        }
    }

    /// <summary>The playable rows Phase 1 is allowed to expose before any game bundle/index read. Curated
    /// rows join after localization, then the silent roster policy removes blocked character names and
    /// outfit stems before a <see cref="CharacterVm"/> or friendly-name entry can be created.</summary>
    internal static List<Character> PhaseOnePlayableRoster(GameDatabase nameDb, DisplayNames? names)
        => PhaseOnePlayableRoster(nameDb, names, out _);

    /// <summary>The same Phase-1 policy with the pre-policy outfit stems the enemy roster must de-duplicate
    /// against. A blocked playable row stays invisible but still owns its stem, matching the roster's
    /// historical table-to-table de-duplication.</summary>
    internal static List<Character> PhaseOnePlayableRoster(
        GameDatabase nameDb, DisplayNames? names, out HashSet<string> enemyExclusionStems)
    {
        var roster = nameDb.ReadRoster();
        if (names is not null) roster = names.Enrich(roster);
        // Curated skins the design DB can't enumerate (no ModelConfigData row names them). Folded in AFTER
        // enrichment: their labels are curated strings, not localization lookups. A curated character the
        // DB already names merges into that row rather than listing a second one.
        roster = CuratedSkins.MergeInto(roster);
        enemyExclusionStems = roster.SelectMany(character => character.Outfits).Select(outfit => outfit.Stem)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return roster
            .Where(character => !RosterBlacklist.IsBlacklisted(character.Name))
            .Select(character => (Character: character,
                Outfits: character.Outfits
                    .Where(outfit => !RosterBlacklist.IsBlacklisted(outfit.Stem)).ToList()))
            // Keep an ordinary DB row that began with no model rows; Phase 3 owns its existence decision.
            // A row that had only blocked outfits is itself now empty because of policy, so it stays silent.
            .Where(row => row.Character.Outfits.Count == 0 || row.Outfits.Count > 0)
            .Select(row => row.Character with { Outfits = row.Outfits })
            .ToList();
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

        // Weapon groups by owner first, then the per-type skin groups, then the per-slot attachment
        // groups — three branches of one tab, so none interleaves another's names. The search also
        // reads the SUBJECT rows: a weapon's own name lives on the row, not the group ("Basic Muzzle
        // Brake" sits under "Parts · Silencers"), so a group-only match would find nothing.
        var wq = WeaponSearchText?.Trim() ?? "";
        Weapons.Clear();
        foreach (var c in _allWeapons
                     .OrderBy(c => c.Name.StartsWith(WeaponRoster.PartGroupPrefix, StringComparison.Ordinal) ? 2
                                 : c.Name.StartsWith(WeaponRoster.SkinGroupPrefix, StringComparison.Ordinal) ? 1 : 0)
                     .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase))
            if (wq.Length == 0 || MatchesCharacter(c, wq)
                || c.Outfits.Any(o => o.Label.Contains(wq, StringComparison.OrdinalIgnoreCase)
                                   || o.Stem.Contains(wq, StringComparison.OrdinalIgnoreCase)))
                Weapons.Add(c);

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
        return shown == 0 && q.Length > 0 ? $"No match for '{q}'." : "";
    }

    /// <summary>Why an enemy the modder searched for may not be listed under its own name. The tab's
    /// duplicate-door filter is otherwise invisible, and a stated fact beats a hole.</summary>
    internal const string EnemyDoorNote =
        "Enemies that reuse a character's meshes are listed under that character.";

    public string CharactersNoMatch => NoMatchLine(SearchText, Characters.Count);
    public string EnemiesNoMatch => NoMatchLine(EnemySearchText, Enemies.Count);
    public string WeaponsNoMatch => NoMatchLine(WeaponSearchText, Weapons.Count);
    public bool HasCharactersNoMatch => CharactersNoMatch.Length > 0;
    public bool HasEnemiesNoMatch => EnemiesNoMatch.Length > 0;
    public bool HasWeaponsNoMatch => WeaponsNoMatch.Length > 0;
    /// <summary><see cref="EnemyDoorNote"/> for the view, so the sentence has one home.</summary>
    public string EnemiesNoMatchNote => EnemyDoorNote;

    private void RefreshEmptyStates()
    {
        OnPropertyChanged(nameof(CharactersNoMatch));
        OnPropertyChanged(nameof(EnemiesNoMatch));
        OnPropertyChanged(nameof(WeaponsNoMatch));
        OnPropertyChanged(nameof(HasCharactersNoMatch));
        OnPropertyChanged(nameof(HasEnemiesNoMatch));
        OnPropertyChanged(nameof(HasWeaponsNoMatch));
    }

    /// <summary>The measurement data already on this machine for the loaded catalog: the cache first,
    /// then the shipped seed. Fast (a file read and a name join, no bundle reads), so the load path takes
    /// it inline. Its <see cref="SharingIndex.CatalogVersion"/> is what the data was MEASURED under —
    /// possibly older than the running game's, which makes it a delta base rather than an answer.</summary>
    internal static SharingBase LoadSharingBase(string cachePath, string seedPath, string catalogVersion,
        SharingPopulation population, Action<string, string>? diagnostic = null,
        string? installIdentity = null)
    {
        if (catalogVersion == GameInfo.UnknownVersion) return default;   // nothing pins a cache or a measurement
        // The CURRENT catalog's cache tolerates a MISSING sidecar: the file lives in this machine's own
        // per-install cache folder, so a sidecar-less one is a prior release's local measurement, not a
        // foreign copy — refusing it would cost every upgrading install one full remeasure for nothing.
        // Adoption mints the sidecar so the prior-catalog delta path (which stays strict) works from the
        // next game patch on. A sidecar that EXISTS and disagrees is still refused — that is the copied
        // file the guard exists for.
        if (LocalSharingContextMatchesOrAbsent(cachePath, installIdentity, out bool sidecarAbsent)
            && SharingIndex.TryLoad(cachePath, population) is { } cached
            && cached.CatalogVersion == catalogVersion)
        {
            if (sidecarAbsent && installIdentity is not null)
                try { WriteSharingInstallContext(cachePath, installIdentity); }
                catch { /* adoption is best-effort; the strict prior-cache path just stays inert */ }
            diagnostic?.Invoke("Asset sharing seed",
                "not read: this catalog's local sharing cache was accepted");
            return new SharingBase(cached, FromSeed: false);
        }
        // A present current-version file is authoritative or damaged. Prior caches are considered only
        // when it is absent, so a damaged current file cannot be silently masked by an older measurement.
        if (!File.Exists(cachePath))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(cachePath))!;
            IEnumerable<string> priorPaths;
            try
            {
                priorPaths = Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, "sharing_*.json")
                        .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(cachePath),
                            StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(Path.GetFullPath(path), Path.GetFullPath(seedPath),
                                StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
                    : Array.Empty<string>();
            }
            catch { priorPaths = Array.Empty<string>(); }

            foreach (string priorPath in priorPaths)
            {
                if (!LocalSharingContextMatches(priorPath, installIdentity)) continue;
                var prior = SharingIndex.TryLoad(priorPath, population, out var priorReport);
                if (prior is null || prior.CatalogVersion == catalogVersion || !prior.IsCompleteLocalBase())
                    continue;
                diagnostic?.Invoke("Asset sharing cache",
                    $"prior local schema {priorReport.StatedSchema} accepted; rows loaded "
                    + $"{priorReport.RowsLoaded}, joined {priorReport.RowsJoined}, "
                    + $"dropped {priorReport.RowsDropped}");
                return new SharingBase(prior, FromSeed: false);
            }
        }
        // A seed that joined to nothing is no base at all — it would read as "everything measured, nothing
        // shared" and every edit would ship as private.
        var seed = SharingIndex.TryLoad(seedPath, population, out var report);
        string schema = report.StatedSchema is { } stated
            ? report.SchemaAccepted ? $"schema {stated} accepted" : $"schema {stated} refused"
            : "schema refused";
        string problem = report.Problem is { Length: > 0 } why ? $"; {why}" : "";
        diagnostic?.Invoke("Asset sharing seed",
            $"{schema}; rows loaded {report.RowsLoaded}, joined {report.RowsJoined}, "
            + $"dropped {report.RowsDropped}{problem}");
        return seed is { MeasuredOutfitCount: > 0 } ? new SharingBase(seed, FromSeed: true) : default;
    }

    /// <summary>Install-context sidecar for local sharing files. A missing sidecar is accepted only by
    /// callers that supplied no context (tests and compatibility callers), never by the production load.</summary>
    internal static string SharingInstallContextPath(string sharingPath) => sharingPath + ".install";

    internal static void WriteSharingInstallContext(string sharingPath, string installIdentity)
    {
        string path = SharingInstallContextPath(sharingPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, installIdentity, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static bool LocalSharingContextMatches(string sharingPath, string? installIdentity)
    {
        if (installIdentity is null) return true;
        try
        {
            string path = SharingInstallContextPath(sharingPath);
            return File.Exists(path)
                && string.Equals(File.ReadAllText(path), installIdentity, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>The current-catalog cache's lenient variant: a missing sidecar passes (and reports itself
    /// so the caller can mint one); a present-but-different sidecar still refuses.</summary>
    private static bool LocalSharingContextMatchesOrAbsent(string sharingPath, string? installIdentity,
        out bool sidecarAbsent)
    {
        sidecarAbsent = false;
        if (installIdentity is null) return true;
        try
        {
            string path = SharingInstallContextPath(sharingPath);
            if (!File.Exists(path)) { sidecarAbsent = true; return true; }
            return string.Equals(File.ReadAllText(path), installIdentity, StringComparison.Ordinal);
        }
        catch { return false; }
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
                // The observation memo spans passes and catalogs: this install's own beside the sharing
                // cache, plus the shipped one, so a row this pass has to measure again still opens only
                // the bundles whose content neither file has ever seen.
                var hashes = new AssetHashMemo(LabPaths.AssetHashMemoFile, LabPaths.AssetHashSeedFile);
                var built = SharingIndex.Build(population, vfs.Catalog,
                    BundleReads.ContentHashLookup(vfs.Manifest), TryDeobfuscateBundle, cv,
                    basis.Index, progress, cts.Token, hashes);
                var publish = SharingPublishes(built, basis, cts.Token);
                if (publish.Cache)
                    try
                    {
                        built.Save(path);
                        WriteSharingInstallContext(path, vfs.InstallIdentity);
                    }
                    catch { /* cache write is best-effort; next launch remeasures */ }
                if (publish.Memo) hashes.Flush();
                return built;
            }
            catch (OperationCanceledException) { throw; }   // superseded, not failed — the cell says nothing
            catch (Exception) { SetSharingFailed(cts, true); throw; }
            finally { ReportSharingProgress(cts, null); }
        }, cts.Token);
    }

    /// <summary>What a completed pass publishes: this install's sharing cache, and the observation memo
    /// beside it. Both come out of ONE read of the token, on purpose — read separately, a cancellation
    /// landing between the two writes publishes one artifact of a pair and not the other, and what cancels
    /// a pass is a rescan that may have just swept the very folder both files live in. The condition is a
    /// decided VALUE rather than a condition re-asked at each write, so the two halves cannot disagree
    /// about whether the pass was still wanted.</summary>
    internal static SharingPublish SharingPublishes(SharingIndex built, SharingBase basis,
        CancellationToken token) =>
        token.IsCancellationRequested
            ? default
            : new SharingPublish(ShouldWriteSharingCache(built, basis), Memo: true);

    /// <summary>The two files a completed sharing pass may publish. <c>default</c> — neither — is what a
    /// cancelled pass gets.</summary>
    internal readonly record struct SharingPublish(bool Cache, bool Memo);

    /// <summary>Whether a completed pass's result is written to this install's cache, as a pure rule.
    /// Many failed outfits is a transient condition (typically the game holding its bundles), not a fact
    /// about the catalog — caching it would serve those outfits as uncovered until the next game update;
    /// a handful is the real per-catalog floor and caches. A result identical to the install's OWN cache
    /// is not written; a basis adopted from the shipped seed always is — that mints the cache.
    /// <para>A CANCELLED pass writes nothing. The build normally throws on cancellation, but a pass that
    /// finished in the same instant reaches here with its token already down — and by then the rescan that
    /// cancelled it may have swept the cache folder, so the write would resurrect the file the modder asked
    /// to clear and seed the next launch from rows measured before the sweep.</para></summary>
    internal static bool ShouldWriteSharingCache(SharingIndex built, SharingBase basis,
        CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return false;
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

    // ---- session Edit shell helpers -----------------------------------------------------------------

    /// <summary>Clipboard copy for the Edit page.</summary>
    public Task CopyTextAsync(string? text) => CopyText(text);

    /// <summary>How many times this session has written the mod out. One committed authored change costs
    /// one of these, which is what the round-trip test measures a whole Blender return against.</summary>
    internal int ProjectSaves { get; private set; }

    /// <summary>Persist a committed session mutation.</summary>
    public void AutoSaveProject() { MarkDirty(); AutoSave(); }

    /// <summary>Autosave, handing back the failure text (null on success) for a caller with its own
    /// surface.</summary>
    private string? TryAutoSaveProject() { MarkDirty(); return AutoSave(); }

    /// <summary>Name an unnamed project from the FIRST subject it takes. Must run BEFORE the folder is
    /// minted so the slug matches; a user-named project is NEVER overwritten.</summary>
    private void AutoNameFromSubject(string subjectCharacter)
    {
        if (CurrentProjectRoot is not null) return;
        if (!string.IsNullOrWhiteSpace(PackageName)) return;
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
        if (CurrentProjectRoot is { } root)
        {
            // The Build step can set RootDir without _modRoot, and a first materialize would then NRE on
            // _modRoot!. Resync off the persisted RootDir before the early return.
            if (!string.Equals(_modRoot, root, StringComparison.Ordinal))
            {
                _modRoot = root; ExportOutDir = root;
                EnsureWatcher();
            }
            return true;
        }
        // INVARIANT: a project's root is established ONCE and stays stable. _modRoot set with RootDir null
        // is a divergence bug — restore the established root LOUDLY rather than silently minting a SECOND
        // folder, which strands files.
        if (_modRoot is not null)
        {
            _projectDocument.RebaseRoot(_modRoot); ExportOutDir = _modRoot;
            EnsureWatcher();
            EditPage.ReportStatus($"Reconnected to the mod folder ({Path.GetFileName(_modRoot)}).");
            return true;
        }
        try
        {
            var modRoot = UniqueDir(_settings.ResolvedLibraryRoot, ModNaming.Slug(ProjectName));
            Directory.CreateDirectory(modRoot);
            _projectDocument.RebaseRoot(modRoot); ExportOutDir = modRoot; _modRoot = modRoot;
            EnsureWatcher();
            return true;
        }
        catch { return false; }
    }

    /// <summary>A session composition for Blender is building. The close, rename, and rescan guards read
    /// this UI-thread-owned flag.</summary>
    private int _buildingCombinedRig;

    private bool BuildingCombinedRig => Volatile.Read(ref _buildingCombinedRig) > 0;


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

    /// <summary>The current asset-sharing measurement line, or empty when no pass is running.</summary>
    internal static string BackgroundWorkLine(SharingProgress? sharing) =>
        sharing is { } state ? SharingLine(state) : "";

    // Short enough that the cell's 180px cap never eats the counter; "shared" is the tooltip's job.
    private static string SharingLine(SharingProgress s) =>
        (s.Delta ? "Updating assets" : "Checking assets")
        + (s.Total > 0 ? $"… {s.Done}/{s.Total}" : "…");

    /// <summary>What the measurement pass is for. The cell's label is a bare count, so the tooltip
    /// carries the whole answer.</summary>
    internal const string SharingCellTip =
        "Reading which outfits share textures and meshes.";

    /// <summary>What a failed pass leaves on the cell: a build after this one discloses instead of
    /// scoping, and Rescan is the retry.</summary>
    internal const string SharingUnmeasured = "Shared assets not checked";
    internal const string SharingUnmeasuredDetail =
        "Edits may also change other outfits that share the same textures or meshes. "
        + "Use Tools · Rescan game files to try again.";

    /// <summary>The background-work cell, as a pure rule. Running work outranks a past failure — a pass is
    /// answering the question the failure raised — and a failure that nothing is replacing STAYS, since a
    /// long visible run ending in a blank cell reads as success.</summary>
    internal static StatusFacet BackgroundFacet(SharingProgress? sharing, bool sharingFailed,
        RiggedGlbPrewarmProgress? riggedGlbPrewarm = null, bool riggedGlbPrewarmFailed = false)
    {
        if (riggedGlbPrewarm is { } prewarm)
            return StatusFacet.Loading(RiggedGlbPrewarmLine(prewarm), RiggedGlbPrewarmTip);
        if (sharing is { } s) return StatusFacet.Loading(SharingLine(s), SharingCellTip);
        if (riggedGlbPrewarmFailed)
            return StatusFacet.Warn(RiggedGlbPrewarmUnavailable, RiggedGlbPrewarmUnavailableDetail);
        return sharingFailed ? StatusFacet.Warn(SharingUnmeasured, SharingUnmeasuredDetail) : StatusFacet.None;
    }

    /// <summary>The current-install reads needed to adapt schema-1 routes and resolve session slots.</summary>
    private BuildEnv NewResolverEnvironment(GameVfs vfs)
    {
        SubjectModel? ResolveSubject(string character, string outfit)
        {
            if (_subjectModels.TryGet(character, outfit) is { } hit) return hit;
            if (PickOutfit(character, outfit) is not { } model) return null;
            var warmed = _subjectModels.GetOrBuild(character, outfit, () =>
                SubjectModelBuilder.Build(vfs.Catalog, logical =>
                {
                    try { return vfs.TryDeobfuscateLogical(logical); }
                    catch { return null; }
                }, model, character));
            SubjectModelWarmCompleted();
            return warmed;
        }

        return new BuildEnv(
            ResolveSubject,
            vfs.Catalog.ResolveAddress,
            logical =>
            {
                try { return vfs.TryDeobfuscateLogical(logical); }
                catch { return null; }
            },
            vfs.CatalogVersion,
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString());
    }

    private void RefreshBackgroundStatus() =>
        BackgroundStatus = BackgroundFacet(_sharingProgress, _sharingFailed,
            _riggedGlbPrewarmProgress, _riggedGlbPrewarmFailed);

    private Outfit? PickOutfit(string character, string stem) =>
        RosterLookup.FindOutfit(_roster, character, stem);

    private bool LaunchInImageEditor(string png, string ok, string failure, IProgress<string> status)
    {
        try
        {
            if (LaunchImageEditorForTests is { } launch)
            {
                if (!launch(png)) throw new InvalidOperationException("the test image editor refused the file");
            }
            else if (ImageEditorLocator.Find(_settings.PreferredImageEditor) is { } editor)
            {
                var psi = new ProcessStartInfo(editor) { UseShellExecute = false };
                psi.ArgumentList.Add(png);
                Process.Start(psi);
            }
            else Process.Start(new ProcessStartInfo(png) { UseShellExecute = true });
            status.Report(ok);
            return true;
        }
        // The start's own exception describes a shell association or an exe the modder didn't name here;
        // what they can change is the editor the app was told to use, so the line points at that.
        catch (Exception e)
        {
            AppLog.Write("Couldn't start the image editor", e);
            status.Report($"{failure}. Set an image editor in Settings.");
            return false;
        }
    }

    internal Func<string, bool>? LaunchImageEditorForTests { get; set; }

    /// <summary>Show the toon-ramp pick list over the main window.</summary>
    public async Task<EditPage.RampChoice?> PickRampAsync(EditPage.RampPickerVm picker)
    {
        if (MainWindow is not { } owner) return null;
        return await RampPickerWindow.Show(owner, picker);
    }


    /// <summary>The live catalog version the "made for a different game version" notice was already shown
    /// for, so it fires once per catalog change.</summary>
    private string? _authoredNoticeShownFor;

    /// <summary>The one-time "made for a different game version" notice, or null when it doesn't apply.
    /// Marks it shown when it returns a pair. BOTH surfacing paths go through here, so the one-shot
    /// semantics and the wording stay in one place.</summary>
    internal const string AuthoredAgainstNoticeId = "project.authored-against";
    internal const string ProjectMigrationNoticeId = "project.migration";

    private NoticeMessage? TakeAuthoredAgainstNotice()
    {
        if (ShowHome || _vfs is null) return null;
        var live = _vfs.CatalogVersion;
        var authoredAgainst = AuthoredSnapshot.AuthoredAgainst?.CatalogVersion;
        if (!AuthoredAgainstPolicy.NeedsStaleNotice(authoredAgainst, live)) return null;
        if (string.Equals(_authoredNoticeShownFor, live, StringComparison.Ordinal)) return null;
        _authoredNoticeShownFor = live;
        return new NoticeMessage(AuthoredAgainstNoticeId, "Made for a different game version",
            "This mod was made for a different version of the game. Check your edits, then build again.",
            ProjectScoped: true, BulletWhenAlone: true);
    }

    // ---- settings persistence ----

    /// <summary>A settings write failed this run. Latched: a folder that refuses one write refuses the
    /// rest, so the failure is reported once rather than per save.</summary>
    private bool _settingsSaveFailed;

    /// <summary>The latched failure is already on the notice cell.</summary>
    private bool _settingsSaveNoticeShown;

    private const string SettingsSaveFailedShort = "settings not saved";
    private const string SettingsSaveFailedDetail =
        "Cannot save settings. Changes are lost when the app closes. Move the app out of a protected folder.";

    /// <summary>Persist settings, reporting a failed write instead of dropping it. A silent failure is the
    /// bad case: the change looks applied and is gone at exit.</summary>
    private void SaveSettings()
    {
        if (_settings.TrySave()) return;
        AppLog.Write("Settings couldn't be saved", $"The write to {LabSettings.DefaultPath} failed.");
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
        MergeNoticeIntoCell(SettingsSaveFailedNotice());
    }

    /// <summary>Surface the stale-version notice on the plain-open path. It must never clobber a warning
    /// the load already put in the cell: replace only an EMPTY cell, otherwise merge.</summary>
    private void MaybeNoticeAuthoredAgainst()
    {
        if (TakeAuthoredAgainstNotice() is { } notice) MergeNoticeIntoCell(notice);
    }

    /// <summary>Fold one notice into the cell without losing what's there: an occupied cell is rebuilt as
    /// the same "N warnings" + "• "-bulleted facet the load-finalize aggregation renders.</summary>
    private static NoticeMessage SettingsSaveFailedNotice() =>
        new("settings.save-failed", SettingsSaveFailedShort, SettingsSaveFailedDetail,
            BulletWhenAlone: true);

    internal enum NoticeSeverity { Info, Warning, Error }

    internal sealed record NoticeMessage(string Identity, string Short, string Detail,
        bool ProjectScoped = false, bool BulletWhenAlone = false,
        NoticeSeverity Severity = NoticeSeverity.Warning);

    private readonly List<NoticeMessage> _noticeMessages = new();
    private StatusFacet _renderedNoticeStatus = StatusFacet.None;
    private long _adoptedNoticeIdentity;

    /// <summary>Fold one identified notice into the cell. Re-evaluating the same notice replaces its
    /// standing entry; it never grows a second bullet merely because its rendered text changed.</summary>
    internal void MergeNoticeIntoCell(NoticeMessage add)
    {
        AdoptExternallyAssignedNotice();
        int standing = _noticeMessages.FindIndex(notice =>
            string.Equals(notice.Identity, add.Identity, StringComparison.Ordinal));
        if (standing < 0) _noticeMessages.Add(add);
        else _noticeMessages[standing] = add;
        RenderNoticeCell();
    }

    private void ReplaceNoticeCell(NoticeMessage notice) => ReplaceNoticeCell(new[] { notice });

    private void ReplaceNoticeCell(IEnumerable<NoticeMessage> notices)
    {
        _noticeMessages.Clear();
        foreach (var notice in notices)
        {
            int standing = _noticeMessages.FindIndex(candidate =>
                string.Equals(candidate.Identity, notice.Identity, StringComparison.Ordinal));
            if (standing < 0) _noticeMessages.Add(notice);
            else _noticeMessages[standing] = notice;
        }
        RenderNoticeCell();
    }

    private static readonly HashSet<string> LoadNoticeIdentities = new(StringComparer.Ordinal)
    {
        "settings.reset",
        "game.invalid-folder",
        "game.rescan-queued",
        "game.locate",
        "game.running",
        "game.files-unreadable",
        "game.data-unreadable",
        "game.enemy-list-unreadable",
        "game.weapon-list-unreadable",
        "game.files-missing",
        "game.outfits-unreadable",
    };

    /// <summary>Replace only the notices one game-file load owns. Project and other standing identities stay
    /// in the cell; a later load has no lifecycle authority over them.</summary>
    internal void ReplaceLoadNotices(IEnumerable<NoticeMessage> notices)
    {
        AdoptExternallyAssignedNotice();
        _noticeMessages.RemoveAll(notice => LoadNoticeIdentities.Contains(notice.Identity));
        foreach (var notice in notices)
        {
            int standing = _noticeMessages.FindIndex(candidate =>
                string.Equals(candidate.Identity, notice.Identity, StringComparison.Ordinal));
            if (standing < 0) _noticeMessages.Add(notice);
            else _noticeMessages[standing] = notice;
        }
        RenderNoticeCell();
    }

    private void ClearNoticeCell()
    {
        _noticeMessages.Clear();
        RenderNoticeCell();
    }

    /// <summary>Project notice teardown and the stale-warning one-shot rearm are one lifecycle operation.
    /// Install/settings notices remain because they describe the app around the project, not the mod left.</summary>
    internal void ResetProjectNoticeLifecycle()
    {
        AdoptExternallyAssignedNotice();
        _noticeMessages.RemoveAll(notice => notice.ProjectScoped);
        _authoredNoticeShownFor = null;
        RenderNoticeCell();
    }

    internal void RemoveNotice(string identity)
    {
        AdoptExternallyAssignedNotice();
        _noticeMessages.RemoveAll(notice => string.Equals(notice.Identity, identity,
            StringComparison.Ordinal));
        RenderNoticeCell();
    }

    /// <summary>Tests can assign the public facet directly. Preserve such a standing cell as an opaque notice
    /// before a later identified merge instead of silently losing it; this is the bridge for those tests.</summary>
    private void AdoptExternallyAssignedNotice()
    {
        if (Equals(NoticeStatus, _renderedNoticeStatus)) return;
        _noticeMessages.Clear();
        if (NoticeStatus.HasGlyph)
            _noticeMessages.Add(new NoticeMessage($"external.{++_adoptedNoticeIdentity}",
                NoticeStatus.Text, NoticeStatus.Detail, Severity: NoticeStatus.Glyph == "✗"
                    ? NoticeSeverity.Error : NoticeStatus.Glyph == "✓"
                        ? NoticeSeverity.Info : NoticeSeverity.Warning));
        _renderedNoticeStatus = NoticeStatus;
    }

    private void RenderNoticeCell()
    {
        StatusFacet rendered;
        if (_noticeMessages.Count == 0)
            rendered = StatusFacet.None;
        else if (_noticeMessages.Count == 1)
        {
            var notice = _noticeMessages[0];
            string detail = notice.BulletWhenAlone && notice.Detail.Length > 0
                ? "• " + notice.Detail : notice.Detail;
            rendered = NoticeFacet(notice.Severity, notice.Short, detail);
        }
        else
        {
            var severity = _noticeMessages.Max(notice => notice.Severity);
            string label = _noticeMessages.All(notice => notice.Severity == NoticeSeverity.Warning)
                ? $"{_noticeMessages.Count} warnings"
                : $"{_noticeMessages.Count} notices";
            rendered = NoticeFacet(severity, label,
                string.Join("\n", _noticeMessages.Select(notice =>
                    "• " + (notice.Detail.Length > 0 ? notice.Detail : notice.Short))));
        }
        _renderedNoticeStatus = rendered;
        NoticeStatus = rendered;
    }

    private static StatusFacet NoticeFacet(NoticeSeverity severity, string text, string detail) =>
        severity switch
        {
            NoticeSeverity.Error => StatusFacet.Bad(text, detail),
            NoticeSeverity.Info => StatusFacet.Info(text, detail),
            _ => StatusFacet.Warn(text, detail),
        };

    private string? BlenderOverride() => !string.IsNullOrWhiteSpace(_settings.PreferredBlender) ? _settings.PreferredBlender
        : string.IsNullOrWhiteSpace(BlenderPath) ? null : BlenderPath;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

}
