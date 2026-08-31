using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Remold.App.Textures;
using Remold.App.ViewModels.EditPage;
using Remold.App.Views;
using Remold.Core;
using Remold.Core.Blender;
using Remold.Core.Bundles;
using Remold.Core.Export;
using Remold.Core.Materials;
using Remold.Core.Mesh;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Textures;
using Remold.Core.Workbench;
using SessionRampChoice = Remold.App.ViewModels.EditPage.RampChoice;
using SessionRampPickLoad = Remold.App.ViewModels.EditPage.RampPickLoad;
using SessionRampPickRowVm = Remold.App.ViewModels.EditPage.RampPickRowVm;
using SessionRampPickerVm = Remold.App.ViewModels.EditPage.RampPickerVm;

namespace Remold.App.ViewModels;

/// <summary>
/// The window's half of the ② Edit page: everything the page's verbs need that is not authored intent —
/// reading the install, rendering geometry, decoding a picture, running Blender or an image editor, asking a
/// question, moving to another step.
///
/// <para>The split is the 0.4 model's own: every authored write below goes through the one
/// <see cref="AuthoredEditSession"/> transaction addressed by the supplied edit and slot. Reads of the game
/// and launches of external tools do not create a second mutable project model.</para>
///
/// <para>Reads of the install go through the session's memoized <see cref="SubjectModel"/>s and the
/// app-wide thumbnail cache rather than through a second set of either.</para>
/// </summary>
public partial class MainWindowViewModel : EditPage.IEditPageShell
{
    // ---- the authored session the page edits -------------------------------------------------------

    /// <summary>The ② Edit page itself. One instance for the app's life: the window is its shell, and a page
    /// re-made per project would take the shell's identity with it. What changes per project is the session
    /// it is <see cref="EditPage.EditPageVm.Load"/>ed with.</summary>
    public EditPage.EditPageVm EditPage { get; }

    /// <summary>Point both pages at the open project's intent. Called wherever the document itself is
    /// replaced, since the pages hold the session rather than the document and cannot see that happen.
    ///
    /// <para>Every open document has a session — a mod that could not be converted did not open at all — so
    /// there is no second state to draw: what the pages show is what the project holds.</para></summary>
    private void LoadEditPage()
    {
        EditPage.Load(_projectDocument.Session);
        BuildPage.Load(_projectDocument.Session);
    }

    /// <summary>Read every subject of the open mod into the shared model memo, off the UI thread, redrawing
    /// the page as each lands.
    ///
    /// <para>The page PEEKS that memo and never builds one: a row's part token, a slot's game texture name
    /// and a subject's bone tree are all reads of a model, and building one costs seconds of bundle
    /// deobfuscation with the window frozen behind it. A mod opened onto a cold memo would therefore show
    /// renderer slot names and no map names until something else happened to read the install — so the open
    /// warms it, which is the one moment the cost is expected.</para>
    ///
    /// <para>Nothing here is required for the page to work, so nothing here is reported: a subject the
    /// install cannot answer for leaves its rows on the names the project holds.</para></summary>
    private async Task WarmSubjectModelsAsync()
    {
        if (_subjectModelWarm is null && (_vfs is null || GameDir is not { Length: > 0 })) return;
        string gameDir = GameDir;
        var document = _projectDocument;
        var project = document.Authored;
        if (project is null) return;
        var selection = project.WorkspaceIndex?.Selection
            ?? project.EditDefinitions.Select(edit => new SelectionEntry
                { Character = edit.Target.Subject, Outfit = edit.Target.Outfit })
                .DistinctBy(entry => (entry.Character.ToUpperInvariant(), entry.Outfit.ToUpperInvariant()))
                .ToList();
        bool settledAny = false;
        foreach (var entry in selection.ToList())
        {
            // Re-asked per subject: each read is seconds long, and a mod closed or swapped inside one must
            // not have the next subject's model filed under it.
            if (!ReferenceEquals(document, _projectDocument)) return;
            if (_subjectModels.TryGet(entry.Character, entry.Outfit) is not null) continue;
            SubjectModel? model;
            try
            {
                if (_subjectModelWarm is not null)
                    model = await Task.Run(() => _subjectModelWarm(entry.Character, entry.Outfit));
                else if (PickOutfit(entry.Character, entry.Outfit) is { } outfit)
                    model = await Task.Run(() => CatalogIndex.LoadCached(gameDir) is { } catalog
                        ? SubjectModelBuilder.Build(catalog, TryDeobfuscateBundle, outfit, entry.Character)
                        : null);
                else model = null;
            }
            // A read that ended without a model is RECORDED and redrawn, not dropped. Nothing retries it
            // within this forward view, so a surface left waiting on it waits for the life of the app —
            // which is what "still being read" said on those subjects' cards until the read that never
            // comes was written down. The record describes the install, so a mod swapped mid-read does not
            // change it; only a re-read of the game does, and that clears it with the models.
            catch
            {
                NoteSubjectUnreadable(entry, replanBuild: false);
                settledAny = true;
                continue;
            }
            if (!ReferenceEquals(document, _projectDocument)) continue;
            if (model is null)
            {
                NoteSubjectUnreadable(entry, replanBuild: false);
                settledAny = true;
                continue;
            }
            _subjectModels.GetOrBuild(entry.Character, model.Stem, () => model);
            SubjectModelWarmCompleted(replanBuild: false);
            settledAny = true;
        }
        // The model pass owns the expensive prerequisite. Starting once after the snapshot has finished
        // avoids cancelling/restarting the prewarm for every subject that lands.
        if (ReferenceEquals(document, _projectDocument))
        {
            if (settledAny) _pageDispatch(() => _ = BuildPage.ReplanAsync());
            TryStartRiggedGlbPrewarm();
        }
    }

    /// <summary>This install cannot answer for one subject, and both panes are redrawn on that answer the
    /// same as on a model landing: it is the news that ends the wait, and a page that never hears it goes on
    /// saying the read is coming.</summary>
    private void NoteSubjectUnreadable(SelectionEntry entry, bool replanBuild = true)
    {
        _subjectModels.MarkUnreadable(entry.Character, entry.Outfit);
        SubjectModelWarmCompleted(replanBuild);
    }

    /// <summary>One subject model became readable. Both panes name parts from the same memo.</summary>
    internal void SubjectModelWarmCompleted(bool replanBuild = true) => _pageDispatch(() =>
    {
        EditPage.Rebuild();
        if (replanBuild) _ = BuildPage.ReplanAsync();
    });

    /// <summary>The open project's authored intent as the one editable session. Every open project has
    /// one: a schema-1 manifest converted at open, or an open that failed.
    ///
    /// <para>The document owns it. Nothing here mints one: a second session over the same project would be a
    /// second owner, and whichever saved last would be the whole truth.</para></summary>
    internal AuthoredEditSession EditSession => _projectDocument.Session;

    private string? EditProjectRoot => _projectDocument.Session.Snapshot().RootDir;

    /// <summary>The open project document.</summary>
    internal AuthoredProjectDocument ProjectDocument => _projectDocument;

    // ---- reading the install ------------------------------------------------------------------------

    /// <summary>What the page says when the game's forward view is not loaded — the app's one sentence for
    /// that state.</summary>
    internal const string EditGameUnavailable = Remold.Core.GameFilesGate.Unavailable;

    /// <summary>How far along this install is with one subject — the one predicate behind every surface that
    /// has to say why a subject's model is not in hand, so they cannot say three different things about one
    /// state.
    ///
    /// <para>SCANNING counts as reading. The tree turns interactive at the end of the load's first phase and
    /// the forward view lands in the second, so on a machine WITH a game there is a stretch — the whole of
    /// it on the first run, and again after every rescan — where the install is on its way and nothing is in
    /// hand. Reading and no-install are separate unmeasured states so each refuses with the sentence that
    /// tells the modder whether to wait or locate the game.</para></summary>
    private EditSubjectRead SubjectReadState(string subject, string outfit)
    {
        if (!IsScanning && _vfs is null && _subjectModelWarm is null)
            return EditSubjectRead.Unavailable;
        if (_subjectModels.TryGet(subject, outfit) is not null) return EditSubjectRead.Answered;
        if (IsScanning) return EditSubjectRead.Reading;   // a rescan re-reads every selected subject
        if (_subjectModels.IsUnreadable(subject, outfit)) return EditSubjectRead.Unreadable;
        return _vfs is not null || _subjectModelWarm is not null
            ? EditSubjectRead.Reading : EditSubjectRead.Unavailable;
    }

    public EditSubjectRead SubjectRead(TargetPart part) => SubjectReadState(part.Subject, part.Outfit);

    /// <summary>Why a verb that needs one subject's model cannot run, in the sentence for the state it is
    /// actually in: a wait, a read that ended without it, or no install at all. The third said the first two
    /// as well until the states were told apart, which sent a modder to Locate game over a game the app had
    /// already found.</summary>
    private string SubjectReadFailure(string subject, string outfit) =>
        SubjectReadState(subject, outfit) switch
        {
            EditSubjectRead.Unavailable => GameFilesGate.Unavailable,
            EditSubjectRead.Reading => GameFilesGate.SubjectReading,
            EditSubjectRead.Unreadable => GameFilesGate.SubjectUnreadable,
            _ => EditGameUnavailable,
        };

    public EditInstallState InstallState() =>
        _vfs is not null || _subjectModelWarm is not null ? new EditInstallState()
        : IsScanning ? new EditInstallState(IsReading: true)
        : new EditInstallState(Unavailable: EditGameUnavailable);

    /// <summary>Re-anchor one part against the current install. A resolver instance caches the bundles it
    /// deobfuscates, so one is kept per install rather than minted per part; a rescan replaces the install and
    /// the next ask mints a fresh one.
    ///
    /// <para>Serialized on its own gate rather than owned by the UI thread: the cache behind it is a plain
    /// dictionary, and the page reads this off-thread through <see cref="ResolvePartAsync"/> while the mint
    /// and shading routes still ask for it where the modder clicked. One lock is what lets both be true —
    /// the wait it can impose is one part's read, which is the cost the caller was going to pay anyway.</para>
    /// </summary>
    public LegacyResolvedPart? ResolvePart(TargetPart target)
    {
        lock (_editResolverGate)
        {
            if (_vfs is not { } vfs) return null;
            if (_resolvedPartCache.TryGet(vfs, target, out var cached)) return cached;
            if (_editResolver is null || !ReferenceEquals(_editResolverInstall, vfs))
            {
                _editResolver = new LegacyProjectResolver(NewResolverEnvironment(vfs));
                _editResolverInstall = vfs;
            }
            var resolved = _editResolver.ResolvePart(target);
            _resolvedPartCache.Store(vfs, target, resolved);
            return resolved;
        }
    }

    public Task<LegacyResolvedPart?> ResolvePartAsync(TargetPart target) =>
        Task.Run(() => ResolvePart(target));

    private readonly object _editResolverGate = new();
    private readonly InstallResolvedPartCache _resolvedPartCache = new();
    private LegacyProjectResolver? _editResolver;
    private GameVfs? _editResolverInstall;

    /// <summary>One part's short token — <c>cloth1</c> — off the subject model. A PEEK: the page redraws on
    /// every change and asks for every row's token, and building a subject model costs seconds. A subject
    /// nothing has read yet answers empty, which leaves the renderer slot standing as the row's title.</summary>
    public string PartToken(TargetPart part) => SubjectPartOf(part)?.Token ?? "";

    /// <summary>What the game draws on one slot. Null where the install cannot name it — the project holds no
    /// name of its own for a game texture, so there is nothing else to fall back to.</summary>
    public string? GameTextureName(EditSlotRef slot)
    {
        if (GameMapFor(slot) is not { } map) return null;
        return map.TextureName.Length > 0 ? map.TextureName : null;
    }

