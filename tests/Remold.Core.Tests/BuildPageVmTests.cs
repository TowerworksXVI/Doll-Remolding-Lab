using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Media.Imaging;
using Remold.App.ViewModels;
using Remold.App.ViewModels.BuildPage;
using Remold.App.ViewModels.EditPage;
using Remold.App.Views;
using Remold.Core.Migoto;
using Remold.Core.Project;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>The ③ Build page over its imperative shell. Projects enter through the validated authored
/// session, so these facts pin the edit-first drawing and the exact session route each page gesture takes.
/// Disk publication, image decoding and dialogs are recorder answers here.</summary>
public class BuildPageVmTests
{
    private sealed class FakeShell : IBuildPageShell
    {
        public AuthoredEditSession? Session;
        public BuildPlanningResult Planning = new(new AuthoredBuildPlan());
        public Func<AuthoredProject?, CancellationToken, BuildPlanningResult>? Plan;
        public BuildLoaderState Loader = UsableLoader;
        public BuildPreviewState? PreviewOverride;
        public TaskCompletionSource? RunHold;
        public TaskCompletionSource? InstallHold;
        public BuildRunResult? RunResult;
        public IReadOnlyList<string> RunWarnings = Array.Empty<string>();
        public IReadOnlyList<string> RunInfos = Array.Empty<string>();
        public BuildInstallResult InstallResult = new(true, false,
            "Installed test-mod to the Mods folder.", @"C:\3dmigoto\Mods\test-mod");
        public int PlanCalls;
        public int RunCalls;
        public int InstallCalls;
        public int LoaderPickCalls;
        public int PreviewSetCalls;
        public int PreviewRemoveCalls;
        public bool ConfirmResult = true;
        public string LastConfirmTitle = "";
        public string LastConfirmBody = "";
        public string LastConfirmLabel = "";
        public bool LastConfirmDangerous;
        public EditRef? LastEditHop;
        public List<long> ChangedRevisions { get; } = new();
        public string? WholeModKey { get; set; }

        public BuildPlanningResult PlanBuild(AuthoredProject? project, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PlanCalls);
            return Plan?.Invoke(project, cancellationToken) ?? Planning;
        }

        public BuildLoaderState LoaderState() => Loader;
        public string SubjectLabel(string subject, string outfit) => $"{subject} · {outfit}";
        public string PartToken(TargetPart part) => part.RendererSlot.Split('_')[2];

        public async Task<BuildRunResult> RunBuildAsync(IProgress<string> progress)
        {
            RunCalls++;
            progress.Report("Building…");
            if (RunHold is not null) await RunHold.Task;
            if (RunResult is not null) return RunResult;
            var preview = ReadPreview(Session?.Snapshot());
            return new BuildRunResult(true, null, @"C:\published\test-mod", @"C:\published\test-mod.zip",
                "test-mod", @"C:\published\test-mod.build.log", RunWarnings, RunInfos,
                Session?.Revision ?? -1, preview.Stamp);
        }

        public async Task<BuildInstallResult> InstallBuildAsync(string builtDir, string package)
        {
            InstallCalls++;
            if (InstallHold is not null) await InstallHold.Task;
            return InstallResult;
        }

        public Task ChooseLoaderAsync() { LoaderPickCalls++; return Task.CompletedTask; }
        public Exception? OpenFailure;
        public void OpenArtifact(BuildArtifactKind kind, string path)
        {
            if (OpenFailure is not null) throw OpenFailure;
        }

        public BuildPreviewState ReadPreview(AuthoredProject? project)
        {
            if (PreviewOverride is { } preview) return preview;
            return project?.Info.Preview is { } relative
                ? new BuildPreviewState(relative, @"C:\mod\" + relative, false, "stamp:" + relative)
                : new BuildPreviewState(null, null, false, "none");
        }

        public Task<Bitmap?> LoadPreviewAsync(string path, int decodeWidth) => Task.FromResult<Bitmap?>(null);
        public Task<string?> PickPreviewAsync() => Task.FromResult<string?>(null);

        public void SetPreviewFrom(AuthoredEditSession session, string sourceFile)
        {
            PreviewSetCalls++;
            session.SetPreview("preview" + Path.GetExtension(sourceFile).ToLowerInvariant());
        }

        public void RemovePreviewFile(AuthoredEditSession session, BuildPreviewState preview)
        {
            PreviewRemoveCalls++;
            session.SetPreview(null);
        }

        public Task<bool> ConfirmAsync(string title, string body, string confirmLabel, bool dangerous = false)
        {
            LastConfirmTitle = title;
            LastConfirmBody = body;
            LastConfirmLabel = confirmLabel;
            LastConfirmDangerous = dangerous;
            return Task.FromResult(ConfirmResult);
        }

        public void GoToEdit(EditRef edit) => LastEditHop = edit;
        public void ProjectChanged(long revision)
        {
            lock (ChangedRevisions) ChangedRevisions.Add(revision);
        }
    }

    private static readonly BuildLoaderState UsableLoader = new(@"C:\3dmigoto\3DMigoto Loader.exe",
        true, @"C:\3dmigoto\Mods", new MigotoIniFacts(true, true, true));

    private static async Task<(BuildPageVm Vm, AuthoredEditSession Session, FakeShell Shell)> Page(
        AuthoredProject project, Action<FakeShell>? arrange = null)
    {
        var session = new AuthoredEditSession(project);
        var shell = new FakeShell { Session = session };
        arrange?.Invoke(shell);
        var vm = new BuildPageVm(shell);
        vm.Load(session);
        await vm.ReplanAsync();
        return (vm, session, shell);
    }

    // ---- the invalidation matrix, ③'s half: which changes the plan is re-derived from ----
    //
    // The planner runs off the UI thread, so neither its call counter nor the settled IsPlanning flag can be
    // read at a fixed moment — a fast plan is finished before the committing call even returns. What IS
    // exact is ENTRY: ReplanAsync raises IsPlanning before its first await, so counting that notification
    // counts plans started, whenever each one finishes.

