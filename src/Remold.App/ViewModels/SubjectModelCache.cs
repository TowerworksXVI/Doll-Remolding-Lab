using System;
using System.Collections.Concurrent;
using Remold.Core.Workbench;

namespace Remold.App.ViewModels;

/// <summary>The session's memo of built <see cref="SubjectModel"/>s, shared by every pane that reads
/// one. A model derives from the game's own data plus the roster outfit and nothing else — an immutable
/// record graph, safe in every reader on any thread — and building one costs bundle deobfuscation plus
/// prefab and CAB reads, seconds for a wide scope. Only the game's own files can invalidate an entry, so
/// <see cref="Clear"/> belongs exactly where the forward view is dropped and re-read.</summary>
public sealed class SubjectModelCache
{
    private readonly object _changeGate = new();
    private long _version;
    private TaskCompletionSource _changed = NewChangeSource();
    private readonly ConcurrentDictionary<SubjectKey, SubjectModel> _models =
        new(SubjectKeyComparer.Instance);

    private readonly record struct SubjectKey(string Character, string Stem);

    private sealed class SubjectKeyComparer : IEqualityComparer<SubjectKey>
    {
        public static readonly SubjectKeyComparer Instance = new();

        public bool Equals(SubjectKey left, SubjectKey right) =>
            string.Equals(left.Character, right.Character, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Stem, right.Stem, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(SubjectKey key) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Character),
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Stem));
    }

    /// <summary>The model for a subject, built on the first ask. A build that THROWS is NOT memoized: the
    /// exception surfaces as that subject's own problem and the next ask retries. Two callers racing the
    /// same key both build; one result is stored and both get it — a duplicated read, never a wrong
    /// answer.</summary>
    public SubjectModel GetOrBuild(string character, string stem, Func<SubjectModel> build)
    {
        var key = new SubjectKey(character, stem);
        if (_models.TryGetValue(key, out var hit)) return hit;
        var made = build();
        var result = _models.GetOrAdd(key, made);
        if (ReferenceEquals(result, made)) SignalChanged();
        return result;
    }

    /// <summary>The model for a subject IF one is already memoized, else null — a peek that never builds.
    /// For a caller on the UI thread that has something better to do than stall the window for seconds on a
    /// subject nothing has read yet.</summary>
    public SubjectModel? TryGet(string character, string stem) =>
        _models.TryGetValue(new SubjectKey(character, stem), out var hit) ? hit : null;

    /// <summary>Record that a read of one subject FINISHED without a model — the roster does not carry it,
    /// or its files could not be read. It describes the INSTALL, not the mod: nothing retries within one
    /// forward view, so every surface asking about that subject can stop promising an answer that is not
    /// coming.
    ///
    /// <para>Kept here rather than beside the reader so it is dropped by the same <see cref="Clear"/> the
    /// models are: a re-read of the game is exactly the event that can change this answer, and a second home
    /// would be a second thing to remember to clear.</para></summary>
    public void MarkUnreadable(string character, string stem)
    {
        if (_unreadable.TryAdd(new SubjectKey(character, stem), 0)) SignalChanged();
    }

    /// <summary>Whether a read of this subject already finished without a model. False for a subject nothing
    /// has tried yet, which is the state a wait belongs to.</summary>
    public bool IsUnreadable(string character, string stem) =>
        _unreadable.ContainsKey(new SubjectKey(character, stem));

    private readonly ConcurrentDictionary<SubjectKey, byte> _unreadable =
        new(SubjectKeyComparer.Instance);

    /// <summary>Drop every memoized model and every recorded failure, for a re-read of the game.</summary>
    public void Clear()
    {
        _models.Clear();
        _unreadable.Clear();
        SignalChanged();
    }

    public long Version { get { lock (_changeGate) return _version; } }

    public Task WaitForChangeAsync(long observed, CancellationToken token)
    {
        Task wait;
        lock (_changeGate)
        {
            if (_version != observed) return Task.CompletedTask;
            wait = _changed.Task;
        }
        return wait.WaitAsync(token);
    }

    private void SignalChanged()
    {
        TaskCompletionSource completed;
        lock (_changeGate)
        {
            _version++;
            completed = _changed;
            _changed = NewChangeSource();
        }
        completed.TrySetResult();
    }

    private static TaskCompletionSource NewChangeSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How many models are memoized. Test seam for the hit and for the drop.</summary>
    internal int Count => _models.Count;
}
