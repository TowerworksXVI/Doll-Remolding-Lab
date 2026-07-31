using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Remold.Core.Blender;
using Remold.Core.Mesh;
using Remold.Core.Tests.Support;
using Xunit;

namespace Remold.Core.Tests;

/// <summary>
/// The send watcher's FAILURE seam: an unimportable send-back must raise
/// <see cref="BlenderSendWatcher.Error"/> carrying the glb path — a single-part send has already
/// overwritten that workspace glb, so the subscriber marks it edited rather than read the failure as
/// "nothing happened" — and must NOT raise <see cref="BlenderSendWatcher.EditReceived"/>.
///
/// Plus the OFFLINE-send seam: a send that landed with no app listening sits on disk as an unconsumed
/// sidecar, and <see cref="BlenderSendWatcher.ScanExisting"/> is what picks it up before a rebuild can
/// write over it — exactly once.
/// </summary>
public class BlenderSendWatcherTests
{
    [Fact]
    public void UnreadableGlb_RaisesErrorWithTheGlbPath_AndNoEditReceived()
    {
        using var t = new TempGame();
        string glb = t.At("body.glb");
        File.WriteAllText(glb, "not a glb at all");   // Blender wrote it, the Lab can't import it

        using var w = new BlenderSendWatcher(t.Root);
        string? errorPath = null, errorMsg = null;
        bool editReceived = false;
        using var raised = new ManualResetEventSlim();
        w.Error += (path, msg) => { errorPath = path; errorMsg = msg; raised.Set(); };
        w.EditReceived += _ => editReceived = true;

        // the bridge script writes the glb first and the sidecar LAST; the watcher fires on the sidecar
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");

        Assert.True(raised.Wait(TimeSpan.FromSeconds(10)), "watcher never raised Error for an unreadable glb");
        Assert.Equal(glb, errorPath);
        Assert.False(string.IsNullOrEmpty(errorMsg));
        Assert.False(editReceived);
    }

    [Fact]
    public void ScanExisting_TakesASendThatLandedWithNothingListening()
    {
        // Blender's Send wrote the glb and the sidecar while the app was closed. Nothing raised an event for
        // it, so only the scan can stop the next workspace rebuild from writing over the modder's file.
        using var t = new TempGame();
        var glb = t.At("body1_lod0.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb),
            "{\"source\":\"blender-send\",\"hiddenParts\":[\"cloth1_lod0\"]}");

        using var w = new BlenderSendWatcher(t.Root);
        var got = new List<IncomingEdit>();
        w.EditReceived += e => got.Add(e);

        w.ScanExisting();

        var edit = Assert.Single(got);
        Assert.Equal("body1_lod0", edit.Name);
        Assert.Equal(new[] { "cloth1_lod0" }, edit.HiddenParts!.ToArray());
        // the sidecar IS the unhandled marker: taking the send consumes it
        Assert.False(File.Exists(BlenderBridge.SidecarPath(glb)));
    }

    [Fact]
    public void ScanExisting_TakesEachSendOnlyOnce()
    {
        // Every watcher re-arm scans, and a mod is re-opened over and over. A send already applied must not
        // be replayed onto the project a second time.
        using var t = new TempGame();
        var glb = t.At("body1_lod0.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");

        using var w = new BlenderSendWatcher(t.Root);
        int received = 0;
        w.EditReceived += _ => received++;

        w.ScanExisting();
        w.ScanExisting();

        Assert.Equal(1, received);
    }

    [Fact]
    public void ScanExisting_AnUnreadableSendReportsAndIsStillConsumed()
    {
        // Blender has already overwritten the workspace glb, so the failure has to be SAID; and re-reading
        // it at every later open would fail identically, so the sidecar goes either way.
        using var t = new TempGame();
        var glb = t.At("body.glb");
        File.WriteAllText(glb, "not a glb at all");
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");

        using var w = new BlenderSendWatcher(t.Root);
        var errors = new List<string>();
        w.Error += (path, _) => errors.Add(path);

        w.ScanExisting();
        w.ScanExisting();

        Assert.Equal(new[] { glb }, errors);
        Assert.False(File.Exists(BlenderBridge.SidecarPath(glb)));
    }

    [Fact]
    public void ScanExisting_StopsWhenTheIngestDisposesTheWatcher()
    {
        // An ingest autosaves, and an autosave can move the mod folder and swap the watcher. The rest of the
        // scan's list is in a folder that no longer exists; the replacement watcher owns what moved.
        using var t = new TempGame();
        foreach (var name in new[] { "a_lod0", "b_lod0", "c_lod0" })
        {
            var g = t.At(name + ".glb");
            MeshGltf.ExportGlb(Triangle(name), g);
            File.WriteAllText(BlenderBridge.SidecarPath(g), "{\"source\":\"blender-send\"}");
        }

        var w = new BlenderSendWatcher(t.Root);
        int received = 0;
        w.EditReceived += _ => { received++; w.Dispose(); };

        w.ScanExisting();

        Assert.Equal(1, received);
    }

    [Fact]
    public void ScanExisting_NoSends_DoesNothing()
    {
        using var t = new TempGame();
        using var w = new BlenderSendWatcher(t.Root);
        bool any = false;
        w.EditReceived += _ => any = true;
        w.Error += (_, _) => any = true;

        w.ScanExisting();

        Assert.False(any);
    }

