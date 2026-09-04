using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using Remold.App.ViewModels.BuildPage;
using Remold.App.ViewModels.EditPage;
using Remold.Core;
using Remold.Core.Bundles;
using Remold.Core.Migoto;
using Remold.Core.Model;
using Remold.Core.Project;
using SixLabors.ImageSharp;
using Remold.Core.Tables;
using Remold.Core.Workbench;

namespace Remold.App.ViewModels;

/// <summary>The window's imperative half of ③ Build: current-install planning, publication, install, preview
/// files and dialogs. The page itself owns no second project model.</summary>
public partial class MainWindowViewModel : IBuildPageShell
{
    public BuildPageVm BuildPage { get; private set; } = null!;

    [ObservableProperty] private bool _isModBuilding;

    partial void OnIsModBuildingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsWorkInFlight));
    }

    public string? WholeModKey => PackageToggleKey;

    private int _buildPlanRuns;
    internal int BuildPlanRuns => Volatile.Read(ref _buildPlanRuns);

    public BuildPlanningResult PlanBuild(AuthoredProject? project, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _buildPlanRuns);
        cancellationToken.ThrowIfCancellationRequested();
        if (_vfs is not { } vfs) return new BuildPlanningResult(GameUnavailable: BuildGate.GameUnavailable);
        // Nothing authored yet: the gate's own empty-mod sentence answers, which is what an outline with no
        // edits says anyway.
        if (project is null) return new BuildPlanningResult();
        try
        {
            var env = NewBuildEnv(vfs);
            var plan = PlanAuthoredBuild(project, env, MaterialEvidenceFor(vfs), MeshEditGateFor(vfs),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            LogPlanDiagnostics(plan);
            return new BuildPlanningResult(Plan: plan);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // The reason a plan cannot be read at all is a diagnosis of this app's own read — a project the
            // validator refuses names slots and rows, and a defect names nothing at all. The line says what
            // failed; the exception itself goes to the log.
            AppLog.Write("Couldn't check this build", e);
            return new BuildPlanningResult(Failure: "Couldn't check this build.");
        }
    }

    /// <summary>The internal-consistency detail behind any plan line the page states plainly. Planning runs
    /// on every authored change, so each distinct diagnostic is written ONCE per launch: a standing guard
    /// would otherwise fill the log with one run's worth of the same line. A build writes them to its own
    /// log as well, where they sit beside the run that met them.</summary>
    private readonly HashSet<string> _loggedPlanDiagnostics = new(StringComparer.Ordinal);

    private void LogPlanDiagnostics(AuthoredBuildPlan plan)
    {
        lock (_loggedPlanDiagnostics)
            foreach (string diagnostic in plan.Diagnostics)
                if (_loggedPlanDiagnostics.Add(diagnostic))
                    AppLog.Write("Couldn't work out how to build part of this mod", diagnostic);
    }

    private static AuthoredBuildPlan PlanAuthoredBuild(AuthoredProject project, BuildEnv env,
        DerivedMaterialEvidence evidence, MeshEditGate meshGate, CancellationToken cancellationToken = default,
        Action<string>? note = null)
    {
        var resolver = new LegacyProjectResolver(env);
        // material-value evidence derives from the current install's own serialized reflection, so it
        // is exactly as current as the identity re-anchor it rides beside; copied source values read
        // the exact source material's serialized rows the same way. The mesh-edit gate judges an
        // authored geometry replacement against the exact current mesh, so a project that holds one on
        // a mesh the swap cannot take (the ② page refuses to author new ones) blocks at plan altitude.
        return AuthoredBuildPlanner.Plan(project,
            new ProductionAuthoredBuildBackend(part =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return resolver.ResolvePart(part);
                }, slot =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return evidence.Resolve(slot);
                },
                new MaterialSourceValueReader(env.Deobfuscate),
                slot =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return slot.Mesh is { } mesh
                        && meshGate.Blocked(mesh.LogicalBundle, mesh.Name ?? "", mesh.PathId) is { } why
                            ? PartSkinGate.PlanRefusal(why) : null;
                }), cancellationToken);
    }

    public BuildLoaderState LoaderState()
    {
        ReadModsFolderState();
        return new BuildLoaderState(_settings.MigotoLoaderExe, _loaderExeExists, _modsFolder, _loaderIni);
    }

    public async Task<BuildRunResult> RunBuildAsync(IProgress<string> progress)
    {
        if (_vfs is not { } vfs)
            return Failed(BuildGate.GameUnavailable, "", "", -1, "none");
        var session = EditSession;
        var (saved, saveReason) = TrySaveProject();
        if (!saved)
            return Failed("Couldn't save the mod. " + saveReason, "", "", session.Revision, "none");

        long revision = session.Revision;
        var project = session.Snapshot();
        var preview = ReadPreview(project);
        string outRoot = _settings.ResolvedPublishedRoot;
        string package = ModNaming.PackageFolderName(project.Info);
        string logPath = BuildLogPath(outRoot, package);
        var logLines = new List<string>();
        IsModBuilding = true;
        try
        {
            SharingIndex? sharing = null;
            if (_sharingTask is { } sharingTask)
            {
                if (!sharingTask.IsCompleted)
                {
                    string waiting = BackgroundWorkLine(_sharingProgress);
                    progress.Report(waiting.Length == 0 ? "Waiting for the shared-asset check…" : waiting);
                }
                try { sharing = await sharingTask; }
                catch { logLines.Add("asset sharing unavailable; the build continues unscoped"); }
            }
            progress.Report("Building…");
            var env = NewBuildEnv(vfs, sharing, note => { lock (logLines) logLines.Add(note); });
            var evidence = MaterialEvidenceFor(vfs);
            var meshGate = MeshEditGateFor(vfs);
            var result = await Task.Run(() =>
            {
                var plan = PlanAuthoredBuild(project, env, evidence, meshGate,
                    note: note => { lock (logLines) logLines.Add(note); });
                lock (logLines) logLines.AddRange(plan.Diagnostics);
                var execution = AuthoredBuildExecution.Create(project, plan);
                return ModBuilder.Build(execution, env, outRoot, message =>
                {
                    lock (logLines) logLines.Add(message);
                    progress.Report(message);
                }, zip: true, BuildCaches.Default, _settings.EncoderCpuLimit);
            });
            package = Path.GetFileName(result.OutDir);
            logPath = BuildLogPath(outRoot, package);
            var warnings = result.Warnings.ToList();
            if (preview.Missing) warnings.Add(BuildPageVm.PreviewMissingWarning);
            warnings = warnings.Distinct(StringComparer.Ordinal).ToList();
            WriteBuildLog(logPath, logLines, warnings, result.Infos, result.Diagnostics, null);
            // A successful materialization is the authored-against boundary only while this project is still
            // open and the catalog is known. The eligible session change raises Changed (and therefore
            // autosaves) exactly like every other project transaction. The build used the pre-stamp revision;
            // only when nothing else moved while it ran may that metadata transaction join this result.
            long builtRevision = StampSuccessfulBuild(session, revision, vfs.CatalogVersion);
            return new BuildRunResult(true, null, result.OutDir, result.ZipPath, package, logPath,
                warnings, result.Infos, builtRevision, preview.Stamp);
        }
        catch (Exception e)
        {
            WriteBuildLog(logPath, logLines, Array.Empty<string>(), Array.Empty<string>(),
                BuildLogDiagnostics.From(e), e.ToString());
            // A refusal was written for the modder and is shown as it is. Everything else is a diagnosis of
            // the build's own machinery — an emitted id, a palette row, an exception's own words — and the
            // whole of it is already in the log this run just wrote.
            return Failed(e is AuthoredRefusalException ? e.Message : BuildFailedPlainly,
                logPath, package, revision, preview.Stamp);
        }
        finally
        {
            IsModBuilding = false;
            RunQueuedRescan();
        }

        static BuildRunResult Failed(string reason, string log, string package, long revision, string stamp) =>
            new(false, reason, "", null, package, log, Array.Empty<string>(), Array.Empty<string>(),
                revision, stamp);
    }

    /// <summary>What ③ says when a build stopped for a reason the build itself has no words for. It follows
    /// the footer's own "Build stopped:" lead, so it is a clause rather than a sentence of its own.</summary>
    internal const string BuildFailedPlainly = "this mod couldn't be built";

    /// <summary>Stamp only the project that is still open after a successful build. A switched-away project
    /// has no visible successful result to make current, and an unknown catalog cannot be a durable key.</summary>
    internal long StampSuccessfulBuild(AuthoredEditSession session, long builtRevision, string liveCatalog)
    {
        if (!ReferenceEquals(session, EditSession) || liveCatalog == GameInfo.UnknownVersion)
            return builtRevision;
        long revisionBeforeStamp = session.Revision;
        session.SetAuthoredAgainst(liveCatalog);
        RemoveNotice(AuthoredAgainstNoticeId);
        return revisionBeforeStamp == builtRevision ? session.Revision : builtRevision;
    }

    public async Task<BuildInstallResult> InstallBuildAsync(string builtDir, string package)
    {
        var loader = LoaderState();
        if (InstallGate.Reason(hasBuild: Directory.Exists(builtDir), loader) is { } blocked)
            return new BuildInstallResult(true, true, blocked);
        string mods = loader.ModsFolder!;
        string installLogPath = BuildLogPath(_settings.ResolvedPublishedRoot, package);
        void LogRetry(string line) => WriteBuildLog(installLogPath, new[] { line }, Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), null, append: true);
        try
        {
            var scan = await Task.Run(() => ModInstall.ScanConflicts(builtDir, mods));
            var prior = scan.PriorVersions;
            if (prior.Count > 0 && !await ConfirmAsync(ModInstall.PriorVersionTitle(prior),
                    ModInstall.PriorVersionBody(prior), "Install"))
                return new BuildInstallResult(false, false, "");
            if (scan.Conflicts.Count > 0 && !await ConfirmAsync("Install beside conflicting mods?",
                    ModInstall.ConflictBody(scan.Conflicts), "Install"))
                return new BuildInstallResult(false, false, "");

            var outcome = await Task.Run(() => ModInstall.Install(builtDir, mods, LogRetry));
            var kept = await Task.Run(() => RemovePriorVersions(mods, prior, LogRetry));
            return new BuildInstallResult(true, false, InstallLine(package, outcome.LeftBehind, kept),
                outcome.InstalledDir);
        }
        catch (ModInstall.InstallFailedException e)
        {
            return new BuildInstallResult(true, true, $"Install stopped: {e.Message} {e.FolderState}");
        }
        catch (Exception e)
        {
            return new BuildInstallResult(true, true,
                $"Install stopped: {e.Message} {ModInstall.InstallFailedException.FolderUntouched}");
        }
    }

    private static IReadOnlyList<string> RemovePriorVersions(string mods, IReadOnlyList<string> prior,
        Action<string>? log = null)
    {
        var kept = new List<string>();
        foreach (string folder in prior)
            try { ModInstall.RemoveInstalled(mods, folder, log); } catch { kept.Add(folder); }
        return kept;
    }

    private static string InstallLine(string package, string? leftBehind, IReadOnlyList<string> kept)
    {
        string line = $"Installed {package} to the Mods folder.";
        foreach (string folder in kept.Concat(leftBehind is null ? Array.Empty<string>() : new[] { leftBehind }))
            line += $" {folder} is still there. Delete it manually.";
        return line;
    }

    public async Task ChooseLoaderAsync()
    {
        if (MainWindow is not { } owner) return;
        var result = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Locate the 3DMigoto loader",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Programs") { Patterns = new[] { "*.exe" } } },
        });
        if (result.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        _settings.MigotoLoaderExe = path;
        SaveSettings();
        RaiseModsFolderGates();
    }

    public void OpenArtifact(BuildArtifactKind kind, string path)
    {
        bool exists = kind is BuildArtifactKind.Folder or BuildArtifactKind.InstalledFolder
            ? Directory.Exists(path) : File.Exists(path);
        // Written for the person reading them, so they reach the status line as they are: a refusal the
        // model raises is shown whole, and only a defect gets the action's own words.
        if (!exists) throw new AuthoredRefusalException(kind switch
        {
            BuildArtifactKind.Folder => "The build folder is gone. Build again.",
            BuildArtifactKind.Zip => "The build zip is gone. Build again.",
            BuildArtifactKind.Log => "The build log is gone. Build again.",
            _ => "The installed folder is gone. Install again.",
        });
        if (kind == BuildArtifactKind.Zip)
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true,
            });
        else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>The last preview file read, keyed by the file's own identity metadata. The stamp is a
    /// content hash and the dimensions a header read, and every board change re-reads the preview state —
    /// so both are recomputed only when the file itself moved under us (path, write time, or size), which
    /// outside of a picture change is never. An immutable instance, so a concurrent read sees a whole
    /// entry or none.</summary>
    private sealed record PreviewReadCache(string Full, long WriteTicks, long Length, string Stamp,
        int? Width, int? Height);
    private PreviewReadCache? _previewReadCache;

    public BuildPreviewState ReadPreview(AuthoredProject? project)
    {
        string? relative = project?.Info.Preview;
        if (string.IsNullOrWhiteSpace(relative)) return new BuildPreviewState(null, null, false, "none");
        if (project?.RootDir is not { } root)
            return new BuildPreviewState(relative, null, true, "missing:" + relative);
        try
        {
            string full = Path.GetFullPath(Path.Combine(root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            var file = new FileInfo(full);
            if (!file.Exists) return new BuildPreviewState(relative, full, true, "missing:" + relative);
            long ticks = file.LastWriteTimeUtc.Ticks;
            if (_previewReadCache is { } cached && string.Equals(cached.Full, full, StringComparison.Ordinal)
                && cached.WriteTicks == ticks && cached.Length == file.Length)
                return new BuildPreviewState(relative, full, false, cached.Stamp,
                    cached.Width, cached.Height);
            Size? size = null;
            try { size = Image.Identify(full)?.Size; } catch { }
            string stamp = PreviewStamp(relative, full);
            _previewReadCache = new PreviewReadCache(full, ticks, file.Length, stamp,
                size?.Width, size?.Height);
            return new BuildPreviewState(relative, full, false, stamp, size?.Width, size?.Height);
        }
        catch { return new BuildPreviewState(relative, null, true, "missing:" + relative); }
    }

    private static string PreviewStamp(string relative, string full)
    {
        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return relative + ":" + Convert.ToHexString(SHA256.HashData(stream));
        }
        catch { return "unreadable:" + relative; }
    }

    public Task<Bitmap?> LoadPreviewAsync(string path, int decodeWidth) => Task.Run<Bitmap?>(() =>
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Bitmap.DecodeToWidth(stream, decodeWidth);
    });

    public async Task<string?> PickPreviewAsync()
    {
        if (MainWindow is not { } owner) return null;
        var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a preview image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = BuildPageVm.PreviewExtensions.Select(extension => "*" + extension).ToArray(),
                },
            },
        });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    public void SetPreviewFrom(AuthoredEditSession session, string sourceFile)
    {
        var project = session.Snapshot();
        string root = project.RootDir ?? throw new InvalidOperationException(BuildPageVm.PreviewNeedsSave);
        string extension = Path.GetExtension(sourceFile).ToLowerInvariant();
        if (!BuildPageVm.PreviewExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(BuildPageVm.PreviewNotAnImage);
        string relative = "preview" + extension;
        string destination = Path.Combine(root, relative);
        string staged = destination + ".tmp";
        string? previous = project.Info.Preview;
        try
        {
            File.Copy(sourceFile, staged, overwrite: true);
            File.Move(staged, destination, overwrite: true);
            session.SetPreview(relative);
            if (previous is not null && !string.Equals(previous, relative, StringComparison.OrdinalIgnoreCase)
                && BuildPageVm.IsOwnedPreview(previous))
            {
                string old = Path.Combine(root, previous);
                if (File.Exists(old)) File.Delete(old);
            }
        }
        finally { try { if (File.Exists(staged)) File.Delete(staged); } catch { } }
    }

    public void RemovePreviewFile(AuthoredEditSession session, BuildPreviewState preview)
    {
        if (preview.RelativeFile is null) return;
        if (BuildPageVm.IsOwnedPreview(preview.RelativeFile) && preview.FullPath is { } full && File.Exists(full))
            File.Delete(full);
        session.SetPreview(null);
    }

    public void GoToEdit(EditRef edit)
    {
        SelectedStep = "② Edit";
        EditPage.SelectEdit(edit);
    }

    /// <summary>The full build world. Pool derivation depends on the wardrobe and timeline readers; the
    /// thinner resolver environment is not interchangeable with this one.</summary>
    internal BuildEnv NewBuildEnv(GameVfs vfs, SharingIndex? sharing = null, Action<string>? note = null)
    {
        string gameDir = GameDir;
        // Set by any reader below that DEGRADED rather than failed. The exact-build completion cache asks
        // it after the run: a build that leaned on a conservative fallback is real but never cacheable.
        bool readDegraded = false;
        var schemeLazy = new Lazy<Dictionary<string, IReadOnlyList<PartScheme.Slot>>?>(() =>
        {
            try { return SchemesByStem(gameDir, note); }
            catch (Exception ex)
            {
                readDegraded = true;
                note?.Invoke($"wardrobe tables unreadable, pools stay conservative: {ex.Message}");
                return null;
            }
        });
        IReadOnlyList<PartScheme.Slot>? PartSchemeFor(string stem) =>
            schemeLazy.Value is { } byStem && byStem.TryGetValue(stem, out var slots) ? slots : null;
        var timelineLazy = new Lazy<TimelineTemplates?>(() =>
        {
            try { return TimelineTemplates.Load(GameDatabase.FromGameDir(gameDir)); }
            catch (Exception ex)
            {
                readDegraded = true;
                note?.Invoke($"timeline tables unreadable, pools stay conservative: {ex.Message}");
                return null;
            }
        });
        IReadOnlyList<TimelineShoe>? TimelineShoesFor(string stem)
        {
            if (timelineLazy.Value is not { } templates) return null;
            var shoes = TimelineShoes.Read(vfs.Catalog, TryDeobfuscateBundle,
                templates.AddressesFor(stem), out int unreadable);
            if (unreadable > 0)
            {
                readDegraded = true;
                note?.Invoke($"{stem}: {unreadable} timeline bundle(s) resolved but could not be read, "
                    + "so their overrides demoted nothing; close the game for a full pass");
            }
            return shoes;
        }
        return new BuildEnv(
            ResolveSubjectForBuild,
            address => vfs.Catalog.ResolveAddress(address),
            TryDeobfuscateBundle,
            vfs.CatalogVersion,
            typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(),
            sharing,
            PartSchemeFor: PartSchemeFor,
            TimelineShoesFor: TimelineShoesFor,
            BundleContentHash: BundleReads.BundleContentHashLookup(vfs.Catalog, vfs.Manifest),
            CatalogIdentity: vfs.CatalogIdentity,
            ReadDegraded: () => readDegraded);
    }

    private SubjectModel? ResolveSubjectForBuild(string character, string stem)
    {
        if (_subjectModels.TryGet(character, stem) is { } hit) return hit;
        var catalog = _vfs?.Catalog;
        if (catalog is null) return null;
        var outfit = RosterLookup.FindOutfit(_roster, character, stem);
        if (outfit is null) return null;
        try
        {
            var model = _subjectModels.GetOrBuild(character, outfit.Stem,
                () => SubjectModelBuilder.Build(catalog, TryDeobfuscateBundle, outfit, character));
            SubjectModelWarmCompleted();
            return model;
        }
        catch { return null; }
    }

    private static string BuildLogPath(string outRoot, string package) =>
        Path.Combine(outRoot, package + ".build.log");

    private static void WriteBuildLog(string path, IReadOnlyList<string> log,
        IReadOnlyList<string> warnings, IReadOnlyList<string> infos,
        IReadOnlyList<string> diagnostics, string? failure, bool append = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = new List<string>(log);
            lines.AddRange(warnings.Select(line => "warning: " + line));
            lines.AddRange(infos.Select(line => "info: " + line));
            lines.AddRange(diagnostics.Select(line => "diag: " + line));
            if (failure is not null) lines.Add("FAILED: " + failure);
            if (append) File.AppendAllLines(path, lines);
            else File.WriteAllLines(path, lines);
        }
        catch { }
    }
}
