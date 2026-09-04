using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Project;
using Remold.Core.Textures;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The ② Edit page's view-model side: the tree the settled surface contract describes — subject → part →
/// edits, each edit's own materials as its closed-by-default child rows — and the verbs that act on it.
///
/// <para>Every project here is built through <see cref="AuthoredEditSession"/> from the pinned fixtures, so
/// the page is always reading a project the validator accepts. The shell is a recorder: nothing touches the
/// game, Blender or the disk, which is exactly the split the seam exists to make.</para>
///
/// <para>What this file cannot reach: anything holding a decoded <c>Bitmap</c>. This host stands no Avalonia
/// render backend of its own, so a picture is decoded here only in a run where some OTHER class stood one up
/// first — measured, not assumed — which means an assertion resting on a decoded picture rests on the order
/// the runner picked. So the preview memoization below is pinned through the KEYS the page files renders
/// under, which is the half that decides whether a redraw re-renders; and how far one committed change
/// reaches into those files is pinned at the change itself, in
/// <see cref="AuthoredSessionFoundationTests.A_change_reaches_exactly_as_far_into_the_pictures_as_it_can_say"/>.</para>
/// </summary>
public class EditPageVmTests
{
    // ---- the shell recorder ----

    private sealed class FakeShell : IEditPageShell
    {
        public AuthoredEditSession? Session;
        /// <summary>What the install answers for a part. Null is "not in this install".</summary>
        public Func<TargetPart, LegacyResolvedPart?> Resolve = _ => null;

        /// <summary>The install's short name for a part. Empty is "the install cannot name one".</summary>
        public Func<TargetPart, string> Token = _ => "";

        /// <summary>What the game draws on one slot. Null is "the install cannot name it".</summary>
        public Func<EditSlotRef, string?> GameTexture = _ => null;

        public EditInstallState Install = new();

        public bool ConfirmResult = true;
        public int ConfirmCalls;
        public int? EditsAtConfirm;
        public string? LastConfirmTitle;
        public string? LastConfirmBody;
        public string? LastConfirmLabel;
        public bool LastConfirmDangerous;

        public int BlenderCalls;
        public TargetPart? LastBlenderPart;
        public EditRef? LastBlenderEdit;
        public bool LastWithReferences;

        /// <summary>Held open until released, so a second click can be tried while the first is running.</summary>
        public TaskCompletionSource? BlenderHold;

        public EditAssetResult? PictureResult;
        public bool PictureLaunched = true;
        public TaskCompletionSource? OpenHold;
        /// <summary>What the picker comes back with. Null is a cancel; an <see cref="EditRampPick"/> with no
        /// file is the pinned keep-the-game's-own row.</summary>
        public EditRampPick? RampResult;
        public EditAssetResult? DropResult;

        public EditSlotRef? LastOpenSlot;
        public int? EditsAtOpen;
        public string? SelectedEditAtOpen;
        public bool? LastOpenConfirmed;
        public EditTextureSharingOffer? LastOpenOffered;
        public EditSlotRef? LastUvSlot;
        public EditSlotRef? LastRampSlot;
        public int? EditsAtRamp;
        public string? SelectedEditAtRamp;
        public EditSlotRef? LastDropSlot;
        public Func<EditNodeVm?>? CurrentSelection;

        public EditSkeletonOutline? Skeleton;

        public int MapPreviewCalls;
        public int MapPreviewCompletions;
        public Queue<TaskCompletionSource<EditMapPreview?>> MapPreviewHolds { get; } = new();
        public int EditPreviewCalls;
        public int PartPreviewCalls;
        /// <summary>Make a preview read throw, which is the failure that carries a cause line.</summary>
        public bool PreviewsThrow;
        public bool PreviewsSucceed;

        /// <summary>Answer every card with the loader's missing-file record, naming this file. Outranks
        /// <see cref="PreviewsSucceed"/>: the shell's two answers are exclusive.</summary>
        public string? PreviewMissingFile;

        public int ProjectChangedCalls;
        public List<long> ProjectChangedRevisions { get; } = new();
        public int BuildCalls;
        public EditRef? LastBuildEdit;

        /// <summary>How many resolves ran on the CALLING thread. A redraw must add none: the read is a
        /// bundle deobfuscation, and the caller is the UI thread.</summary>
        public int SyncResolveCalls;
        public int AsyncResolveCalls;

        /// <summary>Held open, the install read stays in flight so what the page draws without it can be
        /// seen, and so the redraw its landing causes can be watched.</summary>
        public TaskCompletionSource<LegacyResolvedPart?>? ResolveHold;

        public LegacyResolvedPart? ResolvePart(TargetPart target)
        {
            SyncResolveCalls++;
            return Resolve(target);
        }

        public async Task<LegacyResolvedPart?> ResolvePartAsync(TargetPart target)
        {
            AsyncResolveCalls++;
            if (ResolveHold is not null) return await ResolveHold.Task;
            return Resolve(target);
        }

        /// <summary>What the install says one subject's parts are. Empty is "nothing has read this subject
        /// yet", which is what a page with no install in hand sees.</summary>
        public Func<string, string, IReadOnlyList<TargetPart>> Parts =
            (_, _) => Array.Empty<TargetPart>();

        public IReadOnlyList<TargetPart> SubjectParts(string subject, string outfit) =>
            Parts(subject, outfit);

        public EditSkeletonOutline? ReadSkeleton(string subject, string outfit) => Skeleton;

        public EditInstallState InstallState() => Install;

        public string PartToken(TargetPart part) => Token(part);

        public string? GameTextureName(EditSlotRef slot) => GameTexture(slot);

        /// <summary>How many of the item's uses draw the texture behind one slot — one use being one
        /// material position of one part. Null is "the install has not answered for this subject", which is
        /// what a page drawn before the install read lands sees.</summary>
        public Func<EditSlotRef, int?> TextureUseCount = _ => 1;

        public int? TextureUses(EditSlotRef slot) => TextureUseCount(slot);

        /// <summary>How far along the install is with one part's item. Answered by default: the count beside
        /// it is what the page reads.</summary>
        public Func<TargetPart, EditSubjectRead> SubjectReadState = _ => EditSubjectRead.Answered;

        public EditSubjectRead SubjectRead(TargetPart part) => SubjectReadState(part);

        public async Task<EditMapPreview?> LoadMapPreviewAsync(EditSlotRef slot)
        {
            MapPreviewCalls++;
            try
            {
                if (MapPreviewHolds.Count > 0) return await MapPreviewHolds.Dequeue().Task;
                if (PreviewsThrow) throw new InvalidOperationException("the game is holding this file");
                if (PreviewMissingFile is { } gone)
                    return new EditMapPreview(null, EditMapCardVm.NoDimensions, gone);
                return PreviewsSucceed ? new EditMapPreview(TinyBitmap(), "1×1") : null;
            }
            finally { MapPreviewCompletions++; }
        }

        public Task<EditMeshPreview?> LoadEditMeshPreviewAsync(EditRef edit)
        {
            EditPreviewCalls++;
            if (PreviewsThrow) throw new InvalidOperationException("the game is holding this file");
            return Task.FromResult(PreviewsSucceed
                ? new EditMeshPreview(TinyBitmap(), 3, null) : null);
        }

        public Task<EditMeshPreview?> LoadPartMeshPreviewAsync(TargetPart part)
        {
            PartPreviewCalls++;
            if (PreviewsThrow) throw new InvalidOperationException("the game is holding this file");
            return Task.FromResult(PreviewsSucceed
                ? new EditMeshPreview(TinyBitmap(), 3, null) : null);
        }

        private static Avalonia.Media.Imaging.Bitmap TinyBitmap()
        {
            byte[] png = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
            return new Avalonia.Media.Imaging.Bitmap(new MemoryStream(png));
        }

        /// <summary>What the mesh-edit gate answers per part. Null is "the mesh can be edited".</summary>
        public Func<TargetPart, string?> MeshEditBlock = _ => null;
        public int MeshEditBlockCalls;

        /// <summary>Held open, the gate read stays in flight so a click that beats it can be tried.</summary>
        public TaskCompletionSource<string?>? MeshEditGateHold;

        public async Task<string?> MeshEditBlockAsync(TargetPart part)
        {
            MeshEditBlockCalls++;
            if (MeshEditGateHold is not null) return await MeshEditGateHold.Task;
            return MeshEditBlock(part);
        }

        public async Task OpenPartInBlenderAsync(TargetPart part, bool withReferences,
            IProgress<string> status)
        {
            BlenderCalls++;
            LastBlenderPart = part;
            LastBlenderEdit = null;
            LastWithReferences = withReferences;
            if (BlenderHold is not null) await BlenderHold.Task;
        }

        public async Task OpenInBlenderAsync(EditRef edit, bool withReferences, IProgress<string> status)
        {
            BlenderCalls++;
            LastBlenderPart = edit.Part;
            LastBlenderEdit = edit;
            LastWithReferences = withReferences;
            if (BlenderHold is not null) await BlenderHold.Task;
        }

        public async Task<EditPictureOpenResult> OpenPictureAsync(EditSlotRef slot, IProgress<string> status,
            bool confirmed = false, EditTextureSharingOffer? offered = null)
        {
            LastOpenSlot = slot;
            EditsAtOpen = Session?.Outline().Edits.Count;
            SelectedEditAtOpen = CurrentSelection?.Invoke()?.EditDefinitionId;
            LastOpenConfirmed = confirmed;
            LastOpenOffered = offered;
            if (OpenHold is not null) await OpenHold.Task;
            return PictureLaunched
                ? new EditPictureOpenResult(EditPictureOpenOutcome.Launched, Bind(slot, PictureResult))
                : EditPictureOpenResult.NotLaunched;
        }

        public Task OpenUvGuideAsync(EditSlotRef slot, IProgress<string> status)
        {
            LastUvSlot = slot;
            return Task.CompletedTask;
        }

        /// <summary>What the ramp routes throw instead of answering — the curated refusals the picker and
        /// the game-ramp read write for the screen.</summary>
        public Exception? RampFailure;

        public Task<EditRampPick?> PickRampAsync(EditSlotRef slot)
        {
            LastRampSlot = slot;
            EditsAtRamp = Session?.Outline().Edits.Count;
            SelectedEditAtRamp = CurrentSelection?.Invoke()?.EditDefinitionId;
            if (RampFailure is not null) return Task.FromException<EditRampPick?>(RampFailure);
            if (RampResult?.Picked is { } picked) Bind(slot, picked);
            return Task.FromResult(RampResult);
        }

        /// <summary>What the shading-values dialog answers, and what the last opening carried.</summary>
        public IReadOnlyList<EditShadingValueEdit>? ShadingEdits;
        public bool ShadingMatchesOriginal;
        public Exception? ShadingEditFailure;
        public IReadOnlyDictionary<string, string>? LastShadingAuthored;
        public bool? LastShadingAddsFirstEdit;
        public Task<EditShadingValuesResult?> EditShadingValuesAsync(EditRef edit,
            int materialSlotIndex, string materialLabel, IReadOnlyDictionary<string, string> authored,
            bool addsFirstEdit)
        {
            LastShadingAuthored = authored;
            LastShadingAddsFirstEdit = addsFirstEdit;
            if (ShadingEditFailure is not null)
                return Task.FromException<EditShadingValuesResult?>(ShadingEditFailure);
            return Task.FromResult(ShadingEdits is null ? null
                : new EditShadingValuesResult(ShadingEdits, ShadingMatchesOriginal));
        }

        /// <summary>What the shading-source picker answers.</summary>
        public EditShadingSource? ShadingSource;
        public Exception? ShadingCopyFailure;
        public Task<EditShadingSource?> PickShadingSourceAsync(TargetPart part, int materialSlotIndex,
            string materialLabel, GameAssetRef? targetMaterial,
            IReadOnlyList<(string Subject, string Outfit)> subjects, IProgress<string> status) =>
            ShadingCopyFailure is null ? Task.FromResult(ShadingSource)
                : Task.FromException<EditShadingSource?>(ShadingCopyFailure);

        /// <summary>Whether the last drop arrived with the page's own question already answered.</summary>
        public bool? LastDropConfirmed;
        public EditTextureSharingOffer? LastDropOffered;

        /// <summary>What the file dialog behind Browse answers, and how often it was opened.</summary>
        public string? PickedPicture;
        public int PicturePicks;

        public Task<string?> PickPictureAsync()
        {
            PicturePicks++;
            return Task.FromResult(PickedPicture);
        }

        public Task<EditAssetResult?> AcceptDroppedPictureAsync(EditSlotRef slot, string path,
            IProgress<string> status, bool confirmed = false, EditTextureSharingOffer? offered = null)
        {
            LastDropSlot = slot;
            LastDropConfirmed = confirmed;
            LastDropOffered = offered;
            return Task.FromResult(Bind(slot, DropResult));
        }

        private EditAssetResult? Bind(EditSlotRef slot, EditAssetResult? result)
        {
            if (result is null) return null;
            var asset = Session!.Snapshot().ProjectAssets.Single(candidate =>
                string.Equals(candidate.File, result.ProjectRelativeFile, StringComparison.OrdinalIgnoreCase));
            Session.ChooseProjectAsset(slot.Edit.EditDefinitionId, slot.SlotId, asset.Id);
            return result;
        }

        public Task<bool> ConfirmAsync(string title, string body, string confirmLabel, bool dangerous = false)
        {
            ConfirmCalls++;
            EditsAtConfirm = Session?.Outline().Edits.Count;
            LastConfirmTitle = title;
            LastConfirmBody = body;
            LastConfirmLabel = confirmLabel;
            LastConfirmDangerous = dangerous;
            return Task.FromResult(ConfirmResult);
        }

        public Task CopyTextAsync(string? text) => Task.CompletedTask;

        // ---- the subject verbs ----

        /// <summary>Each subject verb the page ran, as "verb subject/outfit", in order.</summary>
        public List<string> SubjectVerbs { get; } = new();

        /// <summary>Held open, a subject verb stays running so the gate it holds can be read.</summary>
        public TaskCompletionSource? SubjectHold;

        private async Task SubjectVerb(string verb, string subject, string outfit)
        {
            SubjectVerbs.Add($"{verb} {subject}/{outfit}");
            if (SubjectHold is not null) await SubjectHold.Task;
        }

        /// <summary>The friendly label for one subject. The default is the internal character key, which is
        /// what the app's own naming home answers with while the roster is cold.</summary>
        public Func<string, string, string> Label = (subject, _) => subject;

        public string SubjectLabel(string subject, string outfit) => Label(subject, outfit);

        public Task OpenSubjectInBlenderAsync(string subject, string outfit, IProgress<string> status) =>
            SubjectVerb("open-all", subject, outfit);

        public Task OpenSubjectFirstEditInBlenderAsync(string subject, string outfit,
            IProgress<string> status) => SubjectVerb("open-all-first-edit", subject, outfit);

        public void ShowSubjectFolder(string subject, string outfit) =>
            SubjectVerbs.Add($"show-folder {subject}/{outfit}");

        public async Task RemoveSubjectAsync(string subject, string outfit)
        {
            await SubjectVerb("remove", subject, outfit);
            Session!.ForgetSubject(subject, outfit);
        }

        public void GoToBuild(EditRef? edit)
        {
            BuildCalls++;
            LastBuildEdit = edit;
        }

        public void ProjectChanged(long revision)
        {
            ProjectChangedCalls++;
            ProjectChangedRevisions.Add(revision);
        }
    }

    // ---- fixtures ----

    private static TargetPart Body => AuthoredEditFixtures.Body;

    private static (EditPageVm Vm, AuthoredEditSession Session, FakeShell Shell) Page(AuthoredProject project,
        Action<FakeShell>? arrange = null)
    {
        var session = new AuthoredEditSession(project);
        var shell = new FakeShell();
        arrange?.Invoke(shell);
        shell.Session = session;
        var vm = new EditPageVm(shell);
        vm.Load(session);
        return (vm, session, shell);
    }

    private static (EditPageVm Vm, FakeShell Shell) On(AuthoredEditSession session,
        Action<FakeShell>? arrange = null)
    {
        var shell = new FakeShell();
        arrange?.Invoke(shell);
        shell.Session = session;
        var vm = new EditPageVm(shell);
        vm.Load(session);
        return (vm, shell);
    }

    private static EditNodeVm Subject(EditPageVm vm) => Assert.Single(vm.Nodes);

    private static EditNodeVm PartRow(EditPageVm vm) =>
        Subject(vm).Children.Single(n => n.IsPart && n.Part!.SameAs(Body));

    private static IReadOnlyList<AuthoredEditOutlineEntry> EditsFor(AuthoredEditSession session,
        TargetPart? part = null) => session.Outline().Edits
        .Where(edit => edit.Target.SameAs(part ?? Body)).ToList();

    [Fact]
    public async Task A_late_older_session_notification_cannot_overwrite_a_newer_revision()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        using var olderEntered = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        session.Changed += (_, change) =>
        {
            if (change.Revision != 1) return;
            olderEntered.Set();
            releaseOlder.Wait(TimeSpan.FromSeconds(5));
        };
        var (vm, shell) = On(session);

