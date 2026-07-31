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
    private readonly ConcurrentDictionary<MainWindowViewModel.SubjectKey, SubjectModel> _models =
        new(MainWindowViewModel.SubjectKeyComparer.Instance);

    /// <summary>The model for a subject, built on the first ask. A build that THROWS is NOT memoized: the
    /// exception surfaces as that subject's own problem and the next ask retries. Two callers racing the
    /// same key both build; one result is stored and both get it — a duplicated read, never a wrong
    /// answer.</summary>
    public SubjectModel GetOrBuild(string character, string stem, Func<SubjectModel> build)
    {
        var key = new MainWindowViewModel.SubjectKey(character, stem);
        if (_models.TryGetValue(key, out var hit)) return hit;
        return _models.GetOrAdd(key, build());
    }

    /// <summary>Drop every memoized model, for a re-read of the game.</summary>
    public void Clear() => _models.Clear();

    /// <summary>How many models are memoized. Test seam for the hit and for the drop.</summary>
    internal int Count => _models.Count;
}