    /// <summary>How many USES of the item draw the game texture behind one slot — one use being one material
    /// position of one part, which is the grain the build binds a picture at. A part that draws the same
    /// texture at two of its own positions is two uses: the build rebinds by the texture's identity, so an
    /// edit made at one of them lands on both, exactly as it would on two parts.
    ///
    /// <para>A peek at the subject model, as the token and the texture name are: a subject the install has
    /// not answered for has NO COUNT, and a slot with no installed map behind it answers 0. Two maps are the
    /// same texture when they name the same logical bundle AND the same object in it — the path id where the
    /// material pinned one, the texture's name where it did not, which is the same selector every read of
    /// these uses.</para>
    /// </summary>
    public int? TextureUses(EditSlotRef slot)
    {
        var part = slot.Edit.Part;
        if (_subjectModels.TryGet(part.Subject, part.Outfit) is not { } model)
            return null;
        // A position the install has no map at: a slot the game no longer draws anything through after an
        // update reads as zero uses, which is private — there is no stock texture here to reach through.
        if (GameMapFor(slot) is not { } map) return 0;
        IReadOnlyDictionary<string, int> index;
        lock (_textureUseIndexGate)
        {
            if (!_textureUseIndexes.TryGetValue(model, out index!))
            {
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in model.Parts)
                    foreach (var material in candidate.Materials)
                        foreach (string identity in material.Maps.Select(TextureIdentity)
                                     .Distinct(StringComparer.OrdinalIgnoreCase))
                            counts[identity] = counts.GetValueOrDefault(identity) + 1;
                _textureUseIndexes[model] = index = counts;
            }
        }
        return index.GetValueOrDefault(TextureIdentity(map));
    }

    private static string TextureIdentity(SubjectMap map) => map.BundleId + "\u001f"
        + (map.PathId != 0 ? "#" + map.PathId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : map.TextureName);

    /// <summary>The same logical bundle and the same object in it. A pinned map and an unpinned one read
    /// as different, which is the conservative half: an unproven pairing leaves the boundary open rather
    /// than refusing an edit nothing has shown to be shared.</summary>
    private static bool SameTexture(SubjectMap a, SubjectMap b) =>
        string.Equals(a.BundleId, b.BundleId, StringComparison.OrdinalIgnoreCase)
        && (a.PathId != 0 || b.PathId != 0
            ? a.PathId == b.PathId
            : string.Equals(a.TextureName, b.TextureName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The installed map behind one card, where its answer resolves to one.
    ///
    /// <para>The domain alone cannot say: a game-domain slot draws the material at its own position, but a
    /// replacement's own output draws whatever the edit ASKS of it — the original map where it keeps the
    /// carrier's, and the map the slot it names resolves to where it takes another slot's value. Only an
    /// output carrying the mod's own file has no installed map behind it, and that file is what the card
    /// shows instead.</para>
    ///
    /// <para>A source answer is followed exactly once. The model's source slots name a slot that answers for
    /// itself; a chain would be a second hop, and a card is not the place to walk one.</para></summary>
    private SubjectMap? GameMapFor(EditSlotRef slot)
    {
        if (slot.Domain == TargetSlotDomain.Game)
            return MapAt(slot.Edit.Part, slot.MaterialSlotIndex ?? 0, slot.Input, slot.ShaderProperty);
        return slot.Binding switch
        {
            BindingKind.InheritedLiveCarrier => MapAt(slot.Edit.Part,
                slot.GameMaterialSlotIndex ?? slot.MaterialSlotIndex ?? 0, slot.Input, slot.ShaderProperty),
            BindingKind.SourceSlot when SourceAnswer(slot.Source) is { Slot: { } from } source =>
                source.File is not null ? null
                    : MapAt(from.Part,
                        from.Domain == TargetSlotDomain.Game
                            ? from.MaterialSlotIndex ?? 0
                            : slot.GameMaterialSlotIndex ?? from.MaterialSlotIndex ?? 0,
                        from.Input, from.ShaderProperty),
            _ => null,
        };
    }

    /// <summary>The map one installed material position draws on one input, or null where the subject has
    /// not been read, the position is past what the install has, or the shader binds nothing there.</summary>
    private SubjectMap? MapAt(TargetPart target, int materialSlotIndex, TargetInputKind input,
        string? shaderProperty)
    {
        if (SubjectPartOf(target) is not { } part) return null;
        if (materialSlotIndex < 0 || materialSlotIndex >= part.Materials.Count) return null;
        return part.Materials[materialSlotIndex].Maps.FirstOrDefault(map =>
            shaderProperty is { Length: > 0 }
                ? string.Equals(map.Slot, shaderProperty, StringComparison.Ordinal)
                : InputOfSlot(map.Slot) == input);
    }

    /// <summary>What the slot a source answer names holds: the slot itself, and the mod's own file where
    /// that slot binds one. Both shapes the model allows are read here — a slot an edit owns, and an exact
    /// game slot no edit owns, which is how the recorded keep-the-original-toon-ramp answer is written.
    /// Null where there is no session or the named slot is gone.</summary>
    private (TargetSlot? Slot, string? File) SourceAnswer(EditSlotSource? source)
    {
        if (source is null || EditSession is not { } session) return (null, null);
        try
        {
            if (source.EditDefinitionId is { Length: > 0 } editId)
            {
                var state = session.Slots(editId).FirstOrDefault(candidate =>
                    string.Equals(candidate.Slot.Id, source.SlotId, StringComparison.Ordinal));
                return state is null ? (null, null) : (state.Slot, state.ProjectAsset?.File);
            }
            return (session.Snapshot().TargetSlots.FirstOrDefault(slot =>
                string.Equals(slot.Id, source.SlotId, StringComparison.Ordinal)), null);
        }
        catch { return (null, null); }
    }

    private static TargetInputKind InputOfSlot(string shaderSlot) =>
        MaterialResolver.IsBaseColor(shaderSlot) ? TargetInputKind.BaseColor
        : MaterialResolver.IsNormal(shaderSlot) ? TargetInputKind.Normal
        : MaterialResolver.IsRmo(shaderSlot) ? TargetInputKind.Rmo
        : MaterialResolver.IsRamp(shaderSlot) ? TargetInputKind.Ramp
        : MaterialResolver.IsBlend(shaderSlot) ? TargetInputKind.Blend
        : TargetInputKind.Texture;

    /// <summary>Every part the install says one subject has, in the model's own order. A PEEK for the same
    /// reason <see cref="PartToken"/> is one — the page asks for every selected subject on every redraw, and
    /// building a subject model costs seconds — so a subject nothing has read yet answers empty.</summary>
    public IReadOnlyList<TargetPart> SubjectParts(string subject, string outfit) =>
        _subjectModels.TryGet(subject, outfit)?.Parts
            .Select(part => new TargetPart
            {
                Subject = subject, Outfit = outfit, RendererSlot = part.SlotName,
            }).ToList()
        ?? (IReadOnlyList<TargetPart>)Array.Empty<TargetPart>();

    /// <summary>One part in the install's own terms, or null where the subject has not been read yet or the
    /// install does not carry the renderer slot.</summary>
    private SubjectPart? SubjectPartOf(TargetPart part) =>
        _subjectModels.TryGet(part.Subject, part.Outfit)?.Parts
            .FirstOrDefault(p => string.Equals(p.SlotName, part.RendererSlot, StringComparison.OrdinalIgnoreCase));

    // A bone tree is a per-subject read that never changes under one install, and the page asks for every
    // subject's on every redraw. Dropped with the roster, where the models it is derived from are dropped.
    private readonly Dictionary<string, EditSkeletonOutline> _editSkeletons = new(StringComparer.Ordinal);

    public EditSkeletonOutline? ReadSkeleton(string subject, string outfit)
    {
        string key = subject + "" + outfit;
        if (_editSkeletons.TryGetValue(key, out var held)) return held;
        if (_subjectModels.TryGet(subject, outfit)?.Skeleton is not { } skeleton) return null;
        var outline = new EditSkeletonOutline(skeleton.BoneCount,
            SkeletonOutline.Tree(skeleton.Bones).Select(node => new EditPage.SkeletonNodeVm(node)).ToList());
        _editSkeletons[key] = outline;
        return outline;
    }

    /// <summary>Drop the per-subject reads the page memoizes. Called where the subject models themselves are
    /// dropped, since these are derived from them.</summary>
    private void ClearEditPageReads()
    {
        _editSkeletons.Clear();
        lock (_textureUseIndexGate) _textureUseIndexes.Clear();
        lock (_shadingSourceCacheGate)
        {
            _shadingSourceRows.Clear();
            _shadingSourceCacheInstall = null;
        }
        _rampCache.Clear();
        lock (_editResolverGate)
        {
            _editResolver = null;
            _editResolverInstall = null;
            _resolvedPartCache.Clear();
        }
        // The wardrobe tables a Blender open classifies part presence against were read off THIS install too.
        lock (_exportSchemesGate) _exportSchemes = null;
        // The page memoizes each part's resolved answer too, and those were read off THIS install.
        EditPage.ForgetInstallReads();
    }

    // ---- previews -----------------------------------------------------------------------------------

    public Task<EditMeshPreview?> LoadPartMeshPreviewAsync(TargetPart part) =>
        RenderMeshAsync(part, editDefinitionId: null);

    public Task<EditMeshPreview?> LoadEditMeshPreviewAsync(EditRef edit) =>
        RenderMeshAsync(edit.Part, edit.EditDefinitionId);

    /// <summary>Render what one row draws. A content edit binding its own geometry renders THAT file; an edit
    /// asking the game for its own geometry, and a part with no edits, render the game's. Either way the maps
    /// sampled are the ones this edit binds where it binds any, so the picture is of what would ship.
    ///
    /// <para>The whole read runs off the UI thread. Returning null is the row's quiet no-preview state; a
    /// throw is its cause line, so a bundle the game is holding reads as a retryable failure rather than as
    /// geometry that will not draw.</para></summary>
    private async Task<EditMeshPreview?> RenderMeshAsync(TargetPart part, string? editDefinitionId)
    {
        if (SubjectPartOf(part) is not { } subjectPart) return null;
        var recipe = subjectPart.ToRecipePart();
        var slots = editDefinitionId is null || EditSession is not { } session
            ? null : session.Slots(editDefinitionId);
        string? root = EditProjectRoot;

        // Read on the UI thread, rendered off it: the session and the project are UI-thread state.
        var (projectGlb, missing) = GeometryFile(slots, root);
        if (missing is not null) throw new FileNotFoundException(GeometryFileMissing(missing));
        var samplerPlan = SamplerPlan(subjectPart, slots, root);

        return await Task.Run(() =>
        {
            var samplers = BuildSamplers(samplerPlan, out bool ownMaps);
            if (projectGlb is not null)
            {
                var edited = EditPreviewService.RenderProjectMesh(projectGlb, samplers);
                if (edited is not { } render) return null;
                // The count and nothing else: rendering here would cache an untextured picture under
                // the game-identity key the bare part's preview is served from.
                int? original = _editPreviews.GameMeshVertexCount(recipe);
                return new EditMeshPreview(EditPreviewService.DecodeMesh(render.Png), render.VertexCount,
                    original);
            }
            var game = _editPreviews.RenderGameMesh(recipe, samplers, ownMaps,
                cacheable: !MissingExpectedGameMaps(samplerPlan, samplers));
            return game is { } drawn
                ? new EditMeshPreview(EditPreviewService.DecodeMesh(drawn.Png), drawn.VertexCount, null)
                : null;
        });
    }

    /// <summary>True when the plan named a game map for some material and the built sampler for that
    /// position came back null — a transient decode/thumb failure, not the part's real look. Such a
    /// render may still be shown, but must not be CACHED as the part's game-identity picture.</summary>
    internal static bool MissingExpectedGameMaps(
        IReadOnlyList<(bool Own, string? File, string? Bundle, string? Texture)> plan,
        IReadOnlyList<MeshPreviewRenderer.PreviewTexture?>? samplers)
    {
        for (int i = 0; i < plan.Count; i++)
        {
            var (own, _, bundle, texture) = plan[i];
            if (own || bundle is null || texture is null) continue;
            if (samplers is null || i >= samplers.Count || samplers[i] is null) return true;
        }
        return false;
    }

    /// <summary>The lod0 geometry this edit binds: the absolute file where it binds one, null where it asks
    /// the game for its own, and the project-relative name where it binds a file that is NOT on disk.
    ///
    /// <para>The third answer is why this is not a plain <c>string?</c>. An edit whose replacement went
    /// missing must not quietly render as the game's own geometry: that is a picture of something the modder
    /// did not author, shown under their edit's name. The caller says so instead.</para></summary>
    internal static (string? Path, string? Missing) GeometryFile(IReadOnlyList<EditSlotState>? slots,
        string? root)
    {
        if (slots is null) return (null, null);
        var geometry = slots.FirstOrDefault(state => state.Slot.Input == TargetInputKind.Geometry
            && string.Equals(state.Slot.Tier, "lod0", StringComparison.OrdinalIgnoreCase))
            ?? slots.FirstOrDefault(state => state.Slot.Input == TargetInputKind.Geometry
                && state.Slot.Tier is null);
        if (geometry?.Binding.Kind != BindingKind.ProjectAsset) return (null, null);
        if (geometry.ProjectAsset?.File is not { Length: > 0 } file) return (null, null);
        if (root is null) return (null, file);
        try
        {
            string path = Path.GetFullPath(Path.Combine(root, file));
            return File.Exists(path) ? (path, null) : (null, file);
        }
        catch { return (null, file); }
    }

    /// <summary>What a render says when the edit's own geometry file is gone from the mod folder. Names the
    /// file, because putting it back is the whole remedy.</summary>
    internal static string GeometryFileMissing(string file) =>
        $"{file} isn't in the mod folder. Send it back from Blender again, or use Revert mesh.";

    /// <summary>What each submesh samples, in renderer-slot order: the file this edit's answer resolves to
    /// where it resolves to one, else the game's own texture. Read on the UI thread; decoded on a worker.
    ///
    /// <para>An answer is not only a file bound HERE. A slot taking another slot's value resolves to that
    /// slot's file, which is the same one hop <see cref="LoadMapPreviewAsync"/> follows for the card beside
    /// this render — and the same file a build would emit, since AuthoredBuildPlanner resolves a source
    /// binding through the edit that owns it. Reading only the direct binding would make this the one surface
    /// of the three that answers with the GAME's map, under the edit's own name.</para>
    ///
    /// <para>No route in the app authors a cross-EDIT borrow today: every command that writes a source
    /// binding — the kept-original ramp, the copied material values, the older format's conversion — names an
    /// exact game slot and no edit. This resolves what the model permits, so the three answers cannot come
    /// apart when one does.</para></summary>
    internal IReadOnlyList<(bool Own, string? File, string? Bundle, string? Texture)> SamplerPlan(
        SubjectPart part, IReadOnlyList<EditSlotState>? slots, string? root)
    {
        var plan = new List<(bool Own, string? File, string? Bundle, string? Texture)>(part.Materials.Count);
        for (int i = 0; i < part.Materials.Count; i++)
        {
            int position = i;
            var here = slots?.Where(state => state.Slot.Input == TargetInputKind.BaseColor
                && state.Slot.MaterialSlotIndex == position).ToList();
            // A file bound here outranks a borrowed one: this edit's own answer for the position is the
            // nearer of the two, and reading it first leaves every plan that has one exactly as it was.
            string? relative = here?.FirstOrDefault(state =>
                state.Binding.Kind == BindingKind.ProjectAsset)?.ProjectAsset?.File;
            if (relative is not { Length: > 0 })
                relative = here?.Select(BorrowedFile).FirstOrDefault(file => file is { Length: > 0 });
            if (relative is { Length: > 0 } answered)
            {
                // A slot the modder answered is theirs whatever happens next: a file that will not resolve
                // renders that submesh untextured rather than falling back to the game's map, which would put
                // the picture the edit replaced back on screen under the edit's own name. A borrowed answer
                // is answered the same way — the modder made that choice too.
                string? file = null;
                if (root is not null)
                {
                    try
                    {
                        string path = Path.GetFullPath(Path.Combine(root, answered));
                        if (File.Exists(path)) file = path;
                    }
                    catch { file = null; }
                }
                plan.Add((true, file, null, null));
                continue;
            }
            var map = part.Materials[i].Maps.FirstOrDefault(m => MaterialResolver.IsBaseColor(m.Slot));
            plan.Add((false, null, map?.BundleId, map?.TextureName));
        }
        return plan;
    }

    /// <summary>The mod's own file one source-slot answer resolves to, or null for every other binding and
    /// for a source that answers with the game's own value. One hop, exactly as the card takes it.</summary>
    private string? BorrowedFile(EditSlotState state) =>
        state.Binding is { Kind: BindingKind.SourceSlot, SourceSlot: { } from }
            ? SourceAnswer(new EditSlotSource(from.EditDefinitionId, from.SlotId)).File
            : null;

    /// <summary>Decode one plan into sampling textures. <paramref name="ownMaps"/> reports whether any slot
    /// took a file of the modder's — the evidence the render's persistence is decided on, so a modder-textured
    /// render is never filed under a key naming game identity alone.</summary>
    private IReadOnlyList<MeshPreviewRenderer.PreviewTexture?>? BuildSamplers(
        IReadOnlyList<(bool Own, string? File, string? Bundle, string? Texture)> plan, out bool ownMaps)
    {
        ownMaps = false;
        if (plan.Count == 0) return null;
        var result = new MeshPreviewRenderer.PreviewTexture?[plan.Count];
        bool any = false;
        for (int i = 0; i < plan.Count; i++)
        {
            var (own, file, bundle, texture) = plan[i];
            if (own)
            {
                // Counted as the modder's the moment the slot is TAKEN, file or no file and decode or no
                // decode: the game's map is not consulted for this slot either way, so the render is not the
                // game's whatever comes back — and must not be filed under a key that says it is.
                ownMaps = true;
                if (file is not null) result[i] = EditPreviewService.Sampler(file);
                any |= result[i] is not null;
                continue;
            }
            if (bundle is null || texture is null) continue;
            result[i] = _editPreviews.Sampler(bundle, texture);
            any |= result[i] is not null;
        }
        return any ? result : null;
    }

    public async Task<EditMapPreview?> LoadMapPreviewAsync(EditSlotRef slot)
    {
        bool rmo = slot.Input == TargetInputKind.Rmo;
        string? root = EditProjectRoot;
        var (own, missing) = MapFile(slot, root);
        // A file the edit answers with and the mod folder does not hold is the card's own state, not the
        // absence of an answer. Falling through to the game's map would put the picture this edit replaced
        // back on screen under the edit's name — the honesty rule the render beside the card already keeps,
        // and the build refuses the same file by name.
        if (missing is not null) return new EditMapPreview(null, EditMapCardVm.NoDimensions, missing);
        var map = own is null ? GameMapFor(slot) : null;

        return await Task.Run(() =>
        {
            if (own is not null) return EditPreviewService.DecodeProjectMap(own, rmo);
            if (map is null) return null;
            string? thumb = _editPreviews.GameTextureThumb(map.BundleId, map.TextureName);
            if (thumb is null) return null;
            // The thumbnail is scaled down, so its own pixel size is not the texture's. The extent comes off
            // the game texture's header, which is what the shipped pane's size line reads too.
            return DecodeCardPicture(thumb, rmo, GameTextureDimensions(map));
        });
    }

    public async Task<IReadOnlyList<EditMapPreview?>> LoadMapPreviewsAsync(
        IReadOnlyList<EditSlotRef> slots)
    {
        var routes = slots.Select((slot, index) =>
        {
            var (own, missing) = MapFile(slot, EditProjectRoot);
            return new CardPreviewRoute(index, slot.Input == TargetInputKind.Rmo, own, missing,
                own is null && missing is null ? GameMapFor(slot) : null);
        }).ToArray();

        return await Task.Run(() =>
        {
            var results = new EditMapPreview?[slots.Count];
            foreach (var route in routes.Where(route => route.Missing is not null))
                results[route.Index] = new EditMapPreview(null, EditMapCardVm.NoDimensions, route.Missing);

            var game = routes.Where(route => route.Own is null && route.Missing is null && route.Map is not null)
                .ToArray();
            var thumbs = _editPreviews.GameTextureThumbs(game.Select(route =>
                (route.Map!.BundleId, route.Map.TextureName)).ToArray());

            var work = routes.Where(route => route.Own is not null).Select(route => (route, thumb: (Core.Workbench.ThumbnailCache.TextureThumb?)null))
                .Concat(game.Select((route, i) => (route, thumb: thumbs[i]))).ToArray();
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
            {
                if (item.route.Own is { } own)
                    results[item.route.Index] = EditPreviewService.DecodeProjectMap(own, item.route.Rmo);
                else if (item.thumb is { } thumb)
                    results[item.route.Index] = DecodeCardPicture(thumb.Path, item.route.Rmo,
                        $"{thumb.Width}×{thumb.Height}");
            });
            return (IReadOnlyList<EditMapPreview?>)results;
        });
    }

    private sealed record CardPreviewRoute(int Index, bool Rmo, string? Own, string? Missing,
        SubjectMap? Map);

    /// <summary>The map file one card's answer resolves to: the absolute file where the mod folder holds it,
    /// null where the card has no file of the mod's behind it at all, and the project-relative name where the
    /// answer names a file that is NOT there.
    ///
    /// <para>Three answers rather than two, and for the reason <see cref="GeometryFile"/> is shaped the same
    /// way: a bound name and a missing file are one null to <see cref="Rooted"/>, and a card that cannot tell
    /// them apart shows the game's own texture under the edit's name. Both routes to a file are read — the
    /// one this slot binds, and the one a source answer resolves to, which is the same one hop
    /// <see cref="SamplerPlan"/> follows for the render beside the card.</para>
    ///
    /// <para>A mod with no folder yet reports the name as missing, exactly as <see cref="GeometryFile"/>
    /// does: there is nowhere the file could be, so it is not there.</para></summary>
    private (string? Path, string? Missing) MapFile(EditSlotRef slot, string? root)
    {
        // A source answer that lands on the mod's own file draws THAT file: the card shows the picture the
        // modder's choice puts on screen, not the original the source slot stands over.
        string? bound = slot.ProjectRelativeFile is { Length: > 0 } direct ? direct
            : slot.Binding == BindingKind.SourceSlot ? SourceAnswer(slot.Source).File : null;
        if (bound is not { Length: > 0 } relative) return (null, null);
        return Rooted(relative, root) is { } path ? (path, null) : (null, relative);
    }

    /// <summary>One project-relative name as a file on disk, or null where the mod has no folder yet, the
    /// name is empty, or nothing is there.</summary>
    private static string? Rooted(string? projectRelativeFile, string? root)
    {
        if (projectRelativeFile is not { Length: > 0 } relative || root is null) return null;
        try
        {
            string path = Path.GetFullPath(Path.Combine(root, relative));
            return File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    private EditMapPreview? DecodeCardPicture(string path, bool rmo, string dimensions)
    {
        try
        {
            // Shared for write and delete: an editor save or a revert landing while this decode reads must
            // not fail on the handle it holds.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return new EditMapPreview(EditPreviewService.DecodeMap(stream, rmo), dimensions);
        }
        catch { return new EditMapPreview(null, dimensions); }
    }

    private string GameTextureDimensions(SubjectMap map)
    {
        try
        {
            var bytes = TryDeobfuscateBundle(map.BundleId);
            var probe = bytes is null ? null : TextureExport.Probe(bytes, map.Ref);
            return probe is { } p ? $"{p.Width}×{p.Height}" : EditMapCardVm.NoDimensions;
        }
        catch { return EditMapCardVm.NoDimensions; }
    }

    // ---- the external tools -------------------------------------------------------------------------

    public async Task OpenPartInBlenderAsync(TargetPart target, bool withReferences,
        IProgress<string> status)
    {
        if (SubjectPartOf(target) is not { } part)
        { status.Report(SubjectReadFailure(target.Subject, target.Outfit)); return; }
        if (withReferences && part.IsStatic) { status.Report(BlenderGate.StaticPart); return; }
        await OpenSessionBlenderAsync(target.Subject, target.Outfit, target, requestedEditId: null,
            withReferences, openAllMode: SessionBlenderOpenAll.None, status: status);
    }

    public async Task OpenInBlenderAsync(EditRef edit, bool withReferences, IProgress<string> status)
    {
        if (SubjectPartOf(edit.Part) is not { } part)
        { status.Report(SubjectReadFailure(edit.Part.Subject, edit.Part.Outfit)); return; }
        if (withReferences && part.IsStatic) { status.Report(BlenderGate.StaticPart); return; }
        await OpenSessionBlenderAsync(edit.Part.Subject, edit.Part.Outfit,
            edit.Part, edit.EditDefinitionId, withReferences,
            openAllMode: SessionBlenderOpenAll.None, status: status);
    }

    // ---- the subject's own verbs ----
    //
    // The subject's parts come from the install model cache, while every writable destination comes from the
    // authored session by exact edit and slot identity.

    // An outfit the roster cannot answer for leaves the character's own name standing alone. Two subjects
    // that then read alike are told apart by the tree itself, which puts each one's stem back on its row
    // for exactly that case.
    public string SubjectLabel(string subject, string outfit) =>
        PickOutfit(subject, outfit) is { } model
            ? _friendly.Subject(subject, model)
            : _friendly.Character(subject);

    public async Task OpenSubjectInBlenderAsync(string subject, string outfit, IProgress<string> status)
    {
        var model = _subjectModels.TryGet(subject, outfit);
        if (model is null) { status.Report(SubjectReadFailure(subject, outfit)); return; }
        if (model.AllPartsStatic) { status.Report(BlenderGate.StaticOnly); return; }
        await OpenSessionBlenderAsync(subject, outfit, requested: null, requestedEditId: null,
            withReferences: true, openAllMode: SessionBlenderOpenAll.Stock, status: status);
    }

    public async Task OpenSubjectFirstEditInBlenderAsync(string subject, string outfit,
        IProgress<string> status)
    {
        var model = _subjectModels.TryGet(subject, outfit);
        if (model is null) { status.Report(SubjectReadFailure(subject, outfit)); return; }
        if (model.AllPartsStatic) { status.Report(BlenderGate.StaticOnly); return; }
        await OpenSessionBlenderAsync(subject, outfit, requested: null, requestedEditId: null,
            withReferences: true, openAllMode: SessionBlenderOpenAll.FirstEdit, status: status);
    }

    private enum SessionBlenderOpenAll { None, Stock, FirstEdit }

    /// <param name="OpenedFromMeshEdit">The part opened from its edit's own authored geometry file — the
    /// one fact the bridge's "already carries an edit" warning means: a send would replace that mesh. An
    /// edit fresh from the original has nothing a send could lose, so it stays false there.</param>
    private sealed record SessionBlenderPart(TargetPart Target, SubjectPart Model, RecipePart Recipe,
        string PreparedGlb,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? Maps, bool OpenedFromMeshEdit);

    private sealed record SessionBlenderWrite(SessionBlenderPart Part, string EditId,
        EditSlotState Geometry, ProjectAssetIngressSession Ingress);

    private sealed class SessionBlenderPrepareScope : IDisposable
    {
        private readonly MainWindowViewModel _owner;
        private readonly bool _combined;
        private int _disposed;

        public SessionBlenderPrepareScope(MainWindowViewModel owner, bool combined)
        {
            _owner = owner;
            _combined = combined;
            if (combined) Interlocked.Increment(ref owner._buildingCombinedRig);
        }

        public void Dispose()
        {
            if (!_combined || Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (Interlocked.Decrement(ref _owner._buildingCombinedRig) == 0)
                _owner.RunQueuedRescan();
        }
    }

    internal IDisposable BeginSessionBlenderPrepare(bool combined) =>
        new SessionBlenderPrepareScope(this, combined);

    /// <summary>Store completed game-side products off the open's critical path: Blender never waits on
    /// cache copies. The interactive scope rides the store so speculation stays parked until it lands —
    /// its restart then finds the entries instead of rebuilding the subject. A failed store is only a lost
    /// optimization. Call on the UI thread (the scope's bookkeeping lives there).</summary>
    private void DeferRiggedGlbPublication(string label, Func<bool> publish)
    {
        var scope = BeginInteractiveRiggedGlbOpen();
        _ = Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            bool stored = false;
            try { stored = publish(); }
            catch { /* a failed store is only a lost optimization */ }
            finally
            {
                scope.Dispose();
                BlenderOpenTiming.WriteLine(
                    $"{watch.ElapsedMilliseconds,9:N0} ms  {label} deferred{(stored ? "" : " (declined)")}");
            }
        });
    }

    // ---- the candidacy roster a rigged open filters its bone tail against ----------------------------
    //
    // Without one, every open offers the WHOLE subject skeleton: bones a build is certain to refuse paint
    // on, painted in Blender and thrown away on the way back. The filter itself lives in Core
    // (AssetExporter.ValidTailBones, off Migoto.PoolDerive.PoolCandidates — the one candidacy seam); what
    // the app owes it is the subject's rows.

    /// <summary>The wardrobe scheme by outfit stem, for the whole forward view: every open of every subject
    /// classifies part presence against the same tables, and reading them is a game-file read.
    ///
    /// <para>Read on the OPEN's own worker thread, never the UI one. Two opens racing the first ask both
    /// build one <see cref="Lazy{T}"/> and one wins — a duplicated table read at worst, never two different
    /// answers, since the tables are the same on both.</para>
    ///
    /// <para>A FAILED load is not "this outfit isn't modular", and it is the WORSE answer of the two: every
    /// modular part classifies as an unknown variant, no sibling can vouch for another, and the coverage
    /// pass that would have widened the tail returns nothing — so the offer collapses to the part's own
    /// posed bones. Bones a build would have accepted paint on simply are not there, which is the direction
    /// the design calls the bad one, and it happens with nothing said unless the open includes it in
    /// <see cref="BlenderOpenNotices"/>.</para>
    ///
    /// <para>Which is why a MOMENTARY failure is retried and nothing else is — see
    /// <see cref="RetryTableRead"/> for which is which. A settled failure is KEPT for the session rather
    /// than re-reading four tables per open to be told the same thing again; an install change clears the
    /// memo either way. The drop races the first ask the same way: a stale extra retry at worst.</para>
    /// </summary>
    private readonly object _exportSchemesGate = new();
    private Lazy<SchemeTables>? _exportSchemes;
    internal Func<string, Dictionary<string, IReadOnlyList<PartScheme.Slot>>> ExportSchemesByStem =
        gameDir => SchemesByStem(gameDir);

    /// <summary>One attempt at the wardrobe tables: the schemes by model stem, or null where the tables
    /// would not read at all — which is NOT the same as an install with no modular outfits.
    /// <see cref="Retry"/> says whether the next open should try again.</summary>
    private readonly record struct SchemeTables(
        Dictionary<string, IReadOnlyList<PartScheme.Slot>>? ByStem, bool Retry);

    /// <summary>Every modular outfit's wardrobe slots by model stem, off the game's own tables. THROWS
    /// whatever the read throws: the two callers differ in what a failure means to them — a build says so on
    /// its status line and keeps its pools conservative for the whole build, an open drops its memo and
    /// re-reads — so only the read itself is shared, and the tables are named in one place.</summary>
    internal static Dictionary<string, IReadOnlyList<PartScheme.Slot>> SchemesByStem(string gameDir,
        Action<string>? note = null)
    {
        var db = GameDatabase.FromGameDir(gameDir);
        return PartScheme.Load(db, note).ByStem(db);
    }

    /// <summary>Whether a failed wardrobe-table read is worth the NEXT open's while, or is this install's
    /// settled answer. A sharing violation is a lock — the game had the file open, and it will not always —
    /// so that one is retried. A table that is not THERE, a folder that is not there, bytes that will not
    /// parse: all of those answer the same way for as long as this install is loaded, and re-reading four
    /// tables per open to be told so again costs every open and changes nothing. An install change clears
    /// the memo regardless, so nothing is remembered across the only event that could alter the answer.
    ///
    /// <para>Not-found is an <see cref="IOException"/> by inheritance and a permanent fact by nature, which
    /// is the one place "retry every I/O failure" gets it wrong.</para></summary>
    internal static bool RetryTableRead(Exception failure) =>
        failure is IOException and not (FileNotFoundException or DirectoryNotFoundException);

    /// <summary>One outfit stem's wardrobe slots, through the session-wide memo, plus whether the tables
    /// themselves would not read (which no null slot list distinguishes on its own).
    ///
    /// <para><paramref name="gameDir"/> is the install the CALLER is working against, passed rather than
    /// read off the live property: an open holds one install for its whole run, and a rescan swapping the
    /// field mid-open would classify presence against a different one.</para></summary>
    internal (IReadOnlyList<PartScheme.Slot>? Slots, bool Unreadable) ExportScheme(string gameDir, string stem)
    {
        Lazy<SchemeTables> lazy;
        lock (_exportSchemesGate)
        {
            lazy = _exportSchemes ??= new Lazy<SchemeTables>(() =>
            {
                try { return new SchemeTables(ExportSchemesByStem(gameDir), Retry: false); }
                catch (Exception e) { return new SchemeTables(null, RetryTableRead(e)); }
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }
        var tables = lazy.Value;
        if (tables.Retry)
        {
            lock (_exportSchemesGate)
                if (ReferenceEquals(_exportSchemes, lazy)) _exportSchemes = null;
        }
        if (tables.ByStem is null) return (null, true);
        return (tables.ByStem.TryGetValue(stem, out var slots) ? slots : null, false);
    }

    /// <summary>
    /// The subject's candidacy roster for a rigged export: every part of <paramref name="model"/> — not only
    /// the ones this open writes a glb for — with the flags <see cref="AssetExporter.SubjectRoster"/> filters
    /// the exported bone tail by.
    ///
    /// <para>Runs OFF the UI thread: the wardrobe tables behind it are a game-file read. The model is the one
    /// the open already holds — nothing is built here, so an open never pays for a subject read it did not
    /// already do.</para>
    ///
    /// <para><paramref name="vfs"/> and <paramref name="gameDir"/> are the caller's OWN install, not the
    /// live fields: a lone open takes no rescan hold, so a rescan landing mid-open would otherwise resolve
    /// these rows through the new catalog while the export reads bundles out of the old one — rows that
    /// resolve nowhere drop out, and every remaining part's tail narrows with nothing said.</para>
    ///
    /// <para>The second half of the answer is whether the WARDROBE TABLES would not read, which the roster
    /// alone cannot say: a null scheme also means "this outfit is not modular". Unreadable tables narrow
    /// every modular subject's offer to its own posed bones, so the open includes it in
    /// <see cref="BlenderOpenNotices"/>.</para>
    ///
    /// <para>Visibility is the part's PREFAB-RESIDENT marker alone. The build merges in the timeline-derived
    /// half as well, but timelines are a build-time input the workbench model never carries, so a part
    /// withheld only by a timeline is admitted here. Deliberate: the export over-offers by that part's bones
    /// and the build's posed gate refuses them, which beats hiding bones a build would have accepted.</para>
    /// </summary>
    private (AssetExporter.SubjectRoster Roster, bool WardrobeUnreadable) ExportRoster(
        GameVfs vfs, string gameDir, SubjectModel model)
    {
        var scheme = ExportScheme(gameDir, model.Stem);
        return (ExportRosterRows(vfs.Catalog, model, scheme.Slots), scheme.Unreadable);
    }

    /// <summary>The roster rows themselves, once the model and the scheme are in hand: one row per part of
    /// <paramref name="model"/> that resolves to a bundle, keyed by the representative slot name and carrying
    /// the part token presence classifies from — two different strings, and pairing them the other way round
    /// would classify every part unknown. Separated from <see cref="ExportRoster"/> so this assembly can be
    /// exercised without a loaded game.</summary>
    internal static AssetExporter.SubjectRoster ExportRosterRows(CatalogIndex catalog, SubjectModel model,
        IReadOnlyList<PartScheme.Slot>? scheme)
    {
        var parts = new List<AssetExporter.RosterPart>();
        foreach (var p in model.Parts)
        {
            // The same forward resolution the build's roster probe does: an smr-body part is addressed by
            // bundle + path id (same-named copies in one enemy bundle), everything else by its recipe
            // address through the catalog. BOTH halves are required for the smr route — a bundle with no
            // path id cannot select among same-named copies, so such a part falls back to its address like
            // any other, and one carrying no address either drops out.
            //
            // A dropped part is not a neutral omission: it has no mesh to measure, so it certifies nothing,
            // and every SIBLING that would have been vouched for by it loses that vouching — their tails
            // narrow, silently. Its own tail goes the other way (unknown candidacy offers everything). So
            // the drop is the conservative answer for one part and the lossy one for the rest, which is why
            // the resolution here has to match the build's exactly.
            bool smr = !string.IsNullOrEmpty(p.MeshBundle) && p.MeshPathId != 0;
            var bundle = smr ? p.MeshBundle!
                : string.IsNullOrEmpty(p.MeshAddress) ? null : catalog.ResolveAddress(p.MeshAddress);
            if (string.IsNullOrEmpty(bundle)) continue;
            parts.Add(new AssetExporter.RosterPart(p.SlotName, p.Token, bundle!, smr ? p.MeshPathId : 0,
                p.CastsShadows, p.Visibility));
        }
        return new AssetExporter.SubjectRoster(parts, scheme, model.PartsPoolAlone);
    }

    private async Task OpenSessionBlenderAsync(string subject, string outfit, TargetPart? requested,
        string? requestedEditId, bool withReferences, SessionBlenderOpenAll openAllMode,
        IProgress<string> status)
    {
        bool openAll = openAllMode != SessionBlenderOpenAll.None;
        using var interactivePriority = BeginInteractiveRiggedGlbOpen();
        // The open's timing block, written only for an open that reached Blender. Sub-steps a helper timed
        // itself land as "· " lines inside their phase's subtotal.
        var openTiming = new List<string>();
        var openWatch = Stopwatch.StartNew();
        var totalWatch = Stopwatch.StartNew();
        void TimeMark(string label, long ms) => openTiming.Add($"{ms,9:N0} ms  {label}");
        void Phase(string label) { TimeMark(label, openWatch.ElapsedMilliseconds); openWatch.Restart(); }
        if (EditSession is not { } session || _vfs is not { } vfs
            || GameDir is not { Length: > 0 } gameDir
            || PickOutfit(subject, outfit) is not { } outfitModel)
        { status.Report(EditGameUnavailable); return; }
        if (_subjectModels.TryGet(subject, outfit) is not { } model)
        { status.Report(SubjectReadFailure(subject, outfit)); return; }
        AutoNameFromSubject(subject);
        if (!EnsureModRoot()) { status.Report("Couldn't create the mod folder."); return; }
        // A return already taken but not yet committed is intent this open would compose around. The scan
        // used to land its edits on the caller's own thread, ahead of everything after it; now that the
        // reading half runs on a worker, THIS is where that ordering is kept — the one caller whose
        // outbound snapshot a pending return would make stale.
        await PendingBlenderReturns;
        var snapshot = session.Snapshot();
        if (snapshot.RootDir is null) { status.Report("Couldn't create the mod folder."); return; }

        var blender = BlenderLocator.Find(BlenderOverride());
        if (blender is null) { status.Report(BlenderGate.NotFound); return; }
        var script = _bridgeScriptPath;
        if (!File.Exists(script))
        {
            status.Report("The Blender script is missing from the app folder. Reinstall Doll Remolding Lab.");
            return;
        }

        var requestedPart = requested is null ? null : SubjectPartOf(requested);
        if (requested is not null && requestedPart is null)
        { status.Report($"Couldn't find {requested.RendererSlot}'s mesh."); return; }
        // The mesh-edit gate, at the one place sessions are written: a part whose game mesh cannot be
        // replaced never opens writable, so nothing a send returns can land geometry on it.
        if (requestedPart is not null
            && await Task.Run(() => MeshEditBlockFor(vfs, requestedPart)) is { } meshBlock)
        { status.Report(meshBlock); return; }
        var displayed = withReferences || openAll
            ? model.Parts.ToList()
            : new List<SubjectPart> { requestedPart! };
        var writableEdits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var writableStockParts = new List<SubjectPart>();
        HashSet<string>? eligible = null;
        if (requestedPart is not null)
        {
            if (requestedEditId is null) writableStockParts.Add(requestedPart);
            else writableEdits[requestedPart.SlotName] = requestedEditId;
        }
        // Open-all is the several-parts-at-once session: every displayed part the static rule and the
        // mesh-edit gate admit opens WRITABLE. Parts without an addressed launch edit expose the same
        // send-time destination inventory as the rest; an absent legacy selection retains the old
        // create-on-return behavior. Statics and gate-blocked parts open read-only.
        if (openAll)
        {
            eligible = await Task.Run(() => displayed
                .Where(part => !part.IsStatic)
                .Where(part => MeshEditBlockFor(vfs, part) is null)
                .Select(part => part.SlotName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase));
            foreach (var part in displayed.Where(part => eligible.Contains(part.SlotName)))
            {
                string? editId = openAllMode == SessionBlenderOpenAll.FirstEdit
                    ? ActiveOrFirstContentEdit(snapshot, SessionTarget(subject, outfit, part)) : null;
                if (editId is null) writableStockParts.Add(part);
                else writableEdits[part.SlotName] = editId;
            }
        }
        if (writableEdits.Count == 0 && writableStockParts.Count == 0)
        {
            status.Report(openAll
                ? "No part of this item can be edited in Blender."
                : "This part cannot be sent back from Blender.");
            return;
        }

        string runDir = Path.Combine(snapshot.RootDir, ProjectAssetIngress.DirectoryName, "blender",
            Guid.NewGuid().ToString("N"));
        string partsDir = Path.Combine(runDir, "parts");
        string mapsDir = Path.Combine(runDir, "textures");
        var allSpecs = new List<(string Part, string SourceBundle, string MeshName, string? GlbOut,
            IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)>();
        var pending = new List<(TargetPart Target, SubjectPart Model, RecipePart Recipe,
            SessionPartPlan Plan)>();
        foreach (var part in model.Parts)
        {
            var recipe = part.ToRecipePart();
            string? bundle = recipe.MeshBundle ?? (recipe.MeshAddress.Length == 0
                ? null : vfs.Catalog.ResolveAddress(recipe.MeshAddress));
            if (bundle is null) continue;
            var target = SessionTarget(subject, outfit, part);
            string? sourceEdit = SessionBlenderSourceEdit(snapshot, target, requested, requestedEditId,
                openAllMode == SessionBlenderOpenAll.FirstEdit
                && eligible?.Contains(part.SlotName) == true);
            var geometry = sourceEdit is null ? (Path: (string?)null, Missing: (string?)null)
                : GeometryFile(session.Slots(sourceEdit), snapshot.RootDir);
            if (geometry.Missing is not null)
            { status.Report(GeometryFileMissing(geometry.Missing)); return; }
            bool show = displayed.Contains(part);
            string rigged = Path.Combine(partsDir, StorageName(part.SlotName) + ".rigged.glb");
            string prepared = Path.Combine(partsDir, StorageName(part.SlotName) + ".glb");
            var maps = sourceEdit is null ? null
                : SessionAuthoredMaps(session.Slots(sourceEdit), snapshot.RootDir);
            var textureMaps = sourceEdit is null ? null
                : SessionAuthoredTextures(session.Slots(sourceEdit), snapshot.RootDir);
            // NULL edited glb, always: this call builds the part's STOCK rigged glb, which is the map record
            // every prepared file below is classified against and the armature this run offers. Two gates
            // already keep the build's edited branch off it — a part with no GlbOut is skeleton-only and
            // stops before that branch, and the branch itself requires a null GlbOut — but a spec naming the
            // edit is a loaded gun: were either gate to move, the build would write the EDIT into
            // <part>.rigged.glb and the stock record `recordGlb: rigged` depends on would be gone.
            allSpecs.Add((part.Token, bundle, recipe.SlotName, show ? rigged : null,
                null, recipe.MeshPathId, null));
            if (show)
                pending.Add((target, part, recipe, new SessionPartPlan(part.Token,
                    recipe.SlotName, rigged, prepared, part.IsStatic, maps, geometry.Path, textureMaps)));
        }
        if (pending.Count == 0) { status.Report("No parts to open."); return; }
        var plans = pending.Select(item => item.Plan).ToList();
        var modelSlotNames = model.Parts.Select(part => part.SlotName).ToList();
        var displayedSlotNames = displayed.Select(part => part.SlotName).ToList();
        bool stockCombinedCandidate = StockCombinedGeometryCandidate(modelSlotNames, displayedSlotNames, plans);
        bool authoredReferenceCandidate = AuthoredReferenceCompositionCandidate(withReferences, openAll,
            requestedPart?.SlotName, modelSlotNames, displayedSlotNames, plans);
        SessionPartPlan? referenceTargetPlan = requestedPart is null ? null : plans
            .Where(plan => string.Equals(plan.SlotName, requestedPart.SlotName,
                StringComparison.OrdinalIgnoreCase))
            .Select(plan => (SessionPartPlan?)plan).SingleOrDefault();
        bool earlyCombinedRestore = withReferences && !openAll && referenceTargetPlan is not null
            && (stockCombinedCandidate || authoredReferenceCandidate);

        Phase("preflight (gates + plan walk)");
        using var preparing = BeginSessionBlenderPrepare(pending.Count > 1);
        status.Report(pending.Count == 1 ? "Preparing the part for Blender…" : "Preparing the parts for Blender…");
        // The parts whose OWN edited geometry could not be read; nothing is opened while it holds anything.
        IReadOnlyList<string> unreadableEdits = Array.Empty<string>();
        // Filled by the preparation pass itself, after it successfully writes a prepared file from inputs
        // with no authored geometry or maps. The combined diagnostics consume this observed provenance;
        // the composition call site does not certify planned paths as game-side merely by naming them.
        var gameSidePrepared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewMemo = new PreviewBlobMemo();
        // The tokens the COMBINED build had to take from the game because it could not assemble from the file
        // this open prepared. It rides that call and not the per-part one, which is handed no edit at all.
        var fallbacks = new List<string>();
        // Every part's own maps by material position, for the combined build — the lone route embeds them
        // through the re-export below, and without this the several-parts session would show the game
        // texture under work the modder already sent back.
        var authoredMaps =
            new Dictionary<string, IReadOnlyList<(string? Base, string? Normal, string? Rmo)>>(
                StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
            if (plan.Maps is not null) authoredMaps[plan.Token] = plan.Maps;
        var authoredTextureMaps = new Dictionary<string, IReadOnlyList<TextureTransportOverride>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
            if (plan.TextureMaps is not null) authoredTextureMaps[plan.Token] = plan.TextureMaps;
        // A set, not a list: the per-part build and the combined one walk the same parts, so a texture neither
        // could read would otherwise be counted twice on the line the modder reads.
        var unreadableTextures = new HashSet<string>(StringComparer.Ordinal);
        // The candidacy roster both builds filter their appended bone tail against. Assembled ONCE, on the
        // prepare worker, and shared: it spans the SUBJECT rather than the parts either call writes a glb
        // for, so the per-part build and the composition judge against one answer by construction. It is
        // assembled off THIS open's own vfs and game dir, never the live fields — see ExportRoster.
        AssetExporter.SubjectRoster? roster = null;
        // …and whether the wardrobe tables behind it would read at all, which narrows every modular
        // subject's offer to its own posed bones and is otherwise invisible.
        bool wardrobeUnreadable = false;
        // The same cache objects and identity span the per-part and stock-combined generations. They are
        // minted on the prepare worker and carried out only after that worker has settled.
        RiggedGlbCache? sessionRigCache = null;
        RiggedGlbCache.Identity? sessionRigIdentity = null;
        StockTextureCache? sessionStockTextures = null;
        // What the per-part restore proved this open (meaningful only on its hit), and what a cold build
        // produced for the deferred publication after Blender is on its way.
        bool partsRestored = false;
        var validatedStock = new Dictionary<string, RiggedGlbCache.StockTexture>(StringComparer.OrdinalIgnoreCase);
        AssetExporter.RiggedBuildDiagnostics? coldPartsDiagnostics = null;
        IReadOnlyList<string>? coldPartsBuilt = null;
        RiggedGlbCache.ServeDependencies? preparedDependencies = null;
        IReadOnlyDictionary<string, string> preparedKeys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var restoredPrepared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool skipStaticPrepare = plans.Count > 1;
        long earlyPreparationMilliseconds = 0;
        string? restoredCombined = null;
        string? authoredCombinedKey = null;
        try
        {
            await Task.Run(() =>
            {
                (roster, wardrobeUnreadable) = ExportRoster(vfs, gameDir, model);
                Phase("roster + wardrobe read");
                var cacheRoot = CacheRootFor();
                var stockTextureRoot = LabPaths.StockTextureRootIn(cacheRoot);
                try
                {
                    sessionRigCache = RiggedGlbCacheAt(LabPaths.RiggedGlbRootIn(cacheRoot));
                    sessionRigIdentity = SessionRiggedCacheIdentity(vfs, outfitModel, subject, roster, allSpecs,
                        wardrobeUnreadable);
                    sessionStockTextures = new StockTextureCache(stockTextureRoot);
                }
                catch { /* an unavailable cache falls through to the exporter */ }

                RiggedGlbCache.ServeDependencies servedDependencies = default;
                if (!wardrobeUnreadable && sessionRigCache is not null
                    && sessionRigIdentity is { } identity && sessionStockTextures is not null)
                    partsRestored = TryRestoreSessionRiggedParts(sessionRigCache, identity, vfs,
                        sessionStockTextures, plans, runDir, out servedDependencies, validatedStock,
                        (label, ms) => TimeMark("· " + label, ms));
                if (partsRestored) preparedDependencies = servedDependencies;
                if (!partsRestored) validatedStock.Clear();
                Phase(partsRestored ? "parts restore (cache hit)" : "parts restore attempt (miss)");

                if (!partsRestored)
                {
                    Directory.CreateDirectory(partsDir);
                    var diagnostics = new AssetExporter.RiggedBuildDiagnostics();
                    coldPartsBuilt = AssetExporter.BuildRiggedGlbs(gameDir, vfs, outfitModel, subject, allSpecs,
                        mapsDir, status, roster: roster,
                        candidacyCacheFile: LabPaths.CandidacyCacheFileIn(cacheRoot),
                        stockTextureCacheRoot: stockTextureRoot, unreadableTextures: unreadableTextures,
                        reportBlenderTexCoordWarnings: true, diagnostics: diagnostics,
                        previewMemo: previewMemo);
                    coldPartsDiagnostics = diagnostics;
                    if (diagnostics.Completed && diagnostics.GameSideOnly && !diagnostics.ProducedComposition
                        && !diagnostics.HadTransientFailures && !diagnostics.WasCanceled
                        && !diagnostics.HadProjectAuthoredContent && sessionStockTextures is not null
                        && TryDescribeRiggedBuildDependencies(vfs, sessionStockTextures, diagnostics,
                            out var builtDependencies))
                        preparedDependencies = builtDependencies;
                    Phase("parts build (cold)");
                }
                var cacheablePlans = plans.Where(plan => !skipStaticPrepare || !plan.Static).ToList();
                var initialPlans = earlyCombinedRestore
                    ? cacheablePlans.Where(plan => string.Equals(plan.SlotName,
                        referenceTargetPlan!.Value.SlotName, StringComparison.OrdinalIgnoreCase)).ToList()
                    : cacheablePlans;
                var prepareWatch = earlyCombinedRestore ? Stopwatch.StartNew() : null;
                if (!wardrobeUnreadable && sessionRigCache is not null
                    && sessionRigIdentity is { } preparedIdentity)
                {
                    preparedKeys = PreparedSessionPartKeys(preparedIdentity, cacheablePlans);
                    restoredPrepared.UnionWith(TryRestoreSessionPreparedParts(sessionRigCache,
                        preparedIdentity, vfs, initialPlans, preparedKeys, gameSidePrepared));
                }
                var prepareMisses = initialPlans
                    .Where(plan => !restoredPrepared.Contains(plan.Prepared)).ToList();
                unreadableEdits = PrepareSessionParts(prepareMisses, gameSidePrepared,
                    previewMemo: previewMemo);
                if (prepareWatch is null) Phase("prepare workspace files");
                else earlyPreparationMilliseconds = prepareWatch.ElapsedMilliseconds;
            });
        }
        catch (IOException) { status.Report("The game is using these files. Close the game and try again."); return; }
        catch (Exception e)
        { status.Report($"Couldn't prepare the Blender file: {Reason(e)}"); return; }
        // Store the cold build's products off the critical path: Blender should not wait on cache copies.
        if (coldPartsDiagnostics is { } partsDiagnostics && coldPartsBuilt is { } partsBuilt
            && !wardrobeUnreadable && sessionRigCache is not null
            && sessionRigIdentity is { } storeIdentity && sessionStockTextures is not null)
            DeferRiggedGlbPublication($"parts publish ({subject}/{outfit})",
                () => PublishSessionRiggedParts(sessionRigCache, storeIdentity, vfs, sessionStockTextures,
                    partsDiagnostics, plans, partsBuilt));
        if (earlyCombinedRestore)
        {
            if (EditRefusal(unreadableEdits) is { } earlyUnreadable)
            { status.Report(earlyUnreadable); return; }
            var combinedMarks = new List<(string Label, long Milliseconds)>();
            var combinedWatch = Stopwatch.StartNew();
            if (authoredReferenceCandidate && referenceTargetPlan is { } authoredTarget
                && sessionRigIdentity is { } keyIdentity)
                authoredCombinedKey = AuthoredCombinedArtifactKey(keyIdentity, authoredTarget, plans);
            if (!wardrobeUnreadable && sessionRigCache is not null
                && sessionRigIdentity is { } earlyIdentity)
            {
                if (stockCombinedCandidate && sessionStockTextures is not null)
                    restoredCombined = await Task.Run(() => TryRestoreSessionStockCombined(sessionRigCache,
                        earlyIdentity, vfs, sessionStockTextures, plans, runDir,
                        partsRestored ? validatedStock : null,
                        (label, ms) => combinedMarks.Add((label, ms))));
                else if (authoredCombinedKey is { } finalKey)
                    restoredCombined = await Task.Run(() => TryRestoreSessionAuthoredCombined(sessionRigCache,
                        earlyIdentity, vfs, finalKey, runDir));
            }
            long combinedRestoreMilliseconds = combinedWatch.ElapsedMilliseconds;

            if (restoredCombined is null)
            {
                var remainingWatch = Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    var remaining = plans.Where(plan => !skipStaticPrepare || !plan.Static)
                        .Where(plan => !string.Equals(plan.SlotName, referenceTargetPlan!.Value.SlotName,
                            StringComparison.OrdinalIgnoreCase)).ToList();
                    if (!wardrobeUnreadable && sessionRigCache is not null
                        && sessionRigIdentity is { } preparedIdentity)
                        restoredPrepared.UnionWith(TryRestoreSessionPreparedParts(sessionRigCache,
                            preparedIdentity, vfs, remaining, preparedKeys, gameSidePrepared));
                    var misses = remaining.Where(plan => !restoredPrepared.Contains(plan.Prepared)).ToList();
                    var unreadableReferences = PrepareSessionParts(misses, gameSidePrepared,
                        previewMemo: previewMemo);
                    if (unreadableReferences.Count > 0)
                        unreadableEdits = unreadableEdits.Concat(unreadableReferences).ToList();
                });
                earlyPreparationMilliseconds += remainingWatch.ElapsedMilliseconds;
            }
            TimeMark("prepare workspace files", earlyPreparationMilliseconds);
            foreach (var mark in combinedMarks) TimeMark("\u00b7 " + mark.Label, mark.Milliseconds);
            TimeMark(restoredCombined is not null
                ? "combined restore (cache hit)" : "combined restore attempt (miss)",
                combinedRestoreMilliseconds);
            openWatch.Restart();
        }
        var preparedToPublish = plans.Where(plan => (!skipStaticPrepare || !plan.Static)
                && !restoredPrepared.Contains(plan.Prepared)
                && File.Exists(plan.Prepared)
                && !unreadableEdits.Contains(plan.Token, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (preparedToPublish.Count > 0 && preparedDependencies is { } storeDependencies
            && sessionRigCache is not null && sessionRigIdentity is { } preparedStoreIdentity)
            DeferRiggedGlbPublication($"prepared parts publish ({subject}/{outfit})",
                () => PublishSessionPreparedParts(sessionRigCache, preparedStoreIdentity, storeDependencies,
                    preparedToPublish, preparedKeys));
        if (EditRefusal(unreadableEdits) is { } unreadable) { status.Report(unreadable); return; }

        var preparedParts = pending.Select(item => new SessionBlenderPart(item.Target, item.Model,
            item.Recipe, item.Plan.Prepared, item.Plan.Maps,
            item.Plan.EditedGlb is not null)).ToList();
        var writes = new List<SessionBlenderWrite>();
        try
        {
            foreach (var part in preparedParts.Where(part =>
                         writableEdits.ContainsKey(part.Model.SlotName)))
            {
                string editId = writableEdits[part.Model.SlotName];
                var geometry = SessionGeometrySlot(session, editId);
                var ingress = ProjectAssetIngress.Begin(snapshot, editId, geometry.Slot.Id, part.PreparedGlb);
                writes.Add(new SessionBlenderWrite(part, editId, geometry, ingress));
            }
        }
        catch (Exception e)
        {
            // Everything this loop can fail on is the model's own account of itself — an edit with no
            // geometry slot, an ingress that cannot be opened on one — and every word of it names an
            // identity the model keeps for itself. The line reports the open; the reason goes to the log.
            AppLog.Write("Couldn't prepare this edit for Blender", e);
            status.Report(e is AuthoredRefusalException
                ? $"Couldn't prepare this edit for Blender: {Reason(e)}"
                : "Couldn't prepare this edit for Blender.");
            return;
        }

        string opened;
        // The parts the composition could not take at all — named on the open's own line below, since the
        // session still opens on the ones that landed. Empty on the lone route, which composes nothing.
        IReadOnlyList<string> absentFromScene = Array.Empty<string>();
        // The single-part shortcut opens an exact write's outbound snapshot or the stock route's prepared
        // file. Both are immutable run artifacts; neither needs a one-part trip through the combined builder.
        if (preparedParts.Count == 1)
            opened = writes.Count == 1
                ? writes.Single().Ingress.OutboundSnapshot : preparedParts.Single().PreparedGlb;
        else
        {
            if (!earlyCombinedRestore && stockCombinedCandidate && !wardrobeUnreadable
                && sessionRigCache is not null
                && sessionRigIdentity is { } combinedIdentity && sessionStockTextures is not null)
            {
                restoredCombined = await Task.Run(() => TryRestoreSessionStockCombined(sessionRigCache,
                    combinedIdentity, vfs, sessionStockTextures, plans, runDir,
                    partsRestored ? validatedStock : null,
                    (label, ms) => TimeMark("· " + label, ms)));
                Phase(restoredCombined is not null
                    ? "combined restore (cache hit)" : "combined restore attempt (miss)");
            }

            if (restoredCombined is not null)
            {
                opened = restoredCombined;
                absentFromScene = Array.Empty<string>();
            }
            else
            {
                opened = Path.Combine(runDir, "composition.glb");
                var combinedSpecs = preparedParts.Select(part =>
                    (part.Model.Token,
                        part.Recipe.MeshBundle ?? vfs.Catalog.ResolveAddress(part.Recipe.MeshAddress) ?? "",
                        part.Recipe.SlotName, (string?)null, (IReadOnlyList<float>?)null,
                        part.Recipe.MeshPathId,
                        (string?)(part.Model.IsStatic ? null : part.PreparedGlb))).ToList();
                // What the build actually composed, its own answer rather than ours: the per-part isolation
                // inside it drops a part whose read faults, and the list it returns is the one place that shows.
                IReadOnlyList<string> composed = Array.Empty<string>();
                var combinedDiagnostics = new AssetExporter.RiggedBuildDiagnostics();
                try
                {
                    await Task.Run(() => composed = AssetExporter.BuildRiggedGlbs(gameDir, vfs, outfitModel,
                        subject, combinedSpecs, mapsDir, status, combinedOut: opened,
                        vanillaFallbacks: fallbacks, roster: roster,
                        candidacyCacheFile: LabPaths.CandidacyCacheFileIn(CacheRootFor()),
                        authoredMaps: authoredMaps,
                        stockTextureCacheRoot: LabPaths.StockTextureRootIn(CacheRootFor()),
                        unreadableTextures: unreadableTextures, authoredTextureMaps: authoredTextureMaps,
                        diagnostics: combinedDiagnostics,
                        observedGameSidePreparedGlbs: stockCombinedCandidate ? gameSidePrepared : null,
                        previewMemo: previewMemo));
                }
                catch (IOException)
                { status.Report("The game is using these files. Close the game and try again."); return; }
                catch (Exception e)
                { status.Report($"Couldn't build the combined Blender file: {Reason(e)}"); return; }
                Phase("combined build (cold)");
                if (!File.Exists(opened))
                { status.Report("No parts could be opened together for this item."); return; }
                // Every part joins the composition through its PREPARED file; one that could not be assembled
                // from opens on the game copy instead. For a bare part that IS the game copy either way, but a
                // part carrying mesh work would open as the game's under the edit's name — refuse instead.
                if (EditRefusal(EditsLostToTheComposition(plans, fallbacks)) is { } lost)
                { status.Report(lost); return; }
                absentFromScene = PartsMissingFromComposition(plans, composed);
                if (stockCombinedCandidate && !wardrobeUnreadable && sessionRigCache is not null
                    && sessionRigIdentity is { } publishIdentity && sessionStockTextures is not null)
                    DeferRiggedGlbPublication($"combined publish ({subject}/{outfit})",
                        () => PublishSessionStockCombined(sessionRigCache, publishIdentity, vfs,
                            sessionStockTextures, combinedDiagnostics, plans, composed, opened));
                else if (authoredReferenceCandidate && authoredCombinedKey is { } finalKey
                    && referenceTargetPlan is { } authoredTarget && !wardrobeUnreadable
                    && sessionRigCache is not null && sessionRigIdentity is { } authoredIdentity)
                    DeferRiggedGlbPublication($"authored combined publish ({subject}/{outfit})",
                        () => PublishSessionAuthoredCombined(sessionRigCache, authoredIdentity, vfs,
                            combinedDiagnostics, plans, authoredTarget, composed, opened, finalKey));
            }
        }

        // A stock-part route is a write offer that has no exact launch ingress. Its Workspace is the
        // prepared glb the session handed Blender; the send target chooses its edit at intake.
        var stockWriteNames = writableStockParts.Select(part => part.SlotName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sessionParts = preparedParts.Select(part =>
        {
            var write = writes.FirstOrDefault(candidate => string.Equals(candidate.Part.Model.SlotName,
                part.Model.SlotName, StringComparison.OrdinalIgnoreCase));
            bool writable = write is not null || stockWriteNames.Contains(part.Model.SlotName);
            // Edited means "a send would replace mesh work the app already holds" — the workspace file the
            // part opened FROM, never the mere existence of an edit. A maps-only edit therefore addresses
            // that edit without claiming it already holds mesh work.
            bool viewportVisible = SessionBlenderViewportVisible(snapshot, part.Target,
                withReferences || openAll);
            return SessionPartForBlender(part.Recipe.SlotName, part.OpenedFromMeshEdit, writable,
                part.Model.IsStatic, write?.EditId,
                writable ? BlenderSessionEdits(snapshot, part.Target) : null,
                writable ? AuthoredEditSession.NewEditLabel(snapshot, part.Target, null) : null,
                viewportVisible ? null : false,
                label: part.Model.Token);
        }).ToList();
        var targets = writes.Select(write => new BlenderSessionTarget(write.Part.Recipe.SlotName,
                write.Ingress.SourceProjectAssetId ?? "", write.Part.PreparedGlb, write.EditId,
                write.Geometry.Slot.Id, write.Ingress.ReturnArtifact, write.Geometry.Binding.Kind,
                BlenderMaterialBaselines(session.Slots(write.EditId)), Subject: subject, Outfit: outfit))
            .Concat(preparedParts.Where(part => stockWriteNames.Contains(part.Model.SlotName))
                .Select(part => new BlenderSessionTarget(part.Recipe.SlotName, "", part.PreparedGlb,
                    Subject: subject, Outfit: outfit)))
            .ToList();
        var notices = BlenderOpenNotices(absentFromScene, wardrobeUnreadable, unreadableTextures);
        try
        {
            BlenderBridge.WriteSession(opened, requested is null ? null : requestedPart!.SlotName,
                sessionParts, AssetExporter.SessionSendGlbName, targets, notices);
            EnsureWatcher();
            WatchBlenderExit(BlenderBridge.Launch(blender, script, opened, Path.GetDirectoryName(opened)!), status);
            if (notices.Count > 0) status.Report(string.Join(" ", notices));
        }
        catch (Exception e) { status.Report($"Couldn't start Blender: {Reason(e)}"); }
        Phase("bridge write + Blender launch");
        TimeMark("TOTAL", totalWatch.ElapsedMilliseconds);
        BlenderOpenTiming.WriteBlock(
            $"{subject}/{outfit} parts={pending.Count} "
            + (openAll ? "open-all" : withReferences ? "with-references" : "single"),
            openTiming);
    }

    /// <summary>One displayed part of a Blender open, as everything after the pending walk reads it: which
    /// files this run built and is about to build for it, and whether it opens on an edit of the modder's.
    /// The open's own loop is the ONE place these are decided; the passes below take them as data, so the
    /// wiring between the decision and the prepare runs in a test rather than only in a loaded install.
    ///
    /// <para><see cref="Static"/> is the slot's renderer class, which is what says whether the part can join
    /// a combined rigged session at all — a plain MeshRenderer slot is not posed and never reaches the
    /// shared armature, so its absence from a composition is that composition's shape and not a shortfall
    /// (see <see cref="PartsMissingFromComposition"/>).</para></summary>
    internal readonly record struct SessionPartPlan(string Token, string SlotName, string Rigged,
        string Prepared, bool Static,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? Maps, string? EditedGlb,
        IReadOnlyList<TextureTransportOverride>? TextureMaps = null);

    internal static SessionPart SessionPartForBlender(string slotName, bool edited, bool writable,
        bool unskinned, string? editId, IReadOnlyList<BlenderSessionEdit>? edits,
        string? defaultEditName, bool? viewportVisible, string? label = null) =>
        new(slotName, Edited: edited, Writable: writable, Unskinned: unskinned, EditId: editId,
            Edits: edits, DefaultEditName: defaultEditName, ViewportVisible: viewportVisible,
            Label: label);

    private const string StockCombinedArtifactPrefix = "\u0001stock-combined-v1:";
    internal const string PreparedPartSpecVersion = "prepared-part-workspace-v1";
    private const string PreparedPartArtifactPrefix = "\u0001prepared-part-v1:";
    private const string AuthoredCombinedArtifactPrefix = "\u0001authored-combined-v1:";

    /// <summary>Content identity of one prepared workspace. Paths, edit ids and timestamps are deliberately
    /// absent: the stock offer, authored geometry, ordered bindings and every authored picture enter by
    /// identity/content instead.</summary>
    internal static string? PreparedPartArtifactKey(RiggedGlbCache.Identity identity, SessionPartPlan plan,
        string preparedSpecVersion = PreparedPartSpecVersion)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var length = new byte[4];
            var fileHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Text(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
            string FileHash(string path)
            {
                string full = Path.GetFullPath(path);
                if (fileHashes.TryGetValue(full, out string? held)) return held;
                using var stream = File.Open(full, FileMode.Open, FileAccess.Read, FileShare.Read);
                held = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                fileHashes.Add(full, held);
                return held;
            }
            void OptionalFile(string? path)
            {
                Text(path is null ? "absent" : "present");
                if (path is not null) Text(FileHash(path));
            }

            Text(preparedSpecVersion);
            Text(identity.CatalogVersion);
            Text(identity.SubjectFingerprint);
            Text(identity.RosterSpecFingerprint);
            Text(plan.Token);
            Text(plan.SlotName);
            Text(plan.Static ? "static" : "skinned");
            Text(FileHash(plan.Rigged));
            OptionalFile(plan.EditedGlb);

            Text(plan.Maps is null ? "legacy-maps-absent" : "legacy-maps-present");
            if (plan.Maps is not null)
            {
                Text(plan.Maps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var row in plan.Maps)
                {
                    OptionalFile(row.Base);
                    OptionalFile(row.Normal);
                    OptionalFile(row.Rmo);
                }
            }

            Text(plan.TextureMaps is null ? "property-maps-absent" : "property-maps-present");
            if (plan.TextureMaps is not null)
            {
                Text(plan.TextureMaps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var row in plan.TextureMaps)
                {
                    Text(row.MaterialIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Text(row.ShaderProperty);
                    Text(row.Kind?.ToString() ?? "kind-absent");
                    Text(FileHash(row.Png));
                }
            }
            return PreparedPartArtifactPrefix
                + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>Whether these inputs describe the one full stock geometry scene. Command mode and
    /// writability deliberately do not enter: open-all and a stock target with stock references share the
    /// same combined artifact, while the session document says which rows may return.</summary>
    internal static bool StockCombinedGeometryCandidate(IReadOnlyCollection<string> modelSlots,
        IReadOnlyCollection<string> displayedSlots, IReadOnlyList<SessionPartPlan> plans)
    {
        if (!StockPlans(plans) || plans.Count(plan => !plan.Static) < 2
            || modelSlots.Count != displayedSlots.Count || displayedSlots.Count != plans.Count)
            return false;
        var model = modelSlots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displayed = displayedSlots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = plans.Select(plan => plan.SlotName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return model.Count == modelSlots.Count && displayed.Count == displayedSlots.Count
            && planned.Count == plans.Count && model.SetEquals(displayed) && displayed.SetEquals(planned);
    }

    internal static bool AuthoredReferenceCompositionCandidate(bool withReferences, bool openAll,
        string? targetSlot, IReadOnlyCollection<string> modelSlots,
        IReadOnlyCollection<string> displayedSlots, IReadOnlyList<SessionPartPlan> plans)
    {
        if (!withReferences || openAll || string.IsNullOrWhiteSpace(targetSlot)
            || plans.Count(plan => !plan.Static) < 2
            || modelSlots.Count != displayedSlots.Count || displayedSlots.Count != plans.Count)
            return false;
        var model = modelSlots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var displayed = displayedSlots.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = plans.Select(plan => plan.SlotName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (model.Count != modelSlots.Count || displayed.Count != displayedSlots.Count
            || planned.Count != plans.Count || !model.SetEquals(displayed) || !displayed.SetEquals(planned))
            return false;
        var target = plans.Where(plan => string.Equals(plan.SlotName, targetSlot,
            StringComparison.OrdinalIgnoreCase)).ToList();
        return target.Count == 1 && !target[0].Static && !IsStockPlan(target[0])
            && plans.Where(plan => !string.Equals(plan.SlotName, targetSlot,
                    StringComparison.OrdinalIgnoreCase)).All(IsStockPlan);
    }

    private static bool StockPlans(IReadOnlyList<SessionPartPlan> plans) => plans.All(plan =>
        IsStockPlan(plan));

    private static bool IsStockPlan(SessionPartPlan plan) =>
        plan.EditedGlb is null && plan.Maps is null && plan.TextureMaps is null;

    internal static string? AuthoredCombinedArtifactKey(RiggedGlbCache.Identity referenceIdentity,
        SessionPartPlan target, IReadOnlyList<SessionPartPlan> orderedParts,
        string combinedWriterSpec = MeshGltf.CombinedWriterSpec)
    {
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var length = new byte[4];
            void Text(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
            Text("authored-reference-composition-v1");
            Text(combinedWriterSpec);
            Text(AssetExporter.RiggedBuildSpec);
            // The references ride into this artifact as their PREPARED shapes, and RiggedBuildSpec
            // guards only the rig builder — the prepared spec is what moves when ReexportPartGlb,
            // texture transport or sidecar semantics change, so it invalidates this key too.
            Text(PreparedPartSpecVersion);
            Text(referenceIdentity.CatalogVersion);
            Text(referenceIdentity.SubjectFingerprint);
            Text(referenceIdentity.RosterSpecFingerprint);
            Text(StockCombinedArtifactKey(orderedParts));
            Text(target.Token);
            Text(target.SlotName);
            if (PreviewMaps.WorkspaceContentIdentity(target.Prepared) is not { } preparedIdentity)
                return null;
            Text(preparedIdentity);
            foreach (var part in orderedParts)
            {
                Text(part.Token);
                Text(part.SlotName);
                Text(part.Static ? "static" : "skinned");
            }
            return AuthoredCombinedArtifactPrefix
                + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>Exact success set required before a cold composition may become a reusable entry. Static
    /// renderer rows never join the shared armature and therefore are deliberately outside the expected set.</summary>
    internal static bool StockCombinedCompositionMatches(IReadOnlyList<SessionPartPlan> plans,
        IReadOnlyCollection<string> composed)
    {
        var expected = plans.Where(plan => !plan.Static).Select(plan => plan.Token).ToList();
        var expectedSet = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var composedSet = composed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expected.Count >= 2 && expectedSet.Count == expected.Count
            && composedSet.Count == composed.Count && expected.Count == composed.Count
            && expectedSet.SetEquals(composedSet);
    }

    /// <summary>Names the combined member by its exact posed token/slot sequence. The surrounding cache
    /// identity already keys the full visible spec; this member key independently prevents a different
    /// composed set from aliasing it if that identity shape ever broadens.</summary>
    internal static string StockCombinedArtifactKey(IReadOnlyList<SessionPartPlan> plans)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var length = new byte[4];
        void Text(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
        Text("stock-combined-artifact-v1");
        foreach (var plan in plans.Where(plan => !plan.Static))
        {
            Text(plan.Token);
            Text(plan.SlotName);
        }
        return StockCombinedArtifactPrefix
            + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Restore one combined member outside the already-complete run, revalidate its PNG
    /// dependencies, then atomically attach the whole GLB/sidecar directory. Any discrepancy is a quiet miss
    /// and leaves the existing per-part run untouched. Like the per-part restore, a warm serve reads no
    /// game file — currency is the manifest's catalog/content-identity check.
    ///
    /// <para><paramref name="alreadyValidatedStock"/> is the per-part restore's validation work from THIS
    /// open (only a hit's collector qualifies): a map it placed and content-checked into this run is trusted
    /// on an exact dependency match plus presence.</para></summary>
    internal static string? TryRestoreSessionStockCombined(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, GameVfs vfs, StockTextureCache stockTextures,
        IReadOnlyList<SessionPartPlan> plans, string runDir,
        IReadOnlyDictionary<string, RiggedGlbCache.StockTexture>? alreadyValidatedStock = null,
        Action<string, long>? timing = null)
    {
        if (!StockPlans(plans)) return null;
        string scratch = runDir + ".combined-rigcache." + Guid.NewGuid().ToString("N") + ".tmp";
        string committed = Path.Combine(runDir, "stock-combined");
        var watch = timing is null ? null : Stopwatch.StartNew();
        void Mark(string label)
        {
            if (watch is null) return;
            timing!(label, watch.ElapsedMilliseconds);
            watch.Restart();
        }
        try
        {
            if (Directory.Exists(committed) || File.Exists(committed)) return null;
            IReadOnlyDictionary<string, string> current;
            try
            {
                current = BundleReads.CurrentKeys(vfs.Catalog, BundleReads.ContentHashLookup(vfs.Manifest));
            }
            catch { return null; }
            if (!cache.TryServe(identity, current,
                    new[] { new RiggedGlbCache.Request(StockCombinedArtifactKey(plans), "composition.glb") },
                    scratch, out var dependencies)
                || dependencies.RequiredBundleReads.Count == 0)
                return null;
            Mark("combined-serve");

            int hashed = 0;
            foreach (var dependency in dependencies.StockTextures)
            {
                string runTexture = Path.Combine(runDir, "textures", dependency.DestinationFileName);
                if (alreadyValidatedStock is not null
                    && alreadyValidatedStock.TryGetValue(dependency.DestinationFileName, out var validated)
                    && validated == dependency)
                {
                    if (!File.Exists(runTexture)) return null;
                    continue;
                }
                hashed++;
                var cached = stockTextures.TryGet(dependency.BundleContentId, dependency.TextureName,
                    dependency.PathId);
                if (cached is null || !RiggedGlbCache.MatchesStockTexture(cached, dependency))
                {
                    stockTextures.Invalidate(dependency.BundleContentId, dependency.TextureName,
                        dependency.PathId);
                    return null;
                }
                if (!RiggedGlbCache.MatchesStockTexture(runTexture, dependency)) return null;
            }
            Mark($"combined-textures ({hashed} of {dependencies.StockTextures.Count} maps hashed)");

            string staged = Path.Combine(scratch, "composition.glb");
            if (!File.Exists(staged)) return null;
            Directory.Move(scratch, committed);
            return Path.Combine(committed, "composition.glb");
        }
        catch { return null; }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            catch { /* throwaway serve staging; the full composition build follows */ }
        }
    }

    internal static string? TryRestoreSessionAuthoredCombined(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, GameVfs vfs, string artifactKey, string runDir)
    {
        try
        {
            var current = BundleReads.CurrentKeys(vfs.Catalog,
                BundleReads.ContentHashLookup(vfs.Manifest));
            string destination = Path.Combine(runDir, "authored-combined", "composition.glb");
            return cache.TryServePrepared(identity, current, artifactKey, destination)
                ? destination : null;
        }
        catch { return null; }
    }

    /// <summary>Best-effort publication of an exact, completed stock composition. Every purity bit and
    /// dependency comes from this composition build's diagnostics; the call site supplies no BuildState
    /// constants.</summary>
    internal static bool PublishSessionStockCombined(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, GameVfs vfs, StockTextureCache stockTextures,
        AssetExporter.RiggedBuildDiagnostics diagnostics, IReadOnlyList<SessionPartPlan> plans,
        IReadOnlyCollection<string> composed, string combinedGlb)
    {
        try
        {
            string sidecar = PreviewMaps.SidecarPath(combinedGlb);
            if (!StockPlans(plans) || !StockCombinedCompositionMatches(plans, composed)
                || !diagnostics.Completed || !diagnostics.ProducedComposition || !diagnostics.GameSideOnly
                || diagnostics.HadTransientFailures || diagnostics.WasCanceled
                || diagnostics.HadProjectAuthoredContent || !File.Exists(combinedGlb))
                return false;

            var buildState = new RiggedGlbCache.BuildState(
                GameSideOnly: diagnostics.GameSideOnly,
                HadTransientFailures: diagnostics.HadTransientFailures,
                WasCanceled: diagnostics.WasCanceled,
                HadProjectAuthoredContent: diagnostics.HadProjectAuthoredContent);
            var contentHashOf = BundleReads.ContentHashLookup(vfs.Manifest);
            string reads = BundleReads.Of(vfs.Catalog, contentHashOf, diagnostics.BundleReads);
            if (string.IsNullOrEmpty(reads) || diagnostics.RequiredBundleReads.Count == 0) return false;
            var stock = new List<RiggedGlbCache.StockTexture>();
            foreach (var dependency in diagnostics.StockTextures)
            {
                var cached = stockTextures.TryGet(dependency.BundleContentId, dependency.TextureName,
                    dependency.PathId);
                if (cached is null) return false;
                var described = RiggedGlbCache.DescribeStockTexture(cached, dependency.BundleContentId,
                    dependency.TextureName, dependency.PathId, dependency.DestinationFileName);
                if (described is null) return false;
                stock.Add(described.Value);
            }

            bool stored;
            try
            {
                stored = cache.TryStore(identity, reads,
                    new RiggedGlbCache.Artifact(StockCombinedArtifactKey(plans), combinedGlb,
                        File.Exists(sidecar) ? sidecar : null,
                        diagnostics.RequiredBundleReads, stock), buildState);
            }
            finally { cache.CompleteSubjectPublication(identity); }
            return stored;
        }
        catch { return false; }
    }

    internal static RiggedGlbCache.Identity SessionRiggedCacheIdentity(GameVfs vfs, Outfit outfit,
        string subject, AssetExporter.SubjectRoster? roster,
        IReadOnlyList<(string Part, string SourceBundle, string MeshName, string? GlbOut,
            IReadOnlyList<float>? BakedRest, long PathId, string? EditedGlb)> specs,
        bool wardrobeUnreadable) =>
        new(vfs.CatalogVersion, SubjectFingerprint.For(vfs.Catalog, outfit),
            AssetExporter.RiggedBuildFingerprint(outfit, subject, roster, specs, wardrobeUnreadable));

    /// <summary>Restore one requested per-part route under a throwaway sibling run, rebuild the stock-PNG
    /// folder its sidecars name, then rename the whole run into place. Every discrepancy is a silent cache
    /// miss. A warm restore reads NO game file: the manifest's catalog/content-identity check is what proves
    /// currency, so a fully-served open succeeds even while the game holds the bundles — only work that
    /// genuinely reads a bundle (the cold build) can refuse BUSY.
    ///
    /// <para>On a hit, <paramref name="validatedStock"/> carries the maps this restore placed and
    /// content-checked, so the same open's combined restore never re-hashes one it just proved. It is
    /// meaningful only when this method returned true.</para></summary>
    internal static bool TryRestoreSessionRiggedParts(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, GameVfs vfs, StockTextureCache stockTextures,
        IReadOnlyList<SessionPartPlan> plans, string runDir,
        IDictionary<string, RiggedGlbCache.StockTexture>? validatedStock = null,
        Action<string, long>? timing = null) => TryRestoreSessionRiggedParts(cache, identity, vfs,
            stockTextures, plans, runDir, out _, validatedStock, timing);

    internal static bool TryRestoreSessionRiggedParts(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, GameVfs vfs, StockTextureCache stockTextures,
        IReadOnlyList<SessionPartPlan> plans, string runDir,
        out RiggedGlbCache.ServeDependencies servedDependencies,
        IDictionary<string, RiggedGlbCache.StockTexture>? validatedStock = null,
        Action<string, long>? timing = null)
    {
        servedDependencies = default;
        var staging = runDir + ".rigcache." + Guid.NewGuid().ToString("N") + ".tmp";
        var watch = timing is null ? null : Stopwatch.StartNew();
        void Mark(string label)
        {
            if (watch is null) return;
            timing!(label, watch.ElapsedMilliseconds);
            watch.Restart();
        }
        try
        {
            IReadOnlyDictionary<string, string> current;
            try
            {
                current = BundleReads.CurrentKeys(vfs.Catalog, BundleReads.ContentHashLookup(vfs.Manifest));
            }
            catch { return false; }
            var requests = plans.Where(plan => !plan.Static).Select(plan => new RiggedGlbCache.Request(plan.SlotName,
                Path.GetFileName(plan.Rigged))).ToList();
            if (requests.Count == 0) return false;
            if (!cache.TryServe(identity, current, requests, Path.Combine(staging, "parts"),
                    out var dependencies)
                || dependencies.RequiredBundleReads.Count == 0)
                return false;
            Mark($"parts-serve ({requests.Count} rigs)");

            var texturesDir = Path.Combine(staging, "textures");
            PreviewMaps.WriteNeutrals(texturesDir);
            foreach (var dependency in dependencies.StockTextures)
            {
                var cached = stockTextures.TryGet(dependency.BundleContentId, dependency.TextureName,
                    dependency.PathId);
                if (cached is null)
                {
                    stockTextures.Invalidate(dependency.BundleContentId, dependency.TextureName,
                        dependency.PathId);
                    return false;
                }
                var destination = Path.Combine(texturesDir, dependency.DestinationFileName);
                // One content check per map: the placed file's bytes ARE the cache entry's (a hard link is
                // the same file; a copy carried them just now), so hashing the destination validates both.
                if (!StockTextureCache.Place(cached, destination)) return false;
                if (!RiggedGlbCache.MatchesStockTexture(destination, dependency))
                {
                    stockTextures.Invalidate(dependency.BundleContentId, dependency.TextureName,
                        dependency.PathId);
                    return false;
                }
                if (validatedStock is not null) validatedStock[dependency.DestinationFileName] = dependency;
            }
            Mark($"parts-textures ({dependencies.StockTextures.Count} maps)");

            if (Directory.Exists(runDir) || File.Exists(runDir)) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(runDir))!);
            Directory.Move(staging, runDir);
            servedDependencies = dependencies;
            return true;
        }
        catch { return false; }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { /* throwaway cache staging only; the cold route still writes a fresh run */ }
        }
    }

    /// <summary>Best-effort publication of the per-part products from one observed exporter run. No
    /// composition or prepared file enters this method; a missing durable stock dependency declines the
    /// optimization rather than publishing a rig whose sidecar a later run cannot satisfy.</summary>
    internal static bool PublishSessionRiggedParts(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, GameVfs vfs, StockTextureCache stockTextures,
        AssetExporter.RiggedBuildDiagnostics diagnostics, IReadOnlyList<SessionPartPlan> plans,
        IReadOnlyList<string> completed) =>
        PublishSessionRiggedPartsCore(cache, identity, vfs, stockTextures, diagnostics, plans, completed)
            == RiggedGlbPrewarmOutcome.Ready;

    private static RiggedGlbPrewarmOutcome PublishSessionRiggedPartsForPrewarm(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, GameVfs vfs, StockTextureCache stockTextures,
        AssetExporter.RiggedBuildDiagnostics diagnostics, IReadOnlyList<SessionPartPlan> plans,
        IReadOnlyList<string> completed) =>
        PublishSessionRiggedPartsCore(cache, identity, vfs, stockTextures, diagnostics, plans, completed);

    private static bool TryDescribeRiggedBuildDependencies(GameVfs vfs, StockTextureCache stockTextures,
        AssetExporter.RiggedBuildDiagnostics diagnostics,
        out RiggedGlbCache.ServeDependencies dependencies)
    {
        dependencies = default;
        try
        {
            var reads = BundleReads.Of(vfs.Catalog, BundleReads.ContentHashLookup(vfs.Manifest),
                diagnostics.BundleReads);
            if (string.IsNullOrEmpty(reads)) return false;
            var stock = new List<RiggedGlbCache.StockTexture>();
            foreach (var dependency in diagnostics.StockTextures)
            {
                var cached = stockTextures.TryGet(dependency.BundleContentId, dependency.TextureName,
                    dependency.PathId);
                if (cached is null) return false;
                var described = RiggedGlbCache.DescribeStockTexture(cached, dependency.BundleContentId,
                    dependency.TextureName, dependency.PathId, dependency.DestinationFileName);
                if (described is null) return false;
                stock.Add(described.Value);
            }
            dependencies = new RiggedGlbCache.ServeDependencies(reads,
                diagnostics.RequiredBundleReads, stock);
            return true;
        }
        catch { return false; }
    }

    internal static bool PublishSessionAuthoredCombined(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, GameVfs vfs,
        AssetExporter.RiggedBuildDiagnostics diagnostics, IReadOnlyList<SessionPartPlan> plans,
        SessionPartPlan target, IReadOnlyCollection<string> composed, string combinedGlb,
        string artifactKey)
    {
        try
        {
            if (IsStockPlan(target) || plans.Where(plan => !string.Equals(plan.SlotName, target.SlotName,
                        StringComparison.OrdinalIgnoreCase)).Any(plan => !IsStockPlan(plan))
                || !StockCombinedCompositionMatches(plans, composed)
                || !diagnostics.Completed || !diagnostics.ProducedComposition || diagnostics.GameSideOnly
                || diagnostics.HadTransientFailures || diagnostics.WasCanceled || !File.Exists(combinedGlb)
                || !string.Equals(AuthoredCombinedArtifactKey(identity, target, plans), artifactKey,
                    StringComparison.Ordinal))
                return false;
            string reads = BundleReads.Of(vfs.Catalog, BundleReads.ContentHashLookup(vfs.Manifest),
                diagnostics.BundleReads);
            if (string.IsNullOrEmpty(reads) || diagnostics.RequiredBundleReads.Count == 0) return false;
            bool stored;
            try
            {
                stored = cache.TryStorePrepared(identity, reads,
                    new RiggedGlbCache.PreparedArtifact(artifactKey, combinedGlb));
            }
            finally { cache.CompleteSubjectPublication(identity); }
            return stored;
        }
        catch { return false; }
    }

    internal static IReadOnlyDictionary<string, string> PreparedSessionPartKeys(
        RiggedGlbCache.Identity identity, IReadOnlyList<SessionPartPlan> plans,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PreparedPartArtifactKey(identity, plan) is { } key)
                keys[plan.Prepared] = key;
        }
        return keys;
    }

    /// <summary>Restore prepared parts one at a time. Content-key or payload failure is local to that part,
    /// so an authored target miss cannot throw away stock-reference hits.</summary>
    internal static IReadOnlySet<string> TryRestoreSessionPreparedParts(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, GameVfs vfs, IReadOnlyList<SessionPartPlan> plans,
        IReadOnlyDictionary<string, string> keys, ICollection<string>? gameSidePrepared = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> current;
        try
        {
            current = BundleReads.CurrentKeys(vfs.Catalog, BundleReads.ContentHashLookup(vfs.Manifest));
        }
        catch { return restored; }
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!keys.TryGetValue(plan.Prepared, out string? key)
                || !cache.TryServePrepared(identity, current, key, plan.Prepared))
                continue;
            restored.Add(plan.Prepared);
            if (plan.EditedGlb is null && plan.Maps is null && plan.TextureMaps is null)
                gameSidePrepared?.Add(plan.Prepared);
        }
        return restored;
    }

    internal static bool PublishSessionPreparedParts(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, RiggedGlbCache.ServeDependencies dependencies,
        IReadOnlyList<SessionPartPlan> plans, IReadOnlyDictionary<string, string> keys,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(dependencies.BundleReads)) return false;
        bool stored = true;
        bool offered = false;
        try
        {
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!keys.TryGetValue(plan.Prepared, out string? key) || !File.Exists(plan.Prepared))
                    continue;
                offered = true;
                if (!string.Equals(PreparedPartArtifactKey(identity, plan), key, StringComparison.Ordinal))
                {
                    stored = false;
                    continue;
                }
                stored &= cache.TryStorePrepared(identity, dependencies.BundleReads,
                    new RiggedGlbCache.PreparedArtifact(key, plan.Prepared));
            }
        }
        finally { if (offered) cache.CompleteSubjectPublication(identity); }
        return offered && stored;
    }

    private static RiggedGlbPrewarmOutcome PublishSessionRiggedPartsCore(RiggedGlbCache cache,
        RiggedGlbCache.Identity identity, GameVfs vfs, StockTextureCache stockTextures,
        AssetExporter.RiggedBuildDiagnostics diagnostics, IReadOnlyList<SessionPartPlan> plans,
        IReadOnlyList<string> completed)
    {
        try
        {
            if (!diagnostics.Completed || !diagnostics.GameSideOnly || diagnostics.ProducedComposition
                || diagnostics.HadTransientFailures
                || diagnostics.WasCanceled || diagnostics.HadProjectAuthoredContent)
                return RiggedGlbPrewarmOutcome.Skipped;
            if (string.IsNullOrWhiteSpace(identity.CatalogVersion)
                || string.Equals(identity.CatalogVersion, GameInfo.UnknownVersion, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(identity.SubjectFingerprint)
                || string.IsNullOrWhiteSpace(identity.RosterSpecFingerprint))
                return RiggedGlbPrewarmOutcome.Skipped;
            var completedSet = completed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (plans.Where(plan => !plan.Static)
                .Any(plan => !completedSet.Contains(plan.Token) || !File.Exists(plan.Rigged)))
                return RiggedGlbPrewarmOutcome.Skipped;

            var buildState = new RiggedGlbCache.BuildState(
                GameSideOnly: diagnostics.GameSideOnly,
                HadTransientFailures: diagnostics.HadTransientFailures,
                WasCanceled: diagnostics.WasCanceled,
                HadProjectAuthoredContent: diagnostics.HadProjectAuthoredContent);

            if (!TryDescribeRiggedBuildDependencies(vfs, stockTextures, diagnostics,
                    out var dependencies))
                return RiggedGlbPrewarmOutcome.CacheFailure;

            bool stored = true;
            try
            {
                foreach (var plan in plans.Where(plan => !plan.Static))
                {
                    var sidecar = PreviewMaps.SidecarPath(plan.Rigged);
                    stored &= cache.TryStore(identity, dependencies.BundleReads,
                        new RiggedGlbCache.Artifact(plan.SlotName,
                            plan.Rigged, File.Exists(sidecar) ? sidecar : null,
                            dependencies.RequiredBundleReads, dependencies.StockTextures), buildState);
                }
            }
            finally { cache.CompleteSubjectPublication(identity); }
            return stored ? RiggedGlbPrewarmOutcome.Ready : RiggedGlbPrewarmOutcome.CacheFailure;
        }
        catch { return RiggedGlbPrewarmOutcome.CacheFailure; }
    }

    /// <summary>Prepare every displayed part of one open, and answer with the parts whose OWN edited
    /// geometry could not be read. Collected rather than thrown so the pass finishes and the refusal can
    /// name all of them at once; the caller opens nothing while it holds anything.
    ///
    /// <para>A missing rigged glb THROWS instead: that file is this run's own build, so its absence is a
    /// failure of the run rather than an answer about the modder's edit, and the shell's generic route says
    /// so.</para></summary>
    internal static IReadOnlyList<string> PrepareSessionParts(IReadOnlyList<SessionPartPlan> parts,
        ICollection<string>? gameSidePrepared = null, bool skipStatic = false,
        System.Threading.CancellationToken cancellationToken = default,
        int maxDegreeOfParallelism = 2, PreviewBlobMemo? previewMemo = null)
    {
        if (maxDegreeOfParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));
        previewMemo ??= new PreviewBlobMemo();
        var unreadableResult = new bool[parts.Count];
        var gameSideResult = new bool[parts.Count];
        var failures = new ExceptionDispatchInfo?[parts.Count];
        System.Threading.Tasks.Parallel.For(0, parts.Count,
            new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(4, maxDegreeOfParallelism),
            }, index =>
        {
            if (cancellationToken.IsCancellationRequested) return;
            var part = parts[index];
            if (skipStatic && part.Static) return;
            try
            {
                if (!File.Exists(part.Rigged))
                    // A fragment, not a sentence: the caller's own "Couldn't prepare the Blender file:" leads it.
                    throw new InvalidDataException($"{part.Token}'s mesh file was not written");
                if (!PrepareSessionPartGlb(part.Rigged, part.EditedGlb, part.SlotName, part.Prepared,
                        part.Maps, part.TextureMaps, previewMemo))
                    unreadableResult[index] = true;
                else if (part.EditedGlb is null && part.Maps is null && part.TextureMaps is null)
                    gameSideResult[index] = true;
            }
            catch (Exception e) { failures[index] = ExceptionDispatchInfo.Capture(e); }
        });
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var failure in failures) failure?.Throw();

        var unreadable = new List<string>();
        for (int index = 0; index < parts.Count; index++)
        {
            if (unreadableResult[index]) unreadable.Add(parts[index].Token);
            if (gameSideResult[index]) gameSidePrepared?.Add(parts[index].Prepared);
        }
        return unreadable;
    }

    /// <summary>The parts a combined open must REFUSE over: each joins the composition through its prepared
    /// file, and one the build could not assemble from opened on the game's copy instead
    /// (<c>vanillaFallbacks</c>). For a bare part that is the game copy either way; for a part carrying mesh
    /// work it is the modder's edit replaced by the mesh it covers, under the edit's own name.</summary>
    internal static IReadOnlyList<string> EditsLostToTheComposition(IReadOnlyList<SessionPartPlan> parts,
        IReadOnlyCollection<string> vanillaFallbacks) => parts
        .Where(part => part.EditedGlb is not null
            && vanillaFallbacks.Contains(part.Token, StringComparer.OrdinalIgnoreCase))
        .Select(part => part.Token).ToList();

    /// <summary>The posed parts that never reached the composition — the build isolates a part whose read
    /// faults and carries on with the others, so a Blender scene can open one part short with nothing said.
    /// Read off what the build REPORTS having composed, not off what it was asked for.
    ///
    /// <para>A STATIC part is not a shortfall: it is drawn unposed, has no skin to join a shared armature,
    /// and no combined build has ever carried one. The open is not refused over what this names — the parts
    /// that landed are still the modder's to work on, and the addon builds its collections from the meshes
    /// the file actually holds, so a send back cannot invent the missing one.</para></summary>
    internal static IReadOnlyList<string> PartsMissingFromComposition(IReadOnlyList<SessionPartPlan> parts,
        IReadOnlyCollection<string> composed) => parts
        .Where(part => !part.Static && !composed.Contains(part.Token, StringComparer.OrdinalIgnoreCase))
        .Select(part => part.Token).ToList();

    /// <summary>Write one displayed part's PREPARED glb — the file this session hands Blender, and the file
    /// both open routes go on to consume: the lone open snapshots it through its transport, and the combined
    /// build assembles the composition out of it.
    ///
    /// <para>The GEOMETRY comes from the part's own edit wherever it has one
    /// (<paramref name="editedGlb"/>): a part carrying mesh work opens on the mesh the modder last sent,
    /// never on the game copy their next send would write over. A bare part re-splits
    /// <paramref name="rigged"/>, which is this run's build of the game's own mesh.</para>
    ///
    /// <para>The map record stays <paramref name="rigged"/>'s EITHER WAY. That record is what classifies a
    /// returned map as untouched-or-asked, and what it has to describe is the part's STOCK maps — which are
    /// the game's whatever the geometry is. An edit's own glb embeds those same stock images (its own
    /// re-split re-embedded them) beside the modder's authored files, so the stock ones still resolve to
    /// their workspace PNGs here and the authored ones arrive through <paramref name="authoredMaps"/>,
    /// exactly as they do for a bare part.</para>
    ///
    /// <para>The ARMATURE is refitted to <paramref name="rigged"/> either way. A bare part re-splits that
    /// file and carries its joints by construction; an edit carries whatever armature its workspace glb
    /// froze at its last send, which may predate this build's bone tail entirely or — if that send came out
    /// of a combined session — be a whole outfit's union armature dragged into a one-part open. So the
    /// edit's joints are reduced to the bones its geometry actually rides, and this run's own offer is
    /// appended after them (<c>refitTo</c>). The modder's paint is what survives: a bone they genuinely
    /// weight rides through even when this build offers no such bone, and one they merely inherited does
    /// not. That is the same reduction the COMBINED route already applies to an edit it reads, so the two
    /// open routes now answer with the same armature for the same part.</para>
    ///
    /// <para>Returns false when the EDIT could not be read — a file that will not parse, will not open, does
    /// not carry the part, or carries it with no armature at all where <paramref name="rigged"/> is posed
    /// (the refusal the combined route already gives that file by name). The caller refuses the whole open
    /// over that answer, so nothing this call may have left at <paramref name="prepared"/> is ever handed to
    /// Blender. EVERY failure of an edited read answers that way, <see cref="IOException"/> included: the
    /// file is the MOD's own, so "close the game and try again" — the sentence the run-wide busy rethrow
    /// buys — is never the remedy.</para>
    ///
    /// <para>Failures on the OUTPUT side are not the edit's and never answer as one: the read of
    /// <paramref name="rigged"/> and of its map record, and the write of <paramref name="prepared"/>, throw
    /// on to the caller's generic route. A full disk and a damaged rigged sidecar used to be reported as
    /// "couldn't read the edit", sending the modder to look at a healthy file. The split is the
    /// re-export's own <c>afterSourceRead</c>, which fires at the last moment a failure could still be the
    /// edit's. A BARE part's failure throws for the same reason all the way through.</para></summary>
    internal static bool PrepareSessionPartGlb(string rigged, string? editedGlb, string slotName,
        string prepared, IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? authoredMaps,
        IReadOnlyList<TextureTransportOverride>? authoredTextures = null,
        PreviewBlobMemo? previewMemo = null)
    {
        if (editedGlb is null)
        {
            MeshGltf.ReexportPartGlb(rigged, slotName, prepared, recordGlb: rigged, authoredMaps: authoredMaps,
                authoredTextures: authoredTextures, previewMemo: previewMemo);
            return true;
        }
        // Opened HERE, outside the answer below: this is the run's own build, and a failure to read it is
        // never the edit's to answer for.
        var offer = MeshGltf.ParsedGlb.Open(rigged);
        bool readingTheEdit = true;
        try
        {
            MeshGltf.ReexportPartGlb(editedGlb, slotName, prepared, recordGlb: rigged,
                authoredMaps: authoredMaps, refitTo: offer,
                afterSourceRead: () => readingTheEdit = false, authoredTextures: authoredTextures,
                previewMemo: previewMemo);
            return true;
        }
        catch (Exception e) when (readingTheEdit && e is not OutOfMemoryException) { return false; }
    }

    /// <summary>What an open says when a part's own edited geometry could not be read. The open is REFUSED
    /// rather than degraded: the alternative is Blender standing the game's mesh under the edit's name, and a
    /// send from that session would replace the modder's work with the copy they were shown. The parts are
    /// NAMED — which edit is unreadable is the whole of what there is to go and look at.</summary>
    /// <summary>Whether an open must be REFUSED over the parts named, and the sentence it refuses with —
    /// null when nothing was named. Both refusals an open can reach (the prepare's unreadable edits, the
    /// composition's lost ones) make the decision here, so neither can drift into opening anyway.</summary>
    internal static string? EditRefusal(IReadOnlyList<string> parts) =>
        parts.Count == 0 ? null : EditGeometryUnreadable(parts);

    internal static string EditGeometryUnreadable(IReadOnlyList<string> parts) =>
        $"Couldn't read the edit on {NameList(parts)}, so Blender was not opened. "
        + $"It would have shown the original mesh instead of your {(parts.Count == 1 ? "edit" : "edits")}.";

    /// <summary>One failure's own words as the tail of a sentence this file began. Whatever the message
    /// ends with, what comes back ends in a full stop: a reason without one ran straight into the sentence
    /// after it.</summary>
    private static string Reason(Exception failure) => failure.Message.TrimEnd().TrimEnd('.') + ".";

    /// <summary>Names in a sentence: "body", "body and cloth", "body, hair and cloth". One home, so every
    /// line an open writes about a set of parts or maps reads the same way.</summary>
    private static string NameList(IReadOnlyList<string> names) => names.Count == 1 ? names[0]
        : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    /// <summary>The notices shared verbatim by the app's launch status and the Blender session document.
    /// The status adds spacing only; the sentences themselves have one construction site.</summary>
    internal static IReadOnlyList<string> BlenderOpenNotices(IReadOnlyList<string> absentParts,
        bool wardrobeUnreadable, IReadOnlyCollection<string> unreadableTextures)
    {
        var notices = new[]
        {
            PartsAbsentNotice(absentParts),
            WardrobeUnreadableNotice(wardrobeUnreadable),
            UnreadableTextureNotice(unreadableTextures),
        };
        return notices.OfType<string>().ToList();
    }

    private static string? PartsAbsentNotice(IReadOnlyList<string> parts) => parts.Count == 0 ? null
        : $"Could not open {NameList(parts)} with the item's other parts, so "
          + (parts.Count == 1 ? "it is not" : "they are not") + " in this Blender scene.";

    private static string? WardrobeUnreadableNotice(bool unreadable) => unreadable
        ? "Could not read the game's wardrobe tables, so this session may offer fewer bones to paint on "
          + "than a build would accept."
        : null;

    private static string? UnreadableTextureNotice(IReadOnlyCollection<string> textures)
    {
        if (textures.Count == 0) return null;
        var named = textures.OrderBy(name => name, StringComparer.Ordinal).Take(3).ToList();
        int rest = textures.Count - named.Count;
        if (rest > 0) named.Add($"{rest} more textures");
        return $"Could not read {NameList(named)}. "
            + (textures.Count == 1 ? "That material opens untextured." : "Those materials open untextured.");
    }

    private static TargetPart SessionTarget(string subject, string outfit, SubjectPart part) => new()
    {
        Subject = subject, Outfit = outfit, RendererSlot = part.SlotName,
    };

    /// <summary>The edit entrance's source for one part: its active content edit, then its first content
    /// edit in authored order. Hide edits never become geometry destinations.</summary>
    internal static string? ActiveOrFirstContentEdit(AuthoredProject project, TargetPart target)
    {
        string? active = project.Always.Select(id => project.EditDefinitions.FirstOrDefault(edit =>
                string.Equals(edit.Id, id, StringComparison.Ordinal)))
            .FirstOrDefault(edit => edit is { Kind: EditDefinitionKind.Content }
                && edit.Target.SameAs(target))?.Id;
        return active ?? project.EditDefinitions.FirstOrDefault(edit =>
            edit.Kind == EditDefinitionKind.Content && edit.Target.SameAs(target))?.Id;
    }

    /// <summary>The authored source one scene part composes from. A direct edit target gets that edit; an
    /// edit-entrance open-all gets active-or-first per part; every stock entrance and every reference gets
    /// the game's original.</summary>
    internal static string? SessionBlenderSourceEdit(AuthoredProject project, TargetPart target,
        TargetPart? requested, string? requestedEditId, bool openAllFromFirstEdit)
    {
        if (requested is not null)
            return requested.SameAs(target) ? requestedEditId : null;
        return openAllFromFirstEdit ? ActiveOrFirstContentEdit(project, target) : null;
    }

    internal static bool ActiveHide(AuthoredProject project, TargetPart target)
    {
        string? selected = project.Always.FirstOrDefault(id => project.EditDefinitions.Any(edit =>
            string.Equals(edit.Id, id, StringComparison.Ordinal) && edit.Target.SameAs(target)));
        return selected is not null && project.EditDefinitions.Any(edit =>
            string.Equals(edit.Id, selected, StringComparison.Ordinal)
            && edit.Kind == EditDefinitionKind.Hide && edit.Target.SameAs(target));
    }

    /// <summary>Only composition-hidden parts in a scene carrying references start hidden in Blender. A
    /// lone target remains visible so it can be worked on; viewport state is never return semantics.</summary>
    internal static bool SessionBlenderViewportVisible(AuthoredProject project, TargetPart target,
        bool carriesReferences) => !carriesReferences || !ActiveHide(project, target);

    private static string StorageName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safe = new(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return safe.Length == 0 ? "part" : safe;
    }

    /// <summary>The live content-edit choices a writable Blender part exposes. Mesh ownership is the edit's
    /// geometry binding, not the geometry the scene happened to open from.</summary>
    internal static IReadOnlyList<BlenderSessionEdit> BlenderSessionEdits(AuthoredProject project,
        TargetPart target)
    {
        var slots = project.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        return project.EditDefinitions
            .Where(edit => edit.Kind == EditDefinitionKind.Content && edit.Target.SameAs(target))
            .Select(edit => new BlenderSessionEdit(edit.Id, edit.Label,
                edit.Bindings.Any(binding => binding.Kind == BindingKind.ProjectAsset
                    && slots.TryGetValue(binding.SlotId, out var slot)
                    && slot.Input == TargetInputKind.Geometry)))
            .ToList();
    }

    private static EditSlotState SessionGeometrySlot(AuthoredEditSession session, string editId) =>
        SessionGeometrySlot(session.Slots(editId), editId);

    private static EditSlotState SessionGeometrySlot(IReadOnlyList<EditSlotState> slots, string editId) =>
        slots.FirstOrDefault(state => state.Slot.Domain == TargetSlotDomain.Game
            && state.Slot.Input == TargetInputKind.Geometry
            && (state.Slot.Tier is null || string.Equals(state.Slot.Tier, "lod0", StringComparison.OrdinalIgnoreCase)))
        ?? throw new InvalidOperationException($"edit '{editId}' has no lod0 geometry slot");

    /// <summary>Every authored semantic map by outbound primitive position. Edit outputs use their recorded
    /// submesh identity; only game-domain slots use the installed material position.</summary>
    internal static IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? SessionAuthoredMaps(
        IReadOnlyList<EditSlotState> slots, string root)
    {
        string? FileAt(int position, TargetInputKind input)
        {
            var state = slots.Where(candidate => BlenderMapPosition(candidate.Slot) == position
                    && candidate.Slot.Input == input && candidate.Binding.Kind == BindingKind.ProjectAsset)
                .OrderBy(candidate => candidate.Slot.Domain == TargetSlotDomain.EditOutput ? 0 : 1)
                .FirstOrDefault();
            if (state?.ProjectAsset?.File is not { Length: > 0 } relative) return null;
            try
            {
                string full = Path.GetFullPath(Path.Combine(root, relative));
                return File.Exists(full) ? full : null;
            }
            catch { return null; }
        }
        int count = slots.Select(state => BlenderMapPosition(state.Slot) ?? -1)
            .DefaultIfEmpty(-1).Max() + 1;
        var maps = Enumerable.Range(0, count)
            .Select(index => (FileAt(index, TargetInputKind.BaseColor), FileAt(index, TargetInputKind.Normal),
                FileAt(index, TargetInputKind.Rmo))).ToList();
        return maps.Any(map => map.Item1 is not null || map.Item2 is not null || map.Item3 is not null)
            ? maps : null;
    }

    /// <summary>Every authored ordinary picture by outbound primitive position and exact property. Fixed
    /// legacy slots without a property retain a semantic fallback; generic slots never do.</summary>
    internal static IReadOnlyList<TextureTransportOverride>? SessionAuthoredTextures(
        IReadOnlyList<EditSlotState> slots, string root)
    {
        var result = new List<TextureTransportOverride>();
        var authored = slots.Where(candidate => BlenderMapPosition(candidate.Slot) is not null
                && candidate.Slot.Input is TargetInputKind.BaseColor or TargetInputKind.Normal
                    or TargetInputKind.Rmo or TargetInputKind.Blend or TargetInputKind.Texture
                && candidate.Binding.Kind == BindingKind.ProjectAsset
                && candidate.ProjectAsset?.File is { Length: > 0 })
            .OrderBy(candidate => candidate.Slot.Domain == TargetSlotDomain.EditOutput ? 0 : 1)
            .GroupBy(candidate => (Position: BlenderMapPosition(candidate.Slot)!.Value,
                candidate.Slot.Input, Property: candidate.Slot.ShaderProperty ?? ""))
            .Select(group => group.First());
        foreach (var state in authored)
        {
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, state.ProjectAsset!.File)); }
            catch { continue; }
            if (!File.Exists(full)) continue;
            var kind = UvGuide.MapKindFor(state.Slot.Input);
            string property = state.Slot.ShaderProperty ?? "";
            if (kind == MapKind.Texture && property.Length == 0) continue;
            result.Add(new TextureTransportOverride(BlenderMapPosition(state.Slot)!.Value,
                property, full, kind));
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>The outbound picture position for one authored slot. Replacement outputs are indexed by
    /// the edit's recorded primitive identity; installed material position belongs only to game-domain
    /// slots and must never reorder a replacement whose material layout differs from the game mesh.</summary>
    private static int? BlenderMapPosition(TargetSlot slot) => slot.Domain switch
    {
        TargetSlotDomain.EditOutput => slot.SubmeshIndex,
        TargetSlotDomain.Game => slot.MaterialSlotIndex,
        _ => null,
    };

    /// <summary>The stale-write baseline the launch writes into a session's exact-slot rows, and the list
    /// <see cref="RequireUnchangedBlenderMaterials"/> compares the project against on return. Internal so a
    /// test can compose the session an exact-slot return arrives on out of the launch's own parts rather
    /// than a restatement of them.</summary>
    internal static IReadOnlyList<BlenderSlotBaseline> BlenderMaterialBaselines(
        IReadOnlyList<EditSlotState> states) => states
        .Where(state => state.Slot.Domain == TargetSlotDomain.EditOutput
            && state.Slot.SubmeshIndex is not null
            && state.Slot.Input is TargetInputKind.BaseColor or TargetInputKind.Normal or TargetInputKind.Rmo
                or TargetInputKind.Blend or TargetInputKind.Texture)
        .OrderBy(state => state.Slot.SubmeshIndex)
        .ThenBy(state => state.Slot.Input)
        .ThenBy(state => state.Slot.ShaderProperty, StringComparer.Ordinal)
        .Select(state => new BlenderSlotBaseline(state.Slot.Id, state.Slot.SubmeshIndex!.Value,
            state.Slot.Input, state.Binding.Kind, state.Binding.ProjectAssetId,
            state.Binding.SourceSlot?.SlotId, state.Binding.SourceSlot?.EditDefinitionId,
            state.Slot.ShaderProperty))
        .ToList();

    /// <summary>What the two stale-session refusals say, in the sentence the part's name is appended to and
    /// the closing "Nothing was changed." follows. Both end the same way because the way out is the same one:
    /// the file in Blender was written against maps this edit no longer has, and only a fresh open writes a
    /// file that matches. Sending again from the open session hands back the same mismatch.</summary>
    internal const string BlenderSessionMapsMovedUnderIt =
        "this edit's maps changed in Doll Remolding Lab while Blender was open, so open the part in Blender "
        + "again before sending";

    internal const string BlenderSessionFromAnOlderVersion =
        "this file from Blender was made by an older version of Doll Remolding Lab, so open the part in "
        + "Blender again before sending";

    private static void RequireUnchangedBlenderMaterials(IReadOnlyList<EditSlotState> slots,
        BlenderSessionTarget target)
    {
        if (target.MaterialSlots is null)
            throw new AuthoredRefusalException(BlenderSessionFromAnOlderVersion);
        var current = BlenderMaterialBaselines(slots);
        if (current.Count != target.MaterialSlots.Count
            || current.Zip(target.MaterialSlots).Any(pair => pair.First != pair.Second))
            throw new AuthoredRefusalException(BlenderSessionMapsMovedUnderIt);
    }

    /// <param name="Canonicalized">Every map file in <paramref name="Rows"/> that THIS PREPARATION wrote,
    /// by full path, against the decoded-pixel identity of what it wrote. Both halves are the preparation's
    /// own work and neither is repeatable anywhere cheaper: the files came out of
    /// <see cref="BlenderMaterialReturn.Normalize"/>, which publishes each one through
    /// <see cref="TextureIngress.Publish(byte[], string, bool, Action{string}?)"/> — the same canonical
    /// encoder the project's picture ingress runs — so re-encoding them at the commit would reproduce them
    /// byte for byte while the window stood still.
    ///
    /// <para>It is a dictionary rather than a flag because it is the SEAM: a row's file is published
    /// through <see cref="ProjectAssetIngress.Prepared"/> only when this names it, and any other file on a
    /// row — a map card's own png, a test's — takes the arm that decodes and re-encodes. Nothing infers
    /// the claim; only the code that did the encoding makes it.</para></param>
    internal readonly record struct BlenderMapIdentity(string? ImageName, string? MaterialName);

    private sealed record PreparedBlenderMaps(IReadOnlyList<SubmeshTextures> Rows,
        IReadOnlyList<(string? Base, string? Normal, string? Rmo)>? Authored, IReadOnlyList<string> Notes,
        IReadOnlyDictionary<string, string> Canonicalized,
        IReadOnlyList<TextureTransportOverride>? AuthoredTextures = null,
        IReadOnlyDictionary<string, BlenderMapIdentity>? ReturnedMaps = null,
        int SubmeshCount = 0);

    private sealed record PreparedBlenderReturn(BlenderSessionTarget Target, TargetPart Part,
        string? ExistingEditId, string? NewEditName, LegacyResolvedPart? Resolved,
        ProjectAssetIngressSession? LegacyIngress, MeshApply.Payload Payload, ProjectAssetSource? Source,
        PreparedBlenderMaps Maps, string? Staged, string ComparisonWorkspace,
        string? SupersededIngressReturn)
    {
        public bool CreatesEdit => ExistingEditId is null;
    }

    /// <summary>An emptied return target after the off-thread half: the part it lands on and, where the
    /// project has no recorded slots yet, what the install said about that part. Content-edit selection is
    /// deliberately absent: an empty return always activates the part's unique hide.</summary>
    private sealed record PreparedBlenderHide(BlenderSessionTarget Target, TargetPart Part,
        LegacyResolvedPart? Resolved);

    private sealed record CommittedBlenderTarget(TargetPart Part, string EditId,
        ProjectAssetIngressSession Ingress, string ComparisonWorkspace, string? SupersededIngressReturn);

    private sealed record ResolvedBlenderTarget(BlenderSessionTarget Target, TargetPart Part,
        string? ExistingEditId, string? NewEditName, bool UseLegacyExact)
    {
        public bool CreatesEdit => ExistingEditId is null;
        public bool ResumesExact => UseLegacyExact || (ExistingEditId is not null && Target.IsExactSlot
            && string.Equals(ExistingEditId, Target.EditDefinitionId, StringComparison.Ordinal));
        public bool RetargetsExistingEdit => ExistingEditId is not null && !ResumesExact;
    }

    private sealed record ResolvedBlenderTargets(IReadOnlyList<ResolvedBlenderTarget> Targets,
        IReadOnlyDictionary<string, TargetPart> Parts, string? Refusal);

    /// <summary>Everything one Blender return needs before a single authored byte moves: the rows that will
    /// publish, the parts that will be hidden, and the staging folder holding the
    /// normalized images and re-exported meshes. <see cref="Refusal"/> is the whole plan where the return
    /// could not be read at all — the transaction then does nothing but say so, which is the same promise
    /// the one-thread version made: parse and preparation failures change nothing.</summary>
    /// <param name="Considered">Every part the preparation ASKED about — the ones that came back and were
    /// not emptied — whether or not they turned out to carry a change. What the return has to report about a
    /// part is not always something it landed: a part moved in Object mode alone comes back byte-identical
    /// and is skipped, and the note saying its move was dropped is exactly what that modder is owed.</param>
    /// <param name="BaselineUnreadable">Whether the file this return was exported FROM was named and could
    /// not be opened, so every part was taken rather than compared. Only ever set where a part's geometry
    /// really was going to be compared.</param>
    private sealed record PreparedBlenderReturnPlan(IReadOnlyList<PreparedBlenderReturn> Returns,
        IReadOnlyList<PreparedBlenderHide> Hides, string? StagingRoot, string? Refusal,
        IReadOnlyList<string> Considered, IReadOnlyList<string> Notes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> PartNotes, string? SessionGlb,
        bool HasReadableSession, IReadOnlyDictionary<string, TargetPart> SessionParts,
        bool BaselineUnreadable = false, int UnchangedParts = 0)
    {
        public static PreparedBlenderReturnPlan Refused(string sentence) =>
            new(Array.Empty<PreparedBlenderReturn>(), Array.Empty<PreparedBlenderHide>(), null, sentence,
                Array.Empty<string>(), Array.Empty<string>(),
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), null, false,
                new Dictionary<string, TargetPart>(StringComparer.OrdinalIgnoreCase));
    }

    private static PreparedBlenderMaps PrepareBlenderMaps(MeshGltf.ParsedGlb returned,
        BlenderSessionTarget target, string stagingRoot)
    {
        var notes = new List<string>();
        var incoming = MeshGltf.ReadSubmeshMaps(returned, target.Part, target.Workspace, notes.Add,
            reportUnkeyed: false);
        var stockRmo = PreviewMaps.ReadSubmeshRmoSources(target.Workspace, target.Part);
        // A new-edit target has no edit id yet, so its staging folder is named by the part instead.
        string directory = Path.Combine(stagingRoot, StorageName(target.EditDefinitionId ?? target.Part));
        var rows = BlenderMaterialReturn.Normalize(incoming, directory,
            submesh => stockRmo.GetValueOrDefault(submesh), notes.Add);
        var bySubmesh = rows.ToDictionary(row => row.Submesh);
        if (rows.Any(row => row.AlbedoAsk == SlotOrigin.ExplicitNeutral))
            throw new InvalidDataException("a base color map cannot be blank");
        var authored = Enumerable.Range(0, incoming.Count).Select(submesh =>
        {
            bySubmesh.TryGetValue(submesh, out var row);
            return (row?.Albedo, row?.Normal, row?.Rmo);
        }).ToList();
        var authoredTextures = new List<TextureTransportOverride>();
        var returnedMaps = new Dictionary<string, BlenderMapIdentity>(StringComparer.OrdinalIgnoreCase);
        for (int submesh = 0; submesh < incoming.Count; submesh++)
        {
            if (!bySubmesh.TryGetValue(submesh, out var row)) continue;
            foreach (var texture in incoming[submesh].Textures ?? Array.Empty<IncomingTexture>())
            {
                var exact = row.Textures?.FirstOrDefault(candidate => string.Equals(candidate.ShaderProperty,
                    texture.ShaderProperty, StringComparison.Ordinal));
                bool primary = (incoming[submesh].Textures ?? Array.Empty<IncomingTexture>())
                    .First(candidate => candidate.Kind == texture.Kind).ShaderProperty == texture.ShaderProperty;
                string? file = exact?.File ?? (exact is not null || !primary ? null : texture.Kind switch
                {
                    MapKind.BaseColor => row.Albedo,
                    MapKind.Normal => row.Normal,
                    MapKind.Rmo => row.Rmo,
                    MapKind.Blend => row.Blend,
                    _ => null,
                });
                if (file is not null)
                {
                    authoredTextures.Add(new TextureTransportOverride(texture.MaterialIndex,
                        texture.ShaderProperty, file, texture.Kind));
                    returnedMaps[Path.GetFullPath(file)] = new BlenderMapIdentity(texture.ImageName,
                        incoming[submesh].MaterialName);
                }
            }
        }
        for (int submesh = 0; submesh < incoming.Count; submesh++)
        {
            if (!bySubmesh.TryGetValue(submesh, out var row)) continue;
            Remember(row.Albedo, incoming[submesh].BaseColorName);
            Remember(row.Normal, incoming[submesh].NormalName);
            Remember(row.Rmo, incoming[submesh].RmoName);

            void Remember(string? file, string? imageName)
            {
                if (file is not null)
                    returnedMaps[Path.GetFullPath(file)] = new BlenderMapIdentity(imageName,
                        incoming[submesh].MaterialName);
            }
        }
        // The decode the COMMIT no longer has to do, done here because here is off the window's thread.
        var canonicalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in rows.SelectMany(row => new[] { row.Albedo, row.Normal, row.Rmo, row.Blend }
                         .Concat(row.Textures?.Select(texture => texture.File) ?? Array.Empty<string?>()))
                     .OfType<string>())
            canonicalized[Path.GetFullPath(file)] = TextureIngress.PixelIdentity(file);
        return new PreparedBlenderMaps(rows, authored.Any(map => map.Item1 is not null
            || map.Item2 is not null || map.Item3 is not null) ? authored : null, notes, canonicalized,
            authoredTextures.Count == 0 ? null : authoredTextures,
            returnedMaps.Count == 0 ? null : returnedMaps, incoming.Count);
    }

    /// <summary>What one returned part CHANGED, as the normalized map rows its publish will consume — or null
    /// where the return changed nothing about it and the part carries no edit at all.
    ///
    /// <para>This is the open-all's central question. Blender's Send hands back every writable part of the
    /// session, so "it came back" says nothing: taking each returned part mints an edit for a part still
    /// carrying the game's mesh and the game's maps, and each one costs the build a replacement pipeline, the
    /// mod a folder of files, and this return the whole re-export and install read behind it. A fifteen-part
    /// open-all with two moved parts is two parts' work.</para>
    ///
    /// <para>Two ways a part carries one, and the MAPS are asked first: their read is the one this method
    /// owes anyway on every part it takes, and a part that asks for a map never pays the mesh comparison at
    /// all. A row exists only where a slot asked for something (<see cref="SlotOrigins.IsAsk"/>), so
    /// re-embedding the part's own stock maps untouched is not an ask — while plugging the neutral in is one,
    /// even though it names no file.</para>
    ///
    /// <para>Then the mesh, against <paramref name="baseline"/> — the file the launch HANDED Blender, which
    /// for a combined session is not this part's workspace glb: those carry geometry only while the session
    /// is rigged, so comparing against one reads every part of the outfit as re-skinned. A return that names
    /// no baseline (an older session file, a hand-written one) cannot be asked, and an unanswerable question
    /// takes the part rather than dropping an edit that is really there.</para>
    ///
    /// <para>The baseline is opened HERE and nowhere earlier: it is the whole combined glb parsed a second
    /// time, and a return whose every part asks for a map never reaches this line at all. Forcing it is
    /// also what records that the file could not be read, which the commit owes the modder a line
    /// about.</para></summary>
    private static PreparedBlenderMaps? PrepareChangedPart(MeshGltf.ParsedGlb returned,
        BlenderSessionTarget target, string stagingRoot, Lazy<MeshGltf.ParsedGlb?> baseline,
        PreparedBlenderMaps? prepared = null, bool force = false)
    {
        var maps = prepared ?? PrepareBlenderMaps(returned, target, stagingRoot);
        if (maps.Rows.Count > 0 || force) return maps;
        if (baseline.Value is not { } opened) return maps;
        return SendBackGeometry.Unchanged(returned, target.Part, opened) ? null : maps;
    }

    /// <param name="canonicalized">What the preparation already canonicalized and measured, by full path —
    /// see <see cref="PreparedBlenderMaps"/>. A file it names is MOVED into the project under the identity
    /// recorded with it; every other file is decoded and re-encoded here, which is what an outside picture
    /// needs and what this route used to do to all of them.</param>
    internal static int PublishBlenderMaps(AuthoredEditSession.CompoundChange change, string editId,
        int submeshCount, IReadOnlyList<SubmeshTextures> rows,
        IReadOnlyDictionary<string, string>? canonicalized = null, Action? onPublished = null,
        IReadOnlyDictionary<string, BlenderMapIdentity>? returnedMaps = null)
    {
        var bySubmesh = rows.ToDictionary(row => row.Submesh);
        int published = 0;
        for (int submesh = 0; submesh < submeshCount; submesh++)
        {
            bySubmesh.TryGetValue(submesh, out var row);
            bool reliefDue = row is not null
                && (row.AlbedoAsk.IsAsk() || row.NormalAsk.IsAsk() || row.RmoAsk.IsAsk());
            Apply(TargetInputKind.BaseColor, null, row?.Albedo, row?.AlbedoAsk ?? SlotOrigin.None,
                implicitNeutral: false);
            Apply(TargetInputKind.Normal, null, row?.Normal, row?.NormalAsk ?? SlotOrigin.None,
                implicitNeutral: true);
            Apply(TargetInputKind.Rmo, null, row?.Rmo, row?.RmoAsk ?? SlotOrigin.None,
                implicitNeutral: true);
            Apply(TargetInputKind.Blend, null, row?.Blend, row?.BlendAsk ?? SlotOrigin.None,
                implicitNeutral: false);
            var outputPictures = change.Slots(editId)
                .Where(candidate => candidate.Slot.Domain == TargetSlotDomain.EditOutput
                    && candidate.Slot.SubmeshIndex == submesh
                    && candidate.Slot.Input is TargetInputKind.BaseColor or TargetInputKind.Normal
                        or TargetInputKind.Rmo or TargetInputKind.Blend or TargetInputKind.Texture
                    && candidate.Slot.ShaderProperty is { Length: > 0 })
                .ToList();
            var primaryFixed = outputPictures.Where(candidate => candidate.Slot.Input != TargetInputKind.Texture)
                .GroupBy(candidate => candidate.Slot.Input)
                .Select(group => group.First().Slot.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var output in outputPictures.Where(candidate =>
                         candidate.Slot.Input == TargetInputKind.Texture || !primaryFixed.Contains(candidate.Slot.Id)))
            {
                var texture = row?.Textures?.FirstOrDefault(candidate =>
                    string.Equals(candidate.ShaderProperty, output.Slot.ShaderProperty, StringComparison.Ordinal));
                Apply(output.Slot.Input, output.Slot.ShaderProperty, texture?.File,
                    texture?.Ask ?? SlotOrigin.None,
                    implicitNeutral: false);
            }
            ApplyRmoAlpha(row?.RmoAlpha);

            void Apply(TargetInputKind input, string? shaderProperty, string? file, SlotOrigin ask,
                bool implicitNeutral)
            {
                var candidates = change.Slots(editId).Where(candidate =>
                    candidate.Slot.Domain == TargetSlotDomain.EditOutput
                    && candidate.Slot.SubmeshIndex == submesh && candidate.Slot.Input == input);
                var state = shaderProperty is null
                    ? candidates.FirstOrDefault()
                    : candidates.SingleOrDefault(candidate => string.Equals(candidate.Slot.ShaderProperty,
                        shaderProperty, StringComparison.Ordinal));
                if (state is null) return;
                if (file is not null)
                {
                    string? identity = null;
                    bool prepared = canonicalized is not null
                        && canonicalized.TryGetValue(Path.GetFullPath(file), out identity);
                    // Prepared bytes are handed OVER: this transport is opened, published and finished
                    // inside this one transaction, and no program outside the app ever sees it.
                    var ingress = change.BeginIngress(editId, state.Slot.Id, file, handOver: prepared);
                    BlenderMapIdentity returned = default;
                    returnedMaps?.TryGetValue(Path.GetFullPath(file), out returned);
                    string? materialName = !string.IsNullOrWhiteSpace(state.Slot.Material?.Name)
                        ? state.Slot.Material.Name : returned.MaterialName;
                    var result = change.PublishAssetForBinding(ingress, ProjectAssetKind.Picture,
                        BlenderMapLabel(returned.ImageName, materialName, input,
                            state.Slot.ShaderProperty),
                        prepared ? ProjectAssetIngress.Prepared(identity!) : ProjectAssetIngress.Png);
                    if (result.Result == ProjectAssetPublishResult.Published)
                    {
                        published++;
                        onPublished?.Invoke();
                    }
                    return;
                }
                var desired = ask == SlotOrigin.VanillaOwn ? BindingKind.InheritedLiveCarrier
                    : ask == SlotOrigin.ExplicitNeutral || (implicitNeutral && reliefDue)
                        ? BindingKind.Neutral : BindingKind.InheritedLiveCarrier;
                if (state.Binding.Kind == desired && state.Binding.ProjectAssetId is null
                    && state.Binding.SourceSlot is null) return;
                if (desired == BindingKind.Neutral) change.ChooseNeutral(editId, state.Slot.Id);
                else change.ChooseInheritedCarrier(editId, state.Slot.Id);
            }

            void ApplyRmoAlpha(RmoAlphaAnswer? answer)
            {
                var slots = change.Slots(editId);
                var alpha = slots.SingleOrDefault(candidate =>
                    candidate.Slot.Domain == TargetSlotDomain.EditOutput
                    && candidate.Slot.SubmeshIndex == submesh
                    && candidate.Slot.Input == TargetInputKind.RmoAlpha);
                var rmo = slots.SingleOrDefault(candidate =>
                    candidate.Slot.Domain == TargetSlotDomain.EditOutput
                    && candidate.Slot.SubmeshIndex == submesh
                    && candidate.Slot.Input == TargetInputKind.Rmo);
                if (alpha is null || rmo is null) return;
                if (answer is not null && rmo.ProjectAsset is { } rmoAsset)
                {
                    change.ChooseStructuredValue(editId, alpha.Slot.Id, "RMO alpha", rmoAsset.File,
                        "rmo-alpha", answer == RmoAlphaAnswer.Rebuild
                            ? "rebuild-from-stock" : "ship-as-authored", rmoAsset.Id);
                    return;
                }
                change.ChooseInheritedCarrier(editId, alpha.Slot.Id);
            }
        }
        return published;
    }

    /// <summary>The authored identity of one image returned by Blender. A carried image name wins; older
    /// returns without one fall back to the material and slot names the return still carries.</summary>
    internal static string BlenderMapLabel(string? returnedName, string? materialName, TargetInputKind input,
        string? shaderProperty)
    {
        string image = Path.GetFileNameWithoutExtension(returnedName ?? "").Trim();
        if (image.Length > 0) return image;
        string material = string.IsNullOrWhiteSpace(materialName) ? "Material" : materialName.Trim();
        return $"{material} {TextureMap.SlotLabel(input, shaderProperty)}";
    }

    /// <summary>Give a new-edit or hide return the places its answers bind at: slots from the install where
    /// it answers, or the recorded routes where the part was already opened. A HIDE needs this exactly as a
    /// content edit does — a hide binds visibility on one of the part's own routes, so an emptied part the
    /// project has never opened has nothing to anchor on.
    ///
    /// <para>A part neither can answer for is refused by <see cref="RequireReturnPartsHaveSomewhere"/>
    /// before any of this runs, so the refusal names every part at fault rather than whichever one the loop
    /// reached first. The session's own per-part refusal underneath stays exactly where it is: that is the
    /// invariant's home, and this route only gets to the whole list before it.</para></summary>
    private static void EnsureReturnPartSlots(AuthoredEditSession.CompoundChange change, TargetPart part,
        LegacyResolvedPart? resolved)
    {
        if (resolved is not null) change.EnsurePartSlots(part, resolved);
        else if (!change.HasPartSlots(part)) change.EnsurePartSlots(part, null);
    }

    /// <summary>Refuse a whole return the game files cannot answer for, NAMING the parts. A return is
    /// all-or-nothing, so one part with nowhere to record what came back costs the modder the entire send —
    /// and a sentence saying "this part" while naming none of the fifteen leaves them to guess which.
    ///
    /// <para>Asked for every new-edit and hide target before the first mutation, so what comes out is the complete list
    /// rather than the first refusal reached.</para></summary>
    private static void RequireReturnPartsHaveSomewhere(AuthoredEditSession.CompoundChange change,
        PreparedBlenderReturnPlan plan)
    {
        var nowhere = plan.Returns.Where(item => item.CreatesEdit)
            .Select(item => (item.Target, item.Part, item.Resolved))
            .Concat(plan.Hides.Select(item => (item.Target, item.Part, item.Resolved)))
            .Where(item => item.Resolved is null && !change.HasPartSlots(item.Part))
            .Select(item => item.Target.Part)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (nowhere.Count > 0) throw new AuthoredRefusalException(PartsWithNowhereToRecord(nowhere));
    }

    /// <summary>What a return says when the game files cannot answer for parts it carries. A part is named
    /// by its RENDERER SLOT for the reason the project conversion names one that way: the short token is
    /// read off a warmed subject model, and an install that could not answer for the part has none. Three
    /// names then a count — a status line is one line.</summary>
    internal static string PartsWithNowhereToRecord(IReadOnlyList<string> parts) => parts.Count == 1
        ? $"{parts[0]} isn't in the current game files, so there is nowhere to record what came back"
        : "These parts aren't in the current game files, so there is nowhere to record what came back: "
          + string.Join(", ", parts.Take(3))
          + (parts.Count > 3 ? $", and {parts.Count - 3} more" : "");

    /// <summary>One target's failure, told with the part it happened on. A return is all-or-nothing, so any
    /// one part's refusal is the whole send's — and every sentence underneath here was written for a
    /// surface that already stands on a part. This one does not: fifteen parts went out, and the modder is
    /// owed which of them came back wrong.</summary>
    internal static string BlenderPartReason(string part, string reason) =>
        $"{reason.TrimEnd().TrimEnd('.')} ({part})";

    /// <summary>Run one target's share of a return, so a failure carries the part it happened on. The
    /// refusal keeps its class: a sentence the model wrote for the modder stays one.</summary>
    private static void ForPart(string part, Action work)
    {
        try { work(); }
        catch (AuthoredRefusalException e)
        { throw new AuthoredRefusalException(BlenderPartReason(part, e.Message)); }
        catch (Exception e) when (e is not OutOfMemoryException)
        { throw new InvalidOperationException(BlenderPartReason(part, e.Message), e); }
    }

    private static ResolvedBlenderTargets ResolveBlenderTargets(IncomingEdit incoming,
        IReadOnlyList<BlenderSessionTarget> routes, BlenderSessionDocument? liveSession,
        AuthoredProject project)
    {
        var resolved = new List<ResolvedBlenderTarget>();
        var parts = new Dictionary<string, TargetPart>(StringComparer.OrdinalIgnoreCase);
        foreach (var route in routes)
        {
            if (parts.ContainsKey(route.Part))
                return new(Array.Empty<ResolvedBlenderTarget>(), parts, BlenderReturnFromAnOlderLab);
            SessionPart? declared = liveSession?.Parts.FirstOrDefault(part =>
                string.Equals(part.Name, route.Part, StringComparison.OrdinalIgnoreCase));
            var selected = incoming.TargetFor(route.Part);
            // A new-object target cannot be emitted by the old bridge. An existing-id string is also the old
            // bridge's launch echo, so only a session carrying the typed inventory promotes that shape from
            // compatibility routing to send-time selection.
            bool useSelection = selected is not null && (selected.IsNew || declared?.Edits is not null);
            if (useSelection && selected!.IsExisting)
            {
                string editId = selected.ExistingEditId!;
                var definition = project.EditDefinitions.SingleOrDefault(candidate =>
                    string.Equals(candidate.Id, editId, StringComparison.Ordinal));
                string? oldLabel = declared?.Edits?.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, editId, StringComparison.Ordinal))?.Label;
                if (definition is null)
                    return new(Array.Empty<ResolvedBlenderTarget>(), parts,
                        BlenderSelectedEditMissing(editId, oldLabel));
                var part = CopyTarget(definition.Target);
                if (definition.Kind != EditDefinitionKind.Content || !RouteMatches(route, part))
                    return new(Array.Empty<ResolvedBlenderTarget>(), parts,
                        BlenderSelectedEditWrongPart(editId, definition.Label, route.Part));
                bool resumesOpenedEdit = route.IsExactSlot && string.Equals(
                    route.EditDefinitionId, editId, StringComparison.Ordinal);
                resolved.Add(new ResolvedBlenderTarget(route, part, editId, null, resumesOpenedEdit));
                parts.Add(route.Part, part);
                continue;
            }
            if (useSelection && selected!.IsNew)
            {
                if (!route.IsPartRoute)
                    return new(Array.Empty<ResolvedBlenderTarget>(), parts, BlenderReturnFromAnOlderLab);
                var part = RoutePart(route);
                resolved.Add(new ResolvedBlenderTarget(route, part, null, selected.NewEditName, false));
                parts.Add(route.Part, part);
                continue;
            }
            if (route.IsExactSlot)
            {
                string editId = route.EditDefinitionId!;
                var definition = project.EditDefinitions.SingleOrDefault(candidate =>
                    string.Equals(candidate.Id, editId, StringComparison.Ordinal));
                if (definition is null)
                    return new(Array.Empty<ResolvedBlenderTarget>(), parts,
                        BlenderOpenedEditMissing);
                var part = CopyTarget(definition.Target);
                resolved.Add(new ResolvedBlenderTarget(route, part, editId, null, true));
                parts.Add(route.Part, part);
                continue;
            }
            if (!route.IsPartRoute)
                return new(Array.Empty<ResolvedBlenderTarget>(), parts, BlenderReturnFromAnOlderLab);
            var routedPart = RoutePart(route);
            resolved.Add(new ResolvedBlenderTarget(route, routedPart, null, null, false));
            parts.Add(route.Part, routedPart);
        }
        return new(resolved, parts, null);
    }

    private static bool RouteMatches(BlenderSessionTarget route, TargetPart part) =>
        string.Equals(route.Part, part.RendererSlot, StringComparison.OrdinalIgnoreCase)
        && (!route.IsPartRoute || part.SameAs(RoutePart(route)));

    private static TargetPart RoutePart(BlenderSessionTarget target) => new()
    {
        Subject = target.Subject!, Outfit = target.Outfit!, RendererSlot = target.Part,
    };

    private static TargetPart CopyTarget(TargetPart target) => new()
    {
        Subject = target.Subject, Outfit = target.Outfit, RendererSlot = target.RendererSlot,
    };

    internal static string BlenderSelectedEditMissing(string editId, string? label) =>
        $"The selected edit '{(string.IsNullOrWhiteSpace(label) ? editId : label)}' no longer exists. "
        + "Nothing was changed.";

    internal const string BlenderOpenedEditMissing =
        "The edit this part was opened from no longer exists. Nothing was changed.";

    internal static string BlenderSelectedEditWrongPart(string editId, string? label, string part) =>
        $"The selected edit '{(string.IsNullOrWhiteSpace(label) ? editId : label)}' does not belong to "
        + $"{part}. Nothing was changed.";

    internal static string BlenderRetargetShapeMismatch(string label, int held, int sent) =>
        $"The selected edit '{label}' holds {held} material slots and the sent mesh has {sent}, "
        + "so the send cannot land on it. Nothing was changed.";

    /// <summary>The replacement material layout an edit already owns, or null while it has no authored
    /// geometry and therefore no replacement layout to preserve. Output indices are minted contiguously by
    /// <see cref="AuthoredEditSession.RecordReplacementOutputs"/>; max-plus-one is the recorded slot count.</summary>
    private static int? BlenderReplacementMaterialSlotCount(IReadOnlyList<EditSlotState> slots)
    {
        bool holdsMesh = slots.Any(state => state.Slot.Domain == TargetSlotDomain.Game
            && state.Slot.Input == TargetInputKind.Geometry && state.ProjectAsset is not null);
        if (!holdsMesh) return null;
        return slots.Where(state => state.Slot.Domain == TargetSlotDomain.EditOutput
                && state.Slot.SubmeshIndex is not null)
            .Select(state => state.Slot.SubmeshIndex!.Value).DefaultIfEmpty(-1).Max() + 1;
    }

    internal static string BlenderMetadataUnreadable(string path) =>
        $"Could not read {Path.GetFileName(path)}, so the file sent back from Blender could not be addressed. "
        + "Nothing was changed.";

    /// <summary>Read one session-native Blender return whole, WITHOUT touching authored state: the glb
    /// parse, every per-part re-export and normalization, and the new-edit targets' install reads. Runs on a
    /// worker — <see cref="QueueBlenderReturn"/> starts the chain on the pool, so nothing here is on the
    /// window's thread and nothing here waits for it.
    ///
    /// <para>The return is consumed solely through the identities written beside it at launch: exact
    /// edit/slot rows and part rows whose sidecar selections are resolved against the live session.
    /// A failure anywhere here is a refusal the transaction reports and nothing else — the working file and
    /// the raw return both stand, exactly as they did when this ran in one piece.</para></summary>
    private async Task<PreparedBlenderReturnPlan> PrepareBlenderReturnAsync(IncomingEdit edit,
        AuthoredEditSession session)
    {
        var targets = BlenderBridge.ReadReturnTargets(edit.GlbPath);
        if (targets.Count == 0)
        {
            if (BlenderBridge.ReturnTargetMetadataExists(edit.GlbPath)
                && !BlenderBridge.ReturnTargetMetadataReadable(edit.GlbPath))
                return PreparedBlenderReturnPlan.Refused(
                    BlenderMetadataUnreadable(BlenderBridge.TargetPath(edit.GlbPath)));
            return PreparedBlenderReturnPlan.Refused(BlenderReturnUnaddressed);
        }
        if (targets.Any(target => !target.IsExactSlot && !target.IsPartRoute))
            return PreparedBlenderReturnPlan.Refused(BlenderReturnFromAnOlderLab);

        string? sessionGlb = BlenderBridge.ReadReturnSessionGlb(edit.GlbPath);
        string? comparisonGlb = BlenderBridge.ReadReturnBaseline(edit.GlbPath);
        BlenderSessionDocument? liveSession = null;
        bool hasReadableSession = false;
        if (sessionGlb is not null && BlenderBridge.SessionMetadataExists(sessionGlb))
        {
            liveSession = BlenderBridge.ReadSessionDocument(sessionGlb);
            if (liveSession is null)
                return PreparedBlenderReturnPlan.Refused(
                    BlenderMetadataUnreadable(BlenderBridge.SessionPath(sessionGlb)));
            hasReadableSession = true;
        }
        var project = session.Snapshot();
        var resolved = ResolveBlenderTargets(edit, targets, liveSession, project);
        if (resolved.Refusal is not null) return PreparedBlenderReturnPlan.Refused(resolved.Refusal);
        var hidden = (edit.HiddenParts ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var returns = new List<PreparedBlenderReturn>();
        var hides = new List<PreparedBlenderHide>();
        var considered = new List<string>();
        int unchangedParts = 0;
        string? stagingRoot = null;
        try
        {
            MeshGltf.ParsedGlb? returned = null;
            if (targets.Any(target => !hidden.Contains(target.Part)))
                returned = MeshGltf.ParsedGlb.Open(edit.GlbPath);
            // Deferred, not merely tidy: opening it is the whole combined glb parsed a SECOND time, and a
            // return whose parts all ask for a map never asks the geometry question at all. Two full parses
            // of a fifteen-part outfit also stand in memory together for as long as the return runs.
            var baseline = new Lazy<MeshGltf.ParsedGlb?>(() => OpenBlenderReturnBaseline(comparisonGlb));
            var returnedMeshes = (returned?.MeshNames ?? Array.Empty<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var returnNotes = (returned?.UnkeyedTextureImages ?? Array.Empty<string>())
                .Select(image => $"Ignored {image} from Blender: it isn't linked to a texture slot.")
                .ToList();
            var partNotes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            // Keep only the small answer every part's warning/filter needs. A parsed prepared GLB can embed
            // all of the part's preview PNGs; retaining one for every considered part multiplies the return's
            // live set before the changed/unchanged question has spared anything.
            var geometryContractCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var unreadableGeometryContracts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (project.RootDir is null) throw new InvalidOperationException("this mod has no folder yet");
            stagingRoot = Path.Combine(project.RootDir, ProjectAssetIngress.DirectoryName,
                "blender-material-return", Guid.NewGuid().ToString("N"));
            foreach (var target in resolved.Targets)
            {
                // Everything one target's share can fail on comes back out NAMING that part: the whole
                // return is about to be refused over it, and the sentences underneath were all written for
                // a surface that already stands on a part.
                try
                {
                    PreparedBlenderMaps? preparedMaps = null;
                    if (target.RetargetsExistingEdit && !hidden.Contains(target.Target.Part)
                        && returnedMeshes.Contains(target.Target.Part))
                    {
                        preparedMaps = PrepareBlenderMaps(returned!, target.Target, stagingRoot!);
                        var targetSlots = session.Slots(target.ExistingEditId!);
                        if (BlenderReplacementMaterialSlotCount(targetSlots) is { } held
                            && held != preparedMaps.SubmeshCount)
                        {
                            string label = project.EditDefinitions.Single(definition => string.Equals(
                                definition.Id, target.ExistingEditId, StringComparison.Ordinal)).Label;
                            DeleteBlenderStaging(stagingRoot);
                            return PreparedBlenderReturnPlan.Refused(
                                BlenderRetargetShapeMismatch(label, held, preparedMaps.SubmeshCount));
                        }
                    }
                    await ReadOneTarget(target, preparedMaps);
                }
                catch (AuthoredRefusalException e)
                { throw new AuthoredRefusalException(BlenderPartReason(target.Target.Part, e.Message)); }
                catch (Exception e) when (e is not OutOfMemoryException)
                { throw new InvalidOperationException(BlenderPartReason(target.Target.Part, e.Message), e); }
            }

            async Task ReadOneTarget(ResolvedBlenderTarget destination, PreparedBlenderMaps? preparedMaps)
            {
                var target = destination.Target;
                if (hidden.Contains(target.Part))
                {
                    LegacyResolvedPart? resolvedPart = null;
                    if (destination.CreatesEdit && !project.TargetSlots.Any(slot =>
                            slot.Part.SameAs(destination.Part)))
                        resolvedPart = await ResolvePartAsync(destination.Part);
                    hides.Add(new PreparedBlenderHide(target, destination.Part, resolvedPart));
                    return;
                }
                // A part routed to a new edit but not carried by the send is untouched. An existing target
                // retains the exact route's stricter behavior: its missing mesh is an invalid return.
                if (destination.CreatesEdit && !returnedMeshes.Contains(target.Part)) return;

                BlenderSessionTarget effective = target;
                if (!destination.ResumesExact)
                    effective = target with
                    {
                        ProjectAssetId = "",
                        EditDefinitionId = destination.ExistingEditId,
                        SlotId = null,
                        IngressReturn = null,
                        SourceBindingKind = null,
                        MaterialSlots = destination.ExistingEditId is null ? null
                            : BlenderMaterialBaselines(session.Slots(destination.ExistingEditId)),
                    };
                considered.Add(target.Part);
                int geometryContract = GeometryContractCount(target);
                var uvNotes = MeshGltf.ReturnedTexCoordWarnings(returned!, target.Part, geometryContract);
                // An explicit destination other than what the scene opened from always lands, even when
                // the geometry is untouched: a retarget copies into the chosen edit, and New Edit on a
                // part OPENED FROM AN EDIT duplicates that edit. New Edit on a stock-opened part keeps
                // the untouched-leaves-nothing rule — otherwise every all-parts send would mint an edit
                // per untouched part.
                bool explicitDestination = destination.RetargetsExistingEdit
                    || (destination.CreatesEdit && target.IsExactSlot);
                if (PrepareChangedPart(returned!, effective, stagingRoot!, baseline, preparedMaps,
                        force: explicitDestination) is not { } maps)
                {
                    AddPartNotes(target.Part, uvNotes);
                    unchangedParts++;
                    return;
                }
                var geometryBaseline = OpenGeometryContract(target, geometryContract);
                if (geometryBaseline is not null) AddPartNotes(target.Part, uvNotes);
                if (destination.ResumesExact)
                {
                    RequireUnchangedBlenderMaterials(session.Slots(destination.ExistingEditId!), effective);
                    var ingress = ProjectAssetIngress.Resume(project, destination.ExistingEditId!,
                        target.SlotId!, target.IngressReturn!, target.ProjectAssetId,
                        target.SourceBindingKind);
                    var payload = MeshGltf.ReexportPartGlb(returned!, target.Part,
                        ingress.ReturnArtifact, recordGlb: target.Workspace, authoredMaps: maps.Authored,
                        authoredTextures: maps.AuthoredTextures, geometryBaseline: geometryBaseline);
                    var geometry = SessionGeometrySlot(session, destination.ExistingEditId!);
                    var lineage = ingress.SourceProjectAssetId is null && geometry.Slot.Mesh is not null
                        ? new ProjectAssetSource { GameAsset = geometry.Slot.Mesh } : null;
                    string comparison = PrepareBlenderComparisonWorkspace(ingress.ReturnArtifact,
                        stagingRoot!, target.Part);
                    returns.Add(new PreparedBlenderReturn(effective, destination.Part,
                        destination.ExistingEditId, null, null, ingress, payload, lineage, maps, null,
                        comparison, null));
                    return;
                }

                var resolvedPartForEdit = destination.CreatesEdit
                    ? await ResolvePartAsync(destination.Part) : null;
                string staged = Path.Combine(stagingRoot!, StorageName(target.Part) + ".return.glb");
                var stagedPayload = MeshGltf.ReexportPartGlb(returned!, target.Part, staged,
                    recordGlb: target.Workspace, authoredMaps: maps.Authored,
                    authoredTextures: maps.AuthoredTextures, geometryBaseline: geometryBaseline);
                string preparedComparison = PrepareBlenderComparisonWorkspace(staged,
                    stagingRoot!, target.Part);
                returns.Add(new PreparedBlenderReturn(effective, destination.Part,
                    destination.ExistingEditId, destination.NewEditName, resolvedPartForEdit, null,
                    stagedPayload, null, maps, staged, preparedComparison,
                    target.IsExactSlot ? target.IngressReturn : null));
            }

            string GeometryContractKey(BlenderSessionTarget target) =>
                target.Workspace + "\0" + target.Part;

            int GeometryContractCount(BlenderSessionTarget target)
            {
                string key = GeometryContractKey(target);
                if (geometryContractCounts.TryGetValue(key, out int count)) return count;
                try
                {
                    var contract = MeshGltf.ParsedGlb.Open(target.Workspace);
                    return geometryContractCounts[key] =
                        MeshGltf.TransportedTexCoordCount(contract, target.Part);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    WarnUnreadableGeometryContract(target, key);
                    return geometryContractCounts[key] = -1; // explicit no-filtering degradation
                }
            }

            MeshGltf.ParsedGlb? OpenGeometryContract(BlenderSessionTarget target, int count)
            {
                if (count < 0) return null;
                string key = GeometryContractKey(target);
                try { return MeshGltf.ParsedGlb.Open(target.Workspace); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    geometryContractCounts[key] = -1;
                    WarnUnreadableGeometryContract(target, key);
                    return null;
                }
            }

            void WarnUnreadableGeometryContract(BlenderSessionTarget target, string key)
            {
                if (!unreadableGeometryContracts.Add(key)) return;
                AddPartNotes(target.Part, new[]
                {
                    $"Couldn't read the file {target.Part} was opened from, so every UV layer "
                    + "that came back was kept."
                });
            }

            void AddPartNotes(string part, IEnumerable<string> additions)
            {
                if (!partNotes.TryGetValue(part, out var list))
                    partNotes.Add(part, list = new List<string>());
                list.AddRange(additions);
            }

            // Named and unopenable, and only ever asked once a part's geometry really was going to be
            // compared: the return then took every part it had, and the commit says so.
            bool unreadable = comparisonGlb is not null && baseline.IsValueCreated && baseline.Value is null;
            return new PreparedBlenderReturnPlan(returns, hides, stagingRoot, null,
                considered, returnNotes, partNotes.ToDictionary(pair => pair.Key,
                    pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase),
                sessionGlb, hasReadableSession, resolved.Parts,
                unreadable, unchangedParts);
        }
        catch (Exception e)
        {
            DeleteBlenderStaging(stagingRoot);
            AppLog.Write("Couldn't read the file sent back from Blender", e);
            // A refusal was written for the modder and names the part it happened on, so it is shown as it
            // is — and its own punctuation is normalized here, since a reason without a full stop ran
            // straight into "Nothing was changed." Everything else is the model's account of its own
            // identifiers (an edit id, a slot id, a missing artifact path) and says nothing on a status
            // line, so that line reports the read and the exception goes to the log.
            return PreparedBlenderReturnPlan.Refused(e is AuthoredRefusalException
                ? $"Couldn't read the file sent back from Blender: {Reason(e)} Nothing was changed."
                : BlenderReturnUnreadable);
        }
    }

    /// <summary>The current acknowledged comparison glb, parsed once for the whole return — every part is
    /// compared against its own mesh inside this one file. Null where the session named none
    /// (<paramref name="comparisonGlb"/> null) or the file will not open, which
    /// <see cref="PrepareChangedPart"/> reads as "cannot tell" and takes the part on: this comparison
    /// exists to spare untouched parts, never to drop an edit.</summary>
    private static MeshGltf.ParsedGlb? OpenBlenderReturnBaseline(string? comparisonGlb)
    {
        if (comparisonGlb is null) return null;
        try { return MeshGltf.ParsedGlb.Open(comparisonGlb); }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>Commit one prepared return: ONE authored transaction, synchronous and single-threaded on the
    /// window's own thread, so every mutation a return makes is ordered against every other one the app can
    /// make. Nothing here reads the game or re-exports a mesh — the plan already carries both — so what it
    /// costs is the publishes themselves.
    ///
    /// <para>A return is one modder action and it commits as one change. Everything it carries — the mints,
    /// the staged re-exports moving onto their ingress artifacts, the geometry and picture publishes, the
    /// per-submesh answers and the hides — goes into a single compound transaction, which is why an open-all
    /// costs the pages one board rebuild, the project one autosave and ③ Build one replan rather than one of
    /// each per answer inside it. That was the freeze: a fifteen-part send is around a hundred and thirty
    /// authored changes, and every one of them re-derived the whole app.</para>
    ///
    /// <para>All or nothing follows from the shape: a compound change that refuses anywhere commits no
    /// intent and takes its own files back out, so the status line's "Nothing was changed." is the
    /// mechanism rather than a hope. The counters below are only read once the batch has committed.</para>
    /// </summary>
    private void CommitBlenderReturn(IncomingEdit edit, AuthoredEditSession session,
        PreparedBlenderReturnPlan plan)
    {
        int published = 0;
        int pictures = 0;
        var committedTargets = new List<CommittedBlenderTarget>();
        var changedEditIds = new HashSet<string>(StringComparer.Ordinal);
        var notes = new List<string>();
        notes.AddRange(plan.Notes);
        notes.AddRange(plan.PartNotes.Values.SelectMany(part => part));
        // First, because it is the reason the counts below read the way they do.
        if (plan.BaselineUnreadable) notes.Add(BlenderReturnBaselineUnreadable);
        var globalWarnings = new List<string>(plan.Notes);
        if (plan.BaselineUnreadable) globalWarnings.Add(BlenderReturnBaselineUnreadable);
        // Asked of every considered part: an Object-mode-only move can be byte-identical and skipped, while
        // its transient status note still has to explain why nothing landed.
        // This re-opens the returned file outside the transaction below. It asks every considered part rather
        // than only landed parts because an Object-mode-only move can leave identical vertices and be skipped.
        var transformNotes = BlenderTransformNotesByPart(edit, plan.Considered);
        notes.AddRange(transformNotes.Values);
        try
        {
            session.Compound(change =>
            {
                // Before anything moves: every part this return has to open, asked at once, so a send the
                // game files cannot answer for names all of them rather than dying on the first.
                RequireReturnPartsHaveSomewhere(change, plan);
                foreach (var item in plan.Returns)
                    ForPart(item.Target.Part, () =>
                    {
                        string editId;
                        if (item.CreatesEdit)
                        {
                            EnsureReturnPartSlots(change, item.Part, item.Resolved);
                            editId = change.CreateEdit(item.Part, item.NewEditName);
                        }
                        else editId = item.ExistingEditId!;
                        if (!item.CreatesEdit)
                            RequireUnchangedBlenderMaterials(change.Slots(editId), item.Target);

                        var geometry = SessionGeometrySlot(change.Slots(editId), editId);
                        var ingress = item.LegacyIngress;
                        if (ingress is null)
                        {
                            string transportSource = File.Exists(item.Target.Workspace)
                                ? item.Target.Workspace : item.Staged!;
                            ingress = change.BeginIngress(editId, geometry.Slot.Id, transportSource);
                            TakeStagedBlenderReturn(item.Staged!, ingress.ReturnArtifact);
                        }
                        var lineage = item.Source ?? (ingress.SourceProjectAssetId is null
                            && geometry.Slot.Mesh is not null
                                ? new ProjectAssetSource { GameAsset = geometry.Slot.Mesh } : null);
                        var result = change.PublishAssetForBinding(ingress, ProjectAssetKind.Geometry,
                            item.Target.Part, ProjectAssetIngress.Binary, lineage,
                            item.Payload.Submeshes.Count);
                        if (result.Result == ProjectAssetPublishResult.Published) published++;
                        int picturesBefore = pictures;
                        PublishBlenderMaps(change, editId,
                            item.Payload.Submeshes.Count, item.Maps.Rows, item.Maps.Canonicalized,
                            () => pictures++, item.Maps.ReturnedMaps);
                        notes.AddRange(item.Maps.Notes);
                        bool changed = item.CreatesEdit
                            || result.Result == ProjectAssetPublishResult.Published
                            || pictures != picturesBefore;
                        if (changed)
                        {
                            changedEditIds.Add(editId);
                            change.SetReturnWarning(editId, ReturnWarningFor(item.Target.Part,
                                globalWarnings, plan.PartNotes, item.Maps.Notes, transformNotes));
                        }
                        committedTargets.Add(new CommittedBlenderTarget(item.Part, editId, ingress,
                            item.ComparisonWorkspace, item.SupersededIngressReturn));
                    });
                // A part the open-all offered and the modder emptied is hidden on a part the project may
                // never have opened, so its routes are minted first — a hide anchors on one of them.
                foreach (var hide in plan.Hides)
                    ForPart(hide.Target.Part,
                        () => EnsureReturnPartSlots(change, hide.Part, hide.Resolved));
                published += HideEmptiedParts(change, plan.Hides.Select(hide => hide.Part), (part, hideId) =>
                {
                    changedEditIds.Add(hideId);
                    change.SetReturnWarning(hideId, ReturnWarningFor(part.RendererSlot,
                        globalWarnings, plan.PartNotes, Array.Empty<string>(), transformNotes));
                });
            });
            if (RewriteCommittedBlenderSession(session, plan, edit.GlbPath, committedTargets) is { } rewriteNote)
            {
                notes.Add(rewriteNote);
                session.Compound(change =>
                {
                    foreach (string editId in changedEditIds) change.AppendReturnWarning(editId, rewriteNote);
                });
            }
            // A send-all hands back every part of the outfit, so a return that landed nothing is the
            // ordinary answer to opening the whole outfit and changing one thing in it — or none. Counting
            // out two zeroes describes a send that failed; this one describes what happened.
            EditPage.ReportStatus(BlenderReturnCounts(published, pictures, plan.UnchangedParts)
                + (notes.Count == 0 ? "" : " " + string.Join(" ", notes.Distinct())));
        }
        catch (Exception e)
        {
            AppLog.Write("Couldn't save the file sent back from Blender", e);
            // Same split the read makes: a refusal is the transaction's sentence for the modder, and a
            // per-field validator list or a model identifier is not.
            EditPage.ReportStatus(e is AuthoredRefusalException
                ? BlenderPublishFailure(e.Message) : BlenderPublishUnreadable);
        }
    }

    private static string? ReturnWarningFor(string part, IEnumerable<string> global,
        IReadOnlyDictionary<string, IReadOnlyList<string>> partNotes, IEnumerable<string> mapNotes,
        IReadOnlyDictionary<string, string> transformNotes)
    {
        var warnings = new List<string>(global);
        if (partNotes.TryGetValue(part, out var local)) warnings.AddRange(local);
        warnings.AddRange(mapNotes);
        if (transformNotes.TryGetValue(part, out string? transform)) warnings.Add(transform);
        var distinct = warnings.Where(note => !string.IsNullOrWhiteSpace(note)).Distinct().ToList();
        return distinct.Count == 0 ? null : string.Join(" ", distinct);
    }

    private static string? RewriteCommittedBlenderSession(AuthoredEditSession session,
        PreparedBlenderReturnPlan plan, string returnGlb,
        IReadOnlyList<CommittedBlenderTarget> committedTargets)
    {
        if (!plan.HasReadableSession || plan.SessionGlb is null) return null;
        try
        {
            var project = session.Snapshot();
            var updates = committedTargets.Select(committed =>
            {
                var slots = session.Slots(committed.EditId);
                var geometry = SessionGeometrySlot(slots, committed.EditId);
                var target = new BlenderSessionTarget(committed.Part.RendererSlot,
                    geometry.ProjectAsset?.Id ?? "", committed.ComparisonWorkspace,
                    committed.EditId, geometry.Slot.Id, committed.Ingress.ReturnArtifact,
                    geometry.Binding.Kind, BlenderMaterialBaselines(slots),
                    Subject: committed.Part.Subject, Outfit: committed.Part.Outfit);
                return new BlenderTargetAcknowledgement(target, committed.ComparisonWorkspace);
            }).ToList();
            bool rewritten = BlenderBridge.AcknowledgeReturn(plan.SessionGlb, returnGlb,
                document => document with
                {
                    Parts = document.Parts.Select(part =>
                    {
                        if (!part.IsWritable || !plan.SessionParts.TryGetValue(part.Name, out var target))
                            return part;
                        return part with
                        {
                            Edits = BlenderSessionEdits(project, target),
                            DefaultEditName = AuthoredEditSession.NewEditLabel(project, target, null),
                        };
                    }).ToList(),
                }, updates);
            if (!rewritten) return BlenderSessionRewriteFailure(plan.SessionGlb);
            foreach (string superseded in committedTargets.Select(target => target.SupersededIngressReturn)
                         .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                DeleteSupersededBlenderIngress(project.RootDir!, superseded);
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return BlenderSessionRewriteFailure(plan.SessionGlb);
        }
    }

    internal static string BlenderSessionRewriteFailure(string openedGlb) =>
        $"Could not update {Path.GetFileName(BlenderBridge.SessionPath(openedGlb))} after this send — "
        + "the Blender panel may offer stale targets until reopened.";

    /// <summary>Remove the leaf transport directory an exact open minted when this send deliberately landed
    /// somewhere else. The app-owned target row is still treated as untrusted path input: only a leaf under
    /// this project's ingress root may be removed. Cleanup is diagnostic-only and can never revoke an
    /// already-acknowledged return.</summary>
    internal static void DeleteSupersededBlenderIngress(string projectRoot, string returnArtifact,
        Action<string>? log = null) =>
        DeleteSupersededIngress(projectRoot, returnArtifact, "Blender ingress", log);

    private static void DeleteSupersededAssetIngress(string projectRoot, string returnArtifact) =>
        DeleteSupersededIngress(projectRoot, returnArtifact, "asset ingress", null);

    private static void DeleteSupersededIngress(string projectRoot, string returnArtifact, string description,
        Action<string>? log)
    {
        log ??= message => Debug.WriteLine(message);
        try
        {
            string ingressRoot = Path.GetFullPath(Path.Combine(projectRoot, ProjectAssetIngress.DirectoryName))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string returned = Path.GetFullPath(returnArtifact);
            string? directory = Path.GetDirectoryName(returned);
            if (directory is null || !directory.StartsWith(ingressRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(returned).StartsWith("return.", StringComparison.OrdinalIgnoreCase))
            {
                log($"Could not delete superseded {description} '{returnArtifact}': the path is outside "
                    + "this project's ingress root.");
                return;
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"Could not delete superseded {description} '{returnArtifact}': {e.Message}");
        }
    }

    /// <summary>Move a send-targeted row's staged re-export onto the ingress artifact the commit opened.
    /// The map record the re-export wrote beside it comes along, so the ingress folder ends up holding
    /// exactly what re-exporting straight into it would have left — which is the whole difference between
    /// this and doing the expensive half here.</summary>
    private static void TakeStagedBlenderReturn(string staged, string returnArtifact)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(returnArtifact)!);
        File.Copy(staged, returnArtifact, overwrite: true);
        string record = PreviewMaps.SidecarPath(staged);
        if (File.Exists(record))
            File.Copy(record, PreviewMaps.SidecarPath(returnArtifact), overwrite: true);
    }

    /// <summary>Freeze one prepared part workspace while all map files its record names still exist.
    /// <see cref="PreviewMaps.CopyPortableWorkspace"/> copies those dependencies under this directory, so
    /// acknowledgement can promote it after map publishing consumes preparation files.</summary>
    private static string PrepareBlenderComparisonWorkspace(string source, string stagingRoot, string part)
    {
        string directory = Path.Combine(stagingRoot, "acknowledgement",
            StorageName(part) + "-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(directory, "workspace.glb");
        PreviewMaps.CopyPortableWorkspace(source, destination);
        return destination;
    }

    /// <summary>Drop a return's staging folder. Best-effort: what it holds is normalized copies the publish
    /// has already taken, so a folder that will not go costs disk, never an edit.</summary>
    private static void DeleteBlenderStaging(string? stagingRoot)
    {
        if (stagingRoot is null) return;
        try { Directory.Delete(stagingRoot, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Emptying a part in Blender and sending it back says the part is not to draw, so the return
    /// puts the part's hide in Always. That is this route's own intent rather than the library's activation
    /// rule: a part whose content edit is already used would otherwise take its hide into the library, where
    /// nothing selects it and the send would change nothing at all.
    ///
    /// <para>A hide already used in Always is left where it is, so sending the same emptied part twice
    /// changes nothing the second time. Returns how many parts this send hid.</para></summary>
    internal static int HideEmptiedParts(AuthoredEditSession.CompoundChange change,
        IEnumerable<TargetPart> parts, Action<TargetPart, string>? changed = null)
    {
        int hidden = 0;
        foreach (var part in parts)
        {
            bool standing = change.HasPlacedHide(part);
            string hideId = change.AddHideEdit(part);
            if (!change.IsPlacedAlways(hideId)) change.PlaceEdit(hideId);
            if (!standing)
            {
                hidden++;
                changed?.Invoke(part, hideId);
            }
        }
        return hidden;
    }

    /// <summary>The line a return owes when the file it was exported FROM was named and could not be
    /// opened — a mod folder renamed out from under a session still open in Blender, a run folder cleaned
    /// by hand. Every part then comes back with nothing to compare it to and is saved, so a modder who
    /// changed one part of an outfit and is handed fifteen edits has the reason in front of them rather
    /// than a mod full of parts they never touched.</summary>
    internal const string BlenderReturnBaselineUnreadable =
        "Couldn't read the file opened in Blender, so every part sent back was saved, "
        + "not only the changed ones.";

    /// <summary>The line a return owes for each part whose Object-mode transform the geometry read dropped.
    /// Blender writes that transform on the glb NODE for a mesh with no skin, and the read takes vertex
    /// positions alone — so the part arrives exactly as it always did, minus the placement the modder can
    /// see in the viewport. The mesh is not at risk here; the silence is what this ends.
    ///
    /// <para>Gated on the flag the read already raised, so an ordinary return never re-opens the file, and
    /// resolved PER PART: one moved part in a multi-part send must not report on its siblings. A file that
    /// won't re-open says nothing rather than costing a landed edit its report.</para></summary>
    internal static IReadOnlyList<string> BlenderTransformNotes(IncomingEdit edit, IEnumerable<string> parts)
        => BlenderTransformNotesByPart(edit, parts).Values.ToList();

    private static IReadOnlyDictionary<string, string> BlenderTransformNotesByPart(IncomingEdit edit,
        IEnumerable<string> parts)
    {
        if (!edit.NodeTransformIgnored)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> moved;
        try { moved = MeshGltf.MeshesWithNodeTransform(edit.GlbPath).ToHashSet(StringComparer.Ordinal); }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
        return parts.Where(moved.Contains).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(part => part,
                part => $"The Object-mode position or scale on {part} was not applied. "
                    + "Apply the transform in Blender (Ctrl+A), then send it back.",
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What a return says when the transaction refuses. It reports no partial landing because
    /// there is none: the whole return is one compound change, so a refusal anywhere leaves the mod exactly
    /// as it was — the same closing sentence a return that could not be READ already gives.</summary>
    internal static string BlenderPublishFailure(string reason) =>
        $"Stopped while saving the file sent back from Blender: {reason.TrimEnd().TrimEnd('.')}. "
        + "Nothing was changed.";

    /// <summary>…and when the transaction refused for a reason it has no words for. Same closing sentence,
    /// because the outcome is the same one: the mod is exactly as it was.</summary>
    internal const string BlenderPublishUnreadable =
        "Stopped while saving the file sent back from Blender. Nothing was changed.";

    public void ShowSubjectFolder(string subject, string outfit)
    {
        if (EditSession?.Snapshot().RootDir is not { } root) return;
        string edits = Path.Combine(root, "assets", "edits");
        string destination = Directory.Exists(edits) ? edits : root;
        try { Process.Start(new ProcessStartInfo { FileName = destination, UseShellExecute = true }); }
        catch { }
    }

    public async Task RemoveSubjectAsync(string subject, string outfit)
    {
        if (EditSession is not { } session) return;
        var project = session.Snapshot();
        int edits = project.EditDefinitions.Count(edit =>
            string.Equals(edit.Target.Subject, subject, StringComparison.OrdinalIgnoreCase)
            && string.Equals(edit.Target.Outfit, outfit, StringComparison.OrdinalIgnoreCase));
        // Named through the app's one naming home, which is what the row being removed is named by. The
        // internal character key and outfit stem are the model's address for this item, and a question
        // about "Cheyanne_01 · Char_Cheyanne_Swim01" is a question about a row the modder cannot see.
        string label = SubjectLabel(subject, outfit);
        if (edits > 0 && !await ConfirmAsync($"Remove {label}?", RemoveSubjectConfirmBody(edits),
                "Remove", dangerous: true)) return;
        session.ForgetSubject(subject, outfit);
        SyncSubjectsFromLedger();
        EditPage.ReportStatus($"Removed {label} from the mod.");
    }

    public async Task<EditPictureOpenResult> OpenPictureAsync(EditSlotRef slot, IProgress<string> status,
        bool confirmed = false, EditTextureSharingOffer? offered = null)
    {
        var launch = TextureSharingAt(slot);
        if (EditMapCardVm.RefusalFor(launch.Kind) is { } refused)
        { status.Report(refused); return EditPictureOpenResult.NotLaunched; }
        if (!confirmed && launch.Kind == EditTextureSharing.Shared)
        {
            if (!await ConfirmAsync("Edit this map?",
                    $"{EditMapCardVm.MapOnMaterial(slot)}.\n\n"
                    + EditMapCardVm.SharedConsequence(launch.Uses!.Value), "Edit"))
                return EditPictureOpenResult.NotLaunched;
        }

        TextureSharingSnapshot? shown = offered is { } cardOffer
            ? new TextureSharingSnapshot(cardOffer.Kind, cardOffer.Uses)
            : confirmed ? null : launch;
        bool sharedConsent = confirmed
            ? offered is { Kind: EditTextureSharing.Shared }
            : launch.Kind == EditTextureSharing.Shared;
        var ingress = await BeginPictureIngressAsync(slot, null, status, shown, sharedConsent);
        if (ingress is null) return EditPictureOpenResult.NotLaunched;
        PictureIngressOpenedForTests?.Invoke(ingress);

        PictureTransportWatcher? watcher = null;
        try
        {
            watcher = new PictureTransportWatcher(ingress.Session.OutboundSnapshot,
                () => OnUi(() => PublishPictureReturn(ingress, status)),
                message => OnUi(() => status.Report(
                    $"Stopped watching for saves from the image editor: {message} Use Open on the card again.")));
            _pictureTransports.Add(watcher);
            if (!LaunchInImageEditor(ingress.Session.OutboundSnapshot,
                $"Opened {ingress.Label} in the image editor. Save to send it back.",
                "Couldn't open the image editor", status))
            {
                _pictureTransports.Remove(watcher);
                watcher.Dispose();
                return EditPictureOpenResult.NotLaunched;
            }
            return EditPictureOpenResult.LaunchedWithoutSave;
        }
        catch (Exception e)
        {
            if (watcher is not null)
            {
                _pictureTransports.Remove(watcher);
                watcher.Dispose();
            }
            status.Report($"Couldn't prepare the image editor: {Reason(e)}");
            return EditPictureOpenResult.NotLaunched;
        }
    }

    /// <summary>The map card's own Browse: the same file dialog ③ Build's preview surface opens, filtered to
    /// the one file type a card takes. The pick is handed straight back — what lands where is the page's
    /// drop route, which asks every question a dragged file is asked.</summary>
    public async Task<string?> PickPictureAsync()
    {
        if (MainWindow is not { } owner) return null;
        var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a .png",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("PNG images") { Patterns = new[] { "*.png" } },
            },
        });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task OpenUvGuideAsync(EditSlotRef slot, IProgress<string> status)
    {
        if (SubjectPartOf(slot.Edit.Part) is not { } part)
        { status.Report(SubjectReadFailure(slot.Edit.Part.Subject, slot.Edit.Part.Outfit)); return; }
        if (EditSession?.Snapshot() is not { RootDir: { } root } project)
        { status.Report("Couldn't find the mod folder."); return; }

        var route = UvGuideRouteFor(slot, EditSession, project);
        if (route.MissingGeometry is { } missing)
        { status.Report(GeometryFileMissing(missing)); return; }

        SubjectMap? map = GameMapFor(slot);
        string textureName;
        string textureBundle;
        string guideSource;
        (int Width, int Height)? canvasSize = null;
        if (map is not null)
        {
            textureName = map.TextureName;
            textureBundle = map.BundleId;
            string subject = ModNaming.SubjectSlug(slot.Edit.Part.Subject, slot.Edit.Part.Outfit);
            bool nameCollision = part.Materials.SelectMany(material => material.Maps).Any(other =>
                other.PathId != map.PathId
                && string.Equals(other.BundleId, map.BundleId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(other.TextureName, map.TextureName, StringComparison.Ordinal));
            guideSource = nameCollision
                ? TextureExport.BundleScopedName(map.BundleId, map.TextureName, subject, map.PathId)
                : TextureExport.BundleScopedName(map.BundleId, map.TextureName, subject);
        }
        else if (route.ReplacementName is { } replacementName)
        {
            textureName = replacementName;
            if (route.CanvasSize is not { } size)
            { status.Report($"Couldn't read {textureName}, so its UV guide cannot be sized."); return; }
            canvasSize = size;
            textureBundle = "";
            guideSource = route.GuideSource!;
        }
        else { status.Report(UvGuideNeedsGameTexture); return; }

        var recipe = part.ToRecipePart();
        var samplers = new List<(string MeshName, string MeshAddress, int Submesh, string? ModdedGlb)>
        {
            // The empty edit id on a bare part deliberately supplies no edit slots. A replacement card uses
            // the edit's own glb, so its guide follows the layout the build ships.
            (recipe.SlotName, recipe.MeshAddress, route.Submesh, route.ModdedGlb),
        };
        string guide = AssetExporter.UvGuidePathFor(Path.Combine(root, ProjectAssetIngress.DirectoryName,
            "guides", guideSource));
        // The effect overlay samples the mesh's SECOND UV set — measured: UV1 exists exactly on the
        // parts that bind _BlendTex, laid out independently of UV0 — so its guide plots that channel.
        // The same evidenced mapping stamps the Blender texture transport; generic properties keep this
        // guide's historical UV0 fallback without gaining a guessed transport coordinate.
        string channel = UvGuide.TexCoordChannel(slot.Input);
        if (_vfs is { } vfs)
        {
            status.Report($"Drawing the UV guide for {textureName}…");
            var problem = await Task.Run(() => AssetExporter.BuildUvGuideOnDemand(vfs, textureName,
                textureBundle, samplers, guide, channel, canvasSize));
            if (problem is not null) { status.Report(problem); return; }
        }
        else if (!File.Exists(guide))
        { status.Report("Game files aren't loaded yet. Try again in a moment."); return; }
        LaunchInImageEditor(guide,
            $"Opened the UV guide for {textureName}. Layer it under the paint.",
            "Couldn't open the UV guide", status);
    }

    internal sealed record UvGuideCardRoute(int Submesh, string? ModdedGlb, string? MissingGeometry,
        string? ReplacementName, string? GuideSource, (int Width, int Height)? CanvasSize);

    /// <summary>The file-and-card half of the UV route. A bare part carries an empty edit id, so it must
    /// never be sent to <see cref="AuthoredEditSession.Slots"/>. A replacement picture contributes its own
    /// file name and canvas size while the edit's geometry contributes the layout.</summary>
    internal static UvGuideCardRoute UvGuideRouteFor(EditSlotRef slot, AuthoredEditSession? session,
        AuthoredProject project)
    {
        IReadOnlyList<EditSlotState>? editSlots = slot.Edit.EditDefinitionId.Length == 0
            ? null : session?.Slots(slot.Edit.EditDefinitionId);
        var geometry = GeometryFile(editSlots, project.RootDir);
        string? replacementName = null;
        string? guideSource = null;
        (int Width, int Height)? canvasSize = null;
        if (slot.Domain == TargetSlotDomain.EditOutput
            && slot.Binding == BindingKind.ProjectAsset
            && slot.ProjectRelativeFile is { Length: > 0 } relative)
        {
            string own = project.Resolve(relative);
            replacementName = Path.GetFileName(relative);
            guideSource = Path.GetFileName(own);
            canvasSize = PngInfo.TrySize(own);
        }
        return new UvGuideCardRoute(slot.SubmeshIndex ?? slot.MaterialSlotIndex ?? 0,
            geometry.Path, geometry.Missing, replacementName, guideSource, canvasSize);
    }

    /// <summary>A guide is drawn against the GAME texture's UV layout, so a slot with no game texture behind
    /// it has nothing to draw one from.</summary>
    internal const string UvGuideNeedsGameTexture =
        "There is no game texture for this map, so no UV guide can be drawn.";

    /// <summary>Show the toon-ramp pick list for one card. The rows, the refusals and the write are the
    /// shipped picker's; what comes back here is what was chosen — a published file, or the pinned
    /// keep-the-game's-own row — which the page turns into a binding.</summary>
    public async Task<EditRampPick?> PickRampAsync(EditSlotRef slot)
    {
        if (EditSession is not { } session) return null;
        var candidates = SessionRampCandidates(slot);
        var picker = new SessionRampPickerVm(
            ct => StreamSessionRamps(candidates, slot.ProjectRelativeFile, ct),
            RampImage.RefuseAsRamp,
            // The material, named as the card group over the button names it: an install that supplies no
            // name leaves the same positional label the cards and every confirm use.
            $"{slot.MaterialName ?? $"material {slot.MaterialSlotIndex ?? 0}"} · {PartToken(slot.Edit.Part)}")
        {
            ExportTo = (choice, path) => Task.Run(() => ExportSessionRamp(choice, path)),
        };
        if (await PickRampAsync(picker) is not { } choice) return null;
        if (choice.IsKeepOwn) return new EditRampPick(null);

        var snapshot = session.Snapshot();
        string? temporary = null;
        string source;
        ProjectAssetSource? lineage = null;
        if (choice.File is { } file)
        {
            // The picker's own refusal, written for the screen: it surfaces as it is rather than as the
            // verb's name, which is all a raw failure would leave.
            if (RampImage.RefuseAsRamp(file) is { } refusal) throw new AuthoredRefusalException(refusal);
            source = file;
            var chosenAsset = snapshot.ProjectAssets.FirstOrDefault(asset =>
            {
                try
                {
                    return string.Equals(Path.GetFullPath(Path.Combine(snapshot.RootDir!, asset.File)),
                        Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });
            if (chosenAsset is not null)
                lineage = new ProjectAssetSource { ProjectAssetId = chosenAsset.Id };
        }
        else
        {
            temporary = Path.Combine(snapshot.RootDir!, ProjectAssetIngress.DirectoryName, "sources",
                Guid.NewGuid().ToString("N") + ".dds");
            WriteGameRamp(choice, temporary);
            source = temporary;
            lineage = new ProjectAssetSource { GameAsset = new GameAssetRef
            {
                GameBuild = _vfs?.CatalogVersion ?? "unknown", LogicalBundle = choice.Bundle!,
                PathId = choice.PathId, Name = choice.Texture,
            } };
        }
        try
        {
            if (IsBareCard(slot))
            {
                var first = await Task.Run(() => PublishFirstEditAsset(session, slot, source,
                    ProjectAssetKind.Ramp, choice.Texture ?? Path.GetFileNameWithoutExtension(source),
                    ProjectAssetIngress.Binary, lineage));
                return new EditRampPick(first);
            }
            var ingress = ProjectAssetIngress.Begin(snapshot, slot.Edit.EditDefinitionId, slot.SlotId, source);
            var result = await Task.Run(() => session.PublishAssetForBinding(ingress,
                ProjectAssetKind.Ramp, choice.Texture ?? Path.GetFileNameWithoutExtension(source),
                ProjectAssetIngress.Binary, lineage));
            return result.Result == ProjectAssetPublishResult.Published
                ? new EditRampPick(new EditAssetResult(result.ProjectRelativeFile!,
                    choice.Texture ?? Path.GetFileNameWithoutExtension(source)))
                : null;
        }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch { } }
    }

    private sealed record SessionRampCandidate(SessionRampChoice Choice, SessionRampChoice ReadFrom,
        List<string> Labels, bool Own = false);

    private List<SessionRampCandidate> SessionRampCandidates(EditSlotRef slot)
    {
        var found = new List<SessionRampCandidate>();
        var own = GameMapFor(slot);
        var ownRead = own is null ? SessionRampChoice.KeepOwn
            : new SessionRampChoice(own.BundleId, own.TextureName, null, own.PathId);
        found.Add(new SessionRampCandidate(SessionRampChoice.KeepOwn, ownRead,
            new List<string> { "Original" }, Own: true));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_subjectModels.TryGet(slot.Edit.Part.Subject, slot.Edit.Part.Outfit) is { } model)
            foreach (var part in model.Parts)
                for (int materialIndex = 0; materialIndex < part.Materials.Count; materialIndex++)
                    foreach (var map in part.Materials[materialIndex].Maps.Where(map =>
                                 InputOfSlot(map.Slot) == TargetInputKind.Ramp))
                    {
                        string key = $"{map.BundleId}|{(map.PathId == 0 ? map.TextureName : map.PathId)}";
                        string label = ReferenceEquals(map, own) ? "This material"
                            : $"{part.Token} · {part.Materials[materialIndex].Name}";
                        if (!seen.Add(key))
                        {
                            var existing = found.First(candidate => !candidate.Own
                                && candidate.ReadFrom.Bundle == map.BundleId
                                && candidate.ReadFrom.PathId == map.PathId
                                && candidate.ReadFrom.Texture == map.TextureName);
                            if (!existing.Labels.Contains(label, StringComparer.Ordinal)) existing.Labels.Add(label);
                            continue;
                        }
                        var choice = new SessionRampChoice(map.BundleId, map.TextureName, null, map.PathId);
                        found.Add(new SessionRampCandidate(
                            choice, choice, new List<string> { label }));
                    }
        if (EditSession?.Snapshot() is { RootDir: { } root } project)
            foreach (var asset in project.ProjectAssets.Where(asset => asset.Kind == ProjectAssetKind.Ramp))
            {
                string file = Path.GetFullPath(Path.Combine(root, asset.File));
                if (File.Exists(file) && seen.Add(file))
                {
                    var choice = new SessionRampChoice(null, null, file);
                    found.Add(new SessionRampCandidate(choice, choice,
                        new List<string> { !string.IsNullOrWhiteSpace(asset.Label)
                            ? asset.Label : Path.GetFileName(file) }));
                }
            }
        return found;
    }

    private SessionRampPickLoad ReadSessionRamps(IReadOnlyList<SessionRampCandidate> candidates,
        string? boundFile, System.Threading.CancellationToken token)
    {
        var decoded = new List<SessionRampReadCandidate>();
        string? bound = boundFile is null || EditProjectRoot is null ? null
            : Path.GetFullPath(Path.Combine(EditProjectRoot, boundFile));
        foreach (var candidate in candidates)
        {
            if (token.IsCancellationRequested) break;
            try
            {
                RampImage.Read? read;
                if (candidate.ReadFrom.IsKeepOwn) read = null;
                else if (candidate.ReadFrom.File is { } file) read = RampImage.ReadDds(file);
                else
                {
                    byte[] bytes = TryDeobfuscateBundle(candidate.ReadFrom.Bundle!)
                        ?? throw new InvalidDataException("bundle unavailable");
                    var raw = new BundleReader().GetTextureHashSource(bytes, candidate.ReadFrom.Ref)
                        ?? throw new InvalidDataException("ramp unavailable");
                    int size = checked(raw.Width * raw.Height * 8);
                    read = new RampImage.Read(raw.Width, raw.Height, raw.PictureData[..size]);
                }
                decoded.Add(new SessionRampReadCandidate(candidate.Choice, candidate.Labels, candidate.Own,
                    candidate.Own ? bound is null : candidate.Choice.File is { } path && bound is not null
                        && string.Equals(Path.GetFullPath(path), bound, StringComparison.OrdinalIgnoreCase), read));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (candidate.Own)
                    decoded.Add(new SessionRampReadCandidate(candidate.Choice, candidate.Labels,
                        IsOwn: true, IsBound: bound is null, Image: null));
            }
        }
        return SessionRampRows.Fold(decoded);
    }

    private async IAsyncEnumerable<SessionRampPickLoad> StreamSessionRamps(
        IReadOnlyList<SessionRampCandidate> candidates, string? boundFile,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var decoded = new List<SessionRampReadCandidate>();
        var pendingGame = new List<SessionRampCandidate>();
        var pendingFiles = new List<SessionRampCandidate>();
        // Each candidate's offered position, so a row landing out of a later bundle still settles where
        // the candidate list put it.
        var order = new Dictionary<(bool Own, SessionRampChoice Choice), int>();
        for (int i = 0; i < candidates.Count; i++)
            order[(candidates[i].Own, candidates[i].Choice)] = i;
        string? bound = boundFile is null || EditProjectRoot is null ? null
            : Path.GetFullPath(Path.Combine(EditProjectRoot, boundFile));
        object? install = _vfs;
        string catalogVersion = _vfs?.CatalogVersion ?? "unknown";

        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            bool isBound = candidate.Own ? bound is null : candidate.Choice.File is { } path
                && bound is not null && string.Equals(Path.GetFullPath(path), bound,
                    StringComparison.OrdinalIgnoreCase);
            if (candidate.ReadFrom.IsKeepOwn)
            {
                decoded.Add(new SessionRampReadCandidate(candidate.Choice, candidate.Labels,
                    candidate.Own, isBound, null));
            }
            else if (candidate.ReadFrom.File is not null)
            {
                if (isBound) decoded.Add(new SessionRampReadCandidate(candidate.Choice,
                    candidate.Labels, candidate.Own, isBound, null));
                pendingFiles.Add(candidate);
            }
            else if (install is not null && _rampCache.TryGet(install, catalogVersion,
                         candidate.ReadFrom, out var cached) && cached is not null)
            {
                decoded.Add(new SessionRampReadCandidate(candidate.Choice, candidate.Labels,
                    candidate.Own, isBound, cached.Read, cached.PreviewPng));
            }
            else
            {
                if (candidate.Own || isBound) decoded.Add(new SessionRampReadCandidate(candidate.Choice,
                    candidate.Labels, candidate.Own, isBound, null));
                pendingGame.Add(candidate);
            }
        }

        yield return SessionRampRows.Fold(decoded);

        foreach (var group in pendingGame.GroupBy(candidate => candidate.ReadFrom.Bundle!,
                     StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            byte[]? bytes = await Task.Run(() => TryDeobfuscateBundle(group.Key), token);
            if (bytes is not null)
            {
                foreach (var candidate in group)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var raw = new BundleReader().GetTextureHashSource(bytes, candidate.ReadFrom.Ref)
                            ?? throw new InvalidDataException("ramp unavailable");
                        int size = checked(raw.Width * raw.Height * 8);
                        var read = new RampImage.Read(raw.Width, raw.Height, raw.PictureData[..size]);
                        byte[]? preview = SessionRampRows.RenderPreview(read);
                        if (install is not null) _rampCache.Store(install, catalogVersion,
                            candidate.ReadFrom, new InstallRampCache.Entry(read, preview));
                        UpsertRamp(decoded, order, candidate, bound, read, preview);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { }
                }
            }
            yield return SessionRampRows.Fold(decoded);
        }

        foreach (var candidate in pendingFiles)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var read = await Task.Run(() => RampImage.ReadDds(candidate.ReadFrom.File!), token);
                UpsertRamp(decoded, order, candidate, bound, read, SessionRampRows.RenderPreview(read));
            }
            catch (Exception e) when (e is not OutOfMemoryException) { }
            yield return SessionRampRows.Fold(decoded);
        }
    }

    /// <summary>Land one finished read into the streamed row list AT ITS CANDIDATE'S POSITION. A landing
    /// replaces the pending placeholder in place, and a row with no placeholder inserts where candidate
    /// order puts it — so the settled picker reads in the same order the candidates were offered, exactly
    /// as the pre-streaming fold did, instead of bundle-completion order.</summary>
    private static void UpsertRamp(List<SessionRampReadCandidate> decoded,
        IReadOnlyDictionary<(bool Own, SessionRampChoice Choice), int> order,
        SessionRampCandidate candidate, string? bound, RampImage.Read read, byte[]? preview)
    {
        bool isBound = candidate.Own ? bound is null : candidate.Choice.File is { } path
            && bound is not null && string.Equals(Path.GetFullPath(path), bound,
                StringComparison.OrdinalIgnoreCase);
        var row = new SessionRampReadCandidate(candidate.Choice, candidate.Labels,
            candidate.Own, isBound, read, preview);
        int existing = decoded.FindIndex(item => item.IsOwn == candidate.Own
            && item.Choice == candidate.Choice);
        if (existing >= 0) { decoded[existing] = row; return; }
        int rank = order.GetValueOrDefault((candidate.Own, candidate.Choice), int.MaxValue);
        int insertAt = decoded.FindIndex(item =>
            order.GetValueOrDefault((item.IsOwn, item.Choice), int.MaxValue) > rank);
        if (insertAt < 0) decoded.Add(row); else decoded.Insert(insertAt, row);
    }

    private string? ExportSessionRamp(SessionRampChoice choice, string destination)
    {
        try
        {
            if (choice.File is { } file) File.Copy(file, destination, overwrite: true);
            else WriteGameRamp(choice, destination);
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { return $"Couldn't export the toon ramp: {Reason(e)}"; }
    }

    private void WriteGameRamp(SessionRampChoice choice, string destination)
    {
        // Both of these are for the modder: the game files could not give the ramp up. They surface as they
        // are, and neither names the bundle it came from — which is the app's own address for it.
        byte[] bytes = TryDeobfuscateBundle(choice.Bundle!)
            ?? throw new AuthoredRefusalException("Couldn't read this toon ramp from the game files.");
        var raw = new BundleReader().GetTextureHashSource(bytes, choice.Ref)
            ?? throw new AuthoredRefusalException($"{choice.Texture} isn't in the game files any more.");
        if (raw.Width != Core.Migoto.RampConversion.RampWidth
            || raw.Height != Core.Migoto.RampConversion.RampHeight
            || !Core.Migoto.RampConversion.WriteRaw(raw, destination))
            throw new InvalidDataException($"{choice.Texture} is not a 256×16 RGBAHalf toon ramp");
    }

    public async Task<EditAssetResult?> AcceptDroppedPictureAsync(EditSlotRef slot, string path,
        IProgress<string> status, bool confirmed = false, EditTextureSharingOffer? offered = null)
    {
        if (!File.Exists(path)) { status.Report($"{Path.GetFileName(path)} is no longer there."); return null; }
        string name = Path.GetFileName(path);
        if (!slot.HasDrawableCarrier)
        { status.Report(EditMapCardVm.NoDrawableCarrier); return null; }
        // Asked before the question rather than only at the bind below, so a picture that cannot land is
        // never confirmed first.
        var launch = TextureSharingAt(slot);
        if (EditMapCardVm.RefusalFor(launch.Kind) is { } refused)
        { status.Report(refused); return null; }
        // The map AND its material, named exactly as the line that reports the result names them, so the
        // question and its answer are about the same thing — and so that the four base-colour cards of a
        // four-material part do not raise one identical dialog between them.
        string consequence = launch.Kind == EditTextureSharing.Shared
            ? "\n\n" + EditMapCardVm.SharedConsequence(launch.Uses!.Value)
            : "";
        if (!confirmed && !await ConfirmAsync($"Apply {name}?",
                $"{name} becomes this edit's {EditMapCardVm.MapInSentence(slot)}.{consequence}",
                "Apply")) return null;

        TextureSharingSnapshot? shown = offered is { } cardOffer
            ? new TextureSharingSnapshot(cardOffer.Kind, cardOffer.Uses)
            : confirmed ? null : launch;
        bool sharedConsent = confirmed
            ? offered is { Kind: EditTextureSharing.Shared }
            : launch.Kind == EditTextureSharing.Shared;
        if (launch.Kind == EditTextureSharing.Shared
            && (!sharedConsent || shown is not { Kind: EditTextureSharing.Shared, Uses: { } shownUses }
                || launch.Uses != shownUses))
        { status.Report(EditMapCardVm.SharedConsentRequired(launch.Uses!.Value)); return null; }
        if (IsBareCard(slot))
        {
            if (EditSession is not { } firstSession) return null;
            try
            {
                return await Task.Run(() => PublishFirstEditAsset(firstSession, slot, path,
                    ProjectAssetKind.Picture, Path.GetFileNameWithoutExtension(path),
                    ProjectAssetIngress.Png, null));
            }
            catch (AuthoredRefusalException e)
            {
                status.Report(e.Message);
                return null;
            }
            catch (Exception e)
            {
                status.Report($"{name} couldn't be read as an image. {e.Message}");
                return null;
            }
        }
        var ingress = await BeginPictureIngressAsync(slot, path, status, shown, sharedConsent);
        if (ingress is null) return null;
        try
        {
            var published = await Task.Run(() => ingress.Owner.PublishAssetForBinding(ingress.Session,
                ProjectAssetKind.Picture, ingress.Label, ProjectAssetIngress.Png, ingress.Source));
            return published.Result == ProjectAssetPublishResult.Published
                ? new EditAssetResult(published.ProjectRelativeFile!, ingress.Label) : null;
        }
        catch (Exception e)
        {
            status.Report($"{name} couldn't be read as an image. {Reason(e)}");
            return null;
        }
    }

    /// <summary>The project file one card's picture lives in, materializing the game texture into the project
    /// on first touch. Hands back the project-relative path; the page is what binds it.</summary>
    /// <param name="Slot">The exact place this transport was opened for. Carried because the image editor's
    /// saves come back through it minutes or hours later, and the check that let the transport open has to
    /// be re-askable about the same slot when they do.</param>
    internal sealed class PictureIngress
    {
        internal PictureIngress(EditSlotRef Slot, AuthoredEditSession Owner,
            ProjectAssetIngressSession Session, string Label, ProjectAssetSource? Source,
            EditTextureSharing LaunchSharing = EditTextureSharing.Private,
            int? LaunchUses = null, bool SharedConsent = false)
        {
            this.Slot = Slot;
            this.Owner = Owner;
            this.Session = Session;
            this.Label = Label;
            this.Source = Source;
            this.LaunchSharing = LaunchSharing;
            this.LaunchUses = LaunchUses;
            this.SharedConsent = SharedConsent;
        }

        internal EditSlotRef Slot { get; }
        internal AuthoredEditSession Owner { get; }
        internal ProjectAssetIngressSession Session { get; }
        internal string Label { get; }
        internal ProjectAssetSource? Source { get; }
        internal EditTextureSharing LaunchSharing { get; }
        internal int? LaunchUses { get; }
        internal bool SharedConsent { get; }
        internal EditSlotRef? LandedSlot { get; set; }
    }

    internal readonly record struct TextureSharingSnapshot(EditTextureSharing Kind, int? Uses);

    /// <summary>The sharing answer for one slot right now. A measured answer can proceed directly or after
    /// shared consent; an unmeasured answer carries the matching game-files refusal.
    ///
    /// <para>The page asks the same question when it draws a card. The bind asks again because the item's
    /// read can land after that redraw; an accepted shared question carries the answer it showed so a larger
    /// live reach cannot inherit consent for a smaller one. Once the live slot binds the mod's own file, the
    /// boundary deliberately leaves it alone.</para>
    ///
    /// <para>Asked through the same rule the card is drawn from, so the gate and the last check cannot
    /// disagree about which slots are covered or about what a use count means.</para>
    ///
    /// <para>ONE window is left open and taken knowingly: between this answer and the bind that follows it
    /// there is a decode-and-copy on a worker, and a read landing inside those milliseconds is not seen. It
    /// is a window at the end of a chain of them, each closed by the next check, and closing this last one
    /// would mean holding the model still across a file copy — a lock on the install for the length of an
    /// image write, to catch a read that would have to land inside it. What lands there is a picture bound
    /// to a texture measured a moment before, which the next redraw shows the modder in full.</para>
    /// </summary>
    private TextureSharingSnapshot TextureSharingAt(EditSlotRef slot)
    {
        int? uses = TextureUses(slot);
        return new TextureSharingSnapshot(EditMapCardVm.SharingFor(slot,
            SubjectRead(slot.Edit.Part), uses), uses);
    }

    internal static string BlenderReturnCounts(int edits, int images, int unchangedParts)
    {
        string result = edits == 0 && images == 0
            ? BlenderReturnNoChanges.TrimEnd('.')
            : $"Blender sent back {edits} edit{(edits == 1 ? "" : "s")} and "
              + $"{images} changed image{(images == 1 ? "" : "s")}";
        if (unchangedParts > 0)
            result += $" · {unchangedParts} unchanged part{(unchangedParts == 1 ? "" : "s")}";
        return result + ".";
    }

    private static bool IsBareCard(EditSlotRef slot) =>
        string.IsNullOrEmpty(slot.Edit.EditDefinitionId) && string.IsNullOrEmpty(slot.SlotId);

    private static EditSlotState FirstEditSlot(IEnumerable<EditSlotState> slots, EditSlotRef bare) =>
        slots.FirstOrDefault(state => state.Slot.Domain == TargetSlotDomain.Game
            && state.Slot.Input == bare.Input
            && string.Equals(state.Slot.ShaderProperty, bare.ShaderProperty, StringComparison.Ordinal)
            && (state.Slot.MaterialSlotIndex ?? state.Slot.SubmeshIndex) == bare.MaterialSlotIndex)
        ?? throw new AuthoredRefusalException("This map is no longer in the game files.");

    internal EditAssetResult PublishFirstEditAsset(AuthoredEditSession session, EditSlotRef bare,
        string source, ProjectAssetKind kind, string label, ProjectAssetNormalization normalization,
        ProjectAssetSource? lineage)
    {
        var snapshot = session.Snapshot();
        bool hasSlots = snapshot.TargetSlots.Any(slot => slot.Part.SameAs(bare.Edit.Part));
        var resolved = hasSlots ? null : ResolvePart(bare.Edit.Part)
            ?? throw new AuthoredRefusalException(
                "This part isn't in the current game files, so there is nowhere to record its values.");
        string handover = Path.Combine(snapshot.RootDir!, ProjectAssetIngress.DirectoryName, "sources",
            Guid.NewGuid().ToString("N") + Path.GetExtension(source));
        Directory.CreateDirectory(Path.GetDirectoryName(handover)!);
        File.Copy(source, handover, overwrite: false);
        EditSlotRef? landed = null;
        ExactAssetPublishResult? published = null;
        string? transientReturn = null;
        try
        {
            session.Compound(change =>
            {
                if (!change.HasPartSlots(bare.Edit.Part)) change.EnsurePartSlots(bare.Edit.Part, resolved);
                string editId = change.CreateEdit(bare.Edit.Part);
                var state = FirstEditSlot(change.Slots(editId), bare);
                var edit = new EditRef(bare.Edit.Part, editId, "");
                landed = new EditSlotRef(edit, state.Slot.Id, state.Slot.Input, state.Slot.Domain,
                    state.Slot.MaterialSlotIndex, state.Slot.Material?.Name, null, state.Binding.Kind,
                    SubmeshIndex: state.Slot.SubmeshIndex, ShaderProperty: state.Slot.ShaderProperty,
                    HasDrawableCarrier: bare.HasDrawableCarrier);
                var ingress = change.BeginIngress(editId, state.Slot.Id, handover, handOver: true);
                transientReturn = ingress.ReturnArtifact;
                published = change.PublishAssetForBinding(ingress, kind, label, normalization, lineage);
            });
        }
        finally { try { File.Delete(handover); } catch { } }
        DeleteSupersededAssetIngress(snapshot.RootDir!, transientReturn!);
        var definition = session.Snapshot().EditDefinitions.Single(candidate =>
            string.Equals(candidate.Id, landed!.Edit.EditDefinitionId, StringComparison.Ordinal));
        landed = landed! with { Edit = landed.Edit with { Label = definition.Label } };
        return new EditAssetResult(published!.ProjectRelativeFile!, label, landed);
    }

    private async Task<PictureIngress?> BeginPictureIngressAsync(EditSlotRef slot, string? supplied,
        IProgress<string> status, TextureSharingSnapshot? offered = null, bool sharedConsent = false)
    {
        if (EditSession is not { } session) return null;
        if (!slot.HasDrawableCarrier)
        { status.Report(EditMapCardVm.NoDrawableCarrier); return null; }
        var launch = TextureSharingAt(slot);
        if (EditMapCardVm.RefusalFor(launch.Kind) is { } refused)
        { status.Report(refused); return null; }
        if (launch.Kind == EditTextureSharing.Shared
            && (!sharedConsent || offered is not { Kind: EditTextureSharing.Shared, Uses: { } shownUses }
                || launch.Uses != shownUses))
        { status.Report(EditMapCardVm.SharedConsentRequired(launch.Uses!.Value)); return null; }
        var snapshot = session.Snapshot();
        string? source = supplied;
        ProjectAssetSource? lineage = null;
        string label = PictureIngressLabel(snapshot, slot, supplied);
        string? prepared = null;
        if (slot.ProjectRelativeFile is null && supplied is null)
        {
            if (GameMapFor(slot) is not { } map) { status.Report(NoPictureBehindSlot); return null; }
            label = PictureIngressLabel(snapshot, slot, supplied, map.TextureName);
            try
            {
                prepared = Path.Combine(snapshot.RootDir!, ProjectAssetIngress.DirectoryName, "sources",
                    Guid.NewGuid().ToString("N") + ".png");
                await Task.Run(() => ExportGamePicture(map, prepared));
                source = prepared;
                lineage = new ProjectAssetSource { GameAsset = GameAsset(map) };
            }
            catch (Exception e)
            {
                status.Report($"Couldn't prepare {map.TextureName}: {Reason(e)}");
                return null;
            }
        }
        try
        {
            ProjectAssetIngressSession transport;
            if (IsBareCard(slot))
            {
                var detached = new AuthoredEditSession(snapshot);
                if (!snapshot.TargetSlots.Any(candidate => candidate.Part.SameAs(slot.Edit.Part)))
                {
                    var resolved = ResolvePart(slot.Edit.Part)
                        ?? throw new AuthoredRefusalException(
                            "This part isn't in the current game files, so there is nowhere to record its values.");
                    detached.EnsurePartSlots(slot.Edit.Part, _ => resolved);
                }
                string editId = detached.CreateEdit(slot.Edit.Part);
                var target = FirstEditSlot(detached.Slots(editId), slot);
                transport = ProjectAssetIngress.Begin(detached.Snapshot(), editId, target.Slot.Id, source);
            }
            else
            {
                transport = ProjectAssetIngress.Begin(snapshot,
                    slot.Edit.EditDefinitionId, slot.SlotId, source);
            }
            return new PictureIngress(slot, session, transport, label, lineage,
                launch.Kind, launch.Uses, sharedConsent);
        }
        finally
        {
            if (prepared is not null) try { File.Delete(prepared); } catch { }
        }
    }

    /// <summary>The display identity a picture round trip carries into its next project asset. A reopen reads
    /// the current exact binding rather than the launch card's path, so a stale card cannot rename another
    /// asset. Stock pictures start with a placeholder that the installed map replaces in the caller.</summary>
    internal static string PictureIngressLabel(AuthoredProject project, EditSlotRef slot, string? supplied,
        string? gameTextureName = null)
    {
        if (supplied is not null) return Path.GetFileNameWithoutExtension(supplied);
        if (!string.IsNullOrWhiteSpace(gameTextureName)) return gameTextureName.Trim();
        var binding = project.EditDefinitions.SingleOrDefault(edit =>
                string.Equals(edit.Id, slot.Edit.EditDefinitionId, StringComparison.Ordinal))?.Bindings
            .SingleOrDefault(candidate => string.Equals(candidate.SlotId, slot.SlotId,
                StringComparison.Ordinal));
        var label = binding?.ProjectAssetId is { } assetId
            ? project.ProjectAssets.SingleOrDefault(asset =>
                string.Equals(asset.Id, assetId, StringComparison.Ordinal))?.Label
            : null;
        return string.IsNullOrWhiteSpace(label) ? "image" : label.Trim();
    }

    private void ExportGamePicture(SubjectMap map, string destination)
    {
        if (ExportGamePictureForTests is { } export)
        {
            export(destination);
            return;
        }
        // Both of these reach the screen through the caller's "Couldn't prepare <texture>:" line, so neither
        // names the app's own address for the game files it could not read.
        byte[] bytes = TryDeobfuscateBundle(map.BundleId)
            ?? throw new InvalidDataException("the game files behind it couldn't be read");
        if (!TextureExport.ExportPng(new BundleReader(), bytes, map.TextureName, destination))
            throw new InvalidDataException("it isn't in the game files any more");
    }

    private GameAssetRef GameAsset(SubjectMap map) => new()
    {
        GameBuild = _vfs?.CatalogVersion ?? "unknown",
        LogicalBundle = map.BundleId,
        PathId = map.PathId,
        Name = map.TextureName,
    };

    /// <summary>One save from the image editor, landing on the slot the transport was opened for.
    ///
    /// <para>The boundary is re-asked HERE and not only where the transport opened, because this is where
    /// the mod takes the picture: an editor stays open across rescans and reads, and the answer that let it
    /// open can be minutes or hours old by the first save. A save that cannot land says why and changes
    /// nothing, and the editor still holds the file, so saving again after the read lands is the way
    /// through.</para></summary>
    internal void PublishPictureReturn(PictureIngress ingress, IProgress<string> status)
    {
        try
        {
            // A save for a mod that is no longer the open one lands nowhere — and SAYS so, in the shape
            // the Blender return's own closed-mod refusal uses. It used to return in silence, which on a
            // page showing another mod is indistinguishable from paint that was thrown away.
            if (!ReferenceEquals(EditSession, ingress.Owner))
            { status.Report(PictureSaveModClosed(ClosedModName(ingress.Owner))); return; }
            var liveSlot = LiveSlotFor(ingress);
            if (!liveSlot.HasDrawableCarrier)
            { status.Report(PictureSaveGateRefusal(liveSlot, EditMapCardVm.NoDrawableCarrier)); return; }
            var live = TextureSharingAt(liveSlot);
            if (EditMapCardVm.RefusalFor(live.Kind) is { } refused)
            { status.Report(PictureSaveGateRefusal(liveSlot, refused)); return; }
            if (live.Kind == EditTextureSharing.Shared
                && (!ingress.SharedConsent || ingress.LaunchUses is not { } consentedUses
                    || live.Uses > consentedUses))
            { status.Report(PictureSaveSharingRefusal(liveSlot, live.Uses!.Value)); return; }
            ExactAssetPublishResult published;
            if (IsBareCard(ingress.Slot) && ingress.LandedSlot is null)
            {
                var first = PublishFirstEditAsset(ingress.Owner, ingress.Slot,
                    ingress.Session.OutboundSnapshot, ProjectAssetKind.Picture, ingress.Label,
                    ProjectAssetIngress.Png, ingress.Source);
                ingress.LandedSlot = first.Target;
                liveSlot = first.Target!;
                published = new ExactAssetPublishResult(ProjectAssetPublishResult.Published, null,
                    first.ProjectRelativeFile);
            }
            else if (ingress.LandedSlot is not null)
            {
                var snapshot = ingress.Owner.Snapshot();
                var current = ProjectAssetIngress.Begin(snapshot, liveSlot.Edit.EditDefinitionId,
                    liveSlot.SlotId, ingress.Session.OutboundSnapshot);
                published = ingress.Owner.PublishAssetForBinding(current, ProjectAssetKind.Picture,
                    ingress.Label, ProjectAssetIngress.Png, ingress.Source);
                DeleteSupersededAssetIngress(snapshot.RootDir!, current.ReturnArtifact);
            }
            else
            {
                published = ingress.Owner.PublishAssetForBinding(ingress.Session, ProjectAssetKind.Picture,
                    ingress.Label, ProjectAssetIngress.Png, ingress.Source);
            }
            if (published.Result == ProjectAssetPublishResult.Published)
                // Named the way the drop's own result line names its place — the edit, the map, the
                // material — rather than "this map", which on a page the modder has moved since names
                // nothing at all.
                status.Report($"Saved {ingress.Label} to "
                    + $"{liveSlot.Edit.Label}'s {EditMapCardVm.MapInSentence(liveSlot)}.");
        }
        catch (Exception e) { status.Report($"Couldn't apply the image editor's save: {Reason(e)}"); }
    }

    /// <summary>Resolve a long-lived editor transport's slot against its session now. The first save can
    /// change the binding from the game's value to a project picture, so the launch card is not live state.</summary>
    private static EditSlotRef LiveSlotFor(PictureIngress ingress)
    {
        var address = ingress.LandedSlot ?? ingress.Slot;
        if (string.IsNullOrEmpty(address.Edit.EditDefinitionId)) return address;
        var state = ingress.Owner.Slots(address.Edit.EditDefinitionId)
            .FirstOrDefault(candidate => candidate.Slot.Id == address.SlotId);
        if (state is null) return address;
        return address with
        {
            Input = state.Slot.Input,
            Domain = state.Slot.Domain,
            MaterialSlotIndex = state.Slot.MaterialSlotIndex,
            MaterialName = state.Slot.Material?.Name,
            ShaderProperty = state.Slot.ShaderProperty,
            ProjectRelativeFile = state.ProjectAsset?.File,
            Binding = state.Binding.Kind,
            Source = state.Binding.SourceSlot is { } source
                ? new EditSlotSource(source.EditDefinitionId, source.SlotId) : null,
            SubmeshIndex = state.Slot.SubmeshIndex,
        };
    }

    internal static string PictureSaveSharingRefusal(EditSlotRef slot, int uses) =>
        $"Couldn't save the image editor's file to {slot.Edit.Label}'s {EditMapCardVm.MapInSentence(slot)}: "
        + EditMapCardVm.SharedConsentRequired(uses);

    internal static string PictureSaveGateRefusal(EditSlotRef slot, string gate) =>
        $"Couldn't save the image editor's file to {slot.Edit.Label}'s {EditMapCardVm.MapInSentence(slot)}: "
        + $"{gate} Nothing was changed. The editor still has the file.";

    /// <summary>What a save from the image editor says when the mod it belongs to is no longer open. The
    /// twin of the Blender return's own sentence, and it carries one fact that one does not have to: the
    /// editor still holds the picture, so the way through is to open that mod again and save again.</summary>
    internal static string PictureSaveModClosed(string mod) =>
        $"Couldn't apply the image editor's save: {mod} is no longer open. Nothing was changed. "
        + "The editor still has the file. Open that mod again and save.";

    /// <summary>A replacement's own map with nothing recorded on it yet: there is no game picture to start
    /// from, so the exact card's drop route is the way in.</summary>
    internal const string NoPictureBehindSlot =
        "There is no image here yet. Drop a .png on this card to add one.";

    // ---- shading values -----------------------------------------------------------------------------

    private readonly object _materialEvidenceGate = new();
    private DerivedMaterialEvidence? _materialEvidence;
    private GameVfs? _materialEvidenceInstall;

    /// <summary>The install-derived material evidence shared by Edit and Build, kept per install exactly
    /// like the edit resolver: a force rescan swaps the install object, and the next ask replaces it.</summary>
    internal DerivedMaterialEvidence MaterialEvidenceFor(GameVfs vfs)
    {
        lock (_materialEvidenceGate)
        {
            if (_materialEvidence is null || !ReferenceEquals(_materialEvidenceInstall, vfs))
            {
                _materialEvidence = new DerivedMaterialEvidence(vfs.TryDeobfuscateLogical);
                _materialEvidenceInstall = vfs;
            }
            return _materialEvidence;
        }
    }

    private (GameVfs Vfs, DerivedMaterialEvidence Evidence)? CurrentMaterialEvidence() =>
        _vfs is { } vfs ? (vfs, MaterialEvidenceFor(vfs)) : null;

    // ---- the mesh-edit gate --------------------------------------------------------------------------

    private readonly object _meshEditGateLock = new();
    private MeshEditGate? _meshEditGate;
    private GameVfs? _meshEditGateInstall;

    /// <summary>The install-derived mesh-edit gate shared by the ② Edit verbs, the Blender session's
    /// writability and the Build plan, kept per install exactly like the material evidence: a force rescan
    /// swaps the install object, and the next ask replaces it.</summary>
    internal MeshEditGate MeshEditGateFor(GameVfs vfs)
    {
        lock (_meshEditGateLock)
        {
            if (_meshEditGate is null || !ReferenceEquals(_meshEditGateInstall, vfs))
            {
                _meshEditGate = new MeshEditGate(vfs.TryDeobfuscateLogical);
                _meshEditGateInstall = vfs;
            }
            return _meshEditGate;
        }
    }

    /// <summary>Why one part's game mesh cannot be edited in Blender, in the page's own sentence, or null
    /// when it can — or when nothing about it can be read, which is a different failure with its own loud
    /// route. Synchronous and bundle-reading: callers run it off the UI thread.</summary>
    private string? MeshEditBlockFor(GameVfs vfs, SubjectPart model)
    {
        var recipe = model.ToRecipePart();
        string? bundle = recipe.MeshBundle ?? (recipe.MeshAddress.Length == 0
            ? null : vfs.Catalog.ResolveAddress(recipe.MeshAddress));
        if (bundle is null) return null;
        return MeshEditGateFor(vfs).Blocked(bundle, recipe.SlotName, recipe.MeshPathId) is { } why
            ? PartSkinGate.EditRefusal(why) : null;
    }

    /// <summary>The ② Edit page's ask: the mesh-edit refusal for one part, read off the UI thread. Null
    /// while the install or the part cannot be read — the gate never blames a mesh for a read that never
    /// happened.</summary>
    public Task<string?> MeshEditBlockAsync(TargetPart part) => Task.Run(() =>
        _vfs is { } vfs && SubjectPartOf(part) is { } model ? MeshEditBlockFor(vfs, model) : null);

    /// <summary>The supported shading fields of one exact material, with the material's own value per
    /// field. Null where nothing is supported — not on the character shader, or nothing provable.</summary>
    private static EditShadingInfo? ShadingFor(GameVfs vfs, DerivedMaterialEvidence evidence,
        GameAssetRef material)
    {
        var proof = evidence.Resolve(new TargetSlot
        {
            Id = "shading-probe",
            Part = new TargetPart { Subject = "probe", Outfit = "probe", RendererSlot = "probe" },
            Input = TargetInputKind.MaterialValue,
            Semantic = MaterialValueSemantics.UseGiFlatten,
            Material = material,
        });
        if (proof is null) return null;
        BundleReader.MaterialShading? shading = null;
        try
        {
            var bytes = vfs.TryDeobfuscateLogical(material.LogicalBundle);
            shading = bytes is null ? null : new BundleReader().GetMaterialShading(bytes, material.PathId);
        }
        catch { }
        var fields = new List<EditShadingField>();
        foreach (var field in proof.Fields)
        {
            if (MaterialValueCatalog.Field(field.Semantic) is not { } meta) continue;
            fields.Add(new EditShadingField(meta.Semantic, meta.Label, meta.Kind,
                meta.ObservedMin, meta.ObservedMax,
                shading is null ? null : MaterialShadingValues.OriginalValue(shading, meta)));
        }
        return fields.Count == 0 ? null : new EditShadingInfo(fields);
    }

    public async Task<EditShadingValuesResult?> EditShadingValuesAsync(EditRef edit,
        int materialSlotIndex, string materialLabel, IReadOnlyDictionary<string, string> authored,
        bool addsFirstEdit)
    {
        try
        {
            var install = CurrentMaterialEvidence()
                ?? throw new EditShadingFailureException(EditPageVm.ShadingInstallUnavailable);
            if (MainWindow is not { } owner) return null;
            // the resolver is page-thread state; only the bundle reads leave the thread
            var material = ResolvePart(edit.Part)?.Materials?
                .FirstOrDefault(candidate => candidate.MaterialSlotIndex == materialSlotIndex)?.Material;
            var project = EditSession?.Snapshot();
            var opened = material is null || project is null ? null
                : await Task.Run<(EditShadingInfo Info, ShadingDialogValues Values)?>(() =>
            {
                var info = ShadingFor(install.Vfs, install.Evidence, material);
                if (info is null) return null;
                var values = ReadShadingDialogValues(project, edit.EditDefinitionId,
                    materialSlotIndex, authored,
                    new MaterialSourceValueReader(install.Vfs.TryDeobfuscateLogical));
                return (Info: info, Values: values);
            });
            // On the status line, not in a modal of its own: this is one of the five things clicking Edit
            // values… can answer, and the other four all speak there. A dialog raised only to be dismissed
            // is a second gesture asked of the modder for news.
            if (opened is null) throw new EditShadingFailureException(NoAdjustableValues);
            return await ShadingValuesWindow.Show(owner, materialLabel, opened.Value.Info.Fields,
                opened.Value.Values.Values, opened.Value.Values.Copied,
                opened.Value.Values.UnreadableCopies, addsFirstEdit);
        }
        catch (EditShadingFailureException) { throw; }
        catch { throw new EditShadingFailureException(EditPageVm.EditShadingValuesFailed); }
    }

    internal const string NoAdjustableValues = "This material's shader has no adjustable values.";

    internal sealed record ShadingDialogValues(IReadOnlyDictionary<string, string> Values,
        IReadOnlySet<string> Copied, IReadOnlySet<string> UnreadableCopies);

    internal static ShadingDialogValues ReadShadingDialogValues(AuthoredProject project,
        string editDefinitionId, int materialSlotIndex,
        IReadOnlyDictionary<string, string> authored, IMaterialGameValueReader reader)
    {
        var values = authored.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var copied = authored.Where(pair => pair.Value.Length == 0)
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        if (copied.Count == 0) return new ShadingDialogValues(values, copied, unreadable);

        var edit = project.EditDefinitions.Single(candidate => candidate.Id == editDefinitionId);
        var slots = project.TargetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        foreach (string semantic in copied)
        {
            var carrier = project.TargetSlots.FirstOrDefault(slot => slot.Part.SameAs(edit.Target)
                && slot.Domain == TargetSlotDomain.Game
                && slot.Input == TargetInputKind.MaterialValue
                && slot.MaterialSlotIndex == materialSlotIndex
                && string.Equals(slot.Semantic, semantic, StringComparison.Ordinal));
            var binding = carrier is null ? null : edit.Bindings.FirstOrDefault(candidate =>
                candidate.SlotId == carrier.Id);
            if (carrier is null || binding?.Kind != BindingKind.SourceSlot || binding.SourceSlot is null
                || !slots.TryGetValue(binding.SourceSlot.SlotId, out var source))
            {
                values[semantic] = "";
                unreadable.Add(semantic);
                continue;
            }
            MaterialGameValueResolution resolved;
            try { resolved = reader.Resolve(source, carrier, semantic); }
            catch
            {
                values[semantic] = "";
                unreadable.Add(semantic);
                continue;
            }
            if (resolved.Verdict != BuildPlanVerdict.Resolved || resolved.Value is not { Length: > 0 } value)
            {
                values[semantic] = "";
                unreadable.Add(semantic);
                continue;
            }
            values[semantic] = value;
        }
        return new ShadingDialogValues(values, copied, unreadable);
    }

    public async Task<EditShadingSource?> PickShadingSourceAsync(TargetPart part, int materialSlotIndex,
        string materialLabel, GameAssetRef? targetMaterial,
        IReadOnlyList<(string Subject, string Outfit)> subjects, IProgress<string> status)
    {
        try
        {
            if (_vfs is not { } vfs)
                throw new EditShadingFailureException(EditPageVm.ShadingInstallUnavailable);
            if (MainWindow is not { } owner) return null;
            // An invalid exact reference is already a complete no-values answer. A valid reference still
            // needs shader evidence and bundle values, so that read stays behind the pick with the source
            // read; nothing here resolves or parses a part merely to open the chooser.
            if (targetMaterial is { } known
                && (known.PathId == 0 || string.IsNullOrWhiteSpace(known.LogicalBundle)))
                throw new EditShadingFailureException(NoAdjustableValues);
            var picked = await ShadingSourcePickerWindow.Show(owner,
                $"{materialLabel} · {PartToken(part)}",
                gone => Task.Run(() => ShadingSourceRows(part, materialSlotIndex, subjects,
                    vfs.CatalogVersion, gone), gone), _subjectModels.WaitForChangeAsync);
            if (picked?.Tag is not (TargetPart sourcePart, int sourceIndex, GameAssetRef sourceMaterial))
                return null;
            status.Report(EditPageVm.ReadingShadingValues);
            // The target material is needed only to compare the source the modder actually chose. Keep its
            // resolver/bundle work behind that choice and off the UI thread, so opening the list never waits
            // on a mesh read or on the shared resolver lock.
            var carrier = targetMaterial ?? await Task.Run(() => ResolvePart(part)?.Materials?
                .FirstOrDefault(candidate => candidate.MaterialSlotIndex == materialSlotIndex)?.Material);
            if (carrier is null)
                throw new EditShadingFailureException(NoAdjustableValues);
            var rows = await Task.Run(() => ShadingCopyRows(carrier, sourceMaterial));
            if (rows is null)
                throw new EditShadingFailureException(NoAdjustableValues);
            if (rows.SourceUnreadable)
                throw new EditShadingFailureException(EditPageVm.ShadingSourceUnreadable);
            return new EditShadingSource(sourcePart, sourceIndex, picked.ToString(), rows.Rows);
        }
        catch (EditShadingFailureException) { throw; }
        catch { throw new EditShadingFailureException(EditPageVm.CopyShadingFailed); }
    }

    /// <summary>Every material of every part the mod's subjects have, except the target position itself.
    /// The subject model already carries the displayed names and the exact material reference, so list
    /// construction never resolves a part or parses its mesh bundle.</summary>
    internal ShadingSourceLoad ShadingSourceRows(TargetPart target, int targetIndex,
        IReadOnlyList<(string Subject, string Outfit)> subjects, string catalogVersion,
        CancellationToken gone)
    {
        var rows = new List<ShadingSourceRow>();
        bool reading = false;
        bool unreadable = false;
        foreach (var (subject, outfit) in subjects)
        {
            if (_subjectModels.TryGet(subject, outfit) is not { } model)
            {
                if (_subjectModels.IsUnreadable(subject, outfit)) unreadable = true;
                else reading = true;
                continue;
            }
            IReadOnlyList<ShadingSourceRow> cached;
            string cacheKey = subject + "\u001f" + outfit + "\u001f"
                + (subjects.Count > 1 ? "many" : "one");
            lock (_shadingSourceCacheGate)
            {
                if (!ReferenceEquals(_shadingSourceCacheInstall, _vfs))
                {
                    _shadingSourceRows.Clear();
                    _shadingSourceCacheInstall = _vfs;
                }
                if (!_shadingSourceRows.TryGetValue(cacheKey, out cached!))
                {
                    var built = new List<ShadingSourceRow>();
                    foreach (var part in model.Parts)
                    {
                        var candidate = new TargetPart
                        {
                            Subject = subject, Outfit = outfit, RendererSlot = part.SlotName,
                        };
                        for (int materialIndex = 0; materialIndex < part.Materials.Count; materialIndex++)
                        {
                            var material = part.Materials[materialIndex];
                            if (material.PathId == 0 || string.IsNullOrWhiteSpace(material.Bundle)) continue;
                            string partLabel = subjects.Count > 1
                                ? $"{part.Token} · {SubjectLabel(subject, outfit)}" : part.Token;
                            string materialName = string.IsNullOrWhiteSpace(material.Name)
                                ? $"material {materialIndex}" : material.Name;
                            var exact = new GameAssetRef
                            {
                                GameBuild = catalogVersion, LogicalBundle = material.Bundle!,
                                PathId = material.PathId, Name = material.Name,
                            };
                            built.Add(new ShadingSourceRow(partLabel, materialName,
                                (candidate, materialIndex, exact)));
                        }
                    }
                    _shadingSourceRows[cacheKey] = cached = built;
                }
            }
            foreach (var row in cached)
            {
                gone.ThrowIfCancellationRequested();
                if (row.Tag is (TargetPart candidate, int materialIndex, GameAssetRef)
                    && candidate.SameAs(target) && materialIndex == targetIndex) continue;
                rows.Add(row);
            }
        }
        return new ShadingSourceLoad(rows, reading ? EditSubjectRead.Reading
            : unreadable ? EditSubjectRead.Unreadable : EditSubjectRead.Answered,
            _subjectModels.Version);
    }

    /// <summary>What copying one material's shading onto another would set: the carrier's supported
    /// fields where the source states a differing value. Null when the carrier supports nothing.</summary>
    internal sealed record ShadingCopyRead(IReadOnlyList<EditShadingCopyRow> Rows,
        bool SourceUnreadable = false);

    private ShadingCopyRead? ShadingCopyRows(GameAssetRef carrier,
        GameAssetRef source)
    {
        var install = CurrentMaterialEvidence()
            ?? throw new EditShadingFailureException(EditPageVm.ShadingInstallUnavailable);
        var info = ShadingFor(install.Vfs, install.Evidence, carrier);
        if (info is null) return null;
        var reader = new BundleReader();
        return ReadShadingCopyRows(info, source, install.Vfs.TryDeobfuscateLogical,
            reader.GetMaterialShading);
    }

    internal static ShadingCopyRead ReadShadingCopyRows(EditShadingInfo info, GameAssetRef source,
        Func<string, byte[]?> deobfuscate,
        Func<byte[], long, BundleReader.MaterialShading?> readMaterial)
    {
        BundleReader.MaterialShading? sourceShading;
        try
        {
            var bytes = deobfuscate(source.LogicalBundle);
            sourceShading = bytes is null ? null
                : readMaterial(bytes, source.PathId);
        }
        catch { sourceShading = null; }
        if (sourceShading is null)
            return new ShadingCopyRead(Array.Empty<EditShadingCopyRow>(), SourceUnreadable: true);
        var rows = new List<EditShadingCopyRow>();
        foreach (var field in info.Fields)
        {
            if (MaterialValueCatalog.Field(field.Semantic) is not { } meta) continue;
            string? sourceValue = MaterialShadingValues.OriginalValue(sourceShading, meta);
            if (sourceValue is null) continue;
            if (string.Equals(sourceValue, field.OriginalValue, StringComparison.Ordinal)) continue;
            rows.Add(new EditShadingCopyRow(meta.Semantic, meta.Label, field.OriginalValue,
                sourceValue));
        }
        return new ShadingCopyRead(rows);
    }

    // ---- questions, clipboard, navigation -----------------------------------------------------------

    internal Func<string, string, string, bool, Task<bool>>? ConfirmForTests { get; set; }
    internal Action<PictureIngress>? PictureIngressOpenedForTests { get; set; }
    internal Action<string>? ExportGamePictureForTests { get; set; }

    /// <summary>Ask a yes/no question. Guarded at this boundary rather than at each call: the page asks from
    /// inside a RelayCommand, and a dialog that threw would take the whole command out with nothing said. A
    /// question that cannot be asked is declined, which leaves the project untouched.</summary>
    public async Task<bool> ConfirmAsync(string title, string body, string confirmLabel,
        bool dangerous = false)
    {
        try
        {
            if (ConfirmForTests is { } confirm)
                return await confirm(title, body, confirmLabel, dangerous);
            if (MainWindow is not { } owner) return false;
            return await ConfirmWindow.Show(owner, title, body, confirmLabel, "Cancel", danger: dangerous);
        }
        catch { return false; }
    }

    /// <summary>Move to ③ Build. Guarded for the same reason as <see cref="ConfirmAsync"/>: this is the last
    /// thing several of the page's commands do, and it must not be able to take one out.</summary>
    public void GoToBuild(EditRef? edit)
    {
        try
        {
            SelectedStep = "③ Build";
            if (edit is not null) BuildPage.SelectEdit(edit);
        }
        catch { /* the step is a display choice; a failure to move is not worth taking a verb out for */ }
    }

    /// <summary>Persist a committed session revision once. Notifications may arrive out of order, so a stale
    /// revision is ignored rather than autosaving an older observation after a newer one.</summary>
    private long _lastPersistedEditRevision = -1;
    private AuthoredEditSession? _persistedEditSession;

    public void ProjectChanged(long revision)
    {
        if (!ReferenceEquals(_persistedEditSession, EditSession))
        {
            _persistedEditSession = EditSession;
            _lastPersistedEditRevision = -1;
        }
        if (revision <= _lastPersistedEditRevision) return;
        _lastPersistedEditRevision = revision;
        AutoSaveProject();
    }
}
