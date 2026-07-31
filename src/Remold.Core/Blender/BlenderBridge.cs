using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Remold.Core.Mesh;

namespace Remold.Core.Blender;

/// <summary>An edit handed back from Blender: the mesh in canonical Unity space, or null when the send
/// carried no mesh (a session whose only part was emptied). <see cref="NodeTransformIgnored"/>: the node
/// had a non-identity Object-mode transform the geometry-only import does NOT apply.
/// <see cref="HiddenParts"/> is the ONLY signal that hides a part — a glb carries one session's parts,
/// so a part's absence can never mean intent.</summary>
public readonly record struct IncomingEdit(UnityMesh? Mesh, string GlbPath, bool NodeTransformIgnored = false,
    IReadOnlyList<string>? HiddenParts = null)
{
    public string Name => Mesh?.Name ?? "";
}

/// <summary>One part of a Blender session: the mesh name the glb carries it under, whether the app already
/// holds an edit for it (so Send can confirm what it would replace), and whether this session may write it
/// back at all. <see cref="Writable"/> false is how the app declares a part the session carries for CONTEXT
/// only — the bridge gives it the Reference collection, so it can be seen and never sent.
/// <see cref="Unskinned"/> declares a part whose mesh carries no skin (a static-renderer prop): the bridge
/// exempts it from the weight gate, which would otherwise count every one of its vertices as unweighted and
/// block the send. The app is the authority — "no armature in the scene" cannot tell an unskinned part from
/// a skinned one whose armature was deleted, and the second must still block.</summary>
public readonly record struct SessionPart(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("edited")] bool Edited,
    [property: JsonPropertyName("writable")] bool? Writable = true,
    [property: JsonPropertyName("unskinned")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool Unskinned = false)
{
    /// <summary>Whether the session may write this part back. Only an EXPLICIT false declares a part context;
    /// an absent flag is a session that said nothing about it, which reads the same as writable — matching the
    /// bridge, and keeping a sidecar the app didn't stamp from silently turning the whole outfit into
    /// scenery.</summary>
    [JsonIgnore] public bool IsWritable => Writable != false;
}

/// <summary>
/// The app side of the Blender bridge: launch Blender on a mesh, pick up the "Send to Lab" export. The
/// bridge writes the <c>.glb</c> then a <c>&lt;name&gt;.gf2send.json</c> sidecar, so watching the
/// sidecar guarantees the glb is complete.
/// </summary>
public static class BlenderBridge
{
    /// <summary>The send sidecar's name suffix. Also the watcher's filter and its scan pattern, so the one
    /// naming rule the bridge script writes to has one home here.</summary>
    internal const string SidecarSuffix = ".gf2send.json";
    private const string SessionSuffix = ".gf2session.json";