        var older = Task.Run(() => session.RenameEdit("edit-long", "Older"));
        Assert.True(olderEntered.Wait(TimeSpan.FromSeconds(5)));
        var newer = Task.Run(() => session.RenameEdit("edit-long", "Newer"));
        await newer.WaitAsync(TimeSpan.FromSeconds(5));
        releaseOlder.Set();
        await older.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new long[] { 2 }, shell.ProjectChangedRevisions);
        Assert.Equal("Newer", PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long").Title);
    }

    [Fact]
    public void Dispatched_session_notifications_filter_stale_revisions_when_the_UI_queue_reorders_them()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var shell = new FakeShell();
        var queued = new List<Action>();
        var vm = new EditPageVm(shell, queued.Add);
        vm.Load(session);

        session.RenameEdit("edit-long", "Older");
        session.RenameEdit("edit-long", "Newer");

        Assert.Equal(2, queued.Count);
        queued[1]();
        queued[0]();

        Assert.Equal(new long[] { 2 }, shell.ProjectChangedRevisions);
        Assert.Equal("Newer", PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long").Title);
    }

    private static EditShadingRowVm ShadingRow(EditPageVm vm, string editId) =>
        PartRow(vm).Children.Single(node => node.EditDefinitionId == editId)
            .MapGroups.Single(group => group.Shading?.MaterialSlotIndex == 0).Shading!;

    private static async Task<EditShadingRowVm> BareShadingRow(EditPageVm vm)
    {
        for (int i = 0; i < 200 && PartRow(vm).MapGroups.Count == 0; i++) await Task.Delay(5);
        return Assert.Single(PartRow(vm).MapGroups).Shading!;
    }

    private static EditMapCardVm Card(EditPageVm vm, string slotId) =>
        PartRow(vm).Children[0].MapGroups.SelectMany(group => group.Cards)
            .Single(card => card.Slot.SlotId == slotId);

    [Fact]
    public void Shading_appears_once_per_material_group_on_both_edit_shapes()
    {
        var (stockVm, _, _) = Page(TextureOnly());
        var stock = Assert.Single(PartRow(stockVm).Children[0].MapGroups);
        Assert.True(stock.HasShading);

        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.RecordReplacementOutputs("edit-long", 2);
        var (replacementVm, _) = On(session, shell => shell.Resolve = part =>
            Installed(part, 2, new[] { 3, 3 }));
        var replacement = PartRow(replacementVm).Children[0].MapGroups;
        Assert.Equal(2, replacement.Count);
        Assert.All(replacement, group => Assert.True(group.HasShading));
        Assert.Equal(2, replacement.Select(group => group.Shading).Distinct().Count());
    }

    [Fact]
    public async Task Typed_shading_values_bind_through_the_session_and_the_row_reports_them()
    {
        string root = Path.Combine(Path.GetTempPath(), "remold-shading-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (vm, session, shell) = Page(TextureOnly(), s =>
            {
                s.Resolve = part => Installed(part);
                s.ShadingEdits = new EditShadingValueEdit[]
                {
                    new("_UseGIFlatten", "0"),
                    new("_Anisotropy", "2.5"),
                };
            });
            session.SetRootDir(root);
            var row = ShadingRow(vm, "edit-long");
            Assert.False(row.IsEdited);

            await vm.EditShadingValuesCommand.ExecuteAsync(row);

            var project = session.Snapshot();
            var edit = project.EditDefinitions.Single(candidate => candidate.Id == "edit-long");
            var bound = project.TargetSlots.Where(slot =>
                slot.Input == TargetInputKind.MaterialValue && edit.Bindings.Any(binding =>
                    binding.SlotId == slot.Id && binding.Kind == BindingKind.ProjectAsset)).ToList();
            Assert.Equal(new[] { "_Anisotropy", "_UseGIFlatten" },
                bound.Select(slot => slot.Semantic).OrderBy(x => x, StringComparer.Ordinal));
            Assert.Empty(AuthoredProjectValidator.Errors(project));

            var again = ShadingRow(vm, "edit-long");
            Assert.True(again.IsEdited);
            Assert.Equal("2 values set", again.Summary);
            Assert.Equal("0", again.AuthoredValues["_UseGIFlatten"]);
            Assert.Equal("2.5", again.AuthoredValues["_Anisotropy"]);

            // reopening hands the dialog what is set, so it can pre-fill
            shell.ShadingEdits = null;
            await vm.EditShadingValuesCommand.ExecuteAsync(again);
            Assert.Equal("0", shell.LastShadingAuthored!["_UseGIFlatten"]);

            long revisionBeforeRevert = session.Revision;
            int notificationsBeforeRevert = shell.ProjectChangedCalls;
            vm.RevertShadingCommand.Execute(ShadingRow(vm, "edit-long"));
            Assert.Equal(revisionBeforeRevert + 1, session.Revision);
            Assert.Equal(notificationsBeforeRevert + 1, shell.ProjectChangedCalls);
            Assert.False(ShadingRow(vm, "edit-long").IsEdited);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Copied_shading_binds_the_exact_source_material_slot()
    {
        var (vm, session, shell) = Page(TextureOnly(), s =>
        {
            s.Resolve = part => Installed(part);
            s.ShadingSource = new EditShadingSource(AuthoredEditFixtures.Hair, 0,
                "hair · mat0", new[]
                {
                    new EditShadingCopyRow("_UseGIFlatten", "Skin lighting", "1", "0"),
                });
        });
        var row = ShadingRow(vm, "edit-long");

        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(row);

        Assert.Equal(1, shell.ConfirmCalls);
        var project = session.Snapshot();
        var edit = project.EditDefinitions.Single(candidate => candidate.Id == "edit-long");
        var carrier = project.TargetSlots.Single(slot =>
            slot.Input == TargetInputKind.MaterialValue && slot.Part.SameAs(Body));
        var source = project.TargetSlots.Single(slot =>
            slot.Input == TargetInputKind.MaterialValue && slot.Part.SameAs(AuthoredEditFixtures.Hair));
        var binding = edit.Bindings.Single(candidate => candidate.SlotId == carrier.Id);
        Assert.Equal(BindingKind.SourceSlot, binding.Kind);
        Assert.Equal(source.Id, binding.SourceSlot!.SlotId);
        Assert.Empty(AuthoredProjectValidator.Errors(project));

        // the cheap row carries the copy marker; the shell resolves its number when the dialog opens
        var again = ShadingRow(vm, "edit-long");
        Assert.True(again.IsEdited);
        Assert.Equal("", again.AuthoredValues["_UseGIFlatten"]);

        // reverting returns the value to the original
        vm.RevertShadingCommand.Execute(again);
        Assert.DoesNotContain(session.Snapshot().TargetSlots, slot => slot.Id == carrier.Id);
        Assert.False(ShadingRow(vm, "edit-long").IsEdited);
    }

    [Fact]
    public async Task Clearing_one_copied_shading_field_keeps_the_other_copy()
    {
        var (vm, session, shell) = Page(TextureOnly(), s =>
        {
            s.Resolve = part => Installed(part);
            s.ShadingSource = new EditShadingSource(AuthoredEditFixtures.Hair, 0,
                "hair material", new[]
                {
                    new EditShadingCopyRow(MaterialValueSemantics.UseGiFlatten,
                        "Skin lighting", "1", "0"),
                    new EditShadingCopyRow("_StockingCenterColor",
                        "Stocking centre colour", "0 0 0 1", "1 1 1 1"),
                });
        });

        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(ShadingRow(vm, "edit-long"));
        var copied = ShadingRow(vm, "edit-long");
        Assert.Equal(2, copied.AuthoredValues.Count);
        shell.ShadingEdits = new[]
        {
            new EditShadingValueEdit(MaterialValueSemantics.UseGiFlatten, null),
        };

        await vm.EditShadingValuesCommand.ExecuteAsync(copied);

        var remaining = ShadingRow(vm, "edit-long");
        Assert.DoesNotContain(MaterialValueSemantics.UseGiFlatten, remaining.AuthoredValues.Keys);
        Assert.Equal("", remaining.AuthoredValues["_StockingCenterColor"]);
        var project = session.Snapshot();
        Assert.DoesNotContain(project.TargetSlots, slot => slot.Part.SameAs(Body)
            && slot.Input == TargetInputKind.MaterialValue
            && slot.Semantic == MaterialValueSemantics.UseGiFlatten);
        Assert.Contains(project.EditDefinitions.Single(edit => edit.Id == "edit-long").Bindings,
            binding => binding.Kind == BindingKind.SourceSlot
                && project.TargetSlots.Single(slot => slot.Id == binding.SlotId).Semantic
                    == "_StockingCenterColor");
    }

    [Fact]
    public async Task A_source_that_already_matches_copies_nothing_and_says_so()
    {
        var (vm, session, shell) = Page(TextureOnly(), s =>
        {
            s.Resolve = part => Installed(part);
            s.ShadingSource = new EditShadingSource(AuthoredEditFixtures.Hair, 0,
                "hair · mat0", Array.Empty<EditShadingCopyRow>());
        });
        long before = session.Revision;

        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(ShadingRow(vm, "edit-long"));

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(before, session.Revision);
        Assert.Equal(EditPageVm.ShadingAlreadyMatches, vm.Status);
    }

    [Fact]
    public async Task Canceling_shading_is_silent_but_dialog_failures_use_the_status_line()
    {
        var (vm, _, shell) = Page(TextureOnly(), s => s.Resolve = part => Installed(part));
        var row = ShadingRow(vm, "edit-long");
        vm.Status = "Ready.";

        await vm.EditShadingValuesCommand.ExecuteAsync(row);
        Assert.Equal("Ready.", vm.Status);

        shell.ShadingEditFailure = new InvalidOperationException("fixture failure");
        await vm.EditShadingValuesCommand.ExecuteAsync(row);
        Assert.Equal(EditPageVm.EditShadingValuesFailed, vm.Status);

        shell.ShadingCopyFailure = new InvalidOperationException("fixture failure");
        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(row);
        Assert.Equal(EditPageVm.CopyShadingFailed, vm.Status);

        // A material whose shader has nothing to set is the fifth outcome of the same button, and it
        // speaks on the same line as the other four rather than raising a modal to be dismissed.
        shell.ShadingEditFailure = new EditShadingFailureException(
            MainWindowViewModel.NoAdjustableValues);
        int asked = shell.ConfirmCalls;
        await vm.EditShadingValuesCommand.ExecuteAsync(row);
        Assert.Equal(MainWindowViewModel.NoAdjustableValues, vm.Status);
        Assert.Equal(asked, shell.ConfirmCalls);

        shell.ShadingCopyFailure = new EditShadingFailureException(
            MainWindowViewModel.NoAdjustableValues);
        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(row);
        Assert.Equal(MainWindowViewModel.NoAdjustableValues, vm.Status);
        Assert.Equal(asked, shell.ConfirmCalls);
    }

    [Fact]
    public async Task Canceling_either_shading_dialog_does_not_touch_the_texture_preview_state()
    {
        var (vm, _, _) = Page(TextureOnly(), shell => shell.Resolve = part => Installed(part));
        var card = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long")
            .MapGroups.SelectMany(group => group.Cards).First();
        card.MarkThumbFailed();
        var thumbnail = card.Thumbnail;
        bool loading = card.IsThumbLoading;
        var row = ShadingRow(vm, "edit-long");

        await vm.EditShadingValuesCommand.ExecuteAsync(row);

        Assert.Same(thumbnail, card.Thumbnail);
        Assert.Equal(loading, card.IsThumbLoading);

        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(row);

        Assert.Same(thumbnail, card.Thumbnail);
        Assert.Equal(loading, card.IsThumbLoading);
    }

    [Fact]
    public async Task Bare_materials_show_shading_and_cancel_or_no_effect_keeps_the_part_bare()
    {
        var (vm, session, shell) = Page(BareWithSelection(), candidate =>
        {
            candidate.Resolve = part => Installed(part);
            candidate.Parts = (_, _) => new[] { Body };
        });
        var row = await BareShadingRow(vm);

        Assert.True(Assert.Single(PartRow(vm).MapGroups).HasShading);
        Assert.True(row.IsFirstEdit);
        Assert.False(row.IsEdited);
        Assert.False(row.ShowsRevert);   // nothing to take back yet — the verb hides, like the bare cards'

        await vm.EditShadingValuesCommand.ExecuteAsync(row);
        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(row);

        shell.ShadingEdits = Array.Empty<EditShadingValueEdit>();
        await vm.EditShadingValuesCommand.ExecuteAsync(row);
        shell.ShadingSource = new EditShadingSource(AuthoredEditFixtures.Hair, 0,
            "hair · mat0", Array.Empty<EditShadingCopyRow>());
        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(row);

        Assert.Empty(EditsFor(session));
        Assert.True(PartRow(vm).IsPart);
    }

    [Fact]
    public async Task Committed_bare_shading_values_mint_the_first_Always_edit()
    {
        var (vm, session, shell) = Page(Bare(), candidate =>
        {
            candidate.Resolve = part => Installed(part);
            candidate.ShadingEdits = new[]
            {
                new EditShadingValueEdit(MaterialValueSemantics.UseGiFlatten, "0"),
            };
        });

        await vm.EditShadingValuesCommand.ExecuteAsync(await BareShadingRow(vm));

        var edit = Assert.Single(EditsFor(session));
        Assert.True(Assert.Single(edit.Placements).IsAlways);
        Assert.Equal(edit.Id, vm.SelectedNode!.EditDefinitionId);
        Assert.Equal("0", ShadingRow(vm, edit.Id).AuthoredValues[MaterialValueSemantics.UseGiFlatten]);
        Assert.True(ShadingRow(vm, edit.Id).ShowsRevert);
        Assert.True(shell.LastShadingAddsFirstEdit);
        Assert.Equal("Added Edit 1. Used in Always.", vm.Status);
    }

    [Fact]
    public async Task Committed_bare_shading_copy_mints_the_first_Always_edit()
    {
        var (vm, session, shell) = Page(Bare(), candidate =>
        {
            candidate.Resolve = part => Installed(part);
            candidate.ShadingSource = new EditShadingSource(AuthoredEditFixtures.Hair, 0,
                "hair · mat0", new[]
                {
                    new EditShadingCopyRow(MaterialValueSemantics.UseGiFlatten,
                        "Skin lighting", "1", "0"),
                });
        });

        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(await BareShadingRow(vm));

        var edit = Assert.Single(EditsFor(session));
        Assert.True(Assert.Single(edit.Placements).IsAlways);
        Assert.Equal(edit.Id, vm.SelectedNode!.EditDefinitionId);
        Assert.Equal("", ShadingRow(vm, edit.Id).AuthoredValues[MaterialValueSemantics.UseGiFlatten]);
        Assert.Contains(EditPageVm.AddsFirstEdit, shell.LastConfirmBody);
        Assert.Equal("Added Edit 1. Used in Always.", vm.Status);
    }

    [Fact]
    public async Task Committed_original_shading_values_report_no_effect_and_keep_a_bare_part_bare()
    {
        var (vm, session, shell) = Page(Bare(), candidate =>
        {
            candidate.Resolve = part => Installed(part);
            candidate.ShadingEdits = Array.Empty<EditShadingValueEdit>();
            candidate.ShadingMatchesOriginal = true;
        });

        await vm.EditShadingValuesCommand.ExecuteAsync(await BareShadingRow(vm));

        Assert.Empty(EditsFor(session));
        Assert.Equal(EditPageVm.ShadingMatchesOriginal, vm.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_bare_shading_write_discards_the_minted_edit(bool copy)
    {
        var (vm, session, shell) = Page(BareWithSelection(), candidate =>
        {
            candidate.Resolve = part => Installed(part);
            candidate.Parts = (_, _) => new[] { Body };
        });
        var row = await BareShadingRow(vm);
        if (copy)
        {
            shell.ShadingSource = new EditShadingSource(AuthoredEditFixtures.Hair, 0,
                "hair · mat0", new[]
                {
                    new EditShadingCopyRow("_NotAuthorable", "Unknown", "1", "0"),
                });
            await vm.CopyShadingFromMaterialCommand.ExecuteAsync(row);
        }
        else
        {
            shell.ShadingEdits = new[] { new EditShadingValueEdit("_NotAuthorable", "1") };
            await vm.EditShadingValuesCommand.ExecuteAsync(row);
        }

        Assert.Empty(EditsFor(session));
        Assert.True(PartRow(vm).IsPart);
    }

    [Fact]
    public async Task A_missing_game_install_puts_the_exact_shading_failure_on_the_status_line()
    {
        var shell = new MainWindowViewModel(startLoad: false);
        var vm = new EditPageVm(shell);
        vm.Load(new AuthoredEditSession(TextureOnly()));
        // The real shell completes its cold subject/install answer asynchronously. Let that landing redraw
        // finish before exercising a command that walks the visible tree to publish its busy gate.
        await Task.Delay(50);
        var row = ShadingRow(vm, "edit-long");

        await vm.EditShadingValuesCommand.ExecuteAsync(row);

        Assert.Equal(EditPageVm.ShadingInstallUnavailable, vm.Status);

        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(row);

        Assert.Equal(EditPageVm.ShadingInstallUnavailable, vm.Status);
    }

    [Fact]
    public async Task An_unreadable_shading_source_is_not_reported_as_already_matching()
    {
        var (vm, session, shell) = Page(TextureOnly(), s =>
        {
            s.Resolve = part => Installed(part);
            s.ShadingCopyFailure = new EditShadingFailureException(
                EditPageVm.ShadingSourceUnreadable);
        });
        long before = session.Revision;

        await vm.CopyShadingFromMaterialCommand.ExecuteAsync(ShadingRow(vm, "edit-long"));

        Assert.Equal(before, session.Revision);
        Assert.Equal(EditPageVm.ShadingSourceUnreadable, vm.Status);
        Assert.NotEqual(EditPageVm.ShadingAlreadyMatches, vm.Status);
    }

    [Fact]
    public void A_failed_source_read_is_the_unreadable_copy_answer_not_an_empty_match()
    {
        var info = new EditShadingInfo(new[]
        {
            new EditShadingField(MaterialValueSemantics.UseGiFlatten, "Skin lighting",
                MaterialValueKind.Float, 0, 1, "1"),
        });
        var source = Ref(74001, "mat0");

        var result = MainWindowViewModel.ReadShadingCopyRows(info, source, _ => null,
            (_, _) => throw new InvalidOperationException("must not parse absent bytes"));

        Assert.True(result.SourceUnreadable);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task Preview_invalidation_survives_a_reentrant_newer_metadata_revision()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.WithOwnedSlots());
        bool nested = false;
        session.Changed += (_, change) =>
        {
            if (nested || (change.Invalidation & AuthoredInvalidation.Preview) == 0) return;
            nested = true;
            session.SetIdentity("Nested metadata", "1.0", null, null, null, true, null, null);
        };
        var shell = new FakeShell
        {
            Session = session,
            PreviewsSucceed = true,
            Resolve = part => Installed(part),
        };
        var vm = new EditPageVm(shell);
        vm.Load(session);
        await vm.LoadPreviewsAsync(PartRow(vm).Children[0]);
        int before = shell.EditPreviewCalls;

        session.ChooseInheritedCarrier("edit-long", "slot-owned");
        await vm.LoadPreviewsAsync(PartRow(vm).Children.Single(row => row.EditDefinitionId == "edit-long"));

        Assert.True(nested);
        Assert.True(shell.EditPreviewCalls > before);
    }

    /// <summary>The card half of the same fact. A rebuild that ran ahead of the invalidation hands the filed
    /// thumbnails to the new cards, and the invalidation then disposes them — so the cards have to be told,
    /// or they draw a dead handle and never ask for another.</summary>
    [Fact]
    public async Task A_card_whose_thumbnail_was_taken_away_asks_for_another()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.WithOwnedSlots());
        bool nested = false;
        session.Changed += (_, change) =>
        {
            if (nested || (change.Invalidation & AuthoredInvalidation.Preview) == 0) return;
            nested = true;
            session.SetIdentity("Nested metadata", "1.0", null, null, null, true, null, null);
        };
        var (vm, shell) = On(session, s =>
        {
            s.PreviewsSucceed = true;
            s.Resolve = part => Installed(part);
        });
        var row = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        await vm.LoadPreviewsAsync(row);
        Assert.NotEmpty(row.MapGroups.SelectMany(group => group.Cards));
        int before = shell.MapPreviewCalls;

        session.ChooseInheritedCarrier("edit-long", "slot-owned");
        await vm.LoadPreviewsAsync(PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long"));

        Assert.True(nested);
        Assert.True(shell.MapPreviewCalls > before);
    }

    /// <summary>Nothing the page took away is left waiting on a producer that was never started. Both tests
    /// above ask for the picture again by hand, which is what a reselection does; this one asks for nothing
    /// and pins where the row and its cards actually SIT once the superseded change has been applied. The
    /// out-of-order case runs no rebuild, so the selection never moves and nothing else would ask — the row
    /// would hold a shimmer with no producer behind it until the modder selected away and back.
    ///
    /// <para>Measured at the shell's counters and at the row's own state rather than at a picture: whether a
    /// decode succeeds here depends on the runner's order (see this file's header), and both outcomes settle
    /// the row the same way.</para></summary>
    [Fact]
    public async Task A_superseded_change_leaves_no_row_waiting_on_a_render_nobody_asked_for()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.WithOwnedSlots());
        FakeShell? recorder = null;
        bool nested = false;
        int meshesAfterRebuild = 0;
        int thumbsAfterRebuild = 0;
        session.Changed += (_, change) =>
        {
            if (nested || (change.Invalidation & AuthoredInvalidation.Preview) == 0) return;
            nested = true;
            session.SetIdentity("Nested metadata", "1.0", null, null, null, true, null, null);
            // The newer revision has been applied by now, redraw and reselection included, so what the page
            // asks for after this line is what the SUPERSEDED change cost — nothing else is left to run.
            meshesAfterRebuild = recorder!.EditPreviewCalls;
            thumbsAfterRebuild = recorder.MapPreviewCalls;
        };
        var (vm, shell) = On(session, s =>
        {
            recorder = s;
            s.PreviewsSucceed = true;
            s.Resolve = part => Installed(part);
        });
        var selected = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        vm.SelectedNode = selected;
        await vm.LoadPreviewsAsync(selected);
        Assert.NotEmpty(selected.MapGroups.SelectMany(group => group.Cards));

        session.ChooseInheritedCarrier("edit-long", "slot-owned");

        Assert.True(nested, "the re-entrant commit never happened");
        // Only the identity revision applied on the ordinary route: the binding change is the superseded one.
        Assert.Equal(new[] { session.Revision }, shell.ProjectChangedRevisions);
        var row = vm.SelectedNode!;
        Assert.False(row.IsMeshPreviewLoading, "the row is waiting on a render nobody is producing");
        Assert.All(row.MapGroups.SelectMany(group => group.Cards),
            card => Assert.False(card.IsThumbLoading, "the card is waiting on a picture nobody is producing"));
        // Where it settled is the invariant; that the page ASKED is what says it settled for the right
        // reason rather than by never having been forgotten at all.
        Assert.True(shell.EditPreviewCalls > meshesAfterRebuild, "the forgotten render was never asked for");
        Assert.True(shell.MapPreviewCalls > thumbsAfterRebuild, "the forgotten thumbnails were never asked for");
    }

    private static GameAssetRef Ref(long pathId, string name) => new()
    {
        GameBuild = "26109",
        LogicalBundle = "characters/vesna_ssr01",
        PathId = pathId,
        Name = name,
    };

    private static AuthoredProject WithAsset(AuthoredProject project, string file, ProjectAssetKind kind)
    {
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "test-asset-" + project.ProjectAssets.Count,
            Kind = kind,
            Label = Path.GetFileNameWithoutExtension(file),
            File = file,
        });
        return project;
    }

    /// <summary>What an install that can answer for a part hands back: one material with a base colour and a
    /// toon ramp, which is the smallest shape <see cref="AuthoredEditSession.EnsurePartSlots"/> mints
    /// anything from.</summary>
    private static LegacyResolvedPart Installed(TargetPart part, int materials = 1,
        IReadOnlyList<int>? materialIndexCounts = null) => new(
        part,
        Ref(70001, part.RendererSlot),
        Ref(72001, part.RendererSlot + "_mesh"),
        Enumerable.Range(0, materials).Select(i => new LegacyResolvedMaterial(i, $"mat{i}",
            Ref(74001 + i, $"mat{i}"),
            new[]
            {
                new LegacyResolvedTexture(TargetInputKind.BaseColor, "bundle", $"base{i}", null,
                    Ref(75001 + i * 10, $"base{i}"), "_BaseMap"),
                new LegacyResolvedTexture(TargetInputKind.Ramp, "bundle", $"ramp{i}", null,
                    Ref(75002 + i * 10, $"ramp{i}"), "_RampMap"),
            })).ToArray(),
        MaterialIndexCounts: materialIndexCounts ?? Enumerable.Repeat(3, materials).ToArray());

    private static LegacyResolvedPart InstalledVocabulary(TargetPart part)
    {
        var shared = Ref(76000, "shared_detail");
        return new LegacyResolvedPart(part, Ref(70001, part.RendererSlot),
            Ref(72001, part.RendererSlot + "_mesh"),
            new[]
            {
                new LegacyResolvedMaterial(0, "mat0", Ref(74001, "mat0"),
                    new[]
                    {
                        new LegacyResolvedTexture(TargetInputKind.Texture, "bundle", "vertex", null,
                            Ref(76003, "vertex"), "_VertexAnimNoiseTex"),
                        new LegacyResolvedTexture(TargetInputKind.Ramp, "bundle", "ramp", null,
                            Ref(75002, "ramp"), "_RampMap"),
                        new LegacyResolvedTexture(TargetInputKind.Texture, "bundle", "shared_detail", 76000,
                            shared, "_DetailMask"),
                        new LegacyResolvedTexture(TargetInputKind.Blend, "bundle", "effect", null,
                            Ref(75005, "effect"), "_BlendTex"),
                        new LegacyResolvedTexture(TargetInputKind.Normal, "bundle", "normal", null,
                            Ref(75003, "normal"), "_BumpMap"),
                        new LegacyResolvedTexture(TargetInputKind.Texture, "bundle", "mask", null,
                            Ref(76002, "mask"), "_MaskTex"),
                        new LegacyResolvedTexture(TargetInputKind.BaseColor, "bundle", "base", null,
                            Ref(75001, "base"), "_BaseMap"),
                        new LegacyResolvedTexture(TargetInputKind.Texture, "bundle", "shared_detail", 76000,
                            shared, "_DetailAlbedo"),
                        new LegacyResolvedTexture(TargetInputKind.Rmo, "bundle", "rmo", null,
                            Ref(75004, "rmo"), "_RMOTex"),
                    }),
            }, MaterialIndexCounts: new[] { 3 });
    }

    private static AuthoredProject TextureOnly(AuthoredProject? source = null)
    {
        var project = source ?? AuthoredEditFixtures.Golden();
        var geometry = project.TargetSlots.Where(slot => slot.Input == TargetInputKind.Geometry)
            .Select(slot => slot.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var binding in project.EditDefinitions.SelectMany(edit => edit.Bindings)
                     .Where(binding => geometry.Contains(binding.SlotId)))
        {
            binding.Kind = BindingKind.TargetGameValue;
            binding.ProjectAssetId = null;
            binding.SourceSlot = null;
        }
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }

    /// <summary>A part the project knows the slots of and has authored no answer for — the bare part the
    /// tree shows with no edit rows under it.</summary>
    private static AuthoredProject Bare() => AuthoredEditFixtures.SlotsOnly();

    private static AuthoredProject BareWithSelection()
    {
        var project = Bare();
        project.WorkspaceIndex = new AuthoredWorkspaceIndex
        {
            Selection = { new SelectionEntry { Character = Body.Subject, Outfit = Body.Outfit } },
        };
        return project;
    }

    /// <summary>A mod with a subject picked in ① Pick and nothing else: the selection is workspace inventory
    /// and the authored model holds nothing at all — no composition, no edits, not even the part's slots.
    /// This is what a fresh mod IS until a part row mints something, and the outline cannot describe it.
    /// </summary>
    private static AuthoredEditSession Picked()
    {
        var session = new AuthoredEditSession(new AuthoredProject());
        session.SetWorkspaceIndex(new AuthoredWorkspaceIndex
        {
            Selection = { new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" } },
        });
        return session;
    }

    private static TargetPart Cloth => AuthoredEditFixtures.Part("c_vesna_cloth1_lod0");

    /// <summary>The tree's subject order is the order they were added to the mod. Authoring against a
    /// subject must not move its row — the edited-jumps-to-the-top shape this pins against.</summary>
    [Fact]
    public void Subjects_keep_their_added_order_even_when_a_later_one_carries_the_edits()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.SetWorkspaceIndex(new AuthoredWorkspaceIndex
        {
            Selection =
            {
                new SelectionEntry { Character = "Aster", Outfit = "AsterSSR01" },
                new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" },
            },
        });
        var (vm, _) = On(session);

        Assert.Equal(new[] { "Aster", "Vesna" }, vm.Nodes.Select(node => node.Subject).ToArray());
    }

    // ---- a hop asked for from another pane ----

    /// <summary>③ Build's row → Edit hop, and ① Pick's open. The page's tree is a fresh read redrawn the
    /// moment the step is entered, so the hop is served where it lands rather than held for a build.</summary>
    [Fact]
    public void A_hop_from_another_pane_selects_the_row_it_names()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden(), s => s.Token = _ => "body");

        vm.SelectPart(Body, "body");
        Assert.Equal(EditNodeKind.Part, vm.SelectedNode?.Kind);
        Assert.True(vm.SelectedNode?.Part?.SameAs(Body));

        vm.SelectSubject("Vesna", "VesnaSSR01");
        Assert.Equal(EditNodeKind.Subject, vm.SelectedNode?.Kind);
    }

    /// <summary>A part this tree does not carry leaves the selection where it is and says so on the page's
    /// own line. The hop otherwise arrives with nothing selected and nothing said.</summary>
    [Fact]
    public void A_hop_onto_a_row_this_tree_does_not_carry_says_so_and_changes_nothing()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden());
        var standing = PartRow(vm);
        vm.SelectedNode = standing;

        vm.SelectPart(AuthoredEditFixtures.Part("c_vesna_ghost_lod0"), "ghost");

        Assert.Same(standing, vm.SelectedNode);
        Assert.Equal("ghost isn't in this list.", vm.Status);
    }

    // ---- what the pane says when there is no intent to draw ----

    [Fact]
    public void A_mod_with_nothing_in_it_says_it_is_empty()
    {
        var (vm, _, _) = Page(new AuthoredProject());

        Assert.True(vm.IsEmpty);
    }

    // ---- the zero-to-first-part path ----

    [Fact]
    public void A_freshly_picked_subject_shows_the_installs_parts_before_anything_is_authored()
    {
        var (vm, _) = On(Picked(), s =>
        {
            s.Parts = (_, _) => new[] { Body, Cloth };
            s.Token = part => part.SameAs(Body) ? "body" : "cloth1";
        });

        var subject = Subject(vm);
        Assert.Equal("Vesna", subject.Title);
        // The internal stem is off the row: the friendly title tells this subject apart from every
        // other one in the tree on its own.
        Assert.Equal("2 parts", subject.Detail);
        Assert.Equal(new[] { "body", "cloth1" },
            subject.Children.Where(n => n.IsPart).Select(n => n.Title));
        // Nothing is authored against either of them yet, which is what makes each one a place a first edit
        // can be minted from.
        Assert.All(subject.Children.Where(n => n.IsPart), n => Assert.True(n.IsBarePart));
        Assert.False(vm.IsEmpty);
    }

    [Fact]
    public void A_first_edit_is_minted_from_an_install_derived_part_row()
    {
        var session = Picked();
        var (vm, _) = On(session, s =>
        {
            s.Parts = (_, _) => new[] { Body };
            s.Resolve = part => Installed(part);
        });

        vm.NewEditCommand.Execute(Subject(vm).Children.Single(n => n.IsPart));

        Assert.Contains(session.Outline().KnownParts, part => part.SameAs(Body));
        Assert.Equal("Edit 1", Assert.Single(EditsFor(session)).Label);
        // …and the tree it redrew shows it under the same row.
        Assert.Equal("Edit 1", Assert.Single(PartRow(vm).Children).Title);
    }

    /// <summary>Hiding a part the project has never touched works exactly as its first content edit does.
    /// A hide binds visibility on one of the part's own routes, so the click opens the part first, mints
    /// the hide and activates it by the one rule — opening the part is what every other minting route does
    /// first, and the hide was the one that skipped it and refused on a fresh part.</summary>
    [Fact]
    public void A_first_hide_is_minted_from_an_install_derived_part_row()
    {
        var session = Picked();
        var (vm, _) = On(session, s =>
        {
            s.Parts = (_, _) => new[] { Body };
            s.Resolve = part => Installed(part);
        });

        vm.HidePartCommand.Execute(Subject(vm).Children.Single(n => n.IsPart));

        var hide = Assert.Single(session.Outline().Edits, edit => edit.Kind == EditDefinitionKind.Hide);
        Assert.True(hide.Placements.Single().IsAlways);
        Assert.Equal("Added Hidden. Used in Always.", vm.Status);
    }

    [Fact]
    public void A_picked_subject_with_no_install_says_why_rather_than_reading_as_an_empty_mod()
    {
        var (vm, _) = On(Picked(),
            s => s.Install = new EditInstallState(Unavailable: MainWindowViewModel.EditGameUnavailable));

        // The subject is in the mod, so the empty-mod sentence would be a lie; what is missing is the
        // install that names its parts, and the tree-level line is what says so.
        Assert.False(vm.IsEmpty);
        Assert.True(vm.HasNodes);
        Assert.Equal("0 parts", Subject(vm).Detail);
        Assert.True(vm.IsUnavailable);
    }

    /// <summary>A project whose authored edits currently have no placement.</summary>
    private static AuthoredProject Unplaced()
    {
        var project = TextureOnly();
        project.Always.Clear();
        return project;
    }

    // ---- tree shape ----

    [Fact]
    public void Tree_is_subject_then_part_then_edits()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden(), s => s.Token = _ => "body");

        var subject = Subject(vm);
        Assert.Equal("Vesna", subject.Title);
        Assert.Equal("1 part", subject.Detail);

        var part = PartRow(vm);
        // The part reads by its short token, with the renderer slot under it: the shipped workbench's part
        // row exactly.
        Assert.Equal("body", part.Title);
        Assert.Equal("c_vesna_body_lod0", part.Detail);
        Assert.Equal("body", part.InspectorHeader);
        Assert.Equal("c_vesna_body_lod0 · 2 edits", part.InspectorDetail);

        Assert.Equal(new[] { "Long body", "Short body" }, part.Children.Select(n => n.Title));
        Assert.All(part.Children, n => Assert.Equal(EditNodeKind.Edit, n.Kind));
        // Each edit's materials are its child rows — one per group of the edit's own pane, in pane order,
        // and the branch starts closed.
        Assert.All(part.Children, n =>
        {
            Assert.Equal(n.MapGroups.Select(group => group.Title),
                n.Children.Select(child => child.Title));
            Assert.All(n.Children, child => Assert.Equal(EditNodeKind.Material, child.Kind));
            if (n.Children.Count > 0) Assert.False(n.IsExpanded);
        });
    }

    [Fact]
    public void An_edit_grows_a_closed_material_row_per_material_of_its_own_pane()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        // A replacement's pane lists ITS materials — here three unfolded submesh positions — not a stock
        // enumeration, and the child rows follow the pane.
        session.RecordReplacementOutputs("edit-long", 3);
        var (vm, _) = On(session);

        var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        Assert.Equal(3, edit.MapGroups.Count);
        Assert.False(edit.IsExpanded);
        Assert.Equal(edit.MapGroups.Select(group => group.Title),
            edit.Children.Select(child => child.Title));
        for (int i = 0; i < edit.Children.Count; i++)
        {
            var material = edit.Children[i];
            Assert.Equal(EditNodeKind.Material, material.Kind);
            Assert.Equal(i, material.MaterialOrdinal);
            Assert.Equal("edit-long", material.EditDefinitionId);
            // The child's pane is the edit's own group, not a copy: same cards, same shading row, so a
            // thumb or busy state landing on one surface is on both.
            Assert.Same(edit.MapGroups[i], Assert.Single(material.MapGroups));
            // None of the edit-level surfaces ride along: the row is the material's controls alone.
            Assert.False(material.IsRenameable);
            Assert.False(material.ShowsMeshPreview);
            Assert.False(material.IsContentEdit);
            // The seam form still addresses the OWNING edit by its own label.
            Assert.Equal("Long body", material.Edit!.Label);
        }
    }

    [Fact]
    public void A_material_selection_and_its_open_branch_survive_the_rebuild_a_change_causes()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.RecordReplacementOutputs("edit-long", 3);
        var (vm, _) = On(session);

        var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        edit.IsExpanded = true;
        vm.SelectedNode = edit.Children[1];

        session.RenameEdit("edit-long", "Renamed");

        var selected = vm.SelectedNode!;
        Assert.Equal(EditNodeKind.Material, selected.Kind);
        Assert.Equal(1, selected.MaterialOrdinal);
        Assert.Equal("edit-long", selected.EditDefinitionId);
        Assert.Equal("Renamed", selected.Edit!.Label);
        // The re-drawn edit row is open again, so the selected material row is on screen.
        Assert.True(PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long").IsExpanded);
    }

    [Fact]
    public async Task Deleting_the_edit_under_a_selected_material_falls_back_to_the_part_row()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.RecordReplacementOutputs("edit-long", 2);
        var (vm, _) = On(session);

        var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        edit.IsExpanded = true;
        vm.SelectedNode = edit.Children[0];

        await vm.DeleteEditCommand.ExecuteAsync(edit);

        Assert.Equal(EditNodeKind.Part, vm.SelectedNode?.Kind);
    }

    /// <summary>A verb that invalidates a picture by name does so AFTER its change's rebuild has restored
    /// the selection and started that row's preview loads — the invalidation cancels those in-flight
    /// requests, and before the fix the row sat in its loading shimmer until the modder selected away and
    /// back. The verb now asks again for what the selected row draws.</summary>
    [Fact]
    public async Task A_reverted_cards_preview_settles_without_selecting_away_and_back()
    {
        // A texture-only edit with an authored base colour: the one card shape whose Revert the pane
        // offers. WithBorrowedSlot authors the base; unbinding the geometry makes the edit texture-only.
        var session = new AuthoredEditSession(AuthoredEditFixtures.WithBorrowedSlot());
        session.Compound(change => change.ChooseTargetGameValue("edit-long", "slot-geometry"));
        var (vm, _) = On(session);
        var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        vm.SelectedNode = edit;
        await vm.LoadPreviewsAsync(edit);
        var card = edit.MapGroups[0].Cards.First(c => c.CanRevert);

        vm.RevertCardCommand.Execute(card);

        var reverted = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long")
            .MapGroups[0].Cards.First(c => c.Slot.SlotId == card.Slot.SlotId);
        for (int i = 0; i < 200 && reverted.IsThumbLoading; i++) await Task.Delay(5);
        Assert.False(reverted.IsThumbLoading);
    }

    /// <inheritdoc cref="A_reverted_cards_preview_settles_without_selecting_away_and_back"/>
    [Fact]
    public async Task A_reverted_meshs_preview_settles_without_selecting_away_and_back()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.RecordReplacementOutputs("edit-long", 1);
        var (vm, _) = On(session);
        var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        Assert.True(edit.HasMeshEdit);
        vm.SelectedNode = edit;
        await vm.LoadPreviewsAsync(edit);

        await vm.RevertMeshCommand.ExecuteAsync(edit);

        var reverted = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        for (int i = 0; i < 200 && reverted.IsMeshPreviewLoading; i++) await Task.Delay(5);
        Assert.False(reverted.IsMeshPreviewLoading);
    }

    [Fact]
    public void A_pending_rename_commit_from_a_material_row_does_not_blank_the_edits_name()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.RecordReplacementOutputs("edit-long", 2);
        var (vm, shell) = On(session);
        var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        vm.SelectedNode = edit.Children[0];

        // Any verb commits a pending rename first; from a material row there is nothing typed to commit.
        vm.GoToBuildCommand.Execute(vm.SelectedNode);

        Assert.Equal("Long body", EditsFor(session).Single(e => e.Id == "edit-long").Label);
        Assert.Equal("Long body", shell.LastBuildEdit!.Label);
    }

    [Fact]
    public void A_part_the_install_cannot_name_keeps_the_renderer_slot_as_its_title()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden());
        Assert.Equal("c_vesna_body_lod0", PartRow(vm).Title);
    }

    [Fact]
    public void Edit_rows_do_not_render_activation_details()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.CreateKeyGroup("F6", "edit-long", "Body cycle");
        session.CreateKeyGroup("F7", "edit-short", "Other cycle");
        var (vm, _) = On(session);
        var part = PartRow(vm);

        Assert.All(part.Children, row => Assert.Equal("", row.Detail));
        Assert.Equal("edit 1 of 2 on c_vesna_body_lod0", part.Children[0].InspectorDetail);
        Assert.Equal("edit 2 of 2 on c_vesna_body_lod0", part.Children[1].InspectorDetail);
        Assert.DoesNotContain("F6", part.Children.SelectMany(row => new[] { row.Detail, row.InspectorDetail }));
        Assert.DoesNotContain("F7", part.Children.SelectMany(row => new[] { row.Detail, row.InspectorDetail }));
    }

    [Fact]
    public void A_bare_part_has_no_edit_rows()
    {
        var (vm, _, _) = Page(Bare());
        var part = PartRow(vm);

        Assert.Empty(part.Children);
        Assert.True(part.IsBarePart);
        Assert.False(part.HasOverview);
        Assert.Equal("c_vesna_body_lod0 · no edits yet", part.InspectorDetail);
    }

    [Fact]
    public void A_hide_row_says_only_that_it_hides()
    {
        var project = Bare();
        var session = new AuthoredEditSession(project);
        session.AddHideEdit(Body);
        var (vm, _) = On(session);

        var hide = Assert.Single(PartRow(vm).Children);
        Assert.Equal("∅", hide.Glyph);
        Assert.True(hide.IsHideEdit);
        Assert.Equal("hides this part", hide.Detail);
        Assert.Empty(hide.MapGroups);
        // No materials, no material rows: a hide binds visibility and nothing else.
        Assert.Empty(hide.Children);
        // The ✎ roll-up is about content: a part answered only by a hide has none.
        Assert.False(PartRow(vm).HasEditBadge);
    }

    [Fact]
    public void The_edit_badge_rolls_up_onto_the_part_and_its_subject_and_stops_at_the_edit_row()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden());

        Assert.True(PartRow(vm).ShowsEditRollup);
        Assert.True(Subject(vm).ShowsEditRollup);
        // An edit row already leads with ✎ as its type glyph; a second one beside it says it twice.
        Assert.Equal("✎", PartRow(vm).Children[0].Glyph);
        Assert.False(PartRow(vm).Children[0].ShowsEditRollup);

        var (bare, _, _) = Page(Bare());
        Assert.False(PartRow(bare).ShowsEditRollup);
        Assert.False(Subject(bare).ShowsEditRollup);
    }

    [Fact]
    public void The_skeleton_row_stays_and_is_left_out_when_the_install_cannot_supply_one()
    {
        var (without, _, _) = Page(AuthoredEditFixtures.Golden());
        Assert.DoesNotContain(Subject(without).Children, n => n.IsSkeleton);

        var (with, _, _) = Page(AuthoredEditFixtures.Golden(),
            shell => shell.Skeleton = new EditSkeletonOutline(86, Array.Empty<SkeletonNodeVm>()));
        var skeleton = Assert.Single(Subject(with).Children, n => n.IsSkeleton);
        Assert.Equal("86 bones", skeleton.Detail);
    }

    [Fact]
    public void The_tree_carries_the_installs_own_reading_and_unavailable_states()
    {
        var (reading, _, _) = Page(AuthoredEditFixtures.Golden(),
            s => s.Install = new EditInstallState(IsReading: true));
        Assert.True(reading.IsReading);
        Assert.False(reading.IsUnavailable);

        var (down, _, _) = Page(AuthoredEditFixtures.Golden(),
            s => s.Install = new EditInstallState(Unavailable: "The game is running. Close it to read parts."));
        Assert.True(down.IsUnavailable);
        Assert.Equal("The game is running. Close it to read parts.", down.Unavailable);
    }

    // ---- filter ----

    [Fact]
    public void The_filter_matches_edit_labels()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden());
        vm.Filter = "short";

        var part = PartRow(vm);
        Assert.False(part.Children[0].IsVisible);
        Assert.True(part.Children[1].IsVisible);
        Assert.True(part.IsVisible);          // the path to the match
        Assert.True(Subject(vm).IsVisible);
        Assert.False(vm.NoMatches);
    }

    [Fact]
    public void The_filter_matches_both_a_texture_label_and_its_storage_filename()
    {
        var (vm, _, _) = Page(TextureOnly());
        vm.Filter = "Warm ramp";

        var part = PartRow(vm);
        Assert.True(part.Children[0].IsVisible);   // binds the asset labelled Warm ramp
        Assert.False(part.Children[1].IsVisible);

        vm.Filter = "warm.dds";
        Assert.True(part.Children[0].IsVisible);   // the same card's underlying storage filename
        Assert.False(part.Children[1].IsVisible);
    }

    [Fact]
    public void The_filter_finds_a_part_by_its_renderer_slot_and_never_by_where_build_put_an_edit()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden(), s => s.Token = _ => "body");

        vm.Filter = "c_vesna_body_lod0";
        Assert.True(PartRow(vm).IsVisible);

        // "always on" is where ③ Build put an edit. This page reports it; it does not answer questions
        // about composition, and a filter that matched it would.
        vm.Filter = "always on";
        Assert.True(vm.NoMatches);
    }

    [Fact]
    public void A_filter_that_hides_everything_says_so()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden());
        vm.Filter = "nothing-matches-this";
        Assert.True(vm.NoMatches);

        vm.ClearFilterCommand.Execute(null);
        Assert.False(vm.NoMatches);
        Assert.True(PartRow(vm).Children[0].IsVisible);
    }

    // ---- creating edits ----

    [Fact]
    public async Task Opening_a_bare_part_in_blender_opens_stock_and_mints_nothing()
    {
        var (vm, session, shell) = Page(Bare(), s => s.Resolve = part => Installed(part));
        long revision = session.Revision;

        await vm.OpenInBlenderCommand.ExecuteAsync(PartRow(vm));

        Assert.Empty(EditsFor(session));
        Assert.Equal(revision, session.Revision);
        Assert.Equal(1, shell.BlenderCalls);
        Assert.True(Body.SameAs(shell.LastBlenderPart!));
        Assert.Null(shell.LastBlenderEdit);
        Assert.False(shell.LastWithReferences);
        Assert.Equal("Opens the original part on its own, without the item's other parts.",
            PartRow(vm).OpenInBlenderHint);
    }

    [Fact]
    public async Task Opening_an_edit_row_addresses_that_edit_without_changing_the_project()
    {
        var (vm, session, shell) = Page(AuthoredEditFixtures.Golden());
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());
        var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-short");

        await vm.OpenWithReferencesCommand.ExecuteAsync(edit);

        Assert.Equal("edit-short", shell.LastBlenderEdit!.EditDefinitionId);
        Assert.True(Body.SameAs(shell.LastBlenderPart!));
        Assert.True(shell.LastWithReferences);
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
        Assert.Equal("Opens this edit with the item's other parts for reference.",
            edit.OpenWithReferencesHint);
    }

    // ---- the mesh-edit gate ----

    private const string MeshBlocked = "This mesh uses expressions and cannot be edited in Blender.";

    [Fact]
    public async Task A_blocked_mesh_refuses_the_blender_open_without_minting_an_edit()
    {
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.MeshEditBlock = _ => MeshBlocked;
        });

        await vm.OpenInBlenderCommand.ExecuteAsync(PartRow(vm));

        Assert.Empty(EditsFor(session));
        Assert.Equal(0, shell.BlenderCalls);
        Assert.Equal(MeshBlocked, vm.Status);
    }

    [Fact]
    public void A_blocked_mesh_disables_the_opens_with_the_reason_and_keeps_every_other_verb()
    {
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.MeshEditBlock = _ => MeshBlocked;
        });

        // Selection starts the gate read; the fake settles it inline.
        vm.SelectedNode = PartRow(vm);

        var row = PartRow(vm);
        Assert.Equal(MeshBlocked, row.MeshEditBlock);
        Assert.False(row.CanOpenInBlender);
        Assert.Equal(MeshBlocked, row.OpenInBlenderHint);
        Assert.Equal(MeshBlocked, row.OpenWithReferencesHint);

        // Maps, shading and Hide ride edits the part can still take.
        vm.NewEditCommand.Execute(PartRow(vm));
        var minted = Assert.Single(EditsFor(session));
        vm.HidePartCommand.Execute(PartRow(vm));
        Assert.Contains(EditsFor(session), edit => edit.Kind == EditDefinitionKind.Hide);

        // The rebuilds those verbs caused re-applied the settled answer to the part row AND the edit row,
        // whose inspector carries the same two opens — without re-reading the bundle.
        var editRow = PartRow(vm).Children.Single(node => node.EditDefinitionId == minted.Id);
        Assert.Equal(MeshBlocked, editRow.MeshEditBlock);
        Assert.False(editRow.CanOpenInBlender);
        Assert.Equal(MeshBlocked, PartRow(vm).MeshEditBlock);
        Assert.Equal(1, shell.MeshEditBlockCalls);
    }

    [Fact]
    public async Task A_blocked_mesh_refuses_the_references_open_on_an_edit_row_too()
    {
        var (vm, _, shell) = Page(AuthoredEditFixtures.Golden(), s =>
        {
            s.Resolve = part => Installed(part);
            s.MeshEditBlock = _ => MeshBlocked;
        });
        var edit = PartRow(vm).Children.First(node => node.IsContentEdit);

        await vm.OpenWithReferencesCommand.ExecuteAsync(edit);

        Assert.Equal(0, shell.BlenderCalls);
        Assert.Equal(MeshBlocked, vm.Status);
    }

    [Fact]
    public async Task A_click_that_beats_the_gate_read_awaits_it_rather_than_opening_past_it()
    {
        var hold = new TaskCompletionSource<string?>();
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.MeshEditGateHold = hold;
        });

        vm.SelectedNode = PartRow(vm);          // starts the read; the answer is still in flight
        Assert.True(PartRow(vm).CanOpenInBlender);   // unread is not blocked

        var clicked = vm.OpenInBlenderCommand.ExecuteAsync(PartRow(vm));
        hold.SetResult(MeshBlocked);
        await clicked;

        Assert.Empty(EditsFor(session));
        Assert.Equal(0, shell.BlenderCalls);
        Assert.Equal(MeshBlocked, vm.Status);
        Assert.Equal(MeshBlocked, PartRow(vm).MeshEditBlock);
        Assert.Equal(1, shell.MeshEditBlockCalls);   // the click awaited the selection's own read
    }

    [Fact]
    public void The_first_content_edit_takes_the_always_on_answer_and_later_ones_do_not()
    {
        var (vm, session, _) = Page(Bare(), s => s.Resolve = part => Installed(part));

        vm.NewEditCommand.Execute(PartRow(vm));
        var first = Assert.Single(EditsFor(session));
        Assert.Contains(first.Id, session.Snapshot().Always);
        Assert.Equal("", PartRow(vm).Children[0].Detail);
        // The two outcomes say what happened, which is not the same thing twice.
        Assert.Equal("Added Edit 1. Used in Always.", vm.Status);

        vm.NewEditCommand.Execute(PartRow(vm));
        var edits = EditsFor(session);
        Assert.Equal(new[] { "Edit 1", "Edit 2" }, edits.Select(e => e.Label));
        Assert.Contains(edits[0].Id, session.Snapshot().Always);
        Assert.DoesNotContain(edits[1].Id, session.Snapshot().Always);
        Assert.Equal("", PartRow(vm).Children[1].Detail);
        Assert.Equal($"Added Edit 2. {EditNodeVm.NotUsedYet} {EditPageVm.PlaceItInBuild}", vm.Status);
    }

    [Fact]
    public void A_new_edit_on_an_opened_part_does_not_need_the_install()
    {
        // Resolve answers null: the game is not mounted. The part's own recorded routes still are.
        var (vm, session, _) = Page(AuthoredEditFixtures.Saved());

        vm.NewEditCommand.Execute(PartRow(vm));

        Assert.Equal(2, EditsFor(session).Count);
        Assert.StartsWith("Added Edit 2", vm.Status);
    }

    [Fact]
    public void An_install_that_cannot_name_an_exact_object_refuses_the_whole_part_by_name()
    {
        var (vm, session, _) = Page(Bare(), s => s.Resolve = part => new LegacyResolvedPart(
            part, new GameAssetRef(), Ref(72001, "mesh"), Array.Empty<LegacyResolvedMaterial>()));

        vm.NewEditCommand.Execute(PartRow(vm));

        Assert.Empty(EditsFor(session));
        Assert.Equal("Couldn't find this part in the current game files.", vm.Status);
        Assert.DoesNotContain(Body.RendererSlot, vm.Status);
        // The sentence stays on the row after the status line moves on.
        Assert.Equal(vm.Status, PartRow(vm).Problem);
        Assert.True(PartRow(vm).HasProblem);
        Assert.Equal(vm.Status, PartRow(vm).PartRefusal);
    }

    [Fact]
    public void A_refusal_from_the_last_project_does_not_follow_the_next_one_in()
    {
        var (vm, _, shell) = Page(Bare(), s => s.Resolve = part => new LegacyResolvedPart(
            part, new GameAssetRef(), Ref(72001, "mesh"), Array.Empty<LegacyResolvedMaterial>()));
        vm.NewEditCommand.Execute(PartRow(vm));
        Assert.True(PartRow(vm).HasProblem);

        shell.Resolve = part => Installed(part);
        vm.Load(new AuthoredEditSession(Bare()));

        Assert.False(PartRow(vm).HasProblem);
        Assert.Null(PartRow(vm).Problem);
    }

    [Fact]
    public void Two_parts_whose_names_run_together_are_still_two_parts()
    {
        // Subject + outfit + slot concatenated is one string for both of these. A refusal filed under it
        // would show up on a part the install never refused.
        var project = Bare();
        var geometry = project.TargetSlots.Single(s => s.Id == "slot-geometry");
        var twin = AuthoredProjectSerializer.Deserialize(AuthoredProjectSerializer.Serialize(project))
            .TargetSlots.Single(s => s.Id == "slot-geometry");
        twin.Id = "slot-twin";
        twin.Part = new TargetPart
        {
            Subject = "Vesna", Outfit = "VesnaSSR0", RendererSlot = "1" + geometry.Part.RendererSlot,
        };
        project.TargetSlots.Add(twin);

        var (vm, _, _) = Page(project, s => s.Resolve = part =>
            part.RendererSlot == geometry.Part.RendererSlot
                ? new LegacyResolvedPart(part, new GameAssetRef(), Ref(72001, "mesh"),
                    Array.Empty<LegacyResolvedMaterial>())
                : Installed(part));

        // Two subjects now, so the rows are found by the part each one addresses.
        EditNodeVm Row(string slot) => vm.Nodes.SelectMany(n => n.Children)
            .Single(n => n.IsPart && n.Part!.RendererSlot == slot);

        vm.NewEditCommand.Execute(Row(geometry.Part.RendererSlot));

        Assert.True(Row(geometry.Part.RendererSlot).HasProblem);
        var other = Row(twin.Part.RendererSlot);
        Assert.Null(other.Problem);
        Assert.False(other.IsBusy);
    }

    [Fact]
    public void Duplicating_an_edit_leaves_it_in_the_library_until_build_assigns_it()
    {
        var (vm, session, _) = Page(AuthoredEditFixtures.Saved());
        var edit = PartRow(vm).Children[0];

        vm.DuplicateEditCommand.Execute(edit);

        var edits = EditsFor(session);
        Assert.Equal(2, edits.Count);
        Assert.Empty(edits[1].Placements);
        Assert.Equal("", PartRow(vm).Children[1].Detail);
    }

    [Fact]
    public async Task Revert_mesh_confirms_by_name_and_has_something_to_take_back_only_where_geometry_was_replaced()
    {
        var (vm, session, shell) = Page(AuthoredEditFixtures.Golden());
        var edit = PartRow(vm).Children[0];
        Assert.True(edit.CanRevertMesh);

        await vm.RevertMeshCommand.ExecuteAsync(edit);

        Assert.Equal(1, shell.ConfirmCalls);
        Assert.Contains("Long body", shell.LastConfirmTitle);
        Assert.Equal("Revert", shell.LastConfirmLabel);
        Assert.True(shell.LastConfirmDangerous);
        Assert.Contains("This cannot be undone.", shell.LastConfirmBody);
        Assert.Contains("Its maps are kept.", shell.LastConfirmBody);

        Assert.Equal(BindingKind.TargetGameValue,
            session.Slots("edit-long").Single(s => s.Slot.Id == "slot-geometry").Binding.Kind);
        var reverted = PartRow(vm).Children[0];
        Assert.False(reverted.CanRevertMesh);
        Assert.Equal("Nothing to revert yet", reverted.RevertMeshHint);
        // The maps it binds are untouched: only geometry went back.
        Assert.Equal(BindingKind.ProjectAsset,
            session.Slots("edit-long").Single(s => s.Slot.Id == "slot-ramp").Binding.Kind);
    }

    [Fact]
    public async Task A_declined_mesh_revert_changes_nothing()
    {
        var (vm, session, shell) = Page(AuthoredEditFixtures.Golden(), s => s.ConfirmResult = false);

        await vm.RevertMeshCommand.ExecuteAsync(PartRow(vm).Children[0]);

        Assert.Equal(BindingKind.ProjectAsset,
            session.Slots("edit-long").Single(s => s.Slot.Id == "slot-geometry").Binding.Kind);
        Assert.Equal(0, shell.ProjectChangedCalls);
    }

    // ---- hide ----

    /// <summary>A new hide is activated by the rule every new edit is activated by: a part with no answer
    /// on the board takes it into Always, a part that already has one keeps it in the library until ③ Build
    /// assigns it. Adding a content edit to the same two parts answers the same way — that is the point.
    /// </summary>
    [Fact]
    public void A_new_hide_is_activated_by_the_rule_every_new_edit_is()
    {
        var unanswered = AuthoredEditFixtures.Golden();
        unanswered.Always.Clear();
        var (vm, session, _) = Page(unanswered);

        vm.HidePartCommand.Execute(PartRow(vm));

        var edits = EditsFor(session);
        var hide = Assert.Single(edits, e => e.Kind == EditDefinitionKind.Hide);
        Assert.True(hide.Placements.Single().IsAlways);
        // It coexists with the content edits rather than destroying them.
        Assert.Equal(2, edits.Count(e => e.Kind == EditDefinitionKind.Content));
        Assert.Equal("Added Hidden. Used in Always.", vm.Status);

        var (answered, answeredSession, _) = Page(AuthoredEditFixtures.Golden());
        answered.HidePartCommand.Execute(PartRow(answered));
        Assert.Empty(Assert.Single(EditsFor(answeredSession),
            e => e.Kind == EditDefinitionKind.Hide).Placements);
    }

    /// <summary>The second press adds nothing, and says so: an "Added" line for a part that already has
    /// its hide reports a change that never happened. Where the standing hide is used is the fact that
    /// answers the question behind the second press.</summary>
    [Fact]
    public void Hiding_a_part_twice_leaves_one_hide_used_once()
    {
        var project = AuthoredEditFixtures.Golden();
        project.Always.Clear();
        var (vm, session, _) = Page(project);

        vm.HidePartCommand.Execute(PartRow(vm));
        vm.HidePartCommand.Execute(PartRow(vm));

        Assert.Equal("Hidden already exists. Used in Always.", vm.Status);
        var hide = Assert.Single(session.Snapshot().EditDefinitions,
            edit => edit.Kind == EditDefinitionKind.Hide);
        Assert.Equal(1, session.Snapshot().Always.Count(id => id == hide.Id));
    }

    [Fact]
    public void Hide_on_a_group_used_part_stays_unplaced_without_narrating_the_group()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        string group = session.CreateKeyGroup("F6", "edit-long", "Body cycle");
        var (vm, _) = On(session);

        vm.HidePartCommand.Execute(PartRow(vm));

        var hide = Assert.Single(EditsFor(session),
            e => e.Kind == EditDefinitionKind.Hide);
        Assert.Empty(hide.Placements);
        // What it says: added, not used yet, and where to put it. What it never says: the group that
        // claimed the part, or any id.
        Assert.Equal($"Added Hidden. {EditNodeVm.NotUsedYet} {EditPageVm.PlaceItInBuild}", vm.Status);
        Assert.DoesNotContain("F6", vm.Status);
        Assert.DoesNotContain(group, vm.Status);
        Assert.Equal(hide.Id, vm.SelectedNode!.EditDefinitionId);
    }

    [Fact]
    public void Hiding_a_claimed_part_twice_reuses_the_one_hide_edit()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.CreateKeyGroup("F6", "edit-long", "Body cycle");
        var (vm, _) = On(session);

        vm.HidePartCommand.Execute(PartRow(vm));
        vm.HidePartCommand.Execute(PartRow(vm));

        Assert.Single(EditsFor(session),
            e => e.Kind == EditDefinitionKind.Hide);
    }

    [Fact]
    public void A_hide_edits_inspector_explains_deletion_without_a_build_handoff()
    {
        Assert.Contains("Deleting this edit shows the part again.", EditNodeVm.HideExplanation);
        Assert.DoesNotContain("③ Build", EditNodeVm.HideExplanation);
    }

    // ---- rename ----

    [Fact]
    public void Renaming_commits_through_the_session_and_a_blank_name_restores_the_default()
    {
        var (vm, session, _) = Page(AuthoredEditFixtures.Saved());

        var edit = PartRow(vm).Children[0];
        edit.EditLabel = "Long coat";
        vm.CommitRenameCommand.Execute(edit);
        Assert.Equal("Long coat",
            EditsFor(session)[0].Label);
        Assert.Equal("Long coat", PartRow(vm).Children[0].Title);

        var renamed = PartRow(vm).Children[0];
        renamed.EditLabel = "   ";
        vm.CommitRenameCommand.Execute(renamed);
        Assert.Equal("Edit 1", EditsFor(session)[0].Label);
    }

    [Fact]
    public void Typing_a_name_and_choosing_another_row_keeps_the_old_name()
    {
        var (vm, session, _) = Page(AuthoredEditFixtures.Golden());
        var edit = PartRow(vm).Children[0];
        vm.SelectedNode = edit;

        edit.EditLabel = "Long coat";
        // Choosing a different row is the one thing that discards what was typed.
        vm.SelectEditCommand.Execute(PartRow(vm).Children[1]);

        Assert.Equal("Long body", EditsFor(session)[0].Label);
    }

    [Fact]
    public void A_verb_elsewhere_on_the_page_lands_the_typed_name_first()
    {
        var (vm, session, _) = Page(TextureOnly());
        var edit = PartRow(vm).Children[0];
        vm.SelectedNode = edit;
        edit.EditLabel = "Long coat";

        // A card verb redraws the tree under the box. What was typed is committed, not thrown away.
        vm.RevertRampCommand.Execute(edit.MapGroups[0].Cards[0]);

        Assert.Equal("Long coat", EditsFor(session)[0].Label);
    }

    /// <summary>A hide edit is renamed by the route every other edit is renamed by, down to a cleared name
    /// restoring the default — which for a hide is "Hidden". ③ Build lists edits by name, so a mod that
    /// hides four parts has four rows worth telling apart.</summary>
    [Fact]
    public void A_hide_edit_is_named_by_the_route_every_edit_is_named_by()
    {
        var session = new AuthoredEditSession(Bare());
        session.AddHideEdit(Body);
        var (vm, _) = On(session);

        var hide = Assert.Single(PartRow(vm).Children);
        Assert.True(hide.IsRenameable);

        hide.EditLabel = "Skirt off";
        vm.CommitRenameCommand.Execute(hide);
        Assert.Equal("Skirt off", EditsFor(session)[0].Label);

        PartRow(vm).Children.Single().EditLabel = "   ";
        vm.CommitRenameCommand.Execute(PartRow(vm).Children.Single());
        Assert.Equal("Hidden", EditsFor(session)[0].Label);
    }

    // ---- delete ----

    [Fact]
    public async Task Deleting_a_used_edit_counts_its_use_and_removes_the_edit()
    {
        var (vm, session, shell) = Page(AuthoredEditFixtures.Golden());

        await vm.DeleteEditCommand.ExecuteAsync(PartRow(vm).Children[0]);

        Assert.Equal(1, shell.ConfirmCalls);
        Assert.Contains("Long body", shell.LastConfirmTitle);
        Assert.Equal("Delete", shell.LastConfirmLabel);
        Assert.Contains("This edit is used in Always.", shell.LastConfirmBody);
        Assert.Contains("This cannot be undone.", shell.LastConfirmBody);

        Assert.DoesNotContain("edit-long", session.Snapshot().Always);
        Assert.Equal(new[] { "Short body" }, EditsFor(session).Select(e => e.Label));
        Assert.Equal("Deleted Long body.", vm.Status);
    }

    [Fact]
    public async Task A_key_state_choosing_the_edit_counts_as_a_use()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.CreateKeyGroup("F6", "edit-long", "Body cycle");
        var (vm, shell) = On(session, s => s.ConfirmResult = false);

        await vm.DeleteEditCommand.ExecuteAsync(PartRow(vm).Children[0]);

        Assert.Contains("This edit is used in 1 state.", shell.LastConfirmBody);
        // Declined: the confirm asked before anything happened, and nothing did.
        Assert.Equal(2, EditsFor(session).Count);
    }

    [Fact]
    public async Task Delete_confirm_counts_an_edits_placements_across_every_group()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.CreateKeyGroup("F6", "edit-long", "First cycle");
        string second = session.CreateKeyGroup("F7", "edit-short", "Second cycle");
        session.PlaceEdit("edit-long", second, "state-0002");
        var (vm, shell) = On(session, candidate => candidate.ConfirmResult = false);

        await vm.DeleteEditCommand.ExecuteAsync(PartRow(vm).Children.Single(row =>
            row.EditDefinitionId == "edit-long"));

        Assert.Contains("This edit is used in 2 states.", shell.LastConfirmBody);
        Assert.Equal(2, vm.PlacementCount("edit-long"));
    }

    [Fact]
    public async Task An_unused_edit_says_so_in_the_confirm()
    {
        var (vm, _, shell) = Page(AuthoredEditFixtures.Golden(), s => s.ConfirmResult = false);

        await vm.DeleteEditCommand.ExecuteAsync(PartRow(vm).Children[1]);

        Assert.Contains("This edit isn't used anywhere.", shell.LastConfirmBody);
    }

    [Fact]
    public async Task A_declined_delete_changes_nothing()
    {
        var (vm, session, shell) = Page(AuthoredEditFixtures.Golden(), s => s.ConfirmResult = false);

        await vm.DeleteEditCommand.ExecuteAsync(PartRow(vm).Children[0]);

        Assert.Equal(2, EditsFor(session).Count);
        Assert.Equal(0, shell.ProjectChangedCalls);
    }

    // ---- cards ----

    [Fact]
    public void A_texture_only_edit_keeps_the_stock_material_group_shape()
    {
        var (vm, _, _) = Page(TextureOnly());

        var group = Assert.Single(PartRow(vm).Children[0].MapGroups);
        Assert.Equal("body_material", group.Title);
        var set = Assert.Single(group.Sets);
        Assert.False(set.HasLabel);
        var card = Assert.Single(group.Cards);
        Assert.Equal("Toon ramp", card.MapLabel);
        Assert.True(card.IsRamp);
        Assert.True(card.IsGameSlot);
        Assert.True(card.HasEditBadge);            // the mod owns what this slot binds
        Assert.Equal("Warm ramp", card.TextureName);
        // A ramp is picked, not painted: no Open, no UV guide.
        Assert.False(card.CanOpen);
        Assert.False(card.CanOpenUvGuide);
        Assert.Equal(RampCardState.PickedCaption, card.RampState.Caption);
    }

    [Fact]
    public void An_authored_card_prefers_its_friendly_label_and_keeps_a_filename_fallback()
    {
        var project = TextureOnly();
        var asset = project.ProjectAssets.Single(candidate => candidate.Id == "ramp-warm");
        asset.File = "assets/edits/edit-long/slots/slot-ramp/asset-0123456789abcdef0123456789abcdef.dds";
        asset.Label = "Sunset ramp";
        var (vm, _, _) = Page(project);

        var card = PartRow(vm).Children[0].MapGroups[0].Cards[0];

        Assert.Equal(asset.File, card.BoundFile);
        Assert.Equal("asset-0123456789abcdef0123456789abcdef.dds", card.BoundFileName);
        Assert.Equal("Sunset ramp", card.TextureName);
        Assert.DoesNotContain("asset-", card.TextureName);
        Assert.Equal(RampCardState.PickedCaption, card.RampState.Caption);

        var malformed = new EditMapCardVm(card.Slot, BindingKind.ProjectAsset, asset.File,
            boundLabel: "   ");
        Assert.Equal("asset-0123456789abcdef0123456789abcdef.dds", malformed.TextureName);

        var laundered = new EditMapCardVm(card.Slot, BindingKind.ProjectAsset, asset.File,
            boundLabel: "asset-0123456789abcdef0123456789abcdef.dds");
        Assert.Null(laundered.BoundLabel);
        Assert.Equal("asset-0123456789abcdef0123456789abcdef.dds", laundered.TextureName);

        var legacy = new EditMapCardVm(card.Slot, BindingKind.ProjectAsset, asset.File,
            boundLabel: "image");
        Assert.Null(legacy.BoundLabel);
        Assert.Equal("asset-0123456789abcdef0123456789abcdef.dds", legacy.TextureName);
    }

    [Fact]
    public void A_stock_card_names_the_texture_the_game_draws_and_the_filter_finds_it()
    {
        var session = new AuthoredEditSession(TextureOnly());
        session.ChooseTargetGameValue("edit-long", "slot-ramp");
        var (vm, _) = On(session, s => s.GameTexture = slot =>
            slot.SlotId == "slot-ramp" ? "T_Toon_Ramp_Common" : null);

        var card = PartRow(vm).Children[0].MapGroups[0].Cards[0];
        Assert.Equal("", card.TextureName);              // the project names no file for the game's own
        Assert.True(card.ShowsGameTextureName);
        Assert.Equal("T_Toon_Ramp_Common", card.GameTextureName);

        vm.Filter = "toon_ramp_common";
        Assert.True(PartRow(vm).Children[0].IsVisible);
        Assert.False(PartRow(vm).Children[1].IsVisible);
    }

    [Fact]
    public void A_card_the_mod_owns_does_not_also_name_a_game_texture()
    {
        var (vm, _, _) = Page(TextureOnly(), s => s.GameTexture = _ => "T_Toon_Ramp_Common");
        Assert.False(PartRow(vm).Children[0].MapGroups[0].Cards[0].ShowsGameTextureName);
    }

    [Fact]
    public void A_replacement_groups_one_to_one_by_folded_material_position_and_hides_stock_cards()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.EnsurePartSlots(Body, part => Installed(part, 2, new[] { 3, 3 }));
        session.RecordReplacementOutputs("edit-long", 2);
        var (vm, _) = On(session, shell => shell.Resolve = part =>
            Installed(part, 2, new[] { 3, 3 }));

        var groups = PartRow(vm).Children[0].MapGroups;
        Assert.Equal(new[] { "mat0", "mat1" }, groups.Select(group => group.Title));
        Assert.All(groups, group =>
        {
            Assert.False(Assert.Single(group.Sets).HasLabel);
            Assert.All(group.Cards, card => Assert.Equal(TargetSlotDomain.EditOutput, card.Slot.Domain));
        });
        Assert.DoesNotContain(groups.SelectMany(group => group.Cards),
            card => card.Slot.Domain == TargetSlotDomain.Game);

        var own = groups[0].Cards.First(card => card.Slot.Input == TargetInputKind.BaseColor);
        Assert.False(own.IsGameSlot);
        Assert.True(own.HasOrigin);
        Assert.False(own.CanRevert);
        // a replacement's own card DOES offer the guide: it draws from the edit's mesh, so the islands
        // are the layout the paint actually lands on
        Assert.True(own.CanOpenUvGuide);
        Assert.Contains("Use Revert mesh", own.RevertHint);
    }

    [Fact]
    public void Fold_many_keeps_one_material_group_with_labelled_submesh_sets()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.RecordReplacementOutputs("edit-long", 2);
        var (vm, _) = On(session, shell => shell.Resolve = part =>
            Installed(part, 1, new[] { 3 }));

        var group = Assert.Single(PartRow(vm).Children[0].MapGroups);
        Assert.Equal("mat0", group.Title);
        Assert.Equal(new[] { "submesh 0", "submesh 1" }, group.Sets.Select(set => set.Label));
        Assert.All(group.Sets, set => Assert.True(set.HasLabel));
    }

    [Fact]
    public void A_replacement_keeps_its_card_groups_when_the_install_cannot_answer()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.RecordReplacementOutputs("edit-long", 2);
        var (vm, _) = On(session, shell => shell.Resolve = part => null);

        var groups = PartRow(vm).Children[0].MapGroups;
        Assert.Equal(new[] { "material 0", "material 1" }, groups.Select(group => group.Title));
        Assert.All(groups, group => Assert.All(group.Cards,
            card => Assert.Equal(TargetSlotDomain.EditOutput, card.Slot.Domain)));
    }

    [Fact]
    public void Replacement_positions_past_donor_coverage_and_zero_count_positions_have_no_group()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.RecordReplacementOutputs("edit-long", 3);
        var (vm, _) = On(session, shell => shell.Resolve = part =>
            Installed(part, 5, new[] { 3, 0, 3, 3, 3 }));

        var groups = PartRow(vm).Children[0].MapGroups;
        Assert.Equal(new[] { "mat0", "mat2", "mat4" }, groups.Select(group => group.Title));
        Assert.All(groups[1].Cards, card => Assert.Equal(2, session.Slots("edit-long")
            .Single(state => state.Slot.Id == card.Slot.SlotId).Slot.SubmeshIndex));
        Assert.All(groups[2].Cards, card => Assert.Equal(1, session.Slots("edit-long")
            .Single(state => state.Slot.Id == card.Slot.SlotId).Slot.SubmeshIndex));
        Assert.False(groups[1].Sets[0].HasLabel);
        Assert.False(groups[2].Sets[0].HasLabel);
    }

    [Fact]
    public void A_blanked_slot_says_what_stands_there_and_offers_no_editor()
    {
        // Only the two packed inputs have a neutral value to plug in, so the blanked card is a normal map.
        var project = AuthoredEditFixtures.WithOwnedSlots();
        project.TargetSlots.Single(s => s.Id == "slot-owned-2").Input = TargetInputKind.Normal;
        var bindings = project.EditDefinitions.Single(e => e.Id == "edit-long").Bindings;
        bindings[bindings.FindIndex(b => b.SlotId == "slot-owned-2")] =
            new Binding { SlotId = "slot-owned-2", Kind = BindingKind.Neutral };
        var (vm, _, _) = Page(project, shell => shell.Resolve = part => Installed(part));

        var card = Card(vm, "slot-owned-2");
        Assert.True(card.IsBlanked);
        Assert.False(card.CanOpen);
        Assert.Equal(EditMapCardVm.BlankedSlotNotEditable, card.OpenHint);
    }

    /// <summary>A game-domain ramp card's answers: any bound asset is a pick — the schema records a ramp's
    /// lineage, never who authored the record, so an asset with a game source and one without are one state
    /// by design — and the game's own value is untouched.</summary>
    [Fact]
    public void A_game_ramp_reads_as_the_answer_it_holds_not_as_whichever_binding_family_it_fell_in()
    {
        var session = new AuthoredEditSession(TextureOnly());
        var (vm, _) = On(session);
        var picked = PartRow(vm).Children[0].MapGroups[0].Cards[0];
        Assert.Equal(RampCardState.PickedCaption, picked.RampState.Caption);

        var sourcedAsset = new ProjectAsset
        {
            Id = "sourced-ramp", Kind = ProjectAssetKind.Ramp, Label = "Sourced",
            File = "textures/carried_ramp.dds", Source = new ProjectAssetSource
            {
                GameAsset = new GameAssetRef
                {
                    GameBuild = "26109", LogicalBundle = "characters/vesna", PathId = 91001, Name = "ramp",
                },
            },
        };
        var sourcedProject = session.Snapshot();
        sourcedProject.ProjectAssets.Add(sourcedAsset);
        var sourcedSession = new AuthoredEditSession(sourcedProject);
        sourcedSession.ChooseProjectAsset("edit-long", "slot-ramp", sourcedAsset.Id);
        var (sourcedVm, _) = On(sourcedSession);
        var sourced = PartRow(sourcedVm).Children[0].MapGroups[0].Cards[0];
        Assert.Equal(RampCardState.PickedCaption, sourced.RampState.Caption);

        session.ChooseTargetGameValue("edit-long", "slot-ramp");
        var (bareVm, _) = On(session);
        var bare = PartRow(bareVm).Children[0].MapGroups[0].Cards[0];
        Assert.Equal(RampCardState.VanillaCaption, bare.RampState.Caption);
        Assert.False(bare.RampState.HasRecord);
    }

    /// <summary>The recorded keep-the-game's on a replacement's own ramp slot. It is a state the modder
    /// chose, so the card states it rather than reading as a slot nobody has answered — and it carries no
    /// ownership marker, because what draws there is the original. The record shows in Revert, which takes
    /// it back to unanswered. The r6 shape: a source-slot answer naming the part's game-domain ramp, no edit
    /// named.</summary>
    [Fact]
    public void A_kept_ramp_on_a_replacement_reads_as_vanilla_and_reverts_as_a_record()
    {
        var project = ReplacementRampProject();
        var (vm, session, _) = Page(project, shell => shell.Resolve = part => Installed(part));
        var card = OwnRampCard(vm);
        Assert.Equal(RampCardState.VanillaCaption, card.RampState.Caption);
        Assert.False(card.RampState.HasRecord);
        Assert.False(card.CanRevertRamp);
        Assert.Equal("Nothing to revert yet", card.RevertRampHint);

        session.ChooseSourceSlot("edit-long", "slot-ramp-own", "slot-ramp");
        var (keptVm, _) = On(session, shell => shell.Resolve = part => Installed(part));
        var kept = OwnRampCard(keptVm);
        Assert.Equal(RampCardState.KeptCaption, kept.RampState.Caption);
        Assert.False(kept.RampState.IsOwned);
        Assert.False(kept.HasEditBadge);
        // The ramp state line is the answer; the origin line would only say it a second time.
        Assert.Null(kept.OriginNote);
        Assert.True(kept.RampState.HasRecord);
        Assert.True(kept.CanRevertRamp);
        Assert.Equal(EditMapCardVm.TakeBackRampChoice, kept.RevertRampHint);

        keptVm.RevertRampCommand.Execute(kept);
        Assert.Equal(BindingKind.InheritedLiveCarrier,
            session.Slots("edit-long").Single(s => s.Slot.Id == "slot-ramp-own").Binding.Kind);
        Assert.Contains("the original part's toon ramp", keptVm.Status);
    }

    /// <summary>Golden, with the replacement's own ramp slot beside the part's game one — the shape a
    /// replaced part has once its outputs are recorded — answered unanswered to start.</summary>
    private static AuthoredProject ReplacementRampProject()
    {
        var project = AuthoredEditFixtures.Golden();
        var game = project.TargetSlots.Single(s => s.Id == "slot-ramp");
        project.TargetSlots.Add(new TargetSlot
        {
            Id = "slot-ramp-own",
            Part = game.Part,
            SubmeshIndex = 0,
            Input = TargetInputKind.Ramp,
            Domain = TargetSlotDomain.EditOutput,
            OwnerEditId = "edit-long",
            Renderer = game.Renderer,
            Mesh = game.Mesh,
        });
        project.EditDefinitions.Single(e => e.Id == "edit-long").Bindings.Add(new Binding
        {
            SlotId = "slot-ramp-own", Kind = BindingKind.InheritedLiveCarrier,
        });
        return project;
    }

    private static EditMapCardVm OwnRampCard(EditPageVm vm) =>
        PartRow(vm).Children[0].MapGroups.SelectMany(g => g.Cards)
            .Single(c => c.IsRamp && !c.IsGameSlot);

    [Fact]
    public void Reverting_a_game_card_puts_it_back_to_the_games_own_value()
    {
        var (vm, session, _) = Page(TextureOnly());
        var card = PartRow(vm).Children[0].MapGroups[0].Cards[0];
        Assert.True(card.CanRevertRamp);

        vm.RevertRampCommand.Execute(card);

        var binding = session.Slots("edit-long").Single(s => s.Slot.Id == "slot-ramp").Binding;
        Assert.Equal(BindingKind.TargetGameValue, binding.Kind);
        // Only the acted-on edit moved: the other still names its own ramp.
        Assert.Equal(BindingKind.ProjectAsset,
            session.Slots("edit-short").Single(s => s.Slot.Id == "slot-ramp").Binding.Kind);
        Assert.False(PartRow(vm).Children[0].MapGroups[0].Cards[0].HasEditBadge);
    }

    [Fact]
    public async Task Picking_a_ramp_binds_only_the_card_it_was_picked_on()
    {
        var (vm, session, shell) = Page(
            WithAsset(TextureOnly(), "textures/gold.dds", ProjectAssetKind.Ramp),
            s => s.RampResult = new EditRampPick(new EditAssetResult("textures/gold.dds", "Gold ramp")));
        var card = PartRow(vm).Children[0].MapGroups[0].Cards[0];

        await vm.ChooseRampCommand.ExecuteAsync(card);

        Assert.Equal("slot-ramp", shell.LastRampSlot!.SlotId);
        Assert.Equal("edit-long", shell.LastRampSlot.Edit.EditDefinitionId);
        Assert.Equal("gold", PartRow(vm).Children[0].MapGroups[0].Cards[0].TextureName);
        Assert.Equal("Cool ramp", PartRow(vm).Children[1].MapGroups[0].Cards[0].TextureName);
        Assert.Equal(1, shell.ProjectChangedCalls);
    }

    [Fact]
    public async Task A_bind_result_without_a_friendly_label_falls_back_to_its_storage_basename()
    {
        var picked = new EditAssetResult("textures/gold.dds", "   ");
        var (vm, _, _) = Page(WithAsset(TextureOnly(), picked.ProjectRelativeFile,
            ProjectAssetKind.Ramp), s => s.RampResult = new EditRampPick(picked));
        var card = PartRow(vm).Children[0].MapGroups[0].Cards[0];

        await vm.ChooseRampCommand.ExecuteAsync(card);

        Assert.Equal($"gold.dds is now Long body's toon ramp on {card.Slot.MaterialName}.", vm.Status);
    }

    /// <summary>The picker's third outcome on a GAME slot: the modder chose the pinned row, so the slot asks
    /// the game for its own value — the state Revert leaves it in, reached deliberately this time.</summary>
    [Fact]
    public async Task Keeping_the_games_own_ramp_on_a_game_slot_binds_the_games_own_value()
    {
        var (vm, session, shell) = Page(TextureOnly(),
            s => s.RampResult = new EditRampPick(null));
        var card = PartRow(vm).Children[0].MapGroups[0].Cards[0];
        Assert.Equal(BindingKind.ProjectAsset,
            session.Slots("edit-long").Single(s => s.Slot.Id == "slot-ramp").Binding.Kind);

        await vm.ChooseRampCommand.ExecuteAsync(card);

        Assert.Equal(BindingKind.TargetGameValue,
            session.Slots("edit-long").Single(s => s.Slot.Id == "slot-ramp").Binding.Kind);
        Assert.Equal(EditPageVm.KeptGameOwnRamp, vm.Status);
        Assert.Equal(1, shell.ProjectChangedCalls);
        // The other edit's answer for the same route is untouched.
        Assert.Equal(BindingKind.ProjectAsset,
            session.Slots("edit-short").Single(s => s.Slot.Id == "slot-ramp").Binding.Kind);
    }

    /// <summary>The same outcome on a REPLACEMENT's own output slot, where "the game's own" is a different
    /// sentence: the slot names the game ramp slot it stands over, which is the recorded decision the
    /// projection reads as keep-the-carrier's-own.</summary>
    [Fact]
    public async Task Keeping_the_games_own_ramp_on_a_replacement_slot_names_the_game_ramp_slot()
    {
        var (vm, session, _) = Page(WithReplacementRamp(), s =>
        {
            s.Resolve = part => Installed(part);
            s.RampResult = new EditRampPick(null);
        });
        var card = PartRow(vm).Children[0].MapGroups.SelectMany(g => g.Cards)
            .Single(c => c.IsRamp && !c.IsGameSlot);

        await vm.ChooseRampCommand.ExecuteAsync(card);

        var binding = session.Slots(card.Slot.Edit.EditDefinitionId)
            .Single(s => s.Slot.Id == card.Slot.SlotId).Binding;
        Assert.Equal(BindingKind.SourceSlot, binding.Kind);
        Assert.Null(binding.SourceSlot!.EditDefinitionId);
        var named = session.Snapshot().TargetSlots.Single(s => s.Id == binding.SourceSlot.SlotId);
        Assert.Equal(TargetSlotDomain.Game, named.Domain);
        Assert.Equal(TargetInputKind.Ramp, named.Input);
        Assert.Equal(EditPageVm.KeptGameOwnRamp, vm.Status);
        // The card now reads as the recorded opt-out rather than as a slot nobody answered.
        var after = PartRow(vm).Children[0].MapGroups.SelectMany(g => g.Cards)
            .Single(c => c.IsRamp && !c.IsGameSlot);
        Assert.True(after.RampState.HasRecord);
    }

    /// <summary>The golden project with a replacement's OWN ramp output beside the game ramp slot it stands
    /// over: unanswered, which is the state the pinned row's answer is told apart from.</summary>
    private static AuthoredProject WithReplacementRamp()
    {
        var project = AuthoredEditFixtures.Golden();
        var game = project.TargetSlots.Single(s => s.Id == "slot-ramp");
        project.TargetSlots.Add(new TargetSlot
        {
            Id = "slot-own-ramp",
            OwnerEditId = "edit-long",
            Part = AuthoredEditFixtures.Body,
            SubmeshIndex = 0,
            MaterialSlotIndex = 0,
            Input = TargetInputKind.Ramp,
            Domain = TargetSlotDomain.EditOutput,
            Renderer = game.Renderer,
        });
        project.EditDefinitions.Single(e => e.Id == "edit-long").Bindings.Add(new Binding
        {
            SlotId = "slot-own-ramp", Kind = BindingKind.InheritedLiveCarrier,
        });
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }

    [Fact]
    public async Task Open_picture_and_cancel_authors_nothing_then_a_return_binds_the_exact_slot()
    {
        var (vm, session, shell) = Page(WithAsset(AuthoredEditFixtures.WithOwnedSlots(),
            "textures/repainted.png", ProjectAssetKind.Picture),
            s => s.Resolve = part => Installed(part));
        var card = Card(vm, "slot-owned");
        Assert.True(card.CanOpen);
        long beforeCancel = session.Revision;

        // The editor was opened and closed with no save: the binding is exactly where it was.
        await vm.OpenCardCommand.ExecuteAsync(card);
        Assert.Equal("slot-owned", shell.LastOpenSlot!.SlotId);
        Assert.Equal("Skin", Card(vm, "slot-owned").TextureName);
        Assert.Equal(0, shell.ProjectChangedCalls);
        Assert.Equal(beforeCancel, session.Revision);

        shell.PictureResult = new EditAssetResult("textures/repainted.png", "Repainted");
        await vm.OpenCardCommand.ExecuteAsync(Card(vm, "slot-owned"));
        Assert.Equal("repainted", Card(vm, "slot-owned").TextureName);
        Assert.Equal(BindingKind.ProjectAsset,
            session.Slots("edit-long").Single(s => s.Slot.Id == "slot-owned").Binding.Kind);
    }

    // ---- drops ----

    [Fact]
    public async Task A_drop_that_is_not_one_png_is_refused_with_no_shell_call()
    {
        var (vm, _, shell) = Page(AuthoredEditFixtures.WithOwnedSlots(),
            s => s.Resolve = part => Installed(part));
        var card = Card(vm, "slot-owned");

        await vm.HandleDropAsync(new[] { "a.glb" }, card);
        Assert.Contains(".png", vm.Status);

        await vm.HandleDropAsync(new[] { "a.png", "b.png" }, card);
        Assert.Contains("one .png", vm.Status);

        vm.SelectedNode = PartRow(vm).Children[0];
        await vm.HandleDropAsync(new[] { "a.png" }, null);
        // Cards are on screen, so the card is what the line names.
        Assert.Equal(EditPageVm.NoDropTarget, vm.Status);
        Assert.Contains("map card", vm.Status);

        Assert.Null(shell.LastDropSlot);
    }

    /// <summary>An off-card drop says where a picture CAN go, and that answer has to be what is on screen
    /// rather than a map card that may not be. A row showing cards names the card; a row showing none names
    /// the row that would show them; a row that already says why it has none says nothing more.</summary>
    [Fact]
    public async Task An_off_card_drop_names_a_target_that_is_actually_on_screen()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden(), s => s.Resolve = part => Installed(part));

        vm.SelectedNode = null;
        await vm.HandleDropAsync(new[] { "a.png" }, null);
        Assert.Equal(EditPageVm.SelectAPart, vm.Status);

        vm.SelectedNode = Subject(vm);
        await vm.HandleDropAsync(new[] { "a.png" }, null);
        Assert.Equal(EditPageVm.SelectAPart, vm.Status);

        // A part with edits shows its overview, not cards: the edit under it is where the maps are.
        vm.SelectedNode = PartRow(vm);
        Assert.False(PartRow(vm).HasMapGroups);
        await vm.HandleDropAsync(new[] { "a.png" }, null);
        Assert.Equal(EditPageVm.SelectAnEdit, vm.Status);

        // A hide edit has no maps by construction and its own panel says so; a sentence here would be the
        // third place one fact is stated, on the surface that holds it for the shortest time.
        var (hideVm, _) = On(new AuthoredEditSession(AuthoredEditFixtures.Golden()));
        hideVm.HidePartCommand.Execute(PartRow(hideVm));
        hideVm.Status = "";
        await hideVm.HandleDropAsync(new[] { "a.png" }, null);
        Assert.Equal("", hideVm.Status);
    }

    [Fact]
    public async Task A_ramp_card_takes_the_drag_so_it_can_refuse_the_drop_in_words()
    {
        var (vm, _, shell) = Page(TextureOnly());
        var ramp = PartRow(vm).Children[0].MapGroups[0].Cards[0];
        Assert.True(ramp.IsRamp);

        // Refusing at the cursor delivers no release, so the card never gets to say why.
        Assert.True(vm.CanAcceptDrop(ramp));

        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, ramp);

        Assert.Contains(EditMapCardVm.RampNotAnImage, vm.Status);
        // The slot's kind is asked BEFORE anything is decoded or published.
        Assert.Null(shell.LastDropSlot);
    }

    [Fact]
    public async Task A_drop_on_a_card_binds_that_slot_and_nothing_else()
    {
        var (vm, session, shell) = Page(WithAsset(AuthoredEditFixtures.WithOwnedSlots(),
            "textures/dropped.png", ProjectAssetKind.Picture),
            s =>
            {
                s.Resolve = part => Installed(part);
                s.DropResult = new EditAssetResult("textures/dropped.png", "Dropped");
            });
        var card = Card(vm, "slot-owned");
        Assert.True(vm.CanAcceptDrop(card));

        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, card);

        Assert.Equal("slot-owned", shell.LastDropSlot!.SlotId);
        Assert.Equal(BindingKind.ProjectAsset,
            session.Slots("edit-long").Single(s => s.Slot.Id == "slot-owned").Binding.Kind);
        Assert.Equal("dropped", Card(vm, "slot-owned").TextureName);
        // The sibling submesh still takes its value from where it did.
        Assert.Equal(BindingKind.SourceSlot,
            session.Slots("edit-long").Single(s => s.Slot.Id == "slot-owned-2").Binding.Kind);
    }

    [Fact]
    public async Task Two_drops_on_one_card_cannot_run_at_once()
    {
        var gate = new TaskCompletionSource<EditAssetResult?>();
        var slow = new SlowShell(gate);
        var page = new EditPageVm(slow);
        page.Load(new AuthoredEditSession(AuthoredEditFixtures.WithOwnedSlots()));
        var slowCard = Card(page, "slot-owned");

        var first = page.DropOnCardAsync(slowCard, @"C:\in\a.png");
        Assert.False(page.CanAcceptDrop(slowCard));
        await page.DropOnCardAsync(slowCard, @"C:\in\b.png");
        Assert.Equal(Remold.App.ViewModels.BlenderGate.Busy, page.Status);

        gate.SetResult(null);
        await first;
        Assert.Equal(1, slow.Drops);
    }

    private sealed class SlowShell : FakeShellBase
    {
        private readonly TaskCompletionSource<EditAssetResult?> _gate;
        internal int Drops;
        internal SlowShell(TaskCompletionSource<EditAssetResult?> gate) => _gate = gate;
        public override LegacyResolvedPart? ResolvePart(TargetPart target) => Installed(target);
        public override Task<EditAssetResult?> AcceptDroppedPictureAsync(EditSlotRef slot, string path,
            IProgress<string> status, bool confirmed = false, EditTextureSharingOffer? offered = null)
        {
            Drops++;
            return _gate.Task;
        }
    }

    // ---- the subject's own verbs ----

    /// <summary>Each non-removal subject verb reaches the shell naming the subject its row stands for.</summary>
    [Theory]
    [InlineData("open-all")]
    [InlineData("open-all-first-edit")]
    [InlineData("show-folder")]
    public async Task A_subject_verb_reaches_the_shell_for_its_own_subject(string verb)
    {
        var (vm, session, shell) = Page(Bare());
        string before = AuthoredProjectSerializer.Serialize(session.Snapshot());
        var subject = Subject(vm);
        Assert.Equal("Each part opens from its active or first edit; parts without edits open from stock.",
            subject.OpenAllFirstEditHint);

        switch (verb)
        {
            case "open-all": await vm.OpenSubjectInBlenderCommand.ExecuteAsync(subject); break;
            case "open-all-first-edit":
                await vm.OpenSubjectFirstEditInBlenderCommand.ExecuteAsync(subject);
                break;
            default: vm.ShowSubjectFolderCommand.Execute(subject); break;
        }

        Assert.Equal(new[] { $"{verb} {subject.Subject}/{subject.Outfit}" }, shell.SubjectVerbs);
        Assert.Equal(before, AuthoredProjectSerializer.Serialize(session.Snapshot()));
        Assert.Equal(0, shell.ProjectChangedCalls);
    }

    /// <summary>Removing the subject is the subject verb that changes the mod, so it is also the one that
    /// tells the shell the project moved and redraws. What it removed is the shell's own doing — the page has
    /// no inventory to write.</summary>
    [Fact]
    public async Task Removing_a_subject_announces_the_change_and_redraws()
    {
        var (vm, _, shell) = Page(Bare());
        var subject = Subject(vm);

        await vm.RemoveSubjectCommand.ExecuteAsync(subject);

        Assert.Equal(new[] { $"remove {subject.Subject}/{subject.Outfit}" }, shell.SubjectVerbs);
        Assert.Equal(1, shell.ProjectChangedCalls);
    }

    /// <summary>A running subject verb holds the whole branch: the part rows under it report busy, and a part
    /// verb refuses rather than running alongside a verb that is acting on that very part.</summary>
    [Fact]
    public async Task A_running_subject_verb_gates_the_parts_under_it()
    {
        var (vm, session, shell) = Page(Bare(), s => s.SubjectHold = new TaskCompletionSource());
        var subject = Subject(vm);

        var running = vm.OpenSubjectInBlenderCommand.ExecuteAsync(subject);

        Assert.True(subject.IsBusy);
        Assert.True(PartRow(vm).IsBusy);
        vm.NewEditCommand.Execute(PartRow(vm));
        Assert.Equal(BlenderGate.Busy, vm.Status);
        Assert.Empty(EditsFor(session));

        shell.SubjectHold!.SetResult();
        await running;
        Assert.False(Subject(vm).IsBusy);
        Assert.False(PartRow(vm).IsBusy);
    }

    /// <summary>A subject verb's refusal is the shell's own sentence, said on the page's status line — the
    /// same rule a part verb's refusal follows.</summary>
    [Fact]
    public async Task A_refused_subject_verb_says_the_refusals_own_words()
    {
        var vm = new EditPageVm(new ThrowingSubjectShell());
        vm.Load(new AuthoredEditSession(Bare()));

        await vm.OpenSubjectInBlenderCommand.ExecuteAsync(Subject(vm));

        Assert.Equal(ThrowingSubjectShell.Refusal, vm.Status);
    }

    /// <summary>A failure with no wording of its own says what the verb could not do, and nothing of what
    /// the failure itself said — those messages name file handles, COM results and the model's own ids, and
    /// none of them means anything to the person reading the line.</summary>
    [Fact]
    public async Task A_subject_verb_that_breaks_names_the_action_rather_than_the_break()
    {
        var vm = new EditPageVm(new BreakingSubjectShell());
        vm.Load(new AuthoredEditSession(Bare()));

        await vm.OpenSubjectInBlenderCommand.ExecuteAsync(Subject(vm));

        Assert.Equal("Couldn't open this item's parts in Blender.", vm.Status);
        Assert.DoesNotContain(BreakingSubjectShell.Internals, vm.Status);
    }

    private sealed class ThrowingSubjectShell : FakeShellBase
    {
        internal const string Refusal = "Game files unavailable.";

        public override Task OpenSubjectInBlenderAsync(string subject, string outfit,
            IProgress<string> status) => throw new AuthoredRefusalException(Refusal);
    }

    private sealed class BreakingSubjectShell : FakeShellBase
    {
        internal const string Internals = "edit-3f2a: 0x80070020";

        public override Task OpenSubjectInBlenderAsync(string subject, string outfit,
            IProgress<string> status) => throw new InvalidOperationException(Internals);
    }

    /// <summary>A shell that answers nothing, for the tests that only need one call overridden.</summary>
    private class FakeShellBase : IEditPageShell
    {
        public virtual LegacyResolvedPart? ResolvePart(TargetPart target) => null;
        public virtual Task<LegacyResolvedPart?> ResolvePartAsync(TargetPart target) =>
            Task.FromResult(ResolvePart(target));
        public virtual IReadOnlyList<TargetPart> SubjectParts(string subject, string outfit) =>
            Array.Empty<TargetPart>();
        public virtual EditSkeletonOutline? ReadSkeleton(string subject, string outfit) => null;
        public virtual EditInstallState InstallState() => new();
        public virtual string PartToken(TargetPart part) => "";
        public virtual string? GameTextureName(EditSlotRef slot) => null;
        public virtual int? TextureUses(EditSlotRef slot) => 1;
        public virtual EditSubjectRead SubjectRead(TargetPart part) => EditSubjectRead.Answered;
        public virtual Task<EditMapPreview?> LoadMapPreviewAsync(EditSlotRef slot) =>
            Task.FromResult<EditMapPreview?>(null);
        public virtual Task<EditMeshPreview?> LoadEditMeshPreviewAsync(EditRef edit) =>
            Task.FromResult<EditMeshPreview?>(null);
        public virtual Task<EditMeshPreview?> LoadPartMeshPreviewAsync(TargetPart part) =>
            Task.FromResult<EditMeshPreview?>(null);
        public virtual Task<string?> MeshEditBlockAsync(TargetPart part) => Task.FromResult<string?>(null);
        public virtual Task OpenPartInBlenderAsync(TargetPart part, bool withReferences,
            IProgress<string> status) => Task.CompletedTask;
        public virtual Task OpenInBlenderAsync(EditRef edit, bool withReferences, IProgress<string> status) =>
            Task.CompletedTask;
        public virtual Task<EditPictureOpenResult> OpenPictureAsync(EditSlotRef slot,
            IProgress<string> status, bool confirmed = false, EditTextureSharingOffer? offered = null) =>
            Task.FromResult(EditPictureOpenResult.NotLaunched);
        public virtual Task OpenUvGuideAsync(EditSlotRef slot, IProgress<string> status) => Task.CompletedTask;
        public virtual Task<string?> PickPictureAsync() => Task.FromResult<string?>(null);
        public virtual Task<EditRampPick?> PickRampAsync(EditSlotRef slot) =>
            Task.FromResult<EditRampPick?>(null);
        public virtual Task<EditShadingValuesResult?> EditShadingValuesAsync(EditRef edit,
            int materialSlotIndex, string materialLabel,
            IReadOnlyDictionary<string, string> authored, bool addsFirstEdit) =>
            Task.FromResult<EditShadingValuesResult?>(null);
        public virtual Task<EditShadingSource?> PickShadingSourceAsync(TargetPart part,
            int materialSlotIndex, string materialLabel, GameAssetRef? targetMaterial,
            IReadOnlyList<(string Subject, string Outfit)> subjects, IProgress<string> status) =>
            Task.FromResult<EditShadingSource?>(null);
        public virtual Task<EditAssetResult?> AcceptDroppedPictureAsync(EditSlotRef slot, string path,
            IProgress<string> status, bool confirmed = false, EditTextureSharingOffer? offered = null) =>
            Task.FromResult<EditAssetResult?>(null);
        public virtual Task<bool> ConfirmAsync(string title, string body, string confirmLabel,
            bool dangerous = false) => Task.FromResult(true);
        public virtual Task CopyTextAsync(string? text) => Task.CompletedTask;
        public virtual string SubjectLabel(string subject, string outfit) => subject;
        public virtual Task OpenSubjectInBlenderAsync(string subject, string outfit,
            IProgress<string> status) => Task.CompletedTask;
        public virtual Task OpenSubjectFirstEditInBlenderAsync(string subject, string outfit,
            IProgress<string> status) => Task.CompletedTask;
        public virtual void ShowSubjectFolder(string subject, string outfit) { }
        public virtual Task RemoveSubjectAsync(string subject, string outfit) => Task.CompletedTask;
        public virtual void GoToBuild(EditRef? edit) { }
        public virtual void ProjectChanged(long revision) { }
    }

    // ---- a replacement whose outputs were never recorded ----

    /// <summary>A released Replace that recorded no donor textures — a removal-style mesh edit is exactly
    /// that shape — adapts to a content edit with a replacement mesh and no output rows of its own. It draws
    /// the part's own material positions rather than nothing at all: without the fallback the inspector has
    /// no cards, no material groups and no shading rows on an edit the mod ships.</summary>
    [Fact]
    public void An_adapted_geometry_only_replacement_draws_the_parts_own_material_positions()
    {
        var legacy = new ModProject();
        legacy.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" });
        legacy.Targets.Add(new ProjectTarget
        {
            AssetType = "Mesh",
            Bundle = "characters/vesna_ssr01",
            ObjectName = Body.RendererSlot,
            PathId = 72001,
            SubjectCharacter = "Vesna",
            SubjectOutfit = "VesnaSSR01",
            ReplaceFile = "meshes/body.glb",
            OriginalFile = "originals/body.glb",
            Edited = true,
        });

        var adapted = LegacyProjectAdapter.Adapt(legacy, part => Installed(part));

        Assert.Empty(AuthoredProjectValidator.Errors(adapted.Project));
        Assert.DoesNotContain(adapted.Project.TargetSlots,
            slot => slot.Domain == TargetSlotDomain.EditOutput);

        var (vm, _, shell) = Page(adapted.Project, s => s.Resolve = part => Installed(part));

        var edit = Assert.Single(PartRow(vm).Children);
        Assert.True(edit.HasMeshEdit);
        var group = Assert.Single(edit.MapGroups);
        Assert.Equal("mat0", group.Title);
        Assert.True(group.HasShading);
        Assert.Equal(new[] { TargetInputKind.BaseColor, TargetInputKind.Ramp },
            group.Cards.Select(card => card.Slot.Input));
        Assert.All(group.Cards, card => Assert.Equal(TargetSlotDomain.Game, card.Slot.Domain));
        // Enumeration is already in the project, but carrier proof is live-install evidence and is refreshed
        // asynchronously for a replacement even when its cards are stand-ins.
        Assert.Equal(0, shell.SyncResolveCalls);
        Assert.Equal(1, shell.AsyncResolveCalls);
    }

    // ---- the install read comes off the UI thread ----

    /// <summary>A replacement's fold needs the install's drawable pattern, and reading it deobfuscates the
    /// part's bundles. A redraw must never do that where it runs: ③ ticks a placement or a rename lands and
    /// the window would stop for seconds per replaced part. The cards fold when the read comes back.</summary>
    [Fact]
    public async Task A_replacements_fold_reads_the_install_off_thread_and_folds_when_it_lands()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.RecordReplacementOutputs("edit-long", 6);
        var hold = new TaskCompletionSource<LegacyResolvedPart?>();
        var (vm, shell) = On(session, s =>
        {
            s.Resolve = part => Installed(part, 2, new[] { 3, 3 });
            s.ResolveHold = hold;
        });

        // Unfolded: six submeshes standing at six positions of their own, and no synchronous read.
        var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        Assert.Equal(6, edit.MapGroups.Count);
        Assert.Equal(0, shell.SyncResolveCalls);
        Assert.Equal(1, shell.AsyncResolveCalls);

        shell.ResolveHold = null;
        hold.SetResult(Installed(Body, 2, new[] { 3, 3 }));
        for (int i = 0; i < 200 && PartRow(vm).Children
                 .Single(node => node.EditDefinitionId == "edit-long").MapGroups.Count != 2; i++)
            await Task.Delay(5);

        var folded = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        Assert.Equal(2, folded.MapGroups.Count);
        Assert.Equal(new[] { "mat0", "mat1" }, folded.MapGroups.Select(group => group.Title));
        Assert.Equal(0, shell.SyncResolveCalls);
        // Settled once per part: a later redraw asks the memo, not the install.
        vm.Rebuild();
        Assert.Equal(1, shell.AsyncResolveCalls);
    }

    /// <summary>A force rescan replaces the install the answers were read off, so the page forgets them and
    /// the next redraw asks again.</summary>
    [Fact]
    public async Task Forgetting_the_install_reads_makes_the_next_redraw_ask_again()
    {
        var (vm, _, shell) = Page(Bare(), s => s.Resolve = part => Installed(part));
        for (int i = 0; i < 200 && PartRow(vm).MapGroups.Count == 0; i++) await Task.Delay(5);
        Assert.Equal(1, shell.AsyncResolveCalls);

        vm.ForgetInstallReads();
        vm.Rebuild();

        Assert.Equal(2, shell.AsyncResolveCalls);
        Assert.Equal(0, shell.SyncResolveCalls);
    }

    // ---- the bare part's inspector: the part's own original maps ----

    /// <summary>The install's material positions become the bare part's card groups, at the same grain and
    /// in the same order an edit's cards use. Their own controls, including the ordinary shading row,
    /// start the first edit.</summary>
    [Fact]
    public void A_bare_part_shows_first_edit_controls_on_its_original_map_cards()
    {
        var (vm, _, _) = Page(Bare(), s => s.Resolve = part => Installed(part, materials: 2));

        var groups = PartRow(vm).MapGroups;

        Assert.Equal(2, groups.Count);
        Assert.Equal(new[] { "mat0", "mat1" }, groups.Select(group => group.Title));
        Assert.All(groups, group =>
        {
            Assert.True(group.HasShading);
            Assert.True(group.Shading!.IsFirstEdit);
            Assert.False(group.Shading.IsEdited);
        });
        var cards = groups.SelectMany(group => group.Cards).ToList();
        Assert.Equal(4, cards.Count);
        Assert.All(cards, card => Assert.True(card.IsOriginal));
        Assert.All(cards.Where(card => card.IsRamp), card => Assert.True(card.ShowsRampActions));
        Assert.All(cards, card => Assert.False(card.HasEditBadge));
        // Open and Choose start the first edit. Revert still needs something authored, and the UV guide is a
        // read of the game's own layout before the modder decides to paint.
        var paintable = cards.Where(card => !card.IsRamp).ToList();
        Assert.All(paintable, card => Assert.True(card.ShowsMapActions));
        Assert.All(paintable, card => Assert.True(card.CanOpen));
        Assert.All(paintable, card => Assert.Equal("Open", card.OpenButtonLabel));
        Assert.All(paintable, card => Assert.Equal(EditMapCardVm.FirstEditOpenHint, card.OpenHint));
        Assert.All(paintable, card => Assert.False(card.CanRevert));
        Assert.All(paintable, card => Assert.True(card.CanOpenUvGuide));
        // A button that could only ever refuse is not drawn at all: this part has no edit for Revert to take
        // back. Browse stands where it would have been, and starts the same first edit a drop does.
        Assert.All(cards, card => Assert.False(card.ShowsRevert));
        Assert.All(paintable, card => Assert.True(card.CanBrowse));
        Assert.All(paintable, card =>
            Assert.Equal("Add an edit and replace this map with a .png", card.BrowseHint));
        Assert.All(cards.Where(card => card.IsRamp), card =>
        {
            Assert.False(card.ShowsMapActions);
            Assert.True(card.CanChooseRamp);
            Assert.Equal("Choose…", card.ChooseRampButtonLabel);
            Assert.Equal("Choose the toon ramp for this material; applying adds an edit", card.ChooseRampHint);
        });
        Assert.True(PartRow(vm).ShowsOriginalMaps);
        Assert.Equal("Original maps", PartRow(vm).OriginalMapsLabel);
        Assert.Null(PartRow(vm).OriginalsNote);
        // The base colour and the toon ramp keep an edit's own card order inside their material.
        Assert.Equal(new[] { TargetInputKind.BaseColor, TargetInputKind.Ramp },
            groups[0].Cards.Select(card => card.Slot.Input));
    }

    [Fact]
    public async Task Bare_Open_launches_for_the_structural_slot_without_minting()
    {
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part, materials: 2);
            s.TextureUseCount = _ => 2;
        });
        var card = PartRow(vm).MapGroups[1].Cards.Single(candidate =>
            candidate.Slot.Input == TargetInputKind.BaseColor);
        shell.CurrentSelection = () => vm.SelectedNode;

        await vm.OpenCardCommand.ExecuteAsync(card);

        Assert.Empty(EditsFor(session));
        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.EditsAtOpen);
        Assert.Null(shell.SelectedEditAtOpen);
        Assert.False(shell.LastOpenConfirmed);
        Assert.Null(shell.LastOpenOffered);
        Assert.Equal(TargetInputKind.BaseColor, shell.LastOpenSlot!.Input);
        Assert.Equal(1, shell.LastOpenSlot.MaterialSlotIndex);
        Assert.Empty(shell.LastOpenSlot.Edit.EditDefinitionId);
        Assert.Empty(shell.LastOpenSlot.SlotId);
    }

    [Fact]
    public async Task Bare_Open_has_no_plain_decline_gate()
    {
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.ConfirmResult = false;
        });
        var card = PartRow(vm).MapGroups[0].Cards.Single(candidate =>
            candidate.Slot.Input == TargetInputKind.BaseColor);
        vm.SelectedNode = PartRow(vm);

        await vm.OpenCardCommand.ExecuteAsync(card);

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Empty(EditsFor(session));
        Assert.NotNull(shell.LastOpenSlot);
        Assert.False(shell.LastOpenConfirmed);
    }

    [Fact]
    public async Task A_bare_Open_that_fails_before_launch_discards_its_fresh_mint()
    {
        var (vm, session, shell) = Page(BareWithSelection(), s =>
        {
            s.Resolve = part => Installed(part);
            s.Parts = (_, _) => new[] { Body };
            s.PictureLaunched = false;
        });
        var card = PartRow(vm).MapGroups[0].Cards.Single(candidate =>
            candidate.Slot.Input == TargetInputKind.BaseColor);
        vm.SelectedNode = PartRow(vm);

        await vm.OpenCardCommand.ExecuteAsync(card);

        Assert.NotNull(shell.LastOpenSlot);
        Assert.Empty(EditsFor(session));
        Assert.True(vm.SelectedNode!.IsPart);
    }

    [Fact]
    public async Task Bare_Open_holds_its_gate_without_minting_through_editor_dispatch()
    {
        var hold = new TaskCompletionSource();
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.OpenHold = hold;
        });
        var card = PartRow(vm).MapGroups[0].Cards.Single(candidate =>
            candidate.Slot.Input == TargetInputKind.BaseColor);

        var running = vm.OpenCardCommand.ExecuteAsync(card);

        Assert.Empty(EditsFor(session));
        Assert.True(card.IsBusy);

        hold.SetResult();
        await running;
        Assert.Empty(EditsFor(session));
    }

    [Fact]
    public async Task Bare_Choose_passes_the_structural_slot_and_does_not_mint_in_the_page()
    {
        var picked = new EditAssetResult("textures/gold.dds", "Gold ramp");
        var (vm, session, shell) = Page(WithAsset(Bare(), picked.ProjectRelativeFile,
            ProjectAssetKind.Ramp), s =>
        {
            s.Resolve = part => Installed(part, materials: 2);
            s.RampResult = new EditRampPick(picked);
        });
        var card = PartRow(vm).MapGroups[1].Cards.Single(candidate => candidate.IsRamp);
        shell.CurrentSelection = () => vm.SelectedNode;

        await vm.ChooseRampCommand.ExecuteAsync(card);

        Assert.Empty(EditsFor(session));
        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.EditsAtRamp);
        Assert.Null(shell.SelectedEditAtRamp);
        Assert.Equal(TargetInputKind.Ramp, shell.LastRampSlot!.Input);
        Assert.Equal(1, shell.LastRampSlot.MaterialSlotIndex);
        Assert.Empty(shell.LastRampSlot.Edit.EditDefinitionId);
        Assert.Empty(shell.LastRampSlot.SlotId);
    }

    [Fact]
    public async Task Bare_Choose_uses_the_shared_material_fallback_in_its_result_line()
    {
        var picked = new EditAssetResult("textures/gold.dds", "Gold ramp");
        var (vm, session, shell) = Page(WithAsset(Bare(), picked.ProjectRelativeFile, ProjectAssetKind.Ramp), s =>
        {
            s.Resolve = part =>
            {
                var resolved = Installed(part);
                resolved.Materials[0].Material.Name = "";
                return resolved;
            };
            s.RampResult = new EditRampPick(picked);
        });
        var card = PartRow(vm).MapGroups[0].Cards.Single(candidate => candidate.IsRamp);

        await vm.ChooseRampCommand.ExecuteAsync(card);

        Assert.Empty(EditsFor(session));
        Assert.Equal(0, shell.ConfirmCalls);
    }

    [Fact]
    public async Task Canceling_bare_Choose_discards_its_fresh_mint()
    {
        var (vm, session, shell) = Page(BareWithSelection(), s =>
        {
            s.Resolve = part => Installed(part);
            s.Parts = (_, _) => new[] { Body };
        });
        var card = PartRow(vm).MapGroups[0].Cards.Single(candidate => candidate.IsRamp);
        vm.SelectedNode = PartRow(vm);
        shell.CurrentSelection = () => vm.SelectedNode;

        await vm.ChooseRampCommand.ExecuteAsync(card);

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.EditsAtRamp);
        Assert.Null(shell.SelectedEditAtRamp);
        Assert.NotNull(shell.LastRampSlot);
        Assert.Empty(EditsFor(session));
        Assert.True(vm.SelectedNode!.IsPart);
    }

    [Fact]
    public async Task Keeping_the_original_from_bare_Choose_discards_its_fresh_mint_silently()
    {
        var (vm, session, shell) = Page(BareWithSelection(), s =>
        {
            s.Resolve = part => Installed(part);
            s.Parts = (_, _) => new[] { Body };
            s.RampResult = new EditRampPick(null);
        });
        var card = PartRow(vm).MapGroups[0].Cards.Single(candidate => candidate.IsRamp);
        vm.SelectedNode = PartRow(vm);
        vm.Status = "Ready.";
        shell.CurrentSelection = () => vm.SelectedNode;

        await vm.ChooseRampCommand.ExecuteAsync(card);

        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(0, shell.EditsAtRamp);
        Assert.Null(shell.SelectedEditAtRamp);
        Assert.Empty(EditsFor(session));
        Assert.True(vm.SelectedNode!.IsPart);
        Assert.Equal("Ready.", vm.Status);
    }

    [Fact]
    public async Task A_failed_first_action_needs_no_cleanup_and_leaves_no_edit()
    {
        var (vm, session, _) = Page(BareWithSelection(), s =>
        {
            s.Resolve = part => Installed(part);
            s.Parts = (_, _) => new[] { Body };
            s.RampFailure = new AuthoredRefusalException("The toon ramp list couldn't be read.");
        });
        var card = PartRow(vm).MapGroups[0].Cards.Single(candidate => candidate.IsRamp);

        await vm.ChooseRampCommand.ExecuteAsync(card);

        Assert.Equal("The toon ramp list couldn't be read.", vm.Status);
        Assert.Empty(EditsFor(session));
    }

    /// <summary>The originals are the install's answer, so a part it has not answered for draws none rather
    /// than an empty frame — and the redraw the answer lands on is what fills them in.</summary>
    [Fact]
    public async Task A_bare_parts_cards_arrive_when_the_off_thread_install_read_lands()
    {
        var hold = new TaskCompletionSource<LegacyResolvedPart?>();
        var (vm, _, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.ResolveHold = hold;
        });

        Assert.Empty(PartRow(vm).MapGroups);
        Assert.False(PartRow(vm).ShowsOriginalMaps);
        Assert.Equal(0, shell.SyncResolveCalls);

        shell.ResolveHold = null;
        hold.SetResult(Installed(AuthoredEditFixtures.Body));
        for (int i = 0; i < 200 && PartRow(vm).MapGroups.Count == 0; i++) await Task.Delay(5);

        Assert.Single(PartRow(vm).MapGroups);
        Assert.Equal(0, shell.SyncResolveCalls);
    }

    /// <summary>Each original card is where the part's first edit starts, bound to exactly the map the
    /// picture landed on — and the selection follows the new edit, so the result is seen where it went.</summary>
    [Fact]
    public async Task A_drop_on_an_original_card_hands_the_exact_bare_map_to_the_shell()
    {
        var (vm, session, shell) = Page(WithAsset(Bare(), "textures/dropped.png", ProjectAssetKind.Picture),
            s =>
            {
                s.Resolve = part => Installed(part, materials: 2);
                s.DropResult = new EditAssetResult("textures/dropped.png", "Dropped");
            });
        var second = PartRow(vm).MapGroups[1].Cards
            .Single(card => card.Slot.Input == TargetInputKind.BaseColor);
        Assert.True(vm.CanAcceptDrop(second));

        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, second);

        Assert.Empty(EditsFor(session));
        Assert.Equal(TargetInputKind.BaseColor, shell.LastDropSlot!.Input);
        Assert.Equal(1, shell.LastDropSlot.MaterialSlotIndex);
        Assert.Empty(shell.LastDropSlot.Edit.EditDefinitionId);
        Assert.Empty(shell.LastDropSlot.SlotId);
    }

    /// <summary>A toon ramp on a bare part shows no drop target, but takes the drag so the release can point
    /// back to the Choose control on that same card and mint nothing.</summary>
    [Fact]
    public async Task An_original_ramp_card_takes_the_drag_and_refuses_by_naming_Choose()
    {
        var (vm, session, shell) = Page(Bare(), s => s.Resolve = part => Installed(part));
        var ramp = PartRow(vm).MapGroups[0].Cards.Single(card => card.IsRamp);

        Assert.False(ramp.ShowsDropTarget);
        Assert.True(vm.CanAcceptDrop(ramp));
        Assert.Null(ramp.DropHint);

        await vm.HandleDropAsync(new[] { @"C:\in\ramp.dds" }, ramp);

        Assert.Equal(EditMapCardVm.RampNotAnImage, vm.Status);
        Assert.Contains("Choose", vm.Status);
        Assert.Empty(EditsFor(session));
        Assert.Null(shell.LastDropSlot);
    }

    /// <summary>A part whose game mesh cannot be edited in Blender keeps every card live — a picture edit is
    /// legal on a part whose shape cannot be replaced — and says why the two opens are off in the same amber
    /// row the install's own refusal uses. On a bare part those opens are the whole action row, so a reason
    /// that lived only on their hover was invisible until the pointer happened to rest on one.</summary>
    [Fact]
    public async Task A_gate_blocked_bare_part_shows_the_same_cards_and_no_instruction_it_refuses()
    {
        const string refusal = "This mesh uses expressions and cannot be edited in Blender.";
        var (vm, _, _) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.MeshEditBlock = _ => refusal;
        });
        vm.SelectedNode = PartRow(vm);
        for (int i = 0; i < 200 && !PartRow(vm).HasMeshEditBlock; i++) await Task.Delay(5);

        var row = PartRow(vm);
        Assert.Equal(refusal, row.MeshEditBlock);
        Assert.False(row.CanOpenInBlender);
        Assert.Equal(refusal, row.OpenInBlenderHint);
        Assert.Single(row.MapGroups);
        Assert.All(row.MapGroups.SelectMany(group => group.Cards).Where(card => !card.IsRamp),
            card => Assert.True(vm.CanAcceptDrop(card)));
        // The gate's one sentence, in the row that already exists for a part-level refusal.
        Assert.True(row.HasPartRefusal);
        Assert.Equal(refusal, row.PartRefusal);
    }

    /// <summary>The two shapes a source answer comes in, told apart by whether the slot it names belongs to
    /// an edit. One takes the game's own value from a place it names — the recorded keep-the-original — and
    /// the mod owns nothing there; the other takes a file the mod made for another of its own positions, and
    /// the mod owns that. One line for each, and the edited marker on exactly the second.</summary>
    [Fact]
    public void A_source_answer_says_which_of_its_two_shapes_it_is()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.WithOwnedSlots(),
            s => s.Resolve = part => Installed(part));

        var borrowed = Card(vm, "slot-owned-2");
        Assert.Equal(BindingKind.SourceSlot, borrowed.Binding);
        Assert.Equal(EditMapCardVm.SharedWithAnotherMap, borrowed.OriginNote);
        Assert.True(borrowed.ShowsOwnedOrigin);
        Assert.True(borrowed.HasEditBadge);
        Assert.False(borrowed.ShowsGameTextureName);
        // The line the mod's own work never gets to wear: nothing here came in with the mesh.
        Assert.NotEqual(EditMapCardVm.ReplacementOrigin, borrowed.OriginNote);
    }

    // ---- the shared-original boundary ----

    /// <summary>A game texture several positions draw stays actionable, and both gestures route through
    /// consent naming the measured reach before anything is written.</summary>
    [Fact]
    public async Task A_texture_more_than_one_place_draws_routes_the_open_and_the_drop_to_consent()
    {
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.TextureUseCount = _ => 2;
        });
        vm.NewEditCommand.Execute(PartRow(vm));
        var edit = Assert.Single(PartRow(vm).Children);
        var card = edit.MapGroups.SelectMany(group => group.Cards)
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);
        Assert.True(card.IsGameSlot);
        Assert.Equal(EditTextureSharing.Shared, card.Sharing);

        Assert.Equal(2, card.SharingUses);
        Assert.True(card.CanOpen);
        Assert.True(card.CanOpenUvGuide); // sharing never gates a read-only guide
        await vm.OpenCardCommand.ExecuteAsync(card);
        Assert.Equal(card.Slot, shell.LastOpenSlot);

        vm.Status = "";
        Assert.True(vm.CanAcceptDrop(card));
        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, card);
        Assert.Equal(card.Slot, shell.LastDropSlot);
        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Equal(BindingKind.TargetGameValue, session.Slots(edit.EditDefinitionId!)
            .Single(state => state.Slot.Domain == TargetSlotDomain.Game
                && state.Slot.Input == TargetInputKind.BaseColor).Binding.Kind);
    }

    /// <summary>The boundary is drawn at SHARING, not at stock textures: a texture only this part draws is
    /// one exact use already, and both gestures go through.</summary>
    [Fact]
    public async Task A_texture_only_this_part_draws_still_opens_and_takes_a_picture()
    {
        var (vm, _, shell) = Page(WithAsset(Bare(), "textures/dropped.png", ProjectAssetKind.Picture), s =>
        {
            s.Resolve = part => Installed(part);
            s.TextureUseCount = _ => 1;
            s.DropResult = new EditAssetResult("textures/dropped.png", "Dropped");
        });
        vm.NewEditCommand.Execute(PartRow(vm));
        var card = Assert.Single(PartRow(vm).Children).MapGroups.SelectMany(group => group.Cards)
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);

        Assert.Equal(EditTextureSharing.Private, card.Sharing);
        Assert.True(card.CanOpen);
        await vm.OpenCardCommand.ExecuteAsync(card);
        Assert.NotNull(shell.LastOpenSlot);

        Assert.True(vm.CanAcceptDrop(card));
        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, card);
        Assert.NotNull(shell.LastDropSlot);
        // What landed, where: the edit, the map and the material it sits on — the same three the question
        // that led here names.
        Assert.Contains("Dropped", vm.Status);
        Assert.Contains("Edit 1", vm.Status);
        Assert.Contains(card.Slot.MaterialName!, vm.Status);
    }

    /// <summary>The bare part's shared card asks before the first edit is minted. Declining leaves the part
    /// with no edit, and the reach sentence gets its own paragraph.</summary>
    [Fact]
    public async Task A_declined_shared_first_edit_drop_names_its_places_and_mints_nothing()
    {
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.TextureUseCount = _ => 2;
            s.ConfirmResult = false;
        });
        var card = PartRow(vm).MapGroups[0].Cards
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);

        Assert.Equal(EditTextureSharing.Shared, card.Sharing);
        Assert.Equal(2, card.SharingUses);
        Assert.True(card.ShowsDropTarget);
        Assert.True(vm.CanAcceptDrop(card));
        Assert.Equal("Original maps", PartRow(vm).OriginalMapsLabel);

        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, card);

        Assert.Empty(EditsFor(session));
        Assert.Equal(1, shell.ConfirmCalls);
        Assert.Equal("Apply dropped.png?", shell.LastConfirmTitle);
        Assert.Equal($"dropped.png becomes this part's base color on {card.Slot.MaterialName}. "
            + "This adds the part's first edit.\n\nThis outfit draws this original map in 2 places. "
            + "The edit changes all of them.", shell.LastConfirmBody);
        Assert.Equal("Apply", shell.LastConfirmLabel);
        Assert.Null(shell.LastDropSlot);
    }

    /// <summary>A toon ramp is outside the boundary. The build emits a ramp at ONE draw by construction — it
    /// anchors on the part's own index buffer and material — so a ramp several parts share is still one
    /// part's shading when it ships, and there is no reach to refuse. Most parts of an item share one ramp,
    /// so a boundary that covered them would put a refusal on nearly every ramp card while the Choose button
    /// beside it went on working: one card, two surfaces, opposite answers.</summary>
    [Fact]
    public void A_toon_ramp_more_than_one_part_draws_keeps_its_pick_list()
    {
        var (vm, _, _) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.TextureUseCount = _ => 2;
        });
        vm.NewEditCommand.Execute(PartRow(vm));
        var ramp = Assert.Single(PartRow(vm).Children).MapGroups.SelectMany(group => group.Cards)
            .First(card => card.IsRamp && card.Slot.MaterialSlotIndex == 0);

        Assert.Equal(EditTextureSharing.Private, ramp.Sharing);
        Assert.Null(ramp.DropHint);   // and not the shared sentence, which is what it used to hover
        Assert.True(ramp.CanChooseRamp);
        Assert.Equal("Choose the toon ramp for this material", ramp.ChooseRampHint);
    }

    /// <summary>An item nothing has read yet cannot say how far an edit to one of its stock textures would
    /// reach, and the page treats that as a refusal rather than as a private texture. The read lands on a
    /// worker after the page is drawn, so the window is real on every first run and after every rescan — and
    /// a picture that goes through it binds the mod's own file, which the boundary then exempts for good.
    ///
    /// <para>The refusal is the state's own sentence, and the redraw the finished read causes lifts it.</para>
    /// </summary>
    [Fact]
    public async Task A_card_on_an_item_still_being_read_refuses_both_gestures_until_the_read_lands()
    {
        // Through EditPageVm.SharingOf → IEditPageShell.SubjectRead and TextureUses, on the page's own open
        // and drop verbs. Both move together, exactly as the window's own shell moves them: while the read
        // is in flight there is no count to give.
        int? uses = null;
        var read = EditSubjectRead.Reading;
        var (vm, session, shell) = Page(WithAsset(Bare(), "textures/dropped.png", ProjectAssetKind.Picture),
            s =>
            {
                s.Resolve = part => Installed(part);
                s.TextureUseCount = _ => uses;
                s.SubjectReadState = _ => read;
                s.DropResult = new EditAssetResult("textures/dropped.png", "Dropped");
            });
        vm.NewEditCommand.Execute(PartRow(vm));
        var edit = Assert.Single(PartRow(vm).Children);
        var reading = edit.MapGroups.SelectMany(group => group.Cards)
            .Single(card => card.Slot.Input == TargetInputKind.BaseColor);

        Assert.Equal(EditTextureSharing.Unknown, reading.Sharing);
        Assert.False(reading.CanOpen);
        Assert.Equal(GameFilesGate.SubjectReading, reading.OpenHint);
        Assert.False(reading.CanOpenUvGuide);
        Assert.Equal(GameFilesGate.SubjectReading, reading.UvHint);
        await vm.OpenCardCommand.ExecuteAsync(reading);
        Assert.Null(shell.LastOpenSlot);
        Assert.Equal(GameFilesGate.SubjectReading, vm.Status);

        vm.Status = "";
        Assert.True(vm.CanAcceptDrop(reading));
        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, reading);
        Assert.Equal(GameFilesGate.SubjectReading, vm.Status);
        Assert.Null(shell.LastDropSlot);
        Assert.Equal(BindingKind.TargetGameValue, session.Slots(edit.EditDefinitionId!)
            .Single(state => state.Slot.Domain == TargetSlotDomain.Game
                && state.Slot.Input == TargetInputKind.BaseColor).Binding.Kind);

        // The read lands: one use, and the redraw it causes is the one the window already runs.
        uses = 1;
        read = EditSubjectRead.Answered;
        vm.Rebuild();
        var counted = Assert.Single(PartRow(vm).Children).MapGroups.SelectMany(group => group.Cards)
            .Single(card => card.Slot.Input == TargetInputKind.BaseColor);

        Assert.Equal(EditTextureSharing.Private, counted.Sharing);
        Assert.True(counted.CanOpen);
        Assert.True(counted.CanOpenUvGuide);
        Assert.True(vm.CanAcceptDrop(counted));
        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, counted);
        Assert.NotNull(shell.LastDropSlot);
    }

    /// <summary>The bare part's twin of the same window. Its cards are where a first edit is minted, so an
    /// unread item has to be refused BEFORE the mint — otherwise the drop leaves an edit standing on a
    /// texture nobody has counted the uses of.
    ///
    /// <para>Driven by a MISSING COUNT with the item reported as answered, which is the pairing the rule
    /// treats as unknown rather than as one use: the two halves of the install's answer are read separately,
    /// and the reading that refuses is the one a disagreement between them takes.</para></summary>
    [Fact]
    public async Task An_original_card_on_an_item_still_being_read_shows_no_drop_target_and_mints_nothing()
    {
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.TextureUseCount = _ => null;
        });
        var card = PartRow(vm).MapGroups[0].Cards
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);

        Assert.Equal(EditTextureSharing.Unknown, card.Sharing);
        Assert.False(card.ShowsDropTarget);
        Assert.True(vm.CanAcceptDrop(card));
        Assert.Equal(GameFilesGate.SubjectReading, card.DropHint);
        Assert.Equal("Original maps", PartRow(vm).OriginalMapsLabel);

        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, card);

        Assert.Equal(GameFilesGate.SubjectReading, vm.Status);
        Assert.Empty(EditsFor(session));
        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Null(shell.LastDropSlot);
    }

    [Theory]
    [InlineData(EditSubjectRead.Unavailable, EditTextureSharing.Unavailable,
        "Original maps · game files unavailable",
        "Game files unavailable. Use Tools · Locate game…, then Tools · Rescan game files.")]
    [InlineData(EditSubjectRead.Reading, EditTextureSharing.Unknown,
        "Original maps · still being read", "This item is still being read. Try again in a moment.")]
    [InlineData(EditSubjectRead.Unreadable, EditTextureSharing.Unreadable,
        "Original maps · couldn't be read",
        "This item couldn't be read from the game files. Use Tools · Rescan game files to try again.")]
    public void A_bare_parts_heading_and_UV_button_state_each_install_gate(
        EditSubjectRead read, EditTextureSharing sharing, string heading, string gate)
    {
        var (vm, _, _) = Page(Bare(), shell =>
        {
            shell.Resolve = part => Installed(part);
            shell.SubjectReadState = _ => read;
            shell.TextureUseCount = _ => null;
        });
        var row = PartRow(vm);
        var card = row.MapGroups[0].Cards.Single(candidate =>
            candidate.Slot.Input == TargetInputKind.BaseColor);

        Assert.Equal(sharing, card.Sharing);
        Assert.Equal(heading, row.OriginalMapsLabel);
        Assert.True(vm.CanAcceptDrop(card));
        Assert.False(card.CanOpenUvGuide);
        Assert.Equal(gate, card.UvHint);
    }

    // ---- the stand-in cards on a replacement that recorded no maps ----

    /// <summary>The one fact, on both surfaces that hold it. A replacement's build draws only the
    /// replacement's own maps — a picture bound to a game texture on that edit is stripped with a warning —
    /// so the cards standing in for those maps must not accept one. The page refusing and the build dropping
    /// are the same rule; a test that watched only one of them would let them drift apart.</summary>
    [Fact]
    public async Task A_stand_in_card_refuses_the_picture_the_build_would_drop()
    {
        string root = Path.Combine(Path.GetTempPath(), "remold-standin-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = AuthoredEditFixtures.Golden();
            project.RootDir = root;
            var ramp = project.TargetSlots.Single(slot => slot.Id == "slot-ramp");
            project.TargetSlots.Add(new TargetSlot
            {
                Id = "slot-old-stock-picture",
                Part = ramp.Part,
                Tier = ramp.Tier,
                SubmeshIndex = ramp.SubmeshIndex,
                MaterialSlotIndex = ramp.MaterialSlotIndex,
                Input = TargetInputKind.BaseColor,
                Domain = TargetSlotDomain.Game,
                Renderer = ramp.Renderer,
                Mesh = ramp.Mesh,
                Material = ramp.Material,
            });
            project.ProjectAssets.Add(new ProjectAsset
            {
                Id = "old-stock-picture",
                Kind = ProjectAssetKind.Picture,
                Label = "Old stock picture",
                File = "textures/old-stock.png",
            });
            // A hand-authored game picture on the edit that replaces the mesh — the shape a released mod
            // converts to, and the one the build has to throw away.
            project.EditDefinitions.Single(edit => edit.Id == "edit-long").Bindings.Add(new Binding
            {
                SlotId = "slot-old-stock-picture",
                Kind = BindingKind.ProjectAsset,
                ProjectAssetId = "old-stock-picture",
            });
            project.EditDefinitions.Single(edit => edit.Id == "edit-short").Bindings.Add(new Binding
            {
                SlotId = "slot-old-stock-picture",
                Kind = BindingKind.TargetGameValue,
            });
            foreach (var asset in project.ProjectAssets)
            {
                string file = Path.Combine(root, asset.File.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, asset.Id);
            }

            var (vm, _, shell) = Page(project, s => s.Resolve = part => Installed(part));
            var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
            Assert.True(edit.HasMeshEdit);
            var card = edit.MapGroups.SelectMany(group => group.Cards)
                .Single(candidate => candidate.Slot.SlotId == "slot-old-stock-picture");

            // What it is, and what it says it is.
            Assert.Equal(EditCardRole.StandIn, card.Role);
            Assert.True(card.IsOriginal);
            Assert.Equal(EditMapCardVm.StandInOrigin, card.OriginNote);
            Assert.True(card.ShowsQuietOrigin);
            Assert.False(card.ShowsMapActions);
            // The islands would be the ORIGINAL mesh's, under paint meant for the replacement.
            Assert.False(card.CanOpenUvGuide);
            Assert.False(card.ShowsDropTarget);
            Assert.False(vm.CanAcceptDrop(card));

            await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, card);
            Assert.Contains(EditMapCardVm.StandInNotDroppable, vm.Status);
            Assert.Null(shell.LastDropSlot);
            Assert.Equal(0, shell.ConfirmCalls);

            // The other half of the same rule: the build strips exactly this binding and says so.
            var plan = AuthoredBuildPlanner.Plan(project, new AuthoredBuildPlannerTests.Backend());
            var stripped = plan.Bindings.Single(binding =>
                binding.AuthoredSlot.Id == "slot-old-stock-picture");
            Assert.Empty(stripped.Emissions);
            Assert.Contains(plan.Warnings, warning =>
                warning.Contains("will not take effect", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ---- a float-format texture refuses every picture gesture by name ----

    /// <summary>The header the size line reads also says whether an edit could be encoded back into the
    /// slot's format. A float-format map cannot, so the card refuses drop, Browse and Open with one sentence
    /// where an unmeasured texture refuses with its own. Until the header is read the card refuses nothing.</summary>
    [Fact]
    public void A_float_format_texture_refuses_drop_browse_and_open_by_name()
    {
        var (vm, _, _) = Page(Bare(), s => s.Resolve = part => Installed(part));
        vm.SelectedNode = PartRow(vm);
        var card = PartRow(vm).MapGroups[0].Cards
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);
        Assert.True(card.ShowsDropTarget);
        Assert.True(card.CanBrowse);

        card.SetThumb(null, "256\u00d716", authorable: false);

        Assert.Equal(EditMapCardVm.FloatFormat, card.SharingRefusal);
        Assert.False(card.ShowsDropTarget);
        Assert.False(card.CanBrowse);
        Assert.False(card.CanOpen);
        Assert.Equal(EditMapCardVm.FloatFormat, card.DropHint);
        Assert.Equal(EditMapCardVm.FloatFormat, card.BrowseHint);
        Assert.Equal(EditMapCardVm.FloatFormat, card.OpenHint);
    }

    // ---- the drop confirm says when the picture is not the map's size ----

    /// <summary>A picture that does not match the map still applies, stretched by the UVs; the confirm says
    /// so before the picture is taken, with both sizes, and says nothing when they match.</summary>
    [Fact]
    public async Task A_first_edit_drop_confirm_names_a_size_that_differs_from_the_map()
    {
        string root = Path.Combine(Path.GetTempPath(), "drl-size-note-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string dropped = Path.Combine(root, "dropped.png");
            using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4, 4))
                SixLabors.ImageSharp.ImageExtensions.SaveAsPng(image, dropped);
            var (vm, _, shell) = Page(Bare(), s =>
            {
                s.Resolve = part => Installed(part);
                s.ConfirmResult = false;
            });
            vm.SelectedNode = PartRow(vm);
            var card = PartRow(vm).MapGroups[0].Cards
                .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);
            card.SetThumb(null, "4096\u00d74096");

            await vm.HandleDropAsync(new[] { dropped }, card);

            Assert.Equal(1, shell.ConfirmCalls);
            Assert.Contains("4\u00d74, the map is 4096\u00d74096. It still applies; UVs stretch it to fit.",
                shell.LastConfirmBody);

            card.SetThumb(null, "4\u00d74");
            await vm.HandleDropAsync(new[] { dropped }, card);

            Assert.Equal(2, shell.ConfirmCalls);
            Assert.DoesNotContain("the map is", shell.LastConfirmBody);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ---- the first-edit drop asks before it mints ----

    /// <summary>The question comes before the mint, and it is about the ACT rather than about an edit: there
    /// is none to name yet. A decline leaves the part exactly as it was — no edit, no selection moved — which
    /// is the abandoned-empty-edit residue this ordering exists to prevent.</summary>
    [Fact]
    public async Task A_declined_first_edit_drop_leaves_no_edit_and_no_selection_jump()
    {
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.ConfirmResult = false;
        });
        vm.SelectedNode = PartRow(vm);
        var card = PartRow(vm).MapGroups[0].Cards
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);

        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, card);

        Assert.Equal(1, shell.ConfirmCalls);
        Assert.Contains("dropped.png", shell.LastConfirmTitle);
        // The question names the act, never an edit the project does not have.
        Assert.DoesNotContain("Edit 1", shell.LastConfirmBody);
        Assert.Contains("first edit", shell.LastConfirmBody);
        Assert.Equal("Apply", shell.LastConfirmLabel);
        Assert.Empty(EditsFor(session));
        Assert.Null(shell.LastDropSlot);
        Assert.True(vm.SelectedNode!.IsPart);
    }

    /// <summary>Accepted, the mint follows and the picture lands on exactly the map it was dropped on — and
    /// the page's own question is not asked a second time by the publish route it hands the picture to.</summary>
    [Fact]
    public async Task An_accepted_first_edit_drop_is_confirmed_once_then_mints_nowhere_in_the_page()
    {
        var (vm, session, shell) = Page(WithAsset(Bare(), "textures/dropped.png", ProjectAssetKind.Picture),
            s =>
            {
                s.Resolve = part => Installed(part, materials: 2);
                s.TextureUseCount = _ => 2;
                s.DropResult = new EditAssetResult("textures/dropped.png", "Dropped");
            });
        var card = PartRow(vm).MapGroups[1].Cards
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);
        // The other material's base-colour card: the one the sentence must not read as.
        string other = PartRow(vm).MapGroups[0].Cards
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor).Slot.MaterialName!;

        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, card);

        Assert.Equal(1, shell.ConfirmCalls);
        Assert.True(shell.LastDropConfirmed);
        Assert.Equal(new EditTextureSharingOffer(EditTextureSharing.Shared, 2), shell.LastDropOffered);
        Assert.Contains("This outfit draws this original map in 2 places. The edit changes all of them.",
            shell.LastConfirmBody);
        Assert.Empty(EditsFor(session));
        Assert.Equal(1, shell.LastDropSlot!.MaterialSlotIndex);
        // The question and the result call the map the same thing — and BOTH name the material it lands
        // on, or a part with two materials raises one identical dialog for either of its base colours.
        Assert.Contains("base color", shell.LastConfirmBody);
        string material = shell.LastDropSlot.MaterialName!;
        Assert.Contains(material, shell.LastConfirmBody);
        Assert.DoesNotContain(other, shell.LastConfirmBody);
    }

    /// <summary>Browse is the drop, entered through a file dialog: the chosen picture is asked about and
    /// handed to the drop's own publish exactly as a dragged one is. A cancelled dialog is the one outcome
    /// that says nothing and leaves the part as it was.</summary>
    [Fact]
    public async Task Browse_lands_a_chosen_picture_through_the_drop_route_and_a_cancel_changes_nothing()
    {
        var (vm, session, shell) = Page(WithAsset(Bare(), "textures/dropped.png", ProjectAssetKind.Picture),
            s =>
            {
                s.Resolve = part => Installed(part, materials: 2);
                s.DropResult = new EditAssetResult("textures/dropped.png", "Dropped");
            });
        var card = PartRow(vm).MapGroups[1].Cards
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);

        await vm.BrowseCardCommand.ExecuteAsync(card);

        Assert.Equal(1, shell.PicturePicks);
        Assert.Equal(0, shell.ConfirmCalls);
        Assert.Empty(EditsFor(session));
        Assert.Equal("", vm.Status);

        shell.PickedPicture = @"C:\in\dropped.png";
        await vm.BrowseCardCommand.ExecuteAsync(card);

        Assert.Equal(2, shell.PicturePicks);
        Assert.Equal(1, shell.ConfirmCalls);
        Assert.Equal("Apply dropped.png?", shell.LastConfirmTitle);
        // The page mints nowhere: the confirmed pick is handed to the drop's publish, like a dragged file.
        Assert.Empty(EditsFor(session));
        Assert.True(shell.LastDropConfirmed);
        Assert.Equal(1, shell.LastDropSlot!.MaterialSlotIndex);
    }

    /// <summary>A file that is not a <c>.png</c> is refused the same way whichever gesture named it: the
    /// picker filters, and a name typed past the filter still meets the drop's own answer.</summary>
    [Fact]
    public async Task Browse_refuses_a_file_that_is_not_a_png_in_the_drops_own_words()
    {
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.PickedPicture = @"C:\in\paint.tga";
        });
        var card = PartRow(vm).MapGroups[0].Cards
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);

        await vm.BrowseCardCommand.ExecuteAsync(card);

        Assert.Equal("Only a .png can replace a map.", vm.Status);
        Assert.Empty(EditsFor(session));
    }

    /// <summary>A publish that fails after the question takes the mint back with it: the modder answered a
    /// question about a picture landing, and an empty edit is not that.</summary>
    [Fact]
    public async Task A_first_edit_drop_that_publishes_nothing_leaves_no_empty_edit_behind()
    {
        // The mod's own selection and the install's part list, as the shipped window has them: taking the
        // mint back takes the slots it opened with it, and the row the modder is standing on is the
        // install's rather than a by-product of having authored something.
        var project = Bare();
        project.WorkspaceIndex = new AuthoredWorkspaceIndex
        {
            Selection = { new SelectionEntry { Character = Body.Subject, Outfit = Body.Outfit } },
        };
        var (vm, session, shell) = Page(project, s =>
        {
            s.Parts = (_, _) => new[] { Body };
            s.Resolve = part => Installed(part);
            s.DropResult = null;   // the picture would not read
        });
        var card = PartRow(vm).MapGroups[0].Cards
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);
        long before = session.Revision;

        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, card);

        Assert.NotNull(shell.LastDropSlot);
        Assert.Empty(EditsFor(session));
        Assert.True(vm.SelectedNode is null || vm.SelectedNode.IsPart);
        Assert.Equal(before, session.Revision);
    }

    // ---- the install read behind a bare part's cards ----

    /// <summary>Three states, and each says which. The read running is a running-work line; a part the game
    /// files do not have is a settled sentence rather than a permanent blank; a material position with
    /// nothing readable under it keeps its heading, because the modder counts positions in that list.</summary>
    [Fact]
    public async Task A_bare_parts_card_area_says_which_of_the_install_reads_three_states_it_is_in()
    {
        var hold = new TaskCompletionSource<LegacyResolvedPart?>();
        var (reading, _, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.ResolveHold = hold;
        });
        Assert.True(PartRow(reading).IsReadingOriginals);
        Assert.Null(PartRow(reading).OriginalsNote);
        shell.ResolveHold = null;
        hold.SetResult(Installed(Body));
        for (int i = 0; i < 200 && PartRow(reading).IsReadingOriginals; i++) await Task.Delay(5);
        Assert.False(PartRow(reading).IsReadingOriginals);
        Assert.Null(PartRow(reading).OriginalsNote);

        var (missing, _, _) = Page(Bare(), s => s.Resolve = _ => null);
        for (int i = 0; i < 200 && PartRow(missing).OriginalsNote is null; i++) await Task.Delay(5);
        Assert.Empty(PartRow(missing).MapGroups);
        Assert.Equal(EditNodeVm.OriginalsNotInstalled, PartRow(missing).OriginalsNote);
        Assert.False(PartRow(missing).IsReadingOriginals);

        // The install has the part and the material, and nothing readable under it.
        var (empty, _, _) = Page(Bare(), s => s.Resolve = part => new LegacyResolvedPart(part,
            Ref(70001, part.RendererSlot), Ref(72001, part.RendererSlot + "_mesh"),
            new[] { new LegacyResolvedMaterial(0, "mat0", Ref(74001, "mat0"),
                Array.Empty<LegacyResolvedTexture>()) }));
        for (int i = 0; i < 200 && empty.SelectedNode is null && PartRow(empty).MapGroups.Count == 0;
             i++) await Task.Delay(5);
        var group = Assert.Single(PartRow(empty).MapGroups);
        Assert.Equal("mat0", group.Title);
        Assert.Empty(group.Cards);
        Assert.Equal(EditMapGroupVm.NoMapsRead, group.Note);
        Assert.Null(PartRow(empty).OriginalsNote);
    }

    [Fact]
    public void Original_cards_keep_every_property_and_use_the_pinned_order()
    {
        var (vm, _, _) = Page(Bare(), shell => shell.Resolve = InstalledVocabulary);

        var cards = Assert.Single(PartRow(vm).MapGroups).Cards;
        Assert.Equal(new[]
        {
            "_BaseMap", "_BumpMap", "_RMOTex", "_BlendTex", "_RampMap",
            "_DetailAlbedo", "_DetailMask", "_MaskTex", "_VertexAnimNoiseTex",
        }, cards.Select(card => card.Slot.ShaderProperty));
        Assert.Equal(new[]
        {
            "Base color", "Normal map", "RMO map", "Effect map", "Toon ramp",
            "Detail color", "Detail mask", "Mask", "Vertex Anim Noise",
        }, cards.Select(card => card.MapLabel));
        Assert.Equal(2, cards.Count(card => card.Slot.Input == TargetInputKind.Texture
            && card.Slot.ShaderProperty is "_DetailAlbedo" or "_DetailMask"));
    }

    [Fact]
    public async Task Original_drop_rematches_the_exact_ordinary_property_after_minting()
    {
        var (vm, session, shell) = Page(Bare(), s =>
        {
            s.Resolve = InstalledVocabulary;
            s.DropResult = null;
        });
        var card = Assert.Single(PartRow(vm).MapGroups).Cards.Single(candidate =>
            candidate.Slot.ShaderProperty == "_DetailMask");

        await vm.HandleDropAsync(new[] { @"C:\in\detail.png" }, card);

        Assert.Equal("_DetailMask", shell.LastDropSlot!.ShaderProperty);
        Assert.Equal(TargetInputKind.Texture, shell.LastDropSlot.Input);
        Assert.Empty(EditsFor(session));
    }

    [Fact]
    public void Replacement_cards_follow_bound_effect_and_do_not_invent_normal()
    {
        var resolved = new LegacyResolvedPart(Body, Ref(70001, Body.RendererSlot),
            Ref(72001, Body.RendererSlot + "_mesh"),
            new[]
            {
                new LegacyResolvedMaterial(0, "mat0", Ref(74001, "mat0"), new[]
                {
                    new LegacyResolvedTexture(TargetInputKind.BaseColor, "bundle", "base", null,
                        Ref(75001, "base"), "_BaseMap"),
                    new LegacyResolvedTexture(TargetInputKind.Blend, "bundle", "effect", null,
                        Ref(75005, "effect"), "_BlendTex"),
                    new LegacyResolvedTexture(TargetInputKind.Texture, "bundle", "detail", null,
                        Ref(75006, "detail"), "_DetailAlbedo"),
                }),
            }, MaterialIndexCounts: new[] { 3 });
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.EnsurePartSlots(Body, _ => resolved);
        session.RecordReplacementOutputs("edit-long", 1);
        var (vm, _) = On(session, shell => shell.Resolve = _ => resolved);

        var cards = Assert.Single(PartRow(vm).Children[0].MapGroups).Cards;
        Assert.Equal(new[] { "_BaseMap", "_BlendTex", "_DetailAlbedo" },
            cards.Select(card => card.Slot.ShaderProperty));
        Assert.DoesNotContain(cards, card => card.Slot.Input == TargetInputKind.Normal);
        var effect = cards.Single(card => card.Slot.Input == TargetInputKind.Blend);
        Assert.Equal("Effect map", effect.MapLabel);
        Assert.Equal("UV1", effect.UvButtonLabel);
        Assert.Equal("Opens a UV guide for this map: a white wireframe of the second UV set (UV1) it uses",
            effect.UvHint);
        Assert.Equal("UV", cards.Single(card => card.Slot.Input == TargetInputKind.BaseColor).UvButtonLabel);
    }

    [Fact]
    public async Task Surplus_material_cards_stay_visible_and_refuse_with_the_no_carrier_reason()
    {
        var resolved = Installed(Body, 2, new[] { 3, 0 });
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.EnsurePartSlots(Body, _ => resolved);
        session.RecordReplacementOutputs("edit-long", 1);
        var (vm, shell) = On(session, s => s.Resolve = _ => resolved);

        var surplus = PartRow(vm).Children[0].MapGroups.Single(group => group.Title == "mat1");
        var card = surplus.Cards.Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);
        Assert.Equal(EditMapCardVm.NoDrawableCarrier, card.OriginNote);
        Assert.Equal(EditMapCardVm.NoDrawableCarrier, card.OpenHint);
        Assert.False(card.CanOpen);

        await vm.HandleDropAsync(new[] { @"C:\in\surplus.png" }, card);

        Assert.Equal(EditMapCardVm.NoDrawableCarrier, vm.Status);
        Assert.Null(shell.LastDropSlot);
    }

    [Fact]
    public async Task An_edited_ramp_card_with_no_drawable_carrier_disables_Choose_with_the_same_gate()
    {
        var resolved = Installed(Body, 2, new[] { 3, 0 });
        var (vm, _, shell) = Page(Bare(), s => s.Resolve = _ => resolved);
        vm.NewEditCommand.Execute(PartRow(vm));
        var ramp = Assert.Single(PartRow(vm).Children).MapGroups
            .Single(group => group.Title == "mat1").Cards.Single(candidate => candidate.IsRamp);

        Assert.Equal(EditCardRole.Edited, ramp.Role);
        Assert.Equal(EditMapCardVm.NoDrawableCarrier, ramp.ChooseRampHint);
        Assert.False(ramp.CanChooseRamp);

        await vm.ChooseRampCommand.ExecuteAsync(ramp);

        Assert.Equal(EditMapCardVm.NoDrawableCarrier, vm.Status);
        Assert.Null(shell.LastRampSlot);
    }

    // ---- the toon ramp a fold moved ----

    /// <summary>Keeping the original toon ramp on a replacement's own slot records the ramp of the material
    /// the card is STANDING at. On a part whose submeshes fold onto fewer materials, the submesh index and
    /// the material position are different numbers, and the submesh one addresses a ramp the modder never
    /// looked at — or none at all.</summary>
    [Fact]
    public async Task Keeping_the_original_ramp_records_the_folded_material_positions_ramp()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        session.EnsurePartSlots(Body, part => Installed(part, 2, new[] { 3, 3 }));
        session.RecordReplacementOutputs("edit-long", 3);
        var (vm, shell) = On(session, s =>
        {
            s.Resolve = part => Installed(part, 2, new[] { 3, 3 });
            s.RampResult = new EditRampPick(null);   // the pinned keep-the-original row
        });
        for (int i = 0; i < 200 && PartRow(vm).Children
                 .Single(node => node.EditDefinitionId == "edit-long").MapGroups.Count != 2;
             i++) await Task.Delay(5);

        var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        // Three submeshes over two materials: the last one's slot records folded material position 1 rather
        // than pretending its submesh index — 2 — is an installed material position.
        var card = edit.MapGroups[1].Cards.Single(candidate =>
            candidate.IsRamp && !candidate.IsGameSlot && candidate.Slot.SubmeshIndex == 2
            && candidate.Slot.MaterialSlotIndex == 1);
        Assert.Equal(1, card.Slot.GameMaterialSlotIndex);

        await vm.ChooseRampCommand.ExecuteAsync(card);

        string recorded = session.Slots("edit-long")
            .Single(state => state.Slot.Id == card.Slot.SlotId).Binding.SourceSlot!.SlotId;
        Assert.Equal(session.GameRampSlot(Body, 1), recorded);
        Assert.NotEqual(session.GameRampSlot(Body, 0), recorded);
        Assert.Equal(EditPageVm.KeptGameOwnRamp, vm.Status);
    }

    /// <summary>A curated refusal thrown by the ramp routes reaches the screen as it is. Those sentences are
    /// written for the person reading them — what could not be read, and what is not a toon ramp — and a
    /// surface that replaced them with the verb's own name would throw the whole diagnosis away.</summary>
    [Fact]
    public async Task A_curated_ramp_refusal_reaches_the_status_line_word_for_word()
    {
        var (vm, _, _) = Page(TextureOnly(), s => s.RampFailure =
            new AuthoredRefusalException(ThrowingRampShell.Refusal));

        await vm.ChooseRampCommand.ExecuteAsync(PartRow(vm).Children[0].MapGroups[0].Cards[0]);

        Assert.Equal(ThrowingRampShell.Refusal, vm.Status);
    }

    private static class ThrowingRampShell
    {
        internal const string Refusal = "gold.dds is not a 256 by 16 toon ramp.";
    }

    // ---- first touch leaves placements alone ----

    [Fact]
    public void A_new_edit_preserves_unplaced_edits_and_places_only_the_new_edit_in_Always()
    {
        var (vm, session, _) = Page(Unplaced());

        vm.NewEditCommand.Execute(PartRow(vm));

        var current = EditsFor(session);
        Assert.Equal("Edit 3", Assert.Single(current, edit =>
            edit.Placements.Any(placement => placement.IsAlways)).Label);
        Assert.All(current.Where(edit => edit.Label != "Edit 3"),
            edit => Assert.DoesNotContain(edit.Placements, placement => placement.IsAlways));
    }

    [Fact]
    public void A_new_edit_preserves_the_existing_Always_hide()
    {
        var project = AuthoredEditFixtures.Golden();
        project.Always.Clear();
        var setup = new AuthoredEditSession(project);
        setup.AddHideEdit(Body);
        var (vm, session, _) = Page(setup.Snapshot());

        vm.NewEditCommand.Execute(PartRow(vm));

        var current = EditsFor(session);
        Assert.Single(current, edit => edit.Kind == EditDefinitionKind.Hide
            && edit.Placements.Any(placement => placement.IsAlways));
        Assert.Equal(3, current.Count(edit => edit.Kind == EditDefinitionKind.Content));
    }

    [Fact]
    public void A_revert_preserves_an_edits_unplaced_status()
    {
        var (vm, session, _) = Page(Unplaced());

        vm.RevertRampCommand.Execute(PartRow(vm).Children[0].MapGroups[0].Cards[0]);

        Assert.Empty(session.Snapshot().Always);
        Assert.DoesNotContain(EditsFor(session), edit =>
            edit.Placements.Any(placement => placement.IsAlways));
    }

    [Fact]
    public void A_refused_new_edit_preserves_the_bare_part_tree()
    {
        var (vm2, shell2) = On(new AuthoredEditSession(Bare()), s =>
            s.Resolve = part => new LegacyResolvedPart(part, new GameAssetRef(), Ref(72001, "mesh"),
                Array.Empty<LegacyResolvedMaterial>()));

        vm2.NewEditCommand.Execute(PartRow(vm2));

        Assert.Equal("Couldn't find this part in the current game files.", vm2.Status);
        Assert.Equal(0, shell2.ProjectChangedCalls);
        Assert.Empty(PartRow(vm2).Children);
    }

    [Fact]
    public async Task A_refused_delete_preserves_existing_placements_and_project_notifications()
    {
        var project = AuthoredEditFixtures.WithBorrowedSlot();
        project.Always.Clear();
        var (vm, session, shell) = Page(project);

        await vm.DeleteEditCommand.ExecuteAsync(PartRow(vm).Children[0]);

        Assert.Contains("cannot be deleted while", vm.Status);
        Assert.Empty(session.Snapshot().Always);
        Assert.Equal(0, shell.ProjectChangedCalls);
    }

    // ---- busy, which outlives the redraw ----

    [Fact]
    public async Task Enter_preserves_a_busy_gate_and_a_recorded_refusal()
    {
        var (vm, _, shell) = Page(Bare(), candidate => candidate.Resolve = part =>
            new LegacyResolvedPart(part, new GameAssetRef(), Ref(72001, "mesh"),
                Array.Empty<LegacyResolvedMaterial>()));
        vm.NewEditCommand.Execute(PartRow(vm));
        string refusal = vm.Status;
        shell.SubjectHold = new TaskCompletionSource();
        var running = vm.OpenSubjectInBlenderCommand.ExecuteAsync(Subject(vm));
        Assert.True(PartRow(vm).IsBusy);

        vm.Enter();

        Assert.True(PartRow(vm).IsBusy);
        Assert.Equal(refusal, PartRow(vm).Problem);
        Assert.Equal(refusal, vm.Status);
        shell.SubjectHold.SetResult();
        await running;
    }

    [Fact]
    public async Task A_bare_parts_open_holds_the_part_and_refuses_a_second_click()
    {
        var gate = new TaskCompletionSource();
        var (vm, _, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.BlenderHold = gate;
        });

        var running = vm.OpenInBlenderCommand.ExecuteAsync(PartRow(vm));

        // The stock open owns the part gate, so both opens on that row wait on the same session.
        Assert.True(PartRow(vm).IsBusy);

        await vm.OpenInBlenderCommand.ExecuteAsync(PartRow(vm));
        Assert.Equal(1, shell.BlenderCalls);
        Assert.Equal(Remold.App.ViewModels.BlenderGate.Busy, vm.Status);

        gate.SetResult();
        await running;
        Assert.False(PartRow(vm).IsBusy);
    }

    [Fact]
    public async Task A_verb_on_one_edit_survives_a_redraw_another_verb_causes()
    {
        var gate = new TaskCompletionSource();
        var (vm, _, _) = Page(AuthoredEditFixtures.Saved(), s => s.BlenderHold = gate);
        var edit = PartRow(vm).Children[0];
        string editId = edit.EditDefinitionId!;

        var running = vm.OpenInBlenderCommand.ExecuteAsync(edit);
        Assert.True(PartRow(vm).Children[0].IsBusy);

        vm.NewEditCommand.Execute(PartRow(vm));

        // The tree is new; edit 1's redrawn row still reports the session running on it.
        var redrawn = PartRow(vm).Children.Single(n => n.EditDefinitionId == editId);
        Assert.True(redrawn.IsBusy);
        // And the second edit, which nothing is running on, does not.
        Assert.False(PartRow(vm).Children.Single(n => n.EditDefinitionId != editId).IsBusy);

        gate.SetResult();
        await running;
        Assert.False(PartRow(vm).Children.Single(n => n.EditDefinitionId == editId).IsBusy);
    }

    // ---- selection and the deep link ----

    [Fact]
    public void Selecting_an_overview_row_selects_that_edit()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden());
        var part = PartRow(vm);
        vm.SelectedNode = part;

        vm.SelectEditCommand.Execute(part.Overview[1]);

        Assert.Same(part.Children[1], vm.SelectedNode);
        Assert.Equal(new[] { "Long body", "Short body" }, part.Overview.Select(o => o.Title));
    }

    [Fact]
    public void The_footer_moves_to_build_on_the_edit_it_was_showing()
    {
        var (vm, _, shell) = Page(AuthoredEditFixtures.Golden());
        var edit = PartRow(vm).Children[1];

        vm.GoToBuildCommand.Execute(edit);

        Assert.Equal(1, shell.BuildCalls);
        Assert.Equal("edit-short", shell.LastBuildEdit!.EditDefinitionId);
    }

    [Fact]
    public void A_selection_survives_the_redraw_a_change_causes()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Saved());
        var edit = PartRow(vm).Children[0];
        vm.SelectedNode = edit;

        edit.EditLabel = "Long coat";
        vm.CommitRenameCommand.Execute(edit);

        Assert.NotNull(vm.SelectedNode);
        Assert.Equal("Long coat", vm.SelectedNode!.Title);
        Assert.Equal(EditNodeKind.Edit, vm.SelectedNode.Kind);
    }

    [Fact]
    public void A_subject_or_skeleton_selection_survives_the_redraw_too()
    {
        var (vm, _, _) = Page(AuthoredEditFixtures.Golden(),
            s => s.Skeleton = new EditSkeletonOutline(86, Array.Empty<SkeletonNodeVm>()));

        vm.SelectedNode = Subject(vm);
        vm.Rebuild();
        Assert.NotNull(vm.SelectedNode);
        Assert.Equal(EditNodeKind.Subject, vm.SelectedNode!.Kind);

        vm.SelectedNode = Subject(vm).Children.Single(n => n.IsSkeleton);
        vm.Rebuild();
        Assert.NotNull(vm.SelectedNode);
        Assert.Equal(EditNodeKind.Skeleton, vm.SelectedNode!.Kind);
    }

    // ---- problems ----

    [Fact]
    public void A_subject_shows_that_something_under_it_needs_attention_without_speaking_for_it()
    {
        var (vm, _, _) = Page(Bare(), s => s.Resolve = part => new LegacyResolvedPart(
            part, new GameAssetRef(), Ref(72001, "mesh"), Array.Empty<LegacyResolvedMaterial>()));

        vm.NewEditCommand.Execute(PartRow(vm));

        var subject = Subject(vm);
        Assert.True(subject.HasProblemBadge);
        // The badge rolls up so a collapsed branch shows one; the sentence stays where the fix is.
        Assert.Null(subject.Problem);
        Assert.False(subject.HasProblem);
        Assert.Equal(EditNodeVm.UnderThisRow, subject.ProblemBadgeTip);
        Assert.Equal(PartRow(vm).Problem, PartRow(vm).ProblemBadgeTip);
    }

    // ---- previews ----

    [Fact]
    public void Indexed_board_matches_scan_rows_and_cards_on_a_mixed_project()
    {
        var project = CrossEditBorrow();
        var session = new AuthoredEditSession(project);
        var board = EditBoardSnapshot.Create(session.Snapshot());
        var (vm, _) = On(session);

        var expectedRows = session.Outline().Edits.Select(edit =>
            (edit.Id, edit.Label, Part: edit.Target.RendererSlot)).ToArray();
        var actualRows = vm.Nodes.SelectMany(subject => subject.Children)
            .Where(part => part.IsPart).SelectMany(part => part.Children)
            .Select(edit => (edit.EditDefinitionId!, edit.Title, edit.Part!.RendererSlot)).ToArray();
        Assert.Equal(expectedRows, actualRows);

        foreach (var expected in session.Outline().Edits.Where(edit => edit.Kind == EditDefinitionKind.Content))
        {
            Assert.Equal(session.Slots(expected.Id).Select(state => state.Slot.Id),
                board.Slots(expected.Id).Select(state => state.Slot.Id));
            var scanStates = session.Slots(expected.Id);
            bool meshEdited = scanStates.Any(state => state.Slot.Input == TargetInputKind.Geometry
                && state.Binding.Kind != BindingKind.TargetGameValue);
            var textureStates = scanStates.Where(state =>
                    (state.Slot.Input is TargetInputKind.BaseColor or TargetInputKind.Normal
                        or TargetInputKind.Rmo or TargetInputKind.Blend or TargetInputKind.Ramp
                        or TargetInputKind.Texture)
                    && state.Slot.MaterialBindingPresent != false).ToArray();
            var outputs = meshEdited ? textureStates.Where(state =>
                state.Slot.Domain == TargetSlotDomain.EditOutput && state.Slot.SubmeshIndex is not null).ToArray()
                : Array.Empty<EditSlotState>();
            var represented = outputs.Select(state => state.Slot.SubmeshIndex!.Value).ToHashSet();
            var scanCards = (outputs.Length == 0
                    ? textureStates.Where(state => state.Slot.Domain == TargetSlotDomain.Game)
                    : outputs.Concat(textureStates.Where(state => state.Slot.Domain == TargetSlotDomain.Game
                        && !represented.Contains(state.Slot.MaterialSlotIndex ?? state.Slot.SubmeshIndex ?? 0))))
                .Select(state => state.Slot.Id).OrderBy(id => id).ToArray();
            var boardCards = vm.Nodes.SelectMany(subject => subject.Children).SelectMany(part => part.Children)
                .Single(row => row.EditDefinitionId == expected.Id).MapGroups
                .SelectMany(group => group.Cards).Select(card => card.Slot.SlotId)
                .OrderBy(id => id).ToArray();
            Assert.Equal(scanCards, boardCards);
        }
    }

    [Fact]
    public async Task Older_card_preview_completion_after_reselection_cannot_land()
    {
        var old = new TaskCompletionSource<EditMapPreview?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<EditMapPreview?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, shell) = Page(TextureOnly(), s =>
        {
            s.MapPreviewHolds.Enqueue(old);
            s.MapPreviewHolds.Enqueue(current);
        });
        var edit = PartRow(vm).Children[0];
        vm.SelectedNode = edit;
        for (int i = 0; i < 200 && shell.MapPreviewCalls < 1; i++) await Task.Delay(5);
        var card = Assert.Single(edit.MapGroups.SelectMany(group => group.Cards));

        card.ReleaseThumb();
        var newest = vm.LoadPreviewsAsync(edit);
        for (int i = 0; i < 200 && shell.MapPreviewCalls < 2; i++) await Task.Delay(5);
        current.SetResult(new EditMapPreview(null, EditMapCardVm.NoDimensions, "new.png"));
        await newest;
        old.SetResult(new EditMapPreview(null, EditMapCardVm.NoDimensions, "old.png"));
        for (int i = 0; i < 200 && shell.MapPreviewCompletions < 2; i++) await Task.Delay(5);

        Assert.Equal(2, shell.MapPreviewCompletions);
        Assert.Equal("new.png", card.MissingFile);
    }

    [Fact]
    public void A_serialized_return_warning_is_exposed_on_the_edit_row_and_inspector()
    {
        var project = TextureOnly();
        var warned = project.EditDefinitions.First(edit => edit.Kind == EditDefinitionKind.Content);
        warned.ReturnWarning = "Blender kept an extra UV layer.";
        var (vm, _, _) = Page(project);

        var edit = PartRow(vm).Children.Single(node => node.EditDefinitionId == warned.Id);
        Assert.True(edit.HasReturnWarning);
        Assert.Equal("Blender kept an extra UV layer.", edit.ReturnWarning);
    }

    [Fact]
    public async Task A_shell_with_no_picture_settles_the_row_into_its_quiet_state()
    {
        var (vm, _, _) = Page(TextureOnly());
        var edit = PartRow(vm).Children[0];

        await vm.LoadPreviewsAsync(edit);

        Assert.True(edit.IsMeshPreviewFailed);
        Assert.False(edit.IsMeshPreviewLoading);
        var card = edit.MapGroups[0].Cards[0];
        Assert.True(card.IsThumbFailed);
        Assert.Equal(EditMapCardVm.NoDimensions, card.Dimensions);
        // A failure is not cached — re-selecting the row asks again.
        Assert.True(edit.NeedsMeshPreview);
        Assert.NotNull(card.BeginThumbRequestIfNeeded());
        // Nothing to read is not a cause; there is no retry line for it.
        Assert.False(edit.HasPreviewCause);
    }

    /// <summary>A card whose answer names a file the mod folder does not hold settles into its OWN state: the
    /// empty tile says the file is missing rather than that there is no preview, and the note under it names
    /// the file. Not the failed-read state either — a retry line would be a promise, and the file is gone.
    /// </summary>
    [Fact]
    public async Task A_card_whose_file_is_gone_names_it_instead_of_reading_as_no_preview()
    {
        var (vm, _, _) = Page(TextureOnly(), s => s.PreviewMissingFile = "textures/skin.png");
        var edit = PartRow(vm).Children[0];

        await vm.LoadPreviewsAsync(edit);

        var card = edit.MapGroups[0].Cards[0];
        Assert.True(card.IsThumbFailed);
        Assert.Equal("textures/skin.png", card.MissingFile);
        Assert.True(card.HasMissingFile);
        Assert.Equal(EditMapCardVm.MissingTile, card.ThumbNote);
        Assert.Equal(EditMapCardVm.MapFileMissing("textures/skin.png"), card.MissingNote);
        Assert.False(card.HasPreviewCause);
    }

    /// <summary>The quiet no-preview card is untouched by that state: it names no file and its tile reads as
    /// it always has.</summary>
    [Fact]
    public async Task A_card_with_nothing_to_show_names_no_missing_file()
    {
        var (vm, _, _) = Page(TextureOnly());
        var edit = PartRow(vm).Children[0];

        await vm.LoadPreviewsAsync(edit);

        var card = edit.MapGroups[0].Cards[0];
        Assert.True(card.IsThumbFailed);
        Assert.False(card.HasMissingFile);
        Assert.Equal(EditMapCardVm.NoPreviewTile, card.ThumbNote);
        Assert.Null(card.MissingNote);
    }

    /// <summary>And the state is never cached: the card asks again on the next read, and a file that is back
    /// leaves the card saying nothing about it. Putting the file back is the remedy the card names, so a card
    /// that kept saying it after would be naming a remedy that does not work.</summary>
    [Fact]
    public async Task The_missing_state_is_asked_again_and_dropped()
    {
        var (vm, _, shell) = Page(TextureOnly(), s => s.PreviewMissingFile = "textures/skin.png");
        var edit = PartRow(vm).Children[0];
        await vm.LoadPreviewsAsync(edit);
        Assert.True(edit.MapGroups[0].Cards[0].HasMissingFile);
        int asked = shell.MapPreviewCalls;

        shell.PreviewMissingFile = null;
        await vm.LoadPreviewsAsync(edit);

        var card = edit.MapGroups[0].Cards[0];
        Assert.True(shell.MapPreviewCalls > asked);
        Assert.False(card.HasMissingFile);
        Assert.Null(card.MissingNote);
        Assert.Equal(EditMapCardVm.NoPreviewTile, card.ThumbNote);
    }

    [Fact]
    public async Task A_preview_that_fails_to_read_says_so_on_the_row_and_never_on_the_status_line()
    {
        var (vm, _, _) = Page(TextureOnly(), s => s.PreviewsThrow = true);
        var edit = PartRow(vm).Children[0];
        vm.Status = "Copied Long body.";

        await vm.LoadPreviewsAsync(edit);

        Assert.True(edit.HasPreviewCause);
        Assert.True(edit.MapGroups[0].Cards[0].HasPreviewCause);
        // The status line belongs to the verb the modder ran.
        Assert.Equal("Copied Long body.", vm.Status);
    }

    [Fact]
    public async Task Preview_invalidation_discards_the_cached_render_and_reloads_it()
    {
        var (vm, session, shell) = Page(AuthoredEditFixtures.WithOwnedSlots(),
            s =>
            {
                s.Resolve = part => Installed(part);
                s.PreviewsSucceed = true;
            });
        var edit = PartRow(vm).Children[0];
        await vm.LoadPreviewsAsync(edit);
        vm.SelectedNode = edit;
        await Task.Yield();
        int meshCalls = shell.EditPreviewCalls;

        session.ChooseInheritedCarrier("edit-long", "slot-owned");
        for (int i = 0; shell.ProjectChangedCalls == 0 && i < 100; i++) await Task.Yield();
        Assert.Equal(1, shell.ProjectChangedCalls);
        var rebuilt = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        await vm.LoadPreviewsAsync(rebuilt);

        Assert.True(shell.EditPreviewCalls > meshCalls);
        session.RenameEdit("edit-long", "dispose cached previews");
    }

    [Fact]
    public void Published_Blender_return_rebuilds_the_page_from_the_session_event()
    {
        string root = Path.Combine(Path.GetTempPath(), "remold-edit-return-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = AuthoredEditFixtures.Saved();
            project.RootDir = root;
            var geometry = project.EditDefinitions.Single().Bindings
                .Single(binding => binding.SlotId == "slot-geometry");
            geometry.Kind = BindingKind.TargetGameValue;
            geometry.ProjectAssetId = null;
            var (vm, session, shell) = Page(project);
            var before = PartRow(vm).Children[0];
            string source = Path.Combine(root, "return.glb");
            File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
            var ingress = ProjectAssetIngress.Begin(session.Snapshot(), "edit-long", "slot-geometry", source);

            session.PublishAssetForBinding(ingress, ProjectAssetKind.Geometry, "Blender return",
                ProjectAssetIngress.Binary, replacementSubmeshCount: 1);

            var after = PartRow(vm).Children[0];
            Assert.NotSame(before, after);
            Assert.Equal(1, shell.ProjectChangedCalls);
            Assert.Equal(BindingKind.ProjectAsset, session.Slots("edit-long")
                .Single(state => state.Slot.Id == "slot-geometry").Binding.Kind);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task A_part_with_no_edits_previews_the_games_own_geometry()
    {
        var (vm, _, shell) = Page(Bare());
        var part = PartRow(vm);
        Assert.True(part.ShowsMeshPreview);

        await vm.LoadPreviewsAsync(part);

        Assert.Equal(1, shell.PartPreviewCalls);
        Assert.Equal(0, shell.EditPreviewCalls);
    }

    // ---- what a BORROWING card's picture is filed under ----
    //
    // Route: the tree build reaches BuildCard → AdoptThumb → ThumbKey for every card, and the key a card was
    // filed under is its own PreviewKey. A card that takes another slot's value names no file of its own, so
    // the only thing that can move its key is the source's answer.

    private static EditMapCardVm CardFor(EditPageVm vm, string editId, string slotId) =>
        PartRow(vm).Children.Single(node => node.EditDefinitionId == editId)
            .MapGroups.SelectMany(group => group.Cards).Single(card => card.Slot.SlotId == slotId);

    /// <summary>The cross-edit borrow with a second picture asset, so the source can be rebound onto another
    /// FILE while the borrower's own binding stays exactly where it was.</summary>
    private static AuthoredProject CrossEditBorrow()
    {
        var project = AuthoredEditFixtures.WithCrossEditBorrow();
        project.ProjectAssets.Add(new ProjectAsset
        {
            Id = "skin-other", Kind = ProjectAssetKind.Picture, Label = "Other",
            File = "textures/other.png",
        });
        return project;
    }

    [Fact]
    public void A_borrowing_cards_key_moves_when_its_source_rebinds()
    {
        var (vm, session, _) = Page(CrossEditBorrow(), s => s.Resolve = part => Installed(part));
        string before = CardFor(vm, "edit-short", "slot-short-base").PreviewKey;
        Assert.NotEqual("", before);

        session.ChooseProjectAsset("edit-long", "slot-owned", "skin-other");

        Assert.NotEqual(before, CardFor(vm, "edit-short", "slot-short-base").PreviewKey);
    }

    /// <summary>A card that binds its own file is filed under its slot and that file and nothing else: the
    /// borrowed tail is added only where there is a source to add.</summary>
    [Fact]
    public void A_direct_cards_key_is_its_slot_and_its_own_file()
    {
        var (vm, _, _) = Page(CrossEditBorrow(), s => s.Resolve = part => Installed(part));

        var card = CardFor(vm, "edit-long", "slot-owned");

        Assert.Equal("textures/skin.png", card.Slot.ProjectRelativeFile);
        Assert.Equal("slot-owned" + "\u0001" + "textures/skin.png", card.PreviewKey);
    }

    [Fact]
    public void A_rename_does_not_change_what_a_render_is_filed_under_and_a_rebind_does()
    {
        var (vm, _, _) = Page(TextureOnly(AuthoredEditFixtures.Saved()));
        var edit = PartRow(vm).Children[0];
        string meshKey = edit.PreviewKey;
        string thumbKey = edit.MapGroups[0].Cards[0].PreviewKey;
        Assert.NotEqual("", meshKey);
        Assert.NotEqual("", thumbKey);

        edit.EditLabel = "Long coat";
        vm.CommitRenameCommand.Execute(edit);

        // A rename redraws the tree. What each row's picture is OF has not changed, so neither has the key
        // the page hands it back by — nothing re-renders.
        var renamed = PartRow(vm).Children[0];
        Assert.Equal(meshKey, renamed.PreviewKey);
        Assert.Equal(thumbKey, renamed.MapGroups[0].Cards[0].PreviewKey);

        // A rebind is a different picture on the same slot, and reads as one.
        vm.RevertRampCommand.Execute(PartRow(vm).Children[0].MapGroups[0].Cards[0]);
        Assert.NotEqual(thumbKey, PartRow(vm).Children[0].MapGroups[0].Cards[0].PreviewKey);
    }

    // ---- what a FAILED read of a part's maps says, and what re-selecting the row does about it ----

    /// <summary>A read that threw is its own state. It used to settle as "this part isn't in the current
    /// game files" — a claim about the install, kept for the session with no retry and no way out named —
    /// where all that happened was that the game had the bundle open. The card area says the failure, in
    /// the mesh preview's own words for the same cause, and coming back to the row asks again.</summary>
    [Fact]
    public async Task A_failed_read_of_a_parts_maps_says_so_and_the_next_visit_asks_again()
    {
        bool broken = true;
        var (vm, _, shell) = Page(Bare(), s => s.Resolve = part => broken
            ? throw new IOException("the game is holding this bundle")
            : Installed(part));
        for (int i = 0; i < 200 && PartRow(vm).OriginalsNote is null; i++) await Task.Delay(5);

        Assert.Equal(EditNodeVm.OriginalsReadFailed, PartRow(vm).OriginalsNote);
        Assert.NotEqual(EditNodeVm.OriginalsNotInstalled, PartRow(vm).OriginalsNote);
        Assert.False(PartRow(vm).IsReadingOriginals);

        // A redraw does not ask again: the failure is settled, or every redraw would fail and redraw.
        int asked = shell.AsyncResolveCalls;
        vm.Rebuild();
        Assert.Equal(asked, shell.AsyncResolveCalls);

        // Neither does the rebuild's own restore of the selection, which is what the retry's failure would
        // otherwise walk into.
        vm.SelectedNode = PartRow(vm);
        for (int i = 0; i < 200 && PartRow(vm).OriginalsNote is null; i++) await Task.Delay(5);
        Assert.Equal(EditNodeVm.OriginalsReadFailed, PartRow(vm).OriginalsNote);
        Assert.True(shell.AsyncResolveCalls > asked);   // the arrival itself asked once

        // …and once the game lets go, coming back to the row shows the maps.
        broken = false;
        asked = shell.AsyncResolveCalls;
        vm.SelectedNode = Subject(vm);
        vm.SelectedNode = PartRow(vm);
        for (int i = 0; i < 200 && PartRow(vm).MapGroups.Count == 0; i++) await Task.Delay(5);
        Assert.True(shell.AsyncResolveCalls > asked);
        Assert.Null(PartRow(vm).OriginalsNote);
        Assert.NotEmpty(PartRow(vm).MapGroups);
    }

    /// <summary>A part the install ANSWERED without is still the settled sentence it always was, and is not
    /// re-read on every visit: that answer is an answer, and re-asking it costs a bundle read a row.</summary>
    [Fact]
    public async Task A_part_the_game_files_do_not_have_is_not_re_read_when_the_row_is_selected_again()
    {
        var (vm, _, shell) = Page(Bare(), s => s.Resolve = _ => null);
        for (int i = 0; i < 200 && PartRow(vm).OriginalsNote is null; i++) await Task.Delay(5);
        Assert.Equal(EditNodeVm.OriginalsNotInstalled, PartRow(vm).OriginalsNote);

        int asked = shell.AsyncResolveCalls;
        vm.SelectedNode = Subject(vm);
        vm.SelectedNode = PartRow(vm);

        Assert.Equal(asked, shell.AsyncResolveCalls);
        Assert.Equal(EditNodeVm.OriginalsNotInstalled, PartRow(vm).OriginalsNote);
    }

    /// <summary>A drop while that read is still running answers with the app's own wait sentence. It used to
    /// clear the standing line and say nothing at all, which on a page whose last line was about something
    /// else reads as a drop that was taken.</summary>
    [Fact]
    public async Task A_drop_on_a_part_still_being_read_answers_with_the_wait_line()
    {
        var hold = new TaskCompletionSource<LegacyResolvedPart?>();
        var (vm, _, _) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.ResolveHold = hold;
        });
        vm.SelectedNode = PartRow(vm);
        Assert.True(PartRow(vm).IsReadingOriginals);
        vm.Status = "Ready.";

        await vm.HandleDropAsync(new[] { @"C:\in\dropped.png" }, null);

        Assert.Equal(GameFilesGate.SubjectReading, vm.Status);
        hold.SetResult(null);
    }

    // ---- naming the place a picture lands ----

    /// <summary>The card hover names the one file type the secondary drop gesture takes. The primary Open
    /// control and the section heading describe their own actions instead.</summary>
    [Fact]
    public async Task The_drop_hover_names_the_file_type_without_making_drag_the_primary_instruction()
    {
        var (vm, _, _) = Page(Bare(), s => s.Resolve = part => Installed(part));
        var card = PartRow(vm).MapGroups[0].Cards
            .Single(candidate => candidate.Slot.Input == TargetInputKind.BaseColor);

        Assert.Contains(".png", card.DropHint);
        Assert.DoesNotContain(".png", card.OpenHint);
        Assert.Equal("Original maps", PartRow(vm).OriginalMapsLabel);

        await vm.HandleDropAsync(new[] { @"C:\in\photo.jpg" }, card);

        Assert.Equal("Only a .png can replace a map.", vm.Status);
    }

    // ---- the subject a sentence names ----

    /// <summary>A hop that misses names the subject the way its row would have been named — through the
    /// shell's one naming home — rather than by the internal character key nobody chose or reads.</summary>
    [Fact]
    public void A_missed_subject_hop_names_the_subject_the_way_the_row_does()
    {
        var (vm, shell) = On(Picked(), s => s.Label = (subject, _) => "Friendly " + subject);

        vm.SelectSubject("Ghost", "GhostSSR01");

        Assert.Equal($"{shell.SubjectLabel("Ghost", "GhostSSR01")} isn't in this list.", vm.Status);
        Assert.DoesNotContain("GhostSSR01", vm.Status);
    }

    /// <summary>The internal outfit stem is off the subject row — except where two rows of this tree would
    /// otherwise read alike, which is the whole of what it was doing for the reader.</summary>
    [Fact]
    public void The_outfit_stem_stays_on_a_subject_row_only_where_it_tells_two_rows_apart()
    {
        var twins = new AuthoredEditSession(new AuthoredProject());
        twins.SetWorkspaceIndex(new AuthoredWorkspaceIndex
        {
            Selection =
            {
                new SelectionEntry { Character = "Vesna", Outfit = "VesnaSSR01" },
                new SelectionEntry { Character = "Vesna", Outfit = "VesnaDorm01" },
                new SelectionEntry { Character = "Aster", Outfit = "AsterSSR01" },
            },
        });
        var (vm, _) = On(twins, s => s.Label = (subject, _) => subject);

        var rows = vm.Nodes.ToList();
        Assert.Equal(3, rows.Count);
        // The two the label cannot tell apart keep it; the one it can does not.
        Assert.StartsWith("VesnaSSR01", rows[0].Detail);
        Assert.StartsWith("VesnaDorm01", rows[1].Detail);
        Assert.DoesNotContain("AsterSSR01", rows[2].Detail);
        Assert.Equal(rows[0].Detail, rows[0].InspectorDetail);
    }

    // ---- the shading row waits on its edit, and says why ----

    /// <summary>The three shading buttons are on the same gate every other verb of this page is: a verb
    /// running on this edit turns them off, their hovers say why, and a click that beats the redraw is
    /// refused rather than opening a dialog onto a model something else is moving.</summary>
    [Fact]
    public async Task The_shading_rows_verbs_wait_on_the_edit_the_page_is_already_working_on()
    {
        var (vm, _, shell) = Page(TextureOnly(), s =>
        {
            s.Resolve = part => Installed(part);
            s.BlenderHold = new TaskCompletionSource();
        });
        var editRow = PartRow(vm).Children.Single(node => node.EditDefinitionId == "edit-long");
        var open = vm.OpenInBlenderCommand.ExecuteAsync(editRow);

        var busy = ShadingRow(vm, "edit-long");
        Assert.True(busy.IsBusy);
        Assert.False(busy.CanRevert);
        Assert.Equal(BlenderGate.Busy, busy.CopyFromMaterialHint);
        Assert.Equal(BlenderGate.Busy, busy.EditValuesHint);
        // Revert has nothing to revert on this row, and says THAT rather than the wait — the map card's
        // own ordering: a line promising "try again" is never shown for a click that will never work.
        Assert.Equal(EditMapCardVm.NothingToRevert, busy.RevertHint);

        await vm.EditShadingValuesCommand.ExecuteAsync(busy);
        Assert.Null(shell.LastShadingAuthored);   // no dialog was opened
        Assert.Equal(BlenderGate.Busy, vm.Status);

        shell.BlenderHold!.SetResult();
        await open;

        var free = ShadingRow(vm, "edit-long");
        Assert.False(free.IsBusy);
        Assert.NotEqual(BlenderGate.Busy, free.EditValuesHint);
        Assert.NotEqual(BlenderGate.Busy, free.CopyFromMaterialHint);
        // Nothing is set on this row, so Revert stays off either way.
        Assert.False(free.CanRevert);
        Assert.Equal(EditMapCardVm.NothingToRevert, free.RevertHint);
    }

    /// <summary>＋ New edit and Hide are disabled by the same gate as their four neighbours, so they answer
    /// the same question on hover: why they are off, not what they would do.</summary>
    [Fact]
    public async Task The_part_rows_two_minting_verbs_say_why_they_are_off()
    {
        var (vm, _, shell) = Page(Bare(), s =>
        {
            s.Resolve = part => Installed(part);
            s.BlenderHold = new TaskCompletionSource();
        });
        Assert.NotEqual(BlenderGate.Busy, PartRow(vm).NewEditHint);

        var open = vm.OpenInBlenderCommand.ExecuteAsync(PartRow(vm));

        Assert.True(PartRow(vm).IsBusy);
        Assert.Equal(BlenderGate.Busy, PartRow(vm).NewEditHint);
        Assert.Equal(BlenderGate.Busy, PartRow(vm).HidePartHint);

        shell.BlenderHold!.SetResult();
        await open;

        Assert.False(PartRow(vm).IsBusy);
        Assert.NotEqual(BlenderGate.Busy, PartRow(vm).NewEditHint);
    }

    // ---- work started somewhere else, said in the page's own convention ----

    /// <summary>A Blender return is started by a send landing rather than by a click here, and while it runs
    /// it changes exactly the rows a subject's own Open would. It takes that same gate, so the ◌ and the
    /// waiting buttons mean one thing — and it gives back only what it took, so a verb's own gate underneath
    /// it survives.</summary>
    [Fact]
    public async Task A_hold_taken_from_outside_the_page_marks_the_subject_the_way_its_own_verbs_do()
    {
        var (vm, _, shell) = Page(AuthoredEditFixtures.Golden(), s =>
        {
            s.Token = _ => "body";
            s.SubjectHold = new TaskCompletionSource();
        });
        Assert.False(Subject(vm).IsBusy);

        var held = vm.HoldSubjects(new[] { (Body.Subject, Body.Outfit) });

        Assert.True(Subject(vm).IsBusy);
        Assert.Equal(BlenderGate.Busy, Subject(vm).OpenAllHint);
        Assert.Equal(BlenderGate.Busy, Subject(vm).OpenAllFirstEditHint);
        // Everything under the subject waits on it, exactly as it does under the subject's own Open.
        Assert.True(PartRow(vm).IsBusy);
        Assert.False(PartRow(vm).CanOpenInBlender);

        // The page's own verb on that subject is refused while the return is changing it, in the same
        // words a second click on any of these verbs gets.
        await vm.OpenSubjectInBlenderCommand.ExecuteAsync(Subject(vm));
        Assert.Empty(shell.SubjectVerbs);
        Assert.Equal(BlenderGate.Busy, vm.Status);

        // A second hold over the same subject takes nothing, so releasing it releases nothing: only what a
        // hold actually took comes back, or one return ending would clear another's gate.
        var second = vm.HoldSubjects(new[] { (Body.Subject, Body.Outfit) });
        second.Dispose();
        Assert.True(Subject(vm).IsBusy);

        held.Dispose();
        Assert.False(Subject(vm).IsBusy);
        Assert.True(PartRow(vm).CanOpenInBlender);
    }

}