    [Theory]
    [MemberData(nameof(InvalidationCases.Names), MemberType = typeof(InvalidationCases))]
    public async Task Only_a_change_the_build_plan_is_derived_from_replans(string name)
    {
        var scenario = InvalidationCases.Named(name);
        string root = Path.Combine(Path.GetTempPath(), "remold-invalidation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var session = InvalidationCases.Session(root);
            scenario.Arrange?.Invoke(session, root);
            var shell = new FakeShell { Session = session };
            var vm = new BuildPageVm(shell);
            vm.Load(session);
            await vm.ReplanAsync();
            int replans = 0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BuildPageVm.IsPlanning) && vm.IsPlanning) replans++;
            };
            long revision = session.Revision;

            scenario.Act(session, root);

            Assert.True(session.Revision > revision, $"'{name}' committed no change");
            Assert.Equal(scenario.Replans ? 1 : 0, replans);
            // The change still reached the page either way: only the plan is gated, never the board.
            Assert.Contains(session.Revision, shell.ChangedRevisions);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    // ---- the same gate under a re-entrant commit, which the matrix above cannot stage ----
    //
    // ② is subscriber #1 in the shipped window and its handler autosaves, which writes the identity form
    // back through SetIdentity — so a newer identity-only revision routinely lands INSIDE the raise for a
    // plan-affecting one, and ③ meets it first. Identity is the single answer the gate says no to, so the
    // change that did move the plan arrives already superseded.
    //
    // Plans are counted at the planner rather than at IsPlanning here: two replans started back to back
    // never raise the flag twice, and what these pin is how many the page asked for.

    /// <summary>The plan is re-derived for a change that moved it even when the page meets a newer revision
    /// first — otherwise the readiness verdict, the issue marks and the Build gate all stay on screen
    /// answering for a project that has moved, with nothing on the page saying so.</summary>
    [Fact]
    public async Task A_plan_affecting_change_still_replans_when_a_reentrant_commit_arrives_first()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        bool nested = false;
        session.Changed += (_, _) =>
        {
            if (nested) return;
            nested = true;
            // What ②'s handler lands through the autosave: the identity form, and nothing else.
            session.SetIdentity("Golden", "1.0", null, "Written back by the autosave.", null, true,
                null, null);
        };
        var (_, shell) = await Reentrant(session);
        int before = shell.PlanCalls;

        session.RenameEdit("edit-long", "Renamed");

        Assert.True(nested, "the re-entrant commit never happened");
        // Only the identity revision applied on the ordinary route: the rename is the superseded one.
        Assert.Equal(new[] { session.Revision }, shell.ChangedRevisions);
        Assert.True(SpinWait.SpinUntil(() => shell.PlanCalls - before >= 1, TimeSpan.FromSeconds(5)),
            "the superseded rename left the plan where it was");
        Assert.Equal(1, shell.PlanCalls - before);
    }

    /// <summary>The control: a re-entrant commit that is itself the plan's business. Both changes ask for a
    /// plan in one notification burst and the planner reads the current snapshot, so they share one run.</summary>
    [Fact]
    public async Task Reentrant_plan_affecting_commits_coalesce_into_one_plan()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        bool nested = false;
        session.Changed += (_, _) =>
        {
            if (nested) return;
            nested = true;
            session.RenameEdit("edit-short", "Nested");
        };
        var (_, shell) = await Reentrant(session);
        int before = shell.PlanCalls;

        session.RenameEdit("edit-long", "Renamed");

        Assert.True(nested, "the re-entrant commit never happened");
        Assert.Equal(new[] { session.Revision }, shell.ChangedRevisions);
        Assert.True(SpinWait.SpinUntil(() => shell.PlanCalls - before >= 1, TimeSpan.FromSeconds(5)),
            "the coalesced plan never reached the planner");
        Assert.Equal(1, shell.PlanCalls - before);
    }

    /// <summary>A page settled on the session, with its coalesced opening plan proven to have reached the
    /// planner. Only then is a plan count a measurement of what the change did.</summary>
    private static async Task<(BuildPageVm Vm, FakeShell Shell)> Reentrant(AuthoredEditSession session)
    {
        var shell = new FakeShell { Session = session };
        var vm = new BuildPageVm(shell);
        vm.Load(session);
        await vm.ReplanAsync();
        Assert.True(SpinWait.SpinUntil(() => shell.PlanCalls >= 1, TimeSpan.FromSeconds(5)),
            "the page's opening plan never reached the planner");
        return (vm, shell);
    }

    [Fact]
    public async Task Replan_requests_in_one_burst_share_one_planner_run()
    {
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var shell = new FakeShell { Session = session };
        var vm = new BuildPageVm(shell);
        vm.Load(session);
        await vm.ReplanAsync();
        int before = shell.PlanCalls;

        Task first = vm.ReplanAsync();
        Task second = vm.ReplanAsync();
        await Task.WhenAll(first, second);

        Assert.Equal(1, shell.PlanCalls - before);
    }

    [Fact]
    public async Task Superseded_running_plan_receives_cancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var shell = new FakeShell { Session = session };
        shell.Plan = (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref call) != 1) return shell.Planning;
            entered.TrySetResult();
            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            if (cancellationToken.IsCancellationRequested) cancelled.TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();
            return shell.Planning;
        };
        var vm = new BuildPageVm(shell);

        vm.Load(session);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.ReplanAsync().WaitAsync(TimeSpan.FromSeconds(5));

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, shell.PlanCalls);
    }

    private static AuthoredProject Keyed(string? key = "F6")
    {
        var project = AuthoredEditFixtures.Golden();
        project.Always.Clear();
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-0001",
            Key = key,
            Label = "Body options",
            States =
            {
                new KeyGroupState { Id = "state-0001", ActiveEditIds = { "edit-long" } },
                new KeyGroupState { Id = "state-0002", ActiveEditIds = { "edit-short" } },
            },
        });
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }

    private static AuthoredProject TouchedByTwoGroups()
    {
        var project = AuthoredEditFixtures.Golden();
        project.Always.Clear();
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-0001", Key = "F6", Label = "First",
            States =
            {
                new KeyGroupState { Id = "state-0001", ActiveEditIds = { "edit-long" } },
                new KeyGroupState { Id = "state-0002" },
            },
        });
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-0002", Key = "F7", Label = "Second",
            States =
            {
                new KeyGroupState { Id = "state-0001" },
                new KeyGroupState { Id = "state-0002", ActiveEditIds = { "edit-long" } },
            },
        });
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }

    private static (AuthoredProject Project, AuthoredBuildPlan Plan) CompositionFixture()
    {
        var project = AuthoredEditFixtures.MultiPart();
        project.Always.Clear();
        project.Always.Add("edit-long");
        string hide = project.Hide(AuthoredEditFixtures.Body);
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-body", Key = "F6", Label = "Body options", States =
            {
                new KeyGroupState
                {
                    Id = "body-mixed", ActiveEditIds = { "edit-long", hide },
                },
                new KeyGroupState { Id = "body-clear" },
            },
        });
        project.KeyGroups.Add(new KeyGroup
        {
            Id = "key-hair", Key = "F7", Label = "Hair color", States =
            {
                new KeyGroupState { Id = "hair-on", ActiveEditIds = { "edit-hair" } },
                new KeyGroupState { Id = "hair-off" },
            },
        });
        Assert.Empty(AuthoredProjectValidator.Errors(project));

        var bodyLocal = new PlanCondition("key-body", "F6", 0, 2, 0);
        var hairLocal = new PlanCondition("key-hair", "F7", 0, 2, 0);
        var body = CompositionPart(AuthoredEditFixtures.Body,
            CompositionOperation(PlannedPartDisposition.Edit, "edit-long", PlanCondition.Always, bodyLocal),
            CompositionOperation(PlannedPartDisposition.Hidden, hide, bodyLocal));
        var hair = CompositionPart(AuthoredEditFixtures.Hair,
            CompositionOperation(PlannedPartDisposition.Edit, "edit-hair", hairLocal));
        return (project, new AuthoredBuildPlan { Parts = new[] { body, hair } });
    }

    private static PlannedPartOperation CompositionOperation(PlannedPartDisposition disposition,
        string editId, params PlanCondition[] activeWhen) => new(activeWhen[0], disposition, editId, null,
        Array.Empty<PlannedBinding>(), activeWhen);

    private static PlannedPart CompositionPart(TargetPart target, params PlannedPartOperation[] operations) =>
        new(target, operations.Any(operation => operation.Disposition == PlannedPartDisposition.Edit)
                ? PlannedPartDisposition.Edit : PlannedPartDisposition.Hidden,
            operations[0].EditDefinitionId, null, operations,
            operations.Where(operation => operation.Disposition == PlannedPartDisposition.Hidden)
                .SelectMany(operation => operation.ActiveWhen).ToArray(),
            null, null, Array.Empty<PlannedBinding>(), Array.Empty<PlannedGroupTouch>());

    private static BuildEditRowVm Edit(BuildPageVm vm, string id) => vm.Subjects.SelectMany(row => row.Parts)
        .SelectMany(row => row.Edits).Single(row => row.EditDefinitionId == id);

    private static int MarkedCardCount(BuildPageVm vm) => (vm.Always.IsMarked ? 1 : 0)
        + vm.Groups.Count(group => group.IsMarked)
        + vm.Groups.SelectMany(group => group.States).Count(state => state.IsMarked);

    /// <summary>The plan's own record of which edits each line is about — what the page marks its rows
    /// from, in place of matching an edit name against the text.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Owners(
        params (string Line, string[] EditIds)[] rows) =>
        rows.ToDictionary(row => row.Line, row => (IReadOnlyList<string>)row.EditIds,
            StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> GroupOwners(
        params (string Line, string[] GroupIds)[] rows) =>
        rows.ToDictionary(row => row.Line, row => (IReadOnlyList<string>)row.GroupIds,
            StringComparer.Ordinal);

    [Fact]
    public void The_strip_source_is_one_fixed_three_row_surface_in_every_state()
    {
        string path = Path.Combine(SourceHygieneTests.RepoRoot(), "src", "Remold.App", "Views",
            "BuildPageView.axaml");
        var document = XDocument.Load(path);
        XNamespace ui = "https://github.com/avaloniaui";
        var strip = Assert.Single(document.Descendants(ui + "Border"), element =>
            (string?)element.Attribute("Height") == "112");
        var rows = Assert.Single(strip.Elements(ui + "Grid"));

        Assert.Equal("34,21,37", (string?)rows.Attribute("RowDefinitions"));
        Assert.Single(document.Descendants(ui + "Flyout"), element =>
            (string?)element.Attribute("Placement") == "TopEdgeAlignedRight");
    }

    [Fact]
    public void Diagnostics_fit_the_viewport_and_give_wrapped_sentences_the_flexible_row()
    {
        string app = Path.Combine(SourceHygieneTests.RepoRoot(), "src", "Remold.App");
        var document = XDocument.Load(Path.Combine(app, "Views", "BuildPageView.axaml"));
        XNamespace ui = "https://github.com/avaloniaui";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var template = Assert.Single(document.Descendants(ui + "DataTemplate"), element =>
            (string?)element.Attribute(x + "Key") == "DiagnosticIssueTemplate");
        var row = Assert.Single(template.Elements(ui + "Grid"));
        var sentence = Assert.Single(row.Elements(ui + "TextBlock"));
        var chips = Assert.Single(row.Elements(ui + "ItemsControl"));
        var flyout = Assert.Single(document.Descendants(ui + "Flyout"), element =>
            (string?)element.Attribute("Placement") == "TopEdgeAlignedRight");
        var viewer = Assert.Single(flyout.Descendants(ui + "ScrollViewer"));

        Assert.Equal("Auto,Auto", (string?)row.Attribute("RowDefinitions"));
        Assert.Equal("Wrap", (string?)sentence.Attribute("TextWrapping"));
        Assert.Equal("1", (string?)chips.Attribute("Grid.Row"));
        Assert.Equal("Left", (string?)chips.Attribute("HorizontalAlignment"));
        Assert.Equal("640", (string?)chips.Attribute("MaxWidth"));
        Assert.Null((string?)viewer.Attribute("Width"));
        Assert.Equal("Disabled", (string?)viewer.Attribute("HorizontalScrollBarVisibility"));
    }

    [Fact]
    public void Editable_by_others_copy_matches_the_owner_ruling()
    {
        string app = Path.Combine(SourceHygieneTests.RepoRoot(), "src", "Remold.App");
        var document = XDocument.Load(Path.Combine(app, "Views", "BuildPageView.axaml"));
        XNamespace ui = "https://github.com/avaloniaui";
        var checkbox = Assert.Single(document.Descendants(ui + "CheckBox"), element =>
            (string?)element.Attribute("Content") == "Editable by others");

        Assert.Equal("Adds the file a later Doll Remolding Lab version will use to open this mod for editing "
                + "and repair. Without it, the mod can only be rebuilt from your project.",
            (string?)checkbox.Attribute("ToolTip.Tip"));
        Assert.Contains("Content = \"New mods are editable by others\"",
            File.ReadAllText(Path.Combine(app, "Views", "SettingsWindow.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_board_card_kind_binds_the_focused_accent()
    {
        string path = Path.Combine(SourceHygieneTests.RepoRoot(), "src", "Remold.App", "Views",
            "BuildPageView.axaml");
        var document = XDocument.Load(path);
        XNamespace ui = "https://github.com/avaloniaui";

        Assert.Equal(3, document.Descendants(ui + "Border").Count(element =>
            (string?)element.Attribute("Classes.focused") == "{Binding IsMarked}"));
        Assert.Single(document.Descendants(ui + "Style"), element =>
            (string?)element.Attribute("Selector") == "Border.buildCard.focused");
    }

    [Fact]
    public void Board_input_routes_clear_marks_through_handled_child_events()
    {
        string source = File.ReadAllText(Path.Combine(SourceHygieneTests.RepoRoot(), "src", "Remold.App",
            "Views", "BuildPageView.axaml.cs"));

        Assert.Contains("AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble, "
            + "handledEventsToo: true);", source, StringComparison.Ordinal);
        string pointerRegistration = source[source.IndexOf(
            "AddHandler(PointerPressedEvent, OnBoardPointerPressed", StringComparison.Ordinal)..];
        pointerRegistration = pointerRegistration[..(pointerRegistration.IndexOf(");", StringComparison.Ordinal) + 2)];
        Assert.Contains("handledEventsToo: true", pointerRegistration, StringComparison.Ordinal);
        Assert.Contains("AddHandler(KeyDownEvent, OnBoardKeyDown, RoutingStrategies.Bubble, "
            + "handledEventsToo: true);", source, StringComparison.Ordinal);
        Assert.Contains("ClearMarkForBoardInput(e.Source);", source, StringComparison.Ordinal);
        Assert.Contains("if (OverNamedZone(e.Source, BehaviorBoardName)) page.ClearMarkedTarget();", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Library_and_board_draw_every_placement_from_the_edit_first_outline()
    {
        var (vm, _, _) = await Page(TouchedByTwoGroups());

        Assert.Equal(2, vm.Subjects.Single().Parts.Single().Edits.Count);
        Assert.Equal(2, vm.Groups.Count);
        Assert.Equal("edit-long", Assert.Single(vm.Groups[0].States[0].Tokens).EditDefinitionId);
        Assert.Equal("edit-long", Assert.Single(vm.Groups[1].States[1].Tokens).EditDefinitionId);
        Assert.Equal(2, Edit(vm, "edit-long").UseIn.Count(choice => choice.IsPlaced));
        Assert.Equal(new[] { "F6 · 1", "F7 · 2" }, Edit(vm, "edit-long").PlacementChips);
    }

    /// <summary>An edit nothing selects says so in ② Edit's own words. Two names for one fact leave the
    /// modder deciding whether they mean the same thing.</summary>
    [Fact]
    public async Task An_unused_edit_wears_the_same_words_both_pages_use()
    {
        var (vm, _, _) = await Page(AuthoredEditFixtures.Golden());

        Assert.Equal(new[] { BuildPageVm.NotUsedChip }, Edit(vm, "edit-short").PlacementChips);
        Assert.StartsWith(BuildPageVm.NotUsedChip, EditNodeVm.NotUsedYet, StringComparison.Ordinal);
    }

    /// <summary>The ⚠ over the Edits list is about the rows under it. A warning that marks no row — a
    /// missing preview, a past run's line — is reported in Warnings and raises no glyph pointing at edits.
    /// </summary>
    [Fact]
    public async Task The_edits_glyph_answers_only_for_warnings_an_edit_wears()
    {
        var unattributed = new AuthoredBuildPlan { Warnings = new[] { "The mod folder moved." } };
        var (loose, _, _) = await Page(Keyed(),
            shell => shell.Planning = new BuildPlanningResult(unattributed));

        Assert.True(loose.HasWarnings);
        Assert.False(loose.EditsNeedAttention);

        const string warning = "Long body never draws.";
        var attributed = new AuthoredBuildPlan
        {
            Warnings = new[] { warning },
            IssueEditIds = Owners((warning, new[] { "edit-long" })),
        };
        var (marked, _, _) = await Page(Keyed(),
            shell => shell.Planning = new BuildPlanningResult(attributed));

        Assert.True(marked.EditsNeedAttention);
    }

    [Fact]
    public async Task Use_in_checklist_places_and_unplaces_without_moving_the_edit()
    {
        var (vm, session, _) = await Page(AuthoredEditFixtures.Golden());
        var unused = Edit(vm, "edit-short").UseIn.Single(choice => choice.GroupId is null);

        unused.IsPlaced = true;

        Assert.Contains("edit-short", session.Snapshot().Always);
        var placed = Edit(vm, "edit-short").UseIn.Single(choice => choice.GroupId is null);
        placed.IsPlaced = false;
        Assert.DoesNotContain("edit-short", session.Snapshot().Always);
        Assert.Equal(2, session.Snapshot().EditDefinitions.Count);
    }

    [Fact]
    public async Task New_key_moves_an_Always_placement_into_its_first_state()
    {
        var (vm, session, _) = await Page(AuthoredEditFixtures.Golden());

        Edit(vm, "edit-long").MakeKeyCommand.Execute(null);

        var project = session.Snapshot();
        var group = Assert.Single(project.KeyGroups);
        Assert.Null(group.Key);
        Assert.DoesNotContain("edit-long", project.Always);
        Assert.Equal(new[] { "edit-long" }, group.States[0].ActiveEditIds);
        Assert.Empty(group.States[1].ActiveEditIds);
    }

    [Fact]
    public async Task Key_and_state_controls_route_clear_duplicate_reorder_and_remove_by_stable_id()
    {
        var (vm, session, _) = await Page(Keyed(key: null));
        vm.Groups.Single().Key = "F8";
        Assert.Equal("F8", session.Snapshot().KeyGroups.Single().Key);

        vm.Groups.Single().AddStateCommand.Execute(null);
        var afterDuplicate = session.Snapshot().KeyGroups.Single();
        Assert.Equal(3, afterDuplicate.States.Count);
        string duplicateId = afterDuplicate.States[2].Id;
        Assert.Equal(new[] { "edit-short" }, afterDuplicate.States[2].ActiveEditIds);

        vm.Groups.Single().States[2].MoveUpCommand.Execute(null);
        Assert.Equal(duplicateId, session.Snapshot().KeyGroups.Single().States[1].Id);
        vm.Groups.Single().States[1].RemoveCommand.Execute(null);
        Assert.Equal(2, session.Snapshot().KeyGroups.Single().States.Count);

        vm.Groups.Single().Key = null;
        Assert.Null(session.Snapshot().KeyGroups.Single().Key);
    }

    /// <summary>A refusal is worded for the person reading it, so it reaches the status line as it stands.
    /// The greyed remove button states the same sentence before the click.</summary>
    [Fact]
    public async Task Removing_one_of_two_states_keeps_the_core_refusal_wording_on_the_page()
    {
        var (vm, session, _) = await Page(Keyed());

        vm.Groups.Single().States[0].RemoveCommand.Execute(null);

        Assert.Equal(AuthoredEditSession.TwoStateFloor, vm.Status);
        Assert.Equal(vm.Status, vm.Groups.Single().States[0].RemoveTip);
        Assert.Equal(2, session.Snapshot().KeyGroups.Single().States.Count);
    }

    /// <summary>A defect is not a refusal. A row naming something the mod no longer has fails with the
    /// model's own text, key-group id and all, so the page says what it could not do instead.</summary>
    [Fact]
    public async Task A_failure_that_is_not_a_refusal_keeps_the_models_own_text_off_the_page()
    {
        var (vm, session, _) = await Page(Keyed());

        vm.DropEdit("edit-short", "key-9999", "state-0001");

        Assert.Equal($"Couldn't {BuildPageVm.ChangeAction}.", vm.Status);
        Assert.DoesNotContain("key-9999", vm.Status, StringComparison.Ordinal);
        Assert.Equal(new[] { "edit-long" },
            session.Snapshot().KeyGroups.Single().States[0].ActiveEditIds);
    }

    [Fact]
    public async Task Dropping_content_on_a_keyless_state_replaces_that_parts_previous_content()
    {
        var (vm, session, _) = await Page(Keyed(key: null));
        var state = vm.Groups.Single().States[0];
        var replacement = state.AvailableEdits.Single(choice => choice.EditDefinitionId == "edit-short");

        replacement.AddCommand.Execute(null);

        var active = session.Snapshot().KeyGroups.Single(group => group.Id == "key-0001")
            .States.Single(row => row.Id == "state-0001").ActiveEditIds;
        Assert.Equal(new[] { "edit-short" }, active);
        Assert.Equal("Body options", vm.Groups.Single().DisplayName);
    }

    /// <summary>The board mints a part's hide once, then places it by the verb that places every edit — so
    /// asking again refuses in the ordinary words instead of silently succeeding, which is the one behaviour
    /// no content edit has.</summary>
    [Fact]
    public async Task Board_hide_mints_once_and_a_repeat_refuses_like_any_other_edit()
    {
        var (vm, session, _) = await Page(Keyed());
        var state = vm.Groups.Single().States[0];
        var hide = state.AvailableEdits.Single(choice => choice.Kind == EditDefinitionKind.Hide);

        hide.AddCommand.Execute(null);
        hide.AddCommand.Execute(null);

        var project = session.Snapshot();
        string hideId = Assert.Single(project.EditDefinitions,
            edit => edit.Kind == EditDefinitionKind.Hide).Id;
        Assert.Equal(1, project.KeyGroups.Single().States[0].ActiveEditIds.Count(id => id == hideId));
        Assert.Equal("Hidden is already used in F6 · State 1.", vm.Status);
    }

    // ---- the board's gestures: what a drag and a drop actually do to the session ----

    /// <summary>A group whose two states are empty, so a tick or a drop is the only thing that puts
    /// anything in them.</summary>
    private static AuthoredProject EmptyStates()
    {
        var project = Keyed();
        foreach (var state in project.KeyGroups.Single().States) state.ActiveEditIds.Clear();
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }

    /// <summary>One edit in Always beside a keyed group with an empty second state.</summary>
    private static AuthoredProject AlwaysBesideStates()
    {
        var project = EmptyStates();
        project.Always.Add("edit-short");
        Assert.Empty(AuthoredProjectValidator.Errors(project));
        return project;
    }

    /// <summary>A token dragged to another state MOVES: the state it came from no longer uses the edit.
    /// The library row is the copy; a token is the use itself.</summary>
    [Fact]
    public async Task Dragging_a_token_to_another_state_takes_the_use_with_it()
    {
        var (vm, session, _) = await Page(Keyed());
        var token = vm.Groups.Single().States[0].Tokens.Single();

        vm.DropToken(token.EditDefinitionId, token.GroupId, token.StateId, token.GroupId, "state-0002");

        var states = session.Snapshot().KeyGroups.Single().States;
        Assert.Empty(states[0].ActiveEditIds);
        // One content answer per part per place: the edit already sitting there gave up its seat.
        Assert.Equal(new[] { "edit-long" }, states[1].ActiveEditIds);
        // The line names where it went and what it unseated: a replaced answer is not a silent loss.
        Assert.Equal("Moved Long body to F6 · State 2. Short body is no longer used there.", vm.Status);
    }

    [Fact]
    public async Task Dragging_a_token_onto_Always_takes_the_use_with_it()
    {
        var (vm, session, _) = await Page(Keyed());

        vm.DropToken("edit-long", "key-0001", "state-0001", null, null);

        var project = session.Snapshot();
        Assert.Equal(new[] { "edit-long" }, project.Always);
        Assert.Empty(project.KeyGroups.Single().States[0].ActiveEditIds);
    }

    [Fact]
    public async Task Dragging_a_token_off_Always_onto_a_state_takes_the_use_with_it()
    {
        var (vm, session, _) = await Page(AlwaysBesideStates());

        vm.DropToken("edit-short", null, null, "key-0001", "state-0002");

        var project = session.Snapshot();
        Assert.Empty(project.Always);
        Assert.Equal(new[] { "edit-short" }, project.KeyGroups.Single().States[1].ActiveEditIds);
    }

    /// <summary>A token dropped where the edit is already used refuses in the same words a library drop
    /// refuses in, and moves nothing.</summary>
    [Fact]
    public async Task A_token_dropped_where_the_edit_already_is_refuses_and_moves_nothing()
    {
        var (vm, session, _) = await Page(TouchedByTwoGroups());

        vm.DropToken("edit-long", "key-0001", "state-0001", "key-0002", "state-0002");

        Assert.Equal("Long body is already there.", vm.Status);
        Assert.Equal(new[] { "edit-long" }, session.Snapshot().KeyGroups[0].States[0].ActiveEditIds);
    }

    [Fact]
    public async Task A_token_dropped_back_where_it_came_from_changes_nothing()
    {
        var (vm, session, _) = await Page(Keyed());
        long revision = session.Revision;

        vm.DropToken("edit-long", "key-0001", "state-0001", "key-0001", "state-0001");

        Assert.Equal(revision, session.Revision);
        Assert.Equal("", vm.Status);
    }

    /// <summary>A drag is one authored change, so the page redraws once and the file is written once. Seat
    /// and move as two commits fire the whole cascade twice, with the incumbent gone and the dragged edit
    /// not yet arrived in between.</summary>
    [Fact]
    public async Task A_token_drag_commits_one_change()
    {
        var (vm, session, _) = await Page(Keyed());
        int changes = 0;
        session.Changed += (_, _) => changes++;

        vm.DropToken("edit-long", "key-0001", "state-0001", "key-0001", "state-0002");

        Assert.Equal(1, changes);
    }

    /// <summary>The same for a placement: one change, and the sentence names what it unseated.</summary>
    [Fact]
    public async Task A_placement_that_replaces_an_answer_commits_once_and_names_what_left()
    {
        var (vm, session, _) = await Page(Keyed());
        int changes = 0;
        session.Changed += (_, _) => changes++;

        vm.DropEdit("edit-short", "key-0001", "state-0001");

        Assert.Equal(1, changes);
        Assert.Equal(new[] { "edit-short" },
            session.Snapshot().KeyGroups.Single().States[0].ActiveEditIds);
        Assert.Equal("Added Short body to F6 · State 1. Long body is no longer used there.", vm.Status);
    }

    /// <summary>A placement that displaced nothing says nothing about displacement.</summary>
    [Fact]
    public async Task A_placement_into_an_empty_state_says_only_what_it_added()
    {
        var (vm, _, _) = await Page(EmptyStates());

        vm.DropEdit("edit-long", "key-0001", "state-0002");

        Assert.Equal("Added Long body to F6 · State 2.", vm.Status);
    }

    /// <summary>What the cursor asks while a drag is still in the air. An edit used in two states dragged
    /// onto the other one would only refuse, and the drag has to say so before the release.</summary>
    [Fact]
    public async Task The_page_answers_whether_an_edit_is_already_used_where_a_drag_is_hovering()
    {
        var (vm, _, _) = await Page(TouchedByTwoGroups());

        Assert.True(vm.IsUsedAt("edit-long", "key-0001", "state-0001"));
        Assert.True(vm.IsUsedAt("edit-long", "key-0002", "state-0002"));
        Assert.False(vm.IsUsedAt("edit-long", "key-0001", "state-0002"));
        Assert.False(vm.IsUsedAt("edit-long", null, null));
        Assert.False(vm.IsUsedAt("edit-short", "key-0001", "state-0001"));
    }

    /// <summary>Every surface names a place the same way, so a state the modder named is called by its
    /// name on the board's chips, its checklist and the line a placement leaves.</summary>
    [Fact]
    public async Task A_named_state_is_named_by_its_name_on_every_board_surface()
    {
        var (vm, session, _) = await Page(Keyed());
        session.RenameState("key-0001", "state-0002", "Coat off");

        Assert.Contains("F6 · Coat off",
            Edit(vm, "edit-long").UseIn.Select(choice => choice.Label));
        vm.DropEdit("edit-long", "key-0001", "state-0002");
        Assert.Equal("Added Long body to F6 · Coat off. Short body is no longer used there.", vm.Status);
    }

    /// <summary>The board sleeps while a build runs: a tick that cannot land cannot leave a box ticked for
    /// an answer the mod never took.</summary>
    [Fact]
    public async Task The_board_and_library_are_shut_while_a_build_runs()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, _) = await Page(Keyed(), recorder => recorder.RunHold = hold);
        Assert.True(vm.BoardEnabled);

        Task run = vm.BuildCommand.ExecuteAsync(null);
        for (int i = 0; !vm.IsWorkInFlight && i < 100; i++) await Task.Yield();
        Assert.False(vm.BoardEnabled);

        hold.SetResult();
        await run;
        Assert.True(vm.BoardEnabled);
    }

    /// <summary>The library row stays a copy: dropping it somewhere new leaves every place it was already
    /// used exactly as it was.</summary>
    [Fact]
    public async Task A_library_drop_copies_and_keeps_the_places_the_edit_already_had()
    {
        var (vm, session, _) = await Page(Keyed());

        vm.DropEdit("edit-long", "key-0001", "state-0002");

        var states = session.Snapshot().KeyGroups.Single().States;
        Assert.Equal(new[] { "edit-long" }, states[0].ActiveEditIds);
        Assert.Equal(new[] { "edit-long" }, states[1].ActiveEditIds);
    }

    [Fact]
    public async Task A_library_drop_on_a_place_the_edit_already_has_refuses()
    {
        var (vm, session, _) = await Page(Keyed());

        vm.DropEdit("edit-long", "key-0001", "state-0001");

        Assert.Equal("Long body is already there.", vm.Status);
        Assert.Equal(new[] { "edit-long" },
            session.Snapshot().KeyGroups.Single().States[0].ActiveEditIds);
    }

    [Fact]
    public async Task Dropping_a_state_header_on_another_state_reorders_them()
    {
        var (vm, session, _) = await Page(Keyed());
        vm.Groups.Single().AddStateCommand.Execute(null);
        string third = session.Snapshot().KeyGroups.Single().States[2].Id;

        vm.DropState("key-0001", third, "state-0001");

        Assert.Equal(third, session.Snapshot().KeyGroups.Single().States[0].Id);
    }

    /// <summary>What a board drag writes is what a board drop reads. The text is the only thing that
    /// survives the platform, so a writer and a reader that disagree is a gesture that quietly does
    /// nothing.</summary>
    [Fact]
    public void A_board_drag_reads_back_exactly_what_it_wrote()
    {
        Assert.Equal(new BuildDragPayload(BuildDragKind.Edit, "edit-0001", null, null),
            BuildDragPayload.Read(BuildDragPayload.Edit("edit-0001")));
        Assert.Equal(new BuildDragPayload(BuildDragKind.State, "", "key-0001", "state-0002"),
            BuildDragPayload.Read(BuildDragPayload.State("key-0001", "state-0002")));
        Assert.Equal(new BuildDragPayload(BuildDragKind.Token, "edit-0001", "key-0001", "state-0002"),
            BuildDragPayload.Read(BuildDragPayload.Token("edit-0001", "key-0001", "state-0002")));
        // Always is the null address at both ends.
        Assert.Equal(new BuildDragPayload(BuildDragKind.Token, "edit-0001", null, null),
            BuildDragPayload.Read(BuildDragPayload.Token("edit-0001", null, null)));

        Assert.Null(BuildDragPayload.Read(null));
        Assert.Null(BuildDragPayload.Read("C:\\shots\\cover.png"));
        Assert.Null(BuildDragPayload.Read("drl-build-edit:"));
        Assert.Null(BuildDragPayload.Read("drl-build-token:edit-0001"));
    }

    /// <summary>One content answer per part per place, on the Always tick exactly as on a state's. Two
    /// content edits of one part that can be active together is a conflict the plan refuses the build for,
    /// so a checkbox must not be able to author one quietly.</summary>
    [Fact]
    public async Task Ticking_a_second_content_edit_into_Always_takes_the_first_ones_seat()
    {
        var (vm, session, _) = await Page(AuthoredEditFixtures.Golden());
        Assert.Equal(new[] { "edit-long" }, session.Snapshot().Always);

        Edit(vm, "edit-short").UseIn.Single(choice => choice.GroupId is null).IsPlaced = true;

        Assert.Equal(new[] { "edit-short" }, session.Snapshot().Always);
    }

    /// <summary>Two ticks in a row on one row's checklist both land, on the row the checklist is still
    /// attached to. Each tick commits a change and a change redraws the page: replacing the rows takes the
    /// open checklist away with the row that owned it, and the second tick has nothing left to land on.
    /// </summary>
    [Fact]
    public async Task Two_ticks_on_one_checklist_both_land_without_replacing_the_row()
    {
        var (vm, session, _) = await Page(EmptyStates());
        var row = Edit(vm, "edit-long");
        var first = row.UseIn.Single(choice => choice.StateId == "state-0001");
        var second = row.UseIn.Single(choice => choice.StateId == "state-0002");

        first.IsPlaced = true;
        second.IsPlaced = true;

        var states = session.Snapshot().KeyGroups.Single().States;
        Assert.Equal(new[] { "edit-long" }, states[0].ActiveEditIds);
        Assert.Equal(new[] { "edit-long" }, states[1].ActiveEditIds);
        // The row and its choices are the same objects the open checklist is bound to.
        Assert.Same(row, Edit(vm, "edit-long"));
        Assert.Same(first, row.UseIn.Single(choice => choice.StateId == "state-0001"));
        Assert.Same(second, row.UseIn.Single(choice => choice.StateId == "state-0002"));
        Assert.True(first.IsPlaced);
        Assert.True(second.IsPlaced);
    }

    /// <summary>A row that leaves the mod does go, and one that arrives is added where it belongs: keeping
    /// rows is not the same as never changing them.</summary>
    [Fact]
    public async Task A_deleted_edit_leaves_the_library_and_a_rename_reaches_the_row_that_stayed()
    {
        var (vm, session, _) = await Page(AuthoredEditFixtures.Golden());
        var kept = Edit(vm, "edit-long");

        session.DeleteEdit("edit-short");
        session.RenameEdit("edit-long", "Longer body");

        var row = Assert.Single(vm.Subjects.Single().Parts.Single().Edits);
        Assert.Same(kept, row);
        Assert.Equal("Longer body", row.Label);
    }

    /// <summary>A line the plan says is about one edit marks that edit and no other. Reading ownership off
    /// the text marks every edit whose name the line happens to contain, and two parts are free to carry
    /// edits named alike.</summary>
    [Fact]
    public async Task A_line_marks_the_edits_the_plan_names_not_the_ones_its_text_mentions()
    {
        const string warning = "Long body and Short body are alike. This one is about the short one.";
        var plan = new AuthoredBuildPlan
        {
            Warnings = new[] { warning },
            IssueEditIds = Owners((warning, new[] { "edit-short" })),
        };
        var (vm, _, _) = await Page(Keyed(), shell => shell.Planning = new BuildPlanningResult(plan));

        Assert.Equal(warning, Edit(vm, "edit-short").Warning);
        Assert.Equal("", Edit(vm, "edit-long").Warning);
    }

    /// <summary>One line reached twice keeps one row and is about both owners. Dropping the second used to
    /// leave a token unmarked by a line that is genuinely about it.</summary>
    [Fact]
    public async Task One_line_two_owners_keeps_one_row_that_marks_both()
    {
        const string reason = "This part's answer cannot be resolved.";
        var plan = new AuthoredBuildPlan
        {
            Bindings = new[] { BlockedRow("edit-long", reason), BlockedRow("edit-short", reason) },
        };
        var (vm, _, _) = await Page(Keyed(), shell => shell.Planning = new BuildPlanningResult(plan));

        var row = Assert.Single(vm.Issues);
        Assert.Equal(new[] { "edit-long", "edit-short" }, row.EditDefinitionIds);
        Assert.True(Edit(vm, "edit-long").IsBlocked);
        Assert.True(Edit(vm, "edit-short").IsBlocked);
        Assert.Equal(2, row.Placements.Count);
    }

    private static PlannedBinding BlockedRow(string editDefinitionId, string reason) => new(
        editDefinitionId + ":row",
        editDefinitionId,
        new TargetSlot { Id = editDefinitionId + "-slot", Part = AuthoredEditFixtures.Body },
        null,
        new Binding { SlotId = editDefinitionId + "-slot", Kind = BindingKind.TargetGameValue },
        null,
        new BuildEmissionGate(Array.Empty<BuildGateTerm>(), Array.Empty<BuildGateTerm>()),
        new BuildOperationResolution(
            BuildPlanDecision.Blocked(BuildPlanVerdict.Unresolved, reason), null));

    private static PlannedPart RenderBlockedPart(bool suppression, string decisionReason, string renderReason)
    {
        var resolution = new BuildOperationResolution(BuildPlanDecision.Inherited(decisionReason),
            new BuildRenderPlan(new[]
            {
                new BuildRenderRole(BuildRenderRoleKind.RenderCarrier, BuildCoverageState.Unsupported,
                    null, null, renderReason),
            }, Array.Empty<RenderContract>(), renderReason));
        var operation = new PlannedPartOperation(PlanCondition.Always,
            suppression ? PlannedPartDisposition.Hidden : PlannedPartDisposition.Edit,
            "edit-long", suppression ? null : resolution, Array.Empty<PlannedBinding>(),
            new[] { PlanCondition.Always });
        return new PlannedPart(AuthoredEditFixtures.Body, operation.Disposition, "edit-long", null,
            new[] { operation }, Array.Empty<PlanCondition>(), suppression ? resolution : null, null,
            Array.Empty<PlannedBinding>(), Array.Empty<PlannedGroupTouch>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_blocking_render_plan_projects_its_own_reason(bool suppression)
    {
        const string decisionReason = "The decision reports no problem.";
        const string renderReason = "Render coverage is missing.";
        var plan = new AuthoredBuildPlan
        {
            Parts = new[] { RenderBlockedPart(suppression, decisionReason, renderReason) },
        };

        var (vm, _, _) = await Page(Keyed(), shell => shell.Planning = new BuildPlanningResult(plan));

        Assert.False(plan.CanBuild);
        var row = Assert.Single(vm.BlockedRows);
        Assert.Equal(renderReason, row.RawMessage);
        Assert.Equal("Cannot build Long body on body: " + renderReason, row.Message);
        Assert.DoesNotContain(decisionReason, row.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Board_marks_keep_the_raw_reason_while_diagnostics_keep_attribution()
    {
        const string reason = "This part's answer cannot be resolved.";
        var plan = new AuthoredBuildPlan { Bindings = new[] { BlockedRow("edit-long", reason) } };
        var (vm, _, _) = await Page(Keyed(), shell => shell.Planning = new BuildPlanningResult(plan));

        var token = vm.Groups.Single().States[0].Tokens.Single();
        var row = Assert.Single(vm.BlockedRows);
        Assert.Equal(reason, token.Warning);
        Assert.Equal(reason, Edit(vm, "edit-long").Warning);
        Assert.Equal("Cannot build Long body on body: " + reason, row.Message);
        Assert.True(vm.ShowBlockedVerdict);
        Assert.False(vm.ShowFooterVerdict);
        Assert.Null(vm.StatusTip);

        Assert.Equal("Body options · State 1", Assert.Single(row.Placements).Label);
        row.Placements[0].FocusCommand.Execute(null);
        Assert.Equal(BuildMarkedTarget.State(vm.Groups.Single().Id, vm.Groups.Single().States[0].Id),
            vm.MarkedTarget);
        Assert.True(vm.Groups.Single().States[0].IsMarked);
        Assert.Equal(1, MarkedCardCount(vm));
        Assert.Equal("state-0001", vm.FocusTarget);
        Assert.Equal("Showing Body options · State 1.", vm.Status);
        Assert.Equal(vm.Status, vm.StatusTip);
    }

    [Fact]
    public async Task A_second_diagnostic_chip_moves_the_exact_mark_and_board_input_clears_it()
    {
        var (vm, _, _) = await Page(TouchedByTwoGroups());
        var group = vm.Groups[1];
        var state = group.States[0];
        var alwaysChip = new BuildPlacementChipVm(vm, "Always", null, null);
        var groupChip = new BuildPlacementChipVm(vm, group.DisplayName, group.Id, null, isGroup: true);
        var stateChip = new BuildPlacementChipVm(vm, state.DisplayName, group.Id, state.Id);

        alwaysChip.FocusCommand.Execute(null);
        Assert.True(vm.Always.IsMarked);
        Assert.Equal(1, MarkedCardCount(vm));

        groupChip.FocusCommand.Execute(null);
        Assert.False(vm.Always.IsMarked);
        Assert.True(group.IsMarked);
        Assert.Equal(1, MarkedCardCount(vm));

        stateChip.FocusCommand.Execute(null);
        Assert.False(group.IsMarked);
        Assert.True(state.IsMarked);
        Assert.False(vm.Groups[0].States[0].IsMarked); // the same state id in the other group is not the card
        Assert.Equal(1, MarkedCardCount(vm));

        vm.ClearMarkedTarget();
        Assert.Null(vm.MarkedTarget);
        Assert.Equal(0, MarkedCardCount(vm));
    }

    [Fact]
    public async Task Edit_selection_does_not_create_a_board_mark()
    {
        var (vm, _, _) = await Page(Keyed());

        vm.SelectEdit(Edit(vm, "edit-short").Edit);

        Assert.Null(vm.MarkedTarget);
        Assert.Equal(0, MarkedCardCount(vm));
        Assert.Equal("edit:edit-short", vm.FocusTarget);
    }

    private static PlannedPart LifecycleBlockedPart(string reason)
    {
        var operation = new PlannedPartOperation(PlanCondition.Always, PlannedPartDisposition.Edit,
            "edit-long", null, Array.Empty<PlannedBinding>(), new[] { PlanCondition.Always });
        return new PlannedPart(AuthoredEditFixtures.Body, PlannedPartDisposition.Edit, "edit-long", null,
            new[] { operation }, Array.Empty<PlanCondition>(), null,
            new BuildLifecycleResolution(BuildPlanVerdict.Unresolved, null, reason),
            Array.Empty<PlannedBinding>(), Array.Empty<PlannedGroupTouch>());
    }

    [Fact]
    public void Blocking_attribution_has_one_approved_shape_per_owner_layout()
    {
        const string reason = "The existing reason stays intact.";

        Assert.Equal("Cannot build Long body on body: " + reason,
            BuildIssueAttribution.Blocking(reason,
                new[] { new BuildIssueOwner("edit-long", "Long body", "body") }));
        Assert.Equal("Cannot build Long body and Short body on body: " + reason,
            BuildIssueAttribution.Blocking(reason, new[]
            {
                new BuildIssueOwner("edit-long", "Long body", "body"),
                new BuildIssueOwner("edit-short", "Short body", "body"),
            }));
        Assert.Equal("Cannot build Long body on body and Warm hat on hat: " + reason,
            BuildIssueAttribution.Blocking(reason, new[]
            {
                new BuildIssueOwner("edit-long", "Long body", "body"),
                new BuildIssueOwner("edit-hat", "Warm hat", "hat"),
            }));
        Assert.Equal("Cannot build Long body and Long body on body: " + reason,
            BuildIssueAttribution.Blocking(reason, new[]
            {
                new BuildIssueOwner("edit-long", "Long body", "body"),
                new BuildIssueOwner("edit-short", "Long body", "body"),
                new BuildIssueOwner("edit-short", "Long body", "body"),
            }));
        Assert.Equal("Cannot build Long body and 2 more: " + reason,
            BuildIssueAttribution.BlockingSummary(reason, new[]
            {
                new BuildIssueOwner("edit-long", "Long body", "body"),
                new BuildIssueOwner("edit-short", "Short body", "body"),
                new BuildIssueOwner("edit-hat", "Warm hat", "hat"),
            }));
        Assert.Equal("Cannot build Long body on body: Missing evidence.",
            BuildIssueAttribution.Blocking("Missing evidence",
                new[] { new BuildIssueOwner("edit-long", "Long body", "body") }));
    }

    [Fact]
    public async Task A_lifecycle_blocker_reaches_the_blocked_rows_gate_and_owned_chip()
    {
        const string reason = "Lifecycle coverage is incomplete.";
        var plan = new AuthoredBuildPlan { Parts = new[] { LifecycleBlockedPart(reason) } };
        var (vm, _, _) = await Page(Keyed(), shell => shell.Planning = new BuildPlanningResult(plan));

        Assert.False(plan.CanBuild);
        var row = Assert.Single(vm.BlockedRows);
        Assert.Equal(reason, row.RawMessage);
        Assert.Equal("Cannot build Long body on body: " + reason, row.Message);
        Assert.Equal(row.Message, vm.PrimaryBlockedMessage);
        Assert.Equal(row.Message, vm.BuildDisabledReason);
        Assert.Equal("Body options · State 1", Assert.Single(row.Placements).Label);
        Assert.Equal(row.Placements, vm.PrimaryBlockedPlacements);
    }

    [Fact]
    public async Task Keyless_group_blockers_keep_the_group_text_and_have_an_exact_group_chip()
    {
        const string named = "Key group 'Body options' has no key. This blocks the build. "
            + "Give it a key, or delete the group.";
        var namedPlan = new AuthoredBuildPlan
        {
            Conflicts = new[] { named },
            IssueEditIds = Owners((named, new[] { "edit-long", "edit-short" })),
            IssueGroupIds = GroupOwners((named, new[] { "key-0001" })),
        };
        var (namedVm, _, _) = await Page(Keyed(key: null),
            shell => shell.Planning = new BuildPlanningResult(namedPlan));

        var namedRow = Assert.Single(namedVm.BlockedRows);
        Assert.Equal(named, namedRow.Message);
        Assert.Equal("group:key-0001", namedRow.Placements[0].Target);
        Assert.Equal("Body options", namedRow.Placements[0].Label);
        Assert.Equal(3, namedRow.Placements.Count);

        const string unnamed = "Unnamed key group has no key. This blocks the build. "
            + "Give it a key, or delete the group.";
        var empty = EmptyStates();
        empty.KeyGroups[0].Key = null;
        empty.KeyGroups[0].Label = null;
        var unnamedPlan = new AuthoredBuildPlan
        {
            Conflicts = new[] { unnamed },
            IssueGroupIds = GroupOwners((unnamed, new[] { "key-0001" })),
        };
        var (unnamedVm, _, _) = await Page(empty,
            shell => shell.Planning = new BuildPlanningResult(unnamedPlan));

        var unnamedRow = Assert.Single(unnamedVm.BlockedRows);
        Assert.Equal(unnamed, unnamedRow.Message);
        Assert.Equal("Unnamed key group", Assert.Single(unnamedRow.Placements).Label);
        Assert.Equal("group:key-0001", unnamedRow.Placements[0].Target);
    }

    [Fact]
    public async Task Strip_states_own_counts_result_text_and_only_one_primary_chip()
    {
        var (vm, _, shell) = await Page(Keyed(), recorder =>
        {
            recorder.RunWarnings = new[] { "Run warning." };
            recorder.RunInfos = new[] { "One info line." };
        });
        Assert.False(vm.HasDiagnostics);
        Assert.Equal("", vm.DiagnosticCounts);

        await vm.BuildCommand.ExecuteAsync(null);
        Assert.Equal("test-mod", vm.BuiltPackage);
        Assert.Equal(@"C:\published\test-mod", vm.FolderTip);
        Assert.Equal("Built test-mod.", vm.Footer.Text);
        Assert.Equal("Warnings 1 · Info 1", vm.DiagnosticCounts);
        Assert.True(vm.HasWarningDiagnosticsOnly);

        const string conflict = "Long body and Short body can be active together.";
        shell.Planning = new BuildPlanningResult(new AuthoredBuildPlan
        {
            Conflicts = new[] { conflict },
            Warnings = new[] { "Live warning." },
            IssueEditIds = Owners((conflict, new[] { "edit-long", "edit-short" })),
        });
        await vm.ReplanAsync();

        Assert.Equal("Blocked 1 · Warnings 2 · Info 1", vm.DiagnosticCounts);
        Assert.Equal(1, vm.BlockedCount);
        Assert.Equal(2, vm.WarningCount);
        Assert.Equal(1, vm.InfoCount);
        Assert.False(vm.HasWarningDiagnosticsOnly);
        Assert.Equal("Blocked 1", vm.BlockedSectionHeader);
        Assert.Equal("Warnings 2", vm.WarningSectionHeader);
        Assert.Equal("Info 1", vm.InfoSectionHeader);
        Assert.Equal(2, vm.PrimaryBlocked!.Placements.Count);
        Assert.Single(vm.PrimaryBlockedPlacements);
        Assert.Equal("Cannot build Long body and 1 more: " + conflict, vm.PrimaryBlockedSummary);
        Assert.Equal(vm.PrimaryBlockedMessage, vm.BuildDisabledReason);
        Assert.DoesNotContain("Something is blocking", vm.BuildDisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switching_projects_clears_and_notifies_the_last_build_result_cluster()
    {
        var (vm, _, shell) = await Page(AuthoredEditFixtures.Golden());
        await vm.BuildCommand.ExecuteAsync(null);
        Assert.True(vm.HasLastBuild);
        Assert.True(vm.HasBuildZip);
        Assert.True(vm.HasBuildLog);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var next = new AuthoredEditSession(new AuthoredProject());
        shell.Session = next;
        shell.Planning = new BuildPlanningResult(new AuthoredBuildPlan());

        vm.Load(next);
        await vm.ReplanAsync();

        Assert.False(vm.HasLastBuild);
        Assert.False(vm.HasBuildZip);
        Assert.False(vm.HasBuildLog);
        Assert.Equal("", vm.BuiltPackage);
        Assert.Equal("", vm.LastBuildDir);
        Assert.Equal(BuildGate.NothingAuthored, vm.Footer.Text);
        Assert.Contains(nameof(BuildPageVm.HasLastBuild), changed);
        Assert.Contains(nameof(BuildPageVm.HasBuildZip), changed);
        Assert.Contains(nameof(BuildPageVm.HasBuildLog), changed);
    }

    [Fact]
    public async Task State_composition_is_lazy_omits_foreign_parts_and_counts_hide_precedence()
    {
        var fixture = CompositionFixture();
        var (vm, _, _) = await Page(fixture.Project,
            shell => shell.Planning = new BuildPlanningResult(fixture.Plan));
        var body = vm.Groups.Single(group => group.Id == "key-body");
        var mixed = body.States.Single(state => state.Id == "body-mixed");
        var clear = body.States.Single(state => state.Id == "body-clear");

        Assert.Equal("1 hidden", mixed.CountLine);
        Assert.Empty(mixed.Composition);
        mixed.OpenCompositionCommand.Execute(null);

        Assert.Equal(mixed.ActiveCount,
            mixed.Composition.Count(row => row.State == BuildResolvedPartState.Active));
        Assert.Equal(mixed.HiddenCount,
            mixed.Composition.Count(row => row.State == BuildResolvedPartState.Hidden));
        Assert.Equal("hidden", mixed.Composition.Single(row => row.Part == "body").Answer);
        Assert.Equal("original", mixed.Composition.Single(row => row.Part == "cape").Answer);
        Assert.DoesNotContain(mixed.Composition, row => row.Part == "hair");

        Assert.Equal("1 active", clear.CountLine);
        Assert.Empty(clear.Composition);
        clear.OpenCompositionCommand.Execute(null);
        Assert.Equal(clear.ActiveCount,
            clear.Composition.Count(row => row.State == BuildResolvedPartState.Active));
        Assert.Equal(clear.HiddenCount,
            clear.Composition.Count(row => row.State == BuildResolvedPartState.Hidden));
        Assert.Equal("Long body", clear.Composition.Single(row => row.Part == "body").Answer);
        Assert.Equal("original", clear.Composition.Single(row => row.Part == "cape").Answer);
        Assert.DoesNotContain(clear.Composition, row => row.Part == "hair");
    }

    [Fact]
    public async Task Composition_cache_advances_only_when_a_presentation_is_applied()
    {
        var fixture = CompositionFixture();
        var (vm, session, shell) = await Page(fixture.Project,
            recorder => recorder.Planning = new BuildPlanningResult(fixture.Plan));
        var state = vm.Groups.Single(group => group.Id == "key-body").States
            .Single(row => row.Id == "body-mixed");
        state.OpenCompositionCommand.Execute(null);
        var cached = state.Composition.Single(row => row.Part == "body");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        shell.Plan = (_, _) =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            return new BuildPlanningResult(fixture.Plan);
        };
        Task pending = vm.ReplanAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            state.OpenCompositionCommand.Execute(null);
            Assert.Same(cached, state.Composition.Single(row => row.Part == "body"));
        }
        finally { release.Set(); }
        await pending;

        var applied = vm.Groups.Single(group => group.Id == "key-body").States
            .Single(row => row.Id == "body-mixed");
        applied.OpenCompositionCommand.Execute(null);
        Assert.NotSame(cached, applied.Composition.Single(row => row.Part == "body"));

        shell.Plan = null;
        shell.Planning = new BuildPlanningResult(fixture.Plan);
        session.RenameEdit("edit-long", "Long body renamed");
        await vm.ReplanAsync();
        var renamed = vm.Groups.Single(group => group.Id == "key-body").States
            .Single(row => row.Id == "body-clear");
        renamed.OpenCompositionCommand.Execute(null);
        Assert.Equal("Long body renamed", renamed.Composition.Single(row => row.Part == "body").Answer);
    }

    [Fact]
    public async Task Delete_group_confirm_names_removed_placements_and_every_edit_left_unused()
    {
        var (vm, session, shell) = await Page(Keyed());

        await vm.Groups.Single().DeleteCommand.ExecuteAsync(null);

        Assert.Equal("Delete Body options?", shell.LastConfirmTitle);
        Assert.Equal("This removes 2 uses of edits. Long body and Short body become unused."
            + "\n\nThis cannot be undone.", shell.LastConfirmBody);
        Assert.Equal("Delete", shell.LastConfirmLabel);
        Assert.True(shell.LastConfirmDangerous);
        Assert.Empty(session.Snapshot().KeyGroups);
        Assert.Equal(2, session.Snapshot().EditDefinitions.Count);
    }

    [Fact]
    public async Task Plan_conflict_marks_both_named_tokens_and_joins_warnings_with_clickable_placements()
    {
        const string conflict = "Long body and Short body can be active together.";
        const string warning = "Long body is redundant.";
        var plan = new AuthoredBuildPlan
        {
            Conflicts = new[] { conflict },
            Warnings = new[] { warning },
            IssueEditIds = Owners((conflict, new[] { "edit-long", "edit-short" }),
                (warning, new[] { "edit-long" })),
        };
        var (vm, _, _) = await Page(Keyed(), shell => shell.Planning = new BuildPlanningResult(plan));

        var longToken = vm.Groups.Single().States[0].Tokens.Single();
        var shortToken = vm.Groups.Single().States[1].Tokens.Single();
        Assert.Equal(conflict + "\n" + warning, longToken.Warning);
        const string attributed =
            "Cannot build Long body and Short body on body: Long body and Short body can be active together.";
        Assert.Equal(conflict, shortToken.Warning);
        Assert.Contains(conflict, Edit(vm, "edit-long").Warning);
        var row = vm.Issues.Single(issue => issue.RawMessage == conflict);
        Assert.Equal(attributed, row.Message);
        Assert.Equal(2, row.Placements.Count);
        Assert.All(row.Placements, chip => Assert.StartsWith("Body options · State ", chip.Label));
        Assert.Equal(attributed, vm.BuildDisabledReason);
    }

    [Fact]
    public async Task Completed_run_and_live_plan_warnings_share_one_channel_and_pill_count()
    {
        var plan = new AuthoredBuildPlan { Warnings = new[] { "Live warning." } };
        var (vm, session, shell) = await Page(AuthoredEditFixtures.Golden(), recorder =>
        {
            recorder.Planning = new BuildPlanningResult(plan);
            recorder.RunWarnings = new[] { "Run warning." };
            recorder.RunInfos = new[] { "One disclosure." };
        });

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.Equal(new[] { BuildWarningSource.LastBuildLead, "Run warning.", "Live warning." }, vm.Warnings);
        Assert.Equal(new[] { "One disclosure." }, vm.Infos);
        Assert.Equal(BuildFooterState.Built, vm.Footer.State);
        Assert.Equal("Built test-mod.", vm.Footer.Text);
        Assert.False(vm.BuildResultStale);

        session.RenameEdit("edit-long", "Longer body");
        Assert.True(vm.BuildResultStale);
        Assert.Equal("Build again to include the latest changes.", vm.BuildAgainLine);
        Assert.Equal(1, shell.RunCalls);
    }

    [Fact]
    public async Task A_stale_run_warning_that_is_now_blocking_is_not_reused_as_a_warning_issue()
    {
        const string conflict = "Long body and Short body can be active together.";
        var (vm, _, shell) = await Page(Keyed(), recorder => recorder.RunWarnings = new[] { conflict });
        await vm.BuildCommand.ExecuteAsync(null);
        shell.Planning = new BuildPlanningResult(new AuthoredBuildPlan
        {
            Conflicts = new[] { conflict },
            IssueEditIds = Owners((conflict, new[] { "edit-long", "edit-short" })),
        });

        await vm.ReplanAsync();

        var blocked = Assert.Single(vm.BlockedRows);
        var warning = Assert.Single(vm.WarningRows, row => !row.IsHeading);
        Assert.False(warning.BlocksBuild);
        Assert.NotSame(blocked, warning);
        Assert.Equal(conflict, warning.Message);
        Assert.Equal(1, vm.BlockedCount);
        Assert.Equal(1, vm.WarningCount);
        Assert.Equal("Blocked 1 · Warnings 1", vm.DiagnosticCounts);
    }

    [Fact]
    public async Task Preview_routes_through_the_session_and_same_name_stamp_changes_make_a_result_stale()
    {
        var project = AuthoredEditFixtures.Golden();
        project.RootDir = @"C:\mod";
        var (vm, session, shell) = await Page(project);

        vm.DropPreview(new[] { @"C:\shots\cover.PNG" });
        Assert.Equal("preview.png", session.Snapshot().Info.Preview);
        Assert.Equal(1, shell.PreviewSetCalls);
        await vm.BuildCommand.ExecuteAsync(null);

        shell.PreviewOverride = new BuildPreviewState("preview.png", @"C:\mod\preview.png", false, "new-bytes");
        vm.Enter();
        await vm.ReplanAsync();
        Assert.True(vm.BuildResultStale);

        await vm.RemovePreviewCommand.ExecuteAsync(null);
        Assert.Equal(1, shell.PreviewRemoveCalls);
        Assert.Null(session.Snapshot().Info.Preview);
    }

    [Fact]
    public async Task Opening_a_project_with_a_preview_finishes_reading_it()
    {
        var project = AuthoredEditFixtures.Golden();
        project.RootDir = @"C:\mod";
        var (vm, _, shell) = await Page(project);

        shell.PreviewOverride = new BuildPreviewState("preview.png", @"C:\mod\preview.png", false, "bytes");
        vm.Enter();
        await Task.Yield();

        Assert.False(vm.PreviewDecoding);
        Assert.True(vm.PreviewUndecodable);
    }

    [Fact]
    public async Task Running_build_holds_page_mutations_until_the_run_lands()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, _, shell) = await Page(Keyed(), recorder => recorder.RunHold = hold);

        Task run = vm.BuildCommand.ExecuteAsync(null);
        for (int i = 0; !vm.IsWorkInFlight && i < 100; i++) await Task.Yield();
        Assert.True(vm.IsWorkInFlight);
        vm.Groups.Single().Key = "F9";
        Assert.Equal(BuildPageVm.BuildRunningReason, vm.Status);

        hold.SetResult();
        await run;
        Assert.False(vm.IsWorkInFlight);
        Assert.Equal(1, shell.RunCalls);
    }

    [Fact]
    public async Task Whole_mod_and_group_key_collision_is_disclosed_on_both_controls()
    {
        var (vm, _, _) = await Page(Keyed(), shell => shell.WholeModKey = "f6");

        Assert.Equal("Same key as Body options. They switch together.", vm.WholeModKeyCollisionTip);
        Assert.Equal("Same key as the whole mod. They switch together.", vm.Groups.Single().CollisionTip);
    }

    [Fact]
    public async Task Loader_stand_in_changes_from_set_to_fix_and_carries_the_disk_diagnosis()
    {
        var (unset, _, _) = await Page(AuthoredEditFixtures.Golden(), shell =>
            shell.Loader = new BuildLoaderState(null, false, null, default));
        Assert.Equal("Set 3DMigoto path…", unset.LoaderButtonLabel);
        Assert.Equal(InstallGate.SetLoader, unset.LoaderButtonTip);

        const string missing = @"C:\moved\3DMigoto Loader.exe";
        var (set, _, _) = await Page(AuthoredEditFixtures.Golden(), shell =>
            shell.Loader = new BuildLoaderState(missing, false, null, default));
        Assert.Equal("Fix 3DMigoto path…", set.LoaderButtonLabel);
        Assert.Equal(LoaderGate.LoaderNotFound(missing), set.LoaderButtonTip);
    }

    /// <summary>The window shell's four sentences about a missing artifact are written for the person
    /// reading them, so they reach the status line whole. Anything else the open throws is a defect and
    /// gets the action's own words instead.</summary>
    [Fact]
    public async Task A_gone_artifact_keeps_its_own_sentence_on_the_status_line()
    {
        var window = new MainWindowViewModel(startLoad: false);
        string missing = Path.Combine(Path.GetTempPath(), "drl-gone-" + Guid.NewGuid().ToString("N"));

        var refusal = Assert.Throws<AuthoredRefusalException>(() =>
            window.OpenArtifact(BuildArtifactKind.Folder, missing));
        Assert.Equal("The build folder is gone. Build again.", refusal.Message);

        var (vm, _, shell) = await Page(AuthoredEditFixtures.Golden());
        shell.OpenFailure = refusal;
        await vm.BuildCommand.ExecuteAsync(null);

        vm.OpenFolderCommand.Execute(null);

        Assert.Equal("The build folder is gone. Build again.", vm.Status);
    }

    [Fact]
    public async Task Install_lands_its_outcome_and_keeps_the_built_result_available()
    {
        var (vm, _, shell) = await Page(AuthoredEditFixtures.Golden());
        await vm.BuildCommand.ExecuteAsync(null);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, shell.InstallCalls);
        Assert.Equal(BuildFooterState.Notice, vm.Footer.State);
        Assert.Equal("Installed test-mod to the Mods folder.", vm.Footer.Text);
        Assert.True(vm.HasLastBuild);
        Assert.True(vm.HasLastInstall);
        Assert.Equal(@"C:\3dmigoto\Mods\test-mod", vm.InstalledFolderTip);
    }

    [Fact]
    public async Task Plan_blockers_do_not_hide_install_running_or_result_lines()
    {
        const string conflict = "Long body and Short body can be active together.";
        var (vm, _, shell) = await Page(Keyed());
        await vm.BuildCommand.ExecuteAsync(null);
        shell.Planning = new BuildPlanningResult(new AuthoredBuildPlan
        {
            Conflicts = new[] { conflict },
            IssueEditIds = Owners((conflict, new[] { "edit-long", "edit-short" })),
        });
        await vm.ReplanAsync();

        Assert.True(vm.HasBlocked);
        Assert.True(vm.HasLastBuild);
        Assert.Equal(BuildFooterState.Built, vm.Footer.State);
        Assert.True(vm.ShowFooterVerdict);
        Assert.False(vm.ShowBlockedVerdict);

        shell.InstallHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task installing = vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(BuildFooterState.Running, vm.Footer.State);
        Assert.Equal("Installing…", vm.Footer.Text);
        Assert.True(vm.ShowFooterVerdict);
        Assert.False(vm.ShowBlockedVerdict);

        shell.InstallHold.SetResult();
        await installing;
        Assert.Equal(BuildFooterState.Notice, vm.Footer.State);
        Assert.Equal("Installed test-mod to the Mods folder.", vm.Footer.Text);
        Assert.True(vm.ShowFooterVerdict);
        Assert.False(vm.ShowBlockedVerdict);
    }

    [Fact]
    public async Task Cancelling_install_restores_the_standing_build_footer()
    {
        var (vm, _, shell) = await Page(AuthoredEditFixtures.Golden());
        shell.InstallResult = new BuildInstallResult(false, false, "");
        await vm.BuildCommand.ExecuteAsync(null);
        var standing = vm.Footer;

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Same(standing, vm.Footer);
        Assert.True(vm.HasLastBuild);
    }

    [Fact]
    public async Task A_failed_first_run_exposes_its_build_log_without_a_result_folder()
    {
        var (vm, session, shell) = await Page(AuthoredEditFixtures.Golden());
        shell.RunResult = new BuildRunResult(false, "encoder stopped", "", null, "test-mod",
            @"C:\published\test-mod.build.log", Array.Empty<string>(), Array.Empty<string>(),
            session.Revision, "none");

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.False(vm.HasLastBuild);
        Assert.True(vm.HasFailureLog);
        Assert.Equal(BuildFooterState.Failed, vm.Footer.State);
    }

    /// <summary>A failure that wrote no log leaves no Log button. Offering the previous run's log labels
    /// another build's account as this one's.</summary>
    [Fact]
    public async Task A_failure_with_no_log_stops_offering_the_last_runs_log()
    {
        var (vm, session, shell) = await Page(AuthoredEditFixtures.Golden());
        await vm.BuildCommand.ExecuteAsync(null);
        Assert.True(vm.HasBuildLog);
        shell.RunResult = new BuildRunResult(false, "encoder stopped", "", null, "test-mod",
            "", Array.Empty<string>(), Array.Empty<string>(), session.Revision, "none");

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.False(vm.HasBuildLog);
        Assert.False(vm.HasFailureLog);
        Assert.Equal("", vm.LastLogPath);
    }

    [Fact]
    public async Task A_failed_rebuild_clears_the_result_when_the_new_run_starts()
    {
        var (vm, session, shell) = await Page(AuthoredEditFixtures.Golden());
        await vm.BuildCommand.ExecuteAsync(null);
        Assert.True(vm.HasLastBuild);
        shell.RunResult = new BuildRunResult(false, "encoder stopped", "", null, "test-mod",
            @"C:\published\test-mod.build.log", Array.Empty<string>(), Array.Empty<string>(),
            session.Revision, "none");

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.False(vm.HasLastBuild);
        Assert.True(vm.HasFailureLog);
    }

    [Fact]
    public async Task Library_row_and_board_token_hop_to_the_exact_edit()
    {
        var (vm, _, shell) = await Page(Keyed());
        var row = Edit(vm, "edit-short");

        vm.SelectEdit(row.Edit);
        Assert.Same(row, vm.SelectedEdit);
        Assert.True(row.IsSelected);
        Assert.Equal("edit:edit-short", vm.FocusTarget);
        row.OpenCommand.Execute(null);
        Assert.Equal("edit-short", shell.LastEditHop?.EditDefinitionId);

        vm.Groups.Single().States[0].Tokens.Single().OpenCommand.Execute(null);
        Assert.Equal("edit-long", shell.LastEditHop?.EditDefinitionId);
    }

    [Fact]
    public async Task Older_session_notification_cannot_overwrite_the_newer_revision()
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
        var shell = new FakeShell { Session = session };
        var vm = new BuildPageVm(shell);
        vm.Load(session);
        await vm.ReplanAsync();

        var older = Task.Run(() => session.RenameEdit("edit-long", "Older"));
        Assert.True(olderEntered.Wait(TimeSpan.FromSeconds(5)));
        await Task.Run(() => session.RenameEdit("edit-long", "Newer")).WaitAsync(TimeSpan.FromSeconds(5));
        releaseOlder.Set();
        await older.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new long[] { 2 }, shell.ChangedRevisions);
        Assert.Equal("Newer", Edit(vm, "edit-long").Label);
    }

    [Fact]
    public async Task Planning_result_overtaken_by_a_newer_generation_never_paints_the_page()
    {
        using var olderEntered = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        int call = 0;
        var dispatched = new ConcurrentQueue<Action>();
        var session = new AuthoredEditSession(AuthoredEditFixtures.Golden());
        var shell = new FakeShell { Session = session };
        shell.Plan = (_, _) =>
        {
            if (Interlocked.Increment(ref call) == 1)
            {
                olderEntered.Set();
                releaseOlder.Wait(TimeSpan.FromSeconds(5));
                return new BuildPlanningResult(new AuthoredBuildPlan
                    { Warnings = new[] { "Older warning." } });
            }
            return new BuildPlanningResult(new AuthoredBuildPlan
                { Warnings = new[] { "Newer warning." } });
        };
        var vm = new BuildPageVm(shell, dispatched.Enqueue);

        vm.Load(session);
        Assert.True(olderEntered.Wait(TimeSpan.FromSeconds(5)));
        Task newer = vm.ReplanAsync();
        Assert.True(SpinWait.SpinUntil(() => dispatched.Count > 0, TimeSpan.FromSeconds(5)));
        Assert.True(dispatched.TryDequeue(out var applyNewer));
        applyNewer();
        await newer.WaitAsync(TimeSpan.FromSeconds(5));
        releaseOlder.Set();
        Assert.True(SpinWait.SpinUntil(() => dispatched.Count > 0, TimeSpan.FromSeconds(5)));
        Assert.True(dispatched.TryDequeue(out var applyOlder));
        applyOlder();

        Assert.Equal(new[] { "Newer warning." }, vm.Warnings);
        Assert.DoesNotContain("Older warning.", vm.Warnings);
    }

    [Fact]
    public async Task Result_and_footer_survive_step_reentry_but_switching_session_drops_them()
    {
        var (vm, _, _) = await Page(AuthoredEditFixtures.Golden());
        await vm.BuildCommand.ExecuteAsync(null);
        string folder = vm.LastBuildDir;
        var footer = vm.Footer;

        vm.Enter();
        await vm.ReplanAsync();
        Assert.Equal(folder, vm.LastBuildDir);
        Assert.Equal(footer, vm.Footer);

        vm.Load(new AuthoredEditSession(Keyed()));
        Assert.False(vm.HasLastBuild);
        Assert.Equal("", vm.LastBuildDir);
    }

    [Fact]
    public void Build_and_install_gates_have_one_ordered_reason_each()
    {
        var placed = new AuthoredEditSession(AuthoredEditFixtures.Golden()).Outline();
        var unplacedProject = AuthoredEditFixtures.Golden();
        unplacedProject.Always.Clear();
        var unplaced = new AuthoredEditSession(unplacedProject).Outline();
        var good = new AuthoredBuildPlan();

        Assert.Equal(BuildGate.GameUnavailable,
            BuildGate.Reason(new BuildPlanningResult(GameUnavailable: BuildGate.GameUnavailable), placed, null));
        Assert.Equal("Planning failed.",
            BuildGate.Reason(new BuildPlanningResult(Failure: "Planning failed."), placed, null));
        Assert.Equal(BuildGate.NothingAuthored,
            BuildGate.Reason(new BuildPlanningResult(good),
                new AuthoredEditSession(new AuthoredProject()).Outline(), null));
        Assert.Equal(BuildGate.NothingPlaced, BuildGate.Reason(new BuildPlanningResult(good), unplaced, null));
        Assert.Equal("A visible conflict.", BuildGate.Reason(new BuildPlanningResult(
            new AuthoredBuildPlan { Conflicts = new[] { "A visible conflict." } }), placed,
            "A visible conflict."));
        Assert.Equal(BuildGate.UnnamedPlanBlocker, BuildGate.Reason(new BuildPlanningResult(
            new AuthoredBuildPlan { Conflicts = new[] { "An unprojected conflict." } }), placed, null));
        Assert.Null(BuildGate.Reason(new BuildPlanningResult(good), placed, null));

        var hookless = UsableLoader with { Ini = new MigotoIniFacts(true, true, false) };
        Assert.Equal(InstallGate.NoBuild, InstallGate.Reason(false, hookless));
        Assert.Equal(LoaderGate.NoTextureHook, InstallGate.Reason(true, hookless));
        Assert.Null(InstallGate.Reason(true, UsableLoader));
    }

    [Fact]
    public async Task The_state_remove_button_greys_at_the_two_state_floor_instead_of_refusing_after_the_click()
    {
        var (vm, _, _) = await Page(Keyed());
        Assert.All(vm.Groups.Single().States, state => Assert.False(state.CanRemove));
        Assert.Contains("Delete the group instead", vm.Groups.Single().States[0].RemoveTip);

        vm.Groups.Single().AddStateCommand.Execute(null);
        await vm.ReplanAsync();

        Assert.All(vm.Groups.Single().States, state => Assert.True(state.CanRemove));
        Assert.Equal(3, vm.Groups.Single().States.Count);
    }

    [Fact]
    public async Task A_group_with_no_key_or_label_yet_reads_as_the_unnamed_key_group_everywhere()
    {
        var (vm, _, _) = await Page(AuthoredEditFixtures.Golden());

        Edit(vm, "edit-long").MakeKeyCommand.Execute(null);
        await vm.ReplanAsync();

        Assert.Equal(new[] { "Unnamed key group · 1" }, Edit(vm, "edit-long").PlacementChips);
        Assert.Equal("Unnamed key group", vm.Groups.Single().DisplayName);
        var choice = Edit(vm, "edit-long").UseIn.Single(row =>
            row.GroupId == vm.Groups.Single().Id && row.Label.EndsWith("State 1", StringComparison.Ordinal));
        Assert.StartsWith("Unnamed key group", choice.Label);
    }

    [Fact]
    public void The_change_summary_counts_geometry_pictures_and_shading_and_says_nothing_for_a_fresh_edit()
    {
        static EditSlotState State(TargetInputKind input, BindingKind kind, string? assetId = null,
            ProjectAssetKind assetKind = ProjectAssetKind.Picture, string? slotId = null) => new(
            new TargetSlot { Id = slotId ?? assetId ?? "slot", Input = input },
            new Binding { Kind = kind, ProjectAssetId = assetId },
            assetId is null ? null : new ProjectAsset { Id = assetId, Kind = assetKind });

        Assert.Equal("", BuildPageVm.ChangeSummary(EditDefinitionKind.Hide, new[]
        {
            State(TargetInputKind.Visibility, BindingKind.Hidden),
        }));
        Assert.Equal("", BuildPageVm.ChangeSummary(EditDefinitionKind.Content, new[]
        {
            State(TargetInputKind.Geometry, BindingKind.TargetGameValue),
            State(TargetInputKind.BaseColor, BindingKind.TargetGameValue),
        }));
        Assert.Equal("mesh", BuildPageVm.ChangeSummary(EditDefinitionKind.Content, new[]
        {
            State(TargetInputKind.Geometry, BindingKind.ProjectAsset, "asset-mesh",
                ProjectAssetKind.Geometry),
        }));
        Assert.Equal("mesh · 1 image", BuildPageVm.ChangeSummary(EditDefinitionKind.Content, new[]
        {
            State(TargetInputKind.Geometry, BindingKind.ProjectAsset, "asset-mesh",
                ProjectAssetKind.Geometry),
            State(TargetInputKind.BaseColor, BindingKind.ProjectAsset, "asset-pic"),
            State(TargetInputKind.Normal, BindingKind.ProjectAsset, "asset-pic"),
        }));
        // A RAMP is a project asset on a non-geometry slot and is not a picture the modder painted — it is
        // picked from the game's own, and carries its own token.
        Assert.Equal("1 image · 1 ramp", BuildPageVm.ChangeSummary(EditDefinitionKind.Content, new[]
        {
            State(TargetInputKind.BaseColor, BindingKind.ProjectAsset, "asset-a"),
            State(TargetInputKind.Ramp, BindingKind.ProjectAsset, "asset-b", ProjectAssetKind.Ramp),
        }));
        // …and a shading-values edit carries no image at all, so it is counted as shading, never as "1 image"
        Assert.Equal("1 shading value", BuildPageVm.ChangeSummary(EditDefinitionKind.Content, new[]
        {
            State(TargetInputKind.Geometry, BindingKind.TargetGameValue),
            State(TargetInputKind.MaterialValue, BindingKind.ProjectAsset, "asset-values",
                ProjectAssetKind.StructuredValue),
        }));
        // A value COPIED from another material carries no project asset at all, so the count is read off
        // the slot rather than the asset — otherwise a copy is invisible on this line.
        Assert.Equal("mesh · 2 shading values", BuildPageVm.ChangeSummary(EditDefinitionKind.Content, new[]
        {
            State(TargetInputKind.Geometry, BindingKind.ProjectAsset, "asset-mesh",
                ProjectAssetKind.Geometry),
            State(TargetInputKind.MaterialValue, BindingKind.ProjectAsset, "asset-values",
                ProjectAssetKind.StructuredValue),
            State(TargetInputKind.MaterialValue, BindingKind.SourceSlot, slotId: "slot-copied"),
        }));
        // An unanswered shading slot is not a change: every content edit on a part carries one once any
        // sibling edit mints it.
        Assert.Equal("", BuildPageVm.ChangeSummary(EditDefinitionKind.Content, new[]
        {
            State(TargetInputKind.MaterialValue, BindingKind.TargetGameValue),
        }));
        // The line stays at counts grain and never names which value or which map moved.
        Assert.Equal("1 image · 1 ramp · 1 shading value", BuildPageVm.ChangeSummary(EditDefinitionKind.Content, new[]
        {
            State(TargetInputKind.BaseColor, BindingKind.ProjectAsset, "asset-a"),
            State(TargetInputKind.Ramp, BindingKind.ProjectAsset, "asset-b", ProjectAssetKind.Ramp),
            State(TargetInputKind.MaterialValue, BindingKind.ProjectAsset, "asset-values",
                ProjectAssetKind.StructuredValue),
        }));
    }

    [Fact]
    public async Task A_blocking_issue_takes_the_blocked_tier_and_never_reads_as_a_warning()
    {
        const string conflict = "Long body and Short body can be active together.";
        const string warning = "Long body is redundant.";
        var plan = new AuthoredBuildPlan
        {
            Conflicts = new[] { conflict },
            Warnings = new[] { warning },
            IssueEditIds = Owners((conflict, new[] { "edit-long", "edit-short" })),
        };
        var (vm, _, _) = await Page(Keyed(), shell => shell.Planning = new BuildPlanningResult(plan));

        Assert.True(vm.HasBlocked);
        Assert.False(vm.EditsNeedAttention);
        Assert.Equal(new[]
        {
            "Cannot build Long body and Short body on body: Long body and Short body can be active together."
        }, vm.BlockedRows.Select(row => row.Message));
        Assert.Equal(new[] { warning }, vm.Warnings);
        var longToken = vm.Groups.Single().States[0].Tokens.Single();
        Assert.True(longToken.IsBlocked);
        Assert.Equal("✗", longToken.Mark);
        Assert.True(Edit(vm, "edit-long").IsBlocked);
        Assert.True(vm.Groups.Single().States[1].Tokens.Single().IsBlocked);
    }

    [Fact]
    public async Task A_plain_warning_stays_amber_and_out_of_the_blocked_tier()
    {
        var plan = new AuthoredBuildPlan { Warnings = new[] { "Long body is redundant." } };
        var (vm, _, _) = await Page(Keyed(), shell => shell.Planning = new BuildPlanningResult(plan));

        Assert.False(vm.HasBlocked);
        Assert.True(vm.HasWarnings);
        Assert.Empty(vm.BlockedRows);
        var token = vm.Groups.Single().States[0].Tokens.Single();
        Assert.False(token.IsBlocked);
        Assert.Equal("⚠", token.Mark);
    }
}