    // ---- a send that will never settle is reported, not waited out -------------

    [Fact]
    public void ASidecarWhoseGlbIsNotThereErrorsWithoutSpendingTheRetryBudget()
    {
        // The budget exists for a glb being written this instant. A sidecar left behind by a glb that was
        // deleted or renamed has no writer to wait for, and the wait is a whole second of nothing.
        using var t = new TempGame();
        string glb = t.At("gone.glb");                       // never written

        using var w = new BlenderSendWatcher(t.Root);
        string? errorPath = null;
        using var raised = new ManualResetEventSlim();
        w.Error += (path, _) => { errorPath = path; raised.Set(); };

        var since = System.Diagnostics.Stopwatch.StartNew();
        File.WriteAllText(BlenderBridge.SidecarPath(glb), "{\"source\":\"blender-send\"}");
        Assert.True(raised.Wait(TimeSpan.FromSeconds(10)), "watcher never raised Error for a missing glb");
        since.Stop();

        Assert.Equal(glb, errorPath);
        Assert.True(since.Elapsed < TimeSpan.FromMilliseconds(2000),
            $"the missing glb was waited out rather than reported ({since.ElapsedMilliseconds} ms)");
    }

    [Fact]
    public void ScanExisting_ATruncatedSidecarErrorsAndIsStillConsumed()
    {
        // A send interrupted mid-write leaves half a json object. Nothing is writing it by the time a scan
        // finds it, so it is reported at once — and consumed, or every later open pays the same read again.
        using var t = new TempGame();
        var glb = t.At("body1_lod0.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        var sidecar = BlenderBridge.SidecarPath(glb);
        File.WriteAllText(sidecar, "{\"source\":\"blender-se");

        using var w = new BlenderSendWatcher(t.Root);
        var errors = new List<string>();
        bool editReceived = false;
        w.Error += (path, _) => errors.Add(path);
        w.EditReceived += _ => editReceived = true;

        var since = System.Diagnostics.Stopwatch.StartNew();
        w.ScanExisting();
        since.Stop();

        Assert.Equal(new[] { glb }, errors);
        Assert.False(editReceived);                          // half a sidecar is never read as a send
        Assert.False(File.Exists(sidecar));
        // The scan runs on the caller's thread at mod open, so each bad send it finds must cost a read, not a
        // retry budget — N of them would otherwise hold the window for N seconds.
        Assert.True(since.Elapsed < TimeSpan.FromMilliseconds(2000),
            $"the scan waited out a sidecar nothing was writing ({since.ElapsedMilliseconds} ms)");
    }

    [Fact]
    public void ScanExisting_AGlbHeldOpenErrorsAtOnceAndTheSendSurvives()
    {
        // A sidecar over a glb something else has open: the scan cannot read it and says so now. Nothing
        // about the send was proved wrong, so it stays on disk rather than being eaten by a read that a
        // virus scanner or a sync client happened to lose.
        using var t = new TempGame();
        var glb = t.At("body1_lod0.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        var sidecar = BlenderBridge.SidecarPath(glb);
        File.WriteAllText(sidecar, "{\"source\":\"blender-send\"}");

        using var hold = File.Open(glb, FileMode.Open, FileAccess.Read, FileShare.None);
        using var w = new BlenderSendWatcher(t.Root);
        var errors = new List<string>();
        w.Error += (path, _) => errors.Add(path);

        var since = System.Diagnostics.Stopwatch.StartNew();
        w.ScanExisting();
        since.Stop();

        Assert.Equal(new[] { glb }, errors);
        Assert.True(File.Exists(sidecar));
        Assert.True(since.Elapsed < TimeSpan.FromMilliseconds(2000),
            $"the scan waited out a locked glb ({since.ElapsedMilliseconds} ms)");
    }

    [Fact]
    public void ScanExisting_ASendThatSurvivedALockedGlbIsTakenOnceTheHolderLetsGo()
    {
        // The whole point of leaving it: the next open reads the send the locked one could not.
        using var t = new TempGame();
        var glb = t.At("body1_lod0.glb");
        MeshGltf.ExportGlb(Triangle("body1_lod0"), glb);
        var sidecar = BlenderBridge.SidecarPath(glb);
        File.WriteAllText(sidecar, "{\"source\":\"blender-send\"}");

        using var w = new BlenderSendWatcher(t.Root);
        var received = new List<string>();
        w.EditReceived += e => received.Add(e.GlbPath);

        using (File.Open(glb, FileMode.Open, FileAccess.Read, FileShare.None)) w.ScanExisting();
        Assert.Empty(received);

        w.ScanExisting();

        Assert.Equal(new[] { glb }, received);
        Assert.False(File.Exists(sidecar));      // taken this time, so it is consumed
    }

    private static UnityMesh Triangle(string name) => new()
    {
        Name = name,
        VertexCount = 3,
        Channels = new()
        {
            ["Vertex"] = new[] { 0f, 0, 0, 1, 0, 0, 0, 1, 0 },
            ["TexCoord0"] = new[] { 0f, 0, 1, 0, 0, 1 },
        },
        Dims = new() { ["Vertex"] = 3, ["TexCoord0"] = 2 },
        Submeshes = new() { new[] { 0, 1, 2 } },
    };
}