    private sealed class SessionDoc
    {
        [JsonPropertyName("part")] public string? Part { get; set; }
        [JsonPropertyName("parts")] public List<SessionPart> Parts { get; set; } = new();
        [JsonPropertyName("sendAs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SendAs { get; set; }
    }

    private sealed class SendDoc
    {
        [JsonPropertyName("hiddenParts")] public List<string> HiddenParts { get; set; } = new();
    }

    private static readonly JsonSerializerOptions SessionJson = new() { WriteIndented = true };

    /// <summary>Launch Blender on <paramref name="glbPath"/> with the bridge script; Send exports into
    /// <paramref name="sendDir"/>.</summary>
    public static Process Launch(string blenderExe, string scriptPath, string glbPath, string sendDir)
    {
        Directory.CreateDirectory(sendDir);
        var psi = new ProcessStartInfo(blenderExe) { UseShellExecute = false };
        psi.ArgumentList.Add("--python");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(glbPath);
        psi.ArgumentList.Add(sendDir);
        return Process.Start(psi) ?? throw new InvalidOperationException("failed to launch Blender");
    }

    /// <summary>Where the session description lives: beside the glb, same stem-plus-suffix rule as the
    /// send sidecar, so the bridge derives it from the glb rather than the command line.</summary>
    public static string SessionPath(string glbPath) =>
        Path.Combine(Path.GetDirectoryName(glbPath) ?? "",
                     Path.GetFileNameWithoutExtension(glbPath) + SessionSuffix);

    /// <summary>Describe the session: which mesh of the multi-part glb it may write back
    /// (<paramref name="sessionPart"/>; null = all), and every part in the glb with its edited flag.
    /// Must be written before the launch so the bridge's deferred import finds it.
    /// <paramref name="sendAs"/> names the file the bridge's Send exports as, in place of the opened glb's
    /// own name — how a combined session's send is kept off the app-published combined glb. Null keeps the
    /// opened name, which for a part's own workspace glb IS the overwrite-in-place contract.</summary>
    public static void WriteSession(string glbPath, string? sessionPart, IReadOnlyList<SessionPart> parts,
        string? sendAs = null)
    {
        var doc = new SessionDoc { Part = sessionPart, Parts = new List<SessionPart>(parts), SendAs = sendAs };
        File.WriteAllText(SessionPath(glbPath), JsonSerializer.Serialize(doc, SessionJson));
    }

    /// <summary>Read back what <see cref="WriteSession"/> wrote, or (null, empty) when there is no session
    /// file — a hand-opened glb, where every mesh is a part.</summary>
    public static (string? Part, IReadOnlyList<SessionPart> Parts) ReadSession(string glbPath)
    {
        var path = SessionPath(glbPath);
        if (!File.Exists(path)) return (null, Array.Empty<SessionPart>());
        try
        {
            var doc = JsonSerializer.Deserialize<SessionDoc>(File.ReadAllText(path), SessionJson);
            return (doc?.Part, (IReadOnlyList<SessionPart>?)doc?.Parts ?? Array.Empty<SessionPart>());
        }
        catch (JsonException) { return (null, Array.Empty<SessionPart>()); }
    }

    /// <summary>The part collections the modder emptied, per the send sidecar. Empty when it carries none
    /// or can't be read: fail toward "hide nothing", losing an intent rather than blanking a wanted part.</summary>
    public static IReadOnlyList<string> ReadHiddenParts(string sidecarPath)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<SendDoc>(File.ReadAllText(sidecarPath), SessionJson);
            return (IReadOnlyList<string>?)doc?.HiddenParts ?? Array.Empty<string>();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    public static string SidecarPath(string glbPath) =>
        Path.Combine(Path.GetDirectoryName(glbPath) ?? "",
                     Path.GetFileNameWithoutExtension(glbPath) + SidecarSuffix);

    /// <summary>Inverse of <see cref="SidecarPath"/>; null if the name isn't a sidecar.</summary>
    public static string? GlbForSidecar(string sidecarPath)
    {
        var name = Path.GetFileName(sidecarPath);
        if (!name.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase)) return null;
        var stem = name[..^SidecarSuffix.Length];
        return Path.Combine(Path.GetDirectoryName(sidecarPath) ?? "", stem + ".glb");
    }

    /// <summary>True only when <paramref name="text"/> parses as a COMPLETE JSON object — the write-complete
    /// sentinel. A truncated mid-<c>json.dump</c> write reads false so the watcher keeps waiting; a malformed
    /// sidecar is never treated as done.</summary>
    public static bool IsCompleteSidecar(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    /// <summary>Read a completed Send: the glb back in Unity space, the sidecar's emptied-part list, and
    /// whether the node carried an ignored Object-mode transform. A send that hides the session's only
    /// part carries no mesh and must read back as a mesh-less edit, not a failure, or a deliberate Hide
    /// is discarded. A glb that won't parse still throws.
    ///
    /// <para>The read is LENIENT, as every other read of a Blender-written glb is: the file on disk is the
    /// modder's edit (for a part session it has already overwritten the workspace glb), so a schema
    /// complaint about accessors this app never reads would cost them that edit.</para></summary>
    public static IncomingEdit ReadSend(string glbPath, string? meshName = null)
    {
        if (meshName is null && MeshGltf.MeshNames(glbPath).Count == 0)
            return new IncomingEdit(null, glbPath, false, ReadHiddenParts(SidecarPath(glbPath)));
        var mesh = MeshGltf.ImportGlb(glbPath, meshName, lenient: true);
        bool nodeMoved = false;
        try { nodeMoved = MeshGltf.HasNonIdentityNodeTransform(glbPath, meshName); }
        catch { /* detection is advisory — never fail a real edit over it */ }
        return new IncomingEdit(mesh, glbPath, nodeMoved, ReadHiddenParts(SidecarPath(glbPath)));
    }
}

/// <summary>Watches a mod's send folder and raises <see cref="EditReceived"/> with the imported mesh.
/// Fires on the sidecar (written last) so the glb is complete; debounces the duplicate Created/Changed
/// events Windows emits per write.
///
/// <para>The sidecar is also the UNHANDLED-send marker: it is deleted once the send has been read back, so
/// <see cref="ScanExisting"/> can pick up a send that landed with no watcher listening and never pick the
/// same one up twice.</para></summary>
public sealed class BlenderSendWatcher : IDisposable
{
    private readonly FileSystemWatcher _fsw;
    private readonly string _sendDir;
    private readonly SearchOption _scanDepth;
    private readonly ConcurrentDictionary<string, DateTime> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _disposed;

