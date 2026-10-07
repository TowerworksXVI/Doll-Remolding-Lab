using System.Collections.Concurrent;
using Remold.App.ViewModels.EditPage;
using Remold.Core.Project;

namespace Remold.App.ViewModels;

/// <summary>One install's immutable material answers. Outfit loading pays for each exact material once;
/// cards only peek, and editor/copy commands share the same completed answer. Authored choices belong
/// to the edit session and are never stored here.</summary>
internal sealed class MaterialShadingCache(Func<GameAssetRef, EditShadingRead> read)
{
    internal const string ReadFailure =
        "Couldn't read this material's shading. Use Tools · Rescan game files to try again.";

    private readonly ConcurrentDictionary<(string Bundle, long PathId), Lazy<EditShadingRead>> _reads = new();

    private static (string Bundle, long PathId) Key(GameAssetRef material) =>
        (material.LogicalBundle.ToUpperInvariant(), material.PathId);

    /// <summary>Null means no completed answer yet. This never starts work or waits for a reader.</summary>
    public EditShadingRead? Peek(GameAssetRef material) =>
        _reads.TryGetValue(Key(material), out var entry) && entry.IsValueCreated ? entry.Value : null;

    /// <summary>Called during outfit loading, or by an explicit command for a material not loaded yet.
    /// Simultaneous callers share one read. A failed or cancelled read is a completed answer too, so a
    /// redraw cannot retry it or leave the card waiting forever; rereading the install replaces this cache.</summary>
    public EditShadingRead GetOrRead(GameAssetRef material) =>
        _reads.GetOrAdd(Key(material), _ => new Lazy<EditShadingRead>(() =>
        {
            try { return read(material); }
            catch (Exception) { return new EditShadingRead(null, ReadFailure); }
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
}
