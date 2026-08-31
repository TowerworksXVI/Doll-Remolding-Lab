using System;
using System.IO;
using System.Threading;

namespace Remold.App.ViewModels.EditPage;

/// <summary>A dumb transport watch for one editor document already addressed to one ingress session. It
/// reports only that this exact transient file settled after a write; the owner already holds the edit and
/// slot identities and never derives either from the path.</summary>
internal sealed class PictureTransportWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _settled;
    private readonly Action _received;
    private bool _disposed;

    internal PictureTransportWatcher(string file, Action received, Action<string>? failed = null)
    {
        _received = received ?? throw new ArgumentNullException(nameof(received));
        string full = Path.GetFullPath(file);
        _settled = new Timer(_ => Receive(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(full)!, Path.GetFileName(full))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => Arm();
        _watcher.Created += (_, _) => Arm();
        _watcher.Renamed += (_, _) => Arm();
        _watcher.Error += (_, e) => failed?.Invoke(e.GetException().Message);
    }

    private void Arm()
    {
        if (_disposed) return;
        _settled.Change(250, Timeout.Infinite);
    }

    private void Receive()
    {
        if (_disposed) return;
        _received();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.Dispose();
        _settled.Dispose();
    }
}