    public event Action<IncomingEdit>? EditReceived;
    /// <summary>A Send landed but could not be read back: (glb path, reason). For a single-part send
    /// Blender has ALREADY overwritten that workspace glb, so the subscriber must treat the file as
    /// changed (edited / revertable), not as "nothing happened".</summary>
    public event Action<string, string>? Error;

    public BlenderSendWatcher(string sendDir, bool includeSubdirectories = false)
    {
        Directory.CreateDirectory(sendDir);
        _sendDir = sendDir;
        _scanDepth = includeSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        _fsw = new FileSystemWatcher(sendDir, "*" + BlenderBridge.SidecarSuffix)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = includeSubdirectories,
            EnableRaisingEvents = true,
        };
        _fsw.Created += OnSidecar;
        _fsw.Changed += OnSidecar;
    }

    /// <summary>Ingest every send sidecar already on disk, oldest write first, through the same path a live
    /// send takes. A send that landed while the app was closed or on another mod has no event to arrive on,
    /// and the next workspace rebuild would write over the file Blender left. Runs on the CALLER's thread,
    /// so those edits land before the caller's next line can rebuild anything — which is also why it takes
    /// the one-attempt route: a scan's sends were written before this scan existed, so a wait for a writer
    /// that isn't there would be the calling thread standing still, once per bad send.</summary>
    public void ScanExisting()
    {
        string[] pending;
        try { pending = Directory.GetFiles(_sendDir, "*" + BlenderBridge.SidecarSuffix, _scanDepth); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Array.Sort(pending, (a, b) => LastWrite(a).CompareTo(LastWrite(b)));
        foreach (var sidecar in pending)
        {
            // A subscriber can dispose this watcher from inside the ingest (the folder moved out from under
            // it, and a replacement watcher now owns the new one). The rest of THIS list no longer exists.
            if (_disposed) return;
            Ingest(sidecar, attempts: ScanAttempts, keepOnLockedGlb: true);
        }

        static DateTime LastWrite(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
        }
    }

    private void OnSidecar(object sender, FileSystemEventArgs e)
    {
        // collapse the burst of Created+Changed events a single write produces
        var now = DateTime.UtcNow;
        if (_lastSeen.TryGetValue(e.FullPath, out var prev) && (now - prev).TotalMilliseconds < 500)
            return;
        _lastSeen[e.FullPath] = now;
        // prune stale entries so a long session over many files doesn't grow the map without bound
        if (_lastSeen.Count > 64)
            foreach (var kv in _lastSeen)
                if ((now - kv.Value).TotalSeconds > 5)
                    _lastSeen.TryRemove(kv.Key, out _);

        Ingest(e.FullPath, attempts: LiveAttempts);
    }

    /// <summary>How many times a LIVE send's file is re-read while it settles: the event fires mid-write, so
    /// the budget covers Blender finishing its json.dump and the OS releasing the glb handle.</summary>
    private const int LiveAttempts = 20;

    /// <summary>The same read for a send the scan found already on disk. Nothing is writing it, so a retry
    /// budget would only be a wait — and the scan runs on the caller's thread.</summary>
    private const int ScanAttempts = 1;

    /// <summary>Read one send back and hand it on, then consume the sidecar. The live watcher and the
    /// startup scan share this so an offline send lands exactly as a live one does; they differ in
    /// <paramref name="attempts"/>, how long a file that isn't settled yet is given, and in
    /// <paramref name="keepOnLockedGlb"/>: a one-attempt read of a glb that is present but held open has
    /// proved nothing about the send, so the scan reports it and leaves it standing. Every other failure
    /// consumes — the glb is gone, the sidecar is garbage, or Blender really wrote bytes that won't
    /// import, and none of those read differently next time.</summary>
    private void Ingest(string sidecarPath, int attempts, bool keepOnLockedGlb = false)
    {
        var glb = BlenderBridge.GlbForSidecar(sidecarPath);
        if (glb is null) return;
        try
        {
            // Created fires while Blender's json.dump may still be mid-write, so wait until the sidecar
            // parses as complete JSON — not merely until it opens — then wait for the glb handle to
            // release. Neither completing throws, surfacing as an Error rather than a half-written read.
            WaitUntilSidecarComplete(sidecarPath, attempts);
            WaitUntilReadable(glb, attempts);
            var edit = BlenderBridge.ReadSend(glb);   // reads the sidecar, so it is consumed after
            ConsumeSidecar(sidecarPath);
            EditReceived?.Invoke(edit);
        }
        // the send survives whoever holds the glb: the live watcher is armed and the next open scans again
        catch (GlbHeldOpenException ex) when (keepOnLockedGlb) { Error?.Invoke(glb, ex.Message); }
        catch (Exception ex) { ConsumeSidecar(sidecarPath); Error?.Invoke(glb, ex.Message); }
    }

    /// <summary>Drop the handled send's sidecar. Consumed on a reported failure too, except where
    /// <see cref="Ingest"/> keeps it: Blender has already overwritten the workspace glb, the failure has
    /// been reported, and a re-read at the next open would fail identically. Best-effort — a locked sidecar
    /// costs a duplicate ingest, never the edit.</summary>
    private static void ConsumeSidecar(string sidecarPath)
    {
        try { File.Delete(sidecarPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    /// <summary>Retry until the sidecar is a COMPLETE JSON object
    /// (<see cref="BlenderBridge.IsCompleteSidecar"/>). Throws if it never completes, so the send surfaces
    /// as a loud Error rather than a silent read of a partial sentinel.</summary>
    private static void WaitUntilSidecarComplete(string path, int attempts)
    {
        Exception? last = null;
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var r = new StreamReader(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                if (BlenderBridge.IsCompleteSidecar(r.ReadToEnd())) return;
            }
            catch (IOException io) { last = io; }   // still locked by the writer — retry
            System.Threading.Thread.Sleep(50);
        }
        throw new IOException($"the Blender send sidecar never completed: {Path.GetFileName(path)}", last);
    }

    /// <summary>Retry until the glb is openable — the sidecar can land a hair before the OS releases the
    /// glb handle. Throws if it never becomes readable, surfacing as an Error;
    /// <see cref="GlbHeldOpenException"/> when the file is THERE and someone else has it, which is the one
    /// failure a later read can still turn into a send.</summary>
    private static void WaitUntilReadable(string path, int attempts)
    {
        IOException? last = null;
        bool present = true;
        for (int i = 0; i < attempts; i++)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) return; }
            catch (IOException io)
            {
                last = io;
                // A glb that isn't there at all is not a handle waiting to be released. The retry budget
                // exists for a file being written this instant; waiting it out on a sidecar whose glb was
                // deleted or renamed only delays the report.
                if (!File.Exists(path)) { present = false; break; }
                System.Threading.Thread.Sleep(50);
            }
        }
        string why = $"the Blender send glb never became readable: {Path.GetFileName(path)}";
        throw present ? new GlbHeldOpenException(why, last) : new IOException(why, last);
    }

    /// <summary>A send whose glb is on disk but held open by another process. Distinct from every other
    /// read failure because nothing about the send has been proved wrong: the holder lets go and the same
    /// bytes read as a send.</summary>
    private sealed class GlbHeldOpenException(string message, Exception? inner) : IOException(message, inner);

    public void Dispose()
    {
        _disposed = true;
        _fsw.Dispose();
    }
}
