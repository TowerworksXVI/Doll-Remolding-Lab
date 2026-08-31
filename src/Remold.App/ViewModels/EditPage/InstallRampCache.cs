using System;
using System.Collections.Generic;
using Remold.App.Textures;

namespace Remold.App.ViewModels.EditPage;

/// <summary>Decoded game ramps and their rendered strips, retained for one loaded install.</summary>
internal sealed class InstallRampCache
{
    internal sealed record Entry(RampImage.Read Read, byte[]? PreviewPng);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private object? _install;

    public bool TryGet(object install, string catalogVersion, RampChoice choice, out Entry? entry)
    {
        UseInstall(install);
        return _entries.TryGetValue(Key(catalogVersion, choice), out entry);
    }

    public void Store(object install, string catalogVersion, RampChoice choice, Entry entry)
    {
        UseInstall(install);
        _entries[Key(catalogVersion, choice)] = entry;
    }

    public void Clear()
    {
        _install = null;
        _entries.Clear();
    }

    internal int Count => _entries.Count;

    private void UseInstall(object install)
    {
        if (ReferenceEquals(_install, install)) return;
        _install = install;
        _entries.Clear();
    }

    private static string Key(string catalogVersion, RampChoice choice) => catalogVersion + "\u001f"
        + choice.Bundle + "\u001f" + (choice.PathId != 0 ? "#" + choice.PathId : choice.Texture);
}
