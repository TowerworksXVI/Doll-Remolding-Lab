using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
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
/// The ROUTE the model-arrival adoption actually travels: the Edit tree's off-thread build lands, sweeps
/// every subject it built, and writes the pane's one line. What the sweep took has to survive that write —
/// the tree build assigns the line itself, so a sweep that said its own piece would be talking into a line
/// about to be overwritten, and with several subjects only the last one's would have stood at all.
/// </summary>
[Collection("Dispatcher")]
public class WorkbenchAdoptionRouteTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "remold-adoptroute-" + Guid.NewGuid().ToString("N"));

    public WorkbenchAdoptionRouteTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Answers each subject's sweep with a line of its own, and records the order it was asked
    /// in — the shell's real seam takes the adoptions and hands back what it took.</summary>
    private sealed class AdoptingShell : IWorkbenchShell
    {
        /// <summary>character/stem → the line that subject's sweep gives back. A subject with no entry took
        /// nothing and answers null.</summary>
        public Dictionary<string, string> Lines { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Swept { get; } = new();

        public string? AdoptSubjectTextureEdits(WorkbenchSubjectRef s, SubjectModel m)
        {
            Swept.Add($"{s.Character}/{s.Stem}");
            return Lines.TryGetValue($"{s.Character}/{s.Stem}", out var line) ? line : null;
        }

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
        public void PrewarmSubject(WorkbenchSubjectRef s) { }
        public void ShowSubjectInFolder(WorkbenchSubjectRef s) { }
        public Task RemoveSubjectAsync(WorkbenchSubjectRef s) => Task.CompletedTask;
        public Task CopyTextAsync(string? text) => Task.CompletedTask;
        public void GoToBuild() { }
        public void AutoSaveProject() { }
    }

    /// <summary>A workbench over a game with nothing in it: the tree still builds a node per picked subject
    /// (carrying the unreadable-subject problem line), which is all this route needs — what is under test is
    /// what the build does with what the sweep hands back.</summary>
    private (WorkbenchVm Vm, AdoptingShell Shell) Pane(params string[] stems)
    {
        var project = new ModProject { RootDir = _root };
        foreach (var stem in stems)
            project.Selection.Add(new SelectionEntry { Character = "Vesna", Outfit = stem });
        // one manifest entry so the vfs loads at all; nothing reads the bundle behind it
        var vfs = TestVfs.Create(_root, Array.Empty<(string, string)>(), null, ("b0", new string('a', 32)));
        var shell = new AdoptingShell();
        return (new WorkbenchVm(
            project: () => project,
            vfs: () => vfs,
            friendly: () => FriendlyNames.Empty,
            roster: () => Array.Empty<Character>(),
            tryDeobfuscate: _ => null,
            catalog: null,
            shell: shell,
            thumbnailRoot: Path.Combine(_root, "thumbs")), shell);
    }

    /// <summary>Drive the tree build to its landing. The heavy half runs off-thread and hands back through
    /// ONE dispatcher post, so the wait has to keep draining the dispatcher — and has to sleep rather than
    /// spin between drains, or this thread starves the very pool the build is queued on.</summary>
    private static bool PumpUntil(Func<bool> condition)
    {
        var since = Stopwatch.StartNew();
        while (since.Elapsed < TimeSpan.FromSeconds(10))
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }

    [Fact]
    public void ABuildThatAdoptedSomething_LeavesTheAdoptedLineStanding()
    {
        var (vm, shell) = Pane("VesnaSSR01");
        shell.Lines["Vesna/VesnaSSR01"] = "Adopted as body's replacement Base color map.";

        vm.Activate();

        Assert.True(PumpUntil(() => !vm.IsBuilding && vm.Nodes.Count == 1), "the tree to land");
        Assert.Equal("Adopted as body's replacement Base color map.", vm.Status);
    }

    /// <summary>Several subjects join into the one line the pane shows, the way one subject's own halves
    /// do — the sweep runs per subject, and only the last one's line would otherwise have survived.</summary>
    [Fact]
    public void EverySubjectsAdoptionReachesTheLine_NotJustTheLast()
    {
        var (vm, shell) = Pane("VesnaSSR01", "VesnaSSR02");
        shell.Lines["Vesna/VesnaSSR01"] = "Adopted as body's replacement Base color map.";
        shell.Lines["Vesna/VesnaSSR02"] = "Adopted as hair's replacement Base color map.";

        vm.Activate();

        Assert.True(PumpUntil(() => !vm.IsBuilding && vm.Nodes.Count == 2), "the tree to land");
        Assert.Equal(new[] { "Vesna/VesnaSSR01", "Vesna/VesnaSSR02" }, shell.Swept);
        Assert.Equal("Adopted as body's replacement Base color map. "
            + "Adopted as hair's replacement Base color map.", vm.Status);
    }

    /// <summary>A build with nothing to announce still reports its size: the adoption line REPLACES the
    /// count, it doesn't remove it from builds that have no news.</summary>
    [Fact]
    public void ABuildThatAdoptedNothing_StillReportsTheTreesSize()
    {
        var (vm, _) = Pane("VesnaSSR01", "VesnaSSR02");

        vm.Activate();

        Assert.True(PumpUntil(() => !vm.IsBuilding && vm.Nodes.Count == 2), "the tree to land");
        Assert.Equal("2 items", vm.Status);
    }
}
