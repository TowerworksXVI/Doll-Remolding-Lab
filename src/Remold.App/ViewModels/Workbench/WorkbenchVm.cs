using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Remold.App.ViewModels;
using Remold.Core.Bundles;
using Remold.Core.Materials;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Textures;
using Remold.Core.Workbench;

namespace Remold.App.ViewModels.Workbench;

/// <summary>
/// The Outfit Workbench: the structure tree of every picked subject (parts → materials → texture maps, plus
/// Skeleton), a per-node inspector, and the edit verbs. Read side via constructor delegates, edit verbs
/// through the injected shell, so it never reaches back into the hosting window. The heavy build runs OFF the
/// UI thread and is assigned in one dispatcher hop; a superseding <see cref="Activate"/> cancels it.
///
/// <para>Previews render for the SELECTED node and are also warmed up after each build
/// (<see cref="StartPreviewWarmup"/>), through the same demand machinery at lower priority. Each load is
/// memoized in-process and backed by the persistent catalog-version thumbnail cache; edited workspace files
/// are decoded directly, never through that cache. A monotonic per-row request id disposes out-of-order
/// completions.</para>
/// </summary>
public sealed partial class WorkbenchVm : ObservableObject
{
    private readonly Func<ModProject> _project;
    private readonly Func<GameVfs?> _vfs;
    private readonly Func<FriendlyNames> _friendly;
    private readonly Func<IReadOnlyList<Character>> _roster;
    private readonly Func<string, byte[]?> _tryDeobfuscate;
    private readonly Func<CatalogIndex?>? _catalog;
    private readonly Func<byte[], Bitmap> _decodeMeshPreview;
    /// <summary>The session's shared subject-model memo, or null in a test construction that builds its own.
    /// Every <see cref="Activate"/> rebuilds the tree, which without this re-reads the game for subjects
    /// already on screen — and the Build step builds the very same models.</summary>
    private readonly SubjectModelCache? _subjectModels;
    /// <summary>The imperative plumbing the verbs reuse, implemented by the hosting window. Null in the
    /// tree-only test construction.</summary>
    private readonly IWorkbenchShell? _shell;

    // Texture meta cache, keyed "catalogVersion|bundle|texture" so a mid-session game update can't serve
    // stale dims. Dims only, never pixels. UI thread only.
    private readonly Dictionary<string, string> _metaCache = new(StringComparer.Ordinal);

    // Persistent preview cache; vanilla thumbs are reusable across mods/sessions. Stateless/thread-safe, so
    // a batch runs several workers in parallel.
    private readonly Core.Workbench.ThumbnailCache _thumbs;
    // The catalog version keying every thumb, captured on the UI thread at each Rebuild.
    private string _catalogVersion = "unknown";
    // Bounded parallelism for preview work: decode is CPU + I/O, so a few workers.
    internal const int ThumbWorkers = 3;

    // ONE VM-wide bound for ALL preview work. A per-batch Parallel cap only bounds a single selection's
    // batch, and rapid keyboard navigation stacks a fresh batch per selection — without a shared gate the
    // concurrent deobfuscate/decode/render work is unbounded. Every work unit takes a permit before its heavy
    // work. Never disposed: the VM outlives every batch it launches.
    private readonly SemaphoreSlim _previewGate = new(ThumbWorkers, ThumbWorkers);

    private CancellationTokenSource? _cts;

