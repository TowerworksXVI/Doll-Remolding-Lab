using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Remold.App.ViewModels;
using Remold.App.ViewModels.Workbench;
using Remold.Core.Export;
using Remold.Core.Model;
using Remold.Core.Project;
using Remold.Core.Tables;
using Remold.Core.Tests.Support;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The prewarm queue as the view-model actually wires it: what a Pick tick, an Edit-tree visit, a remove and
/// each kind of open do to speculative work. Driven with the queue's job replaced by a gate the test opens
/// and closes — the real job reads a game install, and what these pin is the wiring around it, not the
/// reading.
/// </summary>
[Collection("Dispatcher")]
public class PrewarmWiringTests
{
    // ---- a pick costs no disk ----

    [Fact]
    public void TickingASubjectInPickPreparesNothing()
    {
        using var lib = new TempLibrary();
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        var (character, outfit) = Row("Vesna", 1101, "VesnaSSR01");

        vm.AddSubject(character, outfit);

        Assert.True(vm.Prewarm.IsIdle);
        Assert.Empty(jobs.Started);
    }

    /// <summary>The character checkbox grabs every outfit the character has, each through the same
    /// per-subject add — the widest pick there is, and it prepares none of them.</summary>
    [Fact]
    public void TickingAWholeCharacterPreparesNothingForAnyOfItsOutfits()
    {
        using var lib = new TempLibrary();
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        var (character, outfits) = Rows("Vesna", 1101, "VesnaSSR01", "VesnaSSR02", "VesnaSSR03");

        foreach (var o in outfits) vm.AddSubject(character, o);

        Assert.True(vm.Prewarm.IsIdle);
        Assert.Empty(jobs.Started);
    }

    /// <summary>The tick still mints the mod folder, so the ledger entry has somewhere to persist — and that
    /// folder is all it writes.</summary>
    [Fact]
    public void TickingASubjectMintsTheModFolderAndPersistsTheLedgerIntoIt()
    {
        using var lib = new TempLibrary();
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        var (character, outfit) = Row("Vesna", 1101, "VesnaSSR01");

        vm.AddSubject(character, outfit);

        var minted = Assert.Single(Directory.GetDirectories(lib.Root));
        Assert.Equal(ModNaming.Slug(MainWindowViewModel.AutoModName("Vesna")), Path.GetFileName(minted));
        Assert.Equal(new[] { "VesnaSSR01" }, ModProject.Load(minted).Selection.Select(s => s.Outfit));
        Assert.Empty(Directory.GetDirectories(minted));   // no workspace: nothing was materialized
    }

    // ---- the first visit in Edit starts it, remove ends it ----

