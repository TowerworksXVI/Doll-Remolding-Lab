using System;
using System.Collections.Generic;
using Remold.Core.Project;

namespace Remold.App.ViewModels.EditPage;

/// <summary>The expensive resolved-part answer belongs to the loaded install, not to an open project.
/// Project switches therefore keep it; a different install identity or an explicit rescan clears it.</summary>
internal sealed class InstallResolvedPartCache
{
    private readonly Dictionary<string, LegacyResolvedPart?> _parts = new(StringComparer.Ordinal);
    private object? _install;

    public bool TryGet(object install, TargetPart target, out LegacyResolvedPart? part)
    {
        UseInstall(install);
        return _parts.TryGetValue(Key(target), out part);
    }

    public void Store(object install, TargetPart target, LegacyResolvedPart? part)
    {
        UseInstall(install);
        _parts[Key(target)] = part;
    }

    public void Clear()
    {
        _install = null;
        _parts.Clear();
    }

    internal int Count => _parts.Count;

    private void UseInstall(object install)
    {
        ArgumentNullException.ThrowIfNull(install);
        if (ReferenceEquals(_install, install)) return;
        _install = install;
        _parts.Clear();
    }

    private static string Key(TargetPart part) =>
        $"{part.Subject}\u0001{part.Outfit}\u0001{part.RendererSlot}".ToUpperInvariant();
}
