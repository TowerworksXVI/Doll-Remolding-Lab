using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remold.Core;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Textures;
using Remold.Core.Workbench;

namespace Remold.App.ViewModels;

/// <summary>The cache-only, speculative half of the Blender open optimization.</summary>
public partial class MainWindowViewModel
{
    internal readonly record struct RiggedGlbPrewarmProgress(int Done, int Total);

    internal enum RiggedGlbPrewarmOutcome
    {
        Ready,
        Skipped,
        CacheFailure,
    }

    /// <summary>One independently cancellable subject. The production closure captures only immutable
    /// install/model facts and cache paths; the scheduler seam lets lifecycle tests hold work without
    /// constructing a game install.</summary>
    internal readonly record struct RiggedGlbPrewarmWork(Func<CancellationToken, RiggedGlbPrewarmOutcome> Run);

    private CancellationTokenSource? _riggedGlbPrewarmCts;
    private Task _riggedGlbPrewarmTask = Task.CompletedTask;
    private int _riggedGlbPrewarmRunning;
    private int _interactiveRiggedGlbOpen;
    private RiggedGlbPrewarmProgress? _riggedGlbPrewarmProgress;
    private bool _riggedGlbPrewarmFailed;

    internal Task RiggedGlbPrewarmTask => _riggedGlbPrewarmTask;
    internal CancellationTokenSource? RiggedGlbPrewarmCts => _riggedGlbPrewarmCts;
    internal bool RiggedGlbPrewarmRunning => Volatile.Read(ref _riggedGlbPrewarmRunning) > 0;

    internal const string RiggedGlbPrewarmTip =
        "Getting this mod's parts ready so they open in Blender faster.";
    internal const string RiggedGlbPrewarmUnavailable = "Blender opens will be slower";
    internal const string RiggedGlbPrewarmUnavailableDetail =
        "Couldn't get this mod's parts ready in the background. Opening in Blender still works.";

    // The cell trims at 180px, and a line longer than that loses its own counter to the ellipsis — the
    // counter is the living half, so the line stays short enough to keep it. Blender is the tooltip's job.
    internal static string RiggedGlbPrewarmLine(RiggedGlbPrewarmProgress progress) =>
        $"Preparing parts… {progress.Done}/{progress.Total}";

