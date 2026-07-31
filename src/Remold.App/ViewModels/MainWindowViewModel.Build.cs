using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remold.App.Views;
using Remold.Core.Migoto;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Workbench;

namespace Remold.App.ViewModels;

/// <summary>
/// The Build pane: the derived change list, the build run, and the result surface. Verbs are never
/// authored here — the one thing the pane owns is whether a change ships
/// (<see cref="ModProject.BuildExcluded"/>). Emits into the published root, beside a zip.
///
/// <para>It reports on its OWN channel (<see cref="Footer"/>), not the mod-level status line every
/// save/open/autosave also writes: a build failure has to outlive a Ctrl+S.</para>
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The change list, grouped by subject.</summary>
    public ObservableCollection<BuildGroupVm> BuildGroups { get; } = new();
    /// <summary>False = empty, and <see cref="BuildEmptyText"/> stands in for the list.</summary>
    [ObservableProperty] private bool _hasBuildRows;
    [ObservableProperty] private string _buildEmptyText = "";
    /// <summary>Where a build lands, relative to the published root ("published\{package}\").</summary>
    [ObservableProperty] private string _buildDestination = "";
    /// <summary>The same destination in full, for the tooltip.</summary>
    [ObservableProperty] private string _buildDestinationTip = "";
    /// <summary>Ticks are refused while a refresh or a build is in flight: a tick landing on a list being
    /// replaced, or on a run already reading the project, writes an exclusion nobody sees.</summary>
    [ObservableProperty] private bool _buildListEnabled = true;

    /// <summary>Why everything a run would race is off while it runs: the change list's controls, Build, and
    /// Install. One sentence in one place, so the three surfaces can't answer the same question
    /// differently.</summary>
    public const string BuildRunningReason = "A build is running.";

    /// <summary>Why the list's controls are off. Only read while <see cref="BuildListEnabled"/> is false.</summary>
    public string BuildListReason => IsModBuilding ? BuildRunningReason : BuildFooter.ReadingLine;
    /// <summary>Each list control's tooltip: what it does, or why it is off. One binding either way, so a
    /// disabled tick never sits under the pointer claiming it can still be clicked.</summary>
    public string BuildRowTickTip => BuildListEnabled ? RowTickTip : BuildListReason;
    public string BuildIncludeAllTip => BuildListEnabled ? IncludeAllTip : BuildListReason;
    public string BuildExcludeAllTip => BuildListEnabled ? ExcludeAllTip : BuildListReason;

    public const string RowTickTip = "Include this change in the build";
    public const string IncludeAllTip = "Include every change of this subject in the build";
    public const string ExcludeAllTip = "Leave every change of this subject out of the build";

    partial void OnBuildListEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(BuildListReason));
        OnPropertyChanged(nameof(BuildRowTickTip));
        OnPropertyChanged(nameof(BuildIncludeAllTip));
        OnPropertyChanged(nameof(BuildExcludeAllTip));
    }

    /// <summary>The last successful build's mod folder (empty = none for this project).</summary>
    [ObservableProperty] private string _lastBuildDir = "";
    /// <summary>The last successful build's zip (empty = the run built without one).</summary>
    [ObservableProperty] private string _lastZipPath = "";
    /// <summary>The published package's folder name, as the result bar reports it.</summary>
    [ObservableProperty] private string _builtPackage = "";
    /// <summary>"{n} MB · {n} files" for the last build, or empty when it couldn't be measured.</summary>
    [ObservableProperty] private string _buildSizeSummary = "";
    /// <summary>The last build's folder no longer matches what the list would ship. The result bar STAYS —
    /// the folder it names is still on disk and still installable — and says so on its own line.</summary>
    [ObservableProperty] private bool _buildResultStale;
    /// <summary>The last install's folder inside the Mods folder (empty = none this session).</summary>
    [ObservableProperty] private string _lastInstallPath = "";
    [ObservableProperty] private bool _isModBuilding;
    [ObservableProperty] private bool _hasBuildLog;

    /// <summary>The one line a stale result carries.</summary>
    public const string StaleResultLine = "The last build no longer matches this list. Build again.";

    /// <summary>What else the whole-mod key switches, or empty when it stands alone. The ⚠ beside the key
    /// control reads it; see <see cref="BuildRowVm.KeyCollisionTip"/> for the per-change half.</summary>
    [ObservableProperty] private string _packageKeyCollisionTip = "";
    public bool HasPackageKeyCollision => PackageKeyCollisionTip.Length > 0;

    partial void OnPackageKeyCollisionTipChanged(string value) =>
        OnPropertyChanged(nameof(HasPackageKeyCollision));

    /// <summary>The pane's footer line and its state — progress, result, failure and the ready summary, in
    /// one place whose transitions are pure.</summary>
    [ObservableProperty] private BuildFooter _footer = BuildFooter.Idle;

    /// <summary>User-facing: the authored edit won't show or will look wrong, and there is something the
    /// author can do about it. The live derivation's and a completed run's stand together
    /// (<see cref="BuildWarningSource"/>). The build's diagnostics never come here — they go to the build
    /// log alone.</summary>
    public ObservableCollection<string> BuildWarnings { get; } = new();

    /// <summary>User-facing disclosures with nothing to fix: an edit's reach beyond the picked subject and
    /// how the run scoped it. A completed run owns this surface alone — nothing outside a build produces a
    /// disclosure.</summary>
    public ObservableCollection<string> BuildInfos { get; } = new();

    /// <summary>Why the Build action is off, or null when it can run. A failed build is NOT a reason — the
    /// way out is another build. A run in flight outranks every other reason: the tooltip says it rather
    /// than report readiness under a dead button.</summary>
    public string? BuildDisabledReason => IsModBuilding
        ? BuildRunningReason
        : BuildGate.Reason(_vfs is not null, _derivationFailure, CurrentCounts());
    public bool CanBuildMod => !IsModBuilding && BuildDisabledReason is null;
    /// <summary>What Build does, or why it's off (shown on the disabled button).</summary>
    public string BuildButtonTip => BuildDisabledReason ?? BuildGate.Ready;

    /// <summary>Why the Install action is off, or null when it can run. The button and the reason it shows
    /// on hover are the same answer, so a disabled Install always says what to do about it.</summary>
    public string? InstallDisabledReason => IsModBuilding
        ? BuildRunningReason
        : InstallGate.Reason(HasLastBuild, _settings.MigotoLoaderExe, _loaderExeExists, _modsFolder,
            _loaderIni);
    public bool CanInstallBuild => !IsModBuilding && InstallDisabledReason is null;
    public string InstallButtonTip => InstallDisabledReason ?? InstallGate.Ready;

    /// <summary>Whether the configured 3DMigoto install can take a mod at all. False and the pane offers the
    /// way to SET the path where Install would be: a disabled Install is the right answer for "not built
    /// yet", but for a loader nobody has pointed at it is a dead end with the remedy two menus away.</summary>
    public bool HasMigotoLoader => LoaderProblem is null;
    public bool NeedsMigotoLoader => !HasMigotoLoader;

    /// <summary>Why the configured 3DMigoto can't take a mod, or null when it can — ONE reading behind the
    /// stand-in button's label, its tip and the Install gate itself.</summary>
    private string? LoaderProblem =>
        InstallGate.LoaderReason(_settings.MigotoLoaderExe, _loaderExeExists, _modsFolder, _loaderIni);

    /// <summary>Whether a path has been set at all. Blank is the untouched state; anything else is a path
    /// the modder chose, which is a different thing to say about it.</summary>
    private bool LoaderPathSet => !string.IsNullOrWhiteSpace(_settings.MigotoLoaderExe);

    /// <summary>What the stand-in button says. A path nobody has set is a step not taken; a path that IS
    /// set and can't take a mod is something to go back to, and the two read differently on the button.</summary>
    public string SetMigotoPathLabel => LoaderPathSet ? "Fix 3DMigoto path" : "Set 3DMigoto path";

    /// <summary>What the stand-in button says on hover: which file to pick while nothing is set, and what is
    /// wrong with the one that is — the modder who already pointed at a 3DMigoto needs the diagnosis, not
    /// the instruction they already followed.</summary>
    public string SetMigotoPathTip => LoaderPathSet ? LoaderProblem ?? InstallGate.SetLoader
        : InstallGate.SetLoader;

    public const string InstallConflictTitle = "Replace conflicting mod?";

    public bool HasLastBuild => LastBuildDir.Length > 0;
    public bool HasLastInstall => LastInstallPath.Length > 0;
    public bool HasBuildZip => LastZipPath.Length > 0;
    public bool HasBuildSize => BuildSizeSummary.Length > 0;
    public bool HasBuildWarnings => BuildWarnings.Count > 0;
    public bool HasBuildInfos => BuildInfos.Count > 0;
    public string BuildLogTip => HasBuildLog ? "The full build transcript" : "No build log yet";

    private string _buildLogPath = "";
    /// <summary>The blocked-derivation line, else null. The Build gate's reason and the footer's ⛔ are the
    /// same value.</summary>
    private string? _derivationFailure;
    /// <summary>The last derivation's warnings — shown while no run owns the list, so the Edit pane's
    /// "details on the Build page" promise is true before a build.</summary>
    private List<string> _derivationWarnings = new();
    /// <summary>The last completed run's warnings; null = no run owns the list.</summary>
    private List<string>? _runWarnings;
    /// <summary>The last completed run's disclosures; null = no run owns the surface.</summary>
    private List<string>? _runInfos;
    /// <summary>Refresh stamp: a completing refresh whose stamp is stale drops its result rather than
    /// painting a list nobody asked for.</summary>
    private int _refreshGeneration;

    partial void OnLastBuildDirChanged(string value)
    {
        OnPropertyChanged(nameof(HasLastBuild));
        RaiseModsFolderGates();
        // a landed result takes the baseline its own RUN captured, not the list as it stands now: the Edit
        // pane is live through a build, so a mid-build edit can move the list before the result arrives. A
        // dropped result leaves no baseline. The line is re-read here too, or a result replaced while the
        // previous one was stale keeps that line.
        _builtSignature = value.Length > 0 ? _pendingSignature : null;
        RefreshBuildResultStale();
    }
    partial void OnLastZipPathChanged(string value) => OnPropertyChanged(nameof(HasBuildZip));
    partial void OnLastInstallPathChanged(string value) => OnPropertyChanged(nameof(HasLastInstall));
    partial void OnIsModBuildingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanBuildMod));
        OnPropertyChanged(nameof(BuildListReason));
        // Build's own reason and tip stand on this flag too; the Install pair rides the raiser below, which
        // re-raises both halves of the Mods-folder gate.
        OnPropertyChanged(nameof(BuildDisabledReason));
        OnPropertyChanged(nameof(BuildButtonTip));
        RaiseModsFolderGates();
    }
    partial void OnBuildSizeSummaryChanged(string value) => OnPropertyChanged(nameof(HasBuildSize));
    partial void OnHasBuildLogChanged(bool value) => OnPropertyChanged(nameof(BuildLogTip));

    /// <summary>Re-raise the Build gate after anything that changes what would ship.</summary>
    private void RaiseBuildGate()
    {
        OnPropertyChanged(nameof(BuildDisabledReason));
        OnPropertyChanged(nameof(BuildButtonTip));
        OnPropertyChanged(nameof(CanBuildMod));
    }

    /// <summary>(character, stem) → live subject — the same prefab-first route the workbench tree uses, and
    /// through the same memo, so entering this step re-reads the game only for a subject nothing has built
    /// yet.</summary>
    private SubjectModel? ResolveSubjectForBuild(string character, string stem)
    {
        var catalog = _vfs?.Catalog;
        if (catalog is null) return null;
        var outfit = RosterLookup.FindOutfit(_roster, character, stem);
        if (outfit is null) return null;
        try
        {
            return _subjectModels.GetOrBuild(character, outfit.Stem,
                () => SubjectModelBuilder.Build(catalog, TryDeobfuscateBundle, outfit, character));
        }
        catch { return null; }
    }

    /// <summary>The outfit label the Edit pane shows, falling back to the raw stem.</summary>
    private string BuildOutfitLabel(string character, string stem)
    {
        var o = RosterLookup.FindOutfit(_roster, character, stem);
        return o is null ? stem : FriendlyNames.KindAndLabel(o);
    }

    /// <summary>Entering the step: start from the current workbench state. The result bar STAYS across a hop
    /// to another step — the folder it names is still on disk and still installable — and its stale line is
    /// re-read against the list as it now stands. The result itself drops only on a project switch and at the
    /// start of a new run.</summary>
    public void EnterBuildStep()
    {
        Footer = Footer.Cleared();
        RaiseModsFolderGates();   // the loader may have been set, or moved, since the last visit
        // read the line off the list already on screen, so the bar is right through the refresh below
        RefreshBuildResultStale();
        RefreshBuildPreview();
    }

    /// <summary>Switching project: result, log, list and line all go — they would otherwise describe files
    /// this project never built.</summary>
    private void ResetBuildPane()
    {
        ClearBuildResult();
        BuildGroups.Clear();
        HasBuildRows = false;
        BuildEmptyText = "";
        BuildDestination = "";
        BuildDestinationTip = "";
        BuildListEnabled = true;
        HasBuildLog = false;
        _buildLogPath = "";
        _derivationFailure = null;
        _derivationWarnings = new List<string>();
        PackageKeyCollisionTip = "";
        LastInstallPath = "";
        _refreshGeneration++;   // any refresh still in flight belongs to the mod we're leaving
        Footer = Footer.Cleared();
        ShowWarnings();
        ShowInfos();
        RaiseBuildGate();
    }

    /// <summary>Drop the result surface entirely: a project switch and the start of a run. A step hop keeps
    /// it, and so does an edit that only makes it STALE — see <see cref="RefreshBuildResultStale"/>.</summary>
    private void ClearBuildResult()
    {
        LastBuildDir = "";
        LastZipPath = "";
        BuiltPackage = "";
        BuildSizeSummary = "";
        BuildResultStale = false;
        _runWarnings = null;
        _runInfos = null;
        ShowWarnings();
        ShowInfos();
    }

    /// <summary>Re-read whether the result bar still describes what the list would ship: the signature now
    /// against the one the last build consumed. The bar keeps its actions either way — the folder it names
    /// is still on disk and still installable — and only the line comes and goes, so an input put back the
    /// way the build found it takes the line back off again.</summary>
    internal void RefreshBuildResultStale() =>
        BuildResultStale = HasLastBuild && CurrentBuildSignature() != _builtSignature;

    /// <summary>What the build owning the result bar consumed, as a signature; null = no build owns the
    /// bar. Committed from <see cref="_pendingSignature"/> where the result lands.</summary>
    private string? _builtSignature;

    /// <summary>The signature the run in flight took as it started reading the list, or null before the
    /// first run of a session.</summary>
    private string? _pendingSignature;

    /// <summary>Take the baseline the run about to start will be measured against. Captured at the START of
    /// a run because the Edit pane stays live through a build: capturing where the result lands would
    /// describe a list the run never read, and a mid-build edit would leave the ✓ bar reading clean.</summary>
    internal void CaptureBuildBaseline() => _pendingSignature = CurrentBuildSignature();

    /// <summary>Everything a build reads that this pane can change while a result is on screen: the mod
    /// identity, every listed change with its tick and its whole key binding, and the MAPS each change
    /// would ship. Excluded rows are IN it — re-ticking one is a real difference from what shipped.
    /// Separators are control characters, so no field value can imitate one. Row ORDER counts: derivation
    /// order is deterministic, so two readings differ in order only when the authored list itself moved.</summary>
    private string CurrentBuildSignature()
    {
        const char field = (char)0x1f, record = (char)0x1e;
        var (name, version, author, description, modKey) = IdentityForm();
        var sb = new StringBuilder()
            .Append(name).Append(field).Append(version).Append(field).Append(author).Append(field)
            .Append(description).Append(field).Append(modKey).Append(record);
        foreach (var row in BuildGroups.SelectMany(g => g.Rows))
            sb.Append(row.Character).Append(field).Append(row.Outfit).Append(field)
                .Append(row.Mesh).Append(field).Append(row.Verb).Append(field)
                .Append(row.IsIncluded ? '1' : '0').Append(field)
                .Append(ModKeys.Normalize(row.ToggleKey)).Append(field)
                .Append(row.HideWhenOff ? '1' : '0').Append(field)
                .Append(row.StartsOff ? '1' : '0').Append(field)
                .Append(DonorMapAsks(row)).Append(record);
        return sb.ToString();
    }

    /// <summary>The maps a change row's replacement would ship, read off the PROJECT rather than the row:
    /// an adoption or a card drop can author a donor slot on a mesh whose row was derived before it had any,
    /// and a row that carried its own snapshot would report that as no change at all. Only a Replace ships
    /// donor maps; a retexture's own files are named by the rows the derivation built from them.
    ///
    /// <para>The target is picked on the SAME predicate the derivation and the adoption pick theirs on —
    /// present and edited, not merely named the same. A subject can hold more than one target for one mesh
    /// slot, and reading a stale or absent one would sign the build with maps it is not going to ship.</para></summary>
    private string DonorMapAsks(BuildRowVm row)
    {
        if (row.Verb != EditVerbs.Replace) return "";
        var target = _project.Targets.FirstOrDefault(t => t.AssetType == "Mesh"
            && string.Equals(t.ObjectName, row.Mesh, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.SubjectCharacter, row.Character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(t.SubjectOutfit, row.Outfit, StringComparison.OrdinalIgnoreCase)
            && _project.IsTargetPresent(t) && _project.IsEdited(t));
        return BuildRowVm.DonorMapSignature(target?.DonorTextures);
    }

    /// <summary>Recompute the change list off-thread. A derivation failure takes the footer instead of
    /// masquerading as a preview, and leaves the last good list on screen; the list is untickable until
    /// this lands.</summary>
    public void RefreshBuildPreview()
    {
        RefreshPublishedName();
        if (_vfs is null) { ShowEmptyChangeList(BuildGate.GameUnavailable); return; }
        int generation = ++_refreshGeneration;
        var project = _project;
        BuildListEnabled = false;
        Footer = Footer.Reading();
        Task.Run(() =>
        {
            List<BuildGroupVm> groups;
            var warnings = new List<string>();
            try { groups = DeriveChangeList(project, warnings); }
            catch (Exception e)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsCurrentRefresh(generation, project)) return;
                    BlockBuild(e.Message);
                });
                return;
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsCurrentRefresh(generation, project)) return;
                _derivationFailure = null;
                BuildGroups.Clear();
                foreach (var g in groups) AddBuildGroup(g);
                HasBuildRows = groups.Count > 0;
                BuildEmptyText = BuildGate.NothingDerived;
                _derivationWarnings = warnings;
                ShowWarnings();
                ShowInfos();
                RefreshKeyCollisions();
                // the list under a result bar was just replaced, so whether the bar still matches it is a
                // fresh question — a run's own re-derivation lands here
                RefreshBuildResultStale();
                BuildListEnabled = !IsModBuilding;
                Footer = Footer.Derived(CurrentCounts());
                RaiseBuildGate();
            });
        });
    }

    /// <summary>A derivation that couldn't run: the last good list stays on screen — there is just nothing to
    /// build from it — and the footer and the Build gate give the same reason.</summary>
    private void BlockBuild(string message)
    {
        _derivationFailure = BuildFooter.BlockedLine(message);
        Footer = Footer.Blocked(message);
        BuildListEnabled = !IsModBuilding;
        RaiseBuildGate();
    }

    /// <summary>Take one derived group onto the list, wired to the pane's handlers. A group reaches the list
    /// only through here, so every row on screen writes its tick and its key back through the pane.</summary>
    internal void AddBuildGroup(BuildGroupVm group)
    {
        foreach (var row in group.Rows)
        {
            row.Toggled = OnBuildRowToggled;
            row.KeyBound = OnBuildRowKeyBound;
            row.EditRequested = OnBuildRowEditRequested;
        }
        BuildGroups.Add(group);
    }

    /// <summary>No newer refresh started, and the project it derived is still the open one.</summary>
    private bool IsCurrentRefresh(int generation, ModProject project) =>
        BuildRefresh.IsCurrent(generation, _refreshGeneration, project, _project);

    /// <summary>The list goes and a sentence stands in its place. Everything hung off the rows goes with
    /// them: a ⚠ naming a change no longer listed, and a stale line read against a list that no longer
    /// exists.</summary>
    private void ShowEmptyChangeList(string why)
    {
        BuildGroups.Clear();
        HasBuildRows = false;
        BuildEmptyText = why;
        BuildListEnabled = true;
        RefreshKeyCollisions();
        RefreshBuildResultStale();
        Footer = Footer.Derived(CurrentCounts());
        RaiseBuildGate();
    }

    /// <summary>The derived edits as change rows, grouped by subject in derivation order. Excluded entries
    /// are rows too — the pane is where "left out of this build" is visible at all. Part labels are the
    /// live roster's own tokens, the same identity the Edit tree shows.</summary>
    private List<BuildGroupVm> DeriveChangeList(ModProject project, IList<string> warnings)
    {
        var models = new Dictionary<(string, string), SubjectModel?>();
        SubjectModel? Resolve(string character, string stem)
        {
            var key = (character.ToLowerInvariant(), stem.ToLowerInvariant());
            if (models.TryGetValue(key, out var cached)) return cached;
            return models[key] = ResolveSubjectForBuild(character, stem);
        }

        var edits = VerbDerivation.DeriveAll(project, Resolve, warnings);
        var groups = new List<BuildGroupVm>();
        foreach (var e in edits)
        {
            var group = groups.FirstOrDefault(g =>
                string.Equals(g.Outfit, e.Outfit, StringComparison.OrdinalIgnoreCase)
                && string.Equals(g.RawCharacter, e.Character, StringComparison.OrdinalIgnoreCase));
            if (group is null)
                groups.Add(group = new BuildGroupVm
                {
                    Character = _friendly.Character(e.Character),
                    RawCharacter = e.Character,
                    Outfit = e.Outfit,
                    OutfitLabel = BuildOutfitLabel(e.Character, e.Outfit),
                });
            var part = Resolve(e.Character, e.Outfit)?.Parts
                .FirstOrDefault(p => string.Equals(p.SlotName, e.Mesh, StringComparison.OrdinalIgnoreCase));
            var binding = project.FindChangeKey(e.Character, e.Outfit, e.Mesh, e.Verb);
            group.Rows.Add(new BuildRowVm(e, part?.Token ?? e.Mesh,
                included: !project.IsBuildExcluded(e.Character, e.Outfit, e.Mesh, e.Verb),
                toggleKey: binding?.Key, hideWhenOff: binding?.HideWhenOff == true,
                startsOff: binding?.StartsOff == true));
        }
        return groups;
    }

    /// <summary>Persist a row's tick and re-count. No confirm — the change stays authored either way. The
    /// autosave outcome is captured BEFORE the recount, or a failed write is painted over by the counts
    /// it invalidated.</summary>
    private void OnBuildRowToggled(BuildRowVm row, bool included)
    {
        _project.SetBuildExcluded(row.Character, row.Outfit, row.Mesh, row.Verb, !included);
        var autosaveFailure = TryAutoSaveProject();
        foreach (var g in BuildGroups) g.RefreshCounts();
        // the ✓ bar still names a folder that exists; whether it still matches this list is re-read
        RefreshBuildResultStale();
        RefreshKeyCollisions();   // an untick takes its key out of the collision read
        Footer = autosaveFailure is null ? Footer.Ticked(CurrentCounts()) : Footer.Notice(autosaveFailure);
        RaiseBuildGate();
    }

    /// <summary>Persist a row's whole key binding: the built folder's ini is emitted from the keys, so the
    /// result's match with the list is re-read. Same autosave-then-report order as a tick.</summary>
    private void OnBuildRowKeyBound(BuildRowVm row)
    {
        _project.SetChangeKey(row.Character, row.Outfit, row.Mesh, row.Verb, row.ToggleKey,
            row.HideWhenOff, row.StartsOff);
        var autosaveFailure = TryAutoSaveProject();
        RefreshBuildResultStale();
        RefreshKeyCollisions();
        Footer = autosaveFailure is null ? Footer.Ticked(CurrentCounts()) : Footer.Notice(autosaveFailure);
    }

    /// <summary>The row's Edit button: hop to the Edit step and select this change's part there. A retexture
    /// is authored on a map card, so it asks for the material carrying that texture and falls back to the
    /// part. Entering the step rebuilds the tree off-thread, so the request is handed to the workbench,
    /// which applies it when its tree lands. A tree that doesn't carry the part says so there — the hop
    /// otherwise arrives with nothing selected and nothing said.</summary>
    private void OnBuildRowEditRequested(BuildRowVm row)
    {
        SelectedStep = "② Edit";
        Workbench.RequestSelectPart(row.Character, row.Outfit, row.Mesh, row.EditSubmesh, row.PartLabel);
    }

    /// <summary>Recompute which toggle keys are shared, and hang the answer on the controls that hold them:
    /// the whole-mod key first, then the SHIPPING rows — a change left out of the build binds nothing, so
    /// its key can't collide with anything. Rows that don't ship are cleared, or a row unticked out of a
    /// collision would keep the glyph it earned.</summary>
    private void RefreshKeyCollisions()
    {
        var rows = BuildGroups.SelectMany(g => g.Rows).ToList();
        // the group is carried alongside its rows: a row names itself by its subject, which only the group
        // holds as the list labels it
        var shipping = BuildGroups
            .SelectMany(g => g.Rows.Where(r => r.IsIncluded).Select(r => (Row: r, Group: g))).ToList();
        // the whole-mod key has no start-state control of its own: a keyed mod starts on
        var owners = new List<(string Label, string? Key, bool StartsOff)>
        {
            (KeyCollisions.WholeModLabel, PackageToggleKey, false),
        };
        owners.AddRange(shipping.Select(x => (
            KeyCollisions.OwnerLabel(x.Row.PartLabel, x.Row.Character, x.Group.OutfitLabel, x.Row.Verb),
            x.Row.ToggleKey, x.Row.StartsOff)));

        var tips = KeyCollisions.Tips(owners);
        PackageKeyCollisionTip = tips[0];
        foreach (var r in rows) r.KeyCollisionTip = "";
        for (int i = 0; i < shipping.Count; i++) shipping[i].Row.KeyCollisionTip = tips[i + 1];
    }

    /// <summary>What the change list would ship right now.</summary>
    private BuildReadyCounts CurrentCounts()
    {
        var rows = BuildGroups.SelectMany(g => g.Rows).ToList();
        return BuildReadyCounts.From(rows.Count, rows.Where(r => r.IsIncluded).Select(r => r.Verb));
    }

    /// <summary>How many warnings the box is rendering, lead-in excluded — what the Built footer counts, so
    /// the tally and the box under it describe the same set.</summary>
    private int _shownWarningCount;

    /// <summary>Both sources at once: a completed run's warnings under their own lead-in, then the live
    /// derivation's. <see cref="BuildWarningSource"/> owns which line belongs where and what collapses.</summary>
    private void ShowWarnings()
    {
        var surface = BuildWarningSource.Current(_runWarnings, _derivationWarnings);
        BuildWarnings.Clear();
        foreach (var w in surface.Lines) BuildWarnings.Add(w);
        _shownWarningCount = surface.Count;
        RaiseBuildMessageCounts();
    }

    /// <summary>The completed run's disclosures, identical sentences collapsed to one — a run reaches the
    /// same fact once per material map or once per claimant, and the list would otherwise repeat itself.</summary>
    private void ShowInfos()
    {
        BuildInfos.Clear();
        foreach (var i in (_runInfos ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal))
            BuildInfos.Add(i);
        RaiseBuildMessageCounts();
    }

    /// <summary>Build the mod folder. Saves FIRST — the build reads the persisted workspace — then runs
    /// off-thread with progress on the pane's footer.</summary>
    [RelayCommand]
    private async Task BuildModAsync()
    {
        if (_vfs is null) { Footer = Footer.Failed(BuildGate.GameUnavailable); return; }
        var (ok, reason) = TrySaveProject();
        if (!ok) { Footer = Footer.Failed($"couldn't save the mod. {reason}"); return; }
        CaptureBuildBaseline();   // what THIS run consumes; the Edit pane can move the list while it runs
        string outRoot = _settings.ResolvedPublishedRoot;
        // The log is named for the package this run produces, so two mods never overwrite each other's
        // transcript. Known before the build so a failed run still files its log under the right name.
        string logPackage = PublishedPackageName();
        var logLines = new List<string>();
        IsModBuilding = true;
        BuildListEnabled = false;
        // Everything past the busy flags runs inside the try, so a throw from the setup lands on the same
        // catch/finally a failed run does and the pane never keeps reporting a run that stopped.
        try
        {
            // the previous result is no longer what the folder holds
            ClearBuildResult();
            var project = _project;
            var vfs = _vfs;
            // the sharing measurement decides edit scope; a build waits for whatever of it is still running.
            // A faulted/cancelled measurement builds unscoped, which the build's own output says.
            //
            // The wait line comes BEFORE "Building…" and is the status cell's own, streamed word for word: the
            // same work must not read as two different things in two places, and the count is what says the
            // wait is finite. A pass with nothing to re-read reports no line and the wait is a blink.
            SharingIndex? sharing = null;
            if (_sharingTask is { } sharingTask)
            {
                if (!sharingTask.IsCompleted) { _buildWaitingOnSharing = true; RefreshBackgroundStatus(); }
                try { sharing = await sharingTask; } catch { }
                _buildWaitingOnSharing = false;
            }
            Footer = Footer.Streaming("Building…");
            var env = new BuildEnv(
                ResolveSubjectForBuild,
                a => vfs.Catalog.ResolveAddress(a),
                TryDeobfuscateBundle,
                vfs.CatalogVersion,
                typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(),
                sharing);
            var result = await Task.Run(() => ModBuilder.Build(project, env, outRoot, msg =>
            {
                lock (logLines) logLines.Add(msg);
                Dispatcher.UIThread.Post(() => Footer = Footer.Streaming(msg));
            }, zip: true, BuildCaches.Default, _settings.EncoderCpuLimit));
            LastBuildDir = result.OutDir;
            LastZipPath = result.ZipPath ?? "";
            BuiltPackage = Path.GetFileName(result.OutDir);
            logPackage = BuiltPackage;   // the folder that actually landed, whatever the form now says
            _runWarnings = result.Warnings.ToList();
            _runInfos = result.Infos.ToList();
            ShowWarnings();
            ShowInfos();
            BuildSizeSummary = await Task.Run(() => BuildOutputSummary(result.OutDir));
            // the count is what the box above it renders — the union, collapsed — not the run's raw list: a
            // footer promising more warnings than the box shows sends the modder looking for lines that
            // were only ever duplicates
            Footer = Footer.Built(BuiltPackage, _shownWarningCount);
            WriteBuildLog(outRoot, logPackage, logLines, result.Warnings, result.Infos, result.Diagnostics, null);
        }
        catch (Exception e)
        {
            WriteBuildLog(outRoot, logPackage, logLines, Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), e.ToString());
            Footer = Footer.Failed(e.Message);
        }
        finally
        {
            // no run is parked on the measurement any more, so its line stops being re-streamed here
            _buildWaitingOnSharing = false;
            _buildLogPath = BuildLogPath(outRoot, logPackage);
            HasBuildLog = File.Exists(_buildLogPath);
            IsModBuilding = false;
            TakeHeldAdoptions();   // edits made while the run read the project, applied before the list is re-read
            RefreshBuildPreview();
            RunQueuedRescan();   // this run was one of the holds a rescan waits behind
        }
    }

    private void RaiseBuildMessageCounts()
    {
        OnPropertyChanged(nameof(HasBuildWarnings));
        OnPropertyChanged(nameof(HasBuildInfos));
    }

    /// <summary>The published folder name for the CURRENT identity form — the same name the build's
    /// save-then-emit produces, so the destination line names the folder that will appear.</summary>
    private string PublishedPackageName() => ModNaming.PackageFolderName(
        ProjectName, PackageAuthor, string.IsNullOrWhiteSpace(PackageVersion) ? "1.0" : PackageVersion);

    /// <summary>Re-run the naming rule after an identity edit. The destination line under the Build action
    /// is the one place the published folder name appears, so it is re-read on every keystroke in the
    /// Name/Author/Version fields: what the modder types and what the build writes can never disagree.</summary>
    private void RefreshPublishedName()
    {
        var package = PublishedPackageName();
        BuildDestination = $@"published\{package}\";
        BuildDestinationTip = Path.Combine(_settings.ResolvedPublishedRoot, package);
    }

    /// <summary>"{n} MB · {n} files", whole MB rounded AWAY from zero so a build never reads smaller than
    /// it is. Empty when the folder can't be walked.</summary>
    public static string BuildOutputSummary(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            long bytes = files.Sum(f => new FileInfo(f).Length);
            long mb = (long)Math.Round(bytes / 1048576.0, MidpointRounding.AwayFromZero);
            return $"{mb} MB · {files.Length} file{(files.Length == 1 ? "" : "s")}";
        }
        catch { return ""; }
    }

    /// <summary>The build transcript's path: <c>{package}.build.log</c> beside the published package rather
    /// than inside it — one file per mod, so two mods' logs never overwrite each other, and the log is not
    /// carried along by an Install of the folder.</summary>
    private static string BuildLogPath(string outRoot, string package) =>
        Path.Combine(outRoot, package + ".build.log");

    /// <summary>The full transcript, at <see cref="BuildLogPath"/>: the pane shows the warnings, this file
    /// keeps the detail. <paramref name="diagnostics"/> land here and nowhere else — this is their only
    /// surface.</summary>
    private static void WriteBuildLog(string outRoot, string package, IReadOnlyList<string> log,
        IReadOnlyList<string> warnings, IReadOnlyList<string> infos,
        IReadOnlyList<string> diagnostics, string? failure)
    {
        try
        {
            Directory.CreateDirectory(outRoot);
            var lines = new List<string>(log);
            foreach (var w in warnings) lines.Add($"warning: {w}");
            foreach (var i in infos) lines.Add($"info: {i}");
            foreach (var d in diagnostics) lines.Add($"diag: {d}");
            if (failure is not null) lines.Add($"FAILED: {failure}");
            File.WriteAllLines(BuildLogPath(outRoot, package), lines);
        }
        catch { /* the log is best-effort — never fail a build over it */ }
    }

    /// <summary>Copy the built folder into the Mods folder beside the configured loader, replacing a same-named
    /// folder there. Any OTHER installed mod on the same hashes is reported by name first and left in place
    /// whichever way the confirm goes: which of two mods on one character wins is the installing modder's
    /// call, not this app's. An OLDER VERSION of this same mod is a different question — it is this mod, so
    /// it is offered for removal, and removed only once the new folder is in place.</summary>
    [RelayCommand]
    private async Task InstallBuildAsync()
    {
        if (!CanInstallBuild) return;
        string mods = _modsFolder!;
        string built = LastBuildDir, package = BuiltPackage;
        try
        {
            var scan = await Task.Run(() => ModInstall.ScanConflicts(built, mods));
            var prior = scan.PriorVersions;
            if (prior.Count > 0)
            {
                if (MainWindow is not { } owner) return;
                if (!await ConfirmWindow.Show(owner, ModInstall.PriorVersionTitle(prior),
                        ModInstall.PriorVersionBody(prior), "Install", "Cancel"))
                    return;
            }
            if (scan.Conflicts.Count > 0)
            {
                if (MainWindow is not { } owner) return;
                if (!await ConfirmWindow.Show(owner, InstallConflictTitle,
                        ModInstall.ConflictBody(scan.Conflicts), "Install", "Cancel"))
                    return;
            }
            var outcome = await Task.Run(() => ModInstall.Install(built, mods));
            LastInstallPath = outcome.InstalledDir;
            // the older versions go only now: a failed install must never cost the copy already installed
            var kept = await Task.Run(() => RemovePriorVersions(mods, prior));
            Footer = Footer.Notice(InstallLine(package, outcome.LeftBehind, kept));
        }
        catch (ModInstall.InstallFailedException e)
        {
            Footer = Footer.InstallFailed(e.Message, e.FolderState);
        }
        catch (Exception e)
        {
            Footer = Footer.InstallFailed(e.Message, ModInstall.InstallFailedException.FolderUntouched);
        }
    }

    /// <summary>Remove the older versions the confirm accepted, returning the ones that wouldn't go. A
    /// folder that refuses to delete is REPORTED: it still holds the same hashes, so it fights the copy just
    /// installed.</summary>
    private static IReadOnlyList<string> RemovePriorVersions(string mods, IReadOnlyList<string> prior)
    {
        var kept = new List<string>();
        foreach (var folder in prior)
        {
            try { ModInstall.RemoveInstalled(mods, folder); }
            catch { kept.Add(folder); }
        }
        return kept;
    }

    /// <summary>The line one install reports: what landed, and anything still in the Mods folder that will
    /// fight it.</summary>
    private static string InstallLine(string package, string? leftBehind, IReadOnlyList<string> kept)
    {
        var line = $"Installed {package} to the Mods folder.";
        foreach (var folder in kept.Concat(leftBehind is null ? Array.Empty<string>() : new[] { leftBehind }))
            line += $" {folder} is still there. Delete it by hand.";
        return line;
    }

    /// <summary>Reveal the last built mod folder — drop it into a 3DMigoto <c>Mods\</c> to run it.</summary>
    [RelayCommand]
    private void OpenBuildFolder()
    {
        if (!HasLastBuild) return;
        if (!Directory.Exists(LastBuildDir))
        { Footer = Footer.Notice("Build folder is gone. Rebuild first."); return; }
        try { Process.Start(new ProcessStartInfo { FileName = LastBuildDir, UseShellExecute = true }); }
        catch { /* explorer refusal is not a build problem */ }
    }

    /// <summary>Reveal the installed folder inside the Mods folder — where the game reads it from.</summary>
    [RelayCommand]
    private void ShowInstalledFolder()
    {
        if (!HasLastInstall) return;
        if (!Directory.Exists(LastInstallPath))
        { Footer = Footer.Notice("Installed folder is gone. Install again."); return; }
        try { Process.Start(new ProcessStartInfo { FileName = LastInstallPath, UseShellExecute = true }); }
        catch { /* explorer refusal is not an install problem */ }
    }

    /// <summary>Reveal the last build's zip — the file to hand someone.</summary>
    [RelayCommand]
    private void ShowBuildZip()
    {
        if (!HasBuildZip) return;
        if (!File.Exists(LastZipPath))
        { Footer = Footer.Notice("Build zip is gone. Rebuild first."); return; }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe", Arguments = $"/select,\"{LastZipPath}\"", UseShellExecute = true,
            });
        }
        catch { /* best-effort reveal */ }
    }

    /// <summary>Open this project's last transcript, success or failure.</summary>
    [RelayCommand]
    private void OpenBuildLog()
    {
        if (!HasBuildLog) return;
        if (!File.Exists(_buildLogPath))
        { Footer = Footer.Notice("Build log is gone. Rebuild first."); return; }
        try { Process.Start(new ProcessStartInfo { FileName = _buildLogPath, UseShellExecute = true }); }
        catch { /* a shell refusal is not a build problem */ }
    }
}
