using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Remold.Core.Workbench;

/// <summary>A held claim on a <see cref="PrewarmQueue{TKey}"/>. Disposing releases it.</summary>
public interface IPrewarmClaim : IDisposable
{
    /// <summary>The exception from a speculative job this claim ATTACHED to and waited out, so a caller
    /// that would otherwise read the wait as a success can tell the work failed. Null when the claim
    /// resolved on an idle queue, when it preempted the running job, and when the job it waited out
    /// finished or was cancelled. Informational: the caller redoes the work either way.</summary>
    Exception? AttachedJobFault { get; }
}

/// <summary>
/// A priority queue for SPECULATIVE background preparation, with at most one job in flight.
/// <see cref="Enqueue"/> is a guess; <see cref="ClaimAsync"/> is somebody waiting, and always wins.
///
/// <para>A claim on the key already running attaches its progress sink and awaits that job — never a second
/// run of the same work — unless it asks to preempt, which a caller whose own work is far smaller than the
/// job does. A claim on any OTHER key always preempts. A preempted key goes back to the head of the queue to
/// resume after the release, so nobody waits on work nobody asked for. While any claim is held no
/// speculative job starts, which is what bounds the concurrency: one job, claimed or speculative, at a
/// time.</para>
///
/// <para>A preempted key held by ANOTHER outstanding claim is dropped rather than resumed: that claim is
/// doing the key's work explicitly, so a speculative repeat afterwards would redo it over whatever the
/// modder did with the result.</para>
///
/// <para>A job that throws is swallowed: speculative work has no surface of its own, and the claimed path
/// re-does it and reports for itself. A claim that ATTACHED to that job still learns the throw, through
/// <see cref="IPrewarmClaim.AttachedJobFault"/> — its wait ended in a failure, not a result.</para>
///
/// <para>Thread-safe: every mutation runs under one lock and no lock is held across an await. Continuations
/// resume on the caller's context, so a queue driven from a UI thread stays on it.</para>
/// </summary>
/// <typeparam name="TKey">The work identity. Dedupe, claim matching and cancellation all compare with the
/// comparer given to the constructor.</typeparam>
public sealed class PrewarmQueue<TKey> where TKey : notnull
{
    private readonly Func<TKey, IProgress<string>, CancellationToken, Task> _run;
    private readonly IEqualityComparer<TKey> _comparer;
    private readonly object _gate = new();
    private readonly List<TKey> _pending = new();

    private TKey? _runningKey;
    private CancellationTokenSource? _runningCts;
    private RelaySink? _runningSink;
    private JobSignal? _runningJob;

    // Claims outstanding, and the keys they are on. Non-zero parks the pump: an explicit action owns the
    // machinery until it releases. The per-key counts answer "is somebody already doing this key's work?",
    // which decides whether a preempted key is worth resuming.
    private int _claims;
    private readonly Dictionary<TKey, int> _claimed;
    // A job that completed synchronously re-enters Pump from inside Pump's own loop; the flag makes that
    // re-entry a no-op and lets the loop it returned into pick the next job up.
    private bool _pumping;

