using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Remold.Core.Textures;

/// <summary>
/// Watches a mod folder for edits to the editable texture copies (<c>textures/*.png</c>) so an external
/// editor's save lands back in the Edit pane. Recursive; the pristine <c>originals/</c> copies are
/// ignored.
///
/// <b>Trailing-edge debounce:</b> each event restarts a short timer and the change is raised only once
/// the file has been quiet for the window AND is readable, so a PNG still being flushed isn't read
/// truncated. Leading-edge would fire on the FIRST event and swallow the completing writes.
///
/// <b>Self-write suppression:</b> the app writes these files too (e.g. revert), so
/// <see cref="SuppressPath"/> makes it ignore a path briefly and not re-mark the target edited.
/// </summary>
public sealed class TextureEditWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SelfWriteWindow = TimeSpan.FromMilliseconds(1500);

    private readonly FileSystemWatcher _fsw;
    // per-path trailing-edge timers: restarted on each event, fire once the file goes quiet
    private readonly ConcurrentDictionary<string, Timer> _pending = new(StringComparer.OrdinalIgnoreCase);
    // paths the app is about to write itself — events for these are ignored until the recorded time
    private readonly ConcurrentDictionary<string, DateTime> _suppressed = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    /// <summary>Raised with the full path of an edited <c>textures/*.png</c>.</summary>
    public event Action<string>? Changed;
    /// <summary>Raised when the watcher hits an error instead of crashing: the FSW callback runs on a
    /// pool thread, where an escaping exception is fatal.</summary>
    public event Action<string>? Error;

    public TextureEditWatcher(string root)
    {
        Directory.CreateDirectory(root);
        _fsw = new FileSystemWatcher(root, "*.png")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        _fsw.Created += OnChanged;
        _fsw.Changed += OnChanged;
        // some editors save by writing a temp file and renaming it over the target — a Rename, not a
        // Change; RenamedEventArgs derives from FileSystemEventArgs so the same handler reads it
        _fsw.Renamed += OnChanged;
        _fsw.Error += (_, e) => Report(e.GetException());
    }

    /// <summary>Ignore FS events for <paramref name="path"/> briefly so an app-side write doesn't
    /// re-mark the target edited. Call immediately before the write.</summary>
    public void SuppressPath(string path)
    {
        _suppressed[Path.GetFullPath(path)] = DateTime.UtcNow + SelfWriteWindow;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // runs on an FSW pool thread, where any escaping exception tears the process down
        try
        {
            if (_disposed) return;
            // only the editable copy under textures/ is a real edit, never the originals/ copy
            var dir = Path.GetFileName(Path.GetDirectoryName(e.FullPath));
            if (!string.Equals(dir, "textures", StringComparison.OrdinalIgnoreCase)) return;

            var full = Path.GetFullPath(e.FullPath);
            if (IsSuppressed(full)) return;

            // (re)start this path's quiet timer; Fire raises once it goes quiet.
            // Race: Fire() removes and disposes the timer, so the instance fetched here can be disposed
            // under us and Change would throw ODE, losing the trailing event. On ODE drop the stale
            // instance and retry once — the second GetOrAdd mints a fresh timer (infinite due time, so
            // it can't fire before Change arms it).
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var timer = _pending.GetOrAdd(full, p => new Timer(Fire, p, Timeout.Infinite, Timeout.Infinite));
                try { timer.Change(Debounce, Timeout.InfiniteTimeSpan); break; }
                catch (ObjectDisposedException)
                {
                    ((ICollection<KeyValuePair<string, Timer>>)_pending)
                        .Remove(new KeyValuePair<string, Timer>(full, timer));   // only if still THIS instance
                }
            }
        }
        catch (Exception ex) { Report(ex); }
    }

    private void Fire(object? state)
    {
        var path = (string)state!;
        try
        {
            if (_pending.TryRemove(path, out var t)) t.Dispose();
            if (_disposed) return;
            if (IsSuppressed(path)) return;                 // a self-write landed during the quiet window
            if (!File.Exists(path)) return;                 // written then removed (temp churn)
            if (!WaitUntilReadable(path)) return;            // still locked/truncated — the next event re-arms
            Changed?.Invoke(path);
        }
        catch (Exception ex) { Report(ex); }
    }

    private bool IsSuppressed(string full)
    {
        if (!_suppressed.TryGetValue(full, out var until)) return false;
        if (DateTime.UtcNow <= until) return true;
        _suppressed.TryRemove(full, out _);                  // window elapsed — stop suppressing
        return false;
    }

    private void Report(Exception ex)
    {
        try { Error?.Invoke(ex.Message); } catch { /* a bad Error handler must not re-crash the thread */ }
    }

    /// <summary>Retry until the editor releases the PNG handle; false if it never became readable, so a
    /// truncated file never raises.</summary>
    private static bool WaitUntilReadable(string path, int attempts = 20)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return true; }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        _fsw.Dispose();
        foreach (var kv in _pending) { try { kv.Value.Dispose(); } catch { /* best effort */ } }
        _pending.Clear();
    }
}
