using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Remold.App.ViewModels;
using Remold.App.ViewModels.Workbench;
using Remold.Core.Export;
using Remold.Core.Model;
using Remold.Core.Workbench;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The prewarm queue: speculative outfit preparation started from Pick, taken away from that work the moment
/// somebody actually asks for something. These pin the semantics the modder feels — an open never lines up
/// behind a guess, never re-runs a guess that is already preparing the very subject they clicked, and never
/// keeps a subject they removed alive — without materializing anything.
/// </summary>
public class PrewarmQueueTests
{
    private static PrewarmQueue<string> Queue(Fake fake) =>
        new(fake.Run, StringComparer.OrdinalIgnoreCase);

    // ---- a claim that waits says so ----

    [Fact]
    public async Task AClaimOnAnIdleQueueSignalsNoWait()
    {
        var fake = new Fake();
        var q = Queue(fake);
        var waits = new List<bool>();

        using var claim = await q.ClaimAsync("A", onWait: c => waits.Add(c));

        Assert.Empty(waits);   // resolved at once — nothing for the caller to report
    }

    [Fact]
    public async Task AClaimThatPreemptsAnotherKeySignalsACancellingWait()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A");
        var waits = new List<bool>();

        var claim = q.ClaimAsync("B", onWait: c => waits.Add(c));