    [Fact]
    public void TheFirstVisitToAnOutfitStartsItsPrewarm()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);

        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        Assert.Equal(new MainWindowViewModel.SubjectKey("Vesna", "VesnaSSR01"), vm.Prewarm.RunningKey);
    }

    [Fact]
    public void ASecondOutfitVisitedWaitsItsTurn_OneOutfitIsPreparedAtATime()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);

        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));
        vm.PrewarmSubject(Subject("Karst", 1201, "KarstSSR01"));

        Assert.Equal(new MainWindowViewModel.SubjectKey("Vesna", "VesnaSSR01"), vm.Prewarm.RunningKey);
        Assert.Equal(new[] { new MainWindowViewModel.SubjectKey("Karst", "KarstSSR01") }, vm.Prewarm.Pending);
    }

    /// <summary>An outfit is prepared ONCE. The tree restores its selection on every hop back into Edit, and
    /// the queue only dedupes what is running or waiting — so a re-visit of a finished outfit would otherwise
    /// redo the whole combine.</summary>
    [Fact]
    public async Task ReVisitingAnOutfitDoesNotPrepareItAgain()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);

        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));   // still running
        jobs.Complete("Vesna/VesnaSSR01");
        await Until(() => vm.Prewarm.IsIdle, "the first preparation to finish");
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));   // finished

        Assert.Equal(new[] { "Vesna/VesnaSSR01" }, jobs.Started);
        Assert.True(vm.Prewarm.IsIdle);
    }

    [Fact]
    public async Task RemovingASubjectDrainsItsPrewarmBeforeTheLedgerDrops()
    {
        using var lib = new TempLibrary();
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        var (character, outfit) = Row("Vesna", 1101, "VesnaSSR01");
        vm.AddSubject(character, outfit);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        bool removed = await vm.RemoveSubjectAsync("Vesna", "VesnaSSR01", outfit.Model.MeshPrefix, "Vesna · Base");

        Assert.True(removed);                       // never refused for work the modder didn't ask for
        Assert.Contains("Vesna/VesnaSSR01", jobs.Cancelled);
        Assert.True(vm.Prewarm.IsIdle);             // …and it had unwound before the remove returned
    }

    /// <summary>A remove takes the outfit's workspace with it, so the once-per-outfit rule has to forget it:
    /// a re-added subject is one to prepare again.</summary>
    [Fact]
    public async Task ARemovedSubjectIsPreparedAgainWhenItIsVisitedOnceMore()
    {
        using var lib = new TempLibrary();
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        var (character, outfit) = Row("Vesna", 1101, "VesnaSSR01");
        vm.AddSubject(character, outfit);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));
        Assert.True(await vm.RemoveSubjectAsync("Vesna", "VesnaSSR01", outfit.Model.MeshPrefix, "Vesna · Base"));

        vm.AddSubject(character, outfit);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        Assert.Equal(new[] { "Vesna/VesnaSSR01", "Vesna/VesnaSSR01" }, jobs.Started);
    }

    /// <summary>The record says an outfit is preparing or prepared, so a preparation that FAILED has to drop
    /// it: the queue never re-runs a job that threw, and the record would otherwise leave the outfit
    /// unprepared for as long as the mod stays open.</summary>
    [Fact]
    public async Task AnOutfitWhosePreparationFailedIsPreparedAgainOnTheNextVisit()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);

        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));
        jobs.Fail("Vesna/VesnaSSR01");
        await Until(() => { Dispatcher.UIThread.RunJobs(); return vm.Prewarm.IsIdle; },
            "the failed preparation to unwind");
        Dispatcher.UIThread.RunJobs();   // the record's drop is posted behind the job

        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        Assert.Equal(new[] { "Vesna/VesnaSSR01", "Vesna/VesnaSSR01" }, jobs.Started);
    }

    /// <summary>The real job's give-up exits count as failures too: it stops on a missing install, an outfit
    /// the ledger no longer carries, an unreadable catalog. None of those prepared anything, so the record
    /// must not survive them either.</summary>
    [Fact]
    public async Task AnOutfitThePreparationGaveUpOnIsPreparedAgainOnTheNextVisit()
    {
        using var settings = new SettingsSnapshot();
        var vm = new MainWindowViewModel(startLoad: false);   // the real job, with no game files behind it

        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));
        await Until(() => { Dispatcher.UIThread.RunJobs(); return vm.Prewarm.IsIdle; },
            "the preparation to give up");
        Dispatcher.UIThread.RunJobs();   // the record's drop lands on the dispatcher, like the job did

        Assert.Empty(vm.Prepared);
    }

    /// <summary>A remove drains the subject's prewarm, and the Edit tree keeps running while it does. A visit
    /// landing in that window names an outfit whose files are about to be deleted, so its record must not
    /// survive the remove — it would leave the re-added subject never prepared.</summary>
    [Fact]
    public async Task ASubjectVisitedWhileItsRemoveDrainsIsStillPreparedAfterTheReAdd()
    {
        using var lib = new TempLibrary();
        using var jobs = new Jobs { HoldOnCancel = true };
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        var (character, outfit) = Row("Vesna", 1101, "VesnaSSR01");
        vm.AddSubject(character, outfit);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        var removing = vm.RemoveSubjectAsync("Vesna", "VesnaSSR01", outfit.Model.MeshPrefix, "Vesna · Base");
        Assert.False(removing.IsCompleted);                              // the drain is still up…
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));         // …and the tree lands on it
        jobs.Complete("Vesna/VesnaSSR01");
        Assert.True(await removing);

        vm.AddSubject(character, outfit);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        Assert.Equal(new[] { "Vesna/VesnaSSR01", "Vesna/VesnaSSR01" }, jobs.Started);
    }

    /// <summary>The once-per-outfit record belongs to the mod that is open. A new one starting on the same
    /// subject has its own empty workspace, so its first visit prepares it.</summary>
    [Fact]
    public async Task ANewModPreparesTheSameOutfitAgainOnItsFirstVisit()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));
        jobs.Complete("Vesna/VesnaSSR01");
        await Until(() => vm.Prewarm.IsIdle, "the first preparation to finish");

        vm.NewMod();
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        Assert.Equal(new[] { "Vesna/VesnaSSR01", "Vesna/VesnaSSR01" }, jobs.Started);
    }

    // ---- what counts as a visit in the Edit tree ----

    /// <summary>Every node under a subject root rolls up to that ONE subject: its header, a part, and a
    /// material under a part all name the same outfit to prepare, and never a sibling outfit.</summary>
    [Theory]
    [InlineData(WorkbenchNodeKind.Subject)]
    [InlineData(WorkbenchNodeKind.Part)]
    [InlineData(WorkbenchNodeKind.Material)]
    [InlineData(WorkbenchNodeKind.Skeleton)]
    public void LandingOnAnyNodeOfAnOutfitVisitsThatOutfit(WorkbenchNodeKind kind)
    {
        var shell = new VisitShell();
        var vm = TreeVm(shell);
        vm.Nodes.Add(SubjectTree(WbSubject("Vesna", "VesnaSSR01")));
        vm.Nodes.Add(SubjectTree(WbSubject("Karst", "KarstSSR01")));

        vm.SelectedNode = NodeOfKind(vm.Nodes[0], kind);

        Assert.Equal(new[] { "Vesna/VesnaSSR01" }, shell.Visited);
    }

    /// <summary>Opening a subject from Pick lands SELECTED on its root, so the open itself is the
    /// visit that starts preparation — the empty-slot select is the subject-root request the open uses.</summary>
    [Fact]
    public void ARootSelectRequestVisitsTheOutfit()
    {
        var shell = new VisitShell();
        var vm = TreeVm(shell);
        vm.Nodes.Add(SubjectTree(WbSubject("Vesna", "VesnaSSR01")));

        vm.RequestSelectPart("Vesna", "VesnaSSR01", "");

        Assert.Equal(new[] { "Vesna/VesnaSSR01" }, shell.Visited);
        Assert.Same(vm.Nodes[0], vm.SelectedNode);
    }

    /// <summary>A selection restored when the tree lands is a visit too: that outfit is the one the modder
    /// was working on, and a hop out to another step and back is how they come back to it.</summary>
    [Fact]
    public void ASelectionRestoredWhenTheTreeLandsIsAVisit()
    {
        var shell = new VisitShell();
        var vm = TreeVm(shell);
        var root = SubjectTree(WbSubject("Vesna", "VesnaSSR01"));
        vm.Nodes.Add(root);
        vm.SelectedNode = root;
        shell.Visited.Clear();   // the modder's own click; what this pins is the restore after it

        vm.Activate();           // remembers the selection, then rebuilds the tree whole
        Assert.Null(vm.SelectedNode);
        Assert.True(vm.HasPendingSelect);

        vm.Nodes.Add(root);      // the rebuilt tree lands…
        vm.ApplyPendingSelect(); // …and puts the selection back

        Assert.Same(root, vm.SelectedNode);
        Assert.Equal(new[] { "Vesna/VesnaSSR01" }, shell.Visited);
    }

    // ---- an explicit action takes the machinery ----

    [Fact]
    public async Task OpeningANOTHERSubjectPreemptsTheRunningPrewarm()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        var heard = new List<string>();
        await vm.OpenAllPartsInBlenderAsync(Subject("Karst", 1201, "KarstSSR01"), new[] { Recipe("body") }, new Sink(heard));

        Assert.Contains("Vesna/VesnaSSR01", jobs.Cancelled);
        // The wait is never blank: the claim says what it is waiting on from its first instant.
        Assert.Equal(MainWindowViewModel.PrewarmWaitLine(cancelling: true), heard.FirstOrDefault());
        // The open then ran its own path rather than lining up behind the guess; with no install it stops here.
        Assert.Equal("Game files aren't loaded yet.", heard.Skip(1).FirstOrDefault());
    }

    [Fact]
    public async Task OpeningTHESubjectBeingPrewarmedWaitsForIt_RatherThanRestartingIt()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        var heard = new List<string>();
        var open = vm.OpenAllPartsInBlenderAsync(Subject("Vesna", 1101, "VesnaSSR01"),
            new[] { Recipe("body") }, new Sink(heard));

        Assert.False(open.IsCompleted);                 // an open of the whole outfit wants the whole job
        // …and says so rather than sitting blank until the job it is waiting on reports something.
        Assert.Equal(MainWindowViewModel.PrewarmWaitLine(cancelling: false), heard.FirstOrDefault());
        Assert.Empty(jobs.Cancelled);
        jobs.Complete("Vesna/VesnaSSR01");
        await open;
        Assert.Equal(new[] { "Vesna/VesnaSSR01" }, jobs.Started);   // exactly once
    }

    /// <summary>Opening ONE part alone preempts its own subject's combine, and the wait leads with the thing
    /// the modder just clicked. A bare "stopping outfit preparation" reads as the app doing something
    /// else with the click, on the one route whose whole point is not waiting for the outfit.</summary>
    [Fact]
    public async Task OpeningAPartAlone_PreemptsItsOwnSubjectsPrewarm_AndLeadsWithTheOpen()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        var heard = new List<string>();
        await vm.OpenPartAloneInBlenderAsync(Subject("Vesna", 1101, "VesnaSSR01"), Recipe("body"), new Sink(heard));

        Assert.Contains("Vesna/VesnaSSR01", jobs.Cancelled);
        Assert.Equal("Opening body · stopping outfit preparation", heard.FirstOrDefault());
    }

    /// <summary>The two forms of the wait line. With no lead the wait is the whole sentence; with one the
    /// action leads and the wait trails it past the pane's separator.</summary>
    [Fact]
    public void TheWaitLineLeadsWithTheActionWhenItHasOne()
    {
        Assert.Equal("Stopping outfit preparation…", MainWindowViewModel.PrewarmWaitLine(cancelling: true));
        Assert.Equal("Finishing outfit preparation…", MainWindowViewModel.PrewarmWaitLine(cancelling: false));
        Assert.Equal("Opening hair · stopping outfit preparation",
            MainWindowViewModel.PrewarmWaitLine(cancelling: true, "Opening hair"));
        Assert.Equal("Opening hair · finishing outfit preparation",
            MainWindowViewModel.PrewarmWaitLine(cancelling: false, "Opening hair"));
    }

    [Fact]
    public async Task OpeningONEMapPreemptsItsOwnSubjectsPrewarm()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));

        // One PNG is not worth waiting out the outfit's combine — the guess gives the machinery back.
        await vm.OpenMapInEditorAsync(Subject("Vesna", 1101, "VesnaSSR01"), "map", "abc",
            Array.Empty<string>(), new Sink(new List<string>()));

        Assert.Contains("Vesna/VesnaSSR01", jobs.Cancelled);
    }

    // ---- a part that won't prepare keeps its own reason ----

    [Fact]
    public async Task APartThatCouldNotBePreparedKeepsItsOwnReason_NotAMissingMeshOne()
    {
        using var temp = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Path.Combine(temp.Root, ModNaming.Slug("Open Reason"));
        Directory.CreateDirectory(root);
        var seed = new ModProject { RootDir = root };
        seed.Info.Name = "Open Reason";
        seed.Save();

        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        Assert.True(await vm.OpenModAsync(root));

        var heard = new List<string>();
        var recipe = Recipe("body");
        // No game files, so the named part can't be prepared. The session description would then find no
        // mesh for it — reporting that instead would blame the mesh for a preparation that never ran.
        await vm.OpenPartInBlenderAsync(Subject("Vesna", 1101, "VesnaSSR01"), recipe,
            Array.Empty<RecipePart>(), new Sink(heard));

        Assert.Equal(new[] { "Game files aren't loaded yet." }, heard.ToArray());
    }

    // ---- a close the modder backs out of keeps the guess ----

    [Fact]
    public void OnlyACloseThatActuallyLeavesDropsTheGuess()
    {
        // The two passes the window leaves on: the re-close a confirmed flow makes, and the one with nothing
        // to ask. Every other pass ends in a prompt, and a prompt can be declined.
        Assert.True(MainWindowViewModel.CloseDropsSpeculativeWork(
            closeConfirmed: true, workInFlight: false, canCloseSilently: false));
        Assert.True(MainWindowViewModel.CloseDropsSpeculativeWork(false, workInFlight: false, canCloseSilently: true));
        // work in flight is asked about first, so the silent-close answer never decides that pass
        Assert.False(MainWindowViewModel.CloseDropsSpeculativeWork(false, workInFlight: true, canCloseSilently: true));
        // unsaved changes: the save-first prompt is next, and nothing is dropped on the way to it
        Assert.False(MainWindowViewModel.CloseDropsSpeculativeWork(false, workInFlight: false, canCloseSilently: false));
    }

    [Fact]
    public void ACloseTheModderBacksOutOfKeepsThePrewarmsItWasPreparing()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));
        vm.PrewarmSubject(Subject("Karst", 1201, "KarstSSR01"));

        // The dirty gate cancels this close and asks. The app is still open on the very subjects the guesses
        // are preparing, so nothing is dropped on the way to the prompt.
        Assert.False(MainWindowViewModel.CloseDropsSpeculativeWork(
            closeConfirmed: false, workInFlight: false, canCloseSilently: false));
        Assert.Equal(new MainWindowViewModel.SubjectKey("Vesna", "VesnaSSR01"), vm.Prewarm.RunningKey);
        Assert.Equal(new[] { new MainWindowViewModel.SubjectKey("Karst", "KarstSSR01") }, vm.Prewarm.Pending);

        // …and the go-ahead re-closes with the flag set, which is the pass that drops them.
        Assert.True(MainWindowViewModel.CloseDropsSpeculativeWork(true, false, false));
        vm.CancelSpeculativeWork();
        Assert.Contains("Vesna/VesnaSSR01", jobs.Cancelled);
        Assert.Empty(vm.Prewarm.Pending);
    }

    /// <summary>The property behind the close guard, on the flags a test can actually raise: a batch, an
    /// asked-for materialize and a mod build each hold it, and each lets go again. The speculative half is
    /// the control — a guess costs the modder no wait, so it is never work the close asks about.</summary>
    [Fact]
    public void TheCloseGuardReadsTheHoldersThatRaiseItsFlags()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        Assert.False(vm.IsWorkInFlight);

        vm.Workbench.IsMaterializingAll = true;
        Assert.True(vm.IsWorkInFlight);
        vm.Workbench.IsMaterializingAll = false;
        Assert.False(vm.IsWorkInFlight);

        vm.IsModBuilding = true;
        Assert.True(vm.IsWorkInFlight);
        vm.IsModBuilding = false;

        using (vm.BeginMaterialize(Subject("Vesna", 1101, "VesnaSSR01"), disarmWatchers: false))
            Assert.True(vm.IsWorkInFlight);
        Assert.False(vm.IsWorkInFlight);

        using (vm.BeginMaterialize(Subject("Vesna", 1101, "VesnaSSR01"), disarmWatchers: false, background: true))
            Assert.False(vm.IsWorkInFlight);
    }

    // ---- the watchers a speculative run leaves up ----

    [Fact]
    public void ASpeculativeMaterializeDropsNeitherWatcher()
    {
        Assert.True(MainWindowViewModel.MaterializeDisarmsWatchers(disarmWatchers: true, background: false));
        Assert.False(MainWindowViewModel.MaterializeDisarmsWatchers(disarmWatchers: true, background: true));
        Assert.False(MainWindowViewModel.MaterializeDisarmsWatchers(disarmWatchers: false, background: false));
    }

    [Fact]
    public async Task AnImageEditorSaveLandingDuringASpeculativePrewarmStillMarksItsTargetEdited()
    {
        using var temp = new TempGame();
        using var settings = new SettingsSnapshot();
        var root = Path.Combine(temp.Root, ModNaming.Slug("Prewarm Watch"));   // slug-matched: no autosave rename
        var png = Path.Combine(root, "textures", "map.abc.png");
        Directory.CreateDirectory(Path.GetDirectoryName(png)!);
        File.WriteAllBytes(png, new byte[] { 1, 2, 3, 4 });
        var seed = new ModProject { RootDir = root };
        seed.Info.Name = "Prewarm Watch";
        seed.Targets.Add(new ProjectTarget
        {
            AssetType = "Texture2D", Bundle = "abc", ObjectName = "map", ReplaceFile = "textures/map.abc.png",
        });
        seed.Save();

        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        Assert.True(await vm.OpenModAsync(root));   // arms both watchers on the mod folder

        // A speculative outfit prewarm is running. The modder is in an image editor on a map of ANOTHER
        // subject and hits save — the watch is up, so it lands.
        using (vm.BeginMaterialize(Subject("Vesna", 1101, "VesnaSSR01"), disarmWatchers: true, background: true))
        {
            File.WriteAllBytes(png, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });
            // The watcher raises on its own thread and posts the mark to the UI thread; nothing pumps that
            // queue in a test host, so the poll drains it.
            await Until(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return ModProject.Load(root).Targets[0].Edited;
            }, "the save to reach the target");
        }
    }

    // ---- what the status bar says about it ----

    [Fact]
    public async Task TheStatusBarSaysPreparationIsUpForAsLongAsItIs()
    {
        using var jobs = new Jobs();
        var vm = new MainWindowViewModel(startLoad: false, jobs.Run);
        Assert.Equal("", vm.BackgroundStatus.Text);

        vm.PrewarmSubject(Subject("Vesna", 1101, "VesnaSSR01"));
        await Until(() => { Dispatcher.UIThread.RunJobs(); return vm.BackgroundStatus.Text == "Preparing outfits…"; },
            "the preparation line");

        jobs.Complete("Vesna/VesnaSSR01");
        await Until(() => { Dispatcher.UIThread.RunJobs(); return vm.BackgroundStatus.Text == ""; },
            "the line to clear");
    }

    // ---- helpers ----

    private static RecipePart Recipe(string token) =>
        new(token, $"c_VesnaSSR01_slg_{token}_lod0", $"addr/{token}", Array.Empty<RecipeTierSlot>());

    private static WorkbenchSubjectRef Subject(string character, long id, string stem)
    {
        var outfit = new Outfit(id, stem, OutfitKind.Base);
        return new WorkbenchSubjectRef(character, outfit.Stem, outfit.MeshPrefix, outfit);
    }

    /// <summary>A Pick tree row pair, the shape <see cref="MainWindowViewModel.AddSubject"/> takes.</summary>
    private static (CharacterVm Character, OutfitVm Outfit) Row(string character, long id, string stem)
    {
        var (c, outfits) = Rows(character, id, stem);
        return (c, outfits[0]);
    }

    /// <summary>One character row carrying several outfit rows — what the character checkbox grabs.</summary>
    private static (CharacterVm Character, IReadOnlyList<OutfitVm> Outfits) Rows(
        string character, long id, params string[] stems)
    {
        var outfits = stems.Select(s => new Outfit(id, s, OutfitKind.Base)).ToList();
        var model = new Character(id, character, character, id, 0, outfits);
        var c = new CharacterVm(model, (_, _) => { }, (_, _) => { });
        var rows = outfits.Select(o => new OutfitVm(o, new[] { "body" }, _ => { })).ToList();
        foreach (var o in rows) c.Outfits.Add(o);
        return (c, rows);
    }

    /// <summary>A mods library under the run's temp folder, so a mint lands there rather than in the
    /// machine's real one. Restores the settings file it repoints, and takes the library with it.</summary>
    private sealed class TempLibrary : IDisposable
    {
        private readonly SettingsSnapshot _settings = new();

        public string Root { get; }

        public TempLibrary()
        {
            // No Random/Date in tests — the per-test temp name is a Guid.
            Root = Path.Combine(Path.GetTempPath(), "remold-tests", Guid.NewGuid().ToString("N"), "mods");
            Directory.CreateDirectory(Root);
            new LabSettings { LibraryRoot = Root }.Save();
        }

        public void Dispose()
        {
            _settings.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(Root)!, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A tree-only workbench VM: no game behind it, so what it can be driven through is the
    /// selection rule itself.</summary>
    private static WorkbenchVm TreeVm(VisitShell shell) => new(
        project: () => new ModProject(),
        vfs: () => null,
        friendly: () => FriendlyNames.Empty,
        roster: () => Array.Empty<Character>(),
        tryDeobfuscate: _ => null,
        catalog: null,
        shell: shell);

    private static WorkbenchSubjectRef WbSubject(string character, string stem)
    {
        var outfit = new Outfit(0, stem, OutfitKind.Base);
        return new WorkbenchSubjectRef(character, outfit.Stem, outfit.MeshPrefix, outfit);
    }

    /// <summary>One subject's tree in the shape the builder gives it: the header, a part under it, a
    /// material under the part, and the Skeleton row beside the parts.</summary>
    private static WorkbenchNodeVm SubjectTree(WorkbenchSubjectRef subject)
    {
        var part = new WorkbenchNodeVm
        {
            Kind = WorkbenchNodeKind.Part, Title = "body", Subject = subject, Recipe = Recipe("body"),
        };
        part.Children.Add(new WorkbenchNodeVm
        {
            Kind = WorkbenchNodeKind.Material, Title = "mat", Subject = subject, MaterialIndex = 0,
        });
        var root = new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Subject, Title = "subject", Subject = subject };
        root.Children.Add(part);
        root.Children.Add(new WorkbenchNodeVm { Kind = WorkbenchNodeKind.Skeleton, Title = "Skeleton", Subject = subject });
        return root;
    }

    private static WorkbenchNodeVm NodeOfKind(WorkbenchNodeVm root, WorkbenchNodeKind kind) => kind switch
    {
        WorkbenchNodeKind.Subject => root,
        WorkbenchNodeKind.Part => root.Children.First(c => c.Kind == WorkbenchNodeKind.Part),
        WorkbenchNodeKind.Skeleton => root.Children.First(c => c.Kind == WorkbenchNodeKind.Skeleton),
        _ => root.Children.First(c => c.Kind == WorkbenchNodeKind.Part).Children[0],
    };

    /// <summary>Records the subjects the tree hands the shell to prepare, as <c>character/stem</c>.</summary>
    private sealed class VisitShell : IWorkbenchShell
    {
        public List<string> Visited { get; } = new();

        public void PrewarmSubject(WorkbenchSubjectRef s) => Visited.Add($"{s.Character}/{s.Stem}");

        // ---- unused by these ----
        public Task<PartMaterializeOutcome> MaterializePartAsync(WorkbenchSubjectRef s, RecipePart r, IProgress<string> p, CancellationToken c) => Task.FromResult(PartMaterializeOutcome.Ready());
        public Task<bool> MaterializeTextureAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<string> o, IProgress<string> p, CancellationToken c) => Task.FromResult(true);
        public Task OpenPartInBlenderAsync(WorkbenchSubjectRef s, RecipePart r, IReadOnlyList<RecipePart> outfit, IProgress<string> p) => Task.CompletedTask;
        public Task OpenPartAloneInBlenderAsync(WorkbenchSubjectRef s, RecipePart r, IProgress<string> p) => Task.CompletedTask;
        public Task OpenAllPartsInBlenderAsync(WorkbenchSubjectRef s, IReadOnlyList<RecipePart> r, IProgress<string> p) => Task.CompletedTask;
        public Task OpenMapInEditorAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<string> o, IProgress<string> p) => Task.CompletedTask;
        public Task OpenAuthoredMapAsync(string authoredPath, IProgress<string> p) => Task.CompletedTask;
        public Task<int> MaterializeAllAsync(WorkbenchSubjectRef s, IReadOnlyList<MaterializeItem> i, IProgress<string> p, CancellationToken c) => Task.FromResult(0);
        public Task RevertPartAsync(WorkbenchSubjectRef s, string t, IProgress<string> p) => Task.CompletedTask;
        public Task OpenMapUvGuideAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<(string, string, int, string?)> u, IProgress<string> p) => Task.CompletedTask;
        public Task RevertMapAsync(WorkbenchSubjectRef s, string t, string b, IProgress<string> p) => Task.CompletedTask;
        public Task ApplyDroppedPngAsync(WorkbenchSubjectRef s, string t, string b, IReadOnlyList<string> o, string path, IProgress<string> p) => Task.CompletedTask;
        public Task ApplyDroppedPngToAuthoredAsync(string authoredPath, string part, string role, string path, IProgress<string> p) => Task.CompletedTask;
        public Task<bool> ConfirmApplyDroppedPngAsync(DroppedPngConfirm ask) => Task.FromResult(false);
        public Task ApplyDroppedPngToDonorMapAsync(WorkbenchSubjectRef s, DonorMapDrop d, string r, string p, IProgress<string> st) => Task.CompletedTask;
        public void ShowSubjectInFolder(WorkbenchSubjectRef s) { }
        public Task RemoveSubjectAsync(WorkbenchSubjectRef s) => Task.CompletedTask;
        public Task CopyTextAsync(string? text) => Task.CompletedTask;
        public void GoToBuild() { }
        public void AutoSaveProject() { }
        public string? AdoptSubjectTextureEdits(WorkbenchSubjectRef s, Remold.Core.Workbench.SubjectModel m) => null;
    }

    private sealed class Sink : IProgress<string>
    {
        private readonly List<string> _into;
        public Sink(List<string> into) => _into = into;
        public void Report(string value) { lock (_into) _into.Add(value); }
    }

    /// <summary>The queue's job under the test's control: every job blocks until completed or cancelled, so
    /// the wiring is observed rather than raced. Keys read back as <c>character/stem</c>.</summary>
    private sealed class Jobs : IDisposable
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _started = new();
        private readonly List<string> _cancelled = new();

        public string[] Started { get { lock (_lock) return _started.ToArray(); } }
        public string[] Cancelled { get { lock (_lock) return _cancelled.ToArray(); } }

        /// <summary>Keep a cancelled job blocked until <see cref="Complete"/>, so a test can observe the
        /// window between asking a job to stop and it unwinding. The real job's checkpoints are a whole part
        /// apart, which is what makes that window wide enough to matter.</summary>
        public bool HoldOnCancel { get; init; }

        public Task Run(MainWindowViewModel.SubjectKey key, IProgress<string> status, CancellationToken ct)
        {
            var name = $"{key.Character}/{key.Stem}";
            TaskCompletionSource gate;
            lock (_lock)
            {
                _started.Add(name);
                gate = _gates[name] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            ct.Register(() =>
            {
                lock (_lock) _cancelled.Add(name);
                if (!HoldOnCancel) gate.TrySetResult();
            });
            return gate.Task;
        }

        public void Complete(string name)
        {
            TaskCompletionSource gate;
            lock (_lock) gate = _gates[name];
            gate.TrySetResult();
        }

        /// <summary>End a job the way a preparation that broke ends: the queue swallows the throw and never
        /// re-runs the key.</summary>
        public void Fail(string name)
        {
            TaskCompletionSource gate;
            lock (_lock) gate = _gates[name];
            gate.TrySetException(new InvalidOperationException("preparation failed"));
        }

        /// <summary>Release anything still blocked, so a failed assertion can't leave a job parked.</summary>
        public void Dispose()
        {
            lock (_lock) foreach (var g in _gates.Values) g.TrySetResult();
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }
}