    /// <param name="run">The work. Reports through the given sink (dropped until a claim attaches a real
    /// one) and must honor the token.</param>
    /// <param name="comparer">Key identity; defaults to <see cref="EqualityComparer{T}.Default"/>.</param>
    public PrewarmQueue(Func<TKey, IProgress<string>, CancellationToken, Task> run,
        IEqualityComparer<TKey>? comparer = null)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _comparer = comparer ?? EqualityComparer<TKey>.Default;
        _claimed = new Dictionary<TKey, int>(_comparer);
    }

    /// <summary>The key running right now, or <c>default</c>. Diagnostics.</summary>
    public TKey? RunningKey { get { lock (_gate) return _runningJob is null ? default : _runningKey; } }

    /// <summary>The waiting keys, in the order they will run. Diagnostics.</summary>
    public IReadOnlyList<TKey> Pending { get { lock (_gate) return _pending.ToArray(); } }

    /// <summary>Nothing running and nothing waiting.</summary>
    public bool IsIdle { get { lock (_gate) return _runningJob is null && _pending.Count == 0; } }

    /// <summary>Queue speculative work. A key already running or already waiting is left alone — the queue
    /// is a set, not a log.</summary>
    public void Enqueue(TKey key)
    {
        lock (_gate)
        {
            if (_runningJob is not null && _comparer.Equals(_runningKey!, key)) return;
            foreach (var k in _pending) if (_comparer.Equals(k, key)) return;
            _pending.Add(key);
        }
        Pump();
    }

    /// <summary>Forget a key's speculative work, cancelling it if it is running. The returned task completes
    /// when nothing is running for that key, so a caller that needs the machinery free can await it.</summary>
    public Task CancelAsync(TKey key)
    {
        lock (_gate)
        {
            _pending.RemoveAll(k => _comparer.Equals(k, key));
            if (_runningJob is not { } job || !_comparer.Equals(_runningKey!, key)) return Task.CompletedTask;
            _runningCts!.Cancel();
            return job.Done.Task;
        }
    }

    /// <summary>Drop every waiting key and cancel the running one. The queue keeps accepting new work.</summary>
    public void CancelAll()
    {
        lock (_gate)
        {
            _pending.Clear();
            _runningCts?.Cancel();
        }
    }

    /// <summary>Take the queue for an explicit action on <paramref name="key"/>, and hold it until the
    /// returned scope is disposed. Resolves once no JOB is running: immediately when the queue is idle, after
    /// the same key's in-flight job finishes (its progress relayed to <paramref name="status"/>), or after
    /// the running job has been cancelled and unwound. A preempted key resumes after the release; this one
    /// leaves the queue, since the caller is doing its work now. A claim excludes speculative work, not other
    /// claims — serializing explicit actions against each other is the caller's business.</summary>
    /// <param name="preemptSameKey">Cancel an in-flight job for THIS key instead of waiting it out. For a
    /// caller whose own work is one file: waiting out a whole outfit's job costs the modder far more than
    /// letting that job restart afterwards.</param>
    /// <param name="onWait">Called, once, ONLY when this claim has to wait for a job to unwind, with true
    /// when that job is being cancelled and false when it is being waited out. A speculative job gives the
    /// machinery back at its own next checkpoint, which is a whole part or a whole combine away, so the
    /// caller has something to say for seconds before its own work starts. Not called when the queue is
    /// idle: a claim that resolves at once is not a state worth reporting.</param>
    public async Task<IPrewarmClaim> ClaimAsync(TKey key, IProgress<string>? status = null,
        bool preemptSameKey = false, Action<bool>? onWait = null)
    {
        Task wait;
        bool cancelling;
        // Set only when this claim WAITS OUT the running job, which makes that job's outcome this claim's
        // to report.
        JobSignal? attached;
        lock (_gate)
        {
            // Read BEFORE this claim registers: the question is whether SOMEBODY ELSE is already doing the
            // running key's work, and a same-key claim would otherwise answer about itself.
            bool runningIsClaimed = _runningJob is not null && _claimed.ContainsKey(_runningKey!);
            _claims++;
            _claimed[key] = _claimed.TryGetValue(key, out var n) ? n + 1 : 1;
            _pending.RemoveAll(k => _comparer.Equals(k, key));
            if (_runningJob is not { } job) return new Claim(this, key, null);
            if (_comparer.Equals(_runningKey!, key) && !preemptSameKey)
            {
                _runningSink!.Target = status;   // the wait isn't blank: the job's own progress surfaces
                cancelling = false;
                attached = job;
            }
            else
            {
                if (!runningIsClaimed) _pending.Insert(0, _runningKey!);   // resume it once the claim releases
                _runningCts!.Cancel();
                cancelling = true;
                attached = null;   // a job this claim cancelled ends however cancellation ends it
            }
            wait = job.Done.Task;
        }
        onWait?.Invoke(cancelling);   // outside the gate: no caller code runs under the lock
        await wait;   // deliberately NOT ConfigureAwait(false): the caller's context is the caller's business
        return new Claim(this, key, attached?.Fault);
    }

    private void Release(TKey key)
    {
        lock (_gate)
        {
            if (_claims > 0) _claims--;
            if (_claimed.TryGetValue(key, out var n))
            {
                if (n <= 1) _claimed.Remove(key); else _claimed[key] = n - 1;
            }
        }
        Pump();
    }

    private void Pump()
    {
        lock (_gate)
        {
            if (_pumping) return;
            _pumping = true;
            try
            {
                while (_claims == 0 && _runningJob is null && _pending.Count > 0)
                {
                    var key = _pending[0];
                    _pending.RemoveAt(0);
                    Start(key);
                }
            }
            finally { _pumping = false; }
        }
    }

    // Called under _gate.
    private void Start(TKey key)
    {
        var cts = new CancellationTokenSource();
        var sink = new RelaySink();
        var job = new JobSignal();
        _runningKey = key; _runningCts = cts; _runningSink = sink; _runningJob = job;
        _ = RunAsync(key, cts, sink, job);
    }

    private async Task RunAsync(TKey key, CancellationTokenSource cts, RelaySink sink, JobSignal job)
    {
        try { await _run(key, sink, cts.Token); }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Preemption, not a failure: whoever cancelled this is doing the key's work itself.
        }
        catch (Exception e)
        {
            // Speculative work has no surface of its own, so the throw stops here. It is still RECORDED,
            // because a claim attached to this job is waiting on its completion and would otherwise read a
            // failed job as a finished one.
            job.Fault = e;
        }
        finally
        {
            lock (_gate)
            {
                // Only clear what is still OURS — a job cancelled and superseded must not clear its successor.
                if (ReferenceEquals(_runningJob, job))
                {
                    _runningKey = default; _runningCts = null; _runningSink = null; _runningJob = null;
                }
            }
            cts.Dispose();
            job.Done.TrySetResult();
            Pump();
        }
    }

    /// <summary>One job's completion signal plus how it ended. The task always COMPLETES rather than
    /// faulting: it is awaited by speculative paths that have nowhere to put an exception, so the failure
    /// rides <see cref="Fault"/> beside it instead.</summary>
    private sealed class JobSignal
    {
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Written by the job's own continuation before <see cref="Done"/> completes, read by
        /// waiters after it; that ordering is the whole synchronization.</summary>
        public Exception? Fault;
    }

    /// <summary>Drops the job's progress until a claim points it at a real sink.</summary>
    private sealed class RelaySink : IProgress<string>
    {
        private readonly object _lock = new();
        private IProgress<string>? _target;

        public IProgress<string>? Target { set { lock (_lock) _target = value; } }

        public void Report(string value)
        {
            IProgress<string>? target;
            lock (_lock) target = _target;
            target?.Report(value);
        }
    }

    private sealed class Claim : IPrewarmClaim
    {
        private readonly TKey _key;
        private PrewarmQueue<TKey>? _queue;
        public Claim(PrewarmQueue<TKey> queue, TKey key, Exception? attachedJobFault)
        { _queue = queue; _key = key; AttachedJobFault = attachedJobFault; }
        public Exception? AttachedJobFault { get; }
        public void Dispose() => Interlocked.Exchange(ref _queue, null)?.Release(_key);
    }
}