        // Signalled BEFORE the drain: a speculative job only gives up at its next checkpoint, which is the
        // whole point of having something to say. (Only the wait signal is asserted — whether the claim
        // has ALSO completed by now is a thread-pool race, not part of the contract.)
        Assert.Equal(new[] { true }, waits);
        await Until(() => fake.Cancelled.Contains("A"), "A to observe the cancel");
        fake.Complete("A");
        (await claim).Dispose();
    }

    [Fact]
    public async Task AClaimThatWaitsOutItsOwnKeySignalsANonCancellingWait()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A");
        var waits = new List<bool>();

        var claim = q.ClaimAsync("A", onWait: c => waits.Add(c));

        Assert.Equal(new[] { false }, waits);
        fake.Complete("A");
        (await claim).Dispose();
    }

    // ---- one job at a time, in order ----

    [Fact]
    public async Task OneJobRunsAtATime_TheRestWaitInOrder()
    {
        var fake = new Fake();
        var q = Queue(fake);

        q.Enqueue("A"); q.Enqueue("B"); q.Enqueue("C");

        Assert.Equal("A", q.RunningKey);
        Assert.Equal(new[] { "B", "C" }, q.Pending);

        fake.Complete("A");
        await Until(() => q.RunningKey == "B", "B to start");
        fake.Complete("B");
        await Until(() => q.RunningKey == "C", "C to start");
        fake.Complete("C");
        await Until(() => q.IsIdle, "the queue to drain");

        Assert.Equal(1, fake.MaxLive);   // never two heavy jobs on the machine at once
        Assert.Equal(new[] { "A", "B", "C" }, fake.Started);
    }

    [Fact]
    public void EnqueueingTheSameSubjectAgainIsANoOp()
    {
        var fake = new Fake();
        var q = Queue(fake);

        q.Enqueue("A");
        q.Enqueue("A");     // already running
        q.Enqueue("a");     // …and subject identity is case-insensitive, like the ledger's
        q.Enqueue("B");
        q.Enqueue("b");     // already waiting

        Assert.Equal("A", q.RunningKey);
        Assert.Equal(new[] { "B" }, q.Pending);
        Assert.Equal(new[] { "A" }, fake.Started);
    }

    // ---- an explicit open jumps the queue ----

    [Fact]
    public async Task AnOpenPreemptsAnUnrelatedSubjectsPrewarm_RatherThanWaitingOnIt()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A"); q.Enqueue("B");
        Assert.Equal("A", q.RunningKey);

        // the modder opens C while A is speculatively preparing
        using (await q.ClaimAsync("C"))
        {
            Assert.Contains("A", fake.Cancelled);         // A gave the machinery back
            Assert.Null(q.RunningKey);                    // …and nothing speculative runs under the claim
            Assert.Equal(new[] { "A", "B" }, q.Pending);  // A is not lost: it resumes at the head
            q.Enqueue("D");
            Assert.Null(q.RunningKey);                    // a subject picked mid-open still waits its turn
        }

        await Until(() => q.RunningKey == "A", "A to resume after the open");
        Assert.DoesNotContain("C", fake.Started);         // the open did C's work itself; the queue never re-ran it
    }

    [Fact]
    public async Task AnOpenOfAWaitingSubjectTakesItOutOfTheQueue()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A"); q.Enqueue("B"); q.Enqueue("C");

        using (await q.ClaimAsync("C")) { }   // the modder opened the third subject they picked

        await Until(() => q.RunningKey == "A", "A to resume");
        Assert.Equal(new[] { "B" }, q.Pending);
        Assert.DoesNotContain("C", fake.Started);
    }

    [Fact]
    public async Task AnOpenWithNothingPrewarmingResolvesWithoutWaiting()
    {
        // The wiring guarantee: with the queue idle — prewarm disabled, absent, or already drained — the
        // claim is not a step the open has to wait through.
        var fake = new Fake();
        var q = Queue(fake);

        var claim = q.ClaimAsync("A");
        Assert.True(claim.IsCompletedSuccessfully);

        using (await claim) Assert.Empty(fake.Started);
    }

    // ---- the same subject is awaited, never restarted ----

    [Fact]
    public async Task AnOpenOfTheSubjectAlreadyPrewarmingAwaitsIt_AndSeesItsProgress()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A");

        var heard = new List<string>();
        fake.Report("A", "before the claim");            // speculative work is silent: nobody is listening

        var claim = q.ClaimAsync("A", new Sink(heard));
        Assert.False(claim.IsCompleted);                 // the open waits on the work already under way…
        Assert.Equal(new[] { "A" }, fake.Started);       // …which is NOT restarted

        fake.Report("A", "Building the outfit rig for Blender…");
        fake.Complete("A");
        using (await claim) { }

        Assert.Equal(new[] { "Building the outfit rig for Blender…" }, heard);
        Assert.Single(fake.Started);
        Assert.Empty(fake.Cancelled);                    // awaited, never cancelled
    }

    // ---- a one-file action takes the machinery back instead of waiting the outfit out ----

    [Fact]
    public async Task AOneFileActionPreemptsItsOwnSubjectsPrewarm_WhichResumesAfterwards()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A");

        // opening one map of A: a single PNG is not worth waiting out A's whole outfit combine
        using (await q.ClaimAsync("A", preemptSameKey: true))
        {
            Assert.Contains("A", fake.Cancelled);
            Assert.Null(q.RunningKey);
            Assert.Equal(new[] { "A" }, q.Pending);   // the guess is not lost, only postponed
        }

        await Until(() => q.RunningKey == "A", "A to resume after the one-file action");
        Assert.Equal(new[] { "A", "A" }, fake.Started);
    }

    [Fact]
    public async Task APreemptedKeyANOTHERClaimIsAlreadyDoingIsDropped_NotResumed()
    {
        // The modder opened A (that claim is doing A's work explicitly), then opened B. B preempts the A
        // job, but resuming A afterwards would redo — and republish — what the open of A is holding.
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A");

        var openA = q.ClaimAsync("A");            // attaches to the running job
        Assert.False(openA.IsCompleted);
        var openB = q.ClaimAsync("B");            // preempts it out from under the attach

        using (await openB) { }
        using (await openA) { }

        Assert.DoesNotContain("A", q.Pending);
        await Until(() => q.IsIdle, "the queue to settle");
        Assert.Equal(new[] { "A" }, fake.Started);   // A ran once, speculatively, and never again
    }

    // ---- removal cancels ----

    [Fact]
    public async Task RemovingTheRunningSubjectCancelsIt_AndTheQueueMovesOn()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A"); q.Enqueue("B");

        await q.CancelAsync("A");   // the modder unchecked A in Pick

        Assert.Contains("A", fake.Cancelled);
        await Until(() => q.RunningKey == "B", "B to start");
    }

    [Fact]
    public async Task RemovingAWaitingSubjectDropsIt_AndItNeverRuns()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A"); q.Enqueue("B"); q.Enqueue("C");

        await q.CancelAsync("c");   // case-insensitive, like every other subject comparison

        Assert.Equal(new[] { "B" }, q.Pending);
        fake.Complete("A");
        Assert.DoesNotContain("C", fake.Started);
    }

    [Fact]
    public async Task ASubjectRemovedWhilePreemptedIsNotResumedAfterTheOpen()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A"); q.Enqueue("B");

        using (await q.ClaimAsync("Z"))
        {
            Assert.Equal(new[] { "A", "B" }, q.Pending);
            await q.CancelAsync("A");            // unchecked while the open held the queue
            Assert.Equal(new[] { "B" }, q.Pending);
        }

        await Until(() => q.RunningKey == "B", "B to run instead");
        Assert.Equal(new[] { "A", "B" }, fake.Started);   // A ran once before the preempt, never again
    }

    [Fact]
    public async Task ClosingTheProjectDropsEverything()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A"); q.Enqueue("B"); q.Enqueue("C");

        q.CancelAll();

        Assert.Contains("A", fake.Cancelled);
        await Until(() => q.IsIdle, "the queue to empty");
        Assert.Equal(new[] { "A" }, fake.Started);
    }

    // ---- failure is silent ----

    [Fact]
    public async Task AJobThatThrowsIsSwallowedAndTheNextOneStillRuns()
    {
        var fake = new Fake { ThrowFor = "A" };
        var q = Queue(fake);

        q.Enqueue("A"); q.Enqueue("B");

        await Until(() => q.RunningKey == "B", "B to start after A blew up");
    }

    [Fact]
    public async Task AnOpenAfterAFailedPrewarmStillResolves()
    {
        var fake = new Fake { ThrowFor = "A" };
        var q = Queue(fake);
        q.Enqueue("A");

        using (await q.ClaimAsync("A")) { }   // the open takes over and will redo the work itself
    }

    [Fact]
    public async Task AClaimAttachedToAFailingJobObservesTheFailure()
    {
        var fake = new Fake { ThrowAfterGateFor = "A" };
        var q = Queue(fake);
        q.Enqueue("A");
        await Until(() => q.RunningKey == "A", "A to start");

        var claiming = q.ClaimAsync("A");   // attaches: same key, no preempt
        fake.Complete("A");                 // the job unblocks, then throws
        using var claim = await claiming;

        // The claim resolves rather than throwing — the caller redoes the work either way — but the failure
        // is on the claim, not swallowed into a wait that looks like a success.
        Assert.NotNull(claim.AttachedJobFault);
        Assert.IsType<InvalidOperationException>(claim.AttachedJobFault);
    }

    [Fact]
    public async Task AClaimOnAJobThatSucceedsObservesNoFailure()
    {
        var fake = new Fake();
        var q = Queue(fake);
        q.Enqueue("A");
        await Until(() => q.RunningKey == "A", "A to start");

        var claiming = q.ClaimAsync("A");
        fake.Complete("A");
        using var claim = await claiming;

        Assert.Null(claim.AttachedJobFault);
    }

    // ---- helpers ----

    private sealed class Sink : IProgress<string>
    {
        private readonly List<string> _into;
        public Sink(List<string> into) => _into = into;
        public void Report(string value) { lock (_into) _into.Add(value); }
    }

    /// <summary>A runner under the test's control: every job blocks until the test completes or cancels it,
    /// so queue ordering is observed rather than raced.</summary>
    private sealed class Fake
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IProgress<string>> _sinks = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _started = new();
        private readonly List<string> _cancelled = new();
        private int _live;

        /// <summary>A key whose job throws instead of running.</summary>
        public string? ThrowFor { get; init; }

        /// <summary>A key whose job runs to its gate and THEN throws, so a claim has a live job to attach
        /// to before the failure lands.</summary>
        public string? ThrowAfterGateFor { get; init; }

        public string[] Started { get { lock (_lock) return _started.ToArray(); } }
        public string[] Cancelled { get { lock (_lock) return _cancelled.ToArray(); } }
        public int MaxLive { get; private set; }

        public Task Run(string key, IProgress<string> status, CancellationToken ct)
        {
            TaskCompletionSource gate;
            lock (_lock)
            {
                _started.Add(key);
                _live++;
                if (_live > MaxLive) MaxLive = _live;
                gate = _gates[key] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _sinks[key] = status;
            }
            if (string.Equals(key, ThrowFor, StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock) _live--;
                throw new InvalidOperationException("prewarm blew up");
            }
            ct.Register(() => { lock (_lock) _cancelled.Add(key); gate.TrySetResult(); });
            return Finish(gate.Task,
                string.Equals(key, ThrowAfterGateFor, StringComparison.OrdinalIgnoreCase));
        }

        private async Task Finish(Task gate, bool throwAfter)
        {
            try { await gate; }
            finally { lock (_lock) _live--; }
            if (throwAfter) throw new InvalidOperationException("prewarm blew up");
        }

        public void Complete(string key)
        {
            TaskCompletionSource gate;
            lock (_lock) gate = _gates[key];
            gate.TrySetResult();
        }

        public void Report(string key, string message)
        {
            IProgress<string> sink;
            lock (_lock) sink = _sinks[key];
            sink.Report(message);
        }
    }

    /// <summary>Wait for a queue transition that lands on a worker thread. The queue hands control on
    /// without a completion signal of its own, so the observation is polled.</summary>
    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(5);
        }
    }
}