    /// <summary>Snapshot the selected subjects that can currently be answered by this install. No model is
    /// built here: the existing model warm owns that read and calls back when the whole selection has landed.
    /// A static-only subject has no subject-level Blender open and therefore no route to prewarm.</summary>
    internal void TryStartRiggedGlbPrewarm()
    {
        if (IsScanning || _rescanAfterScan || Volatile.Read(ref _interactiveRiggedGlbOpen) > 0
            || _vfs is not { } vfs || GameDir is not { Length: > 0 } gameDir)
            return;

        var document = _projectDocument;
        string cacheRoot;
        RiggedGlbCache cache;
        try
        {
            cacheRoot = CacheRootFor();
            cache = RiggedGlbCacheAt(LabPaths.RiggedGlbRootIn(cacheRoot));
        }
        catch (Exception e)
        {
            AppLog.Write("The Blender prewarm cache couldn't be reached", e);
            CancelRiggedGlbPrewarm(clearFailure: false);
            _riggedGlbPrewarmFailed = true;
            RefreshBackgroundStatus();
            return;
        }

        var work = new List<RiggedGlbPrewarmWork>();
        foreach (var entry in CurrentSelection()
                     .DistinctBy(value => (value.Character.ToUpperInvariant(), value.Outfit.ToUpperInvariant())))
        {
            if (_subjectModels.TryGet(entry.Character, entry.Outfit) is not { } model
                || model.AllPartsStatic
                || PickOutfit(entry.Character, entry.Outfit) is not { } outfit)
                continue;

            string character = entry.Character;
            work.Add(new RiggedGlbPrewarmWork(token =>
            {
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(document, _projectDocument) || !ReferenceEquals(vfs, _vfs))
                    throw new OperationCanceledException(token);
                try
                {
                    var (roster, wardrobeUnreadable) = ExportRoster(vfs, gameDir, model);
                    token.ThrowIfCancellationRequested();
                    if (wardrobeUnreadable) return RiggedGlbPrewarmOutcome.Skipped;
                    return PrewarmRiggedGlbSubject(gameDir, vfs, outfit, character, model, roster,
                        wardrobeUnreadable: false, cacheRoot, cache, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { return RiggedGlbPrewarmOutcome.Skipped; }
            }));
        }
        StartRiggedGlbPrewarm(work);
    }

    /// <summary>Drain a queued rescan before considering new speculative work. Load finalization calls this
    /// only after lowering <see cref="IsScanning"/>; tests use the same boundary to pin that priority.</summary>
    internal void FinishRosterLoadBackgroundWork()
    {
        RunQueuedRescan();
        TryStartRiggedGlbPrewarm();
    }

    /// <summary>Run selected subjects one at a time. Cancellation and exporter degradation are quiet; only
    /// an explicitly classified cache read/write failure leaves the warning after the job.</summary>
    internal void StartRiggedGlbPrewarm(IReadOnlyList<RiggedGlbPrewarmWork> work)
    {
        CancelRiggedGlbPrewarm();
        if (work.Count == 0) return;

        var owner = _riggedGlbPrewarmCts = new CancellationTokenSource();
        Interlocked.Increment(ref _riggedGlbPrewarmRunning);
        ReportRiggedGlbPrewarm(owner, new RiggedGlbPrewarmProgress(0, work.Count), failed: false);
        _riggedGlbPrewarmTask = Task.Run(() =>
        {
            bool failed = false;
            int done = 0;
            try
            {
                foreach (var item in work)
                {
                    owner.Token.ThrowIfCancellationRequested();
                    try
                    {
                        if (item.Run(owner.Token) == RiggedGlbPrewarmOutcome.CacheFailure) failed = true;
                    }
                    catch (OperationCanceledException) when (owner.IsCancellationRequested) { throw; }
                    catch { /* an unclassified subject failure is not evidence that the cache is unavailable */ }
                    owner.Token.ThrowIfCancellationRequested();
                    ReportRiggedGlbPrewarm(owner,
                        new RiggedGlbPrewarmProgress(++done, work.Count), failed: false);
                }
            }
            catch (OperationCanceledException) when (owner.IsCancellationRequested) { }
            finally
            {
                Interlocked.Decrement(ref _riggedGlbPrewarmRunning);
                CompleteRiggedGlbPrewarm(owner, failed && !owner.IsCancellationRequested);
            }
        });
    }

    private void ReportRiggedGlbPrewarm(CancellationTokenSource owner,
        RiggedGlbPrewarmProgress? progress, bool failed) => _pageDispatch(() =>
    {
        if (!ReferenceEquals(_riggedGlbPrewarmCts, owner)) return;
        _riggedGlbPrewarmProgress = progress;
        _riggedGlbPrewarmFailed = failed;
        RefreshBackgroundStatus();
    });

    private void CompleteRiggedGlbPrewarm(CancellationTokenSource owner, bool failed) => _pageDispatch(() =>
    {
        if (ReferenceEquals(_riggedGlbPrewarmCts, owner))
        {
            _riggedGlbPrewarmCts = null;
            _riggedGlbPrewarmProgress = null;
            _riggedGlbPrewarmFailed = failed;
            RefreshBackgroundStatus();
        }
        owner.Dispose();
        RunQueuedRescan();
    });

    /// <summary>Cancel the current speculative generation without waiting for its next exporter boundary.
    /// Its owner guards drop every late progress/completion report.</summary>
    private void CancelRiggedGlbPrewarm(bool clearFailure = true)
    {
        var owner = _riggedGlbPrewarmCts;
        _riggedGlbPrewarmCts = null;
        try { owner?.Cancel(); } catch (ObjectDisposedException) { }
        _riggedGlbPrewarmProgress = null;
        if (clearFailure) _riggedGlbPrewarmFailed = false;
        RefreshBackgroundStatus();
    }

    private sealed class InteractiveRiggedGlbOpenScope : IDisposable
    {
        private readonly MainWindowViewModel _owner;
        private int _disposed;

        public InteractiveRiggedGlbOpenScope(MainWindowViewModel owner)
        {
            _owner = owner;
            Interlocked.Increment(ref owner._interactiveRiggedGlbOpen);
            owner.CancelRiggedGlbPrewarm(clearFailure: false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Decrement(ref _owner._interactiveRiggedGlbOpen) == 0)
                _owner._pageDispatch(_owner.TryStartRiggedGlbPrewarm);
        }
    }

    /// <summary>The interactive route's priority claim. It cancels speculation before any refusal gate or
    /// game read and keeps a model-warm completion from restarting it while the open is preparing.</summary>
    internal IDisposable BeginInteractiveRiggedGlbOpen() => new InteractiveRiggedGlbOpenScope(this);

    /// <summary>Build the common all-parts per-part route wholly inside the derived-cache tree. This method
    /// has no project/session/ingress argument by construction: its only outputs are transient cache-local
    /// build files and clean <see cref="RiggedGlbCache"/> artifacts.</summary>
    internal static RiggedGlbPrewarmOutcome PrewarmRiggedGlbSubject(string gameDir, GameVfs vfs, Outfit outfit,
        string character, SubjectModel model, AssetExporter.SubjectRoster roster, bool wardrobeUnreadable,
        string cacheRoot, RiggedGlbCache cache, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (wardrobeUnreadable) return RiggedGlbPrewarmOutcome.Skipped;

        string parent = Path.Combine(LabPaths.RiggedGlbRootIn(cacheRoot), ".prewarm");
        string transient = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".tmp");
        string buildRun = Path.Combine(transient, "build");
        string partsDir = Path.Combine(buildRun, "parts");
        string texturesDir = Path.Combine(buildRun, "textures");
        var specs = new List<(string Part, string SourceBundle, string MeshName, string? GlbOut,
            IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)>();
        var plans = new List<SessionPartPlan>();
        foreach (var part in model.Parts)
        {
            token.ThrowIfCancellationRequested();
            var recipe = part.ToRecipePart();
            string? bundle = recipe.MeshBundle ?? (recipe.MeshAddress.Length == 0
                ? null : vfs.Catalog.ResolveAddress(recipe.MeshAddress));
            if (bundle is null) continue;
            string rigged = Path.Combine(partsDir, StorageName(part.SlotName) + ".rigged.glb");
            specs.Add((part.Token, bundle, recipe.SlotName, rigged, null, recipe.MeshPathId, null));
            plans.Add(new SessionPartPlan(part.Token, recipe.SlotName, rigged,
                Path.Combine(partsDir, StorageName(part.SlotName) + ".glb"),
                part.IsStatic, null, null));
        }
        if (plans.Count == 0) return RiggedGlbPrewarmOutcome.Skipped;

        var identity = SessionRiggedCacheIdentity(vfs, outfit, character, roster, specs,
            wardrobeUnreadable: false);
        var stock = new StockTextureCache(LabPaths.StockTextureRootIn(cacheRoot));
        var previewMemo = new PreviewBlobMemo();
        try { Directory.CreateDirectory(transient); }
        catch { return RiggedGlbPrewarmOutcome.CacheFailure; }
        try
        {
            token.ThrowIfCancellationRequested();

            RiggedGlbCache.ServeDependencies dependencies;
            bool rigsRestored = TryRestoreSessionRiggedParts(cache, identity, vfs, stock, plans,
                buildRun, out dependencies);
            if (!rigsRestored)
            {
                Directory.CreateDirectory(partsDir);
                var diagnostics = new AssetExporter.RiggedBuildDiagnostics();
                var built = AssetExporter.BuildRiggedGlbs(gameDir, vfs, outfit, character, specs,
                    texturesDir, roster: roster,
                    candidacyCacheFile: LabPaths.CandidacyCacheFileIn(cacheRoot), ct: token,
                    stockTextureCacheRoot: LabPaths.StockTextureRootIn(cacheRoot),
                    diagnostics: diagnostics, previewMemo: previewMemo);
                token.ThrowIfCancellationRequested();
                if (!diagnostics.Completed || !diagnostics.GameSideOnly || diagnostics.ProducedComposition
                    || diagnostics.HadTransientFailures || diagnostics.WasCanceled
                    || diagnostics.HadProjectAuthoredContent
                    || !TryDescribeRiggedBuildDependencies(vfs, stock, diagnostics, out dependencies))
                    return RiggedGlbPrewarmOutcome.Skipped;
                var riggedOutcome = PublishSessionRiggedPartsForPrewarm(cache, identity, vfs, stock,
                    diagnostics, plans, built);
                if (riggedOutcome != RiggedGlbPrewarmOutcome.Ready) return riggedOutcome;
            }

            token.ThrowIfCancellationRequested();
            var preparedPlans = plans.Where(plan => !plan.Static).ToList();
            var preparedKeys = PreparedSessionPartKeys(identity, preparedPlans, token);
            var restored = TryRestoreSessionPreparedParts(cache, identity, vfs, preparedPlans, preparedKeys,
                cancellationToken: token);
            var misses = preparedPlans.Where(plan => !restored.Contains(plan.Prepared)).ToList();
            if (PrepareSessionParts(misses, skipStatic: true, cancellationToken: token,
                    previewMemo: previewMemo).Count != 0)
                return RiggedGlbPrewarmOutcome.Skipped;
            token.ThrowIfCancellationRequested();
            if (misses.Count > 0
                && !PublishSessionPreparedParts(cache, identity, dependencies, misses, preparedKeys, token))
                return RiggedGlbPrewarmOutcome.CacheFailure;
            return RiggedGlbPrewarmOutcome.Ready;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return RiggedGlbPrewarmOutcome.Skipped; }
        finally
        {
            try { if (Directory.Exists(transient)) Directory.Delete(transient, recursive: true); }
            catch { /* cache-local residue only; no completion marker names it */ }
            try { if (Directory.Exists(parent)) Directory.Delete(parent, recursive: false); }
            catch { /* another prewarm or an inert residue still owns the folder */ }
        }
    }
}