    public WorkbenchVm(
        Func<ModProject> project, Func<GameVfs?> vfs, Func<FriendlyNames> friendly,
        Func<IReadOnlyList<Character>> roster, Func<string, byte[]?> tryDeobfuscate,
        Func<CatalogIndex?>? catalog, Func<byte[], Bitmap>? decodeMeshPreview = null,
        IWorkbenchShell? shell = null, string? thumbnailRoot = null,
        SubjectModelCache? subjectModels = null)
    {
        // thumbnailRoot: null = the real per-user cache. Tests pass a temp dir so a preview render can't
        // write into the machine's shared thumb store.
        _thumbs = new Core.Workbench.ThumbnailCache(thumbnailRoot);
        _project = project;
        _vfs = vfs;
        _friendly = friendly;
        _roster = roster;
        _tryDeobfuscate = tryDeobfuscate;
        _catalog = catalog;
        _decodeMeshPreview = decodeMeshPreview ?? DecodeMeshPreview;
        _shell = shell;
        _subjectModels = subjectModels;
        // Wire every root (and its subtree) so an async inspector-line change re-runs the active filter.
        Nodes.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (WorkbenchNodeVm root in e.NewItems) WireFilterInvalidation(root);
        };
    }

    /// <summary>The tree roots (one node per subject).</summary>
    public ObservableCollection<WorkbenchNodeVm> Nodes { get; } = new();

    [ObservableProperty] private WorkbenchNodeVm? _selectedNode;
    [ObservableProperty] private string _status = "";
    /// <summary>True while the game's forward view isn't loaded yet.</summary>
    [ObservableProperty] private bool _isReadingGame;
    /// <summary>The game load FAILED, as opposed to merely pending: the pane shows a static unavailable
    /// state instead of pulsing forever. Cleared when a later load succeeds.</summary>
    [ObservableProperty] private bool _gameUnavailable;
    /// <summary>Carried across a rebuild so entering Edit AFTER a failure shows the static unavailable state
    /// rather than the pulsing wait — the vfs is null in both cases.</summary>
    private bool _gameLoadFailed;
    /// <summary>Nothing to show — no picked subjects and no loose assets.</summary>
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private string _filter = "";
    /// <summary>A non-empty filter hides every node — the panel shows a "no match" placeholder rather than a
    /// silent blank.</summary>
    [ObservableProperty] private bool _noMatches;

    public bool HasNodes => Nodes.Count > 0;

    // ---- lifecycle (driven by the shell) ----

    /// <summary>The pane became active: (re)build the tree, cancelling any in-flight build. With no forward
    /// view yet it shows the reading state and defers to <see cref="NotifyGameReady"/>. The selection is
    /// remembered across the rebuild, so a hop out to another step and back lands where it left.</summary>
    public void Activate()
    {
        RememberSelection();
        Rebuild();
    }

    /// <summary>The current mod changed (new/open/close): cancel, clear, rebuild.</summary>
    public void NotifyProjectChanged() => Rebuild();

    /// <summary>The forward view loaded: rebuild if we were waiting on it or showing the failed state.</summary>
    public void NotifyGameReady()
    {
        if (IsReadingGame || GameUnavailable) Rebuild();
    }

    /// <summary>The game load FAILED: show the static unavailable state, since the pane can't build a tree
    /// without the forward view. A later <see cref="NotifyGameReady"/> clears it.</summary>
    public void NotifyGameFailed()
    {
        _gameLoadFailed = true;
        // Only take over the no-game surface — never wipe a tree built from a prior good load.
        if (IsReadingGame || GameUnavailable || !HasNodes)
        {
            IsReadingGame = false;
            GameUnavailable = true;
            IsEmpty = false;
            IsBuilding = false;
            Status = "Game files unavailable.";
        }
    }

    /// <summary>Cancel any in-flight build and clear the tree.</summary>
    public void Reset()
    {
        _cts?.Cancel();
        _cts = null;
        // The Materialize-all batch is navigation-proof (not tied to _cts), so a MOD CHANGE is what must
        // stop it. Reset runs on new/open/close, never on a plain Pick↔Edit hop.
        _materializeAllCts?.Cancel();
        IsMaterializingAll = false;
        ReleaseThumbs();            // cancel FIRST (above), then free the bitmaps
        Nodes.Clear();
        OnPropertyChanged(nameof(HasNodes));
        SelectedNode = null;
        IsBuilding = false;
        // The request named a part of the mod being torn down; the next mod's tree must not inherit it.
        _pendingSelect = null;
    }

    // ---- build ----

    private void Rebuild()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;

        ReleaseThumbs();            // cancel FIRST (above), then free the bitmaps
        Nodes.Clear();
        OnPropertyChanged(nameof(HasNodes));
        SelectedNode = null;

        var project = _project();
        var vfs = _vfs();

        // snapshot the ledger on the UI thread — no concurrent mutation of the project list
        var subjects = project.Selection
 .Select(s => (s.Character, s.Outfit))
 .ToList();

        if (vfs is null)
        {
            // The vfs is null for both "not loaded yet" and "load failed", so the carried flag decides.
            IsReadingGame = !_gameLoadFailed;
            GameUnavailable = _gameLoadFailed;
            IsEmpty = false;
            IsBuilding = false;
            Status = _gameLoadFailed ? "Game files unavailable." : "Reading game files…";
            return;
        }
        _gameLoadFailed = false;
        IsReadingGame = false;
        GameUnavailable = false;
        _catalogVersion = vfs.CatalogVersion;

        if (subjects.Count == 0)
        {
            IsEmpty = true;
            IsBuilding = false;
            Status = "Nothing in this mod yet. Check subjects in ① Pick, then double-click one to open it here.";
            return;
        }
        IsEmpty = false;
        IsBuilding = true;
        Status = "Reading structure…";

        var friendly = _friendly();
        var roster = _roster();

        // Resolve each subject's Outfit on the UI thread (roster read), then build the models off-thread.
        // FriendlyNames owns the whole label, so this and the Characters tree can't drift.
        var toBuild = subjects
 .Select(s =>
            {
                // Memoize only where the ROSTER answered: the fallback outfit below carries no curated route,
                // so a model built from it is not the one the real outfit would give, and caching it under the
                // same key would serve that answer to every later ask.
                var found = RosterLookup.FindOutfit(roster, s.Character, s.Outfit);
                var outfit = found ?? FallbackOutfit(s.Outfit);
                return (s.Character, Outfit: outfit, Memoize: found is not null,
                        Subject: new WorkbenchSubjectRef(s.Character, outfit.Stem, outfit.MeshPrefix, outfit),
                        Display: friendly.Subject(s.Character, outfit));
            })
 .ToList();

        var catalog = _catalog?.Invoke();

        Task.Run(() =>
        {
            var built = new List<(string Display, SubjectModel Model, WorkbenchSubjectRef Subject)>();
            foreach (var s in toBuild)
            {
                if (token.IsCancellationRequested) return;
                SubjectModel model;
                if (catalog is null)
                {
                    // no readable catalog — surface it, never vanish
                    model = new SubjectModel(s.Character, s.Outfit.Stem, SubjectSource.Prefab,
                        Array.Empty<SubjectPart>(), null,
                        new[] { "Couldn't read this subject: the game catalog isn't readable." });
                }
                else
                {
                    try { model = BuildSubjectModel(catalog, s.Outfit, s.Character, s.Memoize); }
                    catch (Exception e)
                    {
                        // the builder is designed not to throw; surface it rather than vanish if it does
                        model = new SubjectModel(s.Character, s.Outfit.Stem, SubjectSource.Prefab,
                            Array.Empty<SubjectPart>(), null,
                            new[] { $"Couldn't read this subject: {e.Message}" });
                    }
                }
                built.Add((s.Display, model, s.Subject));
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;   // superseded
                AssignNodes(built, token);
            });
        }, token);
    }

    private void AssignNodes(List<(string Display, SubjectModel Model, WorkbenchSubjectRef Subject)> built,
        CancellationToken token)
    {
        Nodes.Clear();
        foreach (var (display, model, subject) in built)
        {
            var root = BuildSubjectNode(display, model, subject);
            PushBlenderFound(root, _blenderFound);   // a fresh tree starts on the current detection answer
            PushPartSessions(root);                  // …and on the sessions already open
            Nodes.Add(root);
        }

        IsBuilding = false;
        Status = Count(Nodes.Count, "item");
        OnPropertyChanged(nameof(HasNodes));
        ApplyFilter();
        RefreshNodeStates();   // seed ✎ / materialized flags from the project
        ApplyPendingSelect();  // a row→Edit hop that arrived before this tree existed
        StartPreviewWarmup(token);
    }

    // ---- selection requested from another pane ----

    /// <summary>A selection asked for before the tree could serve it. <c>Mesh</c> empty selects the subject
    /// root. <c>Label</c> is how a miss names what it couldn't find, and <c>ReportMiss</c> is true only for a
    /// hop the modder asked for — a restore whose part is gone says nothing, since nobody asked for it.</summary>
    private sealed record PendingSelect(string Character, string Stem, string Mesh, int? Submesh,
        string Label, bool ReportMiss);

    /// <summary>A selection waiting on a tree, or null. Held across ONE rebuild and cleared whether or not
    /// the node turned up, so a request for a part no longer in the mod can't reapply itself at some later
    /// rebuild. <see cref="Reset"/> drops it: it names a part of the mod being closed.</summary>
    private PendingSelect? _pendingSelect;

    /// <summary>A hop is waiting on a tree. Test seam for the clear-on-Reset rule.</summary>
    internal bool HasPendingSelect => _pendingSelect is not null;

    /// <summary>Select the tree row for one derived change — the Build pane's row→Edit hop. A request
    /// that finds no tree yet (the Edit rebuild finishes off-thread) is held and applied when it lands. A
    /// part the tree doesn't carry leaves the selection alone and SAYS so on the status line.
    /// <paramref name="submesh"/> asks for the material bound at that submesh (where a retexture is
    /// authored), falling back to the part; <paramref name="partLabel"/> is how a miss names the part.</summary>
    public void RequestSelectPart(string character, string stem, string mesh, int? submesh = null,
        string? partLabel = null)
    {
        _pendingSelect = new PendingSelect(character, stem, mesh, submesh,
            string.IsNullOrEmpty(partLabel) ? mesh : partLabel, ReportMiss: true);
        ApplyPendingSelect();
    }

    /// <summary>Remember what is selected so the rebuild <see cref="Activate"/> kicks off can put it
    /// back. The tree is rebuilt WHOLE on every step hop, so the selection is restored by identity.
    /// Never overwrites a hop already held.</summary>
    private void RememberSelection()
    {
        if (SelectedNode is not { } sel || sel.Subject is not { } s) return;
        var (mesh, submesh) = SelectionIdentity(sel);
        _pendingSelect = new PendingSelect(s.Character, s.Stem, mesh, submesh, mesh, ReportMiss: false);
    }

    /// <summary>What a node restores through: a Part by its own recipe slot, a Material by its part's slot
    /// plus its renderer index, everything else — the subject root and the Skeleton row — by an empty slot,
    /// which restores as the subject root.</summary>
    private (string Mesh, int? Submesh) SelectionIdentity(WorkbenchNodeVm node)
    {
        if (node.Kind == WorkbenchNodeKind.Part) return (node.Recipe?.SlotName ?? "", null);
        if (node.Kind == WorkbenchNodeKind.Material)
            foreach (var root in Nodes)
                foreach (var part in root.Children)
                    if (part.Kind == WorkbenchNodeKind.Part && part.Children.Contains(node))
                        return (part.Recipe?.SlotName ?? "", node.MaterialIndex);
        return ("", null);
    }

    /// <summary>Apply a held selection, if there is one. Internal so the rule can be driven without a tree
    /// built from a live install — <see cref="AssignNodes"/> is its only production caller.</summary>
    internal void ApplyPendingSelect()
    {
        if (_pendingSelect is not { } want) return;
        if (Nodes.Count == 0) return;   // a rebuild is still in flight — hold for its AssignNodes
        _pendingSelect = null;
        // Subject nodes and their children are expanded by default, so selecting is the whole hop.
        if (want.Mesh.Length == 0) { SelectedNode = FindSubjectNode(want.Character, want.Stem); return; }
        if (FindPartNode(want.Character, want.Stem, want.Mesh) is not { } part)
        {
            if (want.ReportMiss) Status = $"{want.Label} isn't in this tree.";
            return;
        }
        SelectedNode = MaterialUnder(part, want.Submesh) ?? part;
    }

    /// <summary>The material node a submesh index binds, or null. Renderer material order IS the submesh
    /// binding, and a shortfall repeats the last slot — the same rule the build's retexture pass and the
    /// preview both assign maps by.</summary>
    private static WorkbenchNodeVm? MaterialUnder(WorkbenchNodeVm part, int? submesh)
    {
        if (submesh is not { } index || index < 0) return null;
        var materials = part.Children.Where(c => c.Kind == WorkbenchNodeKind.Material).ToList();
        return materials.Count == 0 ? null : materials[Math.Min(index, materials.Count - 1)];
    }

    /// <summary>How many renderer material slots the GAME part carries — the count an edited mesh's own
    /// submesh list is measured against. Null when the tree doesn't hold that part, so a caller that can only
    /// report the difference says nothing rather than guessing one.</summary>
    public int? GameSubmeshCount(string character, string stem, string mesh) =>
        FindPartNode(character, stem, mesh)?.SubmeshBaseMaps.Count;

    /// <summary>The RMO the GAME renderer binds on one submesh of a part. <c>Answered</c> is false when the
    /// tree can't speak for that submesh at all — a part it doesn't hold, or an index past the renderer's
    /// slots (what an added submesh lands on); <c>Rmo</c> null with <c>Answered</c> true means the slot
    /// genuinely binds no RMO. A caller reporting a lost emissive mask needs the two apart: only the first is
    /// a map that might have been there.</summary>
    public (bool Answered, SubjectMap? Rmo) GameRmoMap(string character, string stem, string mesh, int submesh)
    {
        var maps = FindPartNode(character, stem, mesh)?.SubmeshRmoMaps;
        return maps is null || submesh < 0 || submesh >= maps.Count ? (false, null) : (true, maps[submesh]);
    }

    /// <summary>How many submeshes <see cref="GameRmoMap"/> can answer for on a part; null when the tree does
    /// not hold it. A caller that has to take every answer at once — because its own work leaves the UI
    /// thread the tree lives on — needs the count before it can ask.</summary>
    public int? GameRmoSlots(string character, string stem, string mesh) =>
        FindPartNode(character, stem, mesh)?.SubmeshRmoMaps.Count;

    /// <summary>The subject root for a (character, outfit stem), or null.</summary>
    private WorkbenchNodeVm? FindSubjectNode(string character, string stem)
    {
        foreach (var root in Nodes)
            if (root.Subject is { } s
                && string.Equals(s.Character, character, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Stem, stem, StringComparison.OrdinalIgnoreCase))
                return root;
        return null;
    }

    /// <summary>The Part node for a (character, outfit stem, mesh slot name), or null. Matches on the same
    /// case-insensitive identity the project ledger and the build's derivation use.</summary>
    private WorkbenchNodeVm? FindPartNode(string character, string stem, string mesh)
    {
        foreach (var root in Nodes)
        {
            if (root.Subject is not { } s
                || !string.Equals(s.Character, character, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(s.Stem, stem, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var child in root.Children)
                if (child.Kind == WorkbenchNodeKind.Part
                    && string.Equals(child.Recipe?.SlotName ?? "", mesh, StringComparison.OrdinalIgnoreCase))
                    return child;
        }
        return null;
    }

    // ---- proactive preview warm-up ----

    /// <summary>Request every node's VANILLA preview through the SAME demand machinery a selection uses,
    /// so the first click lands warm. Selection wins two ways: warm-up runs as ONE serialized batch
    /// (<c>maxDop 1</c>) holding at most one gate permit, and a selection bumps the row's request id,
    /// which rejects the warm completion. Edited maps/parts are left to demand. Runs under the build
    /// token, so a superseding Rebuild/Reset cancels pending work and rejects late completions.</summary>
    internal void StartPreviewWarmup(CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        var proj = _project();
        var version = _catalogVersion;

        // Issue the per-row warm requests on the UI thread (they mutate row state), then hand the heavy
        // deobfuscate/decode to one background task.
        var maps = new List<WorkbenchMapVm>();
        foreach (var root in Nodes) CollectMaps(root, maps);
        var thumbs = new List<ThumbRequest>();
        foreach (var m in maps)
        {
            if (m.IsEdited || m.AuthoredPath is not null) continue;   // demand renders it on selection
            if (!m.HasBundle) continue;                               // donor-derived: no bundle to warm from
            if (m.BeginThumbWarmupIfNeeded() is { } gen) thumbs.Add(new ThumbRequest(m, gen));
        }

        var parts = new List<WorkbenchNodeVm>();
        foreach (var root in Nodes) CollectParts(root, parts);
        var meshes = new List<MeshPreviewRequest>();
        foreach (var part in parts)
        {
            if (!part.NeedsMeshPreviewWarmup) continue;
            var target = PartMeshTarget(proj, part);
            if (target is not null && proj.IsEdited(target)) continue;   // demand renders the edited glb
            meshes.Add(new MeshPreviewRequest(part, part.BeginMeshPreviewWarmup()));
        }

        if (thumbs.Count == 0 && meshes.Count == 0) return;

        // Both batches sequentially at maxDop 1, so warm-up occupies at most a single gate permit.
        Task.Run(() =>
        {
            ThumbBatch(thumbs, version, token, maxDop: 1);
            MeshPreviewBatch(meshes, version, token, maxDop: 1);
        }, token);
    }

    // ---- demand-driven previews ----

    /// <summary>Build the thumbs for the just-selected material's maps: bounded workers, one deobfuscate per
    /// bundle, cache hits skipped. An already-edited map previews its WORKSPACE PNG, decoded off the safe
    /// path — never the cache-eviction one, which would delete the modder's edited file on a decode hiccup.</summary>
    private void EnsureMapThumbs(WorkbenchNodeVm materialNode)
    {
        var proj = _project();
        var version = _catalogVersion;
        var token = _cts?.Token ?? CancellationToken.None;
        var vanilla = new List<ThumbRequest>();
        foreach (var m in materialNode.Maps)
        {
            // An authored file that has gone away must not keep showing its last image; dropping the thumb
            // also re-opens the row for whatever IS behind it.
            m.IsAuthoredFileMissing = m.AuthoredPath is { } gone && !File.Exists(gone);
            if (m.IsAuthoredFileMissing) m.MarkThumbFailed();
            if (m.BeginThumbRequestIfNeeded() is not { } gen) continue;   // loaded or in-flight
            if (m.AuthoredPath is { } ap && File.Exists(ap)) { ReThumbFromWorkspace(m, ap, gen, token); continue; }
            var ws = WorkspacePngFor(proj, m);
            if (m.IsEdited && ws is not null && File.Exists(ws)) ReThumbFromWorkspace(m, ws, gen, token);
            // A donor-derived row whose file is gone has no bundle to fall back to — settle it quietly
            // rather than deobfuscate an empty bundle id.
            else if (!m.HasBundle) m.MarkThumbFailed();
            else vanilla.Add(new ThumbRequest(m, gen));
        }
        if (vanilla.Count > 0) Task.Run(() => ThumbBatch(vanilla, version, token), token);
    }

    /// <summary>Build the just-selected part's mesh preview if it still needs one.</summary>
    private void EnsureMeshPreview(WorkbenchNodeVm part)
    {
        if (part.NeedsMeshPreview) LoadMeshPreview(part);
    }

    /// <summary>A row plus the generation captured at launch, so a stale off-thread completion is rejected
    /// on the dispatcher.</summary>
    private readonly record struct ThumbRequest(WorkbenchMapVm Map, int Generation);

    /// <summary>The map's own subject-scoped texture target, or null for a map with no subject (a
    /// donor-authored row) or none materialized.</summary>
    private static ProjectTarget? MapTarget(ModProject proj, WorkbenchMapVm m) =>
        m.Subject is { } s
            ? Materializer.TextureTarget(proj, s.Character, s.Stem, m.BundleId, m.TextureName)
            : null;

    /// <summary>The absolute workspace PNG path for a materialized map, or null when it isn't materialized
    /// or can't be resolved.</summary>
    private static string? WorkspacePngFor(ModProject proj, WorkbenchMapVm m)
    {
        var t = MapTarget(proj, m);
        if (t is null || proj.RootDir is null) return null;
        try { return Path.GetFullPath(proj.Resolve(t.ReplaceFile)); } catch { return null; }
    }

    private static void CollectMaps(WorkbenchNodeVm node, List<WorkbenchMapVm> into)
    {
        foreach (var m in node.Maps) into.Add(m);
        foreach (var c in node.Children) CollectMaps(c, into);
    }

    private static void CollectParts(WorkbenchNodeVm node, List<WorkbenchNodeVm> into)
    {
        if (node.Kind == WorkbenchNodeKind.Part) into.Add(node);
        foreach (var c in node.Children) CollectParts(c, into);
    }

    /// <summary>Dispose every tree row's bitmap before the tree is dropped, so a repeated Activate/rebuild
    /// cycle doesn't hold native memory until GC. Callers MUST cancel the in-flight batch's token FIRST —
    /// <see cref="AssignThumb"/> re-checks it, so a late producer can't resurrect a released bitmap. UI
    /// thread.</summary>
    private void ReleaseThumbs()
    {
        foreach (var root in Nodes) ReleaseThumbsIn(root);
    }

    private static void ReleaseThumbsIn(WorkbenchNodeVm node)
    {
        node.ReleaseMeshPreview();
        foreach (var m in node.Maps) m.ReleaseThumb();
        // the stash keeps the whole game surface for the restore, including nodes no child reaches — the ones
        // a shorter send-back left no submesh for. Releasing a node the children also hold is idempotent.
        if (node.StashedGameChildren is { } stash)
            foreach (var c in stash) ReleaseThumbsIn(c);
        foreach (var c in node.Children) ReleaseThumbsIn(c);
    }

    /// <summary>Run one preview work unit under the VM-wide concurrency bound. Never lets the wait's
    /// cancellation escape — callers run inside <c>Parallel.ForEach</c> bodies or fire-and-forget tasks.</summary>
    private void RunGated(CancellationToken token, Action body)
    {
        try { _previewGate.Wait(token); }
        catch (OperationCanceledException) { return; }
        try { body(); }
        finally { _previewGate.Release(); }
    }

    /// <summary>Thumbs for a set of map requests, grouped by bundle so one deobfuscate is shared across a
    /// bundle's rows. OFF the UI thread; results marshal back guarded by their captured generation. Never
    /// caches a failure.</summary>
    private void ThumbBatch(IReadOnlyList<ThumbRequest> maps, string version, CancellationToken token,
        int maxDop = ThumbWorkers)
    {
        var byBundle = maps.GroupBy(r => r.Map.BundleId).ToList();
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxDop, CancellationToken = token };
        try
        {
            Parallel.ForEach(byBundle, options, group => RunGated(token, () =>
            {
                if (token.IsCancellationRequested) return;
                var bundleId = group.Key;
                var rows = group.ToList();

                // Deobfuscate at most once, and only if a texture still needs a thumb.
                byte[]? deobfuscateed = null;
                bool deobfuscateTried = false;
                foreach (var req in rows)
                {
                    if (token.IsCancellationRequested) return;
                    var m = req.Map;

                    string? path = _thumbs.TryGetCachedPath(bundleId, m.TextureName, version);
                    if (path is null)
                    {
                        if (!deobfuscateTried)
                        {
                            deobfuscateTried = true;
                            try { deobfuscateed = _tryDeobfuscate(bundleId); } catch { deobfuscateed = null; }
                        }
                        if (deobfuscateed is not null)
                            path = _thumbs.EnsureThumb(deobfuscateed, bundleId, m.TextureName, version);
                    }
                    AssignThumb(m, path, req.Generation, token);
                }
            }));
        }
        catch (OperationCanceledException) { /* superseded */ }
    }

    // ---- demand-driven part mesh previews ----

    internal readonly record struct MeshPreviewRequest(WorkbenchNodeVm Part, int Generation);
    private readonly record struct EditedMeshPreviewRequest(WorkbenchNodeVm Part, int Generation,
        string? WorkspacePath, int? OriginalVertexCount);

    /// <summary>A settled game-geometry render on its way to a row: the original vertex count, the PNG bytes
    /// when the render was produced in memory, and the persisted cache file when it came from one. Exactly
    /// one of the two is set — <see cref="CachePath"/> also names the file a decode failure evicts, and an
    /// unpersisted render (edited/authored maps) has none to evict.</summary>
    private readonly record struct MeshPreviewResult(int VertexCount, string? CachePath, byte[]? Png);

    /// <summary>Runs between a part's samplers being built and the persistence decision made from them —
    /// the window a revert can land in. Test seam for that rule; null in production.</summary>
    internal Action? OnSamplersBuiltForTest;

    /// <summary>Resolve each recipe address before its bundle-qualified cache lookup, then derive misses
    /// through the same chain as materialization. Cache hits need no deobfuscate. Internal so the persistence
    /// rule can be driven directly, without a dispatcher or a live install.</summary>
    internal void MeshPreviewBatch(IReadOnlyList<MeshPreviewRequest> parts, string version, CancellationToken token,
        int maxDop = ThumbWorkers)
    {
        var misses = new List<(MeshPreviewRequest Request, string Bundle)>();
        CatalogIndex? catalog = null;
        bool catalogLoaded = false;
        foreach (var request in parts)
        {
            if (token.IsCancellationRequested) return;
            var part = request.Part;
            // Both prefab-backed forms preview: recipe (address → bundle, mesh by name) and smr-body (the
            // resolved bundle, mesh by exact path id — same-named copies make the id part of the key).
            if (part.Recipe is not { } recipe || (!recipe.IsRecipeBacked && !recipe.IsSmrBacked))
            {
                AssignVanillaMeshPreview(request, null, environmentFailure: false, token);
                continue;
            }

            string? bundle;
            if (recipe.IsRecipeBacked)
            {
                if (!catalogLoaded)
                {
                    catalogLoaded = true;
                    try { catalog = _catalog?.Invoke(); } catch { catalog = null; }
                }
                bundle = catalog?.ResolveAddress(recipe.MeshAddress);
            }
            else bundle = recipe.MeshBundle;
            if (bundle is null)
            {
                AssignVanillaMeshPreview(request, null, environmentFailure: true, token);
                continue;
            }

            // The cache-READ half of WorkbenchNodeVm.HasOwnBaseMaps: a hit here would serve the vanilla
            // render over the edit. This reads UI-mutable row state, which is fine for a READ — the worst a
            // stale answer costs is one fresh render. The WRITE half is decided further down, off the
            // evidence the samplers leave, because there a stale answer poisons the cache.
            var hit = part.HasOwnBaseMaps
                ? null
                : _thumbs.TryGetCachedMesh(bundle, recipe.SlotName, version, recipe.IsRecipeBacked ? 0 : recipe.MeshPathId);
            if (hit is { } cached)
                AssignVanillaMeshPreview(request, new MeshPreviewResult(cached.VertexCount, cached.Path, null),
                    environmentFailure: false, token);
            else misses.Add((request, bundle));
        }

        var groups = misses.GroupBy(x => x.Bundle).ToList();
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxDop, CancellationToken = token };
        var texMemo = new ConcurrentDictionary<string, MeshPreviewRenderer.PreviewTexture?>(StringComparer.Ordinal);
        try
        {
            Parallel.ForEach(groups, options, group => RunGated(token, () =>
            {
                if (token.IsCancellationRequested) return;
                byte[]? deobfuscateed;
                try { deobfuscateed = _tryDeobfuscate(group.Key); } catch { deobfuscateed = null; }
                foreach (var item in group)
                {
                    if (token.IsCancellationRequested) return;
                    MeshPreviewResult? result = null;
                    if (deobfuscateed is not null && item.Request.Part.Recipe is { } recipe)
                    {
                        var part = item.Request.Part;
                        var samplers = BuildPreviewSamplers(part, version, texMemo, out bool usedOwnMaps);
                        OnSamplersBuiltForTest?.Invoke();
                        long pathId = recipe.IsRecipeBacked ? 0 : recipe.MeshPathId;
                        // Persistence follows what the samplers ACTUALLY read, not a second read of the row's
                        // map lists: those are UI-thread state and this is a worker, so a revert landing
                        // between the two reads would file a modder-textured render under a key that carries
                        // game identity only, and hand it to every later project.
                        if (usedOwnMaps)
                        {
                            // fresh every time, persisted nowhere — see WorkbenchNodeVm.HasOwnBaseMaps
                            if (_thumbs.RenderMeshThumb(deobfuscateed, recipe.SlotName, samplers, pathId) is { } fresh)
                                result = new MeshPreviewResult(fresh.VertexCount, null, fresh.Png);
                        }
                        else if (_thumbs.EnsureMeshThumb(deobfuscateed, group.Key, recipe.SlotName, version,
                                     samplers, pathId) is { } thumb)
                            result = new MeshPreviewResult(thumb.VertexCount, thumb.Path, null);
                    }
                    AssignVanillaMeshPreview(item.Request, result, environmentFailure: deobfuscateed is null, token);
                }
            }));
        }
        catch (OperationCanceledException) { /* superseded */ }
    }

    /// <summary>The per-submesh sampling textures for one part: the modder's own base-color map where
    /// there is one (an authored donor map, else the workspace PNG of an EDITED game texture), the cached
    /// vanilla thumb everywhere else. Best-effort: an unresolvable map renders that submesh untextured —
    /// the loud texture-miss surface is materialization, not the preview. <paramref name="editedMesh"/>
    /// sizes the result to the CURRENT mesh; <paramref name="texMemo"/> shares decodes across a batch.
    /// <paramref name="usedOwnMaps"/> is the EVIDENCE persistence is decided on: true when some slot took
    /// a modder file — reported from what this build actually did, so a caller never re-reads the row's
    /// UI-mutable map lists (see <see cref="MeshPreviewBatch"/>).</summary>
    internal IReadOnlyList<MeshPreviewRenderer.PreviewTexture?>? BuildPreviewSamplers(WorkbenchNodeVm part,
        string version, ConcurrentDictionary<string, MeshPreviewRenderer.PreviewTexture?> texMemo,
        out bool usedOwnMaps, bool editedMesh = false)
    {
        usedOwnMaps = false;
        // The edited route sizes to the CURRENT mesh: an edit can carry more submeshes than the game part
        // had, and the unauthored ones fall back to the renderer's shortfall rule.
        int n = editedMesh ? Math.Max(part.SubmeshBaseMaps.Count, part.AuthoredBaseMaps.Count)
                           : part.SubmeshBaseMaps.Count;
        if (n == 0) return null;
        var result = new MeshPreviewRenderer.PreviewTexture?[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            // The modder's file wins over the game's: an authored donor map first (it replaced the slot
            // outright), then an edited game texture's workspace PNG. Both lists are indexed by the SAME
            // number: a donor submesh index (what a send-back records against) and a renderer slot index are
            // taken to be the same position — the alignment the whole texture-slot model rests on.
            var ownPng = i < part.AuthoredBaseMaps.Count ? part.AuthoredBaseMaps[i] : null;
            ownPng ??= i < part.EditedBaseMaps.Count ? part.EditedBaseMaps[i] : null;
            if (ownPng is not null)
            {
                // Counted as own the moment the slot is TAKEN, decode or no decode: the vanilla map is not
                // consulted for this slot either way, so the render is not the game's whatever comes back.
                usedOwnMaps = true;
                var own = texMemo.GetOrAdd("file\0" + ownPng, _ =>
                {
                    try { return MeshPreviewRenderer.PreviewTexture.TryFromPng(File.ReadAllBytes(ownPng)); }
                    catch { return null; }
                });
                result[i] = own;
                any |= own is not null;
                continue;
            }
            int si = Math.Min(i, part.SubmeshBaseMaps.Count - 1);
            if (si < 0 || part.SubmeshBaseMaps[si] is not { } map) continue;
            // An empty id would send the thumb cache hunting; render the submesh untextured instead.
            if (map.BundleId.Length == 0) continue;
            // Keyed by (bundle, name), NOT name alone: same-named maps from different bundles are distinct
            // textures.
            var tex = texMemo.GetOrAdd(TexMemoKey(map), _ =>
            {
                try
                {
                    var path = _thumbs.TryGetCachedPath(map.BundleId, map.TextureName, version)
                               ?? _thumbs.EnsureThumb(_tryDeobfuscate, map.BundleId, map.TextureName, version);
                    return path is null ? null
                        : MeshPreviewRenderer.PreviewTexture.TryFromPng(File.ReadAllBytes(path));
                }
                catch { return null; }
            });
            result[i] = tex;
            any |= tex is not null;
        }
        return any ? result : null;
    }

    /// <summary>(bundle, name), so a same-named texture from another bundle is a distinct decode. Bundle ids
    /// are hex and asset names are identifiers, so the NUL separator can never merge two into a false
    /// match.</summary>
    private static string TexMemoKey(SubjectMap map) => map.BundleId + "\0" + map.TextureName;

    /// <summary>Settle a GAME-geometry render on its row. The maps it sampled may be the modder's own; only
    /// the geometry is necessarily the game's.</summary>
    private void AssignVanillaMeshPreview(MeshPreviewRequest request, MeshPreviewResult? result,
        bool environmentFailure, CancellationToken token)
    {
        var part = request.Part;
        if (token.IsCancellationRequested) return;
        Bitmap? bitmap = null;
        bool decodeFailed = false;
        if (result is { } render)
        {
            var png = render.Png;
            if (png is null && render.CachePath is { } cached)
                try { png = File.ReadAllBytes(cached); } catch { decodeFailed = true; }
            if (png is not null)
                try { bitmap = _decodeMeshPreview(png); }
                catch { decodeFailed = true; }
            // Evict a cache entry that won't read or decode, or every retry loops on the same bad file. An
            // unpersisted render has no file behind it, so there is nothing to evict.
            if (decodeFailed && render.CachePath is { } bad)
                try { File.Delete(bad); } catch { /* the next retry can still replace it */ }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) { bitmap?.Dispose(); return; }
            if (!part.IsCurrentMeshPreviewRequest(request.Generation)) { bitmap?.Dispose(); return; }
            if (bitmap is null) part.MarkMeshPreviewFailed(result?.VertexCount,
                environmentFailure: environmentFailure);
            else part.SetMeshPreview(bitmap, result!.Value.VertexCount);
        });
    }

    private void EditedMeshPreviewBatch(IReadOnlyList<EditedMeshPreviewRequest> requests,
        CancellationToken token)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = ThumbWorkers, CancellationToken = token };
        // An edited-geometry render is never persisted, so it samples the part's own maps freely — the
        // authored ones a send-back recorded, and any edited game texture behind them.
        var version = _catalogVersion;
        var texMemo = new ConcurrentDictionary<string, MeshPreviewRenderer.PreviewTexture?>(StringComparer.Ordinal);
        try
        {
            Parallel.ForEach(requests, options, request => RunGated(token, () =>
            {
                Bitmap? bitmap = null;
                int? editedCount = null;
                if (request.WorkspacePath is not null)
                {
                    try
                    {
                        var mesh = MeshGltf.ImportGlb(request.WorkspacePath);
                        editedCount = mesh.VertexCount;
                        // the own-map evidence is discarded: an edited-geometry render is never persisted
                        var png = MeshPreviewRenderer.RenderWorkspacePng(mesh, ThumbnailCache.MaxDim,
                            BuildPreviewSamplers(request.Part, version, texMemo, out _, editedMesh: true));
                        bitmap = _decodeMeshPreview(png);
                    }
                    catch { bitmap = null; }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) { bitmap?.Dispose(); return; }
                    if (!request.Part.IsCurrentMeshPreviewRequest(request.Generation)) { bitmap?.Dispose(); return; }
                    if (bitmap is null || editedCount is null)
                        request.Part.MarkMeshPreviewFailed(editedCount ?? request.OriginalVertexCount, edited: true,
                            environmentFailure: request.WorkspacePath is null);
                    else request.Part.SetMeshPreview(bitmap, editedCount.Value, request.OriginalVertexCount,
                        edited: true);
                });
            }));
        }
        catch (OperationCanceledException) { /* superseded */ }
    }

    private static Bitmap DecodeMeshPreview(byte[] png)
    {
        using var stream = new MemoryStream(png, writable: false);
        return Bitmap.DecodeToWidth(stream, ThumbnailCache.MaxDim);
    }

    private static ProjectTarget? PartMeshTarget(ModProject project, WorkbenchNodeVm part) =>
        part.Subject is { } subject
            ? Materializer.PartMeshTarget(project, subject.Character, subject.Stem, subject.MeshPrefix, part.PartToken)
            : null;

    private static string? ResolveTargetPath(ModProject project, string relative)
    {
        if (project.RootDir is null) return null;
        try
        {
            var path = Path.GetFullPath(project.Resolve(relative));
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    /// <summary>Decode one map card's PNG at thumbnail width. An RMO's alpha is the emissive mask, not
    /// coverage — sampled as transparency it renders the tile ghostly — so the pixels are composited
    /// opaque first, on the decoded copy alone (the PNG on disk is untouched) and at thumbnail size. A
    /// composite that faults falls through to the plain decode: the two decoders disagree about what they
    /// will read, and a file only one declines is a ghostly tile at worst, not a corrupt cache entry.</summary>
    private static Bitmap DecodeMapThumb(Stream png, bool rmo)
    {
        const int maxDim = Core.Workbench.ThumbnailCache.MaxDim;
        if (!rmo) return Bitmap.DecodeToWidth(png, maxDim);
        using var raw = new MemoryStream();
        png.CopyTo(raw);
        var bytes = raw.ToArray();
        try
        {
            using var opaque = new MemoryStream(TextureExport.OpaquePng(bytes, maxDim), writable: false);
            return Bitmap.DecodeToWidth(opaque, maxDim);
        }
        catch (Exception)
        {
            using var plain = new MemoryStream(bytes, writable: false);
            return Bitmap.DecodeToWidth(plain, maxDim);
        }
    }

    /// <summary>Decode the cached PNG OFF the UI thread, then settle the row's thumb state on the
    /// dispatcher. A null path or decode fault settles to "no preview" and is never cached, so re-selecting
    /// retries. A completion superseding <paramref name="generation"/> is rejected and its bitmap disposed.</summary>
    private static void AssignThumb(WorkbenchMapVm m, string? path, int generation, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;

        Bitmap? bmp = null;
        if (path is not null)
        {
            bool decodeFailed = false;
            try
            {
                using var fs = File.OpenRead(path);
                try { bmp = DecodeMapThumb(fs, m.IsRmo); }
                catch { bmp = null; decodeFailed = true; }   // opened fine but won't decode → corrupt PNG
            }
            catch { bmp = null; }   // couldn't open (mid-write / sharing lock) — keep the file, retry later
            if (decodeFailed)
            {
                // Evict a corrupt cached PNG or every retry loops on the same bad file. The file is closed
                // above, so the delete can succeed on Windows.
                try { File.Delete(path); } catch { /* best-effort — another worker may be replacing it */ }
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested || !m.IsCurrentThumbRequest(generation)) { bmp?.Dispose(); return; }
            if (bmp is null) m.MarkThumbFailed();
            else m.SetThumb(bmp);
        });
    }

    // ---- node construction ----

    /// <summary>"1 bone" / "3 bones" — regular +s nouns only.</summary>
    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>The tree a resolved subject becomes. Internal so the properties the model carries down to the
    /// nodes can be pinned without a game install behind the load that normally calls it.</summary>
    internal static WorkbenchNodeVm BuildSubjectNode(string display, SubjectModel model, WorkbenchSubjectRef subjectRef)
    {
        int matCount = model.Parts.Sum(p => p.Materials.Count);
        var variantNote = model.Parts.Any(p => MeshName.SplitVariant(p.Token).Variant is not null)
            ? "Includes Dorm/Fight variant parts."
            : null;

        var subject = new WorkbenchNodeVm
        {
            Kind = WorkbenchNodeKind.Subject,
            Title = display,
            Subtitle = "",
            InspectorHeader = display,
            InspectorDetail = $"{Count(model.Parts.Count, "part")} · {Count(matCount, "material")}",
            InspectorNote = variantNote,
            // the skeleton's own channel joins the display here: the inspector is where a degraded
            // read should say so, whatever the measurement side makes of it
            InspectorProblems = model.SkeletonProblem is { } rig
                ? model.Problems.Append(rig).ToList() : model.Problems,
            Subject = subjectRef,
            // gated rather than opening on an empty session — a combined session carries the skinned parts
            AllPartsStatic = model.AllPartsStatic,
        };

        // Owner mesh names per texture (the parts' lod0 slot names), so a map's Users is the renderer-exact
        // "used by" set and never a prefix+token derivation.
        var ownersByTexture = OwnerMeshNamesByTexture(model);
        foreach (var part in model.Parts)
            subject.Children.Add(BuildPartNode(part, subjectRef, ownersByTexture));

        subject.Children.Add(BuildSkeletonNode(model, subjectRef));
        return subject;
    }

    /// <summary>Each texture name → the lod0 slot names of the parts whose materials bind it, so a
    /// first-touch materialize records the right Users. A part with no recipe slot name contributes none.</summary>
    private static Dictionary<string, List<string>> OwnerMeshNamesByTexture(SubjectModel model)
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in model.Parts)
        {
            if (part.SlotName.Length == 0) continue;
            foreach (var map in part.Materials.SelectMany(m => m.Maps))
            {
                if (!owners.TryGetValue(map.TextureName, out var list)) owners[map.TextureName] = list = new();
                if (!list.Contains(part.SlotName, StringComparer.Ordinal)) list.Add(part.SlotName);
            }
        }
        return owners;
    }

    private static WorkbenchNodeVm BuildPartNode(SubjectPart part, WorkbenchSubjectRef subject,
        Dictionary<string, List<string>> ownersByTexture)
    {
        var node = new WorkbenchNodeVm
        {
            Kind = WorkbenchNodeKind.Part,
            Title = part.Token,
            Subtitle = part.SlotName,
            Problem = part.Problem,
            InspectorHeader = part.Token,
            InspectorDetail = part.SlotName,
            InspectorNote = "Detail levels are handled automatically.",
            Subject = subject,
            PartToken = part.Token,
            Recipe = part.ToRecipePart(),
            // gates the references open rather than opening a combined session this part is not in
            IsStaticPart = part.IsStatic,
            // renderer slot order, placeholders included = the preview's submesh alignment
            SubmeshBaseMaps = part.Materials
 .Select(m => m.Maps.FirstOrDefault(map => MaterialResolver.IsBaseColor(map.Slot)))
 .ToArray(),
            SubmeshRmoMaps = part.Materials
 .Select(m => m.Maps.FirstOrDefault(map => MaterialResolver.IsRmo(map.Slot)))
 .ToArray(),
        };
        var bound = BoundSubmeshesByMap(part.Materials);
        for (int mi = 0; mi < part.Materials.Count; mi++)
            node.Children.Add(BuildMaterialNode(part.Materials[mi], mi, part.Token, part.SlotName, subject,
                ownersByTexture, bound));
        return node;
    }

    /// <summary>(map slot kind, texture, bundle) → the part's renderer material slots binding it, in slot
    /// order. Material order IS submesh order, so this is also which donor submeshes a map authored on one of
    /// those cards lands on: one stock map dressing three of the part's slots is one image on three
    /// submeshes, which is how the game draws it. A map whose slot is none of the three shippable kinds is
    /// absent — nothing can be authored for it.</summary>
    private static Dictionary<(DonorMapSlot Slot, string Texture, string Bundle), List<int>>
        BoundSubmeshesByMap(IReadOnlyList<SubjectMaterial> materials)
    {
        var bound = new Dictionary<(DonorMapSlot, string, string), List<int>>();
        for (int mi = 0; mi < materials.Count; mi++)
            foreach (var map in materials[mi].Maps)
            {
                if (DonorSlotOf(map.Slot) is not { } slot) continue;
                var key = (slot, map.TextureName, map.BundleId);
                if (!bound.TryGetValue(key, out var list)) bound[key] = list = new List<int>();
                if (!list.Contains(mi)) list.Add(mi);
            }
        return bound;
    }

    /// <summary>The donor slot a shader texture slot ships as, or null when the build has no slot for it.
    /// THE map from shader slot to donor slot, so the card, the drop and the record agree.</summary>
    internal static DonorMapSlot? DonorSlotOf(string shaderSlot) =>
        MaterialResolver.IsBaseColor(shaderSlot) ? DonorMapSlot.BaseColor
        : MaterialResolver.IsNormal(shaderSlot) ? DonorMapSlot.Normal
        : MaterialResolver.IsRmo(shaderSlot) ? DonorMapSlot.Rmo
        : null;

    private static WorkbenchNodeVm BuildMaterialNode(SubjectMaterial mat, int materialIndex, string partToken,
        string partMeshName, WorkbenchSubjectRef subject, Dictionary<string, List<string>> ownersByTexture,
        Dictionary<(DonorMapSlot Slot, string Texture, string Bundle), List<int>> boundSubmeshes)
    {
        string title = mat.IsPlaceholder ? "empty slot" : mat.Name;
        var node = new WorkbenchNodeVm
        {
            Kind = WorkbenchNodeKind.Material,
            Title = title,
            Subtitle = mat.Maps.Count > 0 ? Count(mat.Maps.Count, "map") : "",
            Problem = mat.Problem,
            InspectorHeader = title,
            InspectorDetail = $"used by {partToken}",
            InspectorNote = mat.IsPlaceholder
                ? "An empty renderer slot. It holds the submesh order."
                : null,
            Subject = subject,
            PartToken = partToken,
            MaterialIndex = materialIndex,
        };
        // fallback = this part's own mesh name, so a map with no other owner still records a user
        var ownFallback = partMeshName.Length > 0 ? new List<string> { partMeshName } : new List<string>();
        foreach (var map in mat.Maps)
            node.Maps.Add(WorkbenchNodeVm.MapRow(map, subject,
                ownersByTexture.TryGetValue(map.TextureName, out var o) ? o : ownFallback,
                partToken,
                DonorSlotOf(map.Slot) is { } slot
                && boundSubmeshes.TryGetValue((slot, map.TextureName, map.BundleId), out var bound)
                    ? bound : null));
        return node;
    }

    private static WorkbenchNodeVm BuildSkeletonNode(SubjectModel model, WorkbenchSubjectRef subjectRef)
    {
        if (model.Skeleton is { } sk)
            return new WorkbenchNodeVm
            {
                Kind = WorkbenchNodeKind.Skeleton,
                Title = "Skeleton",
                Subtitle = Count(sk.BoneCount, "bone"),
                Subject = subjectRef,
                InspectorHeader = "Skeleton",
                InspectorDetail = Count(sk.BoneCount, "bone"),
                // SkeletonOutline owns the hierarchy and the default-expansion rule; the VM mirrors it.
                SkeletonTree = SkeletonOutline.Tree(sk.Bones).Select(n => new SkeletonNodeVm(n)).ToList(),
            };
        return new WorkbenchNodeVm
        {
            Kind = WorkbenchNodeKind.Skeleton,
            Title = "Skeleton",
            Subtitle = "structure unavailable",
            Subject = subjectRef,
            Problem = "The rig hierarchy couldn't be read for this subject.",
            InspectorHeader = "Skeleton",
            InspectorDetail = "structure unavailable",
        };
    }

    /// <summary>What a stem the roster doesn't carry is read as: a plain outfit, so the workbench still reads
    /// its prefab structure. By the stem formula, since a synthesized outfit has no mesh-prefix override and no
    /// curated <see cref="SubjectRoute"/> to take.</summary>
    private static Outfit FallbackOutfit(string stem) => new(0, stem, OutfitKind.Other);

    /// <summary>One subject's model, through the session memo where there is one and the subject's outfit came
    /// from the roster. Runs off the UI thread; the memo is built for that.</summary>
    private SubjectModel BuildSubjectModel(CatalogIndex catalog, Outfit outfit, string character, bool memoize) =>
        _subjectModels is { } memo && memoize
            ? memo.GetOrBuild(character, outfit.Stem,
                () => SubjectModelBuilder.Build(catalog, _tryDeobfuscate, outfit, character))
            : SubjectModelBuilder.Build(catalog, _tryDeobfuscate, outfit, character);


    // ---- selection → demand-driven previews + lazy texture meta ----

    /// <summary>Selecting a node is the preview demand signal: each load is memoized and retries a prior
    /// failure. It is also the signal that the modder is working on an outfit, which starts preparing it —
    /// every node under a subject root carries that subject's ref, so landing anywhere inside an outfit
    /// rolls up to the one owning subject, and a selection RESTORED when the tree lands counts the same.</summary>
    partial void OnSelectedNodeChanged(WorkbenchNodeVm? value)
    {
        if (value?.Subject is { } visited) _shell?.PrewarmSubject(visited);
        if (value is { Kind: WorkbenchNodeKind.Material } node && node.Maps.Count > 0)
        {
            LoadMapMeta(node);
            EnsureMapThumbs(node);
        }
        else if (value is { Kind: WorkbenchNodeKind.Part } part)
        {
            EnsureMeshPreview(part);
            EnsureMeshReplaceGate(part);
        }
    }

    /// <summary>Settle the selected part's Open-in-Blender gate: read the recoverable-skin answer for its
    /// GAME mesh once, off the UI thread, and assign it. Demand-driven and memoized — the read costs a
    /// bundle deobfuscate plus a Mesh deserialize, and only the selected node renders the Open button.
    /// Only a REFUSAL is assigned: a part whose bundle or mesh wouldn't read answers null, since that
    /// failure has its own loud route and disabling Open would blame the mesh for something else. The read
    /// is HANDED BACK as well as assigned — a click can land before it settles, so the verb awaits this
    /// rather than reading a property that is still null.</summary>
    private Task<Core.Migoto.StreamDump.SkinRefusal?> EnsureMeshReplaceGate(WorkbenchNodeVm part)
    {
        if (part.MeshReplaceGate is { } asked) return asked;
        if (part.Recipe is not { } recipe || (!recipe.IsRecipeBacked && !recipe.IsSmrBacked))
            return part.MeshReplaceGate = Task.FromResult<Core.Migoto.StreamDump.SkinRefusal?>(null);
        var token = _cts?.Token ?? CancellationToken.None;
        return part.MeshReplaceGate = Task.Run(() =>
        {
            string? bundle;
            if (recipe.IsRecipeBacked)
            {
                CatalogIndex? catalog;
                try { catalog = _catalog?.Invoke(); } catch { catalog = null; }
                bundle = catalog?.ResolveAddress(recipe.MeshAddress);
            }
            else bundle = recipe.MeshBundle;
            if (bundle is null || token.IsCancellationRequested) return null;

            Core.Migoto.StreamDump.SkinRefusal? blocked = null;
            RunGated(token, () => blocked = PartSkinGate.Blocked(_tryDeobfuscate, bundle, recipe.SlotName,
                recipe.IsRecipeBacked ? 0 : recipe.MeshPathId));
            if (blocked is not { } why || token.IsCancellationRequested) return blocked;
            Dispatcher.UIThread.Post(() =>
            {
                if (!token.IsCancellationRequested) part.MeshReplaceBlock = why;
            });
            return blocked;
        }, token);
    }

    /// <summary>Read each map's dimensions lazily off the UI thread, never at build time and never decoding
    /// pixels. A failure shows "unavailable", never a crash. A donor-authored row reads its own PNG header.
    ///
    /// <para>Both readers run through the row's ONE dimensions generation: a row can have a bundle read and
    /// an authored read in flight at once, and the size line belongs to whichever was asked for LAST, not to
    /// whichever finishes last.</para></summary>
    private void LoadMapMeta(WorkbenchNodeVm node)
    {
        var token = _cts?.Token ?? CancellationToken.None;
        var proj = _project();
        foreach (var map in node.Maps)
        {
            // The card shows the authored image, so its size is the one to report — but only while that
            // file is there; a bundle-backed row falls through to the game texture's meta below.
            if (map.AuthoredPath is { } authored && File.Exists(authored))
            { LoadAuthoredMapDims(map, authored, token); continue; }
            // An EDITED map's size is its workspace file's, not the game texture's: a drop can have replaced
            // it at any size, and the meta cache below still holds the stock one keyed by game identity.
            if (map.IsEdited && WorkspacePngFor(proj, map) is { } edited && File.Exists(edited))
            { LoadWorkspaceMapDims(map, edited, token); continue; }
            // No bundle AND no file: the send-back's row went away but its material stayed. Nothing to
            // read — say so instead of hunting a bundle.
            if (!map.HasBundle) { map.BeginDimsRequest(); map.Dimensions = "unavailable"; continue; }

            var key = _catalogVersion + "|" + map.BundleId + "|" + map.TextureName;
            if (_metaCache.TryGetValue(key, out var cached))
            { map.BeginDimsRequest(); map.Dimensions = cached; continue; }
            // Retry a prior FAILED read on re-select: only a successful read is cached, so a row stuck at
            // "unavailable" (often a transient sharing lock) must fall through and re-issue.
            if (map.Dimensions != "…" && map.Dimensions != "unavailable") continue;

            var m = map;
            var generation = m.BeginDimsRequest();
            // Gated like the thumb load — a dimension read is selection-triggered deobfuscate work too.
            Task.Run(() => RunGated(token, () =>
            {
                string dims;
                bool drop = false;   // an HDR/float format the codec can't re-encode — hide the row
                try
                {
                    var dec = _tryDeobfuscate(m.BundleId);
                    var probe = dec is null ? null : TextureExport.Probe(dec, m.TextureName);
                    dims = probe is { } p ? $"{p.Width}×{p.Height}" : "unavailable";
                    if (probe is { Authorable: false }) drop = true;
                }
                catch { dims = "unavailable"; }
                // Cache only a SUCCESSFUL read: a failure is often a transient sharing lock while the game
                // runs, and must not poison the cache.
                Dispatcher.UIThread.Post(() =>
                {
                    // A newer read owns the row now, including the removal below.
                    if (!m.IsCurrentDimsRequest(generation)) return;
                    // A map whose live format can't be authored as a PNG is removed rather than shown as a
                    // dead row. Done here because the format is read for the dims anyway.
                    if (drop) { node.Maps.Remove(m); return; }
                    if (dims != "unavailable") _metaCache[key] = dims;
                    m.Dimensions = dims;
                });
            }));
        }
    }

    /// <summary>A donor-authored row's dimensions from its PNG header, off the UI thread. NEVER cached: each
    /// send-back rewrites that file in place under the same name, so a cached size would go stale silently —
    /// and for the same reason the path alone can't tell two reads apart, so the generation guard does.</summary>
    private void LoadAuthoredMapDims(WorkbenchMapVm map, string authoredPath, CancellationToken token)
    {
        var generation = map.BeginDimsRequest();
        Task.Run(() => RunGated(token, () =>
        {
            var dims = PngInfo.TrySize(authoredPath) is { } size ? $"{size.Width}×{size.Height}" : "unavailable";
            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                if (!map.IsCurrentDimsRequest(generation)) return;
                if (!string.Equals(map.AuthoredPath, authoredPath, StringComparison.OrdinalIgnoreCase)) return;
                map.Dimensions = dims;
            });
        }));
    }

    /// <summary>A bundle-backed row's dimensions from its WORKSPACE PNG rather than the game texture's meta,
    /// off the UI thread. Same no-cache rule as <see cref="LoadAuthoredMapDims"/>, and for the same reason:
    /// once a map has been replaced on disk, its size is the file's, and the file changes under one name.
    /// A row that shows an authored file keeps THAT file's size — the authored image is what its card
    /// displays.</summary>
    private void LoadWorkspaceMapDims(WorkbenchMapVm map, string workspacePng, CancellationToken token)
    {
        var generation = map.BeginDimsRequest();
        Task.Run(() => RunGated(token, () =>
        {
            var dims = PngInfo.TrySize(workspacePng) is { } size ? $"{size.Width}×{size.Height}" : "unavailable";
            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested) return;
                if (!map.IsCurrentDimsRequest(generation)) return;
                if (map.AuthoredPath is { } a && File.Exists(a)) return;
                map.Dimensions = dims;
            });
        }));
    }

    // ---- filter ----

    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ClearFilter() => Filter = "";

    // Coalesce a burst of haystack invalidations into one re-filter on the next dispatcher turn.
    private bool _filterRefreshQueued;

    private void WireFilterInvalidation(WorkbenchNodeVm node)
    {
        node.HaystackInvalidated = OnNodeHaystackInvalidated;
        foreach (var c in node.Children) WireFilterInvalidation(c);
    }

    private void OnNodeHaystackInvalidated()
    {
        if (string.IsNullOrEmpty(Filter) || _filterRefreshQueued) return;
        _filterRefreshQueued = true;
        Dispatcher.UIThread.Post(() => { _filterRefreshQueued = false; ApplyFilter(); });
    }

    /// <summary>Substring, case-insensitive, ALL terms must hit, over part tokens / material names /
    /// texture names. A match reveals the node, its ancestors and its descendants.</summary>
    private void ApplyFilter()
    {
        var terms = (Filter ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var root in Nodes) FilterNode(root, terms, ancestorMatched: false);
        NoMatches = terms.Length > 0 && Nodes.Count > 0 && Nodes.All(n => !n.IsVisible);
    }

    // Visible when it matches, an ancestor matches (a match's whole subtree), or a descendant matches (the
    // path to it). Returns whether THIS subtree contains a match — an ancestor match does not count.
    private static bool FilterNode(WorkbenchNodeVm node, IReadOnlyList<string> terms, bool ancestorMatched)
    {
        if (terms.Count == 0)
        {
            node.IsVisible = true;
            foreach (var c in node.Children) FilterNode(c, terms, ancestorMatched: false);
            return true;
        }

        bool self = node.SelfMatches(terms);
        bool anyChild = false;
        foreach (var c in node.Children)
            anyChild |= FilterNode(c, terms, ancestorMatched || self);

        node.IsVisible = self || anyChild || ancestorMatched;
        if (anyChild) node.IsExpanded = true;   // reveal the path to a matching descendant
        return self || anyChild;
    }

    // ---- verbs ----------------------------------------------------------------------------------
    // The verbs live here; the heavy plumbing is the injected shell's. Each verb sets the invoking node/card
    // busy, awaits the shell, then refreshes ✎ / materialized state.

    private IProgress<string> StatusProgress => new Progress<string>(s => Status = s);

    // A workbench-wide "verb in flight" gate: per-node IsBusy drives the button disable but doesn't exclude
    // ACROSS nodes, and two overlapping verbs would race the single-writer commit. UI-thread only.
    private bool _verbInFlight;

    /// <summary>Shell work that writes the same workspace files the verbs do without being one — a send-back
    /// apply. It is kept apart from <see cref="_verbInFlight"/> so neither can release the other's hold; the
    /// verbs test both. UI-thread only, like the flag beside it.</summary>
    private bool _shellWriteInFlight;

    /// <summary>Whether anything holding the workbench's single-writer gate is in flight.</summary>
    private bool VerbsBusy => _verbInFlight || _shellWriteInFlight;

    /// <summary>Hold the verb gate for shell work that did not start as a verb. A send-back apply rewrites
    /// the workspace glbs a verb would, and one landing on top of it would race that write, so the verbs
    /// report busy for as long as the hold stands.</summary>
    internal IDisposable HoldVerbs()
    {
        _shellWriteInFlight = true;
        return new VerbHold(this);
    }

    /// <summary>Take the gate and WAIT for a verb already in flight to finish. Refusing later verbs is only
    /// half the exclusion: a Materialize-all or an Open-all that started before the hold keeps writing the
    /// same files, so shell work that overwrites them has to let it land first. The refusal starts at once,
    /// as the plain hold's does, so nothing new joins the queue while this waits.</summary>
    internal async Task<IDisposable> HoldVerbsAsync()
    {
        var hold = HoldVerbs();
        await VerbIdle();
        return hold;
    }

    /// <summary>Completed by the in-flight verb's own exit. One source serves every waiter, and continuations
    /// run asynchronously so a waiter can never resume inside the <c>finally</c> that released it.</summary>
    private TaskCompletionSource? _verbIdle;

    /// <summary>A task that finishes when no verb is in flight — already finished when none is.</summary>
    private Task VerbIdle() => _verbInFlight
        ? (_verbIdle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task
        : Task.CompletedTask;

    /// <summary>A verb's exit: drop the flag and release whatever was waiting on it. Every verb's
    /// <c>finally</c> ends here, so no route can clear the flag without waking the waiters.</summary>
    private void EndVerb()
    {
        _verbInFlight = false;
        var idle = _verbIdle;
        _verbIdle = null;
        idle?.TrySetResult();
    }

    private sealed class VerbHold : IDisposable
    {
        private readonly WorkbenchVm _vm;
        public VerbHold(WorkbenchVm vm) => _vm = vm;
        public void Dispose() => _vm._shellWriteInFlight = false;
    }

    /// <summary>A verb refused because another one holds the gate. The buttons disable per NODE, so a verb
    /// on a second node looks live and its click would otherwise land on nothing said at all.</summary>
    private void ReportVerbBusy() => Status = "Busy with the current step. Try again when it finishes.";

    /// <summary>A mesh verb refused because the part's mesh can't be replaced. Same line the disabled button
    /// carries, said on the status channel for a click that reached the command another way.</summary>
    private void ReportMeshUnreplaceable(Core.Migoto.StreamDump.SkinRefusal refusal) =>
        Status = BlenderGate.Blocked(refusal);

    /// <summary>Post a one-line status to the workbench status line.</summary>
    public void ReportStatus(string message) => Status = message;

    /// <summary>Reveal the subject's export folder, through the shell.</summary>
    [RelayCommand]
    private void ShowSubjectFolder(WorkbenchNodeVm? node)
    {
        if (_shell is null || node is not { Kind: WorkbenchNodeKind.Subject, Subject: { } subj }) return;
        _shell.ShowSubjectInFolder(subj);
    }

    /// <summary>Copy a material name, through the shell so the workbench binds to its OWN DataContext.</summary>
    [RelayCommand]
    private async Task CopyName(string? text)
    {
        if (_shell is not null) await _shell.CopyTextAsync(text);
    }

    /// <summary>Advance to the Build step, through the shell so the footer binds to its OWN DataContext.</summary>
    [RelayCommand]
    private void GoToBuild() => _shell?.GoToBuild();

    /// <summary>Remove the whole subject. The shell owns the confirm and the sweep, and rebuilds this tree,
    /// so the node vanishes when it returns.</summary>
    [RelayCommand]
    private async Task RemoveSubject(WorkbenchNodeVm? node)
    {
        if (_shell is null || node is not { Kind: WorkbenchNodeKind.Subject, Subject: { } subj }) return;
        if (node.IsBusy || VerbsBusy) { ReportVerbBusy(); return; }
        _verbInFlight = true; node.IsBusy = true;
        try { await _shell.RemoveSubjectAsync(subj); }
        finally { node.IsBusy = false; EndVerb(); }
    }

    /// <summary>Open a part in Blender, with the rest of the outfit along for context.</summary>
    [RelayCommand]
    private Task OpenPartInBlender(WorkbenchNodeVm? node) =>
        OpenPart(node, withReferences: true, (shell, subj, recipe) =>
            shell.OpenPartInBlenderAsync(subj, recipe, SubjectRecipes(subj), StatusProgress));

    /// <summary>Open a part in Blender on its own, with no outfit around it.</summary>
    [RelayCommand]
    private Task OpenPartAloneInBlender(WorkbenchNodeVm? node) =>
        OpenPart(node, withReferences: false,
            (shell, subj, recipe) => shell.OpenPartAloneInBlenderAsync(subj, recipe, StatusProgress));

    /// <summary>What both part-open verbs share: the same refusals, the same busy gate, the same node state.
    /// Only <paramref name="open"/> differs — which session the shell builds. Two copies of this would be two
    /// places for the unreplaceable-mesh rule to drift apart. <paramref name="withReferences"/> names the
    /// session being asked for, since one refusal reaches only the combined one.</summary>
    private async Task OpenPart(WorkbenchNodeVm? node, bool withReferences,
        Func<IWorkbenchShell, WorkbenchSubjectRef, Core.Export.RecipePart, Task> open)
    {
        if (_shell is null || node is not { Kind: WorkbenchNodeKind.Part, Subject: { } subj, Recipe: { } recipe }) return;
        // The button is disabled for an unreplaceable mesh; the verb refuses too, so the rule holds however
        // the command is reached. Ahead of the busy gate: a wait would imply the click works later.
        if (node.MeshReplaceBlock is { } blocked) { ReportMeshUnreplaceable(blocked); return; }
        // The combined session carries the SKINNED parts, so a static part opened with references would get
        // back a session it is not in. Its lone open is unaffected, and the line names it.
        if (withReferences && node.IsStaticPart) { Status = BlenderGate.StaticPart; return; }
        // Same order the disabled button's hover uses: a live session on this part outranks a running verb.
        if (node.IsOpenInBlender) { Status = BlenderGate.AlreadyOpen; return; }
        if (node.IsBusy || VerbsBusy) { ReportVerbBusy(); return; }
        _verbInFlight = true; node.IsBusy = true;
        try
        {
            // Selection starts the read; a click can beat it. Settling it under the held verb gate is what
            // keeps the answer a property of the mesh rather than of how fast the click landed.
            if (await EnsureMeshReplaceGate(node) is { } late) { ReportMeshUnreplaceable(late); return; }
            await open(_shell, subj, recipe);
        }
        // A click racing a tree teardown loses to it: the gate memo's token cancels and the await throws
        // here. The teardown rebuilds this tree, so there is nothing left for the click to open.
        catch (OperationCanceledException) { }
        finally { node.IsBusy = false; EndVerb(); RefreshNodeStates(); }
    }

    /// <summary>Every part recipe of a subject, read off the tree. Empty when it isn't in the tree.</summary>
    private IReadOnlyList<Core.Export.RecipePart> SubjectRecipes(WorkbenchSubjectRef subject)
    {
        foreach (var node in Nodes)
        {
            if (node.Kind != WorkbenchNodeKind.Subject || node.Subject is not { } s) continue;
            if (!string.Equals(s.Character, subject.Character, StringComparison.Ordinal)
                || !string.Equals(s.Stem, subject.Stem, StringComparison.Ordinal)) continue;
            return node.Children.Where(c => c.Kind == WorkbenchNodeKind.Part && c.Recipe is not null)
                                .Select(c => c.Recipe!).ToList();
        }
        return Array.Empty<Core.Export.RecipePart>();
    }

    /// <summary>Revert a part's edited glb to its pristine original (glb only).</summary>
    [RelayCommand]
    private async Task RevertPart(WorkbenchNodeVm? node)
    {
        if (_shell is null || node is not { Kind: WorkbenchNodeKind.Part, Subject: { } subj } || !node.CanRevert) return;
        if (VerbsBusy) { ReportVerbBusy(); return; }
        _verbInFlight = true; node.IsBusy = true;
        try { await _shell.RevertPartAsync(subj, node.PartToken, StatusProgress); }
        finally { node.IsBusy = false; EndVerb(); RefreshNodeStates(); }
    }

    /// <summary>Flip a part's Hide toggle: workbench STATE the build derives a Hide verb from, never a verb
    /// authored here. Instant — hiding needs no editable copy.</summary>
    [RelayCommand]
    private void ToggleHidden(WorkbenchNodeVm? node)
    {
        if (_shell is null || node is not { Kind: WorkbenchNodeKind.Part, Subject: { } subj, Recipe: { } recipe }) return;
        var proj = _project();
        bool hidden = !node.IsHiddenInMod;
        proj.SetHidden(subj.Character, subj.Stem, recipe.SlotName, hidden);
        node.IsHiddenInMod = hidden;
        Status = hidden ? $"{node.Title}: hidden in the built mod" : $"{node.Title}: shown in the built mod";
        _shell.AutoSaveProject();
    }

    /// <summary>Open every part of the subject in one Blender session (materializing any missing first).</summary>
    [RelayCommand]
    private async Task OpenAllParts(WorkbenchNodeVm? node)
    {
        if (_shell is null || node is not { Kind: WorkbenchNodeKind.Subject, Subject: { } subj }) return;
        // The button is disabled for an all-static subject; the verb refuses too, so the rule holds however
        // the command is reached. Ahead of the busy gate: a wait would imply the click works later.
        if (node.AllPartsStatic) { Status = BlenderGate.StaticOnly; return; }
        if (node.IsBusy || VerbsBusy) { ReportVerbBusy(); return; }
        var recipes = node.Children.Where(c => c.Kind == WorkbenchNodeKind.Part && c.Recipe is not null)
 .Select(c => c.Recipe!).ToList();
        if (recipes.Count == 0) return;
        _verbInFlight = true; node.IsBusy = true;
        try { await _shell.OpenAllPartsInBlenderAsync(subj, recipes, StatusProgress); }
        finally { node.IsBusy = false; EndVerb(); RefreshNodeStates(); }
    }

    /// <summary>A Materialize-all batch is running; drives the Cancel button beside the progress line.</summary>
    [ObservableProperty] private bool _isMaterializingAll;

    /// <summary>NAVIGATION-PROOF: NOT the tree-rebuild token (<see cref="_cts"/>, which a Pick↔Edit hop
    /// cancels), so hopping away and back mid-batch leaves the batch running. Cancelled by the Cancel button
    /// and by <see cref="Reset"/>.</summary>
    private CancellationTokenSource? _materializeAllCts;

    /// <summary>Materialize every part + texture of the subject, with per-item status. Runs under a
    /// navigation-proof token, so it survives a Pick↔Edit hop.</summary>
    [RelayCommand]
    private async Task MaterializeAll(WorkbenchNodeVm? node)
    {
        if (_shell is null || node is not { Kind: WorkbenchNodeKind.Subject, Subject: { } subj }) return;
        if (node.IsBusy || VerbsBusy) { ReportVerbBusy(); return; }
        var items = CollectMaterializeItems(node);
        if (items.Count == 0) return;
        _materializeAllCts?.Cancel(); _materializeAllCts?.Dispose();
        _materializeAllCts = new CancellationTokenSource();
        var token = _materializeAllCts.Token;
        _verbInFlight = true; node.IsBusy = true; IsMaterializingAll = true;
        try { await _shell.MaterializeAllAsync(subj, items, StatusProgress, token); }
        finally { node.IsBusy = false; EndVerb(); IsMaterializingAll = false; RefreshNodeStates(); }
    }

    /// <summary>Cancel a running Materialize-all batch; it stops between items.</summary>
    [RelayCommand]
    private void CancelMaterializeAll() => _materializeAllCts?.Cancel();

    /// <summary>Cancel a running Materialize-all from OUTSIDE the workbench (the window-close guard).</summary>
    public void RequestCancelMaterializeAll() => _materializeAllCts?.Cancel();

    /// <summary>What Open says on a blanked slot with nothing behind it.</summary>
    internal const string BlankedSlotNotEditable =
        "No image on a blanked slot. The build ships its own flat map here.";

    /// <summary>Open a map's texture in the image editor, materializing on first touch. A row carrying an
    /// authored file opens THAT file, bundle or not — the authored PNG is what the build ships, so an edit to
    /// the game texture behind it would be discarded.</summary>
    [RelayCommand]
    private async Task OpenMap(WorkbenchMapVm? map)
    {
        if (_shell is null || map is not { Subject: { } subj }) return;
        if (map.IsBusy || VerbsBusy) { ReportVerbBusy(); return; }
        if (!map.HasBundle && map.AuthoredPath is null)
        {
            // A blanked slot never had an image, as opposed to one whose authored file went missing: the
            // build's own flat map is what ships there, and it names no file to open.
            Status = map.IsBlanked ? BlankedSlotNotEditable
                : $"{map.TextureName} isn't on disk. Send the part back from Blender again.";
            return;
        }
        _verbInFlight = true; map.IsBusy = true;
        try
        {
            if (map.AuthoredPath is { } authored)
                await _shell.OpenAuthoredMapAsync(authored, StatusProgress);
            else await _shell.OpenMapInEditorAsync(subj, map.TextureName, map.BundleId, map.OwnerMeshNames, StatusProgress);
        }
        finally { map.IsBusy = false; EndVerb(); RefreshNodeStates(); }
    }

    /// <summary>Open a map's UV guide, built from game data on first touch so it works before anything is
    /// materialized. Samplers are read off THIS tree, with the submesh indices that bind the texture
    /// (material order == submesh order, placeholders included).</summary>
    [RelayCommand]
    private async Task OpenMapUvGuide(WorkbenchMapVm? map)
    {
        if (_shell is null || map is not { Subject: { } subj }) return;
        if (map.IsBusy || VerbsBusy) { ReportVerbBusy(); return; }
        // The button is disabled for a bundle-less row; the verb refuses too, so the rule holds however the
        // command is reached.
        if (!map.HasBundle) { Status = WorkbenchMapVm.NoUvGuideOnDonorMap + "."; return; }
        var samplers = CollectUvSamplers(Nodes, subj, map.TextureName, map.BundleId, _project());
        _verbInFlight = true; map.IsBusy = true;
        try { await _shell.OpenMapUvGuideAsync(subj, map.TextureName, map.BundleId, samplers, StatusProgress); }
        finally { map.IsBusy = false; EndVerb(); RefreshNodeStates(); }
    }

    /// <summary>Every (lod0 mesh m_Name, recipe mesh address, submesh index, edited workspace glb or null)
    /// of <paramref name="subject"/> whose renderer material references the texture — the UNION the guide
    /// draws, since a shared map is painted once for all its parts. Only an EDITED part rides its glb, so the
    /// wireframe shows the mod's own UVs. Material order == submesh order within a part (the SubjectModel
    /// invariant); a part with no recipe slot name has no addressable mesh and is skipped. Bundle matches
    /// case-insensitive, name ordinal — the same identity the materialize route commits under.</summary>
    internal static List<(string MeshName, string MeshAddress, int Submesh, string? ModdedGlb)> CollectUvSamplers(
        IEnumerable<WorkbenchNodeVm> roots, WorkbenchSubjectRef subject, string textureName, string bundleId,
        ModProject project)
    {
        var samplers = new List<(string, string, int, string?)>();
        foreach (var subjNode in roots)
        {
            if (subjNode.Kind != WorkbenchNodeKind.Subject || subjNode.Subject is not { } s) continue;
            if (!string.Equals(s.Character, subject.Character, StringComparison.Ordinal)
                || !string.Equals(s.Stem, subject.Stem, StringComparison.Ordinal)) continue;
            foreach (var part in subjNode.Children)
            {
                if (part.Kind != WorkbenchNodeKind.Part || string.IsNullOrEmpty(part.Recipe?.SlotName)) continue;

                // Only an edited part rides its glb — an unedited materialized glb has vanilla UVs anyway.
                // Any resolve/IO fault falls back to null, i.e. the game mesh.
                string? moddedGlb = null;
                try
                {
                    var target = PartMeshTarget(project, part);
                    if (target is not null && project.IsEdited(target))
                    {
                        var repl = project.Resolve(target.ReplaceFile);
                        if (File.Exists(repl)) moddedGlb = repl;
                    }
                }
                catch { moddedGlb = null; }

                for (int sub = 0; sub < part.Children.Count; sub++)
                    foreach (var m in part.Children[sub].Maps)
                        if (string.Equals(m.TextureName, textureName, StringComparison.Ordinal)
                            && string.Equals(m.BundleId, bundleId, StringComparison.OrdinalIgnoreCase))
                        { samplers.Add((part.Recipe!.SlotName, part.Recipe!.MeshAddress, sub, moddedGlb)); break; }
            }
        }
        return samplers;
    }

    /// <summary>Revert a map's edited PNG to its pristine original (PNG only).</summary>
    [RelayCommand]
    private async Task RevertMap(WorkbenchMapVm? map)
    {
        if (_shell is null || map is not { Subject: { } subj } || !map.CanRevert) return;
        if (VerbsBusy) { ReportVerbBusy(); return; }
        _verbInFlight = true; map.IsBusy = true;
        try { await _shell.RevertMapAsync(subj, map.TextureName, map.BundleId, StatusProgress); }
        finally { map.IsBusy = false; EndVerb(); RefreshNodeStates(); }
    }

    /// <summary>The subject's parts then its distinct textures as materialize items. Texture identity is
    /// (bundle, name) — the SAME key <see cref="Materializer.TextureTarget"/> commits under. Same-named maps
    /// from DIFFERENT bundles are distinct assets; only an EXACT repeat folds, unioning its owner mesh names
    /// so the later occurrence's users are never dropped.</summary>
    internal static List<MaterializeItem> CollectMaterializeItems(WorkbenchNodeVm subject)
    {
        var items = new List<MaterializeItem>();
        foreach (var part in subject.Children.Where(c => c.Kind == WorkbenchNodeKind.Part))
            items.Add(new MaterializeItem(IsTexture: false, part.PartToken, part.PartToken, Recipe: part.Recipe));

        var textures = new List<MaterializeItem>();
        var byIdentity = new Dictionary<(string Bundle, string Name), int>(TextureIdentity.Instance);
        foreach (var part in subject.Children.Where(c => c.Kind == WorkbenchNodeKind.Part))
            foreach (var mat in part.Children)
                foreach (var map in mat.Maps)
                {
                    // A donor-derived row has no bundle to capture from, and its PNG is already in the mod;
                    // including it would fail the batch on "couldn't read the bundle".
                    if (!map.HasBundle) continue;
                    var key = (map.BundleId, map.TextureName);
                    if (byIdentity.TryGetValue(key, out var idx))
                        textures[idx] = MergeOwners(textures[idx], map.OwnerMeshNames);
                    else
                    {
                        byIdentity[key] = textures.Count;
                        textures.Add(new MaterializeItem(IsTexture: true, map.TextureName, map.TextureName,
                            map.BundleId, map.OwnerMeshNames));
                    }
                }
        items.AddRange(textures);
        return items;
    }

    /// <summary>Union a repeated exact-pair's owner mesh names into an existing item. Ordinal, matching the
    /// target's Users comparison.</summary>
    private static MaterializeItem MergeOwners(MaterializeItem existing, IReadOnlyList<string> more)
    {
        if (more.Count == 0) return existing;
        var union = new List<string>(existing.OwnerMeshNames ?? Array.Empty<string>());
        foreach (var o in more)
            if (!union.Contains(o, StringComparer.Ordinal)) union.Add(o);
        return existing with { OwnerMeshNames = union };
    }

    /// <summary>The (bundle, name) identity, mirroring <see cref="Materializer.TextureTarget"/>: bundle
    /// case-insensitive, asset name ordinal.</summary>
    private sealed class TextureIdentity : IEqualityComparer<(string Bundle, string Name)>
    {
        public static readonly TextureIdentity Instance = new();
        public bool Equals((string Bundle, string Name) a, (string Bundle, string Name) b) =>
            string.Equals(a.Bundle, b.Bundle, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Name, b.Name, StringComparison.Ordinal);
        public int GetHashCode((string Bundle, string Name) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Bundle),
                             StringComparer.Ordinal.GetHashCode(k.Name));
    }

    // ---- edited / materialized state + notify hooks ---------------------------------------------

    /// <summary>Recompute every node's ✎ and materialized flags with the part→subject rollup. Edited follows
    /// <see cref="ModProject.IsEdited"/>'s byte comparison; an imported target with no original is edited by
    /// definition.</summary>
    public void RefreshNodeStates()
    {
        var proj = _project();
        foreach (var root in Nodes) RefreshNode(root, proj);
    }

    private bool _blenderFound = true;

    /// <summary>Whether the shell located a Blender executable. Held here as well as on the nodes so a tree
    /// built AFTER detection (or after a Settings change) starts on the current answer.</summary>
    public bool BlenderFound
    {
        get => _blenderFound;
        set
        {
            if (_blenderFound == value) return;
            _blenderFound = value;
            foreach (var root in Nodes) PushBlenderFound(root, value);
        }
    }

    private static void PushBlenderFound(WorkbenchNodeVm node, bool found)
    {
        node.BlenderFound = found;
        foreach (var c in node.Children) PushBlenderFound(c, found);
    }

    /// <summary>The parts with a live Blender session opened from their row. Held here as well as on the
    /// nodes so a tree built while one is open starts on the current answer — the same discipline as
    /// <see cref="BlenderFound"/>.</summary>
    private readonly HashSet<string> _partSessions = new(StringComparer.Ordinal);

    private static string PartSessionKey(WorkbenchSubjectRef s, string partToken) =>
        $"{s.Character}/{s.Stem}/{partToken}".ToLowerInvariant();

    /// <summary>A Blender session opened from a PART's row started or ended. While one lives, that part's
    /// two opens refuse — two sessions on one part send back to the same file, so the last Send would take
    /// it silently. Every other part is untouched: a session belongs to the row it was opened from.</summary>
    public void SetPartSession(WorkbenchSubjectRef subject, string partToken, bool alive)
    {
        var key = PartSessionKey(subject, partToken);
        if (alive ? !_partSessions.Add(key) : !_partSessions.Remove(key)) return;
        var parts = new List<WorkbenchNodeVm>();
        foreach (var root in Nodes) CollectParts(root, parts);
        foreach (var p in parts)
            if (p.Subject is { } s && PartSessionKey(s, p.PartToken) == key)
                p.IsOpenInBlender = alive;
    }

    private void PushPartSessions(WorkbenchNodeVm node)
    {
        if (node.Kind == WorkbenchNodeKind.Part && node.Subject is { } s)
            node.IsOpenInBlender = _partSessions.Contains(PartSessionKey(s, node.PartToken));
        foreach (var c in node.Children) PushPartSessions(c);
    }

    private bool RefreshNode(WorkbenchNodeVm node, ModProject proj)
    {
        switch (node.Kind)
        {
            case WorkbenchNodeKind.Subject:
            {
                bool anyEdited = false;
                foreach (var c in node.Children) anyEdited |= RefreshNode(c, proj);
                node.IsEdited = anyEdited;
                node.IsMaterialized = node.Children.Any(c => c.Kind == WorkbenchNodeKind.Part && c.IsMaterialized);
                return anyEdited;
            }
            case WorkbenchNodeKind.Part:
            {
                bool mat = node.Subject is { } s &&
                    Materializer.IsPartMaterialized(proj, s.Character, s.Stem, s.MeshPrefix, node.PartToken);
                node.IsMaterialized = mat;
                // the mesh's own edited flag drives Part Revert; ✎ is the rollup with textures
                bool meshEdited = mat && node.Subject is { } s2 && PartMeshEdited(proj, s2, node.PartToken);
                node.MeshEdited = meshEdited;
                node.IsHiddenInMod = node.Subject is { } s3 && node.Recipe is { } r3
                    && proj.IsHidden(s3.Character, s3.Stem, r3.SlotName);
                ReconcileDonorMaterials(node, proj);
                node.AuthoredBaseMaps = AuthoredBaseMapOverlay(proj, node);
                node.EditedBaseMaps = EditedBaseMapOverlay(proj, node);
                bool anyMat = false;
                foreach (var c in node.Children) anyMat |= RefreshNode(c, proj);
                node.IsEdited = meshEdited || anyMat;
                return node.IsEdited;
            }
            case WorkbenchNodeKind.Material:
            {
                bool any = false;
                var donor = DonorRowFor(proj, node);
                foreach (var map in node.Maps)
                {
                    var t = MapTarget(proj, map);
                    map.IsMaterialized = t is not null;
                    map.IsEdited = t is not null && proj.IsEdited(t);
                    ApplyAuthoredPath(proj, map, donor);
                    any |= map.IsEdited || map.AuthoredPath is not null;
                }
                node.IsMaterialized = node.Maps.Any(m => m.IsMaterialized);
                node.IsEdited = any;
                return any;
            }
            default:
                return false;   // skeleton / animations / loose carry no edits
        }
    }

    /// <summary>One absolute PNG path per CURRENT-mesh submesh (null = vanilla), from the target's
    /// <c>DonorTextures</c>. Sized to the donor's own submesh count, since an edit may carry more submeshes
    /// than the game part had.</summary>
    private static IReadOnlyList<string?> AuthoredBaseMapOverlay(ModProject proj, WorkbenchNodeVm part)
    {
        var target = EditedPartMeshTarget(proj, part);
        var rows = target?.DonorTextures;
        if (rows is not { Count: > 0 }) return Array.Empty<string?>();
        int n = Math.Max(target!.DonorMaterials?.Count ?? 0,
                Math.Max(part.SubmeshBaseMaps.Count, rows.Max(r => r.Submesh) + 1));
        var overlay = new string?[n];
        bool any = false;
        foreach (var r in rows)
        {
            if (r.Albedo is null || r.Submesh < 0 || r.Submesh >= overlay.Length) continue;
            try { overlay[r.Submesh] = Path.GetFullPath(proj.Resolve(r.Albedo)); any = true; } catch { /* → vanilla */ }
        }
        return any ? overlay : Array.Empty<string?>();
    }

    /// <summary>One absolute workspace PNG path per RENDERER-slot submesh whose base-color texture is
    /// edited (null = untouched), aligned with <see cref="WorkbenchNodeVm.SubmeshBaseMaps"/>. Read off
    /// the project's own targets, so it covers every route that can rewrite a map file. Empty when
    /// nothing this part samples is edited.</summary>
    private static IReadOnlyList<string?> EditedBaseMapOverlay(ModProject proj, WorkbenchNodeVm part)
    {
        var maps = part.SubmeshBaseMaps;
        string?[]? overlay = null;
        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i] is not { } map || map.BundleId.Length == 0 || part.Subject is not { } s) continue;
            var t = Materializer.TextureTarget(proj, s.Character, s.Stem, map.BundleId, map.TextureName);
            if (t is null || !proj.IsEdited(t)) continue;
            string png;
            try { png = Path.GetFullPath(proj.Resolve(t.ReplaceFile)); } catch { continue; }
            // a recorded edit whose file has gone missing samples the game map, not a hole
            if (!File.Exists(png)) continue;
            (overlay ??= new string?[maps.Count])[i] = png;
        }
        return overlay ?? Array.Empty<string?>();
    }

    /// <summary>An edited part's material children mirror the CURRENT mesh, not the game renderer's
    /// slots: a replace carrying more submeshes must show every one. The fall-through is per SLOT — a
    /// submesh the game part already had keeps its game material node whole, the send-back's maps landing
    /// on its cards one slot at a time (<see cref="ApplyAuthoredPath"/>); what those cards say is the
    /// build's rule (<see cref="BlankedSlots"/>). Only a submesh past the game's material count gets a
    /// donor-derived node. The game-derived children are stashed and restored on revert; the swap runs
    /// only when the donor shape changes.</summary>
    private void ReconcileDonorMaterials(WorkbenchNodeVm part, ModProject proj)
    {
        var target = EditedPartMeshTarget(proj, part);
        var donor = target?.DonorMaterials;
        var rows = target?.DonorTextures;
        string? key = donor is { Count: > 0 } ? DonorShapeKey(donor, rows) : null;
        if (string.Equals(key, part.DonorShapeKey, StringComparison.Ordinal)) return;
        if (key is null)
        {
            // back to the game surface — revert, or the edit cleared
            if (part.StashedGameChildren is { } game) SwapMaterialChildren(part, game, stash: null);
            part.StashedGameChildren = null;
            part.DonorShapeKey = null;
            return;
        }
        var stashed = part.StashedGameChildren
            ??= part.Children.Where(c => c.Kind == WorkbenchNodeKind.Material).ToList();
        var fresh = new List<WorkbenchNodeVm>(donor!.Count);
        for (int i = 0; i < donor.Count; i++)
            fresh.Add(i < stashed.Count
                ? stashed[i]
                : BuildDonorMaterialNode(donor[i], i, part, rows?.FirstOrDefault(r => r.Submesh == i)));
        SwapMaterialChildren(part, fresh, stash: stashed);
        part.DonorShapeKey = key;
    }

    /// <summary>The shape the material children are built from: the donor material names AND what each
    /// submesh's three slots ASK for. The ask, not the file name — a slot the build blanks names no file
    /// and still earns a row, and naming a file IS <see cref="SlotOrigin.Authored"/>. Each name is
    /// length-prefixed, so a name carrying a separator cannot spell a different list's key.</summary>
    private static string DonorShapeKey(IReadOnlyList<string> donor, IReadOnlyList<SubmeshTextures>? rows)
    {
        var key = new StringBuilder();
        for (int i = 0; i < donor.Count; i++)
        {
            var row = rows?.FirstOrDefault(r => r.Submesh == i);
            key.Append(donor[i].Length).Append(':').Append(donor[i])
                .Append(Ask(row?.AlbedoAsk)).Append(Ask(row?.NormalAsk)).Append(Ask(row?.RmoAsk));
        }
        return key.ToString();

        static char Ask(SlotOrigin? ask) => ask is null ? '-' : (char)('0' + (int)ask);
    }

    /// <summary>Put <paramref name="replacement"/> in place of the part's material children, index by index:
    /// a position whose node is already the wanted one is left alone, so a kept node never leaves the
    /// collection and the tree's selection survives the swap. A node the tree carries over — into the
    /// replacement, or into <paramref name="stash"/> for a later restore — holds its thumbs; only ones
    /// dropped for good are released.</summary>
    private static void SwapMaterialChildren(WorkbenchNodeVm part, IReadOnlyList<WorkbenchNodeVm> replacement,
        IReadOnlyList<WorkbenchNodeVm>? stash)
    {
        var slots = new List<int>();
        for (int i = 0; i < part.Children.Count; i++)
            if (part.Children[i].Kind == WorkbenchNodeKind.Material) slots.Add(i);

        void Retire(WorkbenchNodeVm gone)
        {
            if (!replacement.Contains(gone) && stash?.Contains(gone) != true) ReleaseThumbsIn(gone);
        }

        int shared = Math.Min(slots.Count, replacement.Count);
        for (int i = 0; i < shared; i++)
        {
            var old = part.Children[slots[i]];
            if (ReferenceEquals(old, replacement[i])) continue;
            part.Children[slots[i]] = replacement[i];
            Retire(old);
        }
        // surplus goes tail-first, so the positions still to visit stay where they were recorded
        for (int i = slots.Count - 1; i >= shared; i--)
        {
            var old = part.Children[slots[i]];
            part.Children.RemoveAt(slots[i]);
            Retire(old);
        }
        if (shared == replacement.Count) return;
        // reached only when nothing was removed, so the last kept position is still where it was recorded
        int at = slots.Count > 0 ? slots[shared - 1] + 1 : part.Children.Count;
        for (int i = shared; i < replacement.Count; i++) part.Children.Insert(at++, replacement[i]);
    }

    /// <summary>One donor-derived material row for a submesh the game part has no material behind, with a
    /// row per slot the BUILD decides something about: the maps the returned mesh had, and the ones it
    /// blanks (<see cref="BlankedSlots"/>). A blanked slot names no file and has no stock card standing
    /// here, so without its own row the change-list chip would be the only visible trace. A material the
    /// build decides nothing about says what its submesh does instead.</summary>
    private static WorkbenchNodeVm BuildDonorMaterialNode(string materialName, int index, WorkbenchNodeVm part,
        SubmeshTextures? row)
    {
        string title = materialName.Length > 0 ? materialName : $"submesh {index}";
        var flat = row is null ? default : BlankedSlots.Of(row, EditVerbs.Replace);
        var slots = new (string Slot, string? File, bool Flat)[]
        {
            ("_BaseMap", row?.Albedo, flat.Albedo),
            ("_BumpMap", row?.Normal, flat.Normal),
            ("_RMOTex", row?.Rmo, flat.Rmo),
        };
        int mapCount = slots.Count(s => s.File is not null);
        int flatCount = slots.Count(s => s.File is null && s.Flat);
        var node = new WorkbenchNodeVm
        {
            Kind = WorkbenchNodeKind.Material,
            Title = title,
            Subtitle = DonorMaterialSubtitle(mapCount, flatCount),
            InspectorHeader = title,
            InspectorDetail = $"used by {part.PartToken} · {WorkbenchMapVm.AuthoredOrigin}",
            InspectorNote = mapCount + flatCount == 0
                ? "No maps on this material. Its submesh keeps the part's stock maps."
                : null,
            Subject = part.Subject,
            PartToken = part.PartToken,
            MaterialIndex = index,
        };
        // A blanked slot has no image to name, so its row carries an empty texture name and the card's own
        // blanked line is what speaks for it. The slot name is what RefreshNode reads the state back off.
        //
        // The part and the submesh ride the row so a drop can author this slot: the submesh belongs to the
        // replacement and nothing else does, so its own index IS the landing set, and a card with no file on
        // it yet has no other way to name what it stands for.
        foreach (var (slot, file, isFlat) in slots)
            if (file is not null || isFlat)
                node.Maps.Add(WorkbenchNodeVm.MapRow(
                    new SubjectMap(slot, file is null ? "" : Path.GetFileName(file), ""), part.Subject,
                    partToken: part.PartToken, boundSubmeshes: new[] { index }));
        return node;
    }

    /// <summary>What a donor-derived material's row says it holds. The two states are separate news: a map
    /// ships an image and a blanked slot ships the build's flat one.</summary>
    private static string DonorMaterialSubtitle(int mapCount, int flatCount) =>
        mapCount > 0 && flatCount > 0 ? $"{Count(mapCount, "map")} · {flatCount} blanked"
        : mapCount > 0 ? Count(mapCount, "map")
        : flatCount > 0 ? $"{flatCount} blanked"
        : "inherits stock maps";

    /// <summary>The send-back texture row for this material's submesh, or null. The material's renderer-slot
    /// index IS the donor submesh key — the same ordering contract the build relies on.</summary>
    private static SubmeshTextures? DonorRowFor(ModProject proj, WorkbenchNodeVm materialNode)
    {
        if (materialNode.MaterialIndex < 0) return null;
        var rows = EditedPartMeshTarget(proj, materialNode)?.DonorTextures;
        return rows?.FirstOrDefault(r => r.Submesh == materialNode.MaterialIndex);
    }

    /// <summary>The node's part mesh target ONLY while that mesh is edited — the one gate every donor-record
    /// read goes through. A send-back's records describe the EDITED mesh, so read off an unedited target they
    /// would dress game rows in donor state.</summary>
    private static ProjectTarget? EditedPartMeshTarget(ModProject proj, WorkbenchNodeVm node)
    {
        var target = PartMeshTarget(proj, node);
        return target is not null && (target.Edited || proj.IsEdited(target)) ? target : null;
    }

    /// <summary>Reflect the authored donor texture (or its removal) on a map row, superseding the card thumb
    /// when it changes. The blanked state rides the same read and is settled BEFORE the unchanged-path exit:
    /// a blanked slot names no file, so its whole state change would fall on the wrong side of it.</summary>
    private void ApplyAuthoredPath(ModProject proj, WorkbenchMapVm map, SubmeshTextures? donor)
    {
        // A donor row is a mesh replace, so it is read under that verb's rule: the explicit blank on any of
        // the three slots, plus the flat normal/RMO a submesh drawing on donor UVs takes when it asked for
        // anything at all.
        var flat = donor is null ? default : BlankedSlots.Of(donor, EditVerbs.Replace);
        map.IsBlanked =
            MaterialResolver.IsBaseColor(map.SlotName) ? flat.Albedo
            : MaterialResolver.IsNormal(map.SlotName) ? flat.Normal
            : MaterialResolver.IsRmo(map.SlotName) && flat.Rmo;
        string? rel = donor is null ? null
            : MaterialResolver.IsBaseColor(map.SlotName) ? donor.Albedo
            : MaterialResolver.IsNormal(map.SlotName) ? donor.Normal
            : MaterialResolver.IsRmo(map.SlotName) ? donor.Rmo
            : null;
        string? authored = null;
        if (rel is not null)
            try { authored = Path.GetFullPath(proj.Resolve(rel)); } catch { authored = null; }
        if (string.Equals(map.AuthoredPath, authored, StringComparison.OrdinalIgnoreCase)) return;
        map.AuthoredPath = authored;
        var token = _cts?.Token ?? CancellationToken.None;
        if (authored is not null)
        {
            ReThumbFromWorkspace(map, authored, map.BeginThumbRequest(), token);
            // the size belongs to the FILE, so a new file makes the shown dimensions stale
            map.Dimensions = "…";
            LoadAuthoredMapDims(map, authored, token);
        }
        else if (map.HasThumb)
        {
            // reverted: fall back to the edited workspace PNG if one exists, else vanilla
            if (map.IsEdited && WorkspacePngFor(proj, map) is { } ws && File.Exists(ws))
                ReThumbFromWorkspace(map, ws, map.BeginThumbRequest(), token);
            else if (map.HasBundle) RestoreVanillaThumb(map, map.BeginThumbRequest(), token);
            // a donor-derived row has no vanilla behind it — nothing replaces the file that went away
            else { map.MarkThumbFailed(); map.Dimensions = "unavailable"; }
        }
    }

    /// <summary>Whether the part's mesh target differs from its original.</summary>
    private static bool PartMeshEdited(ModProject proj, WorkbenchSubjectRef s, string token) =>
        Materializer.PartTargets(proj, s.Character, s.Stem, s.MeshPrefix, token).Any(proj.IsEdited);

    /// <summary>A mesh edit landed: refresh state and render the edited geometry, or restore the vanilla
    /// preview after a revert.</summary>
    public void NotifyMeshEdited(string fullPath) => NotifyMeshesEdited(new[] { fullPath });

    /// <summary>Batch form for a combined send-back: only the named parts re-render, sharing one batch.</summary>
    public void NotifyMeshesEdited(IReadOnlyList<string> fullPaths)
    {
        RefreshNodeStates();
        var project = _project();
        var token = _cts?.Token ?? CancellationToken.None;
        var affected = new HashSet<string>(fullPaths.Select(SafeFullPath), StringComparer.OrdinalIgnoreCase);
        var parts = new List<WorkbenchNodeVm>();
        foreach (var root in Nodes) CollectParts(root, parts);
        var edited = new List<EditedMeshPreviewRequest>();
        var vanilla = new List<MeshPreviewRequest>();
        foreach (var part in parts)
        {
            var target = PartMeshTarget(project, part);
            if (target is null) continue;
            string workspace;
            try { workspace = SafeFullPath(project.Resolve(target.ReplaceFile)); }
            catch { continue; }
            if (!affected.Contains(workspace)) continue;

            if (project.IsEdited(target))
                edited.Add(new EditedMeshPreviewRequest(part, part.BeginMeshPreviewRequest(edited: true),
                    ResolveTargetPath(project, target.ReplaceFile), target.OriginalVerts));
            else if (part.IsPreviewingEditedMesh)
                vanilla.Add(new MeshPreviewRequest(part, part.BeginMeshPreviewRequest()));
        }
        if (edited.Count > 0)
            Task.Run(() => EditedMeshPreviewBatch(edited, token), token);
        if (vanilla.Count > 0)
            Task.Run(() => MeshPreviewBatch(vanilla, _catalogVersion, token), token);
    }

    /// <summary>A texture file changed: refresh ✎, re-thumb the affected card(s), and re-render the part
    /// previews that sample it. An edited map decodes the workspace PNG directly (NO disk cache); a reverted
    /// one falls back to the cached vanilla thumb. The size line is re-read from the file either way — it
    /// belongs to the file, not to the game texture behind it.</summary>
    public void NotifyTextureFileChanged(string fullPath)
    {
        RefreshNodeStates();
        var proj = _project();
        var full = SafeFullPath(fullPath);
        var token = _cts?.Token ?? CancellationToken.None;
        var maps = new List<WorkbenchMapVm>();
        foreach (var root in Nodes) CollectMaps(root, maps);
        foreach (var m in maps)
        {
            // A donor-authored row is keyed by its FILE, not by a Texture2D target. A row that ALSO has a
            // bundle keeps its target, so a save to that file falls through to the branch below.
            if (m.AuthoredPath is { } authored
                && string.Equals(SafeFullPath(authored), full, StringComparison.OrdinalIgnoreCase))
            {
                ReThumbFromWorkspace(m, authored, m.BeginThumbRequest(), token);
                LoadAuthoredMapDims(m, authored, token);
                continue;
            }
            var t = MapTarget(proj, m);
            if (t is null) continue;
            string ws;
            try { ws = SafeFullPath(proj.Resolve(t.ReplaceFile)); } catch { continue; }
            if (!string.Equals(ws, full, StringComparison.OrdinalIgnoreCase)) continue;
            // force a fresh request so it supersedes any in-flight vanilla load
            if (proj.IsEdited(t)) ReThumbFromWorkspace(m, ws, m.BeginThumbRequest(), token);
            else RestoreVanillaThumb(m, m.BeginThumbRequest(), token);
            // The size belongs to the FILE the card shows, and that file was just overwritten — a drop of a
            // differently-sized image left the stock dimensions standing here. Reset, then re-read. A row
            // that shows an AUTHORED image is not showing this file, so its size line is not this file's.
            if (m.AuthoredPath is not { } shown || !File.Exists(shown))
            {
                m.Dimensions = "…";
                LoadWorkspaceMapDims(m, ws, token);
            }
        }

        InvalidatePartPreviewsSampling(proj, full);
    }

    /// <summary>Every part whose preview samples the file at <paramref name="fullPath"/> drops the render
    /// it is showing, so the changed map reaches the part and not only its map card. A part re-renders at
    /// once only while it IS the selection (the image-editor save route); a card drop's selection is the
    /// MATERIAL node, so that render is remade the next time the part is selected.</summary>
    private void InvalidatePartPreviewsSampling(ModProject proj, string fullPath)
    {
        var parts = new List<WorkbenchNodeVm>();
        foreach (var root in Nodes) CollectParts(root, parts);
        foreach (var part in parts)
        {
            if (!PartSamplesFile(proj, part, fullPath)) continue;
            part.InvalidateMeshPreview();
            if (ReferenceEquals(part, SelectedNode)) LoadMeshPreview(part);
        }
    }

    /// <summary>Does this part's preview sample the file at <paramref name="fullPath"/>? Base-color maps
    /// only — those are the maps the renderer samples. Both files a card can write count: an authored donor
    /// map, and a game texture's workspace PNG. Resolved from the project rather than from the edited
    /// overlay, so a REVERT (which empties that overlay) still invalidates the render it produced.</summary>
    private static bool PartSamplesFile(ModProject proj, WorkbenchNodeVm part, string fullPath)
    {
        foreach (var authored in part.AuthoredBaseMaps)
            if (authored is not null
                && string.Equals(SafeFullPath(authored), fullPath, StringComparison.OrdinalIgnoreCase))
                return true;
        foreach (var map in part.SubmeshBaseMaps)
        {
            if (map is not { } m || m.BundleId.Length == 0 || part.Subject is not { } s) continue;
            var t = Materializer.TextureTarget(proj, s.Character, s.Stem, m.BundleId, m.TextureName);
            if (t is null) continue;
            string ws;
            try { ws = SafeFullPath(proj.Resolve(t.ReplaceFile)); } catch { continue; }
            if (string.Equals(ws, fullPath, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string SafeFullPath(string p) { try { return Path.GetFullPath(p); } catch { return p; } }

    /// <summary>Decode an edited map's workspace PNG off the UI thread, guarded by
    /// <paramref name="generation"/>. NO disk cache — the vanilla thumb cache must never be overwritten.</summary>
    private void ReThumbFromWorkspace(WorkbenchMapVm m, string workspacePng, int generation, CancellationToken token)
    {
        Task.Run(() => RunGated(token, () =>
        {
            Bitmap? bmp = null;
            try
            {
                using var fs = File.OpenRead(workspacePng);
                bmp = DecodeMapThumb(fs, m.IsRmo);
            }
            catch { bmp = null; }
            Dispatcher.UIThread.Post(() =>
            {
                if (token.IsCancellationRequested || !m.IsCurrentThumbRequest(generation)) { bmp?.Dispose(); return; }
                if (bmp is null) m.MarkThumbFailed();
                else m.SetThumb(bmp);
            });
        }), token);
    }

    /// <summary>Restore a reverted map's card to the vanilla thumb, guarded by
    /// <paramref name="generation"/>.</summary>
    private void RestoreVanillaThumb(WorkbenchMapVm m, int generation, CancellationToken token)
    {
        var version = _catalogVersion;
        Task.Run(() => RunGated(token, () =>
        {
            string? path = _thumbs.TryGetCachedPath(m.BundleId, m.TextureName, version)
                           ?? _thumbs.EnsureThumb(_tryDeobfuscate, m.BundleId, m.TextureName, version);
            AssignThumb(m, path, generation, token);
        }), token);
    }

    private void LoadMeshPreview(WorkbenchNodeVm part)
    {
        var project = _project();
        var token = _cts?.Token ?? CancellationToken.None;
        var target = PartMeshTarget(project, part);
        if (target is not null && project.IsEdited(target))
        {
            var request = new EditedMeshPreviewRequest(part, part.BeginMeshPreviewRequest(edited: true),
                ResolveTargetPath(project, target.ReplaceFile), target.OriginalVerts);
            Task.Run(() => EditedMeshPreviewBatch(new[] { request }, token), token);
        }
        else RestoreVanillaMeshPreview(part, token);
    }

    private void RestoreVanillaMeshPreview(WorkbenchNodeVm part, CancellationToken token)
    {
        int request = part.BeginMeshPreviewRequest();
        var version = _catalogVersion;
        Task.Run(() => MeshPreviewBatch(new[] { new MeshPreviewRequest(part, request) }, version, token), token);
    }

    // ---- drag-drop ------------------------------------------------------------------------------

    /// <summary>The ONLY drop that applies anything: a single <c>.png</c> landing directly on a map card,
    /// behind a confirm, replacing THAT card's texture regardless of filename. A drop that arrives without
    /// qualifying says so on the status line and does nothing. An off-card drag never arrives — the view
    /// hands the platform a no-drop effect — but the no-card branch still holds, since this contract is
    /// the VM's own. Meshes are not droppable; they come back from Blender.</summary>
    public async Task HandleDropAsync(IReadOnlyList<string> paths, WorkbenchMapVm? card = null)
    {
        if (_shell is null) return;
        if (card is null || paths.Count != 1 || !IsPng(paths[0]))
        { Status = "Only a .png dropped on a texture card applies."; return; }
        await HandleCardDropAsync(card, paths[0]);
    }

    private static bool IsPng(string path) => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>Apply a PNG dropped ON a map card to that card's texture, filename ignored, after a confirm.
    /// Reuses <see cref="DropPngAsync"/>, so the gate and sequential rules still hold.</summary>
    private async Task HandleCardDropAsync(WorkbenchMapVm map, string path)
    {
        if (map.Subject is null) return;
        if (map.IsBusy || VerbsBusy) { ReportDropBusy(path); return; }
        if (!CanDropPng(map, path)) return;   // refuse BEFORE the confirm — no pointless dialog
        var donor = DonorDropFor(map, out var refusal, out int authoredLanding);
        if (refusal is not null) { Status = $"{Path.GetFileName(path)} {refusal}"; return; }
        // The extension got the file this far; the HEADER decides. A JPEG saved under a .png name, or a file
        // that won't open at all, is refused here rather than copied over the modder's map.
        var incoming = await Task.Run(() => PngInfo.TryPngSize(path));
        if (incoming is not { } size)
        { Status = $"{Path.GetFileName(path)} isn't a readable .png."; return; }
        // Both replacement routes are irreversible at the map grain; the game-texture one is not. The confirm
        // is told which, and everything each body needs, rather than inferring it from the card.
        if (!await _shell!.ConfirmApplyDroppedPngAsync(new DroppedPngConfirm(
                Path.GetFileName(path), map.MapLabel, map.TextureName, map.PartToken,
                donor, map.AuthoredPath is not null, authoredLanding,
                // the parts other than this card's own that draw the same game texture
                donor is null && map.AuthoredPath is null ? Math.Max(0, map.OwnerMeshNames.Count - 1) : 0,
                await SizeMismatchNoteAsync(map, size))))
        { Status = "Nothing applied."; return; }
        await DropPngAsync(map, path, donor);
    }

    /// <summary>The donor-map authoring this card's drop is, or null when the drop is an ordinary map
    /// edit. A card on a REPLACED part shows a stock map the build no longer ships, so an image dropped
    /// there is meant for the replacement, landing as the same per-submesh authored record a Blender
    /// session's map would. A card the mesh edit already authored keeps its own route: that file IS the
    /// record. A slot kind outside base colour/normal/RMO is NOT this route's — the replacement leaves
    /// the game texture drawing there — so this returns null. <paramref name="refusal"/>: the status-line
    /// tail for a drop this route owns and still can't take. <paramref name="authoredLanding"/>: landing
    /// submeshes that ALREADY name a file on this slot — the drop overwrites every one, including files
    /// authored from a card it didn't land on, so the confirm says how many.</summary>
    private DonorMapDrop? DonorDropFor(WorkbenchMapVm map, out string? refusal, out int authoredLanding)
    {
        refusal = null;
        authoredLanding = 0;
        if (map.AuthoredPath is not null || map.PartToken.Length == 0 || map.Subject is not { } s) return null;
        var proj = _project();
        if (!PartMeshEdited(proj, s, map.PartToken)) return null;
        if (DonorSlotOf(map.SlotName) is not { } slot) return null;
        // The build refuses a row past the donor's own submesh count, so the drop refuses first. The donor's
        // material list is what the replacement came back carrying; without one there is no shape to check
        // against and nothing that could be authored in range.
        var target = Materializer.PartMeshTarget(proj, s.Character, s.Stem, s.MeshPrefix, map.PartToken);
        int donorSubmeshes = target?.DonorMaterials?.Count ?? 0;
        var landing = map.BoundSubmeshes.Where(i => i >= 0 && i < donorSubmeshes).ToList();
        if (landing.Count == 0)
        {
            refusal = DonorDropRefusal.PastTheReplacement(map.PartToken);
            return null;
        }
        authoredLanding = landing.Count(i => AuthoredOnSlot(target, i, slot));
        return new DonorMapDrop(map.PartToken, slot, landing);
    }

    /// <summary>Whether one donor submesh already names a file on one slot. Read off the part's own record
    /// rather than the card, since a landing submesh's map may have been authored from a DIFFERENT card —
    /// one stock map dressing several material slots gives each of them a card of its own.</summary>
    private static bool AuthoredOnSlot(ProjectTarget? target, int submesh, DonorMapSlot slot)
    {
        if (target?.DonorTextures?.FirstOrDefault(r => r.Submesh == submesh) is not { } row) return false;
        return slot switch
        {
            DonorMapSlot.BaseColor => row.Albedo is not null,
            DonorMapSlot.Normal => row.Normal is not null,
            _ => row.Rmo is not null,
        };
    }

    /// <summary>The confirm's size line when the dropped image isn't the size of the map it replaces, else
    /// null. Informational, never a refusal: arbitrary sizes are mechanically fine — UVs are normalized and
    /// the shipped override is keyed by hash, not by dimensions — but a modder who dropped a 512 over a 4K
    /// map usually wants to know before it lands. A size we can't read says nothing rather than guessing.</summary>
    private async Task<string?> SizeMismatchNoteAsync(WorkbenchMapVm map, (int Width, int Height) incoming)
    {
        if (await CardMapSizeAsync(map) is not { } current) return null;
        if (current.Width == incoming.Width && current.Height == incoming.Height) return null;
        return $"{incoming.Width}×{incoming.Height}, the map is {current.Width}×{current.Height}. "
             + "It still applies; UVs stretch it to fit.";
    }

    /// <summary>The size of the map a card currently stands for, down the SAME chain the card's size line
    /// reads (<see cref="LoadMapMeta"/>): authored row's own file, then edited map's workspace PNG, then
    /// the game texture's metadata. Null when unreadable. The meta cache is READ here and never written —
    /// it is UI-thread-only state and this runs either side of an await.</summary>
    private async Task<(int Width, int Height)?> CardMapSizeAsync(WorkbenchMapVm map)
    {
        // The card shows the authored image, so its size is the one to report — but only while that file is
        // there; a bundle-backed row falls through to the branches below.
        if (map.AuthoredPath is { } authored && File.Exists(authored))
            return await Task.Run(() => PngInfo.TrySize(authored));
        // An EDITED map's size is its workspace file's, not the game texture's: an earlier drop can have
        // replaced it at any size, and the meta cache below still holds the stock one keyed by game identity.
        if (map.IsEdited && WorkspacePngFor(_project(), map) is { } edited && File.Exists(edited))
            return await Task.Run(() => PngInfo.TrySize(edited));
        if (!map.HasBundle) return null;
        if (_metaCache.TryGetValue(_catalogVersion + "|" + map.BundleId + "|" + map.TextureName, out var cached))
            return ParseDims(cached);
        var bundleId = map.BundleId;
        var textureName = map.TextureName;
        return await Task.Run<(int Width, int Height)?>(() =>
        {
            try
            {
                var dec = _tryDeobfuscate(bundleId);
                if (dec is null) return null;
                return TextureExport.Probe(dec, textureName) is { } probe
                    ? (probe.Width, probe.Height) : null;
            }
            catch { return null; }
        });
    }

    /// <summary>A cached "W×H" size line back into numbers; null for "unavailable" or any other shape. The
    /// cache stores only successful reads, so this is a parse, not a validation.</summary>
    private static (int Width, int Height)? ParseDims(string dims)
    {
        int x = dims.IndexOf('×');
        if (x <= 0) return null;
        return int.TryParse(dims.AsSpan(0, x), out int w) && int.TryParse(dims.AsSpan(x + 1), out int h)
            ? (w, h) : null;
    }

    /// <summary>Whether this card has anything a dropped PNG could become — the rule the DRAG-OVER cursor
    /// reads. A game texture to edit, a map the replacement already carries to overwrite, or a replaced
    /// part's submesh to author for: the third is what gives a submesh the edit ADDED its affordance, since
    /// such a card carries neither a bundle nor, until something lands there, a file. Says nothing on the
    /// status line: a hover is not a refusal yet.</summary>
    internal bool CanAcceptDrop(WorkbenchMapVm map) =>
        map.HasBundle || map.AuthoredPath is not null || DonorDropFor(map, out _, out _) is not null;

    /// <summary>Whether a dropped PNG can apply here, reporting why not when it can't. A row with neither an
    /// authored file nor a bundle has nothing to replace, and would hunt on an empty bundle id.</summary>
    private bool CanDropPng(WorkbenchMapVm map, string path)
    {
        if (CanAcceptDrop(map)) return true;
        Status = $"{Path.GetFileName(path)} can't apply here. {map.TextureName} isn't on disk.";
        return false;
    }

    /// <summary>Ingest a dropped PNG into the file this card actually shows: the authored PNG when the row
    /// has one, the replacement's own map when the part is replaced (<paramref name="donor"/>), else the game
    /// texture's workspace copy.</summary>
    private async Task DropPngAsync(WorkbenchMapVm map, string path, DonorMapDrop? donor = null)
    {
        if (!CanDropPng(map, path)) return;
        if (map.IsBusy || VerbsBusy) { ReportDropBusy(path); return; }
        _verbInFlight = true; map.IsBusy = true;
        try
        {
            if (map.AuthoredPath is { } authored)
                await _shell!.ApplyDroppedPngToAuthoredAsync(authored, map.PartToken, map.MapLabel, path, StatusProgress);
            else if (donor is not null)
                await _shell!.ApplyDroppedPngToDonorMapAsync(map.Subject!, donor, map.MapLabel, path, StatusProgress);
            else
                await _shell!.ApplyDroppedPngAsync(map.Subject!, map.TextureName, map.BundleId, map.OwnerMeshNames, path, StatusProgress);
        }
        finally { map.IsBusy = false; EndVerb(); RefreshNodeStates(); }
    }

    /// <summary>A drop arriving while another verb holds the gate is skipped, not queued — say so, since the
    /// drop path has no disabled button to carry the cue.</summary>
    private void ReportDropBusy(string path) =>
        Status = $"Busy with the current step. Drop {Path.GetFileName(path)} again when it finishes.";
}