/// <summary>
/// The wiring guarantee at the view-model seam: an open with nothing prewarmed runs the path it always ran,
/// reporting the same first line. The claim in front of it is not a behavior.
/// </summary>
[Collection("Dispatcher")]
public class PrewarmOpenWiringTests
{
    [Fact]
    public async Task OpenAllWithNoPrewarm_ReportsExactlyWhatItReportedBefore()
    {
        var vm = new MainWindowViewModel(startLoad: false);
        var outfit = new Outfit(1101, "VesnaSSR01", OutfitKind.Base);
        var subject = new WorkbenchSubjectRef("Vesna", outfit.Stem, outfit.MeshPrefix, outfit);
        var recipe = new RecipePart("body", "c_VesnaSSR01_slg_body_lod0", "addr/body",
            Array.Empty<RecipeTierSlot>());
        var heard = new List<string>();

        await vm.OpenAllPartsInBlenderAsync(subject, new[] { recipe }, new Sink(heard));

        // No game files loaded, so the first part materialize refuses — the same first line as before the
        // queue existed, and reached without waiting on anything.
        Assert.Equal("Game files aren't loaded yet.", heard.FirstOrDefault());
    }

    private sealed class Sink : IProgress<string>
    {
        private readonly List<string> _into;
        public Sink(List<string> into) => _into = into;
        public void Report(string value) { lock (_into) _into.Add(value); }
    }
}
