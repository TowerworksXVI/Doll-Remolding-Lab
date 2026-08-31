using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Remold.Core.Tests.Support;

/// <summary>
/// A stand-in for the window's own thread, for tests that have to drive work the app marshals onto it.
///
/// <para>The suite host has no Avalonia message loop, so <c>Dispatcher.UIThread</c> takes posts and never
/// runs them — a task that hops onto it parks for good. This is the same shape with a pump behind it: ONE
/// thread draining a queue, a <see cref="SynchronizationContext"/> installed on it so an <c>await</c>
/// started there resumes there, and a dispatch that runs INLINE when the caller is already on the thread,
/// exactly as the app's own <c>OnUi</c> does.</para>
///
/// <para>That last pair is the point. An inline-everything test dispatcher cannot deadlock and so cannot
/// show a self-wait; a real one can. Work that blocks this thread while waiting on something that needs
/// this thread hangs here precisely as it hangs in the app, which is what makes a timeout on
/// <see cref="Idle"/> a real assertion rather than a slow one.</para>
/// </summary>
internal sealed class PumpedUiThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly PumpContext _context;
    private int _pending;
    private readonly ManualResetEventSlim _idle = new(initialState: true);
    private long _longestActionTicks;

    public PumpedUiThread()
    {
        _context = new PumpContext(this);
        _thread = new Thread(Run) { IsBackground = true, Name = "test-ui-pump" };
        _thread.Start();
    }

    /// <summary>The <c>pageDispatch</c> seam to hand the window: inline on this thread, queued off it.</summary>
    public Action<Action> Dispatch => Post;

    public void Post(Action work)
    {
        if (Thread.CurrentThread == _thread) { work(); return; }
        Enqueue(work);
    }

    /// <summary>Run one action ON the pump thread and wait for it, the way a click on a command does.
    /// Failures come back to the caller rather than dying inside the pump.</summary>
    public void Invoke(Action work, TimeSpan timeout)
    {
        if (Thread.CurrentThread == _thread) { work(); return; }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try { work(); done.TrySetResult(); }
            catch (Exception e) { done.TrySetException(e); }
        });
        if (!done.Task.Wait(timeout)) throw new TimeoutException("the pumped UI thread never ran the action");
    }

    /// <summary>Wait until the pump has nothing left to run. A queue that never drains inside
    /// <paramref name="timeout"/> is a thread something is holding.</summary>
    public bool Idle(TimeSpan timeout) => _idle.Wait(timeout);

    /// <summary>The longest SINGLE action this thread has run — how long the window was unable to draw or
    /// answer a click at the worst moment, which is the thing a modder feels. A cost spread over many
    /// small dispatches is not a freeze; one action holding the thread is.</summary>
    public TimeSpan LongestAction => TimeSpan.FromTicks(Interlocked.Read(ref _longestActionTicks));

    /// <summary>Forget what has been timed so far, so a measurement covers one act rather than the setup
    /// that preceded it.</summary>
    public void ForgetTimings() => Interlocked.Exchange(ref _longestActionTicks, 0);

    private void Enqueue(Action work)
    {
        Interlocked.Increment(ref _pending);
        _idle.Reset();
        try { _queue.Add(work); }
        catch (InvalidOperationException) { Settled(); }   // the pump is shutting down
    }

    private void Settled()
    {
        if (Interlocked.Decrement(ref _pending) == 0) _idle.Set();
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(_context);
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            long started = Stopwatch.GetTimestamp();
            try { work(); }
            catch { /* a dispatcher has nobody to report to; the caller's own seam carries failures */ }
            finally
            {
                long held = Stopwatch.GetElapsedTime(started).Ticks;
                if (held > Interlocked.Read(ref _longestActionTicks))
                    Interlocked.Exchange(ref _longestActionTicks, held);
                Settled();
            }
        }
    }

    /// <summary>Stop the pump. The queue itself is deliberately NOT disposed: a test that fails while the
    /// pump is deliberately held would otherwise pull the collection out from under the thread still
    /// sitting in it, and the host dies on that rather than on the test's own failure.</summary>
    public void Dispose()
    {
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        _thread.Join(TimeSpan.FromSeconds(10));
    }

    /// <summary>Continuations of awaits started on the pump come back to the pump. Never inline, even from
    /// the pump thread itself — that is what Avalonia's own context does, and running a continuation inline
    /// would hide exactly the re-entrancy this class exists to expose.</summary>
    private sealed class PumpContext(PumpedUiThread owner) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => owner.Enqueue(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (Thread.CurrentThread == owner._thread) { d(state); return; }
            owner.Invoke(() => d(state), TimeSpan.FromSeconds(30));
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}
