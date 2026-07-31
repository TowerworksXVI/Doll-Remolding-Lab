using System;
using System.IO;
using System.Threading;
using Remold.Core.Textures;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>A save under <c>textures/</c> raises <see cref="TextureEditWatcher.Changed"/> once it is quiet
/// and readable, but a self-write suppressed via <see cref="TextureEditWatcher.SuppressPath"/> does not,
/// and the pristine <c>originals/</c> copy doesn't count. FS-event timing is real, so these poll with
/// generous windows.</summary>
public class TextureEditWatcherTests
{
    private static string TexturesDir(TempGame g)
    {
        var d = Path.Combine(g.Root, "textures");
        Directory.CreateDirectory(d);
        return d;
    }

    private static bool WaitFor(Func<bool> cond, int ms = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (cond()) return true; Thread.Sleep(25); }
        return cond();
    }

    [Fact]
    public void RaisesChanged_AfterAWriteUnderTextures()
    {
        using var g = new TempGame();
        var texDir = TexturesDir(g);
        using var w = new TextureEditWatcher(g.Root);
        int hits = 0;
        string? seen = null;
        // payload first, THEN the counter: the increment is the publication barrier the poll reads
        w.Changed += p => { seen = p; Interlocked.Increment(ref hits); };

        var png = Path.Combine(texDir, "body_d.png");
        File.WriteAllBytes(png, new byte[] { 1, 2, 3, 4 });

        Assert.True(WaitFor(() => Volatile.Read(ref hits) >= 1), "watcher never raised Changed");
        Assert.Equal(Path.GetFullPath(png), Path.GetFullPath(seen!));
    }

    [Fact]
    public void SuppressedSelfWrite_DoesNotRaiseChanged()
    {
        using var g = new TempGame();
        var texDir = TexturesDir(g);
        using var w = new TextureEditWatcher(g.Root);
        int hits = 0;
        w.Changed += _ => Interlocked.Increment(ref hits);

        var png = Path.Combine(texDir, "body_d.png");
        w.SuppressPath(png);                       // the app is about to write it itself (a revert)
        File.WriteAllBytes(png, new byte[] { 9, 9, 9, 9 });

        // give the debounce + a margin to elapse; the suppressed write must NOT surface
        Thread.Sleep(1200);
        Assert.Equal(0, Volatile.Read(ref hits));
    }

    [Fact]
    public void IgnoresWritesOutsideTextures()
    {
        using var g = new TempGame();
        TexturesDir(g);
        var origDir = Path.Combine(g.Root, "originals");
        Directory.CreateDirectory(origDir);
        using var w = new TextureEditWatcher(g.Root);
        int hits = 0;
        w.Changed += _ => Interlocked.Increment(ref hits);

        // a write to originals/ (the pristine copy) is not an edit
        File.WriteAllBytes(Path.Combine(origDir, "body_d.png"), new byte[] { 1, 2, 3 });
        Thread.Sleep(1000);
        Assert.Equal(0, Volatile.Read(ref hits));
    }
}
