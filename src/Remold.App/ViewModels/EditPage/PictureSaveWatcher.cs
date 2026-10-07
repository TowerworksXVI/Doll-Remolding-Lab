using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Remold.App.ViewModels.EditPage;

/// <summary>Watches one mod's round-trip folder for image-editor saves. It reports the transport folder a
/// save landed in once the saved file has stopped changing and nothing is still writing it, so a picture the
/// editor is still writing is waited out rather than read half-written. Only folders holding the owner's
/// marker file are image-editor transports; Blender's files in the same tree are never touched. Whether the
/// save is new to the mod is the owner's question.</summary>
internal sealed class PictureSaveWatcher : IDisposable
{
    /// <summary>The file a picture transport hands the image editor, in every transport folder.</summary>
    internal const string SavedFile = "outbound.png";

    private readonly string _marker;

    private readonly FileSystemWatcher _watcher;
    private readonly Action<string> _saved;
    private readonly int _settleMs;
    private readonly int _maxChecks;
    private readonly object _gate = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    private sealed class Pending
    {
        public Timer Timer = null!;
        public int Checks;
        public (long Length, DateTime Written)? Last;
    }

    /// <param name="saved">Raised on a timer thread with the transport folder whose file settled. After
    /// <paramref name="maxChecks"/> checks it is raised anyway, so a file something keeps holding reaches
    /// the owner's read and its failure is reported rather than waited on forever.</param>
    /// <param name="failed">Raised when the operating system stops reporting changes to the folder, after
    /// which this watcher reports nothing more.</param>
    /// <param name="marker">The file that marks a folder as an image-editor transport.</param>
    internal PictureSaveWatcher(string root, string marker, Action<string> saved, Action<Exception> failed,
        int settleMs = 250, int maxChecks = 240)
    {
        _marker = marker ?? throw new ArgumentNullException(nameof(marker));
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
        ArgumentNullException.ThrowIfNull(failed);
        _settleMs = settleMs;
        _maxChecks = maxChecks;
        Directory.CreateDirectory(root);
        _watcher = new FileSystemWatcher(root, SavedFile)
        {
            IncludeSubdirectories = true,
            // The name filter is applied after the operating system reports a change, so Blender's writes
            // in the same tree fill this buffer too; the default overflows on one Blender open.
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += (_, e) => Arm(e.FullPath);
        _watcher.Created += (_, e) => Arm(e.FullPath);
        // An editor that saves by writing a temporary file and renaming it over this one.
        _watcher.Renamed += (_, e) => Arm(e.FullPath);
        _watcher.Error += (_, e) =>
        {
            lock (_gate) if (_disposed) return;
            failed(e.GetException());
        };
        _watcher.EnableRaisingEvents = true;
    }

    private void Arm(string file)
    {
        if (Path.GetDirectoryName(file) is not { } folder
            || !File.Exists(Path.Combine(folder, _marker))) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_pending.TryGetValue(folder, out var pending))
            {
                pending = new Pending();
                pending.Timer = new Timer(_ => Check(folder), null, Timeout.Infinite, Timeout.Infinite);
                _pending[folder] = pending;
            }
            pending.Checks = 0;
            pending.Last = null;
            pending.Timer.Change(_settleMs, Timeout.Infinite);
        }
    }

    /// <summary>A save is settled when two checks one interval apart both open the file exclusively and see
    /// the same length and write time.</summary>
    private void Check(string folder)
    {
        var state = Released(Path.Combine(folder, SavedFile));
        lock (_gate)
        {
            if (_disposed || !_pending.TryGetValue(folder, out var pending)) return;
            bool settled = state is not null && state == pending.Last;
            if (!settled && ++pending.Checks < _maxChecks)
            {
                pending.Last = state;
                pending.Timer.Change(_settleMs, Timeout.Infinite);
                return;
            }
            _pending.Remove(folder);
            pending.Timer.Dispose();
        }
        _saved(folder);
    }

    // Opening to read while refusing other writers fails while a program is still writing the file. Deleting
    // and renaming stay allowed, so an editor that saves by renaming a new file over this one is never
    // refused by the check. The length and write time are read under that handle.
    private static (long, DateTime)? Released(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            return (stream.Length, File.GetLastWriteTimeUtc(file));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pending in _pending.Values) pending.Timer.Dispose();
            _pending.Clear();
        }
        _watcher.Dispose();
    }
}
